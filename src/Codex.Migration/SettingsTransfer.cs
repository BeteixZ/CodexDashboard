using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codex.Migration;

internal static class SettingsTransfer
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static JsonObject Read(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateKeys(document.RootElement);
        var root = JsonNode.Parse(bytes) as JsonObject ?? throw new MigrationFailure("settings_not_object");
        return root;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new MigrationFailure("duplicate_settings_key");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateKeys(child);
    }

    internal static JsonNode? Get(JsonObject root, string name)
        => root.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Put(JsonObject root, string name, JsonNode? value)
    {
        var key = root.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key != null) root.Remove(key);
        root[name] = value;
    }

    internal static byte[] Assistant(byte[] original, bool pauseServices = false)
    {
        var source = Read(original);
        var versionNode = Get(source, "version");
        if (versionNode != null && (!versionNode.AsValue().TryGetValue<int>(out int version) || version is < 0 or > 2))
            throw new MigrationFailure("unsupported_legacy_settings_version");
        var result = new JsonObject { ["version"] = 1 };
        foreach (var name in new[] { "pythonPath", "codexHome", "remoteHosts", "localEnabled", "refreshSeconds", "hostRegistry",
            "maxConcurrentExecutions", "inputMergeWindowMilliseconds", "historyArchiveEnabled", "media" })
            if (Get(source, name) is { } value) result[name] = value.DeepClone();
        foreach (var name in new[] { "weChat", "chatGpt" })
        {
            if (Get(source, name) is { } value && value is not JsonObject)
                throw new MigrationFailure("settings_section_not_object");
            result[name] = Get(source, name)?.DeepClone() ?? new JsonObject();
        }
        if (pauseServices)
        {
            var wechat = (JsonObject)result["weChat"]!;
            var ai = (JsonObject)result["chatGpt"]!;
            Put(wechat, "clawBotEnabled", false); Put(wechat, "pushPlusEnabled", false);
            Put(ai, "enabled", false); Put(ai, "taskControlEnabled", false);
        }
        return JsonSerializer.SerializeToUtf8Bytes(result, Json);
    }

    internal static byte[] Disable(byte[] original)
    {
        var root = Read(original);
        var wechat = Get(root, "weChat") as JsonObject ?? new();
        var ai = Get(root, "chatGpt") as JsonObject ?? new();
        Put(wechat, "clawBotEnabled", false);
        Put(wechat, "pushPlusEnabled", false);
        Put(wechat, "protectedBinding", "");
        Put(wechat, "protectedPushPlusToken", "");
        Put(ai, "enabled", false);
        Put(ai, "taskControlEnabled", false);
        // Detach before putting canonical names to avoid JsonNode parent conflicts.
        Put(root, "weChat", wechat.DeepClone());
        Put(root, "chatGpt", ai.DeepClone());
        return JsonSerializer.SerializeToUtf8Bytes(root, Json);
    }
}
