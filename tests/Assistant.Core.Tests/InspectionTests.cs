using System.Security.Cryptography;
using Assistant.Core;

internal static class InspectionTests
{
    internal static async Task<int> Run()
    {
        await JournalReadOnly();
        foreach (bool archived in new[] { false, true })
        {
            await using var fixture = new Fixture();
            var protector = new TestEncryption(fixture.Key);
            string archivePath = fixture.Path + ".archive";
            if (archived) Directory.CreateDirectory(archivePath);
            async Task<HistoryArchiveSet?> Archive(bool create, bool readOnly = false) => archived
                ? await HistoryArchiveSet.OpenAsync(archivePath, protector, create, readOnly: readOnly) : null;
            await using (var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(fixture.Path), protector,
                historyArchive: await Archive(true)))
            {
                await ledger.ReceiveAsync(Tests.Identity, "inspect-one", "synthetic", MessageTrust.AuthenticatedUser, FakeClock.Now);
                await ledger.BeginGenerationAsync(Tests.Identity, ["inspect-one"]);
            }
            var paths = Directory.GetFiles(Path.GetDirectoryName(fixture.Path)!, "*", SearchOption.AllDirectories);
            var hashes = paths.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            await using (var inspect = await DurableConversationLedger.OpenForInspectionAsync(new FileJournalStorage(fixture.Path, readOnly: true), protector,
                historyArchive: await Archive(false, true)))
            {
                var state = await inspect.ReadConversationAsync(Tests.Identity);
                if (state.Generations.Single().Stage != GenerationStage.Processing)
                    throw new Exception("Inspection changed persisted generation state.");
                bool rejected = false;
                try { await inspect.ReceiveAsync(Tests.Identity, "forbidden", "write", MessageTrust.AuthenticatedUser, FakeClock.Now); }
                catch (LedgerException) { rejected = true; }
                if (!rejected) throw new Exception("Inspection accepted a write.");
            }
            if (hashes.Any(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key))) != x.Value)
                || Directory.GetFiles(Path.GetDirectoryName(fixture.Path)!, "*", SearchOption.AllDirectories).Length != paths.Length)
                throw new Exception("Inspection changed file contents or inventory.");
            await using (var normal = await DurableConversationLedger.OpenAsync(new FileJournalStorage(fixture.Path), protector,
                historyArchive: await Archive(false)))
            {
                if ((await normal.ReadConversationAsync(Tests.Identity)).Generations.Single().Stage != GenerationStage.Interrupted)
                    throw new Exception("Normal recovery no longer works.");
            }
            Console.WriteLine($"PASS inspection archived={archived}: original state, write rejection, unchanged files, normal recovery");
        }
        return 0;
    }

    private static async Task JournalReadOnly()
    {
        await using var fixture = new Fixture();
        string missing = fixture.Path + ".missing";
        try { await using var unexpected = new FileJournalStorage(missing, readOnly: true); throw new Exception("Created missing journal."); }
        catch (FileNotFoundException) { }
        if (File.Exists(missing)) throw new Exception("Missing journal was created.");
        await using (var writer = new FileJournalStorage(fixture.Path, maximumFrameBytes: 1024, maximumJournalBytes: 2048))
            for (int i = 0; i < 8; i++) await writer.AppendAsync(i, new byte[700]);
        if (!File.Exists(fixture.Path + ".bank0")) throw new Exception("Bank fixture was not rotated.");
        var paths = Directory.GetFiles(Path.GetDirectoryName(fixture.Path)!);
        var hashes = paths.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        foreach (var path in paths) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            await using var reader = new FileJournalStorage(fixture.Path, maximumFrameBytes: 1024, maximumJournalBytes: 2048, readOnly: true);
            if ((await reader.ReadLatestAsync())?.Sequence != 8 || reader.Recovery.Required) throw new Exception("Read-only bank scan failed.");
            bool rejected = false;
            try { await reader.AppendAsync(8, new byte[700]); } catch (LedgerException) { rejected = true; }
            if (!rejected) throw new Exception("Read-only journal accepted append.");
        }
        finally { foreach (var path in paths) File.SetAttributes(path, FileAttributes.Normal); }
        if (hashes.Any(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key))) != x.Value))
            throw new Exception("Read-only journal changed bytes.");
        Console.WriteLine("PASS inspection journal: missing not created, read-only bank files readable, append rejected, bytes unchanged");
    }
}
