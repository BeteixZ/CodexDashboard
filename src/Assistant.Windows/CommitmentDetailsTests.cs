using System;
using System.Collections.Immutable;
using System.Linq;
using Assistant.Core;

namespace CodexDashboard;

internal static class CommitmentDetailsTests
{
    internal static void Run(Action<string, bool> check)
    {
        var saved = Sample(); var work = saved.WorkItems.Single(); var schedule = saved.Schedules.Single();
        var detail = CommitmentDetails.Read(saved, work.Id)!;
        check("projection_keeps_full_saved_goal_and_exact_schedule", detail.Body.Contains(work.Goal, StringComparison.Ordinal)
            && detail.Body.Contains(schedule.LocalTime.Replace('T', ' ')) && detail.Body.Contains(schedule.TimeZoneId)
            && detail.Body.Contains("2026-10-21 03:00:00 UTC") && detail.Identity == saved.Identity && detail.WorkVersion == work.Version);
        var pages = CommitmentDetails.Split(detail.Body);
        check("page_payloads_preserve_all_whitespace_and_surrogate_pairs", string.Concat(pages) == detail.Body
            && pages.All(x => x.Length <= CommitmentDetails.PageCharacters && WellFormed(x)));
        bool exact = true;
        for (int index = 0; index < pages.Count; index++)
        {
            string page = CommitmentDetails.FormatPage(detail, new(work.Id, index + 1, CommitmentDetails.Fingerprint(detail)), work.CreatedAt);
            int start = page.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            exact &= page.Length <= 900 && page.Substring(start, pages[index].Length) == pages[index] && WellFormed(page);
        }
        check("every_formatted_page_fits_real_channel_limit_without_cutting_payload", exact);
        check("strict_query_accepts_full_ids_and_rejects_partial_or_control_suffix",
            CommitmentDetails.TryParse("事项详情 " + work.Id.ToString("D"), out var parsed) && parsed!.WorkItemId == work.Id
            && CommitmentDetails.TryParse("事项详情 " + work.Id.ToString("N") + " 2 " + CommitmentDetails.Fingerprint(detail), out _)
            && !CommitmentDetails.TryParse("事项详情 " + work.Id.ToString("N")[..8], out _)
            && !CommitmentDetails.TryParse("事项详情 " + work.Id + "；取消事项 " + work.Id, out _)
            && !CommitmentDetails.TryParse("事项详情 " + Guid.Empty, out _));
        check("later_pages_require_the_same_detail_facts",
            CommitmentDetails.FormatPage(detail, new(work.Id, 2, null)).Contains("从第一页")
            && CommitmentDetails.FormatPage(detail with { Body = detail.Body + "新事实" }, new(work.Id, 2, CommitmentDetails.Fingerprint(detail))).Contains("从第一页"));
        var foreign = saved with { WorkItems = [work with { Identity = new("other", "binding", "main") }] };
        check("foreign_work_never_projects_under_current_identity", CommitmentDetails.Read(foreign, work.Id) == null);
        var run = new CommitmentRun(Guid.NewGuid(), work.Id, saved.Identity, work.Version, "reminder", schedule.DueAtUtc.AddMinutes(2),
            CommitmentRunStage.Skipped, schedule.DueAtUtc.AddMinutes(2), Reason: "SECRET_INTERNAL_REASON");
        var skipped = saved with { CommitmentRuns = [run], Schedules = [schedule with { FiredRunId = run.Id, MissedPolicy = MissedTriggerPolicy.Skip }] };
        check("missed_skip_is_explained_from_actual_run_and_saved_policy", CommitmentDetails.Read(skipped, work.Id)!.Body.Contains("错过原定时间，已按保存的规则跳过。"));
        var decision = skipped with { WorkItems = [work with { Stage = TrackedWorkStage.NeedsDecision }],
            Schedules = [skipped.Schedules.Single() with { MissedPolicy = MissedTriggerPolicy.NeedsDecision }] };
        check("missed_decision_explains_cancel_then_recreate_without_new_authority", CommitmentDetails.Read(decision, work.Id)!.Body.Contains("先取消旧事项，再明确新时间建立提醒"));
        var unspecified = saved with { CommitmentRuns = [run with { Reason = "SECRET_INTERNAL_REASON" }] };
        check("unknown_reason_never_leaks_raw_internal_diagnostics", !CommitmentDetails.Read(unspecified, work.Id)!.Body.Contains("SECRET_INTERNAL_REASON")
            && CommitmentDetails.Read(unspecified, work.Id)!.Body.Contains("记录里没有可直接说明的原因，仍需核对"));
        ProjectionLinks(saved, work, run, check);
        Cache(detail, work.CreatedAt, check);
    }
    private static void Cache(CommitmentDetail detail, DateTimeOffset now, Action<string, bool> check)
    {
        var cache = new CommitmentDetailPageCache(); long epoch = cache.Epoch;
        check("page_cache_captures_only_safe_read_projection", cache.Store(epoch, detail, now, out var first)
            && first!.Detail == detail && first.CapturedAt == now);
        string hash = first!.Token;
        _ = cache.Store(epoch, detail, now.AddSeconds(1), out var second);
        check("equal_content_new_query_has_its_own_token_and_keeps_old_capture_time", second!.Token != first.Token
            && cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, first.Token, now.AddSeconds(2))!.CapturedAt == now
            && cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, second.Token, now.AddSeconds(2))!.CapturedAt == now.AddSeconds(1));
        check("observations_can_change_without_mixing_pages_of_old_snapshot",
            cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, hash, now.AddSeconds(10))!.Detail.Body == detail.Body);
        check("new_work_version_revokes_previous_page_snapshot", cache.Find(epoch, detail.Identity, detail.WorkItemId,
            detail.WorkVersion + 1, hash, now.AddSeconds(10)) == null);
        _ = cache.Store(epoch, detail, now, out first); hash = first!.Token;
        check("page_cache_expires_at_five_minutes", cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, hash, now.AddMinutes(5)) == null);
        _ = cache.Store(epoch, detail, now, out _); cache.Clear();
        check("session_reset_blocks_old_reads_and_old_inflight_store", cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, hash, now) == null
            && !cache.Store(epoch, detail, now, out _));
        epoch = cache.Epoch; _ = cache.Store(epoch, detail, now, out first); hash = first!.Token;
        check("page_cache_never_crosses_identity", cache.Find(epoch, new("other", "binding", "main"), detail.WorkItemId, detail.WorkVersion, hash, now) == null);
        for (int index = 0; index < 8; index++) _ = cache.Store(epoch, detail with { WorkItemId = Guid.NewGuid() }, now.AddSeconds(index), out _);
        check("page_cache_evicts_oldest_after_eight_snapshots", cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, hash, now.AddSeconds(9)) == null);
        cache.Clear(); epoch = cache.Epoch;
        _ = cache.Store(epoch, detail, now, out first); hash = first!.Token;
        _ = cache.Store(epoch, detail with { WorkItemId = Guid.NewGuid(), Body = new string('x', 2 * 1024 * 1024) }, now.AddSeconds(1), out _);
        check("page_cache_obeys_total_character_cap_without_truncation", cache.Find(epoch, detail.Identity, detail.WorkItemId, detail.WorkVersion, hash, now.AddSeconds(2)) == null
            && !cache.Store(epoch, detail with { Body = new string('x', 2 * 1024 * 1024 + 1) }, now, out _));
    }
    private static void ProjectionLinks(ConversationSnapshot saved, TrackedWorkItem work, CommitmentRun run, Action<string, bool> check)
    {
        const string secret = "PRIVATE_REPORT_OR_APPROVAL_BODY";
        var at = work.CreatedAt; var eventId = Guid.NewGuid(); var draftId = Guid.NewGuid();
        var entry = new ConversationEvent(eventId, saved.Identity, ConversationSource.Notification, secret, at, at, 1,
            ExternalEventKey: "synthetic-notice");
        var draft = new DurableNotificationDraft(draftId, saved.Identity, eventId, "synthetic-notice", secret, "{}", null,
            ConversationSource.Notification, NotificationDraftStage.Finalized, 1, 2, at, at, PreparedBody: secret, WorkRunId: run.Id);
        var original = new OutboxMessage(Guid.NewGuid(), saved.Identity, null, 1, 0, new string('文', 200), OutboxStage.Unknown,
            at, at, "PRIVATE_RECEIPT", OutboxKind.Notification, eventId, SendAttempts: 1);
        var pending = run with { Stage = CommitmentRunStage.NotificationPending, NotificationEventId = eventId,
            NotificationDraftId = draftId, Body = secret, Reason = null, DeliveryAttempts = [at] };
        var state = saved with { Events = [entry], NotificationDrafts = [draft], CommitmentRuns = [pending], Outbox = [original] };
        var body = CommitmentDetails.Read(state, work.Id)!.Body;
        check("unknown_exact_outbox_stays_unknown_and_raw_bodies_are_omitted", body.Contains("原通知第 1 页：结果未知，不自动重发")
            && !body.Contains(secret) && !body.Contains("PRIVATE_RECEIPT") && state.Outbox.Single() == original);
        var corrupted = state with { NotificationDrafts = [draft with { WorkRunId = Guid.NewGuid() }] };
        check("wrong_run_link_is_not_presented_as_its_delivery", CommitmentDetails.Read(corrupted, work.Id)!.Body.Contains("通知关联暂无法核对")
            && !CommitmentDetails.Read(corrupted, work.Id)!.Body.Contains("原通知第"));
        var task = new TaskReference("host", "thread", "turn", "profile");
        var sharedWork = work with { Kind = TrackedWorkKind.Subscription, Task = task };
        var shared = entry with { Task = task, RequiredDecision = new("profile", "PRIVATE_DECISION_TOKEN", Guid.NewGuid(), at.AddHours(1)),
            DecisionSubscribers = [new(work.Id, work.Version)] };
        var sharedRun = pending with { SharedDecisionEventId = eventId, NotificationDraftId = null, NotificationEventId = null,
            TriggerKey = "decision-notice/" + eventId.ToString("N"), DeliveryAttempts = [] };
        var sharedState = state with { WorkItems = [sharedWork], Events = [shared], NotificationDrafts = [], CommitmentRuns = [sharedRun],
            Outbox = [original with { Stage = OutboxStage.Accepted }] };
        string sharedBody = CommitmentDetails.Read(sharedState, work.Id)!.Body;
        check("shared_approval_detail_only_shows_actual_receipt_not_approval_content_or_authority", sharedBody.Contains("与必要审批提示共用发送记录")
            && sharedBody.Contains("渠道接口已受理，手机送达未核实") && !sharedBody.Contains("PRIVATE_DECISION_TOKEN")
            && !sharedBody.Contains(secret) && sharedBody.Contains("不表示已经作出审批决定"));
        check("shared_approval_with_wrong_exact_target_is_not_joined", CommitmentDetails.Read(sharedState with
            { Events = [shared with { Task = task with { TurnId = "other-turn" } }] }, work.Id)!.Body.Contains("通知关联暂无法核对"));
        var cancelledShared = sharedState with
        {
            WorkItems = [sharedWork with { Version = 2, Stage = TrackedWorkStage.Cancelled }],
            CommitmentRuns = [sharedRun with { WorkVersion = 2, Stage = CommitmentRunStage.Cancelled }],
            Outbox = [original]
        };
        string cancelledBody = CommitmentDetails.Read(cancelledShared, work.Id)!.Body;
        check("cancelled_shared_run_retains_older_subscriber_provenance_and_unknown_receipt", cancelledBody.Contains("与必要审批提示共用发送记录")
            && cancelledBody.Contains("原通知第 1 页：结果未知，不自动重发") && !cancelledBody.Contains("暂无法核对")
            && !cancelledBody.Contains("PRIVATE_DECISION_TOKEN") && !cancelledBody.Contains(secret));
        var deliveryId = Guid.NewGuid(); var catchEvent = entry with { Id = Guid.NewGuid(), ExternalEventKey = "synthetic-catchup" };
        var window = new CatchUpWindow(Guid.NewGuid(), saved.Identity, "fresh-input", 2, 1, at,
            new(CatchUpDisposition.AllowHistory, true, "UTC"), CatchUpWindowStage.Prepared, [eventId], [], []);
        var batch = new CatchUpBatch(Guid.NewGuid(), window.Id, saved.Identity, catchEvent.Id, at,
            [new(eventId, [new(original.Id, 1, "hash", 0, 100)], deliveryId, "group", run.Id, work.Version)], 2);
        var catchup = state with { Events = [entry, catchEvent], CatchUpWindows = [window], CatchUpBatches = [batch],
            Outbox = [original with { Stage = OutboxStage.Rejected }, new(deliveryId, saved.Identity, null, 2, 0, secret,
                OutboxStage.Accepted, at, at, "PRIVATE_CATCHUP_RECEIPT", OutboxKind.Notification, catchEvent.Id)] };
        string partial = CommitmentDetails.Read(catchup, work.Id)!.Body;
        check("partial_catchup_uses_core_coverage_without_relabeling_original_rejection", partial.Contains("原通知第 1 页：渠道明确拒绝")
            && partial.Contains("正文受理覆盖：100 / 200 字符；剩余 100 字符") && !partial.Contains("PRIVATE_CATCHUP_RECEIPT"));
        string uncertain = CommitmentDetails.Read(catchup with { Outbox = catchup.Outbox.SetItem(1, catchup.Outbox[1] with { Stage = OutboxStage.Unknown }) }, work.Id)!.Body;
        check("unknown_catchup_does_not_count_as_accepted", uncertain.Contains("正文受理覆盖：0 / 200 字符") && uncertain.Contains("不自动重组重发"));
    }
    internal static ConversationSnapshot Sample()
    {
        var identity = new ConversationIdentity("details-test", "binding", "main");
        var at = DateTimeOffset.Parse("2026-10-21T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var work = new TrackedWorkItem(Guid.NewGuid(), identity, TrackedWorkKind.Reminder, "original", Guid.NewGuid(), 1,
            TrackedWorkStage.Active, "  完整安排前置空格\n" + new string('文', 672) + "😀\n末尾保留  ", null, at, at, null, new());
        return new(identity, 1, [], [], [], [], [], [], false, null,
            NotificationDrafts: [], WorkItems: [work], Schedules: [new(work.Id, "2026-10-21T11:00:00", "Asia/Taipei", at, at.AddHours(2), MissedTriggerPolicy.NotifyOnce)],
            Subscriptions: [], CommitmentRuns: [], CatchUpWindows: [], CatchUpBatches: []);
    }
    private static bool WellFormed(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
}
