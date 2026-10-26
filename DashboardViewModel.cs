using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

public sealed class DashboardViewModel : Observable, IDisposable
{
    private readonly MonitorService monitor;
    private readonly Settings settings;
    private readonly NotificationTransitions transitions = new();
    private bool refreshing;
    private bool filtering;
    public event Action? Updated;
    public event Action<TaskRecord>? Notify;
    public List<DashboardHostSnapshot> Hosts { get; private set; } = new();
    public List<string> HostNames { get; private set; } = new() { "全部机器" };
    public List<DashboardTaskRecord> AllTasks { get; private set; } = new();
    public List<DashboardTaskRecord> VisibleTasks { get; private set; } = new();
    public List<DashboardTaskRecord> HudTasks { get; private set; } = new();
    public List<ProjectFilter> Projects { get; private set; } = new() { new ProjectFilter() };
    public string Project { get; set; } = "全部项目";
    public string Host { get; set; } = "全部机器";
    public string Search { get; set; } = "";
    public int StatusFilter { get; set; }
    public string Footer { get; private set; } = "正在连接数据源…";
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public int RefreshSeconds => settings.RefreshSeconds;
    public string WebStatus { get; private set; } = "网页监控正在启动…";
    public string WebAddress { get; private set; } = "";
    public void SetWebStatus(string status, string address = "") { WebStatus = status; WebAddress = address; Changed(nameof(WebStatus)); Changed(nameof(WebAddress)); }
    private TaskRecord? selectedTask;
    public TaskRecord? SelectedTask { get => selectedTask; set { selectedTask = value; Changed(); } }
    private int activeCount;
    private int attentionCount;
    private int completedCount;
    public int ActiveCount => activeCount;
    public int AttentionCount => attentionCount;
    public int CompletedCount => completedCount;
    public string HostCountLabel => $"{Hosts.Count(h => h.Error == null)} / {Hosts.Count}";
    public string HudSummary => $"运行 {ActiveCount}  ·  关注 {AttentionCount}  ·  今日结束 {CompletedCount}";
    public string HudFooter => refreshing ? "正在读取…" : Hosts.Count == 0 ? "尚未启用数据源" : Hosts.Any(h => h.Error != null) ? "部分连接异常 · 查看主窗口" : "会话记录 · " + DateTime.Now.ToString("HH:mm:ss");
    public DashboardViewModel(Settings settings, IHostCollector? collector = null) { this.settings = settings; monitor = new(settings, collector); }

