using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace CodexDashboard;

internal sealed record SummaryMemoryEditorSnapshot(string Profile, string Stamp,
    ImmutableArray<AssistantMemoryEntry> Sources, AssistantMemoryEntry? Summary);

internal sealed class SummaryMemorySavedNotificationException(AssistantMemoryEntry entry,
    AssistantMemoryCommittedNotificationException inner) : IOException("Summary was saved, but a change observer failed.", inner)
{
    internal AssistantMemoryEntry SavedEntry { get; } = entry;
    internal string Profile { get; } = inner.Profile;
    internal long Revision { get; } = inner.Revision;
}

internal sealed partial class AssistantMemoryStore
{
    internal SummaryMemoryEditorSnapshot CaptureDerivedSummaryEditor(string? summaryId = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow; var doc = ReadDocument(); var capture = CaptureDocument(doc, now);
            AssistantMemoryEntry? summary = null;
            if (summaryId != null)
            {
                summary = doc.Entries.SingleOrDefault(e => e.Id == summaryId);
                summary = RequireEditableSummary(summary, now);
            }
            var sources = doc.Entries.Where(e => SummarySourceEligible(e, now)).OrderBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
            if (doc.Profile != Profile) throw new InvalidOperationException("summary_profile_changed");
            return new(doc.Profile, capture.Stamp, sources, summary);
        }
    }
    internal AssistantMemoryEntry CreateDerivedSummaryFromEditor(string text,
        IReadOnlyList<AssistantMemoryReference> exactSources, string expectedStamp)
    {
        lock (gate)
        {
            text = ValidateText(text); var now = DateTimeOffset.UtcNow;
            var doc = SummaryDocument(expectedStamp, now);
            var sources = ValidateSummarySources(doc, exactSources, now);
            string id; do { id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(); }
            while (doc.Entries.Any(e => e.Id == id) || doc.Revocations.Any(r => r.EntryId == id));
            var metadata = SummaryMetadata(sources, 1, null);
            var entry = new AssistantMemoryEntry(id, text, now, now,
                Provenance: new("desktop_editor", doc.Profile, now), Metadata: metadata);
            doc.FormatVersion = Math.Max(doc.FormatVersion, 3); doc.Entries.Add(entry); WriteSummaryDocument(doc, entry); return entry;
        }
    }
    internal AssistantMemoryEntry CorrectDerivedSummaryFromEditor(string id, long expectedVersion, string text,
        IReadOnlyList<AssistantMemoryReference> exactSources, string expectedStamp)
    {
        lock (gate)
        {
            text = ValidateText(text); var now = DateTimeOffset.UtcNow;
            var doc = SummaryDocument(expectedStamp, now);
            var prior = RequireEditableSummary(doc.Entries.SingleOrDefault(e => e.Id == id), now);
            var priorMetadata = prior.Metadata!;
            if (priorMetadata.Version != expectedVersion) throw new InvalidOperationException("summary_target_changed");
            var sources = ValidateSummarySources(doc, exactSources, now);
            var metadata = SummaryMetadata(sources, checked(expectedVersion + 1), AssistantMemoryLayers.Reference(prior));
            if (prior.Text == text && priorMetadata.Dependencies.SequenceEqual(metadata.Dependencies)) return prior;
            var updated = prior with { Text = text, UpdatedAt = now,
                Provenance = new("desktop_editor", doc.Profile, now), Metadata = metadata };
            doc.Entries[doc.Entries.IndexOf(prior)] = updated;
            AssistantMemoryRevocations.Add(doc, prior, "corrected", now);
            AssistantMemoryRevocations.InvalidateDependents(doc, prior.Id, now);
            doc.FormatVersion = Math.Max(doc.FormatVersion, 3); WriteSummaryDocument(doc, updated); return updated;
        }
    }
    private void WriteSummaryDocument(AssistantMemoryDocument document, AssistantMemoryEntry entry)
    {
        try { WriteDocument(document); }
        catch (AssistantMemoryCommittedNotificationException error)
            when (error.Profile == document.Profile && error.Revision == document.Revision)
        { throw new SummaryMemorySavedNotificationException(entry, error); }
    }
    private AssistantMemoryDocument SummaryDocument(string expectedStamp, DateTimeOffset now)
    {
        var doc = ReadDocument(); var captured = CaptureDocument(doc, now);
        if (string.IsNullOrEmpty(expectedStamp) || captured.Stamp != expectedStamp || doc.Profile != Profile)
            throw new InvalidOperationException("summary_sources_changed");
        return doc;
    }
    private static bool SummarySourceEligible(AssistantMemoryEntry entry, DateTimeOffset now) => entry.Metadata is { } metadata
        && metadata.Kind is AssistantMemoryKind.General or AssistantMemoryKind.Preference
        && metadata.Scope == AssistantMemoryLayers.PersonalScope && metadata.Confirmation == AssistantMemoryConfirmation.Explicit
        && metadata.InvalidatedAtUtc == null && (metadata.ExpiresAtUtc == null || metadata.ExpiresAtUtc > now);
    private static AssistantMemoryEntry[] ValidateSummarySources(AssistantMemoryDocument document,
        IReadOnlyList<AssistantMemoryReference> exactSources, DateTimeOffset now)
    {
        if (exactSources == null || exactSources.Count is < 2 or > 8 || exactSources.Any(s => s == null)
            || exactSources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != exactSources.Count)
            throw new ChatGptException("请明确选择 2–8 条不同的有效个人记忆作为来源。");
        var sources = new List<AssistantMemoryEntry>();
        foreach (var reference in exactSources)
        {
            var entry = document.Entries.SingleOrDefault(e => e.Id == reference.Id);
            if (entry == null || !SummarySourceEligible(entry, now) || entry.Metadata!.Version != reference.Version)
                throw new InvalidOperationException("summary_source_version_or_scope_changed");
            sources.Add(entry);
        }
        return sources.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
    private static AssistantMemoryMetadata SummaryMetadata(AssistantMemoryEntry[] sources, long version,
        AssistantMemoryReference? replaces) => new(AssistantMemoryKind.EpisodeSummary, AssistantMemoryLayers.PersonalScope,
            version, sources.Select(e => e.Metadata!.ExpiresAtUtc).Min(), AssistantMemoryConfirmation.Explicit,
            replaces, sources.Select(AssistantMemoryLayers.Reference).ToImmutableArray());
    private static AssistantMemoryEntry RequireEditableSummary(AssistantMemoryEntry? entry, DateTimeOffset now)
    {
        if (entry?.Metadata is not { Kind: AssistantMemoryKind.EpisodeSummary, Confirmation: AssistantMemoryConfirmation.Explicit } metadata
            || metadata.Scope != AssistantMemoryLayers.PersonalScope || !metadata.Events.IsEmpty || entry.Provenance?.SourceKind != "desktop_editor")
            throw new ChatGptException("请选择由你在桌面整理保存的记忆摘要。");
        if (metadata.InvalidatedAtUtc != null || metadata.ExpiresAtUtc <= now)
            throw new ChatGptException("这条摘要已经失效，旧正文不会恢复。请选择有效来源另建摘要。");
        return entry!;
    }
}
