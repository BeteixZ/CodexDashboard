using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly TaskReference Target = new("local", "thread-commitment", "turn-one", "profile");
    private static readonly (string, Func<Task>)[] Cases =
    [
        ("reminder_current_grant_local_mutation_and_restart_proof", CreationRecovery),
        ("same_operation_is_idempotent_even_after_new_input", Duplicate),
        ("stale_source_and_forged_invocation_denied", Authority),
        ("deferred_input_cannot_create_commitment", Deferred),
        ("reminder_text_cannot_be_invented", TextScope),
        ("missing_and_ambiguous_local_time_rejected", Dst),
        ("reminder_trigger_draft_outbox_atomic_and_once", Trigger),
        ("new_inbox_does_not_invalidate_reminder", RevisionIndependent),
        ("sending_restart_unknown_is_not_replayed", SendingRecovery),
        ("pause_blocks_prepared_outbox_resume_restores", PauseResume),
        ("cancel_does_not_revive_rejected_or_unknown", Cancel),
        ("quiet_hours_rechecked_at_send_and_cross_midnight", Quiet),
        ("notification_segments_share_one_reservation", Segments),
        ("rejected_retry_uses_persisted_delivery_budget", DeliveryBudget),
        ("late_timeout_preserves_proven_local_completion", LateTimeout),
        ("misfire_skip_and_needs_decision_are_persistent", Misfire),
        ("failure_only_ignores_success_and_other_turn", FailureOnly),
        ("repeated_failure_has_one_run_and_draft", DuplicateObservation),
        ("stale_offline_unknown_observations_do_not_trigger", BadObservations),
        ("exact_repeated_observation_does_not_write", UnchangedObservation),
        ("newer_unchanged_fact_rejects_older_terminal", () => ObservationWatermark(false)),
        ("newer_unchanged_fact_watermark_survives_reopen", () => ObservationWatermark(true)),
        ("newer_observation_rejects_late_read_result", ObservationWatermarkLateCheck),
        ("observation_watermark_does_not_reset_inactivity", ObservationWatermarkInactivity),
        ("inactivity_requires_fresh_read_claim", NoActivity),
        ("inactivity_read_success_does_not_invent_activity", MissingActivity),
        ("inactivity_offline_retries_bounded_then_needs_decision", CheckBudget),
        ("checking_restart_keeps_attempt_and_rejects_old_claim", CheckRecovery),
        ("pause_invalidates_late_read_and_model_preparation", LateClaims),
        ("default_notification_capture_and_send_policy_atomic", DefaultPolicy),
        ("cancelled_subscription_keeps_default_suppression_exact_turn", DefaultScope),
        ("deadline_blocks_unsent_notification", Deadline),
        ("capacity_keeps_original_work_and_does_not_consume_trigger", Capacity),
        ("v1_missing_commitment_fields_remains_readable", Legacy),
        ("corrupt_links_null_collections_and_proofs_fail_closed", Corruption),
        ("local_commit_flush_failure_restores_proven_result", CommitFault),
        ("trigger_flush_failure_restores_one_draft", TriggerFault),
        ("backward_clock_does_not_duplicate_trigger", ClockBack)
        ,("paused_inflight_acceptance_resume_is_completed", PausedAcceptance)
        ,("new_activity_anchor_skips_old_check_before_next_run", ChangedActivity)
        ,("lowered_check_budget_does_not_leave_unclaimable_work", LoweredBudget)
        ,("frequency_budget_counts_uncertain_delivery_across_restart", Frequency)
        ,("blank_reminder_update_rejected_without_corrupting_history", BlankUpdate)
        ,("needs_decision_deadline_expires_unsent_draft", DecisionDeadline)
        ,("precommit_fault_cannot_create_partial_work", BeforeCommitFault)
        ,("decision_subscription_survives_pause_resume_pending_delivery", DecisionResume)
        ,("inactivity_subscription_survives_pause_resume_pending_delivery", InactivityResume)
        ,("coarse_waiting_fact_preserves_known_decision_and_new_token", DecisionIdentity)
        ,.. BudgetCases()
        ,.. RequiredDecisionCases()
        ,.. WakeCases()
        ,.. CatchUpCases()
    ];
    internal static async Task<int> Run()
    {
        int failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS commitments/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL commitments/" + name + ": " + error); }
        }
        Console.WriteLine($"COMMITMENTS TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }
    private static async Task CreationRecovery()
    {
        await using var f = new F();
        var ledger = await f.Open();
        var context = await Begin(ledger, f.Clock, "schedule", ReminderArgs(f.Clock), "明天提醒我喝水");
        var result = await ledger.ApplyCommitmentToolAsync(context);
        Equal(ToolExecutionState.Completed, result.State);
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(OperationStage.Completed, snapshot.Operations.Single().Stage);
        Equal(ToolInvocationStage.Executing, snapshot.ToolInvocations.Single().Stage);
        Equal(0, snapshot.Outbox.Length);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(ToolInvocationStage.Completed, snapshot.ToolInvocations.Single().Stage);
        Equal(OperationStage.Completed, snapshot.Operations.Single().Stage);
        Equal(1, snapshot.WorkItems.Length);
        Check(snapshot.Events.Any(x => x.Source == ConversationSource.ToolResult));
    }
    private static async Task Duplicate()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        var first = await l.ApplyCommitmentToolAsync(context);
        await l.ReceiveAsync(Identity, "new", "现在查状态", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        var again = await l.ApplyCommitmentToolAsync(context);
        Equal(first.Facts.GetProperty("workItemId").GetGuid(), again.Facts.GetProperty("workItemId").GetGuid());
        Equal(1, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyCommitmentToolAsync(context with { SourceMessageId = "new" }));
    }
    private static async Task Authority()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        await Error(LedgerErrors.NotFound, () => l.ApplyCommitmentToolAsync(context with { InvocationId = Guid.NewGuid() }));
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyCommitmentToolAsync(context with { IdempotencyKey = "invented" }));
        await l.ReceiveAsync(Identity, "new", "不要提醒", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await Error(LedgerErrors.StaleRevision, () => l.ApplyCommitmentToolAsync(context));
        Equal(0, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
    }
    private static async Task Deferred()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyCommitmentToolAsync(context with { InputWorkId = Guid.NewGuid() }));
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyCommitmentToolAsync(context with { ReadAtRevision = context.Revision }));
    }
    private static async Task TextScope()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我看窗外");
        Equal(ToolExecutionState.Rejected, (await l.ApplyCommitmentToolAsync(context)).State);
        Equal(0, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
    }
    private static Task Dst()
    {
        foreach (var local in new[] { "2026-03-08T02:30:00", "2026-11-01T01:30:00" })
        {
            try { CommitmentTools.ResolveLocalTime(local, "America/New_York"); throw new Exception("Expected invalid time."); }
            catch (ArgumentException) { }
        }
        Equal(new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero),
            CommitmentTools.ResolveLocalTime("2026-10-21T09:00:00", "Asia/Taipei"));
        return Task.CompletedTask;
    }
    private static async Task Trigger()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        Equal(CommitmentRunStage.NotificationPending, run.Stage);
        Equal(0, (await l.AdvanceCommitmentsAsync(Identity)).Length);
        var d = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        Equal(run.Id, d.WorkRunId!.Value);
        Equal(ConversationSource.Notification, (await l.ReadConversationAsync(Identity)).Events.Single(x => x.Id == d.EventId).Source);
        await ledgerRestart();
        async Task ledgerRestart()
        {
            await l.DisposeAsync(); l = await f.Open();
            Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
            var output = await l.FinalizeNotificationFallbackAsync(Identity, d.Id, d.ContextBody);
            Equal(1, output.Outbox.Length);
            Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
            await l.BeginOutboxSendAsync(Identity, output.Outbox[0].Id);
            await l.RecordOutboxOutcomeAsync(Identity, output.Outbox[0].Id, OutboxStage.Accepted, "channel accepted");
            Equal(TrackedWorkStage.Completed, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        }
    }
    private static async Task RevisionIndependent()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        await l.ReceiveAsync(Identity, "new", "查任务", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        var output = await Finalize(l); await l.BeginOutboxSendAsync(Identity, output.Id);
        Equal(OutboxStage.Sending, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task SendingRecovery()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var output = await Finalize(l); await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.GetRecoveryPlanAsync(Identity)).OutboxToReconcile.Length);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
    }
    private static async Task PauseResume()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        work = await Change(l, f, work, "pause");
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, output.Id));
        await l.DisposeAsync(); l = await f.Open();
        work = await Change(l, f, work, "resume");
        Equal(1, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        await l.BeginOutboxSendAsync(Identity, output.Id);
    }
    private static async Task Cancel()
    {
        foreach (var outcome in new[] { OutboxStage.Rejected, OutboxStage.Unknown })
        {
            await using var f = new F(); var l = await f.Open(); var work = await Reminder(l, f);
            f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
            await l.BeginOutboxSendAsync(Identity, output.Id); await l.RecordOutboxOutcomeAsync(Identity, output.Id, outcome, "actual receipt");
            await Change(l, f, work, "cancel");
            Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
            Equal(outcome == OutboxStage.Unknown ? OutboxStage.Unknown : OutboxStage.Superseded,
                (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
            await l.DisposeAsync(); l = await f.Open();
            Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        }
    }
    private static async Task Quiet()
    {
        await using var f = new F(); var l = await f.Open();
        await Reminder(l, f, """{"quietStart":"18:03","quietEnd":"06:00","timeZoneId":"UTC"}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, output.Id));
        f.Clock.UtcNow = new DateTimeOffset(f.Clock.UtcNow.UtcDateTime.Date.AddDays(1).AddHours(5), TimeSpan.Zero);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        f.Clock.UtcNow += TimeSpan.FromHours(1);
        await l.BeginOutboxSendAsync(Identity, output.Id);
    }
    private static async Task Segments()
    {
        await using var f = new F(); var l = await f.Open();
        await Reminder(l, f, """{"maximumNotificationsPer24Hours":1,"minimumIntervalSeconds":3600,"maximumDeliveryAttempts":1}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        var d = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        var outputs = (await l.FinalizeNotificationFallbackAsync(Identity, d.Id, "one two", ["one", "two"])).Outbox;
        foreach (var output in outputs)
        {
            await l.BeginOutboxSendAsync(Identity, output.Id);
            await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "accepted");
        }
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
    private static async Task DeliveryBudget()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f, """{"maximumDeliveryAttempts":2}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        for (int i = 0; i < 2; i++)
        {
            await l.BeginOutboxSendAsync(Identity, output.Id);
            await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "definite rejection");
            if (i == 0) await l.RetryRejectedNotificationsAsync(Identity);
        }
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
    }
    private static async Task LateTimeout()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        await l.ApplyCommitmentToolAsync(context);
        var result = await l.FinishToolInvocationAsync(Identity, context.InvocationId, ToolInvocationStage.Unknown,
            """{"status":"unknown","reason":"late timeout"}""", OperationStage.Unknown);
        Equal(ToolInvocationStage.Completed, result.Stage);
        Check(result.ResultJson!.Contains("workItemId", StringComparison.Ordinal));
        Equal(result.ResultJson, (await l.ReadConversationAsync(Identity)).Events.Last(x => x.Source == ConversationSource.ToolResult).Body);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task Misfire()
    {
        foreach (var policy in new[] { "skip", "needsDecision", "notifyOnce" })
        {
            await using var f = new F(); var l = await f.Open();
            await Execute(l, f, "schedule", ReminderArgs(f.Clock, missed: policy), "提醒我喝水");
            f.Clock.UtcNow += TimeSpan.FromDays(1); await l.AdvanceCommitmentsAsync(Identity);
            var s = await l.GetCommitmentsAsync(Identity);
            Equal(policy == "skip" ? TrackedWorkStage.Expired : policy == "needsDecision" ? TrackedWorkStage.NeedsDecision : TrackedWorkStage.Triggered, s.WorkItems.Single().Stage);
            Equal(policy == "skip" ? 0 : 1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
            await l.DisposeAsync(); l = await f.Open(); Equal(0, (await l.AdvanceCommitmentsAsync(Identity)).Length);
        }
    }
    private static async Task FailureOnly()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Failed) with { Task = Target with { TurnId = "other" } })).Length);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Completed))).Length);
        Equal(TrackedWorkStage.Completed, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task DuplicateObservation()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        for (int i = 0; i < 10; i++) await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Failed));
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        await l.DisposeAsync(); l = await f.Open();
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Failed));
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task BadObservations()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var observation = Observe(f, CommitmentTaskState.Failed);
        foreach (var bad in new[] { observation with { Fresh = false }, observation with { Connected = false },
            observation with { ObservedAt = f.Clock.UtcNow.AddDays(-1) }, observation with { State = CommitmentTaskState.Unknown },
            observation with { Task = Target with { ProfileId = "wrong" } } })
            Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, bad)).Length);
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task UnchangedObservation()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var fact = Observe(f, CommitmentTaskState.Running);
        await l.ObserveCommitmentTaskAsync(Identity, fact); long length = new FileInfo(f.Path).Length;
        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        // The observation itself is identical; time passing at the consumer does
        // not make the source newer or justify another durable frame.
        await l.ObserveCommitmentTaskAsync(Identity, fact);
        Equal(length, new FileInfo(f.Path).Length);
        Equal(0, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Length);
        var newer = fact with { ObservedAt = f.Clock.UtcNow };
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, newer)).Length);
        var saved = await l.ReadConversationAsync(Identity);
        Equal(newer.ObservedAt, saved.Subscriptions.Single().LastObservedAt);
        Equal(fact.LastActivityAt, saved.Subscriptions.Single().LastActivityAt);
        Equal(0, saved.CommitmentRuns.Length);
        Equal(0, saved.NotificationDrafts.Length);
    }
    private static async Task NoActivity()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var activity = f.Clock.UtcNow;
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Running));
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        Equal(CommitmentRunStage.PendingCheck, run.Stage); Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        var complete = await l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running) with { LastActivityAt = activity });
        Equal(CommitmentRunStage.NotificationPending, complete.Stage);
        Check(complete.Body!.Contains("不能判断", StringComparison.Ordinal));
        Equal(0, (await l.AdvanceCommitmentsAsync(Identity)).Length);
    }
    private static async Task MissingActivity()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var baseline = (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single().LastActivityAt;
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running) with { LastActivityAt = null });
        Equal(baseline, (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single().LastActivityAt);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task CheckBudget()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity", """{"maximumCheckAttempts":2}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        for (int i = 0; i < 2; i++)
        {
            var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
            run = await l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running) with { Connected = false }, "offline");
            f.Clock.UtcNow += TimeSpan.FromSeconds(31);
        }
        Equal(TrackedWorkStage.NeedsDecision, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        Equal(2, run.CheckAttempts);
        Equal(0, (await l.GetPendingCommitmentChecksAsync(Identity)).Length);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task CheckRecovery()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().CheckAttempts);
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running)));
        f.Clock.UtcNow += TimeSpan.FromSeconds(31);
        Equal(2, (await l.BeginCommitmentCheckAsync(Identity, run.Id)).Attempt);
    }
    private static async Task LateClaims()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "noActivity");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await Change(l, f, work, "pause");
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running)));
        var reminder = await Reminder(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await l.AdvanceCommitmentsAsync(Identity); var d = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        var prep = await l.BeginNotificationPreparationAsync(Identity, d.Id);
        await Change(l, f, reminder, "pause");
        await Error(LedgerErrors.InvalidTransition, () => l.FinalizeNotificationAsync(prep, "late model"));
    }
    private static async Task DefaultPolicy()
    {
        await using var f = new F(); var l = await f.Open();
        var before = await l.EnqueueNotificationAsync(Identity, "default-before", "completed", Target, isDefaultTaskNotification: true);
        await Subscribe(l, f, "failed");
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, before.Outbox[0].Id));
        await Error(LedgerErrors.NotificationSuppressed, () => l.CaptureNotificationAsync(Identity, "after", "facts", "{}", Target, isDefaultTaskNotification: true));
        await Error(LedgerErrors.NotificationSuppressed, () => l.EnqueueNotificationAsync(Identity, "after", "facts", Target, isDefaultTaskNotification: true));
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        var important = await l.EnqueueNotificationAsync(Identity, "approval", "Need your approval", Target);
        await l.BeginOutboxSendAsync(Identity, important.Outbox[0].Id);
    }
    private static async Task DefaultScope()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "failed");
        await Change(l, f, work, "cancel");
        Check(!await l.ShouldNotifyDefaultTaskAsync(Identity, Target));
        Check(await l.ShouldNotifyDefaultTaskAsync(Identity, Target with { TurnId = "next" }));
        Check(await l.ShouldNotifyDefaultTaskAsync(Identity with { BindingId = "other" }, Target));
    }
    private static async Task Deadline()
    {
        await using var f = new F(); var l = await f.Open();
        var args = JsonNode.Parse(ReminderArgs(f.Clock))!;
        args["deadlineUtc"] = f.Clock.UtcNow.AddMinutes(3).ToString("O");
        await Execute(l, f, "schedule", args.ToJsonString(), "提醒我喝水");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, output.Id));
        await l.AdvanceCommitmentsAsync(Identity);
        Equal(TrackedWorkStage.Expired, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task Capacity()
    {
        await using var f = new F(); var l = await f.Open(options: new() { MaximumActiveCommitments = 1, MaximumPendingNotificationDrafts = 1 });
        await Reminder(l, f);
        await Error(LedgerErrors.CapacityExceeded, () => Reminder(l, f));
        Equal(1, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
        await l.CaptureNotificationAsync(Identity, "occupied", "facts", "{}", null);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await Error(LedgerErrors.CapacityExceeded, () => l.AdvanceCommitmentsAsync(Identity));
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        Check((await l.GetCommitmentsAsync(Identity)).Schedules.Single().FiredRunId == null);
    }
    private static async Task Legacy()
    {
        await using var f = new F(); var l = await f.Open();
        await l.ReceiveAsync(Identity, "old", "old text", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await l.DisposeAsync();
        await f.Rewrite(root => { foreach (var key in new[] { "workItems", "schedules", "subscriptions", "commitmentRuns", "commitmentMutations" }) root.AsObject().Remove(key); });
        l = await f.Open(); Equal(1, (await l.ReadConversationAsync(Identity)).Inbox.Length);
        Equal(0, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
    }
    private static async Task Corruption()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["workItems"] = null,
            root => root["schedules"]![0]!["workItemId"] = Guid.NewGuid().ToString(),
            root => root["commitmentMutations"]![0]!["invocationId"] = Guid.NewGuid().ToString(),
            root => root["workItems"]![0]!["identity"]!["bindingId"] = "wrong",
            root => root["notificationDrafts"]![0]!["workRunId"] = Guid.NewGuid().ToString(),
            root => root["commitmentRuns"]![0]!["notificationEventId"] = Guid.NewGuid().ToString()
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
            f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
            await l.DisposeAsync(); await f.Rewrite(mutate);
            var before = await File.ReadAllBytesAsync(f.Path);
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
            var after = await File.ReadAllBytesAsync(f.Path);
            Check(before.SequenceEqual(after));
        }
    }
    private static async Task CommitFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.ApplyCommitmentToolAsync(context));
        await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
        Equal(ToolInvocationStage.Completed, (await l.ReadConversationAsync(Identity)).ToolInvocations.Single().Stage);
    }
    private static async Task TriggerFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
        await Reminder(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.AdvanceCommitmentsAsync(Identity));
        await l.DisposeAsync(); l = await f.Open(); await l.AdvanceCommitmentsAsync(Identity);
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task ClockBack()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity);
        f.Clock.UtcNow -= TimeSpan.FromDays(1); await l.AdvanceCommitmentsAsync(Identity);
        f.Clock.UtcNow += TimeSpan.FromDays(2); await l.AdvanceCommitmentsAsync(Identity);
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task PausedAcceptance()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        await l.BeginOutboxSendAsync(Identity, output.Id);
        work = await Change(l, f, work, "pause");
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "late accepted");
        work = await Change(l, f, work, "resume");
        Equal(TrackedWorkStage.Completed, work.Stage);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task ChangedActivity()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var newActivity = f.Clock.UtcNow.AddSeconds(30);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        Equal(CommitmentRunStage.Skipped, (await l.CompleteCommitmentCheckAsync(claim,
            Observe(f, CommitmentTaskState.Running) with { LastActivityAt = newActivity })).Stage);
        Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        var next = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        Check(next.TriggerKey != run.TriggerKey);
        var nextClaim = await l.BeginCommitmentCheckAsync(Identity, next.Id);
        await l.CompleteCommitmentCheckAsync(nextClaim, Observe(f, CommitmentTaskState.Running) with { LastActivityAt = newActivity });
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        Equal(0, (await l.AdvanceCommitmentsAsync(Identity)).Length);
    }
    private static async Task LoweredBudget()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "noActivity");
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.CompleteCommitmentCheckAsync(claim, null, "offline");
        await Execute(l, f, "change_commitment", JsonSerializer.Serialize(new
        { workItemId = work.Id, expectedVersion = work.Version, action = "update", policy = new { maximumCheckAttempts = 1 } }),
            "只检查一次", Target);
        await l.AdvanceCommitmentsAsync(Identity);
        Equal(TrackedWorkStage.NeedsDecision, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task Frequency()
    {
        await using var f = new F(); var l = await f.Open();
        await Subscribe(l, f, "needsDecision", """{"maximumNotificationsPer24Hours":1,"minimumIntervalSeconds":60}""");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "one" });
        var first = await Finalize(l); await l.BeginOutboxSendAsync(Identity, first.Id);
        await l.RecordOutboxOutcomeAsync(Identity, first.Id, OutboxStage.Unknown, "lost receipt");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "two" });
        var second = await Finalize(l);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, second.Id));
        await l.DisposeAsync(); l = await f.Open();
        f.Clock.UtcNow += TimeSpan.FromHours(23);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        f.Clock.UtcNow += TimeSpan.FromHours(2);
        await l.BeginOutboxSendAsync(Identity, second.Id);
        Equal(OutboxStage.Unknown, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == first.Id).Stage);
    }
    private static async Task BlankUpdate()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Reminder(l, f);
        var context = await Begin(l, f.Clock, "change_commitment", JsonSerializer.Serialize(new
        { workItemId = work.Id, expectedVersion = work.Version, action = "update", text = " " }), "修改提醒为 ");
        Equal(ToolExecutionState.Rejected, (await l.ApplyCommitmentToolAsync(context)).State);
        Equal(work.Goal, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Goal);
        await l.DisposeAsync(); l = await f.Open();
        Equal(work.Goal, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Goal);
    }
    private static async Task DecisionDeadline()
    {
        await using var f = new F(); var l = await f.Open();
        var arguments = JsonNode.Parse(ReminderArgs(f.Clock, missed: "needsDecision"))!;
        arguments["deadlineUtc"] = f.Clock.UtcNow.AddMinutes(10).ToString("O");
        await Execute(l, f, "schedule", arguments.ToJsonString(), "提醒我喝水");
        f.Clock.UtcNow += TimeSpan.FromMinutes(5); await l.AdvanceCommitmentsAsync(Identity);
        Equal(TrackedWorkStage.NeedsDecision, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        f.Clock.UtcNow += TimeSpan.FromMinutes(10); await l.AdvanceCommitmentsAsync(Identity);
        Equal(TrackedWorkStage.Expired, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task BeforeCommitFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
        var context = await Begin(l, f.Clock, "schedule", ReminderArgs(f.Clock), "提醒我喝水");
        fault.Stage = JournalWriteStage.BeforeWrite;
        await Throws<InjectedFailure>(() => l.ApplyCommitmentToolAsync(context));
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.GetCommitmentsAsync(Identity)).WorkItems.Length);
        Equal(OperationStage.Unknown, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage);
    }
    private static async Task DecisionResume()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "needsDecision");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "first" });
        var output = await Finalize(l);
        work = await Change(l, f, work, "pause");
        work = await Change(l, f, work, "resume");
        Equal(TrackedWorkStage.Active, work.Stage);
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "accepted");
        Equal(TrackedWorkStage.Active, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        await l.DisposeAsync(); l = await f.Open();
        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "second" })).Length);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task InactivityResume()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "noActivity");
        var initial = f.Clock.UtcNow;
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        await l.CompleteCommitmentCheckAsync(claim, Observe(f, CommitmentTaskState.Running) with { LastActivityAt = initial });
        var output = await Finalize(l);
        work = await Change(l, f, work, "pause");
        work = await Change(l, f, work, "resume");
        Equal(TrackedWorkStage.Active, work.Stage);
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "accepted");
        Equal(TrackedWorkStage.Active, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Running));
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        Equal(1, (await l.AdvanceCommitmentsAsync(Identity)).Length);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task DecisionIdentity()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "approval-one" });
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision))).Length);
        Equal("approval-one", (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single().LastDecisionId);
        await l.DisposeAsync(); l = await f.Open();
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "approval-two" })).Length);
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision))).Length);
        Equal("approval-two", (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single().LastDecisionId);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static string ReminderArgs(Clock clock, string? policy = null, string missed = "notifyOnce") =>
        $$"""{"text":"喝水","localTime":"{{clock.UtcNow.AddMinutes(2).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}}","timeZoneId":"UTC","missedPolicy":"{{missed}}"{{(policy == null ? "" : ",\"policy\":" + policy)}}}""";
    private static Task<TrackedWorkItem> Reminder(DurableConversationLedger ledger, F f, string? policy = null) =>
        Execute(ledger, f, "schedule", ReminderArgs(f.Clock, policy), "提醒我喝水");
    private static Task<TrackedWorkItem> Subscribe(DurableConversationLedger l, F f, string condition, string? policy = null) =>
        Execute(l, f, "subscribe", $$"""{"hostId":"local","threadId":"thread-commitment","turnId":"turn-one","profileId":"profile","condition":"{{condition}}"{{(condition == "noActivity" ? ",\"inactivitySeconds\":60" : "")}}{{(policy == null ? "" : ",\"policy\":" + policy)}}}""",
            "订阅这个精确轮次，只在指定条件叫我", Target);
    private static Task<TrackedWorkItem> Change(DurableConversationLedger l, F f, TrackedWorkItem work, string action) =>
        Execute(l, f, "change_commitment", JsonSerializer.Serialize(new { workItemId = work.Id, expectedVersion = work.Version, action }),
            action + " " + work.Id, work.Task);
    private static async Task<TrackedWorkItem> Execute(DurableConversationLedger ledger, F f, string action, string args, string body, TaskReference? task = null)
    {
        var context = await Begin(ledger, f.Clock, action, args, body, task);
        var result = await ledger.ApplyCommitmentToolAsync(context);
        Equal(ToolExecutionState.Completed, result.State);
        await ledger.FinishToolInvocationAsync(Identity, context.InvocationId, ToolInvocationStage.Completed,
            JsonSerializer.Serialize(new { status = "completed", facts = result.Facts }), OperationStage.Completed);
        return (await ledger.GetCommitmentsAsync(Identity)).WorkItems.Single(x => x.Id == result.Facts.GetProperty("workItemId").GetGuid());
    }
    private static async Task<ToolExecutionContext> Begin(DurableConversationLedger l, Clock clock, string action, string args, string body, TaskReference? task = null)
    {
        string source = Guid.NewGuid().ToString("N");
        var descriptor = CommitmentTools.Descriptors().Single(x => x.Name == action);
        Check(descriptor.Parameters.TryValidate(args, out var canonical, out _));
        var message = await l.ReceiveAsync(Identity, source, body, MessageTrust.AuthenticatedUser, clock.UtcNow);
        var generation = await l.BeginGenerationAsync(Identity, [source]);
        var invocation = await l.RegisterToolInvocationAsync(generation, source, "call", action, canonical, true);
        var authorization = await l.AuthorizeCurrentUserRequestAsync(Identity, source, action, CommitmentTools.TargetKey(action, canonical), canonical, body, task);
        var operation = await l.QueueOperationAsync(Identity, authorization.Id, source);
        await l.BeginToolInvocationAsync(generation, invocation.Id, operation.Id);
        return new(Identity, source, message.CurrentRevision, invocation.Id, operation.Id, operation.IdempotencyKey);
    }
    private static CommitmentTaskObservation Observe(F f, CommitmentTaskState state) =>
        new(Target, state, f.Clock.UtcNow, f.Clock.UtcNow, true, true, "test recorded lifecycle");
    private static async Task<OutboxMessage> Finalize(DurableConversationLedger l)
    {
        var draft = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        return (await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, draft.ContextBody)).Outbox.Single();
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = FakeClock.Now; }
    private sealed class F : IAsyncDisposable
    {
        private readonly Fixture _files = new();
        private readonly List<DurableConversationLedger> _opened = [];
        internal Clock Clock { get; } = new();
        internal string Path => _files.Path;
        internal async Task<DurableConversationLedger> Open(FaultInjector? fault = null, LedgerOptions? options = null)
        {
            var l = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path, faultInjector: fault), new TestEncryption(_files.Key), Clock, options);
            _opened.Add(l); return l;
        }
        internal async Task Rewrite(Action<JsonNode> mutate)
        {
            await using var storage = new FileJournalStorage(Path);
            var latest = (await storage.ReadLatestAsync())!;
            var protector = new TestEncryption(_files.Key);
            byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{sequence}");
            var plain = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
            var root = JsonNode.Parse(plain)!; mutate(root);
            await storage.AppendAsync(latest.Sequence, protector.Protect(Encoding.UTF8.GetBytes(root.ToJsonString()), Purpose(latest.Sequence + 1)));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var l in _opened) await l.DisposeAsync();
            await _files.DisposeAsync();
        }
    }
    private static async Task Error(string code, Func<Task> action) => Equal(code, (await Throws<LedgerException>(action)).Code);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
