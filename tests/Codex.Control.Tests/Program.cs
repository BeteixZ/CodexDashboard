using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Codex.Control;
using CodexDashboard;

if (args.Contains("--fake-rpc-child"))
{
    if (args.Contains("--project-source-bootstrap"))
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { projectSourceIdentity = new string('a', 64) }));
    await Console.Out.FlushAsync(); await FakeChild.RunAsync(); return;
}
if (args.Length > 0 && args[0] is "--provenance-create-child" or "--provenance-restore-child")
{ await ProvenanceTests.RunChildAsync(args); return; }
string directory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("control-test-results");
Directory.CreateDirectory(directory);
var checks = new Dictionary<string, bool>();
void Check(string name, bool value)
{
    checks.Add(name, value);
    if (!value) throw new InvalidOperationException(name);
}
async Task Reject(string name, Func<Task> action, string? code = null)
{
    try { await action(); Check(name, false); }
    catch (ControlException ex) { Check(name, code == null || ex.Code == code); }
}
var now = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
OwnedServerConfiguration Config(bool verified = true, bool synthetic = true)
{
    var config = new OwnedServerConfiguration
    {
        Connection = new(new("local", "synthetic-profile"), OwnedBackend.LocalStdio, synthetic),
        ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = OperatingSystem.IsWindows() ? "windows" : "linux",
        ExpectedCodexHome = Path.Combine(directory, "synthetic-codex-home"),
        Projects = ImmutableDictionary<string, string>.Empty.Add("sample", Path.Combine(directory, "sample")),
        Models = ImmutableHashSet.Create("synthetic-model"), RequestTimeout = TimeSpan.FromSeconds(2),
        ApprovalLifetime = TimeSpan.FromMinutes(5)
    };
    if (verified) config = config with { Verification = new(config.Connection, config.ExpectedVersion, config.ExpectedPlatformOs,
        OwnedVerification.ConfigurationDigest(config), OwnedVerification.SupportedMethods.ToImmutableHashSet(),
        VerificationKind.SyntheticReplay, "synthetic-contract-replay-only", now.AddMinutes(-1), now.AddHours(1)) };
    return config;
}
CreateOwnedTask Request(OwnedServerConfiguration c) => new("sample", c.Projects["sample"], "synthetic-model", "on-request", "workspace-write");
void Running(FakeRpc rpc, OwnedTaskHandle task, string turn) => rpc.Emit("turn/started", new { threadId = task.Identity.ThreadId, turn = FakeRpc.Turn(turn, "inProgress") });
void Complete(FakeRpc rpc, OwnedTaskHandle task, string turn, string status = "completed")
    => rpc.Emit("turn/completed", new { threadId = task.Identity.ThreadId, turn = FakeRpc.Turn(turn, status) });
void Approval(FakeRpc rpc, OwnedTaskHandle task, string turn, object id)
    => rpc.Emit("item/commandExecution/requestApproval", new
    {
        threadId = task.Identity.ThreadId, turnId = turn, itemId = "synthetic-command",
        command = "echo synthetic", cwd = Request(Config()).Cwd, startedAtMs = now.ToUnixTimeMilliseconds()
    }, id);
