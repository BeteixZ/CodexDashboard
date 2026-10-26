using System.Security.Cryptography;
using System.Text.Json;

namespace Assistant.Core;

internal static class LedgerArchiveCodec
{
    private sealed record Manifest(int SchemaVersion, string ArchiveEncoding, int Bytes, string Sha256, ArchivedPayload[] Chunks);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    internal static bool IsArchived(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.TryGetProperty("schemaVersion", out var version) && version.GetInt32() == 12;
    }
    // End chunks at top-level collection elements. Appending an inbox/event record then
    // reuses unchanged prior records instead of shifting every later fixed-size chunk.
    private static List<(int Offset, int Bytes)> Chunks(byte[] bytes)
    {
        var result = new List<(int, int)>(); int start = 0;
        void Add(int end)
        {
            while (start < end)
            { int count = Math.Min(1024 * 1024, end - start); result.Add((start, count)); start += count; }
        }
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
            if (reader.CurrentDepth == 2 && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray
                or JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
                Add(checked((int)reader.BytesConsumed));
        Add(bytes.Length); return result;
    }
    internal static async Task<byte[]> EncodeAsync(byte[] bytes, HistoryArchiveSet archive, CancellationToken ct)
    {
        var references = new List<ArchivedPayload>();
        foreach (var (offset, count) in Chunks(bytes))
            references.Add(await archive.PutAsync(bytes.AsMemory(offset, count), ct).ConfigureAwait(false));
        return JsonSerializer.SerializeToUtf8Bytes(new Manifest(12, "immutable-history/v1", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), references.ToArray()), Json);
    }
    internal static async Task<byte[]> DecodeAsync(byte[] bytes, HistoryArchiveSet archive, int maximumBytes, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json) ?? throw Corrupt();
        if (manifest.SchemaVersion != 12 || manifest.ArchiveEncoding != "immutable-history/v1"
            || manifest.Bytes is < 1 || manifest.Bytes > maximumBytes || manifest.Chunks == null || manifest.Chunks.Length == 0
            || manifest.Chunks.Sum(chunk => (long)chunk.Bytes) != manifest.Bytes) throw Corrupt();
        var result = new byte[manifest.Bytes]; int offset = 0;
        try
        {
            foreach (var reference in manifest.Chunks)
            {
                var chunk = await archive.ReadAsync(reference, ct).ConfigureAwait(false);
                try { chunk.CopyTo(result, offset); offset += chunk.Length; }
                finally { CryptographicOperations.ZeroMemory(chunk); }
            }
            if (Convert.ToHexString(SHA256.HashData(result)) != manifest.Sha256) throw Corrupt();
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }
    private static LedgerException Corrupt() => new(LedgerErrors.CorruptStorage, "The archived ledger manifest does not match its original complete snapshot.");
}
