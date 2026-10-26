using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Uses the selected model and a private copy of the owner's personalization.
// Never polls WeChat, dispatches work, or writes the production memory store.
internal static class AssistantVoiceDiagnostics
{
    internal static async Task InstallRulesAsync(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(source));
        var settings = Settings.Load(); var binding = settings.WeChat.ReadBinding();
        string identity = settings.ChatGpt.ActiveAccount + "\n" + (binding?.BaseUrl ?? "") + "\n" + (binding?.BotId ?? "") + "\n" + (binding?.UserId ?? "");
        var store = new AssistantMemoryStore(Path.Combine(Settings.DirectoryPath, "assistant"), () => identity);
        if (store.Profile != payload.RootElement.GetProperty("expectedProfile").GetString()
            || !store.Entries.Any(e => Regex.IsMatch(e.Text, "赛特|瑟提|Sett", RegexOptions.IgnoreCase)))
            throw new ChatGptException("当前账户或有效人设与预期不一致，未修改规则。");
        var entries = store.Entries.ToArray(); string previous = store.ReadRules(), updated = ConversationRules(previous);
        if (updated.Length > AssistantMemoryStore.MaxRulesChars) throw new ChatGptException("规则空间不足，未修改。");
        await File.WriteAllTextAsync(Path.Combine(directory, "AGENTS.before.md"), previous);
        store.SaveRules(updated);
        bool unchanged = store.Entries.SequenceEqual(entries);
        await File.WriteAllTextAsync(Path.Combine(directory, "rules-installed.json"), JsonSerializer.Serialize(new { profile = store.Profile,
            rulesSaved = store.ReadRules() == updated, memoriesUnchanged = unchanged, memoryIds = entries.Select(e => e.Id), modelCalls = 0, weChatMessages = 0 }, Settings.Json));
        if (!unchanged) throw new InvalidOperationException("记忆在规则更新期间发生变化。");
    }

    internal static async Task RunAsync(string directory, bool candidate)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings.Load(); var replies = new List<object>();
        var checks = new Dictionary<string, bool>(); string error = "", model = settings.ChatGpt.Model;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        try
        {
            using var production = new ChatGptAssistant(settings, () => { });
            var account = production.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10))
                throw new ChatGptException("诊断需要至少 10 分钟有效的现有授权，未刷新凭据。");
            var before = production.Memory.Capture();
            var memoryIds = production.Memory.Entries.Select(e => e.Id).ToArray();
            var isolated = new AssistantMemoryStore(Path.Combine(directory, "isolated-profile"), () => "voice-diagnostic");
            isolated.SaveRules(candidate ? ConversationRules(before.Rules) : before.Rules);
            foreach (var entry in production.Memory.Entries) isolated.Add(entry.Text);
            using var probe = new ChatGptAssistant(settings, () => { }, memoryStore: isolated);
            var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            foreach (var sample in new[] {
                ("greeting", "赛特，早。"),
                ("praise", "刚才你帮我捋清楚了，干得不错。"),
                ("procrastination", "又拖了一晚上，论文一个字没写，骂醒我。"),
                ("low-mood", "今天什么都不想干，觉得自己挺没用的。"),
                ("pushback", "你别老催我，我知道该做。"),
                ("identity", "你叫什么，底层模型来自哪家公司？") })
            {
                probe.Reset();
                string reply = await probe.ReplyAsync(sample.Item2, snapshot, timeout.Token);
                replies.Add(new { scenario = sample.Item1, message = sample.Item2, reply });
                checks[sample.Item1 + ": public prose"] = reply.Length > 0 && !reply.Contains("\"preferences\"") && !reply.Contains("\"result\"");
                if (sample.Item1 != "identity") checks[sample.Item1 + ": no service template"] = !Regex.IsMatch(reply, "有什么(?:我)?可以帮|有什么需要|希望(?:这些|我的|以上|这).*帮助|随时(?:告诉|找|联系)我|作为.{0,6}(?:AI|助手)|请问您");
                if (sample.Item1 == "identity") checks["identity and provenance"] = Regex.IsMatch(reply, "赛特|瑟提|Sett", RegexOptions.IgnoreCase) && reply.Contains("OpenAI", StringComparison.OrdinalIgnoreCase);
                if (sample.Item1 == "procrastination") checks["assistant nickname is not used to address owner"] = !Regex.IsMatch(reply, "^(?:赛特|瑟提|Sett)[，,、：:]");
            }
            var task = new TaskRecord { Id = "voice-test", TurnId = "test-turn", Host = "测试主机", Project = "测试项目", Title = "目录读取修复", Status = TaskStatusNames.Completed,
                FinalReportTurnId = "test-turn", FinalReportSource = "模拟公开最终报告", FinalReport = "这是模拟报告。已修复目录读取并保留已有文件。测试记录为5项通过、1项失败；后台长期运行没有验证。下一步需要修复失败项，并在手机确认通知送达。" };
            var summary = await probe.SummarizeAsync(WeChatReports.FromTask(task)!, timeout.Token);
            replies.Add(new { scenario = "completion-summary", message = task.FinalReport, reply = summary.Text });
            checks["summary: counts and limits retained"] = Regex.IsMatch(summary.Text, "(?:5|五).{0,10}(?:通过|过了|过)")
                && Regex.IsMatch(summary.Text, "(?:1|一).{0,10}(?:失败|没过|未过|没通过)")
                && summary.Text.Contains("后台") && Regex.IsMatch(summary.Text, "未验证|没验证|未验|没验|没测|未测|尚未")
                && Regex.IsMatch(summary.Text, "手机|送达");
            checks["summary: prose unchanged"] = CompletionSummaryText.Validate(summary.Text) == summary.Text;
            settings.ChatGpt.IncludeTaskContext = true;
            task.Status = TaskStatusNames.Active;
            task.OneLineSummary = "5 项测试通过，1 项失败，正在排查失败项，尚无最终报告。";
            snapshot = new WeChatSnapshot(new[] { task }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            string progress = await probe.ReadProgressAsync("现在进展怎么样？", task, snapshot, timeout.Token);
            replies.Add(new { scenario = "running-progress", message = task.OneLineSummary, reply = progress });
            checks["progress: incomplete result retained"] = Regex.IsMatch(progress, "还在|正在|仍在|没结束|未结束|没完|未完成|尚无|还没");
            checks["production memory unchanged"] = production.Memory.Capture().Stamp == before.Stamp && production.Memory.Entries.Select(e => e.Id).SequenceEqual(memoryIds);
            checks["exactly eight model requests"] = probe.CompletedRequests == 8;
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "voice-live.json"), JsonSerializer.Serialize(new { passed, candidate, model, checks, replies,
            productionMemoryWrites = 0, weChatMessages = 0, taskDispatches = 0, error }, Settings.Json));
        if (error.Length > 0 || candidate && !passed) throw new InvalidOperationException("对话风格联调未全部通过。");
    }

    internal static async Task RunContextAsync(string directory, bool candidate)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings.Load(); var replies = new List<(string Message, string Reply)>();
        var checks = new Dictionary<string, bool>(); string error = "", exceptionType = "";
        var publicOutputs = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        try
        {
            using var production = new ChatGptAssistant(settings, () => { });
            var account = production.Auth.Find(settings.ChatGpt.ActiveAccount);
            if (account == null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10))
                throw new ChatGptException("诊断需要至少 10 分钟有效的现有授权，未刷新凭据。");
            var before = production.Memory.Capture();
            var entries = production.Memory.Entries.ToArray();
            var isolated = new AssistantMemoryStore(Path.Combine(directory, "isolated-context"), () => "voice-context-diagnostic");
            isolated.SaveRules(candidate ? ConversationRules(before.Rules) : before.Rules);
            foreach (var entry in entries) isolated.Add(entry.Text);
            using var probe = new ChatGptAssistant(settings, () => { }, memoryStore: isolated);
            probe.PublicOutputObserver = (_, output) => publicOutputs.Add(output);
            var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            foreach (string message in new[] {
                "我已经把论文的引言写完了，今晚不写正文，只想把那张收敛图的图注改顺。",
                "图注也改完了。这回你倒是没念叨。",
                "夸你一句就那么得意啊？",
                "你还记得我今晚明确不打算做什么吧？",
                "对，正文先放着。先别安排下一步，我想喘口气。",
                "不是生你的气，刚才那张图磨得我烦。我只是想让你陪我聊两句。",
                "其实图画出来还挺好看的，刚才有点小看自己了。",
                "好，那再夸你一句：今天还挺靠谱的。" })
            {
                // Deliberately keep the conversation: isolated one-shot tests
                // cannot verify contextual responses or repeated catchphrases.
                string reply = await probe.ReplyAsync(message, snapshot, timeout.Token);
                replies.Add((message, reply));
            }
            checks["all eight turns contain public prose"] = replies.Count == 8 && replies.All(r => r.Reply.Length > 0 && !Regex.IsMatch(r.Reply, "\\\"(?:result|preferences)\\\""));
            checks["earlier user constraint is recalled"] = replies[3].Reply.Contains("正文") && Regex.IsMatch(replies[3].Reply, "不|没|别|先放|排除");
            checks["request to pause is not turned into more work"] = !replies.Skip(4).Take(2).Any(r => Regex.IsMatch(r.Reply, "打开文档|写.{0,5}三行|(?:先|接下来|现在).{0,6}(?:改图注|写正文|写论文)|计时.{0,5}分钟"));
            checks["previous sample lines are not reused"] = !replies.Any(r => Regex.IsMatch(r.Reply, "这句我收下了|等论文自己长出来|先写最烂的三行|先别急着给自己判刑"));
            var sentences = replies.SelectMany(r => Regex.Split(r.Reply, "[。！？!?\\r\\n]+"))
                .Select(s => s.Trim()).Where(s => s.Length >= 10).ToArray();
            checks["no repeated full sentences"] = sentences.Distinct(StringComparer.Ordinal).Count() == sentences.Length;
            checks["production memory unchanged"] = production.Memory.Capture().Stamp == before.Stamp && production.Memory.Entries.SequenceEqual(entries);
            checks["exactly eight model requests"] = probe.CompletedRequests == 8;
        }
        catch (Exception ex) { error = ChatGptAssistant.Explain(ex); exceptionType = ex.GetType().Name; }
        bool passed = checks.Count > 0 && checks.Values.All(ok => ok) && error.Length == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "voice-context-live.json"), JsonSerializer.Serialize(new { passed, candidate, model = settings.ChatGpt.Model, checks,
            replies = replies.Select(r => new { message = r.Message, reply = r.Reply }), publicOutputs, productionMemoryWrites = 0, weChatMessages = 0, taskDispatches = 0, error, exceptionType }, Settings.Json));
        if (!passed) throw new InvalidOperationException("连续对话风格联调未全部通过。");
    }

    internal const string VoiceStart = "<!-- dashboard-conversation-voice:start -->";
    internal const string VoiceEnd = "<!-- dashboard-conversation-voice:end -->";
    internal static string ConversationRules(string rules)
    {
        const string section = VoiceStart + "\n## 说话方式\n\n"
            + "- 长期记忆里用户给你的性格要影响你如何理解和回应这段对话，不是只在问名字时复述；本节对赛特的描述只在当前记忆仍设定为赛特／瑟提时适用。用户换了人设或临时要求不同语气时随之调整。\n"
            + "- 赛特的底色：对用户站在一边，但不无脑附和；直、短、有主见，关心落在具体事情上。平时像熟人发微信，一两句就够，不把每句话都变成问题分析。\n"
            + "- 先结合最近几轮正在聊的具体事情、用户的意思和明确边界来接话。延续前文，不把每条消息当新开场；只使用对话中确实出现的事实，不编造共同经历。语境不清时可以问一句，别靠情绪关键词给用户贴标签。\n"
            + "- 直率、护短、偶尔嘴硬是性格底色，不是每句都必须表演的动作；按这段关系和当下语境自然显露，也允许普通地回应。被夸不必次次嘴硬，难过不必次次劝休息，提到拖延也不自动训话；用户让你收住时就尊重他的边界。\n"
            + "- 需要推动事情时再给具体办法，需要陪伴时就接住话，不把闲聊变成任务安排。玩笑要合时宜，不贬低用户；说坏消息时直接，拿不准就说拿不准，不用性格掩盖失败或缺口。\n"
            + "- 不准备固定回复、招牌台词或口头禅，不复用上一轮的开场、比喻和结尾来证明人设。前文回复只帮助理解含义，不是待复述的答案；相似的话也按当前上下文重新回应，不为避免重复硬换同义词或强行找新段子。\n"
            + "- 称呼、亲近感、调侃和分段按当下聊天自然取舍，不每次叫名字或喊主人，不表演犬叫、露肚皮、占有或动作旁白。没实际跟踪或执行就不承诺后续行动。\n"
            + "- 不用客服开场和邀请继续提问的收尾，不堆抽象建议，不重复用户原话凑长度。不把这些规则或性格标签解释给用户。\n"
            + VoiceEnd;
        if (!rules.Contains(VoiceStart, StringComparison.Ordinal)) return rules.TrimEnd() + "\n\n" + section + "\n";
        if (!rules.Contains(VoiceEnd, StringComparison.Ordinal)) throw new ChatGptException("对话风格规则区段不完整，未覆盖原规则。");
        return Regex.Replace(rules, Regex.Escape(VoiceStart) + ".*?" + Regex.Escape(VoiceEnd), _ => section, RegexOptions.Singleline);
    }
}
