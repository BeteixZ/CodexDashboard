using System;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class OwnedControlRuntime
{
    internal async Task<CommitmentTaskObservation?> CommitmentObservationAsync(Session session,
        ControlObservation? observation, bool currentEvidence, CancellationToken ct)
    {
        if (observation?.Task == null || observation.TurnId == null
            || observation.Code is not ("turn_lifecycle_observed" or "read_reconciliation" or "read_snapshot"
                or "turn_observed_operation_still_unknown" or "interruption_observed")
            || observation.Sequence <= 0 || observation.ConnectionEpoch == Guid.Empty) return null;
        await session.ObservationRegistration.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Active || !ReferenceEquals(LoadedSession(session.Profile.Id), session)
                || session.Client.ConnectionEpoch != observation.ConnectionEpoch
                || observation.Endpoint != session.Profile.Configuration.Connection.Endpoint
                || observation.Task.Endpoint != observation.Endpoint) return null;
            var lease = session.ObservationLease;
            if (lease?.ConnectionEpoch != observation.ConnectionEpoch)
            {
                if (!currentEvidence) return null;
                lease = await Ledger.RegisterCommitmentObservationSourceAsync(Identity, session.Profile.Id,
                    observation.Task.HostId, observation.Task.ProfileId,
                    OwnedVerification.ConfigurationDigest(session.Profile.Configuration),
                    observation.ConnectionEpoch, ct).ConfigureAwait(false);
                session.ObservationLease = lease;
            }
            // Registration awaited local persistence; replacement/stop may have won meanwhile.
            if (!Active || session.Client.ConnectionEpoch != observation.ConnectionEpoch) return null;
            DateTimeOffset? activity = null;
            if (currentEvidence)
            {
                bool seen = session.CommitmentStates.TryGetValue(observation.Task.Key, out var prior);
                if (!seen || prior.Epoch != observation.ConnectionEpoch || observation.Sequence > prior.Sequence)
                {
                    if (observation.Method != "thread/read" && (!seen || prior.Epoch != observation.ConnectionEpoch
                        || prior.State != observation.State)) activity = observation.ObservedAt;
                    session.CommitmentStates[observation.Task.Key] = (observation.ConnectionEpoch, observation.Sequence, observation.State);
                }
            }
            var state = observation.State switch
            {
                ControlState.Running => CommitmentTaskState.Running,
                ControlState.Completed => CommitmentTaskState.Completed,
                ControlState.Failed => CommitmentTaskState.Failed,
                ControlState.Interrupted => CommitmentTaskState.Interrupted,
                _ => CommitmentTaskState.Unknown
            };
            return new(new(observation.Task.HostId, observation.Task.ThreadId, observation.TurnId,
                observation.Task.ProfileId), state, observation.ObservedAt, activity, true, true,
                observation.Method == "thread/read" ? "owned_readonly_reconciliation" : "owned_app_server_lifecycle",
                Order: new(lease!, observation.Sequence), IsCurrentSourceEvidence: currentEvidence);
        }
        finally { session.ObservationRegistration.Release(); }
    }
}
