using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexDashboard;

namespace Codex.Control;

/// <summary>Host-only launch configuration. Arguments are never composed from a model's command text.</summary>
public sealed record OwnedProcessLaunch(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, bool ReadProjectSource = false);

public static partial class OwnedProcessConnection
{
    public static OwnedProcessLaunch Local(string absoluteCodexExecutable, string workingDirectory, string codexHome)
    {
        RequireLocalPath(absoluteCodexExecutable); RequireLocalPath(workingDirectory); RequireLocalPath(codexHome);
        return new(absoluteCodexExecutable, ["app-server", "--listen", "stdio://"], workingDirectory,
            new Dictionary<string, string> { ["CODEX_HOME"] = codexHome });
    }

    /// <summary>SSH remote command arguments still traverse a POSIX shell, so quote every value independently.</summary>
    public static OwnedProcessLaunch Ssh(string absoluteSshExecutable, HostEndpointIdentity endpoint,
        string localWorkingDirectory, string remoteCodexExecutable, string remoteCodexHome, bool projectIdentity = false)
    {
        RequireLocalPath(absoluteSshExecutable); RequireLocalPath(localWorkingDirectory);
        string prefix = "remote-ssh-discovered:";
        if (!endpoint.HostId.StartsWith(prefix, StringComparison.Ordinal)) throw OwnedVerification.Error("ssh_endpoint_required");
        string alias = endpoint.HostId[prefix.Length..];
        if (!SafeAlias().IsMatch(alias)) throw OwnedVerification.Error("invalid_ssh_alias");
        OwnedVerification.RequirePath(remoteCodexExecutable, "linux"); OwnedVerification.RequirePath(remoteCodexHome, "linux");
        string remote = "exec env CODEX_HOME=" + PosixQuote(remoteCodexHome) + " " + PosixQuote(remoteCodexExecutable)
            + " app-server --listen stdio://";
        if (projectIdentity) remote = "exec python3 -c " + PosixQuote(OwnedProjectSourceBootstrap.Script)
            + " " + PosixQuote(remoteCodexHome) + " " + PosixQuote(remoteCodexExecutable);
        return new(absoluteSshExecutable,
            ["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "--", alias, remote],
            localWorkingDirectory, new Dictionary<string, string>(), projectIdentity);
    }

    public static Task<IJsonRpcConnection> StartAsync(ConnectionIdentity identity, OwnedProcessLaunch launch, CancellationToken ct = default)
    {
        if (identity.Synthetic) throw OwnedVerification.Error("synthetic_identity_cannot_launch_runtime_process");
        return StartCoreAsync(identity, launch, ct);
    }

    internal static Task<IJsonRpcConnection> StartSyntheticFixtureAsync(ConnectionIdentity identity, OwnedProcessLaunch launch, CancellationToken ct = default)
    {
        if (!identity.Synthetic) throw OwnedVerification.Error("synthetic_fixture_identity_required");
        return StartCoreAsync(identity, launch, ct);
    }

    private static async Task<IJsonRpcConnection> StartCoreAsync(ConnectionIdentity identity, OwnedProcessLaunch launch, CancellationToken ct)
    {
        if (identity.Backend is not (OwnedBackend.LocalStdio or OwnedBackend.SshStdio))
            throw OwnedVerification.Error("owned_backend_required");
        ct.ThrowIfCancellationRequested();
        RequireLocalPath(launch.Executable); RequireLocalPath(launch.WorkingDirectory);
        var start = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = launch.WorkingDirectory
        };
        foreach (string argument in launch.Arguments)
        {
            if (argument.Contains('\0')) throw OwnedVerification.Error("invalid_process_argument");
            start.ArgumentList.Add(argument);
        }
        foreach (var pair in launch.Environment)
        {
            if (pair.Key != "CODEX_HOME") throw OwnedVerification.Error("unapproved_environment_override");
            start.Environment[pair.Key] = pair.Value;
        }
        var process = new Process { StartInfo = start };
        if (!process.Start()) { process.Dispose(); throw new IOException("owned_process_start_failed"); }
        // Drain without retaining or logging authentication paths/tokens or arbitrary child diagnostics.
        var stderr = DrainAsync(process.StandardError);
        async ValueTask Release()
        {
            try
            {
                if (!process.HasExited)
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
            }
            finally { process.Dispose(); }
        }
        try
        {
            string? source = launch.ReadProjectSource ? await OwnedProjectSourceBootstrap.ReadAsync(process.StandardOutput.BaseStream, ct) : null;
            return new JsonRpcConnection(identity, process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream, Release, projectSourceIdentity: source);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await Release(); throw; }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void RequireLocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Contains('\0')) throw OwnedVerification.Error("absolute_local_path_required");
    }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeAlias();
}
