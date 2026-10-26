using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Explicit diagnostic: one small real inference, then one clearly labelled
// message to the already bound owner. Never polls WeChat or saves settings.
internal static class ChatGptLiveTest
{
    public static async Task RunAndSaveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        bool discovered = false, completed = false, attempted = false;
        bool? accepted = null;
        string model = "", reply = "", error = "";
        var structure = new List<object>();
        try
        {
            var settings = Settings.Load();
            using var assistant = new ChatGptAssistant(settings, () => { });
            assistant.ResponseObserver = root => {
                string type = ChatGptAuth.Text(root, "type");
                if (structure.Count >= 100 || type.EndsWith(".delta", StringComparison.Ordinal)) return;
                root.TryGetProperty("item", out var item); root.TryGetProperty("response", out var response);
                structure.Add(new { type, itemType = ChatGptAuth.Text(item, "type"), role = ChatGptAuth.Text(item, "role"), phase = ChatGptAuth.Text(item, "phase"), status = ChatGptAuth.Text(response, "status"),
                    outputTypes = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array ? output.EnumerateArray().Select(i => ChatGptAuth.Text(i, "type")).ToArray() : Array.Empty<string>() });
            };
            var account = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
            // A second diagnostic process must never race rotating refresh tokens.
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
                throw new ChatGptException("诊断需要刚授权且有效的账户；请先在主程序登录或刷新。");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var models = await assistant.ModelsAsync(account.ClientId, lifetime.Token); discovered = true;
            await assistant.EnsureModelAsync(lifetime.Token); model = settings.ChatGpt.Model;
            reply = await assistant.ReplyAsync("问 这是微信问答链路的连接测试。只回复一句：ChatGPT 已接通，可以在微信提问。", new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), lifetime.Token);
            completed = assistant.CompletedRequests == 1;
            if (!completed) throw new ChatGptException("未完成实际模型生成。");
            if (!settings.WeChat.ClawBotEnabled || settings.WeChat.ReadBinding() == null) throw new ChatGptException("模型测试已通过，但尚未启用微信绑定。");
            await using var wechat = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, receiveMessages: false);
            await wechat.ConfigureAsync(); attempted = true;
            accepted = await wechat.SendAiTestAsync(reply, lifetime.Token);
        }
        catch (Exception ex) { error = ex is WeChatApiException ? WeChatApi.Explain(ex) : ChatGptAssistant.Explain(ex); }
        File.WriteAllText(Path.Combine(directory, "stream-structure.json"), JsonSerializer.Serialize(structure, Settings.Json));
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
            at = DateTimeOffset.UtcNow, model, modelDiscoveryCompleted = discovered, inferenceCompleted = completed,
            wechatAttempted = attempted, wechatAccepted = accepted, phoneDeliveryVerified = false, reply, error
        }, Settings.Json));
        if (!completed || !attempted || error.Length > 0) throw new InvalidOperationException("ChatGPT live diagnostic not completed; see sanitized result.json.");
    }
}
