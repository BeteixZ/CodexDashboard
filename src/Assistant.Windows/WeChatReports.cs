using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexDashboard;

internal sealed record WeChatReport(string Id, string Host, string Project, string Title, string TurnId, string Status, string Text, string Source, bool Truncated, double CompletedAt);

internal sealed class WeChatReports
{
    private readonly Dictionary<string, WeChatReport> reports = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxReports = 128, MaxCharacters = 1_000_000;
    public static string IdFor(TaskRecord task) => WeChatText.Hash(task.Key + "\n" + task.TurnId)[..12].ToLowerInvariant();
    public void Clear() => reports.Clear();
    public WeChatReport? Find(string id) => reports.GetValueOrDefault(id);
    public static WeChatReport? FromTask(TaskRecord task)
    {
        if (task.Status is not (TaskStatusNames.Completed or TaskStatusNames.Failed or TaskStatusNames.Interrupted)
            || task.TurnId.Length == 0 || task.FinalReportTurnId != task.TurnId || string.IsNullOrWhiteSpace(task.FinalReport)) return null;
        return new(IdFor(task), task.Host, task.Project, task.Title, task.TurnId, task.StatusLabel, task.FinalReport, task.FinalReportSource,
            task.FinalReportTruncated, task.CompletedAt > 0 ? task.CompletedAt : task.FinalReportAt);
    }
    public void Capture(IEnumerable<TaskRecord> tasks)
    {
        foreach (var task in tasks)
        {
            var report = FromTask(task); if (report == null) continue;
            // Retain an immutable copy: a later turn or refreshed snapshot must not
            // change a report halfway through a reader's paginated query.
            reports.TryAdd(report.Id, report);
        }
        int characters = reports.Values.Sum(r => r.Text.Length);
        foreach (var report in reports.Values.OrderBy(r => r.CompletedAt).ThenBy(r => r.Id).ToArray())
        {
            if (reports.Count <= MaxReports && characters <= MaxCharacters) break;
            reports.Remove(report.Id); characters -= report.Text.Length;
        }
    }
    public string Reply(string command, WeChatSnapshot snapshot, DateTimeOffset now)
    {
        Capture(snapshot.Tasks);
        var ordered = reports.Values.OrderByDescending(r => r.CompletedAt).ThenBy(r => r.Id).ToList();
        if (command == "报告列表") return Choices(ordered, "可查询的本轮报告");
        string query = command.Length > 2 ? command[2..].Trim() : "";
        int page = 1;
        var parts = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Length == 12 && parts[0].All(Uri.IsHexDigit))
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1)
                return "页码必须为正整数，例如：报告 " + parts[0] + " 2";
            query = parts[0];
        }
        if (query.Length == 0)
        {
            var latest = snapshot.Tasks.Where(t => t.Status is TaskStatusNames.Completed or TaskStatusNames.Failed or TaskStatusNames.Interrupted)
                .OrderByDescending(t => t.CompletedAt).FirstOrDefault();
            if (latest != null && (ordered.Count == 0 || latest.CompletedAt > ordered[0].CompletedAt))
                return $"最近结束：{WeChatText.Clean(latest.HostProject, 100)}\n{WeChatText.Clean(latest.Title, 140)}\n尚未读取到这一轮的最终回复；请稍后重试，或在 Codex 中查看。发送“报告列表”可选择已有报告。";
            if (ordered.Count == 0) return Choices(ordered, "可查询的本轮报告");
            ordered = ordered.Take(1).ToList();
        }
        else
        {
            var exact = ordered.Where(r => r.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
            ordered = exact.Count > 0 ? exact : ordered.Where(r => (r.Host + "\n" + r.Project + "\n" + r.Title).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ordered.Count != 1) return Choices(ordered, ordered.Count == 0 ? "未找到匹配的报告；可发送“报告列表”查看可用编号。" : "匹配到多份报告，请用编号选择");
        }
        var selected = ordered[0];
        var pages = Pages(selected.Text);
        if (page > pages.Count) return $"报告 {selected.Id} 共有 {pages.Count} 页，请使用 1–{pages.Count} 的页码。";
        string time = selected.CompletedAt > 0 ? "结束于 " + DateTimeOffset.FromUnixTimeSeconds((long)selected.CompletedAt).ToLocalTime().ToString("MM-dd HH:mm:ss") : "未记录结束时间";
        bool stale = snapshot.ReadAt == null || now - snapshot.ReadAt.Value > TimeSpan.FromMinutes(2) || snapshot.Hosts.Any(h => h.Name == selected.Host && h.Error != null);
        string header = $"这是“{WeChatText.Clean(selected.Title, 100)}”在 {WeChatText.Clean(selected.Host, 35)} / {WeChatText.Clean(selected.Project, 45)} 的本轮原文（报告 {selected.Id}，第 {page}/{pages.Count} 页）。\n"
            + $"对应轮次 {WeChatText.Clean(selected.TurnId, 70)}，记录为{WeChatText.Clean(selected.Status, 40)}，{time}；来自{WeChatText.Clean(selected.Source, 50)}。";
        if (stale) header += "\n采集暂不可用或已过期；以下为此前读取的报告。";
        string footer = selected.Truncated ? "\n采集内容达到 64,000 字符上限，这份报告并非全文；完整内容请在 Codex 中查看。" : "";
        footer += page < pages.Count ? $"\n下一页：报告 {selected.Id} {page + 1}" : "\n本轮回复原文结束；本轮结束不代表整个项目完成。";
        return header + "\n\n" + pages[page - 1] + "\n" + footer;
    }
    private static string Choices(IReadOnlyList<WeChatReport> items, string heading)
    {
        if (items.Count == 0) return heading + "\n当前未读取到可查询的最终回复。报告以明确的最终回复或本轮完成记录为来源，不使用中途进展代替。";
        return heading + "\n" + string.Join("\n", items.Take(8).Select(r => $"{r.Id} · {WeChatText.Clean(r.Host, 30)}/{WeChatText.Clean(r.Project, 40)}\n{WeChatText.Clean(r.Title, 100)}"))
            + (items.Count > 8 ? $"\n共 {items.Count} 份，显示最近 8 份；可用“报告 关键词”缩小范围。" : "") + "\n查看：报告 编号\n翻页：报告 编号 2";
    }
    internal static List<string> Pages(string text)
    {
        var pages = new List<string>();
        for (int offset = 0; offset < text.Length;)
        {
            int length = Math.Min(900, text.Length - offset);
            if (offset + length < text.Length)
            {
                if (char.IsHighSurrogate(text[offset + length - 1])) length--;
                int lineBreak = text.LastIndexOf('\n', offset + length - 1, length);
                if (lineBreak >= offset + length / 2) length = lineBreak - offset + 1;
            }
            pages.Add(text.Substring(offset, length)); offset += length;
        }
        if (pages.Count == 0) pages.Add("");
        return pages;
    }
}
