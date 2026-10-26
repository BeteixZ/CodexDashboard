using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Control;
using CodexDashboard;

internal static class ProvenanceTests
{
    private static CreateOwnedTask Request(OwnedServerConfiguration config)
        => new("sample", config.Projects["sample"], "synthetic-model", "on-request", "workspace-write");

    private static async Task<bool> Rejected(Func<Task> action, string? expected = null)
    {
        try { await action(); return false; }
        catch (ProvenanceException ex) { return expected == null || ex.Code == expected; }
        catch (ControlException ex) { return expected == null || ex.Code == expected; }
    }

    internal static async Task RunAsync(string directory, OwnedServerConfiguration config, DateTimeOffset now, Action<string, bool> check)
    {
        string root = Path.Combine(directory, "provenance");
        Directory.CreateDirectory(root);
        await using (var missing = new OwnedAppServerClient(config, new FakeRpc(config), () => now))
        {
            await missing.InitializeAsync();
            check("create without protected store fails before dispatch",
                await Rejected(() => missing.CreateAsync(Guid.NewGuid(), Request(config)), "provenance_store_required"));
        }

        var memory = new MemoryProvenance();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.BeforeAppend = ct => { entered.TrySetResult(); return release.Task.WaitAsync(ct); };
        Guid createOperation = Guid.NewGuid();
        OwnedTaskHandle original;
        await using (var store = new OwnedProvenanceStore(config, memory, new TestProtector()))
        await using (var client = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
        {
            await client.InitializeAsync();
            var pending = client.CreateAsync(createOperation, Request(config));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check("create has no accepted handle before durable acknowledgement", !pending.IsCompleted
                && client.GetOperation(createOperation).State == ControlState.Dispatching && client.GetOperation(createOperation).Task == null);
            release.SetResult();
            var result = await pending;
            original = result.Task!;
            check("durable create returns original operation evidence and scoped access", result.Receipt.State == ControlState.Accepted
                && original.Access == OwnedTaskAccess.CreatedThisSession && original.Creation.CreateOperationId == createOperation
                && original.Creation.ReceiptDigest.Length == 64 && original.Creation.ServerVersion == config.ExpectedVersion);
            string plaintext = Encoding.UTF8.GetString(new TestProtector().Unprotect(memory.Records.Single().Payload, store.Purpose(1)));
            check("proof holds safe creation fields without conversation or credentials", plaintext.Contains(createOperation.ToString())
                && plaintext.Contains("ReceiptDigest") && !plaintext.Contains("preview") && !plaintext.Contains("token"));
            bool reused = await Rejected(() => Task.Run(() => new OwnedAppServerClient(config, new FakeRpc(config), () => now, store)));
            check("one provenance instance cannot attach to another live client", reused);
        }
        memory.BeforeAppend = null;

        // Local catalogue needs neither a client nor initialization. Its DTO cannot be supplied
        // to mutation methods and deliberately contains no current state or turn observation.
        await using (var catalogue = new OwnedProvenanceStore(config with { Verification = null }, memory, new TestProtector()))
        {
            var facts = await catalogue.ReadCreationFactsAsync(now);
            check("local protected catalogue discovers exact creation without client or RPC", facts.Count == 1
                && facts[0].Task == original.Identity && facts[0].Creation == original.Creation
                && facts[0].ProjectId == "sample" && facts[0].Cwd == Request(config).Cwd);
            check("catalogue facts provide no control handle or current turn claim",
                typeof(OwnedCreationFact).GetProperties().All(p => p.PropertyType != typeof(OwnedTaskHandle)
                    && p.Name is not ("State" or "CurrentTurnId" or "ObservedAt")));
        }

        foreach (bool mismatched in new[] { false, true })
        {
            await using var resumeStore = new OwnedProvenanceStore(config, memory, new TestProtector());
            var resumeRpc = new FakeRpc(config);
            resumeRpc.Threads[original.Identity.ThreadId] = FakeRpc.Thread(original.Identity.ThreadId, Request(config).Cwd);
            await using var resumeClient = new OwnedAppServerClient(config, resumeRpc, () => now, resumeStore);
            await resumeClient.InitializeAsync();
            var restoredTask = (await resumeClient.RestoreReadOnlyAsync()).Single();
            check("resume requires observed idle " + mismatched, await Rejected(() => resumeClient.ResumeAsync(Guid.NewGuid(), restoredTask, 1), "thread_not_observed_idle"));
            await resumeClient.ReconcileAsync(restoredTask);
            if (!mismatched)
            {
                var unarchived = await resumeClient.UnarchiveAsync(Guid.NewGuid(), restoredTask, resumeClient.Snapshot(restoredTask).Revision);
                check("restored task can explicitly unarchive without enabling execution", unarchived.State == ControlState.Accepted
                    && restoredTask.Access == OwnedTaskAccess.RestoredReadOnly && resumeRpc.Calls.All(c => c.Method != "turn/start"));
                await resumeClient.ReconcileAsync(restoredTask);
            }
            if (mismatched) resumeRpc.Override = (method, parameters, ct) => method == "thread/resume"
                ? Task.FromResult(FakeRpc.J(new { thread = FakeRpc.Thread("wrong-thread", Request(config).Cwd) }))
                : Task.FromResult(resumeRpc.Reply(method, parameters));
            var receipt = await resumeClient.ResumeAsync(Guid.NewGuid(), restoredTask, resumeClient.Snapshot(restoredTask).Revision);
            check("resume validates same identity " + mismatched, mismatched
                ? receipt.State == ControlState.Unknown && restoredTask.Access == OwnedTaskAccess.RestoredReadOnly
                : receipt.State == ControlState.Accepted && restoredTask.Access == OwnedTaskAccess.ResumedThisSession);
            if (!mismatched)
            {
                check("resume itself never starts work", resumeRpc.Calls.All(c => c.Method != "turn/start"));
                var continued = await resumeClient.StartTurnAsync(Guid.NewGuid(), restoredTask, resumeClient.Snapshot(restoredTask).Revision, "explicit new work");
                check("explicit continuation retains restored identity", continued.Task == original.Identity && continued.State == ControlState.Accepted);
            }
        }
        await using (var reconnectStore = new OwnedProvenanceStore(config, new MemoryProvenance(), new TestProtector()))
        {
            var rpc = new FakeRpc(config);
            await using var client = new OwnedAppServerClient(config, rpc, () => now, reconnectStore);
            await client.InitializeAsync();
            var first = (await client.CreateAsync(Guid.NewGuid(), Request(config))).Task!;
            var other = (await client.CreateAsync(Guid.NewGuid(), Request(config))).Task!;
            rpc.Disconnect();
            var replacement = new FakeRpc(config);
            foreach (var entry in rpc.Threads) replacement.Threads[entry.Key] = entry.Value;
            await client.ReconnectForReadAsync(replacement);
            check("reconnect invalidates every handle and requires fresh observation", first.Access == OwnedTaskAccess.RestoredReadOnly
                && other.Access == OwnedTaskAccess.RestoredReadOnly && client.Snapshot(first).State == OwnedTaskState.Unknown
                && await Rejected(() => client.ResumeAsync(Guid.NewGuid(), first, client.Snapshot(first).Revision), "thread_not_observed_idle"));
            await client.ReconcileAsync(first);
            var resumed = await client.ResumeAsync(Guid.NewGuid(), first, client.Snapshot(first).Revision);
            check("explicit reconnect resume targets only original task without replay", resumed.State == ControlState.Accepted
                && first.Access == OwnedTaskAccess.ResumedThisSession && other.Access == OwnedTaskAccess.RestoredReadOnly
                && replacement.Calls.All(c => c.Method is not ("turn/start" or "thread/start")));
            await client.ReconcileAsync(other);
            check("resuming one handle never unlocks another", await Rejected(() => client.StartTurnAsync(Guid.NewGuid(), other,
                client.Snapshot(other).Revision, "must remain blocked"), "restored_provenance_read_only"));
            var started = await client.StartTurnAsync(Guid.NewGuid(), first, client.Snapshot(first).Revision, "explicit new request");
            check("reconnected resumed handle accepts one new explicit turn", started.State == ControlState.Accepted
                && replacement.Calls.Count(c => c.Method == "turn/start") == 1);
        }
        // Restoration needs exact initialization but no runtime write evidence.
        var readConfig = config with { Verification = null };
        await using (var store = new OwnedProvenanceStore(readConfig, memory, new TestProtector()))
        {
            var rpc = new FakeRpc(readConfig);
            rpc.Threads[original.Identity.ThreadId] = FakeRpc.Thread(original.Identity.ThreadId, Request(config).Cwd,
                FakeRpc.Turn("restored-turn", "inProgress"));
            await using var client = new OwnedAppServerClient(readConfig, rpc, () => now, store);
            await client.InitializeAsync();
            var restored = (await client.RestoreReadOnlyAsync()).Single();
            var observations = new List<ControlObservation>();
            var observer = Task.Run(async () =>
            {
                await foreach (var observation in client.ReadObservationsAsync())
                    lock (observations) observations.Add(observation);
            });
            check("restart restores exact identity and creation proof without assuming task state", restored.Identity == original.Identity
                && restored.Creation == original.Creation && restored.Access == OwnedTaskAccess.RestoredReadOnly
                && client.Snapshot(restored).State == OwnedTaskState.Unknown);
            var read = await client.ReconcileAsync(restored);
            check("restored task can reconcile observed running while permanently write blocked", read.WritesRemainBlocked
                && read.Task.State == OwnedTaskState.Running);
            check("restored task cannot start turn", await Rejected(() => client.StartTurnAsync(Guid.NewGuid(), restored,
                read.Task.Revision, "must not send"), "restored_provenance_read_only"));
            check("restored task cannot steer or interrupt", await Rejected(() => client.SteerAsync(Guid.NewGuid(), restored,
                read.Task.Revision, "restored-turn", "no")) && await Rejected(() => client.InterruptAsync(Guid.NewGuid(), restored,
                read.Task.Revision, "restored-turn")));
            check("restored task cannot rename archive or unarchive", await Rejected(() => client.RenameAsync(Guid.NewGuid(), restored,
                read.Task.Revision, "no")) && await Rejected(() => client.ArchiveAsync(Guid.NewGuid(), restored, read.Task.Revision))
                && await Rejected(() => client.UnarchiveAsync(Guid.NewGuid(), restored, read.Task.Revision)));
            client.SetFocus(restored);
            rpc.Emit("item/commandExecution/requestApproval", new { threadId = restored.Identity.ThreadId, turnId = "restored-turn",
                itemId = "item", command = "echo ignored", cwd = Request(config).Cwd, startedAtMs = now.ToUnixTimeMilliseconds() }, "request");
            check("restored task never exposes actionable approvals", client.PendingApprovals().Count == 0
                && await Rejected(() => client.RespondApprovalAsync(Guid.NewGuid(), "forged-token", restored.Identity, "restored-turn",
                    "sample", 1, ApprovalDecision.Accept)) && rpc.Responses.Count == 0);
            check("restoration and reconciliation send only initialization and read requests",
                rpc.Calls.All(x => x.Method is "initialize" or "initialized" or "thread/read"));
            await client.DisposeAsync();
            await observer.WaitAsync(TimeSpan.FromSeconds(2));
            check("uncorrelated thread read publishes snapshot evidence rather than new lifecycle",
                observations.Any(o => o.Method == "thread/read" && o.Code == "read_snapshot" && o.OperationId == null)
                && observations.All(o => o.Method != "thread/read" || o.Code != "turn_lifecycle_observed"));
        }
        var extended = new MemoryProvenance();
        extended.Records.AddRange(memory.Records);
        await using (var store = new OwnedProvenanceStore(config, extended, new TestProtector()))
        {
            var rpc = new FakeRpc(config);
            rpc.Threads[original.Identity.ThreadId] = FakeRpc.Thread(original.Identity.ThreadId, Request(config).Cwd);
            await using var client = new OwnedAppServerClient(config, rpc, () => now, store);
            await client.InitializeAsync();
            var restored = (await client.RestoreReadOnlyAsync()).Single();
            await client.ReconcileAsync(restored);
            check("historical create operation cannot be replayed", await Rejected(
                () => client.CreateAsync(createOperation, Request(config)), "create_operation_already_recorded_restore_read_only")
                && rpc.Calls.All(x => x.Method != "thread/start"));
            var fresh = await client.CreateAsync(Guid.NewGuid(), Request(config));
            check("new operation appends a distinct task after restart", fresh.Receipt.State == ControlState.Accepted
                && fresh.Task!.Identity != restored.Identity && extended.Records.Count == 2);
            check("new creation does not promote historical task", restored.Access == OwnedTaskAccess.RestoredReadOnly
                && await Rejected(() => client.StartTurnAsync(Guid.NewGuid(), restored, client.Snapshot(restored).Revision, "no")));
            var turn = await client.StartTurnAsync(Guid.NewGuid(), fresh.Task!, client.Snapshot(fresh.Task!).Revision, "new work");
            check("newly created task accepts explicit work after restart", turn.State == ControlState.Accepted);
        }

        // A lost backend reply never generates provenance or an importable handle.
        var unknownMemory = new MemoryProvenance();
        await using (var store = new OwnedProvenanceStore(config, unknownMemory, new TestProtector()))
        {
            var rpc = new FakeRpc(config);
            await using var client = new OwnedAppServerClient(config, rpc, () => now, store);
            await client.InitializeAsync();
            rpc.Override = (method, p, _) =>
            {
                rpc.Reply(method, p); // Server side effect occurred but its reply was lost.
                throw new RpcTransportException(true, "synthetic_lost_reply");
            };
            var result = await client.CreateAsync(Guid.NewGuid(), Request(config));
            check("unknown server create cannot be adopted or automatically retried", result.Task == null
                && result.Receipt.State == ControlState.Unknown && unknownMemory.Records.Count == 0
                && rpc.Calls.Count(x => x.Method == "thread/start") == 1);
        }
        await using (var store = new OwnedProvenanceStore(config, unknownMemory, new TestProtector()))
        await using (var client = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
        {
            await client.InitializeAsync();
            check("no ownership is invented when uncertain create has no durable proof", (await client.RestoreReadOnlyAsync()).Count == 0);
        }

        foreach (var stage in Enum.GetValues<ProvenanceWriteStage>())
        {
            string path = Path.Combine(root, "fault-" + stage + ".proofs");
            var storage = new FileOwnedProvenanceStorage(path, false, at => { if (at == stage) throw new IOException("synthetic write fault"); });
            Guid operation = Guid.NewGuid();
            await using (var store = new OwnedProvenanceStore(config, storage, new TestProtector()))
            {
                var rpc = new FakeRpc(config);
                await using var client = new OwnedAppServerClient(config, rpc, () => now, store);
                await client.InitializeAsync();
                var result = await client.CreateAsync(operation, Request(config));
                check($"storage fault {stage} preserves Unknown operation without owned handle", result.Task == null
                    && result.Receipt.State == ControlState.Unknown && result.Receipt.Code == "provenance_commit_unconfirmed"
                    && client.GetOperation(operation).State == ControlState.Unknown);
                check($"storage fault {stage} poisons further writes with no retry", await Rejected(
                    () => client.CreateAsync(Guid.NewGuid(), Request(config)), "provenance_recovery_required")
                    && rpc.Calls.Count(x => x.Method == "thread/start") == 1);
            }
            byte[] preserved = await File.ReadAllBytesAsync(path);
            await using (var store = new OwnedProvenanceStore(config, new FileOwnedProvenanceStorage(path, true), new TestProtector()))
            await using (var client = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
            {
                await client.InitializeAsync();
                if (stage is ProvenanceWriteStage.BeforeWrite or ProvenanceWriteStage.AfterHeader or ProvenanceWriteStage.AfterPayload)
                {
                    bool rejected = await Rejected(() => client.RestoreReadOnlyAsync());
                    byte[] after = await File.ReadAllBytesAsync(path);
                    check($"torn {stage} record blocks restoration without truncating", rejected && preserved.SequenceEqual(after));
                }
                else
                {
                    var restored = await client.RestoreReadOnlyAsync();
                    check($"reopened {stage} adopts only complete authenticated proof", restored.Count == (stage == ProvenanceWriteStage.BeforeWrite ? 0 : 1)
                        && restored.All(x => x.Access == OwnedTaskAccess.RestoredReadOnly && x.Creation.CreateOperationId == operation));
                }
            }
        }
        await CancellationAndProtectionAsync(config, now, check);
        await LateCommitAsync(config, now, check);
        await TamperAsync(config, now, memory.Records.Single(), check);
        await ProcessRestartAsync(root, config, now, check);
    }

    private static async Task CancellationAndProtectionAsync(OwnedServerConfiguration config, DateTimeOffset now, Action<string, bool> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new MemoryProvenance { BeforeAppend = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using (var store = new OwnedProvenanceStore(config, memory, new TestProtector()))
        await using (var client = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
        {
            await client.InitializeAsync();
            using var cancel = new CancellationTokenSource();
            var create = client.CreateAsync(Guid.NewGuid(), Request(config), cancel.Token);
            await entered.Task; cancel.Cancel();
            var result = await create;
            check("cancellation during persistence is Unknown and never exposes a handle", result.Receipt.State == ControlState.Unknown
                && result.Task == null && memory.Records.Count == 0 && client.CapabilityFailure("thread/start") == "provenance_recovery_required");
        }
        var protectedMemory = new MemoryProvenance();
        await using (var store = new OwnedProvenanceStore(config, protectedMemory, new FailingProtector()))
        await using (var client = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
        {
            await client.InitializeAsync();
            var result = await client.CreateAsync(Guid.NewGuid(), Request(config));
            check("encryption failure never falls back to plaintext", result.Receipt.State == ControlState.Unknown && result.Task == null
                && result.Receipt.Code == "provenance_protection_failed" && protectedMemory.Records.Count == 0);
        }
        var readOnlyStorage = new MemoryProvenance { IsReadOnly = true };
        await using (var store = new OwnedProvenanceStore(config, readOnlyStorage, new TestProtector()))
        {
            var rpc = new FakeRpc(config);
            await using var client = new OwnedAppServerClient(config, rpc, () => now, store);
            await client.InitializeAsync();
            check("read-only provenance storage prevents create before server effect", await Rejected(
                () => client.CreateAsync(Guid.NewGuid(), Request(config)), "provenance_storage_not_writable") && rpc.Calls.Count == 2);
        }
    }

    private static async Task LateCommitAsync(OwnedServerConfiguration config, DateTimeOffset now, Action<string, bool> check)
    {
        foreach (string scenario in new[] { "cancel", "disconnect", "dispose" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var storage = new MemoryProvenance
            {
                // Acknowledgement is delayed after the durable write. Deliberately ignores cancellation.
                AfterCommit = _ => { entered.TrySetResult(); return release.Task; }
            };
            Guid operationId = Guid.NewGuid();
            await using (var store = new OwnedProvenanceStore(config, storage, new TestProtector()))
            {
                var rpc = new FakeRpc(config);
                await using var client = new OwnedAppServerClient(config, rpc, () => now, store);
                await client.InitializeAsync();
                using var cancellation = new CancellationTokenSource();
                var create = client.CreateAsync(operationId, Request(config), cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (scenario == "cancel") cancellation.Cancel();
                if (scenario == "disconnect") rpc.Disconnect();
                if (scenario == "dispose") await client.DisposeAsync();
                check($"in-flight persistence after {scenario} is never detached", !create.IsCompleted && storage.Records.Count == 1);
                release.SetResult();
                var result = await create.WaitAsync(TimeSpan.FromSeconds(2));
                check($"late durable commit after {scenario} exposes no owned handle", result.Task == null
                    && result.Receipt.State == ControlState.Unknown && client.GetOperation(operationId).State == ControlState.Unknown);
            }
            storage.AfterCommit = null;
            await using (var store = new OwnedProvenanceStore(config, storage, new TestProtector()))
            await using (var restored = new OwnedAppServerClient(config, new FakeRpc(config), () => now, store))
            {
                await restored.InitializeAsync();
                var proof = (await restored.RestoreReadOnlyAsync()).Single();
                check($"surviving committed proof after {scenario} is restorable only read-only",
                    proof.Creation.CreateOperationId == operationId && proof.Access == OwnedTaskAccess.RestoredReadOnly
                    && restored.CapabilityFailure("turn/start") == "restored_provenance_read_only");
            }
        }
    }

    private static async Task TamperAsync(OwnedServerConfiguration config, DateTimeOffset now, ProtectedOwnedProvenance good,
        Action<string, bool> check)
    {
        async Task<bool> RejectRecord(OwnedServerConfiguration scope, ProtectedOwnedProvenance record)
        {
            var localStorage = new MemoryProvenance(); localStorage.Records.Add(record);
            await using (var local = new OwnedProvenanceStore(scope, localStorage, new TestProtector()))
                if (!await Rejected(() => local.ReadCreationFactsAsync(now))) return false;
            var storage = new MemoryProvenance(); storage.Records.Add(record);
            await using var store = new OwnedProvenanceStore(scope, storage, new TestProtector());
            var rpc = new FakeRpc(scope);
            await using var client = new OwnedAppServerClient(scope, rpc, () => now, store);
            await client.InitializeAsync();
            return await Rejected(() => client.RestoreReadOnlyAsync()) && rpc.Calls.Count == 2;
        }
        byte[] modified = (byte[])good.Payload.Clone(); modified[^1] ^= 1;
        check("ciphertext modification cannot forge ownership", await RejectRecord(config, good with { Payload = modified }));
        check("plaintext model owner declaration cannot forge ownership", await RejectRecord(config,
            new(1, Encoding.UTF8.GetBytes("""{"owner":"assistant","thread":"forged","CreateOperationId":"claimed"}"""))));
        check("provenance sequence substitution is rejected", await RejectRecord(config, good with { Sequence = 2 }));
        var changes = new Dictionary<string, OwnedServerConfiguration>
        {
            ["profile"] = config with { Connection = config.Connection with { Endpoint = new("local", "other-profile") } },
            ["endpoint and backend"] = config with { Connection = new(new("remote-ssh-discovered:synthetic", "synthetic-profile"), OwnedBackend.SshStdio, true) },
            ["synthetic versus production"] = config with { Connection = config.Connection with { Synthetic = false } },
            ["home"] = config with { ExpectedCodexHome = config.ExpectedCodexHome + "-other" },
            ["cwd"] = config with { Projects = config.Projects.SetItem("sample", config.Projects["sample"] + "-other") },
            ["model"] = config with { Models = ImmutableHashSet.Create("other-model") }
        };
        foreach (var change in changes)
            check($"restoration rejects changed {change.Key}", await RejectRecord(change.Value, good));

        await using var purposeStore = TestProvenance.New(config);
        var protector = new TestProtector();
        byte[] purpose = purposeStore.Purpose(1);
        var node = JsonNode.Parse(protector.Unprotect(good.Payload, purpose))!;
        foreach (string field in new[] { "CreateOperationId", "ServerVersion", "ReceiptDigest", "ConnectionEpoch" })
        {
            var invalid = node.DeepClone();
            invalid["Creation"]![field] = field is "CreateOperationId" or "ConnectionEpoch" ? Guid.Empty.ToString() : "invalid";
            check($"authenticated record still requires valid {field}", await RejectRecord(config,
                new(1, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(invalid), purpose))));
        }
        var missing = node.DeepClone(); missing.AsObject().Remove("Creation");
        check("authenticated record missing original create evidence is rejected", await RejectRecord(config,
            new(1, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(missing), purpose))));
        var extra = node.DeepClone(); extra["ModelOwnershipClaim"] = "trusted";
        check("unrecognized fields do not expand provenance schema", await RejectRecord(config,
            new(1, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(extra), purpose))));
        string duplicate = node.ToJsonString().Replace("\"Schema\":1,", "\"Schema\":1,\"Schema\":1,", StringComparison.Ordinal);
        check("authenticated duplicate JSON fields are rejected", await RejectRecord(config,
            new(1, protector.Protect(Encoding.UTF8.GetBytes(duplicate), purpose))));
    }

    private static async Task ProcessRestartAsync(string root, OwnedServerConfiguration config, DateTimeOffset now, Action<string, bool> check)
    {
        string configPath = Path.Combine(root, "synthetic-configuration.json"), storePath = Path.Combine(root, "process.proofs");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ProcessFixture(config, now)));
        using (var writer = StartChild("--provenance-create-child", configPath, storePath))
        {
            try
            {
                string line = await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? "";
                using var written = JsonDocument.Parse(line);
                check("real child acknowledges only durable owned creation", written.RootElement.GetProperty("durable").GetBoolean());
                bool exclusive = false;
                try { await using var second = new FileOwnedProvenanceStorage(storePath); }
                catch (IOException) { exclusive = true; }
                check("provenance file excludes a second writer process", exclusive);
                // Simulate abrupt exit after acknowledged flush; no client or storage Dispose runs.
                writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                check("synthetic writer crash has exited", writer.HasExited);
            }
            finally { if (!writer.HasExited) { writer.Kill(true); await writer.WaitForExitAsync(); } }
        }
        using (var reader = StartChild("--provenance-restore-child", configPath, storePath))
        {
            try
            {
                string output = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (reader.ExitCode != 0) throw new Exception(await reader.StandardError.ReadToEndAsync());
                using var result = JsonDocument.Parse(output);
                check("different real process restores proof read-only after crash", result.RootElement.GetProperty("restoredReadOnly").GetBoolean());
                check("real restarted process sends no control mutations", result.RootElement.GetProperty("onlyReads").GetBoolean());
                check("real restarted process preserves original create operation", result.RootElement.GetProperty("originalOperation").GetBoolean());
            }
            finally { if (!reader.HasExited) { reader.Kill(true); await reader.WaitForExitAsync(); } }
        }
        byte[] bytes = await File.ReadAllBytesAsync(storePath);
        check("disk proof does not expose plaintext project or thread data", !Encoding.UTF8.GetString(bytes).Contains(config.Projects["sample"])
            && !Encoding.UTF8.GetString(bytes).Contains("synthetic-model"));
        string corrupt = Path.Combine(root, "modified-frame.proofs");
        bytes[60] ^= 1; await File.WriteAllBytesAsync(corrupt, bytes);
        await using (var storage = new FileOwnedProvenanceStorage(corrupt, true))
        {
            bool rejected = await Rejected(async () => { await storage.ReadAllAsync(); });
            byte[] after = await File.ReadAllBytesAsync(corrupt);
            check("file frame tamper is preserved and marked recovery required", rejected && storage.RequiresRecovery && bytes.SequenceEqual(after));
        }
    }

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProvenanceTests).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Synthetic child launch failed.");
    }

    internal static async Task RunChildAsync(string[] arguments)
    {
        var fixture = JsonSerializer.Deserialize<ProcessFixture>(await File.ReadAllTextAsync(arguments[1]))!;
        var config = fixture.Configuration; string path = arguments[2];
        // These modes only ever use FakeRpc. They cannot launch a Codex process.
        if (!config.Connection.Synthetic) throw new InvalidOperationException("Synthetic fixture required.");
        bool create = arguments[0] == "--provenance-create-child";
        await using var store = new OwnedProvenanceStore(config, new FileOwnedProvenanceStorage(path, !create), new TestProtector());
        var rpc = new FakeRpc(config);
        await using var client = new OwnedAppServerClient(config, rpc, () => fixture.Now, store);
        await client.InitializeAsync();
        if (create)
        {
            var result = await client.CreateAsync(Guid.NewGuid(), Request(config));
            await File.WriteAllTextAsync(path + ".synthetic-expected", JsonSerializer.Serialize(result.Task!.Creation));
            Console.WriteLine(JsonSerializer.Serialize(new { durable = result.Receipt.State == ControlState.Accepted }));
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.Infinite);
        }
        else
        {
            var restored = (await client.RestoreReadOnlyAsync()).Single();
            rpc.Threads[restored.Identity.ThreadId] = FakeRpc.Thread(restored.Identity.ThreadId, Request(config).Cwd);
            var observed = await client.ReconcileAsync(restored);
            bool blocked = await Rejected(() => client.StartTurnAsync(Guid.NewGuid(), restored, observed.Task.Revision, "never dispatch"));
            var expected = JsonSerializer.Deserialize<OwnedCreationEvidence>(await File.ReadAllTextAsync(path + ".synthetic-expected"));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                restoredReadOnly = restored.Access == OwnedTaskAccess.RestoredReadOnly && observed.WritesRemainBlocked && blocked,
                onlyReads = rpc.Calls.All(x => x.Method is "initialize" or "initialized" or "thread/read"),
                originalOperation = restored.Creation == expected
            }));
        }
    }
    private sealed record ProcessFixture(OwnedServerConfiguration Configuration, DateTimeOffset Now);
    private sealed class FailingProtector : IOwnedProvenanceProtector
    {
        public string Scheme => "synthetic-failing";
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose) => throw new CryptographicException("synthetic");
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose) => throw new CryptographicException("synthetic");
    }
}
