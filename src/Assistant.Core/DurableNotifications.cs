using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    /// <summary>
    /// Atomically records an untrusted/context event and its actual channel segments. Notifications
    /// are independent of input revisions and never fabricate a user message, generation or grant.
    /// </summary>
    public Task<NotificationEnqueueResult> EnqueueNotificationAsync(ConversationIdentity identity,
        string eventKey, string body, TaskReference? task,
        ConversationSource source = ConversationSource.Notification, IReadOnlyList<string>? segments = null,
        CancellationToken cancellationToken = default, bool isDefaultTaskNotification = false,
        OutputDependencies? outputDependencies = null) => DependencyChangeAsync(identity, _ => outputDependencies, state =>
        {
            ValidateIdentity(identity); ValidateToken(eventKey); ValidateBody(body); ValidateTask(task);
            RequireDefaultNotificationAllowed(state, identity, task, isDefaultTaskNotification);
            if (source is not (ConversationSource.Notification or ConversationSource.ToolResult))
                throw Error(LedgerErrors.AuthorizationRequired, "Only notification or tool-result data can enqueue a notification.");
            var parts = segments is null ? ImmutableArray.Create(body) : segments.ToImmutableArray();
            if (parts.IsDefaultOrEmpty || parts.Length > _options.MaximumSegments)
                throw new ArgumentOutOfRangeException(nameof(segments));
            foreach (var part in parts) ValidateBody(part);
            var existing = state.Events.SingleOrDefault(x => x.Identity == identity && x.ExternalEventKey == eventKey);
            if (existing is not null)
            {
                if (existing.Body != body || existing.Source != source || existing.Task != task
                    || existing.IsDefaultTaskNotification != isDefaultTaskNotification
                    || existing.NotificationSegments is not { } original || !original.SequenceEqual(parts)
                    || !OutputDependencies.Equivalent(existing.OutputDependencies, outputDependencies))
                    throw Error(LedgerErrors.Conflict, "The notification key was already recorded with different content or task identity.");
                return Unchanged(new NotificationEnqueueResult(existing,
                    state.Outbox.Where(x => x.Identity == identity && x.Kind == OutboxKind.Notification && x.NotificationEventId == existing.Id)
                        .OrderBy(x => x.SegmentIndex).ToImmutableArray(), true));
            }
            var revision = Revision(state, identity);
            if (!state.Conversations.Any(x => x.Identity == identity)) ReplaceConversation(state, identity, revision);
            var now = _clock.UtcNow;
            if (outputDependencies != null) { ValidateDependencyDefinition(outputDependencies); PromoteDependencySchema(state, outputDependencies); }
            var entry = new ConversationEvent(Guid.NewGuid(), identity, source, body, now, now, revision,
                Task: task, ExternalEventKey: eventKey, NotificationSegments: parts, IsDefaultTaskNotification: isDefaultTaskNotification,
                OutputDependencies: outputDependencies);
            var outbox = parts.Select((part, index) => new OutboxMessage(Guid.NewGuid(), identity, null,
                revision, index, part, OutboxStage.Pending, now, now,
                Kind: OutboxKind.Notification, NotificationEventId: entry.Id)).ToImmutableArray();
            state.Events.Add(entry);
            NoteNotificationContext(state, entry);
            state.Outbox.AddRange(outbox);
            return Changed(new NotificationEnqueueResult(entry, outbox, false));
        }, cancellationToken);

    /// <summary>Explicit channel-recovery trigger; unknown outcomes and conversation replies are excluded.</summary>
    public Task<ImmutableArray<OutboxMessage>> RetryRejectedNotificationsAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
        {
            ValidateIdentity(identity);
            var rejected = state.Outbox.Where(x => x.Identity == identity && x.Kind == OutboxKind.Notification
                && x.Stage == OutboxStage.Rejected && MessagePolicyReady(state.WorkItems, state.CommitmentRuns,
                    state.NotificationDrafts, state.Events, x, _clock.UtcNow)
                && DependentNotificationReady(state.DependentWork, state.Events, state.CommitmentBudgetReservations, x, _clock.UtcNow)
                && CatchUpRetryAllowed(state, x)).ToArray();
            if (rejected.Length == 0) return Unchanged(ImmutableArray<OutboxMessage>.Empty);
            var next = rejected.Select(x => x with { Stage = OutboxStage.Pending, UpdatedAt = _clock.UtcNow }).ToImmutableArray();
            for (var i = 0; i < rejected.Length; i++) Replace(state.Outbox, rejected[i], next[i]);
            return Changed(next);
        }, cancellationToken);

    public static bool SameOutboxGroup(OutboxMessage first, OutboxMessage second) =>
        first.Identity == second.Identity && first.Kind == second.Kind
        && (first.Kind == OutboxKind.Notification
            ? first.NotificationEventId is not null && first.NotificationEventId == second.NotificationEventId
            : first.GenerationId is not null && first.GenerationId == second.GenerationId);

    private static void ValidateNotificationAndOutboxScope(LedgerState state)
    {
        ValidateNotificationDrafts(state);
        var notificationEvents = state.Events.Where(x => x.ExternalEventKey is not null).ToArray();
        if (notificationEvents.Select(x => (x.Identity, x.ExternalEventKey)).Distinct().Count() != notificationEvents.Length
            || state.Outbox.Select(x => x.Id).Distinct().Count() != state.Outbox.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate notification keys or outbox IDs.");
        foreach (var entry in state.Events)
        {
            if (entry.ExternalEventKey is null)
            {
                if (entry.NotificationSegments is not null)
                    throw Error(LedgerErrors.CorruptStorage, "Notification segments require an event key.");
                continue;
            }
            if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.ExternalEventKey)
                || entry.ExternalEventKey.Length > 512 || entry.ExternalEventKey.Any(char.IsControl)
                || entry.Source is not (ConversationSource.Notification or ConversationSource.ToolResult)
                || entry.Revision < 0 || entry.Revision > Revision(state, entry.Identity)
                || !state.Conversations.Any(x => x.Identity == entry.Identity))
                throw Error(LedgerErrors.CorruptStorage, "Invalid notification event provenance.");
            var messages = state.Outbox.Where(x => x.Identity == entry.Identity && x.Kind == OutboxKind.Notification
                && x.NotificationEventId == entry.Id).OrderBy(x => x.SegmentIndex).ToArray();
            if (entry.NotificationSegments is not { } segments)
            {
                if (messages.Length != 0 || !state.NotificationDrafts.Any(x => x.EventId == entry.Id
                    && x.Identity == entry.Identity && x.Stage != NotificationDraftStage.Finalized))
                    throw Error(LedgerErrors.CorruptStorage, "An unfinished notification must have a captured draft and no outbox.");
                continue;
            }
            if (segments.IsDefaultOrEmpty || segments.Length > 256
                || segments.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1024 * 1024))
                throw Error(LedgerErrors.CorruptStorage, "Invalid notification segment manifest.");
            if (messages.Length != segments.Length || !messages.Select(x => x.SegmentIndex).SequenceEqual(Enumerable.Range(0, segments.Length)))
                throw Error(LedgerErrors.CorruptStorage, "Notification segment manifest does not match its outbox.");
        }
        foreach (var message in state.Outbox)
        {
            if (!Enum.IsDefined(message.Stage) || !Enum.IsDefined(message.Kind) || message.SegmentIndex < 0 || message.SendAttempts < 0)
                throw Error(LedgerErrors.CorruptStorage, "Invalid outbox stage or kind.");
            if (message.Kind == OutboxKind.ConversationReply)
            {
                if (message.GenerationId is null || message.NotificationEventId is not null || message.InputWorkId is not null || message.ReadAtRevision is not null
                    || !state.Generations.Any(x => x.Id == message.GenerationId && x.Identity == message.Identity
                        && x.InputWorkId is null && x.Revision == message.Revision && message.SegmentIndex < x.PreparedSegments.Length
                        && x.PreparedSegments[message.SegmentIndex] == message.Body))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid conversation reply scope.");
            }
            else if (message.Kind == OutboxKind.Notification)
            {
                if (message.GenerationId is not null || message.NotificationEventId is null || message.InputWorkId is not null || message.ReadAtRevision is not null
                    || !notificationEvents.Any(x => x.Id == message.NotificationEventId && x.Identity == message.Identity
                        && x.Revision == message.Revision && x.NotificationSegments is { } segments
                        && message.SegmentIndex < segments.Length && segments[message.SegmentIndex] == message.Body))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid notification outbox scope.");
            }
            else
            {
                if (message.GenerationId is null || message.NotificationEventId is not null || message.InputWorkId is null || message.ReadAtRevision is null
                    || message.ReadAtRevision < message.Revision || message.ReadAtRevision > Revision(state, message.Identity)
                    || !state.Generations.Any(x => x.Id == message.GenerationId && x.Identity == message.Identity
                        && x.InputWorkId == message.InputWorkId && x.Revision == message.Revision
                        && x.ReadAtRevision == message.ReadAtRevision
                        && message.SegmentIndex < x.PreparedSegments.Length && x.PreparedSegments[message.SegmentIndex] == message.Body))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid deferred reply scope.");
            }
        }
        foreach (var generation in state.Generations.Where(x => x.Stage == GenerationStage.HandedToOutbox))
        {
            var messages = state.Outbox.Where(x => x.GenerationId == generation.Id).OrderBy(x => x.SegmentIndex).ToArray();
            if (generation.PreparedSegments.IsDefaultOrEmpty || messages.Length != generation.PreparedSegments.Length
                || !messages.Select(x => x.SegmentIndex).SequenceEqual(Enumerable.Range(0, messages.Length)))
                throw Error(LedgerErrors.CorruptStorage, "Generation reply manifest is incomplete or duplicated.");
        }
    }
}
