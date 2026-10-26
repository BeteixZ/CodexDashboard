using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class TaskControlDiagnostics
{
    private sealed class NoTaskWrites : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("只读查询诊断禁止投递工作指令。"); }
    }
    internal static async Task ProgressLiveAsync(string directory, string host, string threadId)
    {
        Directory.CreateDirectory(directory); string error = "", reply = "", title = "", model = ""; bool selectedFromList = false, sent = false;
        var guard = new NoTaskWrites(); int pending = 0;
        try
        {
            var settings = Settings.Load();
            if (!settings.RemoteHosts.Contains(host) || !Guid.TryParse(threadId, out _)) throw new InvalidOperationException("目标无效。");
            settings.LocalEnabled = false; settings.RemoteHosts = new() { host };
            using var vm = new AssistantTaskMonitor(settings);
            using var assistant = new ChatGptAssistant(settings, () => { });
            var account = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new InvalidOperationException("只读诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
            model = settings.ChatGpt.Model;
            await vm.RefreshAsync();
            var snapshot = new WeChatSnapshot(vm.AllTasks, vm.LastRefreshAt, vm.Hosts);
            var task = snapshot.Tasks.Single(t => t.Id == threadId && t.Host == host); title = task.Title;
            var queue = new TaskControlQueue(settings, guard, null);
            var router = new TaskControlAssistant(settings, assistant, assistant, queue); router.SetOwner("isolated-progress-diagnostic");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            reply = await router.ReplyMessageAsync("汇报 " + host + " 上“" + title + "”的当前进展和已有测试结果，不要改代码", snapshot, Guid.NewGuid().ToString(), timeout.Token);
            if (reply.Contains("要查看哪个目标原任务"))
            {
                var choice = Regex.Match(reply, @"(?m)^([1-8])\. " + Regex.Escape(WeChatText.Clean(title, 70)) + " · ");
                if (!choice.Success) throw new IOException("指定只读目标不在候选项中。");
                selectedFromList = true;
                reply = await router.ReplyMessageAsync(choice.Groups[1].Value, snapshot, Guid.NewGuid().ToString(), timeout.Token);
            }
            pending = queue.Entries.Count;
            if (!reply.StartsWith("任务进展 · 只读", StringComparison.Ordinal) || guard.Calls != 0 || pending != 0) throw new IOException("请求没有完成只读进展路径。");
            await using var wechat = new WeChatService(settings, () => snapshot, () => { }, receiveMessages: false);
            await wechat.ConfigureAsync();
            await wechat.SendControlReceiptAsync(new(WeChatText.Hash("progress-test-" + Guid.NewGuid()), "[测试修复] 运行中任务只读汇报\n" + reply), timeout.Token); sent = true;
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = error.Length == 0 && guard.Calls == 0 && pending == 0 && reply.StartsWith("任务进展 · 只读", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(directory, "progress-live.json"), JsonSerializer.Serialize(new { passed, host, threadId, title, model,
            selectedFromList, taskDispatches = guard.Calls, queuedWork = pending, wechatAttempted = sent, phoneDeliveryVerified = false, reply, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("只读任务进展诊断失败。");
    }
    // Explicit real generation test in an isolated diagnostic session, archived on exit.
    internal static async Task RemoteLiveAsync(string directory, string host)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>(); string error = "", threadId = "", turnId = "";
        RemoteAppServer? connection = null;
        try
        {
            var settings = Settings.Load();
            if (!settings.RemoteHosts.Contains(host)) throw new InvalidOperationException("主机未配置。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var server = connection = await RemoteAppServer.ConnectAsync(host, timeout.Token);
            var finished = new ConcurrentDictionary<string, JsonElement>();
            var answers = new ConcurrentDictionary<string, string>();
            var commands = new ConcurrentDictionary<string, bool>();
            void Observe(string method, JsonElement p) {
                if (method == "turn/completed" && p.TryGetProperty("turn", out var t)) finished[DesktopIpc.Text(t, "id")] = t.Clone();
                if (method == "item/completed")
                {
                    var item = p.GetProperty("item");
                    if (DesktopIpc.Text(item, "type") == "agentMessage") answers[DesktopIpc.Text(p, "turnId")] = DesktopIpc.Text(item, "text");
                    if (DesktopIpc.Text(item, "type") == "commandExecution") commands[DesktopIpc.Text(p, "turnId")] = item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() == 0;
                }
            }
            server.Notification += Observe;
            var started = await server.RequestAsync("thread/start", new { cwd = "/tmp", sandbox = "read-only", approvalPolicy = "on-request" }, timeout.Token);
            threadId = DesktopIpc.Text(started.GetProperty("thread"), "id");
            checks["isolated same-daemon session created"] = Guid.TryParse(threadId, out _);
            await server.RequestAsync("thread/name/set", new { threadId, name = "[测试] 微信远程接续验证（自动归档）" }, timeout.Token);
            string nonce = "WX" + Guid.NewGuid().ToString("N")[..12];
            var first = await server.RequestAsync("turn/start", RemoteTaskTransport.TurnRequest(threadId,
                "记住本轮口令 " + nonce + "。只回复已记住，不调用工具，不读写文件。", "diagnostic-" + Guid.NewGuid().ToString("N")), timeout.Token);
            string firstId = DesktopIpc.Text(first.GetProperty("turn"), "id");
            async Task WaitTurn(string id)
            {
                while (!finished.ContainsKey(id)) await Task.Delay(200, timeout.Token);
                if (DesktopIpc.Text(finished[id], "status") != "completed") throw new IOException("测试轮次未完成：" + DesktopIpc.Text(finished[id], "status") + " " + (finished[id].TryGetProperty("error", out var e) ? DesktopIpc.Text(e, "message") : ""));
            }
            await WaitTurn(firstId); checks["initial generation completed"] = true;
            await server.RequestAsync("thread/archive", new { threadId }, timeout.Token);
            await server.RequestAsync("thread/unarchive", new { threadId }, timeout.Token);
            var cold = await server.RequestAsync("thread/read", new { threadId, includeTurns = false }, timeout.Token);
            checks["stored session unloaded before followup"] = DesktopIpc.Text(cold.GetProperty("thread").GetProperty("status"), "type") == "notLoaded";
            // Use a separate real SSH client, just as the deployed bridge does.
            await using var transport = new RemoteTaskTransport(settings, async (alias, ct) => {
                var channel = await RemoteAppServer.ConnectAsync(alias, ct); channel.Notification += Observe; return channel;
            });
            var delivery = await transport.DeliverAsync(new TaskRecord { Id = threadId, Host = host, Cwd = "/tmp" },
                "只回复刚才记住的口令，不调用工具，不读写文件。", "diagnostic-" + Guid.NewGuid().ToString("N"), timeout.Token);
            turnId = delivery.TurnId; checks["followup submitted to original thread"] = delivery.State == "submitted";
            if (delivery.State != "submitted") throw new IOException(delivery.Detail);
            await WaitTurn(turnId); checks["followup generation completed"] = true;
            checks["original conversation context retained"] = answers.TryGetValue(turnId, out string? answer) && answer.Trim() == nonce;
            var resumed = await server.RequestAsync("thread/resume", RemoteTaskTransport.ResumeRequest(threadId), timeout.Token);
            foreach (string field in new[] { "model", "modelProvider", "cwd", "approvalPolicy", "approvalsReviewer", "sandbox" })
                checks[field + " preserved"] = started.TryGetProperty(field, out var before) && resumed.TryGetProperty(field, out var after) && JsonElement.DeepEquals(before, after);
            var execution = await transport.DeliverAsync(new TaskRecord { Id = threadId, Host = host, Cwd = "/tmp" },
                "请实际调用终端工具运行 pwd，只回复输出的目录路径。不要读写文件，不要申请额外权限。", "diagnostic-" + Guid.NewGuid().ToString("N"), timeout.Token);
            if (execution.State != "submitted") throw new IOException(execution.Detail);
            await WaitTurn(execution.TurnId);
            checks["remote builtin command completes under original sandbox"] = commands.TryGetValue(execution.TurnId, out bool commandOk) && commandOk;
            checks["remote command uses original cwd"] = answers.TryGetValue(execution.TurnId, out string? pathAnswer) && pathAnswer.Contains("/tmp", StringComparison.Ordinal);
            await server.RequestAsync("thread/unsubscribe", new { threadId }, timeout.Token);
            if (checks.ContainsValue(false)) throw new IOException("远程真实接续检查未全部通过。");
        }
        catch (Exception ex) { error = ex is RemoteRpcException rpc ? rpc.Message + ": " + rpc.Diagnostic : ex.Message; }
        finally
        {
            if (connection != null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (Guid.TryParse(threadId, out _)) await connection.RequestAsync("thread/archive", new { threadId }, cleanup.Token);
                    checks["diagnostic session archived"] = true;
                }
                catch { checks["diagnostic session archived"] = false; }
                await connection.DisposeAsync();
            }
        }
        bool passed = checks.Count > 0 && !checks.ContainsValue(false) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "remote-live.json"), JsonSerializer.Serialize(new { host, passed, threadId, turnId, checks, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("远程真实接续诊断失败。");
    }
    internal static async Task RemoteProbeAsync(string directory, string host, string threadId)
    {
        Directory.CreateDirectory(directory);
        bool connected = false; string error = ""; object? detail = null;
        try
        {
            var settings = Settings.Load();
            if (!settings.RemoteHosts.Contains(host)) throw new InvalidOperationException("主机未配置。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var server = await RemoteAppServer.ConnectAsync(host, timeout.Token); connected = true;
            var loaded = await server.RequestAsync("thread/loaded/list", new { }, timeout.Token);
            var list = await server.RequestAsync("thread/list", new { limit = 6 }, timeout.Token);
            var threads = new List<object>();
            if (list.TryGetProperty("data", out var data)) foreach (var t in data.EnumerateArray())
                threads.Add(new { id = DesktopIpc.Text(t, "id"), title = DesktopIpc.Text(t, "name"), status = t.TryGetProperty("status", out var status) ? status.Clone() : default });
            JsonElement taskStatus = default;
            if (Guid.TryParse(threadId, out _))
            {
                var read = await server.RequestAsync("thread/read", new { threadId, includeTurns = false }, timeout.Token);
                if (read.TryGetProperty("thread", out var thread) && thread.TryGetProperty("status", out var status)) taskStatus = status.Clone();
            }
            detail = new { loaded, threads, taskStatus = taskStatus.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : taskStatus };
        }
        catch (Exception ex) { error = ex.Message; }
        await File.WriteAllTextAsync(Path.Combine(directory, "remote-probe.json"), JsonSerializer.Serialize(new { host, connected, detail, error }, Settings.Json));
        if (!connected || error.Length > 0) throw new InvalidOperationException("远程只读探测失败。");
    }
    // Read-only desktop probe: does not resume, submit, or generate anything.
    internal static async Task ProbeAsync(string directory, string threadId, string host)
    {
        Directory.CreateDirectory(directory);
        bool initialized = false, ownerFound = false; string state = "", error = "";
        try
        {
            if (!Guid.TryParse(threadId, out _) || host != "本机" && !Settings.ValidAlias(host)) throw new InvalidOperationException("无效任务或主机。");
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var ipc = await DesktopIpc.ConnectAsync(ct.Token); initialized = true;
            string hostId = DesktopTaskTransport.HostId(host);
            string? owner = await ipc.FindOwnerAsync(hostId, threadId, ct.Token); ownerFound = owner != null;
            if (owner != null) state = await ipc.ReadStateAsync(hostId, threadId, owner, ct.Token);
        }
        catch (Exception ex) { error = ex.Message; }
        await File.WriteAllTextAsync(Path.Combine(directory, "probe.json"), JsonSerializer.Serialize(new { initialized, ownerFound, state, error }, Settings.Json));
        if (!initialized || error.Length > 0) throw new InvalidOperationException("桌面只读探测失败。");
    }
    // Real selected ChatGPT model; all samples are synthetic and no task is sent.
    internal static async Task ModelAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>(); bool passed = true;
        string model = "", error = "";
        try
        {
            var settings = Settings.Load();
            using var assistant = new ChatGptAssistant(settings, () => { });
            var account = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new InvalidOperationException("诊断需要有效期至少 5 分钟的现有授权，未刷新凭据。");
            model = settings.ChatGpt.Model;
            using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            string context = JsonSerializer.Serialize(new { currentTask = new { title = "修复 scipy", project = "scipy", host = "本机" },
                tasks = new[] { new { id = "abc12345", title = "修复 scipy", project = "scipy", host = "本机" }, new { id = "bcd23456", title = "验证 PDE", project = "PCA_TR", host = "host-a" } } });
            foreach (var sample in new[] {
                (text: "继续测试，把失败项修好", expected: "continue", target: ""),
                (text: "今天完成了哪些任务？", expected: "chat", target: ""),
                (text: "把 scipy 那个任务的测试补全", expected: "continue", target: "scipy"),
                (text: "能不能继续测试？", expected: "chat", target: ""),
                (text: "让 host-a 上的 PDE 任务先解释测试结果，不要改代码", expected: "inspect", target: "PDE|host-a"),
                (text: "汇报 P0 当前进展", expected: "inspect", target: "P0"),
                (text: "检查代码并分析失败原因", expected: "continue", target: ""),
                (text: "重新运行测试然后汇报结果", expected: "continue", target: "") })
            {
                var intent = await assistant.InterpretAsync(sample.text, context, ct.Token);
                bool ok = intent.Intent == sample.expected && (sample.target.Length == 0 || sample.target.Split('|').Any(t => intent.TargetQuery.Equals(t, StringComparison.OrdinalIgnoreCase)));
                passed &= ok; results.Add(new { sample.text, expected = sample.expected, intent, ok });
            }
        }
        catch (Exception ex) { passed = false; error = ChatGptAssistant.Explain(ex); }
        await File.WriteAllTextAsync(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(new { model, passed, results, error, taskDispatches = 0 }, Settings.Json));
        if (!passed) throw new InvalidOperationException("自然语言实际模型诊断失败。");
    }
}
