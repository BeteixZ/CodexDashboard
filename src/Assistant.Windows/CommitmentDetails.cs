using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record CommitmentDetail(ConversationIdentity Identity, Guid WorkItemId, long WorkVersion, string Body);
internal sealed record CommitmentDetailQuery(Guid WorkItemId, int Page, string? Fingerprint);

// This is a projection of one already authenticated, immutable ledger snapshot.
// It neither checks remote tasks nor exposes any decision token, report body or raw receipt.
internal static class CommitmentDetails
{
    internal const int PageCharacters = 680;
    private static readonly Regex Query = new(@"\A事项详情\s+(?<id>[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})(?:\s+(?<page>[1-9][0-9]{0,5})(?:\s+(?<hash>[0-9a-fA-F]{16}))?)?\z",
        RegexOptions.CultureInvariant);
    internal static bool IsCandidate(string text) => text.Trim().StartsWith("事项详情", StringComparison.Ordinal);
    internal static bool TryParse(string text, out CommitmentDetailQuery? query)
    {
        query = null; var match = Query.Match(text.Trim());
        if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var id) || id == Guid.Empty) return false;
        int page = match.Groups["page"].Success ? int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture) : 1;
        query = new(id, page, match.Groups["hash"].Success ? match.Groups["hash"].Value.ToUpperInvariant() : null);
        return true;
    }
    internal static IReadOnlyList<CommitmentDetail> Read(ConversationSnapshot saved, IEnumerable<Guid> ids)
    {
        var coverage = Coverage(saved);
        return ids.Distinct().Select(id => Read(saved, id, coverage)).Where(x => x != null).Cast<CommitmentDetail>().ToArray();
    }
    internal static CommitmentDetail? Read(ConversationSnapshot saved, Guid id)
        => Read(saved, id, Coverage(saved));
    private static IReadOnlyDictionary<Guid, CatchUpEventProgress> Coverage(ConversationSnapshot saved)
        => saved.CatchUpWindows.IsDefaultOrEmpty ? new Dictionary<Guid, CatchUpEventProgress>()
            : DurableConversationLedger.GetCatchUpProgress(saved).ToDictionary(x => x.SourceEventId);

    private static CommitmentDetail? Read(ConversationSnapshot saved, Guid id, IReadOnlyDictionary<Guid, CatchUpEventProgress> coverage)
    {
        var work = Items(saved.WorkItems).SingleOrDefault(x => x.Id == id && x.Identity == saved.Identity);
        if (work == null) return null;
        var schedule = Items(saved.Schedules).SingleOrDefault(x => x.WorkItemId == id);
        var subscription = Items(saved.Subscriptions).SingleOrDefault(x => x.WorkItemId == id);
        var runs = Items(saved.CommitmentRuns).Where(x => x.WorkItemId == id && x.Identity == saved.Identity)
            .OrderByDescending(x => x.TriggeredAt).ThenBy(x => x.Id).ToArray();
        var text = new StringBuilder();
        text.AppendLine(work.Kind == TrackedWorkKind.Reminder ? "一次性提醒" : "任务订阅");
        text.AppendLine("当前记录：" + WorkStage(work));
        text.AppendLine().AppendLine("保存的完整内容：").Append(work.Goal).AppendLine().AppendLine();
        text.AppendLine("创建时间：" + At(work.CreatedAt)).AppendLine("记录更新：" + At(work.UpdatedAt));
        if (work.DeadlineUtc is { } deadline) text.AppendLine("截止时间：" + At(deadline));
        if (schedule != null)
        {
            text.AppendLine("原定当地时间：" + schedule.LocalTime.Replace('T', ' ') + "（" + schedule.TimeZoneId + "）");
            text.AppendLine("对应时间：" + At(schedule.DueAtUtc));
            text.AppendLine("解析时间基准：" + At(schedule.ReferenceTimeUtc));
            text.AppendLine("错过时：" + (schedule.MissedPolicy switch
            { MissedTriggerPolicy.NotifyOnce => "恢复后提醒一次", MissedTriggerPolicy.Skip => "跳过原安排", _ => "需要你重新决定" }));
        }
        if (work.Task is { } task)
        {
            text.AppendLine("目标主机：" + task.HostId).AppendLine("任务编号：" + task.ThreadId);
            if (task.ProfileId != null) text.AppendLine("配置范围：" + task.ProfileId);
            text.AppendLine("精确轮次：" + (task.TurnId ?? "未记录"));
        }
        if (subscription != null)
        {
            text.AppendLine("订阅条件：" + (subscription.Condition switch
            {
                SubscriptionCondition.Completed => "这一轮结束时通知", SubscriptionCondition.Failed => "这一轮失败或中断时通知",
                SubscriptionCondition.NeedsDecision => "这一轮需要决定时通知",
                _ => "持续 " + subscription.InactivitySeconds + " 秒未读到新活动后做只读检查"
            }));
            text.AppendLine("最后保存的任务状态：" + TaskState(subscription.LastState));
            text.AppendLine("最后有效观察：" + (subscription.LastObservedAt is { } observed ? At(observed) : "尚无有效观察记录"));
            if (subscription.Condition == SubscriptionCondition.NoActivity)
            {
                text.AppendLine("活动判断基准：" + At(subscription.LastActivityAt));
                text.AppendLine("已存检查时间：" + At(subscription.NextCheckAt) + "（仍须满足当前状态和预算）");
            }
        }
        var policy = work.Policy;
        text.AppendLine("安静时段：" + (policy.QuietStart == null ? "此事项未设置"
            : policy.QuietStart + "–" + policy.QuietEnd + "（" + policy.TimeZoneId + "）"));
        text.AppendLine("通知规则：至少间隔 " + policy.MinimumIntervalSeconds + " 秒，每 24 小时最多 " + policy.MaximumNotificationsPer24Hours + " 次；仍受共享预算限制。");
        text.AppendLine("每次运行上限：只读检查 " + policy.MaximumCheckAttempts + " 次，摘要准备 " + policy.MaximumModelPreparations + " 次，通知尝试 " + policy.MaximumDeliveryAttempts + " 次。");
        text.AppendLine().AppendLine("触发与处理记录（共 " + runs.Length + " 条）：");
        if (runs.Length == 0) text.AppendLine("尚无已保存的触发记录；这不表示任务已执行或通知已送达。");
        foreach (var run in runs)
        {
            text.AppendLine("发生于 " + At(run.TriggeredAt) + " · " + RunStage(run.Stage));
            text.AppendLine("对应安排版本：" + run.WorkVersion + (run.WorkVersion == work.Version ? "" : "（历史版本）"));
            string? reason = ExplainReason(run, schedule);
            if (reason != null) text.AppendLine(reason);
            if (run.Stage is CommitmentRunStage.PendingCheck or CommitmentRunStage.Checking or CommitmentRunStage.ReadyToNotify or CommitmentRunStage.NotificationPending)
                text.AppendLine("最早允许继续的时间：" + At(run.NotBefore) + "（不保证届时执行或发送）");
            text.AppendLine("只读检查已启动 " + run.CheckAttempts + " 次；本事项通知批次已启动 " + Items(run.DeliveryAttempts).Count() + " 次。");
            AppendDelivery(text, saved, work, run, coverage);
            text.AppendLine();
        }
        text.Append("这里只查看保存的事实，不会重新检查任务或批准请求。流程结束不等于任务验证通过；接口受理不等于手机收到。结果未知的发送不会自动重试。");
        return new(saved.Identity, work.Id, work.Version, text.ToString());
    }
    private static void AppendDelivery(StringBuilder text, ConversationSnapshot saved, TrackedWorkItem work,
        CommitmentRun run, IReadOnlyDictionary<Guid, CatchUpEventProgress> coverage)
    {
        Guid? eventId = run.SharedDecisionEventId ?? run.NotificationEventId;
        if (eventId == null)
        { text.AppendLine("还没有关联的通知发送记录。"); return; }
        var entry = saved.Events.SingleOrDefault(x => x.Id == eventId && x.Identity == saved.Identity && x.Task == work.Task);
        bool valid = entry != null && entry.Source == ConversationSource.Notification;
        if (run.SharedDecisionEventId != null)
            valid &= entry?.RequiredDecision != null && entry.DecisionSubscribers is { } subscribers
                && subscribers.Any(x => x.WorkItemId == work.Id && x.WorkVersion > 0
                    && x.WorkVersion <= run.WorkVersion && run.WorkVersion <= work.Version)
                && (run.TriggerKey == "decision-notice/" + entry.Id.ToString("N") || run.TriggerKey == "decision/" + entry.RequiredDecision.DecisionToken);
        else
            valid &= Items(saved.NotificationDrafts).Any(x => x.Id == run.NotificationDraftId && x.Identity == saved.Identity
                && x.EventId == eventId && x.WorkRunId == run.Id && x.Task == work.Task);
        if (!valid) { text.AppendLine("通知关联暂无法核对，未将其他记录当作这项事项的回执。"); return; }
        var pages = saved.Outbox.Where(x => x.Identity == saved.Identity && x.Kind == OutboxKind.Notification
            && x.NotificationEventId == eventId).OrderBy(x => x.SegmentIndex).ToArray();
        if (run.SharedDecisionEventId != null)
            text.AppendLine("与必要审批提示共用发送记录；这里不展示审批内容，也不表示已经作出审批决定。");
        else if (Items(saved.NotificationDrafts).SingleOrDefault(x => x.Id == run.NotificationDraftId) is { } draft)
            text.AppendLine("通知准备：" + (draft.Cancelled ? "已取消后续准备" : draft.Stage switch
            { NotificationDraftStage.Captured => "已记录，尚未准备", NotificationDraftStage.Preparing => "准备中", _ => "已准备" })
                + "；已尝试 " + draft.PreparationAttempts + " 次。");
        if (pages.Length == 0) text.AppendLine("尚无已准备的通知页。");
        foreach (var page in pages)
            text.AppendLine("原通知第 " + (page.SegmentIndex + 1) + " 页：" + DeliveryState(page.Stage)
                + "；开始发送 " + page.SendAttempts + " 次，更新于 " + At(page.UpdatedAt) + "。");
        if (run.SharedDecisionEventId == null && coverage.TryGetValue(eventId.Value, out var progress)
            && progress.Task == work.Task && progress.Sources.All(source => pages.Any(page => page.Id == source.OutboxId)))
        {
            text.AppendLine("正文受理覆盖：" + progress.AcceptedCharacters + " / " + progress.TotalCharacters + " 字符；剩余 " + progress.RemainingCharacters + " 字符。原发送回执保持不变。");
            if (progress.HasUnknown) text.AppendLine("其中有发送结果未知的内容，不自动重组重发。");
            if (progress.HasSending) text.AppendLine("其中有正在发送的内容，尚未取得最终回执。");
        }
    }
    internal static string FormatPage(CommitmentDetail detail, CommitmentDetailQuery query, DateTimeOffset? capturedAt = null, string? pageToken = null)
    {
        var pages = Split(detail.Body); string fingerprint = pageToken ?? Fingerprint(detail);
        if (query.WorkItemId != detail.WorkItemId) throw new ArgumentException("Detail identity mismatch.");
        if ((query.Page > 1 && query.Fingerprint == null) || query.Fingerprint != null && query.Fingerprint != fingerprint)
            return "记录已经变化，或缺少原页的校验值。请重新发送“事项详情 " + detail.WorkItemId.ToString("D") + "”从第一页查看，避免混用不同时间的记录。";
        if (query.Page > pages.Count) return "这份详情共有 " + pages.Count + " 页，请按上一页给出的完整命令翻页。";
        string next = query.Page < pages.Count ? "下一页：事项详情 " + detail.WorkItemId.ToString("D") + " " + (query.Page + 1) + " " + fingerprint : "这份详情已全部展示。";
        string readAt = capturedAt == null ? "" : "\n读取于 " + At(capturedAt.Value);
        string freshness = capturedAt == null ? "" : "\n此页来自当时记录，重新发“事项详情 完整编号”获取最新。";
        return "事项 " + detail.WorkItemId.ToString("D") + " · 第 " + query.Page + "/" + pages.Count + " 页" + readAt + "\n\n"
            + pages[query.Page - 1] + "\n\n" + next + freshness;
    }
    internal static IReadOnlyList<string> Split(string body)
    {
        var pages = new List<string>();
        for (int offset = 0; offset < body.Length;)
        {
            int count = Math.Min(PageCharacters, body.Length - offset);
            if (offset + count < body.Length && char.IsHighSurrogate(body[offset + count - 1])) count--;
            pages.Add(body.Substring(offset, count)); offset += count;
        }
        if (pages.Count == 0) pages.Add("");
        return pages;
    }
    internal static string Fingerprint(CommitmentDetail detail)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(detail.WorkItemId.ToString("N") + "\n" + detail.WorkVersion + "\n" + detail.Body)))[..16];
    private static string? ExplainReason(CommitmentRun run, ReminderSchedule? schedule)
    {
        if (schedule?.FiredRunId == run.Id && run.TriggeredAt - schedule.DueAtUtc > TimeSpan.FromMinutes(1))
        {
            if (schedule.MissedPolicy == MissedTriggerPolicy.Skip) return "错过原定时间，已按保存的规则跳过。";
            if (schedule.MissedPolicy == MissedTriggerPolicy.NeedsDecision)
                return "错过原定时间，需要重新决定。如需改期，先取消旧事项，再明确新时间建立提醒；已触发的原事项不能直接改期。";
        }
        return run.Reason switch
        {
            null or "" => null,
            "Fresh observation does not establish inactivity." => "新的观察不满足无活动条件，本次检查没有继续通知。",
            "The exact task turn reached a terminal state before this check started." => "对应轮次已结束，本次尚未开始的检查已跳过。",
            "No fresh connected task evidence was available." => "没有取得足够新的连接状态证据，不能据此判断任务卡住。",
            "Deadline expired." => "已超过保存的截止时间，不再继续处理。",
            "Read task ended after the commitment reached a terminal state." => "这项安排已结束，迟到的只读检查也已结束，没有重新安排。",
            "Read task ended without a committed fresh observation." => "本次只读检查结束，但未保存新的有效观察。",
            _ => "记录里没有可直接说明的原因，仍需核对。"
        };
    }
    private static string WorkStage(TrackedWorkItem work) => work.Stage switch
    {
        TrackedWorkStage.Active => "安排生效中", TrackedWorkStage.Triggered => "已触发，仍需核对通知记录",
        TrackedWorkStage.Paused => "已暂停后续触发", TrackedWorkStage.Completed => work.Kind == TrackedWorkKind.Reminder ? "提醒流程已结束" : "订阅流程已结束",
        TrackedWorkStage.Cancelled => "已取消后续触发，历史保留", TrackedWorkStage.Expired => "已过期或按错过策略跳过", _ => "需要你重新决定"
    };
    private static string RunStage(CommitmentRunStage stage) => stage switch
    {
        CommitmentRunStage.PendingCheck => "等待只读检查", CommitmentRunStage.Checking => "只读检查已开始",
        CommitmentRunStage.ReadyToNotify => "等待准备通知", CommitmentRunStage.NotificationPending => "等待核对通知结果",
        CommitmentRunStage.Completed => "本次处理流程已结束", CommitmentRunStage.Skipped => "本次已跳过",
        CommitmentRunStage.Cancelled => "本次已取消", _ => "需要你决定"
    };
    private static string TaskState(CommitmentTaskState state) => state switch
    {
        CommitmentTaskState.Running => "运行中", CommitmentTaskState.NeedsDecision => "需要决定", CommitmentTaskState.Completed => "这一轮结束",
        CommitmentTaskState.Failed => "失败", CommitmentTaskState.Interrupted => "已中断", _ => "结果未知"
    };
    private static string DeliveryState(OutboxStage stage) => stage switch
    {
        OutboxStage.Pending => "尚未发送", OutboxStage.Sending => "发送中，未取得回执", OutboxStage.Accepted => "渠道接口已受理，手机送达未核实",
        OutboxStage.Rejected => "渠道明确拒绝", OutboxStage.Unknown => "结果未知，不自动重发", _ => "未开始内容已停止发送"
    };
    private static string At(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    private static IEnumerable<T> Items<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;
}

