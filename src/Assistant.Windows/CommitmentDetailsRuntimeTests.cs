using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class CommitmentDetailsRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--commitment-details-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--commitment-details-runtime-test")
                throw new ArgumentException("Explicit isolated output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
    }
    private sealed class NoControl : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("No task dispatch permitted in this fixture."); }
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Fresh absolute output required.");
        directory = Path.GetFullPath(directory);
        for (var part = new DirectoryInfo(directory); part != null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "commitment-details-runtime-checks.json"), JsonSerializer.Serialize(new
            { actualHost = true, networkUsed = false, actualModelUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        CommitmentDetailsTests.Run(Check);
        await CurrentDetailsAsync(Path.Combine(directory, "current"), Check);
        await ObservationPagingAsync(Path.Combine(directory, "observation-pages"), Check);
        await UnknownAsync(Path.Combine(directory, "unknown"), Check);
        await CatchUpAsync(Path.Combine(directory, "catchup"), Check);
        await MissedAsync(Path.Combine(directory, "missed"), Check);
    }
    private static async Task CurrentDetailsAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        string goal = "查看隔离详情 " + new string('文', 690) + "😀 完整末尾";
        await InputAsync(f, "create-detail", "两小时后提醒我" + goal);
        var before = await f.ReadAsync(); var work = before.WorkItems.Single(); string query = "事项详情 " + work.Id.ToString("D");
        string first = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        var detail = CommitmentDetails.Read(before, work.Id)!;
        var view = await f.Runtime.ReadWorkspaceAsync();
        check("desktop_detail_and_row_share_exact_current_snapshot_work", view.Identity == before.Identity
            && view.Commitments!.Single().Id == work.Id.ToString("N") && view.CommitmentDetails!.Single() == detail
            && detail.Body.Contains(work.Goal, StringComparison.Ordinal) && detail.Body.Contains("完整末尾"));
        _ = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query); _ = await f.Runtime.ReadWorkspaceAsync();
        check("repeated_direct_reads_are_bitwise_ledger_read_only_and_no_model_or_rpc",
            JsonSerializer.Serialize(before, Settings.Json) == JsonSerializer.Serialize(await f.ReadAsync(), Settings.Json)
            && f.Script.Calls == 0 && control.Calls == 0 && f.Queue.Entries.Count == 0);
        var expectedPages = CommitmentDetails.Split(detail.Body); var actualPayloads = new List<string>();
        string? pageToken = null;
        for (int page = 1; page <= expectedPages.Count; page++)
        {
            string command = query + (page == 1 ? "" : " " + page + " " + pageToken);
            string source = "detail-page-" + page;
            string wire = await InputAsync(f, source, command);
            if (page == 1) pageToken = NextToken(wire);
            string expected = CommitmentDetails.FormatPage(detail, new(work.Id, page, page == 1 ? null : pageToken), clock.UtcNow, pageToken);
            int start = wire.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            actualPayloads.Add(wire.Substring(start, expectedPages[page - 1].Length));
            if (wire != expected || wire.Length > 900) throw new InvalidOperationException("Exact persisted detail page differs from fake HTTP body.");
        }
        var after = await f.ReadAsync();
        check("model_off_query_sends_every_long_detail_page_exactly_once", string.Concat(actualPayloads) == detail.Body
            && f.Script.Calls == 0 && control.Calls == 0 && actualPayloads.Count > 1);
        check("detail_inputs_only_add_their_own_conversation_replies_not_work_or_budget_mutations",
            WorkFacts(before) == WorkFacts(after) && before.Outbox.All(original => after.Outbox.Single(x => x.Id == original.Id) == original)
            && after.Operations.Length == before.Operations.Length && after.Authorizations.Length == before.Authorizations.Length
            && after.ToolInvocations.Length == before.ToolInvocations.Length);
        check("valid_detail_query_is_readonly_but_mixed_cancel_is_not",
            DurableAssistantRuntime.IsKnownReadonlyInput(query)
            && !DurableAssistantRuntime.IsKnownReadonlyInput(query + "；取消事项 " + work.Id));
        string invalid = await InputAsync(f, "partial-id-detail", "事项详情 " + work.Id.ToString("N")[..8]);
        check("partial_guid_does_not_choose_a_record_or_fall_through_to_model", invalid.Contains("完整编号")
            && !invalid.Contains(goal) && f.Script.Calls == 0 && control.Calls == 0);
        string missing = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, "事项详情 " + Guid.NewGuid());
        check("missing_id_does_not_guess_another_work", missing.Contains("当前绑定没有这条事项") && !missing.Contains(goal));
        await f.Runtime.StopSessionAsync();
        await f.Runtime.ActivateAsync(f.Binding, default);
        string resetPage = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query + " 2 " + pageToken);
        check("same_identity_session_reactivation_cannot_revive_old_page_snapshot", resetPage.Contains("分页已失效") && !resetPage.Contains(goal));
        string newPage = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        string oldHash = NextToken(newPage);
        await InputAsync(f, "pause-detail", "暂停提醒 " + work.Id);
        string changed = await InputAsync(f, "changed-page-detail", query + " 2 " + oldHash);
        check("modified_work_cannot_be_joined_to_old_page_sequence", changed.Contains("从第一页")
            && !changed.Contains(goal) && (await f.ReadAsync()).WorkItems.Single().Stage == TrackedWorkStage.Paused);
        string account = f.Settings.ChatGpt.ActiveAccount;
        try
        {
            f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_OTHER_DETAIL_ACCOUNT";
            bool blocked = false;
            try { _ = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query); }
            catch (InvalidOperationException error) when (error.Message == "commitment_details_binding_changed") { blocked = true; }
            check("runtime_detail_rejects_old_identity_after_account_switch", blocked && control.Calls == 0 && f.Script.Calls == 0);
        }
        finally { f.Settings.ChatGpt.ActiveAccount = account; }
    }
    private static async Task ObservationPagingAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
        f.ObservedTask.Title = "Alpha" + new string('文', 300);
        f.ObservedTask.LastActivity = clock.UtcNow.ToUnixTimeSeconds();
        await InputAsync(f, "observe-subscribe", f.ObservedTask.Title + "失败才叫我");
        var before = await f.ReadAsync(); var work = before.WorkItems.Single();
        await f.Ledger.ObserveCommitmentTaskAsync(before.Identity, new(work.Task!, CommitmentTaskState.Running,
            clock.UtcNow, before.Subscriptions.Single().LastActivityAt, true, true, "synthetic-initial-detail-observation"));
        before = await f.ReadAsync();
        string query = "事项详情 " + work.Id; string first = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        var original = CommitmentDetails.Read(await f.ReadAsync(), work.Id)!;
        var at = clock.UtcNow; string hash = NextToken(first);
        clock.Advance(TimeSpan.FromSeconds(1));
        string equalFirst = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        string stillOld = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query + " 2 " + hash);
        check("same_body_new_first_page_does_not_replace_old_token_or_read_time", NextToken(equalFirst) != hash
            && stillOld == CommitmentDetails.FormatPage(original, new(work.Id, 2, hash), at, hash)
            && equalFirst == CommitmentDetails.FormatPage(original, new(work.Id, 1, null), clock.UtcNow, NextToken(equalFirst)));
        clock.Advance(TimeSpan.FromSeconds(2));
        await f.Ledger.ObserveCommitmentTaskAsync(before.Identity, new(work.Task!, CommitmentTaskState.Running,
            clock.UtcNow, before.Subscriptions.Single().LastActivityAt, true, true, "synthetic-detail-observation"));
        var newer = await f.ReadAsync();
        string second = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query + " 2 " + hash);
        File.WriteAllText(Path.Combine(path, "observation-pages-synthetic.json"), JsonSerializer.Serialize(new
        { before = before.Subscriptions, after = newer.Subscriptions, first, second, expected = CommitmentDetails.FormatPage(original, new(work.Id, 2, hash), at, hash) }, Settings.Json));
        check("ordinary_new_observation_preserves_entire_frozen_page_not_a_mix", newer.Subscriptions.Single().LastObservedAt > before.Subscriptions.Single().LastObservedAt
            && newer.WorkItems.Single().Version == work.Version
            && second == CommitmentDetails.FormatPage(original, new(work.Id, 2, hash), at, hash)
            && second.Contains("此页来自当时记录"));
        string refreshed = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        var latest = CommitmentDetails.Read(await f.ReadAsync(), work.Id)!;
        check("new_first_page_uses_latest_persisted_observation", CommitmentDetails.Fingerprint(latest) != CommitmentDetails.Fingerprint(original)
            && refreshed == CommitmentDetails.FormatPage(latest, new(work.Id, 1, null), clock.UtcNow, NextToken(refreshed)));
        string latestHash = NextToken(refreshed);
        clock.Advance(TimeSpan.FromMinutes(5));
        string expired = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query + " 2 " + latestHash);
        check("actual_runtime_expires_old_page_without_model_or_rpc", expired.Contains("分页已失效") && control.Calls == 0 && f.Script.Calls == 0);
        string currentFirst = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query);
        string currentHash = NextToken(currentFirst);
        string account = f.Settings.ChatGpt.ActiveAccount;
        f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_DETAIL_PROFILE_B";
        await f.Runtime.ActivateAsync(f.Binding, default);
        f.Settings.ChatGpt.ActiveAccount = account;
        await f.Runtime.ActivateAsync(f.Binding, default);
        string returned = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, query + " 2 " + currentHash);
        check("identity_a_to_b_to_a_does_not_resurrect_old_page_token", returned.Contains("分页已失效") && control.Calls == 0 && f.Script.Calls == 0);
    }
    private static async Task UnknownAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        await InputAsync(f, "unknown-create", "一分钟后提醒我核对隔离未知结果");
        f.SendOutcome = "{}"; clock.Advance(TimeSpan.FromSeconds(62));
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Kind == OutboxKind.Notification && x.Stage == OutboxStage.Unknown));
        var before = await f.ReadAsync(); var notification = before.Outbox.Single(x => x.Kind == OutboxKind.Notification);
        var work = before.WorkItems.Single(); int oldSends = f.Sent.Count(x => x == notification.Body);
        f.SendOutcome = """{"ret":0}""";
        string query = "事项详情 " + work.Id;
        var detail = CommitmentDetails.Read(before, work.Id)!;
        string? unknownToken = null;
        for (int page = 1; page <= CommitmentDetails.Split(detail.Body).Count; page++)
        {
            string reply = await InputAsync(f, "unknown-detail-" + page, query + (page == 1 ? "" : " " + page + " " + unknownToken));
            if (page == 1 && CommitmentDetails.Split(detail.Body).Count > 1) unknownToken = NextToken(reply);
        }
        var after = await f.ReadAsync();
        check("query_of_unknown_notification_retains_exact_receipt_attempt_and_run", detail.Body.Contains("原通知第 1 页：结果未知，不自动重发")
            && after.Outbox.Single(x => x.Id == notification.Id) == notification && WorkFacts(before) == WorkFacts(after)
            && f.Sent.Count(x => x == notification.Body) == oldSends && oldSends == 1 && f.Script.Calls == 0 && control.Calls == 0);
        var again = await f.Runtime.ReadWorkspaceAsync();
        check("desktop_shows_unknown_without_completing_the_commitment", again.CommitmentDetails!.Single().Body.Contains("结果未知，不自动重发")
            && (await f.ReadAsync()).WorkItems.Single().Stage != TrackedWorkStage.Completed
            && (await f.ReadAsync()).Outbox.Single(x => x.Id == notification.Id) == notification);
    }
    private static async Task MissedAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        await InputAsync(f, "skip-create", "一分钟后提醒我查看跳过原因");
        var work = (await f.ReadAsync()).WorkItems.Single();
        await InputAsync(f, "skip-policy", "设置提醒 " + work.Id + " 错过时跳过");
        var configured = await f.ReadAsync();
        File.WriteAllText(Path.Combine(path, "configured-synthetic.json"), JsonSerializer.Serialize(configured, Settings.Json));
        check("missed_fixture_has_persisted_skip_policy_before_clock_advance", configured.Schedules.Single().MissedPolicy == MissedTriggerPolicy.Skip
            && configured.WorkItems.Single().Stage == TrackedWorkStage.Active);
        clock.Advance(TimeSpan.FromMinutes(3));
        try { await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Any(x => x.Stage == CommitmentRunStage.Skipped)); }
        catch
        {
            File.WriteAllText(Path.Combine(path, "timeout-synthetic.json"), JsonSerializer.Serialize(new { clock.UtcNow, f.Runtime.LastError, snapshot = await f.ReadAsync() }, Settings.Json));
            throw;
        }
        var before = await f.ReadAsync();
        var detail = CommitmentDetails.Read(before, work.Id)!;
        check("actual_missed_skip_detail_uses_saved_due_policy_and_trigger_not_a_guess", detail.Body.Contains("错过原定时间，已按保存的规则跳过。")
            && before.Schedules.Single().MissedPolicy == MissedTriggerPolicy.Skip && before.CommitmentRuns.Single().Stage == CommitmentRunStage.Skipped
            && before.Outbox.All(x => x.Kind != OutboxKind.Notification));
        _ = await f.Runtime.ReadCommitmentDetailsAsync(before.Identity, "事项详情 " + work.Id);
        check("viewing_skipped_record_does_not_catch_up_work_or_consume_budget", WorkFacts(before) == WorkFacts(await f.ReadAsync())
            && (await f.ReadAsync()).Outbox.Length == before.Outbox.Length && f.Script.Calls == 0 && control.Calls == 0);
    }
    private static string NextToken(string page)
    {
        var match = System.Text.RegularExpressions.Regex.Match(page, @"下一页：事项详情 [0-9a-fA-F-]+ [0-9]+ ([0-9A-F]{16})");
        if (!match.Success) throw new InvalidOperationException("Expected persisted next-page token.");
        return match.Groups[1].Value;
    }
    private static string WorkFacts(ConversationSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        snapshot.WorkItems, snapshot.Schedules, snapshot.Subscriptions, snapshot.CommitmentRuns,
        snapshot.CommitmentBudgetReservations, snapshot.NotificationDrafts,
        notices = snapshot.Outbox.Where(x => x.Kind == OutboxKind.Notification).ToArray()
    }, Settings.Json);
    private static async Task CatchUpAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        await InputAsync(f, "catchup-create", "一分钟后提醒我核对详情的补报关联");
        f.SendOutcome = """{"ret":-2}"""; clock.Advance(TimeSpan.FromSeconds(62));
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Kind == OutboxKind.Notification && x.Stage == OutboxStage.Rejected));
        var rejected = await f.ReadAsync(); var original = rejected.Outbox.Single(x => x.Kind == OutboxKind.Notification);
        f.SendOutcome = """{"ret":0}""";
        int sendsBefore = f.Sent.Count;
        await InputAsync(f, "detail-must-hold-old-notice", "事项详情 " + rejected.WorkItems.Single().Id);
        var held = await f.ReadAsync();
        check("fresh_detail_input_holds_history_and_sends_only_its_own_reply", held.CatchUpBatches.Length == rejected.CatchUpBatches.Length
            && WorkFacts(held) == WorkFacts(rejected) && held.Outbox.Single(x => x.Id == original.Id) == original
            && f.Sent.Count == sendsBefore + 1 && f.Sent.Last().StartsWith("事项 ", StringComparison.Ordinal)
            && held.CatchUpWindows.Last().Input.Disposition == CatchUpDisposition.HoldHistory);
        await InputAsync(f, "fresh-context-for-detail", "状态");
        await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Single().Stage == CommitmentRunStage.Completed);
        var saved = await f.ReadAsync(); var work = saved.WorkItems.Single();
        var detail = CommitmentDetails.Read(saved, work.Id)!;
        var view = await f.Runtime.ReadWorkspaceAsync();
        check("actual_catchup_details_preserve_original_rejection_and_report_precise_complete_coverage",
            saved.Outbox.Single(x => x.Id == original.Id) == original && saved.CatchUpBatches.Length == 1
            && detail.Body.Contains("原通知第 1 页：渠道明确拒绝")
            && detail.Body.Contains("正文受理覆盖：" + original.Body.Length + " / " + original.Body.Length + " 字符；剩余 0 字符")
            && view.CommitmentDetails!.Single() == detail && f.Script.Calls == 0 && control.Calls == 0);
        _ = await f.Runtime.ReadCommitmentDetailsAsync(saved.Identity, "事项详情 " + work.Id);
        check("viewing_catchup_lineage_does_not_spend_another_delivery_budget_or_rewrite_any_outbox",
            WorkFacts(saved) == WorkFacts(await f.ReadAsync())
            && saved.Outbox.SequenceEqual((await f.ReadAsync()).Outbox));
    }
    private static async Task<string> InputAsync(DurableRuntimeTests.Fixture fixture, string source, string body)
    {
        await fixture.InputAsync(source, body);
        await OwnedHostRuntimeTests.WaitReplyAsync(fixture, source);
        var state = await fixture.ReadAsync();
        var input = state.Inbox.Single(x => x.SourceMessageId == WeChatText.Hash(source));
        var pages = state.Outbox.Where(x => x.GenerationId == input.GenerationId).OrderBy(x => x.SegmentIndex).ToArray();
        if (pages.Length == 0 || pages.Any(x => x.Stage != OutboxStage.Accepted)) throw new InvalidOperationException("Reply not accepted.");
        // The fixture's Sent queue is populated only by its actual fake HTTP handler.
        if (pages.Any(page => !fixture.Sent.Contains(page.Body))) throw new InvalidOperationException("Persisted reply did not reach fake HTTP.");
        return string.Concat(pages.Select(x => x.Body));
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic commitment detail condition.");
            await Task.Delay(20);
        }
    }
}
