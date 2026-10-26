using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class AssistantMemoryStore
{
    // Verify the original selection, not today's complete selection. An unrelated new memory is not a revocation.
    // The original document revision is used only to verify its fingerprint, never as current write authority.
    private bool EpisodeContextCurrent(AssistantMemoryDocument doc, ConversationEvent source, DateTimeOffset now, string? projectReference)
    {
        var dependencies = source.OutputDependencies;
        if (dependencies == null || dependencies.Mode == OutputDependencyMode.Independent)
            return projectReference == null || source.Task == null;
        if (dependencies.Mode != OutputDependencyMode.Bound || dependencies.Contexts.IsDefault) return false;
        ProjectMemoryTarget? originalProject;
        try
        {
            originalProject = ProjectMemoryProof.Read(dependencies);
            if (originalProject != null) ProjectMemoryProof.Validate(originalProject, source.RecordedAt);
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return false; }
        if (originalProject != null && originalProject.Project.Reference != projectReference
            || originalProject != null && source.Task != null && source.Task != originalProject.Task
            || projectReference != null && originalProject == null && source.Task != null) return false;
        if (dependencies.Contexts.Any(c => c.Provider != ProjectMemoryProof.Provider && c.ScopeKey != doc.Profile
                || c.Provider is not (AssistantMemoryDependencies.PolicyProvider or AssistantMemoryDependencies.MemoryProvider or ProjectMemoryProof.Provider))
            || EpisodeSourceDeadlines(source).Any(t => t <= now)) return false;
        var policies = dependencies.Contexts.Where(c => c.Provider == AssistantMemoryDependencies.PolicyProvider).ToArray();
        var memories = dependencies.Contexts.Where(c => c.Provider == AssistantMemoryDependencies.MemoryProvider).ToArray();
        if (policies.Length != 1 || policies[0].Revision != doc.PersonalizationRevision) return false;
        if (policies[0].Fingerprint == WeChatText.Hash("disabled")) return memories.Length == 0 && originalProject == null;
        if (policies[0].Fingerprint != WeChatText.Hash("enabled") || memories.Length != 1) return false;
        var memory = memories[0];
        var history = AssistantMemoryRevocations.Capture(doc, now);
        DateTimeOffset? originalExpiry = originalProject?.ValidUntilUtc;
        foreach (var item in memory.Items)
        {
            var entry = doc.Entries.SingleOrDefault(e => e.Id == item.Id && AssistantMemoryLayers.Version(e) == item.Version);
            if (entry == null || history.IsEntryVersionRevoked(item.Id, item.Version)
                || entry.Metadata?.Scope.Kind == AssistantMemoryScopeKind.Project
                    && entry.Metadata.Scope.ProjectReference != originalProject?.Project.Reference
                || entry.Metadata?.Events.IsEmpty == false && !EpisodeCurrent(doc, entry, now)) return false;
            if (entry.Metadata?.ExpiresAtUtc is { } expiry && (originalExpiry == null || expiry < originalExpiry)) originalExpiry = expiry;
        }
        if (originalProject != null && memory.ValidUntilUtc != originalExpiry) return false;
        string stamp = AssistantMemoryDependencies.ContentStamp(doc.Profile, memory.Revision, doc.RulesRevision,
            doc.PersonalizationRevision, WeChatText.Hash(ReadRulesCore(doc.Profile)),
            memory.Items.Select(i => new AssistantMemoryReference(i.Id, i.Version)).ToImmutableArray(), originalProject);
        return memory.Fingerprint == WeChatText.Hash(stamp);
    }

    // Project proof expiry limits one live read, not the lifetime of its archived report.
    // Memory.ValidUntilUtc is an aggregate (including that proof); actual entry expiries
    // are checked individually above and inherited by the saved summary.
    private static IEnumerable<DateTimeOffset?> EpisodeSourceDeadlines(ConversationEvent source)
    {
        var contexts = source.OutputDependencies?.Contexts ?? [];
        bool hasProject = contexts.Any(c => c.Provider == ProjectMemoryProof.Provider);
        return contexts.Where(c => !hasProject || c.Provider is not (ProjectMemoryProof.Provider or AssistantMemoryDependencies.MemoryProvider))
            .Select(c => c.ValidUntilUtc);
    }

    private static ImmutableArray<ContextItemVersion> EpisodeContextItems(ConversationEvent source) =>
        source.OutputDependencies?.Contexts.Where(c => c.Provider == AssistantMemoryDependencies.MemoryProvider)
            .SelectMany(c => c.Items).ToImmutableArray() ?? [];
}
