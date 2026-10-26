using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class DependencyRecoveryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-recovery-test", var directory]) return false;
        try { await RunAsync(directory); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("New isolated output required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "dependency-recovery-checks.json"), JsonSerializer.Serialize(new
            { checks, networkUsed = false, actualModelUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var clock = new Clock(); var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (p, _) =>
        { rpc = new(p.Configuration, () => Task.FromResult(true)); return Task.FromResult<IJsonRpcConnection>(rpc); });
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), ownedOptions: options, clock: clock,
            ledgerOptions: new() { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 20 } });
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!; var identity = f.Runtime.Identity!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        string start = "继续 " + thread + " 准备";
        await OwnedHostRuntimeTests.SendToolAsync(f, "start", start, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), start));
        f.Settings.ChatGpt.Enabled = false;
        async Task Observe(bool complete)
        {
            string turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!; string status = complete ? "completed" : "inProgress";
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, status));
            rpc.Emit(complete ? "turn/completed" : "turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, status) });
            await Wait(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == (complete ? OwnedTaskState.Completed : OwnedTaskState.Running)));
        }
        async Task Input(string key, string body) { await f.InputAsync(key, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkItem> Work(Guid id) => (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string command)
        {
            var before = (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Select(w => w.Id).ToHashSet();
            await Input(key, command); return (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Single(w => !before.Contains(w.Id));
        }
        async Task<ConversationEvent?> Event(Guid id, DependentWorkStage stage) => (await f.ReadAsync()).Events
            .SingleOrDefault(e => e.DependentWorkNotice?.WorkItemId == id && e.DependentWorkNotice.Stage == stage);
        async Task<bool> Delivered(ConversationEvent entry) => (await f.ReadAsync()).Outbox.Where(o => o.NotificationEventId == entry.Id).All(o => o.Stage == OutboxStage.Accepted);
        async Task<ConversationEvent> Notice(Guid id, DependentWorkStage stage)
        {
            await Wait(async () => await Event(id, stage) != null); return (await Event(id, stage))!;
        }
        await Observe(false);
        var parent = await Add("parent", $"等 {thread} 本轮完成后，向 {thread} 发送：被取消的前置工作");
        var work = await Add("child", $"等依赖 {parent.Id:D} 完成后，向 {thread} 发送：修正后接续的安排");
        await Input("cancel-parent", "取消依赖 " + parent.Id.ToString("D")); await f.Runtime.AdvanceDependenciesAsync(identity);
        var warning = await Notice(work.Id, DependentWorkStage.NeedsDecision); await Wait(() => Delivered(warning));
        Check("original_blocked_notice_is_accepted_before_recovery_tracking", warning.DependentWorkNotice!.RecoveryOfEventId == null && rpc!.GuardedTexts.IsEmpty);
        await Input("correct", $"修改依赖 {work.Id:D} 前置：任务 {thread}");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("corrected_but_waiting_arrangement_does_not_claim_recovery", (await Work(work.Id)).Stage == DependentWorkStage.Waiting
            && !(await f.ReadAsync()).Events.Any(e => e.DependentWorkNotice?.RecoveryOfEventId != null));
        await Observe(true); await f.Runtime.AdvanceDependenciesAsync(identity);
        var running = await Notice(work.Id, DependentWorkStage.Running); await Wait(() => Delivered(running));
        work = await Work(work.Id);
        Check("real_acceptance_links_original_warning_exact_attempt_and_result_turn", running.DependentWorkNotice!.RecoveryOfEventId == warning.Id
            && running.DependentWorkNotice.AttemptId == work.AttemptId && running.Task == work.ResultTask && running.Body.Contains("实际结果还在等待")
            && work.Stage == DependentWorkStage.Running);
        for (int i = 0; i < 10; i++) await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("ten_unchanged_running_refreshes_emit_one_recovery_notice", f.Sent.Count(s => s == running.Body) == 1 && rpc!.GuardedTexts.Count == 1);
        await Observe(true); await Wait(async () => (await Work(work.Id)).Stage == DependentWorkStage.Completed);
        var complete = await Notice(work.Id, DependentWorkStage.Completed); await Wait(() => Delivered(complete));
        var snapshot = await f.ReadAsync();
        Check("actual_completion_updates_recovery_without_claiming_tests_passed", complete.Task == work.ResultTask && complete.Body.Contains("本轮已结束")
            && complete.Body.Contains("测试是否通过仍需核对") && complete.DependentWorkNotice!.RecoveryOfEventId == warning.Id);
        Check("recovered_completion_does_not_duplicate_default_owned_notice", !snapshot.Events.Any(e => e.Task == work.ResultTask
            && e.ExternalEventKey?.StartsWith("owned-event/") == true && e.Body.Contains("这一轮已经结束")));
        await Observe(true); for (int i = 0; i < 10; i++) await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("repeated_completion_is_silent_and_does_not_restart_execution", f.Sent.Count(s => s == complete.Body) == 1 && rpc!.GuardedTexts.Count == 1);

        var cancelled = await Add("cancelled", $"等依赖 {parent.Id:D} 完成后，向 {thread} 发送：发送等待中取消恢复提示");
        await f.Runtime.AdvanceDependenciesAsync(identity); var beforeCancel = await Notice(cancelled.Id, DependentWorkStage.NeedsDecision); await Wait(() => Delivered(beforeCancel));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc!.BeforeGuard = async () => { entered.TrySetResult(); await release.Task; };
        await Input("correct-cancelled", $"修改依赖 {cancelled.Id:D} 前置：任务 {thread}");
        var advance = f.Runtime.AdvanceDependenciesAsync(identity); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var channelGate = (SemaphoreSlim)typeof(WeChatService).GetField("clawSendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Channel)!;
        await channelGate.WaitAsync();
        ConversationEvent held;
        try
        {
            release.TrySetResult(); await advance; held = await Notice(cancelled.Id, DependentWorkStage.Running);
            await Wait(async () => (await f.ReadAsync()).Outbox.Any(o => o.NotificationEventId == held.Id && o.Stage == OutboxStage.Sending));
            await f.InputAsync("cancel-during-channel-wait", "取消依赖 " + cancelled.Id.ToString("D"));
            await Wait(async () => (await Work(cancelled.Id)).Stage == DependentWorkStage.Cancelled);
        }
        finally { release.TrySetResult(); rpc.BeforeGuard = null; channelGate.Release(); }
        await Wait(async () => (await f.ReadAsync()).Outbox.Any(o => o.NotificationEventId == held.Id && o.Stage == OutboxStage.Rejected));
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-during-channel-wait");
        Check("actual_channel_gate_rechecks_cancel_before_http", !f.Sent.Contains(held.Body)
            && (await f.ReadAsync()).Outbox.Single(o => o.NotificationEventId == held.Id).ChannelReceipt!.Contains("尚未调用渠道"));
        await Observe(true); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("cancelled_arrangement_keeps_task_result_without_recovery_notification", (await Work(cancelled.Id)).Completion != null
            && await Event(cancelled.Id, DependentWorkStage.Completed) == null && rpc.GuardedTexts.Count == 2);

        var quiet = await Add("quiet-child", $"等依赖 {parent.Id:D} 完成后，向 {thread} 发送：安静时段只报最新进展");
        await f.Runtime.AdvanceDependenciesAsync(identity); var quietWarning = await Notice(quiet.Id, DependentWorkStage.NeedsDecision); await Wait(() => Delivered(quietWarning));
        string from = clock.UtcNow.ToLocalTime().AddHours(-1).ToString("HH:mm"), until = clock.UtcNow.ToLocalTime().AddHours(1).ToString("HH:mm");
        await Input("quiet-policy", $"修改依赖 {quiet.Id:D} 安静时段：{from}-{until}");
        await Input("correct-quiet", $"修改依赖 {quiet.Id:D} 前置：任务 {thread}"); await f.Runtime.AdvanceDependenciesAsync(identity);
        var quietRunning = await Notice(quiet.Id, DependentWorkStage.Running);
        Check("quiet_hours_hold_recovery_without_channel_attempt", (await f.ReadAsync()).Outbox.Where(o => o.NotificationEventId == quietRunning.Id)
            .All(o => o.Stage == OutboxStage.Pending && o.SendAttempts == 0));
        await Observe(true); await Wait(async () => (await Work(quiet.Id)).Stage == DependentWorkStage.Completed);
        var quietCompleted = await Notice(quiet.Id, DependentWorkStage.Completed);
        Check("completion_retires_unsent_intermediate_recovery", (await f.ReadAsync()).Outbox.Where(o => o.NotificationEventId == quietRunning.Id).All(o => o.Stage == OutboxStage.Superseded)
            && !f.Sent.Contains(quietRunning.Body) && !f.Sent.Contains(quietCompleted.Body));
        clock.Advance(TimeSpan.FromHours(2)); await f.Runtime.AdvanceDependenciesAsync(identity); await Wait(() => Delivered(quietCompleted));
        Check("quiet_end_sends_only_latest_verified_completion", !f.Sent.Contains(quietRunning.Body) && f.Sent.Count(s => s == quietCompleted.Body) == 1
            && rpc.GuardedTexts.Count == 3);
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unchanged_final_state_keeps_recovery_deduplication", (await f.ReadAsync()).Events.Count(e => e.DependentWorkNotice?.WorkItemId == quiet.Id
            && e.DependentWorkNotice.Stage == DependentWorkStage.Completed) == 1 && f.Sent.Count(s => s == quietCompleted.Body) == 1);
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Dependency recovery condition not reached."); await Task.Delay(20); }
    }
}
