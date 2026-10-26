using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Migration;
using CodexDashboard;

if (args.Length != 2 || args[0] != "--output" || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Use --output <new absolute directory>; all test data is synthetic.");
string output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new ArgumentException("Choose a fresh output directory.");
Directory.CreateDirectory(output);
var results = new List<object>();
int failed = 0;
async Task Test(string name, Func<Fixture, Task> test)
{
    var fixture = new Fixture(Path.Combine(output, $"case-{results.Count + 1:D3}"));
    var started = DateTimeOffset.UtcNow;
    try
    {
        await test(fixture);
        results.Add(new { name, passed = true, elapsedMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds });
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failed++;
        // Error type only: even failures must not accidentally dump a protected fixture payload.
        results.Add(new { name, passed = false, errorType = ex.GetType().Name });
        Console.WriteLine("FAIL " + name + " (" + ex.GetType().Name + ")");
    }
    File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(
        new { syntheticOnly = true, count = results.Count, failed, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
}
static void Require(bool condition)
{
    if (!condition) throw new TestFailure();
}
static async Task ExpectCrash(Func<Task> action)
{
    try { await action(); } catch (InjectedCrash) { return; }
    throw new TestFailure();
}

await Test("discovery and planning use metadata without opening protected payloads", f =>
{
    using var lockedSettings = new FileStream(f.Source("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
    using var lockedCredential = new FileStream(f.Source("chatgpt.credentials"), FileMode.Open, FileAccess.Read, FileShare.None);
    var located = MigrationDiscovery.Locate([f.Request.SourceDirectory]);
    Require(located.Single().HasSettings && located.Single().HasCredentialStore);
    var plan = new MigrationEngine().Plan(f.Request);
    Require(plan.RelativeFiles.Count == 5 && plan.TotalBytes > 0);
    Require(!Directory.Exists(f.Request.TargetDirectory) && !Directory.Exists(f.Request.TransactionDirectory));
    return Task.CompletedTask;
});
await Test("modern assistant state and settings survive migration and rollback", async f =>
{
    string hash = new string('b', 64);
    string[] paths = ["conversations.state", "owned-control.json", "existing-control.json", "history-archive/catalogue.state",
        "history-archive/archive.owner", "history-archive/" + new string('c', 32) + ".volume",
        "owned-state/" + hash + ".owned", "attachments/" + hash + "/" + hash + ".state"];
    foreach (var path in paths) { Directory.CreateDirectory(Path.GetDirectoryName(f.Source(path))!); File.WriteAllText(f.Source(path), "SYNTHETIC_CURRENT_" + path); }
    var settings = JsonNode.Parse(File.ReadAllText(f.Source("settings.json")))!.AsObject();
    settings["historyArchiveEnabled"] = true; settings["inputMergeWindowMilliseconds"] = 1250;
    settings["maxConcurrentExecutions"] = 2; settings["media"] = new JsonObject { ["attachments"] = true };
    File.WriteAllText(f.Source("settings.json"), settings.ToJsonString());
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    Require(paths.All(p => File.ReadAllBytes(f.Source(p)).SequenceEqual(File.ReadAllBytes(f.Target(p)))));
    var target = JsonNode.Parse(File.ReadAllText(f.Target("settings.json")))!;
    Require(target["historyArchiveEnabled"]!.GetValue<bool>() && target["inputMergeWindowMilliseconds"]!.GetValue<int>() == 1250
        && target["maxConcurrentExecutions"]!.GetValue<int>() == 2 && target["media"]!["attachments"]!.GetValue<bool>());
    Require((await engine.RollbackAsync(f.Request)).Succeeded);
    Require(paths.All(p => File.ReadAllText(f.Source(p)) == "SYNTHETIC_CURRENT_" + p));
});
await Test("new conversation after migration cannot be overwritten by rollback", async f =>
{
    File.WriteAllText(f.Source("conversations.state"), "SYNTHETIC_OLD");
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    File.WriteAllText(f.Target("conversations.state"), "SYNTHETIC_NEW_MESSAGE_AND_SUBSCRIPTION_PROGRESS");
    var rollback = await engine.RollbackAsync(f.Request);
    Require(!rollback.Succeeded && File.ReadAllText(f.Target("conversations.state")) == "SYNTHETIC_NEW_MESSAGE_AND_SUBSCRIPTION_PROGRESS");
});
await Test("unknown assistant data is never silently omitted", f =>
{
    Directory.CreateDirectory(f.Source("history-archive")); File.WriteAllText(f.Source("history-archive/new-format.bin"), "SYNTHETIC");
    bool rejected = false; try { new MigrationEngine().Plan(f.Request); } catch { rejected = true; }
    Require(rejected && !Directory.Exists(f.Request.TargetDirectory)); return Task.CompletedTask;
});
await Test("default guard refuses before copying or creating destination", async f =>
{
    var result = await new MigrationEngine().MigrateAsync(f.Request);
    Require(result.Code == "receiver_guard_required" && !result.Succeeded);
    Require(!Directory.Exists(f.Request.TargetDirectory) && !Directory.Exists(f.Request.TransactionDirectory));
});
foreach (var denied in new[] { "old", "new", "scope", "revalidate" })
    await Test("guard denies incomplete exclusion: " + denied, async f =>
    {
        var guard = new Guard { Denied = denied };
        var result = await new MigrationEngine(guard).MigrateAsync(f.Request);
        Require(result.Code == "receiver_guard_required" && !Directory.Exists(f.Request.TargetDirectory));
    });
await Test("whitelist migration preserves opaque identity cursor dedup queue and profile bytes", async f =>
{
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Succeeded && result.ActivationAllowed && result.Phase == MigrationPhase.Completed);
    f.CheckTransferred();
    Require(!File.Exists(f.Target("startup-error.txt")) && !File.Exists(f.Target("dashboard.settings.json")));
    Require(File.ReadAllText(f.Source("dashboard.settings.json")) == "dashboard settings unchanged");
    var source = JsonNode.Parse(File.ReadAllText(f.Source("settings.json")))!;
    Require(source["weChat"]!["protectedBinding"]!.GetValue<string>() == "");
    Require(source["weChat"]!["protectedPushPlusToken"]!.GetValue<string>() == "");
    Require(!source["chatGpt"]!["enabled"]!.GetValue<bool>());
    Require(!File.Exists(f.Source("chatgpt.credentials")));
    Require(MigrationStartup.CanStart(f.Request.TargetDirectory));
    string journal = File.ReadAllText(Path.Combine(f.Request.TransactionDirectory, "journal.json"));
    Require(!journal.Contains(Fixture.Secret) && !JsonSerializer.Serialize(result).Contains(Fixture.Secret));
});
await Test("completed migration is idempotent and preserves new target data", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    File.WriteAllText(f.Target("task-control.state"), "new journal with completed operation");
    File.WriteAllText(f.Target("new-conversation.state"), "new messages");
    var repeat = await engine.MigrateAsync(f.Request);
    Require(repeat.Succeeded && File.ReadAllText(f.Target("task-control.state")) == "new journal with completed operation");
    Require(File.ReadAllText(f.Target("new-conversation.state")) == "new messages");
});
await Test("preexisting target is never overwritten", async f =>
{
    Directory.CreateDirectory(f.Request.TargetDirectory);
    File.WriteAllText(f.Target("settings.json"), "existing account");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "target_not_empty" && File.ReadAllText(f.Target("settings.json")) == "existing account");
    Require(File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
});
await Test("future legacy settings version rejected before payload copying", async f =>
{
    var settings = JsonNode.Parse(File.ReadAllText(f.Source("settings.json")))!;
    settings["version"] = 999;
    File.WriteAllText(f.Source("settings.json"), settings.ToJsonString());
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "unsupported_legacy_settings_version");
    Require(!Directory.Exists(Path.Combine(f.Request.TransactionDirectory, "backup")));
});
await Test("case duplicate credential properties rejected", async f =>
{
    File.WriteAllText(f.Source("settings.json"), """{"version":2,"weChat":{"protectedBinding":"x","ProtectedBinding":"y"}}""");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "duplicate_settings_key");
});
await Test("mixed-case property names scrubbed without leaving alias credentials", async f =>
{
    File.WriteAllText(f.Source("settings.json"), """{"Version":2,"WeChat":{"ProtectedBinding":"test","ProtectedPushPlusToken":"test","ClawBotEnabled":true},"ChatGpt":{"Enabled":true}}""");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Succeeded);
    string scrubbed = File.ReadAllText(f.Source("settings.json"));
    Require(!scrubbed.Contains("test") && !scrubbed.Contains("ProtectedBinding") && scrubbed.Contains("protectedBinding"));
});
await Test("relative and overlapping directories rejected before guard", async f =>
{
    var guard = new Guard();
    var result = await new MigrationEngine(guard).MigrateAsync(f.Request with { TargetDirectory = "relative" });
    Require(result.Code == "absolute_path_required" && guard.Acquired == 0);
    result = await new MigrationEngine(guard).MigrateAsync(f.Request with { TargetDirectory = Path.Combine(f.Request.SourceDirectory, "nested") });
    Require(result.Code == "overlapping_directories" && guard.Acquired == 0);
});
await Test("selector validates absolute paths and size without reading settings", f =>
{
    string install = Path.Combine(f.Root, "install");
    Directory.CreateDirectory(install);
    string selector = Path.Combine(install, "data-directory.txt");
    Require(MigrationDiscovery.ResolveDataDirectory(install, f.Request.SourceDirectory) == f.Request.SourceDirectory);
    File.WriteAllText(selector, f.Request.SourceDirectory + "\n", new UTF8Encoding(false));
    Require(MigrationDiscovery.ResolveDataDirectory(install, f.Request.TargetDirectory) == f.Request.SourceDirectory);
    File.WriteAllText(selector, "relative");
    bool rejected = false;
    try { MigrationDiscovery.ResolveDataDirectory(install, f.Request.TargetDirectory); } catch { rejected = true; }
    Require(rejected);
    File.WriteAllText(selector, new string('x', 9000));
    rejected = false;
    try { MigrationDiscovery.ResolveDataDirectory(install, f.Request.TargetDirectory); } catch { rejected = true; }
    Require(rejected && new FileInfo(selector).Length == 9000);
    return Task.CompletedTask;
});
await Test("metadata discovery returns all explicit candidates without guessing", f =>
{
    Directory.CreateDirectory(f.Request.TargetDirectory);
    File.WriteAllText(f.Target("settings.json"), "another account");
    Require(MigrationDiscovery.Locate([f.Request.SourceDirectory, f.Request.TargetDirectory]).Count == 2);
    return Task.CompletedTask;
});
await Test("corrupt settings remain untouched with nonsecret error", async f =>
{
    File.WriteAllText(f.Source("settings.json"), "{secret payload");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "invalid_json_preserved" && !JsonSerializer.Serialize(result).Contains("secret payload"));
    Require(File.ReadAllText(f.Source("settings.json")) == "{secret payload");
});
await Test("guard loss prevents activation and allows later recovery", async f =>
{
    var guard = new Guard { LoseAfter = 3 };
    var result = await new MigrationEngine(guard).MigrateAsync(f.Request);
    Require(!result.Succeeded && result.Code == "receiver_exclusion_lost");
    Require(!File.Exists(f.Target("chatgpt.credentials")));
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
    f.CheckTransferred();
});
await Test("source changed after prepare cannot be overwritten during resume", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("phase:Prepared")).MigrateAsync(f.Request));
    File.WriteAllText(f.Source("task-control.state"), "new operation");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(!result.Succeeded && result.Code == "payload_changed_requires_reconciliation");
    Require(File.ReadAllText(f.Source("task-control.state")) == "new operation");
});
await Test("backup corruption keeps destination blocked and source preserved", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("phase:Prepared")).MigrateAsync(f.Request));
    File.WriteAllText(Path.Combine(f.Request.TransactionDirectory, "backup", "chatgpt.credentials"), "corrupt");
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(!result.Succeeded && File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
});
await Test("future journal version is preserved and rejected", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("journal_created")).MigrateAsync(f.Request));
    string path = Path.Combine(f.Request.TransactionDirectory, "journal.json");
    var document = JsonNode.Parse(File.ReadAllText(path))!;
    document["Version"] = 99;
    File.WriteAllText(path, document.ToJsonString());
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "unsupported_journal_version");
    Require(JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>() == 99);
});
await Test("journal path injection never accesses nonallowlisted files", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("journal_created")).MigrateAsync(f.Request));
    string path = Path.Combine(f.Request.TransactionDirectory, "journal.json");
    var document = JsonNode.Parse(File.ReadAllText(path))!;
    document["SourceFiles"]![0]!["RelativePath"] = "../outside";
    File.WriteAllText(path, document.ToJsonString());
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Code == "journal_path_not_allowlisted");
});
await Test("competing transactions fail without altering the held source", async f =>
{
    using var held = new FileStream(f.Source(".codex-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
    Require(result.Code == "io_failure_recoverable");
    Require(File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
});
await Test("source file access failure preserves payload and recovers after release", async f =>
{
    var engine = new MigrationEngine(new Guard());
    using (var held = new FileStream(f.Source("chatgpt.credentials"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var result = await engine.MigrateAsync(f.Request);
        Require(result.Code == "io_failure_recoverable" && !File.Exists(f.Target("chatgpt.credentials")));
    }
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    f.CheckTransferred();
});
await Test("journal missing required version cannot silently become version one", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("journal_created")).MigrateAsync(f.Request));
    string path = Path.Combine(f.Request.TransactionDirectory, "journal.json");
    var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    document.Remove("Version");
    File.WriteAllText(path, document.ToJsonString());
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Code == "unsupported_journal_version");
});
await Test("journal empty and duplicate inventories are rejected", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("journal_created")).MigrateAsync(f.Request));
    string path = Path.Combine(f.Request.TransactionDirectory, "journal.json");
    var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var originals = document["SourceFiles"]!.DeepClone();
    document["SourceFiles"] = new JsonArray();
    File.WriteAllText(path, document.ToJsonString());
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Code == "invalid_journal");
    document["SourceFiles"] = originals;
    document["SourceFiles"]!.AsArray().Add(document["SourceFiles"]![0]!.DeepClone());
    File.WriteAllText(path, document.ToJsonString());
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Code == "invalid_journal");
});
await Test("unknown profile and diagnostics paths are excluded from transfer", async f =>
{
    Directory.CreateDirectory(f.Source("assistant/profiles/not-a-stable-profile"));
    File.WriteAllText(f.Source("assistant/profiles/not-a-stable-profile/memory.state"), "not a recognized profile");
    File.WriteAllText(f.Source(f.Profile + "/extra.credentials"), "not an allowlisted file");
    var engine = new MigrationEngine(new Guard());
    Require(engine.Plan(f.Request).RelativeFiles.Count == 5);
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    Require(!File.Exists(f.Target(f.Profile + "/extra.credentials")));
    Require(!Directory.Exists(f.Target("assistant/profiles/not-a-stable-profile")));
});

