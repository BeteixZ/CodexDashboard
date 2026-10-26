using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual host, protected ledger and production channel gates. Only identities,
// credentials, clock and HTTP peer are synthetic. Never delegates to a network.
internal static class ChannelWakeRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--channel-wake-runtime-test");
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
            File.WriteAllText(Path.Combine(directory, "channel-wake-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual host and channel with synthetic clock, HTTP and protected ledger"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await NoLegacyMirrorAsync(Path.Combine(directory, "no-legacy-mirror"), Check);
        await ApprovalShortWakeAsync(Path.Combine(directory, "approval-short-wake"), Check);
        await InputDuringRealGateAsync(Path.Combine(directory, "input-during-gate"), Check);
        await ActualSendDrainAsync(Path.Combine(directory, "actual-send-drain"), Check);
        await UnknownRestartAndRouteAsync(Path.Combine(directory, "unknown-restart"), Check);
        await BindingAndDisableAsync(Path.Combine(directory, "binding-disable"), Check);
        await QuietAndBudgetAsync(Path.Combine(directory, "quiet-budget"), Check);
        await ThrowingObserverAsync(Path.Combine(directory, "throwing-observer"), Check);
        await SettingsChangedDuringGateAsync(Path.Combine(directory, "settings-change-gate"), Check);
        await RouteInitializationFaultAsync(Path.Combine(directory, "route-persist-fault"), Check);
    }

    private static async Task NoLegacyMirrorAsync(string directory, Action<string, bool> check)
    {
        var summary = new CountingSummary();
        await using var f = await WakeFixture.CreateAsync(directory, summary: summary);
        var host = f.Host;
        var task = host.ObservedTask;
        task.Status = task.RecordedStatus = TaskStatusNames.Completed;
        task.FinalReport = PrivateBody;
        task.FinalReportTurnId = task.TurnId;
        task.FinalReportSource = "synthetic public final";
        task.CompletedAt = task.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        host.Channel.Observe(task);
        await InputAsync(host, "status-after-legacy-observe", "状态");
        check("durable_legacy_observe_does_not_start_summary_or_full_body_push",
            summary.Calls == 0 && f.Wire.Pushes.IsEmpty
            && !(await host.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification));
        await host.Channel.ObserveAsync(task);
        await WaitAsync(async () => (await host.ReadAsync()).Outbox.Any(o => o.Kind == OutboxKind.Notification
            && o.Stage == OutboxStage.Accepted));
        var state = await host.ReadAsync();
        check("accepted_claw_completion_never_gets_pushplus_mirrored",
            summary.Calls == 1 && f.Wire.Pushes.IsEmpty && host.Sent.Count(x => x == CountingSummary.Body) == 1
            && state.Outbox.Count(o => o.Kind == OutboxKind.Notification) == 1
            && state.Operations.IsEmpty && host.Script.Calls == 0 && host.Queue.Entries.Count == 0);
    }

    private const string PrivateBody = "SYNTHETIC_PRIVATE_COMPLETION_BODY_NOT_FOR_PUSHPLUS";
    private const string PushToken = "SYNTHETIC_CHANNEL_WAKE_ROUTE_ONE";
    private const string OtherPushToken = "SYNTHETIC_CHANNEL_WAKE_ROUTE_TWO";

    private sealed class Clock : IClock
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            Interlocked.Add(ref ticks, amount.Ticks);
        }
        internal void Set(DateTimeOffset time)
        {
            if (time < UtcNow) throw new InvalidOperationException("Synthetic clock must be monotonic.");
            Interlocked.Exchange(ref ticks, time.UtcTicks);
        }
    }

    private sealed record PushRequest(string Title, string Body, string RouteHash, long StartedAt);
    private sealed record ClawRequest(string ClientId, string Body);
    private sealed class Wire
    {
        internal readonly ConcurrentQueue<PushRequest> Pushes = new();
        internal readonly ConcurrentQueue<ClawRequest> ClawSends = new();
        internal string PushOutcome = """{"code":200,"data":"SYNTHETIC_WAKE_RECEIPT"}""";
        internal Func<PushRequest, CancellationToken, Task>? BeforePushReply;
        internal async Task<HttpResponseMessage?> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal) == true)
            {
                using var claw = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var message = claw.RootElement.GetProperty("msg");
                ClawSends.Enqueue(new(message.GetProperty("client_id").GetString()!,
                    message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!));
                return null; // The existing fixture still records and answers this fake Claw request.
            }
            if (request.RequestUri?.AbsoluteUri != "https://www.pushplus.plus/send") return null;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string token = body.RootElement.GetProperty("token").GetString()!;
            if (token is not (PushToken or OtherPushToken)
                || body.RootElement.GetProperty("channel").GetString() != "wechat"
                || body.RootElement.GetProperty("template").GetString() != "txt"
                || request.Headers.Authorization != null)
                throw new InvalidOperationException("Unexpected synthetic push route or wire shape.");
            var value = new PushRequest(body.RootElement.GetProperty("title").GetString()!,
                body.RootElement.GetProperty("content").GetString()!, WeChatText.Hash(token), Stopwatch.GetTimestamp());
            Pushes.Enqueue(value);
            if (BeforePushReply != null) await BeforePushReply(value, ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(PushOutcome, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class WakeFixture : IAsyncDisposable
    {
        internal required DurableRuntimeTests.Fixture Host;
        internal required Clock Clock;
        internal required Wire Wire;
        internal static async Task<WakeFixture> CreateAsync(string directory, Clock? clock = null,
            Wire? wire = null, ClawBinding? savedBinding = null, LedgerOptions? options = null,
            ICompletionSummarizer? summary = null, Guid? route = null, OwnedHostOptions? ownedOptions = null,
            bool modelEnabled = false)
        {
            clock ??= new(); wire ??= new();
            var host = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: modelEnabled,
                savedBinding: savedBinding, clock: clock, channelRequestOverride: wire.HandleAsync,
                ledgerOptions: options, completionSummarizer: summary, completionSummariesEnabled: summary != null,
                ownedOptions: ownedOptions);
            try
            {
                host.Settings.WeChat.ProtectedPushPlusToken = LocalSecret.Protect(PushToken);
                host.Settings.WeChat.PushPlusEnabled = true;
                if (route is { } savedRoute) host.Settings.WeChat.PushPlusWakeRouteId = savedRoute;
                await host.Channel.ConfigureAsync();
                return new() { Host = host, Clock = clock, Wire = wire };
            }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task ChangeRouteAsync(string token)
        {
            if (token is not (PushToken or OtherPushToken)) throw new ArgumentException("Synthetic routes only.");
            Host.Settings.WeChat.WritePushPlusToken(token);
            await Host.Channel.ConfigureAsync();
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task ApprovalShortWakeAsync(string directory, Action<string, bool> check)
    {
        WakeFixture? current = null;
        OwnedHostRuntimeTests.FakeRpc? rpc = null;
        string hostDirectory = Path.Combine(directory, "host");
        var profile = OwnedHostRuntimeTests.Profile(hostDirectory, verified: true);
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            rpc = new(configured.Configuration, async () =>
                (await current!.Host.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        });
        await using var f = await WakeFixture.CreateAsync(hostDirectory, ownedOptions: options, modelEnabled: true);
        current = f;
        var host = f.Host;
        host.Settings.ChatGpt.TaskControlEnabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(host, "create", "新建 demo",
            _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var runtime = host.Runtime.OwnedRuntime!;
        string thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
        string text = "继续 " + thread + " 检查合成渠道唤醒";
        await OwnedHostRuntimeTests.SendToolAsync(host, "start", text,
            _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), text));
        var task = runtime.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        rpc!.Emit("turn/started", new { threadId = thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, "inProgress") });
        await WaitAsync(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
        host.Settings.ChatGpt.Enabled = false;
        int modelCalls = host.Script.Calls, rpcCalls = rpc.Methods.Count;
        await InputAsync(host, "subscribe", thread + "需要决定时叫我");
        host.SendOutcome = """{"ret":-2}""";
        rpc.Emit("item/commandExecution/requestApproval", new
        {
            threadId = thread, turnId = turn, itemId = "synthetic-wake-approval",
            command = "echo SYNTHETIC_WAKE_APPROVAL", cwd = task.Snapshot.Cwd,
            startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, 901);
        var approval = runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
        string key = OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token);
        await WaitAsync(async () => (await host.ReadAsync()).ChannelWakes.Any(w => w.Stage == ChannelWakeStage.Pending));
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        await WaitAsync(async () => (await host.ReadAsync()).ChannelWakes.Any(w => w.Stage == ChannelWakeStage.Accepted
            && w.ReleasedAt != null));
        var state = await host.ReadAsync();
        var detail = state.Events.Single(e => e.ExternalEventKey == key);
        var wake = state.ChannelWakes.Single();
        var push = f.Wire.Pushes.Single();
        check("approval_window_failure_sends_only_fixed_short_wake",
            push.Body == ChannelWakeRecord.ShortBody && push.Body.Length < 100
            && !push.Title.Contains(thread, StringComparison.Ordinal) && !push.Body.Contains(thread, StringComparison.Ordinal)
            && !push.Body.Contains(task.Snapshot.Cwd, StringComparison.Ordinal)
            && !push.Body.Contains(OwnedControlToolAdapter.ApprovalReference("demo", approval), StringComparison.Ordinal)
            && wake.ReservedAttempts == 1 && wake.PlatformReceipt == "SYNTHETIC_WAKE_RECEIPT");
        check("wake_acceptance_does_not_claim_claw_delivery_or_approval_presentation",
            state.Outbox.Where(o => o.NotificationEventId == detail.Id).Any(o => o.Stage == OutboxStage.Rejected)
            && state.CommitmentRuns.Single().Stage == CommitmentRunStage.NotificationPending
            && state.CommitmentRuns.Single().SharedDecisionEventId == detail.Id
            && runtime.Presented(detail.Id) == null && state.Context!.PendingApproval == null
            && approval.State == ApprovalState.Pending && rpc.Responses == 0);
        int operationsBeforeQuery = state.Operations.Length;
        host.SendOutcome = """{"ret":0}""";
        await InputAsync(host, "fresh-after-wake", "唤醒记录");
        await WaitAsync(async () => (await host.ReadAsync()).Context!.PendingApproval?.SourceEventId == detail.Id);
        state = await host.ReadAsync();
        check("fresh_input_delivers_original_detailed_approval_without_second_wake_or_control",
            state.Outbox.Where(o => o.NotificationEventId == detail.Id).All(o => o.Stage == OutboxStage.Accepted)
            && state.CommitmentRuns.Single().Stage == CommitmentRunStage.Completed
            && runtime.Presented(detail.Id)?.Request.Token == approval.Token
            && f.Wire.Pushes.Count == 1 && rpc.Responses == 0 && rpc.Methods.Count == rpcCalls
            && host.Script.Calls == modelCalls && !state.Operations.Any(o => o.Action == "respond_approval"));
        long queryRevision = state.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("fresh-after-wake")).Revision;
        string queryReply = string.Join("\n", state.Outbox.Where(o => o.Kind == OutboxKind.ConversationReply
            && o.Revision == queryRevision && o.Stage == OutboxStage.Accepted).OrderBy(o => o.SegmentIndex).Select(o => o.Body));
        check("readonly_wake_query_distinguishes_api_acceptance_from_phone_and_full_report_delivery",
            queryReply.Contains("pushplus 已受理短提示，手机送达未知", StringComparison.Ordinal)
            && queryReply.Contains("整个助手的唤醒额度", StringComparison.Ordinal)
            && queryReply.Contains("正文另存于待补报记录", StringComparison.Ordinal)
            && state.Operations.Length == operationsBeforeQuery && host.Script.Calls == modelCalls
            && rpc.Methods.Count == rpcCalls && rpc.Responses == 0 && host.Queue.Entries.Count == 0);
    }

    private static async Task InputDuringRealGateAsync(string directory, Action<string, bool> check)
    {
        await using var f = await WakeFixture.CreateAsync(directory);
        var host = f.Host;
        await InputAsync(host, "prior-input", "状态");
        await host.Channel.TestPushAsync(PushToken, default);
        var primer = f.Wire.Pushes.Single();
        var source = await RejectNotificationAsync(f, "gate-source");
        var wake = (await host.ReadAsync()).ChannelWakes.Single();
        long revision = (await host.ReadAsync()).Revision;
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        await WaitAsync(() => Task.FromResult(host.Channel.WakeRateWaitActive));
        await host.InputAsync("prior-input", "状态"); // Same authenticated ID is a replay.
        await InjectIgnoredInputAsync(host, foreign: true);
        await InjectIgnoredInputAsync(host, foreign: false);
        var state = await host.ReadAsync();
        check("duplicate_foreign_and_stale_inputs_cannot_cancel_a_pending_wake",
            state.Revision == revision && state.ChannelWakes.Single().Stage == ChannelWakeStage.Pending
            && state.ChannelWakes.Single().ReservedAttempts == 0 && f.Wire.Pushes.Count == 1);
        host.SendOutcome = """{"ret":0}""";
        await InputAsync(host, "fresh-during-gate", "状态");
        await WaitAsync(async () => AcceptedCatchUp(await host.ReadAsync(), source, f.Wire, "fresh-during-gate", 1));
        var remaining = TimeSpan.FromSeconds(13.4) - Stopwatch.GetElapsedTime(primer.StartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await Task.Delay(600);
        state = await host.ReadAsync();
        var after = state.ChannelWakes.Single(w => w.Id == wake.Id);
        var budget = await host.Ledger.GetChannelWakeBudgetAsync();
        check("fresh_input_cancels_before_post_rate_limit_begin_without_spending_attempt_or_budget",
            after.Stage == ChannelWakeStage.Cancelled && after.ReservedAttempts == 0 && after.ClaimId == null
            && f.Wire.Pushes.Count == 1 && budget.StartedLastHour == 0 && budget.InFlight == 0
            && Stopwatch.GetElapsedTime(primer.StartedAt) >= TimeSpan.FromSeconds(13));
        check("fresh_input_records_catch_up_without_rewriting_rejected_source_or_recreating_wake",
            state.ChannelWakes.Length == 1 && AcceptedCatchUp(state, source, f.Wire, "fresh-during-gate", 1)
            && state.Operations.IsEmpty && host.Script.Calls == 0 && host.Runtime.LastError == null);
    }

    private static async Task ActualSendDrainAsync(string directory, Action<string, bool> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new Wire { BeforePushReply = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        await using var f = await WakeFixture.CreateAsync(directory, wire: wire);
        var host = f.Host;
        var identity = host.Runtime.Identity!;
        Task? stop = null;
        OutboxMessage source = null!;
        try
        {
            source = await RejectNotificationAsync(f, "drain-source");
            f.Clock.Advance(TimeSpan.FromSeconds(6));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            host.SendOutcome = """{"ret":0}""";
            await InputAsync(host, "fresh-after-begin", "状态");
            var whileSending = await host.ReadAsync();
            check("fresh_input_after_begin_does_not_pretend_to_retract_an_inflight_wake",
                whileSending.ChannelWakes.Single().Stage == ChannelWakeStage.Sending
                && whileSending.ChannelWakes.Single().ReleasedAt == null
                && whileSending.Outbox.Any(o => o.Kind == OutboxKind.ConversationReply && o.Stage == OutboxStage.Accepted)
                && AcceptedCatchUp(whileSending, source, f.Wire, "fresh-after-begin", 1));
            stop = host.Runtime.StopSessionAsync();
            await Task.Delay(200);
            var state = await host.Ledger.ReadConversationAsync(identity);
            var budget = await host.Ledger.GetChannelWakeBudgetAsync();
            check("stop_waits_for_actual_push_request_that_ignores_cancellation",
                !stop.IsCompleted && state.ChannelWakes.Single().Stage == ChannelWakeStage.Sending
                && state.ChannelWakes.Single().ReleasedAt == null && budget.InFlight == 1
                && state.ChannelWakes.Single().ReservedAttempts == 1 && wire.Pushes.Count == 1);
        }
        finally { release.TrySetResult(); }
        if (stop != null) await stop.WaitAsync(TimeSpan.FromSeconds(8));
        var drained = await host.Ledger.ReadConversationAsync(identity);
        var drainedBudget = await host.Ledger.GetChannelWakeBudgetAsync();
        // The fake peer ignored cancellation and then returned a valid receipt.
        // A stop request cannot erase that definitive outcome or refund its quota.
        check("actual_wire_completion_preserves_late_acceptance_and_releases_claim_without_refund",
            drained.ChannelWakes.Single().Stage == ChannelWakeStage.Accepted
            && drained.ChannelWakes.Single().PlatformReceipt == "SYNTHETIC_WAKE_RECEIPT"
            && drained.ChannelWakes.Single().ReleasedAt != null && drainedBudget.InFlight == 0
            && drainedBudget.StartedLastHour == 1
            && AcceptedCatchUp(drained, source, f.Wire, "fresh-after-begin", 1));
        await host.Runtime.ActivateAsync(host.Binding, default);
        await Task.Delay(700);
        check("same_binding_reactivation_does_not_repeat_late_accepted_wake",
            (await host.ReadAsync()).ChannelWakes.Length == 1 && wire.Pushes.Count == 1
            && (await host.ReadAsync()).ChannelWakes.Single().Stage == ChannelWakeStage.Accepted
            && (await host.ReadAsync()).ChannelWakes.Single().PlatformReceipt == "SYNTHETIC_WAKE_RECEIPT"
            && host.Script.Calls == 0 && host.Queue.Entries.Count == 0
            && AcceptedCatchUp(await host.ReadAsync(), source, f.Wire, "fresh-after-begin", 1));
    }

    private static async Task BindingAndDisableAsync(string directory, Action<string, bool> check)
    {
        await using var f = await WakeFixture.CreateAsync(directory);
        var host = f.Host;
        var original = host.Runtime.Identity!;
        await RejectNotificationAsync(f, "old-binding-source");
        var replacement = new ClawBinding
        {
            BotId = "synthetic-replacement-" + Guid.NewGuid().ToString("N"), UserId = "synthetic-replacement-user",
            Token = "SYNTHETIC_REPLACEMENT_TOKEN", ContextToken = "SYNTHETIC_REPLACEMENT_CONTEXT",
            BaseUrl = WeChatApi.DefaultBaseUrl
        };
        host.Settings.WeChat.WriteBinding(replacement);
        host.Binding = replacement;
        await host.Channel.ConfigureAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(700);
        var old = await host.Ledger.ReadConversationAsync(original);
        var current = await host.ReadAsync();
        check("binding_switch_never_sends_old_identity_wake_to_new_identity",
            current.Identity != original && current.ChannelWakes.IsEmpty && f.Wire.Pushes.IsEmpty
            && old.ChannelWakes.Single().ReservedAttempts == 0
            && old.Outbox.Single().Stage == OutboxStage.Rejected);
        await RejectNotificationAsync(f, "new-binding-disabled-source");
        host.Settings.WeChat.PushPlusEnabled = false;
        await host.Channel.ConfigureAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(700);
        current = await host.ReadAsync();
        check("disabling_push_route_leaves_unsent_wake_without_http_or_budget_charge",
            f.Wire.Pushes.IsEmpty && current.ChannelWakes.All(w => w.ReservedAttempts == 0)
            && current.WakeChannel is { Enabled: false }
            && (await host.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 0
            && host.Script.Calls == 0 && current.Operations.IsEmpty);
    }

    private static async Task UnknownRestartAndRouteAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        Guid route, wakeId;
        ClawBinding saved;
        OutboxMessage source;
        var firstWire = new Wire { PushOutcome = """{"code":200}""" };
        await using (var f = await WakeFixture.CreateAsync(directory, clock: clock, wire: firstWire))
        {
            source = await RejectNotificationAsync(f, "unknown-source");
            f.Clock.Advance(TimeSpan.FromSeconds(6));
            await WaitAsync(async () => (await f.Host.ReadAsync()).ChannelWakes.Any(w => w.Stage == ChannelWakeStage.Unknown
                && w.ReleasedAt != null));
            var state = await f.Host.ReadAsync();
            route = f.Host.Settings.WeChat.PushPlusWakeRouteId;
            wakeId = state.ChannelWakes.Single().Id;
            saved = f.Host.PersistedBinding();
            check("push_accepted_code_without_receipt_is_unknown_and_original_claw_stays_rejected",
                firstWire.Pushes.Count == 1 && state.ChannelWakes.Single().ReservedAttempts == 1
                && state.Outbox.Single().Stage == OutboxStage.Rejected
                && (await f.Host.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 1);
        }
        await using var reopened = await WakeFixture.CreateAsync(directory, clock: clock, savedBinding: saved, route: route);
        await Task.Delay(700);
        await reopened.ChangeRouteAsync(OtherPushToken);
        clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(700);
        var beforeInput = await reopened.Host.ReadAsync();
        check("unknown_wake_survives_restart_and_rotated_route_without_new_attempt",
            reopened.Wire.Pushes.IsEmpty && reopened.Host.Settings.WeChat.PushPlusWakeRouteId != route
            && beforeInput.ChannelWakes.Length == 1 && beforeInput.ChannelWakes.Single().Id == wakeId
            && beforeInput.ChannelWakes.Single().Stage == ChannelWakeStage.Unknown);
        await InputAsync(reopened.Host, "fresh-after-restart", "状态");
        await WaitAsync(async () => AcceptedCatchUp(await reopened.Host.ReadAsync(), source,
            reopened.Wire, "fresh-after-restart", 0));
        var stateAfter = await reopened.Host.ReadAsync();
        check("fresh_input_after_unknown_only_recovers_claw_without_wake_replay",
            reopened.Wire.Pushes.IsEmpty && stateAfter.ChannelWakes.Length == 1
            && stateAfter.ChannelWakes.Single().Stage == ChannelWakeStage.Unknown
            && stateAfter.Operations.IsEmpty && reopened.Host.Script.Calls == 0
            && AcceptedCatchUp(stateAfter, source, reopened.Wire, "fresh-after-restart", 0));
    }

    private static async Task QuietAndBudgetAsync(string directory, Action<string, bool> check)
    {
        var clock = new Clock();
        var quietDay = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date.AddDays(1).AddHours(10), TimeSpan.Zero);
        clock.Set(quietDay);
        await using var f = await WakeFixture.CreateAsync(directory, clock: clock);
        var host = f.Host;
        if (!host.Settings.WeChat.TrySetWakeQuietHours("10:00", "11:00", "UTC", out var error))
            throw new InvalidOperationException(error);
        await host.Channel.ConfigureAsync();
        var quietSource = await RejectNotificationAsync(f, "quiet-source");
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(800);
        check("quiet_hours_keep_wake_pending_without_reservation_or_http",
            (await host.ReadAsync()).ChannelWakes.Single().Stage == ChannelWakeStage.Pending
            && (await host.ReadAsync()).ChannelWakes.Single().ReservedAttempts == 0 && f.Wire.Pushes.IsEmpty);
        host.SendOutcome = """{"ret":0}""";
        await InputAsync(host, "status-during-quiet", "状态");
        check("quiet_wake_never_blocks_current_status_or_claw_recovery",
            (await host.ReadAsync()).ChannelWakes.Single().Stage == ChannelWakeStage.Cancelled
            && f.Wire.Pushes.IsEmpty && host.Script.Calls == 0
            && AcceptedCatchUp(await host.ReadAsync(), quietSource, f.Wire, "status-during-quiet", 1));
        var nextQuietSource = await RejectNotificationAsync(f, "quiet-source-next");
        clock.Set(quietDay.AddHours(1).AddMinutes(1));
        await WaitAsync(async () => (await host.ReadAsync()).ChannelWakes.Any(w => w.Stage == ChannelWakeStage.Accepted
            && w.ReleasedAt != null));
        var firstPush = f.Wire.Pushes.Single();
        check("quiet_end_sends_one_short_wake_for_the_current_pending_episode",
            firstPush.Body == ChannelWakeRecord.ShortBody
            && (await host.ReadAsync()).ChannelWakes.Count(w => w.ReservedAttempts == 1) == 1);
        host.SendOutcome = """{"ret":0}""";
        await InputAsync(host, "fresh-before-budget", "状态");
        var source = await RejectNotificationAsync(f, "budget-source");
        clock.Advance(TimeSpan.FromSeconds(6));
        var remaining = TimeSpan.FromSeconds(13.4) - Stopwatch.GetElapsedTime(firstPush.StartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await Task.Delay(700);
        var state = await host.ReadAsync();
        var pending = state.ChannelWakes.Single(w => w.Stage == ChannelWakeStage.Pending);
        check("global_hour_budget_defers_next_wake_after_real_rate_gate_without_attempt",
            f.Wire.Pushes.Count == 1 && pending.ReservedAttempts == 0
            && pending.Sources.Any(s => s.OutboxId == source.Id)
            && (await host.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 1
            && AcceptedCatchUp(state, nextQuietSource, f.Wire, "fresh-before-budget", 1));
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await WaitAsync(async () => (await host.ReadAsync()).ChannelWakes.Single(w => w.Id == pending.Id)
            is { Stage: ChannelWakeStage.Accepted, ReleasedAt: not null });
        state = await host.ReadAsync();
        check("next_budget_window_sends_existing_wake_once_without_task_or_model_work",
            f.Wire.Pushes.Count == 2 && f.Wire.Pushes.All(p => p.Body == ChannelWakeRecord.ShortBody)
            && state.ChannelWakes.Single(w => w.Id == pending.Id).ReservedAttempts == 1
            && state.Operations.IsEmpty && host.Script.Calls == 0 && host.Queue.Entries.Count == 0
            && state.Outbox.Single(o => o.Id == source.Id).Stage == OutboxStage.Rejected);
    }

    private static async Task<OutboxMessage> RejectNotificationAsync(WakeFixture f, string key)
    {
        f.Host.SendOutcome = """{"ret":-2}""";
        var result = await f.Host.Runtime.EnqueueNotificationAsync("synthetic/wake/" + key, PrivateBody, null, default);
        var id = result.Outbox.Single().Id;
        await WaitAsync(async () => (await f.Host.ReadAsync()).Outbox.Single(o => o.Id == id).Stage == OutboxStage.Rejected);
        await WaitAsync(async () => (await f.Host.ReadAsync()).ChannelWakes.Any(w => w.Stage == ChannelWakeStage.Pending
            && w.Sources.Any(s => s.OutboxId == id)));
        return (await f.Host.ReadAsync()).Outbox.Single(o => o.Id == id);
    }

    // Ordinary history has separate delivery identity. Preserve the original rejection and
    // verify exact source lineage, complete text and one actual accepted catch-up page.
    // RequiredDecision deliberately never uses this helper: its original detail must be Accepted.
    private static bool AcceptedCatchUp(ConversationSnapshot state, OutboxMessage source,
        Wire wire, string sourceInput, int originalWireAttempts)
    {
        var original = state.Outbox.SingleOrDefault(o => o.Id == source.Id);
        if (original == null || original.Stage != OutboxStage.Rejected || original.Body != source.Body
            || original.SendAttempts != source.SendAttempts || original.ChannelReceipt != source.ChannelReceipt
            || original.RejectionReason != source.RejectionReason || original.Identity != source.Identity
            || state.Events.Single(e => e.Id == original.NotificationEventId).RequiredDecision != null) return false;
        var links = state.CatchUpBatches.SelectMany(batch => batch.Members
            .Where(member => member.Sources.Any(row => row.OutboxId == source.Id))
            .Select(member => (Batch: batch, Member: member))).ToArray();
        if (links.Length != 1) return false;
        var (batch, member) = links[0];
        if (member.SourceEventId != source.NotificationEventId || member.Sources.Length != 1
            || member.Sources[0].OutboxId != source.Id || member.Sources[0].SendAttempts != source.SendAttempts
            || member.Sources[0].BodyHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Body)))
            || !state.CatchUpWindows.Any(w => w.Id == batch.WindowId && w.Identity == source.Identity
                && w.SourceMessageId == WeChatText.Hash(sourceInput))) return false;
        var page = state.Outbox.SingleOrDefault(o => o.Id == member.CatchUpOutboxId);
        if (page == null || page.Id == source.Id || page.NotificationEventId != batch.DeliveryEventId
            || page.Identity != source.Identity || page.Kind != OutboxKind.Notification
            || page.Stage != OutboxStage.Accepted || page.SendAttempts != 1
            || !page.Body.Contains(source.Body, StringComparison.Ordinal)
            || page.Body.IndexOf(source.Body, StringComparison.Ordinal) != page.Body.LastIndexOf(source.Body, StringComparison.Ordinal)) return false;
        // Different historical events can legitimately have identical bodies (including the
        // displayed minute). Only their actual client/outbox ID establishes a duplicate send.
        var originalRequests = wire.ClawSends.Where(request => request.ClientId == "assistant-outbox-" + source.Id.ToString("N")).ToArray();
        var pageRequests = wire.ClawSends.Where(request => request.ClientId == "assistant-outbox-" + page.Id.ToString("N")).ToArray();
        return originalRequests.Length == originalWireAttempts && originalRequests.All(request => request.Body == source.Body)
            && pageRequests.Length == 1 && pageRequests[0].Body == page.Body;
    }

    private static async Task ThrowingObserverAsync(string directory, Action<string, bool> check)
    {
        await using var f = await WakeFixture.CreateAsync(directory);
        int failures = 0;
        void Observer()
        {
            if (!f.Host.Channel.PushStatus.Contains("已受理短提示", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref failures);
            throw new InvalidOperationException("SYNTHETIC_PRESENTATION_OBSERVER_FAILURE");
        }
        f.Host.Channel.Changed += Observer;
        try
        {
            await RejectNotificationAsync(f, "observer-source");
            f.Clock.Advance(TimeSpan.FromSeconds(6));
            await WaitAsync(async () => (await f.Host.ReadAsync()).ChannelWakes.Any(w => w.ReleasedAt != null));
            var state = await f.Host.ReadAsync();
            var wake = state.ChannelWakes.Single();
            check("throwing_ui_observer_cannot_downgrade_actual_push_acceptance",
                failures == 1 && wake.Stage == ChannelWakeStage.Accepted
                && wake.PlatformReceipt == "SYNTHETIC_WAKE_RECEIPT" && f.Wire.Pushes.Count == 1);
            check("throwing_ui_observer_still_commits_and_releases_real_send_claim",
                wake.ReleasedAt != null && (await f.Host.Ledger.GetChannelWakeBudgetAsync()).InFlight == 0
                && (await f.Host.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 1
                && state.Outbox.Single().Stage == OutboxStage.Rejected && f.Host.Runtime.LastError == null);
        }
        finally { f.Host.Channel.Changed -= Observer; }
    }

    private static async Task SettingsChangedDuringGateAsync(string directory, Action<string, bool> check)
    {
        await using var f = await WakeFixture.CreateAsync(directory);
        var host = f.Host;
        var identity = host.Runtime.Identity!;
        await host.Channel.TestPushAsync(PushToken, default);
        var primer = f.Wire.Pushes.Single();
        await RejectNotificationAsync(f, "settings-gate-source");
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        await WaitAsync(() => Task.FromResult(host.Channel.WakeRateWaitActive));
        // Deliberately do not call ConfigureAsync. The transport must read the
        // current trusted settings after the actual throttle wait, not only its
        // cached session/binding captured when the worker entered the gate.
        host.Settings.WeChat.WriteBinding(new()
        {
            BotId = "synthetic-unconfigured-" + Guid.NewGuid().ToString("N"), UserId = "synthetic-unconfigured-user",
            Token = "SYNTHETIC_UNCONFIGURED_TOKEN", ContextToken = "SYNTHETIC_UNCONFIGURED_CONTEXT",
            BaseUrl = WeChatApi.DefaultBaseUrl
        });
        var remaining = TimeSpan.FromSeconds(13.4) - Stopwatch.GetElapsedTime(primer.StartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await Task.Delay(600);
        var state = await host.Ledger.ReadConversationAsync(identity);
        check("binding_settings_change_without_configure_blocks_send_after_real_gate",
            host.Runtime.Identity == identity && !host.Runtime.IsCurrentBinding(identity)
            && state.ChannelWakes.Single().ReservedAttempts == 0 && state.ChannelWakes.Single().StartedAt == null
            && f.Wire.Pushes.Count == 1 && (await host.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 0
            && state.Outbox.Single().Stage == OutboxStage.Rejected);
    }

    private static async Task RouteInitializationFaultAsync(string directory, Action<string, bool> check)
    {
        Directory.CreateDirectory(directory);
        var settings = new Settings
        {
            WeChat = new() { PushPlusEnabled = true, ProtectedPushPlusToken = LocalSecret.Protect(PushToken) }
        };
        string path = Path.Combine(directory, "synthetic-route-id.txt");
        int writes = 0;
        Guid failedRoute = Guid.Empty;
        using var wire = new RejectAllHttp();
        await using var service = new WeChatService(settings,
            () => new([], DateTimeOffset.UtcNow, []), () =>
            {
                writes++;
                if (writes == 1)
                {
                    failedRoute = settings.WeChat.PushPlusWakeRouteId;
                    throw new IOException("SYNTHETIC_ROUTE_PERSIST_FAILURE");
                }
                File.WriteAllText(path, settings.WeChat.PushPlusWakeRouteId.ToString("D"));
            }, wire, receiveMessages: false);
        bool failed = false;
        try { await service.ConfigureAsync(); }
        catch (IOException) { failed = true; }
        check("first_route_persistence_failure_restores_empty_in_memory_identity",
            failed && writes == 1 && failedRoute != Guid.Empty && settings.WeChat.PushPlusWakeRouteId == Guid.Empty
            && !File.Exists(path) && wire.Calls == 0);
        await service.ConfigureAsync();
        Guid stored = Guid.Parse(File.ReadAllText(path));
        await service.ConfigureAsync();
        check("next_configuration_must_persist_new_route_before_reusing_it",
            writes == 2 && stored != Guid.Empty && stored != failedRoute
            && settings.WeChat.PushPlusWakeRouteId == stored && wire.Calls == 0);
    }

    private sealed class RejectAllHttp : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("No HTTP is allowed for synthetic route initialization.");
        }
    }

    private static async Task InjectIgnoredInputAsync(DurableRuntimeTests.Fixture host, bool foreign)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ret = 0, msgs = new[] { new
            {
                from_user_id = foreign ? "foreign-synthetic-user" : host.Binding.UserId,
                to_user_id = host.Binding.BotId, message_type = 1,
                message_id = foreign ? "foreign-gate-input" : "stale-gate-input",
                create_time_ms = DateTimeOffset.UtcNow.AddMinutes(foreign ? 0 : -10).ToUnixTimeMilliseconds(),
                context_token = "IGNORED_SYNTHETIC_CONTEXT",
                item_list = new[] { new { type = 1, text_item = new { text = "状态" } } }
            } }
        }));
        await host.Channel.ProcessUpdatesAsync(payload.RootElement.Clone(), default);
    }

    private sealed class CountingSummary : ICompletionSummarizer
    {
        internal const string Body = "合成检查已完成；这段完整结果只通过原对话发送。";
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public bool CompletionSummariesEnabled => true;
        public Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new CompletionSummary(Body, "synthetic", true, "", Dependencies: OutputDependencies.Independent));
        }
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
    private static async Task WaitAsync(Func<Task<bool>> predicate, int seconds = 10)
    {
        long start = Stopwatch.GetTimestamp();
        while (!await predicate())
        {
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(seconds))
                throw new TimeoutException("Synthetic channel wake condition timed out.");
            await Task.Delay(20);
        }
    }
}
