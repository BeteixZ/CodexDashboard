using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Assistant.Core;

public sealed record JournalSnapshot(long Sequence, byte[] Payload);
public sealed record JournalRecovery(bool Required, string? Reason, long LastCommittedOffset);

// Only built-in storage preflight may assert that this append never began.
internal sealed class JournalCapacityRejection(string message)
    : LedgerException(LedgerErrors.CapacityExceeded, message);

/// <summary>
/// Append returns only after the complete record has been committed to durable storage. After an
/// uncertain append failure, this instance must refuse all further writes until reopened.
/// </summary>
public interface IJournalStorage : IAsyncDisposable
{
    JournalRecovery Recovery { get; }
    ValueTask<JournalSnapshot?> ReadLatestAsync(CancellationToken cancellationToken = default);
    ValueTask AppendAsync(long expectedSequence, ReadOnlyMemory<byte> encryptedSnapshot, CancellationToken cancellationToken = default);
}

public enum JournalWriteStage { BeforeWrite, AfterHeader, AfterPayload, BeforeFlush, AfterFlush }
public interface IJournalFaultInjector { void At(JournalWriteStage stage, long sequence); }

/// <summary>
/// An append-only journal of encrypted transaction snapshots. The exclusive writer handle prevents
/// cross-process writers. Frames are bounded and checksummed; Flush(true), not rename, commits.
/// Incomplete or damaged data is preserved and leaves the journal read-only for explicit recovery.
/// </summary>
public sealed class FileJournalStorage : IJournalStorage
{
    private static ReadOnlySpan<byte> Magic => "ASLEDG01"u8;
    private static ReadOnlySpan<byte> BankAnchorMagic => "ASBANK02"u8;
    private static ReadOnlySpan<byte> CommitMarker => "COMMIT01"u8;
    private const int HeaderLength = 44;
    private readonly FileStream _file;
    private readonly int _maxFrameBytes;
    private readonly long _maxJournalBytes;
    private readonly IJournalFaultInjector? _faults;
    private readonly IJournalCompactionFaultInjector? _compactionFaults;
    private readonly long _maxSwitchBytes;
    private BankJournalStorage? _banks;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private JournalSnapshot? _latest;
    private bool _faulted;
    private bool _disposed;
    private readonly bool _readOnly;
    public JournalRecovery Recovery { get; private set; } = new(false, null, 8);
    public string Path { get; }

