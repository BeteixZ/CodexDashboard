using Assistant.Core;

internal static partial class DependentWorkTests
{
    private static async Task<(DependentWorkItem Work, NotificationEnqueueResult Warning, CommitmentObservationLease Source)> RecoverySetup(DurableConversationLedger l, F f, bool accept = true)
    {
        var work = await NotifyingWork(l, f); var source = await Source(l);
        work = (await Evaluate(l, work, [Proof(f, source, Target, CommitmentTaskState.Completed, 1, true),
            Proof(f, source, Predecessor, CommitmentTaskState.Failed)])).Work;
        var warning = await Notice(l, work);
        if (accept) await AcceptNotice(l, warning);
        return (work, warning, source);
    }
    private static async Task AcceptNotice(DurableConversationLedger l, NotificationEnqueueResult notice)
    {
        foreach (var page in notice.Outbox)
        {
            await l.BeginOutboxSendAsync(Identity, page.Id);
            await l.RecordOutboxOutcomeAsync(Identity, page.Id, OutboxStage.Accepted, "synthetic API receipt");
        }
    }
    private static async Task<DependentWorkItem> StartRecovered(DurableConversationLedger l, F f, DependentWorkItem work, CommitmentObservationLease source)
    {
        work = await Change(l, f, work, "update", "after explicit correction");
        var lease = (await Evaluate(l, work, Evidence(f, source, 2), true)).Lease!;
        return await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "new turn accepted", Target with { TurnId = "recovered-result" });
    }
    private static async Task<NotificationEnqueueResult> RecoveryNotice(DurableConversationLedger l, DependentWorkItem work)
    {
        var expected = DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work)!;
        return (await l.EnqueueDependentWorkNotificationAsync(Identity, expected, "verified recovery " + work.Stage, ["verified recovery " + work.Stage]))!;
    }
    private static async Task RecoveryMilestones()
    {
        await using var f = new F(); var l = await f.Open(); var (work, warning, source) = await RecoverySetup(l, f);
        work = await Change(l, f, work, "update", "after explicit correction");
        Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
        var lease = (await Evaluate(l, work, Evidence(f, source, 2), true)).Lease!;
        work = (await l.GetDependentWorkAsync(Identity)).WorkItems.Single();
        Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
        work = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Unknown, "lost receipt");
        Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
        work = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "exact late receipt", Target with { TurnId = "recovered-result" });
        var running = await RecoveryNotice(l, work); Equal(warning.Event.Id, running.Event.DependentWorkNotice!.RecoveryOfEventId);
        Equal(lease.AttemptId, running.Event.DependentWorkNotice.AttemptId); Equal(work.ResultTask, running.Event.Task);
        await AcceptNotice(l, running); Check((await RecoveryNotice(l, work)).IsDuplicate);
        work = await l.ObserveDependentCompletionAsync(Identity, work.Id, Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed, 3).Observation);
        var completed = await RecoveryNotice(l, work); Check(completed.Event.Id != running.Event.Id); Equal(DependentWorkStage.Completed, completed.Event.DependentWorkNotice!.Stage);
        await l.DisposeAsync(); l = await f.Open(); Check((await RecoveryNotice(l, work)).IsDuplicate);
    }
    private static async Task RecoveryUnseenWarning()
    {
        foreach (var mode in new[] { "pending", "partial", "rejected", "unknown" })
        {
            await using var f = new F(); var l = await f.Open(); var (work, warning, source) = await RecoverySetup(l, f, false);
            if (mode != "pending")
            {
                await l.BeginOutboxSendAsync(Identity, warning.Outbox[0].Id);
                await l.RecordOutboxOutcomeAsync(Identity, warning.Outbox[0].Id, mode == "partial" ? OutboxStage.Accepted : mode == "unknown" ? OutboxStage.Unknown : OutboxStage.Rejected, mode);
            }
            work = await StartRecovered(l, f, work, source);
            Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
            Check(await l.EnqueueDependentWorkNotificationAsync(Identity, new(work.Id, work.Version, work.Stage, warning.Event.Id, work.AttemptId), "unsupported recovery", ["unsupported recovery"]) == null);
            work = await l.ObserveDependentCompletionAsync(Identity, work.Id, Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed, 3).Observation);
            Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
        }
    }
    private static async Task RecoveryQuietCoalescing()
    {
        await using var f = new F(); var l = await f.Open(); var (work, _, source) = await RecoverySetup(l, f);
        var policy = new CommitmentPolicy(f.Clock.UtcNow.ToString("HH:mm"), f.Clock.UtcNow.AddHours(1).ToString("HH:mm"), "UTC");
        work = await Execute(l, f, "change_dependency", new DependentWorkChange(work.Id, work.Version, "update", NotificationPolicy: policy), "quiet hours");
        var lease = (await Evaluate(l, work, Evidence(f, source, 2), true)).Lease!;
        work = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", Target with { TurnId = "recovered-result" });
        var running = await RecoveryNotice(l, work); Check(!(await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Any(o => o.NotificationEventId == running.Event.Id));
        work = await l.ObserveDependentCompletionAsync(Identity, work.Id, Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed, 3).Observation);
        var completed = await RecoveryNotice(l, work);
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single(o => o.Id == running.Outbox.Single().Id).Stage);
        Check(!l.IsDependentNotificationCurrent(running.Outbox.Single()));
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
        f.Clock.UtcNow += TimeSpan.FromHours(2); await AcceptNotice(l, completed);
        Equal(2, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task RecoveryPauseCancel()
    {
        await using var f = new F(); var l = await f.Open(); var (work, _, source) = await RecoverySetup(l, f);
        work = await StartRecovered(l, f, work, source); var running = await RecoveryNotice(l, work); await AcceptNotice(l, running);
        work = await Change(l, f, work, "pause"); work = await Change(l, f, work, "resume");
        Equal(running.Event.DependentWorkNotice, DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work));
        Check(await l.EnqueueDependentWorkNotificationAsync(Identity, running.Event.DependentWorkNotice!, running.Event.Body, [running.Event.Body]) == null);
        Equal(1, (await l.ReadConversationAsync(Identity)).Events.Count(e => e.DependentWorkNotice?.AttemptId == work.AttemptId));
        work = await Change(l, f, work, "cancel");
        work = await l.ObserveDependentCompletionAsync(Identity, work.Id, Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed, 3).Observation);
        Equal(DependentWorkStage.Cancelled, work.Stage); Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
    }
    private static async Task RecoveryFault()
    {
        foreach (var stage in new[] { JournalWriteStage.BeforeWrite, JournalWriteStage.AfterFlush })
        {
            await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
            var (work, _, source) = await RecoverySetup(l, f); work = await StartRecovered(l, f, work, source); fault.Stage = stage;
            await Throws<InjectedFailure>(() => RecoveryNotice(l, work)); await l.DisposeAsync(); l = await f.Open();
            Equal(stage == JournalWriteStage.AfterFlush, (await RecoveryNotice(l, work)).IsDuplicate);
        }
    }
    private static async Task RecoveryCorruption()
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await using var f = new F(); var l = await f.Open(); var (work, _, source) = await RecoverySetup(l, f);
            work = await StartRecovered(l, f, work, source); await RecoveryNotice(l, work); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                var notice = root["events"]!.AsArray().Single(e => e?["dependentWorkNotice"]?["recoveryOfEventId"] != null)!["dependentWorkNotice"]!;
                if (mode == 0) root["schemaVersion"] = 9;
                else if (mode == 1) notice["recoveryOfEventId"] = Guid.NewGuid();
                else notice["attemptId"] = null;
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task RecoveryExpiryAndNewFailure()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Expired(l, f);
        var warning = await Notice(l, work); await AcceptNotice(l, warning);
        work = await Change(l, f, work, "update", deadline: f.Clock.UtcNow.AddHours(1).ToString("O"));
        var source = await Source(l); var lease = (await Evaluate(l, work, Evidence(f, source), true)).Lease!;
        work = await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted after explicit renewal", Target with { TurnId = "renewed-result" });
        var recovered = await RecoveryNotice(l, work); Equal(warning.Event.Id, recovered.Event.DependentWorkNotice!.RecoveryOfEventId);
        await AcceptNotice(l, recovered);
        work = await l.ObserveDependentCompletionAsync(Identity, work.Id, Proof(f, source, work.ResultTask!, CommitmentTaskState.Failed, 2).Observation);
        var failed = await Notice(l, work); Check(failed.Event.Id != warning.Event.Id && failed.Event.Id != recovered.Event.Id);
        Equal(work.Version, failed.Event.DependentWorkNotice!.WorkVersion); Check(failed.Event.DependentWorkNotice.RecoveryOfEventId == null);
        Check((await Notice(l, work)).IsDuplicate);
        await l.DisposeAsync(); l = await f.Open(); Check((await Notice(l, work)).IsDuplicate);
    }
    private static async Task RecoveryLegacyBaseline()
    {
        foreach (bool alreadyCompleted in new[] { false, true })
        {
            await using var f = new F(); var l = await f.Open(); var (work, _, source) = await RecoverySetup(l, f);
            work = await StartRecovered(l, f, work, source);
            if (alreadyCompleted) work = await l.ObserveDependentCompletionAsync(Identity, work.Id,
                Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed, 3).Observation);
            await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                root["schemaVersion"] = 9;
                foreach (var attempt in root["dependentAttempts"]!.AsArray()) attempt!.AsObject().Remove("recoveryOfEventId");
            });
            l = await f.Open();
            Check(DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
            source = await Source(l);
            work = await l.ObserveDependentCompletionAsync(Identity, work.Id,
                Proof(f, source, work.ResultTask!, CommitmentTaskState.Completed).Observation);
            var notice = DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work);
            Equal(alreadyCompleted, notice == null);
            if (!alreadyCompleted) Check((await RecoveryNotice(l, work)).Event.DependentWorkNotice!.RecoveryOfEventId != null);
            await l.DisposeAsync(); l = await f.Open();
            Equal(alreadyCompleted, DurableConversationLedger.DependentNoticeFor(await l.ReadConversationAsync(Identity), work) == null);
        }
    }
}
