using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexDashboard;

public sealed class NotificationTransitions
{
    private sealed record Seen(string Turn, double Started, string Status, bool Disconnected);
    private readonly Dictionary<string, Seen> prior = new();
    private double? lastRead;
    public List<TaskRecord> Observe(IReadOnlyList<TaskRecord> tasks, DateTimeOffset now)
    {
        var notifications = new List<TaskRecord>();
        foreach (var task in tasks)
        {
            bool fresh = !task.Disconnected && task.Status != TaskStatusNames.Unknown;
            bool terminal = task.Status is TaskStatusNames.Completed or TaskStatusNames.Failed;
            bool recentTurn = lastRead.HasValue && task.StartedAt >= lastRead.Value - 1;
            if (prior.TryGetValue(task.Key, out var old))
            {
                bool sameTurn = task.TurnId.Length > 0 ? task.TurnId == old.Turn : task.StartedAt == old.Started;
                if (fresh && !old.Disconnected)
                {
                    bool ended = terminal && (sameTurn && old.Status is TaskStatusNames.Active or TaskStatusNames.Waiting
                        || !sameTurn && recentTurn);
                    bool waiting = task.Status == TaskStatusNames.Waiting && (!sameTurn || old.Status != TaskStatusNames.Waiting);
                    if (ended || waiting) notifications.Add(task);
                }
                // A quiet long-running command may temporarily become unknown; keep its
                // observed active state so the eventual completion still produces a notification.
                string status = task.Status == TaskStatusNames.Unknown && sameTurn && !task.Disconnected ? old.Status : task.Status;
                prior[task.Key] = new(task.TurnId, task.StartedAt, status, task.Disconnected);
            }
            else
            {
                // Catch short turns born and completed between polls, but never announce
                // historical tasks on initial load or after a connection recovery.
                if (fresh && recentTurn && (terminal || task.Status == TaskStatusNames.Waiting)) notifications.Add(task);
                prior[task.Key] = new(task.TurnId, task.StartedAt, task.Status, task.Disconnected);
            }
        }
        var keys = tasks.Select(t => t.Key).ToHashSet();
        foreach (string key in prior.Keys.Where(k => !keys.Contains(k)).ToArray()) prior.Remove(key);
        lastRead = now.ToUnixTimeMilliseconds() / 1000.0;
        return notifications;
    }
}
