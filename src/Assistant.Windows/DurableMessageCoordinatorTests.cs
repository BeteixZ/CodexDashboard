using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Isolated runner tests. No production settings, UI, credentials or network adapters.
internal static class DurableMessageCoordinatorTests
{
    private static readonly ConversationIdentity Identity = new("test-user", "test-binding", "test-conversation");
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("accept_is_durable_before_model_completion", AcceptBeforeModel),
        ("short_burst_preserves_all_inputs", ShortBurst),
        ("explicit_correction_covers_pending_reply_without_deferred_duplicate", PendingCorrection),
        ("fast_query_bypasses_noncooperative_model", FastQuery),
        ("trusted_fast_path_uses_current_user_grant", TrustedFastPath),
        ("new_input_blocks_old_remaining_segments", SupersededSegments),
        ("prepared_reply_recovers_without_model", PreparedRecovery),
        ("pending_reply_sends_once_across_restarts", PendingRecovery),
        ("unknown_segment_never_replays_or_sends_later_segments", UnknownRecovery),
        ("duplicate_does_not_regenerate_conflict_preserves_original", DuplicateInput),
        ("failed_commit_does_not_call_model_or_channel", FailedCommit),
        ("binding_switch_requires_actual_external_drain", BindingSwitch),
        ("new_identity_does_not_recover_old_identity", IdentityRecovery),
        ("channel_exception_is_unknown_without_retry", ChannelFailure),
        ("late_exact_acceptance_is_saved_before_actual_drain", LateAcceptance),
        ("late_exact_rejection_is_saved_before_actual_drain", LateRejection),
        ("late_no_receipt_stays_unknown_without_retry", LateUnknown),
        ("model_that_ignores_cancellation_keeps_drain_pending", ModelDrain),
        ("concurrent_inputs_preserve_every_message", ConcurrentInputs),
        ("unrelated_inputs_are_not_one_generation", DifferentTopics),
        ("model_failure_gets_durable_neutral_reply", FailureReply),
        ("typed_model_failure_is_actionable_without_raw_error_or_retry", TypedFailureReply),
        ("busy_fast_handler_resumes_latest_input_when_slot_releases", BusyFastHandler),
        ("notification_survives_input_revision_and_each_page_is_durable", NotificationDuringInput),
        ("explicit_context_recovery_retries_rejected_notifications_only", NotificationContextRecovery),
        ("notification_binding_switch_saves_original_receipt_without_new_send", NotificationBindingSwitch),
        ("deferred_inputs_run_fifo_after_current_fast_reply", DeferredFifo),
        ("fixed_query_bypasses_noncooperative_deferred_model", DeferredFastQuery),
        ("deferred_queued_work_resumes_after_restart", DeferredRecovery),
        ("unknown_deferred_reply_never_regenerates_or_sends_remaining_parts", DeferredUnknown),
        ("deferred_model_keeps_actual_drain_pending", DeferredDrain),
        ("new_dialogue_cancels_older_queued_inputs", DeferredNewDialogue),
        ("bounded_admission_drains_existing_and_overflow_backlog", DeferredCapacity)
    ];

    internal static async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS coordinator/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL coordinator/" + name + ": " + error); }
        }
        Console.WriteLine($"COORDINATOR TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    private static async Task AcceptBeforeModel()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        var model = new Model(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return ToolModelResponse.Reply("done"); });
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        var accepted = await c.AcceptAsync(Identity, "m1", "hello", DateTimeOffset.UtcNow);
        Check(!accepted.IsDuplicate);
        Equal(InboxStage.Received, accepted.Message.Stage);
        await entered.Task;
        Equal(1, (await f.Read()).Inbox.Length);
        Equal(0, f.Sends.Count);
        release.SetResult();
        await UntilAsync(async () => (await f.Read()).Outbox.Any(x => x.Stage == OutboxStage.Accepted));
    }

    private static async Task ShortBurst()
    {
        await using var f = new Fixture();
        var model = Model.Reply();
        await using var c = f.Coordinator(model, options: new() { MergeWindow = TimeSpan.FromMilliseconds(100) });
        await c.ActivateSessionAsync(Identity);
        var firstAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        await c.AcceptAsync(Identity, "m1", "read task A", firstAt);
        await c.AcceptAsync(Identity, "m2", "do not start it", firstAt.AddSeconds(1));
        await Until(() => f.Sends.Count == 1);
        var state = await f.Read();
        Equal(1, model.Requests.Count);
        Equal(2, state.Generations.Single(x => x.Stage == GenerationStage.HandedToOutbox).InboxIds.Length);
        Equal(firstAt, state.Inbox.Single(x => x.SourceMessageId == "m1").OccurredAt);
        Check(model.Requests.Single().Context.Any(x => x.Body == "read task A"));
        Check(model.Requests.Single().Context.Any(x => x.Body == "do not start it"));
    }

    private static async Task PendingCorrection()
    {
        await using var f = new Fixture();
        var entered = Signal();
        var model = new Model(async (request, ct) =>
        {
            if (request.CurrentMessage.SourceMessageId == "old")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return ToolModelResponse.Reply("corrected");
        });
        await using var c = f.Coordinator(model, options: new()
        {
            MergeWindow = TimeSpan.Zero,
            ReplacesPendingReply = (_, current) => current.Body == "replace previous"
        });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "old", "original request", DateTimeOffset.UtcNow);
        await entered.Task;
        await c.AcceptAsync(Identity, "new", "replace previous", DateTimeOffset.UtcNow);
        await UntilAsync(async () => (await f.Read()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
        Equal(0, (await f.Ledger.QueueAvailableInputWorkAsync(Identity)).Length);
        var state = await f.Read();
        Equal(2, state.Generations.Single(g => g.Stage == GenerationStage.HandedToOutbox).InboxIds.Length);
        Equal(0, state.InputWork.Length);
        Equal(1, f.Sends.Count);
    }

    private static async Task FastQuery()
    {
        await using var f = new Fixture();
        var entered = Signal(); var deferredEntered = Signal(); var release = Signal();
        var model = new Model(async (request, _) =>
        {
            if (request.DeferredWork == null) entered.TrySetResult();
            else deferredEntered.TrySetResult();
            await release.Task; return ToolModelResponse.Reply("obsolete");
        });
        await using var c = f.Coordinator(model,
            fast: (message, _) => Task.FromResult(message.Body == "status" ? new DurableFastReply(["current facts"]) : null));
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "m1", "long question", DateTimeOffset.UtcNow);
        await entered.Task;
        await c.AcceptAsync(Identity, "m2", "status", DateTimeOffset.UtcNow);
        await Until(() => f.Sends.Count == 1);
        Equal("current facts", f.Sends.Single().Message.Body);
        // The independent-input worker may already have claimed the unanswered m1.
        // Observe that permitted read explicitly instead of racing its scheduling.
        await deferredEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(1, model.Requests.Count(x => x.DeferredWork == null));
        Equal(1, model.Requests.Count(x => x.DeferredWork?.SourceMessageId == "m1"));
        Equal(2, model.Requests.Count);
        var drain = c.DrainSessionAsync();
        release.SetResult();
        await drain;
        await c.WaitForSessionDrainAsync();
        Equal(1, f.Sends.Count);
    }

    private static async Task TrustedFastPath()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "queue task", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var grant = await f.Ledger.AuthorizeCurrentUserRequestAsync(Identity, "old", "send_to_task", "local/A", "{}", "queue task");
        var queued = await f.Ledger.QueueOperationAsync(Identity, grant.Id, "old-operation");
        var model = Model.Reply();
        await using var c = f.Coordinator(model, trusted: async (context, ct) =>
        {
            if (context.Message.Body != "cancel queued") return null;
            Check(context.StillCurrent());
            var cancel = await context.Ledger.AuthorizeCurrentUserRequestAsync(Identity, context.Message.SourceMessageId,
                "cancel_queued", queued.Id.ToString("N"), "{}", "cancel queued", cancellationToken: ct);
            await context.Ledger.CancelQueuedOperationAsync(Identity, queued.Id, cancel.Id, ct);
            return new(["Queue entry cancelled."]);
        }, options: new() { MergeWindow = TimeSpan.FromSeconds(1) });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "cancel", "cancel queued", DateTimeOffset.UtcNow);
        await Until(() => f.Sends.Count == 1);
        Equal(OperationStage.CancelledBeforeDispatch, (await f.Read()).Operations.Single().Stage);
        Equal(0, model.Requests.Count);
    }

    private static async Task SupersededSegments()
    {
        await using var f = new Fixture();
        var model = new Model((request, _) => Task.FromResult(request.CurrentMessage.SourceMessageId == "m1"
            ? ToolModelResponse.Reply("old-1", "old-2", "old-3") : ToolModelResponse.Reply("new-only")));
        DurableMessageCoordinator? c = null;
        c = f.Coordinator(model, send: async (request, _) =>
        {
            f.Sends.Enqueue(request);
            if (request.Message.Body == "old-1") await c!.AcceptAsync(Identity, "m2", "new constraint", DateTimeOffset.UtcNow);
            return new(OutboxStage.Accepted, "api accepted");
        });
        await using (c)
        {
            await c.ActivateSessionAsync(Identity);
            await c.AcceptAsync(Identity, "m1", "first request", DateTimeOffset.UtcNow);
            await Until(() => f.Sends.Count == 2);
            Check(f.Sends.Select(x => x.Message.Body).SequenceEqual(["old-1", "new-only"]));
            Equal(2, (await f.Read()).Outbox.Count(x => x.Stage == OutboxStage.Superseded));
        }
    }

    private static async Task PreparedRecovery()
    {
        await using var f = new Fixture();
        await f.Prepare(false);
        await f.Reopen();
        var model = Model.Reply();
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        await Until(() => f.Sends.Count == 2);
        Equal(0, model.Requests.Count);
        Check(f.Sends.Select(x => x.Message.Body).SequenceEqual(["prepared-1", "prepared-2"]));
    }

    private static async Task PendingRecovery()
    {
        await using var f = new Fixture();
        await f.Prepare(true);
        await f.Reopen();
        await using (var c = f.Coordinator(Model.Reply()))
        {
            await c.ActivateSessionAsync(Identity);
            await UntilAsync(async () => (await f.Read()).Outbox.All(x => x.Stage == OutboxStage.Accepted));
        }
        Equal(2, f.Sends.Count);
        await f.Reopen();
        await using var next = f.Coordinator(Model.Reply());
        await next.ActivateSessionAsync(Identity);
        await Task.Delay(80);
        Equal(2, f.Sends.Count);
    }

    private static async Task UnknownRecovery()
    {
        await using var f = new Fixture();
        var outbox = await f.Prepare(true);
        await f.Ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await f.Reopen();
        await using var c = f.Coordinator(Model.Reply());
        await c.ActivateSessionAsync(Identity);
        await Task.Delay(80);
        Equal(0, f.Sends.Count);
        var state = await f.Read();
        Equal(OutboxStage.Unknown, state.Outbox[0].Stage);
        Equal(OutboxStage.Pending, state.Outbox[1].Stage);
    }

    private static async Task DuplicateInput()
    {
        await using var f = new Fixture();
        var model = Model.Reply();
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        var now = DateTimeOffset.UtcNow;
        await c.AcceptAsync(Identity, "m1", "original", now);
        Check((await c.AcceptAsync(Identity, "m1", "original", now)).IsDuplicate);
        await Until(() => f.Sends.Count == 1);
        try { await c.AcceptAsync(Identity, "m1", "replacement", now); throw new InvalidOperationException("Conflicting duplicate accepted."); }
        catch (LedgerException error) { Equal(LedgerErrors.Conflict, error.Code); }
        Equal("original", (await f.Read()).Inbox.Single().Body);
        Equal(1, model.Requests.Count);
    }

    private static async Task FailedCommit()
    {
        var fault = new CommitFault();
        await using var f = new Fixture(fault);
        var model = Model.Reply();
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        fault.Armed = true;
        try { await c.AcceptAsync(Identity, "missing", "do work", DateTimeOffset.UtcNow); throw new InvalidOperationException("Failed commit acknowledged."); }
        catch (Exception error) when (error is LedgerException or IOException) { }
        Equal(0, model.Requests.Count);
        Equal(0, f.Sends.Count);
        Equal(0, (await f.Read()).Inbox.Length);
    }

    private static async Task BindingSwitch()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        DurableDeliveryRequest? oldRequest = null;
        var current = Identity;
        var second = Identity with { BindingId = "other-binding" };
        await using var c = f.Coordinator(Model.Reply(), send: async (request, _) =>
        {
            f.Sends.Enqueue(request);
            if (request.Message.Identity == Identity) { oldRequest = request; entered.TrySetResult(); await release.Task; }
            return new(OutboxStage.Accepted, "accepted");
        }, provider: () => current);
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "old", "old identity", DateTimeOffset.UtcNow);
        await entered.Task;
        current = second;
        Check(!await c.DrainSessionAsync());
        Check(!oldRequest!.StillCurrent());
        var draining = c.WaitForSessionDrainAsync();
        Check(!draining.IsCompleted);
        await c.ActivateSessionAsync(second);
        await c.AcceptAsync(second, "new", "new identity", DateTimeOffset.UtcNow);
        await UntilAsync(async () => (await f.Ledger.ReadConversationAsync(second)).Outbox.Length == 1);
        Equal(1, f.Sends.Count);
        release.SetResult();
        await draining;
        await Until(() => f.Sends.Count == 2);
        Equal(second, f.Sends.Last().Message.Identity);
        Equal(OutboxStage.Accepted, (await f.Read()).Outbox.Single().Stage);
        Equal("accepted", (await f.Read()).Outbox.Single().ChannelReceipt);
        Equal(1, (await f.Read()).Events.Count(x => x.Source == ConversationSource.Assistant));
    }

    private static Task LateAcceptance() => LateReceipt(OutboxStage.Accepted);
    private static Task LateRejection() => LateReceipt(OutboxStage.Rejected);
    private static Task LateUnknown() => LateReceipt(OutboxStage.Unknown);
    private static async Task LateReceipt(OutboxStage expected)
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        await using var c = f.Coordinator(Model.Reply(), send: async (request, _) =>
        {
            f.Sends.Enqueue(request); entered.TrySetResult();
            await release.Task;
            return new(expected, "exact original attempt receipt", expected == OutboxStage.Rejected
                ? OutboxRejectionReason.ContextExpired : OutboxRejectionReason.None);
        }, options: new() { MergeWindow = TimeSpan.Zero, DrainTimeout = TimeSpan.FromMilliseconds(100) });
        try
        {
            await c.ActivateSessionAsync(Identity);
            await c.AcceptAsync(Identity, "late-receipt", "original request", DateTimeOffset.UtcNow);
            await entered.Task;
            Check(!await c.DrainSessionAsync());
            var unknown = (await f.Read()).Outbox.Single();
            Equal(OutboxStage.Unknown, unknown.Stage);
            Equal(1, unknown.SendAttempts);
            var drain = c.WaitForSessionDrainAsync();
            Check(!drain.IsCompleted);
            release.TrySetResult();
            await drain;
            var saved = (await f.Read()).Outbox.Single();
            Equal(expected, saved.Stage);
            Equal(unknown.Id, saved.Id); Equal(1, saved.SendAttempts); Equal(1, f.Sends.Count);
            if (expected != OutboxStage.Unknown) Equal("exact original attempt receipt", saved.ChannelReceipt);
            Equal(expected == OutboxStage.Rejected ? OutboxRejectionReason.ContextExpired : OutboxRejectionReason.None,
                saved.RejectionReason);
            Equal(expected == OutboxStage.Accepted ? 1 : 0,
                (await f.Read()).Events.Count(x => x.RelatedOutboxId == saved.Id && x.Source == ConversationSource.Assistant));
            await f.Reopen();
            Equal(expected, (await f.Read()).Outbox.Single().Stage);
            Equal(1, f.Sends.Count);
        }
        finally { release.TrySetResult(); await c.DrainSessionAsync(); await c.WaitForSessionDrainAsync(); }
    }

    private static async Task IdentityRecovery()
    {
        await using var f = new Fixture();
        await f.Prepare(true);
        await f.Reopen();
        var different = Identity with { UserId = "another-user" };
        await using var c = f.Coordinator(Model.Reply(), provider: () => different);
        await c.ActivateSessionAsync(different);
        await Task.Delay(60);
        Equal(0, f.Sends.Count);
        await c.AcceptAsync(different, "m1", "new user", DateTimeOffset.UtcNow);
        await Until(() => f.Sends.Count == 1);
        Equal(different, f.Sends.Single().Message.Identity);
        Equal(2, (await f.Read()).Outbox.Count(x => x.Stage == OutboxStage.Pending));
    }

    private static async Task ChannelFailure()
    {
        await using var f = new Fixture();
        var calls = 0;
        await using var c = f.Coordinator(Model.Reply(), send: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("uncertain channel receipt");
        });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "m1", "hello", DateTimeOffset.UtcNow);
        await UntilAsync(async () => (await f.Read()).Outbox.Any(x => x.Stage == OutboxStage.Unknown));
        await c.AcceptAsync(Identity, "m1", "hello", DateTimeOffset.UtcNow);
        await Task.Delay(50);
        Equal(1, calls);
    }

    private static async Task ModelDrain()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        var model = new Model(async (_, _) => { entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply("late"); });
        await using var c = f.Coordinator(model, options: new() { MergeWindow = TimeSpan.Zero, DrainTimeout = TimeSpan.FromMilliseconds(30) });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "m1", "long model", DateTimeOffset.UtcNow);
        await entered.Task;
        Check(!await c.DrainSessionAsync());
        var drain = c.WaitForSessionDrainAsync();
        Check(!drain.IsCompleted);
        release.SetResult();
        await drain;
        Equal(0, f.Sends.Count);
    }

    private static async Task ConcurrentInputs()
    {
        await using var f = new Fixture();
        await using var c = f.Coordinator(Model.Reply(), options: new() { MergeWindow = TimeSpan.FromMilliseconds(200) });
        await c.ActivateSessionAsync(Identity);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => c.AcceptAsync(Identity, "m" + i, "constraint " + i, DateTimeOffset.UtcNow)));
        await Until(() => f.Sends.Count == 1);
        var state = await f.Read();
        Equal(20, state.Inbox.Length);
        Equal(20L, state.Revision);
        Equal(state.Revision, f.Sends.Single().Message.Revision);
        Equal(20, state.Generations.Single(x => x.Stage == GenerationStage.HandedToOutbox).InboxIds.Length);
    }

    private static async Task DifferentTopics()
    {
        await using var f = new Fixture();
        await using var c = f.Coordinator(Model.Reply(), options: new() { MergeWindow = TimeSpan.FromMilliseconds(100), CanGroup = (_, _) => false });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "m1", "topic one", DateTimeOffset.UtcNow);
        await c.AcceptAsync(Identity, "m2", "unrelated topic", DateTimeOffset.UtcNow);
        await Until(() => f.Sends.Count == 2);
        var state = await f.Read();
        Check(state.Generations.Where(x => x.Stage == GenerationStage.HandedToOutbox).All(x => x.InboxIds.Length == 1));
        Equal(InboxStage.HandedToOutbox, state.Inbox.Single(x => x.SourceMessageId == "m1").Stage);
        Equal("m1", state.InputWork.Single().SourceMessageId);
    }

    private static async Task TypedFailureReply()
    {
        var operationId = Guid.NewGuid();
        var threadId = Guid.NewGuid().ToString();
        string CreationReceipt(string status, Guid boundOperation) => System.Text.Json.JsonSerializer.Serialize(new
        {
            status, facts = new { operationId, observedTask = new { ThreadId = threadId },
                newlyBoundTaskEvidence = new { createOperationId = boundOperation, task = new { ThreadId = threadId } } }
        });
        ToolLoopResult FailedCreation(string status, Guid boundOperation) => new(ToolLoopState.Failed, null!,
            [new("create", "create_task", CreationReceipt(status, boundOperation), OperationId: operationId)], []);
        string createdReply = DurableMessageCoordinator.FailureReply(FailedCreation("accepted", operationId)).Single();
        Check(createdReply.Contains(threadId) && createdReply.Contains("任务已创建") && createdReply.Contains("请勿重复创建"));
        Check(!DurableMessageCoordinator.FailureReply(FailedCreation("unknown", operationId)).Single().Contains("任务已创建"));
        Check(!DurableMessageCoordinator.FailureReply(FailedCreation("accepted", Guid.NewGuid())).Single().Contains("任务已创建"));
        foreach (var item in new[] { (ToolModelFailureKind.Authorization, "重新登录"),
            (ToolModelFailureKind.UsageLimit, "额度"), (ToolModelFailureKind.InvalidOutput, "格式"), (ToolModelFailureKind.Timeout, "超时") })
        {
            await using var f = new Fixture();
            var model = new Model((_, _) => throw new ToolModelException(item.Item1, new IOException("PRIVATE_SERVER_DETAIL")));
            await using var c = f.Coordinator(model);
            await c.ActivateSessionAsync(Identity);
            await c.AcceptAsync(Identity, "failed", "你好", DateTimeOffset.UtcNow);
            await UntilAsync(async () => (await f.Read()).Outbox.Any(x => x.Stage == OutboxStage.Accepted));
            string body = f.Sends.Single().Message.Body;
            Check(body.Contains(item.Item2) && !body.Contains("PRIVATE_SERVER_DETAIL") && !body.Contains("工作列表") && !body.Contains("状态和报告"));
            Equal(1, model.Requests.Count);
            Equal(0, (await f.Read()).Operations.Length);
        }
    }

    private static async Task FailureReply()
    {
        await using var f = new Fixture();
        var model = new Model((_, _) => throw new IOException("model offline"));
        await using (var c = f.Coordinator(model))
        {
            await c.ActivateSessionAsync(Identity);
            await c.AcceptAsync(Identity, "m1", "do something", DateTimeOffset.UtcNow);
            await UntilAsync(async () => (await f.Read()).Outbox.Any(x => x.Stage == OutboxStage.Accepted));
            Check(f.Sends.Single().Message.Body.Contains("这次没能收到可用的模型回复", StringComparison.Ordinal)
                && !f.Sends.Single().Message.Body.Contains("工作列表", StringComparison.Ordinal));
            Equal(0, (await f.Read()).Operations.Length);
        }
        await f.Reopen();
        var afterRestart = Model.Reply();
        await using var next = f.Coordinator(afterRestart);
        await next.ActivateSessionAsync(Identity);
        await Task.Delay(60);
        Equal(0, afterRestart.Requests.Count);
        Equal(1, f.Sends.Count);
    }

    private static async Task BusyFastHandler()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        var model = Model.Reply();
        await using var c = f.Coordinator(model, fast: async (message, _) =>
        {
            if (message.SourceMessageId == "m1")
            {
                entered.TrySetResult();
                await release.Task;
                return new(["old fast result"]);
            }
            return new(["latest fast result"]);
        });
        await c.ActivateSessionAsync(Identity);
        await c.AcceptAsync(Identity, "m1", "first fast input", DateTimeOffset.UtcNow);
        await entered.Task;
        await c.AcceptAsync(Identity, "m2", "latest fast input", DateTimeOffset.UtcNow);
        await Task.Delay(40);
        Equal(0, f.Sends.Count);
        release.SetResult();
        await Until(() => f.Sends.Count == 2);
        Equal("latest fast result", f.Sends.Single(x => x.Message.Kind == OutboxKind.ConversationReply).Message.Body);
        Equal("m1", model.Requests.Single().DeferredWork!.SourceMessageId);
        Check(f.Sends.All(x => x.Message.Body != "old fast result"));
    }

    private static async Task NotificationDuringInput()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        DurableDeliveryRequest? notification = null;
        CancellationToken sendingToken = default;
        await using var c = f.Coordinator(Model.Reply(), segmenter: new Segmenter(), send: async (request, ct) =>
        {
            f.Sends.Enqueue(request);
            if (request.Message.Body == "notification-one")
            {
                notification = request;
                sendingToken = ct;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return new(OutboxStage.Accepted, "API accepted only");
        });
        await c.ActivateSessionAsync(Identity);
        var saved = await c.EnqueueNotificationAsync(Identity, "completion", "notification-one|notification-two", new("local", "task-A", "turn-A"));
        Equal(2, saved.Outbox.Length);
        Check(saved.Outbox.All(x => x.GenerationId is null && x.Kind == OutboxKind.Notification));
        await entered.Task;
        await c.AcceptAsync(Identity, "user", "new question", DateTimeOffset.UtcNow);
        await UntilAsync(async () => (await f.Read()).Outbox.Any(x => x.Kind == OutboxKind.ConversationReply));
        Check(notification!.StillCurrent());
        Check(!sendingToken.IsCancellationRequested);
        release.SetResult();
        await UntilAsync(async () => (await f.Read()).Outbox.Count(x => x.Stage == OutboxStage.Accepted) == 3);
        Equal(3, f.Sends.Count);
        Equal(2, (await f.Read()).Events.Count(x => x.Source == ConversationSource.Assistant && x.ReferencedEventId == saved.Event.Id));
    }

    private static async Task NotificationContextRecovery()
    {
        await using var f = new Fixture();
        var rejected = (await f.Ledger.EnqueueNotificationAsync(Identity, "rejected", "retry only this", null)).Outbox.Single();
        var unknown = (await f.Ledger.EnqueueNotificationAsync(Identity, "unknown", "never replay", null)).Outbox.Single();
        await f.Ledger.BeginOutboxSendAsync(Identity, rejected.Id);
        await f.Ledger.RecordOutboxOutcomeAsync(Identity, rejected.Id, OutboxStage.Rejected, "context rejected");
        await f.Ledger.BeginOutboxSendAsync(Identity, unknown.Id);
        await f.Reopen();
        var model = Model.Reply();
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        await Task.Delay(50);
        Equal(0, f.Sends.Count);
        await c.RetryRejectedNotificationsAsync(Identity);
        await Until(() => f.Sends.Count == 1);
        Equal(rejected.Id, f.Sends.Single().Message.Id);
        Equal(OutboxStage.Unknown, (await f.Read()).Outbox.Single(x => x.Id == unknown.Id).Stage);
        Equal(0, model.Requests.Count);
    }

    private static async Task NotificationBindingSwitch()
    {
        await using var f = new Fixture();
        var entered = Signal(); var release = Signal();
        var current = Identity;
        var second = Identity with { BindingId = "second" };
        DurableDeliveryRequest? request = null;
        await using var c = f.Coordinator(Model.Reply(), provider: () => current, send: async (next, _) =>
        {
            request = next;
            f.Sends.Enqueue(next);
            entered.TrySetResult();
            await release.Task;
            return new(OutboxStage.Accepted, "late receipt");
        });
        await c.ActivateSessionAsync(Identity);
        await c.EnqueueNotificationAsync(Identity, "notice", "old binding facts", null);
        await entered.Task;
        current = second;
        Check(!await c.DrainSessionAsync());
        Check(!request!.StillCurrent());
        release.SetResult();
        await c.WaitForSessionDrainAsync();
        await c.ActivateSessionAsync(second);
        await Task.Delay(50);
        Equal(1, f.Sends.Count);
        Equal(OutboxStage.Accepted, (await f.Read()).Outbox.Single().Stage);
        Equal("late receipt", (await f.Read()).Outbox.Single().ChannelReceipt);
        Equal(0, (await f.Ledger.ReadConversationAsync(second)).Events.Length);
    }

    private static async Task DeferredFifo()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "one", "read one", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "two", "read two", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var model = new Model((request, _) =>
        {
            Check(request.DeferredWork is not null);
            Equal("latest", request.LatestInput!.SourceMessageId);
            return Task.FromResult(ToolModelResponse.Reply("read " + request.CurrentMessage.SourceMessageId));
        });
        await using var c = f.Coordinator(model, fast: (_, _) => Task.FromResult<DurableFastReply?>(new(["current status"])));
        await c.ActivateSessionAsync(Identity);
        await UntilAsync(async () => (await f.Read()).InputWork.Count(x => x.Stage == InputWorkStage.Completed) == 2);
        Equal("one,two", string.Join(",", model.Requests.Select(x => x.CurrentMessage.SourceMessageId)));
        Equal(3, f.Sends.Count);
        Check(f.Sends.Skip(1).All(x => x.Message.Kind == OutboxKind.DeferredReply && x.Message.Revision < x.Message.ReadAtRevision));
        Equal(0, (await f.Read()).Authorizations.Length);
    }

    private static async Task DeferredFastQuery()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "old independent question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var entered = Signal(); var release = Signal();
        var reads = 0;
        var model = new Model(async (request, _) =>
        {
            Check(request.DeferredWork is not null);
            if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply("obsolete read"); }
            Equal("new", request.LatestInput!.SourceMessageId);
            return ToolModelResponse.Reply("updated read");
        });
        await using var c = f.Coordinator(model, fast: (_, _) => Task.FromResult<DurableFastReply?>(new(["status ready"])));
        await c.ActivateSessionAsync(Identity);
        await entered.Task;
        await c.AcceptAsync(Identity, "new", "status with a new constraint", DateTimeOffset.UtcNow);
        await Until(() => f.Sends.Count(x => x.Message.Kind == OutboxKind.ConversationReply) == 2);
        Check(f.Sends.All(x => x.Message.Body != "obsolete read"));
        release.SetResult();
        await UntilAsync(async () => (await f.Read()).InputWork.Any(x => x.Stage == InputWorkStage.Completed));
        Check(f.Sends.Any(x => x.Message.Body == "updated read"));
        Check(f.Sends.All(x => x.Message.Body != "obsolete read"));
    }

    private static async Task DeferredRecovery()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "retained independent question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "current answered", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var latest = await f.Ledger.BeginGenerationAsync(Identity, ["latest"]);
        await f.Ledger.PrepareReplyAsync(latest, ["previous reply"]);
        var reply = (await f.Ledger.EnqueuePreparedReplyAsync(latest)).Single();
        await f.Ledger.BeginOutboxSendAsync(Identity, reply.Id);
        await f.Ledger.RecordOutboxOutcomeAsync(Identity, reply.Id, OutboxStage.Accepted, "prior API receipt");
        var work = (await f.Ledger.QueueUnprocessedInputsAsync(Identity)).Single();
        await f.Ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        await f.Reopen();
        var model = Model.Reply();
        await using var c = f.Coordinator(model);
        await c.ActivateSessionAsync(Identity);
        await UntilAsync(async () => (await f.Read()).InputWork.Single().Stage == InputWorkStage.Completed);
        Equal("old", model.Requests.Single().CurrentMessage.SourceMessageId);
        Equal(1, f.Sends.Count);
        Equal(OutboxKind.DeferredReply, f.Sends.Single().Message.Kind);
    }

    private static async Task DeferredUnknown()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "earlier question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var work = (await f.Ledger.QueueUnprocessedInputsAsync(Identity)).Single();
        var lease = await f.Ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        await f.Ledger.PrepareReplyAsync(lease, ["unknown first", "must stay held"]);
        var replies = await f.Ledger.EnqueuePreparedReplyAsync(lease);
        await f.Ledger.BeginOutboxSendAsync(Identity, replies[0].Id);
        await f.Reopen();
        var model = Model.Reply();
        await using var c = f.Coordinator(model, fast: (_, _) => Task.FromResult<DurableFastReply?>(new(["current status"])));
        await c.ActivateSessionAsync(Identity);
        await Until(() => f.Sends.Count == 1);
        await Task.Delay(100);
        Equal(0, model.Requests.Count);
        Equal(InputWorkStage.Unknown, (await f.Read()).InputWork.Single().Stage);
        Check(f.Sends.All(x => x.Message.Kind == OutboxKind.ConversationReply));
    }

    private static async Task DeferredDrain()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "earlier question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        var entered = Signal(); var release = Signal();
        var model = new Model(async (_, _) => { entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply("late"); });
        await using var c = f.Coordinator(model, fast: (_, _) => Task.FromResult<DurableFastReply?>(new(["current status"])),
            options: new() { MergeWindow = TimeSpan.Zero, DrainTimeout = TimeSpan.FromMilliseconds(30) });
        await c.ActivateSessionAsync(Identity);
        await entered.Task;
        Check(!await c.DrainSessionAsync());
        var drained = c.WaitForSessionDrainAsync();
        await Task.Delay(30);
        Check(!drained.IsCompleted);
        release.SetResult();
        await drained;
        Check(f.Sends.All(x => x.Message.Body != "late"));
    }

    private static async Task DeferredNewDialogue()
    {
        await using var f = new Fixture();
        await f.Ledger.ReceiveAsync(Identity, "old", "earlier question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.ReceiveAsync(Identity, "latest", "new dialogue", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.QueueUnprocessedInputsAsync(Identity);
        var model = Model.Reply();
        await using var c = f.Coordinator(model, trusted: async (request, ct) =>
        {
            await f.Ledger.BeginNewDialogueAsync(Identity, request.Message.SourceMessageId, ct);
            return new(["new dialogue ready"]);
        });
        await c.ActivateSessionAsync(Identity);
        await Until(() => f.Sends.Count == 1);
        await Task.Delay(50);
        Equal(0, model.Requests.Count);
        Equal(InputWorkStage.Cancelled, (await f.Read()).InputWork.Single().Stage);
    }

    private static async Task DeferredCapacity()
    {
        foreach (bool existingQueue in new[] { false, true })
        {
            await using var f = new Fixture(options: new() { MaximumPendingInputWork = 1 });
            await f.Ledger.ReceiveAsync(Identity, "one", "first independent question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            await f.Ledger.ReceiveAsync(Identity, "two", "second independent question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            if (existingQueue) await f.Ledger.QueueUnprocessedInputsAsync(Identity);
            await f.Ledger.ReceiveAsync(Identity, "three", "third independent question", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            await f.Ledger.ReceiveAsync(Identity, "latest", "status", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var model = Model.Reply();
            await using var c = f.Coordinator(model, fast: (_, _) => Task.FromResult<DurableFastReply?>(new(["current status"])));
            await c.ActivateSessionAsync(Identity);
            await UntilAsync(async () => (await f.Read()).InputWork.Count(x => x.Stage == InputWorkStage.Completed) == 3);
            Equal("one,two,three", string.Join(",", model.Requests.Select(x => x.CurrentMessage.SourceMessageId)));
            Equal(4, f.Sends.Count);
            Equal(4, (await f.Read()).Inbox.Length);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(5, deadline.Token);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "assistant-coordinator-" + Guid.NewGuid().ToString("N"));
        private readonly Protector protector = new();
        private readonly IJournalFaultInjector? fault;
        private readonly LedgerOptions? options;
        internal DurableConversationLedger Ledger { get; private set; }
        internal ConcurrentQueue<DurableDeliveryRequest> Sends { get; } = new();
        internal Fixture(IJournalFaultInjector? fault = null, LedgerOptions? options = null)
        {
            this.fault = fault;
            this.options = options;
            Directory.CreateDirectory(directory);
            Ledger = Open().GetAwaiter().GetResult();
        }
        private Task<DurableConversationLedger> Open() => DurableConversationLedger.OpenAsync(
            new FileJournalStorage(Path.Combine(directory, "test.journal"), faultInjector: fault), protector, options: options);
        internal Task<ConversationSnapshot> Read() => Ledger.ReadConversationAsync(Identity);
        internal async Task Reopen() { await Ledger.DisposeAsync(); Ledger = await Open(); }
        internal DurableMessageCoordinator Coordinator(IToolModel model,
            Func<DurableDeliveryRequest, CancellationToken, Task<DurableDeliveryResult>>? send = null,
            Func<InboxMessage, CancellationToken, Task<DurableFastReply?>>? fast = null,
            Func<DurableFastContext, CancellationToken, Task<DurableFastReply?>>? trusted = null,
            DurableMessageOptions? options = null, Func<ConversationIdentity?>? provider = null,
            IReplySegmenter? segmenter = null)
        {
            var loop = new ToolAgentLoop(Ledger, model, ToolCatalog.Initial(), new NoExecutor(), new NoAuthorization(), replySegmenter: segmenter);
            return new(Ledger, loop, provider ?? (() => Identity), send ?? ((request, _) =>
            {
                Check(request.StillCurrent());
                Sends.Enqueue(request);
                return Task.FromResult(new DurableDeliveryResult(OutboxStage.Accepted, "API acceptance only"));
            }), readonlyFastHandler: fast, trustedFastHandler: trusted, replySegmenter: segmenter,
                options: options ?? new() { MergeWindow = TimeSpan.Zero });
        }
        internal async Task<ImmutableArray<OutboxMessage>> Prepare(bool enqueue)
        {
            await Ledger.ReceiveAsync(Identity, "prepared", "stored request", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var lease = await Ledger.BeginGenerationAsync(Identity, ["prepared"]);
            await Ledger.PrepareReplyAsync(lease, ["prepared-1", "prepared-2"]);
            return enqueue ? await Ledger.EnqueuePreparedReplyAsync(lease) : [];
        }
        public async ValueTask DisposeAsync()
        {
            await Ledger.DisposeAsync();
            var resolved = Path.GetFullPath(directory);
            var root = Path.GetFullPath(Path.GetTempPath());
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("assistant-coordinator-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
            protector.Dispose();
        }
    }
    private sealed class Model(Func<ToolModelRequest, CancellationToken, Task<ToolModelResponse>> generate) : IToolModel
    {
        internal ConcurrentQueue<ToolModelRequest> Requests { get; } = new();
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return generate(request, cancellationToken);
        }
        internal static Model Reply() => new((_, _) => Task.FromResult(ToolModelResponse.Reply("reply")));
    }
    private sealed class NoExecutor : IToolExecutor
    {
        public ToolCapability GetCapability(string capability) => new(false);
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No test tool should execute.");
    }
    private sealed class NoAuthorization : IUserAuthorizationPolicy
    {
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserAuthorizationDecision(false));
    }
    private sealed class Segmenter : IReplySegmenter
    {
        public ImmutableArray<string> Split(ImmutableArray<string> replySegments) =>
            replySegments.SelectMany(x => x.Split('|')).ToImmutableArray();
    }
    private sealed class CommitFault : IJournalFaultInjector
    {
        internal bool Armed;
        public void At(JournalWriteStage stage, long sequence)
        {
            if (Armed && stage == JournalWriteStage.BeforeWrite) throw new IOException("Injected durable commit failure.");
        }
    }
    private sealed class Protector : IStateProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public string Scheme => "test-only-aes-gcm";
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
        {
            var result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), purpose);
            return result;
        }
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose)
        {
            var result = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext[..12], ciphertext[28..], ciphertext.Slice(12, 16), result, purpose);
            return result;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }
}
