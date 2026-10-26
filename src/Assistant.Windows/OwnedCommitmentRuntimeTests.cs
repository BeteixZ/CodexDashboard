using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual owned runtime, approval worker, commitment worker and durable channel.
// The test reuses the synthetic-only connection from OwnedHostRuntimeTests.
internal static class OwnedCommitmentRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--owned-commitment-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit new synthetic directory required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("New absolute WPF test directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected directory rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "owned-commitment-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual host with synthetic model, HTTP, protected files and fake owned JSON-RPC"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), verified: true);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        DurableRuntimeTests.Fixture? fixture = null;
        int connections = 0;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            connections++;
            rpc = new(configured.Configuration, async () => (await fixture!.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        });
        await using var f = fixture = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), ownedOptions: options);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        Check("construction_with_synthetic_verification_still_does_not_connect", rpc == null && connections == 0);
        await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo",
            _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        string instruction = "继续 " + thread + " 检查合成审批订阅";
        await OwnedHostRuntimeTests.SendToolAsync(f, "start", instruction,
            _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), instruction));
        var task = runtime.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        rpc!.Emit("turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, "inProgress") });
        await WaitAsync(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
        object Request(string item) => new
        {
            threadId = thread, turnId = turn, itemId = item, command = "echo SYNTHETIC_SUBSCRIPTION_APPROVAL",
            cwd = task.Snapshot.Cwd, startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var firstRequest = Request("first-command");
        rpc.Emit("item/commandExecution/requestApproval", firstRequest, 701);
        var first = runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
        await WaitAsync(async () =>
            (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.RequestId
                == OwnedControlToolAdapter.ApprovalReference("demo", first));
        var state = await f.ReadAsync();
        string firstKey = OwnedControlRuntime.ApprovalNotificationKey("demo", first.Token);
        var firstDetail = state.Events.Single(e => e.ExternalEventKey == firstKey);
        Check("detailed_pending_approval_exists_before_subscription",
            state.WorkItems.IsEmpty && Delivered(state, firstDetail.Id)
            && firstDetail.Body.Contains(OwnedControlToolAdapter.ApprovalReference("demo", first), StringComparison.Ordinal));

        f.Settings.ChatGpt.Enabled = false;
        int modelCalls = f.Script.Calls, rpcMethods = rpc.Methods.Count;
        await f.InputAsync("subscribe-after-approval", thread + "需要决定时叫我");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "subscribe-after-approval");
        await WaitAsync(async () =>
        {
            var snapshot = await f.ReadAsync();
            return snapshot.CommitmentRuns.Any(r => ExactDecision(snapshot, r, first)
                && r.Stage == CommitmentRunStage.Completed);
        });
        state = await f.ReadAsync();
        var work = state.WorkItems.Single();
        var firstRun = state.CommitmentRuns.Single();
        Check("subscription_created_after_pending_approval_observes_exact_existing_token",
            work.Kind == TrackedWorkKind.Subscription && work.Stage == TrackedWorkStage.Active
            && ExactDecision(state, firstRun, first)
            && state.Subscriptions.Single().LastDecisionId == first.Token
            && work.Task == new TaskReference(task.Handle.Identity.HostId, thread, turn, task.Handle.Identity.ProfileId)
            && f.Script.Calls == modelCalls && rpc.Methods.Count == rpcMethods && rpc.Responses == 0);
        Check("subscription_uses_required_detailed_approval_without_a_second_notice",
            firstRun.SharedDecisionEventId == firstDetail.Id && firstRun.NotificationEventId == null
            && firstRun.NotificationDraftId == null && Delivered(state, firstDetail.Id)
            && state.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1 && !firstDetail.IsDefaultTaskNotification
            && runtime.Presented(firstDetail.Id)?.Request.Token == first.Token);

        rpc.Emit("item/commandExecution/requestApproval", firstRequest, 701);
        await Task.Delay(2200); // Exercise repeated exact-token worker and cached state polling.
        state = await f.ReadAsync();
        Check("repeated_request_and_cached_waiting_keep_one_subscription_notice",
            state.CommitmentRuns.Length == 1 && state.Subscriptions.Single().LastDecisionId == first.Token
            && state.Events.Count(e => e.ExternalEventKey == firstKey) == 1
            && runtime.LoadedSession("demo")!.Client.PendingApprovals().Count == 1);
        // The fake server resolves independently. No user/model approval is sent.
        // The client deliberately retains its cached WaitingApproval state until
        // later lifecycle evidence, reproducing tokenless cached observations.
        rpc.Emit("serverRequest/resolved", new { threadId = thread, requestId = 701 });
        await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval == null);
        await Task.Delay(2200);
        state = await f.ReadAsync();
        Check("resolved_token_followed_by_cached_waiting_does_not_create_generic_second_notice",
            runtime.Tasks().Single().Snapshot.State == OwnedTaskState.WaitingApproval
            && runtime.LoadedSession("demo")!.Client.PendingApprovals().Count == 0
            && state.CommitmentRuns.Length == 1 && state.Subscriptions.Single().LastDecisionId == first.Token);

        rpc.Emit("item/commandExecution/requestApproval", Request("second-command"), 702);
        var second = runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
        string secondKey = OwnedControlRuntime.ApprovalNotificationKey("demo", second.Token);
        await WaitAsync(async () =>
        {
            var snapshot = await f.ReadAsync();
            return snapshot.CommitmentRuns.Any(r => ExactDecision(snapshot, r, second)
                && r.Stage == CommitmentRunStage.Completed)
                && snapshot.Events.Any(e => e.ExternalEventKey == secondKey && Delivered(snapshot, e.Id))
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.RequestId
                    == OwnedControlToolAdapter.ApprovalReference("demo", second);
        });
        state = await f.ReadAsync();
        var secondDetail = state.Events.Single(e => e.ExternalEventKey == secondKey);
        Check("new_request_token_on_same_turn_creates_one_new_subscription_notice",
            second.Token != first.Token && state.WorkItems.Length == 1 && state.CommitmentRuns.Length == 2
            && state.CommitmentRuns.Count(r => ExactDecision(state, r, first)) == 1
            && state.CommitmentRuns.Count(r => ExactDecision(state, r, second)) == 1
            && state.WorkItems.Single().Stage == TrackedWorkStage.Active
            && state.Subscriptions.Single().LastDecisionId == second.Token);
        Check("new_detailed_approval_has_exact_reference_target_and_presented_binding",
            secondDetail.Body.Contains(OwnedControlToolAdapter.ApprovalReference("demo", second), StringComparison.Ordinal)
            && secondDetail.Body.Contains(thread, StringComparison.Ordinal) && secondDetail.Body.Contains(turn, StringComparison.Ordinal)
            && secondDetail.Body.Contains(task.Snapshot.Cwd, StringComparison.Ordinal)
            && secondDetail.Body.Contains("本次会话批准", StringComparison.Ordinal)
            && runtime.Presented(secondDetail.Id)?.Request.Token == second.Token
            && !secondDetail.IsDefaultTaskNotification);
        await Task.Delay(2200);
        state = await f.ReadAsync();
        Check("stable_second_request_stays_deduplicated_without_approval_or_model_execution",
            state.CommitmentRuns.Length == 2 && state.Operations.Count(o => o.Action == "subscribe") == 1
            && !state.Operations.Any(o => o.Action == "respond_approval") && rpc.Responses == 0
            && rpc.Methods.Count == rpcMethods && connections == 1 && f.Script.Calls == modelCalls
            && f.Runtime.LastError == null && runtime.Failure == null);
    }

    private static bool ExactDecision(ConversationSnapshot state, CommitmentRun run, PendingApproval approval)
        => state.Events.Any(e => e.Id == run.SharedDecisionEventId
            && e.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token)
            && e.RequiredDecision is { } decision && decision.DecisionToken == approval.Token
            && decision.ProviderScope == "demo" && decision.ConnectionEpoch == approval.ConnectionEpoch
            && decision.ExpiresAt == approval.ExpiresAt
            && e.Task == new TaskReference(approval.Task.HostId, approval.Task.ThreadId, approval.TurnId, approval.Task.ProfileId));

    private static bool Delivered(ConversationSnapshot state, Guid eventId)
    {
        var messages = state.Outbox.Where(o => o.NotificationEventId == eventId).ToArray();
        return messages.Length > 0 && messages.All(o => o.Stage == OutboxStage.Accepted);
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic owned commitment condition not reached.");
            await Task.Delay(20);
        }
    }
}
