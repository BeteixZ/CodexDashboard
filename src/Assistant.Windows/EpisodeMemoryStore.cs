using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

// References only. Original prose remains in the protected conversation journal.
internal sealed record EpisodeEventReference(ConversationIdentity Identity, Guid EventId, string Fingerprint)
{
    internal static EpisodeEventReference From(EpisodeSource source) => source.Reference;
    internal static EpisodeEventReference From(ConversationEvent source) => new(source.Identity, source.Id,
        WeChatText.Hash(JsonSerializer.Serialize(source, Settings.Json)));
    internal static void Validate(int format, AssistantMemoryMetadata metadata)
    {
        if (metadata.Events.IsEmpty) return;
        if (format is not (4 or 5 or 6 or 7) || metadata.Events.Length > 8
            || metadata.Scope != AssistantMemoryLayers.PersonalScope && (format < 5 || metadata.Scope.Kind != AssistantMemoryScopeKind.Project)
            || metadata.Confirmation != AssistantMemoryConfirmation.Explicit
            || metadata.Events.Any(e => e == null || e.Identity == null || e.EventId == Guid.Empty
                || e.Fingerprint == null || e.Fingerprint.Length != 64 || !e.Fingerprint.All(Uri.IsHexDigit))
            || metadata.Events.Select(e => e.EventId).Distinct().Count() != metadata.Events.Length
            || metadata.Events.Select(e => e.Identity).Distinct().Count() != 1)
            throw new InvalidDataException("episode_sources_invalid");
        if (metadata.Scope.Kind == AssistantMemoryScopeKind.Project) ProjectMemoryProof.ParseReference(metadata.Scope.ProjectReference!);
    }
}
internal sealed record EpisodeMemoryEditorSnapshot(string Profile, string Stamp,
    ImmutableArray<EpisodeSource> Sources, AssistantMemoryEntry? Summary, ProjectMemoryTarget? Project = null);

internal sealed partial class AssistantMemoryStore
{
    // The provider reads a committed snapshot without acquiring the ledger's transaction lock.
    internal Func<ConversationIdentity, Guid, EpisodeSource?>? EpisodeSourceReader { get; set; }
    internal Func<ProjectMemoryTarget, bool>? EpisodeProjectCurrent { get; set; }

    internal static bool EpisodeSourceEligible(ConversationEvent e) => e.RequiredDecision == null
        && e.Source is not ConversationSource.System
        && (e.Source is ConversationSource.TrustedUser or ConversationSource.UntrustedContent or ConversationSource.Report
            || e.OutputDependencies is { Mode: OutputDependencyMode.Independent or OutputDependencyMode.Bound });

    private bool EpisodeSourceCurrent(AssistantMemoryDocument doc, EpisodeEventReference reference, DateTimeOffset now,
        string? projectReference = null)
    {
        var current = EpisodeSourceReader?.Invoke(reference.Identity, reference.EventId);
        if (current == null || current.Reference != reference || current.Event.RequiredDecision != null
            || !current.DependencyEvents.All(e => EpisodeSourceEligible(e) && EpisodeContextCurrent(doc, e, now, projectReference))) return false;
        var history = AssistantMemoryRevocations.Capture(doc, now);
        return current.DependencyEvents.All(e =>
            !(e.SourceMessageId is { } sourceId && history.IsHistoricalSourceRevoked(e.Identity, sourceId, e.Revision))
            && !(e.RelatedInvocationId is { } invocation && history.IsHistoricalInvocationRevoked(invocation))
            && (e.Source == ConversationSource.Report || history.UnknownSourceHistoryCutoffUtc == null || e.RecordedAt > history.UnknownSourceHistoryCutoffUtc));
    }
    private bool EpisodeCurrent(AssistantMemoryDocument doc, AssistantMemoryEntry entry, DateTimeOffset now) =>
        entry.Metadata!.Events.All(reference => EpisodeSourceCurrent(doc, reference, now, entry.Metadata.Scope.ProjectReference));

    internal EpisodeMemoryEditorSnapshot CaptureEpisodeEditor(IEnumerable<ConversationEvent> events, string? summaryId = null)
        => CaptureEpisodeSources(events.Select(e => (EpisodeSource)e!), summaryId);

