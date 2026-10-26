using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Codex.Control;

namespace CodexDashboard;

// Synthetic configuration and newly created DPAPI proof fixtures only. Never opens a connection.
internal static class OwnedControlConfigurationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--owned-configuration-test");
        if (index < 0) return false;
        try { await RunAsync(args[index + 1]); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(directory)
            || Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_absolute_test_directory_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_test_directory_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool condition) { checks.Add(name, condition); if (!condition) throw new InvalidOperationException(name); }
        string configRoot = Path.Combine(directory, "configuration"), evidenceRoot = Path.Combine(directory, "test-only-acceptance");
        var now = new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);
        OwnedConfigurationLoadResult Load() => OwnedControlConfiguration.Load(configRoot, evidenceRoot, now);
        void Save(JsonNode value)
        {
            Directory.CreateDirectory(configRoot);
            File.WriteAllText(Path.Combine(configRoot, OwnedControlConfiguration.FileName), value.ToJsonString());
        }
        JsonObject Profile() => new()
        {
            ["id"] = "demo", ["label"] = "测试配置", ["host"] = "local", ["profile"] = "synthetic-profile",
            ["codexHome"] = Path.Combine(directory, "unused-codex-home"), ["project"] = "sample",
            ["cwd"] = Path.Combine(directory, "unused-project"), ["model"] = "synthetic-model",
            ["approval"] = "on-request", ["sandbox"] = "workspace-write", ["cliVersion"] = OwnedVerification.SchemaVersion,
            ["platform"] = "windows", ["local"] = new JsonObject { ["executable"] = Path.Combine(directory, "never-started-codex.exe") }
        };
        JsonObject Document(JsonObject? profile = null) => new() { ["version"] = 1, ["profiles"] = new JsonArray(profile?.DeepClone() ?? Profile()) };
        void Invalid(string name, Action<JsonObject, JsonObject> mutate)
        {
            var document = Document();
            mutate(document, (JsonObject)document["profiles"]![0]!);
            Save(document);
            var result = Load();
            Check(name, result.Profiles.Length == 0 && result.Problems.Single().Code == "owned_configuration_invalid");
        }
        JsonObject Acceptance(ConfiguredOwnedProfile profile) => new()
        {
            ["version"] = 1, ["kind"] = "RuntimeAcceptance", ["configurationId"] = profile.Id,
            ["host"] = profile.Configuration.Connection.Endpoint.HostId, ["profile"] = profile.Configuration.Connection.Endpoint.ProfileId,
            ["backend"] = profile.Configuration.Connection.Backend.ToString(), ["cliVersion"] = profile.Configuration.ExpectedVersion,
            ["platform"] = profile.Configuration.ExpectedPlatformOs,
            ["configurationDigest"] = OwnedVerification.ConfigurationDigest(profile.Configuration),
            ["launchPolicyDigest"] = OwnedControlConfiguration.LaunchPolicyDigest(profile),
            ["methods"] = new JsonArray("thread/start"), ["evidenceReference"] = "TEST ONLY; no runtime acceptance performed",
            ["verifiedAt"] = now.AddMinutes(-1), ["expiresAt"] = now.AddMinutes(30)
        };
        void Proof(string text, string id = "fixture")
        {
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllBytes(OwnedControlConfiguration.AcceptancePath(evidenceRoot, id),
                new WindowsStateProtector().Protect(Encoding.UTF8.GetBytes(text), OwnedControlConfiguration.AcceptancePurpose(evidenceRoot, id)));
        }
        void EvidenceRejected(string name, ConfiguredOwnedProfile profile, Action<JsonObject> mutate)
        {
            var evidence = Acceptance(profile); mutate(evidence); Proof(evidence.ToJsonString());
            var result = Load();
            Check(name, result.Profiles.Single().Mode == OwnedProfileMode.Disabled
                && result.Profiles.Single().Configuration.Verification == null && result.Problems.Length == 1);
        }
        try
        {
            var missing = Load();
            Check("missing configuration is empty and does not create directories",
                !missing.FilePresent && missing.Profiles.Length == 0 && missing.Problems.Length == 0
                && !Directory.Exists(configRoot) && !Directory.Exists(evidenceRoot));
            Save(new JsonObject { ["version"] = 1, ["profiles"] = new JsonArray() });
            Check("explicit empty profiles remains disabled", Load().Profiles.Length == 0 && Load().Problems.Length == 0);
            Save(Document());
            var disabled = Load().Profiles.Single();
            Check("omitted mode defaults disabled with a real nonsynthetic identity",
                disabled.Mode == OwnedProfileMode.Disabled && !disabled.Configuration.Connection.Synthetic
                && disabled.Configuration.Verification == null);
            Check("local launch is fixed stdio with only CODEX_HOME override",
                disabled.Launch!.Arguments.SequenceEqual(["app-server", "--listen", "stdio://"])
                && disabled.Launch.Environment.Count == 1 && disabled.Launch.Environment.ContainsKey("CODEX_HOME"));
            Invalid("unknown top-level field rejected", (d, _) => d["verification"] = true);
            Invalid("unknown nested launch argument rejected", (_, p) => p["local"]!["arguments"] = new JsonArray("/c", "calc"));
            Invalid("model-like synthetic flag rejected", (_, p) => p["synthetic"] = true);
            Invalid("arbitrary environment rejected", (_, p) => p["environment"] = new JsonObject { ["SECRET"] = "ignored" });
            Invalid("inline verification rejected", (_, p) => p["verification"] = new JsonObject { ["verified"] = true });
            Invalid("unknown version rejected", (d, _) => d["version"] = 2);
            Invalid("null profile rejected", (d, _) => d["profiles"] = new JsonArray((JsonNode?)null));
            Invalid("null required field rejected", (_, p) => p["cwd"] = null);
            Invalid("missing required field rejected", (_, p) => p.Remove("model"));
            Invalid("unreviewed CLI version rejected", (_, p) => p["cliVersion"] = "99.0.0");
            Invalid("unknown mode rejected", (_, p) => p["mode"] = "enabled");
            Invalid("never-approve policy rejected", (_, p) => p["approval"] = "never");
            Invalid("full access sandbox rejected", (_, p) => p["sandbox"] = "danger-full-access");
            Invalid("relative executable rejected", (_, p) => p["local"]!["executable"] = "codex.exe");
            Invalid("working directory traversal rejected", (_, p) => p["cwd"] = Path.Combine(directory, "..", "other"));
            Invalid("alternate data stream path rejected", (_, p) => p["codexHome"] = Path.Combine(directory, "home:other"));
            Invalid("acceptance filename traversal rejected", (_, p) => p["acceptanceId"] = "../proof");
            Invalid("local host mismatch rejected", (_, p) => p["host"] = "remote-ssh-discovered:fixture");
            Invalid("local platform mismatch rejected", (_, p) => p["platform"] = "linux");
            Invalid("missing launch rejected", (_, p) => p.Remove("local"));
            Invalid("ambiguous launch rejected", (_, p) => p["ssh"] = new JsonObject
            { ["executable"] = "C:\\ssh.exe", ["localWorkingDirectory"] = directory, ["remoteExecutable"] = "/bin/codex" });
            Invalid("duplicate profile ID rejected", (d, p) =>
            { var other = p.DeepClone(); other["profile"] = "different"; ((JsonArray)d["profiles"]!).Add(other); });
            Invalid("duplicate endpoint with different config ID rejected", (d, p) =>
            { var other = p.DeepClone(); other["id"] = "alternate"; ((JsonArray)d["profiles"]!).Add(other); });
            foreach (var pair in new[]
            {
                ("duplicate top-level property rejected", Document().ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)),
                ("duplicate nested property rejected", Document().ToJsonString().Replace("\"id\":\"demo\"", "\"id\":\"demo\",\"id\":\"demo\"", StringComparison.Ordinal))
            })
            {
                File.WriteAllText(Path.Combine(configRoot, OwnedControlConfiguration.FileName), pair.Item2);
                Check(pair.Item1, Load().Profiles.Length == 0 && Load().Problems.Length == 1);
            }
            var readOnly = Profile(); readOnly["mode"] = "readOnly"; Save(Document(readOnly));
            Check("explicit read-only configuration requires no write acceptance",
                Load().Profiles.Single().Mode == OwnedProfileMode.ReadOnly && Load().Problems.Length == 0);

            var verified = Profile(); verified["mode"] = "verifiedControl"; verified["acceptanceId"] = "fixture";
            Save(Document(verified));
            var requested = Load().Profiles.Single();
            Check("missing acceptance disables requested write mode", requested.Mode == OwnedProfileMode.Disabled && Load().Problems.Length == 1);
            Directory.CreateDirectory(evidenceRoot);
            File.WriteAllText(OwnedControlConfiguration.AcceptancePath(evidenceRoot, "fixture"), Acceptance(requested).ToJsonString());
            Check("plaintext acceptance has no fallback", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);
            Proof(Acceptance(requested).ToJsonString());
            var accepted = Load();
            Check("protected exact acceptance enables only its declared method",
                accepted.Problems.Length == 0 && accepted.Profiles.Single().Mode == OwnedProfileMode.VerifiedControl
                && OwnedVerification.CapabilityFailure(accepted.Profiles.Single().Configuration, "thread/start", now) == null
                && OwnedVerification.CapabilityFailure(accepted.Profiles.Single().Configuration, "turn/start", now) == "method_not_runtime_verified");
            EvidenceRejected("replay evidence cannot enable real configuration", requested, e => e["kind"] = "SyntheticReplay");
            EvidenceRejected("expired acceptance rejected", requested, e => e["expiresAt"] = now);
            EvidenceRejected("future acceptance rejected", requested, e => e["verifiedAt"] = now.AddMinutes(1));
            EvidenceRejected("unbounded acceptance lifetime rejected", requested, e => e["expiresAt"] = now.AddDays(31));
            EvidenceRejected("wrong endpoint acceptance rejected", requested, e => e["host"] = "remote-ssh-discovered:other");
            EvidenceRejected("wrong backend acceptance rejected", requested, e => e["backend"] = "SshStdio");
            EvidenceRejected("wrong configuration acceptance rejected", requested, e => e["configurationId"] = "alternate");
            EvidenceRejected("wrong digest acceptance rejected", requested, e => e["configurationDigest"] = new string('0', 64));
            EvidenceRejected("wrong launch policy acceptance rejected", requested, e => e["launchPolicyDigest"] = new string('0', 64));
            EvidenceRejected("unsupported method acceptance rejected", requested, e => e["methods"] = new JsonArray("shell/execute"));
            EvidenceRejected("duplicate method acceptance rejected", requested, e => e["methods"] = new JsonArray("thread/start", "thread/start"));
            EvidenceRejected("unknown protected evidence field rejected", requested, e => e["enabled"] = true);
            Proof(Acceptance(requested).ToJsonString().Replace("\"kind\":\"RuntimeAcceptance\"",
                "\"kind\":\"SyntheticReplay\",\"kind\":\"RuntimeAcceptance\"", StringComparison.Ordinal));
            Check("duplicate properties in protected evidence rejected", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);
            Proof(Acceptance(requested).ToJsonString());
            var proofPath = OwnedControlConfiguration.AcceptancePath(evidenceRoot, "fixture");
            var tampered = File.ReadAllBytes(proofPath); tampered[^1] ^= 1; File.WriteAllBytes(proofPath, tampered);
            Check("protected evidence tampering rejected", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);
            Proof(Acceptance(requested).ToJsonString());
            var changed = (JsonObject)verified.DeepClone(); changed["approval"] = "untrusted"; Save(Document(changed));
            Check("approval policy change invalidates old acceptance", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);
            changed = (JsonObject)verified.DeepClone(); changed["local"]!["executable"] = Path.Combine(directory, "other-codex.exe"); Save(Document(changed));
            Check("executable path change invalidates old acceptance", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);
            Save(Document(verified));
            string copiedRoot = Path.Combine(directory, "copied-evidence"); Directory.CreateDirectory(copiedRoot);
            File.Copy(proofPath, OwnedControlConfiguration.AcceptancePath(copiedRoot, "fixture"));
            Check("copied evidence cannot change trusted directory scope",
                OwnedControlConfiguration.Load(configRoot, copiedRoot, now).Profiles.Single().Mode == OwnedProfileMode.Disabled);
            File.Copy(proofPath, OwnedControlConfiguration.AcceptancePath(evidenceRoot, "renamed"));
            changed = (JsonObject)verified.DeepClone(); changed["acceptanceId"] = "renamed"; Save(Document(changed));
            Check("renamed evidence cannot change acceptance identifier", Load().Profiles.Single().Mode == OwnedProfileMode.Disabled);

            var ssh = Profile(); ssh.Remove("local"); ssh["host"] = "remote-ssh-discovered:fixture-host"; ssh["platform"] = "linux";
            ssh["cwd"] = "/tmp/synthetic-project"; ssh["codexHome"] = "/tmp/synthetic home's";
            ssh["ssh"] = new JsonObject { ["executable"] = Path.Combine(directory, "never-started-ssh.exe"),
                ["localWorkingDirectory"] = directory, ["remoteExecutable"] = "/usr/bin/codex'$(whoami)" };
            Save(Document(ssh));
            var remote = Load().Profiles.Single();
            Check("SSH uses fixed arguments batch mode host-key checks and exact alias",
                remote.Configuration.Connection.Backend == OwnedBackend.SshStdio
                && remote.Launch!.Arguments.Take(7).SequenceEqual(["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "--", "fixture-host"]));
            Check("SSH path metacharacters remain individually POSIX quoted",
                remote.Launch!.Arguments.Last() == "exec env CODEX_HOME='/tmp/synthetic home'\\''s' '/usr/bin/codex'\\''$(whoami)' app-server --listen stdio://");
            ssh["host"] = "remote-ssh-discovered:fixture;whoami"; Save(Document(ssh));
            Check("SSH host injection rejected", Load().Profiles.Length == 0 && Load().Problems.Length == 1);
            Check("configuration loading never created executable or Codex home", !File.Exists(disabled.Launch!.Executable)
                && !Directory.Exists(disabled.Configuration.ExpectedCodexHome));
        }
        catch (Exception error)
        {
            checks["failure"] = false;
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "owned-configuration-checks.json"),
                JsonSerializer.Serialize(new { passed = checks.Values.All(v => v), count = checks.Count, checks,
                    runtimeAcceptancePerformed = false, realProcessesStarted = 0, networkConnections = 0, productionCredentialsRead = false },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{checks.Count(c => c.Value)}/{checks.Count} owned configuration checks passed");
        }
    }
}
