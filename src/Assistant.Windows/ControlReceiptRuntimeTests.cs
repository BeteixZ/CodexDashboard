using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

// Actual queue, protected ledger, binding lifecycle and fake HTTP channel.
// No real task, credentials, remote connection or process is used.
internal static class ControlReceiptRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--control-receipt-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit synthetic output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("New absolute WPF test directory required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "control-receipt-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(x => x), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await CurrentReceiptAsync(Path.Combine(directory, "submitted"), "submitted", Check);
        await CurrentReceiptAsync(Path.Combine(directory, "unknown"), "unconfirmed", Check);
        await DelayedReceiptAsync(Path.Combine(directory, "binding-change"), false, Check);
        await DelayedReceiptAsync(Path.Combine(directory, "binding-aba"), true, Check);
        await CancelledCapturedReceiptAsync(Path.Combine(directory, "cancelled-receipt"), Check);
        await AttentionAsync(Path.Combine(directory, "attention"), Check);
        await RemoteProvenanceAsync(Check);
        await RawRemoteHostChainAsync(Path.Combine(directory, "raw-remote-host"), Check);
    }
    internal sealed class CapturingTransport : IReceiptScopedTaskTransport
    {
        internal TaskDelivery Next = new("submitted", "Synthetic backend receipt", "synthetic-confirmed-turn");
        internal ControlDispatchProvenance? Provenance;
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
            => throw new InvalidOperationException("Actual queue/gateway must carry captured provenance.");
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId,
            ControlDispatchProvenance provenance, CancellationToken ct)
        { Calls++; Provenance = provenance; return Task.FromResult(Next); }
    }
    private static async Task<ControlEntry> SeedQueuedAsync(DurableRuntimeTests.Fixture f)
    {
        f.Settings.ChatGpt.TaskControlEnabled = true;
        f.ObservedTask.Status = TaskStatusNames.Active;
        await f.InputAsync("queue-source", "状态");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "queue-source");
        await f.Queue.SubmitDurableAsync(f.ObservedTask, "SYNTHETIC_PRIVATE_INSTRUCTION", "synthetic-operation",
            WeChatText.Hash("queue-source"), f.CurrentSnapshot(), default);
        f.ObservedTask.Status = TaskStatusNames.Completed;
        return f.Queue.Entries.Single();
    }
    private static async Task CurrentReceiptAsync(string directory, string phase, Action<string, bool> check)
    {
        var transport = new CapturingTransport { Next = new(phase, "Synthetic receipt", phase == "submitted" ? "synthetic-confirmed-turn" : "") };
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, taskTransport: transport);
        var queued = await SeedQueuedAsync(f);
        ControlReceipt? captured = null;
        f.Queue.Notify = (receipt, ct) =>
        {
            captured = receipt;
            return Application.Current.Dispatcher.InvokeAsync(() => f.Channel.SendControlReceiptAsync(receipt, ct)).Task.Unwrap();
        };
        await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        await WaitNoticeAsync(f, queued.Id);
        var snapshot = await f.ReadAsync();
        var notice = snapshot.Events.Single(e => e.ExternalEventKey == "control/" + queued.Id);
        check(phase + "_captures_original_owner_state_and_immutable_target", transport.Calls == 1
            && captured?.QueueScope?.State == phase && captured.QueueScope.Owner == transport.Provenance?.Owner
            && captured.QueueScope.Task == notice.Task && notice.Task?.ThreadId == f.ObservedTask.Id
            && notice.Task.TurnId == (phase == "submitted" ? "synthetic-confirmed-turn" : queued.Target!.TurnId));
        check(phase + "_sends_natural_body_without_changing_transport_fact", f.Queue.Entries.Single().State == phase
            && !notice.Body.Contains("任务接续", StringComparison.Ordinal) && !notice.Body.Contains("指令编号", StringComparison.Ordinal)
            && (phase == "submitted" ? notice.Body.Contains("还没有确认它开始运行", StringComparison.Ordinal)
                : notice.Body.Contains("不能确认", StringComparison.Ordinal) && notice.Body.Contains("不会自动重发", StringComparison.Ordinal))
            && f.Sent.Count(body => body == notice.Body) == 1 && snapshot.Operations.IsEmpty && f.Script.Calls == 0);
        int before = f.Sent.Count;
        await f.Channel.SendControlReceiptAsync(new(queued.Id, "任务接续 · unproved", captured!.Task), default);
        check(phase + "_unproved_body_with_task_never_uses_legacy_fallback", f.Sent.Count == before
            && (await f.ReadAsync()).Events.Count(e => e.ExternalEventKey == "control/" + queued.Id) == 1);
    }
    private static async Task DelayedReceiptAsync(string directory, bool aba, Action<string, bool> check)
    {
        var transport = new CapturingTransport();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, taskTransport: transport);
        var queued = await SeedQueuedAsync(f);
        var original = f.Runtime.Identity!;
        var originalBinding = f.Settings.WeChat.ReadBinding()!;
        var entered = new TaskCompletionSource<ControlReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Queue.Notify = async (receipt, ct) =>
        {
            entered.TrySetResult(receipt);
            await release.Task;
            await Application.Current.Dispatcher.InvokeAsync(() => f.Channel.SendControlReceiptAsync(receipt, ct)).Task.Unwrap();
        };
        var pump = f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        try
        {
            var receipt = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SwitchAsync(f, NewBinding(originalBinding));
            var changed = f.Runtime.Identity!;
            if (aba) await SwitchAsync(f, originalBinding);
            int before = f.Sent.Count;
            release.TrySetResult();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            var old = await f.Ledger.ReadConversationAsync(original);
            var other = await f.Ledger.ReadConversationAsync(changed);
            string name = aba ? "owner_aba" : "owner_change";
            check(name + "_delayed_callback_does_not_adopt_old_format_into_current_identity", f.Sent.Count == before
                && !old.Events.Any(e => e.ExternalEventKey == "control/" + queued.Id)
                && !other.Events.Any(e => e.ExternalEventKey == "control/" + queued.Id)
                && f.Queue.Entries.Count == 0 && receipt.QueueScope != null && transport.Calls == 1);
            check(name + "_captured_generation_is_not_resigned_at_consumption",
                receipt.QueueScope!.Owner != f.Queue.CaptureReceiptOwner()
                && !f.Queue.IsCurrentReceipt(receipt, f.Queue.CaptureReceiptOwner().Owner)
                && (aba ? f.Runtime.Identity == original : f.Runtime.Identity == changed));
        }
        finally { release.TrySetResult(); await pump; }
    }
    private static async Task AttentionAsync(string directory, Action<string, bool> check)
    {
        var transport = new CapturingTransport();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, taskTransport: transport);
        var queued = await SeedQueuedAsync(f);
        await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        var provenance = transport.Provenance ?? throw new InvalidOperationException("Missing dispatch proof.");
        var attention = new RemoteAttention(f.ObservedTask.Key, "synthetic-attention", "requestApproval",
            new(provenance, transport.Next.TurnId));
        var receipt = attention.ToReceipt("这项任务正在等待处理，请在 Codex 中核对。")!;
        await f.Channel.SendControlReceiptAsync(receipt, default);
        await WaitNoticeAsync(f, receipt.Id);
        var accepted = await f.ReadAsync();
        check("attention_uses_original_dispatch_and_exact_confirmed_turn", accepted.Events.Any(e =>
            e.ExternalEventKey == "control/" + receipt.Id && e.Body == receipt.Body && e.Task == receipt.Task)
            && receipt.Task?.TurnId == transport.Next.TurnId && accepted.Operations.IsEmpty && f.Script.Calls == 0);
        int before = f.Sent.Count;
        await f.Channel.SendControlReceiptAsync(receipt with { Id = "wrong-turn", AttentionScope = new(provenance, "other-turn") }, default);
        await f.Channel.SendControlReceiptAsync(new("unproved-attention", receipt.Body, receipt.Task), default);
        check("attention_rejects_unproved_target_and_mismatched_turn", f.Sent.Count == before
            && !(await f.ReadAsync()).Events.Any(e => e.ExternalEventKey is "control/wrong-turn" or "control/unproved-attention"));
        var old = f.Runtime.Identity!;
        var originalBinding = f.Settings.WeChat.ReadBinding()!;
        await SwitchAsync(f, NewBinding(originalBinding));
        await f.Channel.SendControlReceiptAsync(receipt with { Id = "late-attention" }, default);
        check("attention_delayed_after_binding_change_never_enters_new_identity", f.Sent.Count == before
            && !(await f.ReadAsync()).Events.Any(e => e.ExternalEventKey == "control/late-attention")
            && (await f.Ledger.ReadConversationAsync(old)).Events.Count(e => e.ExternalEventKey == "control/synthetic-attention") == 1);
        await SwitchAsync(f, originalBinding);
        await f.Channel.SendControlReceiptAsync(receipt with { Id = "aba-attention" }, default);
        check("attention_owner_aba_cannot_restore_original_dispatch_permission", f.Sent.Count == before
            && !(await f.ReadAsync()).Events.Any(e => e.ExternalEventKey == "control/aba-attention"));
    }
    private static async Task CancelledCapturedReceiptAsync(string directory, Action<string, bool> check)
    {
        var transport = new CapturingTransport();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, taskTransport: transport);
        var queued = await SeedQueuedAsync(f);
        await f.InputAsync("new-constraint", "不要修改，只分析");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "new-constraint");
        var captured = f.Queue.CaptureReceipt(queued.Id) ?? throw new InvalidOperationException("Missing held receipt.");
        bool cancelled = f.Queue.CancelEntry(queued.Id);
        int before = f.Sent.Count;
        await f.Channel.SendControlReceiptAsync(captured, default);
        check("same_owner_state_change_invalidates_captured_held_receipt", cancelled && captured.QueueScope?.State == "held_constraints_changed"
            && f.Queue.Entries.Single().State == "cancelled" && transport.Calls == 0 && f.Sent.Count == before
            && !(await f.ReadAsync()).Events.Any(e => e.ExternalEventKey == "control/" + queued.Id));
    }
    private static ClawBinding NewBinding(ClawBinding original) => new()
    {
        BotId = "synthetic-replacement-" + Guid.NewGuid().ToString("N"), UserId = "synthetic-other-user",
        Token = "SYNTHETIC_OTHER_TOKEN", ContextToken = "SYNTHETIC_OTHER_CONTEXT",
        Cursor = "SYNTHETIC_CURSOR_INITIAL", BaseUrl = original.BaseUrl
    };
    private static async Task SwitchAsync(DurableRuntimeTests.Fixture f, ClawBinding binding)
    { f.Binding = binding; f.Settings.WeChat.WriteBinding(binding); await f.Channel.ConfigureAsync(); }
    private static async Task WaitNoticeAsync(DurableRuntimeTests.Fixture f, string id)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < until)
        {
            var snapshot = await f.ReadAsync();
            var notice = snapshot.Events.SingleOrDefault(e => e.ExternalEventKey == "control/" + id);
            if (notice != null && snapshot.Outbox.Any(o => o.NotificationEventId == notice.Id && o.Stage == OutboxStage.Accepted)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Synthetic receipt was not accepted.");
    }
    private sealed class RemotePeer : IRemoteAppServer, IRemoteApprovalEvents
    {
        public bool Connected { get; set; } = true;
        public event Action<string, string, string>? Attention;
        public event Action<RemoteApprovalEvent>? ScopedAttention;
        internal string Thread = Guid.NewGuid().ToString(), Turn = "remote-turn-1";
        internal bool Early, LostAfterWrite;
        internal readonly List<string> RawRequests = new();
        internal int ParsedRequests, StartRequests;
        internal void Emit(string turn, string request) => EmitRaw(JsonSerializer.Serialize(new
        {
            id = request, method = "item/commandExecution/requestApproval", @params = new { threadId = Thread, turnId = turn }
        }));
        internal void EmitRaw(string json)
        {
            RawRequests.Add(json);
            using var document = JsonDocument.Parse(json);
            // This is the same parser called by the production WebSocket loop.
            if (RemoteAppServer.ParseAttentionRequest(document.RootElement) is { } parsed)
            { ParsedRequests++; ScopedAttention?.Invoke(parsed); }
        }
        internal void EmitLegacy() => Attention?.Invoke(Thread, "requestApproval", "legacy-request");
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, Action? beforeSend = null)
        {
            ct.ThrowIfCancellationRequested(); beforeSend?.Invoke();
            if (method == "turn/start")
            {
                StartRequests++;
                if (Early) { Emit(Turn, "early-current"); Emit("unrelated-turn", "early-other"); }
                if (LostAfterWrite) throw new IOException("Synthetic uncertain reply.");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { turn = new { id = Turn } }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new
                { cwd = "/synthetic", thread = new { id = Thread, status = new { type = "idle" } } }));
        }
        public ValueTask DisposeAsync() { Connected = false; return ValueTask.CompletedTask; }
    }
    private static async Task RemoteProvenanceAsync(Action<string, bool> check)
    {
        var settings = new Settings { RemoteHosts = new() { "synthetic-host" },
            ChatGpt = new() { Enabled = true, TaskControlEnabled = true }, WeChat = new() { ClawBotEnabled = true } };
        var firstPeer = new RemotePeer { Early = true };
        RemotePeer peer = firstPeer;
        await using var remote = new RemoteTaskTransport(settings, (_, _) => Task.FromResult<IRemoteAppServer>(peer));
        var task = new TaskRecord { Id = peer.Thread, Host = "synthetic-host", Cwd = "/synthetic",
            Title = "Synthetic remote", Status = TaskStatusNames.Completed, TurnId = "prior-turn" };
        await using var queue = new TaskControlQueue(settings, remote, null);
        queue.SetOwner("synthetic-owner-A");
        WeChatSnapshot Snapshot() => new(new[] { task }, DateTimeOffset.UtcNow,
            new[] { new HostSnapshot { Name = task.Host, LastSuccess = DateTimeOffset.UtcNow } });
        queue.ObserveAuthenticatedInput("remote-source-A", true);
        await queue.SubmitDurableAsync(task, "SYNTHETIC_REMOTE_TEXT", "remote-operation-A", "remote-source-A", Snapshot(), default);
        var first = queue.Entries.Single();
        var early = remote.DrainAttention().Single();
        check("remote_early_attention_captures_dispatch_then_requires_exact_accepted_turn", early.Scope?.Dispatch.EntryId == first.Id
            && early.Scope.ObservedTurnId == peer.Turn && queue.IsCurrentAttention(early.ToReceipt("等待处理。")!, "synthetic-owner-A"));
        peer.Emit("", "missing-turn"); peer.Emit("other-turn", "wrong-turn"); peer.EmitLegacy();
        check("remote_missing_turn_and_legacy_events_have_no_durable_attention", !remote.DrainAttention().Any());
        peer.Emit(peer.Turn, "old-before-switch");
        var delayed = remote.DrainAttention().Single();
        queue.SetOwner("synthetic-owner-B");
        task.TurnId = peer.Turn; peer.Turn = "remote-turn-2"; peer.Early = false;
        queue.ObserveAuthenticatedInput("remote-source-B", true);
        await queue.SubmitDurableAsync(task, "SYNTHETIC_NEW_OWNER", "remote-operation-B", "remote-source-B", Snapshot(), default);
        peer.Emit("remote-turn-1", "late-old-turn"); peer.Emit("remote-turn-2", "current-turn");
        var events = remote.DrainAttention().ToArray();
        check("remote_reused_connection_never_relabels_old_turn_as_new_owner", events.Length == 2
            && !queue.IsCurrentAttention(delayed.ToReceipt("旧提示。")!, "synthetic-owner-B")
            && events.Single(e => e.Scope!.ObservedTurnId == "remote-turn-1").Scope!.Dispatch.Owner.Owner == "synthetic-owner-A"
            && !queue.IsCurrentAttention(events.Single(e => e.Scope!.ObservedTurnId == "remote-turn-1").ToReceipt("旧提示。")!, "synthetic-owner-B")
            && queue.IsCurrentAttention(events.Single(e => e.Scope!.ObservedTurnId == "remote-turn-2").ToReceipt("当前提示。")!, "synthetic-owner-B"));
        task.TurnId = peer.Turn; peer.Turn = "remote-turn-unknown"; peer.Early = true; peer.LostAfterWrite = true;
        queue.ObserveAuthenticatedInput("remote-source-unknown", true);
        await queue.SubmitDurableAsync(task, "SYNTHETIC_UNKNOWN", "remote-operation-unknown", "remote-source-unknown", Snapshot(), default);
        peer.Emit(peer.Turn, "late-unknown");
        check("remote_uncertain_start_does_not_adopt_attention_or_change_unknown", queue.Entries.Single(e => e.RequestSourceId == "remote-source-unknown").State == "unconfirmed"
            && !remote.DrainAttention().Any());
        firstPeer.Connected = false;
        peer = new RemotePeer { Thread = task.Id, Turn = "remote-turn-new-connection" };
        queue.ObserveAuthenticatedInput("remote-source-reconnect", true);
        await queue.SubmitDurableAsync(task, "SYNTHETIC_RECONNECT", "remote-operation-reconnect", "remote-source-reconnect", Snapshot(), default);
        firstPeer.Emit("remote-turn-1", "retired-connection"); peer.Emit(peer.Turn, "new-connection");
        var reconnected = remote.DrainAttention().ToArray();
        check("remote_retired_connection_cannot_emit_into_replacement_scope", reconnected.Length == 1
            && reconnected[0].Scope?.ObservedTurnId == peer.Turn
            && queue.IsCurrentAttention(reconnected[0].ToReceipt("当前提示。")!, "synthetic-owner-B"));
    }
    private static async Task RawRemoteHostChainAsync(string directory, Action<string, bool> check)
    {
        var firstPeer = new RemotePeer { Early = true, Turn = "raw-turn-owner-A" };
        RemotePeer peer = firstPeer;
        var remoteSettings = new Settings { RemoteHosts = new() { "synthetic-host" } };
        int connections = 0;
        await using var remote = new RemoteTaskTransport(remoteSettings, (_, _) =>
        { connections++; return Task.FromResult<IRemoteAppServer>(peer); });
        var task = new TaskRecord { Id = peer.Thread, Host = "synthetic-host", Cwd = "/synthetic",
            Project = "Synthetic", Title = "Synthetic raw approval", TurnId = "prior-turn",
            Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed };
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory,
            taskTransport: remote, syntheticTask: task);
        var queued = await SeedQueuedAsync(f);
        await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        var early = remote.DrainAttention().Single();
        const string firstBody = "这一轮正在等待处理，请在 Codex 中核对。";
        var firstReceipt = early.ToReceipt(firstBody) ?? throw new InvalidOperationException("Raw early event lacks provenance.");
        await f.Channel.SendControlReceiptAsync(firstReceipt, default);
        await WaitNoticeAsync(f, firstReceipt.Id);
        var accepted = await f.ReadAsync();
        check("raw_json_production_parser_remote_queue_runtime_and_fake_http_form_one_chain",
            peer.RawRequests.Count == 2 && peer.ParsedRequests == 2 && peer.StartRequests == 1
            && early.Scope?.Dispatch.EntryId == queued.Id && early.Scope.ObservedTurnId == "raw-turn-owner-A"
            && f.Sent.Count(body => body == firstBody) == 1 && f.Script.Calls == 0 && accepted.Operations.IsEmpty
            && accepted.Events.Single(e => e.ExternalEventKey == "control/" + early.Id).Task
                == new TaskReference("remote-ssh-discovered:synthetic-host", peer.Thread, "raw-turn-owner-A"));

        int before = f.Sent.Count, parsedBefore = peer.ParsedRequests;
        peer.EmitRaw(JsonSerializer.Serialize(new { id = "no-turn", method = "item/commandExecution/requestApproval",
            @params = new { threadId = peer.Thread } }));
        peer.EmitRaw(JsonSerializer.Serialize(new { id = (object?)null, method = "item/commandExecution/requestApproval",
            @params = new { threadId = peer.Thread, turnId = peer.Turn } }));
        peer.EmitRaw("{\"id\":\"duplicate\",\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\""
            + peer.Thread + "\",\"turnId\":\"wrong\",\"turnId\":\"" + peer.Turn + "\"}}");
        peer.Emit("different-turn", "wrong-turn");
        var invalid = remote.DrainAttention().ToArray();
        foreach (var notice in invalid)
            if (notice.ToReceipt("不得发送的无效提示。") is { } receipt) await f.Channel.SendControlReceiptAsync(receipt, default);
        check("raw_missing_turn_null_request_duplicate_fields_and_wrong_turn_cannot_reach_wire",
            peer.ParsedRequests == parsedBefore + 1 && invalid.Length == 0 && f.Sent.Count == before
            && (await f.ReadAsync()).Events.Count(e => e.ExternalEventKey?.StartsWith("control/", StringComparison.Ordinal) == true) == 1);

        peer.Emit(peer.Turn, "before-owner-switch");
        var delayed = remote.DrainAttention().Single();
        var oldIdentity = f.Runtime.Identity!;
        await SwitchAsync(f, NewBinding(f.Settings.WeChat.ReadBinding()!));
        await f.Channel.SendControlReceiptAsync(delayed.ToReceipt("旧身份的提示不能转送。")!, default);
        check("raw_event_captured_before_switch_is_rejected_by_actual_new_identity",
            f.Sent.Count == before && (await f.ReadAsync()).Events.IsEmpty
            && (await f.Ledger.ReadConversationAsync(oldIdentity)).Events.Count(e => e.ExternalEventKey?.StartsWith("control/", StringComparison.Ordinal) == true) == 1);

        task.TurnId = peer.Turn; peer.Turn = "raw-turn-owner-B"; peer.Early = false;
        await DispatchAsync("owner-B-source", "owner-B-operation");
        peer.Emit("raw-turn-owner-A", "old-turn-after-new-dispatch");
        peer.Emit(peer.Turn, "new-owner-current-turn");
        var notices = remote.DrainAttention().ToArray();
        const string rejectedBody = "旧轮次不应出现在新身份。", currentBody = "当前轮次正在等待处理，请在 Codex 中核对。";
        foreach (var notice in notices)
            await f.Channel.SendControlReceiptAsync(notice.ToReceipt(notice.Scope!.ObservedTurnId == peer.Turn ? currentBody : rejectedBody)!, default);
        var current = notices.Single(notice => notice.Scope!.ObservedTurnId == peer.Turn);
        await WaitNoticeAsync(f, current.Id);
        check("raw_same_connection_same_task_second_owner_dispatch_never_adopts_old_turn",
            connections == 1 && notices.Length == 2 && peer.StartRequests == 2
            && !f.Sent.Contains(rejectedBody) && f.Sent.Count(body => body == currentBody) == 1
            && (await f.ReadAsync()).Events.Count(e => e.ExternalEventKey?.StartsWith("control/", StringComparison.Ordinal) == true) == 1);

        task.TurnId = peer.Turn; peer.Turn = "raw-unknown-turn"; peer.Early = true; peer.LostAfterWrite = true;
        await DispatchAsync("unknown-source", "unknown-operation");
        before = f.Sent.Count;
        peer.Emit(peer.Turn, "unknown-late-request");
        var uncertain = remote.DrainAttention().ToArray();
        foreach (var notice in uncertain)
            if (notice.ToReceipt("未知投递不能认领审批。") is { } receipt) await f.Channel.SendControlReceiptAsync(receipt, default);
        check("raw_unknown_start_drops_early_and_late_approval_without_adoption_or_retry",
            uncertain.Length == 0 && f.Sent.Count == before && peer.StartRequests == 3
            && f.Queue.Entries.Single(e => e.RequestSourceId == WeChatText.Hash("unknown-source")).State == "unconfirmed"
            && (await f.ReadAsync()).Operations.IsEmpty);

        firstPeer.Connected = false;
        peer = new RemotePeer { Thread = task.Id, Turn = "raw-replacement-connection" };
        await DispatchAsync("replacement-source", "replacement-operation");
        firstPeer.Emit("raw-turn-owner-B", "retired-wire-request");
        peer.Emit(peer.Turn, "replacement-wire-request");
        var replacement = remote.DrainAttention().ToArray();
        const string replacementBody = "新连接中的当前轮次正在等待处理。";
        foreach (var notice in replacement) await f.Channel.SendControlReceiptAsync(notice.ToReceipt(replacementBody)!, default);
        if (replacement.Length == 1) await WaitNoticeAsync(f, replacement[0].Id);
        check("raw_retired_connection_is_rejected_before_replacement_runtime_delivery",
            connections == 2 && replacement.Length == 1 && replacement[0].Scope!.ObservedTurnId == peer.Turn
            && f.Sent.Count(body => body == replacementBody) == 1 && firstPeer.StartRequests == 3
            && peer.StartRequests == 1 && f.Script.Calls == 0 && (await f.ReadAsync()).Operations.IsEmpty);

        async Task DispatchAsync(string source, string operation)
        {
            await f.InputAsync(source, "状态");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, source);
            await f.Queue.SubmitDurableAsync(task, "SYNTHETIC_RAW_CHAIN_INSTRUCTION", operation,
                WeChatText.Hash(source), f.CurrentSnapshot(), default);
        }
    }
}
