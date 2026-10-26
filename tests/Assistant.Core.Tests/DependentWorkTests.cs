using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Assistant.Core;

internal static partial class DependentWorkTests
{
    private static readonly ConversationIdentity Identity = new("dependency-user", "binding", "conversation");
    private static readonly TaskReference Target = new("local", "target", "baseline");
    private static readonly TaskReference Predecessor = new("local", "upstream", "original-turn");
    private static readonly string Fingerprint = new('a', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("registration_is_local_and_receipt_survives_new_input_restart", Registration),
        ("registration_requires_literal_current_authorized_instruction", Authorization),
        ("all_predecessors_and_available_target_are_required", AllConditions),
        ("only_current_ordered_connected_evidence_can_release_work", EvidenceBoundary),
        ("exact_task_and_configuration_change_requires_decision", ChangedBinding),
        ("new_unknown_observation_blocks_older_success", EvidenceOrdering),
        ("failed_predecessor_is_not_success", FailedPredecessor),
        ("dispatch_claim_is_durable_and_unique", Claim),
        ("before_send_rechecks_cancel_deadline_and_connection", BeforeSend),
        ("accepted_new_turn_is_not_completed_work", Completion),
        ("new_result_observation_blocks_out_of_order_completion", CompletionOrdering),
        ("unknown_dispatch_never_replays_but_exact_late_ack_is_retained", UnknownDispatch),
        ("restart_during_dispatch_stays_unknown", DispatchRecovery),
        ("paused_or_cancelled_inflight_ack_does_not_reactivate_work", InflightChange),
        ("known_unsent_attempt_can_retry_without_old_receipt_rebinding", KnownUnsent),
        ("dependency_chain_waits_for_verified_result_turn", WorkChain),
        ("changed_parent_version_requires_fresh_decision", ChangedParent),
        ("cyclic_update_is_atomic_and_cross_identity_reference_rejected", Cycles),
        ("pause_resume_update_cancel_are_versioned", Management),
        ("expired_work_requires_explicit_future_deadline_to_reopen", RenewDeadline),
        ("deadline_edits_cannot_reopen_dispatched_or_unknown_work", NoRedispatchByDeadline),
        ("same_task_chain_freezes_each_actual_predecessor_result_turn", SameTaskChain),
        ("continuation_requires_exact_parent_target_and_preserves_anchor_on_edit", ContinuationAnchor),
        ("fixed_target_and_unrelated_new_turn_never_become_implicit_continuations", NoImplicitContinuation),
        ("continuation_unknown_restart_retains_frozen_target_and_exact_late_ack", ContinuationRecovery),
        ("schema7_reads_without_continuation_and_upgrades_only_on_explicit_registration", ContinuationSchema),
        ("tampered_continuation_mode_or_frozen_target_is_rejected", ContinuationCorruption),
        ("terminal_notice_is_deduplicated_across_reopen", NoticeRecovery),
        ("quiet_policy_holds_notice_without_spending_and_releases_pages", NoticeQuiet),
        ("cancel_and_renew_retire_pending_notice_and_fence_reserved_send", NoticeChange),
        ("dependent_notices_share_durable_budget_and_do_not_repeat_execution", NoticeBudget),
        ("unknown_notice_is_not_replayed_and_exact_late_receipt_survives_cancel", NoticeUnknown),
        ("rejected_notice_recovers_individually_without_catchup_bypass", NoticeCatchUp),
        ("notification_commit_fault_does_not_duplicate_captured_event", NoticeFault),
        ("notice_and_policy_tampering_rejects_storage", NoticeCorruption),
        ("legacy_work_does_not_broadcast_historical_failures_on_upgrade", NoticeLegacy),
        ("recovery_requires_actual_acceptance_and_completion_milestones", RecoveryMilestones),
        ("unaccepted_warning_does_not_create_recovery_chatter", RecoveryUnseenWarning),
        ("quiet_recovery_reports_only_latest_milestone", RecoveryQuietCoalescing),
        ("pause_resume_does_not_repeat_same_attempt_recovery", RecoveryPauseCancel),
        ("recovery_notice_commit_fault_preserves_exact_event", RecoveryFault),
        ("recovery_proof_and_schema_cannot_be_substituted", RecoveryCorruption),
        ("expired_renewal_recovers_and_a_new_failure_is_a_distinct_event", RecoveryExpiryAndNewFailure),
        ("schema9_history_is_silent_until_new_verified_progress", RecoveryLegacyBaseline),
        ("command_condition_requires_structured_exact_success", CommandCondition),
        ("last_matching_command_attempt_controls_success", CommandLastAttempt),
        ("newer_incomplete_command_read_blocks_old_success", CommandOrdering),
        ("command_requirement_keeps_literal_authorization_and_version", CommandAuthorization),
        ("command_work_chain_checks_actual_result_turn", CommandWorkChain),
        ("command_dispatch_recovery_preserves_proof_without_replay", CommandRecovery),
        ("command_proof_and_format_tampering_is_rejected", CommandCorruption),
        ("committed_registration_overrides_late_tool_failure", LateFailure),
        ("registration_write_fault_recovers_only_committed_state", RegistrationFault),
        ("dispatch_write_fault_never_replays_committed_claim", DispatchFault),
        ("schema6_without_dependency_fields_remains_readable", OldSchema),
        ("broken_authorization_or_attempt_link_is_read_only", Corruption)
    ];
    internal static async Task<int> Run()
    {
        int failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS dependent_work_" + name); }
            catch (Exception error) { failed++; Console.WriteLine($"FAIL dependent_work_{name}: {error}"); }
        }
        Console.WriteLine($"DEPENDENT WORK TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    private static async Task Registration()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f, "schedule_dependency", Spec(f), "finish documentation");
        var result = await l.ApplyDependentWorkToolAsync(context);
        Check(!result.Facts.GetProperty("taskDispatched").GetBoolean());
        Check(!result.Facts.GetProperty("completionVerified").GetBoolean());
        var work = (await l.GetDependentWorkAsync(Identity)).WorkItems.Single();
        Equal(DependentWorkStage.Waiting, work.Stage);
        Equal(0, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
        await l.ReceiveAsync(Identity, "next", "new request", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await l.DisposeAsync(); l = await f.Open();

        Equal(work.Id, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().Id);
        Equal(work.Id, (await l.ApplyDependentWorkToolAsync(context)).Facts.GetProperty("workItemId").GetGuid());
        var snapshot = await l.ReadConversationAsync(Identity);
        Equal(OperationStage.Completed, snapshot.Operations.Single().Stage);
        Equal(ToolInvocationStage.Completed, snapshot.ToolInvocations.Single().Stage);
        Equal(1, snapshot.Events.Count(e => e.RelatedInvocationId == context.InvocationId));
    }
    private static async Task Authorization()
    {
        await using var f = new F(); var l = await f.Open();
        var context = await Begin(l, f, "schedule_dependency", Spec(f), "inspect only");
        await Throws<ArgumentException>(() => l.ApplyDependentWorkToolAsync(context));
        Equal(0, (await l.GetDependentWorkAsync(Identity)).WorkItems.Length);
        context = await Begin(l, f, "schedule_dependency", Spec(f), "finish documentation");
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyDependentWorkToolAsync(context with { IdempotencyKey = "other" }));
        await Error(LedgerErrors.AuthorizationRequired, () => l.ApplyDependentWorkToolAsync(context with { InputWorkId = Guid.NewGuid() }));
        await l.ReceiveAsync(Identity, "new", "cancel that request", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await Error(LedgerErrors.StaleRevision, () => l.ApplyDependentWorkToolAsync(context));
        Equal(0, (await l.GetDependentWorkAsync(Identity)).WorkItems.Length);
    }
    private static async Task AllConditions()
    {
        await using var f = new F(); var l = await f.Open(); var extra = Predecessor with { ThreadId = "other" };
        var w = await Create(l, f, requirements: [new(Predecessor, Fingerprint), new(extra, Fingerprint)]);
        var source = await Source(l); var evidence = Evidence(f, source).Add(Proof(f, source, extra, CommitmentTaskState.Running));
        Equal("conditions_not_met", (await Evaluate(l, w, evidence, true)).Code);
        evidence = evidence.SetItem(2, Proof(f, source, extra, CommitmentTaskState.Completed, 2));
        evidence = evidence.SetItem(0, evidence[0] with { CanStartTurn = false, Observation = evidence[0].Observation with { Order = new(source, 2) } });
        Check(!(await Evaluate(l, w, evidence, true)).Ready);
        evidence = evidence.SetItem(0, Proof(f, source, Target, CommitmentTaskState.Completed, 3, true));
        var ready = await Evaluate(l, w, evidence);
        Check(ready.Ready && ready.Lease == null);
        Equal(0, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
        evidence = evidence.SetItem(0, Proof(f, source, Target, CommitmentTaskState.Unknown, 4, true));
        Check(!(await Evaluate(l, w, evidence, true)).Ready);
        evidence = evidence.SetItem(0, Proof(f, source, Target, CommitmentTaskState.Running, 5, true));
        Check(!(await Evaluate(l, w, evidence, true)).Ready);
    }
    private static async Task EvidenceBoundary()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var good = Evidence(f, source);
        foreach (var invalid in new[] {
            good[1].Observation with { Order = null }, good[1].Observation with { IsCurrentSourceEvidence = false },
            good[1].Observation with { Connected = false }, good[1].Observation with { Fresh = false },
            good[1].Observation with { ObservedAt = f.Clock.UtcNow.AddMinutes(-6) },
            good[1].Observation with { Order = new(source with { ConnectionEpoch = Guid.NewGuid() }, 1) } })
            Equal("evidence_not_current", (await Evaluate(l, w, good.SetItem(1, new(invalid)), true)).Code);
        Equal("evidence_incomplete", (await Evaluate(l, w, [good[0]], true)).Code);
        Equal(0, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
        await l.DisposeAsync(); l = await f.Open();
        Equal("evidence_not_current", (await Evaluate(l, w, good, true)).Code);
        var current = await Source(l);
        Check((await Evaluate(l, w, Evidence(f, current), true)).Lease != null);
    }
    private static async Task ChangedBinding()
    {
        foreach (bool configuration in new[] { false, true })
        {
            await using var f = new F(); var l = await f.Open(); var w = await Create(l, f);
            var source = await Source(l, configuration ? new('b', 64) : Fingerprint);
            var evidence = Evidence(f, source);
            if (!configuration) evidence = evidence.SetItem(1, Proof(f, source, Predecessor with { TurnId = "different" }, CommitmentTaskState.Completed));
            var result = await Evaluate(l, w, evidence, true);
            Equal(DependentWorkStage.NeedsDecision, result.Work.Stage); Check(result.Lease == null);
            await l.DisposeAsync(); l = await f.Open();
        }
    }
    private static async Task EvidenceOrdering()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var evidence = Evidence(f, source, 3).SetItem(1, Proof(f, source, Predecessor, CommitmentTaskState.Unknown, 3));
        Check(!(await Evaluate(l, w, evidence, true)).Ready);
        Equal("evidence_out_of_order", (await Evaluate(l, w, Evidence(f, source, 2), true)).Code);
        Equal("evidence_out_of_order", (await Evaluate(l, w, Evidence(f, source, 3), true)).Code);
        await l.DisposeAsync(); l = await f.Open(); source = await Source(l);
        Check((await Evaluate(l, w, Evidence(f, source), true)).Ready);
    }
    private static async Task FailedPredecessor()
    {
        foreach (var terminal in new[] { CommitmentTaskState.Failed, CommitmentTaskState.Interrupted })
        {
            await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
            var result = await Evaluate(l, w, Evidence(f, source).SetItem(1, Proof(f, source, Predecessor, terminal)), true);
            Equal(DependentWorkStage.NeedsDecision, result.Work.Stage); Check(result.Lease == null);
        }
    }
    private static async Task Claim()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Evaluate(l, w, Evidence(f, source), true)));
        Equal(1, attempts.Count(a => a.Lease != null));
        var lease = attempts.Single(a => a.Lease != null).Lease!;
        Check(await l.IsDependentDispatchCurrentAsync(lease));
        Equal(1, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
        await Error(LedgerErrors.Conflict, () => l.FinishDependentDispatchAsync(lease with { Instructions = "other" }, DependentDispatchStage.Unknown, "uncertain"));
    }
    private static async Task BeforeSend()
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
            var lease = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
            if (mode == 0) await Change(l, f, w, "cancel");
            if (mode == 1) f.Clock.UtcNow += TimeSpan.FromHours(2);
            if (mode == 2) await Source(l);
            Check(!await l.IsDependentDispatchCurrentAsync(lease));
        }
    }
    private static async Task Completion()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var lease = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
        foreach (var wrong in new[] { Target, Target with { ThreadId = "wrong", TurnId = "new" }, Target with { ProfileId = "wrong", TurnId = "new" } })
            await Throws<ArgumentException>(() => l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", wrong));
        var actual = Target with { TurnId = "actual-new-turn" };
        w = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", actual);
        Equal(DependentWorkStage.Running, w.Stage); Check(w.Completion == null);
        Equal(w, await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", actual));
        foreach (var invalid in new[] { Proof(f, source, Target, CommitmentTaskState.Completed, 2).Observation,
            Proof(f, source, actual, CommitmentTaskState.Running, 2).Observation,
            Proof(f, source, actual, CommitmentTaskState.Completed, 2).Observation with { Order = null } })
            Equal(DependentWorkStage.Running, (await l.ObserveDependentCompletionAsync(Identity, w.Id, invalid)).Stage);
        w = await l.ObserveDependentCompletionAsync(Identity, w.Id, Proof(f, source, actual, CommitmentTaskState.Completed, 3).Observation);
        Equal(DependentWorkStage.Completed, w.Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(DependentWorkStage.Completed, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().Stage);
    }
    private static async Task UnknownDispatch()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var lease = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
        w = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Unknown, "no acknowledgement");
        Check(!(await Evaluate(l, w, Evidence(f, source), true)).Ready);
        await Error(LedgerErrors.InvalidTransition, () => l.FinishDependentDispatchAsync(lease, DependentDispatchStage.NotDispatched, "guess"));
        w = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "late accepted", Target with { TurnId = "late" });
        Equal(DependentWorkStage.Running, w.Stage);
        Equal(1, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
    }
    private static async Task CompletionOrdering()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var child = await Create(l, f, requirements: [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        var source = await Source(l); var lease = (await Evaluate(l, parent, Evidence(f, source), true)).Lease!;
        var actual = Target with { TurnId = "result" };
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", actual);
        var tooEarly = Proof(f, source, actual, CommitmentTaskState.Completed, 2).Observation with { ObservedAt = f.Clock.UtcNow.AddSeconds(-1) };
        Check((await l.ObserveDependentCompletionAsync(Identity, parent.Id, tooEarly)).Completion == null);
        await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Unknown, 4).Observation);
        foreach (long sequence in new long[] { 3, 4 })
            Check((await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Completed, sequence).Observation)).Completion == null);
        Equal("predecessor_not_completed", (await Evaluate(l, child, [], true)).Code);
        await l.DisposeAsync(); l = await f.Open();
        Check((await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Completed, 5).Observation)).Completion == null);
        source = await Source(l);
        Equal(DependentWorkStage.Completed, (await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Completed).Observation)).Stage);
    }
    private static async Task DispatchRecovery()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var lease = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
        await l.DisposeAsync(); l = await f.Open();
        var snapshot = await l.GetDependentWorkAsync(Identity); w = snapshot.WorkItems.Single();
        Equal(DependentWorkStage.Unknown, w.Stage); Equal(DependentDispatchStage.Unknown, snapshot.Attempts.Single().Stage);
        source = await Source(l); Check(!(await Evaluate(l, w, Evidence(f, source), true)).Ready);
        Check(!await l.IsDependentDispatchCurrentAsync(lease));
        Equal(DependentWorkStage.Running, (await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "exact late ack", Target with { TurnId = "late" })).Stage);
    }
    private static async Task InflightChange()
    {
        foreach (string action in new[] { "pause", "cancel" })
        {
            await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
            var lease = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
            w = await Change(l, f, w, action); var stage = w.Stage;
            var actual = Target with { TurnId = "sent-before-change" };
            w = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "late accepted", actual);
            Equal(stage, w.Stage);
            w = await l.ObserveDependentCompletionAsync(Identity, w.Id, Proof(f, source, actual, CommitmentTaskState.Completed, 2).Observation);
            Equal(stage, w.Stage); Check(w.Completion != null);
            if (action == "pause") Equal(DependentWorkStage.Completed, (await Change(l, f, w, "resume")).Stage);
            await l.DisposeAsync(); l = await f.Open();
        }
    }
    private static async Task KnownUnsent()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
        var first = (await Evaluate(l, w, Evidence(f, source), true)).Lease!;
        w = await l.FinishDependentDispatchAsync(first, DependentDispatchStage.NotDispatched, "before-send gate refused");
        var second = (await Evaluate(l, w, Evidence(f, source, 2), true)).Lease!;
        Check(first.AttemptId != second.AttemptId);
        await Error(LedgerErrors.Conflict, () => l.FinishDependentDispatchAsync(first, DependentDispatchStage.Accepted, "wrong late ack", Target with { TurnId = "other" }));
        await l.FinishDependentDispatchAsync(second, DependentDispatchStage.Rejected, "backend refused");
        await l.DisposeAsync(); l = await f.Open();
        Equal(DependentWorkStage.NeedsDecision, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().Stage);
    }
    private static async Task WorkChain()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var child = await Create(l, f, requirements: [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        var source = await Source(l);
        Equal("predecessor_not_completed", (await Evaluate(l, child, [], true)).Code);
        var lease = (await Evaluate(l, parent, Evidence(f, source), true)).Lease!;
        var actual = Target with { TurnId = "parent-result" };
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", actual);
        Equal("predecessor_not_completed", (await Evaluate(l, child, [], true)).Code);
        await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Completed, 2).Observation);
        var evidence = Evidence(f, source, 3).SetItem(1, Proof(f, source, actual, CommitmentTaskState.Completed, 3));
        Check((await Evaluate(l, child, evidence, true)).Lease != null);
        await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task ChangedParent()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var child = await Create(l, f, requirements: [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        await Change(l, f, parent, "pause");
        Equal(DependentWorkStage.NeedsDecision, (await Evaluate(l, child, [], true)).Work.Stage);
        Equal(DependentWorkStage.Cancelled, (await Change(l, f, child, "cancel")).Stage);
        await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task Cycles()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var child = await Create(l, f, requirements: [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        await Throws<ArgumentException>(() => Change(l, f, parent, "update", requirements: [new(WorkItemId: child.Id, WorkVersion: child.Version)]));
        Equal(1L, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single(w => w.Id == parent.Id).Version);
        Equal(0, (await l.GetDependentWorkAsync(Identity with { UserId = "other" })).WorkItems.Length);
        await Error(LedgerErrors.NotFound, () => l.EvaluateDependentWorkAsync(Identity with { UserId = "other" }, parent.Id, parent.Version, []));
        await Error(LedgerErrors.NotFound, () => Create(l, f, requirements: [new(WorkItemId: Guid.NewGuid(), WorkVersion: 1)]));
        await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task Management()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var original = w;
        w = await Change(l, f, w, "pause"); Equal(DependentWorkStage.Paused, w.Stage);
        await Error(LedgerErrors.Conflict, () => Change(l, f, original, "resume"));
        w = await Change(l, f, w, "update", "write a summary"); Equal(DependentWorkStage.Paused, w.Stage);
        Equal("write a summary", w.Instructions);
        w = await Change(l, f, w, "resume"); Equal(DependentWorkStage.Waiting, w.Stage);
        f.Clock.UtcNow += TimeSpan.FromHours(2);
        w = (await Evaluate(l, w, [], true)).Work; Equal(DependentWorkStage.Expired, w.Stage);
        w = await Change(l, f, w, "cancel"); Equal(DependentWorkStage.Cancelled, w.Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(5L, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().Version);
    }
    private static async Task LateFailure()
    {
        await using var f = new F(); var l = await f.Open(); var context = await Begin(l, f, "schedule_dependency", Spec(f), "finish documentation");
        await l.ApplyDependentWorkToolAsync(context);
        var completed = await l.FinishToolInvocationAsync(Identity, context.InvocationId, ToolInvocationStage.Unknown, "{\"error\":\"late timeout\"}", OperationStage.Unknown);
        Equal(ToolInvocationStage.Completed, completed.Stage);
        Check(completed.ResultJson!.Contains("workItemId"));
        await l.DisposeAsync(); l = await f.Open();
    }
    private static Task<DependentWorkItem> ContinueAfter(DurableConversationLedger l, F f, DependentWorkItem parent) =>
        Execute(l, f, "schedule_dependency", Spec(f, [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]) with { ContinueAfterWorkId = parent.Id },
            "after the named work completes on the same task, finish documentation");
    private static ImmutableArray<DependencyEvidence> ContinuationEvidence(F f, CommitmentObservationLease source, TaskReference target, long sequence) =>
        [Proof(f, source, target, CommitmentTaskState.Completed, sequence, true), Proof(f, source, target, CommitmentTaskState.Completed, sequence)];
    private static async Task<DependentWorkItem> CompleteParent(DurableConversationLedger l, F f, DependentWorkItem parent, CommitmentObservationLease source)
    {
        var lease = (await Evaluate(l, parent, Evidence(f, source), true)).Lease!;
        var actual = Target with { TurnId = "parent-result" };
        parent = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", actual);
        return await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, actual, CommitmentTaskState.Completed, 2).Observation);
    }
    private static async Task SameTaskChain()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var child = await ContinueAfter(l, f, parent); var next = await ContinueAfter(l, f, child);
        Check(!(await Evaluate(l, child, [], true)).Ready);
        var source = await Source(l); parent = await CompleteParent(l, f, parent, source);
        var plan = await Evaluate(l, child, []);
        Check(plan.EvidenceTargets.SequenceEqual(new[] { parent.ResultTask!, parent.ResultTask! }));
        var lease = (await Evaluate(l, child, ContinuationEvidence(f, source, parent.ResultTask!, 3), true)).Lease!;
        Equal(parent.ResultTask, lease.Target); Equal(Target, child.Target); Check(await l.IsDependentDispatchCurrentAsync(lease));
        var childResult = Target with { TurnId = "child-result" };
        await Throws<ArgumentException>(() => l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "old turn", parent.ResultTask));
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", childResult);
        Check(!(await Evaluate(l, next, [], true)).Ready);
        await l.ObserveDependentCompletionAsync(Identity, child.Id, Proof(f, source, childResult, CommitmentTaskState.Completed, 4).Observation);
        var lastLease = (await Evaluate(l, next, ContinuationEvidence(f, source, childResult, 5), true)).Lease!;
        Equal(childResult, lastLease.Target); Equal(Target, next.Target);
        await l.FinishDependentDispatchAsync(lastLease, DependentDispatchStage.Accepted, "accepted", Target with { TurnId = "third-result" });
        await l.DisposeAsync(); l = await f.Open();
        var saved = await l.GetDependentWorkAsync(Identity);
        Equal(3, saved.Attempts.Length); Equal(childResult, saved.Attempts.Single(a => a.WorkItemId == next.Id).Evidence[0].Observation.Task);
    }
    private static async Task ContinuationAnchor()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f); var child = await ContinueAfter(l, f, parent);
        await Throws<ArgumentException>(() => Execute(l, f, "schedule_dependency", Spec(f) with { ContinueAfterWorkId = parent.Id }, "finish documentation"));
        var spec = Spec(f, [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]) with { ContinueAfterWorkId = parent.Id };
        await Throws<ArgumentException>(() => Execute(l, f, "schedule_dependency", spec with { Target = Target with { ThreadId = "different" } }, "finish documentation"));
        await Throws<ArgumentException>(() => Execute(l, f, "schedule_dependency", spec with { BindingFingerprint = new string('b', 64) }, "finish documentation"));
        await Throws<ArgumentException>(() => Change(l, f, child, "update", requirements: [new(Predecessor, Fingerprint)]));
        Equal(child, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single(w => w.Id == child.Id));
        parent = await Change(l, f, parent, "update", "new parent instructions");
        Equal(DependentWorkStage.NeedsDecision, (await Evaluate(l, child, [], true)).Work.Stage);
        child = (await l.GetDependentWorkAsync(Identity)).WorkItems.Single(w => w.Id == child.Id);
        child = await Change(l, f, child, "update", requirements: [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        Equal(parent.Id, child.ContinueAfterWorkId); Equal(DependentWorkStage.Waiting, child.Stage);
        await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task NoImplicitContinuation()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f);
        var fixedTarget = await Create(l, f, [new(WorkItemId: parent.Id, WorkVersion: parent.Version)]);
        var explicitTarget = await ContinueAfter(l, f, parent); var source = await Source(l);
        parent = await CompleteParent(l, f, parent, source);
        Equal(DependentWorkStage.NeedsDecision, (await Evaluate(l, fixedTarget, ContinuationEvidence(f, source, parent.ResultTask!, 3), true)).Work.Stage);
        var unrelated = parent.ResultTask! with { TurnId = "unrelated-later-turn" };
        Equal(DependentWorkStage.NeedsDecision, (await Evaluate(l, explicitTarget, ContinuationEvidence(f, source, unrelated, 4), true)).Work.Stage);
        Equal(1, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
    }
    private static async Task ContinuationRecovery()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f); var child = await ContinueAfter(l, f, parent);
        var source = await Source(l); parent = await CompleteParent(l, f, parent, source);
        var lease = (await Evaluate(l, child, ContinuationEvidence(f, source, parent.ResultTask!, 3), true)).Lease!;
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Unknown, "lost acknowledgement");
        await l.DisposeAsync(); l = await f.Open();
        child = (await l.GetDependentWorkAsync(Identity)).WorkItems.Single(w => w.Id == child.Id);
        Equal(DependentWorkStage.Unknown, child.Stage); Check((await Evaluate(l, child, [], true)).Lease == null);
        await Throws<LedgerException>(() => l.FinishDependentDispatchAsync(lease with { Target = Target }, DependentDispatchStage.Accepted, "wrong", Target with { TurnId = "late" }));
        var actual = Target with { TurnId = "late-result" };
        child = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "exact late ack", actual);
        var fresh = await Source(l);
        child = await l.ObserveDependentCompletionAsync(Identity, child.Id, Proof(f, fresh, actual, CommitmentTaskState.Completed, 1).Observation);
        Equal(DependentWorkStage.Completed, child.Stage); Equal(2, (await l.GetDependentWorkAsync(Identity)).Attempts.Length);
        await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task ContinuationSchema()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f); await l.DisposeAsync();
        await f.Rewrite(root => { Equal(7, root["schemaVersion"]!.GetValue<int>()); foreach (var w in root["dependentWork"]!.AsArray()) w!.AsObject().Remove("continueAfterWorkId"); });
        l = await f.Open(); Equal(null, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().ContinueAfterWorkId);
        await ContinueAfter(l, f, parent); await l.DisposeAsync();
        await f.Rewrite(root => Equal(8, root["schemaVersion"]!.GetValue<int>()));
        l = await f.Open(); Equal(2, (await l.GetDependentWorkAsync(Identity)).WorkItems.Length);
    }
    private static async Task ContinuationCorruption()
    {
        for (int mode = 0; mode < 2; mode++)
        {
            await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f); var child = await ContinueAfter(l, f, parent);
            var source = await Source(l); parent = await CompleteParent(l, f, parent, source);
            await Evaluate(l, child, ContinuationEvidence(f, source, parent.ResultTask!, 3), true); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                if (mode == 0) root["dependentWork"]![1]!["continueAfterWorkId"] = null;
                else root["dependentAttempts"]![1]!["evidence"]![0]!["observation"]!["task"]!["turnId"] = "unrelated";
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task RenewDeadline()
    {
        await using var f = new F(); var l = await f.Open(); var original = await Create(l, f);
        var paused = await Change(l, f, original, "pause");
        string future = f.Clock.UtcNow.AddHours(3).ToString("O");
        var work = await Change(l, f, paused, "update", deadline: future);
        Equal(DependentWorkStage.Paused, work.Stage); Equal(original.Instructions, work.Instructions);
        Check(work.Requirements.SequenceEqual(original.Requirements));
        work = await Change(l, f, work, "resume");
        f.Clock.UtcNow += TimeSpan.FromHours(4);
        work = (await Evaluate(l, work, [], false)).Work; Equal(DependentWorkStage.Expired, work.Stage);
        await Throws<ArgumentException>(() => Change(l, f, work, "update", "new instructions"));
        await Throws<ArgumentException>(() => Change(l, f, work, "update", deadline: future));
        var renewed = f.Clock.UtcNow.AddHours(2);
        work = await Change(l, f, work, "update", deadline: renewed.ToString("O"));
        Equal(DependentWorkStage.Waiting, work.Stage); Equal(renewed, work.DeadlineUtc); Equal(original.Target, work.Target);
        Check((await l.GetDependentWorkAsync(Identity)).Attempts.IsEmpty);
        await l.DisposeAsync(); l = await f.Open();
        var restored = (await l.GetDependentWorkAsync(Identity)).WorkItems.Single();
        Equal(work.Version, restored.Version); Equal(renewed, restored.DeadlineUtc); Equal(DependentWorkStage.Waiting, restored.Stage);
    }
    private static async Task NoRedispatchByDeadline()
    {
        foreach (var result in new[] { DependentDispatchStage.Accepted, DependentDispatchStage.Unknown })
        {
            await using var f = new F(); var l = await f.Open(); var work = await Create(l, f); var source = await Source(l);
            var lease = (await Evaluate(l, work, Evidence(f, source), true)).Lease!;
            work = await l.FinishDependentDispatchAsync(lease, result, "recorded", result == DependentDispatchStage.Accepted ? Target with { TurnId = "new-turn" } : null);
            await Throws<ArgumentException>(() => Change(l, f, work, "update", deadline: f.Clock.UtcNow.AddDays(1).ToString("O")));
            if (result == DependentDispatchStage.Accepted)
            {
                work = await Change(l, f, work, "pause");
                await Throws<ArgumentException>(() => Change(l, f, work, "update", deadline: f.Clock.UtcNow.AddDays(1).ToString("O")));
            }
            var saved = await l.GetDependentWorkAsync(Identity);
            Equal(work, saved.WorkItems.Single()); Equal(1, saved.Attempts.Length); Equal(result, saved.Attempts.Single().Stage);
        }
    }
    private static async Task RegistrationFault()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
            var context = await Begin(l, f, "schedule_dependency", Spec(f), "finish documentation"); fault.Stage = stage;
            await Throws<InjectedFailure>(() => l.ApplyDependentWorkToolAsync(context));
            await l.DisposeAsync(); l = await f.Open();
            var count = stage == JournalWriteStage.AfterFlush ? 1 : 0;
            Equal(count, (await l.GetDependentWorkAsync(Identity)).WorkItems.Length);
            if (count == 1) Equal(ToolInvocationStage.Completed, (await l.ReadConversationAsync(Identity)).ToolInvocations.Single().Stage);
        }
    }
    private static async Task DispatchFault()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
            var w = await Create(l, f); var source = await Source(l); fault.Stage = stage;
            await Throws<InjectedFailure>(() => Evaluate(l, w, Evidence(f, source), true));
            await l.DisposeAsync(); l = await f.Open();
            var snapshot = await l.GetDependentWorkAsync(Identity);
            Equal(stage == JournalWriteStage.AfterFlush ? DependentWorkStage.Unknown : DependentWorkStage.Waiting, snapshot.WorkItems.Single().Stage);
            Equal(stage == JournalWriteStage.AfterFlush ? 1 : 0, snapshot.Attempts.Length);
        }
    }
    private static async Task OldSchema()
    {
        await using var f = new F(); var l = await f.Open();
        await l.ReceiveAsync(Identity, "old", "existing input", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await l.DisposeAsync(); await f.Rewrite(root => {
            root["schemaVersion"] = 6; root.AsObject().Remove("dependentWork"); root.AsObject().Remove("dependentAttempts"); root.AsObject().Remove("dependentMutations"); });
        l = await f.Open();
        Equal(0, (await l.GetDependentWorkAsync(Identity)).WorkItems.Length);
        await Create(l, f); await l.DisposeAsync(); l = await f.Open();
    }
    private static async Task Corruption()
    {
        for (int mode = 0; mode < 2; mode++)
        {
            await using var f = new F(); var l = await f.Open(); var w = await Create(l, f); var source = await Source(l);
            await Evaluate(l, w, Evidence(f, source), true); await l.DisposeAsync();
            await f.Rewrite(root => { if (mode == 0) root["dependentWork"]![0]!["instructions"] = "substituted";
                else root["dependentWork"]![0]!["attemptId"] = Guid.NewGuid().ToString(); });
            var bytes = await File.ReadAllBytesAsync(f.Path); await Error(LedgerErrors.CorruptStorage, () => f.Open());
            var preserved = await File.ReadAllBytesAsync(f.Path);
            Check(bytes.SequenceEqual(preserved));
        }
    }

    private static DependentWorkSpecification Spec(F f, ImmutableArray<DependencyRequirement>? requirements = null) =>
        new(Target, Fingerprint, "finish documentation", requirements ?? [new(Predecessor, Fingerprint)], f.Clock.UtcNow.AddHours(1).ToString("O"));
    private static Task<DependentWorkItem> Create(DurableConversationLedger l, F f, ImmutableArray<DependencyRequirement>? requirements = null) =>
        Execute(l, f, "schedule_dependency", Spec(f, requirements), "when the prerequisite completes, finish documentation");
    private static Task<DependentWorkItem> Change(DurableConversationLedger l, F f, DependentWorkItem w, string action, string? instructions = null,
        ImmutableArray<DependencyRequirement>? requirements = null, string? deadline = null) => Execute(l, f, "change_dependency",
            new DependentWorkChange(w.Id, w.Version, action, instructions, DeadlineUtc: deadline, Requirements: requirements), action + " " + (instructions ?? w.Id.ToString()));
    private static async Task<DependentWorkItem> Execute(DurableConversationLedger l, F f, string action, object spec, string body)
    {
        var context = await Begin(l, f, action, spec, body); var result = await l.ApplyDependentWorkToolAsync(context);
        await l.FinishToolInvocationAsync(Identity, context.InvocationId, ToolInvocationStage.Completed, "{\"status\":\"completed\"}", OperationStage.Completed);
        return (await l.GetDependentWorkAsync(Identity)).WorkItems.Single(w => w.Id == result.Facts.GetProperty("workItemId").GetGuid());
    }
    private static async Task<ToolExecutionContext> Begin(DurableConversationLedger l, F f, string action, object spec, string body)
    {
        string source = Guid.NewGuid().ToString("N"); string args = JsonSerializer.Serialize(spec, Json);
        Check(DependentWorkTools.Descriptors().Single(d => d.Name == action).Parameters.TryValidate(args, out var canonical, out _));
        var input = await l.ReceiveAsync(Identity, source, body, MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        var generation = await l.BeginGenerationAsync(Identity, [source]);
        var invocation = await l.RegisterToolInvocationAsync(generation, source, "call", action, canonical, true);
        var authorization = await l.AuthorizeCurrentUserRequestAsync(Identity, source, action, DependentWorkTools.TargetKey(action, canonical), canonical, body,
            spec is DependentWorkSpecification specification ? specification.Target : Target);
        var operation = await l.QueueOperationAsync(Identity, authorization.Id, source);
        await l.BeginToolInvocationAsync(generation, invocation.Id, operation.Id);
        return new(Identity, source, input.CurrentRevision, invocation.Id, operation.Id, operation.IdempotencyKey);
    }
    private static Task<CommitmentObservationLease> Source(DurableConversationLedger l, string? fingerprint = null) =>
        l.RegisterCommitmentObservationSourceAsync(Identity, "fake-local", Target.HostId, null, fingerprint ?? Fingerprint, Guid.NewGuid());
    private static DependencyEvidence Proof(F f, CommitmentObservationLease source, TaskReference task, CommitmentTaskState state, long sequence = 1, bool canStart = false) =>
        new(new(task, state, f.Clock.UtcNow, f.Clock.UtcNow, true, true, "fake direct backend lifecycle", Order: new(source, sequence), IsCurrentSourceEvidence: true), canStart);
    private static ImmutableArray<DependencyEvidence> Evidence(F f, CommitmentObservationLease source, long sequence = 1) =>
        [Proof(f, source, Target, CommitmentTaskState.Completed, sequence, true), Proof(f, source, Predecessor, CommitmentTaskState.Completed, sequence)];
    private static Task<DependencyReadiness> Evaluate(DurableConversationLedger l, DependentWorkItem w, ImmutableArray<DependencyEvidence> evidence, bool dispatch = false) =>
        l.EvaluateDependentWorkAsync(Identity, w.Id, w.Version, evidence, dispatch);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = FakeClock.Now; }
    private sealed class F : IAsyncDisposable
    {
        private readonly Fixture _files = new();
        internal Clock Clock { get; } = new();
        internal string Path => _files.Path;
        internal Task<DurableConversationLedger> Open(FaultInjector? fault = null, LedgerOptions? options = null) => _files.Open(fault, options, clock: Clock);
        internal async Task Rewrite(Action<JsonNode> change)
        {
            await using var storage = new FileJournalStorage(Path); var latest = (await storage.ReadLatestAsync())!;
            var protector = new TestEncryption(_files.Key);
            byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{sequence}");
            var plain = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
            var root = JsonNode.Parse(plain)!; change(root);
            await storage.AppendAsync(latest.Sequence, protector.Protect(Encoding.UTF8.GetBytes(root.ToJsonString()), Purpose(latest.Sequence + 1)));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
        public ValueTask DisposeAsync() => _files.DisposeAsync();
    }
    private static async Task Error(string code, Func<Task> action) => Equal(code, (await Throws<LedgerException>(action)).Code);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
