using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexDashboard;

// Host-owned applicability data. None of these values grants tool authority.
internal enum AssistantMemoryKind { General, Preference, ProjectKnowledge, EpisodeSummary, Persona }
internal enum AssistantMemoryScopeKind { Profile, Project }
internal enum AssistantMemoryConfirmation { LegacyUnspecified, Explicit, Pending }
internal sealed record AssistantMemoryReference(string Id, long Version);
internal sealed record AssistantMemoryScope(AssistantMemoryScopeKind Kind, string? ProjectReference = null);
internal sealed record AssistantMemoryMetadata(AssistantMemoryKind Kind, AssistantMemoryScope Scope, long Version,
    DateTimeOffset? ExpiresAtUtc = null, AssistantMemoryConfirmation Confirmation = AssistantMemoryConfirmation.Explicit,
    AssistantMemoryReference? Replaces = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<AssistantMemoryReference> DerivedFrom = default,
    DateTimeOffset? InvalidatedAtUtc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<EpisodeEventReference> EventSources = default)
{
    // Records are compared by the manual command guard, including after reopen.
    // ImmutableArray's reference equality would make unchanged disk entries differ.
    public bool Equals(AssistantMemoryMetadata? other) => other != null && Kind == other.Kind && Scope == other.Scope
        && Version == other.Version && ExpiresAtUtc == other.ExpiresAtUtc && Confirmation == other.Confirmation
        && Replaces == other.Replaces && InvalidatedAtUtc == other.InvalidatedAtUtc
        && Dependencies.SequenceEqual(other.Dependencies) && Events.SequenceEqual(other.Events);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Kind); hash.Add(Scope); hash.Add(Version); hash.Add(ExpiresAtUtc);
        hash.Add(Confirmation); hash.Add(Replaces); hash.Add(InvalidatedAtUtc);
        foreach (var reference in Dependencies) hash.Add(reference);
        foreach (var reference in Events) hash.Add(reference);
        return hash.ToHashCode();
    }
    [System.Text.Json.Serialization.JsonIgnore]
    internal ImmutableArray<AssistantMemoryReference> Dependencies => DerivedFrom.IsDefault ? [] : DerivedFrom;
    [System.Text.Json.Serialization.JsonIgnore]
    internal ImmutableArray<EpisodeEventReference> Events => EventSources.IsDefault ? [] : EventSources;
}
internal sealed record AssistantMemoryRetrievalContext(DateTimeOffset AsOfUtc, ProjectMemoryTarget? Project = null);
internal sealed record AssistantMemoryInputManifest(string Profile, long DocumentRevision, string RulesHash,
    ImmutableArray<AssistantMemoryReference> IncludedReferences, DateTimeOffset? ValidUntilUtc,
    long RulesRevision = 0, long PersonalizationRevision = 0, ProjectMemoryTarget? Project = null);
internal sealed record AssistantGenerationPersonalization(bool Enabled, string Profile,
    long PersonalizationRevision, AssistantPersonalization? Content);

