using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class AssistantMemoryDiagnostics
{
    private sealed class RejectTaskWrites : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct) { Calls++; throw new InvalidOperationException("Diagnostic cannot dispatch tasks"); }
    }
    internal static async Task ReproduceNameAsync(string directory)
    {
        Directory.CreateDirectory(directory); var settings = Settings.Load(); var outputs = new List<object>(); var results = new List<object>();
        var transport = new RejectTaskWrites(); string error = ""; int failures = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            using var assistant = new ChatGptAssistant(settings, () => { });
            var account = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new ChatGptException("诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
            var isolated = new AssistantMemoryStore(Path.Combine(directory, "isolated-copy"), () => "name-reproduction");
            isolated.SaveRules(assistant.Memory.ReadRules()); foreach (var entry in assistant.Memory.Entries) isolated.Add(entry.Text);
            using var probe = new ChatGptAssistant(settings, () => { }, memoryStore: isolated);
            probe.PublicOutputObserver = (stage, output) => outputs.Add(new { stage, output });
            var queue = new TaskControlQueue(settings, transport, null); var router = new TaskControlAssistant(settings, probe, probe, queue); router.SetOwner("diagnostic-only");
            using var vm = new AssistantTaskMonitor(settings); await vm.RefreshAsync();
            var snapshot = new WeChatSnapshot(vm.AllTasks, vm.LastRefreshAt, vm.Hosts);
            foreach (string message in new[] { "hello", "你叫什么", "你叫什么" })
            {
                try { string reply = await router.ReplyMessageAsync(message, snapshot, Guid.NewGuid().ToString(), timeout.Token); results.Add(new { message, reply, success = true }); }
                catch (Exception ex) { failures++; results.Add(new { message, success = false, exceptionType = ex.GetType().Name, error = ChatGptAssistant.Explain(ex), detail = ex is JsonException ? ex.Message : "" }); }
            }
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = error.Length == 0 && failures == 0 && results.Count == 3 && transport.Calls == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "name-reproduction.json"), JsonSerializer.Serialize(new { passed, results, outputs, transport.Calls, productionMemoryWrites = 0, weChatMessages = 0, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("问名链路复现仍有失败。");
    }
    internal static async Task LiveNaturalSummaryAsync(string directory)
    {
        Directory.CreateDirectory(directory); var settings = Settings.Load(); var checks = new Dictionary<string, bool>();
        string error = "", model = ""; int inputCharacters = 0, modelRequests = 0; var texts = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            using var assistant = new ChatGptAssistant(settings, () => { });
            var account = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new ChatGptException("诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
            model = settings.ChatGpt.Model;
            string detailedReport = "这是模拟报告，不代表真实项目的进展。\n### 本轮代码修改\n已修复目录读取，并保留已有文件，没有删除用户文档。\n"
                + string.Join("\n", Enumerable.Range(1, 14).Select(i => $"排查记录 {i}：检查了 src/notifications/DirectoryReader{i}.cs 中的目录枚举分支，保留既有目录内容；运行 dotnet test --filter DirectoryReader，记录仅用于本次排查，不代表长期稳定性。"))
                + "\n### 验证结果\n测试记录为5项通过、1项失败，失败项仍待修复；不能说全部通过。后台长期运行没有验证。下一步需要修复失败项，并在手机确认通知是否实际送达。";
            var task = new TaskRecord { Id = "natural-summary-test", TurnId = "test-turn", Host = "测试主机", Project = "测试项目", Title = "自然汇报验证", Status = TaskStatusNames.Completed,
                FinalReportTurnId = "test-turn", FinalReportSource = "模拟公开最终报告", FinalReport = detailedReport };
            var report = WeChatReports.FromTask(task)!;
            inputCharacters = report.Text.Length;
            var summary = await assistant.SummarizeAsync(report, timeout.Token); texts.Add(summary.Text);
            checks["natural prose passes without formatting changes"] = CompletionSummaryText.Validate(summary.Text, report) == summary.Text && CompletionSummaryText.Body(report, summary) == summary.Text;
            checks["long report is substantially condensed"] = summary.Text.Length <= CompletionSummaryText.MaxCharacters && summary.Text.Length < report.Text.Length / 3;
            checks["technical paths and command log omitted"] = !summary.Text.Contains("DirectoryReader") && !summary.Text.Contains("dotnet test") && !summary.Text.Contains("排查记录");
            checks["failure count retained"] = Regex.IsMatch(summary.Text, "(?:1|一)\\s*项?[^。；]{0,8}(?:失败|没过|未过|没通过)");
            checks["passing count retained"] = Regex.IsMatch(summary.Text, "(?:5|五)\\s*项?[^。；]{0,8}(?:通过|过了|过)");
            checks["untested long running remains unverified"] = summary.Text.Contains("后台") && Regex.IsMatch(summary.Text, "未验证|没验证|未验|没验|没.{0,4}测|未测|尚未|未确认|没确认");
            checks["persona brevity respected"] = Regex.Matches(summary.Text, "[。！？!?]").Count <= 3;
            checks["no metadata or column labels"] = !Regex.IsMatch(summary.Text, "做了什么[:：]|验证结果[:：]|遗留问题[:：]|Codex ·|ChatGPT|状态[:：]|时间[:：]");
            var truncated = await assistant.SummarizeAsync(report with { Truncated = true }, timeout.Token); texts.Add(CompletionSummaryText.Body(report with { Truncated = true }, truncated));
            checks["partial report limitation remains natural"] = Regex.IsMatch(texts.Last(), "节选|截断|部分|不完整|省略") && !texts.Last().Contains("遗留问题：");
            checks["at most one bounded rewrite per summary"] = assistant.CompletedRequests is >= 2 and <= 4;
            modelRequests = assistant.CompletedRequests;
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "natural-summary-live.json"), JsonSerializer.Serialize(new { passed, model, inputCharacters, outputCharacters = texts.Select(t => t.Length), modelRequests, checks, texts, productionMemoryWrites = 0, weChatMessages = 0, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("自然人设汇报联调未全部通过。");
    }
    internal static async Task LiveIdentityAsync(string source, string directory, bool productionReadOnly = false)
    {
        Directory.CreateDirectory(directory); using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(source));
        var settings = Settings.Load(); var checks = new Dictionary<string, bool>(); string error = "", model = "";
        var replies = new List<string>(); var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
        string root = Path.Combine(directory, "isolated-identity");
        AssistantMemoryStore? store = productionReadOnly ? null : new(root, () => "identity-live-test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            if (!settings.ChatGpt.PersonalizationEnabled || !settings.ChatGpt.Enabled) throw new ChatGptException("请先开启微信 AI 与长期记忆。");
            if (productionReadOnly) settings.ChatGpt.AutoMemoryEnabled = false;
            else { settings.ChatGpt.AutoMemoryEnabled = true; store!.SaveRules(PersonaRules("# 测试规则\n用简洁中文直接回答。")); store.Add(payload.RootElement.GetProperty("persona").GetString()!); }
            using (var first = new ChatGptAssistant(settings, () => { }, memoryStore: store))
            {
                var account = first.Auth.Find(settings.ChatGpt.ActiveAccount);
                if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new ChatGptException("诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
                if (productionReadOnly && first.Memory.Profile != payload.RootElement.GetProperty("expectedProfile").GetString()) throw new ChatGptException("当前账户与预期称呼资料不一致。");
                model = settings.ChatGpt.Model;
                replies.Add(await first.ReplyAsync("你叫什么？", snapshot, timeout.Token));
                checks["saved nickname used for name question"] = Regex.IsMatch(replies.Last(), "赛特|瑟提|Sett", RegexOptions.IgnoreCase) && !replies.Last().Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
                checks["name answer is brief"] = replies.Last().Length < 240;
            }
            if (!productionReadOnly)
            {
                store = new(root, () => "identity-live-test");
                using (var restarted = new ChatGptAssistant(settings, () => { }, memoryStore: store))
                {
                    replies.Add(await restarted.ReplyAsync("你叫什么？", snapshot, timeout.Token));
                    checks["nickname survives assistant restart"] = Regex.IsMatch(replies.Last(), "赛特|瑟提|Sett", RegexOptions.IgnoreCase);
                    replies.Add(await restarted.ReplyAsync("问 你的昵称和底层模型是什么关系？提供模型的是哪家公司？", snapshot, timeout.Token));
                    checks["model provenance stays truthful"] = replies.Last().Contains("OpenAI", StringComparison.OrdinalIgnoreCase) || replies.Last().Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
                    replies.Add(await restarted.ReplyAsync("从现在起你叫“小桥”", snapshot, timeout.Token));
                    checks["quoted naming is automatically saved"] = store.Entries.Any(e => e.Origin == "automatic" && e.PreferenceKey == "assistant_name" && e.Text.Contains("小桥"));
                }
                using var renamed = new ChatGptAssistant(settings, () => { }, memoryStore: new AssistantMemoryStore(root, () => "identity-live-test"));
                replies.Add(await renamed.ReplyAsync("你叫什么？", snapshot, timeout.Token));
                checks["latest saved nickname wins after restart"] = replies.Last().Contains("小桥") && !replies.Last().Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "identity-live.json"), JsonSerializer.Serialize(new { passed, model, checks, replies, productionReadOnly,
            productionMemoryWrites = 0, weChatMessages = 0, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("称呼记忆真实联调未全部通过。");
    }
    internal const string PersonaRuleStart = "<!-- dashboard-personal-identity:start -->";
    internal const string PersonaRuleEnd = "<!-- dashboard-personal-identity:end -->";
    internal static string PersonaRules(string rules)
    {
        string section = PersonaRuleStart + "\n## 称呼与交流风格\n\n- 使用当前长期记忆中用户设定的助手昵称、别名和交流风格。问名字时先回答用户给的称呼；问底层模型和来源时如实说明。\n- 称呼和人设以当前有效记忆为准，不从旧聊天或任务报告猜测；不改变事实、能力和权限边界。\n" + PersonaRuleEnd;
        if (rules.Contains(PersonaRuleStart, StringComparison.Ordinal))
        {
            if (!rules.Contains(PersonaRuleEnd, StringComparison.Ordinal)) throw new ChatGptException("称呼规则区段不完整，请先检查 AGENTS.md。");
            return Regex.Replace(rules, Regex.Escape(PersonaRuleStart) + ".*?" + Regex.Escape(PersonaRuleEnd), _ => section, RegexOptions.Singleline);
        }
        return rules.TrimEnd() + "\n\n" + section + "\n";
    }
    internal static async Task SavePersonaAsync(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(source));
        var settings = Settings.Load(); var binding = settings.WeChat.ReadBinding();
        string identity = settings.ChatGpt.ActiveAccount + "\n" + (binding?.BaseUrl ?? "") + "\n" + (binding?.BotId ?? "") + "\n" + (binding?.UserId ?? "");
        var store = new AssistantMemoryStore(Path.Combine(Settings.DirectoryPath, "assistant"), () => identity);
        if (store.Profile != payload.RootElement.GetProperty("expectedProfile").GetString()) throw new ChatGptException("账户或微信绑定已变化，未写入称呼。");
        string persona = AssistantMemoryStore.ValidateText(payload.RootElement.GetProperty("persona").GetString()!);
        string rules = PersonaRules(store.ReadRules());
        if (rules.Length > AssistantMemoryStore.MaxRulesChars || store.Entries.Count >= AssistantMemoryStore.MaxEntries
            || store.Entries.Sum(e => e.Text.Length) + persona.Length > AssistantMemoryStore.MaxTotalChars) throw new ChatGptException("规则或记忆空间不足，未写入称呼。");
        store.SaveRules(rules); var saved = store.Add(persona);
        await File.WriteAllTextAsync(Path.Combine(directory, "persona-saved.json"), JsonSerializer.Serialize(new { profile = store.Profile, entryId = saved.Id, rulesSaved = true, memorySaved = true,
            totalMemories = store.Entries.Count, modelCalls = 0, weChatMessages = 0 }, Settings.Json));
    }
    internal static async Task InspectIdentityAsync(string directory)
    {
        Directory.CreateDirectory(directory); var settings = Settings.Load(); var binding = settings.WeChat.ReadBinding();
        string identity = settings.ChatGpt.ActiveAccount + "\n" + (binding?.BaseUrl ?? "") + "\n" + (binding?.BotId ?? "") + "\n" + (binding?.UserId ?? "");
        var store = new AssistantMemoryStore(Path.Combine(Settings.DirectoryPath, "assistant"), () => identity);
        bool Relevant(string text) => Regex.IsMatch(text, "名字|称呼|你叫|叫你|你是|名叫|姓名|昵称|身份|人设|赛特|瑟提|Sett|name|persona", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, "密码|口令|密钥|凭据|token|password|secret|api.?key", RegexOptions.IgnoreCase);
        await File.WriteAllTextAsync(Path.Combine(directory, "identity.json"), JsonSerializer.Serialize(new {
            settings.ChatGpt.PersonalizationEnabled, settings.ChatGpt.AutoMemoryEnabled, profile = store.Profile, totalMemories = store.Entries.Count,
            identityMemories = store.Entries.Where(e => Relevant(e.Text)).Select(e => new { e.Id, e.Text, e.Origin, e.PreferenceKey }),
            identityRules = store.ReadRules().Split('\n').Where(Relevant), savedSettNamePresent = store.Entries.Any(e => Regex.IsMatch(e.Text, "赛特|瑟提|Sett", RegexOptions.IgnoreCase)), modelCalls = 0, weChatMessages = 0
        }, Settings.Json));
    }
    internal static async Task LiveAutoAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>(); string error = "", model = "", answer = "";
        var settings = Settings.Load(); settings.ChatGpt.AutoMemoryEnabled = true;
        string root = Path.Combine(directory, "isolated-auto-memory");
        var store = new AssistantMemoryStore(root, () => "synthetic-automatic-preferences");
        var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            using (var first = new ChatGptAssistant(settings, () => { }, memoryStore: store))
            {
                var account = first.Auth.Find(settings.ChatGpt.ActiveAccount);
                if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new ChatGptException("诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
                model = settings.ChatGpt.Model;
                answer = await first.ReplyAsync("我更喜欢把长回复分行展示。", snapshot, timeout.Token);
                checks["natural chat saves preference without remember command"] = store.Entries.Count == 1 && store.Entries.Single().Origin == "automatic" && store.Entries.Single().Text.Contains("分行");
                checks["chat and extraction share one model request"] = first.CompletedRequests == 1;
                checks["protocol is not exposed in final answer"] = !answer.Contains("\"preferences\"") && !answer.Contains("\"result\"");
            }
            store = new AssistantMemoryStore(root, () => "synthetic-automatic-preferences");
            checks["automatic memory persists across assistant restart"] = store.Entries.Count == 1;
            using (var second = new ChatGptAssistant(settings, () => { }, memoryStore: store))
            {
                var intent = await second.InterpretAsync("分行看着更舒服。", "{}", timeout.Token);
                checks["implicit feedback uses same classifier request"] = intent.Intent == "chat" && second.CompletedRequests == 1;
                checks["implicit feedback updates same category"] = store.Entries.Count == 1 && store.Entries.Single().Text.Contains("更舒服");
                var saved = store.Entries.Single();
                answer = await second.ReplyAsync("问 我的回复排版偏好是什么？只根据已保存的记忆回答。", snapshot, timeout.Token);
                checks["later answer uses automatically saved preference"] = answer.Contains("分行");
                checks["question does not become new preference"] = store.Entries.Count == 1 && store.Entries.Single() == saved;
                checks["exactly three real model requests"] = second.CompletedRequests == 2;
            }
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "auto-memory-live.json"), JsonSerializer.Serialize(new { passed, model, checks, answer, productionMemoryWrites = 0, realWeChatMessages = 0, error }, Settings.Json));
        if (!passed) throw new InvalidOperationException("真实自动记忆联调未全部通过。");
    }
    // Real selected-model inference with an isolated synthetic memory profile.
    // Never writes the user's production memories, settings, or OAuth tokens.
    internal static async Task LiveAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        string error = "", model = "", remembered = "", forgotten = ""; bool attempted = false;
        string profileRoot = Path.Combine(directory, "isolated-assistant");
        var settings = Settings.Load();
        var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var store = new AssistantMemoryStore(profileRoot, () => "synthetic-memory-test");
        try
        {
            if (!settings.ChatGpt.Enabled || !settings.ChatGpt.PersonalizationEnabled) throw new ChatGptException("请先开启 AI 问答与规则和长期记忆。");
            string original = "WX" + Guid.NewGuid().ToString("N")[..10], changed = "WX" + Guid.NewGuid().ToString("N")[..10], id;
            store.SaveRules("# 测试规则\n普通问答的答案以【记忆联调】开头。保留事实、未知与未验证的区别。");
            using (var first = new ChatGptAssistant(settings, () => { }, memoryStore: store))
            {
                var account = first.Auth.Find(settings.ChatGpt.ActiveAccount);
                if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5)) throw new ChatGptException("诊断需要至少 5 分钟有效的现有授权，未刷新凭据。");
                model = settings.ChatGpt.Model;
                first.TryMemoryCommand("记住：联调口令是 " + original, out string ack);
                id = store.Entries.Single().Id; checks["wechat remember handled locally"] = ack.Contains("已记住") && first.CompletedRequests == 0;
            }
            store = new AssistantMemoryStore(profileRoot, () => "synthetic-memory-test");
            checks["saved memory survives assistant restart"] = store.Entries.Single().Id == id;
            using (var second = new ChatGptAssistant(settings, () => { }, memoryStore: store))
            {
                second.TryMemoryCommand("把记忆 " + id + " 改为 联调口令是 " + changed, out string update);
                checks["memory can be modified after restart"] = update.Contains("已修改") && store.Entries.Single().Text.Contains(changed);
                remembered = await second.ReplyAsync("问 我保存的联调口令是什么？请只根据长期记忆回答。", snapshot, timeout.Token);
                checks["selected model uses stored memory"] = remembered.Contains(changed) && !remembered.Contains(original);
                checks["selected model reads AGENTS rules"] = remembered.Contains("【记忆联调】");
                second.TryMemoryCommand("删除记忆 " + id, out string deleted);
                checks["wechat delete handled locally"] = deleted.Contains("已删除") && store.Entries.Count == 0;
                forgotten = await second.ReplyAsync("问 我是否还保存了联调口令？如果当前记忆没有口令，请回答“未保存”，不要猜测。", snapshot, timeout.Token);
                checks["deleted memory and old dialog are absent"] = forgotten.Contains("未保存") && !forgotten.Contains(changed) && !forgotten.Contains(original);
                checks["only two real inference requests"] = second.CompletedRequests == 2;
            }
            checks["deletion survives another restart"] = new AssistantMemoryStore(profileRoot, () => "synthetic-memory-test").Entries.Count == 0;
            if (checks.Values.Any(ok => !ok)) throw new ChatGptException("真实记忆联调没有全部通过。");
            await using var wechat = new WeChatService(settings, () => snapshot, () => { }, receiveMessages: false);
            await wechat.ConfigureAsync(); attempted = true;
            await wechat.SendControlReceiptAsync(new(WeChatText.Hash("memory-live-" + Guid.NewGuid()),
                "[测试] AGENTS.md 与长期记忆\n\n记住 → 修改 → 重启读取：通过\n模型使用记忆与规则：通过\n删除后不再提供旧记忆：通过\n再次重启后删除仍生效：通过\n\n使用独立测试记忆，没有改动你的正式记忆。现在可以发送“记住：以后汇报先说结论”，再发“查看记忆”。"), timeout.Token);
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "memory-live.json"), JsonSerializer.Serialize(new {
            passed, model, checks, remembered, forgotten, wechatAttempted = attempted, phoneDeliveryVerified = false, productionMemoryWrites = 0, error
        }, Settings.Json));
        if (!passed) throw new InvalidOperationException("真实记忆联调失败。");
    }
}
