using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Codex.Protocol;
using CodexDashboard;

return await new PackageChecks().RunAsync(args);

sealed class PackageChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly List<Child> children = [];
    private readonly List<object> results = [];
    private string output = "";
    private string runId = Guid.NewGuid().ToString("N");
    private string dashboardSource = "", assistantSource = "";

    internal async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--self-test" && args[1] == "--output")
                return await ValidateHookAsync(args[2]);
            if (args.Length is not (6 or 7) || args[0] != "--dashboard-package"
                || args[2] != "--assistant-package" || args[4] != "--output"
                || args.Length == 7 && args[6] is not ("--preflight-only" or "--installer-only"))
                throw new ArgumentException("Use --dashboard-package <absolute> --assistant-package <absolute> --output <new absolute> [--preflight-only|--installer-only].");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows distribution checks only.");
            dashboardSource = ExistingDirectory(args[1]);
            assistantSource = ExistingDirectory(args[3]);
            output = FullPath(args[5]);
            if (Directory.Exists(output) || File.Exists(output) || IsWithin(dashboardSource, output) || IsWithin(assistantSource, output))
                throw new ArgumentException("Output must be new and outside either source package.");
            AssertNoReparse(Path.GetDirectoryName(output)!);
            Directory.CreateDirectory(output);
            Write("run.json", new { runId, checkKind = "independent-package-isolated-check", startedAt = DateTimeOffset.UtcNow,
                productionLifecycleExercised = false, installerUninstallTested = false, legacyReceiverHandoffTested = false });
            var dashboardPreflight = Preflight(dashboardSource, ProtocolProduct.Dashboard);
            var assistantPreflight = Preflight(assistantSource, ProtocolProduct.Assistant);
            Write("preflight.json", new { dashboard = dashboardPreflight, assistant = assistantPreflight });
            if (args.Length == 7 && args[6] == "--preflight-only") { Write("results.json", new { passed = true, preflightOnly = true }); return 0; }
            bool installedOnly = args.Length == 7 && args[6] == "--installer-only";
            foreach (var target in new[] { ProtocolProduct.Dashboard, ProtocolProduct.Assistant })
                foreach (string action in installedOnly ? new[] { "installer-uninstall" } : new[] { "graceful-stop", "own-process-crash", "package-relocation" })
                    await ScenarioAsync(target, action);
            Write("results.json", new { passed = true, scenarioCount = results.Count, results,
                scope = installedOnly ? "installed-package-isolated-check" : "independent-package-isolated-check", productionLifecycleExercised = false,
                installerUninstallTested = installedOnly, legacyReceiverHandoffTested = false,
                fixtureTaskCount = 120, productionSshPerformanceBaseline = "pending" });
            Console.WriteLine($"Completed {results.Count} isolated scenarios; installer tested: {installedOnly}. Production lifecycle remains pending.");
            return 0;
        }
        catch (Exception ex)
        {
            if (output.Length > 0 && Directory.Exists(output))
                Write("results.json", new { passed = false, errorType = ex.GetType().Name,
                    error = ex is CheckFailure ? ex.Message : "runner_or_preflight_failed", completedScenarios = results });
            Console.WriteLine("FAILED " + ex.GetType().Name + (ex is CheckFailure ? ": " + ex.Message : ""));
            return 1;
        }
        finally
        {
            foreach (var child in children)
                try { await StopOwnedAsync(child); } catch { }
        }
    }

    private async Task<int> ValidateHookAsync(string directory)
    {
        output = FullPath(directory);
        Require(!Directory.Exists(output) && !File.Exists(output), "self_test_output_must_be_new");
        AssertNoReparse(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        foreach (string violation in new[] { "channelsEnabled", "modelEnabled", "taskControlEnabled",
            "realLocalCollectionEnabled", "missingExplicitDisabledFlag", "remoteHosts",
            "wrongProduct", "wrongPackage", "extraDataFile", "badFixture" })
        {
            string data = Path.Combine(output, violation);
            string evidence = Path.Combine(data, "evidence");
            Directory.CreateDirectory(evidence);
            var manifest = new Dictionary<string, object?>
            {
                ["version"] = 1, ["purpose"] = "independent-package-isolated-check", ["runId"] = runId,
                ["product"] = "Dashboard", ["dataDirectory"] = data, ["evidenceDirectory"] = evidence,
                ["packageDirectory"] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                ["channelsEnabled"] = false, ["modelEnabled"] = false, ["taskControlEnabled"] = false,
                ["realLocalCollectionEnabled"] = false, ["remoteHosts"] = Array.Empty<string>(), ["maximumSeconds"] = 5
            };
            if (violation is "channelsEnabled" or "modelEnabled" or "taskControlEnabled" or "realLocalCollectionEnabled")
                manifest[violation] = true;
            if (violation == "missingExplicitDisabledFlag") manifest.Remove("modelEnabled");
            if (violation == "remoteHosts") manifest["remoteHosts"] = new[] { "synthetic-host-not-to-connect" };
            if (violation == "wrongProduct") manifest["product"] = "Assistant";
            if (violation == "wrongPackage") manifest["packageDirectory"] = data;
            if (violation == "extraDataFile") File.WriteAllText(Path.Combine(data, "unknown.txt"), "synthetic");
            File.WriteAllText(Path.Combine(data, "lifecycle.manifest.json"), JsonSerializer.Serialize(manifest, Json));
            File.WriteAllText(Path.Combine(data, "fixture.json"), JsonSerializer.Serialize(new
            {
                version = 1, source = "synthetic-lifecycle-fixture", taskCount = violation == "badFixture" ? 0 : 120
            }, Json));
            int code = await ProductLifecycleTest.RunAsync(
                ["--isolated-lifecycle-test", evidence, "--data-dir", data], ProtocolProduct.Dashboard);
            Require(code == 2 && !Directory.EnumerateFileSystemEntries(evidence).Any(), "unsafe_hook_input_was_not_rejected");
            checks.Add(new { violation, passed = true, code, processStarted = false, endpointStarted = false });
        }
        Require(await ProductLifecycleTest.RunAsync(["--isolated-lifecycle-test"], ProtocolProduct.Dashboard) == 2,
            "missing_explicit_paths_not_rejected");
        checks.Add(new { violation = "missingArguments", passed = true, code = 2, processStarted = false, endpointStarted = false });
        Write("checks.json", new { passed = true, count = checks.Count, checks });
        Console.WriteLine($"PASS {checks.Count} hook safety checks; no child process or protocol endpoint started.");
        return 0;
    }

    private async Task ScenarioAsync(ProtocolProduct targetProduct, string action)
    {
        string name = targetProduct.ToString().ToLowerInvariant() + "-" + action;
        string scenario = Path.Combine(output, name);
        Directory.CreateDirectory(scenario);
        var dashboard = action == "installer-uninstall"
            ? await PrepareInstalledAsync(scenario, ProtocolProduct.Dashboard, dashboardSource)
            : Prepare(scenario, ProtocolProduct.Dashboard, dashboardSource);
        var assistant = action == "installer-uninstall"
            ? await PrepareInstalledAsync(scenario, ProtocolProduct.Assistant, assistantSource)
            : Prepare(scenario, ProtocolProduct.Assistant, assistantSource);
        Start(dashboard); Start(assistant);
        await WaitHeartbeatAsync(dashboard, 3);
        await WaitHeartbeatAsync(assistant, 3);
        await ProbeAsync(dashboard);
        await ProbeAsync(assistant);
        Require(ReadStartup(dashboard).GetProperty("singletonName").GetString() != ReadStartup(assistant).GetProperty("singletonName").GetString(),
            "products_must_have_distinct_test_singletons");
        Require(ReadStartup(dashboard).GetProperty("endpoint").GetString() != ReadStartup(assistant).GetProperty("endpoint").GetString(),
            "products_must_have_distinct_test_endpoints");
        if (action == "graceful-stop")
        {
            await CheckDuplicateAsync(dashboard);
            await CheckDuplicateAsync(assistant);
        }
        var target = targetProduct == ProtocolProduct.Dashboard ? dashboard : assistant;
        var survivor = targetProduct == ProtocolProduct.Dashboard ? assistant : dashboard;
        var dualStart = ReadHeartbeat(survivor);
        await WaitHeartbeatAsync(survivor, dualStart.Sequence + 6);
        var dualEnd = ReadHeartbeat(survivor);
        var otherHealthBefore = await ProbeAsync(survivor);
        long before = dualEnd.Sequence;
        DateTimeOffset disruptionAt = DateTimeOffset.UtcNow;
        if (action == "own-process-crash")
        {
            Require(target.Process != null && !target.Process.HasExited, "target_was_not_running");
            // This exact Process object was started by this runner. No process-name or arbitrary PID lookup.
            target.Process!.Kill(entireProcessTree: false);
            await target.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(!File.Exists(Path.Combine(target.Evidence, "stopped.json")), "crash_unexpectedly_reported_graceful_stop");
        }
        else
        {
            await StopOwnedAsync(target);
            Require(target.Process!.ExitCode == 0, "target_graceful_exit_failed");
            var stopped = ReadObject(Path.Combine(target.Evidence, "stopped.json"));
            Require(stopped.GetProperty("reason").GetString() == "stop_file", "target_did_not_obey_own_stop_file");
            if (action == "installer-uninstall")
            {
                string installRoot = Path.Combine(scenario, "installed", target.Product.ToString());
                AssertRunChild(installRoot);
                string beforeData = DataFingerprint(target.Data);
                await RunScriptAsync(Path.Combine(installRoot, "Uninstall-Product.ps1"),
                    ["-InstallationDirectory", installRoot], Path.Combine(scenario, "uninstall"));
                Require(!Directory.Exists(installRoot), "installed_product_not_removed");
                Require(DataFingerprint(target.Data) == beforeData, "uninstall_changed_isolated_data");
                Require(Directory.Exists(survivor.Package), "uninstall_removed_other_product");
            }
            if (action == "package-relocation")
            {
                string relocated = Path.Combine(scenario, "removed-package-" + target.Product);
                AssertRunChild(target.Package); AssertRunChild(relocated);
                Require(!Directory.Exists(relocated), "relocated_path_already_exists");
                Directory.Move(target.Package, relocated);
                Require(!Directory.Exists(target.Package) && Directory.Exists(relocated), "package_relocation_failed");
            }
        }
        await WaitHeartbeatAsync(survivor, before + 4);
        var soloStart = ReadHeartbeat(survivor);
        var healthAfter = await ProbeAsync(survivor);
        Require(healthAfter > otherHealthBefore && healthAfter >= disruptionAt, "survivor_status_did_not_advance");
        await WaitHeartbeatAsync(survivor, soloStart.Sequence + 6);
        var soloEnd = ReadHeartbeat(survivor);
        Require(await ProbeAsync(survivor) > healthAfter, "survivor_status_stopped_after_first_check");
        var snapshot = ReadObject(Path.Combine(survivor.Evidence, "snapshotcheck.json"));
        Require(snapshot.GetProperty("passed").GetBoolean() && snapshot.GetProperty("taskCount").GetInt32() == 120,
            "survivor_fixture_snapshot_invalid");
        Require(!await EndpointAvailableAsync(target), "stopped_target_endpoint_still_available");
        Require(survivor.Process != null && !survivor.Process.HasExited, "survivor_exited");
        await StopOwnedAsync(survivor);
        Require(survivor.Process!.ExitCode == 0, "survivor_graceful_cleanup_failed");
        var result = new
        {
            name, passed = true, disruptedProduct = target.Product.ToString(), action,
            targetPid = target.Process!.Id, survivorPid = survivor.Process.Id,
            survivorProduct = survivor.Product.ToString(), heartbeatBefore = before, heartbeatAfter = soloEnd.Sequence,
            statusBefore = otherHealthBefore, statusAfter = healthAfter, targetEndpointGone = true,
            movedPackageOnly = action == "package-relocation", installerUninstallTested = action == "installer-uninstall",
            dualFixturePhase = Measure(dualStart, dualEnd), survivorOnlyFixturePhase = Measure(soloStart, soloEnd),
            performanceScope = "120 in-memory synthetic tasks at a 250ms interval, including evidence and protocol overhead; not a production SSH baseline"
        };
        results.Add(result);
        Write(Path.Combine(name, "scenario.json"), result);
        Console.WriteLine("PASS " + name);
    }

    private static object Measure(Heartbeat first, Heartbeat last)
    {
        double wall = (last.ObservedAt - first.ObservedAt).TotalMilliseconds;
        return new
        {
            elapsedMs = wall, refreshes = last.Sequence - first.Sequence,
            cpuMs = last.CpuMs - first.CpuMs, equivalentSingleCoreCpuPercent = (last.CpuMs - first.CpuMs) / wall * 100,
            lastWorkingSetBytes = last.WorkingSetBytes, lastCollectionMs = last.CollectionMs
        };
    }

    private Child Prepare(string scenario, ProtocolProduct product, string source, string? installedPackage = null)
    {
        string package = installedPackage ?? Path.Combine(scenario, "packages", product.ToString());
        string data = Path.Combine(scenario, "data", product.ToString());
        string evidence = Path.Combine(data, "evidence");
        if (installedPackage == null) CopyPackage(source, package);
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(data, "lifecycle.manifest.json"), JsonSerializer.Serialize(new
        {
            version = 1, purpose = "independent-package-isolated-check", runId, product = product.ToString(),
            dataDirectory = data, evidenceDirectory = evidence, packageDirectory = package,
            channelsEnabled = false, modelEnabled = false, taskControlEnabled = false,
            realLocalCollectionEnabled = false, remoteHosts = Array.Empty<string>(), maximumSeconds = 90
        }, Json));
        File.WriteAllText(Path.Combine(data, "fixture.json"), JsonSerializer.Serialize(new
        {
            version = 1, source = "synthetic-lifecycle-fixture", taskCount = 120
        }, Json));
        var child = new Child(product, package, data, evidence);
        children.Add(child);
        return child;
    }

    private async Task<Child> PrepareInstalledAsync(string scenario, ProtocolProduct product, string source)
    {
        string installRoot = Path.Combine(scenario, "installed", product.ToString());
        string data = Path.Combine(scenario, "data", product.ToString());
        AssertRunChild(installRoot); AssertRunChild(data);
        await RunScriptAsync(Path.Combine(source, "Install-Product.ps1"),
            ["-PackageDirectory", source, "-InstallationDirectory", installRoot, "-DataDirectory", data,
                "-ProtocolProfile", "synthetic-installer-" + runId], Path.Combine(scenario, "install-" + product));
        var state = ReadObject(Path.Combine(installRoot, "installation.json"));
        string release = state.GetProperty("activeRelease").GetString()!;
        Require(Guid.TryParseExact(release, "N", out _), "invalid_installed_release");
        return Prepare(scenario, product, source, Path.Combine(installRoot, "releases", release));
    }

    private async Task RunScriptAsync(string script, string[] arguments, string evidencePrefix)
    {
        AssertRunChild(evidencePrefix);
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", script }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CheckFailure("installer_process_not_started");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await File.WriteAllTextAsync(evidencePrefix + ".stdout.log", await stdout);
            await File.WriteAllTextAsync(evidencePrefix + ".stderr.log", await stderr);
            Require(process.ExitCode == 0, "installer_process_failed");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); }
        }
    }

    private string DataFingerprint(string directory)
    {
        AssertRunChild(directory);
        return string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path)
                + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    }

    private void Start(Child child)
    {
        AssertRunChild(child.Package); AssertRunChild(child.Data);
        var start = new ProcessStartInfo(Path.Combine(child.Package, "Codex" + child.Product + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = child.Package
        };
        foreach (string arg in new[] { "--isolated-lifecycle-test", child.Evidence, "--data-dir", child.Data })
            start.ArgumentList.Add(arg);
        start.Environment["CODEX_HOME"] = Path.Combine(child.Data, "unused-codex");
        child.Process = Process.Start(start) ?? throw new CheckFailure("process_start_failed");
        child.StartTimeUtc = child.Process.StartTime.ToUniversalTime();
        Write(Path.GetRelativePath(output, Path.Combine(child.Evidence, "runner-process." + child.Process.Id + ".json")),
            new { processId = child.Process.Id, child.StartTimeUtc, executable = start.FileName, ownedByRunner = true });
    }

    private async Task CheckDuplicateAsync(Child first)
    {
        var duplicate = new Child(first.Product, first.Package, first.Data, first.Evidence);
        children.Add(duplicate);
        Start(duplicate);
        await duplicate.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(duplicate.Process.ExitCode == 10, "same_data_directory_singleton_not_rejected");
        Require(File.Exists(Path.Combine(first.Evidence, $"duplicate.{duplicate.Process.Id}.json")), "duplicate_rejection_evidence_missing");
        Require(!first.Process!.HasExited, "duplicate_stopped_first_process");
    }

    private async Task StopOwnedAsync(Child child)
    {
        if (child.Process == null || child.Process.HasExited) return;
        AssertRunChild(child.Data);
        Require(child.Process.StartTime.ToUniversalTime() == child.StartTimeUtc, "owned_process_identity_changed");
        File.WriteAllText(Path.Combine(child.Data, "stop.request"), "stop this isolated child only");
        try { await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill(entireProcessTree: false);
                await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
    }

    private static async Task WaitHeartbeatAsync(Child child, long minimumSequence)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (child.Process!.HasExited) throw new CheckFailure("isolated_process_exited_before_heartbeat");
            if (File.Exists(Path.Combine(child.Evidence, "failure.json"))) throw new CheckFailure("isolated_hook_reported_failure");
            try
            {
                var heartbeat = ReadHeartbeat(child);
                if (heartbeat.Sequence >= minimumSequence)
                {
                    Require(heartbeat.ProcessId == child.Process.Id && heartbeat.SnapshotValid, "heartbeat_identity_or_snapshot_mismatch");
                    return;
                }
            }
            catch (IOException) { }
            await Task.Delay(100);
        }
        throw new CheckFailure("heartbeat_timeout");
    }

    private static async Task<DateTimeOffset> ProbeAsync(Child child)
    {
        var startup = ReadStartup(child);
        string endpoint = startup.GetProperty("endpoint").GetString()!;
        string profile = startup.GetProperty("profile").GetString()!;
        Require(profile.StartsWith("lifecycle-", StringComparison.Ordinal)
            && endpoint == LocalProtocolEndpoint.ForCurrentUser(child.Product, profile), "unexpected_test_endpoint");
        Require(startup.GetProperty("processId").GetInt32() == child.Process!.Id
            && !startup.GetProperty("channelsConstructed").GetBoolean()
            && !startup.GetProperty("modelConstructed").GetBoolean()
            && !startup.GetProperty("credentialsRead").GetBoolean(), "unsafe_startup_report");
        await using var client = await LocalProtocolClient.ConnectAsync(endpoint,
            new() { Product = child.Product == ProtocolProduct.Dashboard ? ProtocolProduct.Assistant : ProtocolProduct.Dashboard,
                ProductVersion = "isolated-test", InstanceId = Guid.NewGuid() }, child.Product);
        var health = await client.GetHealthAsync();
        Require(health.Readiness == Availability.Available && DateTimeOffset.UtcNow - health.ObservedAt < TimeSpan.FromSeconds(4),
            "status_unavailable_or_stale");
        if (child.Product == ProtocolProduct.Assistant)
        {
            var assistant = await client.GetAssistantStatusAsync();
            Require(assistant.WeChat == ConnectionState.NotConfigured && assistant.Model == Availability.Unavailable
                && assistant.QueuedCount == 0, "assistant_real_services_not_disabled");
        }
        return health.ObservedAt;
    }

    private static async Task<bool> EndpointAvailableAsync(Child child)
    {
        try
        {
            string endpoint = ReadStartup(child).GetProperty("endpoint").GetString()!;
            await using var client = await LocalProtocolClient.ConnectAsync(endpoint,
                new() { Product = ProtocolProduct.Dashboard, ProductVersion = "isolated-test", InstanceId = Guid.NewGuid() },
                child.Product, new() { ConnectTimeout = TimeSpan.FromMilliseconds(250) });
            return true;
        }
        catch (IOException) { return false; }
    }

    private object Preflight(string source, ProtocolProduct product)
    {
        var files = PackageFiles(source);
        string ownName = "Codex" + product;
        string otherName = product == ProtocolProduct.Dashboard ? "CodexAssistant" : "CodexDashboard";
        Require(File.Exists(Path.Combine(source, ownName + ".exe")) && File.Exists(Path.Combine(source, ownName + ".dll")),
            "expected_distribution_executable_missing");
        Require(!files.Any(f => Path.GetFileNameWithoutExtension(f).Equals(otherName, StringComparison.OrdinalIgnoreCase)),
            "distribution_contains_other_product");
        string assembly = Path.Combine(source, ownName + ".dll");
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
        var typeNames = metadata.TypeDefinitions.Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Name)).ToArray();
        Require(!references.Contains(otherName, StringComparer.OrdinalIgnoreCase), "cross_product_assembly_reference");
        if (product == ProtocolProduct.Dashboard)
            Require(!typeNames.Intersect(new[] { "WeChatService", "ChatGptAssistant", "TaskControlQueue", "TaskTransport", "AssistantApp" }).Any(),
                "dashboard_contains_assistant_implementation");
        Require(typeNames.Contains("ProductLifecycleTest"), "safe_hook_type_missing");
        string expectedApp = product == ProtocolProduct.Dashboard ? "App" : "AssistantApp";
        List<string>? startupCalls = null;
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (!metadata.GetString(type.Name).StartsWith("<OnStartup>d__", StringComparison.Ordinal)
                || type.GetDeclaringType().IsNil
                || metadata.GetString(metadata.GetTypeDefinition(type.GetDeclaringType()).Name) != expectedApp) continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name) != "MoveNext" || method.RelativeVirtualAddress == 0) continue;
                startupCalls = IlCalls(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, metadata);
            }
        }
        Require(startupCalls != null, "startup_state_machine_missing");
        int requested = startupCalls!.FindIndex(m => m == "ProductLifecycleTest.IsRequested");
        int hook = startupCalls.FindIndex(m => m == "ProductLifecycleTest.RunAsync");
        int sensitive = startupCalls.FindIndex(m => m is "Mutex..ctor" or "Settings.SetDataDirectory"
            or "DashboardSettings.SetDataDirectory" or "AssistantSettings.SetDataDirectory"
            or "Settings.Load" or "DashboardSettings.Load" or "AssistantSettings.Load"
            or "MigrationStartup.AcquireRuntimeLease" or "AssistantTestDispatch.TryRunAsync");
        Require(requested >= 0 && hook > requested && (sensitive < 0 || hook < sensitive), "safe_hook_not_before_production_startup");
        return new
        {
            product = product.ToString(), fileCount = files.Length, assemblyReferences = references,
            otherProductReference = false, earlyHookVerified = true,
            startupCallsBeforeHook = startupCalls.Take(hook + 1).ToArray(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
            settingsLoadedByHook = false, inspection = "PE metadata and startup IL call ordering, plus reviewed source"
        };
    }

    private static List<string> IlCalls(byte[] il, MetadataReader metadata)
    {
        var result = new List<string>();
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => unchecked((ushort)c.Value));
        for (int index = 0; index < il.Length;)
        {
            ushort code = il[index++];
            if (code == 0xfe) code = (ushort)(0xfe00 | il[index++]);
            var op = codes[code];
            if (op.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, index);
                var handle = MetadataTokens.EntityHandle(token);
                string? name = null;
                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                    name = metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name) + "." + metadata.GetString(method.Name);
                }
                else if (handle.Kind == HandleKind.MemberReference)
                {
                    var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                    string? parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name),
                        HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name),
                        _ => null
                    };
                    if (parent != null) name = parent + "." + metadata.GetString(member.Name);
                }
                if (name != null) result.Add(name);
            }
            index += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index),
                _ => 4
            };
        }
        return result;
    }

    private static string[] PackageFiles(string source)
    {
        var files = new List<string>();
        var directories = new Stack<string>(); directories.Push(source);
        while (directories.TryPop(out var directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "package_contains_redirected_path");
                if ((attributes & FileAttributes.Directory) != 0) { directories.Push(entry); continue; }
                string name = Path.GetFileName(entry);
                Require(!name.Contains("credentials", StringComparison.OrdinalIgnoreCase)
                    && !new[] { "settings.json", "dashboard.settings.json", "task-control.state", "memory.state", "data-directory.txt" }
                        .Contains(name, StringComparer.OrdinalIgnoreCase), "package_contains_user_data_or_selector");
                files.Add(entry);
            }
        return files.ToArray();
    }
    private void CopyPackage(string source, string destination)
    {
        AssertRunChild(destination);
        Require(!Directory.Exists(destination), "package_copy_destination_exists");
        foreach (string file in PackageFiles(source))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            AssertRunChild(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }
    private void AssertRunChild(string path)
    {
        Require(IsWithin(output, FullPath(path)), "path_escaped_test_run");
        string? existing = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing != null) AssertNoReparse(existing);
    }
    private static bool IsWithin(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string FullPath(string path)
    {
        Require(Path.IsPathFullyQualified(path) && !path.Any(char.IsControl), "absolute_path_required");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static string ExistingDirectory(string path)
    {
        string full = FullPath(path);
        Require(Directory.Exists(full), "source_directory_missing");
        AssertNoReparse(full); return full;
    }
    private static void AssertNoReparse(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            Require((directory.Attributes & FileAttributes.ReparsePoint) == 0, "redirected_path_rejected");
    }
    private static JsonElement ReadObject(string path)
    {
        // The producer replaces snapshots atomically. Allow replacement while reading the old handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<JsonElement>(stream);
    }
    private static JsonElement ReadStartup(Child child) => ReadObject(Path.Combine(child.Evidence, "startup.json"));
    private static Heartbeat ReadHeartbeat(Child child)
    {
        var item = ReadObject(Path.Combine(child.Evidence, "heartbeat.json"));
        return new(item.GetProperty("sequence").GetInt64(), item.GetProperty("observedAt").GetDateTimeOffset(),
            item.GetProperty("processId").GetInt32(), item.GetProperty("snapshotValid").GetBoolean(),
            item.GetProperty("cpuMs").GetDouble(), item.GetProperty("workingSetBytes").GetInt64(),
            item.GetProperty("collectionMs").GetDouble());
    }
    private void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Json));
    private static void Require(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
    private sealed record Heartbeat(long Sequence, DateTimeOffset ObservedAt, int ProcessId, bool SnapshotValid,
        double CpuMs, long WorkingSetBytes, double CollectionMs);
    private sealed record Child(ProtocolProduct Product, string Package, string Data, string Evidence)
    {
        internal Process? Process { get; set; }
        internal DateTime StartTimeUtc { get; set; }
    }
}

sealed class CheckFailure(string message) : Exception(message);
