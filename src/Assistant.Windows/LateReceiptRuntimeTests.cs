using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

// Full production host/channel/API path. Only the HTTP peer is synthetic; its
// first reply intentionally ignores cancellation. No credentials or network are used.
internal static class LateReceiptRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--late-receipt-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--late-receipt-runtime-test")
                throw new ArgumentException("An explicit fresh output directory is required.");
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
            throw new ArgumentException("A fresh absolute directory on the WPF dispatcher is required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        Settings.SetDataDirectory(Path.Combine(directory, "isolated-settings"));
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "late-receipt-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, failures = checks.Count(pair => !pair.Value), checks,
                actualNetworkRequests = 0, productionDataRead = false, actualModelRequests = 0,
                runtime = "actual host, WeChatApi JSON and protected file ledger; synthetic HTTP peer"
            }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        await StoppedReceiptAsync(Path.Combine(directory, "accepted"), OutboxStage.Accepted, "{\"ret\":0}", Check);
        await StoppedReceiptAsync(Path.Combine(directory, "rejected"), OutboxStage.Rejected,
            "{\"ret\":-2,\"errmsg\":\"synthetic expired context\"}", Check);
        await StoppedReceiptAsync(Path.Combine(directory, "missing-ret"), OutboxStage.Unknown, "{}", Check);
        await BindingSwitchAsync(Path.Combine(directory, "binding-switch"), Check);
    }

    private static async Task StoppedReceiptAsync(string directory, OutboxStage expected, string response, Action<string, bool> check)
    {
        string prefix = expected.ToString().ToLowerInvariant() + "_";
        var peer = new Peer(response);
        ClawBinding binding;
        ConversationIdentity identity;
        OutboxMessage saved;
        string file;
        Task? stopping = null;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            coordinatorOptions: Options(), channelRequestOverride: peer.SendAsync))
        {
            identity = f.Runtime.Identity!; binding = f.Settings.WeChat.ReadBinding()!; file = f.LedgerPath;
            try
            {
                var notification = await f.Runtime.EnqueueNotificationAsync("synthetic-late/" + prefix,
                    "这份合成通知已经准备好，等待接口给出确定结果。", null, default);
                var original = notification.Outbox.Single();
                await peer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var sending = (await f.Ledger.ReadConversationAsync(identity)).Outbox.Single(row => row.Id == original.Id);
                var wire = peer.Wires.Single();
                check(prefix + "actual_api_wire_matches_original_outbox_and_client_id",
                    sending.Stage == OutboxStage.Sending && sending.SendAttempts == 1
                    && wire.ClientId == ClientId(sending) && wire.Body == sending.Body && wire.ToUser == binding.UserId
                    && wire.MessageType == 2 && wire.MessageState == 2 && wire.ItemType == 1 && wire.HasContext);

                if (expected == OutboxStage.Rejected)
                {
                    await f.InputAsync("fresh-before-old-context-rejection", "状态");
                    await UntilAsync(async () => (await f.Ledger.ReadConversationAsync(identity)).Revision > sending.AttemptTrustedInputRevision!.Value,
                        "the new authenticated input was not persisted");
                }
                stopping = f.Runtime.StopSessionAsync();
                await UntilAsync(async () => (await f.Ledger.ReadConversationAsync(identity)).Outbox.Single(row => row.Id == original.Id).Stage == OutboxStage.Unknown,
                    "the bounded caller wait did not record Unknown");
                var waiting = await f.Ledger.ReadConversationAsync(identity);
                await EvidenceAsync(directory, "waiting-for-real-http", f, identity, peer);
                check(prefix + "stop_waits_for_real_http_after_unknown_is_durable",
                    !stopping.IsCompleted && peer.Returned == 0 && peer.Wires.Count == 1
                    && waiting.Outbox.Single(row => row.Id == original.Id).SendAttempts == 1);
                peer.Release.TrySetResult();
                await stopping.WaitAsync(TimeSpan.FromSeconds(8));
                var state = await f.Ledger.ReadConversationAsync(identity);
                saved = state.Outbox.Single(row => row.Id == original.Id);
                await EvidenceAsync(directory, "after-real-http-and-drain", f, identity, peer);
                check(prefix + "actual_http_json_outcome_is_committed_before_drain_finishes",
                    saved.Stage == expected && saved.SendAttempts == 1 && peer.Returned == 1
                    && peer.Wires.Count(w => w.ClientId == ClientId(saved)) == 1
                    && saved.AttemptTrustedInputRevision == sending.AttemptTrustedInputRevision
                    && saved.RejectionReason == (expected == OutboxStage.Rejected ? OutboxRejectionReason.ContextExpired : OutboxRejectionReason.None));
                check(prefix + "accepted_event_is_exactly_once_and_unknown_never_claims_success",
                    state.Events.Count(item => item.Source == ConversationSource.Assistant && item.RelatedOutboxId == saved.Id)
                        == (expected == OutboxStage.Accepted ? 1 : 0)
                    && state.Authorizations.IsEmpty && state.Operations.IsEmpty && f.Script.Calls == 0);
                if (expected == OutboxStage.Rejected)
                {
                    var route = Guid.NewGuid();
                    await f.Ledger.ConfigureWakeChannelAsync(identity, new(route, true, TimeZoneInfo.Local.Id));
                    var wake = await f.Ledger.EnsurePendingWakeAsync(identity, route);
                    check(prefix + "old_context_attempt_cannot_create_a_fresh_input_wake",
                        state.Revision > saved.AttemptTrustedInputRevision!.Value && saved.AttemptTrustedInputRevision == 0
                        && wake == null && (await f.Ledger.GetChannelWakesAsync(identity)).IsEmpty && peer.Wires.Count == 1);
                }
            }
            catch (Exception error)
            {
                await EvidenceAsync(directory, "failure-before-cleanup", f, identity, peer, error);
                throw;
            }
            finally
            {
                peer.Release.TrySetResult();
                if (stopping != null) await stopping.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        // Reopen the exact same DPAPI journal, not a copied or in-memory snapshot.
        if (expected == OutboxStage.Rejected)
        {
            await using var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(file), new WindowsStateProtector());
            var state = await reopened.ReadConversationAsync(identity);
            check(prefix + "same_dpapi_file_reopen_preserves_exact_attempt_and_rejection",
                !state.IsReadOnly && state.Outbox.Single(row => row.Id == saved.Id) == saved
                && state.ChannelWakes.IsEmpty && peer.Wires.Count == 1);
            return;
        }
        await using (var resumed = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            savedBinding: binding, coordinatorOptions: Options(), channelRequestOverride: peer.SendAsync))
        {
            var state = await resumed.Ledger.ReadConversationAsync(identity);
            check(prefix + "same_dpapi_file_and_complete_host_reopen_preserve_receipt",
                !state.IsReadOnly && state.RecoveryReason == null && state.Outbox.Single(row => row.Id == saved.Id) == saved
                && peer.Wires.Count(w => w.ClientId == ClientId(saved)) == 1);
            await resumed.InputAsync("fresh-query-after-" + prefix, "状态");
            await UntilAsync(async () => CurrentReplyAccepted(await resumed.ReadAsync()), "reopened current query was not answered");
            state = await resumed.ReadAsync();
            await EvidenceAsync(directory, "after-reopen-and-current-query", resumed, identity, peer);
            check(prefix + "new_query_works_without_blind_replay_of_old_client_id",
                state.Outbox.Single(row => row.Id == saved.Id) == saved
                && peer.Wires.Count(w => w.ClientId == ClientId(saved)) == 1
                && CurrentReplyAccepted(state) && state.ChannelWakes.IsEmpty && resumed.Script.Calls == 0);
        }
    }

    private static async Task BindingSwitchAsync(string directory, Action<string, bool> check)
    {
        const string oldText = "旧绑定长回复标记：";
        var peer = new Peer("{\"ret\":0}");
        Task? changing = null;
        ClawBinding next;
        ConversationIdentity oldIdentity;
        OutboxMessage first;
        Guid[] originalIds;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: true,
            coordinatorOptions: Options(), channelRequestOverride: peer.SendAsync))
        {
            oldIdentity = f.Runtime.Identity!;
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(oldText + new string('甲', 2150)));
            try
            {
                await f.InputAsync("old-long-reply", "请详细说明这次合成测试的已有记录。");
                await peer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var before = await f.Ledger.ReadConversationAsync(oldIdentity);
                var pages = before.Outbox.Where(row => row.Kind == OutboxKind.ConversationReply).OrderBy(row => row.SegmentIndex).ToArray();
                first = pages[0]; originalIds = pages.Select(row => row.Id).ToArray();
                check("binding_original_long_reply_has_unsent_pages_and_exact_first_wire",
                    pages.Length >= 3 && first.Stage == OutboxStage.Sending && pages.Skip(1).All(row => row.Stage == OutboxStage.Pending)
                    && peer.Wires.Single().Body == first.Body && peer.Wires.Single().ClientId == ClientId(first));
                next = new ClawBinding { BaseUrl = f.Binding.BaseUrl, BotId = f.Binding.BotId, UserId = "synthetic-second-user",
                    Token = "SYNTHETIC_SECOND_TOKEN", Cursor = "SYNTHETIC_SECOND_CURSOR", ContextToken = "SYNTHETIC_SECOND_CONTEXT" };
                f.Settings.WeChat.WriteBinding(next);
                changing = f.Channel.ConfigureAsync();
                await UntilAsync(async () => (await f.Ledger.ReadConversationAsync(oldIdentity)).Outbox.Single(row => row.Id == first.Id).Stage == OutboxStage.Unknown,
                    "binding switch did not record the original in-flight attempt as Unknown");
                check("binding_switch_cannot_finish_or_send_a_tail_before_original_http_drains",
                    !changing.IsCompleted && peer.Wires.Count == 1 && peer.Returned == 0);
                peer.Release.TrySetResult();
                await changing.WaitAsync(TimeSpan.FromSeconds(8));
                var old = await f.Ledger.ReadConversationAsync(oldIdentity);
                await EvidenceAsync(directory, "after-binding-switch-real-http", f, oldIdentity, peer);
                check("binding_late_acceptance_stays_on_the_original_identity_and_attempt",
                    old.Outbox.Single(row => row.Id == first.Id).Stage == OutboxStage.Accepted
                    && old.Outbox.Single(row => row.Id == first.Id).SendAttempts == 1
                    && old.Outbox.Where(row => originalIds.Contains(row.Id) && row.Id != first.Id).All(row => row.SendAttempts == 0)
                    && old.Events.Count(row => row.RelatedOutboxId == first.Id && row.Source == ConversationSource.Assistant) == 1
                    && f.Runtime.Identity == DurableAssistantRuntime.BindingIdentity(next, f.Settings.ChatGpt.ActiveAccount));
                f.Binding = next;
                await f.InputAsync("new-binding-current-query", "状态");
                await UntilAsync(async () => CurrentReplyAccepted(await f.ReadAsync()), "the new binding current query did not complete");
                var current = await f.ReadAsync();
                var allWire = peer.Wires.ToArray();
                check("binding_new_identity_query_succeeds_without_receiving_old_remaining_pages",
                    CurrentReplyAccepted(current) && current.Identity != oldIdentity
                    && current.Outbox.All(row => !originalIds.Contains(row.Id) && !row.Body.Contains(oldText, StringComparison.Ordinal))
                    && allWire.Count(row => row.ClientId == ClientId(first)) == 1
                    && allWire.Skip(1).All(row => row.ToUser == next.UserId
                        && current.Outbox.Any(output => ClientId(output) == row.ClientId && output.Body == row.Body))
                    && old.Outbox.Skip(1).All(row => allWire.All(sent => sent.ClientId != ClientId(row))));
            }
            catch (Exception error)
            {
                await EvidenceAsync(directory, "failure-before-cleanup", f, oldIdentity, peer, error);
                throw;
            }
            finally
            {
                peer.Release.TrySetResult();
                if (changing != null) await changing.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            savedBinding: next, coordinatorOptions: Options(), channelRequestOverride: peer.SendAsync);
        var reopenedOld = await reopened.Ledger.ReadConversationAsync(oldIdentity);
        await reopened.InputAsync("new-binding-after-reopen", "状态");
        await UntilAsync(async () => CurrentReplyAccepted(await reopened.ReadAsync()), "reopened new binding query did not complete");
        check("binding_file_reopen_keeps_original_fact_without_replaying_old_pages",
            reopenedOld.Outbox.Single(row => row.Id == first.Id).Stage == OutboxStage.Accepted
            && reopenedOld.Outbox.Where(row => originalIds.Contains(row.Id) && row.Id != first.Id).All(row => row.SendAttempts == 0)
            && peer.Wires.Count(row => row.ClientId == ClientId(first)) == 1
            && peer.Wires.All(row => !originalIds.Skip(1).Any(id => row.ClientId == "assistant-outbox-" + id.ToString("N"))));
        await EvidenceAsync(directory, "after-new-binding-file-reopen", reopened, oldIdentity, peer);
    }

    private static DurableMessageOptions Options() => new()
    { MergeWindow = TimeSpan.Zero, DrainTimeout = TimeSpan.FromMilliseconds(100), DeliveryTimeout = TimeSpan.FromSeconds(15) };
    private static string ClientId(OutboxMessage row) => "assistant-outbox-" + row.Id.ToString("N");
    private static bool CurrentReplyAccepted(ConversationSnapshot state) => state.Outbox.Any(row =>
        row.Kind == OutboxKind.ConversationReply && row.Revision == state.Revision && row.Stage == OutboxStage.Accepted);
    private static async Task UntilAsync(Func<Task<bool>> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(failure);
            await Task.Delay(15);
        }
    }
    private static async Task EvidenceAsync(string directory, string phase, DurableRuntimeTests.Fixture fixture,
        ConversationIdentity identity, Peer peer, Exception? error = null)
    {
        try
        {
            var state = await fixture.Ledger.ReadConversationAsync(identity);
            File.WriteAllText(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(new
            {
                phase, failure = error?.ToString(), realNetwork = false, productionDataRead = false,
                identity, currentIdentity = fixture.Runtime.Identity, state.Revision, state.IsReadOnly, state.RecoveryReason,
                state.Outbox, acceptedEvents = state.Events.Where(row => row.Source == ConversationSource.Assistant).ToArray(),
                state.ChannelWakes, wire = peer.Wires.ToArray(), peer.Returned, peer.FirstResponse,
                channelReceipt = fixture.Channel.LastNotificationAttempt, fixture.Channel.Diagnostics
            }, Settings.Json));
        }
        catch (Exception evidenceError)
        {
            File.WriteAllText(Path.Combine(directory, phase + "-evidence-error.txt"),
                (error?.ToString() ?? "") + Environment.NewLine + evidenceError);
        }
    }
    private sealed record Wire(string ClientId, string Body, string ToUser, int MessageType, int MessageState, int ItemType, bool HasContext);
    private sealed class Peer(string firstResponse)
    {
        internal string FirstResponse => firstResponse;
        internal readonly ConcurrentQueue<Wire> Wires = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sends, returned;
        internal int Returned => Volatile.Read(ref returned);
        internal async Task<HttpResponseMessage?> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected HTTP route in the synthetic late-receipt peer.");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var message = payload.RootElement.GetProperty("msg");
            var item = message.GetProperty("item_list")[0];
            Wires.Enqueue(new(message.GetProperty("client_id").GetString()!, item.GetProperty("text_item").GetProperty("text").GetString()!,
                message.GetProperty("to_user_id").GetString()!, message.GetProperty("message_type").GetInt32(),
                message.GetProperty("message_state").GetInt32(), item.GetProperty("type").GetInt32(),
                !string.IsNullOrWhiteSpace(message.GetProperty("context_token").GetString())));
            int call = Interlocked.Increment(ref sends);
            if (call == 1)
            {
                Entered.TrySetResult();
                // Intentionally ignore ct only in the fake peer. The production
                // HttpClient/API read path receives its ordinary cancellation.
                await Release.Task;
            }
            Interlocked.Increment(ref returned);
            return new(HttpStatusCode.OK) { Content = new StringContent(call == 1 ? firstResponse : "{\"ret\":0}", Encoding.UTF8, "application/json") };
        }
    }
}
