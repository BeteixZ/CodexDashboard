using System.Text.Json;
using Assistant.Core;

internal static partial class OutputDependencyTests
{
    private static (string, Func<Task>)[] AbandonCases() =>
    [
        ("queue_to_begin_dependency_change_abandons_without_dispatch", () => PreDispatchRace("invalid")),
        ("queue_to_begin_dependency_unavailable_abandons_without_dispatch", () => PreDispatchRace("unavailable")),
        ("queue_to_begin_expiry_abandons_without_dispatch", () => PreDispatchRace("expiry")),
        ("queue_to_begin_cancel_abandons_without_dispatch", () => PreDispatchRace("cancel")),
        ("queue_to_begin_new_input_cleans_only_original_attempt", () => PreDispatchRace("input")),
        ("queue_to_begin_capability_withdrawal_abandons_without_dispatch", () => PreDispatchRace("capability")),
        ("abandoned_operation_is_not_replayed_under_new_call_id", AbandonedNoReplay),
        ("cleanup_exact_scope_and_no_guessed_null_operation", CleanupScope),
        ("cleanup_never_changes_dispatched_or_unknown_records", CleanupDispatched),
        ("concurrent_cleanup_commits_one_not_started_receipt", CleanupConcurrent),
        ("real_cleanup_flush_recovers_atomic_unavailable_and_cancelled", CleanupFlush),
        ("real_begin_flush_fault_recovers_unknown_not_false_cancelled", BeginFlushGap),
        ("queue_flush_fault_with_no_returned_id_never_guesses_cleanup", QueueFlushGap),
        ("recovered_prepared_call_rebinds_before_exact_cleanup", RecoveredPrepared),
        ("readonly_prepared_cleanup_is_audited_without_operation", ReadonlyCleanup),
        ("late_old_lease_cannot_dispatch_or_abandon_rebound_invocation", LateOldLease),
        ("cleanup_before_write_failure_keeps_known_queue_but_stale_lease_cannot_dispatch", CleanupBeforeWrite)
    ];
    private sealed class GateExecutor(DurableConversationLedger ledger, Action callback, bool deny = false) : IToolExecutor
    {
        internal int Calls;
        internal bool GateRan;
        public ToolCapability GetCapability(string capability)
        {
            if (capability == "remember" && !GateRan && ledger.ReadConversationAsync(Identity).GetAwaiter().GetResult()
                .Operations.Any(x => x.Stage == OperationStage.Queued))
            { GateRan = true; callback(); return new(!deny); }
            return new(true);
        }
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new ToolExecutionResult(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new { synthetic = true }))); }
    }
    private static async Task PreDispatchRace(string kind)
    {
        await using var f = new F();
        if (kind == "expiry") f.Provider.Current = Bound(expiry: f.Clock.UtcNow.AddMinutes(1));
        var l = await f.Open(); await Input(l); using var cancellation = new CancellationTokenSource();
        var executor = new GateExecutor(l, () =>
        {
            if (kind == "invalid") f.Provider.Current = Bound(2);
            if (kind == "unavailable") f.Provider.Forced = OutputDependencyStatus.Unavailable;
            if (kind == "expiry") f.Clock.UtcNow += TimeSpan.FromMinutes(1);
            if (kind == "cancel") cancellation.Cancel();
            if (kind == "input") Input(l, "new-input").GetAwaiter().GetResult();
        }, kind == "capability");
        var model = new Model((_, index) => Task.FromResult(index == 0 ? ToolModelResponse.Call(Remember()) : ToolModelResponse.Reply("The tool was unavailable.")));
        var loop = new ToolAgentLoop(l, model, ToolCatalog.Initial(), executor, new Policy(), contextProvider: f.Provider);
        var result = await loop.RunAsync(Identity, "input", cancellationToken: cancellation.Token);
        Check(executor.GateRan); Equal(0, executor.Calls);
        Equal(kind == "cancel" ? ToolLoopState.Cancelled : kind == "capability" ? ToolLoopState.Completed : ToolLoopState.Superseded, result.State);
        var s = await l.ReadConversationAsync(Identity); var operation = s.Operations.Single(); var invocation = s.ToolInvocations.Single();
        Equal(OperationStage.CancelledBeforeDispatch, operation.Stage); Equal(ToolInvocationStage.Unavailable, invocation.Stage);
        Equal(operation.Id, invocation.OperationId); Equal("not_started", JsonDocument.Parse(operation.Receipt!).RootElement.GetProperty("status").GetString());
        Check(!s.Events.Where(x => x.Source == ConversationSource.ToolResult).Any(x => x.Body.Contains("\"status\":\"completed\"", StringComparison.Ordinal)));
        await l.DisposeAsync(); l = await f.Open(); Equal(0, (await l.GetRecoveryPlanAsync(Identity)).QueuedOperations.Length);
        Equal(OperationStage.CancelledBeforeDispatch, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage);
    }
    private static async Task AbandonedNoReplay()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l);
        var e = new GateExecutor(l, () => f.Provider.Current = Bound(2));
        var first = new Model((_, _) => Task.FromResult(ToolModelResponse.Call(Remember())));
        await new ToolAgentLoop(l, first, ToolCatalog.Initial(), e, new Policy(), contextProvider: f.Provider).RunAsync(Identity, "input");
        var second = new Model((_, index) => Task.FromResult(index == 0 ? ToolModelResponse.Call(Remember("new-call")) : ToolModelResponse.Reply("No action was dispatched.")));
        var real = new Executor(); await Loop(l, second, f.Provider, real).RunAsync(Identity, "input");
        Equal(0, real.Calls.Count); var snapshot = await l.ReadConversationAsync(Identity);
        Equal(1, snapshot.Operations.Length); Equal(OperationStage.CancelledBeforeDispatch, snapshot.Operations.Single().Stage);
        Check(snapshot.ToolInvocations.All(x => x.Stage == ToolInvocationStage.Unavailable));
    }
    private static async Task<(GenerationLease Generation, ToolInvocationRecord Invocation, OperationRecord Operation)> Queued(DurableConversationLedger l)
    {
        var g = await Generation(l); var call = Remember();
        var invocation = await l.RegisterToolInvocationAsync(g, "input", call.Id, call.Name, call.ArgumentsJson, true);
        var grant = await l.AuthorizeCurrentUserRequestAsync(Identity, "input", call.Name, "synthetic-memory", call.ArgumentsJson, "remember synthetic preference");
        var operation = await l.QueueOperationAsync(Identity, grant.Id, "queued-memory"); return (g, invocation, operation);
    }
    private static async Task CleanupScope()
    {
        await using var f = new F(); var l = await f.Open(); var p = await Queued(l);
        Check(!await l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, null, "no returned operation ID"));
        await Error(LedgerErrors.IdentityMismatch, () => l.AbandonPreparedToolInvocationAsync(p.Generation with { Id = Guid.NewGuid() }, p.Invocation.Id, p.Operation.Id, "foreign lease"));
        var other = await l.RegisterToolInvocationAsync(p.Generation, "input", "other-call", "forget", "{\"memoryId\":\"different\"}", true);
        await Error(LedgerErrors.IdentityMismatch, () => l.AbandonPreparedToolInvocationAsync(p.Generation, other.Id, p.Operation.Id, "wrong operation"));
        var snapshot = await l.ReadConversationAsync(Identity); Equal(OperationStage.Queued, snapshot.Operations.Single().Stage);
        Check(snapshot.ToolInvocations.All(x => x.Stage == ToolInvocationStage.Prepared));
    }
    private static async Task CleanupDispatched()
    {
        foreach (bool unknown in new[] { false, true })
        {
            await using var f = new F(); var l = await f.Open(); var p = await Queued(l); await l.BeginToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id);
            if (unknown) await l.FinishToolInvocationAsync(Identity, p.Invocation.Id, ToolInvocationStage.Unknown, "{}", OperationStage.Unknown);
            Check(!await l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "too late"));
            var snapshot = await l.ReadConversationAsync(Identity); Equal(unknown ? OperationStage.Unknown : OperationStage.Dispatching, snapshot.Operations.Single().Stage);
            Equal(unknown ? ToolInvocationStage.Unknown : ToolInvocationStage.Executing, snapshot.ToolInvocations.Single().Stage);
        }
    }
    private static async Task CleanupConcurrent()
    {
        await using var f = new F(); var l = await f.Open(); var p = await Queued(l);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "host admission withdrawn")));
        Equal(1, results.Count(x => x)); Equal(1, (await l.ReadConversationAsync(Identity)).Events.Count(x => x.Source == ConversationSource.ToolResult));
    }
    private static async Task CleanupFlush()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault: fault); var p = await Queued(l);
        fault.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "not dispatched"));
        await l.DisposeAsync(); l = await f.Open(); var snapshot = await l.ReadConversationAsync(Identity);
        Equal(OperationStage.CancelledBeforeDispatch, snapshot.Operations.Single().Stage); Equal(ToolInvocationStage.Unavailable, snapshot.ToolInvocations.Single().Stage);
        Equal(p.Operation.Id, snapshot.ToolInvocations.Single().OperationId); Equal(0, (await l.GetRecoveryPlanAsync(Identity)).QueuedOperations.Length);
    }
    private static async Task BeginFlushGap()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault: fault); await Input(l);
        var e = new GateExecutor(l, () => fault.Stage = JournalWriteStage.AfterFlush);
        var m = new Model((_, _) => Task.FromResult(ToolModelResponse.Call(Remember())));
        await Throws<InjectedFailure>(() => new ToolAgentLoop(l, m, ToolCatalog.Initial(), e, new Policy(), contextProvider: f.Provider).RunAsync(Identity, "input"));
        Equal(0, e.Calls); await l.DisposeAsync(); l = await f.Open();
        Equal(OperationStage.Unknown, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage);
        Equal(ToolInvocationStage.Unknown, (await l.ReadConversationAsync(Identity)).ToolInvocations.Single().Stage);
    }
    private static async Task QueueFlushGap()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault: fault); var g = await Generation(l); var call = Remember();
        var invocation = await l.RegisterToolInvocationAsync(g, "input", call.Id, call.Name, call.ArgumentsJson, true);
        var grant = await l.AuthorizeCurrentUserRequestAsync(Identity, "input", call.Name, "synthetic-memory", call.ArgumentsJson, "remember synthetic preference");
        fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(() => l.QueueOperationAsync(Identity, grant.Id, "uncertain-queue"));
        await Error(LedgerErrors.RecoveryRequired, () => l.AbandonPreparedToolInvocationAsync(g, invocation.Id, null, "queue did not return"));
        await l.DisposeAsync(); l = await f.Open(); Check(!await l.AbandonPreparedToolInvocationAsync(g, invocation.Id, null, "still no exact operation ID"));
        Equal(OperationStage.Queued, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage); Equal(ToolInvocationStage.Prepared, (await l.ReadConversationAsync(Identity)).ToolInvocations.Single().Stage);
    }
    private static async Task RecoveredPrepared()
    {
        await using var f = new F(); var l = await f.Open(); var p = await Queued(l); await l.DisposeAsync(); l = await f.Open();
        var next = await l.BeginGenerationAsync(Identity, ["input"], outputDependencies: Bound());
        var same = await l.RegisterToolInvocationAsync(next, "input", p.Invocation.CallId, p.Invocation.ToolName, p.Invocation.ArgumentsJson, true);
        Equal(p.Invocation.Id, same.Id); Equal(next.Id, same.GenerationId);
        Check(await l.AbandonPreparedToolInvocationAsync(next, same.Id, p.Operation.Id, "recovered admission rejected"));
        await l.DisposeAsync(); Check(!(await (await f.Open()).ReadConversationAsync(Identity)).IsReadOnly);
    }
    private static async Task ReadonlyCleanup()
    {
        await using var f = new F(); var l = await f.Open(); var generation = await Generation(l);
        var invocation = await l.RegisterToolInvocationAsync(generation, "input", "read", "list_tasks", "{}", false);
        Check(await l.AbandonPreparedToolInvocationAsync(generation, invocation.Id, null, "host read cancelled before admission"));
        var snapshot = await l.ReadConversationAsync(Identity); Equal(0, snapshot.Operations.Length); Equal(ToolInvocationStage.Unavailable, snapshot.ToolInvocations.Single().Stage);
        Equal(invocation.Id, snapshot.Events.Single(x => x.Source == ConversationSource.ToolResult).RelatedInvocationId);
    }
    private static async Task LateOldLease()
    {
        await using var f = new F(); var l = await f.Open(); var p = await Queued(l);
        await l.EndGenerationAttemptAsync(p.Generation);
        var next = await l.BeginGenerationAsync(Identity, ["input"], outputDependencies: Bound());
        await l.RegisterToolInvocationAsync(next, "input", p.Invocation.CallId, p.Invocation.ToolName, p.Invocation.ArgumentsJson, true);
        await Error(LedgerErrors.InvalidTransition, () => l.BeginToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id));
        await Error(LedgerErrors.IdentityMismatch, () => l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "late old cleanup"));
        Equal(OperationStage.Queued, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage);
        var started = await l.BeginToolInvocationAsync(next, p.Invocation.Id, p.Operation.Id);
        Equal(next.Id, started.GenerationId); Equal(ToolInvocationStage.Executing, started.Stage);
    }
    private static async Task CleanupBeforeWrite()
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault: fault); var p = await Queued(l);
        f.Provider.Current = Bound(2); fault.Stage = JournalWriteStage.BeforeWrite;
        await Throws<InjectedFailure>(() => l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "not admitted"));
        await l.DisposeAsync(); l = await f.Open(); var snapshot = await l.ReadConversationAsync(Identity);
        Equal(OperationStage.Queued, snapshot.Operations.Single().Stage); Equal(ToolInvocationStage.Prepared, snapshot.ToolInvocations.Single().Stage);
        Check(snapshot.Operations.Single().Receipt == null);
        await Error(LedgerErrors.DependencyInvalidated, () => l.BeginToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id));
        Check(await l.AbandonPreparedToolInvocationAsync(p.Generation, p.Invocation.Id, p.Operation.Id, "recovered exact unstarted attempt"));
        Equal(OperationStage.CancelledBeforeDispatch, (await l.ReadConversationAsync(Identity)).Operations.Single().Stage);
    }
}
