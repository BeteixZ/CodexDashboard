using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public async Task<ToolExecutionResult> ApplyCommitmentToolAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ChangeAsync(state =>
            {
                var invocation = RequireInvocation(state, context.Identity, context.InvocationId);
                if (context.OperationId is not { } operationId || context.InputWorkId != null || context.ReadAtRevision != null)
                    throw Error(LedgerErrors.AuthorizationRequired, "A current ordinary tool invocation is required.");
                var operation = RequireOperation(state, context.Identity, operationId);
                var prior = state.CommitmentMutations.SingleOrDefault(x => x.OperationId == operationId);
                if (prior != null)
                {
                    if (prior.Identity != context.Identity || prior.InvocationId != invocation.Id)
                        throw Error(LedgerErrors.IdentityMismatch, "Local mutation receipt does not match.");
                    if (context.SourceMessageId != operation.SourceMessageId || context.Revision != operation.Revision
                        || context.IdempotencyKey != operation.IdempotencyKey || invocation.OperationId != operation.Id)
                        throw Error(LedgerErrors.AuthorizationRequired, "The replay context is not the original local operation.");
                    return Unchanged(CommitmentExecution(prior.ReceiptJson, operation));
                }
                var source = RequireInbox(state, context.Identity, context.SourceMessageId);
                var grant = RequireAuthorization(state, context.Identity, operation.AuthorizationId);
                var generation = state.Generations.SingleOrDefault(x => x.Id == invocation.GenerationId);
                RequireCurrent(state, context.Identity, context.Revision);
                if (!CommitmentTools.Handles(invocation.ToolName) || source.Trust != MessageTrust.AuthenticatedUser
                    || source.Revision != context.Revision || operation.Stage != OperationStage.Dispatching
                    || invocation.Stage != ToolInvocationStage.Executing || !invocation.HasSideEffects
                    || invocation.OperationId != operation.Id || operation.Action != invocation.ToolName
                    || operation.ArgumentsJson != invocation.ArgumentsJson || operation.SourceMessageId != source.SourceMessageId
                    || operation.Revision != context.Revision || operation.IdempotencyKey != context.IdempotencyKey
                    || invocation.SourceMessageId != source.SourceMessageId || invocation.Revision != context.Revision
                    || grant.SourceMessageId != source.SourceMessageId || grant.Revision != context.Revision
                    || grant.SourceExcerpt != source.Body || grant.Action != operation.Action
                    || grant.Task != operation.Task
                    || grant.ArgumentsJson != operation.ArgumentsJson || grant.TargetKey != operation.TargetKey
                    || operation.TargetKey != CommitmentTools.TargetKey(operation.Action, operation.ArgumentsJson)
                    || generation == null || generation.InputWorkId != null || generation.Stage != GenerationStage.Processing
                    || generation.Identity != context.Identity
                    || generation.Revision != context.Revision || !generation.InboxIds.Contains(source.Id))
                    throw Error(LedgerErrors.AuthorizationRequired, "Commitment mutation is not bound to the current durable authorization.");
                TrackedWorkItem work = operation.Action switch
                {
                    "schedule" => CreateReminder(state, source, operation),
                    "subscribe" => CreateSubscription(state, source, operation),
                    "change_commitment" => ChangeCommitment(state, source, operation),
                    _ => throw new ArgumentException("Unsupported commitment action.")
                };
                var schedule = state.Schedules.SingleOrDefault(x => x.WorkItemId == work.Id);
                var facts = JsonSerializer.SerializeToElement(new
                {
                    workItemId = work.Id, version = work.Version, kind = work.Kind.ToString(),
                    stage = work.Stage.ToString(), goal = work.Goal, task = work.Task,
                    dueAtUtc = schedule?.DueAtUtc, localTime = schedule?.LocalTime, timeZoneId = schedule?.TimeZoneId,
                    policy = work.Policy, allowsTaskExecution = false, channelDeliveryGuaranteed = false
                });
                string receipt = JsonSerializer.Serialize(new { status = "completed", facts, reason = (string?)null, operationId });
                state.CommitmentMutations.Add(new(operationId, invocation.Id, work.Id, context.Identity, work.Version, receipt));
                Replace(state.Operations, operation, operation with
                { Stage = OperationStage.Completed, Receipt = receipt, UpdatedAt = _clock.UtcNow });
                return Changed(new ToolExecutionResult(ToolExecutionState.Completed, facts,
                    TargetKey: operation.TargetKey, Task: operation.Task));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or JsonException)
        {
            return new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new
            { code = "invalid_commitment", saved = false, reason = error.Message }));
        }
    }

    private TrackedWorkItem CreateReminder(LedgerState state, InboxMessage source, OperationRecord operation)
    {
        var spec = JsonSerializer.Deserialize<ReminderSpecification>(operation.ArgumentsJson, Json)!;
        ValidateBody(spec.Text);
        if (spec.Text.Length > 1000 || !source.Body.Contains(spec.Text, StringComparison.Ordinal) || operation.Task != null)
            throw new ArgumentException("Reminder text must be a literal part of the current request.");
        var due = CommitmentTools.ResolveLocalTime(spec.LocalTime, spec.TimeZoneId);
        if (due <= source.ReceivedAt) throw new ArgumentException("Reminder time must follow the original receipt time.");
        var deadline = Deadline(spec.DeadlineUtc);
        if (deadline < due) throw new ArgumentException("The deadline precedes the reminder.");
        var work = NewCommitment(state, source, operation, TrackedWorkKind.Reminder, spec.Text, null, deadline, spec.Policy);
        state.Schedules.Add(new(work.Id, spec.LocalTime, spec.TimeZoneId, source.ReceivedAt, due, spec.MissedPolicy));
        return work;
    }
    private TrackedWorkItem CreateSubscription(LedgerState state, InboxMessage source, OperationRecord operation)
    {
        var spec = JsonSerializer.Deserialize<SubscriptionSpecification>(operation.ArgumentsJson, Json)!;
        var task = new TaskReference(spec.HostId, spec.ThreadId, spec.TurnId, spec.ProfileId);
        ValidateTask(task);
        if (string.IsNullOrWhiteSpace(task.TurnId) || task != operation.Task)
            throw new ArgumentException("A subscription must bind the exact authorized task and turn.");
        if (spec.Condition == SubscriptionCondition.NoActivity
            ? spec.InactivitySeconds is not (>= 1 and <= 604800) : spec.InactivitySeconds != null)
            throw new ArgumentException("Only inactivity subscriptions have a bounded inactivity threshold.");
        var deadline = Deadline(spec.DeadlineUtc);
        if (deadline <= source.ReceivedAt) throw new ArgumentException("Subscription deadline has already passed.");
        var work = NewCommitment(state, source, operation, TrackedWorkKind.Subscription, source.Body, task, deadline, spec.Policy);
        state.Subscriptions.Add(new(work.Id, spec.Condition, spec.InactivitySeconds, _clock.UtcNow,
            CommitmentTaskState.Unknown, _clock.UtcNow.AddSeconds(spec.InactivitySeconds ?? 0)));
        return work;
    }
    private TrackedWorkItem NewCommitment(LedgerState state, InboxMessage source, OperationRecord operation,
        TrackedWorkKind kind, string goal, TaskReference? task, DateTimeOffset? deadline, CommitmentPolicy? policy)
    {
        if (state.WorkItems.Count(x => x.Stage is TrackedWorkStage.Active or TrackedWorkStage.Triggered
            or TrackedWorkStage.Paused or TrackedWorkStage.NeedsDecision) >= _options.MaximumActiveCommitments)
            throw Error(LedgerErrors.CapacityExceeded, "The active commitment capacity is full; existing records were retained.");
        policy ??= new();
        ValidateCommitmentPolicy(policy);
        var work = new TrackedWorkItem(Guid.NewGuid(), source.Identity, kind, source.SourceMessageId, operation.Id,
            1, TrackedWorkStage.Active, goal, task, _clock.UtcNow, _clock.UtcNow, deadline, policy);
        state.WorkItems.Add(work);
        return work;
    }
    private TrackedWorkItem ChangeCommitment(LedgerState state, InboxMessage source, OperationRecord operation)
    {
        var spec = JsonSerializer.Deserialize<CommitmentChangeSpecification>(operation.ArgumentsJson, Json)!;
        if (!Guid.TryParse(spec.WorkItemId, out var id)) throw new ArgumentException("A real commitment ID is required.");
        var old = RequireCommitment(state, source.Identity, id);
        if (old.Version != spec.ExpectedVersion || operation.Task != old.Task)
            throw Error(LedgerErrors.Conflict, "Commitment version or target changed.");
        if (spec.Action != "update" && (spec.Text != null || spec.LocalTime != null || spec.TimeZoneId != null
            || spec.MissedPolicy != null || spec.Policy != null)) throw new ArgumentException("This action cannot also edit fields.");
        var nextStage = spec.Action switch
        {
            "pause" when old.Stage is TrackedWorkStage.Active or TrackedWorkStage.Triggered => TrackedWorkStage.Paused,
            "resume" when old.Stage == TrackedWorkStage.Paused && old.Kind == TrackedWorkKind.Reminder
                && state.CommitmentRuns.Any(x => x.WorkItemId == id && x.Stage == CommitmentRunStage.Completed) => TrackedWorkStage.Completed,
            "resume" when old.Stage == TrackedWorkStage.Paused && old.Kind == TrackedWorkKind.Reminder =>
                state.Schedules.Any(x => x.WorkItemId == id && x.FiredRunId != null) ? TrackedWorkStage.Triggered : TrackedWorkStage.Active,
            "resume" when old.Stage == TrackedWorkStage.Paused && state.Subscriptions.Any(x => x.WorkItemId == id
                && x.LastState is CommitmentTaskState.Completed or CommitmentTaskState.Failed or CommitmentTaskState.Interrupted) =>
                state.CommitmentRuns.Any(x => x.WorkItemId == id && x.Stage == CommitmentRunStage.NotificationPending)
                    ? TrackedWorkStage.Triggered : TrackedWorkStage.Completed,
            "resume" when old.Stage == TrackedWorkStage.Paused => TrackedWorkStage.Active,
            "cancel" => TrackedWorkStage.Cancelled,
            "update" when old.Stage is TrackedWorkStage.Active or TrackedWorkStage.Paused or TrackedWorkStage.Triggered => old.Stage,
            _ => throw new ArgumentException("The commitment cannot make this transition.")
        };
        var policy = spec.Policy ?? old.Policy;
        ValidateCommitmentPolicy(policy);
        string goal = old.Goal;
        if (spec.Action == "update")
        {
            bool reminderEdit = spec.Text != null || spec.LocalTime != null || spec.TimeZoneId != null || spec.MissedPolicy != null;
            if (!reminderEdit && spec.Policy == null) throw new ArgumentException("No update was provided.");
            if (reminderEdit)
            {
                var schedule = state.Schedules.SingleOrDefault(x => x.WorkItemId == id)
                    ?? throw new ArgumentException("Subscription targets cannot be changed; create a new subscription.");
                if (schedule.FiredRunId != null) throw new ArgumentException("This reminder has triggered; cancel it and create another.");
                if (spec.Text != null)
                {
                    ValidateBody(spec.Text);
                    if (!source.Body.Contains(spec.Text, StringComparison.Ordinal)) throw new ArgumentException("Use the user's exact reminder text.");
                    goal = spec.Text;
                }
                string local = spec.LocalTime ?? schedule.LocalTime, zone = spec.TimeZoneId ?? schedule.TimeZoneId;
                var due = CommitmentTools.ResolveLocalTime(local, zone);
                if (due <= source.ReceivedAt || old.DeadlineUtc < due) throw new ArgumentException("The updated time is outside the permitted interval.");
                Replace(state.Schedules, schedule, schedule with { LocalTime = local, TimeZoneId = zone, DueAtUtc = due,
                    ReferenceTimeUtc = source.ReceivedAt, MissedPolicy = spec.MissedPolicy ?? schedule.MissedPolicy });
            }
        }
        var work = old with { Version = checked(old.Version + 1), Stage = nextStage, Goal = goal,
            Policy = policy, UpdatedAt = _clock.UtcNow };
        Replace(state.WorkItems, old, work);
        foreach (var run in state.CommitmentRuns.Where(x => x.WorkItemId == id
            && x.Stage is not (CommitmentRunStage.Completed or CommitmentRunStage.Skipped or CommitmentRunStage.Cancelled)).ToArray())
        {
            var next = run with { WorkVersion = work.Version, CheckClaimId = null,
                Stage = nextStage == TrackedWorkStage.Cancelled ? CommitmentRunStage.Cancelled
                    : run.Stage == CommitmentRunStage.Checking ? CommitmentRunStage.PendingCheck : run.Stage };
            Replace(state.CommitmentRuns, run, next);
            if (run.NotificationDraftId is not { } draftId) continue;
            var draft = RequireNotificationDraft(state, work.Identity, draftId);
            var updatedDraft = draft.Stage == NotificationDraftStage.Preparing
                ? draft with { Stage = NotificationDraftStage.Captured, PreparationId = null, UpdatedAt = _clock.UtcNow } : draft;
            updatedDraft = updatedDraft with
            { MaximumPreparationAttempts = Math.Max(updatedDraft.PreparationAttempts, policy.MaximumModelPreparations) };
            if (nextStage == TrackedWorkStage.Cancelled)
            {
                updatedDraft = updatedDraft with { Cancelled = true, UpdatedAt = _clock.UtcNow };
                foreach (var message in state.Outbox.Where(x => x.NotificationEventId == draft.EventId
                    && x.Stage is OutboxStage.Pending or OutboxStage.Rejected).ToArray())
                    Replace(state.Outbox, message, message with { Stage = OutboxStage.Superseded, UpdatedAt = _clock.UtcNow });
            }
            Replace(state.NotificationDrafts, draft, updatedDraft);
        }
        return work;
    }

    public async Task<CommitmentSnapshot> GetCommitmentsAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return new(snapshot.WorkItems, snapshot.Schedules, snapshot.Subscriptions, snapshot.CommitmentRuns);
    }
    public async Task<bool> ShouldNotifyDefaultTaskAsync(ConversationIdentity identity, TaskReference task,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetCommitmentsAsync(identity, cancellationToken).ConfigureAwait(false);
        return !snapshot.WorkItems.Any(x => x.Kind == TrackedWorkKind.Subscription && x.Task == task);
    }
    public async Task<ImmutableArray<DurableNotificationDraft>> GetPendingCommitmentNotificationsAsync(
        ConversationIdentity identity, CancellationToken cancellationToken = default) =>
        (await GetPendingNotificationDraftsAsync(identity, cancellationToken).ConfigureAwait(false))
            .Where(x => x.WorkRunId != null).ToImmutableArray();
    public async Task<ImmutableArray<CommitmentRun>> GetPendingCommitmentChecksAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return snapshot.IsReadOnly || !BudgetAvailable(identity, snapshot.CommitmentBudgetReservations,
            CommitmentBudgetKind.ReadCheck, _clock.UtcNow) ? [] : snapshot.CommitmentRuns.Where(x => x.Stage == CommitmentRunStage.PendingCheck
            && x.NotBefore <= _clock.UtcNow && snapshot.WorkItems.Any(w => w.Id == x.WorkItemId
                && w.Version == x.WorkVersion && w.Stage == TrackedWorkStage.Active)).ToImmutableArray();
    }
    private static TrackedWorkItem RequireCommitment(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.WorkItems.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "The commitment does not belong to this conversation.");
    private static DateTimeOffset? Deadline(string? value)
    {
        if (value == null) return null;
        if (!DateTimeOffset.TryParseExact(value, ["O", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed)) throw new ArgumentException("Deadline must be an explicit ISO timestamp.");
        return parsed.ToUniversalTime();
    }
    private static void ValidateCommitmentPolicy(CommitmentPolicy policy)
    {
        if (policy.MinimumIntervalSeconds is < 0 or > 604800 || policy.MaximumNotificationsPer24Hours is < 1 or > 100
            || policy.MaximumCheckAttempts is < 1 or > 8 || policy.MaximumModelPreparations is < 1 or > 3
            || policy.MaximumDeliveryAttempts is < 1 or > 8 || (policy.QuietStart == null) != (policy.QuietEnd == null))
            throw new ArgumentException("Invalid commitment policy bounds.");
        if (policy.QuietStart == null) return;
        if (policy.TimeZoneId == null || !TimeOnly.TryParseExact(policy.QuietStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(policy.QuietEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start == end)
            throw new ArgumentException("Quiet hours require a valid timezone and two different HH:mm times.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
    }
    private static ToolExecutionResult CommitmentExecution(string receipt, OperationRecord operation)
    {
        using var document = JsonDocument.Parse(receipt);
        return new(ToolExecutionState.Completed, document.RootElement.GetProperty("facts").Clone(),
            TargetKey: operation.TargetKey, Task: operation.Task);
    }
}
