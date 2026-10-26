using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using CodexDashboard;

if (args.Length != 1) throw new ArgumentException("New isolated test directory required.");
string directory = Path.GetFullPath(args[0]);
if (Directory.Exists(directory)) throw new ArgumentException("Output exists.");
Directory.CreateDirectory(directory);
var checks = new Dictionary<string, bool>();
var skips = new List<string>();
void Check(string name, bool ok) { checks.Add(name, ok); if (!ok) throw new InvalidOperationException(name); }
var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
const string Owner = "synthetic-owner";
WorkspaceAdmission Reserve(WorkspaceWriteCoordinator coordinator, string id, CanonicalTaskIdentity? task, WorkspaceLocation location)
    => coordinator.TryReserve(id, task, location, Owner);
bool Amend(WorkspaceWriteCoordinator coordinator, CanonicalTaskIdentity task, string turn, WorkspaceLocation location)
    => coordinator.CanAmend(task, turn, location, Owner);
WorkspaceClaimEvidence Claim(string id, CanonicalTaskIdentity? task, WorkspaceLocation location, string? turn, DateTimeOffset at, bool reconcile = true)
    => new(id, task, location, turn, at, reconcile, Owner);
WorkspaceLocation Location(string path = @"C:\work", string host = "local") => new(host, host == "local" ? "windows" : "linux", path);
CanonicalTaskIdentity TaskId(string id, string? profile = null, string host = "local") => new(host, id, profile);
WorkspaceWriteCoordinator Guard(params string[] sources) => new(new TestResolver(), sources, () => now);
WorkspaceTaskEvidence Evidence(CanonicalTaskIdentity task, string? turn, WorkspaceTaskState state,
    bool reconciliation = false, string path = @"C:\work") => new(task, Location(path, task.HostId), turn, state, now, true,
        WorkspaceEvidenceKind.LiveBackend, reconciliation, Owner);
