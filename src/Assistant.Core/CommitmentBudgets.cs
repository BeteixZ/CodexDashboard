using System.Collections.Immutable;

namespace Assistant.Core;

public enum CommitmentBudgetKind { ReadCheck, ModelPreparation, Notification }
/// <summary>Trusted host configuration. No model-controlled tool can raise these limits.</summary>
public sealed record CommitmentBudgetOptions
{
    public int MaximumReadChecksPerHour { get; init; } = 60;
    public int MaximumReadChecksInFlight { get; init; } = 2;
    public int MaximumNotificationsPerDay { get; init; } = 20;
    public int MaximumNotificationsPerMinute { get; init; } = 5;
    public int MaximumModelPreparationsPerDay { get; init; } = 20;
}
/// <summary>
/// Immutable spending identity; ReleasedAt only closes a read's actual in-flight occupancy.
/// Closing occupancy never refunds its rolling-window charge.
/// </summary>
public sealed record CommitmentBudgetReservation(Guid Id, ConversationIdentity Identity,
    Guid WorkItemId, Guid RunId, CommitmentBudgetKind Kind, DateTimeOffset ReservedAt,
    Guid ResourceId, int Attempt, long WorkVersion, DateTimeOffset? ReleasedAt = null, Guid? CatchUpBatchId = null);
public sealed record CommitmentBudgetWindow(CommitmentBudgetKind Kind, TimeSpan Window,
    int Used, int Limit, DateTimeOffset? NextAvailableAt);
public sealed record CommitmentBudgetStatus(ConversationIdentity Identity, DateTimeOffset ObservedAt,
    int ReadChecksInFlight, int MaximumReadChecksInFlight, ImmutableArray<CommitmentBudgetWindow> Windows);

