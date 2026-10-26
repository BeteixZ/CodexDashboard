using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assistant.Core;

public sealed record HistoryArchiveStorageSnapshot(long FileBytes, int FileCount, int RegisteredVolumes, int MaximumVolumes);

/// <summary>Durable volume catalogue. Payloads and catalogue commits precede publishing references.</summary>
public sealed class HistoryArchiveSet : IAsyncDisposable
{
    private sealed record Volume(Guid File, Guid Archive);
    private sealed record Catalogue(int Version, Volume[] Volumes);
    private readonly string directory;
    private readonly IStateProtector protector;
    private readonly long volumeBytes;
    private readonly int maximumVolumes;
    private readonly FileStream owner;
    private readonly FileJournalStorage catalogue;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Volume> volumes = [];
    private readonly Dictionary<Guid, HistoryArchive> opened = [];
    private readonly Dictionary<string, ArchivedPayload> contents = new(StringComparer.Ordinal);
    private long sequence;
    private bool uncertain, disposed;
    private bool inspection;
    public bool RequiresRecovery => uncertain || catalogue.Recovery.Required;
    public bool IsReadOnly => inspection || RequiresRecovery;
    public int VolumeCount => volumes.Count;

    public async Task<HistoryArchiveStorageSnapshot> ReadStorageAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long bytes = 0; int count = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            { bytes += file.Length; count++; }
            return new(bytes, count, volumes.Count, maximumVolumes);
        }
        finally { gate.Release(); }
    }

    private HistoryArchiveSet(string directory, IStateProtector protector, long volumeBytes, int maximumVolumes,
        FileStream owner, FileJournalStorage catalogue)
    { this.directory = directory; this.protector = protector; this.volumeBytes = volumeBytes; this.maximumVolumes = maximumVolumes;
        this.owner = owner; this.catalogue = catalogue; }

    public static async Task<HistoryArchiveSet> OpenAsync(string directory, IStateProtector protector, bool createNew = false,
        long volumeBytes = 64L * 1024 * 1024, int maximumVolumes = 128,
        IJournalFaultInjector? catalogueFault = null, CancellationToken ct = default, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(protector); ct.ThrowIfCancellationRequested();
        if (readOnly && createNew) throw new ArgumentException("Read-only archive sets cannot be created.");
        if (volumeBytes < 128 || maximumVolumes is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(volumeBytes));
        // The caller chooses an existing dedicated directory. Missing data is never initialized on reopen.
        var owner = new FileStream(Path.Combine(directory, "archive.owner"), readOnly ? FileMode.Open : FileMode.OpenOrCreate,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
        HistoryArchiveSet? result = null; FileJournalStorage? journal = null;
        try
        {
            string path = Path.Combine(directory, "catalogue.state");
            if (createNew)
            {
                if (File.Exists(path) || Directory.EnumerateFiles(directory, "*.volume").Any())
                    throw new IOException("A new archive set requires an unused directory.");
            }
            else if (!File.Exists(path) || new FileInfo(path).Length < 8) throw Corrupt();
            journal = new FileJournalStorage(path, maximumFrameBytes: 64 * 1024, maximumJournalBytes: 4 * 1024 * 1024, faultInjector: catalogueFault, readOnly: readOnly);
            result = new(directory, protector, volumeBytes, maximumVolumes, owner, journal);
            result.inspection = readOnly;
            var snapshot = await journal.ReadLatestAsync(ct).ConfigureAwait(false);
            if (snapshot == null)
            {
                if (!createNew || journal.Recovery.Required) throw Corrupt();
                await result.SaveCatalogueAsync([], ct).ConfigureAwait(false);
            }
            else
            {
                result.sequence = snapshot.Sequence;
                var plain = protector.Unprotect(snapshot.Payload, result.Purpose(snapshot.Sequence));
                Catalogue saved;
                try { saved = JsonSerializer.Deserialize<Catalogue>(plain) ?? throw Corrupt(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (saved.Version != 1 || saved.Volumes.Length > 128
                    || saved.Volumes.Any(v => v.File == Guid.Empty || v.Archive == Guid.Empty)
                    || saved.Volumes.Select(v => v.File).Distinct().Count() != saved.Volumes.Length
                    || saved.Volumes.Select(v => v.Archive).Distinct().Count() != saved.Volumes.Length) throw Corrupt();
                foreach (var volume in saved.Volumes)
                {
                    ct.ThrowIfCancellationRequested();
                    var archive = new HistoryArchive(result.VolumePath(volume.File), protector, volumeBytes, readOnly: readOnly);
                    result.opened.Add(volume.Archive, archive);
                    if (archive.Id != volume.Archive) throw Corrupt();
                    result.uncertain |= archive.Recovery.Required;
                    result.volumes.Add(volume);
                    foreach (var reference in archive.References) result.contents.TryAdd(reference.Sha256, reference);
                }
            }
            return result;
        }
        catch
        {
            if (result != null) await result.DisposeAsync().ConfigureAwait(false);
            else { if (journal != null) await journal.DisposeAsync().ConfigureAwait(false); owner.Dispose(); }
            throw;
        }
    }
    private string VolumePath(Guid file) => Path.Combine(directory, file.ToString("N") + ".volume");
    private byte[] Purpose(long number) => Encoding.UTF8.GetBytes($"assistant-history-catalogue/v1/{protector.Scheme}/{number}");
    private static LedgerException Corrupt() => new(LedgerErrors.CorruptStorage, "The history catalogue or a referenced volume is missing or invalid; original files were preserved.");
    private async Task SaveCatalogueAsync(Volume[] next, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Catalogue(1, next));
        byte[] encrypted;
        try { encrypted = protector.Protect(plain, Purpose(sequence + 1)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        try
        {
            await catalogue.AppendAsync(sequence, encrypted, ct).ConfigureAwait(false);
            sequence++;
        }
        catch { uncertain = true; throw; }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }
    public async Task<ArchivedPayload> PutAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (body.Length is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(body));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsReadOnly) throw new LedgerException(LedgerErrors.RecoveryRequired, "The archive set requires reconciliation before new references can be published.");
            string hash = Convert.ToHexString(SHA256.HashData(body.Span));
            if (contents.TryGetValue(hash, out var existing))
                return await opened[existing.ArchiveId].PutAsync(body, ct).ConfigureAwait(false);
            if (volumes.Count > 0)
            {
                try
                {
                    var reference = await opened[volumes[^1].Archive].PutAsync(body, ct).ConfigureAwait(false);
                    contents.Add(reference.Sha256, reference); return reference;
                }
                catch (JournalCapacityRejection) { }
                catch { uncertain |= opened[volumes[^1].Archive].Recovery.Required; throw; }
            }
            if (volumes.Count >= maximumVolumes) throw new JournalCapacityRejection("The history volume limit was reached; existing history was retained.");
            Guid name = Guid.NewGuid();
            var next = new HistoryArchive(VolumePath(name), protector, volumeBytes, createNew: true);
            try
            {
                // Persist the payload before the catalogue. Failed publication leaves only an unreferenced file.
                var reference = await next.PutAsync(body, ct).ConfigureAwait(false);
                var volume = new Volume(name, next.Id);
                await SaveCatalogueAsync([.. volumes, volume], ct).ConfigureAwait(false);
                volumes.Add(volume); opened.Add(next.Id, next); contents.Add(reference.Sha256, reference);
                next = null!;
                return reference;
            }
            finally { if (next != null) await next.DisposeAsync().ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }
    public async Task<byte[]> ReadAsync(ArchivedPayload reference, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!opened.TryGetValue(reference.ArchiveId, out var volume)) throw Corrupt();
            return await volume.ReadAsync(reference, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return; disposed = true;
            try { foreach (var volume in opened.Values) await volume.DisposeAsync().ConfigureAwait(false); }
            finally { try { await catalogue.DisposeAsync().ConfigureAwait(false); } finally { owner.Dispose(); } }
        }
        finally { gate.Release(); }
    }
}