try
{
    var a = TaskId("a", "p1"); var b = TaskId("b", "p2"); var guard = Guard("queue", "owned");
    Check("unrecovered sources block admission", !Reserve(guard, "first", a, Location()).Allowed);
    guard.CompleteRecovery("queue");
    Check("one recovered source cannot bypass another", !guard.CanStart(a, Location()));
    guard.CompleteRecovery("owned");
    var first = Reserve(guard, "first", a, Location());
    Check("recovered empty host admits one task", first.Allowed && guard.Blockers().Count == 1);
    Check("same directory different profile conflicts", !Reserve(guard, "other", b, Location()).Allowed);
    Check("windows case separators and trailing slash normalize", !Reserve(guard, "case", b, Location("c:/WORK/")).Allowed);
    Check("child directory overlaps root", !Reserve(guard, "child", b, Location(@"C:\work\child")).Allowed);
    Check("similar string prefix is a different directory", Reserve(guard, "prefix", b, Location(@"C:\workspace")).Allowed);
    first.Reservation!.Accepted(a, "turn-new");
    Check("acceptance retains workspace occupancy", !guard.CanStart(b, Location()));
    guard.Observe(Evidence(a, "turn-old", WorkspaceTaskState.Completed));
    Check("old terminal cannot free newer accepted turn", !guard.CanStart(b, Location()));
    guard.Observe(Evidence(a, "turn-new", WorkspaceTaskState.Running));
    Check("exact running turn may steer under same claim", Amend(guard, a, "turn-new", Location()));
    Check("wrong turn cannot steer", !Amend(guard, a, "turn-old", Location()));
    first.Reservation.Unknown();
    first.Reservation.Accepted(a, "turn-new");
    Check("late acceptance cannot clear an Unknown resource claim", !Amend(guard, a, "turn-new", Location()));
    guard.Observe(Evidence(a, "turn-new", WorkspaceTaskState.Completed));
    Check("Unknown survives ordinary late terminal until reconciliation", !guard.CanStart(b, Location()));
    guard.Observe(Evidence(a, "turn-new", WorkspaceTaskState.Completed, reconciliation: true));
    Check("exact explicit reconciliation releases known turn", guard.CanStart(b, Location()));
    Check("retired operation identifier cannot be retried", !Reserve(guard, "first", a, Location()).Allowed);
    var early = Guard();
    var earlyClaim = Reserve(early, "early", a, Location());
    early.Observe(Evidence(a, "early-turn", WorkspaceTaskState.Completed));
    earlyClaim.Reservation!.Accepted(a, "early-turn");
    Check("known early terminal survives delayed direct acceptance", early.CanStart(b, Location()));
    var written = Reserve(early, "written", a, Location());
    written.Reservation!.Accepted(a, "written-turn");
    written.Reservation.NotDispatched();
    Check("not-dispatched API cannot release a known accepted claim", !early.CanStart(b, Location()));

    var unknown = Reserve(guard, "unknown-no-turn", a, Location());
    unknown.Reservation!.Unknown();
    guard.Observe(Evidence(a, "another-turn", WorkspaceTaskState.Completed, reconciliation: true));
    Check("unknown turn cannot be inferred from a different completed turn", !guard.CanStart(b, Location()));
    var recovered = Guard();
    recovered.RestoreUnresolved(Claim("previous-process", a, Location(), "t", now.AddMinutes(-1)));
    Check("restarted coordinator imports persistent unresolved evidence", !recovered.CanStart(b, Location()));
    recovered.Observe(Evidence(a, "t", WorkspaceTaskState.Completed));
    Check("restored claim requires explicit read reconciliation", !recovered.CanStart(b, Location()));
    recovered.Observe(Evidence(a, "t", WorkspaceTaskState.Completed, true));
    Check("restored exact turn can reconcile without new control", recovered.CanStart(b, Location()));

    var creation = Guard();
    creation.Observe(Evidence(a, null, WorkspaceTaskState.Unknown) with { Kind = WorkspaceEvidenceKind.CreationOnly, Fresh = false });
    Check("creation-only history conservatively blocks without claiming running", !creation.CanStart(b, Location()) && creation.Blockers().Count == 0);
    creation.Observe(Evidence(a, null, WorkspaceTaskState.Idle));
    Check("fresh verified idle replaces creation-only baseline", creation.CanStart(b, Location()));
    creation.Observe(Evidence(a, null, WorkspaceTaskState.Unknown) with { Kind = WorkspaceEvidenceKind.CreationOnly, Fresh = false });
    Check("repeat historical import cannot erase stronger observation", creation.CanStart(b, Location()));
    now = now.AddMinutes(3);
    Check("stale idle does not imply workspace remains free", !creation.CanStart(b, Location()));

    var remote = Guard();
    var ra = TaskId("a", "x", "remote-ssh-discovered:host-a");
    var rb = TaskId("b", "y", "remote-ssh-discovered:host-a");
    Reserve(remote, "remote", ra, Location("/one", ra.HostId));
    Check("unverified remote canonical paths serialize whole exact host", !Reserve(remote, "remote-other", rb, Location("/two", rb.HostId)).Allowed);
    var rc = TaskId("c", null, "remote-ssh-discovered:host-b");
    Check("equal directory strings do not identify different hosts", Reserve(remote, "different-host", rc, Location("/one", rc.HostId)).Allowed);

    var concurrent = Guard();
    var attempts = await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
        System.Threading.Tasks.Task.Run(() => Reserve(concurrent, "parallel-" + i, TaskId("task-" + i), Location()))));
    Check("concurrent cross-transport starts admit exactly one", attempts.Count(x => x.Allowed) == 1);
    attempts.Single(x => x.Allowed).Reservation!.NotDispatched();
    Check("proven no-write rejection releases reservation", concurrent.CanStart(a, Location()));
    concurrent.FailRecovery("corrupt-store");
    Check("corrupt recovery evidence never resumes automatically", !Reserve(concurrent, "after-corrupt", a, Location()).Allowed);

    var limited = new WorkspaceWriteCoordinator(new TestResolver(), clock: () => now, maxConcurrentExecutions: 2);
    var counted = await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
        System.Threading.Tasks.Task.Run(() => Reserve(limited, "capacity-" + i, TaskId("capacity-task-" + i), Location(@"C:\isolated-" + i)))));
    Check("global admission atomically limits different directories", counted.Count(x => x.Allowed) == 2
        && counted.Where(x => !x.Allowed).All(x => x.Code == "workspace_global_capacity")
        && limited.Snapshot() is { Limit: 2, Occupied: 2, Reserved: 2, Unknown: 0 });
    var reservations = counted.Where(x => x.Allowed).Select(x => x.Reservation!).ToArray();
    reservations[0].Unknown(); reservations[0].NotDispatched();
    Check("Unknown retains a global slot despite cancellation-style release", limited.Snapshot() is { Occupied: 2, Unknown: 1 });
    limited.SetLimit(1);
    Check("lowering the limit preserves existing claims and blocks new starts", limited.Snapshot().Occupied == 2
        && limited.CheckStart(rc, Location("/new", rc.HostId)).Code == "workspace_global_capacity");
    limited.SetLimit(null);
    Check("unlimited restores admission without deleting prior uncertainty", Reserve(limited, "unlimited-new", rc, Location("/new", rc.HostId)).Allowed
        && limited.Snapshot() is { Limit: null, Occupied: 3, Unknown: 1 });
    bool invalidLimit = false;
    try { limited.SetLimit(0); } catch (ArgumentOutOfRangeException) { invalidLimit = true; }
    Check("invalid limit cannot silently alter current policy", invalidLimit && limited.Snapshot().Limit == null);

    var dedupe = new WorkspaceWriteCoordinator(new TestResolver(), ["legacy", "owned"], () => now, 1);
    dedupe.RestoreUnresolved(Claim("start", a, Location(), "same-turn", now, false));
    dedupe.RestoreUnresolved(Claim("steer", a, Location(), "same-turn", now, false));
    dedupe.RestoreUnresolved(Claim("approval", a, Location(), "same-turn", now));
    Check("recovered start steer and approval share one exact task-turn slot", dedupe.Snapshot() is { Occupied: 1, Unknown: 1, RecoveryReady: false });
    dedupe.CompleteRecovery("legacy");
    Check("capacity remains closed until both recovery sources finish", dedupe.CheckStart(b, Location(@"C:\elsewhere")).Code == "workspace_recovery_required");
    dedupe.CompleteRecovery("owned");
    Check("restored Unknown blocks a different directory after complete recovery", dedupe.CheckStart(b, Location(@"C:\elsewhere")).Code == "workspace_global_capacity");
    dedupe.Observe(Evidence(a, "same-turn", WorkspaceTaskState.Completed));
    Check("ordinary terminal cannot free recovered Unknown capacity", dedupe.Snapshot() is { Occupied: 1, Unknown: 1 });
    dedupe.Observe(Evidence(a, "same-turn", WorkspaceTaskState.Completed, true));
    Check("exact reconciled terminal frees global capacity", dedupe.Snapshot().Occupied == 0
        && dedupe.CanStart(b, Location(@"C:\elsewhere")));
    dedupe.RestoreUnresolved(Claim("unbound-1", a, Location(), null, now));
    dedupe.RestoreUnresolved(Claim("unbound-2", a, Location(), null, now));
    Check("unbound attempts are not guessed to be the same execution", dedupe.Snapshot() is { Occupied: 2, Unknown: 2 });

    var reserved = new WorkspaceWriteCoordinator(new TestResolver(), clock: () => now, maxConcurrentExecutions: 1);
    var lease = Reserve(reserved, "exact-owner", a, Location()).Reservation!;
    Check("exact reservation can recheck its own admission without taking a second slot",
        reserved.CheckStart(a, Location(), lease, Owner).Allowed && reserved.Snapshot().Occupied == 1);
    Check("reservation cannot authorize another task or directory",
        !reserved.CheckStart(b, Location(), lease, Owner).Allowed && !reserved.CheckStart(a, Location(@"C:\other"), lease, Owner).Allowed);
    var unrelated = new WorkspaceWriteCoordinator(new TestResolver(), clock: () => now);
    Check("reservation from another coordinator is not proof", !unrelated.CheckStart(a, Location(), lease, Owner).Allowed);
    reserved.Observe(Evidence(b, "outside-turn", WorkspaceTaskState.Running));
    Check("reserved recheck still rejects a new conflicting external writer", !reserved.CheckStart(a, Location(), lease, Owner).Allowed);
    lease.NotDispatched();
    Check("known no-dispatch release removes capacity without erasing external conflict",
        reserved.Snapshot().Occupied == 0 && !reserved.CanStart(a, Location()));
    var creationOnly = new WorkspaceWriteCoordinator(new TestResolver(), clock: () => now, maxConcurrentExecutions: 1);
    creationOnly.Observe(Evidence(a, null, WorkspaceTaskState.Unknown) with { Kind = WorkspaceEvidenceKind.CreationOnly, Fresh = false });
    Check("read-only creation history does not pretend to occupy execution capacity",
        creationOnly.Snapshot().Occupied == 0 && creationOnly.CanStart(b, Location(@"C:\other")) && !creationOnly.CanStart(b, Location()));
    var creationLease = Reserve(creationOnly, "empty-create", null, Location(@"C:\other")).Reservation!;
    creationLease.NotDispatched();
    Check("known empty creation releases its temporary slot", creationOnly.Snapshot().Occupied == 0);
    var owners = new WorkspaceWriteCoordinator(new TestResolver(), clock: () => now, maxConcurrentExecutions: 2);
    owners.RestoreUnresolved(Claim("owner-a", a, Location(), "shared-turn", now, false));
    owners.RestoreUnresolved(Claim("owner-b", a, Location(), "shared-turn", now, false) with { OwnerScope = "different-owner" });
    Check("same backend task and turn from different owners never merge capacity", owners.Snapshot().Occupied == 2);
    owners.Observe(Evidence(a, "shared-turn", WorkspaceTaskState.Running));
    Check("another owner claim cannot be borrowed for amend", !owners.CanAmend(a, "shared-turn", Location(), Owner)
        && !owners.CanAmend(a, "shared-turn", Location(), "different-owner"));
    owners.Observe(Evidence(a, "shared-turn", WorkspaceTaskState.Completed));
    Check("one owner terminal cannot release another owner claim", owners.Snapshot().Occupied == 1
        && owners.Blockers().Single().ClaimId == "owner-b");
    owners.Observe(Evidence(a, "shared-turn", WorkspaceTaskState.Completed, true) with { OwnerScope = null });
    Check("unscoped terminal evidence never releases an owned claim", owners.Snapshot().Occupied == 1);
    owners.Observe(Evidence(a, "shared-turn", WorkspaceTaskState.Completed, true) with { OwnerScope = "different-owner" });
    Check("exact second owner evidence releases only its remaining claim", owners.Snapshot().Occupied == 0);
    owners.RestoreUnresolved(Claim("legacy-unscoped", a, Location(), "old-turn", now, false) with { OwnerScope = null });
    owners.Observe(Evidence(a, "old-turn", WorkspaceTaskState.Completed, true));
    Check("old unscoped claim is uncertain and cannot acquire current ownership", owners.Snapshot() is { Occupied: 1, Unknown: 1 });
    Check("new dispatch requires explicit trusted owner scope", owners.TryReserve("missing-owner", b, Location(@"C:\new")).Code == "workspace_owner_unverified");

    var actual = new WorkspacePathResolver();
    Check("production resolver rejects missing local directory", actual.Resolve(Location(Path.Combine(directory, "absent"))) == null);
    Check("production resolver validates real isolated local directory", actual.Resolve(Location(directory)) is { HostWide: false });
    var realScope = actual.Resolve(Location(directory));
    Check("production scope uses filesystem volume identity and normalized directory",
        realScope?.Path?.StartsWith(@"\\?\VOLUME{", StringComparison.Ordinal) == true);
    Check("production resolver unifies case slash and trailing separator variants",
        realScope == actual.Resolve(Location(directory.ToUpperInvariant().Replace('\\', '/') + "/")));
    Check("production resolver rejects traversal and alternate data streams",
        actual.Resolve(Location(@"C:\x\..\y")) == null && actual.Resolve(Location(@"C:\x:stream")) == null);
    Check("production resolver rejects trailing dot space UNC and device path aliases",
        actual.Resolve(Location(directory + ".")) == null && actual.Resolve(Location(directory + " ")) == null
        && actual.Resolve(Location(@"\\server\share\project")) == null
        && actual.Resolve(Location(@"\\?\" + directory)) == null);
    string child = Path.Combine(directory, "Long Directory For Synthetic Alias Verification");
    Directory.CreateDirectory(child);
    var shortName = new StringBuilder(32768);
    uint shortLength = Native.GetShortPathName(child, shortName, (uint)shortName.Capacity);
    if (shortLength > 0 && shortLength < shortName.Capacity
        && !shortName.ToString().Equals(child, StringComparison.OrdinalIgnoreCase))
        Check("real filesystem short path resolves to the same normalized directory",
            actual.Resolve(Location(child)) == actual.Resolve(Location(shortName.ToString())));
    else skips.Add("Real 8.3 alias was unavailable or disabled for the isolated directory; no system setting was changed.");
    var aliases = new WorkspaceWriteCoordinator(new AliasResolver(), clock: () => now);
    Reserve(aliases, "canonical-alias", a, Location(@"C:\long-workspace"));
    Check("verified 8.3 and drive aliases share the coordinator scope through canonical resolver evidence",
        !Reserve(aliases, "short-alias", b, Location(@"C:\LONGWO~1")).Allowed
        && !Reserve(aliases, "drive-alias", b, Location(@"Z:\")).Allowed);
    Check("production resolver does not use Windows normalization for remote paths",
        actual.Resolve(Location("/tmp/project", "remote-ssh-discovered:unit")) is { HostWide: true, Path: null });
}
catch (Exception ex) { checks["failure"] = false; await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
finally
{
    await File.WriteAllTextAsync(Path.Combine(directory, "workspace-checks.json"), JsonSerializer.Serialize(new { checks, skips, passed = checks.Values.All(x => x), count = checks.Count,
        realTaskOperations = 0, realNetworkConnections = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count(x => x.Value)}/{checks.Count} workspace checks passed");
}
sealed class AliasResolver : IWorkspacePathResolver
{
    public WorkspaceScope? Resolve(WorkspaceLocation location)
        => new(location.HostId, @"\\?\VOLUME{SYNTHETIC}\LONG-WORKSPACE", false);
}
static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint capacity);
}

sealed class TestResolver : IWorkspacePathResolver
{
    public WorkspaceScope? Resolve(WorkspaceLocation location) => location.HostId == "local"
        ? new(location.HostId, location.Cwd.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant(), false)
        : new(location.HostId, null, true);
}
