using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static (string, Func<Task>)[] CatchUpBoundaryCases() =>
    [
        ("catchup_new_dialogue_hold_captured_and_prepared_allow_current_reply", CatchDialogueBoundary),
        ("catchup_expiring_verified_id_is_revoked_without_losing_input", CatchExpiredVerification),
        ("catchup_foreign_and_nondecision_verification_fail_closed", CatchForeignVerification),
        ("catchup_new_required_notice_needs_explicit_live_qualification", CatchNewDecision),
        ("catchup_expired_source_retires_only_unstarted_page", CatchExpiredSource),
        ("catchup_quiet_does_not_retire_prepared_page", CatchQuietRetained),
        ("catchup_captured_during_quiet_keeps_one_page_until_quiet_ends", CatchCapturedDuringQuiet),
        ("catchup_quiet_source_does_not_delay_a_sendable_reminder", CatchMixedQuietSources),
        ("catchup_context_failure_wake_has_independent_delivery_budget", CatchWakeBudget),
        ("catchup_attempted_short_notice_is_not_adopted_by_required_decision", CatchAttemptedShort),
        ("catchup_legacy_approval_classifies_before_batch_or_conflicts_without_write", CatchLegacyApproval),
        ("catchup_reserved_legacy_approval_never_bypasses_live_marker", CatchReservedLegacyApproval),
        ("catchup_same_work_runs_split_impossible_combination_and_recheck_policy", CatchAggregatePolicy)
    ];
    private static async Task CatchDialogueBoundary()
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
            var decision = await EnsureDecision(l, f);
            var source = await CatchInput(l, f, mode == 0 ? CatchUpDisposition.HoldHistory : CatchUpDisposition.AllowHistory);
            CatchUpCaptureResult? prepared = mode == 2 ? await l.PrepareCatchUpAsync(Identity, source,
                verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]) : null;
            await l.BeginNewDialogueAsync(Identity, source);
            var result = await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
            Equal(0, result.Window.VerifiedRequiredDecisionEventIds.Length);
            if (prepared != null) Equal(OutboxStage.Superseded, result.Outbox.Single().Stage);
            await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
            long size = new FileInfo(f.Path).Length;
            await l.PrepareCatchUpAsync(Identity, source); Equal(size, new FileInfo(f.Path).Length);
            await l.DisposeAsync(); await f.Open();
        }
    }
    private static async Task CatchExpiredVerification()
    {
        await using var f = new F(); var l = await f.Open(); var proof = Decision(f) with { ExpiresAt = f.Clock.UtcNow.AddSeconds(1) };
        var notice = await EnsureDecision(l, f, proof); var source = await CatchInput(l, f);
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [notice.Notification.Event.Id]);
        f.Clock.UtcNow += TimeSpan.FromSeconds(2);
        Equal(0, (await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [notice.Notification.Event.Id]))
            .Window.VerifiedRequiredDecisionEventIds.Length);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, notice.Notification.Outbox[0].Id));
    }
    private static async Task CatchForeignVerification()
    {
        await using var f = new F(); var l = await f.Open(); var plain = await l.EnqueueNotificationAsync(Identity, "plain", "plain", Target);
        var foreign = await l.EnsureRequiredDecisionNotificationAsync(Identity with { BindingId = "foreign" }, "foreign", Target, Decision(f), "foreign");
        var source = await CatchInput(l, f);
        await Error(LedgerErrors.IdentityMismatch, () => l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [plain.Event.Id]));
        await Throws<LedgerException>(() => l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [foreign.Notification.Event.Id]));
        Equal(CatchUpWindowStage.Captured, (await l.GetCatchUpAsync(Identity)).Windows.Single().Stage);
    }
    private static async Task CatchNewDecision()
    {
        await using var f = new F(); var l = await f.Open(); var source = await CatchInput(l, f);
        await l.PrepareCatchUpAsync(Identity, source); var notice = await EnsureDecision(l, f);
        var reply = await CatchReply(l, source);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, notice.Notification.Outbox[0].Id));
        Check((await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(x => x.Id == reply.Id));
        var prepared = await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [notice.Notification.Event.Id]);
        Check(prepared.Window.RequiredDecisionEventIds.Contains(notice.Notification.Event.Id));
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, reply.Id));
        await l.PrepareCatchUpAsync(Identity, source); await l.BeginOutboxSendAsync(Identity, reply.Id);
    }
    private static async Task CatchExpiredSource()
    {
        await using var f = new F(); var l = await f.Open();
        var args = JsonNode.Parse(ReminderArgs(f.Clock))!;
        args["deadlineUtc"] = f.Clock.UtcNow.AddMinutes(4).ToString("O");
        await Execute(l, f, "schedule", args.ToJsonString(), "提醒我喝水");
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity);
        var old = await Finalize(l); var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        Equal(OutboxStage.Superseded, (await l.PrepareCatchUpAsync(Identity, source)).Outbox.Single().Stage);
        Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == old.Id).Stage);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchQuietRetained()
    {
        await using var f = new F(); var l = await f.Open();
        string start = f.Clock.UtcNow.AddMinutes(4).ToString("HH:mm"), end = f.Clock.UtcNow.AddMinutes(20).ToString("HH:mm");
        await Reminder(l, f, $$"""{"quietStart":"{{start}}","quietEnd":"{{end}}","timeZoneId":"UTC"}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity); await Finalize(l);
        var source = await CatchInput(l, f); await l.PrepareCatchUpAsync(Identity, source); f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        Equal(OutboxStage.Pending, (await l.PrepareCatchUpAsync(Identity, source)).Outbox.Single().Stage);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchCapturedDuringQuiet()
    {
        await using var f = new F(); var l = await f.Open();
        f.Clock.UtcNow = new(2026, 10, 21, 17, 59, 0, TimeSpan.Zero);
        await Reminder(l, f, """{"quietStart":"18:03","quietEnd":"06:00","timeZoneId":"UTC"}""");
        f.Clock.UtcNow = new(2026, 10, 21, 18, 2, 0, TimeSpan.Zero);
        await l.AdvanceCommitmentsAsync(Identity);
        var original = await Finalize(l);
        f.Clock.UtcNow = new(2026, 10, 21, 18, 4, 0, TimeSpan.Zero);
        var source = await CatchInput(l, f);
        var prepared = await l.PrepareCatchUpAsync(Identity, source);
        Equal(CatchUpWindowStage.Prepared, prepared.Window.Stage);
        var page = prepared.Outbox.Single();
        Equal(OutboxStage.Pending, page.Stage);
        Equal(0, page.SendAttempts);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, page.Id));
        Check(!(await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(x => x.Id == page.Id || x.Id == original.Id));
        await AcceptDecision(l, await CatchReply(l, source));
        var quiet = await l.ReadConversationAsync(Identity);
        Equal(0, quiet.CommitmentBudgetReservations.Length);
        Equal(0, quiet.Outbox.Single(x => x.Id == page.Id).SendAttempts);
        await l.DisposeAsync(); l = await f.Open();
        var reopened = await l.PrepareCatchUpAsync(Identity, source);
        Equal(prepared.Batch!.Id, reopened.Batch!.Id);
        Equal(page.Id, reopened.Outbox.Single().Id);
        Equal(OutboxStage.Pending, reopened.Outbox.Single().Stage);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, page.Id));
        f.Clock.UtcNow = new(2026, 10, 22, 6, 1, 0, TimeSpan.Zero);
        Check((await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(x => x.Id == page.Id));
        await AcceptDecision(l, page);
        var saved = await l.ReadConversationAsync(Identity);
        var progress = DurableConversationLedger.GetCatchUpProgress(saved).Single();
        Equal(0, progress.RemainingCharacters);
        Equal(original.Body.Length, progress.Sources.Single().AcceptedCharacters);
        Equal(1, saved.CatchUpBatches.Length);
        Equal(OutboxStage.Accepted, saved.Outbox.Single(x => x.Id == page.Id).Stage);
        Equal(1, saved.Outbox.Single(x => x.Id == page.Id).SendAttempts);
        Equal(OutboxStage.Pending, saved.Outbox.Single(x => x.Id == original.Id).Stage);
        Equal(0, saved.Outbox.Single(x => x.Id == original.Id).SendAttempts);
        Equal(CommitmentRunStage.Completed, saved.CommitmentRuns.Single().Stage);
        Equal(TrackedWorkStage.Completed, saved.WorkItems.Single().Stage);
        Equal(1, saved.CommitmentBudgetReservations.Length);
        Equal(prepared.Batch.Id, (await l.PrepareCatchUpAsync(Identity, source)).Batch!.Id);
        Check(!(await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(x => x.Id == page.Id || x.Id == original.Id));
    }
    private static async Task CatchMixedQuietSources()
    {
        await using var f = new F(); var l = await f.Open();
        f.Clock.UtcNow = new(2026, 10, 21, 17, 59, 0, TimeSpan.Zero);
        var quietWork = await Reminder(l, f, """{"quietStart":"18:03","quietEnd":"06:00","timeZoneId":"UTC"}""");
        var readyArguments = System.Text.Json.Nodes.JsonNode.Parse(ReminderArgs(f.Clock))!;
        readyArguments["text"] = "合成可发送提醒";
        var readyWork = await Execute(l, f, "schedule", readyArguments.ToJsonString(), "提醒我合成可发送提醒");
        f.Clock.UtcNow = new(2026, 10, 21, 18, 2, 0, TimeSpan.Zero);
        await l.AdvanceCommitmentsAsync(Identity);
        foreach (var draft in await l.GetPendingCommitmentNotificationsAsync(Identity))
            await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, draft.ContextBody);
        var originals = (await l.ReadConversationAsync(Identity)).Outbox.Where(x => x.Kind == OutboxKind.Notification).ToArray();
        var readyOriginal = originals.Single(x => x.Body == "合成可发送提醒");
        var quietOriginal = originals.Single(x => x.Id != readyOriginal.Id);
        f.Clock.UtcNow = new(2026, 10, 21, 18, 4, 0, TimeSpan.Zero);
        var source = await CatchInput(l, f);
        var prepared = await l.PrepareCatchUpAsync(Identity, source);
        var readyPage = prepared.Outbox.Single(x => x.Body.Contains(readyOriginal.Body, StringComparison.Ordinal));
        await AcceptDecision(l, readyPage);
        Equal(0, readyPage.SegmentIndex);
        Check(!readyPage.Body.Contains(quietOriginal.Body, StringComparison.Ordinal));
        Equal(2, prepared.Outbox.Length);
        var quietPage = prepared.Outbox.Single(x => x.Id != readyPage.Id);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, quietPage.Id));
        await AcceptDecision(l, await CatchReply(l, source));
        var duringQuiet = await l.ReadConversationAsync(Identity);
        Equal(TrackedWorkStage.Completed, duringQuiet.WorkItems.Single(x => x.Id == readyWork.Id).Stage);
        Equal(TrackedWorkStage.Triggered, duringQuiet.WorkItems.Single(x => x.Id == quietWork.Id).Stage);
        Equal(0, duringQuiet.Outbox.Single(x => x.Id == quietPage.Id).SendAttempts);
        foreach (var original in originals)
        {
            Equal(OutboxStage.Pending, duringQuiet.Outbox.Single(x => x.Id == original.Id).Stage);
            Equal(0, duringQuiet.Outbox.Single(x => x.Id == original.Id).SendAttempts);
        }
        f.Clock.UtcNow = new(2026, 10, 22, 6, 1, 0, TimeSpan.Zero);
        Check((await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(x => x.Id == quietPage.Id));
        await AcceptDecision(l, quietPage);
        var completed = await l.ReadConversationAsync(Identity);
        Equal(1, completed.CatchUpBatches.Length);
        Equal(2, completed.CommitmentBudgetReservations.Length);
        Check(completed.WorkItems.All(x => x.Stage == TrackedWorkStage.Completed)
            && completed.CommitmentRuns.All(x => x.Stage == CommitmentRunStage.Completed)
            && DurableConversationLedger.GetCatchUpProgress(completed).All(x => x.RemainingCharacters == 0));
        foreach (var page in prepared.Outbox)
        {
            Equal(OutboxStage.Accepted, completed.Outbox.Single(x => x.Id == page.Id).Stage);
            Equal(1, completed.Outbox.Single(x => x.Id == page.Id).SendAttempts);
        }
        foreach (var original in originals)
        {
            Equal(OutboxStage.Pending, completed.Outbox.Single(x => x.Id == original.Id).Stage);
            Equal(0, completed.Outbox.Single(x => x.Id == original.Id).SendAttempts);
        }
        Equal(prepared.Batch!.Id, (await l.PrepareCatchUpAsync(Identity, source)).Batch!.Id);
    }
    private static async Task CatchWakeBudget()
    {
        foreach (var outcome in new[] { OutboxStage.Rejected, OutboxStage.Unknown })
        {
            await using var f = new F(); var l = await f.Open(options: new() { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 2 } });
            await ConfigureWake(l); await Reminder(l, f, "{\"maximumDeliveryAttempts\":2}");
            f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity); var old = await Finalize(l);
            await l.BeginOutboxSendAsync(Identity, old.Id); await l.RecordOutboxOutcomeAsync(Identity, old.Id, OutboxStage.Rejected, "first context failure",
                rejectionReason: OutboxRejectionReason.ContextUnavailable);
            var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id);
            await l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[0].Id, outcome, "second transport outcome",
                rejectionReason: outcome == OutboxStage.Rejected ? OutboxRejectionReason.ContextUnavailable : OutboxRejectionReason.None);
            Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
            var wake = await l.EnsurePendingWakeAsync(Identity, WakeRoute);
            if (outcome == OutboxStage.Unknown) { Check(wake == null); continue; }
            Check(wake != null); f.Clock.UtcNow = wake!.NotBefore;
            var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); await l.ReleaseWakeSendAsync(lease);
        }
    }
    private static async Task CatchAttemptedShort()
    {
        foreach (var outcome in new[] { OutboxStage.Sending, OutboxStage.Unknown, OutboxStage.Accepted, OutboxStage.Rejected })
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
            await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "token-one" });
            await Finalize(l); var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source);
            await l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id);
            if (outcome != OutboxStage.Sending) await l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[0].Id, outcome, "actual batch receipt");
            var detailed = await EnsureDecision(l, f); Check(detailed.Runs.Single().SharedDecisionEventId == null);
            if (outcome == OutboxStage.Sending) await l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[0].Id, OutboxStage.Accepted, "late batch receipt");
            await l.DisposeAsync(); await f.Open();
        }
    }
    private static async Task CatchLegacyApproval()
    {
        foreach (bool alreadyPrepared in new[] { false, true })
        {
            await using var f = new F(); var l = await f.Open();
            string key = alreadyPrepared ? "legacy-explicit-proof" : "owned-approval/owned-profile/token-one";
            var plain = await l.EnqueueNotificationAsync(Identity, key,
                "请核对这个精确任务的审批，编号 token-one。", Target);
            var source = await CatchInput(l, f);
            if (alreadyPrepared)
            {
                await l.PrepareCatchUpAsync(Identity, source); long size = new FileInfo(f.Path).Length;
                await Error(LedgerErrors.Conflict, () => EnsureDecision(l, f, key: key)); Equal(size, new FileInfo(f.Path).Length);
                Check((await l.ReadConversationAsync(Identity)).Events.Single(x => x.Id == plain.Event.Id).RequiredDecision == null);
            }
            else
            {
                await EnsureDecision(l, f);
                var result = await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [plain.Event.Id]);
                Check(result.Batch == null); Check(result.Window.RequiredDecisionEventIds.Contains(plain.Event.Id));
                Check(!result.Window.CandidateEventIds.Contains(plain.Event.Id)); await AcceptDecision(l, plain.Outbox[0]);
            }
            await l.DisposeAsync(); await f.Open();
        }
    }
    private static async Task CatchReservedLegacyApproval()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        var plain = await l.EnqueueNotificationAsync(Identity, "owned-approval/owned-profile/token-one",
            "请核对这个精确任务的审批，编号 token-one。", Target);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, plain.Outbox[0].Id));
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        var source = await CatchInput(l, f); var result = await l.PrepareCatchUpAsync(Identity, source);
        Check(result.Batch == null); Equal(0, result.Window.CandidateEventIds.Length);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
        await EnsureDecision(l, f);
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [plain.Event.Id]);
        await AcceptDecision(l, plain.Outbox[0]); await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchAggregatePolicy()
    {
        foreach (string policy in new[] { "{\"maximumNotificationsPer24Hours\":1}", "{\"minimumIntervalSeconds\":60}" })
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision", policy);
            foreach (string token in new[] { "one", "two" })
            {
                f.Clock.UtcNow += TimeSpan.FromSeconds(1);
                await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = token });
                await Finalize(l);
            }
            var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source);
            Equal(2, batch.Batch!.Members.Length); Equal(2, batch.Outbox.Length);
            await AcceptDecision(l, batch.Outbox[0]);
            await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id));
            Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
            Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Sum(x => x.DeliveryAttempts.Length));
            await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
            f.Clock.UtcNow += policy.Contains("maximumNotifications", StringComparison.Ordinal) ? TimeSpan.FromDays(1) : TimeSpan.FromSeconds(60);
            await AcceptDecision(l, batch.Outbox[1]);
            Check((await l.GetCommitmentsAsync(Identity)).Runs.All(x => x.Stage == CommitmentRunStage.Completed && x.DeliveryAttempts.Length == 1));
        }
    }
}