internal static class AssistantMemoryLayers
{
    internal static readonly AssistantMemoryScope PersonalScope = new(AssistantMemoryScopeKind.Profile);
    internal static long Version(AssistantMemoryEntry entry) => entry.Metadata?.Version ?? 1;
    internal static AssistantMemoryReference Reference(AssistantMemoryEntry entry) => new(entry.Id, Version(entry));
    internal static AssistantMemoryMetadata Initial(AssistantMemoryKind kind) => new(kind, PersonalScope, 1,
        DerivedFrom: []);
    internal static AssistantMemoryMetadata Corrected(AssistantMemoryEntry prior, AssistantMemoryKind kind) =>
        new(kind, PersonalScope, checked(Version(prior) + 1), prior.Metadata?.ExpiresAtUtc,
            AssistantMemoryConfirmation.Explicit, Reference(prior), []);
    private static bool Id(string? value) => value != null && Regex.IsMatch(value, "\\A[0-9A-F]{8}\\z");
    private static bool Utc(DateTimeOffset? value) => value == null || value != default(DateTimeOffset)
        && value.Value.Offset == TimeSpan.Zero;
    internal static void Validate(AssistantMemoryDocument document)
    {
        if (document.FormatVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || document.Revision < 0 || document.Entries == null
            || document.Entries.Count > AssistantMemoryStore.MaxEntries || document.Entries.Any(e => e == null)
            || document.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != document.Entries.Count)
            throw new InvalidDataException("memory_document_invalid");
        var entries = document.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            var metadata = entry.Metadata;
            if (!Id(entry.Id) || entry.Text == null || entry.Text.Length > AssistantMemoryStore.MaxEntryChars
                || entry.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                || (metadata?.InvalidatedAtUtc == null ? string.IsNullOrWhiteSpace(entry.Text) : entry.Text.Length != 0)
                || metadata != null && (entry.CreatedAt == default || entry.UpdatedAt < entry.CreatedAt))
                throw new InvalidDataException("memory_entry_invalid");
            if (metadata == null) continue; // v1 entries are not reclassified from their prose.
            if (document.FormatVersion is not (2 or 3 or 4 or 5 or 6 or 7) || !Enum.IsDefined(metadata.Kind) || metadata.Scope == null
                || !Enum.IsDefined(metadata.Scope.Kind) || !Enum.IsDefined(metadata.Confirmation)
                || metadata.Version < 1 || !Utc(metadata.ExpiresAtUtc) || !Utc(metadata.InvalidatedAtUtc)
                || metadata.Scope.Kind == AssistantMemoryScopeKind.Profile && metadata.Scope.ProjectReference != null
                || metadata.Scope.Kind == AssistantMemoryScopeKind.Project
                    && (string.IsNullOrWhiteSpace(metadata.Scope.ProjectReference) || metadata.Scope.ProjectReference.Length > 512
                        || metadata.Scope.ProjectReference.Any(char.IsControl))
                || metadata.Kind == AssistantMemoryKind.ProjectKnowledge && metadata.Scope.Kind != AssistantMemoryScopeKind.Project
                || metadata.Replaces != null && (!Id(metadata.Replaces.Id) || metadata.Replaces.Id != entry.Id
                    || metadata.Replaces.Version != metadata.Version - 1)
                || metadata.Version == 1 && metadata.Replaces != null || metadata.Version > 1 && metadata.Replaces == null
                || metadata.Dependencies.Length > AssistantMemoryStore.MaxEntries
                || metadata.Dependencies.Any(r => r == null || !Id(r.Id) || r.Version < 1 || r.Id == entry.Id)
                || metadata.Dependencies.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != metadata.Dependencies.Length
                || (metadata.Kind == AssistantMemoryKind.EpisodeSummary) != (metadata.Dependencies.Length > 0 || metadata.Events.Length > 0)
                || metadata.InvalidatedAtUtc != null && metadata.Dependencies.Length == 0 && metadata.Events.Length == 0)
                throw new InvalidDataException("memory_metadata_invalid");
            EpisodeEventReference.Validate(document.FormatVersion, metadata);
            if (metadata.Scope.ProjectReference?.StartsWith(ProjectMemoryProof.RemotePrefix, StringComparison.Ordinal) == true)
            {
                if (document.FormatVersion < 6) throw new InvalidDataException("remote_project_format_required");
                var remote = ProjectMemoryProof.ParseReference(metadata.Scope.ProjectReference);
                if (remote.Backend == "assistant-owned" && document.FormatVersion < 7) throw new InvalidDataException("remote_owned_project_format_required");
            }
            if (metadata.InvalidatedAtUtc != null) continue; // audit references may point at deleted versions.
            foreach (var dependency in metadata.Dependencies)
                if (!entries.TryGetValue(dependency.Id, out var source) || Version(source) != dependency.Version
                    || source.Metadata?.InvalidatedAtUtc != null
                    || (source.Metadata?.Scope ?? PersonalScope) != metadata.Scope
                        && !(document.FormatVersion >= 5 && !metadata.Events.IsEmpty
                            && metadata.Scope.Kind == AssistantMemoryScopeKind.Project
                            && (source.Metadata?.Scope ?? PersonalScope) == PersonalScope))
                    throw new InvalidDataException("memory_dependency_invalid");
        }
        if (document.Entries.Sum(e => e.Text.Length) > AssistantMemoryStore.MaxTotalChars)
            throw new InvalidDataException("memory_capacity_exceeded");
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        void Visit(AssistantMemoryEntry entry)
        {
            if (complete.Contains(entry.Id) || entry.Metadata?.InvalidatedAtUtc != null) return;
            if (!visiting.Add(entry.Id)) throw new InvalidDataException("memory_dependency_cycle");
            foreach (var reference in entry.Metadata?.Dependencies ?? []) Visit(entries[reference.Id]);
            visiting.Remove(entry.Id); complete.Add(entry.Id);
        }
        foreach (var entry in document.Entries) Visit(entry);
        AssistantMemoryRevocations.Validate(document);
    }
    internal static ImmutableArray<AssistantMemoryEntry> Select(AssistantMemoryDocument document, DateTimeOffset asOfUtc,
        ProjectMemoryTarget? project = null, Func<AssistantMemoryEntry, bool>? episodeCurrent = null)
    {
        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("memory_capture_time_invalid");
        Validate(document);
        if (project != null) ProjectMemoryProof.Validate(project, asOfUtc);
        var entries = document.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var eligibility = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Eligible(AssistantMemoryEntry entry)
        {
            if (eligibility.TryGetValue(entry.Id, out bool eligible)) return eligible;
            var m = entry.Metadata;
            bool scope = m != null && (m.Scope.Kind == AssistantMemoryScopeKind.Profile
                    && m.Kind is not (AssistantMemoryKind.ProjectKnowledge or AssistantMemoryKind.Persona)
                || project != null && m.Scope.Kind == AssistantMemoryScopeKind.Project
                    && (m.Kind == AssistantMemoryKind.ProjectKnowledge || m.Kind == AssistantMemoryKind.EpisodeSummary && !m.Events.IsEmpty)
                    && m.Scope.ProjectReference == project.Project.Reference
                    && m.Confirmation == AssistantMemoryConfirmation.Explicit && entry.Provenance?.SourceKind == "desktop_editor");
            eligible = m == null || m.InvalidatedAtUtc == null && scope
                && m.Confirmation != AssistantMemoryConfirmation.Pending
                && (m.Kind != AssistantMemoryKind.EpisodeSummary || m.Confirmation == AssistantMemoryConfirmation.Explicit)
                && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > asOfUtc)
                && (m.Events.IsEmpty || episodeCurrent?.Invoke(entry) == true)
                && m.Dependencies.All(r => Eligible(entries[r.Id]));
            eligibility[entry.Id] = eligible; return eligible;
        }
        return document.Entries.Where(Eligible).OrderBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
    }
    // Mutation and transitive invalidation are committed by one WriteDocument.
    // The old summary prose is removed, while references remain as an audit fact.
    internal static void InvalidateDependents(AssistantMemoryDocument document, string sourceId, DateTimeOffset now)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal) { sourceId };
        bool again;
        do
        {
            again = false;
            for (int index = 0; index < document.Entries.Count; index++)
            {
                var entry = document.Entries[index]; var m = entry.Metadata;
                if (m == null || m.InvalidatedAtUtc != null || !m.Dependencies.Any(r => changed.Contains(r.Id))) continue;
                document.Entries[index] = entry with { Text = "", UpdatedAt = now, Metadata = m with
                { Version = checked(m.Version + 1), Replaces = Reference(entry), InvalidatedAtUtc = now } };
                changed.Add(entry.Id); again = true;
            }
        } while (again);
    }
}
