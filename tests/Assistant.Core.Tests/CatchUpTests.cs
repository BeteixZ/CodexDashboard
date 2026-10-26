using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static (string, Func<Task>)[] CatchUpCases() =>
    [
        ("catchup_receive_hold_is_atomic_and_old_sender_cannot_bypass", CatchHold),
        ("catchup_capture_fence_and_batch_precede_current_reply", CatchOrder),
        ("catchup_lossless_grouping_and_observation_time", CatchGrouping),
        ("catchup_acceptance_is_separate_coverage_not_original_acceptance", CatchCoverage),
        ("catchup_long_sources_have_bounded_pages_without_truncation", CatchLong),
        ("catchup_partially_accepted_source_group_skips_accepted_prefix", CatchPartial),
        ("catchup_unknown_and_sending_sources_are_excluded", CatchUncertainSources),
        ("catchup_new_input_supersedes_only_unstarted_pages", CatchNewInput),
        ("catchup_unknown_page_never_reassembled_under_new_input", CatchUnknownPage),
        ("catchup_rejected_page_needs_new_input_not_prepare_loop", CatchRejectedPage),
        ("catchup_commitment_run_has_independent_completion_proof", CatchRunProof),
        ("catchup_multiple_run_charges_are_atomic_and_distinct", CatchCharges),
        ("catchup_insufficient_multi_run_quota_consumes_nothing_and_allows_reply", CatchBudget),
        ("catchup_multisegment_same_run_is_one_charge", CatchOneRun),
        ("catchup_source_quiet_defers_without_blocking_current_reply", CatchQuiet),
        ("catchup_source_cancel_keeps_inflight_actual_receipt", CatchCancel),
        ("catchup_required_decision_is_not_batched_and_needs_live_ids", CatchRequired),
        ("catchup_verified_decision_rejected_once_per_input", CatchRequiredRetry),
        ("catchup_revoke_decision_verification_unblocks_reply", CatchRequiredRevoke),
        ("catchup_permanent_decision_suppression_preserves_outbox_facts", CatchRequiredSuppressed),
        ("catchup_unknown_required_group_is_not_retried", CatchRequiredUnknown),
        ("catchup_duplicate_prepare_no_new_body_or_write", CatchNoOp),
        ("catchup_untrusted_input_cannot_request_window", CatchAuthority),
        ("catchup_input_without_context_holds_historical_sources", CatchNoContext),
        ("catchup_file_receive_flush_recovers_window_before_seen", CatchReceiveFault),
        ("catchup_file_prepare_flush_recovers_exact_batch", CatchPrepareFault),
        ("catchup_file_begin_flush_recovers_unknown_coverage", CatchBeginFault),
        ("catchup_file_accept_flush_recovers_completion_proof", CatchAcceptFault),
        ("catchup_version_two_blocks_old_reader_semantics", CatchVersion),
        ("catchup_corrupt_scope_hash_coverage_and_run_proofs_fail_closed", CatchCorrupt),
        ("catchup_capacity_does_not_commit_input_without_hold", CatchCapacity),
        ("catchup_concurrent_prepare_has_one_manifest", CatchConcurrent),
        ..CatchUpBoundaryCases(),
        ..CatchUpSpanCases()
    ];
    private static async Task<string> CatchInput(DurableConversationLedger l, F f, CatchUpDisposition disposition = CatchUpDisposition.AllowHistory,
        bool context = true, ConversationIdentity? identity = null)
    {
        string id = Guid.NewGuid().ToString("N");
        await l.ReceiveAsync(identity ?? Identity, id, "我回来了", MessageTrust.AuthenticatedUser, f.Clock.UtcNow,
            catchUp: new(disposition, context, "UTC")); return id;
    }
    private static async Task<OutboxMessage> CatchReply(DurableConversationLedger l, string source)
    {
        var generation = await l.BeginGenerationAsync(Identity, [source]);
        await l.PrepareReplyAsync(generation, ["这次的普通回复。"]);
        return (await l.EnqueuePreparedReplyAsync(generation)).Single();
    }
    private static async Task CatchHold()
    {
        await using var f = new F(); var l = await f.Open();
        var original = (await l.EnqueueNotificationAsync(Identity, "old", "旧通知", Target)).Outbox.Single();
        var source = await CatchInput(l, f, CatchUpDisposition.HoldHistory);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, original.Id));
        var result = await l.PrepareCatchUpAsync(Identity, source); Equal(CatchUpWindowStage.Deferred, result.Window.Stage); Check(result.Batch == null);
        var reply = await CatchReply(l, source); await l.BeginOutboxSendAsync(Identity, reply.Id);
        await l.DisposeAsync(); l = await f.Open(); Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
    }
    private static async Task CatchOrder()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
        var source = await CatchInput(l, f); var reply = await CatchReply(l, source);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, reply.Id));
        var batch = await l.PrepareCatchUpAsync(Identity, source);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, reply.Id));
        await AcceptDecision(l, batch.Outbox.Single()); await l.BeginOutboxSendAsync(Identity, reply.Id);
    }
    private static async Task CatchGrouping()
    {
        await using var f = new F(); var l = await f.Open();
        var first = await RejectedNotice(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2); var second = await RejectedNotice(l, f);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f));
        Equal(1, batch.Outbox.Length); Equal(2, batch.Batch!.Members.Length);
        Equal(2, batch.Outbox.Single().Body.Split("原始正文，不改写。", StringSplitOptions.None).Length - 1);
        Check(batch.Outbox.Single().Body.Contains(first.CreatedAt.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal));
        Check(batch.Outbox.Single().Body.Contains(second.CreatedAt.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal));
        Check(batch.Outbox.Single().Body.Length <= 900);
        var snapshot = await l.ReadConversationAsync(Identity); Equal(0, snapshot.Authorizations.Length); Equal(0, snapshot.Operations.Length);
    }
    private static async Task CatchCoverage()
    {
        await using var f = new F(); var l = await f.Open(); var old = await RejectedNotice(l, f);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await AcceptDecision(l, batch.Outbox.Single());
        var snapshot = await l.ReadConversationAsync(Identity); var original = snapshot.Outbox.Single(x => x.Id == old.Id);
        Equal(OutboxStage.Rejected, original.Stage); Equal(old.Body, original.Body); Equal(old.SendAttempts, original.SendAttempts);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        await Error(LedgerErrors.NotificationSuppressed, () => l.RetryRejectedOutboxAsync(Identity, old.Id));
        var again = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); Check(again.Batch == null);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchLong()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CatchUp = new() { MaximumPageCharacters = 100 } });
        string body = new('长', 500); var old = (await l.EnqueueNotificationAsync(Identity, "long", body, Target)).Outbox.Single();
        var source = await CatchInput(l, f); var result = await l.PrepareCatchUpAsync(Identity, source);
        Check(result.Batch != null); Equal(3, result.Outbox.Length); Equal(1, result.Window.DeferredEventCount);
        Equal(body, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == old.Id).Body);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, old.Id));
        foreach (var page in result.Outbox) await AcceptDecision(l, page);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchPartial()
    {
        await using var f = new F(); var l = await f.Open();
        var rows = (await l.EnqueueNotificationAsync(Identity, "parts", "完整两页", Target, segments: ["第一部分", "第二部分"])).Outbox;
        await AcceptDecision(l, rows[0]); var result = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f));
        Check(result.Batch != null); Equal(0, result.Window.DeferredEventCount);
        Check(result.Batch!.Members.SelectMany(x => x.Sources).All(x => x.OutboxId == rows[1].Id));
        Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox[0].Stage);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, rows[1].Id));
    }
    private static async Task CatchUncertainSources()
    {
        foreach (var stage in new[] { OutboxStage.Unknown, OutboxStage.Sending })
        {
            await using var f = new F(); var l = await f.Open();
            var rows = (await l.EnqueueNotificationAsync(Identity, "uncertain", "两页", Target, segments: ["一", "二"])).Outbox;
            await l.BeginOutboxSendAsync(Identity, rows[0].Id);
            if (stage == OutboxStage.Unknown) await l.RecordOutboxOutcomeAsync(Identity, rows[0].Id, stage, "unknown");
            Check((await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f))).Batch == null);
        }
    }
    private static async Task CatchNewInput()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
        var first = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f));
        var second = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f));
        Check(second.Batch != null); Check(first.Batch!.Id != second.Batch!.Id);
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == first.Outbox[0].Id).Stage);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchUnknownPage()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
        var first = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await l.BeginOutboxSendAsync(Identity, first.Outbox[0].Id);
        await l.DisposeAsync(); l = await f.Open();
        var source = await CatchInput(l, f); Check((await l.PrepareCatchUpAsync(Identity, source)).Batch == null);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchRejectedPage()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f); var source = await CatchInput(l, f);
        var first = await l.PrepareCatchUpAsync(Identity, source); await l.BeginOutboxSendAsync(Identity, first.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[0].Id, OutboxStage.Rejected, "definitely not accepted");
        Equal(OutboxStage.Rejected, (await l.PrepareCatchUpAsync(Identity, source)).Outbox.Single().Stage);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        Check((await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f))).Batch!.Id != first.Batch!.Id);
    }
    private static async Task<List<OutboxMessage>> CatchReminderSources(DurableConversationLedger l, F f, int count, bool multipart = false)
    {
        for (int i = 0; i < count; i++) await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity);
        var rows = new List<OutboxMessage>();
        foreach (var draft in await l.GetPendingCommitmentNotificationsAsync(Identity))
        {
            var finalized = await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, draft.ContextBody, multipart ? ["完整第一段", "完整第二段"] : null);
            var output = finalized.Outbox[0]; await l.BeginOutboxSendAsync(Identity, output.Id);
            rows.Add(await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context absent", rejectionReason: OutboxRejectionReason.ContextUnavailable));
        }
        return rows;
    }
    private static async Task CatchRunProof()
    {
        await using var f = new F(); var l = await f.Open(); var old = (await CatchReminderSources(l, f, 1)).Single();
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await AcceptDecision(l, batch.Outbox.Single());
        var run = (await l.GetCommitmentsAsync(Identity)).Runs.Single(); Equal(CommitmentRunStage.Completed, run.Stage);
        Equal(batch.Outbox.Single().Id, run.CatchUpDeliveryOutboxId!.Value);
        Equal(OutboxStage.Rejected, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == old.Id).Stage);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchCharges()
    {
        await using var f = new F(); var l = await f.Open(); await CatchReminderSources(l, f, 2);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); Equal(1, batch.Outbox.Length);
        await l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id);
        var snapshot = await l.ReadConversationAsync(Identity); Equal(4, snapshot.CommitmentBudgetReservations.Length);
        Equal(2, snapshot.CommitmentBudgetReservations.Count(x => x.CatchUpBatchId == batch.Batch!.Id));
        Check(snapshot.CommitmentRuns.All(x => x.DeliveryAttempts.Length == 2)); await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchBudget()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 3 } });
        await CatchReminderSources(l, f, 2); var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id));
        Equal(2, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchOneRun()
    {
        await using var f = new F(); var l = await f.Open(); await CatchReminderSources(l, f, 1, true);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); Equal(2, batch.Batch!.Members.Single().Sources.Length);
        await AcceptDecision(l, batch.Outbox[0]); var snapshot = await l.ReadConversationAsync(Identity);
        Equal(2, snapshot.CommitmentBudgetReservations.Length); Equal(2, snapshot.CommitmentRuns.Single().DeliveryAttempts.Length);
        Equal(CommitmentRunStage.Completed, snapshot.CommitmentRuns.Single().Stage);
    }
    private static async Task CatchQuiet()
    {
        await using var f = new F(); var l = await f.Open();
        string start = f.Clock.UtcNow.AddMinutes(4).ToString("HH:mm"), end = f.Clock.UtcNow.AddMinutes(20).ToString("HH:mm");
        await Reminder(l, f, $$"""{"quietStart":"{{start}}","quietEnd":"{{end}}","timeZoneId":"UTC"}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        await l.BeginOutboxSendAsync(Identity, output.Id); await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "rejected");
        var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source); f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id));
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchCancel()
    {
        await using var f = new F(); var l = await f.Open(); await CatchReminderSources(l, f, 1);
        var work = (await l.GetCommitmentsAsync(Identity)).WorkItems.Single();
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id);
        await Change(l, f, work, "cancel"); await l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[0].Id, OutboxStage.Accepted, "real accepted after cancel");
        Equal(CommitmentRunStage.Cancelled, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == batch.Outbox[0].Id).Stage);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task CatchRequired()
    {
        await using var f = new F(); var l = await f.Open(); var decision = await EnsureDecision(l, f); await RejectedNotice(l, f);
        var source = await CatchInput(l, f); var first = await l.PrepareCatchUpAsync(Identity, source);
        Equal(1, first.Batch!.Members.Length); Check(!first.Batch.Members.Any(x => x.SourceEventId == decision.Notification.Event.Id));
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, decision.Notification.Outbox[0].Id));
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, first.Outbox[0].Id));
        await AcceptDecision(l, decision.Notification.Outbox[0]); await AcceptDecision(l, first.Outbox[0]);
    }
    private static async Task CatchRequiredRetry()
    {
        await using var f = new F(); var l = await f.Open(); var decision = await EnsureDecision(l, f); var output = decision.Notification.Outbox[0];
        await l.BeginOutboxSendAsync(Identity, output.Id); await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "old rejection");
        var source = await CatchInput(l, f);
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        await l.BeginOutboxSendAsync(Identity, output.Id); await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "new rejection");
        long size = new FileInfo(f.Path).Length;
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        Equal(size, new FileInfo(f.Path).Length); Equal(OutboxStage.Rejected, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        await Error(LedgerErrors.NotificationSuppressed, () => l.RetryRejectedOutboxAsync(Identity, output.Id));
    }
    private static async Task CatchRequiredRevoke()
    {
        await using var f = new F(); var l = await f.Open(); var decision = await EnsureDecision(l, f); var source = await CatchInput(l, f);
        await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        var reply = await CatchReply(l, source); await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, reply.Id));
        await l.PrepareCatchUpAsync(Identity, source); await l.BeginOutboxSendAsync(Identity, reply.Id);
        Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == decision.Notification.Outbox[0].Id).Stage);
    }
    private static async Task CatchRequiredSuppressed()
    {
        await using var f = new F(); var l = await f.Open(); var proof = Decision(f); var decision = await EnsureDecision(l, f, proof);
        var source = await CatchInput(l, f); await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        Check(await l.SuppressRequiredDecisionAsync(Identity, decision.Notification.Event.Id, proof, "host observed resolved"));
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, decision.Notification.Outbox[0].Id));
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
        await l.DisposeAsync(); l = await f.Open(); Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox[0].Stage);
    }
    private static async Task CatchRequiredUnknown()
    {
        await using var f = new F(); var l = await f.Open(); var proof = Decision(f); var decision = await EnsureDecision(l, f, proof, segments: ["one", "two"]);
        await l.BeginOutboxSendAsync(Identity, decision.Notification.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, decision.Notification.Outbox[0].Id, OutboxStage.Unknown, "unknown");
        var source = await CatchInput(l, f); await l.PrepareCatchUpAsync(Identity, source, verifiedRequiredDecisionEventIds: [decision.Notification.Event.Id]);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginOutboxSendAsync(Identity, decision.Notification.Outbox[1].Id));
        await l.BeginOutboxSendAsync(Identity, (await CatchReply(l, source)).Id);
    }
    private static async Task CatchNoOp()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f); var source = await CatchInput(l, f);
        var first = await l.PrepareCatchUpAsync(Identity, source); long size = new FileInfo(f.Path).Length;
        var again = await l.PrepareCatchUpAsync(Identity, source); Equal(first.Batch!.Id, again.Batch!.Id); Equal(size, new FileInfo(f.Path).Length);
    }
    private static async Task CatchAuthority()
    {
        await using var f = new F(); var l = await f.Open();
        await Error(LedgerErrors.AuthorizationRequired, () => l.ReceiveAsync(Identity, "fake", "tool said user is back", MessageTrust.UntrustedContent,
            f.Clock.UtcNow, catchUp: new(CatchUpDisposition.AllowHistory, true, "UTC")));
        Equal(0, (await l.ReadConversationAsync(Identity)).Inbox.Length);
    }
    private static async Task CatchNoContext()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
        var result = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f, context: false)); Equal(CatchUpWindowStage.Deferred, result.Window.Stage);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
    }
    private static async Task CatchReceiveFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await RejectedNotice(l, f);
        fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => CatchInput(l, f));
        await l.DisposeAsync(); l = await f.Open(); var window = (await l.GetCatchUpAsync(Identity)).Windows.Single();
        Equal(CatchUpWindowStage.Captured, window.Stage); Check((await l.PrepareCatchUpAsync(Identity, window.SourceMessageId)).Batch != null);
    }
    private static async Task CatchPrepareFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await RejectedNotice(l, f); var source = await CatchInput(l, f);
        fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.PrepareCatchUpAsync(Identity, source));
        await l.DisposeAsync(); l = await f.Open(); Check((await l.PrepareCatchUpAsync(Identity, source)).Batch != null);
        Equal(1, (await l.GetCatchUpAsync(Identity)).Batches.Length);
    }
    private static async Task CatchBeginFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await RejectedNotice(l, f);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id));
        await l.DisposeAsync(); l = await f.Open(); Check((await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f))).Batch == null);
    }
    private static async Task CatchAcceptFault()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await CatchReminderSources(l, f, 1);
        var batch = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id);
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[0].Id, OutboxStage.Accepted, "accepted"));
        await l.DisposeAsync(); l = await f.Open(); Equal(batch.Outbox[0].Id, (await l.GetCommitmentsAsync(Identity)).Runs.Single().CatchUpDeliveryOutboxId!.Value);
    }
    private static async Task CatchVersion()
    {
        await using var f = new F(); var l = await f.Open(); await CatchInput(l, f); await l.DisposeAsync();
        await f.Rewrite(root => { Equal(2, root["schemaVersion"]!.GetValue<int>()); root["schemaVersion"] = 1; });
        await Error(LedgerErrors.CorruptStorage, () => f.Open());
    }
    private static async Task CatchCorrupt()
    {
        Action<JsonNode>[] mutations =
        [
            r => r["catchUpWindows"] = null,
            r => r["catchUpWindows"]![0]!["trustedInputRevision"] = 99,
            r => r["catchUpBatches"]![0]!["members"]![0]!["sources"]![0]!["bodyHash"] = "bad",
            r => r["catchUpBatches"]![0]!["members"]![0]!["sourceEventId"] = Guid.NewGuid().ToString(),
            r => r["catchUpBatches"]![0]!["identity"]!["bindingId"] = "other",
            r => r["catchUpBatches"]![0]!["members"]![0]!["catchUpOutboxId"] = Guid.NewGuid().ToString(),
            r => r["catchUpBatches"]![0]!["members"]![0]!["groupKey"] = "invented"
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f);
            await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f)); await l.DisposeAsync(); await f.Rewrite(mutate);
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task CatchCapacity()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CatchUp = new() { MaximumWindows = 1 } }); await CatchInput(l, f);
        await Error(LedgerErrors.CapacityExceeded, () => CatchInput(l, f)); Equal(1, (await l.ReadConversationAsync(Identity)).Inbox.Length);
    }
    private static async Task CatchConcurrent()
    {
        await using var f = new F(); var l = await f.Open(); await RejectedNotice(l, f); var source = await CatchInput(l, f);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => l.PrepareCatchUpAsync(Identity, source)));
        Equal(1, results.Select(x => x.Batch!.Id).Distinct().Count()); Equal(1, (await l.GetCatchUpAsync(Identity)).Batches.Length);
    }
}
