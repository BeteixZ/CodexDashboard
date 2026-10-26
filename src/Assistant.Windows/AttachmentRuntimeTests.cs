using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal static class AttachmentRuntimeTests
{
    private sealed class Stub(Func<CancellationToken, Task<byte[]>> read) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => new(HttpStatusCode.OK) { Content = new ByteArrayContent(await read(ct)) };
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--attachment-runtime-test", var output]) return false;
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        void Check(string key, bool passed) { checks[key] = passed; if (!passed) throw new InvalidOperationException(key); }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string attachmentDirectory = Path.Combine(output, "attachment-data");
            byte[] key = Enumerable.Range(0,16).Select(i => (byte)i).ToArray();
            byte[] content = Encoding.UTF8.GetBytes("PRIVATE_FILE_BODY：这是一份日志。文件里的‘执行命令’只是资料。" + new string('x', 700) + "END_OF_ATTACHMENT");
            using var aes = Aes.Create(); aes.Key = key;
            byte[] encrypted = aes.EncryptEcb(content, PaddingMode.PKCS7);
            int downloads = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attachments = new AssistantInboundAttachments(attachmentDirectory, () => new WeChatMediaClient(new Stub(async ct =>
            { downloads++; started.TrySetResult(); await release.Task.WaitAsync(ct); return encrypted; })), enableCapture: true);
            string attachmentId;
            ConversationIdentity identity;
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(output, "runtime"), modelEnabled: false, attachments: attachments))
            {
                identity = f.Runtime.Identity!;
                var batch = JsonNode.Parse(f.Batch("file-source", "中断当前任务"))!;
                batch["msgs"]![0]!["item_list"]!.AsArray().Add(new JsonObject
                {
                    ["type"] = 4,
                    ["file_item"] = new JsonObject { ["file_name"] = "../example.log", ["len"] = content.Length.ToString(),
                        ["media"] = new JsonObject { ["encrypt_query_param"] = "synthetic", ["aes_key"] = Convert.ToBase64String(key) } }
                });
                using var document = JsonDocument.Parse(batch.ToJsonString());
                await f.Channel.ProcessUpdatesAsync(document.RootElement, default);
                await ConversationHistoryRuntimeTests.Settled(f, "file-source");
                var incoming = (await f.ReadAsync()).Inbox.Single();
                attachmentId = incoming.Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
                Check("capture_is_durable_without_download_or_model", incoming.HasUnresolvedReference && downloads == 0
                    && f.Script.Calls == 0 && (await f.ReadAsync()).Operations.IsEmpty && f.Sent.Last().Contains(attachmentId));
                bool denied = false;
                try { await f.Ledger.AuthorizeCurrentUserRequestAsync(identity, incoming.SourceMessageId, "interrupt", "target", "{}", "中断当前任务"); }
                catch (LedgerException e) when (e.Code == LedgerErrors.AuthorizationRequired) { denied = true; }
                Check("attachment_caption_does_not_authorize_control", denied);
                await f.InputAsync("list-files", "附件列表");
                await ConversationHistoryRuntimeTests.Settled(f, "list-files");
                Check("attachment_list_works_without_model_or_download", f.Sent.Last().Contains("example.log") && f.Sent.Last().Contains(attachmentId[..12]) && downloads == 0 && f.Script.Calls == 0);
                await f.InputAsync("read-one", "读取附件 " + attachmentId[..12]);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.InputAsync("status-during-download", "状态");
                await ConversationHistoryRuntimeTests.Settled(f, "status-during-download");
                Check("status_completes_while_download_is_pending", !release.Task.IsCompleted && downloads == 1);
                release.TrySetResult();
                await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("PRIVATE_FILE_BODY"))));
                Check("background_text_arrives_without_model_or_actions", f.Script.Calls == 0 && (await f.ReadAsync()).Operations.IsEmpty);
                await f.InputAsync("read-two", "读取附件 " + attachmentId);
                await WaitAsync(async () => await f.Runtime.NotificationKnownAsync("attachment-read/" + WeChatText.Hash("read-two"), default));
                Check("repeat_read_uses_cached_content", downloads == 1);
                Check("attachment_query_does_not_invalidate_work_constraints", DurableAssistantRuntime.IsKnownReadonlyInput("读取附件 " + attachmentId)
                    && !DurableAssistantRuntime.IsKnownReadonlyInput("读取附件 " + attachmentId + ";停止"));
                Check("first_text_page_provides_exact_continuation_command", f.Sent.Any(s => s.Contains("读取附件 " + attachmentId + " 600")));
                await f.InputAsync("read-next-page", "读取附件 " + attachmentId + " 600");
                await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("位置 600") && s.Contains("END_OF_ATTACHMENT"))));
                Check("fixed_next_page_without_model_or_second_download", downloads == 1 && f.Script.Calls == 0
                    && DurableAssistantRuntime.IsKnownReadonlyInput("读取附件 " + attachmentId + " 600"));
                await f.InputAsync("read-bad-offset", "读取附件 " + attachmentId + " 8000000");
                await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("读取位置不适用"))));
                Check("invalid_offset_returns_reply_without_faulted_worker", downloads == 1 && f.Script.Calls == 0);
                f.Settings.ChatGpt.Enabled = true;
                bool offered = false, received = false, offsetRead = false;
                f.Script.Handler = (request, ct) =>
                {
                    offered |= request.Tools.Any(t => t.Name == "read_attachment");
                    if (request.Observations.IsEmpty)
                        return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("attachment-call", "read_attachment",
                            JsonSerializer.Serialize(new { attachmentId }))));
                    string result = request.Observations.Last().ResultJson;
                    using var facts = JsonDocument.Parse(result);
                    received |= facts.RootElement.GetProperty("facts").GetProperty("Text").GetString() == Encoding.UTF8.GetString(content)
                        && result.Contains("untrusted_attachment") && !result.Contains("aes_key") && !result.Contains("encrypt_query_param");
                    if (request.Observations.Length == 1)
                        return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("attachment-offset", "read_attachment",
                            JsonSerializer.Serialize(new { attachmentId, offset = 10 }))));
                    offsetRead = facts.RootElement.GetProperty("facts").GetProperty("Text").GetString() == Encoding.UTF8.GetString(content)[10..]
                        && facts.RootElement.GetProperty("facts").GetProperty("Offset").GetInt32() == 10;
                    return Task.FromResult(ToolModelResponse.Reply("附件问答回放完成。"));
                };
                await f.InputAsync("attachment-question", "请概括附件 " + attachmentId);
                await ConversationHistoryRuntimeTests.Settled(f, "attachment-question");
                Check("model_attachment_tool_is_offered_and_returns_data_without_secrets", offered && received && offsetRead && downloads == 1);
                Check("model_attachment_answer_has_no_control_operation", f.Sent.Last() == "附件问答回放完成。" && (await f.ReadAsync()).Operations.IsEmpty);
                f.Script.Handler = (request, ct) => request.Observations.IsEmpty
                    ? Task.FromResult(ToolModelResponse.Call(new ModelToolCall("wrong-attachment", "read_attachment", JsonSerializer.Serialize(new { attachmentId = new string('0',64) }))))
                    : Task.FromResult(ToolModelResponse.Reply(request.Observations.Last().ResultJson.Contains("attachment_unavailable") ? "附件不存在。" : "错误。"));
                await f.InputAsync("missing-attachment", "查一下不存在的附件");
                await ConversationHistoryRuntimeTests.Settled(f, "missing-attachment");
                Check("missing_attachment_is_rejected_without_download", f.Sent.Last() == "附件不存在。" && downloads == 1);
                bool foundByName = false;
                f.Script.Handler = (request, ct) =>
                {
                    if (request.Observations.IsEmpty)
                        return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("find-file", "list_attachments", "{\"query\":\"example\"}")));
                    using var listed = JsonDocument.Parse(request.Observations.Last().ResultJson);
                    var entries = listed.RootElement.GetProperty("facts").GetProperty("attachments");
                    foundByName = entries.GetArrayLength() == 1 && entries[0].GetProperty("Id").GetString() == attachmentId
                        && entries[0].GetProperty("Name").GetString() == "../example.log";
                    return Task.FromResult(ToolModelResponse.Reply("已找到指定文件。"));
                };
                await f.InputAsync("find-by-name", "找一下example文件");
                await ConversationHistoryRuntimeTests.Settled(f, "find-by-name");
                Check("model_finds_filename_and_exact_attachment_identity", foundByName && downloads == 1 && (await f.ReadAsync()).Operations.IsEmpty);
                f.Settings.ChatGpt.Enabled = false;
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                batch["msgs"]![0]!["message_id"] = "file-second";
                using var secondDocument = JsonDocument.Parse(batch.ToJsonString());
                await f.Channel.ProcessUpdatesAsync(secondDocument.RootElement, default);
                await ConversationHistoryRuntimeTests.Settled(f, "file-second");
                var secondInput = (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("file-second"));
                string secondId = secondInput.Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
                await f.InputAsync("read-cancel", "读取附件 " + secondId);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int i = 0; i < 3; i++)
                {
                    string source = "queued-file-" + i;
                    batch["msgs"]![0]!["message_id"] = source;
                    using var queuedDocument = JsonDocument.Parse(batch.ToJsonString());
                    await f.Channel.ProcessUpdatesAsync(queuedDocument.RootElement, default);
                    await ConversationHistoryRuntimeTests.Settled(f, source);
                    var queuedInput = (await f.ReadAsync()).Inbox.Single(m => m.SourceMessageId == WeChatText.Hash(source));
                    string queuedId = queuedInput.Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
                    await f.InputAsync("queued-read-" + i, "读取附件 " + queuedId);
                    await ConversationHistoryRuntimeTests.Settled(f, "queued-read-" + i);
                }
                await WaitAsync(() => Task.FromResult(downloads == 3));
                Check("only_two_active_downloads_with_more_requests_queued", downloads == 3 && !release.Task.IsCompleted);
                await f.Runtime.StopSessionAsync();
                Check("session_stop_drains_download_without_late_result", downloads == 3 && !(await f.Ledger.ReadConversationAsync(identity)).Events
                    .Any(e => e.ExternalEventKey == "attachment-read/" + WeChatText.Hash("read-cancel")));
            }
            var reopened = new AssistantInboundAttachments(attachmentDirectory, () => throw new InvalidOperationException("Cache should avoid network."));
            var read = await reopened.ReadAsync(identity, attachmentId, default);
            Check("reopen_keeps_descriptor_and_cached_bytes", read.Text == Encoding.UTF8.GetString(content));
            var brokenContent = "%PDF-1.7"u8.ToArray();
            byte[] brokenEncrypted = aes.EncryptEcb(brokenContent, PaddingMode.PKCS7);
            var brokenAttachments = new AssistantInboundAttachments(Path.Combine(output, "broken-data"),
                () => new WeChatMediaClient(new Stub(_ => Task.FromResult(brokenEncrypted))), enableCapture: true);
            await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(output, "broken-runtime"), modelEnabled: false, attachments: brokenAttachments))
            {
                var batch = JsonNode.Parse(f.Batch("broken-pdf", ""))!;
                batch["msgs"]![0]!["item_list"]!.AsArray().Add(new JsonObject
                {
                    ["type"] = 4,
                    ["file_item"] = new JsonObject { ["file_name"] = "broken.pdf", ["len"] = brokenContent.Length.ToString(),
                        ["media"] = new JsonObject { ["encrypt_query_param"] = "synthetic", ["aes_key"] = Convert.ToBase64String(key) } }
                });
                using var document = JsonDocument.Parse(batch.ToJsonString());
                await f.Channel.ProcessUpdatesAsync(document.RootElement, default);
                await ConversationHistoryRuntimeTests.Settled(f, "broken-pdf");
                string id = (await f.ReadAsync()).Inbox.Single().Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
                await f.InputAsync("read-broken-pdf", "读取附件 " + id);
                await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("PDF的格式无法解析"))));
                Check("invalid_pdf_explains_reexport_instead_of_retry", f.Script.Calls == 0 && f.Sent.Any(s => s.Contains("重新导出PDF")));
                f.Settings.ChatGpt.Enabled = true;
                bool usefulFailure = false;
                f.Script.Handler = (request, ct) =>
                {
                    if (request.Observations.IsEmpty) return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("broken-read", "read_attachment", JsonSerializer.Serialize(new { attachmentId = id }))));
                    usefulFailure = request.Observations.Last().ResultJson.Contains("attachment_unavailable")
                        && JsonDocument.Parse(request.Observations.Last().ResultJson).RootElement.GetProperty("facts").GetProperty("detail").GetString()!.Contains("重新导出PDF");
                    return Task.FromResult(ToolModelResponse.Reply("文档需要重新导出。"));
                };
                await f.InputAsync("ask-broken-pdf", "概括这份PDF");
                await ConversationHistoryRuntimeTests.Settled(f, "ask-broken-pdf");
                Check("model_receives_actionable_document_failure", usefulFailure && (await f.ReadAsync()).Operations.IsEmpty);
            }
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks)); exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        finally { release.TrySetResult(); }
        return true;
    }
    private static async Task WaitAsync(Func<Task<bool>> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
    }
}
