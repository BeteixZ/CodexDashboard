using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal sealed class WeChatApiException : Exception
{
    public int Code { get; }
    public bool IsHttpStatus { get; }
    public WeChatApiException(string service, int code, bool isHttpStatus = false) : base(service + "返回错误 " + code.ToString(CultureInfo.InvariantCulture) + (code == -2 ? "；请先在微信给 ClawBot 发一条消息，再查询或测试。" : code == -14 ? "；绑定凭据失效，请重新扫码。" : "；请检查账号状态和服务额度。"))
    { Code = code; IsHttpStatus = isHttpStatus; }
}

internal sealed class ClawLogin
{
    public string Code { get; init; } = "";
    public string QrContent { get; init; } = "";
    public string BaseUrl { get; set; } = WeChatApi.DefaultBaseUrl;
}

internal sealed class ClawTextRejectedException : Exception
{
    internal ClawTextRejectedException() : base("待发送正文超出渠道文本范围，已保留原文，未调用发送接口。") { }
}

internal sealed partial class WeChatApi : IDisposable
{
    public const string DefaultBaseUrl = "https://ilinkai.weixin.qq.com/";
    // Wire compatibility baseline, from Tencent/openclaw-weixin 2.4.10. This is our own client.
    private const string ProtocolVersion = "2.4.10";
    private readonly HttpClient client;
    public WeChatApi(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    }
    public static Uri TrustedBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !uri.DnsSafeHost.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("微信返回的服务地址未通过检查，连接已停止。");
        return uri;
    }
    private static object BaseInfo => new { channel_version = ProtocolVersion, bot_agent = "CodexDashboard/1.0.0" };
    public async Task<ClawLogin> StartLoginAsync(CancellationToken cancellation)
    {
        using var data = await RequestAsync(DefaultBaseUrl, "ilink/bot/get_bot_qrcode?bot_type=3", new { local_token_list = Array.Empty<string>() }, null, 20, cancellation);
        string code = String(data.RootElement, "qrcode"), content = String(data.RootElement, "qrcode_img_content");
        if (code.Length == 0 || content.Length == 0 || content.Length > 4096) throw new InvalidOperationException("微信未返回可用的二维码，请稍后重试。");
        return new ClawLogin { Code = code, QrContent = content };
    }
    public async Task<(string Status, ClawBinding? Binding)> PollLoginAsync(ClawLogin login, string? verifyCode, CancellationToken cancellation)
    {
        string endpoint = "ilink/bot/get_qrcode_status?qrcode=" + Uri.EscapeDataString(login.Code);
        if (!string.IsNullOrEmpty(verifyCode)) endpoint += "&verify_code=" + Uri.EscapeDataString(verifyCode);
        using var data = await RequestAsync(login.BaseUrl, endpoint, null, null, 40, cancellation);
        var root = data.RootElement;
        string status = String(root, "status");
        if (status == "scaned_but_redirect")
        {
            string host = String(root, "redirect_host");
            login.BaseUrl = TrustedBase("https://" + host.TrimEnd('/') + "/").AbsoluteUri;
        }
        if (status != "confirmed") return (status, null);
        var binding = new ClawBinding { Token = String(root, "bot_token"), BotId = String(root, "ilink_bot_id"), UserId = String(root, "ilink_user_id"), BaseUrl = String(root, "baseurl") };
        if (binding.BaseUrl.Length == 0) binding.BaseUrl = login.BaseUrl;
        binding.BaseUrl = TrustedBase(binding.BaseUrl).AbsoluteUri;
        if (binding.Token.Length == 0 || binding.BotId.Length == 0 || binding.UserId.Length == 0)
            throw new InvalidOperationException("微信绑定信息不完整，请重新扫码；未启用消息查询。");
        return (status, binding);
    }
    public async Task<JsonDocument> GetUpdatesAsync(ClawBinding binding, CancellationToken cancellation)
    {
        var response = await RequestAsync(binding.BaseUrl, "ilink/bot/getupdates",
            new { get_updates_buf = binding.Cursor, base_info = BaseInfo }, binding.Token, 45, cancellation);
        try
        {
            // Preserve the existing empty-poll forms and per-member filtering.
            // An unreadable batch must never reach the cursor-commit path.
            if (response.RootElement.TryGetProperty("msgs", out var messages)
                && messages.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
                throw new InvalidOperationException("微信消息列表格式异常；本批尚未读取，收件游标未前移。");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    public async Task<bool?> SendClawAsync(ClawBinding binding, string text, string clientId, CancellationToken cancellation,
        bool preserveText = false, Action<string>? observeMessageId = null)
    {
        if (binding.ContextToken.Length == 0) throw new InvalidOperationException("请先在微信给 ClawBot 发送“帮助”，激活收发消息。");
        // A durable outbox already records the exact page. Cleaning or trimming
        // here would make its acceptance proof describe content never sent.
        string wireText = preserveText ? ExactClawText(text) : WeChatText.Clean(text, 3500);
        using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/sendmessage", new
        {
            msg = new { from_user_id = "", to_user_id = binding.UserId, client_id = clientId, message_type = 2, message_state = 2, context_token = binding.ContextToken,
                item_list = new[] { new { type = 1, text_item = new { text = wireText } } } },
            base_info = BaseInfo
        }, binding.Token, 20, cancellation);
        // A successful API response means acceptance, not a device delivery receipt.
        RequireZero(response.RootElement, "ret", "微信");
        WeChatFieldProbe.RecordSend(binding, clientId, response.RootElement);
        string messageId = String(response.RootElement, "message_id");
        if (messageId.Length > 0) observeMessageId?.Invoke(messageId);
        // The live backend can return a 2xx response without ret. Preserve that
        // uncertainty instead of reporting rejection or confirmed acceptance.
        return response.RootElement.TryGetProperty("ret", out _) ? true : null;
    }
    private static string ExactClawText(string text)
    {
        if (text.Length is < 1 or > 3500) throw new ClawTextRejectedException();
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (char.IsControl(value) && value is not ('\n' or '\r' or '\t') || char.IsLowSurrogate(value))
                throw new ClawTextRejectedException();
            if (char.IsHighSurrogate(value) && (++i >= text.Length || !char.IsLowSurrogate(text[i])))
                throw new ClawTextRejectedException();
        }
        return text;
    }
    internal async Task<string> GetTypingTicketAsync(ClawBinding binding, CancellationToken cancellation)
    {
        using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/getconfig", new
        { ilink_user_id = binding.UserId, context_token = binding.ContextToken, base_info = BaseInfo }, binding.Token, 5, cancellation);
        RequireZero(response.RootElement, "ret", "微信");
        return String(response.RootElement, "typing_ticket");
    }
    internal async Task SendTypingAsync(ClawBinding binding, string ticket, bool active, CancellationToken cancellation)
    {
        using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/sendtyping", new
        { ilink_user_id = binding.UserId, typing_ticket = ticket, status = active ? 1 : 2, base_info = BaseInfo }, binding.Token, 5, cancellation);
        RequireZero(response.RootElement, "ret", "微信");
    }
    public async Task LifecycleAsync(ClawBinding binding, bool start, CancellationToken cancellation)
    {
        using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/msg/notify" + (start ? "start" : "stop"), new { base_info = BaseInfo }, binding.Token, 5, cancellation);
    }
    public async Task<string> SendPushPlusAsync(string token, string title, string body, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请在微信通知设置中填写 pushplus 令牌。");
        using var response = await RequestAsync("https://www.pushplus.plus/", "send", new { token, title, content = body, channel = "wechat", template = "txt" }, null, 20, cancellation, pushPlus: true);
        if (!response.RootElement.TryGetProperty("code", out var code) || !code.TryGetInt32(out int status)) throw new InvalidOperationException("pushplus 返回格式异常，未确认受理。");
        if (status != 200) throw new WeChatApiException("pushplus", status);
        string receipt = String(response.RootElement, "data");
        if (string.IsNullOrWhiteSpace(receipt) || receipt.Length > 512 || receipt.Any(char.IsControl))
            throw new InvalidOperationException("pushplus 未返回有效的消息流水号，投递结果未确认。");
        return receipt;
    }
    private async Task<JsonDocument> RequestAsync(string baseUrl, string endpoint, object? body, string? token, int seconds, CancellationToken cancellation, bool pushPlus = false)
    {
        Uri uri = pushPlus ? new Uri("https://www.pushplus.plus/" + endpoint) : new Uri(TrustedBase(baseUrl), endpoint);
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!pushPlus)
        {
            request.Headers.Add("iLink-App-Id", "bot"); request.Headers.Add("iLink-App-ClientVersion", ((2 << 16) | (4 << 8) | 10).ToString(CultureInfo.InvariantCulture));
            if (body != null)
            {
                request.Headers.Add("AuthorizationType", "ilink_bot_token");
                string uin = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)).ToString(CultureInfo.InvariantCulture);
                request.Headers.Add("X-WECHAT-UIN", Convert.ToBase64String(Encoding.UTF8.GetBytes(uin)));
            }
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var response = await client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new WeChatApiException(pushPlus ? "pushplus HTTP " : "微信 HTTP ", (int)response.StatusCode, isHttpStatus: true);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        try
        {
            if (!pushPlus) { RequireZero(result.RootElement, "ret", "微信"); RequireZero(result.RootElement, "errcode", "微信"); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static void RequireZero(JsonElement root, string property, string service, bool required = false)
    {
        if (!root.TryGetProperty(property, out var value)) { if (required) throw new InvalidOperationException(service + "未返回确认状态，投递结果未知。"); return; }
        if (!value.TryGetInt32(out int code)) throw new InvalidOperationException(service + "返回状态格式异常。");
        if (code != 0) throw new WeChatApiException(service, code);
    }
    public static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "" : "";
    public static int Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    // Never echo remote response bodies, request URIs or exception messages containing credentials.
    public static string Explain(Exception error) => error switch
    {
        WeChatApiException or InvalidOperationException or CryptographicException => error.Message,
        OperationCanceledException => "请求超时或已取消，投递结果未确认。",
        HttpRequestException => "网络连接失败，请检查网络；投递结果未确认。",
        JsonException => "服务返回数据无法解析，请稍后重试。",
        _ => "微信功能暂时不可用，请检查设置并重试。"
    };
    public void Dispose() => client.Dispose();
}
