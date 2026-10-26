using System.Security.Cryptography;
using System.Text.Json;

namespace Assistant.Core;

/// <summary>
/// Size of the validated in-memory logical state serialized by the current
/// writer, across every identity in this ledger. Not disk usage, free space,
/// guaranteed room for another transaction, or a claim that recovery is done.
/// </summary>
public sealed record LedgerCapacitySnapshot(int LogicalSnapshotBytes, int MaximumSnapshotBytes,
    long JournalSequence, bool IsReadOnly, int? StoredSnapshotBytes = null, int? MaximumStoredSnapshotBytes = null,
    HistoryArchiveStorageSnapshot? ArchiveStorage = null)
{
    public bool NearLimit => (long)LogicalSnapshotBytes * 5 >= (long)MaximumSnapshotBytes * 4
        || StoredSnapshotBytes is { } stored && MaximumStoredSnapshotBytes is { } maximum && (long)stored * 5 >= (long)maximum * 4;
}

public sealed record ConversationDiagnosticSnapshot(ConversationSnapshot Conversation, LedgerCapacitySnapshot Capacity);

public sealed partial class DurableConversationLedger
{
    // Measured once after open; successful mutations already have the exact
    // serialized buffer, so diagnostics do not repeatedly allocate all history.
    private int? _logicalSnapshotBytes;

    public async Task<ConversationDiagnosticSnapshot> ReadConversationDiagnosticsAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_logicalSnapshotBytes.HasValue)
            {
                byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(_state, Json);
                try { _logicalSnapshotBytes = plaintext.Length; }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            return new(SnapshotOf(_state, identity),
                new(_logicalSnapshotBytes.Value, LogicalCapacity, _sequence, IsReadOnly,
                    _historyArchive == null ? null : _storedSnapshotBytes, _historyArchive == null ? null : _options.MaximumSnapshotBytes,
                    _historyArchive == null ? null : await _historyArchive.ReadStorageAsync(cancellationToken).ConfigureAwait(false)));
        }
        finally { _gate.Release(); }
    }
}
