using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private readonly ExistingHostOptions? existingOptions;
    private ExistingControlRuntime? existingRuntime;
    private Task existingEvents = Task.CompletedTask;
    private readonly object existingLifecycle = new();
    private Task existingStopping = Task.CompletedTask;
    internal ExistingControlRuntime? ExistingRuntime => Volatile.Read(ref existingRuntime);
    private void ActivateExistingSession(ConversationIdentity active)
    {
        if (existingOptions == null) return;
        var runtime = new ExistingControlRuntime(active, () => IsCurrentBinding(active) ? Identity : null, ledger, existingOptions,
            () => settings.ChatGpt.TaskControlEnabled && settings.WeChat.ClawBotEnabled, commitmentClock,
            task => gateway.ReadTask(new(task.HostId, task.ThreadId, task.ProfileId)).Value);
        Volatile.Write(ref existingRuntime, runtime);
        existingEvents = Task.Run(() => ObserveExistingEventsAsync(runtime));
    }
    private Task StopExistingSessionAsync()
    {
        lock (existingLifecycle)
        {
            var runtime = Interlocked.Exchange(ref existingRuntime, null);
            return runtime == null ? existingStopping : existingStopping = StopExistingCoreAsync(runtime, existingEvents);
        }
    }
    private static async Task StopExistingCoreAsync(ExistingControlRuntime runtime, Task events)
    { await runtime.DisposeAsync(); await events; }
    private async Task ObserveExistingEventsAsync(ExistingControlRuntime runtime)
    {
        try
        {
            await foreach (var item in runtime.ReadEventsAsync(default))
            {
                var value = item.Observation;
                if (!IsCurrentBinding(runtime.Identity) || value.Task == null || value.OperationId == null) continue;
                string? body = value.Code switch
                {
                    "target_turn_observed_interrupted" => "已经观察到这项已有远程任务的指定轮次中断。",
                    "target_turn_ended_without_interruption" => "这项已有远程任务的指定轮次已结束，但没有观察到中断结果。",
                    "connection_lost_no_replay" => "连接中断了，这次操作的结果还不能确认。我不会自动重发。",
                    _ => null
                };
                if (body == null) continue;
                await coordinator.EnqueueNotificationAsync(runtime.Identity, "existing-control:" + value.OperationId + ":" + value.Code, body,
                    new(value.Task.HostId, value.Task.ThreadId, value.TurnId, value.Task.ProfileId), ConversationSource.ToolResult, default);
            }
        }
        catch (Exception) { LastError = "existing_control_notification_failed"; }
    }
    private static bool IsExistingFastCandidate(string text) => text.Trim() == "已有控制"
        || ExistingControlRuntime.IsFocusedSteer(text)
        || text.TrimStart().StartsWith("纠偏已有", StringComparison.Ordinal) || text.TrimStart().StartsWith("中断已有", StringComparison.Ordinal);
    private async Task<DurableFastReply?> HandleExistingFastAsync(DurableFastContext context, CancellationToken ct)
    {
        string text = context.Message.Body.Trim();
        bool steer = ExistingControlRuntime.IsFocusedSteer(text);
        if (steer || ExistingControlRuntime.IsFocusedStop(text))
        {
            var saved = await ledger.ReadConversationContextAsync(context.Message.Identity, ct);
            if (saved.Focus is not { } focus) return steer ? new(["还没有选定任务，请先从“任务列表”选择已有远程任务。"]): null;
            var choice = (await FocusChoicesAsync(ct)).Choices.SingleOrDefault(c => SameTask(c.Task, focus.Task));
            if (choice?.OwnedConfigurationId != null || focus.Task.HostId == "local")
                return steer ? new(["这个纠偏入口目前用于已有远程任务，当前任务不属于该范围。"]): null;
            var focusedCall = choice == null ? null : ExistingRuntime?.FocusedCommand(context.Message.Body, focus);
            return focusedCall == null
                ? new([steer ? "尚未发送纠偏。请使用“纠偏当前任务：原样指令”，并用“当前任务”或“已有控制”核对运行轮次和能力。"
                    : "当前选定的已有任务尚不能中断：需要唯一的控制配置和新鲜的运行轮次。可以发“当前任务”或“已有控制”核对。"])
                : await ExecuteControlFastAsync(context, focusedCall, ct);
        }
        if (!IsExistingFastCandidate(text)) return null;
        if (text == "已有控制") return new([ExistingCapabilitySummary()]);
        var call = ExistingControlRuntime.ParseCommand(context.Message.Body);
        if (call == null) return new(["请完整写出“中断已有 配置/任务/轮次”或“纠偏已有 配置/任务/轮次：原样指令”。"]);
        return await ExecuteControlFastAsync(context, call, ct);
    }
    private string ExistingCapabilitySummary()
    {
        var runtime = ExistingRuntime;
        string body = runtime == null || runtime.Profiles.Count == 0 ? "尚未配置已有远程任务的控制范围。"
                : string.Join("\n", runtime.Profiles.Select(p => p.Id + " · " + p.Label + " · " + p.Configuration.Connection.Endpoint.HostId
                    + " · " + p.Configuration.Directories.Single() + "\n纠偏：" + (runtime.WriteFailure(p, "turn/steer") == null ? "可用" : "未开放")
                    + "；中断：" + (runtime.WriteFailure(p, "turn/interrupt") == null ? "可用" : "未开放")
                    + "；只读核对：" + (runtime.CanReconcile(p) ? "可用" : "未开放")));
        return body + "\n用法：中断已有 配置/任务/轮次；纠偏已有 配置/任务/轮次：原样指令。用“核对操作 完整操作编号”只读核对中断结果。共享审批仍请在 Codex 处理。";
    }
}
