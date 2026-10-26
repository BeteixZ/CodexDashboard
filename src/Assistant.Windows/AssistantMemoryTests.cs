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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexDashboard;

internal static class AssistantMemoryTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class NoWrites : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct) { Calls++; throw new InvalidOperationException("unexpected task write"); }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Answer(string text) => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new {
        type = "response.completed", response = new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } }
    }) + "\n\n", Encoding.UTF8, "text/event-stream") };
    private static bool Reject(Action action) { try { action(); return false; } catch (ChatGptException) { return true; } }
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool ok) { checks[name] = ok; if (!ok) throw new InvalidOperationException(name); }
        string scope = "account-A/owner-A"; string root = Path.Combine(directory, "profiles-test");
        var store = new AssistantMemoryStore(root, () => scope);
        try
        {
            store.Initialize(); Check("creates editable AGENTS file", File.Exists(store.RulesPath) && store.ReadRules() == AssistantMemoryStore.DefaultRules);
            var first = store.Add("MEMORY_PRIVATE_A：以后汇报先说结论");
            Check("memory encrypted on disk", File.Exists(store.DataPath) && !File.ReadAllText(store.DataPath!).Contains("MEMORY_PRIVATE_A"));
            var restarted = new AssistantMemoryStore(root, () => scope);
            Check("entries survive store restart", restarted.Entries.Single() == first);
            Check("duplicate remembers do not multiply entries", restarted.Add(first.Text).Id == first.Id && restarted.Entries.Count == 1);
            store.SaveRules("STYLE_RULE_A：回答先写结论。\n");
            Check("rules survive restart", restarted.ReadRules().Contains("STYLE_RULE_A"));
            scope = "account-B/owner-A";
            Check("accounts isolate both memory and rules", store.Entries.Count == 0 && !store.ReadRules().Contains("STYLE_RULE_A"));
            store.Add("MEMORY_PRIVATE_B"); scope = "account-A/owner-B";
            Check("wechat owners isolate memory", store.Entries.Count == 0);
            scope = "account-A/owner-A"; var modified = restarted.Update(first.Id, "MEMORY_PRIVATE_A_UPDATED");
            Check("updates keep stable id and persist", modified.Id == first.Id && store.Entries.Single().Text == modified.Text);
            store.Delete(first.Id); Check("deletion survives restart", restarted.Entries.Count == 0);
            Check("invalid memory not silently truncated", Reject(() => store.Add(new string('x', AssistantMemoryStore.MaxEntryChars + 1))) && store.Entries.Count == 0);
            Check("invalid rules not silently truncated", Reject(() => store.SaveRules(new string('x', AssistantMemoryStore.MaxRulesChars + 1))) && store.ReadRules().Contains("STYLE_RULE_A"));
            string good = File.ReadAllText(store.DataPath!); File.WriteAllText(store.DataPath!, "corrupt-state");
            Check("corrupt memory is preserved and fails closed", Reject(() => _ = store.Entries) && File.ReadAllText(store.DataPath!) == "corrupt-state");
            File.WriteAllText(store.DataPath!, good);
            var clock = DateTimeOffset.UtcNow; var commands = new AssistantMemoryCommands(store, () => clock);
            commands.TryHandle("记住：报告先讲结论", out string reply);
            Check("wechat remember stores exact supplied preference", reply.Contains("已记住") && store.Entries.Single().Text == "报告先讲结论");
            string rememberedId = store.Entries.Single().Id;
            commands.TryHandle("查看记忆", out reply); Check("wechat list shows stable identifiers", reply.Contains(rememberedId) && reply.Contains("报告先讲结论"));
            commands.TryHandle("修改记忆 " + rememberedId + " 改为先说明验证范围", out reply);
            Check("wechat numbered update persists", reply.Contains("已修改") && store.Entries.Single().Text == "先说明验证范围");
            commands.TryHandle("把记忆 " + rememberedId + " 改为 先说明遗留问题", out reply);
            Check("natural update keeps literal new content", store.Entries.Single().Text == "先说明遗留问题");
            commands.TryHandle("删除记忆 " + rememberedId, out reply); Check("wechat numbered delete works", reply.Contains("已删除") && store.Entries.Count == 0);
            commands.TryHandle("记住以后使用中文回答", out reply); commands.TryHandle("忘掉刚才那条偏好", out reply);
            Check("recent explicit preference can be forgotten", reply.Contains("已删除") && store.Entries.Count == 0);
            var a = store.Add("报告A先写结论"); var b = store.Add("报告B先写限制");
            commands.TryHandle("删除记忆 报告", out reply);
            Check("ambiguous deletion asks before mutation", reply.Contains("尚未修改或删除") && store.Entries.Count == 2);
            commands.TryHandle("2", out reply); Check("memory choice only deletes selected stable entry", store.Entries.Single().Id == a.Id && reply.Contains(b.Id));
            b = store.Add("报告B先写限制"); commands.TryHandle("把记忆 报告 改为 新的报告偏好", out reply);
            commands.TryHandle("1", out reply); Check("ambiguous update retains pending replacement", store.Entries.Single(e => e.Id == a.Id).Text == "新的报告偏好");
            commands.TryHandle("删除记忆 报告", out reply); scope = "account-B/owner-A";
            Check("owner change discards old memory selection", !commands.CanHandle("1") && store.Entries.Single().Text == "MEMORY_PRIVATE_B");
            scope = "account-A/owner-A"; commands.TryHandle("删除记忆 报告", out reply); clock = clock.AddMinutes(11);
            Check("expired selection does not mutate records", !commands.CanHandle("1") && store.Entries.Count == 2);
            Check("malformed memory writes remain local", AssistantMemoryCommands.StartsCommand("修改记忆不完整") && AssistantMemoryCommands.StartsCommand("把记忆改成"));
            Check("ordinary questions and file operations are not memory writes", !AssistantMemoryCommands.StartsCommand("记住了吗？") && !AssistantMemoryCommands.StartsCommand("删除测试文件") && !AssistantMemoryCommands.StartsCommand("继续任务"));
            commands.TryHandle("查看规则", out reply); Check("rules are inspectable in wechat", reply.Contains("AGENTS.md") && reply.Contains("STYLE_RULE_A"));
            var bounded = new AssistantMemoryStore(null, () => "bounded");
            for (int i = 0; i < AssistantMemoryStore.MaxEntries; i++) bounded.Add("item-" + i);
            Check("entry bound enforced without dropping old records", Reject(() => bounded.Add("one-too-many")) && bounded.Entries.Count == AssistantMemoryStore.MaxEntries);
            var characterBound = new AssistantMemoryStore(null, () => "characters");
            for (int i = 0; i < 15; i++) characterBound.Add(i + new string('x', 1000));
            Check("total memory bound preserves existing entries", Reject(() => characterBound.Add(new string('y', 1200))) && characterBound.Entries.Count == 15);

            string vaultPath = Path.Combine(directory, "test.credentials");
            var account = new ChatGptAccount { ClientId = "oaiapp_memory_test", Subject = "test", AccessToken = "TEST_ACCESS", RefreshToken = "TEST_REFRESH", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            new ChatGptVault { Accounts = new() { account } }.Save(vaultPath);
            var settings = new Settings { ChatGpt = new() { Enabled = true, TaskControlEnabled = true, ActiveAccount = account.ClientId, Model = "test-model", CompletionSummariesEnabled = true }, WeChat = new() { ClawBotEnabled = true } };
            var owned = new AssistantMemoryStore(Path.Combine(directory, "inference-profile"), () => "test-owned");
            owned.SaveRules("CUSTOM_STYLE_SENTINEL：默认先说结论。"); var fact = owned.Add("FACT_SENTINEL：长期偏好。");
            var requests = new List<JsonElement>(); bool hold = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(vaultPath), new Stub(async (request, ct) => {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var body = json.RootElement.Clone(); requests.Add(body);
                if (hold) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
                string instruction = body.GetProperty("instructions").GetString()!;
                if (instruction == TaskIntentText.Instructions + TaskIntentText.QueryExamples) return Answer("{\"intent\":\"chat\",\"targetQuery\":\"\",\"reference\":\"unspecified\"}");
                return Answer(instruction.Contains(CompletionSummaryText.Instructions) ? "测试做完了，报告自述通过。还剩什么问题，报告没提。" : "PUBLIC_ANSWER");
            }), owned);
            var task = new TaskRecord { Id = Guid.NewGuid().ToString(), Title = "test task", Host = "本机", TurnId = "turn", Status = TaskStatusNames.Completed, FinalReport = "公开报告", FinalReportTurnId = "turn" };
            var snapshot = new WeChatSnapshot(new[] { task }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
            await assistant.ReplyAsync("问 测试", snapshot, default);
            var normal = requests.Last();
            await assistant.ReplyAsync("问 CONTEXT_FOLLOWUP", snapshot, default);
            var followup = requests.Last().GetProperty("input").EnumerateArray().ToArray();
            Check("conversation supplies both prior user text and unmodified agent reply", followup.Any(m => m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == "测试")
                && followup.Any(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("content").GetString() == "PUBLIC_ANSWER")
                && followup.Last().GetProperty("content").GetString()!.Contains("CONTEXT_FOLLOWUP", StringComparison.Ordinal));
            Check("rules injected into answer instructions", normal.GetProperty("instructions").GetString()!.Contains("CUSTOM_STYLE_SENTINEL"));
            Check("memory sent as data rather than instructions", normal.GetProperty("input").ToString().Contains("FACT_SENTINEL") && !normal.GetProperty("instructions").GetString()!.Contains("FACT_SENTINEL"));
            string answerRules = normal.GetProperty("instructions").GetString()!;
            Check("standing voice settings apply without promoting memory to authority", answerRules.Contains("同样是持续生效的交流设定") && answerRules.Contains("不能作为当前任务状态、新工作请求或授权"));
            Check("default prompts do not impose a particular character", !answerRules.Contains("赛特") && !answerRules.Contains("瑟提"));
            string voiceRules = AssistantVoiceDiagnostics.ConversationRules("CUSTOM_RULES_TO_KEEP");
            Check("voice rules preserve custom rules and remain idempotent", voiceRules.StartsWith("CUSTOM_RULES_TO_KEEP") && AssistantVoiceDiagnostics.ConversationRules(voiceRules) == voiceRules);
            Check("voice traits are conditional on current saved persona", voiceRules.Contains("只在当前记忆仍设定为赛特／瑟提时适用") && voiceRules.Contains("用户换了人设或临时要求不同语气时随之调整"));
            Check("voice section contains no scripted dialogue to copy", !voiceRules.Contains("→") && !voiceRules.Contains("用户：") && !voiceRules.Contains("等论文自己长出来") && !voiceRules.Contains("这句我收下了"));
            string oldVoiceRules = "CUSTOM_RULES_TO_KEEP\n" + AssistantVoiceDiagnostics.VoiceStart + "\n用户：旧问题 → 旧固定回答\n" + AssistantVoiceDiagnostics.VoiceEnd + "\nOTHER_RULES_TO_KEEP";
            string upgradedVoiceRules = AssistantVoiceDiagnostics.ConversationRules(oldVoiceRules);
            Check("voice upgrade removes old script and preserves surrounding rules", !upgradedVoiceRules.Contains("旧固定回答") && upgradedVoiceRules.StartsWith("CUSTOM_RULES_TO_KEEP") && upgradedVoiceRules.EndsWith("OTHER_RULES_TO_KEEP"));
            Check("incomplete voice section is preserved rather than overwritten", Reject(() => AssistantVoiceDiagnostics.ConversationRules("原有规则\n" + AssistantVoiceDiagnostics.VoiceStart)));
            owned.SaveRules(""); await assistant.ReplyAsync("问 只有记忆时的称呼", snapshot, default);
            Check("memory voice remains active even with empty AGENTS", requests.Last().GetProperty("instructions").GetString()!.Contains("同样是持续生效的交流设定") && requests.Last().GetProperty("input").ToString().Contains("FACT_SENTINEL"));
            owned.SaveRules("CUSTOM_STYLE_SENTINEL：默认先说结论。");
            Check("personalization does not introduce model tools or storage", !normal.TryGetProperty("tools", out _) && !normal.GetProperty("store").GetBoolean());
            await assistant.InterpretAsync("测试", "{}", default);
            Check("router never consumes user AGENTS or memory", requests.Last().GetProperty("instructions").GetString() == TaskIntentText.Instructions + TaskIntentText.QueryExamples && !requests.Last().ToString().Contains("FACT_SENTINEL"));
            await assistant.ReadProgressAsync("汇报进展", task, snapshot, default);
            Check("progress interpretation uses rules and memory", requests.Last().GetProperty("instructions").GetString()!.Contains("CUSTOM_STYLE_SENTINEL") && requests.Last().GetProperty("input").ToString().Contains("FACT_SENTINEL"));
            await assistant.SummarizeAsync(WeChatReports.FromTask(task)!, default);
            Check("completion summary uses saved style with factual boundaries", requests.Last().GetProperty("instructions").GetString()!.Contains(CompletionSummaryText.Instructions) && requests.Last().GetProperty("input").ToString().Contains("FACT_SENTINEL"));
            assistant.SetPersonalizationEnabled(false); await assistant.ReplyAsync("问 测试", snapshot, default);
            Check("personalization opt out omits both rules and memory", !requests.Last().ToString().Contains("CUSTOM_STYLE_SENTINEL") && !requests.Last().ToString().Contains("FACT_SENTINEL"));
            Check("personalization opt out omits standing voice instructions", requests.Last().GetProperty("instructions").GetString() == ChatGptAssistant.Instructions);
            assistant.SetPersonalizationEnabled(true); int calls = requests.Count;
            await assistant.ReplyAsync("查看记忆", snapshot, default); await assistant.ReplyAsync("新对话", snapshot, default);
            Check("local memory commands do not infer and new chat retains memories", requests.Count == calls && owned.Entries.Count == 1);
            hold = true; var inflight = assistant.ReplyAsync("问 使用旧记忆", snapshot, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            assistant.TryMemoryCommand("删除记忆 " + fact.Id, out reply); release.TrySetResult(); bool discarded = false;
            try { await inflight; } catch (ChatGptException ex) { discarded = ex.Message.Contains("旧回答已丢弃"); }
            Check("forget during generation invalidates stale answer", discarded && owned.Entries.Count == 0 && assistant.LocalReply("回答 1").Contains("尚无")); hold = false;
            var writer = new NoWrites(); var queue = new TaskControlQueue(settings, writer, null); var router = new TaskControlAssistant(settings, assistant, assistant, queue); router.SetOwner("owner");
            calls = requests.Count;
            await router.ReplyMessageAsync("记住：不会因为记忆调用任务工具", snapshot, "memory-msg", default);
            Check("memory command bypasses task intent and dispatch", requests.Count == calls && writer.Calls == 0 && queue.Entries.Count == 0 && owned.Entries.Count == 1);
            reply = await router.ReplyMessageAsync("1", snapshot, "expired-choice", default);
            Check("bare ordinal without pending choice cannot become task instruction", writer.Calls == 0 && reply.Contains("没有待选择"));

            var binding = new ClawBinding { UserId = "owner", BotId = "bot", Token = "TEST_TOKEN", ContextToken = "TEST_CONTEXT" }; settings.WeChat.WriteBinding(binding);
            var sentBodies = new List<string>();
            await using (var wechat = new WeChatService(settings, () => snapshot, () => { }, new Stub(async (request, ct) => { sentBodies.Add(await request.Content!.ReadAsStringAsync(ct)); return Json("{\"ret\":0}"); }), false, router))
            {
                await wechat.ConfigureAsync();
                async Task Receive(string owner, string text, string id)
                {
                    using var msg = JsonDocument.Parse(JsonSerializer.Serialize(new { msgs = new[] { new { from_user_id = owner, to_user_id = "bot", message_type = 1, message_id = id, context_token = "CTX", item_list = new[] { new { type = 1, text_item = new { text } } } } } }));
                    await wechat.ProcessUpdatesAsync(msg.RootElement, default);
                }
                int count = owned.Entries.Count; calls = requests.Count;
                await Receive("intruder", "记住：UNAUTHORIZED_MEMORY", "wrong-owner");
                Check("non-owner cannot write memory through wechat", owned.Entries.Count == count);
                await Receive("owner", "记住：WECHAT_SAVED_MEMORY", "owner-memory");
                Check("owner memory command goes through production receive path", owned.Entries.Any(e => e.Text == "WECHAT_SAVED_MEMORY") && requests.Count == calls && writer.Calls == 0);
                await Receive("owner", "记住：WECHAT_SAVED_MEMORY", "owner-memory");
                Check("replayed wechat message does not duplicate memory", owned.Entries.Count == count + 1);
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); hold = true;
                await Receive("owner", "问 开始等待生成", "waiting-answer"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                int sentBefore = sentBodies.Count;
                await Receive("owner", "记住：MEMORY_WHILE_GENERATING", "memory-during-generation");
                Check("wechat memory commands remain available during inference", owned.Entries.Any(e => e.Text == "MEMORY_WHILE_GENERATING"));
                release.TrySetResult();
                await wechat.DisposeAsync();
                Check("wechat never forwards stale generated answer after memory changes", !sentBodies.Skip(sentBefore).Any(s => s.Contains("PUBLIC_ANSWER")));
                hold = false;
            }
            using (var scopedAssistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(vaultPath), new Stub((_, _) => Task.FromResult(Answer("unused")))))
            {
                scopedAssistant.Memory.Add("STABLE_OWNER_FACT");
                binding.Token = "ROTATED_TEST_TOKEN"; settings.WeChat.WriteBinding(binding);
                Check("token rotation keeps same owners memory", scopedAssistant.Memory.Entries.Single().Text == "STABLE_OWNER_FACT");
                settings.ChatGpt.Model = "another-test-model";
                Check("model switch keeps long term memory scope", scopedAssistant.Memory.Entries.Count == 1); settings.ChatGpt.Model = "test-model";
                binding.UserId = "another-owner"; settings.WeChat.WriteBinding(binding);
                Check("production profile key isolates changed owner", scopedAssistant.Memory.Entries.Count == 0);
                binding.UserId = "owner"; settings.WeChat.WriteBinding(binding);
                Check("returning owner sees its own saved memory", scopedAssistant.Memory.Entries.Single().Text == "STABLE_OWNER_FACT");
            }
            Check("memory content is not added to status or settings", !JsonSerializer.Serialize(settings, Settings.Json).Contains("WECHAT_SAVED_MEMORY"));
            var preview = new AssistantMemoryWindow(assistant, settings); preview.Show(); preview.UpdateLayout();
            void Render(string file)
            {
                var bitmap = new RenderTargetBitmap((int)preview.ActualWidth, (int)preview.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
                using var image = File.Create(Path.Combine(directory, file)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(image);
            }
            Render("rules-editor.png"); preview.MemoryTab.IsSelected = true; preview.UpdateLayout(); Render("memory-editor.png"); preview.Close();
            Check("rules and memory editor rendered", true);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString()); throw; }
        finally { await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
    }
}
