using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexDashboard;

// Explicit, process-local opt-in. No values, hashes, identifiers, URLs or tokens are exported.
internal sealed class WeChatFieldProbe
{
    private sealed record Sent(string Bot, string User, int Ordinal, Dictionary<string, string> Ids);
    private static readonly object Gate = new();
    private static readonly Queue<Sent> Sends = new();
    private static int sendCount, evidenceCount;
    private static string? ProbeDirectory => Environment.GetEnvironmentVariable("CODEX_ASSISTANT_FIELD_PROBE");
    internal static Dictionary<string, string> CandidateIds(JsonElement value, string path = "response", int depth = 0)
    {
        var ids = new Dictionary<string, string>();
        if (depth > 8 || value.ValueKind != JsonValueKind.Object) return ids;
        foreach (var p in value.EnumerateObject().Take(80))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(p.Name, "\\A[a-z][a-z0-9_]{0,63}\\z")) continue;
            string key = path + "." + p.Name;
            if (p.Name is "msg_id" or "message_id" or "client_id" or "id")
            {
                string id = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetRawText() : "";
                if (id.Length > 0) ids[key] = id;
            }
            foreach (var pair in CandidateIds(p.Value, key, depth + 1)) ids[pair.Key] = pair.Value;
        }
        return ids;
    }
    internal static void RecordSend(ClawBinding account, string clientId, JsonElement response)
    {
        if (string.IsNullOrWhiteSpace(ProbeDirectory)) return;
        lock (Gate)
        {
            var ids = CandidateIds(response);
            ids["request.client_id"] = clientId;
            var sent = new Sent(account.BotId, account.UserId, ++sendCount, ids);
            Sends.Enqueue(sent);
            while (Sends.Count > 32) Sends.Dequeue();
            WriteEvidence(new { kind = "send", ordinal = sent.Ordinal, candidates = ids.Select(x => new { path = x.Key, length = x.Value.Length }), responseShape = Shape(response, referenceFields: true) });
        }
    }
    internal static void CompareReference(ClawBinding account, JsonElement message)
    {
        if (string.IsNullOrWhiteSpace(ProbeDirectory) || !message.TryGetProperty("item_list", out var items) || items.ValueKind != JsonValueKind.Array) return;
        lock (Gate)
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("ref_msg", out var reference) || reference.ValueKind != JsonValueKind.Object) continue;
            var ids = CandidateIds(reference, "reference");
            var sameAccount = Sends.Where(s => s.Bot == account.BotId && s.User == account.UserId).ToArray();
            var matches = ids.SelectMany(id => sameAccount.SelectMany(s => s.Ids.Where(p => p.Value == id.Value).Select(p => new { referencePath = id.Key, sendOrdinal = s.Ordinal, sendPath = p.Key }))).ToArray();
            WriteEvidence(new { kind = "reference-comparison", observedSends = sameAccount.Length, candidates = ids.Select(x => new { path = x.Key, length = x.Value.Length }), matches });
        }
    }
    private static void WriteEvidence(object evidence)
    {
        string? folder = ProbeDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || evidenceCount >= 48) return;
        try
        {
            Directory.CreateDirectory(folder);
            using var file = new FileStream(Path.Combine(folder, $"id-match-{Environment.ProcessId}-{++evidenceCount:D2}.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, evidence, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private readonly string? directory = Environment.GetEnvironmentVariable("CODEX_ASSISTANT_FIELD_PROBE");
    private int count;
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    { "message_id", "seq", "message_type", "message_state", "item_list", "type", "text_item", "text", "ref_msg",
      "voice_item", "image_item", "file_item", "video_item", "title", "message_item", "from_user_id", "to_user_id",
      "create_time_ms", "context_token", "group_id", "media", "encrypt_query_param", "aes_key", "encrypt_type",
      "playtime", "encode_type", "bits_per_sample", "sample_rate", "file_name", "len", "md5", "url" };
    internal static object Shape(JsonElement value, int depth = 0, bool referenceFields = false)
    {
        if (depth > 8) return new { kind = "depth_limit" };
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new Dictionary<string, object>(); int unknown = 0;
            foreach (var property in value.EnumerateObject().Take(80))
                if (Names.Contains(property.Name) || referenceFields && System.Text.RegularExpressions.Regex.IsMatch(property.Name, "\\A[a-z][a-z0-9_]{0,63}\\z"))
                    fields[property.Name] = property.Name == "type" && property.Value.TryGetInt32Safe(out int type)
                    ? new { kind = "Number", itemType = type } : Shape(property.Value, depth + 1, property.Name is "ref_msg" or "message_item");
                else unknown++;
            return new { kind = "Object", fields, unknownFieldCount = unknown };
        }
        if (value.ValueKind == JsonValueKind.Array) return new { kind = "Array", count = value.GetArrayLength(), items = value.EnumerateArray().Take(20).Select(x => Shape(x, depth + 1)).ToArray() };
        if (value.ValueKind == JsonValueKind.String) return new { kind = "String", length = value.GetString()!.Length };
        return new { kind = value.ValueKind.ToString() };
    }
    internal void Capture(JsonElement message)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || count >= 12) return;
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"fields-{Environment.ProcessId}-{++count:D2}.json");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new { version = 1, observedAtUtc = DateTimeOffset.UtcNow, shape = Shape(message) }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Optional diagnostics cannot stop reception. */ }
    }
}

internal static class FieldProbeJson
{
    internal static bool TryGetInt32Safe(this JsonElement value, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
}
