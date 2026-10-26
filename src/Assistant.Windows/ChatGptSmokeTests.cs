using System;
using System.Collections.Generic;
using Microsoft.IdentityModel.JsonWebTokens;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace CodexDashboard;

internal static class ChatGptSmokeTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static readonly object Metadata = new { issuer = ChatGptAuth.Issuer, jwks_uri = ChatGptAuth.Issuer + "/.well-known/jwks.json", token_endpoint = ChatGptAuth.Issuer + "/api/accounts/oauth/token", revocation_endpoint = ChatGptAuth.Issuer + "/api/accounts/oauth/revoke" };
    private static ChatGptAccount Account() => new() { ClientId = "oaiapp_test", Subject = "subject", Email = "test@example.invalid", AccessToken = "TEST_ACCESS", RefreshToken = "TEST_REFRESH", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
    private static string Completed(string text) => "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output = new object[] {
        new { type = "reasoning", summary = new[] { new { text = "HIDDEN_REASONING" } } },
        new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } } }) + "\n\n";
    private static async Task<bool> Reject(Func<Task> action)
    { try { await action(); return false; } catch (ChatGptException) { return true; } }
    public static async Task RunAndSaveAsync(string directory)
    {
        var checks = new Dictionary<string, bool>();
        try
        {
            string selectedDirectory = Path.GetFullPath(Path.Combine(directory, "selected-profile"));
            string directorySelection = Path.Combine(directory, "test-data-directory.txt");
            checks["unconfiguredDataDirectoryKeepsDefault"] = Settings.ResolveDataDirectory(directorySelection, selectedDirectory) == selectedDirectory;
            File.WriteAllText(directorySelection, selectedDirectory + "\n", new UTF8Encoding(false));
            checks["explicitDataDirectoryPreservesExistingStoreLocation"] = Settings.ResolveDataDirectory(directorySelection, "unused-fallback") == selectedDirectory;
            File.WriteAllText(directorySelection, "relative-profile"); bool relativeRejected = false;
            try { Settings.ResolveDataDirectory(directorySelection, "unused-fallback"); } catch (InvalidDataException) { relativeRejected = true; }
            checks["relativeDataDirectoryCannotSilentlySwitchProfiles"] = relativeRejected;
            File.WriteAllText(directorySelection, new string('x', 9000)); bool oversizedRejected = false;
            try { Settings.ResolveDataDirectory(directorySelection, "unused-fallback"); } catch (InvalidDataException) { oversizedRejected = true; }
            checks["oversizedDataDirectorySelectionPreservedAndRejected"] = oversizedRejected && new FileInfo(directorySelection).Length == 9000;
            var a = Account();
            string verifier = ChatGptAuth.Random();
            var url = ChatGptAuth.Authorize("urn:uuid:123", "http://127.0.0.1:12345/auth/callback", "state", "nonce", verifier, null);
            var q = QueryHelpers.ParseQuery(url.Query);
            checks["officialOAuthAndPkce"] = url.Host == "auth.openai.com" && q["client_id"] == "dynamic_agent_client" && q["resource"] == ChatGptAuth.Resource && q["code_challenge"] == Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) && !url.Query.Contains(verifier);
            var returning = QueryHelpers.ParseQuery(ChatGptAuth.Authorize("urn:uuid:123", "http://127.0.0.1:10001/auth/callback", "state", "nonce", verifier, a).Query);
            checks["returningRegistrationReused"] = returning["client_id"] == a.ClientId && !returning.ContainsKey("agent_name_hint");
            var callback = new Dictionary<string, string> { ["state"] = "state", ["code"] = "code", ["client_id"] = a.ClientId };
            checks["issuedCallbackClientRequired"] = ChatGptAuth.ValidateCallback(callback, "state", null) == a.ClientId
                && await Reject(() => { ChatGptAuth.ValidateCallback(new Dictionary<string, string> { ["state"] = "state", ["code"] = "code" }, "state", null); return Task.CompletedTask; });
            checks["callbackWrongStateRejected"] = await Reject(() => { ChatGptAuth.ValidateCallback(callback, "different", null); return Task.CompletedTask; });
            checks["callbackChangedClientRejected"] = await Reject(() => { ChatGptAuth.ValidateCallback(callback, "state", "other"); return Task.CompletedTask; });
            checks["callbackErrorRejected"] = await Reject(() => { ChatGptAuth.ValidateCallback(new Dictionary<string, string> { ["state"] = "state", ["error"] = "access_denied" }, "state", null); return Task.CompletedTask; });

            using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
            string Token(string issuer = ChatGptAuth.Issuer, string audience = "oaiapp_test", string nonce = "nonce", bool expired = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
                Issuer = issuer, Audience = audience, Subject = new System.Security.Claims.ClaimsIdentity(new[] {
                    new System.Security.Claims.Claim("sub", "subject"), new System.Security.Claims.Claim("email", "test@example.invalid"), new System.Security.Claims.Claim("nonce", nonce) }),
                IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(10), SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256) });
            checks["signedIdentityValidated"] = ChatGptAuth.ValidateIdentity(Token(), a.ClientId, "nonce", new[] { key }).Subject == "subject";
            foreach (var variant in new[] { "issuer", "audience", "nonce", "expiry", "signature" })
                checks["rejectIdentity_" + variant] = await Reject(() => {
                    using var other = RSA.Create(2048);
                    ChatGptAuth.ValidateIdentity(Token(variant == "issuer" ? "https://invalid.example" : ChatGptAuth.Issuer, variant == "audience" ? "other" : a.ClientId, expired: variant == "expiry"), a.ClientId,
                        variant == "nonce" ? "wrong" : "nonce", variant == "signature" ? new[] { new RsaSecurityKey(other) } : new[] { key }); return Task.CompletedTask; });

            string vaultPath = Path.Combine(directory, "fake.credentials"); var vault = new ChatGptVault { Accounts = new() { a } }; vault.Save(vaultPath);
            checks["credentialsProtectedAndStableHost"] = !File.ReadAllText(vaultPath).Contains("TEST_ACCESS") && !File.ReadAllText(vaultPath).Contains(a.Email) && ChatGptVault.Load(vaultPath).HostId == vault.HostId;
            int refreshes = 0;
            a.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); vault.Save(vaultPath);
            using (var auth = new ChatGptAuth(vaultPath, new Stub(async (request, ct) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration")) return Json(Metadata);
                refreshes++; string form = await request.Content!.ReadAsStringAsync(ct);
                checks["refreshContract"] = form.Contains("grant_type=refresh_token") && form.Contains("client_id=oaiapp_test") && !form.Contains("dynamic_agent_client") && !form.Contains("scope=");
                await Task.Delay(30, ct);
                return Json(new { access_token = "NEW_ACCESS", refresh_token = "NEW_REFRESH", token_type = "Bearer", expires_in = 3600 });
            })))
            {
                await Task.WhenAll(auth.AccessTokenAsync(a.ClientId, CancellationToken.None), auth.AccessTokenAsync(a.ClientId, CancellationToken.None));
                checks["refreshSerializedAndAtomicallySaved"] = refreshes == 1 && ChatGptVault.Load(vaultPath).Accounts[0].RefreshToken == "NEW_REFRESH";
            }
            a.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); vault.Save(vaultPath);
            using (var auth = new ChatGptAuth(vaultPath, new Stub((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("openid-configuration") ? Json(Metadata) : new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("temporary") }))))
            {
                await Reject(async () => await auth.AccessTokenAsync(a.ClientId, CancellationToken.None));
                checks["temporaryFailurePreservesTokens"] = auth.Find(a.ClientId)!.RefreshToken == "TEST_REFRESH";
            }
            using (var auth = new ChatGptAuth(vaultPath, new Stub((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("openid-configuration") ? Json(Metadata) : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") }))))
            {
                checks["terminalRefreshRequiresNewLogin"] = await Reject(async () => await auth.AccessTokenAsync(a.ClientId, CancellationToken.None)) && auth.Find(a.ClientId)!.AccessToken.Length == 0 && auth.Find(a.ClientId)!.ClientId == a.ClientId;
            }

            async Task<string> Read(string s) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(s)); return await ChatGptAssistant.ReadResponseAsync(stream, CancellationToken.None); }
            checks["completePublicTextOnly"] = await Read(Completed("PUBLIC_FINAL")) == "PUBLIC_FINAL";
            const string compact = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
            checks["compactCompletionUsesBufferedPublicText"] = await Read("data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"PUBLIC_\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"FINAL\"}\n\n" + compact) == "PUBLIC_FINAL";
            checks["textDoneDoesNotDuplicateDeltas"] = await Read("data: {\"type\":\"response.output_text.delta\",\"delta\":\"FINAL\"}\n\ndata: {\"type\":\"response.output_text.done\",\"text\":\"FINAL\"}\n\n" + compact) == "FINAL";
            checks["reasoningStreamNeverReturned"] = await Read("data: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"HIDDEN\"}\n\ndata: {\"type\":\"response.output_text.done\",\"text\":\"FINAL\"}\n\n" + compact) == "FINAL";
            checks["commentaryStreamExcluded"] = await Read("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\"}}\n\ndata: {\"type\":\"response.output_text.done\",\"output_index\":0,\"text\":\"COMMENTARY\"}\n\ndata: {\"type\":\"response.output_text.done\",\"output_index\":1,\"text\":\"FINAL\"}\n\n" + compact) == "FINAL";
            checks["partialStreamNotSuccess"] = await Reject(async () => await Read("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
            checks["failureAfterDeltaNotSuccess"] = await Reject(async () => await Read("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"SECRET\"}}}\n\n"));
            checks["incompleteNotSuccess"] = await Reject(async () => await Read("data: {\"type\":\"response.incomplete\"}\n\n"));
            checks["oversizedLineBounded"] = await Reject(async () => await Read("data: " + new string('x', 512001)));
            using (var error = JsonDocument.Parse("{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"PRIVATE_ACCESS\"}}"))
                checks["limitsExplainedWithoutSecrets"] = ChatGptAssistant.ApiError(HttpStatusCode.TooManyRequests, error.RootElement).Message.Contains("settings/usage") && !ChatGptAssistant.ApiError(HttpStatusCode.TooManyRequests, error.RootElement).Message.Contains("PRIVATE_ACCESS")
                    && ChatGptAssistant.ApiError(HttpStatusCode.TooManyRequests, error.RootElement).Kind == ChatGptFailureKind.UsageLimit;
            checks["parseFailureDoesNotRecommendRelogin"] = !ChatGptAssistant.Explain(new JsonException("PRIVATE_ACCESS")).Contains("PRIVATE_ACCESS")
                && !ChatGptAssistant.Explain(new JsonException()).Contains("或重新登录");

            a.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Save(vaultPath);
            var settings = new Settings(); settings.ChatGpt.Enabled = true; settings.ChatGpt.ActiveAccount = a.ClientId; settings.ChatGpt.Model = "test-model";
            int calls = 0;
            var snapshot = new WeChatSnapshot(new[] { new TaskRecord { Id = "thread", TurnId = "current", FinalReportTurnId = "old", FinalReport = "OLD_FINAL_SECRET", Summary = "COMMENTARY_SECRET", Title = "任务" } }, DateTimeOffset.UtcNow.AddMinutes(-5), Array.Empty<HostSnapshot>());
            string context = ChatGptAssistant.Context("任务", snapshot);
            checks["contextExcludesWrongTurnsAndCommentary"] = !context.Contains("OLD_FINAL_SECRET") && !context.Contains("COMMENTARY_SECRET") && context.Contains("\"stale\":true");
            using var assistant = new ChatGptAssistant(settings, () => { }, new ChatGptAuth(vaultPath), new Stub(async (request, ct) => {
                checks["bearerSentOnlyToOfficialApi"] = request.RequestUri!.Host == "api.openai.com" && request.Headers.Authorization?.Parameter == "TEST_ACCESS";
                if (request.Method == HttpMethod.Get) return Json(new { catalogMetadata = new string('x', 300_000), models = new[] { new { slug = "test-model", display_name = "Test", visibility = "list" }, new { slug = "hidden", display_name = "Hidden", visibility = "hidden" } } });
                calls++; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = json.RootElement;
                checks["subscriptionInferenceContract"] = !root.GetProperty("store").GetBoolean() && root.GetProperty("stream").GetBoolean() && root.GetProperty("input").ValueKind == JsonValueKind.Array && !root.TryGetProperty("max_output_tokens", out _) && !root.TryGetProperty("tools", out _) && !root.TryGetProperty("previous_response_id", out _);
                return new(HttpStatusCode.OK) { Content = new StringContent(Completed(new string('字', 1400)), Encoding.UTF8, "text/event-stream") };
            }));
            assistant.ReportError(new JsonException("PRIVATE_ACCESS"), "wechat_message");
            checks["failureDiagnosticsExcludeRawPayloadAndExceptionMessage"] = assistant.LastFailure?.ExceptionType == nameof(JsonException)
                && assistant.LastFailure.Stage == "wechat_message" && !JsonSerializer.Serialize(assistant.LastFailure, Settings.Json).Contains("PRIVATE_ACCESS");
            checks["accountSpecificModelDiscovery"] = (await assistant.ModelsAsync(a.ClientId, CancellationToken.None)).Select(m => m.Slug).SequenceEqual(new[] { "test-model" });
            checks["largeModelCatalogAccepted"] = (await assistant.ModelsAsync(a.ClientId, CancellationToken.None)).Count == 1;
            settings.ChatGpt.Model = "";
            await assistant.EnsureModelAsync(CancellationToken.None);
            checks["authorizedEmptyModelInitialized"] = settings.ChatGpt.Model == "test-model" && calls == 0;
            settings.ChatGpt.Model = "user-selected";
            await assistant.EnsureModelAsync(CancellationToken.None);
            checks["explicitModelSelectionPreserved"] = settings.ChatGpt.Model == "user-selected";
            settings.ChatGpt.Model = "test-model";
            string reply = await assistant.ReplyAsync("问 测试", snapshot, CancellationToken.None);
            checks["generatedAnswerContainsOnlyFullAgentText"] = reply == new string('字', 1400) && calls == 1;
            checks["manualAnswerPageContainsOnlyAgentText"] = assistant.LocalReply("回答 2") == new string('字', 500);
            checks["newConversationClearsAnswer"] = assistant.LocalReply("新对话").Contains("已清空") && assistant.LocalReply("回答 2").Contains("尚无");
            assistant.Configure(a.ClientId, "test-model", false, true);
            checks["disabledDoesNotInfer"] = (await assistant.ReplyAsync("问 测试", snapshot, CancellationToken.None)).Contains("关闭") && calls == 1;
            int beforeProgressCalls = calls;
            checks["disabledProgressDoesNotInfer"] = (await assistant.ReadProgressAsync("汇报进展", snapshot.Tasks[0], snapshot, CancellationToken.None)).Contains("关闭") && calls == beforeProgressCalls;
            assistant.Configure(a.ClientId, "test-model", true, false);
            checks["progressHonorsContextOptOut"] = (await assistant.ReadProgressAsync("汇报进展", snapshot.Tasks[0], snapshot, CancellationToken.None)).Contains("上下文已关闭") && calls == beforeProgressCalls;
            assistant.Configure(a.ClientId, "test-model", true, true);
            string progress = await assistant.ReadProgressAsync("汇报进展", snapshot.Tasks[0], snapshot, CancellationToken.None);
            checks["progressContainsOnlyFullAgentText"] = progress == new string('字', 1400) && calls == beforeProgressCalls + 1;
            checks["progressAllowsReadingBusyOrStaleSnapshotWithContext"] = ChatGptAssistant.ProgressContext(snapshot.Tasks[0], snapshot).Contains("\"stale\":true");
            checks["missingProgressTaskCannotInfer"] = (await assistant.ReadProgressAsync("汇报进展", new TaskRecord { Id = "missing" }, snapshot, CancellationToken.None)).Contains("不在当前快照") && calls == beforeProgressCalls + 1;
            using (var revoke = new ChatGptAuth(vaultPath, new Stub((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("openid-configuration") ? Json(Metadata) : new HttpResponseMessage(HttpStatusCode.OK)))))
                checks["signOutClearsTokensKeepsRegistration"] = await revoke.SignOutAsync(a.ClientId, CancellationToken.None) && revoke.Find(a.ClientId)!.AccessToken.Length == 0 && revoke.Find(a.ClientId)!.ClientId == a.ClientId;

            await CheckOAuthAsync(directory, checks, key, nonce => Token(nonce: nonce));
            await CheckWeChatAsync(checks);
            await CheckLongRepliesAsync(checks);
            await CompletionSummaryTests.RunAsync(checks, directory);
            var preview = new ChatGptWindow(assistant, settings); preview.Show(); preview.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)preview.ActualWidth, (int)preview.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
            using (var file = File.Create(Path.Combine(directory, "chatgpt-settings.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); }
            preview.Close(); checks["settingsWindowRendered"] = true;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); throw; }
        finally { File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
        if (checks.Values.Any(passed => !passed)) throw new InvalidOperationException("ChatGPT smoke check failed.");
    }
    private static async Task CheckOAuthAsync(string directory, Dictionary<string, bool> checks, RsaSecurityKey key, Func<string, string> token)
    {
        string path = Path.Combine(directory, "oauth-fake.credentials"), nonce = "", redirect = "", verifierHash = "";
        var publicKey = key.Rsa.ExportParameters(false);
        Task? browser = null;
        using var auth = new ChatGptAuth(path, new Stub(async (request, ct) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration")) return Json(Metadata);
            if (request.RequestUri!.AbsolutePath.EndsWith("jwks.json")) return Json(new { keys = new[] { new { kty = "RSA", kid = key.KeyId, alg = "RS256", use = "sig", n = Base64UrlEncoder.Encode(publicKey.Modulus!), e = Base64UrlEncoder.Encode(publicKey.Exponent!) } } });
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            checks["oauthCodeExchangeContract"] = form["grant_type"] == "authorization_code" && form["client_id"] == "oaiapp_test" && form["redirect_uri"] == redirect && form["resource"] == ChatGptAuth.Resource
                && Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) == verifierHash && !form.ContainsKey("client_secret");
            return Json(new { access_token = "OAUTH_ACCESS", refresh_token = "OAUTH_REFRESH", token_type = "Bearer", id_token = token(nonce), expires_in = 3600, scope = ChatGptAuth.Scopes });
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var account = await auth.SignInAsync(null, timeout.Token, url => {
            var query = QueryHelpers.ParseQuery(url.Query); nonce = query["nonce"].ToString(); redirect = query["redirect_uri"].ToString(); verifierHash = query["code_challenge"].ToString();
            browser = Task.Run(async () => {
                using var client = new HttpClient();
                using var bad = await client.GetAsync(redirect + "?state=wrong&code=bad", timeout.Token);
                checks["unrelatedCallbackDoesNotConsumeLogin"] = bad.StatusCode == HttpStatusCode.BadRequest;
                using var good = await client.GetAsync(redirect + "?state=" + Uri.EscapeDataString(query["state"].ToString()) + "&code=fake-code&client_id=oaiapp_test", timeout.Token);
                checks["loopbackCallbackReturnsSafely"] = good.IsSuccessStatusCode && !(await good.Content.ReadAsStringAsync()).Contains("OAUTH_ACCESS");
            });
        });
        if (browser != null) await browser;
        checks["fullOAuthFlowStoresValidatedIdentity"] = account.Subject == "subject" && account.Sharing && ChatGptVault.Load(path).Accounts[0].AccessToken == "OAUTH_ACCESS";
        string host = ChatGptVault.Load(path).HostId;
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await auth.SignInAsync(account.ClientId, cancelled.Token, _ => { }); } catch (OperationCanceledException) { }
        checks["cancelledLoginPreservesAccountAndHost"] = ChatGptVault.Load(path).Accounts[0].AccessToken == "OAUTH_ACCESS" && ChatGptVault.Load(path).HostId == host;
        var noScope = Account(); noScope.Scope = "";
        checks["identityWithoutPlanGrantCannotInfer"] = !noScope.Sharing;
    }
    private sealed class DelayedAssistant : IWeChatAssistant, IWeChatReplyContext
    {
        public bool Enabled => true;
        public long ReplyRevision { get; set; }
        public int Calls;
        public TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ReplyAsync(string text, WeChatSnapshot snapshot, CancellationToken ct) { Calls++; return await Result.Task.WaitAsync(ct); }
    }
    private static async Task CheckLongRepliesAsync(Dictionary<string, bool> checks)
    {
        foreach (string mode in new[] { "complete", "reset", "failed" })
        {
            var settings = new Settings(); settings.WeChat.ClawBotEnabled = true;
            var binding = new ClawBinding { UserId = "owner", BotId = "bot", Token = "FAKE", ContextToken = "CONTEXT" }; settings.WeChat.WriteBinding(binding);
            var ai = new DelayedAssistant(); var received = new List<string>(); var ids = new List<string>(); int attempts = 0;
            string full = "结论：完成。\n\n" + new string('字', 887) + "🙂" + new string('文', 950) + "\n\n最后的问题已说明。";
            var pages = WeChatReports.Pages(full).Select(p => WeChatText.Clean(p, 3500)).ToArray();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) => {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var message = doc.RootElement.GetProperty("msg");
                attempts++;
                if (mode == "failed" && attempts == 2) { done.TrySetResult(); throw new HttpRequestException("synthetic send failure"); }
                received.Add(message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!);
                ids.Add(message.GetProperty("client_id").GetString()!);
                if (mode == "reset") { ai.ReplyRevision++; done.TrySetResult(); }
                if (received.Count == pages.Length) done.TrySetResult();
                return Json(new { ret = 0 });
            }), false, ai);
            await service.ConfigureAsync();
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { msgs = new[] { new { from_user_id = "owner", to_user_id = "bot", message_type = 1, message_id = "long-" + mode,
                context_token = "CTX", item_list = new[] { new { type = 1, text_item = new { text = "问 测试长回答" } } } } } }));
            await service.ProcessUpdatesAsync(input.RootElement, default); ai.Result.TrySetResult(full);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(8)); await service.DisposeAsync();
            if (mode == "complete")
            {
                checks["longReplyAutomaticallySendsEveryAgentPart"] = received.SequenceEqual(pages);
                checks["longReplyPartsHaveUniqueClientIds"] = ids.Distinct().Count() == pages.Length;
                checks["longReplyHasNoBrandingOrPagingFooter"] = received.All(p => !p.Contains("ChatGPT") && !p.Contains("订阅额度") && !p.Contains("下一页：回答"));
                checks["longReplyRetainsUnicodeAndEnding"] = string.Concat(received).Contains("🙂") && received.Last().EndsWith("最后的问题已说明。");
            }
            if (mode == "reset") checks["conversationResetStopsUnsentAnswerParts"] = received.Count == 1 && attempts == 1;
            if (mode == "failed") checks["uncertainPartFailureStopsWithoutReplay"] = received.Count == 1 && attempts == 2 && ai.Calls == 1;
        }
    }
    private static async Task CheckWeChatAsync(Dictionary<string, bool> checks)
    {
        var settings = new Settings(); settings.WeChat.ClawBotEnabled = true;
        var binding = new ClawBinding { UserId = "owner", BotId = "bot", Token = "FAKE_TOKEN", ContextToken = "FAKE_CONTEXT" }; settings.WeChat.WriteBinding(binding);
        var ai = new DelayedAssistant(); var replies = new List<string>();
        var answerSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), DateTimeOffset.UtcNow, Array.Empty<HostSnapshot>()), () => { }, new Stub(async (request, ct) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("sendmessage"))
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string text = doc.RootElement.GetProperty("msg").GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!;
                replies.Add(text); if (text == "AI_RESULT") answerSent.TrySetResult(true);
            }
            return Json(new { ret = 0 });
        }), receiveMessages: false, assistant: ai);
        await service.ConfigureAsync();
        async Task Receive(string owner, string id, string text)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { msgs = new[] { new { from_user_id = owner, to_user_id = "bot", message_type = 1, message_id = id, context_token = "CONTEXT", item_list = new[] { new { type = 1, text_item = new { text } } } } } }));
            await service.ProcessUpdatesAsync(doc.RootElement, CancellationToken.None);
        }
        await Receive("outsider", "wrong-owner", "问 测试"); checks["onlyBoundOwnerCanInvokeAi"] = ai.Calls == 0;
        await Receive("owner", "one", "问 测试"); checks["inferenceDoesNotBlockPolling"] = ai.Calls == 1 && !ai.Result.Task.IsCompleted;
        await Receive("owner", "one", "问 测试"); checks["duplicateInboundDoesNotInferAgain"] = ai.Calls == 1;
        await Receive("owner", "status", "状态"); checks["fixedCommandsWorkDuringInference"] = replies.Any(t => t.Contains("Codex 状态"));
        await Receive("owner", "busy", "问 再测试"); checks["concurrentAiBounded"] = ai.Calls == 1 && replies.Any(t => t.Contains("上一条 AI"));
        ai.Result.TrySetResult("AI_RESULT"); await answerSent.Task.WaitAsync(TimeSpan.FromSeconds(6));
        checks["completedAiReplyReachesWeChat"] = replies.Contains("AI_RESULT");
    }
}
