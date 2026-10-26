using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CodexDashboard;

internal sealed record TaskDelivery(string State, string Detail, string TurnId = "");
internal interface IReceiptScopedTaskTransport : ITaskTransport
{
    Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance provenance, CancellationToken ct);
}
internal interface ITaskTransport
{
    Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct);
}

// Adapter for the installed desktop's local, user-scoped IPC. No second agent,
// new thread, shell command, model override, or permission override is created.
// The versioned follower protocol is internal; incompatible versions fail closed.
internal sealed class DesktopTaskTransport : ITaskTransport
{
    private readonly System.Collections.Generic.Dictionary<string, DateTimeOffset> opened = new();
    internal static string HostId(string host) => host == "本机" ? "local" : "remote-ssh-discovered:" + host;
    public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
    {
        if (!Guid.TryParse(task.Id, out _) || text.Length is < 1 or > 4000)
            return new("failed", "任务编号或指令无效，未投递。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        bool dispatched = false;
        try
        {
            await using var ipc = await DesktopIpc.ConnectAsync(timeout.Token);
            string host = HostId(task.Host);
            string? owner = await ipc.FindOwnerAsync(host, task.Id, timeout.Token);
            if (owner == null)
            {
                if (opened.TryGetValue(task.Key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2))
                    return new("unavailable", "原任务尚未在桌面载入；请在 Codex 打开该会话，指令仍在等待。");
                opened[task.Key] = DateTimeOffset.UtcNow;
                // The desktop itself resumes the original chat and its permissions.
                // Opening this exact existing thread cannot submit a user message.
                string url = "codex://threads/" + task.Id + (host == "local" ? "" : "?hostId=" + Uri.EscapeDataString(host));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                for (int i = 0; i < 10 && owner == null; i++)
                {
                    await Task.Delay(600, timeout.Token);
                    owner = await ipc.FindOwnerAsync(host, task.Id, timeout.Token);
                }
            }
            if (owner == null) return new("unavailable", "原任务尚未在桌面载入；请保持 Codex 运行。指令仍在等待。");
            var live = await ipc.ReadStateAsync(host, task.Id, owner, timeout.Token);
            if (live != "idle") return new(live == "busy" ? "busy" : "unavailable",
                live == "busy" ? "原任务正在运行或等待输入，指令已排队。" : "暂时无法确认原任务空闲，指令仍在等待。");
            var request = TurnRequest(task.Id, text, messageId);
            // Persisting the caller's 'sending' reservation happens before this.
            // After the write starts, timeout/disconnect must never auto-retry.
            dispatched = true;
            var response = await ipc.RequestAsync("thread-follower-start-turn", request,
                host == "local" ? 2 : 3, owner, host == "local" ? null : host, timeout.Token, 25000);
            if (DesktopIpc.Text(response, "resultType") != "success")
                return new("unconfirmed", "Codex 没有确认接续结果；为避免重复执行，不会自动重发。请查看原任务。");
            string turn = "";
            if (response.TryGetProperty("result", out var a) && a.TryGetProperty("result", out var b)
                && b.TryGetProperty("turn", out var c)) turn = DesktopIpc.Text(c, "id");
            return new("submitted", "Codex 已确认接收，沿用原任务的上下文和执行设置。", turn);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return dispatched
                ? new("unconfirmed", "投递结果未确认；为避免重复执行，不会自动重发。请查看原任务。")
                : new("unavailable", "无法连接或确认原桌面任务；指令仍在等待，未另起任务。");
        }
    }
    internal static object TurnRequest(string threadId, string text, string messageId) => new {
        conversationId = threadId,
        turnStart = new {
            request = new { threadId, input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }, clientUserMessageId = messageId },
            context = new { inheritThreadSettings = true }
        }
    };
}

