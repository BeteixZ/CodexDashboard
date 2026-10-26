using System;
using System.Linq;
using Codex.Protocol;

namespace CodexDashboard;

// Describes implemented entry points and the current local connection state.
// This view never probes a service, opens credentials or claims phone delivery.
internal static class AssistantChannelCapabilities
{
    internal static string Describe(bool wechatEnabled, bool pushPlusEnabled, bool modelEnabled, ConnectionState state, AssistantMediaFeatures? media = null)
    {
        media ??= AssistantMediaFeatures.Capture(new());
        string connection = !wechatEnabled ? "尚未开启" : state switch
        {
            ConnectionState.NotConfigured => "尚未配置",
            ConnectionState.Connecting => "正在连接",
            ConnectionState.Connected => "当前连接正常",
            ConnectionState.Degraded => "连接需要恢复",
            ConnectionState.Disconnected => "当前未连接",
            _ => "连接状态尚未确认"
        };
        string experiments = string.Join("、", new[] {
            media.Attachments ? "附件读取" : null,
            media.Files ? "文件发送" : null,
            media.Images && media.Attachments ? "图片分析" : null,
            media.Typing ? "输入状态" : null
        }.Where(value => value != null));
        return "微信：" + connection + "。\n"
            + "模型问答：" + (modelEnabled ? "已开启，能否回答还取决于当前授权和额度。" : "尚未开启。") + "\n"
            + "不用开启模型，也能查状态、工作记录、提醒规则和记忆来源。\n"
            + "语音：能读微信附带的转写；没有转写时请发文字。语音回复：当前尚未开放。\n"
            + "引用：能找回本版本记录过的助手回复并用于问答；找不到会请你补充原文。已关联任务的通知可引用后发“这个继续”，需模型和任务接续已开启；其他操作请单独发文字，写清目标。\n"
            + (media.Attachments ? "附件：支持UTF-8文本、Word正文和PDF文字层。" : "附件读取未开启。")
            + (media.Images && media.Attachments ? "图片分析支持PNG/JPEG，需开启模型。" : "图片分析未开启。") + "\n"
            + (media.Files ? "文件发送：先在电脑端准备文件，微信发“待发送文件”，再按最近列表序号发“发送文件 1”。" : "文件发送未开启。")
            + "发“文件帮助”可看用法。\n"
            + (media.Typing ? "回答时会尝试显示输入状态。" : "输入状态未开启。")
            + (experiments.Length > 0 ? "上述" + experiments + "为试验功能，尚未完成当前账号和手机验证。" : "") + "\n"
            + "提醒在助手运行时检查；暂时发不出会保留待补报记录，结果未知时不重发。\n"
            + (pushPlusEnabled ? "pushplus短唤醒已开启，需有效令牌和绑定；仅在正文待补报时尝试短提示。" : "pushplus短唤醒未开启。")
            + "连接正常或接口受理，都不代表手机已收到。";
    }
}