internal sealed record CommitmentDetailPageSnapshot(CommitmentDetail Detail, DateTimeOffset CapturedAt, string Token);

// Volatile, strictly bounded query snapshots. A page token is never a control grant.
// Session changes discard every entry, including activation of the same identity.
internal sealed class CommitmentDetailPageCache
{
    private readonly object sync = new();
    private readonly List<CommitmentDetailPageSnapshot> entries = [];
    private long epoch;
    internal long Epoch { get { lock (sync) return epoch; } }
    internal void Clear() { lock (sync) { entries.Clear(); epoch++; } }
    internal bool Store(long expectedEpoch, CommitmentDetail detail, DateTimeOffset now, out CommitmentDetailPageSnapshot? snapshot)
    {
        lock (sync)
        {
            snapshot = null; Purge(now);
            if (epoch != expectedEpoch || detail.Body.Length > 2 * 1024 * 1024) return false;
            while (entries.Count >= 8 || entries.Sum(item => item.Detail.Body.Length) + detail.Body.Length > 2 * 1024 * 1024)
                entries.RemoveAt(0);
            string token;
            do { token = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(); }
            while (entries.Any(item => item.Token == token));
            snapshot = new(detail, now, token); entries.Add(snapshot); return true;
        }
    }
    internal CommitmentDetailPageSnapshot? Find(long expectedEpoch, ConversationIdentity identity, Guid id, long currentVersion,
        string fingerprint, DateTimeOffset now)
    {
        lock (sync)
        {
            Purge(now);
            if (epoch != expectedEpoch) return null;
            entries.RemoveAll(item => item.Detail.Identity == identity && item.Detail.WorkItemId == id && item.Detail.WorkVersion != currentVersion);
            return entries.SingleOrDefault(item => item.Detail.Identity == identity && item.Detail.WorkItemId == id
                && item.Detail.WorkVersion == currentVersion && item.Token == fingerprint);
        }
    }
    private void Purge(DateTimeOffset now) => entries.RemoveAll(item => now < item.CapturedAt || now - item.CapturedAt >= TimeSpan.FromMinutes(5));
}
