using System.IO.Pipes;

namespace Codex.Protocol;

/// <summary>
/// A Windows-only, current-user, read-only v1 endpoint. RunAsync owns all pipe handles until cancelled.
/// Each connection is sequential; a fixed worker count bounds connections and request processing.
/// </summary>
public sealed class LocalProtocolServer
{
    private readonly string _pipeName;
    private readonly ProtocolPeerIdentity _identity;
    private readonly IProtocolStatusProvider _provider;
    private readonly ProtocolOptions _options;
    private readonly SemaphoreSlim _providerSlots;
    private int _started;

    public LocalProtocolServer(string pipeName, ProtocolPeerIdentity identity,
        IProtocolStatusProvider provider, ProtocolOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The v1 local protocol requires Windows.");
        ProtocolOptions.ValidatePipeName(pipeName);
        ProtocolOptions.ValidateIdentity(identity);
        _pipeName = pipeName;
        _identity = identity;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new();
        _options.Validate();
        _providerSlots = new(_options.MaximumConcurrentConnections, _options.MaximumConcurrentConnections);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A server instance can only be started once.");
        cancellationToken.ThrowIfCancellationRequested();
        var pipes = new List<NamedPipeServerStream>();
        try
        {
            for (var i = 0; i < _options.MaximumConcurrentConnections; i++)
            {
                var flags = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
                if (i == 0) flags |= PipeOptions.FirstPipeInstance;
                pipes.Add(new(_pipeName, PipeDirection.InOut, _options.MaximumConcurrentConnections,
                    PipeTransmissionMode.Byte, flags, 4096, 4096));
            }
            await Task.WhenAll(pipes.Select(pipe => ServeWorkerAsync(pipe, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var pipe in pipes) pipe.Dispose();
        }
    }

    private async Task ServeWorkerAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { /* A malformed frame or disconnected peer ends only this connection. */ }
            finally
            {
                if (pipe.IsConnected)
                {
                    try { pipe.Disconnect(); }
                    catch (IOException) { }
                }
            }
        }
    }

