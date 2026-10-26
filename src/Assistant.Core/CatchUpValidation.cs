using System.Text;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private static void ValidateCatchUp(LedgerState state)
    {
        static void Require(bool value, string detail)
        { if (!value) throw Error(LedgerErrors.CorruptStorage, "Invalid catch-up " + detail); }
        Require(!state.CatchUpWindows.Any(x => x == null) && !state.CatchUpBatches.Any(x => x == null), "null record.");
        Require(state.SchemaVersion >= 2 || state.CatchUpWindows.Count == 0 && state.CatchUpBatches.Count == 0
            && state.Events.All(x => x.RequiredDecisionSuppressedAt == null) && state.CommitmentRuns.All(x => x.CatchUpDeliveryOutboxId == null)
            && state.CommitmentBudgetReservations.All(x => x.CatchUpBatchId == null), "feature version gate.");
        Require(state.CatchUpWindows.Select(x => x.Id).Distinct().Count() == state.CatchUpWindows.Count
            && state.CatchUpWindows.Select(x => (x.Identity, x.SourceMessageId)).Distinct().Count() == state.CatchUpWindows.Count
            && state.CatchUpBatches.Select(x => x.Id).Distinct().Count() == state.CatchUpBatches.Count
            && state.CatchUpBatches.Select(x => x.WindowId).Distinct().Count() == state.CatchUpBatches.Count
            && state.CatchUpBatches.Select(x => x.DeliveryEventId).Distinct().Count() == state.CatchUpBatches.Count, "duplicate identity.");
        foreach (var group in state.CatchUpWindows.GroupBy(x => x.Identity))
            Require(group.Count(x => x.Stage != CatchUpWindowStage.Superseded) <= 1, "overlapping current windows.");
        foreach (var window in state.CatchUpWindows)
        {
            var input = state.Inbox.SingleOrDefault(x => x.Identity == window.Identity && x.SourceMessageId == window.SourceMessageId);
            Require(window.Id != Guid.Empty && input?.Trust == MessageTrust.AuthenticatedUser && input.Revision == window.TrustedInputRevision
                && window.DialogueEpoch >= 0 && window.DialogueEpoch <= ContextFor(state, window.Identity).Epoch
                && window.CapturedAt != default && Enum.IsDefined(window.Stage) && window.Input != null
                && !window.CandidateEventIds.IsDefault && !window.RequiredDecisionEventIds.IsDefault && !window.VerifiedRequiredDecisionEventIds.IsDefault
                && window.CandidateEventIds.Distinct().Count() == window.CandidateEventIds.Length
                && window.RequiredDecisionEventIds.Distinct().Count() == window.RequiredDecisionEventIds.Length
                && window.VerifiedRequiredDecisionEventIds.Distinct().Count() == window.VerifiedRequiredDecisionEventIds.Length
                && window.DeferredEventCount >= 0 && window.DeferredEventCount <= window.CandidateEventIds.Length, "window provenance.");
            try { ValidateCatchUpInput(window.Input!); }
            catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw Error(LedgerErrors.CorruptStorage, "Invalid catch-up input settings."); }
            foreach (var id in window.CandidateEventIds.Concat(window.RequiredDecisionEventIds))
            {
                var entry = state.Events.SingleOrDefault(x => x.Id == id && x.Identity == window.Identity);
                Require(entry?.ExternalEventKey != null && !state.CatchUpBatches.Any(x => x.DeliveryEventId == id)
                    && (entry.RequiredDecision == null) == window.CandidateEventIds.Contains(id), "historical event association.");
            }
            Require(window.VerifiedRequiredDecisionEventIds.All(window.RequiredDecisionEventIds.Contains), "verified decision association.");
            Require(window.Stage == CatchUpWindowStage.Superseded || window.TrustedInputRevision == TrustedInputRevision(state, window.Identity), "stale input window.");
            Require(window.Stage != CatchUpWindowStage.Prepared || state.CatchUpBatches.Any(x => x.WindowId == window.Id), "missing prepared batch.");
        }
        foreach (var batch in state.CatchUpBatches)
        {
            Require(batch.ManifestVersion is 1 or 2 && (batch.ManifestVersion == 1 || state.SchemaVersion >= 4), "span feature version gate.");
            var window = state.CatchUpWindows.SingleOrDefault(x => x.Id == batch.WindowId && x.Identity == batch.Identity);
            var entry = state.Events.SingleOrDefault(x => x.Id == batch.DeliveryEventId && x.Identity == batch.Identity);
            Require(batch.Id != Guid.Empty && window != null && entry != null && entry.RequiredDecision == null
                && entry.ExternalEventKey == "catch-up/" + batch.Id.ToString("N") && entry.Source == ConversationSource.Notification
                && entry.Task == null && !entry.IsDefaultTaskNotification && entry.NotificationSegments != null
                && !state.NotificationDrafts.Any(x => x.EventId == entry.Id) && batch.CreatedAt != default
                && !batch.Members.IsDefaultOrEmpty && !batch.Members.Any(x => x == null)
                && (batch.ManifestVersion == 1
                    ? batch.Members.Select(x => x.SourceEventId).Distinct().Count() == batch.Members.Length
                    : batch.Members.Select(x => (x.SourceEventId, x.CatchUpOutboxId)).Distinct().Count() == batch.Members.Length), "batch manifest.");
            foreach (var member in batch.Members)
            {
                var original = state.Events.SingleOrDefault(x => x.Id == member.SourceEventId && x.Identity == batch.Identity);
                var output = state.Outbox.SingleOrDefault(x => x.Id == member.CatchUpOutboxId && x.Identity == batch.Identity);
                var sources = state.Outbox.Where(x => x.Identity == batch.Identity && x.NotificationEventId == member.SourceEventId).OrderBy(x => x.SegmentIndex).ToArray();
                Require(original != null && original.RequiredDecision == null && original.DependentWorkNotice == null && (batch.ManifestVersion == 1 || !UnclassifiedRequiredDecision(original)) && window!.CandidateEventIds.Contains(original.Id)
                    && output != null && output.NotificationEventId == batch.DeliveryEventId && !member.Sources.IsDefaultOrEmpty
                    && !member.Sources.Any(x => x == null), "source manifest.");
                if (batch.ManifestVersion == 1)
                    Require(member.Sources.Length == sources.Length && member.Sources.Select(x => x.OutboxId).SequenceEqual(sources.Select(x => x.Id)), "complete legacy source manifest.");
                foreach (var span in member.Sources)
                {
                    var source = sources.SingleOrDefault(x => x.Id == span.OutboxId);
                    Require(source != null && span.SendAttempts == source.SendAttempts && span.SendAttempts >= 0
                        && span.BodyHash == CatchUpHash(source.Body), "immutable source content or attempt.");
                    Require(batch.ManifestVersion == 1 ? span.Start == 0 && span.Length == null
                        : span.Start >= 0 && span.Start < source!.Body.Length && span.Length is > 0
                            && span.Length <= source.Body.Length - span.Start && ScalarBoundary(source.Body, span.Start)
                            && ScalarBoundary(source.Body, span.Start + span.Length.Value), "source span bounds or scalar boundary.");
                }
                Require(member.Sources.SequenceEqual(member.Sources.OrderBy(x => sources.Single(s => s.Id == x.OutboxId).SegmentIndex).ThenBy(x => x.Start)), "source span order.");
                var draft = state.NotificationDrafts.SingleOrDefault(x => x.EventId == original!.Id);
                Require(member.WorkRunId == draft?.WorkRunId && (member.WorkRunId != null || member.WorkVersion == 0), "source run association.");
                CommitmentRun? run = null;
                if (member.WorkRunId is { } id)
                {
                    run = state.CommitmentRuns.SingleOrDefault(x => x.Id == id && x.Identity == batch.Identity);
                    Require(run != null && member.WorkVersion > 0 && member.WorkVersion <= run.WorkVersion, "source run version.");
                }
                // Group identity is stable task/kind data, not free-form model classification.
                var category = run == null ? original!.Task != null ? "任务记录" : "待补报记录"
                    : state.WorkItems.Single(x => x.Id == run.WorkItemId).Kind == TrackedWorkKind.Reminder ? "提醒" : "订阅记录";
                var task = original!.Task;
                string group = category + "|" + (task == null ? "" : string.Join("/", task.ProfileId, task.HostId, task.ThreadId, task.TurnId));
                Require(member.GroupKey == group, "group identity.");
            }
            var pages = state.Outbox.Where(x => x.NotificationEventId == batch.DeliveryEventId).OrderBy(x => x.SegmentIndex).ToArray();
            Require(pages.Length > 0 && batch.Members.Select(x => x.CatchUpOutboxId).Distinct().Count() == pages.Length, "page coverage.");
            foreach (var page in pages)
            {
                var members = batch.Members.Where(x => x.CatchUpOutboxId == page.Id).ToArray();
                Require(members.Length > 0 && members.Select(x => x.GroupKey).Distinct().Count() == 1, "page group.");
                var body = new StringBuilder(CatchUpHeading(members[0].GroupKey));
                foreach (var member in members)
                {
                    if (body.Length > CatchUpHeading(members[0].GroupKey).Length) body.Append("\n\n");
                    var original = state.Events.Single(x => x.Id == member.SourceEventId);
                    body.Append(batch.ManifestVersion == 1
                        ? RenderCatchUpSource(original, member.Sources.Select(s => state.Outbox.Single(o => o.Id == s.OutboxId)), window!.Input.DisplayTimeZoneId)
                        : RenderSpanMember(original, member, state.Outbox, window!.Input.DisplayTimeZoneId));
                }
                Require(page.Body == body.ToString() && page.Body.Length <= 8000, "lossless rendered body.");
            }
            Require(entry!.Body == string.Join("\n\n", pages.Select(x => x.Body)), "composite body.");
            if (batch.ManifestVersion >= 2)
                foreach (var members in batch.Members.GroupBy(x => x.SourceEventId))
                {
                    var ordered = members.OrderBy(x => pages.Single(p => p.Id == x.CatchUpOutboxId).SegmentIndex)
                        .SelectMany(x => x.Sources).Select(x => (Index: state.Outbox.Single(o => o.Id == x.OutboxId).SegmentIndex, x.Start)).ToArray();
                    Require(ordered.SequenceEqual(ordered.OrderBy(x => x.Index).ThenBy(x => x.Start)), "source continuation page order.");
                }
            foreach (var spans in batch.Members.SelectMany(x => x.Sources).GroupBy(x => x.OutboxId))
            {
                int previousEnd = 0;
                var original = state.Outbox.Single(x => x.Id == spans.Key);
                foreach (var span in spans.OrderBy(x => x.Start))
                { Require(span.Start >= previousEnd, "overlapping spans in one batch."); previousEnd = span.Start + (span.Length ?? original.Body.Length); }
            }
        }
        foreach (var group in state.CatchUpBatches.SelectMany(x => x.Members).Where(x => state.Outbox.Single(o => o.Id == x.CatchUpOutboxId).Stage is
            OutboxStage.Pending or OutboxStage.Sending or OutboxStage.Accepted or OutboxStage.Unknown).SelectMany(x => x.Sources).GroupBy(x => x.OutboxId))
        {
            var original = state.Outbox.Single(x => x.Id == group.Key);
            int previousEnd = original.Stage == OutboxStage.Accepted ? original.Body.Length : 0;
            foreach (var span in group.OrderBy(x => x.Start))
            { Require(span.Start >= previousEnd, "overlapping live or uncertain coverage."); previousEnd = span.Start + (span.Length ?? original.Body.Length); }
        }
        foreach (var run in state.CommitmentRuns.Where(x => x.CatchUpDeliveryOutboxId != null))
            Require(run.Stage == CommitmentRunStage.Completed && run.SharedDecisionEventId == null
                && state.Outbox.Any(x => x.Id == run.CatchUpDeliveryOutboxId && x.Identity == run.Identity && x.Stage == OutboxStage.Accepted)
                && state.CatchUpBatches.Any(x => x.Identity == run.Identity && x.Members.Any(m => m.WorkRunId == run.Id && m.CatchUpOutboxId == run.CatchUpDeliveryOutboxId
                    && EventCoverageComplete(state.Outbox, state.CatchUpBatches, m.SourceEventId))),
                "completed run acceptance proof.");
        foreach (var entry in state.Events.Where(x => x.RequiredDecisionSuppressedAt != null || x.RequiredDecisionSuppressionReason != null))
            Require(entry.RequiredDecision != null && entry.RequiredDecisionSuppressedAt is { } at && at != default
                && !string.IsNullOrWhiteSpace(entry.RequiredDecisionSuppressionReason), "required decision suppression.");
    }
}