internal sealed class DesktopIpc : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private string clientId = "initializing-client";
    private const int MaxFrame = 16 * 1024 * 1024;
    private DesktopIpc(NamedPipeClientStream pipe) => this.pipe = pipe;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    internal static async Task<DesktopIpc> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
        var ipc = new DesktopIpc(pipe);
        try
        {
            await pipe.ConnectAsync(2000, ct);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new IOException("无法验证 Codex IPC 服务。");
            using var process = Process.GetProcessById((int)pid);
            string path = process.MainModule?.FileName ?? "";
            bool desktop = path.EndsWith("\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && (path.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || path.Contains("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase));
            bool vscode = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }
                .Where(p => p.Length > 0).Any(p => path.Equals(Path.Combine(p, "Microsoft VS Code", "Code.exe"), StringComparison.OrdinalIgnoreCase));
            if (!desktop && !vscode) throw new IOException("未知 IPC 服务。");
            if (!OpenProcessToken(process.Handle, 8, out var token)) throw new IOException("无法确认 IPC 服务用户。");
            using (token)
            using (var serverIdentity = new WindowsIdentity(token.DangerousGetHandle()))
            using (var identity = WindowsIdentity.GetCurrent())
                if (serverIdentity.User != identity.User) throw new IOException("IPC 服务属于其他用户。");
            var init = await ipc.RequestAsync("initialize", new { clientType = "codex_dashboard" }, 0, null, null, ct);
            if (Text(init, "resultType") != "success" || !init.TryGetProperty("result", out var result)) throw new IOException("IPC 初始化失败。");
            ipc.clientId = Text(result, "clientId");
            if (ipc.clientId.Length == 0) throw new IOException("IPC 初始化失败。");
            return ipc;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }
    internal async Task<string?> FindOwnerAsync(string host, string id, CancellationToken ct)
    {
        var value = await RequestAsync("thread-owner-discovery", new { hostId = host, conversationId = id }, 1, null, null, ct);
        if (Text(value, "resultType") == "success") return Text(value, "handledByClientId");
        if (Text(value, "error") == "no-client-found") return null;
        throw new IOException("桌面会话协议不兼容。");
    }
    internal async Task<string> ReadStateAsync(string host, string id, string owner, CancellationToken ct)
    {
        await WriteAsync(new { type = "broadcast", method = "thread-stream-following-changed", sourceClientId = clientId, version = 1,
            @params = new { conversationId = id, hostId = host, following = true }, targetClientIds = new[] { owner } }, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(5000);
        while (true)
        {
            var value = await ReadAsync(timeout.Token);
            if (Text(value, "method") != "thread-stream-state-changed" || !value.TryGetProperty("params", out var p)
                || Text(p, "conversationId") != id || Text(p, "hostId") != host || Text(value, "sourceClientId") != owner
                || !p.TryGetProperty("change", out var change) || Text(change, "type") != "snapshot"
                || !change.TryGetProperty("conversationState", out var state)) continue;
            return State(state);
        }
    }
    internal static string State(JsonElement state)
    {
        if (state.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Array && requests.GetArrayLength() > 0) return "busy";
        if (!state.TryGetProperty("threadRuntimeStatus", out var status)) return "unknown";
        if (Text(status, "type") == "active") return "busy";
        if (Text(status, "type") != "idle" || Text(state, "resumeState") != "resumed") return "unknown";
        return "idle";
    }
    internal async Task<JsonElement> RequestAsync(string method, object parameters, int version, string? owner, string? host,
        CancellationToken ct, int timeoutMs = 4000)
    {
        string id = Guid.NewGuid().ToString();
        await WriteAsync(new { type = "request", requestId = id, sourceClientId = clientId, version, method,
            @params = parameters, targetClientId = owner, hostId = host, timeoutMs }, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(timeoutMs + 1000);
        while (true)
        {
            var value = await ReadAsync(timeout.Token);
            if (Text(value, "type") == "response" && Text(value, "requestId") == id) return value;
        }
    }
    private async Task WriteAsync(object message, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, ct); await pipe.WriteAsync(payload, ct); await pipe.FlushAsync(ct);
    }
    private async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, ct);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaxFrame) throw new IOException("IPC 响应超出限制。");
            byte[] payload = new byte[length]; await pipe.ReadExactlyAsync(payload, ct);
            using var document = JsonDocument.Parse(payload);
            var value = document.RootElement.Clone();
            if (Text(value, "type") != "client-discovery-request") return value;
            await WriteAsync(new { type = "client-discovery-response", requestId = Text(value, "requestId"), response = new { canHandle = false } }, ct);
        }
    }
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
