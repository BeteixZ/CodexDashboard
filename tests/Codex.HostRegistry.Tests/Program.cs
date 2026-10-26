using System.Text.Json;
using CodexDashboard;

if (args.Length != 2 || args[0] != "--output" || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Use --output <new absolute directory>.");
string output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new ArgumentException("Output directory must be new.");
Directory.CreateDirectory(output);
var now = new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
var checks = new List<object>();
int failed = 0;
void Test(string name, Action test)
{
    try { test(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex)
    {
        failed++;
        checks.Add(new { name, passed = false, errorType = ex.GetType().Name });
        Console.WriteLine("FAIL " + name + " (" + ex.GetType().Name + ")");
    }
}
static void Require(bool value) { if (!value) throw new TestFailure(); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
    throw new TestFailure();
}
static string Remote(string alias) => HostIdentity.RemotePrefix + alias;
HostRegistry Registry(params string[] aliases)
{
    var registry = new HostRegistry();
    registry.ReconcileConfiguration(true, aliases, "C:/synthetic/.codex");
    return registry;
}
ExplicitHostConfirmation Confirmation(string id = "current-user-message") => new(id, now.AddMinutes(1));
string Rename(HostRegistry registry, string oldAlias, string newAlias)
{
    string id = registry.ProposeAliasRename(Remote(oldAlias), Remote(newAlias), now);
    registry.ReconcileConfiguration(true, [newAlias], "C:/synthetic/.codex");
    registry.ConfirmAliasRename(id, Confirmation());
    registry.Observe(new(Remote(newAlias)), HostAvailability.Online, now.AddMinutes(1));
    return id;
}

Test("local and full Codex remote IDs accepted without display aliases", () =>
{
    Require(new HostEndpointIdentity("local").HostId == "local");
    Require(new HostEndpointIdentity(Remote("GPU-1.example")).HostId == Remote("GPU-1.example"));
    foreach (string bad in new[] { "本机", "GPU", "LOCAL", "remote:GPU", Remote("-option"), Remote("GPU;command"), Remote("GPU\n") })
        Reject(() => _ = new HostEndpointIdentity(bad));
});
Test("same thread ID on different hosts has distinct canonical keys", () =>
{
    var identities = new[] { new CanonicalTaskIdentity("local", "thread"), new CanonicalTaskIdentity(Remote("A"), "thread"),
        new CanonicalTaskIdentity(Remote("B"), "thread") };
    Require(identities.Select(i => i.Key).Distinct().Count() == 3);
});
Test("same host and thread on different profiles has distinct keys", () =>
{
    var identities = new[] { new CanonicalTaskIdentity("local", "thread"), new CanonicalTaskIdentity("local", "thread", "one"),
        new CanonicalTaskIdentity("local", "thread", "two") };
    Require(identities.Select(i => i.Key).Distinct().Count() == 3);
});
Test("thread delimiters percent escapes and Unicode cannot collide", () =>
{
    var identities = new[] { new CanonicalTaskIdentity("local", "a/b"), new CanonicalTaskIdentity("local", "a%2Fb"),
        new CanonicalTaskIdentity("local", "a:b"), new CanonicalTaskIdentity("local", "a", "b"),
        new CanonicalTaskIdentity("local", "中文😀") };
    Require(identities.Select(i => i.Key).Distinct().Count() == identities.Length);
    foreach (string bad in new[] { "", " ", "bad\nthread", "\uD800" })
        Reject(() => _ = new CanonicalTaskIdentity("local", bad));
});
Test("canonical identity roundtrip preserves exact case and profile", () =>
{
    var identity = new CanonicalTaskIdentity(Remote("GPU"), "same-thread", "profile-A");
    var copy = JsonSerializer.Deserialize<CanonicalTaskIdentity>(JsonSerializer.Serialize(identity, SharedJson.Options), SharedJson.Options)!;
    Require(copy == identity && copy.Key == identity.Key && copy.Endpoint == identity.Endpoint);
});
Test("invalid empty or path-like profile IDs are rejected", () =>
{
    foreach (string bad in new[] { "", "../other", "one/two", "space profile", new string('a', 101) })
        Reject(() => _ = new CanonicalTaskIdentity("local", "thread", bad));
});
Test("legacy task key is unchanged and distinct from canonical identity", () =>
{
    var task = new TaskRecord { Host = "本机", Id = "thread" };
    Require(task.Key == "本机:thread" && task.Key != new CanonicalTaskIdentity("local", "thread").Key);
});
Test("configuration imports stable IDs with unknown availability", () =>
{
    var registry = Registry("GPU", "V100");
    Require(registry.Registrations.Count == 3);
    Require(registry.Registrations.All(h => h.MappingState == HostMappingState.Resolved && h.Availability == HostAvailability.Unknown));
    Require(registry.Registrations.Single(h => h.HostId == Remote("GPU")).ConnectionReference == "ssh:GPU");
    Require(!registry.CheckControlTarget(new(Remote("GPU"), "thread"), now).CanAddress);
});
Test("invalid or case-ambiguous alias input is atomic", () =>
{
    var registry = Registry("GPU");
    long revision = registry.Export().Revision;
    Reject(() => registry.ReconcileConfiguration(true, ["GPU", "gpu"]));
    Reject(() => registry.ReconcileConfiguration(true, ["evil;command"]));
    Require(registry.Export().Revision == revision && registry.Registrations.Count == 2);
});
Test("deleting host retains identity and disables it without deleting history", () =>
{
    var registry = Registry("GPU");
    var identity = new CanonicalTaskIdentity(Remote("GPU"), "thread");
    registry.Observe(identity.Endpoint, HostAvailability.Online, now);
    Require(registry.CheckControlTarget(identity, now).CanAddress);
    registry.ReconcileConfiguration(true, []);
    var retained = registry.Registrations.Single(h => h.HostId == Remote("GPU"));
    Require(!retained.Configured && !retained.Enabled && retained.Availability == HostAvailability.Unknown);
    Require(registry.CheckControlTarget(identity, now).Code == "host_not_configured");
});
Test("reappearing configuration does not silently re-enable a retired preference", () =>
{
    var registry = Registry("GPU");
    registry.ReconcileConfiguration(true, []);
    registry.ReconcileConfiguration(true, ["GPU"]);
    var host = registry.Registrations.Single(h => h.HostId == Remote("GPU"));
    Require(host.Configured && !host.Enabled);
    registry.SetEnabled(host.Identity, true);
    Require(registry.CheckControlTarget(new(host.HostId, "thread"), now).Code == "host_observation_unknown");
});
Test("display renames and duplicate display names do not affect task identities", () =>
{
    var registry = Registry("A", "B");
    var first = new CanonicalTaskIdentity(Remote("A"), "thread");
    string key = first.Key;
    registry.SetDisplayName(first.Endpoint, "同名主机");
    registry.SetDisplayName(new(Remote("B")), "同名主机");
    Require(first.Key == key && registry.Registrations.Count(h => h.DisplayName == "同名主机") == 2);
    Reject(() => _ = new CanonicalTaskIdentity("同名主机", "thread"));
});
Test("online observation permits only addressing and still requires live backend verification", () =>
{
    var registry = Registry("GPU");
    var identity = new CanonicalTaskIdentity(Remote("GPU"), "thread");
    registry.Observe(identity.Endpoint, HostAvailability.Online, now);
    var decision = registry.CheckControlTarget(identity, now);
    Require(decision.CanAddress && decision.RequiresLiveBackendVerification && decision.Original == identity);
});
foreach (var availability in new[] { HostAvailability.Offline, HostAvailability.Unknown })
    Test("control never redirects unavailable host: " + availability, () =>
    {
        var registry = Registry("A", "B");
        var original = new CanonicalTaskIdentity(Remote("A"), "same-thread");
        registry.Observe(original.Endpoint, availability, now);
        registry.Observe(new(Remote("B")), HostAvailability.Online, now);
        var result = registry.CheckControlTarget(original, now);
        Require(!result.CanAddress && result.Original == original);
    });
Test("stale and future observations cannot authorize addressing", () =>
{
    var registry = Registry("GPU");
    var task = new CanonicalTaskIdentity(Remote("GPU"), "thread");
    registry.Observe(task.Endpoint, HostAvailability.Online, now);
    Require(!registry.CheckControlTarget(task, now.AddMinutes(3)).CanAddress);
    registry.Observe(task.Endpoint, HostAvailability.Online, now.AddHours(1));
    Require(!registry.CheckControlTarget(task, now).CanAddress);
    Reject(() => registry.CheckControlTarget(task, now, TimeSpan.FromDays(1)));
});
Test("out-of-order older observations cannot overwrite newer offline evidence", () =>
{
    var registry = Registry("GPU");
    var task = new CanonicalTaskIdentity(Remote("GPU"), "thread");
    registry.Observe(task.Endpoint, HostAvailability.Offline, now.AddSeconds(1));
    registry.Observe(task.Endpoint, HostAvailability.Online, now);
    Require(registry.CheckControlTarget(task, now.AddSeconds(1)).Code == "host_offline");
});
Test("unknown host and unknown profile remain unknown despite matching thread IDs", () =>
{
    var registry = Registry("GPU");
    registry.Observe(new(Remote("GPU")), HostAvailability.Online, now);
    Require(registry.CheckControlTarget(new(Remote("missing"), "thread"), now).Code == "unknown_host_or_profile");
    Require(registry.CheckControlTarget(new(Remote("GPU"), "thread", "missing"), now).Code == "unknown_host_or_profile");
});
Test("single directory cannot introduce unnecessary profile IDs", () =>
{
    var registry = Registry();
    Reject(() => registry.ConfigureProfiles("local", [new("one", "C:/one/.codex")], Confirmation()));
    Require(registry.Registrations.Single().ProfileId == null);
});
Test("two profiles require explicit directories and preserve old ambiguous identity", () =>
{
    var registry = Registry();
    var old = new CanonicalTaskIdentity("local", "same-thread");
    registry.ConfigureProfiles("local", [new("one", "C:/one/.codex"), new("two", "D:/two/.codex")], Confirmation());
    Require(registry.Registrations.Count == 3);
    Require(registry.Registrations.Single(h => h.ProfileId == null).MappingState == HostMappingState.ProfileRequired);
    Require(registry.Registrations.Where(h => h.ProfileId != null).All(h => h.ConfirmationUserMessageId == "current-user-message"));
    Require(!registry.CheckControlTarget(old, now).CanAddress && old.ProfileId == null);
    registry.Observe(new("local", "one"), HostAvailability.Online, now);
    Require(registry.CheckControlTarget(new("local", "same-thread", "one"), now).CanAddress);
    Require(!registry.CheckControlTarget(new("local", "same-thread", "two"), now).CanAddress);
});
Test("duplicate profiles and duplicate directory references are rejected atomically", () =>
{
    var registry = Registry();
    Reject(() => registry.ConfigureProfiles("local", [new("same", "C:/one"), new("same", "C:/two")], Confirmation()));
    Reject(() => registry.ConfigureProfiles("local", [new("one", "C:/same"), new("two", "C:/same")], Confirmation()));
    Require(registry.Registrations.Count == 1);
});
Test("assigned profile IDs cannot be repointed to another directory", () =>
{
    var registry = Registry();
    registry.ConfigureProfiles("local", [new("one", "C:/one"), new("two", "C:/two")], Confirmation());
    Reject(() => registry.ConfigureProfiles("local", [new("one", "C:/different"), new("two", "C:/two")], Confirmation()));
    Require(registry.Registrations.Single(h => h.ProfileId == "one").CodexDirectory == "C:/one");
});
Test("removing one profile retains both history keys and never collapses the remaining key", () =>
{
    var registry = Registry();
    registry.ConfigureProfiles("local", [new("one", "C:/one"), new("two", "C:/two")], Confirmation());
    string key = new CanonicalTaskIdentity("local", "thread", "one").Key;
    registry.ConfigureProfiles("local", [new("one", "C:/one")], Confirmation("remove-profile"));
    registry.ReconcileConfiguration(true, []);
    Require(registry.Registrations.Count == 3);
    Require(!registry.Registrations.Single(h => h.ProfileId == "two").Enabled);
    Require(new CanonicalTaskIdentity("local", "thread", "one").Key == key);
});
Test("configuration directory change is flagged instead of retargeting an old identity", () =>
{
    var registry = Registry();
    var result = registry.ReconcileConfiguration(true, [], "D:/another/.codex");
    var host = registry.Registrations.Single();
    Require(result.Conflicts.Count == 1 && host.MappingState == HostMappingState.Conflict);
    Require(host.CodexDirectory == "C:/synthetic/.codex" && !host.Enabled);
});
Test("alias proposal alone cannot change any control target", () =>
{
    var registry = Registry("A");
    var old = new CanonicalTaskIdentity(Remote("A"), "thread");
    registry.Observe(old.Endpoint, HostAvailability.Online, now);
    registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    registry.ReconcileConfiguration(true, ["B"]);
    registry.Observe(new(Remote("B")), HostAvailability.Online, now);
    Require(registry.CheckControlTarget(old, now).Code == "mapping_unconfirmed");
    Require(registry.CheckControlTarget(new(Remote("B"), "thread"), now).Code == "mapping_unconfirmed");
    Require(!registry.ResolveForHistory(old).Resolved);
});
Test("confirmation requires a current user reference and valid chronology", () =>
{
    Reject(() => _ = new ExplicitHostConfirmation("", now));
    Reject(() => _ = new ExplicitHostConfirmation("user", default));
    var registry = Registry("A");
    string id = registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    Reject(() => registry.ConfirmAliasRename(id, null!));
    Reject(() => registry.ConfirmAliasRename(id, new("old-message", now.AddMinutes(-1))));
    Require(registry.AliasMappings.Single().State == AliasMappingState.Pending);
});
Test("confirmed alias resolves history while leaving old approvals and operations fixed", () =>
{
    var registry = Registry("A");
    var approvalTarget = new CanonicalTaskIdentity(Remote("A"), "thread");
    var operationTarget = new CanonicalTaskIdentity(Remote("A"), "thread");
    string oldKey = approvalTarget.Key;
    string id = Rename(registry, "A", "B");
    var history = registry.ResolveForHistory(approvalTarget);
    Require(history.Resolved && history.Suggested!.HostId == Remote("B") && history.MappingIds.Single() == id);
    Require(approvalTarget.Key == oldKey && operationTarget.Key == oldKey);
    Require(!registry.CheckControlTarget(approvalTarget, now.AddMinutes(1)).CanAddress);
    Require(registry.CheckControlTarget(new(Remote("B"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("confirmed rename chains retain every mapping and never redirect control", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    Rename(registry, "B", "C");
    var old = new CanonicalTaskIdentity(Remote("A"), "thread");
    var history = registry.ResolveForHistory(old);
    Require(history.Resolved && history.Suggested!.HostId == Remote("C") && history.MappingIds.Count == 2);
    Require(registry.AliasMappings.Count == 2 && !registry.CheckControlTarget(old, now.AddMinutes(1)).CanAddress);
});
Test("rename cycles and branches are rejected without mutating state", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    Rename(registry, "B", "C");
    long revision = registry.Export().Revision;
    Reject(() => registry.ProposeAliasRename(Remote("C"), Remote("A"), now));
    Reject(() => registry.ProposeAliasRename(Remote("A"), Remote("D"), now));
    Require(registry.Export().Revision == revision);
});
Test("existing target identities cannot be silently merged by renaming", () =>
{
    var registry = Registry("A", "B");
    Reject(() => registry.ProposeAliasRename(Remote("A"), Remote("B"), now));
    Require(registry.AliasMappings.Count == 0);
});
Test("pending chains cannot confer identity before their source is confirmed", () =>
{
    var registry = Registry("A");
    registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    Reject(() => registry.ProposeAliasRename(Remote("B"), Remote("C"), now));
    Require(registry.AliasMappings.Count == 1);
});
Test("case-only changes need and accept an explicit confirmed rename", () =>
{
    var registry = Registry("GPU");
    Reject(() => registry.ReconcileConfiguration(true, ["gpu"]));
    Rename(registry, "GPU", "gpu");
    Require(registry.ResolveForHistory(new(Remote("GPU"), "thread")).Suggested!.HostId == Remote("gpu"));
    Require(!registry.CheckControlTarget(new(Remote("GPU"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("case-only rename chains retain explicit history without blocking configuration", () =>
{
    var registry = Registry("GPU");
    Rename(registry, "GPU", "gpu");
    Rename(registry, "gpu", "Gpu");
    var history = registry.ResolveForHistory(new(Remote("GPU"), "thread"));
    Require(history.Resolved && history.MappingIds.Count == 2 && history.Suggested!.HostId == Remote("Gpu"));
    Require(registry.CheckControlTarget(new(Remote("Gpu"), "thread"), now.AddMinutes(1)).CanAddress);
    Require(!registry.CheckControlTarget(new(Remote("gpu"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("rejected case-only rename does not prevent original configuration reconciliation", () =>
{
    var registry = Registry("GPU");
    string id = registry.ProposeAliasRename(Remote("GPU"), Remote("gpu"), now);
    registry.RejectAliasRename(id);
    registry.ReconcileConfiguration(true, ["GPU"]);
    registry.Observe(new(Remote("GPU")), HostAvailability.Online, now);
    Require(registry.CheckControlTarget(new(Remote("GPU"), "thread"), now).CanAddress);
    Require(!registry.CheckControlTarget(new(Remote("gpu"), "thread"), now).CanAddress);
    var restored = new HostRegistry(registry.Export());
    Require(restored.AliasMappings.Single().State == AliasMappingState.Rejected);
});
Test("local identity cannot be renamed as an SSH alias", () =>
{
    var registry = Registry("A");
    Reject(() => registry.ProposeAliasRename("local", Remote("B"), now));
    Reject(() => registry.ProposeAliasRename(Remote("A"), "local", now));
});
Test("rejecting a rename retains rejected history and keeps target disabled", () =>
{
    var registry = Registry("A");
    string id = registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    registry.RejectAliasRename(id);
    Require(registry.AliasMappings.Single().State == AliasMappingState.Rejected);
    Require(registry.Registrations.Single(h => h.HostId == Remote("B")).MappingState == HostMappingState.Retired);
    Reject(() => registry.ConfirmAliasRename(id, Confirmation()));
    registry.Observe(new(Remote("A")), HostAvailability.Online, now);
    Require(registry.CheckControlTarget(new(Remote("A"), "thread"), now).CanAddress);
});
Test("configuration removal retains confirmed mappings and history resolution", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    registry.ReconcileConfiguration(false, []);
    Require(registry.AliasMappings.Count == 1);
    Require(registry.ResolveForHistory(new(Remote("A"), "thread")).Suggested!.HostId == Remote("B"));
    Require(!registry.CheckControlTarget(new(Remote("B"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("profiled alias rename preserves profile IDs and rejects profileless controls", () =>
{
    var registry = Registry("A");
    registry.ConfigureProfiles(Remote("A"), [new("one", "/one/.codex"), new("two", "/two/.codex")], Confirmation());
    string id = registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    registry.ReconcileConfiguration(true, ["B"]);
    registry.ConfirmAliasRename(id, Confirmation());
    var old = new CanonicalTaskIdentity(Remote("A"), "thread", "one");
    var history = registry.ResolveForHistory(old);
    Require(history.Resolved && history.Suggested == new CanonicalTaskIdentity(Remote("B"), "thread", "one"));
    Require(!registry.CheckControlTarget(new(Remote("B"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("JSON persistence keeps exact identities confirmations and disabled histories", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    var json = JsonSerializer.Serialize(registry.Export(), SharedJson.Options);
    var restored = new HostRegistry(JsonSerializer.Deserialize<HostRegistryDocument>(json, SharedJson.Options));
    Require(restored.Export().Revision == registry.Export().Revision);
    Require(restored.AliasMappings.Single().ConfirmationUserMessageId == "current-user-message");
    Require(restored.ResolveForHistory(new(Remote("A"), "thread")).Suggested!.HostId == Remote("B"));
    Require(!restored.CheckControlTarget(new(Remote("A"), "thread"), now.AddMinutes(1)).CanAddress);
});
Test("export list mutation cannot change internal registry state", () =>
{
    var registry = Registry("A");
    var copy = registry.Export();
    copy.Hosts.Clear();
    Require(registry.Registrations.Count == 2);
});
Test("unsupported registry version and duplicate endpoint identities rejected", () =>
{
    var registry = Registry("A");
    Reject(() => _ = new HostRegistry(registry.Export() with { Version = 2 }));
    var data = registry.Export();
    data.Hosts.Add(data.Hosts[0]);
    Reject(() => _ = new HostRegistry(data));
});
Test("mismatched connection references rejected before gateway use", () =>
{
    var data = Registry("A").Export();
    int index = data.Hosts.FindIndex(h => h.HostId == Remote("A"));
    data.Hosts[index] = data.Hosts[index] with { ConnectionReference = "ssh:B" };
    Reject(() => _ = new HostRegistry(data));
});
Test("serialized resolved mappings without user confirmation rejected", () =>
{
    var registry = Registry("A");
    registry.ProposeAliasRename(Remote("A"), Remote("B"), now);
    var data = registry.Export();
    data.AliasMappings[0] = data.AliasMappings[0] with { State = AliasMappingState.Resolved };
    Reject(() => _ = new HostRegistry(data));
});
Test("serialized conflicting edges and cycles rejected", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    Rename(registry, "B", "C");
    var data = registry.Export();
    data.AliasMappings.Add(new()
    {
        Id = Guid.NewGuid().ToString("N"), FromHostId = Remote("C"), ToHostId = Remote("A"), ProposedAt = now
    });
    Reject(() => _ = new HostRegistry(data));
    data = registry.Export();
    data.AliasMappings.Add(new()
    {
        Id = Guid.NewGuid().ToString("N"), FromHostId = Remote("A"), ToHostId = Remote("C"), ProposedAt = now
    });
    Reject(() => _ = new HostRegistry(data));
});
Test("serialized single-profile identity cannot bypass multiple-directory requirement", () =>
{
    var data = Registry().Export();
    data.Hosts[0] = data.Hosts[0] with { ProfileId = "unneeded" };
    Reject(() => _ = new HostRegistry(data));
});
Test("serialized case collisions require explicit recorded history", () =>
{
    var data = Registry("GPU").Export();
    var source = data.Hosts.Single(h => h.HostId == Remote("GPU"));
    data.Hosts.Add(source with { HostId = Remote("gpu"), ConnectionReference = "ssh:gpu" });
    Reject(() => _ = new HostRegistry(data));
});
Test("null serialized mappings fail validation before graph traversal", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    var data = registry.Export();
    data.AliasMappings.Add(null!);
    Reject(() => _ = new HostRegistry(data));
});
Test("history does not resolve a profile that never existed at the original identity", () =>
{
    var registry = Registry("A");
    Rename(registry, "A", "B");
    registry.ConfigureProfiles(Remote("B"), [new("one", "/one/.codex"), new("two", "/two/.codex")], Confirmation());
    Require(!registry.ResolveForHistory(new(Remote("A"), "thread", "one")).Resolved);
});
Test("addressability requires a real current time and handles the DateTimeOffset limit", () =>
{
    var registry = Registry("A");
    var task = new CanonicalTaskIdentity(Remote("A"), "thread");
    Reject(() => registry.CheckControlTarget(task, default));
    registry.Observe(task.Endpoint, HostAvailability.Online, DateTimeOffset.MaxValue);
    Require(registry.CheckControlTarget(task, DateTimeOffset.MaxValue).CanAddress);
});

File.WriteAllText(Path.Combine(output, "checks.json"),
    JsonSerializer.Serialize(new { count = checks.Count, failed, networkUsed = false, checks }, SharedJson.Options));
Console.WriteLine($"Completed {checks.Count} checks; failures={failed}; no network or real host access.");
return failed == 0 ? 0 : 1;

sealed class TestFailure : Exception;
