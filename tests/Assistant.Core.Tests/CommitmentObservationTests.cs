using Assistant.Core;

internal static partial class CommitmentTests
{
    private static async Task ObservationWatermark(bool reopen)
    {
        await using var f = new F(); var ledger = await f.Open();
        await Subscribe(ledger, f, "failed");
        var first = Observe(f, CommitmentTaskState.Running);
        await ledger.ObserveCommitmentTaskAsync(Identity, first);
        f.Clock.UtcNow += TimeSpan.FromSeconds(20);
        var latest = first with { ObservedAt = f.Clock.UtcNow };
        Equal(0, (await ledger.ObserveCommitmentTaskAsync(Identity, latest)).Length);
        if (reopen) { await ledger.DisposeAsync(); ledger = await f.Open(); }
        var oldFailure = first with { State = CommitmentTaskState.Failed, ObservedAt = first.ObservedAt.AddSeconds(10) };
        Equal(0, (await ledger.ObserveCommitmentTaskAsync(Identity, oldFailure)).Length);
        var saved = await ledger.ReadConversationAsync(Identity);
        Equal(TrackedWorkStage.Active, saved.WorkItems.Single().Stage);
        Equal(0, saved.CommitmentRuns.Length);
        Equal(0, saved.NotificationDrafts.Length);
        Equal(latest.ObservedAt, saved.Subscriptions.Single().LastObservedAt);
        Equal(first.LastActivityAt, saved.Subscriptions.Single().LastActivityAt);

        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        var newFailure = first with { State = CommitmentTaskState.Failed, ObservedAt = f.Clock.UtcNow };
        Equal(1, (await ledger.ObserveCommitmentTaskAsync(Identity, newFailure)).Length);
        Equal(0, (await ledger.ObserveCommitmentTaskAsync(Identity, newFailure)).Length);
        saved = await ledger.ReadConversationAsync(Identity);
        Equal(1, saved.CommitmentRuns.Length);
        Equal(1, saved.NotificationDrafts.Length);
        Equal(Target, saved.NotificationDrafts.Single().Task);
    }

    private static async Task ObservationWatermarkLateCheck()
    {
        await using var f = new F(); var ledger = await f.Open();
        await Subscribe(ledger, f, "noActivity");
        var first = Observe(f, CommitmentTaskState.Running);
        await ledger.ObserveCommitmentTaskAsync(Identity, first);
        f.Clock.UtcNow += TimeSpan.FromSeconds(61);
        var run = (await ledger.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await ledger.BeginCommitmentCheckAsync(Identity, run.Id);
        f.Clock.UtcNow += TimeSpan.FromSeconds(20);
        var latest = first with { ObservedAt = f.Clock.UtcNow };
        await ledger.ObserveCommitmentTaskAsync(Identity, latest);
        var oldFailure = first with { State = CommitmentTaskState.Failed, ObservedAt = latest.ObservedAt.AddSeconds(-10) };
        var result = await ledger.CompleteCommitmentCheckAsync(claim, oldFailure);
        Equal(CommitmentRunStage.PendingCheck, result.Stage);
        var saved = await ledger.ReadConversationAsync(Identity);
        Equal(TrackedWorkStage.Active, saved.WorkItems.Single().Stage);
        Equal(CommitmentTaskState.Running, saved.Subscriptions.Single().LastState);
        Equal(latest.ObservedAt, saved.Subscriptions.Single().LastObservedAt);
        Equal(0, saved.NotificationDrafts.Length);
        Equal(1, saved.CommitmentRuns.Single().CheckAttempts);
        Check(saved.CommitmentRuns.Single().CheckClaimId == null);
    }

    private static async Task ObservationWatermarkInactivity()
    {
        await using var f = new F(); var ledger = await f.Open();
        await Subscribe(ledger, f, "noActivity");
        var first = Observe(f, CommitmentTaskState.Running);
        await ledger.ObserveCommitmentTaskAsync(Identity, first);
        f.Clock.UtcNow += TimeSpan.FromSeconds(50);
        await ledger.ObserveCommitmentTaskAsync(Identity, first with { ObservedAt = f.Clock.UtcNow });
        var saved = await ledger.ReadConversationAsync(Identity);
        Equal(first.LastActivityAt, saved.Subscriptions.Single().LastActivityAt);
        Equal(first.ObservedAt.AddSeconds(60), saved.Subscriptions.Single().NextCheckAt);
        Equal(0, saved.NotificationDrafts.Length);
        f.Clock.UtcNow += TimeSpan.FromSeconds(11);
        var run = (await ledger.AdvanceCommitmentsAsync(Identity)).Single();
        Equal("inactivity/" + first.LastActivityAt!.Value.UtcTicks, run.TriggerKey);
        var claim = await ledger.BeginCommitmentCheckAsync(Identity, run.Id);
        await ledger.CompleteCommitmentCheckAsync(claim, first with { ObservedAt = f.Clock.UtcNow });
        Equal(1, (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Length);
        f.Clock.UtcNow += TimeSpan.FromSeconds(20);
        await ledger.ObserveCommitmentTaskAsync(Identity, first with { ObservedAt = f.Clock.UtcNow });
        Equal(0, (await ledger.AdvanceCommitmentsAsync(Identity)).Length);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).CommitmentRuns.Length);
    }
}
