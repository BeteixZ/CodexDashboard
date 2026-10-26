using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Codex.Control;

internal enum ProvenanceWriteStage { BeforeWrite, AfterHeader, AfterPayload, AfterCommit, AfterFlush }

/// <summary>
/// Bounded append-only encrypted creation proofs. The writer handle excludes other writers.
/// Acknowledgement follows Flush(true). A torn or damaged record is preserved and blocks use;
/// repair/truncation is deliberately outside this API. Host must provide a protected local path.
/// </summary>
public sealed class FileOwnedProvenanceStorage : IOwnedProvenanceStorage
{
    private static readonly byte[] Magic = "CDXPROV1"u8.ToArray();
    private static readonly byte[] Commit = "COMMIT01"u8.ToArray();
    private const int HeaderLength = 52, MaximumPayload = 128 * 1024, MaximumRecords = 1024;
    private readonly FileStream stream;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<ProvenanceWriteStage>? fault;
    private long sequence;
    private bool scanned, disposed;
    public bool IsReadOnly { get; }
    public bool RequiresRecovery { get; private set; }

    public FileOwnedProvenanceStorage(string path, bool readOnly = false) : this(path, readOnly, null) { }
    internal FileOwnedProvenanceStorage(string path, bool readOnly, Action<ProvenanceWriteStage>? fault)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute store path is required.", nameof(path));
        IsReadOnly = readOnly; this.fault = fault;
        // Parent creation/access controls and key custody belong to the trusted host.
        bool created = false;
        if (readOnly)
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        else
        {
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                created = true;
            }
            catch (IOException) when (File.Exists(path))
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
        }
        // Existing empty data might be a complete truncation. Only an atomically created file may
        // start empty; reopening an empty file requires explicit host recovery, never a fresh identity.
        if (!created && stream.Length == 0) RequiresRecovery = true;
    }

    public async ValueTask<IReadOnlyList<ProtectedOwnedProvenance>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<ProtectedOwnedProvenance>> ReadCoreAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RequiresRecovery) throw new ProvenanceException("provenance_file_recovery_required");
        var records = new List<ProtectedOwnedProvenance>();
        try
        {
            long length = stream.Length;
            if (length > (long)MaximumRecords * (MaximumPayload + HeaderLength + Commit.Length))
                throw new ProvenanceException("provenance_file_too_large");
            stream.Position = 0;
            while (stream.Position < length)
            {
                ct.ThrowIfCancellationRequested();
                byte[] header = new byte[HeaderLength];
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                long recordSequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8));
                int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16, 4));
                if (!header.AsSpan(0, 8).SequenceEqual(Magic) || recordSequence != records.Count + 1L
                    || size is <= 0 or > MaximumPayload || records.Count >= MaximumRecords
                    || stream.Position + size + Commit.Length > length)
                    throw new ProvenanceException("provenance_file_frame_invalid");
                byte[] payload = new byte[size], commit = new byte[Commit.Length];
                await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                await stream.ReadExactlyAsync(commit, ct).ConfigureAwait(false);
                if (!commit.AsSpan().SequenceEqual(Commit)
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header.AsSpan(20, 32)))
                    throw new ProvenanceException("provenance_file_commit_invalid");
                records.Add(new(recordSequence, payload));
            }
            if (stream.Length != length) throw new ProvenanceException("provenance_file_changed_during_read");
            sequence = records.Count; scanned = true;
            return records;
        }
        catch (OperationCanceledException) { throw; }
        catch { RequiresRecovery = true; throw new ProvenanceException("provenance_file_recovery_required"); }
    }

    public async ValueTask AppendAsync(long expectedSequence, ReadOnlyMemory<byte> protectedRecord,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsReadOnly || RequiresRecovery) throw new ProvenanceException("provenance_file_not_writable");
            if (!scanned) await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (expectedSequence != sequence || sequence >= MaximumRecords || protectedRecord.Length is <= 0 or > MaximumPayload)
                throw new ProvenanceException("provenance_file_sequence_or_size_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] header = new byte[HeaderLength];
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), sequence + 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), protectedRecord.Length);
            SHA256.HashData(protectedRecord.Span).CopyTo(header, 20);
            try
            {
                fault?.Invoke(ProvenanceWriteStage.BeforeWrite);
                stream.Position = stream.Length;
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                fault?.Invoke(ProvenanceWriteStage.AfterHeader);
                await stream.WriteAsync(protectedRecord, cancellationToken).ConfigureAwait(false);
                fault?.Invoke(ProvenanceWriteStage.AfterPayload);
                await stream.WriteAsync(Commit, cancellationToken).ConfigureAwait(false);
                fault?.Invoke(ProvenanceWriteStage.AfterCommit);
                // No acknowledgement or owned handle before the OS confirms this flush.
                stream.Flush(flushToDisk: true);
                fault?.Invoke(ProvenanceWriteStage.AfterFlush);
                sequence++;
            }
            catch { RequiresRecovery = true; throw new ProvenanceException("provenance_file_commit_unconfirmed"); }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await stream.DisposeAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
