using Assistant.Core;

internal static partial class CommitmentTests
{
    internal static async Task<int> RunObservationOrderTests()
    {
        (string, Func<Task>)[] cases =
        [
            ("same_epoch_clock_rollback_terminal", OrderedRollback),
            ("same_sequence_and_older_sequence_do_not_change_state", OrderedDuplicate),
            ("higher_sequence_same_state_is_watermark_not_activity", OrderedWatermark),
            ("reopen_requires_new_connection_and_keeps_old_fence", OrderedRecovery),
            ("new_epoch_wins_and_late_old_epoch_cannot_return", OrderedEpochRace),
            ("unordered_cache_cannot_overwrite_ordered_source", OrderedUnstamped),
            ("foreign_identity_host_profile_and_source_fail_closed", OrderedScope),
            ("unknown_observation_does_not_claim_terminal", OrderedUnknown),
            ("same_registration_does_not_append", OrderedRegisterNoOp),
            ("schema_1_to_4_migrate_without_stamping_cache", OrderedLegacy),
            ("malformed_order_and_source_are_rejected_on_reopen", OrderedCorruption),
            ("stale_read_check_retains_attempt_without_faking_notification", OrderedCheck),
            ("read_event_before_check_completion_uses_same_exact_order", OrderedReadRace),
            ("terminal_read_event_before_check_completion_skips_exact_claim", OrderedTerminalReadRace),
            ("source_registry_missing_duplicate_or_downgraded_is_corrupt", OrderedRegistryCorruption),
        ];
        int failures = 0;
        foreach (var (name, run) in cases)
        {
            try { await run(); Console.WriteLine("PASS observation_order/" + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL observation_order/" + name + " " + error); }
        }
        Console.WriteLine($"Observation order: {cases.Length - failures}/{cases.Length} passed.");
        return failures;
    }

    private static Task<CommitmentObservationLease> RegisterOrder(DurableConversationLedger ledger,
        Guid? epoch = null, string key = "owned-test") => ledger.RegisterCommitmentObservationSourceAsync(
            Identity, key, Target.HostId, Target.ProfileId, new string('A', 64), epoch ?? Guid.NewGuid());
    private static CommitmentTaskObservation Ordered(F f, CommitmentObservationLease lease, long sequence,
        CommitmentTaskState state = CommitmentTaskState.Running, bool live = true) =>
        new(Target, state, f.Clock.UtcNow, null, true, true, "trusted_owned_test",
            Order: new(lease, sequence), IsCurrentSourceEvidence: live);

    private static async Task OrderedRollback()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "completed");
        var lease = await RegisterOrder(l); var before = f.Clock.UtcNow;
        await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 10));
        f.Clock.UtcNow -= TimeSpan.FromMinutes(10);
        Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 11, CommitmentTaskState.Completed))).Length);
        var saved = await l.ReadConversationAsync(Identity);
        Equal(f.Clock.UtcNow, saved.Subscriptions.Single().LastObservedAt);
        Equal(before, saved.Subscriptions.Single().LastActivityAt);
        Equal(11L, saved.Subscriptions.Single().LastObservationOrder!.Sequence);
        Equal(1, saved.NotificationDrafts.Length);
    }
    private static async Task OrderedDuplicate()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 4));
        var before = await l.ReadConversationDiagnosticsAsync(Identity);
        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 4, CommitmentTaskState.Failed))).Length);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 3, CommitmentTaskState.Failed))).Length);
        var after = await l.ReadConversationDiagnosticsAsync(Identity);
        Equal(before.Capacity.JournalSequence, after.Capacity.JournalSequence);
        Equal(CommitmentTaskState.Running, after.Conversation.Subscriptions.Single().LastState);
        Equal(0, after.Conversation.CommitmentBudgetReservations.Length);
    }
    private static async Task OrderedWatermark()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
        var first = (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single();
        f.Clock.UtcNow += TimeSpan.FromSeconds(20);
        await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 2));
        var next = (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single();
        Equal(f.Clock.UtcNow, next.LastObservedAt); Equal(first.LastActivityAt, next.LastActivityAt);
        Equal(first.NextCheckAt, next.NextCheckAt); Equal(2L, next.LastObservationOrder!.Sequence);
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task OrderedRecovery()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "completed");
        var old = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, old, 20));
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, old, 21, CommitmentTaskState.Completed))).Length);
        await Error(LedgerErrors.InvalidTransition, () => RegisterOrder(l, old.ConnectionEpoch));
        f.Clock.UtcNow -= TimeSpan.FromMinutes(10); var current = await RegisterOrder(l);
        Equal(old.Generation + 1, current.Generation);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, current, 1, CommitmentTaskState.Completed, false))).Length);
        Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, current, 1, CommitmentTaskState.Completed))).Length);
    }
    private static async Task OrderedEpochRace()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var old = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, old, 80));
        var current = await RegisterOrder(l);
        await Task.WhenAll(l.ObserveCommitmentTaskAsync(Identity, Ordered(f, old, 100, CommitmentTaskState.Failed)),
            l.ObserveCommitmentTaskAsync(Identity, Ordered(f, current, 1)));
        var snapshot = await l.GetCommitmentsAsync(Identity);
        Equal(0, snapshot.Runs.Length); Equal(current, snapshot.Subscriptions.Single().LastObservationOrder!.Lease);
        Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, current, 2, CommitmentTaskState.Failed))).Length);
    }
    private static async Task OrderedUnstamped()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 3));
        f.Clock.UtcNow += TimeSpan.FromSeconds(10);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Failed))).Length);
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task OrderedScope()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
        var lease = await RegisterOrder(l);
        foreach (var invalid in new[] { lease with { HostId = "foreign" }, lease with { ProfileId = "foreign" },
            lease with { Identity = new("other", "other", "other") }, lease with { SourceKey = "foreign" },
            lease with { ConnectionEpoch = Guid.Empty } })
            Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, invalid, 1, CommitmentTaskState.Failed))).Length);
        await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
        var foreignSource = await RegisterOrder(l, key: "another-source");
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, foreignSource, 2, CommitmentTaskState.Failed))).Length);
    }
    private static async Task OrderedUnknown()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "completed");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 2, CommitmentTaskState.Unknown))).Length);
        Equal(1L, (await l.GetCommitmentsAsync(Identity)).Subscriptions.Single().LastObservationOrder!.Sequence);
    }
    private static async Task OrderedRegisterNoOp()
    {
        await using var f = new F(); var l = await f.Open(); var lease = await RegisterOrder(l);
        var before = await l.ReadConversationDiagnosticsAsync(Identity);
        Equal(lease, await RegisterOrder(l, lease.ConnectionEpoch));
        Equal(before.Capacity.JournalSequence, (await l.ReadConversationDiagnosticsAsync(Identity)).Capacity.JournalSequence);
    }
    private static async Task OrderedLegacy()
    {
        for (int version = 1; version <= 4; version++)
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "completed");
            await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Running));
            await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                root["schemaVersion"] = version; root.AsObject().Remove("commitmentObservationSources");
                if (version < 3)
                {
                    root["outputDependenciesRequired"] = false;
                    foreach (var e in root["events"]!.AsArray())
                    { e!.AsObject().Remove("outputDependencies"); e.AsObject().Remove("relatedInvocationId"); }
                    foreach (var g in root["generations"]!.AsArray())
                    { g!.AsObject().Remove("generationContext"); g.AsObject().Remove("contextTransitions"); g.AsObject().Remove("dependencyInvalidationReason"); }
                    foreach (var i in root["toolInvocations"]!.AsArray()) i!.AsObject().Remove("generationContext");
                }
            });
            l = await f.Open(); var lease = await RegisterOrder(l); f.Clock.UtcNow -= TimeSpan.FromMinutes(1);
            Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1, CommitmentTaskState.Completed, false))).Length);
            Equal(1, (await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1, CommitmentTaskState.Completed))).Length);
        }
    }
    private static async Task OrderedCorruption()
    {
        foreach (string field in new[] { "connectionEpoch", "generation", "configurationFingerprint", "sequence", "sourceKey" })
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
            var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
            await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                var order = root["subscriptions"]![0]!["lastObservationOrder"]!;
                if (field == "sequence") order["sequence"] = 0;
                else order["lease"]!.AsObject().Remove(field);
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task OrderedRegistryCorruption()
    {
        foreach (string damage in new[] { "missing", "null", "duplicate", "downgraded", "wrong-generation" })
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "failed");
            var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
            await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                var sources = root["commitmentObservationSources"]!.AsArray();
                switch (damage)
                {
                    case "missing": sources[0]!["lease"]!.AsObject().Remove("configurationFingerprint"); break;
                    case "null": sources[0]!["lease"] = null; break;
                    case "duplicate": sources.Add(sources[0]!.DeepClone()); break;
                    case "downgraded": root["schemaVersion"] = 4; break;
                    case "wrong-generation": root["subscriptions"]![0]!["lastObservationOrder"]!["lease"]!["generation"] = 2; break;
                }
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task OrderedTerminalReadRace()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
        f.Clock.UtcNow += TimeSpan.FromSeconds(61);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        var terminal = Ordered(f, lease, 2, CommitmentTaskState.Completed);
        await l.ObserveCommitmentTaskAsync(Identity, terminal);
        Equal(1, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(CommitmentRunStage.Skipped, (await l.CompleteCommitmentCheckAsync(claim, terminal)).Stage);
        Equal(0, (await l.GetCommitmentBudgetStatusAsync(Identity)).ReadChecksInFlight);
        Equal(0, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Length);
        var beforeDuplicate = await l.ReadConversationDiagnosticsAsync(Identity);
        Equal(CommitmentRunStage.Skipped, (await l.CompleteCommitmentCheckAsync(claim, terminal)).Stage);
        Equal(beforeDuplicate.Capacity.JournalSequence, (await l.ReadConversationDiagnosticsAsync(Identity)).Capacity.JournalSequence);
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim with { ClaimId = Guid.NewGuid() }, terminal));
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim, terminal with { State = CommitmentTaskState.Failed }));
        await Error(LedgerErrors.InvalidTransition, () => l.CompleteCommitmentCheckAsync(claim, terminal with { Order = terminal.Order! with { Sequence = 3 } }));
        Equal(beforeDuplicate.Capacity.JournalSequence, (await l.ReadConversationDiagnosticsAsync(Identity)).Capacity.JournalSequence);
        await l.ReleaseCommitmentCheckAsync(claim);
        Equal(TrackedWorkStage.Completed, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
    }
    private static async Task OrderedReadRace()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 1));
        f.Clock.UtcNow += TimeSpan.FromSeconds(61);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        var fresh = Ordered(f, lease, 2);
        await l.ObserveCommitmentTaskAsync(Identity, fresh);
        Equal(CommitmentRunStage.NotificationPending, (await l.CompleteCommitmentCheckAsync(claim, fresh)).Stage);
        Equal(1, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Length);
    }
    private static async Task OrderedCheck()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "noActivity");
        var lease = await RegisterOrder(l); await l.ObserveCommitmentTaskAsync(Identity, Ordered(f, lease, 2));
        f.Clock.UtcNow += TimeSpan.FromSeconds(61);
        var run = (await l.AdvanceCommitmentsAsync(Identity)).Single();
        var claim = await l.BeginCommitmentCheckAsync(Identity, run.Id);
        Equal(CommitmentRunStage.PendingCheck,
            (await l.CompleteCommitmentCheckAsync(claim, Ordered(f, lease, 1, CommitmentTaskState.Failed))).Stage);
        var saved = await l.GetCommitmentsAsync(Identity);
        Equal(1, saved.Runs.Single().CheckAttempts); Equal(CommitmentTaskState.Running, saved.Subscriptions.Single().LastState);
        Equal(0, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Length);
    }
}
