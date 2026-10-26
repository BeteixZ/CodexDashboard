using Assistant.Core;

internal static class OutboxAttemptTests
{
    private static readonly ConversationIdentity Identity = new("attempt-test", "original-binding", "main");
    private static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("definite_receipt_and_timeout_commute_without_duplicate_effects", ReceiptRace),
        ("wrong_identity_attempt_or_input_revision_cannot_commit", Scope),
        ("old_attempt_cannot_overwrite_explicit_rejected_retry", RetryFence),
        ("contradictory_definite_result_cannot_replace_original", Contradiction),
        ("pending_or_never_attempted_message_cannot_gain_receipt", NotDispatched),
        ("exact_receipt_survives_reopen_without_retry_permission", Reopen),
        ("receipt_write_failure_preserves_actual_committed_prefix", WriteFailure)
    ];

    internal static async Task<int> Run()
    {
        int failures = 0;
        foreach (var (name, run) in Cases)
        {
            try { await run(); Console.WriteLine("PASS outbox-attempt/" + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL outbox-attempt/" + name + ": " + error); }
        }
        Console.WriteLine($"OUTBOX ATTEMPT TOTAL {Cases.Length}; PASSED {Cases.Length - failures}; FAILED {failures}");
        return failures;
    }

    private static async Task<OutboxMessage> Prepare(DurableConversationLedger ledger)
    {
        await ledger.ReceiveAsync(Identity, "original-input", "read status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var generation = await ledger.BeginGenerationAsync(Identity, ["original-input"]);
        await ledger.PrepareReplyAsync(generation, ["original reply"]);
        return (await ledger.EnqueuePreparedReplyAsync(generation)).Single();
    }
    private static OutboxAttemptLease Lease(OutboxMessage sending) =>
        new(sending.Identity, sending.Id, sending.SendAttempts, sending.AttemptTrustedInputRevision);
    private static Task<OutboxMessage> Outcome(DurableConversationLedger ledger, OutboxAttemptLease lease,
        OutboxStage result, string receipt = "exact original receipt") =>
        ledger.RecordOutboxAttemptOutcomeAsync(lease, result, receipt,
            result == OutboxStage.Rejected ? OutboxRejectionReason.ContextExpired : OutboxRejectionReason.None);

    private static async Task ReceiptRace()
    {
        foreach (var definite in new[] { OutboxStage.Accepted, OutboxStage.Rejected })
        foreach (bool timeoutFirst in new[] { false, true })
        {
            await using var f = new Fixture(); var ledger = await f.Open();
            var pending = await Prepare(ledger); var lease = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
            if (timeoutFirst) await Outcome(ledger, lease, OutboxStage.Unknown, "wait ended");
            var saved = await Outcome(ledger, lease, definite);
            var before = ReadJournal(f.Path);
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                Outcome(ledger, lease, i % 2 == 0 ? OutboxStage.Unknown : definite)));
            Check(results.All(x => x == saved));
            Check(before.SequenceEqual(ReadJournal(f.Path)));
            var state = await ledger.ReadConversationAsync(Identity);
            Check(state.Outbox.Single().SendAttempts == 1 && state.Authorizations.IsEmpty && state.Operations.IsEmpty);
            Check(state.Events.Count(x => x.Source == ConversationSource.Assistant && x.RelatedOutboxId == pending.Id)
                == (definite == OutboxStage.Accepted ? 1 : 0));
        }
    }
    private static async Task Scope()
    {
        await using var f = new Fixture(); var ledger = await f.Open();
        var pending = await Prepare(ledger); var lease = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
        var before = ReadJournal(f.Path);
        foreach (var invalid in new[]
        {
            lease with { Identity = Identity with { BindingId = "different-binding" } },
            lease with { OutboxId = Guid.NewGuid() }, lease with { Number = lease.Number + 1 },
            lease with { TrustedInputRevision = lease.TrustedInputRevision + 1 },
            lease with { TrustedInputRevision = null }, lease with { Number = 0 }
        }) await Refused(() => Outcome(ledger, invalid, OutboxStage.Accepted));
        Check(before.SequenceEqual(ReadJournal(f.Path)));
        Check((await ledger.ReadConversationAsync(Identity)).Outbox.Single().Stage == OutboxStage.Sending);
    }
    private static async Task RetryFence()
    {
        await using var f = new Fixture(); var ledger = await f.Open();
        var pending = await Prepare(ledger); var first = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
        await Outcome(ledger, first, OutboxStage.Rejected);
        await ledger.RetryRejectedOutboxAsync(Identity, pending.Id);
        var second = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
        Check(second.Number == first.Number + 1);
        await Outcome(ledger, second, OutboxStage.Unknown);
        var before = ReadJournal(f.Path);
        await Refused(() => Outcome(ledger, first, OutboxStage.Accepted));
        Check(before.SequenceEqual(ReadJournal(f.Path)));
        Check((await Outcome(ledger, second, OutboxStage.Accepted)).SendAttempts == 2);
    }
    private static async Task Contradiction()
    {
        foreach (var initial in new[] { OutboxStage.Accepted, OutboxStage.Rejected })
        {
            await using var f = new Fixture(); var ledger = await f.Open();
            var pending = await Prepare(ledger); var lease = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
            var saved = await Outcome(ledger, lease, initial); var before = ReadJournal(f.Path);
            await Refused(() => Outcome(ledger, lease, initial == OutboxStage.Accepted ? OutboxStage.Rejected : OutboxStage.Accepted));
            Check(before.SequenceEqual(ReadJournal(f.Path)));
            Check((await ledger.ReadConversationAsync(Identity)).Outbox.Single() == saved);
        }
    }
    private static async Task NotDispatched()
    {
        await using var f = new Fixture(); var ledger = await f.Open(); var pending = await Prepare(ledger);
        await Refused(() => Outcome(ledger, Lease(pending), OutboxStage.Accepted));
        await Refused(() => Outcome(ledger, Lease(pending) with { Number = 1 }, OutboxStage.Accepted));
        Check((await ledger.ReadConversationAsync(Identity)).Outbox.Single().Stage == OutboxStage.Pending);
    }
    private static async Task Reopen()
    {
        await using var f = new Fixture(); var ledger = await f.Open(); var pending = await Prepare(ledger);
        var lease = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
        await ledger.DisposeAsync(); ledger = await f.Open();
        Check((await ledger.GetRecoveryPlanAsync(Identity)).OutboxToReconcile.Single().Id == pending.Id);
        await Refused(() => ledger.BeginOutboxSendAsync(Identity, pending.Id));
        await Outcome(ledger, lease, OutboxStage.Accepted);
        await ledger.DisposeAsync(); ledger = await f.Open();
        Check((await ledger.ReadConversationAsync(Identity)).Outbox.Single().Stage == OutboxStage.Accepted);
        Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
    }
    private static async Task WriteFailure()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var f = new Fixture(); var fault = new FaultInjector(); var ledger = await f.Open(faults: fault);
            var pending = await Prepare(ledger); var lease = Lease(await ledger.BeginOutboxSendAsync(Identity, pending.Id));
            await Outcome(ledger, lease, OutboxStage.Unknown); fault.Stage = stage;
            bool failed = false;
            try { await Outcome(ledger, lease, OutboxStage.Accepted); } catch (InjectedFailure) { failed = true; }
            Check(failed); await ledger.DisposeAsync(); ledger = await f.Open();
            Check((await ledger.ReadConversationAsync(Identity)).Outbox.Single().Stage ==
                (stage == JournalWriteStage.AfterFlush ? OutboxStage.Accepted : OutboxStage.Unknown));
            Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
        }
    }
    private static byte[] ReadJournal(string path)
    {
        // The real ledger keeps its writer open. Observe committed bytes without
        // asking Windows to deny that already-existing writer's access.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }
    private static async Task Refused(Func<Task> action)
    {
        try { await action(); } catch (LedgerException) { return; }
        throw new InvalidOperationException("Expected the original attempt fence to reject this result.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Outbox attempt assertion failed."); }
}
