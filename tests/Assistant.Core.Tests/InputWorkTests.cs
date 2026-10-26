using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static class InputWorkTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private const string ReadArgs = """{"hostId":"local","threadId":"thread-A"}""";
    private const string SendArgs = """{"hostId":"local","threadId":"thread-A","text":"continue tests"}""";
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("old_inputs_are_claimed_once_in_source_fifo_order", Fifo),
        ("claimed_input_cannot_join_an_ordinary_generation", ExcludeGrouping),
        ("deferred_effects_never_reach_policy_or_executor", EffectsDenied),
        ("ledger_execution_boundary_independently_rejects_effects", LedgerEffectBoundary),
        ("readonly_execution_proof_binds_source_work_invocation_and_revision", ExecutionProof),
        ("latest_input_survives_truncated_context_as_explicit_data", LatestInput),
        ("source_budgets_survive_attempts_and_restart_without_refund", Budget),
        ("same_model_call_id_in_new_version_performs_a_fresh_read", FreshRead),
        ("new_input_cancels_a_readonly_model_without_preparing_reply", CancelModel),
        ("new_input_cancels_a_readonly_tool_without_preparing_reply", CancelTool),
        ("restart_requeues_reading_and_retains_charged_tool_attempt", RecoverReading),
        ("restart_prepared_reply_hands_off_original_segments_once", RecoverPrepared),
        ("restart_sending_is_unknown_and_does_not_resume_remaining_segments", RecoverSending),
        ("new_input_requeues_only_work_with_no_send_attempt", NewInputUnsent),
        ("new_input_after_partial_acceptance_preserves_history_without_replacement", NewInputPartial),
        ("late_acceptance_after_new_input_never_reopens_old_remaining_segments", LateAcceptance),
        ("rejected_ordinary_reply_cannot_be_recreated_as_deferred_work", RejectedOrdinaryReply),
        ("completed_work_stays_completed_after_new_input_and_dialogue_reset", CompletedHistory),
        ("same_revision_dialogue_reset_cancels_active_model_and_old_work", ResetDuringModel),
        ("new_dialogue_cancels_all_queued_work_and_keeps_original_inbox", ResetQueued),
        ("needs_confirmation_is_durable_and_never_automatically_requeued", ConfirmationRecovery),
        ("registered_unknown_effect_is_not_duplicated_as_input_work", UnknownEffect),
        ("legacy_snapshot_without_input_work_remains_compatible", Legacy),
        ("corrupt_work_generation_reference_fails_closed_without_rewriting", CorruptReference),
        ("work_capacity_failure_is_atomic_and_preserves_original_inputs", Capacity)
    ];

    internal static async Task<int> Run()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS input_work/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL input_work/" + name + ": " + error); }
        }
        Console.WriteLine($"INPUT WORK TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    private static Task<ReceiveResult> Input(DurableConversationLedger ledger, string id, string body) =>
        ledger.ReceiveAsync(Identity, id, body, MessageTrust.AuthenticatedUser, FakeClock.Now);

    private static async Task<InputWorkItem> Seed(DurableConversationLedger ledger)
    {
        await Input(ledger, "old", "read task A and explain its status");
        await Input(ledger, "latest", "Only read and explain. Do not change any task.");
        return (await ledger.QueueUnprocessedInputsAsync(Identity)).Single();
    }

    private static async Task Fifo()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "first", "first independent question");
        await Input(ledger, "second", "second independent question");
        await Input(ledger, "current", "current question");
        var work = await ledger.QueueUnprocessedInputsAsync(Identity);
        Equal(2, work.Length);
        Equal("first", work[0].SourceMessageId);
        Equal("second", work[1].SourceMessageId);
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        await Error(LedgerErrors.Conflict, () => ledger.BeginReadOnlyWorkAsync(Identity, work[1].Id));
        var first = await ledger.BeginReadOnlyWorkAsync(Identity, work[0].Id);
        Equal(1L, first.Revision);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(3L, state.InputWork[0].ReadAtRevision!.Value);
        Equal(1L, state.InputWork[0].Version);
        Equal("first independent question", state.Inbox.Single(x => x.SourceMessageId == "first").Body);
        await Error(LedgerErrors.Conflict, () => ledger.BeginReadOnlyWorkAsync(Identity, work[1].Id));
        await ledger.FinishReadOnlyWorkAsync(first, InputWorkStage.Failed, "A factual read failure is terminal.");
        await ledger.EndGenerationAttemptAsync(first);
        var second = await ledger.BeginReadOnlyWorkAsync(Identity, work[1].Id);
        Equal(2L, second.Revision);
        Check((await ledger.ReadConversationAsync(Identity)).Authorizations.IsEmpty);
    }

    private static async Task ExcludeGrouping()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Seed(ledger);
        await Error(LedgerErrors.InvalidTransition, () => ledger.GroupMessagesAsync(Identity, ["old", "latest"]));
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginGenerationAsync(Identity, ["old", "latest"]));
        var current = await ledger.BeginGenerationAsync(Identity, ["latest"]);
        Equal(2L, current.Revision);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).InputWork.Length);
    }

    private static async Task EffectsDenied()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var policy = new CountingPolicy();
        var executor = new RecordingExecutor(ledger);
        var model = new ScriptModel((request, index, _) =>
        {
            Check(request.Tools.All(x => x.Name is not ("send_to_task" or "remember" or "forget" or "cancel_queued")));
            return Task.FromResult(index == 0
                ? ToolModelResponse.Call(
                    new("send", "send_to_task", SendArgs),
                    new("remember", "remember", """{"text":"old instruction","scope":"conversation"}"""),
                    new("forget", "forget", """{"memoryId":"old-memory"}"""),
                    new("cancel", "cancel_queued", """{"operationId":"old-operation"}"""))
                : ToolModelResponse.Reply("Restate the requested action in a current message."));
        });
        var result = await Loop(ledger, model, executor, policy).RunReadOnlyWorkAsync(Identity, work.Id);
        Equal(ToolLoopState.NeedsConfirmation, result.State);
        Equal(0, policy.Calls);
        Equal(0, executor.Calls.Count);
        Equal(4, result.Observations.Length);
        Check(result.Observations.All(x => x.ResultJson.Contains("needs_confirmation", StringComparison.Ordinal)));
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.Authorizations.IsEmpty && state.Operations.IsEmpty);
        Equal(InputWorkStage.NeedsConfirmation, state.InputWork.Single().Stage);
        Equal(OutboxKind.DeferredReply, result.Outbox.Single().Kind);
        Equal(work.Id, result.Outbox.Single().InputWorkId!.Value);
    }

    private static async Task LedgerEffectBoundary()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        var effect = await ledger.RegisterToolInvocationAsync(lease, "old", "forged-effect", "send_to_task", SendArgs, true);
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.BeginToolInvocationAsync(lease, effect.Id));
        var read = await ledger.RegisterToolInvocationAsync(lease, "old", "read", "read_task", ReadArgs, false);
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.BeginToolInvocationAsync(lease, read.Id, Guid.NewGuid()));
        await Error(LedgerErrors.StaleRevision, () => ledger.AuthorizeCurrentUserRequestAsync(
            Identity, "old", "send_to_task", "local/thread-A", SendArgs, "read task A"));
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.Authorizations.IsEmpty && state.Operations.IsEmpty);
        Check(state.ToolInvocations.All(x => x.Stage == ToolInvocationStage.Prepared));
    }

    private static async Task ExecutionProof()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        var invocation = await ledger.RegisterToolInvocationAsync(lease, "old", "read", "read_task", ReadArgs, false);
        var context = new ToolExecutionContext(Identity, "old", lease.Revision, invocation.Id, null, null, work.Id, 2);
        Check(!await ledger.VerifyReadOnlyWorkExecutionAsync(context));
        await ledger.BeginToolInvocationAsync(lease, invocation.Id);
        Check(await ledger.VerifyReadOnlyWorkExecutionAsync(context));
        foreach (var forged in new[]
        {
            context with { Identity = Identity with { BindingId = "another-binding" } },
            context with { SourceMessageId = "latest" },
            context with { Revision = 2 },
            context with { ReadAtRevision = 1 },
            context with { InputWorkId = Guid.NewGuid() },
            context with { InvocationId = Guid.NewGuid() },
            context with { OperationId = Guid.NewGuid() },
            context with { IdempotencyKey = "side-effect-key" }
        })
            Check(!await ledger.VerifyReadOnlyWorkExecutionAsync(forged));
        await Input(ledger, "new", "Stop that read.");
        Check(!await ledger.VerifyReadOnlyWorkExecutionAsync(context));
        await Error(LedgerErrors.StaleRevision, () => ledger.PrepareReplyAsync(lease, ["stale reply"]));
    }

    private static async Task LatestInput()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        await ledger.AppendContextAsync(Identity, ConversationSource.Notification, new string('x', 2048), FakeClock.Now);
        var model = new ScriptModel((request, _, _) =>
        {
            var latest = request.LatestInput ?? throw new InvalidOperationException("The latest input must be explicit.");
            Equal("old", request.CurrentMessage.SourceMessageId);
            Equal("latest", latest.SourceMessageId);
            Equal("Only read and explain. Do not change any task.", latest.Body);
            Equal(2L, request.DeferredWork!.ReadAtRevision!.Value);
            Equal(1L, request.Revision);
            Check(request.Context.IsEmpty);
            return Task.FromResult(ToolModelResponse.Reply("The current read-only constraint is preserved."));
        });
        var result = await Loop(ledger, model, new RecordingExecutor(ledger), options: new()
        {
            MaximumContextCharacters = 1024
        }).RunReadOnlyWorkAsync(Identity, work.Id);
        Equal(ToolLoopState.Completed, result.State);
    }

    private static async Task Budget()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var first = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        Check(await ledger.ReserveAgentBudgetAsync(first, "old", false, 2, 1));
        Check(await ledger.ReserveAgentBudgetAsync(first, "old", true, 2, 1));
        await ledger.EndGenerationAttemptAsync(first);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var second = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        Check(await ledger.ReserveAgentBudgetAsync(second, "old", false, 2, 1));
        Check(!await ledger.ReserveAgentBudgetAsync(second, "old", false, 2, 1));
        Check(!await ledger.ReserveAgentBudgetAsync(second, "old", true, 2, 1));
        await ledger.EndGenerationAttemptAsync(second);
        var model = ScriptModel.Reply();
        var result = await Loop(ledger, model, new RecordingExecutor(ledger), options: new()
        {
            MaximumModelTurns = 2, MaximumToolCalls = 1
        }).RunReadOnlyWorkAsync(Identity, work.Id);
        Equal(ToolLoopState.BudgetExceeded, result.State);
        Equal(0, model.Requests.Count);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Failed, state.InputWork.Single().Stage);
        var budget = state.AgentBudgets.Single();
        Equal("old", budget.SourceMessageId);
        Equal(1L, budget.Revision);
        Equal(2, budget.ModelTurns);
        Equal(1, budget.ToolCalls);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(InputWorkStage.Failed, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
    }

    private static async Task FreshRead()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var executor = new RecordingExecutor(ledger);
        var first = await Loop(ledger, ScriptModel.ReadThenReply(), executor).RunReadOnlyWorkAsync(Identity, work.Id);
        Equal(ToolLoopState.Completed, first.State);
        await Input(ledger, "newer", "Read again with the latest facts.");
        var secondModel = ScriptModel.ReadThenReply();
        var second = await Loop(ledger, secondModel, executor).RunReadOnlyWorkAsync(Identity, work.Id);
        Equal(ToolLoopState.Completed, second.State);
        Equal(2, executor.Calls.Count);
        Check(secondModel.Requests[0].Observations.IsEmpty);
        Equal(1, secondModel.Requests[1].Observations.Length);
        Check(secondModel.Requests[1].Observations[0].ResultJson.Contains("\"readNumber\":2", StringComparison.Ordinal));
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(2, state.ToolInvocations.Length);
        Equal(2, state.ToolInvocations.Select(x => x.GenerationId).Distinct().Count());
        Equal(1, state.ToolInvocations.Select(x => x.CallId).Distinct().Count());
        Equal(2L, state.InputWork.Single().Version);
        Equal(4, state.AgentBudgets.Single().ModelTurns);
        Equal(2, state.AgentBudgets.Single().ToolCalls);
        Equal(OutboxStage.Superseded, state.Outbox.Single(x => x.Id == first.Outbox[0].Id).Stage);
    }

    private static async Task CancelModel()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptModel(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ToolModelResponse.Reply("must not be prepared");
        });
        var running = Loop(ledger, model, new RecordingExecutor(ledger)).RunReadOnlyWorkAsync(Identity, work.Id);
        await entered.Task;
        await Input(ledger, "new", "A newer constraint.");
        Equal(ToolLoopState.Superseded, (await running).State);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Queued, state.InputWork.Single().Stage);
        Check(state.Outbox.IsEmpty);
    }

    private static async Task CancelTool()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RecordingExecutor(ledger)
        {
            Handler = async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Facts(99);
            }
        };
        var running = Loop(ledger, ScriptModel.ReadThenReply(), executor).RunReadOnlyWorkAsync(Identity, work.Id);
        await entered.Task;
        await Input(ledger, "new", "Stop that read.");
        Equal(ToolLoopState.Superseded, (await running).State);
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.Outbox.IsEmpty && state.Operations.IsEmpty);
        Equal(ToolInvocationStage.Failed, state.ToolInvocations.Single().Stage);
        Equal(InputWorkStage.Queued, state.InputWork.Single().Stage);
    }

    private static async Task RecoverReading()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        Check(await ledger.ReserveAgentBudgetAsync(lease, "old", true, 8, 16));
        var invocation = await ledger.RegisterToolInvocationAsync(lease, "old", "read", "read_task", ReadArgs, false);
        await ledger.BeginToolInvocationAsync(lease, invocation.Id);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Queued, state.InputWork.Single().Stage);
        Equal(GenerationStage.Interrupted, state.Generations.Single().Stage);
        Equal(ToolInvocationStage.Interrupted, state.ToolInvocations.Single().Stage);
        Equal(1, state.AgentBudgets.Single().ToolCalls);
        Check(state.Outbox.IsEmpty);
        var resumed = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        Check(resumed.Id != lease.Id);
        Equal(2L, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Version);
    }

    private static async Task RecoverPrepared()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        await ledger.PrepareReplyAsync(lease, ["first exact channel segment", "second exact channel segment"]);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var recovery = await ledger.GetRecoveryPlanAsync(Identity);
        Equal(lease.Id, recovery.PreparedReplies.Single().Id);
        var outbox = await ledger.EnqueuePreparedReplyAsync(lease);
        var duplicate = await ledger.EnqueuePreparedReplyAsync(lease);
        Check(outbox.Select(x => x.Id).SequenceEqual(duplicate.Select(x => x.Id)));
        Equal(2, outbox.Length);
        Check(outbox.All(x => x.Kind == OutboxKind.DeferredReply && x.InputWorkId == work.Id
            && x.Revision == 1 && x.ReadAtRevision == 2 && x.NotificationEventId is null));
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
        foreach (var part in outbox)
        {
            await ledger.BeginOutboxSendAsync(Identity, part.Id);
            await ledger.RecordOutboxOutcomeAsync(Identity, part.Id, OutboxStage.Accepted, "channel accepted");
        }
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Completed, state.InputWork.Single().Stage);
        Equal(2, state.Events.Count(x => x.Source == ConversationSource.Assistant));
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(InputWorkStage.Completed, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
    }

    private static async Task RecoverSending()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var outbox = await Prepare(ledger, work.Id, "first", "second");
        await ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Unknown, state.InputWork.Single().Stage);
        Check(state.InputWork.Single().HasSendAttempt);
        Equal(OutboxStage.Unknown, state.Outbox.Single(x => x.Id == outbox[0].Id).Stage);
        var recovery = await ledger.GetRecoveryPlanAsync(Identity);
        Check(recovery.PendingOutbox.IsEmpty);
        Equal(outbox[0].Id, recovery.OutboxToReconcile.Single().Id);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work.Id));
        await Error(LedgerErrors.StaleRevision, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
    }

    private static async Task NewInputUnsent()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var outbox = await Prepare(ledger, work.Id, "first", "second");
        await Input(ledger, "new", "Use newer constraints.");
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Queued, state.InputWork.Single().Stage);
        Check(state.Outbox.All(x => x.Stage == OutboxStage.Superseded));
        await Error(LedgerErrors.StaleRevision, () => ledger.BeginOutboxSendAsync(Identity, outbox[0].Id));
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        Equal(1L, lease.Revision);
        state = await ledger.ReadConversationAsync(Identity);
        Equal(3L, state.InputWork.Single().ReadAtRevision!.Value);
        Equal(2L, state.InputWork.Single().Version);
    }

    private static async Task NewInputPartial()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var outbox = await Prepare(ledger, work.Id, "first", "second");
        await ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, outbox[0].Id, OutboxStage.Accepted, "accepted first");
        await Input(ledger, "new", "Only use these new constraints.");
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Superseded, state.InputWork.Single().Stage);
        Equal(OutboxStage.Accepted, state.Outbox.Single(x => x.Id == outbox[0].Id).Stage);
        Equal(OutboxStage.Superseded, state.Outbox.Single(x => x.Id == outbox[1].Id).Stage);
        Equal("first", state.Events.Single(x => x.RelatedOutboxId == outbox[0].Id).Body);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work.Id));
        await Error(LedgerErrors.StaleRevision, () => ledger.BeginOutboxSendAsync(Identity, outbox[1].Id));
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(InputWorkStage.Superseded, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
    }

    private static async Task LateAcceptance()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var outbox = await Prepare(ledger, work.Id, "first", "second");
        await ledger.BeginOutboxSendAsync(Identity, outbox[0].Id);
        await Input(ledger, "new", "Stop the old answer.");
        await ledger.RecordOutboxOutcomeAsync(Identity, outbox[0].Id, OutboxStage.Accepted, "late channel acceptance");
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(OutboxStage.Accepted, state.Outbox.Single(x => x.Id == outbox[0].Id).Stage);
        Equal(OutboxStage.Superseded, state.Outbox.Single(x => x.Id == outbox[1].Id).Stage);
        Check(state.InputWork.Single().Stage is InputWorkStage.Unknown or InputWorkStage.Superseded);
        Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work.Id));
    }

    private static async Task RejectedOrdinaryReply()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "old", "The original question was already answered by a send attempt.");
        var lease = await ledger.BeginGenerationAsync(Identity, ["old"]);
        await ledger.PrepareReplyAsync(lease, ["original ordinary reply"]);
        var reply = (await ledger.EnqueuePreparedReplyAsync(lease)).Single();
        Equal(OutboxKind.ConversationReply, reply.Kind);
        await ledger.BeginOutboxSendAsync(Identity, reply.Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, reply.Id, OutboxStage.Rejected, "definite channel rejection");
        await Input(ledger, "latest", "A later independent question.");
        var state = await ledger.ReadConversationAsync(Identity);
        var superseded = state.Outbox.Single();
        Equal(OutboxStage.Superseded, superseded.Stage);
        Equal("definite channel rejection", superseded.ChannelReceipt);
        Equal(InboxStage.HandedToOutbox, state.Inbox.Single(x => x.SourceMessageId == "old").Stage);
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        state = await ledger.ReadConversationAsync(Identity);
        Check(state.InputWork.IsEmpty);
        Equal(1, state.Outbox.Length);
        Equal(reply.Id, state.Outbox.Single().Id);
        Equal("The original question was already answered by a send attempt.",
            state.Inbox.Single(x => x.SourceMessageId == "old").Body);
    }

    private static async Task CompletedHistory()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var reply = (await Prepare(ledger, work.Id, "completed answer")).Single();
        await ledger.BeginOutboxSendAsync(Identity, reply.Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, reply.Id, OutboxStage.Accepted, "accepted completed answer");
        Equal(InputWorkStage.Completed, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        await Input(ledger, "new", "A new independent question.");
        Equal(InputWorkStage.Completed, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        await Input(ledger, "reset", "新对话");
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.Completed, state.InputWork.Single().Stage);
        Equal(OutboxStage.Accepted, state.Outbox.Single().Stage);
        Equal("completed answer", state.Events.Single(x => x.RelatedOutboxId == reply.Id).Body);
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(InputWorkStage.Completed, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        Check((await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.IsEmpty);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work.Id));
    }

    private static async Task ResetDuringModel()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "old", "An earlier question.");
        await Input(ledger, "reset", "新对话");
        var work = (await ledger.QueueUnprocessedInputsAsync(Identity)).Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptModel(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ToolModelResponse.Reply("must not cross the dialogue boundary");
        });
        var running = Loop(ledger, model, new RecordingExecutor(ledger)).RunReadOnlyWorkAsync(Identity, work.Id);
        await entered.Task;
        var before = (await ledger.ReadConversationAsync(Identity)).Revision;
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        Equal(ToolLoopState.Superseded, (await running).State);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(before, state.Revision);
        Equal(InputWorkStage.Cancelled, state.InputWork.Single().Stage);
        Check(state.Outbox.IsEmpty);
        Equal(1L, state.Context!.Epoch);
    }

    private static async Task ResetQueued()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "first", "first original");
        await Input(ledger, "second", "second original");
        await Input(ledger, "reset", "新对话");
        var work = await ledger.QueueUnprocessedInputsAsync(Identity);
        Equal(2, work.Length);
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.InputWork.All(x => x.Stage == InputWorkStage.Cancelled));
        Equal("first original", state.Inbox.Single(x => x.SourceMessageId == "first").Body);
        Equal("second original", state.Inbox.Single(x => x.SourceMessageId == "second").Body);
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work[0].Id));
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Check((await ledger.ReadConversationAsync(Identity)).InputWork.All(x => x.Stage == InputWorkStage.Cancelled));
    }

    private static async Task ConfirmationRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        await ledger.MarkReadOnlyWorkNeedsConfirmationAsync(lease, "A current restatement is required.");
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(InputWorkStage.NeedsConfirmation, state.InputWork.Single().Stage);
        Check(state.InputWork.Single().RequiresConfirmation);
        Check(state.Authorizations.IsEmpty && state.Operations.IsEmpty && state.Outbox.IsEmpty);
        await Input(ledger, "new", "What is the current status?");
        Equal(InputWorkStage.NeedsConfirmation, (await ledger.ReadConversationAsync(Identity)).InputWork.Single().Stage);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginReadOnlyWorkAsync(Identity, work.Id));
    }

    private static async Task UnknownEffect()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "old", "continue tests");
        var authorization = await ledger.AuthorizeCurrentUserRequestAsync(Identity, "old", "send_to_task",
            "local/thread-A", SendArgs, "continue tests", new("local", "thread-A"));
        var operation = await ledger.QueueOperationAsync(Identity, authorization.Id, "original-effect");
        await ledger.BeginOperationDispatchAsync(Identity, operation.Id);
        await Input(ledger, "latest", "Only inspect.");
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Check((await ledger.QueueUnprocessedInputsAsync(Identity)).IsEmpty);
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.InputWork.IsEmpty && state.Outbox.IsEmpty);
        Equal(1, state.Operations.Length);
        Equal(operation.Id, state.Operations.Single().Id);
        Equal(OperationStage.Unknown, state.Operations.Single().Stage);
        Equal(1, state.Authorizations.Length);
    }

    private static async Task Legacy()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "old", "original persisted input");
        await ledger.DisposeAsync();
        await Rewrite(f, root => root.AsObject().Remove("inputWork"));
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.InputWork.IsEmpty);
        Equal("original persisted input", state.Inbox.Single().Body);
        await Input(ledger, "latest", "current");
        Equal("old", (await ledger.QueueUnprocessedInputsAsync(Identity)).Single().SourceMessageId);
    }

    private static async Task CorruptReference()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var work = await Seed(ledger);
        await ledger.BeginReadOnlyWorkAsync(Identity, work.Id);
        await ledger.DisposeAsync();
        await Rewrite(f, root => root["inputWork"]![0]!["generationId"] = Guid.NewGuid().ToString());
        var before = await File.ReadAllBytesAsync(f.Path);
        await Error(LedgerErrors.CorruptStorage, async () => { await using var invalid = await f.Open(); });
        var after = await File.ReadAllBytesAsync(f.Path);
        Check(before.SequenceEqual(after));
    }

    private static async Task Capacity()
    {
        await using var f = new Fixture();
        var ledger = await f.Open(options: new() { MaximumPendingInputWork = 1 });
        await Input(ledger, "first", "first full original text");
        await Input(ledger, "second", "second full original text");
        await Input(ledger, "latest", "latest full original text");
        await Error(LedgerErrors.CapacityExceeded, () => ledger.QueueUnprocessedInputsAsync(Identity));
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.InputWork.IsEmpty);
        Check(state.Inbox.Select(x => x.Body).SequenceEqual(new[]
        {
            "first full original text", "second full original text", "latest full original text"
        }));
        Check(state.Inbox.All(x => x.Stage == InboxStage.Received));
        await ledger.DisposeAsync();
        ledger = await f.Open(options: new() { MaximumPendingInputWork = 1 });
        Equal(3, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
        Check((await ledger.ReadConversationAsync(Identity)).InputWork.IsEmpty);
    }

    private static async Task<ImmutableArray<OutboxMessage>> Prepare(DurableConversationLedger ledger,
        Guid workId, params string[] segments)
    {
        var lease = await ledger.BeginReadOnlyWorkAsync(Identity, workId);
        await ledger.PrepareReplyAsync(lease, segments);
        return await ledger.EnqueuePreparedReplyAsync(lease);
    }

    private static ToolAgentLoop Loop(DurableConversationLedger ledger, IToolModel model, IToolExecutor executor,
        IUserAuthorizationPolicy? policy = null, ToolLoopOptions? options = null) =>
        new(ledger, model, ToolCatalog.Initial(), executor, policy ?? new CountingPolicy(), options);

    private static ToolExecutionResult Facts(int readNumber) =>
        new(ToolExecutionState.ReadOnlySuccess, JsonSerializer.SerializeToElement(new { readNumber }));

    private sealed class ScriptModel(Func<ToolModelRequest, int, CancellationToken, Task<ToolModelResponse>> step) : IToolModel
    {
        internal List<ToolModelRequest> Requests { get; } = [];
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return step(request, Requests.Count - 1, cancellationToken);
        }
        internal static ScriptModel Reply() => new((_, _, _) => Task.FromResult(ToolModelResponse.Reply("Read complete.")));
        internal static ScriptModel ReadThenReply() => new((_, index, _) => Task.FromResult(index == 0
            ? ToolModelResponse.Call(new ModelToolCall("same-model-call", "read_task", ReadArgs))
            : ToolModelResponse.Reply("Read complete.")));
    }

    private sealed class RecordingExecutor(DurableConversationLedger ledger) : IToolExecutor
    {
        internal List<(ToolExecutionContext Context, ModelToolCall Call)> Calls { get; } = [];
        internal Func<ToolExecutionContext, ModelToolCall, CancellationToken, Task<ToolExecutionResult>>? Handler { get; init; }
        public ToolCapability GetCapability(string capability) => new(true);
        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken)
        {
            Check(await ledger.VerifyReadOnlyWorkExecutionAsync(context, cancellationToken));
            Calls.Add((context, call));
            return Handler is { } handler ? await handler(context, call, cancellationToken) : Facts(Calls.Count);
        }
    }

    private sealed class CountingPolicy : IUserAuthorizationPolicy
    {
        internal int Calls;
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new UserAuthorizationDecision(true, "local/thread-A",
                request.CurrentMessage.Body, new("local", "thread-A")));
        }
    }

    private static async Task Rewrite(Fixture f, Action<JsonNode> mutate)
    {
        await using var storage = new FileJournalStorage(f.Path);
        var latest = (await storage.ReadLatestAsync())!;
        var protector = new TestEncryption(f.Key);
        byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{sequence}");
        var plaintext = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
        var root = JsonNode.Parse(plaintext)!;
        mutate(root);
        var modified = Encoding.UTF8.GetBytes(root.ToJsonString());
        await storage.AppendAsync(latest.Sequence, protector.Protect(modified, Purpose(latest.Sequence + 1)));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(modified);
    }

    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (LedgerException error) { Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
