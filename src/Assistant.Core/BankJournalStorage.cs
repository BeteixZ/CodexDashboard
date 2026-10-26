using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Assistant.Core;

public enum JournalCompactionStage
{
    AfterFilesCreated, CheckpointAfterHeader, CheckpointAfterPayload, CheckpointBeforeFlush, CheckpointAfterFlush,
    SwitchAfterHeader, SwitchBeforeFlush, SwitchAfterFlush, AnchorBeforeFlush, AnchorAfterFlush
}
public interface IJournalCompactionFaultInjector { void At(JournalCompactionStage stage, long sequence); }

/// <summary>
/// Two bounded data banks and an append-only switch log. The original journal handle remains the
/// exclusive lifetime owner. A switch never changes the logical sequence or checkpoint ciphertext.
/// </summary>
internal sealed class BankJournalStorage : IAsyncDisposable
{
    internal const int BankHeaderLength = 80, SwitchHeaderLength = 56, SwitchRecordLength = 120;
    private const int FrameHeaderLength = 44, EndLength = 64;
    private static ReadOnlySpan<byte> BankMagic => "ASBNK002"u8;
    private static ReadOnlySpan<byte> SwitchMagic => "ASSWCH02"u8;
    private static ReadOnlySpan<byte> EndMagic => "ASEND002"u8;
    private static ReadOnlySpan<byte> Commit => "COMMIT01"u8;
    private readonly FileStream[] banks;
    private readonly FileStream switches;
    private readonly int maxFrame;
    private readonly long maxBank, maxSwitch;
    private readonly IJournalFaultInjector? appendFaults;
    private readonly IJournalCompactionFaultInjector? faults;
    private Guid storeId;
    internal Guid StoreId => storeId;
    private SwitchRecord? active;
    private long endOffset;
    internal JournalSnapshot? Latest { get; private set; }
    internal JournalRecovery Recovery { get; private set; } = new(false, null, 0);

    internal static string BankPath(string path, int index) => path + ".bank" + index;
    internal static string SwitchPath(string path) => path + ".switch";
    internal static bool AnyArtifacts(string path) => File.Exists(BankPath(path, 0))
        || File.Exists(BankPath(path, 1)) || File.Exists(SwitchPath(path));

    private BankJournalStorage(FileStream[] banks, FileStream switches, int maxFrame, long maxBank,
        long maxSwitch, IJournalFaultInjector? appendFaults, IJournalCompactionFaultInjector? faults)
    {
        this.banks = banks; this.switches = switches; this.maxFrame = maxFrame; this.maxBank = maxBank;
        this.maxSwitch = maxSwitch; this.appendFaults = appendFaults; this.faults = faults;
    }

    internal static BankJournalStorage Open(string path, int maxFrame, long maxBank, long maxSwitch,
        IJournalFaultInjector? appendFaults, IJournalCompactionFaultInjector? faults, bool readOnly = false)
    {
        FileStream? first = null, second = null, control = null;
        try
        {
            first = Stream(BankPath(path, 0), FileMode.Open, readOnly);
            second = Stream(BankPath(path, 1), FileMode.Open, readOnly);
            control = Stream(SwitchPath(path), FileMode.Open, readOnly);
            var result = new BankJournalStorage([first, second], control, maxFrame, maxBank, maxSwitch, appendFaults, faults);
            result.ScanSwitches();
            if (!result.Recovery.Required) result.ScanActive();
            return result;
        }
        catch { first?.Dispose(); second?.Dispose(); control?.Dispose(); throw; }
    }

