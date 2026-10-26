using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Complete host/ledger/tool loop, with synthetic model, HTTP and owned RPC.
internal static class OwnedTurnAmendRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--owned-turn-amend-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--owned-turn-amend-runtime-test")
                throw new ArgumentException("Explicit fresh output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Fresh output required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool condition)
        {
            checks.Add(name, condition);
            File.WriteAllText(Path.Combine(directory, "owned-turn-amend-runtime-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, actualModelUsed = false, actualCodexProcesses = 0, productionDataRead = false, checks }, Settings.Json));
            if (!condition) throw new InvalidOperationException(name);
        }
        await AcceptedAsync(Path.Combine(directory, "accepted"), Check);
        await ChangedTurnAsync(Path.Combine(directory, "changed-turn"), Check);
        await ChangedInputAsync(Path.Combine(directory, "changed-input"), Check);
        await UnknownRestartAsync(Path.Combine(directory, "unknown-restart"), Check);
    }

    private static async Task AcceptedAsync(string directory, Action<string, bool> check)
    {
        await using var h = await Harness.CreateRunningAsync(directory);
        var f = h.Host; var target = h.Current; string turn = target.Snapshot.CurrentTurnId!;
        string body = "补充 demo/" + target.Handle.Identity.ThreadId + " 先核对测试结果并保留已有约束";
        ToolModelRequest? following = null;
        f.Script.Handler = (request, _) =>
        {
            if (request.Observations.IsEmpty) return Task.FromResult(ToolModelResponse.Call(Steer(target, turn, body)));
            following = request;
            return Task.FromResult(ToolModelResponse.Reply("补充要求已受理，尚未确认已经生效。"));
        };
        await f.InputAsync("amend", body); await OwnedHostRuntimeTests.WaitReplyAsync(f, "amend");
        var state = await f.ReadAsync();
        var operation = state.Operations.Single(x => x.Action == "steer_turn");
        var invocation = state.ToolInvocations.Single(x => x.OperationId == operation.Id);
        var grant = state.Authorizations.Single(x => x.Id == operation.AuthorizationId);
        var wire = h.Rpc.Steers.Single();
        check("actual_loop_steer_binds_exact_current_source_turn_and_durable_dispatch",
            h.Rpc.DispatchProofs.SequenceEqual(new[] { true }) && operation.Stage == OperationStage.Accepted
            && invocation.Stage == ToolInvocationStage.Completed && operation.SourceMessageId == WeChatText.Hash("amend")
            && grant.SourceExcerpt == body && operation.Task?.TurnId == turn
            && wire.GetProperty("threadId").GetString() == target.Handle.Identity.ThreadId
            && wire.GetProperty("expectedTurnId").GetString() == turn
            && wire.GetProperty("clientUserMessageId").GetString() == operation.Id.ToString("D")
            && wire.GetProperty("input")[0].GetProperty("text").GetString() == body);
        var observed = following?.Observations.Single(x => x.ToolName == "steer_turn");
        using var result = JsonDocument.Parse(observed?.ResultJson ?? "{}");
        check("accepted_amendment_is_not_claimed_applied_or_completed",
            result.RootElement.GetProperty("status").GetString() == "accepted"
            && !result.RootElement.GetProperty("facts").GetProperty("independentlyVerified").GetBoolean()
            && !result.RootElement.GetProperty("facts").GetProperty("automaticRetry").GetBoolean()
            && h.Current.Snapshot.State == OwnedTaskState.Running && h.Current.Snapshot.CurrentTurnId == turn
            && f.Sent.Contains("补充要求已受理，尚未确认已经生效。")
            && !state.Events.Any(x => x.RelatedOperationId == operation.Id && x.Body.Contains("\"phase\":\"Completed\"", StringComparison.Ordinal)));
        int modelCalls = f.Script.Calls;
        await f.InputAsync("amend", body);
        var duplicate = await f.Ledger.ReceiveAsync(f.Runtime.Identity!, WeChatText.Hash("amend"), body,
            MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Runtime.StopSessionAsync();
        var final = await f.Ledger.ReadConversationAsync(h.Identity);
        check("accepted_amendment_duplicate_preserves_one_operation_without_second_rpc",
            duplicate.IsDuplicate && h.Rpc.Steers.Count == 1 && f.Script.Calls == modelCalls
            && final.Operations.Single(x => x.Id == operation.Id) == operation
            && final.Inbox.Count(x => x.SourceMessageId == WeChatText.Hash("amend")) == 1);
    }

    private static async Task ChangedTurnAsync(string directory, Action<string, bool> check)
    {
        await using var h = await Harness.CreateRunningAsync(directory);
        var f = h.Host; var prior = h.Current; string oldTurn = prior.Snapshot.CurrentTurnId!;
        string nextTurn = Guid.NewGuid().ToString("D");
        // An arbitrary new turn/started is deliberately ignored by the client.
        // Use its actual authenticated read path to establish the changed turn.
        h.Rpc.Inner.SetThread(prior.Handle.Identity.ThreadId, prior.Snapshot.Cwd,
            OwnedHostRuntimeTests.FakeRpc.Turn(nextTurn, "inProgress"));
        var read = await f.Runtime.OwnedRuntime!.LoadedSession("demo")!.Client.ReconcileAsync(prior.Handle);
        var latest = h.Current;
        check("changed_turn_is_established_by_real_read_not_an_ignored_old_event",
            read.Task.CurrentTurnId == nextTurn && latest.Snapshot.CurrentTurnId == nextTurn
            && latest.Snapshot.Revision > prior.Snapshot.Revision && latest.Snapshot.State == OwnedTaskState.Running);
        string body = "补充 demo/" + latest.Handle.Identity.ThreadId + " 先检查测试说明";
        f.Script.Handler = (request, _) => Task.FromResult(request.Observations.IsEmpty
            ? ToolModelResponse.Call(Steer(latest, oldTurn, body))
            : ToolModelResponse.Reply("运行轮次已经变化，这条补充没有发出。"));
        await f.InputAsync("stale-turn", body); await OwnedHostRuntimeTests.WaitReplyAsync(f, "stale-turn");
        var state = await f.ReadAsync();
        var rejected = state.ToolInvocations.Single(x => x.ToolName == "steer_turn");
        using var arguments = JsonDocument.Parse(rejected.ArgumentsJson);
        check("old_expected_turn_rejects_even_with_the_latest_snapshot_revision",
            arguments.RootElement.GetProperty("revision").GetInt64() == latest.Snapshot.Revision
            && arguments.RootElement.GetProperty("expectedTurnId").GetString() == oldTurn
            && rejected.Stage == ToolInvocationStage.NeedsConfirmation && rejected.OperationId == null
            && !state.Authorizations.Any(x => x.SourceMessageId == WeChatText.Hash("stale-turn")));
        check("stale_amendment_never_retargets_or_reaches_rpc",
            h.Rpc.Steers.IsEmpty && !state.Operations.Any(x => x.Action == "steer_turn")
            && h.Current.Snapshot.CurrentTurnId == nextTurn && f.Sent.Contains("运行轮次已经变化，这条补充没有发出。"));
    }

    private static async Task ChangedInputAsync(string directory, Action<string, bool> check)
    {
        await using var h = await Harness.CreateRunningAsync(directory);
        var f = h.Host; var target = h.Current; string turn = target.Snapshot.CurrentTurnId!;
        string oldBody = "补充 demo/" + target.Handle.Identity.ThreadId + " 按原计划修改代码";
        const string constraint = "不要修改代码，只解释结果";
        var entered = new TaskCompletionSource<ToolModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Script.Handler = (request, _) =>
        {
            if (request.CurrentMessage.SourceMessageId == WeChatText.Hash("old-proposal") && request.DeferredWork == null)
            { entered.TrySetResult(request); return release.Task; }
            return Task.FromResult(ToolModelResponse.Reply(request.DeferredWork == null
                ? "收到，按最新要求只解释结果。" : "先前的要求已保留为只读记录。"));
        };
        Guid generationId = Guid.Empty;
        try
        {
            await f.InputAsync("old-proposal", oldBody);
            var pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            generationId = pending.GenerationContext!.GenerationId;
            await f.InputAsync("new-constraint", constraint); await OwnedHostRuntimeTests.WaitReplyAsync(f, "new-constraint");
            var current = await f.ReadAsync();
            check("new_constraint_is_answered_while_old_model_ignores_cancellation",
                !release.Task.IsCompleted && f.Sent.Contains("收到，按最新要求只解释结果。")
                && current.Generations.Single(x => x.Id == generationId).Stage == GenerationStage.Superseded);
            check("superseded_proposal_retains_original_sources_without_authority_transfer",
                current.Inbox.Single(x => x.SourceMessageId == WeChatText.Hash("old-proposal")).Body == oldBody
                && current.Inbox.Single(x => x.SourceMessageId == WeChatText.Hash("new-constraint")).Body == constraint
                && !current.Authorizations.Any(x => x.SourceMessageId == WeChatText.Hash("old-proposal"))
                && h.Rpc.Steers.IsEmpty);
        }
        finally
        {
            release.TrySetResult(ToolModelResponse.Call(Steer(target, turn, oldBody)));
            await f.Runtime.StopSessionAsync();
        }
        var final = await f.Ledger.ReadConversationAsync(h.Identity);
        check("late_steer_proposal_cannot_execute_after_new_constraints_and_drain",
            generationId != Guid.Empty && h.Rpc.Steers.IsEmpty
            && !final.Operations.Any(x => x.Action == "steer_turn")
            && !final.ToolInvocations.Any(x => x.GenerationId == generationId && x.ToolName == "steer_turn"));
    }

    private static async Task UnknownRestartAsync(string directory, Action<string, bool> check)
    {
        ClawBinding binding; OperationRecord uncertain; ConversationIdentity identity;
        string body;
        await using (var h = await Harness.CreateRunningAsync(directory))
        {
            var f = h.Host; var target = h.Current;
            body = "补充 demo/" + target.Handle.Identity.ThreadId + " 核对当前测试";
            h.Rpc.DisconnectOnSteer = true;
            f.Script.Handler = (request, _) => Task.FromResult(request.Observations.IsEmpty
                ? ToolModelResponse.Call(Steer(target, target.Snapshot.CurrentTurnId!, body))
                : ToolModelResponse.Reply("补充请求的结果还不能确认，我不会自动重发。"));
            await f.InputAsync("unknown-amend", body); await OwnedHostRuntimeTests.WaitReplyAsync(f, "unknown-amend");
            var state = await f.ReadAsync(); uncertain = state.Operations.Single(x => x.Action == "steer_turn");
            check("disconnect_after_actual_steer_dispatch_is_unknown_with_exact_target",
                h.Rpc.DispatchProofs.SequenceEqual(new[] { true }) && h.Rpc.Steers.Count == 1
                && uncertain.Stage == OperationStage.Unknown && uncertain.Task?.TurnId == target.Snapshot.CurrentTurnId
                && state.ToolInvocations.Single(x => x.OperationId == uncertain.Id).Stage == ToolInvocationStage.Unknown);
            await f.InputAsync("unknown-amend", body);
            await f.InputAsync("status-after-unknown-amend", "状态");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, "status-after-unknown-amend");
            var after = await f.ReadAsync();
            check("unknown_steer_survives_fresh_query_and_channel_redelivery_without_retry",
                h.Rpc.Steers.Count == 1 && after.Operations.Single(x => x.Id == uncertain.Id) == uncertain
                && after.Inbox.Count(x => x.SourceMessageId == WeChatText.Hash("unknown-amend")) == 1);
            binding = f.Settings.WeChat.ReadBinding()!; identity = h.Identity;
        }
        var newWires = new ConcurrentQueue<RecordingRpc>();
        var profile = OwnedHostRuntimeTests.Profile(directory, verified: true);
        var options = Options(directory, profile, rpc => newWires.Enqueue(rpc));
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(directory, savedBinding: binding, ownedOptions: options);
        var restored = await reopened.Ledger.ReadConversationAsync(identity);
        var duplicate = await reopened.Ledger.ReceiveAsync(identity, WeChatText.Hash("unknown-amend"), body,
            MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await reopened.InputAsync("unknown-amend", body);
        await reopened.InputAsync("status-after-file-reopen", "状态");
        await OwnedHostRuntimeTests.WaitReplyAsync(reopened, "status-after-file-reopen");
        await reopened.Runtime.StopSessionAsync();
        var final = await reopened.Ledger.ReadConversationAsync(identity);
        check("actual_file_reopen_preserves_unknown_control_without_blind_replay",
            !restored.IsReadOnly && restored.RecoveryReason == null && restored.Operations.Single(x => x.Id == uncertain.Id) == uncertain
            && duplicate.IsDuplicate && final.Operations.Single(x => x.Id == uncertain.Id) == uncertain
            && final.Operations.Count(x => x.Action == "steer_turn") == 1
            && newWires.Sum(rpc => rpc.Steers.Count) == 0 && reopened.Script.Calls == 0);
    }

    private static ModelToolCall Steer(OwnedRuntimeTask task, string turn, string body) => OwnedHostRuntimeTests.Call("steer_turn", new
    { configurationId = task.ConfigurationId, threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision,
        expectedTurnId = turn, text = body });
    private static OwnedHostOptions Options(string directory, ConfiguredOwnedProfile profile, Action<RecordingRpc> created) =>
        new(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            var rpc = new RecordingRpc(new(configured.Configuration, () => Task.FromResult(true)));
            created(rpc); return Task.FromResult<IJsonRpcConnection>(rpc);
        });

    private sealed class Harness(DurableRuntimeTests.Fixture host, RecordingRpc rpc) : IAsyncDisposable
    {
        internal DurableRuntimeTests.Fixture Host { get; } = host;
        internal RecordingRpc Rpc { get; } = rpc;
        internal ConversationIdentity Identity { get; } = host.Runtime.Identity!;
        internal OwnedRuntimeTask Current => Host.Runtime.OwnedRuntime!.Tasks().Single();
        internal static async Task<Harness> CreateRunningAsync(string directory)
        {
            RecordingRpc? rpc = null;
            var profile = OwnedHostRuntimeTests.Profile(directory, verified: true);
            var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, ownedOptions: Options(directory, profile, value => rpc = value));
            try
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
                var target = f.Runtime.OwnedRuntime!.Tasks().Single();
                string text = "继续 " + target.Handle.Identity.ThreadId + " 检查测试";
                await OwnedHostRuntimeTests.SendToolAsync(f, "start", text, _ => OwnedHostRuntimeTests.ContinueCall(target, text));
                target = f.Runtime.OwnedRuntime.Tasks().Single();
                rpc!.Inner.Emit("turn/started", new { threadId = target.Handle.Identity.ThreadId,
                    turn = OwnedHostRuntimeTests.FakeRpc.Turn(target.Snapshot.CurrentTurnId!, "inProgress") });
                await WaitAsync(() => Task.FromResult(f.Runtime.OwnedRuntime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
                rpc.VerifyDispatch = async args =>
                {
                    var snapshot = await f.ReadAsync();
                    var id = Guid.Parse(args.GetProperty("clientUserMessageId").GetString()!);
                    return snapshot.Operations.Any(x => x.Id == id && x.Stage == OperationStage.Dispatching && x.Action == "steer_turn")
                        && snapshot.ToolInvocations.Any(x => x.OperationId == id && x.Stage == ToolInvocationStage.Executing);
                };
                return new(f, rpc);
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
    private sealed class RecordingRpc(OwnedHostRuntimeTests.FakeRpc inner) : IJsonRpcConnection, IAsyncDisposable
    {
        private bool disconnected;
        private event Action? localDisconnected;
        internal OwnedHostRuntimeTests.FakeRpc Inner => inner;
        internal ConcurrentQueue<JsonElement> Steers { get; } = new();
        internal ConcurrentQueue<bool> DispatchProofs { get; } = new();
        internal Func<JsonElement, Task<bool>>? VerifyDispatch;
        internal bool DisconnectOnSteer;
        public ConnectionIdentity Identity => inner.Identity;
        public bool IsConnected => !disconnected && inner.IsConnected;
        public event Action<RpcIncoming>? Incoming { add => inner.Incoming += value; remove => inner.Incoming -= value; }
        public event Action? Disconnected
        {
            add { inner.Disconnected += value; localDisconnected += value; }
            remove { inner.Disconnected -= value; localDisconnected -= value; }
        }
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            if (method == "turn/steer")
            {
                var args = JsonSerializer.SerializeToElement(parameters); Steers.Enqueue(args);
                DispatchProofs.Enqueue(VerifyDispatch != null && await VerifyDispatch(args));
                if (DisconnectOnSteer)
                {
                    disconnected = true; localDisconnected?.Invoke();
                    throw new RpcTransportException(true, "synthetic_steer_disconnected_after_write");
                }
            }
            return await inner.RequestAsync(method, parameters, ct);
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct) => inner.NotifyAsync(method, parameters, ct);
        public Task RespondAsync(JsonElement id, object result, CancellationToken ct) => inner.RespondAsync(id, result, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic owned turn condition was not reached.");
            await Task.Delay(20);
        }
    }
}
