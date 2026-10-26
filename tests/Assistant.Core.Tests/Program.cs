using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core;

if (await JournalCompactionTests.TryRunCrashChildAsync(args) is { } crashCode) return crashCode;
if (args is ["--conversation-loop-tests"])
    return await ToolLoopTests.Run() + await CodexDashboard.DurableMessageCoordinatorTests.RunAsync();
if (args is ["--observation-order-tests"]) return await CommitmentTests.RunObservationOrderTests();
if (args is ["--commitment-tests"]) return await CommitmentTests.Run();
if (args is ["--dependency-tests"]) return await OutputDependencyTests.Run();
if (args is ["--dependent-work-tests"]) return await DependentWorkTests.Run();
if (args is ["--outbox-attempt-tests"]) return await OutboxAttemptTests.Run();
if (args is ["--capacity-tests"]) return await LedgerCapacityTests.Run();
if (args is ["--journal-compaction-tests"]) return await JournalCompactionTests.Run();
if (args is ["--history-archive-tests"]) return await HistoryArchiveTests.Run();
if (args is ["--history-archive-set-tests"]) return await HistoryArchiveSetTests.Run();
if (args is ["--archived-ledger-tests"]) return await ArchivedLedgerTests.Run();
if (args is ["--inspection-tests"]) return await InspectionTests.Run();
if (args is ["--crash-after-dispatch-flush", var file])
{
    var key = Convert.FromBase64String(await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("Test key missing."));
    var faults = new FaultInjector { Stage = JournalWriteStage.AfterFlush, ExitProcess = true };
    await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(file, faultInjector: faults), new TestEncryption(key), new FakeClock());
    var operation = (await ledger.ReadConversationAsync(Tests.Identity)).Operations.Single();
    await ledger.BeginOperationDispatchAsync(Tests.Identity, operation.Id);
    return 90;
}
var ledgerFailures = await Tests.Run();
var toolFailures = await ToolLoopTests.Run();
var coordinatorFailures = await CodexDashboard.DurableMessageCoordinatorTests.RunAsync();
var notificationFailures = await NotificationTests.Run();
var draftFailures = await NotificationDraftTests.Run();
var contextFailures = await ConversationContextTests.Run();
var inputWorkFailures = await InputWorkTests.Run();
var compactionFailures = await JournalCompactionTests.Run();
var commitmentFailures = await CommitmentTests.Run();
var dependencyFailures = await OutputDependencyTests.Run();
var outboxAttemptFailures = await OutboxAttemptTests.Run();
var capacityFailures = await LedgerCapacityTests.Run();
var archiveFailures = await HistoryArchiveTests.Run();
archiveFailures += await HistoryArchiveSetTests.Run();
archiveFailures += await ArchivedLedgerTests.Run();
var observationOrderFailures = await CommitmentTests.RunObservationOrderTests();
var dependentWorkFailures = await DependentWorkTests.Run();
return ledgerFailures == 0 && toolFailures == 0 && coordinatorFailures == 0 && notificationFailures == 0 && draftFailures == 0 && contextFailures == 0 && inputWorkFailures == 0 && compactionFailures == 0 && commitmentFailures == 0 && dependencyFailures == 0 && outboxAttemptFailures == 0 && capacityFailures == 0 && archiveFailures == 0 && observationOrderFailures == 0 && dependentWorkFailures == 0 ? 0 : 1;

