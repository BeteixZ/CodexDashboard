using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class CompletionSummaryTests
{
    private const string Generated = "目录读取这轮修过了，COMPLETED_ONLY。报告记着 5 项通过、1 项失败，后台长期运行还没验证。失败项得继续修，手机送达也得实测。";
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class BrokenResponse : Stream
    {
        private readonly MemoryStream prefix = new(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"PRIVATE_PARTIAL\"}\n\n"));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => prefix.Position < prefix.Length ? prefix.Read(buffer, offset, count) : throw Failure();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => prefix.Position < prefix.Length ? prefix.ReadAsync(buffer, ct) : ValueTask.FromException<int>(Failure());
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) prefix.Dispose(); base.Dispose(disposing); }
    }
    private static HttpRequestException Failure() => new(HttpRequestError.ConnectionError, "PRIVATE_NETWORK_PAYLOAD", new SocketException((int)SocketError.ConnectionReset));
    private static ChatGptException ApiFailure(HttpStatusCode status, string code = "")
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { error = new { code, message = "PRIVATE_API_PAYLOAD" } }));
        return ChatGptAssistant.ApiError(status, payload.RootElement);
    }
    private sealed class Summarizer : ICompletionSummarizer
    {
        public bool CompletionSummariesEnabled { get; set; } = true;
        public int Calls;
        public string ReadText = "";
        public Func<CancellationToken, Task>? Before;
        public bool CopyReport;
        public async Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken ct)
        { Calls++; ReadText = report.Text; if (Before != null) await Before(ct); return new(CopyReport ? report.Text : Generated, "test-model", true, "", 1, Dependencies: Assistant.Core.OutputDependencies.Independent); }
    }
    private static TaskRecord Completed() => new() { Id = "task", TurnId = "turn", Host = "host", Project = "project", Title = "完成摘要检查", Status = TaskStatusNames.Completed,
        FinalReport = "本轮最终回复：5 项通过，1 项失败，后台稳定性未验证。", FinalReportTurnId = "turn", FinalReportSource = "本轮最终回复", CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static string Stream(string text) => "data: " + JsonSerializer.Serialize(new { type = "response.output_text.done", text })
        + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
    private static async Task<bool> Reject(Func<Task> action)
    { try { await action(); return false; } catch (ChatGptException) { return true; } }
    public static async Task RunAsync(Dictionary<string, bool> checks, string directory)
    {
        checks["completionSummaryPreservesNaturalAgentProse"] = CompletionSummaryText.Validate(Generated) == Generated;
        checks["legacySummaryLabelsRejected"] = await Reject(() => { CompletionSummaryText.Validate("做了什么：完成。\n验证结果：通过。\n遗留问题：未提及。"); return Task.CompletedTask; });
        checks["summaryEmptyOrIncompleteRejected"] = await Reject(() => { CompletionSummaryText.Validate(" "); return Task.CompletedTask; })
            && await Reject(() => { CompletionSummaryText.Validate("完成了。"); return Task.CompletedTask; });
        checks["summaryJsonAndListsRejected"] = await Reject(() => { CompletionSummaryText.Validate("{\"result\":\"完成了测试\"}"); return Task.CompletedTask; })
            && await Reject(() => { CompletionSummaryText.Validate("- 目录改好了，测试还没过。"); return Task.CompletedTask; });
        checks["completionSummaryOverlongNotCut"] = await Reject(() => { CompletionSummaryText.Validate(Generated + new string('字', 1000)); return Task.CompletedTask; });
        var task = Completed(); var report = WeChatReports.FromTask(task)!;
        checks["summaryCannotForwardUnchangedSource"] = await Reject(() => { CompletionSummaryText.Validate(Generated, report with { Text = Generated }); return Task.CompletedTask; });
        checks["punctuationChangesDoNotCountAsRewriting"] = await Reject(() => { CompletionSummaryText.Validate(Generated.Replace("。", "！"), report with { Text = Generated }); return Task.CompletedTask; });
        checks["summaryCannotForwardVerbatimLongExcerpt"] = await Reject(() => { CompletionSummaryText.Validate(Generated, report with { Text = "前面的报告段落。" + Generated + "后面的报告段落。" }); return Task.CompletedTask; });
        checks["summaryShortLimitCannotBeBypassed"] = await Reject(() => { CompletionSummaryText.Validate(new string('字', 301)); return Task.CompletedTask; });
        string partialText = "目录读取修过了，但报告只提供了部分内容，失败项还得继续处理。";
        checks["partialReportProseNotGivenDuplicateFooter"] = CompletionSummaryText.Body(report with { Truncated = true }, new(partialText, "test", true, "")) == partialText;
        string partialAlternative = "目录读取修好了，但内容只显示到部分，还有多少问题暂时没法确认。";
        checks["partialContentWordingNotGivenDuplicateFooter"] = CompletionSummaryText.Body(report with { Truncated = true }, new(partialAlternative, "test", true, "")) == partialAlternative;
        checks["partialReportOmissionStillQualifiedNaturally"] = CompletionSummaryText.Body(report with { Truncated = true }, new(Generated, "test", true, "")).EndsWith("报告只拿到一部分，细节可能有遗漏。");
        const string genericFailure = "这轮处理结束了，但摘要暂时没整理好。发“报告”查看原文。";
        checks["legacySummaryFailureNeverRendersArbitraryDiagnosticReason"] = CompletionSummaryText.Body(report,
            new("", "PRIVATE_MODEL", false, "PRIVATE_API_PAYLOAD HTTP 200 摘要校验细节")) == genericFailure;
        checks["summaryFailureClassificationDoesNotGuessFromExceptionText"] =
            CompletionSummaryText.FailureFrom(new ChatGptException("ChatGPT 额度已达限制。")) == CompletionSummaryFailureKind.GenerationFailed
            && CompletionSummaryText.FailureFrom(ApiFailure(HttpStatusCode.TooManyRequests, "subscription_sharing_usage_limit_exceeded")) == CompletionSummaryFailureKind.UsageLimit;
        string longText = "HEAD" + new string('字', 31_000) + "OMITTED_SECRET" + new string('字', 31_000) + "TAIL_UNRESOLVED";
        using (var json = JsonDocument.Parse(CompletionSummaryText.Input(report with { Text = longText })))
        {
            string text = json.RootElement.GetProperty("report").GetString()!;
            checks["summaryLongReportRetainsEndAndTruncation"] = json.RootElement.GetProperty("truncated").GetBoolean() && text.Contains("HEAD") && text.Contains("TAIL_UNRESOLVED") && !text.Contains("OMITTED_SECRET") && text.Length < 33_000;
        }
        await CheckModelIsolationAsync(checks, directory, report);
        await CheckRewriteAsync(checks, directory, report);
        await CheckNetworkRecoveryAsync(checks, directory, report);
        await CheckPipelineAsync(checks, "generated", new(), task);
        await CheckPipelineAsync(checks, "missing", new(), Completed().WithoutFinal());
        await CheckPipelineAsync(checks, "failed", new() { Before = _ => throw ApiFailure(HttpStatusCode.TooManyRequests, "subscription_sharing_usage_limit_exceeded") }, Completed());
        await CheckPipelineAsync(checks, "authorization", new() { Before = _ => throw ApiFailure(HttpStatusCode.Unauthorized) }, Completed());
        await CheckPipelineAsync(checks, "api-unknown", new() { Before = _ => throw ApiFailure(HttpStatusCode.OK) }, Completed());
        await CheckPipelineAsync(checks, "network", new() { Before = _ => throw Failure() }, Completed());
        await CheckPipelineAsync(checks, "timeout", new() { Before = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) }, Completed());
        await CheckPipelineAsync(checks, "delayed", new(), Completed().WithoutFinal());
        await CheckPipelineAsync(checks, "newTurn", new(), Completed().WithoutFinal());
        await CheckPipelineAsync(checks, "both", new(), Completed());
        await CheckPipelineAsync(checks, "off", new() { CompletionSummariesEnabled = false }, Completed());
        await CheckPipelineAsync(checks, "rulesOff", new(), Completed());
        await CheckPipelineAsync(checks, "unavailable", new(), Completed());
        await CheckPipelineAsync(checks, "copied", new() { CopyReport = true }, Completed());
    }
    private static TaskRecord WithoutFinal(this TaskRecord task) { task.FinalReport = ""; task.FinalReportTurnId = ""; return task; }
    private static async Task CheckModelIsolationAsync(Dictionary<string, bool> checks, string directory, WeChatReport report)
    {
        string path = Path.Combine(directory, "summary-fake.credentials");
        var vault = new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId = "summary-client", Subject = "summary-user", AccessToken = "FAKE_ACCESS", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } } }; vault.Save(path);
        var settings = new Settings(); settings.ChatGpt.ActiveAccount = "summary-client"; settings.ChatGpt.Model = "test-model"; settings.ChatGpt.Enabled = true; settings.ChatGpt.CompletionSummariesEnabled = true;
        int calls = 0; bool invalid = false;
        using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(path), new Stub(async (request, ct) => {
            calls++; string body = await request.Content!.ReadAsStringAsync(ct); using var json = JsonDocument.Parse(body); var root = json.RootElement;
            bool summary = root.GetProperty("instructions").GetString()!.StartsWith(CompletionSummaryText.Instructions, StringComparison.Ordinal);
            if (summary)
                checks["summaryRequestOnlyOwnReportNoChatHistory"] = root.GetProperty("input").GetArrayLength() == 1 && !body.Contains("QUESTION_SECRET") && !body.Contains("PRIVATE_QA_TEXT") && body.Contains("reportId")
                    && !root.GetProperty("store").GetBoolean() && root.GetProperty("stream").GetBoolean() && !root.TryGetProperty("tools", out _);
            else if (calls > 2) checks["summaryNotInsertedIntoQuestionHistory"] = !body.Contains("COMPLETED_ONLY") && body.Contains("QUESTION_SECRET");
            return new(HttpStatusCode.OK) { Content = new StringContent(Stream(summary ? invalid ? "不合格式" : Generated : "PRIVATE_QA_TEXT"), Encoding.UTF8, "text/event-stream") };
        }));
        var snapshot = new WeChatSnapshot(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>());
        await assistant.ReplyAsync("问 QUESTION_SECRET", snapshot, CancellationToken.None);
        var result = await assistant.SummarizeAsync(report, CancellationToken.None);
        checks["summaryGeneratedWithChosenModel"] = result.Generated && result.Model == "test-model" && result.Text.Contains("1 项失败");
        checks["summaryDoesNotReplaceLastChatAnswer"] = assistant.LocalReply("回答 1").Contains("PRIVATE_QA_TEXT");
        await assistant.ReplyAsync("问 后续问题", snapshot, CancellationToken.None);
        settings.ChatGpt.CompletionSummariesEnabled = false; int before = calls;
        checks["summaryOptOutNoInference"] = await Reject(async () => await assistant.SummarizeAsync(report, CancellationToken.None)) && before == calls;
        settings.ChatGpt.CompletionSummariesEnabled = true; settings.ChatGpt.Enabled = false;
        checks["summaryIndependentOfQuestionAnswerSwitch"] = (await assistant.SummarizeAsync(report, CancellationToken.None)).Generated;
        invalid = true; checks["summaryBadModelFormatRejected"] = await Reject(async () => await assistant.SummarizeAsync(report, CancellationToken.None));
    }
    private static async Task CheckNetworkRecoveryAsync(Dictionary<string, bool> checks, string directory, WeChatReport report)
    {
        string path = Path.Combine(directory, "network-fake.credentials");
        new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId = "network-client", Subject = "network-user", AccessToken = "FAKE_ACCESS", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } } }.Save(path);
        var settings = new Settings(); settings.ChatGpt.ActiveAccount = "network-client"; settings.ChatGpt.Model = "test-model"; settings.ChatGpt.CompletionSummariesEnabled = true;
        int calls = 0; string mode = "transient";
        var payloads = new List<string>();
        CancellationTokenSource? cancel = null;
        using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(path), new Stub(async (request, ct) => {
            calls++; payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            if (calls == 1 || mode == "persistent")
            {
                if (mode == "changed") settings.ChatGpt.Model = "changed-model";
                if (mode == "cancel") cancel!.CancelAfter(30);
                if (mode == "stream") return new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenResponse()) };
                throw Failure();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Stream(mode == "copied" ? report.Text : Generated), Encoding.UTF8, "text/event-stream") };
        }));
        var result = await assistant.SummarizeAsync(report, default);
        checks["transientSummaryNetworkErrorRecoversOnce"] = calls == 2 && result.Generated && result.NetworkRetried && !result.RewriteRetried && result.ModelRequests == 2;
        checks["networkRetryUsesSameReadOnlyInputAndInstructions"] = payloads.Count == 2 && payloads[0] == payloads[1] && !payloads[1].Contains("\"tools\"");
        checks["networkFailureDiagnosticsAreStructuredAndPrivate"] = assistant.LastFailure?.HttpError == "ConnectionError" && assistant.LastFailure.SocketError == "ConnectionReset" && assistant.LastFailure.TransportPhase == "response_headers" && !JsonSerializer.Serialize(assistant.LastFailure, Settings.Json).Contains("PRIVATE_");
        mode = "persistent"; calls = 0; bool failed = false;
        try { await assistant.SummarizeAsync(report, default); } catch (HttpRequestException) { failed = true; }
        checks["persistentSummaryNetworkFailureStopsAfterTwoCalls"] = failed && calls == 2;
        mode = "copied"; calls = 0;
        checks["networkAndRewriteShareTwoCallLimit"] = await Reject(async () => await assistant.SummarizeAsync(report, default)) && calls == 2;
        mode = "changed"; calls = 0;
        checks["settingsChangeBeforeReconnectStopsSecondCall"] = await Reject(async () => await assistant.SummarizeAsync(report, default)) && calls == 1;
        settings.ChatGpt.Model = "test-model"; mode = "cancel"; calls = 0; cancel = new(); failed = false;
        using (cancel) { try { await assistant.SummarizeAsync(report, cancel.Token); } catch (OperationCanceledException) { failed = true; } }
        checks["summaryCancellationDuringBackoffStopsRetry"] = failed && calls == 1;
        mode = "stream"; calls = 0; result = await assistant.SummarizeAsync(report, default);
        checks["brokenStreamRetryDiscardsPartialSummary"] = calls == 2 && result.Text == Generated && !result.Text.Contains("PRIVATE_PARTIAL") && assistant.LastFailure?.TransportPhase == "response_stream";
        string expiredPath = Path.Combine(directory, "network-expired-fake.credentials");
        new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId = "network-client", Subject = "network-user", AccessToken = "FAKE_EXPIRED", RefreshToken = "FAKE_REFRESH", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) } } }.Save(expiredPath);
        int authCalls = 0, modelCalls = 0;
        using var authFailure = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(expiredPath, new Stub((_, _) => { authCalls++; throw Failure(); })), new Stub((_, _) => { modelCalls++; throw new InvalidOperationException(); }));
        failed = false;
        try { await authFailure.SummarizeAsync(report, default); } catch (HttpRequestException) { failed = true; }
        checks["summaryRetryNeverRepeatsAuthorizationNetworkRequest"] = failed && authCalls == 1 && modelCalls == 0 && authFailure.LastFailure?.TransportPhase == "authorization";
    }
    private static async Task CheckRewriteAsync(Dictionary<string, bool> checks, string directory, WeChatReport report)
    {
        string path = Path.Combine(directory, "rewrite-fake.credentials");
        new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId = "rewrite-client", Subject = "rewrite-user", AccessToken = "FAKE_ACCESS", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } } }.Save(path);
        var settings = new Settings(); settings.ChatGpt.ActiveAccount = "rewrite-client"; settings.ChatGpt.Model = "test-model"; settings.ChatGpt.CompletionSummariesEnabled = true;
        int calls = 0; string mode = "repair"; var payloads = new List<JsonElement>();
        using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(path), new Stub(async (request, ct) => {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); payloads.Add(body.RootElement.Clone());
            if (mode == "api-failure") return new(HttpStatusCode.BadGateway) { Content = new StringContent("{\"error\":{\"code\":\"unavailable\"}}") };
            if (mode == "account-change") settings.ChatGpt.Model = "changed";
            string text = mode == "repair" && calls == 2 ? Generated : report.Text;
            return new(HttpStatusCode.OK) { Content = new StringContent(Stream(text), Encoding.UTF8, "text/event-stream") };
        }));
        var result = await assistant.SummarizeAsync(report, default);
        checks["copiedFirstResultIsRewrittenOnce"] = calls == 2 && result.Text == Generated && result.ModelRequests == 2 && result.RewriteRetried;
        checks["rewriteUsesSameSourceWithoutPreviousAnswerOrChat"] = payloads.All(p => p.GetProperty("model").GetString() == "test-model" && p.GetProperty("input").GetArrayLength() == 1)
            && payloads.Last().GetProperty("instructions").GetString()!.Contains(CompletionSummaryText.RewriteInstructions);
        mode = "still-copied"; calls = 0;
        checks["unsatisfactoryRewriteStopsAfterTwoRequests"] = await Reject(async () => await assistant.SummarizeAsync(report, default)) && calls == 2;
        mode = "api-failure"; calls = 0;
        checks["summaryApiFailureDoesNotTriggerRewriteOrRetry"] = await Reject(async () => await assistant.SummarizeAsync(report, default)) && calls == 1;
        mode = "account-change"; calls = 0;
        checks["accountChangeStopsRewriteBeforeSecondRequest"] = await Reject(async () => await assistant.SummarizeAsync(report, default)) && calls == 1;
    }
    private static async Task CheckPipelineAsync(Dictionary<string, bool> checks, string mode, Summarizer summarizer, TaskRecord task)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true;
        settings.WeChat.WriteBinding(new() { Token = "FAKE", UserId = "owner", BotId = "bot", ContextToken = "CONTEXT" });
        if (mode == "both") { settings.WeChat.PushPlusEnabled = true; settings.WeChat.ProtectedPushPlusToken = LocalSecret.Protect("PUSH_TOKEN"); }
        if (mode == "rulesOff") settings.WeChat.NotifyCompleted = false;
        if (mode == "unavailable") settings.ChatGpt.CompletionSummariesEnabled = true;
        string body = ""; int sends = 0; var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WeChatService(settings, () => new(new[] { task }, DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body = json.RootElement.GetProperty("msg").GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!;
                sends++; if (mode != "both") sent.TrySetResult(true); return Json("{\"ret\":0}");
            }
            if (request.RequestUri!.Host.Contains("pushplus"))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (mode == "both") checks["pushCompletionContainsNoTaskMetadata"] = json.RootElement.GetProperty("title").GetString() == WeChatText.Clean(Generated, 100)
                    && !json.RootElement.GetProperty("content").GetString()!.Contains("状态：") && !json.RootElement.GetProperty("content").GetString()!.Contains("host / project");
                sends++; sent.TrySetResult(true); return Json("{\"code\":200}");
            }
            return Json("{\"ret\":0}");
        }), receiveMessages: false, summarizer: mode == "unavailable" ? null : summarizer) { SummaryTimeout = TimeSpan.FromMilliseconds(mode == "timeout" ? 30 : 1500), ReportWaitTimeout = TimeSpan.FromMilliseconds(mode is "delayed" or "newTurn" ? 350 : 0) };
        CompletionSummary? prepared = null;
        service.CompletionPrepared += result => prepared = result;
        await service.ConfigureAsync(); string original = task.FinalReport;
        service.Observe(task); service.Observe(task);
        if (mode == "rulesOff") { await Task.Delay(40); checks["completionNotifyOptOutNoSummaryCost"] = summarizer.Calls == 0 && sends == 0; return; }
        if (mode == "generated") { task.TurnId = "new"; task.FinalReportTurnId = "new"; task.FinalReport = "WRONG_TURN"; }
        if (mode is "delayed" or "newTurn")
        {
            await Task.Delay(40); if (mode == "newTurn") task.TurnId = "new";
            task.FinalReportTurnId = task.TurnId; task.FinalReport = mode == "delayed" ? "LATE_CORRECT_REPORT" : "WRONG_TURN";
        }
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(4)); await Task.Delay(30);
        switch (mode)
        {
            case "generated": checks["completionSummaryUsesImmutableTurn"] = summarizer.ReadText == original && body.Contains("COMPLETED_ONLY") && !body.Contains("WRONG_TURN");
                checks["completionSummaryContainsOnlyNaturalAgentContent"] = body == Generated;
                checks["completionSummaryNoMetadataOrQueryFooter"] = !body.Contains("Codex ·") && !body.Contains("host / project") && !body.Contains("完成摘要检查")
                    && !body.Contains("状态：") && !body.Contains("时间：") && !body.Contains("报告 ") && !body.Contains("不代表整个项目完成");
                checks["completionSummaryOmitsBrandingAndColumnLabels"] = !body.Contains("ChatGPT 摘要") && !body.Contains("test-model") && !body.Contains("做了什么：") && !body.Contains("验证结果：") && !body.Contains("遗留问题：");
                checks["completionSummaryDeduplicatedBeforeModelCall"] = summarizer.Calls == 1 && sends == 1;
                checks["preparedSummaryDiagnosticsContainCountsWithoutReportOrReply"] = service.LastPreparedCompletion is { Generated: true, ModelRequests: 1 }
                    && service.LastPreparedCompletion.SourceCharacters == original.Length && service.LastPreparedCompletion.PreparedCharacters == Generated.Length
                    && !JsonSerializer.Serialize(service.LastPreparedCompletion).Contains("COMPLETED_ONLY"); break;
            case "unavailable": checks["enabledButUnavailableSummaryCannotFallBackToOriginal"] = summarizer.Calls == 0
                && body == "这轮处理结束了，但现在暂时无法整理摘要。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.SummaryUnavailable, Reason: "摘要助手暂时不可用，未转发原始报告。" }; break;
            case "copied": checks["sendBoundaryRejectsVerbatimProviderOutput"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但摘要暂时没整理好。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.InvalidSummary, Reason: "摘要仍在照搬原报告，未完成提炼。" }; break;
            case "missing": checks["missingReportNaturalFallbackNoModelCost"] = summarizer.Calls == 0
                && body == "这轮处理结束了，但还没读到这轮的最终报告。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.ReportUnavailable, Reason: "尚未读取到对应轮次的公开最终报告。" }; break;
            case "failed": checks["failedSummaryStillSendsCompletion"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但当前可用额度已到上限，暂时没法整理摘要。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.UsageLimit }
                && prepared.Reason == ApiFailure(HttpStatusCode.TooManyRequests, "subscription_sharing_usage_limit_exceeded").Message; break;
            case "authorization": checks["summaryAuthorizationFailureIsActionableWithoutApiWrapper"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但连接授权需要在助手设置里检查，暂时没法整理摘要。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.Authorization }
                && prepared.Reason == ApiFailure(HttpStatusCode.Unauthorized).Message; break;
            case "api-unknown": checks["unknownApiFailureRetainsInternalReasonWithoutHttp200Notice"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但摘要暂时没整理好。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.GenerationFailed }
                && prepared.Reason == ApiFailure(HttpStatusCode.OK).Message && prepared.Reason.Contains("HTTP 200"); break;
            case "network": checks["summaryNetworkFailureDoesNotRenderRawPayload"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但连接暂时不可用，摘要还没拿到。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.Network }
                && prepared.Reason == ChatGptAssistant.Explain(Failure()) && !prepared.Reason.Contains("PRIVATE_NETWORK_PAYLOAD"); break;
            case "timeout": checks["timeoutSummaryStillSendsCompletion"] = summarizer.Calls == 1
                && body == "这轮处理结束了，但整理摘要等得太久，暂时没拿到结果。发“报告”查看原文。"
                && prepared is { FailureKind: CompletionSummaryFailureKind.Timeout, Reason: "生成摘要等待超时，已保留完成提醒。" }; break;
            case "delayed": checks["lateSameTurnReportUsed"] = summarizer.ReadText == "LATE_CORRECT_REPORT" && body == Generated; break;
            case "newTurn": checks["lateDifferentTurnNeverSummarized"] = summarizer.Calls == 0
                && body == "这轮处理结束了，但还没读到这轮的最终报告。发“报告”查看原文。"
                && prepared?.FailureKind == CompletionSummaryFailureKind.ReportUnavailable; break;
            case "both": checks["twoChannelsShareOneSummary"] = summarizer.Calls == 1 && sends == 2; break;
            case "off": checks["summaryOffPreservesPlainOverview"] = summarizer.Calls == 0 && body == task.OverviewText && !body.Contains("ChatGPT 摘要"); break;
        }
        if (prepared is { Generated: false })
        {
            using var diagnostics = JsonDocument.Parse(JsonSerializer.Serialize(service.LastPreparedCompletion));
            checks["summaryFailureDiagnosticsKeepTypedCauseWithoutReplyOrReason_" + mode] =
                service.LastPreparedCompletion?.FailureKind == prepared.FailureKind
                && diagnostics.RootElement.ValueKind == JsonValueKind.Object
                && !diagnostics.RootElement.TryGetProperty("Reason", out _)
                && !diagnostics.RootElement.TryGetProperty("Text", out _)
                && !diagnostics.RootElement.TryGetProperty("Model", out _)
                && sends == 1 && !body.Contains("ChatGPT", StringComparison.Ordinal) && !body.Contains("HTTP", StringComparison.Ordinal);
        }
    }
}
