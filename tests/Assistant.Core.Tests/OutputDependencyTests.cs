using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static partial class OutputDependencyTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly (string, Func<Task>)[] Cases =
    [
        ("strict_legacy_unknown_cannot_model_prepare_or_send", StrictLegacy),
        ("strict_requirement_survives_option_downgrade", StickyRequirement),
        ("explicit_independent_facts_need_no_validator", Independent),
        ("capture_is_once_and_persists_before_model", CaptureOnce),
        ("invalid_after_model_discards_final_reply", ModelChange),
        ("invalid_before_next_tool_prevents_second_effect", BetweenTools),
        ("filter_can_remove_events_observations_and_focus_projection", FilterProjection),
        ("readonly_result_has_exact_invocation_dependency_provenance", ReadResult),
        ("capture_exception_fails_closed_and_releases", CaptureFailure),
        ("release_exception_preserves_completed_result", ReleaseFailure),
        ("prepared_reply_invalid_before_handoff_is_durably_superseded", PreparedChange),
        ("send_guard_persists_invalidation_before_throwing", SendChange),
        ("accepted_prefix_preserved_remaining_segment_superseded", AcceptedPrefix),
        ("sending_real_receipt_survives_context_revocation", InflightReceipt),
        ("unknown_remains_unknown_after_revocation_and_restart", Unknown),
        ("missing_validator_holds_restart_until_current_verification", RestartUnavailable),
        ("expiry_is_invalid_even_when_host_validator_says_valid", Expiry),
        ("unavailable_is_not_false_revocation_or_history_rewrite", Unavailable),
        ("notification_finalize_requires_actual_captured_manifest", NotificationFinalize),
        ("fallback_fact_is_explicitly_independent", NotificationFallback),
        ("finalized_notification_cannot_be_resigned_with_new_context", NotificationResign),
        ("bound_catchup_page_inherits_original_sources", CatchInheritance),
        ("catchup_conflicting_contexts_never_become_independent", CatchConflict),
        ("span_catchup_revocation_preserves_accepted_and_unknown_ranges", SpanCatchRevocation),
        ("span_catchup_restart_missing_validator_then_expiry_never_resigns", SpanCatchRestartExpiry),
        ("valid_memory_transition_advances_epoch_and_discards_old_calls", ValidTransition),
        ("model_facts_cannot_forge_typed_transition", FactsDoNotTransition),
        ("typed_transition_wrong_invocation_rejected", () => BadTransition("invocation")),
        ("typed_transition_wrong_before_rejected", () => BadTransition("before")),
        ("accepted_operation_cannot_advance_epoch", () => BadTransition("accepted")),
        ("typed_transition_host_denial_rejected", () => BadTransition("host")),
        ("typed_transition_same_revision_changed_fingerprint_rejected", () => BadTransition("revision")),
        ("typed_transition_readonly_cannot_advance_epoch", () => BadTransition("readonly")),
        ("generation_capture_cannot_be_rebound", CaptureImmutable),
        ("schema_three_never_downgrades_on_catchup", VersionGate),
        ("corrupt_dependency_context_epoch_and_provenance_fail_closed", Corruption),
        ("actual_file_after_flush_bind_recovers_exact_capture", () => FlushRecovery("bind")),
        ("actual_file_after_flush_prepare_recovers_manifest", () => FlushRecovery("prepare")),
        ("actual_file_after_flush_send_recovers_unknown", () => FlushRecovery("send")),
        ("actual_file_after_flush_transition_recovers_new_epoch", () => FlushRecovery("transition")),
        ("structural_equivalence_and_merge_never_drop_unknown", Equivalence),
        ..AbandonCases()
    ];
    internal static async Task<int> Run()
    {
        int failures = 0;
        foreach (var (name, action) in Cases)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS dependencies/" + name); }
            catch (Exception error) { failures++; Console.WriteLine($"FAIL dependencies/{name}: {error}"); }
        }
        Console.WriteLine($"DEPENDENCIES TOTAL {Cases.Length}; PASSED {Cases.Length - failures}; FAILED {failures}");
        return failures;
    }
    private static OutputDependencies Bound(long version = 1, DateTimeOffset? expiry = null) =>
        new(OutputDependencyMode.Bound, [new("memory", "synthetic-scope", version, "fingerprint-" + version, expiry, [new("memory-one", version)])]);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = FakeClock.Now; }
    private sealed class Provider : IModelContextProvider
    {
        internal OutputDependencies Current = Bound();
        internal OutputDependencyStatus? Forced;
        internal int Captures, Releases, Transitions;
        internal bool CaptureThrows, ReleaseThrows, AllowTransition = true, EmptyProjection;
        internal Action? OnFilter { get; set; }
        public OutputDependencyValidation Validate(ConversationIdentity identity, OutputDependencies dependencies) =>
            new(Forced ?? (OutputDependencies.Equivalent(Current, dependencies) ? OutputDependencyStatus.Valid : OutputDependencyStatus.Invalid));
        public ValueTask<OutputDependencies> CaptureAsync(GenerationLease generation, CancellationToken cancellationToken)
        { Captures++; if (CaptureThrows) throw new IOException("synthetic failure"); return ValueTask.FromResult(Current); }
        public ValueTask<ModelContextSelection> FilterAsync(ConversationIdentity identity, GenerationContext context,
            ImmutableArray<ConversationEvent> events, ImmutableArray<ModelToolObservation> observations, CancellationToken cancellationToken)
        { OnFilter?.Invoke(); return ValueTask.FromResult(EmptyProjection ? new ModelContextSelection([], []) : new(events, observations)); }
        public ValueTask<bool> AcceptTransitionAsync(ConversationIdentity identity, GenerationContext current,
            ContextDependencyTransition transition, CancellationToken cancellationToken)
        { Transitions++; return ValueTask.FromResult(AllowTransition && OutputDependencies.Equivalent(Current, transition.After)); }
        public void Release(GenerationLease generation) { Releases++; if (ReleaseThrows) throw new IOException("synthetic cache release"); }
    }
    private sealed class Model(Func<ToolModelRequest, int, Task<ToolModelResponse>> handler) : IToolModel
    {
        internal List<ToolModelRequest> Requests = [];
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return handler(request, Requests.Count - 1); }
    }
    private sealed class Executor : IToolExecutor
    {
        internal List<ToolExecutionContext> Calls = [];
        internal Func<ToolExecutionContext, ModelToolCall, Task<ToolExecutionResult>>? Handler;
        public ToolCapability GetCapability(string capability) => new(true);
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken)
        { Calls.Add(context); return Handler?.Invoke(context, call) ?? Task.FromResult(new ToolExecutionResult(ToolExecutionState.ReadOnlySuccess, JsonSerializer.SerializeToElement(new { fact = "synthetic result" }))); }
    }
    private sealed class Policy : IUserAuthorizationPolicy
    {
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserAuthorizationDecision(true, "synthetic-memory", request.CurrentMessage.Body));
    }
    private sealed class F : IAsyncDisposable
    {
        internal readonly Fixture Files = new();
        internal readonly Clock Clock = new();
        internal readonly Provider Provider = new();
        internal Task<DurableConversationLedger> Open(bool strict = true, bool validator = true, FaultInjector? fault = null) =>
            Files.Open(fault, new() { RequireOutputDependencies = strict }, validator ? Provider : null, Clock);
        internal async Task Rewrite(Action<JsonNode> mutate)
        {
            await using var storage = new FileJournalStorage(Files.Path); var latest = (await storage.ReadLatestAsync())!;
            var protector = new TestEncryption(Files.Key);
            byte[] Purpose(long seq) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{seq}");
            var plain = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
            try
            {
                var root = JsonNode.Parse(plain)!; mutate(root);
                await storage.AppendAsync(latest.Sequence, protector.Protect(Encoding.UTF8.GetBytes(root.ToJsonString()), Purpose(latest.Sequence + 1)));
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        }
        public ValueTask DisposeAsync() => Files.DisposeAsync();
    }
    private static async Task Input(DurableConversationLedger l, string id = "input", bool catchup = false) =>
        _ = await l.ReceiveAsync(Identity, id, "remember synthetic preference", MessageTrust.AuthenticatedUser, FakeClock.Now,
            catchUp: catchup ? new(CatchUpDisposition.AllowHistory, true, "UTC") : null);
    private static async Task<GenerationLease> Generation(DurableConversationLedger l, OutputDependencies? dependencies = null)
    { await Input(l); return await l.BeginGenerationAsync(Identity, ["input"], outputDependencies: dependencies ?? Bound()); }
    private static async Task<ImmutableArray<OutboxMessage>> Reply(DurableConversationLedger l, params string[] parts)
    { var g = await Generation(l); await l.PrepareReplyAsync(g, parts.Length == 0 ? ["synthetic reply"] : parts); return await l.EnqueuePreparedReplyAsync(g); }
    private static ToolAgentLoop Loop(DurableConversationLedger l, Model m, Provider? p, Executor? executor = null) =>
        new(l, m, ToolCatalog.Initial(), executor ?? new(), new Policy(), contextProvider: p);
    private static Model FinalModel() => new((_, _) => Task.FromResult(ToolModelResponse.Reply("synthetic reply")));
    private static Model ReadModel() => new((_, i) => Task.FromResult(i == 0 ? ToolModelResponse.Call(new ModelToolCall("read", "list_tasks", "{}")) : ToolModelResponse.Reply("synthetic reply")));
    private static ModelToolCall Remember(string id = "remember") => new(id, "remember", "{\"text\":\"synthetic preference\",\"scope\":\"user\"}");
    private static async Task<OutboxMessage> Notice(DurableConversationLedger l, OutputDependencies? dependencies = null, string key = "notice") =>
        (await l.EnqueueNotificationAsync(Identity, key, "synthetic complete original report", null, outputDependencies: dependencies ?? Bound())).Outbox.Single();
    private static async Task Outcome(DurableConversationLedger l, OutboxMessage o, OutboxStage stage)
    { await l.BeginOutboxSendAsync(Identity, o.Id); await l.RecordOutboxOutcomeAsync(Identity, o.Id, stage, "synthetic transport receipt"); }
    private static async Task StrictLegacy()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var m = FinalModel();
        Equal(ToolLoopState.Superseded, (await Loop(l, m, null).RunAsync(Identity, "input")).State); Equal(0, m.Requests.Count);
        var g = await l.BeginGenerationAsync(Identity, ["input"]);
        await Error(LedgerErrors.DependencyUnavailable, () => l.PrepareReplyAsync(g, ["unknown provenance"]));
        await Error(LedgerErrors.DependencyUnavailable, () => l.EnqueueNotificationAsync(Identity, "unknown", "legacy", null));
        Equal(0, (await l.ReadConversationAsync(Identity)).Outbox.Length);
    }
    private static async Task StickyRequirement()
    { await using var f = new F(); var l = await f.Open(); await l.DisposeAsync(); l = await f.Open(strict: false); Check((await l.ReadConversationAsync(Identity)).OutputDependenciesRequired); await Error(LedgerErrors.DependencyUnavailable, () => l.EnqueueNotificationAsync(Identity, "unknown", "legacy", null)); }
    private static async Task Independent()
    { await using var f = new F(); var l = await f.Open(validator: false); var o = await Notice(l, OutputDependencies.Independent); await Outcome(l, o, OutboxStage.Accepted); Equal(OutputDependencyMode.Independent, DurableConversationLedger.ResolveOutputDependencies(await l.ReadConversationAsync(Identity), (await l.ReadConversationAsync(Identity)).Outbox.Single()).Mode); }
    private static async Task CaptureOnce()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var m = ReadModel();
        var r = await Loop(l, m, f.Provider).RunAsync(Identity, "input"); Equal(ToolLoopState.Completed, r.State); Equal(1, f.Provider.Captures); Equal(1, f.Provider.Releases);
        Check(m.Requests.All(x => x.GenerationContext?.GenerationId == r.Generation.Id && x.GenerationContext.Epoch == 0));
        Check(OutputDependencies.Equivalent(Bound(), (await l.ReadConversationAsync(Identity)).Generations.Single().GenerationContext!.Dependencies));
    }
    private static async Task ModelChange()
    { await using var f = new F(); var l = await f.Open(); await Input(l); var m = new Model((_, _) => { f.Provider.Current = Bound(2); return Task.FromResult(ToolModelResponse.Reply("old preference")); }); var r = await Loop(l, m, f.Provider).RunAsync(Identity, "input"); Equal(ToolLoopState.Superseded, r.State); Equal(0, r.Outbox.Length); Equal(GenerationStage.Superseded, (await l.ReadConversationAsync(Identity)).Generations.Single().Stage); }
    private static async Task BetweenTools()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var e = new Executor { Handler = (_, _) => { f.Provider.Current = Bound(2); return Task.FromResult(new ToolExecutionResult(ToolExecutionState.ReadOnlySuccess, JsonSerializer.SerializeToElement(new { fact = true }))); } };
        var m = new Model((_, _) => Task.FromResult(ToolModelResponse.Call(new("read", "list_tasks", "{}"), Remember())));
        Equal(ToolLoopState.Superseded, (await Loop(l, m, f.Provider, e).RunAsync(Identity, "input")).State); Equal(1, e.Calls.Count); Equal(0, (await l.ReadConversationAsync(Identity)).Operations.Length);
    }
    private static async Task FilterProjection()
    {
        await using var f = new F(); f.Provider.EmptyProjection = true; var l = await f.Open(); await Input(l);
        await l.AppendContextAsync(Identity, ConversationSource.Report, "revoked synthetic context", FakeClock.Now);
        var m = ReadModel(); await Loop(l, m, f.Provider).RunAsync(Identity, "input");
        Check(m.Requests.All(x => x.Context.Length == 0 && x.Observations.Length == 0 && x.ConversationContext == null));
    }
    private static async Task ReadResult()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var m = ReadModel(); await Loop(l, m, f.Provider).RunAsync(Identity, "input");
        var s = await l.ReadConversationAsync(Identity); var invocation = s.ToolInvocations.Single(); var entry = s.Events.Single(x => x.Source == ConversationSource.ToolResult);
        Equal(invocation.Id, entry.RelatedInvocationId); Check(entry.RelatedOperationId == null); Check(OutputDependencies.Equivalent(entry.OutputDependencies, invocation.GenerationContext!.Dependencies)); Equal(invocation.Id, m.Requests[1].Observations.Single().InvocationId);
        await l.DisposeAsync(); Check(!(await (await f.Open()).ReadConversationAsync(Identity)).IsReadOnly);
    }
    private static async Task CaptureFailure()
    { await using var f = new F(); f.Provider.CaptureThrows = true; var l = await f.Open(); await Input(l); var m = FinalModel(); Equal(ToolLoopState.Superseded, (await Loop(l, m, f.Provider).RunAsync(Identity, "input")).State); Equal(0, m.Requests.Count); Equal(1, f.Provider.Releases); }
    private static async Task ReleaseFailure()
    { await using var f = new F(); f.Provider.ReleaseThrows = true; var l = await f.Open(); await Input(l); Equal(ToolLoopState.Completed, (await Loop(l, FinalModel(), f.Provider).RunAsync(Identity, "input")).State); Equal(1, (await l.ReadConversationAsync(Identity)).Outbox.Length); }
    private static async Task PreparedChange()
    { await using var f = new F(); var l = await f.Open(); var g = await Generation(l); await l.PrepareReplyAsync(g, ["old preference"]); f.Provider.Current = Bound(2); await Error(LedgerErrors.DependencyInvalidated, () => l.EnqueuePreparedReplyAsync(g)); Equal(GenerationStage.Superseded, (await l.ReadConversationAsync(Identity)).Generations.Single().Stage); await l.DisposeAsync(); Equal(0, (await (await f.Open()).GetRecoveryPlanAsync(Identity)).PreparedReplies.Length); }
    private static async Task SendChange()
    { await using var f = new F(); var l = await f.Open(); var o = (await Reply(l)).Single(); f.Provider.Current = Bound(2); await Error(LedgerErrors.DependencyInvalidated, () => l.BeginOutboxSendAsync(Identity, o.Id)); var result = (await l.ReadConversationAsync(Identity)).Outbox.Single(); Equal(OutboxStage.Superseded, result.Stage); Equal(0, result.SendAttempts); Check(result.DependencyInvalidationReason != null); }
    private static async Task AcceptedPrefix()
    { await using var f = new F(); var l = await f.Open(); var o = await Reply(l, "page one", "page two"); await Outcome(l, o[0], OutboxStage.Accepted); f.Provider.Current = Bound(2); await l.RefreshOutputDependenciesAsync(Identity); var s = await l.ReadConversationAsync(Identity); Equal(OutboxStage.Accepted, s.Outbox[0].Stage); Equal(OutboxStage.Superseded, s.Outbox[1].Stage); }
    private static async Task InflightReceipt()
    { await using var f = new F(); var l = await f.Open(); var o = await Notice(l); await l.BeginOutboxSendAsync(Identity, o.Id); f.Provider.Current = Bound(2); Equal(0, await l.RefreshOutputDependenciesAsync(Identity)); await l.RecordOutboxOutcomeAsync(Identity, o.Id, OutboxStage.Accepted, "actual peer accepted"); Equal(OutboxStage.Accepted, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage); }
    private static async Task Unknown()
    { await using var f = new F(); var l = await f.Open(); var o = await Notice(l); await l.BeginOutboxSendAsync(Identity, o.Id); await l.DisposeAsync(); f.Provider.Current = Bound(2); l = await f.Open(); Equal(OutboxStage.Unknown, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage); Equal(0, (await l.RetryRejectedNotificationsAsync(Identity)).Length); Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length); }
    private static async Task RestartUnavailable()
    { await using var f = new F(); var l = await f.Open(); await Notice(l); await l.DisposeAsync(); l = await f.Open(validator: false); Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage); Equal(0, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length); await l.DisposeAsync(); l = await f.Open(); Equal(1, (await l.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length); }
    private static async Task Expiry()
    { await using var f = new F(); f.Provider.Current = Bound(expiry: f.Clock.UtcNow.AddMinutes(1)); var l = await f.Open(); var o = await Notice(l, f.Provider.Current); f.Clock.UtcNow += TimeSpan.FromMinutes(1); f.Provider.Forced = OutputDependencyStatus.Valid; await Error(LedgerErrors.DependencyInvalidated, () => l.BeginOutboxSendAsync(Identity, o.Id)); Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage); }
    private static async Task Unavailable()
    { await using var f = new F(); var l = await f.Open(); var o = await Notice(l); f.Provider.Forced = OutputDependencyStatus.Unavailable; await Error(LedgerErrors.DependencyUnavailable, () => l.BeginOutboxSendAsync(Identity, o.Id)); Equal(0, await l.RefreshOutputDependenciesAsync(Identity)); Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single().Stage); f.Provider.Forced = null; await Outcome(l, o, OutboxStage.Accepted); }
    private static async Task NotificationFinalize()
    { await using var f = new F(); var l = await f.Open(); var c = await l.CaptureNotificationAsync(Identity, "draft", "public facts", "{}", null); var lease = await l.BeginNotificationPreparationAsync(Identity, c.Draft.Id); await Error(LedgerErrors.DependencyUnavailable, () => l.FinalizeNotificationAsync(lease, "personalized summary")); var result = await l.FinalizeNotificationAsync(lease, "personalized summary", outputDependencies: Bound()); Check(OutputDependencies.Equivalent(Bound(), result.Event.OutputDependencies)); f.Provider.Current = Bound(2); await Error(LedgerErrors.DependencyInvalidated, () => l.BeginOutboxSendAsync(Identity, result.Outbox.Single().Id)); }
    private static async Task NotificationFallback()
    { await using var f = new F(); var l = await f.Open(validator: false); var c = await l.CaptureNotificationAsync(Identity, "fallback", "public facts", "{}", null); var o = await l.FinalizeNotificationFallbackAsync(Identity, c.Draft.Id, "public facts"); Equal(OutputDependencyMode.Independent, o.Event.OutputDependencies!.Mode); await Outcome(l, o.Outbox.Single(), OutboxStage.Accepted); }
    private static async Task NotificationResign()
    { await using var f = new F(); var l = await f.Open(); var c = await l.CaptureNotificationAsync(Identity, "draft", "public facts", "{}", null); var lease = await l.BeginNotificationPreparationAsync(Identity, c.Draft.Id); await l.FinalizeNotificationAsync(lease, "summary", outputDependencies: Bound()); f.Provider.Current = Bound(2); await Error(LedgerErrors.Conflict, () => l.FinalizeNotificationAsync(lease, "summary", outputDependencies: Bound(2))); Check(OutputDependencies.Equivalent(Bound(), (await l.ReadConversationAsync(Identity)).Events.Single().OutputDependencies)); }
    private static async Task CatchInheritance()
    { await using var f = new F(); var l = await f.Open(); var o = await Notice(l); await Outcome(l, o, OutboxStage.Rejected); await Input(l, "return", true); var batch = await l.PrepareCatchUpAsync(Identity, "return"); var page = batch.Outbox.Single(); Check(OutputDependencies.Equivalent(Bound(), DurableConversationLedger.ResolveOutputDependencies(await l.ReadConversationAsync(Identity), page))); f.Provider.Current = Bound(2); await Error(LedgerErrors.DependencyInvalidated, () => l.BeginOutboxSendAsync(Identity, page.Id)); var s = await l.ReadConversationAsync(Identity); Equal(OutboxStage.Superseded, s.Outbox.Single(x => x.Id == page.Id).Stage); Equal(o.Body, s.Outbox.Single(x => x.Id == o.Id).Body); await l.DisposeAsync(); Check(!(await (await f.Open()).ReadConversationAsync(Identity)).IsReadOnly); }
    private static async Task CatchConflict()
    { await using var f = new F(); f.Provider.Forced = OutputDependencyStatus.Valid; var l = await f.Open(); await Outcome(l, await Notice(l, Bound(), "first"), OutboxStage.Rejected); await Outcome(l, await Notice(l, Bound(2), "second"), OutboxStage.Rejected); await Input(l, "return", true); var batch = await l.PrepareCatchUpAsync(Identity, "return"); var s = await l.ReadConversationAsync(Identity); foreach (var page in batch.Outbox) { Equal(OutputDependencyMode.LegacyUnknown, DurableConversationLedger.ResolveOutputDependencies(s, page).Mode); await Error(LedgerErrors.DependencyUnavailable, () => l.BeginOutboxSendAsync(Identity, page.Id)); } }
    private static async Task ValidTransition()
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var e = new Executor();
        e.Handler = async (ctx, _) =>
        {
            var s = await l.ReadConversationAsync(Identity); Equal(OperationStage.Dispatching, s.Operations.Single().Stage); Equal(0, s.Generations.Single().GenerationContext!.Epoch);
            f.Provider.Current = Bound(2); return new(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new { memoryId = "memory-one" }), ContextTransition: new(ctx.GenerationContext!.GenerationId, 0, ctx.InvocationId, ctx.OperationId!.Value, Bound(), Bound(2)));
        };
        var m = new Model((_, i) => Task.FromResult(i == 0 ? ToolModelResponse.Call(Remember(), Remember("must-discard")) : ToolModelResponse.Reply("new preference acknowledged")));
        var r = await Loop(l, m, f.Provider, e).RunAsync(Identity, "input"); Equal(ToolLoopState.Completed, r.State); Equal(1, e.Calls.Count); Equal(1, f.Provider.Captures); Equal(1, m.Requests[1].GenerationContext!.Epoch);
        var snapshot = await l.ReadConversationAsync(Identity); Equal(OperationStage.Completed, snapshot.Operations.Single().Stage); Equal(1, snapshot.Generations.Single().ContextTransitions!.Value.Length);
        Check(OutputDependencies.Equivalent(Bound(), snapshot.ToolInvocations.Single().GenerationContext!.Dependencies)); Check(OutputDependencies.Equivalent(Bound(2), DurableConversationLedger.ResolveOutputDependencies(snapshot, snapshot.Outbox.Single())));
        await l.DisposeAsync(); Check(!(await (await f.Open()).ReadConversationAsync(Identity)).IsReadOnly);
    }
    private static async Task FactsDoNotTransition()
    { await using var f = new F(); var l = await f.Open(); await Input(l); var e = new Executor { Handler = (_, _) => { f.Provider.Current = Bound(2); return Task.FromResult(new ToolExecutionResult(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new { contextTransition = new { expectedEpoch = 0, after = Bound(2) } }))); } }; var m = new Model((_, _) => Task.FromResult(ToolModelResponse.Call(Remember()))); Equal(ToolLoopState.Superseded, (await Loop(l, m, f.Provider, e).RunAsync(Identity, "input")).State); Equal(0, (await l.ReadConversationAsync(Identity)).Generations.Single().GenerationContext!.Epoch); Equal(0, f.Provider.Transitions); }
    private static async Task BadTransition(string kind)
    {
        await using var f = new F(); var l = await f.Open(); await Input(l); var e = new Executor(); f.Provider.AllowTransition = kind != "host";
        e.Handler = (ctx, _) =>
        {
            var after = kind == "revision" ? new OutputDependencies(OutputDependencyMode.Bound, [Bound().Contexts[0] with { Fingerprint = "changed" }]) : Bound(2); f.Provider.Current = after;
            var proof = new ContextDependencyTransition(ctx.GenerationContext!.GenerationId, 0, kind == "invocation" ? Guid.NewGuid() : ctx.InvocationId, ctx.OperationId ?? Guid.NewGuid(), kind == "before" ? Bound(7) : Bound(), after);
            return Task.FromResult(new ToolExecutionResult(kind == "accepted" ? ToolExecutionState.Accepted : kind == "readonly" ? ToolExecutionState.ReadOnlySuccess : ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new { memoryId = "one" }), ContextTransition: proof));
        };
        var m = new Model((_, i) => Task.FromResult(i == 0 ? ToolModelResponse.Call(kind == "readonly" ? new("read", "list_tasks", "{}") : Remember()) : ToolModelResponse.Reply("must not reach")));
        var r = await Loop(l, m, f.Provider, e).RunAsync(Identity, "input"); Check(r.State is ToolLoopState.Failed or ToolLoopState.Superseded); Equal(1, m.Requests.Count); Equal(0, (await l.ReadConversationAsync(Identity)).Generations.Single().GenerationContext!.Epoch); Equal(0, r.Outbox.Length);
    }
    private static async Task CaptureImmutable()
    { await using var f = new F(); var l = await f.Open(); var g = await Generation(l); await l.BindGenerationContextAsync(g, Bound()); f.Provider.Current = Bound(2); await Error(LedgerErrors.Conflict, () => l.BindGenerationContextAsync(g, Bound(2))); }
    private static async Task VersionGate()
    { await using var f = new F(); var l = await f.Open(); await Notice(l); await Input(l, "return", true); await l.PrepareCatchUpAsync(Identity, "return"); await l.DisposeAsync(); int schema = 0; await f.Rewrite(root => schema = root["schemaVersion"]!.GetValue<int>()); Equal(4, schema); l = await f.Open(); Check(!(await l.ReadConversationAsync(Identity)).IsReadOnly); }
    private static async Task Corruption()
    {
        foreach (string kind in new[] { "schema", "epoch", "context", "invocation" })
        {
            await using var f = new F(); var l = await f.Open(); await Input(l); await Loop(l, ReadModel(), f.Provider).RunAsync(Identity, "input"); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                if (kind == "schema") root["schemaVersion"] = 2;
                if (kind == "epoch") root["generations"]![0]!["generationContext"]!["epoch"] = 8;
                if (kind == "context") root["generations"]![0]!["generationContext"]!["dependencies"]!["contexts"]![0]!["items"] = null;
                if (kind == "invocation") root["events"]!.AsArray().Single(x => x!["relatedInvocationId"] != null)!["relatedInvocationId"] = Guid.NewGuid().ToString();
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
    private static async Task<(GenerationLease Generation, ContextDependencyTransition Transition)> CompletedOperation(DurableConversationLedger l)
    {
        var g = await Generation(l); var call = Remember(); var inv = await l.RegisterToolInvocationAsync(g, "input", call.Id, call.Name, call.ArgumentsJson, true);
        var grant = await l.AuthorizeCurrentUserRequestAsync(Identity, "input", call.Name, "synthetic-memory", call.ArgumentsJson, "remember synthetic preference");
        var op = await l.QueueOperationAsync(Identity, grant.Id, "memory-operation"); await l.BeginToolInvocationAsync(g, inv.Id, op.Id);
        await l.FinishToolInvocationAsync(Identity, inv.Id, ToolInvocationStage.Completed, "{}", OperationStage.Completed);
        return (g, new(g.Id, 0, inv.Id, op.Id, Bound(), Bound(2)));
    }
    private static async Task FlushRecovery(string phase)
    {
        await using var f = new F(); var fault = new FaultInjector(); var l = await f.Open(fault: fault); GenerationLease? generation = null; Func<Task> action;
        if (phase == "bind") { await Input(l); generation = await l.BeginGenerationAsync(Identity, ["input"]); action = () => l.BindGenerationContextAsync(generation, Bound()); }
        else if (phase == "prepare") { generation = await Generation(l); action = () => l.PrepareReplyAsync(generation, ["prepared"]); }
        else if (phase == "send") { var output = await Notice(l); action = () => l.BeginOutboxSendAsync(Identity, output.Id); }
        else { var completed = await CompletedOperation(l); generation = completed.Generation; f.Provider.Current = Bound(2); action = () => l.AdvanceGenerationContextAsync(completed.Generation, completed.Transition); }
        fault.Stage = JournalWriteStage.AfterFlush; await Throws<InjectedFailure>(action); await l.DisposeAsync(); l = await f.Open(); Check(!(await l.ReadConversationAsync(Identity)).IsReadOnly);
        var s = await l.ReadConversationAsync(Identity);
        if (phase == "send") Equal(OutboxStage.Unknown, s.Outbox.Single().Stage);
        else { var g = s.Generations.Single(); Check(g.GenerationContext != null); Equal(phase == "transition" ? 1 : 0, g.GenerationContext!.Epoch); if (phase == "prepare") Equal(GenerationStage.Prepared, g.Stage); }
    }
    private static Task Equivalence()
    {
        var b = Bound(); Check(OutputDependencies.Equivalent(b, b with { Contexts = [b.Contexts[0] with { Items = [new("memory-one", 1)] }] }));
        Equal(OutputDependencyMode.LegacyUnknown, DurableConversationLedger.MergeOutputDependencies([b, OutputDependencies.LegacyUnknown]).Mode);
        Equal(OutputDependencyMode.LegacyUnknown, DurableConversationLedger.MergeOutputDependencies([b, Bound(2)]).Mode);
        Check(OutputDependencies.Equivalent(b, DurableConversationLedger.MergeOutputDependencies([b, OutputDependencies.Independent, b]))); return Task.CompletedTask;
    }
    private static async Task Error(string code, Func<Task> action)
    { try { await action(); } catch (LedgerException e) { Equal(code, e.Code); return; } throw new InvalidOperationException("Expected " + code); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