    private async Task ServeConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var frameLimit = _options.MaximumFrameBytes;
        using (var handshake = Deadline(cancellationToken, _options.HandshakeTimeout))
        {
            var request = await ProtocolWire.ReadAsync<RequestEnvelope>(pipe, frameLimit, handshake.Token).ConfigureAwait(false);
            if (request is null) return;
            ValidateEnvelope(request);
            ResponseEnvelope response;
            var accepted = false;
            try
            {
                if (request.Method != ProtocolMethods.Hello)
                    throw new ProtocolException(ProtocolErrors.HandshakeRequired, "The first request must be hello.");
                var hello = ProtocolWire.Deserialize<HelloRequest>(request.Params);
                try
                {
                    ProtocolOptions.ValidateIdentity(hello.Identity);
                    ProtocolOptions.ValidateRange(hello.SupportedVersions);
                }
                catch (ArgumentException)
                {
                    throw new ProtocolException(ProtocolErrors.IdentityMismatch, "Invalid peer identity or version range.");
                }
                if (hello.MaximumFrameBytes is < 1024 or > 1024 * 1024)
                    throw new ProtocolException(ProtocolErrors.InvalidMessage, "Invalid frame limit.");
                frameLimit = Math.Min(frameLimit, hello.MaximumFrameBytes);
                var selected = Negotiate(_options.SupportedVersions, hello.SupportedVersions);
                response = ProtocolWire.Success(request.Id, new HelloResponse
                {
                    Identity = _identity, SupportedVersions = _options.SupportedVersions,
                    SelectedVersion = selected, MaximumFrameBytes = frameLimit
                });
                accepted = true;
            }
            catch (ProtocolException error)
            {
                response = ProtocolWire.Failure(request.Id, error.Code, error.Message);
            }
            await ProtocolWire.WriteAsync(pipe, response, frameLimit, handshake.Token).ConfigureAwait(false);
            if (!accepted)
            {
                await FinishResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        for (var count = 0; count < _options.MaximumRequestsPerConnection; count++)
        {
            RequestEnvelope? request;
            using (var idle = Deadline(cancellationToken, _options.IdleTimeout))
            {
                request = await ProtocolWire.ReadAsync<RequestEnvelope>(pipe, frameLimit, idle.Token).ConfigureAwait(false);
            }
            if (request is null) return;
            ValidateEnvelope(request);
            using var deadline = Deadline(cancellationToken, _options.RequestTimeout);
            ResponseEnvelope response;
            try
            {
                // v1 has no method parameters beyond hello. Unknown envelope fields are ignored.
                if (request.Params is { } parameters && parameters.ValueKind is not System.Text.Json.JsonValueKind.Null
                    && (parameters.ValueKind != System.Text.Json.JsonValueKind.Object || parameters.EnumerateObject().Any()))
                    throw new ProtocolException(ProtocolErrors.InvalidMessage, "This v1 method does not accept parameters.");
                object result = request.Method switch
                {
                    ProtocolMethods.Health => await InvokeProviderAsync(
                        ct => _provider.GetHealthAsync(ct), deadline.Token).ConfigureAwait(false),
                    ProtocolMethods.Capabilities => Capabilities(frameLimit),
                    ProtocolMethods.AssistantStatus when _identity.Product == ProtocolProduct.Assistant
                        => await InvokeProviderAsync(ct => _provider.GetAssistantStatusAsync(ct), deadline.Token).ConfigureAwait(false),
                    ProtocolMethods.AssistantStatus => throw new ProtocolException(
                        ProtocolErrors.CapabilityUnavailable, "This endpoint does not provide assistant status."),
                    ProtocolMethods.Hello => throw new ProtocolException(
                        ProtocolErrors.InvalidMessage, "The connection has already completed hello."),
                    _ => throw new ProtocolException(ProtocolErrors.MethodNotFound, "Method is not supported by protocol v1.")
                };
                ValidateStatus(result);
                response = ProtocolWire.Success(request.Id, result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response = ProtocolWire.Failure(request.Id, ProtocolErrors.Timeout, "Status read timed out.");
            }
            catch (ProtocolException error)
            {
                response = ProtocolWire.Failure(request.Id, error.Code, error.Message);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                response = ProtocolWire.Failure(request.Id, ProtocolErrors.InternalError, "Status is temporarily unavailable.");
            }
            // Use a separate bounded write deadline so a timed-out provider can still return an error.
            using var write = Deadline(cancellationToken, _options.RequestTimeout);
            await ProtocolWire.WriteAsync(pipe, response, frameLimit, write.Token).ConfigureAwait(false);
        }
        await FinishResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishResponseAsync(Stream pipe, CancellationToken cancellationToken)
    {
        // DisconnectNamedPipe discards unread bytes even after FlushAsync. Let the client read and
        // close (or send its next sequential request) before disconnecting a terminal response.
        // Never use WaitForPipeDrain: a peer that stops reading could block shutdown indefinitely.
        using var drain = Deadline(cancellationToken, _options.RequestTimeout);
        try { await pipe.ReadAsync(new byte[1], drain.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private async Task<T> InvokeProviderAsync<T>(Func<CancellationToken, ValueTask<T>> invoke, CancellationToken cancellationToken)
    {
        if (!await _providerSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ProtocolException(ProtocolErrors.Busy, "Status provider is busy.");
        Task<T> pending;
        try { pending = invoke(cancellationToken).AsTask(); }
        catch (Exception)
        {
            _providerSlots.Release();
            throw new ProtocolException(ProtocolErrors.InternalError, "Status is temporarily unavailable.");
        }
        // Keep timed-out, noncooperative provider calls counted until they actually finish. A client
        // reconnect cannot accumulate an unbounded number of abandoned provider operations.
        _ = pending.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _providerSlots.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new ProtocolException(ProtocolErrors.InternalError, "Status is temporarily unavailable.");
        }
    }

    private ProtocolCapabilities Capabilities(int frameLimit) => new()
    {
        Capabilities =
        [
            new() { Name = ProtocolMethods.Hello, Availability = Availability.Available },
            new() { Name = ProtocolMethods.Health, Availability = Availability.Available },
            new() { Name = ProtocolMethods.Capabilities, Availability = Availability.Available },
            new() { Name = ProtocolMethods.AssistantStatus,
                Availability = _identity.Product == ProtocolProduct.Assistant ? Availability.Available : Availability.Unavailable,
                Reason = _identity.Product == ProtocolProduct.Assistant ? null : "Provided by the assistant endpoint only." }
        ],
        MaximumFrameBytes = frameLimit,
        MaximumConcurrentConnections = _options.MaximumConcurrentConnections,
        MaximumRequestsPerConnection = _options.MaximumRequestsPerConnection
    };

    internal static ProtocolVersion Negotiate(ProtocolVersionRange local, ProtocolVersionRange remote)
    {
        if (local.Major != remote.Major ||
            Math.Max(local.MinimumMinor, remote.MinimumMinor) > Math.Min(local.MaximumMinor, remote.MaximumMinor))
            throw new ProtocolException(ProtocolErrors.VersionMismatch, "No compatible protocol version is available.");
        return new() { Major = local.Major, Minor = Math.Min(local.MaximumMinor, remote.MaximumMinor) };
    }

    private static void ValidateEnvelope(RequestEnvelope request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 64 || request.Id.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 64)
            throw new ProtocolException(ProtocolErrors.InvalidMessage, "A bounded request identifier and method are required.");
    }

    private static void ValidateStatus(object status)
    {
        static bool ValidReason(string? value) => value is null || value.Length <= 256 && !value.Any(char.IsControl);
        var valid = status switch
        {
            HealthStatus health => health.ObservedAt != default && Enum.IsDefined(health.Readiness) && ValidReason(health.Reason),
            AssistantStatus assistant => assistant.ObservedAt != default && Enum.IsDefined(assistant.WeChat)
                && Enum.IsDefined(assistant.Model) && assistant.QueuedCount >= 0 && assistant.AttentionCount >= 0
                && ValidReason(assistant.AttentionReason),
            ProtocolCapabilities => true,
            _ => false
        };
        if (!valid) throw new ProtocolException(ProtocolErrors.InternalError, "Status provider returned invalid status.");
    }

    internal static CancellationTokenSource Deadline(CancellationToken parent, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        source.CancelAfter(timeout);
        return source;
    }
}
