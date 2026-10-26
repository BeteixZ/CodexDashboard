using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantHealthTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--assistant-health-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--assistant-health-test")
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
        void Check(string name, bool pass)
        {
            checks.Add(name, pass);
            File.WriteAllText(Path.Combine(directory, "assistant-health-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, actualModelUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!pass) throw new InvalidOperationException(name);
        }
        await ActualRuntimeAsync(Path.Combine(directory, "actual"), Check);
        await WindowGuardsAsync(Check);
        await SessionReadGuardsAsync(Path.Combine(directory, "session"), Check);
    }
    private static async Task ActualRuntimeAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, channelRequestOverride: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") } : null));
        const string privateBody = "SYNTHETIC_PRIVATE_HEALTH_REPLY";
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(privateBody));
        await f.InputAsync("health-original", "请解释这条隔离的诊断检查");
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!(await f.ReadAsync()).Outbox.Any(item => item.Stage == OutboxStage.Unknown))
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Synthetic unknown receipt missing.");
            await Task.Delay(20);
        }
        var before = await f.ReadAsync(); int calls = f.Script.Calls, sends = f.Sent.Count;
        var channelBefore = f.Channel.ConnectionState;
        var first = await f.Runtime.ReadHealthAsync(); var channel = f.Channel.ReadHealth();
        var second = await f.Runtime.ReadHealthAsync(); _ = f.Channel.ReadHealth();
        var after = await f.ReadAsync();
        check("health_reads_actual_unknown_receipt_without_relabeling_as_delivered",
            first.Identity == f.Runtime.Identity && first.State == AssistantLedgerHealthState.Available
            && first.UnknownMessages == 1 && first.PendingMessages == 0 && first.SendingMessages == 0
            && first.UnknownOperations == 0 && before.Outbox.Single().Body == privateBody);
        check("repeated_health_reads_preserve_ledger_and_do_not_generate_send_or_control",
            first == second && JsonSerializer.Serialize(before, Settings.Json) == JsonSerializer.Serialize(after, Settings.Json)
            && f.Script.Calls == calls && f.Sent.Count == sends && f.Queue.Entries.Count == 0
            && channel.State == channelBefore && f.Channel.ConnectionState == channelBefore);
        check("health_projection_contains_no_conversation_text_or_protocol_binding",
            !JsonSerializer.Serialize(first, Settings.Json).Contains(privateBody, StringComparison.Ordinal)
            && !JsonSerializer.Serialize(channel, Settings.Json).Contains(f.Binding.BotId, StringComparison.Ordinal)
            && !JsonSerializer.Serialize(channel, Settings.Json).Contains(f.Binding.UserId, StringComparison.Ordinal));
        check("health_capacity_is_whole_ledger_bounded_read_only_diagnostic",
            first.Capacity is { LogicalSnapshotBytes: > 0, MaximumSnapshotBytes: 8388608, IsReadOnly: false }
            && first.Capacity.JournalSequence > 0 && first.Capacity == second.Capacity);
        string account = f.Settings.ChatGpt.ActiveAccount;
        try
        {
            f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_OTHER_HEALTH_ACCOUNT";
            bool refused = false;
            try { _ = await f.Runtime.ReadHealthAsync(); }
            catch (InvalidOperationException error) when (error.Message == "health_binding_changed") { refused = true; }
            check("health_read_rejects_changed_binding_without_exposing_old_counts", refused
                && f.Script.Calls == calls && f.Sent.Count == sends);
        }
        finally { f.Settings.ChatGpt.ActiveAccount = account; }
    }
    internal static AssistantHealthSnapshot Sample(ConversationIdentity? identity = null, string directory = "SYNTHETIC_DATA_DIRECTORY")
    {
        var at = DateTimeOffset.Parse("2026-10-20T21:00:00Z");
        return new(at, new(Codex.Protocol.ConnectionState.Degraded, true, true, true, false),
            true, true, 3, at.AddMinutes(-15), at.AddMinutes(-2), 2, 2, 1, 8, at.AddMinutes(-20), true,
            new(identity, AssistantLedgerHealthState.Available, WaitingInputs: 1, UnknownMessages: 2, UnknownOperations: 1,
                Capacity: new(7 * 1024 * 1024, 8 * 1024 * 1024, 123, false)),
            directory, new(3, 2, 1, 0, true));
    }
    private static async Task SessionReadGuardsAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false);
        await Task.Delay(100);
        var original = f.Runtime.Identity!;
        var gate = (SemaphoreSlim)typeof(DurableConversationLedger)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Ledger)!;
        Task<AssistantLedgerHealth>? reading = null;
        Task? activating = null;
        await gate.WaitAsync();
        try
        {
            reading = f.Runtime.ReadHealthAsync();
            activating = f.Runtime.ActivateAsync(f.Binding, CancellationToken.None);
            var until = DateTimeOffset.UtcNow.AddSeconds(5);
            while (ReferenceEquals(f.Runtime.Identity, original))
            {
                if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic activation did not reach its identity boundary.");
                await Task.Delay(10);
            }
        }
        finally { gate.Release(); }
        bool rejected = false;
        try { _ = await reading!.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException error) when (error.Message == "health_binding_changed") { rejected = true; }
        await activating!.WaitAsync(TimeSpan.FromSeconds(5));
        check("same_binding_reactivation_rejects_health_read_started_under_old_session", rejected
            && original == f.Runtime.Identity && !ReferenceEquals(original, f.Runtime.Identity));
        var fresh = await f.Runtime.ReadHealthAsync();
        check("fresh_health_read_uses_current_session_without_external_calls", ReferenceEquals(fresh.Identity, f.Runtime.Identity)
            && fresh.UnknownMessages == 0 && f.Script.Calls == 0 && f.Sent.Count == 0 && f.Queue.Entries.Count == 0);

        var captured = Sample(fresh.Identity);
        var window = new AssistantHealthWindow(_ => Task.FromResult(captured), f.Runtime.IsCurrentHealthIdentity);
        try
        {
            await window.RefreshAsync();
            await f.Runtime.ActivateAsync(f.Binding, CancellationToken.None);
            window.InvalidateIfIdentityChanged();
            check("same_binding_new_session_clears_displayed_health_and_disables_export",
                window.RecordsText.Text == "" && window.DetailsBox.Text == "" && !window.ExportButton.IsEnabled
                && window.ExportSnapshot(() => throw new InvalidOperationException("stale_export_must_not_open_picker"))
                    == AssistantHealthExportResult.SnapshotChanged);
            captured = Sample((await f.Runtime.ReadHealthAsync()).Identity);
            await window.RefreshAsync();
            check("explicit_refresh_restores_health_for_the_current_activation",
                window.RecordsText.Text.Length > 0 && window.ExportButton.IsEnabled);
        }
        finally { window.Close(); }
    }
    private static async Task WindowGuardsAsync(Action<string, bool> check)
    {
        var a = new ConversationIdentity("synthetic-health-A", "binding-A", "main");
        var b = new ConversationIdentity("synthetic-health-B", "binding-B", "main");
        ConversationIdentity current = a; var snapshot = Sample(a);
        bool fail = false;
        var window = new AssistantHealthWindow(_ => fail
            ? Task.FromException<AssistantHealthSnapshot>(new IOException("SYNTHETIC_INTERNAL_EXCEPTION_BODY"))
            : Task.FromResult(snapshot), identity => identity == current);
        try
        {
            await window.RefreshAsync();
            check("capacity_ui_distinguishes_logical_size_disk_and_future_save_guarantees",
                window.RecordsText.Text.Contains("7.00 MiB / 8.00 MiB")
                && window.RecordsText.Text.Contains("整份账本，包含其他绑定")
                && window.RecordsText.Text.Contains("接近容量上限")
                && window.RecordsText.Text.Contains("逻辑大小不是磁盘占用")
                && window.RecordsText.Text.Contains("剩余空间不保证下一次操作可保存"));
            check("readonly_capacity_does_not_claim_latest_disk_state_or_zero_for_missing",
                (snapshot with { Ledger = snapshot.Ledger! with { Capacity = new(1024, 8388608, 12, true) } })
                    .CapacitySummary.Contains("尚不能确认磁盘中的最后一次写入")
                && (snapshot with { Ledger = snapshot.Ledger! with { Capacity = new(1024, 8388608, 12, true) } })
                    .CapacitySummary.Contains("1.0 KiB")
                && (snapshot with { Ledger = snapshot.Ledger! with { Capacity = null } }).CapacitySummary == "");
            var archived = snapshot with { Ledger = snapshot.Ledger! with { Capacity = new(9 * 1048576, 64 * 1048576, 12, false, 1024, 8 * 1048576, new(3L * 1048576, 4, 2, 128)) } };
            check("archive_health_distinguishes_manifest_and_backup_dataset", archived.CapacitySummary.Contains("9.00 MiB / 64.00 MiB")
                && archived.CapacitySummary.Contains("1.0 KiB / 8.00 MiB") && archived.CapacitySummary.Contains("history-archive") && archived.CapacitySummary.Contains("3.00 MiB")
                && archived.CapacitySummary.Contains("已登记卷 2 / 128") && archived.CapacitySummary.Contains("不含主账本"));
            check("diagnostic_view_keeps_technical_details_collapsed_and_does_not_claim_end_to_end_success",
                window.DetailsExpander.IsExpanded == false && window.DetailsBox.IsReadOnly
                && window.RecordsText.Text.Contains("发送结果未知 2 条")
                && window.DetailsBox.Text.Contains("本页未验证端到端连接")
                && window.ConcurrencyText.Text.Contains("占用不等于正在运行")
                && window.ConnectionText.Text.Contains("启用状态不代表")
                && window.SourcesText.Text.Contains(snapshot.LastCollectionAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")));
            current = b; window.InvalidateIfIdentityChanged();
            check("identity_change_clears_every_old_diagnostic_panel",
                window.ConnectionText.Text == "" && window.RecordsText.Text == "" && window.ConcurrencyText.Text == ""
                && window.SourcesText.Text == "" && window.ReadAtText.Text == "" && window.DetailsBox.Text == "");
            snapshot = Sample(b); await window.RefreshAsync();
            fail = true; await window.RefreshAsync();
            check("failed_read_clears_stale_counts_without_showing_raw_exception",
                window.RecordsText.Text == "" && window.DetailsBox.Text == "" && window.RefreshButton.IsEnabled
                && !window.ResultText.Text.Contains("SYNTHETIC_INTERNAL_EXCEPTION_BODY"));
        }
        finally { window.Close(); }
        current = a;
        var delayed = new TaskCompletionSource<AssistantHealthSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new AssistantHealthWindow(_ => delayed.Task, identity => identity == current);
        try
        {
            Task loading = slow.RefreshAsync(); current = b; slow.InvalidateIfIdentityChanged();
            delayed.SetResult(Sample(a)); await loading;
            check("late_diagnostic_read_cannot_publish_previous_identity", slow.RecordsText.Text == "" && slow.DetailsBox.Text == "");
        }
        finally { slow.Close(); }
        var closedRead = new TaskCompletionSource<AssistantHealthSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new AssistantHealthWindow(_ => closedRead.Task, identity => identity == current);
        Task closing = closed.RefreshAsync(); closed.Close(); closedRead.SetResult(Sample(b)); await closing;
        check("closed_diagnostic_window_never_displays_late_snapshot", closed.DetailsBox.Text == "" && closed.RecordsText.Text == "");
    }
}