public sealed partial class DurableConversationLedger
{
    public async Task<CommitmentBudgetStatus> GetCommitmentBudgetStatusAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return BudgetStatus(identity, snapshot.CommitmentBudgetReservations, _clock.UtcNow);
    }

    /// <summary>
    /// Call only in a host finally after the actual read task has ended, even after cancellation.
    /// This releases occupancy, not spent budget or authority, and never applies stale observations.
    /// </summary>
    public Task<bool> ReleaseCommitmentCheckAsync(CommitmentCheckLease lease,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var run = RequireCommitmentRun(state, lease.Identity, lease.RunId);
        var work = RequireCommitment(state, lease.Identity, lease.WorkItemId);
        if (run.WorkItemId != work.Id || work.Task != lease.Task || lease.ClaimId == Guid.Empty)
            throw Error(LedgerErrors.IdentityMismatch, "The read lease does not identify this commitment.");
        bool changed = CloseReadReservation(state, lease);
        if (run.CheckClaimId == lease.ClaimId && run.CheckAttempts == lease.Attempt
            && run.WorkVersion == lease.WorkVersion && work.Version == lease.WorkVersion
            && run.Stage == CommitmentRunStage.Checking)
        {
            // Release is deliberately not a business observation. Advance handles budget exhaustion.
            bool terminal = work.Stage is TrackedWorkStage.Completed or TrackedWorkStage.Cancelled or TrackedWorkStage.Expired;
            Replace(state.CommitmentRuns, run, run with { Stage = terminal ? CommitmentRunStage.Skipped : CommitmentRunStage.PendingCheck,
                CheckClaimId = null, NotBefore = _clock.UtcNow.AddSeconds(30),
                Reason = terminal ? "Read task ended after the commitment reached a terminal state."
                    : "Read task ended without a committed fresh observation." });
            changed = true;
        }
        return new Mutation<bool>(changed, changed);
    }, cancellationToken);

    private static void ValidateBudgetOptions(CommitmentBudgetOptions options)
    {
        if (options == null || options.MaximumReadChecksPerHour is < 1 or > 100000
            || options.MaximumReadChecksInFlight is < 1 or > 2
            || options.MaximumNotificationsPerDay is < 1 or > 100000
            || options.MaximumNotificationsPerMinute is < 1 or > 100000
            || options.MaximumModelPreparationsPerDay is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    private CommitmentBudgetStatus BudgetStatus(ConversationIdentity identity,
        IEnumerable<CommitmentBudgetReservation> reservations, DateTimeOffset now)
    {
        var selected = reservations.Where(x => x.Identity == identity).ToArray();
        var limits = _options.CommitmentBudgets;
        CommitmentBudgetWindow Window(CommitmentBudgetKind kind, TimeSpan duration, int maximum)
        {
            var times = selected.Where(x => x.Kind == kind && x.ReservedAt > now.Subtract(duration))
                .Select(x => x.ReservedAt).Order().ToArray();
            return new(kind, duration, times.Length, maximum,
                times.Length < maximum ? null : times[times.Length - maximum].Add(duration));
        }
        return new(identity, now, selected.Count(x => x.Kind == CommitmentBudgetKind.ReadCheck && x.ReleasedAt == null),
            limits.MaximumReadChecksInFlight,
            [
                Window(CommitmentBudgetKind.ReadCheck, TimeSpan.FromHours(1), limits.MaximumReadChecksPerHour),
                Window(CommitmentBudgetKind.Notification, TimeSpan.FromDays(1), limits.MaximumNotificationsPerDay),
                Window(CommitmentBudgetKind.Notification, TimeSpan.FromMinutes(1), limits.MaximumNotificationsPerMinute),
                Window(CommitmentBudgetKind.ModelPreparation, TimeSpan.FromDays(1), limits.MaximumModelPreparationsPerDay)
            ]);
    }
    private bool BudgetAvailable(ConversationIdentity identity, IEnumerable<CommitmentBudgetReservation> reservations,
        CommitmentBudgetKind kind, DateTimeOffset now)
    {
        var status = BudgetStatus(identity, reservations, now);
        return (kind != CommitmentBudgetKind.ReadCheck || status.ReadChecksInFlight < status.MaximumReadChecksInFlight)
            && status.Windows.Where(x => x.Kind == kind).All(x => x.Used < x.Limit);
    }
    private void ReserveCommitmentBudget(LedgerState state, TrackedWorkItem work, CommitmentRun run,
        CommitmentBudgetKind kind, Guid resourceId, int attempt, Guid? catchUpBatchId = null)
    {
        if (!BudgetAvailable(work.Identity, state.CommitmentBudgetReservations, kind, _clock.UtcNow))
            throw Error(LedgerErrors.BudgetDeferred, "The shared commitment budget is unavailable; work remains pending without consuming an attempt.");
        state.CommitmentBudgetReservations.Add(new(Guid.NewGuid(), work.Identity, work.Id, run.Id,
            kind, _clock.UtcNow.ToUniversalTime(), resourceId, attempt, work.Version, CatchUpBatchId: catchUpBatchId));
    }
    private bool CloseReadReservation(LedgerState state, CommitmentCheckLease lease)
    {
        var reservation = state.CommitmentBudgetReservations.SingleOrDefault(x => x.Identity == lease.Identity
            && x.Kind == CommitmentBudgetKind.ReadCheck && x.RunId == lease.RunId && x.ResourceId == lease.ClaimId);
        if (reservation == null) return false; // A compatible legacy claim predates budget reservations.
        if (reservation.WorkItemId != lease.WorkItemId || reservation.Attempt != lease.Attempt || reservation.WorkVersion != lease.WorkVersion)
            throw Error(LedgerErrors.IdentityMismatch, "The read reservation does not match its lease.");
        if (reservation.ReleasedAt != null) return false;
        Replace(state.CommitmentBudgetReservations, reservation, reservation with { ReleasedAt = _clock.UtcNow });
        return true;
    }
    private bool RecoverCommitmentBudgetOccupancy(LedgerState state)
    {
        var open = state.CommitmentBudgetReservations.Where(x => x.Kind == CommitmentBudgetKind.ReadCheck && x.ReleasedAt == null).ToArray();
        foreach (var reservation in open)
            Replace(state.CommitmentBudgetReservations, reservation, reservation with { ReleasedAt = _clock.UtcNow });
        return open.Length != 0;
    }
    private static void ValidateCommitmentBudgets(LedgerState state)
    {
        var rows = state.CommitmentBudgetReservations;
        var spanBatchIds = state.CatchUpBatches.Where(x => x.ManifestVersion >= 2).Select(x => x.Id).ToHashSet();
        var spanCharges = rows.Where(x => x.CatchUpBatchId is { } id && spanBatchIds.Contains(id)).ToArray();
        if (spanCharges.Select(x => (x.CatchUpBatchId, x.RunId)).Distinct().Count() != spanCharges.Length)
            throw Error(LedgerErrors.CorruptStorage, "A span batch may charge a commitment run only once.");
        if (rows.Select(x => x.Id).Distinct().Count() != rows.Count
            || rows.Select(x => (x.Kind, x.ResourceId, x.Attempt, Run: x.CatchUpBatchId == null ? Guid.Empty : x.RunId)).Distinct().Count() != rows.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate commitment budget reservations.");
        foreach (var row in rows)
        {
            if (ValidateDependentNotificationBudget(state, row)) continue;
            var work = state.WorkItems.SingleOrDefault(x => x.Id == row.WorkItemId && x.Identity == row.Identity);
            var run = state.CommitmentRuns.SingleOrDefault(x => x.Id == row.RunId && x.WorkItemId == row.WorkItemId && x.Identity == row.Identity);
            if (row.Id == Guid.Empty || row.ResourceId == Guid.Empty || !Enum.IsDefined(row.Kind) || work == null || run == null
                || row.Attempt < 1 || row.WorkVersion < 1 || row.WorkVersion > work.Version
                || row.ReservedAt == default || row.ReservedAt.Offset != TimeSpan.Zero
                || row.Kind != CommitmentBudgetKind.ReadCheck && row.ReleasedAt != null)
                throw Error(LedgerErrors.CorruptStorage, "Invalid commitment budget association.");
            if (row.CatchUpBatchId is { } batchId)
            {
                var batch = state.CatchUpBatches.SingleOrDefault(x => x.Id == batchId && x.Identity == row.Identity);
                var output = state.Outbox.SingleOrDefault(x => x.Id == row.ResourceId && x.Identity == row.Identity);
                if (row.Kind != CommitmentBudgetKind.Notification || batch == null || output == null
                    || output.NotificationEventId != batch.DeliveryEventId || row.Attempt > output.SendAttempts
                    || !batch.Members.Any(x => x.WorkRunId == row.RunId && x.WorkVersion == row.WorkVersion && x.CatchUpOutboxId == row.ResourceId))
                    throw Error(LedgerErrors.CorruptStorage, "Invalid catch-up delivery budget proof.");
            }
            else if (row.Kind == CommitmentBudgetKind.ReadCheck)
            {
                if (row.Attempt > run.CheckAttempts || row.Attempt > 8
                    || state.Outbox.Any(x => x.Id == row.ResourceId)
                    || run.CheckClaimId != null && run.CheckAttempts == row.Attempt && run.CheckClaimId != row.ResourceId
                    || state.Subscriptions.SingleOrDefault(x => x.WorkItemId == work.Id)?.Condition != SubscriptionCondition.NoActivity)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid read-check budget proof.");
            }
            else if (row.Kind == CommitmentBudgetKind.ModelPreparation)
            {
                var draft = state.NotificationDrafts.SingleOrDefault(x => x.Id == run.NotificationDraftId && x.WorkRunId == run.Id);
                if (draft == null || row.Attempt > draft.PreparationAttempts || row.Attempt > 16)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid model preparation budget proof.");
                if (state.Outbox.Any(x => x.Id == row.ResourceId)
                    || draft.PreparationId != null && draft.PreparationAttempts == row.Attempt && draft.PreparationId != row.ResourceId)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid model preparation claim identity.");
            }
            else
            {
                var output = state.Outbox.SingleOrDefault(x => x.Id == row.ResourceId && x.Identity == row.Identity
                    && x.NotificationEventId == run.NotificationEventId && x.Kind == OutboxKind.Notification);
                if (output == null || row.Attempt > output.SendAttempts)
                    throw Error(LedgerErrors.CorruptStorage, "Invalid notification send budget proof.");
            }
        }
    }
}
