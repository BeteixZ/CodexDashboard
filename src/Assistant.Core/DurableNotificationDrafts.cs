using System.Collections.Immutable;
using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    /// <summary>
    /// Captures host-synthesized public facts before any model preparation. Payload is data only:
    /// it cannot grant authority. The host must exclude credentials and private transport metadata.
    /// </summary>
    public Task<NotificationCaptureResult> CaptureNotificationAsync(ConversationIdentity identity,
        string eventKey, string contextBody, string payloadJson, TaskReference? task,
        ConversationSource source = ConversationSource.Notification, int maximumPreparationAttempts = 3,
        CancellationToken cancellationToken = default, bool isDefaultTaskNotification = false) => ChangeAsync(state =>
        {
            ValidateIdentity(identity); ValidateToken(eventKey); ValidateBody(contextBody); ValidateTask(task);
            RequireDefaultNotificationAllowed(state, identity, task, isDefaultTaskNotification);
            ValidateNotificationPayload(payloadJson, _options.MaximumBodyCharacters);
            if (source is not (ConversationSource.Notification or ConversationSource.ToolResult))
                throw Error(LedgerErrors.AuthorizationRequired, "A notification draft is read-only context, never user authority.");
            if (maximumPreparationAttempts is < 1 or > 16)
                throw new ArgumentOutOfRangeException(nameof(maximumPreparationAttempts));
            var existing = state.NotificationDrafts.SingleOrDefault(x => x.Identity == identity && x.EventKey == eventKey);
            if (existing is not null)
            {
                if (existing.ContextBody != contextBody || existing.PayloadJson != payloadJson
                    || existing.Task != task || existing.Source != source
                    || existing.MaximumPreparationAttempts != maximumPreparationAttempts
                    || RequireEvent(state, identity, existing.EventId).IsDefaultTaskNotification != isDefaultTaskNotification)
                    throw Error(LedgerErrors.Conflict, "The notification key already identifies different captured facts.");
                return Unchanged(new NotificationCaptureResult(existing, RequireEvent(state, identity, existing.EventId), true));
            }
            if (state.Events.Any(x => x.Identity == identity && x.ExternalEventKey == eventKey))
                throw Error(LedgerErrors.Conflict, "The notification key was already finalized without a draft.");
            // This is a global unfinished-work bound, in addition to the journal/snapshot byte bounds.
            if (state.NotificationDrafts.Count(x => x.Stage != NotificationDraftStage.Finalized && !x.Cancelled) >= _options.MaximumPendingNotificationDrafts)
                throw Error(LedgerErrors.CapacityExceeded, "The pending notification draft capacity is full; nothing was removed.");
            EnsureConversation(state, identity);
            var now = _clock.UtcNow;
            var entry = new ConversationEvent(Guid.NewGuid(), identity, source, contextBody, now, now,
                Revision(state, identity), Task: task, ExternalEventKey: eventKey, IsDefaultTaskNotification: isDefaultTaskNotification);
            var draft = new DurableNotificationDraft(Guid.NewGuid(), identity, entry.Id, eventKey,
                contextBody, payloadJson, task, source, NotificationDraftStage.Captured, 0,
                maximumPreparationAttempts, now, now);
            state.Events.Add(entry);
            NoteNotificationContext(state, entry);
            state.NotificationDrafts.Add(draft);
            return Changed(new NotificationCaptureResult(draft, entry, false));
        }, cancellationToken);

    /// <summary>Commits the exclusive preparation claim and its attempt before the host calls a read-only model.</summary>
    public Task<NotificationPreparationLease> BeginNotificationPreparationAsync(ConversationIdentity identity,
        Guid draftId, CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var draft = RequireNotificationDraft(state, identity, draftId);
            RequireCommitmentDraftPolicy(state, draft);
            if (draft.Stage != NotificationDraftStage.Captured)
                throw Error(LedgerErrors.InvalidTransition, "Only an unclaimed captured notification can be prepared.");
            if (draft.PreparationAttempts >= draft.MaximumPreparationAttempts)
                throw Error(LedgerErrors.PreparationBudgetExceeded, "The durable preparation budget is exhausted; use a factual fallback.");
            var claim = Guid.NewGuid();
            var attempt = checked(draft.PreparationAttempts + 1);
            if (draft.WorkRunId is { } runId)
            {
                var run = RequireCommitmentRun(state, identity, runId);
                var work = RequireCommitment(state, identity, run.WorkItemId);
                ReserveCommitmentBudget(state, work, run, CommitmentBudgetKind.ModelPreparation, claim, attempt);
            }
            Replace(state.NotificationDrafts, draft, draft with
            {
                Stage = NotificationDraftStage.Preparing, PreparationId = claim,
                PreparationAttempts = attempt, UpdatedAt = _clock.UtcNow
            });
            return Changed(new NotificationPreparationLease(draft.Id, identity, claim, attempt));
        }, cancellationToken);

    /// <summary>Releases a matching claim after cancellation/failure without refunding its durable attempt.</summary>
    public Task<DurableNotificationDraft> EndNotificationPreparationAsync(NotificationPreparationLease lease,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var draft = RequireNotificationDraft(state, lease.Identity, lease.DraftId);
            RequireNotificationClaim(draft, lease);
            if (draft.Stage == NotificationDraftStage.Finalized) return Unchanged(draft);
            if (draft.Stage != NotificationDraftStage.Preparing)
                throw Error(LedgerErrors.InvalidTransition, "The preparation claim is no longer active.");
            var next = draft with { Stage = NotificationDraftStage.Captured, PreparationId = null, UpdatedAt = _clock.UtcNow };
            Replace(state.NotificationDrafts, draft, next);
            return Changed(next);
        }, cancellationToken);

    /// <summary>Atomically saves the prepared result and one outbox row per actual channel send.</summary>
    public Task<NotificationEnqueueResult> FinalizeNotificationAsync(NotificationPreparationLease lease,
        string preparedBody, IReadOnlyList<string>? segments = null, CancellationToken cancellationToken = default,
        OutputDependencies? outputDependencies = null) =>
        DependencyChangeAsync(lease.Identity, _ => outputDependencies, state =>
        {
            var draft = RequireNotificationDraft(state, lease.Identity, lease.DraftId);
            RequireNotificationClaim(draft, lease);
            if (draft.Stage is not (NotificationDraftStage.Preparing or NotificationDraftStage.Finalized))
                throw Error(LedgerErrors.InvalidTransition, "The preparation claim is no longer active.");
            return FinalizeNotification(state, draft, preparedBody, segments, outputDependencies);
        }, cancellationToken);

    /// <summary>
    /// Finalizes host-provided factual text without a model attempt. Allowed after budget exhaustion,
    /// but never while another preparation claim is active.
    /// </summary>
    public Task<NotificationEnqueueResult> FinalizeNotificationFallbackAsync(ConversationIdentity identity,
        Guid draftId, string body, IReadOnlyList<string>? segments = null, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var draft = RequireNotificationDraft(state, identity, draftId);
            if (draft.Stage == NotificationDraftStage.Preparing)
                throw Error(LedgerErrors.InvalidTransition, "Release the active preparation claim before using fallback.");
            return FinalizeNotification(state, draft, body, segments, OutputDependencies.Independent);
        }, cancellationToken);

    public async Task<ImmutableArray<DurableNotificationDraft>> GetPendingNotificationDraftsAsync(
        ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return snapshot.IsReadOnly ? [] : snapshot.NotificationDrafts
            .Where(x => x.Stage == NotificationDraftStage.Captured && CommitmentPreparationReady(snapshot, x, _clock.UtcNow)).ToImmutableArray();
    }

    private Mutation<NotificationEnqueueResult> FinalizeNotification(LedgerState state,
        DurableNotificationDraft draft, string body, IReadOnlyList<string>? segments, OutputDependencies? outputDependencies)
    {
        RequireCommitmentDraftPolicy(state, draft);
        ValidateBody(body);
        var parts = segments is null ? ImmutableArray.Create(body) : segments.ToImmutableArray();
        if (parts.IsDefaultOrEmpty || parts.Length > _options.MaximumSegments)
            throw new ArgumentOutOfRangeException(nameof(segments));
        foreach (var part in parts) ValidateBody(part);
        var entry = RequireEvent(state, draft.Identity, draft.EventId);
        if (draft.Stage == NotificationDraftStage.Finalized)
        {
            if (draft.PreparedBody != body || entry.NotificationSegments is not { } original || !original.SequenceEqual(parts)
                || !OutputDependencies.Equivalent(entry.OutputDependencies, outputDependencies))
                throw Error(LedgerErrors.Conflict, "The notification was already finalized with different text or channel segments.");
            return Unchanged(new NotificationEnqueueResult(entry,
                state.Outbox.Where(x => x.Identity == draft.Identity && x.Kind == OutboxKind.Notification
                    && x.NotificationEventId == entry.Id).OrderBy(x => x.SegmentIndex).ToImmutableArray(), true));
        }
        var now = _clock.UtcNow;
        if (outputDependencies != null) { ValidateDependencyDefinition(outputDependencies); PromoteDependencySchema(state, outputDependencies); }
        // The source event body remains the original facts; generated wording lives in PreparedBody/outbox.
        var preparedEvent = entry with { NotificationSegments = parts, OutputDependencies = outputDependencies };
        var outbox = parts.Select((part, index) => new OutboxMessage(Guid.NewGuid(), draft.Identity, null,
            entry.Revision, index, part, OutboxStage.Pending, now, now,
            Kind: OutboxKind.Notification, NotificationEventId: entry.Id)).ToImmutableArray();
        Replace(state.Events, entry, preparedEvent);
        Replace(state.NotificationDrafts, draft, draft with
        {
            Stage = NotificationDraftStage.Finalized, PreparedBody = body, UpdatedAt = now
        });
        state.Outbox.AddRange(outbox);
        return Changed(new NotificationEnqueueResult(preparedEvent, outbox, false));
    }

    private void RequireCommitmentDraftPolicy(LedgerState state, DurableNotificationDraft draft)
    {
        // A finalized retry is a read only lookup; cancellation still blocks host preparation.
        if (draft.Cancelled || draft.Stage != NotificationDraftStage.Finalized
            && !PreparationReady(state.WorkItems, state.CommitmentRuns, state.Events, draft, _clock.UtcNow))
            throw Error(LedgerErrors.NotificationSuppressed, "The draft is cancelled, paused, expired or suppressed by its current policy.");
    }

    private static DurableNotificationDraft RequireNotificationDraft(LedgerState state,
        ConversationIdentity identity, Guid id) =>
        state.NotificationDrafts.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "Notification draft does not belong to this conversation.");

    private static void RequireNotificationClaim(DurableNotificationDraft draft, NotificationPreparationLease lease)
    {
        if (lease.ClaimId == Guid.Empty || draft.PreparationId != lease.ClaimId || draft.PreparationAttempts != lease.Attempt)
            throw Error(LedgerErrors.InvalidTransition, "The notification preparation claim was superseded or recovered.");
    }

    private static void ValidateNotificationPayload(string payload, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > maximumCharacters)
            throw new ArgumentException("A bounded notification payload object is required.", nameof(payload));
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Notification payload must be a JSON object.", nameof(payload));
            ValidateFields(document.RootElement);
        }
        catch (JsonException error) { throw new ArgumentException("Notification payload must be strict JSON with depth at most 16.", nameof(payload), error); }

        static void ValidateFields(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in element.EnumerateObject())
                {
                    if (!names.Add(field.Name)) throw new ArgumentException("Notification payload contains duplicate fields.");
                    ValidateFields(field.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) ValidateFields(item);
        }
    }

    private static void ValidateNotificationDrafts(LedgerState state)
    {
        if (state.NotificationDrafts.Select(x => x.Id).Distinct().Count() != state.NotificationDrafts.Count
            || state.NotificationDrafts.Select(x => (x.Identity, x.EventKey)).Distinct().Count() != state.NotificationDrafts.Count
            || state.NotificationDrafts.Select(x => x.EventId).Distinct().Count() != state.NotificationDrafts.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate notification draft identity.");
        foreach (var draft in state.NotificationDrafts)
        {
            try
            {
                ValidateIdentity(draft.Identity); ValidateTask(draft.Task);
                ValidateNotificationPayload(draft.PayloadJson, 1024 * 1024);
            }
            catch (ArgumentException)
            {
                throw Error(LedgerErrors.CorruptStorage, "Invalid notification draft identity or payload.");
            }
            var entry = state.Events.SingleOrDefault(x => x.Id == draft.EventId && x.Identity == draft.Identity);
            if (draft.Id == Guid.Empty || draft.EventId == Guid.Empty || entry is null
                || entry.Body != draft.ContextBody || entry.ExternalEventKey != draft.EventKey
                || entry.Task != draft.Task || entry.Source != draft.Source
                || string.IsNullOrWhiteSpace(draft.ContextBody) || draft.ContextBody.Length > 1024 * 1024
                || !Enum.IsDefined(draft.Stage) || draft.MaximumPreparationAttempts is < 1 or > 16
                || draft.PreparationAttempts < 0 || draft.PreparationAttempts > draft.MaximumPreparationAttempts
                || draft.PreparationId == Guid.Empty
                || (draft.PreparationId is not null && draft.PreparationAttempts == 0)
                || (draft.Stage == NotificationDraftStage.Captured && draft.PreparationId is not null)
                || (draft.Stage == NotificationDraftStage.Preparing && draft.PreparationId is null)
                || (draft.Stage != NotificationDraftStage.Finalized && (draft.PreparedBody is not null || entry.NotificationSegments is not null))
                || (draft.Stage == NotificationDraftStage.Finalized
                    && (string.IsNullOrWhiteSpace(draft.PreparedBody) || draft.PreparedBody.Length > 1024 * 1024 || entry.NotificationSegments is null)))
                throw Error(LedgerErrors.CorruptStorage, "Invalid notification draft association or preparation state.");
        }
    }
}
