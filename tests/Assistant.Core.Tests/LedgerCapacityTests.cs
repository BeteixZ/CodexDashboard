using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Core;

internal static class LedgerCapacityTests
{
    private static readonly ConversationIdentity Identity = new("capacity-owner", "capacity-binding", "main");
    private static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("read_only_measurement_creates_no_records_or_writes", ReadOnly),
        ("reported_bytes_equal_actual_committed_utf8_payload_and_reopen", ExactSize),
        ("whole_ledger_capacity_does_not_leak_other_identity_records", WholeLedger),
        ("capacity_rejection_preserves_previous_size_and_records", RejectedWrite),
        ("storage_capacity_rejection_allows_smaller_transaction_without_reopen", StorageRejectedWrite),
        ("capacity_error_after_flush_remains_unknown_until_reopen", CapacityAfterFlush),
        ("uncertain_write_reports_validated_prefix_until_real_reopen", UncertainWrite),
        ("concurrent_diagnostic_count_and_sequence_use_one_snapshot", ConcurrentRead),
        ("near_limit_threshold_is_precise_and_cancel_dispose_are_respected", Boundaries)
    ];

    internal static async Task<int> Run()
    {
        int failures = 0;
        foreach (var (name, run) in Cases)
        {
            try { await run(); Console.WriteLine("PASS capacity/" + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL capacity/" + name + ": " + error); }
        }
        Console.WriteLine($"CAPACITY TOTAL {Cases.Length}; PASSED {Cases.Length - failures}; FAILED {failures}");
        return failures;
    }
    private static Task<ReceiveResult> Receive(DurableConversationLedger ledger, string id, string body = "合成消息，仅用于测量")
        => ledger.ReceiveAsync(Identity, id, body, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
    private static byte[] ReadBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream(); file.CopyTo(output); return output.ToArray();
    }
    private static async Task StorageRejectedWrite()
    {
        await using var fixture = new Fixture();
        await using var ledger = await DurableConversationLedger.OpenAsync(
            new FileJournalStorage(fixture.Path, maximumFrameBytes: 8192), new TestEncryption(fixture.Key));
        await Receive(ledger, "first", "small");
        var before = await ledger.ReadConversationDiagnosticsAsync(Identity);
        byte[] bytes = ReadBytes(fixture.Path);
        try { await Receive(ledger, "large", new string('x', 9000)); throw new InvalidOperationException("capacity_not_rejected"); }
        catch (LedgerException error) when (error.Code == LedgerErrors.CapacityExceeded) { }
        var after = await ledger.ReadConversationDiagnosticsAsync(Identity);
        Check(before.Capacity == after.Capacity && !after.Conversation.IsReadOnly && bytes.SequenceEqual(ReadBytes(fixture.Path)));
        await Receive(ledger, "second", "small");
        Check((await ledger.ReadConversationAsync(Identity)).Inbox.Length == 2);
        await ledger.DisposeAsync();
        await using var reopened = await fixture.Open();
        await Receive(reopened, "first", "small");
        Check((await reopened.ReadConversationAsync(Identity)).Inbox.Length == 2);
    }
    private sealed class CapacityFault : IJournalFaultInjector
    {
        public void At(JournalWriteStage stage, long sequence)
        { if (stage == JournalWriteStage.AfterFlush) throw new LedgerException(LedgerErrors.CapacityExceeded, "injected_after_commit"); }
    }
    private static async Task CapacityAfterFlush()
    {
        await using var fixture = new Fixture();
        await using var ledger = await DurableConversationLedger.OpenAsync(
            new FileJournalStorage(fixture.Path, faultInjector: new CapacityFault()), new TestEncryption(fixture.Key));
        try { await Receive(ledger, "committed"); throw new InvalidOperationException("fault_not_injected"); }
        catch (LedgerException error) when (error.Code == LedgerErrors.CapacityExceeded) { }
        Check((await ledger.ReadConversationDiagnosticsAsync(Identity)).Capacity.IsReadOnly);
        await ledger.DisposeAsync();
        await using var reopened = await fixture.Open();
        Check((await reopened.ReadConversationAsync(Identity)).Inbox.Single().SourceMessageId == "committed");
    }

    private static async Task ReadOnly()
    {
        await using var fixture = new Fixture(); var ledger = await fixture.Open();
        byte[] before = ReadBytes(fixture.Path);
        var a = await ledger.ReadConversationDiagnosticsAsync(Identity);
        var b = await ledger.ReadConversationDiagnosticsAsync(Identity);
        Check(a.Capacity == b.Capacity && a.Capacity.LogicalSnapshotBytes > 0
            && a.Capacity.MaximumSnapshotBytes == 8 * 1024 * 1024 && a.Capacity.JournalSequence == 0
            && !a.Capacity.IsReadOnly && !a.Capacity.NearLimit && a.Conversation.Inbox.IsEmpty
            && (await ledger.ListConversationIdentitiesAsync()).IsEmpty
            && before.SequenceEqual(ReadBytes(fixture.Path)));
    }
    private static async Task ExactSize()
    {
        await using var fixture = new Fixture(); var ledger = await fixture.Open();
        await Receive(ledger, "one", "中文🙂\n带引号\"和反斜线\\的原文");
        var before = await ledger.ReadConversationDiagnosticsAsync(Identity);
        await ledger.DisposeAsync();
        int actual;
        await using (var storage = new FileJournalStorage(fixture.Path))
        {
            var record = await storage.ReadLatestAsync() ?? throw new InvalidOperationException("record_missing");
            var protector = new TestEncryption(fixture.Key);
            byte[] plaintext = protector.Unprotect(record.Payload,
                Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{record.Sequence}"));
            try { actual = plaintext.Length; }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        ledger = await fixture.Open();
        var reopened = await ledger.ReadConversationDiagnosticsAsync(Identity);
        Check(actual == before.Capacity.LogicalSnapshotBytes && before.Capacity == reopened.Capacity
            && reopened.Conversation.Inbox.Length == 1);
    }
    private static async Task WholeLedger()
    {
        await using var fixture = new Fixture(); var ledger = await fixture.Open();
        await Receive(ledger, "first");
        var a = await ledger.ReadConversationDiagnosticsAsync(Identity);
        var other = new ConversationIdentity("PRIVATE_OTHER_OWNER", "PRIVATE_OTHER_BINDING", "PRIVATE_OTHER_CONVERSATION");
        await ledger.ReceiveAsync(other, "PRIVATE_OTHER_MESSAGE", "PRIVATE_OTHER_BODY",
            MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var b = await ledger.ReadConversationDiagnosticsAsync(Identity);
        Check(b.Conversation.Inbox.Length == 1 && b.Capacity.JournalSequence == a.Capacity.JournalSequence + 1
            && b.Capacity.LogicalSnapshotBytes > a.Capacity.LogicalSnapshotBytes
            && !JsonSerializer.Serialize(b).Contains("PRIVATE_OTHER_", StringComparison.Ordinal));
    }
    private static async Task RejectedWrite()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open(options: new() { MaximumSnapshotBytes = 2048 });
        var before = await ledger.ReadConversationDiagnosticsAsync(Identity);
        byte[] bytes = ReadBytes(fixture.Path);
        try { await Receive(ledger, "too-large", new string('x', 4000)); throw new InvalidOperationException("capacity_not_rejected"); }
        catch (LedgerException error) when (error.Code == LedgerErrors.CapacityExceeded) { }
        var after = await ledger.ReadConversationDiagnosticsAsync(Identity);
        Check(after.Capacity == before.Capacity && after.Conversation.Inbox.IsEmpty
            && !after.Conversation.IsReadOnly && bytes.SequenceEqual(ReadBytes(fixture.Path)));
    }
    private static async Task UncertainWrite()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var fixture = new Fixture(); var faults = new FaultInjector();
            var ledger = await fixture.Open(faults);
            await Receive(ledger, "first");
            var before = await ledger.ReadConversationDiagnosticsAsync(Identity);
            faults.Stage = stage;
            try { await Receive(ledger, "second"); throw new InvalidOperationException("fault_not_injected"); }
            catch (InjectedFailure) { }
            var uncertain = await ledger.ReadConversationDiagnosticsAsync(Identity);
            Check(uncertain.Capacity == before.Capacity with { IsReadOnly = true }
                && uncertain.Conversation.IsReadOnly && uncertain.Conversation.Inbox.Length == 1);
            await ledger.DisposeAsync();
            ledger = await fixture.Open();
            var reopened = await ledger.ReadConversationDiagnosticsAsync(Identity);
            Check(!reopened.Capacity.IsReadOnly && reopened.Conversation.Inbox.Length
                == (stage == JournalWriteStage.AfterFlush ? 2 : 1));
            Check(stage == JournalWriteStage.AfterFlush
                ? reopened.Capacity.LogicalSnapshotBytes > before.Capacity.LogicalSnapshotBytes
                    && reopened.Capacity.JournalSequence == before.Capacity.JournalSequence + 1
                : reopened.Capacity == before.Capacity);
        }
    }
    private static async Task ConcurrentRead()
    {
        await using var fixture = new Fixture(); var ledger = await fixture.Open();
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            await Receive(ledger, "item-" + i);
            var snapshot = await ledger.ReadConversationDiagnosticsAsync(Identity);
            Check(snapshot.Capacity.JournalSequence == snapshot.Conversation.Inbox.Length
                && snapshot.Conversation.Revision == snapshot.Conversation.Inbox.Length);
        });
        await Task.WhenAll(tasks);
        Check((await ledger.ReadConversationDiagnosticsAsync(Identity)).Conversation.Inbox.Length == 20);
    }
    private static async Task Boundaries()
    {
        Check(!new LedgerCapacitySnapshot(799, 1000, 0, false).NearLimit
            && new LedgerCapacitySnapshot(800, 1000, 0, false).NearLimit
            && new LedgerCapacitySnapshot(1001, 1000, 0, true).NearLimit);
        await using var fixture = new Fixture(); var ledger = await fixture.Open();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await ledger.ReadConversationDiagnosticsAsync(Identity, cancelled.Token); throw new InvalidOperationException("cancellation_ignored"); }
        catch (OperationCanceledException) { }
        await ledger.DisposeAsync();
        try { await ledger.ReadConversationDiagnosticsAsync(Identity); throw new InvalidOperationException("dispose_ignored"); }
        catch (ObjectDisposedException) { }
    }
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Capacity assertion failed.");
    }
}
