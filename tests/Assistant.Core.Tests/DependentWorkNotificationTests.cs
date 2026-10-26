using Assistant.Core;

internal static partial class DependentWorkTests
{
    private static async Task<DependentWorkItem> Expired(DurableConversationLedger ledger, F f)
    {
        var work = await NotifyingWork(ledger, f); f.Clock.UtcNow += TimeSpan.FromHours(2);
        return (await Evaluate(ledger, work, [])).Work;
    }
    private static Task<DependentWorkItem> NotifyingWork(DurableConversationLedger ledger, F f) =>
        Execute(ledger, f, "schedule_dependency", Spec(f) with { NotificationPolicy = new() }, "finish documentation");
    private static async Task<NotificationEnqueueResult> Notice(DurableConversationLedger ledger, DependentWorkItem work) =>
        (await ledger.EnqueueDependentWorkNotificationAsync(Identity, new(work.Id, work.Version, work.Stage), "Factual first page and second page", ["first page", "second page"]))!;

    private static async Task NoticeRecovery()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Expired(l, f);
        var notice = await Notice(l, w); Check(!notice.IsDuplicate); Equal(2, notice.Outbox.Length);
        Check((await Notice(l, w)).IsDuplicate);
        await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, notice.Outbox[0].Id, OutboxStage.Accepted, "synthetic API acceptance");
        await l.DisposeAsync(); l = await f.Open();
        var same = await Notice(l, w); Check(same.IsDuplicate); Equal(notice.Event.Id, same.Event.Id);
        Equal(OutboxStage.Accepted, same.Outbox[0].Stage); Equal(OutboxStage.Pending, same.Outbox[1].Stage);
        Check((await l.GetDependentWorkAsync(Identity)).Attempts.IsEmpty);
    }
    private static async Task NoticeQuiet()
    {
        await using var f = new F(); var l = await f.Open();
        string start = f.Clock.UtcNow.ToString("HH:mm"), end = f.Clock.UtcNow.AddHours(4).ToString("HH:mm");
        var w = await Execute(l, f, "schedule_dependency", Spec(f) with { NotificationPolicy = new(start, end, "UTC") }, "finish documentation");
        f.Clock.UtcNow += TimeSpan.FromHours(2); w = (await Evaluate(l, w, [])).Work;
        var notice = await Notice(l, w);
        Check(!(await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(o => o.NotificationEventId == notice.Event.Id));
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id));
        Check((await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.IsEmpty);
        await l.DisposeAsync(); l = await f.Open(); f.Clock.UtcNow += TimeSpan.FromHours(3);
        var first = await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id); Check(l.IsDependentNotificationCurrent(first));
        await l.RecordOutboxOutcomeAsync(Identity, first.Id, OutboxStage.Accepted, "first accepted");
        await l.BeginOutboxSendAsync(Identity, notice.Outbox[1].Id);
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task NoticeChange()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Expired(l, f); var notice = await Notice(l, w);
        var cancelled = await Change(l, f, w, "cancel");
        Check((await l.ReadConversationAsync(Identity)).Outbox.Where(o => o.NotificationEventId == notice.Event.Id).All(o => o.Stage == OutboxStage.Superseded));
        Check(await l.EnqueueDependentWorkNotificationAsync(Identity, new(w.Id, w.Version, w.Stage), "stale", ["stale"]) == null);
        w = await Expired(l, f); notice = await Notice(l, w);
        var sending = await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id); Check(l.IsDependentNotificationCurrent(sending));
        var renewed = await Change(l, f, w, "update", deadline: f.Clock.UtcNow.AddHours(1).ToString("O"));
        Check(!l.IsDependentNotificationCurrent(sending));
        await l.RecordOutboxOutcomeAsync(Identity, sending.Id, OutboxStage.Rejected, "cancelled at actual channel boundary");
        await Error(LedgerErrors.NotificationSuppressed, () => l.RetryRejectedOutboxAsync(Identity, sending.Id));
        f.Clock.UtcNow += TimeSpan.FromHours(2); renewed = (await Evaluate(l, renewed, [])).Work;
        var later = await Notice(l, renewed); Check(later.Event.Id != notice.Event.Id && !later.IsDuplicate);
        Equal(renewed.Version, later.Event.DependentWorkNotice!.WorkVersion);
    }
    private static async Task NoticeBudget()
    {
        await using var f = new F(); var options = new LedgerOptions { CommitmentBudgets = new() { MaximumNotificationsPerMinute = 1 } };
        var l = await f.Open(options: options); var a = await NotifyingWork(l, f); var b = await NotifyingWork(l, f);
        f.Clock.UtcNow += TimeSpan.FromHours(2); a = (await Evaluate(l, a, [])).Work; b = (await Evaluate(l, b, [])).Work;
        var first = await Notice(l, a); var second = await Notice(l, b);
        await l.BeginOutboxSendAsync(Identity, first.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, first.Outbox[0].Id, OutboxStage.Accepted, "accepted");
        await l.BeginOutboxSendAsync(Identity, first.Outbox[1].Id); // Same logical notice has already paid.
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginOutboxSendAsync(Identity, second.Outbox[0].Id));
        var status = await l.GetCommitmentBudgetStatusAsync(Identity);
        Equal(1, status.Windows.Single(x => x.Kind == CommitmentBudgetKind.Notification && x.Window == TimeSpan.FromDays(1)).Used);
        await l.DisposeAsync(); l = await f.Open(options: options); f.Clock.UtcNow += TimeSpan.FromMinutes(1);
        await l.BeginOutboxSendAsync(Identity, second.Outbox[0].Id);
        Check((await l.GetDependentWorkAsync(Identity)).Attempts.IsEmpty);
    }
    private static async Task NoticeUnknown()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Expired(l, f); var notice = await Notice(l, work);
        var sending = await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id);
        var attempt = new OutboxAttemptLease(Identity, sending.Id, sending.SendAttempts, sending.AttemptTrustedInputRevision);
        await l.DisposeAsync(); l = await f.Open();
        Check((await l.RetryRejectedNotificationsAsync(Identity)).IsEmpty);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginOutboxSendAsync(Identity, sending.Id));
        await Change(l, f, work, "cancel");
        await l.RecordOutboxAttemptOutcomeAsync(attempt, OutboxStage.Accepted, "late exact API receipt");
        var saved = await l.ReadConversationAsync(Identity);
        Equal(OutboxStage.Accepted, saved.Outbox.Single(o => o.Id == sending.Id).Stage);
        Equal(DependentWorkStage.Cancelled, saved.DependentWork!.WorkItems.Single().Stage);
        Check(saved.DependentWork.Attempts.IsEmpty);
    }
    private static async Task NoticeCatchUp()
    {
        await using var f = new F(); var l = await f.Open(); var w = await Expired(l, f); var notice = await Notice(l, w);
        await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, notice.Outbox[0].Id, OutboxStage.Rejected, "context expired", rejectionReason: OutboxRejectionReason.ContextExpired);
        await l.ReceiveAsync(Identity, "fresh-context", "status", MessageTrust.AuthenticatedUser, f.Clock.UtcNow,
            catchUp: new(CatchUpDisposition.AllowHistory, true, "UTC"));
        var history = await l.PrepareCatchUpAsync(Identity, "fresh-context");
        Check(!history.Window.CandidateEventIds.Contains(notice.Event.Id)); Check(history.Batch == null);
        await l.RetryRejectedNotificationsAsync(Identity); await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, notice.Outbox[0].Id, OutboxStage.Rejected, "again");
        await l.RetryRejectedNotificationsAsync(Identity); await l.BeginOutboxSendAsync(Identity, notice.Outbox[0].Id);
        await l.RecordOutboxOutcomeAsync(Identity, notice.Outbox[0].Id, OutboxStage.Rejected, "third");
        Check((await l.RetryRejectedNotificationsAsync(Identity)).IsEmpty);
        Equal(3, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task NoticeFault()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
            var work = await Expired(l, f); fault.Stage = stage;
            await Throws<InjectedFailure>(() => Notice(l, work)); await l.DisposeAsync(); l = await f.Open();
            var notice = await Notice(l, work); Equal(stage == JournalWriteStage.AfterFlush, notice.IsDuplicate);
            Equal(1, (await l.ReadConversationAsync(Identity)).Events.Count(e => e.DependentWorkNotice != null));
        }
    }
    private static async Task NoticeCorruption()
    {
        for (int mode = 0; mode < 4; mode++)
        {
            await using var f = new F(); var l = await f.Open(); var w = await Expired(l, f); await Notice(l, w); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                if (mode == 0) root["schemaVersion"] = 8;
                else if (mode == 1) root["events"]!.AsArray().Single(e => e?["dependentWorkNotice"] != null)!["dependentWorkNotice"]!["workVersion"] = 9;
                else if (mode == 2) root["dependentWork"]![0]!["notificationPolicy"] = System.Text.Json.Nodes.JsonNode.Parse("{\"quietStart\":\"22:00\",\"quietEnd\":\"08:00\",\"timeZoneId\":\"UTC\"}");
                else root["events"]!.AsArray().Single(e => e?["dependentWorkNotice"] != null)!["dependentWorkNotice"] = null;
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task NoticeLegacy()
    {
        await using var f = new F(); var l = await f.Open(); var old = await Create(l, f);
        f.Clock.UtcNow += TimeSpan.FromHours(2); old = (await Evaluate(l, old, [])).Work;
        await l.DisposeAsync(); l = await f.Open();
        Check(await l.EnqueueDependentWorkNotificationAsync(Identity, new(old.Id, old.Version, old.Stage), "old expiry", ["old expiry"]) == null);
        Check((await l.ReadConversationAsync(Identity)).Outbox.IsEmpty);
        var enabled = await Execute(l, f, "change_dependency", new DependentWorkChange(old.Id, old.Version, "update",
            DeadlineUtc: f.Clock.UtcNow.AddHours(1).ToString("O"), NotificationPolicy: new()), "explicit renewal and notifications");
        f.Clock.UtcNow += TimeSpan.FromHours(2); enabled = (await Evaluate(l, enabled, [])).Work;
        Check(!(await Notice(l, enabled)).IsDuplicate);
    }
}
