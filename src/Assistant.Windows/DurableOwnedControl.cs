using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed record OwnedHostOptions(string DataDirectory, IReadOnlyList<ConfiguredOwnedProfile> Profiles,
    Func<ConfiguredOwnedProfile, CancellationToken, Task<IJsonRpcConnection>>? ConnectionFactory = null,
    WorkspaceWriteCoordinator? Workspace = null,
    Func<ConfiguredOwnedProfile, string, CancellationToken, Task<string>>? ProjectProbe = null);

internal sealed partial class DurableAssistantRuntime
{
    private readonly OwnedHostOptions? ownedOptions;
    private OwnedControlRuntime? ownedRuntime;
    private OwnedControlToolAdapter? ownedAdapter;
    private CancellationTokenSource? ownedLifetime;
    private Task ownedEvents = Task.CompletedTask;
    private Task ownedApprovals = Task.CompletedTask;
    private readonly object ownedLifecycle = new();
    private Task ownedStopping = Task.CompletedTask;
    private int ownedDeliveryFault;
    internal OwnedControlRuntime? OwnedRuntime => Volatile.Read(ref ownedRuntime);

    private async Task ActivateOwnedSessionAsync(ConversationIdentity active, CancellationToken ct)
    {
        if (ownedOptions == null) return;
        var runtime = new OwnedControlRuntime(active, () => Identity, ledger, ownedOptions.DataDirectory,
            ownedOptions.Profiles, ownedOptions.ConnectionFactory, clock: commitmentClock, workspace: ownedOptions.Workspace);
        if (ownedOptions.ProjectProbe != null) runtime.ProjectProbe = ownedOptions.ProjectProbe;
        var cancellation = new CancellationTokenSource();
        Volatile.Write(ref ownedDeliveryFault, 0);
        ownedLifetime = cancellation;
        Volatile.Write(ref ownedRuntime, runtime);
        Volatile.Write(ref ownedAdapter, new(runtime));
        await runtime.PrepareWorkspaceAsync(ct);
        // Neither construction nor these local subscriptions starts an App Server.
        // A tool must first satisfy the configured capability and current-user guard.
        ownedEvents = Task.Run(() => ObserveOwnedEventsAsync(runtime, cancellation.Token));
        ownedApprovals = Task.Run(() => ObserveOwnedApprovalsAsync(runtime, cancellation.Token));
    }

    private Task StopOwnedSessionAsync()
    {
        lock (ownedLifecycle)
        {
            Volatile.Write(ref ownedAdapter, null);
            var runtime = Interlocked.Exchange(ref ownedRuntime, null);
            if (runtime == null) return ownedStopping;
            var cancellation = ownedLifetime;
            ownedLifetime = null;
            return ownedStopping = StopOwnedCoreAsync(runtime, cancellation, ownedEvents, ownedApprovals);
        }
    }
    private static async Task StopOwnedCoreAsync(OwnedControlRuntime runtime, CancellationTokenSource? cancellation,
        Task events, Task approvals)
    {
        cancellation?.Cancel();
        try { await runtime.DisposeAsync(); }
        finally
        {
            try { await Task.WhenAll(events, approvals); }
            catch (OperationCanceledException) { }
            finally { cancellation?.Dispose(); }
        }
    }

