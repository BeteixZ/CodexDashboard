using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codex.Control;

namespace CodexDashboard;

internal sealed record OwnedProjectDirectoryProof(Guid Epoch, DateTimeOffset ValidUntil,
    ImmutableDictionary<string, ProjectDirectoryEvidence> Directories);

internal static class OwnedProjectDirectoryProbe
{
    internal static ProcessStartInfo StartInfo(ConfiguredOwnedProfile profile, string requestId)
    {
        var config = profile.Configuration;
        if (config.Connection.Backend != OwnedBackend.SshStdio || config.ExpectedPlatformOs != "linux" || profile.Launch == null)
            throw new InvalidOperationException("remote_owned_project_probe_unavailable");
        string alias = HostIdentity.DisplayName(config.Connection.Endpoint.HostId);
        if (!HostIdentity.ValidAlias(alias)) throw new InvalidOperationException("invalid_ssh_alias");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        string command = "python3 - --home " + Quote(config.ExpectedCodexHome) + " --request-id " + Quote(requestId)
            + string.Concat(config.Projects.Values.Distinct(StringComparer.Ordinal).Select(path => " --project-directory " + Quote(path)));
        var start = new ProcessStartInfo(profile.Launch.Executable)
        {
            WorkingDirectory = profile.Launch.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5", "--", alias, command })
            start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task<string> ReadAsync(ConfiguredOwnedProfile profile, string requestId, CancellationToken ct)
    {
        if (profile.Configuration.Connection.Synthetic) throw new InvalidOperationException("synthetic_probe_requires_injection");
        string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "collector.py"), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(18));
        using var process = new Process { StartInfo = StartInfo(profile, requestId) };
        if (!process.Start()) throw new IOException("project_probe_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string result = await output; await error;
            if (process.ExitCode != 0 || result.Length > 1024 * 1024) throw new IOException("project_probe_failed");
            return result;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static OwnedProjectDirectoryProof Parse(string json, ConfiguredOwnedProfile profile, string requestId,
        Guid epoch, DateTimeOffset started, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("kind").GetString() != "project-directory-snapshot/v1"
            || root.GetProperty("requestId").GetString() != requestId) throw new InvalidDataException("project_probe_response_mismatch");
        var observed = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(root.GetProperty("observedAt").GetDouble() * 1000)));
        if (observed < started.AddSeconds(-30) || observed > now.AddSeconds(30)) throw new InvalidDataException("project_probe_time_mismatch");
        var expected = profile.Configuration.Projects.Values.ToHashSet(StringComparer.Ordinal);
        var proven = ImmutableDictionary.CreateBuilder<string, ProjectDirectoryEvidence>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("directories").EnumerateArray())
        {
            string path = item.GetProperty("requestedDirectory").GetString()!;
            if (!expected.Remove(path)) throw new InvalidDataException("project_probe_directory_mismatch");
            string? status = item.GetProperty("status").GetString();
            var value = item.GetProperty("evidence");
            if (status == "unavailable" && value.ValueKind == JsonValueKind.Null) continue;
            var proof = value.Deserialize<ProjectDirectoryEvidence>(SharedJson.Options);
            if (status != "observed" || proof is not { Version: 1, Kind: "linux-directory/v1" } || proof.RequestedDirectory != path)
                throw new InvalidDataException("project_probe_evidence_invalid");
            _ = new ProjectMemoryIdentity(profile.Configuration.Connection.Endpoint.HostId, "assistant-owned",
                profile.Configuration.Connection.Endpoint.ProfileId, proof.CanonicalDirectory, proof.SourceIdentity, proof.DirectoryIdentity).Reference;
            proven.Add(path, proof);
        }
        if (expected.Count != 0) throw new InvalidDataException("project_probe_directories_missing");
        return new(epoch, started.AddMinutes(2), proven.ToImmutable());
    }
}

internal sealed partial class OwnedControlRuntime
{
    private readonly System.Collections.Generic.Dictionary<string, OwnedProjectDirectoryProof> projectProofs = new(StringComparer.Ordinal);
    internal Func<ConfiguredOwnedProfile, string, CancellationToken, Task<string>> ProjectProbe { get; set; } = OwnedProjectDirectoryProbe.ReadAsync;

    internal async Task<bool> RefreshProjectProofAsync(string configurationId, Func<bool> stillCurrent, CancellationToken ct,
        Assistant.Core.TaskReference? exactTask = null)
    {
        using var activity = Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        if (!openGates.TryGetValue(configurationId, out var gate)) return false;
        await gate.WaitAsync(linked.Token);
        try
        {
            var session = LoadedSession(configurationId);
            if (!Active || !stillCurrent() || session == null || session.Profile.Configuration.Connection.Backend != OwnedBackend.SshStdio
                || session.Profile.Configuration.ExpectedPlatformOs != "linux" || session.Client.ProjectSourceIdentity == null
                || session.Client.CapabilityFailure("thread/start") != null) return false;
            if (exactTask != null)
            {
                var task = DependencyTarget(exactTask);
                if (task == null || task.ConfigurationId != configurationId || task.Handle.Access != OwnedTaskAccess.CreatedThisSession
                    || task.Snapshot.CurrentTurnId != exactTask.TurnId) return false;
                if (task.ObservedAt is not { } observed || observed.AddMinutes(2) <= clock())
                {
                    var read = await session.Client.ReconcileAsync(task.Handle, linked.Token);
                    Observed(session, task.Handle);
                    if (read.Task.CurrentTurnId != exactTask.TurnId || !stillCurrent()) return false;
                }
                if (ProjectProof(configurationId, task.Snapshot.Cwd) != null) return true;
            }
            lock (sync) projectProofs.Remove(configurationId);
            var epoch = session.Client.ConnectionEpoch; var started = clock(); string requestId = Guid.NewGuid().ToString("N");
            string json = await ProjectProbe(session.Profile, requestId, linked.Token);
            var proof = OwnedProjectDirectoryProbe.Parse(json, session.Profile, requestId, epoch, started, clock());
            if (proof.Directories.Values.Any(value => value.SourceIdentity != session.Client.ProjectSourceIdentity))
                throw new InvalidDataException("project_probe_changed_app_server_source");
            lock (sync)
            {
                if (!Active || !stillCurrent() || session != LoadedSession(configurationId) || session.Client.ConnectionEpoch != epoch
                    || session.Client.CapabilityFailure("thread/start") != null || proof.ValidUntil <= clock()) return false;
                projectProofs[configurationId] = proof;
                return proof.Directories.Count > 0;
            }
        }
        finally { gate.Release(); }
    }

    internal (ProjectDirectoryEvidence Evidence, DateTimeOffset ValidUntil, Guid Epoch)? ProjectProof(string configurationId, string cwd)
    {
        lock (sync)
        {
            var session = LoadedSession(configurationId);
            if (!Active || session == null || !projectProofs.TryGetValue(configurationId, out var proof)
                || proof.ValidUntil <= clock() || proof.Epoch != session.Client.ConnectionEpoch
                || session.Client.CapabilityFailure("thread/start") != null || !proof.Directories.TryGetValue(cwd, out var value)) return null;
            return (value, proof.ValidUntil, proof.Epoch);
        }
    }
}
