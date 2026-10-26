using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class HistoryPaginationRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--history-pagination-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--history-pagination-runtime-test")
                throw new ArgumentException("Fresh explicit output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero); }
    private sealed class NoControl : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No control permitted in history tests."); }
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Fresh absolute output required.");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        Settings.SetDataDirectory(Path.Combine(directory, "isolated-settings"));
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "history-pagination-runtime-checks.json"), JsonSerializer.Serialize(new
            { actualHost = true, actualProtectedLedger = true, networkUsed = false, actualModelUsed = false,
                productionDataRead = false, count = checks.Count, failures = checks.Count(x => !x.Value), checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await ActualPagesAsync(Path.Combine(directory, "actual-pages"), Check);
        await SessionBoundaryAsync(Path.Combine(directory, "session-boundary"), Check);
    }
    private static async Task ActualPagesAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control);
        await Task.Delay(100); // Let the empty activation workers finish their initial read.
        var identity = f.Runtime.Identity!;
        var target = new TaskReference("synthetic-host", "synthetic-history-thread", "synthetic-history-turn", "synthetic-profile");
        var outputs = new Dictionary<Guid, OutboxStage>();
        foreach (var stage in new[] { OutboxStage.Accepted, OutboxStage.Unknown })
        {
            var notice = await f.Ledger.EnqueueNotificationAsync(identity, "synthetic-history-" + stage,
                "保存的完整通知原文\n😀 " + stage, target, outputDependencies: OutputDependencies.Independent);
            var message = notice.Outbox.Single();
            await f.Ledger.BeginOutboxSendAsync(identity, message.Id);
            await f.Ledger.RecordOutboxOutcomeAsync(identity, message.Id, stage, "SYNTHETIC_SAVED_FACT_NO_HTTP");
            outputs.Add(message.Id, stage);
        }
        var baseline = clock.UtcNow;
        // Real API writes and real DPAPI storage, not an in-memory replacement of
        // the reader's snapshot. Repeated timestamps exercise the Id tiebreaker.
        for (int index = 0; index < 411; index++)
        {
            clock.UtcNow = baseline.AddSeconds(10 + index / 7);
            await f.Ledger.ReceiveAsync(identity, "history-seed-" + index,
                "原始记录 " + index + "\n第二行保留 😀 " + (index == 3 ? new string('文', 1700) : "末尾"),
                MessageTrust.AuthenticatedUser, clock.UtcNow, task: index == 3 ? target : null);
        }
        var saved = await f.ReadAsync();
        var workspace = await f.Runtime.ReadWorkspaceAsync();
        var first = workspace.ConversationPage!;
        var expected = saved.Inbox.Select(item => (item.Id, Time: item.ReceivedAt, item.Body))
            .Concat(saved.Outbox.Select(item => (item.Id, Time: item.CreatedAt, item.Body)))
            .OrderByDescending(item => item.Time).ThenBy(item => item.Id.ToString("N"), StringComparer.Ordinal).ToArray();
        check("actual_413_receipts_default_page_is_exact_newest_200", first.Rows.Count == 200
            && first.TotalCount == 413 && first.StartIndex == 1 && first.EndIndex == 200
            && first.Rows.Select(row => row.Id).SequenceEqual(expected.Take(200).Select(item => item.Id.ToString("N")))
            && ReferenceEquals(first.Identity, identity) && ReferenceEquals(first.Rows, workspace.Conversation));
        var second = await f.Runtime.ReadConversationPageAsync(first.Identity!, first.OlderCursor);
        var third = await f.Runtime.ReadConversationPageAsync(second.Identity!, second.OlderCursor);
        var all = first.Rows.Concat(second.Rows).Concat(third.Rows).ToArray();
        var exported = await f.Runtime.ReadConversationExportAsync(identity, default);
        check("export_reads_all_receipts_from_one_snapshot_beyond_page_limit", exported.Rows.Count == 413
            && exported.OlderCursor == null && exported.Rows.Select(row => row.Id).SequenceEqual(expected.Select(item => item.Id.ToString("N")))
            && exported.Rows.Zip(expected).All(pair => pair.First.Body == pair.Second.Body));
        bool rejectedExport = false;
        try { await f.Runtime.ReadConversationExportAsync(identity with { }, default); }
        catch (InvalidOperationException) { rejectedExport = true; }
        check("export_rejects_old_session_identity_object", rejectedExport);
        var search = await f.Runtime.SearchConversationAsync(identity, "原始记录", null, default);
        var searchOlder = await f.Runtime.SearchConversationAsync(identity, "原始记录", search.OlderCursor, default);
        var searchOldest = await f.Runtime.SearchConversationAsync(identity, "原始记录", searchOlder.OlderCursor, default);
        check("search_matches_all_history_with_bounded_stable_pages", search.TotalCount == 411 && search.Rows.Count == 200
            && searchOlder.Rows.Count == 200 && searchOldest.Rows.Count == 11 && searchOldest.OlderCursor == null
            && search.Rows.Concat(searchOlder.Rows).Concat(searchOldest.Rows).Select(row => row.Id).Distinct().Count() == 411);
        var exactSearchRow = all.Single(row => row.Body.StartsWith("原始记录 3\n", StringComparison.Ordinal));
        var byId = await f.Runtime.SearchConversationAsync(identity, exactSearchRow.Id.ToUpperInvariant(), null, default);
        var deepBody = await f.Runtime.SearchConversationAsync(identity, new string('文', 1700), null, default);
        check("search_finds_complete_id_and_body_beyond_preview", byId.Rows.Single().Id == exactSearchRow.Id && deepBody.Rows.Single().Id == exactSearchRow.Id);
        bool wrongQuery = false;
        try { await f.Runtime.SearchConversationAsync(identity, "不同条件", search.OlderCursor, default); }
        catch (InvalidOperationException) { wrongQuery = true; }
        check("search_cursor_cannot_be_reused_for_different_query", wrongQuery);
        var noMatches = await f.Runtime.SearchConversationAsync(identity, "NO_MATCH_SYNTHETIC", null, default);
        check("search_empty_result_has_no_fabricated_rows_or_cursor", noMatches.Rows.Count == 0 && noMatches.TotalCount == 0 && noMatches.OlderCursor == null);
        check("three_bounded_pages_have_no_duplicate_or_missing_receipts_including_ties", second.Rows.Count == 200
            && second.StartIndex == 201 && second.EndIndex == 400 && third.Rows.Count == 13
            && third.StartIndex == 401 && third.EndIndex == 413 && third.OlderCursor == null
            && all.Select(row => row.Id).SequenceEqual(expected.Select(item => item.Id.ToString("N")))
            && all.Select(row => row.Id).Distinct().Count() == 413);
        check("all_original_multiline_unicode_bodies_and_saved_sources_are_preserved", all.Zip(expected).All(pair =>
            pair.First.Body == pair.Second.Body && pair.First.Detail.StartsWith(pair.Second.Body + "\n\n", StringComparison.Ordinal))
            && all.Single(row => row.Body.StartsWith("原始记录 3\n")).Body.Length > 1700
            && all.Where(row => !outputs.ContainsKey(Guid.Parse(row.Id))).All(row => row.Speaker == "你" && row.TimeLabel == "接收时间")
            && all.Where(row => outputs.ContainsKey(Guid.Parse(row.Id))).All(row => row.Speaker == "助手通知"));
        check("deep_page_keeps_unknown_and_interface_acceptance_distinct", all.Single(row => outputs.GetValueOrDefault(Guid.Parse(row.Id)) == OutboxStage.Unknown)
            .State == "发送结果未知，不自动重发"
            && all.Single(row => outputs.GetValueOrDefault(Guid.Parse(row.Id)) == OutboxStage.Accepted).State == "渠道接口已受理，手机送达未核实");
        check("every_page_has_its_own_read_time_without_mutating_original_workspace_page", first.ReadAt <= second.ReadAt
            && second.ReadAt <= third.ReadAt && workspace.ConversationPage == first && first.Rows.Count == 200);
        check("desktop_pagination_does_not_write_ledger_or_invoke_model_channel_control", JsonSerializer.Serialize(saved, Settings.Json)
            == JsonSerializer.Serialize(await f.ReadAsync(), Settings.Json) && f.Script.Calls == 0 && f.Sent.Count == 0
            && control.Calls == 0 && f.Queue.Entries.Count == 0);

        clock.UtcNow = baseline.AddHours(1);
        await f.Ledger.ReceiveAsync(identity, "arrived-after-first-page", "本页读取之后的新消息", MessageTrust.AuthenticatedUser, clock.UtcNow);
        var afterInsert = await f.ReadAsync();
        var continued = await f.Runtime.ReadConversationPageAsync(first.Identity!, first.OlderCursor);
        check("new_inbound_does_not_shift_old_cursor_or_repeat_previous_page", continued.StartIndex == 202
            && continued.EndIndex == 401 && continued.TotalCount == 414
            && continued.Rows.Select(row => row.Id).SequenceEqual(second.Rows.Select(row => row.Id))
            && !continued.Rows.Any(row => first.Rows.Any(prior => prior.Id == row.Id)));
        var latest = await f.Runtime.ReadConversationPageAsync(first.Identity!, null);
        check("return_recent_reads_latest_snapshot_and_retains_original_page_objects", latest.Rows[0].Body == "本页读取之后的新消息"
            && latest.StartIndex == 1 && latest.EndIndex == 200 && latest.TotalCount == 414
            && !first.Rows.Any(row => row.Body == "本页读取之后的新消息") && latest.ReadAt >= continued.ReadAt);
        bool badIdentity = false, badAnchor = false, malformed = false, cancelled = false;
        try { _ = await f.Runtime.ReadConversationPageAsync(new("other", "other", "main"), first.OlderCursor); }
        catch (InvalidOperationException) { badIdentity = true; }
        try { _ = await f.Runtime.ReadConversationPageAsync(identity, first.OlderCursor! with { BeforeId = Guid.NewGuid().ToString("N") }); }
        catch (InvalidOperationException error) when (error.Message == "history_cursor_anchor_unavailable") { badAnchor = true; }
        try { _ = await f.Runtime.ReadConversationPageAsync(identity, first.OlderCursor! with { BeforeId = "bad-id" }); }
        catch (InvalidOperationException error) when (error.Message == "history_cursor_identity_or_format_invalid") { malformed = true; }
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            try { _ = await f.Runtime.ReadConversationPageAsync(identity, first.OlderCursor, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        check("foreign_cursor_missing_anchor_malformed_and_cancelled_reads_fail_closed", badIdentity && badAnchor && malformed && cancelled);
        check("all_rejected_reads_leave_latest_records_and_adapters_unchanged", JsonSerializer.Serialize(afterInsert, Settings.Json)
            == JsonSerializer.Serialize(await f.ReadAsync(), Settings.Json) && f.Script.Calls == 0 && f.Sent.Count == 0 && control.Calls == 0);
        var empty = afterInsert with { Inbox = [], Outbox = [] };
        var emptyPage = AssistantConversationHistory.ReadPage(empty, _ => throw new InvalidOperationException(), clock.UtcNow);
        var exact = afterInsert with { Inbox = afterInsert.Inbox.Take(200).ToImmutableArray(), Outbox = [] };
        var exactPage = AssistantConversationHistory.ReadPage(exact, _ => throw new InvalidOperationException(), clock.UtcNow);
        check("empty_and_exact_200_boundaries_do_not_offer_an_empty_older_page", emptyPage.Rows.Count == 0 && emptyPage.TotalCount == 0
            && emptyPage.StartIndex == 0 && emptyPage.EndIndex == 0 && emptyPage.OlderCursor == null
            && exactPage.Rows.Count == 200 && exactPage.OlderCursor == null);
    }
    private static async Task SessionBoundaryAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false);
        var old = (await f.Runtime.ReadWorkspaceAsync()).ConversationPage!;
        string account = f.Settings.ChatGpt.ActiveAccount;
        f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_HISTORY_ACCOUNT_B";
        bool blocked = false;
        try { _ = await f.Runtime.ReadConversationPageAsync(old.Identity!, null); }
        catch (InvalidOperationException error) when (error.Message == "workspace_binding_changed") { blocked = true; }
        check("settings_identity_change_blocks_page_before_runtime_reactivation", blocked);
        await f.Runtime.ActivateAsync(f.Binding, default);
        var other = await f.Runtime.ReadWorkspaceAsync();
        check("new_identity_page_does_not_reuse_prior_account_records", other.Identity != old.Identity
            && other.ConversationPage!.TotalCount == 0 && other.ConversationPage.Rows.Count == 0);
        f.Settings.ChatGpt.ActiveAccount = account;
        await f.Runtime.ActivateAsync(f.Binding, default);
        blocked = false;
        try { _ = await f.Runtime.ReadConversationPageAsync(old.Identity!, null); }
        catch (InvalidOperationException error) when (error.Message == "workspace_binding_changed") { blocked = true; }
        var current = (await f.Runtime.ReadWorkspaceAsync()).ConversationPage!;
        check("A_B_A_new_session_does_not_revive_old_page_identity_reference", blocked
            && current.Identity == old.Identity && !ReferenceEquals(current.Identity, old.Identity));
        var fresh = await f.Runtime.ReadConversationPageAsync(current.Identity!, null);
        check("explicit_new_workspace_read_restores_current_session_paging", ReferenceEquals(fresh.Identity, current.Identity)
            && fresh.Rows.Count == 0 && f.Script.Calls == 0 && f.Sent.Count == 0);
    }
}