foreach (string point in new[]
{
    "journal_created", "backup_file:chatgpt.credentials", "staging_file:settings.json",
    "phase:Prepared", "target_blocked", "phase:DisablingSource", "settings_disabled:written",
    "source_disabled", "phase:SourceDisabled", "phase:Publishing",
    "published_file:chatgpt.credentials", "published_file:settings.json",
    "phase:Published", "activation_released", "phase:Completed"
})
    await Test("forward crash recovery: " + point, async f =>
    {
        await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt(point)).MigrateAsync(f.Request));
        if (File.Exists(f.Target("settings.json")) && !File.Exists(f.Source("chatgpt.credentials")))
        {
            var old = JsonNode.Parse(File.ReadAllText(f.Source("settings.json")))!;
            Require(old["weChat"]!["protectedBinding"]!.GetValue<string>() == "");
        }
        var result = await new MigrationEngine(new Guard()).MigrateAsync(f.Request);
        Require(result.Succeeded && result.Phase == MigrationPhase.Completed);
        f.CheckTransferred();
    });

await Test("rollback requires a new stopped-receiver guard", async f =>
{
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
    var result = await new MigrationEngine().RollbackAsync(f.Request);
    Require(result.Code == "receiver_guard_required");
    Require(!File.Exists(f.Source("chatgpt.credentials")));
});
await Test("rollback before activation restores exact originals and blocks target", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    var result = await engine.RollbackAsync(f.Request);
    Require(result.Succeeded && result.Phase == MigrationPhase.RolledBack);
    Require(File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
    Require(File.ReadAllBytes(f.Source("task-control.state")).SequenceEqual(f.OriginalQueue));
    Require(File.ReadAllText(f.Source("chatgpt.credentials")) == Fixture.Secret);
    Require(!File.Exists(f.Target("chatgpt.credentials")) && !MigrationStartup.CanStart(f.Request.TargetDirectory));
    Require((await engine.RollbackAsync(f.Request)).Succeeded);
    Require((await engine.MigrateAsync(f.Request)).Code == "rollback_transaction_cannot_migrate");
});
await Test("activated assistant requires reconciliation even if data is byte-identical", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    MigrationStartup.RecordActivation(f.Request.TargetDirectory);
    var result = await engine.RollbackAsync(f.Request);
    Require(result.Code == "rollback_requires_reconciliation_after_activation");
    Require(!File.Exists(f.Source("chatgpt.credentials")));
    f.CheckTransferred();
});
foreach (var change in new[] { "queue", "new-file", "new-profile" })
    await Test("rollback preserves new target data: " + change, async f =>
    {
        var engine = new MigrationEngine(new Guard());
        Require((await engine.MigrateAsync(f.Request)).Succeeded);
        string changed = change switch
        {
            "queue" => f.Target("task-control.state"),
            "new-profile" => f.Target("assistant/profiles/" + new string('b', 64) + "/memory.state"),
            _ => f.Target("inbox.state")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        File.WriteAllText(changed, "new completed side effect");
        Require(!(await engine.RollbackAsync(f.Request)).Succeeded);
        Require(File.ReadAllText(changed) == "new completed side effect" && !File.Exists(f.Source("chatgpt.credentials")));
    });
foreach (string point in new[]
{
    "rollback_target_blocked", "phase:RollbackDisablingTarget", "settings_disabled:written",
    "rollback_target_disabled", "phase:RollbackRestoringSource",
    "rollback_restored_file:chatgpt.credentials", "rollback_restored_file:settings.json", "phase:RolledBack"
})
    await Test("rollback crash recovery: " + point, async f =>
    {
        Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
        await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt(point)).RollbackAsync(f.Request));
        Require(!MigrationStartup.CanStart(f.Request.TargetDirectory));
        var result = await new MigrationEngine(new Guard()).RollbackAsync(f.Request);
        Require(result.Succeeded && result.Phase == MigrationPhase.RolledBack);
        Require(File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
        Require(File.ReadAllBytes(f.Source("task-control.state")).SequenceEqual(f.OriginalQueue));
        Require(!File.Exists(f.Target("chatgpt.credentials")));
    });
