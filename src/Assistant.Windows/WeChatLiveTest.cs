using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using System.Diagnostics;
using System.Linq;

namespace CodexDashboard;

// Explicit opt-in CLI test: sends one labelled simulated completion through the
// production transition detector and notification queue. It neither polls WeChat
// nor saves settings, so a running Dashboard retains its own cursor and binding.
internal static class WeChatLiveTest
{
    public static async Task RunAndSaveAsync(string directory, bool aiSummary = false, bool exclusiveAuthRefresh = false)
    {
        Directory.CreateDirectory(directory);
        string testId = "live-completion-" + Guid.NewGuid().ToString("N");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        int transitions = 0;
        bool? accepted = null;
        CompletionSummary? summary = null;
        AssistantFailure? failure = null;
        string detail;
        try
        {
            var settings = Settings.Load();
            if (!settings.WeChat.ClawBotEnabled || !settings.WeChat.NotifyCompleted)
                throw new InvalidOperationException("ClawBot 或本轮结束通知未启用，测试未发送。");
            var account = settings.WeChat.ReadBinding();
            if (account == null || account.ContextToken.Length == 0)
                throw new InvalidOperationException("请先绑定微信，并给 ClawBot 发送一条消息激活；测试未发送。");
            // Test-specific changes remain in this process and never affect user settings.
            settings.WeChat.PushPlusEnabled = false;
            settings.WeChat.IncludeSummary = true;
            using var assistant = aiSummary ? new ChatGptAssistant(settings, () => { }) : null;
            if (assistant != null)
            {
                assistant.Changed += () => failure = assistant.LastFailure;
                var active = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
                if (active == null)
                    throw new ChatGptException("测试需要有效的 ChatGPT 登录；请先在主程序完成登录或刷新。");
                if (exclusiveAuthRefresh)
                {
                    // A rotating refresh token must have exactly one owner. The
                    // launcher stops the main app and restarts it after this test.
                    using var own = Process.GetCurrentProcess();
                    var others = Process.GetProcessesByName(own.ProcessName);
                    try
                    {
                        if (others.Any(p => p.Id != own.Id)) throw new ChatGptException("独占续期测试要求先退出所有其他 Dashboard 进程，未调用模型或发送消息。");
                    }
                    finally { foreach (var process in others) process.Dispose(); }
                    using var refreshTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(35));
                    _ = await assistant.Auth.AccessTokenAsync(active.ClientId, refreshTimeout.Token);
                }
                else if (active.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
                    throw new ChatGptException("测试需要有效的 ChatGPT 登录；请先在主程序完成登录或刷新。");
                settings.ChatGpt.CompletionSummariesEnabled = true;
                settings.WeChat.ReportQueriesEnabled = false; // No synthetic report ID in the real Dashboard.
            }
            var task = new TaskRecord
            {
                Id = testId, TurnId = testId, Host = "本机", Project = "Dashboard 通知测试",
                Title = "[测试] 微信自动完成提醒", Status = TaskStatusNames.Active,
                StartedAt = started.ToUnixTimeSeconds(), LastActivity = started.ToUnixTimeSeconds(),
                OneLineSummary = "这是你要求的单次自动提醒测试：模拟任务从运行中变为本轮结束，经状态检测、通知队列和微信发送。"
            };
            if (aiSummary)
            {
                task.Title = "[测试] AI 自动完成摘要（模拟任务）";
                task.FinalReportTurnId = testId; task.FinalReportSource = "单次模拟任务的公开最终报告";
                task.FinalReport = "这是明确标注的模拟任务，只验证自动摘要和微信发送，不代表其他项目的实际进展。\n"
                    + "以下是模拟结果：目录读取流程已经调整，检查有 5 项通过、1 项失败；失败项是异常输入仍未正确处理。\n"
                    + "本条消息在手机上是否收到尚未确认，后台长期运行和网络长期稳定性也都未验证。";
            }
            var detector = new NotificationTransitions();
            detector.Observe(new[] { task }, started);
            await using var service = new WeChatService(settings, () => new(new[] { task }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, receiveMessages: false, assistant: assistant);
            await service.ConfigureAsync();
            var receipt = new TaskCompletionSource<WeChatReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.NotificationAttempted += result => { if (result.Channel == "clawbot") receipt.TrySetResult(result); };
            service.CompletionPrepared += result => summary = result;
            await Task.Delay(1500);
            task.Status = TaskStatusNames.Completed;
            task.CompletedAt = task.LastActivity = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var completed = detector.Observe(new[] { task }, DateTimeOffset.UtcNow); transitions = completed.Count;
            if (transitions != 1) throw new InvalidOperationException("完成状态检测未触发一次通知，测试已停止。");
            foreach (var item in completed) service.Observe(item);
            var result = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(aiSummary ? 65 : 25)); accepted = result.Accepted; detail = result.Detail;
        }
        catch (Exception ex) { detail = ex is ChatGptException ? ChatGptAssistant.Explain(ex) : WeChatApi.Explain(ex); }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            testId, simulatedTask = true, startedAt = started.ToOffset(TimeSpan.FromHours(8)),
            finishedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)), transitionsObserved = transitions,
            channel = "clawbot", accepted, detail, handsetDeliveryVerified = false,
            aiSummaryRequested = aiSummary, summaryGenerated = summary?.Generated == true, summaryModel = summary?.Model, summaryText = summary?.Text, summaryReason = summary?.Reason,
            summaryModelRequests = summary?.ModelRequests, summaryRewriteRetried = summary?.RewriteRetried, summaryNetworkRetried = summary?.NetworkRetried, failure
        }, Settings.Json));
    }
}
