using System.Diagnostics;
using System.Text.Json;
using Codex.Control;
using CodexDashboard;

internal static class FakeChild
{
    internal static async Task RunAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var parsed = JsonDocument.Parse(line);
            var message = parsed.RootElement;
            if (!message.TryGetProperty("method", out var method))
            {
                await Send(new { jsonrpc = "2.0", method = "test/answered", @params = new
                { id = message.GetProperty("id"), result = message.GetProperty("result") } });
                continue;
            }
            if (!message.TryGetProperty("id", out var id)) continue;
            switch (method.GetString())
            {
                case "test/echo":
                    await Send(new { jsonrpc = "2.0", id, result = message.GetProperty("params") }); break;
                case "test/pid":
                    await Send(new { jsonrpc = "2.0", id, result = new { pid = Environment.ProcessId } }); break;
                case "test/error":
                    await Send(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "synthetic private error must not enter public diagnostics" } }); break;
                case "test/wait": break;
                case "test/drop": return;
                case "test/oversized": await Console.Out.WriteLineAsync(new string('x', 1024 * 1024 + 1)); break;
                case "test/approval":
                    await Send(new { jsonrpc = "2.0", method = "test/server-request", id = "synthetic-approval", @params = new { text = "synthetic" } });
                    await Send(new { jsonrpc = "2.0", id, result = new { queued = true } }); break;
            }
        }
    }
    private static async Task Send(object value)
    { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
}