await Test("pending corrupt and future markers fail closed", f =>
{
    Directory.CreateDirectory(f.Request.TargetDirectory);
    Require(MigrationStartup.CanStart(f.Request.TargetDirectory));
    string marker = f.Target(".codex-migration.json");
    File.WriteAllText(marker, "{bad");
    Require(!MigrationStartup.CanStart(f.Request.TargetDirectory));
    File.WriteAllText(marker, """{"Version":99,"TransactionId":"test","State":"ready"}""");
    Require(!MigrationStartup.CanStart(f.Request.TargetDirectory));
    return Task.CompletedTask;
});
await Test("runtime lease blocks migration until the assistant exits", async f =>
{
    var engine = new MigrationEngine(new Guard());
    using (MigrationStartup.AcquireRuntimeLease(f.Request.TargetDirectory))
    {
        var result = await engine.MigrateAsync(f.Request);
        Require(result.Code == "io_failure_recoverable");
        Require(File.ReadAllBytes(f.Source("settings.json")).SequenceEqual(f.OriginalSettings));
    }
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
});
await Test("runtime lease records activation before releasing startup and blocks rollback", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    using (MigrationStartup.AcquireRuntimeLease(f.Request.TargetDirectory))
    {
        Require(File.Exists(f.Target(".codex-migration-activated.json")));
        Require((await engine.RollbackAsync(f.Request)).Code == "io_failure_recoverable");
    }
    Require((await engine.RollbackAsync(f.Request)).Code == "rollback_requires_reconciliation_after_activation");
});
await Test("pending migration cannot acquire a runtime lease", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("source_disabled")).MigrateAsync(f.Request));
    bool blocked = false;
    try { using var lease = MigrationStartup.AcquireRuntimeLease(f.Request.TargetDirectory); }
    catch (InvalidOperationException) { blocked = true; }
    Require(blocked && !File.Exists(f.Target(".codex-migration-activated.json")));
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
});
await Test("cancellation after source handoff remains blocked and resumes safely", async f =>
{
    using var cancellation = new CancellationTokenSource();
    var result = await new MigrationEngine(new Guard(), new CancelAt("phase:SourceDisabled", cancellation))
        .MigrateAsync(f.Request, cancellation.Token);
    Require(!result.Succeeded && result.Code == "cancelled_recoverable");
    Require(!MigrationStartup.CanStart(f.Request.TargetDirectory) && !File.Exists(f.Source("chatgpt.credentials")));
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
    f.CheckTransferred();
});

