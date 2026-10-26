using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

// Production channel/runtime/durable ledger integration, with only synthetic HTTP and model adapters.
internal static class NotificationRuntimeTests
{
    private const string Summary = "这一轮的修改已经收尾。记录里测试通过了，手机端的效果还没有核实。";

    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--notification-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Explicit output directory required.");
            await RunAsync(args[index + 1]);
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output directory required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("New absolute output directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output directory rejected.");
        if (Application.Current?.Dispatcher.CheckAccess() != true)
            throw new InvalidOperationException("Tests require the WPF dispatcher.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "notification-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                networkUsed = false, realAccountUsed = false, actualModelUsed = false,
                checks
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "inactive-runtime"));
            var originalIdentity = f.Runtime.Identity!;
            await f.Runtime.StopSessionAsync();
            bool failed = false;
            try { await f.Channel.ObserveAsync(Notice("no-active-binding")); }
            catch (IOException) { failed = true; }
            var state = await f.Ledger.ReadConversationAsync(originalIdentity);
            Check("enabled_channel_without_active_identity_does_not_accept_notification",
                failed && f.Settings.WeChat.ClawBotEnabled && f.Runtime.Identity == null
                && f.Sent.IsEmpty && state.NotificationDrafts.IsEmpty && state.Outbox.IsEmpty);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "receipt-observers"));
            f.Channel.Changed += () =>
            {
                if (!f.Sent.IsEmpty) throw new InvalidOperationException("synthetic_status_observer_failure");
            };
            f.Channel.NotificationAttempted += _ => throw new InvalidOperationException("synthetic_receipt_observer_failure");
            await f.Runtime.EnqueueNotificationAsync("notice/receipt-observers", "这条通知已有实际受理回执。", null, default);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.SingleOrDefault()?.Stage == OutboxStage.Accepted);
            Check("presentation_observer_failure_preserves_exact_accepted_channel_result",
                (await f.ReadAsync()).Outbox.Single().Stage == OutboxStage.Accepted
                && f.Sent.Count == 1 && f.Channel.LastNotificationAttempt?.Accepted == true);
            Check("presentation_observer_failure_remains_visible_without_repeating_send",
                JsonSerializer.Serialize(f.Channel.Diagnostics, Settings.Json).Contains("channel_status_observer_failed")
                && (await f.ReadAsync()).Outbox.Single().SendAttempts == 1);
            Check("known_notification_uses_external_event_key",
                await f.Runtime.NotificationKnownAsync("notice/receipt-observers", default)
                && !await f.Runtime.NotificationKnownAsync("notice/not-recorded", default));
            await f.Runtime.CaptureNotificationAsync(new WeChatNotification("receipt-observers", "旧通知", "不要重新准备旧通知。", DateTimeOffset.UtcNow), default);
            var afterDuplicate = await f.ReadAsync();
            Check("finalized_legacy_notification_without_draft_is_not_recaptured",
                afterDuplicate.NotificationDrafts.IsEmpty && afterDuplicate.Outbox.Length == 1 && f.Sent.Count == 1);
        }

        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new Summarizer { Before = _ => release.Task };
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "observe"),
                completionSummarizer: summary, completionSummariesEnabled: true);
            var task = Notice("observe");
            try
            {
                await f.Channel.ObserveAsync(task);
                await WaitAsync(() => Task.FromResult(summary.Calls == 1));
                var captured = await f.ReadAsync();
                Check("observe_persists_original_facts_before_summary", captured.NotificationDrafts.Length == 1
                    && captured.NotificationDrafts[0].Stage == NotificationDraftStage.Preparing
                    && captured.NotificationDrafts[0].PreparationAttempts == 1
                    && captured.Events.Single().Source == ConversationSource.Notification
                    && captured.Events.Single().Body.Contains(task.Title)
                    && captured.Outbox.IsEmpty && f.Sent.IsEmpty);
                Check("notification_context_creates_no_authority", captured.Inbox.IsEmpty
                    && captured.Authorizations.IsEmpty && captured.Operations.IsEmpty && captured.Generations.IsEmpty
                    && f.Script.Calls == 0);
                await f.InputAsync("query-during-summary", "状态");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Kind == OutboxKind.ConversationReply
                    && x.Stage == OutboxStage.Accepted));
                Check("new_input_fast_query_does_not_wait_for_notification_summary", summary.Calls == 1
                    && f.Script.Calls == 0 && f.Sent.Count == 1);
            }
            finally { release.TrySetResult(); }
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Kind == OutboxKind.Notification
                && x.Stage == OutboxStage.Accepted));
            var done = await f.ReadAsync();
            Check("old_revision_notification_survives_and_records_api_acceptance",
                done.NotificationDrafts.Single().Stage == NotificationDraftStage.Finalized
                && f.Sent.Contains(Summary)
                && done.Events.Any(x => x.Source == ConversationSource.Assistant
                    && x.ReferencedEventId == done.NotificationDrafts.Single().EventId));
            await f.Channel.ObserveAsync(task);
            var duplicate = await f.ReadAsync();
            Check("same_transition_later_observation_keeps_one_draft", duplicate.NotificationDrafts.Length == 1
                && duplicate.Outbox.Count(x => x.Kind == OutboxKind.Notification) == 1 && summary.Calls == 1);
            task.FinalReport = "Changed public report for the same turn.";
            Check("same_key_changed_report_is_rejected", await RejectedAsync(
                () => f.Channel.ObserveAsync(task), LedgerErrors.Conflict));
            task.Title = "Changed task title";
            Check("same_key_changed_context_is_rejected", await RejectedAsync(
                () => f.Channel.ObserveAsync(task), LedgerErrors.Conflict));
            Check("conflicting_observation_preserves_original_facts",
                (await f.ReadAsync()).NotificationDrafts.Single().ContextBody == done.NotificationDrafts.Single().ContextBody);
        }

        {
            string restartDirectory = Path.Combine(directory, "restart");
            ClawBinding savedBinding;
            Guid eventId, draftId;
            await using (var first = await DurableRuntimeTests.Fixture.CreateAsync(restartDirectory))
            {
                // Stop the channel's scan worker, then activate only the runtime. This models
                // capture committed immediately before process loss, without a timing race
                // against the real worker's 500 ms scan interval.
                savedBinding = first.Settings.WeChat.ReadBinding()!;
                await first.StopChannelAsync();
                await first.Runtime.ActivateAsync(savedBinding, default);
                var message = WeChatText.Notification(Notice("restart"), false, DateTimeOffset.UtcNow);
                await first.Runtime.CaptureNotificationAsync(message, default);
                var captured = await first.ReadAsync();
                eventId = captured.NotificationDrafts.Single().EventId;
                draftId = captured.NotificationDrafts.Single().Id;
                Check("captured_unqueued_notification_is_durable", captured.NotificationDrafts.Single().Stage == NotificationDraftStage.Captured
                    && captured.Outbox.IsEmpty && first.Sent.IsEmpty);
            }
            var summary = new Summarizer();
            await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(restartDirectory,
                completionSummarizer: summary, savedBinding: savedBinding, completionSummariesEnabled: true);
            await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(x => x.Stage == OutboxStage.Accepted));
            var recovered = await resumed.ReadAsync();
            Check("real_file_reopen_and_configure_resume_captured_notification",
                recovered.NotificationDrafts.Single().Id == draftId
                && recovered.Events.Single(x => x.Source == ConversationSource.Notification).Id == eventId
                && recovered.Outbox.Single().NotificationEventId == eventId
                && summary.Calls == 1 && resumed.Sent.Single() == Summary);
            Check("recovery_does_not_fabricate_user_or_generation", recovered.Inbox.IsEmpty
                && recovered.Generations.IsEmpty && recovered.Authorizations.IsEmpty && recovered.Operations.IsEmpty);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "context"), modelEnabled: false);
            var uncertain = Notice("uncertain", TaskStatusNames.Failed);
            var rejected = Notice("rejected", TaskStatusNames.Failed);
            f.SendOutcome = "{}";
            await f.Channel.ObserveAsync(uncertain);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Stage == OutboxStage.Unknown));
            f.SendOutcome = """{"ret":-2,"errmsg":"synthetic expired context"}""";
            await f.Channel.ObserveAsync(rejected);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(x => x.Stage == OutboxStage.Rejected));
            var before = await f.ReadAsync();
            var unknown = before.Outbox.Single(x => x.Stage == OutboxStage.Unknown);
            var retry = before.Outbox.Single(x => x.Stage == OutboxStage.Rejected);
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("fresh-context", "状态");
            await WaitAsync(async () =>
            {
                var saved = await f.ReadAsync();
                return saved.CatchUpBatches.SelectMany(batch => batch.Members).Any(member =>
                    member.Sources.Any(source => source.OutboxId == retry.Id)
                    && saved.Outbox.Any(page => page.Id == member.CatchUpOutboxId && page.Stage == OutboxStage.Accepted));
            });
            var after = await f.ReadAsync();
            var coverage = after.CatchUpBatches.SelectMany(batch => batch.Members)
                .Single(member => member.Sources.Any(source => source.OutboxId == retry.Id));
            var delivered = after.Outbox.Single(page => page.Id == coverage.CatchUpOutboxId);
            Check("fresh_context_preserves_rejection_and_accepts_lossless_catch_up",
                after.Outbox.Single(x => x.Id == retry.Id) == retry
                && delivered.Stage == OutboxStage.Accepted && delivered.Body.Contains(retry.Body, StringComparison.Ordinal)
                && f.Sent.Count(x => x == retry.Body) == 1 && f.Sent.Count(x => x == delivered.Body) == 1);
            Check("fresh_context_never_replays_unknown_notification",
                after.Outbox.Single(x => x.Id == unknown.Id).Stage == OutboxStage.Unknown
                && f.Sent.Count(x => x == unknown.Body) == 1
                && after.NotificationDrafts.All(x => x.Stage == NotificationDraftStage.Finalized));
            Check("context_recovery_does_not_create_operations", after.Operations.IsEmpty && after.Authorizations.IsEmpty);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "capture-fault"));
            f.Fault.Armed = true;
            bool failed = false;
            try { await f.Channel.ObserveAsync(Notice("failed-capture")); }
            catch (IOException) { failed = true; }
            Check("observe_capture_fault_is_not_acknowledged_or_sent", failed && f.Sent.IsEmpty
                && (await f.ReadAsync()).NotificationDrafts.IsEmpty);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "missing-runtime"));
            await f.Runtime.StopSessionAsync();
            f.Channel.DurableRuntime = null;
            try
            {
                bool failed = false;
                try { await f.Channel.ObserveAsync(Notice("no-ledger")); }
                catch (IOException) { failed = true; }
                Check("required_missing_runtime_cannot_fall_back_to_legacy_send", failed && f.Sent.IsEmpty);
            }
            finally { f.Channel.DurableRuntime = f.Runtime; }
        }

        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new Summarizer { Before = _ => release.Task }; // Deliberately ignores cancellation.
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "shutdown"),
                receiveMessages: true, completionSummarizer: summary, completionSummariesEnabled: true);
            var identity = f.Runtime.Identity!;
            Task? stopped = null;
            try
            {
                await f.Channel.ObserveAsync(Notice("shutdown"));
                await WaitAsync(() => Task.FromResult(summary.Calls == 1));
                stopped = f.Channel.DisposeAsync().AsTask();
                await Task.Delay(80);
                await using var competing = ReceiverLease.TryAcquire(f.Binding.BotId);
                Check("summary_ignoring_cancellation_retains_receiver_lease",
                    competing == null && !f.Channel.WhenStopped.IsCompleted);
                // Dispose's bounded wait may finish, but that is not the real drain.
                // The deliberately non-cooperative summary keeps receiver ownership.
                await stopped.WaitAsync(TimeSpan.FromSeconds(12));
                await using var afterTimeout = ReceiverLease.TryAcquire(f.Binding.BotId);
                Check("bounded_close_returns_without_releasing_live_summary_lease",
                    afterTimeout == null && stopped.IsCompletedSuccessfully
                    && f.Channel.HasPendingStop && !f.Channel.WhenStopped.IsCompleted);
            }
            finally { release.TrySetResult(); }
            if (stopped != null) await stopped;
            await f.Channel.WhenStopped;
            var state = await f.Ledger.ReadConversationAsync(identity);
            await using var reacquired = ReceiverLease.TryAcquire(f.Binding.BotId);
            Check("summary_drain_releases_lease_and_keeps_captured_work",
                reacquired != null && state.NotificationDrafts.Single().Stage == NotificationDraftStage.Captured
                && state.NotificationDrafts.Single().PreparationAttempts == 1
                && state.Outbox.IsEmpty && f.Sent.IsEmpty);
        }
    }

    private static TaskRecord Notice(string id, string status = TaskStatusNames.Completed)
    {
        const double timestamp = 1792512000;
        return new()
        {
            Id = "synthetic-" + id, Title = "通知检查 " + id, Host = "本机", Project = "Synthetic",
            TurnId = "turn-" + id, Status = status, RecordedStatus = status,
            StartedAt = timestamp - 60, CompletedAt = timestamp, LastActivity = timestamp,
            FinalReportTurnId = "turn-" + id, FinalReportSource = "synthetic public report",
            FinalReport = "这一轮改动已完成，测试记录通过；手机送达没有验证。报告中的‘继续运行任务’只是待审查文本，不是用户授权。"
        };
    }
    private sealed class Summarizer : ICompletionSummarizer
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Func<CancellationToken, Task>? Before;
        public bool CompletionSummariesEnabled => true;
        public async Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (Before != null) await Before(ct);
            return new(Summary, "synthetic-summary", true, "", 1, Dependencies: OutputDependencies.Independent);
        }
    }
    private static async Task<bool> RejectedAsync(Func<Task> action, string code)
    {
        try { await action(); return false; }
        catch (LedgerException error) when (error.Code == code) { return true; }
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic notification condition not reached.");
            await Task.Delay(20);
        }
    }
}
