using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static (string, Func<Task>)[] CatchUpSpanCases() =>
    [
        ("span_long_report_is_lossless_across_three_page_windows", SpanWindows),
        ("span_original_accepted_prefix_is_not_repeated", SpanAcceptedPrefix),
        ("span_same_batch_run_reserves_once_despite_interval_and_limits", SpanOneCharge),
        ("span_new_window_is_new_attempt_and_quota_is_atomic", SpanNewCharge),
        ("span_original_later_segment_cannot_skip_new_attempt_limit", SpanLaterSegmentLimit),
        ("span_partial_run_does_not_complete_until_exact_remaining_acceptance", SpanRunCompletion),
        ("span_unknown_second_page_blocks_entire_source_after_restart", SpanUnknown),
        ("span_late_sending_receipt_survives_new_window_without_replay", SpanSending),
        ("span_rejected_page_retries_only_unaccepted_ranges_next_input", SpanRejected),
        ("span_explicit_unknown_reconciliation_unlocks_only_remaining", SpanReconcile),
        ("span_unicode_and_original_whitespace_survive_boundaries", SpanUnicode),
        ("span_progress_uses_same_snapshot_and_is_read_only", SpanProgress),
        ("span_old_schema_three_and_missing_manifest_version_remain_readable", SpanLegacy),
        ("span_schema_four_is_required_and_never_downgraded", SpanVersion),
        ("span_corrupt_bounds_order_overlap_and_surrogates_fail_closed", SpanCorrupt),
        ("span_reordered_pages_with_valid_bodies_still_fail_closed", SpanPageOrder),
        ("span_forged_partial_completion_anchor_is_rejected", SpanFalseCompletion),
        ("span_duplicate_batch_run_charge_is_rejected_on_recovery", SpanDuplicateCharge),
        ("span_prepare_afterflush_recovers_immutable_ranges", () => SpanFlush("prepare")),
        ("span_second_begin_afterflush_recovers_unknown_and_one_charge", () => SpanFlush("begin")),
        ("span_final_accept_afterflush_recovers_complete_coverage", () => SpanFlush("accept")),
        ("span_quiet_pause_and_cancel_still_apply_to_charged_continuation", SpanPolicy),
        ("span_distinct_runs_on_later_page_charge_only_new_members", SpanMixedRuns),
        ("span_global_minute_absolute_limit_splits_impossible_charge_groups", () => SpanAbsoluteBudget(false)),
        ("span_global_day_absolute_limit_splits_impossible_charge_groups", () => SpanAbsoluteBudget(true))
    ];
    private static string LongBody(int count = 4200) => string.Concat(Enumerable.Range(0, count).Select(i => (char)('一' + i % 500))) + "  \r\n尾部\t";
    private static async Task<OutboxMessage> SpanNotice(DurableConversationLedger l, string body, string key = "span-notice") =>
        (await l.EnqueueNotificationAsync(Identity, key, body, Target)).Outbox.Single();
    private static async Task<CatchUpCaptureResult> SpanWindow(DurableConversationLedger l, F f) =>
        await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f));
    private static string SpanText(ConversationSnapshot snapshot, CatchUpCaptureResult prepared) => string.Concat(prepared.Batch!.Members
        .SelectMany(x => x.Sources).Select(x => snapshot.Outbox.Single(o => o.Id == x.OutboxId).Body.Substring(x.Start, x.Length!.Value)));
    private static async Task AcceptSpanPages(DurableConversationLedger l, CatchUpCaptureResult prepared)
    { foreach (var page in prepared.Outbox) await AcceptDecision(l, page); }
    private static async Task<(OutboxMessage Output, TrackedWorkItem Work)> SpanReminder(DurableConversationLedger l, F f,
        string body, string? policy = null, bool prefix = false)
    {
        var work = await Reminder(l, f, policy); f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity);
        var draft = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        var result = await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, body, prefix ? ["accepted prefix", body] : null);
        if (prefix) await AcceptDecision(l, result.Outbox[0]);
        return (result.Outbox[^1], work);
    }
    private static async Task SpanWindows()
    {
        await using var f = new F(); var l = await f.Open(); string body = LongBody(); var original = await SpanNotice(l, body);
        string reconstructed = ""; int windows = 0;
        do
        {
            var prepared = await SpanWindow(l, f); windows++; Check(prepared.Batch?.ManifestVersion == 2);
            Check(prepared.Outbox.Length is > 0 and <= 3 && prepared.Outbox.All(x => x.Body.Length <= 900));
            reconstructed += SpanText(await l.ReadConversationAsync(Identity), prepared); await AcceptSpanPages(l, prepared);
            var progress = (await l.GetCatchUpProgressAsync(Identity)).Single(); Equal(reconstructed.Length, progress.AcceptedCharacters);
            Equal(body.Length - reconstructed.Length, progress.RemainingCharacters);
        } while (reconstructed.Length < body.Length && windows < 4);
        Equal(2, windows); Equal(body, reconstructed);
        Equal(original, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == original.Id));
        Check((await SpanWindow(l, f)).Batch == null); await l.DisposeAsync(); await f.Open();
    }
    private static async Task SpanAcceptedPrefix()
    {
        await using var f = new F(); var l = await f.Open(); string body = LongBody(1600);
        var rows = (await l.EnqueueNotificationAsync(Identity, "prefix", "public source", Target, segments: ["already accepted", body, "last page"])).Outbox;
        await AcceptDecision(l, rows[0]); var prepared = await SpanWindow(l, f);
        Check(prepared.Batch!.Members.SelectMany(x => x.Sources).All(x => x.OutboxId != rows[0].Id));
        Equal(body + "last page", SpanText(await l.ReadConversationAsync(Identity), prepared)); await AcceptSpanPages(l, prepared);
        var progress = (await l.GetCatchUpProgressAsync(Identity)).Single(); Equal(0, progress.RemainingCharacters);
        Equal(rows[0].Body.Length, progress.Sources[0].AcceptedCharacters); Equal(0, progress.Sources[0].PendingCharacters);
        Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == rows[1].Id).Stage);
    }
    private static async Task SpanOneCharge()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 1, MaximumNotificationsPerDay = 1 } });
        await SpanReminder(l, f, LongBody(2000), "{\"maximumDeliveryAttempts\":1,\"minimumIntervalSeconds\":3600,\"maximumNotificationsPer24Hours\":1}");
        var batch = await SpanWindow(l, f); Equal(3, batch.Outbox.Length);
        foreach (var page in batch.Outbox)
        {
            await AcceptDecision(l, page); var s = await l.ReadConversationAsync(Identity);
            Equal(1, s.CommitmentBudgetReservations.Count(x => x.CatchUpBatchId == batch.Batch!.Id)); Equal(1, s.CommitmentRuns.Single().DeliveryAttempts.Length);
        }
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task SpanNewCharge()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 1 } });
        await SpanReminder(l, f, LongBody(), "{\"maximumDeliveryAttempts\":3}"); var first = await SpanWindow(l, f); await AcceptSpanPages(l, first);
        var second = await SpanWindow(l, f); Check(second.Batch != null);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, second.Outbox[0].Id));
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
        f.Clock.UtcNow += TimeSpan.FromSeconds(61); await AcceptSpanPages(l, second);
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
    private static async Task SpanLaterSegmentLimit()
    {
        await using var f = new F(); var l = await f.Open(); await SpanReminder(l, f, LongBody(1300), "{\"maximumDeliveryAttempts\":1}", prefix: true);
        var batch = await SpanWindow(l, f); Check(batch.Batch != null);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[0].Id));
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task SpanRunCompletion()
    {
        await using var f = new F(); var l = await f.Open(); await SpanReminder(l, f, LongBody(), prefix: true);
        var first = await SpanWindow(l, f); await AcceptSpanPages(l, first);
        var partial = (await l.GetCommitmentsAsync(Identity)).Runs.Single(); Equal(CommitmentRunStage.NotificationPending, partial.Stage); Check(partial.CatchUpDeliveryOutboxId == null);
        var second = await SpanWindow(l, f); foreach (var page in second.Outbox.Take(second.Outbox.Length - 1)) await AcceptDecision(l, page);
        Equal(CommitmentRunStage.NotificationPending, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        await AcceptDecision(l, second.Outbox[^1]); var complete = (await l.GetCommitmentsAsync(Identity)).Runs.Single();
        Equal(CommitmentRunStage.Completed, complete.Stage); Equal(second.Outbox[^1].Id, complete.CatchUpDeliveryOutboxId!.Value);
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task SpanUnknown()
    {
        await using var f = new F(); var l = await f.Open(); var original = await SpanNotice(l, LongBody()); var first = await SpanWindow(l, f);
        await AcceptDecision(l, first.Outbox[0]); await l.BeginOutboxSendAsync(Identity, first.Outbox[1].Id); await l.DisposeAsync(); l = await f.Open();
        await SpanNotice(l, "unrelated clear fact", "unrelated"); var second = await SpanWindow(l, f);
        Check(second.Batch!.Members.All(x => x.SourceEventId != original.NotificationEventId));
        var progress = (await l.GetCatchUpProgressAsync(Identity)).Single(x => x.SourceEventId == original.NotificationEventId);
        Check(progress.HasUnknown); Check(progress.AcceptedCharacters > 0 && progress.RemainingCharacters > 0);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
    }
    private static async Task SpanSending()
    {
        await using var f = new F(); var l = await f.Open(); await SpanNotice(l, LongBody()); var first = await SpanWindow(l, f);
        await AcceptDecision(l, first.Outbox[0]); await l.BeginOutboxSendAsync(Identity, first.Outbox[1].Id);
        var held = await l.PrepareCatchUpAsync(Identity, await CatchInput(l, f, CatchUpDisposition.HoldHistory)); Check(held.Batch == null);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[1].Id, OutboxStage.Accepted, "actual late acceptance");
        var second = await SpanWindow(l, f); var starts = second.Batch!.Members.SelectMany(x => x.Sources).Select(x => x.Start).ToArray();
        Equal(first.Batch!.Members.Take(2).SelectMany(x => x.Sources).Sum(x => x.Length!.Value), starts.Min());
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == first.Outbox[2].Id).Stage);
    }
    private static async Task SpanRejected()
    {
        await using var f = new F(); var l = await f.Open(); await SpanNotice(l, LongBody(2000)); var first = await SpanWindow(l, f);
        await AcceptDecision(l, first.Outbox[0]); await l.BeginOutboxSendAsync(Identity, first.Outbox[1].Id);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[1].Id, OutboxStage.Rejected, "definite rejection");
        var second = await SpanWindow(l, f);
        Equal(first.Batch!.Members[1].Sources[0].Start, second.Batch!.Members[0].Sources[0].Start);
        await AcceptSpanPages(l, second); Equal(0, (await l.GetCatchUpProgressAsync(Identity)).Single().RemainingCharacters);
    }
    private static async Task SpanReconcile()
    {
        await using var f = new F(); var l = await f.Open(); await SpanNotice(l, LongBody(2000)); var first = await SpanWindow(l, f);
        await AcceptDecision(l, first.Outbox[0]); await l.BeginOutboxSendAsync(Identity, first.Outbox[1].Id);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[1].Id, OutboxStage.Unknown, "unknown");
        Check((await SpanWindow(l, f)).Batch == null);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[1].Id, OutboxStage.Accepted, "exact observed receipt", reconciliation: true);
        var next = await SpanWindow(l, f); Equal(first.Batch!.Members[2].Sources[0].Start, next.Batch!.Members[0].Sources[0].Start);
        await AcceptSpanPages(l, next); Equal(0, (await l.GetCatchUpProgressAsync(Identity)).Single().RemainingCharacters);
    }
    private static async Task SpanUnicode()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CatchUp = new() { MaximumPageCharacters = 100, MaximumPages = 20 } });
        string body = string.Concat(Enumerable.Repeat("🙂\r\n字\t ", 50)); await SpanNotice(l, body); var batch = await SpanWindow(l, f);
        Equal(body, SpanText(await l.ReadConversationAsync(Identity), batch));
        foreach (var span in batch.Batch!.Members.SelectMany(x => x.Sources))
        { Check(span.Start == 0 || !char.IsLowSurrogate(body[span.Start])); int end = span.Start + span.Length!.Value; Check(end == body.Length || !char.IsLowSurrogate(body[end])); }
        Check(batch.Outbox.All(x => x.Body.Length <= 100)); await l.DisposeAsync(); await f.Open();
    }
    private static async Task SpanProgress()
    {
        await using var f = new F(); var l = await f.Open(); var original = await SpanNotice(l, LongBody(1600)); var batch = await SpanWindow(l, f);
        var snapshot = await l.ReadConversationAsync(Identity); long size = new FileInfo(f.Path).Length;
        var prior = DurableConversationLedger.GetCatchUpProgress(snapshot).Single(); Equal(0, prior.AcceptedCharacters); Equal(original.Body.Length, prior.PendingCharacters);
        Equal(size, new FileInfo(f.Path).Length); await AcceptDecision(l, batch.Outbox[0]);
        Equal(0, DurableConversationLedger.GetCatchUpProgress(snapshot).Single().AcceptedCharacters);
        var current = (await l.GetCatchUpProgressAsync(Identity)).Single(); Equal(batch.Batch!.Members[0].Sources.Sum(x => x.Length!.Value), current.AcceptedCharacters);
        Equal(current.TotalCharacters, current.AcceptedCharacters + current.RemainingCharacters); Equal(original.Id, current.Sources.Single().OutboxId);
    }
    private static async Task SpanLegacy()
    {
        await using var f = new F(); var l = await f.Open(options: new() { CatchUp = new() { MaximumPageCharacters = 8000 } });
        await SpanNotice(l, LongBody(1200)); var legacy = await SpanWindow(l, f); Equal(1, legacy.Outbox.Length); await l.DisposeAsync();
        await f.Rewrite(root =>
        {
            root["schemaVersion"] = 3; root["catchUpBatches"]![0]!.AsObject().Remove("manifestVersion");
            foreach (var source in root["catchUpBatches"]![0]!["members"]![0]!["sources"]!.AsArray()) { source!.AsObject().Remove("start"); source.AsObject().Remove("length"); }
            string old = legacy.Outbox[0].Body.Replace(" 记录（待补报部分）：\n", " 记录：\n", StringComparison.Ordinal);
            root["outbox"]!.AsArray().Single(x => x!["id"]!.GetValue<Guid>() == legacy.Outbox[0].Id)!["body"] = old;
            var entry = root["events"]!.AsArray().Single(x => x!["id"]!.GetValue<Guid>() == legacy.Batch!.DeliveryEventId)!;
            entry["body"] = old; entry["notificationSegments"] = new JsonArray(old);
        });
        l = await f.Open(); await l.BeginOutboxSendAsync(Identity, legacy.Outbox[0].Id); await l.RecordOutboxOutcomeAsync(Identity, legacy.Outbox[0].Id, OutboxStage.Rejected, "legacy attempt rejected");
        var next = await SpanWindow(l, f); Check(next.Outbox.Length > 1); await AcceptSpanPages(l, next);
        Equal(0, (await l.GetCatchUpProgressAsync(Identity)).Single().RemainingCharacters); await l.DisposeAsync(); await f.Open();
    }
    private static async Task SpanVersion()
    {
        await using var f = new F(); var l = await f.Open(); await SpanNotice(l, "short"); await SpanWindow(l, f);
        await CatchInput(l, f, CatchUpDisposition.HoldHistory); await l.DisposeAsync();
        await f.Rewrite(root => { Equal(4, root["schemaVersion"]!.GetValue<int>()); root["schemaVersion"] = 3; });
        await Error(LedgerErrors.CorruptStorage, () => f.Open());
    }
    private static async Task SpanCorrupt()
    {
        foreach (string kind in new[] { "negative", "overflow", "zero", "missing", "overlap", "order", "surrogate", "version" })
        {
            await using var f = new F(); var l = await f.Open(); await SpanNotice(l, "🙂" + LongBody(1600)); await SpanWindow(l, f); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                var batch = root["catchUpBatches"]![0]!; var spans = batch["members"]![0]!["sources"]!; var span = spans[0]!;
                if (kind == "negative") span["start"] = -1;
                if (kind == "overflow") span["length"] = int.MaxValue;
                if (kind == "zero") span["length"] = 0;
                if (kind == "missing") span["length"] = null;
                if (kind == "overlap") batch["members"]![1]!["sources"]![0]!["start"] = 0;
                if (kind == "order") spans.AsArray().Add(span.DeepClone());
                if (kind == "surrogate") span["start"] = 1;
                if (kind == "version") batch["manifestVersion"] = 1;
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task SpanPageOrder()
    {
        await using var f = new F(); var l = await f.Open(); await SpanNotice(l, LongBody(1300));
        var batch = await SpanWindow(l, f); Equal(2, batch.Outbox.Length); await l.DisposeAsync();
        await f.Rewrite(root =>
        {
            var members = root["catchUpBatches"]![0]!["members"]!.AsArray();
            members[0]!["catchUpOutboxId"] = batch.Outbox[1].Id.ToString();
            members[1]!["catchUpOutboxId"] = batch.Outbox[0].Id.ToString();
            var rows = root["outbox"]!.AsArray();
            rows.Single(x => x!["id"]!.GetValue<Guid>() == batch.Outbox[0].Id)!["body"] = batch.Outbox[1].Body;
            rows.Single(x => x!["id"]!.GetValue<Guid>() == batch.Outbox[1].Id)!["body"] = batch.Outbox[0].Body;
            var entry = root["events"]!.AsArray().Single(x => x!["id"]!.GetValue<Guid>() == batch.Batch!.DeliveryEventId)!;
            entry["notificationSegments"] = new JsonArray(batch.Outbox[1].Body, batch.Outbox[0].Body);
            entry["body"] = batch.Outbox[1].Body + "\n\n" + batch.Outbox[0].Body;
        });
        await Error(LedgerErrors.CorruptStorage, () => f.Open());
    }
    private static async Task SpanFalseCompletion()
    {
        await using var f = new F(); var l = await f.Open(); await SpanReminder(l, f, LongBody(2000)); var batch = await SpanWindow(l, f);
        await AcceptDecision(l, batch.Outbox[0]); await l.DisposeAsync();
        await f.Rewrite(root => { var run = root["commitmentRuns"]![0]!; run["stage"] = (int)CommitmentRunStage.Completed; run["catchUpDeliveryOutboxId"] = batch.Outbox[0].Id.ToString(); });
        await Error(LedgerErrors.CorruptStorage, () => f.Open());
    }
    private static async Task SpanDuplicateCharge()
    {
        await using var f = new F(); var l = await f.Open(); await SpanReminder(l, f, LongBody(2000)); var batch = await SpanWindow(l, f);
        await AcceptDecision(l, batch.Outbox[0]); await l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id); await l.DisposeAsync();
        await f.Rewrite(root => { var rows = root["commitmentBudgetReservations"]!.AsArray(); var duplicate = rows.Single()!.DeepClone(); duplicate["id"] = Guid.NewGuid().ToString(); duplicate["resourceId"] = batch.Outbox[1].Id.ToString(); rows.Add(duplicate); });
        await Error(LedgerErrors.CorruptStorage, () => f.Open());
    }
    private static async Task SpanFlush(string phase)
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await SpanReminder(l, f, LongBody(1500));
        string input = await CatchInput(l, f); CatchUpCaptureResult? batch = null;
        if (phase == "prepare") { fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.PrepareCatchUpAsync(Identity, input)); }
        else
        {
            batch = await l.PrepareCatchUpAsync(Identity, input); Equal(2, batch.Outbox.Length); await AcceptDecision(l, batch.Outbox[0]);
            if (phase == "begin") { fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id)); }
            else { await l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id); fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[1].Id, OutboxStage.Accepted, "accepted")); }
        }
        await l.DisposeAsync(); l = await f.Open(); var s = await l.ReadConversationAsync(Identity); Equal(1, s.CatchUpBatches.Length);
        if (phase == "prepare") { Equal(0, s.CommitmentBudgetReservations.Length); Equal(2, (await l.PrepareCatchUpAsync(Identity, input)).Outbox.Length); }
        else { Equal(1, s.CommitmentBudgetReservations.Length); Equal(1, s.CommitmentRuns.Single().DeliveryAttempts.Length); Equal(phase == "begin" ? CommitmentRunStage.NotificationPending : CommitmentRunStage.Completed, s.CommitmentRuns.Single().Stage); }
        if (phase == "begin") Check((await l.GetCatchUpProgressAsync(Identity)).Single().HasUnknown);
    }
    private static async Task SpanPolicy()
    {
        foreach (string mode in new[] { "quiet", "pause", "cancel" })
        {
            await using var f = new F(); var l = await f.Open(); string? policy = mode == "quiet" ? "{\"quietStart\":\"18:04\",\"quietEnd\":\"18:20\",\"timeZoneId\":\"UTC\"}" : null;
            var item = await SpanReminder(l, f, LongBody(2000), policy); var batch = await SpanWindow(l, f); await AcceptDecision(l, batch.Outbox[0]);
            if (mode == "quiet") f.Clock.UtcNow += TimeSpan.FromMinutes(2); else await Change(l, f, item.Work, mode);
            var expectedStage = mode == "quiet" ? OutboxStage.Pending : OutboxStage.Superseded;
            Equal(expectedStage, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == batch.Outbox[1].Id).Stage);
            await Error(mode == "quiet" ? LedgerErrors.NotificationSuppressed : LedgerErrors.InvalidTransition,
                () => l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id));
            Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Single().DeliveryAttempts.Length);
            Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == batch.Outbox[0].Id).Stage);
        }
    }
    private static async Task SpanMixedRuns()
    {
        await using var f = new F(); var l = await f.Open(); await Reminder(l, f); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity);
        int index = 0; foreach (var draft in await l.GetPendingCommitmentNotificationsAsync(Identity))
            await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, index++ == 0 ? LongBody(1200) : "second reminder");
        var batch = await SpanWindow(l, f); Check(batch.Outbox.Length >= 2); await AcceptSpanPages(l, batch);
        var snapshot = await l.ReadConversationAsync(Identity); Equal(2, snapshot.CommitmentBudgetReservations.Length);
        Check(snapshot.CommitmentRuns.All(x => x.DeliveryAttempts.Length == 1 && x.Stage == CommitmentRunStage.Completed));
    }
    private static async Task SpanAbsoluteBudget(bool daily)
    {
        await using var f = new F(); var l = await f.Open(options: new() { CommitmentBudgets = daily
            ? new() { MaximumNotificationsPerDay = 1 } : new() { MaximumNotificationsPerMinute = 1 } });
        await Reminder(l, f); await Reminder(l, f); await Reminder(l, f);
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity);
        foreach (var draft in await l.GetPendingCommitmentNotificationsAsync(Identity))
            await l.FinalizeNotificationFallbackAsync(Identity, draft.Id, "distinct reminder with complete source text");
        var source = await CatchInput(l, f); var batch = await l.PrepareCatchUpAsync(Identity, source); Equal(3, batch.Outbox.Length);
        for (int i = 0; i < batch.Outbox.Length; i++)
        {
            if (i > 0)
            {
                await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[i].Id));
                Equal(i, (await l.GetCommitmentsAsync(Identity)).Runs.Sum(x => x.DeliveryAttempts.Length));
                f.Clock.UtcNow += daily ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(1);
            }
            await AcceptDecision(l, batch.Outbox[i]);
        }
        var snapshot = await l.ReadConversationAsync(Identity); Equal(3, snapshot.CommitmentBudgetReservations.Length);
        Check(snapshot.CommitmentRuns.All(x => x.Stage == CommitmentRunStage.Completed && x.DeliveryAttempts.Length == 1));
        await l.DisposeAsync(); await f.Open(options: new() { CommitmentBudgets = daily
            ? new() { MaximumNotificationsPerDay = 1 } : new() { MaximumNotificationsPerMinute = 1 } });
    }
}
