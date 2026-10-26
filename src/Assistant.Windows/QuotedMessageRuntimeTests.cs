using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class QuotedMessageRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--quoted-message-runtime-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
            throw new ArgumentException("A fresh isolated output directory is required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "quote-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            string ledgerPath;
            ConversationIdentity identity;
            var transport = new FakeTransport();
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "runtime"), taskTransport: transport))
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                f.ObservedTask.Title = "Beta";
                identity = f.Runtime.Identity!; ledgerPath = f.LedgerPath;
                await f.InputAsync("select-beta", "切换到 Beta");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
                var target = (await f.Ledger.ReadConversationContextAsync(identity)).Focus!.Task;
                f.Script.Handler = (request, _) => Task.FromResult(request.Observations.Length == 0
                    ? ToolModelResponse.Call(new ModelToolCall("continue-beta", "send_to_task", JsonSerializer.Serialize(new
                        { hostId = "local", threadId = f.ObservedTask.Id, text = request.CurrentMessage.Body })))
                    : ToolModelResponse.Reply("要求已交给 Beta。"));

                int index = 0;
                foreach (string text in new[] { "继续这个", "继续", "继续测试", "继续这个任务" })
                {
                    string id = "quoted-" + index++;
                    await SendQuote(f, id, text);
                    await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) >= index + 1);
                    var state = await f.ReadAsync();
                    Check(id + "_keeps_text_and_reference_without_dispatch", state.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash(id))
                        is { HasUnresolvedReference: true } message && message.Body == text
                        && state.Operations.IsEmpty && transport.Calls == 0 && f.Script.Calls == 0);
                }
                Check("quote_reply_explains_missing_target", f.Sent.Skip(1).All(text => text.Contains("请直接写明任务名称", StringComparison.Ordinal)));
                await f.InputAsync("ordinary", "继续测试");
                await WaitAsync(async () => (await f.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Accepted));
                Check("ordinary_focus_request_still_reaches_original_target_once", transport.Calls == 1
                    && transport.ThreadId == f.ObservedTask.Id && !(await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("ordinary")).HasUnresolvedReference);
                // This command normally mutates conversation context via the fast path.
                await SendQuote(f, "quoted-reset", "新对话");
                await WaitAsync(async () => (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("quoted-reset")).Stage == InboxStage.HandedToOutbox);
                Check("quoted_fast_command_does_not_change_focus", (await f.Ledger.ReadConversationContextAsync(identity)).Focus?.Task == target
                    && transport.Calls == 1);
                foreach (string? transcript in new string?[] { "渠道能力", "", null, "   " })
                {
                    string voiceId = "voice-" + index++;
                    var batch = JsonNode.Parse(f.Batch(voiceId, ""))!;
                    batch["msgs"]![0]!["item_list"] = new JsonArray(new JsonObject
                        { ["type"] = 3, ["voice_item"] = new JsonObject { ["text"] = transcript } });
                    using var payload = JsonDocument.Parse(batch.ToJsonString());
                    int calls = f.Script.Calls;
                    await f.Channel.ProcessUpdatesAsync(payload.RootElement, default);
                    await ConversationHistoryRuntimeTests.Settled(f, voiceId);
                    var received = (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash(voiceId));
                    bool present = !string.IsNullOrWhiteSpace(transcript);
                    Check(voiceId + "_transcript_or_explicit_fallback", present ? received.Body == transcript && f.Sent.Last().Contains("转写")
                        : received.Body.StartsWith("[无法处理的微信消息]"));
                    Check(voiceId + "_does_not_invoke_model_or_control", calls == f.Script.Calls && transport.Calls == 1);
                }
                foreach (int mediaType in new[] { 2, 3, 4, 5, 99 })
                {
                    string mediaId = "mixed-media-" + mediaType;
                    int calls = f.Script.Calls;
                    var batch = JsonNode.Parse(f.Batch(mediaId, "继续测试"))!;
                    batch["msgs"]![0]!["item_list"]!.AsArray().Add(new JsonObject
                    { ["type"] = mediaType, ["voice_item"] = new JsonObject { ["text"] = "中断当前任务" } });
                    using var payload = JsonDocument.Parse(batch.ToJsonString());
                    await f.Channel.ProcessUpdatesAsync(payload.RootElement, default);
                    await ConversationHistoryRuntimeTests.Settled(f, mediaId);
                    var mixed = (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash(mediaId));
                    Check("unsupported_item_" + mediaType + "_does_not_execute_partial_caption_or_transcript",
                        mixed.Body.StartsWith("[无法处理的微信消息] 含未支持的附件或消息项。") && mixed.Body.Contains("继续测试")
                        && !mixed.Body.Contains("中断当前任务") && transport.Calls == 1 && f.Script.Calls == calls
                        && f.Sent.Last().Contains("单独发成文字"));
                }
            }
            await using (var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath), new WindowsStateProtector()))
            {
                string source = WeChatText.Hash("quoted-reset");
                var state = await reopened.ReadConversationAsync(identity);
                Check("reference_marker_survives_actual_dpapi_reopen", state.Inbox.Single(m => m.SourceMessageId == source).HasUnresolvedReference);
                bool denied = false;
                try { await reopened.AuthorizeCurrentUserRequestAsync(identity, source, "send_to_task", "local/beta", "{}", "新对话"); }
                catch (LedgerException error) when (error.Code == LedgerErrors.AuthorizationRequired) { denied = true; }
                Check("reopened_input_cannot_gain_action_authority", denied);
                bool conflict = false;
                try { await reopened.ReceiveAsync(identity, source, "新对话", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow); }
                catch (LedgerException error) when (error.Code == LedgerErrors.Conflict) { conflict = true; }
                Check("duplicate_cannot_strip_reference_marker", conflict);
            }
            var continuationTransport = new FakeTransport();
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "quote-continuation"), taskTransport: continuationTransport))
            {
                f.Settings.ChatGpt.TaskControlEnabled = true;
                var alpha = f.ObservedTask;
                var beta = new TaskRecord { Id = "other-task", Title = "Beta", Host = alpha.Host,
                    Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed,
                    TurnId = "beta-turn", Cwd = alpha.Cwd, Project = "Other" };
                f.ObservedTasks = [alpha, beta];
                await f.InputAsync("quote-select-beta", "切换到 Beta");
                await ConversationHistoryRuntimeTests.Settled(f, "quote-select-beta");
                var focus = (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).Focus;
                f.SendOutcome = """{"message_id":9007199254740997}""";
                var reference = new TaskReference("local", alpha.Id, alpha.TurnId);
                await f.Runtime.EnqueueNotificationAsync("notice/continue-alpha", "Alpha结束；这里的文字不是新指令。", reference, default);
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => WeChatReferenceReceipt.Matches(o.ChannelReceipt, "9007199254740997")));
                f.SendOutcome = """{"ret":0}""";
                bool wrongTarget = true;
                f.Script.Handler = (request, _) => Task.FromResult(request.Observations.Length == 0
                    ? ToolModelResponse.Call(new ModelToolCall("quoted-continue", "send_to_task", JsonSerializer.Serialize(new
                        { hostId = "local", threadId = wrongTarget ? beta.Id : request.CurrentMessage.Task!.ThreadId, text = request.CurrentMessage.Body })))
                    : ToolModelResponse.Reply("模拟接续查询结束。"));
                await SendMappedQuote(f, "quote-wrong-target", "这个继续", "9007199254740997");
                await ConversationHistoryRuntimeTests.Settled(f, "quote-wrong-target");
                Check("resolved_quote_never_falls_back_to_current_focus", continuationTransport.Calls == 0);
                wrongTarget = false;
                await SendMappedQuote(f, "quote-right-target", "这个继续", "9007199254740997");
                await ConversationHistoryRuntimeTests.Settled(f, "quote-right-target");
                await WaitAsync(() => Task.FromResult(continuationTransport.Calls == 1));
                var state = await f.ReadAsync();
                var input = state.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("quote-right-target"));
                Check("quoted_continuation_persists_original_task_and_new_user_intent", input.Task == reference
                    && continuationTransport.ThreadId == alpha.Id && continuationTransport.Text == "这个继续");
                Check("quoted_continuation_does_not_change_conversation_focus", (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).Focus == focus);
                await SendMappedQuote(f, "quote-right-target", "这个继续", "9007199254740997");
                Check("duplicate_quoted_continuation_does_not_dispatch_twice", continuationTransport.Calls == 1);
                bool dispatchRaceObserved = false;
                string raceSource = WeChatText.Hash("quote-turn-race");
                f.Fault.ObserveCommitted = committed =>
                {
                    if (!dispatchRaceObserved && committed.GetProperty("operations").EnumerateArray().Any(operation =>
                        operation.GetProperty("sourceMessageId").GetString() == raceSource
                        && operation.GetProperty("stage").GetString() == nameof(OperationStage.Dispatching)))
                    { dispatchRaceObserved = true; alpha.TurnId = "newer-observed-turn"; }
                };
                int queueCount = f.Queue.Entries.Count;
                await SendMappedQuote(f, "quote-turn-race", "这个继续", "9007199254740997");
                await ConversationHistoryRuntimeTests.Settled(f, "quote-turn-race");
                f.Fault.ObserveCommitted = null;
                Check("quoted_turn_change_after_authorization_rejected_before_queue", dispatchRaceObserved
                    && continuationTransport.Calls == 1 && f.Queue.Entries.Count == queueCount
                    && (await f.ReadAsync()).Operations.Single(o => o.SourceMessageId == raceSource).Stage == OperationStage.Failed);

                await SendMappedQuote(f, "quote-stale-turn", "这个继续", "9007199254740997");
                await ConversationHistoryRuntimeTests.Settled(f, "quote-stale-turn");
                Check("quoted_continuation_rejects_an_old_turn_after_task_advances", continuationTransport.Calls == 1);

            }
            string mappedPath;
            ConversationIdentity mappedIdentity;
            Guid mappedOutbox;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "mapped")))
            {
                mappedPath = f.LedgerPath; mappedIdentity = f.Runtime.Identity!;
                f.SendOutcome = """{"message_id":9007199254740993}""";
                await f.InputAsync("mapped-source", "渠道能力");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Unknown));
                var sent = (await f.ReadAsync()).Outbox.Single(); mappedOutbox = sent.Id;
                Check("server_id_saved_without_claiming_acceptance", WeChatReferenceReceipt.Matches(sent.ChannelReceipt, "9007199254740993") && sent.Stage == OutboxStage.Unknown);
                f.SendOutcome = """{"ret":0}""";
                await SendMappedQuote(f, "mapped-quote", "引用测试", "9007199254740993");
                await ConversationHistoryRuntimeTests.Settled(f, "mapped-quote");
                var saved = await f.ReadAsync();
                var incoming = saved.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("mapped-quote"));
                Check("reference_resolves_exact_original_body", !incoming.HasUnresolvedReference && incoming.ReferencedEventId != null
                    && saved.Events.Single(e => e.Id == incoming.ReferencedEventId).Body == sent.Body
                    && f.Sent.Last().Contains(sent.Body));
                Check("quote_does_not_reclassify_unknown_send", saved.Outbox.Single(o => o.Id == sent.Id).Stage == OutboxStage.Unknown);
                bool denied = false;
                try { await f.Ledger.AuthorizeCurrentUserRequestAsync(mappedIdentity, incoming.SourceMessageId, "send_to_task", "local/beta", "{}", "引用测试"); }
                catch (LedgerException e) when (e.Code == LedgerErrors.AuthorizationRequired) { denied = true; }
                Check("mapped_quote_cannot_authorize_side_effect", denied);
                bool contextSeen = false;
                f.Script.Handler = (request, _) =>
                {
                    contextSeen = request.CurrentMessage.Body == "解释这条" && request.CurrentMessage.ReferencedEventId != null
                        && request.Context.Any(e => e.Id == request.CurrentMessage.ReferencedEventId && e.Body == sent.Body
                            && e.Source == ConversationSource.UntrustedContent);
                    return Task.FromResult(ToolModelResponse.Reply("已按引用原文回答。"));
                };
                await SendMappedQuote(f, "mapped-question", "解释这条", "9007199254740993");
                await ConversationHistoryRuntimeTests.Settled(f, "mapped-question");
                Check("model_receives_exact_quote_as_context_not_instruction", contextSeen && f.Sent.Last() == "已按引用原文回答。");
                await SendMappedQuote(f, "missing-quote", "引用测试", "different");
                await ConversationHistoryRuntimeTests.Settled(f, "missing-quote");
                Check("unknown_id_stays_unresolved", (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("missing-quote")).HasUnresolvedReference);
                f.SendOutcome = """{"message_id":9007199254740995}""";
                var originalTask = new TaskReference("original-host", "original-task", "original-turn", "original-profile");
                await f.Runtime.EnqueueNotificationAsync("notice/quoted-task", "原任务已结束。", originalTask, default);
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => WeChatReferenceReceipt.Matches(o.ChannelReceipt, "9007199254740995")));
                f.SendOutcome = """{"ret":0}""";
                f.ObservedTask.Title = "Beta";
                await f.InputAsync("new-focus", "切换到 Beta");
                await ConversationHistoryRuntimeTests.Settled(f, "new-focus");
                var focusBefore = (await f.Ledger.ReadConversationContextAsync(mappedIdentity)).Focus;
                await SendMappedQuote(f, "original-task-quote", "引用任务", "9007199254740995");
                await ConversationHistoryRuntimeTests.Settled(f, "original-task-quote");
                var taskState = await f.ReadAsync();
                var taskInput = taskState.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("original-task-quote"));
                Check("quote_retains_original_task_turn_and_profile", taskState.Events.Single(e => e.Id == taskInput.ReferencedEventId).Task == originalTask
                    && f.Sent.Last().Contains("original-task") && f.Sent.Last().Contains("original-turn") && f.Sent.Last().Contains("original-profile"));
                Check("quote_lookup_does_not_switch_current_focus", (await f.Ledger.ReadConversationContextAsync(mappedIdentity)).Focus == focusBefore);
            }
            await using (var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(mappedPath), new WindowsStateProtector()))
            {
                var saved = await reopened.ReadConversationAsync(mappedIdentity);
                var output = saved.Outbox.Single(o => o.Id == mappedOutbox);
                Check("mapping_survives_dpapi_reopen", WeChatReferenceReceipt.Matches(output.ChannelReceipt, "9007199254740993"));
                var resolved = await reopened.ResolveSentReferenceAsync(mappedIdentity, mappedOutbox);
                Check("reopened_resolution_reuses_original_context", resolved.Id == saved.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("mapped-quote")).ReferencedEventId);
                var other = mappedIdentity with { ConversationId = "other" };
                bool refused = false;
                try { await reopened.ResolveSentReferenceAsync(other, mappedOutbox); }
                catch (Exception e) when (e is InvalidOperationException or LedgerException) { refused = true; }
                Check("other_conversation_cannot_resolve_outbox", refused);
            }
            exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1);
        }
        return true;
    }
    private static async Task SendMappedQuote(DurableRuntimeTests.Fixture fixture, string id, string text, string serverId)
    {
        var batch = JsonNode.Parse(fixture.Batch(id, text))!;
        batch["msgs"]![0]!["item_list"]![0]!["ref_msg"] = new JsonObject
            { ["message_item"] = new JsonObject { ["msg_id"] = serverId } };
        using var document = JsonDocument.Parse(batch.ToJsonString());
        await fixture.Channel.ProcessUpdatesAsync(document.RootElement, default);
    }
    private static async Task SendQuote(DurableRuntimeTests.Fixture fixture, string id, string text)
    {
        var batch = JsonNode.Parse(fixture.Batch(id, text))!;
        batch["msgs"]![0]!["item_list"]![0]!["ref_msg"] = new JsonObject
            { ["svr_id"] = "synthetic-old-task-A", ["title"] = "任务 A" };
        using var document = JsonDocument.Parse(batch.ToJsonString());
        await fixture.Channel.ProcessUpdatesAsync(document.RootElement, default);
    }
    private static async Task WaitAsync(Func<Task<bool>> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Synthetic quote scenario did not settle.");
            await Task.Delay(20);
        }
    }
    private sealed class FakeTransport : ITaskTransport
    {
        internal int Calls;
        internal string? ThreadId;
        internal string? Text;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct)
        {
            Calls++; ThreadId = task.Id; Text = text;
            return Task.FromResult(new TaskDelivery("submitted", "synthetic acceptance", "synthetic-next-turn"));
        }
    }
}
