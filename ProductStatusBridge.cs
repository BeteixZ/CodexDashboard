using System;
using System.Threading;
using System.Threading.Tasks;
using Codex.Protocol;

namespace CodexDashboard;

// A cached, content-free view. Protocol requests never enter application/UI state.
internal sealed class ProductStatusBridge : IProtocolStatusProvider, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ProtocolPeerIdentity identity;
    private readonly string profile;
    private Task? serving;
    private HealthStatus health = new() { ObservedAt = DateTimeOffset.UtcNow, Readiness = Availability.Unknown };
    private AssistantStatus assistant = new()
    {
        ObservedAt = DateTimeOffset.UtcNow, WeChat = ConnectionState.Unknown,
        Model = Availability.Unknown, QueuedCount = 0, AttentionCount = 0
    };
    internal string? EndpointFailure { get; private set; }
    internal ProductStatusBridge(ProtocolProduct product, string profile = "default")
    {
        this.profile = profile;
        identity = new() { Product = product, ProductVersion = "1.0.0", InstanceId = Guid.NewGuid() };
    }
    internal void Start() => serving = ServeAsync();
    private async Task ServeAsync()
    {
        try
        {
            var server = new LocalProtocolServer(LocalProtocolEndpoint.ForCurrentUser(identity.Product, profile), identity, this);
            await server.RunAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { EndpointFailure = "本机状态连接不可用；应用自身仍可独立工作。"; }
    }
    internal void Publish(HealthStatus value, AssistantStatus? assistantValue = null)
    {
        Volatile.Write(ref health, value);
        if (assistantValue != null) Volatile.Write(ref assistant, assistantValue);
    }
    public ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Volatile.Read(ref health)); }
    public ValueTask<AssistantStatus> GetAssistantStatusAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Volatile.Read(ref assistant)); }
    internal async Task<AssistantStatus?> ReadAssistantAsync(CancellationToken ct)
    {
        try
        {
            await using var client = await LocalProtocolClient.ConnectAsync(
                LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Assistant, profile), identity,
                ProtocolProduct.Assistant, cancellationToken: ct).ConfigureAwait(false);
            return await client.GetAssistantStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or UnauthorizedAccessException or ProtocolException)
        { return null; }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (serving != null) await serving.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
