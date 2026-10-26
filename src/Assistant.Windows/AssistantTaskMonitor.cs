using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Each assistant owns a collector and a notification baseline, independent of Dashboard.
internal sealed class AssistantTaskMonitor : IDisposable
{
    private readonly MonitorService collector;
    private readonly NotificationTransitions transitions = new();
    private readonly Dictionary<string, TaskRecord> pendingNotifications = new(StringComparer.Ordinal);
    private WeChatSnapshot snapshot = new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>());
    public IReadOnlyList<TaskRecord> AllTasks { get; private set; } = Array.Empty<TaskRecord>();
    public IReadOnlyList<HostSnapshot> Hosts { get; private set; } = Array.Empty<HostSnapshot>();
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public event Func<TaskRecord, Task>? Notify;
    internal AssistantTaskMonitor(Settings settings, IHostCollector? source = null) => collector = new(settings, source);
    public async Task RefreshAsync()
    {
        var hosts = await collector.RefreshAsync();
        if (hosts == null) return;
        Hosts = hosts;
        AllTasks = hosts.SelectMany(h => h.Tasks).ToArray();
        LastRefreshAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref snapshot, new(AllTasks, LastRefreshAt, Hosts));
        foreach (var task in transitions.Observe(AllTasks, LastRefreshAt.Value))
            pendingNotifications.TryAdd(WeChatText.Hash(task.Key + "\n" + task.TurnId + "\n" + task.Status + "\n" + task.StartedAt), task);
        foreach (var pair in pendingNotifications.ToArray())
        {
            if (Notify != null)
                foreach (Func<TaskRecord, Task> handler in Notify.GetInvocationList())
                    await handler(pair.Value);
            pendingNotifications.Remove(pair.Key);
        }
    }
    internal WeChatSnapshot Snapshot() => Volatile.Read(ref snapshot);
    public void Dispose() => collector.Dispose();
}
