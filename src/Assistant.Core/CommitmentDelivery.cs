using System.Collections.Immutable;
using System.Globalization;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    // Compatibility for the v1 host's reserved public approval key, never inferred from message text.
    private static bool UnclassifiedRequiredDecision(ConversationEvent entry) => entry.RequiredDecision == null
        && entry.ExternalEventKey?.StartsWith("owned-approval/", StringComparison.Ordinal) == true;
    private static bool DefaultNotificationSuppressed(IEnumerable<TrackedWorkItem> workItems, ConversationEvent entry) =>
        entry.IsDefaultTaskNotification && entry.Task != null && workItems.Any(x => x.Identity == entry.Identity
            && x.Kind == TrackedWorkKind.Subscription && x.Task == entry.Task);

    private static void RequireDefaultNotificationAllowed(LedgerState state, ConversationIdentity identity,
        TaskReference? task, bool isDefault)
    {
        if (isDefault && task == null) throw new ArgumentException("A default task notification requires an exact task.");
        if (isDefault && state.WorkItems.Any(x => x.Identity == identity && x.Kind == TrackedWorkKind.Subscription && x.Task == task))
            throw Error(LedgerErrors.NotificationSuppressed, "Explicit subscription policy suppresses the default notification for this exact turn.");
    }
    private static bool WorkAllowsNotification(TrackedWorkItem work, CommitmentRun run, DateTimeOffset now) =>
        work.Version == run.WorkVersion && run.Stage == CommitmentRunStage.NotificationPending
        && work.Stage is TrackedWorkStage.Active or TrackedWorkStage.Triggered or TrackedWorkStage.NeedsDecision
        && (work.DeadlineUtc == null || work.DeadlineUtc > now) && run.NotBefore <= now;

    private static bool InCommitmentQuietHours(CommitmentPolicy policy, DateTimeOffset now)
    {
        if (policy.QuietStart == null) return false;
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId!)).DateTime);
        var start = TimeOnly.ParseExact(policy.QuietStart, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(policy.QuietEnd!, "HH:mm", CultureInfo.InvariantCulture);
        return start < end ? local >= start && local < end : local >= start || local < end;
    }
    private static bool CommitmentPreparationReady(ConversationSnapshot snapshot, DurableNotificationDraft draft, DateTimeOffset now) =>
        PreparationReady(snapshot.WorkItems, snapshot.CommitmentRuns, snapshot.Events, draft, now);

    private static bool PreparationReady(IEnumerable<TrackedWorkItem> works, IEnumerable<CommitmentRun> runs,
        IEnumerable<ConversationEvent> events, DurableNotificationDraft draft, DateTimeOffset now)
    {
        if (draft.Cancelled) return false;
        var entry = events.Single(x => x.Id == draft.EventId);
        if (DefaultNotificationSuppressed(works, entry)) return false;
        if (draft.WorkRunId == null) return true;
        var run = runs.Single(x => x.Id == draft.WorkRunId);
        var work = works.Single(x => x.Id == run.WorkItemId);
        return WorkAllowsNotification(work, run, now) && !InCommitmentQuietHours(work.Policy, now);
    }
    private bool CommitmentDeliveryReady(ConversationSnapshot snapshot, OutboxMessage message, DateTimeOffset now) =>
        MessagePolicyReady(snapshot.WorkItems, snapshot.CommitmentRuns, snapshot.NotificationDrafts, snapshot.Events, message, now)
        && DependentNotificationReady(snapshot.DependentWork?.WorkItems ?? [], snapshot.Events, snapshot.CommitmentBudgetReservations, message, now)
        && SharedCommitmentDeliveryReady(snapshot, message, now)
        && (snapshot.CatchUpWindows.IsDefaultOrEmpty || CatchUpDeliveryReady(snapshot, message, now));

    private bool SharedCommitmentDeliveryReady(ConversationSnapshot snapshot, OutboxMessage message, DateTimeOffset now)
    {
        if (message.Kind != OutboxKind.Notification || message.SegmentIndex > 0 && message.SendAttempts == 0
            || !snapshot.NotificationDrafts.Any(x => x.EventId == message.NotificationEventId && x.WorkRunId != null)
                && !snapshot.Events.Any(e => e.Id == message.NotificationEventId && e.DependentWorkNotice != null)) return true;
        return BudgetAvailable(message.Identity, snapshot.CommitmentBudgetReservations, CommitmentBudgetKind.Notification, now);
    }

    private static bool MessagePolicyReady(IEnumerable<TrackedWorkItem> works, IEnumerable<CommitmentRun> runs,
        IEnumerable<DurableNotificationDraft> drafts, IEnumerable<ConversationEvent> events,
        OutboxMessage message, DateTimeOffset now)
    {
        if (message.Kind != OutboxKind.Notification) return true;
        var entry = events.Single(x => x.Id == message.NotificationEventId);
        if (UnclassifiedRequiredDecision(entry)) return false;
        if (entry.RequiredDecisionSuppressedAt != null) return false;
        if (entry.RequiredDecision is { } decision && decision.ExpiresAt <= now) return false;
        if (DefaultNotificationSuppressed(works, entry)) return false;
        var draft = drafts.SingleOrDefault(x => x.EventId == entry.Id);
        if (draft == null) return true;
        if (draft.Cancelled) return false;
        if (draft.WorkRunId == null) return true;
        var run = runs.Single(x => x.Id == draft.WorkRunId);
        var work = works.Single(x => x.Id == run.WorkItemId);
        if (!WorkAllowsNotification(work, run, now) || InCommitmentQuietHours(work.Policy, now)) return false;
        // Later, never-attempted segments belong to the already reserved notification.
        if (message.SegmentIndex > 0 && message.SendAttempts == 0) return true;
        var ownAttempts = run.DeliveryAttempts.IsDefault ? [] : run.DeliveryAttempts;
        if (ownAttempts.Length >= work.Policy.MaximumDeliveryAttempts) return false;
        var attempts = runs.Where(x => x.WorkItemId == work.Id).SelectMany(x =>
            x.DeliveryAttempts.IsDefault ? ImmutableArray<DateTimeOffset>.Empty : x.DeliveryAttempts).ToArray();
        return attempts.Count(x => x > now.AddHours(-24)) < work.Policy.MaximumNotificationsPer24Hours
            && !attempts.Any(x => x.AddSeconds(work.Policy.MinimumIntervalSeconds) > now);
    }
    private static void RequireCommitmentMessagePolicy(LedgerState state, OutboxMessage message, DateTimeOffset now)
    {
        if (!MessagePolicyReady(state.WorkItems, state.CommitmentRuns, state.NotificationDrafts, state.Events, message, now)
            || !DependentNotificationReady(state.DependentWork, state.Events, state.CommitmentBudgetReservations, message, now))
            throw Error(LedgerErrors.NotificationSuppressed, "The current durable notification policy does not permit this send.");
    }
    private void ReserveCommitmentDelivery(LedgerState state, OutboxMessage message)
    {
        RequireCommitmentMessagePolicy(state, message, _clock.UtcNow);
        if (message.Kind != OutboxKind.Notification) return;
        if (ReserveDependentNotification(state, message)) return;
        if (ReserveCatchUpDelivery(state, message)) return;
        var draft = state.NotificationDrafts.SingleOrDefault(x => x.EventId == message.NotificationEventId);
        if (draft?.WorkRunId is not { } id || message.SegmentIndex > 0 && message.SendAttempts == 0) return;
        var run = RequireCommitmentRun(state, message.Identity, id);
        var work = RequireCommitment(state, message.Identity, run.WorkItemId);
        ReserveCommitmentBudget(state, work, run, CommitmentBudgetKind.Notification, message.Id, message.SendAttempts + 1);
        Replace(state.CommitmentRuns, run, run with
        { DeliveryAttempts = (run.DeliveryAttempts.IsDefault ? [] : run.DeliveryAttempts).Add(_clock.UtcNow) });
    }
    private void NoteCommitmentDeliveryOutcome(LedgerState state, OutboxMessage message)
    {
        if (message.Kind != OutboxKind.Notification) return;
        NoteSharedDecisionOutcome(state, message);
        var draft = state.NotificationDrafts.SingleOrDefault(x => x.EventId == message.NotificationEventId);
        if (draft?.WorkRunId is not { } id) return;
        var run = RequireCommitmentRun(state, message.Identity, id);
        if (run.Stage == CommitmentRunStage.Cancelled || message.Stage != OutboxStage.Accepted
            || !state.Outbox.Where(x => x.NotificationEventId == message.NotificationEventId).All(x => x.Stage == OutboxStage.Accepted)) return;
        Replace(state.CommitmentRuns, run, run with { Stage = CommitmentRunStage.Completed });
        var work = RequireCommitment(state, message.Identity, run.WorkItemId);
        if (work.Stage == TrackedWorkStage.Triggered)
            Replace(state.WorkItems, work, work with { Stage = TrackedWorkStage.Completed, UpdatedAt = _clock.UtcNow });
    }
}