try
{
    var disabledConfig = Config(false);
    var disabledRpc = new FakeRpc(disabledConfig);
    await using (var disabled = new OwnedAppServerClient(disabledConfig, disabledRpc, () => now, TestProvenance.New(disabledConfig)))
    {
        await disabled.InitializeAsync();
        Check("all runtime controls disabled without verification", disabled.CapabilityFailure("thread/start") == "runtime_verification_required");
        await Reject("disabled create sends no request", () => disabled.CreateAsync(Guid.NewGuid(), Request(disabledConfig)), "runtime_verification_required");
        Check("initialization declares stable API only", !disabledRpc.Calls[0].Parameters.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean()
            && disabledRpc.Calls.Select(x => x.Method).SequenceEqual(["initialize", "initialized"]));
    }
    var runtimeConfig = Config(synthetic: false);
    await using (var desktop = new OwnedAppServerClient(Config(false), new FakeRpc(Config(false))
        { UserAgent = "Codex Desktop/0.147.0 (Windows 10.0.26220; x86_64) dumb (codex_assistant_owned; 1.0.0)" }, () => now))
    {
        await desktop.InitializeAsync();
        Check("desktop user agent uses product version not OS or client version", desktop.CapabilityFailure("thread/start") == "runtime_verification_required");
    }
    await using (var wrongDesktop = new OwnedAppServerClient(Config(false), new FakeRpc(Config(false))
        { UserAgent = "Codex Desktop/0.148.0 (Windows 10.0.26220; x86_64) dumb (client; 0.147.0)" }, () => now))
        await Reject("client version cannot hide wrong server version", () => wrongDesktop.InitializeAsync(), "initialize_version_platform_or_profile_mismatch");
    Check("synthetic verification cannot enable production", OwnedVerification.CapabilityFailure(runtimeConfig, "thread/start", now) == "synthetic_evidence_cannot_enable_runtime");
    var config = Config();
    Check("verification scope binds configured directory", OwnedVerification.CapabilityFailure(config with
    { Projects = config.Projects.SetItem("sample", Path.Combine(directory, "other")) }, "thread/start", now) == "verification_scope_mismatch");
    Check("verification expires", OwnedVerification.CapabilityFailure(config, "thread/start", now.AddHours(2)) == "verification_expired_or_invalid");
    Check("unverified individual method remains closed", OwnedVerification.CapabilityFailure(config with
    { Verification = config.Verification! with { Methods = ImmutableHashSet.Create("thread/read") } }, "turn/interrupt", now) == "method_not_runtime_verified");
    var wrongRpc = new FakeRpc(config) { Version = "0.148.0" };
    await using (var wrong = new OwnedAppServerClient(config, wrongRpc, () => now, TestProvenance.New(config)))
        await Reject("initialize rejects wrong server version", () => wrong.InitializeAsync(), "initialize_version_platform_or_profile_mismatch");
    var wrongHome = new FakeRpc(config) { Home = Path.Combine(directory, "different-home") };
    await using (var wrong = new OwnedAppServerClient(config, wrongHome, () => now, TestProvenance.New(config)))
        await Reject("initialize rejects wrong profile", () => wrong.InitializeAsync(), "initialize_version_platform_or_profile_mismatch");

    var rpc = new FakeRpc(config);
    await using var client = new OwnedAppServerClient(config, rpc, () => now, TestProvenance.New(config));
    var observed = new List<ControlObservation>();
    using var eventsCancel = new CancellationTokenSource();
    var consume = Task.Run(async () =>
    {
        try { await foreach (var observation in client.ReadObservationsAsync(eventsCancel.Token)) lock (observed) observed.Add(observation); }
        catch (OperationCanceledException) { }
    });
    await client.InitializeAsync();
    await Reject("create requires configured project directory", () => client.CreateAsync(Guid.NewGuid(), Request(config) with { Cwd = Path.Combine(directory, "wrong") }));
    await Reject("create requires configured model", () => client.CreateAsync(Guid.NewGuid(), Request(config) with { Model = "other-model" }));
    await Reject("never approval is not enabled", () => client.CreateAsync(Guid.NewGuid(), Request(config) with { ApprovalPolicy = "never" }));
    await Reject("dangerous sandbox is not enabled", () => client.CreateAsync(Guid.NewGuid(), Request(config) with { Sandbox = "danger-full-access" }));
    Check("invalid create checks precede network dispatch", rpc.Calls.Count == 2);
    var creation = await client.CreateAsync(Guid.NewGuid(), Request(config));
    var task = creation.Task ?? throw new Exception("Synthetic create failed: " + creation.Receipt.Code);
    Check("create returns scoped assistant-owned identity", creation.Receipt.State == ControlState.Accepted
        && task.Identity.Endpoint == config.Connection.Endpoint && client.Snapshot(task).State == OwnedTaskState.Idle);
    var otherRpc = new FakeRpc(config);
    await using (var other = new OwnedAppServerClient(config, otherRpc, () => now, TestProvenance.New(config)))
    {
        await other.InitializeAsync();
        await Reject("foreign client cannot use owned handle", () => other.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "work"), "assistant_owned_handle_required");
    }
    await Reject("stale task revision fails before dispatch", () => client.StartTurnAsync(Guid.NewGuid(), task, 0, "work"), "stale_task_revision");
    Guid startId = Guid.NewGuid();
    var started = await client.StartTurnAsync(startId, task, client.Snapshot(task).Revision, "synthetic work");
    string turnId = started.TurnId!;
    Check("turn start response is accepted not running", started.State == ControlState.Accepted && client.Snapshot(task).State == OwnedTaskState.Accepted);
    Check("client message id preserves caller operation identity", rpc.Calls.Last().Parameters.GetProperty("clientUserMessageId").GetString() == startId.ToString("D"));
    await Reject("cannot start a second accepted turn", () => client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "again"), "thread_not_observed_idle");
    Running(rpc, task, turnId);
    Check("turn started event establishes running", client.Snapshot(task).State == OwnedTaskState.Running && client.GetOperation(startId).State == ControlState.Running);
    int before = rpc.Calls.Count;
    await Reject("wrong expected turn cannot steer", () => client.SteerAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "other-turn", "change"), "expected_active_turn_mismatch");
    Check("wrong turn never reached server", before == rpc.Calls.Count);
    var steer = await client.SteerAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, turnId, "keep constraints");
    Check("steer serializes expectedTurnId and remains accepted", steer.State == ControlState.Accepted
        && rpc.Calls.Last().Parameters.GetProperty("expectedTurnId").GetString() == turnId);
    var interrupt = await client.InterruptAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, turnId);
    Check("interrupt ack does not mean interrupted", interrupt.State == ControlState.Accepted && client.Snapshot(task).State == OwnedTaskState.Running);
    Complete(rpc, task, turnId, "interrupted");
    Check("interrupted event confirms exact operation", client.GetOperation(interrupt.OperationId).State == ControlState.Interrupted && client.Snapshot(task).State == OwnedTaskState.Interrupted);
    await Reject("operation identifier cannot be reused", () => client.StartTurnAsync(startId, task, client.Snapshot(task).Revision, "repeat"), "operation_id_missing_or_reused");
    var rename = await client.RenameAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "Renamed synthetic task");
    Check("rename targets exact owned thread", rename.State == ControlState.Accepted && rpc.Calls.Last().Method == "thread/name/set"
        && rpc.Calls.Last().Parameters.GetProperty("threadId").GetString() == task.Identity.ThreadId);
    var archive = await client.ArchiveAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision);
    Check("archive administrative acceptance is separate from turn outcome", archive.State == ControlState.Accepted && client.Snapshot(task).State == OwnedTaskState.Archived);
    var unarchive = await client.UnarchiveAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision);
    Check("unarchive validates exact original identity", unarchive.State == ControlState.Accepted && client.Snapshot(task).State == OwnedTaskState.Idle);

    string earlyTurn = Guid.NewGuid().ToString();
    rpc.Override = (method, parameters, _) =>
    {
        if (method != "turn/start") return Task.FromResult(rpc.Reply(method, parameters));
        Running(rpc, task, earlyTurn); Complete(rpc, task, earlyTurn);
        return Task.FromResult(FakeRpc.J(new { turn = FakeRpc.Turn(earlyTurn, "inProgress") }));
    };
    var early = await client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "early events");
    Check("lifecycle before response never regresses to accepted", early.State == ControlState.Completed && client.Snapshot(task).State == OwnedTaskState.Completed);
    Running(rpc, task, earlyTurn);
    Check("late started event cannot reopen completed turn", client.Snapshot(task).State == OwnedTaskState.Completed);
    rpc.Override = (method, parameters, _) =>
    {
        if (method == "turn/start") Complete(rpc, task, earlyTurn);
        return Task.FromResult(rpc.Reply(method, parameters));
    };
    var approvalStart = await client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "approval scenario");
    Check("previous turn completion cannot bind to pending new start", approvalStart.State == ControlState.Accepted && approvalStart.TurnId != earlyTurn);
    rpc.Override = null;
    string approvalTurn = approvalStart.TurnId!; Running(rpc, task, approvalTurn);
    Complete(rpc, task, earlyTurn);
    Check("older turn event cannot overwrite current turn", client.Snapshot(task).CurrentTurnId == approvalTurn && client.Snapshot(task).State == OwnedTaskState.Running);
    long focus = client.SetFocus(task);
    Approval(rpc, task, approvalTurn, 7);
    var approval = client.PendingApprovals().Single();
    Check("approval includes precise scope and decisions", approval.Task == task.Identity && approval.TurnId == approvalTurn && approval.ProjectId == "sample"
        && approval.ItemId == "synthetic-command" && approval.Decisions.Length == 4 && approval.RequestId.ValueKind == JsonValueKind.Number);
    await Reject("approval wrong project is rejected", () => client.RespondApprovalAsync(Guid.NewGuid(), approval.Token, task.Identity, approvalTurn, "wrong", focus, ApprovalDecision.Accept), "approval_context_mismatch");
    await Reject("approval wrong turn is rejected", () => client.RespondApprovalAsync(Guid.NewGuid(), approval.Token, task.Identity, "wrong", "sample", focus, ApprovalDecision.Accept), "approval_context_mismatch");
    var approved = await client.RespondApprovalAsync(Guid.NewGuid(), approval.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Accept);
    Check("approval write without resolution remains unknown", approved.State == ControlState.Unknown && rpc.Responses.Single().Id.GetInt32() == 7);
    await Reject("approval is consumed once before response", () => client.RespondApprovalAsync(Guid.NewGuid(), approval.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Accept), "approval_expired_consumed_or_missing");
    rpc.Emit("serverRequest/resolved", new { threadId = task.Identity.ThreadId, requestId = 7 });
    Check("resolved request confirms approval response acceptance", client.GetOperation(approved.OperationId).State == ControlState.Accepted);
    rpc.Emit("thread/status/changed", new { threadId = task.Identity.ThreadId, status = new { type = "active", activeFlags = Array.Empty<string>() } });
    Check("active status event clears observed approval wait", client.Snapshot(task).State == OwnedTaskState.Running);
    Approval(rpc, task, approvalTurn, "7");
    var stringApproval = client.PendingApprovals().Single();
    Check("numeric and string request ids stay distinct", stringApproval.RequestId.ValueKind == JsonValueKind.String);
    rpc.Emit("serverRequest/resolved", new { threadId = task.Identity.ThreadId, requestId = "7" });
    await Reject("already resolved request cannot be approved", () => client.RespondApprovalAsync(Guid.NewGuid(), stringApproval.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Accept));
    Approval(rpc, task, approvalTurn, 8);
    var expired = client.PendingApprovals().Single(); now = now.AddMinutes(6);
    await Reject("expired approval cannot apply to later requests", () => client.RespondApprovalAsync(Guid.NewGuid(), expired.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Accept));
    Approval(rpc, task, approvalTurn, 9);
    var switched = client.PendingApprovals().Single(); client.SetFocus(null); client.SetFocus(task);
    await Reject("focus switch invalidates old approval context", () => client.RespondApprovalAsync(Guid.NewGuid(), switched.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Accept));
    focus = client.SetFocus(task);
    Approval(rpc, task, approvalTurn, 10);
    var concurrentApproval = client.PendingApprovals().Single();
    int responseCount = rpc.Responses.Count, consumed = 0, declined = 0;
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
    {
        try
        {
            await client.RespondApprovalAsync(Guid.NewGuid(), concurrentApproval.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Decline);
            Interlocked.Increment(ref consumed);
        }
        catch (ControlException) { Interlocked.Increment(ref declined); }
    })));
    Check("concurrent approval replies consume exactly once", consumed == 1 && declined == 11 && rpc.Responses.Count == responseCount + 1);
    rpc.Emit("item/fileChange/requestApproval", new { threadId = task.Identity.ThreadId, turnId = approvalTurn,
        itemId = "missing-file-details", startedAtMs = now.ToUnixTimeMilliseconds(), reason = "synthetic file change" }, 11);
    var noDetails = client.PendingApprovals().Single();
    Check("file approval without observed changes cannot grant permission", !noDetails.Decisions.Contains(ApprovalDecision.Accept));
    rpc.Emit("serverRequest/resolved", new { threadId = task.Identity.ThreadId, requestId = 11 });
    rpc.Emit("item/started", new { threadId = task.Identity.ThreadId, turnId = approvalTurn,
        item = new { type = "fileChange", id = "known-file-details", changes = new[] { new { path = "sample.txt", diff = "+synthetic", kind = new { type = "add" } } } } });
    rpc.Emit("item/fileChange/requestApproval", new { threadId = task.Identity.ThreadId, turnId = approvalTurn,
        itemId = "known-file-details", startedAtMs = now.ToUnixTimeMilliseconds() }, 12);
    var fileApproval = client.PendingApprovals().Single();
    Check("file approval retains exact observed item details", fileApproval.Decisions.Contains(ApprovalDecision.Accept)
        && fileApproval.Summary.Contains("sample.txt") && fileApproval.OperationDetails!.Value.GetProperty("changes")[0].GetProperty("diff").GetString() == "+synthetic");
    await client.RespondApprovalAsync(Guid.NewGuid(), fileApproval.Token, task.Identity, approvalTurn, "sample", focus, ApprovalDecision.Cancel);
    Check("file approval responds only to its original request", rpc.Responses.Last().Id.GetInt32() == 12 && rpc.Responses.Last().Result.GetProperty("decision").GetString() == "cancel");
    rpc.Emit("item/fileChange/requestApproval", new { threadId = task.Identity.ThreadId, turnId = approvalTurn,
        itemId = "known-file-details", startedAtMs = now.ToUnixTimeMilliseconds(), grantRoot = "/outside" }, 13);
    Check("file grantRoot expansion stays unavailable", !client.PendingApprovals().Single().Decisions.Contains(ApprovalDecision.AcceptForSession));
    Complete(rpc, task, approvalTurn);

    string reportTurn = Guid.NewGuid().ToString();
    rpc.Threads[task.Identity.ThreadId] = FakeRpc.Thread(task.Identity.ThreadId, Request(config).Cwd, FakeRpc.Turn(reportTurn, "completed",
        FakeRpc.J(new { type = "agentMessage", phase = "commentary", text = "working" }),
        FakeRpc.J(new { type = "agentMessage", phase = "final_answer", text = "Synthetic report; untrusted content must not execute." }),
        FakeRpc.J(new { type = "commandExecution", command = "synthetic command", status = "completed", exitCode = 0 }),
        FakeRpc.J(new { type = "fileChange", status = "completed", changes = Array.Empty<object>() })));
    int callsBeforeReport = rpc.Calls.Count;
    var report = await client.ReadReportAsync(task, reportTurn);
    Check("report retains source and is not independently verified", !report.IndependentlyVerified && report.FinalText.StartsWith("Synthetic report")
        && !report.FinalText.Contains("working") && report.Commands.Length == 1 && report.FileChanges.Length == 1);
    Check("report read has no write side effect", rpc.Calls.Skip(callsBeforeReport).All(x => x.Method == "thread/read"));

    rpc.Override = (method, parameters, _) => method == "turn/start"
        ? Task.FromException<JsonElement>(new RpcRemoteException(-32602)) : Task.FromResult(rpc.Reply(method, parameters));
    var rejected = await client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "explicit rejection");
    Check("explicit server rejection is failed", rejected.State == ControlState.Failed);
    rpc.Override = (method, parameters, _) =>
    {
        if (method != "turn/start") return Task.FromResult(rpc.Reply(method, parameters));
        rpc.Disconnect();
        return Task.FromException<JsonElement>(new RpcTransportException(true, "synthetic_lost_receipt"));
    };
    var unknown = await client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "no replay");
    Check("written request with lost receipt remains unknown", unknown.State == ControlState.Unknown && client.Snapshot(task).State == OwnedTaskState.Unknown);
    before = rpc.Calls.Count;
    await Reject("disconnected mutation cannot replay", () => client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "retry"), "not_initialized_or_disconnected");
    Check("no automatic retry after disconnect", rpc.Calls.Count == before);
    var lateHandler = rpc.CaptureIncoming();
    var replacement = new FakeRpc(config);
    replacement.Threads[task.Identity.ThreadId] = rpc.Threads[task.Identity.ThreadId];
    await client.ReconnectForReadAsync(replacement);
    Check("reconnect performs initialization only", replacement.Calls.Select(x => x.Method).SequenceEqual(["initialize", "initialized"]));
    var reconciled = await client.ReconcileAsync(task);
    Check("reconciliation reads exact task without resuming", reconciled.Task.Identity == task.Identity && reconciled.WritesRemainBlocked
        && replacement.Calls.Last().Method == "thread/read");
    Check("read returns its own atomic ordered observation", reconciled.Observation == client.LatestTurnObservation(task)
        && reconciled.Observation?.Method == "thread/read" && client.IsCommandReadCurrent(task, reconciled.Observation.Sequence));
    replacement.Emit("item/completed", new { threadId = task.Identity.ThreadId, turnId = reconciled.Task.CurrentTurnId,
        item = new { id = "changed-command", type = "commandExecution", command = "synthetic tests", status = "failed", exitCode = 1 } });
    Check("command change retires read proof but preserves lifecycle observation", !client.IsCommandReadCurrent(task, reconciled.Observation!.Sequence)
        && client.LatestTurnObservation(task) == reconciled.Observation);
    var reread = await client.ReconcileAsync(task);
    Check("new exact read reestablishes command proof with new sequence", reread.Observation!.Sequence > reconciled.Observation.Sequence
        && client.IsCommandReadCurrent(task, reread.Observation.Sequence));
    var savedThread = replacement.Threads[task.Identity.ThreadId];
    replacement.Threads[task.Identity.ThreadId] = FakeRpc.Thread(task.Identity.ThreadId, Request(config).Cwd,
        FakeRpc.Turn(reread.Task.CurrentTurnId!, "inProgress"));
    var staleRead = await client.ReconcileAsync(task);
    Check("ignored stale running read cannot borrow old completed proof", staleRead.Observation == null
        && !client.IsCommandReadCurrent(task, reread.Observation.Sequence) && client.Snapshot(task).State == OwnedTaskState.Completed);
    replacement.Threads[task.Identity.ThreadId] = savedThread;
    lateHandler?.Invoke(new("turn/started", FakeRpc.J(new { threadId = task.Identity.ThreadId, turn = FakeRpc.Turn(reportTurn, "inProgress") })));
    Check("late old connection event ignored", client.Snapshot(task).State == OwnedTaskState.Completed);
    await Reject("read reconciliation does not enable writes", () => client.StartTurnAsync(Guid.NewGuid(), task, client.Snapshot(task).Revision, "new command"), "reconnected_read_only_reconciliation");
    var previous = client.Snapshot(task);
    replacement.Threads[task.Identity.ThreadId] = FakeRpc.Thread("foreign", Request(config).Cwd);
    await Reject("read result cannot change task ownership", () => client.ReconcileAsync(task), "read_thread_identity_or_directory_mismatch");
    Check("identity mismatch leaves last verified observation intact", client.Snapshot(task) == previous);
    await client.DisposeAsync(); await consume.WaitAsync(TimeSpan.FromSeconds(5));
    lock (observed)
    {
        Check("control observations preserve caller operation identity", observed.Any(x => x.OperationId == startId && x.State == ControlState.Running));
        Check("approval resolution carries explicit reconciliation evidence", observed.Any(x => x.OperationId == approved.OperationId
            && x.Code == "server_request_resolved" && x.Reconciliation));
        Check("approval event wakes host without exposing command text", observed.Any(x => x.Code == "approval_requested"
            && x.OperationId == null && x.Task == task.Identity));
        Check("observations ordered and contain safe codes only", observed.Select(x => x.Sequence).SequenceEqual(observed.Select(x => x.Sequence).Order())
            && observed.All(x => !x.Code.Contains("synthetic work") && !x.Code.Contains(directory)));
    }

    await AdversarialTests.RunAsync(config, now, Check);
    await ProvenanceTests.RunAsync(directory, config, now, Check);
    await WireTests.RunAsync(directory, config.Connection, Check);
    if (args.Length > 1) SchemaTests.Run(Path.GetFullPath(args[1]), Check);
}
catch (Exception ex)
{
    checks["failure"] = false;
    await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString());
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
finally
{
    await File.WriteAllTextAsync(Path.Combine(directory, "control-checks.json"), JsonSerializer.Serialize(new
    {
        passed = checks.Values.All(x => x), count = checks.Count, checks,
        realCodexProcessesStarted = 0, realTaskOperations = 0, productionCredentialsRead = false
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count(x => x.Value)}/{checks.Count} checks passed");
}
