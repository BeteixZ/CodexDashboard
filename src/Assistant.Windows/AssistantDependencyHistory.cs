using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantDependencyDetail(ConversationIdentity Identity, AssistantWorkspaceRow Row,
    string Body, string Commands);

// Rows, original authorization and dispatch details all come from one ledger read.
internal static class AssistantDependencyHistory
{
    internal static IReadOnlyList<AssistantDependencyDetail> Read(ConversationSnapshot snapshot)
    {
        if (snapshot.DependentWork is not { } saved) return [];
        var edits = snapshot.Operations.Where(op => op.Action == "change_dependency" && op.Stage == OperationStage.Completed).Select(op =>
        {
            using var json = JsonDocument.Parse(op.ArgumentsJson);
            return (Id: json.RootElement.GetProperty("workItemId").GetGuid(), Version: json.RootElement.GetProperty("expectedVersion").GetInt64(), op.SourceMessageId);
        }).ToDictionary(edit => (edit.Id, edit.Version), edit => edit.SourceMessageId);
        return saved.WorkItems.OrderByDescending(w => w.CreatedAt).ThenBy(w => w.Id).Select(work =>
        {
            var body = new StringBuilder();
            body.AppendLine($"依赖工作：{work.Id:D} · 第 {work.Version} 版");
            body.AppendLine("当前记录：" + DependentWorkToolAdapter.Stage(work.Stage));
            body.AppendLine("等待截止：" + When(work.DeadlineUtc));
            body.AppendLine(DependentWorkToolAdapter.NotificationSummary(work.NotificationPolicy));
            body.AppendLine((work.ContinueAfterWorkId == null ? "目标任务：" : "登记时的任务：") + Target(work.Target));
            if (work.ContinueAfterWorkId is { } continuation)
                body.AppendLine($"接续依赖 {continuation:D} 实际完成的轮次；不跟随其他新轮次。");
            body.AppendLine("\n条件满足后发送的完整指令：\n" + work.Instructions);
            body.AppendLine("\n需要全部满足的前置条件：");
            foreach (var condition in work.Requirements)
            {
                if (condition.WorkItemId is { } id)
                {
                    var parent = saved.WorkItems.Single(w => w.Id == id);
                    body.AppendLine($"• 依赖 {id:D} 第 {condition.WorkVersion} 版完成");
                    body.AppendLine($"  当前第 {parent.Version} 版 · {DependentWorkToolAdapter.Stage(parent.Stage)}"
                        + (parent.Version != condition.WorkVersion ? "；版本已变化，需要重新安排。" : ""));
                }
                else body.AppendLine("• 任务轮次完成：" + Target(condition.Task!));
                if (condition.CommandSuccess is { } command)
                    body.AppendLine($"  指定测试命令：{command.Command}\n  执行目录：{command.WorkingDirectory}\n  最后一次实际执行须结束且退出码为 0；报告文字不能代替。");
            }
            if (work.Reason != null) body.AppendLine("\n最近核对：" + (work.Reason switch
            {
                "deadline_passed" => "等待期限已过。",
                "predecessor_changed_or_failed" => "前置工作版本变化、失败、取消、到期或需要重新决定。",
                "task_or_configuration_changed" => "任务轮次或连接配置已经变化。",
                "predecessor_failed" => "前置任务失败或被中断。",
                "predecessor_command_failed" => "指定测试命令的最后一次执行未成功。",
                "dispatch_budget_exhausted" => "已达到派发尝试上限。",
                "known_not_dispatched" => "上次明确未发送，仍需满足条件后才能再次派发。",
                _ => "请核对当前前置条件和派发记录。"
            }));
            body.AppendLine("\n实际派发记录：");
            var attempts = saved.Attempts.Where(a => a.WorkItemId == work.Id).OrderBy(a => a.StartedAt).ToArray();
            if (attempts.Length == 0) body.AppendLine("尚无派发尝试；登记不表示已发送。");
            foreach (var attempt in attempts)
            {
                body.AppendLine($"{When(attempt.StartedAt)} · 第 {attempt.WorkVersion} 版 · " + (attempt.Stage switch
                {
                    DependentDispatchStage.Dispatching => "正在派发，尚无结果",
                    DependentDispatchStage.Accepted => "已受理，需单独核对完成",
                    DependentDispatchStage.NotDispatched => "明确未发送",
                    DependentDispatchStage.Rejected => "请求被拒绝",
                    _ => "发送结果未知，不会重发"
                }));
                body.AppendLine("尝试编号：" + attempt.Id.ToString("D"));
                if (work.ContinueAfterWorkId != null) body.AppendLine("本次接续起点：" + Target(attempt.Evidence[0].Observation.Task));
                if (attempt.ResultTask != null) body.AppendLine("实际任务：" + Target(attempt.ResultTask));
                foreach (var proof in attempt.Evidence.Skip(1).Where(e => e.Commands != null))
                {
                    body.AppendLine("核对的命令记录（" + Target(proof.Observation.Task) + "）：");
                    foreach (var command in proof.Commands!.Value)
                        body.AppendLine($"  {command.ItemId} · {command.Command}\n  {command.WorkingDirectory} · {command.State} · 退出码 {command.ExitCode?.ToString() ?? "未知"}");
                }
            }
            if (work.Completion is { } complete)
                body.AppendLine("\n实际轮次终态：" + Observation(complete));
            else if (work.LastResultObservation is { } latest)
                body.AppendLine("\n最近轮次观察：" + Observation(latest) + "；尚未核实完成。");
            else if (work.ResultTask != null) body.AppendLine("\n尚未核实实际轮次的终态。");
            if (work.Stage is DependentWorkStage.Paused or DependentWorkStage.Cancelled)
                body.AppendLine("暂停或取消安排不会中断已开始的任务；派发及终态记录仍单独保留。");
            body.AppendLine("轮次完成不表示测试通过。记录只反映读取时已保存的证据，请刷新查看后续变化。");
            body.AppendLine("\n登记时的原始请求：\n" + snapshot.Inbox.Single(i => i.SourceMessageId == work.SourceMessageId).Body);
            if (work.Version > 1)
            {
                string source = edits[(work.Id, work.Version - 1)];
                body.AppendLine("\n最近一次调整请求：\n" + snapshot.Inbox.Single(i => i.SourceMessageId == source).Body);
            }
            body.AppendLine("\n登记于 " + When(work.CreatedAt) + "\n更新于 " + When(work.UpdatedAt));
            string predecessor = work.ContinueAfterWorkId is { } anchor ? "依赖 " + anchor.ToString("D") : "任务 完整任务编号";
            string workPredecessor = work.ContinueAfterWorkId?.ToString("D") ?? "完整工作编号";
            string commands = $"暂停依赖 {work.Id:D}\n恢复依赖 {work.Id:D}\n取消依赖 {work.Id:D}\n修改依赖 {work.Id:D}：新的完整指令\n"
                + $"修改依赖 {work.Id:D} 截止：{work.DeadlineUtc.ToLocalTime():yyyy-MM-dd HH:mm zzz}\n"
                + $"修改依赖 {work.Id:D} 前置：{predecessor}\n修改依赖 {work.Id:D} 前置：依赖 {workPredecessor}；截止：带时区的未来时间\n"
                + $"等依赖 {work.Id:D} 完成后，向 目标任务完整编号 发送：后续完整指令";
            commands += $"\n等依赖 {work.Id:D} 完成后，在同一任务继续：后续完整指令";
            commands += $"\n等依赖 {work.Id:D} 测试命令「原样命令」成功后，向 目标任务完整编号 发送：后续完整指令"
                + $"\n等依赖 {work.Id:D} 测试命令「原样命令」成功后，在同一任务继续：后续完整指令"
                + $"\n修改依赖 {work.Id:D} 测试命令：「原样命令」\n修改依赖 {work.Id:D} 测试命令：无";
            commands += $"\n修改依赖 {work.Id:D} 安静时段：22:00-08:00\n修改依赖 {work.Id:D} 安静时段：无";
            return new AssistantDependencyDetail(snapshot.Identity,
                new(work.Id.ToString("D"), "依赖工作", DependentWorkToolAdapter.Stage(work.Stage), work.Instructions,
                    work.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss")), body.ToString().TrimEnd(), commands);
        }).ToArray();
    }
    private static string When(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    private static string Target(TaskReference task) => $"{task.HostId} / {task.ThreadId} / {task.TurnId}";
    private static string Observation(CommitmentTaskObservation value) => (value.State switch
    { CommitmentTaskState.Completed => "已完成", CommitmentTaskState.Failed => "失败", CommitmentTaskState.Interrupted => "已中断",
        CommitmentTaskState.Running => "仍在运行", _ => "未知" }) + " · " + When(value.ObservedAt) + "\n" + Target(value.Task);
}
