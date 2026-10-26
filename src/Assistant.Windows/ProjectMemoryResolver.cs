using System;
using System.Linq;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed class ProjectMemoryResolver : IProjectMemoryResolver
{
    private readonly ICodexGateway gateway;
    private readonly Func<OwnedControlRuntime?> owned;
    private readonly IWorkspacePathResolver paths;
    private readonly Func<DateTimeOffset> clock;
    private readonly IProjectMemoryPathVerifier pathVerifier;
    private readonly Func<string, bool>? remoteConfigured;
    internal ProjectMemoryResolver(ICodexGateway gateway, Func<OwnedControlRuntime?> owned,
        IWorkspacePathResolver? paths = null, Func<DateTimeOffset>? clock = null,
        IProjectMemoryPathVerifier? pathVerifier = null, Func<string, bool>? remoteConfigured = null)
    {
        this.gateway = gateway; this.owned = owned; this.paths = paths ?? new WorkspacePathResolver();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.pathVerifier = pathVerifier ?? new ProjectMemoryPathVerifier();
        this.remoteConfigured = remoteConfigured;
    }

    public async System.Threading.Tasks.Task<ProjectMemoryTarget?> PrepareAsync(TaskReference exactTask,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = Resolve(exactTask);
        if (current != null || exactTask.HostId == "local" || string.IsNullOrWhiteSpace(exactTask.TurnId)) return current;
        var identity = new CanonicalTaskIdentity(exactTask.HostId, exactTask.ThreadId, exactTask.ProfileId);
        if (gateway.ReadTask(identity).Value != null) return null;
        var runtime = owned();
        var task = runtime?.DependencyTarget(exactTask);
        if (task == null || runtime?.Active != true) return null;
        try
        {
            await runtime.RefreshProjectProofAsync(task.ConfigurationId, () => owned() == runtime && runtime.Active, ct, exactTask);
            return Resolve(exactTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return null; }
    }

    public ProjectMemoryTarget? Resolve(TaskReference exactTask)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exactTask.TurnId)) return null;
            var now = clock().ToUniversalTime();
            var identity = new CanonicalTaskIdentity(exactTask.HostId, exactTask.ThreadId, exactTask.ProfileId);
            var desktop = gateway.ReadTask(identity).Value;
            var runtime = owned();
            var ownedMatches = runtime?.Tasks().Where(t => t.Handle.Identity == identity).Take(2).ToArray() ?? [];
            if (ownedMatches.Length > 1 || desktop != null && ownedMatches.Length > 0) return null;
            string cwd, backend, source; DateTimeOffset observed;
            if (desktop != null)
            {
                if (exactTask.ProfileId != null || !desktop.Fresh || desktop.Record.Disconnected
                    || desktop.Record.TurnId != exactTask.TurnId || desktop.ObservedAt is not { } at) return null;
                cwd = desktop.Record.Cwd; backend = "desktop-existing"; source = "collector-local-directory/v1"; observed = at;
            }
            else if (ownedMatches.Length == 1 && runtime?.Active == true)
            {
                var task = ownedMatches[0]; var profile = runtime.Profile(task.ConfigurationId);
                var session = runtime.LoadedSession(task.ConfigurationId);
                // Existing capability proof is a conservative connection check only.
                // This code neither requests that capability nor calls the backend.
                if (session == null || profile == null || session.Client.CapabilityFailure("thread/start") != null
                    || task.Handle.Access != OwnedTaskAccess.CreatedThisSession || task.Snapshot.CurrentTurnId != exactTask.TurnId
                    || task.ObservedAt is not { } at
                    || !profile.Configuration.Projects.TryGetValue(task.Snapshot.ProjectId, out var configured)
                    || configured != task.Snapshot.Cwd) return null;
                cwd = task.Snapshot.Cwd; backend = "assistant-owned";
                source = "owned-config-directory/v1/" + task.ConfigurationId; observed = at;
                if (exactTask.HostId != "local")
                {
                    if (profile.Configuration.ExpectedPlatformOs != "linux" || at > now.AddSeconds(30) || at.AddMinutes(2) <= now
                        || runtime.ProjectProof(task.ConfigurationId, cwd) is not { } proof) return null;
                    var ownedProject = new ProjectMemoryIdentity(exactTask.HostId, backend, exactTask.ProfileId,
                        proof.Evidence.CanonicalDirectory, proof.Evidence.SourceIdentity, proof.Evidence.DirectoryIdentity);
                    string proofSource = "owned-linux-directory/v1/" + task.ConfigurationId + "/" + proof.Epoch.ToString("N");
                    var until = proof.ValidUntil < at.AddMinutes(2) ? proof.ValidUntil : at.AddMinutes(2);
                    var ownedTarget = new ProjectMemoryTarget(ownedProject, exactTask, proofSource,
                        ProjectMemoryProof.Fingerprint(ownedProject, exactTask, proofSource), until.ToUniversalTime());
                    ProjectMemoryProof.Validate(ownedTarget, now); return ownedTarget;
                }
                if (profile.Configuration.ExpectedPlatformOs != "windows") return null;
            }
            else return null;
            if (observed > now.AddSeconds(30) || observed.AddMinutes(2) <= now) return null;
            if (exactTask.HostId != "local")
            {
                if (remoteConfigured?.Invoke(exactTask.HostId) != true
                    || desktop?.Record.ProjectEvidence is not { Version: 1, Kind: "linux-directory/v1",
                    SourceIdentity: { Length: 64 }, CanonicalDirectory: { Length: > 0 }, DirectoryIdentity: { Length: > 0 } } proof
                    || proof.RequestedDirectory != cwd || exactTask.ProfileId != null) return null;
                var remoteProject = new ProjectMemoryIdentity(exactTask.HostId, backend, null, proof.CanonicalDirectory,
                    proof.SourceIdentity, proof.DirectoryIdentity);
                var remoteTarget = new ProjectMemoryTarget(remoteProject, exactTask, "collector-linux-directory/v1",
                    ProjectMemoryProof.Fingerprint(remoteProject, exactTask, "collector-linux-directory/v1"), observed.AddMinutes(2).ToUniversalTime());
                ProjectMemoryProof.Validate(remoteTarget, now); return remoteTarget;
            }
            var resolved = paths.Resolve(new("local", "windows", cwd));
            if (resolved == null || resolved.HostWide || resolved.HostId != "local" || resolved.Path == null) return null;
            if (!pathVerifier.IsCaseInsensitiveLocalDirectory(cwd, resolved.Path)) return null;
            var project = new ProjectMemoryIdentity("local", backend, exactTask.ProfileId, resolved.Path);
            var target = new ProjectMemoryTarget(project, exactTask, source,
                ProjectMemoryProof.Fingerprint(project, exactTask, source), observed.AddMinutes(2).ToUniversalTime());
            ProjectMemoryProof.Validate(target, now); return target;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException or ObjectDisposedException)
        { return null; }
    }
}
