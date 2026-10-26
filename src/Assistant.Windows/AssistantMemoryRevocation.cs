using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Assistant.Core;

namespace CodexDashboard;

// No remembered prose or original message content. These are exclusion references,
// never authority to replay the old message or recreate its memory.
internal sealed record AssistantMemoryRevocation(string Profile, string EntryId, long Version,
    DateTimeOffset RevokedAtUtc, string Reason, ConversationIdentity? SourceIdentity = null,
    string? SourceMessageId = null, long? SourceRevision = null, Guid? OperationId = null,
    Guid? InvocationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool KnownEventSummary = false);

internal sealed record AssistantMemoryHistoryFilter(string Profile, long DocumentRevision,
    ImmutableArray<AssistantMemoryRevocation> Revocations, DateTimeOffset? UnknownSourceHistoryCutoffUtc)
{
    internal bool IsHistoricalSourceRevoked(ConversationIdentity identity, string sourceId, long revision) =>
        Revocations.Any(r => r.SourceIdentity == identity && r.SourceMessageId == sourceId && r.SourceRevision == revision);
    internal bool IsHistoricalInvocationRevoked(Guid invocationId) => invocationId != Guid.Empty
        && Revocations.Any(r => r.InvocationId == invocationId);
    internal bool IsEntryVersionRevoked(string id, long version) => Revocations.Any(r => r.EntryId == id && r.Version == version);
}

internal sealed record AssistantMemoryMutation<T>(T Value, AssistantGenerationPersonalization Before,
    AssistantGenerationPersonalization After, bool Changed);

internal static class AssistantMemoryRevocations
{
    internal const int MaximumRecords = 512;
    internal static AssistantMemoryRevocation FromEntry(string profile, AssistantMemoryEntry entry, string reason, DateTimeOffset now)
    {
        var source = entry.Provenance;
        return new(profile, entry.Id, AssistantMemoryLayers.Version(entry), now, reason,
            source?.Identity, source?.SourceMessageId, source?.SourceRevision, source?.OperationId, source?.InvocationId,
            entry.Metadata?.Events.IsEmpty == false);
    }
    internal static void Add(AssistantMemoryDocument document, AssistantMemoryEntry entry, string reason, DateTimeOffset now)
    {
        if (document.Revocations.Any(r => r.EntryId == entry.Id && r.Version == AssistantMemoryLayers.Version(entry)))
            throw new InvalidDataException("memory_version_already_revoked");
        if (document.Revocations.Count >= MaximumRecords)
            throw new ChatGptException("记忆撤销记录已达保存上限，本次未修改；原内容和撤销证据均已保留。");
        document.Revocations.Add(FromEntry(document.Profile, entry, reason, now));
        document.FormatVersion = Math.Max(document.FormatVersion, entry.Metadata?.Events.IsEmpty == false ? 4 : 3);
    }
    internal static void InvalidateDependents(AssistantMemoryDocument document, string sourceId, DateTimeOffset now)
    {
        var before = document.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        AssistantMemoryLayers.InvalidateDependents(document, sourceId, now);
        foreach (var entry in document.Entries.Where(e => e.Metadata?.InvalidatedAtUtc != null
            && before[e.Id].Metadata?.InvalidatedAtUtc == null))
            Add(document, before[entry.Id], "source_revoked", now);
    }
    internal static AssistantMemoryHistoryFilter Capture(AssistantMemoryDocument document, DateTimeOffset asOfUtc)
    {
        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("memory_capture_time_invalid");
        var records = document.Revocations.ToList();
        var entries = document.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var ceasedTimes = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        DateTimeOffset? Ceased(AssistantMemoryEntry entry)
        {
            if (ceasedTimes.TryGetValue(entry.Id, out var known)) return known;
            var own = entry.Metadata?.InvalidatedAtUtc ?? entry.Metadata?.ExpiresAtUtc;
            if (entry.Metadata?.InvalidatedAtUtc == null)
                foreach (var dependency in entry.Metadata?.Dependencies ?? [])
                {
                    var prior = Ceased(entries[dependency.Id]);
                    if (prior != null && (own == null || prior < own)) own = prior;
                }
            ceasedTimes[entry.Id] = own; return own;
        }
        // Expiry is already durable. Derive exclusion without a write/timer so
        // opening after a long shutdown cannot re-inject it through history.
        foreach (var entry in document.Entries)
        {
            DateTimeOffset? ceased = Ceased(entry);
            if (ceased == null || ceased > asOfUtc || records.Any(r => r.EntryId == entry.Id && r.Version == AssistantMemoryLayers.Version(entry))) continue;
            records.Add(FromEntry(document.Profile, entry, entry.Metadata?.InvalidatedAtUtc != null ? "source_revoked" : "expired", ceased.Value));
        }
        return new(document.Profile, document.Revision, records.OrderBy(r => r.EntryId, StringComparer.Ordinal)
            .ThenBy(r => r.Version).ToImmutableArray(), records.Where(r => r.SourceMessageId == null && !r.KnownEventSummary)
                .Select(r => (DateTimeOffset?)r.RevokedAtUtc).Max());
    }
    internal static void Validate(AssistantMemoryDocument document)
    {
        if (document.RulesRevision < 0 || document.PersonalizationRevision < 0 || document.Revocations == null
            || document.Revocations.Count > MaximumRecords || document.Revocations.Any(r => r == null)
            || document.FormatVersion < 3 && (document.RulesRevision != 0 || document.PersonalizationRevision != 0 || document.Revocations.Count != 0)
            || document.Revocations.Select(r => (r.EntryId, r.Version)).Distinct().Count() != document.Revocations.Count)
            throw new InvalidDataException("memory_revocation_document_invalid");
        static bool Token(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
        foreach (var r in document.Revocations)
        {
            if (r.Profile != document.Profile || r.EntryId == null || !Regex.IsMatch(r.EntryId, "\\A[0-9A-F]{8}\\z")
                || r.KnownEventSummary && (document.FormatVersion < 4 || r.SourceIdentity != null)
                || r.Version < 1 || r.RevokedAtUtc == default || r.RevokedAtUtc.Offset != TimeSpan.Zero
                || r.Reason is not ("deleted" or "corrected" or "metadata_changed" or "preference_replaced" or "source_revoked")
                || document.Entries.Any(e => e.Id == r.EntryId && AssistantMemoryLayers.Version(e) <= r.Version))
                throw new InvalidDataException("memory_revocation_invalid");
            if (r.SourceIdentity == null)
            {
                if (r.SourceMessageId != null || r.SourceRevision != null || r.OperationId != null || r.InvocationId != null)
                    throw new InvalidDataException("memory_revocation_partial_source");
            }
            else if (!Token(r.SourceIdentity.UserId) || !Token(r.SourceIdentity.BindingId) || !Token(r.SourceIdentity.ConversationId)
                || !Token(r.SourceMessageId) || r.SourceRevision is not > 0 || r.OperationId == null || r.OperationId == Guid.Empty
                || r.InvocationId == null || r.InvocationId == Guid.Empty)
                throw new InvalidDataException("memory_revocation_source_invalid");
        }
    }
}
