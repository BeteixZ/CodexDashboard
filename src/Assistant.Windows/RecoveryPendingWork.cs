using System;
using System.Collections.Generic;
using System.Linq;
using Assistant.Core;

namespace CodexDashboard;

// Only internal identifiers and scheduling facts leave the encrypted ledger.
internal sealed record RecoveryPendingItem(string Kind, Guid Id, string Stage,
    DateTimeOffset? DueAtUtc = null, string? MissedPolicy = null);

internal static class RecoveryPendingWork
{
    internal static RecoveryPendingItem[] Read(ConversationSnapshot state)
    {
        var items = new List<RecoveryPendingItem>();
        foreach (var operation in state.Operations)
            if (operation.Stage == OperationStage.Queued)
                items.Add(new("Operation", operation.Id, operation.Stage.ToString()));
        foreach (var message in state.Outbox)
            if (message.Stage == OutboxStage.Pending)
                items.Add(new("Outbox", message.Id, message.Stage.ToString()));
        foreach (var generation in state.Generations)
            if (generation.Stage is GenerationStage.Processing or GenerationStage.Prepared)
                items.Add(new("Generation", generation.Id, generation.Stage.ToString()));
        foreach (var draft in state.NotificationDrafts)
            if (draft.Stage is NotificationDraftStage.Captured or NotificationDraftStage.Preparing)
                items.Add(new("NotificationDraft", draft.Id, draft.Stage.ToString()));
        foreach (var work in state.WorkItems)
        {
            if (work.Stage is TrackedWorkStage.Completed or TrackedWorkStage.Cancelled or TrackedWorkStage.Expired) continue;
            var schedule = state.Schedules.FirstOrDefault(x => x.WorkItemId == work.Id);
            items.Add(new(work.Kind.ToString(), work.Id, work.Stage.ToString(), schedule?.DueAtUtc, schedule?.MissedPolicy.ToString()));
        }
        foreach (var run in state.CommitmentRuns)
            if (run.Stage is not (CommitmentRunStage.Completed or CommitmentRunStage.Skipped or CommitmentRunStage.Cancelled))
                items.Add(new("CommitmentRun", run.Id, run.Stage.ToString(), run.NotBefore));
        if (state.DependentWork is { } dependent)
        {
            foreach (var work in dependent.WorkItems)
                if (work.Stage is not (DependentWorkStage.Completed or DependentWorkStage.Failed or DependentWorkStage.Cancelled or DependentWorkStage.Expired))
                    items.Add(new("DependentWork", work.Id, work.Stage.ToString(), work.DeadlineUtc));
            foreach (var attempt in dependent.Attempts)
                if (attempt.Stage is DependentDispatchStage.Dispatching or DependentDispatchStage.Accepted or DependentDispatchStage.Unknown)
                    items.Add(new("DependentDispatch", attempt.Id, attempt.Stage.ToString()));
        }
        foreach (var wake in state.ChannelWakes)
            if (wake.Stage is ChannelWakeStage.Pending or ChannelWakeStage.Sending or ChannelWakeStage.Unknown)
                items.Add(new("ChannelWake", wake.Id, wake.Stage.ToString(), wake.NotBefore));
        foreach (var work in state.InputWork)
            if (work.Stage is InputWorkStage.Queued or InputWorkStage.Reading or InputWorkStage.Prepared or InputWorkStage.ReplyPending
                or InputWorkStage.NeedsConfirmation or InputWorkStage.Unknown)
                items.Add(new("InputWork", work.Id, work.Stage.ToString()));
        foreach (var invocation in state.ToolInvocations)
            if (invocation.Stage is ToolInvocationStage.Prepared or ToolInvocationStage.Executing or ToolInvocationStage.Unknown
                or ToolInvocationStage.NeedsConfirmation or ToolInvocationStage.Interrupted)
                items.Add(new("ToolInvocation", invocation.Id, invocation.Stage.ToString()));
        return items.ToArray();
    }
}
