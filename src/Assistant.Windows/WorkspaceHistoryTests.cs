using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class WorkspaceHistoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--workspace-history-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--workspace-history-test")
                throw new ArgumentException("Explicit isolated output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Fresh output required.");
        for (var part = new DirectoryInfo(directory); part != null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool condition)
        {
            checks.Add(name, condition);
            File.WriteAllText(Path.Combine(directory, "workspace-history-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, actualModelUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!condition) throw new InvalidOperationException(name);
        }
        await ActualRecordsAsync(Path.Combine(directory, "actual"), Check);
        await UnknownAsync(Path.Combine(directory, "unknown"), Check);
        await WindowGuardsAsync(Check);
    }
    private static async Task ActualRecordsAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory);
        const string input = "请解释这条合成历史记录\n保留原文 😀";
        string body = new string('文', 170) + " 😀\n完整回复的末尾也应保留。";
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(body));
        await f.InputAsync("history-original", input);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "history-original");
        var before = await f.ReadAsync();
        int modelCalls = f.Script.Calls, sends = f.Sent.Count;
        var workspace = await f.Runtime.ReadWorkspaceAsync();
        var incoming = workspace.Conversation!.Single(x => x.Speaker == "你");
        var outgoing = workspace.Conversation!.Single(x => x.Speaker == "助手回复");
        check("history_uses_actual_inbox_identity_original_body_and_receipt_time",
            workspace.Identity == f.Runtime.Identity && workspace.ConversationRecordCount == 2
            && incoming.Id == before.Inbox.Single().Id.ToString("N") && incoming.Body == input
            && incoming.CreatedAt == before.Inbox.Single().ReceivedAt && incoming.TimeLabel == "接收时间");
        check("history_preserves_full_outbox_body_and_distinguishes_api_acceptance",
            outgoing.Id == before.Outbox.Single().Id.ToString("N") && outgoing.Body == body
            && outgoing.State.Contains("渠道接口已受理") && outgoing.State.Contains("手机送达未核实")
            && outgoing.CreatedAt == before.Outbox.Single().CreatedAt && outgoing.TimeLabel == "准备时间"
            && outgoing.Preview.Length < body.Length && outgoing.Detail.StartsWith(body + "\n\n", StringComparison.Ordinal));
        _ = await f.Runtime.ReadWorkspaceAsync();
        var after = await f.ReadAsync();
        check("repeated_desktop_reads_do_not_mutate_execution_or_call_external_adapters",
            JsonSerializer.Serialize(before, Settings.Json) == JsonSerializer.Serialize(after, Settings.Json)
            && f.Script.Calls == modelCalls && f.Sent.Count == sends && after.Operations.IsEmpty);

        var longPreview = incoming with { Body = new string('x', 159) + "😀tail" };
        check("preview_never_splits_surrogate_and_full_body_is_unchanged",
            longPreview.Preview == new string('x', 159) + "…" && longPreview.Body.EndsWith("😀tail"));
        var many = before with
        {
            Inbox = Enumerable.Range(0, 205).Select(index => before.Inbox.Single() with
            {
                Id = Guid.NewGuid(), SourceMessageId = "synthetic-" + index,
                ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(index)
            }).ToImmutableArray(),
            Outbox = []
        };
        var bounded = AssistantConversationHistory.Read(many, _ => throw new InvalidOperationException());
        check("history_limit_is_view_only_and_retains_newest_exact_records",
            bounded.Count == 200 && many.Inbox.Length == 205
            && bounded.First().CreatedAt == DateTimeOffset.UnixEpoch.AddSeconds(204)
            && bounded.Last().CreatedAt == DateTimeOffset.UnixEpoch.AddSeconds(5)
            && bounded.All(row => row.Body == input));
        string account = f.Settings.ChatGpt.ActiveAccount;
        try
        {
            f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_OTHER_HISTORY_ACCOUNT";
            bool rejected = false;
            try { _ = await f.Runtime.ReadWorkspaceAsync(); }
            catch (InvalidOperationException error) when (error.Message == "workspace_binding_changed") { rejected = true; }
            catch (OperationCanceledException error) when (error.Message == "当前微信绑定已变化。") { rejected = true; }
            check("actual_workspace_rejects_changed_binding_before_exposing_old_rows", rejected
                && (await f.Ledger.ReadConversationAsync(before.Identity)).Inbox.Single().Body == input);
        }
        finally { f.Settings.ChatGpt.ActiveAccount = account; }
    }
    private static async Task UnknownAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, channelRequestOverride: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") } : null));
        await f.InputAsync("history-unknown", "状态");
        var until = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!(await f.ReadAsync()).Outbox.Any(x => x.Stage == OutboxStage.Unknown))
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic unknown receipt missing.");
            await Task.Delay(20);
        }
        var before = await f.ReadAsync(); var workspace = await f.Runtime.ReadWorkspaceAsync();
        var row = workspace.Conversation!.Single(x => x.Speaker == "助手回复");
        check("unknown_message_is_visible_without_claiming_delivery_or_retrying",
            row.Body == before.Outbox.Single().Body && row.State == "发送结果未知，不自动重发"
            && row.Id == before.Outbox.Single().Id.ToString("N")
            && (await f.ReadAsync()).Outbox.Single() == before.Outbox.Single() && f.Script.Calls == 0);
    }
    private static async Task WindowGuardsAsync(Action<string, bool> check)
    {
        var first = new ConversationIdentity("synthetic-history-a", "binding-a", "main");
        var second = new ConversationIdentity("synthetic-history-b", "binding-b", "main");
        ConversationIdentity current = first;
        static AssistantWorkspaceSnapshot Snapshot(ConversationIdentity identity, string text) => new(
            text, text, text, DateTimeOffset.UtcNow,
            [new(text, text, text, text, text)], [new(text, text, text, text, text)], [new(text, text, text, text, text)],
            [new(text, text, text, text, text)],
            [new(text, "你", "已持久接收", text, text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "接收时间")],
            identity, 1);
        const string privateA = "SYNTHETIC_PRIVATE_HISTORY_A";
        var snapshot = Snapshot(first, privateA);
        var window = new AssistantWorkWindow(_ => Task.FromResult(snapshot), identity => identity == current);
        try
        {
            await window.RefreshAsync();
            window.ConversationGrid.SelectedIndex = 0;
            check("history_window_displays_full_selected_text_as_readonly",
                window.ConversationGrid.IsReadOnly && window.ConversationDetailBox.IsReadOnly
                && window.ConversationDetailBox.Text.StartsWith(privateA + "\n\n") && window.ConversationGrid.Items.Count == 1);
            current = second; window.InvalidateIfIdentityChanged();
            check("identity_change_clears_every_old_record_and_selected_body",
                window.WorkGrid.Items.Count == 0 && window.OperationsGrid.Items.Count == 0 && window.MessagesGrid.Items.Count == 0
                && window.CommitmentsGrid.Items.Count == 0 && window.ConversationGrid.Items.Count == 0
                && !window.ConversationDetailBox.Text.Contains(privateA) && !window.ContextText.Text.Contains(privateA)
                && window.CapabilitiesText.Text.Length == 0 && window.ReadAtText.Text.Length == 0);
            snapshot = Snapshot(second, "SYNTHETIC_CURRENT_HISTORY_B");
            await window.RefreshAsync(); window.ConversationGrid.SelectedIndex = 0;
            check("explicit_refresh_after_switch_only_displays_current_identity",
                window.ConversationGrid.Items.Count == 1 && window.ConversationDetailBox.Text.Contains("SYNTHETIC_CURRENT_HISTORY_B")
                && !window.ConversationDetailBox.Text.Contains(privateA));
        }
        finally { window.Close(); }
        current = first;
        var delayed = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new AssistantWorkWindow(_ => delayed.Task, identity => identity == current);
        try
        {
            Task read = slow.RefreshAsync();
            current = second; slow.InvalidateIfIdentityChanged();
            delayed.SetResult(Snapshot(first, privateA)); await read;
            check("late_read_from_old_identity_cannot_repopulate_a_cleared_window",
                slow.ConversationGrid.Items.Count == 0 && slow.WorkGrid.Items.Count == 0
                && !slow.ConversationDetailBox.Text.Contains(privateA) && !slow.ContextText.Text.Contains(privateA));
        }
        finally { slow.Close(); }
        bool failRead = false;
        var failing = new AssistantWorkWindow(_ => failRead
            ? Task.FromException<AssistantWorkspaceSnapshot>(new IOException("SYNTHETIC_PRIVATE_INTERNAL_ERROR"))
            : Task.FromResult(Snapshot(current, privateA)), identity => identity == current);
        try
        {
            await failing.RefreshAsync(); failing.ConversationGrid.SelectedIndex = 0;
            failRead = true; await failing.RefreshAsync();
            check("failed_refresh_clears_old_text_and_does_not_expose_internal_error",
                failing.ConversationGrid.Items.Count == 0 && failing.WorkGrid.Items.Count == 0
                && !failing.ConversationDetailBox.Text.Contains(privateA)
                && !failing.DiagnosticText.Text.Contains("SYNTHETIC_PRIVATE_INTERNAL_ERROR")
                && failing.RefreshButton.IsEnabled);
        }
        finally { failing.Close(); }
        var closedRead = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new AssistantWorkWindow(_ => closedRead.Task, identity => identity == current);
        Task loading = closed.RefreshAsync(); closed.Close();
        closedRead.SetResult(Snapshot(current, privateA)); await loading;
        check("closed_window_does_not_publish_late_read_results",
            closed.ConversationGrid.Items.Count == 0 && !closed.ConversationDetailBox.Text.Contains(privateA));
    }
}
