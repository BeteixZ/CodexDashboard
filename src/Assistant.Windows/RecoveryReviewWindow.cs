using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace CodexDashboard;

internal sealed class RecoveryReviewWindow : Window
{
    internal RecoveryReviewWindow(JsonElement report)
    {
        SetResourceReference(StyleProperty, "ShellWindow");
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
        Title = "恢复副本审阅"; Width = 760; Height = 640; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(20) };
        panel.SetResourceReference(Panel.BackgroundProperty, "Bg");
        var notice = new TextBlock { Text = "副本保持暂停。本页是离线检查时的快照，不会连接微信、恢复调度或重发消息。关闭后仍保持暂停。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(notice, Dock.Top); panel.Children.Add(notice);
        var close = new Button { Content = "关闭审阅", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = Describe(report), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = panel;
    }

    internal static string Describe(JsonElement report)
    {
        var text = new StringBuilder();
        text.AppendLine("检查时间（UTC）：" + report.GetProperty("inspectedAtUtc").GetString());
        var fingerprint = report.GetProperty("snapshotFingerprint");
        text.AppendLine("副本文件摘要：" + fingerprint.GetProperty("Sha256"));
        text.AppendLine("文件数：" + fingerprint.GetProperty("FileCount") + " · 总字节：" + fingerprint.GetProperty("Bytes"));
        text.AppendLine("摘要不含运行锁和空目录，仅用于区分副本内容，不是恢复许可。");
        text.AppendLine("检查范围：对话账本及记忆文档。尚未核对外部任务、凭据、规则正文和其他配置。\n");
        if (report.TryGetProperty("reconciliationRecords", out var records))
            text.AppendLine("可比对持久记录：" + records.GetArrayLength() + "。记录按身份和原编号摘要对应，不以数量相同判断内容相同。\n");
        var conversations = report.GetProperty("conversations");
        text.AppendLine("对话数：" + conversations.GetArrayLength());
        foreach (var item in conversations.EnumerateArray())
        {
            text.AppendLine("\n对话 " + item.GetProperty("ordinal") + " · 修订 " + item.GetProperty("revision"));
            text.AppendLine("收件 " + item.GetProperty("inboxCount") + " · 事件 " + item.GetProperty("eventCount") + " · 生成记录 " + item.GetProperty("generationCount"));
            States("操作", item.GetProperty("operationStates"));
            States("发送", item.GetProperty("outboxStates"));
            text.AppendLine("未发送输出：" + item.GetProperty("pendingOutbox"));
            Ids("待核对操作编号", item.GetProperty("operationsToReconcile"));
            Ids("发送不确定编号", item.GetProperty("outboxToReconcile"));
            text.AppendLine("需审阅的后台工作（原状态保留）：");
            var pending = item.GetProperty("pendingWork");
            if (pending.GetArrayLength() == 0) text.AppendLine("  无");
            foreach (var work in pending.EnumerateArray())
            {
                string kind = work.GetProperty("Kind").GetString() switch {
                    "Reminder" => "提醒", "Subscription" => "订阅", "CommitmentRun" => "提醒/订阅运行",
                    "DependentWork" => "依赖工作", "DependentDispatch" => "依赖派发",
                    "ChannelWake" => "短唤醒", "InputWork" => "排队输入", "ToolInvocation" => "工具调用",
                    "Operation" => "待派发操作", "Outbox" => "待发送消息", "Generation" => "回复生成", "NotificationDraft" => "通知草稿", _ => "其他工作" };
                string stage = work.GetProperty("Stage").GetString() switch {
                    "Paused" => "已暂停", "Unknown" => "结果未知", "Active" => "生效中", "Triggered" => "已触发",
                    "NeedsDecision" or "NeedsConfirmation" => "等待决定", "Pending" or "Queued" => "排队中",
                    "Waiting" => "等待前置条件", "Running" => "运行中", "Dispatching" => "派发中",
                    "Accepted" => "接口已受理", "Sending" => "发送中", "Reading" => "读取中",
                    "Prepared" => "已准备", "ReplyPending" or "NotificationPending" => "等待发送",
                    "PendingCheck" => "等待检查", "Checking" => "检查中", "ReadyToNotify" => "待生成通知",
                    "Executing" => "执行中", "Interrupted" => "已中断", "Processing" or "Preparing" => "处理中",
                    "Captured" => "已记录", var value => value ?? "未知状态" };
                text.AppendLine("  " + kind + " · " + stage + " · " + work.GetProperty("Id"));
                if (work.GetProperty("DueAtUtc").ValueKind != JsonValueKind.Null)
                    text.AppendLine("    时间（UTC；依赖工作为截止时间）：" + work.GetProperty("DueAtUtc"));
                if (work.GetProperty("MissedPolicy").ValueKind != JsonValueKind.Null)
                    text.AppendLine("    错过策略：" + (work.GetProperty("MissedPolicy").GetString() switch {
                        "NotifyOnce" => "补报一次", "Skip" => "跳过", "NeedsDecision" => "等待决定", _ => "未知" }));
            }
        }
        text.AppendLine("\n记忆文档数：" + report.GetProperty("memories").GetArrayLength());
        foreach (var item in report.GetProperty("memories").EnumerateArray())
        {
            text.AppendLine("\n记忆文档 " + item.GetProperty("ordinal") + " · 修订 " + item.GetProperty("revision"));
            text.AppendLine("条目 " + item.GetProperty("entryCount") + " · 撤销 " + item.GetProperty("revocationCount")
                + " · 失效 " + item.GetProperty("invalidatedCount") + " · 到期 " + item.GetProperty("expiredCount"));
            States("来源引用", item.GetProperty("eventReferenceStates"));
        }
        text.AppendLine("\n接口已受理不代表手机送达。未知结果保持未知，不能重发来验证。来源指纹相同也不证明当前仍可使用。\n正式启用还需完成旧实例交接和外部对账；本窗口不提供启动许可。");
        return text.ToString();

        void States(string label, JsonElement states)
        {
            text.AppendLine(label + "状态：");
            bool any = false;
            foreach (var entry in states.EnumerateObject())
            {
                any = true;
                string name = entry.Name switch {
                    "Unknown" => "结果未知", "Dispatching" => "派发中", "Accepted" => "接口已受理",
                    "Running" => "运行中", "Failed" => "失败", "Pending" => "待发送", "Sending" => "发送中",
                    "Succeeded" => "已成功", "Cancelled" => "已取消", "Queued" => "已排队",
                    "fingerprint_matches" => "指纹相同", "fingerprint_differs" => "指纹不同",
                    "missing_or_unavailable" => "来源缺失或不可读取", _ => entry.Name };
                text.AppendLine("  " + name + "：" + entry.Value);
            }
            if (!any) text.AppendLine("  无");
        }
        void Ids(string label, JsonElement ids)
        {
            text.AppendLine(label + "：" + (ids.GetArrayLength() == 0 ? "无" : ""));
            foreach (var id in ids.EnumerateArray()) text.AppendLine("  " + id.GetString());
        }
    }
}
