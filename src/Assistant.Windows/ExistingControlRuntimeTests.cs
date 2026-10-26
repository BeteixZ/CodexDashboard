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

internal static class ExistingControlRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--existing-control-runtime-test"); if (index < 0) return false;
        string root = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks.Add(name, value); if (!value) throw new InvalidOperationException(name); }
        var now = DateTimeOffset.UtcNow;
        var config = new SharedExistingConfiguration
        {
            Connection = new(new("remote-ssh-discovered:synthetic-host", "synthetic-profile"), OwnedBackend.SharedRemoteSocket, true),
            ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = "linux", ExpectedCodexHome = "/synthetic/home",
            Directories = ImmutableHashSet.Create("/synthetic/project"), RequestTimeout = TimeSpan.FromSeconds(2)
        };
        config = config with { Verification = new(config.Connection, config.ExpectedVersion, config.ExpectedPlatformOs, config.ConfigurationDigest(),
            ImmutableHashSet.Create("turn/steer", "turn/interrupt"), VerificationKind.SyntheticReplay, "TEST ONLY synthetic fixture", now.AddMinutes(-1), now.AddHours(1)) };
        var profile = new ConfiguredExistingProfile("demo", "模拟已有任务", config, true);
        try
        {
            var selectionConfig = config with { Connection = config.Connection with { Endpoint = new(config.Connection.Endpoint.HostId) }, Verification = null };
            selectionConfig = selectionConfig with { Verification = config.Verification! with
                { Connection = selectionConfig.Connection, ConfigurationDigest = selectionConfig.ConfigurationDigest() } };
            var selectionProfile = profile with { Configuration = selectionConfig };
            int selectionConnections = 0;
            var selectionSocket = new SharedExistingControlTests.Socket { ThreadId = "selected-thread", TurnId = "next-turn" };
            var observed = new TaskRecord { Id = "selected-thread", Title = "选中的任务", Host = "synthetic-host",
                Cwd = "/synthetic/project", TurnId = "selected-turn", Status = TaskStatusNames.Active, RecordedStatus = TaskStatusNames.Active };
            await using (var selected = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(root, "selection"), modelEnabled: false,
                syntheticTask: observed, existingOptions: new([selectionProfile], (_, _) =>
                { selectionConnections++; return Task.FromResult<IJsonRpcConnection>(SharedRemoteControlConnection.AttachSynthetic(
                    selectionConfig.Connection, RemoteAppServer.AttachSyntheticSocket(selectionSocket))); })))
            {
                selected.Settings.ChatGpt.TaskControlEnabled = true;
                async Task Query(string id, string text) { await selected.InputAsync(id, text); await OwnedHostRuntimeTests.WaitReplyAsync(selected, id); }
                await Query("list", "任务列表"); await Query("choose", "1"); await Query("current", "当前任务");
                Check("selected remote task provides exact copyable commands without model or connection", selected.Sent.Last().Contains("中断已有 demo/selected-thread/selected-turn", StringComparison.Ordinal)
                    && selected.Sent.Last().Contains("纠偏已有 demo/selected-thread/selected-turn：", StringComparison.Ordinal)
                    && selectionConnections == 0 && selected.Script.Calls == 0 && (await selected.ReadAsync()).Operations.IsEmpty);
                observed.TurnId = "next-turn";
                await Query("updated", "当前任务");
                Check("current task command follows freshly observed turn without executing", selected.Sent.Last().Contains("demo/selected-thread/next-turn", StringComparison.Ordinal)
                    && !selected.Sent.Last().Contains("demo/selected-thread/selected-turn", StringComparison.Ordinal));
                observed.Cwd = "/other"; await Query("directory", "当前任务");
                Check("changed directory does not offer a control command", !selected.Sent.Last().Contains("中断已有 demo/", StringComparison.Ordinal));
                observed.Cwd = "/synthetic/project"; observed.Status = TaskStatusNames.Completed; await Query("ended", "当前任务");
                Check("completed task does not offer interruption", selected.Sent.Last().Contains("尚未读到正在运行", StringComparison.Ordinal));
                observed.Status = TaskStatusNames.Active;
                var task = new GatewayTask(new(selectionConfig.Connection.Endpoint.HostId, observed.Id), observed, now, true, "synthetic");
                Check("stale observations do not offer commands", selected.Runtime.ExistingRuntime!.TargetCommands(task with { Fresh = false }).Contains("过期", StringComparison.Ordinal));
                selected.Settings.ChatGpt.TaskControlEnabled = false;
                await Query("disabled", "当前任务");
                Check("disabled control does not advertise usable commands", selected.Sent.Last().Contains("尚未开放", StringComparison.Ordinal));
                selected.Settings.ChatGpt.TaskControlEnabled = true;
                await using var ambiguous = new ExistingControlRuntime(selected.Runtime.Identity!, () => selected.Runtime.Identity, selected.Ledger,
                    new([selectionProfile, selectionProfile with { Id = "duplicate" }]), () => true, () => now);
                Check("multiple matching profiles require explicit scope selection", ambiguous.TargetCommands(task).Contains("没有唯一", StringComparison.Ordinal));
                Check("different profile identity cannot borrow control scope", selected.Runtime.ExistingRuntime.TargetCommands(task with
                    { Identity = new(task.Identity.HostId, task.Identity.ThreadId, "another-profile") }).Contains("没有唯一", StringComparison.Ordinal));
                Check("selection queries never create operations or connect", (await selected.ReadAsync()).Operations.IsEmpty && selectionConnections == 0);
                await Query("stop-current", "中断当前任务");
                var focusedOperation = (await selected.ReadAsync()).Operations.Last();
                Check("focused short command interrupts exact selected remote turn", focusedOperation.Stage == OperationStage.Accepted
                    && focusedOperation.Task?.ThreadId == "selected-thread" && focusedOperation.Task.TurnId == "next-turn"
                    && selectionSocket.Writes == 1 && selectionConnections == 1);
                Check("focused authorization preserves actual short source", (await selected.ReadAsync()).Authorizations
                    .Single(a => a.Id == focusedOperation.AuthorizationId).SourceExcerpt == "中断当前任务");
                selectionSocket.Handle = request =>
                {
                    if (SharedExistingControlTests.Socket.Method(request) == "thread/read") observed.TurnId = "changed-before-write";
                    return Task.FromResult(false);
                };
                await Query("changed-turn", "先停一下");
                Check("observed turn change before actual write invalidates focused authorization", selectionSocket.Writes == 1
                    && (await selected.ReadAsync()).Operations.Last().Stage != OperationStage.Accepted);
                selectionSocket.Handle = null;
                observed.TurnId = "next-turn";
                selected.Settings.ChatGpt.TaskControlEnabled = false;
                await Query("stop-disabled", "停止当前任务");
                Check("disabled short command cannot dispatch", selectionSocket.Writes == 1);
                selected.Settings.ChatGpt.TaskControlEnabled = true;
                await Query("reset-focus", "新对话"); await Query("stop-no-focus", "中断当前任务");
                Check("new dialogue cannot reuse prior remote focus", selectionSocket.Writes == 1
                    && (await selected.ReadAsync()).Context!.Focus == null);
                await Query("steer-no-focus", "纠偏当前任务：只分析");
                Check("focused steer requires an explicit selected task", selectionSocket.Writes == 1 && selected.Sent.Last().Contains("还没有选定", StringComparison.Ordinal));
                await Query("reselect", "切换到 selected-thread");
                await Query("empty-steer", "纠偏当前任务：  ");
                Check("empty focused instruction never dispatches", selectionSocket.Writes == 1);
                string rawInstruction = "  保留原样：第一行\n  第二行  ";
                await Query("steer-current", "纠偏当前任务：" + rawInstruction);
                var steerOperation = (await selected.ReadAsync()).Operations.Last();
                using var steerArguments = JsonDocument.Parse(steerOperation.ArgumentsJson);
                var wireSteer = selectionSocket.Sent.Last(x => SharedExistingControlTests.Socket.Method(x) == "turn/steer");
                Check("focused steer preserves multiline instruction and exact turn", steerOperation.Stage == OperationStage.Accepted
                    && steerOperation.Task?.TurnId == "next-turn" && selectionSocket.Writes == 2
                    && steerArguments.RootElement.GetProperty("text").GetString() == rawInstruction
                    && wireSteer.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString() == rawInstruction);
                Check("focused steer preserves original authorization source", (await selected.ReadAsync()).Authorizations
                    .Single(a => a.Id == steerOperation.AuthorizationId).SourceExcerpt == "纠偏当前任务：" + rawInstruction);
            }
            string settingsRoot = Path.Combine(root, "config"), acceptanceRoot = Path.Combine(root, "acceptance");
            Check("absent config creates nothing", ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.IsEmpty && !Directory.Exists(settingsRoot));
            Directory.CreateDirectory(settingsRoot); Directory.CreateDirectory(acceptanceRoot);
            void Save(string mode = "disabled", string? acceptance = null, string cwd = "/synthetic/project") => File.WriteAllText(
                Path.Combine(settingsRoot, ExistingControlConfiguration.FileName), JsonSerializer.Serialize(new { version = 1,
                    profiles = new[] { new { id = "demo", label = "模拟配置", host = config.Connection.Endpoint.HostId,
                        profile = config.Connection.Endpoint.ProfileId, cwd, codexHome = config.ExpectedCodexHome,
                        cliVersion = config.ExpectedVersion, platform = config.ExpectedPlatformOs, mode, acceptanceId = acceptance } } }));
            Save(); var loaded = ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now);
            Check("file configuration is real and disabled by default", loaded.Profiles.Length == 1 && !loaded.Profiles[0].Enabled && !loaded.Profiles[0].Configuration.Connection.Synthetic);
            var real = loaded.Profiles[0].Configuration;
            Save("verifiedControl", "test");
            Check("missing protected acceptance cannot enable profile", !ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.Single().Enabled);
            var proof = config.Verification! with { Connection = real.Connection, ConfigurationDigest = real.ConfigurationDigest() };
            void Proof(RuntimeVerification evidence) => File.WriteAllBytes(ExistingControlConfiguration.AcceptancePath(acceptanceRoot, "test"),
                new WindowsStateProtector().Protect(JsonSerializer.SerializeToUtf8Bytes(new ExistingAcceptanceDocument(1, "demo", evidence), OwnedControlConfiguration.Json),
                    ExistingControlConfiguration.AcceptancePurpose(acceptanceRoot, "test")));
            Proof(proof);
            Check("protected replay proof cannot enable real connection", !ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.Single().Enabled);
            Proof(proof with { Kind = VerificationKind.RuntimeAcceptance });
            Check("scoped protected runtime fixture loads without connecting", ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.Single().Enabled);
            Save("verifiedControl", "test", "/different");
            Check("directory change invalidates acceptance", !ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.Single().Enabled);
            Save("verifiedControl", "test");
            Proof(proof with { Kind = VerificationKind.RuntimeAcceptance, Methods = ImmutableHashSet.Create("thread/start") });
            Check("acceptance cannot import owned creation capability", !ExistingControlConfiguration.Load(settingsRoot, acceptanceRoot, now).Profiles.Single().Enabled);

            int connections = 0;
            var socket = new SharedExistingControlTests.Socket();
            bool durableAtWrite = true;
            var options = new ExistingHostOptions([profile], (_, _) =>
            {
                connections++;
                return Task.FromResult<IJsonRpcConnection>(SharedRemoteControlConnection.AttachSynthetic(config.Connection, RemoteAppServer.AttachSyntheticSocket(socket)));
            });
            string ledgerPath; ConversationIdentity identity; Guid unknownId;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(root, "host"), modelEnabled: false, existingOptions: options))
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                identity = f.Runtime.Identity!; ledgerPath = f.LedgerPath;
                Check("host activation does not connect", f.Runtime.ExistingRuntime != null && connections == 0);
                var exactTask = new CanonicalTaskIdentity(config.Connection.Endpoint.HostId, socket.ThreadId, config.Connection.Endpoint.ProfileId);
                Check("capability requires exact endpoint and configured directory", f.Runtime.ExistingRuntime!.Capability(exactTask, socket.Cwd, CodexCapability.Interrupt).Enabled
                    && !f.Runtime.ExistingRuntime.Capability(exactTask, "/other", CodexCapability.Interrupt).Enabled);
                async Task Input(string id, string body) { await f.InputAsync(id, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, id); }
                await Input("scopes", "已有控制");
                Check("scope query works with model disabled and no connection", connections == 0 && f.Script.Calls == 0 && f.Sent.Last().Contains("demo", StringComparison.Ordinal));
                await Input("malformed", "中断已有 demo");
                Check("incomplete target cannot create an operation", (await f.ReadAsync()).Operations.IsEmpty && connections == 0);
                socket.Handle = async request =>
                {
                    if (SharedExistingControlTests.Socket.Method(request) is "turn/steer" or "turn/interrupt")
                    {
                        var saved = await f.ReadAsync();
                        durableAtWrite &= saved.Operations.Any(o => o.Stage == OperationStage.Dispatching
                            && saved.Authorizations.Any(a => a.Id == o.AuthorizationId && a.Task == o.Task)
                            && saved.ToolInvocations.Any(i => i.OperationId == o.Id && i.Stage == ToolInvocationStage.Executing));
                    }
                    return false;
                };
                string target = "demo/" + socket.ThreadId + "/" + socket.TurnId;
                await Input("steer", "纠偏已有 " + target + "：保持原目录和权限\n只修当前问题");
                var steer = (await f.ReadAsync()).Operations.Single();
                Check("explicit steer reaches original turn after durable authorization", steer.Stage == OperationStage.Accepted && socket.Writes == 1 && durableAtWrite
                    && steer.Action == "steer_existing_turn" && steer.Task?.TurnId == socket.TurnId && connections == 1 && f.Script.Calls == 0);
                await f.InputAsync("steer", "纠偏已有 " + target + "：保持原目录和权限\n只修当前问题");
                Check("duplicate inbound does not dispatch again", socket.Writes == 1 && (await f.ReadAsync()).Operations.Length == 1);
                await Input("interrupt", "中断已有 " + target);
                var interrupted = (await f.ReadAsync()).Operations.Single(o => o.Action == "interrupt_existing_turn");
                Check("interrupt acknowledgement remains accepted in durable ledger", interrupted.Stage == OperationStage.Accepted && socket.Writes == 2);
                socket.Event("turn/completed", new { threadId = socket.ThreadId, turn = new { id = socket.TurnId, status = "interrupted" } });
                await Wait(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == interrupted.Id).Stage == OperationStage.Interrupted);
                await Wait(() => Task.FromResult(f.Sent.Any(s => s.Contains("指定轮次中断", StringComparison.Ordinal))));
                Check("matching interruption is persisted and naturally reported", f.Runtime.ExistingRuntime!.Failure == null);
                await Input("operation", "操作记录 " + interrupted.Id.ToString("N")[..8]);
                Check("operation query includes existing task and observed state", f.Sent.Last().Contains("中断", StringComparison.Ordinal) && socket.Writes == 2);
                await Input("wrong-turn", "中断已有 demo/" + socket.ThreadId + "/other-turn");
                Check("wrong turn is durably failed without write", (await f.ReadAsync()).Operations.Last().Stage == OperationStage.Failed && socket.Writes == 2);

                f.Settings.ChatGpt.Enabled = true;
                f.Script.Handler = (request, _) => Task.FromResult(request.Observations.IsEmpty
                    ? ToolModelResponse.Call(ExistingControlRuntime.ParseCommand("中断已有 " + target)!) : ToolModelResponse.Reply("未执行。"));
                await Input("injected", "只查看状态，不要中断任务");
                Check("model cannot turn a read request into an interrupt", socket.Writes == 2 && (await f.ReadAsync()).Operations.Length == 3);
                f.Settings.ChatGpt.Enabled = false;
                f.Settings.ChatGpt.TaskControlEnabled = false;
                await Input("disabled", "中断已有 " + target);
                Check("control switch disables explicit shortcut", socket.Writes == 2 && (await f.ReadAsync()).Operations.Length == 3);
                f.Settings.ChatGpt.TaskControlEnabled = true;

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                socket.Handle = async request =>
                {
                    if (SharedExistingControlTests.Socket.Method(request) == "thread/read") { entered.TrySetResult(); await release.Task; }
                    return false;
                };
                await f.InputAsync("superseded", "中断已有 " + target);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                await f.InputAsync("newer", "状态"); release.TrySetResult();
                await OwnedHostRuntimeTests.WaitReplyAsync(f, "newer");
                await Wait(async () => !(await f.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
                Check("new user revision cancels stale mutation before write", socket.Writes == 2);
                socket.Handle = request => Task.FromResult(SharedExistingControlTests.Socket.Method(request) == "turn/steer");
                await Input("unknown", "纠偏已有 " + target + "：只说明结论");
                var unknown = (await f.ReadAsync()).Operations.Last(); unknownId = unknown.Id;
                Check("lost control reply is durably unknown", unknown.Stage == OperationStage.Unknown && socket.Writes == 3);
                await Input("unknown-query", "操作记录 " + unknownId.ToString("N")[..8]);
                Check("unknown query never retries", socket.Writes == 3 && f.Sent.Last().Contains("未知", StringComparison.Ordinal));
                socket.Abort();
                await Wait(() => Task.FromResult(!f.Runtime.ExistingRuntime!.GetCapability("existing.interrupt_existing_turn").Available));
                Check("disconnected existing session disables capability without reconnect", connections == 1);
            }
            await using (var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath), new WindowsStateProtector()))
            {
                var saved = await reopened.ReadConversationAsync(identity);
                Check("protected ledger reopens with exact unknown operation", saved.Operations.Single(o => o.Id == unknownId).Stage == OperationStage.Unknown);
                await using var runtime = new ExistingControlRuntime(identity, () => identity, reopened, options, () => true, () => now);
                Check("new runtime never reconnects or replays saved operations", connections == 1 && socket.Writes == 3 && runtime.GetCapability("existing.interrupt_existing_turn").Available);
            }
        }
        catch (Exception ex) { checks["failure"] = false; File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); }
        File.WriteAllText(Path.Combine(root, "existing-control-runtime-checks.json"), JsonSerializer.Serialize(new
        { checks, realNetworkUsed = false, productionCredentialsRead = false }, new JsonSerializerOptions { WriteIndented = true }));
        exit(checks.Values.All(v => v) ? 0 : 1); return true;
    }
    private static async Task Wait(Func<Task<bool>> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!await ready()) await Task.Delay(20, timeout.Token);
    }
}
