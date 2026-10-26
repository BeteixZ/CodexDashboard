using System;
using System.Collections.Immutable;
using System.Linq;

namespace CodexDashboard;

// Visibility only. Refreshing a view does not refresh its write precondition.
internal sealed record MemoryViewState(string Profile, ImmutableArray<AssistantMemoryEntry> Entries, DateTimeOffset? NextExpiryUtc)
{
    internal bool ContainsVersion(AssistantMemoryEntry entry) => Entries.Any(e => e.Text.Length > 0 && e.Id == entry.Id
        && AssistantMemoryLayers.Version(e) == AssistantMemoryLayers.Version(entry));
}
internal sealed record EpisodeMemoryViewState(ImmutableArray<Guid> AvailableEvents, bool SummaryCurrent, DateTimeOffset? NextExpiryUtc);

internal sealed partial class AssistantMemoryStore
{
    internal MemoryViewState CaptureMemoryView()
    {
        lock (gate) return MemoryView(ReadDocument(), DateTimeOffset.UtcNow);
    }
    private MemoryViewState MemoryView(AssistantMemoryDocument document, DateTimeOffset now)
    {
        var revoked = AssistantMemoryRevocations.Capture(document, now);
        // Keep inactive audit entries manageable, but never display their old prose.
        return new(document.Profile, document.Entries.Select(e => revoked.IsEntryVersionRevoked(e.Id, AssistantMemoryLayers.Version(e))
                || e.Metadata?.Events.IsEmpty == false && !EpisodeCurrent(document, e, now) ? e with { Text = "" } : e)
            .ToImmutableArray(), document.Entries.Where(e => e.Metadata?.InvalidatedAtUtc == null && e.Metadata?.ExpiresAtUtc > now)
                .Select(e => e.Metadata!.ExpiresAtUtc).Min());
    }
    internal EpisodeMemoryViewState CaptureEpisodeView(EpisodeMemoryEditorSnapshot snapshot)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow; var doc = ReadDocument();
            if (doc.Profile != snapshot.Profile) return new([], false, null);
            VerifyEpisodeProject(snapshot.Project, now);
            var view = MemoryView(doc, now);
            return new(snapshot.Sources.Where(e => EpisodeSourceCurrent(doc, EpisodeEventReference.From(e), now, snapshot.Project?.Project.Reference))
                .Select(e => e.Id).ToImmutableArray(), snapshot.Summary == null || view.ContainsVersion(snapshot.Summary),
                snapshot.Sources.SelectMany(s => s.DependencyEvents).SelectMany(EpisodeSourceDeadlines)
                    .Where(t => t > now).Append(view.NextExpiryUtc).Append(snapshot.Project?.ValidUntilUtc).Min());
        }
    }
}
