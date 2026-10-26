using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal sealed partial class DurableAssistantRuntime
{
    private readonly AssistantInboundAttachments? attachments;
    private readonly bool imageAnalysisEnabled;
    private readonly Func<string, AssistantImageInput, CancellationToken, Task<string>>? imageAnalyzer;
    private CancellationTokenSource attachmentStop = new();
    private readonly Dictionary<string, Task> attachmentReads = new();
    private readonly SemaphoreSlim attachmentSlots = new(2, 2);
    internal string? CaptureAttachment(ClawBinding binding, string sourceId, JsonElement items)
    {
        var active = BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
        if (!IsCurrentBinding(active)) return null;
        try { return attachments?.Capture(active, sourceId, items); }
        catch (WeChatMediaException) { return null; }
    }
    private const string AttachmentReadPattern = @"\A读取附件 ([0-9A-Fa-f]{8,64})(?: ([0-9]{1,8}))?\z";
    private static bool TryParseAttachmentRead(string text, out string id, out int offset)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, AttachmentReadPattern);
        id = match.Success ? match.Groups[1].Value : "";
        offset = match.Success && match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return match.Success && offset <= AssistantAttachmentStore.MaximumBytes;
    }
    private async Task<DurableFastReply> StartAttachmentReadAsync(InboxMessage message, string id, CancellationToken ct, int offset = 0)
    {
        if (attachments == null) return new(["这个实例尚未开启附件读取。"]);
        try
        {
            if (id.Length is >= 8 and < 64 && id.All(Uri.IsHexDigit))
            {
                var matches = (await ListAttachmentsAsync(message.Identity, null, ct)).Where(a => a.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) return new(["这个附件短编号无法唯一定位，请发送“附件列表”核对完整编号。"]);
                id = matches[0].Id;
            }
            var descriptor = attachments.Descriptor(message.Identity, id);
            var saved = await ledger.ReadConversationAsync(message.Identity, ct);
            if (!saved.Inbox.Any(m => m.SourceMessageId == AssistantInboundAttachments.OriginalSource(descriptor)
                && m.HasUnresolvedReference && m.Body.StartsWith(AssistantInboundAttachments.Marker + id + "\n", StringComparison.Ordinal)))
                return new(["找不到这份附件对应的原消息，请重新发送附件。"]);
            foreach (string finished in attachmentReads.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) attachmentReads.Remove(finished);
            if (attachmentReads.ContainsKey(id)) return new(["这份附件正在读取，结果会在这里回复。"]);
            attachmentReads[id] = ReadAttachmentInBackgroundAsync(message, id, offset, attachmentStop.Token);
            return new(["正在读取附件，读完会在这里回复。"]);
        }
        catch (Exception error) when (error is IOException or CryptographicException or ArgumentException or JsonException or WeChatMediaException)
        { return new(["这份附件暂时无法读取，请核对编号或重新发送。"]); }
    }
    private async Task ReadAttachmentInBackgroundAsync(InboxMessage request, string id, int offset, CancellationToken ct)
    {
        await Task.Yield();
        string reply;
        try
        {
            await attachmentSlots.WaitAsync(ct);
            AttachmentContent content;
            try { content = await attachments!.ReadAsync(request.Identity, id, ct, offset, maximumCharacters: 600); }
            finally { attachmentSlots.Release(); }
            reply = content.Text != null ? (offset == 0 ? "附件中的文字如下：\n" : "附件文字（位置 " + offset + " 起）：\n") + (content.ExtractionNote == null ? "" : content.ExtractionNote + "\n") + content.Text
                + (content.NextOffset is { } next ? "\n继续读取：读取附件 " + id + " " + next : "\n已读到文件末尾。")
                : "附件已保存；格式为 " + content.Kind + "，当前尚不能提取这类内容。";
        }
        catch (ArgumentOutOfRangeException) { reply = "这个读取位置不适用于附件。请先发“读取附件 " + id + "”，再使用回复中的继续读取命令。"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException or WeChatMediaException or OperationCanceledException)
        { reply = AssistantAttachmentFailure.Describe(error); }
        if (ct.IsCancellationRequested || !IsCurrentBinding(request.Identity)) return;
        try { await coordinator.EnqueueNotificationAsync(request.Identity, "attachment-read/" + request.SourceMessageId, reply, null, cancellationToken: ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (LedgerException) { LastError = "attachment_read_result_not_recorded"; }
    }
    private async Task<AttachmentContent> ReadAttachmentForModelAsync(ConversationIdentity active, string id, int offset, CancellationToken ct)
    {
        if (!IsCurrentBinding(active)) throw new WeChatMediaException("attachment_binding_changed");
        var descriptor = attachments!.Descriptor(active, id);
        var saved = await ledger.ReadConversationAsync(active, ct);
        if (!saved.Inbox.Any(m => m.SourceMessageId == AssistantInboundAttachments.OriginalSource(descriptor)
            && m.HasUnresolvedReference && m.Body.StartsWith(AssistantInboundAttachments.Marker + id + "\n", StringComparison.Ordinal)))
            throw new WeChatMediaException("attachment_source_missing");
        await attachmentSlots.WaitAsync(ct);
        try
        {
            var content = await attachments.ReadAsync(active, id, ct, offset);
            if (!IsCurrentBinding(active)) throw new WeChatMediaException("attachment_binding_changed");
            return content;
        }
        finally { attachmentSlots.Release(); }
    }
    private async Task<JsonElement> AnalyzeAttachmentImageAsync(ConversationIdentity active, string id, string question, CancellationToken ct)
    {
        string expectedModel = settings.ChatGpt.Model;
        void RequireCurrent()
        {
            ct.ThrowIfCancellationRequested();
            if (!settings.ChatGpt.Enabled || settings.ChatGpt.Model != expectedModel || !IsCurrentBinding(active)) throw new OperationCanceledException(ct);
        }
        RequireCurrent();
        var descriptor = attachments!.Descriptor(active, id);
        var saved = await ledger.ReadConversationAsync(active, ct);
        if (!saved.Inbox.Any(m => m.SourceMessageId == AssistantInboundAttachments.OriginalSource(descriptor)
            && m.HasUnresolvedReference && m.Body.StartsWith(AssistantInboundAttachments.Marker + id + "\n", StringComparison.Ordinal)))
            throw new WeChatMediaException("attachment_source_missing");
        AssistantImageInput image;
        await attachmentSlots.WaitAsync(ct);
        try { image = AssistantImageInput.FromBytes(await attachments.ReadBytesAsync(active, id, ct)); }
        finally { attachmentSlots.Release(); }
        RequireCurrent();
        string answer = imageAnalyzer != null ? await imageAnalyzer(question, image, ct)
            : await model.DescribeImageAsync(question, image, ct, RequireCurrent);
        RequireCurrent();
        int answerLength = Math.Min(answer.Length, 16000);
        if (answerLength < answer.Length && char.IsHighSurrogate(answer[answerLength - 1])) answerLength--;
        return ToolResultJson.FitText(answer[..answerLength], text => ToolResultJson.Element(new { attachmentId = id, image.Width, image.Height,
            text, truncated = text.Length < answer.Length,
            source = "model_image_interpretation", independentlyVerified = false, currentAuthorization = false }));
    }
    internal sealed record AttachmentEntry(string Id, string Name, int ItemType, DateTimeOffset ReceivedAt);
    private async Task<AttachmentEntry[]> ListAttachmentsAsync(ConversationIdentity active, string? query, CancellationToken ct)
    {
        if (!IsCurrentBinding(active)) throw new OperationCanceledException(ct);
        if (attachments?.Enabled != true) return [];
        var saved = await ledger.ReadConversationAsync(active, ct);
        var found = new List<AttachmentEntry>();
        foreach (var message in saved.Inbox.Where(m => m.HasUnresolvedReference && m.Body.StartsWith(AssistantInboundAttachments.Marker, StringComparison.Ordinal)).OrderByDescending(m => m.Revision))
        {
            ct.ThrowIfCancellationRequested();
            string id = message.Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
            try
            {
                var descriptor = attachments.Descriptor(active, id);
                if (AssistantInboundAttachments.OriginalSource(descriptor) != message.SourceMessageId) continue;
                string name = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? (descriptor.ItemType == 2 ? "图片" : "未命名文件") : descriptor.DisplayName;
                if (!string.IsNullOrEmpty(query) && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(new(id, name, descriptor.ItemType, message.ReceivedAt));
            }
            catch (Exception error) when (error is IOException or CryptographicException or ArgumentException or JsonException or WeChatMediaException) { }
        }
        if (!IsCurrentBinding(active)) throw new OperationCanceledException(ct);
        return found.ToArray();
    }
    private async Task<JsonElement> ListAttachmentsForModelAsync(ConversationIdentity active, string? query, CancellationToken ct)
    {
        var all = await ListAttachmentsAsync(active, query, ct);
        return JsonSerializer.SerializeToElement(new { attachments = all.Take(20), total = all.Length,
            truncated = all.Length > 20, scope = "current_retained_inbox", source = "untrusted_attachment_metadata" });
    }
    private async Task<DurableFastReply> AttachmentListReplyAsync(InboxMessage message, CancellationToken ct)
    {
        var all = await ListAttachmentsAsync(message.Identity, null, ct);
        if (all.Length == 0) return new(["当前没有可读取的附件记录。"]);
        string body = "最近的附件：\n" + string.Join("\n", all.Take(10).Select(a => (all.Count(other => other.Id.StartsWith(a.Id[..12], StringComparison.Ordinal)) == 1 ? a.Id[..12] : a.Id) + "  "
            + new string(a.Name.Where(c => !char.IsControl(c)).Take(120).ToArray())))
            + "\n发送“读取附件 短编号”即可查看文字。";
        return new([body]);
    }
    private async Task StopAttachmentReadsAsync()
    {
        attachmentStop.Cancel();
        await Task.WhenAll(attachmentReads.Values);
        attachmentReads.Clear();
    }
}