    public FileJournalStorage(string path, int maximumFrameBytes = 16 * 1024 * 1024,
        long maximumJournalBytes = 512L * 1024 * 1024, IJournalFaultInjector? faultInjector = null,
        IJournalCompactionFaultInjector? compactionFaultInjector = null, long maximumSwitchJournalBytes = 1024 * 1024,
        bool readOnly = false)
    {
        if (maximumFrameBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        if (maximumJournalBytes < maximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumJournalBytes));
        if (maximumSwitchJournalBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumSwitchJournalBytes));
        Path = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        _maxFrameBytes = maximumFrameBytes;
        _maxJournalBytes = maximumJournalBytes;
        _faults = faultInjector;
        _compactionFaults = compactionFaultInjector;
        _maxSwitchBytes = maximumSwitchJournalBytes;
        _readOnly = readOnly;
        var existed = File.Exists(Path);
        if (!existed && BankJournalStorage.AnyArtifacts(Path))
            throw new LedgerException(LedgerErrors.CorruptStorage, "The bank journal anchor is missing; existing data was preserved.");
        _file = new(Path, readOnly ? FileMode.Open : FileMode.OpenOrCreate,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read,
            4096, readOnly ? FileOptions.Asynchronous : FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            if (_file.Length == 0 && !existed)
            {
                _file.Write(Magic);
                _file.Flush(flushToDisk: true);
            }
            Scan();
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    private void Scan()
    {
        _file.Position = 0;
        Span<byte> magic = stackalloc byte[8];
        var magicLength = _file.Read(magic);
        if (magicLength == 8 && magic.SequenceEqual(BankAnchorMagic))
        {
            OpenBanks();
            return;
        }
        if (magicLength != 8 || !magic.SequenceEqual(Magic))
        {
            Recovery = new(true, "Journal header is damaged; the original file was preserved.", 0);
            return;
        }
        var lastOffset = 8L;
        var expectedSequence = 1L;
        while (_file.Position < _file.Length)
        {
            var header = new byte[HeaderLength];
            if (!TryReadExactly(header))
            {
                Recovery = new(true, "An incomplete journal tail was preserved.", lastOffset);
                return;
            }
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (sequence != expectedSequence || length is <= 0 || length > _maxFrameBytes)
            {
                Recovery = new(true, "A journal frame header is invalid; the original file was preserved.", lastOffset);
                return;
            }
            if (_file.Length - _file.Position < length + 8L)
            {
                Recovery = new(true, "An incomplete journal tail was preserved.", lastOffset);
                return;
            }
            var payload = new byte[length];
            _file.ReadExactly(payload);
            var trailer = new byte[8];
            _file.ReadExactly(trailer);
            var hash = HashFrame(sequence, payload);
            if (!trailer.AsSpan().SequenceEqual(CommitMarker) || !CryptographicOperations.FixedTimeEquals(hash, header.AsSpan(12, 32)))
            {
                Recovery = new(true, "A journal checksum or commit marker is invalid; the original file was preserved.", lastOffset);
                return;
            }
            _latest = new(sequence, payload);
            lastOffset = _file.Position;
            expectedSequence++;
        }
        Recovery = new(false, null, lastOffset);
        if (BankJournalStorage.AnyArtifacts(Path))
            Recovery = new(true, "Unpublished or uncertain bank migration artifacts exist; v1 cannot be reopened for writing.", lastOffset);
    }

    private void OpenBanks()
    {
        var anchor = new byte[56];
        _file.Position = 0;
        if (_file.Length < anchor.Length || !TryReadExactly(anchor)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(anchor.AsSpan(0, 24)), anchor.AsSpan(24)))
        {
            Recovery = new(true, "The bank journal version anchor is incomplete or damaged.", 0);
            return;
        }
        var storeId = new Guid(anchor.AsSpan(8, 16));
        try
        {
            _banks = BankJournalStorage.Open(Path, _maxFrameBytes, _maxJournalBytes, _maxSwitchBytes, _faults, _compactionFaults, _readOnly);
            if (storeId == Guid.Empty || _banks.StoreId != storeId)
            {
                Recovery = new(true, "The bank journal identity does not match its anchor.", 0);
                return;
            }
            _latest = _banks.Latest;
            Recovery = _banks.Recovery;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LedgerException)
        {
            Recovery = new(true, "A required bank journal component is missing or unavailable; no older snapshot was selected.", 0);
        }
    }

