using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class ExistingReconciliationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--existing-reconciliation-test"); if (index < 0) return false;
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks.Add(name, value); if (!value) throw new InvalidOperationException(name); }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var config = new SharedExistingConfiguration
            {
                Connection = new(new("remote-ssh-discovered:synthetic-host", "synthetic-profile"), OwnedBackend.SharedRemoteSocket, true),
                ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = "linux", ExpectedCodexHome = "/synthetic/home",
                Directories = ImmutableHashSet.Create("/synthetic/project"), RequestTimeout = TimeSpan.FromMilliseconds(750)
            };
            config = config with { Verification = new(config.Connection, config.ExpectedVersion, config.ExpectedPlatformOs, config.ConfigurationDigest(),
                ImmutableHashSet.Create("turn/interrupt", "turn/steer"), VerificationKind.SyntheticReplay, "TEST ONLY", now.AddMinutes(-1), now.AddHours(1)) };
            var profile = new ConfiguredExistingProfile("demo", "模拟", config, true);
            string configRoot = Path.Combine(directory, "readonly-config"); Directory.CreateDirectory(configRoot);
            File.WriteAllText(Path.Combine(configRoot, ExistingControlConfiguration.FileName), JsonSerializer.Serialize(new { version = 1,
                profiles = new[] { new { id = "demo", label = "只读模拟", host = config.Connection.Endpoint.HostId,
                    profile = config.Connection.Endpoint.ProfileId, cwd = "/synthetic/project", codexHome = config.ExpectedCodexHome,
                    cliVersion = config.ExpectedVersion, platform = config.ExpectedPlatformOs, mode = "readOnly" } } }));
            var readConfig = ExistingControlConfiguration.Load(configRoot, Path.Combine(directory, "unused-acceptance"), now).Profiles.Single();
            Check("readonly file configuration grants no mutation verification", readConfig.ReadOnly && !readConfig.Enabled && readConfig.Configuration.Verification == null);
            var sockets = new List<SharedExistingControlTests.Socket>();
            string readState = "inProgress", readTurn = "existing-turn";
            Task<IJsonRpcConnection> Connect(ConfiguredExistingProfile value, CancellationToken ct)
            {
                var socket = new SharedExistingControlTests.Socket { TurnState = readState, TurnId = readTurn }; sockets.Add(socket);
                if (sockets.Count == 1) socket.Handle = request => Task.FromResult(SharedExistingControlTests.Socket.Method(request) is "turn/interrupt" or "turn/steer");
                return Task.FromResult<IJsonRpcConnection>(SharedRemoteControlConnection.AttachSynthetic(value.Configuration.Connection, RemoteAppServer.AttachSyntheticSocket(socket)));
            }
            var options = new ExistingHostOptions([profile], Connect);
            string ledgerPath; ConversationIdentity identity; Guid pendingId, steerId;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "host"), modelEnabled: false, existingOptions: options))
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                identity = f.Runtime.Identity!; ledgerPath = f.LedgerPath;
                async Task Input(string id, string text) { await f.InputAsync(id, text); await OwnedHostRuntimeTests.WaitReplyAsync(f, id); }
                await Input("unknown-first", "中断已有 demo/synthetic-existing-thread/existing-turn");
                var first = (await f.ReadAsync()).Operations.Single();
                Check("uncertain interrupt saved with original scope before write", first.Stage == OperationStage.Unknown && sockets[0].Writes == 1
                    && (await f.ReadAsync()).Events.Any(e => e.Body.Contains("shared_existing_dispatch_scope", StringComparison.Ordinal)));
                await Input("read-running", "核对操作 " + first.Id.ToString("N"));
                Check("actual host read keeps running turn unknown without resending", sockets.Count == 2 && sockets.Sum(s => s.Writes) == 1
                    && (await f.ReadAsync()).Operations.Single().Stage == OperationStage.Unknown && f.Sent.Last().Contains("仍在运行", StringComparison.Ordinal));
                readState = "interrupted";
                await Input("read-interrupted", "核对操作 " + first.Id.ToString("D"));
                Check("actual host reconciles interruption through a separate read connection", (await f.ReadAsync()).Operations.Single().Stage == OperationStage.Interrupted
                    && sockets.Sum(s => s.Writes) == 1 && f.Script.Calls == 0);
                int connections = sockets.Count;
                await Input("read-terminal", "核对操作 " + first.Id.ToString("N"));
                Check("terminal operation query does not reconnect", sockets.Count == connections);
                sockets[0].TurnId = "second-turn"; readTurn = "second-turn"; readState = "inProgress";
                await Input("unknown-second", "中断已有 demo/synthetic-existing-thread/second-turn");
                pendingId = (await f.ReadAsync()).Operations.Last().Id;
                await Input("unknown-steer", "纠偏已有 demo/synthetic-existing-thread/second-turn：保留现有约束");
                steerId = (await f.ReadAsync()).Operations.Last().Id;
                Check("later operations retain their distinct original turns", sockets.Sum(s => s.Writes) == 3
                    && (await f.ReadAsync()).Operations.Single(o => o.Id == pendingId).Task?.TurnId == "second-turn");
            }
            await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath), new WindowsStateProtector());
            var readonlyProfile = profile with { Enabled = false, ReadOnly = true, Configuration = config with { Verification = null } };
            int before = sockets.Count;
            await using var runtime = new ExistingControlRuntime(identity, () => identity, ledger, new([readonlyProfile], Connect), () => false, () => DateTimeOffset.UtcNow);
            Check("restored readonly host neither connects nor advertises writes", sockets.Count == before && !runtime.GetCapability("existing.interrupt_existing_turn").Available);
            async Task<InboxMessage> Query(Guid id, bool unresolved = false) => (await ledger.ReceiveAsync(identity, Guid.NewGuid().ToString("N"),
                "核对操作 " + id.ToString("N"), MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow, hasUnresolvedReference: unresolved)).Message;
            var steerQuery = await Query(steerId);
            await runtime.ReconcileOperationAsync(steerQuery, steerId, default);
            Check("readonly reconciliation cannot infer steer effectiveness", sockets.Count == before
                && (await ledger.ReadConversationAsync(identity)).Operations.Single(o => o.Id == steerId).Stage == OperationStage.Unknown);
            var unknown = Guid.NewGuid(); await runtime.ReconcileOperationAsync(await Query(unknown), unknown, default);
            Check("foreign operation id does not cause a connection", sockets.Count == before);
            await runtime.ReconcileOperationAsync(await Query(pendingId, true), pendingId, default);
            Check("unresolved reference cannot trigger reconciliation", sockets.Count == before);
            var staleQuery = await Query(pendingId);
            await ledger.ReceiveAsync(identity, Guid.NewGuid().ToString("N"), "状态", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            await runtime.ReconcileOperationAsync(staleQuery, pendingId, default);
            Check("superseded query does not open a connection", sockets.Count == before);
            await using (var changed = new ExistingControlRuntime(identity, () => identity, ledger,
                new([readonlyProfile with { Configuration = readonlyProfile.Configuration with { Directories = ImmutableHashSet.Create("/other") } }], Connect), () => false, () => now))
            {
                await changed.ReconcileOperationAsync(await Query(pendingId), pendingId, default);
                Check("changed configuration cannot read the old operation target", sockets.Count == before);
            }
            readTurn = "missing-turn";
            await runtime.ReconcileOperationAsync(await Query(pendingId), pendingId, default);
            Check("missing exact turn preserves uncertainty", (await ledger.ReadConversationAsync(identity)).Operations.Single(o => o.Id == pendingId).Stage == OperationStage.Unknown);
            readTurn = "second-turn"; readState = "completed";
            string result = await runtime.ReconcileOperationAsync(await Query(pendingId), pendingId, default);
            var reconciled = (await ledger.ReadConversationAsync(identity)).Operations.Single(o => o.Id == pendingId);
            Check("reopened protected operation reconciles natural completion without claiming interruption", reconciled.Stage == OperationStage.Failed
                && result.Contains("其他状态", StringComparison.Ordinal) && reconciled.Receipt!.Contains("shared_existing_read_reconciliation", StringComparison.Ordinal));
            Check("all reconciliation connections send only initialize and thread read", sockets.Skip(1).All(s => s.Sent.All(frame =>
                SharedExistingControlTests.Socket.Method(frame) is "initialize" or "initialized" or "thread/read")) && sockets.Sum(s => s.Writes) == 3);
            Check("read reconciliation does not enable subsequent mutation capability", !runtime.GetCapability("existing.interrupt_existing_turn").Available);
            var readonlySocket = new SharedExistingControlTests.Socket();
            await using var client = new SharedExistingAppServerClient(config,
                SharedRemoteControlConnection.AttachSynthetic(config.Connection, RemoteAppServer.AttachSyntheticSocket(readonlySocket)), readOnly: true);
            await client.InitializeAsync(); bool rejected = false;
            try { await client.InterruptAsync(Guid.NewGuid(), new(new(config.Connection.Endpoint.HostId, readonlySocket.ThreadId, config.Connection.Endpoint.ProfileId), readonlySocket.Cwd, readonlySocket.TurnId), _ => Task.FromResult(true)); }
            catch (ControlException ex) { rejected = ex.Code == "read_only_reconciliation_connection"; }
            Check("typed readonly client refuses control even with valid verification", rejected && readonlySocket.Writes == 0);
        }
        catch (Exception ex) { checks["failure"] = false; File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); }
        File.WriteAllText(Path.Combine(directory, "existing-reconciliation-checks.json"), JsonSerializer.Serialize(new
        { checks, realNetworkUsed = false, productionCredentialsRead = false }, new JsonSerializerOptions { WriteIndented = true }));
        exit(checks.Values.All(v => v) ? 0 : 1); return true;
    }
}
