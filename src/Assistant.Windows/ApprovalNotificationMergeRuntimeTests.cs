using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Real host/approval worker, local command routing, ledger and channel delivery.
// The owned peer, HTTP channel and model are synthetic; no real task is opened.
internal static class ApprovalNotificationMergeRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--approval-notification-merge-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit new synthetic directory required.");
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
            File.WriteAllText(Path.Combine(directory, "approval-notification-merge-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual host with synthetic owned RPC, model, HTTP and protected ledger"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await AcceptedAndMultipleAsync(Path.Combine(directory, "accepted"), Check);
        await UnknownAndRestartAsync(Path.Combine(directory, "unknown"), Check);
        await CancelWhileSendingAsync(Path.Combine(directory, "cancel-sending"), Check);
        await PausedAndBudgetAsync(Path.Combine(directory, "paused-budget"), Check);
    }

    private static async Task AcceptedAndMultipleAsync(string directory, Action<string, bool> check)
    {
        await using var owned = await OwnedFixture.CreateAsync(directory);
        var f = owned.Host;
        await owned.SubscribeAsync("subscribe-one");
        await owned.SubscribeAsync("subscribe-two");
        check("multiple_subscriptions_are_durable_before_the_approval_arrives",
            (await f.ReadAsync()).WorkItems.Count(w => w.Kind == TrackedWorkKind.Subscription) == 2
            && !(await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification));
        var approval = owned.EmitApproval(801);
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            return Runs(state, approval).Count() == 2 && Runs(state, approval).All(r => r.Stage == CommitmentRunStage.Completed)
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.RequestId == Reference(approval);
        });
        var state = await f.ReadAsync();
        var detail = Detail(state, approval);
        check("one_necessary_detail_outbox_satisfies_both_subscription_runs",
            Runs(state, approval).All(r => r.SharedDecisionEventId == detail.Id && r.NotificationEventId == null && r.NotificationDraftId == null)
            && state.NotificationDrafts.All(d => d.WorkRunId == null)
            && state.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
            && Delivered(state, detail.Id) && !detail.IsDefaultTaskNotification && f.Sent.Count(b => b.Contains(Reference(approval))) == 1);
        check("merged_detail_retains_exact_target_reference_and_presented_context",
            detail.Task == owned.Target && detail.Body.Contains(Reference(approval), StringComparison.Ordinal)
            && detail.Body.Contains(owned.Thread, StringComparison.Ordinal) && detail.Body.Contains(owned.Turn, StringComparison.Ordinal)
            && detail.Body.Contains(owned.Cwd, StringComparison.Ordinal)
            && f.Runtime.OwnedRuntime!.Presented(detail.Id)?.Request.Token == approval.Token
            && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.SourceEventId == detail.Id);
        check("completed_delivery_runs_do_not_approve_or_finish_the_task",
            state.WorkItems.All(w => w.Stage == TrackedWorkStage.Active)
            && f.Runtime.OwnedRuntime!.LoadedSession("demo")!.Client.PendingApprovals().Single().State == ApprovalState.Pending
            && f.Runtime.OwnedRuntime.Tasks().Single().Snapshot.State == OwnedTaskState.WaitingApproval
            && owned.Rpc.Responses == 0 && !state.Operations.Any(o => o.Action == "respond_approval"));
        await owned.SubscribeAsync("subscribe-after-accepted");
        await WaitAsync(async () => Runs(await f.ReadAsync(), approval).Count(r => r.Stage == CommitmentRunStage.Completed) == 3);
        await Task.Delay(1100);
        state = await f.ReadAsync();
        check("new_subscription_after_acceptance_links_existing_detail_without_sending_again",
            Runs(state, approval).Count() == 3 && Runs(state, approval).All(r => r.SharedDecisionEventId == detail.Id)
            && state.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
            && f.Sent.Count(b => b.Contains(Reference(approval))) == 1
            && state.WorkItems.All(w => w.Stage == TrackedWorkStage.Active));
        check("shared_required_notice_spends_no_subscription_budget_and_uses_no_model_or_control",
            !state.CommitmentBudgetReservations.Any(r => r.Kind == CommitmentBudgetKind.Notification)
            && state.Operations.Length == 5 && owned.Rpc.Responses == 0
            && owned.Rpc.Methods.Count == owned.InitialRpcMethods && f.Script.Calls == owned.InitialModelCalls
            && f.Runtime.LastError == null);
    }

    private static async Task UnknownAndRestartAsync(string directory, Action<string, bool> check)
    {
        var owned = await OwnedFixture.CreateAsync(directory);
        var f = owned.Host;
        ClawBinding binding;
        Guid eventId;
        PendingApproval approval;
        try
        {
            await owned.SubscribeAsync("unknown-subscribe-one");
            f.SendOutcome = "{}";
            approval = owned.EmitApproval(802);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Unknown));
            f.SendOutcome = """{"ret":0}""";
            await owned.SubscribeAsync("unknown-subscribe-two");
            await WaitAsync(async () => Runs(await f.ReadAsync(), approval).Count() == 2);
            await Task.Delay(1100);
            var state = await f.ReadAsync();
            var detail = Detail(state, approval);
            eventId = detail.Id;
            check("unknown_detail_is_shared_without_a_new_short_notice_or_retry",
                Runs(state, approval).All(r => r.Stage == CommitmentRunStage.NotificationPending
                    && r.SharedDecisionEventId == detail.Id && r.NotificationEventId == null && r.NotificationDraftId == null)
                && state.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
                && state.Outbox.Single(o => o.NotificationEventId == detail.Id).Stage == OutboxStage.Unknown
                && state.Outbox.Single(o => o.NotificationEventId == detail.Id).SendAttempts == 1
                && f.Sent.Count(b => b.Contains(Reference(approval))) == 1);
            check("unknown_shared_delivery_never_creates_actionable_presented_approval",
                f.Runtime.OwnedRuntime!.Presented(detail.Id) == null
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval == null
                && owned.Rpc.Responses == 0 && state.WorkItems.All(w => w.Stage == TrackedWorkStage.Active));
            binding = f.Settings.WeChat.ReadBinding()!;
        }
        finally { await owned.DisposeAsync(); }

        // Reopen the real protected journal after all first-host objects stopped.
        // The original connection factory must remain unused during discovery.
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(owned.HostDirectory,
            modelEnabled: false, savedBinding: binding, ownedOptions: owned.Options);
        await InputAsync(reopened, "reopened-status", "状态");
        await Task.Delay(1100);
        var restored = await reopened.ReadAsync();
        check("reopened_ledger_preserves_shared_unknown_receipt_without_replay",
            Runs(restored, approval).Count() == 2 && Runs(restored, approval).All(r => r.SharedDecisionEventId == eventId
                && r.Stage == CommitmentRunStage.NotificationPending)
            && restored.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
            && restored.Outbox.Single(o => o.NotificationEventId == eventId).Stage == OutboxStage.Unknown
            && restored.Outbox.Single(o => o.NotificationEventId == eventId).SendAttempts == 1
            && !reopened.Sent.Any(b => b.Contains(Reference(approval))));
        check("restart_discovers_facts_without_connecting_or_reauthorizing_unknown_approval",
            owned.Connections == 1 && owned.Rpc.Disposed && reopened.Script.Calls == 0
            && reopened.Runtime.OwnedRuntime!.Tasks().Count == 0
            && (await reopened.Ledger.ReadConversationContextAsync(reopened.Runtime.Identity!)).PendingApproval == null
            && !restored.Operations.Any(o => o.Action == "respond_approval") && owned.Rpc.Responses == 0
            && reopened.Runtime.LastError == null);
    }

    private static async Task CancelWhileSendingAsync(string directory, Action<string, bool> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage?> Intercept(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string body = json.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                    .GetProperty("text_item").GetProperty("text").GetString()!;
                if (body.Contains("这项自建任务正在等审批", StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
            return null; // Delegate to the fixture's existing fake accepted response and send recording.
        }
        await using var owned = await OwnedFixture.CreateAsync(directory, channelOverride: Intercept);
        var f = owned.Host;
        var work = await owned.SubscribeAsync("cancel-subscribe");
        var approval = owned.EmitApproval(803);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitAsync(async () => Runs(await f.ReadAsync(), approval).Any());
            // The local cancellation can commit while the independent sender is held.
            await f.InputAsync("cancel-subscription", "取消订阅 " + work.Id);
            await WaitAsync(async () => (await f.ReadAsync()).WorkItems.Single(w => w.Id == work.Id).Stage == TrackedWorkStage.Cancelled);
            var during = await f.ReadAsync();
            var detail = Detail(during, approval);
            check("cancelling_subscription_during_send_keeps_shared_necessary_outbox",
                Runs(during, approval).Single().Stage == CommitmentRunStage.Cancelled
                && Runs(during, approval).Single().SharedDecisionEventId == detail.Id
                && during.Outbox.Single(o => o.NotificationEventId == detail.Id).Stage == OutboxStage.Sending
                && !detail.IsDefaultTaskNotification);
        }
        finally { release.TrySetResult(); }
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-subscription");
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            return Delivered(state, Detail(state, approval).Id)
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.RequestId == Reference(approval);
        });
        var saved = await f.ReadAsync();
        var accepted = Detail(saved, approval);
        check("cancelled_subscription_does_not_suppress_detailed_approval_or_presented_binding",
            saved.WorkItems.Single().Stage == TrackedWorkStage.Cancelled
            && Runs(saved, approval).Single().Stage == CommitmentRunStage.Cancelled
            && f.Runtime.OwnedRuntime!.Presented(accepted.Id)?.Request.Token == approval.Token
            && f.Sent.Count(b => b.Contains(Reference(approval))) == 1 && owned.Rpc.Responses == 0);
        check("subscription_cancellation_is_not_an_approval_decline_or_task_interrupt",
            owned.Rpc.Methods.Count == owned.InitialRpcMethods && f.Script.Calls == owned.InitialModelCalls
            && saved.Operations.Count(o => o.Action == "change_commitment") == 1
            && !saved.Operations.Any(o => o.Action is "respond_approval" or "interrupt_owned_task")
            && f.Runtime.OwnedRuntime!.LoadedSession("demo")!.Client.PendingApprovals().Single().State == ApprovalState.Pending
            && f.Runtime.LastError == null);
    }

    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset time) => Interlocked.Exchange(ref ticks, time.UtcTicks);
    }
    private static async Task PausedAndBudgetAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        await using var owned = await OwnedFixture.CreateAsync(directory, clock, new()
        { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 1, MaximumNotificationsPerDay = 1 } });
        var f = owned.Host;
        var work = await owned.SubscribeAsync("paused-subscribe");
        await InputAsync(f, "pause-subscription", "暂停订阅 " + work.Id);
        await InputAsync(f, "spend-budget", "一分钟后提醒我查看合成预算结果");
        clock.Set((await f.ReadAsync()).Schedules.Single().DueAtUtc.AddSeconds(1));
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification && o.Stage == OutboxStage.Accepted));
        var budget = await f.Ledger.GetCommitmentBudgetStatusAsync(f.Runtime.Identity!);
        check("ordinary_reminder_fills_budget_while_subscription_is_paused",
            budget.Windows.Where(w => w.Kind == CommitmentBudgetKind.Notification).All(w => w.Used == w.Limit)
            && (await f.ReadAsync()).WorkItems.Single(w => w.Id == work.Id).Stage == TrackedWorkStage.Paused);
        var approval = owned.EmitApproval(804);
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            return state.Events.Any(e => e.ExternalEventKey == Key(approval) && Delivered(state, e.Id))
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingApproval?.RequestId == Reference(approval);
        });
        var saved = await f.ReadAsync();
        var detail = Detail(saved, approval);
        check("paused_subscription_and_exhausted_budget_cannot_hide_required_approval",
            Delivered(saved, detail.Id) && !detail.IsDefaultTaskNotification
            && saved.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 2
            && f.Runtime.OwnedRuntime!.Presented(detail.Id)?.Request.Token == approval.Token
            && saved.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && owned.Rpc.Responses == 0);
        await InputAsync(f, "resume-subscription", "恢复订阅 " + work.Id);
        await WaitAsync(async () => Runs(await f.ReadAsync(), approval).Any(r => r.Stage == CommitmentRunStage.Completed));
        await Task.Delay(1100);
        saved = await f.ReadAsync();
        check("resuming_subscription_links_already_accepted_detail_without_spending_budget",
            Runs(saved, approval).Single().SharedDecisionEventId == detail.Id
            && saved.WorkItems.Single(w => w.Id == work.Id).Stage == TrackedWorkStage.Active
            && saved.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 2
            && saved.CommitmentBudgetReservations.Count(r => r.Kind == CommitmentBudgetKind.Notification) == 1
            && f.Sent.Count(b => b.Contains(Reference(approval))) == 1
            && owned.Rpc.Responses == 0 && f.Script.Calls == owned.InitialModelCalls && f.Runtime.LastError == null);
    }

    private sealed class OwnedFixture : IAsyncDisposable
    {
        internal DurableRuntimeTests.Fixture Host = null!;
        internal OwnedHostRuntimeTests.FakeRpc Rpc = null!;
        internal OwnedHostOptions Options = null!;
        internal string HostDirectory = "", Thread = "", Turn = "", Cwd = "";
        internal TaskReference Target = null!;
        internal int Connections, InitialRpcMethods, InitialModelCalls;
        internal static async Task<OwnedFixture> CreateAsync(string directory, IClock? clock = null, LedgerOptions? limits = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? channelOverride = null)
        {
            var fixture = new OwnedFixture { HostDirectory = Path.Combine(directory, "host") };
            var profile = OwnedHostRuntimeTests.Profile(fixture.HostDirectory, verified: true);
            fixture.Options = new(Path.Combine(directory, "owned"), [profile], (configured, _) =>
            {
                fixture.Connections++;
                fixture.Rpc = new(configured.Configuration, async () =>
                    (await fixture.Host.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
                return Task.FromResult<IJsonRpcConnection>(fixture.Rpc);
            });
            fixture.Host = await DurableRuntimeTests.Fixture.CreateAsync(fixture.HostDirectory, ownedOptions: fixture.Options,
                clock: clock, channelRequestOverride: channelOverride, ledgerOptions: limits);
            try
            {
                fixture.Host.Settings.ChatGpt.TaskControlEnabled = true;
                await OwnedHostRuntimeTests.SendToolAsync(fixture.Host, "create", "新建 demo",
                    _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
                var runtime = fixture.Host.Runtime.OwnedRuntime!;
                fixture.Thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
                string text = "继续 " + fixture.Thread + " 检查合成审批通知合并";
                await OwnedHostRuntimeTests.SendToolAsync(fixture.Host, "start", text,
                    _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), text));
                var task = runtime.Tasks().Single();
                fixture.Turn = task.Snapshot.CurrentTurnId!; fixture.Cwd = task.Snapshot.Cwd;
                fixture.Target = new(task.Handle.Identity.HostId, fixture.Thread, fixture.Turn, task.Handle.Identity.ProfileId);
                fixture.Rpc.Emit("turn/started", new { threadId = fixture.Thread,
                    turn = OwnedHostRuntimeTests.FakeRpc.Turn(fixture.Turn, "inProgress") });
                await WaitAsync(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
                fixture.Host.Settings.ChatGpt.Enabled = false;
                fixture.InitialModelCalls = fixture.Host.Script.Calls;
                fixture.InitialRpcMethods = fixture.Rpc.Methods.Count;
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<TrackedWorkItem> SubscribeAsync(string sourceId)
        {
            await InputAsync(Host, sourceId, Thread + "需要决定时叫我");
            return (await Host.ReadAsync()).WorkItems.Single(w => w.SourceMessageId == WeChatText.Hash(sourceId));
        }
        internal PendingApproval EmitApproval(int requestId)
        {
            Rpc.Emit("item/commandExecution/requestApproval", new
            {
                threadId = Thread, turnId = Turn, itemId = "synthetic-command-" + requestId,
                command = "echo SYNTHETIC_APPROVAL_MERGE", cwd = Cwd, startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, requestId);
            return Host.Runtime.OwnedRuntime!.LoadedSession("demo")!.Client.PendingApprovals().Single();
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
    private static string Key(PendingApproval approval) => OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token);
    private static string Reference(PendingApproval approval) => OwnedControlToolAdapter.ApprovalReference("demo", approval);
    private static ConversationEvent Detail(ConversationSnapshot state, PendingApproval approval)
        => state.Events.Single(e => e.ExternalEventKey == Key(approval));
    private static IEnumerable<CommitmentRun> Runs(ConversationSnapshot state, PendingApproval approval)
        => state.CommitmentRuns.Where(r => state.Events.Any(e => e.Id == r.SharedDecisionEventId
            && e.ExternalEventKey == Key(approval) && e.RequiredDecision is { } decision && decision.DecisionToken == approval.Token
            && decision.ProviderScope == "demo" && decision.ConnectionEpoch == approval.ConnectionEpoch
            && decision.ExpiresAt == approval.ExpiresAt
            && e.Task == new TaskReference(approval.Task.HostId, approval.Task.ThreadId, approval.TurnId, approval.Task.ProfileId)));
    private static bool Delivered(ConversationSnapshot state, Guid eventId)
    {
        var messages = state.Outbox.Where(o => o.NotificationEventId == eventId).ToArray();
        return messages.Length > 0 && messages.All(o => o.Stage == OutboxStage.Accepted);
    }
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
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic shared approval condition not reached.");
            await Task.Delay(20);
        }
    }
}
