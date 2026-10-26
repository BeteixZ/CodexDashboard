using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    /// <summary>The host supplies fresh read-only evidence. This method never invokes a backend.</summary>
    public Task<DependencyReadiness> EvaluateDependentWorkAsync(ConversationIdentity identity, Guid workItemId, long version,
        ImmutableArray<DependencyEvidence> evidence, bool beginDispatch = false, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var original = RequireDependentWork(state, identity, workItemId); var work = original;
        if (work.Version != version) throw Error(LedgerErrors.Conflict, "Dependent work version changed.");
        ImmutableArray<TaskReference> evidenceTargets = [];
        DependencyReadiness Result(string code, bool ready = false, DependentDispatchLease? lease = null) => new(work, code, ready, lease, evidenceTargets);
        if (work.Stage != DependentWorkStage.Waiting) return Unchanged(Result("work_not_waiting"));
        if (work.DeadlineUtc <= _clock.UtcNow)
        {
            work = work with { Stage = DependentWorkStage.Expired, Reason = "deadline_passed", UpdatedAt = _clock.UtcNow };
            Replace(state.DependentWork, original, work); return Changed(Result("deadline_passed"));
        }
        string? invalid = null;
        var expected = new List<(TaskReference Task, string Fingerprint)> { (work.Target, work.BindingFingerprint) };
        foreach (var requirement in work.Requirements)
        {
            if (requirement.Task != null) expected.Add((requirement.Task, requirement.BindingFingerprint!));
            else
            {
                var parent = RequireDependentWork(state, identity, requirement.WorkItemId!.Value);
                if (parent.Version != requirement.WorkVersion || parent.Stage is DependentWorkStage.Cancelled or DependentWorkStage.Failed
                    or DependentWorkStage.Expired or DependentWorkStage.NeedsDecision) { invalid = "predecessor_changed_or_failed"; break; }
                if (parent.Stage != DependentWorkStage.Completed || parent.ResultTask == null) return Unchanged(Result("predecessor_not_completed"));
                expected.Add((parent.ResultTask, parent.BindingFingerprint));
            }
        }
        if (invalid == null)
        {
            if (work.ContinueAfterWorkId is { } parentId)
                expected[0] = (RequireDependentWork(state, identity, parentId).ResultTask!, work.BindingFingerprint);
            evidenceTargets = expected.Select(item => item.Task).ToImmutableArray();
            if (evidence.IsDefault || evidence.Length != expected.Count) return Unchanged(Result("evidence_incomplete"));
            for (int i = 0; i < expected.Count; i++)
            {
                var observation = evidence[i].Observation;
                ValidateDependencyCommands(evidence[i]);
                if (!FreshDependencyEvidence(state, identity, observation)) return Unchanged(Result("evidence_not_current"));
                if (observation.Task != expected[i].Task || observation.Order!.Lease.ConfigurationFingerprint != expected[i].Fingerprint)
                { invalid = "task_or_configuration_changed"; break; }
                if (!work.LastEvidence.IsDefaultOrEmpty && !DependencyEvidenceFollows(work.LastEvidence[i], evidence[i]))
                    return Unchanged(Result("evidence_out_of_order"));
            }
        }
        if (invalid != null)
        {
            work = work with { Stage = DependentWorkStage.NeedsDecision, Reason = invalid, UpdatedAt = _clock.UtcNow };
            Replace(state.DependentWork, original, work); return Changed(Result(invalid));
        }
        bool failed = evidence.Skip(1).Any(e => e.Observation.State is CommitmentTaskState.Failed or CommitmentTaskState.Interrupted);
        var commands = work.Requirements.Select((r, i) => DependencyCommandCondition(r, evidence[i + 1])).ToArray();
        bool commandFailed = commands.Contains(DependencyCommandState.Failed);
        failed |= commandFailed;
        string failure = commandFailed ? "predecessor_command_failed" : "predecessor_failed";
        bool ready = !failed && evidence[0].CanStartTurn
            && evidence[0].Observation.State is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted
            && evidence.Skip(1).All(e => e.Observation.State == CommitmentTaskState.Completed)
            && commands.All(c => c == DependencyCommandState.Completed);
        bool changed = !work.LastEvidence.SequenceEqual(evidence) || failed;
        if (changed) work = work with { LastEvidence = evidence, UpdatedAt = _clock.UtcNow,
            Stage = failed ? DependentWorkStage.NeedsDecision : work.Stage, Reason = failed ? failure : null };
        if (evidence.Any(e => e.Commands != null)) state.SchemaVersion = Math.Max(state.SchemaVersion, 11);
        DependentDispatchLease? lease = null;
        if (ready && beginDispatch)
        {
            if (state.DependentAttempts.Count >= 2048 || state.DependentAttempts.Count(a => a.WorkItemId == work.Id) >= 32)
            {
                work = work with { Stage = DependentWorkStage.NeedsDecision, Reason = "dispatch_budget_exhausted", UpdatedAt = _clock.UtcNow };
                Replace(state.DependentWork, original, work); return Changed(Result("dispatch_budget_exhausted"));
            }
            var attempt = new DependentDispatchAttempt(Guid.NewGuid(), work.Id, identity, work.Version,
                DependentDispatchStage.Dispatching, _clock.UtcNow, _clock.UtcNow, evidence);
            state.DependentAttempts.Add(attempt);
            work = work with { Stage = DependentWorkStage.Dispatching, AttemptId = attempt.Id, UpdatedAt = _clock.UtcNow };
            lease = new(attempt.Id, work.Id, identity, work.Version, expected[0].Task, work.BindingFingerprint, work.Instructions);
            changed = true;
        }
        if (changed) Replace(state.DependentWork, original, work);
        return new Mutation<DependencyReadiness>(Result(failed ? failure : ready ? "ready" : "conditions_not_met", ready, lease), changed);
    }, cancellationToken);

    // Invoked at the adapter's actual before-send boundary after its scope and directory checks.
    public Task<bool> IsDependentDispatchCurrentAsync(DependentDispatchLease lease, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var (work, attempt) = RequireDependentAttempt(state, lease);
        return Unchanged(work.Version == lease.WorkVersion && work.Stage == DependentWorkStage.Dispatching
            && work.DeadlineUtc > _clock.UtcNow && attempt.Stage == DependentDispatchStage.Dispatching
            && attempt.Evidence.All(e => FreshDependencyEvidence(state, work.Identity, e.Observation))
            && work.Requirements.Where(r => r.WorkItemId != null).All(r =>
                state.DependentWork.Any(w => w.Identity == work.Identity && w.Id == r.WorkItemId && w.Version == r.WorkVersion && w.Stage == DependentWorkStage.Completed)));
    }, cancellationToken);

    public Task<DependentWorkItem> FinishDependentDispatchAsync(DependentDispatchLease lease, DependentDispatchStage outcome,
        string receipt, TaskReference? resultTask = null, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var (work, attempt) = RequireDependentAttempt(state, lease);
        ValidateBody(receipt);
        if (receipt.Length > 4000 || outcome == DependentDispatchStage.Dispatching || !Enum.IsDefined(outcome))
            throw new ArgumentException("A bounded factual dispatch receipt is required.");
        if (outcome == DependentDispatchStage.Accepted)
        {
            ValidateTask(resultTask);
            if (resultTask == null || string.IsNullOrWhiteSpace(resultTask.TurnId) || resultTask.TurnId == lease.Target.TurnId
                || resultTask.HostId != lease.Target.HostId || resultTask.ThreadId != lease.Target.ThreadId || resultTask.ProfileId != lease.Target.ProfileId)
                throw new ArgumentException("Acceptance must identify the actual new turn on the same task.");
        }
        else if (resultTask != null) throw new ArgumentException("Only acceptance identifies a resulting turn.");
        if (attempt.Stage == outcome && attempt.Receipt == receipt && attempt.ResultTask == resultTask) return Unchanged(work);
        if (attempt.Stage != DependentDispatchStage.Dispatching
            && !(attempt.Stage == DependentDispatchStage.Unknown && outcome == DependentDispatchStage.Accepted))
            throw Error(LedgerErrors.InvalidTransition, "The dispatch receipt is already settled or cannot be reinterpreted.");
        var updated = attempt with { Stage = outcome, ResultTask = resultTask, Receipt = receipt, UpdatedAt = _clock.UtcNow };
        updated = BindDependentRecovery(state, work, updated);
        Replace(state.DependentAttempts, attempt, updated);
        var stage = outcome switch { DependentDispatchStage.Accepted => DependentWorkStage.Running,
            DependentDispatchStage.Unknown => DependentWorkStage.Unknown,
            DependentDispatchStage.NotDispatched => DependentWorkStage.Waiting, _ => DependentWorkStage.NeedsDecision };
        var changed = work with { ResultTask = resultTask, UpdatedAt = _clock.UtcNow,
            Stage = work.Stage is DependentWorkStage.Cancelled or DependentWorkStage.Paused or DependentWorkStage.Expired ? work.Stage : stage,
            Reason = outcome == DependentDispatchStage.NotDispatched ? "known_not_dispatched" : null };
        Replace(state.DependentWork, work, changed); SupersedeDependentNotices(state, changed); return Changed(changed);
    }, cancellationToken);

    public Task<DependentWorkItem> ObserveDependentCompletionAsync(ConversationIdentity identity, Guid workItemId,
        CommitmentTaskObservation observation, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var work = RequireDependentWork(state, identity, workItemId);
        if (work.ResultTask != observation.Task || work.Completion != null
            || !FreshDependencyEvidence(state, identity, observation)
            || observation.Order!.Lease.ConfigurationFingerprint != work.BindingFingerprint) return Unchanged(work);
        var attempt = state.DependentAttempts.Single(a => a.Id == work.AttemptId);
        var previous = work.LastResultObservation ?? attempt.Evidence[0].Observation;
        if (observation.ObservedAt < attempt.StartedAt || observation == work.LastResultObservation
            || !DependencyEvidenceFollows(new(previous), new(observation))) return Unchanged(work);
        bool terminal = observation.State is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted;
        if (observation.State == CommitmentTaskState.Completed && work.Stage != DependentWorkStage.Cancelled)
        {
            var recovered = BindDependentRecovery(state, work, attempt);
            if (recovered != attempt) Replace(state.DependentAttempts, attempt, recovered);
        }
        var changed = work with { LastResultObservation = observation, Completion = terminal ? observation : null, UpdatedAt = _clock.UtcNow,
            Stage = !terminal || work.Stage is DependentWorkStage.Paused or DependentWorkStage.Cancelled ? work.Stage : CompletionStage(observation.State) };
        Replace(state.DependentWork, work, changed); SupersedeDependentNotices(state, changed); return Changed(changed);
    }, cancellationToken);

    private bool FreshDependencyEvidence(LedgerState state, ConversationIdentity identity, CommitmentTaskObservation observation) =>
        observation.Order != null && observation.IsCurrentSourceEvidence && UsableCommitmentObservation(observation with { State = CommitmentTaskState.Running })
        && Enum.IsDefined(observation.State)
        && IsCurrentObservationOrder(state, identity, observation);
    private static bool DependencyEvidenceFollows(DependencyEvidence previous, DependencyEvidence next)
    {
        var before = previous.Observation.Order!; var after = next.Observation.Order!;
        return before.Lease.SourceKey == after.Lease.SourceKey && (after.Lease.Generation > before.Lease.Generation
            || after.Lease == before.Lease && (after.Sequence > before.Sequence || after.Sequence == before.Sequence && previous == next));
    }
    private static DependentWorkStage CompletionStage(CommitmentTaskState state) => state == CommitmentTaskState.Completed
        ? DependentWorkStage.Completed : DependentWorkStage.Failed;
    private static (DependentWorkItem Work, DependentDispatchAttempt Attempt) RequireDependentAttempt(LedgerState state, DependentDispatchLease lease)
    {
        var work = RequireDependentWork(state, lease.Identity, lease.WorkItemId);
        var attempt = state.DependentAttempts.SingleOrDefault(a => a.Id == lease.AttemptId && a.Identity == lease.Identity
            && a.WorkItemId == work.Id && a.WorkVersion == lease.WorkVersion);
        if (attempt == null || work.AttemptId != attempt.Id || lease.Target != attempt.Evidence[0].Observation.Task
            || lease.BindingFingerprint != work.BindingFingerprint || lease.Instructions != work.Instructions)
            throw Error(LedgerErrors.Conflict, "Dispatch lease does not name the exact recorded attempt.");
        return (work, attempt);
    }
    private bool RecoverDependentWork(LedgerState state)
    {
        bool changed = false;
        foreach (var receipt in state.DependentMutations)
        {
            var work = state.DependentWork.Single(w => w.Id == receipt.WorkItemId);
            var invocation = RequireInvocation(state, work.Identity, receipt.InvocationId);
            if (invocation.Stage != ToolInvocationStage.Executing) continue;
            Replace(state.ToolInvocations, invocation, invocation with { Stage = ToolInvocationStage.Completed,
                ResultJson = receipt.ReceiptJson, UpdatedAt = _clock.UtcNow });
            state.Events.Add(new(Guid.NewGuid(), work.Identity, ConversationSource.ToolResult, receipt.ReceiptJson,
                _clock.UtcNow, _clock.UtcNow, invocation.Revision, invocation.SourceMessageId, RelatedOperationId: receipt.OperationId,
                OutputDependencies: invocation.GenerationContext?.Dependencies, RelatedInvocationId: invocation.Id));
            changed = true;
        }
        foreach (var attempt in state.DependentAttempts.Where(a => a.Stage == DependentDispatchStage.Dispatching).ToArray())
        {
            Replace(state.DependentAttempts, attempt, attempt with { Stage = DependentDispatchStage.Unknown,
                Receipt = "Process ended during the recorded dispatch attempt; do not resend.", UpdatedAt = _clock.UtcNow });
            var work = RequireDependentWork(state, attempt.Identity, attempt.WorkItemId);
            if (work.Stage == DependentWorkStage.Dispatching) Replace(state.DependentWork, work,
                work with { Stage = DependentWorkStage.Unknown, UpdatedAt = _clock.UtcNow });
            changed = true;
        }
        return changed;
    }
}
