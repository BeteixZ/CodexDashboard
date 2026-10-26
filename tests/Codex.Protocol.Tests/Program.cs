using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Codex.Protocol;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows is required for the real current-user named-pipe tests.");
    return 2;
}
if (args is ["--serve", var childName])
{
    using var shutdown = new CancellationTokenSource();
    var server = new LocalProtocolServer(childName, Tests.Identity(ProtocolProduct.Assistant), new TestProvider());
    var run = server.RunAsync(shutdown.Token);
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    await shutdown.CancelAsync();
    await run;
    return 0;
}
return await Tests.RunAsync();

internal static class Tests
{
    private static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("all_v1_methods_and_status_contract", RoundTrip),
        ("major_version_mismatch", MajorMismatch),
        ("minor_version_overlap_and_disjoint", MinorNegotiation),
        ("unknown_fields_are_ignored", UnknownFields),
        ("missing_required_hello_field_is_rejected", MissingRequired),
        ("duplicate_json_properties_are_rejected", DuplicateFields),
        ("hello_is_required", HelloRequired),
        ("invalid_peer_identity_is_rejected", InvalidIdentity),
        ("unexpected_server_product_is_rejected", UnexpectedServer),
        ("only_v1_read_methods_are_exposed", NoControlMethods),
        ("oversized_frame_rejected_before_payload_allocation", OversizedFrame),
        ("nonpositive_frame_lengths_rejected", NonpositiveLengths),
        ("bounded_outbound_serialization", BoundedWrite),
        ("provider_timeout_is_distinct_from_disconnect", ProviderTimeout),
        ("client_cancellation_closes_connection", ClientCancellation),
        ("connect_timeout_is_bounded", ConnectTimeout),
        ("handshake_timeout_reclaims_slot", HandshakeTimeout),
        ("idle_timeout_reclaims_slot", IdleTimeout),
        ("request_limit_closes_connection", RequestLimit),
        ("concurrent_clients_and_serialized_client_calls", ConcurrentClients),
        ("noncooperative_provider_calls_stay_bounded", BoundedProviders),
        ("shutdown_releases_connected_and_waiting_workers", Shutdown),
        ("duplicate_server_cannot_take_existing_endpoint", DuplicateServer),
        ("invalid_provider_status_is_not_reported_as_success", InvalidProviderStatus),
        ("provider_exception_details_are_not_exposed", ProviderExceptionPrivacy),
        ("dashboard_does_not_provide_assistant_status", DashboardStatus),
        ("current_user_pipe_configuration", CurrentUserConfiguration),
        ("real_separate_process_integration", SeparateProcess)
    ];

    internal static ProtocolPeerIdentity Identity(ProtocolProduct product) => new()
    {
        Product = product, ProductVersion = "test-1.0", InstanceId = Guid.NewGuid()
    };

    internal static async Task<int> RunAsync()
    {
        Console.WriteLine($"Environment: {Environment.OSVersion}; .NET {Environment.Version}; {DateTimeOffset.UtcNow:O}");
        Console.WriteLine("Scope: isolated pipe names; fake cached status; no credentials, network channels or Codex sessions.");
        var failed = 0;
        foreach (var (name, run) in Cases)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await run().WaitAsync(TimeSpan.FromSeconds(12));
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {error}");
            }
        }
        Console.WriteLine($"TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task RoundTrip()
    {
        await using var host = new TestHost();
        await using var client = await host.Connect();
        Equal(ProtocolVersion.V1, client.Remote.SelectedVersion);
        Equal(Availability.Available, (await client.GetHealthAsync()).Readiness);
        var status = await client.GetAssistantStatusAsync();
        Equal(ConnectionState.NotConfigured, status.WeChat);
        Equal(0, status.QueuedCount);
        var capabilities = await client.GetCapabilitiesAsync();
        Equal(4, capabilities.Capabilities.Count);
        Check(capabilities.Capabilities.All(c => c.Availability == Availability.Available));
        Equal(host.Options.MaximumFrameBytes, capabilities.MaximumFrameBytes);
    }

    private static async Task MajorMismatch()
    {
        await using var host = new TestHost();
        await Error(ProtocolErrors.VersionMismatch, async () => await host.Connect(new()
        {
            SupportedVersions = new() { Major = 2, MinimumMinor = 0, MaximumMinor = 1 }
        }));
    }

    private static async Task MinorNegotiation()
    {
        await using var host = new TestHost(options: new()
        {
            SupportedVersions = new() { Major = 1, MinimumMinor = 1, MaximumMinor = 3 }
        });
        await using var client = await host.Connect(new()
        {
            SupportedVersions = new() { Major = 1, MinimumMinor = 0, MaximumMinor = 2 }
        });
        Equal(2, client.Remote.SelectedVersion.Minor);
        await Error(ProtocolErrors.VersionMismatch, async () => await host.Connect());
    }

    private static async Task UnknownFields()
    {
        await using var host = new TestHost();
        using var pipe = await host.RawConnect();
        var identity = Identity(ProtocolProduct.Dashboard);
        var hello = $$$"""
            {"id":"first","method":"hello","futureEnvelope":{"a":1},"params":{
            "identity":{"product":"dashboard","productVersion":"1","instanceId":"{{{identity.InstanceId}}}","futureIdentity":true},
            "supportedVersions":{"major":1,"minimumMinor":0,"maximumMinor":0,"futureVersion":"ok"},
            "maximumFrameBytes":65536,"futureParams":[1,2]}}
            """;
        await RawWrite(pipe, hello);
        var response = await Read(pipe);
        Check(response?.Error is null && response?.Result is not null);
        await RawWrite(pipe, """{"id":"health","method":"health","futureFlag":"ignored"}""");
        response = await Read(pipe);
        Equal("health", response!.Id);
        Check(response.Error is null);
    }

    private static async Task MissingRequired()
    {
        await using var host = new TestHost();
        using var pipe = await host.RawConnect();
        await RawWrite(pipe, """{"id":"x","method":"hello","params":{"maximumFrameBytes":65536}}""");
        var response = await Read(pipe);
        Equal(ProtocolErrors.InvalidMessage, response!.Error!.Code);
    }

    private static async Task DuplicateFields()
    {
        await using var host = new TestHost();
        using var pipe = await host.RawConnect();
        await RawWrite(pipe, """{"id":"x","id":"y","method":"health"}""");
        await Disconnected(pipe);
    }

    private static async Task HelloRequired()
    {
        await using var host = new TestHost(options: new() { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        using var pipe = await host.RawConnect();
        await RawWrite(pipe, """{"id":"x","method":"health"}""");
        Equal(ProtocolErrors.HandshakeRequired, (await Read(pipe))!.Error!.Code);
        await Disconnected(pipe);
    }

    private static async Task InvalidIdentity()
    {
        await using var host = new TestHost();
        using var pipe = await host.RawConnect();
        await SendHello(pipe, Identity(ProtocolProduct.Unknown));
        Equal(ProtocolErrors.IdentityMismatch, (await Read(pipe))!.Error!.Code);
    }

    private static async Task UnexpectedServer()
    {
        await using var host = new TestHost();
        await Error(ProtocolErrors.IdentityMismatch, async () => await LocalProtocolClient.ConnectAsync(
            host.Name, Identity(ProtocolProduct.Dashboard), ProtocolProduct.Dashboard));
    }

    private static async Task NoControlMethods()
    {
        await using var host = new TestHost();
        using var pipe = await host.RawConnect();
        await SendHello(pipe);
        await Read(pipe);
        foreach (var method in new[] { "tasks.snapshot", "commands.submit", "approvals.respond", "operations.get", "events.subscribe" })
        {
            await RawWrite(pipe, JsonSerializer.Serialize(new { id = "x", method }));
            Equal(ProtocolErrors.MethodNotFound, (await Read(pipe))!.Error!.Code);
        }
    }

    private static async Task OversizedFrame()
    {
        await using var host = new TestHost(options: new() { MaximumFrameBytes = 1024 });
        using var pipe = await host.RawConnect();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await pipe.WriteAsync(header);
        await Disconnected(pipe);
        await using var validClient = await host.Connect();
        Equal(1024, validClient.Remote.MaximumFrameBytes);
        await validClient.GetHealthAsync();
    }

    private static async Task NonpositiveLengths()
    {
        await using var host = new TestHost();
        foreach (var length in new[] { 0, -1 })
        {
            using var pipe = await host.RawConnect();
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await pipe.WriteAsync(header);
            await Disconnected(pipe);
        }
    }

    private static async Task BoundedWrite()
    {
        using var stream = new MemoryStream();
        await Error(ProtocolErrors.FrameTooLarge, async () => await ProtocolWire.WriteAsync(
            stream, new { value = new string('x', 10000) }, 1024, CancellationToken.None));
        Equal(0L, stream.Length);
    }

    private static async Task ProviderTimeout()
    {
        await using var host = new TestHost(provider: new TestProvider { HealthDelay = TimeSpan.FromSeconds(10) },
            options: new() { RequestTimeout = TimeSpan.FromMilliseconds(60) });
        await using var client = await host.Connect(new() { RequestTimeout = TimeSpan.FromSeconds(1) });
        await Error(ProtocolErrors.Timeout, async () => await client.GetHealthAsync());
    }

    private static async Task ClientCancellation()
    {
        var provider = new TestProvider { HealthDelay = TimeSpan.FromSeconds(10) };
        await using var host = new TestHost(provider: provider);
        await using var client = await host.Connect();
        using var cancel = new CancellationTokenSource();
        var pending = client.GetHealthAsync(cancel.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancel.CancelAsync();
        await Throws<OperationCanceledException>(async () => await pending);
        await Throws<ObjectDisposedException>(async () => await client.GetCapabilitiesAsync());
    }

    private static async Task ConnectTimeout()
    {
        var timer = Stopwatch.StartNew();
        await Error(ProtocolErrors.Timeout, async () => await LocalProtocolClient.ConnectAsync(
            TestHost.NewName(), Identity(ProtocolProduct.Dashboard), ProtocolProduct.Assistant,
            new() { ConnectTimeout = TimeSpan.FromMilliseconds(70) }));
        Check(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static async Task HandshakeTimeout()
    {
        await using var host = new TestHost(options: new()
        {
            MaximumConcurrentConnections = 1, HandshakeTimeout = TimeSpan.FromMilliseconds(60)
        });
        using var idle = await host.RawConnect();
        await Disconnected(idle);
        await using var client = await host.Connect();
        await client.GetHealthAsync();
    }

    private static async Task IdleTimeout()
    {
        await using var host = new TestHost(options: new() { IdleTimeout = TimeSpan.FromMilliseconds(60) });
        using var pipe = await host.RawConnect();
        await SendHello(pipe);
        await Read(pipe);
        await Disconnected(pipe);
    }

    private static async Task RequestLimit()
    {
        await using var host = new TestHost(options: new() { MaximumRequestsPerConnection = 1 });
        await using var client = await host.Connect();
        await client.GetHealthAsync();
        await Error(ProtocolErrors.Disconnected, async () => await client.GetCapabilitiesAsync());
    }

    private static async Task ConcurrentClients()
    {
        await using var host = new TestHost(options: new() { MaximumConcurrentConnections = 2 });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var client = await host.Connect();
            await Task.WhenAll(client.GetHealthAsync(), client.GetHealthAsync(), client.GetCapabilitiesAsync());
        }));
    }

    private static async Task BoundedProviders()
    {
        var provider = new TestProvider { IgnoreCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = new TestHost(provider: provider, options: new()
        {
            MaximumConcurrentConnections = 1, RequestTimeout = TimeSpan.FromMilliseconds(70)
        });
        await using (var client = await host.Connect())
            await Error(ProtocolErrors.Timeout, async () => await client.GetHealthAsync());
        await using (var client = await host.Connect())
            await Error(ProtocolErrors.Busy, async () => await client.GetHealthAsync());
        Equal(1, provider.CallCount);
        provider.IgnoreCancellation.SetResult(TestProvider.Healthy);
    }

    private static async Task Shutdown()
    {
        var host = new TestHost();
        await using var client = await host.Connect();
        await host.DisposeAsync();
        await Error(ProtocolErrors.Disconnected, async () => await client.GetHealthAsync());
    }

    private static async Task DuplicateServer()
    {
        await using var host = new TestHost();
        var duplicate = new LocalProtocolServer(host.Name, Identity(ProtocolProduct.Assistant), new TestProvider());
        var error = await Throws<Exception>(() => duplicate.RunAsync(CancellationToken.None));
        Check(error is IOException or UnauthorizedAccessException);
        await using var client = await host.Connect();
        await client.GetHealthAsync();
    }

    private static async Task InvalidProviderStatus()
    {
        await using var host = new TestHost(provider: new TestProvider { InvalidReason = true });
        await using var client = await host.Connect();
        await Error(ProtocolErrors.InternalError, async () => await client.GetHealthAsync());
    }

    private static async Task ProviderExceptionPrivacy()
    {
        await using var host = new TestHost(provider: new TestProvider { ThrowPrivateException = true });
        await using var client = await host.Connect();
        var error = await Throws<ProtocolException>(async () => await client.GetHealthAsync());
        Equal(ProtocolErrors.InternalError, error.Code);
        Check(!error.ToString().Contains("fake-private-detail", StringComparison.Ordinal));
    }

    private static async Task DashboardStatus()
    {
        await using var host = new TestHost(product: ProtocolProduct.Dashboard);
        await using var client = await LocalProtocolClient.ConnectAsync(
            host.Name, Identity(ProtocolProduct.Assistant), ProtocolProduct.Dashboard);
        var caps = await client.GetCapabilitiesAsync();
        Equal(Availability.Unavailable, caps.Capabilities.Single(c => c.Name == ProtocolMethods.AssistantStatus).Availability);
        await Error(ProtocolErrors.CapabilityUnavailable, async () => await client.GetAssistantStatusAsync());
    }

    private static Task CurrentUserConfiguration()
    {
        var first = LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Assistant);
        Equal(first, LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Assistant));
        Check(first != LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Dashboard));
        Check(first != LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Assistant, "test-profile"));
        // Real same-user server/client handshake was exercised above. A second OS user is not
        // available in this unattended suite; enforcement uses CurrentUserOnly on both handles.
        return Task.CompletedTask;
    }

    private static async Task SeparateProcess()
    {
        var name = TestHost.NewName();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(name);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated test child.");
        try
        {
            Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await using (var client = await LocalProtocolClient.ConnectAsync(
                name, Identity(ProtocolProduct.Dashboard), ProtocolProduct.Assistant))
            {
                Equal(Availability.Available, (await client.GetHealthAsync()).Readiness);
                Equal(4, (await client.GetCapabilitiesAsync()).Capabilities.Count);
                Equal(ConnectionState.NotConfigured, (await client.GetAssistantStatusAsync()).WeChat);
            }
            await child.StandardInput.WriteLineAsync("stop");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Equal(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    private static Task SendHello(Stream pipe, ProtocolPeerIdentity? identity = null) => ProtocolWire.WriteAsync(pipe,
        new RequestEnvelope
        {
            Id = "hello", Method = ProtocolMethods.Hello, Params = JsonSerializer.SerializeToElement(new HelloRequest
            {
                Identity = identity ?? Identity(ProtocolProduct.Dashboard),
                SupportedVersions = ProtocolVersionRange.V1, MaximumFrameBytes = 65536
            }, ProtocolWire.Json)
        }, 65536, CancellationToken.None).AsTask();

    private static async Task RawWrite(Stream pipe, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task<ResponseEnvelope?> Read(Stream pipe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await ProtocolWire.ReadAsync<ResponseEnvelope>(pipe, 65536, deadline.Token);
    }

    private static async Task Disconnected(Stream pipe)
    {
        try { Check(await Read(pipe) is null); }
        catch (IOException) { }
    }

    private static async Task Error(string code, Func<Task> action)
    {
        var error = await Throws<ProtocolException>(action);
        Equal(code, error.Code);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Assertion failed.");
    }
}

internal sealed class TestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _run;
    internal string Name { get; } = NewName();
    internal ProtocolOptions Options { get; }
    internal TestHost(TestProvider? provider = null, ProtocolOptions? options = null,
        ProtocolProduct product = ProtocolProduct.Assistant)
    {
        Options = options ?? new();
        _run = new LocalProtocolServer(Name, Tests.Identity(product), provider ?? new TestProvider(), Options)
            .RunAsync(_shutdown.Token);
    }
    internal static string NewName() => "codex-protocol-test-" + Guid.NewGuid().ToString("N");
    internal Task<LocalProtocolClient> Connect(ProtocolOptions? options = null) => LocalProtocolClient.ConnectAsync(
        Name, Tests.Identity(ProtocolProduct.Dashboard), ProtocolProduct.Assistant, options);
    internal async Task<NamedPipeClientStream> RawConnect()
    {
        var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(deadline.Token);
        return pipe;
    }
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(2));
        _shutdown.Dispose();
    }
}

internal sealed class TestProvider : IProtocolStatusProvider
{
    internal TimeSpan HealthDelay { get; init; }
    internal bool InvalidReason { get; init; }
    internal bool ThrowPrivateException { get; init; }
    internal TaskCompletionSource<HealthStatus>? IgnoreCancellation { get; init; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int CallCount;
    internal static HealthStatus Healthy => new()
    {
        ObservedAt = DateTimeOffset.UtcNow, Readiness = Availability.Available
    };
    public async ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CallCount);
        Entered.TrySetResult();
        if (ThrowPrivateException) throw new ProtocolException("fake-private-detail", "fake-private-detail");
        if (IgnoreCancellation is not null) return await IgnoreCancellation.Task;
        if (HealthDelay > TimeSpan.Zero) await Task.Delay(HealthDelay, cancellationToken);
        return Healthy with { Reason = InvalidReason ? new string('x', 257) : null };
    }
    public ValueTask<AssistantStatus> GetAssistantStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new AssistantStatus
    {
        ObservedAt = DateTimeOffset.UtcNow, WeChat = ConnectionState.NotConfigured,
        Model = Availability.Unavailable, QueuedCount = 0, AttentionCount = 0
    });
}
