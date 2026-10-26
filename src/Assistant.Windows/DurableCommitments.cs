using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private CancellationTokenSource? commitmentLifetime;
    private Task commitmentWorker = Task.CompletedTask;
    private readonly object commitmentLifecycle = new();
    private Task commitmentStopping = Task.CompletedTask;
    private readonly CommitmentToolAdapter commitments;
    private readonly Func<DateTimeOffset> commitmentClock;

    private void StartCommitmentSession(ConversationIdentity active)
    {
        var cancellation = new CancellationTokenSource();
        commitmentLifetime = cancellation;
        commitmentWorker = Task.Run(() => CommitmentLoopAsync(active, cancellation.Token));
        dependentWorker = Task.Run(() => DependencyLoopAsync(active, cancellation.Token));
    }
    private Task StopCommitmentSessionAsync()
    {
        lock (commitmentLifecycle)
        {
            var cancellation = Interlocked.Exchange(ref commitmentLifetime, null);
            if (cancellation == null) return commitmentStopping;
            return commitmentStopping = StopCommitmentWorkerAsync(cancellation, Task.WhenAll(commitmentWorker, dependentWorker));
        }
    }
    private static async Task StopCommitmentWorkerAsync(CancellationTokenSource cancellation, Task worker)
    {
        cancellation?.Cancel();
        try { await worker; }
        finally { cancellation?.Dispose(); }
    }
    private async Task CommitmentLoopAsync(ConversationIdentity active, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Identity == active)
        {
            bool failed = false;
            try
            {
                if (!await OnUi(() => Task.FromResult(IsCurrentBinding(active)), ct)) break;
                var saved = await ledger.GetCommitmentsAsync(active, ct);
                foreach (var target in saved.WorkItems.Where(w => w.Kind == TrackedWorkKind.Subscription
                    && w.Stage == TrackedWorkStage.Active && w.Task != null).Select(w => w.Task!).Distinct())
                {
                    var observation = await ReadCommitmentObservationAsync(target, false, ct);
                    if (observation != null && Identity == active)
                        await ledger.ObserveCommitmentTaskAsync(active, observation, ct);
                }
                await ledger.AdvanceCommitmentsAsync(active, 32, ct);
                foreach (var pending in (await ledger.GetPendingCommitmentChecksAsync(active, ct)).Take(8))
                {
                    if (!await OnUi(() => Task.FromResult(IsCurrentBinding(active)), ct)) break;
                    var claim = await ledger.BeginCommitmentCheckAsync(active, pending.Id, ct);
                    try
                    {
                        CommitmentTaskObservation? observation = null;
                        string? reason = null;
                        try
                        {
                            observation = await ReadCommitmentObservationAsync(claim.Task, true, ct);
                            if (observation == null) reason = "target_not_currently_readable";
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { reason = "readonly_check_unavailable"; }
                        ct.ThrowIfCancellationRequested();
                        if (Identity == active)
                            await ledger.CompleteCommitmentCheckAsync(claim, observation, reason, ct);
                    }
                    finally
                    {
                        // Cancellation is not proof the underlying read stopped.
                        // This finally runs only after the actual await returned.
                        await ledger.ReleaseCommitmentCheckAsync(claim, CancellationToken.None);
                    }
                }
                foreach (var draft in await ledger.GetPendingCommitmentNotificationsAsync(active, ct))
                {
                    if (!await OnUi(() => Task.FromResult(IsCurrentBinding(active)), ct)) break;
                    // Core supplied factual text; this path has no model call or
                    // control execution, and never replays the creation message.
                    await ledger.FinalizeNotificationFallbackAsync(active, draft.Id, draft.ContextBody,
                        WeChatReports.Pages(draft.ContextBody), ct);
                }
                if (Identity == active && (saved.WorkItems.Length > 0 || saved.Runs.Length > 0))
                    coordinator.NotifyDeliveryAvailable(active);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (LedgerException error) when (error.Code is LedgerErrors.NotificationSuppressed
                or LedgerErrors.BudgetDeferred or LedgerErrors.StaleRevision)
            { /* A user change invalidated a pending observation/preparation. */ }
            catch
            {
                LastError = "commitment_processing_unavailable";
                failed = true;
            }
            try { await Task.Delay(failed ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task<CommitmentTaskObservation?> ReadCommitmentObservationAsync(TaskReference target,
        bool check, CancellationToken ct)
    {
        var runtime = OwnedRuntime;
        var ownedMatches = runtime?.Tasks().Where(t => t.Handle.Identity.HostId == target.HostId
            && t.Handle.Identity.ThreadId == target.ThreadId && t.Handle.Identity.ProfileId == target.ProfileId).Take(2).ToArray();
        if (ownedMatches?.Length > 1) return null;
        if (ownedMatches?.Length == 1)
        {
            var task = ownedMatches[0];
            var session = runtime!.LoadedSession(task.ConfigurationId);
            if (session == null || task.Snapshot.CurrentTurnId != target.TurnId) return null;
            if (check)
            {
                // An explicit saved no-activity subscription permits only this
                // read on an already loaded session. Never start another server.
                using var entry = runtime.Enter();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.StopToken);
                var read = await session.Client.ReconcileAsync(task.Handle, linked.Token);
                runtime.Observed(session, task.Handle);
                if (read.Task.CurrentTurnId != target.TurnId) return null;
                return await runtime.CommitmentObservationAsync(session,
                    session.Client.LatestTurnObservation(task.Handle), true, ct);
            }
            // Owned subscriptions consume actual lifecycle/read observations from
            // their serialized event bridge. Re-submitting the public cache can
            // race ahead of that bridge with the same sequence but no activity.
            return null;
        }
        return await OnUi(() =>
        {
            var read = gateway.ReadTask(new(target.HostId, target.ThreadId, target.ProfileId));
            if (read.Value is not { } item || item.Record.TurnId != target.TurnId)
                return Task.FromResult<CommitmentTaskObservation?>(null);
            DateTimeOffset? activity = null;
            if (double.IsFinite(item.Record.LastActivity) && item.Record.LastActivity > 0)
            {
                try { activity = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(item.Record.LastActivity * 1000))); }
                catch (ArgumentOutOfRangeException) { }
                catch (OverflowException) { }
            }
            var state = item.Record.Status switch
            {
                TaskStatusNames.Active => CommitmentTaskState.Running,
                TaskStatusNames.Waiting => CommitmentTaskState.NeedsDecision,
                TaskStatusNames.Completed => CommitmentTaskState.Completed,
                TaskStatusNames.Failed => CommitmentTaskState.Failed,
                TaskStatusNames.Interrupted => CommitmentTaskState.Interrupted,
                _ => CommitmentTaskState.Unknown
            };
            return Task.FromResult<CommitmentTaskObservation?>(new(target, state,
                item.ObservedAt ?? DateTimeOffset.MinValue, activity, item.Fresh,
                !item.Record.Disconnected && item.Fresh, item.EvidenceSource));
        }, ct);
    }
    private static CommitmentTaskState CommitmentState(OwnedTaskState state) => state switch
    {
        OwnedTaskState.Running => CommitmentTaskState.Running,
        OwnedTaskState.WaitingApproval => CommitmentTaskState.NeedsDecision,
        OwnedTaskState.Completed => CommitmentTaskState.Completed,
        OwnedTaskState.Failed => CommitmentTaskState.Failed,
        OwnedTaskState.Interrupted => CommitmentTaskState.Interrupted,
        _ => CommitmentTaskState.Unknown
    };
    internal async Task<bool> ShouldNotifyDefaultTaskAsync(TaskRecord task, CancellationToken ct)
    {
        var id = CodexGateway.Identity(task);
        return await ShouldNotifyDefaultTaskAsync(new TaskReference(id.HostId, id.ThreadId, task.TurnId, id.ProfileId), ct);
    }
    internal async Task<bool> ShouldNotifyDefaultTaskAsync(TaskReference? task, CancellationToken ct)
    {
        var active = Identity;
        if (active == null || task == null || !IsCurrentBinding(active)) return false;
        bool allowed = await ledger.ShouldNotifyDefaultTaskAsync(active, task, ct);
        return allowed && IsCurrentBinding(active);
    }
    private async Task<string> ReadCommitmentBudgetAsync(ConversationIdentity active, CancellationToken ct)
    {
        var status = await ledger.GetCommitmentBudgetStatusAsync(active, ct);
        if (!IsCurrentBinding(active)) throw new OperationCanceledException("当前微信绑定已变化。", ct);
        string lines = string.Join("\n", status.Windows.Select(window =>
        {
            string kind = window.Kind switch
            {
                CommitmentBudgetKind.ReadCheck => "只读检查",
                CommitmentBudgetKind.ModelPreparation => "通知模型整理",
                _ => "提醒与订阅发送"
            };
            string period = window.Window == TimeSpan.FromMinutes(1) ? "最近 1 分钟"
                : window.Window == TimeSpan.FromHours(1) ? "最近 1 小时" : "最近 24 小时";
            return $"{kind}（{period}）：{window.Used}/{window.Limit}"
                + (window.NextAvailableAt is { } next
                    ? "，最早可用 " + next.ToLocalTime().ToString("MM-dd HH:mm:ss zzz") : "");
        }));
        return $"正在做只读检查：{status.ReadChecksInFlight}/{status.MaximumReadChecksInFlight}\n" + lines
            + "\n这些额度由当前绑定的所有提醒和订阅共享；已开始或结果未知的尝试仍占额度。普通对话和必要审批提示不受这项限额隐藏。";
    }
}
