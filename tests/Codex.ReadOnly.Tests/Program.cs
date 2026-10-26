using System.Text.Json;
using CodexDashboard;

var checks = new Dictionary<string, bool>();
void Check(string name, bool value)
{
    checks.Add(name, value);
    if (!value) throw new InvalidOperationException("Failed: " + name);
}

TaskRecord Record(string status = TaskStatusNames.Active, string host = "本机", string id = "one", string turn = "turn-1") => new()
{
    Id = id, Title = "可核实的任务", Project = "project", Cwd = "synthetic/workspace", Model = "model",
    TurnId = turn, Status = status, RecordedStatus = "recorded", LastActivity = 2000, StartedAt = 1000, CompletedAt = 1900,
    Summary = "进展", OneLineSummary = "一行概览", SummarySource = "公开记录", FinalReport = "最终报告",
    FinalReportTurnId = turn, FinalReportSource = "最终公开回复", FinalReportTruncated = true,
    FinalReportAt = 1950, Activity = "activity", Source = "source", Host = host,
    Plan = new() { new() { Step = "步骤", Status = TaskStatusNames.Completed } },
    Timeline = new() { new() { Time = 1800, Kind = "进展", Text = "已确认" } }
};

try
{
    Check("legacy dashboard keeps its existing instance identity", DashboardInstanceIdentity.Prefix(null) == "Local\\CodexDashboard");
    string profileA = Path.Combine(Path.GetTempPath(), "dashboard-profile-a");
    string profileB = Path.Combine(Path.GetTempPath(), "dashboard-profile-b");
    Check("separate data directories cannot activate or stop each other",
        DashboardInstanceIdentity.Prefix(profileA) != DashboardInstanceIdentity.Prefix(profileB));
    Check("trailing separator does not create a duplicate profile",
        DashboardInstanceIdentity.Prefix(profileA) == DashboardInstanceIdentity.Prefix(profileA + Path.DirectorySeparatorChar));
    Check("explicit Python interpreter is retained", CollectorEnvironment.ResolvePython("custom-interpreter") == "custom-interpreter");
    string packageFixture = Path.Combine(Path.GetTempPath(), "codex-release-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(packageFixture);
    try
    {
        Check("development packages retain the legacy data directory",
            CollectorEnvironment.DistributionDataDirectory("dashboard", "legacy", packageFixture) == "legacy");
        string marker = Path.Combine(packageFixture, "distribution.json");
        File.WriteAllText(marker, """{"schemaVersion":1,"mode":"portable","product":"dashboard"}""");
        string portableData = CollectorEnvironment.DistributionDataDirectory("dashboard", "legacy", packageFixture);
        Check("portable dashboard uses a separate data directory", portableData.EndsWith("CodexDashboardPortable") && portableData != "legacy");
        bool rejectedForeign = false;
        try { CollectorEnvironment.DistributionDataDirectory("assistant", "legacy", packageFixture); }
        catch (InvalidDataException) { rejectedForeign = true; }
        Check("foreign distribution marker never falls back to legacy data", rejectedForeign);
        File.WriteAllText(marker, """{"schemaVersion":2,"mode":"portable","product":"dashboard"}""");
        bool rejectedVersion = false;
        try { CollectorEnvironment.DistributionDataDirectory("dashboard", "legacy", packageFixture); }
        catch (InvalidDataException) { rejectedVersion = true; }
        Check("future distribution marker fails closed", rejectedVersion);
        string bundledDirectory = Path.Combine(packageFixture, "python");
        Directory.CreateDirectory(bundledDirectory);
        string bundledPython = Path.Combine(bundledDirectory, "python.exe");
        File.WriteAllText(bundledPython, "synthetic - never executed");
        if (OperatingSystem.IsWindows())
        {
            Check("bundled default is stored without a versioned absolute path", CollectorEnvironment.FindPython(packageFixture) == "python");
            Check("default interpreter resolves to the current package", CollectorEnvironment.ResolvePython("python", packageFixture) == bundledPython);
            Check("custom interpreter survives standalone packaging", CollectorEnvironment.ResolvePython("custom-interpreter", packageFixture) == "custom-interpreter");
        }
    }
    finally { Directory.Delete(packageFixture, recursive: true); }
    Check("shared library has no UI or application reference",
        typeof(MonitorService).Assembly.GetReferencedAssemblies().All(a =>
            a.Name != "PresentationFramework" && a.Name != "WindowsBase"
            && a.Name != "CodexDashboard" && !a.Name!.StartsWith("Assistant.")));
    Check("raw task has no dashboard properties", new[] { "Color", "HudTip", "ShortAge", "PlanPercent", "Duration" }
        .All(n => typeof(TaskRecord).GetProperty(n) == null));
    Check("raw host has no dashboard colors", typeof(HostSnapshot).GetProperty("Color") == null);
    Check("raw plan and timeline have no visual fields",
        typeof(PlanStep).GetProperty("Mark") == null && typeof(TimelineEntry).GetProperty("Clock") == null);

    Check("local identity follows Codex", HostIdentity.ToHostId("本机") == "local" && HostIdentity.DisplayName("local") == "本机");
    Check("remote identity follows Codex", HostIdentity.ToHostId("host-a") == "remote-ssh-discovered:host-a"
        && HostIdentity.DisplayName("remote-ssh-discovered:host-a") == "host-a");
    Check("alias rejects commands and options", new[] { "", "-oProxyCommand=x", "a;cmd", "a b", "a\n", "../host" }.All(a => !HostIdentity.ValidAlias(a)));
    Check("alias accepts existing supported syntax", new[] { "host-a", "host-a", "host_a", "host.a" }.All(HostIdentity.ValidAlias));
    Check("clean preserves Unicode scalar boundary", TextSafety.Clean("A😀B", 2) == "A…" && TextSafety.Clean("A😀B", 3) == "A😀…");
    Check("clean removes controls and permits text whitespace", TextSafety.Clean(" \0A\nB\tC\u0001 ", 100) == "A\nB\tC");
    Check("hash is stable and unchanged from SHA256 baseline",
        TextSafety.Hash("abc") == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");

    var source = Record();
    var display = new DashboardTaskRecord(source);
    Check("presentation retains all raw scalar values", typeof(TaskRecord).GetProperties()
        .Where(p => p.CanWrite && p.Name is not ("Plan" or "Timeline"))
        .All(p => Equals(p.GetValue(source), p.GetValue(display))));
    Check("presentation preserves report and task identity",
        display.Key == source.Key && display.FinalReportTurnId == source.FinalReportTurnId && display.FinalReport == source.FinalReport);
    Check("presentation gives plans and timeline their existing bindings",
        display.Plan[0] is DashboardPlanStep { Mark: "✓" }
        && display.Timeline[0] is DashboardTimelineEntry { Clock.Length: > 0 });
    display.Plan[0].Step = "display change";
    Check("presentation does not modify collector plan", source.Plan[0].Step == "步骤");
    Check("dashboard colors preserved", display.Color == "#10893E"
        && DashboardPresentation.ColorFor(TaskStatusNames.Completed) == "#0078D4"
        && DashboardPresentation.ColorFor(TaskStatusNames.Failed) == "#D13438");
    Check("dashboard plan and duration retained", display.PlanPercent == 100 && display.PlanLabel == "当前计划  1 / 1 步"
        && display.Duration == "15 分 0 秒");
    var disconnected = new DashboardTaskRecord(Record()) { Disconnected = true };
    Check("offline record remains explicitly stale", disconnected.Color == "#8A8886"
        && disconnected.EffectiveStatus == TaskStatusNames.Offline && disconnected.OverviewText.StartsWith("上次成功读取"));
    Check("collector JSON excludes host-derived and UI values",
        !JsonSerializer.Serialize(source, SharedJson.Options).Contains("\"host\"")
        && !JsonSerializer.Serialize(source, SharedJson.Options).Contains("\"color\""));
    var parsed = JsonSerializer.Deserialize<CollectorResult>(
        """{"schemaVersion":1,"collectedAt":123,"tasks":[{"id":"t","turnId":"round","finalReport":"answer","finalReportTurnId":"round","finalReportTruncated":true}],"warnings":[],"futureField":true}""",
        SharedJson.Options)!;
    Check("collector schema preserves round report and ignores unknown fields",
        parsed.Tasks.Single() is { Id: "t", TurnId: "round", FinalReport: "answer", FinalReportTurnId: "round", FinalReportTruncated: true });

    var origin = DateTimeOffset.FromUnixTimeSeconds(3000);
    var transitions = new NotificationTransitions();
    var current = Record(TaskStatusNames.Active);
    var history = Record(TaskStatusNames.Completed, id: "historical");
    Check("initial baseline is silent", transitions.Observe(new[] { current, history }, origin).Count == 0);
    current.Status = TaskStatusNames.Completed;
    Check("same round completion emits once", transitions.Observe(new[] { current, history }, origin.AddSeconds(10)).Single().Id == current.Id);
    Check("duplicate snapshot is silent", transitions.Observe(new[] { current, history }, origin.AddSeconds(20)).Count == 0);
    current.TurnId = "turn-2"; current.StartedAt = 3030; current.Status = TaskStatusNames.Active;
    transitions.Observe(new[] { current }, origin.AddSeconds(30));
    current.Status = TaskStatusNames.Unknown;
    Check("temporary unknown status does not notify", transitions.Observe(new[] { current }, origin.AddSeconds(40)).Count == 0);
    current.Status = TaskStatusNames.Failed;
    Check("unknown does not hide eventual failure", transitions.Observe(new[] { current }, origin.AddSeconds(50)).Count == 1);
    current.TurnId = "turn-3"; current.Status = TaskStatusNames.Active; current.StartedAt = 3060;
    transitions.Observe(new[] { current }, origin.AddSeconds(60));
    current.Disconnected = true;
    Check("offline snapshot is silent", transitions.Observe(new[] { current }, origin.AddSeconds(70)).Count == 0);
    current.Disconnected = false; current.Status = TaskStatusNames.Completed;
    Check("connection recovery does not replay completion", transitions.Observe(new[] { current }, origin.AddSeconds(80)).Count == 0);
    var shortTask = Record(TaskStatusNames.Completed, id: "short"); shortTask.StartedAt = 3085;
    Check("new short round between polls emits", transitions.Observe(new[] { current, shortTask }, origin.AddSeconds(90)).Single().Id == "short");
    Check("independent host baseline is silent", new NotificationTransitions().Observe(new[] { shortTask }, origin.AddSeconds(100)).Count == 0);
    var hostTransitions = new NotificationTransitions();
    var local = Record();
    var remote = Record(host: "worker");
    hostTransitions.Observe(new[] { local, remote }, origin);
    remote.Status = TaskStatusNames.Completed;
    Check("same task ID on different hosts does not collide",
        hostTransitions.Observe(new[] { local, remote }, origin.AddSeconds(10)).Single().Host == "worker");

    var settings = new Settings { LocalEnabled = true, RemoteHosts = new() { "worker" } };
    int round = 0;
    var fake = new FakeCollector((host, _, _) => Task.FromResult(new HostSnapshot
    {
        Name = host, Error = host == "worker" && round is 1 or 4 ? "synthetic offline" : null,
        LastSuccess = host == "worker" && round is 1 or 4 ? null : origin.AddSeconds(round),
        Tasks = host == "worker" && round is 1 or 4 ? new() : new() { Record(host: host) }
    }));
    using (var monitor = new MonitorService(settings, fake))
    {
        var first = (await monitor.RefreshAsync())!;
        Check("collects each configured host using injected reader",
            first.Select(h => h.Name).SequenceEqual(new[] { "本机", "worker" }) && fake.Calls.Count == 2);
        round = 1;
        var lost = (await monitor.RefreshAsync())!.Single(h => h.Name == "worker");
        Check("failed host retains prior tasks as stale",
            lost.Error == "synthetic offline" && lost.LastSuccess == origin && lost.Tasks.Single().Disconnected
            && lost.Tasks.Single().FinalReport == "最终报告");
        round = 2;
        var recovered = (await monitor.RefreshAsync())!.Single(h => h.Name == "worker");
        Check("recovery replaces stale records with fresh records",
            recovered.Error == null && recovered.LastSuccess == origin.AddSeconds(2) && !recovered.Tasks.Single().Disconnected);
        round = 3; settings.RemoteHosts.Clear();
        Check("removed host disappears", (await monitor.RefreshAsync())!.All(h => h.Name != "worker"));
        round = 4; settings.RemoteHosts.Add("worker");
        Check("removed host cache does not resurrect on re-add failure", (await monitor.RefreshAsync())!.Single(h => h.Name == "worker").Tasks.Count == 0);
    }
    var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blocking = new FakeCollector(async (host, _, ct) =>
    {
        gateEntered.SetResult();
        await gateRelease.Task.WaitAsync(ct);
        return new HostSnapshot { Name = host };
    });
    using (var monitor = new MonitorService(new Settings { LocalEnabled = true }, blocking))
    {
        var pending = monitor.RefreshAsync();
        await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("overlapping refresh does not start duplicate reads", await monitor.RefreshAsync() == null && blocking.Calls.Count == 1);
        gateRelease.SetResult();
        await pending;
    }

    var vmSettings = new Settings { LocalEnabled = true };
    var vmReader = new FakeCollector((host, _, _) => Task.FromResult(new HostSnapshot
    {
        Name = host, LastSuccess = DateTimeOffset.UtcNow,
        Tasks = new() { Record(host: host) }
    }));
    using (var vm = new DashboardViewModel(vmSettings, vmReader))
    {
        int notices = 0;
        vm.Notify += _ => notices++;
        await vm.RefreshAsync();
        Check("VM receives dashboard wrappers without changing raw collector types",
            vm.AllTasks.Single() is DashboardTaskRecord && vm.Hosts.Single() is DashboardHostSnapshot
            && vm.AllTasks.Single().Plan.Single() is DashboardPlanStep);
        Check("VM initial load keeps notifications quiet", notices == 0);
        Check("VM runtime detail binding retains UI fields",
            vm.SelectedTask is DashboardTaskRecord { Color: "#10893E", PlanPercent: 100 });
        vm.Search = "missing"; vm.Filter();
        Check("VM filtering preserved", vm.VisibleTasks.Count == 0 && vm.SelectedTask == null);
        vm.Select(vm.AllTasks.Single());
        Check("VM task selection preserved", vm.VisibleTasks.Count == 1 && vm.SelectedTask?.Id == "one");
    }
}
catch (Exception ex)
{
    checks["unhandled failure: " + ex.Message] = false;
    Console.Error.WriteLine(ex);
}

var report = new { passed = checks.Values.All(v => v), count = checks.Count, checks, realCollectorProcesses = 0, realNetworkCalls = 0 };
string json = JsonSerializer.Serialize(report, SharedJson.Options);
if (args.Length > 0)
{
    Directory.CreateDirectory(args[0]);
    await File.WriteAllTextAsync(Path.Combine(args[0], "readonly-checks.json"), json);
}
Console.WriteLine(json);
return report.passed ? 0 : 1;

sealed class FakeCollector(Func<string, ICollectorSettings, CancellationToken, Task<HostSnapshot>> read) : IHostCollector
{
    public List<string> Calls { get; } = new();
    public Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken cancellationToken)
    {
        Calls.Add(host);
        return read(host, settings, cancellationToken);
    }
}
