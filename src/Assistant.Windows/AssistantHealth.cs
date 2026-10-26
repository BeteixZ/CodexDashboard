using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Protocol;

namespace CodexDashboard;

internal sealed record AssistantChannelHealth(ConnectionState State, bool ReceiverRunning,
    bool SenderRunning, bool ReceiverLeaseHeld, bool Stopping);

internal sealed partial class WeChatService
{
    // Only state already held by this instance. Do not load a binding, examine
    // credentials, probe an endpoint, or serialize the anonymous Diagnostics.
    internal AssistantChannelHealth ReadHealth() => new(ConnectionState,
        receiver is { IsCompleted: false }, sender is { IsCompleted: false },
        receiverLease != null, stopRequested || HasPendingStop);
}

internal enum AssistantLedgerHealthState { Inactive, Available, ReadOnly }
internal sealed record AssistantLedgerHealth(ConversationIdentity? Identity,
    AssistantLedgerHealthState State, int WaitingInputs = 0, int GeneratingInputs = 0,
    int PendingMessages = 0, int SendingMessages = 0, int UnknownMessages = 0,
    int AcceptedOperations = 0, int RunningOperations = 0, int UnknownOperations = 0,
    bool HasRuntimeError = false, int InputDecisions = 0, LedgerCapacitySnapshot? Capacity = null);

internal sealed partial class DurableAssistantRuntime
{
    internal bool IsCurrentHealthIdentity(ConversationIdentity? expected) => expected == null
        ? Identity == null : ReferenceEquals(expected, Identity) && IsCurrentBinding(expected);

    internal async Task<AssistantLedgerHealth> ReadHealthAsync(CancellationToken ct = default)
    {
        var active = Identity;
        if (active == null) return new(null, AssistantLedgerHealthState.Inactive, HasRuntimeError: LastError != null);
        if (!IsCurrentHealthIdentity(active)) throw new InvalidOperationException("health_binding_changed");
        var diagnostic = await ledger.ReadConversationDiagnosticsAsync(active, ct);
        var saved = diagnostic.Conversation;
        if (!IsCurrentHealthIdentity(active)) throw new InvalidOperationException("health_binding_changed");
        return new(active, saved.IsReadOnly ? AssistantLedgerHealthState.ReadOnly : AssistantLedgerHealthState.Available,
            saved.InputWork.Count(x => x.DialogueEpoch == saved.Context?.Epoch && x.Stage == InputWorkStage.Queued),
            saved.Generations.Count(x => x.Stage == GenerationStage.Processing),
            saved.Outbox.Count(x => x.Stage == OutboxStage.Pending),
            saved.Outbox.Count(x => x.Stage == OutboxStage.Sending),
            saved.Outbox.Count(x => x.Stage == OutboxStage.Unknown),
            saved.Operations.Count(x => x.Stage == OperationStage.Accepted),
            saved.Operations.Count(x => x.Stage == OperationStage.Running),
            saved.Operations.Count(x => x.Stage == OperationStage.Unknown),
            LastError != null,
            saved.InputWork.Count(x => x.DialogueEpoch == saved.Context?.Epoch && x.Stage == InputWorkStage.NeedsConfirmation),
            diagnostic.Capacity);
    }
}

