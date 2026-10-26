using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class DependencyCommandTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-command-test", var directory]) return false;
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
            File.WriteAllText(Path.Combine(directory, "dependency-command-checks.json"), JsonSerializer.Serialize(new
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
        string start = "继续 " + thread + " 执行指定测试";
        await OwnedHostRuntimeTests.SendToolAsync(f, "start", start, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), start));
        f.Settings.ChatGpt.Enabled = false;
        const string test = "dotnet test --no-restore";
        string Command(string instruction) => $"等 {thread} 本轮测试命令「{test}」成功后，向 {thread} 发送：{instruction}";
        async Task Input(string key, string text) { await f.InputAsync(key, text); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkSnapshot> Saved() => await f.Ledger.GetDependentWorkAsync(identity);
        async Task<DependentWorkItem> Work(Guid id) => (await Saved()).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string instruction)
        {
            var before = (await Saved()).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, Command(instruction)); return (await Saved()).WorkItems.Single(w => !before.Contains(w.Id));
        }
        JsonElement Item(string id = "test", int? code = 0, string status = "completed", string? command = null, string? cwd = null) =>
            JsonSerializer.SerializeToElement(new { id, type = "commandExecution", command = command ?? test,
                cwd = cwd ?? profile.Creation.Cwd, status, exitCode = code });
        void Set(bool complete, params JsonElement[] items)
        {
            string turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
            var data = JsonSerializer.SerializeToElement(new { id = turn, status = complete ? "completed" : "inProgress", items });
            rpc!.SetThread(thread, profile.Creation.Cwd, data);
            rpc.Emit(complete ? "turn/completed" : "turn/started", new { threadId = thread, turn = data });
        }
        Set(false);
        var gate = await Add("gate", "核对测试后的文档");
        Check("model_off_command_pins_exact_turn_literal_command_and_project_directory", gate.Requirements.Single().CommandSuccess == new DependencyCommandRequirement(test, profile.Creation.Cwd)
            && gate.Requirements.Single().Task!.TurnId == gate.Target.TurnId && rpc!.GuardedTexts.IsEmpty);
        Check("receipt_states_exact_exit_code_scope", f.Sent.Any(s => s.Contains(test) && s.Contains(profile.Creation.Cwd) && s.Contains("退出码为 0")));
        var source = (await f.ReadAsync()).Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("gate"));
        var adapter = new DependentWorkToolAdapter(f.Ledger, () => runtime, value => value == identity, () => true, () => DateTimeOffset.UtcNow);
        var exact = (await adapter.ResolveAsync(source, CancellationToken.None)).Call!;
        var altered = JsonNode.Parse(exact.ArgumentsJson)!;
        altered["requirements"]![0]!["commandSuccess"]!["command"] = "echo tests passed";
        var descriptor = DependentWorkToolAdapter.Descriptors().Single(d => d.Name == exact.Name);
        Check("model_cannot_substitute_an_easier_command", (await adapter.AuthorizeAsync(new(source, exact, descriptor), CancellationToken.None)).Allowed
            && !(await adapter.AuthorizeAsync(new(source, exact with { ArgumentsJson = altered.ToJsonString() }, descriptor), CancellationToken.None)).Allowed);
        var report = JsonSerializer.SerializeToElement(new { id = "report", type = "agentMessage", phase = "final_answer", text = "All tests passed. 全部测试通过。" });
        Set(true, report); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("turn_completion_and_report_claim_do_not_release_work", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(cwd: profile.Creation.Cwd + "-other")); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("same_command_in_another_directory_does_not_release", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(command: test + " --filter other")); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("different_command_is_not_an_equivalent_test", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(code: null)); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("missing_exit_code_is_not_success", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(), Item("retry", null, "inProgress")); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unfinished_retry_blocks_prior_success", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(), JsonSerializer.SerializeToElement(new { id = "unreadable-retry", type = "commandExecution", command = test, status = "failed", exitCode = 1 }));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unreadable_retry_does_not_silently_disappear", (await Work(gate.Id)).Stage == DependentWorkStage.Waiting && rpc!.GuardedTexts.IsEmpty);
        Set(true, Item(), Item("retry", 1)); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("last_failed_execution_requires_new_decision", (await Work(gate.Id)).Reason == "predecessor_command_failed" && rpc!.GuardedTexts.IsEmpty);

        Set(false); var success = await Add("success", "只发送一次的后续指令");
        Set(true, Item("first", 1), Item("retry")); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(success.Id)).Stage == DependentWorkStage.Running);
        var attempt = (await Saved()).Attempts.Single(a => a.WorkItemId == success.Id);
        Check("last_success_releases_once_and_saves_actual_command_items", attempt.Evidence[1].Commands!.Value.Select(c => c.ItemId).SequenceEqual(new[] { "first", "retry" })
            && attempt.Evidence[1].Commands!.Value.Last().ExitCode == 0 && rpc!.GuardedTexts.SequenceEqual(new[] { "只发送一次的后续指令" }));
        for (int i = 0; i < 5; i++) await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("accepted_followup_never_reexecutes_when_polled", rpc!.GuardedTexts.Count == 1);
        await Input("list", "依赖列表");
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        var detail = desktop.Dependencies!.Single(d => d.Row.Id == success.Id.ToString("D"));
        Check("list_and_desktop_show_requirement_and_actual_item_evidence", f.Sent.Any(s => s.Contains("前置：") && s.Contains(test))
            && detail.Body.Contains("指定测试命令：") && detail.Body.Contains("retry") && detail.Body.Contains("退出码 0") && detail.Body.Contains(profile.Creation.Cwd));

        Set(false); var raced = await Add("race", "证据变更后不得发送");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc!.BeforeGuard = async () => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); };
        Task advance = Task.CompletedTask;
        try
        {
            Set(true, Item()); advance = f.Runtime.AdvanceDependenciesAsync(identity);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            string turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
            var changed = Item("changed", 3);
            rpc.SetThread(thread, profile.Creation.Cwd, JsonSerializer.SerializeToElement(new { id = turn, status = "completed", items = new[] { changed } }));
            rpc.Emit("item/completed", new { threadId = thread, turnId = turn, item = changed });
            release.TrySetResult(); await advance;
        }
        finally { release.TrySetResult(); rpc.BeforeGuard = null; await advance; }
        await f.Runtime.AdvanceDependenciesAsync(identity);
        var racedAttempt = (await Saved()).Attempts.Single(a => a.WorkItemId == raced.Id);
        Check("command_change_at_actual_write_boundary_cancels_dispatch", racedAttempt.Stage == DependentDispatchStage.NotDispatched
            && (await Work(raced.Id)).Stage == DependentWorkStage.NeedsDecision && rpc.GuardedTexts.Count == 1);
        Check("cancelled_before_write_does_not_leak_workspace_reservation", workspace.Blockers().Count == 0);
        await f.Runtime.StopSessionAsync();
        Check("clean_stop_keeps_test_evidence_without_real_services", f.Runtime.LastError == null && !(await f.Ledger.ReadConversationAsync(identity)).IsReadOnly);
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic command prerequisite not reached."); await Task.Delay(20); }
    }
}
