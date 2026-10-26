using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual WPF runtime, channel parser/outbox and DPAPI ledger; only synthetic model, HTTP and RPC.
// This runs before application settings. No real Codex process, transport or account is opened.
internal static class OwnedHostRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--owned-host-runtime-test");
        bool reportPagesOnly = args.Contains("--owned-report-pages-test");
        bool coldSelectionOnly = args.Contains("--owned-cold-selection-test");
        if (reportPagesOnly) index = Array.IndexOf(args, "--owned-report-pages-test");
        if (coldSelectionOnly) index = Array.IndexOf(args, "--owned-cold-selection-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Explicit output directory required.");
            if (coldSelectionOnly) await ColdSelectionAsync(args[index + 1]);
            else if (reportPagesOnly) await ReportPagesAsync(args[index + 1]);
            else await RunAsync(args[index + 1]);
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task ColdSelectionAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string data = Path.Combine(directory, "host"), taskId;
        ClawBinding savedBinding;
        var profile = Profile(Path.Combine(directory, "profile"), verified: true);
        int connections = 0;
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            connections++;
            return Task.FromResult<IJsonRpcConnection>(new FakeRpc(configured.Configuration, () => Task.FromResult(true)));
        });
        await using (var first = await DurableRuntimeTests.Fixture.CreateAsync(data, ownedOptions: options))
        {
            first.Settings.ChatGpt.TaskControlEnabled = true;
            await SendToolAsync(first, "create", "创建 demo", _ => Call("create_task", new { configurationId = "demo" }));
            taskId = first.Runtime.OwnedRuntime!.Tasks().Single().Handle.Identity.ThreadId;
            savedBinding = first.Binding;
        }
        int before = connections;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(data, modelEnabled: false, ownedOptions: options, savedBinding: savedBinding);
        await f.InputAsync("cold-list", "任务列表");
        await ConversationHistoryRuntimeTests.Settled(f, "cold-list");
        var context = await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!);
        var choice = context.PendingSelection!.Candidates.Select((c, i) => (c, i)).Single(x => x.c.Task.ThreadId == taskId);
        bool listed = !f.Sent.Last().Contains(taskId) && f.Sent.Last().Contains("待刷新");
        await f.InputAsync("cold-select", "选择 " + (choice.i + 1));
        await ConversationHistoryRuntimeTests.Settled(f, "cold-select");
        context = await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!);
        bool selected = context.Focus?.Task.ThreadId == taskId && connections == before && f.Script.Calls == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { listedWithoutUuid = listed, selectedWithoutConnectionOrModel = selected }));
        if (!listed || !selected) throw new InvalidOperationException("cold_selection_failed");
    }

    private static async Task ReportPagesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var profile = Profile(Path.Combine(directory, "profile"), verified: false);
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile],
            (_, _) => throw new InvalidOperationException("Report paging must not connect"));
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "host"), ownedOptions: options);
        f.Settings.ChatGpt.Enabled = false;
        var runtime = f.Runtime.OwnedRuntime!;
        var snapshot = runtime.SaveQuerySnapshot("read_owned_report", "demo/task", "turn-original", 2000,
            new[] { "第一段原文", "第二段原文🙂", "第三段原文" }.Select(text => JsonSerializer.SerializeToElement(new { finalText = text, sourceTruncated = false })));
        var checks = new Dictionary<string, bool>();
        async Task Check(string source, string command, string expected)
        {
            await f.InputAsync(source, command);
            await ConversationHistoryRuntimeTests.Settled(f, source);
            checks.Add(source, f.Sent.Last().Contains(expected));
            if (!checks[source]) throw new InvalidOperationException(source);
        }
        await Check("middle", $"报告续页 {snapshot.Id} 2", "第二段原文🙂");
        checks.Add("next_command", f.Sent.Last().Contains("报告下一页"));
        await Check("last", "报告下一页", "第三段原文");
        await Check("numbered", "报告第3页", "第三段原文");
        checks.Add("last_has_no_next", !f.Sent.Last().Contains("继续阅读"));
        await Check("range", $"报告续页 {snapshot.Id} 4", "共有 3 页");
        await Check("unavailable", $"报告续页 {new string('b', 32)} 2", "已过期或因重启清除");
        await Check("malformed", "报告续页 实际编号 2", "完整续页命令");
        string longText = new string('文', 899) + "🙂" + new string('字', 1099);
        var longSnapshot = runtime.SaveQuerySnapshot("read_owned_report", "demo/long", "old-turn", 2000,
            [JsonSerializer.SerializeToElement(new { finalText = longText, sourceTruncated = true })]);
        int before = f.Sent.Count;
        await f.InputAsync("long-page", $"报告续页 {longSnapshot.Id} 1");
        await ConversationHistoryRuntimeTests.Settled(f, "long-page");
        await ConversationHistoryRuntimeTests.WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            var message = state.Inbox.Single(m => m.SourceMessageId == WeChatText.Hash("long-page"));
            return state.Outbox.Where(o => o.GenerationId == message.GenerationId).All(o => o.Stage == OutboxStage.Accepted);
        });
        string[] sent = f.Sent.Skip(before).ToArray();
        checks.Add("long_page_lossless_segmented_with_truncation_notice", string.Concat(sent) == runtime.ReadReportPage(longSnapshot.Id, 1)
            && sent.All(s => s.Length <= 900 && !char.IsHighSurrogate(s[^1]))
            && sent.Last().Contains("不代表完整报告"));
        var unrelated = runtime.SaveQuerySnapshot("list_owned_tasks", "catalogue", null, 1,
            [JsonSerializer.SerializeToElement(new { privateMarker = "not a report" })]);
        await Check("wrong-query", $"报告续页 {unrelated.Id} 1", "已过期或因重启清除");
        for (int i = 0; i < 8; i++) runtime.SaveQuerySnapshot("list_owned_tasks", "other-" + i, null, 1,
            [JsonSerializer.SerializeToElement(new { count = i })]);
        await Check("evicted", $"报告续页 {snapshot.Id} 2", "已过期或因重启清除");
        checks.Add("no_model_or_operations", f.Script.Calls == 0 && (await f.ReadAsync()).Operations.IsEmpty);
        checks.Add("page_query_does_not_replace_work_constraints", DurableAssistantRuntime.IsKnownReadonlyInput($"报告续页 {snapshot.Id} 2")
            && !DurableAssistantRuntime.IsKnownReadonlyInput($"报告续页 {snapshot.Id} 2;停止任务"));
        await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks));
        if (checks.Values.Any(ok => !ok)) throw new InvalidOperationException("report_page_checks_failed");
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("New absolute test directory required.");
        if (Application.Current?.Dispatcher.CheckAccess() != true)
            throw new InvalidOperationException("The synthetic host test requires the active WPF dispatcher.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output directory rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Save() => File.WriteAllText(Path.Combine(directory, "owned-host-checks.json"), JsonSerializer.Serialize(new
        {
            passed = checks.Values.All(v => v), count = checks.Count, checks, realCodexProcessesStarted = 0,
            realNetworkConnections = 0, realMessagesSent = 0, productionCredentialsRead = false,
            runtime = "actual DurableAssistantRuntime with synthetic model, HTTP channel and JSON-RPC"
        }, new JsonSerializerOptions { WriteIndented = true }));
        void Check(string name, bool condition)
        { checks.Add(name, condition); Save(); if (!condition) throw new InvalidOperationException(name); }
        try
        {
            await DefaultDisabledAsync(directory, Check);
            await IntegratedLifecycleAsync(directory, Check);
            await SharedWorkspaceLifecycleAsync(directory, Check);
            await SharedWorkspaceLifecycleAsync(directory, (name, passed) => Check("global limit across different directories: " + name, passed), true);
        }
        catch (Exception error)
        {
            checks["failure"] = false; Save();
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString());
            throw;
        }
        finally { Save(); Console.WriteLine($"{checks.Count(c => c.Value)}/{checks.Count} owned host checks passed"); }
    }

    private static async Task DefaultDisabledAsync(string directory, Action<string, bool> check)
    {
        int connections = 0;
        var profile = Profile(Path.Combine(directory, "disabled"), verified: false);
        var options = new OwnedHostOptions(Path.Combine(directory, "disabled-owned"), [profile],
            (_, _) => { Interlocked.Increment(ref connections); throw new InvalidOperationException("Unverified connection must not be constructed."); });
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "disabled"), ownedOptions: options);
        check("construction and activation with no verification starts no connection",
            f.Runtime.OwnedRuntime != null && connections == 0 && f.Runtime.OwnedRuntime.Tasks().Count == 0);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        bool createAdvertised = false;
        f.Script.Handler = (request, _) =>
        {
            createAdvertised |= request.Tools.Any(t => t.Name == "create_task");
            return Task.FromResult(request.Observations.IsEmpty ? ToolModelResponse.Call(Call("create_task", new { configurationId = "demo" }))
                : ToolModelResponse.Reply("模拟：未启用控制。"));
        };
        await f.InputAsync("disabled-create", "新建 demo");
        await WaitReplyAsync(f, "disabled-create");
        var snapshot = await f.ReadAsync();
        check("unverified create is absent from model tools and cannot dispatch",
            !createAdvertised && snapshot.Operations.IsEmpty && connections == 0);
        await SendToolAsync(f, "disabled-list", "查询自建范围", _ => Call("list_owned_tasks", new { }));
        check("local owned listing through actual host does not connect", connections == 0
            && (await f.ReadAsync()).ToolInvocations.Any(i => i.ToolName == "list_owned_tasks" && i.Stage == ToolInvocationStage.Completed));
        f.Settings.ChatGpt.Enabled = false;
        int modelCalls = f.Script.Calls;
        foreach (string command in new[] { "控制能力", "能力状态", "操作记录" })
        {
            string source = "disabled-diagnostic-" + command;
            await f.InputAsync(source, command); await WaitReplyAsync(f, source);
            check("model-disabled diagnostic is local without authority " + command,
                f.Script.Calls == modelCalls && connections == 0 && (await f.ReadAsync()).Operations.IsEmpty
                && (command == "操作记录" ? f.Sent.Last().Contains("没有已登记", StringComparison.Ordinal)
                    : f.Sent.Last().Contains("自建任务控制尚未开放", StringComparison.Ordinal)));
        }
    }

    private static async Task IntegratedLifecycleAsync(string directory, Action<string, bool> check)
    {
        DurableRuntimeTests.Fixture? fixture = null;
        FakeRpc? rpc = null;
        int connections = 0;
        bool durableDispatchAtConnect = false, toolAdvertised = false;
        var profile = Profile(Path.Combine(directory, "active"), verified: true);
        var options = new OwnedHostOptions(Path.Combine(directory, "active-owned"), [profile],
            async (configured, ct) =>
            {
                Interlocked.Increment(ref connections);
                var state = await fixture!.Ledger.ReadConversationAsync(fixture.Runtime.Identity!, ct);
                durableDispatchAtConnect = state.Operations.Any(o => o.Stage == OperationStage.Dispatching);
                rpc = new FakeRpc(configured.Configuration, async () => (await fixture.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
                return rpc;
            });
        await using var f = fixture = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "active"), ownedOptions: options);
        var identity = f.Runtime.Identity!;
        f.Settings.ChatGpt.TaskControlEnabled = true;
        check("verified configuration construction still starts no process", connections == 0);
        await SendToolAsync(f, "ambiguous", "帮我看看这个项目", request =>
        {
            toolAdvertised |= request.Tools.Any(t => t.Name == "create_task");
            return Call("create_task", new { configurationId = "demo" });
        });
        check("actual tool loop refuses model create without current explicit request",
            toolAdvertised && connections == 0 && (await f.ReadAsync()).Operations.IsEmpty);
        await SendToolAsync(f, "create", "新建 demo", _ => Call("create_task", new { configurationId = "demo" }));
        var owned = f.Runtime.OwnedRuntime!;
        var task = owned.Tasks().Single();
        var created = (await f.ReadAsync()).Operations.Single(o => o.Action == "create_task");
        check("actual host model create commits dispatch before fake initialization and create",
            connections == 1 && durableDispatchAtConnect && rpc!.DispatchSeenAtCreate);
        check("host create preserves original immutable authorization target",
            created.Stage == OperationStage.Accepted && created.Task == null && created.TargetKey == "owned-create/demo"
            && task.Handle.Access == OwnedTaskAccess.CreatedThisSession);
        check("host adopts task only after protected provenance exists", new FileInfo(owned.ProvenancePath("demo")).Length > 0);

        f.Settings.ChatGpt.Enabled = false;
        int diagnosticModelCalls = f.Script.Calls, diagnosticRpcCalls = rpc!.Methods.Count;
        foreach (string command in new[] { "控制能力", "能力状态", "操作记录 " + created.Id.ToString("D"), "操作记录 " + created.Id.ToString("N")[..8] })
        {
            string source = "active-diagnostic-" + command;
            await f.InputAsync(source, command); await WaitReplyAsync(f, source);
            check("model-disabled existing-operation diagnostic causes no RPC or operation " + command,
                f.Script.Calls == diagnosticModelCalls && rpc.Methods.Count == diagnosticRpcCalls
                && (await f.ReadAsync()).Operations.Length == 1
                && (!command.StartsWith("操作记录", StringComparison.Ordinal) || f.Sent.Last().Contains("已受理", StringComparison.Ordinal)));
        }
        f.Settings.ChatGpt.Enabled = true;
        string historicalTurn = "synthetic-historical-turn";
        rpc.SetThread(task.Handle.Identity.ThreadId, task.Snapshot.Cwd, FakeRpc.Turn(historicalTurn, "completed"));
        await SendToolAsync(f, "read-history", "读取这个任务旧轮次报告", _ => Call("read_owned_report",
            new { configurationId = "demo", threadId = task.Handle.Identity.ThreadId, turnId = historicalTurn }));
        await WaitAsync(async () => (await f.ReadAsync()).Events.Any(e => e.Task?.TurnId == historicalTurn
            && e.Body.Contains("\"Code\":\"read_snapshot\"", StringComparison.Ordinal)));
        await Task.Delay(100);
        var history = await f.ReadAsync();
        check("actual history report reads retain audit without announcing old completion",
            history.Events.Any(e => e.Task?.TurnId == historicalTurn && e.Body.Contains("\"Code\":\"read_snapshot\"", StringComparison.Ordinal))
            && !history.Events.Any(e => e.Task?.TurnId == historicalTurn && e.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true)
            && history.Operations.Length == 1);

        string instruction = "继续 " + task.Handle.Identity.ThreadId + " 修复测试";
        await SendToolAsync(f, "continue-one", instruction, _ => ContinueCall(owned.Tasks().Single(), instruction));
        var start = (await f.ReadAsync()).Operations.Last(o => o.Action == "continue_owned_task");
        string turn = owned.Tasks().Single().Snapshot.CurrentTurnId!;
        check("turn acceptance is not a completion notice", start.Stage == OperationStage.Accepted
            && owned.Tasks().Single().Snapshot.State == OwnedTaskState.Accepted
            && !(await f.ReadAsync()).Events.Any(e => e.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true));
        rpc!.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(turn, "inProgress") });
        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == start.Id).Stage == OperationStage.Running);
        check("actual lifecycle pump records running separately", owned.Tasks().Single().Snapshot.State == OwnedTaskState.Running);
        rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(turn, "completed") });
        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == start.Id).Stage == OperationStage.Completed);
        await WaitNoticeAsync(f, turn, "这一轮已经结束");
        var completed = await f.ReadAsync();
        check("known turn-start completion reaches actual notification outbox",
            completed.Events.Any(e => e.Body.Contains("\"Method\":\"turn/start\"", StringComparison.Ordinal)
                && e.Body.Contains("\"Code\":\"turn_lifecycle_observed\"", StringComparison.Ordinal))
            && NoticeCount(completed, turn, "这一轮已经结束") == 1);
        rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(turn, "completed") });
        await Task.Delay(100);
        check("duplicate completion notification is not sent again", NoticeCount(await f.ReadAsync(), turn, "这一轮已经结束") == 1);

        instruction = "继续 " + task.Handle.Identity.ThreadId + " 再核对一次";
        await SendToolAsync(f, "continue-two", instruction, _ => ContinueCall(owned.Tasks().Single(), instruction));
        var secondStart = (await f.ReadAsync()).Operations.Last(o => o.Action == "continue_owned_task");
        string secondTurn = owned.Tasks().Single().Snapshot.CurrentTurnId!;
        rpc.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(secondTurn, "inProgress") });
        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == secondStart.Id).Stage == OperationStage.Running);
        int modelBeforeInterrupt = f.Script.Calls;
        f.Settings.ChatGpt.Enabled = false;
        await SendToolAsync(f, "interrupt", "中断 " + task.Handle.Identity.ThreadId, _ =>
            WriteCall("interrupt_turn", owned.Tasks().Single(), new { turnId = secondTurn }));
        var interrupted = (await f.ReadAsync()).Operations.Single(o => o.Action == "interrupt_turn");
        check("model-disabled explicit interrupt uses durable fast path without model calls",
            f.Script.Calls == modelBeforeInterrupt && rpc.Methods.Count(m => m == "turn/interrupt") == 1);
        await f.InputAsync("interrupt", "中断 " + task.Handle.Identity.ThreadId);
        check("duplicate interrupt channel delivery cannot redispatch",
            rpc.Methods.Count(m => m == "turn/interrupt") == 1
            && (await f.ReadAsync()).Operations.Count(o => o.Action == "interrupt_turn") == 1);
        await f.InputAsync("missing-interrupt", "中断 missing-thread");
        await WaitReplyAsync(f, "missing-interrupt");
        check("model-disabled unknown interrupt target cannot choose another task",
            f.Script.Calls == modelBeforeInterrupt && rpc.Methods.Count(m => m == "turn/interrupt") == 1);
        check("host interrupt acknowledgement does not announce observed interruption",
            interrupted.Stage == OperationStage.Accepted && NoticeCount(await f.ReadAsync(), secondTurn, "轮次中断") == 0);
        int beforeSelectionOperations = (await f.ReadAsync()).Operations.Length;
        await f.InputAsync("select-owned", "切换到 demo/" + task.Handle.Identity.ThreadId);
        await WaitReplyAsync(f, "select-owned");
        await f.InputAsync("current-owned", "当前任务");
        await WaitReplyAsync(f, "current-owned");
        var selectedFocus = (await f.Ledger.ReadConversationContextAsync(identity)).Focus?.Task;
        check("actual owned selection and current-task query save canonical focus without control",
            selectedFocus is { HostId: "local" } && selectedFocus.ThreadId == task.Handle.Identity.ThreadId
            && selectedFocus.ProfileId == task.Handle.Identity.ProfileId
            && (await f.ReadAsync()).Operations.Length == beforeSelectionOperations && f.Script.Calls == modelBeforeInterrupt
            && f.Sent.Last().Contains("demo/" + task.Handle.Identity.ThreadId, StringComparison.Ordinal));
        await f.InputAsync("focused-stop", "先停一下");
        await WaitReplyAsync(f, "focused-stop");
        check("model-disabled generic stop works after actual owned selection",
            rpc.Methods.Count(m => m == "turn/interrupt") == 2 && f.Script.Calls == modelBeforeInterrupt);
        var otherNotice = await f.Runtime.EnqueueNotificationAsync("synthetic-other-notice", "另一个合成任务的通知。",
            new("local", "synthetic-other-owned-task", "other-turn", task.Handle.Identity.ProfileId), default);
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Where(o => o.NotificationEventId == otherNotice.Event.Id)
            .All(o => o.Stage == OutboxStage.Accepted));
        await f.InputAsync("ambiguous-focused-stop", "先停一下");
        await WaitReplyAsync(f, "ambiguous-focused-stop");
        check("another task notification makes generic stop require clarification",
            rpc.Methods.Count(m => m == "turn/interrupt") == 2
            && !(await f.ReadAsync()).Operations.Any(o => o.SourceMessageId == WeChatText.Hash("ambiguous-focused-stop")));
        await f.InputAsync("explicit-current-stop", "停止当前任务");
        await WaitReplyAsync(f, "explicit-current-stop");
        check("explicit current task disambiguates notification while preserving exact selected target",
            rpc.Methods.Count(m => m == "turn/interrupt") == 3 && f.Script.Calls == modelBeforeInterrupt
            && (await f.Ledger.ReadConversationContextAsync(identity)).Focus?.Task == selectedFocus
            && (await f.ReadAsync()).Operations.Single(o => o.SourceMessageId == WeChatText.Hash("explicit-current-stop")).Task
                == new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, secondTurn, task.Handle.Identity.ProfileId));
        rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(secondTurn, "interrupted") });
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            return state.Operations.Single(o => o.Id == secondStart.Id).Stage == OperationStage.Interrupted
                && state.Operations.Where(o => o.Action == "interrupt_turn").All(o => o.Stage == OperationStage.Interrupted);
        });
        await WaitNoticeAsync(f, secondTurn, "轮次中断");
        await Task.Delay(100);
        check("start and interrupt terminal observations share one notification", NoticeCount(await f.ReadAsync(), secondTurn, "轮次中断") == 1);

        f.Settings.ChatGpt.Enabled = true;
        instruction = "继续 " + task.Handle.Identity.ThreadId + " 检查审批流程";
        await SendToolAsync(f, "continue-approval", instruction, _ => ContinueCall(owned.Tasks().Single(), instruction));
        var approvalStart = (await f.ReadAsync()).Operations.Last(o => o.Action == "continue_owned_task");
        string approvalTurn = owned.Tasks().Single().Snapshot.CurrentTurnId!;
        rpc.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(approvalTurn, "inProgress") });
        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == approvalStart.Id).Stage == OperationStage.Running);
        rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = approvalTurn,
            itemId = "command-host-one", command = "echo SYNTHETIC_APPROVAL", cwd = task.Snapshot.Cwd,
            startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, 81);
        var approval = owned.LoadedSession("demo")!.Client.PendingApprovals().Single();
        await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval?.RequestId
            == OwnedControlToolAdapter.ApprovalReference("demo", approval));
        var pendingContext = (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval!;
        var approvalState = await f.ReadAsync();
        var notice = approvalState.Events.Single(e => e.Id == pendingContext.SourceEventId);
        check("actual approval loop binds exact accepted notification and Core pending slot",
            notice.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token)
            && owned.Presented(notice.Id)?.Request.Token == approval.Token
            && pendingContext.Task == new TaskReference(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, approvalTurn, task.Handle.Identity.ProfileId)
            && pendingContext.BackendEpoch == approval.ConnectionEpoch
            && approvalState.Outbox.Where(o => o.NotificationEventId == notice.Id).All(o => o.Stage == OutboxStage.Accepted));
        check("approval message includes actual host task turn directory and decisions",
            notice.Body.Contains(task.Handle.Identity.HostId, StringComparison.Ordinal)
            && notice.Body.Contains(task.Handle.Identity.ThreadId, StringComparison.Ordinal)
            && notice.Body.Contains(approvalTurn, StringComparison.Ordinal) && notice.Body.Contains(task.Snapshot.Cwd, StringComparison.Ordinal)
            && notice.Body.Contains("本次会话批准", StringComparison.Ordinal));
        int modelBeforeApproval = f.Script.Calls;
        f.Settings.ChatGpt.Enabled = false;
        await SendToolAsync(f, "bare-approval", "同意", _ => ApprovalCall(owned.Tasks().Single(), approval));
        check("plain agreement cannot use the host pending slot as authorization", rpc.Responses == 0
            && !(await f.ReadAsync()).Operations.Any(o => o.Action == "respond_approval"));
        await SendToolAsync(f, "exact-approval", "批准 " + OwnedControlToolAdapter.ApprovalReference("demo", approval),
            _ => ApprovalCall(owned.Tasks().Single(), approval));
        var approvalOperation = (await f.ReadAsync()).Operations.Single(o => o.Action == "respond_approval");
        check("exact approval response is consumed once and remains unknown without server resolution",
            rpc.Responses == 1 && approvalOperation.Stage == OperationStage.Unknown);
        check("model-disabled explicit approval uses durable fast path without model calls", f.Script.Calls == modelBeforeApproval);
        await f.InputAsync("exact-approval", "批准 " + OwnedControlToolAdapter.ApprovalReference("demo", approval));
        check("duplicate approval channel delivery cannot consume twice",
            rpc.Responses == 1 && (await f.ReadAsync()).Operations.Count(o => o.Action == "respond_approval") == 1);
        rpc.Emit("serverRequest/resolved", new { threadId = task.Handle.Identity.ThreadId, requestId = 81 });
        await WaitAsync(async () => (await f.ReadAsync()).Operations.Single(o => o.Id == approvalOperation.Id).Stage == OperationStage.Accepted);
        await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval == null);
        check("resolved approval reconciles original operation and clears Core pending slot", rpc.Responses == 1);
        await f.InputAsync("resolved-approval-again", "批准 " + OwnedControlToolAdapter.ApprovalReference("demo", approval));
        await WaitReplyAsync(f, "resolved-approval-again");
        check("new message referencing resolved approval creates no new control operation",
            rpc.Responses == 1 && !(await f.ReadAsync()).Operations.Any(o => o.SourceMessageId == WeChatText.Hash("resolved-approval-again")));

        rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = approvalTurn,
            itemId = "command-host-expire", command = "echo EXPIRED_SCOPE", cwd = task.Snapshot.Cwd,
            startedAtMs = DateTimeOffset.UtcNow.Subtract(profile.Configuration.ApprovalLifetime).AddSeconds(2).ToUnixTimeMilliseconds() }, 83);
        var expiring = owned.LoadedSession("demo")!.Client.PendingApprovals().Single();
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, (expiring.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds) + 80));
        await f.InputAsync("expired-approval", "批准 " + OwnedControlToolAdapter.ApprovalReference("demo", expiring));
        await WaitReplyAsync(f, "expired-approval");
        await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval == null);
        check("expired exact approval reference cannot dispatch even with model disabled",
            rpc.Responses == 1 && f.Script.Calls == modelBeforeApproval
            && !(await f.ReadAsync()).Operations.Any(o => o.SourceMessageId == WeChatText.Hash("expired-approval")));

        rpc.Emit("item/commandExecution/requestApproval", new { threadId = task.Handle.Identity.ThreadId, turnId = approvalTurn,
            itemId = "command-host-reset", command = "echo RESET_SCOPE", cwd = task.Snapshot.Cwd,
            startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, 82);
        var beforeReset = owned.LoadedSession("demo")!.Client.PendingApprovals().Single();
        await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval != null);
        int callsBeforeReset = rpc.Methods.Count;
        await f.InputAsync("new-dialogue", "新对话");
        await WaitReplyAsync(f, "new-dialogue");
        check("actual new-dialogue command invalidates owned approval without interrupting task",
            owned.LoadedSession("demo")!.Client.PendingApprovals().Count == 0 && !owned.IsFocused(beforeReset)
            && (await f.Ledger.ReadConversationContextAsync(identity)).PendingApproval == null
            && rpc.Methods.Count == callsBeforeReset && rpc.Responses == 1);
        check("event and approval delivery did not trip host or runtime failure", f.Runtime.LastError == null && owned.Failure == null);

        // Keep one real runtime execution scope alive, then hold fake connection disposal.
        // Shutdown must await both phases and cannot release ownership after a bounded wait.
        var inFlight = owned.Enter();
        rpc.HoldDisposal = true;
        Task? stopping = null;
        try
        {
            stopping = f.Runtime.StopSessionAsync();
            await Task.Delay(80);
            check("host stop waits for actual owned execution drain", !stopping.IsCompleted && !rpc.DisposeEntered.Task.IsCompleted);
            inFlight.Dispose();
            await rpc.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check("host stop also waits for owned connection disposal", !stopping.IsCompleted);
        }
        finally
        {
            inFlight.Dispose();
            rpc.ReleaseDisposal.TrySetResult();
            if (stopping != null) await stopping.WaitAsync(TimeSpan.FromSeconds(8));
        }
        check("fully drained stop revokes owned runtime and preserves durable ledger",
            f.Runtime.Identity == null && f.Runtime.OwnedRuntime == null && rpc.Disposed
            && (await f.Ledger.ReadConversationAsync(identity)).Operations.Any(o => o.Id == created.Id));
    }

    internal static ConfiguredOwnedProfile Profile(string directory, bool verified)
    {
        var configuration = new OwnedServerConfiguration
        {
            Connection = new(new("local", "synthetic-owned-host-profile"), OwnedBackend.LocalStdio, true),
            ExpectedVersion = OwnedVerification.SchemaVersion, ExpectedPlatformOs = "windows",
            ExpectedCodexHome = Path.Combine(directory, "unused-home"),
            Projects = ImmutableDictionary<string, string>.Empty.Add("sample", Path.Combine(directory, "unused-project")),
            Models = ImmutableHashSet.Create("synthetic-model"), RequestTimeout = TimeSpan.FromSeconds(3)
        };
        if (verified) configuration = configuration with
        {
            Verification = new(configuration.Connection, configuration.ExpectedVersion, configuration.ExpectedPlatformOs,
                OwnedVerification.ConfigurationDigest(configuration), OwnedVerification.SupportedMethods.ToImmutableHashSet(),
                VerificationKind.SyntheticReplay, "synthetic-owned-host-test-only", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1))
        };
        return new("demo", "合成测试项目", configuration,
            new("sample", configuration.Projects["sample"], "synthetic-model", "on-request", "workspace-write"),
            Mode: OwnedProfileMode.VerifiedControl);
    }

    private static async Task SharedWorkspaceLifecycleAsync(string directory, Action<string, bool> check, bool globalLimit = false)
    {
        string root = Path.Combine(directory, globalLimit ? "global-capacity-host" : "shared-workspace-host");
        var profile = Profile(root, verified: true);
        Directory.CreateDirectory(profile.Creation.Cwd);
        var workspace = new WorkspaceWriteCoordinator(recoverySources: ["legacy", "owned"], maxConcurrentExecutions: globalLimit ? 1 : null);
        var transport = new WorkspaceTransport();
        FakeRpc? rpc = null;
        var options = new OwnedHostOptions(Path.Combine(root, "owned"), [profile], (configured, _) =>
        {
            rpc = new FakeRpc(configured.Configuration, () => Task.FromResult(true));
            return Task.FromResult<IJsonRpcConnection>(rpc);
        }, Workspace: workspace);
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(root, ownedOptions: options, taskTransport: transport);
        if (globalLimit)
        {
            check("existing settings preserve unlimited default", f.Settings.MaxConcurrentExecutions == null);
            var restoredSettings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(f.Settings, Settings.Json), Settings.Json)!;
            restoredSettings.MaxConcurrentExecutions = 2;
            restoredSettings.Normalize();
            var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(restoredSettings, Settings.Json), Settings.Json)!;
            check("explicit concurrency setting survives config round trip", roundTrip.MaxConcurrentExecutions == 2);
            bool invalid = false;
            restoredSettings.MaxConcurrentExecutions = 0;
            try { restoredSettings.Normalize(); } catch (InvalidDataException) { invalid = true; }
            check("invalid saved concurrency limit fails closed rather than becoming unlimited", invalid);
        }
        f.Settings.ChatGpt.TaskControlEnabled = true;
        f.ObservedTask.Cwd = globalLimit ? Path.Combine(root, "separate-legacy-project") : profile.Creation.Cwd;
        Directory.CreateDirectory(f.ObservedTask.Cwd);
        workspace.RefreshExternalEvidence = () => f.Queue.RefreshWorkspace(f.CurrentSnapshot());
        check("actual shared host completes both recovery sources without connecting", workspace.Ready && rpc == null);
        await SendToolAsync(f, "workspace-create", "新建 demo", _ => Call("create_task", new { configurationId = "demo" }));
        var owned = f.Runtime.OwnedRuntime!;
        var task = owned.Tasks().Single();
        check("actual shared host empty creation remains live idle and releases its reservation",
            task.Snapshot.State == OwnedTaskState.Idle && workspace.Blockers().Count == 0);
        string text = "继续 " + task.Handle.Identity.ThreadId + " 测试目录协调";
        await SendToolAsync(f, "workspace-start", text, _ => ContinueCall(owned.Tasks().Single(), text));
        string turn = owned.Tasks().Single().Snapshot.CurrentTurnId!;
        await f.Queue.SubmitAsync(f.ObservedTask, "synthetic legacy work", "workspace-legacy-queued", f.CurrentSnapshot(), default);
        check(globalLimit ? "legacy queue waits on global capacity while its own directory is free" : "actual legacy queue cannot dispatch into an accepted owned turn in the same directory",
            transport.Calls == 0 && f.Queue.Entries.Single().State == "queued" && workspace.Blockers().Count == 1
            && (!globalLimit || f.Queue.Entries.Single().Detail.Contains("额度已满")));
        rpc!.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(turn, "completed") });
        await WaitAsync(() => Task.FromResult(owned.Tasks().Single().Snapshot.State == OwnedTaskState.Completed && workspace.Blockers().Count == 0));
        await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        check("actual legacy queue dispatches only after the owned turn is observed terminal",
            transport.Calls == 1 && f.Queue.Entries.Single().State == "submitted" && workspace.Blockers().Count == 1);
        int starts = rpc.Methods.Count(m => m == "turn/start");
        await SendToolAsync(f, "workspace-blocked-owned", text, _ => ContinueCall(owned.Tasks().Single(), text));
        check("actual owned tool path cannot bypass the legacy accepted resource claim",
            rpc.Methods.Count(m => m == "turn/start") == starts && workspace.Blockers().Count == 1);
        f.ObservedTask.TurnId = transport.Next.TurnId;
        f.Queue.RefreshWorkspace(f.CurrentSnapshot());
        check("exact legacy terminal evidence is persisted before releasing the shared directory",
            f.Queue.Entries.Single().WorkspaceTerminalObservedAt != null && workspace.Blockers().Count == 0);
        string terminalCopy = Path.Combine(root, "verified-terminal-copy.state");
        File.Copy(Path.Combine(root, "synthetic-queue.state"), terminalCopy);
        var recoveredTerminal = new WorkspaceWriteCoordinator(recoverySources: ["legacy"]);
        await using (var recoveredQueue = new TaskControlQueue(f.Settings, transport, terminalCopy, workspace: recoveredTerminal))
            check("legacy restart does not resurrect a durably observed terminal claim",
                recoveredTerminal.Ready && recoveredTerminal.Blockers().Count == 0);
        await SendToolAsync(f, "workspace-resume-owned", text, _ => ContinueCall(owned.Tasks().Single(), text));
        turn = owned.Tasks().Single().Snapshot.CurrentTurnId!;
        check("actual owned continuation resumes after exact legacy terminal evidence",
            rpc.Methods.Count(m => m == "turn/start") == starts + 1 && workspace.Blockers().Count == 1);
        rpc.Emit("turn/completed", new { threadId = task.Handle.Identity.ThreadId, turn = FakeRpc.Turn(turn, "completed") });
        await WaitAsync(() => Task.FromResult(owned.Tasks().Single().Snapshot.State == OwnedTaskState.Completed && workspace.Blockers().Count == 0));
        transport.Next = new("unconfirmed", "synthetic lost acceptance");
        await f.Queue.SubmitAsync(f.ObservedTask, "synthetic uncertain legacy work", "workspace-legacy-unknown", f.CurrentSnapshot(), default);
        check("actual legacy unknown blocks owned start without presenting it as running",
            transport.Calls == 2 && workspace.Blockers().Single().State == "Unknown"
            && !owned.WorkingDirectoryAvailable(owned.Tasks().Single()));
        f.Queue.SetOwner("another-synthetic-owner");
        f.ObservedTasks = [];
        f.Queue.RefreshWorkspace(f.CurrentSnapshot());
        check("binding switch and missing collector tasks do not clear unknown workspace evidence",
            workspace.Blockers().Count == 1 && f.Queue.Entries.Count == 0);
        string unknownCopy = Path.Combine(root, "unknown-history-copy.state");
        File.Copy(Path.Combine(root, "synthetic-queue.state"), unknownCopy);
        var recoveredUnknown = new WorkspaceWriteCoordinator(recoverySources: ["legacy"], maxConcurrentExecutions: globalLimit ? 1 : null);
        await using (var recoveredQueue = new TaskControlQueue(f.Settings, transport, unknownCopy, workspace: recoveredUnknown))
            check("legacy restart restores unresolved dispatch from the prior owner history",
                recoveredUnknown.Ready && recoveredUnknown.Blockers().Single().State == "Unknown"
                && (!globalLimit || recoveredUnknown.Snapshot() is { Occupied: 1, Unknown: 1 }
                    && recoveredUnknown.CheckStart(null, new("local", "windows", profile.Creation.Cwd)).Code == "workspace_global_capacity"));
    }

    private sealed class WorkspaceTransport : ITaskTransport
    {
        internal int Calls;
        internal TaskDelivery Next = new("submitted", "synthetic legacy acceptance", "workspace-legacy-turn");
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); return Task.FromResult(Next); }
    }
    internal static async Task SendToolAsync(DurableRuntimeTests.Fixture f, string sourceId, string body,
        Func<ToolModelRequest, ModelToolCall> call)
    {
        f.Script.Handler = (request, _) => Task.FromResult(request.Observations.IsEmpty
            ? ToolModelResponse.Call(call(request)) : ToolModelResponse.Reply("模拟工具处理已记录。"));
        await f.InputAsync(sourceId, body);
        await WaitReplyAsync(f, sourceId);
    }
    internal static Task WaitReplyAsync(DurableRuntimeTests.Fixture f, string sourceId) => WaitAsync(async () =>
    {
        var state = await f.ReadAsync();
        var message = state.Inbox.SingleOrDefault(i => i.SourceMessageId == WeChatText.Hash(sourceId));
        if (message?.GenerationId == null) return false;
        var outbox = state.Outbox.Where(o => o.GenerationId == message.GenerationId).ToArray();
        return outbox.Length > 0 && outbox.All(o => o.Stage == OutboxStage.Accepted);
    });
    private static Task WaitNoticeAsync(DurableRuntimeTests.Fixture f, string turn, string text) => WaitAsync(async () =>
    {
        var state = await f.ReadAsync();
        var notices = state.Events.Where(e => e.Task?.TurnId == turn
            && e.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true && e.Body.Contains(text, StringComparison.Ordinal)).ToArray();
        return notices.Length > 0 && notices.All(e =>
        {
            var outbox = state.Outbox.Where(o => o.NotificationEventId == e.Id).ToArray();
            return outbox.Length > 0 && outbox.All(o => o.Stage == OutboxStage.Accepted);
        });
    });
    private static int NoticeCount(ConversationSnapshot state, string turn, string text)
        => state.Events.Count(e => e.Task?.TurnId == turn && e.ExternalEventKey?.StartsWith("owned-event/", StringComparison.Ordinal) == true
            && e.Body.Contains(text, StringComparison.Ordinal));
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic owned host condition not reached.");
            await Task.Delay(20);
        }
    }
    internal static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    internal static ModelToolCall ContinueCall(OwnedRuntimeTask task, string text)
        => WriteCall("continue_owned_task", task, new { text });
    private static ModelToolCall ApprovalCall(OwnedRuntimeTask task, PendingApproval approval)
        => WriteCall("respond_approval", task, new { turnId = approval.TurnId, requestToken = approval.Token, decision = "accept" });
    private static ModelToolCall WriteCall(string name, OwnedRuntimeTask task, object extras)
    {
        var properties = new Dictionary<string, object>
        {
            ["configurationId"] = task.ConfigurationId, ["threadId"] = task.Handle.Identity.ThreadId, ["revision"] = task.Snapshot.Revision
        };
        foreach (var extra in JsonSerializer.SerializeToElement(extras).EnumerateObject()) properties.Add(extra.Name, extra.Value.Clone());
        return Call(name, properties);
    }

    internal sealed class FakeRpc(OwnedServerConfiguration configuration, Func<Task<bool>> dispatch) : IJsonRpcConnection
    {
        public ConnectionIdentity Identity => configuration.Connection;
        public string? ProjectSourceIdentity { get; set; }
        public bool IsConnected { get; private set; } = true;
        public event Action<RpcIncoming>? Incoming;
        public event Action? Disconnected;
        internal readonly ConcurrentQueue<string> Methods = new();
        private readonly ConcurrentDictionary<string, JsonElement> threads = new();
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DispatchSeenAtCreate, HoldDisposal, Disposed;
        internal int Responses;
        public bool SupportsGuardedRequests => true;
        internal Func<Task>? BeforeGuard;
        internal bool GuardedReplyLost;
        internal readonly ConcurrentQueue<string> GuardedTexts = new();
        public async Task<JsonElement> RequestGuardedAsync(string method, object parameters,
            Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken ct)
        {
            if (BeforeGuard != null) await BeforeGuard();
            if (!await beforeWrite(ct)) throw new RpcTransportException(false, "request_guard_rejected");
            ct.ThrowIfCancellationRequested();
            var result = await RequestAsync(method, parameters, ct);
            var args = JsonSerializer.SerializeToElement(parameters);
            if (method == "turn/start")
            {
                string id = args.GetProperty("threadId").GetString()!;
                GuardedTexts.Enqueue(args.GetProperty("input")[0].GetProperty("text").GetString()!);
                SetThread(id, threads[id].GetProperty("cwd").GetString()!, result.GetProperty("turn"));
            }
            if (GuardedReplyLost) throw new RpcTransportException(true, "synthetic_ack_lost");
            return result;
        }
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Methods.Enqueue(method);
            var args = JsonSerializer.SerializeToElement(parameters);
            if (method == "initialize") return JsonSerializer.SerializeToElement(new
            { userAgent = "codex_cli_rs/" + configuration.ExpectedVersion, platformOs = configuration.ExpectedPlatformOs,
                platformFamily = configuration.ExpectedPlatformOs == "windows" ? "windows" : "unix", codexHome = configuration.ExpectedCodexHome });
            if (method == "thread/start")
            {
                DispatchSeenAtCreate = await dispatch();
                string id = Guid.NewGuid().ToString("D"), cwd = args.GetProperty("cwd").GetString()!;
                threads[id] = JsonSerializer.SerializeToElement(new { id, cwd, turns = Array.Empty<object>() });
                return JsonSerializer.SerializeToElement(new { thread = threads[id], cwd, model = args.GetProperty("model").GetString(),
                    approvalPolicy = args.GetProperty("approvalPolicy").GetString(), approvalsReviewer = "user",
                    sandbox = new { type = "workspaceWrite", networkAccess = false } });
            }
            if (method == "turn/start") return JsonSerializer.SerializeToElement(new { turn = Turn(Guid.NewGuid().ToString("D"), "inProgress") });
            if (method == "turn/steer") return JsonSerializer.SerializeToElement(new { turnId = args.GetProperty("expectedTurnId").GetString() });
            if (method is "thread/read" or "thread/unarchive") return JsonSerializer.SerializeToElement(new { thread = threads[args.GetProperty("threadId").GetString()!] });
            if (method is "turn/interrupt" or "thread/name/set" or "thread/archive") return JsonSerializer.SerializeToElement(new { });
            throw new InvalidOperationException("Unexpected fake owned RPC method: " + method);
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Methods.Enqueue(method); return Task.CompletedTask; }
        public Task RespondAsync(JsonElement requestId, object result, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Responses); return Task.CompletedTask; }
        internal void Emit(string method, object parameters, object? requestId = null) => Incoming?.Invoke(new(method,
            JsonSerializer.SerializeToElement(parameters), requestId == null ? null : JsonSerializer.SerializeToElement(requestId)));
        internal void SetThread(string id, string cwd, params JsonElement[] turns)
            => threads[id] = JsonSerializer.SerializeToElement(new { id, cwd, turns });
        internal static JsonElement Turn(string id, string status) => JsonSerializer.SerializeToElement(new { id, status, items = Array.Empty<object>() });
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            if (HoldDisposal) await ReleaseDisposal.Task;
            IsConnected = false; Disposed = true; Disconnected?.Invoke();
        }
    }
}
