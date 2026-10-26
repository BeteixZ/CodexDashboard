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

internal static class WeChatSmokeTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static TaskRecord TaskRecord(string status, string turn = "turn-1", double start = 1000) => new() { Id = "thread-1", TurnId = turn, Host = "machine", Project = "project", Title = "task", Status = status, StartedAt = start, CompletedAt = status == "completed" ? start + 5 : 0, LastActivity = start + 5, OneLineSummary = "PRIVATE_SUMMARY" };
    private static ClawBinding Binding() => new() { Token = "PRIVATE_BOT_TOKEN", BotId = "bot@im.bot", UserId = "owner@im.wechat", ContextToken = "PRIVATE_CONTEXT" };
    public static async Task RunAndSaveAsync(string directory)
    {
        var checks = new Dictionary<string, bool>();
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var initial = new NotificationTransitions();
        checks["initialLoadSilent"] = initial.Observe(new[] { TaskRecord("completed"), TaskRecord("waiting").WithId("thread-2") }, now).Count == 0;
        var tracker = new NotificationTransitions();
        tracker.Observe(new[] { TaskRecord("active") }, now);
        checks["completionTransition"] = tracker.Observe(new[] { TaskRecord("completed") }, now.AddSeconds(10)).Count == 1;
        checks["repeatCompletionSilent"] = tracker.Observe(new[] { TaskRecord("completed") }, now.AddSeconds(20)).Count == 0;
        checks["quickNextTurn"] = tracker.Observe(new[] { TaskRecord("completed", "turn-2", 1022) }, now.AddSeconds(30)).Count == 1;
        checks["quickNewThread"] = tracker.Observe(new[] { TaskRecord("completed", "turn-3", 1032).WithId("thread-2") }, now.AddSeconds(40)).Count == 1;
        var quiet = new NotificationTransitions(); quiet.Observe(new[] { TaskRecord("active") }, now);
        quiet.Observe(new[] { TaskRecord("unknown") }, now.AddMinutes(6));
        checks["longQuietCompletion"] = quiet.Observe(new[] { TaskRecord("completed") }, now.AddMinutes(7)).Count == 1;
        var recovery = new NotificationTransitions(); recovery.Observe(new[] { TaskRecord("active") }, now);
        var offline = TaskRecord("active"); offline.Disconnected = true; recovery.Observe(new[] { offline }, now.AddSeconds(10));
        checks["recoverySilent"] = recovery.Observe(new[] { TaskRecord("completed") }, now.AddSeconds(20)).Count == 0;
        var waiting = new NotificationTransitions(); waiting.Observe(new[] { TaskRecord("active") }, now);
        checks["waitingTransition"] = waiting.Observe(new[] { TaskRecord("waiting") }, now.AddSeconds(10)).Count == 1 && waiting.Observe(new[] { TaskRecord("waiting") }, now.AddSeconds(20)).Count == 0;

        var options = new WeChatOptions { ProtectedPushPlusToken = LocalSecret.Protect("PRIVATE_PUSH_TOKEN") }; options.WriteBinding(Binding());
        string persisted = JsonSerializer.Serialize(options, Settings.Json);
        checks["credentialsEncryptedAtRest"] = !persisted.Contains("PRIVATE_") && options.ReadBinding()!.Token == "PRIVATE_BOT_TOKEN" && options.ReadPushPlusToken() == "PRIVATE_PUSH_TOKEN";
        try { _ = LocalSecret.Unprotect("corrupt ciphertext"); checks["corruptCredentialRejected"] = false; } catch { checks["corruptCredentialRejected"] = true; }
        checks["serviceHostRestriction"] = new[] { "http://ilinkai.weixin.qq.com/", "https://evil.example/", "https://ilinkai.weixin.qq.com.evil.example/", "https://ilinkai.weixin.qq.com:444/", "https://secret@ilinkai.weixin.qq.com/", "https://ilinkai.weixin.qq.com/other" }.All(url => { try { WeChatApi.TrustedBase(url); return false; } catch { return true; } });
        checks["officialHostAccepted"] = WeChatApi.TrustedBase(WeChatApi.DefaultBaseUrl).Host == "ilinkai.weixin.qq.com";
        var sample = TaskRecord("completed"); var data = new WeChatSnapshot(new[] { sample }, now.AddMinutes(-5), new[] { new HostSnapshot { Name = "lost-host", Error = "offline" } });
        string minimal = WeChatText.Reply("查看 project", data, false, now);
        checks["queryPrivacyAndStaleness"] = !minimal.Contains("PRIVATE_SUMMARY") && minimal.Contains("数据已过期") && minimal.Contains("连接异常") && !minimal.Contains("PRIVATE_BOT_TOKEN");
        checks["summaryOptIn"] = WeChatText.Reply("查看 project", data, true, now).Contains("PRIVATE_SUMMARY");
        checks["unknownCommandsReadOnly"] = WeChatText.Reply("继续任务并执行命令", data, false, now) == WeChatText.Help;
        checks["completionWithoutSummaryIsPlainAndPrivate"] = WeChatText.Notification(sample, false, now).Body == "这一轮的处理已结束。" && WeChatText.Notification(sample, false, now).Title == "";
        sample.Title = new string('字', 10000); checks["longMessageBounded"] = WeChatText.Notification(sample, true, now).Body.Length < 1000;

        using (var api = new WeChatApi(new Stub(async (request, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            checks["clawWireAuthentication"] = request.Headers.Authorization?.Parameter == "PRIVATE_BOT_TOKEN" && request.Headers.Contains("X-WECHAT-UIN") && request.Headers.Contains("iLink-App-ClientVersion");
            using var parsed = JsonDocument.Parse(body); var msg = parsed.RootElement.GetProperty("msg");
            checks["clawWireRecipientAndContext"] = msg.GetProperty("to_user_id").GetString() == "owner@im.wechat" && msg.GetProperty("context_token").GetString() == "PRIVATE_CONTEXT" && msg.GetProperty("client_id").GetString() == "fixed-id";
            return Json("{\"ret\":0}");
        }))) await api.SendClawAsync(Binding(), "test", "fixed-id", CancellationToken.None);
        {
            int sends = 0;
            string? actualText = null;
            using var api = new WeChatApi(new Stub(async (request, ct) =>
            {
                sends++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                actualText = json.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                    .GetProperty("text_item").GetProperty("text").GetString();
                return Json("{\"ret\":0}");
            }));
            string exact = "  逐字保留😀\r\n行尾空格  \n\t";
            await api.SendClawAsync(Binding(), exact, "exact-page", CancellationToken.None, preserveText: true);
            checks["durableWirePreservesWhitespaceAndUnicodeExactly"] = sends == 1 && actualText == exact;
            int rejected = 0;
            foreach (var invalid in new[] { "", new string('字', 3501), "含\0控制符", "\ud800", "\udc00" })
            {
                try { await api.SendClawAsync(Binding(), invalid, "invalid-page", CancellationToken.None, preserveText: true); }
                catch (ClawTextRejectedException) { rejected++; }
            }
            checks["invalidDurableTextRejectedBeforeHttpWithoutTruncating"] = sends == 1 && rejected == 5 && actualText == exact;
            await api.SendClawAsync(Binding(), exact, "legacy-page", CancellationToken.None);
            checks["legacyWireCleaningBehaviorPreserved"] = sends == 2 && actualText == WeChatText.Clean(exact, 3500);
        }
        using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json("{\"ret\":-2,\"errmsg\":\"PRIVATE_BOT_TOKEN\"}")))))
        {
            try { await api.SendClawAsync(Binding(), "test", "id", CancellationToken.None); checks["expiredContextSurfaced"] = false; }
            catch (WeChatApiException ex) { checks["expiredContextSurfaced"] = ex.Code == -2 && WeChatApi.Explain(ex).Contains("发一条消息") && !WeChatApi.Explain(ex).Contains("PRIVATE_"); }
        }
        using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json("{}")))))
        {
            checks["missingAcceptanceRemainsUnconfirmed"] = await api.SendClawAsync(Binding(), "test", "id", CancellationToken.None) == null;
        }
        using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json("{\"ret\":0,\"errcode\":-14}")))))
        {
            try { using var response = await api.GetUpdatesAsync(Binding(), CancellationToken.None); checks["errcodeChecked"] = false; }
            catch (WeChatApiException ex) { checks["errcodeChecked"] = ex.Code == -14; }
        }
        using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json("{\"status\":\"scaned_but_redirect\",\"redirect_host\":\"evil.example\"}")))))
        {
            try { await api.PollLoginAsync(new(), null, CancellationToken.None); checks["qrRedirectRestricted"] = false; }
            catch (InvalidOperationException) { checks["qrRedirectRestricted"] = true; }
        }
        using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json("{\"status\":\"confirmed\",\"bot_token\":\"secret\",\"ilink_bot_id\":\"bot\"}")))))
        {
            try { await api.PollLoginAsync(new(), null, CancellationToken.None); checks["ownerIdentityRequiredAtLogin"] = false; }
            catch (InvalidOperationException) { checks["ownerIdentityRequiredAtLogin"] = true; }
        }
        using (var api = new WeChatApi(new Stub(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            checks["pushPlusHttpsAndFixedChannel"] = request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "www.pushplus.plus" && request.Headers.Authorization == null && json.RootElement.GetProperty("channel").GetString() == "wechat";
            return Json("{\"code\":200,\"data\":\"receipt\"}");
        }))) checks["pushPlusAcceptance"] = await api.SendPushPlusAsync("PRIVATE_PUSH_TOKEN", "title", "body", CancellationToken.None) == "receipt";

        await CheckPollEnvelopeAsync(checks);
        await CheckInboundAsync(checks);
        await CheckDeduplicationAsync(checks);
        await CheckFailureAndDisableAsync(checks);
        await CheckSenderOnlyAsync(checks);
        await CheckDeferredCompletionAsync(checks);
        await CheckContextRefreshDuringSendAsync(checks);
        WeChatReportSmokeTests.Run(checks);
        await CheckReportInboundAsync(checks);
        bool passed = checks.Values.All(v => v);
        File.WriteAllText(Path.Combine(directory, "wechat-tests.json"), JsonSerializer.Serialize(new { passed, count = checks.Count, checks }, Settings.Json));
        if (!passed) throw new InvalidOperationException("微信回归检查未通过。");
    }
    private static TaskRecord WithId(this TaskRecord record, string id) { record.Id = id; return record; }
    internal static async Task<bool> RunPollEnvelopeChecksAsync(string directory)
    {
        var checks = new Dictionary<string, bool>();
        var observations = new List<object>();
        await CheckPollEnvelopeAsync(checks, observations);
        bool passed = checks.Values.All(value => value);
        File.WriteAllText(Path.Combine(directory, "poll-envelope-checks.json"),
            JsonSerializer.Serialize(new { passed, count = checks.Count, checks, observations }, Settings.Json));
        return passed;
    }
    private static async Task CheckPollEnvelopeAsync(Dictionary<string, bool> checks, List<object>? observations = null)
    {
        var envelopes = new (string Name, string? Value, bool Accepted)[]
        {
            ("omitted", null, true), ("null", "null", true), ("empty_array", "[]", true),
            ("array_null_member", "[null]", true), ("object", "{\"opaque\":\"PRIVATE_BAD_BATCH\"}", false),
            ("string", "\"PRIVATE_BAD_BATCH\"", false), ("number", "42", false), ("boolean", "true", false)
        };
        foreach (var envelope in envelopes)
        {
            string json = "{\"ret\":0,\"get_updates_buf\":\"next-envelope-cursor\""
                + (envelope.Value == null ? "" : ",\"msgs\":" + envelope.Value) + "}";
            bool accepted = false, safeFailure = false;
            using (var api = new WeChatApi(new Stub((_, _) => System.Threading.Tasks.Task.FromResult(Json(json)))))
            {
                try { using var response = await api.GetUpdatesAsync(Binding(), default); accepted = true; }
                catch (InvalidOperationException error)
                { safeFailure = !WeChatApi.Explain(error).Contains("PRIVATE_") && !WeChatApi.Explain(error).Contains("next-envelope-cursor"); }
            }
            checks["pollEnvelopeApi_" + envelope.Name] = accepted == envelope.Accepted && (accepted || safeFailure);

            var settings = new Settings(); settings.WeChat.ClawBotEnabled = true;
            var account = Binding(); account.BotId = "synthetic-envelope-" + Guid.NewGuid().ToString("N");
            account.Cursor = "original-envelope-cursor"; settings.WeChat.WriteBinding(account);
            int polls = 0, saves = 0, sends = 0;
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = new WeChatService(settings,
                () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()),
                () => saves++, new Stub(async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
                    {
                        if (++polls == 1) return Json(json);
                        observed.TrySetResult();
                        await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage")) sends++;
                    return Json("{\"ret\":0}");
                }));
            service.Changed += () =>
            {
                if (polls > 0 && (saves > 0 || service.ConnectionState == Codex.Protocol.ConnectionState.Disconnected))
                    observed.TrySetResult();
            };
            await service.ConfigureAsync();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var saved = settings.WeChat.ReadBinding()!;
            checks["pollEnvelopeReceiver_" + envelope.Name] = envelope.Accepted
                ? saved.Cursor == "next-envelope-cursor" && saves == 1 && saved.SeenMessages.Count == 0 && sends == 0
                : saved.Cursor == "original-envelope-cursor" && saves == 0 && saved.SeenMessages.Count == 0 && sends == 0
                    && service.ConnectionState == Codex.Protocol.ConnectionState.Disconnected
                    && !service.ClawStatus.Contains("PRIVATE_") && !service.ClawStatus.Contains("next-envelope-cursor");
            observations?.Add(new { shape = envelope.Name, apiAccepted = accepted, saved.Cursor, saves, sends,
                seenCount = saved.SeenMessages.Count, state = service.ConnectionState.ToString() });
        }
    }
    private static async Task CheckContextRefreshDuringSendAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextSaved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0, polls = 0;
        await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>()), () => {
            if (settings.WeChat.ReadBinding()?.ContextToken == "refreshed") contextSaved.TrySetResult(true);
        }, new Stub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
            {
                if (polls++ == 0)
                {
                    await firstStarted.Task.WaitAsync(ct);
                    return Json("{\"ret\":0,\"msgs\":[{\"from_user_id\":\"owner@im.wechat\",\"to_user_id\":\"bot@im.bot\",\"message_type\":1,\"message_id\":\"refresh-in-flight\",\"context_token\":\"refreshed\",\"item_list\":[{\"type\":1,\"text_item\":{\"text\":\"状态\"}}]}]}");
                }
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                sends++;
                if (sends == 1)
                {
                    firstStarted.TrySetResult(true);
                    await contextSaved.Task.WaitAsync(ct);
                    return Json("{\"ret\":-2}");
                }
            }
            return Json("{\"ret\":0}");
        }));
        service.NotificationAttempted += receipt => { if (receipt.Accepted == true) resumed.TrySetResult(true); };
        await service.ConfigureAsync(); service.Observe(TaskRecord("completed"));
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        checks["oldContextRejectionCannotBlockFreshInboundContext"] = sends == 3 && !JsonSerializer.Serialize(service.Diagnostics, Settings.Json).Contains("\"contextBlocked\": true");
    }
    private sealed class DeferredSummary : ICompletionSummarizer
    {
        public bool CompletionSummariesEnabled => true;
        public int Calls;
        public Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken cancellation)
        { Calls++; return System.Threading.Tasks.Task.FromResult(new CompletionSummary("这轮检查跑完了，还有一个失败项得继续处理，长期运行的表现尚未验证。", "mock", true, "", 1, Dependencies: Assistant.Core.OutputDependencies.Independent)); }
    }
    private static async Task CheckDeferredCompletionAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        settings.ChatGpt.CompletionSummariesEnabled = true;
        var summary = new DeferredSummary();
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipts = new List<WeChatReceipt>();
        var bodies = new List<string>();
        int sends = 0, polls = 0, completionSends = 0;
        await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
            {
                if (polls++ == 0)
                {
                    await refresh.Task.WaitAsync(ct);
                    return Json("{\"ret\":0,\"msgs\":[{\"from_user_id\":\"owner@im.wechat\",\"to_user_id\":\"bot@im.bot\",\"message_type\":1,\"message_id\":\"recover-completion\",\"context_token\":\"refreshed\",\"item_list\":[{\"type\":1,\"text_item\":{\"text\":\"状态\"}}]}]}");
                }
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                sends++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string body = json.RootElement.GetProperty("msg").GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!;
                bodies.Add(body);
                if (sends == 1) return Json("{\"ret\":-2}");
                if (body.Contains("这轮检查跑完了") && ++completionSends == 2) recovered.TrySetResult(true);
            }
            return Json("{\"ret\":0}");
        }), summarizer: summary);
        service.NotificationAttempted += receipt => { receipts.Add(receipt); if (receipt.Accepted == false) rejected.TrySetResult(true); };
        await service.ConfigureAsync();
        var first = TaskRecord("completed"); first.FinalReport = "本轮实际结果：通过五项，失败一项，长期运行未验证。"; first.FinalReportTurnId = first.TurnId;
        service.Observe(first);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var second = TaskRecord("completed", "turn-2"); second.FinalReport = first.FinalReport; second.FinalReportTurnId = second.TurnId;
        service.Observe(second); service.Observe(first);
        await System.Threading.Tasks.Task.Delay(50);
        string destination = Binding().BotId + ":" + Binding().UserId;
        checks["blockedCompletionDoesNotConsumeSendAttempt"] = !new[] { first, second }.Any(t => settings.WeChat.RecentAttempts.Contains(WeChatText.Hash("claw\n" + destination + "\n" + WeChatText.Notification(t, false, DateTimeOffset.UtcNow).Key)));
        checks["blockedCompletionWaitsWithoutRequestsOrExtraSummary"] = sends == 1 && summary.Calls == 1;
        refresh.TrySetResult(true);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await System.Threading.Tasks.Task.Delay(50);
        checks["rejectedAndBlockedCompletionsResumeOnce"] = completionSends == 2 && receipts.Count(r => r.Accepted == true) == 2;
        checks["recoveryReusesAlreadyPreparedSummary"] = summary.Calls == 2 && bodies.Count(b => b.Contains("这轮检查跑完了")) == 3;
        checks["notificationDiagnosticsContainNoCredentialsOrReport"] = !JsonSerializer.Serialize(service.Diagnostics, Settings.Json).Contains("PRIVATE_") && !JsonSerializer.Serialize(service.Diagnostics, Settings.Json).Contains(first.FinalReport);
    }
    private static async Task CheckInboundAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        int sends = 0, polls = 0; var replied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var messageJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            get_updates_buf = "next-cursor",
            msgs = new object?[]
            {
                null,
                new { from_user_id = "stranger", to_user_id = "bot@im.bot", message_id = "foreign", message_type = 1, group_id = "", context_token = "foreign", item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } },
                new { from_user_id = "owner@im.wechat", to_user_id = "another-bot", message_id = "other-bot", message_type = 1, group_id = "", context_token = "other", item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } },
                new { from_user_id = "owner@im.wechat", to_user_id = "bot@im.bot", message_id = "group", message_type = 1, group_id = "group", context_token = "group", item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } },
                new { from_user_id = "owner@im.wechat", to_user_id = "bot@im.bot", message_id = 9007199254740993L, message_type = 1, group_id = "", context_token = "fresh-context", item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } },
                new { from_user_id = "owner@im.wechat", to_user_id = "bot@im.bot", message_id = "9007199254740993", message_type = 1, group_id = "", context_token = "stale-replay-context", item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } },
                new { from_user_id = "owner@im.wechat", to_user_id = "bot@im.bot", message_id = "old", message_type = 1, group_id = "", context_token = "old-context", create_time_ms = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), item_list = new[] { new { type = 1, text_item = new { text = "状态" } } } }
            }
        }));
        await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
            {
                if (polls++ == 0) return Json(messageJson.RootElement.GetRawText());
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                sends++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                checks["inboundUsesFreshContext"] = body.RootElement.GetProperty("msg").GetProperty("context_token").GetString() == "fresh-context";
                replied.TrySetResult(true);
            }
            return Json("{\"ret\":0}");
        }));
        await service.ConfigureAsync(); await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await System.Threading.Tasks.Task.Delay(30);
        checks["onlyOwnerDirectMessagesAnswered"] = sends == 1;
        var state = settings.WeChat.ReadBinding()!;
        checks["cursorAndDedupSurviveRestart"] = state.Cursor == "next-cursor" && state.SeenMessages.Count == 2 && state.ContextToken == "fresh-context";
        checks["replayedAndOldContextsIgnored"] = state.ContextToken == "fresh-context" && sends == 1;
    }
    private static async Task CheckDeduplicationAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.PushPlusEnabled = true; settings.WeChat.ProtectedPushPlusToken = LocalSecret.Protect("PRIVATE_PUSH_TOKEN");
        int sends = 0; var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Stub Handler() => new((_, _) => { sends++; accepted.TrySetResult(true); return System.Threading.Tasks.Task.FromResult(Json("{\"code\":200,\"data\":\"receipt\"}")); });
        WeChatSnapshot Snapshot() => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>());
        await using (var service = new WeChatService(settings, Snapshot, () => { }, Handler()))
        {
            await service.ConfigureAsync(); service.Observe(TaskRecord("completed")); service.Observe(TaskRecord("completed"));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3)); await System.Threading.Tasks.Task.Delay(30);
            checks["automaticNotificationDeduplicated"] = sends == 1 && service.PushStatus.Contains("尚未确认手机送达");
        }
        await using (var service = new WeChatService(settings, Snapshot, () => { }, Handler()))
        {
            await service.ConfigureAsync(); service.Observe(TaskRecord("completed")); await System.Threading.Tasks.Task.Delay(30);
            checks["automaticDedupSurvivesRestart"] = sends == 1;
        }
    }
    private static async Task CheckFailureAndDisableAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        int sends = 0, polls = 0;
        var inbound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
            {
                if (polls++ == 0)
                {
                    await inbound.Task.WaitAsync(ct);
                    return Json("{\"ret\":0,\"msgs\":[{\"from_user_id\":\"owner@im.wechat\",\"to_user_id\":\"bot@im.bot\",\"message_type\":1,\"message_id\":\"refresh\",\"context_token\":\"refreshed\",\"item_list\":[{\"type\":1,\"text_item\":{\"text\":\"状态\"}}]}]}");
                }
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                sends++;
                if (sends == 1) return Json("{\"ret\":-2}");
                reply.TrySetResult(true);
            }
            return Json("{\"ret\":0}");
        })))
        {
            await service.ConfigureAsync();
            string first = await service.TestClawAsync(CancellationToken.None);
            string second = await service.TestClawAsync(CancellationToken.None);
            checks["expiredContextStopsRepeatedSends"] = sends == 1 && first.Contains("发一条消息") && second.Contains("会话凭据已失效");
            inbound.TrySetResult(true); await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
            checks["freshInboundRestoresSending"] = sends == 2 && settings.WeChat.ReadBinding()!.ContextToken == "refreshed";
            settings.WeChat.ClawBotEnabled = false; await service.ConfigureAsync();
            service.Observe(TaskRecord("completed")); await System.Threading.Tasks.Task.Delay(30);
            checks["disabledClawDoesNotSend"] = sends == 2;
        }
        var push = new Settings(); push.WeChat.PushPlusEnabled = true; push.WeChat.NotifyCompleted = false; push.WeChat.ProtectedPushPlusToken = LocalSecret.Protect("PRIVATE_PUSH_TOKEN");
        int pushSends = 0;
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pushService = new WeChatService(push, () => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>()), () => { }, new Stub((_, _) =>
        {
            pushSends++; rejected.TrySetResult(true); return System.Threading.Tasks.Task.FromResult(Json("{\"code\":900,\"msg\":\"PRIVATE_PUSH_TOKEN\"}"));
        }));
        await pushService.ConfigureAsync(); pushService.Observe(TaskRecord("completed")); await System.Threading.Tasks.Task.Delay(30);
        checks["notificationRulesApplied"] = pushSends == 0;
        pushService.Observe(TaskRecord("failed")); await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3)); await System.Threading.Tasks.Task.Delay(30);
        checks["providerFailureVisibleWithoutSecrets"] = pushSends == 1 && pushService.PushStatus.Contains("900") && !pushService.PushStatus.Contains("PRIVATE_");
    }
    private static async Task CheckSenderOnlyAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        int sends = 0, receives = 0;
        var attempted = new TaskCompletionSource<WeChatReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>()), () => { }, new Stub((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage")) sends++;
            else receives++;
            return System.Threading.Tasks.Task.FromResult(Json("{}"));
        }), receiveMessages: false);
        await service.ConfigureAsync(); service.NotificationAttempted += result => attempted.TrySetResult(result);
        service.Observe(TaskRecord("completed")); service.Observe(TaskRecord("completed"));
        var receipt = await attempted.Task.WaitAsync(TimeSpan.FromSeconds(3)); await System.Threading.Tasks.Task.Delay(30);
        checks["senderOnlyDoesNotCompeteForInboundMessages"] = sends == 1 && receives == 0;
        checks["unconfirmedDeliveryNotRetriedOrMisreported"] = receipt.Accepted == null && sends == 1 && service.ClawStatus.Contains("未提供确认状态");
    }
    private static async Task CheckReportInboundAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true; settings.WeChat.WriteBinding(Binding());
        var task = TaskRecord("completed"); task.FinalReport = "REPORT_START\n结果、改动、验证与产物。\nREPORT_END"; task.FinalReportTurnId = task.TurnId; task.FinalReportSource = "本轮最终回复";
        var replied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int polls = 0, sends = 0;
        await using var service = new WeChatService(settings, () => new(new[] { task }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates"))
            {
                if (polls++ == 0) return Json("{\"ret\":0,\"msgs\":[{\"from_user_id\":\"owner@im.wechat\",\"to_user_id\":\"bot@im.bot\",\"message_type\":1,\"message_id\":\"report-request\",\"context_token\":\"report-context\",\"item_list\":[{\"type\":1,\"text_item\":{\"text\":\"报告\"}}]}]}");
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                sends++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var msg = body.RootElement.GetProperty("msg"); string text = msg.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!;
                checks["inboundReportReachesOutgoingMessage"] = text.Contains("REPORT_START") && text.Contains("REPORT_END") && text.Contains(task.TurnId) && msg.GetProperty("to_user_id").GetString() == "owner@im.wechat";
                replied.TrySetResult(true);
            }
            return Json("{\"ret\":0}");
        }));
        await service.ConfigureAsync(); await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        checks["reportRequestSendsOnlyOnePage"] = sends == 1;
    }
}
