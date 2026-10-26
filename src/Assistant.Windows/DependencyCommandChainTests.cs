using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class DependencyCommandChainTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-command-chain-test", var directory]) return false;
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
            File.WriteAllText(Path.Combine(directory, "dependency-command-chain-checks.json"), JsonSerializer.Serialize(new
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
        var runtime = f.Runtime.OwnedRuntime!; var identity = f.Runtime.Identity!;
        OwnedRuntimeTask TaskOf(string id) => runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == id);
        JsonElement Item(string command, int code = 0) => JsonSerializer.SerializeToElement(new
        { id = command, type = "commandExecution", command, cwd = profile.Creation.Cwd, status = "completed", exitCode = code });
        void Set(string thread, bool complete, params JsonElement[] items)
        {
            string turn = TaskOf(thread).Snapshot.CurrentTurnId!;
            var data = JsonSerializer.SerializeToElement(new { id = turn, status = complete ? "completed" : "inProgress", items });
            rpc!.SetThread(thread, profile.Creation.Cwd, data);
            rpc.Emit(complete ? "turn/completed" : "turn/started", new { threadId = thread, turn = data });
        }
        async Task Start(string thread, string key)
        {
            string text = "继续 " + thread + " 准备前置条件";
            await OwnedHostRuntimeTests.SendToolAsync(f, key, text, _ => OwnedHostRuntimeTests.ContinueCall(TaskOf(thread), text));
        }
        async Task<string> Create(string key)
        {
            var before = runtime.Tasks().Select(t => t.Handle.Identity.ThreadId).ToHashSet();
            await OwnedHostRuntimeTests.SendToolAsync(f, key, "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
            string thread = runtime.Tasks().Single(t => !before.Contains(t.Handle.Identity.ThreadId)).Handle.Identity.ThreadId;
            await Start(thread, key + "-start"); Set(thread, true);
            await Wait(() => Task.FromResult(workspace.Blockers().Count == 0)); return thread;
        }
        string a = await Create("a"), b = await Create("b");
        await Start(a, "prepare-parent"); Set(a, false); f.Settings.ChatGpt.Enabled = false;
        async Task Input(string key, string body) { await f.InputAsync(key, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkSnapshot> Saved() => await f.Ledger.GetDependentWorkAsync(identity);
        async Task<DependentWorkItem> Work(Guid id) => (await Saved()).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string body)
        {
            var before = (await Saved()).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, body); return (await Saved()).WorkItems.Single(w => !before.Contains(w.Id));
        }
        string Edit(Guid id, string body) => $"修改依赖 {id:D} {body}";
        var parent = await Add("parent", $"等 {a} 本轮完成后，向 {a} 发送：执行前置测试");
        var child = await Add("child", $"等依赖 {parent.Id:D} 测试命令「test-old」成功后，向 {b} 发送：跨任务后续");
        Check("chain_command_freezes_parent_version_and_its_project_directory", child.Requirements.Single() == new DependencyRequirement(
            WorkItemId: parent.Id, WorkVersion: parent.Version, CommandSuccess: new("test-old", profile.Creation.Cwd)) && child.Target.ThreadId == b);
        var source = (await f.ReadAsync()).Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("child"));
        var adapter = new DependentWorkToolAdapter(f.Ledger, () => runtime, value => value == identity, () => true, () => DateTimeOffset.UtcNow);
        var exact = (await adapter.ResolveAsync(source, CancellationToken.None)).Call!;
        var altered = JsonNode.Parse(exact.ArgumentsJson)!; altered["requirements"]![0]!.AsObject().Remove("commandSuccess");
        var descriptor = DependentWorkToolAdapter.Descriptors().Single(d => d.Name == exact.Name);
        Check("model_cannot_strip_chain_command_condition", (await adapter.AuthorizeAsync(new(source, exact, descriptor), CancellationToken.None)).Allowed
            && !(await adapter.AuthorizeAsync(new(source, exact with { ArgumentsJson = altered.ToJsonString() }, descriptor), CancellationToken.None)).Allowed);

        await Input("pause-child", "暂停依赖 " + child.Id.ToString("D"));
        await Input("edit-paused", Edit(child.Id, "测试命令：「test-edited」"));
        var changed = await Work(child.Id);
        Check("command_only_edit_keeps_pause_original_target_and_parent_version", changed.Stage == DependentWorkStage.Paused && changed.Version == 3
            && changed.Target == child.Target && changed.Instructions == child.Instructions && changed.Requirements.Single().WorkVersion == 1
            && changed.Requirements.Single().CommandSuccess == new DependencyCommandRequirement("test-edited", profile.Creation.Cwd));
        await Input("parent-version", $"修改依赖 {parent.Id:D}：执行新版前置测试"); parent = await Work(parent.Id);
        await Input("resume-child", "恢复依赖 " + child.Id.ToString("D")); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("parent_change_is_not_implicitly_adopted", (await Work(child.Id)).Stage == DependentWorkStage.NeedsDecision && rpc!.GuardedTexts.IsEmpty);
        await Input("edit-stale-parent", Edit(child.Id, "测试命令：「test-pass」")); await f.Runtime.AdvanceDependenciesAsync(identity);
        changed = await Work(child.Id);
        Check("editing_only_command_does_not_refresh_parent_version", changed.Stage == DependentWorkStage.NeedsDecision && changed.Requirements.Single().WorkVersion == 1);
        await Input("ambiguous-rebind", Edit(child.Id, $"前置：依赖 {parent.Id:D}"));
        Check("condition_replacement_requires_explicit_command_choice", (await Work(child.Id)).Version == changed.Version
            && f.Sent.Any(s => s.Contains("原安排含测试命令条件")));
        string date = DateTimeOffset.UtcNow.AddDays(2).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        string composite = Edit(child.Id, $"前置：依赖 {parent.Id:D}；测试命令：「test-pass」；截止：{date}");
        await Input("explicit-rebind", composite); child = await Work(child.Id);
        Check("combined_edit_saves_new_parent_command_and_deadline_together", child.Stage == DependentWorkStage.Waiting
            && child.Requirements.Single().WorkVersion == parent.Version && child.Requirements.Single().CommandSuccess!.Command == "test-pass"
            && child.DeadlineUtc == DateTimeOffset.ParseExact(date, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture).ToUniversalTime());
        await Input("invalid-composite", Edit(child.Id, $"前置：依赖 {parent.Id:D}；测试命令：「must-not-save」；截止：2000-01-01 01:00 +08:00"));
        Check("invalid_combined_deadline_leaves_all_fields_unchanged", (await Work(child.Id)) == child);

        var same = await Add("same", $"等依赖 {parent.Id:D} 测试命令「test-same」成功后，在同一任务继续：同任务后续");
        Check("same_task_command_keeps_explicit_continuation_anchor", same.ContinueAfterWorkId == parent.Id && same.Target == parent.Target
            && same.Requirements.Single().WorkVersion == parent.Version && same.Requirements.Single().CommandSuccess!.Command == "test-same");
        await Input("remove-anchor", Edit(same.Id, $"前置：任务 {a}；测试命令：无"));
        Check("clearing_command_cannot_remove_same_task_anchor", (await Work(same.Id)).Version == same.Version);
        await Input("pause-same", "暂停依赖 " + same.Id.ToString("D"));

        var ordinary = await Add("ordinary", $"等 {a} 本轮完成后，向 {b} 发送：取消的编辑示例");
        var originalRequirement = ordinary.Requirements.Single();
        await Input("attach-command", Edit(ordinary.Id, "测试命令：「test-added」"));
        changed = await Work(ordinary.Id);
        Check("command_can_be_added_to_existing_exact_task_requirement", changed.Requirements.Single().Task == originalRequirement.Task
            && changed.Requirements.Single().CommandSuccess == new DependencyCommandRequirement("test-added", profile.Creation.Cwd));
        await Input("clear-command", Edit(ordinary.Id, "测试命令：无"));
        Check("explicit_clear_preserves_original_task_condition", (await Work(ordinary.Id)).Requirements.Single() == originalRequirement);
        await Input("cancel-example", "取消依赖 " + ordinary.Id.ToString("D"));
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        var detail = desktop.Dependencies!.Single(d => d.Row.Id == child.Id.ToString("D"));
        Check("desktop_explains_latest_combined_edit_and_provides_command_actions", detail.Body.Contains(composite) && detail.Body.Contains("test-pass")
            && detail.Commands.Contains("测试命令：「原样命令」") && detail.Commands.Contains("测试命令：无") && detail.Commands.Contains("成功后，在同一任务继续"));

        Set(a, true); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Running);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("parent_acceptance_does_not_satisfy_child_command", (await Work(child.Id)).Stage == DependentWorkStage.Waiting
            && rpc!.GuardedTexts.SequenceEqual(new[] { "执行新版前置测试" }));
        Set(a, true); await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Completed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("completed_parent_without_command_records_keeps_child_waiting", (await Work(child.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.Count == 1);
        Set(a, true, Item("test-pass")); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Running);
        var childAttempt = (await Saved()).Attempts.Single(x => x.WorkItemId == child.Id);
        parent = await Work(parent.Id); child = await Work(child.Id);
        Check("cross_task_dispatch_uses_actual_parent_result_command_evidence", childAttempt.Evidence[1].Observation.Task == parent.ResultTask
            && childAttempt.Evidence[1].Commands!.Value.Single().Command == "test-pass" && child.ResultTask!.ThreadId == b);
        await Input("edit-accepted", Edit(child.Id, "测试命令：无"));
        Check("accepted_work_cannot_be_reopened_by_clearing_condition", (await Work(child.Id)).Version == child.Version);
        Set(b, true); await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Completed);
        await Wait(() => Task.FromResult(workspace.Blockers().Count == 0));

        await Input("resume-same", "恢复依赖 " + same.Id.ToString("D")); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("different_command_on_same_parent_does_not_release_continuation", (await Work(same.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.Count == 2);
        Set(a, true, Item("test-pass"), Item("test-same")); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(same.Id)).Stage == DependentWorkStage.Running);
        var sameAttempt = (await Saved()).Attempts.Single(x => x.WorkItemId == same.Id);
        same = await Work(same.Id);
        Check("same_task_dispatch_starts_from_actual_tested_result", sameAttempt.Evidence[0].Observation.Task == parent.ResultTask
            && sameAttempt.Evidence[1].Observation.Task == parent.ResultTask && same.ResultTask!.ThreadId == a
            && same.ResultTask.TurnId != parent.ResultTask!.TurnId && sameAttempt.Evidence[1].Commands!.Value.Last().Command == "test-same");
        Set(a, true); await Wait(async () => (await Work(same.Id)).Stage == DependentWorkStage.Completed);
        for (int i = 0; i < 5; i++) await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("all_three_steps_execute_once_with_no_remaining_workspace_claim", rpc!.GuardedTexts.SequenceEqual(new[] { "执行新版前置测试", "跨任务后续", "同任务后续" })
            && (await Saved()).Attempts.Length == 3 && workspace.Blockers().Count == 0);
        await f.Runtime.StopSessionAsync();
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic command chain condition not reached."); await Task.Delay(20); }
    }
}
