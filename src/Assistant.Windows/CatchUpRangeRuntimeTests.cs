using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

namespace CodexDashboard;

internal static class CatchUpRangeRuntimeTests
{
    private sealed record Wire(string ClientId, string Body);
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--catch-up-range-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--catch-up-range-runtime-test")
                throw new ArgumentException("Explicit fresh output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Fresh output required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output rejected.");
        if (Application.Current?.Dispatcher.CheckAccess() != true) throw new InvalidOperationException("Dispatcher required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "catch-up-range-runtime-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }

        {
            var wire = new ConcurrentQueue<Wire>();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "two-windows"),
                modelEnabled: false, channelRequestOverride: async (request, ct) =>
                { if (await ReadWireAsync(request, ct) is { } sent) wire.Enqueue(sent); return null; });
            string body = "跨窗口开始\n" + string.Concat(Enumerable.Repeat("甲😀乙\n", 850)) + "末尾原样保留  \n";
            var originals = await RejectLongAsync(f, "two-windows", body);
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("window-one", "状态");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var first = await f.ReadAsync();
            var firstPages = BatchPages(first);
            var firstProgress = DurableConversationLedger.GetCatchUpProgress(first).Single();
            Check("first_window_has_three_bounded_pages_and_exact_partial_progress",
                firstPages.Length == 3 && firstPages.All(page => page.Stage == OutboxStage.Accepted && page.Body.Length <= 900)
                && firstProgress.AcceptedCharacters > 0 && firstProgress.RemainingCharacters > 0
                && firstProgress.AcceptedCharacters + firstProgress.RemainingCharacters == body.Length
                && first.CatchUpWindows.Single().DeferredEventCount == 1);
            var workspace = await f.Runtime.ReadWorkspaceAsync();
            Check("desktop_partial_page_is_never_labelled_as_complete",
                firstProgress.Sources.Where(source => source.AcceptedCharacters > 0 && source.AcceptedCharacters < source.TotalCharacters)
                    .Any(source => workspace.Messages.Single(row => row.Id == source.OutboxId.ToString("N"))
                        .State.Contains("部分内容", StringComparison.Ordinal))
                && workspace.Diagnostic.Contains("仍有未确认部分", StringComparison.Ordinal));
            int firstWindowPages = firstPages.Length;
            await f.InputAsync("window-two", "补报记录");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var second = await f.ReadAsync();
            // The fixture uses enough text to require a second window but stay
            // below the combined six-page capacity, including headings.
            Check("second_authenticated_window_finishes_only_remaining_content",
                second.CatchUpBatches.Length == 2 && BatchPages(second).Length > firstWindowPages
                && DurableConversationLedger.GetCatchUpProgress(second).Single().RemainingCharacters == 0
                && ExactAcceptedContent(second, originals));
            Check("original_receipts_and_entire_unicode_body_are_unchanged",
                originals.All(original => second.Outbox.Single(page => page.Id == original.Id) == original)
                && string.Concat(originals.OrderBy(page => page.SegmentIndex).Select(page => page.Body)) == body
                && BatchPages(second).All(page => ValidUtf16(page.Body)));
            Check("each_actual_catch_up_http_request_matches_its_exact_outbox_id",
                BatchPages(second).All(page => wire.Count(item => item.ClientId == ClientId(page)) == 1
                    && wire.Single(item => item.ClientId == ClientId(page)).Body == page.Body)
                && ExactWireContent(second, wire)
                && f.Script.Calls == 0 && second.Operations.IsEmpty && second.Authorizations.IsEmpty);
            await f.InputAsync("query-completed-progress", "补报记录");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            Check("current_query_reports_complete_acceptance_separately_from_phone_delivery",
                f.Sent.Any(text => text.Contains("完整内容已被渠道受理", StringComparison.Ordinal)
                    && text.Contains("接口受理不代表手机已经收到", StringComparison.Ordinal)));
        }

        {
            var wire = new ConcurrentQueue<Wire>();
            bool recovering = false;
            int originalRequests = 0;
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "accepted-prefix"),
                modelEnabled: false, channelRequestOverride: async (request, ct) =>
                {
                    if (await ReadWireAsync(request, ct) is not { } sent) return null;
                    wire.Enqueue(sent);
                    if (recovering) return Response("""{"ret":0}""");
                    return Response(++originalRequests == 1 ? """{"ret":0}""" : """{"ret":-2,"errmsg":"synthetic expired context"}""");
                });
            string body = "PREFIX_ALREADY_ACCEPTED\n" + new string('前', 950) + "\nREMAINING_ONLY\n" + new string('后', 1000) + "\nEND";
            await f.Runtime.EnqueueNotificationAsync("notice/prefix", body, null, default);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page => page.Stage == OutboxStage.Rejected));
            var originals = (await f.ReadAsync()).Outbox.OrderBy(page => page.SegmentIndex).ToArray();
            recovering = true;
            await f.InputAsync("prefix-return", "状态");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var saved = await f.ReadAsync();
            Check("accepted_original_prefix_is_covered_without_copying_or_retrying_it",
                originals[0].Stage == OutboxStage.Accepted
                && saved.CatchUpBatches.SelectMany(batch => batch.Members).SelectMany(member => member.Sources)
                    .All(source => source.OutboxId != originals[0].Id)
                && wire.Count(item => item.ClientId == ClientId(originals[0])) == 1
                && BatchPages(saved).All(page => !page.Body.Contains("PREFIX_ALREADY_ACCEPTED", StringComparison.Ordinal)));
            Check("partial_original_delivery_and_new_ranges_prove_full_coverage",
                ExactAcceptedContent(saved, originals)
                && ExactWireContent(saved, wire)
                && originals.All(original => saved.Outbox.Single(page => page.Id == original.Id) == original)
                && DurableConversationLedger.GetCatchUpProgress(saved).Single().RemainingCharacters == 0);
        }

        {
            var wire = new ConcurrentQueue<Wire>();
            DurableRuntimeTests.Fixture? current = null;
            bool recovering = false;
            int catchUpRequests = 0;
            async Task<HttpResponseMessage?> Send(HttpRequestMessage request, CancellationToken ct)
            {
                if (await ReadWireAsync(request, ct) is not { } sent) return null;
                wire.Enqueue(sent);
                if (!recovering) return Response("""{"ret":-2,"errmsg":"synthetic expired context"}""");
                var snapshot = await current!.ReadAsync();
                if (BatchPages(snapshot).Any(page => ClientId(page) == sent.ClientId) && ++catchUpRequests == 2)
                    return Response("{}");
                return Response("""{"ret":0}""");
            }
            string path = Path.Combine(directory, "unknown-middle");
            ClawBinding binding;
            OutboxMessage[] originals;
            OutboxMessage unknown;
            string originalManifest;
            int attempted;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(path,
                modelEnabled: false, channelRequestOverride: Send))
            {
                current = f;
                originals = await RejectLongAsync(f, "unknown-middle", new string('甲', 3800));
                recovering = true;
                await f.InputAsync("unknown-window", "状态");
                await WaitAsync(async () => BatchPages(await f.ReadAsync()).Any(page => page.Stage == OutboxStage.Unknown)
                    && HasCurrentReply(await f.ReadAsync()));
                var uncertain = await f.ReadAsync();
                unknown = BatchPages(uncertain).Single(page => page.Stage == OutboxStage.Unknown);
                originalManifest = JsonSerializer.Serialize(uncertain.CatchUpBatches, Settings.Json);
                attempted = catchUpRequests;
                binding = f.Settings.WeChat.ReadBinding()!;
            }
            // Dispose the actual runtime and journal writer, then reopen the
            // same encrypted file and identity through a new complete host.
            await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false,
                savedBinding: binding, channelRequestOverride: Send);
            current = resumed;
            var reopened = await resumed.ReadAsync();
            Check("actual_file_restart_preserves_unknown_receipt_and_exact_range_manifest",
                !reopened.IsReadOnly && reopened.RecoveryReason == null && reopened.Outbox.Single(page => page.Id == unknown.Id) == unknown
                && JsonSerializer.Serialize(reopened.CatchUpBatches, Settings.Json) == originalManifest
                && originals.All(original => reopened.Outbox.Single(page => page.Id == original.Id) == original)
                && catchUpRequests == attempted && wire.Count(item => item.ClientId == ClientId(unknown)) == 1);
            await resumed.InputAsync("after-unknown", "补报记录");
            await WaitAsync(async () => HasCurrentReply(await resumed.ReadAsync()));
            var saved = await resumed.ReadAsync();
            var progress = DurableConversationLedger.GetCatchUpProgress(saved).Single();
            Check("unknown_middle_range_blocks_repackaging_of_its_entire_source",
                saved.CatchUpBatches.Length == 1 && catchUpRequests == attempted
                && saved.Outbox.Single(page => page.Id == unknown.Id) == unknown
                && wire.Count(item => item.ClientId == ClientId(unknown)) == 1);
            Check("partial_acceptance_and_unknown_are_not_claimed_as_complete",
                progress.HasUnknown && progress.AcceptedCharacters > 0 && progress.RemainingCharacters > 0
                && !ExactAcceptedContent(saved, originals) && ExactWireContent(saved, wire) && saved.Operations.IsEmpty);
            Check("unknown_history_still_allows_current_query_and_explains_no_retry",
                wire.Any(item => item.Body.Contains("部分内容已受理，剩余保留", StringComparison.Ordinal)
                    && item.Body.Contains("存在未知结果，不自动重发", StringComparison.Ordinal)));
        }

        {
            var wire = new ConcurrentQueue<Wire>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DurableRuntimeTests.Fixture? current = null;
            bool recovering = false;
            int catchUpRequests = 0;
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "new-input-inflight"),
                modelEnabled: false, channelRequestOverride: async (request, ct) =>
                {
                    if (await ReadWireAsync(request, ct) is not { } sent) return null;
                    wire.Enqueue(sent);
                    if (!recovering) return Response("""{"ret":-2,"errmsg":"synthetic expired context"}""");
                    if (BatchPages(await current!.ReadAsync()).Any(page => ClientId(page) == sent.ClientId)
                        && ++catchUpRequests == 2) { entered.TrySetResult(); await release.Task; }
                    return Response("""{"ret":0}""");
                });
            current = f;
            var originals = await RejectLongAsync(f, "inflight", new string('甲', 3000) + "最后保留");
            recovering = true;
            try
            {
                await f.InputAsync("inflight-window", "状态");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var sending = BatchPages(await f.ReadAsync()).Single(page => page.Stage == OutboxStage.Sending);
                await f.InputAsync("inflight-hold", "先别补报");
                release.TrySetResult();
                await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
                var held = await f.ReadAsync();
                Check("new_constraint_preserves_actual_late_acceptance_and_stops_unstarted_pages",
                    held.Outbox.Single(page => page.Id == sending.Id).Stage == OutboxStage.Accepted
                    && BatchPages(held).Any(page => page.Stage == OutboxStage.Superseded && page.SendAttempts == 0)
                    && held.CatchUpBatches.Length == 1 && catchUpRequests == 2);
                await f.InputAsync("inflight-resume", "状态");
                await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
                var saved = await f.ReadAsync();
                Check("fresh_window_after_late_receipt_reuses_exact_accepted_ranges",
                    ExactAcceptedContent(saved, originals)
                    && ExactWireContent(saved, wire)
                    && BatchPages(saved).Where(page => page.Stage == OutboxStage.Accepted)
                        .All(page => wire.Count(item => item.ClientId == ClientId(page)) == 1)
                    && saved.Operations.IsEmpty);
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task<OutboxMessage[]> RejectLongAsync(DurableRuntimeTests.Fixture f, string key, string body)
    {
        f.SendOutcome = """{"ret":-2,"errmsg":"synthetic expired context"}""";
        var result = await f.Runtime.EnqueueNotificationAsync("notice/ranges/" + key, body, null, default);
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page =>
            page.NotificationEventId == result.Event.Id && page.Stage == OutboxStage.Rejected));
        return (await f.ReadAsync()).Outbox.Where(page => page.NotificationEventId == result.Event.Id)
            .OrderBy(page => page.SegmentIndex).ToArray();
    }
    private static OutboxMessage[] BatchPages(ConversationSnapshot saved) => saved.Outbox.Where(page =>
        saved.CatchUpBatches.Any(batch => batch.DeliveryEventId == page.NotificationEventId)).ToArray();
    private static bool HasCurrentReply(ConversationSnapshot saved) => saved.Outbox.Any(page =>
        page.Kind == OutboxKind.ConversationReply && page.Revision == saved.Revision && page.Stage == OutboxStage.Accepted);
    private static string ClientId(OutboxMessage page) => "assistant-outbox-" + page.Id.ToString("N");
    private static bool ExactWireContent(ConversationSnapshot saved, IEnumerable<Wire> wire)
    {
        var sent = wire.ToArray();
        foreach (var batch in saved.CatchUpBatches)
        {
            if (batch.ManifestVersion != 2) return false;
            var window = saved.CatchUpWindows.Single(item => item.Id == batch.WindowId);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(window.Input.DisplayTimeZoneId);
            foreach (var pageId in batch.Members.Select(member => member.CatchUpOutboxId).Distinct())
            {
                var page = saved.Outbox.Single(item => item.Id == pageId);
                var members = batch.Members.Where(member => member.CatchUpOutboxId == pageId).ToArray();
                if (members.Select(member => member.GroupKey).Distinct().Count() != 1) return false;
                var expected = new StringBuilder("你不在的时候，这些" + members[0].GroupKey.Split('|')[0] + "还没发出：\n");
                for (int index = 0; index < members.Length; index++)
                {
                    if (index > 0) expected.Append("\n\n");
                    var member = members[index];
                    var originalEvent = saved.Events.Single(item => item.Id == member.SourceEventId);
                    expected.Append(TimeZoneInfo.ConvertTime(originalEvent.RecordedAt, zone)
                        .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).Append(" 记录（待补报部分）：\n");
                    for (int sourceIndex = 0; sourceIndex < member.Sources.Length; sourceIndex++)
                    {
                        if (sourceIndex > 0) expected.Append('\n');
                        var span = member.Sources[sourceIndex];
                        var original = saved.Outbox.Single(item => item.Id == span.OutboxId);
                        if (span.Length is not > 0 || span.Start < 0 || span.Start > original.Body.Length - span.Length.Value
                            || span.BodyHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.Body)))) return false;
                        expected.Append(original.Body.AsSpan(span.Start, span.Length.Value));
                    }
                }
                if (expected.ToString() != page.Body || !ValidUtf16(page.Body)) return false;
                var requests = sent.Where(item => item.ClientId == ClientId(page)).ToArray();
                if (page.SendAttempts == 0) { if (requests.Length != 0) return false; }
                else if (requests.Length != 1 || requests[0].Body != expected.ToString()) return false;
            }
        }
        return true;
    }
    private static bool ExactAcceptedContent(ConversationSnapshot saved, OutboxMessage[] originals)
    {
        foreach (var original in originals)
        {
            if (original.Stage == OutboxStage.Accepted) continue;
            var spans = saved.CatchUpBatches.SelectMany(batch => batch.Members)
                .Where(member => saved.Outbox.Single(page => page.Id == member.CatchUpOutboxId).Stage == OutboxStage.Accepted)
                .SelectMany(member => member.Sources).Where(source => source.OutboxId == original.Id)
                .OrderBy(source => source.Start).ToArray();
            int offset = 0;
            var rebuilt = new StringBuilder();
            foreach (var span in spans)
            {
                int length = span.Length ?? original.Body.Length;
                if (span.Start != offset || length <= 0 || span.Start + length > original.Body.Length
                    || span.SendAttempts != original.SendAttempts
                    || span.BodyHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.Body)))) return false;
                rebuilt.Append(original.Body.Substring(span.Start, length)); offset += length;
            }
            if (offset != original.Body.Length || rebuilt.ToString() != original.Body) return false;
        }
        return true;
    }
    private static bool ValidUtf16(string body)
    {
        for (int i = 0; i < body.Length; i++)
        {
            if (char.IsLowSurrogate(body[i])) return false;
            if (char.IsHighSurrogate(body[i]) && (++i >= body.Length || !char.IsLowSurrogate(body[i]))) return false;
        }
        return true;
    }
    private static async Task<Wire?> ReadWireAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var message = payload.RootElement.GetProperty("msg");
        return new(message.GetProperty("client_id").GetString()!,
            message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!);
    }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic range delivery condition not reached.");
            await Task.Delay(20);
        }
    }
}