internal static class WireTests
{
    internal static async Task RunAsync(string directory, ConnectionIdentity identity, Action<string, bool> check)
    {
        string executable = Environment.ProcessPath!;
        var childArguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            childArguments.Add(typeof(WireTests).Assembly.Location);
        childArguments.Add("--fake-rpc-child");
        var launch = new OwnedProcessLaunch(executable, childArguments, directory, new Dictionary<string, string>());
        var sourceLaunch = launch with { Arguments = childArguments.Concat(new[] { "--project-source-bootstrap" }).ToArray(), ReadProjectSource = true };
        await using (var sourceWire = await OwnedProcessConnection.StartSyntheticFixtureAsync(identity, sourceLaunch))
        {
            var echo = await sourceWire.RequestAsync("test/echo", new { projectSourceIdentity = new string('b', 64) }, default);
            check("same process bootstrap consumed before RPC and source remains immutable", sourceWire.ProjectSourceIdentity == new string('a', 64)
                && echo.GetProperty("projectSourceIdentity").GetString() == new string('b', 64));
        }
        bool productionRejectedSynthetic = false;
        try { await OwnedProcessConnection.StartAsync(identity, launch); }
        catch (ControlException ex) { productionRejectedSynthetic = ex.Code == "synthetic_identity_cannot_launch_runtime_process"; }
        check("production process launcher rejects synthetic verification identity", productionRejectedSynthetic);
        await using (var wire = await OwnedProcessConnection.StartSyntheticFixtureAsync(identity, launch))
        {
            int pid = (await wire.RequestAsync("test/pid", new { }, default)).GetProperty("pid").GetInt32();
            string tricky = "quotes ' \" newline\n dollar$(not a shell) ; & 中文";
            var echo = await wire.RequestAsync("test/echo", new { text = tricky }, default);
            check("real fake child roundtrips JSON without shell interpolation", echo.GetProperty("text").GetString() == tricky);
            var atGuard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGuard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = wire.RequestGuardedAsync("test/echo", new { text = "guarded" }, async _ =>
            { atGuard.SetResult(); await releaseGuard.Task; return true; }, default);
            await atGuard.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool allowed = true, secondGuardEntered = false;
            var second = wire.RequestGuardedAsync("test/echo", new { text = "cancelled while queued" }, _ =>
            { secondGuardEntered = true; return Task.FromResult(allowed); }, default);
            check("guard runs only after acquiring actual stream writer", !secondGuardEntered);
            allowed = false; releaseGuard.SetResult();
            check("guarded stream request roundtrips", (await first).GetProperty("text").GetString() == "guarded");
            bool guardRejected = false;
            try { await second; } catch (RpcTransportException error) { guardRejected = !error.MayHaveBeenWritten && error.Code == "request_guard_rejected"; }
            check("queued cancellation refuses stream bytes without disconnecting", guardRejected && secondGuardEntered && wire.IsConnected);
            bool rejected = false;
            try { await wire.RequestAsync("test/error", new { }, default); }
            catch (RpcRemoteException ex) { rejected = ex.Code == -32602 && !ex.Message.Contains("private error"); }
            check("RPC rejection preserves code without arbitrary error text", rejected);
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            bool unknown = false;
            try { await wire.RequestAsync("test/wait", new { }, cancelled.Token); }
            catch (RpcTransportException ex) { unknown = ex.MayHaveBeenWritten; }
            check("cancellation after write is not reported as no effect", unknown);
            var incoming = new TaskCompletionSource<RpcIncoming>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answered = new TaskCompletionSource<RpcIncoming>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.Incoming += message =>
            {
                if (message.Method == "test/server-request") incoming.TrySetResult(message);
                if (message.Method == "test/answered") answered.TrySetResult(message);
            };
            await wire.RequestAsync("test/approval", new { }, default);
            var request = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await wire.RespondAsync(request.RequestId!.Value, new { decision = "decline" }, default);
            var response = await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("server requests and typed response ids survive wire",
                response.Parameters.GetProperty("id").GetString() == "synthetic-approval"
                && response.Parameters.GetProperty("result").GetProperty("decision").GetString() == "decline");
            var echoes = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => wire.RequestAsync("test/echo", new { ordinal = i }, default)));
            check("concurrent JSONRPC calls keep distinct correlation ids", echoes.Select(x => x.GetProperty("ordinal").GetInt32()).SequenceEqual(Enumerable.Range(0, 25)));
            await wire.DisposeAsync();
            bool stopped;
            try { using var child = Process.GetProcessById(pid); stopped = child.HasExited; }
            catch (ArgumentException) { stopped = true; }
            check("dispose reaps only the child owned by this transport", stopped);
        }
        await using (var wire = await OwnedProcessConnection.StartSyntheticFixtureAsync(identity, launch))
        {
            bool unknown = false;
            try { await wire.RequestAsync("test/drop", new { }, default).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (RpcTransportException ex) { unknown = ex.MayHaveBeenWritten; }
            check("child crash after request produces unknown delivery", unknown && !wire.IsConnected);
            bool neverWritten = false;
            try { await wire.RequestAsync("test/echo", new { }, default); }
            catch (RpcTransportException ex) { neverWritten = !ex.MayHaveBeenWritten; }
            check("disconnected transport never silently respawns", neverWritten);
        }
        await using (var wire = await OwnedProcessConnection.StartSyntheticFixtureAsync(identity, launch))
        {
            bool bounded = false;
            try { await wire.RequestAsync("test/oversized", new { }, default).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (RpcTransportException ex) { bounded = ex.MayHaveBeenWritten; }
            check("oversized frame closes bounded parser", bounded && !wire.IsConnected);
        }
        var ssh = OwnedProcessConnection.Ssh(executable, new HostEndpointIdentity("remote-ssh-discovered:TestHost"),
            directory, "/opt/codex path/'$(x);/codex", "/tmp/home ' $(whoami)");
        check("SSH alias is a standalone argument after option separator", ssh.Arguments[^2] == "TestHost" && ssh.Arguments[^3] == "--");
        check("SSH remote values are POSIX quoted and host keys are strict",
            ssh.Arguments[^1] == "exec env CODEX_HOME='/tmp/home '\\'' $(whoami)' '/opt/codex path/'\\''$(x);/codex' app-server --listen stdio://"
            && ssh.Arguments.Contains("StrictHostKeyChecking=yes") && ssh.Arguments.Contains("BatchMode=yes"));
        bool invalidAlias = false;
        try { OwnedProcessConnection.Ssh(executable, new HostEndpointIdentity("remote-ssh-discovered:-oBad"), directory, "/codex", "/home"); }
        catch (ArgumentException) { invalidAlias = true; }
        catch (ControlException) { invalidAlias = true; }
        check("SSH option injection alias rejected", invalidAlias);
        var local = OwnedProcessConnection.Local(executable, directory, Path.Combine(directory, "home"));
        check("local process uses explicit profile and argument list", local.Arguments.SequenceEqual(["app-server", "--listen", "stdio://"])
            && local.Environment["CODEX_HOME"] == Path.Combine(directory, "home"));
    }
}

internal static class SchemaTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        using var methods = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ClientRequest.json")));
        string text = methods.RootElement.GetRawText();
        check("audited stable schema contains implemented client methods", OwnedVerification.SupportedMethods
            .Where(x => !x.StartsWith("item/", StringComparison.Ordinal)).All(x => text.Contains("\"" + x + "\"", StringComparison.Ordinal)));
        using var steer = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "v2", "TurnSteerParams.json")));
        check("schema requires expectedTurnId for steer", steer.RootElement.GetProperty("required").EnumerateArray().Any(x => x.GetString() == "expectedTurnId"));
        using var init = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "v1", "InitializeResponse.json")));
        check("initialize schema does not claim a server capability list", !init.RootElement.GetProperty("properties").TryGetProperty("capabilities", out _));
        using var approval = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "CommandExecutionRequestApprovalResponse.json")));
        string decisions = approval.RootElement.GetProperty("definitions").GetProperty("CommandExecutionApprovalDecision").GetRawText();
        check("approval decisions match stable schema", new[] { "accept", "acceptForSession", "decline", "cancel" }
            .All(x => decisions.Contains("\"" + x + "\"", StringComparison.Ordinal)));
    }
}
