using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class TaskControlTests
{
    private sealed class Model : IWeChatAssistant, ITaskIntentInterpreter, ITaskProgressReader
    {
        public bool Enabled => true;
        public TaskIntent Next = new("continue", "", "current");
        public int Calls, Chats;
        public List<(string Key, string Question)> Inspected = new();
        public Task<TaskIntent> InterpretAsync(string text, string context, CancellationToken ct) { Calls++; return Task.FromResult(Next); }
        public Task<string> ReplyAsync(string text, WeChatSnapshot snapshot, CancellationToken ct) { Chats++; return Task.FromResult("问答：" + text); }
        public Task<string> ReadProgressAsync(string text, TaskRecord task, WeChatSnapshot snapshot, CancellationToken ct)
        { Inspected.Add((task.Key, text)); return Task.FromResult("只读进展：" + task.Title); }
    }
    private sealed class Transport : ITaskTransport
    {
        public List<(string Key, string Text, string Id)> Sent = new();
        public TaskDelivery Next = new("submitted", "confirmed", "turn-next");
        public Func<CancellationToken, Task>? Before;
        public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct)
        { Sent.Add((task.Key, text, id)); if (Before != null) await Before(ct); return Next; }
    }
    internal static async Task RunAndSaveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool ok) { checks.Add(new { name, ok }); if (!ok) throw new InvalidOperationException(name); }
        Settings settings = new() { ChatGpt = new() { Enabled = true, TaskControlEnabled = true }, WeChat = new() { ClawBotEnabled = true } };
        TaskRecord a = new() { Id = Guid.NewGuid().ToString(), Host = "本机", Title = "修复 scipy", Project = "scipy", Status = TaskStatusNames.Completed, LastActivity = 10 };
        TaskRecord b = new() { Id = Guid.NewGuid().ToString(), Host = "host-a", Title = "验证 PDE", Project = "PCA_TR", Status = TaskStatusNames.Completed, LastActivity = 9 };
        TaskRecord a2 = new() { Id = Guid.NewGuid().ToString(), Host = "本机", Title = "另一个 scipy 任务", Project = "scipy", Status = TaskStatusNames.Completed, LastActivity = 8 };
        WeChatSnapshot Snap(params TaskRecord[] tasks) => new(tasks, DateTimeOffset.UtcNow, tasks.Select(t => t.Host).Distinct().Select(h => new HostSnapshot { Name = h, LastSuccess = DateTimeOffset.UtcNow }).ToArray());
        var snapshot = Snap(a, b, a2); var model = new Model(); var transport = new Transport();
        var queue = new TaskControlQueue(settings, transport, null); var router = new TaskControlAssistant(settings, model, model, queue); router.SetOwner("owner");
        try
        {
            string original = "继续测试，把失败项修好；先解释原因，别改其他文件。";
            string reply = await router.ReplyMessageAsync(original, snapshot, "m1", default);
            Check("ambiguous instruction asks without dispatch", reply.Contains("尚未投递") && transport.Sent.Count == 0);
            reply = await router.ReplyMessageAsync("我选第二个", snapshot, "m2", default);
            Check("natural ordinal dispatches original text to chosen host", transport.Sent.Single().Key == b.Key && transport.Sent[0].Text == original && reply.Contains("已投递到原任务"));
            await router.ReplyMessageAsync("补充边界测试", snapshot, "m3", default);
            Check("natural followup keeps selected thread", transport.Sent.Last().Key == b.Key && transport.Sent.Last().Text == "补充边界测试");
            int sent = transport.Sent.Count;
            await router.ReplyMessageAsync("补充边界测试", snapshot, "m3", default);
            Check("message id deduplicates dispatch", transport.Sent.Count == sent);
            model.Next = new("chat", "", "unspecified");
            reply = await router.ReplyMessageAsync("能不能继续测试？", snapshot, "m4", default);
            Check("capability question remains chat", reply.StartsWith("问答：") && transport.Sent.Count == sent);
            model.Next = new("continue", "scipy", "unspecified");
            reply = await router.ReplyMessageAsync("修复 scipy 的测试", snapshot, "m5", default);
            Check("same project multiple tasks asks selection", reply.Contains("尚未投递") && transport.Sent.Count == sent);
            model.Next = new("select", "scipy", "unspecified");
            reply = await router.ReplyMessageAsync("选 scipy 那个", snapshot, "m5n", default);
            Check("narrowing ambiguous choice retains original instruction", reply.Contains("尚未投递") && transport.Sent.Count == sent);
            await router.ReplyMessageAsync("1", snapshot, "m6", default);
            Check("selection id not sent as instruction", transport.Sent.Last().Key == a.Key && transport.Sent.Last().Text == "修复 scipy 的测试");
            model.Next = new("continue", "PCA_TR", "unspecified");
            await router.ReplyMessageAsync("继续干活", snapshot, "m7", default);
            Check("hallucinated target evidence cannot dispatch", transport.Sent.Count == sent + 1);
            model.Next = new("continue", "", "current");
            router.NoteNotification(b.Key);
            reply = await router.ReplyMessageAsync("继续做", snapshot, "m8", default);
            Check("new notification conflicting with selected task asks", reply.Contains("尚未投递") && transport.Sent.Count == sent + 1);
            model.Next = new("cancel", "", "unspecified");
            await router.ReplyMessageAsync("取消刚才的指令", snapshot, "m9", default);
            model.Next = new("continue", "", "notification");
            router.Reset(); router.NoteNotification(a.Key);
            await router.ReplyMessageAsync("刚才那条报告，继续测试", snapshot, "m10", default);
            Check("single recent notification maps original task", transport.Sent.Last().Key == a.Key);
            router.NoteNotification(b.Key);
            sent = transport.Sent.Count;
            reply = await router.ReplyMessageAsync("刚才那些报告继续做", snapshot, "m11", default);
            Check("multiple notification targets remain ambiguous", reply.Contains("尚未投递") && transport.Sent.Count == sent);
            router.Reset(); model.Next = new("continue", "PCA_TR", "unspecified"); b.Status = TaskStatusNames.Active;
            reply = await router.ReplyMessageAsync("PCA_TR 继续测试", snapshot, "m12", default);
            Check("active task queues without interruption", reply.Contains("已排队") && transport.Sent.Count == sent);
            b.Status = TaskStatusNames.Completed;
            int notified = 0; queue.Notify = (_, _) => { notified++; return Task.CompletedTask; };
            await router.PumpAsync(snapshot, default);
            Check("queued instruction starts after task finishes", transport.Sent.Count == sent + 1 && notified == 1);
            await router.PumpAsync(snapshot, default);
            Check("completed delivery not replayed by pump", transport.Sent.Count == sent + 1 && notified == 1);
            var stale = new WeChatSnapshot(new[] { b }, DateTimeOffset.UtcNow.AddMinutes(-5), snapshot.Hosts);
            await router.ReplyMessageAsync("PCA_TR 再次测试", stale, "m13", default);
            Check("stale metadata blocks dispatch", transport.Sent.Count == sent + 1);
            await router.ReplyMessageAsync("取消接续", snapshot, "m14", default);
            await router.PumpAsync(snapshot, default);
            Check("cancelled instruction stays cancelled", transport.Sent.Count == sent + 1);
            b.Disconnected = true;
            await router.ReplyMessageAsync("PCA_TR 运行验证", snapshot, "m15", default);
            Check("offline host blocks dispatch", transport.Sent.Count == sent + 1);
            await router.ReplyMessageAsync("取消接续", snapshot, "m16", default); b.Disconnected = false;
            transport.Next = new("unconfirmed", "unknown");
            await router.ReplyMessageAsync("PCA_TR 检查结果", snapshot, "m17", default);
            sent = transport.Sent.Count;
            await router.PumpAsync(snapshot, default);
            Check("uncertain delivery never auto retries", transport.Sent.Count == sent && queue.Entries.Last().State == "unconfirmed");
            Check("terminal journal erases instruction text", queue.Entries.Where(e => e.State != "queued").All(e => e.Instruction.Length == 0));
            transport.Next = new("submitted", "ok");
            settings.ChatGpt.TaskControlEnabled = false;
            reply = await router.ReplyMessageAsync("继续工作", snapshot, "m18", default);
            Check("disabled control cannot dispatch", reply.StartsWith("问答：") && transport.Sent.Count == sent);
            settings.ChatGpt.TaskControlEnabled = true;
            router.SetOwner("new-owner");
            Check("binding change clears selection and queue", router.Selection.Length == 0 && queue.Entries.Count == 0);
            model.Next = new("unsupported", "", "current");
            reply = await router.ReplyMessageAsync("批准所有权限", snapshot, "m19", default);
            Check("approvals are never synthesized", reply.Contains("审批请在 Codex") && transport.Sent.Count == sent);
            string payload = JsonSerializer.Serialize(DesktopTaskTransport.TurnRequest(a.Id, original, "stable-id"));
            using var parsed = JsonDocument.Parse(payload);
            var request = parsed.RootElement.GetProperty("turnStart").GetProperty("request");
            Check("transport preserves exact original text", request.GetProperty("input")[0].GetProperty("text").GetString() == original);
            Check("transport targets same thread with stable message identity", request.GetProperty("threadId").GetString() == a.Id && request.GetProperty("clientUserMessageId").GetString() == "stable-id");
            Check("transport does not override permissions or model", !payload.Contains("sandbox") && !payload.Contains("approvalPolicy") && !payload.Contains("model") && !payload.Contains("cwd"));
            string Idle(string status, int requests = 0) => JsonSerializer.Serialize(new { threadRuntimeStatus = new { type = status }, resumeState = "resumed", requests = Enumerable.Repeat(new { id = "approval" }, requests) });
            foreach (var (state, count, expectedState) in new[] { ("idle", 0, "idle"), ("active", 0, "busy"), ("idle", 1, "busy"), ("notLoaded", 0, "unknown") })
            { using var stateJson = JsonDocument.Parse(Idle(state, count)); Check($"live state {state} pending={count}", DesktopIpc.State(stateJson.RootElement) == expectedState); }
            bool invalid = false; try { TaskIntentText.Parse("{\"intent\":\"execute_shell\",\"reference\":\"current\",\"targetQuery\":\"\"}"); } catch (JsonException) { invalid = true; }
            Check("invalid model action fails closed", invalid);
            invalid = false; try { TaskIntentText.Parse("{\"intent\":\"continue\",\"reference\":\"current\"}"); } catch (JsonException) { invalid = true; }
            Check("missing target field fails closed", invalid);
            string journalPath = Path.Combine(directory, "test-queue.state");
            var persisted = new TaskControlQueue(settings, transport, journalPath); persisted.SetOwner("test-owner");
            b.Status = TaskStatusNames.Active;
            await persisted.SubmitAsync(b, "PRIVATE-TEST-INSTRUCTION", "persisted-message", snapshot, default);
            Check("pending instructions protected at rest", !File.ReadAllText(journalPath).Contains("PRIVATE-TEST-INSTRUCTION"));
            var restored = new TaskControlQueue(settings, transport, journalPath);
            Check("queued work survives restart", restored.Entries.Single().Instruction == "PRIVATE-TEST-INSTRUCTION" && restored.Entries.Single().State == "queued");
            restored.Cancel(null); b.Status = TaskStatusNames.Completed;
            var sending = new ControlJournal { Owner = "test-owner", Entries = new() { new() { Id = "recover-id", TaskKey = b.Key, State = "sending", Instruction = "must-not-retry" } } };
            File.WriteAllText(journalPath, LocalSecret.Protect(JsonSerializer.Serialize(sending, Settings.Json)));
            var recovered = new TaskControlQueue(settings, transport, journalPath);
            sent = transport.Sent.Count; await recovered.PumpAsync(snapshot, default);
            Check("crash during send recovers as unconfirmed without replay", recovered.Entries.Single().State == "unconfirmed" && recovered.Entries.Single().Instruction.Length == 0 && transport.Sent.Count == sent);
            var ownerQueue = new TaskControlQueue(settings, transport, null); ownerQueue.SetOwner("first");
            transport.Before = _ => { ownerQueue.SetOwner("second"); return Task.CompletedTask; };
            await ownerQueue.SubmitAsync(b, "old owner text", "owner-message", snapshot, default);
            Check("owner change cannot retain old request in new journal", ownerQueue.Entries.Count == 0);
            transport.Before = null;
            var scopedModel = new Model { Next = new("continue", "host-a", "unspecified") };
            var scopedTransport = new Transport();
            var scopedQueue = new TaskControlQueue(settings, scopedTransport, null);
            var scopedRouter = new TaskControlAssistant(settings, scopedModel, scopedModel, scopedQueue); scopedRouter.SetOwner("scoped-owner");
            var sameProject = new TaskRecord { Id = b.Id, Host = "host-b", Project = b.Project, Title = "另一个服务器的 PDE", Status = TaskStatusNames.Completed, LastActivity = 12 };
            var gpuOther = new TaskRecord { Id = Guid.NewGuid().ToString(), Host = "host-a", Project = "other", Title = "同服务器另一个项目", Status = TaskStatusNames.Completed, LastActivity = 7 };
            var scopedSnapshot = Snap(b, sameProject, gpuOther);
            reply = await scopedRouter.ReplyMessageAsync("让 host-a 的任务继续验证", scopedSnapshot, "scope-original", default);
            Check("remote host with multiple tasks asks choice", reply.Contains("尚未投递") && !reply.Contains("host-b") && scopedTransport.Sent.Count == 0);
            scopedModel.Next = new("select", "host-b", "unspecified");
            reply = await scopedRouter.ReplyMessageAsync("host-b", scopedSnapshot, "scope-outside", default);
            Check("pending host choice cannot silently switch servers", reply.Contains("尚未投递") && scopedTransport.Sent.Count == 0);
            scopedModel.Next = new("select", "PCA_TR", "unspecified");
            await scopedRouter.ReplyMessageAsync("PCA_TR 那个", scopedSnapshot, "scope-project", default);
            Check("project narrowing retains remote host and original instruction", scopedTransport.Sent.Single().Key == b.Key && scopedTransport.Sent[0].Text == "让 host-a 的任务继续验证");
            scopedRouter.Reset(); scopedModel.Next = new("continue", "PCA_TR", "unspecified");
            await scopedRouter.ReplyMessageAsync("让 host-a 的 PCA_TR 任务继续验证", scopedSnapshot, "scope-explicit", default);
            Check("explicit host constrains model project fragment", scopedTransport.Sent.Count == 2 && scopedTransport.Sent.Last().Key == b.Key);
            sameProject.Project = "v100only"; scopedRouter.Reset(); scopedModel.Next = new("continue", "v100only", "unspecified");
            reply = await scopedRouter.ReplyMessageAsync("让 host-a 的 v100only 任务继续验证", scopedSnapshot, "scope-missing", default);
            Check("missing project on named host cannot redirect to another server", scopedTransport.Sent.Count == 2 && reply.Contains("尚未投递") && !reply.Contains("host-b"));
            var p0 = new TaskRecord { Id = Guid.NewGuid().ToString(), Host = "host-a", Title = "执行 P0 任务并汇报", Project = "PCA_TR", Status = TaskStatusNames.Active, LastActivity = 20 };
            var jax = new TaskRecord { Id = Guid.NewGuid().ToString(), Host = "host-a", Title = "生成 JAX 显存压力测试", Project = "PCA_TR", Status = TaskStatusNames.Completed, LastActivity = 10 };
            var inspectSnapshot = Snap(p0, jax);
            var inspectModel = new Model { Next = new("inspect", "P0", "unspecified") };
            var inspectTransport = new Transport(); var inspectQueue = new TaskControlQueue(settings, inspectTransport, null);
            var inspectRouter = new TaskControlAssistant(settings, inspectModel, inspectModel, inspectQueue); inspectRouter.SetOwner("inspect-owner");
            reply = await inspectRouter.ReplyMessageAsync("汇报 host-a 的 P0 当前进展", inspectSnapshot, "inspect-active", default);
            Check("active task progress query answers without waiting", reply.StartsWith("只读进展") && inspectModel.Inspected.Single().Key == p0.Key);
            Check("progress query never submits or queues a turn", inspectTransport.Sent.Count == 0 && inspectQueue.Entries.Count == 0 && p0.Status == TaskStatusNames.Active);
            inspectRouter.Reset(); inspectModel.Next = new("inspect", "PDE", "unspecified");
            string progressQuestion = "让 host-a 上的 PDE 任务先解释测试结果，不要改代码";
            reply = await inspectRouter.ReplyMessageAsync(progressQuestion, inspectSnapshot, "inspect-ambiguous", default);
            Check("ambiguous progress asks for actual target even while active", reply.Contains("运行中也可查看") && reply.Contains(p0.Title) && inspectTransport.Sent.Count == 0);
            await inspectRouter.ReplyMessageAsync("1", inspectSnapshot, "inspect-select", default);
            Check("numeric selection preserves read-only query and original question", inspectModel.Inspected.Last() == (p0.Key, progressQuestion) && inspectTransport.Sent.Count == 0 && inspectQueue.Entries.Count == 0);
            inspectRouter.Reset();
            await inspectRouter.ReplyMessageAsync(progressQuestion, inspectSnapshot, "inspect-narrow", default);
            inspectModel.Next = new("select", "P0", "unspecified");
            await inspectRouter.ReplyMessageAsync("P0 那个", inspectSnapshot, "inspect-narrow-choice", default);
            Check("natural narrowing does not turn read-only query into work", inspectModel.Inspected.Last() == (p0.Key, progressQuestion) && inspectQueue.Entries.Count == 0);
            inspectModel.Next = new("continue", "", "current");
            reply = await inspectRouter.ReplyMessageAsync("继续测试，把失败项修好", inspectSnapshot, "inspect-then-work", default);
            Check("new work after inspection still queues on original busy task", reply.Contains("已排队") && inspectQueue.Entries.Single().TaskKey == p0.Key && inspectTransport.Sent.Count == 0);
            p0.Status = TaskStatusNames.Completed; await inspectRouter.PumpAsync(inspectSnapshot, default);
            Check("queued work after inspection preserves instruction", inspectTransport.Sent.Single().Key == p0.Key && inspectTransport.Sent[0].Text == "继续测试，把失败项修好");
            p0.Status = TaskStatusNames.Active; p0.TurnId = "current-turn"; p0.FinalReportTurnId = "old-turn"; p0.FinalReport = "OLD_INSPECT_REPORT";
            p0.Timeline = new() { new() { Kind = "进展", Text = "PUBLIC_PROGRESS" }, new() { Kind = "分析", Text = "PRIVATE_ANALYSIS" }, new() { Kind = "命令", Text = "RAW_COMMAND_OUTPUT" } };
            string inspectContext = ChatGptAssistant.ProgressContext(p0, inspectSnapshot);
            Check("progress context includes public active messages", inspectContext.Contains("PUBLIC_PROGRESS"));
            using var progressTime = JsonDocument.Parse(inspectContext);
            Check("progress timestamps use client timezone", progressTime.RootElement.GetProperty("displayTimeZone").GetString() == TimeZoneInfo.Local.Id
                && progressTime.RootElement.GetProperty("readAt").GetDateTimeOffset() == inspectSnapshot.ReadAt
                && progressTime.RootElement.GetProperty("readAt").GetDateTimeOffset().Offset == TimeZoneInfo.Local.GetUtcOffset(inspectSnapshot.ReadAt!.Value));
            Check("progress context excludes analysis commands and wrong-turn final", !inspectContext.Contains("PRIVATE_ANALYSIS") && !inspectContext.Contains("RAW_COMMAND_OUTPUT") && !inspectContext.Contains("OLD_INSPECT_REPORT"));
            p0.Timeline = Enumerable.Range(0, 10).Select(i => new TimelineEntry { Kind = "进展", Text = new string('x', 2000), Time = i }).ToList();
            using var boundedProgress = JsonDocument.Parse(ChatGptAssistant.ProgressContext(p0, inspectSnapshot));
            Check("progress context has bounded recent public excerpts", boundedProgress.RootElement.GetProperty("publicProgress").GetArrayLength() == 6
                && boundedProgress.RootElement.GetProperty("publicProgress")[0].GetProperty("text").GetString()!.Length <= 1201);
            Check("inspect intent parser accepts strict read-only mode", TaskIntentText.Parse("{\"intent\":\"inspect\",\"targetQuery\":\"P0\",\"reference\":\"current\"}").Intent == "inspect");
        }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
    }
}
