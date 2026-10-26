namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public Task EndGenerationAttemptAsync(GenerationLease lease, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var generation = state.Generations.SingleOrDefault(x => x.Id == lease.Id && x.Identity == lease.Identity);
            if (generation is null || generation.Revision != lease.Revision)
                throw Error(LedgerErrors.NotFound, "Generation does not belong to this conversation.");
            if (generation.Stage != GenerationStage.Processing) return Unchanged(false);
            Replace(state.Generations, generation, generation with { Stage = GenerationStage.Interrupted });
            SetInboxStage(state, generation, generation.Revision == Revision(state, generation.Identity) ? InboxStage.Received : InboxStage.Superseded);
            RecoverInputWorkAttempt(state, generation, _clock.UtcNow);
            return Changed(true);
        }, cancellationToken);

    public Task<bool> ReserveAgentBudgetAsync(GenerationLease lease, string sourceMessageId,
        bool toolCall, int maximumModelTurns, int maximumToolCalls, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            RequireGeneration(state, lease);
            var source = RequireInbox(state, lease.Identity, sourceMessageId);
            if (source.Revision != lease.Revision || maximumModelTurns is < 1 or > 64 || maximumToolCalls is < 1 or > 256)
                throw new ArgumentException("Invalid budget or source revision.");
            var current = state.AgentBudgets.SingleOrDefault(x => x.Identity == lease.Identity && x.SourceMessageId == sourceMessageId);
            current ??= new(lease.Identity, sourceMessageId, lease.Revision, 0, 0);
            if (toolCall ? current.ToolCalls >= maximumToolCalls : current.ModelTurns >= maximumModelTurns)
                return Unchanged(false);
            var next = toolCall ? current with { ToolCalls = current.ToolCalls + 1 } : current with { ModelTurns = current.ModelTurns + 1 };
            state.AgentBudgets.Remove(current);
            state.AgentBudgets.Add(next);
            return Changed(true);
        }, cancellationToken);

    public Task<ToolInvocationRecord> RegisterToolInvocationAsync(GenerationLease lease, string sourceMessageId,
        string callId, string toolName, string argumentsJson, bool hasSideEffects,
        CancellationToken cancellationToken = default) => DependencyChangeAsync(lease.Identity,
        state => RequireGeneration(state, lease).GenerationContext?.Dependencies, state =>
        {
            var generation = RequireGeneration(state, lease);
            if (generation.Stage != GenerationStage.Processing)
                throw Error(LedgerErrors.InvalidTransition, "Tools may run only during active generation.");
            ValidateToken(callId); ValidateToken(toolName); ValidateBody(argumentsJson);
            var source = RequireInbox(state, lease.Identity, sourceMessageId);
            if (source.Revision != lease.Revision || !generation.InboxIds.Contains(source.Id))
                throw Error(LedgerErrors.AuthorizationRequired, "Tool calls must identify the current source message in their generation.");
            var existing = state.ToolInvocations.SingleOrDefault(x => x.Identity == lease.Identity
                && x.SourceMessageId == sourceMessageId && x.CallId == callId
                && state.Generations.Any(g => g.Id == x.GenerationId && g.InputWorkId == generation.InputWorkId
                    && g.WorkVersion == generation.WorkVersion));
            if (existing is not null)
            {
                if (existing.ToolName != toolName || existing.ArgumentsJson != argumentsJson || existing.HasSideEffects != hasSideEffects)
                    throw Error(LedgerErrors.Conflict, "A tool call ID cannot be reused with different arguments.");
                if (existing.Stage == ToolInvocationStage.Prepared
                    || existing.Stage == ToolInvocationStage.Interrupted && !existing.HasSideEffects)
                {
                    var resumed = existing with { GenerationId = lease.Id, Stage = ToolInvocationStage.Prepared,
                        GenerationContext = generation.GenerationContext, UpdatedAt = _clock.UtcNow };
                    Replace(state.ToolInvocations, existing, resumed);
                    return Changed(resumed);
                }
                return Unchanged(existing);
            }
            var now = _clock.UtcNow;
            var invocation = new ToolInvocationRecord(Guid.NewGuid(), lease.Identity, sourceMessageId,
                lease.Revision, lease.Id, callId, toolName, argumentsJson, hasSideEffects,
                ToolInvocationStage.Prepared, now, now, GenerationContext: generation.GenerationContext);
            state.ToolInvocations.Add(invocation);
            return Changed(invocation);
        }, cancellationToken);

    /// <summary>Atomically persists execution and, for side effects, dispatching before external I/O.</summary>
    public Task<ToolInvocationRecord> BeginToolInvocationAsync(GenerationLease lease, Guid invocationId,
        Guid? operationId = null, CancellationToken cancellationToken = default) => DependencyChangeAsync(lease.Identity,
        state => RequireGeneration(state, lease).GenerationContext?.Dependencies, state =>
        {
            var generation = RequireGeneration(state, lease);
            var invocation = RequireInvocation(state, lease.Identity, invocationId);
            if (generation.InputWorkId is not null && (invocation.HasSideEffects || operationId is not null))
                throw Error(LedgerErrors.AuthorizationRequired, "Deferred input work can never execute a side effect.");
            if (generation.Stage != GenerationStage.Processing || invocation.GenerationId != lease.Id
                || invocation.Revision != lease.Revision || invocation.Stage != ToolInvocationStage.Prepared
                || invocation.GenerationContext?.Epoch != generation.GenerationContext?.Epoch
                || !OutputDependencies.Equivalent(invocation.GenerationContext?.Dependencies, generation.GenerationContext?.Dependencies))
                throw Error(LedgerErrors.InvalidTransition, "Only a current, prepared tool call may execute.");
            if (invocation.HasSideEffects)
            {
                if (operationId is not { } id)
                    throw Error(LedgerErrors.AuthorizationRequired, "A side effect requires an authorized durable operation.");
                var operation = RequireOperation(state, lease.Identity, id);
                RequireCurrent(state, lease.Identity, operation.Revision);
                if (operation.Stage != OperationStage.Queued || operation.Action != invocation.ToolName
                    || operation.SourceMessageId != invocation.SourceMessageId || operation.ArgumentsJson != invocation.ArgumentsJson)
                    throw Error(LedgerErrors.AuthorizationRequired, "The operation does not authorize this exact tool call.");
                Replace(state.Operations, operation, operation with { Stage = OperationStage.Dispatching, UpdatedAt = _clock.UtcNow });
            }
            else if (operationId is not null)
                throw Error(LedgerErrors.InvalidTransition, "A read-only tool must not attach a side-effect operation.");
            var updated = invocation with
            {
                Stage = ToolInvocationStage.Executing, OperationId = operationId,
                GenerationId = lease.Id, GenerationContext = generation.GenerationContext, UpdatedAt = _clock.UtcNow
            };
            Replace(state.ToolInvocations, invocation, updated);
            return Changed(updated);
        }, cancellationToken);

    /// <summary>
    /// Host failure cleanup, not a user cancellation command. Only an exact, still-unstarted
    /// invocation and queued operation can be abandoned. Stale revisions may clean up their own
    /// records, but can never change an execution or uncertain receipt.
    /// </summary>
    public Task<bool> AbandonPreparedToolInvocationAsync(GenerationLease lease, Guid invocationId,
        Guid? operationId, string reason, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateBody(reason);
        var generation = state.Generations.SingleOrDefault(x => x.Identity == lease.Identity && x.Id == lease.Id
            && x.Revision == lease.Revision) ?? throw Error(LedgerErrors.IdentityMismatch, "The cleanup lease does not identify this generation.");
        var invocation = RequireInvocation(state, lease.Identity, invocationId);
        if (invocation.GenerationId != generation.Id || invocation.Revision != lease.Revision
            || !generation.InboxIds.Contains(RequireInbox(state, lease.Identity, invocation.SourceMessageId).Id))
            throw Error(LedgerErrors.IdentityMismatch, "Cleanup must identify the invocation's exact generation and source.");
        OperationRecord? operation = null;
        if (operationId is { } id)
        {
            operation = RequireOperation(state, lease.Identity, id);
            if (!invocation.HasSideEffects || operation.Revision != invocation.Revision
                || operation.SourceMessageId != invocation.SourceMessageId || operation.Action != invocation.ToolName
                || operation.ArgumentsJson != invocation.ArgumentsJson
                || invocation.OperationId is { } attached && attached != id)
                throw Error(LedgerErrors.IdentityMismatch, "Cleanup cannot substitute a different operation.");
        }
        else if (invocation.OperationId != null || invocation.HasSideEffects && state.Operations.Any(x => x.Identity == lease.Identity
            && x.SourceMessageId == invocation.SourceMessageId && x.Action == invocation.ToolName && x.ArgumentsJson == invocation.ArgumentsJson))
            return Unchanged(false); // Never guess the ID of an operation whose queue write may have succeeded.
        if (invocation.Stage != ToolInvocationStage.Prepared || operation != null && operation.Stage != OperationStage.Queued)
            return Unchanged(false);
        string evidence = System.Text.Json.JsonSerializer.Serialize(new { status = "not_started", reason, operationId });
        ValidateBody(evidence);
        if (operation != null) Replace(state.Operations, operation, operation with
        {
            Stage = OperationStage.CancelledBeforeDispatch, Receipt = evidence, UpdatedAt = _clock.UtcNow
        });
        var updated = invocation with { Stage = ToolInvocationStage.Unavailable, OperationId = operationId,
            ResultJson = evidence, UpdatedAt = _clock.UtcNow };
        Replace(state.ToolInvocations, invocation, updated);
        state.SchemaVersion = Math.Max(state.SchemaVersion, 3);
        state.Events.Add(new(Guid.NewGuid(), lease.Identity, ConversationSource.ToolResult, evidence,
            _clock.UtcNow, _clock.UtcNow, invocation.Revision, invocation.SourceMessageId,
            RelatedOperationId: operationId, OutputDependencies: invocation.GenerationContext?.Dependencies,
            RelatedInvocationId: invocation.Id));
        return Changed(true);
    }, cancellationToken);

    public Task<ToolInvocationRecord> FinishToolInvocationAsync(ConversationIdentity identity, Guid invocationId,
        ToolInvocationStage outcome, string resultJson, OperationStage? operationOutcome = null,
        CancellationToken cancellationToken = default, OperationObservation? directReceipt = null) => ChangeAsync(state =>
        {
            ValidateBody(resultJson);
            using (System.Text.Json.JsonDocument.Parse(resultJson)) { }
            var invocation = RequireInvocation(state, identity, invocationId);
            if (invocation.Stage is not (ToolInvocationStage.Prepared or ToolInvocationStage.Executing)
                || outcome is not (ToolInvocationStage.Completed or ToolInvocationStage.Failed or ToolInvocationStage.Unknown
                    or ToolInvocationStage.NeedsConfirmation or ToolInvocationStage.Unavailable or ToolInvocationStage.InvalidArguments))
                throw Error(LedgerErrors.InvalidTransition, "Tool invocation cannot accept this outcome.");
            if (invocation.Stage == ToolInvocationStage.Prepared && operationOutcome is not null)
                throw Error(LedgerErrors.InvalidTransition, "An unexecuted tool cannot report a new operation outcome.");
            var recordedResult = resultJson;
            var localCommit = state.CommitmentMutations.SingleOrDefault(x => x.InvocationId == invocationId && x.Identity == identity)?.ReceiptJson
                ?? state.DependentMutations.SingleOrDefault(x => x.InvocationId == invocationId)?.ReceiptJson;
            if (localCommit != null)
            {
                // A late timeout cannot erase the completed local transaction proven by this receipt.
                outcome = ToolInvocationStage.Completed;
                operationOutcome = OperationStage.Completed;
                recordedResult = localCommit;
                directReceipt = null;
            }
            if (directReceipt is not null) ValidateBody(directReceipt.Evidence);
            if (invocation.OperationId is { } id)
            {
                var operation = RequireOperation(state, identity, id);
                if (operationOutcome is not
                    (OperationStage.Accepted or OperationStage.Running or OperationStage.Completed or OperationStage.Failed or OperationStage.Unknown))
                    throw Error(LedgerErrors.InvalidTransition, "An executing side effect needs a factual operation outcome.");
                if (directReceipt is not null)
                {
                    ValidateObservationIdentity(operation, directReceipt);
                    if (directReceipt.Stage != operationOutcome || directReceipt.Reconciliation)
                        throw Error(LedgerErrors.InvalidTransition, "Direct receipts cannot act as implicit reconciliation.");
                }
                var merged = MergeOperationStage(operation.Stage, operationOutcome.Value, reconciliation: false, directReceipt: true);
                if (merged != operationOutcome)
                {
                    recordedResult = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = merged == OperationStage.Unknown ? "unknown_needs_reconciliation" : merged.ToString().ToLowerInvariant(),
                        operationId = operation.Id,
                        directReceipt = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resultJson)
                    });
                    ValidateBody(recordedResult);
                    if (merged == OperationStage.Unknown) outcome = ToolInvocationStage.Unknown;
                }
                if (merged != operation.Stage)
                    Replace(state.Operations, operation, operation with
                    {
                        Stage = merged, Receipt = resultJson, UpdatedAt = _clock.UtcNow
                    });
            }
            else if (operationOutcome is not null || directReceipt is not null)
                throw Error(LedgerErrors.InvalidTransition, "No dispatched operation exists for this tool call.");
            var updated = invocation with { Stage = outcome, ResultJson = recordedResult, UpdatedAt = _clock.UtcNow };
            Replace(state.ToolInvocations, invocation, updated);
            state.SchemaVersion = Math.Max(state.SchemaVersion, 3);
            state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.ToolResult, localCommit != null ? recordedResult : resultJson,
                _clock.UtcNow, _clock.UtcNow, invocation.Revision, invocation.SourceMessageId,
                RelatedOperationId: invocation.OperationId, OutputDependencies: invocation.GenerationContext?.Dependencies,
                RelatedInvocationId: invocation.Id));
            if (directReceipt is not null)
                state.Events.Add(new(Guid.NewGuid(), identity, ConversationSource.ToolResult, directReceipt.Evidence,
                    _clock.UtcNow, _clock.UtcNow, invocation.Revision, invocation.SourceMessageId,
                    directReceipt.Task, invocation.OperationId, OutputDependencies: invocation.GenerationContext?.Dependencies,
                    RelatedInvocationId: invocation.Id));
            return Changed(updated);
        }, cancellationToken);

    private static ToolInvocationRecord RequireInvocation(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.ToolInvocations.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "Tool invocation does not belong to this conversation.");
}