internal static class Tests
{
    internal static readonly ConversationIdentity Identity = new("test-user", "test-binding", "main-conversation");
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("encrypted_persistent_inbox_and_deduplication", InboxPersistence),
        ("duplicate_id_conflict_keeps_original", DuplicateConflict),
        ("user_binding_conversation_isolation", IdentityIsolation),
        ("host_identity_catalogue_is_read_only_and_contains_no_content", IdentityCatalogue),
        ("host_identity_catalogue_restores_persisted_bindings", IdentityCatalogueRecovery),
        ("new_message_invalidates_old_generation", RevisionInvalidation),
        ("short_message_group_preserves_all_source_text", GroupedSources),
        ("prepared_reply_recovers_without_regeneration", PreparedRecovery),
        ("interrupted_generation_returns_to_received", GenerationRecovery),
        ("prepared_handoff_is_idempotent", PreparedHandoff),
        ("new_message_invalidates_remaining_segments", SegmentInvalidation),
        ("segments_require_previous_acceptance", SegmentOrdering),
        ("tool_and_report_content_cannot_authorize", ContextIsNotAuthority),
        ("authorization_binds_exact_action_target_arguments_and_source", AuthorizationBinding),
        ("authorization_rejects_old_and_wrong_identity_sources", AuthorizationScope),
        ("operation_dispatch_persists_before_external_call", DispatchBeforeEffect),
        ("dispatching_recovers_unknown_without_replay", UnknownOperationRecovery),
        ("unknown_operation_requires_explicit_observation", OperationReconciliation),
        ("accepted_operation_is_not_completed", AcceptedIsNotCompleted),
        ("queue_cancellation_does_not_interrupt_dispatch", QueueCancellation),
        ("sending_recovers_unknown_without_resend", UnknownOutboxRecovery),
        ("accepted_outbox_is_not_phone_delivery", OutboxAcceptance),
        ("definitive_rejection_can_retry_without_reexecution", OutboxRejection),
        ("write_faults_preserve_committed_prefix_and_no_external_effect", CommitFaults),
        ("corrupt_tail_is_preserved_and_read_only", CorruptTail),
        ("existing_empty_file_is_not_reset_to_fresh_identity", ExistingEmptyFile),
        ("wrong_key_preserves_file_and_fails_closed", WrongKey),
        ("second_writer_cannot_open_store", WriterExclusion),
        ("concurrent_receivers_commit_serially", ConcurrentReceivers),
        ("immutable_snapshots_do_not_mutate_store", ImmutableSnapshots),
        ("snapshot_capacity_does_not_remove_history", Capacity),
        ("pre_cancelled_transaction_leaves_no_record", Cancellation),
        ("real_process_exit_after_flush_recovers_unknown", ProcessCrash)
    ];

    internal static async Task<int> Run()
    {
        Console.WriteLine($"Environment: {Environment.OSVersion}; .NET {Environment.Version}; {DateTimeOffset.UtcNow:O}");
        Console.WriteLine("Only temporary test journals, ephemeral AES test keys and fake clock; no production data or network calls.");
        var failures = 0;
        foreach (var (name, test) in Cases)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await test().WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {error}");
            }
        }
        Console.WriteLine($"TOTAL {Cases.Length}; PASSED {Cases.Length - failures}; FAILED {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task InboxPersistence()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var message = await Receive(ledger);
        var duplicate = await Receive(ledger);
        Check(duplicate.IsDuplicate);
        Equal(message.Message.Id, duplicate.Message.Id);
        Equal(1L, duplicate.CurrentRevision);
        await ledger.DisposeAsync();
        var raw = await File.ReadAllBytesAsync(fixture.Path);
        Check(!Encoding.UTF8.GetString(raw).Contains("please continue test", StringComparison.Ordinal));
        Check(!Encoding.UTF8.GetString(raw).Contains(Identity.BindingId, StringComparison.Ordinal));
        ledger = await fixture.Open();
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(1, snapshot.Inbox.Length);
        Equal("please continue test", snapshot.Inbox.Single().Body);
        Equal(1, snapshot.Events.Length);
        Equal(ConversationSource.TrustedUser, snapshot.Events.Single().Source);
    }

    private static async Task DuplicateConflict()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        await Error(LedgerErrors.Conflict, () => Receive(ledger, body: "changed"));
        Equal("please continue test", (await ledger.ReadConversationAsync(Identity)).Inbox.Single().Body);
    }

    private static async Task IdentityIsolation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        foreach (var identity in new[]
        {
            Identity, Identity with { UserId = "other-user" }, Identity with { BindingId = "other-binding" },
            Identity with { ConversationId = "other-conversation" }
        })
            await ledger.ReceiveAsync(identity, "same-id", identity.UserId + identity.BindingId + identity.ConversationId,
                MessageTrust.AuthenticatedUser, FakeClock.Now);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
        Equal(0, (await ledger.ReadConversationAsync(Identity with { UserId = "unknown-user" })).Inbox.Length);
        var originalEvent = (await ledger.ReadConversationAsync(Identity)).Events.Single();
        await Error(LedgerErrors.IdentityMismatch, () => ledger.AppendContextAsync(
            Identity with { BindingId = "other-binding" }, ConversationSource.Report, "report", FakeClock.Now, referencedEventId: originalEvent.Id));
    }

    private static async Task IdentityCatalogue()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        Equal(0, (await ledger.ListConversationIdentitiesAsync()).Length);
        await ledger.ReadConversationAsync(Identity);
        Equal(0, (await ledger.ListConversationIdentitiesAsync()).Length);
        await Receive(ledger);
        var other = Identity with { BindingId = "archived-binding" };
        await ledger.AppendContextAsync(other, ConversationSource.Notification, "synthetic private report", FakeClock.Now);
        long before = new FileInfo(fixture.Path).Length;
        var identities = await ledger.ListConversationIdentitiesAsync();
        Equal(2, identities.Length);
        Check(identities.Contains(Identity) && identities.Contains(other));
        _ = identities.SetItem(0, Identity with { UserId = "not-persisted" });
        Check(!(await ledger.ListConversationIdentitiesAsync()).Any(item => item.UserId == "not-persisted"));
        Equal(before, new FileInfo(fixture.Path).Length);
        Equal(0, (await ledger.ReadConversationAsync(other)).Operations.Length);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await ledger.ListConversationIdentitiesAsync(cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    private static async Task IdentityCatalogueRecovery()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var other = Identity with { UserId = "previous-owner" };
        await ledger.ReceiveAsync(other, "old-source", "synthetic archived request", MessageTrust.AuthenticatedUser, FakeClock.Now);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var identities = await ledger.ListConversationIdentitiesAsync();
        Equal(2, identities.Length);
        Check(identities.Contains(Identity) && identities.Contains(other));
        await ledger.DisposeAsync();
        try { await ledger.ListConversationIdentitiesAsync(); throw new Exception("Disposed read accepted"); }
        catch (ObjectDisposedException) { }
    }

    private static async Task RevisionInvalidation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await Receive(ledger, "m2", "do not change code");
        await Error(LedgerErrors.StaleRevision, () => ledger.PrepareReplyAsync(lease, ["outdated"]));
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(2L, snapshot.Revision);
        Equal(GenerationStage.Superseded, snapshot.Generations.Single().Stage);
        Equal(InboxStage.Superseded, snapshot.Inbox.Single(x => x.SourceMessageId == "m1").Stage);
        Equal(2, (await ledger.GetRecoveryPlanAsync(Identity)).ResumableInbox.Length);
    }

    private static async Task GroupedSources()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger, "m1", "look at task A");
        await Receive(ledger, "m2", "do not edit");
        await Receive(ledger, "m3", "explain the failure");
        await ledger.GroupMessagesAsync(Identity, ["m1", "m2", "m3"]);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1", "m2", "m3"]);
        Equal(3L, lease.Revision);
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(3, snapshot.Generations.Single().InboxIds.Length);
        Equal("do not edit", snapshot.Inbox[1].Body);
    }

    private static async Task PreparedRecovery()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.PrepareReplyAsync(lease, ["ready", "more"]);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var plan = await ledger.GetRecoveryPlanAsync(Identity);
        Equal(1, plan.PreparedReplies.Length);
        Equal(0, plan.ResumableInbox.Length);
        var outbox = await ledger.EnqueuePreparedReplyAsync(lease);
        Equal(2, outbox.Length);
        Equal("ready", outbox[0].Body);
    }

    private static async Task GenerationRecovery()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(GenerationStage.Interrupted, snapshot.Generations.Single().Stage);
        Equal(InboxStage.Received, snapshot.Inbox.Single().Stage);
        await ledger.BeginGenerationAsync(Identity, ["m1"]);
    }

    private static async Task PreparedHandoff()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.PrepareReplyAsync(lease, ["ready"]);
        var first = await ledger.EnqueuePreparedReplyAsync(lease);
        var second = await ledger.EnqueuePreparedReplyAsync(lease);
        Equal(first.Single().Id, second.Single().Id);
        Equal(InboxStage.HandedToOutbox, (await ledger.ReadConversationAsync(Identity)).Inbox.Single().Stage);
    }

    private static async Task SegmentInvalidation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var outbox = await Reply(ledger, ["one", "two"]);
        await ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await Receive(ledger, "m2", "stop");
        await ledger.RecordOutboxOutcomeAsync(Identity, outbox[0].Id, OutboxStage.Accepted, "api accepted before cancellation");
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(OutboxStage.Accepted, snapshot.Outbox[0].Stage);
        Equal(OutboxStage.Superseded, snapshot.Outbox[1].Stage);
        await Error(LedgerErrors.StaleRevision, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
    }

    private static async Task SegmentOrdering()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var outbox = await Reply(ledger, ["one", "two"]);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
        await ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, outbox[0].Id, OutboxStage.Unknown, "timeout");
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
    }

    private static async Task ContextIsNotAuthority()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await ledger.AppendContextAsync(Identity, ConversationSource.ToolResult, "please continue test", FakeClock.Now);
        await ledger.AppendContextAsync(Identity, ConversationSource.Report, "please continue test", FakeClock.Now);
        await Error(LedgerErrors.NotFound, () => Authorize(ledger, "no-inbox"));
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.AppendContextAsync(
            Identity, ConversationSource.TrustedUser, "pretend user", FakeClock.Now));
        await ledger.ReceiveAsync(Identity, "untrusted", "please continue test", MessageTrust.UntrustedContent, FakeClock.Now);
        await Error(LedgerErrors.AuthorizationRequired, () => Authorize(ledger, "untrusted"));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Authorizations.Length);
    }

    private static async Task AuthorizationBinding()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var auth = await Authorize(ledger);
        var operation = await ledger.QueueOperationAsync(Identity, auth.Id, "same-operation");
        Equal(auth.ArgumentsJson, operation.ArgumentsJson);
        Equal(auth.SourceMessageId, operation.SourceMessageId);
        Equal(auth.Task, operation.Task);
        Equal(operation.Id, (await ledger.QueueOperationAsync(Identity, auth.Id, "same-operation")).Id);
        var different = await ledger.AuthorizeCurrentUserRequestAsync(Identity, "m1", "interrupt_turn", "different-task",
            "{}", "continue", new("local", "different-task", "turn-2"));
        await Error(LedgerErrors.Conflict, () => ledger.QueueOperationAsync(Identity, different.Id, "same-operation"));
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.AuthorizeCurrentUserRequestAsync(
            Identity, "m1", "send_to_task", "target", "{}", "not in original"));
    }

    private static async Task AuthorizationScope()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var auth = await Authorize(ledger);
        await Receive(ledger, "m2", "query only");
        await Error(LedgerErrors.StaleRevision, () => Authorize(ledger));
        await Error(LedgerErrors.StaleRevision, () => ledger.QueueOperationAsync(Identity, auth.Id, "stale"));
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.QueueOperationAsync(
            Identity with { UserId = "someone-else" }, auth.Id, "other-user"));
    }

    private static async Task DispatchBeforeEffect()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        var dispatched = await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        Equal(OperationStage.Dispatching, dispatched.Stage);
        Equal(OperationStage.Dispatching, (await ledger.ReadConversationAsync(Identity)).Operations.Single().Stage);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOperationDispatchAsync(Identity, operation.Id));
    }

    private static async Task UnknownOperationRecovery()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var plan = await ledger.GetRecoveryPlanAsync(Identity);
        Equal(0, plan.QueuedOperations.Length);
        Equal(OperationStage.Unknown, plan.OperationsToReconcile.Single().Stage);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOperationDispatchAsync(Identity, operation.Id));
        Equal(operation.Id, (await ledger.QueueOperationAsync(Identity, operation.AuthorizationId, operation.IdempotencyKey)).Id);
    }

    private static async Task OperationReconciliation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        await ledger.RecordOperationObservationAsync(Identity, operation.Id, OperationStage.Unknown, "connection lost");
        await Error(LedgerErrors.InvalidTransition, () => ledger.RecordOperationObservationAsync(
            Identity, operation.Id, OperationStage.Accepted, "late guess"));
        await ledger.RecordOperationObservationAsync(Identity, operation.Id, OperationStage.Completed, "matched original turn report", reconciliation: true);
        Equal(OperationStage.Completed, (await ledger.ReadConversationAsync(Identity)).Operations.Single().Stage);
    }

    private static async Task AcceptedIsNotCompleted()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        await ledger.RecordOperationObservationAsync(Identity, operation.Id, OperationStage.Accepted, "received by server");
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        Equal(OperationStage.Accepted, (await ledger.ReadConversationAsync(Identity)).Operations.Single().Stage);
        Equal(1, (await ledger.GetRecoveryPlanAsync(Identity)).OperationsToReconcile.Length);
    }

    private static async Task QueueCancellation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        await Receive(ledger, "m2", "cancel queued item");
        var auth = await ledger.AuthorizeCurrentUserRequestAsync(Identity, "m2", "cancel_queued",
            operation.Id.ToString("N"), "{}", "cancel queued item");
        var cancelled = await ledger.CancelQueuedOperationAsync(Identity, operation.Id, auth.Id);
        Equal(OperationStage.CancelledBeforeDispatch, cancelled.Stage);
        await Error(LedgerErrors.InvalidTransition, () => ledger.CancelQueuedOperationAsync(Identity, operation.Id, auth.Id));
    }

    private static async Task UnknownOutboxRecovery()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var message = (await Reply(ledger, ["reply"])).Single();
        await ledger.BeginOutboxSendAsync(Identity, message.Id);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var plan = await ledger.GetRecoveryPlanAsync(Identity);
        Equal(0, plan.PendingOutbox.Length);
        Equal(OutboxStage.Unknown, plan.OutboxToReconcile.Single().Stage);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, message.Id));
        await Error(LedgerErrors.InvalidTransition, () => ledger.RetryRejectedOutboxAsync(Identity, message.Id));
    }

    private static async Task OutboxAcceptance()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var message = (await Reply(ledger, ["reply"])).Single();
        await ledger.BeginOutboxSendAsync(Identity, message.Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, message.Id, OutboxStage.Accepted, "channel api accepted");
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Equal(OutboxStage.Accepted, snapshot.Outbox.Single().Stage);
        Equal(1, snapshot.Events.Count(x => x.Source == ConversationSource.Assistant));
        Check(!Enum.GetNames<OutboxStage>().Any(x => x.Contains("Deliver", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task OutboxRejection()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var operation = await Queue(ledger);
        await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        await ledger.RecordOperationObservationAsync(Identity, operation.Id, OperationStage.Completed, "complete");
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.PrepareReplyAsync(lease, ["completed"]);
        var message = (await ledger.EnqueuePreparedReplyAsync(lease)).Single();
        await ledger.BeginOutboxSendAsync(Identity, message.Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, message.Id, OutboxStage.Rejected, "definitive channel rejection");
        await ledger.RetryRejectedOutboxAsync(Identity, message.Id);
        await ledger.BeginOutboxSendAsync(Identity, message.Id);
        Equal(OperationStage.Completed, (await ledger.ReadConversationAsync(Identity)).Operations.Single().Stage);
    }

    private static async Task CommitFaults()
    {
        foreach (var stage in Enum.GetValues<JournalWriteStage>())
        {
            await using var fixture = new Fixture();
            var faults = new FaultInjector();
            var ledger = await fixture.Open(faults: faults);
            var operation = await Queue(ledger);
            faults.Stage = stage;
            var externalEffect = false;
            await Throws<InjectedFailure>(async () =>
            {
                await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
                externalEffect = true;
            });
            Check(!externalEffect);
            Check((await ledger.ReadConversationAsync(Identity)).IsReadOnly);
            await ledger.DisposeAsync();
            ledger = await fixture.Open();
            var snapshot = await ledger.ReadConversationAsync(Identity);
            Equal(1, snapshot.Operations.Length);
            if (stage is JournalWriteStage.AfterHeader or JournalWriteStage.AfterPayload)
            {
                Check(snapshot.IsReadOnly);
                Equal(0, (await ledger.GetRecoveryPlanAsync(Identity)).QueuedOperations.Length);
                Equal(OperationStage.Queued, snapshot.Operations.Single().Stage);
            }
            else if (stage is JournalWriteStage.BeforeFlush or JournalWriteStage.AfterFlush)
                Equal(OperationStage.Unknown, snapshot.Operations.Single().Stage);
            else
                Equal(OperationStage.Queued, snapshot.Operations.Single().Stage);
        }
    }

    private static async Task CorruptTail()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Queue(ledger);
        await ledger.DisposeAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.Path);
        bytes[^10] ^= 0x5a;
        await File.WriteAllBytesAsync(fixture.Path, bytes);
        ledger = await fixture.Open();
        var snapshot = await ledger.ReadConversationAsync(Identity);
        Check(snapshot.IsReadOnly);
        await Error(LedgerErrors.RecoveryRequired, () => Receive(ledger, "m2"));
        Equal(0, (await ledger.GetRecoveryPlanAsync(Identity)).QueuedOperations.Length);
        await ledger.DisposeAsync();
        var preserved = await File.ReadAllBytesAsync(fixture.Path);
        Check(bytes.SequenceEqual(preserved));
    }

    private static async Task WrongKey()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        await ledger.DisposeAsync();
        var original = await File.ReadAllBytesAsync(fixture.Path);
        await Error(LedgerErrors.ProtectionFailed, () => DurableConversationLedger.OpenAsync(
            new FileJournalStorage(fixture.Path), new TestEncryption(RandomNumberGenerator.GetBytes(32))));
        var preserved = await File.ReadAllBytesAsync(fixture.Path);
        Check(original.SequenceEqual(preserved));
    }

    private static async Task ExistingEmptyFile()
    {
        await using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Path, []);
        var ledger = await fixture.Open();
        Check((await ledger.ReadConversationAsync(Identity)).IsReadOnly);
        await Error(LedgerErrors.RecoveryRequired, () => Receive(ledger));
        await ledger.DisposeAsync();
        Equal(0L, new FileInfo(fixture.Path).Length);
    }

    private static async Task WriterExclusion()
    {
        await using var fixture = new Fixture();
        await fixture.Open();
        await Throws<IOException>(() =>
        {
            _ = new FileJournalStorage(fixture.Path);
            return Task.CompletedTask;
        });
    }

    private static async Task ConcurrentReceivers()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Receive(ledger, "m" + i, "body " + i)));
        Equal(30, results.Select(x => x.CurrentRevision).Distinct().Count());
        Equal(30L, (await ledger.ReadConversationAsync(Identity)).Revision);
        await ledger.DisposeAsync();
        ledger = await fixture.Open();
        Equal(30, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
    }

    private static async Task ImmutableSnapshots()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Receive(ledger);
        var snapshot = await ledger.ReadConversationAsync(Identity);
        _ = snapshot.Inbox.SetItem(0, snapshot.Inbox[0] with { Body = "mutated" });
        Equal("please continue test", (await ledger.ReadConversationAsync(Identity)).Inbox.Single().Body);
    }

    private static async Task Capacity()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open(options: new() { MaximumSnapshotBytes = 2048 });
        await Receive(ledger);
        await Error(LedgerErrors.CapacityExceeded, () => Receive(ledger, "m2", new string('x', 3000)));
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
    }

    private static async Task Cancellation()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Throws<OperationCanceledException>(() => ledger.ReceiveAsync(Identity, "cancelled", "body",
            MessageTrust.AuthenticatedUser, FakeClock.Now, cancellationToken: cancel.Token));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
    }

    private static async Task ProcessCrash()
    {
        await using var fixture = new Fixture();
        var ledger = await fixture.Open();
        await Queue(ledger);
        await ledger.DisposeAsync();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (System.IO.Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-after-dispatch-flush");
        start.ArgumentList.Add(fixture.Path);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start test child.");
        try
        {
            await child.StandardInput.WriteLineAsync(Convert.ToBase64String(fixture.Key));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(73, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
        ledger = await fixture.Open();
        Equal(OperationStage.Unknown, (await ledger.ReadConversationAsync(Identity)).Operations.Single().Stage);
        Equal(0, (await ledger.GetRecoveryPlanAsync(Identity)).QueuedOperations.Length);
    }

    private static Task<ReceiveResult> Receive(DurableConversationLedger ledger, string id = "m1", string body = "please continue test") =>
        ledger.ReceiveAsync(Identity, id, body, MessageTrust.AuthenticatedUser, FakeClock.Now);
    private static Task<UserAuthorization> Authorize(DurableConversationLedger ledger, string id = "m1") =>
        ledger.AuthorizeCurrentUserRequestAsync(Identity, id, "send_to_task", "local/thread/turn",
            """{"text":"continue test"}""", "continue test", new("local", "thread", "turn"));
    private static async Task<OperationRecord> Queue(DurableConversationLedger ledger)
    {
        await Receive(ledger);
        var auth = await Authorize(ledger);
        return await ledger.QueueOperationAsync(Identity, auth.Id, "operation-idempotency-key");
    }
    private static async Task<ImmutableArray<OutboxMessage>> Reply(DurableConversationLedger ledger, string[] segments)
    {
        await Receive(ledger);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.PrepareReplyAsync(lease, segments);
        return await ledger.EnqueuePreparedReplyAsync(lease);
    }
    private static async Task Error(string code, Func<Task> action) => Equal(code, (await Throws<LedgerException>(action)).Code);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
}

internal sealed class Fixture : IAsyncDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-ledger-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<DurableConversationLedger> _opened = [];
    internal string Path => System.IO.Path.Combine(_directory, "conversation.journal");
    internal byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    internal Fixture() => Directory.CreateDirectory(_directory);
    internal async Task<DurableConversationLedger> Open(FaultInjector? faults = null, LedgerOptions? options = null,
        IOutputDependencyValidator? validator = null, IClock? clock = null)
    {
        var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path, faultInjector: faults),
            new TestEncryption(Key), clock ?? new FakeClock(), options, dependencyValidator: validator);
        _opened.Add(ledger);
        return ledger;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var ledger in _opened) await ledger.DisposeAsync();
        // Only the exact unique test directory created by this fixture is removed.
        var resolved = System.IO.Path.GetFullPath(_directory);
        var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(resolved).StartsWith("assistant-ledger-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the test temporary directory.");
        Directory.Delete(resolved, recursive: true);
        CryptographicOperations.ZeroMemory(Key);
    }
}

internal sealed class FakeClock : IClock
{
    internal static DateTimeOffset Now => new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Now;
}

// Test-only ephemeral encryption. Production Windows hosts inject current-user DPAPI instead.
internal sealed class TestEncryption(byte[] key) : IStateProtector
{
    public string Scheme => "test-aes-gcm-256";
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
    {
        var result = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), purpose);
        return result;
    }
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose)
    {
        var result = new byte[ciphertext.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(ciphertext[..12], ciphertext[28..], ciphertext.Slice(12, 16), result, purpose);
        return result;
    }
}

internal sealed class InjectedFailure : IOException;
internal sealed class FaultInjector : IJournalFaultInjector
{
    internal JournalWriteStage? Stage { get; set; }
    internal bool ExitProcess { get; init; }
    public void At(JournalWriteStage stage, long sequence)
    {
        if (stage != Stage) return;
        if (ExitProcess) Environment.Exit(73);
        throw new InjectedFailure();
    }
}
