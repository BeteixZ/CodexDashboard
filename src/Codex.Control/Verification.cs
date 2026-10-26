using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Frozen;

namespace Codex.Control;

public static partial class OwnedVerification
{
    // Audited from generated 0.147.0 stable schema. This is not a server-advertised capability list.
    public const string SchemaVersion = "0.147.0";
    internal static bool AgentVersionMatches(string userAgent, string expectedVersion)
        => Regex.Match(userAgent, @"\A[^/\r\n]+/(?<version>\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?)(?:\s|$)")
            .Groups["version"].Value == expectedVersion;
    public static readonly IReadOnlySet<string> SupportedMethods = new[]
    {
        "thread/start", "thread/read", "thread/resume", "turn/start", "turn/steer", "turn/interrupt",
        "thread/name/set", "thread/archive", "thread/unarchive",
        "item/commandExecution/requestApproval", "item/fileChange/requestApproval"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static string ConfigurationDigest(OwnedServerConfiguration configuration)
    {
        string value = JsonSerializer.Serialize(new
        {
            endpoint = configuration.Connection.Endpoint.Key, backend = configuration.Connection.Backend.ToString(),
            version = configuration.ExpectedVersion, platform = configuration.ExpectedPlatformOs, home = configuration.ExpectedCodexHome,
            projects = configuration.Projects.OrderBy(x => x.Key, StringComparer.Ordinal),
            models = configuration.Models.Order(StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static string? CapabilityFailure(OwnedServerConfiguration configuration, string method, DateTimeOffset now)
    {
        if (!SupportedMethods.Contains(method)) return "unsupported_schema_method";
        var evidence = configuration.Verification;
        if (evidence == null) return "runtime_verification_required";
        if (evidence.Connection != configuration.Connection
            || evidence.Version != configuration.ExpectedVersion
            || evidence.PlatformOs != configuration.ExpectedPlatformOs
            || evidence.ConfigurationDigest != ConfigurationDigest(configuration))
            return "verification_scope_mismatch";
        if (evidence.VerifiedAt > now || evidence.ExpiresAt <= now || evidence.ExpiresAt <= evidence.VerifiedAt
            || string.IsNullOrWhiteSpace(evidence.EvidenceReference))
            return "verification_expired_or_invalid";
        if (!configuration.Connection.Synthetic && evidence.Kind != VerificationKind.RuntimeAcceptance)
            return "synthetic_evidence_cannot_enable_runtime";
        return evidence.Methods.Contains(method) ? null : "method_not_runtime_verified";
    }

    internal static void Validate(OwnedServerConfiguration configuration)
    {
        if (configuration.Connection.Backend is not (OwnedBackend.LocalStdio or OwnedBackend.SshStdio))
            throw Error("owned_backend_required");
        if (configuration.ExpectedVersion != SchemaVersion) throw Error("unsupported_schema_version");
        if (configuration.ExpectedPlatformOs is not ("windows" or "linux" or "macos")) throw Error("unsupported_platform");
        if (configuration.Connection.Backend == OwnedBackend.LocalStdio && configuration.Connection.Endpoint.HostId != "local"
            || configuration.Connection.Backend == OwnedBackend.SshStdio && configuration.Connection.Endpoint.HostId == "local")
            throw Error("host_backend_mismatch");
        RequirePath(configuration.ExpectedCodexHome, configuration.ExpectedPlatformOs);
        if (configuration.Projects.Count == 0 || configuration.Models.Count == 0) throw Error("explicit_project_model_required");
        foreach (var project in configuration.Projects)
        {
            RequireText(project.Key, 256);
            RequirePath(project.Value, configuration.ExpectedPlatformOs);
        }
        foreach (string model in configuration.Models) RequireText(model, 256);
        if (configuration.RequestTimeout < TimeSpan.FromMilliseconds(50) || configuration.RequestTimeout > TimeSpan.FromMinutes(2)
            || configuration.ApprovalLifetime <= TimeSpan.Zero || configuration.ApprovalLifetime > TimeSpan.FromMinutes(30))
            throw Error("invalid_timeout");
    }

    internal static void RequirePath(string path, string platform)
    {
        RequireText(path, 4096);
        bool absolute = platform == "windows" ? WindowsAbsolute().IsMatch(path) : path.StartsWith('/');
        if (!absolute || path.Split('/', '\\').Any(x => x is "." or "..")) throw Error("absolute_normalized_path_required");
    }
    internal static bool SamePath(string a, string b, string platform) => string.Equals(a, b,
        platform == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static void RequireText(string value, int maximum = 16384)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw Error("invalid_text");
    }
    internal static ControlException Error(string code) => new(code, code);
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsAbsolute();
}
