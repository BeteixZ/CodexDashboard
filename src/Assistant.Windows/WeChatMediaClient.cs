using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal sealed class WeChatMediaException(string code) : Exception(code);

// Protocol implementation, not yet enabled on the live receive path.
// Tencent/openclaw-weixin 874637a0: CDN download, AES-128-ECB/PKCS7.
internal sealed class WeChatMediaDescriptor
{
    internal required int ItemType { get; init; }
    internal required Uri DownloadUri { get; init; }
    internal string? Base64Key { get; init; }
    internal string? HexKey { get; init; }
    internal long? PlaintextLength { get; init; }
    public override string ToString() => "WeChat media descriptor (private fields omitted)";

    internal static WeChatMediaDescriptor Parse(JsonElement item)
    {
        int type = WeChatApi.Number(item, "type");
        string field = type switch { 2 => "image_item", 3 => "voice_item", 4 => "file_item", 5 => "video_item", _ => throw new WeChatMediaException("unsupported_media_type") };
        if (!item.TryGetProperty(field, out var details) || details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
            throw new WeChatMediaException("missing_media_descriptor");
        string full = WeChatApi.String(media, "full_url");
        string query = WeChatApi.String(media, "encrypt_query_param");
        if (full.Length == 0 && query.Length == 0) throw new WeChatMediaException("missing_media_location");
        string url = full.Length > 0 ? full : "https://novac2c.cdn.weixin.qq.com/c2c/download?encrypted_query_param=" + Uri.EscapeDataString(query);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.DnsSafeHost != "novac2c.cdn.weixin.qq.com" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/c2c/download") throw new WeChatMediaException("unsupported_media_location");
        string hex = type == 2 ? WeChatApi.String(details, "aeskey") : "";
        string key = WeChatApi.String(media, "aes_key");
        if (type != 2 && key.Length == 0) throw new WeChatMediaException("missing_media_key");
        long? length = null;
        string declared = type == 4 ? WeChatApi.String(details, "len") : "";
        if (declared.Length > 0)
        {
            if (!long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                throw new WeChatMediaException("invalid_media_length");
            length = parsed;
        }
        return new() { ItemType = type, DownloadUri = uri, HexKey = hex.Length == 0 ? null : hex,
            Base64Key = key.Length == 0 ? null : key, PlaintextLength = length };
    }
    internal byte[]? ReadKey()
    {
        try
        {
            byte[]? key = HexKey != null ? Convert.FromHexString(HexKey)
                : Base64Key != null ? Convert.FromBase64String(Base64Key) : null;
            if (key == null || key.Length == 16) return key;
            if (HexKey == null && key.Length == 32)
            {
                byte[] decoded;
                try { decoded = Convert.FromHexString(Encoding.ASCII.GetString(key)); }
                finally { CryptographicOperations.ZeroMemory(key); }
                if (decoded.Length == 16) return decoded;
            }
            else CryptographicOperations.ZeroMemory(key);
            throw new WeChatMediaException("invalid_media_key");
        }
        catch (FormatException) { throw new WeChatMediaException("invalid_media_key"); }
    }
}

internal sealed class WeChatMediaClient : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan downloadTimeout;
    internal WeChatMediaClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        downloadTimeout = timeout ?? TimeSpan.FromSeconds(30);
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal async Task<byte[]> DownloadAsync(WeChatMediaDescriptor media, int maximumBytes, CancellationToken ct)
    {
        if (maximumBytes is < 1 or > 100 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(downloadTimeout);
        ct = deadline.Token;
        byte[]? key = media.ReadKey();
        try
        {
            if (media.PlaintextLength > maximumBytes) throw new WeChatMediaException("media_too_large");
            int limit = key == null ? maximumBytes : checked((maximumBytes / 16 + 1) * 16);
            using var request = new HttpRequestMessage(HttpMethod.Get, media.DownloadUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new WeChatMediaException("media_download_rejected");
            if (response.Content.Headers.ContentLength > limit) throw new WeChatMediaException("media_too_large");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > limit) throw new WeChatMediaException("media_too_large");
                output.Write(buffer, 0, count);
            }
            ct.ThrowIfCancellationRequested();
            byte[] content = output.ToArray();
            if (key != null)
            {
                using var aes = Aes.Create(); aes.Key = key;
                content = aes.DecryptEcb(content, PaddingMode.PKCS7);
            }
            if (content.Length > maximumBytes) throw new WeChatMediaException("media_too_large");
            if (media.PlaintextLength is { } expected && content.LongLength != expected)
                throw new WeChatMediaException("media_length_mismatch");
            return content;
        }
        catch (HttpRequestException) { throw new WeChatMediaException("media_network_failure"); }
        catch (CryptographicException) { throw new WeChatMediaException("media_decryption_failed"); }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
    internal async Task<string> UploadAsync(string url, byte[] ciphertext, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.DnsSafeHost != "novac2c.cdn.weixin.qq.com" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/c2c/upload") throw new WeChatMediaException("unsupported_upload_location");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(downloadTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(ciphertext) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new WeChatMediaException("media_upload_rejected");
        if (!response.Headers.TryGetValues("x-encrypted-param", out var values)) throw new WeChatMediaException("missing_uploaded_download_parameter");
        string value = System.Linq.Enumerable.Single(values);
        if (string.IsNullOrWhiteSpace(value)) throw new WeChatMediaException("missing_uploaded_download_parameter");
        return value;
    }
    public void Dispose() => client.Dispose();
}
