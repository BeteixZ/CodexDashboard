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
internal static class WeChatFileTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return send(request); } }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--file-upload-test", var output]) return false;
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) { checks[name] = passed; if (!passed) throw new InvalidOperationException(name); }
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes("合成产物正文");
            string hex = "", sendResponse = "{\"ret\":0,\"message_id\":\"1234567890123456789\"}";
            int uploads = 0, sends = 0;
            bool sendWire = false;
            using var api = new WeChatApi(new Stub(async request =>
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var root = doc.RootElement;
                if (request.RequestUri!.AbsolutePath.EndsWith("getuploadurl"))
                {
                    hex = root.GetProperty("aeskey").GetString()!;
                    Check("upload_ticket_fields", root.GetProperty("media_type").GetInt32() == 3
                        && root.GetProperty("rawsize").GetInt32() == plain.Length
                        && root.GetProperty("filesize").GetInt32() == (plain.Length / 16 + 1) * 16
                        && root.GetProperty("rawfilemd5").GetString() == Convert.ToHexString(MD5.HashData(plain)).ToLowerInvariant()
                        && root.GetProperty("no_need_thumb").GetBoolean());
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"upload_param\":\"private+upload/\"}") };
                }
                sends++;
                var item = root.GetProperty("msg").GetProperty("item_list")[0];
                var file = item.GetProperty("file_item");
                sendWire = item.GetProperty("type").GetInt32() == 4 && file.GetProperty("file_name").GetString() == "报告.txt"
                    && file.GetProperty("len").GetString() == plain.Length.ToString()
                    && Encoding.ASCII.GetString(Convert.FromBase64String(file.GetProperty("media").GetProperty("aes_key").GetString()!)) == hex
                    && file.GetProperty("media").GetProperty("encrypt_query_param").GetString() == "private-download";
                return new(HttpStatusCode.OK) { Content = new StringContent(sendResponse) };
            }));
            using var cdn = new WeChatMediaClient(new Stub(async request =>
            {
                uploads++;
                Check("cdn_upload_has_no_bot_authorization", request.Headers.Authorization == null
                    && request.Method == HttpMethod.Post && request.RequestUri!.Query.Contains("private%2Bupload%2F"));
                using var aes = Aes.Create(); aes.Key = Convert.FromHexString(hex);
                Check("uploaded_ciphertext_decrypts_to_exact_file", aes.DecryptEcb(await request.Content!.ReadAsByteArrayAsync(), PaddingMode.PKCS7).SequenceEqual(plain));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("x-encrypted-param", "private-download"); return response;
            }));
            var binding = new ClawBinding { BotId = "synthetic-bot", UserId = "synthetic-user", Token = "SYNTHETIC", BaseUrl = WeChatApi.DefaultBaseUrl, ContextToken = "synthetic-context" };
            var uploaded = await api.UploadFileAsync(binding, plain, cdn, default);
            Check("upload_does_not_send_message", uploads == 1 && sends == 0 && !uploaded.ToString().Contains("private-download"));
            string? id = null;
            Check("explicit_ret_is_api_acceptance", await api.SendUploadedFileAsync(binding, uploaded, "报告.txt", "client-1", default, value => id = value) == true && sendWire && id == "1234567890123456789");
            sendResponse = "{\"message_id\":\"1234567890123456790\"}";
            Check("message_id_without_ret_is_api_acceptance", await api.SendUploadedFileAsync(binding, uploaded, "报告.txt", "client-2", default) == true && sends == 2 && uploads == 1);
            sendResponse = "{}";
            Check("missing_receipt_remains_unknown_without_retry", await api.SendUploadedFileAsync(binding, uploaded, "报告.txt", "client-empty", default) == null && sends == 3);
            binding.UserId = "other-user";
            bool mismatch = false;
            try { await api.SendUploadedFileAsync(binding, uploaded, "报告.txt", "client-3", default); } catch (WeChatMediaException) { mismatch = true; }
            Check("cannot_send_upload_to_other_binding", mismatch && sends == 3);
            bool untrusted = false;
            try { await cdn.UploadAsync("https://example.com/upload", new byte[16], default); } catch (WeChatMediaException e) { untrusted = e.Message == "unsupported_upload_location"; }
            Check("untrusted_upload_destination_rejected_before_network", untrusted && uploads == 1);
            using var missing = new WeChatMediaClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            bool noReceipt = false;
            try { await missing.UploadAsync("https://novac2c.cdn.weixin.qq.com/c2c/upload", new byte[16], default); } catch (WeChatMediaException e) { noReceipt = e.Message == "missing_uploaded_download_parameter"; }
            Check("upload_requires_download_parameter", noReceipt);
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks)); exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
}
