using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Actual SendWakeAsync and DPAPI/FileJournal leases. Its caller journal is
// separate from the fixture runtime's empty ledger so the background worker
// cannot race this deliberately controlled begin/transport boundary.
internal static class WakeDeliveryGuardTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--wake-delivery-guards-test");
        if (index < 0) return false;
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)) throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "wake-delivery-guard-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, failures = checks.Count(item => !item.Value), actualTransport = true,
                actualDurableLease = true, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks
            }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }

        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "rejected-observer"), HttpStatusCode.OK,
                """{"code":401,"msg":"synthetic explicit refusal"}""");
            int observers = 0;
            void Observer() { observers++; throw new InvalidOperationException("synthetic_ui_observer"); }
            f.Host.Channel.Changed += Observer;
            WakeSendAttempt? result;
            try { result = await f.SendAsync(); }
            finally { f.Host.Channel.Changed -= Observer; }
            Check("explicit_business_rejection_survives_throwing_ui_observer",
                result?.Outcome == WakeDeliveryOutcome.Rejected && result.PlatformReceipt == null
                && observers > 0 && f.BeginCalls == 1 && f.WireCalls == 1 && f.OnlyShortBody);
            await f.RecordAndReleaseAsync(result!);
            var state = await f.Ledger.ReadConversationAsync(f.Identity);
            var budget = await f.Ledger.GetChannelWakeBudgetAsync();
            Check("rejection_is_committed_and_actual_send_claim_released_without_refund",
                state.ChannelWakes.Single().Stage == ChannelWakeStage.Rejected && state.ChannelWakes.Single().ReleasedAt != null
                && state.ChannelWakes.Single().PlatformReceipt == null && budget.InFlight == 0
                && budget.StartedLastHour == 1 && budget.StartedLast24Hours == 1 && f.SourceIntact(state));
        }

        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "cipher-after-begin"));
            Guid route = f.Host.Settings.WeChat.PushPlusWakeRouteId;
            var result = await f.SendAsync(() =>
                f.Host.Settings.WeChat.ProtectedPushPlusToken = LocalSecret.Protect("SYNTHETIC_CHANGED_WITHOUT_ROUTE"));
            var sending = (await f.Ledger.ReadConversationAsync(f.Identity)).ChannelWakes.Single();
            Check("cipher_change_after_real_begin_without_route_change_returns_not_started",
                result?.Outcome == WakeDeliveryOutcome.NotStarted && result.PlatformReceipt == null && f.WireCalls == 0
                && f.BeginCalls == 1 && sending.Stage == ChannelWakeStage.Sending && sending.ReservedAttempts == 1
                && f.Host.Settings.WeChat.PushPlusWakeRouteId == route);
            await f.RecordAndReleaseAsync(result!);
            var state = await f.Ledger.ReadConversationAsync(f.Identity);
            var budget = await f.Ledger.GetChannelWakeBudgetAsync();
            bool blocked = false;
            try { await f.Ledger.BeginWakeSendAsync(f.Identity, f.Pending.Id, route); }
            catch (LedgerException error) when (error.Code == LedgerErrors.InvalidTransition) { blocked = true; }
            Check("not_started_reservation_is_terminal_released_and_never_rearmed",
                state.ChannelWakes.Single().Stage == ChannelWakeStage.NotStarted && state.ChannelWakes.Single().ReleasedAt != null
                && budget.InFlight == 0 && budget.StartedLastHour == 1 && blocked && f.SourceIntact(state));
        }

        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "changed-binding"));
            var changed = f.Host.Settings.WeChat.ReadBinding()!;
            changed.UserId = "synthetic-other-user";
            f.Host.Settings.WeChat.WriteBinding(changed); // No Configure: the cached service binding still names the old user.
            var result = await f.SendAsync();
            var state = await f.Ledger.ReadConversationAsync(f.Identity);
            Check("actual_settings_binding_change_blocks_before_begin_or_http",
                result == null && f.BeginCalls == 0 && f.WireCalls == 0 && state.ChannelWakes.Single().ReservedAttempts == 0
                && state.ChannelWakes.Single().Stage == ChannelWakeStage.Pending
                && (await f.Ledger.GetChannelWakeBudgetAsync()).StartedLastHour == 0 && f.SourceIntact(state));
        }

        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "corrupt-quiet"));
            f.Host.Settings.WeChat.PushPlusWakeQuietStart = "22:00";
            f.Host.Settings.WeChat.PushPlusWakeQuietEnd = null;
            f.Host.Settings.WeChat.PushPlusWakeTimeZoneId = "UTC";
            var result = await f.SendAsync();
            var state = await f.Ledger.ReadConversationAsync(f.Identity);
            Check("malformed_quiet_configuration_disables_transport_without_begin",
                !f.Host.Channel.ReadWakeConfiguration().Enabled && result == null && f.BeginCalls == 0 && f.WireCalls == 0
                && state.ChannelWakes.Single().ReservedAttempts == 0 && f.SourceIntact(state));
        }

        foreach (var test in new[]
        {
            (Name: "http_non_success", Status: HttpStatusCode.ServiceUnavailable, Body: """{"code":200,"data":"SYNTHETIC_RECEIPT"}"""),
            (Name: "missing_receipt", Status: HttpStatusCode.OK, Body: """{"code":200}"""),
            (Name: "invalid_json", Status: HttpStatusCode.OK, Body: "not-json"),
            (Name: "whitespace_receipt", Status: HttpStatusCode.OK, Body: JsonSerializer.Serialize(new { code = 200, data = "   " })),
            (Name: "long_receipt", Status: HttpStatusCode.OK, Body: JsonSerializer.Serialize(new { code = 200, data = new string('x', 513) })),
            (Name: "control_receipt", Status: HttpStatusCode.OK, Body: JsonSerializer.Serialize(new { code = 200, data = "synthetic\nreceipt" }))
        })
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, test.Name), test.Status, test.Body);
            var result = await f.SendAsync();
            Check(test.Name + "_is_unknown_after_actual_request_started",
                result?.Outcome == WakeDeliveryOutcome.Unknown && result.PlatformReceipt == null
                && f.BeginCalls == 1 && f.WireCalls == 1 && f.OnlyShortBody);
            await f.RecordAndReleaseAsync(result!);
            var state = await f.Ledger.ReadConversationAsync(f.Identity);
            var budget = await f.Ledger.GetChannelWakeBudgetAsync();
            var again = await f.Ledger.EnsurePendingWakeAsync(f.Identity, f.Configuration.RouteId);
            Check(test.Name + "_persists_terminal_unknown_with_budget_and_original_body_intact",
                state.ChannelWakes.Single().Stage == ChannelWakeStage.Unknown && state.ChannelWakes.Single().ReleasedAt != null
                && budget.InFlight == 0 && budget.StartedLastHour == 1 && budget.StartedLast24Hours == 1
                && again?.Id == f.Pending.Id && again.Stage == ChannelWakeStage.Unknown && f.SourceIntact(state)
                && f.Host.Script.Calls == 0);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Token = "SYNTHETIC_WAKE_GUARD_TOKEN";
        private const string PrivateBody = "SYNTHETIC_PRIVATE_BODY_MUST_STAY_IN_ORIGINAL_OUTBOX";
        internal DurableRuntimeTests.Fixture Host = null!;
        internal DurableConversationLedger Ledger = null!;
        internal ConversationIdentity Identity = null!;
        internal WakeChannelConfiguration Configuration = null!;
        internal ChannelWakeRecord Pending = null!;
        internal int WireCalls, BeginCalls;
        internal bool OnlyShortBody = true;
        private Guid sourceId;

        internal static async Task<Fixture> CreateAsync(string directory, HttpStatusCode status = HttpStatusCode.OK,
            string body = "{\"code\":200,\"data\":\"SYNTHETIC_GUARD_RECEIPT\"}")
        {
            var f = new Fixture();
            f.Host = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "host"), modelEnabled: false,
                channelRequestOverride: async (request, ct) =>
                {
                    if (request.RequestUri?.AbsoluteUri != "https://www.pushplus.plus/send") return null;
                    f.WireCalls++;
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    f.OnlyShortBody &= json.RootElement.GetProperty("token").GetString() == Token
                        && json.RootElement.GetProperty("title").GetString() == WeChatService.WakeTitle
                        && json.RootElement.GetProperty("content").GetString() == ChannelWakeRecord.ShortBody;
                    return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                });
            try
            {
                f.Host.Settings.WeChat.WritePushPlusToken(Token);
                f.Host.Settings.WeChat.PushPlusEnabled = true;
                await f.Host.Channel.ConfigureAsync();
                f.Identity = f.Host.Runtime.Identity!;
                f.Configuration = f.Host.Channel.ReadWakeConfiguration();
                f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "transport-caller.state")),
                    new WindowsStateProtector(), options: new LedgerOptions { WakeAggregationSeconds = 0 });
                await f.Ledger.ConfigureWakeChannelAsync(f.Identity, f.Configuration);
                var source = await f.Ledger.EnqueueNotificationAsync(f.Identity, "synthetic-guard-notification", PrivateBody, null);
                f.sourceId = source.Outbox.Single().Id;
                await f.Ledger.BeginOutboxSendAsync(f.Identity, f.sourceId);
                await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, f.sourceId, OutboxStage.Rejected, "synthetic context expiry",
                    rejectionReason: OutboxRejectionReason.ContextExpired);
                f.Pending = await f.Ledger.EnsurePendingWakeAsync(f.Identity, f.Configuration.RouteId)
                    ?? throw new InvalidOperationException("synthetic_pending_wake_missing");
                return f;
            }
            catch { if (f.Ledger != null) await f.Ledger.DisposeAsync(); await f.Host.DisposeAsync(); throw; }
        }

        internal Task<WakeSendAttempt?> SendAsync(Action? afterBegin = null) => Host.Channel.SendWakeAsync(Identity, Configuration,
            async ct =>
            {
                BeginCalls++;
                var lease = await Ledger.BeginWakeSendAsync(Identity, Pending.Id, Configuration.RouteId, ct);
                afterBegin?.Invoke();
                return lease;
            }, CancellationToken.None);

        internal async Task RecordAndReleaseAsync(WakeSendAttempt result)
        {
            try { await Ledger.RecordWakeOutcomeAsync(result.Lease, result.Outcome, result.Evidence, result.PlatformReceipt); }
            finally { await Ledger.ReleaseWakeSendAsync(result.Lease); }
        }

        internal bool SourceIntact(ConversationSnapshot state) => state.Outbox.Single().Id == sourceId
            && state.Outbox.Single().Stage == OutboxStage.Rejected && state.Outbox.Single().Body == PrivateBody
            && state.Outbox.Single().SendAttempts == 1 && state.Operations.IsEmpty;

        public async ValueTask DisposeAsync()
        { await Host.DisposeAsync(); await Ledger.DisposeAsync(); }
    }
}
