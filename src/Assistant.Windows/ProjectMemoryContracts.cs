using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Core;

namespace CodexDashboard;

// Applicability only: these records never confer control, filesystem or tool authority.
internal sealed record ProjectMemoryIdentity(string HostId, string Backend, string? ProfileId, string CanonicalDirectory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RemoteSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DirectoryIdentity = null)
{
    internal string Reference => ProjectMemoryProof.Reference(this);
}
internal sealed record ProjectMemoryTarget(ProjectMemoryIdentity Project, TaskReference Task,
    string EvidenceSource, string EvidenceFingerprint, DateTimeOffset ValidUntilUtc);
internal interface IProjectMemoryResolver
{
    // Pure local observation: no network, ledger reentry, launch, write, or guessed target.
    ProjectMemoryTarget? Resolve(TaskReference exactTask);
    System.Threading.Tasks.Task<ProjectMemoryTarget?> PrepareAsync(TaskReference exactTask,
        System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(Resolve(exactTask));
}
internal sealed record ProjectMemoryEditorSnapshot(string Profile, string Stamp, ProjectMemoryTarget Target,
    ImmutableArray<AssistantMemoryEntry> Entries, AssistantMemoryEntry? Entry);

internal static class ProjectMemoryProof
{
    internal const string Provider = "assistant-project-memory";
    private const string Prefix = "local-project/v1/";
    internal const string RemotePrefix = "remote-project/v1/";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8
    };
    internal static string Reference(ProjectMemoryIdentity project)
    {
        _ = new HostEndpointIdentity(project.HostId, project.ProfileId);
        bool local = project.HostId == "local";
        if (project.Backend is not ("desktop-existing" or "assistant-owned")
            || project.Backend == "desktop-existing" && project.ProfileId != null
            || project.ProfileId is { } profile && (profile.Length is < 1 or > 128 || profile.Any(char.IsControl))
            || project.CanonicalDirectory.Any(char.IsControl)
            || local && (!project.CanonicalDirectory.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                || !project.CanonicalDirectory.Contains(@"}\", StringComparison.Ordinal)
                || project.RemoteSource != null || project.DirectoryIdentity != null)
            || !local && (!CanonicalLinuxPath(project.CanonicalDirectory)
                || !System.Text.RegularExpressions.Regex.IsMatch(project.RemoteSource ?? "", "\\A[0-9a-f]{64}\\z")
                || !System.Text.RegularExpressions.Regex.IsMatch(project.DirectoryIdentity ?? "", "\\A[0-9a-f]+:[0-9a-f]+\\z")))
            throw new ArgumentException("project_scope_invalid");
        string value = (local ? Prefix : RemotePrefix) + JsonSerializer.Serialize(project, Json);
        if (value.Length > 512) throw new ArgumentException("project_scope_too_long");
        return value;
    }
    internal static ProjectMemoryIdentity ParseReference(string reference)
    {
        string prefix = reference.StartsWith(RemotePrefix, StringComparison.Ordinal) ? RemotePrefix : Prefix;
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("project_scope_unknown");
        var project = JsonSerializer.Deserialize<ProjectMemoryIdentity>(reference[prefix.Length..], Json)
            ?? throw new ArgumentException("project_scope_invalid");
        if (Reference(project) != reference) throw new ArgumentException("project_scope_noncanonical");
        return project;
    }
    private static bool CanonicalLinuxPath(string path) => path.StartsWith('/') && !path.Contains('\\')
        && (path == "/" || !path.EndsWith('/') && !path.Split('/').Skip(1).Any(p => p is "" or "." or ".."));
    internal static string Fingerprint(ProjectMemoryIdentity project, TaskReference task, string source)
        => WeChatText.Hash(project.Reference + "\n" + JsonSerializer.Serialize(task, Json) + "\n" + source);
    internal static bool SameEvidence(ProjectMemoryTarget left, ProjectMemoryTarget right)
        => left.Project == right.Project && left.Task == right.Task && left.EvidenceSource == right.EvidenceSource
            && left.EvidenceFingerprint == right.EvidenceFingerprint;
    internal static void Validate(ProjectMemoryTarget target, DateTimeOffset now)
    {
        _ = target.Project.Reference;
        _ = new CanonicalTaskIdentity(target.Task.HostId, target.Task.ThreadId, target.Task.ProfileId);
        if (target.Task.HostId != target.Project.HostId || target.Task.ProfileId != target.Project.ProfileId
            || string.IsNullOrWhiteSpace(target.Task.TurnId) || target.Task.TurnId.Length > 128 || target.Task.TurnId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(target.EvidenceSource) || target.EvidenceSource.Length > 128 || target.EvidenceSource.Any(char.IsControl)
            || target.ValidUntilUtc.Offset != TimeSpan.Zero || target.ValidUntilUtc <= now
            || target.EvidenceFingerprint != Fingerprint(target.Project, target.Task, target.EvidenceSource))
            throw new ArgumentException("project_evidence_invalid");
        if (JsonSerializer.Serialize(target.Task, Json).Length + 5 > 512) throw new ArgumentException("project_task_too_long");
    }
    internal static ContextDependency Dependency(ProjectMemoryTarget target)
        => new(Provider, target.Project.Reference, 1, target.EvidenceFingerprint, target.ValidUntilUtc,
            [new("task:" + JsonSerializer.Serialize(target.Task, Json), 1), new("source:" + target.EvidenceSource, 1)]);
    internal static ProjectMemoryTarget? Read(OutputDependencies? dependencies)
    {
        var contexts = dependencies?.Contexts.Where(c => c.Provider == Provider).ToArray() ?? [];
        if (contexts.Length == 0) return null;
        if (contexts.Length != 1) throw new ArgumentException("project_dependencies_ambiguous");
        var proof = contexts[0];
        if (proof.Revision != 1 || proof.Items.IsDefault || proof.Items.Length != 2 || proof.ValidUntilUtc == null
            || proof.Items.Any(i => i.Version != 1)) throw new ArgumentException("project_dependency_invalid");
        var taskItems = proof.Items.Where(i => i.Id.StartsWith("task:", StringComparison.Ordinal)).ToArray();
        var sourceItems = proof.Items.Where(i => i.Id.StartsWith("source:", StringComparison.Ordinal)).ToArray();
        if (taskItems.Length != 1 || sourceItems.Length != 1) throw new ArgumentException("project_dependency_invalid");
        var taskItem = taskItems[0]; var sourceItem = sourceItems[0];
        var task = JsonSerializer.Deserialize<TaskReference>(taskItem.Id[5..], Json)
            ?? throw new ArgumentException("project_task_invalid");
        var target = new ProjectMemoryTarget(ParseReference(proof.ScopeKey), task, sourceItem.Id[7..], proof.Fingerprint, proof.ValidUntilUtc.Value);
        if (Dependency(target) is { } canonical && !OutputDependencies.Equivalent(
            new(OutputDependencyMode.Bound, [canonical]), new(OutputDependencyMode.Bound, [proof])))
            throw new ArgumentException("project_dependency_noncanonical");
        return target;
    }
}
