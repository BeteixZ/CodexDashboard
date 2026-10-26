using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

public partial class AssistantApp
{
    internal void ShowHealth()
    {
        if (exiting) return;
        var dialog = new AssistantHealthWindow(ReadHealthSnapshotAsync,
            active => durable == null ? active == null : durable.IsCurrentHealthIdentity(active))
            { Owner = window };
        void Changed() => dialog.InvalidateIfIdentityChanged();
        if (wechat != null) wechat.Changed += Changed;
        if (model != null) model.Changed += Changed;
        try { dialog.ShowDialog(); }
        finally
        {
            if (wechat != null) wechat.Changed -= Changed;
            if (model != null) model.Changed -= Changed;
        }
    }

    internal async Task<AssistantHealthSnapshot> ReadHealthSnapshotAsync(CancellationToken ct = default)
    {
        var records = durable == null ? null : await durable.ReadHealthAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (durable != null && !durable.IsCurrentHealthIdentity(records?.Identity))
            throw new InvalidOperationException("health_binding_changed");
        return new(DateTimeOffset.UtcNow, wechat?.ReadHealth(), model != null, settings.ChatGpt.Enabled,
            model?.CompletedRequests ?? 0, model?.LastCompletedAt, model?.LastFailure?.At,
            settings.RemoteHosts.Count + (settings.LocalEnabled ? 1 : 0),
            monitor?.Hosts.Count ?? 0, monitor?.Hosts.Count(item => item.Error != null) ?? 0,
            monitor?.AllTasks.Count ?? 0, monitor?.LastRefreshAt,
            status != null && status.EndpointFailure == null, records, Settings.DirectoryPath, workspaceWrites?.Snapshot());
    }
}
