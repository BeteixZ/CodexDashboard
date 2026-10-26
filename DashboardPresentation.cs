using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace CodexDashboard;

public static class DashboardPresentation
{
    public static string ColorFor(string effectiveStatus) => effectiveStatus switch
    {
        TaskStatusNames.Active => "#10893E",
        TaskStatusNames.Waiting => "#C27C00",
        TaskStatusNames.Failed => "#D13438",
        TaskStatusNames.Completed => "#0078D4",
        _ => "#8A8886"
    };

    public static string RelativeAge(double time)
    {
        if (time <= 0) return "暂无更新时间";
        double delta = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time);
        if (delta < 60) return $"{(int)delta} 秒前";
        if (delta < 3600) return $"{(int)(delta / 60)} 分钟前";
        if (delta < 86400) return $"{(int)(delta / 3600)} 小时前";
        return $"{(int)(delta / 86400)} 天前";
    }

    internal static string FormatDuration(double seconds) => seconds < 3600
        ? $"{(int)(seconds / 60)} 分 {(int)(seconds % 60)} 秒"
        : $"{(int)(seconds / 3600)} 小时 {(int)(seconds % 3600 / 60)} 分";
}

public sealed class DashboardTaskRecord : TaskRecord
{
    public DashboardTaskRecord(TaskRecord source) : base(source)
    {
        Plan = source.Plan.Select(p => (PlanStep)new DashboardPlanStep(p)).ToList();
        Timeline = source.Timeline.Select(t => (TimelineEntry)new DashboardTimelineEntry(t)).ToList();
    }

    [JsonIgnore] public string Color => DashboardPresentation.ColorFor(EffectiveStatus);
    [JsonIgnore] public string HudTip => $"{Project} · {Host}\n{StatusLabel}\n{Title}\n{Preview}";
    [JsonIgnore] public string ShortAge => Age.Replace(" 秒前", "s").Replace(" 分钟前", "m").Replace(" 小时前", "h").Replace(" 天前", "d");
    [JsonIgnore] public string Age => DashboardPresentation.RelativeAge(LastActivity);
    [JsonIgnore] public int CompletedStepCount => Plan.Count(p => p.Status == TaskStatusNames.Completed);
    [JsonIgnore] public string PlanLabel => Plan.Count == 0 ? "暂无结构化计划" : $"当前计划  {CompletedStepCount} / {Plan.Count} 步";
    [JsonIgnore] public double PlanPercent => Plan.Count == 0 ? 0 : 100.0 * CompletedStepCount / Plan.Count;
    [JsonIgnore] public string Preview => OverviewText;
    [JsonIgnore] public string Duration => StartedAt <= 0 ? "—" : DashboardPresentation.FormatDuration(
        Math.Max(0, (CompletedAt > 0 ? CompletedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - StartedAt));
    [JsonIgnore] public string DataNote => Disconnected ? "连接异常，以下为上次成功读取的内容。"
        : "根据 Codex 会话记录推断；5 分钟无活动时标为待确认。等待审批状态可能尚未写入记录。";
}

public sealed class DashboardPlanStep : PlanStep
{
    public DashboardPlanStep(PlanStep source) { Step = source.Step; Status = source.Status; }
    [JsonIgnore] public string Mark => Status == "completed" ? "✓" : Status == "in_progress" ? "●" : "○";
}

public sealed class DashboardTimelineEntry : TimelineEntry
{
    public DashboardTimelineEntry(TimelineEntry source) { Time = source.Time; Kind = source.Kind; Text = source.Text; }
    [JsonIgnore] public string Clock => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)Time).ToLocalTime().ToString("MM-dd HH:mm:ss") : "";
}

public sealed class DashboardHostSnapshot : HostSnapshot
{
    public DashboardHostSnapshot(HostSnapshot source)
    {
        Name = source.Name; LastSuccess = source.LastSuccess; Error = source.Error; Note = source.Note;
        Tasks = source.Tasks.Select(t => (TaskRecord)new DashboardTaskRecord(t)).ToList();
    }

    [JsonIgnore] public string Display => Error != null ? Name + " · 连接异常" : Name + " · 已读取";
    [JsonIgnore] public string Color => Error != null ? "#C27C00" : "#10893E";
    [JsonIgnore] public string Detail => Error ?? ("最近读取 " + (LastSuccess?.ToLocalTime().ToString("HH:mm:ss") ?? "—") + "  " + Note);
}
