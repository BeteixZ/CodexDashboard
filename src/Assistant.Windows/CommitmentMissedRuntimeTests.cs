using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

// Real model-off runtime, scheduler, coordinator, DPAPI file reopen and HTTP
// serialization. Every channel/control/model transport remains synthetic.
internal static class CommitmentMissedRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-missed-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset value)
        {
            if (value < UtcNow) throw new InvalidOperationException("clock_must_be_monotonic");
            Interlocked.Exchange(ref ticks, value.UtcTicks);
        }
    }
    private sealed class NoControl : ITaskTransport
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("missed_reminder_must_not_control_tasks");
        }
    }
    private sealed record Seed(ClawBinding Binding, TrackedWorkItem Work, ReminderSchedule Schedule, int Operations);
    private sealed class UnknownProbe
    {
        internal readonly ConcurrentQueue<string> Wire = new();
        internal bool Unknown = true;
        internal async Task<HttpResponseMessage?> InspectAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string body = document.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                .GetProperty("text_item").GetProperty("text").GetString()!;
            if (!body.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal)) return null;
            Wire.Enqueue(body);
            return Unknown ? new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") } : null;
        }
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("new_absolute_wpf_output_required");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool success)
        {
            checks.Add(name, success);
            File.WriteAllText(Path.Combine(directory, "commitment-missed-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, failures = checks.Count(x => !x.Value), actualHost = true,
                actualModelUsed = false, actualNetworkRequests = 0, actualCodexProcesses = 0,
                productionDataRead = false, checks
            }, Settings.Json));
            if (!success) throw new InvalidOperationException(name);
        }
        await DecisionRestartAndRebuildAsync(Path.Combine(directory, "decision-restart"), Check);
        await UnknownRestartAsync(Path.Combine(directory, "unknown-restart"), Check);
        foreach (var policy in new[] { MissedTriggerPolicy.NotifyOnce, MissedTriggerPolicy.Skip, MissedTriggerPolicy.NeedsDecision })
            foreach (bool pastGrace in new[] { false, true })
                await GraceBoundaryAsync(Path.Combine(directory, "grace-" + policy + "-" + pastGrace), policy, pastGrace, Check);
        await QuietDecisionAsync(Path.Combine(directory, "quiet-decision"), Check);
        foreach (var policy in new[] { MissedTriggerPolicy.NotifyOnce, MissedTriggerPolicy.Skip, MissedTriggerPolicy.NeedsDecision })
            await PauseMissedResumeAsync(Path.Combine(directory, "paused-missed-" + policy), policy, Check);
    }

    private static async Task<Seed> SeedAsync(string path, Clock clock, NoControl control,
        MissedTriggerPolicy policy, string goal, bool quiet = false)
    {
        await using var fixture = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control);
        await InputAsync(fixture, "missed-create", "一分钟后提醒我" + goal);
        var created = await fixture.ReadAsync(); var original = created.Schedules.Single();
        string rule = policy switch
        {
            MissedTriggerPolicy.Skip => "错过时跳过",
            MissedTriggerPolicy.NeedsDecision => "需要我决定", _ => "恢复后提醒一次"
        };
        var work = created.WorkItems.Single();
        await InputAsync(fixture, "missed-policy", $"设置提醒 {work.Id} {rule}");
        if (quiet)
        {
            var local = TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.Local);
            string start = local.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
            string end = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture);
            await InputAsync(fixture, "missed-quiet", $"设置事项 {work.Id} 安静时段 {start}-{end}");
        }
        var saved = await fixture.ReadAsync(); var schedule = saved.Schedules.Single();
        if (saved.WorkItems.Single().Goal != goal || saved.WorkItems.Single().Stage != TrackedWorkStage.Active
            || schedule.MissedPolicy != policy || schedule.DueAtUtc != original.DueAtUtc || schedule.FiredRunId != null
            || !saved.CommitmentRuns.IsEmpty || !NoEffects(fixture, control, saved))
            throw new InvalidOperationException("current_model_off_commands_must_save_untriggered_exact_policy");
        return new(fixture.Settings.WeChat.ReadBinding()!, saved.WorkItems.Single(), schedule, saved.Operations.Length);
    }

    private static async Task DecisionRestartAndRebuildAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        var seed = await SeedAsync(path, clock, control, MissedTriggerPolicy.NeedsDecision, "核对错过后需决定的合成结果");
        clock.Set(seed.Schedule.DueAtUtc.AddSeconds(61));
        ClawBinding binding; Guid originalRun; Guid originalNotice;
        await using (var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: seed.Binding))
        {
            await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
            var saved = await resumed.ReadAsync(); var run = saved.CommitmentRuns.Single();
            var notice = saved.Outbox.Single(o => o.Kind == OutboxKind.Notification);
            originalRun = run.Id; originalNotice = notice.Id;
            check("needs_decision_real_file_reopen_keeps_exact_schedule_and_single_run",
                saved.WorkItems.Single().Id == seed.Work.Id && saved.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision
                && saved.Schedules.Single() == seed.Schedule with { FiredRunId = run.Id }
                && run.WorkItemId == seed.Work.Id && run.Stage == CommitmentRunStage.Completed
                && notice.Stage == OutboxStage.Accepted && notice.SendAttempts == 1);
            check("decision_notice_is_exact_factual_advice_not_authority_or_automatic_rerun",
                notice.Body == saved.NotificationDrafts.Single().ContextBody && notice.Body == run.Body
                && notice.Body.Contains("取消事项 " + seed.Work.Id.ToString("N"), StringComparison.Ordinal)
                && notice.Body.Contains("再明确新时间建立提醒", StringComparison.Ordinal)
                && notice.Body.Contains("已触发的原事项不能直接改期", StringComparison.Ordinal)
                && saved.Events.Single(e => e.Id == notice.NotificationEventId).RequiredDecision == null
                && resumed.Sent.Count(text => text == notice.Body) == 1
                && saved.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
                && saved.Operations.Length == seed.Operations && NoEffects(resumed, control, saved));
            binding = resumed.Settings.WeChat.ReadBinding()!;
        }
        await using var again = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: binding);
        await InputAsync(again, "decision-restart-list", "提醒列表"); await Task.Delay(1200);
        var before = await again.ReadAsync();
        check("accepted_decision_notice_second_restart_is_silent_and_work_still_needs_decision",
            before.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision && before.CommitmentRuns.Single().Id == originalRun
            && before.Outbox.Single(o => o.Kind == OutboxKind.Notification).Id == originalNotice
            && before.Outbox.Single(o => o.Id == originalNotice).SendAttempts == 1
            && before.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && !again.Sent.Any(text => text.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal))
            && NoEffects(again, control, before));
        await InputAsync(again, "decision-illegal-reschedule", $"修改提醒 {seed.Work.Id} 一分钟后提醒我改写旧事项");
        var rejected = await again.ReadAsync();
        check("triggered_decision_reminder_cannot_be_implicitly_rescheduled",
            rejected.Schedules.Single() == before.Schedules.Single() && rejected.WorkItems.Single() == before.WorkItems.Single()
            && rejected.Operations.Length == before.Operations.Length && rejected.CommitmentRuns.Single() == before.CommitmentRuns.Single()
            && again.Sent.Last().Contains("只可修改尚未触发", StringComparison.Ordinal));
        await InputAsync(again, "decision-cancel-original", $"取消事项 {seed.Work.Id}");
        await InputAsync(again, "decision-create-replacement", "一分钟后提醒我核对新建事项合成结果");
        var replacement = await again.ReadAsync(); var newer = replacement.WorkItems.Single(w => w.Id != seed.Work.Id);
        var newSchedule = replacement.Schedules.Single(s => s.WorkItemId == newer.Id);
        check("explicit_cancel_then_new_request_creates_new_identity_without_rewriting_history",
            replacement.WorkItems.Single(w => w.Id == seed.Work.Id).Stage == TrackedWorkStage.Cancelled
            && replacement.Schedules.Single(s => s.WorkItemId == seed.Work.Id) == before.Schedules.Single()
            && newer.Stage == TrackedWorkStage.Active && newer.SourceMessageId == WeChatText.Hash("decision-create-replacement")
            && newSchedule.FiredRunId == null && replacement.CommitmentRuns.Single().Id == originalRun
            && replacement.CommitmentRuns.Single().Stage == CommitmentRunStage.Completed
            && replacement.Outbox.Single(o => o.Id == originalNotice).Stage == OutboxStage.Accepted
            && replacement.Operations.Length == seed.Operations + 2);
        clock.Set(newSchedule.DueAtUtc.AddSeconds(1));
        await WaitAsync(async () => (await again.ReadAsync()).WorkItems.Single(w => w.Id == newer.Id).Stage == TrackedWorkStage.Completed);
        var completed = await again.ReadAsync();
        check("only_new_schedule_fires_after_current_cancel_and_rebuild",
            completed.CommitmentRuns.Length == 2 && completed.CommitmentRuns.Single(r => r.WorkItemId == seed.Work.Id).Id == originalRun
            && completed.Schedules.Single(s => s.WorkItemId == newer.Id).FiredRunId == completed.CommitmentRuns.Single(r => r.WorkItemId == newer.Id).Id
            && again.Sent.Count(text => text == newer.Goal) == 1
            && !again.Sent.Any(text => text.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal))
            && completed.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 2
            && NoEffects(again, control, completed));
        Save(path, completed, again.Sent.ToArray());
    }

    private static async Task UnknownRestartAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl(); var probe = new UnknownProbe();
        var seed = await SeedAsync(path, clock, control, MissedTriggerPolicy.NeedsDecision, "核对结果未知的错过提醒");
        clock.Set(seed.Schedule.DueAtUtc.AddSeconds(61));
        ClawBinding binding; OutboxMessage unknown; Guid runId;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock,
            taskTransport: control, savedBinding: seed.Binding, channelRequestOverride: probe.InspectAsync))
        {
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Unknown));
            var state = await f.ReadAsync(); unknown = state.Outbox.Single(o => o.Kind == OutboxKind.Notification); runId = state.CommitmentRuns.Single().Id;
            check("missed_decision_unknown_receipt_keeps_one_pending_run_and_spent_budget",
                state.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision
                && state.CommitmentRuns.Single().Stage == CommitmentRunStage.NotificationPending
                && state.CommitmentRuns.Single().DeliveryAttempts.Length == 1 && unknown.SendAttempts == 1
                && state.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
                && probe.Wire.Count == 1 && probe.Wire.Single() == unknown.Body && NoEffects(f, control, state));
            binding = f.Settings.WeChat.ReadBinding()!;
        }
        probe.Unknown = false;
        await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock,
            taskTransport: control, savedBinding: binding, channelRequestOverride: probe.InspectAsync);
        await InputAsync(resumed, "unknown-missed-current-input", "提醒列表");
        clock.Set(clock.UtcNow.AddDays(1)); await Task.Delay(1200);
        var restored = await resumed.ReadAsync();
        check("unknown_decision_file_restart_and_fresh_context_never_replay_or_rebatch",
            restored.Outbox.Single(o => o.Id == unknown.Id) == unknown && restored.CommitmentRuns.Single().Id == runId
            && restored.Schedules.Single().FiredRunId == runId && restored.CatchUpBatches.IsEmpty
            && restored.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
            && restored.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && probe.Wire.Count == 1 && !resumed.Sent.Contains(unknown.Body)
            && restored.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision && NoEffects(resumed, control, restored));
        Save(path, restored, probe.Wire.Concat(resumed.Sent).ToArray());
    }

    private static async Task GraceBoundaryAsync(string path, MissedTriggerPolicy policy, bool pastGrace, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        var seed = await SeedAsync(path, clock, control, policy, "核对严格宽限边界合成结果");
        clock.Set(seed.Schedule.DueAtUtc.AddSeconds(60).AddTicks(pastGrace ? 1 : 0));
        await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: seed.Binding);
        bool skip = pastGrace && policy == MissedTriggerPolicy.Skip;
        bool decision = pastGrace && policy == MissedTriggerPolicy.NeedsDecision;
        await WaitAsync(async () =>
        {
            var snapshot = await resumed.ReadAsync();
            return skip ? snapshot.CommitmentRuns.Any(r => r.Stage == CommitmentRunStage.Skipped)
                : snapshot.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted);
        });
        await Task.Delay(1100);
        var saved = await resumed.ReadAsync(); var run = saved.CommitmentRuns.Single();
        var notice = saved.Outbox.SingleOrDefault(o => o.Kind == OutboxKind.Notification);
        var expectedWork = skip ? TrackedWorkStage.Expired : decision ? TrackedWorkStage.NeedsDecision : TrackedWorkStage.Completed;
        check("exact_grace_" + (pastGrace ? "sixty_seconds_plus_one_tick_" : "sixty_seconds_") + policy,
            saved.WorkItems.Single().Stage == expectedWork && saved.Schedules.Single() == seed.Schedule with { FiredRunId = run.Id }
            && run.Stage == (skip ? CommitmentRunStage.Skipped : CommitmentRunStage.Completed)
            && run.DeliveryAttempts.Length == (skip ? 0 : 1)
            && (skip ? notice == null && saved.NotificationDrafts.IsEmpty && resumed.Sent.IsEmpty
                : notice?.Stage == OutboxStage.Accepted && notice.SendAttempts == 1 && resumed.Sent.Count(text => text == notice.Body) == 1
                    && (decision ? notice.Body.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal) : notice.Body == seed.Work.Goal))
            && saved.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == (skip ? 0 : 1)
            && saved.Operations.Length == seed.Operations && NoEffects(resumed, control, saved));
        Save(path, saved, resumed.Sent.ToArray());
    }

    private static async Task QuietDecisionAsync(string path, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        var seed = await SeedAsync(path, clock, control, MissedTriggerPolicy.NeedsDecision, "核对安静时段错过提醒", quiet: true);
        clock.Set(seed.Schedule.DueAtUtc.AddSeconds(61));
        await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: seed.Binding);
        await WaitAsync(async () => (await resumed.ReadAsync()).CommitmentRuns.Any(r => r.Stage == CommitmentRunStage.NotificationPending));
        await Task.Delay(1200); var held = await resumed.ReadAsync();
        check("missed_decision_is_ordinary_notice_and_cannot_bypass_quiet_hours",
            held.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision && held.CommitmentRuns.Single().DeliveryAttempts.IsEmpty
            && held.NotificationDrafts.Single().Stage == NotificationDraftStage.Captured
            && !held.Outbox.Any(o => o.Kind == OutboxKind.Notification) && resumed.Sent.IsEmpty
            && !held.CommitmentBudgetReservations.Any(r => r.Kind == CommitmentBudgetKind.Notification)
            && held.Events.Single(e => e.Id == held.NotificationDrafts.Single().EventId).RequiredDecision == null);
        clock.Set(clock.UtcNow.AddHours(4));
        await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
        await Task.Delay(1100); var sent = await resumed.ReadAsync(); var notice = sent.Outbox.Single(o => o.Kind == OutboxKind.Notification);
        check("after_quiet_same_missed_run_notifies_once_without_model_or_control",
            sent.CommitmentRuns.Single().Id == held.CommitmentRuns.Single().Id
            && sent.CommitmentRuns.Single().Stage == CommitmentRunStage.Completed
            && sent.WorkItems.Single().Stage == TrackedWorkStage.NeedsDecision
            && sent.Schedules.Single().FiredRunId == held.CommitmentRuns.Single().Id
            && sent.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && resumed.Sent.Count(text => text == notice.Body) == 1 && NoEffects(resumed, control, sent));
        Save(path, sent, resumed.Sent.ToArray());
    }

    private static async Task PauseMissedResumeAsync(string path, MissedTriggerPolicy policy, Action<string, bool> check)
    {
        var clock = new Clock(); var control = new NoControl();
        var seed = await SeedAsync(path, clock, control, policy, "核对暂停后恢复原安排的合成结果");
        ClawBinding binding; TrackedWorkItem pausedWork;
        await using (var beforeDue = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: seed.Binding))
        {
            await InputAsync(beforeDue, "pause-before-due", $"暂停提醒 {seed.Work.Id}");
            var paused = await beforeDue.ReadAsync(); pausedWork = paused.WorkItems.Single();
            if (pausedWork.Stage != TrackedWorkStage.Paused || pausedWork.Version != seed.Work.Version + 1
                || paused.Schedules.Single() != seed.Schedule || !paused.CommitmentRuns.IsEmpty
                || paused.Operations.Length != seed.Operations + 1 || !NoEffects(beforeDue, control, paused))
                throw new InvalidOperationException("current_pause_must_preserve_untriggered_original_schedule");
            binding = beforeDue.Settings.WeChat.ReadBinding()!;
        }
        clock.Set(seed.Schedule.DueAtUtc.AddSeconds(61));
        await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
            clock: clock, taskTransport: control, savedBinding: binding);
        await Task.Delay(1200);
        var held = await resumed.ReadAsync();
        check("paused_expired_schedule_file_reopen_is_silent_" + policy,
            held.WorkItems.Single() == pausedWork && held.Schedules.Single() == seed.Schedule
            && held.CommitmentRuns.IsEmpty && held.NotificationDrafts.IsEmpty
            && !held.Outbox.Any(o => o.Kind == OutboxKind.Notification) && resumed.Sent.IsEmpty
            && !held.CommitmentBudgetReservations.Any(r => r.Kind == CommitmentBudgetKind.Notification)
            && held.Operations.Length == seed.Operations + 1 && NoEffects(resumed, control, held));

        await InputAsync(resumed, "resume-after-missed", $"恢复提醒 {seed.Work.Id}");
        bool skip = policy == MissedTriggerPolicy.Skip;
        bool decision = policy == MissedTriggerPolicy.NeedsDecision;
        await WaitAsync(async () =>
        {
            var state = await resumed.ReadAsync();
            return skip ? state.CommitmentRuns.Any(r => r.Stage == CommitmentRunStage.Skipped)
                : state.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted);
        });
        await Task.Delay(1100);
        var saved = await resumed.ReadAsync(); var work = saved.WorkItems.Single(); var run = saved.CommitmentRuns.Single();
        var notice = saved.Outbox.SingleOrDefault(o => o.Kind == OutboxKind.Notification);
        var expectedStage = skip ? TrackedWorkStage.Expired : decision ? TrackedWorkStage.NeedsDecision : TrackedWorkStage.Completed;
        var resumeOperation = saved.Operations.Single(o => o.SourceMessageId == WeChatText.Hash("resume-after-missed"));
        check("explicit_resume_applies_original_missed_policy_without_reschedule_or_control_" + policy,
            work.Id == seed.Work.Id && work.Stage == expectedStage && work.Version == pausedWork.Version + 1
            && work.Goal == seed.Work.Goal && work.SourceMessageId == seed.Work.SourceMessageId
            && work.CreationOperationId == seed.Work.CreationOperationId
            && saved.Schedules.Single() == seed.Schedule with { FiredRunId = run.Id }
            && run.WorkItemId == work.Id && run.WorkVersion == work.Version
            && run.Stage == (skip ? CommitmentRunStage.Skipped : CommitmentRunStage.Completed)
            && run.DeliveryAttempts.Length == (skip ? 0 : 1)
            && (skip ? notice == null && saved.NotificationDrafts.IsEmpty && !resumed.Sent.Contains(seed.Work.Goal)
                    && !resumed.Sent.Any(text => text.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal))
                : notice?.Stage == OutboxStage.Accepted && notice.SendAttempts == 1
                    && notice.Body == run.Body && notice.Body == saved.NotificationDrafts.Single().ContextBody
                    && resumed.Sent.Count(text => text == notice.Body) == 1
                    && (decision ? notice.Body.StartsWith("这条提醒已错过原定时间：", StringComparison.Ordinal)
                            && notice.Body.Contains("取消事项 " + seed.Work.Id.ToString("N"), StringComparison.Ordinal)
                        : notice.Body == seed.Work.Goal))
            && saved.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == (skip ? 0 : 1)
            && saved.Operations.Length == seed.Operations + 2 && resumeOperation.Action == "change_commitment"
            && resumeOperation.Stage == OperationStage.Completed && NoEffects(resumed, control, saved));
        Save(path, saved, resumed.Sent.ToArray());
    }

    private static bool NoEffects(DurableRuntimeTests.Fixture fixture, NoControl control, ConversationSnapshot snapshot) =>
        fixture.Script.Calls == 0 && control.Calls == 0 && fixture.Queue.Entries.Count == 0
        && !fixture.Settings.ChatGpt.TaskControlEnabled && fixture.Runtime.LastError == null
        && snapshot.Operations.All(o => o.Action is "schedule" or "change_commitment");
    private static void Save(string path, ConversationSnapshot snapshot, string[] wire) =>
        File.WriteAllText(Path.Combine(path, "synthetic-missed-observation.json"), JsonSerializer.Serialize(new
        {
            syntheticOnly = true, snapshot.WorkItems, snapshot.Schedules, snapshot.CommitmentRuns,
            snapshot.NotificationDrafts, outbox = snapshot.Outbox.Where(o => o.Kind == OutboxKind.Notification),
            snapshot.CommitmentBudgetReservations, snapshot.CatchUpBatches, wire
        }, Settings.Json));
    private static async Task InputAsync(DurableRuntimeTests.Fixture fixture, string source, string text)
    {
        int accepted = (await fixture.ReadAsync()).Outbox.Count(o => o.Kind != OutboxKind.Notification && o.Stage == OutboxStage.Accepted);
        await fixture.InputAsync(source, text);
        await WaitAsync(async () => (await fixture.ReadAsync()).Outbox.Count(o => o.Kind != OutboxKind.Notification && o.Stage == OutboxStage.Accepted) > accepted);
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("synthetic_missed_runtime_condition");
            await Task.Delay(20);
        }
    }
}
