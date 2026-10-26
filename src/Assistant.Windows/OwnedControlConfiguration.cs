using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.Control;

namespace CodexDashboard;

internal sealed record OwnedConfigurationProblem(string Code, string? ConfigurationId = null);
internal sealed record OwnedConfigurationLoadResult(ImmutableArray<ConfiguredOwnedProfile> Profiles,
    ImmutableArray<OwnedConfigurationProblem> Problems, bool FilePresent);

/// <summary>
/// Reads trusted host configuration and protected, explicitly installed acceptance evidence.
/// No path, launch argument, evidence, environment or owner is obtained from a model or message.
/// Loading never starts a process, connects to a host, creates a file, or performs acceptance.
/// </summary>
internal static class OwnedControlConfiguration
{
    internal const string FileName = "owned-control.json";
    private const int MaximumBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    internal static OwnedConfigurationLoadResult Load(string directory, string trustedAcceptanceDirectory,
        DateTimeOffset? now = null)
    {
        bool present = false;
        try
        {
            string root = LocalPath(directory), evidenceRoot = LocalPath(trustedAcceptanceDirectory);
            RequireNoRedirect(root); RequireNoRedirect(evidenceRoot);
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return new([], [], false);
            present = true;
            RequireNoRedirect(path);
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(ReadBounded(path), Json)
                ?? throw Invalid();
            if (document.Version != 1 || document.Profiles.Length > 16 || document.Profiles.Any(p => p == null)) throw Invalid();
            var profiles = ImmutableArray.CreateBuilder<ConfiguredOwnedProfile>();
            var problems = ImmutableArray.CreateBuilder<OwnedConfigurationProblem>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in document.Profiles)
            {
                var profile = ConvertProfile(source);
                if (!identifiers.Add(profile.Id) || !endpoints.Add(profile.Configuration.Connection.Endpoint.Key))
                    throw Invalid();
                if (profile.Mode == OwnedProfileMode.VerifiedControl)
                {
                    var evidence = ReadAcceptance(evidenceRoot, source.AcceptanceId, profile, now ?? DateTimeOffset.UtcNow);
                    if (evidence == null)
                    {
                        problems.Add(new("owned_runtime_acceptance_missing_or_invalid", profile.Id));
                        profile = profile with { Mode = OwnedProfileMode.Disabled };
                    }
                    else profile = profile with { Configuration = profile.Configuration with { Verification = evidence } };
                }
                else if (source.AcceptanceId != null)
                    throw Invalid();
                profiles.Add(profile);
            }
            return new(profiles.ToImmutable(), problems.ToImmutable(), true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException
            or IOException or UnauthorizedAccessException or CryptographicException or NotSupportedException)
        {
            // Never include JSON fragments, user paths, child diagnostics or protected plaintext in errors.
            return new([], [new("owned_configuration_invalid")], present);
        }
    }

    private static ConfiguredOwnedProfile ConvertProfile(ProfileDocument source)
    {
        Identifier(source.Id); Text(source.Label, 128); Identifier(source.Project);
        Text(source.Model, 256); Text(source.Profile, 256);
        if (source.AcceptanceId != null) Identifier(source.AcceptanceId);
        if (source.CliVersion != OwnedVerification.SchemaVersion
            || source.Approval is not ("on-request" or "untrusted")
            || source.Sandbox is not ("read-only" or "workspace-write")) throw Invalid();
        OwnedProfileMode mode = source.Mode switch
        {
            "disabled" => OwnedProfileMode.Disabled, "readOnly" => OwnedProfileMode.ReadOnly,
            "verifiedControl" => OwnedProfileMode.VerifiedControl, _ => throw Invalid()
        };
        var endpoint = new HostEndpointIdentity(source.Host, source.Profile);
        string home, cwd;
        OwnedProcessLaunch launch;
        OwnedBackend backend;
        if (source.Local != null && source.Ssh == null)
        {
            if (source.Host != "local" || source.Platform != "windows") throw Invalid();
            home = LocalPath(source.CodexHome); cwd = LocalPath(source.Cwd);
            launch = OwnedProcessConnection.Local(LocalPath(source.Local.Executable), cwd, home);
            backend = OwnedBackend.LocalStdio;
        }
        else if (source.Ssh != null && source.Local == null)
        {
            if (source.Host == "local" || source.Platform is not ("linux" or "macos")) throw Invalid();
            home = RemotePath(source.CodexHome); cwd = RemotePath(source.Cwd);
            launch = OwnedProcessConnection.Ssh(LocalPath(source.Ssh.Executable), endpoint,
                LocalPath(source.Ssh.LocalWorkingDirectory), RemotePath(source.Ssh.RemoteExecutable), home, source.Ssh.ProjectIdentity);
            backend = OwnedBackend.SshStdio;
        }
        else throw Invalid();
        var config = new OwnedServerConfiguration
        {
            // A config file can never designate itself synthetic or import replay-only acceptance.
            Connection = new(endpoint, backend, Synthetic: false),
            ExpectedVersion = source.CliVersion, ExpectedPlatformOs = source.Platform,
            ExpectedCodexHome = home, Projects = ImmutableDictionary<string, string>.Empty.Add(source.Project, cwd),
            Models = ImmutableHashSet.Create(StringComparer.Ordinal, source.Model)
        };
        return new(source.Id, source.Label, config,
            new(source.Project, cwd, source.Model, source.Approval, source.Sandbox), launch, mode);
    }

