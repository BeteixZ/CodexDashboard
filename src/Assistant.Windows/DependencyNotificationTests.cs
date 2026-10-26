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

internal static class DependencyNotificationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--dependency-notification-test", var directory]) return false;
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
            File.WriteAllText(Path.Combine(directory, "dependency-notification-checks.json"), JsonSerializer.Serialize(new
            { checks, networkUsed = false, actualModelUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var clock = new Clock();
        var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (p, _) =>
        { rpc = new(p.Configuration, () => Task.FromResult(true)); return Task.FromResult<IJsonRpcConnection>(rpc); });
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), ownedOptions: options, clock: clock);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!; var identity = f.Runtime.Identity!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        async Task Start(string key)
        {
            f.Settings.ChatGpt.Enabled = true;
            string text = "继续 " + thread + " 准备";
            await OwnedHostRuntimeTests.SendToolAsync(f, key, text, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), text));
            f.Settings.ChatGpt.Enabled = false;
        }
        async Task Observe(string status)
        {
            string turn = runtime.Tasks().Single().Snapshot.CurrentTurnId!;
            rpc!.SetThread(thread, profile.Creation.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, status));
            rpc.Emit(status == "inProgress" ? "turn/started" : "turn/completed", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, status) });
            await Wait(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == (status == "inProgress" ? OwnedTaskState.Running : status == "completed" ? OwnedTaskState.Completed : OwnedTaskState.Failed)));
        }
        async Task Input(string key, string text) { await f.InputAsync(key, text); await OwnedHostRuntimeTests.WaitReplyAsync(f, key); }
        async Task<DependentWorkItem> Work(Guid id) => (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Single(w => w.Id == id);
        async Task<DependentWorkItem> Add(string key, string text)
        {
            await Input(key, $"等 {thread} 本轮完成后，向 {thread} 发送：{text}");
            return (await f.Ledger.GetDependentWorkAsync(identity)).WorkItems.Single(w => w.Instructions == text);
        }
        async Task<ConversationEvent> Notice(Guid id) => (await f.ReadAsync()).Events.Single(e => e.DependentWorkNotice?.WorkItemId == id);
        async Task<bool> Delivered(Guid id) => (await f.ReadAsync()).Outbox.Any(o => o.NotificationEventId == id && o.Stage == OutboxStage.Accepted);

        await Start("start"); await Observe("inProgress");
        var blocked = await Add("blocked", "前置失败时暂停这项后续安排");
        string from = clock.UtcNow.ToLocalTime().AddHours(-1).ToString("HH:mm"), until = clock.UtcNow.ToLocalTime().AddHours(1).ToString("HH:mm");
        string quietCommand = $"修改依赖 {blocked.Id:D} 安静时段：{from}-{until}";
        await Input("quiet", quietCommand); blocked = await Work(blocked.Id);
        Check("model_off_quiet_command_saves_exact_local_timezone_and_version", blocked.Version == 2
            && blocked.NotificationPolicy?.QuietStart == from && blocked.NotificationPolicy.QuietEnd == until
            && blocked.NotificationPolicy.TimeZoneId == TimeZoneInfo.Local.Id);
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        Check("desktop_and_receipt_show_actual_policy_and_management_command", desktop.Dependencies!.Single().Body.Contains($"安静时段 {from}-{until}")
            && desktop.Dependencies!.Single().Commands.Contains("安静时段：无") && f.Sent.Any(s => s.Contains($"安静时段 {from}-{until}")));
        var source = (await f.ReadAsync()).Inbox.Single(i => i.Body == quietCommand);
        var adapter = new DependentWorkToolAdapter(f.Ledger, () => runtime, i => i == identity, () => true, () => clock.UtcNow);
        var exact = (await adapter.ResolveAsync(source, CancellationToken.None)).Call!;
        var changed = JsonNode.Parse(exact.ArgumentsJson)!; changed["notificationPolicy"]!["maximumDeliveryAttempts"] = 8;
        var descriptor = DependentWorkToolAdapter.Descriptors().Single(d => d.Name == exact.Name);
        Check("model_cannot_raise_notification_budget_while_setting_quiet_hours", (await adapter.AuthorizeAsync(new(source, exact, descriptor), CancellationToken.None)).Allowed
            && !(await adapter.AuthorizeAsync(new(source, exact with { ArgumentsJson = changed.ToJsonString() }, descriptor), CancellationToken.None)).Allowed);
        await Input("invalid-quiet", $"修改依赖 {blocked.Id:D} 安静时段：24:99-08:00");
        Check("invalid_quiet_time_does_not_change_authorization", (await Work(blocked.Id)).Version == blocked.Version);
        await Observe("failed"); await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(blocked.Id)).Stage == DependentWorkStage.NeedsDecision);
        var pending = await Notice(blocked.Id); var saved = await f.ReadAsync();
        Check("failed_predecessor_captures_one_notice_and_quiet_hours_hold_without_attempt", pending.Body.Contains("前置任务失败或中断")
            && saved.Outbox.Where(o => o.NotificationEventId == pending.Id).All(o => o.Stage == OutboxStage.Pending && o.SendAttempts == 0)
            && !saved.CommitmentBudgetReservations.Any(r => r.WorkItemId == blocked.Id) && rpc!.GuardedTexts.IsEmpty);
        await f.Runtime.AdvanceDependenciesAsync(identity); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("unchanged_refresh_does_not_recapture_failure_notice", (await f.ReadAsync()).Events.Count(e => e.DependentWorkNotice?.WorkItemId == blocked.Id) == 1);
        clock.Advance(TimeSpan.FromHours(2)); await f.Runtime.AdvanceDependenciesAsync(identity);
        try { await Wait(() => Delivered(pending.Id)); }
        catch
        {
            var diagnosis = await f.ReadAsync();
            File.WriteAllText(Path.Combine(directory, "delivery-diagnosis.json"), JsonSerializer.Serialize(new
            {
                clock.UtcNow, f.Runtime.LastError, f.Channel.ClawStatus,
                work = await Work(blocked.Id), pending,
                outputs = diagnosis.Outbox, plan = await f.Ledger.GetRecoveryPlanAsync(identity),
                windows = diagnosis.CatchUpWindows, budget = diagnosis.CommitmentBudgetReservations,
                channelGuard = diagnosis.Outbox.Where(o => o.NotificationEventId == pending.Id).Select(f.Ledger.IsDependentNotificationCurrent).ToArray()
            }, Settings.Json));
            throw;
        }
        Check("quiet_end_releases_factual_notice_once_through_actual_channel_adapter", f.Sent.Count(s => s == pending.Body) == 1
            && (await f.ReadAsync()).CommitmentBudgetReservations.Count(r => r.WorkItemId == blocked.Id) == 1 && rpc!.GuardedTexts.IsEmpty);

        // Reset the fixture clock before new live-like synthetic observations; expiry uses explicit fake-time advancement below.
        clock.Advance(DateTimeOffset.UtcNow - clock.UtcNow);
        await Start("start-execution"); await Observe("completed");
        var executed = await Add("executed", "执行后失败不重做");
        await f.Runtime.AdvanceDependenciesAsync(identity);
        await Wait(async () => (await Work(executed.Id)).Stage == DependentWorkStage.Running);
        await Observe("failed");
        await Wait(async () => (await Work(executed.Id)).Stage == DependentWorkStage.Failed);
        await f.Runtime.AdvanceDependenciesAsync(identity); var failed = await Notice(executed.Id);
        await Wait(() => Delivered(failed.Id));
        saved = await f.ReadAsync(); executed = await Work(executed.Id);
        Check("actual_result_failure_replaces_generic_failure_notice", failed.Task == executed.ResultTask && failed.Body.Contains("原因还需要核对报告")
            && !saved.Events.Any(e => e.Task == executed.ResultTask && e.ExternalEventKey?.StartsWith("owned-event/") == true && e.Body.Contains("失败")));
        await Observe("failed"); await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("repeated_result_failure_does_not_send_or_execute_again", f.Sent.Count(s => s == failed.Body) == 1
            && rpc!.GuardedTexts.Count(t => t == "执行后失败不重做") == 1);

        await Start("start-expiry"); await Observe("inProgress");
        var expired = await Add("expired", "到期后不执行的安排");
        var cancelled = await Add("cancelled", "取消后不得补发的到期安排");
        string deadline = clock.UtcNow.AddMinutes(2).ToString("yyyy-MM-dd HH:mm:ss zzz");
        from = clock.UtcNow.ToLocalTime().AddHours(-1).ToString("HH:mm"); until = clock.UtcNow.ToLocalTime().AddHours(1).ToString("HH:mm");
        foreach (var w in new[] { expired, cancelled })
        {
            await Input("deadline-" + w.Id, $"修改依赖 {w.Id:D} 截止：{deadline}");
            await Input("quiet-" + w.Id, $"修改依赖 {w.Id:D} 安静时段：{from}-{until}");
        }
        f.Settings.ChatGpt.TaskControlEnabled = false; clock.Advance(TimeSpan.FromMinutes(3));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        var expiredNotice = await Notice(expired.Id); var cancelledNotice = await Notice(cancelled.Id);
        Check("expiry_is_recorded_while_task_control_is_disabled", (await Work(expired.Id)).Stage == DependentWorkStage.Expired
            && expiredNotice.Body.Contains("等待期限已过") && !await Delivered(expiredNotice.Id));
        await Input("cancel-expired", "取消依赖 " + cancelled.Id.ToString("D"));
        Check("cancelled_expiry_retires_all_unsent_pages", (await f.ReadAsync()).Outbox.Where(o => o.NotificationEventId == cancelledNotice.Id).All(o => o.Stage == OutboxStage.Superseded));
        clock.Advance(TimeSpan.FromHours(2)); await f.Runtime.AdvanceDependenciesAsync(identity); await Wait(() => Delivered(expiredNotice.Id));
        Check("expiry_notification_does_not_restart_work_or_revive_cancelled_notice", f.Sent.Count(s => s == expiredNotice.Body) == 1
            && !f.Sent.Contains(cancelledNotice.Body) && !rpc!.GuardedTexts.Any(t => t.Contains("到期")));
        await f.Runtime.AdvanceDependenciesAsync(identity);
        Check("one_notification_reservation_per_delivered_dependency_event", (await f.ReadAsync()).CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 3);
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Dependency notification condition not reached."); await Task.Delay(20); }
    }
}
