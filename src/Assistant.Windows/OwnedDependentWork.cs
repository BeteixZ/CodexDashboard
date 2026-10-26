using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class OwnedControlRuntime
{
    internal OwnedRuntimeTask? DependencyTarget(TaskReference reference) => Tasks().SingleOrDefault(t =>
        t.Handle.Identity.HostId == reference.HostId && t.Handle.Identity.ThreadId == reference.ThreadId
        && t.Handle.Identity.ProfileId == reference.ProfileId);

    internal bool CanScheduleDependency(OwnedRuntimeTask task) => Active && task.Handle.Access == OwnedTaskAccess.CreatedThisSession
        && task.Snapshot.CurrentTurnId != null && WriteFailure(task.ConfigurationId, "turn/start") == null
        && LoadedSession(task.ConfigurationId)?.Client.SupportsGuardedRequests == true;

    internal async Task<DependencyEvidence?> ReadDependencyEvidenceAsync(TaskReference reference, CancellationToken ct, bool includeCommands = false)
    {
        var task = DependencyTarget(reference);
        if (task == null || !CanScheduleDependency(task)) return null;
        var session = LoadedSession(task.ConfigurationId)!;
        using var entry = Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, StopToken);
        var read = await session.Client.ReconcileAsync(task.Handle, linked.Token).ConfigureAwait(false);
        Observed(session, task.Handle);
        var observation = await CommitmentObservationAsync(session, read.Observation, true, linked.Token).ConfigureAwait(false);
        var current = DependencyTarget(reference);
        if (observation == null || current == null) return null;
        if (includeCommands && !session.Client.IsCommandReadCurrent(task.Handle, read.Observation!.Sequence)) return null;
        return new(observation, CanScheduleDependency(current)
            && current.Snapshot.State is OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted
            && CheckWorkingDirectory(current).Allowed, includeCommands ? OwnedDependencyCommands.Read(read, reference) : null);
    }

    internal async Task DispatchDependencyAsync(DependentDispatchLease lease, Func<bool> enabled, CancellationToken ct)
    {
        WorkspaceReservation? reservation = null;
        bool invoked = false;
        var stage = DependentDispatchStage.NotDispatched;
        string receipt = "dependency_target_not_available";
        TaskReference? result = null;
        try
        {
            using var entry = Enter();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, StopToken);
            var target = DependencyTarget(lease.Target);
            if (lease.Identity != Identity || !enabled() || target == null || !CanScheduleDependency(target)
                || target.Snapshot.CurrentTurnId != lease.Target.TurnId
                || OwnedVerification.ConfigurationDigest(Profile(target.ConfigurationId)!.Configuration) != lease.BindingFingerprint
                || !await Ledger.IsDependentDispatchCurrentAsync(lease, linked.Token).ConfigureAwait(false)) return;
            await PrepareWorkspaceAsync(linked.Token).ConfigureAwait(false);
            var admission = ReserveWorkspace(target.ConfigurationId, lease.AttemptId, target);
            receipt = admission.Code;
            if (!admission.Allowed) return;
            reservation = admission.Reservation;
            var session = LoadedSession(target.ConfigurationId)!;
            var attempt = (await Ledger.GetDependentWorkAsync(Identity, linked.Token).ConfigureAwait(false)).Attempts.Single(a => a.Id == lease.AttemptId);
            using var directory = ReserveDirectory(target);
            invoked = true;
            var response = await session.Client.StartTurnAsync(lease.AttemptId, target.Handle, target.Snapshot.Revision,
                lease.Instructions, linked.Token, async token =>
                {
                    if (!await Ledger.IsDependentDispatchCurrentAsync(lease, token).ConfigureAwait(false)) return false;
                    return enabled() && Active && ReferenceEquals(LoadedSession(target.ConfigurationId), session)
                        && WriteFailure(target.ConfigurationId, "turn/start") == null
                        && attempt.Evidence.All(CurrentDependencyEvidence)
                        && (workspace == null || CheckWorkingDirectory(target, reservation).Allowed);
                }).ConfigureAwait(false);
            receipt = response.Code;
            if (response.Task == target.Handle.Identity && !string.IsNullOrWhiteSpace(response.TurnId)
                && response.TurnId != lease.Target.TurnId
                && response.State is ControlState.Accepted or ControlState.Running or ControlState.Completed)
            {
                result = new(lease.Target.HostId, lease.Target.ThreadId, response.TurnId, lease.Target.ProfileId);
                stage = DependentDispatchStage.Accepted;
            }
            else stage = response.State switch
            {
                ControlState.CancelledBeforeDispatch => DependentDispatchStage.NotDispatched,
                ControlState.Failed when response.Code == "server_rejected" => DependentDispatchStage.Rejected,
                _ => DependentDispatchStage.Unknown
            };
        }
        catch (OperationCanceledException) { receipt = "dependency_dispatch_cancelled"; stage = invoked ? DependentDispatchStage.Unknown : DependentDispatchStage.NotDispatched; }
        catch { receipt = "dependency_dispatch_unverified"; stage = invoked ? DependentDispatchStage.Unknown : DependentDispatchStage.NotDispatched; }
        finally
        {
            if (stage == DependentDispatchStage.Accepted) reservation?.Accepted(new(result!.HostId, result.ThreadId, result.ProfileId), result.TurnId);
            else if (stage == DependentDispatchStage.Unknown) reservation?.Unknown();
            else reservation?.NotDispatched();
            await Ledger.FinishDependentDispatchAsync(lease, stage, receipt, result, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool CurrentDependencyEvidence(DependencyEvidence evidence)
    {
        var order = evidence.Observation.Order!;
        var task = DependencyTarget(evidence.Observation.Task);
        var session = task == null ? null : LoadedSession(task.ConfigurationId);
        if (session == null || session.ObservationLease != order.Lease || session.Client.ConnectionEpoch != order.Lease.ConnectionEpoch
            || session.Client.CapabilityFailure("thread/read") != null || task!.Snapshot.CurrentTurnId != evidence.Observation.Task.TurnId) return false;
        var latest = session.Client.LatestTurnObservation(task.Handle);
        return latest != null && (evidence.Commands == null ? latest.Sequence >= order.Sequence : latest.Sequence == order.Sequence)
            && (evidence.Commands == null || session.Client.IsCommandReadCurrent(task.Handle, order.Sequence))
            && latest.TurnId == evidence.Observation.Task.TurnId
            && latest.State switch
            {
                ControlState.Completed => evidence.Observation.State == CommitmentTaskState.Completed,
                ControlState.Failed => evidence.Observation.State == CommitmentTaskState.Failed,
                ControlState.Interrupted => evidence.Observation.State == CommitmentTaskState.Interrupted,
                _ => false
            };
    }

    private async Task RestoreDependentWorkspaceAsync(ConversationIdentity identity, CancellationToken ct)
    {
        var saved = await Ledger.GetDependentWorkAsync(identity, ct).ConfigureAwait(false);
        foreach (var work in saved.WorkItems.Where(w => w.AttemptId != null))
        {
            var attempt = saved.Attempts.Single(a => a.Id == work.AttemptId);
            if (attempt.Stage is not (DependentDispatchStage.Dispatching or DependentDispatchStage.Unknown or DependentDispatchStage.Accepted)
                || attempt.Stage == DependentDispatchStage.Accepted && work.Completion != null) continue;
            var profile = profiles.Values.SingleOrDefault(p => p.Configuration.Connection.Endpoint.HostId == work.Target.HostId
                && p.Configuration.Connection.Endpoint.ProfileId == work.Target.ProfileId
                && OwnedVerification.ConfigurationDigest(p.Configuration) == work.BindingFingerprint);
            if (profile == null) { workspace!.FailRecovery("dependent_work_configuration_missing"); continue; }
            workspace!.RestoreUnresolved(new("owned/" + attempt.Id.ToString("N"),
                new(work.Target.HostId, work.Target.ThreadId, work.Target.ProfileId), Location(profile, profile.Creation.Cwd),
                attempt.ResultTask?.TurnId, attempt.StartedAt, RequireReconciliation: attempt.Stage != DependentDispatchStage.Accepted,
                OwnerScope: WorkspaceOwner(identity)));
        }
    }
}
