using System.Security.Cryptography;
using Assistant.Core;

internal static class HistoryArchiveSetTests
{
    internal static async Task<int> Run()
    {
        (string Name, Func<Task> Test)[] cases = [
            ("rollover_reopen_and_cross_volume_dedup_keep_original_references", Rollover),
            ("maximum_volume_count_does_not_remove_or_replace_history", Limit),
            ("catalogue_publish_faults_never_adopt_orphan_payloads", Publication),
            ("missing_registered_volume_is_not_recreated", MissingVolume),
            ("foreign_volume_with_same_filename_is_rejected", ForeignVolume),
            ("missing_catalogue_cannot_reinitialize_existing_history", MissingCatalogue),
            ("damaged_volume_prefix_stays_readable_but_no_new_reference", Damaged),
            ("set_writer_exclusion_and_cancel_preserve_catalogue", Exclusive)
        ];
        int failed = 0;
        foreach (var (name, test) in cases)
        {
            try { await test(); Console.WriteLine("PASS archive_set/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL archive_set/" + name + ": " + error); }
        }
        Console.WriteLine($"ARCHIVE SET TOTAL {cases.Length}; PASSED {cases.Length - failed}; FAILED {failed}"); return failed;
    }
    private static string Root(Fixture f) => f.Path + ".set";
    private static async Task<HistoryArchiveSet> Open(Fixture f, bool create = false, int limit = 8, FaultInjector? fault = null)
    {
        if (create) Directory.CreateDirectory(Root(f));
        return await HistoryArchiveSet.OpenAsync(Root(f), new TestEncryption(f.Key), create, 320, limit, fault);
    }
    private static byte[] Body(int id) => Enumerable.Repeat((byte)id, 100).ToArray();
    private static string[] Files(Fixture f) => Directory.GetFiles(Root(f), "*.volume").Order().ToArray();
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("archive_set_assertion_failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is LedgerException or IOException or CryptographicException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private static async Task Rollover()
    {
        await using var f = new Fixture(); var refs = new List<ArchivedPayload>();
        await using (var set = await Open(f, true))
        {
            for (int i = 0; i < 4; i++) refs.Add(await set.PutAsync(Body(i)));
            Check(set.VolumeCount == 4 && Files(f).Length == 4);
            Check(await set.PutAsync(Body(0)) == refs[0] && Files(f).Length == 4);
        }
        await using var reopened = await Open(f);
        for (int i = 0; i < refs.Count; i++) Check((await reopened.ReadAsync(refs[i])).SequenceEqual(Body(i)));
        Check(await reopened.PutAsync(Body(1)) == refs[1] && reopened.VolumeCount == 4);
    }
    private static async Task Limit()
    {
        await using var f = new Fixture(); await using var set = await Open(f, true, 1);
        var first = await set.PutAsync(Body(1));
        await Reject(() => set.PutAsync(Body(2)));
        Check(!set.IsReadOnly && set.VolumeCount == 1 && Files(f).Length == 1 && await set.PutAsync(Body(1)) == first);
    }
    private static async Task Publication()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterHeader, JournalWriteStage.AfterFlush })
        {
            await using var f = new Fixture(); var faults = new FaultInjector();
            await using (var set = await Open(f, true, fault: faults))
            {
                faults.Stage = stage; await Reject(() => set.PutAsync(Body(1)));
                Check(set.IsReadOnly); await Reject(() => set.PutAsync(Body(2)));
            }
            string orphan = Files(f).Single(); ArchivedPayload original;
            await using (var volume = new HistoryArchive(orphan, new TestEncryption(f.Key)))
                original = new(volume.Id, Convert.ToHexString(SHA256.HashData(Body(1))), Body(1).Length);
            await using var reopened = await Open(f);
            if (stage == JournalWriteStage.AfterFlush)
            { Check(await reopened.PutAsync(Body(1)) == original && Files(f).Length == 1); }
            else
            {
                await Reject(() => reopened.ReadAsync(original)); Check(reopened.VolumeCount == 0);
                if (stage == JournalWriteStage.AfterHeader) Check(reopened.IsReadOnly);
                else { Check(await reopened.PutAsync(Body(1)) != original && Files(f).Length == 2); }
            }
        }
    }
    private static async Task MissingVolume()
    {
        await using var f = new Fixture();
        await using (var set = await Open(f, true)) await set.PutAsync(Body(1));
        string file = Files(f).Single(); File.Move(file, file + ".retained");
        await Reject(async () => { await using var unused = await Open(f); });
        Check(!File.Exists(file) && File.Exists(file + ".retained"));
    }
    private static async Task ForeignVolume()
    {
        await using var f = new Fixture(); await using var other = new Fixture();
        await using (var set = await Open(f, true)) await set.PutAsync(Body(1));
        await using (var set = await Open(other, true)) await set.PutAsync(Body(1));
        File.Copy(Files(other).Single(), Files(f).Single(), true);
        await Reject(async () => { await using var unused = await Open(f); });
    }
    private static async Task MissingCatalogue()
    {
        await using var f = new Fixture();
        await using (var set = await Open(f, true)) await set.PutAsync(Body(1));
        string path = Path.Combine(Root(f), "catalogue.state"); File.Move(path, path + ".retained");
        await Reject(async () => { await using var unused = await Open(f); });
        Check(!File.Exists(path) && Files(f).Length == 1);
    }
    private static async Task Damaged()
    {
        await using var f = new Fixture(); ArchivedPayload first;
        await using (var set = await Open(f, true)) first = await set.PutAsync(Body(1));
        using (var tail = new FileStream(Files(f).Single(), FileMode.Append, FileAccess.Write)) { tail.WriteByte(9); tail.Flush(true); }
        await using var reopened = await Open(f);
        Check(reopened.IsReadOnly && (await reopened.ReadAsync(first)).SequenceEqual(Body(1)));
        await Reject(() => reopened.PutAsync(Body(2))); Check(Files(f).Length == 1);
    }
    private static async Task Exclusive()
    {
        await using var f = new Fixture(); await using var set = await Open(f, true);
        await Reject(async () => { await using var unused = await Open(f); });
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await set.PutAsync(Body(1), cancel.Token); throw new InvalidOperationException("cancel_ignored"); }
        catch (OperationCanceledException) { }
        Check(set.VolumeCount == 0 && Files(f).Length == 0); await set.PutAsync(Body(1));
    }
}
