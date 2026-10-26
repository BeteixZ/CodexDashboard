using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codex.Control;

namespace CodexDashboard;

internal interface IRemoteAppServer : IAsyncDisposable
{
    bool Connected { get; }
    event Action<string, string, string>? Attention;
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, Action? beforeSend = null);
}
internal sealed record RemoteApprovalEvent(string ThreadId, string TurnId, string Method, string RequestId);
internal interface IRemoteApprovalEvents
{
    event Action<RemoteApprovalEvent>? ScopedAttention;
}
internal sealed class RemoteAppServer : IRemoteAppServer, IRemoteApprovalEvents
{
    private readonly Process? process;
    private readonly WebSocket socket;
    private readonly HttpMessageInvoker? invoker;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private Task? reader;
    private readonly Task<string> stderr;
    private bool disposed;
    internal event Action<string, JsonElement>? Notification;
    internal event Action<RpcIncoming>? Incoming;
    internal event Action? Disconnected;
    public event Action<string, string, string>? Attention;
    public event Action<RemoteApprovalEvent>? ScopedAttention;
    public bool Connected => !disposed && socket.State == WebSocketState.Open && reader is { IsCompleted: false };
    private RemoteAppServer(Process process, ClientWebSocket socket, HttpMessageInvoker invoker)
    { this.process = process; this.socket = socket; this.invoker = invoker; stderr = process.StandardError.ReadToEndAsync(); }
    private RemoteAppServer(WebSocket socket)
    { this.socket = socket; stderr = Task.FromResult(""); reader = ReadLoopAsync(); }
    internal static RemoteAppServer AttachSyntheticSocket(WebSocket socket) => new(socket);
    internal static RemoteApprovalEvent? ParseAttentionRequest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root)
            || !root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("id", out var id) || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
            || !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || HasDuplicates(parameters)
            || !parameters.TryGetProperty("threadId", out var threadValue) || threadValue.ValueKind != JsonValueKind.String
            || !parameters.TryGetProperty("turnId", out var turnValue) || turnValue.ValueKind != JsonValueKind.String)
            return null;
        string method = methodValue.GetString()!, thread = threadValue.GetString()!, turn = turnValue.GetString()!;
        string request = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        return Valid(method) && Valid(thread) && Valid(turn) && Valid(request)
            ? new(thread, turn, method, request) : null;
        static bool Valid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
        static bool HasDuplicates(JsonElement value)
        {
            var names = value.EnumerateObject().Select(property => property.Name).ToArray();
            return names.Distinct(StringComparer.Ordinal).Count() != names.Length;
        }
    }
    internal static string ProxyCommand(string source) => "python3 -u -c \"import base64;exec(base64.b64decode('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) + "'))\"";
    internal static ProcessStartInfo StartInfo(string host, string source)
    {
        if (!Settings.ValidAlias(host)) throw new InvalidOperationException("SSH 别名无效。");
        var start = new ProcessStartInfo("ssh.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", host, ProxyCommand(source) }) start.ArgumentList.Add(arg);
        return start;
    }
    internal static async Task<RemoteAppServer> ConnectAsync(string host, CancellationToken ct, bool initialize = true)
    {
        string source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "remote_socket_proxy.py"), ct);
        var process = Process.Start(StartInfo(host, source)) ?? throw new IOException("无法启动 SSH 连接。");
        var stream = new StdioDuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(stream) };
        var invoker = new HttpMessageInvoker(handler);
        var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var server = new RemoteAppServer(process, socket, invoker);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(18));
            await socket.ConnectAsync(new Uri("ws://codex-app-server/rpc"), invoker, timeout.Token);
            server.reader = server.ReadLoopAsync();
            if (initialize)
            {
                await server.RequestAsync("initialize", new { clientInfo = new { name = "codex_dashboard", title = "Codex Dashboard WeChat", version = "1.0" }, capabilities = new { experimentalApi = true } }, timeout.Token);
                await server.SendAsync(new { method = "initialized" }, timeout.Token);
            }
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            string error = server.stderr.IsCompletedSuccessfully ? server.stderr.Result : "";
            if (error.Contains("REMOTE_SOCKET_", StringComparison.Ordinal)) throw new IOException("远程原 Codex 控制 socket 不可用或权限校验失败。");
            throw new IOException("无法连接远程原 Codex 服务（SSH/WebSocket）。");
        }
    }
    public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, Action? beforeSend = null)
        => RequestCoreAsync(method, parameters, ct, beforeSend, null);
    internal Task<JsonElement> RequestGuardedAsync(string method, object parameters,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken ct)
        => RequestCoreAsync(method, parameters, ct, null, beforeWrite);
    internal Task NotifyAsync(string method, object parameters, CancellationToken ct)
        => SendAsync(new { method, @params = parameters }, ct);
    private async Task<JsonElement> RequestCoreAsync(string method, object parameters, CancellationToken ct,
        Action? beforeSend, Func<CancellationToken, Task<bool>>? beforeWrite)
    {
        if (disposed || socket.State != WebSocketState.Open)
        {
            if (beforeWrite != null) throw new RpcTransportException(false, "disconnected_before_write");
            throw new IOException("远程 Codex 连接已断开。");
        }
        string id = Guid.NewGuid().ToString(); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool writing = false;
        pending[id] = completion;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, ct, () => { beforeSend?.Invoke(); writing = true; }, beforeWrite);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(35), ct);
            if (result.TryGetProperty("error", out var error)) throw new RemoteRpcException(method, DesktopIpc.Text(error, "message"),
                error.TryGetProperty("code", out var code) && code.TryGetInt32(out int value) ? value : -32603);
            if (!result.TryGetProperty("result", out var payload)) throw new IOException("远程 Codex 返回格式不兼容。");
            return payload.Clone();
        }
        catch (Exception ex) when (beforeWrite != null && ex is not (RemoteRpcException or RpcTransportException))
        { throw new RpcTransportException(writing, writing ? "write_outcome_unknown" : "not_dispatched", ex); }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(object value, CancellationToken ct, Action? beforeSend = null,
        Func<CancellationToken, Task<bool>>? beforeWrite = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await writeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (beforeWrite != null && !await beforeWrite(ct)) throw new RpcTransportException(false, "request_guard_rejected");
            ct.ThrowIfCancellationRequested();
            beforeSend?.Invoke(); await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { writeGate.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[65536];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var body = new MemoryStream(); ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("远程 Codex 连接已关闭。");
                    if (frame.MessageType != WebSocketMessageType.Text || body.Length + frame.Count > 16 * 1024 * 1024) throw new IOException("远程 Codex 响应超出限制。");
                    body.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using var json = JsonDocument.Parse(body.ToArray()); var root = json.RootElement;
                string method = DesktopIpc.Text(root, "method");
                if (root.TryGetProperty("id", out var id) && method.Length == 0)
                { if (pending.TryGetValue(id.ToString(), out var completion)) completion.TrySetResult(root.Clone()); continue; }
                if (!root.TryGetProperty("params", out var parameters)) continue;
                Incoming?.Invoke(new(method, parameters.Clone(), root.TryGetProperty("id", out var requestId) ? requestId.Clone() : null));
                if (root.TryGetProperty("id", out _))
                {
                    // Existing desktop clients may answer these shared server
                    // requests. The bridge never grants permissions or tools.
                    if (ParseAttentionRequest(root) is { } approval) ScopedAttention?.Invoke(approval);
                    Attention?.Invoke(DesktopIpc.Text(parameters, "threadId"), method, root.GetProperty("id").ToString()); continue;
                }
                if (method is "turn/started" or "turn/completed" or "thread/status/changed" or "serverRequest/resolved")
                    Notification?.Invoke(method, parameters.Clone());
                if (method == "item/completed" && parameters.TryGetProperty("item", out var item) && DesktopIpc.Text(item, "type") is "agentMessage" or "commandExecution")
                    Notification?.Invoke(method, parameters.Clone());
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or JsonException)
        { foreach (var completion in pending.Values) completion.TrySetException(new IOException("远程 Codex 连接中断。")); }
        finally { Disconnected?.Invoke(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); socket.Abort();
        try { process?.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        if (reader != null) try { await reader; } catch { }
        if (process != null) try
        {
            using var timeout = new CancellationTokenSource(1000);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } }
        socket.Dispose(); invoker?.Dispose(); process?.Dispose(); lifetime.Dispose();
    }
}
internal sealed class RemoteRpcException : IOException
{
    internal string Method { get; }
    internal string Diagnostic { get; }
    internal int Code { get; }
    internal RemoteRpcException(string method, string diagnostic = "", int code = -32603) : base("远程 Codex 拒绝请求：" + method)
    { Method = method; Diagnostic = WeChatText.Clean(diagnostic, 400); Code = code; }
}
internal sealed class StdioDuplexStream : Stream
{
    private readonly Stream input, output;
    internal StdioDuplexStream(Stream input, Stream output) { this.input = input; this.output = output; }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { input.Dispose(); output.Dispose(); } base.Dispose(disposing); }
}
