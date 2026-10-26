using System.Collections.Immutable;
using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    // Immutable committed state; safe for a host view validator already holding its own store lock.
    public ConversationContext? ReadCommittedConversationContext(ConversationIdentity identity) =>
        Volatile.Read(ref _state).Contexts.SingleOrDefault(c => c.Identity == identity);

    public async Task<ConversationContext> ReadConversationContextAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default) =>
        (await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false)).Context!;

    /// <summary>
    /// Trusted host fast path after recognizing an explicit new-dialogue request. Retains history,
    /// notifications and registered operations. Never reports a running task as cancelled.
    /// </summary>
    public Task<ConversationContext> BeginNewDialogueAsync(ConversationIdentity identity, string currentSourceMessageId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var source = RequireCurrentTrustedInput(state, identity, currentSourceMessageId);
            var context = ContextFor(state, identity);
            if (context.ResetSourceMessageId == currentSourceMessageId) return Unchanged(context);
            var next = new ConversationContext(identity, checked(context.Epoch + 1), checked(context.Version + 1),
                source.Revision, source.SourceMessageId);
            SaveContext(state, next);
            InvalidateOldGenerations(state, identity, source.Revision);
            InvalidateInputWork(state, identity, source.Revision, reset: true);
            foreach (var message in state.Inbox.Where(x => x.Identity == identity && x.Revision < source.Revision
                && x.Stage is InboxStage.Received or InboxStage.Grouped or InboxStage.Processing or InboxStage.Superseded).ToArray())
                Replace(state.Inbox, message, message with { Stage = InboxStage.Cancelled });
            state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.System,
                JsonSerializer.Serialize(new { kind = "dialogue_boundary", epoch = next.Epoch,
                    historyDeleted = false, registeredOperationsCancelled = false }),
                _clock.UtcNow, _clock.UtcNow, source.Revision, source.SourceMessageId));
            return Changed(next);
        }, cancellationToken);

    /// <summary>
    /// Trusted host context update only. The host must verify that this current user selected the
    /// exact canonical task. Task reads, model proposals and notifications must not call this method.
    /// </summary>
    public Task<ConversationContext> SetWorkFocusAsync(ConversationIdentity identity, string currentSourceMessageId,
        TaskReference task, string sourceExcerpt, CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var source = RequireCurrentTrustedInput(state, identity, currentSourceMessageId);
            ValidateTask(task); ArgumentNullException.ThrowIfNull(task);
            ValidateSourceExcerpt(source, sourceExcerpt);
            var context = ContextFor(state, identity);
            if (context.Focus is { } focus && focus.SourceMessageId == source.SourceMessageId
                && focus.Task == task && focus.SourceExcerpt == sourceExcerpt && context.PendingSelection is null)
                return Unchanged(context);
            var next = context with
            {
                Version = checked(context.Version + 1),
                Focus = new(task, source.SourceMessageId, sourceExcerpt, _clock.UtcNow),
                PendingSelection = null, LastSelection = null
            };
            SaveContext(state, next);
            return Changed(next);
        }, cancellationToken);

    /// <summary>Persists an immutable, bounded candidate list. Selecting a candidate never executes a prior request.</summary>
    public Task<PendingTaskSelection> OpenTaskSelectionAsync(ConversationIdentity identity, string currentSourceMessageId,
        TaskSelectionPurpose purpose, IReadOnlyList<TaskSelectionCandidate> candidates, TimeSpan lifetime,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            var source = RequireCurrentTrustedInput(state, identity, currentSourceMessageId);
            if (!Enum.IsDefined(purpose) || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            var choices = candidates.ToImmutableArray();
            ValidateSelectionCandidates(choices);
            var context = ContextFor(state, identity);
            if (context.PendingSelection is { } existing && existing.SourceMessageId == source.SourceMessageId)
            {
                if (existing.Purpose != purpose || !existing.Candidates.SequenceEqual(choices)
                    || existing.ExpiresAt - existing.CreatedAt != lifetime)
                    throw Error(LedgerErrors.Conflict, "This input already opened a different task selection.");
                return Unchanged(existing);
            }
            var now = _clock.UtcNow;
            var selection = new PendingTaskSelection(Guid.NewGuid(), context.Epoch, source.SourceMessageId,
                purpose, choices, now, now + lifetime);
            SaveContext(state, context with { Version = checked(context.Version + 1), PendingSelection = selection, LastSelection = null });
            return Changed(selection);
        }, cancellationToken);

    /// <summary>
    /// Resolves one current user selection against the saved list (one-based numbering).
    /// The host must recognize the literal choice, not ask a model to supply authorization.
    /// </summary>
    public Task<TaskSelectionResult> SelectTaskCandidateAsync(ConversationIdentity identity, string currentSourceMessageId,
        Guid selectionId, int oneBasedNumber, string sourceExcerpt, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var source = RequireCurrentTrustedInput(state, identity, currentSourceMessageId);
            ValidateSourceExcerpt(source, sourceExcerpt);
            var context = ContextFor(state, identity);
            if (context.LastSelection is { } previous && previous.SelectionSourceMessageId == source.SourceMessageId)
            {
                if (previous.SelectionId != selectionId || previous.ChoiceNumber != oneBasedNumber)
                    throw Error(LedgerErrors.Conflict, "This user input already selected a different task candidate.");
                return Unchanged(previous);
            }
            var selection = context.PendingSelection;
            if (selection is null || selection.Id != selectionId || selection.DialogueEpoch != context.Epoch
                || selection.ExpiresAt <= _clock.UtcNow)
                throw Error(LedgerErrors.InvalidTransition, "The task selection expired or belongs to another dialogue.");
            if (oneBasedNumber < 1 || oneBasedNumber > selection.Candidates.Length)
                throw new ArgumentOutOfRangeException(nameof(oneBasedNumber));
            var candidate = selection.Candidates[oneBasedNumber - 1];
            var result = new TaskSelectionResult(candidate.Task, selection.Purpose,
                selection.SourceMessageId, source.SourceMessageId, selection.Id, oneBasedNumber);
            var next = context with
            {
                Version = checked(context.Version + 1), PendingSelection = null, LastSelection = result,
                Focus = selection.Purpose == TaskSelectionPurpose.Focus
                    ? new(candidate.Task, source.SourceMessageId, sourceExcerpt, _clock.UtcNow) : context.Focus
            };
            SaveContext(state, next);
            return Changed(result);
        }, cancellationToken);

    /// <summary>
    /// Records a host-observed approval as context only. An executor must independently verify the
    /// live backend request, current user authority and capability before answering any approval.
    /// </summary>
    public Task<PendingApprovalContext> RecordPendingApprovalAsync(ConversationIdentity identity, Guid sourceEventId,
        string requestId, TaskReference task, Guid backendEpoch, DateTimeOffset expiresAt, long expectedDialogueEpoch,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            ValidateIdentity(identity); ValidateToken(requestId); ValidateTask(task);
            if (task?.TurnId is null || backendEpoch == Guid.Empty || expiresAt <= _clock.UtcNow
                || expiresAt > _clock.UtcNow.AddHours(24))
                throw new ArgumentException("A bounded approval must identify the exact backend and turn.");
            var entry = RequireEvent(state, identity, sourceEventId);
            var context = ContextFor(state, identity);
            if (context.Epoch != expectedDialogueEpoch || entry.Revision < context.StartRevision)
                throw Error(LedgerErrors.StaleRevision, "Approval context belongs to an older dialogue.");
            if (entry.Source is not (ConversationSource.ToolResult or ConversationSource.Notification) || entry.Task != task)
                throw Error(LedgerErrors.IdentityMismatch, "Approval context must match its exact host observation.");
            if (context.PendingApproval is { } existing && existing.BackendEpoch == backendEpoch && existing.RequestId == requestId)
            {
                if (existing.SourceEventId != sourceEventId || existing.Task != task || existing.ExpiresAt != expiresAt)
                    throw Error(LedgerErrors.Conflict, "The approval request was already observed with different evidence.");
                return Unchanged(existing);
            }
            var approval = new PendingApprovalContext(Guid.NewGuid(), context.Epoch, sourceEventId,
                requestId, task, backendEpoch, _clock.UtcNow, expiresAt);
            SaveContext(state, context with { Version = checked(context.Version + 1), PendingApproval = approval });
            return Changed(approval);
        }, cancellationToken);

    public Task<bool> ResolvePendingApprovalAsync(ConversationIdentity identity, Guid approvalId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            ValidateIdentity(identity);
            var context = ContextFor(state, identity);
            if (context.PendingApproval?.Id != approvalId) return Unchanged(false);
            SaveContext(state, context with { Version = checked(context.Version + 1), PendingApproval = null });
            return Changed(true);
        }, cancellationToken);

    private static ConversationContext ContextFor(LedgerState state, ConversationIdentity identity) =>
        state.Contexts.SingleOrDefault(x => x.Identity == identity) ?? new(identity, 0, 0, 0);

    private static void SaveContext(LedgerState state, ConversationContext context)
    {
        EnsureConversation(state, context.Identity);
        var previous = state.Contexts.SingleOrDefault(x => x.Identity == context.Identity);
        if (previous is null) state.Contexts.Add(context);
        else Replace(state.Contexts, previous, context);
    }

    private static InboxMessage RequireCurrentTrustedInput(LedgerState state, ConversationIdentity identity, string sourceId)
    {
        ValidateIdentity(identity); ValidateToken(sourceId);
        var source = RequireInbox(state, identity, sourceId);
        RequireCurrent(state, identity, source.Revision);
        if (source.Trust != MessageTrust.AuthenticatedUser)
            throw Error(LedgerErrors.AuthorizationRequired, "Context selection requires a current authenticated user input.");
        return source;
    }

    private static void ValidateSourceExcerpt(InboxMessage source, string excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt) || excerpt.Length > 4096
            || !source.Body.Contains(excerpt, StringComparison.Ordinal))
            throw Error(LedgerErrors.AuthorizationRequired, "The source excerpt must occur literally in the current user input.");
    }

    private static void ValidateSelectionCandidates(ImmutableArray<TaskSelectionCandidate> candidates)
    {
        if (candidates.IsDefaultOrEmpty || candidates.Length > 40 || candidates.Any(x => x is null || x.Task is null)
            || candidates.Select(x => x.Task).Distinct().Count() != candidates.Length)
            throw new ArgumentException("Select 1 to 40 distinct canonical task candidates.");
        foreach (var candidate in candidates)
        {
            ValidateTask(candidate.Task);
            if (string.IsNullOrWhiteSpace(candidate.Label) || candidate.Label.Length > 256 || candidate.Label.Any(char.IsControl))
                throw new ArgumentException("Task selection labels must be bounded plain text.");
        }
    }

    private static void NoteNotificationContext(LedgerState state, ConversationEvent entry)
    {
        var context = ContextFor(state, entry.Identity);
        if (entry.Revision < context.StartRevision) return;
        SaveContext(state, context with { Version = checked(context.Version + 1),
            LastNotification = new(entry.Id, entry.Task, entry.RecordedAt) });
    }

    private static void NoteNotificationAcceptance(LedgerState state, OutboxMessage message)
    {
        var context = ContextFor(state, message.Identity);
        if (context.LastNotification is not { } notice || notice.EventId != message.NotificationEventId) return;
        SaveContext(state, context with { Version = checked(context.Version + 1),
            LastNotification = notice with { LastAcceptedOutboxId = message.Id } });
    }

    private static void ValidateConversationContexts(LedgerState state)
    {
        try { ValidateConversationContextsCore(state); }
        catch (LedgerException error) when (error.Code != LedgerErrors.CorruptStorage)
        { throw Error(LedgerErrors.CorruptStorage, "Conversation context references invalid provenance."); }
        catch (ArgumentException)
        { throw Error(LedgerErrors.CorruptStorage, "Conversation context contains invalid data."); }
    }

    private static void ValidateConversationContextsCore(LedgerState state)
    {
        if (state.Contexts.Select(x => x.Identity).Distinct().Count() != state.Contexts.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate conversation context.");
        foreach (var context in state.Contexts)
        {
            if (context.Epoch < 0 || context.Version < 0 || context.StartRevision < 0
                || context.StartRevision > Revision(state, context.Identity)
                || !state.Conversations.Any(x => x.Identity == context.Identity))
                throw Error(LedgerErrors.CorruptStorage, "Invalid conversation context boundary.");
            if (context.ResetSourceMessageId is { } reset)
            {
                var source = RequireInbox(state, context.Identity, reset);
                if (source.Trust != MessageTrust.AuthenticatedUser || source.Revision != context.StartRevision || context.Epoch == 0)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid dialogue reset provenance.");
            }
            else if (context.Epoch != 0 || context.StartRevision != 0)
                throw Error(LedgerErrors.CorruptStorage, "Dialogue boundary requires its original user source.");
            if (context.Focus is { } focus)
            {
                var source = RequireInbox(state, context.Identity, focus.SourceMessageId);
                if (focus.Task is null || source.Trust != MessageTrust.AuthenticatedUser || source.Revision < context.StartRevision
                    || string.IsNullOrWhiteSpace(focus.SourceExcerpt) || !source.Body.Contains(focus.SourceExcerpt, StringComparison.Ordinal))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid work focus provenance.");
                ValidateTask(focus.Task);
            }
            if (context.LastNotification is { } notice)
            {
                var entry = RequireEvent(state, context.Identity, notice.EventId);
                if (entry.ExternalEventKey is null || entry.Task != notice.Task || entry.RecordedAt != notice.RecordedAt
                    || entry.Revision < context.StartRevision || notice.LastAcceptedOutboxId is { } outboxId
                    && !state.Outbox.Any(x => x.Identity == context.Identity && x.Id == outboxId
                        && x.NotificationEventId == entry.Id && x.Stage == OutboxStage.Accepted))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid recent notification context.");
            }
            if (context.PendingSelection is { } selection)
            {
                var source = RequireInbox(state, context.Identity, selection.SourceMessageId);
                if (selection.Id == Guid.Empty || selection.DialogueEpoch != context.Epoch || !Enum.IsDefined(selection.Purpose)
                    || source.Trust != MessageTrust.AuthenticatedUser || source.Revision < context.StartRevision
                    || selection.ExpiresAt <= selection.CreatedAt || selection.ExpiresAt - selection.CreatedAt > TimeSpan.FromHours(1))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid pending selection context.");
                try { ValidateSelectionCandidates(selection.Candidates); }
                catch (ArgumentException) { throw Error(LedgerErrors.CorruptStorage, "Invalid selection candidate list."); }
            }
            if (context.PendingApproval is { } approval)
            {
                var entry = RequireEvent(state, context.Identity, approval.SourceEventId);
                if (approval.Id == Guid.Empty || approval.BackendEpoch == Guid.Empty || approval.DialogueEpoch != context.Epoch
                    || entry.Task != approval.Task || approval.Task?.TurnId is null || entry.Revision < context.StartRevision
                    || entry.Source is not (ConversationSource.ToolResult or ConversationSource.Notification)
                    || approval.ExpiresAt <= approval.CreatedAt || approval.ExpiresAt - approval.CreatedAt > TimeSpan.FromHours(24))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid pending approval context.");
                ValidateToken(approval.RequestId);
            }
            if (context.LastSelection is { } result)
            {
                var original = RequireInbox(state, context.Identity, result.OriginalSourceMessageId);
                var selected = RequireInbox(state, context.Identity, result.SelectionSourceMessageId);
                if (result.SelectionId == Guid.Empty || !Enum.IsDefined(result.Purpose) || result.Task is null
                    || result.ChoiceNumber is < 1 or > 40 || original.Trust != MessageTrust.AuthenticatedUser
                    || selected.Trust != MessageTrust.AuthenticatedUser || original.Revision < context.StartRevision
                    || selected.Revision < original.Revision || context.PendingSelection is not null)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid completed selection context.");
                ValidateTask(result.Task);
            }
        }
    }
}
