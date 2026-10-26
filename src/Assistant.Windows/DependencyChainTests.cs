using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class DependencyChainTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-chain-test", var directory]) return false;
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
            File.WriteAllText(Path.Combine(directory, "dependency-chain-checks.json"), JsonSerializer.Serialize(new
            { checks, networkUsed = false, actualModelUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], maxConcurrentExecutions: 1);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (p, _) =>
        {
            rpc = new(p.Configuration, () => Task.FromResult(true));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        }, workspace);
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), ownedOptions: options);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        async Task<string> NewTask(string key)
        {
            var before = f.Runtime.OwnedRuntime!.Tasks().Select(t => t.Handle.Identity.ThreadId).ToHashSet();
            await OwnedHostRuntimeTests.SendToolAsync(f, key + "-create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
            var task = f.Runtime.OwnedRuntime.Tasks().Single(t => !before.Contains(t.Handle.Identity.ThreadId));
            string text = "继续 " + task.Handle.Identity.ThreadId + " 做准备";
            await OwnedHostRuntimeTests.SendToolAsync(f, key + "-start", text, _ => OwnedHostRuntimeTests.ContinueCall(task, text));
            string thread = task.Handle.Identity.ThreadId;
            await Observe(thread, "completed");
            return thread;
        }
        async Task Observe(string thread, string state)
        {
            var task = f.Runtime.OwnedRuntime!.Tasks().Single(t => t.Handle.Identity.ThreadId == thread);
            string turn = task.Snapshot.CurrentTurnId!;
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, state));
            rpc.Emit(state == "inProgress" ? "turn/started" : "turn/completed", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, state) });
            await Wait(() => Task.FromResult(f.Runtime.OwnedRuntime.Tasks().Single(t => t.Handle.Identity.ThreadId == thread).Snapshot.State
                == (state == "completed" ? OwnedTaskState.Completed : state == "failed" ? OwnedTaskState.Failed : OwnedTaskState.Running)));
            if (state != "inProgress") await Wait(() => Task.FromResult(workspace.Blockers().Count == 0));
        }
        string a = await NewTask("a"), b = await NewTask("b");
        var identity = f.Runtime.Identity!;
        f.Settings.ChatGpt.Enabled = false;
        async Task BeginPrerequisite(string key)
        {
            f.Settings.ChatGpt.Enabled = true;
            var task = f.Runtime.OwnedRuntime!.Tasks().Single(t => t.Handle.Identity.ThreadId == a);
            string text = "继续 " + a + " 准备下一阶段";
            await OwnedHostRuntimeTests.SendToolAsync(f, key, text, _ => OwnedHostRuntimeTests.ContinueCall(task, text));
            await Observe(a, "inProgress");
            f.Settings.ChatGpt.Enabled = false;
        }
        async Task Input(string key, string text) { await f.InputAsync(key, text); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkSnapshot> Saved() => await f.Ledger.GetDependentWorkAsync(identity);
        async Task<DependentWorkItem> Work(Guid id) => (await Saved()).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string command)
        {
            var before = (await Saved()).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, command);
            return (await Saved()).WorkItems.Single(w => !before.Contains(w.Id));
        }
        string Parent(string text) => $"等 {a} 本轮完成后，向 {a} 发送：{text}";
        string Child(Guid id, string text) => $"等依赖 {id:D} 完成后，向 {b} 发送：{text}";
        // Keep an actual new prerequisite turn running while arranging the pair.
        await BeginPrerequisite("prepare-chain");
        var parent = await Add("parent", Parent("完成第一段实现"));
        var child = await Add("child", Child(parent.Id, "完成第二段核对"));
        Check("chain_pins_same_binding_parent_id_and_version", child.Requirements.Single() == new DependencyRequirement(WorkItemId: parent.Id, WorkVersion: parent.Version)
            && child.Target.ThreadId == b && child.Instructions == "完成第二段核对");
        await Input("chain-list", "依赖列表");
        Check("wechat_list_displays_pinned_parent_version", f.Sent.Any(s => s.Contains($"依赖 {parent.Id:D} 第 1 版完成")));
        await Input("invalid-parent", Child(Guid.NewGuid(), "不得发送"));
        Check("missing_parent_id_does_not_create_work", (await Saved()).WorkItems.Length == 2);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("running_predecessor_blocks_both_chain_steps", rpc!.GuardedTexts.IsEmpty);
        await Observe(a, "completed");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Running);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("accepted_parent_does_not_release_child", rpc.GuardedTexts.SequenceEqual(new[] { "完成第一段实现" })
            && (await Work(child.Id)).Stage == DependentWorkStage.Waiting);
        await Observe(a, "completed");
        await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Completed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Running);
        child = await Work(child.Id);
        Check("verified_parent_completion_dispatches_different_task_once", rpc.GuardedTexts.SequenceEqual(new[] { "完成第一段实现", "完成第二段核对" })
            && child.ResultTask!.ThreadId == b && child.ResultTask.TurnId != child.Target.TurnId && child.Completion == null);
        await Observe(b, "completed");
        await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Completed);
        Check("child_requires_its_own_terminal_evidence", (await Work(child.Id)).Completion!.Task == child.ResultTask && workspace.Blockers().Count == 0);

        // Hold A's current turn while registering; no timer delays or service calls are needed.
        await BeginPrerequisite("prepare-failures");
        var changed = await Add("changed", Parent("旧版前置"));
        var changedChild = await Add("changed-child", Child(changed.Id, "不得沿用旧版本"));
        await Input("change-parent", $"修改依赖 {changed.Id:D}：新版前置");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("parent_version_change_requires_new_decision", (await Work(changedChild.Id)).Stage == DependentWorkStage.NeedsDecision
            && (await Work(changedChild.Id)).Requirements.Single().WorkVersion == 1 && rpc.GuardedTexts.Count == 2);
        await Input("cancel-changed", "取消依赖 " + changed.Id.ToString("D"));
        var cancelled = await Add("cancelled", Parent("将取消的前置"));
        var cancelledChild = await Add("cancelled-child", Child(cancelled.Id, "取消后不得执行"));
        await Input("cancel-parent", "取消依赖 " + cancelled.Id.ToString("D"));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("cancelled_parent_does_not_release_child", (await Work(cancelledChild.Id)).Stage == DependentWorkStage.NeedsDecision && rpc.GuardedTexts.Count == 2);
        var failed = await Add("failed", Parent("将失败的前置"));
        var failedChild = await Add("failed-child", Child(failed.Id, "失败后不得执行"));
        await Observe(a, "completed");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(failed.Id)).Stage == DependentWorkStage.Running);
        await Observe(a, "failed");
        await Wait(async () => (await Work(failed.Id)).Stage == DependentWorkStage.Failed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("failed_actual_parent_turn_does_not_release_child", (await Work(failedChild.Id)).Stage == DependentWorkStage.NeedsDecision
            && rpc.GuardedTexts.Count == 3 && !rpc.GuardedTexts.Contains("失败后不得执行"));

        var snapshot = await f.Runtime.ReadWorkspaceAsync();
        var detail = snapshot.Dependencies!.Single(d => d.Row.Id == child.Id.ToString("D"));
        Check("desktop_reads_exact_source_requirements_and_actual_result", detail.Body.Contains(Child(parent.Id, "完成第二段核对"))
            && detail.Body.Contains(parent.Id.ToString("D")) && detail.Body.Contains(child.ResultTask!.TurnId!)
            && detail.Body.Contains("实际轮次终态：已完成") && detail.Body.Contains("轮次完成不表示测试通过"));
        Check("desktop_shows_pinned_and_changed_parent_versions", snapshot.Dependencies!.Single(d => d.Row.Id == changedChild.Id.ToString("D")).Body.Contains("版本已变化"));
        var atomic = await f.ReadAsync();
        Check("conversation_snapshot_includes_same_identity_work_and_attempts", atomic.DependentWork!.WorkItems.Length == 8
            && atomic.DependentWork.Attempts.Length == 3 && atomic.DependentWork.WorkItems.All(w => w.Identity == atomic.Identity));
        var foreign = await f.Ledger.ReadConversationAsync(identity with { BindingId = "other" });
        Check("other_binding_snapshot_contains_no_dependency_records", foreign.DependentWork!.WorkItems.IsEmpty && foreign.DependentWork.Attempts.IsEmpty);
        await VerifyWindow(snapshot, child.Id.ToString("D"), directory, Check);
    }
    private static async Task VerifyWindow(AssistantWorkspaceSnapshot snapshot, string selectedId, string directory, Action<string, bool> check)
    {
        var current = snapshot.Identity;
        int reads = 0;
        var window = new AssistantWorkWindow(_ => { reads++; return Task.FromResult(snapshot); }, value => value == current)
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); await Idle(window);
            window.DependenciesTab.IsSelected = true;
            check("dependency_page_loads_all_saved_rows_readonly", window.DependenciesGrid.Items.Count == 8 && window.DependenciesGrid.IsReadOnly
                && window.DependencyDetailBox.IsReadOnly && window.DependencyCommandsBox.IsReadOnly);
            window.DependencySearchBox.Text = selectedId;
            window.DependenciesGrid.SelectedIndex = 0;
            string body = window.DependencyDetailBox.Text;
            check("full_id_search_resolves_complete_selected_detail", window.DependenciesGrid.Items.Count == 1
                && body == snapshot.Dependencies!.Single(d => d.Row.Id == selectedId).Body && window.DependencyCommandsBox.Text.Contains("取消依赖 " + selectedId));
            window.DependencySearchBox.Clear();
            check("filter_change_clears_selected_body_and_commands", window.DependenciesGrid.Items.Count == 8 && window.DependencyCommandsBox.Text.Length == 0
                && window.DependencyDetailBox.Text != body);
            window.DependencySearchBox.Text = "失败后不得执行";
            check("full_instruction_search_is_local_to_snapshot", window.DependenciesGrid.Items.Count == 1 && reads == 1);
            window.DependencySearchBox.Text = selectedId; window.DependenciesGrid.SelectedIndex = 0;
            await Idle(window); Capture(window, Path.Combine(directory, "dependencies-normal.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Idle(window);
            check("minimum_window_keeps_list_and_scrollable_detail_visible", window.DependenciesGrid.ActualHeight > 40
                && window.DependencyDetailScroll.ViewportHeight > 45 && window.DependencyDetailScroll.ScrollableHeight > 0);
            Capture(window, Path.Combine(directory, "dependencies-minimum.png"));
            window.DependencyCommandsExpander.IsExpanded = true;
            // Fluent expands with an animation, as in the existing record-detail window tests.
            await Task.Delay(350); await Idle(window);
            window.DependencyDetailScroll.ScrollToBottom(); await Idle(window);
            Capture(window, Path.Combine(directory, "dependencies-commands.png"));
            File.WriteAllText(Path.Combine(directory, "dependency-layout.json"), JsonSerializer.Serialize(new
            {
                window.DependencyDetailScroll.VerticalOffset, window.DependencyDetailScroll.ViewportHeight,
                window.DependencyDetailScroll.ExtentHeight, window.DependencyDetailScroll.ScrollableHeight,
                commandHeight = window.DependencyCommandsBox.ActualHeight,
                commandY = window.DependencyCommandsBox.TransformToAncestor(window.DependencyDetailScroll).Transform(new Point(0, 0)).Y,
                commandBottom = window.DependencyCommandsBox.TransformToAncestor(window.DependencyDetailScroll).Transform(new Point(0, window.DependencyCommandsBox.ActualHeight)).Y,
                window.DependencyCommandsExpander.IsExpanded, commandText = window.DependencyCommandsBox.Text
            }, Settings.Json));
            double commandBottom = window.DependencyCommandsBox.TransformToAncestor(window.DependencyDetailScroll)
                .Transform(new Point(0, window.DependencyCommandsBox.ActualHeight)).Y;
            check("expanded_commands_are_reachable_at_minimum_window", window.DependencyCommandsBox.ActualHeight > 0
                && window.DependencyDetailScroll.VerticalOffset > 0
                && commandBottom > 0 && commandBottom <= window.DependencyDetailScroll.ViewportHeight);
            current = snapshot.Identity! with { BindingId = "switched" }; window.InvalidateIfIdentityChanged();
            check("binding_change_clears_dependency_rows_body_commands_and_search", window.DependenciesGrid.Items.Count == 0
                && window.DependencyDetailBox.Text != body && window.DependencyCommandsBox.Text.Length == 0 && window.DependencySearchBox.Text.Length == 0);
            current = snapshot.Identity; await window.RefreshAsync();
            check("explicit_refresh_restores_only_current_binding_snapshot", window.DependenciesGrid.Items.Count == 8 && window.DependencyCommandsBox.Text.Length == 0);
        }
        finally { window.Close(); }
        check("closing_dependency_page_clears_records", window.DependenciesGrid.Items.Count == 0 && window.DependencyCommandsBox.Text.Length == 0);
    }
    private static async Task Idle(Window window)
    { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Chain host condition not reached."); await Task.Delay(20); }
    }
}
