using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Exercises the real durable host, delivery worker, owned client and protected
// ledger. Only the clock, model, HTTP transport and JSON-RPC peer are synthetic.
internal static class CommitmentBudgetRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-budget-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit new synthetic directory required.");
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
        internal void Set(DateTimeOffset time)
        {
            if (time < UtcNow) throw new InvalidOperationException("Synthetic clock must be monotonic.");
            Interlocked.Exchange(ref ticks, time.UtcTicks);
        }
    }
    private static LedgerOptions Limits() => new()
    {
        CommitmentBudgets = new()
        {
            MaximumNotificationsPerMinute = 1, MaximumNotificationsPerDay = 20,
            MaximumReadChecksInFlight = 1, MaximumReadChecksPerHour = 20
        }
    };

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("New absolute WPF test directory required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "commitment-budget-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(x => x), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual host with synthetic clock, model, HTTP, protected files and fake owned JSON-RPC"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await SharedNotificationsAsync(Path.Combine(directory, "shared-notifications"), Check);
        await UnknownConsumesBudgetAsync(Path.Combine(directory, "unknown-notification"), Check);
        await ActualReadDrainAsync(Path.Combine(directory, "owned-read-drain"), Check);
    }

    private static async Task CreatePairAsync(DurableRuntimeTests.Fixture f, Clock clock)
    {
        // Both natural commands name exactly the same future local minute.
        string when = TimeZoneInfo.ConvertTime(clock.UtcNow.AddMinutes(3), TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        await InputAsync(f, "create-first", when + "提醒我查看合成甲结果");
        await InputAsync(f, "create-second", when + "提醒我查看合成乙结果");
        var schedules = (await f.ReadAsync()).Schedules;
        if (schedules.Length != 2 || schedules[0].DueAtUtc != schedules[1].DueAtUtc)
            throw new InvalidOperationException("Both natural requests must persist the same due instant.");
        clock.Set(schedules[0].DueAtUtc.AddSeconds(1));
    }

    private static async Task SharedNotificationsAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            clock: clock, ledgerOptions: Limits());
        await CreatePairAsync(f, clock);
        await WaitAsync(async () =>
        {
            var snapshot = await f.ReadAsync();
            return snapshot.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 2
                && snapshot.Outbox.Count(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted) == 1;
        });
        var state = await f.ReadAsync();
        var accepted = state.Outbox.Single(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted);
        var waiting = state.Outbox.Single(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Pending);
        var budget = await f.Ledger.GetCommitmentBudgetStatusAsync(f.Runtime.Identity!);
        check("distinct_natural_reminders_share_one_minute_budget",
            state.WorkItems.Length == 2 && state.CommitmentRuns.Length == 2
            && state.Operations.Length == 2 && state.Operations.All(o => o.Stage == OperationStage.Completed)
            && Minute(budget).Used == 1 && Minute(budget).Limit == 1 && Day(budget).Used == 1);
        check("deferred_notice_keeps_zero_attempts_and_durable_pending_state",
            waiting.SendAttempts == 0 && state.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && state.CommitmentRuns.Single(r => r.NotificationEventId == waiting.NotificationEventId).Stage == CommitmentRunStage.NotificationPending
            && f.Sent.Count(body => body == accepted.Body) == 1 && !f.Sent.Contains(waiting.Body));
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        check("desktop_and_budget_api_show_actual_pending_and_accepted_results",
            desktop.Messages.Single(m => m.Id == waiting.Id.ToString("N")).State == "已经准备，尚未发送"
            && desktop.Messages.Single(m => m.Id == accepted.Id.ToString("N")).State.Contains("手机送达未核实", StringComparison.Ordinal)
            && desktop.Commitments!.Count(c => c.State == "已触发，待渠道受理") == 1
            && desktop.Capabilities.Contains("提醒与订阅发送（最近 1 分钟）：1/1", StringComparison.Ordinal));
        await InputAsync(f, "status-during-budget", "提醒状态");
        await InputAsync(f, "ordinary-status-during-budget", "状态");
        state = await f.ReadAsync();
        check("budget_deferred_notification_does_not_block_current_status_replies",
            state.Outbox.Single(o => o.Id == waiting.Id).Stage == OutboxStage.Pending
            && state.Outbox.Single(o => o.Id == waiting.Id).SendAttempts == 0
            && f.Sent.Any(body => body.Contains("提醒与订阅发送（最近 1 分钟）：1/1", StringComparison.Ordinal))
            && state.Operations.Length == 2 && f.Script.Calls == 0 && f.Queue.Entries.Count == 0);
        var catchUp = ExactCatchUpPage(state, "ordinary-status-during-budget", waiting);
        var olderPages = state.CatchUpBatches.Where(batch => batch.Id != catchUp.Batch.Id)
            .SelectMany(batch => batch.Members).Select(member => member.CatchUpOutboxId).Distinct().ToArray();
        if (olderPages.Length != 1 || olderPages.Any(id => state.Outbox.Single(o => o.Id == id).Stage != OutboxStage.Superseded
            || state.Outbox.Single(o => o.Id == id).SendAttempts != 0))
            throw new InvalidOperationException("The newer authenticated input must supersede the first unattempted catch-up page.");
        SaveCatchUpObservation(directory, f, state, waiting, catchUp.Page.Id, budget);
        clock.Advance(TimeSpan.FromSeconds(61));
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Single(o => o.Id == catchUp.Page.Id).Stage == OutboxStage.Accepted);
        await Task.Delay(1100);
        state = await f.ReadAsync();
        budget = await f.Ledger.GetCommitmentBudgetStatusAsync(f.Runtime.Identity!);
        desktop = await f.Runtime.ReadWorkspaceAsync();
        SaveCatchUpObservation(directory, f, state, waiting, catchUp.Page.Id, budget);
        check("next_minute_accepts_exact_current_catchup_without_rewriting_original_notice",
            f.Sent.Count(body => body == accepted.Body) == 1 && !f.Sent.Contains(waiting.Body)
            && f.Sent.Count(body => body == catchUp.Page.Body) == 1
            && state.Outbox.Single(o => o.Id == waiting.Id) == waiting
            && state.Outbox.Single(o => o.Id == catchUp.Page.Id).Stage == OutboxStage.Accepted
            && state.Outbox.Single(o => o.Id == catchUp.Page.Id).SendAttempts == 1
            && ExactAcceptedCoverage(state, waiting, catchUp.Page.Id)
            && state.Outbox.Count(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted) == 2
            && state.WorkItems.All(w => w.Stage == TrackedWorkStage.Completed)
            && desktop.Commitments!.All(c => c.State == "本次已结束，送达须另核对")
            && desktop.Messages.Single(m => m.Id == waiting.Id.ToString("N")).State == "原页尚未直接发送；完整内容另有补报受理记录");
        check("rolling_minute_and_daily_status_preserve_both_charges",
            Minute(budget).Used == 1 && Day(budget).Used == 2
            && state.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 2
            && state.CommitmentBudgetReservations.Single(r => r.ResourceId == accepted.Id).CatchUpBatchId == null
            && state.CommitmentBudgetReservations.Single(r => r.ResourceId == catchUp.Page.Id).CatchUpBatchId == catchUp.Batch.Id
            && state.CommitmentRuns.All(r => r.DeliveryAttempts.Length == 1)
            && f.Runtime.LastError == null && f.Script.Calls == 0);
    }

    private static async Task UnknownConsumesBudgetAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            clock: clock, ledgerOptions: Limits());
        // Creation replies must still be accepted. Change the synthetic channel
        // only after both durable creations, before advancing the test clock.
        string when = TimeZoneInfo.ConvertTime(clock.UtcNow.AddMinutes(3), TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        await InputAsync(f, "unknown-create-one", when + "提醒我检查未知甲结果");
        await InputAsync(f, "unknown-create-two", when + "提醒我检查未知乙结果");
        f.SendOutcome = "{}";
        clock.Set((await f.ReadAsync()).Schedules.Max(s => s.DueAtUtc).AddSeconds(1));
        await WaitAsync(async () =>
        {
            var saved = await f.ReadAsync();
            return saved.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 2
                && saved.Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Unknown);
        });
        f.SendOutcome = """{"ret":0}""";
        var state = await f.ReadAsync();
        var unknown = state.Outbox.Single(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Unknown);
        var pending = state.Outbox.Single(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Pending);
        var identity = f.Runtime.Identity!;
        var budget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
        check("unknown_attempt_spends_shared_budget_without_marking_completed",
            Minute(budget).Used == 1 && Day(budget).Used == 1 && unknown.SendAttempts == 1
            && pending.SendAttempts == 0 && state.WorkItems.All(w => w.Stage != TrackedWorkStage.Completed));
        await InputAsync(f, "new-current-context", "提醒状态");
        await f.Runtime.StopSessionAsync();
        await f.Runtime.ActivateAsync(f.Settings.WeChat.ReadBinding()!, default);
        await InputAsync(f, "new-current-after-reactivation", "状态");
        await Task.Delay(1100);
        state = await f.ReadAsync();
        check("new_message_and_same_binding_reactivation_do_not_bypass_spent_budget",
            state.Outbox.Single(o => o.Id == unknown.Id).Stage == OutboxStage.Unknown
            && state.Outbox.Single(o => o.Id == pending.Id).Stage == OutboxStage.Pending
            && state.Outbox.Single(o => o.Id == pending.Id).SendAttempts == 0
            && f.Sent.Count(body => body == unknown.Body) == 1 && !f.Sent.Contains(pending.Body)
            && (await f.Ledger.GetCommitmentBudgetStatusAsync(identity)).Windows.Single(w =>
                w.Kind == CommitmentBudgetKind.Notification && w.Window == TimeSpan.FromMinutes(1)).Used == 1);
        var desktop = await f.Runtime.ReadWorkspaceAsync();
        check("desktop_keeps_unknown_distinct_from_accepted_and_pending",
            desktop.Messages.Single(m => m.Id == unknown.Id.ToString("N")).State == "发送结果未知，不自动重发"
            && desktop.Messages.Single(m => m.Id == pending.Id.ToString("N")).State == "已经准备，尚未发送");
        var catchUp = ExactCatchUpPage(state, "new-current-after-reactivation", pending);
        SaveCatchUpObservation(directory, f, state, pending, catchUp.Page.Id, budget);
        clock.Advance(TimeSpan.FromSeconds(61));
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Single(o => o.Id == catchUp.Page.Id).Stage == OutboxStage.Accepted);
        await Task.Delay(1100);
        state = await f.ReadAsync();
        budget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
        SaveCatchUpObservation(directory, f, state, pending, catchUp.Page.Id, budget);
        check("window_expiry_accepts_exact_pending_source_coverage_and_never_retries_unknown",
            state.Outbox.Single(o => o.Id == unknown.Id) == unknown
            && state.Outbox.Single(o => o.Id == pending.Id) == pending
            && state.Outbox.Single(o => o.Id == catchUp.Page.Id).Stage == OutboxStage.Accepted
            && state.Outbox.Single(o => o.Id == catchUp.Page.Id).SendAttempts == 1
            && ExactAcceptedCoverage(state, pending, catchUp.Page.Id)
            && state.CatchUpBatches.SelectMany(b => b.Members).All(m => m.SourceEventId != unknown.NotificationEventId)
            && f.Sent.Count(body => body == unknown.Body) == 1 && !f.Sent.Contains(pending.Body)
            && f.Sent.Count(body => body == catchUp.Page.Body) == 1
            && Day(budget).Used == 2 && Minute(budget).Used == 1 && state.Operations.Length == 2
            && state.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 2
            && state.CommitmentBudgetReservations.Single(r => r.ResourceId == unknown.Id).CatchUpBatchId == null
            && state.CommitmentBudgetReservations.Single(r => r.ResourceId == catchUp.Page.Id).CatchUpBatchId == catchUp.Batch.Id
            && state.CommitmentRuns.Single(r => r.NotificationEventId == unknown.NotificationEventId).Stage == CommitmentRunStage.NotificationPending
            && state.CommitmentRuns.All(r => r.DeliveryAttempts.Length == 1)
            && f.Script.Calls == 0 && f.Queue.Entries.Count == 0 && f.Runtime.LastError == null);
    }

    private static (CatchUpBatch Batch, OutboxMessage Page) ExactCatchUpPage(ConversationSnapshot snapshot,
        string sourceMessageId, OutboxMessage original)
    {
        var window = snapshot.CatchUpWindows.Single(w => w.SourceMessageId == WeChatText.Hash(sourceMessageId));
        var batch = snapshot.CatchUpBatches.Single(b => b.WindowId == window.Id);
        var member = batch.Members.Single();
        var span = member.Sources.Single();
        var page = snapshot.Outbox.Single(o => o.Id == member.CatchUpOutboxId);
        if (window.Stage != CatchUpWindowStage.Prepared || batch.ManifestVersion != 2
            || member.SourceEventId != original.NotificationEventId || span.OutboxId != original.Id
            || span.Start != 0 || span.Length != original.Body.Length || span.SendAttempts != 0
            || page.Stage != OutboxStage.Pending || page.SendAttempts != 0
            || !page.Body.EndsWith(original.Body, StringComparison.Ordinal))
            throw new InvalidOperationException("Expected the exact current input's unattempted full-span catch-up page.");
        return (batch, page);
    }

    private static bool ExactAcceptedCoverage(ConversationSnapshot snapshot, OutboxMessage original, Guid acceptedPageId)
    {
        var progress = DurableConversationLedger.GetCatchUpProgress(snapshot).Single(p => p.SourceEventId == original.NotificationEventId);
        var source = progress.Sources.Single(s => s.OutboxId == original.Id);
        var run = snapshot.CommitmentRuns.Single(r => r.NotificationEventId == original.NotificationEventId);
        return source.TotalCharacters == original.Body.Length && source.AcceptedCharacters == original.Body.Length
            && source.PendingCharacters == 0 && !source.HasSending && !source.HasUnknown
            && progress.RemainingCharacters == 0 && run.Stage == CommitmentRunStage.Completed
            && run.CatchUpDeliveryOutboxId == acceptedPageId && run.DeliveryAttempts.Length == 1;
    }

    private static void SaveCatchUpObservation(string directory, DurableRuntimeTests.Fixture fixture,
        ConversationSnapshot snapshot, OutboxMessage original, Guid acceptedPageId, CommitmentBudgetStatus budget)
    {
        File.WriteAllText(Path.Combine(directory, "synthetic-catchup-budget-observation.json"), JsonSerializer.Serialize(new
        {
            syntheticOnly = true, originalId = original.Id, acceptedPageId,
            outbox = snapshot.Outbox.Where(o => o.Kind == OutboxKind.Notification).ToArray(),
            snapshot.CatchUpWindows, snapshot.CatchUpBatches, snapshot.CommitmentRuns,
            snapshot.CommitmentBudgetReservations, progress = DurableConversationLedger.GetCatchUpProgress(snapshot),
            wireText = fixture.Sent.ToArray(), budget
        }, Settings.Json));
    }

    private static async Task ActualReadDrainAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        var profile = OwnedHostRuntimeTests.Profile(Path.Combine(directory, "fixture"), verified: true);
        IgnoringCancellationRpc? rpc = null;
        DurableRuntimeTests.Fixture? fixture = null;
        int connections = 0;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            connections++;
            rpc = new(new(configured.Configuration, async () =>
                (await fixture!.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching)));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        });
        await using var f = fixture = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"),
            ownedOptions: options, clock: clock, ledgerOptions: Limits());
        f.Settings.ChatGpt.TaskControlEnabled = true;
        check("owned_budget_fixture_construction_never_opens_a_connection", connections == 0 && rpc == null);
        await OwnedHostRuntimeTests.SendToolAsync(f, "owned-create", "新建 demo",
            _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = f.Runtime.OwnedRuntime!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        string text = "继续 " + thread + " 检查合成只读停机";
        await OwnedHostRuntimeTests.SendToolAsync(f, "owned-start", text,
            _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), text));
        var task = runtime.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        rpc!.Inner.SetThread(thread, task.Snapshot.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(turn, "inProgress"));
        rpc.Inner.Emit("turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, "inProgress") });
        await WaitAsync(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
        f.Settings.ChatGpt.Enabled = false;
        int modelCalls = f.Script.Calls;
        await InputAsync(f, "owned-inactivity", thread + "一分钟没动静检查一下");
        var identity = f.Runtime.Identity!;
        var created = await f.ReadAsync();
        check("actual_owned_inactivity_subscription_is_bound_to_exact_existing_turn",
            created.WorkItems.Single().Task == new TaskReference(task.Handle.Identity.HostId, thread, turn, task.Handle.Identity.ProfileId)
            && created.Subscriptions.Single().Condition == SubscriptionCondition.NoActivity && created.Operations.Length == 3);
        clock.Set(created.Subscriptions.Single().NextCheckAt.AddSeconds(1));
        Task? stopping = null;
        try
        {
            await rpc.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var inRead = await f.Ledger.ReadConversationAsync(identity);
            var budget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
            check("real_read_await_holds_persisted_inflight_slot_and_one_attempt",
                inRead.CommitmentRuns.Single().Stage == CommitmentRunStage.Checking
                && inRead.CommitmentRuns.Single().CheckAttempts == 1 && budget.ReadChecksInFlight == 1
                && ReadHour(budget).Used == 1 && rpc.ActiveReads == 1);
            stopping = f.Runtime.StopSessionAsync();
            await rpc.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200); // Give an incorrectly detached outer await time to finish.
            var draining = await f.Ledger.ReadConversationAsync(identity);
            budget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
            check("stop_waits_for_actual_ignoring_cancellation_read_to_end",
                !stopping.IsCompleted && rpc.ActiveReads == 1 && !rpc.Inner.Disposed && f.Runtime.Identity == null);
            check("cancel_does_not_release_core_inflight_or_clear_the_active_claim",
                budget.ReadChecksInFlight == 1 && draining.CommitmentRuns.Single().Stage == CommitmentRunStage.Checking
                && draining.CommitmentRuns.Single().CheckClaimId != null
                && draining.CommitmentBudgetReservations.Single(r => r.Kind == CommitmentBudgetKind.ReadCheck).ReleasedAt == null);
        }
        finally
        {
            // Always let our fake read finish, including on a failed assertion.
            rpc.ReleaseRead.TrySetResult();
            await rpc.ReadFinished.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (stopping != null) await stopping.WaitAsync(TimeSpan.FromSeconds(8));
        }
        var stopped = await f.Ledger.ReadConversationAsync(identity);
        var afterBudget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
        var run = stopped.CommitmentRuns.Single();
        check("actual_read_completion_runs_finally_and_returns_unobserved_check_to_pending",
            afterBudget.ReadChecksInFlight == 0 && ReadHour(afterBudget).Used == 1
            && run.Stage == CommitmentRunStage.PendingCheck && run.CheckClaimId == null && run.CheckAttempts == 1
            && stopped.CommitmentBudgetReservations.Single(r => r.Kind == CommitmentBudgetKind.ReadCheck).ReleasedAt != null
            && rpc.Inner.Disposed && rpc.ActiveReads == 0 && rpc.ReadCalls == 1);
        check("cancelled_late_read_does_not_create_notification_or_new_control",
            !stopped.NotificationDrafts.Any(d => d.WorkRunId != null) && stopped.Operations.Length == 3
            && rpc.Inner.Responses == 0 && f.Script.Calls == modelCalls);
        await f.Runtime.ActivateAsync(f.Settings.WeChat.ReadBinding()!, default);
        check("same_binding_reactivation_keeps_read_budget_without_automatic_connection",
            f.Runtime.Identity == identity && connections == 1 && f.Runtime.OwnedRuntime != runtime
            && (await f.Ledger.GetCommitmentBudgetStatusAsync(identity)).ReadChecksInFlight == 0);
        clock.Advance(TimeSpan.FromSeconds(31));
        await WaitAsync(async () =>
        {
            var resumed = await f.ReadAsync();
            return resumed.CommitmentRuns.Single().CheckAttempts == 2
                && resumed.CommitmentRuns.Single().Stage == CommitmentRunStage.PendingCheck;
        });
        var resumedState = await f.ReadAsync();
        afterBudget = await f.Ledger.GetCommitmentBudgetStatusAsync(identity);
        check("same_process_reactivation_does_not_leave_checking_stuck_forever",
            resumedState.CommitmentRuns.Single().CheckClaimId == null && afterBudget.ReadChecksInFlight == 0
            && ReadHour(afterBudget).Used == 2 && connections == 1 && rpc.ReadCalls == 1
            && resumedState.Operations.Length == 3 && f.Script.Calls == modelCalls && f.Runtime.LastError == null);
    }

    private sealed class IgnoringCancellationRpc(OwnedHostRuntimeTests.FakeRpc inner) : IJsonRpcConnection
    {
        internal OwnedHostRuntimeTests.FakeRpc Inner => inner;
        public ConnectionIdentity Identity => inner.Identity;
        public bool IsConnected => inner.IsConnected;
        public event Action<RpcIncoming>? Incoming { add => inner.Incoming += value; remove => inner.Incoming -= value; }
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        internal readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeReads, readCalls;
        internal int ActiveReads => Volatile.Read(ref activeReads);
        internal int ReadCalls => Volatile.Read(ref readCalls);
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            if (method != "thread/read") return await inner.RequestAsync(method, parameters, ct);
            Interlocked.Increment(ref readCalls);
            Interlocked.Increment(ref activeReads);
            using var cancellation = ct.Register(() => CancellationObserved.TrySetResult());
            ReadEntered.TrySetResult();
            try
            {
                await ReleaseRead.Task; // Deliberately ignores cancellation until the test releases it.
                return await inner.RequestAsync(method, parameters, CancellationToken.None);
            }
            finally { Interlocked.Decrement(ref activeReads); ReadFinished.TrySetResult(); }
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct) => inner.NotifyAsync(method, parameters, ct);
        public Task RespondAsync(JsonElement requestId, object result, CancellationToken ct) => inner.RespondAsync(requestId, result, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static CommitmentBudgetWindow Minute(CommitmentBudgetStatus status) => status.Windows.Single(w =>
        w.Kind == CommitmentBudgetKind.Notification && w.Window == TimeSpan.FromMinutes(1));
    private static CommitmentBudgetWindow Day(CommitmentBudgetStatus status) => status.Windows.Single(w =>
        w.Kind == CommitmentBudgetKind.Notification && w.Window == TimeSpan.FromDays(1));
    private static CommitmentBudgetWindow ReadHour(CommitmentBudgetStatus status) => status.Windows.Single(w =>
        w.Kind == CommitmentBudgetKind.ReadCheck && w.Window == TimeSpan.FromHours(1));
    private static async Task InputAsync(DurableRuntimeTests.Fixture f, string id, string text)
    {
        await f.InputAsync(id, text);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, id);
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic commitment budget condition not reached.");
            await Task.Delay(20);
        }
    }
}
