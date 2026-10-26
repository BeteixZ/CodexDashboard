using System.Security.Cryptography;
using System.Text;
using Assistant.Core;

internal static class HistoryArchiveTests
{
    internal static async Task<int> Run()
    {
        (string Name, Func<Task> Test)[] cases = [
            ("encrypted_deduplicated_payload_survives_reopen", RoundTrip),
            ("volume_identity_and_content_reference_are_exact", Identity),
            ("capacity_rejection_preserves_payload_and_allows_smaller_write", Capacity),
            ("append_faults_preserve_committed_prefix_without_repair", Faults),
            ("damaged_payload_cannot_be_read_or_silently_replaced", Corruption),
            ("missing_empty_existing_archive_is_not_created_or_reset", Missing),
            ("concurrent_duplicate_puts_share_one_committed_frame", Concurrent),
            ("cancelled_append_preserves_bytes_and_writer", Cancel)
        ];
        int failed = 0;
        foreach (var (name, test) in cases)
        {
            try { await test(); Console.WriteLine("PASS archive/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL archive/" + name + ": " + error); }
        }
        Console.WriteLine($"ARCHIVE TOTAL {cases.Length}; PASSED {cases.Length - failed}; FAILED {failed}"); return failed;
    }
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("SYNTHETIC_ARCHIVE_BODY：原始消息、来源与时间保持不变。🙂");
    private static HistoryArchive Open(Fixture f, bool create = false, long max = 64 * 1024 * 1024, FaultInjector? fault = null)
        => new(f.Path + ".archive", new TestEncryption(f.Key), max, fault, create);
    private static byte[] Bytes(Fixture f)
    {
        using var file = new FileStream(f.Path + ".archive", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream(); file.CopyTo(memory); return memory.ToArray();
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("archive_assertion_failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is LedgerException or IOException or CryptographicException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private static async Task RoundTrip()
    {
        await using var f = new Fixture(); ArchivedPayload reference;
        await using (var archive = Open(f, true))
        {
            reference = await archive.PutAsync(Body); var bytes = Bytes(f);
            Check(await archive.PutAsync(Body) == reference && bytes.SequenceEqual(Bytes(f)));
            Check(!Encoding.UTF8.GetString(bytes).Contains("SYNTHETIC_ARCHIVE_BODY"));
        }
        await using var reopened = Open(f); Check((await reopened.ReadAsync(reference)).SequenceEqual(Body));
    }
    private static async Task Identity()
    {
        await using var f = new Fixture(); await using var other = new Fixture();
        await using var archive = Open(f, true); await using var second = Open(other, true);
        var reference = await archive.PutAsync(Body); await second.PutAsync(Body);
        await Reject(() => second.ReadAsync(reference));
        await Reject(() => archive.ReadAsync(reference with { Bytes = reference.Bytes + 1 }));
        await Reject(() => archive.ReadAsync(reference with { Sha256 = new string('0', 64) }));
    }
    private static async Task Capacity()
    {
        await using var f = new Fixture(); await using var archive = Open(f, true, 512);
        var first = await archive.PutAsync(Body); var bytes = Bytes(f);
        await Reject(() => archive.PutAsync(new byte[800]));
        Check(!archive.Recovery.Required && bytes.SequenceEqual(Bytes(f)) && (await archive.ReadAsync(first)).SequenceEqual(Body));
        await archive.PutAsync(new byte[1]);
    }
    private static async Task Faults()
    {
        foreach (JournalWriteStage stage in Enum.GetValues<JournalWriteStage>())
        {
            await using var f = new Fixture(); var fault = new FaultInjector(); ArchivedPayload first;
            await using (var archive = Open(f, true, fault: fault))
            {
                first = await archive.PutAsync(Body); fault.Stage = stage;
                await Reject(() => archive.PutAsync(new byte[37]));
                Check(archive.Recovery.Required && (await archive.ReadAsync(first)).SequenceEqual(Body));
                await Reject(() => archive.PutAsync(new byte[38]));
            }
            var bytes = Bytes(f); await using var reopened = Open(f);
            Check((await reopened.ReadAsync(first)).SequenceEqual(Body) && bytes.SequenceEqual(Bytes(f)));
            bool torn = stage is JournalWriteStage.AfterHeader or JournalWriteStage.AfterPayload;
            Check(reopened.Recovery.Required == torn);
            if (torn) await Reject(() => reopened.PutAsync(new byte[37]));
            else
            {
                var recovered = await reopened.PutAsync(new byte[37]);
                Check((await reopened.ReadAsync(recovered)).Length == 37);
                if (stage != JournalWriteStage.BeforeWrite) Check(bytes.SequenceEqual(Bytes(f)));
            }
        }
    }
    private static async Task Corruption()
    {
        await using var f = new Fixture(); ArchivedPayload first;
        await using (var archive = Open(f, true)) first = await archive.PutAsync(Body);
        var bytes = Bytes(f); bytes[100] ^= 1; await File.WriteAllBytesAsync(f.Path + ".archive", bytes);
        await using var reopened = Open(f); Check(reopened.Recovery.Required);
        await Reject(() => reopened.ReadAsync(first)); await Reject(() => reopened.PutAsync(Body));
        Check(bytes.SequenceEqual(Bytes(f)));
    }
    private static async Task Missing()
    {
        await using var f = new Fixture();
        await Reject(async () => { await using var unused = Open(f); });
        Check(!File.Exists(f.Path + ".archive")); await File.WriteAllBytesAsync(f.Path + ".archive", []);
        await Reject(async () => { await using var unused = Open(f); });
        Check(new FileInfo(f.Path + ".archive").Length == 0);
        await Reject(async () => { await using var unused = Open(f, true); });
    }
    private static async Task Concurrent()
    {
        await using var f = new Fixture(); await using var archive = Open(f, true);
        var references = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => archive.PutAsync(Body)));
        Check(references.Distinct().Count() == 1);
        var bytes = Bytes(f); Check(await archive.PutAsync(Body) == references[0] && bytes.SequenceEqual(Bytes(f)));
        await Reject(async () => { await using var anotherWriter = Open(f); });
    }
    private static async Task Cancel()
    {
        await using var f = new Fixture(); await using var archive = Open(f, true); var bytes = Bytes(f);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await archive.PutAsync(Body, cancellation.Token); throw new InvalidOperationException("cancel_ignored"); }
        catch (OperationCanceledException) { }
        Check(bytes.SequenceEqual(Bytes(f)) && !archive.Recovery.Required); await archive.PutAsync(Body);
    }
}
