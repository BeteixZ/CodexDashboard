using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class DependencyContinuationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-continuation-test", var directory]) return false;
        try { await RunAsync(directory); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("New isolated output required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "dependency-continuation-checks.json"), JsonSerializer.Serialize(new
            { checks, networkUsed = false, actualModelUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], maxConcurrentExecutions: 1);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (p, _) =>
        { rpc = new(p.Configuration, () => Task.FromResult(true)); return Task.FromResult<IJsonRpcConnection>(rpc); }, workspace);
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), ownedOptions: options);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!; var identity = f.Runtime.Identity!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        string start = "继续 " + thread + " 做准备";
        await OwnedHostRuntimeTests.SendToolAsync(f, "start", start, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), start));
        string baseline = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
        async Task Observe(bool complete)
        {
            string turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, complete ? "completed" : "inProgress"));
            rpc.Emit(complete ? "turn/completed" : "turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, complete ? "completed" : "inProgress") });
            await Wait(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == (complete ? OwnedTaskState.Completed : OwnedTaskState.Running)));
        }
        await Observe(false); f.Settings.ChatGpt.Enabled = false;
        async Task Input(string key, string body) { await f.InputAsync(key, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkSnapshot> Saved() => await f.Ledger.GetDependentWorkAsync(identity);
        async Task<DependentWorkItem> Work(Guid id) => (await Saved()).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string command)
        {
            var before = (await Saved()).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, command); return (await Saved()).WorkItems.Single(w => !before.Contains(w.Id));
        }
        string Normal(string instruction) => $"等 {thread} 本轮完成后，向 {thread} 发送：{instruction}";
        string Same(Guid parent, string instruction) => $"等依赖 {parent:D} 完成后，在同一任务继续：{instruction}";
        var first = await Add("first", Normal("第一步"));
        var second = await Add("second", Same(first.Id, "第二步"));
        var third = await Add("third", Same(second.Id, "第三步"));
        var fixedTarget = await Add("fixed", $"等依赖 {first.Id:D} 完成后，向 {thread} 发送：固定目标不得跟随");
        Check("model_off_registration_explicitly_pins_continuation_and_original_target", second.ContinueAfterWorkId == first.Id
            && third.ContinueAfterWorkId == second.Id && second.Target.TurnId == baseline && third.Target.TurnId == baseline
            && fixedTarget.ContinueAfterWorkId == null && second.Requirements.Single().WorkVersion == first.Version && rpc!.GuardedTexts.IsEmpty);
        Check("receipt_explains_actual_predecessor_turn_not_arbitrary_future_turn", f.Sent.Any(s => s.Contains("接续起点是这项前置工作实际完成的轮次") && s.Contains("不会跟随任意新轮次")));
        await Input("remove-anchor", $"修改依赖 {second.Id:D} 前置：任务 {thread}");
        Check("ordinary_condition_edit_cannot_remove_explicit_continuation_source", (await Work(second.Id)).Version == second.Version
            && (await Work(second.Id)).ContinueAfterWorkId == first.Id && f.Sent.Any(s => s.Contains("必须保留原来指定的前置工作")));
        await Observe(true); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(first.Id)).Stage == DependentWorkStage.Running);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("accepted_first_step_does_not_start_same_task_second_step", (await Work(second.Id)).Stage == DependentWorkStage.Waiting
            && rpc!.GuardedTexts.SequenceEqual(new[] { "第一步" }));
        await Observe(true); await Wait(async () => (await Work(first.Id)).Stage == DependentWorkStage.Completed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(second.Id)).Stage == DependentWorkStage.Running);
        first = await Work(first.Id); second = await Work(second.Id);
        var saved = await Saved();
        Check("second_dispatch_freezes_first_actual_result_as_starting_turn", saved.Attempts.Single(a => a.WorkItemId == second.Id).Evidence[0].Observation.Task == first.ResultTask
            && second.Target.TurnId == baseline && second.ResultTask!.TurnId != first.ResultTask!.TurnId && second.ResultTask.ThreadId == thread);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("ordinary_fixed_target_does_not_gain_continuation_permission", (await Work(fixedTarget.Id)).Stage == DependentWorkStage.NeedsDecision
            && !rpc!.GuardedTexts.Contains("固定目标不得跟随"));
        await Observe(true); await Wait(async () => (await Work(second.Id)).Stage == DependentWorkStage.Completed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(third.Id)).Stage == DependentWorkStage.Running);
        third = await Work(third.Id); saved = await Saved();
        Check("third_step_uses_second_result_instead_of_registration_baseline", saved.Attempts.Single(a => a.WorkItemId == third.Id).Evidence[0].Observation.Task == second.ResultTask
            && rpc!.GuardedTexts.SequenceEqual(new[] { "第一步", "第二步", "第三步" }));
        await Observe(true); await Wait(async () => (await Work(third.Id)).Stage == DependentWorkStage.Completed);
        await Wait(() => Task.FromResult(workspace.Blockers().Count == 0));
        Check("three_distinct_result_turns_finish_without_extra_send", new[] { first.ResultTask!.TurnId, second.ResultTask!.TurnId, third.ResultTask!.TurnId }.Distinct().Count() == 3
            && (await Saved()).Attempts.Length == 3 && workspace.Blockers().Count == 0);
        var stale = await Add("stale", Same(first.Id, "不得追随别的轮次"));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("later_unrelated_current_turn_requires_new_decision", (await Work(stale.Id)).Stage == DependentWorkStage.NeedsDecision && rpc!.GuardedTexts.Count == 3);
        await Input("list", "依赖列表");
        Check("wechat_list_identifies_explicit_continuation_source", f.Sent.Any(s => s.Contains($"接续依赖 {first.Id:D} 实际完成的轮次")));
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        var detail = desktop.Dependencies!.Single(d => d.Row.Id == second.Id.ToString("D"));
        Check("desktop_distinguishes_registration_turn_dispatch_start_and_result", detail.Body.Contains("登记时的任务：") && detail.Body.Contains(baseline)
            && detail.Body.Contains("本次接续起点：") && detail.Body.Contains(first.ResultTask!.TurnId!) && detail.Body.Contains(second.ResultTask!.TurnId!)
            && detail.Commands.Contains("在同一任务继续"));
        await CheckWindow(desktop, second.Id, directory, Check);

        string ordinary = $"等依赖 {third.Id:D} 完成后，向 {thread} 发送：保持固定目标";
        var source = await f.Ledger.ReceiveAsync(identity, "ordinary-auth", ordinary, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var adapter = new DependentWorkToolAdapter(f.Ledger, () => runtime, value => value == identity, () => true, () => DateTimeOffset.UtcNow);
        var exact = (await adapter.ResolveAsync(source.Message, CancellationToken.None)).Call!;
        var altered = JsonNode.Parse(exact.ArgumentsJson)!; altered["continueAfterWorkId"] = third.Id.ToString("D");
        var descriptor = DependentWorkToolAdapter.Descriptors().Single(d => d.Name == exact.Name);
        Check("model_cannot_add_continuation_mode_to_fixed_target_request", (await adapter.AuthorizeAsync(new(source.Message, exact, descriptor), CancellationToken.None)).Allowed
            && !(await adapter.AuthorizeAsync(new(source.Message, exact with { ArgumentsJson = altered.ToJsonString() }, descriptor), CancellationToken.None)).Allowed);

        var fourth = await Add("fourth", Normal("第四步"));
        await Wait(async () => (await Work(fourth.Id)).Stage == DependentWorkStage.Running);
        var cancelled = await Add("cancelled", Same(fourth.Id, "取消后不得接续"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc!.BeforeGuard = async () => { entered.TrySetResult(); await release.Task; };
        await Observe(true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        await Input("cancel", "取消依赖 " + cancelled.Id.ToString("D")); release.SetResult();
        await Wait(async () => (await Saved()).Attempts.Single(a => a.WorkItemId == cancelled.Id).Stage == DependentDispatchStage.NotDispatched);
        await Wait(() => Task.FromResult(workspace.Blockers().Count == 0));
        Check("continuation_cancel_at_write_boundary_sends_nothing_and_releases_claim", (await Work(cancelled.Id)).Stage == DependentWorkStage.Cancelled
            && rpc.GuardedTexts.Count == 4 && workspace.Blockers().Count == 0);
        rpc.BeforeGuard = null; rpc.GuardedReplyLost = true;
        var unknown = await Add("unknown", Same(fourth.Id, "未知接续不重发"));
        await Wait(async () => (await Work(unknown.Id)).Stage == DependentWorkStage.Unknown);
        unknown = await Work(unknown.Id); fourth = await Work(fourth.Id);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unknown_continuation_retains_resolved_target_and_does_not_replay", rpc.GuardedTexts.Count == 5
            && (await Saved()).Attempts.Single(a => a.WorkItemId == unknown.Id).Evidence[0].Observation.Task == fourth.ResultTask
            && unknown.Target.TurnId != fourth.ResultTask!.TurnId && workspace.Blockers().Single().State == "Unknown");
        await f.Runtime.StopSessionAsync();
        var recovered = new WorkspaceWriteCoordinator(recoverySources: ["owned"]);
        await using var restored = new OwnedControlRuntime(identity, () => identity, f.Ledger, options.DataDirectory, options.Profiles,
            (_, _) => throw new InvalidOperationException("Recovery must not connect."), workspace: recovered);
        await restored.PrepareWorkspaceAsync();
        Check("fresh_host_recovers_unknown_continuation_claim_without_connecting", recovered.Ready
            && recovered.Blockers().Any(b => b.ClaimId == "owned/" + unknown.AttemptId!.Value.ToString("N") && b.State == "Unknown"));
    }
    private static async Task CheckWindow(AssistantWorkspaceSnapshot snapshot, Guid selected, string directory, Action<string, bool> check)
    {
        var window = new AssistantWorkWindow(_ => Task.FromResult(snapshot), value => value == snapshot.Identity)
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.DependenciesTab.IsSelected = true;
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout();
            window.DependenciesGrid.SelectedItem = window.DependenciesGrid.Items.OfType<AssistantWorkspaceRow>().Single(row => row.Id == selected.ToString("D"));
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout();
            check("desktop_selects_complete_continuation_detail_from_one_snapshot", window.DependencyDetailBox.Text == snapshot.Dependencies!.Single(d => d.Row.Id == selected.ToString("D")).Body);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "continuation-detail.png")); png.Save(stream);
        }
        finally { window.Close(); }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Continuation host condition not reached."); await Task.Delay(20); }
    }
}
