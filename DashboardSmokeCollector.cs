using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Explicit UI smoke entry point only: never reads a real Codex task or starts a process.
internal sealed class DashboardSmokeCollector : IHostCollector
{
    public Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (settings.PythonPath.EndsWith("nonexistent-python.exe", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HostSnapshot { Name = host, Error = "synthetic collector unavailable" });
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var snapshot = new HostSnapshot { Name = host, LastSuccess = DateTimeOffset.UtcNow };
        string[] statuses = { TaskStatusNames.Active, TaskStatusNames.Waiting, TaskStatusNames.Completed, TaskStatusNames.Failed };
        for (int index = 0; index < statuses.Length; index++)
        {
            bool ended = index >= 2;
            snapshot.Tasks.Add(new TaskRecord
            {
                Id = $"00000000-0000-0000-0000-{index + 1:000000000000}", Host = host,
                Project = "隔离演示项目", Cwd = Path.Combine(settings.CodexHome, "fixture-project"),
                Title = new[] { "实现独立助手", "等待确认的合成任务", "采集回归已完成", "模拟失败与恢复" }[index],
                TurnId = $"synthetic-turn-{index}", Status = statuses[index], RecordedStatus = statuses[index],
                StartedAt = now - 120, LastActivity = now, CompletedAt = ended ? now - 5 : 0,
                OneLineSummary = "这是用于界面验证的合成任务，没有访问真实工作会话。",
                Summary = "检查任务状态、筛选、HUD 和网页展示。",
                FinalReport = ended ? "隔离测试报告。该内容不代表真实项目结果。" : "",
                FinalReportTurnId = ended ? $"synthetic-turn-{index}" : "",
                Plan = new() { new() { Step = "检查独立宿主", Status = "completed" }, new() { Step = "检查展示", Status = "in_progress" } },
                Timeline = new() { new() { Time = now - 10, Kind = "message", Text = "合成进展事件" } }
            });
        }
        return Task.FromResult(snapshot);
    }
}