    public ValueTask<JournalSnapshot?> ReadLatestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_latest is null ? null : new JournalSnapshot(_latest.Sequence, (byte[])_latest.Payload.Clone()));
    }

    public async ValueTask AppendAsync(long expectedSequence, ReadOnlyMemory<byte> encryptedSnapshot,
        CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AppendCoreAsync(expectedSequence, encryptedSnapshot, cancellationToken).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }

    private async ValueTask AppendCoreAsync(long expectedSequence, ReadOnlyMemory<byte> encryptedSnapshot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readOnly || _faulted || Recovery.Required)
            throw new LedgerException(LedgerErrors.RecoveryRequired, "Reopen or explicitly recover the journal before writing.");
        if (expectedSequence != (_latest?.Sequence ?? 0))
            throw new LedgerException(LedgerErrors.Conflict, "Journal sequence changed.");
        if (encryptedSnapshot.Length is <= 0 || encryptedSnapshot.Length > _maxFrameBytes)
            throw new JournalCapacityRejection("Journal capacity would be exceeded; no data was removed.");
        cancellationToken.ThrowIfCancellationRequested();
        if (_banks is not null)
        {
            await AppendBankAsync(expectedSequence, encryptedSnapshot, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_latest is not null && BankJournalStorage.ShouldCompact(_file.Length,
            BankJournalStorage.FrameSize(encryptedSnapshot.Length), _latest.Payload.Length, _maxJournalBytes))
        {
            if (!BankJournalStorage.CheckpointAndNextFit(_latest.Payload.Length, encryptedSnapshot.Length, _maxJournalBytes))
                throw new JournalCapacityRejection("The unchanged checkpoint and next frame cannot fit the bounded journal.");
            await MigrateToBanksAsync(cancellationToken).ConfigureAwait(false);
            await AppendBankAsync(expectedSequence, encryptedSnapshot, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_file.Length + HeaderLength + encryptedSnapshot.Length + 8 > _maxJournalBytes)
            throw new JournalCapacityRejection("Journal capacity would be exceeded; no data was removed.");
        var sequence = checked(expectedSequence + 1);
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt64LittleEndian(header, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), encryptedSnapshot.Length);
        HashFrame(sequence, encryptedSnapshot.Span).CopyTo(header.AsSpan(12));
        try
        {
            _faults?.At(JournalWriteStage.BeforeWrite, sequence);
            _file.Position = _file.Length;
            await _file.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            _faults?.At(JournalWriteStage.AfterHeader, sequence);
            await _file.WriteAsync(encryptedSnapshot, cancellationToken).ConfigureAwait(false);
            _faults?.At(JournalWriteStage.AfterPayload, sequence);
            await _file.WriteAsync(CommitMarker.ToArray(), cancellationToken).ConfigureAwait(false);
            _faults?.At(JournalWriteStage.BeforeFlush, sequence);
            _file.Flush(flushToDisk: true);
            _faults?.At(JournalWriteStage.AfterFlush, sequence);
            _latest = new(sequence, encryptedSnapshot.ToArray());
            Recovery = new(false, null, _file.Position);
        }
        catch
        {
            _faulted = true;
            Recovery = new(true, "The last append outcome is uncertain; reopen before performing external actions.", Recovery.LastCommittedOffset);
            throw;
        }
    }

    private async Task MigrateToBanksAsync(CancellationToken ct)
    {
        try
        {
            if (BankJournalStorage.AnyArtifacts(Path))
                throw new LedgerException(LedgerErrors.RecoveryRequired, "Uncertain migration artifacts cannot be overwritten.");
            _banks = await BankJournalStorage.CreateAsync(Path, _latest!, _maxFrameBytes, _maxJournalBytes,
                _maxSwitchBytes, _faults, _compactionFaults, ct).ConfigureAwait(false);
            var anchor = new byte[56];
            BankAnchorMagic.CopyTo(anchor); _banks.StoreId.TryWriteBytes(anchor.AsSpan(8, 16));
            SHA256.HashData(anchor.AsSpan(0, 24)).CopyTo(anchor.AsSpan(24));
            _file.Position = 0;
            await _file.WriteAsync(anchor, ct).ConfigureAwait(false);
            _compactionFaults?.At(JournalCompactionStage.AnchorBeforeFlush, _latest!.Sequence);
            _file.Flush(true);
            _compactionFaults?.At(JournalCompactionStage.AnchorAfterFlush, _latest!.Sequence);
            // The published anchor and selected checkpoint have both been flushed. Only redundant
            // physical v1 snapshots are reclaimed; the unchanged encrypted logical snapshot remains.
            _file.SetLength(anchor.Length);
            _file.Flush(true);
            Recovery = _banks.Recovery;
        }
        catch
        {
            _faulted = true;
            Recovery = new(true, "Bank migration outcome is uncertain; reopen before any external action.", Recovery.LastCommittedOffset);
            throw;
        }
    }

    private async Task AppendBankAsync(long expectedSequence, ReadOnlyMemory<byte> snapshot, CancellationToken ct)
    {
        try
        {
            if (_file.Length > 56)
            {
                // Resume only the redundant-tail cleanup of an already validated, published anchor.
                // No cleanup runs while recovery is required.
                _file.SetLength(56);
                _file.Flush(true);
            }
            await _banks!.AppendAsync(expectedSequence, snapshot, ct).ConfigureAwait(false);
            _latest = _banks.Latest;
            Recovery = _banks.Recovery;
        }
        catch (JournalCapacityRejection) when (!_banks!.Recovery.Required) { throw; }
        catch
        {
            _faulted = true;
            Recovery = new(true, _banks!.Recovery.Reason ?? "A bank append failed; reopen before any external action.",
                _banks.Recovery.LastCommittedOffset);
            throw;
        }
    }

    private bool TryReadExactly(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _file.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    private static byte[] HashFrame(long sequence, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(prefix, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[8..], payload.Length);
        hash.AppendData(prefix);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_banks is not null) await _banks.DisposeAsync().ConfigureAwait(false);
            await _file.DisposeAsync().ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
}
