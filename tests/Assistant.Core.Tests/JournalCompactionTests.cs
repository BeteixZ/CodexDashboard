using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core;

internal static class JournalCompactionTests
{
    private const int FrameLimit = 1024;
    private const long BankLimit = 2048;
    private const int AnchorLength = 56;
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("legacy_stays_readable_without_sidecars_below_threshold", Legacy),
        ("small_bank_capacity_compacts_repeatedly_without_sequence_reset", RepeatedCompaction),
        ("migration_checkpoint_preserves_exact_ciphertext_and_original_sequence", CheckpointBytes),
        ("anchor_binds_published_storage_and_replaces_duplicate_physical_snapshots", Anchor),
        ("ledger_source_deduplication_survives_physical_compaction", LedgerDeduplication),
        ("ledger_prepared_segments_survive_compaction_and_restart", LedgerPrepared),
        ("ledger_dispatching_operation_still_recovers_unknown", LedgerUnknownOperation),
        ("ledger_sending_outbox_still_recovers_unknown", LedgerUnknownOutbox),
        ("active_input_work_survives_compaction_with_original_source_and_charged_budget", LedgerInputWork),
        ("dispatch_write_compaction_fault_never_returns_host_dispatch_authority", DispatchCompactionFault),
        ("migration_fault_matrix_never_invents_next_transaction_or_falls_back_writable", MigrationFaults),
        ("rotation_fault_matrix_preserves_committed_sequence", RotationFaults),
        ("v2_append_fault_matrix_never_treats_overwritten_end_marker_as_writable_prefix", AppendFaults),
        ("real_process_exit_after_switch_flush_preserves_publication_boundary", ProcessExitAfterSwitch),
        ("selected_bank_corruption_is_readonly_without_fallback", SelectedCorrupt),
        ("selected_bank_truncation_is_readonly_without_fallback", SelectedTruncated),
        ("selected_bank_missing_is_not_recreated", SelectedMissing),
        ("empty_selected_bank_is_not_initialized_as_new", SelectedEmpty),
        ("switch_log_corruption_fails_closed_preserving_evidence", SwitchCorrupt),
        ("switch_log_incomplete_tail_fails_closed", SwitchTornTail),
        ("missing_switch_log_never_falls_back_to_legacy_or_other_bank", SwitchMissing),
        ("empty_switch_log_never_reinitializes_storage", SwitchEmpty),
        ("missing_anchor_with_existing_sidecars_fails_closed", AnchorMissing),
        ("corrupt_anchor_with_existing_sidecars_fails_closed", AnchorCorrupt),
        ("foreign_complete_sidecar_group_cannot_attach_to_another_anchor", ForeignSidecars),
        ("legacy_anchor_with_partial_v2_artifacts_is_readonly", UnpublishedArtifacts),
        ("inactive_partial_copy_is_ignored_and_next_rotation_replaces_it", InactivePartial),
        ("stable_writer_exclusion_survives_migration_and_rotation", WriterExclusion),
        ("switch_journal_capacity_is_bounded_without_sequence_loss", SwitchCapacity),
        ("checkpoint_and_next_frame_must_both_fit_without_deleting_current_state", CheckpointCapacity),
        ("bank_capacity_rejection_keeps_files_and_allows_smaller_append", BankCapacityRetry),
        ("precancelled_append_does_not_create_migration_artifacts", CancelBeforeMigration),
        ("precancelled_v2_append_does_not_modify_any_file", CancelAfterMigration)
    ];

    internal static async Task<int> Run()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(45)); Console.WriteLine("PASS journal_compaction/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL journal_compaction/" + name + ": " + error); }
        }
        Console.WriteLine($"JOURNAL COMPACTION TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    internal static async Task<int?> TryRunCrashChildAsync(string[] args)
    {
        if (args is not ["--crash-journal-switch-flush", var path]) return null;
        var resolved = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(resolved)!;
        var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(directory).StartsWith("assistant-ledger-tests-", StringComparison.Ordinal)
            || System.IO.Path.GetFileName(resolved) != "conversation.journal" || !File.Exists(resolved))
            throw new InvalidOperationException("Crash child accepts only its existing unique temporary fixture.");
        await using var storage = new FileJournalStorage(resolved, maximumFrameBytes: FrameLimit,
            maximumJournalBytes: BankLimit, compactionFaultInjector: new ExitAfterSwitch());
        var sequence = (await storage.ReadLatestAsync())?.Sequence ?? 0;
        for (var i = 0; i < 64; i++) sequence = await Push(storage, sequence);
        return 91;
    }

    private static FileJournalStorage Open(Fixture f, IJournalCompactionFaultInjector? faults = null,
        long switchLimit = 1024 * 1024, IJournalFaultInjector? appendFaults = null) =>
        new(f.Path, maximumFrameBytes: FrameLimit, maximumJournalBytes: BankLimit,
            faultInjector: appendFaults, compactionFaultInjector: faults, maximumSwitchJournalBytes: switchLimit);

    private static string Bank(Fixture f, int index) => f.Path + ".bank" + index;
    private static string Switch(Fixture f) => f.Path + ".switch";
    private static string[] Paths(Fixture f) => [f.Path, Bank(f, 0), Bank(f, 1), Switch(f)];

    private static byte[] Payload(long sequence, int length = 128)
    {
        var result = new byte[length];
        BinaryPrimitives.WriteInt64LittleEndian(result, sequence);
        for (var offset = 8; offset < result.Length; offset++)
            result[offset] = (byte)((sequence * 43 + offset * 17) % 251);
        return result;
    }

    private static async Task<long> Push(FileJournalStorage storage, long sequence)
    {
        await storage.AppendAsync(sequence, Payload(sequence + 1));
        return sequence + 1;
    }

    private static async Task<long> UntilV2(FileJournalStorage storage, Fixture f, long sequence = 0)
    {
        for (var attempts = 0; attempts < 64; attempts++)
        {
            if (await IsV2(f)) return sequence;
            sequence = await Push(storage, sequence);
        }
        throw new InvalidOperationException("Expected automatic migration within the small file capacity.");
    }

    private static async Task<bool> IsV2(Fixture f)
    {
        if (!File.Exists(f.Path)) return false;
        var data = await Read(f.Path);
        return data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual("ASBANK02"u8);
    }

    private static async Task Legacy()
    {
        await using var f = new Fixture();
        await using (var storage = Open(f))
        {
            long sequence = 0;
            for (var i = 0; i < 3; i++) sequence = await Push(storage, sequence);
            Check(!await IsV2(f));
            Check(Paths(f).Skip(1).All(path => !File.Exists(path)));
            Equal(3L, (await storage.ReadLatestAsync())!.Sequence);
        }
        await using var reopened = Open(f);
        Check(!reopened.Recovery.Required);
        var latest = (await reopened.ReadLatestAsync())!;
        Equal(3L, latest.Sequence);
        Bytes(Payload(3), latest.Payload);
        Check(Paths(f).Skip(1).All(path => !File.Exists(path)));
    }

    private static async Task RepeatedCompaction()
    {
        await using var f = new Fixture();
        long sequence = 0;
        await using (var storage = Open(f))
        {
            for (var i = 0; i < 90; i++)
            {
                sequence = await Push(storage, sequence);
                var latest = (await storage.ReadLatestAsync())!;
                Equal(sequence, latest.Sequence);
                Bytes(Payload(sequence), latest.Payload);
                Check(!storage.Recovery.Required);
                foreach (var bank in new[] { Bank(f, 0), Bank(f, 1) })
                    if (File.Exists(bank)) Check(new FileInfo(bank).Length <= BankLimit);
            }
            Check(await IsV2(f));
            Check(new FileInfo(Switch(f)).Length > 56 + 2 * 120);
        }
        await using var reopened = Open(f);
        Check(!reopened.Recovery.Required);
        Equal(sequence, (await reopened.ReadLatestAsync())!.Sequence);
        Bytes(Payload(sequence), (await reopened.ReadLatestAsync())!.Payload);
        sequence = await Push(reopened, sequence);
        Equal(91L, sequence);
    }

    private static async Task CheckpointBytes()
    {
        await using var f = new Fixture();
        await using (var storage = Open(f))
        {
            var sequence = await UntilV2(storage, f);
            Check(sequence > 2);
            var active = await SelectedBank(f, Payload(sequence));
            var bank = await Read(active);
            Check(Contains(bank, Payload(sequence - 1)));
            Check(Contains(bank, Payload(sequence)));
            Equal(sequence, (await storage.ReadLatestAsync())!.Sequence);
            Bytes(Payload(sequence), (await storage.ReadLatestAsync())!.Payload);
        }
        await using var reopened = Open(f);
        Check(!reopened.Recovery.Required);
        var latest = (await reopened.ReadLatestAsync())!;
        Bytes(Payload(latest.Sequence), latest.Payload);
    }

    private static async Task Anchor()
    {
        await using var f = new Fixture();
        await using var storage = Open(f);
        var sequence = await UntilV2(storage, f);
        Equal((long)AnchorLength, new FileInfo(f.Path).Length);
        var anchor = await Read(f.Path);
        Check(anchor.AsSpan(0, 8).SequenceEqual("ASBANK02"u8));
        Check(!anchor.AsSpan(0, 8).SequenceEqual("ASLEDG01"u8));
        Check(Paths(f).Skip(1).All(File.Exists));
        Equal(sequence, (await storage.ReadLatestAsync())!.Sequence);
        Check(new FileInfo(Bank(f, 0)).Length <= BankLimit && new FileInfo(Bank(f, 1)).Length <= BankLimit);
    }

    private static Task<DurableConversationLedger> OpenLedger(Fixture f) =>
        DurableConversationLedger.OpenAsync(
            new FileJournalStorage(f.Path, maximumFrameBytes: 128 * 1024, maximumJournalBytes: 256 * 1024),
            new TestEncryption(f.Key), new FakeClock());

    private static async Task PadUntilV2(DurableConversationLedger ledger, Fixture f)
    {
        for (var i = 0; i < 128; i++)
        {
            if (await IsV2(f)) return;
            await ledger.AppendContextAsync(Identity, ConversationSource.Report,
                "test-only-context-" + i + "-" + new string('x', 384), FakeClock.Now);
        }
        throw new InvalidOperationException("The ledger test did not reach physical compaction.");
    }

    private static Task<ReceiveResult> Input(DurableConversationLedger ledger) =>
        ledger.ReceiveAsync(Identity, "original-source", "Original exact user request.",
            MessageTrust.AuthenticatedUser, FakeClock.Now);

    private static async Task LedgerDeduplication()
    {
        await using var f = new Fixture();
        await using var initial = await OpenLedger(f);
        var received = await Input(initial);
        await PadUntilV2(initial, f);
        var before = await initial.ReadConversationAsync(Identity);
        await initial.DisposeAsync();
        await using var reopened = await OpenLedger(f);
        var duplicate = await Input(reopened);
        Check(duplicate.IsDuplicate);
        Equal(received.Message.Id, duplicate.Message.Id);
        Equal(1L, duplicate.CurrentRevision);
        var after = await reopened.ReadConversationAsync(Identity);
        Equal(before.Events.Length, after.Events.Length);
        Equal("Original exact user request.", after.Inbox.Single().Body);
        Check(!after.IsReadOnly);
    }

    private static async Task LedgerPrepared()
    {
        await using var f = new Fixture();
        await using var initial = await OpenLedger(f);
        await Input(initial);
        var lease = await initial.BeginGenerationAsync(Identity, ["original-source"]);
        await initial.PrepareReplyAsync(lease, ["first actual segment", "second actual segment"]);
        await PadUntilV2(initial, f);
        await initial.DisposeAsync();
        await using var reopened = await OpenLedger(f);
        Equal(lease.Id, (await reopened.GetRecoveryPlanAsync(Identity)).PreparedReplies.Single().Id);
        var outbox = await reopened.EnqueuePreparedReplyAsync(lease);
        Check(outbox.Select(x => x.Body).SequenceEqual(new[] { "first actual segment", "second actual segment" }));
        var again = await reopened.EnqueuePreparedReplyAsync(lease);
        Check(outbox.Select(x => x.Id).SequenceEqual(again.Select(x => x.Id)));
        Equal(1, (await reopened.ReadConversationAsync(Identity)).Inbox.Length);
    }

    private static async Task LedgerUnknownOperation()
    {
        await using var f = new Fixture();
        await using var initial = await OpenLedger(f);
        await Input(initial);
        var authorization = await initial.AuthorizeCurrentUserRequestAsync(Identity, "original-source",
            "test-action", "target", "{}", "exact user request");
        var operation = await initial.QueueOperationAsync(Identity, authorization.Id, "original-effect");
        await initial.BeginOperationDispatchAsync(Identity, operation.Id);
        await PadUntilV2(initial, f);
        await initial.DisposeAsync();
        await using var reopened = await OpenLedger(f);
        var saved = (await reopened.ReadConversationAsync(Identity)).Operations.Single();
        Equal(operation.Id, saved.Id);
        Equal(OperationStage.Unknown, saved.Stage);
        Equal(operation.Id, (await reopened.GetRecoveryPlanAsync(Identity)).OperationsToReconcile.Single().Id);
        await Error(LedgerErrors.InvalidTransition, () => reopened.BeginOperationDispatchAsync(Identity, operation.Id));
    }

    private static async Task LedgerUnknownOutbox()
    {
        await using var f = new Fixture();
        await using var initial = await OpenLedger(f);
        await Input(initial);
        var lease = await initial.BeginGenerationAsync(Identity, ["original-source"]);
        await initial.PrepareReplyAsync(lease, ["uncertain channel segment"]);
        var message = (await initial.EnqueuePreparedReplyAsync(lease)).Single();
        await initial.BeginOutboxSendAsync(Identity, message.Id);
        await PadUntilV2(initial, f);
        await initial.DisposeAsync();
        await using var reopened = await OpenLedger(f);
        var saved = (await reopened.ReadConversationAsync(Identity)).Outbox.Single();
        Equal(message.Id, saved.Id);
        Equal("uncertain channel segment", saved.Body);
        Equal(OutboxStage.Unknown, saved.Stage);
        var recovery = await reopened.GetRecoveryPlanAsync(Identity);
        Check(recovery.PendingOutbox.IsEmpty);
        Equal(message.Id, recovery.OutboxToReconcile.Single().Id);
        await Error(LedgerErrors.InvalidTransition, () => reopened.BeginOutboxSendAsync(Identity, message.Id));
    }

    private static async Task LedgerInputWork()
    {
        await using var f = new Fixture();
        await using var initial = await OpenLedger(f);
        var source = await Input(initial);
        await initial.ReceiveAsync(Identity, "latest-source", "Only read; do not change any task.",
            MessageTrust.AuthenticatedUser, FakeClock.Now);
        var work = (await initial.QueueUnprocessedInputsAsync(Identity)).Single();
        var lease = await initial.BeginReadOnlyWorkAsync(Identity, work.Id);
        Check(await initial.ReserveAgentBudgetAsync(lease, "original-source", false, 1, 1));
        Check(await initial.ReserveAgentBudgetAsync(lease, "original-source", true, 1, 1));
        var invocation = await initial.RegisterToolInvocationAsync(lease, "original-source",
            "read-original", "read_task", """{"hostId":"local","threadId":"thread-A"}""", false);
        await initial.BeginToolInvocationAsync(lease, invocation.Id);
        await PadUntilV2(initial, f);
        var before = await initial.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Reading, before.InputWork.Single().Stage);
        Equal(ToolInvocationStage.Executing, before.ToolInvocations.Single().Stage);
        Equal(2L, before.Revision);
        await initial.DisposeAsync();

        await using var reopened = await OpenLedger(f);
        var recovered = await reopened.ReadConversationAsync(Identity);
        Check(!recovered.IsReadOnly);
        var saved = recovered.InputWork.Single();
        Equal(work.Id, saved.Id);
        Equal("original-source", saved.SourceMessageId);
        Equal(source.Message.Revision, saved.SourceRevision);
        Equal(2L, saved.ReadAtRevision!.Value);
        Equal(0L, saved.DialogueEpoch);
        Equal(1L, saved.Version);
        Equal(lease.Id, saved.GenerationId!.Value);
        Equal(InputWorkStage.Queued, saved.Stage);
        Equal(GenerationStage.Interrupted, recovered.Generations.Single().Stage);
        Equal(ToolInvocationStage.Interrupted, recovered.ToolInvocations.Single().Stage);
        Equal(source.Message.Id, recovered.Inbox.Single(x => x.SourceMessageId == "original-source").Id);
        Equal("Original exact user request.", recovered.Inbox.Single(x => x.SourceMessageId == "original-source").Body);
        Equal(before.Events.Length, recovered.Events.Length);
        Equal(before.AgentBudgets.Single(), recovered.AgentBudgets.Single());
        Check(recovered.Authorizations.IsEmpty && recovered.Operations.IsEmpty && recovered.Outbox.IsEmpty);

        var resumed = await reopened.BeginReadOnlyWorkAsync(Identity, work.Id);
        Equal(1L, resumed.Revision);
        Check(resumed.Id != lease.Id);
        var restarted = await reopened.ReadConversationAsync(Identity);
        Equal(2L, restarted.InputWork.Single().Version);
        Equal(2L, restarted.InputWork.Single().ReadAtRevision!.Value);
        Check(!await reopened.ReserveAgentBudgetAsync(resumed, "original-source", false, 1, 1));
        Check(!await reopened.ReserveAgentBudgetAsync(resumed, "original-source", true, 1, 1));
        await Error(LedgerErrors.StaleRevision, () => reopened.AuthorizeCurrentUserRequestAsync(
            Identity, "original-source", "test-action", "target", "{}", "exact user request"));
        var effect = await reopened.RegisterToolInvocationAsync(resumed, "original-source",
            "forged-effect", "send_to_task", "{}", true);
        await Error(LedgerErrors.AuthorizationRequired, () => reopened.BeginToolInvocationAsync(resumed, effect.Id));
        restarted = await reopened.ReadConversationAsync(Identity);
        Check(restarted.Authorizations.IsEmpty && restarted.Operations.IsEmpty && restarted.Outbox.IsEmpty);
        Equal(1, restarted.AgentBudgets.Single().ModelTurns);
        Equal(1, restarted.AgentBudgets.Single().ToolCalls);
    }

    private static async Task DispatchCompactionFault()
    {
        foreach (var alreadyMigrated in new[] { false, true })
        {
            await using var f = new Fixture();
            var originalStorage = new FileJournalStorage(f.Path, maximumFrameBytes: 128 * 1024,
                maximumJournalBytes: 256 * 1024);
            await using var original = await DurableConversationLedger.OpenAsync(
                originalStorage, new TestEncryption(f.Key), new FakeClock());
            await Input(original);
            var authorization = await original.AuthorizeCurrentUserRequestAsync(Identity, "original-source",
                "test-action", "target", "{}", "exact user request");
            var operation = await original.QueueOperationAsync(Identity, authorization.Id, "dispatch-boundary");
            if (alreadyMigrated) await PadUntilV2(original, f);
            var checkpoint = (await originalStorage.ReadLatestAsync())!;
            var before = await original.ReadConversationAsync(Identity);
            Equal(OperationStage.Queued, before.Operations.Single().Stage);

            // FakeClock is constant and BeginDispatch changes only Queued -> Dispatching. The
            // encrypted test format has fixed overhead, so this transition grows by exactly five
            // UTF-8 bytes. Fit the unchanged checkpoint plus that frame, while forcing this specific
            // next write to cross the compaction threshold.
            var nextLength = checkpoint.Payload.Length
                + OperationStage.Dispatching.ToString().Length - OperationStage.Queued.ToString().Length;
            var frameLimit = Math.Max(1024, nextLength + 128);
            var bankLimit = 80L + 52 + checkpoint.Payload.Length + 52 + nextLength + 64 + 64;
            var activePath = alreadyMigrated ? await SelectedBank(f, checkpoint.Payload) : f.Path;
            var activeLength = new FileInfo(activePath).Length;
            Check(activeLength + nextLength + 52 > bankLimit);
            if (alreadyMigrated) Check(activeLength <= bankLimit);
            await original.DisposeAsync();

            var fault = new CompactionFault { Stage = JournalCompactionStage.SwitchAfterFlush };
            var faultingStorage = new FileJournalStorage(f.Path, maximumFrameBytes: frameLimit,
                maximumJournalBytes: bankLimit, compactionFaultInjector: fault);
            await using var faulting = await DurableConversationLedger.OpenAsync(
                faultingStorage, new TestEncryption(f.Key), new FakeClock());
            Equal(checkpoint.Sequence, (await faultingStorage.ReadLatestAsync())!.Sequence);
            var hostDispatchCalls = 0;
            var failed = false;
            try
            {
                await faulting.BeginOperationDispatchAsync(Identity, operation.Id);
                hostDispatchCalls++; // A host can reach external dispatch only after the durable call succeeds.
            }
            catch (InjectedFailure) { failed = true; }
            Check(failed && fault.Hit);
            Equal(checkpoint.Sequence, fault.Sequence);
            Equal(0, hostDispatchCalls);
            var afterFailure = await faulting.ReadConversationAsync(Identity);
            Check(afterFailure.IsReadOnly);
            Equal(OperationStage.Queued, afterFailure.Operations.Single().Stage);
            Equal(operation.Id, afterFailure.Operations.Single().Id);
            Equal(before.Events.Length, afterFailure.Events.Length);
            Equal(checkpoint.Sequence, (await faultingStorage.ReadLatestAsync())!.Sequence);
            Bytes(checkpoint.Payload, (await faultingStorage.ReadLatestAsync())!.Payload);
            await Error(LedgerErrors.RecoveryRequired,
                () => faulting.BeginOperationDispatchAsync(Identity, operation.Id));
            await faulting.DisposeAsync();

            var reopenedStorage = new FileJournalStorage(f.Path, maximumFrameBytes: 128 * 1024,
                maximumJournalBytes: 256 * 1024);
            await using var reopened = await DurableConversationLedger.OpenAsync(
                reopenedStorage, new TestEncryption(f.Key), new FakeClock());
            var recovered = await reopened.ReadConversationAsync(Identity);
            Equal(!alreadyMigrated, recovered.IsReadOnly);
            Equal(checkpoint.Sequence, (await reopenedStorage.ReadLatestAsync())!.Sequence);
            Bytes(checkpoint.Payload, (await reopenedStorage.ReadLatestAsync())!.Payload);
            Equal(operation, recovered.Operations.Single());
            Equal(authorization, recovered.Authorizations.Single());
            Equal(before.Events.Length, recovered.Events.Length);
            Equal(before.Revision, recovered.Revision);
            Equal(0, hostDispatchCalls);
            if (!alreadyMigrated)
            {
                Check((await reopened.GetRecoveryPlanAsync(Identity)).QueuedOperations.IsEmpty);
                await Error(LedgerErrors.RecoveryRequired,
                    () => reopened.BeginOperationDispatchAsync(Identity, operation.Id));
            }
            else
                Equal(operation.Id, (await reopened.GetRecoveryPlanAsync(Identity)).QueuedOperations.Single().Id);
        }
    }

    private static async Task MigrationFaults()
    {
        foreach (var stage in Enum.GetValues<JournalCompactionStage>())
        {
            await using var f = new Fixture();
            var fault = new CompactionFault { Stage = stage };
            long sequence = 0;
            await using (var storage = Open(f, fault))
            {
                var failed = false;
                for (var i = 0; i < 64 && !failed; i++)
                {
                    try { sequence = await Push(storage, sequence); }
                    catch (InjectedFailure) { failed = true; }
                }
                Check(failed && fault.Hit);
                Equal(sequence, fault.Sequence);
                Check(storage.Recovery.Required);
                await Error(LedgerErrors.RecoveryRequired, () => storage.AppendAsync(sequence, Payload(sequence + 1)).AsTask());
            }
            await using var reopened = Open(f);
            var latest = await reopened.ReadLatestAsync();
            if (latest is not null)
            {
                Equal(sequence, latest.Sequence);
                Bytes(Payload(sequence), latest.Payload);
            }
            if (reopened.Recovery.Required)
                await Error(LedgerErrors.RecoveryRequired, () => reopened.AppendAsync(sequence, Payload(sequence + 1)).AsTask());
            else
            {
                Check(stage is JournalCompactionStage.AnchorBeforeFlush or JournalCompactionStage.AnchorAfterFlush);
                Check(latest is not null);
                Equal(sequence + 1, await Push(reopened, sequence));
            }
        }
    }

    private static async Task RotationFaults()
    {
        foreach (var stage in new[]
        {
            JournalCompactionStage.CheckpointAfterHeader, JournalCompactionStage.CheckpointAfterPayload,
            JournalCompactionStage.CheckpointBeforeFlush, JournalCompactionStage.CheckpointAfterFlush,
            JournalCompactionStage.SwitchAfterHeader, JournalCompactionStage.SwitchBeforeFlush,
            JournalCompactionStage.SwitchAfterFlush
        })
        {
            await using var f = new Fixture();
            var fault = new CompactionFault();
            long sequence;
            await using (var storage = Open(f, fault))
            {
                sequence = await UntilV2(storage, f);
                fault.Stage = stage;
                var failed = false;
                for (var i = 0; i < 64 && !failed; i++)
                {
                    try { sequence = await Push(storage, sequence); }
                    catch (InjectedFailure) { failed = true; }
                }
                Check(failed && fault.Hit);
                Equal(sequence, fault.Sequence);
                Check(storage.Recovery.Required);
            }
            await using var reopened = Open(f);
            var latest = await reopened.ReadLatestAsync();
            if (latest is not null)
            {
                Equal(sequence, latest.Sequence);
                Bytes(Payload(sequence), latest.Payload);
            }
            if (stage is JournalCompactionStage.CheckpointAfterHeader or JournalCompactionStage.CheckpointAfterPayload
                or JournalCompactionStage.CheckpointBeforeFlush or JournalCompactionStage.CheckpointAfterFlush)
            {
                Check(!reopened.Recovery.Required);
                Check(latest is not null);
            }
            if (reopened.Recovery.Required)
                await Error(LedgerErrors.RecoveryRequired, () => reopened.AppendAsync(sequence, Payload(sequence + 1)).AsTask());
            else
                Equal(sequence + 1, await Push(reopened, sequence));
        }
    }

    private static async Task AppendFaults()
    {
        foreach (var stage in Enum.GetValues<JournalWriteStage>())
        {
            await using var f = new Fixture();
            long sequence;
            await using (var seed = Open(f))
                sequence = await UntilV2(seed, f);
            var switchBefore = await Read(Switch(f));
            var fault = new FaultInjector { Stage = stage };
            await using (var storage = Open(f, appendFaults: fault))
            {
                var failed = false;
                try { await storage.AppendAsync(sequence, Payload(sequence + 1)); }
                catch (InjectedFailure) { failed = true; }
                Check(failed);
                Check(storage.Recovery.Required);
                Equal(sequence, (await storage.ReadLatestAsync())!.Sequence);
                await Error(LedgerErrors.RecoveryRequired,
                    () => storage.AppendAsync(sequence, Payload(sequence + 1)).AsTask());
            }
            var switchAfter = await Read(Switch(f));
            Bytes(switchBefore, switchAfter);
            var physicalBeforeOpen = await Files(f);
            await using var reopened = Open(f);
            var latest = (await reopened.ReadLatestAsync())!;
            if (stage is JournalWriteStage.AfterHeader or JournalWriteStage.AfterPayload)
            {
                Check(reopened.Recovery.Required);
                Equal(sequence, latest.Sequence);
                Bytes(Payload(sequence), latest.Payload);
                await Error(LedgerErrors.RecoveryRequired,
                    () => reopened.AppendAsync(sequence, Payload(sequence + 1)).AsTask());
            }
            else
            {
                Check(!reopened.Recovery.Required);
                // BeforeFlush injects after writing the complete frame and replacement End marker.
                // Closing/reopening this real file observes those bytes; this is not a power-loss test.
                var observedSequence = stage == JournalWriteStage.BeforeWrite ? sequence : sequence + 1;
                Equal(observedSequence, latest.Sequence);
                Bytes(Payload(observedSequence), latest.Payload);
            }
            await SameFiles(f, physicalBeforeOpen);
        }
    }

    private static async Task ProcessExitAfterSwitch()
    {
        foreach (var alreadyMigrated in new[] { false, true })
        {
            await using var f = new Fixture();
            if (alreadyMigrated) await MakeV2(f);
            else
            {
                await using var seed = Open(f);
                await seed.AppendAsync(0, Payload(1));
            }
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--crash-journal-switch-flush");
            start.ArgumentList.Add(f.Path);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the crash child.");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            var text = (await output).Trim();
            var stderr = await errors;
            if (child.ExitCode != 74)
                throw new InvalidOperationException($"Crash child returned {child.ExitCode}: {stderr}; {text}");
            Check(long.TryParse(text, out var checkpointSequence));
            await using var reopened = Open(f);
            Equal(!alreadyMigrated, reopened.Recovery.Required);
            var latest = (await reopened.ReadLatestAsync())!;
            Equal(checkpointSequence, latest.Sequence);
            Bytes(Payload(checkpointSequence), latest.Payload);
            if (alreadyMigrated)
                Equal(checkpointSequence + 1, await Push(reopened, checkpointSequence));
            else
                await Error(LedgerErrors.RecoveryRequired,
                    () => reopened.AppendAsync(checkpointSequence, Payload(checkpointSequence + 1)).AsTask());
        }
    }

    private static async Task<long> MakeV2(Fixture f)
    {
        await using var storage = Open(f);
        var sequence = await UntilV2(storage, f);
        for (var i = 0; i < 12; i++) sequence = await Push(storage, sequence);
        return sequence;
    }

    private static async Task SelectedCorrupt()
    {
        await using var f = new Fixture();
        var sequence = await MakeV2(f);
        var path = await SelectedBank(f, Payload(sequence));
        var bytes = await Read(path);
        bytes[^1] ^= 0x55;
        await Write(path, bytes);
        await FailClosed(f);
    }

    private static async Task SelectedTruncated()
    {
        await using var f = new Fixture();
        var sequence = await MakeV2(f);
        var path = await SelectedBank(f, Payload(sequence));
        var bytes = await Read(path);
        await Write(path, bytes[..^11]);
        await FailClosed(f);
    }

    private static async Task SelectedMissing()
    {
        await using var f = new Fixture();
        var sequence = await MakeV2(f);
        File.Delete(await SelectedBank(f, Payload(sequence)));
        await FailClosed(f);
    }

    private static async Task SelectedEmpty()
    {
        await using var f = new Fixture();
        var sequence = await MakeV2(f);
        await Write(await SelectedBank(f, Payload(sequence)), []);
        await FailClosed(f);
    }

    private static async Task SwitchCorrupt()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        var bytes = await Read(Switch(f));
        bytes[^1] ^= 0x55;
        await Write(Switch(f), bytes);
        await FailClosed(f);
    }

    private static async Task SwitchTornTail()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        await using (var file = new FileStream(Switch(f), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
            file.Flush(true);
        }
        await FailClosed(f);
    }

    private static async Task SwitchMissing()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        File.Delete(Switch(f));
        await FailClosed(f);
    }

    private static async Task SwitchEmpty()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        await Write(Switch(f), []);
        await FailClosed(f);
    }

    private static async Task AnchorMissing()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        File.Delete(f.Path);
        await FailClosed(f);
    }

    private static async Task AnchorCorrupt()
    {
        await using var f = new Fixture();
        await MakeV2(f);
        var bytes = await Read(f.Path);
        bytes[16] ^= 0x55;
        await Write(f.Path, bytes);
        await FailClosed(f);
    }

    private static async Task ForeignSidecars()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        await MakeV2(first);
        await MakeV2(second);
        foreach (var suffix in new[] { ".bank0", ".bank1", ".switch" })
            await Write(first.Path + suffix, await Read(second.Path + suffix));
        await FailClosed(first);
    }

    private static async Task UnpublishedArtifacts()
    {
        await using var f = new Fixture();
        await using (var storage = Open(f))
            await storage.AppendAsync(0, Payload(1));
        await Write(Bank(f, 0), "unfinished-bank-copy"u8.ToArray());
        Check(!await IsV2(f));
        await FailClosed(f);
    }

    private static async Task InactivePartial()
    {
        await using var f = new Fixture();
        var sequence = await MakeV2(f);
        var selected = await SelectedBank(f, Payload(sequence));
        var inactive = selected == Bank(f, 0) ? Bank(f, 1) : Bank(f, 0);
        await Write(inactive, "unfinished-inactive-copy"u8.ToArray());
        var previousSwitchLength = new FileInfo(Switch(f)).Length;
        await using (var reopened = Open(f))
        {
            Check(!reopened.Recovery.Required);
            Equal(sequence, (await reopened.ReadLatestAsync())!.Sequence);
            for (var i = 0; i < 64 && new FileInfo(Switch(f)).Length == previousSwitchLength; i++)
                sequence = await Push(reopened, sequence);
            Check(new FileInfo(Switch(f)).Length > previousSwitchLength);
            Equal(inactive, await SelectedBank(f, Payload(sequence)));
            Check(!reopened.Recovery.Required);
        }
        await using var again = Open(f);
        Check(!again.Recovery.Required);
        Equal(sequence, (await again.ReadLatestAsync())!.Sequence);
    }

    private static async Task WriterExclusion()
    {
        await using var f = new Fixture();
        await using var owner = Open(f);
        await WriterDenied(f);
        var sequence = await UntilV2(owner, f);
        await WriterDenied(f);
        for (var i = 0; i < 24; i++) sequence = await Push(owner, sequence);
        await WriterDenied(f);
        Equal(sequence, (await owner.ReadLatestAsync())!.Sequence);
    }

    private static Task WriterDenied(Fixture f)
    {
        try
        {
            using var other = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException) { return Task.CompletedTask; }
        throw new InvalidOperationException("A second writer acquired the lifetime anchor.");
    }

    private static async Task SwitchCapacity()
    {
        await using var f = new Fixture();
        long sequence = 0;
        var rejected = false;
        await using (var storage = Open(f, switchLimit: 1024))
        {
            for (var i = 0; i < 512 && !rejected; i++)
            {
                try { sequence = await Push(storage, sequence); }
                catch (LedgerException error) when (error.Code == LedgerErrors.CapacityExceeded) { rejected = true; }
            }
            Check(rejected);
            Check(!storage.Recovery.Required);
            Check(await IsV2(f));
            Check(new FileInfo(Switch(f)).Length <= 1024);
            Equal(sequence, (await storage.ReadLatestAsync())!.Sequence);
            Bytes(Payload(sequence), (await storage.ReadLatestAsync())!.Payload);
        }
        await using var reopened = Open(f, switchLimit: 1024);
        Check(!reopened.Recovery.Required);
        Equal(sequence, (await reopened.ReadLatestAsync())!.Sequence);
        Bytes(Payload(sequence), (await reopened.ReadLatestAsync())!.Payload);
    }

    private static async Task BankCapacityRetry()
    {
        await using var f = new Fixture(); await using var storage = Open(f);
        long sequence = 0;
        for (int i = 0; i < 40; i++) sequence = await Push(storage, sequence);
        Check(await IsV2(f));
        await storage.AppendAsync(sequence, Payload(++sequence, 1000));
        var before = await Files(f);
        await Error(LedgerErrors.CapacityExceeded, () => storage.AppendAsync(sequence, Payload(sequence + 1, 1000)).AsTask());
        Check(!storage.Recovery.Required); await SameFiles(f, before);
        await storage.AppendAsync(sequence, Payload(sequence + 1, 100));
        Equal(sequence + 1, (await storage.ReadLatestAsync())!.Sequence);
    }

    private static async Task CheckpointCapacity()
    {
        await using var f = new Fixture();
        await using (var storage = Open(f))
        {
            await storage.AppendAsync(0, Payload(1, 1000));
            await Error(LedgerErrors.CapacityExceeded, () => storage.AppendAsync(1, Payload(2, 1000)).AsTask());
            Equal(1L, (await storage.ReadLatestAsync())!.Sequence);
            Bytes(Payload(1, 1000), (await storage.ReadLatestAsync())!.Payload);
        }
        await using var reopened = Open(f);
        Check(!reopened.Recovery.Required);
        Equal(1L, (await reopened.ReadLatestAsync())!.Sequence);
        Bytes(Payload(1, 1000), (await reopened.ReadLatestAsync())!.Payload);
    }

    private static async Task CancelBeforeMigration()
    {
        await using var f = new Fixture();
        await using var storage = Open(f);
        var sequence = await Push(storage, 0);
        var before = await Files(f);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Cancelled(() => storage.AppendAsync(sequence, Payload(sequence + 1), cancel.Token).AsTask());
        await SameFiles(f, before);
        Check(Paths(f).Skip(1).All(path => !File.Exists(path)));
        Check(!storage.Recovery.Required);
        Equal(sequence + 1, await Push(storage, sequence));
    }

    private static async Task CancelAfterMigration()
    {
        await using var f = new Fixture();
        await using var storage = Open(f);
        var sequence = await UntilV2(storage, f);
        var before = await Files(f);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Cancelled(() => storage.AppendAsync(sequence, Payload(sequence + 1), cancel.Token).AsTask());
        await SameFiles(f, before);
        Check(!storage.Recovery.Required);
        Equal(sequence + 1, await Push(storage, sequence));
    }

    private static async Task<string> SelectedBank(Fixture f, byte[] latestPayload)
    {
        var matches = new List<string>();
        foreach (var path in new[] { Bank(f, 0), Bank(f, 1) })
            if (Contains(await Read(path), latestPayload)) matches.Add(path);
        Equal(1, matches.Count);
        return matches.Single();
    }

    private static bool Contains(byte[] bytes, byte[] needle) => bytes.AsSpan().IndexOf(needle) >= 0;

    private static async Task FailClosed(Fixture f)
    {
        var before = await Files(f);
        try
        {
            await using var storage = Open(f);
            Check(storage.Recovery.Required);
            var latest = await storage.ReadLatestAsync();
            await Error(LedgerErrors.RecoveryRequired, () => storage.AppendAsync(latest?.Sequence ?? 0, Payload(999)).AsTask());
        }
        catch (Exception error) when (error is IOException or LedgerException)
        {
            // Missing or malformed initialization may reject opening entirely. It must not repair,
            // replace, or resume writing from an older physical copy.
        }
        await SameFiles(f, before);
    }

    private static async Task<Dictionary<string, byte[]>> Files(Fixture f)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Paths(f))
            if (File.Exists(path)) result.Add(path, await Read(path));
        return result;
    }

    private static async Task SameFiles(Fixture f, Dictionary<string, byte[]> expected)
    {
        var actual = await Files(f);
        Check(actual.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.Keys.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var (path, bytes) in expected) Bytes(bytes, actual[path]);
    }

    private static async Task<byte[]> Read(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Test fixture unexpectedly exceeded its bounded size.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static async Task Write(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(bytes);
        file.Flush(true);
    }

    private sealed class CompactionFault : IJournalCompactionFaultInjector
    {
        internal JournalCompactionStage? Stage;
        internal bool Hit;
        internal long Sequence;
        public void At(JournalCompactionStage stage, long sequence)
        {
            if (Stage != stage) return;
            Hit = true;
            Sequence = sequence;
            throw new InjectedFailure();
        }
    }

    private sealed class ExitAfterSwitch : IJournalCompactionFaultInjector
    {
        public void At(JournalCompactionStage stage, long sequence)
        {
            if (stage != JournalCompactionStage.SwitchAfterFlush) return;
            Console.Out.WriteLine(sequence);
            Console.Out.Flush();
            Environment.Exit(74);
        }
    }

    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation before any write.");
    }

    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (LedgerException error) { Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }

    private static void Bytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual));
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
