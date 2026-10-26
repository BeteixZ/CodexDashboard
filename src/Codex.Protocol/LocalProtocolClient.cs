using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Codex.Protocol;

/// <summary>
/// A serialized read-only client. Cancellation or timeout after starting I/O closes the connection;
/// callers must explicitly reconnect, so a late response cannot be used by another request.
/// </summary>
public sealed class LocalProtocolClient : IAsyncDisposable, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ProtocolOptions _options;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private int _frameLimit;
    private int _disposed;
    private HelloResponse? _hello;

    private LocalProtocolClient(NamedPipeClientStream pipe, ProtocolOptions options)
    {
        _pipe = pipe;
        _options = options;
        _frameLimit = options.MaximumFrameBytes;
    }

    public HelloResponse Remote => _hello ?? throw new InvalidOperationException("Handshake is not complete.");

    public static async Task<LocalProtocolClient> ConnectAsync(string pipeName, ProtocolPeerIdentity identity,
        ProtocolProduct expectedServer, ProtocolOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The v1 local protocol requires Windows.");
        ProtocolOptions.ValidatePipeName(pipeName);
        ProtocolOptions.ValidateIdentity(identity);
        if (expectedServer is not (ProtocolProduct.Dashboard or ProtocolProduct.Assistant))
            throw new ArgumentOutOfRangeException(nameof(expectedServer));
        options ??= new();
        options.Validate();
        // "." is intentional: this API never accepts a network server name. Identification prevents
        // the status server from impersonating this client to access unrelated resources.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        var client = new LocalProtocolClient(pipe, options);
        try
        {
            using var connect = LocalProtocolServer.Deadline(cancellationToken, options.ConnectTimeout);
            try { await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProtocolException(ProtocolErrors.Timeout, "The local endpoint did not connect before the deadline.");
            }
            var hello = await client.CallAsync<HelloResponse>(ProtocolMethods.Hello, new HelloRequest
            {
                Identity = identity, SupportedVersions = options.SupportedVersions, MaximumFrameBytes = options.MaximumFrameBytes
            }, options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            try
            {
                ProtocolOptions.ValidateIdentity(hello.Identity);
                ProtocolOptions.ValidateRange(hello.SupportedVersions);
            }
            catch (ArgumentException)
            {
                throw new ProtocolException(ProtocolErrors.IdentityMismatch, "The endpoint returned an invalid identity.");
            }
            if (hello.Identity.Product != expectedServer)
                throw new ProtocolException(ProtocolErrors.IdentityMismatch, "The endpoint is not the expected product.");
            if (hello.SelectedVersion is null || hello.SelectedVersion != LocalProtocolServer.Negotiate(options.SupportedVersions, hello.SupportedVersions))
                throw new ProtocolException(ProtocolErrors.VersionMismatch, "The endpoint selected an incompatible protocol version.");
            if (hello.MaximumFrameBytes is < 1024 || hello.MaximumFrameBytes > options.MaximumFrameBytes)
                throw new ProtocolException(ProtocolErrors.InvalidMessage, "The endpoint returned an invalid frame limit.");
            client._hello = hello;
            client._frameLimit = hello.MaximumFrameBytes;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default) =>
        CallAsync<HealthStatus>(ProtocolMethods.Health, null, _options.RequestTimeout, cancellationToken);

    public Task<ProtocolCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        CallAsync<ProtocolCapabilities>(ProtocolMethods.Capabilities, null, _options.RequestTimeout, cancellationToken);

    public Task<AssistantStatus> GetAssistantStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync<AssistantStatus>(ProtocolMethods.AssistantStatus, null, _options.RequestTimeout, cancellationToken);

    private async Task<T> CallAsync<T>(string method, object? parameters, TimeSpan timeout,
        CancellationToken cancellationToken) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = LocalProtocolServer.Deadline(cancellationToken, timeout);
        var acquired = false;
        var startedIo = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var request = new RequestEnvelope
            {
                Id = Guid.NewGuid().ToString("N"), Method = method,
                Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, ProtocolWire.Json)
            };
            startedIo = true;
            await ProtocolWire.WriteAsync(_pipe, request, _frameLimit, deadline.Token).ConfigureAwait(false);
            var response = await ProtocolWire.ReadAsync<ResponseEnvelope>(_pipe, _frameLimit, deadline.Token).ConfigureAwait(false)
                ?? throw new ProtocolException(ProtocolErrors.Disconnected, "The local endpoint disconnected.");
            if (response.Id != request.Id || (response.Result is null) == (response.Error is null))
                throw new ProtocolException(ProtocolErrors.InvalidMessage, "The response does not match the request.");
            if (response.Error is { } error)
            {
                if (string.IsNullOrWhiteSpace(error.Code) || error.Code.Length > 64 || error.Message is null || error.Message.Length > 256)
                    throw new ProtocolException(ProtocolErrors.InvalidMessage, "The endpoint returned an invalid error.");
                throw new ProtocolException(error.Code, error.Message);
            }
            return ProtocolWire.Deserialize<T>(response.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (startedIo) Dispose();
            throw new ProtocolException(ProtocolErrors.Timeout, "The local status request timed out.");
        }
        catch (OperationCanceledException)
        {
            if (startedIo) Dispose();
            throw;
        }
        catch (ProtocolException)
        {
            if (startedIo) Dispose();
            throw;
        }
        catch (IOException)
        {
            Dispose();
            throw new ProtocolException(ProtocolErrors.Disconnected, "The local endpoint disconnected.");
        }
        finally
        {
            if (acquired) _requests.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _pipe.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
