using System.Collections.Immutable;

namespace Assistant.Core;

/// <summary>Host-verified public approval identity. This is a notification fact, never an authorization.</summary>
public sealed record RequiredDecisionNotice(string ProviderScope, string DecisionToken,
    Guid ConnectionEpoch, DateTimeOffset ExpiresAt);
public sealed record DecisionNoticeSubscriber(Guid WorkItemId, long WorkVersion);
public sealed record DecisionNotificationResult(NotificationEnqueueResult Notification,
    ImmutableArray<CommitmentRun> Runs, ImmutableArray<Guid> DeferredWorkItemIds);

public sealed partial class DurableConversationLedger
{
    /// <summary>
    /// Host only: atomically ensures the actual required approval notice and shares its receipt with
    /// exact NeedsDecision subscriptions. The notice has no WorkRunId, budget, model or user grant.
    /// Repeated calls must use the original immutable text and segments. An existing plain notification
    /// can be adopted only after the host has reverified the actual pending approval and exact key.
    /// </summary>
    public Task<DecisionNotificationResult> EnsureRequiredDecisionNotificationAsync(ConversationIdentity identity,
        string eventKey, TaskReference task, RequiredDecisionNotice decision, string body,
        IReadOnlyList<string>? segments = null, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateIdentity(identity); ValidateToken(eventKey); ValidateBody(body); ValidateTask(task);
        ValidateRequiredDecision(decision);
        if (string.IsNullOrWhiteSpace(task.TurnId) || decision.ExpiresAt <= _clock.UtcNow)
            throw Error(LedgerErrors.InvalidTransition, "A required decision notice needs a live exact task turn.");
        var parts = segments is null ? ImmutableArray.Create(body) : segments.ToImmutableArray();
        if (parts.IsDefaultOrEmpty || parts.Length > _options.MaximumSegments) throw new ArgumentOutOfRangeException(nameof(segments));
        foreach (var part in parts) ValidateBody(part);
        var entry = state.Events.SingleOrDefault(x => x.Identity == identity && x.ExternalEventKey == eventKey);
        bool duplicate = entry != null, changed = false;
        if (state.Events.Any(x => x.Identity == identity && x.Id != entry?.Id && x.Task == task
            && x.RequiredDecision is { } proof && SameDecision(proof, decision)))
            throw Error(LedgerErrors.Conflict, "This exact decision already has a different notification key.");
        if (entry != null)
        {
            if (entry.Source != ConversationSource.Notification || entry.Task != task || entry.Body != body
                || entry.IsDefaultTaskNotification || entry.NotificationSegments is not { } original || !original.SequenceEqual(parts)
                || entry.RequiredDecision != null && entry.RequiredDecision != decision
                || state.NotificationDrafts.Any(x => x.EventId == entry.Id))
                throw Error(LedgerErrors.Conflict, "The required notice conflicts with the existing notification or its ownership.");
            if (entry.RequiredDecision == null)
            {
                // A manifest is immutable delivery lineage, including uncertain attempts. It cannot
                // retrospectively become an approval presentation or be rewritten to escape coverage.
                if (state.CatchUpBatches.Any(x => x.Members.Any(m => m.SourceEventId == entry.Id)))
                    throw Error(LedgerErrors.Conflict, "A notification already captured in a catch-up manifest cannot be reclassified as an approval.");
                foreach (var window in state.CatchUpWindows.Where(x => x.CandidateEventIds.Contains(entry.Id)).ToArray())
                {
                    Replace(state.CatchUpWindows, window, window with
                    {
                        CandidateEventIds = window.CandidateEventIds.Remove(entry.Id),
                        RequiredDecisionEventIds = window.RequiredDecisionEventIds.Add(entry.Id),
                        DeferredEventCount = Math.Max(0, window.DeferredEventCount - 1)
                    });
                    changed = true;
                }
            }
        }
        else
        {
            long revision = Revision(state, identity);
            if (!state.Conversations.Any(x => x.Identity == identity)) ReplaceConversation(state, identity, revision);
            entry = new(Guid.NewGuid(), identity, ConversationSource.Notification, body, _clock.UtcNow, _clock.UtcNow,
                revision, Task: task, ExternalEventKey: eventKey, NotificationSegments: parts);
            state.Events.Add(entry); NoteNotificationContext(state, entry);
            state.Outbox.AddRange(parts.Select((part, index) => new OutboxMessage(Guid.NewGuid(), identity, null,
                revision, index, part, OutboxStage.Pending, _clock.UtcNow, _clock.UtcNow,
                Kind: OutboxKind.Notification, NotificationEventId: entry.Id)));
            changed = true;
        }
        var subscribers = (entry.DecisionSubscribers ?? []).ToBuilder();
        foreach (var work in state.WorkItems.Where(x => x.Identity == identity && x.Task == task
            && x.Kind == TrackedWorkKind.Subscription && x.Stage == TrackedWorkStage.Active
            && (x.DeadlineUtc == null || x.DeadlineUtc > _clock.UtcNow)
            && state.Subscriptions.Any(s => s.WorkItemId == x.Id && s.Condition == SubscriptionCondition.NeedsDecision)))
        {
            var target = new DecisionNoticeSubscriber(work.Id, work.Version);
            int index = -1;
            for (int i = 0; i < subscribers.Count; i++) if (subscribers[i].WorkItemId == work.Id) { index = i; break; }
            if (index < 0) { subscribers.Add(target); changed = true; }
            // A fresh live host observation may recapture a resumed subscription. Recovery never does.
            else if (subscribers[index] != target) { subscribers[index] = target; changed = true; }
        }
        if (entry.RequiredDecision == null || entry.OutputDependencies == null || changed)
        {
            var updated = entry with { RequiredDecision = decision, DecisionSubscribers = subscribers.ToImmutable(), OutputDependencies = OutputDependencies.Independent };
            PromoteDependencySchema(state, OutputDependencies.Independent);
            Replace(state.Events, entry, updated); entry = updated; changed = true;
        }
        var linked = LinkRequiredDecisionSubscribers(state, entry, out bool linksChanged);
        changed |= linksChanged;
        var output = state.Outbox.Where(x => x.Identity == identity && x.NotificationEventId == entry.Id)
            .OrderBy(x => x.SegmentIndex).ToImmutableArray();
        return new Mutation<DecisionNotificationResult>(new(new(entry, output, duplicate), linked.Runs, linked.Deferred), changed);
    }, cancellationToken);

    private static bool SameDecision(RequiredDecisionNotice first, RequiredDecisionNotice second) =>
        first.ProviderScope == second.ProviderScope && first.DecisionToken == second.DecisionToken
        && first.ConnectionEpoch == second.ConnectionEpoch;

    private static void ValidateRequiredDecision(RequiredDecisionNotice decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ValidateToken(decision.ProviderScope); ValidateToken(decision.DecisionToken);
        if (decision.ConnectionEpoch == Guid.Empty || decision.ExpiresAt == default || decision.ExpiresAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A required decision requires a connection epoch and a UTC expiry.");
    }

    private (ImmutableArray<CommitmentRun> Runs, ImmutableArray<Guid> Deferred) LinkRequiredDecisionSubscribers(
        LedgerState state, ConversationEvent entry, out bool changed)
    {
        changed = false;
        var runs = ImmutableArray.CreateBuilder<CommitmentRun>();
        var deferred = ImmutableArray.CreateBuilder<Guid>();
        string trigger = "decision-notice/" + entry.Id.ToString("N");
        foreach (var subscriber in entry.DecisionSubscribers ?? [])
        {
            var work = RequireCommitment(state, entry.Identity, subscriber.WorkItemId);
            var run = state.CommitmentRuns.SingleOrDefault(x => x.WorkItemId == work.Id && x.SharedDecisionEventId == entry.Id);
            if (run != null) { runs.Add(run); continue; }
            if (work.Version != subscriber.WorkVersion || work.Stage != TrackedWorkStage.Active || work.DeadlineUtc <= _clock.UtcNow) continue;
            // Preserve historical short notices. Only a wholly unstarted group can be replaced.
            run = state.CommitmentRuns.SingleOrDefault(x => x.WorkItemId == work.Id && x.SharedDecisionEventId == null
                && x.TriggerKey == "decision/" + entry.RequiredDecision!.DecisionToken);
            if (run != null)
            {
                var previous = state.Outbox.Where(x => x.NotificationEventId == run.NotificationEventId).ToArray();
                if (run.Stage is CommitmentRunStage.Completed or CommitmentRunStage.Cancelled or CommitmentRunStage.Skipped
                    || previous.Any(x => x.SendAttempts != 0 || x.Stage is not (OutboxStage.Pending or OutboxStage.Superseded))
                    || state.CatchUpBatches.SelectMany(x => x.Members).Where(x => x.SourceEventId == run.NotificationEventId)
                        .Any(x => state.Outbox.Single(o => o.Id == x.CatchUpOutboxId).SendAttempts != 0))
                { runs.Add(run); continue; }
                if (run.NotificationDraftId is { } oldId)
                {
                    var draft = RequireNotificationDraft(state, entry.Identity, oldId);
                    Replace(state.NotificationDrafts, draft, draft with { Cancelled = true,
                        Stage = draft.Stage == NotificationDraftStage.Preparing ? NotificationDraftStage.Captured : draft.Stage,
                        PreparationId = null, UpdatedAt = _clock.UtcNow });
                    foreach (var message in previous.Where(x => x.Stage == OutboxStage.Pending))
                        Replace(state.Outbox, message, message with { Stage = OutboxStage.Superseded, UpdatedAt = _clock.UtcNow });
                }
            }
            else
            {
                if (state.CommitmentRuns.Count >= _options.MaximumCommitmentRuns)
                { deferred.Add(work.Id); continue; }
                run = AddCommitmentRun(state, work, trigger, CommitmentRunStage.NotificationPending);
            }
            var shared = run with { SharedDecisionEventId = entry.Id, Stage = DecisionNoticeAccepted(state, entry.Id)
                ? CommitmentRunStage.Completed : CommitmentRunStage.NotificationPending, WorkVersion = work.Version };
            Replace(state.CommitmentRuns, run, shared); runs.Add(shared); changed = true;
            var subscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
            if (subscription.LastState != CommitmentTaskState.NeedsDecision || subscription.LastDecisionId != entry.RequiredDecision!.DecisionToken)
                Replace(state.Subscriptions, subscription, subscription with { LastState = CommitmentTaskState.NeedsDecision,
                    LastDecisionId = entry.RequiredDecision!.DecisionToken, LastObservedAt = _clock.UtcNow });
        }
        return (runs.ToImmutable(), deferred.ToImmutable());
    }

    private static bool DecisionNoticeAccepted(LedgerState state, Guid eventId)
    {
        var messages = state.Outbox.Where(x => x.NotificationEventId == eventId).ToArray();
        return messages.Length > 0 && messages.All(x => x.Stage == OutboxStage.Accepted);
    }

    private bool RecoverRequiredDecisionLinks(LedgerState state, ConversationIdentity? identity = null)
    {
        bool changed = false;
        foreach (var entry in state.Events.Where(x => x.RequiredDecision != null && (identity == null || x.Identity == identity)).ToArray())
        { LinkRequiredDecisionSubscribers(state, entry, out bool added); changed |= added; }
        return changed;
    }

    private static void NoteSharedDecisionOutcome(LedgerState state, OutboxMessage message)
    {
        if (message.Kind != OutboxKind.Notification || message.Stage != OutboxStage.Accepted
            || !DecisionNoticeAccepted(state, message.NotificationEventId!.Value)) return;
        foreach (var run in state.CommitmentRuns.Where(x => x.SharedDecisionEventId == message.NotificationEventId
            && x.Stage == CommitmentRunStage.NotificationPending).ToArray())
            Replace(state.CommitmentRuns, run, run with { Stage = CommitmentRunStage.Completed });
    }

    private static void ValidateRequiredDecisionNotifications(LedgerState state)
    {
        static void Require(bool condition, string detail)
        { if (!condition) throw Error(LedgerErrors.CorruptStorage, "Invalid required decision notification: " + detail); }
        foreach (var entry in state.Events)
        {
            if (entry.RequiredDecision == null)
            { Require(entry.DecisionSubscribers == null, "subscriber facts have no decision identity."); continue; }
            try { ValidateRequiredDecision(entry.RequiredDecision); }
            catch (ArgumentException error) { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid decision identity.", error); }
            Require(entry.Source == ConversationSource.Notification && entry.ExternalEventKey != null
                && entry.Task?.TurnId != null && !entry.IsDefaultTaskNotification && entry.NotificationSegments != null
                && !state.NotificationDrafts.Any(x => x.EventId == entry.Id)
                && entry.DecisionSubscribers is { IsDefault: false }, "required notice must own only its independent outbox.");
            Require(!state.Events.Any(x => x.Id != entry.Id && x.Identity == entry.Identity && x.Task == entry.Task
                && x.RequiredDecision is { } proof && SameDecision(proof, entry.RequiredDecision)), "duplicate decision identity.");
            var targets = entry.DecisionSubscribers!.Value;
            Require(targets.All(x => x != null), "null subscriber.");
            Require(targets.Select(x => x.WorkItemId).Distinct().Count() == targets.Length, "duplicate subscribers.");
            foreach (var target in targets)
            {
                var work = state.WorkItems.SingleOrDefault(x => x.Id == target.WorkItemId && x.Identity == entry.Identity);
                Require(work != null && work.Task == entry.Task && work.Kind == TrackedWorkKind.Subscription
                    && target.WorkVersion > 0 && target.WorkVersion <= work.Version
                    && state.Subscriptions.Any(x => x.WorkItemId == work.Id && x.Condition == SubscriptionCondition.NeedsDecision),
                    "subscriber target, provenance or version.");
            }
        }
        foreach (var run in state.CommitmentRuns.Where(x => x.SharedDecisionEventId != null))
        {
            var entry = state.Events.SingleOrDefault(x => x.Id == run.SharedDecisionEventId && x.Identity == run.Identity);
            var work = state.WorkItems.Single(x => x.Id == run.WorkItemId);
            Require(entry?.RequiredDecision != null && entry.Task == work.Task
                && entry.DecisionSubscribers!.Value.Any(x => x.WorkItemId == work.Id)
                && run.CheckAttempts == 0 && run.CheckClaimId == null && run.DeliveryAttempts.IsEmpty
                && run.Stage is CommitmentRunStage.NotificationPending or CommitmentRunStage.Completed or CommitmentRunStage.Cancelled
                && (run.TriggerKey == "decision-notice/" + entry.Id.ToString("N")
                    || run.TriggerKey == "decision/" + entry.RequiredDecision.DecisionToken), "shared run scope.");
            Require(state.CommitmentRuns.Count(x => x.WorkItemId == work.Id && x.SharedDecisionEventId == entry!.Id) == 1,
                "duplicate shared run.");
            if (run.Stage == CommitmentRunStage.Completed)
                Require(DecisionNoticeAccepted(state, entry!.Id), "completed shared run has no complete channel acceptance.");
            if (run.NotificationDraftId is { } draftId)
                Require(state.NotificationDrafts.Single(x => x.Id == draftId).Cancelled
                    && state.Outbox.Where(x => x.NotificationEventId == run.NotificationEventId)
                        .All(x => x.Stage == OutboxStage.Superseded && x.SendAttempts == 0), "shared run retains a sendable old short notice.");
        }
    }
}
