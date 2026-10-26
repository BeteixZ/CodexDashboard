namespace Assistant.Core;

/// <summary>Host-only connection registration. This is observation ordering, never control authorization.</summary>
public sealed record CommitmentObservationLease(ConversationIdentity Identity, string SourceKey,
    string HostId, string? ProfileId, string ConfigurationFingerprint, long Generation, Guid ConnectionEpoch);
public sealed record CommitmentObservationOrder(CommitmentObservationLease Lease, long Sequence);
internal sealed record CommitmentObservationSource(CommitmentObservationLease Lease, bool Active);

public sealed partial class DurableConversationLedger
{
    /// <summary>Call only for a newly verified current connection, serialized against its replacement.
    /// Reusing the same live epoch is a no-op. Reopened ledgers require a new connection epoch.</summary>
    public Task<CommitmentObservationLease> RegisterCommitmentObservationSourceAsync(ConversationIdentity identity,
        string sourceKey, string hostId, string? profileId, string configurationFingerprint, Guid connectionEpoch,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var proposed = new CommitmentObservationLease(identity, sourceKey, hostId, profileId,
            configurationFingerprint, 1, connectionEpoch);
        ValidateObservationLease(proposed);
        var previous = state.CommitmentObservationSources.SingleOrDefault(x => x.Lease.Identity == identity
            && x.Lease.SourceKey == sourceKey);
        if (previous != null)
        {
            var old = previous.Lease;
            if (old.ConnectionEpoch == connectionEpoch)
            {
                if (!previous.Active || old != proposed with { Generation = old.Generation })
                    throw Error(LedgerErrors.InvalidTransition, "An old connection epoch cannot be reactivated or rebound.");
                return Unchanged(old);
            }
            proposed = proposed with { Generation = checked(old.Generation + 1) };
            Replace(state.CommitmentObservationSources, previous, new(proposed, true));
        }
        else
        {
            if (state.CommitmentObservationSources.Count >= 256)
                throw Error(LedgerErrors.CapacityExceeded, "The observation source bound is full; no source was replaced.");
            state.CommitmentObservationSources.Add(new(proposed, true));
        }
        state.SchemaVersion = Math.Max(state.SchemaVersion, 5);
        return Changed(proposed);
    }, cancellationToken);

    private static void ValidateObservationLease(CommitmentObservationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease); ValidateIdentity(lease.Identity);
        ValidateToken(lease.SourceKey); ValidateToken(lease.HostId);
        if (lease.ProfileId != null) ValidateToken(lease.ProfileId);
        if (lease.Generation <= 0 || lease.ConnectionEpoch == Guid.Empty
            || lease.ConfigurationFingerprint?.Length != 64
            || lease.ConfigurationFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Incomplete observation source registration.");
    }

    private static bool IsCurrentObservationOrder(LedgerState state, ConversationIdentity identity,
        CommitmentTaskObservation observation)
    {
        if (observation.Order is not { } order) return true;
        try { ValidateObservationLease(order.Lease); }
        catch (ArgumentException) { return false; }
        return order.Sequence > 0 && order.Lease.Identity == identity
            && order.Lease.HostId == observation.Task.HostId && order.Lease.ProfileId == observation.Task.ProfileId
            && state.CommitmentObservationSources.Any(x => x.Active && x.Lease == order.Lease);
    }

    private static bool ObservationFollows(TaskSubscription subscription, CommitmentTaskObservation observation)
    {
        if (observation.Order is not { } order)
            return subscription.LastObservationOrder == null && !(subscription.LastObservedAt > observation.ObservedAt);
        if (subscription.LastObservationOrder is not { } before)
            // Migration must not re-label an old cached sample as a new event.
            return observation.IsCurrentSourceEvidence;
        if (before.Lease.Identity != order.Lease.Identity || before.Lease.SourceKey != order.Lease.SourceKey
            || before.Lease.HostId != order.Lease.HostId || before.Lease.ProfileId != order.Lease.ProfileId)
            return false;
        return order.Lease.Generation > before.Lease.Generation
            ? observation.IsCurrentSourceEvidence
            : order.Lease == before.Lease && order.Sequence > before.Sequence;
    }

    private static bool RecoverCommitmentObservationSources(LedgerState state)
    {
        bool changed = false;
        foreach (var source in state.CommitmentObservationSources.Where(x => x.Active).ToArray())
        { Replace(state.CommitmentObservationSources, source, source with { Active = false }); changed = true; }
        return changed;
    }

    private static void ValidateCommitmentObservationSources(LedgerState state)
    {
        void Require(bool valid)
        { if (!valid) throw Error(LedgerErrors.CorruptStorage, "Invalid commitment observation ordering."); }
        try
        {
            Require(state.CommitmentObservationSources.Count <= 256
                && state.CommitmentObservationSources.Select(x => (x.Lease.Identity, x.Lease.SourceKey)).Distinct().Count()
                    == state.CommitmentObservationSources.Count);
            Require(state.SchemaVersion >= 5 || state.CommitmentObservationSources.Count == 0
                && state.Subscriptions.All(x => x.LastObservationOrder == null));
            foreach (var source in state.CommitmentObservationSources) ValidateObservationLease(source.Lease);
            foreach (var subscription in state.Subscriptions.Where(x => x.LastObservationOrder != null))
            {
                var order = subscription.LastObservationOrder!; ValidateObservationLease(order.Lease);
                var work = state.WorkItems.Single(x => x.Id == subscription.WorkItemId);
                var current = state.CommitmentObservationSources.SingleOrDefault(x => x.Lease.Identity == work.Identity
                    && x.Lease.SourceKey == order.Lease.SourceKey);
                Require(order.Sequence > 0 && order.Lease.Identity == work.Identity
                    && work.Task?.HostId == order.Lease.HostId && work.Task.ProfileId == order.Lease.ProfileId
                    && subscription.LastObservedAt != null && current != null
                    && order.Lease.Generation <= current.Lease.Generation
                    && (order.Lease.Generation != current.Lease.Generation || order.Lease == current.Lease));
            }
        }
        catch (Exception e) when (e is ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid commitment observation registration.", e); }
    }
}
