using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    internal string ControlCapabilitySummary()
    {
        string existing = settings.ChatGpt.Enabled && settings.ChatGpt.TaskControlEnabled && settings.WeChat.ClawBotEnabled
            ? "已有任务可以按当前请求接续，执行前仍核对原任务；远程纠偏和中断按独立控制范围开放，共享审批仍到 Codex 处理。"
            : "已有任务的接续开关未全部开启；状态和报告可按已配置的数据源查询。";
        var available = OwnedControlToolAdapter.Descriptors()
            .Where(descriptor => descriptor.HasSideEffects && tools.GetCapability(descriptor.Capability).Available)
            .Select(descriptor => ActionLabel(descriptor.Name)).ToArray();
        return existing + "\n" + ExistingCapabilitySummary() + "\n" + (available.Length == 0
            ? "自建任务控制尚未开放。需要有效配置、对应运行验收与控制开关，才能使用。"
            : "自建范围当前可用：" + string.Join("、", available)
                + "。每次操作仍须匹配明确目标、当前轮次和权限；重启恢复的归属仅供查询。");
    }

    private static bool IsOperationQuery(string text)
        => TryOperationPage(text, out _) || Regex.IsMatch(text, @"\A操作记录\s+[0-9A-Fa-f-]{8,36}\z", RegexOptions.CultureInvariant);

    private static bool TryOperationPage(string text, out int page)
    {
        page = 1;
        var match = Regex.Match(text, @"\A操作记录(?:\s+第(?<page>[1-9][0-9]{0,5})页)?\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        if (match.Groups["page"].Success) page = int.Parse(match.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private async Task<string> ReadOperationsFastAsync(InboxMessage message, CancellationToken ct)
    {
        var snapshot = await ledger.ReadConversationAsync(message.Identity, ct);
        string[] fields = Regex.Split(message.Body.Trim(), @"\s+", RegexOptions.CultureInvariant);
        var matches = snapshot.Operations.AsEnumerable();
        bool listing = TryOperationPage(message.Body.Trim(), out int page);
        if (!listing && fields.Length == 2)
        {
            string id = fields[1].Replace("-", "", StringComparison.Ordinal);
            matches = matches.Where(item => item.Id.ToString("N").StartsWith(id, StringComparison.OrdinalIgnoreCase));
            var found = matches.ToArray();
            if (found.Length == 0) return "当前绑定的记录中没有这个操作编号。没有查用其他用户的记录。";
            if (found.Length > 1)
                return "这个编号前缀对应多条记录，请使用其中一个完整编号：\n"
                    + string.Join("\n", found.Take(12).Select(item => item.Id.ToString("N") + " · " + ActionLabel(item.Action)));
            var item = found[0];
            string target = item.Task == null ? ""
                : "\n目标：" + SingleLine(item.Task.HostId, 140) + " / " + SingleLine(item.Task.ThreadId, 140)
                    + (item.Task.TurnId == null ? "" : "\n轮次：" + SingleLine(item.Task.TurnId, 140));
            return OperationLine(item) + "\n完整编号：" + item.Id.ToString("N") + target
                + "\n登记：" + item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                + "\n更新：" + item.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                + "\n受理、执行结束和验证通过是不同状态；结果未知时不会自动重发。";
        }
        var operations = matches.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
        if (operations.Length == 0) return "当前还没有已登记的操作。";
        int pages = (operations.Length + 11) / 12;
        if (page > pages) return "操作记录共有 " + pages + " 页，请发“操作记录 第1页”重新查看。";
        return "第 " + page + "/" + pages + " 页，共 " + operations.Length + " 条操作记录。\n"
            + string.Join("\n", operations.Skip((page - 1) * 12).Take(12).Select(item =>
                "[" + item.Id.ToString("N") + "] " + ActionLabel(item.Action) + "：" + OperationLabel(item.Stage)))
            + (page < pages ? "\n下一页：操作记录 第" + (page + 1) + "页" : "")
            + "\n发“操作记录 完整编号”查看详情。受理不代表已经运行或完成；发送结果未知的操作不会自动重发。";
    }

    private static string OperationLine(OperationRecord item)
        => "[" + item.Id.ToString("N")[..8] + "] " + ActionLabel(item.Action) + "：" + OperationLabel(item.Stage);
    private static string ActionLabel(string action) => action switch
    {
        "create_task" => "新建任务", "continue_owned_task" => "接续自建任务",
        "steer_turn" => "纠偏", "interrupt_turn" => "中断",
        "steer_existing_turn" => "已有远程任务纠偏", "interrupt_existing_turn" => "已有远程轮次中断",
        "respond_approval" => "回应审批", "rename_owned_task" => "重命名",
        "archive_owned_task" => "归档", "unarchive_owned_task" => "恢复归档",
        "send_to_task" => "接续原任务", "cancel_queued" => "取消排队",
        "remember" => "保存记忆", "update_memory" => "修改记忆", "forget" => "删除记忆",
        "learn_preference" => "保存明确偏好", "set_auto_memory" => "调整自动记忆",
        "schedule" => "安排提醒", "subscribe" => "订阅任务", "change_commitment" => "调整安排",
        "schedule_dependency" => "登记依赖工作", "change_dependency" => "调整依赖工作",
        _ => "已登记操作"
    };
    private static string OperationLabel(OperationStage stage) => stage switch
    {
        OperationStage.Queued => "已登记，尚未投递",
        OperationStage.Dispatching => "已经开始投递，尚未确认结果",
        OperationStage.Accepted => "已受理，尚未确认执行结果",
        OperationStage.Running => "已观察到正在运行",
        OperationStage.Completed => "操作已结束，是否验证通过须查看具体证据",
        OperationStage.Interrupted => "已观察到轮次中断",
        OperationStage.Failed => "操作失败",
        OperationStage.CancelledBeforeDispatch => "投递前已取消",
        _ => "结果未知，等待核对"
    };
}