    private async Task ObserveOwnedEventsAsync(OwnedControlRuntime runtime, CancellationToken ct)
    {
        try
        {
            await foreach (var item in runtime.ReadEventsAsync(ct))
            {
                if (Identity != runtime.Identity) break;
                var observation = item.Observation;
                if (observation.Task == null) continue;
                var task = new TaskReference(observation.Task.HostId, observation.Task.ThreadId,
                    observation.TurnId, observation.Task.ProfileId);
                // The runtime has already persisted operation observations before
                // publishing. These context records never grant additional control.
                string facts = JsonSerializer.Serialize(new
                {
                    source = "owned_app_server_event", configurationId = item.ConfigurationId,
                    observation.OperationId, observation.Task, observation.TurnId,
                    observation.Method, state = observation.State.ToString(), observation.Code,
                    observation.ObservedAt, independentlyVerified = false
                });
                await ledger.AppendContextAsync(runtime.Identity, ConversationSource.ToolResult,
                    facts, observation.ObservedAt, task, cancellationToken: ct);
                bool lifecycle = IsOwnedTurnObservation(observation);
                if (item.CommitmentObservation is { } ordered)
                {
                    await ledger.ObserveCommitmentTaskAsync(runtime.Identity, ordered, ct);
                    foreach (var work in (await ledger.GetDependentWorkAsync(runtime.Identity, ct)).WorkItems
                        .Where(w => w.ResultTask == ordered.Task && w.Completion == null))
                        await ledger.ObserveDependentCompletionAsync(runtime.Identity, work.Id, ordered, ct);
                }
                if (OwnedEventNotice(item) is not { } notice) continue;
                if (lifecycle && observation.State == ControlState.Completed)
                {
                    var completedSnapshot = await ledger.ReadConversationAsync(runtime.Identity, ct);
                    if (completedSnapshot.DependentWork!.WorkItems.Any(w => w.ResultTask == task
                        && DurableConversationLedger.DependentNoticeFor(completedSnapshot, w)?.RecoveryOfEventId != null))
                    {
                        await NotifyDependentWorkAsync(runtime.Identity, ct);
                        continue;
                    }
                }
                if (lifecycle && observation.State is ControlState.Failed or ControlState.Interrupted
                    && (await ledger.GetDependentWorkAsync(runtime.Identity, ct)).WorkItems.Any(w => w.ResultTask == task && w.NotificationPolicy != null))
                {
                    await NotifyDependentWorkAsync(runtime.Identity, ct);
                    continue;
                }
                try
                {
                    await coordinator.EnqueueNotificationAsync(runtime.Identity, notice.Key, notice.Body, task,
                        ConversationSource.ToolResult, ct, isDefaultTaskNotification: lifecycle);
                }
                catch (LedgerException error) when (error.Code == "notification_suppressed")
                { /* An explicit subscription replaces this lifecycle notice. */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            LastError = "owned_event_notification_failed";
            Volatile.Write(ref ownedDeliveryFault, 1);
        }
    }

    private static bool IsOwnedTurnObservation(ControlObservation value)
        => value.TurnId != null && value.Code is "turn_lifecycle_observed"
            or "read_reconciliation" or "turn_observed_operation_still_unknown" or "interruption_observed";

    internal static (string Key, string Body)? OwnedEventNotice(OwnedRuntimeEvent item)
    {
        var value = item.Observation;
        if (value.Task == null) return null;
        // A query of restored history is a baseline read, not a new completion.
        // Explicit operation reconciliation can still produce a useful update.
        if (value.Method == "thread/read" && value.OperationId == null) return null;
        bool turnEvidence = IsOwnedTurnObservation(value);
        string? body = value.State switch
        {
            ControlState.Completed when turnEvidence
                => "这一轮已经结束，可以查看任务的最新结果。",
            ControlState.Interrupted when turnEvidence => "已经观察到这项自建任务的轮次中断。",
            ControlState.Failed when turnEvidence => "这项自建任务的这一轮出现了失败记录，需要核对报告。",
            ControlState.Failed when value.Code == "turn_ended_without_interruption"
                => "中断请求没有观察到中断结果，这一轮已经以其他状态结束。",
            ControlState.Failed => "这次自建任务操作未成功，需要核对操作记录。",
            ControlState.Unknown => "这次自建任务操作的结果还不能确认，我不会自动重发。",
            _ => null
        };
        if (body == null) return null;
        // A turn can publish both operation-specific and general observations.
        // Preserve each operation in the audit ledger, but one terminal notice.
        string scope = turnEvidence && value.State is ControlState.Completed or ControlState.Interrupted or ControlState.Failed
            ? $"turn\n{value.Task}\n{value.TurnId}"
            : $"operation\n{value.OperationId}\n{value.Method}\n{value.Task}\n{value.TurnId}";
        return ("owned-event/" + WeChatText.Hash(item.ConfigurationId + "\n" + scope + "\n" + value.State), body);
    }

    private async Task ObserveOwnedApprovalsAsync(OwnedControlRuntime runtime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Identity == runtime.Identity)
        {
            try
            {
                foreach (var profile in runtime.Profiles)
                {
                    var session = runtime.LoadedSession(profile.Id);
                    if (session == null) continue;
                    foreach (var approval in session.Client.PendingApprovals())
                    {
                        if (approval.State != ApprovalState.Pending || approval.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                        string key = OwnedControlRuntime.ApprovalNotificationKey(profile.Id, approval.Token);
                        var reference = new TaskReference(approval.Task.HostId, approval.Task.ThreadId,
                            approval.TurnId, approval.Task.ProfileId);
                        var before = await ledger.ReadConversationAsync(runtime.Identity, ct);
                        var notification = before.Events.SingleOrDefault(e => e.ExternalEventKey == key);
                        string body;
                        string[] pages;
                        if (notification == null)
                        {
                            string summary = WeChatText.Clean(approval.Summary, 600);
                            var target = runtime.FindTask(profile.Id, approval.Task.ThreadId);
                            string decisions = string.Join("、", approval.Decisions.Select(decision => decision switch
                            {
                                ApprovalDecision.Accept => "批准",
                                ApprovalDecision.AcceptForSession => "本次会话批准",
                                ApprovalDecision.Decline => "拒绝",
                                _ => "取消审批"
                            }));
                            body = $"这项自建任务正在等审批：{summary}\n"
                                + (approval.Summary.Length > 600
                                    ? "审批摘要过长，以上已截断；请先到 Codex 查看完整内容再决定。\n" : "")
                                + $"目标：{SingleLine(approval.Task.HostId, 100)}/{approval.Task.ThreadId}，轮次 {approval.TurnId}\n"
                                + $"目录：{SingleLine(target?.Snapshot.Cwd ?? "尚未核对", 600)}\n"
                                + $"可回复“决定 编号”，允许的决定：{decisions}。编号：\n"
                                + OwnedControlToolAdapter.ApprovalReference(profile.Id, approval);
                            pages = WeChatReports.Pages(body).ToArray();
                        }
                        else
                        {
                            // The original event and segmentation are the receipt
                            // identity. A new subscription must not rewrite them
                            // from a changed task title or directory snapshot.
                            body = notification.Body;
                            pages = notification.NotificationSegments?.ToArray()
                                ?? throw new LedgerException(LedgerErrors.CorruptStorage, "Approval notification segments are missing.");
                        }
                        var candidate = notification ?? new ConversationEvent(Guid.Empty, runtime.Identity,
                            ConversationSource.Notification, body, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                            before.Revision, Task: reference, ExternalEventKey: key,
                            RequiredDecision: new(profile.Id, approval.Token, approval.ConnectionEpoch, approval.ExpiresAt));
                        if (!runtime.TryValidateRequiredDecision(candidate, out _, out _)) continue;
                        DecisionNotificationResult ensured;
                        try
                        {
                            ensured = await coordinator.EnsureRequiredDecisionNotificationAsync(runtime.Identity,
                                key, reference, new(profile.Id, approval.Token, approval.ConnectionEpoch, approval.ExpiresAt),
                                body, pages, ct);
                        }
                        catch (LedgerException error) when (error.Code == LedgerErrors.InvalidTransition
                            && !runtime.TryValidateRequiredDecision(candidate, out _, out _)) { continue; }
                        notification = ensured.Notification.Event;
                        var segments = ensured.Notification.Outbox;
                        if (segments.Length == 0 || segments.Any(o => o.Stage != OutboxStage.Accepted)) continue;
                        await runtime.TryRecordPendingApprovalAsync(notification, ct);
                    }
                }
                // Do not use the loop's earlier immutable approval snapshots to keep a slot live.
                // A request can resolve, expire or lose its connection while ledger I/O is pending.
                var latest = await ledger.ReadConversationAsync(runtime.Identity, ct);
                if (latest.Context is { PendingApproval: { } pending } context)
                {
                    var notice = latest.Events.SingleOrDefault(e => e.Id == pending.SourceEventId);
                    if (notice == null || notice.Revision < context.StartRevision || pending.DialogueEpoch != context.Epoch
                        || !runtime.TryValidateRequiredDecision(notice, out var current, out _) || !runtime.IsFocused(current)
                        || !OwnedControlRuntime.ContextFocusMatches(context, current)
                        || pending.RequestId != OwnedControlToolAdapter.ApprovalReference(notice.RequiredDecision!.ProviderScope, current)
                        || pending.Task != notice.Task || pending.BackendEpoch != current.ConnectionEpoch
                        || pending.ExpiresAt != current.ExpiresAt || runtime.Presented(notice.Id) == null)
                        await ledger.ResolvePendingApprovalAsync(runtime.Identity, pending.Id, ct);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (LedgerException ex) when (ex.Code == LedgerErrors.StaleRevision)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(100), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            catch (Exception)
            {
                LastError = "owned_approval_notification_failed";
                Volatile.Write(ref ownedDeliveryFault, 1);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    private sealed class RuntimeToolRouter(AssistantToolExecutor existing, CommitmentToolAdapter commitments, DependentWorkToolAdapter dependencies,
        Func<OwnedControlToolAdapter?> owned, Func<ExistingControlRuntime?> shared,
        DurableConversationLedger ledger, Func<bool> controlEnabled, Func<ConversationIdentity, bool> isCurrentBinding)
        : IToolExecutor, IUserAuthorizationPolicy
    {
        public ToolCapability GetCapability(string capability)
        {
            if (capability.StartsWith("existing.", StringComparison.Ordinal))
                return shared()?.GetCapability(capability) ?? new(false, "已有任务控制尚未配置。");
            if (CommitmentToolAdapter.Handles(capability)) return commitments.GetCapability(capability);
            if (DependentWorkToolAdapter.Handles(capability)) return dependencies.GetCapability(capability);
            if (!capability.StartsWith("owned.", StringComparison.Ordinal)) return existing.GetCapability(capability);
            string name = capability[6..];
            if (OwnedControlToolAdapter.Catalog().TryGet(name, out var descriptor)
                && descriptor.HasSideEffects && !controlEnabled()) return new(false, "自建控制已关闭，或操作通知需要恢复。");
            return owned()?.GetCapability(capability) ?? new(false, "自建任务控制尚未配置或验证。");
        }
        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
        {
            var catalog = AssistantToolExecutor.Catalog(includeOwned: true);
            if (!isCurrentBinding(context.Identity) || !catalog.TryGet(call.Name, out var descriptor) || !GetCapability(descriptor.Capability).Available
                || (context.InputWorkId != null && (descriptor.HasSideEffects || context.OperationId != null
                    || !await ledger.VerifyReadOnlyWorkExecutionAsync(context, ct))))
                return new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code = "runtime_execution_guard_rejected" }));
            if (!isCurrentBinding(context.Identity))
                return new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code = "runtime_binding_changed" }));
            return await (ExistingControlRuntime.Handles(call.Name)
                ? shared()?.ExecuteAsync(context, call, ct) ?? Task.FromResult(new ToolExecutionResult(ToolExecutionState.Rejected,
                    JsonSerializer.SerializeToElement(new { code = "existing_control_not_configured" })))
                : DependentWorkToolAdapter.Handles(call.Name) ? dependencies.ExecuteAsync(context, call, ct)
                : CommitmentToolAdapter.Handles(call.Name)
                ? commitments.ExecuteAsync(context, call, ct)
                : OwnedControlToolAdapter.Handles(call.Name)
                ? owned()?.ExecuteAsync(context, call, ct)
                    ?? Task.FromResult(new ToolExecutionResult(ToolExecutionState.Rejected,
                        JsonSerializer.SerializeToElement(new { code = "owned_control_not_configured" })))
                : existing.ExecuteAsync(context, call, ct));
        }
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
            => !isCurrentBinding(request.CurrentMessage.Identity)
                ? ValueTask.FromResult(new UserAuthorizationDecision(false, Reason: "当前账户或微信绑定已经变化。"))
                : ExistingControlRuntime.Handles(request.Call.Name)
                ? shared()?.AuthorizeAsync(request, ct) ?? ValueTask.FromResult(new UserAuthorizationDecision(false, Reason: "已有任务控制尚未配置。"))
                : DependentWorkToolAdapter.Handles(request.Call.Name) ? dependencies.AuthorizeAsync(request, ct)
                : CommitmentToolAdapter.Handles(request.Call.Name)
                ? commitments.AuthorizeAsync(request, ct)
                : OwnedControlToolAdapter.Handles(request.Call.Name)
                ? owned()?.AuthorizeAsync(request, ct)
                    ?? ValueTask.FromResult(new UserAuthorizationDecision(false, Reason: "自建任务控制尚未配置或验证。"))
                : existing.AuthorizeAsync(request, ct);
    }
}