await Test("inspection of a new transaction is read only and never grants execution", f =>
{
    using var locked = new FileStream(f.Source("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
    var result = new MigrationEngine().Inspect(f.Request);
    Require(!result.TransactionExists && result.Phase == null && !result.StartupAllowed
        && result.Rollback == RollbackAssessment.NoTransaction);
    Require(!Directory.Exists(f.Request.TargetDirectory) && !Directory.Exists(f.Request.TransactionDirectory));
    return Task.CompletedTask;
});
await Test("completed inspection reads metadata while protected payloads are locked", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    using var source = new FileStream(f.Source("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
    using var target = new FileStream(f.Target("chatgpt.credentials"), FileMode.Open, FileAccess.Read, FileShare.None);
    var inspection = engine.Inspect(f.Request);
    Require(inspection.Phase == MigrationPhase.Completed && inspection.StartupAllowed && !inspection.ActivationRecorded
        && inspection.Rollback == RollbackAssessment.NeedsReceiverGuardAndIntegrityCheck
        && inspection.SourceFiles == 5 && inspection.TargetFiles == 5);
});
await Test("activation inspection explicitly requires reconciliation without opening new data", async f =>
{
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded);
    using var lease = MigrationStartup.AcquireRuntimeLease(f.Request.TargetDirectory);
    File.WriteAllText(f.Target("new-conversation.state"), "synthetic new data");
    var inspection = engine.Inspect(f.Request);
    Require(inspection.ActivationRecorded && inspection.Rollback == RollbackAssessment.RequiresReconciliation);
});
await Test("inspection distinguishes interrupted forward and completed rollback", async f =>
{
    await ExpectCrash(() => new MigrationEngine(new Guard(), new CrashAt("phase:Prepared")).MigrateAsync(f.Request));
    Require(new MigrationEngine().Inspect(f.Request).Rollback == RollbackAssessment.FinishForwardRecovery);
    var engine = new MigrationEngine(new Guard());
    Require((await engine.MigrateAsync(f.Request)).Succeeded && (await engine.RollbackAsync(f.Request)).Succeeded);
    var inspection = engine.Inspect(f.Request);
    Require(inspection.Phase == MigrationPhase.RolledBack && !inspection.StartupAllowed
        && inspection.Rollback == RollbackAssessment.AlreadyRolledBack);
});
await Test("inspection rejects duplicate or foreign journal metadata and preserves it", async f =>
{
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
    string path = Path.Combine(f.Request.TransactionDirectory, "journal.json");
    string original = File.ReadAllText(path);
    string duplicate = original.Insert(original.IndexOf('{') + 1, "\"Version\":1,");
    File.WriteAllText(path, duplicate);
    var inspection = new MigrationEngine().Inspect(f.Request);
    Require(inspection.Rollback == RollbackAssessment.Unverified && !inspection.StartupAllowed
        && File.ReadAllText(path) == duplicate);
    File.WriteAllText(path, original);
    inspection = new MigrationEngine().Inspect(f.Request with { TargetDirectory = Path.Combine(f.Root, "other-target") });
    Require(inspection.Code == "journal_identity_mismatch" && !inspection.StartupAllowed);
});
await Test("inspection never treats a stale or modified activation marker as execution permission", async f =>
{
    Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
    File.WriteAllText(f.Target(".codex-migration.json"), "{\"Version\":1,\"TransactionId\":\"wrong\",\"State\":\"ready\"}");
    var inspection = new MigrationEngine().Inspect(f.Request);
    Require(!inspection.StartupAllowed && inspection.Code == "metadata_only");
});
if (OperatingSystem.IsWindows())
{
    await Test("preview rejects implicit relative network and overlapping directories", f =>
    {
        var preview = new MigrationPreviewModel();
        Require(!preview.Evaluate(new("", "", "")).Valid);
        Require(!preview.Evaluate(new("relative", f.Request.TargetDirectory, f.Request.TransactionDirectory)).Valid);
        Require(!preview.Evaluate(new(@"\\server\share", f.Request.TargetDirectory, f.Request.TransactionDirectory)).Valid);
        Require(!preview.Evaluate(new(f.Request.SourceDirectory, Path.Combine(f.Request.SourceDirectory, "nested"),
            f.Request.TransactionDirectory)).Valid);
        return Task.CompletedTask;
    });
    await Test("preview honors only explicit selector metadata and shows both candidate directories", f =>
    {
        string install = Path.Combine(f.Root, "selected-install"), fallback = Path.Combine(f.Root, "explicit-fallback");
        Directory.CreateDirectory(install); Directory.CreateDirectory(fallback);
        File.WriteAllText(Path.Combine(install, "data-directory.txt"), f.Request.SourceDirectory);
        using var locked = new FileStream(f.Source("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new MigrationPreviewModel().Evaluate(new(fallback, f.Request.TargetDirectory, f.Request.TransactionDirectory, install));
        Require(result.Valid && result.ResolvedSource == f.Request.SourceDirectory && result.Candidates.Count == 2
            && result.Categories.Sum(c => c.Count) == 5);
        return Task.CompletedTask;
    });
    await Test("preview refuses existing target or transaction without reading their contents", f =>
    {
        var input = new MigrationPreviewRequest(f.Request.SourceDirectory, f.Request.TargetDirectory, f.Request.TransactionDirectory);
        Directory.CreateDirectory(f.Request.TargetDirectory);
        File.WriteAllText(f.Target("existing.state"), "unrelated private fixture");
        Require(new MigrationPreviewModel().Evaluate(input).Code == "target_not_empty");
        string cleanTarget = Path.Combine(f.Root, "clean-target");
        Directory.CreateDirectory(f.Request.TransactionDirectory);
        File.WriteAllText(Path.Combine(f.Request.TransactionDirectory, "unrelated.state"), "private fixture");
        Require(new MigrationPreviewModel().Evaluate(input with { TargetDirectory = cleanTarget }).Code == "transaction_not_empty");
        return Task.CompletedTask;
    });
    await Test("preview explains activated rollback without disclosing protected values", async f =>
    {
        Require((await new MigrationEngine(new Guard()).MigrateAsync(f.Request)).Succeeded);
        MigrationStartup.RecordActivation(f.Request.TargetDirectory);
        var result = new MigrationPreviewModel().Evaluate(new(f.Request.SourceDirectory, f.Request.TargetDirectory, f.Request.TransactionDirectory));
        Require(result.Valid && result.Inspection!.Rollback == RollbackAssessment.RequiresReconciliation
            && result.RollbackDetail.Contains("对账") && !JsonSerializer.Serialize(result).Contains(Fixture.Secret));
    });
}
Console.WriteLine($"Completed {results.Count} checks; failures={failed}; synthetic data only.");
return failed == 0 ? 0 : 1;

sealed class TestFailure : Exception;
sealed class InjectedCrash : Exception;
sealed class CrashAt(string target) : IMigrationFaultInjector
{
    public void At(string point) { if (point == target) throw new InjectedCrash(); }
}
sealed class CancelAt(string target, CancellationTokenSource cancellation) : IMigrationFaultInjector
{
    public void At(string point) { if (point == target) cancellation.Cancel(); }
}
sealed class Guard : ILegacyReceiverGuard
{
    public string Denied = "";
    public int LoseAfter = int.MaxValue, Acquired;
    public ValueTask<IReceiverExclusionLease?> TryAcquireAsync(MigrationRequest request, MigrationDirection direction, CancellationToken cancellation)
    {
        Acquired++;
        return ValueTask.FromResult<IReceiverExclusionLease?>(new Lease(Denied, LoseAfter));
    }
    private sealed class Lease(string denied, int loseAfter) : IReceiverExclusionLease
    {
        private int checks;
        public bool LegacyStoppedAndWritesBlocked => denied != "old";
        public bool AssistantStoppedAndWritesBlocked => denied != "new";
        public bool CredentialScopeCompatible => denied != "scope";
        public ValueTask<bool> RevalidateAsync(CancellationToken cancellation)
            => ValueTask.FromResult(denied != "revalidate" && ++checks <= loseAfter);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
sealed class Fixture
{
    internal const string Secret = "SYNTHETIC_PROTECTED_PAYLOAD_NOT_REAL_CREDENTIALS";
    internal readonly string Root;
    internal readonly MigrationRequest Request;
    internal readonly byte[] OriginalSettings, OriginalQueue;
    internal string Profile => "assistant/profiles/" + new string('a', 64);
    internal Fixture(string root)
    {
        Root = root;
        Request = new(Path.Combine(root, "source"), Path.Combine(root, "target"), Path.Combine(root, "transaction"));
        Directory.CreateDirectory(Request.SourceDirectory);
        var original = new
        {
            version = 2, pythonPath = "python", codexHome = "C:/synthetic/.codex",
            remoteHosts = new[] { "synthetic-host" }, localEnabled = true, refreshSeconds = 17,
            hudVisible = true, webPort = 4317,
            weChat = new
            {
                clawBotEnabled = true, pushPlusEnabled = true,
                protectedBinding = Secret, protectedPushPlusToken = Secret,
                recentAttempts = new[] { "stable-dedup-key" }, notifyCompleted = true
            },
            chatGpt = new { enabled = true, taskControlEnabled = true, activeAccount = "stable-synthetic-id", model = "model" }
        };
        OriginalSettings = JsonSerializer.SerializeToUtf8Bytes(original);
        File.WriteAllBytes(Source("settings.json"), OriginalSettings);
        File.WriteAllText(Source("chatgpt.credentials"), Secret);
        // Opaque protected-store bytes. Migration never parses, truncates or reclassifies operations.
        OriginalQueue = Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"owner":"stable-owner","entries":[{"id":"stable-id","state":"unconfirmed"},{"id":"other-id","state":"sending"},{"id":"queued-id","state":"queued"}]}""")));
        File.WriteAllBytes(Source("task-control.state"), OriginalQueue);
        Directory.CreateDirectory(Source(Profile));
        File.WriteAllText(Source(Profile + "/AGENTS.md"), "自然回复；人格没有授权作用。");
        File.WriteAllText(Source(Profile + "/memory.state"), "SYNTHETIC_ENCRYPTED_MEMORY");
        File.WriteAllText(Source("startup-error.txt"), "Do not migrate diagnostics");
        File.WriteAllText(Source("dashboard.settings.json"), "dashboard settings unchanged");
    }
    internal string Source(string relative) => Path.Combine(Request.SourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    internal string Target(string relative) => Path.Combine(Request.TargetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    internal void CheckTransferred()
    {
        var target = JsonNode.Parse(File.ReadAllText(Target("settings.json")))!;
        if (target["version"]!.GetValue<int>() != 1 || target["hudVisible"] != null
            || target["weChat"]!["protectedBinding"]!.GetValue<string>() != Secret
            || target["weChat"]!["recentAttempts"]![0]!.GetValue<string>() != "stable-dedup-key"
            || target["chatGpt"]!["activeAccount"]!.GetValue<string>() != "stable-synthetic-id"
            || File.ReadAllText(Target("chatgpt.credentials")) != Secret
            || !File.ReadAllBytes(Target("task-control.state")).SequenceEqual(OriginalQueue)
            || File.ReadAllText(Target(Profile + "/memory.state")) != "SYNTHETIC_ENCRYPTED_MEMORY"
            || File.ReadAllText(Target(Profile + "/AGENTS.md")) != "自然回复；人格没有授权作用。")
            throw new TestFailure();
    }
}