internal sealed record AssistantHealthSnapshot(DateTimeOffset ReadAt, AssistantChannelHealth? Channel,
    bool ModelInitialized, bool ModelEnabled, int CompletedRequests, DateTimeOffset? LastModelCompletedAt,
    DateTimeOffset? LastModelFailureAt, int ConfiguredSources, int ObservedSources, int FailedSources,
    int ObservedTasks, DateTimeOffset? LastCollectionAt, bool StatusServiceStarted,
    AssistantLedgerHealth? Ledger, string DataDirectory, WorkspaceConcurrencySnapshot? Concurrency = null)
{
    internal ConversationIdentity? Identity => Ledger?.Identity;
    internal string ConnectionSummary =>
        "微信：" + (Channel == null ? "尚未启动" : Channel.State switch
        {
            ConnectionState.NotConfigured => "尚未启用或配置",
            ConnectionState.Disconnected => "当前未连接",
            ConnectionState.Connecting => "正在连接",
            ConnectionState.Connected => "连接已建立",
            ConnectionState.Degraded => "连接需要检查",
            _ => "状态尚未确认"
        }) + (Channel?.Stopping == true ? "，正在停止" : "")
        + "\n模型：" + (!ModelInitialized ? "尚未就绪" : !ModelEnabled ? "问答已关闭" : "问答已启用")
        + "。启用状态不代表已经验证当前请求可用。"
        + "\n本次启动已完成 " + CompletedRequests + " 次模型请求；最近完成："
        + When(LastModelCompletedAt) + "。"
        + (LastModelFailureAt.HasValue ? "\n本次启动最近一次模型错误：" + When(LastModelFailureAt) + "，请结合连接设置核对。" : "");

    internal string RecordsSummary => Ledger == null ? "持久记录尚未就绪。收件和任务控制不能据此视为可用。"
        : Ledger.State == AssistantLedgerHealthState.Inactive ? "当前微信绑定尚未激活，没有载入其他绑定的记录。"
        : (Ledger.State == AssistantLedgerHealthState.ReadOnly
            ? "记录已进入保护性只读状态，请保留原文件并检查恢复说明。" : "当前绑定的持久记录可读取。")
            + "\n以前的输入：" + Ledger.WaitingInputs + " 条待补处理，" + Ledger.InputDecisions + " 条需要重新明确要求。"
            + "\n正在整理的回复记录 " + Ledger.GeneratingInputs + " 条。"
            + "\n发送中 " + Ledger.SendingMessages
            + " 条，发送结果未知 " + Ledger.UnknownMessages + " 条。"
            + "\n操作已受理 " + Ledger.AcceptedOperations + " 项，已观察到运行 " + Ledger.RunningOperations
            + " 项，执行结果待核对 " + Ledger.UnknownOperations + " 项。"
            + (Ledger.HasRuntimeError ? "\n运行期间有错误记录，需要结合连接设置和工作记录检查。" : "")
            + CapacitySummary
            + "\n结果未知的操作和消息不会因刷新本页重新执行或发送。";

    internal string CapacitySummary => Ledger?.Capacity is not { } size ? ""
        : "\n当前可读取记录的逻辑大小：" + Bytes(size.LogicalSnapshotBytes) + " / " + Bytes(size.MaximumSnapshotBytes)
            + "（整份账本，包含其他绑定）。"
            + (size.MaximumStoredSnapshotBytes is { } maximum ? "\n已开启历史归档；当前引用清单："
                + (size.StoredSnapshotBytes is { } stored ? Bytes(stored) : "尚未提交") + " / " + Bytes(maximum)
                + "。备份需同时保留账本和 history-archive 目录。" : "")
            + (size.ArchiveStorage is { } archive ? "\n归档目录文件总长度：" + Bytes(archive.FileBytes)
                + "，共 " + archive.FileCount + " 个文件；已登记卷 " + archive.RegisteredVolumes + " / " + archive.MaximumVolumes
                + "。包含目录索引及遗留文件，不含主账本、子目录或独立备份；不是文件系统分配空间。" : "")
            + (size.NearLimit ? "\n记录接近容量上限；后续收件和状态保存可能失败。请保留完整数据，勿删文件腾空间。"
                : "")
            + (size.IsReadOnly ? "\n该数字仅对应已恢复的可读快照；尚不能确认磁盘中的最后一次写入。" : "")
            + "\n逻辑大小不是磁盘占用；压缩不删除历史，剩余空间不保证下一次操作可保存。";

    private static string Bytes(long bytes) => bytes < 1048576
        ? (bytes / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KiB"
        : (bytes / 1048576d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MiB";

    internal string SourcesSummary => "已配置 " + ConfiguredSources + " 个数据源，最近快照包含 "
        + ObservedSources + " 个来源、" + ObservedTasks + " 个任务。"
        + "\n最近采集：" + When(LastCollectionAt) + "；其中 " + FailedSources + " 个来源记录了读取异常。"
        + "\n这里显示最近保存的快照，刷新本页不会重新连接主机；数据时间不会改写成当前时间。";

    internal string ConcurrencySummary => Concurrency == null ? "任务占用协调尚未就绪。"
        : "本实例的任务配置共用额度：" + (Concurrency.Limit.HasValue ? "最多 " + Concurrency.Limit + " 项" : "未设置数量上限") + "。"
            + "\n已登记占用 " + Concurrency.Occupied + " 项，其中已预留 " + Concurrency.Reserved
            + " 项、结果待核对 " + Concurrency.Unknown + " 项；占用不等于正在运行。"
            + (Concurrency.RecoveryReady ? "\n占用记录恢复已完成；新工作仍需通过任务权限与目录检查。"
                : "\n占用记录尚未恢复完整，新写操作暂不开放。");

    internal string Details => "读取时间：" + When(ReadAt)
        + "\n助手数据目录：" + DataDirectory
        + "\n微信接收循环：" + Yes(Channel?.ReceiverRunning)
        + "\n通知排队循环：" + Yes(Channel?.SenderRunning)
        + "\n本实例持有接收租约：" + Yes(Channel?.ReceiverLeaseHeld)
        + "\n本机状态服务：" + (StatusServiceStarted ? "已启动，本页未验证端到端连接" : "尚未就绪")
        + (Ledger?.State is AssistantLedgerHealthState.Available or AssistantLedgerHealthState.ReadOnly
            ? "\n原始 Pending 状态记录：" + Ledger.PendingMessages
                + "（包含历史输出或补报来源，不等于当前可发送条数）" : "")
        + "\n\n接收租约只说明本实例已取得本机接收权。连接、接口受理和手机实际收信分别核对。"
        + "\n本页只显示运行状态与计数，不包含聊天正文、任务指令或凭据。";

    private static string When(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "尚无记录";
    private static string Yes(bool? value) => value == null ? "尚未就绪" : value.Value ? "是" : "否";
}
