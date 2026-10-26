using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codex.Control;

namespace CodexDashboard;

internal sealed record ConfiguredExistingProfile(string Id, string Label, SharedExistingConfiguration Configuration, bool Enabled = false, bool ReadOnly = false);
internal sealed record ExistingConfigurationLoadResult(ImmutableArray<ConfiguredExistingProfile> Profiles, ImmutableArray<string> Problems);
internal sealed record ExistingAcceptanceDocument(int Version, string ConfigurationId, RuntimeVerification Verification);

internal static class ExistingControlConfiguration
{
    internal const string FileName = "existing-control.json";
    // Loading has no connection or acceptance side effects. The model cannot supply a profile or evidence.
    internal static ExistingConfigurationLoadResult Load(string directory, string acceptanceDirectory, DateTimeOffset now)
    {
        try
        {
            string root = OwnedControlConfiguration.LocalPath(directory), evidenceRoot = OwnedControlConfiguration.LocalPath(acceptanceDirectory);
            OwnedControlConfiguration.RequireNoRedirect(root); OwnedControlConfiguration.RequireNoRedirect(evidenceRoot);
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return new([], []);
            OwnedControlConfiguration.RequireNoRedirect(path);
            var source = JsonSerializer.Deserialize<Document>(OwnedControlConfiguration.ReadBounded(path), OwnedControlConfiguration.Json)
                ?? throw new InvalidOperationException();
            if (source.Version != 1 || source.Profiles.Length > 16) throw new InvalidOperationException();
            var profiles = ImmutableArray.CreateBuilder<ConfiguredExistingProfile>();
            var problems = ImmutableArray.CreateBuilder<string>();
            foreach (var item in source.Profiles)
            {
                OwnedControlConfiguration.Identifier(item.Id); OwnedControlConfiguration.Text(item.Label, 128);
                if (item.Mode is not ("disabled" or "readOnly" or "verifiedControl")) throw new InvalidOperationException();
                var configuration = new SharedExistingConfiguration
                {
                    Connection = new(new(item.Host, item.Profile), OwnedBackend.SharedRemoteSocket, false),
                    ExpectedVersion = item.CliVersion, ExpectedPlatformOs = item.Platform,
                    ExpectedCodexHome = OwnedControlConfiguration.RemotePath(item.CodexHome),
                    Directories = ImmutableHashSet.Create(StringComparer.Ordinal, OwnedControlConfiguration.RemotePath(item.Cwd))
                };
                configuration.Validate();
                if (profiles.Any(p => p.Id == item.Id || p.Configuration.Connection.Endpoint == configuration.Connection.Endpoint
                    && p.Configuration.Directories.SetEquals(configuration.Directories))) throw new InvalidOperationException();
                bool enabled = item.Mode == "verifiedControl";
                if (enabled)
                {
                    var evidence = ReadAcceptance(evidenceRoot, item.AcceptanceId, item.Id, configuration, now);
                    if (evidence == null) { enabled = false; problems.Add("existing_runtime_acceptance_missing_or_invalid:" + item.Id); }
                    else configuration = configuration with { Verification = evidence };
                }
                else if (item.AcceptanceId != null) throw new InvalidOperationException();
                profiles.Add(new(item.Id, item.Label, configuration, enabled, item.Mode == "readOnly"));
            }
            return new(profiles.ToImmutable(), problems.ToImmutable());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or JsonException or CryptographicException or NotSupportedException)
        { return new([], ["existing_configuration_invalid"]); }
    }
    private static RuntimeVerification? ReadAcceptance(string root, string? id, string profileId,
        SharedExistingConfiguration configuration, DateTimeOffset now)
    {
        if (id == null) return null;
        byte[]? bytes = null;
        try
        {
            string path = AcceptancePath(root, id); OwnedControlConfiguration.RequireNoRedirect(path);
            if (!File.Exists(path)) return null;
            bytes = new WindowsStateProtector().Unprotect(OwnedControlConfiguration.ReadBounded(path), AcceptancePurpose(root, id));
            var record = JsonSerializer.Deserialize<ExistingAcceptanceDocument>(bytes, OwnedControlConfiguration.Json);
            if (record == null || record.Version != 1 || record.ConfigurationId != profileId
                || record.Verification.Kind != VerificationKind.RuntimeAcceptance
                || record.Verification.Methods.Count == 0
                || record.Verification.ExpiresAt - record.Verification.VerifiedAt > TimeSpan.FromDays(30)) return null;
            var verified = configuration with { Verification = record.Verification };
            return record.Verification.Methods.All(method => verified.CapabilityFailure(method, now) == null) ? record.Verification : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or JsonException or CryptographicException or NotSupportedException) { return null; }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string AcceptancePath(string directory, string id)
    { OwnedControlConfiguration.Identifier(id); return Path.Combine(OwnedControlConfiguration.LocalPath(directory), id + ".existing-acceptance"); }
    internal static byte[] AcceptancePurpose(string directory, string id)
    {
        OwnedControlConfiguration.Identifier(id);
        return Encoding.UTF8.GetBytes("CodexAssistant.ExistingRuntimeAcceptance/v1/"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OwnedControlConfiguration.LocalPath(directory).ToUpperInvariant()))) + "/" + id);
    }
    private sealed record Document(int Version, ProfileDocument[] Profiles);
    private sealed record ProfileDocument(string Id, string Label, string Host, string? Profile, string Cwd, string CodexHome,
        string CliVersion, string Platform, string Mode = "disabled", string? AcceptanceId = null);
}
