using System.Collections.Immutable;
using System.Globalization;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    /// <summary>Trusted host settings only; contains no provider credentials or message content.</summary>
    public Task ConfigureWakeChannelAsync(ConversationIdentity identity, WakeChannelConfiguration configuration,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateIdentity(identity); ValidateWakeConfiguration(configuration);
        var old = state.WakeChannels.SingleOrDefault(x => x.Identity == identity);
        if (old?.Configuration == configuration) return Unchanged(false);
        if (!state.Conversations.Any(x => x.Identity == identity)) ReplaceConversation(state, identity, 0);
        if (old != null) state.WakeChannels.Remove(old);
        state.WakeChannels.Add(new(identity, configuration));
        if (!configuration.Enabled || old != null && old.Configuration.RouteId != configuration.RouteId)
            CancelPendingWakes(state, identity, "The secondary-channel route was disabled or changed.");
        return Changed(true);
    }, cancellationToken);

    /// <summary>Captures rejected notification references, never copies or changes their immutable bodies.</summary>
    public Task<ChannelWakeRecord?> EnsurePendingWakeAsync(ConversationIdentity identity, Guid routeId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateIdentity(identity);
        var episode = TrustedInputRevision(state, identity);
        var existing = state.ChannelWakes.SingleOrDefault(x => x.Identity == identity && x.EpisodeRevision == episode);
        var configuration = state.WakeChannels.SingleOrDefault(x => x.Identity == identity)?.Configuration;
        if (configuration is not { Enabled: true } || routeId == Guid.Empty || configuration.RouteId != routeId)
            return Unchanged<ChannelWakeRecord?>(existing);
        // The tombstone belongs to the input episode, not to a provider route. Rotation cannot rearm it.
        if (existing != null && (existing.Stage != ChannelWakeStage.Pending || existing.RouteId != routeId))
            return Unchanged<ChannelWakeRecord?>(existing);
        var sources = state.Outbox.Where(x => x.Identity == identity && WakeSourceEligible(state, x, episode, false))
            .Select(x => new ChannelWakeSource(x.Id, x.SendAttempts, episode, x.RejectionReason)).ToArray();
        if (sources.Length == 0)
        {
            if (existing == null) return Unchanged<ChannelWakeRecord?>(null);
            var cancelled = existing with { Stage = ChannelWakeStage.Cancelled, FinishedAt = _clock.UtcNow.ToUniversalTime(),
                Evidence = "No qualifying pending report remains for this wake episode." };
            Replace(state.ChannelWakes, existing, cancelled);
            return Changed<ChannelWakeRecord?>(cancelled);
        }
        if (existing != null)
        {
            var added = sources.Where(x => !existing.Sources.Contains(x)).ToArray();
            if (added.Length == 0) return Unchanged<ChannelWakeRecord?>(existing);
            var updated = existing with { Sources = existing.Sources.AddRange(added) };
            Replace(state.ChannelWakes, existing, updated);
            return Changed<ChannelWakeRecord?>(updated);
        }
        if (state.ChannelWakes.Count >= _options.MaximumChannelWakes)
            throw Error(LedgerErrors.CapacityExceeded, "Wake history is full; original pending reports remain intact.");
        var now = _clock.UtcNow.ToUniversalTime();
        var wake = new ChannelWakeRecord(Guid.NewGuid(), identity, episode, routeId, [.. sources],
            ChannelWakeStage.Pending, now, now.AddSeconds(_options.WakeAggregationSeconds));
        state.ChannelWakes.Add(wake);
        return Changed<ChannelWakeRecord?>(wake);
    }, cancellationToken);

    public async Task<ImmutableArray<ChannelWakeRecord>> GetChannelWakesAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default) =>
        (await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false)).ChannelWakes;

    /// <summary>Call after the actual transport throttle wait, immediately before the HTTP request.</summary>
    public Task<ChannelWakeLease> BeginWakeSendAsync(ConversationIdentity identity, Guid wakeId, Guid routeId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var wake = RequireWake(state, identity, wakeId);
        if (wake.RouteId != routeId) throw Error(LedgerErrors.IdentityMismatch, "The wake belongs to a different route.");
        if (wake.Stage != ChannelWakeStage.Pending || wake.ReservedAttempts != 0)
            throw Error(LedgerErrors.InvalidTransition, "Only a never-started pending wake can be sent.");
        var configuration = state.WakeChannels.SingleOrDefault(x => x.Identity == identity)?.Configuration;
        var now = _clock.UtcNow.ToUniversalTime();
        if (configuration is not { Enabled: true } || configuration.RouteId != routeId
            || wake.EpisodeRevision != TrustedInputRevision(state, identity) || wake.NotBefore > now
            || InCommitmentQuietHours(new(configuration.QuietStart, configuration.QuietEnd, configuration.TimeZoneId), now)
            || !wake.Sources.Any(source => CurrentWakeSource(state, wake, source, true)))
            throw Error(LedgerErrors.NotificationSuppressed, "The current input, source policy, route, or quiet period prevents this wake.");
        var budget = WakeBudget(state.ChannelWakes, now);
        if (budget.StartedLastHour >= 1 || budget.StartedLast24Hours >= 3 || budget.InFlight >= 1)
            throw Error(LedgerErrors.BudgetDeferred, "The global wake budget is unavailable; no attempt was consumed.");
        var claim = Guid.NewGuid();
        Replace(state.ChannelWakes, wake, wake with { Stage = ChannelWakeStage.Sending, ClaimId = claim,
            ReservedAttempts = 1, StartedAt = now });
        return Changed(new ChannelWakeLease(wake.Id, identity, routeId, wake.EpisodeRevision, claim));
    }, cancellationToken);

    /// <summary>Records only a transport result. Accepted means API acceptance, never phone delivery.</summary>
    public Task RecordWakeOutcomeAsync(ChannelWakeLease lease, WakeDeliveryOutcome outcome, string evidence,
        string? platformReceipt = null, bool reconciliation = false, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateBody(evidence);
        if (!Enum.IsDefined(outcome)) throw new ArgumentException("Unknown wake outcome.");
        if (outcome == WakeDeliveryOutcome.Accepted) ValidateToken(platformReceipt!);
        else if (platformReceipt != null) throw new ArgumentException("Only a proven API acceptance can carry a receipt.");
        var wake = RequireWakeLease(state, lease);
        var stage = outcome switch
        {
            WakeDeliveryOutcome.Accepted => ChannelWakeStage.Accepted,
            WakeDeliveryOutcome.Rejected => ChannelWakeStage.Rejected,
            WakeDeliveryOutcome.NotStarted => ChannelWakeStage.NotStarted,
            _ => ChannelWakeStage.Unknown
        };
        if (wake.Stage == stage && wake.Evidence == evidence && wake.PlatformReceipt == platformReceipt) return Unchanged(false);
        if (wake.Stage != ChannelWakeStage.Sending && !(wake.Stage == ChannelWakeStage.Unknown && reconciliation
            && outcome is WakeDeliveryOutcome.Accepted or WakeDeliveryOutcome.Rejected))
            throw Error(LedgerErrors.InvalidTransition, "The wake cannot be replayed; an unknown result requires explicit reconciliation.");
        Replace(state.ChannelWakes, wake, wake with { Stage = stage, FinishedAt = _clock.UtcNow.ToUniversalTime(),
            Evidence = evidence, PlatformReceipt = platformReceipt });
        // A timeout/result is not proof that an external task has drained. Release is separate.
        return Changed(true);
    }, cancellationToken);

    /// <summary>Call in finally only after the actual external task has terminated. Never refunds quota.</summary>
    public Task<bool> ReleaseWakeSendAsync(ChannelWakeLease lease, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var wake = RequireWakeLease(state, lease);
        if (wake.ReleasedAt != null) return Unchanged(false);
        var now = _clock.UtcNow.ToUniversalTime();
        var updated = wake with { ReleasedAt = now };
        if (wake.Stage == ChannelWakeStage.Sending) updated = updated with
        { Stage = ChannelWakeStage.Unknown, FinishedAt = now, Evidence = "The external send ended without a committed receipt." };
        Replace(state.ChannelWakes, wake, updated);
        return Changed(true);
    }, cancellationToken);

    public async Task<ChannelWakeBudgetStatus> GetChannelWakeBudgetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return WakeBudget(_state.ChannelWakes, _clock.UtcNow); }
        finally { _gate.Release(); }
    }

    private static ChannelWakeBudgetStatus WakeBudget(IEnumerable<ChannelWakeRecord> wakes, DateTimeOffset now)
    {
        var rows = wakes.Where(x => x.StartedAt != null).ToArray();
        var hour = rows.Where(x => x.StartedAt > now.AddHours(-1)).Select(x => x.StartedAt!.Value).Order().ToArray();
        var day = rows.Where(x => x.StartedAt > now.AddDays(-1)).Select(x => x.StartedAt!.Value).Order().ToArray();
        DateTimeOffset? next = hour.Length >= 1 ? hour[^1].AddHours(1) : null;
        if (day.Length >= 3 && (next == null || day[^3].AddDays(1) > next)) next = day[^3].AddDays(1);
        return new(now, hour.Length, day.Length, rows.Count(x => x.ReleasedAt == null), next);
    }
    private static long TrustedInputRevision(LedgerState state, ConversationIdentity identity) =>
        state.Inbox.Where(x => x.Identity == identity && x.Trust == MessageTrust.AuthenticatedUser).Select(x => x.Revision).DefaultIfEmpty(0).Max();
    private void CancelPendingWakes(LedgerState state, ConversationIdentity identity, string evidence)
    {
        foreach (var wake in state.ChannelWakes.Where(x => x.Identity == identity && x.Stage == ChannelWakeStage.Pending).ToArray())
            Replace(state.ChannelWakes, wake, wake with { Stage = ChannelWakeStage.Cancelled,
                FinishedAt = _clock.UtcNow.ToUniversalTime(), Evidence = evidence });
    }
    private static bool WakeReason(OutboxRejectionReason reason) =>
        reason is OutboxRejectionReason.ContextUnavailable or OutboxRejectionReason.ContextExpired;
    private bool CurrentWakeSource(LedgerState state, ChannelWakeRecord wake, ChannelWakeSource source, bool checkQuiet)
    {
        var output = state.Outbox.SingleOrDefault(x => x.Identity == wake.Identity && x.Id == source.OutboxId);
        return output != null && output.SendAttempts == source.SendAttempt && output.RejectionReason == source.RejectionReason
            && WakeSourceEligible(state, output, wake.EpisodeRevision, checkQuiet);
    }
    private bool WakeSourceEligible(LedgerState state, OutboxMessage output, long episode, bool checkQuiet)
    {
        if (output.Kind != OutboxKind.Notification || output.Stage != OutboxStage.Rejected || !WakeReason(output.RejectionReason)
            || output.SendAttempts < 1 || output.AttemptTrustedInputRevision != episode) return false;
        var entry = RequireEvent(state, output.Identity, output.NotificationEventId!.Value);
        if (UnclassifiedRequiredDecision(entry)) return false;
        if (entry.RequiredDecisionSuppressedAt != null) return false;
        var catchUpSnapshot = SnapshotOf(state, output.Identity);
        if (!OutputDependencyReady(catchUpSnapshot, output)) return false;
        if (CatchUpOriginalHeld(catchUpSnapshot, output)) return false;
        if (CatchUpBatchFor(catchUpSnapshot, output) is { } batch
            && !CatchUpPageReady(catchUpSnapshot, batch, output, _clock.UtcNow, enforceBudget: false, checkQuiet: checkQuiet)) return false;
        if (entry.RequiredDecision is { } decision && decision.ExpiresAt <= _clock.UtcNow) return false;
        if (DefaultNotificationSuppressed(state.WorkItems, entry)) return false;
        var draft = state.NotificationDrafts.SingleOrDefault(x => x.EventId == entry.Id);
        if (draft == null) return true;
        if (draft.Cancelled) return false;
        if (draft.WorkRunId == null) return true;
        var run = RequireCommitmentRun(state, output.Identity, draft.WorkRunId.Value);
        var work = RequireCommitment(state, output.Identity, run.WorkItemId);
        return WorkAllowsNotification(work, run, _clock.UtcNow) && (!checkQuiet || !InCommitmentQuietHours(work.Policy, _clock.UtcNow));
    }
    private static ChannelWakeRecord RequireWake(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.ChannelWakes.SingleOrDefault(x => x.Id == id && x.Identity == identity)
        ?? throw Error(LedgerErrors.NotFound, "The wake was not found in this identity.");
    private static ChannelWakeRecord RequireWakeLease(LedgerState state, ChannelWakeLease lease)
    {
        var wake = RequireWake(state, lease.Identity, lease.WakeId);
        if (lease.ClaimId == Guid.Empty || wake.ClaimId != lease.ClaimId || wake.RouteId != lease.RouteId
            || wake.EpisodeRevision != lease.EpisodeRevision || wake.ReservedAttempts != 1 || wake.StartedAt == null)
            throw Error(LedgerErrors.IdentityMismatch, "The wake lease does not match its persisted attempt.");
        return wake;
    }
    private bool RecoverChannelWakes(LedgerState state)
    {
        var open = state.ChannelWakes.Where(x => x.StartedAt != null && x.ReleasedAt == null).ToArray();
        foreach (var wake in open)
        {
            var updated = wake with { ReleasedAt = _clock.UtcNow.ToUniversalTime() };
            if (wake.Stage == ChannelWakeStage.Sending) updated = updated with { Stage = ChannelWakeStage.Unknown,
                FinishedAt = _clock.UtcNow.ToUniversalTime(), Evidence = "The former process ended without a committed wake receipt." };
            Replace(state.ChannelWakes, wake, updated);
        }
        return open.Length != 0;
    }
    private static void ValidateWakeConfiguration(WakeChannelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Enabled && configuration.RouteId == Guid.Empty) throw new ArgumentException("An enabled wake route needs a nonsecret identity.");
        ValidateToken(configuration.TimeZoneId);
        _ = TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId);
        if (configuration.QuietStart == null && configuration.QuietEnd == null) return;
        if (!TimeOnly.TryParseExact(configuration.QuietStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(configuration.QuietEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start == end) throw new ArgumentException("Wake quiet hours require distinct valid start and end times.");
    }
    private static void ValidateChannelWakes(LedgerState state)
    {
        bool KnownEpisode(ConversationIdentity identity, long revision) => revision == 0 || revision > 0
            && state.Inbox.Any(x => x.Identity == identity && x.Trust == MessageTrust.AuthenticatedUser && x.Revision == revision);
        if (state.WakeChannels.Any(x => x == null) || state.ChannelWakes.Any(x => x == null)
            || state.WakeChannels.Select(x => x.Identity).Distinct().Count() != state.WakeChannels.Count
            || state.ChannelWakes.Select(x => x.Id).Distinct().Count() != state.ChannelWakes.Count
            || state.ChannelWakes.Select(x => (x.Identity, x.EpisodeRevision)).Distinct().Count() != state.ChannelWakes.Count
            || state.ChannelWakes.Count(x => x.StartedAt != null && x.ReleasedAt == null) > 1)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate wake settings, identities or episodes.");
        foreach (var channel in state.WakeChannels)
        {
            if (!state.Conversations.Any(x => x.Identity == channel.Identity)) throw Error(LedgerErrors.CorruptStorage, "Unknown wake identity.");
            try { ValidateWakeConfiguration(channel.Configuration); }
            catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw Error(LedgerErrors.CorruptStorage, "Invalid persisted wake configuration."); }
        }
        foreach (var output in state.Outbox)
            if (!Enum.IsDefined(output.RejectionReason)
                || output.AttemptTrustedInputRevision is { } revision && (output.SendAttempts < 1 || !KnownEpisode(output.Identity, revision))
                || output.RejectionReason != OutboxRejectionReason.None && (output.AttemptTrustedInputRevision == null
                    || output.Stage is not (OutboxStage.Rejected or OutboxStage.Pending or OutboxStage.Superseded)))
                throw Error(LedgerErrors.CorruptStorage, "Invalid structured outbox rejection provenance.");
        foreach (var wake in state.ChannelWakes)
        {
            var configuration = state.WakeChannels.SingleOrDefault(x => x.Identity == wake.Identity)?.Configuration;
            if (wake.Id == Guid.Empty || wake.RouteId == Guid.Empty || !Enum.IsDefined(wake.Stage) || !KnownEpisode(wake.Identity, wake.EpisodeRevision)
                || !state.Conversations.Any(x => x.Identity == wake.Identity) || configuration == null || wake.Sources.IsDefaultOrEmpty
                || wake.CreatedAt == default || wake.NotBefore < wake.CreatedAt || wake.NotBefore > wake.CreatedAt.AddSeconds(60)
                || wake.Sources.Any(x => x == null) || wake.Sources.Select(x => (x.OutboxId, x.SendAttempt)).Distinct().Count() != wake.Sources.Length)
                throw Error(LedgerErrors.CorruptStorage, "Invalid wake record or source scope.");
            foreach (var source in wake.Sources)
            {
                var output = state.Outbox.SingleOrDefault(x => x.Identity == wake.Identity && x.Id == source.OutboxId);
                if (output == null || output.Kind != OutboxKind.Notification || source.SendAttempt < 1 || source.SendAttempt > output.SendAttempts
                    || source.TrustedInputRevision != wake.EpisodeRevision || !WakeReason(source.RejectionReason)
                    || source.SendAttempt == output.SendAttempts && (output.AttemptTrustedInputRevision != source.TrustedInputRevision
                        || output.RejectionReason != source.RejectionReason))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid wake source attempt or identity.");
            }
            bool unstarted = wake.Stage is ChannelWakeStage.Pending or ChannelWakeStage.Cancelled;
            if (unstarted ? wake.ReservedAttempts != 0 || wake.ClaimId != null || wake.StartedAt != null || wake.ReleasedAt != null
                : wake.ReservedAttempts != 1 || wake.ClaimId is null || wake.ClaimId == Guid.Empty || wake.StartedAt is null || wake.StartedAt == default(DateTimeOffset))
                throw Error(LedgerErrors.CorruptStorage, "Invalid wake claim or attempt.");
            bool unfinished = wake.Stage is ChannelWakeStage.Pending or ChannelWakeStage.Sending;
            if (unfinished ? wake.FinishedAt != null || wake.Evidence != null || wake.ReleasedAt != null
                : wake.FinishedAt is null || string.IsNullOrWhiteSpace(wake.Evidence))
                throw Error(LedgerErrors.CorruptStorage, "Invalid wake outcome facts.");
            if (wake.Stage == ChannelWakeStage.Accepted)
            {
                try { ValidateToken(wake.PlatformReceipt!); }
                catch (ArgumentException) { throw Error(LedgerErrors.CorruptStorage, "Invalid accepted wake receipt."); }
            }
            else if (wake.PlatformReceipt != null) throw Error(LedgerErrors.CorruptStorage, "Unproven wake receipt.");
            if (wake.Stage == ChannelWakeStage.Pending && (wake.EpisodeRevision != TrustedInputRevision(state, wake.Identity)
                || !configuration.Enabled || configuration.RouteId != wake.RouteId))
                throw Error(LedgerErrors.CorruptStorage, "An old wake episode was not cancelled atomically with input.");
        }
    }
}
