using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static class ArchivedLedgerTests
{
    private static readonly ConversationIdentity Identity = new("archive-ledger", "synthetic", "main");
    private static readonly LedgerOptions Options = new() { MaximumSnapshotBytes = 4096, MaximumArchivedSnapshotBytes = 64 * 1024 };
    private static readonly string Body = new string('x', 6000) + "中文🙂\n保留原文";
    private static string Root(Fixture f) => f.Path + ".history";
    private static async Task<DurableConversationLedger> Open(Fixture f, bool create = false,
        FaultInjector? ledgerFault = null, FaultInjector? archiveFault = null, LedgerOptions? options = null, string? path = null)
    {
        if (create) Directory.CreateDirectory(Root(f));
        var archive = await HistoryArchiveSet.OpenAsync(Root(f), new TestEncryption(f.Key), create, catalogueFault: archiveFault);
        return await DurableConversationLedger.OpenAsync(new FileJournalStorage(path ?? f.Path, faultInjector: ledgerFault),
            new TestEncryption(f.Key), options: options ?? Options, historyArchive: archive);
    }
    private static Task<ReceiveResult> Receive(DurableConversationLedger ledger, string id, string? body = null)
        => ledger.ReceiveAsync(Identity, id, body ?? Body, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("archived_ledger_assertion_failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is LedgerException or IOException or CryptographicException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    internal static async Task<int> Run()
    {
        (string Name, Func<Task> Test)[] cases = [
            ("expanded_history_exceeds_manifest_limit_and_reopens_exactly", RoundTrip),
            ("archive_required_reader_never_falls_back_to_empty_history", Required),
            ("legacy_snapshot_moves_to_archive_only_on_next_commit", Legacy),
            ("archive_commit_failure_never_commits_inbox_or_advances_ledger", ArchiveFailure),
            ("ledger_after_flush_failure_recovers_committed_history_without_duplicate", LedgerFailure),
            ("missing_volume_never_recreates_history_or_changes_ledger", Missing),
            ("whole_snapshot_digest_rejects_invalid_manifest", Manifest),
            ("expanded_limit_rejects_before_archive_mutation", ExpandedLimit)
            ,("archived_dispatch_and_send_recover_unknown_without_replay", UnknownEffects)
            ,("manifest_limit_does_not_publish_uncommitted_history", ManifestLimit)
        ];
        int failed = 0;
        foreach (var (name, test) in cases)
        {
            try { await test(); Console.WriteLine("PASS archived_ledger/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL archived_ledger/" + name + ": " + error); }
        }
        Console.WriteLine($"ARCHIVED LEDGER TOTAL {cases.Length}; PASSED {cases.Length - failed}; FAILED {failed}"); return failed;
    }
    private static async Task RoundTrip()
    {
        Check(new LedgerCapacitySnapshot(10, 1000, 0, false, 800, 1000).NearLimit
            && !new LedgerCapacitySnapshot(10, 1000, 0, false, 799, 1000).NearLimit);
        await using var f = new Fixture(); Guid eventId;
        await using (var ledger = await Open(f, true))
        {
            await Receive(ledger, "one"); var snapshot = await ledger.ReadConversationDiagnosticsAsync(Identity);
            Check(snapshot.Capacity.LogicalSnapshotBytes > Options.MaximumSnapshotBytes
                && snapshot.Capacity.StoredSnapshotBytes < Options.MaximumSnapshotBytes
                && snapshot.Capacity.MaximumSnapshotBytes == Options.MaximumArchivedSnapshotBytes
                && snapshot.Capacity.MaximumStoredSnapshotBytes == Options.MaximumSnapshotBytes);
            eventId = snapshot.Conversation.Events.Single().Id;
            await Receive(ledger, "two", "另一条消息");
        }
        await using var reopened = await Open(f); await Receive(reopened, "one");
        var read = await reopened.ReadConversationAsync(Identity);
        Check(read.Inbox.Length == 2 && read.Inbox[0].Body == Body && read.Events.Single(e => e.Id == eventId).Body == Body);
    }
    private static async Task Required()
    {
        await using var f = new Fixture();
        await using (var ledger = await Open(f, true)) await Receive(ledger, "one");
        var before = await File.ReadAllBytesAsync(f.Path);
        await Reject(async () => { await using var unused = await f.Open(); });
        var after = await File.ReadAllBytesAsync(f.Path); Check(before.SequenceEqual(after));
        await using var reopened = await Open(f); Check((await reopened.ReadConversationAsync(Identity)).Inbox.Length == 1);
    }
    private static async Task Legacy()
    {
        await using var f = new Fixture();
        await using (var old = await f.Open()) await Receive(old, "legacy", "旧消息");
        var before = await File.ReadAllBytesAsync(f.Path);
        await using (var migrated = await Open(f, true))
        {
            Check(before.SequenceEqual(ReadShared(f.Path))); await Receive(migrated, "new");
        }
        await using var reopened = await Open(f);
        Check((await reopened.ReadConversationAsync(Identity)).Inbox.Select(x => x.SourceMessageId).SequenceEqual(new[] { "legacy", "new" }));
    }
    private static byte[] ReadShared(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var memory = new MemoryStream(); file.CopyTo(memory); return memory.ToArray(); }
    private static async Task ArchiveFailure()
    {
        await using var f = new Fixture(); var faults = new FaultInjector();
        await using (var ledger = await Open(f, true, archiveFault: faults))
        {
            faults.Stage = JournalWriteStage.AfterFlush; await Reject(() => Receive(ledger, "one"));
            var snapshot = await ledger.ReadConversationDiagnosticsAsync(Identity);
            Check(snapshot.Capacity.JournalSequence == 0 && snapshot.Conversation.Inbox.IsEmpty && snapshot.Capacity.IsReadOnly);
        }
        await using var reopened = await Open(f); Check((await reopened.ReadConversationAsync(Identity)).Inbox.IsEmpty);
        await Receive(reopened, "one"); Check((await reopened.ReadConversationAsync(Identity)).Inbox.Length == 1);
    }
    private static async Task LedgerFailure()
    {
        await using var f = new Fixture(); var faults = new FaultInjector();
        await using (var ledger = await Open(f, true, ledgerFault: faults))
        {
            faults.Stage = JournalWriteStage.AfterFlush; await Reject(() => Receive(ledger, "one"));
            Check((await ledger.ReadConversationAsync(Identity)).IsReadOnly);
        }
        await using var reopened = await Open(f); await Receive(reopened, "one");
        Check((await reopened.ReadConversationAsync(Identity)).Inbox.Single().Body == Body);
    }
    private static async Task Missing()
    {
        await using var f = new Fixture();
        await using (var ledger = await Open(f, true)) await Receive(ledger, "one");
        var before = await File.ReadAllBytesAsync(f.Path); string volume = Directory.GetFiles(Root(f), "*.volume").Single();
        File.Move(volume, volume + ".retained"); await Reject(async () => { await using var unused = await Open(f); });
        var after = await File.ReadAllBytesAsync(f.Path); Check(!File.Exists(volume) && before.SequenceEqual(after));
    }
    private static async Task Manifest()
    {
        await using var f = new Fixture();
        await using (var ledger = await Open(f, true)) await Receive(ledger, "one");
        var protection = new TestEncryption(f.Key); byte[] purpose = Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protection.Scheme}/1");
        byte[] original;
        await using (var storage = new FileJournalStorage(f.Path)) original = protection.Unprotect((await storage.ReadLatestAsync())!.Payload, purpose);
        var parsed = JsonNode.Parse(original)!; CryptographicOperations.ZeroMemory(original); parsed["sha256"] = new string('0', 64);
        byte[] altered = Encoding.UTF8.GetBytes(parsed.ToJsonString());
        await using (var storage = new FileJournalStorage(f.Path + ".bad")) await storage.AppendAsync(0, protection.Protect(altered, purpose));
        CryptographicOperations.ZeroMemory(altered);
        await Reject(async () => { await using var unused = await Open(f, path: f.Path + ".bad"); });
    }
    private static async Task ExpandedLimit()
    {
        await using var f = new Fixture(); await using var ledger = await Open(f, true,
            options: Options with { MaximumArchivedSnapshotBytes = 8192 });
        await Reject(() => Receive(ledger, "large"));
        Check(Directory.GetFiles(Root(f), "*.volume").Length == 0 && !(await ledger.ReadConversationAsync(Identity)).IsReadOnly);
        await Receive(ledger, "small", "still works");
    }
    private static async Task UnknownEffects()
    {
        foreach (bool operation in new[] { true, false })
        {
            await using var f = new Fixture(); Guid id;
            await using (var ledger = await Open(f, true))
            {
                await Receive(ledger, "m1", "please continue test");
                if (operation)
                {
                    var authorization = await ledger.AuthorizeCurrentUserRequestAsync(Identity, "m1", "send_to_task", "local/thread/turn",
                        """{"text":"continue test"}""", "continue test", new("local", "thread", "turn"));
                    var queued = await ledger.QueueOperationAsync(Identity, authorization.Id, "synthetic-operation");
                    id = queued.Id; await ledger.BeginOperationDispatchAsync(Identity, id);
                }
                else
                {
                    var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
                    await ledger.PrepareReplyAsync(lease, ["synthetic reply"]);
                    id = (await ledger.EnqueuePreparedReplyAsync(lease)).Single().Id;
                    await ledger.BeginOutboxSendAsync(Identity, id);
                }
            }
            await using var reopened = await Open(f); var plan = await reopened.GetRecoveryPlanAsync(Identity);
            if (operation)
            {
                Check(plan.QueuedOperations.IsEmpty && plan.OperationsToReconcile.Single().Stage == OperationStage.Unknown);
                await Reject(() => reopened.BeginOperationDispatchAsync(Identity, id));
            }
            else
            {
                Check(plan.PendingOutbox.IsEmpty && plan.OutboxToReconcile.Single().Stage == OutboxStage.Unknown);
                await Reject(() => reopened.BeginOutboxSendAsync(Identity, id));
            }
        }
    }
    private static async Task ManifestLimit()
    {
        await using var f = new Fixture(); var options = Options with { MaximumSnapshotBytes = 1024 }; int retained = 0;
        await using (var ledger = await Open(f, true, options: options))
        {
            bool rejected = false;
            for (int i = 0; i < 5 && !rejected; i++)
            {
                var before = await ledger.ReadConversationDiagnosticsAsync(Identity);
                try { await Receive(ledger, "item-" + i); retained++; }
                catch (LedgerException error) when (error.Code == LedgerErrors.CapacityExceeded && error.Message.Contains("manifest"))
                {
                    rejected = true; var after = await ledger.ReadConversationDiagnosticsAsync(Identity);
                    // Rejected manifest publication can leave unreferenced archive chunks.
                    // Compare committed logical capacity, not physical archive byte growth.
                    Check(before.Capacity with { ArchiveStorage = null } == after.Capacity with { ArchiveStorage = null }
                        && after.Conversation.Inbox.Length == retained);
                }
            }
            Check(rejected && retained > 0);
        }
        await using var reopened = await Open(f, options: options);
        Check((await reopened.ReadConversationAsync(Identity)).Inbox.Length == retained);
    }
}
