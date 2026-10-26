using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Real owned event parsing, host bridge, worker, fake HTTP, and protected files.
// No injected Core observations and no system-clock change.
internal static class OwnedObservationOrderRuntimeTests
{
    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--owned-observation-order-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--owned-observation-order-runtime-test")
                throw new ArgumentException("Explicit fresh output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory)
            || directory.Any(char.IsControl) || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("new_absolute_output_required");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        Settings.SetDataDirectory(Path.Combine(directory, "isolated-settings"));
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool condition)
        {
            checks.Add(name, condition);
            File.WriteAllText(Path.Combine(directory, "owned-observation-order-runtime-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, actualModelUsed = false, systemClockChanged = false, productionDataRead = false,
                count = checks.Count, failures = checks.Count(item => !item.Value), checks }, Settings.Json));
            if (!condition) throw new InvalidOperationException(name);
        }
        await CompletionAndUnknownAsync(Path.Combine(directory, "completed-unknown"), Check);
        await OrderedFailureAsync(Path.Combine(directory, "ordered-failed"), Check);
        await RegistrationDrainAsync(Path.Combine(directory, "registration-drain"), Check);
        await NewEpochReadAsync(Path.Combine(directory, "new-epoch-read"), Check);
        await ReadonlyActivityAsync(Path.Combine(directory, "readonly-activity"), Check);
        await LiveBridgeFenceAsync(Path.Combine(directory, "live-bridge-fence"), Check);
    }

    private static async Task CompletionAndUnknownAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock(); var high = clock.UtcNow;
        ClawBinding binding; ConversationIdentity identity; OperationRecord unknown; Guid runId, workId, eventId;
        string duplicateBody; CommitmentObservationOrder order; int oldSteerCount;
        await using (var h = await Harness.CreateAsync(directory, clock, readBaseline: false))
        {
            var f = h.Host; var target = h.Current; string turn = target.Snapshot.CurrentTurnId!;
            var running = Receipt((await f.ReadAsync()).Operations.Single(x => x.Action == "continue_owned_task"));
            await h.SubscribeAsync("完成后告诉我");
            var before = await f.ReadAsync(); var work = before.WorkItems.Single(); workId = work.Id;
            check("completed_subscription_is_created_from_current_exact_owned_turn_without_model",
                work.Task == new TaskReference(target.Handle.Identity.HostId, target.Handle.Identity.ThreadId, turn, target.Handle.Identity.ProfileId)
                && work.Stage == TrackedWorkStage.Active && before.Subscriptions.Single().Condition == SubscriptionCondition.Completed
                && before.CommitmentRuns.IsEmpty && h.Current.Snapshot.State == OwnedTaskState.Running);

            duplicateBody = "补充 demo/" + target.Handle.Identity.ThreadId + " 保留已有检查要求";
            f.Settings.ChatGpt.Enabled = true; h.Rpc.FailSteerAfterWrite = true;
            await OwnedHostRuntimeTests.SendToolAsync(f, "uncertain-steer", duplicateBody, _ => OwnedHostRuntimeTests.Call("steer_turn", new
            { configurationId = "demo", threadId = target.Handle.Identity.ThreadId, revision = h.Current.Snapshot.Revision,
                expectedTurnId = turn, text = duplicateBody }));
            unknown = (await f.ReadAsync()).Operations.Single(x => x.Action == "steer_turn");
            oldSteerCount = h.Rpc.Steers.Count;
            check("actual_written_steer_failure_is_unknown_before_terminal_observation",
                oldSteerCount == 1 && h.Rpc.SteerDispatchProofs.SequenceEqual(new[] { true })
                && unknown.Stage == OperationStage.Unknown && unknown.Task?.TurnId == turn
                && (await f.ReadAsync()).ToolInvocations.Single(x => x.OperationId == unknown.Id).Stage == ToolInvocationStage.Unknown);
            f.Settings.ChatGpt.Enabled = false; int modelBefore = f.Script.Calls;
            clock.Set(high.AddMinutes(-10));
            h.Emit("completed");
            await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Length == 1
                && (await f.ReadAsync()).CommitmentRuns.Single().Stage == CommitmentRunStage.Completed, h, "completed-notification");
            var after = await f.ReadAsync(); var subscription = after.Subscriptions.Single(); var run = after.CommitmentRuns.Single();
            var terminal = Receipt(after.Operations.Single(x => x.Action == "continue_owned_task"));
            order = subscription.LastObservationOrder!; runId = run.Id;
            var draft = after.NotificationDrafts.Single(x => x.Id == run.NotificationDraftId); eventId = draft.EventId;
            var notification = after.Outbox.Single(x => x.NotificationEventId == eventId);
            h.WriteEvidence("rollback-completed", after);
            check("higher_owned_sequence_accepts_real_completion_with_accurate_earlier_timestamp",
                terminal.Sequence > running.Sequence && terminal.At == high.AddMinutes(-10)
                && subscription.LastObservedAt == terminal.At && subscription.LastState == CommitmentTaskState.Completed
                && order.Sequence == terminal.Sequence && order.Lease.ConnectionEpoch == h.Session.Client.ConnectionEpoch
                && order.Lease.Identity == f.Runtime.Identity && order.Lease.Generation > 0);
            check("completed_subscription_creates_one_factual_run_and_one_actual_accepted_notice",
                run.WorkItemId == work.Id && run.TriggerKey == "terminal" && run.Stage == CommitmentRunStage.Completed
                && after.WorkItems.Single().Stage == TrackedWorkStage.Completed && notification.Stage == OutboxStage.Accepted
                && f.Sent.Count(x => x == notification.Body) == 1
                && notification.Body.Contains("结果和测试还没有额外复核", StringComparison.Ordinal)
                && !after.Events.Any(x => x.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true
                    && x.Body.Contains("这一轮已经结束", StringComparison.Ordinal)));
            check("completion_does_not_resolve_unknown_steer_or_issue_additional_control",
                after.Operations.Single(x => x.Id == unknown.Id) == unknown && h.Rpc.Steers.Count == 1
                && f.Script.Calls == modelBefore && f.Runtime.LastError == null && f.Runtime.OwnedRuntime!.Failure == null);

            h.Emit("completed"); h.Emit("inProgress");
            h.Rpc.Inner.Emit("turn/completed", new { threadId = target.Handle.Identity.ThreadId,
                turn = OwnedHostRuntimeTests.FakeRpc.Turn("unrelated-older-turn", "failed") });
            clock.Set(high.AddMinutes(5));
            await Task.Delay(2200); // Two actual worker passes after cached evidence becomes stale.
            var repeated = await f.ReadAsync();
            check("duplicate_terminal_late_started_and_wrong_turn_never_create_another_run_or_send",
                repeated.CommitmentRuns.Single().Id == runId && repeated.NotificationDrafts.Length == 1
                && repeated.Outbox.Count(x => x.NotificationEventId == eventId) == 1
                && f.Sent.Count(x => x == notification.Body) == 1 && h.Current.Snapshot.State == OwnedTaskState.Completed
                && h.Current.Snapshot.CurrentTurnId == turn && repeated.Subscriptions.Single().LastObservationOrder == order
                && repeated.Subscriptions.Single().LastObservedAt == high.AddMinutes(-10));
            binding = f.Settings.WeChat.ReadBinding()!; identity = f.Runtime.Identity!;
        }

        // Actual DPAPI file + host reopen. Construction never starts an app server.
        var created = new ConcurrentQueue<RecordingRpc>();
        var profile = Harness.Profile(directory, high);
        var options = Harness.Options(directory, profile, null, rpc => created.Enqueue(rpc));
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(directory, savedBinding: binding,
            ownedOptions: options, clock: clock, modelEnabled: false);
        var restored = await reopened.Ledger.ReadConversationAsync(identity);
        check("file_and_host_reopen_preserve_completed_subscription_order_and_unknown_operation",
            !restored.IsReadOnly && restored.RecoveryReason == null && restored.WorkItems.Single().Id == workId
            && restored.WorkItems.Single().Stage == TrackedWorkStage.Completed && restored.CommitmentRuns.Single().Id == runId
            && restored.Subscriptions.Single().LastObservationOrder == order
            && restored.Operations.Single(x => x.Id == unknown.Id) == unknown && created.IsEmpty);
        var duplicate = await reopened.Ledger.ReceiveAsync(identity, WeChatText.Hash("uncertain-steer"), duplicateBody,
            MessageTrust.AuthenticatedUser, clock.UtcNow);
        await reopened.InputAsync("uncertain-steer", duplicateBody);
        await reopened.InputAsync("status-after-order-reopen", "状态");
        await OwnedHostRuntimeTests.WaitReplyAsync(reopened, "status-after-order-reopen");
        await reopened.Runtime.StopSessionAsync();
        var final = await reopened.Ledger.ReadConversationAsync(identity);
        check("fresh_input_after_reopen_does_not_resend_unknown_control_or_completed_notification",
            duplicate.IsDuplicate && final.Operations.Single(x => x.Id == unknown.Id) == unknown
            && final.CommitmentRuns.Single().Id == runId && final.Outbox.Single(x => x.NotificationEventId == eventId).Stage == OutboxStage.Accepted
            && created.IsEmpty && reopened.Script.Calls == 0
            && reopened.Sent.All(x => !x.Contains("结果和测试还没有额外复核", StringComparison.Ordinal)));
    }

    private static async Task OrderedFailureAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock(); var high = clock.UtcNow;
        await using var h = await Harness.CreateAsync(directory, clock, readBaseline: true);
        var f = h.Host; var target = h.Current; string turn = target.Snapshot.CurrentTurnId!;
        await h.SubscribeAsync("失败才叫我");
        // No matching start operation: this real live running event publishes a
        // new source sample, unlike a repeated operation state which is a no-op.
        h.Emit("inProgress");
        await WaitAsync(async () => (await f.ReadAsync()).Subscriptions.Single().LastObservationOrder != null, h, "ordered-running");
        var before = await f.ReadAsync(); var initial = before.Subscriptions.Single(); var initialOrder = initial.LastObservationOrder!;
        check("real_live_running_event_establishes_ordered_high_watermark_after_subscription",
            initial.LastObservedAt == high && initial.LastState == CommitmentTaskState.Running
            && initialOrder.Sequence == h.Session.Client.LatestTurnObservation(target.Handle)!.Sequence
            && initialOrder.Lease.ConnectionEpoch == h.Session.Client.ConnectionEpoch
            && before.CommitmentRuns.IsEmpty && !before.Operations.Any(x => x.Action == "continue_owned_task"));
        var ordinaryRead = (Task<CommitmentTaskObservation?>)typeof(DurableAssistantRuntime).GetMethod("ReadCommitmentObservationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(f.Runtime,
                new object[] { before.WorkItems.Single().Task!, false, CancellationToken.None })!;
        var cached = await ordinaryRead;
        check("ordinary_owned_worker_does_not_submit_cached_order_ahead_of_the_serial_live_bridge",
            cached == null && (await f.ReadAsync()).Subscriptions.Single() == initial
            && h.Session.Client.LatestTurnObservation(target.Handle)!.Sequence == initialOrder.Sequence);
        clock.Set(high.AddSeconds(20)); h.Emit("inProgress");
        await WaitAsync(async () => (await f.ReadAsync()).Subscriptions.Single().LastObservationOrder!.Sequence > initialOrder.Sequence, h, "ordered-repeat");
        var repeated = (await f.ReadAsync()).Subscriptions.Single();
        check("same_state_live_sample_advances_source_watermark_without_inventing_activity",
            repeated.LastObservedAt == high.AddSeconds(20) && repeated.LastActivityAt == initial.LastActivityAt
            && repeated.LastObservationOrder!.Lease == initialOrder.Lease && (await f.ReadAsync()).CommitmentRuns.IsEmpty);
        int callsBefore = h.Rpc.Inner.Methods.Count, modelBefore = f.Script.Calls;
        clock.Set(high.AddMinutes(-10)); h.Emit("failed");
        await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Length == 1
            && (await f.ReadAsync()).CommitmentRuns.Single().Stage == CommitmentRunStage.Completed, h, "failed-notification");
        var after = await f.ReadAsync(); var final = after.Subscriptions.Single(); var run = after.CommitmentRuns.Single();
        var draft = after.NotificationDrafts.Single(x => x.Id == run.NotificationDraftId);
        var sent = after.Outbox.Single(x => x.NotificationEventId == draft.EventId);
        h.WriteEvidence("rollback-failed", after);
        check("ordered_failure_after_rollback_replaces_high_watermark_by_sequence_not_wall_clock",
            final.LastState == CommitmentTaskState.Failed && final.LastObservedAt == high.AddMinutes(-10)
            && final.LastObservationOrder!.Sequence > repeated.LastObservationOrder!.Sequence
            && final.LastObservationOrder.Lease == repeated.LastObservationOrder.Lease
            && final.LastActivityAt == repeated.LastActivityAt && h.Current.Snapshot.State == OwnedTaskState.Failed);
        check("failure_only_subscription_delivers_exact_turn_once_without_default_duplicate_or_extra_rpc",
            run.TriggerKey == "terminal" && sent.Stage == OutboxStage.Accepted && sent.Body.Contains("这一轮失败了", StringComparison.Ordinal)
            && f.Sent.Count(x => x == sent.Body) == 1 && after.WorkItems.Single().Task?.TurnId == turn
            && after.Outbox.Count(x => x.Kind == OutboxKind.Notification) == 1
            && h.Rpc.Inner.Methods.Count == callsBefore && f.Script.Calls == modelBefore
            && f.Runtime.LastError == null && f.Runtime.OwnedRuntime!.Failure == null);
        h.Emit("failed"); h.Emit("inProgress");
        clock.Set(high.AddMinutes(5)); await Task.Delay(2200);
        var deduped = await f.ReadAsync();
        check("failed_run_stays_single_after_duplicate_event_and_clock_catches_up",
            deduped.CommitmentRuns.Single().Id == run.Id && deduped.NotificationDrafts.Length == 1
            && f.Sent.Count(x => x == sent.Body) == 1 && deduped.WorkItems.Single().Stage == TrackedWorkStage.Completed
            && h.Current.Snapshot.CurrentTurnId == turn && h.Current.Snapshot.State == OwnedTaskState.Failed);
    }

    private static async Task RegistrationDrainAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        await using var h = await Harness.CreateAsync(directory, clock, readBaseline: false, emitRunning: false);
        var f = h.Host; var oldRuntime = f.Runtime.OwnedRuntime!; var oldSession = h.Session; var oldTask = h.Current;
        var epoch = oldSession.Client.ConnectionEpoch; var identity = f.Runtime.Identity!;
        var entered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int blocked = 0;
        Task? configure = null;
        f.Fault.ObserveCommitted = _ => { }; // Capture the real plaintext transaction before DPAPI encryption.
        f.Fault.Observe = stage =>
        {
            if (stage != JournalWriteStage.BeforeFlush || f.Fault.CandidateSnapshot is not { } candidate
                || !candidate.TryGetProperty("commitmentObservationSources", out var sources) || sources.GetArrayLength() != 1
                || Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.TrySetResult(sources[0].Clone());
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("synthetic_registration_flush_release");
        };
        try
        {
            h.Emit("inProgress");
            var pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            check("registration_pause_is_the_actual_first_source_transaction_before_durable_flush",
                pending.GetProperty("lease").GetProperty("connectionEpoch").GetGuid() == epoch
                && pending.GetProperty("lease").GetProperty("generation").GetInt64() == 1
                && oldSession.ObservationLease == null && !oldSession.Pump.IsCompleted);
            var rotated = f.Settings.WeChat.ReadBinding()!;
            rotated.Token = "SYNTHETIC_ROTATED_TOKEN";
            f.Settings.WeChat.WriteBinding(rotated);
            configure = f.Channel.ConfigureAsync();
            await Task.Delay(150); // Storage remains blocked by a test-owned signal, not a slow-write assumption.
            check("actual_channel_reconfigure_waits_for_old_registration_and_owned_pump_to_drain",
                !configure.IsCompleted && !oldSession.Pump.IsCompleted && !release.IsSet
                && (f.Runtime.OwnedRuntime == null || ReferenceEquals(f.Runtime.OwnedRuntime, oldRuntime)));
        }
        finally
        {
            release.Set(); f.Fault.Observe = null; f.Fault.ObserveCommitted = null;
            if (configure != null) await configure.WaitAsync(TimeSpan.FromSeconds(12));
        }
        var newer = f.Runtime.OwnedRuntime!;
        check("replacement_begins_only_after_original_registration_and_pump_finish",
            oldSession.Pump.IsCompleted && h.Rpc.Inner.Disposed && newer != oldRuntime
            && newer.Active && f.Runtime.Identity == identity && oldSession.ObservationLease?.ConnectionEpoch == epoch);
        // A restored task is read-only. Seed the new fake server's actual read
        // response, then use the real session restoration/read path.
        Harness.ConnectionSeeds[newer.Identity.BindingId + "\n" + directory] = next => next.Inner.SetThread(
            oldTask.Handle.Identity.ThreadId, oldTask.Snapshot.Cwd,
            OwnedHostRuntimeTests.FakeRpc.Turn(oldTask.Snapshot.CurrentTurnId!, "inProgress"));
        var nextSession = await newer.EnsureSessionAsync("demo", "thread/read", true, default);
        var restoredTask = newer.Tasks().Single(x => x.Handle.Identity == oldTask.Handle.Identity);
        await nextSession.Client.ReconcileAsync(restoredTask.Handle);
        // The factory records the actual current fake connection; no old source is re-signed.
        var nextRpc = Harness.Connections[newer.Identity.BindingId + "\n" + directory].Last();
        await WaitAsync(() => Task.FromResult(nextSession.ObservationLease != null), h, "replacement-source");
        var currentLease = nextSession.ObservationLease!;
        h.Rpc.Inner.Emit("turn/completed", new { threadId = oldTask.Handle.Identity.ThreadId,
            turn = OwnedHostRuntimeTests.FakeRpc.Turn(oldTask.Snapshot.CurrentTurnId!, "completed") });
        await Task.Delay(100);
        check("new_epoch_registers_next_generation_and_disposed_old_event_cannot_cover_it",
            currentLease.ConnectionEpoch != epoch && currentLease.Generation == 2
            && currentLease.Identity == identity && nextSession.ObservationLease == currentLease
            && newer.Tasks().Single(x => x.Handle.Identity == restoredTask.Handle.Identity).Snapshot.State == OwnedTaskState.Running
            && restoredTask.Handle.Access == OwnedTaskAccess.RestoredReadOnly
            && nextRpc.Inner.Methods.Contains("thread/read") && !nextRpc.Inner.Methods.Contains("turn/start")
            && f.Runtime.LastError == null && newer.Failure == null);
        h.WriteEvidence("registration-replacement-drained", await f.ReadAsync());
    }

    private static async Task NewEpochReadAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock(); var high = clock.UtcNow;
        // Creation proof must remain in the past after the later rollback;
        // the independent provenance future-time guard remains unchanged.
        clock.Set(high.AddMinutes(-30));
        await using var h = await Harness.CreateAsync(directory, clock, readBaseline: true);
        var f = h.Host; var original = h.Current;
        clock.Set(high);
        await h.Session.Client.ReconcileAsync(original.Handle);
        await WaitAsync(() => Task.FromResult(h.Current.ObservedAt == high), h, "fresh-baseline-before-subscribe");
        await h.SubscribeAsync("完成后告诉我"); h.Emit("inProgress");
        await WaitAsync(async () => (await f.ReadAsync()).Subscriptions.Single().LastObservationOrder != null, h, "pre-replacement-order");
        var prior = (await f.ReadAsync()).Subscriptions.Single(); var oldLease = prior.LastObservationOrder!.Lease;
        var oldSession = h.Session; var oldIdentity = f.Runtime.Identity!; int modelBefore = f.Script.Calls;
        clock.Set(high.AddMinutes(-10));
        var rotated = f.Settings.WeChat.ReadBinding()!; rotated.Token = "SYNTHETIC_NEW_EPOCH_TOKEN";
        f.Settings.WeChat.WriteBinding(rotated); await f.Channel.ConfigureAsync();
        var current = f.Runtime.OwnedRuntime!;
        var beforeRead = await f.ReadAsync();
        check("replacement_does_not_relabel_cached_old_epoch_or_create_completion_without_new_evidence",
            h.Rpc.Inner.Disposed && oldSession.Pump.IsCompleted && current.Tasks().Count == 0
            && beforeRead.Subscriptions.Single().LastObservationOrder == prior.LastObservationOrder
            && beforeRead.WorkItems.Single().Stage == TrackedWorkStage.Active && beforeRead.CommitmentRuns.IsEmpty
            && beforeRead.NotificationDrafts.IsEmpty && f.Runtime.Identity == oldIdentity);
        Harness.ConnectionSeeds[current.Identity.BindingId + "\n" + directory] = next => next.Inner.SetThread(
            original.Handle.Identity.ThreadId, original.Snapshot.Cwd,
            OwnedHostRuntimeTests.FakeRpc.Turn(original.Snapshot.CurrentTurnId!, "completed"));
        var session = await current.EnsureSessionAsync("demo", "thread/read", true, default);
        var restored = current.Tasks().Single();
        await session.Client.ReconcileAsync(restored.Handle);
        await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Length == 1
            && (await f.ReadAsync()).CommitmentRuns.Single().Stage == CommitmentRunStage.Completed, h, "fresh-read-completion");
        var after = await f.ReadAsync(); var subscription = after.Subscriptions.Single(); var run = after.CommitmentRuns.Single();
        var draft = after.NotificationDrafts.Single(); var notice = after.Outbox.Single(x => x.NotificationEventId == draft.EventId);
        var newLease = subscription.LastObservationOrder!.Lease;
        // OwnedRuntimeTask is an immutable view captured before the explicit
        // read. Re-read the actual current view after the true client update.
        var observedRestored = current.Tasks().Single();
        h.WriteEvidence("fresh-new-epoch-before-assert", after);
        check("fresh_new_epoch_read_establishes_exact_active_subscription_order_across_clock_rollback",
            newLease.ConnectionEpoch == session.Client.ConnectionEpoch && newLease.ConnectionEpoch != oldLease.ConnectionEpoch
            && newLease.Generation == oldLease.Generation + 1 && newLease.SourceKey == oldLease.SourceKey
            && subscription.LastState == CommitmentTaskState.Completed && subscription.LastObservedAt == high.AddMinutes(-10)
            && after.WorkItems.Single().Task?.TurnId == original.Snapshot.CurrentTurnId
            && observedRestored.Handle.Access == OwnedTaskAccess.RestoredReadOnly && observedRestored.Snapshot.State == OwnedTaskState.Completed);
        check("active_subscription_read_completion_sends_once_without_default_historical_notice_or_control",
            run.Stage == CommitmentRunStage.Completed && notice.Stage == OutboxStage.Accepted
            && f.Sent.Count(x => x == notice.Body) == 1 && after.NotificationDrafts.Length == 1
            && !after.Events.Any(x => x.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true)
            && Harness.Connections[current.Identity.BindingId + "\n" + directory].Last().Inner.Methods.All(x => x != "turn/start" && x != "turn/steer")
            && f.Script.Calls == modelBefore);
        int oldReads = ReadEventCount(after);
        await session.Client.ReconcileAsync(restored.Handle);
        await WaitAsync(async () => ReadEventCount(await f.ReadAsync()) > oldReads, h, "repeated-read-persisted");
        h.Rpc.Inner.Emit("turn/completed", new { threadId = original.Handle.Identity.ThreadId,
            turn = OwnedHostRuntimeTests.FakeRpc.Turn(original.Snapshot.CurrentTurnId!, "failed") });
        var final = await f.ReadAsync();
        check("repeated_read_and_disposed_old_epoch_cannot_duplicate_or_replace_the_finished_run",
            final.CommitmentRuns.Single().Id == run.Id && final.Subscriptions.Single().LastObservationOrder!.Lease == newLease
            && final.Subscriptions.Single().LastState == CommitmentTaskState.Completed
            && final.Outbox.Count(x => x.NotificationEventId == draft.EventId) == 1 && f.Sent.Count(x => x == notice.Body) == 1
            && f.Runtime.LastError == null && current.Failure == null);
        h.WriteEvidence("fresh-new-epoch-read", final);
    }
    private static int ReadEventCount(ConversationSnapshot state) => state.Events.Count(x => x.Source == ConversationSource.ToolResult
        && x.Body.Contains("owned_app_server_event", StringComparison.Ordinal) && x.Body.Contains("thread/read", StringComparison.Ordinal));

    private static async Task ReadonlyActivityAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock(); var high = clock.UtcNow;
        await using var h = await Harness.CreateAsync(directory, clock, readBaseline: true);
        await h.SubscribeAsync("完成后告诉我");
        var before = (await h.Host.ReadAsync()).Subscriptions.Single();
        clock.Set(high.AddSeconds(20));
        await h.Session.Client.ReconcileAsync(h.Current.Handle);
        await WaitAsync(async () => (await h.Host.ReadAsync()).Subscriptions.Single().LastObservationOrder != null, h, "read-only-watermark");
        var after = await h.Host.ReadAsync(); var updated = after.Subscriptions.Single();
        check("fresh_read_from_unknown_to_running_advances_order_without_resetting_activity_time",
            before.LastState == CommitmentTaskState.Unknown && before.LastObservationOrder == null
            && updated.LastState == CommitmentTaskState.Running && updated.LastObservedAt == high.AddSeconds(20)
            && updated.LastActivityAt == before.LastActivityAt && updated.LastObservationOrder!.Sequence > 0
            && updated.LastObservationOrder.Lease.ConnectionEpoch == h.Session.Client.ConnectionEpoch
            && after.CommitmentRuns.IsEmpty && after.NotificationDrafts.IsEmpty
            && h.Session.Client.LatestTurnObservation(h.Current.Handle)?.Method == "thread/read");
        h.WriteEvidence("readonly-state-is-not-activity", after);
    }

    private static async Task LiveBridgeFenceAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock(); var high = clock.UtcNow;
        await using var h = await Harness.CreateAsync(directory, clock, readBaseline: true);
        var f = h.Host;
        await h.SubscribeAsync("完成后告诉我");
        var before = await f.ReadAsync(); var prior = before.Subscriptions.Single();
        long priorSequence = h.Session.Client.LatestTurnObservation(h.Current.Handle)!.Sequence;
        var entered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int blocked = 0;
        f.Fault.ObserveCommitted = _ => { };
        f.Fault.Observe = stage =>
        {
            if (stage != JournalWriteStage.BeforeFlush || f.Fault.CandidateSnapshot is not { } candidate
                || !candidate.TryGetProperty("subscriptions", out var subscriptions) || subscriptions.GetArrayLength() != 1) return;
            var subscription = subscriptions[0];
            if (subscription.GetProperty("lastState").GetString() != nameof(CommitmentTaskState.Completed)
                || subscription.GetProperty("lastObservedAt").GetDateTimeOffset() != high.AddSeconds(20)
                || Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.TrySetResult(subscription.Clone());
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("synthetic_live_observation_flush_release");
        };
        try
        {
            clock.Set(high.AddSeconds(20)); h.Emit("completed");
            var candidate = await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var latest = h.Session.Client.LatestTurnObservation(h.Current.Handle)!;
            var pendingLedgerRead = f.ReadAsync();
            var read = (Task<CommitmentTaskObservation?>)typeof(DurableAssistantRuntime).GetMethod("ReadCommitmentObservationAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(f.Runtime,
                    new object[] { before.WorkItems.Single().Task!, false, CancellationToken.None })!;
            var cached = await read.WaitAsync(TimeSpan.FromSeconds(2));
            File.WriteAllText(Path.Combine(directory, "before-live-flush.json"), JsonSerializer.Serialize(new
            { prior, candidate, latest, ordinaryPollReturnedObservation = cached != null,
                actualLedgerReadBlocked = !pendingLedgerRead.IsCompleted }, Settings.Json));
            check("real_client_cache_cannot_submit_same_sequence_while_live_observation_flush_is_blocked",
                !release.IsSet && !pendingLedgerRead.IsCompleted && latest.Sequence > priorSequence
                && latest.State == ControlState.Completed && candidate.GetProperty("lastActivityAt").GetDateTimeOffset() == high.AddSeconds(20)
                && cached == null);
        }
        finally { release.Set(); f.Fault.Observe = null; f.Fault.ObserveCommitted = null; }
        await WaitAsync(async () => (await f.ReadAsync()).CommitmentRuns.Length == 1
            && (await f.ReadAsync()).CommitmentRuns.Single().Stage == CommitmentRunStage.Completed, h, "live-flush-completed");
        var after = await f.ReadAsync(); var saved = after.Subscriptions.Single();
        var draft = after.NotificationDrafts.Single(); var notice = after.Outbox.Single(x => x.NotificationEventId == draft.EventId);
        check("released_live_transaction_keeps_original_activity_time_and_sends_only_its_one_notification",
            saved.LastActivityAt == high.AddSeconds(20) && saved.LastActivityAt > prior.LastActivityAt
            && saved.LastObservedAt == high.AddSeconds(20) && saved.LastObservationOrder!.Sequence > priorSequence
            && notice.Stage == OutboxStage.Accepted && f.Sent.Count(x => x == notice.Body) == 1
            && after.CommitmentRuns.Length == 1 && after.NotificationDrafts.Length == 1
            && f.Runtime.LastError == null && f.Runtime.OwnedRuntime!.Failure == null);
        h.WriteEvidence("after-live-flush", after);
    }

    private sealed class Harness(DurableRuntimeTests.Fixture host, RecordingRpc rpc, string directory, Clock clock) : IAsyncDisposable
    {
        internal static readonly ConcurrentDictionary<string, ConcurrentQueue<RecordingRpc>> Connections = new();
        internal static readonly ConcurrentDictionary<string, Action<RecordingRpc>> ConnectionSeeds = new();
        internal DurableRuntimeTests.Fixture Host => host;
        internal RecordingRpc Rpc => rpc;
        internal OwnedRuntimeTask Current => host.Runtime.OwnedRuntime!.Tasks().Single();
        internal OwnedControlRuntime.Session Session => host.Runtime.OwnedRuntime!.LoadedSession("demo")!;
        internal static ConfiguredOwnedProfile Profile(string directory, DateTimeOffset now)
        {
            var p = OwnedHostRuntimeTests.Profile(directory, verified: true);
            return p with { Configuration = p.Configuration with
                { Verification = p.Configuration.Verification! with { VerifiedAt = now.AddDays(-1), ExpiresAt = now.AddDays(1) } } };
        }
        internal static OwnedHostOptions Options(string directory, ConfiguredOwnedProfile profile,
            Func<Task<ConversationSnapshot>>? snapshot, Action<RecordingRpc> created) =>
            new(Path.Combine(directory, "owned"), [profile], (configured, _) =>
            {
                var rpc = new RecordingRpc(new(configured.Configuration, async () => snapshot != null
                    && (await snapshot()).Operations.Any(x => x.Stage == OperationStage.Dispatching)), snapshot);
                created(rpc); return Task.FromResult<IJsonRpcConnection>(rpc);
            });
        internal static async Task<Harness> CreateAsync(string directory, Clock clock, bool readBaseline, bool emitRunning = true)
        {
            RecordingRpc? rpc = null; DurableRuntimeTests.Fixture? f = null;
            var options = Options(directory, Profile(directory, clock.UtcNow), () => f!.ReadAsync(), value =>
            {
                rpc = value;
                string key = f!.Runtime.Identity!.BindingId + "\n" + directory;
                Connections.GetOrAdd(key, _ => new()).Enqueue(value);
                if (ConnectionSeeds.TryGetValue(key, out var seed)) seed(value);
            });
            f = await DurableRuntimeTests.Fixture.CreateAsync(directory, ownedOptions: options, clock: clock);
            try
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
                var h = new Harness(f, rpc!, directory, clock); var task = h.Current;
                if (readBaseline)
                {
                    rpc!.Inner.SetThread(task.Handle.Identity.ThreadId, task.Snapshot.Cwd,
                        OwnedHostRuntimeTests.FakeRpc.Turn(Guid.NewGuid().ToString("D"), "inProgress"));
                    await h.Session.Client.ReconcileAsync(task.Handle);
                    await WaitAsync(() => Task.FromResult(h.Current.ObservedAt == clock.UtcNow
                        && h.Current.Snapshot.State == OwnedTaskState.Running), h, "fresh-owned-read");
                }
                else
                {
                    string instruction = "继续 " + task.Handle.Identity.ThreadId + " 检查合成时钟回拨订阅";
                    await OwnedHostRuntimeTests.SendToolAsync(f, "start", instruction, _ => OwnedHostRuntimeTests.ContinueCall(h.Current, instruction));
                    if (emitRunning)
                    {
                        h.Emit("inProgress");
                        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(x => x.Action == "continue_owned_task").Stage == OperationStage.Running, h, "real-running");
                    }
                }
                return h;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task SubscribeAsync(string suffix)
        {
            Host.Settings.ChatGpt.Enabled = false;
            await Host.InputAsync("subscribe", Current.Handle.Identity.ThreadId + suffix);
            await OwnedHostRuntimeTests.WaitReplyAsync(Host, "subscribe");
            await WaitAsync(async () => (await Host.ReadAsync()).Subscriptions.Length == 1, this, "subscription-created");
        }
        internal void Emit(string status) => Rpc.Inner.Emit(status == "inProgress" ? "turn/started" : "turn/completed",
            new { threadId = Current.Handle.Identity.ThreadId, turn = OwnedHostRuntimeTests.FakeRpc.Turn(Current.Snapshot.CurrentTurnId!, status) });
        internal void WriteEvidence(string phase, ConversationSnapshot state) => File.WriteAllText(Path.Combine(directory, phase + ".json"),
            JsonSerializer.Serialize(new { phase, injectedClockUtc = clock.UtcNow, state.WorkItems, state.Subscriptions, state.CommitmentRuns,
                state.NotificationDrafts, outbox = state.Outbox.Select(x => new { x.Id, x.Stage, x.Kind, x.Body, x.NotificationEventId }),
                operations = state.Operations.Select(x => new { x.Id, x.Action, x.Stage, x.Receipt }),
                runtimeError = Host.Runtime.LastError, ownedError = Host.Runtime.OwnedRuntime?.Failure,
                rpcMethods = Rpc.Inner.Methods.ToArray() }, Settings.Json));
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
    private sealed class RecordingRpc(OwnedHostRuntimeTests.FakeRpc inner, Func<Task<ConversationSnapshot>>? snapshot) : IJsonRpcConnection
    {
        internal OwnedHostRuntimeTests.FakeRpc Inner => inner;
        internal readonly ConcurrentQueue<JsonElement> Steers = new();
        internal readonly ConcurrentQueue<bool> SteerDispatchProofs = new();
        internal bool FailSteerAfterWrite;
        public ConnectionIdentity Identity => inner.Identity;
        public bool IsConnected => inner.IsConnected;
        public event Action<RpcIncoming>? Incoming { add => inner.Incoming += value; remove => inner.Incoming -= value; }
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            if (method == "turn/steer")
            {
                var args = JsonSerializer.SerializeToElement(parameters); Steers.Enqueue(args);
                var saved = snapshot == null ? null : await snapshot();
                var id = Guid.Parse(args.GetProperty("clientUserMessageId").GetString()!);
                SteerDispatchProofs.Enqueue(saved?.Operations.Any(x => x.Id == id && x.Stage == OperationStage.Dispatching) == true
                    && saved.ToolInvocations.Any(x => x.OperationId == id && x.Stage == ToolInvocationStage.Executing));
                if (FailSteerAfterWrite) throw new RpcTransportException(true, "synthetic_written_steer_reply_lost");
            }
            return await inner.RequestAsync(method, parameters, ct);
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct) => inner.NotifyAsync(method, parameters, ct);
        public Task RespondAsync(JsonElement id, object result, CancellationToken ct) => inner.RespondAsync(id, result, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed record SourceReceipt(long Sequence, DateTimeOffset At);
    private static SourceReceipt Receipt(OperationRecord operation)
    {
        using var value = JsonDocument.Parse(operation.Receipt!);
        return new(value.RootElement.GetProperty("Sequence").GetInt64(), value.RootElement.GetProperty("observedAt").GetDateTimeOffset());
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate, Harness h, string phase)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until)
            {
                h.WriteEvidence("timeout-" + phase, await h.Host.ReadAsync());
                throw new TimeoutException("synthetic_owned_order_" + phase);
            }
            await Task.Delay(20);
        }
    }
}
