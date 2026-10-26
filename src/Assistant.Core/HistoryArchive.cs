using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core;

public sealed record ArchivedPayload(Guid ArchiveId, string Sha256, int Bytes);

/// <summary>
/// Immutable encrypted payloads for future ledger manifests. A returned reference is durable;
/// an uncertain append requires reopen. This store never removes history or repairs a damaged tail.
/// </summary>
public sealed class HistoryArchive : IAsyncDisposable
{
    private const int HeaderBytes = 24, FrameBytes = 72, MaximumPayload = 1024 * 1024, MaximumCipher = 2 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "ASHIST01"u8;
    private static ReadOnlySpan<byte> Commit => "HISTDONE"u8;
    private readonly FileStream file;
    private readonly IStateProtector protector;
    private readonly long maximumBytes;
    private readonly IJournalFaultInjector? faults;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private sealed record Entry(long Offset, int Bytes, int CipherBytes);
    private bool disposed;
    public Guid Id { get; }
    internal IReadOnlyList<ArchivedPayload> References => entries.Select(item => new ArchivedPayload(Id, item.Key, item.Value.Bytes)).ToArray();
    public JournalRecovery Recovery { get; private set; } = new(false, null, HeaderBytes);

    public HistoryArchive(string path, IStateProtector protector, long maximumBytes = 64L * 1024 * 1024,
        IJournalFaultInjector? faultInjector = null, bool createNew = false, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (readOnly && createNew) throw new ArgumentException("Read-only archives cannot be created.");
        if (string.IsNullOrWhiteSpace(protector.Scheme) || protector.Scheme.Length > 128 || maximumBytes < HeaderBytes)
            throw new ArgumentException("Invalid archive protection or size.");
        this.protector = protector; this.maximumBytes = maximumBytes; faults = faultInjector;
        file = new FileStream(path, createNew ? FileMode.CreateNew : FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (createNew)
            {
                Id = Guid.NewGuid(); var header = new byte[HeaderBytes]; Magic.CopyTo(header); Id.TryWriteBytes(header.AsSpan(8));
                file.Write(header); file.Flush(true);
            }
            else
            {
                Span<byte> header = stackalloc byte[HeaderBytes];
                file.ReadExactly(header);
                if (!header[..8].SequenceEqual(Magic)) throw Corrupt();
                Id = new Guid(header[8..]);
                if (Id == Guid.Empty) throw Corrupt();
            }
            Scan();
        }
        catch { file.Dispose(); throw; }
    }

    private void Scan()
    {
        long offset = HeaderBytes;
        while (offset < file.Length)
        {
            file.Position = offset;
            if (file.Length - offset < FrameBytes) { Damaged(offset); return; }
            var header = new byte[FrameBytes]; file.ReadExactly(header);
            int plain = BinaryPrimitives.ReadInt32LittleEndian(header), cipher = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (plain is < 1 or > MaximumPayload || cipher is < 1 or > MaximumCipher || file.Length - file.Position < cipher + Commit.Length)
            { Damaged(offset); return; }
            var encrypted = new byte[cipher]; file.ReadExactly(encrypted);
            var marker = new byte[Commit.Length]; file.ReadExactly(marker);
            string hash = Convert.ToHexString(header.AsSpan(8, 32));
            if (!marker.AsSpan().SequenceEqual(Commit) || !SHA256.HashData(encrypted).AsSpan().SequenceEqual(header.AsSpan(40, 32))
                || !entries.TryAdd(hash, new(offset, plain, cipher))) { Damaged(offset); return; }
            offset = file.Position;
        }
        Recovery = new(false, null, offset);
    }
    private void Damaged(long offset) => Recovery = new(true, "The archive tail is incomplete or damaged; original bytes were preserved.", offset);
    private byte[] Purpose(string hash) => Encoding.UTF8.GetBytes($"assistant-history/v1/{protector.Scheme}/{Id:N}/{hash}");
    private static LedgerException Corrupt() => new(LedgerErrors.CorruptStorage, "The history archive is invalid; original bytes were preserved.");

    public async Task<ArchivedPayload> PutAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        if (plaintext.Length is < 1 or > MaximumPayload) throw new ArgumentOutOfRangeException(nameof(plaintext));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!file.CanWrite || Recovery.Required) throw new LedgerException(LedgerErrors.RecoveryRequired, Recovery.Reason ?? "Archive is open for inspection.");
            string hash = Convert.ToHexString(SHA256.HashData(plaintext.Span));
            var reference = new ArchivedPayload(Id, hash, plaintext.Length);
            if (entries.ContainsKey(hash))
            {
                var existing = await ReadCoreAsync(reference, ct).ConfigureAwait(false);
                try { if (!existing.AsSpan().SequenceEqual(plaintext.Span)) throw Corrupt(); }
                finally { CryptographicOperations.ZeroMemory(existing); }
                return reference;
            }
            byte[] encrypted = protector.Protect(plaintext.Span, Purpose(hash));
            try
            {
                if (encrypted.Length is < 1 or > MaximumCipher || file.Length + FrameBytes + encrypted.Length + Commit.Length > maximumBytes)
                    throw new JournalCapacityRejection("The history volume is full; no history was removed.");
                ct.ThrowIfCancellationRequested();
                var header = new byte[FrameBytes];
                BinaryPrimitives.WriteInt32LittleEndian(header, plaintext.Length); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), encrypted.Length);
                Convert.FromHexString(hash).CopyTo(header, 8); SHA256.HashData(encrypted).CopyTo(header, 40);
                long offset = file.Length, sequence = entries.Count + 1;
                try
                {
                    faults?.At(JournalWriteStage.BeforeWrite, sequence);
                    file.Position = offset; await file.WriteAsync(header, ct).ConfigureAwait(false);
                    faults?.At(JournalWriteStage.AfterHeader, sequence);
                    await file.WriteAsync(encrypted, ct).ConfigureAwait(false);
                    faults?.At(JournalWriteStage.AfterPayload, sequence);
                    await file.WriteAsync(Commit.ToArray(), ct).ConfigureAwait(false);
                    faults?.At(JournalWriteStage.BeforeFlush, sequence); file.Flush(true);
                    faults?.At(JournalWriteStage.AfterFlush, sequence);
                    entries.Add(hash, new(offset, plaintext.Length, encrypted.Length));
                    Recovery = new(false, null, file.Position);
                    return reference;
                }
                catch { Recovery = new(true, "The archive append outcome is uncertain; reopen before publishing a reference.", offset); throw; }
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> ReadAsync(ArchivedPayload reference, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await ReadCoreAsync(reference, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<byte[]> ReadCoreAsync(ArchivedPayload reference, CancellationToken ct)
    {
        if (reference.ArchiveId != Id || !entries.TryGetValue(reference.Sha256, out var entry) || reference.Bytes != entry.Bytes)
            throw Corrupt();
        file.Position = entry.Offset + FrameBytes;
        var encrypted = new byte[entry.CipherBytes]; await file.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
        byte[] plaintext = protector.Unprotect(encrypted, Purpose(reference.Sha256));
        if (plaintext.Length == reference.Bytes && Convert.ToHexString(SHA256.HashData(plaintext)) == reference.Sha256) return plaintext;
        CryptographicOperations.ZeroMemory(plaintext); throw Corrupt();
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await file.DisposeAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
