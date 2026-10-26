using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantAttachment(int Version, ConversationIdentity Identity, string SourceMessageId,
    int ItemIndex, string DisplayName, int ItemType, TaskReference? Task, string ContentHash, byte[] Content);

// Small inbound attachments only; caller commits the returned ID to the inbox afterwards.
// Same message/item is idempotent. Metadata and content are protected as one envelope.
internal sealed class AssistantAttachmentStore
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string root;
    private readonly IStateProtector protector;
    internal AssistantAttachmentStore(string dataDirectory, IStateProtector? protector = null)
    {
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Absolute attachment data directory required.");
        root = Path.Combine(Path.GetFullPath(dataDirectory), "attachments");
        this.protector = protector ?? new WindowsStateProtector();
    }
    private static string IdentityKey(ConversationIdentity identity) => WeChatText.Hash(JsonSerializer.Serialize(identity));
    private static string ItemId(string sourceId, int index) => WeChatText.Hash(JsonSerializer.Serialize(new { sourceId, index }));
    internal AssistantAttachment? Find(ConversationIdentity identity, string sourceId, int index)
    {
        try { return Load(identity, ItemId(sourceId, index)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private byte[] Purpose(ConversationIdentity identity, string id) => Encoding.UTF8.GetBytes("assistant-attachment/v1/" + protector.Scheme + "/" + IdentityKey(identity) + "/" + id);
    private string Location(ConversationIdentity identity, string id)
    {
        if (id.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(id, "\\A[0-9A-Fa-f]{64}\\z")) throw new ArgumentException("Invalid attachment ID.");
        string path = Path.Combine(root, IdentityKey(identity), id + ".state");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Redirected attachment directory.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Redirected attachment file.");
        return path;
    }
    internal string Save(ConversationIdentity identity, string sourceId, int index, string displayName, int itemType,
        byte[] content, TaskReference? task = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 512 || index < 0 || displayName.Length > 512
            || itemType is < 2 or > 5 || content.Length > MaximumBytes) throw new ArgumentException("Invalid attachment metadata or size.");
        string id = ItemId(sourceId, index), path = Location(identity, id);
        var record = new AssistantAttachment(1, identity, sourceId, index, displayName, itemType, task,
            Convert.ToHexString(SHA256.HashData(content)), content);
        if (File.Exists(path)) { RequireSame(Load(identity, id), record); return id; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record);
        byte[] cipher;
        try { cipher = protector.Protect(plain, Purpose(identity, id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(cipher); file.Flush(true); }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { RequireSame(Load(identity, id), record); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return id;
    }
    private static void RequireSame(AssistantAttachment saved, AssistantAttachment incoming)
    {
        if (saved with { Content = incoming.Content } != incoming) throw new IOException("Attachment source conflict.");
    }
    internal AssistantAttachment Load(ConversationIdentity identity, string id)
    {
        string path = Location(identity, id);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 16 * 1024 * 1024) throw new IOException("Attachment envelope too large.");
        byte[] cipher = new byte[checked((int)file.Length)]; file.ReadExactly(cipher);
        byte[] plain = protector.Unprotect(cipher, Purpose(identity, id));
        try
        {
            var record = JsonSerializer.Deserialize<AssistantAttachment>(plain) ?? throw new IOException("Invalid attachment envelope.");
            if (record.Version != 1 || record.Identity != identity || record.Content == null || record.Content.Length > MaximumBytes
                || ItemId(record.SourceMessageId, record.ItemIndex) != id || Convert.ToHexString(SHA256.HashData(record.Content)) != record.ContentHash)
                throw new IOException("Invalid attachment provenance.");
            return record;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
