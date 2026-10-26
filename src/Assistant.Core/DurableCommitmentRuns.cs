using System.Collections.Immutable;
using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public Task<ImmutableArray<CommitmentRun>> AdvanceCommitmentsAsync(ConversationIdentity identity,
        int maximum = 32, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateIdentity(identity);
        if (maximum is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximum));
        var results = ImmutableArray.CreateBuilder<CommitmentRun>();
        bool changed = RecoverRequiredDecisionLinks(state, identity);
        foreach (var exhausted in state.CommitmentRuns.Where(x => x.Identity == identity && x.Stage == CommitmentRunStage.PendingCheck).ToArray())
        {
            var owner = RequireCommitment(state, identity, exhausted.WorkItemId);
            if (owner.DeadlineUtc <= _clock.UtcNow && owner.Stage is TrackedWorkStage.Active or TrackedWorkStage.Paused)
            { ExpireCommitment(state, owner); changed = true; continue; }
            if (owner.Stage == TrackedWorkStage.Active && exhausted.CheckAttempts >= owner.Policy.MaximumCheckAttempts)
            { results.Add(RetryOrFailCommitmentCheck(state, owner, exhausted)); changed = true; }
            if (results.Count >= maximum) return Changed(results.ToImmutable());
        }
        foreach (var work in state.WorkItems.Where(x => x.Identity == identity
            && x.Stage is TrackedWorkStage.Active or TrackedWorkStage.Triggered or TrackedWorkStage.Paused or TrackedWorkStage.NeedsDecision).ToArray())
        {
            if (results.Count >= maximum) break;
            if (work.DeadlineUtc <= _clock.UtcNow)
            {
                ExpireCommitment(state, work); changed = true; continue;
            }
            if (work.Stage != TrackedWorkStage.Active) continue;
            var schedule = state.Schedules.SingleOrDefault(x => x.WorkItemId == work.Id);
            if (schedule != null)
            {
                if (schedule.FiredRunId != null || schedule.DueAtUtc > _clock.UtcNow) continue;
                // A bounded grace absorbs normal polling latency. Later arrivals use the persisted missed policy.
                bool missed = _clock.UtcNow - schedule.DueAtUtc > TimeSpan.FromMinutes(1);
                var stage = missed && schedule.MissedPolicy == MissedTriggerPolicy.Skip ? CommitmentRunStage.Skipped
                    : missed && schedule.MissedPolicy == MissedTriggerPolicy.NeedsDecision ? CommitmentRunStage.NeedsDecision
                    : CommitmentRunStage.ReadyToNotify;
                var run = AddCommitmentRun(state, work, "reminder", stage);
                Replace(state.Schedules, schedule, schedule with { FiredRunId = run.Id });
                Replace(state.WorkItems, work, work with { Stage = stage == CommitmentRunStage.Skipped ? TrackedWorkStage.Expired
                    : stage == CommitmentRunStage.NeedsDecision ? TrackedWorkStage.NeedsDecision : TrackedWorkStage.Triggered,
                    UpdatedAt = _clock.UtcNow });
                if (stage == CommitmentRunStage.ReadyToNotify)
                    run = CaptureCommitmentNotification(state, work, run, work.Goal);
                else if (stage == CommitmentRunStage.NeedsDecision)
                    run = CaptureCommitmentNotification(state, work, run, "这条提醒已错过原定时间：" + work.Goal
                        + "。如需改期，请先回复“取消事项 " + work.Id.ToString("N") + "”，再明确新时间建立提醒；已触发的原事项不能直接改期。");
                results.Add(run); changed = true;
                continue;
            }
            var subscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
            if (subscription.Condition != SubscriptionCondition.NoActivity || subscription.NextCheckAt > _clock.UtcNow
                || state.CommitmentRuns.Any(x => x.WorkItemId == work.Id && x.Stage is CommitmentRunStage.PendingCheck or CommitmentRunStage.Checking))
                continue;
            string key = "inactivity/" + subscription.LastActivityAt.UtcTicks;
            if (state.CommitmentRuns.Any(x => x.WorkItemId == work.Id && x.TriggerKey == key)) continue;
            results.Add(AddCommitmentRun(state, work, key, CommitmentRunStage.PendingCheck)); changed = true;
        }
        return new Mutation<ImmutableArray<CommitmentRun>>(results.ToImmutable(), changed);
    }, cancellationToken);

    public Task<ImmutableArray<CommitmentRun>> ObserveCommitmentTaskAsync(ConversationIdentity identity,
        CommitmentTaskObservation observation, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateIdentity(identity); ValidateTask(observation.Task);
        if (!UsableCommitmentObservation(observation) || !IsCurrentObservationOrder(state, identity, observation)) return Unchanged(ImmutableArray<CommitmentRun>.Empty);
        var results = ImmutableArray.CreateBuilder<CommitmentRun>(); bool changed = false;
        foreach (var work in state.WorkItems.Where(x => x.Identity == identity && x.Task == observation.Task
            && x.Kind == TrackedWorkKind.Subscription && x.Stage == TrackedWorkStage.Active).ToArray())
        {
            if (work.DeadlineUtc <= _clock.UtcNow) { ExpireCommitment(state, work); changed = true; continue; }
            changed |= ApplyCommitmentObservation(state, work, observation, results);
        }
        return new Mutation<ImmutableArray<CommitmentRun>>(results.ToImmutable(), changed);
    }, cancellationToken);

    public Task<CommitmentCheckLease> BeginCommitmentCheckAsync(ConversationIdentity identity, Guid runId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var run = RequireCommitmentRun(state, identity, runId);
        var work = RequireCommitment(state, identity, run.WorkItemId);
        if (run.Stage != CommitmentRunStage.PendingCheck || run.NotBefore > _clock.UtcNow
            || work.Stage != TrackedWorkStage.Active || work.Version != run.WorkVersion || work.DeadlineUtc <= _clock.UtcNow
            || run.CheckAttempts >= work.Policy.MaximumCheckAttempts || work.Task == null)
            throw Error(LedgerErrors.InvalidTransition, "The read-only check is not ready or its budget is exhausted.");
        var claim = Guid.NewGuid();
        ReserveCommitmentBudget(state, work, run, CommitmentBudgetKind.ReadCheck, claim, run.CheckAttempts + 1);
        Replace(state.CommitmentRuns, run, run with { Stage = CommitmentRunStage.Checking, CheckClaimId = claim,
            CheckAttempts = run.CheckAttempts + 1 });
        return Changed(new CommitmentCheckLease(run.Id, work.Id, identity, work.Version, claim, run.CheckAttempts + 1, work.Task));
    }, cancellationToken);

    public Task<CommitmentRun> CompleteCommitmentCheckAsync(CommitmentCheckLease lease,
        CommitmentTaskObservation? observation, string? failureReason = null,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var run = RequireCommitmentRun(state, lease.Identity, lease.RunId);
        var work = RequireCommitment(state, lease.Identity, lease.WorkItemId);
        var settledSubscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
        bool exactSettledTerminal = observation is { Order: not null, IsCurrentSourceEvidence: true }
            && observation.Task == lease.Task
            && observation.State is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted
            && settledSubscription.LastObservationOrder == observation.Order
            && settledSubscription.LastState == observation.State && settledSubscription.LastObservedAt == observation.ObservedAt;
        if (exactSettledTerminal && run.Stage == CommitmentRunStage.Skipped && work.Stage == TrackedWorkStage.Completed
            && run.WorkItemId == work.Id && work.Task == lease.Task && work.Version == lease.WorkVersion
            && run.WorkVersion == lease.WorkVersion && run.CheckAttempts == lease.Attempt && run.CheckClaimId == null
            && lease.ClaimId != Guid.Empty && state.CommitmentBudgetReservations.Any(x => x.Identity == lease.Identity
                && x.Kind == CommitmentBudgetKind.ReadCheck && x.RunId == run.Id && x.WorkItemId == work.Id
                && x.ResourceId == lease.ClaimId && x.Attempt == lease.Attempt && x.WorkVersion == lease.WorkVersion
                && x.ReleasedAt != null)) return Unchanged(run);
        if (run.WorkItemId != work.Id || work.Version != lease.WorkVersion || run.WorkVersion != lease.WorkVersion
            || work.Task != lease.Task || run.Stage != CommitmentRunStage.Checking
            || run.CheckClaimId != lease.ClaimId || lease.ClaimId == Guid.Empty || run.CheckAttempts != lease.Attempt)
            throw Error(LedgerErrors.InvalidTransition, "The read-only check claim was cancelled, superseded or recovered.");
        if (work.Stage == TrackedWorkStage.Completed && exactSettledTerminal
            && UsableCommitmentObservation(observation!) && IsCurrentObservationOrder(state, lease.Identity, observation!))
        {
            // The exact read's event can reach the serialized bridge before its
            // caller resumes. The actual read has ended; keep the terminal fact
            // and finish only this still-matching check, without a retry/notice.
            CloseReadReservation(state, lease);
            var skipped = run with { Stage = CommitmentRunStage.Skipped, CheckClaimId = null,
                Reason = "The exact task turn reached a terminal state before this check completed." };
            Replace(state.CommitmentRuns, run, skipped);
            return Changed(skipped);
        }
        if (work.Stage != TrackedWorkStage.Active)
            throw Error(LedgerErrors.InvalidTransition, "The read-only check owner is no longer active.");
        CloseReadReservation(state, lease);
        if (work.DeadlineUtc <= _clock.UtcNow)
        {
            ExpireCommitment(state, work);
            return Changed(RequireCommitmentRun(state, lease.Identity, run.Id));
        }
        if (observation?.Task != work.Task || observation == null || !UsableCommitmentObservation(observation))
            return Changed(RetryOrFailCommitmentCheck(state, work, run));
        var subscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
        if (!IsCurrentObservationOrder(state, lease.Identity, observation)
            || !ObservationFollows(subscription, observation) && !(observation.Order != null
                && subscription.LastObservationOrder == observation.Order && subscription.LastState == observation.State))
            return Changed(RetryOrFailCommitmentCheck(state, work, run));
        ApplyCommitmentObservation(state, work, observation, ImmutableArray.CreateBuilder<CommitmentRun>());
        subscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
        var updatedWork = RequireCommitment(state, lease.Identity, work.Id);
        if (updatedWork.Stage != TrackedWorkStage.Active || observation.State is not CommitmentTaskState.Running
            || run.TriggerKey != "inactivity/" + subscription.LastActivityAt.UtcTicks
            || subscription.LastActivityAt.AddSeconds(subscription.InactivitySeconds!.Value) > _clock.UtcNow)
        {
            var skipped = run with { Stage = CommitmentRunStage.Skipped, CheckClaimId = null, Reason = "Fresh observation does not establish inactivity." };
            Replace(state.CommitmentRuns, run, skipped);
            return Changed(skipped);
        }
        var ready = run with { CheckClaimId = null, Stage = CommitmentRunStage.ReadyToNotify };
        Replace(state.CommitmentRuns, run, ready);
        return Changed(CaptureCommitmentNotification(state, work, ready,
            "这一轮仍显示运行，暂未读到新的活动证据。仅凭这些信息不能判断它卡住了。"));
    }, cancellationToken);

    private bool ApplyCommitmentObservation(LedgerState state, TrackedWorkItem work, CommitmentTaskObservation observation,
        ImmutableArray<CommitmentRun>.Builder results)
    {
        var subscription = state.Subscriptions.Single(x => x.WorkItemId == work.Id);
        if (!ObservationFollows(subscription, observation)) return false;
        var activity = observation.LastActivityAt ?? subscription.LastActivityAt;
        // A coarse status read omits identity; absence does not revoke an exact live decision.
        string? decision = observation.State == CommitmentTaskState.NeedsDecision
            ? observation.DecisionId ?? subscription.LastDecisionId : null;
        if (subscription.LastObservedAt != null && activity < subscription.LastActivityAt) activity = subscription.LastActivityAt;
        bool semanticChange = subscription.LastState != observation.State || subscription.LastActivityAt != activity
            || subscription.LastDecisionId != decision || subscription.LastObservedAt == null;
        // A newer read can establish ordering without reporting new task activity.
        // Persist that watermark so a late older terminal/decision cannot win,
        // including after reopening the ledger. Exact repeats remain no-ops.
        bool observationAdvanced = observation.Order != null
            ? subscription.LastObservationOrder != observation.Order
            : subscription.LastObservedAt < observation.ObservedAt;
        if (semanticChange || observationAdvanced)
            Replace(state.Subscriptions, subscription, subscription with { LastState = observation.State,
                LastActivityAt = activity, LastDecisionId = decision, LastObservedAt = observation.ObservedAt,
                LastObservationOrder = observation.Order,
                NextCheckAt = activity.AddSeconds(subscription.InactivitySeconds ?? 0) });
        bool terminal = observation.State is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted;
        bool matches = subscription.Condition switch
        {
            SubscriptionCondition.Completed => observation.State == CommitmentTaskState.Completed,
            SubscriptionCondition.Failed => observation.State is CommitmentTaskState.Failed or CommitmentTaskState.Interrupted,
            SubscriptionCondition.NeedsDecision => observation.State == CommitmentTaskState.NeedsDecision,
            _ => false
        };
        if (matches)
        {
            string trigger = observation.State == CommitmentTaskState.NeedsDecision
                ? "decision/" + (decision ?? "waiting") : "terminal";
            if (!state.CommitmentRuns.Any(x => x.WorkItemId == work.Id && x.TriggerKey == trigger)
                && !(observation.State == CommitmentTaskState.NeedsDecision && state.Events.Any(x => x.Identity == work.Identity
                    && x.Task == work.Task && x.RequiredDecision?.DecisionToken == decision
                    && x.DecisionSubscribers is { } targets && targets.Any(t => t.WorkItemId == work.Id))))
            {
                var run = AddCommitmentRun(state, work, trigger, CommitmentRunStage.ReadyToNotify);
                results.Add(CaptureCommitmentNotification(state, work, run, observation.State switch
                {
                    CommitmentTaskState.Completed => "已观察到你订阅的这一轮结束。结果和测试还没有额外复核。",
                    CommitmentTaskState.NeedsDecision => "你订阅的这一轮需要你作决定。",
                    CommitmentTaskState.Interrupted => "你订阅的这一轮已中断。",
                    _ => "你订阅的这一轮失败了。"
                }));
                semanticChange = true;
            }
        }
        if (terminal)
        {
            Replace(state.WorkItems, work, work with { Stage = matches ? TrackedWorkStage.Triggered : TrackedWorkStage.Completed,
                UpdatedAt = _clock.UtcNow });
            foreach (var pending in state.CommitmentRuns.Where(x => x.WorkItemId == work.Id && x.Stage == CommitmentRunStage.PendingCheck).ToArray())
                Replace(state.CommitmentRuns, pending, pending with
                { Stage = CommitmentRunStage.Skipped, Reason = "The exact task turn reached a terminal state before this check started." });
            semanticChange = true;
        }
        return semanticChange || observationAdvanced;
    }
    private bool UsableCommitmentObservation(CommitmentTaskObservation observation) =>
        observation.Fresh && observation.Connected && Enum.IsDefined(observation.State)
        && observation.State != CommitmentTaskState.Unknown && !string.IsNullOrWhiteSpace(observation.Source)
        && observation.Source.Length <= 512 && observation.DecisionId?.Length is not > 512
        && observation.ObservedAt <= _clock.UtcNow.AddSeconds(30) && observation.ObservedAt >= _clock.UtcNow.AddMinutes(-5)
        && (observation.LastActivityAt == null || observation.LastActivityAt <= observation.ObservedAt.AddSeconds(30));

    private CommitmentRun AddCommitmentRun(LedgerState state, TrackedWorkItem work, string trigger, CommitmentRunStage stage)
    {
        if (state.CommitmentRuns.Count >= _options.MaximumCommitmentRuns)
            throw Error(LedgerErrors.CapacityExceeded, "The run capacity is full; existing records were retained.");
        var run = new CommitmentRun(Guid.NewGuid(), work.Id, work.Identity, work.Version, trigger,
            _clock.UtcNow, stage, _clock.UtcNow, DeliveryAttempts: []);
        state.CommitmentRuns.Add(run);
        return run;
    }
    private CommitmentRun CaptureCommitmentNotification(LedgerState state, TrackedWorkItem work, CommitmentRun run, string body)
    {
        if (run.NotificationDraftId != null) return run;
        if (state.NotificationDrafts.Count(x => x.Stage != NotificationDraftStage.Finalized && !x.Cancelled)
            >= _options.MaximumPendingNotificationDrafts)
            throw Error(LedgerErrors.CapacityExceeded, "The pending notification capacity is full; no trigger was consumed.");
        ValidateBody(body);
        string key = "commitment/" + run.Id.ToString("N");
        var entry = new ConversationEvent(Guid.NewGuid(), work.Identity, ConversationSource.Notification, body,
            _clock.UtcNow, _clock.UtcNow, Revision(state, work.Identity), Task: work.Task, ExternalEventKey: key);
        var draft = new DurableNotificationDraft(Guid.NewGuid(), work.Identity, entry.Id, key, body,
            JsonSerializer.Serialize(new { kind = "commitment", workItemId = work.Id, runId = run.Id, body }),
            work.Task, ConversationSource.Notification, NotificationDraftStage.Captured, 0,
            work.Policy.MaximumModelPreparations, _clock.UtcNow, _clock.UtcNow, WorkRunId: run.Id);
        state.Events.Add(entry); NoteNotificationContext(state, entry); state.NotificationDrafts.Add(draft);
        var next = run with { Stage = CommitmentRunStage.NotificationPending, CheckClaimId = null,
            NotificationDraftId = draft.Id, NotificationEventId = entry.Id, Body = body, NotBefore = _clock.UtcNow };
        Replace(state.CommitmentRuns, run, next);
        return next;
    }
    private CommitmentRun RetryOrFailCommitmentCheck(LedgerState state, TrackedWorkItem work, CommitmentRun run)
    {
        var next = run with { CheckClaimId = null, Stage = CommitmentRunStage.PendingCheck,
            NotBefore = _clock.UtcNow.AddSeconds(30), Reason = "No fresh connected task evidence was available." };
        Replace(state.CommitmentRuns, run, next);
        if (run.CheckAttempts < work.Policy.MaximumCheckAttempts) return next;
        Replace(state.WorkItems, work, work with { Stage = TrackedWorkStage.NeedsDecision, UpdatedAt = _clock.UtcNow });
        return CaptureCommitmentNotification(state, work, next,
            "这次没能取得这一轮的最新状态，检查次数已用完。需要你决定是否重新安排检查；目前不能判断它是否卡住。");
    }
    private void ExpireCommitment(LedgerState state, TrackedWorkItem work)
    {
        Replace(state.WorkItems, work, work with { Stage = TrackedWorkStage.Expired, UpdatedAt = _clock.UtcNow });
        foreach (var run in state.CommitmentRuns.Where(x => x.WorkItemId == work.Id
            && x.Stage is not (CommitmentRunStage.Completed or CommitmentRunStage.Skipped or CommitmentRunStage.Cancelled)).ToArray())
        {
            Replace(state.CommitmentRuns, run, run with { Stage = CommitmentRunStage.Cancelled, CheckClaimId = null, Reason = "Deadline expired." });
            if (run.NotificationDraftId is not { } id) continue;
            var draft = RequireNotificationDraft(state, work.Identity, id);
            Replace(state.NotificationDrafts, draft, draft with { Cancelled = true,
                Stage = draft.Stage == NotificationDraftStage.Preparing ? NotificationDraftStage.Captured : draft.Stage,
                PreparationId = draft.Stage == NotificationDraftStage.Preparing ? null : draft.PreparationId });
            foreach (var message in state.Outbox.Where(x => x.NotificationEventId == draft.EventId
                && x.Stage is OutboxStage.Pending or OutboxStage.Rejected).ToArray())
                Replace(state.Outbox, message, message with { Stage = OutboxStage.Superseded, UpdatedAt = _clock.UtcNow });
        }
    }
    private static CommitmentRun RequireCommitmentRun(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.CommitmentRuns.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "The commitment run does not belong to this conversation.");
}
