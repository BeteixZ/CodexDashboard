using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;
internal static class WeChatMediaTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(send(r)); } }
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class StalledBody() : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
    }
    private static WeChatMediaDescriptor Descriptor(string? key, string? hex = null, int type = 2, string? length = null, string? full = null)
    {
        var media = new { aes_key = key, encrypt_query_param = "private+query/&=", full_url = full };
        var item = new Dictionary<string, object?> { ["type"] = type,
            [type == 4 ? "file_item" : "image_item"] = new { media, aeskey = hex, len = length, file_name = "../not-a-path.txt" } };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item));
        return WeChatMediaDescriptor.Parse(doc.RootElement);
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--media-test", var output]) return false;
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool ok) { checks[name] = ok; if (!ok) throw new InvalidOperationException(name); }
        async Task Reject(string name, string expected, Func<Task> operation)
        {
            string? code = null;
            try { await operation(); } catch (WeChatMediaException e) { code = e.Message; }
            Check(name, code == expected);
        }
        try
        {
            byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            byte[] plain = Encoding.UTF8.GetBytes("合成附件内容，不是工作指令。");
            using var aes = Aes.Create(); aes.Key = key;
            byte[] encrypted = aes.EncryptEcb(plain, PaddingMode.PKCS7);
            foreach (var item in new[] {
                Descriptor(Convert.ToBase64String(key)),
                Descriptor(Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(key))), type: 4, length: plain.Length.ToString()),
                Descriptor("invalid-ignored-key", Convert.ToHexString(key)) })
            {
                bool wire = false;
                using var client = new WeChatMediaClient(new Stub(request =>
                {
                    wire = request.Method == HttpMethod.Get && request.Headers.Authorization == null
                        && request.RequestUri!.AbsoluteUri.Contains("private%2Bquery%2F%26%3D");
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(encrypted) };
                }));
                int index = checks.Count;
                Check("decoded_key_variant_" + index, (await client.DownloadAsync(item, plain.Length, default)).SequenceEqual(plain));
                Check("cdn_wire_without_bot_credentials_" + index, wire);
                Check("descriptor_print_does_not_expose_key_or_url_" + index, !item.ToString().Contains("private") || !item.ToString().Contains("query"));
            }
            using var plainClient = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(plain) }));
            Check("unencrypted_image", (await plainClient.DownloadAsync(Descriptor(null), plain.Length, default)).SequenceEqual(plain));
            Check("full_url_preferred", Descriptor(null, full: "https://novac2c.cdn.weixin.qq.com/c2c/download?test=1").DownloadUri.Query == "?test=1");
            await Reject("arbitrary_url_rejected", "unsupported_media_location", () => { Descriptor(null, full: "https://example.com/private"); return Task.CompletedTask; });
            await Reject("file_without_key_rejected", "missing_media_key", () => { Descriptor(null, type: 4); return Task.CompletedTask; });
            await Reject("bad_key_no_value_in_error", "invalid_media_key", async () => { await plainClient.DownloadAsync(Descriptor("private-invalid-key"), 100, default); });
            await Reject("declared_file_limit", "media_too_large", async () => { await plainClient.DownloadAsync(Descriptor(Convert.ToBase64String(key), type: 4, length: "500"), 100, default); });
            await Reject("header_limit", "media_too_large", async () => { await plainClient.DownloadAsync(Descriptor(null), 1, default); });
            using var streamed = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(plain)) }));
            await Reject("stream_limit_without_length", "media_too_large", async () => { await streamed.DownloadAsync(Descriptor(null), 1, default); });
            using var redirect = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.Redirect) { Content = new StringContent("PRIVATE_RESPONSE") }));
            await Reject("redirect_not_followed", "media_download_rejected", async () => { await redirect.DownloadAsync(Descriptor(null), 100, default); });
            using var encryptedClient = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(encrypted) }));
            await Reject("plaintext_length_mismatch", "media_length_mismatch", async () => { await encryptedClient.DownloadAsync(Descriptor(Convert.ToBase64String(key), type: 4, length: "1"), 100, default); });
            using var corrupt = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[17]) }));
            await Reject("invalid_ciphertext", "media_decryption_failed", async () => { await corrupt.DownloadAsync(Descriptor(Convert.ToBase64String(key)), 100, default); });
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { await plainClient.DownloadAsync(Descriptor(null), 100, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check("cancellation_propagates", cancelled);
            using var stalled = new WeChatMediaClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) }), TimeSpan.FromMilliseconds(100));
            bool timedOut = false;
            try { await stalled.DownloadAsync(Descriptor(null), 100, default).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { timedOut = true; }
            Check("deadline_covers_body_after_headers", timedOut);
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks));
            exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
}