    internal static async Task<BankJournalStorage> CreateAsync(string path, JournalSnapshot checkpoint,
        int maxFrame, long maxBank, long maxSwitch, IJournalFaultInjector? appendFaults,
        IJournalCompactionFaultInjector? faults, CancellationToken ct)
    {
        FileStream? first = null, second = null, control = null;
        BankJournalStorage? result = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            first = Stream(BankPath(path, 0), FileMode.CreateNew);
            second = Stream(BankPath(path, 1), FileMode.CreateNew);
            control = Stream(SwitchPath(path), FileMode.CreateNew);
            result = new([first, second], control, maxFrame, maxBank, maxSwitch, appendFaults, faults) { storeId = Guid.NewGuid() };
            var header = new byte[SwitchHeaderLength];
            SwitchMagic.CopyTo(header); result.storeId.TryWriteBytes(header.AsSpan(8, 16));
            SHA256.HashData(header.AsSpan(0, 24)).CopyTo(header.AsSpan(24));
            await control.WriteAsync(header, ct).ConfigureAwait(false);
            control.Flush(true);
            await second.WriteAsync(result.BankHeader(Guid.NewGuid(), 0), ct).ConfigureAwait(false);
            second.Flush(true);
            first.Flush(true);
            faults?.At(JournalCompactionStage.AfterFilesCreated, checkpoint.Sequence);
            var generation = Guid.NewGuid();
            await result.WriteCheckpointAsync(0, generation, checkpoint, ct).ConfigureAwait(false);
            var record = new SwitchRecord(1, 0, generation, checkpoint.Sequence,
                checkpoint.Payload.Length, SHA256.HashData(checkpoint.Payload));
            await result.PublishAsync(record, ct).ConfigureAwait(false);
            result.active = record;
            result.Latest = new(checkpoint.Sequence, (byte[])checkpoint.Payload.Clone());
            result.Recovery = new(false, null, result.endOffset);
            return result;
        }
        catch
        {
            if (result is not null) await result.DisposeAsync().ConfigureAwait(false);
            else { first?.Dispose(); second?.Dispose(); control?.Dispose(); }
            throw;
        }
    }

    internal async Task AppendAsync(long expectedSequence, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (Recovery.Required) throw Error(LedgerErrors.RecoveryRequired, "The bank journal requires recovery.");
        if (active is null || Latest is null || expectedSequence != Latest.Sequence)
            throw Error(LedgerErrors.Conflict, "The bank journal sequence changed.");
        if (payload.Length is <= 0 || payload.Length > maxFrame)
            throw new JournalCapacityRejection("The encrypted frame limit would be exceeded.");
        ct.ThrowIfCancellationRequested();
        var required = FrameSize(payload.Length);
        bool compact = ShouldCompact(banks[active.Bank].Length, required, Latest.Payload.Length, maxBank);
        if (compact)
        {
            if (BankHeaderLength + FrameSize(Latest.Payload.Length) + required + EndLength > maxBank)
                throw new JournalCapacityRejection("Compaction cannot fit the unchanged checkpoint and next bounded frame.");
            if (switches.Length + SwitchRecordLength > maxSwitch)
                throw new JournalCapacityRejection("The bounded switch log is full; no history was discarded.");
        }
        else if (endOffset + required + EndLength > maxBank)
            throw new JournalCapacityRejection("The bank capacity would be exceeded.");
        try
        {
            if (compact) await CompactAsync(ct).ConfigureAwait(false);
            var sequence = checked(expectedSequence + 1);
            var file = banks[active.Bank];
            appendFaults?.At(JournalWriteStage.BeforeWrite, sequence);
            file.Position = endOffset;
            await file.WriteAsync(FrameHeader(sequence, payload.Span), ct).ConfigureAwait(false);
            appendFaults?.At(JournalWriteStage.AfterHeader, sequence);
            await file.WriteAsync(payload, ct).ConfigureAwait(false);
            appendFaults?.At(JournalWriteStage.AfterPayload, sequence);
            await file.WriteAsync(Commit.ToArray(), ct).ConfigureAwait(false);
            var newEndOffset = file.Position;
            await file.WriteAsync(End(active.Generation, sequence), ct).ConfigureAwait(false);
            file.SetLength(file.Position);
            appendFaults?.At(JournalWriteStage.BeforeFlush, sequence);
            file.Flush(true);
            appendFaults?.At(JournalWriteStage.AfterFlush, sequence);
            endOffset = newEndOffset;
            Latest = new(sequence, payload.ToArray());
            Recovery = new(false, null, endOffset);
        }
        catch
        {
            Recovery = new(true, "The bank append or switch outcome is uncertain; reopen before external actions.", endOffset);
            throw;
        }
    }

    internal static bool ShouldCompact(long length, long incomingFrameSize, int latestPayloadLength, long maximumBytes) =>
        length + incomingFrameSize > maximumBytes
        || length + incomingFrameSize >= maximumBytes - maximumBytes / 4
            && length > BankHeaderLength + 2 * FrameSize(latestPayloadLength) + EndLength;
    internal static long FrameSize(int payloadLength) => FrameHeaderLength + (long)payloadLength + 8;
    internal static bool CheckpointAndNextFit(int checkpointLength, int incomingLength, long maximumBytes) =>
        BankHeaderLength + FrameSize(checkpointLength) + FrameSize(incomingLength) + EndLength <= maximumBytes;

    private async Task CompactAsync(CancellationToken ct)
    {
        var checkpoint = Latest!;
        var slot = 1 - active!.Bank;
        var generation = Guid.NewGuid();
        await WriteCheckpointAsync(slot, generation, checkpoint, ct).ConfigureAwait(false);
        var next = new SwitchRecord(checked(active.Epoch + 1), slot, generation, checkpoint.Sequence,
            checkpoint.Payload.Length, SHA256.HashData(checkpoint.Payload));
        await PublishAsync(next, ct).ConfigureAwait(false);
        active = next;
        Recovery = new(false, null, endOffset);
    }

    private async Task WriteCheckpointAsync(int slot, Guid generation, JournalSnapshot checkpoint, CancellationToken ct)
    {
        if (checkpoint.Sequence < 1 || checkpoint.Payload.Length is <= 0 || checkpoint.Payload.Length > maxFrame
            || BankHeaderLength + FrameSize(checkpoint.Payload.Length) + EndLength > maxBank)
            throw Error(LedgerErrors.CapacityExceeded, "The unchanged checkpoint does not fit this bank.");
        var file = banks[slot];
        file.Position = 0; file.SetLength(0);
        await file.WriteAsync(BankHeader(generation, checkpoint.Sequence), ct).ConfigureAwait(false);
        faults?.At(JournalCompactionStage.CheckpointAfterHeader, checkpoint.Sequence);
        await file.WriteAsync(FrameHeader(checkpoint.Sequence, checkpoint.Payload), ct).ConfigureAwait(false);
        await file.WriteAsync(checkpoint.Payload, ct).ConfigureAwait(false);
        faults?.At(JournalCompactionStage.CheckpointAfterPayload, checkpoint.Sequence);
        await file.WriteAsync(Commit.ToArray(), ct).ConfigureAwait(false);
        var nextEndOffset = file.Position;
        await file.WriteAsync(End(generation, checkpoint.Sequence), ct).ConfigureAwait(false);
        file.SetLength(file.Position);
        faults?.At(JournalCompactionStage.CheckpointBeforeFlush, checkpoint.Sequence);
        file.Flush(true);
        faults?.At(JournalCompactionStage.CheckpointAfterFlush, checkpoint.Sequence);
        // Readback detects copy errors. Flush(true), not this read or a rename, is the durable boundary.
        file.Position = BankHeaderLength;
        var stored = ReadFrame(file, checkpoint.Sequence, out var reason);
        var trailer = new byte[EndLength];
        if (stored is null || reason is not null || !stored.Payload.AsSpan().SequenceEqual(checkpoint.Payload)
            || !ReadExactly(file, trailer) || !ValidEnd(trailer, generation, checkpoint.Sequence) || file.Position != file.Length)
            throw Error(LedgerErrors.CorruptStorage, "Checkpoint readback did not match the original snapshot.");
        endOffset = nextEndOffset;
    }

    private async Task PublishAsync(SwitchRecord record, CancellationToken ct)
    {
        if (switches.Length + SwitchRecordLength > maxSwitch)
            throw Error(LedgerErrors.CapacityExceeded, "The switch log capacity would be exceeded.");
        var bytes = new byte[SwitchRecordLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, record.Epoch);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), record.Bank);
        record.Generation.TryWriteBytes(bytes.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), record.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), record.PayloadLength);
        record.PayloadHash.CopyTo(bytes.AsSpan(48, 32));
        SwitchHash(bytes.AsSpan(0, 80)).CopyTo(bytes.AsSpan(80, 32));
        Commit.CopyTo(bytes.AsSpan(112, 8));
        switches.Position = switches.Length;
        await switches.WriteAsync(bytes.AsMemory(0, 80), ct).ConfigureAwait(false);
        faults?.At(JournalCompactionStage.SwitchAfterHeader, record.Sequence);
        await switches.WriteAsync(bytes.AsMemory(80), ct).ConfigureAwait(false);
        faults?.At(JournalCompactionStage.SwitchBeforeFlush, record.Sequence);
        switches.Flush(true);
        faults?.At(JournalCompactionStage.SwitchAfterFlush, record.Sequence);
    }

    private void ScanSwitches()
    {
        var header = new byte[SwitchHeaderLength];
        switches.Position = 0;
        if (switches.Length > maxSwitch || !ReadExactly(switches, header)
            || !header.AsSpan(0, 8).SequenceEqual(SwitchMagic)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header.AsSpan(0, 24)), header.AsSpan(24)))
        { Fail("The switch journal header is missing, oversized or damaged."); return; }
        storeId = new Guid(header.AsSpan(8, 16));
        if (storeId == Guid.Empty) { Fail("The switch store identity is empty."); return; }
        var epoch = 1L;
        var generations = new HashSet<Guid>();
        while (switches.Position < switches.Length)
        {
            var bytes = new byte[SwitchRecordLength];
            if (!ReadExactly(switches, bytes) || !bytes.AsSpan(112, 8).SequenceEqual(Commit)
                || !CryptographicOperations.FixedTimeEquals(SwitchHash(bytes.AsSpan(0, 80)), bytes.AsSpan(80, 32)))
            { Fail("The switch journal has an uncertain or damaged tail; older switches are not a writable fallback."); return; }
            var next = new SwitchRecord(BinaryPrimitives.ReadInt64LittleEndian(bytes),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)), new Guid(bytes.AsSpan(16, 16)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)),
                bytes.AsSpan(48, 32).ToArray());
            if (next.Epoch != epoch++ || next.Bank is < 0 or > 1 || next.Sequence < 1
                || next.PayloadLength is <= 0 || next.PayloadLength > maxFrame
                || next.Generation == Guid.Empty || !generations.Add(next.Generation)
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44)) != 0
                || active is { } prior && (next.Sequence < prior.Sequence || next.Bank == prior.Bank))
            { Fail("The switch identity, sequence or checkpoint scope is invalid."); return; }
            active = next;
        }
        if (active is null) Fail("No committed bank activation exists.");
    }

    private void ScanActive()
    {
        var descriptor = active!;
        var file = banks[descriptor.Bank];
        var header = new byte[BankHeaderLength];
        file.Position = 0;
        if (file.Length > maxBank || !ReadExactly(file, header)
            || !header.AsSpan(0, 8).SequenceEqual(BankMagic)
            || new Guid(header.AsSpan(8, 16)) != storeId || new Guid(header.AsSpan(24, 16)) != descriptor.Generation
            || BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)) != descriptor.Sequence
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header.AsSpan(0, 48)), header.AsSpan(48)))
        { Fail("The selected bank header is damaged; another bank is not a fallback."); return; }
        var sequence = descriptor.Sequence;
        var first = true;
        while (true)
        {
            var position = file.Position;
            var prefix = new byte[8];
            if (!ReadExactly(file, prefix)) { Fail("The selected bank has an incomplete tail."); return; }
            file.Position = position;
            if (prefix.AsSpan().SequenceEqual(EndMagic))
            {
                var end = new byte[EndLength];
                if (first || !ReadExactly(file, end) || !ValidEnd(end, descriptor.Generation, sequence - 1) || file.Position != file.Length)
                { Fail("The selected bank end marker is missing or damaged."); return; }
                endOffset = position;
                Recovery = new(false, null, position);
                return;
            }
            var snapshot = ReadFrame(file, sequence, out var error);
            if (snapshot is null)
            { Fail(error ?? "The selected bank frame is invalid."); return; }
            if (first && (snapshot.Payload.Length != descriptor.PayloadLength
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(snapshot.Payload), descriptor.PayloadHash)))
            { Fail("The selected checkpoint does not match its activation; no older bank was selected."); return; }
            Latest = snapshot;
            endOffset = file.Position;
            first = false;
            sequence = checked(sequence + 1);
        }
    }

    private JournalSnapshot? ReadFrame(FileStream file, long expectedSequence, out string? error)
    {
        error = null;
        var header = new byte[FrameHeaderLength];
        if (!ReadExactly(file, header)) { error = "An incomplete bank frame header was preserved."; return null; }
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (sequence != expectedSequence || length is <= 0 || length > maxFrame || file.Length - file.Position < length + 8L)
        { error = "A bank frame length or logical sequence is invalid."; return null; }
        var payload = new byte[length]; var marker = new byte[8];
        if (!ReadExactly(file, payload) || !ReadExactly(file, marker) || !marker.AsSpan().SequenceEqual(Commit)
            || !CryptographicOperations.FixedTimeEquals(HashFrame(sequence, payload), header.AsSpan(12, 32)))
        { error = "A bank frame checksum or commit marker is damaged."; return null; }
        return new(sequence, payload);
    }

    private byte[] BankHeader(Guid generation, long sequence)
    {
        var bytes = new byte[BankHeaderLength];
        BankMagic.CopyTo(bytes); storeId.TryWriteBytes(bytes.AsSpan(8, 16)); generation.TryWriteBytes(bytes.AsSpan(24, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), sequence);
        SHA256.HashData(bytes.AsSpan(0, 48)).CopyTo(bytes.AsSpan(48));
        return bytes;
    }
    private static byte[] End(Guid generation, long sequence)
    {
        var bytes = new byte[EndLength]; EndMagic.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), sequence); generation.TryWriteBytes(bytes.AsSpan(16, 16));
        SHA256.HashData(bytes.AsSpan(0, 32)).CopyTo(bytes.AsSpan(32));
        return bytes;
    }
    private static bool ValidEnd(ReadOnlySpan<byte> bytes, Guid generation, long sequence) =>
        bytes[..8].SequenceEqual(EndMagic) && BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) == sequence
        && new Guid(bytes.Slice(16, 16)) == generation
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes[..32]), bytes[32..]);
    private byte[] SwitchHash(ReadOnlySpan<byte> prefix)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(storeId.ToByteArray()); hash.AppendData(prefix); return hash.GetHashAndReset();
    }
    private static byte[] FrameHeader(long sequence, ReadOnlySpan<byte> payload)
    {
        var header = new byte[FrameHeaderLength];
        BinaryPrimitives.WriteInt64LittleEndian(header, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
        HashFrame(sequence, payload).CopyTo(header.AsSpan(12)); return header;
    }
    private static byte[] HashFrame(long sequence, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(prefix, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[8..], payload.Length);
        hash.AppendData(prefix); hash.AppendData(payload); return hash.GetHashAndReset();
    }
    private static bool ReadExactly(FileStream file, Span<byte> bytes)
    {
        int read = 0;
        while (read < bytes.Length) { int count = file.Read(bytes[read..]); if (count == 0) return false; read += count; }
        return true;
    }
    private static FileStream Stream(string path, FileMode mode, bool readOnly = false) =>
        new(path, mode, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read, 4096,
            readOnly ? FileOptions.Asynchronous : FileOptions.Asynchronous | FileOptions.WriteThrough);
    private void Fail(string reason) => Recovery = new(true, reason, endOffset);
    private static LedgerException Error(string code, string message) => new(code, message);
    private sealed record SwitchRecord(long Epoch, int Bank, Guid Generation, long Sequence, int PayloadLength, byte[] PayloadHash);
    public async ValueTask DisposeAsync()
    {
        foreach (var bank in banks) await bank.DisposeAsync().ConfigureAwait(false);
        await switches.DisposeAsync().ConfigureAwait(false);
    }
}
