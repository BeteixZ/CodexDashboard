using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private static bool IsOwnedFastCandidate(string text)
        => text.StartsWith("报告续页", StringComparison.Ordinal) || text == "报告下一页"
            || Regex.IsMatch(text, @"^报告第[1-9][0-9]{0,3}页$") || IsCurrentOwnedStop(text)
            || Regex.IsMatch(text, @"^(?:中断|停止)\s+\S+$|^(?:批准|同意|批|本次会话批准|本次会话同意|拒绝|不批准|取消审批)(?:\s|$)",
            RegexOptions.CultureInvariant);
    private static bool IsCurrentOwnedStop(string text)
        => text.TrimEnd('。', '！', '!', '.', ' ') is "先停一下" or "停止运行" or "中断当前任务" or "停止当前任务";

    private async Task<DurableFastReply?> HandleOwnedFastAsync(DurableFastContext command, CancellationToken ct)
    {
        string text = command.Message.Body.Trim().TrimEnd('。', '！', '!', '.', ' ');
        if (!IsOwnedFastCandidate(text)) return null;
        var runtime = OwnedRuntime;
        var numberedPage = Regex.Match(text, @"^报告第([1-9][0-9]{0,3})页$");
        if (text is "报告下一页" or "报告续页" || numberedPage.Success)
            return new([runtime?.ReadCurrentReportPage(numberedPage.Success ? int.Parse(numberedPage.Groups[1].Value) : null)
                ?? "还没有正在阅读的报告，请先查询任务结果。"]);
        if (text.StartsWith("报告续页", StringComparison.Ordinal))
        {
            var page = Regex.Match(text, @"^报告续页 ([0-9a-f]{32}) ([1-9][0-9]{0,3})$");
            if (!page.Success) return new(["请复制报告末尾的完整续页命令。"]);
            return new([runtime?.ReadReportPage(page.Groups[1].Value, int.Parse(page.Groups[2].Value))
                ?? "这份报告的阅读记录已失效，请重新查询原任务的报告。"]);
        }
        if (runtime == null || runtime.Profiles.Count == 0)
            return new(["自建任务控制尚未配置。已有桌面任务暂时不能从这里中断或审批。"]);
        ModelToolCall call;
        if (IsCurrentOwnedStop(text) || Regex.IsMatch(text, @"^(?:中断|停止)\s+\S+$", RegexOptions.CultureInvariant))
        {
            OwnedRuntimeTask[] matches;
            if (IsCurrentOwnedStop(text))
            {
                var context = await ledger.ReadConversationContextAsync(command.Message.Identity, ct);
                if (context.Focus == null)
                    return new(["还没有选定要停止的任务。请先从“任务列表”选择，或完整写出“中断 配置编号/任务编号”。"]);
                matches = runtime.Tasks().Where(task => SameTask(context.Focus.Task,
                    new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, ProfileId: task.Handle.Identity.ProfileId))).ToArray();
            }
            else
            {
                string targetText = Regex.Split(text, @"\s+", RegexOptions.CultureInvariant)[1];
                // A recent notification, model guess or similarly named task
                // cannot select a literal-ID command's target.
                matches = runtime.Tasks().Where(task => targetText == task.Handle.Identity.ThreadId
                    || targetText == task.ConfigurationId + "/" + task.Handle.Identity.ThreadId).ToArray();
            }
            if (matches.Length != 1)
                return new(["还不能唯一确认要中断的自建任务，请完整写出“中断 配置编号/任务编号”。没有向其他任务发送停止请求。"]);
            var target = matches[0];
            if (target.Snapshot.CurrentTurnId == null
                || target.Snapshot.State is not (OwnedTaskState.Running or OwnedTaskState.WaitingApproval))
                return new(["这项任务没有已确认正在运行的轮次，尚未发送中断请求。"]);
            call = Call("interrupt_turn", new
            {
                configurationId = target.ConfigurationId, threadId = target.Handle.Identity.ThreadId,
                revision = target.Snapshot.Revision, turnId = target.Snapshot.CurrentTurnId
            });
        }
        else
        {
            string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return new(["请把决定和完整审批编号一起发来。只有“同意”或“批”无法确认对应哪次请求。"]);
            string decision = parts[0] switch
            {
                "批准" or "同意" or "批" => "accept",
                "本次会话批准" or "本次会话同意" => "acceptForSession",
                "拒绝" or "不批准" => "decline",
                _ => "cancel"
            };
            var requests = runtime.Profiles.SelectMany(profile =>
                (runtime.LoadedSession(profile.Id)?.Client.PendingApprovals() ?? [])
                    .Select(approval => new { Profile = profile.Id, Approval = approval }))
                .Where(item => OwnedControlToolAdapter.ApprovalReference(item.Profile, item.Approval) == parts[1])
                .ToArray();
            if (requests.Length != 1)
                return new(["这个审批编号已经失效或无法核对，我没有回应其他审批。"]);
            var request = requests[0];
            var target = runtime.FindTask(request.Profile, request.Approval.Task.ThreadId);
            if (target == null) return new(["审批对应的任务已无法核对，没有提交决定。"]);
            call = Call("respond_approval", new
            {
                configurationId = request.Profile, threadId = target.Handle.Identity.ThreadId,
                revision = target.Snapshot.Revision, turnId = request.Approval.TurnId,
                requestToken = request.Approval.Token, decision
            });
        }
        return await ExecuteControlFastAsync(command, call, ct);
    }

    private async Task<DurableFastReply> ExecuteControlFastAsync(DurableFastContext command,
        ModelToolCall proposed, CancellationToken ct)
    {
        var catalog = AssistantToolExecutor.Catalog(includeOwned: true);
        if (!catalog.TryGet(proposed.Name, out var descriptor)
            || !descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out var canonical, out _))
            return new(["参数无法核对，尚未执行操作。"]);
        if (!tools.GetCapability(descriptor.Capability).Available)
            return new(["这项控制能力尚未启用，或当前连接需要先恢复；没有执行操作。"]);
        var call = proposed with { ArgumentsJson = canonical };
        var permission = await tools.AuthorizeAsync(new(command.Message, call, descriptor), ct);
        if (!permission.Allowed)
            return new([permission.Reason ?? "这条请求的目标、权限或当前轮次无法核对，尚未执行。"]);
        if (!command.StillCurrent()) throw new OperationCanceledException(ct);
        string key = WeChatText.Hash(command.Message.SourceMessageId + "\n" + call.Name + "\n" + canonical);
        var saved = await ledger.ReadConversationAsync(command.Message.Identity, ct);
        var operation = saved.Operations.SingleOrDefault(item => item.IdempotencyKey == key);
        if (operation != null)
            return new(["这条请求已有操作记录，我没有再次发送。可以查询操作记录核对结果。"]);
        var lease = await ledger.BeginGenerationAsync(command.Message.Identity,
            [command.Message.SourceMessageId], command.Message.Task, ct, outputDependencies: OutputDependencies.Independent);
        ToolInvocationRecord? preparedInvocation = null;
        bool dispatchStarted = false;
        try
        {
            var generationContext = await ledger.GetGenerationContextAsync(lease, ct);
            var invocation = await ledger.RegisterToolInvocationAsync(lease, command.Message.SourceMessageId,
                call.Id, call.Name, canonical, true, ct);
            preparedInvocation = invocation;
            var grant = await ledger.AuthorizeCurrentUserRequestAsync(command.Message.Identity, command.Message.SourceMessageId,
                call.Name, permission.TargetKey!, canonical, permission.SourceExcerpt!, permission.Task, ct);
            operation = await ledger.QueueOperationAsync(command.Message.Identity, grant.Id, key, ct);
            await ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id, ct);
            dispatchStarted = true;
            ToolExecutionResult result;
            try
            {
                result = await tools.ExecuteAsync(new(command.Message.Identity, command.Message.SourceMessageId,
                    command.Message.Revision, invocation.Id, operation.Id, key, GenerationContext: generationContext), call, ct);
            }
            catch (Exception)
            {
                result = new(ToolExecutionState.Unknown,
                    JsonSerializer.SerializeToElement(new { code = "owned_fast_outcome_unknown", automaticRetry = false }));
            }
            var phase = result.State switch
            {
                ToolExecutionState.Accepted => OperationStage.Accepted,
                ToolExecutionState.Running => OperationStage.Running,
                ToolExecutionState.Completed => OperationStage.Completed,
                ToolExecutionState.Rejected => OperationStage.Failed,
                _ => OperationStage.Unknown
            };
            await ledger.FinishToolInvocationAsync(command.Message.Identity, invocation.Id,
                result.State == ToolExecutionState.Unknown ? ToolInvocationStage.Unknown : ToolInvocationStage.Completed,
                result.Facts.GetRawText(), phase, CancellationToken.None,
                new(phase, result.Facts.GetRawText(), result.TargetKey ?? operation.TargetKey, result.Task ?? operation.Task));
            // A lifecycle event may have arrived before the direct response.
            var final = (await ledger.ReadConversationAsync(command.Message.Identity, CancellationToken.None))
                .Operations.Single(item => item.Id == operation.Id);
            return final.Stage switch
            {
                OperationStage.Interrupted => new(["已经观察到这项任务的轮次中断。"]),
                OperationStage.Unknown => new(["操作结果还不能确认，我不会自动重发。可以查询操作记录核对。"]),
                OperationStage.Failed => new(["这次操作没有成功，请核对原任务和操作记录。"]),
                _ when call.Name is "interrupt_turn" or "interrupt_existing_turn" => new(["中断请求已受理，还需要等待轮次状态确认。"]),
                _ when call.Name == "steer_existing_turn" => new(["补充指令已受理；是否按要求执行，还需要核对这一轮的实际结果。"]),
                _ => new(["审批答复已提交；这不代表命令已经执行或任务已经完成。"])
            };
        }
        finally
        {
            try
            {
                if (!dispatchStarted && preparedInvocation != null)
                    await ledger.AbandonPreparedToolInvocationAsync(lease, preparedInvocation.Id, operation?.Id,
                        "owned_fast_not_started", CancellationToken.None);
            }
            finally { await ledger.EndGenerationAttemptAsync(lease, CancellationToken.None); }
        }
    }
}
