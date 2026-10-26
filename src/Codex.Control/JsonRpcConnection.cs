using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Codex.Control;

/// <summary>Newline-delimited App Server JSON-RPC. Never retries a write or reconnects implicitly.</summary>
public sealed class JsonRpcConnection : IJsonRpcConnection
{
    private sealed class Pending
    {
        internal volatile bool Written;
        internal TaskCompletionSource<JsonElement> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Stream input, output;
    private readonly Func<ValueTask>? releaseOwnedProcess;
    private readonly ConcurrentDictionary<long, Pending> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task receive;
    private readonly int maximumFrameBytes;
    private long nextId;
    private int disconnected, disposed;
    public ConnectionIdentity Identity { get; }
    public string? ProjectSourceIdentity { get; }
    public bool IsConnected => Volatile.Read(ref disconnected) == 0;
    public event Action<RpcIncoming>? Incoming;
    public event Action? Disconnected;

    public JsonRpcConnection(ConnectionIdentity identity, Stream serverOutput, Stream serverInput,
        Func<ValueTask>? releaseOwnedProcess = null, int maximumFrameBytes = 1024 * 1024, string? projectSourceIdentity = null)
    {
        if (maximumFrameBytes is < 1024 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        Identity = identity; input = serverOutput; output = serverInput;
        ProjectSourceIdentity = projectSourceIdentity;
        this.releaseOwnedProcess = releaseOwnedProcess; this.maximumFrameBytes = maximumFrameBytes;
        receive = Task.Run(ReceiveAsync);
    }

    public bool SupportsGuardedRequests => true;
    public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        => RequestCoreAsync(method, parameters, null, cancellationToken);
    public Task<JsonElement> RequestGuardedAsync(string method, object parameters,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken)
        => RequestCoreAsync(method, parameters, beforeWrite, cancellationToken);
    private async Task<JsonElement> RequestCoreAsync(string method, object parameters,
        Func<CancellationToken, Task<bool>>? beforeWrite, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref nextId);
        var call = new Pending();
        if (!pending.TryAdd(id, call)) throw new InvalidOperationException();
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, () => call.Written = true, cancellationToken, beforeWrite);
            return await call.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcRemoteException) { throw; }
        catch (Exception ex) when (ex is not RpcTransportException)
        { throw new RpcTransportException(call.Written, ex is OperationCanceledException ? "request_cancelled" : "connection_failed", ex); }
        finally { pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        => WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, null, cancellationToken);
    public Task RespondAsync(JsonElement requestId, object result, CancellationToken cancellationToken)
    {
        if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new ArgumentException("Invalid request ID.");
        return WriteAsync(new { jsonrpc = "2.0", id = requestId, result }, null, cancellationToken);
    }

    private async Task WriteAsync(object frame, Action? writing, CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? beforeWrite = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (bytes.Length > maximumFrameBytes) throw new RpcTransportException(false, "frame_too_large");
        bool begun = false, held = false;
        try
        {
            await writer.WaitAsync(cancellationToken).ConfigureAwait(false); held = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new RpcTransportException(false, "disconnected_before_write");
            if (beforeWrite != null && !await beforeWrite(cancellationToken).ConfigureAwait(false))
                throw new RpcTransportException(false, "request_guard_rejected");
            cancellationToken.ThrowIfCancellationRequested();
            begun = true; writing?.Invoke();
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcTransportException) { throw; }
        catch (Exception ex)
        {
            // Even a failed partial write can have reached the peer; never claim rejection.
            if (begun) Close();
            throw new RpcTransportException(begun, "write_failed_or_cancelled", ex);
        }
        finally { if (held) writer.Release(); }
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[8192];
        using var frame = new MemoryStream();
        try
        {
            while (true)
            {
                int count = await input.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    byte value = buffer[i];
                    if (value == '\n')
                    {
                        if (frame.Length == 0) continue;
                        using var parsed = JsonDocument.Parse(frame.ToArray());
                        Dispatch(parsed.RootElement);
                        frame.SetLength(0);
                    }
                    else
                    {
                        if (frame.Length >= maximumFrameBytes) throw new IOException("frame_too_large");
                        frame.WriteByte(value);
                    }
                }
            }
        }
        catch (Exception) { /* Fault details must not expose server output or credentials. */ }
        finally { Close(); }
    }

    private void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new IOException("invalid_rpc_frame");
        if (message.TryGetProperty("method", out var method))
        {
            string name = method.GetString() ?? throw new IOException("invalid_method");
            JsonElement parameters = message.TryGetProperty("params", out var p) ? p.Clone() : JsonSerializer.SerializeToElement(new { });
            JsonElement? id = message.TryGetProperty("id", out var requestId) ? requestId.Clone() : null;
            if (id.HasValue && id.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                throw new IOException("invalid_request_id");
            Incoming?.Invoke(new(name, parameters, id));
            return;
        }
        if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out long number)) return;
        if (!pending.TryGetValue(number, out var call)) return; // Late response to a timed-out request is not replay authority.
        if (message.TryGetProperty("error", out var error))
        {
            int code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out int n) ? n : -32603;
            call.Result.TrySetException(new RpcRemoteException(code));
        }
        else if (message.TryGetProperty("result", out var result)) call.Result.TrySetResult(result.Clone());
        else throw new IOException("invalid_response");
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref disconnected, 1) != 0) return;
        foreach (var call in pending.Values)
            call.Result.TrySetException(new RpcTransportException(call.Written, "connection_lost"));
        try { Disconnected?.Invoke(); } catch { }
        lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Close();
        await output.DisposeAsync().ConfigureAwait(false);
        await input.DisposeAsync().ConfigureAwait(false);
        if (releaseOwnedProcess != null) await releaseOwnedProcess().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
        lifetime.Dispose();
        // A previously admitted writer may still be unwinding; disposing its semaphore here races Release.
    }
}
