using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Explicit local diagnostic. Never polls the inbox, updates the binding, or
// writes credentials. Only the optional send flag makes one labelled test send.
internal static class WeChatDiagnostics
{
    public static async Task RunAsync(string directory, bool sendTest)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings.Load();
        var account = settings.WeChat.ReadBinding();
        using var monitor = new MonitorService(settings);
        var hosts = await monitor.RefreshAsync();
        bool? accepted = null;
        string? sendDetail = null;
        if (sendTest)
        {
            try
            {
                if (!settings.WeChat.ClawBotEnabled || account == null) throw new InvalidOperationException("微信尚未绑定或启用，未发送测试。");
                using var api = new WeChatApi();
                accepted = await api.SendClawAsync(account, "我在查任务结束后为什么没提醒你。这是一条单次测试消息，收到就说明当前微信发送通道还能用。", "codex-diagnostic-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
                sendDetail = accepted == true ? "微信接口已受理，请以手机收信为准。" : "微信接口未提供受理状态，请以手机收信为准。";
            }
            catch (Exception ex) { sendDetail = WeChatApi.Explain(ex); }
        }
        var tasks = hosts?.SelectMany(h => h.Tasks).Where(t => t.CompletedAt > DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()).Select(t =>
        {
            string key = WeChatText.Notification(t, false, DateTimeOffset.UtcNow).Key;
            return new { t.Title, t.Host, t.Status, t.StartedAt, t.CompletedAt,
                summaryAttempted = settings.WeChat.RecentAttempts.Contains(WeChatText.Hash("chatgpt-summary\ncompletion\n" + key)),
                clawAttempted = account != null && settings.WeChat.RecentAttempts.Contains(WeChatText.Hash("claw\n" + account.BotId + ":" + account.UserId + "\n" + key)) };
        });
        File.WriteAllText(Path.Combine(directory, "wechat-diagnostic.json"), JsonSerializer.Serialize(new {
            at = DateTimeOffset.UtcNow, bound = account != null, contextPresent = account?.ContextToken.Length > 0,
            lastOwnerMessageAt = account?.LastContextMessageAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(account.LastContextMessageAt) : (DateTimeOffset?)null,
            sendTest, accepted, sendDetail, tasks
        }, Settings.Json));
    }
}
