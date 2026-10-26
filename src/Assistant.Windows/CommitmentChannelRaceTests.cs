using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

// Actual channel, durable runtime and sender; new synthetic identities and fake HTTP only.
// The quiet-hours fixture seeds a trusted policy through the full Core authorization
// lifecycle. It does not claim that the current natural-language adapter accepts policy JSON.
internal static class CommitmentChannelRaceTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-channel-race-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit new test directory required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("New absolute synthetic WPF directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected directory rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "commitment-channel-race-checks.json"),
                JsonSerializer.Serialize(new { count = checks.Count, passed = checks.Values.All(v => v), checks,
                    syntheticOnly = true, actualNetworkUsed = false, actualModelUsed = false, actualTaskOperations = 0 }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }

        {
            var pushes = new PushRecorder();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new HeldSummary(release.Task);
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "legacy-mirror-disabled"),
                modelEnabled: false, completionSummarizer: summary, completionSummariesEnabled: true,
                channelRequestOverride: pushes.HandleAsync);
            EnablePush(f);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
            var beta = MakeTask("synthetic-beta", "Beta", TaskStatusNames.Active);
            f.ObservedTasks = [f.ObservedTask, beta];
            var alphaNotice = Terminal(f.ObservedTask, TaskStatusNames.Completed);
            var betaNotice = Terminal(beta, TaskStatusNames.Failed);
            try
            {
                // Durable capture must use ObserveAsync. The retained synchronous
                // legacy entry must not create an independent PushPlus mirror.
                f.Channel.Observe(alphaNotice);
                f.Channel.Observe(betaNotice);
                await InputAsync(f, "subscribe-alpha", "Alpha失败才叫我");
                await InputAsync(f, "subscribe-beta", "Beta失败才叫我");
                Check("legacy_observe_cannot_start_summary_or_push_in_durable_host",
                    (await f.ReadAsync()).WorkItems.Length == 2 && pushes.Bodies.IsEmpty
                    && summary.Calls == 0 && !(await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification)
                    && (await f.ReadAsync()).Operations.All(o => o.Stage == OperationStage.Completed) && f.Script.Calls == 0);
            }
            finally { release.TrySetResult(); }
            var unaffected = MakeTask("synthetic-unaffected", "Unaffected", TaskStatusNames.Failed);
            await f.Channel.ObserveAsync(unaffected);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
            var state = await f.ReadAsync();
            var defaults = state.Events.Where(e => e.ExternalEventKey?.StartsWith("notice/", StringComparison.Ordinal) == true).ToArray();
            Check("durable_capture_uses_claw_without_pushplus_body_mirror",
                summary.Calls == 0 && pushes.Bodies.IsEmpty
                && defaults.Length == 1 && defaults.Single().Task?.ThreadId == unaffected.Id
                && !f.Sent.Contains(HeldSummary.Body));
            Check("durable_claw_notice_is_marked_default_for_later_policy_checks",
                defaults.Single().IsDefaultTaskNotification);
            Check("disabled_legacy_mirror_does_not_poison_sender_or_create_control_operations",
                state.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted)
                && state.Operations.Length == 2 && f.Queue.Entries.Count == 0 && f.Script.Calls == 0);
        }

        {
            var pushes = new PushRecorder();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new HeldSummary(release.Task);
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "durable-summary-race"),
                modelEnabled: false, completionSummarizer: summary, completionSummariesEnabled: true,
                channelRequestOverride: pushes.HandleAsync);
            EnablePush(f);
            f.ObservedTask.Status = f.ObservedTask.RecordedStatus = TaskStatusNames.Active;
            var target = Terminal(f.ObservedTask, TaskStatusNames.Completed);
            var message = WeChatText.Notification(target, f.Settings.WeChat.IncludeSummary, DateTimeOffset.UtcNow,
                f.Settings.WeChat.ReportQueriesEnabled);
            string attempted = WeChatText.Hash("pushplus\n" + WeChatText.Hash(PushToken) + "\n" + message.Key);
            try
            {
                await f.Channel.ObserveAsync(target);
                await WaitAsync(() => Task.FromResult(summary.Calls == 1));
                await InputAsync(f, "subscribe-during-summary", "Alpha失败才叫我");
                Check("subscription_commits_while_durable_summary_is_in_flight",
                    (await f.ReadAsync()).WorkItems.Length == 1 && pushes.Bodies.IsEmpty
                    && !f.Settings.WeChat.RecentAttempts.Contains(attempted));
            }
            finally { release.TrySetResult(); }
            var unaffected = MakeTask("synthetic-after-summary", "AfterSummary", TaskStatusNames.Failed);
            await f.Channel.ObserveAsync(unaffected);
            // A later durable draft is a real preparation-loop drain witness.
            await WaitAsync(async () =>
            {
                var current = await f.ReadAsync();
                return current.Events.Any(e => e.Task?.ThreadId == unaffected.Id && e.NotificationSegments != null
                    && current.Outbox.Any(o => o.NotificationEventId == e.Id && o.Stage == OutboxStage.Accepted));
            });
            var state = await f.ReadAsync();
            Check("post_summary_policy_suppresses_original_notice_without_push_attempt",
                pushes.Bodies.IsEmpty && !f.Sent.Contains(HeldSummary.Body)
                && !state.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Body == HeldSummary.Body)
                && !f.Settings.WeChat.RecentAttempts.Contains(attempted));
            Check("suppressed_capture_remains_audited_without_task_operations_or_replay",
                state.Events.Any(e => e.ExternalEventKey == "notice/" + message.Key && e.IsDefaultTaskNotification)
                && state.Events.Where(e => e.ExternalEventKey?.StartsWith("notice/", StringComparison.Ordinal) == true)
                    .All(e => e.IsDefaultTaskNotification)
                && summary.Calls == 1 && f.Script.Calls == 0 && state.Operations.Length == 1
                && f.Queue.Entries.Count == 0);
        }

        {
            var clock = new Clock(new(2026, 10, 21, 17, 59, 0, TimeSpan.Zero));
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "quiet-pending"),
                modelEnabled: false, clock: clock);
            var identity = f.Runtime.Identity!;
            await f.Runtime.StopSessionAsync();
            await SeedQuietReminderAsync(f.Ledger, identity, clock);
            clock.Set(new(2026, 10, 21, 18, 2, 0, TimeSpan.Zero));
            await f.Ledger.AdvanceCommitmentsAsync(identity);
            var draft = (await f.Ledger.GetPendingCommitmentNotificationsAsync(identity)).Single();
            var quietOutbox = (await f.Ledger.FinalizeNotificationFallbackAsync(identity, draft.Id, draft.ContextBody)).Outbox.Single();
            clock.Set(new(2026, 10, 21, 18, 4, 0, TimeSpan.Zero));
            Check("quiet_fixture_has_a_real_pending_outbox_excluded_by_recovery_policy",
                (await f.Ledger.ReadConversationAsync(identity)).Outbox.Single(o => o.Id == quietOutbox.Id).Stage == OutboxStage.Pending
                && !(await f.Ledger.GetRecoveryPlanAsync(identity)).PendingOutbox.Any(o => o.Id == quietOutbox.Id));
            await f.Runtime.ActivateAsync(f.Binding, default);
            await f.Runtime.EnqueueNotificationAsync("synthetic/unrelated", "不受这条提醒静默时段影响的通知。", null, default);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification
                && o.Id != quietOutbox.Id && o.Stage == OutboxStage.Accepted));
            await InputAsync(f, "status-during-quiet", "状态");
            var state = await f.ReadAsync();
            Check("quiet_pending_notification_does_not_starve_unrelated_notice_or_status_reply",
                state.Outbox.Single(o => o.Id == quietOutbox.Id).Stage == OutboxStage.Pending
                && state.Outbox.Single(o => o.Id == quietOutbox.Id).SendAttempts == 0
                && state.Outbox.Any(o => o.Kind == OutboxKind.ConversationReply && o.Stage == OutboxStage.Accepted)
                && state.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Id != quietOutbox.Id && o.Stage == OutboxStage.Accepted)
                && !f.Sent.Contains(QuietBody) && f.Script.Calls == 0);
            clock.Set(new(2026, 10, 22, 6, 1, 0, TimeSpan.Zero));
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Single(o => o.Id == quietOutbox.Id).Stage == OutboxStage.Accepted);
            state = await f.ReadAsync();
            Check("quiet_end_delivers_original_pending_reminder_once_without_recreating_work",
                state.WorkItems.Length == 1 && state.CommitmentRuns.Length == 1
                && f.Sent.Count(body => body == QuietBody) == 1
                && state.Outbox.Single(o => o.Id == quietOutbox.Id).SendAttempts == 1
                && state.Operations.Length == 1 && f.Queue.Entries.Count == 0 && f.Script.Calls == 0);
        }
    }

    private const string PushToken = "SYNTHETIC_PUSH_RACE_TOKEN";
    private const string QuietBody = "合成静默提醒";
    private static void EnablePush(DurableRuntimeTests.Fixture f)
    {
        f.Settings.WeChat.ProtectedPushPlusToken = LocalSecret.Protect(PushToken);
        f.Settings.WeChat.PushPlusEnabled = true;
    }
    private static TaskRecord MakeTask(string id, string title, string status) => new()
    {
        Id = id, Title = title, Host = "本机", Project = "Synthetic", Cwd = "C:\\synthetic-unused",
        TurnId = "turn-" + id, Status = status, RecordedStatus = status,
        LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d
    };
    private static TaskRecord Terminal(TaskRecord original, string status)
    {
        var value = MakeTask(original.Id, original.Title, status);
        value.TurnId = original.TurnId;
        value.FinalReportTurnId = original.TurnId;
        value.FinalReportSource = "synthetic public final";
        value.FinalReport = HeldSummary.Body;
        value.CompletedAt = value.LastActivity;
        return value;
    }
    private static async Task InputAsync(DurableRuntimeTests.Fixture f, string id, string body)
    {
        await f.InputAsync(id, body);
        string source = WeChatText.Hash(id);
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            var message = state.Inbox.SingleOrDefault(i => i.SourceMessageId == source);
            return message != null && state.Outbox.Any(o => o.Kind == OutboxKind.ConversationReply
                && o.Revision == message.Revision && o.Stage == OutboxStage.Accepted);
        });
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate, int seconds = 8)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic commitment channel condition timed out.");
            await System.Threading.Tasks.Task.Delay(20);
        }
    }
    private static async Task SeedQuietReminderAsync(DurableConversationLedger ledger, ConversationIdentity identity, Clock clock)
    {
        string args = JsonSerializer.Serialize(new
        {
            text = QuietBody, localTime = "2026-10-21T18:01:00", timeZoneId = "UTC", missedPolicy = "notifyOnce",
            policy = new { quietStart = "18:03", quietEnd = "06:00", timeZoneId = "UTC" }
        });
        var descriptor = CommitmentTools.Descriptors().Single(d => d.Name == "schedule");
        if (!descriptor.Parameters.TryValidate(args, out string canonical, out _)) throw new InvalidOperationException("Invalid synthetic schema.");
        const string source = "trusted-quiet-fixture";
        string body = "2026-10-21 18:01 UTC 提醒我" + QuietBody + "，静默 18:03 至 06:00 UTC";
        var received = await ledger.ReceiveAsync(identity, source, body, MessageTrust.AuthenticatedUser, clock.UtcNow);
        var lease = await ledger.BeginGenerationAsync(identity, [source]);
        var invocation = await ledger.RegisterToolInvocationAsync(lease, source, "synthetic-quiet", "schedule", canonical, true);
        var authorization = await ledger.AuthorizeCurrentUserRequestAsync(identity, source, "schedule",
            CommitmentTools.TargetKey("schedule", canonical), canonical, body);
        var operation = await ledger.QueueOperationAsync(identity, authorization.Id, source);
        await ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
        var result = await ledger.ApplyCommitmentToolAsync(new(identity, source, received.CurrentRevision,
            invocation.Id, operation.Id, operation.IdempotencyKey));
        if (result.State != ToolExecutionState.Completed) throw new InvalidOperationException("Synthetic quiet policy was not saved.");
        await ledger.FinishToolInvocationAsync(identity, invocation.Id, ToolInvocationStage.Completed,
            JsonSerializer.Serialize(new { status = "completed", facts = result.Facts }), OperationStage.Completed);
        await ledger.PrepareReplyAsync(lease, ["合成静默规则已保存。"]);
        await ledger.EnqueuePreparedReplyAsync(lease);
    }
    private sealed class HeldSummary(System.Threading.Tasks.Task release) : ICompletionSummarizer
    {
        internal const string Body = "这一轮改动已完成，测试记录通过；手机送达没有验证。";
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public bool CompletionSummariesEnabled => true;
        public async Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.WaitAsync(ct);
            return new(Body, "synthetic", true, "", Dependencies: OutputDependencies.Independent);
        }
    }
    private sealed class PushRecorder
    {
        internal readonly ConcurrentQueue<string> Bodies = new();
        internal async Task<HttpResponseMessage?> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsoluteUri != "https://www.pushplus.plus/send") return null;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Bodies.Enqueue(body.RootElement.GetProperty("content").GetString()!);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"data":"SYNTHETIC_PUSH_RECEIPT"}""", Encoding.UTF8, "application/json")
            };
        }
    }
    private sealed class Clock(DateTimeOffset now) : IClock
    {
        private long ticks = now.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }
}
