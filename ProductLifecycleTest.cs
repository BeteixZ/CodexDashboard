using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codex.Protocol;

namespace CodexDashboard;

/// <summary>
/// Isolated distribution check only. This bypasses the normal UI/channel/model lifecycle.
/// Invoke before settings, credential loading, production singleton or service construction.
/// </summary>
internal static class ProductLifecycleTest
{
    internal static bool IsRequested(string[] args) => args.Contains("--isolated-lifecycle-test", StringComparer.Ordinal);

    internal static async Task<int> RunAsync(string[] args, ProtocolProduct product)
    {
        string? evidence = null;
        try
        {
            if (args.Length != 4 || args[0] != "--isolated-lifecycle-test" || args[2] != "--data-dir")
                return 2;
            string data = RequireDirectory(args[3]);
            string requestedEvidence = RequireDirectory(args[1]);
            if (!requestedEvidence.Equals(Path.Combine(data, "evidence"), StringComparison.OrdinalIgnoreCase))
                return 2;
            // Only metadata is inspected before the dedicated synthetic manifest is authenticated.
            string[] allowed = { "lifecycle.manifest.json", "fixture.json", "evidence", "stop.request" };
            if (Directory.EnumerateFileSystemEntries(data).Any(p => !allowed.Contains(Path.GetFileName(p), StringComparer.Ordinal)))
                return 2;
            string manifestPath = Path.Combine(data, "lifecycle.manifest.json");
            RequireSmallFile(manifestPath);
            var manifest = JsonSerializer.Deserialize<LifecycleManifest>(File.ReadAllText(manifestPath), SharedJson.Options);
            if (manifest == null || manifest.Version != 1 || manifest.Purpose != "independent-package-isolated-check"
                || !Guid.TryParseExact(manifest.RunId, "N", out _) || manifest.Product != product.ToString()
                || manifest.DataDirectory != data || manifest.EvidenceDirectory != requestedEvidence
                || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))
                    .Equals(manifest.PackageDirectory, StringComparison.OrdinalIgnoreCase)
                || manifest.ChannelsEnabled != false || manifest.ModelEnabled != false || manifest.TaskControlEnabled != false
                || manifest.RealLocalCollectionEnabled != false || manifest.RemoteHosts == null || manifest.RemoteHosts.Count != 0
                || manifest.MaximumSeconds is < 5 or > 120)
                return 2;
            string fixturePath = Path.Combine(data, "fixture.json");
            RequireSmallFile(fixturePath);
            var fixture = JsonSerializer.Deserialize<LifecycleFixture>(File.ReadAllText(fixturePath), SharedJson.Options);
            if (fixture == null || fixture.Version != 1 || fixture.TaskCount is < 1 or > 1000
                || fixture.Source != "synthetic-lifecycle-fixture")
                return 2;
            evidence = requestedEvidence;
            string directoryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..24];
            string singletonName = "Local\\CodexLifecycleTest." + product + "." + directoryHash;
            // The named object lifetime enforces uniqueness; no thread-affine ownership crosses awaits.
            using var singleton = new Mutex(false, singletonName, out bool created);
            if (!created)
            {
                WriteJson(Path.Combine(evidence, $"duplicate.{Environment.ProcessId}.json"),
                    new { rejected = true, reason = "isolated_data_directory_in_use", processId = Environment.ProcessId });
                return 10;
            }
            if (File.Exists(Path.Combine(evidence, "startup.json"))) return 2;
            string profile = "lifecycle-" + directoryHash;
            string endpoint = LocalProtocolEndpoint.ForCurrentUser(product, profile);
            using var process = Process.GetCurrentProcess();
            using var monitor = new MonitorService(new FixtureSettings(data), new FixtureCollector(fixture.TaskCount));
            await using var bridge = new ProductStatusBridge(product, profile);
            WriteJson(Path.Combine(evidence, "startup.json"), new
            {
                checkKind = "independent-package-isolated-check", manifest.RunId, product = product.ToString(),
                processId = Environment.ProcessId, processStartedAt = process.StartTime.ToUniversalTime(),
                dataDirectory = data, packageDirectory = manifest.PackageDirectory, singletonName, endpoint, profile,
                productionLifecycleExercised = false, channelsConstructed = false, modelConstructed = false,
                realTaskTransportConstructed = false, realLocalCollectionEnabled = false,
                remoteHosts = Array.Empty<string>(), credentialsRead = false, fixture.TaskCount
            });
            bridge.Start();
            var watch = Stopwatch.StartNew();
            var transitions = new NotificationTransitions();
            long sequence = 0;
            int notificationCount = 0;
            string reason = "maximum_duration";
            while (watch.Elapsed < TimeSpan.FromSeconds(manifest.MaximumSeconds))
            {
                if (File.Exists(Path.Combine(data, "stop.request"))) { reason = "stop_file"; break; }
                long before = Stopwatch.GetTimestamp();
                var hosts = await monitor.RefreshAsync() ?? throw new InvalidOperationException("fixture_refresh_skipped");
                double collectionMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                var tasks = hosts.SelectMany(h => h.Tasks).ToArray();
                var now = DateTimeOffset.UtcNow;
                notificationCount += transitions.Observe(tasks, now).Count;
                bool snapshotValid = hosts.Count == 1 && hosts[0].Name == HostIdentity.LocalDisplayName
                    && hosts[0].Error == null && tasks.Length == fixture.TaskCount
                    && tasks.All(t => t.Id.StartsWith("isolated-fixture-", StringComparison.Ordinal)
                        && t.Cwd == Path.Combine(data, "unused-codex", "fixture-project")
                        && t.FinalReportTurnId == t.TurnId && !t.Disconnected);
                if (!snapshotValid) throw new InvalidOperationException("synthetic_snapshot_invalid");
                bridge.Publish(new() { ObservedAt = now, Readiness = Availability.Available },
                    new() { ObservedAt = now, WeChat = ConnectionState.NotConfigured,
                        Model = Availability.Unavailable, QueuedCount = 0, AttentionCount = 0 });
                if (bridge.EndpointFailure != null) throw new IOException("isolated_status_endpoint_unavailable");
                sequence++;
                process.Refresh();
                var heartbeat = new
                {
                    sequence, observedAt = now, processId = Environment.ProcessId, product = product.ToString(),
                    snapshotValid, taskCount = tasks.Length, collectionMs, elapsedMs = watch.Elapsed.TotalMilliseconds,
                    cpuMs = process.TotalProcessorTime.TotalMilliseconds, workingSetBytes = process.WorkingSet64,
                    allocatedBytes = GC.GetTotalAllocatedBytes(), notificationCount, channelsConstructed = false
                };
                WriteJson(Path.Combine(evidence, "snapshotcheck.json"), new
                {
                    sequence, passed = snapshotValid, source = fixture.Source,
                    taskCount = tasks.Length, distinctCanonicalTasks = tasks.Select(t => new CanonicalTaskIdentity("local", t.Id).Key).Distinct().Count(),
                    sameTurnReports = true, realTaskAccess = false
                });
                WriteJson(Path.Combine(evidence, "heartbeat.json"), heartbeat);
                File.AppendAllText(Path.Combine(evidence, "samples.jsonl"),
                    JsonSerializer.Serialize(heartbeat, CompactJson) + Environment.NewLine);
                await Task.Delay(250);
            }
            WriteJson(Path.Combine(evidence, "stopped.json"), new
            {
                reason, sequence, processId = Environment.ProcessId, observedAt = DateTimeOffset.UtcNow,
                productionLifecycleExercised = false
            });
            return 0;
        }
        catch (Exception ex)
        {
            // Only a previously validated isolated evidence directory may receive diagnostics.
            if (evidence != null)
                try { WriteJson(Path.Combine(evidence, "failure.json"), new { errorType = ex.GetType().Name }); } catch { }
            return 1;
        }
    }

    private static readonly JsonSerializerOptions CompactJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static void WriteJson(string path, object value)
    {
        string temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, SharedJson.Options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    private static string RequireDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) throw new ArgumentException("absolute_directory_required");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full) || full == Path.GetPathRoot(full)) throw new ArgumentException("isolated_directory_required");
        for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_directory_rejected");
        return full;
    }
    private static void RequireSmallFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16384 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("invalid_synthetic_fixture");
    }
    private sealed class LifecycleManifest
    {
        public int Version { get; set; }
        public string Purpose { get; set; } = "";
        public string RunId { get; set; } = "";
        public string Product { get; set; } = "";
        public string DataDirectory { get; set; } = "";
        public string EvidenceDirectory { get; set; } = "";
        public string PackageDirectory { get; set; } = "";
        public bool? ChannelsEnabled { get; set; }
        public bool? ModelEnabled { get; set; }
        public bool? TaskControlEnabled { get; set; }
        public bool? RealLocalCollectionEnabled { get; set; }
        public List<string>? RemoteHosts { get; set; }
        public int MaximumSeconds { get; set; }
    }
    private sealed class LifecycleFixture
    {
        public int Version { get; set; }
        public string Source { get; set; } = "";
        public int TaskCount { get; set; }
    }
    private sealed class FixtureSettings(string data) : ICollectorSettings
    {
        public string PythonPath => Path.Combine(data, "never-start-python.exe");
        public string CodexHome => Path.Combine(data, "unused-codex");
        public List<string> RemoteHosts { get; } = new();
        public bool LocalEnabled => true; // Only the explicitly injected synthetic collector sees this flag.
    }
    private sealed class FixtureCollector(int taskCount) : IHostCollector
    {
        private int generation;
        public Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (host != HostIdentity.LocalDisplayName || settings.RemoteHosts.Count != 0)
                throw new InvalidOperationException("fixture_host_mismatch");
            generation++;
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            var snapshot = new HostSnapshot { Name = host, LastSuccess = DateTimeOffset.UtcNow };
            for (int i = 0; i < taskCount; i++)
            {
                string turn = $"synthetic-turn-{i}-{generation / 8}";
                snapshot.Tasks.Add(new()
                {
                    Id = $"isolated-fixture-{i:D4}", Host = host, Cwd = Path.Combine(settings.CodexHome, "fixture-project"),
                    Title = "隔离生命周期合成任务", Project = "合成项目",
                    TurnId = turn, FinalReportTurnId = turn, FinalReport = "合成报告，无真实任务访问。",
                    Status = i % 3 == 0 ? TaskStatusNames.Completed : TaskStatusNames.Active,
                    RecordedStatus = i % 3 == 0 ? TaskStatusNames.Completed : TaskStatusNames.Active,
                    StartedAt = now - 30, LastActivity = now, CompletedAt = i % 3 == 0 ? now : 0,
                    Summary = "仅验证独立发行包采集与状态协议。", OneLineSummary = "合成任务"
                });
            }
            return Task.FromResult(snapshot);
        }
    }
}
