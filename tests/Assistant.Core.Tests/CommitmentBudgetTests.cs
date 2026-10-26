using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static (string, Func<Task>)[] BudgetCases() =>
    [
        ("shared_budget_read_limit_crosses_workitems_without_consuming_deferred_attempt", ReadBudget),
        ("shared_budget_read_actual_occupancy_survives_pause_until_release", ReadOccupancy),
        ("shared_budget_read_release_is_idempotent_and_same_process_resumable", ReadRelease),
        ("shared_budget_read_restart_releases_occupancy_but_preserves_spend", ReadBudgetRecovery),
        ("shared_budget_old_lease_cannot_change_new_check_result", OldReadRelease),
        ("shared_budget_concurrent_claims_cannot_exceed_two", ConcurrentReadBudget),
        ("shared_budget_notification_burst_crosses_workitems", NotificationBurst),
        ("shared_budget_notification_unknown_counts_after_restart", NotificationBudgetRecovery),
        ("shared_budget_actual_pages_share_one_charge", SharedSegmentBudget),
        ("shared_budget_rejected_retry_spends_again", SharedRejectedBudget),
        ("shared_budget_approval_and_control_receipts_bypass_commitment_budget", ApprovalBudget),
        ("shared_budget_model_preparation_spends_before_read_and_fallback_is_available", ModelBudget),
        ("shared_budget_model_restart_does_not_refund", ModelBudgetRecovery),
        ("shared_budget_status_read_does_not_write_or_include_other_identity", BudgetStatusRead),
        ("shared_budget_backward_clock_does_not_refund", BudgetClockBack),
        ("shared_budget_legacy_missing_collection_reads_empty", BudgetLegacy),
        ("shared_budget_corrupt_associations_fail_closed", BudgetCorruption),
        ("shared_budget_flush_failure_cannot_allow_an_uncharged_send", BudgetFault)
        ,("shared_budget_unknown_is_invalid_transition_even_when_quota_full", UnknownIsNotDeferred)
        ,("shared_budget_terminal_event_keeps_actual_read_occupancy_until_finally", TerminalReadRelease)
        ,("shared_budget_terminal_check_crash_recovers_skipped_without_refund", TerminalReadRecovery)
    ];
    private static LedgerOptions Limits(int reads = 60, int inFlight = 2, int perDay = 20, int perMinute = 5, int models = 20) =>
        new() { CommitmentBudgets = new() { MaximumReadChecksPerHour = reads, MaximumReadChecksInFlight = inFlight,
            MaximumNotificationsPerDay = perDay, MaximumNotificationsPerMinute = perMinute, MaximumModelPreparationsPerDay = models } };
    private static async Task<ImmutableArray<CommitmentRun>> PendingChecks(DurableConversationLedger l, F f, int count)
    {
        for (int i = 0; i < count; i++) await Subscribe(l, f, "noActivity");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        return await l.AdvanceCommitmentsAsync(Identity);
    }
    private static async Task<ImmutableArray<OutboxMessage>> PendingReminders(DurableConversationLedger l, F f, int count,
        string? policy = null)
    {
        for (int i = 0; i < count; i++) await Reminder(l, f, policy);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var outputs = ImmutableArray.CreateBuilder<OutboxMessage>();
        foreach (var d in await l.GetPendingCommitmentNotificationsAsync(Identity))
            outputs.AddRange((await l.FinalizeNotificationFallbackAsync(Identity, d.Id, d.ContextBody)).Outbox);
        return outputs.ToImmutable();
    }
    private static async Task ReadBudget()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(reads: 1));
        var runs = await PendingChecks(l, f, 2);
        var first = await l.BeginCommitmentCheckAsync(Identity, runs[0].Id);
        await l.CompleteCommitmentCheckAsync(first, null, "no evidence");
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginCommitmentCheckAsync(Identity, runs[1].Id));
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Single(x => x.Id == runs[1].Id).CheckAttempts);
        Equal(0, (await l.GetPendingCommitmentChecksAsync(Identity)).Length);
        var status = await l.GetCommitmentBudgetStatusAsync(Identity);
        var window = status.Windows.Single(x => x.Kind == CommitmentBudgetKind.ReadCheck);
        Equal(1, window.Used); Equal(0, status.ReadChecksInFlight);
        Equal(f.Clock.UtcNow.AddHours(1), window.NextAvailableAt!.Value);
        f.Clock.UtcNow += TimeSpan.FromHours(1);
        Equal(1, (await l.BeginCommitmentCheckAsync(Identity, runs[1].Id)).Attempt);
    }
    private static async Task ReadOccupancy()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(inFlight: 1));
        var runs = await PendingChecks(l, f, 2);
        var claim = await l.BeginCommitmentCheckAsync(Identity, runs[0].Id);
        var work = (await l.GetCommitmentsAsync(Identity)).WorkItems.Single(x => x.Id == claim.WorkItemId);
        await Change(l, f, work, "pause");
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginCommitmentCheckAsync(Identity, runs[1].Id));
        Check(await l.ReleaseCommitmentCheckAsync(claim));
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        await l.BeginCommitmentCheckAsync(Identity, runs[1].Id);
        Equal(TrackedWorkStage.Paused, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single(x => x.Id == work.Id).Stage);
    }
    private static async Task ReadRelease()
    {
        await using var f = new F(); var l = await f.Open();
        var run = (await PendingChecks(l, f, 1)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        Check(await l.ReleaseCommitmentCheckAsync(claim));
        Check(!await l.ReleaseCommitmentCheckAsync(claim));
        Equal(CommitmentRunStage.PendingCheck, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().CheckAttempts);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.ReadCheck).Used);
        f.Clock.UtcNow += TimeSpan.FromSeconds(31);
        Equal(2, (await l.BeginCommitmentCheckAsync(Identity, run.Id)).Attempt);
    }
    private static async Task ReadBudgetRecovery()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(reads: 1));
        var run = (await PendingChecks(l, f, 1)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.DisposeAsync(); l = await f.Open(options: Limits(reads: 1));
        var status = await l.GetCommitmentBudgetStatusAsync(Identity);
        Equal(0, status.ReadChecksInFlight);
        Equal(1, status.Windows.Single(x => x.Kind == CommitmentBudgetKind.ReadCheck).Used);
        Check(!await l.ReleaseCommitmentCheckAsync(claim));
        f.Clock.UtcNow += TimeSpan.FromSeconds(31);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginCommitmentCheckAsync(Identity, run.Id));
    }
    private static async Task OldReadRelease()
    {
        await using var f = new F(); var l = await f.Open();
        var run = (await PendingChecks(l, f, 1)).Single();
        var old = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        var work = (await l.GetCommitmentsAsync(Identity)).WorkItems.Single();
        work = await Change(l, f, work, "pause"); await Change(l, f, work, "resume");
        var current = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        Equal(2, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        await Error(LedgerErrors.IdentityMismatch, () => l.ReleaseCommitmentCheckAsync(old with { Attempt = 999 }));
        Check(await l.ReleaseCommitmentCheckAsync(old));
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(current.ClaimId, (await l.GetCommitmentsAsync(Identity)).Runs.Single().CheckClaimId!.Value);
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(old, Observe(f, CommitmentTaskState.Running)));
        await l.CompleteCommitmentCheckAsync(current, null);
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
    }
    private static async Task ConcurrentReadBudget()
    {
        await using var f = new F(); var l = await f.Open();
        var runs = await PendingChecks(l, f, 4);
        var outcomes = await Task.WhenAll(runs.Select(async run =>
        {
            try { await l.BeginCommitmentCheckAsync(Identity, run.Id); return true; }
            catch (LedgerException error) when (error.Code == LedgerErrors.BudgetDeferred) { return false; }
        }));
        Equal(2, outcomes.Count(x => x));
        Equal(2, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Sum(x => x.CheckAttempts));
    }
    private static async Task NotificationBurst()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perMinute: 1));
        var outputs = await PendingReminders(l, f, 2);
        await l.BeginOutboxSendAsync(Identity, outputs[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, outputs[0].Id, OutboxStage.Accepted, "accepted");
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginOutboxSendAsync(Identity, outputs[1].Id));
        Equal(0, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == outputs[1].Id).SendAttempts);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        f.Clock.UtcNow += TimeSpan.FromMinutes(1);
        Equal(1, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        await l.BeginOutboxSendAsync(Identity, outputs[1].Id);
    }
    private static async Task NotificationBudgetRecovery()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1));
        var outputs = await PendingReminders(l, f, 2);
        await l.BeginOutboxSendAsync(Identity, outputs[0].Id);
        await l.DisposeAsync(); l = await f.Open(options: Limits(perDay: 1));
        Equal(OutboxStage.Unknown, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == outputs[0].Id).Stage);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginOutboxSendAsync(Identity, outputs[1].Id));
        f.Clock.UtcNow += TimeSpan.FromDays(1);
        await l.BeginOutboxSendAsync(Identity, outputs[1].Id);
        Equal(1, (await l.GetRecoveryPlanAsync(Identity)).OutboxToReconcile.Length);
    }
    private static async Task SharedSegmentBudget()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1, perMinute: 1));
        await Reminder(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var draft = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        var outputs = (await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, "one two three", ["one", "two", "three"])).Outbox;
        foreach (var output in outputs)
        {
            await l.BeginOutboxSendAsync(Identity, output.Id);
            await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "accepted");
        }
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task SharedRejectedBudget()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1));
        var output = (await PendingReminders(l, f, 1)).Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "definitely rejected");
        await l.RetryRejectedOutboxAsync(Identity, output.Id);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginOutboxSendAsync(Identity, output.Id));
        f.Clock.UtcNow += TimeSpan.FromDays(1);
        await l.BeginOutboxSendAsync(Identity, output.Id);
        Equal(2, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task ApprovalBudget()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1));
        var output = (await PendingReminders(l, f, 1)).Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        var approval = await l.EnqueueNotificationAsync(Identity, "approval-exact", "approval request 123", Target);
        await l.BeginOutboxSendAsync(Identity, approval.Outbox.Single().Id);
        var status = await l.GetCommitmentBudgetStatusAsync(Identity);
        Equal(1, status.Windows.Single(x => x.Kind == CommitmentBudgetKind.Notification && x.Window == TimeSpan.FromDays(1)).Used);
    }
    private static async Task ModelBudget()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(models: 1));
        await Reminder(l, f); await Reminder(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var drafts = await l.GetPendingCommitmentNotificationsAsync(Identity);
        var claim = await l.BeginNotificationPreparationAsync(Identity, drafts[0].Id);
        await l.EndNotificationPreparationAsync(claim);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginNotificationPreparationAsync(Identity, drafts[1].Id));
        Equal(0, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Single(x => x.Id == drafts[1].Id).PreparationAttempts);
        Equal(2, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        await l.FinalizeNotificationFallbackAsync(Identity, drafts[1].Id, drafts[1].ContextBody);
        var ordinary = await l.CaptureNotificationAsync(Identity, "ordinary", "task facts", "{}", Target);
        await l.BeginNotificationPreparationAsync(Identity, ordinary.Draft.Id);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.ModelPreparation).Used);
    }
    private static async Task ModelBudgetRecovery()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(models: 1));
        await Reminder(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var draft = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        await l.BeginNotificationPreparationAsync(Identity, draft.Id);
        await l.DisposeAsync(); l = await f.Open(options: Limits(models: 1));
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginNotificationPreparationAsync(Identity, draft.Id));
        Equal(1, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Single().PreparationAttempts);
        await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, draft.ContextBody);
    }
    private static async Task BudgetStatusRead()
    {
        await using var f = new F(); var l = await f.Open();
        var output = (await PendingReminders(l, f, 1)).Single(); await l.BeginOutboxSendAsync(Identity, output.Id);
        long length = new FileInfo(f.Path).Length;
        var foreign = await l.GetCommitmentBudgetStatusAsync(Identity with { BindingId = "other" });
        Equal(0, foreign.Windows.Sum(x => x.Used));
        Equal(0, foreign.ReadChecksInFlight);
        Equal(length, new FileInfo(f.Path).Length);
    }
    private static async Task BudgetClockBack()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1));
        var outputs = await PendingReminders(l, f, 2); await l.BeginOutboxSendAsync(Identity, outputs[0].Id);
        f.Clock.UtcNow -= TimeSpan.FromDays(1);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.Notification && x.Window == TimeSpan.FromDays(1)).Used);
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task BudgetLegacy()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
        await l.DisposeAsync(); await f.Rewrite(root => root.AsObject().Remove("commitmentBudgetReservations"));
        l = await f.Open();
        Equal(1, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Sum(x => x.Used));
    }
    private static async Task BudgetCorruption()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["commitmentBudgetReservations"] = null,
            root => root["commitmentBudgetReservations"]![0]!["identity"]!["bindingId"] = "other",
            root => root["commitmentBudgetReservations"]![0]!["runId"] = Guid.NewGuid().ToString(),
            root => root["commitmentBudgetReservations"]![0]!["resourceId"] = Guid.NewGuid().ToString(),
            root => root["commitmentBudgetReservations"]![0]!["attempt"] = 99,
            root => root["commitmentBudgetReservations"]![0]!["reservedAt"] = DateTimeOffset.MinValue.ToString("O")
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new F(); var l = await f.Open();
            var output = (await PendingReminders(l, f, 1)).Single(); await l.BeginOutboxSendAsync(Identity, output.Id);
            await l.DisposeAsync(); await f.Rewrite(mutate);
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task BudgetFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault, Limits(perDay: 1));
        var outputs = await PendingReminders(l, f, 2);
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.BeginOutboxSendAsync(Identity, outputs[0].Id));
        await l.DisposeAsync(); l = await f.Open(options: Limits(perDay: 1));
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.Notification && x.Window == TimeSpan.FromDays(1)).Used);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginOutboxSendAsync(Identity, outputs[1].Id));
    }
    private static async Task UnknownIsNotDeferred()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1));
        var output = (await PendingReminders(l, f, 1)).Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Unknown, "unconfirmed");
        await Error(LedgerErrors.InvalidTransition, () => l.BeginOutboxSendAsync(Identity, output.Id));
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
        Equal(OutboxStage.Unknown, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task TerminalReadRelease()
    {
        await using var f = new F(); var l = await f.Open();
        var runs = await PendingChecks(l, f, 2);
        var claim = await l.BeginCommitmentCheckAsync(Identity, runs[0].Id);
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Completed));
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        var snapshot = await l.GetCommitmentsAsync(Identity);
        Equal(CommitmentRunStage.Checking, snapshot.Runs.Single(x => x.Id == claim.RunId).Stage);
        Equal(CommitmentRunStage.Skipped, snapshot.Runs.Single(x => x.Id == runs[1].Id).Stage);
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running)));
        Check(await l.ReleaseCommitmentCheckAsync(claim));
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(CommitmentRunStage.Skipped, (await l.GetCommitmentsAsync(Identity)).Runs.Single(x => x.Id == claim.RunId).Stage);
        Equal(0, (await l.GetPendingCommitmentChecksAsync(Identity)).Length);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.ReadCheck).Used);
    }
    private static async Task TerminalReadRecovery()
    {
        await using var f = new F(); var l = await f.Open();
        var run = (await PendingChecks(l, f, 1)).Single();
        await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Completed));
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).Windows.Single(x => x.Kind == CommitmentBudgetKind.ReadCheck).Used);
        Equal(CommitmentRunStage.Skipped, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
}
