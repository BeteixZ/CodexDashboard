using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assistant.Core;

/// <summary>
/// Provider-neutral model/tool loop. Only the trusted host policy can authorize a side effect.
/// Adapter calls happen outside storage transactions, with durable pre-dispatch and outcome records.
/// </summary>
public sealed class ToolAgentLoop
{
    private readonly DurableConversationLedger _ledger;
    private readonly IToolModel _model;
    private readonly ToolCatalog _catalog;
    private readonly IToolExecutor _executor;
    private readonly IUserAuthorizationPolicy _authorization;
    private readonly ToolLoopOptions _options;
    private readonly IReplySegmenter? _replySegmenter;
    private readonly IModelContextProvider? _contextProvider;
    private readonly SemaphoreSlim _modelSlots;
    private readonly SemaphoreSlim _executorSlots;
    private readonly object _externalGate = new();
    private readonly HashSet<Task> _externalCalls = [];

    public ToolAgentLoop(DurableConversationLedger ledger, IToolModel model, ToolCatalog catalog,
        IToolExecutor executor, IUserAuthorizationPolicy authorization, ToolLoopOptions? options = null,
        IReplySegmenter? replySegmenter = null, IModelContextProvider? contextProvider = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _options = options ?? new();
        _replySegmenter = replySegmenter;
        _contextProvider = contextProvider;
        if (_options.MaximumModelTurns is < 1 or > 64 || _options.MaximumToolCalls is < 1 or > 256
            || _options.MaximumContextEvents is < 1 or > 1000 || _options.MaximumContextCharacters is < 1024 or > 1024 * 1024
            || _options.MaximumArgumentsCharacters is < 2 or > 128 * 1024 || _options.MaximumResultCharacters is < 256 or > 128 * 1024
            || _options.MaximumReplySegments is < 1 or > 3 || _options.MaximumConcurrentExternalCalls is < 1 or > 32
            || _options.ModelTimeout <= TimeSpan.Zero || _options.ModelTimeout > TimeSpan.FromMinutes(5)
            || _options.ToolTimeout <= TimeSpan.Zero || _options.ToolTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options));
        _modelSlots = new(_options.MaximumConcurrentExternalCalls);
        _executorSlots = new(_options.MaximumConcurrentExternalCalls);
    }

    public Task<ToolLoopResult> RunAsync(ConversationIdentity identity, string currentSourceMessageId,
        IReadOnlyList<string>? groupedSourceMessageIds = null, CancellationToken cancellationToken = default) =>
        RunCoreAsync(identity, currentSourceMessageId, groupedSourceMessageIds, null, cancellationToken);

    public async Task<ToolLoopResult> RunReadOnlyWorkAsync(ConversationIdentity identity, Guid workId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _ledger.ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        var work = snapshot.InputWork.SingleOrDefault(x => x.Id == workId)
            ?? throw new LedgerException(LedgerErrors.NotFound, "Input work was not found.");
        var result = await RunCoreAsync(identity, work.SourceMessageId, null, workId, cancellationToken).ConfigureAwait(false);
        if (result.State is ToolLoopState.ModelUnavailable or ToolLoopState.Failed or ToolLoopState.BudgetExceeded)
            await _ledger.FinishReadOnlyWorkAsync(result.Generation, InputWorkStage.Failed,
                result.Reason ?? "Read-only processing could not finish.", CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private async Task<ToolLoopResult> RunCoreAsync(ConversationIdentity identity, string currentSourceMessageId,
        IReadOnlyList<string>? groupedSourceMessageIds, Guid? workId, CancellationToken cancellationToken)
    {
        var snapshot = await _ledger.ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        var current = snapshot.Inbox.SingleOrDefault(x => x.SourceMessageId == currentSourceMessageId)
            ?? throw new LedgerException(LedgerErrors.NotFound, "The current input is not in this conversation's inbox.");
        if (workId is null && current.Revision != snapshot.Revision)
            throw new LedgerException(LedgerErrors.StaleRevision, "This input is no longer current.");
        var lease = workId is { } id
            ? await _ledger.BeginReadOnlyWorkAsync(identity, id, cancellationToken).ConfigureAwait(false)
            : await _ledger.BeginGenerationAsync(identity, groupedSourceMessageIds ?? [currentSourceMessageId],
                current.Task, cancellationToken).ConfigureAwait(false);
        snapshot = await _ledger.ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
        var work = workId is null ? null : snapshot.InputWork.Single(x => x.Id == workId);
        var readRevision = work?.ReadAtRevision ?? lease.Revision;
        var observations = snapshot.ToolInvocations.Where(x => x.SourceMessageId == currentSourceMessageId
            && x.Stage != ToolInvocationStage.Prepared && snapshot.Generations.Any(g => g.Id == x.GenerationId
                && g.InputWorkId == workId && (work is null || g.WorkVersion == work.Version)))
            .Select(Observation).ToList();
        var needsConfirmation = observations.Any(x => x.ResultJson.Contains("\"needs_confirmation\"", StringComparison.Ordinal));
        using var revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnRevision(ConversationIdentity changed, long revision)
        {
            if (changed == identity && revision != readRevision) revisionCancellation.Cancel();
        }
        void OnDialogue(ConversationIdentity changed, long epoch)
        {
            if (changed == identity && work is not null && epoch != work.DialogueEpoch) revisionCancellation.Cancel();
        }
        _ledger.RevisionChanged += OnRevision;
        _ledger.DialogueChanged += OnDialogue;
        try
        {
            if (_contextProvider != null)
            {
                OutputDependencies captured;
                try { captured = await _contextProvider.CaptureAsync(lease, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException && error is not LedgerException)
                { throw new LedgerException(LedgerErrors.DependencyUnavailable, "The host could not capture the current context.", error); }
                await _ledger.BindGenerationContextAsync(lease, captured, cancellationToken).ConfigureAwait(false);
            }
            snapshot = await _ledger.ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
            if (snapshot.Revision != readRevision || work is not null && snapshot.Context!.Epoch != work.DialogueEpoch) revisionCancellation.Cancel();
            var token = revisionCancellation.Token;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await RequireContext(lease, token).ConfigureAwait(false);
                if (!await Reserve(lease, currentSourceMessageId, false, token).ConfigureAwait(false))
                    return Result(ToolLoopState.BudgetExceeded, "The model-turn budget was exhausted.");
                ToolModelResponse response;
                try
                {
                    snapshot = await _ledger.ReadConversationAsync(identity, token).ConfigureAwait(false);
                    var generationContext = snapshot.Generations.Single(x => x.Id == lease.Id).GenerationContext;
                    var selected = new ModelContextSelection(Context(snapshot, current.ReferencedEventId), observations.ToImmutableArray(), snapshot.Context);
                    if (_contextProvider != null && generationContext != null)
                        selected = await _contextProvider.FilterAsync(identity, generationContext, selected.Context, selected.Observations, token).ConfigureAwait(false);
                    if (selected == null || selected.Context.IsDefault || selected.Observations.IsDefault)
                        return Result(ToolLoopState.Failed, "The host did not provide a valid context projection.");
                    await RequireContext(lease, token).ConfigureAwait(false);
                    var request = new ToolModelRequest(current, lease.Revision, selected.Context,
                        _catalog.AvailableDefinitions(_executor, work is not null), selected.Observations, selected.ConversationContext,
                        work, snapshot.Inbox.SingleOrDefault(x => x.Revision == readRevision), generationContext);
                    response = await BoundedCall(
                        ct => _model.GenerateAsync(request, ct), _modelSlots, _options.ModelTimeout, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return Result(ToolLoopState.ModelUnavailable, "The model request timed out.") with { ModelFailure = ToolModelFailureKind.Timeout };
                }
                catch (ToolModelException error)
                {
                    return Result(ToolLoopState.ModelUnavailable, "The model request failed.") with { ModelFailure = error.Kind };
                }
                catch (Exception error) when (error is not OperationCanceledException && error is not LedgerException)
                {
                    return Result(ToolLoopState.ModelUnavailable, "The model is temporarily unavailable.");
                }
                token.ThrowIfCancellationRequested();
                await RequireContext(lease, token).ConfigureAwait(false);
                if (response is null || response.Calls.IsDefault || response.ReplySegments.IsDefault
                    || (response.Calls.Length > 0) == (response.ReplySegments.Length > 0))
                    return Result(ToolLoopState.Failed, "The model response must contain tool calls or a final reply.");
                if (response.ReplySegments.Length > 0)
                {
                    if (response.ReplySegments.Length > _options.MaximumReplySegments
                        || response.ReplySegments.Any(string.IsNullOrWhiteSpace))
                        return Result(ToolLoopState.Failed, "The model returned an invalid reply.");
                    var actualSegments = _replySegmenter?.Split(response.ReplySegments) ?? response.ReplySegments;
                    if (actualSegments.IsDefaultOrEmpty || actualSegments.Any(string.IsNullOrWhiteSpace))
                        return Result(ToolLoopState.Failed, "The channel segmenter returned an invalid reply.");
                    await _ledger.PrepareReplyAsync(lease, actualSegments, token).ConfigureAwait(false);
                    var outbox = await _ledger.EnqueuePreparedReplyAsync(lease, token).ConfigureAwait(false);
                    return new(needsConfirmation ? ToolLoopState.NeedsConfirmation : ToolLoopState.Completed,
                        lease, observations.ToImmutableArray(), outbox);
                }
                foreach (var proposed in response.Calls)
                {
                    token.ThrowIfCancellationRequested();
                    await RequireContext(lease, token).ConfigureAwait(false);
                    if (!await Reserve(lease, currentSourceMessageId, true, token).ConfigureAwait(false))
                        return Result(ToolLoopState.BudgetExceeded, "The tool-call budget was exhausted.");
                    if (!ValidCall(proposed))
                        return Result(ToolLoopState.Failed, "The model returned an invalid tool call.");
                    var processed = await ProcessCall(lease, current, proposed, work, token).ConfigureAwait(false);
                    observations.Add(processed.Observation);
                    needsConfirmation |= processed.Observation.ResultJson.Contains("\"needs_confirmation\"", StringComparison.Ordinal);
                    if (processed.ContextAdvanced) break; // Remaining calls were proposed under the former context.
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result(cancellationToken.IsCancellationRequested ? ToolLoopState.Cancelled : ToolLoopState.Superseded,
                cancellationToken.IsCancellationRequested ? "Generation was cancelled." : "A newer user message superseded this generation.");
        }
        catch (LedgerException error) when (error.Code == LedgerErrors.StaleRevision)
        {
            return Result(ToolLoopState.Superseded, "A newer user message superseded this generation.");
        }
        catch (LedgerException error) when (error.Code is LedgerErrors.DependencyInvalidated or LedgerErrors.DependencyUnavailable)
        {
            return Result(ToolLoopState.Superseded, "The captured generation context is no longer verified.");
        }
        catch (LedgerException)
        {
            return Result(ToolLoopState.Failed, "A durable state conflict or storage failure prevented continuing.");
        }
        finally
        {
            _ledger.RevisionChanged -= OnRevision;
            _ledger.DialogueChanged -= OnDialogue;
            try { await _ledger.EndGenerationAttemptAsync(lease, CancellationToken.None).ConfigureAwait(false); }
            catch (LedgerException) { /* The existing read-only/recovery condition remains visible on the ledger. */ }
            try { _contextProvider?.Release(lease); }
            catch { /* Releasing prompt cache cannot mask a committed outcome. */ }
        }

        ToolLoopResult Result(ToolLoopState state, string reason) => new(state, lease, observations.ToImmutableArray(), [], reason);
    }

    private sealed record ProcessedCall(ModelToolObservation Observation, bool ContextAdvanced = false);
    private async Task<ProcessedCall> ProcessCall(GenerationLease lease, InboxMessage current,
        ModelToolCall proposed, InputWorkItem? work, CancellationToken cancellationToken)
    {
        var known = _catalog.TryGet(proposed.Name, out var descriptor);
        var valid = known && descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out _, out _);
        var canonical = proposed.ArgumentsJson;
        if (valid) descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out canonical, out _);
        var call = proposed with { ArgumentsJson = canonical };
        var invocation = await _ledger.RegisterToolInvocationAsync(lease, current.SourceMessageId,
            call.Id, call.Name, call.ArgumentsJson, known && descriptor.HasSideEffects, cancellationToken).ConfigureAwait(false);
        if (invocation.Stage != ToolInvocationStage.Prepared)
            return new(Observation(invocation));
        OperationRecord? operation = null;
        bool dispatchBegan = false;
        try
        {
        if (!known)
            return await Finish(ToolInvocationStage.Unavailable, "unsupported_tool", "This tool is not registered.").ConfigureAwait(false);
        if (!valid)
            return await Finish(ToolInvocationStage.InvalidArguments, "invalid_arguments", "Arguments do not match the declared schema.").ConfigureAwait(false);
        if (work is not null && descriptor.HasSideEffects)
        {
            const string reason = "This older input is being reconsidered read-only. Restate the complete request in a current message before any action.";
            await _ledger.MarkReadOnlyWorkNeedsConfirmationAsync(lease, reason, cancellationToken).ConfigureAwait(false);
            return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation", reason).ConfigureAwait(false);
        }
        if (!_executor.GetCapability(descriptor.Capability).Available)
            return await Finish(ToolInvocationStage.Unavailable, "capability_unavailable", "The backend has not enabled this capability.").ConfigureAwait(false);

        if (descriptor.HasSideEffects)
        {
            var idempotencyKey = ToolKey(current.SourceMessageId, call);
            var snapshot = await _ledger.ReadConversationAsync(lease.Identity, cancellationToken).ConfigureAwait(false);
            operation = snapshot.Operations.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);
            if (operation is not null && operation.Stage != OperationStage.Queued)
            {
                var facts = JsonSerializer.Serialize(new
                {
                    status = operation.Stage == OperationStage.Unknown ? "unknown_needs_reconciliation" : operation.Stage.ToString().ToLowerInvariant(),
                    operationId = operation.Id, replayed = false,
                    evidence = operation.Receipt is { Length: > 4096 } ? operation.Receipt[..4096] : operation.Receipt,
                    evidenceTruncated = operation.Receipt?.Length > 4096
                });
                var saved = await _ledger.FinishToolInvocationAsync(lease.Identity, invocation.Id,
                    operation.Stage == OperationStage.Unknown ? ToolInvocationStage.Unknown
                        : operation.Stage is OperationStage.CancelledBeforeDispatch or OperationStage.Failed or OperationStage.Interrupted
                            ? ToolInvocationStage.Unavailable : ToolInvocationStage.Completed,
                    facts, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(Observation(saved));
            }
            if (current.Trust != MessageTrust.AuthenticatedUser)
                return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation", "Only a current authenticated user request can authorize this action.").ConfigureAwait(false);
            UserAuthorizationDecision decision;
            try
            {
                decision = await _authorization.AuthorizeAsync(new(current, call, descriptor), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation", "Authorization could not be established.").ConfigureAwait(false);
            }
            if (!decision.Allowed || string.IsNullOrWhiteSpace(decision.TargetKey) || string.IsNullOrWhiteSpace(decision.SourceExcerpt))
                return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation", DenialReason(decision)).ConfigureAwait(false);
            try
            {
                if (operation is null)
                {
                    var grant = await _ledger.AuthorizeCurrentUserRequestAsync(lease.Identity, current.SourceMessageId,
                        call.Name, decision.TargetKey, call.ArgumentsJson, decision.SourceExcerpt, decision.Task, cancellationToken).ConfigureAwait(false);
                    operation = await _ledger.QueueOperationAsync(lease.Identity, grant.Id, idempotencyKey, cancellationToken).ConfigureAwait(false);
                }
                else if (operation.Action != call.Name || operation.ArgumentsJson != call.ArgumentsJson
                    || operation.SourceMessageId != current.SourceMessageId || operation.TargetKey != decision.TargetKey
                    || operation.Task != decision.Task || !current.Body.Contains(decision.SourceExcerpt, StringComparison.Ordinal))
                {
                    return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation",
                        "The recovered operation does not match the currently resolved authorization.").ConfigureAwait(false);
                }
            }
            catch (LedgerException error) when (error.Code == LedgerErrors.AuthorizationRequired)
            {
                return await Finish(ToolInvocationStage.NeedsConfirmation, "needs_confirmation", "Authorization evidence did not match the current input.").ConfigureAwait(false);
            }
        }

        // Recheck immediately before the persisted execution boundary.
        if (!_executor.GetCapability(descriptor.Capability).Available)
            return await Finish(ToolInvocationStage.Unavailable, "capability_unavailable", "The backend capability is no longer available.").ConfigureAwait(false);
        invocation = await _ledger.BeginToolInvocationAsync(lease, invocation.Id, operation?.Id, cancellationToken).ConfigureAwait(false);
        dispatchBegan = true;
        ToolExecutionResult? execution = null;
        var cancelled = false;
        try
        {
            execution = await BoundedCall(ct => _executor.ExecuteAsync(new(lease.Identity, current.SourceMessageId,
                lease.Revision, invocation.Id, operation?.Id, operation?.IdempotencyKey, work?.Id, work?.ReadAtRevision, invocation.GenerationContext,
                current.ReferencedEventId != null ? current.Task : null), call, ct),
                _executorSlots, _options.ToolTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalBusyException)
        {
            execution = new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { }), "The tool adapter is busy.");
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
        }
        catch (Exception) { /* An adapter exception after dispatch cannot prove absence of an effect. */ }

        string resultJson;
        ToolInvocationStage outcome;
        OperationStage? operationOutcome = null;
        if (execution is not null)
        {
            try { execution = execution with { Facts = execution.Facts.Clone() }; }
            catch (Exception) { execution = null; }
        }
        if (!ValidExecution(execution, descriptor.HasSideEffects))
        {
            resultJson = Status(descriptor.HasSideEffects ? "unknown_needs_reconciliation" : "failed",
                descriptor.HasSideEffects ? "The tool outcome could not be confirmed." : "The read-only tool did not return a valid result.");
            outcome = descriptor.HasSideEffects ? ToolInvocationStage.Unknown : ToolInvocationStage.Failed;
            if (descriptor.HasSideEffects) operationOutcome = OperationStage.Unknown;
        }
        else
        {
            resultJson = ToolResultJson.Serialize(new
            {
                status = execution!.State.ToString().ToLowerInvariant(), facts = execution.Facts,
                reason = execution.Reason, operationId = operation?.Id
            });
            outcome = execution.State switch
            {
                ToolExecutionState.Rejected => ToolInvocationStage.Failed,
                ToolExecutionState.Unknown => ToolInvocationStage.Unknown,
                _ => ToolInvocationStage.Completed
            };
            if (descriptor.HasSideEffects) operationOutcome = execution.State switch
            {
                ToolExecutionState.Accepted => OperationStage.Accepted,
                ToolExecutionState.Running => OperationStage.Running,
                ToolExecutionState.Completed => OperationStage.Completed,
                ToolExecutionState.Rejected => OperationStage.Failed,
                _ => OperationStage.Unknown
            };
        }
        if (resultJson.Length > _options.MaximumResultCharacters)
        {
            resultJson = Status(descriptor.HasSideEffects ? "unknown_needs_reconciliation" : "failed", "The tool result exceeded the configured limit.");
            outcome = descriptor.HasSideEffects ? ToolInvocationStage.Unknown : ToolInvocationStage.Failed;
            if (descriptor.HasSideEffects) operationOutcome = OperationStage.Unknown;
        }
        // Cancellation must not prevent recording an uncertain external effect. This is a short
        // local durable transaction, not another network call.
        var finished = await _ledger.FinishToolInvocationAsync(lease.Identity, invocation.Id,
            outcome, resultJson, operationOutcome, CancellationToken.None,
            operation is not null && execution is not null && (execution.TargetKey is not null || execution.Task is not null)
                ? new(operationOutcome!.Value, resultJson, execution.TargetKey ?? "", execution.Task)
                : null).ConfigureAwait(false);
        if (cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (execution?.ContextTransition is { } transition)
        {
            if (_contextProvider == null || outcome != ToolInvocationStage.Completed || operationOutcome != OperationStage.Completed
                || invocation.GenerationContext is not { } before || transition.InvocationId != invocation.Id || transition.OperationId != operation?.Id
                || !await _contextProvider.AcceptTransitionAsync(lease.Identity, before, transition, cancellationToken).ConfigureAwait(false))
                throw new LedgerException(LedgerErrors.DependencyInvalidated, "The executor's context transition could not be verified.");
            await _ledger.AdvanceGenerationContextAsync(lease, transition, cancellationToken).ConfigureAwait(false);
            return new(Observation(finished), true);
        }
        return new(Observation(finished));

        async Task<ProcessedCall> Finish(ToolInvocationStage stage, string status, string reason)
        {
            if (operation != null && await _ledger.AbandonPreparedToolInvocationAsync(lease, invocation.Id, operation.Id,
                reason, CancellationToken.None).ConfigureAwait(false))
            {
                var snapshot = await _ledger.ReadConversationAsync(lease.Identity, CancellationToken.None).ConfigureAwait(false);
                return new(Observation(snapshot.ToolInvocations.Single(x => x.Id == invocation.Id)));
            }
            var finished = await _ledger.FinishToolInvocationAsync(lease.Identity, invocation.Id, stage,
                Status(status, reason), cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(Observation(finished));
        }
        }
        finally
        {
            if (!dispatchBegan)
            {
                try
                {
                    await _ledger.AbandonPreparedToolInvocationAsync(lease, invocation.Id, operation?.Id,
                        "Tool execution did not reach its durable dispatch boundary.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (LedgerException error) when (error.Code == LedgerErrors.RecoveryRequired)
                { /* A prior write has an uncertain outcome; reopening, not a guessed cleanup, is required. */ }
            }
        }
    }

    private bool ValidCall(ModelToolCall? call) => call is not null && !string.IsNullOrWhiteSpace(call.Id)
        && call.Id.Length <= 128 && !call.Id.Any(char.IsControl) && !string.IsNullOrWhiteSpace(call.Name)
        && call.Name.Length <= 64 && !call.Name.Any(char.IsControl) && !string.IsNullOrWhiteSpace(call.ArgumentsJson)
        && call.ArgumentsJson.Length <= _options.MaximumArgumentsCharacters;

    private bool ValidExecution(ToolExecutionResult? result, bool sideEffects)
    {
        if (result is null || !Enum.IsDefined(result.State) || result.Facts.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)
            || result.Reason?.Length > 512 || result.Facts.GetRawText().Length > _options.MaximumResultCharacters / 2)
            return false;
        return sideEffects ? result.State != ToolExecutionState.ReadOnlySuccess
            : result.State is ToolExecutionState.ReadOnlySuccess or ToolExecutionState.Rejected;
    }

    private Task<bool> Reserve(GenerationLease lease, string source, bool tool, CancellationToken cancellationToken) =>
        _ledger.ReserveAgentBudgetAsync(lease, source, tool, _options.MaximumModelTurns, _options.MaximumToolCalls, cancellationToken);

    private async Task RequireContext(GenerationLease lease, CancellationToken cancellationToken)
    {
        var result = await _ledger.ValidateGenerationContextAsync(lease, cancellationToken).ConfigureAwait(false);
        if (result.Status != OutputDependencyStatus.Valid)
            throw new LedgerException(result.Status == OutputDependencyStatus.Invalid ? LedgerErrors.DependencyInvalidated : LedgerErrors.DependencyUnavailable,
                result.Reason ?? "The captured context cannot be verified.");
    }

    private ImmutableArray<ConversationEvent> Context(ConversationSnapshot snapshot, Guid? referencedEventId)
    {
        var selected = new List<ConversationEvent>();
        var characters = 0;
        var quoted = snapshot.Events.SingleOrDefault(e => e.Id == referencedEventId);
        if (quoted != null && quoted.Body.Length <= _options.MaximumContextCharacters)
        {
            selected.Add(quoted);
            characters += quoted.Body.Length;
        }
        foreach (var item in snapshot.Events.Where(x => x.Revision >= (snapshot.Context?.StartRevision ?? 0))
            .Reverse().Take(_options.MaximumContextEvents))
        {
            if (item.Id == referencedEventId) continue;
            if (characters + item.Body.Length > _options.MaximumContextCharacters) break;
            selected.Add(item);
            characters += item.Body.Length;
        }
        selected.Reverse();
        return selected.ToImmutableArray();
    }

    private static string ToolKey(string sourceMessageId, ModelToolCall call) => "tool-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceMessageId + "\0" + call.Name + "\0" + call.ArgumentsJson)));
    private static string Status(string status, string reason) => JsonSerializer.Serialize(new { status, reason });
    private static string DenialReason(UserAuthorizationDecision decision)
    {
        const string fallback = "The current user request does not authorize this action.";
        // These are facts from the host's policy, not an exception, model text,
        // or a new authorization. A malformed positive decision stays generic.
        if (decision.Allowed || string.IsNullOrWhiteSpace(decision.Reason)) return fallback;
        var text = new StringBuilder();
        foreach (var rune in decision.Reason.EnumerateRunes())
        {
            if (text.Length + rune.Utf16SequenceLength > 512) break;
            text.Append(Rune.IsControl(rune) ? " " : rune.ToString());
        }
        string result = text.ToString().Trim();
        return result.Length == 0 ? fallback : result;
    }
    private static ModelToolObservation Observation(ToolInvocationRecord invocation) => new(invocation.CallId,
        invocation.ToolName, invocation.ResultJson ?? Status(
            invocation.Stage == ToolInvocationStage.Unknown ? "unknown_needs_reconciliation" : invocation.Stage.ToString().ToLowerInvariant(),
            "Recovered from the durable tool invocation record."), invocation.Id, invocation.OperationId);

    private sealed class ExternalBusyException : Exception;
    /// <summary>After cancelling callers, wait for adapters that ignored cancellation to actually stop.</summary>
    public async Task WaitForExternalDrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_externalGate) pending = [.. _externalCalls];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending.Select(x => x.ContinueWith(done => { _ = done.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> BoundedCall<T>(Func<CancellationToken, Task<T>> invoke, SemaphoreSlim slots,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new ExternalBusyException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<T> pending;
        try { pending = invoke(deadline.Token) ?? Task.FromException<T>(new InvalidOperationException("External adapter returned no task.")); }
        catch
        {
            slots.Release();
            throw;
        }
        lock (_externalGate) _externalCalls.Add(pending);
        _ = pending.ContinueWith(completed =>
        {
            _ = completed.Exception;
            slots.Release();
            lock (_externalGate) _externalCalls.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
    }
}
