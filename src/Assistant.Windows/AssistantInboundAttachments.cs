using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed class AssistantInboundAttachments(string directory, Func<WeChatMediaClient>? clientFactory = null,
    bool enableCapture = false)
{
    internal bool Enabled => enableCapture;
    internal const string Marker = "[微信附件资料] ";
    private const string DescriptorPrefix = "media-descriptor/";
    private readonly AssistantAttachmentStore store = new(directory);
    internal string? Capture(ConversationIdentity identity, string sourceId, JsonElement items)
    {
        if (!enableCapture || items.ValueKind != JsonValueKind.Array) return null;
        var indexed = items.EnumerateArray().Select((item, index) => (item, index)).ToArray();
        var media = indexed.Where(x => WeChatApi.Number(x.item, "type") != 1).ToArray();
        if (media.Length != 1 || WeChatApi.Number(media[0].item, "type") is not (2 or 4)) return null;
        var selected = media[0];
        var descriptor = WeChatMediaDescriptor.Parse(selected.item);
        // Validate key shape before durable acceptance, without persisting a decoded key.
        var key = descriptor.ReadKey();
        if (key != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        string field = descriptor.ItemType == 4 ? "file_item" : "image_item";
        string name = WeChatApi.String(selected.item.GetProperty(field), "file_name");
        return store.Save(identity, DescriptorPrefix + sourceId, selected.index, name, descriptor.ItemType,
            Encoding.UTF8.GetBytes(selected.item.GetRawText()));
    }
    internal AssistantAttachment Descriptor(ConversationIdentity identity, string id)
    {
        var descriptor = store.Load(identity, id);
        if (!descriptor.SourceMessageId.StartsWith(DescriptorPrefix, StringComparison.Ordinal))
            throw new WeChatMediaException("not_an_attachment_descriptor");
        return descriptor;
    }
    internal static string OriginalSource(AssistantAttachment descriptor) => descriptor.SourceMessageId[DescriptorPrefix.Length..];
    internal async Task<AttachmentContent> ReadAsync(ConversationIdentity identity, string id, CancellationToken ct, int offset = 0, int maximumCharacters = 16000)
    {
        byte[] bytes = await ReadBytesAsync(identity, id, ct);
        var result = await Task.Run(() => AssistantAttachmentContent.Read(bytes, maximumCharacters, offset, ct), ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }
    internal async Task<byte[]> ReadBytesAsync(ConversationIdentity identity, string id, CancellationToken ct)
    {
        var descriptor = Descriptor(identity, id);
        string source = OriginalSource(descriptor);
        var cached = store.Find(identity, source, descriptor.ItemIndex);
        if (cached == null)
        {
            using var json = JsonDocument.Parse(descriptor.Content);
            using var client = clientFactory?.Invoke() ?? new WeChatMediaClient();
            byte[] bytes = await client.DownloadAsync(WeChatMediaDescriptor.Parse(json.RootElement), AssistantAttachmentStore.MaximumBytes, ct);
            ct.ThrowIfCancellationRequested();
            string saved = store.Save(identity, source, descriptor.ItemIndex, descriptor.DisplayName, descriptor.ItemType, bytes);
            cached = store.Load(identity, saved);
        }
        return cached.Content;
    }
}
