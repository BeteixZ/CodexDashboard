using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexDashboard;

namespace Codex.Control;

/// <summary>Host-supplied scope for the original shared service. This grants no ownership of its tasks.</summary>
public sealed record SharedExistingConfiguration
{
    public required ConnectionIdentity Connection { get; init; }
    public required string ExpectedVersion { get; init; }
    public required string ExpectedPlatformOs { get; init; }
    public required string ExpectedCodexHome { get; init; }
    public required ImmutableHashSet<string> Directories { get; init; }
    public RuntimeVerification? Verification { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string ConfigurationDigest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            endpoint = Connection.Endpoint.Key, backend = Connection.Backend.ToString(),
            version = ExpectedVersion, platform = ExpectedPlatformOs, home = ExpectedCodexHome,
            directories = Directories.Order(StringComparer.Ordinal)
        }))));

    public void Validate()
    {
        if (Connection.Backend != OwnedBackend.SharedRemoteSocket || Connection.Endpoint.HostId == "local")
            throw OwnedVerification.Error("shared_remote_backend_required");
        if (ExpectedVersion != OwnedVerification.SchemaVersion) throw OwnedVerification.Error("unsupported_schema_version");
        if (ExpectedPlatformOs is not ("linux" or "macos")) throw OwnedVerification.Error("unsupported_platform");
        OwnedVerification.RequirePath(ExpectedCodexHome, ExpectedPlatformOs);
        if (Directories.Count == 0) throw OwnedVerification.Error("explicit_directory_required");
        foreach (var directory in Directories) OwnedVerification.RequirePath(directory, ExpectedPlatformOs);
        if (RequestTimeout < TimeSpan.FromMilliseconds(50) || RequestTimeout > TimeSpan.FromMinutes(2))
            throw OwnedVerification.Error("invalid_timeout");
    }

    public string? CapabilityFailure(string method, DateTimeOffset now)
    {
        if (method is not ("turn/steer" or "turn/interrupt")) return "shared_method_not_supported";
        var evidence = Verification;
        if (evidence == null) return "runtime_verification_required";
        if (evidence.Connection != Connection || evidence.Version != ExpectedVersion
            || evidence.PlatformOs != ExpectedPlatformOs || evidence.ConfigurationDigest != ConfigurationDigest())
            return "verification_scope_mismatch";
        if (evidence.VerifiedAt > now || evidence.ExpiresAt <= now || evidence.ExpiresAt <= evidence.VerifiedAt
            || string.IsNullOrWhiteSpace(evidence.EvidenceReference)) return "verification_expired_or_invalid";
        if (!Connection.Synthetic && evidence.Kind != VerificationKind.RuntimeAcceptance)
            return "synthetic_evidence_cannot_enable_runtime";
        return evidence.Methods.Contains(method) ? null : "method_not_runtime_verified";
    }
}

/// <summary>Exact user-selected turn and directory; no owned-task handle is created or accepted.</summary>
public sealed record ExistingTurnTarget(CanonicalTaskIdentity Task, string Cwd, string TurnId);
public sealed record ExistingTurnRead(ExistingTurnTarget Target, string? Status, DateTimeOffset ObservedAt, Guid ConnectionEpoch);
