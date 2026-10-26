using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Core;

/// <summary>
/// Serializes local transactions. No network call runs under this lock: begin-dispatch/send persists
/// before returning; the caller invokes the external system afterwards and records its observation.
/// </summary>
public sealed partial class DurableConversationLedger : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32, AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly IJournalStorage _storage;
    private readonly IStateProtector _protector;
    private readonly HistoryArchiveSet? _historyArchive;
    private int? _storedSnapshotBytes;
    private int LogicalCapacity => _historyArchive == null ? _options.MaximumSnapshotBytes : _options.MaximumArchivedSnapshotBytes;
    private readonly IClock _clock;
    private readonly LedgerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LedgerState _state;
    private long _sequence;
    private string? _fault;
    private bool _disposed;
    internal event Action<ConversationIdentity, long>? RevisionChanged;
    internal event Action<ConversationIdentity, long>? DialogueChanged;

    private DurableConversationLedger(IJournalStorage storage, IStateProtector protector,
        IClock clock, LedgerOptions options, LedgerState state, long sequence, IOutputDependencyValidator? dependencyValidator,
        HistoryArchiveSet? historyArchive)
    {
        _storage = storage;
        _protector = protector;
        _historyArchive = historyArchive;
        _clock = clock;
        _options = options;
        _state = state;
        _sequence = sequence;
        _dependencyValidator = dependencyValidator;
    }

    /// <summary>Takes ownership of storage, including on failed open. Corruption never clears the file.</summary>
    public static Task<DurableConversationLedger> OpenAsync(IJournalStorage storage, IStateProtector protector,
        IClock? clock = null, LedgerOptions? options = null, CancellationToken cancellationToken = default,
        IOutputDependencyValidator? dependencyValidator = null, HistoryArchiveSet? historyArchive = null)
        => OpenCoreAsync(storage, protector, clock, options, cancellationToken, dependencyValidator, historyArchive, false);

    /// <summary>Reads persisted states without interrupted-work recovery. All ledger mutations are rejected.
    /// The caller must supply storage/archive handles opened on an offline copy; opening those handles
    /// is outside this API's no-write guarantee. Takes ownership as OpenAsync does.</summary>
    public static Task<DurableConversationLedger> OpenForInspectionAsync(IJournalStorage storage, IStateProtector protector,
        LedgerOptions? options = null, CancellationToken cancellationToken = default, HistoryArchiveSet? historyArchive = null)
        => OpenCoreAsync(storage, protector, null, options, cancellationToken, null, historyArchive, true);

    private static async Task<DurableConversationLedger> OpenCoreAsync(IJournalStorage storage, IStateProtector protector,
        IClock? clock, LedgerOptions? options, CancellationToken cancellationToken,
        IOutputDependencyValidator? dependencyValidator, HistoryArchiveSet? historyArchive, bool inspection)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(protector);
        options ??= new();
        ValidateCatchUpOptions(options.CatchUp);
        ValidateBudgetOptions(options.CommitmentBudgets);
        if (string.IsNullOrWhiteSpace(protector.Scheme) || protector.Scheme.Length > 128)
            throw new ArgumentException("An authenticated encryption provider is required.", nameof(protector));
        if (options.MaximumBodyCharacters is < 1 or > 1024 * 1024
            || options.MaximumSnapshotBytes is < 1024 or > 64 * 1024 * 1024
            || options.MaximumArchivedSnapshotBytes < options.MaximumSnapshotBytes || options.MaximumArchivedSnapshotBytes > 64 * 1024 * 1024
            || options.MaximumSegments is < 1 or > 256 || options.MaximumPendingNotificationDrafts is < 1 or > 1024
            || options.MaximumPendingInputWork is < 1 or > 1024
            || options.MaximumActiveCommitments is < 1 or > 1024 || options.MaximumCommitmentRuns is < 1 or > 16384
            || options.MaximumChannelWakes is < 1 or > 16384 || options.WakeAggregationSeconds is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(options));
        try
        {
            var last = await storage.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
            var state = LedgerState.Empty();
            int? storedBytes = null;
            if (last is not null)
            {
                byte[] plaintext;
                try { plaintext = protector.Unprotect(last.Payload, Purpose(last.Sequence, protector.Scheme)); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new LedgerException(LedgerErrors.ProtectionFailed, "The journal cannot be decrypted; its contents were preserved.");
                }
                try
                {
                    if (plaintext.Length > options.MaximumSnapshotBytes)
                        throw new LedgerException(LedgerErrors.CapacityExceeded, "The decrypted snapshot exceeds the configured bound.");
                    storedBytes = plaintext.Length;
                    if (LedgerArchiveCodec.IsArchived(plaintext))
                    {
                        if (historyArchive == null) throw new LedgerException(LedgerErrors.RecoveryRequired, "This ledger requires its original history archive; no fallback was loaded.");
                        byte[] expanded = await LedgerArchiveCodec.DecodeAsync(plaintext, historyArchive, options.MaximumArchivedSnapshotBytes, cancellationToken).ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(plaintext); plaintext = expanded;
                    }
                    state = JsonSerializer.Deserialize<LedgerState>(plaintext, Json)
                        ?? throw new LedgerException(LedgerErrors.CorruptStorage, "The journal snapshot is missing.");
                    ValidateState(state);
                }
                catch (JsonException)
                {
                    throw new LedgerException(LedgerErrors.CorruptStorage, "The journal snapshot is invalid; its contents were preserved.");
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            var ledger = new DurableConversationLedger(storage, protector, clock ?? new SystemClock(), options, state, last?.Sequence ?? 0, dependencyValidator, historyArchive);
            ledger._storedSnapshotBytes = storedBytes;
            if (inspection) ledger._fault = "Offline inspection: persisted states have not been recovered; writes are disabled.";
            if (!ledger.IsReadOnly)
                await ledger.RecoverInterruptedWorkAsync(cancellationToken).ConfigureAwait(false);
            return ledger;
        }
        catch
        {
            try { await storage.DisposeAsync().ConfigureAwait(false); }
            finally { if (historyArchive != null) await historyArchive.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public Task<ConversationEvent> ResolveSentReferenceAsync(ConversationIdentity identity, Guid outboxId,
        CancellationToken cancellationToken = default) => DependencyChangeAsync(identity,
        state => ResolveOutputDependencies(SnapshotOf(state, identity), RequireOutbox(state, identity, outboxId)), state =>
    {
        var output = state.Outbox.Single(x => x.Identity == identity && x.Id == outboxId);
        if (output.Stage is not (OutboxStage.Accepted or OutboxStage.Unknown))
            throw Error(LedgerErrors.InvalidTransition, "Only observed sends can be referenced.");
        string key = "channel-reference/" + output.Id.ToString("N");
        var task = output.NotificationEventId is { } noticeId
            ? RequireEvent(state, identity, noticeId).Task
            : state.Generations.SingleOrDefault(g => g.Identity == identity && g.Id == output.GenerationId)?.Task;
        var existing = state.Events.SingleOrDefault(e => e.Identity == identity && e.SourceMessageId == key);
        if (existing != null)
        {
            if (existing.Task == task) return Unchanged(existing);
            var enriched = existing with { Task = task };
            state.Events[state.Events.IndexOf(existing)] = enriched;
            return Changed(enriched);
        }
        // Quoted output is context, never a new user instruction or a delivery acknowledgement.
        var entry = new ConversationEvent(Guid.NewGuid(), identity, ConversationSource.UntrustedContent,
            output.Body, output.CreatedAt, _clock.UtcNow, Revision(state, identity), SourceMessageId: key, Task: task,
            RelatedOutboxId: output.Id, OutputDependencies: ResolveOutputDependencies(SnapshotOf(state, identity), output));
        state.Events.Add(entry);
        return Changed(entry);
    }, cancellationToken);

    public Task<ReceiveResult> ReceiveAsync(ConversationIdentity identity, string sourceMessageId,
        string body, MessageTrust trust, DateTimeOffset occurredAt, TaskReference? task = null,
        Guid? referencedEventId = null, CancellationToken cancellationToken = default, CatchUpInputOptions? catchUp = null,
        bool hasUnresolvedReference = false) =>
        ChangeAsync(state =>
        {
            ValidateIdentity(identity); ValidateToken(sourceMessageId); ValidateBody(body);
            if (!Enum.IsDefined(trust) || occurredAt == default) throw new ArgumentException("A valid source and occurrence time are required.");
            if (catchUp != null && trust != MessageTrust.AuthenticatedUser)
                throw Error(LedgerErrors.AuthorizationRequired, "Only authenticated channel input can create a catch-up window.");
            if (trust == MessageTrust.AuthenticatedUser && !hasUnresolvedReference && task == null
                && referencedEventId is { } quoteId && QuotedTaskContinuation.IsRequest(body))
            {
                var quote = RequireEvent(state, identity, quoteId);
                if (quote.SourceMessageId?.StartsWith("channel-reference/", StringComparison.Ordinal) == true)
                    task = quote.Task;
            }
            ValidateTask(task);
            var existing = state.Inbox.SingleOrDefault(x => x.Identity == identity && x.SourceMessageId == sourceMessageId);
            if (existing is not null)
            {
                if (existing.Body != body || existing.Trust != trust || existing.Task != task || existing.ReferencedEventId != referencedEventId
                    || existing.HasUnresolvedReference != hasUnresolvedReference)
                    throw Error(LedgerErrors.Conflict, "The same source message ID was received with different content.");
                return Unchanged(new ReceiveResult(existing, true, Revision(state, identity)));
            }
            if (referencedEventId is { } reference)
                RequireEvent(state, identity, reference);
            var revision = checked(Revision(state, identity) + 1);
            ReplaceConversation(state, identity, revision);
            InvalidateOldGenerations(state, identity, revision);
            var now = _clock.UtcNow;
            var message = new InboxMessage(Guid.NewGuid(), identity, sourceMessageId, body, trust,
                occurredAt, now, revision, InboxStage.Received, Task: task, ReferencedEventId: referencedEventId,
                HasUnresolvedReference: hasUnresolvedReference);
            if (hasUnresolvedReference) state.SchemaVersion = Math.Max(state.SchemaVersion, 6);
            state.Inbox.Add(message);
            if (trust == MessageTrust.AuthenticatedUser) CancelPendingWakes(state, identity, "A new authenticated input ended this wake episode.");
            state.Events.Add(new(Guid.NewGuid(), identity,
                trust == MessageTrust.AuthenticatedUser ? ConversationSource.TrustedUser : ConversationSource.UntrustedContent,
                body, occurredAt, now, revision, sourceMessageId, task, ReferencedEventId: referencedEventId));
            if (trust == MessageTrust.AuthenticatedUser) CaptureCatchUpWindow(state, message, catchUp);
            return Changed(new ReceiveResult(message, false, revision));
        }, cancellationToken);

    /// <summary>Context events can never create a TrustedUser event or a user authorization.</summary>
    public Task<ConversationEvent> AppendContextAsync(ConversationIdentity identity, ConversationSource source,
        string body, DateTimeOffset occurredAt, TaskReference? task = null, Guid? referencedEventId = null,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            ValidateIdentity(identity); ValidateBody(body); ValidateTask(task);
            if (!Enum.IsDefined(source) || source is ConversationSource.TrustedUser || occurredAt == default)
                throw Error(LedgerErrors.AuthorizationRequired, "Trusted user input must enter through the authenticated inbox.");
            if (referencedEventId is { } reference) RequireEvent(state, identity, reference);
            EnsureConversation(state, identity);
            var entry = new ConversationEvent(Guid.NewGuid(), identity, source, body, occurredAt,
                _clock.UtcNow, Revision(state, identity), Task: task, ReferencedEventId: referencedEventId);
            state.Events.Add(entry);
            return Changed(entry);
        }, cancellationToken);

    public Task GroupMessagesAsync(ConversationIdentity identity, IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            foreach (var message in SelectInbox(state, identity, sourceMessageIds))
            {
                if (state.InputWork.Any(x => x.Identity == identity && x.SourceMessageId == message.SourceMessageId))
                    throw Error(LedgerErrors.InvalidTransition, "Deferred work cannot be regrouped as a current request.");
                if (message.Stage is not (InboxStage.Received or InboxStage.Grouped or InboxStage.Superseded))
                    throw Error(LedgerErrors.InvalidTransition, "This message cannot be grouped.");
                Replace(state.Inbox, message, message with { Stage = InboxStage.Grouped, GenerationId = null });
            }
            return Changed(true);
        }, cancellationToken);

    public Task<GenerationLease> BeginGenerationAsync(ConversationIdentity identity,
        IReadOnlyList<string> sourceMessageIds, TaskReference? task = null, CancellationToken cancellationToken = default,
        OutputDependencies? outputDependencies = null) =>
        ChangeAsync(state =>
        {
            ValidateTask(task);
            var messages = SelectInbox(state, identity, sourceMessageIds);
            var revision = Revision(state, identity);
            if (messages.All(x => x.Revision != revision))
                throw Error(LedgerErrors.StaleRevision, "The current message must be included in this generation.");
            if (state.Generations.Any(x => x.Identity == identity && x.Revision == revision
                && x.Stage is GenerationStage.Processing or GenerationStage.Prepared))
                throw Error(LedgerErrors.Conflict, "A current generation is already active.");
            foreach (var message in messages)
                if (message.Stage is not (InboxStage.Received or InboxStage.Grouped or InboxStage.Superseded)
                    || state.InputWork.Any(x => x.Identity == identity && x.SourceMessageId == message.SourceMessageId))
                    throw Error(LedgerErrors.InvalidTransition, "This message is already processed or active.");
            var id = Guid.NewGuid();
            if (outputDependencies != null)
            {
                ValidateDependencyDefinition(outputDependencies);
                RequireValidDependency(DependencyValidation(identity, outputDependencies, state.OutputDependenciesRequired));
                PromoteDependencySchema(state, outputDependencies);
            }
            var generation = new GenerationRecord(id, identity, revision,
                messages.Select(x => x.Id).ToImmutableArray(), GenerationStage.Processing,
                _clock.UtcNow, [], task, GenerationContext: outputDependencies == null ? null : new(id, 0, outputDependencies),
                ContextTransitions: outputDependencies == null ? null : []);
            state.Generations.Add(generation);
            foreach (var message in messages)
                Replace(state.Inbox, message, message with { Stage = InboxStage.Processing, GenerationId = id });
            return Changed(new GenerationLease(id, identity, revision));
        }, cancellationToken);

    public Task PrepareReplyAsync(GenerationLease lease, IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default) => DependencyChangeAsync(lease.Identity,
        state => RequireGeneration(state, lease).GenerationContext?.Dependencies, state =>
        {
            var generation = RequireGeneration(state, lease);
            if (generation.Stage != GenerationStage.Processing)
                throw Error(LedgerErrors.InvalidTransition, "Only an active generation can prepare a reply.");
            if (segments.Count is < 1 || segments.Count > _options.MaximumSegments) throw new ArgumentOutOfRangeException(nameof(segments));
            foreach (var segment in segments) ValidateBody(segment);
            Replace(state.Generations, generation, generation with
            {
                Stage = GenerationStage.Prepared, PreparedSegments = segments.ToImmutableArray()
            });
            SetInboxStage(state, generation, InboxStage.Prepared);
            if (generation.InputWorkId is { } workId)
            {
                var work = RequireInputWork(state, lease.Identity, workId);
                Replace(state.InputWork, work, work with { Stage = InputWorkStage.Prepared, UpdatedAt = _clock.UtcNow });
            }
            return Changed(true);
        }, cancellationToken);

    public Task<ImmutableArray<OutboxMessage>> EnqueuePreparedReplyAsync(GenerationLease lease,
        CancellationToken cancellationToken = default) => DependencyChangeAsync(lease.Identity,
        state => RequireGeneration(state, lease).GenerationContext?.Dependencies, state =>
        {
            var generation = RequireGeneration(state, lease);
            if (generation.Stage == GenerationStage.HandedToOutbox)
                return Unchanged(state.Outbox.Where(x => x.GenerationId == lease.Id).OrderBy(x => x.SegmentIndex).ToImmutableArray());
            if (generation.Stage != GenerationStage.Prepared)
                throw Error(LedgerErrors.InvalidTransition, "The reply has not been prepared.");
            var now = _clock.UtcNow;
            var work = generation.InputWorkId is { } id ? RequireInputWork(state, lease.Identity, id) : null;
            var messages = generation.PreparedSegments.Select((body, index) => new OutboxMessage(
                Guid.NewGuid(), lease.Identity, lease.Id, lease.Revision, index, body, OutboxStage.Pending, now, now,
                Kind: work is null ? OutboxKind.ConversationReply : OutboxKind.DeferredReply,
                InputWorkId: work?.Id, ReadAtRevision: work?.ReadAtRevision)).ToImmutableArray();
            state.Outbox.AddRange(messages);
            Replace(state.Generations, generation, generation with { Stage = GenerationStage.HandedToOutbox });
            SetInboxStage(state, generation, InboxStage.HandedToOutbox);
            if (work is not null) Replace(state.InputWork, work, work with
            {
                Stage = work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : InputWorkStage.ReplyPending,
                UpdatedAt = now
            });
            return Changed(messages);
        }, cancellationToken);

    /// <summary>The host, not the model, calls this after verifying explicit intent in the current message.</summary>
    public Task<UserAuthorization> AuthorizeCurrentUserRequestAsync(ConversationIdentity identity,
        string sourceMessageId, string action, string targetKey, string argumentsJson, string sourceExcerpt,
        TaskReference? task = null, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            ValidateToken(action); ValidateToken(targetKey); ValidateTask(task); ValidateBody(argumentsJson);
            using (JsonDocument.Parse(argumentsJson)) { }
            var message = RequireInbox(state, identity, sourceMessageId);
            if (message.Trust != MessageTrust.AuthenticatedUser || message.HasUnresolvedReference)
                throw Error(LedgerErrors.AuthorizationRequired, "Untrusted content cannot authorize an operation.");
            if (message.ReferencedEventId is { } quotedId
                && RequireEvent(state, identity, quotedId) is { } quote
                && quote.SourceMessageId?.StartsWith("channel-reference/", StringComparison.Ordinal) == true)
            {
                if (action is not ("send_to_task" or "continue_owned_task") || !QuotedTaskContinuation.IsRequest(message.Body)
                    || !QuotedTaskContinuation.SameTarget(quote.Task, message.Task)
                    || !QuotedTaskContinuation.SameTarget(quote.Task, task))
                    throw Error(LedgerErrors.AuthorizationRequired, "Only explicit continuation of the resolved quoted task is allowed.");
                RequireValidDependency(DependencyValidation(identity, quote.OutputDependencies, state.OutputDependenciesRequired));
            }
            if (message.Revision != Revision(state, identity))
                throw Error(LedgerErrors.StaleRevision, "Authorization must refer to the current user message.");
            if (string.IsNullOrWhiteSpace(sourceExcerpt) || !message.Body.Contains(sourceExcerpt, StringComparison.Ordinal))
                throw Error(LedgerErrors.AuthorizationRequired, "The authorization evidence must quote the current user message.");
            var existing = state.Authorizations.SingleOrDefault(x => x.Identity == identity
                && x.SourceMessageId == sourceMessageId && x.Action == action && x.TargetKey == targetKey
                && x.ArgumentsJson == argumentsJson && x.Task == task);
            if (existing is not null) return Unchanged(existing);
            var authorization = new UserAuthorization(Guid.NewGuid(), identity, sourceMessageId,
                message.Revision, action, targetKey, argumentsJson, sourceExcerpt, _clock.UtcNow, task);
            state.Authorizations.Add(authorization);
            return Changed(authorization);
        }, cancellationToken);

    public Task<OperationRecord> QueueOperationAsync(ConversationIdentity identity, Guid authorizationId,
        string idempotencyKey, CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            ValidateToken(idempotencyKey);
            var authorization = RequireAuthorization(state, identity, authorizationId);
            var existing = state.Operations.SingleOrDefault(x => x.Identity == identity && x.IdempotencyKey == idempotencyKey);
            if (existing is not null)
            {
                if (existing.AuthorizationId != authorizationId)
                    throw Error(LedgerErrors.Conflict, "The idempotency key belongs to a different authorized operation.");
                return Unchanged(existing);
            }
            RequireCurrent(state, identity, authorization.Revision);
            var now = _clock.UtcNow;
            var operation = new OperationRecord(Guid.NewGuid(), identity, authorizationId,
                authorization.SourceMessageId, authorization.Revision, idempotencyKey, authorization.Action,
                authorization.TargetKey, authorization.ArgumentsJson, OperationStage.Queued, now, now, authorization.Task);
            state.Operations.Add(operation);
            return Changed(operation);
        }, cancellationToken);

    public Task<OperationRecord> BeginOperationDispatchAsync(ConversationIdentity identity, Guid operationId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var operation = RequireOperation(state, identity, operationId);
            RequireCurrent(state, identity, operation.Revision);
            if (operation.Stage != OperationStage.Queued)
                throw Error(LedgerErrors.InvalidTransition, "Only a queued operation may be dispatched; unknown outcomes require reconciliation.");
            var updated = operation with { Stage = OperationStage.Dispatching, UpdatedAt = _clock.UtcNow };
            Replace(state.Operations, operation, updated);
            return Changed(updated);
        }, cancellationToken);

    public Task<OperationRecord> RecordOperationObservationAsync(ConversationIdentity identity, Guid operationId,
        OperationStage observedStage, string evidence, bool reconciliation = false,
        CancellationToken cancellationToken = default) =>
        RecordOperationObservationCoreAsync(identity, operationId, observedStage, evidence, reconciliation, null, cancellationToken);

    public Task<OperationRecord> RecordOperationObservationAsync(ConversationIdentity identity, Guid operationId,
        OperationObservation observation, CancellationToken cancellationToken = default) =>
        RecordOperationObservationCoreAsync(identity, operationId, observation.Stage, observation.Evidence,
            observation.Reconciliation, observation, cancellationToken);

    private Task<OperationRecord> RecordOperationObservationCoreAsync(ConversationIdentity identity, Guid operationId,
        OperationStage observedStage, string evidence, bool reconciliation, OperationObservation? observation,
        CancellationToken cancellationToken) => ChangeAsync(state =>
        {
            ValidateBody(evidence);
            var operation = RequireOperation(state, identity, operationId);
            if (observation is not null) ValidateObservationIdentity(operation, observation);
            var stage = MergeOperationStage(operation.Stage, observedStage, reconciliation, directReceipt: false);
            var updated = stage == operation.Stage ? operation : operation with { Stage = stage, Receipt = evidence, UpdatedAt = _clock.UtcNow };
            Replace(state.Operations, operation, updated);
            state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.ToolResult, evidence,
                _clock.UtcNow, _clock.UtcNow, Revision(state, identity), operation.SourceMessageId,
                operation.Task, operation.Id));
            return Changed(updated);
        }, cancellationToken);

    public Task<OperationRecord> CancelQueuedOperationAsync(ConversationIdentity identity, Guid operationId,
        Guid cancellationAuthorizationId, CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var operation = RequireOperation(state, identity, operationId);
            var authorization = RequireAuthorization(state, identity, cancellationAuthorizationId);
            RequireCurrent(state, identity, authorization.Revision);
            if (authorization.Action != "cancel_queued" || authorization.TargetKey != operationId.ToString("N"))
                throw Error(LedgerErrors.AuthorizationRequired, "Cancellation authorization targets a different action or operation.");
            if (operation.Stage != OperationStage.Queued)
                throw Error(LedgerErrors.InvalidTransition, "Cancelling a queue entry cannot interrupt a dispatched operation.");
            var updated = operation with { Stage = OperationStage.CancelledBeforeDispatch, UpdatedAt = _clock.UtcNow };
            Replace(state.Operations, operation, updated);
            state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.System,
                "Queue entry cancelled before dispatch.", _clock.UtcNow, _clock.UtcNow,
                authorization.Revision, authorization.SourceMessageId, operation.Task, operation.Id));
            return Changed(updated);
        }, cancellationToken);

    public Task<OutboxMessage> BeginOutboxSendAsync(ConversationIdentity identity, Guid outboxId,
        CancellationToken cancellationToken = default) => DependencyChangeAsync(identity,
        state => ResolveOutputDependencies(SnapshotOf(state, identity), RequireOutbox(state, identity, outboxId)), state =>
        {
            var message = RequireOutbox(state, identity, outboxId);
            RequireOutboxCurrent(state, message);
            if (message.Stage != OutboxStage.Pending)
                throw Error(LedgerErrors.InvalidTransition, "Only pending messages may be sent; unknown outcomes require reconciliation.");
            if (state.Outbox.Any(x => SameOutboxGroup(x, message) && x.SegmentIndex < message.SegmentIndex
                && x.Stage != OutboxStage.Accepted))
                throw Error(LedgerErrors.InvalidTransition, "Earlier segments must be accepted before this segment is sent.");
            RequireCatchUpDispatch(state, message);
            ReserveCommitmentDelivery(state, message);
            var updated = message with { Stage = OutboxStage.Sending, UpdatedAt = _clock.UtcNow,
                SendAttempts = checked(message.SendAttempts + 1), AttemptTrustedInputRevision = TrustedInputRevision(state, identity),
                RejectionReason = OutboxRejectionReason.None };
            Replace(state.Outbox, message, updated);
            if (message.InputWorkId is { } workId)
            {
                var work = RequireInputWork(state, identity, workId);
                Replace(state.InputWork, work, work with { HasSendAttempt = true, UpdatedAt = _clock.UtcNow });
            }
            return Changed(updated);
        }, cancellationToken);

    public Task<OutboxMessage> RecordOutboxOutcomeAsync(ConversationIdentity identity, Guid outboxId,
        OutboxStage outcome, string evidence, bool reconciliation = false,
        CancellationToken cancellationToken = default, OutboxRejectionReason rejectionReason = OutboxRejectionReason.None) =>
        RecordOutboxOutcomeCoreAsync(identity, outboxId, outcome, evidence, reconciliation, cancellationToken, rejectionReason, null);

    /// <summary>
    /// Saves the result of the exact original in-flight attempt, including after
    /// its caller stopped waiting. This never dispatches or grants a new attempt.
    /// </summary>
    public Task<OutboxMessage> RecordOutboxAttemptOutcomeAsync(OutboxAttemptLease attempt,
        OutboxStage outcome, string evidence, OutboxRejectionReason rejectionReason = OutboxRejectionReason.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return RecordOutboxOutcomeCoreAsync(attempt.Identity, attempt.OutboxId, outcome, evidence,
            true, cancellationToken, rejectionReason, attempt);
    }

    private Task<OutboxMessage> RecordOutboxOutcomeCoreAsync(ConversationIdentity identity, Guid outboxId,
        OutboxStage outcome, string evidence, bool reconciliation, CancellationToken cancellationToken,
        OutboxRejectionReason rejectionReason, OutboxAttemptLease? attempt) => ChangeAsync(state =>
        {
            ValidateBody(evidence);
            if (!Enum.IsDefined(rejectionReason) || outcome != OutboxStage.Rejected && rejectionReason != OutboxRejectionReason.None)
                throw new ArgumentException("A structured rejection reason requires a rejected channel outcome.");
            var message = RequireOutbox(state, identity, outboxId);
            if (outcome is not (OutboxStage.Accepted or OutboxStage.Rejected or OutboxStage.Unknown))
                throw Error(LedgerErrors.InvalidTransition, "A channel result must be accepted, rejected or unknown.");
            if (attempt is not null)
            {
                if (attempt.Number < 1 || attempt.TrustedInputRevision is null || message.SendAttempts != attempt.Number
                    || message.AttemptTrustedInputRevision != attempt.TrustedInputRevision)
                    throw Error(LedgerErrors.Conflict, "The receipt belongs to a different channel attempt.");
                if (message.Stage is OutboxStage.Accepted or OutboxStage.Rejected)
                {
                    // A timeout racing a definite receipt cannot erase it. Duplicate
                    // completion also cannot append events or settle a budget twice.
                    if (outcome == OutboxStage.Unknown
                        || outcome == message.Stage && rejectionReason == message.RejectionReason)
                        return Unchanged(message);
                    throw Error(LedgerErrors.Conflict, "The same channel attempt already has a different definite result.");
                }
                if (message.Stage == OutboxStage.Unknown && outcome == OutboxStage.Unknown)
                    return Unchanged(message);
            }
            if (message.Stage != OutboxStage.Sending && !(message.Stage == OutboxStage.Unknown && reconciliation)
                || outcome is not (OutboxStage.Accepted or OutboxStage.Rejected or OutboxStage.Unknown))
                throw Error(LedgerErrors.InvalidTransition, "This outcome cannot advance the outbox entry.");
            var updated = message with { Stage = outcome, ChannelReceipt = evidence, UpdatedAt = _clock.UtcNow,
                RejectionReason = rejectionReason };
            Replace(state.Outbox, message, updated);
            NoteCommitmentDeliveryOutcome(state, updated);
            NoteCatchUpAcceptance(state, updated);
            if (outcome == OutboxStage.Accepted)
            {
                var task = message.Kind is OutboxKind.ConversationReply or OutboxKind.DeferredReply
                    ? state.Generations.Single(x => x.Id == message.GenerationId && x.Identity == identity).Task
                    : RequireEvent(state, identity, message.NotificationEventId!.Value).Task;
                state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.Assistant, message.Body,
                    _clock.UtcNow, _clock.UtcNow, message.Revision, Task: task, RelatedOutboxId: message.Id,
                    ReferencedEventId: message.NotificationEventId));
                if (message.Kind == OutboxKind.Notification) NoteNotificationAcceptance(state, message);
            }
            if (message.InputWorkId is { } workId)
            {
                var work = RequireInputWork(state, identity, workId);
                var allAccepted = state.Outbox.Where(x => x.GenerationId == message.GenerationId).All(x => x.Stage == OutboxStage.Accepted);
                var stage = outcome == OutboxStage.Unknown ? InputWorkStage.Unknown
                    : allAccepted && WorkCurrent(state, work) ? work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : InputWorkStage.Completed
                    : work.Stage;
                Replace(state.InputWork, work, work with { Stage = stage, UpdatedAt = _clock.UtcNow });
            }
            return Changed(updated);
        }, cancellationToken);

    public Task<OutboxMessage> RetryRejectedOutboxAsync(ConversationIdentity identity, Guid outboxId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var message = RequireOutbox(state, identity, outboxId);
            RequireOutboxCurrent(state, message);
            if (message.Stage != OutboxStage.Rejected)
                throw Error(LedgerErrors.InvalidTransition, "Only a definitely rejected message may be retried.");
            RequireCatchUpRetry(state, message);
            RequireCommitmentMessagePolicy(state, message, _clock.UtcNow);
            var updated = message with { Stage = OutboxStage.Pending, UpdatedAt = _clock.UtcNow };
            Replace(state.Outbox, message, updated);
            return Changed(updated);
        }, cancellationToken);

    /// <summary>
    /// Lists identities already present in the protected local store for trusted host startup
    /// reconciliation. This returns no message text and creates no conversation or permission.
    /// Hosts must not expose other bindings through user-facing queries or model tools.
    /// </summary>
    public async Task<ImmutableArray<ConversationIdentity>> ListConversationIdentitiesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _state.Conversations.Select(item => item.Identity)
                .OrderBy(item => item.UserId, StringComparer.Ordinal)
                .ThenBy(item => item.BindingId, StringComparer.Ordinal)
                .ThenBy(item => item.ConversationId, StringComparer.Ordinal).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ConversationSnapshot> ReadConversationAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return SnapshotOf(_state, identity);
        }
        finally { _gate.Release(); }
    }

    private ConversationSnapshot SnapshotOf(LedgerState state, ConversationIdentity identity) => new(identity, Revision(state, identity),
        state.Inbox.Where(x => x.Identity == identity).ToImmutableArray(), state.Events.Where(x => x.Identity == identity).ToImmutableArray(),
        state.Generations.Where(x => x.Identity == identity).ToImmutableArray(), state.Authorizations.Where(x => x.Identity == identity).ToImmutableArray(),
        state.Operations.Where(x => x.Identity == identity).ToImmutableArray(), state.Outbox.Where(x => x.Identity == identity).ToImmutableArray(),
        IsReadOnly, _fault ?? _storage.Recovery.Reason, state.ToolInvocations.Where(x => x.Identity == identity).ToImmutableArray(),
        state.AgentBudgets.Where(x => x.Identity == identity).ToImmutableArray(), state.NotificationDrafts.Where(x => x.Identity == identity).ToImmutableArray(),
        ContextFor(state, identity), state.InputWork.Where(x => x.Identity == identity).ToImmutableArray(),
        state.WorkItems.Where(x => x.Identity == identity).ToImmutableArray(),
        state.Schedules.Where(x => state.WorkItems.Any(w => w.Id == x.WorkItemId && w.Identity == identity)).ToImmutableArray(),
        state.Subscriptions.Where(x => state.WorkItems.Any(w => w.Id == x.WorkItemId && w.Identity == identity)).ToImmutableArray(),
        state.CommitmentRuns.Where(x => x.Identity == identity).ToImmutableArray(), state.CommitmentBudgetReservations.Where(x => x.Identity == identity).ToImmutableArray(),
        state.ChannelWakes.Where(x => x.Identity == identity).ToImmutableArray(), state.WakeChannels.SingleOrDefault(x => x.Identity == identity)?.Configuration,
        state.CatchUpWindows.Where(x => x.Identity == identity).ToImmutableArray(), state.CatchUpBatches.Where(x => x.Identity == identity).ToImmutableArray(),
        state.OutputDependenciesRequired || _options.RequireOutputDependencies,
        new(state.DependentWork.Where(x => x.Identity == identity).ToImmutableArray(),
            state.DependentAttempts.Where(x => x.Identity == identity).ToImmutableArray()));

    public async Task<RecoveryPlan> GetRecoveryPlanAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return new(
            snapshot.IsReadOnly ? [] : snapshot.Inbox.Where(x => (x.Stage is InboxStage.Received or InboxStage.Grouped or InboxStage.Superseded)
                && !snapshot.InputWork.Any(w => w.SourceMessageId == x.SourceMessageId)).ToImmutableArray(),
            snapshot.IsReadOnly ? [] : snapshot.Generations.Where(x => x.Stage == GenerationStage.Prepared
                && DependencyValidation(identity, x.GenerationContext?.Dependencies, snapshot.OutputDependenciesRequired).Status == OutputDependencyStatus.Valid
                && (x.InputWorkId is null ? x.Revision == snapshot.Revision : snapshot.InputWork.Any(w => w.Id == x.InputWorkId
                    && w.GenerationId == x.Id && w.ReadAtRevision == snapshot.Revision && w.DialogueEpoch == snapshot.Context!.Epoch))).ToImmutableArray(),
            snapshot.IsReadOnly ? [] : snapshot.Operations.Where(x => x.Stage == OperationStage.Queued && x.Revision == snapshot.Revision).ToImmutableArray(),
            snapshot.Operations.Where(x => x.Stage is OperationStage.Unknown or OperationStage.Accepted or OperationStage.Running
                || snapshot.IsReadOnly && x.Stage is OperationStage.Queued or OperationStage.Dispatching).ToImmutableArray(),
            snapshot.IsReadOnly ? [] : snapshot.Outbox.Where(x => x.Stage == OutboxStage.Pending
                && OutboxCurrent(snapshot, x) && OutputDependencyReady(snapshot, x) && CommitmentDeliveryReady(snapshot, x, _clock.UtcNow)).ToImmutableArray(),
            snapshot.Outbox.Where(x => x.Stage == OutboxStage.Unknown
                || snapshot.IsReadOnly && x.Stage is OutboxStage.Pending or OutboxStage.Sending).ToImmutableArray(),
            snapshot.IsReadOnly ? [] : snapshot.NotificationDrafts.Where(x => x.Stage == NotificationDraftStage.Captured && !x.Cancelled
                && CommitmentPreparationReady(snapshot, x, _clock.UtcNow)).ToImmutableArray());
    }

    private Task RecoverInterruptedWorkAsync(CancellationToken cancellationToken) => ChangeAsync(state =>
    {
        bool changed = RecoverCommitmentObservationSources(state);
        if (_options.RequireOutputDependencies && !state.OutputDependenciesRequired)
        { state.OutputDependenciesRequired = true; state.SchemaVersion = Math.Max(state.SchemaVersion, 3); changed = true; }
        changed |= RecoverCommitments(state);
        changed |= RecoverDependentWork(state);
        changed |= RecoverChannelWakes(state);
        foreach (var draft in state.NotificationDrafts.Where(x => x.Stage == NotificationDraftStage.Preparing).ToArray())
        {
            Replace(state.NotificationDrafts, draft, draft with
            {
                Stage = NotificationDraftStage.Captured, PreparationId = null, UpdatedAt = _clock.UtcNow
            });
            changed = true;
        }
        foreach (var operation in state.Operations.Where(x => x.Stage == OperationStage.Dispatching).ToArray())
        {
            Replace(state.Operations, operation, operation with { Stage = OperationStage.Unknown, UpdatedAt = _clock.UtcNow });
            changed = true;
        }
        foreach (var message in state.Outbox.Where(x => x.Stage == OutboxStage.Sending).ToArray())
        {
            Replace(state.Outbox, message, message with { Stage = OutboxStage.Unknown, UpdatedAt = _clock.UtcNow });
            if (message.InputWorkId is { } workId)
            {
                var work = RequireInputWork(state, message.Identity, workId);
                Replace(state.InputWork, work, work with { Stage = InputWorkStage.Unknown, UpdatedAt = _clock.UtcNow });
            }
            changed = true;
        }
        foreach (var generation in state.Generations.Where(x => x.Stage == GenerationStage.Processing).ToArray())
        {
            Replace(state.Generations, generation, generation with { Stage = GenerationStage.Interrupted });
            SetInboxStage(state, generation, generation.Revision == Revision(state, generation.Identity) ? InboxStage.Received : InboxStage.Superseded);
            RecoverInputWorkAttempt(state, generation, _clock.UtcNow);
            changed = true;
        }
        foreach (var invocation in state.ToolInvocations.Where(x => x.Stage == ToolInvocationStage.Executing).ToArray())
        {
            Replace(state.ToolInvocations, invocation, invocation with
            {
                Stage = invocation.HasSideEffects ? ToolInvocationStage.Unknown : ToolInvocationStage.Interrupted,
                UpdatedAt = _clock.UtcNow
            });
            changed = true;
        }
        changed |= RefreshDependencies(state) > 0;
        return new Mutation<bool>(true, changed);
    }, cancellationToken);

    private bool IsReadOnly => _fault is not null || _storage.Recovery.Required || _historyArchive?.IsReadOnly == true;

    private async Task<T> ChangeAsync<T>(Func<LedgerState, Mutation<T>> change, CancellationToken cancellationToken)
    {
        ConversationState[] revisions = [];
        ConversationContext[] dialogues = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsReadOnly) throw Error(LedgerErrors.RecoveryRequired, "The ledger is read-only until its storage is reconciled.");
            var next = _state.Clone();
            var mutation = change(next);
            if (!mutation.Changed) return mutation.Value;
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            try
            {
                if (plaintext.Length > LogicalCapacity)
                    throw Error(LedgerErrors.CapacityExceeded, "The ledger snapshot capacity would be exceeded; nothing was removed.");
                byte[] stored = _historyArchive == null ? plaintext
                    : await LedgerArchiveCodec.EncodeAsync(plaintext, _historyArchive, cancellationToken).ConfigureAwait(false);
                int storedLength = stored.Length;
                if (storedLength > _options.MaximumSnapshotBytes)
                {
                    if (!ReferenceEquals(stored, plaintext)) CryptographicOperations.ZeroMemory(stored);
                    throw Error(LedgerErrors.CapacityExceeded, "The ledger manifest capacity would be exceeded; original history was retained.");
                }
                byte[] encrypted;
                try
                {
                    encrypted = _protector.Protect(stored, Purpose(checked(_sequence + 1), _protector.Scheme));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw Error(LedgerErrors.ProtectionFailed, "The transaction could not be protected; nothing was sent.");
                }
                finally { if (!ReferenceEquals(stored, plaintext)) CryptographicOperations.ZeroMemory(stored); }
                try
                {
                    await _storage.AppendAsync(_sequence, encrypted, cancellationToken).ConfigureAwait(false);
                }
                catch (JournalCapacityRejection) when (!_storage.Recovery.Required) { throw; }
                catch
                {
                    _fault = "The last durable write outcome is uncertain. Reopen before any external action.";
                    throw;
                }
                revisions = next.Conversations.Where(x => x.Revision != Revision(_state, x.Identity)).ToArray();
                dialogues = next.Contexts.Where(x => x.Epoch != ContextFor(_state, x.Identity).Epoch).ToArray();
                Volatile.Write(ref _state, next);
                _sequence++;
                _logicalSnapshotBytes = plaintext.Length;
                _storedSnapshotBytes = storedLength;
                return mutation.Value;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally
        {
            _gate.Release();
            foreach (var conversation in revisions)
            {
                try { RevisionChanged?.Invoke(conversation.Identity, conversation.Revision); }
                catch (AggregateException) { /* A cancellation callback must not undo a committed inbox record. */ }
            }
            foreach (var context in dialogues)
            {
                try { DialogueChanged?.Invoke(context.Identity, context.Epoch); }
                catch (AggregateException) { /* Cancellation callbacks cannot undo a committed boundary. */ }
            }
        }
    }

    private static void InvalidateOldGenerations(LedgerState state, ConversationIdentity identity, long revision)
    {
        foreach (var generation in state.Generations.Where(x => x.Identity == identity && x.Revision < revision
            && x.Stage is GenerationStage.Processing or GenerationStage.Prepared).ToArray())
        {
            Replace(state.Generations, generation, generation with { Stage = GenerationStage.Superseded });
            SetInboxStage(state, generation, InboxStage.Superseded);
        }
        foreach (var pending in state.Outbox.Where(x => x.Identity == identity && x.Kind == OutboxKind.ConversationReply && x.Revision < revision
            && x.Stage is OutboxStage.Pending or OutboxStage.Rejected).ToArray())
            Replace(state.Outbox, pending, pending with { Stage = OutboxStage.Superseded });
        InvalidateInputWork(state, identity, revision);
    }

    private static void SetInboxStage(LedgerState state, GenerationRecord generation, InboxStage stage)
    {
        foreach (var message in state.Inbox.Where(x => generation.InboxIds.Contains(x.Id) && x.GenerationId == generation.Id).ToArray())
            Replace(state.Inbox, message, message with { Stage = stage });
    }

    private static GenerationRecord RequireGeneration(LedgerState state, GenerationLease lease)
    {
        var generation = state.Generations.SingleOrDefault(x => x.Id == lease.Id && x.Identity == lease.Identity)
            ?? throw Error(LedgerErrors.NotFound, "Generation does not belong to this conversation.");
        if (generation.Revision != lease.Revision) throw Error(LedgerErrors.StaleRevision, "Generation revision does not match.");
        if (generation.InputWorkId is { } workId)
        {
            var work = RequireInputWork(state, lease.Identity, workId);
            if (!WorkCurrent(state, work) || work.GenerationId != generation.Id || work.Version != generation.WorkVersion)
                throw Error(LedgerErrors.StaleRevision, "Read-only work was superseded by newer constraints.");
        }
        else RequireCurrent(state, lease.Identity, lease.Revision);
        return generation;
    }
    private static InboxMessage[] SelectInbox(LedgerState state, ConversationIdentity identity, IReadOnlyList<string> ids)
    {
        if (ids.Count is < 1 or > 64 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("Select 1 to 64 distinct source message IDs.", nameof(ids));
        return ids.Select(id => RequireInbox(state, identity, id)).ToArray();
    }
    private static InboxMessage RequireInbox(LedgerState state, ConversationIdentity identity, string id) =>
        state.Inbox.SingleOrDefault(x => x.Identity == identity && x.SourceMessageId == id)
        ?? throw Error(LedgerErrors.NotFound, "Message does not belong to this conversation.");
    private static UserAuthorization RequireAuthorization(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.Authorizations.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.AuthorizationRequired, "Authorization does not belong to this conversation.");
    private static OperationRecord RequireOperation(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.Operations.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "Operation does not belong to this conversation.");
    private static OutboxMessage RequireOutbox(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.Outbox.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "Outbox entry does not belong to this conversation.");
    private static ConversationEvent RequireEvent(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.Events.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.IdentityMismatch, "Referenced event does not belong to this conversation.");
    private static long Revision(LedgerState state, ConversationIdentity identity) =>
        state.Conversations.SingleOrDefault(x => x.Identity == identity)?.Revision ?? 0;
    private static void RequireCurrent(LedgerState state, ConversationIdentity identity, long revision)
    {
        if (revision != Revision(state, identity))
            throw Error(LedgerErrors.StaleRevision, "A newer message changed the conversation context.");
    }
    private static void EnsureConversation(LedgerState state, ConversationIdentity identity)
    {
        if (!state.Conversations.Any(x => x.Identity == identity)) state.Conversations.Add(new(identity, 0));
    }
    private static void ReplaceConversation(LedgerState state, ConversationIdentity identity, long revision)
    {
        EnsureConversation(state, identity);
        var original = state.Conversations.Single(x => x.Identity == identity);
        Replace(state.Conversations, original, original with { Revision = revision });
    }
    private static void Replace<T>(List<T> list, T original, T replacement) => list[list.IndexOf(original)] = replacement;
    private static Mutation<T> Changed<T>(T value) => new(value, true);
    private static Mutation<T> Unchanged<T>(T value) => new(value, false);
    private sealed record Mutation<T>(T Value, bool Changed);
    private static LedgerException Error(string code, string message) => new(code, message);
    private static byte[] Purpose(long sequence, string scheme) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{scheme}/{sequence}");
    private void ValidateBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > _options.MaximumBodyCharacters)
            throw new ArgumentException("A nonempty, bounded body is required.", nameof(body));
    }
    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512 || token.Any(char.IsControl))
            throw new ArgumentException("A nonempty, bounded identifier is required.");
    }
    private static void ValidateIdentity(ConversationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateToken(identity.UserId); ValidateToken(identity.BindingId); ValidateToken(identity.ConversationId);
    }
    private static void ValidateTask(TaskReference? task)
    {
        if (task is null) return;
        ValidateToken(task.HostId); ValidateToken(task.ThreadId);
        if (task.TurnId is not null) ValidateToken(task.TurnId);
        if (task.ProfileId is not null) ValidateToken(task.ProfileId);
    }
    private static void ValidateState(LedgerState state)
    {
        if (state.SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11) || state.Conversations is null || state.Inbox is null || state.Events is null
            || state.Generations is null || state.Authorizations is null || state.Operations is null || state.Outbox is null
            || state.ToolInvocations is null || state.AgentBudgets is null || state.NotificationDrafts is null || state.Contexts is null || state.InputWork is null
            || state.WorkItems is null || state.Schedules is null || state.Subscriptions is null || state.CommitmentRuns is null || state.CommitmentMutations is null
            || state.CommitmentBudgetReservations is null || state.ChannelWakes is null || state.WakeChannels is null
            || state.CatchUpWindows is null || state.CatchUpBatches is null || state.CommitmentObservationSources is null
            || state.DependentWork is null || state.DependentAttempts is null || state.DependentMutations is null)
            throw Error(LedgerErrors.CorruptStorage, "Unsupported or incomplete ledger snapshot.");
        foreach (var conversation in state.Conversations)
        {
            ValidateIdentity(conversation.Identity);
            if (conversation.Revision < 0) throw Error(LedgerErrors.CorruptStorage, "Invalid conversation revision.");
        }
        if (state.Conversations.Select(x => x.Identity).Distinct().Count() != state.Conversations.Count
            || state.Inbox.Select(x => (x.Identity, x.SourceMessageId)).Distinct().Count() != state.Inbox.Count
            || state.Operations.Select(x => (x.Identity, x.IdempotencyKey)).Distinct().Count() != state.Operations.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate identities or idempotency keys in ledger.");
        foreach (var message in state.Inbox)
        {
            if (message.Id == Guid.Empty || !Enum.IsDefined(message.Trust) || !Enum.IsDefined(message.Stage)
                || message.Revision < 1 || message.Revision > Revision(state, message.Identity))
                throw Error(LedgerErrors.CorruptStorage, "Invalid inbox entry.");
        }
        foreach (var authorization in state.Authorizations)
        {
            var source = RequireInbox(state, authorization.Identity, authorization.SourceMessageId);
            if (source.Trust != MessageTrust.AuthenticatedUser || authorization.Revision != source.Revision
                || string.IsNullOrWhiteSpace(authorization.SourceExcerpt)
                || !source.Body.Contains(authorization.SourceExcerpt, StringComparison.Ordinal))
                throw Error(LedgerErrors.CorruptStorage, "Invalid authorization provenance.");
        }
        foreach (var operation in state.Operations)
        {
            var authorization = RequireAuthorization(state, operation.Identity, operation.AuthorizationId);
            if (!Enum.IsDefined(operation.Stage) || operation.Action != authorization.Action
                || operation.TargetKey != authorization.TargetKey || operation.ArgumentsJson != authorization.ArgumentsJson
                || operation.SourceMessageId != authorization.SourceMessageId || operation.Revision != authorization.Revision
                || operation.Task != authorization.Task)
                throw Error(LedgerErrors.CorruptStorage, "Invalid operation provenance.");
        }
        foreach (var generation in state.Generations)
            if (!Enum.IsDefined(generation.Stage) || generation.InboxIds.IsDefaultOrEmpty
                || generation.InboxIds.Any(id => !state.Inbox.Any(x => x.Id == id && x.Identity == generation.Identity)))
                throw Error(LedgerErrors.CorruptStorage, "Invalid generation scope.");
        ValidateNotificationAndOutboxScope(state);
        ValidateConversationContexts(state);
        ValidateInputWork(state);
        ValidateCommitments(state);
        ValidateDependentWork(state);
        ValidateCommitmentObservationSources(state);
        ValidateCommitmentBudgets(state);
        ValidateChannelWakes(state);
        ValidateCatchUp(state);
        ValidateOutputDependencyState(state);
        foreach (var invocation in state.ToolInvocations)
        {
            var source = RequireInbox(state, invocation.Identity, invocation.SourceMessageId);
            if (!Enum.IsDefined(invocation.Stage) || invocation.Revision != source.Revision
                || !state.Generations.Any(x => x.Id == invocation.GenerationId && x.Identity == invocation.Identity))
                throw Error(LedgerErrors.CorruptStorage, "Invalid tool invocation scope.");
            if (invocation.OperationId is { } operationId)
            {
                var operation = RequireOperation(state, invocation.Identity, operationId);
                if (!invocation.HasSideEffects || operation.SourceMessageId != invocation.SourceMessageId
                    || operation.Action != invocation.ToolName || operation.ArgumentsJson != invocation.ArgumentsJson)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid tool operation association.");
            }
            if (state.Generations.Single(x => x.Id == invocation.GenerationId).InputWorkId is not null
                && (invocation.OperationId is not null || invocation.HasSideEffects && invocation.Stage is ToolInvocationStage.Executing or ToolInvocationStage.Unknown))
                throw Error(LedgerErrors.CorruptStorage, "Deferred input work cannot dispatch side effects.");
        }
        foreach (var budget in state.AgentBudgets)
            if (budget.ModelTurns < 0 || budget.ToolCalls < 0
                || RequireInbox(state, budget.Identity, budget.SourceMessageId).Revision != budget.Revision)
                throw Error(LedgerErrors.CorruptStorage, "Invalid agent budget scope.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await _storage.DisposeAsync().ConfigureAwait(false); }
            finally { if (_historyArchive != null) await _historyArchive.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads one immutable event from the last committed snapshot without taking the transaction lock.
    /// Dependency validators may call this while a new transaction is being evaluated.</summary>
    public ConversationEvent? ReadCommittedEvent(ConversationIdentity identity, Guid eventId) =>
        Volatile.Read(ref _state).Events.SingleOrDefault(e => e.Identity == identity && e.Id == eventId);

    private sealed class LedgerState
    {
        public required int SchemaVersion { get; set; }
        public bool OutputDependenciesRequired { get; set; }
        public required List<ConversationState> Conversations { get; init; }
        public required List<InboxMessage> Inbox { get; init; }
        public required List<ConversationEvent> Events { get; init; }
        public required List<GenerationRecord> Generations { get; init; }
        public required List<UserAuthorization> Authorizations { get; init; }
        public required List<OperationRecord> Operations { get; init; }
        public required List<OutboxMessage> Outbox { get; init; }
        public List<ToolInvocationRecord> ToolInvocations { get; init; } = [];
        public List<AgentBudgetRecord> AgentBudgets { get; init; } = [];
        public List<DurableNotificationDraft> NotificationDrafts { get; init; } = [];
        public List<ConversationContext> Contexts { get; init; } = [];
        public List<InputWorkItem> InputWork { get; init; } = [];
        public List<TrackedWorkItem> WorkItems { get; init; } = [];
        public List<ReminderSchedule> Schedules { get; init; } = [];
        public List<TaskSubscription> Subscriptions { get; init; } = [];
        public List<CommitmentObservationSource> CommitmentObservationSources { get; init; } = [];
        public List<CommitmentRun> CommitmentRuns { get; init; } = [];
        public List<CommitmentMutationReceipt> CommitmentMutations { get; init; } = [];
        public List<DependentWorkItem> DependentWork { get; init; } = [];
        public List<DependentDispatchAttempt> DependentAttempts { get; init; } = [];
        public List<DependentWorkMutation> DependentMutations { get; init; } = [];
        public List<CommitmentBudgetReservation> CommitmentBudgetReservations { get; init; } = [];
        public List<ChannelWakeRecord> ChannelWakes { get; init; } = [];
        public List<ConfiguredWakeChannel> WakeChannels { get; init; } = [];
        public List<CatchUpWindow> CatchUpWindows { get; init; } = [];
        public List<CatchUpBatch> CatchUpBatches { get; init; } = [];
        public static LedgerState Empty() => new()
        {
            SchemaVersion = 1, Conversations = [], Inbox = [], Events = [], Generations = [],
            Authorizations = [], Operations = [], Outbox = [], ToolInvocations = [], AgentBudgets = [], NotificationDrafts = [], Contexts = []
        };
        public LedgerState Clone() => new()
        {
            SchemaVersion = SchemaVersion, OutputDependenciesRequired = OutputDependenciesRequired, Conversations = [.. Conversations], Inbox = [.. Inbox],
            Events = [.. Events], Generations = [.. Generations], Authorizations = [.. Authorizations],
            Operations = [.. Operations], Outbox = [.. Outbox], ToolInvocations = [.. ToolInvocations], AgentBudgets = [.. AgentBudgets],
            NotificationDrafts = [.. NotificationDrafts], Contexts = [.. Contexts], InputWork = [.. InputWork],
            WorkItems = [.. WorkItems], Schedules = [.. Schedules], Subscriptions = [.. Subscriptions],
            CommitmentObservationSources = [.. CommitmentObservationSources],
            CommitmentRuns = [.. CommitmentRuns], CommitmentMutations = [.. CommitmentMutations],
            DependentWork = [.. DependentWork], DependentAttempts = [.. DependentAttempts], DependentMutations = [.. DependentMutations],
            CommitmentBudgetReservations = [.. CommitmentBudgetReservations], ChannelWakes = [.. ChannelWakes], WakeChannels = [.. WakeChannels],
            CatchUpWindows = [.. CatchUpWindows], CatchUpBatches = [.. CatchUpBatches]
        };
    }
}
