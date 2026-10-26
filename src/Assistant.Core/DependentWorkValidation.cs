using System.Text.Json;
using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private static void ValidateDependentWork(LedgerState state)
    {
        void Require(bool condition)
        { if (!condition) throw Error(LedgerErrors.CorruptStorage, "Invalid dependent work snapshot."); }
        try
        {
            var requirementsAtVersion = new Dictionary<(Guid, long), ImmutableArray<DependencyRequirement>>();
            Require(state.SchemaVersion >= 7 || state.DependentWork.Count + state.DependentAttempts.Count + state.DependentMutations.Count == 0);
            Require(state.SchemaVersion >= 8 || state.DependentWork.All(w => w.ContinueAfterWorkId == null));
            Require(state.SchemaVersion >= 9 || state.DependentWork.All(w => w.NotificationPolicy == null) && state.Events.All(e => e.DependentWorkNotice == null));
            Require(state.DependentWork.Count <= 256 && state.DependentAttempts.Count <= 2048
                && state.DependentWork.Select(w => w.Id).Distinct().Count() == state.DependentWork.Count
                && state.DependentAttempts.Select(a => a.Id).Distinct().Count() == state.DependentAttempts.Count
                && state.DependentMutations.Select(m => m.OperationId).Distinct().Count() == state.DependentMutations.Count);
            foreach (var work in state.DependentWork)
            {
                ValidateIdentity(work.Identity); RequireDependencyTarget(work.Target, work.BindingFingerprint);
                if (work.NotificationPolicy != null) ValidateCommitmentPolicy(work.NotificationPolicy);
                Require(work.Id != Guid.Empty && work.Version > 0 && Enum.IsDefined(work.Stage) && !string.IsNullOrWhiteSpace(work.Instructions) && work.Instructions.Length <= 4000
                    && work.CreatedAt != default && work.UpdatedAt >= work.CreatedAt && work.DeadlineUtc > work.CreatedAt
                    && work.DeadlineUtc.Offset == TimeSpan.Zero && !work.Requirements.IsDefaultOrEmpty && work.Requirements.Length <= 8
                    && work.Requirements.Distinct().Count() == work.Requirements.Length && !work.LastEvidence.IsDefault
                    && (work.LastEvidence.IsEmpty || work.LastEvidence.Length == work.Requirements.Length + 1));
                foreach (var r in work.Requirements)
                {
                    if (r.CommandSuccess is { } command) { Require(state.SchemaVersion >= 11); RequireDependencyCommand(command); }
                    if (r.Task != null && r.WorkItemId == null && r.WorkVersion == null) RequireDependencyTarget(r.Task, r.BindingFingerprint!);
                    else Require(r.Task == null && r.BindingFingerprint == null && r.WorkVersion is > 0 && r.WorkItemId != work.Id
                        && state.DependentWork.Any(p => p.Id == r.WorkItemId && p.Identity == work.Identity && p.Version >= r.WorkVersion));
                }
                RequireContinueAfterTarget(state, work);
                var operation = RequireOperation(state, work.Identity, work.CreationOperationId);
                var source = RequireInbox(state, work.Identity, work.SourceMessageId);
                Require(operation.Action == "schedule_dependency" && operation.SourceMessageId == source.SourceMessageId
                    && operation.Task == work.Target && source.Trust == MessageTrust.AuthenticatedUser);
                var original = JsonSerializer.Deserialize<DependentWorkSpecification>(operation.ArgumentsJson, Json)!;
                string text = original.Instructions; var requirements = original.Requirements; var deadline = Deadline(original.DeadlineUtc);
                var policy = original.NotificationPolicy;
                Require(original.Target == work.Target && original.BindingFingerprint == work.BindingFingerprint && original.ContinueAfterWorkId == work.ContinueAfterWorkId);
                var mutations = state.DependentMutations.Where(m => m.WorkItemId == work.Id).OrderBy(m => m.WorkVersion).ToArray();
                Require(mutations.Length == work.Version && mutations.Select(m => m.WorkVersion).SequenceEqual(Enumerable.Range(1, mutations.Length).Select(i => (long)i)));
                foreach (var mutation in mutations)
                {
                    var op = RequireOperation(state, work.Identity, mutation.OperationId);
                    var call = RequireInvocation(state, work.Identity, mutation.InvocationId);
                    var grant = RequireAuthorization(state, work.Identity, op.AuthorizationId);
                    Require(op.Stage == OperationStage.Completed && op.Receipt == mutation.ReceiptJson && op.Task == work.Target
                        && call.OperationId == op.Id && call.ToolName == op.Action && call.ArgumentsJson == op.ArgumentsJson
                        && grant.Action == op.Action && grant.ArgumentsJson == op.ArgumentsJson && grant.TargetKey == op.TargetKey
                        && op.TargetKey == DependentWorkTools.TargetKey(op.Action, op.ArgumentsJson));
                    if (mutation.WorkVersion == 1) Require(mutation.OperationId == work.CreationOperationId);
                    else
                    {
                        var change = JsonSerializer.Deserialize<DependentWorkChange>(op.ArgumentsJson, Json)!;
                        Require(op.Action == "change_dependency" && change.WorkItemId == work.Id && change.ExpectedVersion == mutation.WorkVersion - 1);
                        text = change.Instructions ?? text; requirements = change.Requirements ?? requirements;
                        deadline = change.DeadlineUtc == null ? deadline : Deadline(change.DeadlineUtc);
                        policy = change.NotificationPolicy ?? policy;
                    }
                    requirementsAtVersion.Add((work.Id, mutation.WorkVersion), requirements);
                    foreach (var requirement in requirements.Where(r => r.CommandSuccess != null))
                    { Require(state.SchemaVersion >= 11); RequireDependencyCommand(requirement.CommandSuccess!); }
                }
                Require(work.Instructions == text && work.Requirements.SequenceEqual(requirements) && work.DeadlineUtc == deadline && work.NotificationPolicy == policy);
                foreach (var evidence in work.LastEvidence) ValidateDependentEvidence(state, work.Identity, evidence);
                var attempt = work.AttemptId == null ? null : state.DependentAttempts.SingleOrDefault(a => a.Id == work.AttemptId && a.WorkItemId == work.Id);
                Require(work.AttemptId == null || attempt != null);
                Require(work.Stage != DependentWorkStage.Dispatching || attempt?.Stage == DependentDispatchStage.Dispatching);
                Require(work.Stage != DependentWorkStage.Unknown || attempt?.Stage == DependentDispatchStage.Unknown);
                Require(work.Stage is not (DependentWorkStage.Running or DependentWorkStage.Completed or DependentWorkStage.Failed)
                    || attempt?.Stage == DependentDispatchStage.Accepted && work.ResultTask == attempt.ResultTask);
                Require((work.ResultTask != null) == (attempt?.Stage == DependentDispatchStage.Accepted));
                if (work.LastResultObservation != null)
                {
                    ValidateDependentEvidence(state, work.Identity, work.LastResultObservation);
                    Require(work.LastResultObservation.Task == work.ResultTask
                        && work.LastResultObservation.Order!.Lease.ConfigurationFingerprint == work.BindingFingerprint);
                }
                if (work.Completion != null)
                {
                    ValidateDependentEvidence(state, work.Identity, work.Completion);
                    Require(work.Completion == work.LastResultObservation && work.Completion.Task == work.ResultTask && work.Completion.Order!.Lease.ConfigurationFingerprint == work.BindingFingerprint
                        && work.Completion.State is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted);
                }
                Require(work.Stage is not (DependentWorkStage.Completed or DependentWorkStage.Failed)
                    || work.Completion != null && CompletionStage(work.Completion.State) == work.Stage);
            }
            foreach (var attempt in state.DependentAttempts)
            {
                var work = RequireDependentWork(state, attempt.Identity, attempt.WorkItemId);
                Require(attempt.RecoveryOfEventId == null || ValidDependentRecoveryReference(state, work, attempt));
                var dispatchTarget = work.Target;
                if (work.ContinueAfterWorkId is { } parentId)
                {
                    var parent = RequireDependentWork(state, work.Identity, parentId);
                    Require(parent.Stage == DependentWorkStage.Completed && parent.ResultTask != null
                        && parent.Completion?.State == CommitmentTaskState.Completed);
                    dispatchTarget = parent.ResultTask!;
                }
                Require(attempt.Id != Guid.Empty && Enum.IsDefined(attempt.Stage) && attempt.WorkVersion > 0 && attempt.WorkVersion <= work.Version
                    && attempt.StartedAt >= work.CreatedAt && attempt.UpdatedAt >= attempt.StartedAt && !attempt.Evidence.IsDefaultOrEmpty
                    && attempt.Evidence.Length is >= 2 and <= 9 && dispatchTarget != null && attempt.Evidence[0].Observation.Task == dispatchTarget
                    && attempt.Evidence[0].CanStartTurn && attempt.Evidence[0].Observation.Order!.Lease.ConfigurationFingerprint == work.BindingFingerprint
                    && attempt.Evidence.Skip(1).All(e => e.Observation.State == CommitmentTaskState.Completed)
                    && (attempt.Stage == DependentDispatchStage.Accepted) == (attempt.ResultTask != null));
                foreach (var evidence in attempt.Evidence) ValidateDependentEvidence(state, attempt.Identity, evidence);
                var required = requirementsAtVersion[(work.Id, attempt.WorkVersion)];
                Require(required.Length + 1 == attempt.Evidence.Length);
                for (int i = 0; i < required.Length; i++)
                {
                    var r = required[i];
                    if (r.CommandSuccess == null) continue;
                    var parent = r.WorkItemId is { } prerequisiteId ? RequireDependentWork(state, work.Identity, prerequisiteId) : null;
                    var proof = attempt.Evidence[i + 1].Observation;
                    Require(proof.Task == (r.Task ?? parent!.ResultTask)
                        && proof.Order!.Lease.ConfigurationFingerprint == (r.BindingFingerprint ?? parent!.BindingFingerprint));
                }
                Require(required.Select((r, i) => DependencyCommandCondition(r, attempt.Evidence[i + 1])).All(s => s == DependencyCommandState.Completed));
                if (attempt.ResultTask is { } result) Require(result.HostId == work.Target.HostId && result.ThreadId == work.Target.ThreadId
                    && result.ProfileId == work.Target.ProfileId && !string.IsNullOrWhiteSpace(result.TurnId) && result.TurnId != dispatchTarget!.TurnId);
                Require(attempt.Stage == DependentDispatchStage.Dispatching ? attempt.Receipt == null : !string.IsNullOrWhiteSpace(attempt.Receipt) && attempt.Receipt.Length <= 4000);
            }
            ValidateDependencyGraph(state);
            ValidateDependentNotices(state);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or NullReferenceException)
        { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid dependent work data or authorization graph.", error); }
    }
    private static void ValidateDependentEvidence(LedgerState state, ConversationIdentity identity, DependencyEvidence evidence)
    {
        ValidateDependentEvidence(state, identity, evidence.Observation);
        if (evidence.Commands != null && state.SchemaVersion < 11) throw new ArgumentException("Command evidence requires format 11.");
        ValidateDependencyCommands(evidence);
    }
    private static void ValidateDependentEvidence(LedgerState state, ConversationIdentity identity, CommitmentTaskObservation observation)
    {
        ValidateTask(observation.Task);
        if (observation.Order is not { } order || !observation.IsCurrentSourceEvidence || !Enum.IsDefined(observation.State)
            || order.Sequence <= 0 || order.Lease.Identity != identity || order.Lease.HostId != observation.Task.HostId
            || order.Lease.ProfileId != observation.Task.ProfileId
            || !state.CommitmentObservationSources.Any(s => s.Lease.Identity == identity && s.Lease.SourceKey == order.Lease.SourceKey
                && (s.Lease.Generation > order.Lease.Generation || s.Lease == order.Lease)))
            throw new ArgumentException("Dependent evidence has no registered source.");
        ValidateObservationLease(order.Lease);
    }
}
