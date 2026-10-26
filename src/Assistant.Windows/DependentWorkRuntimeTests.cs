using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual input/coordinator/ledger/worker, with synthetic channel, model and owned RPC only.
internal static class DependentWorkRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependent-work-runtime-test", var directory]) return false;
        try { await RunAsync(directory); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("A new isolated output directory is required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "dependent-work-runtime-checks.json"), JsonSerializer.Serialize(new
            { checks, networkUsed = false, actualModelUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var fixturePath = Path.Combine(directory, "fixture");
        var profile = OwnedHostRuntimeTests.Profile(fixturePath, true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], maxConcurrentExecutions: 1);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        DurableRuntimeTests.Fixture? fixture = null;
        int connections = 0;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (p, _) =>
        {
            connections++;
            rpc = new(p.Configuration, async () => (await fixture!.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        }, workspace);
        await using var f = fixture = await DurableRuntimeTests.Fixture.CreateAsync(fixturePath, ownedOptions: options);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        Check("constructing_dependency_worker_does_not_connect", connections == 0);
        await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!; var identity = f.Runtime.Identity!;
        var task = runtime.Tasks().Single(); string thread = task.Handle.Identity.ThreadId;
        string startText = "继续 " + thread + " 做前置工作";
        await OwnedHostRuntimeTests.SendToolAsync(f, "start", startText, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), startText));
        var turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
        Check("fixture_starts_exact_owned_task", !string.IsNullOrWhiteSpace(turn));
        void Observe(string turnId, string status)
        {
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turnId, status));
            rpc.Emit(status == "inProgress" ? "turn/started" : "turn/completed", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turnId, status) });
        }
        Observe(turn, "inProgress");
        await Wait(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
        f.Settings.ChatGpt.Enabled = false;
        string Schedule(string text) => $"等 {thread} 本轮完成后，向 {thread} 发送：{text}";
        async Task Input(string key, string text) { await f.InputAsync(key, text); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkItem> Latest() => (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.OrderByDescending(w => w.CreatedAt).First();
        await Input("schedule", Schedule("检查并完善说明文档"));
        var work = await Latest();
        Check("model_off_input_creates_exact_authorized_dependency", work.Stage == DependentWorkStage.Waiting && work.Target.TurnId == turn
            && work.Instructions == "检查并完善说明文档" && work.Requirements.Single().Task == work.Target);
        Check("registration_has_no_task_send_and_explains_deadline", rpc!.GuardedTexts.IsEmpty
            && f.Sent.Any(s => s.Contains(work.Id.ToString("D")) && s.Contains("截止")));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("running_predecessor_does_not_release_work", rpc.GuardedTexts.IsEmpty && (await f.Ledger.GetDependentWorkAsync(identity)).Attempts.IsEmpty);
        await Input("pause", "暂停依赖 " + work.Id.ToString("D"));
        Check("explicit_pause_is_saved_with_model_off", (await Latest()).Stage == DependentWorkStage.Paused);
        await Input("edit", "修改依赖 " + work.Id.ToString("D") + "：核对最终说明文档");
        Check("paused_edit_keeps_exact_new_instruction_and_version", (await Latest()).Instructions == "核对最终说明文档"
            && (await Latest()).Version == 3 && (await Latest()).Stage == DependentWorkStage.Paused);
        await Input("resume", "恢复依赖 " + work.Id.ToString("D"));
        Check("explicit_resume_returns_to_waiting", (await Latest()).Stage == DependentWorkStage.Waiting);
        await Input("not-tests", $"等 {thread} 测试通过后，向 {thread} 发送：不应执行");
        Check("test_success_claim_is_not_rewritten_as_turn_completion", (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Length == 1
            && f.Sent.Any(s => s.Contains("尚不能") && s.Contains("测试通过")));
        await Input("list", "依赖列表");
        Check("list_reads_real_work_without_model", f.Sent.Any(s => s.Contains(work.Id.ToString("D")) && s.Contains("等待前置条件")));
        await Input("remembered", "你还记着哪些事");
        var remembered = await f.ReadAsync();
        var rememberedInput = remembered.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("remembered"));
        Check("all_work_query_includes_actual_dependency_id", remembered.Outbox.Any(o => o.GenerationId == rememberedInput.GenerationId
            && o.Body.Contains(work.Id.ToString("D"))));
        f.Settings.ChatGpt.Enabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(f, "model-list", "查询已登记的依赖工作", _ => OwnedHostRuntimeTests.Call("list_dependencies", new { page = 1 }));
        Check("model_read_tool_routes_to_real_dependency_records", (await f.ReadAsync()).ToolInvocations.Any(i => i.ToolName == "list_dependencies"
            && i.Stage == ToolInvocationStage.Completed && i.ResultJson!.Contains(work.Id.ToString("D"))));
        f.Settings.ChatGpt.Enabled = false;
        f.Settings.ChatGpt.TaskControlEnabled = false; Observe(turn, "completed");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("disabled_control_does_not_dispatch_ready_work", rpc.GuardedTexts.IsEmpty);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await Wait(async () => (await Latest()).Stage == DependentWorkStage.Running);
        work = await Latest();
        Check("background_worker_sends_literal_instruction_once", rpc.GuardedTexts.SequenceEqual(new[] { "核对最终说明文档" })
            && work.ResultTask?.TurnId != turn && work.ResultTask?.ThreadId == thread && work.Completion == null);
        Check("accepted_work_holds_shared_directory_slot", workspace.Blockers().Count == 1);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("acceptance_is_not_completion_and_is_not_replayed", (await Latest()).Stage == DependentWorkStage.Running && rpc.GuardedTexts.Count == 1);
        turn = work.ResultTask!.TurnId!; Observe(turn, "completed");
        await Wait(async () => (await Latest()).Stage == DependentWorkStage.Completed && workspace.Blockers().Count == 0);
        Check("separate_terminal_evidence_completes_work_and_releases_slot", (await Latest()).Completion?.Task.TurnId == turn);

        // A queued writer is paused at its real guard boundary while a new user command cancels it.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeGuard = async () => { entered.TrySetResult(); await release.Task; };
        await Input("cancel-schedule", Schedule("不得在取消后发送"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        work = await Latest();
        await Input("cancel", "取消依赖 " + work.Id.ToString("D"));
        release.SetResult();
        await Wait(async () => (await f.Ledger.GetDependentWorkAsync(identity)).Attempts.Last().Stage == DependentDispatchStage.NotDispatched);
        var cancelledWork = await Latest();
        File.WriteAllText(Path.Combine(directory, "cancel-evidence.json"), JsonSerializer.Serialize(new
        { sends = rpc.GuardedTexts.Count, stage = cancelledWork.Stage.ToString(), blockers = workspace.Blockers() }, Settings.Json));
        Check("cancel_during_writer_wait_sends_zero_bytes", rpc.GuardedTexts.Count == 1);
        Check("known_unsent_receipt_preserves_cancelled_work", cancelledWork.Stage == DependentWorkStage.Cancelled);
        Check("known_unsent_receipt_releases_directory_slot", workspace.Blockers().Count == 0);
        rpc.BeforeGuard = null;

        // A transport that wrote the request but lost the reply must retain its execution claim.
        rpc.GuardedReplyLost = true;
        await Input("unknown-schedule", Schedule("未知结果不得重发"));
        await Wait(async () => (await Latest()).Stage == DependentWorkStage.Unknown);
        work = await Latest();
        Check("lost_ack_keeps_unknown_work_and_directory_claim", rpc.GuardedTexts.Count == 2 && workspace.Blockers().Single().State == "Unknown");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unknown_work_is_never_automatically_replayed", rpc.GuardedTexts.Count == 2);
        await Input("unknown-list", "依赖列表");
        Check("unknown_is_reported_factually", f.Sent.Any(s => s.Contains(work.Id.ToString("D")) && s.Contains("结果未知，不重发")));
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        Check("desktop_unknown_is_not_reported_as_success_or_unsent", desktop.Dependencies!.Single(d => d.Row.Id == work.Id.ToString("D")).Body.Contains("发送结果未知，不会重发"));
        Check("desktop_cancelled_dispatch_keeps_known_unsent_attempt", desktop.Dependencies!.Single(d => d.Row.Id == cancelledWork.Id.ToString("D")).Body.Contains("明确未发送"));
        Check("desktop_pending_list_includes_unknown_dependency", desktop.Work.Any(r => r.Id == work.Id.ToString("D") && r.State.Contains("结果未知")));
        Check("other_binding_cannot_read_dependency_records", (await f.Ledger.GetDependentWorkAsync(identity with { BindingId = "unrelated" })).WorkItems.IsEmpty);
        await f.Runtime.StopSessionAsync();
        var recovered = new WorkspaceWriteCoordinator(recoverySources: ["owned"]);
        await using var restored = new OwnedControlRuntime(identity, () => identity, f.Ledger, options.DataDirectory, options.Profiles,
            (_, _) => throw new InvalidOperationException("Recovery must not connect."), workspace: recovered);
        await restored.PrepareWorkspaceAsync();
        Check("fresh_host_restores_unknown_dependency_directory_without_connection", recovered.Ready
            && recovered.Blockers().Any(b => b.ClaimId == "owned/" + work.AttemptId!.Value.ToString("N") && b.State == "Unknown") && connections == 1);
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Dependency host condition not reached."); await Task.Delay(20); }
    }
}
