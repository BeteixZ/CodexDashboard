using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantOperationDetail(
    ConversationIdentity Identity, Guid OperationId, string Body);

// This projection reads the existing immutable ledger snapshot only. It does not
// parse raw tool results, refresh the backend, reconcile or grant another action.
internal static class OperationDetails
{
    internal static IReadOnlyList<AssistantOperationDetail> Read(ConversationSnapshot saved,
        IEnumerable<Guid> ids)
    {
        var index = new DetailIndex(saved);
        return ids.Distinct().Select(id => Read(saved, id, index)).Where(detail => detail != null)
            .Cast<AssistantOperationDetail>().ToArray();
    }

    private sealed class DetailIndex(ConversationSnapshot saved)
    {
        internal readonly ILookup<Guid, OperationRecord> Operations = saved.Operations.ToLookup(item => item.Id);
        internal readonly ILookup<Guid, UserAuthorization> Authorizations = saved.Authorizations.ToLookup(item => item.Id);
        internal readonly ILookup<(string, long), InboxMessage> Inbox = saved.Inbox.ToLookup(item => (item.SourceMessageId, item.Revision));
        internal readonly ILookup<Guid?, ToolInvocationRecord> Invocations =
            (saved.ToolInvocations.IsDefault ? [] : saved.ToolInvocations).ToLookup(item => item.OperationId);
    }

    internal static AssistantOperationDetail? Read(ConversationSnapshot saved, Guid id)
        => Read(saved, id, null);

    private static AssistantOperationDetail? Read(ConversationSnapshot saved, Guid id, DetailIndex? index)
    {
        var operation = (index?.Operations[id] ?? saved.Operations).SingleOrDefault(item => item.Id == id && item.Identity == saved.Identity);
        if (operation == null) return null;
        var text = new StringBuilder();
        text.AppendLine(Action(operation.Action)).AppendLine("当前保存的状态：" + State(operation.Stage));
        text.AppendLine("登记时间：" + At(operation.CreatedAt)).AppendLine("记录更新：" + At(operation.UpdatedAt));
        if (operation.Task is { } task)
        {
            text.AppendLine().AppendLine("主机：" + task.HostId).AppendLine("任务：" + task.ThreadId);
            if (task.TurnId != null) text.AppendLine("轮次：" + task.TurnId);
            if (task.ProfileId != null) text.AppendLine("配置范围：" + task.ProfileId);
        }
        text.AppendLine().AppendLine("触发这项操作的原始消息：");
        var authorization = (index?.Authorizations[operation.AuthorizationId] ?? saved.Authorizations).SingleOrDefault(item => item.Id == operation.AuthorizationId
            && item.Identity == saved.Identity && item.SourceMessageId == operation.SourceMessageId
            && item.Revision == operation.Revision && item.Action == operation.Action
            && item.TargetKey == operation.TargetKey && item.ArgumentsJson == operation.ArgumentsJson
            && item.Task == operation.Task);
        var input = authorization == null ? null : (index?.Inbox[(operation.SourceMessageId, operation.Revision)] ?? saved.Inbox).SingleOrDefault(item => item.Identity == saved.Identity
            && item.SourceMessageId == operation.SourceMessageId && item.Revision == operation.Revision
            && item.Trust == MessageTrust.AuthenticatedUser
            && !string.IsNullOrEmpty(authorization.SourceExcerpt)
            && item.Body.Contains(authorization.SourceExcerpt, StringComparison.Ordinal));
        if (input == null)
            text.AppendLine("这条操作的原始消息关联暂无法完整核对，未用其他消息代替。");
        else
            text.AppendLine(input.Body).AppendLine().AppendLine("原消息接收于 " + At(input.ReceivedAt))
                .AppendLine("这里展示历史原话，不表示它仍然适用，也不会再次执行。");

        var invocations = saved.ToolInvocations.IsDefault ? [] : (index?.Invocations[operation.Id] ?? saved.ToolInvocations).Where(item =>
            item.OperationId == operation.Id && item.Identity == saved.Identity
            && item.SourceMessageId == operation.SourceMessageId && item.Revision == operation.Revision
            && item.ToolName == operation.Action && item.ArgumentsJson == operation.ArgumentsJson).ToArray();
        if (invocations.Length > 0)
        {
            text.AppendLine().AppendLine("关联的工具处理记录：");
            foreach (var invocation in invocations.OrderBy(item => item.CreatedAt))
                text.AppendLine(ToolState(invocation.Stage) + "；登记于 " + At(invocation.CreatedAt)
                    + "，更新于 " + At(invocation.UpdatedAt) + "。");
            text.AppendLine("工具步骤结束与 Codex 任务结束分别记录，不能互相代替。");
        }
        text.AppendLine().Append(StageGuidance(operation.Stage));
        text.AppendLine().Append("此页只查看保存的记录，不会重新投递、批准请求或改变任务。受理、运行、结束和验证通过分别判断。");
        return new(saved.Identity, operation.Id, text.ToString());
    }

