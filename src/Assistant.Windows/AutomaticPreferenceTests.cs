using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class AutomaticPreferenceTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class NoWrites : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct) { Calls++; throw new InvalidOperationException("unexpected dispatch"); }
    }
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new {
        type = "response.completed", response = new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } }
    }) + "\n\n", Encoding.UTF8, "text/event-stream") };
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool ok) { checks[name] = ok; if (!ok) throw new InvalidOperationException(name); }
        IReadOnlyList<AutomaticPreference> Candidates(string source, string evidence, string key = "report_layout")
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { preferences = new[] { new { key, evidence } } }));
            return AutomaticPreferenceText.Read(doc.RootElement, source);
        }
        try
        {
            Check("natural long term feedback eligible", AutomaticPreferenceText.MayLearn("我更喜欢分行") && AutomaticPreferenceText.MayLearn("最好加入分行。"));
            Check("implicit feedback needs no activation keyword", AutomaticPreferenceText.MayLearn("分行看着更舒服") && Candidates("分行看着更舒服", "分行看着更舒服").Count == 1);
            Check("plain work instruction is not a preference", !AutomaticPreferenceText.MayLearn("继续测试并修复失败项"));
            foreach (var item in new[] {
                ("new SSH alias", "让 render-node 上的任务以后分行汇报"),
                ("ASCII alias punctuation", "让 build.worker_27 上的任务以后分行汇报"),
                ("numeric host", "让 192.0.2.10 上的任务以后分行汇报"),
                ("local host", "让本机的任务以后分行汇报"),
                ("server noun", "让服务器上的任务以后分行汇报"),
                ("host noun", "让主机上的任务以后分行汇报"),
                ("spaced polite prefix", "请 让 ci-runner 上的任务以后分行汇报") })
            {
                Check("host work is not eligible: " + item.Item1, !AutomaticPreferenceText.MayLearn(item.Item2));
                Check("host work cannot persist model evidence: " + item.Item1, Candidates(item.Item2, item.Item2).Count == 0);
                using var candidate = JsonDocument.Parse(JsonSerializer.Serialize(new { preferences = new[] {
                    new { key = "report_layout", evidence = item.Item2 } } }));
                Check("host work cannot pass automatic memory tool: " + item.Item1,
                    !AutomaticPreferenceText.TryReadTool(candidate.RootElement, item.Item2, out _));
            }
            const string styleFeedback = "让回答以后都分行";
            Check("ordinary style feedback beginning with let remains eligible", AutomaticPreferenceText.MayLearn(styleFeedback)
                && Candidates(styleFeedback, styleFeedback).Count == 1);
            using (var preference = JsonDocument.Parse(JsonSerializer.Serialize(new { preferences = new[] {
                new { key = "report_layout", evidence = styleFeedback } } })))
                Check("ordinary style feedback remains valid for automatic memory tool",
                    AutomaticPreferenceText.TryReadTool(preference.RootElement, styleFeedback, out var learned) && learned.Single().Evidence == styleFeedback);
            const string mixedHostWork = "让 render-node 上的任务以后分行汇报。以后回答先说结论。";
            Check("independent preference beside host work remains learnable", AutomaticPreferenceText.MayLearn(mixedHostWork)
                && Candidates(mixedHostWork, "让 render-node 上的任务以后分行汇报").Count == 0
                && Candidates(mixedHostWork, "以后回答先说结论", "report_order").Count == 1);
            Check("assistant naming is eligible", AutomaticPreferenceText.MayLearn("以后你叫赛特") && Candidates("以后你叫赛特", "以后你叫赛特", "assistant_name").Count == 1);
            Check("quoted nickname is assignment rather than quotation", AutomaticPreferenceText.MayLearn("从现在起你叫“小桥”") && Candidates("从现在起你叫“小桥”", "从现在起你叫“小桥”", "assistant_name").Count == 1);
            Check("user preferred name is supported separately", Candidates("叫我阿林", "叫我阿林", "user_name").Count == 1);
            Check("assistant and user names cannot be swapped", Candidates("叫我阿林", "叫我阿林", "assistant_name").Count == 0 && Candidates("你叫赛特", "你叫赛特", "user_name").Count == 0);
            Check("name question never becomes naming fact", Candidates("你叫什么", "你叫什么", "assistant_name").Count == 0);
            Check("quoted third party name cannot be saved", Candidates("他说“你叫小桥”", "你叫小桥", "assistant_name").Count == 0);
            Check("persona alias statement is supported", Candidates("你是赛特，也叫瑟提（Sett）", "你是赛特，也叫瑟提（Sett）", "assistant_name").Count == 1);
            string personaRules = AssistantMemoryDiagnostics.PersonaRules("用户原有规则");
            Check("persona guidance preserves user rules and is idempotent", personaRules.StartsWith("用户原有规则") && AssistantMemoryDiagnostics.PersonaRules(personaRules) == personaRules);
            Check("persona name has one source in memory", !personaRules.Contains("赛特") && !personaRules.Contains("小桥"));
            var fallbackIntent = AutomaticPreferenceText.Intent("{\"result\":\"我是 ChatGPT。\",\"preferences\":[]}", "你叫什么", out var ignored);
            Check("observed classifier answer falls back only to chat", fallbackIntent == new TaskIntent("chat", "", "unspecified") && ignored.Count == 0);
            fallbackIntent = AutomaticPreferenceText.Intent("{\"result\":\"马上执行删除\",\"preferences\":[{\"key\":\"report_layout\",\"evidence\":\"我更喜欢分行\"}]}", "我更喜欢分行", out ignored);
            Check("unstructured classifier reply cannot authorize work or save memory", fallbackIntent.Intent == "chat" && ignored.Count == 0);
            bool rejectedShape = false; try { AutomaticPreferenceText.Intent("{\"result\":null}", "你叫什么", out _); } catch (JsonException) { rejectedShape = true; }
            Check("non text invalid routing remains rejected", rejectedShape);
            Check("routing and answering have unambiguous separate contracts", AutomaticPreferenceText.IntentInstructions.Contains("即使用户发 hello、你叫什么")
                && !AutomaticPreferenceText.IntentInstructions.Contains("result 必须是直接回答用户的文本字符串"));
            const string prose = "那今晚就只改图注，正文先不碰。";
            string mixed = prose + JsonSerializer.Serialize(new { result = prose, preferences = new[] { new { key = "report_layout", evidence = "我更喜欢分行" } } });
            Check("observed duplicate prose and envelope expose only agent text", AutomaticPreferenceText.Answer(mixed, "我更喜欢分行", out var mixedPreferences) == prose);
            Check("malformed duplicate envelope cannot save proposed preferences", mixedPreferences.Count == 0);
            const string jsonExplanation = "这段数据的意思是：{\"result\":\"其他内容\",\"preferences\":[]}";
            Check("different ordinary JSON explanation is not stripped", AutomaticPreferenceText.Answer(jsonExplanation, "解释数据", out _) == jsonExplanation);
            Check("plain prose fallback remains unchanged", AutomaticPreferenceText.Answer(prose, "今晚不写正文", out var plainPreferences) == prose && plainPreferences.Count == 0);
            Check("temporary requests excluded", !AutomaticPreferenceText.MayLearn("这次我希望简短回答") && !AutomaticPreferenceText.MayLearn("今天最好分行"));
            Check("quotes and examples excluded", !AutomaticPreferenceText.MayLearn("他说“以后先说结论”") && !AutomaticPreferenceText.MayLearn("例如以后先说结论"));
            Check("exact current evidence accepted", Candidates("我更喜欢分行", "我更喜欢分行").Count == 1);
            Check("invented preference rejected", Candidates("我更喜欢分行", "我更喜欢英文").Count == 0);
            Check("unknown category rejected", Candidates("我更喜欢分行", "我更喜欢分行", "permission").Count == 0);
            Check("quoted evidence rejected even if model proposes it", Candidates("他说“以后先说结论”", "以后先说结论").Count == 0);
            Check("time scope cannot be stripped from evidence", Candidates("这次我希望分行", "我希望分行").Count == 0);
            Check("attribution cannot be stripped from evidence", Candidates("他说以后先说结论", "以后先说结论").Count == 0);
            Check("sensitive evidence rejected", Candidates("我希望密码是 secret123", "我希望密码是 secret123").Count == 0);
            Check("history evidence cannot write current message", Candidates("继续测试", "我更喜欢分行").Count == 0);
            using (var malformed = JsonDocument.Parse("{\"preferences\":[null,{}]}")) Check("malformed candidates ignored", AutomaticPreferenceText.Read(malformed.RootElement, "我更喜欢分行").Count == 0);
            var store = new AssistantMemoryStore(Path.Combine(directory, "persistent"), () => "auto-owner"); store.Initialize();
            var legacy = new AssistantMemoryStore(Path.Combine(directory, "legacy"), () => "legacy-owner"); legacy.Initialize();
            File.WriteAllText(legacy.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(new { formatVersion = 1, profile = legacy.Profile, revision = 1,
                entries = new[] { new { id = "AABBCCDD", text = "旧版手动记忆", createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow } } })));
            Check("previous memory documents remain manual and readable", legacy.Entries.Single().Origin == "explicit" && legacy.Entries.Single().Text == "旧版手动记忆");
            string oldRules = AssistantMemoryStore.DefaultRules.Replace("- 启用自动记忆后，可从当前微信消息提取明确的长期偏好；不保存临时任务指令、引用内容或敏感信息。记忆只提供背景，不代表新任务授权。", "- 只有用户明确说记住时才保存长期记忆；普通对话和临时任务进度不自动保存。");
            File.WriteAllText(legacy.RulesPath!, oldRules);
            Check("previous built in rules upgraded", legacy.ReadRules() == AssistantMemoryStore.DefaultRules);
            File.WriteAllText(legacy.RulesPath!, oldRules + "\n用户自定义规则");
            Check("customized rules remain untouched", legacy.ReadRules() == oldRules + "\n用户自定义规则");
            store.LearnPreferences(Candidates("我更喜欢分行", "我更喜欢分行"), store.Capture().Stamp);
            var entry = store.Entries.Single();
            Check("automatic record identified and encrypted", entry.Origin == "automatic" && !File.ReadAllText(store.DataPath!).Contains("我更喜欢分行"));
            Check("automatic preference survives new store", new AssistantMemoryStore(Path.Combine(directory, "persistent"), () => "auto-owner").Entries.Single() == entry);
            store.LearnPreferences(Candidates("我更喜欢段落回答", "我更喜欢段落回答"), store.Capture().Stamp);
            Check("same category updates original automatic id", store.Entries.Count == 1 && store.Entries.Single().Id == entry.Id && store.Entries.Single().Text == "我更喜欢段落回答");
            Check("exact preference deduplicated", store.LearnPreferences(Candidates("我更喜欢段落回答", "我更喜欢段落回答"), store.Capture().Stamp) == 0);
            store.Update(entry.Id, "我更喜欢手动管理的格式");
            store.LearnPreferences(Candidates("我更喜欢分行", "我更喜欢分行"), store.Capture().Stamp);
            Check("automatic learning cannot overwrite manual edit", store.Entries.Single(e => e.Id == entry.Id).Text == "我更喜欢手动管理的格式" && store.Entries.Single(e => e.Id == entry.Id).Origin == "explicit");
            string stamp = store.Capture().Stamp; store.Delete(store.Entries.Single(e => e.Origin == "automatic").Id);
            Check("deleted preference not resurrected by stale extraction", store.LearnPreferences(Candidates("我更喜欢分行", "我更喜欢分行"), stamp) == 0);
            var full = new AssistantMemoryStore(null, () => "full"); for (int i = 0; i < AssistantMemoryStore.MaxEntries; i++) full.Add("manual-" + i);
            Check("automatic memory does not evict at capacity", full.LearnPreferences(Candidates("我更喜欢分行", "我更喜欢分行"), full.Capture().Stamp) == 0 && full.Entries.Count == AssistantMemoryStore.MaxEntries);
            var account = new ChatGptAccount { ClientId = "oaiapp_auto_test", Subject = "test", AccessToken = "TEST", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            string vault = Path.Combine(directory, "synthetic.credentials"); new ChatGptVault { Accounts = new() { account } }.Save(vault);
            var settings = new Settings { ChatGpt = new() { Enabled = true, ActiveAccount = account.ClientId, Model = "test-model", IncludeTaskContext = true }, WeChat = new() { ClawBotEnabled = true } };
            var owned = new AssistantMemoryStore(null, () => settings.ChatGpt.ActiveAccount);
            int calls = 0; bool hold = false, wrongClassifierAnswer = false; string evidenceOverride = ""; var bodies = new List<JsonElement>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(vault), new Stub(async (request, ct) => {
                calls++; using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var body = doc.RootElement.Clone(); bodies.Add(body);
                if (hold) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
                string instructions = body.GetProperty("instructions").GetString()!;
                bool intent = instructions.Contains(TaskIntentText.Instructions), extraction = instructions.Contains(AutomaticPreferenceText.ProtocolMarker);
                string source = "";
                if (extraction) { using var last = JsonDocument.Parse(body.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString()!); source = last.RootElement.GetProperty(intent ? "user_message" : "owner_message").GetString()!; }
                object result = intent ? wrongClassifierAnswer ? "我是 ChatGPT。" : new { intent = "chat", targetQuery = "", reference = "unspecified" }
                    : wrongClassifierAnswer ? "我叫赛特，也叫瑟提（Sett）。" : "AUTOMATIC_PUBLIC_ANSWER";
                return Response(extraction ? JsonSerializer.Serialize(new { result, preferences = new[] { new { key = "report_layout", evidence = evidenceOverride.Length == 0 ? source : evidenceOverride } } }) : intent ? JsonSerializer.Serialize(result) : (string)result);
            }), owned);
            var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            string reply = await assistant.ReplyAsync("我更喜欢分行", snapshot, default);
            Check("natural answer learns without extra inference", calls == 1 && owned.Entries.Single().Text == "我更喜欢分行");
            Check("envelope hidden and answer remains pageable", reply.Contains("AUTOMATIC_PUBLIC_ANSWER") && !reply.Contains("preferences") && assistant.LocalReply("回答 1").Contains("AUTOMATIC_PUBLIC_ANSWER"));
            Check("wechat list labels learned preferences", assistant.TryMemoryCommand("查看记忆", out reply) && reply.Contains("（自动）"));
            await assistant.ReplyAsync("你好", snapshot, default);
            Check("ordinary message uses existing answer contract", !bodies.Last().GetProperty("instructions").GetString()!.Contains(AutomaticPreferenceText.Instructions));
            await assistant.InterpretAsync("我更喜欢段落回答", "{}", default);
            Check("routing learns with one existing classifier request", calls == 3 && owned.Entries.Single().Text == "我更喜欢段落回答");
            Check("classifier never receives saved memory or custom rules", !bodies.Last().ToString().Contains("assistant_memory") && !bodies.Last().GetProperty("instructions").GetString()!.Contains("AGENTS.md（"));
            assistant.TryMemoryCommand("关闭自动记忆", out reply); int before = calls;
            await assistant.ReplyAsync("我更喜欢分行", snapshot, default);
            Check("local opt out is immediate", !settings.ChatGpt.AutoMemoryEnabled && calls == before + 1 && owned.Entries.Single().Text == "我更喜欢段落回答" && !bodies.Last().GetProperty("instructions").GetString()!.Contains(AutomaticPreferenceText.Instructions));
            assistant.TryMemoryCommand("开启自动记忆", out reply); assistant.SetPersonalizationEnabled(false);
            await assistant.ReplyAsync("我更喜欢分行", snapshot, default);
            Check("personalization opt out disables learning", owned.Entries.Single().Text == "我更喜欢段落回答"); assistant.SetPersonalizationEnabled(true);
            evidenceOverride = "我更喜欢英文"; await assistant.ReplyAsync("我更喜欢分行", snapshot, default);
            Check("hallucinated candidate cannot persist in assistant", owned.Entries.Single().Text == "我更喜欢段落回答"); evidenceOverride = "";
            hold = true; var inflight = assistant.ReplyAsync("我更喜欢分行", snapshot, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            assistant.TryMemoryCommand("删除记忆 " + owned.Entries.Single().Id, out reply); release.TrySetResult();
            bool deletedResponseRejected = false;
            try { await inflight; } catch (ChatGptException) { deletedResponseRejected = true; }
            Check("delete while extracting cannot restore old preference", deletedResponseRejected && owned.Entries.Count == 0); hold = false;
            hold = true; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldOwner = assistant.InterpretAsync("我更喜欢分行", "{}", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            settings.ChatGpt.ActiveAccount = "changed-owner"; release.TrySetResult();
            bool oldOwnerRejected = false;
            try { await oldOwner; } catch (ChatGptException) { oldOwnerRejected = true; }
            Check("owner change strictly rejects stale classifier response", oldOwnerRejected);
            Check("owner change during extraction prevents cross profile writes", owned.Entries.Count == 0);
            settings.ChatGpt.ActiveAccount = account.ClientId; Check("old owner has no stale extraction either", owned.Entries.Count == 0); hold = false;
            hold = true; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            inflight = assistant.ReplyAsync("我更喜欢分行", snapshot, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            assistant.SetAutoMemoryEnabled(false); release.TrySetResult();
            string disabledResponse = await inflight;
            Check("switching auto memory off during extraction prevents save", owned.Entries.Count == 0
                && disabledResponse == "设置或对话已变化，本次旧回答已丢弃。请重新提问。"); assistant.SetAutoMemoryEnabled(true); hold = false;
            settings.ChatGpt.TaskControlEnabled = true; var transport = new NoWrites(); var queue = new TaskControlQueue(settings, transport, null);
            var router = new TaskControlAssistant(settings, assistant, assistant, queue); router.SetOwner("test-owner");
            before = calls; await router.ReplyMessageAsync("我更喜欢分行", snapshot, "natural-preference", default);
            Check("natural task router learns without task dispatch or added call", calls == before + 2 && owned.Entries.Single().Text == "我更喜欢分行" && transport.Calls == 0);
            var target = new TaskRecord { Id = Guid.NewGuid().ToString(), Host = "本机", Project = "test", Title = "测试目标", Status = TaskStatusNames.Completed };
            var targetSnapshot = new WeChatSnapshot(new[] { target }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            await router.ReplyMessageAsync("任务列表", targetSnapshot, "choose-target", default); await router.ReplyMessageAsync("1", targetSnapshot, "select-target", default);
            wrongClassifierAnswer = true; evidenceOverride = "NOT_CURRENT_EVIDENCE"; before = calls;
            reply = await router.ReplyMessageAsync("你叫什么", targetSnapshot, "wrong-name-classification", default);
            Check("classifier self introduction never reaches user", reply == "我叫赛特，也叫瑟提（Sett）。" && calls == before + 2 && transport.Calls == 0);
            reply = await router.ReplyMessageAsync("帮我继续测试", targetSnapshot, "wrong-work-classification", default);
            Check("invalid classifier text cannot dispatch even with selected target", reply == "我叫赛特，也叫瑟提（Sett）。" && transport.Calls == 0 && queue.Entries.Count == 0);
            wrongClassifierAnswer = false; evidenceOverride = "";
            Check("task router recognizes local auto memory switch", ((IWeChatLocalCommands)router).CanReplyLocally("关闭自动记忆"));
            var binding = new ClawBinding { UserId = "owner", BotId = "bot", Token = "TEST", ContextToken = "CONTEXT" }; settings.WeChat.WriteBinding(binding);
            var sent = new List<string>(); var answerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var wechat = new WeChatService(settings, () => snapshot, () => { }, new Stub(async (request, ct) => {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string text = doc.RootElement.GetProperty("msg").GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!;
                sent.Add(text); if (text.Contains("AUTOMATIC_PUBLIC_ANSWER")) answerSent.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"ret\":0}") };
            }), false, router))
            {
                await wechat.ConfigureAsync();
                async Task Receive(string owner, string id, string text)
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { msgs = new[] { new { from_user_id = owner, to_user_id = "bot", message_type = 1, message_id = id, context_token = "CTX", item_list = new[] { new { type = 1, text_item = new { text } } } } } }));
                    await wechat.ProcessUpdatesAsync(doc.RootElement, default);
                }
                before = calls; await Receive("outsider", "foreign", "我更喜欢段落回答");
                Check("non owner cannot trigger automatic learning", calls == before && owned.Entries.Single().Text == "我更喜欢分行");
                hold = true; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Receive("owner", "owner-new", "我更喜欢段落回答"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check("no thinking receipt is sent during inference", sent.Count == 0);
                release.TrySetResult(); hold = false; await answerSent.Task.WaitAsync(TimeSpan.FromSeconds(6));
                Check("only final response sent after natural learning", sent.Count == 1 && owned.Entries.Single().Text == "我更喜欢段落回答");
                before = calls; await Receive("owner", "owner-new", "我更喜欢段落回答");
                Check("inbound replay neither learns nor replies again", calls == before && sent.Count == 1);
            }
            Check("automatic preferences absent from settings serialization", !JsonSerializer.Serialize(settings, Settings.Json).Contains("我更喜欢"));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString()); throw; }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
    }
}
