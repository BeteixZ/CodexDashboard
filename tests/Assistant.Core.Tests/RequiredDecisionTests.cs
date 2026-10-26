using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static (string, Func<Task>)[] RequiredDecisionCases() =>
    [
        ("required_decision_without_subscription_is_independent", DecisionIndependent),
        ("required_decision_many_subscriptions_share_actual_outbox", DecisionShared),
        ("required_decision_repeat_and_coarse_observation_do_not_write_or_duplicate", DecisionNoOp),
        ("required_decision_accepted_before_subscription_is_reused", DecisionAlreadyAccepted),
        ("required_decision_sending_before_subscription_is_not_duplicated", DecisionAlreadySending),
        ("required_decision_unknown_and_restart_never_replay", DecisionUnknown),
        ("required_decision_exact_reconciliation_completes_shared_runs", DecisionReconciliation),
        ("required_decision_all_actual_segments_must_be_accepted", DecisionPages),
        ("required_decision_cancellation_does_not_cancel_necessary_notice_or_other_run", DecisionCancellation),
        ("required_decision_pause_and_resume_preserve_receipts", DecisionPause),
        ("required_decision_quiet_and_full_quota_cannot_hide_approval", DecisionPolicyBypass),
        ("required_decision_same_key_conflicts_fail_closed", DecisionConflicts),
        ("required_decision_token_epoch_and_target_do_not_alias", DecisionScope),
        ("required_decision_legacy_plain_event_adopts_without_resend", DecisionLegacyNotice),
        ("required_decision_unstarted_short_notice_is_superseded", DecisionOldShort),
        ("required_decision_preparing_short_claim_cannot_finalize_late", DecisionOldPreparing),
        ("required_decision_attempted_short_history_is_not_erased", DecisionOldAttempted),
        ("required_decision_capacity_defers_links_but_keeps_required_outbox", DecisionCapacity),
        ("required_decision_cancelled_deferred_capture_cannot_revive", DecisionDeferredCancel),
        ("required_decision_concurrent_duplicates_have_one_event", DecisionConcurrent),
        ("required_decision_file_before_write_has_no_partial_notice", DecisionBeforeWrite),
        ("required_decision_file_after_flush_recovers_one_notice_and_link", DecisionAfterFlush),
        ("required_decision_corrupt_scope_and_shared_links_fail_closed", DecisionCorruption),
        ("required_decision_creates_no_user_grant_or_approval_context", DecisionDataOnly)
        ,("required_decision_plain_waiting_without_live_token_remains_available", DecisionPlainWaiting)
        ,("required_decision_paused_subscriber_is_not_captured_until_live_resume", DecisionInitiallyPaused)
        ,("required_decision_expired_subscription_does_not_hide_necessary_prompt", DecisionWorkDeadline)
        ,("required_decision_rejected_recovery_is_explicit_and_same_outbox", DecisionRejected)
        ,("required_decision_model_draft_cannot_be_promoted_to_required", DecisionCannotPromoteDraft)
    ];
    private static RequiredDecisionNotice Decision(F f, string token = "token-one", Guid? epoch = null) =>
        new("owned-profile", token, epoch ?? Guid.Parse("cb520e07-d853-407e-9b3f-24d2e4b72e55"), f.Clock.UtcNow.AddHours(1));
    private static Task<DecisionNotificationResult> EnsureDecision(DurableConversationLedger l, F f,
        RequiredDecisionNotice? notice = null, string key = "owned-approval/owned-profile/token-one",
        string body = "请核对这个精确任务的审批，编号 token-one。", string[]? segments = null) =>
        l.EnsureRequiredDecisionNotificationAsync(Identity, key, Target, notice ?? Decision(f), body, segments);
    private static async Task AcceptDecision(DurableConversationLedger l, OutboxMessage output)
    {
        await l.BeginOutboxSendAsync(output.Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(output.Identity, output.Id, OutboxStage.Accepted, "channel accepted only");
    }
    private static async Task DecisionIndependent()
    {
        await using var f = new F(); var l = await f.Open();
        var result = await EnsureDecision(l, f);
        Equal(0, result.Runs.Length); Equal(0, result.DeferredWorkItemIds.Length);
        var snapshot = await l.ReadConversationAsync(Identity);
        Equal(0, snapshot.NotificationDrafts.Length); Equal(1, snapshot.Outbox.Length);
        Check(result.Notification.Event.RequiredDecision != null);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
    }
    private static async Task DecisionShared()
    {
        await using var f = new F(); var l = await f.Open();
        await Subscribe(l, f, "needsDecision"); await Subscribe(l, f, "needsDecision"); await Subscribe(l, f, "failed");
        var result = await EnsureDecision(l, f);
        Equal(2, result.Runs.Length); Equal(1, result.Notification.Outbox.Length);
        Check(result.Runs.All(r => r.SharedDecisionEventId == result.Notification.Event.Id && r.NotificationDraftId == null && r.NotificationEventId == null));
        Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        var snapshot = await l.ReadConversationAsync(Identity);
        Check(snapshot.CommitmentRuns.All(r => r.Stage == CommitmentRunStage.Completed));
        Check(snapshot.WorkItems.All(w => w.Stage == TrackedWorkStage.Active));
        Equal(1, snapshot.Events.Count(e => e.Source == ConversationSource.Assistant));
    }
    private static async Task DecisionNoOp()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var proof = Decision(f); var first = await EnsureDecision(l, f, proof);
        long bytes = new FileInfo(f.Path).Length;
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var again = await EnsureDecision(l, f, proof);
        Equal(first.Notification.Event.Id, again.Notification.Event.Id); Check(again.Notification.IsDuplicate);
        Equal(bytes, new FileInfo(f.Path).Length);
        Equal(0, (await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision)
            with { DecisionId = proof.DecisionToken, LastActivityAt = null })).Length);
        Equal(0, (await l.ReadConversationAsync(Identity)).NotificationDrafts.Length);
    }
    private static async Task DecisionAlreadyAccepted()
    {
        await using var f = new F(); var l = await f.Open(); var result = await EnsureDecision(l, f);
        await AcceptDecision(l, result.Notification.Outbox.Single()); await Subscribe(l, f, "needsDecision");
        var linked = await EnsureDecision(l, f);
        Equal(CommitmentRunStage.Completed, linked.Runs.Single().Stage);
        Equal(result.Notification.Event.Id, linked.Runs.Single().SharedDecisionEventId!.Value);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Single().SendAttempts);
    }
    private static async Task DecisionAlreadySending()
    {
        await using var f = new F(); var l = await f.Open(); var result = await EnsureDecision(l, f);
        await l.BeginOutboxSendAsync(Identity, result.Notification.Outbox.Single().Id);
        await Subscribe(l, f, "needsDecision"); var linked = await EnsureDecision(l, f);
        Equal(CommitmentRunStage.NotificationPending, linked.Runs.Single().Stage);
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
        await l.RecordOutboxOutcomeAsync(Identity, result.Notification.Outbox.Single().Id, OutboxStage.Accepted, "accepted");
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
    private static async Task DecisionUnknown()
    {
        await using var f = new F(); var l = await f.Open(); var proof = Decision(f);
        await Subscribe(l, f, "needsDecision"); var result = await EnsureDecision(l, f, proof);
        await l.BeginOutboxSendAsync(Identity, result.Notification.Outbox.Single().Id);
        await l.DisposeAsync(); l = await f.Open();
        await Subscribe(l, f, "needsDecision"); var linked = await EnsureDecision(l, f, proof);
        Equal(2, linked.Runs.Length); Check(linked.Runs.All(r => r.Stage == CommitmentRunStage.NotificationPending));
        Equal(OutboxStage.Unknown, linked.Notification.Outbox.Single().Stage);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginOutboxSendAsync(Identity, result.Notification.Outbox.Single().Id));
    }
    private static async Task DecisionReconciliation()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var output = (await EnsureDecision(l, f)).Notification.Outbox.Single();
        await l.BeginOutboxSendAsync(Identity, output.Id); await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Unknown, "lost receipt");
        await Error(LedgerErrors.InvalidTransition, () => l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "not reconciled"));
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Accepted, "exact receipt recovered", reconciliation: true);
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
    private static async Task DecisionPages()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var result = await EnsureDecision(l, f, segments: ["审批详情", "审批完整编号"]);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginOutboxSendAsync(Identity, result.Notification.Outbox[1].Id));
        await AcceptDecision(l, result.Notification.Outbox[0]);
        Equal(CommitmentRunStage.NotificationPending, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        await AcceptDecision(l, result.Notification.Outbox[1]);
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
        Equal(2, (await l.ReadConversationAsync(Identity)).Events.Count(x => x.Source == ConversationSource.Assistant));
    }
    private static async Task DecisionCancellation()
    {
        await using var f = new F(); var l = await f.Open(); var a = await Subscribe(l, f, "needsDecision");
        var b = await Subscribe(l, f, "needsDecision"); var result = await EnsureDecision(l, f);
        await l.BeginOutboxSendAsync(Identity, result.Notification.Outbox.Single().Id); await Change(l, f, a, "cancel");
        await l.RecordOutboxOutcomeAsync(Identity, result.Notification.Outbox.Single().Id, OutboxStage.Accepted, "accepted while subscription cancelled");
        var snapshot = await l.GetCommitmentsAsync(Identity);
        Equal(CommitmentRunStage.Cancelled, snapshot.Runs.Single(x => x.WorkItemId == a.Id).Stage);
        Equal(CommitmentRunStage.Completed, snapshot.Runs.Single(x => x.WorkItemId == b.Id).Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task DecisionPause()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "needsDecision");
        var result = await EnsureDecision(l, f); work = await Change(l, f, work, "pause");
        await AcceptDecision(l, result.Notification.Outbox.Single());
        Equal(TrackedWorkStage.Paused, (await l.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage);
        work = await Change(l, f, work, "resume");
        Equal(TrackedWorkStage.Active, work.Stage);
        Equal(CommitmentRunStage.Completed, (await EnsureDecision(l, f)).Runs.Single().Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
    }
    private static async Task DecisionPolicyBypass()
    {
        await using var f = new F(); var l = await f.Open(options: Limits(perDay: 1, perMinute: 1));
        var spent = (await PendingReminders(l, f, 1)).Single(); await AcceptDecision(l, spent);
        var policy = "{\"quietStart\":\"00:00\",\"quietEnd\":\"23:59\",\"timeZoneId\":\"UTC\",\"maximumDeliveryAttempts\":1}";
        await Subscribe(l, f, "needsDecision", policy); var result = await EnsureDecision(l, f);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        Equal(1, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single(x => x.SharedDecisionEventId != null).Stage);
    }
    private static async Task DecisionConflicts()
    {
        await using var f = new F(); var l = await f.Open(); var proof = Decision(f); await EnsureDecision(l, f, proof);
        await Error(LedgerErrors.Conflict, () => EnsureDecision(l, f, proof, body: "different body"));
        await Error(LedgerErrors.Conflict, () => EnsureDecision(l, f, proof with { ConnectionEpoch = Guid.NewGuid() }));
        await Error(LedgerErrors.Conflict, () => EnsureDecision(l, f, proof, key: "different-key"));
        await Error(LedgerErrors.Conflict, () => l.EnsureRequiredDecisionNotificationAsync(Identity,
            "owned-approval/owned-profile/token-one", Target with { TurnId = "different" }, proof, "请核对这个精确任务的审批，编号 token-one。"));
        await Error(LedgerErrors.InvalidTransition, () => EnsureDecision(l, f, proof with { ExpiresAt = f.Clock.UtcNow }));
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
    }
    private static async Task DecisionScope()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var first = await EnsureDecision(l, f); var second = await EnsureDecision(l, f, Decision(f, epoch: Guid.NewGuid()), key: "different-epoch");
        Check(first.Notification.Event.Id != second.Notification.Event.Id); Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        var other = await l.EnsureRequiredDecisionNotificationAsync(Identity, "other-target", Target with { TurnId = "other" }, Decision(f), "other task");
        Equal(0, other.Runs.Length);
        var binding = await l.EnsureRequiredDecisionNotificationAsync(Identity with { BindingId = "other" }, "same-key", Target, Decision(f), "other binding");
        Equal(0, binding.Runs.Length);
        await l.DisposeAsync(); l = await f.Open();
        Equal(2, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task DecisionLegacyNotice()
    {
        foreach (var outcome in new[] { OutboxStage.Accepted, OutboxStage.Unknown })
        {
            await using var f = new F(); var l = await f.Open();
            const string body = "请核对这个精确任务的审批，编号 token-one。";
            var legacy = await l.EnqueueNotificationAsync(Identity, "legacy-approval-fixture", body, Target);
            await l.BeginOutboxSendAsync(Identity, legacy.Outbox.Single().Id);
            await l.RecordOutboxOutcomeAsync(Identity, legacy.Outbox.Single().Id, outcome, "old exact receipt");
            // Produce the persisted v1 host fact; the new runtime itself must not send an unmarked reserved approval key.
            await l.DisposeAsync();
            await f.Rewrite(root => root["events"]!.AsArray().Single(x => x?["externalEventKey"]?.GetValue<string>() == "legacy-approval-fixture")!
                ["externalEventKey"] = "owned-approval/owned-profile/token-one");
            l = await f.Open();
            await Subscribe(l, f, "needsDecision"); var adopted = await EnsureDecision(l, f);
            Equal(legacy.Event.Id, adopted.Notification.Event.Id); Equal(legacy.Outbox.Single().Id, adopted.Notification.Outbox.Single().Id);
            Equal(outcome, adopted.Notification.Outbox.Single().Stage);
            await l.DisposeAsync(); l = await f.Open(); Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
        }
    }
    private static async Task DecisionOldShort()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "token-one" });
        var old = await Finalize(l); var result = await EnsureDecision(l, f);
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length); Check(result.Runs.Single().NotificationDraftId != null);
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == old.Id).Stage);
        Equal(result.Notification.Event.Id, result.Runs.Single().SharedDecisionEventId!.Value);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        await l.DisposeAsync(); l = await f.Open();
        Equal(CommitmentRunStage.Completed, (await l.GetCommitmentsAsync(Identity)).Runs.Single().Stage);
    }
    private static async Task DecisionOldPreparing()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "token-one" });
        var draft = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single();
        var claim = await l.BeginNotificationPreparationAsync(Identity, draft.Id); await EnsureDecision(l, f);
        await Error(LedgerErrors.InvalidTransition, () => l.FinalizeNotificationAsync(claim, "late short text"));
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
        await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task DecisionOldAttempted()
    {
        foreach (var outcome in new[] { OutboxStage.Sending, OutboxStage.Unknown, OutboxStage.Accepted, OutboxStage.Rejected })
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
            await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision) with { DecisionId = "token-one" });
            var old = await Finalize(l); await l.BeginOutboxSendAsync(Identity, old.Id);
            if (outcome != OutboxStage.Sending) await l.RecordOutboxOutcomeAsync(Identity, old.Id, outcome, "historical send attempt");
            var result = await EnsureDecision(l, f); Check(result.Runs.Single().SharedDecisionEventId == null);
            Equal(outcome, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == old.Id).Stage);
            Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
            Equal(2, (await l.ReadConversationAsync(Identity)).Outbox.Length);
        }
    }
    private static async Task DecisionCapacity()
    {
        await using var f = new F(); var l = await f.Open(options: new() { MaximumCommitmentRuns = 1 });
        await Subscribe(l, f, "needsDecision"); await Subscribe(l, f, "needsDecision");
        var proof = Decision(f); var result = await EnsureDecision(l, f, proof);
        Equal(1, result.Runs.Length); Equal(1, result.DeferredWorkItemIds.Length);
        Equal(2, result.Notification.Event.DecisionSubscribers!.Value.Length);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        await l.DisposeAsync(); l = await f.Open(options: new() { MaximumCommitmentRuns = 2 });
        var snapshot = await l.GetCommitmentsAsync(Identity);
        Equal(2, snapshot.Runs.Length); Check(snapshot.Runs.All(x => x.Stage == CommitmentRunStage.Completed));
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length);
    }
    private static async Task DecisionDeferredCancel()
    {
        await using var f = new F(); var l = await f.Open(options: new() { MaximumCommitmentRuns = 1 });
        await Subscribe(l, f, "needsDecision"); await Subscribe(l, f, "needsDecision");
        var result = await EnsureDecision(l, f); var snapshot = await l.GetCommitmentsAsync(Identity);
        var deferred = snapshot.WorkItems.Single(x => x.Id == result.DeferredWorkItemIds.Single());
        await Change(l, f, deferred, "cancel"); await l.DisposeAsync(); l = await f.Open(options: new() { MaximumCommitmentRuns = 2 });
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task DecisionConcurrent()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => EnsureDecision(l, f)));
        Equal(1, results.Count(x => !x.Notification.IsDuplicate)); Equal(1, results.Select(x => x.Notification.Event.Id).Distinct().Count());
        Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
    }
    private static async Task DecisionBeforeWrite()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
        await Subscribe(l, f, "needsDecision"); fault.Stage = JournalWriteStage.BeforeWrite;
        await Throws<InjectedFailure>(() => EnsureDecision(l, f)); await l.DisposeAsync(); l = await f.Open();
        Equal(0, (await l.ReadConversationAsync(Identity)).Outbox.Length); Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        Equal(1, (await EnsureDecision(l, f)).Notification.Outbox.Length);
    }
    private static async Task DecisionAfterFlush()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault);
        await Subscribe(l, f, "needsDecision"); fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => EnsureDecision(l, f)); await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length); Equal(1, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        var again = await EnsureDecision(l, f); Check(again.Notification.IsDuplicate);
        Equal(1, again.Notification.Outbox.Length);
    }
    private static async Task DecisionCorruption()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["commitmentRuns"]![0]!["sharedDecisionEventId"] = Guid.NewGuid().ToString(),
            root => root["events"]!.AsArray().Single(x => x!["requiredDecision"] != null)!["requiredDecision"]!["connectionEpoch"] = Guid.Empty.ToString(),
            root => root["events"]!.AsArray().Single(x => x!["requiredDecision"] != null)!["decisionSubscribers"] = null,
            root => root["events"]!.AsArray().Single(x => x!["requiredDecision"] != null)!["decisionSubscribers"]![0] = null,
            root => root["events"]!.AsArray().Single(x => x!["requiredDecision"] != null)!["decisionSubscribers"]![0]!["workItemId"] = Guid.NewGuid().ToString(),
            root => root["events"]!.AsArray().Single(x => x!["requiredDecision"] != null)!["task"]!["turnId"] = "other",
            root => root["commitmentRuns"]![0]!["stage"] = "completed"
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision"); await EnsureDecision(l, f);
            await l.DisposeAsync(); await f.Rewrite(mutate);
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task DecisionDataOnly()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var before = await l.ReadConversationAsync(Identity); var result = await EnsureDecision(l, f);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        var after = await l.ReadConversationAsync(Identity);
        Equal(before.Authorizations.Length, after.Authorizations.Length); Equal(before.Operations.Length, after.Operations.Length);
        Equal(before.Inbox.Length, after.Inbox.Length); Equal(before.Generations.Length, after.Generations.Length);
        Check(after.Context!.PendingApproval == null);
    }
    private static async Task DecisionPlainWaiting()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        await EnsureDecision(l, f);
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.Running));
        f.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var generic = await l.ObserveCommitmentTaskAsync(Identity, Observe(f, CommitmentTaskState.NeedsDecision));
        Equal(1, generic.Length); Equal("decision/waiting", generic.Single().TriggerKey);
        Check(generic.Single().SharedDecisionEventId == null);
        Equal(1, (await l.GetPendingCommitmentNotificationsAsync(Identity)).Length);
    }
    private static async Task DecisionInitiallyPaused()
    {
        await using var f = new F(); var l = await f.Open(); var work = await Subscribe(l, f, "needsDecision");
        work = await Change(l, f, work, "pause"); var result = await EnsureDecision(l, f);
        Equal(0, result.Runs.Length); Equal(0, result.Notification.Event.DecisionSubscribers!.Value.Length);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        await Change(l, f, work, "resume");
        Equal(0, (await l.GetCommitmentsAsync(Identity)).Runs.Length);
        Equal(CommitmentRunStage.Completed, (await EnsureDecision(l, f)).Runs.Single().Stage);
    }
    private static async Task DecisionWorkDeadline()
    {
        await using var f = new F(); var l = await f.Open();
        await Execute(l, f, "subscribe", $$"""{"hostId":"local","threadId":"thread-commitment","turnId":"turn-one","profileId":"profile","condition":"needsDecision","deadlineUtc":"{{f.Clock.UtcNow.AddMinutes(1):O}}"}""",
            "订阅这个任务轮次", Target);
        var result = await EnsureDecision(l, f); f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await l.AdvanceCommitmentsAsync(Identity);
        await AcceptDecision(l, result.Notification.Outbox.Single());
        var snapshot = await l.GetCommitmentsAsync(Identity);
        Equal(TrackedWorkStage.Expired, snapshot.WorkItems.Single().Stage);
        Equal(CommitmentRunStage.Cancelled, snapshot.Runs.Single().Stage);
        await l.DisposeAsync(); l = await f.Open();
        Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
    }
    private static async Task DecisionRejected()
    {
        await using var f = new F(); var l = await f.Open(); await Subscribe(l, f, "needsDecision");
        var output = (await EnsureDecision(l, f)).Notification.Outbox.Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context token missing, not accepted");
        await Subscribe(l, f, "needsDecision"); await EnsureDecision(l, f);
        Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        Equal(output.Id, (await l.RetryRejectedNotificationsAsync(Identity)).Single().Id);
        await AcceptDecision(l, output);
        Check((await l.GetCommitmentsAsync(Identity)).Runs.All(x => x.Stage == CommitmentRunStage.Completed));
        Equal(0, (await l.ReadConversationAsync(Identity)).CommitmentBudgetReservations.Length);
    }
    private static async Task DecisionCannotPromoteDraft()
    {
        await using var f = new F(); var l = await f.Open();
        const string key = "owned-approval/owned-profile/token-one";
        const string body = "请核对这个精确任务的审批，编号 token-one。";
        var draft = await l.CaptureNotificationAsync(Identity, key, body, "{}", Target);
        await l.FinalizeNotificationFallbackAsync(Identity, draft.Draft.Id, body);
        await Error(LedgerErrors.Conflict, () => EnsureDecision(l, f));
        Check((await l.ReadConversationAsync(Identity)).Events.Single(x => x.ExternalEventKey == key).RequiredDecision == null);
    }
}
