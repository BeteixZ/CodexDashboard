using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private bool RequiredDecisionStillCurrent(ConversationEvent notice)
        => notice.RequiredDecisionSuppressedAt == null && IsCurrentBinding(notice.Identity) && OwnedRuntime is { } owned
            && owned.TryValidateRequiredDecision(notice, out _, out _);

    private async Task<string> ReadCatchUpStatusAsync(ConversationIdentity active, CancellationToken ct)
    {
        if (!IsCurrentBinding(active)) throw new InvalidOperationException("catch_up_binding_changed");
        var saved = await ledger.ReadConversationAsync(active, ct);
        if (!IsCurrentBinding(active)) throw new InvalidOperationException("catch_up_binding_changed");
        var batches = saved.CatchUpBatches.IsDefault ? [] : saved.CatchUpBatches;
        var windows = saved.CatchUpWindows.IsDefault ? [] : saved.CatchUpWindows;
        var pages = saved.Outbox.Where(item => batches.Any(batch => batch.DeliveryEventId == item.NotificationEventId))
            .OrderByDescending(item => item.UpdatedAt).ToArray();
        string history = pages.Length == 0 ? "尚无已准备的补报页。"
            : string.Join("\n", pages.Take(10).Select(page =>
                page.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + CatchUpStateLabel(page.Stage)));
        var latest = windows.OrderByDescending(window => window.TrustedInputRevision).FirstOrDefault();
        if (latest is not null && latest.DeferredEventCount > 0)
            history += "\n本次还有 " + latest.DeferredEventCount + " 条历史通知尚有内容未进入补报页，原记录仍保留。";
        var coverage = DurableConversationLedger.GetCatchUpProgress(saved);
        if (!coverage.IsEmpty)
            history += "\n正文进度：\n" + string.Join("\n", coverage.TakeLast(10).Select(item =>
            {
                string target = item.Task == null ? "当前对话" : SingleLine(item.Task.HostId, 60)
                    + " / " + SingleLine(item.Task.ThreadId, 80);
                string state = item.RemainingCharacters == 0 ? "完整内容已被渠道受理"
                    : item.AcceptedCharacters > 0 ? "部分内容已受理，剩余保留" : "尚未确认正文受理";
                if (item.HasUnknown) state += "；存在未知结果，不自动重发";
                else if (item.HasSending) state += "；仍有发送正在等待回执";
                return target + " · " + state;
            }));
        return history + "\n补报保留每条原通知的内容和记录时间，不会重新执行其中的工作。"
            + "\n原发送回执与补报回执分别保存；接口受理不代表手机已经收到，未知结果不自动重发。";
    }

    private static string CatchUpStateLabel(OutboxStage stage) => stage switch
    {
        OutboxStage.Pending => "补报已准备，尚未发送",
        OutboxStage.Sending => "补报发送中，尚无确定回执",
        OutboxStage.Accepted => "补报接口已受理，手机送达未知",
        OutboxStage.Rejected => "补报被明确拒绝，内容仍保留",
        OutboxStage.Unknown => "补报结果未知，不自动重发",
        _ => "尚未开始的补报已失效，原记录仍保留"
    };
}
