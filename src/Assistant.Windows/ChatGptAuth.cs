using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CodexDashboard;

public sealed class ChatGptOptions
{
    public bool TaskControlEnabled { get; set; }
    public bool Enabled { get; set; }
    public bool IncludeTaskContext { get; set; } = true;
    public bool CompletionSummariesEnabled { get; set; }
    public bool PersonalizationEnabled { get; set; } = true;
    public bool AutoMemoryEnabled { get; set; } = true;
    public string ActiveAccount { get; set; } = "";
    public string Model { get; set; } = "";
}

// The entire store (including account labels) is protected with Windows DPAPI.
// It is independent of settings.json, so WeChat cursor saves cannot roll back a
// rotating refresh token. Only the singleton assistant writes production tokens.
internal sealed class ChatGptAccount
{
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Welcomed { get; set; }
    public string Label => WeChatText.Clean(Email.Length > 0 ? Email : "ChatGPT 账户", 80) + " · " + ClientId[^Math.Min(6, ClientId.Length)..];
    public bool Sharing => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct");
    public void ClearTokens() { AccessToken = RefreshToken = IdToken = ""; ExpiresAt = default; }
}
internal sealed class ChatGptVault
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public List<ChatGptAccount> Accounts { get; set; } = new();
    public static ChatGptVault Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<ChatGptVault>(LocalSecret.Unprotect(File.ReadAllText(path)), Settings.Json) ?? throw new InvalidDataException()
        : new();
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(pending, LocalSecret.Protect(JsonSerializer.Serialize(this, Settings.Json))); File.Move(pending, path, true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
internal sealed record ChatGptModel(string Slug, string DisplayName);
internal enum ChatGptFailureKind { Unspecified, Unavailable, UsageLimit, Authorization, RateLimited, InvalidOutput }
internal sealed class ChatGptException(string message, ChatGptFailureKind kind = ChatGptFailureKind.Unspecified) : Exception(message)
{
    internal ChatGptFailureKind Kind { get; } = kind;
}
internal sealed record ChatGptIdentity(string Subject, string Email);

internal sealed class ChatGptAuth : IDisposable
{
    internal const string Issuer = "https://auth.openai.com", Resource = "https://api.openai.com/v1";
    internal const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private readonly string path;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ChatGptVault vault;
    private JsonElement? discovery;
    public IReadOnlyList<ChatGptAccount> Accounts => vault.Accounts;
    public event Action? Changed;
    public ChatGptAuth(string? path = null, HttpMessageHandler? handler = null)
    {
        this.path = path ?? Path.Combine(Settings.DirectoryPath, "chatgpt.credentials");
        vault = ChatGptVault.Load(this.path);
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public ChatGptAccount? Find(string clientId) => vault.Accounts.FirstOrDefault(a => a.ClientId == clientId);
    public void Save() { vault.Save(path); Changed?.Invoke(); }
    private async Task<JsonElement> DiscoveryAsync(CancellationToken ct)
    {
        if (discovery is { } cached) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, Issuer + "/.well-known/openid-configuration");
        var value = await JsonAsync(request, ct);
        if (Text(value, "issuer") != Issuer) throw new ChatGptException("OpenAI 身份提供方不匹配。");
        discovery = value; return value;
    }
    private static Uri Endpoint(JsonElement metadata, string name)
    {
        if (!Uri.TryCreate(Text(metadata, name), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com" || uri.Port != 443 || uri.UserInfo.Length > 0)
            throw new ChatGptException("OpenAI 身份服务地址无效。");
        return uri;
    }
    internal static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    internal static Uri Authorize(string host, string redirect, string state, string nonce, string verifier, ChatGptAccount? account)
    {
        var values = new Dictionary<string, string> { ["client_id"] = account?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = host,
            ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = Scopes, ["resource"] = Resource,
            ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
        if (account == null) values["agent_name_hint"] = "Codex Assistant";
        else if (account.IdToken.Length > 0) values["id_token_hint"] = account.IdToken;
        if (account != null && !account.Sharing) values["prompt"] = "consent";
        return new(Issuer + "/api/accounts/authorize?" + string.Join("&", values.Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value))));
    }
    internal static string ValidateCallback(IReadOnlyDictionary<string, string> query, string state, string? expectedClient)
    {
        if (!query.TryGetValue("state", out var supplied) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(supplied)))
            throw new ChatGptException("登录回调校验失败，请重新登录。");
        if (query.TryGetValue("iss", out var issuer) && issuer != Issuer) throw new ChatGptException("登录回调来源不匹配。");
        if (query.ContainsKey("error")) throw new ChatGptException("ChatGPT 登录或订阅使用授权未完成。");
        string client = query.GetValueOrDefault("client_id", expectedClient ?? "");
        if (client.Length == 0 || client == "dynamic_agent_client" || (expectedClient != null && expectedClient != client))
            throw new ChatGptException("未取得匹配的 ChatGPT 应用注册信息，请重试。");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new ChatGptException("登录回调缺少授权码。");
        return client;
    }
    public async Task<ChatGptAccount> SignInAsync(string? clientId, CancellationToken cancellation, Action<Uri>? open = null)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            // Persist the host before the first attempt, even when consent is cancelled.
            vault.Save(path);
            var old = clientId == null ? null : Find(clientId) ?? throw new ChatGptException("找不到所选账户。");
            var metadata = await DiscoveryAsync(cancellation);
            string state = Random(), nonce = Random(), verifier = Random();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lifetime.CancelAfter(TimeSpan.FromMinutes(10));
            var completion = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            await using var callback = builder.Build();
            callback.MapGet("/auth/callback", async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                if (context.Request.Query.Any(q => q.Value.Count != 1)) { context.Response.StatusCode = 400; return; }
                var query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
                // Unrelated callbacks do not consume the legitimate pending attempt.
                if (query.GetValueOrDefault("state") != state) { context.Response.StatusCode = 400; return; }
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("已收到 ChatGPT 登录回调，请返回 Codex 助手查看授权结果。", lifetime.Token);
                completion.TrySetResult(query);
            });
            await callback.StartAsync(lifetime.Token);
            try
            {
                string address = callback.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                string redirect = address + "/auth/callback";
                var url = Authorize(vault.HostId, redirect, state, nonce, verifier, old);
                if (open != null) open(url); else Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                var query = await completion.Task.WaitAsync(lifetime.Token);
                string issued = ValidateCallback(query, state, old?.ClientId);
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(metadata, "token_endpoint")) { Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["grant_type"] = "authorization_code", ["client_id"] = issued, ["code"] = query["code"], ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource }) };
                var token = await JsonAsync(request, lifetime.Token);
                var identity = await VerifyAsync(Text(token, "id_token"), issued, nonce, metadata, lifetime.Token);
                if (old != null && identity.Subject != old.Subject) throw new ChatGptException("登录账户与原账户不匹配，原连接保持不变。请使用“添加账户”。");
                var registered = vault.Accounts.FirstOrDefault(a => a.ClientId == issued);
                if (registered != null && registered.Subject != identity.Subject) throw new ChatGptException("应用注册身份不匹配。");
                var account = new ChatGptAccount { ClientId = issued, Subject = identity.Subject, Email = identity.Email, Welcomed = registered?.Welcomed ?? false };
                ApplyToken(account, token, false);
                if (registered != null) vault.Accounts.Remove(registered);
                vault.Accounts.Add(account); Save(); return account;
            }
            finally { await callback.StopAsync(CancellationToken.None); }
        }
        finally { gate.Release(); }
    }
    internal async Task<ChatGptIdentity> VerifyAsync(string jwt, string clientId, string? nonce, JsonElement metadata, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(metadata, "jwks_uri"));
        var jwks = await JsonAsync(request, ct);
        return ValidateIdentity(jwt, clientId, nonce, new JsonWebKeySet(jwks.GetRawText()).GetSigningKeys());
    }
    internal static ChatGptIdentity ValidateIdentity(string jwt, string clientId, string? nonce, IEnumerable<SecurityKey> keys)
    {
        try
        {
            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
            // All keys are already fetched; validation here performs local crypto only.
            var validation = handler.ValidateTokenAsync(jwt, new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = clientId,
                IssuerSigningKeys = keys, ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true,
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(5),
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256 } }).GetAwaiter().GetResult();
            if (!validation.IsValid) throw new SecurityTokenValidationException();
            var principal = validation.ClaimsIdentity;
            string subject = principal.FindFirst("sub")?.Value ?? "";
            if (subject.Length == 0 || principal.FindFirst("iat") == null || (nonce != null && principal.FindFirst("nonce")?.Value != nonce)) throw new SecurityTokenValidationException();
            return new(subject, principal.FindFirst("email")?.Value ?? "");
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        { throw new ChatGptException("ChatGPT 身份验证失败，请重新登录。"); }
    }
    internal static void ApplyToken(ChatGptAccount account, JsonElement token, bool refresh)
    {
        string access = Text(token, "access_token"), replacement = Text(token, "refresh_token");
        string type = Text(token, "token_type"), scope = Text(token, "scope");
        if (access.Length == 0 || !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || !token.TryGetProperty("expires_in", out var expiry)
            || !expiry.TryGetInt32(out int seconds) || seconds <= 0 || seconds > 86400 || (refresh && replacement.Length == 0))
            throw new ChatGptException("ChatGPT 返回的凭据信息不完整，请重新登录。");
        account.AccessToken = access; account.RefreshToken = replacement; account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        if (scope.Length > 0 || !refresh) account.Scope = scope;
        if (Text(token, "id_token") is { Length: > 0 } id) account.IdToken = id;
    }
    public async Task<string> AccessTokenAsync(string clientId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var account = Find(clientId) ?? throw new ChatGptException("请先在助手中 登录 ChatGPT。");
            if (!account.Sharing) throw new ChatGptException("此连接未授权使用 ChatGPT 订阅，请在助手中 重新授权。");
            if (account.AccessToken.Length == 0) throw new ChatGptException("请在助手中 重新登录 ChatGPT。");
            if (account.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return account.AccessToken;
            if (account.RefreshToken.Length == 0) throw new ChatGptException("ChatGPT 登录已过期，请重新登录。");
            var metadata = await DiscoveryAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(metadata, "token_endpoint")) { Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId, ["refresh_token"] = account.RefreshToken, ["resource"] = Resource }) };
            JsonElement token;
            try { token = await JsonAsync(request, ct); }
            catch (ChatGptSessionExpiredException) { account.ClearTokens(); Save(); throw new ChatGptException("ChatGPT 登录已失效，请在助手中 重新登录。"); }
            if (Text(token, "id_token").Length > 0)
            {
                var identity = await VerifyAsync(Text(token, "id_token"), account.ClientId, null, metadata, ct);
                if (identity.Subject != account.Subject) throw new ChatGptException("刷新后的 ChatGPT 账户身份不匹配。");
            }
            ApplyToken(account, token, true); Save();
            if (!account.Sharing) throw new ChatGptException("ChatGPT 订阅使用权限已撤销，请重新授权。");
            return account.AccessToken;
        }
        finally { gate.Release(); }
    }
    public async Task<bool> SignOutAsync(string clientId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var account = Find(clientId); if (account == null) return true;
            bool revoked = account.RefreshToken.Length == 0;
            try
            {
                if (!revoked)
                {
                    var metadata = await DiscoveryAsync(ct);
                    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(metadata, "revocation_endpoint")) { Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }) };
                    using var response = await http.SendAsync(request, ct); revoked = response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ChatGptException) { }
            account.ClearTokens(); Save(); return revoked;
        }
        finally { gate.Release(); }
    }
    private async Task<JsonElement> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        string body = await ReadBoundedAsync(response.Content, ct);
        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch (JsonException) { throw new ChatGptException("OpenAI 身份服务返回了无法识别的响应，请稍后重试。"); }
        if (!response.IsSuccessStatusCode)
        {
            string code = Text(root, "error");
            if (code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused") throw new ChatGptSessionExpiredException();
            throw new ChatGptException($"OpenAI 登录服务请求失败（HTTP {(int)response.StatusCode}），请稍后重试。");
        }
        return root;
    }
    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct, int limit = 256_000)
    {
        await using var stream = await content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
        var chars = new char[4096]; var result = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(chars.AsMemory(), ct)) > 0)
        { if (result.Length + count > limit) throw new ChatGptException("OpenAI 响应超出读取上限。"); result.Append(chars, 0, count); }
        return result.ToString();
    }
    internal static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    public void Dispose() => http.Dispose();
    private sealed class ChatGptSessionExpiredException : Exception { }
}
