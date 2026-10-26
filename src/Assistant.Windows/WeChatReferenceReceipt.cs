using System;
using System.Linq;
using System.Text.Json;

namespace CodexDashboard;

internal static class WeChatReferenceReceipt
{
    private const string Prefix = "wechat-message-sha256:";
    internal static string Encode(string? id, string status) => string.IsNullOrEmpty(id) ? status : Prefix + WeChatText.Hash(id) + "\n" + status;
    internal static bool Matches(string? receipt, string id) => receipt?.StartsWith(Prefix + WeChatText.Hash(id) + "\n", StringComparison.Ordinal) == true;
    internal static string? ReadReferenceId(JsonElement message)
    {
        if (!message.TryGetProperty("item_list", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var references = items.EnumerateArray().Where(i => i.TryGetProperty("ref_msg", out var r) && r.ValueKind != JsonValueKind.Null).ToArray();
        if (references.Length != 1 || !references[0].TryGetProperty("ref_msg", out var reference)
            || reference.ValueKind != JsonValueKind.Object || !reference.TryGetProperty("message_item", out var item)
            || item.ValueKind != JsonValueKind.Object) return null;
        string id = WeChatApi.String(item, "msg_id");
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
