using System.Text.Json;

namespace CodexDashboard;

internal static class OwnedReportText
{
    internal static string Render(JsonElement facts, int number, int totalPages, string snapshotId)
    {
        string text = facts.GetProperty("finalText").GetString() ?? "";
        string body = string.IsNullOrWhiteSpace(text) ? "这一页没有最终答复正文。" : text;
        string title = totalPages == 1 ? "任务回复：\n" : $"任务回复（第 {number}/{totalPages} 页）：\n";
        string limited = facts.GetProperty("sourceTruncated").GetBoolean()
            ? "\n报告来源有截断，当前内容不代表完整报告。" : "";
        string next = number < totalPages ? "回复“报告下一页”继续，也可说“报告第2页”。\n\n" : "";
        return title + next + body + limited;
    }
}
