using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;
internal static class WeChatFieldProbeTests
{
    internal static Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--field-probe-test", var output]) return Task.FromResult(false);
        Directory.CreateDirectory(output);
        using var doc = JsonDocument.Parse("""{"context_token":"SECRET_TOKEN","from_user_id":"SECRET_USER","SECRET_KEY":"SECRET_VALUE","item_list":[{"type":3,"voice_item":{"text":"PRIVATE_TRANSCRIPT","media":{"aes_key":"SECRET_AES","url":"PRIVATE_URL"}}},{"type":1,"ref_msg":{"message_id":"SECRET_ID","text_item":{"text":"PRIVATE_QUOTE"}}}]}""");
        string result = JsonSerializer.Serialize(WeChatFieldProbe.Shape(doc.RootElement));
        bool clean = !result.Contains("SECRET") && !result.Contains("PRIVATE");
        bool useful = result.Contains("voice_item") && result.Contains("ref_msg") && result.Contains("itemType") && result.Contains("length");
        string? previous = Environment.GetEnvironmentVariable("CODEX_ASSISTANT_FIELD_PROBE");
        bool matched, isolated, evidenceClean;
        try
        {
            string probe = Path.Combine(output, "id-evidence");
            Environment.SetEnvironmentVariable("CODEX_ASSISTANT_FIELD_PROBE", Path.GetFullPath(probe));
            var account = new ClawBinding { BotId = "SECRET_BOT", UserId = "SECRET_USER" };
            using var sent = JsonDocument.Parse("""{"ret":0,"message_id":9007199254740993}""");
            using var quoted = JsonDocument.Parse("""{"item_list":[{"ref_msg":{"message_item":{"msg_id":"9007199254740993"}}}]}""");
            WeChatFieldProbe.RecordSend(account, "SECRET_CLIENT", sent.RootElement);
            WeChatFieldProbe.CompareReference(account, quoted.RootElement);
            WeChatFieldProbe.CompareReference(new ClawBinding { BotId = "OTHER", UserId = account.UserId }, quoted.RootElement);
            var files = Directory.GetFiles(probe, "id-match-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            using var match = JsonDocument.Parse(File.ReadAllText(files[1]));
            using var other = JsonDocument.Parse(File.ReadAllText(files[2]));
            matched = match.RootElement.GetProperty("matches").GetArrayLength() == 1;
            isolated = other.RootElement.GetProperty("matches").GetArrayLength() == 0;
            evidenceClean = Array.TrueForAll(files, f => !File.ReadAllText(f).Contains("SECRET") && !File.ReadAllText(f).Contains("9007199254740993"));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_ASSISTANT_FIELD_PROBE", previous); }
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { valuesRemoved = clean, shapeRetained = useful, matched, isolated, evidenceClean }));
        exit(clean && useful && matched && isolated && evidenceClean ? 0 : 1); return Task.FromResult(true);
    }
}
