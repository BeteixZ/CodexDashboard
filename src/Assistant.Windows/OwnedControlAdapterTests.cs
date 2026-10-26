using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Synthetic RPC + newly created test-only DPAPI files. No settings, credentials, real process,
// task transport, WeChat channel, model service or existing user session is opened.
internal static class OwnedControlAdapterTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--owned-control-test");
        bool recheckOnly = args.Contains("--owned-recheck-test");
        bool focusOnly = args.Contains("--owned-focus-policy-test");
        if (recheckOnly) index = Array.IndexOf(args, "--owned-recheck-test");
        if (focusOnly) index = Array.IndexOf(args, "--owned-focus-policy-test");
        if (index < 0) return false;
        try
        {
            if (focusOnly)
            {
                var checks = new Dictionary<string, bool>();
                await PersistedFocusAsync(args[index + 1], (name, passed) => { checks.Add(name, passed); if (!passed) throw new InvalidOperationException(name); });
                await File.WriteAllTextAsync(Path.Combine(args[index + 1], "checks.json"), JsonSerializer.Serialize(checks));
            }
            else if (recheckOnly) await RecheckReasonAsync(args[index + 1]);
            else await RunAsync(args[index + 1]);
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RecheckReasonAsync(string directory)
    {
        var now = new TestClock().UtcNow;
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using var f = await Fixture.OpenAsync(directory, workspace: workspace);
        f.OnConnect = () => workspace.Observe(new(new CanonicalTaskIdentity("local", "late-task"),
            new WorkspaceLocation("local", "windows", f.Profile.Creation.Cwd), "late-turn", WorkspaceTaskState.Running,
            now, true, WorkspaceEvidenceKind.Collector, OwnerScope: "other-owner"));
        var result = await f.ExecuteAsync("创建 示例范围", CreateCall());
        bool preserved = result.Result.State == ToolExecutionState.Rejected
            && result.Result.Reason!.Contains("目录可能有运行任务")
            && !f.Rpc!.Calls.Contains("thread/start");
        await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { specificRecheckReasonPreserved = preserved, taskCreationCalls = f.Rpc!.Calls.Count(c => c == "thread/start") }));
        if (!preserved) throw new InvalidOperationException("specific_recheck_reason_lost");
    }
    internal static async Task RunAsync(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_windows_test_directory_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_test_directory_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) { checks.Add(name, passed); if (!passed) throw new InvalidOperationException(name); }
        try
        {
            await using (var disabled = await Fixture.OpenAsync(Path.Combine(directory, "disabled"), verified: false))
            {
                Check("no verification disables real control without constructing a connection",
                    !disabled.Adapter.GetCapability("owned.create_task").Available && disabled.ConnectCount == 0);
                var request = await disabled.ReceiveAsync("新建 demo", CreateCall());
                Check("disabled profile cannot authorize create", !(await disabled.AuthorizeAsync(request)).Allowed && disabled.ConnectCount == 0);
                var read = await disabled.ReadAsync("list_owned_tasks", new { });
                Check("local listing starts no process", read.State == ToolExecutionState.ReadOnlySuccess && disabled.ConnectCount == 0);
            }
            await using (var f = await Fixture.OpenAsync(Path.Combine(directory, "active")))
            {
                var schema = OwnedControlToolAdapter.Catalog();
                schema.TryGet("create_task", out var createDefinition);
                Check("model schema cannot supply executable verification or owner",
                    !createDefinition.Parameters.TryValidate("""{"configurationId":"demo","executable":"cmd","owner":"assistant"}""", out _, out _));
                var vague = await f.ReceiveAsync("看看当前任务", CreateCall());
                var loop = new ToolAgentLoop(f.Ledger, new SingleCallModel(vague.Call), schema, f.Adapter, f.Adapter);
                var loopResult = await loop.RunAsync(f.Identity, vague.Message.SourceMessageId);
                Check("ambiguous model-created action returns NeedsConfirmation without executing",
                    loopResult.State == ToolLoopState.NeedsConfirmation && f.ConnectCount == 0);
                foreach (string body in new[] { "不要新建 demo", "如果通过后新建 demo", "引用：新建 demo", "新建 demo 但不要创建", "新建 missing" })
                {
                    var request = await f.ReceiveAsync(body, CreateCall());
                    Check("current authority rejects " + body, !(await f.AuthorizeAsync(request)).Allowed);
                }
                var plain = await f.ReceiveAsync("新建 demo", CreateCall());
                var forgedContext = new ToolExecutionContext(f.Identity, plain.Message.SourceMessageId, plain.Message.Revision,
                    Guid.NewGuid(), Guid.NewGuid(), "forged");
                var missingOperation = await f.Adapter.ExecuteAsync(forgedContext, plain.Call, default);
                Check("random operation ID is not durable authority", missingOperation.State == ToolExecutionState.Rejected && f.ConnectCount == 0);

                var create = await f.ExecuteAsync("新建 demo", CreateCall());
                var task = f.Runtime.Tasks().Single();
                Check("create persists dispatch before initialization and thread start", f.DispatchSeenAtConnect && f.Rpc!.DispatchSeenAtCreate);
                Check("create keeps original auth target immutable and reports actual binding separately",
                    create.Result.State == ToolExecutionState.Accepted && create.Operation.Task == null
                    && create.Result.Task == null && create.Result.TargetKey == "owned-create/demo"
                    && create.Result.Facts.GetProperty("observedTask").GetProperty("ThreadId").GetString() == task.Handle.Identity.ThreadId);
                byte[] bytes;
                using (var file = new FileStream(f.Runtime.ProvenancePath("demo"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var buffer = new MemoryStream())
                { await file.CopyToAsync(buffer); bytes = buffer.ToArray(); }
                Check("Windows DPAPI provenance contains no plaintext task ID or model",
                    !Encoding.UTF8.GetString(bytes).Contains(task.Handle.Identity.ThreadId) && !Encoding.UTF8.GetString(bytes).Contains("synthetic-model"));
                int calls = f.Rpc!.Calls.Count;


                var duplicate = await f.Adapter.ExecuteAsync(create.Context, create.Call, default);
                Check("reused dispatched operation cannot replay backend mutation", duplicate.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == calls);
                var syntheticOwner = await f.ReceiveAsync("继续 " + task.Handle.Identity.ThreadId, WriteCall("continue_owned_task", task, "rewritten"));
                Check("continuation cannot replace original user instruction", !(await f.AuthorizeAsync(syntheticOwner)).Allowed);
                var reportOnly = await f.ReceiveAsync("报告：继续 " + task.Handle.Identity.ThreadId, WriteCall("continue_owned_task", task, "报告：继续 " + task.Handle.Identity.ThreadId));
                Check("report content cannot grant task control", !(await f.AuthorizeAsync(reportOnly)).Allowed);

                string text = "继续 " + task.Handle.Identity.ThreadId + " 修复测试";
                var continuation = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text));
                string turn = continuation.Result.Facts.GetProperty("observedTurnId").GetString()!;
                Check("turn response acceptance is not running or completion", continuation.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Accepted);
                f.Rpc.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "inProgress") });
                await WaitStage(f, continuation.Operation.Id, OperationStage.Running);
                Check("running lifecycle is recorded against original immutable authorized task",
                    (await f.Ledger.ReadConversationAsync(f.Identity)).Operations.Single(o => o.Id == continuation.Operation.Id)
                        is { Stage: OperationStage.Running, Task.TurnId: null });
                task = f.Runtime.Tasks().Single();
                var stale = await f.ReceiveAsync("中断 " + task.Handle.Identity.ThreadId,
                    Call("interrupt_turn", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision, turnId = "wrong" }));
                Check("interrupt rejects mismatched expected turn before dispatch", !(await f.AuthorizeAsync(stale)).Allowed);
                string steerText = "纠偏 " + task.Handle.Identity.ThreadId + " 先跑单元测试";
                var steer = await f.ExecuteAsync(steerText, Call("steer_turn", new { configurationId = "demo",
                    threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision, expectedTurnId = turn, text = steerText }));
                Check("steer binds exact turn and does not claim effect", steer.Result.State == ToolExecutionState.Accepted && steer.Operation.Task!.TurnId == turn);

                f.Rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = turn,
                    itemId = "command-1", command = "echo synthetic", cwd = task.Snapshot.Cwd,
                    startedAtMs = f.Clock.UtcNow.ToUnixTimeMilliseconds() }, 7);
                task = f.Runtime.Tasks().Single();
                var approval = f.Runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
                var approvalCall = ApprovalCall(task, turn, approval.Token);
                var bare = await f.ReceiveAsync("同意", approvalCall);
                Check("plain agreement cannot guess most recent approval", !(await f.AuthorizeAsync(bare)).Allowed && f.Rpc.Responses == 0);
                var notice = (await f.Ledger.EnsureRequiredDecisionNotificationAsync(f.Identity,
                    OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token),
                    new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, turn, task.Handle.Identity.ProfileId),
                    new("demo", approval.Token, approval.ConnectionEpoch, approval.ExpiresAt), "synthetic approval description",
                    segments: ["synthetic scope", "synthetic operation and decision"])).Notification;
                bool beforeDelivery = false;
                try { await f.Runtime.BindPresentedApprovalAsync("demo", approval.Token, notice.Event.Id); }
                catch (InvalidOperationException) { beforeDelivery = true; }
                Check("approval reference cannot bind an unsent notification", beforeDelivery);
                await f.Ledger.BeginOutboxSendAsync(f.Identity, notice.Outbox[0].Id);
                await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, notice.Outbox[0].Id, OutboxStage.Accepted, "synthetic channel acceptance");
                bool partiallyAccepted = false;
                try { await f.Runtime.BindPresentedApprovalAsync("demo", approval.Token, notice.Event.Id); }
                catch (InvalidOperationException) { partiallyAccepted = true; }
                Check("partial notification acceptance cannot bind approval", partiallyAccepted);
                await f.Ledger.BeginOutboxSendAsync(f.Identity, notice.Outbox[1].Id);
                await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, notice.Outbox[1].Id, OutboxStage.Accepted, "synthetic second segment acceptance");
                var unrelated = await f.Ledger.EnqueueNotificationAsync(f.Identity, "unrelated-same-task",
                    "unrelated task notice", notice.Event.Task);
                await f.Ledger.BeginOutboxSendAsync(f.Identity, unrelated.Outbox[0].Id);
                await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, unrelated.Outbox[0].Id, OutboxStage.Accepted, "synthetic unrelated acceptance");
                bool wrongNotice = false;
                try { await f.Runtime.BindPresentedApprovalAsync("demo", approval.Token, unrelated.Event.Id); }
                catch (InvalidOperationException) { wrongNotice = true; }
                Check("same-task unrelated accepted notification cannot bind approval", wrongNotice);
                await f.Runtime.BindPresentedApprovalAsync("demo", approval.Token, notice.Event.Id);
                var approved = await f.ExecuteAsync("同意", approvalCall, referencedEventId: notice.Event.Id);
                Check("trusted current Inbox reference binds exact request once", approved.Result.State == ToolExecutionState.Unknown && f.Rpc.Responses == 1);
                f.Rpc.Emit("serverRequest/resolved", new { threadId = task.Handle.Identity.ThreadId, requestId = 7 });
                await WaitStage(f, approved.Operation.Id, OperationStage.Accepted);
                Check("exact server request resolution explicitly reconciles unknown approval", (await f.Ledger.ReadConversationAsync(f.Identity))
                    .Operations.Single(o => o.Id == approved.Operation.Id).Stage == OperationStage.Accepted);
                var reused = await f.ReceiveAsync("同意", ApprovalCall(f.Runtime.Tasks().Single(), turn, approval.Token), referencedEventId: notice.Event.Id);
                Check("resolved approval cannot apply to another response", !(await f.AuthorizeAsync(reused)).Allowed && f.Rpc.Responses == 1);

                f.Rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = turn,
                    itemId = "command-2", command = "echo synthetic two", cwd = task.Snapshot.Cwd,
                    startedAtMs = f.Clock.UtcNow.ToUnixTimeMilliseconds() }, "7");
                var next = f.Runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
                string full = "批准 " + OwnedControlToolAdapter.ApprovalReference("demo", next);
                var fullApproval = await f.ExecuteAsync(full, ApprovalCall(f.Runtime.Tasks().Single(), turn, next.Token));
                Check("complete typed request reference authorizes without guessing notification context",
                    fullApproval.Result.State == ToolExecutionState.Unknown && f.Rpc.Responses == 2
                    && OwnedControlToolAdapter.ApprovalReference("demo", next) != OwnedControlToolAdapter.ApprovalReference("demo", approval));
                f.Rpc.Emit("serverRequest/resolved", new { threadId = task.Handle.Identity.ThreadId, requestId = "7" });

                f.Rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = turn,
                    itemId = "command-focus", command = "echo scope", cwd = task.Snapshot.Cwd,
                    startedAtMs = f.Clock.UtcNow.ToUnixTimeMilliseconds() }, "focus-check");
                var focusApproval = f.Runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
                task = f.Runtime.Tasks().Single();
                var focusNotice = (await f.Ledger.EnsureRequiredDecisionNotificationAsync(f.Identity,
                    OwnedControlRuntime.ApprovalNotificationKey("demo", focusApproval.Token),
                    new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, turn, task.Handle.Identity.ProfileId),
                    new("demo", focusApproval.Token, focusApproval.ConnectionEpoch, focusApproval.ExpiresAt),
                    "synthetic focus approval")).Notification;
                await f.Ledger.BeginOutboxSendAsync(f.Identity, focusNotice.Outbox[0].Id);
                await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, focusNotice.Outbox[0].Id, OutboxStage.Accepted, "synthetic acceptance");
                await f.Runtime.BindPresentedApprovalAsync("demo", focusApproval.Token, focusNotice.Event.Id);
                int callsBeforeClear = f.Rpc.Calls.Count, responsesBeforeClear = f.Rpc.Responses;
                f.Runtime.ClearFocus();
                Check("clearing focus invalidates pending approval and presented reference without interrupting task",
                    f.Runtime.LoadedSession("demo")!.Client.PendingApprovals().Count == 0
                    && f.Runtime.Presented(focusNotice.Event.Id) == null && !f.Runtime.IsFocused(focusApproval)
                    && f.Runtime.Tasks().Single().Snapshot.State == task.Snapshot.State
                    && f.Rpc.Calls.Count == callsBeforeClear && f.Rpc.Responses == responsesBeforeClear);
                var clearedReply = await f.ReceiveAsync("同意", ApprovalCall(task, turn, focusApproval.Token), referencedEventId: focusNotice.Event.Id);
                Check("old notification reply cannot authorize after focus is cleared", !(await f.AuthorizeAsync(clearedReply)).Allowed);
                f.Runtime.SetFocus("demo", task.Handle.Identity.ThreadId);
                var reselectedReply = await f.ReceiveAsync("批准 " + OwnedControlToolAdapter.ApprovalReference("demo", focusApproval),
                    ApprovalCall(task, turn, focusApproval.Token));
                Check("returning to the task does not revive an invalidated approval", !(await f.AuthorizeAsync(reselectedReply)).Allowed);

                task = f.Runtime.Tasks().Single();
                var interruption = await f.ExecuteAsync("中断 " + task.Handle.Identity.ThreadId,
                    Call("interrupt_turn", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId,
                        revision = task.Snapshot.Revision, turnId = turn }));
                Check("interrupt acknowledgement is not an observed interruption", interruption.Result.State == ToolExecutionState.Accepted);
                f.Rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "interrupted") });
                await WaitStage(f, interruption.Operation.Id, OperationStage.Interrupted);
                Check("interruption lifecycle preserves precise interrupted state", (await f.Ledger.ReadConversationAsync(f.Identity))
                    .Operations.Single(o => o.Id == interruption.Operation.Id).Stage == OperationStage.Interrupted);
                task = f.Runtime.Tasks().Single();
                var rename = await f.ExecuteAsync("重命名 " + task.Handle.Identity.ThreadId + " 为 新标题",
                    Call("rename_owned_task", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId,
                        revision = task.Snapshot.Revision, name = "新标题" }));
                Check("rename uses configured owned task only", rename.Result.State == ToolExecutionState.Accepted);
                var archive = await f.ExecuteAsync("归档 " + task.Handle.Identity.ThreadId, WriteCall("archive_owned_task", f.Runtime.Tasks().Single()));
                Check("archive remains administrative acceptance", archive.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Archived);
                var unarchive = await f.ExecuteAsync("取消归档 " + task.Handle.Identity.ThreadId, WriteCall("unarchive_owned_task", f.Runtime.Tasks().Single()));
                Check("unarchive preserves exact owned identity", unarchive.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Handle.Identity == task.Handle.Identity);
                f.Rpc.Threads[task.Handle.Identity.ThreadId] = Rpc.Thread(task.Handle.Identity.ThreadId, task.Snapshot.Cwd,
                    Rpc.Turn(turn, "completed", JsonSerializer.SerializeToElement(new { type = "agentMessage", phase = "final_answer", text = "synthetic report" })));
                var report = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, turnId = turn });
                Check("report is scoped recorded evidence without independent verification", report.State == ToolExecutionState.ReadOnlySuccess
                    && !report.Facts.GetProperty("IndependentlyVerified").GetBoolean() && report.Facts.GetProperty("finalText").GetString() == "synthetic report");
                var operations = await f.ReadAsync("read_operations", new { operationId = create.Operation.Id.ToString("D") });
                Check("operation query reads durable exact operation without replay", operations.Facts.GetProperty("operations").GetArrayLength() == 1);
                Check("no event mapping failure occurred", f.Runtime.Failure == null);

                await f.Runtime.DisposeAsync();
                f.Profile = f.Profile with { Mode = OwnedProfileMode.ReadOnly,
                    Configuration = f.Profile.Configuration with { Verification = null } };
                f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [f.Profile], f.Connect, () => f.Clock.UtcNow);
                f.Adapter = new(f.Runtime);
                Check("explicit read-only configuration construction does not start a process",
                    f.ConnectCount == 1 && f.Profile.Configuration.Verification == null && !f.Adapter.GetCapability("owned.create_task").Available);
                var restoredRead = await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, refresh = true });
                Check("runtime restart restores protected ownership only read-only", restoredRead.State == ToolExecutionState.ReadOnlySuccess
                    && f.Runtime.Tasks().Single().Handle.Access == OwnedTaskAccess.RestoredReadOnly
                    && !f.Adapter.GetCapability("owned.continue_owned_task").Available);
            }
            await using (var f = await Fixture.OpenAsync(Path.Combine(directory, "restart-resume")))
            {
                await f.ExecuteAsync("创建 示例范围", Call("create_task", new { configurationId = "demo" }));
                var originalTask = f.Runtime.Tasks().Single();
                string threadId = originalTask.Handle.Identity.ThreadId;
                await f.Runtime.DisposeAsync();
                f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [f.Profile], f.Connect, () => f.Clock.UtcNow);
                f.Adapter = new(f.Runtime);
                await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId, refresh = true });
                var restoredTask = f.Runtime.Tasks().Single();
                var unarchiveCall = WriteCall("unarchive_owned_task", restoredTask);
                Check("query cannot authorize restored unarchive", !(await f.AuthorizeAsync(await f.ReceiveAsync("查询 " + threadId, unarchiveCall))).Allowed);
                var unarchived = await f.ExecuteAsync("取消归档 " + threadId, unarchiveCall);
                Check("restored unarchive leaves control read only", unarchived.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Handle.Access == OwnedTaskAccess.RestoredReadOnly && !f.Rpc!.Calls.Contains("turn/start"));
                await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId, refresh = true });
                restoredTask = f.Runtime.Tasks().Single();
                var resumeCall = WriteCall("resume_owned_task", restoredTask);
                Check("query does not authorize restored control", !(await f.AuthorizeAsync(await f.ReceiveAsync("查询 " + threadId, resumeCall))).Allowed);
                var resumed = await f.ExecuteAsync("恢复控制 " + threadId, resumeCall);
                Check("explicit resume promotes only exact restored task", resumed.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Handle.Access == OwnedTaskAccess.ResumedThisSession
                    && !f.Rpc!.Calls.Contains("turn/start"));
                string body = "继续 " + threadId + "：执行测试";
                var continued = await f.ExecuteAsync(body, Call("continue_owned_task", new { configurationId = "demo", threadId,
                    revision = f.Runtime.Tasks().Single().Snapshot.Revision, text = body }));
                Check("restored task accepts new explicit work after resume", continued.Result.State == ToolExecutionState.Accepted
                    && f.Rpc!.Calls.Count(x => x == "turn/start") == 1);
            }
            await using (var f = await Fixture.OpenAsync(Path.Combine(directory, "transport-recovery")))
            {
                await f.ExecuteAsync("创建 示例范围", CreateCall());
                var task = f.Runtime.Tasks().Single();
                string threadId = task.Handle.Identity.ThreadId;
                await f.Rpc!.DisposeAsync();
                await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId, refresh = true });
                Check("explicit read reconnects once without replaying writes", f.ConnectCount == 2
                    && f.Runtime.Tasks().Single().Handle.Access == OwnedTaskAccess.RestoredReadOnly
                    && f.Rpc!.Calls.All(c => c is "initialize" or "initialized" or "thread/read"));
                var resumed = await f.ExecuteAsync("恢复控制 " + threadId, WriteCall("resume_owned_task", f.Runtime.Tasks().Single()));
                Check("host restores original task after transport recovery", resumed.Result.State == ToolExecutionState.Accepted
                    && f.Runtime.Tasks().Single().Handle.Identity == task.Handle.Identity
                    && f.Runtime.Tasks().Single().Handle.Access == OwnedTaskAccess.ResumedThisSession);
            }
            await using (var f = await Fixture.OpenAsync(Path.Combine(directory, "restart-create")))
            {
                await f.ExecuteAsync("创建 示例范围", Call("create_task", new { configurationId = "demo" }));
                string oldId = f.Runtime.Tasks().Single().Handle.Identity.ThreadId;
                await f.Runtime.DisposeAsync();
                Directory.CreateDirectory(f.Profile.Creation.Cwd);
                var guard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => f.Clock.UtcNow);
                f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [f.Profile], f.Connect, () => f.Clock.UtcNow, guard);
                f.Adapter = new(f.Runtime);
                await f.Runtime.PrepareWorkspaceAsync();
                Check("acknowledged never-started task does not occupy recovered workspace", f.Runtime.CanCreateWorkspace("demo"));
                Check("restart query does not authorize creation", !(await f.AuthorizeAsync(await f.ReceiveAsync("查询 示例范围", CreateCall()))).Allowed);
                var created = await f.ExecuteAsync("创建 示例范围", CreateCall());
                var fresh = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId != oldId);
                Check("explicit creation after restart appends new task without adopting old control", created.Result.State == ToolExecutionState.Accepted
                    && fresh.Handle.Access == OwnedTaskAccess.CreatedThisSession
                    && f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == oldId).Handle.Access == OwnedTaskAccess.RestoredReadOnly);
                string body = "继续 " + fresh.Handle.Identity.ThreadId + "：执行测试";
                var started = await f.ExecuteAsync(body, Call("continue_owned_task", new { configurationId = "demo",
                    threadId = fresh.Handle.Identity.ThreadId, revision = fresh.Snapshot.Revision, text = body }));
                Check("new task after cold restart can execute", started.Result.State == ToolExecutionState.Accepted);
                string turnId = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == fresh.Handle.Identity.ThreadId).Snapshot.CurrentTurnId!;
                f.Rpc!.Emit("turn/completed", new { threadId = fresh.Handle.Identity.ThreadId, turn = Rpc.Turn(turnId, "completed") });
                await WaitStage(f, started.Operation.Id, OperationStage.Completed);
                var sameInput = (await f.Ledger.ReadConversationAsync(f.Identity)).Inbox.Single(m => m.SourceMessageId == started.Context.SourceMessageId);
                var repeated = Call("continue_owned_task", new { configurationId = "demo", threadId = fresh.Handle.Identity.ThreadId,
                    revision = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == fresh.Handle.Identity.ThreadId).Snapshot.Revision, text = body });
                OwnedControlToolAdapter.Catalog().TryGet(repeated.Name, out var definition);
                Check("changed task revision cannot redispatch same user message", !(await f.Adapter.AuthorizeAsync(new(sameInput, repeated, definition), default)).Allowed
                    && f.Rpc.Calls.Count(c => c == "turn/start") == 1);
            }
            await PersistedFocusAsync(directory, Check);
            await ReadOnlyWorkAsync(directory, Check);
            await WorkingDirectoryIsolationAsync(directory, Check);
            await SharedWorkspaceAsync(directory, Check);
            await GlobalAdmissionAsync(directory, Check);
            await TerminalCapacityRecoveryAsync(directory, Check);
            await WorkspaceRecoveryAsync(directory, Check);
            await UnknownAndIsolationAsync(directory, Check);
            await QuotedContinuationAsync(directory, Check);
            await CatalogueAndPagingAsync(directory, Check);
            await ReportPagingAsync(directory, Check);
        }
        catch (Exception error)
        {
            checks["failure"] = false;
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "owned-adapter-checks.json"), JsonSerializer.Serialize(new
            {
                passed = checks.Values.All(x => x), count = checks.Count, checks,
                realCodexProcessesStarted = 0, realTaskOperations = 0, realMessagesSent = 0, productionCredentialsRead = false,
                protection = "Windows DPAPI current-user synthetic stores only"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{checks.Count(x => x.Value)}/{checks.Count} owned adapter checks passed");
        }
    }

    private static async Task QuotedContinuationAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "quoted-owned"));
        await f.ExecuteAsync("新建 demo", CreateCall());
        var first = f.Runtime.Tasks().Single();
        string instruction = "继续 " + first.Handle.Identity.ThreadId + " 测试";
        var started = await f.ExecuteAsync(instruction, WriteCall("continue_owned_task", first, instruction));
        string oldTurn = f.Runtime.Tasks().Single().Snapshot.CurrentTurnId!;
        f.Rpc!.Emit("turn/completed", new { threadId = first.Handle.Identity.ThreadId, turn = Rpc.Turn(oldTurn, "completed") });
        await WaitStage(f, started.Operation.Id, OperationStage.Completed);
        await f.ExecuteAsync("新建 demo", CreateCall());
        var second = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId != first.Handle.Identity.ThreadId);
        var selected = await f.ReceiveAsync("选择其他任务", Call("list_owned_tasks", new { }));
        var focus = new TaskReference(second.Handle.Identity.HostId, second.Handle.Identity.ThreadId, ProfileId: second.Handle.Identity.ProfileId);
        await f.Ledger.SetWorkFocusAsync(f.Identity, selected.Message.SourceMessageId, focus, selected.Message.Body);
        var reference = new TaskReference(first.Handle.Identity.HostId, first.Handle.Identity.ThreadId, oldTurn, first.Handle.Identity.ProfileId);
        var notice = await f.Ledger.EnqueueNotificationAsync(f.Identity, "quoted-owned-result", "任务报告正文只作资料", reference);
        await f.Ledger.BeginOutboxSendAsync(f.Identity, notice.Outbox[0].Id);
        await f.Ledger.RecordOutboxOutcomeAsync(f.Identity, notice.Outbox[0].Id, OutboxStage.Accepted, "synthetic receipt");
        var quoted = await f.Ledger.ResolveSentReferenceAsync(f.Identity, notice.Outbox[0].Id);
        var wrong = await f.ReceiveAsync("这个继续", WriteCall("continue_owned_task", second, "这个继续"), referencedEventId: quoted.Id);
        check("owned quote cannot authorize the other focused task", !(await f.AuthorizeAsync(wrong)).Allowed);
        var target = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == first.Handle.Identity.ThreadId);
        var continued = await f.ExecuteAsync("这个继续", WriteCall("continue_owned_task", target, "这个继续"), quoted.Id);
        check("owned quote continues exact original task with current user text", continued.Result.State == ToolExecutionState.Accepted
            && continued.Operation.Task?.ThreadId == first.Handle.Identity.ThreadId);
        check("owned quote preserves the user's separate focus", (await f.Ledger.ReadConversationContextAsync(f.Identity)).Focus?.Task == focus);
        target = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == first.Handle.Identity.ThreadId);
        f.Rpc.Emit("turn/completed", new { threadId = first.Handle.Identity.ThreadId, turn = Rpc.Turn(target.Snapshot.CurrentTurnId!, "completed") });
        await WaitStage(f, continued.Operation.Id, OperationStage.Completed);
        target = f.Runtime.Tasks().Single(t => t.Handle.Identity.ThreadId == first.Handle.Identity.ThreadId);
        var stale = await f.ReceiveAsync("这个继续", WriteCall("continue_owned_task", target, "这个继续"), referencedEventId: quoted.Id);
        check("owned quote from an older turn cannot authorize after a newer turn", !(await f.AuthorizeAsync(stale)).Allowed);
    }

    private static async Task CatalogueAndPagingAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "catalogue"));
        await f.ExecuteAsync("新建 demo", CreateCall());
        await f.ExecuteAsync("新建 demo", CreateCall());
        var operationPage = await f.ReadAsync("read_operations", new { pageSize = 1 });
        string operationToken = operationPage.Facts.GetProperty("page").GetProperty("snapshotId").GetString()!;
        string firstOperation = operationPage.Facts.GetProperty("operations")[0].GetProperty("operationId").GetString()!;
        var originalTasks = f.Runtime.Tasks();
        var listing = await f.ReadAsync("list_owned_tasks", new { pageSize = 1 });
        string token = listing.Facts.GetProperty("page").GetProperty("snapshotId").GetString()!;
        string firstId = listing.Facts.GetProperty("tasks")[0].GetProperty("identity").GetProperty("ThreadId").GetString()!;
        check("local list merges creation catalogue and live handle without duplication", listing.Facts.GetProperty("totalTasks").GetInt32() == 2
            && listing.Facts.GetProperty("tasks").GetArrayLength() == 1 && listing.Facts.GetProperty("page").GetProperty("totalPages").GetInt32() == 2);
        await f.ExecuteAsync("新建 demo", CreateCall());
        int calls = f.Rpc!.Calls.Count;
        var operationNext = await f.ReadAsync("read_operations", new { page = 2, snapshotId = operationToken });
        check("operation pages retain snapshot across new operations without replay",
            operationNext.Facts.GetProperty("totalOperations").GetInt32() == 2
            && operationNext.Facts.GetProperty("operations")[0].GetProperty("operationId").GetString() != firstOperation
            && operationNext.Facts.GetProperty("page").GetProperty("nextPage").ValueKind == JsonValueKind.Null
            && f.Rpc.Calls.Count == calls);
        var wrongOperationScope = await f.ReadAsync("read_operations", new { operationId = firstOperation, snapshotId = operationToken });
        check("operation snapshot cannot be reused for a different filter", wrongOperationScope.State == ToolExecutionState.Rejected);
        var next = await f.ReadAsync("list_owned_tasks", new { page = 2, snapshotId = token });
        check("list pages retain one immutable snapshot while tasks are added", next.Facts.GetProperty("totalTasks").GetInt32() == 2
            && next.Facts.GetProperty("tasks")[0].GetProperty("identity").GetProperty("ThreadId").GetString() != firstId
            && next.Facts.GetProperty("page").GetProperty("nextPage").ValueKind == JsonValueKind.Null && f.Rpc.Calls.Count == calls);
        var repeat = await f.ReadAsync("list_owned_tasks", new { page = 1, snapshotId = token });
        check("repeated list page is deterministic including snapshot metadata", repeat.Facts.GetRawText() == listing.Facts.GetRawText());
        foreach (var args in new object[] { new { page = 2 }, new { page = 3, snapshotId = token },
            new { page = 2, snapshotId = token, pageSize = 2 }, new { page = 1, snapshotId = new string('0', 32) } })
        {
            var rejected = await f.ReadAsync("list_owned_tasks", args);
            check("invalid list page fails closed " + JsonSerializer.Serialize(args),
                rejected.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == calls);
        }
        var fresh = await f.ReadAsync("list_owned_tasks", new { });
        check("fresh list snapshot sees newly created task with bounded page metadata", fresh.Facts.GetProperty("totalTasks").GetInt32() == 3
            && fresh.Facts.GetProperty("page").GetProperty("consistentSnapshot").GetBoolean()
            && !fresh.Facts.GetRawText().Contains("ExpectedCodexHome", StringComparison.Ordinal));
        await f.Runtime.DisposeAsync();
        var profile = f.Profile with { Mode = OwnedProfileMode.Disabled, Configuration = f.Profile.Configuration with { Verification = null } };
        f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [profile], f.Connect, () => f.Clock.UtcNow);
        f.Adapter = new(f.Runtime);
        check("local catalogue capability discovery needs no initialized service",
            f.Adapter.GetCapability("owned.read_owned_task").Available && f.ConnectCount == 1
            && !f.Adapter.GetCapability("owned.create_task").Available);
        var restarted = await f.ReadAsync("list_owned_tasks", new { });
        check("disabled restart discovers creation facts without constructing RPC or handles",
            restarted.Facts.GetProperty("tasks").GetArrayLength() == 3 && f.Runtime.Tasks().Count == 0 && f.ConnectCount == 1
            && restarted.Facts.GetProperty("tasks").EnumerateArray().All(t => t.GetProperty("state").GetString() == "Unknown"
                && t.GetProperty("ObservedAt").ValueKind == JsonValueKind.Null && t.GetProperty("CurrentTurnId").ValueKind == JsonValueKind.Null
                && t.GetProperty("Revision").ValueKind == JsonValueKind.Null && t.GetProperty("access").GetString() == "CreationEvidenceOnly"));
        var historical = await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId = firstId });
        check("exact historical task can be read locally without implying observation or control",
            historical.State == ToolExecutionState.ReadOnlySuccess && !historical.Facts.GetProperty("refreshed").GetBoolean()
            && historical.Facts.GetProperty("task").GetProperty("EvidenceSource").GetString() == "protected_owned_provenance_creation_only"
            && f.ConnectCount == 1 && f.Runtime.FindTask("demo", firstId) == null);
        var refresh = await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId = firstId, refresh = true });
        check("catalogue discovery does not enable disabled refresh", refresh.State == ToolExecutionState.Rejected && f.ConnectCount == 1);
        var write = await f.ReceiveAsync("重命名 " + firstId + " 为 不能执行",
            Call("rename_owned_task", new { configurationId = "demo", threadId = firstId, revision = 1, name = "不能执行" }));
        check("creation-only fact cannot authorize mutation", !(await f.AuthorizeAsync(write)).Allowed && f.ConnectCount == 1);
        var oldSnapshot = await f.ReadAsync("list_owned_tasks", new { snapshotId = token });
        check("query snapshot token does not survive runtime restart", oldSnapshot.State == ToolExecutionState.Rejected);

        await f.Runtime.DisposeAsync();
        var changedConfig = profile.Configuration with { Projects = profile.Configuration.Projects.SetItem("sample", profile.Creation.Cwd + "-changed") };
        var changed = profile with { Configuration = changedConfig, Creation = profile.Creation with { Cwd = profile.Creation.Cwd + "-changed" } };
        f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [changed], f.Connect, () => f.Clock.UtcNow); f.Adapter = new(f.Runtime);
        var wrongScope = await f.ReadAsync("list_owned_tasks", new { });
        check("configuration change blocks local catalogue all-or-nothing without connection", wrongScope.Facts.GetProperty("totalTasks").GetInt32() == 0
            && !wrongScope.Facts.GetProperty("completeCatalogue").GetBoolean()
            && wrongScope.Facts.GetProperty("unavailableConfigurations")[0].GetString() == "demo" && f.ConnectCount == 1);

        await f.Runtime.DisposeAsync();
        string path = f.Runtime.ProvenancePath("demo");
        byte[] encrypted = await File.ReadAllBytesAsync(path); encrypted[^1] ^= 1; await File.WriteAllBytesAsync(path, encrypted);
        f.Runtime = new(f.Identity, () => f.Active, f.Ledger, f.DataPath, [profile with { Mode = OwnedProfileMode.ReadOnly }], f.Connect, () => f.Clock.UtcNow);
        f.Adapter = new(f.Runtime);
        var damaged = await f.ReadAsync("list_owned_tasks", new { });
        var damagedRead = await f.ReadAsync("read_owned_task", new { configurationId = "demo", threadId = firstId, refresh = true });
        check("damaged catalogue exposes no partial facts and cannot trigger refresh RPC", damaged.Facts.GetProperty("totalTasks").GetInt32() == 0
            && damagedRead.State == ToolExecutionState.Rejected && f.ConnectCount == 1
            && (await File.ReadAllBytesAsync(path)).SequenceEqual(encrypted));
        bool rejectedConnection = false;
        try { await f.Runtime.EnsureSessionAsync("demo", "thread/read", true, default); }
        catch (ProvenanceException) { rejectedConnection = true; }
        check("direct runtime read initialization authenticates existing proofs before connecting", rejectedConnection && f.ConnectCount == 1);
    }

    private static async Task ReportPagingAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "report-pages"));
        await f.ExecuteAsync("新建 demo", CreateCall());
        var task = f.Runtime.Tasks().Single();
        string thread = task.Handle.Identity.ThreadId, turn = "historical-turn";
        string text = new('甲', 255); text += "😀" + new string('乙', 1250);
        JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        f.Rpc!.Threads[thread] = Rpc.Thread(thread, task.Snapshot.Cwd, Rpc.Turn(turn, "completed",
            J(new { type = "reasoning", text = "secret-analysis" }),
            J(new { type = "agentMessage", phase = "analysis", text = "hidden-agent-analysis" }),
            J(new { type = "agentMessage", phase = "final_answer", text }),
            J(new { id = "cmd", type = "commandExecution", status = "completed", exitCode = 0,
                command = "secret-command", aggregatedOutput = "secret-output", environment = "secret-environment" }),
            J(new { id = "files", type = "fileChange", status = "completed", changes = new[] {
                new { path = "result.cs", kind = new { type = "update" }, diff = "secret-diff" } } })));
        var first = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn, pageSize = 256 });
        string token = first.Facts.GetProperty("page").GetProperty("snapshotId").GetString()!;
        int count = first.Facts.GetProperty("page").GetProperty("totalPages").GetInt32(), reads = f.Rpc.Calls.Count;
        check("report page carries exact turn and immutable snapshot metadata", first.Facts.GetProperty("TurnId").GetString() == turn
            && first.Facts.GetProperty("page").GetProperty("consistentSnapshot").GetBoolean() && first.Facts.GetProperty("truncated").GetBoolean()
            && !first.Facts.GetProperty("sourceTruncated").GetBoolean() && count > 1);
        check("report projection excludes analysis raw commands environment output and diffs",
            !first.Facts.GetRawText().Contains("secret-", StringComparison.Ordinal) && !first.Facts.GetRawText().Contains("hidden-agent-analysis", StringComparison.Ordinal)
            && first.Facts.GetProperty("commandItems")[0].GetProperty("exitCode").GetInt32() == 0
            && first.Facts.GetProperty("fileChanges")[0].GetProperty("path").GetString() == "result.cs");
        f.Rpc.Threads[thread] = Rpc.Thread(thread, task.Snapshot.Cwd, Rpc.Turn("newer-turn", "completed",
            J(new { type = "agentMessage", phase = "final_answer", text = "changed report must not enter old pages" })));
        var joined = new StringBuilder(first.Facts.GetProperty("finalText").GetString());
        for (int page = 2; page <= count; page++)
        {
            var part = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn, page, snapshotId = token });
            joined.Append(part.Facts.GetProperty("finalText").GetString());
            if (page == count) check("last report page explicitly ends continuation", part.Facts.GetProperty("page").GetProperty("nextPage").ValueKind == JsonValueKind.Null);
        }
        check("report pages reconstruct exact unicode final answer without rereading changed server", joined.ToString() == text && f.Rpc.Calls.Count == reads);
        var repeat = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn, snapshotId = token });
        check("same report page and evidence metadata are repeatable", first.Facts.GetRawText() == repeat.Facts.GetRawText());
        foreach (var args in new object[] {
            new { configurationId = "demo", threadId = thread, turnId = turn, page = 2 },
            new { configurationId = "demo", threadId = thread, turnId = "other", page = 2, snapshotId = token },
            new { configurationId = "demo", threadId = "other", turnId = turn, page = 2, snapshotId = token },
            new { configurationId = "alternate", threadId = thread, turnId = turn, page = 2, snapshotId = token },
            new { configurationId = "demo", threadId = thread, turnId = turn, page = 2, pageSize = 300, snapshotId = token },
            new { configurationId = "demo", threadId = thread, turnId = turn, page = 1024, snapshotId = token } })
        {
            var bad = await f.ReadAsync("read_owned_report", args);
            check("report pagination binds exact query scope " + JsonSerializer.Serialize(args), bad.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == reads);
        }
        var wrongQuery = await f.ReadAsync("list_owned_tasks", new { snapshotId = token });
        check("report snapshot cannot be used as list snapshot", wrongQuery.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == reads);
        f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(11);
        var expired = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn, page = 2, snapshotId = token });
        check("expired snapshot rejects rather than mixing new service data", expired.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == reads);
        f.Rpc.Threads[thread] = Rpc.Thread(thread, task.Snapshot.Cwd, Rpc.Turn(turn, "completed",
            J(new { type = "agentMessage", phase = "final_answer", text = new string('x', 128 * 1024 + 1) })));
        var limited = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn });
        check("bounded report snapshot declares source truncation and retained length", limited.Facts.GetProperty("sourceTruncated").GetBoolean()
            && limited.Facts.GetProperty("retainedFinalTextCharacters").GetInt32() == 128 * 1024
            && limited.Facts.GetProperty("totalFinalTextCharacters").GetInt32() == 128 * 1024 + 1);
        string limitedToken = limited.Facts.GetProperty("page").GetProperty("snapshotId").GetString()!;
        for (int i = 0; i < 8; i++) await f.ReadAsync("list_owned_tasks", new { });
        var evicted = await f.ReadAsync("read_owned_report", new { configurationId = "demo", threadId = thread, turnId = turn, snapshotId = limitedToken });
        check("bounded snapshot cache eviction fails closed", evicted.State == ToolExecutionState.Rejected);
    }

    private static async Task PersistedFocusAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "persisted-focus"));
        await f.ExecuteAsync("新建 demo", CreateCall());
        var task = f.Runtime.Tasks().Single();
        var reference = new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, ProfileId: task.Handle.Identity.ProfileId);
        check("accepted creation persists current task for short commands",
            (await f.Ledger.ReadConversationContextAsync(f.Identity)).Focus?.Task == reference);
        ModelToolCall Stop(string? turn = null) => Call("interrupt_turn", new { configurationId = "demo",
            threadId = task.Handle.Identity.ThreadId, revision = f.Runtime.Tasks().Single().Snapshot.Revision,
            turnId = turn ?? f.Runtime.Tasks().Single().Snapshot.CurrentTurnId });
        async Task<bool> Allowed(string body, ModelToolCall call) => (await f.AuthorizeAsync(await f.ReceiveAsync(body, call))).Allowed;
        async Task Focus(TaskReference selected)
        {
            var message = await f.ReceiveAsync("选定 " + selected.ThreadId, Call("list_owned_tasks", new { }));
            await f.Ledger.SetWorkFocusAsync(f.Identity, message.Message.SourceMessageId, selected, message.Message.Body);
        }
        var initialReset = await f.ReceiveAsync("开始新对话", Call("list_owned_tasks", new { }));
        await f.Ledger.BeginNewDialogueAsync(f.Identity, initialReset.Message.SourceMessageId);
        check("short continuation needs independently persisted explicit focus",
            !await Allowed("继续测试", WriteCall("continue_owned_task", task, "继续测试")));
        await Focus(reference);
        var focused = await f.ReceiveAsync("继续测试", WriteCall("continue_owned_task", task, "继续测试"));
        var focusedDecision = await f.AuthorizeAsync(focused);
        check("exact short continuation resolves persisted canonical focus without modifying Inbox.Task",
            focusedDecision.Allowed && focused.Message.Task == null && focusedDecision.Task == reference
            && (await f.Ledger.ReadConversationAsync(f.Identity)).Inbox.Last().Task == null);
        check("focus continuation still requires full original instruction",
            !await Allowed("继续测试", WriteCall("continue_owned_task", task, "继续测试并修改其他文件")));
        check("explicit current-task continuation is authorized by exact focus",
            await Allowed("继续当前任务", WriteCall("continue_owned_task", task, "继续当前任务")));
        const string focusedInstruction = "继续当前任务：只回复短指令正常，不要使用工具或访问文件。";
        check("focused continuation accepts original explicit instruction without uuid", await Allowed(focusedInstruction,
            WriteCall("continue_owned_task", task, focusedInstruction)));
        check("focused continuation cannot change instruction", !await Allowed(focusedInstruction,
            WriteCall("continue_owned_task", task, "执行其他内容")));
        foreach (string body in new[] { "继续另一个任务", "继续其他任务", "不要继续测试", "如果通过就继续测试", "继续测试其他项目" })
            check("focus continuation rejects " + body, !await Allowed(body, WriteCall("continue_owned_task", task, body)));
        var continued = await f.ExecuteAsync("继续测试", WriteCall("continue_owned_task", task, "继续测试"));
        string turn = continued.Result.Facts.GetProperty("observedTurnId").GetString()!;
        f.Rpc!.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "inProgress") });
        await WaitStage(f, continued.Operation.Id, OperationStage.Running);
        foreach (string body in new[] { "先停一下", "停止运行", "中断当前任务", "停止当前任务" })
            check("exact focus stop accepts " + body, await Allowed(body, Stop()));
        check("explicit current task cannot bypass exact active turn", !await Allowed("中断当前任务", Stop("old-turn")));
        foreach (string body in new[] { "停止另一个任务", "停止其他任务", "不要停止当前任务", "如果通过就停止当前任务", "先停一下别的任务" })
            check("focus stop rejects " + body, !await Allowed(body, Stop()));
        await f.Ledger.EnqueueNotificationAsync(f.Identity, "focus-notice-unknown", "unknown target", null);
        check("unknown notification target blocks a generic stop", !await Allowed("先停一下", Stop()));
        check("explicit current task disambiguates unknown notification", await Allowed("中断当前任务", Stop()));
        var other = reference with { ThreadId = "synthetic-other-thread" };
        await f.Ledger.EnqueueNotificationAsync(f.Identity, "focus-notice-other", "other target", other);
        check("another notification target blocks generic stop", !await Allowed("停止运行", Stop()));
        check("explicit current task retains focus despite another notification", await Allowed("停止当前任务", Stop()));
        await f.Ledger.EnqueueNotificationAsync(f.Identity, "focus-notice-same", "same target", reference with { TurnId = turn });
        check("same canonical notification does not displace generic focus", await Allowed("先停一下", Stop()));
        await Focus(reference with { ProfileId = "different-profile" });
        check("focus must match exact endpoint profile", !await Allowed("中断当前任务", Stop()));
        await Focus(reference);
        var selectionMessage = await f.ReceiveAsync("选择任务", Call("list_owned_tasks", new { }));
        await f.Ledger.OpenTaskSelectionAsync(f.Identity, selectionMessage.Message.SourceMessageId, TaskSelectionPurpose.Focus,
            [new(reference, "one"), new(other, "two")], TimeSpan.FromMinutes(2));
        check("pending selection blocks generic focus stop", !await Allowed("先停一下", Stop()));
        check("pending selection also blocks current-task shortcut", !await Allowed("停止当前任务", Stop()));
        await Focus(reference);
        f.Rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = turn,
            itemId = "focus-approval", command = "echo focus", cwd = task.Snapshot.Cwd,
            startedAtMs = f.Clock.UtcNow.ToUnixTimeMilliseconds() }, 100);
        var pendingApproval = f.Runtime.LoadedSession("demo")!.Client.PendingApprovals().Single();
        var notice = await f.Ledger.EnqueueNotificationAsync(f.Identity, "focus-pending-approval", "approval",
            reference with { TurnId = turn });
        var context = await f.Ledger.ReadConversationContextAsync(f.Identity);
        await f.Ledger.RecordPendingApprovalAsync(f.Identity, notice.Event.Id, "focus-request", notice.Event.Task!,
            pendingApproval.ConnectionEpoch, pendingApproval.ExpiresAt, context.Epoch);
        check("pending approval blocks generic focus stop", !await Allowed("先停一下", Stop()));
        check("explicit current-task interruption remains available while that task waits for approval",
            f.Runtime.Tasks().Single().Snapshot.State == OwnedTaskState.WaitingApproval && await Allowed("中断当前任务", Stop()));
        var otherNotice = await f.Ledger.EnqueueNotificationAsync(f.Identity, "focus-other-approval", "other approval",
            other with { TurnId = "other-turn" });
        await f.Ledger.RecordPendingApprovalAsync(f.Identity, otherNotice.Event.Id, "other-request", otherNotice.Event.Task!,
            Guid.NewGuid(), f.Clock.UtcNow.AddMinutes(2), context.Epoch);
        check("approval belonging to another canonical task blocks current-task shortcut", !await Allowed("停止当前任务", Stop()));
        var reset = await f.ReceiveAsync("新对话", Call("list_owned_tasks", new { }));
        await f.Ledger.BeginNewDialogueAsync(f.Identity, reset.Message.SourceMessageId);
        check("new dialogue removes inherited focus authority", !await Allowed("中断当前任务", Stop()));
    }

    private static async Task ReadOnlyWorkAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "readonly-work"), verified: false);
        var old = await f.ReceiveAsync("查询原有任务", Call("list_owned_tasks", new { }));
        await f.ReceiveAsync("补充：只查询", Call("list_owned_tasks", new { }));
        var work = (await f.Ledger.QueueUnprocessedInputsAsync(f.Identity)).Single();
        var lease = await f.Ledger.BeginReadOnlyWorkAsync(f.Identity, work.Id);
        var invocation = await f.Ledger.RegisterToolInvocationAsync(lease, old.Message.SourceMessageId,
            old.Call.Id, old.Call.Name, old.Call.ArgumentsJson, false);
        await f.Ledger.BeginToolInvocationAsync(lease, invocation.Id);
        var context = new ToolExecutionContext(f.Identity, old.Message.SourceMessageId, old.Message.Revision,
            invocation.Id, null, null, work.Id, (await f.Ledger.ReadConversationAsync(f.Identity)).Revision);
        var read = await f.Adapter.ExecuteAsync(context, old.Call, default);
        check("older input may perform the exact registered read under a current read-only work lease",
            read.State == ToolExecutionState.ReadOnlySuccess && f.ConnectCount == 0);
        var changedCall = Call("read_operations", new { }) with { Id = old.Call.Id };
        check("read-only lease cannot substitute a different tool invocation",
            (await f.Adapter.ExecuteAsync(context, changedCall, default)).State == ToolExecutionState.Rejected);
        check("read-only lease never grants mutation authority",
            (await f.Adapter.ExecuteAsync(context, CreateCall(), default)).State == ToolExecutionState.Rejected && f.ConnectCount == 0);
        check("older input without its durable work lease is rejected",
            (await f.Adapter.ExecuteAsync(context with { InputWorkId = null }, old.Call, default)).State == ToolExecutionState.Rejected);
        check("invented work identity cannot authorize a read",
            (await f.Adapter.ExecuteAsync(context with { InputWorkId = Guid.NewGuid() }, old.Call, default)).State == ToolExecutionState.Rejected);
        await f.ReceiveAsync("停止这次查询", Call("list_owned_tasks", new { }));
        check("new constraint invalidates an earlier read-only work lease",
            (await f.Adapter.ExecuteAsync(context, old.Call, default)).State == ToolExecutionState.Rejected);
    }

    private static async Task WorkingDirectoryIsolationAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "directory-isolation"), twoProfiles: true);
        await f.ExecuteAsync("新建 demo", CreateCall());
        var first = f.Runtime.Tasks().Single();
        await f.ExecuteAsync("新建 alternate", CreateCall("alternate"));
        var second = f.Runtime.Tasks().Single(t => t.ConfigurationId == "alternate");
        using (f.Runtime.ReserveDirectory(first))
        {
            check("start reservation excludes the same directory across endpoint profiles",
                !f.Runtime.WorkingDirectoryAvailable(second));
        }
        check("released start reservation permits an idle task", f.Runtime.WorkingDirectoryAvailable(second));
        string text = "继续 " + first.Handle.Identity.ThreadId + " 修复测试";
        var start = await f.ExecuteAsync(text, WriteCall("continue_owned_task", first, text));
        string nextText = "继续 " + second.Handle.Identity.ThreadId + " 处理另一任务";
        var blocked = await f.ReceiveAsync(nextText, WriteCall("continue_owned_task", second, nextText));
        check("accepted active turn blocks a second profile in the same working directory",
            start.Result.State == ToolExecutionState.Accepted && !(await f.AuthorizeAsync(blocked)).Allowed);
    }

    private static async Task UnknownAndIsolationAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "unknown"));
        var create = await f.ExecuteAsync("新建 demo", CreateCall());
        var task = f.Runtime.Tasks().Single();
        string text = "继续 " + task.Handle.Identity.ThreadId + " 测试";
        f.Rpc!.DropTurn = true;
        var start = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text));
        check("lost control receipt is Unknown with automatic retry disabled", start.Result.State == ToolExecutionState.Unknown
            && !start.Result.Facts.GetProperty("automaticRetry").GetBoolean());
        int calls = f.Rpc.Calls.Count;
        var repeated = await f.Adapter.ExecuteAsync(start.Context, start.Call, default);
        check("unknown durable operation cannot be redispatched", repeated.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == calls);
        var reference = new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, ProfileId: task.Handle.Identity.ProfileId);
        var old = await f.ReceiveAsync("继续 " + task.Handle.Identity.ThreadId, WriteCall("continue_owned_task", task, "继续 " + task.Handle.Identity.ThreadId), reference);
        f.Active = new("other-user", "other-binding", "main");
        check("binding change closes authorization and capabilities", !(await f.AuthorizeAsync(old)).Allowed
            && !f.Adapter.GetCapability("owned.create_task").Available);
        await f.Runtime.DisposeAsync();
        var other = f.Active;
        var freshRuntime = new OwnedControlRuntime(other!, () => other, f.Ledger, f.DataPath, [f.Profile], f.Connect, () => f.Clock.UtcNow);
        await using (freshRuntime)
        {
            File.Copy(f.Runtime.ProvenancePath("demo"), freshRuntime.ProvenancePath("demo"));
            bool rejected = false;
            try { using var scope = freshRuntime.Enter(); await freshRuntime.EnsureSessionAsync("demo", "thread/read", true, default); }
            catch (ProvenanceException) { rejected = true; }
            check("DPAPI purpose prevents copied proof from changing conversation owner", rejected && freshRuntime.Tasks().Count == 0);
        }
    }
    private static async Task SharedWorkspaceAsync(string directory, Action<string, bool> check)
    {
        var now = new TestClock().UtcNow;
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "shared-workspace"), workspace: workspace);
        var created = await f.ExecuteAsync("新建 demo", CreateCall());
        var task = f.Runtime.Tasks().Single();
        var location = new WorkspaceLocation("local", "windows", task.Snapshot.Cwd);
        check("known empty created thread is live idle and does not retain a resource claim",
            created.Result.State == ToolExecutionState.Accepted && task.Snapshot.State == OwnedTaskState.Idle
            && workspace.Blockers().Count == 0 && f.Runtime.WorkingDirectoryAvailable(task));
        var legacy = new CanonicalTaskIdentity("local", "legacy-workspace-task");
        var held = workspace.TryReserve("legacy/synthetic", legacy, location, "synthetic-legacy-owner");
        held.Reservation!.Accepted(legacy, "legacy-turn");
        string text = "继续 " + task.Handle.Identity.ThreadId + " 修复测试";
        int before = f.Rpc!.Calls.Count;
        check("a shared legacy accepted turn blocks owned start without any RPC",
            !(await f.AuthorizeAsync(await f.ReceiveAsync(text, WriteCall("continue_owned_task", task, text)))).Allowed
            && f.Rpc.Calls.Count == before);
        workspace.Observe(new(legacy, location, "legacy-turn", WorkspaceTaskState.Completed, now, true, WorkspaceEvidenceKind.Collector,
            OwnerScope: "synthetic-legacy-owner"));
        var started = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text));
        task = f.Runtime.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        check("owned turn acceptance holds the shared directory against the legacy path",
            started.Result.State == ToolExecutionState.Accepted && workspace.Blockers().Count == 1
            && !workspace.TryReserve("legacy/second", legacy, location, "synthetic-legacy-owner").Allowed);
        f.Rpc.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "inProgress") });
        await WaitStage(f, started.Operation.Id, OperationStage.Running);
        task = f.Runtime.Tasks().Single();
        check("same owned live turn can be amended while retaining its start claim", f.Runtime.CanAmendWorkspace(task, turn));
        workspace.RestoreUnresolved(new("legacy/unknown", legacy, location, "unconfirmed-turn", now, OwnerScope: "synthetic-legacy-owner"));
        task = f.Runtime.Tasks().Single();
        check("an unresolved legacy dispatch blocks steer on the same directory", !f.Runtime.CanAmendWorkspace(task, turn));
        var interrupt = await f.ReceiveAsync("中断 " + task.Handle.Identity.ThreadId,
            Call("interrupt_turn", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId,
                revision = task.Snapshot.Revision, turnId = turn }));
        check("workspace uncertainty does not prevent explicitly authorized exact-turn interruption",
            (await f.AuthorizeAsync(interrupt)).Allowed);
        f.Rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "completed") });
        await WaitStage(f, started.Operation.Id, OperationStage.Completed);
        f.Runtime.RefreshWorkspace();
        check("owned terminal releases its own claim while unrelated unknown remains",
            workspace.Blockers().Count == 1 && workspace.Blockers().Single().ClaimId == "legacy/unknown"
            && !f.Runtime.WorkingDirectoryAvailable(f.Runtime.Tasks().Single()));
        await f.Runtime.DisposeAsync();
        check("runtime stop never releases an unrelated unresolved claim", workspace.Blockers().Count == 1);
    }
    private static async Task GlobalAdmissionAsync(string directory, Action<string, bool> check)
    {
        var now = new TestClock().UtcNow;
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now, maxConcurrentExecutions: 1);
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "global-admission"), workspace: workspace);
        string otherPath = Path.Combine(directory, "other-capacity-project");
        Directory.CreateDirectory(otherPath);
        var otherTask = new CanonicalTaskIdentity("local", "other-capacity-task", "other-profile");
        var otherLocation = new WorkspaceLocation("local", "windows", otherPath);
        WorkspaceReservation? other = null;
        var raced = await f.ExecuteAsync("新建 demo", CreateCall(), beforeExecute: () =>
        {
            other = workspace.TryReserve("legacy/global-race", otherTask, otherLocation, "synthetic-other-owner").Reservation!;
            other.Accepted(otherTask, "other-turn");
        });
        var deniedOperation = (await f.Ledger.ReadConversationAsync(f.Identity)).Operations.Single(o => o.Id == raced.Operation.Id);
        check("capacity lost after durable dispatch rejects with zero RPC and known no-dispatch evidence",
            raced.Result.State == ToolExecutionState.Rejected && f.ConnectCount == 0 && deniedOperation.Stage == OperationStage.Failed
            && raced.Result.Facts.GetProperty("dispatched").ValueKind == JsonValueKind.False
            && raced.Result.Reason!.Contains("额度已满") && workspace.Snapshot().Occupied == 1);
        var denied = await f.AuthorizeAsync(await f.ReceiveAsync("新建 demo", CreateCall()));
        check("full global capacity is explained before any new authorization or connection",
            !denied.Allowed && denied.Reason!.Contains("稍后再次明确要求") && f.ConnectCount == 0
            && (await f.Ledger.ReadConversationAsync(f.Identity)).Operations.Length == 1);
        var read = await f.ReadAsync("list_owned_tasks", new { });
        check("full global capacity does not block local read-only tools", read.State == ToolExecutionState.ReadOnlySuccess && f.ConnectCount == 0);
        workspace.Observe(new(otherTask, otherLocation, "other-turn", WorkspaceTaskState.Completed, now, true, WorkspaceEvidenceKind.LiveBackend,
            OwnerScope: "synthetic-other-owner"));
        var created = await f.ExecuteAsync("新建 demo", CreateCall());
        check("owned create reserves before constructing a connection then releases known empty thread",
            created.Result.State == ToolExecutionState.Accepted && f.ReservedSeenAtConnect && workspace.Snapshot().Occupied == 0);
        var task = f.Runtime.Tasks().Single();
        string text = "继续 " + task.Handle.Identity.ThreadId + " 执行并发检查";
        int beforeRace = f.Rpc!.Calls.Count;
        var continuationRace = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text), beforeExecute: () =>
        {
            workspace.TryReserve("legacy/continuation-capacity-race", otherTask, otherLocation, "synthetic-other-owner")
                .Reservation!.Accepted(otherTask, "second-other-turn");
        });
        check("continuation losing capacity after authorization is Failed with zero RPC and no replay",
            continuationRace.Result.State == ToolExecutionState.Rejected && f.Rpc.Calls.Count == beforeRace
            && (await f.Ledger.ReadConversationAsync(f.Identity)).Operations.Single(o => o.Id == continuationRace.Operation.Id).Stage == OperationStage.Failed
            && continuationRace.Result.Facts.GetProperty("dispatched").ValueKind == JsonValueKind.False);
        workspace.Observe(new(otherTask, otherLocation, "second-other-turn", WorkspaceTaskState.Completed, now, true,
            WorkspaceEvidenceKind.LiveBackend, OwnerScope: "synthetic-other-owner"));
        var started = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text));
        task = f.Runtime.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        f.Rpc!.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "inProgress") });
        await WaitStage(f, started.Operation.Id, OperationStage.Running);
        task = f.Runtime.Tasks().Single();
        string steerText = "补充 " + task.Handle.Identity.ThreadId + " 只检查日志";
        var steered = await f.ExecuteAsync(steerText, Call("steer_turn", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId,
            revision = task.Snapshot.Revision, expectedTurnId = turn, text = steerText }));
        check("steering the exact active turn at capacity does not allocate another slot",
            steered.Result.State == ToolExecutionState.Accepted && workspace.Snapshot().Occupied == 1);
        task = f.Runtime.Tasks().Single();
        var interrupted = await f.ExecuteAsync("中断 " + task.Handle.Identity.ThreadId, Call("interrupt_turn", new
        { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision, turnId = turn }));
        check("interrupting at capacity does not consume another slot or release before terminal evidence",
            interrupted.Result.State == ToolExecutionState.Accepted && workspace.Snapshot().Occupied == 1);
        workspace.RestoreUnresolved(new("owned/same-turn-uncertain-approval", task.Handle.Identity,
            new("local", "windows", task.Snapshot.Cwd), turn, now, OwnerScope: f.Runtime.WorkspaceOwnerScope));
        check("uncertain same-turn approval groups with the existing execution and stays visibly uncertain",
            workspace.Snapshot() is { Occupied: 1, Unknown: 1 });
        await f.Runtime.DisposeAsync();
        check("runtime disposal preserves global unresolved occupancy", workspace.Snapshot() is { Occupied: 1, Unknown: 1 });
    }
    private static async Task WorkspaceRecoveryAsync(string directory, Action<string, bool> check)
    {
        var now = new TestClock().UtcNow;
        await using var f = await Fixture.OpenAsync(Path.Combine(directory, "workspace-recovery"));
        Directory.CreateDirectory(f.Profile.Creation.Cwd);
        await f.ExecuteAsync("新建 demo", CreateCall());
        var createdTask = f.Runtime.Tasks().Single();
        string text = "继续 " + createdTask.Handle.Identity.ThreadId + " 测试恢复";
        await f.ExecuteAsync(text, WriteCall("continue_owned_task", createdTask, text));
        string acceptedTurn = f.Runtime.Tasks().Single().Snapshot.CurrentTurnId!;
        await f.Runtime.DisposeAsync();
        var other = new ConversationIdentity("new-current-user", "new-current-binding", "main");
        f.Active = other;
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now, maxConcurrentExecutions: 1);
        await using (var recovered = new OwnedControlRuntime(other, () => other, f.Ledger, f.DataPath, [f.Profile], f.Connect,
            () => now, workspace))
        {
            int before = f.ConnectCount;
            await recovered.PrepareWorkspaceAsync();
            var catalogue = await recovered.ReadCatalogueAsync(default);
            check("workspace recovery scans historical protected ownership without connecting or adopting it into current UI",
                workspace.Ready && f.ConnectCount == before && recovered.Tasks().Count == 0 && catalogue.Tasks.Count == 0);
            string separatePath = Path.Combine(directory, "historical-capacity-separate-project");
            Directory.CreateDirectory(separatePath);
            check("historical owned dispatch consumes global capacity in another directory before any RPC",
                workspace.Snapshot().Occupied == 1 && workspace.CheckStart(null, new("local", "windows", separatePath)).Code == "workspace_global_capacity"
                && f.ConnectCount == before);
            check("historical creation-only unknown conservatively blocks a new identity in the same directory",
                !recovered.CanCreateWorkspace("demo"));
            var location = new WorkspaceLocation("local", "windows", f.Profile.Creation.Cwd);
            workspace.Observe(new(createdTask.Handle.Identity, location, "older-turn", WorkspaceTaskState.Completed,
                now, true, WorkspaceEvidenceKind.LiveBackend, true, f.Runtime.WorkspaceOwnerScope));
            check("recovered accepted start does not release on a different historical turn", workspace.Blockers().Count == 1);
            workspace.Observe(new(createdTask.Handle.Identity, location, acceptedTurn, WorkspaceTaskState.Completed,
                now, true, WorkspaceEvidenceKind.LiveBackend, true, f.Runtime.WorkspaceOwnerScope));
            check("recovered resource claim binds the actual accepted turn receipt", workspace.Blockers().Count == 0
                && workspace.CanStart(null, location));
        }
        f.Active = f.Identity;
        var restoredGuard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using (var restored = new OwnedControlRuntime(f.Identity, () => f.Active, f.Ledger, f.DataPath,
            [f.Profile], f.Connect, () => now, restoredGuard))
        {
            int before = f.ConnectCount;
            await restored.PrepareWorkspaceAsync();
            check("restart creation capability does not bypass historical workspace claims",
                restored.WriteFailure("demo", "thread/start") == null && !restored.CanCreateWorkspace("demo") && f.ConnectCount == before);
            using (restored.Enter()) await restored.EnsureSessionAsync("demo", "thread/read", true, default);
            check("read-only restoration alone supplies no fresh execution observation or write permission",
                !restored.CanCreateWorkspace("demo") && restored.Tasks().Single().ObservedAt == null
                && restored.WriteFailure("demo", "turn/start") != null);
        }
        var empty = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using (var noProfiles = new OwnedControlRuntime(other, () => other, f.Ledger,
            Path.Combine(directory, "empty-owned-recovery"), [], f.Connect, () => now, empty))
        {
            // Use a distinct empty ledger: an existing historical owned operation with removed
            // configuration must remain blocked even when today's profiles list is empty.
            await noProfiles.PrepareWorkspaceAsync();
            check("removed profiles cannot erase historical owned recovery obligations", !empty.Ready);
        }
        await using var clean = await Fixture.OpenAsync(Path.Combine(directory, "workspace-empty"));
        var cleanGuard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using (var noProfiles = new OwnedControlRuntime(clean.Identity, () => clean.Active, clean.Ledger,
            clean.DataPath, [], clean.Connect, () => now, cleanGuard))
        {
            await noProfiles.PrepareWorkspaceAsync();
            check("no profiles and no historical owned facts completes recovery for legacy use", cleanGuard.Ready && clean.ConnectCount == 0);
        }
        File.Delete(f.Runtime.ProvenancePath("demo"));
        var missing = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now);
        await using (var deleted = new OwnedControlRuntime(f.Identity, () => f.Active, f.Ledger,
            f.DataPath, [f.Profile], f.Connect, () => now, missing))
        {
            await deleted.PrepareWorkspaceAsync();
            check("accepted Core creation with deleted ownership proof fails closed", !missing.Ready);
        }
    }
    private static async Task TerminalCapacityRecoveryAsync(string directory, Action<string, bool> check)
    {
        foreach (bool unknown in new[] { false, true })
        {
            string label = unknown ? "unknown-steer" : "accepted-steer";
            string fixtureDirectory = Path.Combine(directory, "terminal-capacity-" + label);
            var now = new TestClock().UtcNow;
            var guard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now, maxConcurrentExecutions: 1);
            await using var f = await Fixture.OpenAsync(fixtureDirectory, workspace: guard);
            await f.ExecuteAsync("新建 demo", CreateCall());
            var task = f.Runtime.Tasks().Single();
            string text = "继续 " + task.Handle.Identity.ThreadId + " 验证终态恢复";
            var started = await f.ExecuteAsync(text, WriteCall("continue_owned_task", task, text));
            task = f.Runtime.Tasks().Single();
            string turn = task.Snapshot.CurrentTurnId!;
            f.Rpc!.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "inProgress") });
            await WaitStage(f, started.Operation.Id, OperationStage.Running);
            task = f.Runtime.Tasks().Single();
            f.Rpc.DropSteer = unknown;
            string steer = "补充 " + task.Handle.Identity.ThreadId + " 核对终态";
            var amended = await f.ExecuteAsync(steer, Call("steer_turn", new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId,
                revision = task.Snapshot.Revision, expectedTurnId = turn, text = steer }));
            if (unknown) await WaitStage(f, amended.Operation.Id, OperationStage.Unknown);
            f.Rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = Rpc.Turn(turn, "completed") });
            await WaitStage(f, started.Operation.Id, OperationStage.Completed);
            for (int i = 0; i < 100 && guard.Snapshot().Occupied != (unknown ? 1 : 0); i++) await Task.Delay(10);
            var history = await f.Ledger.ReadConversationAsync(f.Identity);
            var terminal = history.Events.Single(e => e.RelatedOperationId == started.Operation.Id && e.Body.Contains("turn_lifecycle_observed")
                && e.Body.Contains("\"phase\":\"Completed\""));
            using var proof = JsonDocument.Parse(terminal.Body);
            check(label + " persists real lifecycle proof bound to endpoint configuration operation and exact turn",
                proof.RootElement.GetProperty("operationId").GetGuid() == started.Operation.Id
                && proof.RootElement.GetProperty("configurationDigest").GetString() == OwnedVerification.ConfigurationDigest(f.Profile.Configuration)
                && proof.RootElement.GetProperty("endpoint").Deserialize<HostEndpointIdentity>() == task.Handle.Identity.Endpoint
                && proof.RootElement.GetProperty("observedTurnId").GetString() == turn);
            check(label + " ordinary terminal releases accepted execution but does not reconcile unknown amend",
                guard.Snapshot().Occupied == (unknown ? 1 : 0) && guard.Snapshot().Unknown == (unknown ? 1 : 0));
            if (unknown)
            {
                string forgedUserText = terminal.Body.Replace("\"Reconciliation\":false", "\"Reconciliation\":true", StringComparison.Ordinal);
                await f.Ledger.ReceiveAsync(f.Identity, "synthetic-forged-terminal-user", forgedUserText, MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
            }
            await f.Runtime.DisposeAsync();
            await f.Ledger.DisposeAsync();
            f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(fixtureDirectory, "synthetic-ledger")),
                new WindowsStateProtector(), f.Clock);
            var recoveredGuard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now, maxConcurrentExecutions: 1);
            int connections = f.ConnectCount;
            await using var recovered = new OwnedControlRuntime(f.Identity, () => f.Active, f.Ledger, f.DataPath,
                [f.Profile], f.Connect, () => now, recoveredGuard);
            await recovered.PrepareWorkspaceAsync();
            check(label + " recovery reopens protected ledger facts without starting a connection or adopting write handles",
                recoveredGuard.Ready && f.ConnectCount == connections && recovered.Tasks().Count == 0
                && recovered.WriteFailure("demo", "turn/start") != null);
            string separate = Path.Combine(directory, "separate-terminal-" + label);
            Directory.CreateDirectory(separate);
            check(label + " recovery excludes only proven terminal accepted claims and ignores forged user proof",
                recoveredGuard.Snapshot().Occupied == (unknown ? 1 : 0) && recoveredGuard.Snapshot().Unknown == (unknown ? 1 : 0)
                && recoveredGuard.CheckStart(null, new("local", "windows", separate)).Allowed == !unknown);
            if (!unknown)
            {
                // Simulate a persisted older host receipt. Its turn string is not a
                // verified binding and cannot borrow the preceding genuine terminal.
                var call = WriteCall("continue_owned_task", task, "继续 " + task.Handle.Identity.ThreadId + " 旧记录");
                var input = await f.ReceiveAsync("继续 " + task.Handle.Identity.ThreadId + " 旧记录", call);
                var lease = await f.Ledger.BeginGenerationAsync(f.Identity, [input.Message.SourceMessageId]);
                var target = new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, null, task.Handle.Identity.ProfileId);
                var grant = await f.Ledger.AuthorizeCurrentUserRequestAsync(f.Identity, input.Message.SourceMessageId,
                    call.Name, task.Handle.Identity.Key, input.Call.ArgumentsJson, input.Message.Body, target);
                var legacyOperation = await f.Ledger.QueueOperationAsync(f.Identity, grant.Id, Guid.NewGuid().ToString("N"));
                var invocation = await f.Ledger.RegisterToolInvocationAsync(lease, input.Message.SourceMessageId,
                    input.Call.Id, call.Name, input.Call.ArgumentsJson, true);
                await f.Ledger.BeginToolInvocationAsync(lease, invocation.Id, legacyOperation.Id);
                string oldReceipt = JsonSerializer.Serialize(new { observedTurnId = turn });
                await f.Ledger.FinishToolInvocationAsync(f.Identity, invocation.Id, ToolInvocationStage.Completed,
                    oldReceipt, OperationStage.Accepted, directReceipt: new(OperationStage.Accepted, oldReceipt, legacyOperation.TargetKey, target));
                var legacyGuard = new WorkspaceWriteCoordinator(recoverySources: ["owned"], clock: () => now, maxConcurrentExecutions: 1);
                await using var legacyRecovery = new OwnedControlRuntime(f.Identity, () => f.Active, f.Ledger, f.DataPath,
                    [f.Profile], f.Connect, () => now, legacyGuard);
                await legacyRecovery.PrepareWorkspaceAsync();
                check("old unproved accepted turn cannot borrow another operation terminal to release capacity",
                    legacyGuard.Ready && legacyGuard.Snapshot() is { Occupied: 1, Unknown: 1 }
                    && legacyGuard.CheckStart(null, new("local", "windows", separate)).Code == "workspace_global_capacity"
                    && f.ConnectCount == connections);
            }
        }
    }
    private static async Task WaitStage(Fixture fixture, Guid operation, OperationStage expected)
    {
        for (int i = 0; i < 200; i++)
        {
            if ((await fixture.Ledger.ReadConversationAsync(fixture.Identity)).Operations.Single(o => o.Id == operation).Stage == expected) return;
            if (fixture.Runtime.Failure != null) throw new InvalidOperationException(fixture.Runtime.Failure);
            await Task.Delay(10);
        }
        throw new TimeoutException("Synthetic lifecycle did not reach " + expected);
    }
    private static ModelToolCall CreateCall(string configurationId = "demo") => Call("create_task", new { configurationId });
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    private static ModelToolCall WriteCall(string name, OwnedRuntimeTask task, string? text = null) =>
        text == null ? Call(name, new { configurationId = task.ConfigurationId, threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision })
        : Call(name, new { configurationId = task.ConfigurationId, threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision, text });
    private static ModelToolCall ApprovalCall(OwnedRuntimeTask task, string turn, string token) => Call("respond_approval",
        new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, revision = task.Snapshot.Revision,
            turnId = turn, requestToken = token, decision = "accept" });
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 5, 0, 0, TimeSpan.Zero); }
    private sealed class SingleCallModel(ModelToolCall call) : IToolModel
    {
        private bool called;
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        { if (called) return Task.FromResult(ToolModelResponse.Reply("synthetic reply")); called = true; return Task.FromResult(ToolModelResponse.Call(call)); }
    }
    private sealed record Request(InboxMessage Message, ModelToolCall Call);
    private sealed record Executed(ToolExecutionResult Result, OperationRecord Operation, ToolExecutionContext Context, ModelToolCall Call);
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ConversationIdentity Identity = new("synthetic-owner", "synthetic-binding", "main");
        internal ConversationIdentity? Active;
        internal readonly TestClock Clock = new();
        internal required DurableConversationLedger Ledger;
        internal required ConfiguredOwnedProfile Profile;
        internal required string DataPath;
        internal OwnedControlRuntime Runtime = null!;
        internal OwnedControlToolAdapter Adapter = null!;
        internal Rpc? Rpc;
        internal int ConnectCount;
        internal bool DispatchSeenAtConnect;
        internal bool ReservedSeenAtConnect;
        internal Action? OnConnect;
        private WorkspaceWriteCoordinator? workspace;
        internal static async Task<Fixture> OpenAsync(string directory, bool verified = true, bool twoProfiles = false,
            WorkspaceWriteCoordinator? workspace = null)
        {
            Directory.CreateDirectory(directory);
            var clock = new TestClock();
            var config = new OwnedServerConfiguration
            {
                Connection = new(new("local", "synthetic-profile"), OwnedBackend.LocalStdio, true),
                ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = "windows",
                ExpectedCodexHome = Path.Combine(directory, "synthetic-codex-home"),
                Projects = ImmutableDictionary<string, string>.Empty.Add("sample", Path.Combine(directory, "sample")),
                Models = ImmutableHashSet.Create("synthetic-model"), RequestTimeout = TimeSpan.FromSeconds(3)
            };
            if (verified) config = config with { Verification = new(config.Connection, config.ExpectedVersion, config.ExpectedPlatformOs,
                OwnedVerification.ConfigurationDigest(config), OwnedVerification.SupportedMethods.ToImmutableHashSet(),
                VerificationKind.SyntheticReplay, "synthetic-adapter-contract-only", clock.UtcNow.AddMinutes(-1), clock.UtcNow.AddHours(1)) };
            var fixture = new Fixture
            {
                Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "synthetic-ledger")),
                    new WindowsStateProtector(), clock),
                Profile = new("demo", "示例范围", config, new("sample", config.Projects["sample"], "synthetic-model", "on-request", "workspace-write"),
                    Mode: OwnedProfileMode.VerifiedControl),
                DataPath = Path.Combine(directory, "owned"), workspace = workspace
            };
            fixture.Active = fixture.Identity;
            if (workspace != null) Directory.CreateDirectory(config.Projects["sample"]);
            var profiles = new List<ConfiguredOwnedProfile> { fixture.Profile };
            if (twoProfiles)
            {
                var alternateConfig = config with { Connection = config.Connection with { Endpoint = new("local", "synthetic-profile-2") } };
                alternateConfig = alternateConfig with { Verification = new(alternateConfig.Connection, alternateConfig.ExpectedVersion,
                    alternateConfig.ExpectedPlatformOs, OwnedVerification.ConfigurationDigest(alternateConfig),
                    OwnedVerification.SupportedMethods.ToImmutableHashSet(), VerificationKind.SyntheticReplay,
                    "synthetic-alternate-adapter-contract-only", clock.UtcNow.AddMinutes(-1), clock.UtcNow.AddHours(1)) };
                profiles.Add(fixture.Profile with { Id = "alternate", Label = "备用范围", Configuration = alternateConfig });
            }
            fixture.Runtime = new(fixture.Identity, () => fixture.Active, fixture.Ledger, fixture.DataPath,
                profiles, fixture.Connect, () => fixture.Clock.UtcNow, workspace);
            fixture.Adapter = new(fixture.Runtime);
            return fixture;
        }
        internal async Task<IJsonRpcConnection> Connect(ConfiguredOwnedProfile profile, CancellationToken ct)
        {
            ConnectCount++;
            ReservedSeenAtConnect = workspace?.Snapshot() is { Reserved: 1 };
            DispatchSeenAtConnect = (await Ledger.ReadConversationAsync(Identity, ct)).Operations.Any(o => o.Stage == OperationStage.Dispatching);
            OnConnect?.Invoke();
            var previous = Rpc;
            Rpc = new(profile.Configuration, async () => (await Ledger.ReadConversationAsync(Identity)).Operations.Any(o => o.Stage == OperationStage.Dispatching));
            if (previous != null) foreach (var item in previous.Threads) Rpc.Threads[item.Key] = item.Value;
            return Rpc;
        }
        internal async Task<Request> ReceiveAsync(string body, ModelToolCall call, TaskReference? task = null, Guid? referencedEventId = null)
        {
            if (OwnedControlToolAdapter.Catalog().TryGet(call.Name, out var descriptor)
                && descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _)) call = call with { ArgumentsJson = canonical };
            var received = await Ledger.ReceiveAsync(Identity, Guid.NewGuid().ToString("N"), body,
                MessageTrust.AuthenticatedUser, Clock.UtcNow, task, referencedEventId);
            return new(received.Message, call);
        }
        internal ValueTask<UserAuthorizationDecision> AuthorizeAsync(Request request)
        {
            OwnedControlToolAdapter.Catalog().TryGet(request.Call.Name, out var descriptor);
            return Adapter.AuthorizeAsync(new(request.Message, request.Call, descriptor), default);
        }
        internal async Task<Executed> ExecuteAsync(string body, ModelToolCall call, Guid? referencedEventId = null, Action? beforeExecute = null)
        {
            var request = await ReceiveAsync(body, call, referencedEventId: referencedEventId);
            var decision = await AuthorizeAsync(request);
            if (!decision.Allowed) throw new InvalidOperationException("Synthetic request not authorized: " + decision.Reason);
            var lease = await Ledger.BeginGenerationAsync(Identity, [request.Message.SourceMessageId]);
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, request.Message.SourceMessageId,
                call.Name, decision.TargetKey!, request.Call.ArgumentsJson, decision.SourceExcerpt!, decision.Task);
            var operation = await Ledger.QueueOperationAsync(Identity, grant.Id, Guid.NewGuid().ToString("N"));
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, request.Message.SourceMessageId,
                request.Call.Id, call.Name, request.Call.ArgumentsJson, true);
            await Ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
            var context = new ToolExecutionContext(Identity, request.Message.SourceMessageId, request.Message.Revision,
                invocation.Id, operation.Id, operation.IdempotencyKey);
            beforeExecute?.Invoke();
            var result = await Adapter.ExecuteAsync(context, request.Call, default);
            var stage = result.State switch
            {
                ToolExecutionState.Accepted => OperationStage.Accepted, ToolExecutionState.Running => OperationStage.Running,
                ToolExecutionState.Completed => OperationStage.Completed, ToolExecutionState.Rejected => OperationStage.Failed, _ => OperationStage.Unknown
            };
            await Ledger.FinishToolInvocationAsync(Identity, invocation.Id, result.State == ToolExecutionState.Unknown ? ToolInvocationStage.Unknown : ToolInvocationStage.Completed,
                result.Facts.GetRawText(), stage, directReceipt: result.TargetKey != null || result.Task != null
                    ? new(stage, result.Facts.GetRawText(), result.TargetKey ?? operation.TargetKey, result.Task) : null);
            return new(result, operation, context, request.Call);
        }
        internal async Task<ToolExecutionResult> ReadAsync(string name, object args)
        {
            var request = await ReceiveAsync("查询", Call(name, args));
            return await Adapter.ExecuteAsync(new(Identity, request.Message.SourceMessageId, request.Message.Revision,
                Guid.NewGuid(), null, null), request.Call, default);
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); await Ledger.DisposeAsync(); }
    }
    private sealed class Rpc(OwnedServerConfiguration config, Func<Task<bool>> dispatch) : IJsonRpcConnection
    {
        public ConnectionIdentity Identity => config.Connection;
        public bool IsConnected { get; private set; } = true;
        public event Action<RpcIncoming>? Incoming;
        public event Action? Disconnected;
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, JsonElement> Threads = new();
        internal bool DispatchSeenAtCreate, DropTurn, DropSteer;
        internal int Responses;
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(method); var args = JsonSerializer.SerializeToElement(parameters);
            if (method == "initialize") return J(new { userAgent = "codex_cli_rs/" + config.ExpectedVersion,
                platformOs = "windows", platformFamily = "windows", codexHome = config.ExpectedCodexHome });
            if (method == "thread/start")
            {
                DispatchSeenAtCreate = await dispatch();
                string id = Guid.NewGuid().ToString("D"), cwd = args.GetProperty("cwd").GetString()!;
                Threads[id] = Thread(id, cwd);
                return J(new { thread = Threads[id], cwd, model = args.GetProperty("model").GetString(),
                    approvalPolicy = args.GetProperty("approvalPolicy").GetString(), approvalsReviewer = "user",
                    sandbox = new { type = "workspaceWrite", networkAccess = false } });
            }
            if (method == "turn/start")
            {
                if (DropTurn) throw new RpcTransportException(true, "synthetic_lost_receipt");
                return J(new { turn = Turn(Guid.NewGuid().ToString("D"), "inProgress") });
            }
            if (method == "turn/steer")
            {
                if (DropSteer) throw new RpcTransportException(true, "synthetic_lost_steer_receipt");
                return J(new { turnId = args.GetProperty("expectedTurnId").GetString() });
            }
            if (method is "thread/read" or "thread/unarchive") return J(new { thread = Threads[args.GetProperty("threadId").GetString()!] });
            if (method == "thread/resume") return J(new { thread = Threads[args.GetProperty("threadId").GetString()!],
                cwd = args.GetProperty("cwd").GetString(), model = args.GetProperty("model").GetString(),
                approvalPolicy = args.GetProperty("approvalPolicy").GetString(), approvalsReviewer = "user",
                sandbox = new { type = "workspaceWrite", networkAccess = false } });
            return J(new { });
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct) { Calls.Add(method); return Task.CompletedTask; }
        public Task RespondAsync(JsonElement requestId, object result, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Responses++; return Task.CompletedTask; }
        internal void Emit(string method, object parameters, object? requestId = null) => Incoming?.Invoke(new(method, J(parameters), requestId == null ? null : J(requestId)));
        internal static JsonElement Thread(string id, string cwd, params JsonElement[] turns) => J(new { id, cwd, turns });
        internal static JsonElement Turn(string id, string status, params JsonElement[] items) => J(new { id, status, items });
        private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        public ValueTask DisposeAsync() { IsConnected = false; Disconnected?.Invoke(); return ValueTask.CompletedTask; }
    }
}
