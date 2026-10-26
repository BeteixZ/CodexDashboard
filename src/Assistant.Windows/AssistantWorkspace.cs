using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantWorkspaceRow(string Id, string Kind, string State, string Target, string Updated);
internal sealed record AssistantWorkspaceSnapshot(
    string Capabilities, string Context, string Diagnostic, DateTimeOffset ReadAt,
    IReadOnlyList<AssistantWorkspaceRow> Work, IReadOnlyList<AssistantWorkspaceRow> Operations,
    IReadOnlyList<AssistantWorkspaceRow> Messages,
    IReadOnlyList<AssistantWorkspaceRow>? Commitments = null,
    IReadOnlyList<AssistantConversationRow>? Conversation = null,
    ConversationIdentity? Identity = null, int ConversationRecordCount = 0,
    IReadOnlyList<CommitmentDetail>? CommitmentDetails = null,
    IReadOnlyList<AssistantOperationDetail>? OperationDetails = null,
    AssistantConversationPage? ConversationPage = null,
    AssistantCommitmentHistory? CommitmentHistory = null,
    IReadOnlyList<AssistantDependencyDetail>? Dependencies = null,
    IReadOnlyList<AssistantMessageDetail>? MessageDetails = null);

internal sealed record AssistantMessageDetail(ConversationIdentity Identity, string Id, string Kind, string Body);

