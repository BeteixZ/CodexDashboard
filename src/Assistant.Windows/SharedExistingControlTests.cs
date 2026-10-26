using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Codex.Control;

namespace CodexDashboard;

internal static class SharedExistingControlTests
{
    // Exercises the production RemoteAppServer receive pump and serialized write gate without SSH or a network socket.
    internal sealed class Socket : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private WebSocketState state = WebSocketState.Open;
        internal readonly ConcurrentQueue<JsonElement> Sent = new();
        internal Func<JsonElement, Task<bool>>? Handle;
        internal string ThreadId = "synthetic-existing-thread", Cwd = "/synthetic/project", TurnId = "existing-turn", TurnState = "inProgress";
        internal string Version = OwnedVerification.SchemaVersion;
        internal void Frame(object frame) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(frame));
        internal void Reply(JsonElement request, object result) => Frame(new { id = request.GetProperty("id"), result });
        internal void Event(string method, object parameters) => Frame(new { method, @params = parameters });
        internal int Writes => Sent.Count(x => Method(x) is "turn/steer" or "turn/interrupt");
        internal static string Method(JsonElement value) => value.GetProperty("method").GetString()!;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => state;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseAsync(status, description, ct);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            byte[] bytes;
            try { bytes = await incoming.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { throw new WebSocketException(); }
            bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(bytes.AsMemory());
            var request = document.RootElement.Clone(); Sent.Enqueue(request);
            if (Handle != null && await Handle(request)) return;
            if (!request.TryGetProperty("id", out _)) return;
            switch (Method(request))
            {
                case "initialize": Reply(request, new { userAgent = "codex_cli_rs/" + Version,
                    platformOs = "linux", platformFamily = "unix", codexHome = "/synthetic/home" }); break;
                case "thread/read": Reply(request, new { thread = new { id = ThreadId, cwd = Cwd,
                    status = new { type = TurnState == "inProgress" ? "active" : "idle" },
                    turns = new[] { new { id = TurnId, status = TurnState, items = Array.Empty<object>() } } } }); break;
                case "turn/steer": Reply(request, new { turnId = request.GetProperty("params").GetProperty("expectedTurnId").GetString() }); break;
                default: Reply(request, new { }); break;
            }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Socket Socket = new();
        internal readonly SharedRemoteControlConnection Wire;
        internal readonly SharedExistingAppServerClient Client;
        internal readonly ExistingTurnTarget Target;
        internal Fixture(SharedExistingConfiguration configuration, Func<DateTimeOffset> clock)
        {
            Wire = SharedRemoteControlConnection.AttachSynthetic(configuration.Connection, RemoteAppServer.AttachSyntheticSocket(Socket));
            Client = new(configuration, Wire, clock);
            Target = new(new(configuration.Connection.Endpoint.HostId, Socket.ThreadId, configuration.Connection.Endpoint.ProfileId), Socket.Cwd, Socket.TurnId);
        }
        internal async Task EventAsync(string method, object parameters, object? requestId = null)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Read(RpcIncoming message) { if (message.Method == method) received.TrySetResult(); }
            Wire.Incoming += Read;
            try
            {
                if (requestId == null) Socket.Event(method, parameters);
                else Socket.Frame(new { id = requestId, method, @params = parameters });
                await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { Wire.Incoming -= Read; }
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--shared-existing-control-test"); if (index < 0) return false;
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks.Add(name, value); if (!value) throw new InvalidOperationException(name); }
        async Task Reject(string name, Func<Task> action, string code)
        {
            try { await action(); Check(name, false); }
            catch (ControlException ex) { Check(name, ex.Code == code); }
        }
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        SharedExistingConfiguration Config(bool verified = true)
        {
            var value = new SharedExistingConfiguration
            {
                Connection = new(new("remote-ssh-discovered:synthetic-host", "synthetic-profile"), OwnedBackend.SharedRemoteSocket, true),
                ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = "linux", ExpectedCodexHome = "/synthetic/home",
                Directories = ImmutableHashSet.Create("/synthetic/project"), RequestTimeout = TimeSpan.FromSeconds(2)
            };
            return verified ? value with { Verification = new(value.Connection, value.ExpectedVersion, value.ExpectedPlatformOs,
                value.ConfigurationDigest(), ImmutableHashSet.Create("turn/steer", "turn/interrupt"), VerificationKind.SyntheticReplay,
                "synthetic-socket-replay", now.AddMinutes(-1), now.AddHours(1)) } : value;
        }
        static Task<bool> Allow(CancellationToken _) => Task.FromResult(true);
        try
        {
            await using (var disabled = new Fixture(Config(false), () => now))
            {
                await disabled.Client.InitializeAsync();
                await Reject("unverified backend cannot write", () => disabled.Client.InterruptAsync(Guid.NewGuid(), disabled.Target, Allow), "runtime_verification_required");
                Check("no mutation for disabled backend", disabled.Socket.Writes == 0);
            }
            var real = Config() with { Connection = Config().Connection with { Synthetic = false } };
            real = real with { Verification = real.Verification! with { Connection = real.Connection, ConfigurationDigest = real.ConfigurationDigest() } };
            Check("synthetic evidence cannot enable real backend", real.CapabilityFailure("turn/steer", now) == "synthetic_evidence_cannot_enable_runtime");
            Check("scope changes retire verification", (Config() with { Directories = ImmutableHashSet.Create("/other") }).CapabilityFailure("turn/steer", now) == "verification_scope_mismatch");
            await using (var wrong = new Fixture(Config(), () => now))
            {
                wrong.Socket.Version = "0.0.1";
                await Reject("initialize requires verified server version", () => wrong.Client.InitializeAsync(), "initialize_version_platform_or_profile_mismatch");
            }
            await using var f = new Fixture(Config(), () => now);
            await f.Client.InitializeAsync();
            Check("existing control initializes once without experimental override", f.Socket.Sent.Count(x => Socket.Method(x) == "initialize") == 1
                && !f.Socket.Sent.First().GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean());
            Check("shared control has no create resume or approval capability", new[] { "thread/start", "thread/resume", "turn/start", "item/commandExecution/requestApproval" }
                .All(method => f.Client.CapabilityFailure(method) == "shared_method_not_supported"));
            await Reject("shared wire refuses approval responses", () => f.Wire.RespondAsync(JsonSerializer.SerializeToElement("approval"), new { decision = "accept" }, default), "shared_approval_response_not_supported");
            await Reject("foreign endpoint rejected before read", () => f.Client.InterruptAsync(Guid.NewGuid(), f.Target with { Task = new("local", f.Target.Task.ThreadId) }, Allow), "target_outside_configured_scope");
            var mismatch = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target with { TurnId = "other-turn" }, Allow);
            Check("wrong turn never dispatched", mismatch.State == ControlState.Failed && f.Socket.Writes == 0);
            f.Socket.Cwd = "/other";
            var moved = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, Allow);
            Check("live directory mismatch never dispatched", moved.Code == "read_thread_identity_or_directory_mismatch" && f.Socket.Writes == 0);
            f.Socket.Cwd = f.Target.Cwd;
            var cancelled = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, _ => Task.FromResult(false));
            Check("durable guard rejection prevents frame", cancelled.State == ControlState.CancelledBeforeDispatch && f.Socket.Writes == 0);
            var raced = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, async _ =>
            {
                await f.EventAsync("turn/completed", new { threadId = f.Target.Task.ThreadId, turn = new { id = f.Target.TurnId, status = "completed" } });
                return true;
            });
            Check("turn change while authorizing prevents frame", raced.State == ControlState.CancelledBeforeDispatch && f.Socket.Writes == 0);

            var expired = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, _ => { now = now.AddHours(2); return Task.FromResult(true); });
            now = now.AddHours(-2);
            Check("expired verification during authorization prevents frame", expired.State == ControlState.CancelledBeforeDispatch && f.Socket.Writes == 0);
            f.Socket.Handle = request => Socket.Method(request) == "thread/read" ? throw new WebSocketException() : Task.FromResult(false);
            var readFailed = await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, Allow);
            Check("read connection failure does not claim uncertain mutation", readFailed.State == ControlState.Failed && f.Socket.Writes == 0);
            f.Socket.Handle = null;
            int framesBeforeApproval = f.Socket.Sent.Count;
            await f.EventAsync("item/commandExecution/requestApproval", new { threadId = f.Target.Task.ThreadId, turnId = f.Target.TurnId, itemId = "synthetic-command" }, "shared-request");
            Check("incoming shared approval receives no automatic response", f.Socket.Sent.Count == framesBeforeApproval);

            const string text = "保留原目录和权限\n`$HOME` $(not-a-shell) \"原样\"";
            Guid steerId = Guid.NewGuid(); var steer = await f.Client.SteerAsync(steerId, f.Target, text, Allow);
            var sent = f.Socket.Sent.Last(x => Socket.Method(x) == "turn/steer").GetProperty("params");
            Check("steer binds literal text and expected turn only", steer.State == ControlState.Accepted && sent.EnumerateObject().Count() == 3
                && sent.GetProperty("threadId").GetString() == f.Target.Task.ThreadId && sent.GetProperty("expectedTurnId").GetString() == f.Target.TurnId
                && sent.GetProperty("input")[0].GetProperty("text").GetString() == text);
            Check("same operation returns receipt without replay", (await f.Client.SteerAsync(steerId, f.Target, text, Allow)) == steer && f.Socket.Writes == 1);
            await Reject("operation identity cannot change text", () => f.Client.SteerAsync(steerId, f.Target, "different", Allow), "operation_identity_conflict");
            await f.EventAsync("turn/completed", new { threadId = f.Target.Task.ThreadId, turn = new { id = f.Target.TurnId, status = "completed" } });
            Check("completed turn does not prove steering effect", f.Client.GetOperation(steerId).State == ControlState.Accepted);

            Guid interruptId = Guid.NewGuid(); var interrupted = await f.Client.InterruptAsync(interruptId, f.Target, Allow);
            Check("interrupt acknowledgement is not observed interruption", interrupted.State == ControlState.Accepted);
            await f.EventAsync("turn/completed", new { threadId = "other-thread", turn = new { id = f.Target.TurnId, status = "interrupted" } });
            await f.EventAsync("turn/completed", new { threadId = f.Target.Task.ThreadId, turn = new { id = "other-turn", status = "interrupted" } });
            Check("foreign task and turn cannot complete operation", f.Client.GetOperation(interruptId).State == ControlState.Accepted);
            await f.EventAsync("turn/completed", new { threadId = f.Target.Task.ThreadId, turn = new { id = f.Target.TurnId, status = "interrupted" } });
            Check("matching event establishes observed interruption", f.Client.GetOperation(interruptId).State == ControlState.Interrupted);

            f.Socket.Handle = request =>
            {
                if (Socket.Method(request) != "turn/interrupt") return Task.FromResult(false);
                f.Socket.Event("turn/completed", new { threadId = f.Target.Task.ThreadId, turn = new { id = f.Target.TurnId, status = "interrupted" } });
                f.Socket.Reply(request, new { }); return Task.FromResult(true);
            };
            Check("early interruption is not downgraded by acknowledgement", (await f.Client.InterruptAsync(Guid.NewGuid(), f.Target, Allow)).State == ControlState.Interrupted);
            f.Socket.Handle = null;
            Guid endedId = Guid.NewGuid(); await f.Client.InterruptAsync(endedId, f.Target, Allow);
            f.Socket.TurnState = "completed";
            Check("natural completion is not reported as successful interruption", (await f.Client.ReconcileAsync(endedId)).Code == "target_turn_ended_without_interruption");
            f.Socket.TurnState = "inProgress";
            Guid readId = Guid.NewGuid(); await f.Client.InterruptAsync(readId, f.Target, Allow);
            f.Socket.TurnState = "interrupted";
            Check("exact read can establish observed interruption", (await f.Client.ReconcileAsync(readId)).State == ControlState.Interrupted);
            f.Socket.TurnState = "inProgress";

            f.Socket.Handle = request =>
            {
                if (Socket.Method(request) != "turn/steer") return Task.FromResult(false);
                f.Socket.Frame(new { id = request.GetProperty("id"), error = new { code = -32602, message = "synthetic-private-diagnostic" } });
                return Task.FromResult(true);
            };
            Check("explicit remote rejection is failed not unknown", (await f.Client.SteerAsync(Guid.NewGuid(), f.Target, text, Allow)).Code == "server_rejected_request");
            f.Socket.Handle = request =>
            {
                if (Socket.Method(request) != "turn/steer") return Task.FromResult(false);
                f.Socket.Reply(request, new { turnId = "wrong" }); return Task.FromResult(true);
            };
            Check("wrong steer acknowledgement remains unknown", (await f.Client.SteerAsync(Guid.NewGuid(), f.Target, text, Allow)).State == ControlState.Unknown);
            f.Socket.Handle = null;

            var atGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = f.Wire.RequestGuardedAsync("synthetic/first", new { }, async _ => { atGate.SetResult(); await release.Task; return true; }, default);
            await atGate.Task.WaitAsync(TimeSpan.FromSeconds(3));
            bool secondGuard = false; using var cancel = new CancellationTokenSource();
            var waiting = f.Wire.RequestGuardedAsync("synthetic/second", new { }, _ => { secondGuard = true; return Task.FromResult(true); }, cancel.Token);
            cancel.Cancel(); bool unsent = false;
            try { await waiting; } catch (RpcTransportException ex) { unsent = !ex.MayHaveBeenWritten; }
            release.SetResult(); await held;
            Check("write gate cancellation is known unsent and never calls guard", unsent && !secondGuard && f.Socket.Sent.All(x => Socket.Method(x) != "synthetic/second"));
            var beforeUnknown = f.Socket.Writes;
            f.Socket.Handle = request => Task.FromResult(Socket.Method(request) == "turn/steer");
            Guid unknownId = Guid.NewGuid(); using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var unknown = await f.Client.SteerAsync(unknownId, f.Target, text, Allow, shortWait.Token);
            Check("lost reply is unknown and repeat does not resend", unknown.State == ControlState.Unknown
                && (await f.Client.SteerAsync(unknownId, f.Target, text, Allow)).State == ControlState.Unknown && f.Socket.Writes == beforeUnknown + 1);
            f.Socket.Handle = request => Task.FromResult(Socket.Method(request) == "turn/interrupt");
            Guid unknownInterruptId = Guid.NewGuid(); using var shortInterrupt = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var unknownInterrupt = await f.Client.InterruptAsync(unknownInterruptId, f.Target, Allow, shortInterrupt.Token);
            int beforeRead = f.Socket.Writes; f.Socket.Handle = null; f.Socket.TurnState = "interrupted";
            Check("unknown interruption reconciles by read without resending", unknownInterrupt.State == ControlState.Unknown
                && (await f.Client.ReconcileAsync(unknownInterruptId)).State == ControlState.Interrupted && f.Socket.Writes == beforeRead);
            f.Socket.TurnState = "inProgress";
            f.Socket.Handle = null;
            Guid droppedId = Guid.NewGuid(); await f.Client.InterruptAsync(droppedId, f.Target, Allow);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Wire.Disconnected += () => disconnected.TrySetResult(); f.Socket.Abort();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check("disconnect freezes pending interruption without reconnect", f.Client.GetOperation(droppedId).State == ControlState.Unknown
                && f.Client.CapabilityFailure("turn/interrupt") == "not_initialized_or_disconnected");
            Check("control never creates resumes modifies policy or responds to approvals", f.Socket.Sent.All(x => Socket.Method(x) is
                "initialize" or "initialized" or "thread/read" or "turn/steer" or "turn/interrupt" or "synthetic/first"));

            var owned = new OwnedServerConfiguration { Connection = Config().Connection, ExpectedVersion = OwnedVerification.SchemaVersion,
                ExpectedPlatformOs = "linux", ExpectedCodexHome = "/synthetic/home", Projects = ImmutableDictionary<string, string>.Empty.Add("sample", "/synthetic/project"),
                Models = ImmutableHashSet.Create("synthetic-model") };
            await Reject("shared endpoint cannot become an owned client", () => { _ = new OwnedAppServerClient(owned, f.Wire); return Task.CompletedTask; }, "owned_backend_required");
            await Reject("shared endpoint cannot launch an owned process", async () =>
                await OwnedProcessConnection.StartAsync(owned.Connection with { Synthetic = false },
                    new OwnedProcessLaunch("unused", Array.Empty<string>(), "unused", new Dictionary<string, string>())), "owned_backend_required");
        }
        catch (Exception ex)
        {
            checks["failure"] = false; await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString());
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "shared-existing-control-checks.json"), JsonSerializer.Serialize(new
        { checks, realNetworkUsed = false, realCodexOperations = 0, productionCredentialsRead = false }, new JsonSerializerOptions { WriteIndented = true }));
        exit(checks.Values.All(value => value) ? 0 : 1); return true;
    }
}
