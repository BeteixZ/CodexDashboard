using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal sealed record RemoteAttention(string TaskKey, string Id, string Method, ControlAttentionScope? Scope = null)
{
    internal ControlReceipt? ToReceipt(string body) => Scope == null ? null
        : new(Id, body, Scope.Dispatch.Task with { TurnId = Scope.ObservedTurnId }, AttentionScope: Scope);
}
internal sealed class RemoteTaskTransport : IReceiptScopedTaskTransport, IAsyncDisposable
{
    private readonly Settings settings;
    private readonly Func<string, CancellationToken, Task<IRemoteAppServer>> connect;
    private readonly Dictionary<string, IRemoteAppServer> servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> controlled = new();
    private readonly ConcurrentQueue<RemoteAttention> attention = new();
    private readonly object provenanceGate = new();
    private readonly Dictionary<string, Guid> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ControlDispatchProvenance> provenTurns = new();
    private readonly Dictionary<string, PendingDispatch> pendingDispatches = new();
    private sealed record PendingDispatch(Guid Connection, ControlDispatchProvenance Provenance, List<RemoteApprovalEvent> Events);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    internal RemoteTaskTransport(Settings settings, Func<string, CancellationToken, Task<IRemoteAppServer>>? connect = null)
    { this.settings = settings; this.connect = connect ?? (async (host, ct) => await RemoteAppServer.ConnectAsync(host, ct)); }
    public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        => await DeliverCoreAsync(task, text, messageId, null, ct);
    public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance provenance, CancellationToken ct)
        => DeliverCoreAsync(task, text, messageId, provenance, ct);
    private async Task<TaskDelivery> DeliverCoreAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance? provenance, CancellationToken ct)
    {
        if (!Settings.ValidAlias(task.Host) || !settings.RemoteHosts.Contains(task.Host, StringComparer.OrdinalIgnoreCase)
            || !Guid.TryParse(task.Id, out _) || text.Length is < 1 or > 4000)
            return new("failed", "远程主机、任务编号或指令无效，未投递。");
        if (provenance != null && (provenance.Task.HostId != DesktopTaskTransport.HostId(task.Host)
            || provenance.Task.ThreadId != task.Id || provenance.Task.ProfileId != null
            || provenance.Task.TurnId != task.TurnId || provenance.Cwd != task.Cwd))
            return new("failed", "原任务通知来源与投递目标不一致，未投递。");
        bool dispatched = false;
        await gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(65));
            if (disposed) return new("unavailable", "远程控制正在退出，指令仍在等待。");
            if (!servers.TryGetValue(task.Host, out var server) || !server.Connected)
            {
                if (server != null) await server.DisposeAsync();
                server = await connect(task.Host, timeout.Token); servers[task.Host] = server;
                var connection = Guid.NewGuid();
                lock (provenanceGate) connections[task.Host] = connection;
                string host = task.Host;
                if (server is IRemoteApprovalEvents scopedEvents)
                    scopedEvents.ScopedAttention += value => ObserveAttention(host, connection, value);
                else server.Attention += (threadId, method, requestId) => {
                        string key = task.Host + ":" + threadId;
                        if (!controlled.ContainsKey(key) || attention.Count >= 64) return;
                        // Compatibility observations lack a turn/dispatch proof.
                        // They remain readable by legacy diagnostics, but cannot
                        // become a durable host notification.
                        attention.Enqueue(new(key, WeChatText.Hash(key + "\n" + requestId + "\n" + method), method));
                };
            }
            var read = await server.RequestAsync("thread/read", new { threadId = task.Id, includeTurns = false }, timeout.Token);
            var before = ReadThread(read, task.Id);
            string state = Runtime(before);
            if (state == "active") return new("busy", "远程原任务正在运行或等待输入，指令已排队。");
            if (state is not ("idle" or "notLoaded")) return new("unavailable", "远程原任务状态尚未就绪，指令仍在等待。");
            // Resume the same id on the existing daemon. Omitted optional fields
            // preserve the session's settings; no new runtime or thread is made.
            var resumed = await server.RequestAsync("thread/resume", ResumeRequest(task.Id), timeout.Token);
            var thread = ReadThread(resumed, task.Id);
            if (Runtime(thread) == "active") return new("busy", "远程原任务已开始另一轮工作，当前指令已排队。");
            if (Runtime(thread) != "idle") return new("unavailable", "远程原会话尚未就绪，指令仍在等待。");
            string cwd = DesktopIpc.Text(resumed, "cwd");
            if (cwd.Length == 0) cwd = DesktopIpc.Text(thread, "cwd");
            if (task.Cwd.Length > 0 && cwd.Length > 0 && task.Cwd.TrimEnd('/') != cwd.TrimEnd('/'))
                return new("failed", "原任务工作目录与采集记录不一致，已停止投递。请在 Codex 核对。");
            controlled[task.Key] = 0;
            Guid activeConnection;
            lock (provenanceGate)
            {
                activeConnection = connections[task.Host];
                if (provenance != null) pendingDispatches[task.Key] = new(activeConnection, provenance, new());
            }
            var result = await server.RequestAsync("turn/start", TurnRequest(task.Id, text, messageId), timeout.Token, () => dispatched = true);
            if (!result.TryGetProperty("turn", out var turn) || DesktopIpc.Text(turn, "id").Length == 0)
                return new("unconfirmed", "远程服务未返回有效轮次；为避免重复执行，不会自动重发。");
            string turnId = DesktopIpc.Text(turn, "id");
            if (provenance != null)
            {
                lock (provenanceGate)
                {
                    string key = ProvenTurnKey(activeConnection, task.Key, turnId);
                    // Never relabel an old turn with a later owner's dispatch.
                    if (!provenTurns.TryGetValue(key, out var existing) || existing == provenance)
                    {
                        if (provenTurns.Count < 512 || provenTurns.ContainsKey(key)) provenTurns[key] = provenance;
                        if (pendingDispatches.TryGetValue(task.Key, out var pending) && pending.Provenance == provenance)
                            foreach (var value in pending.Events.Where(value => value.TurnId == turnId))
                                EnqueueAttention(task.Key, value, provenance);
                    }
                }
            }
            return new("submitted", "远程原 Codex 服务已确认接收，指令原文已进入同一会话。", turnId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return dispatched
                ? new("unconfirmed", "远程投递结果未确认；不会自动重发，请查看原任务。")
                : new("unavailable", "无法连接或恢复远程原任务，指令仍在等待；请检查 SSH 与原 Codex 服务。");
        }
        finally
        {
            lock (provenanceGate) pendingDispatches.Remove(task.Key);
            gate.Release();
        }
    }
    private static string ProvenTurnKey(Guid connection, string taskKey, string turn)
        => connection.ToString("N") + "\n" + taskKey + "\n" + turn;
    private void ObserveAttention(string host, Guid connection, RemoteApprovalEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.ThreadId) || string.IsNullOrWhiteSpace(value.TurnId)
            || string.IsNullOrWhiteSpace(value.RequestId)) return;
        string taskKey = host + ":" + value.ThreadId;
        lock (provenanceGate)
        {
            if (!connections.TryGetValue(host, out var current) || current != connection) return;
            if (provenTurns.TryGetValue(ProvenTurnKey(connection, taskKey, value.TurnId), out var provenance))
                EnqueueAttention(taskKey, value, provenance);
            else if (pendingDispatches.TryGetValue(taskKey, out var pending) && pending.Connection == connection
                && pending.Events.Count < 64) pending.Events.Add(value);
        }
    }
    private void EnqueueAttention(string taskKey, RemoteApprovalEvent value, ControlDispatchProvenance provenance)
    {
        if (attention.Count >= 64) return;
        string id = WeChatText.Hash(taskKey + "\n" + value.TurnId + "\n" + value.RequestId + "\n" + value.Method
            + "\n" + provenance.EntryId);
        attention.Enqueue(new(taskKey, id, value.Method, new(provenance, value.TurnId)));
    }
    internal static object ResumeRequest(string threadId) => new { threadId, excludeTurns = true };
    internal static object TurnRequest(string threadId, string text, string messageId) => new { threadId,
        input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }, clientUserMessageId = messageId };
    internal static JsonElement ReadThread(JsonElement result, string id)
    {
        if (!result.TryGetProperty("thread", out var thread) || DesktopIpc.Text(thread, "id") != id) throw new IOException("远程服务返回另一项任务，已停止投递。");
        return thread;
    }
    private static string Runtime(JsonElement thread) => thread.TryGetProperty("status", out var status) ? DesktopIpc.Text(status, "type") : "";
    internal IEnumerable<RemoteAttention> DrainAttention()
    { while (attention.TryDequeue(out var value)) yield return value; }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; foreach (var server in servers.Values) await server.DisposeAsync(); servers.Clear(); }
        finally { gate.Release(); }
    }
}
internal sealed class TaskTransport : IReceiptScopedTaskTransport, IAsyncDisposable
{
    private readonly DesktopTaskTransport local = new();
    internal RemoteTaskTransport Remote { get; }
    internal TaskTransport(Settings settings) => Remote = new(settings);
    public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        => task.Host == "本机" ? local.DeliverAsync(task, text, messageId, ct) : Remote.DeliverAsync(task, text, messageId, ct);
    public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance provenance, CancellationToken ct)
        => task.Host == "本机" ? local.DeliverAsync(task, text, messageId, ct)
            : Remote.DeliverAsync(task, text, messageId, provenance, ct);
    public ValueTask DisposeAsync() => Remote.DisposeAsync();
}