    internal EpisodeMemoryEditorSnapshot CaptureEpisodeSources(IEnumerable<EpisodeSource> events, string? summaryId = null,
        ProjectMemoryTarget? project = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow; var doc = ReadDocument();
            VerifyEpisodeProject(project, now);
            var summary = summaryId == null ? null : doc.Entries.SingleOrDefault(e => e.Id == summaryId);
            if (summaryId != null && (summary?.Metadata?.Events.IsEmpty != false || summary.Metadata.InvalidatedAtUtc != null
                || summary.Metadata.ExpiresAtUtc <= now || summary.Metadata.Scope.ProjectReference != project?.Project.Reference
                || !EpisodeCurrent(doc, summary, now)))
                throw new ChatGptException("摘要来源已不可用。请另选有效记录，旧正文不会重新使用。");
            var sources = events.Where(e => EpisodeSourceCurrent(doc, e.Reference, now, project?.Project.Reference))
                .OrderByDescending(e => e.Event.RecordedAt).ThenBy(e => e.Id).ToImmutableArray();
            return new(doc.Profile, CaptureDocument(doc, now, project).Stamp, sources, summary, project);
        }
    }

    internal AssistantMemoryEntry SaveEpisodeFromEditor(EpisodeMemoryEditorSnapshot snapshot, string text,
        IReadOnlyList<EpisodeEventReference> references)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow; text = ValidateText(text);
            var doc = EpisodeDocument(snapshot, now);
            var sources = ValidateEpisodeSources(doc, snapshot, references, now);
            // Exact original memory sources inherit deletion/correction/expiry. Unrelated memories are not dependencies.
            var originalEvents = sources.SelectMany(s => s.DependencyEvents).ToArray();
            var contextItems = originalEvents.SelectMany(s => EpisodeContextItems(s)).Distinct().ToArray();
            var dependencies = doc.Entries.Where(e => e.Metadata?.InvalidatedAtUtc == null && (contextItems.Any(i => i.Id == e.Id)
                || originalEvents.Any(s =>
                e.Provenance is { } p && p.Identity == s.Identity && p.SourceMessageId == s.SourceMessageId
                    && p.SourceRevision == s.Revision))).ToArray();
            var prior = snapshot.Summary;
            if (prior != null && dependencies.Any(e => e.Id == prior.Id))
                throw new ChatGptException("所选记录使用过当前摘要，不能反过来作为它的纠正来源。请另选原始记录。");
            string id = prior?.Id ?? Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            if (prior == null && (doc.Entries.Any(e => e.Id == id) || doc.Revocations.Any(e => e.EntryId == id)))
                throw new InvalidOperationException("summary_id_collision");
            var scope = snapshot.Project is { } target ? new AssistantMemoryScope(AssistantMemoryScopeKind.Project, target.Project.Reference)
                : AssistantMemoryLayers.PersonalScope;
            var metadata = new AssistantMemoryMetadata(AssistantMemoryKind.EpisodeSummary, scope,
                prior == null ? 1 : checked(AssistantMemoryLayers.Version(prior) + 1),
                dependencies.Select(e => e.Metadata?.ExpiresAtUtc)
                    .Concat(originalEvents.SelectMany(EpisodeSourceDeadlines)).Min(),
                Replaces: prior == null ? null : AssistantMemoryLayers.Reference(prior),
                DerivedFrom: dependencies.Select(AssistantMemoryLayers.Reference).ToImmutableArray(), EventSources: references.ToImmutableArray());
            var entry = new AssistantMemoryEntry(id, text, prior?.CreatedAt ?? now, now,
                Provenance: new("desktop_editor", doc.Profile, now), Metadata: metadata);
            if (prior == null) doc.Entries.Add(entry);
            else
            {
                int index = doc.Entries.FindIndex(e => e.Id == prior.Id && AssistantMemoryLayers.Version(e) == AssistantMemoryLayers.Version(prior));
                if (index < 0) throw new InvalidOperationException("summary_target_changed");
                doc.Entries[index] = entry;
                AssistantMemoryRevocations.Add(doc, prior, "corrected", now);
                AssistantMemoryRevocations.InvalidateDependents(doc, prior.Id, now);
            }
            VerifyEpisodeProject(snapshot.Project, now);
            doc.FormatVersion = Math.Max(doc.FormatVersion, snapshot.Project == null ? 4 : snapshot.Project.Project.HostId == "local" ? 5 : snapshot.Project.Project.Backend == "assistant-owned" ? 7 : 6);
            WriteSummaryDocument(doc, entry); return entry;
        }
    }

    internal ImmutableArray<EpisodeSource> ReadEpisodeDraftSources(EpisodeMemoryEditorSnapshot snapshot,
        IReadOnlyList<EpisodeEventReference> references)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var doc = EpisodeDocument(snapshot, now);
            return ValidateEpisodeSources(doc, snapshot, references, now).ToImmutableArray();
        }
    }
    internal EpisodeSource? ReadReviewedEpisodeSource(GenerationContext context, ConversationIdentity identity, Guid eventId, DateTimeOffset now)
    {
        lock (gate)
        {
            var reference = ReadGenerationEntries(context, now).SelectMany(e => e.Metadata?.Events ?? [])
                .FirstOrDefault(r => r.Identity == identity && r.EventId == eventId);
            if (reference == null) return null;
            var source = EpisodeSourceReader?.Invoke(identity, eventId);
            return source != null && EpisodeEventReference.From(source) == reference ? source : null;
        }
    }
    private EpisodeSource[] ValidateEpisodeSources(AssistantMemoryDocument doc, EpisodeMemoryEditorSnapshot snapshot,
        IReadOnlyList<EpisodeEventReference> references, DateTimeOffset now)
    {
        if (snapshot.Profile != doc.Profile || references.Count is < 1 or > 8
            || references.Select(e => e.EventId).Distinct().Count() != references.Count
            || references.Select(e => e.Identity).Distinct().Count() != 1
            || references.Any(r => !snapshot.Sources.Any(s => EpisodeEventReference.From(s) == r)
                || !EpisodeSourceCurrent(doc, r, now, snapshot.Project?.Project.Reference)))
            throw new InvalidOperationException("summary_event_sources_changed");
        // Return the exact immutable records just validated, not a second read of a newer event.
        return references.Select(r => snapshot.Sources.Single(s => s.Id == r.EventId && s.Event.Identity == r.Identity)).ToArray();
    }
    private void VerifyEpisodeProject(ProjectMemoryTarget? target, DateTimeOffset now)
    {
        if (target == null) return;
        ProjectMemoryProof.Validate(target, now);
        if (EpisodeProjectCurrent?.Invoke(target) != true) throw new InvalidOperationException("episode_project_changed");
    }
    private AssistantMemoryDocument EpisodeDocument(EpisodeMemoryEditorSnapshot snapshot, DateTimeOffset now)
    {
        VerifyEpisodeProject(snapshot.Project, now);
        var doc = ReadDocument();
        if (doc.Profile != snapshot.Profile || doc.Profile != Profile || CaptureDocument(doc, now, snapshot.Project).Stamp != snapshot.Stamp)
            throw new InvalidOperationException("summary_sources_changed");
        return doc;
    }
}
