using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core;

namespace CodexDashboard;

internal static class OwnedControlReply
{
    internal static ToolModelResponse? Resolve(ToolModelRequest request)
    {
        if (request.DeferredWork != null || request.CurrentMessage.Trust != MessageTrust.AuthenticatedUser
            || request.CurrentMessage.Revision != request.Revision) return null;
        var last = request.Observations.LastOrDefault();
        var simpleControl = Regex.Match(request.CurrentMessage.Body.Trim(),
            @"^(?:请)?(?<action>归档|取消归档|恢复归档|中断|停止)\s+[0-9a-fA-F-]{36}[。]?$" );
        if (simpleControl.Success && last != null)
        {
            string action = simpleControl.Groups["action"].Value;
            string tool = action switch { "归档" => "archive_owned_task", "取消归档" or "恢复归档" => "unarchive_owned_task", _ => "interrupt_turn" };
            if (last.ToolName == tool)
            {
                using var receipt = JsonDocument.Parse(last.ResultJson);
                string? status = receipt.RootElement.GetProperty("status").GetString();
                if (status == "accepted") return ToolModelResponse.Reply(action switch {
                    "归档" => "归档请求已受理。",
                    "取消归档" or "恢复归档" => "取消归档请求已受理，还没有启动工作。",
                    _ => "中断请求已受理，还需核对任务是否已经停止。" });
                if (status is "unknown" or "unknown_needs_reconciliation")
                    return ToolModelResponse.Reply("这次操作的结果还没确认，我没有重发。可发“工作列表”核对。");
            }
        }
        var reportQuery = Regex.Match(request.CurrentMessage.Body.Trim(),
            @"^(?:请)?查询任务\s+(?<id>[0-9a-fA-F-]{36})\s*的最新结果[。？?]?$" );
        bool currentReport = request.CurrentMessage.Body.Trim().TrimEnd('。', '？', '?') is "查询当前任务结果" or "查询当前任务的最新结果";
        if (currentReport && (request.ConversationContext?.Focus == null || request.ConversationContext.PendingSelection != null))
            return ToolModelResponse.Reply("请先发“任务列表”并选一个任务，再说“查询当前任务结果”。");
        string? reportId = reportQuery.Success ? reportQuery.Groups["id"].Value : currentReport ? request.ConversationContext?.Focus?.Task.ThreadId : null;
        if (reportId != null && last is { ToolName: "read_owned_report" })
        {
            using var report = JsonDocument.Parse(last.ResultJson);
            var result = report.RootElement;
            if (result.GetProperty("status").GetString() == "readonlysuccess")
            {
                var facts = result.GetProperty("facts");
                if (facts.GetProperty("task").GetProperty("ThreadId").GetString() == reportId
                    && (!currentReport || facts.GetProperty("task").GetProperty("HostId").GetString() == request.ConversationContext!.Focus!.Task.HostId
                        && facts.GetProperty("task").GetProperty("ProfileId").GetString() == request.ConversationContext.Focus.Task.ProfileId))
                {
                    var page = facts.GetProperty("page");
                    int total = page.GetProperty("totalPages").GetInt32();
                    return ToolModelResponse.Reply(OwnedReportText.Render(facts,
                        page.GetProperty("number").GetInt32(), total, page.GetProperty("snapshotId").GetString()!));
                }
            }
        }
        if (request.Observations.Length == 0 && Regex.IsMatch(request.CurrentMessage.Body.Trim(),
            @"^(?:请)?(?:继续|接续|运行|执行|查询任务|恢复控制|归档|取消归档|恢复归档)\s*(?:完整任务编号|实际编号)(?:\s|[:：]|的|$)"))
            return ToolModelResponse.Reply("这里填的是编号提示文字。请使用创建回复里的真实任务编号；如果尚未创建成功，请先完成创建。这条没有查询或执行任务。");
        bool resumeOnly = Regex.IsMatch(request.CurrentMessage.Body.Trim(), @"^(?:请)?恢复控制\s+[0-9a-fA-F-]{36}[。]?$" );
        if (resumeOnly && request.Observations.LastOrDefault() is { ToolName: "resume_owned_task" } resumed)
        {
            using var receipt = JsonDocument.Parse(resumed.ResultJson);
            if (receipt.RootElement.GetProperty("status").GetString() == "accepted")
                return ToolModelResponse.Reply("已恢复这个任务的控制，还没有启动新一轮工作。直接发送要继续做的事，并带上任务编号即可。");
        }
        var calls = request.Observations.Where(o => o.ToolName == "continue_owned_task").ToArray();
        var continuation = Regex.Match(request.CurrentMessage.Body.Trim(),
            @"^(?:请)?继续\s+(?<id>[0-9a-fA-F-]{36})\s*[:：]\s*\S");
        bool focusedContinuation = Regex.IsMatch(request.CurrentMessage.Body.Trim(), @"^(?:请)?继续当前任务\s*[:：]\s*\S");
        var focus = focusedContinuation ? request.ConversationContext?.Focus?.Task : null;
        string? requestedId = continuation.Success ? continuation.Groups["id"].Value : focus?.ThreadId;
        if (calls.Length == 0 && requestedId != null && last is { ToolName: "read_owned_task" }
            && request.Tools.Any(t => t.Name == "continue_owned_task"))
        {
            using var read = JsonDocument.Parse(last.ResultJson);
            var result = read.RootElement;
            if (result.GetProperty("status").GetString() == "readonlysuccess")
            {
                var facts = result.GetProperty("facts");
                var task = facts.GetProperty("task");
                if (facts.GetProperty("refreshed").GetBoolean()
                    && task.GetProperty("identity").GetProperty("ThreadId").GetString() == requestedId
                    && (focus == null || task.GetProperty("identity").GetProperty("HostId").GetString() == focus.HostId
                        && task.GetProperty("identity").GetProperty("ProfileId").GetString() == focus.ProfileId)
                    && task.GetProperty("access").GetString() is "CreatedThisSession" or "ResumedThisSession"
                    && task.GetProperty("state").GetString() is "Idle" or "Completed" or "Failed" or "Interrupted")
                    return ToolModelResponse.Call(new ModelToolCall(Guid.NewGuid().ToString("N"), "continue_owned_task",
                        JsonSerializer.Serialize(new { configurationId = task.GetProperty("configurationId").GetString(),
                            threadId = requestedId, revision = task.GetProperty("Revision").GetInt64(),
                            text = request.CurrentMessage.Body })));
            }
        }
        if (calls.Length != 1 || last != calls[0]) return null;
        using var document = JsonDocument.Parse(calls[0].ResultJson);
        return document.RootElement.GetProperty("status").GetString() switch
        {
            "accepted" => ToolModelResponse.Reply("已把这条要求交给任务处理。"),
            "running" => ToolModelResponse.Reply("任务正在处理这条要求。"),
            "completed" => ToolModelResponse.Reply("这一轮已完成，可以查询任务的最新结果。"),
            "unknown" or "unknown_needs_reconciliation" => ToolModelResponse.Reply("这次是否送达任务还没确认，我没有重发。可发“工作列表”核对。"),
            _ => null
        };
    }
}
