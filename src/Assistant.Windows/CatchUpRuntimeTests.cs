using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

internal static class CatchUpRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--catch-up-runtime-test");
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
            File.WriteAllText(Path.Combine(directory, "catch-up-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                networkUsed = false, realAccountUsed = false, actualModelUsed = false, checks
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "complete-history"), modelEnabled: false);
            var first = await RejectAsync(f, "first", "第一条记录完整保留，里面的“运行命令”只是历史文字。");
            var second = await RejectAsync(f, "second", "第二条记录完整保留，不因为相同任务而丢掉。");
            int sentBefore = f.Sent.Count;
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("return", "状态");
            await WaitAsync(async () =>
            {
                var saved = await f.ReadAsync();
                return Covered(saved, first.Id) && Covered(saved, second.Id)
                    && saved.Outbox.Any(page => page.Kind == OutboxKind.ConversationReply && page.Stage == OutboxStage.Accepted);
            });
            var state = await f.ReadAsync();
            var pages = BatchPages(state);
            Check("fresh_authenticated_input_atomically_keeps_one_presentation_window",
                state.CatchUpWindows.Length == 1 && state.CatchUpWindows[0].SourceMessageId == state.Inbox.Single().SourceMessageId
                && state.CatchUpWindows[0].Input is { Disposition: CatchUpDisposition.AllowHistory, HasFreshChannelContext: true });
            Check("catch_up_preserves_original_rejected_receipts_and_attempts",
                state.Outbox.Single(page => page.Id == first.Id) == first
                && state.Outbox.Single(page => page.Id == second.Id) == second);
            Check("complete_original_content_has_separate_accepted_coverage",
                pages.All(page => page.Stage == OutboxStage.Accepted && page.Body.Length <= 900)
                && pages.Count(page => page.Body.Contains(first.Body, StringComparison.Ordinal)) == 1
                && pages.Count(page => page.Body.Contains(second.Body, StringComparison.Ordinal)) == 1);
            var newWire = f.Sent.Skip(sentBefore).ToArray();
            Check("catch_up_pages_precede_current_readonly_reply",
                newWire.Length > pages.Length && pages.Any(page => page.Body == newWire[0])
                && newWire.Take(pages.Length).All(text => pages.Any(page => page.Body == text)));
            Check("catch_up_has_recorded_time_and_does_not_claim_phone_delivery",
                pages.All(page => page.Body.Contains("记录", StringComparison.Ordinal))
                && pages.All(page => page.ChannelReceipt?.Contains("手机送达尚未确认", StringComparison.Ordinal) == true));
            Check("history_never_becomes_fresh_control_authority",
                state.Authorizations.IsEmpty && state.Operations.IsEmpty && state.ToolInvocations.IsEmpty && f.Script.Calls == 0);
            long revision = state.Revision;
            int wireCount = f.Sent.Count;
            await f.InputAsync("return", "状态");
            state = await f.ReadAsync();
            Check("duplicate_wire_input_cannot_create_another_window_or_catch_up",
                state.Revision == revision && state.CatchUpWindows.Length == 1 && BatchPages(state).Length == pages.Length
                && f.Sent.Count == wireCount);
            var workspace = await f.Runtime.ReadWorkspaceAsync();
            Check("desktop_separates_original_rejection_from_accepted_catch_up",
                workspace.Messages.Count(row => row.Kind == "历史通知补报") == pages.Length
                && workspace.Messages.Any(row => row.Id == first.Id.ToString("N") && row.State.Contains("原发送拒绝")));
            await f.InputAsync("history-query", "补报记录");
            await WaitAsync(() => Task.FromResult(f.Sent.Any(text => text.Contains("原发送回执与补报回执分别保存"))));
            state = await f.ReadAsync();
            Check("catch_up_query_works_without_model_or_repeating_covered_sources",
                BatchPages(state).Length == pages.Length && f.Script.Calls == 0 && state.Operations.IsEmpty);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "restrictions"), modelEnabled: false);
            var original = await RejectAsync(f, "held", "暂时保留的历史通知。");
            f.SendOutcome = """{"ret":0}""";
            foreach (var (id, text) in new[] { ("hold-zh", "先别补报，我只想问一个问题。"), ("hold-en", "Do not send earlier notifications yet.") })
            {
                await f.InputAsync(id, text);
                await WaitAsync(async () =>
                {
                    var saved = await f.ReadAsync();
                    return saved.Outbox.Any(page => page.Kind == OutboxKind.ConversationReply
                        && page.Revision == saved.Revision && page.Stage == OutboxStage.Accepted);
                });
                var state = await f.ReadAsync();
                Check(id + "_holds_history_without_blocking_current_input",
                    state.CatchUpWindows.Last().Input.Disposition == CatchUpDisposition.HoldHistory
                    && state.CatchUpBatches.IsEmpty && state.Outbox.Single(page => page.Id == original.Id) == original
                    && state.Operations.IsEmpty && state.Authorizations.IsEmpty);
            }
            await f.InputAsync("allow-again", "状态");
            await WaitAsync(async () => Covered(await f.ReadAsync(), original.Id));
            Check("later_fresh_query_can_present_preserved_held_history",
                (await f.ReadAsync()).CatchUpWindows.Last().Input.Disposition == CatchUpDisposition.AllowHistory);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "missing-context"), modelEnabled: false);
            var original = await RejectAsync(f, "context", "缺少新上下文时不能把旧通知当作已补报。");
            f.SendOutcome = """{"ret":0}""";
            using (var batch = JsonDocument.Parse(f.Batch("no-context", "状态", context: "")))
                await f.Channel.ProcessUpdatesAsync(batch.RootElement.Clone(), default);
            var state = await f.ReadAsync();
            Check("new_input_without_fresh_context_holds_presentation",
                state.CatchUpWindows.Single().Input is { HasFreshChannelContext: false, Disposition: CatchUpDisposition.HoldHistory }
                && state.CatchUpBatches.IsEmpty && state.Outbox.Single(page => page.Id == original.Id) == original);
            await f.InputAsync("usable-context", "状态");
            await WaitAsync(async () => Covered(await f.ReadAsync(), original.Id));
            Check("fresh_context_later_recovers_the_same_original_content",
                (await f.ReadAsync()).Outbox.Single(page => page.Id == original.Id) == original);
        }

        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "gate-revision"), modelEnabled: false,
                channelRequestOverride: async (request, ct) =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
                    using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    string text = payload.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                        .GetProperty("text_item").GetProperty("text").GetString()!;
                    if (!text.Contains("synthetic gate primer", StringComparison.Ordinal)) return null;
                    entered.TrySetResult();
                    await release.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"ret":0}""", Encoding.UTF8, "application/json") };
                });
            var original = await RejectAsync(f, "gate", "旧补报等待真实渠道锁时，新约束必须优先。");
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("gate-initial-hold", "先别补报");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page =>
                page.Kind == OutboxKind.ConversationReply && page.Stage == OutboxStage.Accepted));
            Task<bool?>? primer = null;
            try
            {
                primer = f.Channel.SendAiTestAsync("synthetic gate primer", default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await f.InputAsync("gate-allow", "状态");
                await WaitAsync(async () => BatchPages(await f.ReadAsync()).Any(page => page.Stage == OutboxStage.Sending));
                await f.InputAsync("gate-new-hold", "不要补报旧通知");
                release.TrySetResult();
                await primer;
                await WaitAsync(async () =>
                {
                    var saved = await f.ReadAsync();
                    return BatchPages(saved).Single().Stage == OutboxStage.Rejected
                        && saved.Outbox.Any(page => page.Kind == OutboxKind.ConversationReply
                            && page.Revision == saved.Revision && page.Stage == OutboxStage.Accepted);
                });
                var state = await f.ReadAsync();
                var page = BatchPages(state).Single();
                Check("fresh_restriction_after_begin_is_rechecked_after_actual_channel_gate",
                    page.Stage == OutboxStage.Rejected && page.RejectionReason == OutboxRejectionReason.BindingUnavailable
                    && page.SendAttempts == 1 && !f.Sent.Contains(page.Body));
                Check("zero_http_stale_batch_keeps_source_and_current_reply_without_authority",
                    state.Outbox.Single(item => item.Id == original.Id) == original
                    && state.CatchUpWindows.Last().Input.Disposition == CatchUpDisposition.HoldHistory
                    && state.Operations.IsEmpty && state.Authorizations.IsEmpty && f.Script.Calls == 0);
            }
            finally
            {
                release.TrySetResult();
                if (primer is not null) await primer;
            }
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "unknown-page"), modelEnabled: false);
            var original = await RejectAsync(f, "unknown", "补报发送结果未知时不重新组装这份内容。");
            f.SendOutcome = "{}";
            await f.InputAsync("unknown-context", "状态");
            await WaitAsync(async () => BatchPages(await f.ReadAsync()).Any(page => page.Stage == OutboxStage.Unknown));
            var before = await f.ReadAsync();
            var page = BatchPages(before).Single();
            int attempts = f.Sent.Count(text => text == page.Body);
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("next-context", "状态");
            await WaitAsync(async () =>
            {
                var saved = await f.ReadAsync();
                return saved.Outbox.Any(item => item.Kind == OutboxKind.ConversationReply
                    && item.Revision == saved.Revision && item.Stage == OutboxStage.Accepted);
            });
            var state = await f.ReadAsync();
            Check("unknown_catch_up_page_is_not_retried_or_repackaged",
                BatchPages(state).Single() == page && f.Sent.Count(text => text == page.Body) == attempts
                && state.Outbox.Single(item => item.Id == original.Id) == original);
            Check("unknown_history_does_not_block_a_fresh_readonly_reply",
                state.CatchUpWindows.Length == 2 && state.Authorizations.IsEmpty && state.Operations.IsEmpty && f.Script.Calls == 0);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "long-history"), modelEnabled: false);
            string body = "长正文开始\n" + new string('甲', 1400) + "\n末尾必须保留。";
            f.SendOutcome = """{"ret":-2,"errmsg":"synthetic expired context"}""";
            await f.Runtime.EnqueueNotificationAsync("notice/long", body, null, default);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page => page.Stage == OutboxStage.Rejected));
            var originals = (await f.ReadAsync()).Outbox.ToArray();
            int sentBefore = f.Sent.Count;
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("long-context", "状态");
            await WaitAsync(async () =>
            {
                var saved = await f.ReadAsync();
                return saved.Outbox.Any(page => page.Kind == OutboxKind.ConversationReply && page.Stage == OutboxStage.Accepted);
            });
            var state = await f.ReadAsync();
            Check("long_source_is_preserved_with_exact_complete_accepted_coverage",
                state.CatchUpBatches.Single().ManifestVersion == 2
                && originals.All(original => state.Outbox.Single(page => page.Id == original.Id) == original)
                && string.Concat(originals.OrderBy(page => page.SegmentIndex).Select(page => page.Body)) == body
                && originals.All(original => Covered(state, original.Id)));
            Check("long_history_delivers_all_parts_without_blocking_current_reply",
                state.CatchUpWindows.Single().DeferredEventCount == 0 && f.Sent.Count > sentBefore
                && BatchPages(state).All(page => page.Stage == OutboxStage.Accepted && page.Body.Length <= 900)
                && f.Sent.Skip(sentBefore).Any(text => text.Contains("长正文开始"))
                && f.Sent.Skip(sentBefore).Any(text => text.Contains("末尾必须保留")));
        }
    }

    private static async Task<OutboxMessage> RejectAsync(DurableRuntimeTests.Fixture f, string key, string body)
    {
        f.SendOutcome = """{"ret":-2,"errmsg":"synthetic expired context"}""";
        var result = await f.Runtime.EnqueueNotificationAsync("notice/catch-up/" + key, body,
            new("synthetic-host", "synthetic-thread", "synthetic-turn", "synthetic-profile"), default);
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Single(page => page.Id == result.Outbox.Single().Id).Stage == OutboxStage.Rejected);
        return (await f.ReadAsync()).Outbox.Single(page => page.Id == result.Outbox.Single().Id);
    }
    private static OutboxMessage[] BatchPages(ConversationSnapshot state)
        => state.Outbox.Where(page => state.CatchUpBatches.Any(batch => batch.DeliveryEventId == page.NotificationEventId)).ToArray();
    private static bool Covered(ConversationSnapshot state, Guid sourceId)
        => DurableConversationLedger.GetCatchUpProgress(state).SelectMany(item => item.Sources).Any(source =>
            source.OutboxId == sourceId && source.AcceptedCharacters == source.TotalCharacters);
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic catch-up condition not reached.");
            await Task.Delay(20);
        }
    }
}