    private static RuntimeVerification? ReadAcceptance(string root, string? id, ConfiguredOwnedProfile profile, DateTimeOffset now)
    {
        if (id == null) return null;
        byte[]? plaintext = null;
        try
        {
            string path = AcceptancePath(root, id);
            RequireNoRedirect(path);
            if (!File.Exists(path)) return null;
            plaintext = new WindowsStateProtector().Unprotect(ReadBounded(path), AcceptancePurpose(root, id));
            var record = JsonSerializer.Deserialize<AcceptanceDocument>(plaintext, Json) ?? throw Invalid();
            var configuration = profile.Configuration;
            if (record.Version != 1 || record.Kind != "RuntimeAcceptance" || record.ConfigurationId != profile.Id
                || record.Host != configuration.Connection.Endpoint.HostId
                || record.Profile != configuration.Connection.Endpoint.ProfileId
                || record.Backend != configuration.Connection.Backend.ToString()
                || record.CliVersion != configuration.ExpectedVersion || record.Platform != configuration.ExpectedPlatformOs
                || record.ConfigurationDigest != OwnedVerification.ConfigurationDigest(configuration)
                || record.LaunchPolicyDigest != LaunchPolicyDigest(profile)
                || record.Methods.Length == 0 || record.Methods.Length > OwnedVerification.SupportedMethods.Count
                || record.Methods.Distinct(StringComparer.Ordinal).Count() != record.Methods.Length
                || record.Methods.Any(m => !OwnedVerification.SupportedMethods.Contains(m))
                || record.VerifiedAt > now || record.ExpiresAt <= now || record.ExpiresAt <= record.VerifiedAt
                || record.ExpiresAt - record.VerifiedAt > TimeSpan.FromDays(30))
                return null;
            Text(record.EvidenceReference, 1024);
            var evidence = new RuntimeVerification(configuration.Connection, record.CliVersion, record.Platform,
                record.ConfigurationDigest, record.Methods.ToImmutableHashSet(StringComparer.Ordinal),
                VerificationKind.RuntimeAcceptance, record.EvidenceReference, record.VerifiedAt, record.ExpiresAt);
            // Exercise the same scope/time/method gate as execution, not just a parallel interpretation.
            return record.Methods.All(m => OwnedVerification.CapabilityFailure(configuration with { Verification = evidence }, m, now) == null)
                ? evidence : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException
            or IOException or UnauthorizedAccessException or CryptographicException or NotSupportedException)
        { return null; }
        finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>Acceptance also binds the fixed launch, project policy and budgets omitted by the protocol digest.</summary>
    internal static string LaunchPolicyDigest(ConfiguredOwnedProfile profile)
    {
        var launch = profile.Launch ?? throw Invalid();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id,
            configuration = OwnedVerification.ConfigurationDigest(profile.Configuration),
            creation = profile.Creation,
            launch.Executable, arguments = launch.Arguments, launch.WorkingDirectory,
            environment = launch.Environment.OrderBy(p => p.Key, StringComparer.Ordinal),
            requestTimeout = profile.Configuration.RequestTimeout.Ticks,
            approvalLifetime = profile.Configuration.ApprovalLifetime.Ticks
        })));
    }
    internal static string AcceptancePath(string directory, string id)
    { Identifier(id); return Path.Combine(LocalPath(directory), id + ".owned-acceptance"); }
    internal static byte[] AcceptancePurpose(string directory, string id)
    {
        Identifier(id);
        return Encoding.UTF8.GetBytes("CodexAssistant.OwnedRuntimeAcceptance/v1/"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalPath(directory).ToUpperInvariant()))) + "/" + id);
    }
    internal static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumBytes) throw Invalid();
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Invalid();
        return bytes;
    }
    internal static string LocalPath(string value)
    {
        Text(value, 4096);
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':'
            || value[2] is not ('\\' or '/') || value[2..].Contains(':')
            || value.Split('\\', '/').Any(p => p is "." or "..")) throw Invalid();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    internal static string RemotePath(string value)
    {
        Text(value, 4096);
        if (!value.StartsWith('/') || value.Contains('\\') || value.Contains("//", StringComparison.Ordinal)
            || value.Split('/').Any(p => p is "." or "..")) throw Invalid();
        return value.Length == 1 ? value : value.TrimEnd('/');
    }
    internal static void RequireNoRedirect(string path)
    {
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw Invalid();
    }
    internal static void Identifier(string value)
    {
        Text(value, 64);
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Invalid();
    }
    internal static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("invalid_owned_control_configuration");

    private sealed record ConfigurationDocument(int Version, ProfileDocument[] Profiles);
    private sealed record ProfileDocument(string Id, string Label, string Host, string Profile, string CodexHome,
        string Project, string Cwd, string Model, string Approval, string Sandbox, string CliVersion, string Platform,
        LocalLaunchDocument? Local = null, SshLaunchDocument? Ssh = null, string Mode = "disabled", string? AcceptanceId = null);
    private sealed record LocalLaunchDocument(string Executable);
    private sealed record SshLaunchDocument(string Executable, string LocalWorkingDirectory, string RemoteExecutable, bool ProjectIdentity = false);
    private sealed record AcceptanceDocument(int Version, string Kind, string ConfigurationId, string Host, string Profile,
        string Backend, string CliVersion, string Platform, string ConfigurationDigest, string LaunchPolicyDigest,
        string[] Methods, string EvidenceReference, DateTimeOffset VerifiedAt, DateTimeOffset ExpiresAt);
}
