using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class CommitmentTests
{
    private static readonly Guid WakeRoute = Guid.Parse("5df7bc8c-2ba3-4806-aef7-ae5ca4c7d63c");
    private static (string, Func<Task>)[] WakeCases() =>
    [
        ("wake_only_structured_context_rejection_qualifies", WakeReasons),
        ("wake_notification_only_not_conversation_reply", WakeNotificationOnly),
        ("wake_aggregate_preserves_each_body_and_no_authority", WakeAggregate),
        ("wake_duplicate_settings_and_capture_do_not_write", WakeNoOp),
        ("wake_new_trusted_input_cancels_pending_atomically", WakeNewInput),
        ("wake_duplicate_untrusted_and_context_do_not_end_episode", WakeUntrusted),
        ("wake_late_old_rejection_cannot_create_new_episode", WakeLateRejection),
        ("wake_gate_cancel_before_begin_spends_nothing", WakeGateCancel),
        ("wake_begin_revalidates_explicit_retry_and_current_source", WakeSourceChanged),
        ("wake_no_remaining_source_cancels_pending_without_rearming", WakeNoSource),
        ("wake_pending_restart_restores_exact_episode", WakePendingRestart),
        ("wake_sending_restart_unknown_never_replays_across_routes", WakeUnknownRestart),
        ("wake_accepted_requires_actual_receipt_not_phone_delivery", WakeReceipt),
        ("wake_unknown_keeps_real_occupancy_until_finally", WakeDrain),
        ("wake_send_started_before_new_input_keeps_real_result", WakeNewInputDuringSend),
        ("wake_global_hour_day_and_inflight_budget_across_identities", WakeGlobalBudget),
        ("wake_budget_denial_does_not_consume_attempt", WakeBudgetPending),
        ("wake_backward_clock_does_not_refund_budget", WakeClockBack),
        ("wake_global_quiet_rechecked_after_gate", WakeQuiet),
        ("wake_source_quiet_and_pause_cancel_are_respected", WakeSourcePolicy),
        ("wake_disabled_and_route_change_cancel_pending_without_rearm", WakeRouteChange),
        ("wake_cross_identity_and_forged_attempt_rejected", WakeLeaseScope),
        ("wake_definite_rejection_and_notstarted_are_terminal", WakeTerminal),
        ("wake_unknown_requires_explicit_reconciliation", WakeReconcile),
        ("wake_concurrent_ensure_and_begin_reserve_once", WakeConcurrent),
        ("wake_capacity_preserves_original_claw_rejection", WakeCapacity),
        ("wake_v1_missing_fields_remain_readable_and_ineligible", WakeLegacy),
        ("wake_corrupt_source_claim_episode_and_receipt_fail_closed", WakeCorruption),
        ("wake_file_prewrite_begin_failure_cannot_start_external_send", WakeBeforeWrite),
        ("wake_file_postflush_begin_recovers_unknown_and_charge", WakeAfterFlush),
        ("wake_file_postflush_receive_keeps_cancellation", WakeReceiveFlush),
        ("wake_missed_reminder_explains_cancel_then_recreate", WakeMisfireText)
        ,("wake_expired_approval_cannot_create_a_new_hint", WakeExpiredApproval)
        ,("wake_approval_expiry_cancels_pending_hint", WakePendingApprovalExpiry)
        ,("wake_expired_approval_blocks_new_claw_send_and_rejected_retry", WakeExpiredApprovalOutbox)
        ,("wake_approval_expiry_keeps_started_acceptance_and_unknown_facts", WakeExpiredApprovalHistory)
    ];
    private static Task ConfigureWake(DurableConversationLedger l, ConversationIdentity? identity = null, Guid? route = null,
        string? start = null, string? end = null) => l.ConfigureWakeChannelAsync(identity ?? Identity,
        new(route ?? WakeRoute, true, "UTC", start, end));
    private static async Task<OutboxMessage> RejectedNotice(DurableConversationLedger l, F f,
        OutboxRejectionReason reason = OutboxRejectionReason.ContextUnavailable, ConversationIdentity? identity = null,
        string? key = null, OutboxStage outcome = OutboxStage.Rejected)
    {
        var scope = identity ?? Identity;
        var output = (await l.EnqueueNotificationAsync(scope, key ?? Guid.NewGuid().ToString("N"), "原始正文，不改写。", Target)).Outbox.Single();
        await l.BeginOutboxSendAsync(scope, output.Id);
        return await l.RecordOutboxOutcomeAsync(scope, output.Id, outcome, "structured test result", rejectionReason: reason);
    }
    private static async Task<ChannelWakeRecord> PendingWake(DurableConversationLedger l, F f,
        ConversationIdentity? identity = null, bool receive = true)
    {
        var scope = identity ?? Identity;
        await ConfigureWake(l, scope);
        if (receive) await l.ReceiveAsync(scope, Guid.NewGuid().ToString("N"), "这是当前已认证输入", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await RejectedNotice(l, f, identity: scope);
        var wake = (await l.EnsurePendingWakeAsync(scope, WakeRoute))!;
        f.Clock.UtcNow = wake.NotBefore;
        return wake;
    }
    private static async Task WakeReasons()
    {
        foreach (var reason in Enum.GetValues<OutboxRejectionReason>())
        {
            await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
            await RejectedNotice(l, f, reason);
            Equal(reason is OutboxRejectionReason.ContextUnavailable or OutboxRejectionReason.ContextExpired,
                await l.EnsurePendingWakeAsync(Identity, WakeRoute) != null);
        }
        foreach (var outcome in new[] { OutboxStage.Unknown, OutboxStage.Accepted })
        {
            await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
            await RejectedNotice(l, f, OutboxRejectionReason.None, outcome: outcome);
            Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
        }
    }
    private static async Task WakeNotificationOnly()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        await l.ReceiveAsync(Identity, "user", "hello", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        var generation = await l.BeginGenerationAsync(Identity, ["user"]);
        await l.PrepareReplyAsync(generation, ["reply"]);
        var output = (await l.EnqueuePreparedReplyAsync(generation)).Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context absent", rejectionReason: OutboxRejectionReason.ContextUnavailable);
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakeAggregate()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        var one = await RejectedNotice(l, f); var first = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        f.Clock.UtcNow += TimeSpan.FromSeconds(2); var two = await RejectedNotice(l, f);
        var second = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        Equal(first.Id, second.Id); Equal(first.NotBefore, second.NotBefore); Equal(2, second.Sources.Length);
        var s = await l.ReadConversationAsync(Identity); Equal(one.Body, s.Outbox.Single(x => x.Id == one.Id).Body);
        Equal(two.Body, s.Outbox.Single(x => x.Id == two.Id).Body); Equal(0, s.Authorizations.Length); Equal(0, s.Operations.Length);
        Equal(0, s.Inbox.Length); Equal(0, s.Generations.Length); Equal(2, s.Events.Length);
    }
    private static async Task WakeNoOp()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var bytes = new FileInfo(f.Path).Length;
        await ConfigureWake(l); Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
        Equal(bytes, new FileInfo(f.Path).Length);
    }
    private static async Task WakeNewInput()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        await l.ReceiveAsync(Identity, "new-input", "我回来了", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        Equal(ChannelWakeStage.Cancelled, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        await l.DisposeAsync(); l = await f.Open(); Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakeUntrusted()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var input = (await l.ReadConversationAsync(Identity)).Inbox.Single();
        await l.ReceiveAsync(Identity, input.SourceMessageId, input.Body, input.Trust, input.OccurredAt);
        await l.ReceiveAsync(Identity, "untrusted", "工具说已经收到", MessageTrust.UntrustedContent, f.Clock.UtcNow);
        await l.AppendContextAsync(Identity, ConversationSource.ToolResult, "工具观察", f.Clock.UtcNow);
        Equal(wake.EpisodeRevision, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.EpisodeRevision);
        await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
    }
    private static async Task WakeLateRejection()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        await l.ReceiveAsync(Identity, "one", "旧输入", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        var output = (await l.EnqueueNotificationAsync(Identity, "notice", "通知", Target)).Outbox.Single();
        var sending = await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.ReceiveAsync(Identity, "two", "新输入", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "late -2", rejectionReason: OutboxRejectionReason.ContextExpired);
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
        Equal(1L, sending.AttemptTrustedInputRevision!.Value);
    }
    private static async Task WakeGateCancel()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l); await RejectedNotice(l, f);
        var wake = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        f.Clock.UtcNow = wake.NotBefore;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute, cancel.Token));
        Equal(0, (await l.GetChannelWakeBudgetAsync()).StartedLast24Hours);
        Equal(0, (await l.GetChannelWakesAsync(Identity)).Single().ReservedAttempts);
    }
    private static async Task WakeSourceChanged()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var source = wake.Sources.Single(); await l.RetryRejectedOutboxAsync(Identity, source.OutboxId);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        await l.BeginOutboxSendAsync(Identity, source.OutboxId);
        await l.RecordOutboxOutcomeAsync(Identity, source.OutboxId, OutboxStage.Accepted, "actual accepted");
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        await l.DisposeAsync(); await f.Open();
    }
    private static async Task WakePendingRestart()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        await l.DisposeAsync(); l = await f.Open(); await ConfigureWake(l);
        var restored = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        Equal(wake.Id, restored.Id); Equal(ChannelWakeStage.Pending, restored.Stage); Equal(wake.NotBefore, restored.NotBefore);
        await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
    }
    private static async Task WakeNoSource()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var source = wake.Sources.Single(); await l.RetryRejectedOutboxAsync(Identity, source.OutboxId);
        await l.BeginOutboxSendAsync(Identity, source.OutboxId);
        await l.RecordOutboxOutcomeAsync(Identity, source.OutboxId, OutboxStage.Accepted, "report now accepted");
        var cancelled = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        Equal(ChannelWakeStage.Cancelled, cancelled.Stage); Equal(wake.Id, cancelled.Id);
        await RejectedNotice(l, f); Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
        Equal(ChannelWakeStage.Cancelled, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
    }
    private static async Task WakeUnknownRestart()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); await l.DisposeAsync(); l = await f.Open();
        var route = Guid.NewGuid(); await ConfigureWake(l, route: route);
        var restored = (await l.EnsurePendingWakeAsync(Identity, route))!;
        Equal(wake.Id, restored.Id); Equal(ChannelWakeStage.Unknown, restored.Stage); Check(restored.ReleasedAt != null);
        Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
        await l.ConfigureWakeChannelAsync(Identity, new(Guid.Empty, false, "UTC")); await ConfigureWake(l);
        Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
    }
    private static async Task WakeReceipt()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
        await Throws<ArgumentException>(() => l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Accepted, "accepted"));
        await l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Accepted, "API accepted only", "receipt-123");
        Equal(ChannelWakeStage.Accepted, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Equal(OutboxStage.Rejected, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
        Equal(0, (await l.ReadConversationAsync(Identity)).Events.Count(x => x.Source == ConversationSource.Assistant));
        Equal(1, (await l.GetChannelWakeBudgetAsync()).InFlight); await l.ReleaseWakeSendAsync(lease);
        Equal(0, (await l.GetChannelWakeBudgetAsync()).InFlight); await l.DisposeAsync(); await f.Open();
    }
    private static async Task WakeDrain()
    {
        await using var f = new F(); var l = await f.Open(); var first = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, first.Id, WakeRoute);
        await l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Unknown, "outer wait ended but actual request still running");
        f.Clock.UtcNow += TimeSpan.FromHours(2);
        var other = Identity with { BindingId = "other" }; var second = await PendingWake(l, f, other);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginWakeSendAsync(other, second.Id, WakeRoute));
        Equal(1, (await l.GetChannelWakeBudgetAsync()).InFlight);
        Check(await l.ReleaseWakeSendAsync(lease)); Check(!await l.ReleaseWakeSendAsync(lease));
        await l.BeginWakeSendAsync(other, second.Id, WakeRoute);
        Equal(2, (await l.GetChannelWakeBudgetAsync()).StartedLast24Hours);
    }
    private static async Task WakeNewInputDuringSend()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
        await l.ReceiveAsync(Identity, "new", "新消息", MessageTrust.AuthenticatedUser, f.Clock.UtcNow);
        Equal(ChannelWakeStage.Sending, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        await l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Accepted, "accepted after inbound", "receipt");
        await l.ReleaseWakeSendAsync(lease); Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakeGlobalBudget()
    {
        await using var f = new F(); var l = await f.Open();
        for (int i = 0; i < 3; i++)
        {
            var identity = Identity with { BindingId = "global-" + i }; var wake = await PendingWake(l, f, identity);
            var lease = await l.BeginWakeSendAsync(identity, wake.Id, WakeRoute);
            await l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Unknown, "unknown still consumes quota"); await l.ReleaseWakeSendAsync(lease);
            f.Clock.UtcNow += TimeSpan.FromHours(1);
        }
        var final = await PendingWake(l, f); await Error(LedgerErrors.BudgetDeferred, () => l.BeginWakeSendAsync(Identity, final.Id, WakeRoute));
        var budget = await l.GetChannelWakeBudgetAsync(); Equal(3, budget.StartedLast24Hours); Check(budget.NextWindowAt != null);
        f.Clock.UtcNow = budget.NextWindowAt!.Value; await l.BeginWakeSendAsync(Identity, final.Id, WakeRoute);
    }
    private static async Task WakeBudgetPending()
    {
        await using var f = new F(); var l = await f.Open(); var first = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, first.Id, WakeRoute); await l.ReleaseWakeSendAsync(lease);
        var second = await PendingWake(l, f);
        await Error(LedgerErrors.BudgetDeferred, () => l.BeginWakeSendAsync(Identity, second.Id, WakeRoute));
        Equal(0, (await l.GetChannelWakesAsync(Identity)).Single(x => x.Id == second.Id).ReservedAttempts);
        f.Clock.UtcNow += TimeSpan.FromHours(1); await l.BeginWakeSendAsync(Identity, second.Id, WakeRoute);
    }
    private static async Task WakeClockBack()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); await l.ReleaseWakeSendAsync(lease);
        f.Clock.UtcNow -= TimeSpan.FromDays(2); Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
        Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
    }
    private static async Task WakeQuiet()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var now = f.Clock.UtcNow; await ConfigureWake(l, start: now.AddMinutes(-1).ToString("HH:mm"), end: now.AddMinutes(1).ToString("HH:mm"));
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        Equal(0, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
        f.Clock.UtcNow += TimeSpan.FromMinutes(2); await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
    }
    private static async Task WakeSourcePolicy()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        var start = f.Clock.UtcNow.AddMinutes(4).ToString("HH:mm"); var end = f.Clock.UtcNow.AddMinutes(20).ToString("HH:mm");
        var work = await Reminder(l, f, $$"""{"quietStart":"{{start}}","quietEnd":"{{end}}","timeZoneId":"UTC","maximumDeliveryAttempts":1}""");
        f.Clock.UtcNow += TimeSpan.FromMinutes(3); await l.AdvanceCommitmentsAsync(Identity); var output = await Finalize(l);
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context absent", rejectionReason: OutboxRejectionReason.ContextUnavailable);
        var wake = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        f.Clock.UtcNow += TimeSpan.FromMinutes(2);
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        f.Clock.UtcNow += TimeSpan.FromMinutes(20);
        // Claw attempt budget is full; the distinct short-wake budget still permits one truthful hint.
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); await l.ReleaseWakeSendAsync(lease);
        work = await Change(l, f, work, "pause"); await RejectedNotice(l, f);
        var generic = await l.EnsurePendingWakeAsync(Identity, WakeRoute); Check(generic != null);
        Check(!generic!.Sources.Any(x => x.OutboxId == output.Id));
    }
    private static async Task WakeRouteChange()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f); var route = Guid.NewGuid();
        await ConfigureWake(l, route: route); var existing = (await l.EnsurePendingWakeAsync(Identity, route))!;
        Equal(wake.Id, existing.Id); Equal(ChannelWakeStage.Cancelled, existing.Stage);
        await l.ConfigureWakeChannelAsync(Identity, new(Guid.Empty, false, "UTC"));
        await ConfigureWake(l); Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
        await Throws<ArgumentException>(() => l.ConfigureWakeChannelAsync(Identity, new(Guid.Empty, true, "UTC")));
    }
    private static async Task WakeLeaseScope()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        await Error(LedgerErrors.NotFound, () => l.BeginWakeSendAsync(Identity with { BindingId = "other" }, wake.Id, WakeRoute));
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
        await Error(LedgerErrors.IdentityMismatch, () => l.ReleaseWakeSendAsync(lease with { ClaimId = Guid.NewGuid() }));
        await Error(LedgerErrors.IdentityMismatch, () => l.RecordWakeOutcomeAsync(lease with { EpisodeRevision = lease.EpisodeRevision + 1 }, WakeDeliveryOutcome.Unknown, "forged"));
        await Error(LedgerErrors.IdentityMismatch, () => l.ReleaseWakeSendAsync(lease with { RouteId = Guid.NewGuid() }));
    }
    private static async Task WakeTerminal()
    {
        foreach (var outcome in new[] { WakeDeliveryOutcome.NotStarted, WakeDeliveryOutcome.Rejected })
        {
            await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
            var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute);
            await l.RecordWakeOutcomeAsync(lease, outcome, "actual result"); await l.ReleaseWakeSendAsync(lease);
            await Error(LedgerErrors.InvalidTransition, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
            Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
            await l.DisposeAsync(); l = await f.Open(); Equal(wake.Id, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Id);
        }
    }
    private static async Task WakeReconcile()
    {
        await using var f = new F(); var l = await f.Open(); var wake = await PendingWake(l, f);
        var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); await l.ReleaseWakeSendAsync(lease);
        await Error(LedgerErrors.InvalidTransition, () => l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Accepted, "late", "exact"));
        await l.RecordWakeOutcomeAsync(lease, WakeDeliveryOutcome.Accepted, "exact reconciliation", "exact", reconciliation: true);
        Equal(ChannelWakeStage.Accepted, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLast24Hours);
    }
    private static async Task WakeConcurrent()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l); await RejectedNotice(l, f);
        var wakes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => l.EnsurePendingWakeAsync(Identity, WakeRoute)));
        Equal(1, wakes.Select(x => x!.Id).Distinct().Count()); var wake = wakes[0]!; f.Clock.UtcNow = wake.NotBefore;
        async Task<bool> Attempt() { try { await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); return true; } catch (LedgerException) { return false; } }
        Equal(1, (await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Attempt()))).Count(x => x));
        Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
    }
    private static async Task WakeCapacity()
    {
        await using var f = new F(); var l = await f.Open(options: new() { MaximumChannelWakes = 1 }); await PendingWake(l, f);
        await l.ReceiveAsync(Identity, "new", "new", MessageTrust.AuthenticatedUser, f.Clock.UtcNow); var output = await RejectedNotice(l, f);
        await Error(LedgerErrors.CapacityExceeded, () => l.EnsurePendingWakeAsync(Identity, WakeRoute));
        Equal(OutboxStage.Rejected, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == output.Id).Stage);
    }
    private static async Task WakeLegacy()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l); await RejectedNotice(l, f);
        await l.DisposeAsync(); await f.Rewrite(root =>
        {
            root.AsObject().Remove("channelWakes"); root.AsObject().Remove("wakeChannels");
            foreach (var row in root["outbox"]!.AsArray()) { row!.AsObject().Remove("attemptTrustedInputRevision"); row.AsObject().Remove("rejectionReason"); }
        });
        l = await f.Open(); await ConfigureWake(l); Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakeCorruption()
    {
        Action<JsonNode>[] mutations =
        [
            r => r["channelWakes"] = null,
            r => r.AsObject().Remove("wakeChannels"),
            r => r["wakeChannels"]![0]!["configuration"]!["enabled"] = true,
            r => r["channelWakes"]![0]!["sources"]![0]!["outboxId"] = Guid.NewGuid().ToString(),
            r => r["channelWakes"]![0]!["sources"]![0]!["sendAttempt"] = 9,
            r => r["channelWakes"]![0]!["sources"]![0]!["trustedInputRevision"] = 99,
            r => r["channelWakes"]![0]!["identity"]!["bindingId"] = "other",
            r => r["channelWakes"]![0]!["episodeRevision"] = 99,
            r => r["channelWakes"]![0]!["claimId"] = Guid.NewGuid().ToString(),
            r => r["channelWakes"]![0]!["platformReceipt"] = "unproven",
            r => r["outbox"]![0]!["rejectionReason"] = "bindingUnavailable",
            r => r["outbox"]![0]!["attemptTrustedInputRevision"] = 99
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new F(); var l = await f.Open(); await PendingWake(l, f);
            await l.DisposeAsync(); await f.Rewrite(r => { mutate(r); if (mutate == mutations[2]) r["wakeChannels"]![0]!["configuration"]!["routeId"] = Guid.Empty.ToString(); });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task WakeBeforeWrite()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); var wake = await PendingWake(l, f);
        fault.Stage = JournalWriteStage.BeforeWrite; await Throws<InjectedFailure>(() => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        await l.DisposeAsync(); l = await f.Open(); Equal(ChannelWakeStage.Pending, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Equal(0, (await l.GetChannelWakeBudgetAsync()).StartedLastHour);
    }
    private static async Task WakeAfterFlush()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); var wake = await PendingWake(l, f);
        fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        await l.DisposeAsync(); l = await f.Open(); Equal(ChannelWakeStage.Unknown, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Equal(1, (await l.GetChannelWakeBudgetAsync()).StartedLastHour); Equal(0, (await l.GetChannelWakeBudgetAsync()).InFlight);
    }
    private static async Task WakeReceiveFlush()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault); await PendingWake(l, f);
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.ReceiveAsync(Identity, "new", "new", MessageTrust.AuthenticatedUser, f.Clock.UtcNow));
        await l.DisposeAsync(); l = await f.Open(); Equal(ChannelWakeStage.Cancelled, (await l.GetChannelWakesAsync(Identity)).Single().Stage);
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakeMisfireText()
    {
        await using var f = new F(); var l = await f.Open();
        var work = await Execute(l, f, "schedule", ReminderArgs(f.Clock, missed: "needsDecision"), "提醒我喝水");
        f.Clock.UtcNow += TimeSpan.FromDays(1); await l.AdvanceCommitmentsAsync(Identity);
        var body = (await l.GetPendingCommitmentNotificationsAsync(Identity)).Single().ContextBody;
        Check(body.Contains("取消事项 " + work.Id.ToString("N"), StringComparison.Ordinal));
        Check(body.Contains("再明确新时间建立提醒", StringComparison.Ordinal));
    }
    private static async Task WakeExpiredApproval()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        var notice = Decision(f); var output = (await EnsureDecision(l, f, notice)).Notification.Outbox.Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context expired", rejectionReason: OutboxRejectionReason.ContextExpired);
        f.Clock.UtcNow = notice.ExpiresAt;
        Check(await l.EnsurePendingWakeAsync(Identity, WakeRoute) == null);
    }
    private static async Task WakePendingApprovalExpiry()
    {
        await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
        var notice = Decision(f); var output = (await EnsureDecision(l, f, notice)).Notification.Outbox.Single();
        await l.BeginOutboxSendAsync(Identity, output.Id);
        await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context absent", rejectionReason: OutboxRejectionReason.ContextUnavailable);
        var wake = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!;
        f.Clock.UtcNow = notice.ExpiresAt;
        await Error(LedgerErrors.NotificationSuppressed, () => l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute));
        Equal(ChannelWakeStage.Cancelled, (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Stage);
        Equal(0, (await l.GetChannelWakeBudgetAsync()).StartedLast24Hours);
    }
    private static async Task WakeExpiredApprovalOutbox()
    {
        foreach (bool rejected in new[] { false, true })
        {
            await using var f = new F(); var l = await f.Open(); var notice = Decision(f);
            var output = (await EnsureDecision(l, f, notice)).Notification.Outbox.Single();
            if (rejected)
            {
                await l.BeginOutboxSendAsync(Identity, output.Id);
                await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "definite rejection");
            }
            f.Clock.UtcNow = notice.ExpiresAt;
            Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
            if (rejected)
            {
                Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length);
                await Error(LedgerErrors.NotificationSuppressed, () => l.RetryRejectedOutboxAsync(Identity, output.Id));
            }
            else await Error(LedgerErrors.NotificationSuppressed, () => l.BeginOutboxSendAsync(Identity, output.Id));
            Equal(rejected ? OutboxStage.Rejected : OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage);
        }
    }
    private static async Task WakeExpiredApprovalHistory()
    {
        foreach (var outcome in new[] { WakeDeliveryOutcome.Accepted, WakeDeliveryOutcome.Unknown })
        {
            await using var f = new F(); var l = await f.Open(); await ConfigureWake(l);
            var notice = Decision(f); var output = (await EnsureDecision(l, f, notice)).Notification.Outbox.Single();
            await l.BeginOutboxSendAsync(Identity, output.Id);
            await l.RecordOutboxOutcomeAsync(Identity, output.Id, OutboxStage.Rejected, "context absent", rejectionReason: OutboxRejectionReason.ContextUnavailable);
            var wake = (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!; f.Clock.UtcNow = wake.NotBefore;
            var lease = await l.BeginWakeSendAsync(Identity, wake.Id, WakeRoute); f.Clock.UtcNow = notice.ExpiresAt;
            await l.RecordWakeOutcomeAsync(lease, outcome, "actual late result", outcome == WakeDeliveryOutcome.Accepted ? "receipt" : null);
            await l.ReleaseWakeSendAsync(lease); await l.DisposeAsync(); l = await f.Open();
            Equal(outcome == WakeDeliveryOutcome.Accepted ? ChannelWakeStage.Accepted : ChannelWakeStage.Unknown,
                (await l.EnsurePendingWakeAsync(Identity, WakeRoute))!.Stage);
        }
    }
}
