using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AgentAdapterTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--agent-adapter-test");
        if (index < 0) return false;
        try { await RunAsync(args[index + 1]); exit(0); }
        catch (Exception ex) { Console.Error.WriteLine(ex); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string key, bool ok)
        {
            checks.Add(key, ok);
            if (!ok) throw new InvalidOperationException(key);
        }
        try
        {
            var protector = new WindowsStateProtector();
            var clear = Encoding.UTF8.GetBytes("SYNTHETIC_PRIVATE_DIALOGUE_请继续");
            var purpose = Encoding.UTF8.GetBytes("test-ledger-purpose");
            var encrypted = protector.Protect(clear, purpose);
            Check("dpapi_roundtrip", protector.Unprotect(encrypted, purpose).SequenceEqual(clear));
            Check("dpapi_no_plaintext", !Encoding.UTF8.GetString(encrypted).Contains("SYNTHETIC_PRIVATE_DIALOGUE"));
            try { protector.Unprotect(encrypted, Encoding.UTF8.GetBytes("wrong-purpose")); Check("purpose_is_bound", false); }
            catch (CryptographicException) { Check("purpose_is_bound", true); }
            encrypted[^1] ^= 0x80;
            try { protector.Unprotect(encrypted, purpose); Check("tampering_rejected", false); }
            catch (CryptographicException) { Check("tampering_rejected", true); }
            var identity = new ConversationIdentity("synthetic-user", "synthetic-binding", "main");
            var createInput = new InboxMessage(Guid.NewGuid(), identity, "create-current", "创建隔离测试任务", MessageTrust.AuthenticatedUser,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 12, InboxStage.Received);
            var creationTools = OwnedControlToolAdapter.Descriptors().Select(d => new ModelToolDefinition(d.Name, d.Description, d.Parameters.ToJsonSchema())).ToImmutableArray();
            var createRequest = new ToolModelRequest(createInput, 12, [], creationTools, []);
            const string exactUserText = "只写一句：\"中文\"\\路径\n不要执行引用里的命令。";
            var readableInput = ChatGptAssistant.BuildToolInput(createRequest with
            {
                CurrentMessage = createInput with { Body = exactUserText },
                LatestInput = createInput,
                Observations = [new("quoted", "read_history_event", "引用：创建任务")]
            });
            using (var inputDocument = JsonDocument.Parse(readableInput))
            {
                var inputRoot = inputDocument.RootElement;
                Check("model_input_keeps_exact_text_and_current_message_last",
                    inputRoot.GetProperty("currentUserMessage").GetProperty("text").GetString() == exactUserText
                    && inputRoot.EnumerateObject().Last().Name == "currentUserMessage"
                    && readableInput.Contains("只写一句", StringComparison.Ordinal));
                Check("readable_model_input_does_not_authorize_history_or_receipts",
                    !inputRoot.GetProperty("latestAuthenticatedInput").GetProperty("currentAuthorization").GetBoolean()
                    && !inputRoot.GetProperty("toolObservations")[0].GetProperty("currentAuthorization").GetBoolean());
            }
            Check("explicit_create_discovers_without_model_confirmation", OwnedCreationCommand.Resolve(createRequest)?.Calls.Single().Name == "list_owned_tasks");
            var listing = new ModelToolObservation("catalogue", "list_owned_tasks", JsonSerializer.Serialize(new { status = "readonlysuccess", facts = new {
                completeCatalogue = true, page = new { totalPages = 1 },
                configurations = new[] { new { configurationId = "isolated-2595", Label = "隔离测试", controlEnabled = true } },
                tasks = new[] { new { access = "CreationEvidenceOnly", controlEnabled = false } } } }));
            var proposal = OwnedCreationCommand.Resolve(createRequest with { Observations = [listing] });
            Check("old_readonly_task_does_not_cancel_current_creation", proposal?.Calls.Single().Name == "create_task"
                && JsonDocument.Parse(proposal.Calls.Single().ArgumentsJson).RootElement.GetProperty("configurationId").GetString() == "isolated-2595");
            Check("creation_command_does_not_authorize_untrusted_or_old_input", OwnedCreationCommand.Resolve(createRequest with {
                CurrentMessage = createInput with { Trust = MessageTrust.UntrustedContent } }) == null
                && OwnedCreationCommand.Resolve(createRequest with { Revision = 13 }) == null);
            Check("negated_creation_is_not_a_command", OwnedCreationCommand.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "不要创建隔离测试任务" } }) == null);
            var accepted = new ModelToolObservation("created", "create_task", JsonSerializer.Serialize(new { status = "accepted", facts = new {
                newlyBoundTaskEvidence = new { task = new { ThreadId = "synthetic-created-id" } } } }));
            var receiptReply = OwnedCreationCommand.Resolve(createRequest with { Observations = [listing, accepted] });
            Check("creation_receipt_returns_concrete_id_without_another_model_call", receiptReply?.Calls.Length == 0
                && receiptReply.ReplySegments.Single().Contains("任务列表", StringComparison.Ordinal) && !receiptReply.ReplySegments.Single().Contains("synthetic-created-id"));
            var selectedCreation = new ModelToolObservation("created", "create_task", JsonSerializer.Serialize(new { status = "accepted", facts = new {
                selectedCurrentTask = true, newlyBoundTaskEvidence = new { task = new { ThreadId = "synthetic-created-id" } } } }));
            Check("creation_selected_task_uses_short_next_step", OwnedCreationCommand.Resolve(createRequest with { Observations = [selectedCreation] })
                ?.ReplySegments.Single().Contains("继续当前任务：") == true);
            var continuationRequest = createRequest with { CurrentMessage = createInput with { Body = "继续 synthetic-created-id：执行测试" },
                Observations = [new("dispatch", "continue_owned_task", "{\"status\":\"accepted\"}")] };
            var dispatchReply = OwnedControlReply.Resolve(continuationRequest);
            Check("resume_only_reply_does_not_claim_work_started", OwnedControlReply.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "恢复控制 00000000-0000-0000-0000-000000000001" },
                Observations = [new("resume", "resume_owned_task", "{\"status\":\"accepted\"}")] })?.ReplySegments.Single().Contains("还没有启动") == true);
            Check("resume_before_continuation_keeps_tool_loop_open", OwnedControlReply.Resolve(continuationRequest with {
                Observations = [new("resume", "resume_owned_task", "{\"status\":\"accepted\"}")] }) == null);
            Check("accepted_continuation_finishes_reply_without_new_call_or_completion_claim", dispatchReply?.Calls.Length == 0
                && dispatchReply.ReplySegments.Single() == "已把这条要求交给任务处理。");
            var unknownReply = OwnedControlReply.Resolve(continuationRequest with {
                Observations = [new("dispatch", "continue_owned_task", "{\"status\":\"unknown\"}")] });
            Check("unknown_continuation_reports_uncertainty_without_retry", unknownReply?.Calls.Length == 0
                && unknownReply.ReplySegments.Single().Contains("没有重发", StringComparison.Ordinal));
            Check("placeholder_target_never_selects_an_old_task", OwnedControlReply.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "继续 完整任务编号：测试" } })?.Calls.Length == 0);
            foreach (string body in new[] { "继续 实际编号：只回复“微信控制链路正常”。不要使用工具或访问文件。", "查询任务 实际编号 的最新结果。" })
                Check("actual_placeholder_no_tools_" + body, OwnedControlReply.Resolve(createRequest with {
                    CurrentMessage = createInput with { Body = body } })?.Calls.Length == 0);
            Check("multiple_task_receipts_remain_available_for_combined_reply", OwnedControlReply.Resolve(continuationRequest with {
                Observations = [new("a", "continue_owned_task", "{\"status\":\"accepted\"}"),
                    new("b", "continue_owned_task", "{\"status\":\"running\"}")] }) == null);
            const string reportId = "00000000-0000-0000-0000-000000000001";
            ModelToolObservation Ready(string id = reportId, string access = "ResumedThisSession", bool refreshed = true)
                => new("ready", "read_owned_task", JsonSerializer.Serialize(new { status = "readonlysuccess", facts = new {
                    refreshed, task = new { configurationId = "demo", identity = new { ThreadId = id, HostId = "local", ProfileId = "test" }, access, state = "Completed", Revision = 7 } } }));
            var readyRequest = createRequest with { CurrentMessage = createInput with {
                Body = "继续 " + reportId + "：只回复测试通过，不要使用工具或访问文件。" }, Observations = [Ready()] };
            var proposed = OwnedControlReply.Resolve(readyRequest)?.Calls.Single();
            using (var arguments = JsonDocument.Parse(proposed!.ArgumentsJson))
                Check("fresh_resumed_task_continues_exact_current_request", proposed.Name == "continue_owned_task"
                    && arguments.RootElement.GetProperty("text").GetString() == readyRequest.CurrentMessage.Body
                    && arguments.RootElement.GetProperty("revision").GetInt64() == 7);
            Check("continuation_requires_same_fresh_controllable_target", OwnedControlReply.Resolve(readyRequest with { Observations = [Ready(id: "other")] }) == null
                && OwnedControlReply.Resolve(readyRequest with { Observations = [Ready(access: "RestoredReadOnly")] }) == null
                && OwnedControlReply.Resolve(readyRequest with { Observations = [Ready(refreshed: false)] }) == null);
            Check("later_read_never_repeats_unknown_dispatch", OwnedControlReply.Resolve(readyRequest with {
                Observations = [new("sent", "continue_owned_task", "{\"status\":\"unknown\"}"), Ready()] }) == null);
            var selectedContext = new ConversationContext(identity, 1, 1, 1, Focus: new(new("local", reportId, ProfileId: "test"), "choice", "选择 1", DateTimeOffset.UtcNow));
            var focusedRequest = readyRequest with { CurrentMessage = createInput with { Body = "继续当前任务：只回复测试通过。" }, ConversationContext = selectedContext };
            Check("focused_command_proposes_exact_selected_task", OwnedControlReply.Resolve(focusedRequest)?.Calls.Single().ArgumentsJson.Contains(reportId) == true);
            Check("focused_command_without_selection_never_guesses", OwnedControlReply.Resolve(focusedRequest with { ConversationContext = null }) == null);
            Check("focused_command_checks_endpoint_profile", OwnedControlReply.Resolve(focusedRequest with {
                ConversationContext = selectedContext with { Focus = selectedContext.Focus! with { Task = selectedContext.Focus.Task with { ProfileId = "other" } } } }) == null);
            Check("interrupt_receipt_does_not_claim_task_stopped", OwnedControlReply.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "中断 " + reportId },
                Observations = [new("stop", "interrupt_turn", "{\"status\":\"accepted\"}")] })?.ReplySegments.Single().Contains("还需核对") == true);
            Check("unarchive_receipt_does_not_claim_task_started", OwnedControlReply.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "取消归档 " + reportId },
                Observations = [new("restore", "unarchive_owned_task", "{\"status\":\"accepted\"}")] })?.ReplySegments.Single().Contains("还没有启动") == true);
            Check("compound_control_request_keeps_model_loop_open", OwnedControlReply.Resolve(createRequest with {
                CurrentMessage = createInput with { Body = "取消归档 " + reportId + "，然后继续检查" },
                Observations = [new("restore", "unarchive_owned_task", "{\"status\":\"accepted\"}")] }) == null);
            ModelToolObservation Report(string id = reportId, int pages = 1, bool truncated = false, string text = "测试通过。\n保留原文。")
                => new("report", "read_owned_report", JsonSerializer.Serialize(new { status = "readonlysuccess", facts = new {
                    task = new { ThreadId = id }, finalText = text, sourceTruncated = truncated,
                    page = new { totalPages = pages, number = 1, snapshotId = new string('a', 32) } } }));
            var reportRequest = createRequest with { CurrentMessage = createInput with { Body = "查询任务 " + reportId + " 的最新结果" },
                Observations = [Report()] };
            Check("complete_exact_report_returns_original_text_with_source", OwnedControlReply.Resolve(reportRequest)?.ReplySegments.Single()
                == "任务回复：\n测试通过。\n保留原文。");
            Check("report_analysis_remains_model_work", OwnedControlReply.Resolve(reportRequest with {
                CurrentMessage = createInput with { Body = "分析任务 " + reportId + " 的结果是否可靠" } }) == null);
            Check("partial_report_does_not_masquerade_as_complete", OwnedControlReply.Resolve(reportRequest with { Observations = [Report(pages: 2)] })?.ReplySegments.Single().Contains("报告下一页") == true
                && OwnedControlReply.Resolve(reportRequest with { Observations = [Report(truncated: true)] })?.ReplySegments.Single().Contains("不代表完整报告") == true);
            Check("empty_report_does_not_claim_success", OwnedControlReply.Resolve(reportRequest with { Observations = [Report(text: "")] })?.ReplySegments.Single().Contains("没有最终答复正文") == true);
            Check("different_task_report_is_not_returned", OwnedControlReply.Resolve(reportRequest with { Observations = [Report(id: "other")] }) == null);
            Check("dispatch_receipt_does_not_hide_later_report", OwnedControlReply.Resolve(continuationRequest with {
                Observations = [continuationRequest.Observations.Single(), Report()] }) == null);
            Check("durable_unknown_receipt_never_invites_resend", OwnedControlReply.Resolve(continuationRequest with {
                Observations = [new("dispatch", "continue_owned_task", "{\"status\":\"unknown_needs_reconciliation\"}")] })?.ReplySegments.Single().Contains("没有重发") == true);
            string journal = Path.Combine(directory, "synthetic-dialogue.state");
            await using (var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(journal), protector))
                await ledger.ReceiveAsync(identity, "m1", "SYNTHETIC_SECRET_MESSAGE", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            await using (var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(journal), protector))
                Check("real_dpapi_ledger_restart", (await ledger.ReadConversationAsync(identity)).Inbox.Single().Body == "SYNTHETIC_SECRET_MESSAGE");
            Check("ledger_file_contains_no_body", !Encoding.UTF8.GetString(File.ReadAllBytes(journal)).Contains("SYNTHETIC_SECRET_MESSAGE"));

            var reply = ChatGptAssistant.ParseToolResponse("""{"calls":[],"replies":["好，我在。"]}""");
            var firstRound = ChatGptAssistant.ParseStructuredToolResponse("""{"calls":[{"id":"1","name":"list_tasks","argumentsJson":"{}"}],"replies":[]}""").Calls.Single();
            var secondRound = ChatGptAssistant.ParseStructuredToolResponse("""{"calls":[{"id":"1","name":"list_tasks","argumentsJson":"{\"hostId\":\"local\"}"}],"replies":[]}""").Calls.Single();
            await using (var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "call-ids.state")), protector))
            {
                await ledger.ReceiveAsync(identity, "rounds", "查询任务", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                var lease = await ledger.BeginGenerationAsync(identity, ["rounds"]);
                await ledger.RegisterToolInvocationAsync(lease, "rounds", firstRound.Id, firstRound.Name, firstRound.ArgumentsJson, false);
                await ledger.RegisterToolInvocationAsync(lease, "rounds", secondRound.Id, secondRound.Name, secondRound.ArgumentsJson, false);
                Check("model_ids_are_response_local", (await ledger.ReadConversationAsync(identity)).ToolInvocations.Length == 2
                    && firstRound.Id != secondRound.Id && secondRound.ArgumentsJson == "{\"hostId\":\"local\"}");
            }
            try
            {
                ChatGptAssistant.ParseStructuredToolResponse("""{"calls":[{"id":"1","name":"list_tasks","argumentsJson":"{}"},{"id":"1","name":"list_tasks","argumentsJson":"{}"}],"replies":[]}""");
                Check("duplicate_ids_in_one_response_rejected", false);
            }
            catch (JsonException) { Check("duplicate_ids_in_one_response_rejected", true); }
            Check("natural_reply_json", reply.Calls.Length == 0 && reply.ReplySegments.Single() == "好，我在。");
            var call = ChatGptAssistant.ParseToolResponse("""{"calls":[{"id":"call-1","name":"list_tasks","arguments":{"hostId":"local"}}],"replies":[]}""");
            Check("tool_arguments_preserved", call.Calls.Single().ArgumentsJson == """{"hostId":"local"}""");
            var structured = ChatGptAssistant.ParseStructuredToolResponse("""{"calls":[{"id":"s1","name":"list_tasks","argumentsJson":"{\"hostId\":\"local\"}"}],"replies":[]}""");
            Check("structured_arguments_preserved", structured.Calls.Single().ArgumentsJson == """{"hostId":"local"}""");
            try { ChatGptAssistant.ParseStructuredToolResponse("""{"calls":[{"id":"s1","name":"list_tasks","argumentsJson":"[]"}],"replies":[]}"""); Check("structured_nonobject_rejected", false); }
            catch (JsonException) { Check("structured_nonobject_rejected", true); }
            var invalid = new Dictionary<string, string>
            {
                ["mixed_call_and_reply"] = """{"calls":[{"id":"a","name":"read_task","arguments":{}}],"replies":["已执行"]}""",
                ["empty_response"] = """{"calls":[],"replies":[]}""",
                ["duplicate_root_field"] = """{"calls":[],"replies":["a"],"replies":["b"]}""",
                ["extra_field"] = """{"calls":[],"replies":["a"],"authorization":true}""",
                ["duplicate_call_field"] = """{"calls":[{"id":"a","name":"read_task","name":"send_to_task","arguments":{}}],"replies":[]}""",
                ["duplicate_call_id"] = """{"calls":[{"id":"a","name":"list_tasks","arguments":{}},{"id":"a","name":"list_tasks","arguments":{}}],"replies":[]}""",
                ["string_arguments"] = """{"calls":[{"id":"a","name":"list_tasks","arguments":"{}"}],"replies":[]}""",
                ["code_fence"] = "```json\n{\"calls\":[],\"replies\":[\"a\"]}\n```",
                ["empty_reply"] = """{"calls":[],"replies":[" "]}""",
                ["invalid_tool_name"] = """{"calls":[{"id":"a","name":"../shell","arguments":{}}],"replies":[]}""",
                ["extra_segments"] = """{"calls":[],"replies":["a","b","c","d"]}"""
            };
            foreach (var item in invalid)
            {
                try { ChatGptAssistant.ParseToolResponse(item.Value); Check(item.Key, false); }
                catch (JsonException) { Check(item.Key, true); }
            }
            var account = new ChatGptAccount
            {
                ClientId = "oaiapp_synthetic_tools", Subject = "synthetic-subject", Email = "test@example.invalid",
                AccessToken = "SYNTHETIC_ACCESS", RefreshToken = "SYNTHETIC_REFRESH", Scope = ChatGptAuth.Scopes,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            string vaultPath = Path.Combine(directory, "synthetic.credentials");
            new ChatGptVault { Accounts = new() { account } }.Save(vaultPath);
            using var auth = new ChatGptAuth(vaultPath, new Stub((_, _) => throw new InvalidOperationException("No auth network expected.")));
            var settings = new Settings { ChatGpt = new() { Enabled = true, ActiveAccount = account.ClientId, Model = "synthetic-model" } };
            string raw = """{"calls":[{"id":"request-1","name":"list_tasks","argumentsJson":"{}"}],"replies":[]}""";
            string sse = "data: " + JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new { status = "completed", output = new[] {
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = raw } } }
                } }
            }) + "\n\n";
            int requests = 0;
            using var model = new ChatGptAssistant(settings, () => { }, auth, new Stub(async (request, ct) =>
            {
                requests++;
                Check("same_responses_authorization_transport", request.RequestUri!.ToString() == ChatGptAuth.Resource + "/responses"
                    && request.Headers.Authorization?.Scheme == "Bearer");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var format = body.RootElement.GetProperty("text").GetProperty("format");
                Check("tool_transport_enforces_schema", format.GetProperty("type").GetString() == "json_schema"
                    && format.GetProperty("strict").GetBoolean()
                    && !format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
                var instructions = body.RootElement.GetProperty("instructions").GetString()!;
                Check("tool_mode_does_not_claim_no_tools_ever", instructions.Contains("真实工具回执")
                    && !instructions.Contains("本次对话步骤没有执行工具"));
                Check("native_functions_not_claimed_verified", !body.RootElement.TryGetProperty("tools", out _));
                string input = body.RootElement.GetProperty("input").GetRawText();
                Check("persistent_context_and_current_source_in_prompt", input.Contains("current-message")
                    && input.Contains("historical-notification") && input.Contains("currentAuthorization"));
                return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }));
            var message = new InboxMessage(Guid.NewGuid(), identity, "current-message", "看看任务", MessageTrust.AuthenticatedUser,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2, InboxStage.Processing);
            var events = ImmutableArray.Create(new ConversationEvent(Guid.NewGuid(), identity, ConversationSource.Notification,
                "historical-notification", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
            var output = await ((IToolModel)model).GenerateAsync(new(message, 2, events,
                ImmutableArray.Create(new ModelToolDefinition("list_tasks", "Read tasks", JsonSerializer.SerializeToElement(new { type = "object" }))), []), default);
            Check("fake_responses_tool_roundtrip", requests == 1 && output.Calls.Single().Name == "list_tasks");
        }
        finally { File.WriteAllText(Path.Combine(directory, "adapter-checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
    }
}
