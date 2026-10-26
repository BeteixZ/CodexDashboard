using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private readonly CommitmentDetailPageCache commitmentDetailPages = new();
    internal async Task<string> ReadCommitmentDetailsAsync(ConversationIdentity identity, string query, CancellationToken ct = default)
    {
        if (!IsCurrentBinding(identity))
        { commitmentDetailPages.Clear(); throw new InvalidOperationException("commitment_details_binding_changed"); }
        if (!CommitmentDetails.TryParse(query, out var parsed))
            return "请使用“事项详情 完整编号”，例如从事项列表复制完整编号。翻页请复制当前页末尾的命令。这里只查询记录，不改变安排。";
        long epoch = commitmentDetailPages.Epoch;
        var saved = await ledger.ReadConversationAsync(identity, ct);
        if (!IsCurrentBinding(identity) || commitmentDetailPages.Epoch != epoch) throw new InvalidOperationException("commitment_details_binding_changed");
        var work = saved.WorkItems.SingleOrDefault(item => item.Id == parsed!.WorkItemId && item.Identity == identity);
        if (work == null) return "当前绑定没有这条事项，请查看“事项列表”核对完整编号。没有查询其他绑定的记录。";
        CommitmentDetailPageSnapshot? snapshot;
        if (parsed!.Page == 1 && parsed.Fingerprint == null)
        {
            var detail = CommitmentDetails.Read(saved, parsed.WorkItemId)!;
            if (!commitmentDetailPages.Store(epoch, detail, commitmentClock(), out snapshot))
                return "详情内容超过本次分页容量，或会话已变化。请在桌面“工作与记录”选择这项安排查看完整内容；没有截断保存的记录。";
        }
        else snapshot = parsed.Fingerprint == null ? null : commitmentDetailPages.Find(epoch, identity,
            work.Id, work.Version, parsed.Fingerprint, commitmentClock());
        if (!IsCurrentBinding(identity) || commitmentDetailPages.Epoch != epoch) throw new InvalidOperationException("commitment_details_binding_changed");
        string body = snapshot == null ? "这份分页已失效、安排已经变化或缺少原页校验值。请重新发送“事项详情 " + work.Id.ToString("D") + "”从第一页查看。"
            : CommitmentDetails.FormatPage(snapshot.Detail, parsed, snapshot.CapturedAt, snapshot.Token);
        return body;
    }
}
