using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public async Task<CatchUpSnapshot> GetCatchUpAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return new(snapshot.CatchUpWindows, snapshot.CatchUpBatches);
    }

    /// <summary>Prepares bounded, lossless source spans; no model, truncation or user authority.</summary>
    public Task<CatchUpCaptureResult> PrepareCatchUpAsync(ConversationIdentity identity, string sourceMessageId,
        CancellationToken cancellationToken = default, IReadOnlyList<Guid>? verifiedRequiredDecisionEventIds = null) => ChangeAsync(state =>
    {
        var window = RequireCatchUpWindow(state, identity, sourceMessageId);
        if (window.TrustedInputRevision != TrustedInputRevision(state, identity))
            throw Error(LedgerErrors.StaleRevision, "This catch-up input is no longer current.");
        bool verifiedChanged = ApplyRequiredDecisionVerification(state, ref window, verifiedRequiredDecisionEventIds ?? []);
        var existing = state.CatchUpBatches.SingleOrDefault(x => x.WindowId == window.Id);
        CatchUpCaptureResult Result(CatchUpWindow selected, CatchUpBatch? batch) => new(selected, batch,
            batch == null ? [] : state.Outbox.Where(x => x.NotificationEventId == batch.DeliveryEventId).OrderBy(x => x.SegmentIndex).ToImmutableArray());
        if (window.Stage != CatchUpWindowStage.Captured)
        {
            bool retired = existing != null && RetireInvalidCatchUpPages(state, existing);
            return new Mutation<CatchUpCaptureResult>(Result(window, existing), verifiedChanged || retired);
        }
        if (window.DialogueEpoch != ContextFor(state, identity).Epoch)
        {
            var held = window with { Stage = CatchUpWindowStage.Deferred, DeferredEventCount = window.CandidateEventIds.Length };
            Replace(state.CatchUpWindows, window, held); return Changed(Result(held, null));
        }
        return Changed(PrepareSpanCatchUp(state, window));
    }, cancellationToken);

    /// <summary>Permanent suppression only for a host-confirmed resolved/invalid exact approval, not temporary offline state.</summary>
    public Task<bool> SuppressRequiredDecisionAsync(ConversationIdentity identity, Guid eventId,
        RequiredDecisionNotice expectedDecision, string reason, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateBody(reason);
        var entry = RequireEvent(state, identity, eventId);
        if (entry.RequiredDecision != expectedDecision || expectedDecision == null)
            throw Error(LedgerErrors.IdentityMismatch, "Suppression requires the exact persisted decision proof.");
        if (entry.RequiredDecisionSuppressedAt != null) return Unchanged(false);
        state.SchemaVersion = Math.Max(state.SchemaVersion, 2);
        Replace(state.Events, entry, entry with { RequiredDecisionSuppressedAt = _clock.UtcNow, RequiredDecisionSuppressionReason = reason });
        foreach (var window in state.CatchUpWindows.Where(x => x.Identity == identity && x.VerifiedRequiredDecisionEventIds.Contains(eventId)).ToArray())
            Replace(state.CatchUpWindows, window, window with { VerifiedRequiredDecisionEventIds = window.VerifiedRequiredDecisionEventIds.Remove(eventId) });
        return Changed(true);
    }, cancellationToken);

    private bool ApplyRequiredDecisionVerification(LedgerState state, ref CatchUpWindow window, IReadOnlyList<Guid> ids)
    {
        var proposed = ids.Distinct().Order().ToImmutableArray();
        if (proposed.Length != ids.Count) throw new ArgumentException("Duplicate verified approval IDs.");
        var currentWindow = window;
        // Newly created approvals also need explicit current live qualification, including during a window.
        var candidates = window.RequiredDecisionEventIds.Concat(state.Events.Where(x => x.Identity == currentWindow.Identity
            && x.RequiredDecision != null && state.Outbox.Any(o => o.NotificationEventId == x.Id
                && o.Stage is OutboxStage.Pending or OutboxStage.Rejected)).Select(x => x.Id)).Distinct().ToImmutableArray();
        foreach (var id in proposed)
        {
            var entry = RequireEvent(state, window.Identity, id);
            if (!candidates.Contains(id) || entry.RequiredDecision == null)
                throw Error(LedgerErrors.IdentityMismatch, "A verified approval must belong to this conversation's exact pending candidates.");
        }
        // A deadline/context can change between the host's read and this transaction. Revoke it, do not lose the input.
        var verified = proposed.Where(id => currentWindow.Input.HasFreshChannelContext
            && currentWindow.DialogueEpoch == ContextFor(state, currentWindow.Identity).Epoch
            && state.Events.Single(x => x.Id == id) is { RequiredDecision: { } proof, RequiredDecisionSuppressedAt: null }
            && proof.ExpiresAt > _clock.UtcNow).ToImmutableArray();
        bool changed = !window.VerifiedRequiredDecisionEventIds.SequenceEqual(verified) || !window.RequiredDecisionEventIds.SequenceEqual(candidates);
        if (changed)
        {
            var next = window with { RequiredDecisionEventIds = candidates, VerifiedRequiredDecisionEventIds = verified };
            Replace(state.CatchUpWindows, window, next); window = next;
        }
        foreach (var id in verified)
        {
            var rows = state.Outbox.Where(x => x.NotificationEventId == id).ToArray();
            if (rows.Any(x => x.Stage is OutboxStage.Unknown or OutboxStage.Sending)) continue;
            long revision = window.TrustedInputRevision;
            foreach (var output in rows.Where(x => x.Stage == OutboxStage.Rejected && x.AttemptTrustedInputRevision != revision))
            { Replace(state.Outbox, output, output with { Stage = OutboxStage.Pending, UpdatedAt = _clock.UtcNow }); changed = true; }
        }
        return changed;
    }

    /// <summary>The host must reverify the actual live provider request before calling this method.</summary>
    public Task<ImmutableArray<OutboxMessage>> RetryRequiredDecisionForInputAsync(ConversationIdentity identity,
        string sourceMessageId, Guid eventId, TaskReference task, RequiredDecisionNotice decision,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var window = RequireCatchUpWindow(state, identity, sourceMessageId);
        if (window.TrustedInputRevision != TrustedInputRevision(state, identity) || window.DialogueEpoch != ContextFor(state, identity).Epoch)
            throw Error(LedgerErrors.StaleRevision, "The approval recovery input is no longer current.");
        var entry = RequireEvent(state, identity, eventId);
        if (!window.RequiredDecisionEventIds.Contains(eventId) || entry.RequiredDecision != decision || entry.Task != task || entry.RequiredDecisionSuppressedAt != null
            || decision.ExpiresAt <= _clock.UtcNow || !window.Input.HasFreshChannelContext)
            throw Error(LedgerErrors.IdentityMismatch, "The verified live approval does not match this recovery window.");
        var outputs = state.Outbox.Where(x => x.NotificationEventId == eventId).OrderBy(x => x.SegmentIndex).ToArray();
        // A single uncertain page prevents automatic replay of any part of this approval group.
        if (outputs.Any(x => x.Stage is OutboxStage.Unknown or OutboxStage.Sending)) return Unchanged(ImmutableArray<OutboxMessage>.Empty);
        bool changed = false;
        if (!window.VerifiedRequiredDecisionEventIds.Contains(eventId))
        {
            Replace(state.CatchUpWindows, window, window with { VerifiedRequiredDecisionEventIds = window.VerifiedRequiredDecisionEventIds.Add(eventId) });
            changed = true;
        }
        foreach (var output in outputs.Where(x => x.Stage == OutboxStage.Rejected && x.AttemptTrustedInputRevision != window.TrustedInputRevision))
        { Replace(state.Outbox, output, output with { Stage = OutboxStage.Pending, UpdatedAt = _clock.UtcNow }); changed = true; }
        return new Mutation<ImmutableArray<OutboxMessage>>(state.Outbox.Where(x => x.NotificationEventId == eventId && x.Stage == OutboxStage.Pending)
            .OrderBy(x => x.SegmentIndex).ToImmutableArray(), changed);
    }, cancellationToken);

    /// <summary>Presentation order only. BeginOutboxSend independently enforces the same durable fence.</summary>
    public static int OutboxPriority(ConversationSnapshot snapshot, OutboxMessage message)
    {
        if (message.Kind != OutboxKind.Notification) return 2;
        var entry = snapshot.Events.Single(x => x.Id == message.NotificationEventId);
        return entry.RequiredDecision != null ? 0 : snapshot.CatchUpBatches.Any(x => x.DeliveryEventId == entry.Id) ? 1 : 3;
    }

    private void CaptureCatchUpWindow(LedgerState state, InboxMessage input, CatchUpInputOptions? options)
    {
        // Even hosts that do not request a new window cannot revive old pending batches on new input.
        foreach (var old in state.CatchUpWindows.Where(x => x.Identity == input.Identity && x.Stage != CatchUpWindowStage.Superseded).ToArray())
        {
            Replace(state.CatchUpWindows, old, old with { Stage = CatchUpWindowStage.Superseded });
            foreach (var batch in state.CatchUpBatches.Where(x => x.WindowId == old.Id))
                foreach (var output in state.Outbox.Where(x => x.NotificationEventId == batch.DeliveryEventId && x.Stage == OutboxStage.Pending).ToArray())
                    Replace(state.Outbox, output, output with { Stage = OutboxStage.Superseded, UpdatedAt = _clock.UtcNow });
        }
        if (options == null) return;
        ValidateCatchUpInput(options);
        if (state.CatchUpWindows.Count >= _options.CatchUp.MaximumWindows)
            throw Error(LedgerErrors.CapacityExceeded, "Catch-up history is full; the input was not committed.");
        var batchEventIds = state.CatchUpBatches.Select(x => x.DeliveryEventId).ToHashSet();
        // These are current-state notices with their own version fence and shared charge. Keep them individually recoverable.
        var pending = state.Events.Where(x => x.Identity == input.Identity && x.ExternalEventKey != null && x.DependentWorkNotice == null && !UnclassifiedRequiredDecision(x) && !batchEventIds.Contains(x.Id)
            && state.Outbox.Any(o => o.NotificationEventId == x.Id && o.Stage is OutboxStage.Pending or OutboxStage.Rejected)).ToArray();
        var deferred = options.Disposition == CatchUpDisposition.HoldHistory || !options.HasFreshChannelContext;
        state.SchemaVersion = Math.Max(state.SchemaVersion, 2); // Never downgrade the dependency feature gate.
        state.CatchUpWindows.Add(new(Guid.NewGuid(), input.Identity, input.SourceMessageId, input.Revision,
            ContextFor(state, input.Identity).Epoch, _clock.UtcNow, options,
            deferred ? CatchUpWindowStage.Deferred : CatchUpWindowStage.Captured,
            pending.Where(x => x.RequiredDecision == null).Select(x => x.Id).ToImmutableArray(),
            pending.Where(x => x.RequiredDecision != null).Select(x => x.Id).ToImmutableArray(), [],
            deferred ? pending.Count(x => x.RequiredDecision == null) : 0));
    }
    private static CatchUpWindow RequireCatchUpWindow(LedgerState state, ConversationIdentity identity, string source) =>
        state.CatchUpWindows.SingleOrDefault(x => x.Identity == identity && x.SourceMessageId == source)
        ?? throw Error(LedgerErrors.NotFound, "This authenticated input has no durable catch-up window.");
    private static CatchUpWindow? CurrentCatchUpWindow(ConversationSnapshot snapshot) => snapshot.CatchUpWindows
        .Where(x => x.Stage != CatchUpWindowStage.Superseded).OrderByDescending(x => x.TrustedInputRevision).FirstOrDefault();
    private static CatchUpBatch? CatchUpBatchFor(ConversationSnapshot snapshot, OutboxMessage output) =>
        snapshot.CatchUpBatches.SingleOrDefault(x => x.DeliveryEventId == output.NotificationEventId);
    private static bool CatchUpSourceOccupied(ConversationSnapshot snapshot, Guid sourceEventId) => snapshot.CatchUpBatches
        .SelectMany(x => x.Members).Where(x => x.SourceEventId == sourceEventId)
        .Any(x => snapshot.Outbox.Single(o => o.Id == x.CatchUpOutboxId).Stage is OutboxStage.Pending or OutboxStage.Sending or OutboxStage.Accepted or OutboxStage.Unknown);
    private static bool CatchUpOriginalHeld(ConversationSnapshot snapshot, OutboxMessage output)
    {
        if (output.Kind != OutboxKind.Notification) return false;
        var id = output.NotificationEventId!.Value;
        if (CatchUpSourceOccupied(snapshot, id)) return true;
        // Preserve a captured backlog hold even if a later legacy caller receives input without CatchUp options.
        return snapshot.CatchUpWindows.Any(x => x.CandidateEventIds.Contains(id));
    }
    private bool CompleteCatchUpSource(ConversationSnapshot snapshot, ConversationEvent entry, OutboxMessage[] outputs, DateTimeOffset now)
    {
        if (entry.RequiredDecision != null || UnclassifiedRequiredDecision(entry) || outputs.Length == 0 || CatchUpSourceOccupied(snapshot, entry.Id)
            || outputs.Any(x => x.Stage != OutboxStage.Rejected && !(x.Stage == OutboxStage.Pending && x.SendAttempts == 0))) return false;
        return outputs.All(x => OutputDependencyReady(snapshot, x)
            && MessagePolicyReady(snapshot.WorkItems, snapshot.CommitmentRuns, snapshot.NotificationDrafts, snapshot.Events, x, now));
    }
    private static string CatchUpHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string CatchUpGroup(ConversationSnapshot snapshot, ConversationEvent entry, CommitmentRun? run)
    {
        var work = run == null ? null : snapshot.WorkItems.Single(x => x.Id == run.WorkItemId);
        string category = work?.Kind == TrackedWorkKind.Reminder ? "提醒" : work?.Kind == TrackedWorkKind.Subscription ? "订阅记录"
            : entry.Task != null ? "任务记录" : "待补报记录";
        return category + "|" + (entry.Task == null ? "" : string.Join("/", entry.Task.ProfileId, entry.Task.HostId, entry.Task.ThreadId, entry.Task.TurnId));
    }
    private static string CatchUpHeading(string group) => "你不在的时候，这些" + group.Split('|')[0] + "还没发出：\n";
    private static string RenderCatchUpSource(ConversationEvent entry, IEnumerable<OutboxMessage> outputs, string zoneId) =>
        TimeZoneInfo.ConvertTime(entry.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)
        + " 记录：\n" + string.Join("\n", outputs.OrderBy(x => x.SegmentIndex).Select(x => x.Body));

    private bool CatchUpPageReady(ConversationSnapshot snapshot, CatchUpBatch batch, OutboxMessage output, DateTimeOffset now,
        bool enforceBudget = true, bool checkQuiet = true)
    {
        var window = snapshot.CatchUpWindows.Single(x => x.Id == batch.WindowId);
        if (window.Stage != CatchUpWindowStage.Prepared || CurrentCatchUpWindow(snapshot)?.Id != window.Id
            || snapshot.Context!.Epoch != window.DialogueEpoch) return false;
        if (!OutputDependencyReady(snapshot, output)) return false;
        if (batch.ManifestVersion >= 2) return SpanPageReady(snapshot, batch, output, now, enforceBudget, checkQuiet);
        var members = batch.Members.Where(x => x.CatchUpOutboxId == output.Id).ToArray();
        if (members.Length == 0) return false;
        foreach (var member in members)
        {
            var original = snapshot.Events.Single(x => x.Id == member.SourceEventId);
            foreach (var source in member.Sources)
            {
                var old = snapshot.Outbox.Single(x => x.Id == source.OutboxId);
                if (old.SendAttempts != source.SendAttempts || old.Stage != OutboxStage.Rejected && !(old.Stage == OutboxStage.Pending && old.SendAttempts == 0)
                    || !(enforceBudget
                        ? MessagePolicyReady(snapshot.WorkItems, snapshot.CommitmentRuns, snapshot.NotificationDrafts, snapshot.Events, old, now)
                        : CatchUpSourcePolicyReady(snapshot, original, now, checkQuiet))) return false;
            }
            if (member.WorkRunId is { } id && snapshot.CommitmentRuns.Single(x => x.Id == id).WorkVersion != member.WorkVersion) return false;
            if (original.RequiredDecision != null) return false;
        }
        if (!enforceBudget) return true;
        var chargedRuns = members.Where(x => x.WorkRunId != null).Select(x => x.WorkRunId!.Value).Distinct()
            .Select(id => snapshot.CommitmentRuns.Single(x => x.Id == id)).ToArray();
        foreach (var workGroup in chargedRuns.GroupBy(x => x.WorkItemId))
        {
            var work = snapshot.WorkItems.Single(x => x.Id == workGroup.Key);
            var attempts = snapshot.CommitmentRuns.Where(x => x.WorkItemId == work.Id).SelectMany(x =>
                x.DeliveryAttempts.IsDefault ? ImmutableArray<DateTimeOffset>.Empty : x.DeliveryAttempts);
            if (attempts.Count(x => x > now.AddHours(-24)) + workGroup.Count() > work.Policy.MaximumNotificationsPer24Hours
                || workGroup.Count() > 1 && work.Policy.MinimumIntervalSeconds > 0) return false;
        }
        int charges = chargedRuns.Length;
        var budget = BudgetStatus(snapshot.Identity, snapshot.CommitmentBudgetReservations, now);
        return budget.Windows.Where(x => x.Kind == CommitmentBudgetKind.Notification).All(x => x.Used + charges <= x.Limit);
    }
    private static bool CatchUpSourcePolicyReady(ConversationSnapshot snapshot, ConversationEvent entry, DateTimeOffset now, bool checkQuiet)
    {
        if (entry.RequiredDecision != null || UnclassifiedRequiredDecision(entry) || DefaultNotificationSuppressed(snapshot.WorkItems, entry)) return false;
        var draft = snapshot.NotificationDrafts.SingleOrDefault(x => x.EventId == entry.Id);
        if (draft == null) return true;
        if (draft.Cancelled) return false;
        if (draft.WorkRunId is not { } id) return true;
        var run = snapshot.CommitmentRuns.Single(x => x.Id == id);
        var work = snapshot.WorkItems.Single(x => x.Id == run.WorkItemId);
        return WorkAllowsNotification(work, run, now) && (!checkQuiet || !InCommitmentQuietHours(work.Policy, now));
    }
    private bool RetireInvalidCatchUpPages(LedgerState state, CatchUpBatch batch)
    {
        var snapshot = SnapshotOf(state, batch.Identity);
        bool changed = false;
        foreach (var output in snapshot.Outbox.Where(x => x.NotificationEventId == batch.DeliveryEventId && x.Stage == OutboxStage.Pending))
        {
            var dependency = DependencyValidation(batch.Identity, ResolveOutputDependencies(snapshot, output), snapshot.OutputDependenciesRequired);
            if (dependency.Status == OutputDependencyStatus.Unavailable) continue;
            // Quiet and transport quotas only delay a page. Changed source facts invalidate its old manifest.
            if (CatchUpPageReady(snapshot, batch, output, _clock.UtcNow, enforceBudget: false, checkQuiet: false)) continue;
            Replace(state.Outbox, output, output with { Stage = OutboxStage.Superseded, UpdatedAt = _clock.UtcNow,
                DependencyInvalidationReason = dependency.Status == OutputDependencyStatus.Invalid ? DependencyReason(dependency.Reason) : null });
            changed = true;
        }
        return changed;
    }
    private bool CatchUpBasicReady(ConversationSnapshot snapshot, OutboxMessage output, DateTimeOffset now)
    {
        if (!OutputDependencyReady(snapshot, output)) return false;
        if (CatchUpBatchFor(snapshot, output) is { } batch) return CatchUpPageReady(snapshot, batch, output, now);
        if (CatchUpOriginalHeld(snapshot, output)) return false;
        if (output.Kind == OutboxKind.Notification && CurrentCatchUpWindow(snapshot) is { } window
            && snapshot.Events.Single(x => x.Id == output.NotificationEventId).RequiredDecision != null
            && (!window.VerifiedRequiredDecisionEventIds.Contains(output.NotificationEventId!.Value)
                || snapshot.Outbox.Any(x => x.NotificationEventId == output.NotificationEventId && x.Stage is OutboxStage.Unknown or OutboxStage.Sending))) return false;
        return true;
    }
    private bool CatchUpDeliveryReady(ConversationSnapshot snapshot, OutboxMessage output, DateTimeOffset now)
    {
        if (!CatchUpBasicReady(snapshot, output, now)) return false;
        int priority = OutboxPriority(snapshot, output);
        if (priority >= 2 && CurrentCatchUpWindow(snapshot) is { Stage: CatchUpWindowStage.Captured }) return false;
        return !snapshot.Outbox.Any(other => other.Id != output.Id && other.Stage == OutboxStage.Pending
            && OutboxPriority(snapshot, other) < priority && CatchUpBasicReady(snapshot, other, now)
            && MessagePolicyReady(snapshot.WorkItems, snapshot.CommitmentRuns, snapshot.NotificationDrafts, snapshot.Events, other, now)
            && snapshot.Outbox.Where(prior => SameOutboxGroup(prior, other) && prior.SegmentIndex < other.SegmentIndex).All(prior => prior.Stage == OutboxStage.Accepted));
    }
    private void RequireCatchUpDispatch(LedgerState state, OutboxMessage output)
    {
        // Version-one sessions retain their existing behavior until a real channel window opts in.
        if (!state.CatchUpWindows.Any(x => x.Identity == output.Identity)) return;
        if (!CatchUpDeliveryReady(SnapshotOf(state, output.Identity), output, _clock.UtcNow))
            throw Error(LedgerErrors.NotificationSuppressed, "This send is held by the current catch-up window, source policy or presentation order.");
    }
    private bool CatchUpRetryAllowed(LedgerState state, OutboxMessage output)
    {
        var snapshot = SnapshotOf(state, output.Identity);
        if (output.Kind == OutboxKind.Notification && CurrentCatchUpWindow(snapshot) is { } window
            && output.AttemptTrustedInputRevision == window.TrustedInputRevision
            && snapshot.Events.Single(x => x.Id == output.NotificationEventId).RequiredDecision != null) return false;
        return CatchUpBatchFor(snapshot, output) == null && !CatchUpOriginalHeld(snapshot, output)
            && CatchUpBasicReady(snapshot, output, _clock.UtcNow);
    }
    private void RequireCatchUpRetry(LedgerState state, OutboxMessage output)
    {
        if (!CatchUpRetryAllowed(state, output))
            throw Error(LedgerErrors.NotificationSuppressed, "This historical source needs a new catch-up window or has uncertain coverage.");
    }
    private bool ReserveCatchUpDelivery(LedgerState state, OutboxMessage output)
    {
        var batch = state.CatchUpBatches.SingleOrDefault(x => x.DeliveryEventId == output.NotificationEventId);
        if (batch == null) return false;
        var snapshot = SnapshotOf(state, output.Identity);
        if (!CatchUpPageReady(snapshot, batch, output, _clock.UtcNow))
            throw Error(LedgerErrors.NotificationSuppressed, "Catch-up sources or their delivery budget are no longer ready.");
        foreach (var id in batch.Members.Where(x => x.CatchUpOutboxId == output.Id && x.WorkRunId != null).Select(x => x.WorkRunId!.Value).Distinct())
        {
            if (CatchUpRunCharged(snapshot, batch, id)) continue;
            var run = RequireCommitmentRun(state, output.Identity, id);
            var work = RequireCommitment(state, output.Identity, run.WorkItemId);
            ReserveCommitmentBudget(state, work, run, CommitmentBudgetKind.Notification, output.Id, output.SendAttempts + 1, batch.Id);
            Replace(state.CommitmentRuns, run, run with { DeliveryAttempts = run.DeliveryAttempts.Add(_clock.UtcNow) });
        }
        return true;
    }
    private static void NoteCatchUpAcceptance(LedgerState state, OutboxMessage output)
    {
        if (output.Stage != OutboxStage.Accepted) return;
        var batch = state.CatchUpBatches.SingleOrDefault(x => x.DeliveryEventId == output.NotificationEventId);
        if (batch == null) return;
        foreach (var member in batch.Members.Where(x => x.CatchUpOutboxId == output.Id && x.WorkRunId != null))
        {
            var run = RequireCommitmentRun(state, output.Identity, member.WorkRunId!.Value);
            if (run.Stage is CommitmentRunStage.Cancelled or CommitmentRunStage.Skipped or CommitmentRunStage.Completed
                || run.SharedDecisionEventId != null || !EventCoverageComplete(state.Outbox, state.CatchUpBatches, member.SourceEventId)) continue;
            Replace(state.CommitmentRuns, run, run with { Stage = CommitmentRunStage.Completed, CatchUpDeliveryOutboxId = output.Id });
            var work = RequireCommitment(state, output.Identity, run.WorkItemId);
            if (work.Stage == TrackedWorkStage.Triggered)
                Replace(state.WorkItems, work, work with { Stage = TrackedWorkStage.Completed, UpdatedAt = output.UpdatedAt });
        }
    }
    private static void ValidateCatchUpOptions(CatchUpOptions options)
    {
        if (options == null || options.MaximumEvents is < 1 or > 100 || options.MaximumPages is < 1 or > 20
            || options.MaximumPageCharacters is < 100 or > 8000 || options.MaximumWindows is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    private static void ValidateCatchUpInput(CatchUpInputOptions options)
    {
        if (!Enum.IsDefined(options.Disposition)) throw new ArgumentException("Unknown catch-up disposition.");
        ValidateToken(options.DisplayTimeZoneId); _ = TimeZoneInfo.FindSystemTimeZoneById(options.DisplayTimeZoneId);
    }
}