internal sealed partial class DurableAssistantRuntime
{
    // The desktop view reads only this instance's already-open ledger. Opening
    // it never polls Codex, submits a command, sends a message or calls a model.
    internal async Task<AssistantWorkspaceSnapshot> ReadWorkspaceAsync(CancellationToken ct = default)
    {
        string capabilities = ControlCapabilitySummary() + "\n\n渠道与媒体\n" + ChannelCapabilitySummary();
        var active = Identity;
        if (active == null)
            return new(capabilities, "微信绑定尚未激活；此处没有载入其他绑定的工作记录。",
                LastError ?? "当前没有已登记的运行错误。", DateTimeOffset.UtcNow, [], [], []);
        var saved = await ledger.ReadConversationAsync(active, ct);
        var catchUpProgress = DurableConversationLedger.GetCatchUpProgress(saved);
        var sourceCoverage = catchUpProgress.SelectMany(item => item.Sources).ToDictionary(item => item.OutboxId);
        // All rows and their selected details use this same immutable snapshot.

        capabilities += "\n\n提醒与订阅\n" + await ReadCommitmentBudgetAsync(active, ct);
        capabilities += "\n\n短唤醒\n" + await ReadWakeStatusAsync(active, ct);
        var wakes = await ledger.GetChannelWakesAsync(active, ct);
        if (!ReferenceEquals(Identity, active) || !CurrentBindingMatches(active))
            throw new InvalidOperationException("workspace_binding_changed");
        static string When(DateTimeOffset time) => time.ToLocalTime().ToString("MM-dd HH:mm:ss");
        static string Target(TaskReference? task) => task == null ? "当前对话"
            : SingleLine(task.HostId, 100) + " / " + SingleLine(task.ThreadId, 120)
                + (task.TurnId == null ? "" : " / " + SingleLine(task.TurnId, 100));
        var work = new List<AssistantWorkspaceRow>();
        work.AddRange(queue.Entries.Where(entry => TaskControlQueue.IsPending(entry) || entry.State is "sending" or "unconfirmed")
            .TakeLast(200).Select(entry => new AssistantWorkspaceRow(entry.Id, "原任务接续",
                QueueState(entry.State), SingleLine(entry.Detail, 180), When(entry.CreatedAt))));
        work.AddRange(saved.InputWork.Where(item => item.Stage is InputWorkStage.Queued or InputWorkStage.Reading
            or InputWorkStage.NeedsConfirmation or InputWorkStage.Failed or InputWorkStage.Unknown)
            .TakeLast(200).Select(item => new AssistantWorkspaceRow(item.Id.ToString("N"), "以前的输入",
                item.Stage switch
                {
                    InputWorkStage.Queued => "等待补做只读处理", InputWorkStage.Reading => "正在补做只读处理",
                    InputWorkStage.NeedsConfirmation => "需要按最新要求重新明确",
                    InputWorkStage.Unknown => "答复发送结果未知", _ => "尚未完成"
                },
                SingleLine(saved.Inbox.Single(input => input.SourceMessageId == item.SourceMessageId).Body, 180),
                When(item.UpdatedAt))));
        work.AddRange(saved.Operations.Where(item => OwnedControlToolAdapter.Handles(item.Action)
                && item.Stage is OperationStage.Queued or OperationStage.Dispatching or OperationStage.Accepted
                    or OperationStage.Running or OperationStage.Unknown)
            .TakeLast(200).Select(item => new AssistantWorkspaceRow(item.Id.ToString("N"), ActionLabel(item.Action),
                OperationLabel(item.Stage), Target(item.Task), When(item.UpdatedAt))));
        var operations = saved.Operations.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id)
            .Select(item => new AssistantWorkspaceRow(item.Id.ToString("N"), ActionLabel(item.Action),
                OperationLabel(item.Stage), Target(item.Task), When(item.UpdatedAt))).ToArray();
        string MessageState(OutboxMessage item) => ConversationMessageState(saved, active, sourceCoverage, item);
        var messages = saved.Outbox.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id)
            .Select(item => new AssistantWorkspaceRow(item.Id.ToString("N"),
            !saved.CatchUpBatches.IsDefault && saved.CatchUpBatches.Any(batch => batch.DeliveryEventId == item.NotificationEventId)
                ? "历史通知补报" : item.Kind switch
            {
                OutboxKind.Notification => "主动通知", OutboxKind.DeferredReply => "以前输入的补答", _ => "对话回复"
            }, MessageState(item), SingleLine(item.Body, 180), When(item.UpdatedAt))).ToList();
        messages.AddRange(wakes.OrderByDescending(item => item.CreatedAt)
            .Select(item => new AssistantWorkspaceRow(item.Id.ToString("N"), "pushplus 短唤醒",
                WakeStateLabel(item.Stage), "只提示回来查看；正文待补报，手机送达未核实。",
                When(item.FinishedAt ?? item.StartedAt ?? item.CreatedAt))));
        var commitmentHistory = new AssistantCommitmentHistory(saved);
        var commitmentPage = commitmentHistory.ReadPage();
        var dependencyDetails = AssistantDependencyHistory.Read(saved);
        var pendingDependencies = saved.DependentWork!.WorkItems.Where(w => w.Stage is not (DependentWorkStage.Completed or DependentWorkStage.Cancelled))
            .Select(w => w.Id.ToString("D")).ToHashSet();
        work.AddRange(dependencyDetails.Where(d => pendingDependencies.Contains(d.Row.Id)).Select(d => d.Row));
        string context = saved.Context?.Focus is { } focus ? "当前交流目标：" + Target(focus.Task) : "尚未选定当前交流目标。";
        if (saved.Context?.PendingSelection != null) context += "\n有一份待选择的任务列表。";
        if (saved.Context?.PendingApproval != null)
            context += "\n有一条待核对的审批提示；请在微信使用完整审批编号回应。";
        string diagnostic = saved.IsReadOnly ? "持久记录已停止写入：" + SingleLine(saved.RecoveryReason ?? "需要检查", 200)
            : LastError == null ? "当前没有已登记的运行错误。" : "最近记录：" + SingleLine(LastError, 200);
        diagnostic += "\n本页按当前绑定读取；依赖工作可搜索全部记录，提醒、订阅、操作和发送记录可搜索、翻页，对话可读取更早记录。结果未知的事项不会自动重试。";
        diagnostic += "\n提醒只在助手运行时检查；电脑关机或助手退出期间不会发送。微信接口受理不代表手机已收到。";
        if (!catchUpProgress.IsEmpty)
            diagnostic += "\n历史通知：" + catchUpProgress.Count(item => item.RemainingCharacters == 0)
                + " 条正文已完整受理，" + catchUpProgress.Count(item => item.RemainingCharacters > 0)
                + " 条仍有未确认部分；部分补报不会显示为全文完成。";

        var operationDetails = CodexDashboard.OperationDetails.Read(saved, operations.Select(row => Guid.Parse(row.Id)));
        var outgoing = saved.Outbox.ToDictionary(item => item.Id.ToString("N"));
        var messageDetails = messages.Select(row => new AssistantMessageDetail(active, row.Id, row.Kind,
            row.Kind == "pushplus 短唤醒" ? "类型：" + row.Kind + "\n状态：" + row.State + "\n" + row.Target
            : "状态：" + row.State + "\n创建：" + outgoing[row.Id].CreatedAt.ToString("O")
                + "\n更新：" + outgoing[row.Id].UpdatedAt.ToString("O") + "\n发送尝试：" + outgoing[row.Id].SendAttempts
                + "\n编号：" + row.Id + "\n\n已保存正文：\n" + outgoing[row.Id].Body
                + "\n\n接口受理不等于手机送达。这里只查看记录，不会重发。" )).ToArray();
        if (!ReferenceEquals(Identity, active) || !CurrentBindingMatches(active))
            throw new InvalidOperationException("workspace_binding_changed");
        var page = AssistantConversationHistory.ReadPage(saved, MessageState, DateTimeOffset.UtcNow, sessionIdentity: active);
        if (!ReferenceEquals(Identity, active) || !CurrentBindingMatches(active))
            throw new InvalidOperationException("workspace_binding_changed");
        return new(capabilities, context, diagnostic, page.ReadAt, work, operations, messages, commitmentPage.Rows,
            page.Rows, active, page.TotalCount, commitmentPage.Details, operationDetails, page, commitmentHistory, dependencyDetails, messageDetails);
    }

    private string ConversationMessageState(ConversationSnapshot saved, ConversationIdentity active,
        IReadOnlyDictionary<Guid, CatchUpSourceProgress> sourceCoverage, OutboxMessage item)
    {
        if (item.Stage is OutboxStage.Pending or OutboxStage.Rejected
            && sourceCoverage.TryGetValue(item.Id, out var coverage) && coverage.AcceptedCharacters > 0)
        {
            string original = item.Stage == OutboxStage.Rejected ? "原发送拒绝；" : "原页尚未直接发送；";
            return original + (coverage.AcceptedCharacters == coverage.TotalCharacters
                ? "完整内容另有补报受理记录" : "部分内容已补报受理，仍有剩余");
        }
        return item.Stage switch
        {
            OutboxStage.Pending => ledger.ValidateOutputDependencies(active,
                DurableConversationLedger.ResolveOutputDependencies(saved, item)).Status switch
            {
                OutputDependencyStatus.Invalid => "使用的记忆已变化，旧内容不会发送",
                OutputDependencyStatus.Unavailable => "生成来源暂无法核对，等待处理",
                _ => "已经准备，尚未发送"
            },
            OutboxStage.Sending => "发送中，尚未取得回执",
            OutboxStage.Accepted => "渠道接口已受理，手机送达未核实",
            OutboxStage.Rejected => "渠道明确拒绝",
            OutboxStage.Unknown => "发送结果未知，不自动重发",
            _ => item.DependencyInvalidationReason != null
                ? "使用的记忆已变化，旧内容已停止发送" : "已经失效，未继续发送"
        };
    }

    internal async Task<AssistantConversationPage> ReadConversationPageAsync(ConversationIdentity expected,
        AssistantConversationCursor? before, CancellationToken ct = default)
        => await ReadConversationSnapshotAsync(expected, before, AssistantConversationHistory.Limit, ct);

    internal Task<AssistantConversationPage> ReadConversationExportAsync(ConversationIdentity expected, CancellationToken ct)
        => ReadConversationSnapshotAsync(expected, null, int.MaxValue, ct);

    internal Task<AssistantConversationPage> SearchConversationAsync(ConversationIdentity expected, string query,
        AssistantConversationCursor? before, CancellationToken ct)
        => ReadConversationSnapshotAsync(expected, before, AssistantConversationHistory.Limit, ct, query);

    private async Task<AssistantConversationPage> ReadConversationSnapshotAsync(ConversationIdentity expected,
        AssistantConversationCursor? before, int limit, CancellationToken ct, string query = "")
    {
        var active = Identity;
        // The desktop passes the identity object from its workspace read. A new
        // activation creates a new object even for A -> B -> A, so an old page
        // cannot revive when the textual account/binding identity happens to match.
        if (active == null || !ReferenceEquals(expected, active) || !CurrentBindingMatches(active)
            || before != null && !ReferenceEquals(before.Identity, active))
            throw new InvalidOperationException("workspace_binding_changed");
        var saved = await ledger.ReadConversationAsync(active, ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Identity, active) || !CurrentBindingMatches(active))
            throw new InvalidOperationException("workspace_binding_changed");
        var coverage = DurableConversationLedger.GetCatchUpProgress(saved)
            .SelectMany(item => item.Sources).ToDictionary(item => item.OutboxId);
        var page = AssistantConversationHistory.ReadPage(saved,
            item => ConversationMessageState(saved, active, coverage, item), DateTimeOffset.UtcNow, before, active, limit, query);
        if (!ReferenceEquals(Identity, active) || !CurrentBindingMatches(active))
            throw new InvalidOperationException("workspace_binding_changed");
        return page;
    }
}
