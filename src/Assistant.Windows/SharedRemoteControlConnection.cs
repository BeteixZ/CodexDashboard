using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codex.Control;

namespace CodexDashboard;

/// <summary>Connects to the original remote socket, with initialization owned by the typed client.</summary>
internal sealed class SharedRemoteControlConnection : IJsonRpcConnection
{
    private readonly RemoteAppServer server;
    public ConnectionIdentity Identity { get; }
    public bool IsConnected => server.Connected;
    public bool SupportsGuardedRequests => true;
    public event Action<RpcIncoming>? Incoming { add => server.Incoming += value; remove => server.Incoming -= value; }
    public event Action? Disconnected { add => server.Disconnected += value; remove => server.Disconnected -= value; }
    private SharedRemoteControlConnection(ConnectionIdentity identity, RemoteAppServer server)
    { Identity = identity; this.server = server; }
    internal static SharedRemoteControlConnection AttachSynthetic(ConnectionIdentity identity, RemoteAppServer server)
    {
        if (!identity.Synthetic) throw new ControlException("synthetic_identity_required", "synthetic_identity_required");
        return new(identity, server);
    }
    internal static async Task<SharedRemoteControlConnection> ConnectAsync(ConnectionIdentity identity, CancellationToken ct)
    {
        const string prefix = "remote-ssh-discovered:";
        if (identity.Synthetic || identity.Backend != OwnedBackend.SharedRemoteSocket
            || !identity.Endpoint.HostId.StartsWith(prefix, StringComparison.Ordinal))
            throw new ControlException("shared_remote_identity_required", "shared_remote_identity_required");
        var server = await RemoteAppServer.ConnectAsync(identity.Endpoint.HostId[prefix.Length..], ct, initialize: false);
        return new(identity, server);
    }
    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        try { return await server.RequestAsync(method, parameters, cancellationToken); }
        catch (RemoteRpcException ex) { throw new RpcRemoteException(ex.Code); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or TimeoutException)
        { throw new RpcTransportException(false, ex is OperationCanceledException ? "request_cancelled" : "read_connection_failed", ex); }
    }
    public async Task<JsonElement> RequestGuardedAsync(string method, object parameters,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken)
    {
        try { return await server.RequestGuardedAsync(method, parameters, beforeWrite, cancellationToken); }
        catch (RemoteRpcException ex) { throw new RpcRemoteException(ex.Code); }
    }
    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        => server.NotifyAsync(method, parameters, cancellationToken);
    public Task RespondAsync(JsonElement requestId, object result, CancellationToken cancellationToken)
        => throw new ControlException("shared_approval_response_not_supported", "shared_approval_response_not_supported");
    public ValueTask DisposeAsync() => server.DisposeAsync();
}