    private static string Action(string action) => action switch
    {
        "create_task" => "新建任务", "continue_owned_task" => "接续自建任务",
        "steer_turn" => "补充当前轮次", "interrupt_turn" => "请求中断",
        "steer_existing_turn" => "补充已有远程任务", "interrupt_existing_turn" => "中断已有远程轮次",
        "respond_approval" => "回应审批", "rename_owned_task" => "重命名任务",
        "archive_owned_task" => "归档任务", "unarchive_owned_task" => "恢复归档任务",
        "send_to_task" => "接续原任务", "cancel_queued" => "取消待投递指令",
        "remember" => "保存记忆", "update_memory" => "修改记忆", "forget" => "删除记忆",
        "learn_preference" => "保存明确偏好", "set_auto_memory" => "调整自动记忆",
        "schedule" => "安排提醒", "subscribe" => "订阅任务", "change_commitment" => "调整安排",
        "schedule_dependency" => "登记依赖工作", "change_dependency" => "调整依赖工作",
        _ => "已登记的操作"
    };
    private static string State(OperationStage stage) => stage switch
    {
        OperationStage.Queued => "已登记，尚未投递",
        OperationStage.Dispatching => "已经开始投递，尚未确认结果",
        OperationStage.Accepted => "接口已受理，执行结果仍需核对",
        OperationStage.Running => "已观察到正在运行",
        OperationStage.Completed => "操作已结束，验证结果须另核对",
        OperationStage.Failed => "操作失败",
        OperationStage.CancelledBeforeDispatch => "投递前已取消",
        OperationStage.Interrupted => "已观察到对应轮次中断",
        _ => "结果未知，等待核对"
    };
    private static string ToolState(ToolInvocationStage stage) => stage switch
    {
        ToolInvocationStage.Prepared => "工具步骤已准备", ToolInvocationStage.Executing => "工具步骤已开始",
        ToolInvocationStage.Completed => "工具步骤已结束", ToolInvocationStage.Failed => "工具步骤失败",
        ToolInvocationStage.Unknown => "工具步骤结果未知", ToolInvocationStage.NeedsConfirmation => "需要重新明确请求",
        ToolInvocationStage.Unavailable => "能力或原请求已不可用", ToolInvocationStage.InvalidArguments => "参数未通过核对",
        _ => "工具步骤已停止，实际任务状态仍须另核对"
    };
    private static string StageGuidance(OperationStage stage) => stage switch
    {
        OperationStage.Queued => "如需取消尚未投递的原任务指令，请先查看“工作列表”核对对应排队记录。",
        OperationStage.Dispatching or OperationStage.Unknown => "当前不能确定这项操作是否生效，不会自动重发。需要结合原任务的确切记录核对。",
        OperationStage.Accepted => "接口受理只说明请求已被接收，尚不能据此认定已经运行、完成或测试通过。",
        OperationStage.Running => "这是已保存的运行观察；原任务之后的变化可能尚未反映在这里。",
        OperationStage.Interrupted => "这里只确认对应轮次的中断观察，不代表所有派生进程都已结束。",
        OperationStage.Completed => "操作结束不等于整个项目完成；如需判断成果质量，请查看对应轮次报告和验证证据。",
        OperationStage.CancelledBeforeDispatch => "这项记录在投递前取消，不能据此推断其他指令或正在运行的任务也已停止。",
        _ => "失败记录不会自动授权重试；如需下一步操作，请核对目标后重新明确提出。"
    };
    private static string At(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
}
