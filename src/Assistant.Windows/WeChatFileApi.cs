using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;
internal sealed class WeChatUploadedFile
{
    internal required string BotId { get; init; }
    internal required string UserId { get; init; }
    internal required string DownloadParameter { get; init; }
    internal required string Base64Key { get; init; }
    internal required int Length { get; init; }
    public override string ToString() => "Uploaded WeChat file (private fields omitted)";
}
internal sealed partial class WeChatApi
{
    // Low-level protocol only. Durable artifact selection and delivery are wired separately.
    internal async Task<WeChatUploadedFile> UploadFileAsync(ClawBinding binding, byte[] content,
        WeChatMediaClient cdn, CancellationToken ct)
    {
        if (content.Length > AssistantAttachmentStore.MaximumBytes) throw new WeChatMediaException("media_too_large");
        byte[] key = RandomNumberGenerator.GetBytes(16);
        try
        {
            string hex = Convert.ToHexString(key).ToLowerInvariant();
            string fileKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            using var aes = Aes.Create(); aes.Key = key;
            byte[] encrypted = aes.EncryptEcb(content, PaddingMode.PKCS7);
            using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/getuploadurl", new
            {
                filekey = fileKey, media_type = 3, to_user_id = binding.UserId,
                rawsize = content.Length, rawfilemd5 = Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant(),
                filesize = encrypted.Length, no_need_thumb = true, aeskey = hex, base_info = BaseInfo
            }, binding.Token, 20, ct);
            string full = String(response.RootElement, "upload_full_url").Trim();
            string parameter = String(response.RootElement, "upload_param");
            if (full.Length == 0 && parameter.Length == 0) throw new WeChatMediaException("missing_upload_location");
            string url = full.Length > 0 ? full : "https://novac2c.cdn.weixin.qq.com/c2c/upload?encrypted_query_param="
                + Uri.EscapeDataString(parameter) + "&filekey=" + Uri.EscapeDataString(fileKey);
            string downloaded = await cdn.UploadAsync(url, encrypted, ct);
            return new() { BotId = binding.BotId, UserId = binding.UserId, Length = content.Length,
                DownloadParameter = downloaded, Base64Key = Convert.ToBase64String(Encoding.ASCII.GetBytes(hex)) };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal async Task<bool?> SendUploadedFileAsync(ClawBinding binding, WeChatUploadedFile file, string name,
        string clientId, CancellationToken ct, Action<string>? observeMessageId = null)
    {
        if (binding.BotId != file.BotId || binding.UserId != file.UserId) throw new WeChatMediaException("uploaded_file_binding_mismatch");
        if (binding.ContextToken.Length == 0) throw new WeChatMediaException("missing_send_context");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\']) >= 0) throw new WeChatMediaException("invalid_file_display_name");
        using var response = await RequestAsync(binding.BaseUrl, "ilink/bot/sendmessage", new
        {
            msg = new { from_user_id = "", to_user_id = binding.UserId, client_id = clientId, message_type = 2,
                message_state = 2, context_token = binding.ContextToken,
                item_list = new[] { new { type = 4, file_item = new { file_name = name,
                    len = file.Length.ToString(CultureInfo.InvariantCulture),
                    media = new { encrypt_query_param = file.DownloadParameter, aes_key = file.Base64Key, encrypt_type = 1 } } } } },
            base_info = BaseInfo
        }, binding.Token, 20, ct);
        string id = String(response.RootElement, "message_id");
        if (id.Length > 0) observeMessageId?.Invoke(id);
        return response.RootElement.TryGetProperty("ret", out _) || id.Length > 0 ? true : null;
    }
}
