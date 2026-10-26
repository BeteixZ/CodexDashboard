using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private CancellationTokenSource? wakeLifetime;
    private Task wakeWorker = Task.CompletedTask;
    private readonly object wakeLifecycle = new();
    private Task wakeStopping = Task.CompletedTask;

    private void StartWakeSession(ConversationIdentity active)
    {
        var cancellation = new CancellationTokenSource();
        wakeLifetime = cancellation;
        wakeWorker = Task.Run(() => WakeLoopAsync(active, cancellation.Token));
    }

    private Task StopWakeSessionAsync()
    {
        lock (wakeLifecycle)
        {
            var cancellation = Interlocked.Exchange(ref wakeLifetime, null);
            if (cancellation == null) return wakeStopping;
            return wakeStopping = StopWakeWorkerAsync(cancellation, wakeWorker);
        }
    }

    private static async Task StopWakeWorkerAsync(CancellationTokenSource cancellation, Task worker)
    {
        cancellation.Cancel();
        try { await worker; }
        finally { cancellation.Dispose(); }
    }

    private async Task WakeLoopAsync(ConversationIdentity active, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Identity == active)
        {
            bool failed = false;
            try
            {
                var configuration = await OnUi(() => Task.FromResult(IsCurrentBinding(active)
                    ? channel.ReadWakeConfiguration() : null), ct);
                if (configuration == null) break;
                await ledger.ConfigureWakeChannelAsync(active, configuration, ct);
                if (configuration.Enabled)
                {
                    var pending = await ledger.EnsurePendingWakeAsync(active, configuration.RouteId, ct);
                    if (pending is { Stage: ChannelWakeStage.Pending } && pending.NotBefore <= commitmentClock())
                    {
                        var result = await OnUi(() => channel.SendWakeAsync(active, configuration,
                            token => ledger.BeginWakeSendAsync(active, pending.Id, configuration.RouteId, token), ct), ct);
                        if (result != null)
                        {
                            try
                            {
                                // Save even after a switch/stop: this exact old
                                // claim owns the actual result, never a new identity.
                                await ledger.RecordWakeOutcomeAsync(result.Lease, result.Outcome, result.Evidence,
                                    result.PlatformReceipt, cancellationToken: CancellationToken.None);
                            }
                            finally
                            {
                                // SendWakeAsync awaited the real wire task. A
                                // timeout/cancel request alone never reaches here.
                                await ledger.ReleaseWakeSendAsync(result.Lease, CancellationToken.None);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (LedgerException error) when (error.Code is LedgerErrors.BudgetDeferred
                or LedgerErrors.NotificationSuppressed or LedgerErrors.StaleRevision
                or LedgerErrors.InvalidTransition or LedgerErrors.IdentityMismatch)
            { /* A new input, policy or route can invalidate a pending wake. */ }
            catch
            {
                LastError = "channel_wake_unavailable";
                failed = true;
            }
            try { await Task.Delay(failed ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task<string> ReadWakeStatusAsync(ConversationIdentity active, CancellationToken ct)
    {
        if (!IsCurrentBinding(active)) throw new InvalidOperationException("wake_binding_changed");
        var records = await ledger.GetChannelWakesAsync(active, ct);
        var budget = await ledger.GetChannelWakeBudgetAsync(ct);
        if (!IsCurrentBinding(active)) throw new InvalidOperationException("wake_binding_changed");
        string state = records.Length == 0 ? "尚无已登记的短唤醒。"
            : string.Join("\n", records.OrderByDescending(item => item.CreatedAt).Take(10).Select(item =>
                item.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + WakeStateLabel(item.Stage)));
        return state + "\n短唤醒只提示回来查看，正文另存于待补报记录。"
            + "\n整个助手的唤醒额度：最近1小时 " + budget.StartedLastHour + "/1，最近24小时 "
            + budget.StartedLast24Hours + "/3，实际发送中 " + budget.InFlight + "/1。"
            + "\n已开始的未知尝试仍占用额度；同一次对话间隔不自动重发。";
    }

    private static string WakeStateLabel(ChannelWakeStage stage) => stage switch
    {
        ChannelWakeStage.Pending => "待发短提示，尚未尝试",
        ChannelWakeStage.Sending => "短提示发送中，尚无确定回执",
        ChannelWakeStage.Accepted => "pushplus 已受理短提示，手机送达未知",
        ChannelWakeStage.Rejected => "短提示被明确拒绝，不自动重发",
        ChannelWakeStage.Unknown => "短提示结果未知，不自动重发",
        ChannelWakeStage.Cancelled => "未开始的短提示已取消",
        ChannelWakeStage.NotStarted => "预留后未调用渠道，不自动重试",
        _ => "短提示状态需检查"
    };
}
