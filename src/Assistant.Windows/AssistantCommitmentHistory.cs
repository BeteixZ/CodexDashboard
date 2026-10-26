using System;
using System.Collections.Generic;
using System.Linq;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantCommitmentPage(string Query, int PageIndex, int TotalCount,
    IReadOnlyList<AssistantWorkspaceRow> Rows, IReadOnlyList<CommitmentDetail> Details)
{
    internal bool HasPrevious => PageIndex > 0;
    internal bool HasNext => (PageIndex + 1) * AssistantCommitmentHistory.PageSize < TotalCount;
}

// Browsing stays on one immutable workspace read until the user refreshes.
// Full text is searched before truncating previews; details are built for this page only.
internal sealed class AssistantCommitmentHistory(ConversationSnapshot saved)
{
    internal const int PageSize = 50;

    internal AssistantCommitmentPage ReadPage(string query = "", int pageIndex = 0)
    {
        query = query.Trim();
        var matches = saved.WorkItems.Where(item => item.Identity == saved.Identity && (query.Length == 0
            || item.Goal.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Id.ToString("N").Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Id.ToString("D").Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Task is { } task && (task.HostId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || task.ThreadId.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id).ToArray();
        pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, (matches.Length - 1) / PageSize));
        var selected = matches.Skip(pageIndex * PageSize).Take(PageSize).ToArray();
        var rows = selected.Select(item =>
        {
            var due = saved.Schedules.SingleOrDefault(schedule => schedule.WorkItemId == item.Id);
            string summary = Preview(item.Goal);
            if (due != null)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(due.TimeZoneId);
                summary += " · " + TimeZoneInfo.ConvertTime(due.DueAtUtc, zone).ToString("yyyy-MM-dd HH:mm:ss zzz")
                    + " (" + due.TimeZoneId + ")";
            }
            else summary += " · " + (item.Task is { } task ? Preview(task.HostId) + " / " + Preview(task.ThreadId)
                + (task.TurnId == null ? "" : " / " + Preview(task.TurnId)) : "当前对话");
            summary += " · " + CommitmentToolAdapter.PolicySummary(item, due);
            return new AssistantWorkspaceRow(item.Id.ToString("N"),
                item.Kind == TrackedWorkKind.Reminder ? "一次性提醒" : "任务订阅", item.Stage switch
                {
                    TrackedWorkStage.Active => "等待触发",
                    TrackedWorkStage.Triggered => "已触发，待渠道受理",
                    TrackedWorkStage.Paused => "已暂停",
                    TrackedWorkStage.Completed => "本次已结束，送达须另核对",
                    TrackedWorkStage.Cancelled => "已取消后续触发",
                    TrackedWorkStage.Expired => "已过截止时间",
                    _ => "需要决定"
                }, summary, item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"));
        }).ToArray();
        return new(query, pageIndex, matches.Length, rows, CommitmentDetails.Read(saved, selected.Select(item => item.Id)));
    }

    private static string Preview(string value)
    {
        string text = value.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 180 ? text : text[..180] + "…";
    }
}
