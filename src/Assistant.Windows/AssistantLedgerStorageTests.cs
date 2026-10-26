using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantLedgerStorageTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--archive-storage-test") return false;
        string root = args[1]; Directory.CreateDirectory(root); var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks.Add(name, value); if (!value) throw new InvalidOperationException(name); }
        var identity = new ConversationIdentity("synthetic-archive-user", "synthetic-binding", "main");
        async Task Input(DurableConversationLedger ledger, string id) => await ledger.ReceiveAsync(identity, id,
            "SYNTHETIC_ARCHIVE_" + id, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        async Task<bool> Rejected(string folder)
        {
            try { await using var unused = await AssistantLedgerStorage.OpenAsync(folder, false); return false; }
            catch (Exception error) when (error is LedgerException or IOException or CryptographicException) { return true; }
        }
        byte[] Read(string path)
        { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var data = new MemoryStream(); file.CopyTo(data); return data.ToArray(); }
        try
        {
            var settings = new Settings();
            Check("archive setting defaults off and roundtrips opt in", !settings.HistoryArchiveEnabled
                && JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { HistoryArchiveEnabled = true }, Settings.Json), Settings.Json)!.HistoryArchiveEnabled);
            string legacy = Folder("legacy");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(legacy, false)) await Input(ledger, "legacy");
            Check("default host creates no archive", !Directory.Exists(Path.Combine(legacy, "history-archive")));
            string archived = Folder("archived"), journal = Path.Combine(archived, "conversations.state");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(archived, false)) await Input(ledger, "original");
            var before = Read(journal);
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(archived, true))
            {
                Check("enable leaves original journal untouched before next transaction", before.SequenceEqual(Read(journal)));
                await Input(ledger, "new");
                var capacity = (await ledger.ReadConversationDiagnosticsAsync(identity)).Capacity;
                Check("host archive exposes separate logical and manifest bounds", capacity.MaximumSnapshotBytes == 64 * 1024 * 1024
                    && capacity.MaximumStoredSnapshotBytes == 8 * 1024 * 1024 && capacity.StoredSnapshotBytes > 0);
                string archiveDirectory = Path.Combine(archived, "history-archive");
                var files = new DirectoryInfo(archiveDirectory).GetFiles();
                Check("archive file lengths match actual dedicated directory", capacity.ArchiveStorage is { } measured
                    && measured.FileBytes == files.Sum(file => file.Length) && measured.FileCount == files.Length
                    && measured.RegisteredVolumes == 1 && measured.MaximumVolumes == 128);
                File.WriteAllBytes(Path.Combine(archiveDirectory, "synthetic-retained.volume"), new byte[137]);
                var refreshed = (await ledger.ReadConversationDiagnosticsAsync(identity)).Capacity;
                Check("archive measurement includes unregistered retained files without adopting them",
                    refreshed.ArchiveStorage!.FileBytes == capacity.ArchiveStorage!.FileBytes + 137
                    && refreshed.ArchiveStorage.FileCount == capacity.ArchiveStorage.FileCount + 1
                    && refreshed.ArchiveStorage.RegisteredVolumes == 1 && refreshed.JournalSequence == capacity.JournalSequence);
            }
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(archived, false))
            {
                var read = await ledger.ReadConversationAsync(identity);
                Check("persisted archive reopens even if opt in flag is off", read.Inbox.Length == 2
                    && read.Inbox[0].SourceMessageId == "original" && read.Inbox[1].SourceMessageId == "new");
                await Input(ledger, "new");
                Check("protected archived input remains deduplicated", (await ledger.ReadConversationAsync(identity)).Inbox.Length == 2);
            }
            string missing = Folder("missing-volume");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(missing, true)) await Input(ledger, "missing");
            string missingJournal = Path.Combine(missing, "conversations.state"); before = Read(missingJournal);
            string volume = Directory.GetFiles(Path.Combine(missing, "history-archive"), "*.volume").Single();
            File.Move(volume, volume + ".retained");
            Check("missing archive volume never falls back or recreates data", await Rejected(missing)
                && !File.Exists(volume) && before.SequenceEqual(Read(missingJournal)));
            await using (var storage = new FileJournalStorage(missingJournal))
                Check("failed archive open releases journal ownership", await storage.ReadLatestAsync() != null);
            string noJournal = Folder("missing-journal");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(noJournal, true)) await Input(ledger, "one");
            string path = Path.Combine(noJournal, "conversations.state"); File.Move(path, path + ".retained");
            Check("remaining archive prevents empty replacement ledger", await Rejected(noJournal) && !File.Exists(path));
            string partial = Folder("partial-archive");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(partial, false)) await Input(ledger, "one");
            Directory.CreateDirectory(Path.Combine(partial, "history-archive")); bool refused = false;
            try { await using var unused = await AssistantLedgerStorage.OpenAsync(partial, true); }
            catch (LedgerException) { refused = true; }
            Check("existing incomplete archive is not silently initialized", refused
                && !File.Exists(Path.Combine(partial, "history-archive", "catalogue.state")));
        }
        catch (Exception error) { checks["failure"] = false; File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); }
        File.WriteAllText(Path.Combine(root, "archive-storage-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        exit(checks.Values.All(value => value) ? 0 : 1); return true;
    }
}