    public async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        Changed(nameof(HudFooter));
        try
        {
            var hosts = await monitor.RefreshAsync();
            if (hosts == null) return;
            Hosts = hosts.Select(h => new DashboardHostSnapshot(h)).ToList();
            LastRefreshAt = DateTimeOffset.UtcNow;
            AllTasks = Hosts.SelectMany(h => h.Tasks).Cast<DashboardTaskRecord>().ToList();
            ApplyProjectNames();
            var today = DateTime.Today;
            activeCount = AllTasks.Count(t => t.EffectiveStatus == TaskStatusNames.Active);
            attentionCount = AllTasks.Count(t => t.Attention && Recent(t, 1));
            completedCount = AllTasks.Count(t => t.EffectiveStatus == TaskStatusNames.Completed && t.CompletedAt > 0 && DateTimeOffset.FromUnixTimeSeconds((long)t.CompletedAt).LocalDateTime.Date == today);
            foreach (var task in transitions.Observe(AllTasks, LastRefreshAt.Value)) Notify?.Invoke(task);
            HostNames = new[] { "全部机器" }.Concat(hosts.Select(h => h.Name)).ToList();
            if (!HostNames.Contains(Host)) Host = "全部机器";
            Projects = new() { new ProjectFilter { Detail = $"{AllTasks.Count} 个主任务" } };
            Projects.AddRange(AllTasks.GroupBy(t => t.Project).OrderByDescending(g => g.Any(t => t.EffectiveStatus == TaskStatusNames.Active)).ThenBy(g => g.Key)
                .Select(g => new ProjectFilter { Name = g.Key, Label = g.Key, Detail = $"{g.Count(t => t.EffectiveStatus == TaskStatusNames.Active)} 运行 / {g.Count()} 任务" }));
            if (!Projects.Any(p => p.Name == Project)) Project = "全部项目";
            Footer = $"最近读取 {DateTime.Now:HH:mm:ss}  ·  {AllTasks.Count} 个主任务  ·  {settings.RefreshSeconds} 秒刷新";
            foreach (string prop in new[] { nameof(Hosts), nameof(AllTasks), nameof(HostNames), nameof(Projects), nameof(ActiveCount), nameof(AttentionCount), nameof(CompletedCount), nameof(HostCountLabel), nameof(HudSummary), nameof(Footer) }) Changed(prop);
            Filter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Footer = "刷新未完成：" + ex.Message;
            Changed(nameof(Footer));
        }
        finally { refreshing = false; Changed(nameof(HudFooter)); Updated?.Invoke(); }
    }

    public void Filter()
    {
        if (filtering) return;
        filtering = true;
        try
        {
            var query = AllTasks.AsEnumerable();
            if (Project != "全部项目") query = query.Where(t => t.Project == Project);
            if (Host != "全部机器") query = query.Where(t => t.Host == Host);
            if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(t => (t.Title + " " + t.Project + " " + t.Host + " " + t.Summary + " " + t.OneLineSummary).Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
            query = StatusFilter switch
            {
                0 => query.Where(t => Recent(t, 7)), 2 => query.Where(t => t.EffectiveStatus == TaskStatusNames.Active),
                3 => query.Where(t => t.Attention), 4 => query.Where(t => t.EffectiveStatus == TaskStatusNames.Completed),
                5 => query.Where(t => t.EffectiveStatus is TaskStatusNames.Unknown or TaskStatusNames.Offline), _ => query
            };
            var key = SelectedTask?.Key;
            VisibleTasks = query.OrderBy(t => t.Priority).ThenByDescending(t => t.LastActivity).ToList();
            SelectedTask = VisibleTasks.FirstOrDefault(t => t.Key == key) ?? VisibleTasks.FirstOrDefault();
            HudTasks = AllTasks.Where(t => Recent(t, 1)).OrderBy(t => t.Priority).ThenByDescending(t => t.LastActivity).Take(settings.HudRows).ToList();
            Changed(nameof(VisibleTasks)); Changed(nameof(HudTasks));
            Updated?.Invoke();
        }
        finally { filtering = false; }
    }
    public void Select(TaskRecord task)
    {
        Project = "全部项目"; Host = "全部机器"; Search = ""; StatusFilter = 1;
        SelectedTask = task; Filter();
    }
    private static bool Recent(TaskRecord t, int days) => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t.LastActivity < days * 86400 || t.EffectiveStatus is TaskStatusNames.Active or TaskStatusNames.Waiting;
    private void ApplyProjectNames()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(settings.CodexHome, ".codex-global-state.json")));
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            static string Key(string host, string path) => host + ":" + (path.StartsWith(@"\\?\") ? path[4..] : path).TrimEnd('/', '\\').Replace('\\', '/');
            if (doc.RootElement.TryGetProperty("local-projects", out var local))
                foreach (var p in local.EnumerateObject())
                    foreach (var path in p.Value.GetProperty("rootPaths").EnumerateArray())
                        names[Key("本机", path.GetString() ?? "")] = p.Value.GetProperty("name").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("remote-projects", out var remote))
                foreach (var p in remote.EnumerateArray())
                {
                    string host = (p.GetProperty("hostId").GetString() ?? "").Replace("remote-ssh-discovered:", "");
                    names[Key(host, p.GetProperty("remotePath").GetString() ?? "")] = p.GetProperty("label").GetString() ?? "";
                }
            foreach (var task in AllTasks)
                if (names.TryGetValue(Key(task.Host, task.Cwd), out var name)) task.Project = name;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
    }
    public void Dispose() => monitor.Dispose();
}
