using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Actual host/coordinator/channel with synthetic HTTP, controlled clock and
// protected isolated files. No real reminder, account or task is contacted.
internal static class CommitmentRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-runtime-test");
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
        internal void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
    }
    private static async Task PagingAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false);
        for (int i = 0; i < 10; i++) await InputAsync(f, "page-create-" + i, "一小时后提醒我核对分页事项" + i);
        var saved = await f.ReadAsync(); var ids = saved.WorkItems.Select(w => w.Id.ToString("D")).ToArray();
        async Task<string> Reply(string source)
        {
            await ConversationHistoryRuntimeTests.Settled(f, source);
            var state = await f.ReadAsync(); var generation = state.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash(source)).GenerationId;
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Where(o => o.GenerationId == generation).All(o => o.Stage == OutboxStage.Accepted));
            return string.Concat(state.Outbox.Where(o => o.GenerationId == generation).OrderBy(o => o.SegmentIndex).Select(o => o.Body));
        }
        await InputAsync(f, "page-one", "事项列表"); string first = await Reply("page-one");
        await InputAsync(f, "page-two", "事项列表 2"); string second = await Reply("page-two");
        File.WriteAllText(Path.Combine(directory, "paging-diagnostics.json"), JsonSerializer.Serialize(new { ids, first, second, f.Script.Calls }));
        check("fixed_commitment_pages_cover_saved_items_without_model", ids.Length == 10 && first.Contains("下一页：事项列表 2")
            && second.Contains("第 2/2 页") && ids.All(id => first.Contains(id) || second.Contains(id)) && f.Script.Calls == 0
            && DurableAssistantRuntime.IsKnownReadonlyInput("事项列表 2") && !DurableAssistantRuntime.IsKnownReadonlyInput("事项列表 2；取消所有"));
        f.Settings.ChatGpt.Enabled = true; var replies = new List<string>(); int calls = 0;
        f.Script.Handler = (request, ct) =>
        {
            int page = 1;
            if (!request.Observations.IsEmpty)
            {
                using var result = JsonDocument.Parse(request.Observations.Last().ResultJson);
                var facts = result.RootElement.GetProperty("facts"); replies.Add(facts.GetProperty("text").GetString()!);
                if (facts.GetProperty("nextPage").ValueKind == JsonValueKind.Null) return Task.FromResult(ToolModelResponse.Reply("事项已查全。"));
                page = facts.GetProperty("nextPage").GetInt32();
            }
            return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("page-" + ++calls, "list_commitments", JsonSerializer.Serialize(new { page }))));
        };
        await InputAsync(f, "model-pages", "请查所有提醒事项");
        await ConversationHistoryRuntimeTests.Settled(f, "model-pages");
        File.WriteAllText(Path.Combine(directory, "model-page-diagnostics.json"), JsonSerializer.Serialize(new { toolCalls = calls, replies, modelCalls = f.Script.Calls, f.Runtime.LastError, reply = f.Sent.Last(), operations = (await f.ReadAsync()).Operations.Length }));
        check("model_commitment_pages_keep_records_without_new_actions", calls == 2 && ids.All(id => replies.Any(r => r.Contains(id)))
            && (await f.ReadAsync()).Operations.Length == saved.Operations.Length);
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_output_required");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "commitment-runtime-checks.json"), JsonSerializer.Serialize(new
            { actualHost = true, networkUsed = false, realAccountUsed = false, actualModelUsed = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await PagingAsync(Path.Combine(directory, "paging"), Check);
        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "named-search"), modelEnabled: false);
            await InputAsync(f, "future", "大后天09:00提醒我喝水");
            var before = await f.ReadAsync();
            Check("three_days_reminder_enters_fast_path_without_model", before.WorkItems.Single().Goal == "喝水" && f.Script.Calls == 0);
            await InputAsync(f, "search", "查提醒 喝水");
            var after = await f.ReadAsync();
            Check("reminder_search_is_readonly_fast_path", f.Sent.Last().Contains(before.WorkItems.Single().Id.ToString("D"))
                && f.Script.Calls == 0 && after.Operations.Length == before.Operations.Length
                && DurableAssistantRuntime.IsKnownReadonlyInput("查提醒 喝水 第2页"));
        }
        {
            var clock = new Clock();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "once"), modelEnabled: false, clock: clock);
            string text = "一分钟后提醒我查看合成测试结果";
            await InputAsync(f, "reminder-create", text);
            var saved = await f.ReadAsync();
            var work = saved.WorkItems.Single();
            var schedule = saved.Schedules.Single();
            Check("model_off_natural_reminder_creates_durable_authorized_work",
                work.Kind == TrackedWorkKind.Reminder && work.Stage == TrackedWorkStage.Active
                && saved.Authorizations.Single().SourceExcerpt == text && saved.Operations.Single().Stage == OperationStage.Completed
                && saved.ToolInvocations.Single().Stage == ToolInvocationStage.Completed && f.Script.Calls == 0);
            Check("relative_due_uses_received_time_and_system_zone",
                schedule.ReferenceTimeUtc == saved.Inbox.Single().ReceivedAt
                && schedule.DueAtUtc >= schedule.ReferenceTimeUtc.AddMinutes(1)
                && schedule.DueAtUtc - schedule.ReferenceTimeUtc < TimeSpan.FromSeconds(61)
                && schedule.TimeZoneId == TimeZoneInfo.Local.Id);
            Check("creation_receipt_contains_actual_local_time_and_id",
                f.Sent.Any(body => body.Contains(work.Id.ToString(), StringComparison.Ordinal)
                    && body.Contains(DateTime.Parse(schedule.LocalTime, System.Globalization.CultureInfo.InvariantCulture).ToString("yyyy年MM月dd日 HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
            await f.InputAsync("reminder-create", text);
            Check("duplicate_input_does_not_create_second_reminder", (await f.ReadAsync()).WorkItems.Length == 1);
            var view = await f.Runtime.ReadWorkspaceAsync();
            Check("desktop_view_reads_actual_saved_commitment", view.Commitments?.Single().Id == work.Id.ToString("N"));
            await InputAsync(f, "list-real", "你还记着哪些事");
            Check("readonly_list_reads_real_saved_id", f.Sent.Last().Contains(work.Id.ToString(), StringComparison.Ordinal)
                && f.Script.Calls == 0 && (await f.ReadAsync()).Operations.Length == 1);
            Check("nothing_sent_before_due", !(await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification));
            clock.Advance(TimeSpan.FromSeconds(62));
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
            saved = await f.ReadAsync();
            Check("host_scheduler_prepares_and_sends_real_saved_reminder_once",
                saved.CommitmentRuns.Length == 1 && saved.NotificationDrafts.Single().WorkRunId == saved.CommitmentRuns.Single().Id
                && saved.WorkItems.Single().Stage == TrackedWorkStage.Completed
                && f.Sent.Count(body => body == work.Goal) == 1
                && saved.Operations.Length == 1 && f.Script.Calls == 0);
            Check("ordinary_notification_worker_skips_commitment_payload",
                (await f.Runtime.PendingNotificationsAsync(default)).Length == 0 && f.Runtime.LastError == null);
            clock.Advance(TimeSpan.FromDays(1));
            await InputAsync(f, "after-due-list", "提醒列表");
            Check("completed_reminder_list_describes_notification_flow_not_task_or_phone_success",
                f.Sent.Last().Contains("] 提醒 · 提醒流程已结束", StringComparison.Ordinal)
                && f.Sent.Last().Contains(work.Goal, StringComparison.Ordinal)
                && !f.Sent.Last().Contains(" · 已完成", StringComparison.Ordinal)
                && f.Sent.Last().Contains("接口受理不等于手机已收到", StringComparison.Ordinal)
                && (await f.ReadAsync()).Outbox.Single(o => o.Kind == OutboxKind.Notification).Stage == OutboxStage.Accepted
                && f.Queue.Entries.Count == 0 && f.Script.Calls == 0);
            Check("later_tick_does_not_repeat_completed_reminder", (await f.ReadAsync()).CommitmentRuns.Length == 1
                && (await f.ReadAsync()).Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1);
        }
        {
            var clock = new Clock();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "changes"), modelEnabled: false, clock: clock);
            await InputAsync(f, "change-create", "一分钟后提醒我看初始结果");
            var first = (await f.ReadAsync()).WorkItems.Single();
            string id = first.Id.ToString();
            await InputAsync(f, "pause", "暂停提醒 看初始结果");
            Check("pause_is_durable_and_does_not_interrupt_task", (await f.ReadAsync()).WorkItems.Single().Stage == TrackedWorkStage.Paused
                && f.Queue.Entries.Count == 0);
            clock.Advance(TimeSpan.FromMinutes(2));
            await InputAsync(f, "paused-list", "提醒列表");
            await Task.Delay(1100);
            Check("paused_due_produces_no_run_or_notification", (await f.ReadAsync()).CommitmentRuns.IsEmpty
                && !(await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification));
            await InputAsync(f, "change", "修改提醒 看初始结果 改为 十分钟后提醒我看更新结果");
            var changed = await f.ReadAsync();
            var changedAt = changed.Inbox.Single(input => input.Body.StartsWith("修改提醒 ", StringComparison.Ordinal)).ReceivedAt;
            Check("update_uses_new_request_time_and_preserves_pause",
                changed.WorkItems.Single().Goal == "看更新结果" && changed.WorkItems.Single().Stage == TrackedWorkStage.Paused
                && changed.Schedules.Single().ReferenceTimeUtc == changedAt
                && changed.Schedules.Single().DueAtUtc >= changedAt.AddMinutes(10)
                && changed.Schedules.Single().DueAtUtc - changedAt.AddMinutes(10) < TimeSpan.FromSeconds(1));
            await InputAsync(f, "resume", "恢复提醒 看更新结果");
            Check("resume_is_versioned", (await f.ReadAsync()).WorkItems.Single().Stage == TrackedWorkStage.Active
                && (await f.ReadAsync()).WorkItems.Single().Version > first.Version);
            await InputAsync(f, "cancel", "取消提醒 看更新结果");
            clock.Advance(TimeSpan.FromHours(1));
            await InputAsync(f, "cancelled-list", "事项列表");
            await Task.Delay(1100);
            Check("cancelled_reminder_never_fires_and_keeps_history", (await f.ReadAsync()).WorkItems.Single().Stage == TrackedWorkStage.Cancelled
                && (await f.ReadAsync()).CommitmentRuns.IsEmpty && f.Script.Calls == 0 && f.Queue.Entries.Count == 0);
            await InputAsync(f, "quoted", "如果需要，一分钟后提醒我执行任务");
            Check("conditional_text_creates_no_commitment_or_control", (await f.ReadAsync()).WorkItems.Length == 1
                && f.Queue.Entries.Count == 0);
        }
        {
            var clock = new Clock();
            string path = Path.Combine(directory, "restart");
            ClawBinding binding;
            Guid workId;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, clock: clock))
            {
                await InputAsync(f, "restart-create", "一分钟后提醒我查看重启后结果");
                workId = (await f.ReadAsync()).WorkItems.Single().Id;
                binding = f.Settings.WeChat.ReadBinding()!;
            }
            clock.Advance(TimeSpan.FromMinutes(5)); // Explicit default missed policy: notify once.
            await using (var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, savedBinding: binding, clock: clock))
            {
                await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
                Check("file_reopen_restores_once_after_missed_time", (await resumed.ReadAsync()).WorkItems.Single().Id == workId
                    && (await resumed.ReadAsync()).CommitmentRuns.Length == 1 && resumed.Sent.Count == 1 && resumed.Script.Calls == 0);
                binding = resumed.Settings.WeChat.ReadBinding()!;
            }
            await using var again = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, savedBinding: binding, clock: clock);
            await InputAsync(again, "restart-query", "状态");
            await Task.Delay(1100);
            Check("second_restart_does_not_replay_accepted_reminder", again.Sent.Count == 1
                && !again.Sent.Contains("查看重启后结果")
                && (await again.ReadAsync()).CommitmentRuns.Length == 1
                && (await again.ReadAsync()).Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1);
        }
        foreach (bool unknown in new[] { false, true })
        {
            var clock = new Clock();
            var wire = new System.Collections.Concurrent.ConcurrentQueue<(string ClientId, string Body)>();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, unknown ? "unknown" : "rejected"), modelEnabled: false, clock: clock,
                channelRequestOverride: async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal))
                    {
                        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                        var message = payload.RootElement.GetProperty("msg");
                        wire.Enqueue((message.GetProperty("client_id").GetString()!,
                            message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!));
                    }
                    return null;
                });
            await InputAsync(f, "delivery-create", "一分钟后提醒我查看渠道恢复结果");
            f.SendOutcome = unknown ? "{}" : """{"ret":-2,"errmsg":"synthetic expired context"}""";
            clock.Advance(TimeSpan.FromSeconds(62));
            var expected = unknown ? OutboxStage.Unknown : OutboxStage.Rejected;
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == expected));
            var before = await f.ReadAsync();
            var notice = before.Outbox.Single(o => o.Kind == OutboxKind.Notification);
            string originalRecord = JsonSerializer.Serialize(notice, Settings.Json);
            string originalClient = "assistant-outbox-" + notice.Id.ToString("N");
            var originalRun = before.CommitmentRuns.Single();
            f.SendOutcome = """{"ret":0}""";
            await InputAsync(f, "fresh-context-query", "状态");
            if (!unknown)
                await WaitAsync(async () =>
                {
                    var current = await f.ReadAsync();
                    return current.CatchUpBatches.SelectMany(batch => batch.Members)
                        .Any(member => member.Sources.Any(span => span.OutboxId == notice.Id)
                            && current.Outbox.Any(page => page.Id == member.CatchUpOutboxId && page.Stage == OutboxStage.Accepted));
                });
            else await Task.Delay(1100);
            var saved = await f.ReadAsync();
            var actual = saved.Outbox.Single(o => o.Id == notice.Id);
            var run = saved.CommitmentRuns.Single();
            var window = saved.CatchUpWindows.Single(item => item.SourceMessageId == WeChatText.Hash("fresh-context-query"));
            var catchUpPages = saved.CatchUpBatches.SelectMany(batch => batch.Members).Select(member => member.CatchUpOutboxId)
                .Distinct().Select(id => saved.Outbox.Single(page => page.Id == id)).ToArray();
            Check(unknown ? "unknown_reminder_never_retried_on_new_context" : "explicit_rejection_reminder_retries_on_new_context",
                actual.Stage == expected && actual.SendAttempts == 1 && f.Sent.Count(body => body == notice.Body) == 1
                && (unknown ? catchUpPages.Length == 0 : catchUpPages.Length == 1 && catchUpPages[0].Stage == OutboxStage.Accepted));
            Check(unknown ? "unknown_does_not_claim_completed" : "retry_does_not_create_second_work_or_operation",
                saved.WorkItems.Length == 1 && saved.WorkItems.Single().Id == before.WorkItems.Single().Id
                && saved.Operations.Length == 1 && saved.Operations.Single().Id == before.Operations.Single().Id
                && run.Id == originalRun.Id && run.NotificationEventId == notice.NotificationEventId
                && (unknown ? saved.WorkItems.Single().Stage != TrackedWorkStage.Completed && run.Stage != CommitmentRunStage.Completed
                    : saved.WorkItems.Single().Stage == TrackedWorkStage.Completed && run.Stage == CommitmentRunStage.Completed));
            Check(unknown ? "unknown_original_record_and_current_input_are_preserved" : "catchup_preserves_rejected_original_and_binds_current_input",
                JsonSerializer.Serialize(actual, Settings.Json) == originalRecord
                && window.TrustedInputRevision == saved.Inbox.Single(item => item.SourceMessageId == window.SourceMessageId).Revision
                && window.Input.HasFreshChannelContext && window.Input.Disposition == CatchUpDisposition.AllowHistory
                && wire.Count(item => item.ClientId == originalClient) == 1
                && wire.Single(item => item.ClientId == originalClient).Body == notice.Body);
            if (unknown)
                Check("unknown_has_no_catchup_manifest_or_completion_anchor", saved.CatchUpBatches.IsEmpty
                    && !window.CandidateEventIds.Contains(notice.NotificationEventId!.Value) && run.CatchUpDeliveryOutboxId == null);
            else
            {
                var batch = saved.CatchUpBatches.Single(); var member = batch.Members.Single(); var span = member.Sources.Single();
                var page = catchUpPages.Single(); var originalEvent = saved.Events.Single(item => item.Id == notice.NotificationEventId);
                string bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(notice.Body)));
                string exactBody = "你不在的时候，这些提醒还没发出：\n"
                    + TimeZoneInfo.ConvertTime(originalEvent.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById(window.Input.DisplayTimeZoneId))
                        .ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture)
                    + " 记录（待补报部分）：\n" + notice.Body;
                string pageClient = "assistant-outbox-" + page.Id.ToString("N");
                Check("rejected_catchup_manifest_and_actual_wire_cover_exact_original_once",
                    batch.ManifestVersion == 2 && batch.WindowId == window.Id && window.CandidateEventIds.SequenceEqual(new[] { originalEvent.Id })
                    && member.SourceEventId == originalEvent.Id && member.WorkRunId == originalRun.Id && member.WorkVersion == originalRun.WorkVersion
                    && member.GroupKey == "提醒|" && member.CatchUpOutboxId == page.Id && page.NotificationEventId == batch.DeliveryEventId
                    && span.OutboxId == notice.Id && span.SendAttempts == notice.SendAttempts && span.BodyHash == bodyHash
                    && span.Start == 0 && span.Length == notice.Body.Length && page.Body == exactBody && page.SendAttempts == 1
                    && run.CatchUpDeliveryOutboxId == page.Id && wire.Count(item => item.ClientId == pageClient) == 1
                    && wire.Single(item => item.ClientId == pageClient).Body == exactBody);
            }
            var reservations = saved.CommitmentBudgetReservations.Where(item => item.Kind == CommitmentBudgetKind.Notification).ToArray();
            Check(unknown ? "unknown_keeps_exact_original_notification_charge" : "catchup_charges_exactly_once_for_existing_run_and_batch",
                reservations.Length == (unknown ? 1 : 2) && run.DeliveryAttempts.Length == (unknown ? 1 : 2)
                && reservations.All(item => item.RunId == originalRun.Id && item.WorkItemId == originalRun.WorkItemId && item.ReleasedAt == null)
                && reservations.Count(item => item.ResourceId == notice.Id && item.CatchUpBatchId == null && item.Attempt == 1) == 1
                && (unknown || reservations.Count(item => item.CatchUpBatchId == saved.CatchUpBatches.Single().Id
                    && item.ResourceId == catchUpPages.Single().Id && item.Attempt == 1) == 1));
            string manifest = JsonSerializer.Serialize(saved.CatchUpBatches, Settings.Json);
            string budgets = JsonSerializer.Serialize(saved.CommitmentBudgetReservations, Settings.Json);
            await InputAsync(f, "another-context-query", "状态");
            await Task.Delay(1100);
            var later = await f.ReadAsync();
            Check(unknown ? "later_input_still_never_replays_unknown_reminder" : "later_input_does_not_repeat_accepted_catchup_or_charge_again",
                JsonSerializer.Serialize(later.CatchUpBatches, Settings.Json) == manifest
                && JsonSerializer.Serialize(later.CommitmentBudgetReservations, Settings.Json) == budgets
                && JsonSerializer.Serialize(later.Outbox.Single(item => item.Id == notice.Id), Settings.Json) == originalRecord
                && wire.Count(item => item.ClientId == originalClient) == 1
                && catchUpPages.All(page => wire.Count(item => item.ClientId == "assistant-outbox-" + page.Id.ToString("N")) == 1)
                && later.CommitmentRuns.Length == 1 && later.WorkItems.Length == 1 && later.Operations.Length == 1
                && f.Script.Calls == 0 && f.Queue.Entries.Count == 0);
        }
        foreach (bool fail in new[] { false, true })
        {
            var clock = new Clock();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, fail ? "failure" : "quiet-success"), modelEnabled: false, clock: clock);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
            f.ObservedTask.LastActivity = clock.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            await InputAsync(f, "subscribe-failure", "Alpha失败才叫我");
            var before = await f.ReadAsync();
            Check(fail ? "failure_subscription_bound_to_exact_turn" : "success_quiet_subscription_authorized",
                before.Subscriptions.Single().Condition == SubscriptionCondition.Failed
                && before.WorkItems.Single().Task?.TurnId == f.ObservedTask.TurnId && f.Script.Calls == 0);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = fail ? TaskStatusNames.Failed : TaskStatusNames.Completed;
            await f.Channel.ObserveAsync(f.ObservedTask);
            await WaitAsync(async () => (await f.ReadAsync()).WorkItems.Single().Stage is TrackedWorkStage.Triggered or TrackedWorkStage.Completed);
            if (fail)
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
            await f.Channel.ObserveAsync(f.ObservedTask);
            var after = await f.ReadAsync();
            Check(fail ? "failure_sends_exactly_one_subscription_notification" : "success_is_silent_under_failure_only_subscription",
                after.Outbox.Count(o => o.Kind == OutboxKind.Notification) == (fail ? 1 : 0)
                && after.NotificationDrafts.All(d => d.WorkRunId != null)
                && after.Operations.Length == 1 && f.Script.Calls == 0);
            Check(fail ? "failure_tombstone_keeps_default_suppressed" : "success_tombstone_keeps_default_suppressed",
                !await f.Runtime.ShouldNotifyDefaultTaskAsync(f.ObservedTask, default));
            await InputAsync(f, "subscription-ended-list", "订阅列表");
            Check(fail ? "failed_task_subscription_list_reports_only_subscription_end" : "silent_subscription_end_never_claims_notification_delivered",
                f.Sent.Last().Contains("] 订阅 · 订阅流程已结束", StringComparison.Ordinal)
                && !f.Sent.Last().Contains(" · 已完成", StringComparison.Ordinal)
                && (await f.ReadAsync()).Outbox.Count(o => o.Kind == OutboxKind.Notification) == (fail ? 1 : 0)
                && f.Queue.Entries.Count == 0 && f.Script.Calls == 0);
            f.ObservedTask.TurnId = "synthetic-next-turn";
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Failed;
            Check(fail ? "new_turn_not_suppressed_by_old_failure_subscription" : "new_turn_not_suppressed_by_old_success_subscription",
                await f.Runtime.ShouldNotifyDefaultTaskAsync(f.ObservedTask, default));
        }
        {
            var clock = new Clock();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "inactivity"), modelEnabled: false, clock: clock);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
            f.ObservedTask.LastActivity = clock.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            await InputAsync(f, "inactivity-create", "Alpha一分钟没动静检查一下");
            clock.Advance(TimeSpan.FromSeconds(62));
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
            var saved = await f.ReadAsync();
            Check("inactivity_check_is_readonly_and_does_not_claim_task_stuck",
                saved.CommitmentRuns.Single().CheckAttempts == 1 && saved.Operations.Length == 1
                && f.Queue.Entries.Count == 0 && f.Script.Calls == 0
                && saved.Outbox.Single(o => o.Kind == OutboxKind.Notification).Body.Contains("不能", StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromMinutes(20));
            await InputAsync(f, "inactivity-list", "订阅列表");
            await Task.Delay(1100);
            Check("unchanged_activity_anchor_is_silent", (await f.ReadAsync()).CommitmentRuns.Length == 1
                && (await f.ReadAsync()).Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1);
            var identity = f.Runtime.Identity!;
            await f.Runtime.StopSessionAsync();
            clock.Advance(TimeSpan.FromDays(1));
            Check("stopped_runtime_has_no_active_identity", f.Runtime.Identity == null
                && (await f.Ledger.GetCommitmentsAsync(identity)).Runs.Length == 1);
        }
    }
    private static async Task InputAsync(DurableRuntimeTests.Fixture fixture, string id, string body)
    {
        int replies = (await fixture.ReadAsync()).Outbox.Count(o => o.Kind != OutboxKind.Notification && o.Stage == OutboxStage.Accepted);
        await fixture.InputAsync(id, body);
        await WaitAsync(async () => (await fixture.ReadAsync()).Outbox.Count(o => o.Kind != OutboxKind.Notification
            && o.Stage == OutboxStage.Accepted) > replies);
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("synthetic_commitment_host_condition");
            await Task.Delay(20);
        }
    }
}
