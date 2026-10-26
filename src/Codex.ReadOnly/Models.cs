using System.Text.Json.Serialization;

namespace CodexDashboard;

public class PlanStep
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "pending";
}

public class TimelineEntry
{
    public double Time { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

// Optional collector capability. Fields come from OS directory handles, never task prose.
public sealed record ProjectDirectoryEvidence(int Version, string Kind, string SourceIdentity,
    string RequestedDirectory, string CanonicalDirectory, string DirectoryIdentity);

/// <summary>Collector data plus channel-independent status and report semantics.</summary>
public class TaskRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string Cwd { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectDirectoryEvidence? ProjectEvidence { get; set; }
    public string Model { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string RecordedStatus { get; set; } = "unknown";
    public double LastActivity { get; set; }
    public double StartedAt { get; set; }
    public double CompletedAt { get; set; }
    public string Summary { get; set; } = "";
    public string OneLineSummary { get; set; } = "";
    public string SummarySource { get; set; } = "";
    public string FinalReport { get; set; } = "";
    public string FinalReportTurnId { get; set; } = "";
    public string FinalReportSource { get; set; } = "";
    public bool FinalReportTruncated { get; set; }
    public double FinalReportAt { get; set; }
    public string Activity { get; set; } = "";
    public string Source { get; set; } = "会话记录";
    public List<PlanStep> Plan { get; set; } = new();
    public List<TimelineEntry> Timeline { get; set; } = new();
    [JsonIgnore] public string Host { get; set; } = HostIdentity.LocalDisplayName;
    [JsonIgnore] public bool Disconnected { get; set; }
    // Preserve legacy persisted queue/report keys. Protocol identities are explicit DTOs.
    [JsonIgnore] public string Key => Host + ":" + Id;
    [JsonIgnore] public string EffectiveStatus => Disconnected ? TaskStatusNames.Offline : Status;
    [JsonIgnore] public bool Attention => EffectiveStatus is TaskStatusNames.Waiting or TaskStatusNames.Failed;
    [JsonIgnore] public int Priority => EffectiveStatus switch
    {
        TaskStatusNames.Waiting => 0,
        TaskStatusNames.Failed => 1,
        TaskStatusNames.Active => 2,
        TaskStatusNames.Offline => 3,
        TaskStatusNames.Unknown => 4,
        TaskStatusNames.Interrupted => 5,
        _ => 6
    };
    [JsonIgnore] public string StatusLabel => EffectiveStatus switch
    {
        TaskStatusNames.Active => "运行中 · 记录",
        TaskStatusNames.Waiting => "等待输入 · 记录",
        TaskStatusNames.Completed => "本轮结束",
        TaskStatusNames.Failed => "本轮失败",
        TaskStatusNames.Interrupted => "已中断",
        TaskStatusNames.Offline => "连接异常",
        _ => "状态待确认"
    };
    [JsonIgnore] public string ShortStatus => EffectiveStatus switch
    {
        TaskStatusNames.Active => "运行中",
        TaskStatusNames.Waiting => "等待输入",
        TaskStatusNames.Completed => "本轮结束",
        TaskStatusNames.Failed => "失败",
        TaskStatusNames.Interrupted => "已中断",
        TaskStatusNames.Offline => "连接异常",
        _ => "待确认"
    };
    [JsonIgnore] public string SummaryText => string.IsNullOrWhiteSpace(Summary) ? "本轮尚未记录进展说明。" : Summary;
    [JsonIgnore] public string OverviewText => (Disconnected ? "上次成功读取的概览：" : "")
        + (string.IsNullOrWhiteSpace(OneLineSummary) ? "当前记录暂无一句话概览。" : OneLineSummary);
    [JsonIgnore] public string OverviewSource => string.IsNullOrWhiteSpace(SummarySource) ? "依据会话记录整理" : "依据" + SummarySource + "整理";
    [JsonIgnore] public string HostProject => Host + "  /  " + Project;

    public TaskRecord() { }

    protected TaskRecord(TaskRecord source)
    {
        Id = source.Id; Title = source.Title; Project = source.Project; Cwd = source.Cwd;
        ProjectEvidence = source.ProjectEvidence;
        Model = source.Model; TurnId = source.TurnId; Status = source.Status; RecordedStatus = source.RecordedStatus;
        LastActivity = source.LastActivity; StartedAt = source.StartedAt; CompletedAt = source.CompletedAt;
        Summary = source.Summary; OneLineSummary = source.OneLineSummary; SummarySource = source.SummarySource;
        FinalReport = source.FinalReport; FinalReportTurnId = source.FinalReportTurnId;
        FinalReportSource = source.FinalReportSource; FinalReportTruncated = source.FinalReportTruncated;
        FinalReportAt = source.FinalReportAt; Activity = source.Activity; Source = source.Source;
        Host = source.Host; Disconnected = source.Disconnected;
        Plan = source.Plan.Select(p => new PlanStep { Step = p.Step, Status = p.Status }).ToList();
        Timeline = source.Timeline.Select(t => new TimelineEntry { Time = t.Time, Kind = t.Kind, Text = t.Text }).ToList();
    }
}

public sealed class CollectorResult
{
    public int SchemaVersion { get; set; }
    public double CollectedAt { get; set; }
    public List<TaskRecord> Tasks { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
    public bool Truncated { get; set; }
}

public class HostSnapshot
{
    public string Name { get; set; } = "";
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Error { get; set; }
    public List<TaskRecord> Tasks { get; set; } = new();
    public string Note { get; set; } = "";
}
