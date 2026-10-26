using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private bool RecoverCommitments(LedgerState state)
    {
        bool changed = RecoverCommitmentBudgetOccupancy(state);
        changed |= RecoverRequiredDecisionLinks(state);
        foreach (var run in state.CommitmentRuns.Where(x => x.Stage == CommitmentRunStage.Checking).ToArray())
        {
            var work = RequireCommitment(state, run.Identity, run.WorkItemId);
            if (work.Stage == TrackedWorkStage.Active)
                RetryOrFailCommitmentCheck(state, work, run);
            else Replace(state.CommitmentRuns, run, run with
            { Stage = work.Stage is TrackedWorkStage.Completed or TrackedWorkStage.Cancelled or TrackedWorkStage.Expired
                ? CommitmentRunStage.Skipped : CommitmentRunStage.PendingCheck,
                CheckClaimId = null, NotBefore = _clock.UtcNow.AddSeconds(30) });
            changed = true;
        }
        foreach (var receipt in state.CommitmentMutations)
        {
            var invocation = RequireInvocation(state, receipt.Identity, receipt.InvocationId);
            if (invocation.Stage != ToolInvocationStage.Executing) continue;
            // Local mutation and its completed receipt are the same durable transaction.
            // External dispatches have no such proof and continue to recover Unknown.
            Replace(state.ToolInvocations, invocation, invocation with
            { Stage = ToolInvocationStage.Completed, ResultJson = receipt.ReceiptJson, UpdatedAt = _clock.UtcNow });
            state.Events.Add(new(Guid.NewGuid(), receipt.Identity, ConversationSource.ToolResult, receipt.ReceiptJson,
                _clock.UtcNow, _clock.UtcNow, invocation.Revision, invocation.SourceMessageId, RelatedOperationId: receipt.OperationId));
            changed = true;
        }
        return changed;
    }

    private static void ValidateCommitments(LedgerState state)
    {
        static void Require(bool valid, string reason)
        {
            if (!valid) throw Error(LedgerErrors.CorruptStorage, "Invalid commitment: " + reason);
        }
        Require(state.WorkItems.Select(x => x.Id).Distinct().Count() == state.WorkItems.Count
            && state.Schedules.Select(x => x.WorkItemId).Distinct().Count() == state.Schedules.Count
            && state.Subscriptions.Select(x => x.WorkItemId).Distinct().Count() == state.Subscriptions.Count
            && state.CommitmentRuns.Select(x => x.Id).Distinct().Count() == state.CommitmentRuns.Count
            && state.CommitmentRuns.Select(x => (x.WorkItemId, x.TriggerKey)).Distinct().Count() == state.CommitmentRuns.Count
            && state.CommitmentMutations.Select(x => x.OperationId).Distinct().Count() == state.CommitmentMutations.Count,
            "duplicate IDs or trigger keys.");
        foreach (var work in state.WorkItems)
        {
            try { ValidateIdentity(work.Identity); ValidateTask(work.Task); ValidateCommitmentPolicy(work.Policy); }
            catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or NullReferenceException)
            { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid commitment identity or policy.", e); }
            var source = state.Inbox.SingleOrDefault(x => x.Identity == work.Identity && x.SourceMessageId == work.SourceMessageId);
            var operation = state.Operations.SingleOrDefault(x => x.Identity == work.Identity && x.Id == work.CreationOperationId);
            Require(work.Id != Guid.Empty && work.Version > 0 && Enum.IsDefined(work.Kind) && Enum.IsDefined(work.Stage)
                && !string.IsNullOrWhiteSpace(work.Goal) && work.Goal.Length <= 1024 * 1024
                && source?.Trust == MessageTrust.AuthenticatedUser && operation != null
                && operation.SourceMessageId == work.SourceMessageId && operation.Task == work.Task
                && operation.Stage == OperationStage.Completed
                && operation.Action == (work.Kind == TrackedWorkKind.Reminder ? "schedule" : "subscribe")
                && state.CommitmentMutations.Any(x => x.OperationId == operation.Id && x.WorkItemId == work.Id),
                "work provenance.");
            Require(work.Kind == TrackedWorkKind.Reminder
                ? work.Task == null && state.Schedules.Count(x => x.WorkItemId == work.Id) == 1 && !state.Subscriptions.Any(x => x.WorkItemId == work.Id)
                : work.Task?.TurnId != null && state.Subscriptions.Count(x => x.WorkItemId == work.Id) == 1 && !state.Schedules.Any(x => x.WorkItemId == work.Id),
                "work kind associations.");
        }
        foreach (var schedule in state.Schedules)
        {
            var work = state.WorkItems.SingleOrDefault(x => x.Id == schedule.WorkItemId && x.Kind == TrackedWorkKind.Reminder);
            Require(work != null && Enum.IsDefined(schedule.MissedPolicy) && schedule.DueAtUtc > schedule.ReferenceTimeUtc
                && (work.DeadlineUtc == null || schedule.DueAtUtc <= work.DeadlineUtc), "schedule interval.");
            try { Require(CommitmentTools.ResolveLocalTime(schedule.LocalTime, schedule.TimeZoneId) == schedule.DueAtUtc, "schedule timezone."); }
            catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid reminder time.", e); }
            Require(schedule.FiredRunId == null || state.CommitmentRuns.Any(x => x.Id == schedule.FiredRunId
                && x.WorkItemId == schedule.WorkItemId && x.TriggerKey == "reminder"), "schedule trigger.");
        }
        foreach (var subscription in state.Subscriptions)
        {
            Require(state.WorkItems.Any(x => x.Id == subscription.WorkItemId && x.Kind == TrackedWorkKind.Subscription)
                && Enum.IsDefined(subscription.Condition) && Enum.IsDefined(subscription.LastState)
                && (subscription.Condition == SubscriptionCondition.NoActivity
                    ? subscription.InactivitySeconds is >= 1 and <= 604800 : subscription.InactivitySeconds == null)
                && subscription.LastDecisionId?.Length is not > 512, "subscription association.");
        }
        foreach (var run in state.CommitmentRuns)
        {
            var work = state.WorkItems.SingleOrDefault(x => x.Id == run.WorkItemId && x.Identity == run.Identity);
            Require(run.Id != Guid.Empty && work != null && run.WorkVersion > 0 && run.WorkVersion <= work.Version
                && Enum.IsDefined(run.Stage) && !string.IsNullOrWhiteSpace(run.TriggerKey) && run.TriggerKey.Length <= 1024
                && run.CheckAttempts >= 0 && run.CheckAttempts <= 8 && run.CheckClaimId != Guid.Empty
                && (run.Stage == CommitmentRunStage.Checking) == (run.CheckClaimId != null)
                && (run.Stage != CommitmentRunStage.Checking || run.CheckAttempts > 0 && work.Version == run.WorkVersion)
                && !run.DeliveryAttempts.IsDefault && run.DeliveryAttempts.Length <= 8,
                "run identity, claim or budget.");
            Require((run.NotificationDraftId == null) == (run.NotificationEventId == null)
                && (run.Stage is not (CommitmentRunStage.NotificationPending or CommitmentRunStage.Completed)
                    || run.NotificationDraftId != null || run.SharedDecisionEventId != null),
                "run notification reference.");
            if (run.NotificationDraftId is { } id)
            {
                var draft = state.NotificationDrafts.SingleOrDefault(x => x.Id == id);
                Require(draft != null && draft.WorkRunId == run.Id && draft.Identity == run.Identity
                    && draft.Task == work!.Task && draft.EventId == run.NotificationEventId && draft.ContextBody == run.Body
                    && draft.EventKey == "commitment/" + run.Id.ToString("N")
                    && draft.Source == ConversationSource.Notification, "run/draft association.");
            }
        }
        foreach (var draft in state.NotificationDrafts)
        {
            Require(draft.WorkRunId == null || state.CommitmentRuns.Any(x => x.Id == draft.WorkRunId
                && x.Identity == draft.Identity && x.NotificationDraftId == draft.Id && x.NotificationEventId == draft.EventId),
                "orphaned draft.");
            Require(!draft.Cancelled || draft.WorkRunId != null && draft.Stage != NotificationDraftStage.Preparing,
                "cancelled draft scope.");
        }
        foreach (var receipt in state.CommitmentMutations)
        {
            var operation = state.Operations.SingleOrDefault(x => x.Id == receipt.OperationId && x.Identity == receipt.Identity);
            var invocation = state.ToolInvocations.SingleOrDefault(x => x.Id == receipt.InvocationId && x.Identity == receipt.Identity);
            var work = state.WorkItems.SingleOrDefault(x => x.Id == receipt.WorkItemId && x.Identity == receipt.Identity);
            Require(operation != null && invocation != null && work != null && receipt.WorkVersion > 0
                && receipt.WorkVersion <= work.Version && operation.Stage == OperationStage.Completed
                && operation.Receipt == receipt.ReceiptJson && operation.Task == work.Task
                && invocation.OperationId == operation.Id && invocation.ToolName == operation.Action
                && CommitmentTools.Handles(operation.Action) && invocation.HasSideEffects
                && invocation.Stage is ToolInvocationStage.Executing or ToolInvocationStage.Completed,
                "local mutation proof.");
            try
            {
                Require(operation!.TargetKey == CommitmentTools.TargetKey(operation.Action, operation.ArgumentsJson), "mutation target.");
                using var parsed = JsonDocument.Parse(receipt.ReceiptJson);
                var facts = parsed.RootElement.GetProperty("facts");
                Require(parsed.RootElement.GetProperty("status").GetString() == "completed"
                    && facts.GetProperty("workItemId").GetGuid() == work!.Id
                    && facts.GetProperty("version").GetInt64() == receipt.WorkVersion, "mutation receipt.");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid commitment mutation proof.", e); }
        }
        foreach (var entry in state.Events.Where(x => x.IsDefaultTaskNotification))
            Require(entry.Task != null && entry.ExternalEventKey != null
                && entry.Source is ConversationSource.Notification or ConversationSource.ToolResult, "default notification marker.");
        ValidateRequiredDecisionNotifications(state);
    }
}
