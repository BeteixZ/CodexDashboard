using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Real model-off host, coordinator, scheduler and durable channel path. The
// reused fixture replaces every HTTP request/model/task transport and keeps
// all protected state in a new synthetic output directory.
internal static class CommitmentPolicyRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-policy-runtime-test");
        if (index < 0) return false;
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }

    private sealed class NoControl : ITaskTransport
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("policy_runtime_must_not_control_tasks");
        }
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "commitment-policy-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, failures = checks.Count(item => !item.Value), actualHost = true,
                networkUsed = false, productionDataRead = false, actualModelUsed = false, checks
            }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }

        {
            var clock = new Clock();
            var control = new NoControl();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "quiet"),
                modelEnabled: false, clock: clock, taskTransport: control);
            await InputAsync(f, "quiet-create", "一分钟后提醒我查看安静时段合成结果");
            var initial = await f.ReadAsync();
            var original = initial.WorkItems.Single();
            var originalSchedule = initial.Schedules.Single();
            var (start, end) = QuietAround(clock.UtcNow);
            await InputAsync(f, "quiet-set", $"设置提醒 {original.Id} 安静时段 {start}-{end}");
            await InputAsync(f, "frequency-set", $"设置事项 {original.Id} 每24小时最多3次");
            var saved = await f.ReadAsync();
            var policy = saved.WorkItems.Single().Policy;
            Check("model_off_full_id_policy_commands_update_original_authorized_record",
                saved.WorkItems.Single().Id == original.Id && saved.WorkItems.Single().Version == original.Version + 2
                && policy.QuietStart == start && policy.QuietEnd == end && policy.TimeZoneId == TimeZoneInfo.Local.Id
                && policy.MaximumNotificationsPer24Hours == 3 && saved.Schedules.Single() == originalSchedule
                && saved.Operations.Length == 3 && saved.Operations.All(item => item.Stage == OperationStage.Completed)
                && saved.ToolInvocations.Length == 3 && saved.ToolInvocations.All(item => item.Stage == ToolInvocationStage.Completed)
                && f.Script.Calls == 0 && control.Calls == 0);

            await InputAsync(f, "quiet-list", "提醒列表");
            var view = await f.Runtime.ReadWorkspaceAsync();
            string summary = CommitmentToolAdapter.PolicySummary(saved.WorkItems.Single(), saved.Schedules.Single());
            Check("actual_wechat_list_and_desktop_view_share_saved_policy_summary",
                f.Sent.Last().Contains(summary, StringComparison.Ordinal)
                && f.Sent.Last().Contains("提醒与订阅通知还受共享预算限制：滚动24小时最多20次", StringComparison.Ordinal)
                && view.Commitments?.Single().Id == original.Id.ToString("N")
                && view.Commitments?.Single().Target.Contains(summary, StringComparison.Ordinal) == true
                && (await f.ReadAsync()).Operations.Length == 3);

            clock.Advance(TimeSpan.FromSeconds(62));
            await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Any(item => item.Stage == CommitmentRunStage.NotificationPending));
            await Task.Delay(1200); // At least one further host scheduler pass at the same fake time.
            saved = await f.ReadAsync();
            var run = saved.CommitmentRuns.Single();
            Check("due_inside_quiet_hours_keeps_pending_run_without_send_attempt_or_quota",
                saved.WorkItems.Single().Stage == TrackedWorkStage.Triggered
                && saved.Schedules.Single().FiredRunId == run.Id && run.DeliveryAttempts.IsDefaultOrEmpty
                && saved.NotificationDrafts.Single().Stage == NotificationDraftStage.Captured
                && saved.NotificationDrafts.Single().PreparationAttempts == 0
                && !saved.Outbox.Any(item => item.Kind == OutboxKind.Notification)
                && !saved.CommitmentBudgetReservations.Any(item => item.Kind == CommitmentBudgetKind.Notification)
                && !f.Sent.Contains(original.Goal) && f.Runtime.LastError == null);

            await InputAsync(f, "quiet-clear", $"清除事项 {original.Id} 安静时段");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(item => item.Kind == OutboxKind.Notification && item.Stage == OutboxStage.Accepted));
            saved = await f.ReadAsync();
            var notice = saved.Outbox.Single(item => item.Kind == OutboxKind.Notification);
            Check("new_current_clear_command_releases_original_reminder_exactly_once",
                saved.WorkItems.Single().Id == original.Id && saved.WorkItems.Single().Stage == TrackedWorkStage.Completed
                && saved.WorkItems.Single().Policy == policy with { QuietStart = null, QuietEnd = null, TimeZoneId = null }
                && saved.Schedules.Single().DueAtUtc == originalSchedule.DueAtUtc
                && saved.CommitmentRuns.Single().Id == run.Id && saved.CommitmentRuns.Single().DeliveryAttempts.Length == 1
                && notice.Stage == OutboxStage.Accepted && notice.SendAttempts == 1
                && saved.CommitmentBudgetReservations.Count(item => item.Kind == CommitmentBudgetKind.Notification) == 1
                && f.Sent.Count(body => body == original.Goal) == 1 && saved.Operations.Length == 4);

            clock.Advance(TimeSpan.FromDays(1));
            await InputAsync(f, "quiet-finished-list", "事项列表");
            await Task.Delay(1200);
            saved = await f.ReadAsync();
            Check("later_runtime_tick_does_not_repeat_released_reminder",
                saved.CommitmentRuns.Length == 1 && saved.Outbox.Single(item => item.Kind == OutboxKind.Notification).Id == notice.Id
                && saved.Outbox.Single(item => item.Kind == OutboxKind.Notification).SendAttempts == 1
                && f.Sent.Count(body => body == original.Goal) == 1 && f.Script.Calls == 0 && control.Calls == 0
                && f.Runtime.LastError == null);
        }

        {
            var clock = new Clock();
            var control = new NoControl();
            string path = Path.Combine(directory, "missed-restart");
            ClawBinding binding;
            Guid workId;
            ReminderSchedule originalSchedule;
            string goal;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock, taskTransport: control))
            {
                await InputAsync(f, "skip-create", "一分钟后提醒我查看重启跳过合成结果");
                var saved = await f.ReadAsync();
                workId = saved.WorkItems.Single().Id;
                goal = saved.WorkItems.Single().Goal;
                originalSchedule = saved.Schedules.Single();
                await InputAsync(f, "skip-set", $"设置提醒 {workId} 错过时跳过");
                saved = await f.ReadAsync();
                Check("untriggered_reminder_persists_skip_through_current_model_off_command",
                    saved.WorkItems.Single().Id == workId && saved.WorkItems.Single().Stage == TrackedWorkStage.Active
                    && saved.Schedules.Single().MissedPolicy == MissedTriggerPolicy.Skip
                    && saved.Schedules.Single().DueAtUtc == originalSchedule.DueAtUtc
                    && saved.Schedules.Single().FiredRunId == null && saved.CommitmentRuns.IsEmpty
                    && saved.Operations.Length == 2 && saved.Operations.All(item => item.Stage == OperationStage.Completed)
                    && f.Script.Calls == 0 && control.Calls == 0);
                binding = f.Settings.WeChat.ReadBinding()!;
            }
            clock.Advance(TimeSpan.FromMinutes(5)); // More than the persisted due time plus the one-minute grace.
            await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
                savedBinding: binding, clock: clock, taskTransport: control);
            await WaitAsync(async () => (await resumed.ReadAsync()).CommitmentRuns.Any(item => item.Stage == CommitmentRunStage.Skipped));
            var reopened = await resumed.ReadAsync();
            Check("file_restart_after_grace_marks_original_skip_record_expired",
                reopened.WorkItems.Single().Id == workId && reopened.WorkItems.Single().Stage == TrackedWorkStage.Expired
                && reopened.Schedules.Single().DueAtUtc == originalSchedule.DueAtUtc
                && reopened.Schedules.Single().MissedPolicy == MissedTriggerPolicy.Skip
                && reopened.Schedules.Single().FiredRunId == reopened.CommitmentRuns.Single().Id
                && reopened.CommitmentRuns.Single().Stage == CommitmentRunStage.Skipped);
            await InputAsync(resumed, "skip-list", "提醒列表");
            await Task.Delay(1200);
            reopened = await resumed.ReadAsync();
            Check("skipped_restart_never_creates_notification_or_spends_delivery_budget",
                reopened.CommitmentRuns.Length == 1 && reopened.CommitmentRuns.Single().DeliveryAttempts.IsDefaultOrEmpty
                && reopened.NotificationDrafts.IsEmpty && !reopened.Outbox.Any(item => item.Kind == OutboxKind.Notification)
                && !reopened.CommitmentBudgetReservations.Any(item => item.Kind == CommitmentBudgetKind.Notification)
                && !resumed.Sent.Contains(goal) && resumed.Script.Calls == 0 && control.Calls == 0
                && resumed.Runtime.LastError == null);
        }

        {
            var clock = new Clock();
            var control = new NoControl();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "delete-pending"),
                modelEnabled: false, clock: clock, taskTransport: control);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
            f.ObservedTask.LastActivity = clock.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            string turn = f.ObservedTask.TurnId;
            await InputAsync(f, "delete-create", "一分钟后提醒我查看删除安排合成结果");
            var saved = await f.ReadAsync();
            var original = saved.WorkItems.Single();
            var schedule = saved.Schedules.Single();
            var (start, end) = QuietAround(clock.UtcNow);
            await InputAsync(f, "delete-quiet", $"设置事项 {original.Id} 安静时段 {start}-{end}");
            clock.Advance(TimeSpan.FromSeconds(62));
            await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Any(item => item.Stage == CommitmentRunStage.NotificationPending));
            var pending = await f.ReadAsync();
            Check("delete_fixture_contains_actual_due_notification_waiting_for_quiet_hours",
                pending.WorkItems.Single().Stage == TrackedWorkStage.Triggered
                && pending.NotificationDrafts.Single().Stage == NotificationDraftStage.Captured
                && !pending.Outbox.Any(item => item.Kind == OutboxKind.Notification)
                && pending.CommitmentRuns.Single().DeliveryAttempts.IsDefaultOrEmpty);

            await InputAsync(f, "delete-alias", $"删除提醒 {original.Id}");
            saved = await f.ReadAsync();
            Check("delete_alias_cancels_pending_arrangement_but_preserves_audit",
                saved.WorkItems.Single().Id == original.Id && saved.WorkItems.Single().Stage == TrackedWorkStage.Cancelled
                && saved.Schedules.Single().DueAtUtc == schedule.DueAtUtc
                && saved.CommitmentRuns.Single().Id == pending.CommitmentRuns.Single().Id
                && saved.CommitmentRuns.Single().Stage == CommitmentRunStage.Cancelled
                && saved.NotificationDrafts.Single().Cancelled && saved.Operations.Length == 3
                && saved.Operations.All(item => item.Stage == OperationStage.Completed)
                && f.Sent.Last().Contains("历史", StringComparison.Ordinal)
                && f.Sent.Last().Contains("任务", StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromHours(4)); // Outside the former quiet interval.
            await InputAsync(f, "deleted-list", "事项列表");
            await Task.Delay(1200);
            saved = await f.ReadAsync();
            Check("deleted_arrangement_stays_unsent_after_quiet_hours_with_no_budget_charge",
                saved.CommitmentRuns.Length == 1 && saved.CommitmentRuns.Single().Stage == CommitmentRunStage.Cancelled
                && saved.CommitmentRuns.Single().DeliveryAttempts.IsDefaultOrEmpty
                && !saved.Outbox.Any(item => item.Kind == OutboxKind.Notification)
                && !saved.CommitmentBudgetReservations.Any(item => item.Kind == CommitmentBudgetKind.Notification)
                && !f.Sent.Contains(original.Goal));
            Check("arrangement_deletion_never_interrupts_running_task_or_calls_model",
                f.ObservedTask.Status == TaskStatusNames.Active && f.ObservedTask.RecordedStatus == TaskStatusNames.Active
                && f.ObservedTask.TurnId == turn && f.Queue.Entries.Count == 0 && control.Calls == 0 && f.Script.Calls == 0
                && saved.Operations.All(item => item.Action is "schedule" or "change_commitment")
                && f.Runtime.LastError == null);
        }
    }

    private static (string Start, string End) QuietAround(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        return (local.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture),
            local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private static async Task InputAsync(DurableRuntimeTests.Fixture fixture, string id, string body)
    {
        int replies = (await fixture.ReadAsync()).Outbox.Count(item => item.Kind != OutboxKind.Notification && item.Stage == OutboxStage.Accepted);
        await fixture.InputAsync(id, body);
        await WaitAsync(async () => (await fixture.ReadAsync()).Outbox.Count(item => item.Kind != OutboxKind.Notification
            && item.Stage == OutboxStage.Accepted) > replies);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("synthetic_commitment_policy_runtime_condition");
            await Task.Delay(20);
        }
    }
}
