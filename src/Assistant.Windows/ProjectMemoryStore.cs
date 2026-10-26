using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace CodexDashboard;

internal sealed class ProjectMemorySavedNotificationException(AssistantMemoryEntry entry,
    AssistantMemoryCommittedNotificationException inner) : IOException("Project memory was saved, but a change observer failed.", inner)
{
    internal AssistantMemoryEntry SavedEntry { get; } = entry;
}

internal sealed partial class AssistantMemoryStore
{
    private static string ProjectMemoryScopeDescription(string? reference)
    {
        try
        {
            var project = ProjectMemoryProof.ParseReference(reference ?? "");
            return "指定项目记忆；不代表操作授权\n主机：" + HostIdentity.DisplayName(project.HostId)
                + "\n后端：" + project.Backend + "；配置：" + (project.ProfileId ?? "默认")
                + "\n规范目录：" + project.CanonicalDirectory;
        }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
        { return "历史项目范围尚未核对（当前未开放项目检索）；不代表操作授权"; }
    }
    internal ProjectMemoryEditorSnapshot CaptureProjectEditor(ProjectMemoryTarget target,
        IProjectMemoryResolver resolver, string? entryId = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow; var current = VerifyProjectTarget(target, resolver, now);
            var doc = ReadDocument(); var capture = CaptureDocument(doc, now);
            var entries = doc.Entries.Where(e => e.Metadata?.Kind == AssistantMemoryKind.ProjectKnowledge
                && e.Metadata.Scope.ProjectReference == current.Project.Reference).OrderBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
            var entry = entryId == null ? null : (entries.SingleOrDefault(e => e.Id == entryId)
                ?? throw new InvalidOperationException("project_memory_target_changed"));
            if (doc.Profile != Profile) throw new InvalidOperationException("project_memory_profile_changed");
            return new(doc.Profile, capture.Stamp, current, entries, entry);
        }
    }
    internal AssistantMemoryEntry SaveProjectFromEditor(ProjectMemoryEditorSnapshot snapshot, string text,
        IProjectMemoryResolver resolver)
    {
        lock (gate)
        {
            text = ValidateText(text); var now = DateTimeOffset.UtcNow;
            _ = VerifyProjectTarget(snapshot.Target, resolver, now);
            var doc = ReadDocument();
            if (doc.Profile != snapshot.Profile || doc.Profile != Profile || CaptureDocument(doc, now).Stamp != snapshot.Stamp)
                throw new InvalidOperationException("project_memory_profile_or_revision_changed");
            var scope = new AssistantMemoryScope(AssistantMemoryScopeKind.Project, snapshot.Target.Project.Reference);
            AssistantMemoryEntry saved;
            if (snapshot.Entry is { } prior)
            {
                var existing = doc.Entries.SingleOrDefault(e => e.Id == prior.Id);
                if (existing != prior || existing.Metadata is not { Kind: AssistantMemoryKind.ProjectKnowledge } metadata
                    || metadata.Scope != scope || metadata.InvalidatedAtUtc != null)
                    throw new InvalidOperationException("project_memory_target_changed");
                if (existing.Text == text) return existing;
                saved = existing with { Text = text, UpdatedAt = now, Provenance = new("desktop_editor", doc.Profile, now),
                    Metadata = metadata with { Version = checked(metadata.Version + 1), Replaces = AssistantMemoryLayers.Reference(existing),
                        Confirmation = AssistantMemoryConfirmation.Explicit } };
                doc.Entries[doc.Entries.IndexOf(existing)] = saved;
                AssistantMemoryRevocations.Add(doc, existing, "corrected", now);
                AssistantMemoryRevocations.InvalidateDependents(doc, existing.Id, now);
            }
            else
            {
                var same = doc.Entries.SingleOrDefault(e => e.Text == text && e.Metadata?.Kind == AssistantMemoryKind.ProjectKnowledge
                    && e.Metadata.Scope == scope && e.Metadata.Confirmation == AssistantMemoryConfirmation.Explicit
                    && e.Metadata.ExpiresAtUtc == null && e.Metadata.InvalidatedAtUtc == null && e.Provenance?.SourceKind == "desktop_editor");
                if (same != null) return same;
                string id; do { id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(); }
                while (doc.Entries.Any(e => e.Id == id) || doc.Revocations.Any(r => r.EntryId == id));
                saved = new(id, text, now, now, Provenance: new("desktop_editor", doc.Profile, now),
                    Metadata: new(AssistantMemoryKind.ProjectKnowledge, scope, 1, DerivedFrom: []));
                doc.Entries.Add(saved);
            }
            // Re-check the latest collected evidence before the atomic, protected write.
            _ = VerifyProjectTarget(snapshot.Target, resolver, DateTimeOffset.UtcNow);
            doc.FormatVersion = Math.Max(doc.FormatVersion, snapshot.Target.Project.HostId == "local" ? 3 : snapshot.Target.Project.Backend == "assistant-owned" ? 7 : 6);
            try { WriteDocument(doc); }
            catch (AssistantMemoryCommittedNotificationException error) when (error.Profile == doc.Profile && error.Revision == doc.Revision)
            { throw new ProjectMemorySavedNotificationException(saved, error); }
            return saved;
        }
    }
    private static ProjectMemoryTarget VerifyProjectTarget(ProjectMemoryTarget expected,
        IProjectMemoryResolver resolver, DateTimeOffset now)
    {
        var current = resolver.Resolve(expected.Task);
        if (current == null || !ProjectMemoryProof.SameEvidence(expected, current))
            throw new InvalidOperationException("project_memory_scope_changed");
        ProjectMemoryProof.Validate(current, now); return current;
    }
}
