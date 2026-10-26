using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public async Task<ImmutableArray<CatchUpEventProgress>> GetCatchUpProgressAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        return GetCatchUpProgress(snapshot);
    }
    public static ImmutableArray<CatchUpEventProgress> GetCatchUpProgress(ConversationSnapshot snapshot)
    {
        return snapshot.CatchUpWindows.SelectMany(x => x.CandidateEventIds).Distinct()
            .Select(id => snapshot.Events.Single(x => x.Id == id))
            .Where(x => x.RequiredDecision == null && !UnclassifiedRequiredDecision(x))
            .OrderBy(x => x.RecordedAt).ThenBy(x => x.Id).Select(entry =>
            {
                var originals = snapshot.Outbox.Where(x => x.NotificationEventId == entry.Id).ToArray();
                var sources = originals.OrderBy(x => x.SegmentIndex).Select(original =>
                {
                    var ids = snapshot.CatchUpBatches.SelectMany(x => x.Members).Where(x => x.Sources.Any(s => s.OutboxId == original.Id))
                        .Select(x => x.CatchUpOutboxId).ToHashSet();
                    var deliveries = snapshot.Outbox.Where(x => x.Id == original.Id || ids.Contains(x.Id)).ToArray();
                    return new CatchUpSourceProgress(original.Id, original.Body.Length,
                        AcceptedSourceCharacters(snapshot.Outbox, snapshot.CatchUpBatches, original),
                        MergeRanges(CoveredRanges(snapshot.Outbox, snapshot.CatchUpBatches, original, o => o.Stage == OutboxStage.Pending)).Sum(r => r.Length),
                        deliveries.Any(x => x.Stage == OutboxStage.Sending), deliveries.Any(x => x.Stage == OutboxStage.Unknown));
                }).ToImmutableArray();
                int total = sources.Sum(x => x.TotalCharacters), accepted = sources.Sum(x => x.AcceptedCharacters), pending = sources.Sum(x => x.PendingCharacters);
                return new CatchUpEventProgress(entry.Id, entry.Task, total, accepted, pending, total - accepted,
                    sources.Any(x => x.HasSending), sources.Any(x => x.HasUnknown), sources);
            }).ToImmutableArray();
    }

    private readonly record struct CatchRange(int Start, int Length) { internal int End => checked(Start + Length); }
    private static IEnumerable<OutboxMessage> EventDeliveries(IEnumerable<OutboxMessage> outputs, IEnumerable<CatchUpBatch> batches, Guid eventId)
    {
        var ids = batches.SelectMany(x => x.Members).Where(x => x.SourceEventId == eventId).Select(x => x.CatchUpOutboxId).ToHashSet();
        return outputs.Where(x => x.NotificationEventId == eventId || ids.Contains(x.Id));
    }
    private static bool EventDeliveryUncertain(ConversationSnapshot snapshot, Guid eventId) =>
        EventDeliveries(snapshot.Outbox, snapshot.CatchUpBatches, eventId).Any(x => x.Stage is OutboxStage.Sending or OutboxStage.Unknown);
    private static IEnumerable<CatchRange> CoveredRanges(IEnumerable<OutboxMessage> outputs, IEnumerable<CatchUpBatch> batches,
        OutboxMessage original, Func<OutboxMessage, bool> included)
    {
        var selected = outputs.Where(included).Select(x => x.Id).ToHashSet();
        return batches.SelectMany(x => x.Members).Where(x => selected.Contains(x.CatchUpOutboxId)).SelectMany(x => x.Sources)
            .Where(x => x.OutboxId == original.Id).Select(x => new CatchRange(x.Start, x.Length ?? original.Body.Length));
    }
    private static CatchRange[] MergeRanges(IEnumerable<CatchRange> source)
    {
        var result = new List<CatchRange>();
        foreach (var range in source.OrderBy(x => x.Start))
        {
            if (result.Count == 0 || result[^1].End < range.Start) result.Add(range);
            else { var last = result[^1]; result[^1] = new(last.Start, Math.Max(last.End, range.End) - last.Start); }
        }
        return result.ToArray();
    }
    private static int AcceptedSourceCharacters(IEnumerable<OutboxMessage> outputs, IEnumerable<CatchUpBatch> batches, OutboxMessage original) =>
        original.Stage == OutboxStage.Accepted ? original.Body.Length
            : MergeRanges(CoveredRanges(outputs, batches, original, x => x.Stage == OutboxStage.Accepted)).Sum(x => x.Length);
    private static bool EventCoverageComplete(IEnumerable<OutboxMessage> outputs, IEnumerable<CatchUpBatch> batches, Guid eventId)
    {
        var originals = outputs.Where(x => x.NotificationEventId == eventId).ToArray();
        return originals.Length > 0 && originals.All(x => AcceptedSourceCharacters(outputs, batches, x) == x.Body.Length);
    }
    private static CatchRange[] UncoveredRanges(ConversationSnapshot snapshot, OutboxMessage original)
    {
        if (original.Stage == OutboxStage.Accepted) return [];
        var covered = MergeRanges(CoveredRanges(snapshot.Outbox, snapshot.CatchUpBatches, original,
            x => x.Stage is OutboxStage.Pending or OutboxStage.Sending or OutboxStage.Accepted or OutboxStage.Unknown));
        var result = new List<CatchRange>(); int cursor = 0;
        foreach (var range in covered)
        { if (cursor < range.Start) result.Add(new(cursor, range.Start - cursor)); cursor = Math.Max(cursor, range.End); }
        if (cursor < original.Body.Length) result.Add(new(cursor, original.Body.Length - cursor));
        return result.ToArray();
    }
    private static string SpanHeading(ConversationEvent entry, string zoneId) =>
        TimeZoneInfo.ConvertTime(entry.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById(zoneId))
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) + " 记录（待补报部分）：\n";
    private static bool ScalarBoundary(string body, int index) => index == 0 || index == body.Length
        || !(char.IsHighSurrogate(body[index - 1]) && char.IsLowSurrogate(body[index]));
    private static string RenderSpanMember(ConversationEvent original, CatchUpMember member, IEnumerable<OutboxMessage> outputs, string zoneId) =>
        SpanHeading(original, zoneId) + string.Join("\n", member.Sources.Select(source =>
        {
            var body = outputs.Single(x => x.Id == source.OutboxId).Body;
            return body.Substring(source.Start, source.Length!.Value);
        }));
    private sealed class CatchPage(Guid id, string group)
    {
        internal Guid Id { get; } = id;
        internal string Group { get; } = group;
        internal StringBuilder Text { get; } = new(CatchUpHeading(group));
        internal List<CatchUpMember> Members { get; } = [];
    }

    private CatchUpCaptureResult PrepareSpanCatchUp(LedgerState state, CatchUpWindow window)
    {
        var snapshot = SnapshotOf(state, window.Identity);
        var selected = window.CandidateEventIds.Select(id => snapshot.Events.Single(x => x.Id == id)).Select(entry =>
        {
            var outputs = snapshot.Outbox.Where(x => x.NotificationEventId == entry.Id).OrderBy(x => x.SegmentIndex).ToArray();
            var draft = snapshot.NotificationDrafts.SingleOrDefault(x => x.EventId == entry.Id);
            var run = draft?.WorkRunId is { } id ? snapshot.CommitmentRuns.Single(x => x.Id == id) : null;
            return (Entry: entry, Outputs: outputs, Run: run, Group: CatchUpGroup(snapshot, entry, run));
        }).Where(x => SpanSourceReady(snapshot, x.Entry, x.Outputs, _clock.UtcNow))
            .OrderBy(x => x.Group, StringComparer.Ordinal).ThenBy(x => x.Entry.RecordedAt).ThenBy(x => x.Entry.Id)
            .Take(_options.CatchUp.MaximumEvents).ToArray();
        var pages = new List<CatchPage>();
        var planned = new Dictionary<Guid, int>();
        foreach (var source in selected)
        {
            var remaining = source.Outputs.SelectMany(output => UncoveredRanges(snapshot, output).Select(range => (Output: output, Range: range))).ToList();
            int index = 0, cursor = remaining.Count == 0 ? 0 : remaining[0].Range.Start;
            while (index < remaining.Count)
            {
                var heading = SpanHeading(source.Entry, window.Input.DisplayTimeZoneId);
                var page = pages.LastOrDefault();
                int separator = page?.Members.Count > 0 ? 2 : 0;
                if (page == null || page.Group != source.Group
                    || page.Text.Length + separator + heading.Length + 2 > _options.CatchUp.MaximumPageCharacters
                    || !CanShareSpanPage(snapshot, pages, page, source.Run))
                {
                    if (pages.Count == _options.CatchUp.MaximumPages) break;
                    page = new(Guid.NewGuid(), source.Group); pages.Add(page); separator = 0;
                }
                if (separator != 0) page.Text.Append("\n\n");
                page.Text.Append(heading);
                var spans = ImmutableArray.CreateBuilder<CatchUpSource>();
                while (index < remaining.Count)
                {
                    var piece = remaining[index];
                    int gap = spans.Count == 0 ? 0 : 1;
                    int take = Math.Min(piece.Range.End - cursor, _options.CatchUp.MaximumPageCharacters - page.Text.Length - gap);
                    if (take > 0 && !ScalarBoundary(piece.Output.Body, cursor + take)) take--;
                    if (take <= 0) break;
                    if (gap != 0) page.Text.Append('\n');
                    page.Text.Append(piece.Output.Body.AsSpan(cursor, take));
                    spans.Add(new(piece.Output.Id, piece.Output.SendAttempts, CatchUpHash(piece.Output.Body), cursor, take));
                    planned[source.Entry.Id] = planned.GetValueOrDefault(source.Entry.Id) + take;
                    cursor += take;
                    if (cursor == piece.Range.End)
                    { index++; if (index < remaining.Count) cursor = remaining[index].Range.Start; }
                    else break;
                }
                if (spans.Count == 0) throw Error(LedgerErrors.CapacityExceeded, "The configured catch-up page cannot contain a complete Unicode scalar and its provenance heading.");
                page.Members.Add(new(source.Entry.Id, spans.ToImmutable(), page.Id, source.Group, source.Run?.Id, source.Run?.WorkVersion ?? 0));
            }
        }
        int deferred = window.CandidateEventIds.Count(id => snapshot.Outbox.Where(x => x.NotificationEventId == id)
            .Sum(x => x.Body.Length - AcceptedSourceCharacters(snapshot.Outbox, snapshot.CatchUpBatches, x)) > planned.GetValueOrDefault(id));
        var updated = window with { Stage = pages.Count == 0 ? CatchUpWindowStage.Empty : CatchUpWindowStage.Prepared, DeferredEventCount = deferred };
        Replace(state.CatchUpWindows, window, updated);
        if (pages.Count == 0) return new(updated, null, []);
        state.SchemaVersion = Math.Max(state.SchemaVersion, 4);
        var now = _clock.UtcNow; var batchId = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var parts = pages.Select(x => x.Text.ToString()).ToImmutableArray();
        foreach (var part in parts) ValidateBody(part);
        string body = string.Join("\n\n", parts); ValidateBody(body);
        var entry = new ConversationEvent(eventId, window.Identity, ConversationSource.Notification, body,
            now, now, Revision(state, window.Identity), ExternalEventKey: "catch-up/" + batchId.ToString("N"), NotificationSegments: parts);
        var batch = new CatchUpBatch(batchId, window.Id, window.Identity, eventId, now, pages.SelectMany(x => x.Members).ToImmutableArray(), 2);
        var outbox = pages.Select((page, i) => new OutboxMessage(page.Id, window.Identity, null, entry.Revision, i,
            parts[i], OutboxStage.Pending, now, now, Kind: OutboxKind.Notification, NotificationEventId: eventId)).ToImmutableArray();
        state.Events.Add(entry); state.CatchUpBatches.Add(batch); state.Outbox.AddRange(outbox);
        return new(updated, batch, outbox);
    }
    private bool CanShareSpanPage(ConversationSnapshot snapshot, List<CatchPage> pages, CatchPage page, CommitmentRun? candidate)
    {
        if (candidate == null) return true;
        // Prior pages must be Accepted before this page can begin, so their runs are already charged.
        // Split only combinations that can never be reserved together. Temporary usage remains a send-time gate.
        var alreadyIntroduced = pages.Where(x => x != page).SelectMany(x => x.Members)
            .Where(x => x.WorkRunId != null).Select(x => x.WorkRunId!.Value).ToHashSet();
        var newRuns = page.Members.Where(x => x.WorkRunId != null).Select(x => x.WorkRunId!.Value)
            .Append(candidate.Id).Distinct().Where(x => !alreadyIntroduced.Contains(x))
            .Select(id => snapshot.CommitmentRuns.Single(x => x.Id == id)).ToArray();
        if (newRuns.Length > Math.Min(_options.CommitmentBudgets.MaximumNotificationsPerMinute,
            _options.CommitmentBudgets.MaximumNotificationsPerDay)) return false;
        foreach (var group in newRuns.GroupBy(x => x.WorkItemId))
        {
            var policy = snapshot.WorkItems.Single(x => x.Id == group.Key).Policy;
            if (group.Count() > policy.MaximumNotificationsPer24Hours || group.Count() > 1 && policy.MinimumIntervalSeconds > 0) return false;
        }
        return true;
    }
    private bool SpanSourceReady(ConversationSnapshot snapshot, ConversationEvent entry, OutboxMessage[] originals, DateTimeOffset now)
    {
        if (entry.RequiredDecision != null || UnclassifiedRequiredDecision(entry) || originals.Length == 0
            || EventDeliveryUncertain(snapshot, entry.Id) || !CatchUpSourcePolicyReady(snapshot, entry, now, checkQuiet: true)) return false;
        bool any = false;
        foreach (var output in originals)
        {
            if (UncoveredRanges(snapshot, output).Length == 0) continue;
            any = true;
            if (output.Stage != OutboxStage.Rejected && !(output.Stage == OutboxStage.Pending && output.SendAttempts == 0)
                || !OutputDependencyReady(snapshot, output)) return false;
        }
        return any;
    }
    private static bool CatchUpRunCharged(ConversationSnapshot snapshot, CatchUpBatch batch, Guid runId) => batch.ManifestVersion >= 2
        && snapshot.CommitmentBudgetReservations.Any(x => x.CatchUpBatchId == batch.Id && x.RunId == runId && x.Kind == CommitmentBudgetKind.Notification);
    private bool SpanPageReady(ConversationSnapshot snapshot, CatchUpBatch batch, OutboxMessage page, DateTimeOffset now,
        bool enforceBudget, bool checkQuiet)
    {
        var members = batch.Members.Where(x => x.CatchUpOutboxId == page.Id).ToArray();
        if (members.Length == 0) return false;
        foreach (var member in members)
        {
            var entry = snapshot.Events.Single(x => x.Id == member.SourceEventId);
            if (enforceBudget && EventDeliveryUncertain(snapshot, entry.Id)
                || !CatchUpSourcePolicyReady(snapshot, entry, now, checkQuiet)) return false;
            foreach (var source in member.Sources)
            {
                var original = snapshot.Outbox.Single(x => x.Id == source.OutboxId);
                if (original.SendAttempts != source.SendAttempts || original.Stage != OutboxStage.Rejected
                    && !(original.Stage == OutboxStage.Pending && original.SendAttempts == 0)) return false;
            }
            if (member.WorkRunId is { } runId && snapshot.CommitmentRuns.Single(x => x.Id == runId).WorkVersion != member.WorkVersion) return false;
        }
        if (!enforceBudget) return true;
        var newRuns = members.Where(x => x.WorkRunId != null).Select(x => x.WorkRunId!.Value).Distinct()
            .Where(id => !CatchUpRunCharged(snapshot, batch, id)).Select(id => snapshot.CommitmentRuns.Single(x => x.Id == id)).ToArray();
        foreach (var group in newRuns.GroupBy(x => x.WorkItemId))
        {
            var work = snapshot.WorkItems.Single(x => x.Id == group.Key);
            if (group.Any(x => x.DeliveryAttempts.Length >= work.Policy.MaximumDeliveryAttempts)) return false;
            var attempts = snapshot.CommitmentRuns.Where(x => x.WorkItemId == work.Id).SelectMany(x =>
                x.DeliveryAttempts.IsDefault ? ImmutableArray<DateTimeOffset>.Empty : x.DeliveryAttempts).ToArray();
            if (attempts.Count(x => x > now.AddHours(-24)) + group.Count() > work.Policy.MaximumNotificationsPer24Hours
                || group.Count() > 1 && work.Policy.MinimumIntervalSeconds > 0
                || attempts.Any(x => x.AddSeconds(work.Policy.MinimumIntervalSeconds) > now)) return false;
        }
        return BudgetStatus(snapshot.Identity, snapshot.CommitmentBudgetReservations, now).Windows
            .Where(x => x.Kind == CommitmentBudgetKind.Notification).All(x => x.Used + newRuns.Length <= x.Limit);
    }
}
