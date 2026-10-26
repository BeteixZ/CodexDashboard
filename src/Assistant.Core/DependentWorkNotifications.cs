using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public static DependentWorkNotice? DependentNoticeFor(ConversationSnapshot snapshot, DependentWorkItem work)
    {
        if (work.Identity != snapshot.Identity || work.NotificationPolicy == null) return null;
        if (HasDependentNotice(work.Stage)) return new(work.Id, work.Version, work.Stage);
        if (work.Stage is not (DependentWorkStage.Running or DependentWorkStage.Completed) || work.AttemptId is not { } attemptId) return null;
        // One milestone for an actual attempt, even if pause/resume increments the work version.
        var recorded = snapshot.Events.SingleOrDefault(e => e.DependentWorkNotice is { } n && n.WorkItemId == work.Id
            && n.AttemptId == attemptId && n.Stage == work.Stage);
        if (recorded != null) return recorded.DependentWorkNotice;
        var attempt = snapshot.DependentWork!.Attempts.Single(a => a.Id == attemptId);
        if (attempt.Stage != DependentDispatchStage.Accepted || attempt.ResultTask != work.ResultTask
            || work.Stage == DependentWorkStage.Completed && work.Completion?.State != CommitmentTaskState.Completed) return null;
        return attempt.RecoveryOfEventId is { } warningId ? new(work.Id, work.Version, work.Stage, warningId, attemptId) : null;
    }

    // Attach recovery to newly committed progress, never to a scan of historical terminal work.
    private static DependentDispatchAttempt BindDependentRecovery(LedgerState state, DependentWorkItem work, DependentDispatchAttempt attempt)
    {
        if (attempt.RecoveryOfEventId != null || work.NotificationPolicy == null || attempt.Stage != DependentDispatchStage.Accepted) return attempt;
        var warning = state.Events.Where(e => e.Identity == work.Identity && e.DependentWorkNotice is { RecoveryOfEventId: null } n && n.WorkItemId == work.Id
            && n.WorkVersion < attempt.WorkVersion && n.Stage is DependentWorkStage.NeedsDecision or DependentWorkStage.Expired
            && e.RecordedAt <= attempt.StartedAt && e.NotificationSegments != null
            && state.Outbox.Count(o => o.NotificationEventId == e.Id && o.Stage == OutboxStage.Accepted) == e.NotificationSegments.Value.Length)
            .OrderByDescending(e => e.DependentWorkNotice!.WorkVersion).FirstOrDefault();
        if (warning == null) return attempt;
        state.SchemaVersion = Math.Max(state.SchemaVersion, 10);
        return attempt with { RecoveryOfEventId = warning.Id };
    }

    /// <summary>Read the published immutable state at the channel's actual before-send boundary.</summary>
    public bool IsDependentNotificationCurrent(OutboxMessage output)
    {
        if (output.Kind != OutboxKind.Notification) return true;
        if (IsReadOnly) return false;
        var state = Volatile.Read(ref _state);
        var entry = state.Events.Single(e => e.Id == output.NotificationEventId && e.Identity == output.Identity);
        if (entry.DependentWorkNotice is not { } notice) return true;
        var work = RequireDependentWork(state, output.Identity, notice.WorkItemId);
        return work.Version == notice.WorkVersion && work.Stage == notice.Stage
            && !InCommitmentQuietHours(work.NotificationPolicy ?? new(), _clock.UtcNow);
    }

    private static void SupersedeDependentNotices(LedgerState state, DependentWorkItem work)
    {
        var ids = state.Events.Where(e => e.Identity == work.Identity && e.DependentWorkNotice is { } n
            && n.WorkItemId == work.Id && (n.WorkVersion != work.Version || n.Stage != work.Stage)).Select(e => e.Id).ToHashSet();
        foreach (var output in state.Outbox.Where(o => o.NotificationEventId is { } id && ids.Contains(id)
            && o.Stage is OutboxStage.Pending or OutboxStage.Rejected).ToArray())
            Replace(state.Outbox, output, output with { Stage = OutboxStage.Superseded, UpdatedAt = work.UpdatedAt });
    }
    // The host supplies factual text; the transaction pins it to the exact current work state.
    public Task<NotificationEnqueueResult?> EnqueueDependentWorkNotificationAsync(ConversationIdentity identity,
        DependentWorkNotice expected, string body, IReadOnlyList<string> segments, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var work = RequireDependentWork(state, identity, expected.WorkItemId);
        if (work.Version != expected.WorkVersion || work.Stage != expected.Stage || DependentNoticeFor(SnapshotOf(state, identity), work) != expected)
            return Unchanged<NotificationEnqueueResult?>(null);
        ValidateBody(body);
        var parts = segments.ToImmutableArray();
        if (parts.IsDefaultOrEmpty || parts.Length > _options.MaximumSegments) throw new ArgumentException("Notification segments required.");
        foreach (var part in parts) ValidateBody(part);
        string key = DependentNoticeKey(expected);
        var existing = state.Events.SingleOrDefault(e => e.Identity == identity && e.ExternalEventKey == key);
        if (existing != null)
        {
            if (existing.DependentWorkNotice != expected || existing.Body != body || !existing.NotificationSegments!.Value.SequenceEqual(parts))
                throw Error(LedgerErrors.Conflict, "Different facts for the same dependent work notice.");
            return Unchanged<NotificationEnqueueResult?>(new(existing,
                state.Outbox.Where(o => o.NotificationEventId == existing.Id).OrderBy(o => o.SegmentIndex).ToImmutableArray(), true));
        }
        var now = _clock.UtcNow;
        var entry = new ConversationEvent(Guid.NewGuid(), identity, ConversationSource.Notification, body, work.UpdatedAt, now,
            Revision(state, identity), Task: work.ResultTask ?? work.Target, ExternalEventKey: key, NotificationSegments: parts,
            OutputDependencies: OutputDependencies.Independent, DependentWorkNotice: expected);
        var outputs = parts.Select((part, index) => new OutboxMessage(Guid.NewGuid(), identity, null, entry.Revision,
            index, part, OutboxStage.Pending, now, now, Kind: OutboxKind.Notification, NotificationEventId: entry.Id)).ToImmutableArray();
        state.Events.Add(entry); NoteNotificationContext(state, entry); state.Outbox.AddRange(outputs);
        state.SchemaVersion = Math.Max(state.SchemaVersion, expected.RecoveryOfEventId == null ? 9 : 10);
        return Changed<NotificationEnqueueResult?>(new(entry, outputs, false));
    }, cancellationToken);

    private static bool HasDependentNotice(DependentWorkStage stage) =>
        stage is DependentWorkStage.Failed or DependentWorkStage.Expired or DependentWorkStage.NeedsDecision;
    private static string DependentNoticeKey(DependentWorkNotice notice) => notice.AttemptId is { } id
        ? $"dependent-work/{notice.WorkItemId:N}/attempt/{id:N}/{notice.Stage}"
        : $"dependent-work/{notice.WorkItemId:N}/{notice.WorkVersion}/{notice.Stage}";

    private static bool DependentNotificationReady(IEnumerable<DependentWorkItem> works, IEnumerable<ConversationEvent> events,
        IEnumerable<CommitmentBudgetReservation> reservations, OutboxMessage output, DateTimeOffset now)
    {
        if (output.Kind != OutboxKind.Notification || events.Single(e => e.Id == output.NotificationEventId).DependentWorkNotice is not { } notice) return true;
        var work = works.Single(w => w.Id == notice.WorkItemId && w.Identity == output.Identity);
        var policy = work.NotificationPolicy ?? new();
        if (work.Version != notice.WorkVersion || work.Stage != notice.Stage || InCommitmentQuietHours(policy, now)) return false;
        // Later pages consume the already reserved logical notification, as reminders do.
        if (output.SegmentIndex > 0 && output.SendAttempts == 0) return true;
        var own = reservations.Where(r => r.Identity == output.Identity && r.WorkItemId == work.Id && r.Kind == CommitmentBudgetKind.Notification).ToArray();
        return own.Count(r => r.RunId == output.NotificationEventId) < policy.MaximumDeliveryAttempts
            && own.Count(r => r.ReservedAt > now.AddHours(-24)) < policy.MaximumNotificationsPer24Hours
            && !own.Any(r => r.ReservedAt.AddSeconds(policy.MinimumIntervalSeconds) > now);
    }

    private bool ReserveDependentNotification(LedgerState state, OutboxMessage output)
    {
        if (state.Events.Single(e => e.Id == output.NotificationEventId).DependentWorkNotice is not { } notice) return false;
        if (output.SegmentIndex > 0 && output.SendAttempts == 0) return true;
        if (!BudgetAvailable(output.Identity, state.CommitmentBudgetReservations, CommitmentBudgetKind.Notification, _clock.UtcNow))
            throw Error(LedgerErrors.BudgetDeferred, "The shared notification budget is unavailable.");
        state.CommitmentBudgetReservations.Add(new(Guid.NewGuid(), output.Identity, notice.WorkItemId, output.NotificationEventId!.Value,
            CommitmentBudgetKind.Notification, _clock.UtcNow.ToUniversalTime(), output.Id, output.SendAttempts + 1, notice.WorkVersion));
        return true;
    }

    private static void ValidateDependentNotices(LedgerState state)
    {
        foreach (var entry in state.Events.Where(e => e.DependentWorkNotice != null || e.ExternalEventKey?.StartsWith("dependent-work/", StringComparison.Ordinal) == true))
        {
            var notice = entry.DependentWorkNotice ?? throw Error(LedgerErrors.CorruptStorage, "Dependent notice marker is missing.");
            var work = RequireDependentWork(state, entry.Identity, notice.WorkItemId);
            bool recovery = notice.RecoveryOfEventId != null;
            if (state.SchemaVersion < (recovery ? 10 : 9) || work.NotificationPolicy == null || notice.WorkVersion < 1 || notice.WorkVersion > work.Version
                || (recovery ? !ValidDependentRecovery(state, entry, work) : notice.AttemptId != null || !HasDependentNotice(notice.Stage))
                || entry.ExternalEventKey != DependentNoticeKey(notice) || entry.Source != ConversationSource.Notification
                || entry.NotificationSegments == null || entry.IsDefaultTaskNotification || entry.RequiredDecision != null
                || entry.Task == null || entry.Task.HostId != work.Target.HostId || entry.Task.ThreadId != work.Target.ThreadId || entry.Task.ProfileId != work.Target.ProfileId
                || state.NotificationDrafts.Any(d => d.EventId == entry.Id))
                throw Error(LedgerErrors.CorruptStorage, "Invalid dependent work notice association.");
        }
    }

    private static bool ValidDependentRecovery(LedgerState state, ConversationEvent entry, DependentWorkItem work)
    {
        var notice = entry.DependentWorkNotice!;
        var attempt = state.DependentAttempts.SingleOrDefault(a => a.Identity == work.Identity && a.WorkItemId == work.Id && a.Id == notice.AttemptId);
        return notice.Stage is DependentWorkStage.Running or DependentWorkStage.Completed
            && attempt != null && attempt.RecoveryOfEventId == notice.RecoveryOfEventId && ValidDependentRecoveryReference(state, work, attempt)
            && attempt.WorkVersion <= notice.WorkVersion && entry.Task == attempt.ResultTask
            && (notice.Stage != DependentWorkStage.Completed || work.Completion?.State == CommitmentTaskState.Completed);
    }

    private static bool ValidDependentRecoveryReference(LedgerState state, DependentWorkItem work, DependentDispatchAttempt attempt)
    {
        var warning = state.Events.SingleOrDefault(e => e.Identity == work.Identity && e.Id == attempt.RecoveryOfEventId);
        return state.SchemaVersion >= 10 && work.NotificationPolicy != null
            && warning?.DependentWorkNotice is { RecoveryOfEventId: null } prior && prior.WorkItemId == work.Id
            && prior.Stage is DependentWorkStage.NeedsDecision or DependentWorkStage.Expired
            && attempt.Stage == DependentDispatchStage.Accepted && prior.WorkVersion < attempt.WorkVersion && warning.RecordedAt <= attempt.StartedAt
            && warning.NotificationSegments is { } parts
            && state.Outbox.Count(o => o.NotificationEventId == warning.Id && o.Stage == OutboxStage.Accepted) == parts.Length;
    }

    private static bool ValidateDependentNotificationBudget(LedgerState state, CommitmentBudgetReservation row)
    {
        var work = state.DependentWork.SingleOrDefault(w => w.Id == row.WorkItemId && w.Identity == row.Identity);
        if (work == null) return false;
        var entry = state.Events.SingleOrDefault(e => e.Id == row.RunId && e.Identity == row.Identity);
        var output = state.Outbox.SingleOrDefault(o => o.Id == row.ResourceId && o.Identity == row.Identity && o.NotificationEventId == row.RunId);
        if (state.SchemaVersion < 9 || row.Id == Guid.Empty || row.Kind != CommitmentBudgetKind.Notification || row.ReleasedAt != null || row.CatchUpBatchId != null
            || row.ReservedAt == default || row.ReservedAt.Offset != TimeSpan.Zero || row.Attempt < 1 || row.WorkVersion < 1 || row.WorkVersion > work.Version
            || entry?.DependentWorkNotice?.WorkItemId != work.Id || entry.DependentWorkNotice.WorkVersion != row.WorkVersion
            || output == null || row.Attempt > output.SendAttempts)
            throw Error(LedgerErrors.CorruptStorage, "Invalid dependent notification budget proof.");
        return true;
    }
}
