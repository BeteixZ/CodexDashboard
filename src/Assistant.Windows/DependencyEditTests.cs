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

internal static class DependencyEditTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-edit-test", var directory]) return false;
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
            File.WriteAllText(Path.Combine(directory, "dependency-edit-checks.json"), JsonSerializer.Serialize(new
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
        async Task Observe(string thread, bool complete)
        {
            var task = TaskOf(thread); string turn = task.Snapshot.CurrentTurnId!;
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, complete ? "completed" : "inProgress"));
            rpc.Emit(complete ? "turn/completed" : "turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, complete ? "completed" : "inProgress") });
            await Wait(() => Task.FromResult(TaskOf(thread).Snapshot.State == (complete ? OwnedTaskState.Completed : OwnedTaskState.Running)));
            if (complete) await Wait(() => Task.FromResult(workspace.Blockers().Count == 0));
        }
        async Task Start(string thread, string key)
        {
            string text = "继续 " + thread + " 准备依赖";
            await OwnedHostRuntimeTests.SendToolAsync(f, key, text, _ => OwnedHostRuntimeTests.ContinueCall(TaskOf(thread), text));
        }
        async Task<string> Create(string key)
        {
            var before = runtime.Tasks().Select(t => t.Handle.Identity.ThreadId).ToHashSet();
            await OwnedHostRuntimeTests.SendToolAsync(f, key, "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
            string id = runtime.Tasks().Single(t => !before.Contains(t.Handle.Identity.ThreadId)).Handle.Identity.ThreadId;
            await Start(id, key + "-start"); await Observe(id, true); return id;
        }
        string a = await Create("a"), b = await Create("b");
        await Start(a, "predecessor-running"); await Observe(a, false);
        f.Settings.ChatGpt.Enabled = false;
        async Task Input(string key, string body) { await f.InputAsync(key, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkSnapshot> Saved() => await f.Ledger.GetDependentWorkAsync(identity);
        async Task<DependentWorkItem> Work(Guid id) => (await Saved()).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string target, string instruction)
        {
            var before = (await Saved()).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, $"等 {a} 本轮完成后，向 {target} 发送：{instruction}");
            return (await Saved()).WorkItems.Single(w => !before.Contains(w.Id));
        }
        var parent = await Add("parent", a, "前置原文");
        var child = await Add("child", b, "后续原文");
        string date = DateTimeOffset.UtcNow.AddDays(2).ToOffset(TimeSpan.FromMinutes(345)).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        var deadline = DateTimeOffset.ParseExact(date, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        string Edit(Guid id, string fields) => $"修改依赖 {id:D} {fields}";
        string deadlineCommand = Edit(child.Id, "截止：" + date);
        await Input("deadline", deadlineCommand);
        var changed = await Work(child.Id);
        Check("model_off_deadline_edit_converts_explicit_offset_and_keeps_original_work", changed.DeadlineUtc == deadline.ToUniversalTime()
            && changed.DeadlineUtc.Offset == TimeSpan.Zero && changed.Version == 2 && changed.Target == child.Target
            && changed.Instructions == child.Instructions && changed.Requirements.SequenceEqual(child.Requirements) && rpc!.GuardedTexts.IsEmpty);
        long version = changed.Version;
        await Input("missing-offset", Edit(child.Id, "截止：" + deadline.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        await Input("past", Edit(child.Id, "截止：2000-01-01 12:00 +08:00"));
        await Input("invalid-id", "修改依赖 ------------------------------------ 截止：" + date);
        Check("ambiguous_past_time_and_invalid_id_leave_work_unchanged", (await Work(child.Id)).Version == version);
        await Input("pause", "暂停依赖 " + child.Id.ToString("D"));
        string composite = Edit(child.Id, $"前置：依赖 {parent.Id:D}；截止：{date}");
        await Input("replace-paused", composite);
        changed = await Work(child.Id);
        Check("combined_edit_atomically_replaces_conditions_and_deadline_while_paused", changed.Stage == DependentWorkStage.Paused
            && changed.Requirements.Single() == new DependencyRequirement(WorkItemId: parent.Id, WorkVersion: parent.Version)
            && changed.DeadlineUtc == deadline.ToUniversalTime() && changed.Instructions == "后续原文");
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        string body = desktop.Dependencies!.Single(d => d.Row.Id == child.Id.ToString("D")).Body;
        Check("desktop_separately_shows_original_and_latest_edit_request", body.Contains("登记时的原始请求：")
            && body.Contains($"等 {a} 本轮完成后，向 {b} 发送：后续原文") && body.Contains("最近一次调整请求：\n" + composite));
        Check("edit_receipt_reports_saved_version_deadline_and_pinned_parent", f.Sent.Any(s => s.Contains("第 4 版")
            && s.Contains($"依赖 {parent.Id:D} 第 1 版完成") && s.Contains("截止")));
        await Input("change-parent", $"修改依赖 {parent.Id:D}：前置的新原文");
        await Input("resume-child", "恢复依赖 " + child.Id.ToString("D"));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("old_parent_version_remains_blocked_after_resume", (await Work(child.Id)).Stage == DependentWorkStage.NeedsDecision);
        await Input("deadline-only", deadlineCommand); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("deadline_edit_does_not_silently_refresh_invalid_predecessor", (await Work(child.Id)).Stage == DependentWorkStage.NeedsDecision
            && (await Work(child.Id)).Requirements.Single().WorkVersion == 1 && rpc!.GuardedTexts.IsEmpty);
        await Input("explicit-rebind", Edit(child.Id, $"前置：依赖 {parent.Id:D}"));
        changed = await Work(child.Id); parent = await Work(parent.Id);
        Check("explicit_predecessor_edit_recovers_needs_decision_with_current_version", changed.Stage == DependentWorkStage.Waiting
            && changed.Requirements.Single().WorkVersion == parent.Version && parent.Version == 2 && rpc!.GuardedTexts.IsEmpty);
        var source = await f.Ledger.ReceiveAsync(identity, "explicit-edit-auth", deadlineCommand, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var adapter = new DependentWorkToolAdapter(f.Ledger, () => runtime, value => value == identity, () => true, () => DateTimeOffset.UtcNow);
        var exact = (await adapter.ResolveAsync(source.Message, CancellationToken.None)).Call!;
        var parameters = JsonNode.Parse(exact.ArgumentsJson)!;
        parameters["deadlineUtc"] = deadline.AddHours(1).ToUniversalTime().ToString("O");
        var substituted = exact with { ArgumentsJson = parameters.ToJsonString() };
        var descriptor = DependentWorkToolAdapter.Descriptors().Single(d => d.Name == exact.Name);
        Check("model_authorization_accepts_exact_edit_but_rejects_substituted_deadline", (await adapter.AuthorizeAsync(new(source.Message, exact, descriptor), CancellationToken.None)).Allowed
            && !(await adapter.AuthorizeAsync(new(source.Message, substituted, descriptor), CancellationToken.None)).Allowed);
        long parentVersion = parent.Version, childVersion = changed.Version;
        await Input("cycle", Edit(parent.Id, $"前置：依赖 {child.Id:D}"));
        await Input("self", Edit(child.Id, $"前置：依赖 {child.Id:D}"));
        Check("cycle_and_self_reference_reject_without_partial_mutation", (await Work(parent.Id)).Version == parentVersion
            && (await Work(child.Id)).Version == childVersion && (await Saved()).Attempts.IsEmpty);
        await Input("direct", Edit(child.Id, $"前置：任务 {a}"));
        changed = await Work(child.Id);
        Check("task_predecessor_edit_captures_exact_current_turn_and_configuration", changed.Requirements.Single().Task!.TurnId == TaskOf(a).Snapshot.CurrentTurnId
            && changed.Requirements.Single().BindingFingerprint == OwnedVerification.ConfigurationDigest(profile.Configuration));
        await Input("back-to-chain", Edit(child.Id, $"前置：依赖 {parent.Id:D}"));
        await Observe(a, true); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Running);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("edited_chain_still_waits_for_actual_parent_result", (await Work(child.Id)).Stage == DependentWorkStage.Waiting
            && rpc!.GuardedTexts.SequenceEqual(new[] { "前置的新原文" }));
        await Observe(a, true); await Wait(async () => (await Work(parent.Id)).Stage == DependentWorkStage.Completed);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Running);
        Check("edited_conditions_dispatch_original_child_instruction_once", rpc!.GuardedTexts.SequenceEqual(new[] { "前置的新原文", "后续原文" }));
        changed = await Work(child.Id);
        await Input("edit-running", Edit(child.Id, "截止：" + date));
        Check("accepted_work_cannot_be_reopened_by_deadline_edit", (await Work(child.Id)).Version == changed.Version && (await Saved()).Attempts.Length == 2);
        await Observe(b, true); await Wait(async () => (await Work(child.Id)).Stage == DependentWorkStage.Completed);
        await Input("edit-completed", Edit(child.Id, $"前置：任务 {a}"));
        Check("completed_work_keeps_original_terminal_record", (await Work(child.Id)).Version == changed.Version && (await Work(child.Id)).Completion != null);

        rpc!.GuardedReplyLost = true;
        await Input("unknown", $"等 {b} 本轮完成后，向 {b} 发送：未知不重发");
        await Wait(async () => (await Saved()).WorkItems.Any(w => w.Stage == DependentWorkStage.Unknown));
        var unknown = (await Saved()).WorkItems.Single(w => w.Stage == DependentWorkStage.Unknown);
        await Input("edit-unknown", Edit(unknown.Id, "截止：" + date));
        Check("unknown_dispatch_edit_keeps_original_attempt_and_workspace_claim", (await Work(unknown.Id)) == unknown
            && rpc.GuardedTexts.Count == 3 && workspace.Blockers().Single().State == "Unknown");

    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Dependency edit condition not reached."); await Task.Delay(20); }
    }
}
