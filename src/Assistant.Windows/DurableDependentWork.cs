using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private readonly DependentWorkToolAdapter dependencies;
    private Task dependentWorker = Task.CompletedTask;
    private readonly SemaphoreSlim dependencyCycle = new(1, 1);
    private int dependencyCursor;
    private bool DependencyControlEnabled() => settings.ChatGpt.TaskControlEnabled && settings.WeChat.ClawBotEnabled
        && settings.ChatGpt.IncludeTaskContext && Volatile.Read(ref ownedDeliveryFault) == 0;

    private async Task DependencyLoopAsync(ConversationIdentity active, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Identity == active)
        {
            try { await AdvanceDependenciesAsync(active, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (LedgerException error) when (error.Code is LedgerErrors.Conflict or LedgerErrors.InvalidTransition) { }
            catch { LastError = "dependent_work_processing_unavailable"; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
    internal async Task AdvanceDependenciesAsync(ConversationIdentity active, CancellationToken ct = default)
    {
        await dependencyCycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AdvanceDependencyCycleAsync(active, ct).ConfigureAwait(false);
            await NotifyDependentWorkAsync(active, ct).ConfigureAwait(false);
        }
        finally { dependencyCycle.Release(); }
    }
    private async Task AdvanceDependencyCycleAsync(ConversationIdentity active, CancellationToken ct)
    {
        if (!await OnUi(() => Task.FromResult(IsCurrentBinding(active)), ct)) return;
        var runtime = OwnedRuntime;
        var saved = await ledger.GetDependentWorkAsync(active, ct);
        var waiting = saved.WorkItems.Where(w => w.Stage == DependentWorkStage.Waiting).OrderBy(w => w.CreatedAt).ToArray();
        int start = waiting.Length == 0 ? 0 : dependencyCursor % waiting.Length;
        var batch = waiting.Skip(start).Concat(waiting.Take(start)).Take(8).ToArray();
        dependencyCursor = start + batch.Length;
        foreach (var work in batch)
        {
            if (!IsCurrentBinding(active)) return;
            // Check expiry and work-version prerequisites before requesting any backend reads.
            var preliminary = await ledger.EvaluateDependentWorkAsync(active, work.Id, work.Version, [], cancellationToken: ct);
            if (preliminary.Code != "evidence_incomplete") continue;
            if (!DependencyControlEnabled() || runtime == null || !runtime.Active || runtime.Identity != active) continue;
            var evidence = ImmutableArray.CreateBuilder<DependencyEvidence>();
            var observations = new Dictionary<TaskReference, DependencyEvidence>();
            foreach (var target in preliminary.EvidenceTargets)
            {
                if (!observations.TryGetValue(target, out var proof))
                {
                    bool commands = work.Requirements.Select((r, i) => (r, i)).Any(p => p.r.CommandSuccess != null && preliminary.EvidenceTargets[p.i + 1] == target);
                    proof = await runtime.ReadDependencyEvidenceAsync(target, ct, commands).ConfigureAwait(false);
                    if (proof == null) break;
                    observations.Add(target, proof);
                }
                evidence.Add(proof);
            }
            if (!IsCurrentBinding(active) || !DependencyControlEnabled()) return;
            var evaluated = await ledger.EvaluateDependentWorkAsync(active, work.Id, work.Version, evidence.ToImmutable(), true, ct);
            if (evaluated.Lease is { } lease)
                await runtime.DispatchDependencyAsync(lease, () => IsCurrentBinding(active) && DependencyControlEnabled(), ct).ConfigureAwait(false);
        }
        if (!IsCurrentBinding(active) || !DependencyControlEnabled() || runtime == null || !runtime.Active || runtime.Identity != active) return;
        // Event delivery normally supplies this evidence. Read once when recovering a missed event.
        foreach (var work in (await ledger.GetDependentWorkAsync(active, ct)).WorkItems.Where(w => w.ResultTask != null && w.Completion == null).Take(8))
        {
            var proof = await runtime.ReadDependencyEvidenceAsync(work.ResultTask!, ct).ConfigureAwait(false);
            if (proof != null && IsCurrentBinding(active))
                await ledger.ObserveDependentCompletionAsync(active, work.Id, proof.Observation, ct);
        }
    }

    private async Task NotifyDependentWorkAsync(ConversationIdentity active, CancellationToken ct)
    {
        if (!IsCurrentBinding(active)) return;
        var snapshot = await ledger.ReadConversationAsync(active, ct);
        bool hasNotice = snapshot.Events.Any(e => e.DependentWorkNotice != null);
        foreach (var work in snapshot.DependentWork!.WorkItems)
        {
            if (!IsCurrentBinding(active)) return;
            var expected = DurableConversationLedger.DependentNoticeFor(snapshot, work);
            if (expected == null) continue;
            if (snapshot.Events.Any(e => e.DependentWorkNotice == expected)) continue;
            string reason = work.Stage switch
            {
                DependentWorkStage.Running => "之前等待重新处理的安排已继续，后续指令已被受理；实际结果还在等待。",
                DependentWorkStage.Completed => "之前等待重新处理的安排已经执行，本轮已结束。测试是否通过仍需核对具体结果。",
                DependentWorkStage.Expired => "等待期限已过，这项安排已停止等待。",
                DependentWorkStage.Failed when work.Completion?.State == CommitmentTaskState.Interrupted => "这项安排执行的轮次已中断。",
                DependentWorkStage.Failed => "这项安排执行的轮次有明确的失败记录，原因还需要核对报告。",
                _ => work.Reason switch
                {
                    "predecessor_failed" => "前置任务失败或中断，后续安排需要重新决定。",
                    "predecessor_command_failed" => "指定测试命令的最后一次执行未成功，后续安排需要重新决定。",
                    "predecessor_changed_or_failed" => "前置工作已修改或不能继续，后续安排需要重新决定。",
                    "task_or_configuration_changed" => "原任务轮次或连接配置已经变化，这项安排需要重新决定。",
                    "dispatch_budget_exhausted" => "这项安排已达到派发尝试上限，需要重新决定。",
                    _ => "这项安排尚未获得派发受理，需要核对原记录再决定。"
                }
            };
            string body = reason + "\n\n原指令：\n" + work.Instructions + "\n\n可以发“依赖列表”查看这项安排。";
            hasNotice |= await ledger.EnqueueDependentWorkNotificationAsync(active, expected, body, WeChatReports.Pages(body), ct) != null;
        }
        // Also wakes a previously captured notice when quiet hours or the shared budget have elapsed.
        if (hasNotice) coordinator.NotifyDeliveryAvailable(active);
    }
}
