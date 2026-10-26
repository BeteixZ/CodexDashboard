using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class OwnedProjectMemoryTests
{
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--owned-project-memory-test") return false;
        string root = args[1]; Directory.CreateDirectory(root); var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) { checks.Add(name, value); if (!value) throw new InvalidOperationException(name); }
        try
        {
            var clock = new Clock(); var initial = clock.UtcNow;
            var original = OwnedHostRuntimeTests.Profile(root, true);
            var config = original.Configuration with { Connection = new(new("remote-ssh-discovered:synthetic-project-host", "project-profile"), OwnedBackend.SshStdio, true),
                ExpectedPlatformOs = "linux", ExpectedCodexHome = "/synthetic/home",
                Projects = ImmutableDictionary<string, string>.Empty.Add("sample", "/synthetic/project"), Verification = null };
            config = config with { Verification = original.Configuration.Verification! with { Connection = config.Connection,
                PlatformOs = "linux", ConfigurationDigest = OwnedVerification.ConfigurationDigest(config) } };
            var profile = original with { Configuration = config, Creation = original.Creation with { Cwd = "/synthetic/project" },
                Launch = new(Path.Combine(root, "synthetic-ssh.exe"), [], root, ImmutableDictionary<string, string>.Empty) };
            int probes = 0; char source = 'a'; bool wrongRequest = false;
            string Response(string request) => JsonSerializer.Serialize(new { schemaVersion = 1, kind = "project-directory-snapshot/v1",
                requestId = request, observedAt = clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0, directories = new[] {
                    new { requestedDirectory = "/synthetic/project", status = "observed", evidence = new { version = 1, kind = "linux-directory/v1",
                        sourceIdentity = new string(source, 64), requestedDirectory = "/synthetic/project", canonicalDirectory = "/physical/project", directoryIdentity = "8:1" } } } });
            var start = OwnedProjectDirectoryProbe.StartInfo(profile, "request");
            Check("probe launch uses configured SSH and explicit home scope", start.FileName == profile.Launch.Executable
                && start.CreateNoWindow && start.ArgumentList.Contains("StrictHostKeyChecking=yes")
                && start.ArgumentList.Last().Contains("--home '/synthetic/home'", StringComparison.Ordinal)
                && start.ArgumentList.Last().Contains("--project-directory '/synthetic/project'", StringComparison.Ordinal));
            OwnedHostRuntimeTests.FakeRpc? rpc = null;
            var options = new OwnedHostOptions(Path.Combine(root, "owned"), [profile], (p, _) =>
            {
                rpc = new OwnedHostRuntimeTests.FakeRpc(p.Configuration, () => Task.FromResult(true));
                rpc.ProjectSourceIdentity = new string('a', 64);
                return Task.FromResult<IJsonRpcConnection>(rpc);
            }, ProjectProbe: async (_, request, ct) => { probes++; await Task.Yield(); ct.ThrowIfCancellationRequested();
                return Response(wrongRequest ? "old-request" : request); });
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(root, "host"), ownedOptions: options, clock: clock);
            f.Settings.ChatGpt.TaskControlEnabled = true;
            Check("activation never probes remote directories", probes == 0);
            await OwnedHostRuntimeTests.SendToolAsync(f, "create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
            var runtime = f.Runtime.OwnedRuntime!; var task = runtime.Tasks().Single();
            string instruction = "继续 " + task.Handle.Identity.ThreadId + " 分析项目";
            await OwnedHostRuntimeTests.SendToolAsync(f, "continue", instruction, _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), instruction));
            task = runtime.Tasks().Single();
            var reference = new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, task.Snapshot.CurrentTurnId, task.Handle.Identity.ProfileId);
            var resolver = f.Model.ProjectMemoryResolver!;
            Check("remote owned task requires independent proof", resolver.Resolve(reference) == null && probes == 0);
            f.Settings.ChatGpt.Enabled = false;
            async Task Query(string id, string body) { await f.InputAsync(id, body); await OwnedHostRuntimeTests.WaitReplyAsync(f, id); }
            await Query("select", "切换到 " + reference.ThreadId);
            int writes = (await f.ReadAsync()).Operations.Length; int calls = rpc!.Methods.Count;
            await Query("refresh", "刷新项目来源");
            var target = resolver.Resolve(reference);
            Check("explicit model disabled refresh enables exact remote owned project", probes == 1 && target?.Project.Backend == "assistant-owned"
                && target.Project.ProfileId == "project-profile" && target.Project.CanonicalDirectory == "/physical/project"
                && f.Sent.Last().Contains("已刷新", StringComparison.Ordinal));
            Check("project refresh never creates operation or sends Codex RPC", (await f.ReadAsync()).Operations.Length == writes && rpc.Methods.Count == calls);
            var memoryRoot = Path.Combine(root, "memory");
            var memory = new AssistantMemoryStore(memoryRoot, () => "synthetic-owned-memory"); memory.Initialize();
            var entry = memory.SaveProjectFromEditor(memory.CaptureProjectEditor(target!, resolver), "SYNTHETIC_OWNED_PROJECT_KNOWLEDGE", resolver);
            var capture = memory.CaptureForGeneration(true, DateTimeOffset.UtcNow, target);
            var document = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(memory.DataPath!)), Settings.Json)!;
            Check("remote owned knowledge saves protected format seven", document.FormatVersion == 7
                && capture.Content!.Context.Contains(entry.Text) && !memory.Capture().Context.Contains(entry.Text));
            var reopened = new AssistantMemoryStore(memoryRoot, () => "synthetic-owned-memory");
            Check("format seven reopens without losing project scope", reopened.Entries.Any(e => e.Id == entry.Id));
            document.FormatVersion = 6; bool oldFormatRejected = false;
            try { AssistantMemoryLayers.Validate(document); } catch (InvalidDataException) { oldFormatRejected = true; }
            Check("owned remote scope cannot masquerade as old format", oldFormatRejected);
            var before = memory.CaptureProjectEditor(target!, resolver, entry.Id);
            source = 'b'; await Query("refresh-changed", "刷新项目来源");
            var replacement = resolver.Resolve(reference);
            bool editRejected = false;
            try { memory.SaveProjectFromEditor(before, "STALE", resolver); } catch (InvalidOperationException) { editRejected = true; }
            Check("probe redirected away from app server rejects old edit and knowledge", editRejected && replacement == null);
            source = 'a';
            wrongRequest = true; await Query("refresh-mismatch", "刷新项目来源");
            Check("wrong request invalidates prior cached evidence", resolver.Resolve(reference) == null);
            wrongRequest = false; await Query("refresh-again", "刷新项目来源");
            clock.UtcNow = initial.AddMinutes(3);
            Check("expired proof does not enable memory", resolver.Resolve(reference) == null);
            // The basic fake does not persist an unguarded turn/start reply into thread/read.
            rpc.SetThread(reference.ThreadId, task.Snapshot.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn(reference.TurnId!, "inProgress"));
            int previousProbes = probes; int previousReads = rpc.Methods.Count(m => m == "thread/read");
            var prepared = await Task.WhenAll(resolver.PrepareAsync(reference, default), resolver.PrepareAsync(reference, default));
            File.WriteAllText(Path.Combine(root, "demand-observation.json"), JsonSerializer.Serialize(new {
                prepared, probes, previousProbes, previousReads, methods = rpc.Methods.ToArray(),
                task = runtime.Tasks().Single().Snapshot, reference }));
            Check("concurrent demand refresh shares one proof and one task read", prepared.All(p => p != null)
                && probes == previousProbes + 1 && rpc.Methods.Count(m => m == "thread/read") == previousReads + 1);
            Check("demand refresh creates no control operation", (await f.ReadAsync()).Operations.Length == writes);
            int cachedProbes = probes; int cachedCalls = rpc.Methods.Count;
            Check("project editor entry reuses fresh proof", await f.Model.ProjectMemoryTargetProvider!(default) != null
                && probes == cachedProbes && rpc.Methods.Count == cachedCalls);
            var wrongTurn = reference with { TurnId = "another-turn" };
            Check("demand never follows an unselected turn", await resolver.PrepareAsync(wrongTurn, default) == null
                && probes == cachedProbes && rpc.Methods.Count == cachedCalls);
            using (var canceled = new System.Threading.CancellationTokenSource())
            {
                canceled.Cancel(); bool canceledRead = false;
                try { await resolver.PrepareAsync(reference, canceled.Token); } catch (OperationCanceledException) { canceledRead = true; }
                Check("canceled demand does not probe", canceledRead && probes == cachedProbes);
            }
            clock.UtcNow = initial.AddMinutes(6); wrongRequest = true;
            Check("failed demand excludes project without failing answer", await resolver.PrepareAsync(reference, default) == null);
            wrongRequest = false;
            Check("next intentional read can recover proof", await f.Model.ProjectMemoryTargetProvider!(default) != null);
            clock.UtcNow = initial.AddMinutes(9); int beforeChangedTurn = probes;
            rpc.SetThread(reference.ThreadId, task.Snapshot.Cwd, OwnedHostRuntimeTests.FakeRpc.Turn("new-server-turn", "inProgress"));
            Check("read detecting a newer turn never probes or adopts it", await resolver.PrepareAsync(reference, default) == null
                && probes == beforeChangedTurn);
            await rpc.DisposeAsync();
            Check("disconnected app server invalidates directory proof", resolver.Resolve(reference) == null);
        }
        catch (Exception error) { checks["failure"] = false; File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); }
        File.WriteAllText(Path.Combine(root, "owned-project-checks.json"), JsonSerializer.Serialize(new { checks, realNetworkUsed = false, productionCredentialsRead = false }, new JsonSerializerOptions { WriteIndented = true }));
        exit(checks.Values.All(v => v) ? 0 : 1); return true;
    }
}
