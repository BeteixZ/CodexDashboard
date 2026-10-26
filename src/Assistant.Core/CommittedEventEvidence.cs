using System.Collections.Immutable;

namespace Assistant.Core;

public sealed record CommittedEventEvidence(ConversationEvent Event, ImmutableArray<string> ToolNames,
    ImmutableArray<string> InputTexts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<CommittedCatchUpMember> Members = default);
public sealed record CommittedCatchUpSpan(Guid OutboxId, string BodyHash, int Start, int Length, string Text);
public sealed record CommittedCatchUpMember(CommittedEventEvidence Source, ImmutableArray<CommittedCatchUpSpan> Spans);

public sealed partial class DurableConversationLedger
{
    // One committed state supplies the event and its producing context. No transaction-lock reentry.
    public CommittedEventEvidence? ReadCommittedEventEvidence(ConversationIdentity identity, Guid eventId)
    {
        var state = Volatile.Read(ref _state);
        return ReadCommittedEventEvidence(state, identity, eventId);
    }
    private CommittedEventEvidence? ReadCommittedEventEvidence(LedgerState state, ConversationIdentity identity, Guid eventId)
    {
        var entry = state.Events.SingleOrDefault(e => e.Identity == identity && e.Id == eventId);
        if (entry == null) return null;
        GenerationRecord? generation = null;
        ToolInvocationRecord? invocation = null;
        var dependencies = entry.OutputDependencies;
        Guid? deliveryEvent = entry.Id, pageId = null;
        if (entry.Source == ConversationSource.Assistant)
        {
            var output = state.Outbox.SingleOrDefault(o => o.Identity == identity && o.Id == entry.RelatedOutboxId);
            generation = state.Generations.SingleOrDefault(g => g.Identity == identity && g.Id == output?.GenerationId);
            var notice = state.Events.SingleOrDefault(e => e.Identity == identity && e.Id == output?.NotificationEventId);
            deliveryEvent = output?.NotificationEventId; pageId = output?.Id;
            if (notice?.RequiredDecision != null) entry = entry with { RequiredDecision = notice.RequiredDecision };
            dependencies = generation?.GenerationContext?.Dependencies ?? notice?.OutputDependencies ?? OutputDependencies.LegacyUnknown;
        }
        if (entry.RelatedInvocationId is { } invocationId)
        {
            invocation = state.ToolInvocations.SingleOrDefault(i => i.Identity == identity && i.Id == invocationId);
            dependencies = invocation?.GenerationContext?.Dependencies ?? dependencies ?? OutputDependencies.LegacyUnknown;
        }
        var tools = generation != null ? state.ToolInvocations.Where(i => i.GenerationId == generation.Id).Select(i => i.ToolName).ToImmutableArray()
            : invocation != null ? [invocation.ToolName] : ImmutableArray<string>.Empty;
        var inputs = generation != null ? state.Inbox.Where(i => generation.InboxIds.Contains(i.Id)).Select(i => i.Body).ToImmutableArray() : [];
        var batch = state.CatchUpBatches.SingleOrDefault(b => b.Identity == identity && b.DeliveryEventId == deliveryEvent);
        if (batch == null) return new(entry with { OutputDependencies = dependencies }, tools, inputs);
        var members = ImmutableArray.CreateBuilder<CommittedCatchUpMember>();
        foreach (var group in batch.Members.Where(m => pageId == null || m.CatchUpOutboxId == pageId).GroupBy(m => m.SourceEventId))
        {
            var original = ReadCommittedEventEvidence(state, identity, group.Key)!;
            var spans = group.SelectMany(m => m.Sources).Select(span =>
            {
                var output = state.Outbox.Single(o => o.Id == span.OutboxId && o.Identity == identity);
                int length = span.Length ?? output.Body.Length;
                return new CommittedCatchUpSpan(output.Id, span.BodyHash, span.Start, length, output.Body.Substring(span.Start, length));
            }).ToImmutableArray();
            members.Add(new(original, spans));
        }
        // Different original contexts are not collapsed or relabelled as independent.
        return new(entry with { OutputDependencies = OutputDependencies.LegacyUnknown }, tools, inputs, members.ToImmutable());
    }
}
