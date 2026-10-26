using System.Collections.Immutable;
using System.Text.Json;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    public Task<ToolExecutionResult> ApplyDependentWorkToolAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var invocation = RequireInvocation(state, context.Identity, context.InvocationId);
        if (context.OperationId is not { } id || context.InputWorkId != null || context.ReadAtRevision != null)
            throw Error(LedgerErrors.AuthorizationRequired, "A current ordinary invocation is required.");
        var operation = RequireOperation(state, context.Identity, id);
        if (invocation.OperationId != id || context.SourceMessageId != operation.SourceMessageId
            || context.Revision != operation.Revision || context.IdempotencyKey != operation.IdempotencyKey)
            throw Error(LedgerErrors.AuthorizationRequired, "Dependent work invocation does not match its operation.");
        var prior = state.DependentMutations.SingleOrDefault(x => x.OperationId == id);
        if (prior != null)
        {
            if (prior.InvocationId != invocation.Id) throw Error(LedgerErrors.IdentityMismatch, "Different mutation invocation.");
            return Unchanged(CommitmentExecution(prior.ReceiptJson, operation));
        }
        var source = RequireInbox(state, context.Identity, context.SourceMessageId);
        var grant = RequireAuthorization(state, context.Identity, operation.AuthorizationId);
        var generation = state.Generations.SingleOrDefault(x => x.Id == invocation.GenerationId);
        RequireCurrent(state, context.Identity, context.Revision);
        if (!DependentWorkTools.Handles(invocation.ToolName) || source.Trust != MessageTrust.AuthenticatedUser
            || source.Revision != context.Revision || operation.Stage != OperationStage.Dispatching
            || invocation.Stage != ToolInvocationStage.Executing || !invocation.HasSideEffects
            || operation.Action != invocation.ToolName || operation.ArgumentsJson != invocation.ArgumentsJson
            || invocation.SourceMessageId != source.SourceMessageId || invocation.Revision != context.Revision
            || grant.SourceMessageId != source.SourceMessageId || grant.Revision != context.Revision
            || grant.SourceExcerpt != source.Body || grant.Action != operation.Action || grant.Task != operation.Task
            || grant.ArgumentsJson != operation.ArgumentsJson || grant.TargetKey != operation.TargetKey
            || operation.TargetKey != DependentWorkTools.TargetKey(operation.Action, operation.ArgumentsJson)
            || generation == null || generation.InputWorkId != null || generation.Stage != GenerationStage.Processing
            || generation.Identity != context.Identity || generation.Revision != context.Revision || !generation.InboxIds.Contains(source.Id))
            throw Error(LedgerErrors.AuthorizationRequired, "Dependent work needs exact current durable authorization.");
        var work = operation.Action == "schedule_dependency" ? CreateDependentWork(state, source, operation)
            : ChangeDependentWork(state, source, operation);
        var facts = JsonSerializer.SerializeToElement(new { workItemId = work.Id, version = work.Version, stage = work.Stage.ToString(),
            target = work.Target, instructions = work.Instructions, requirements = work.Requirements, deadlineUtc = work.DeadlineUtc,
            continueAfterWorkId = work.ContinueAfterWorkId, notificationPolicy = work.NotificationPolicy, taskDispatched = false, completionVerified = false });
        string receipt = JsonSerializer.Serialize(new { status = "completed", facts, operationId = id });
        state.DependentMutations.Add(new(id, invocation.Id, work.Id, work.Version, receipt));
        Replace(state.Operations, operation, operation with { Stage = OperationStage.Completed, Receipt = receipt, UpdatedAt = _clock.UtcNow });
        state.SchemaVersion = Math.Max(state.SchemaVersion, work.ContinueAfterWorkId == null ? 7 : 8);
        if (work.NotificationPolicy != null) state.SchemaVersion = Math.Max(state.SchemaVersion, 9);
        if (work.Requirements.Any(r => r.CommandSuccess != null)) state.SchemaVersion = Math.Max(state.SchemaVersion, 11);
        return Changed(new ToolExecutionResult(ToolExecutionState.Completed, facts, TargetKey: operation.TargetKey, Task: operation.Task));
    }, cancellationToken);

    private DependentWorkItem CreateDependentWork(LedgerState state, InboxMessage source, OperationRecord operation)
    {
        var spec = JsonSerializer.Deserialize<DependentWorkSpecification>(operation.ArgumentsJson, Json)!;
        RequireDependencyTarget(spec.Target, spec.BindingFingerprint);
        if (operation.Task != spec.Target) throw new ArgumentException("Dependency target must match the authorized task.");
        RequireDependencyInstructions(source, spec.Instructions);
        RequireLiteralDependencyCommands(source, spec.Requirements);
        var deadline = Deadline(spec.DeadlineUtc) ?? throw new ArgumentException("A deadline is required.");
        if (deadline <= _clock.UtcNow) throw new ArgumentException("The dependency deadline has passed.");
        if (state.DependentWork.Count >= 256) throw Error(LedgerErrors.CapacityExceeded, "Dependent work capacity is full.");
        var work = new DependentWorkItem(Guid.NewGuid(), source.Identity, source.SourceMessageId, operation.Id, 1,
            DependentWorkStage.Waiting, spec.Target, spec.BindingFingerprint, spec.Instructions, spec.Requirements,
            deadline, _clock.UtcNow, _clock.UtcNow, LastEvidence: [], ContinueAfterWorkId: spec.ContinueAfterWorkId, NotificationPolicy: spec.NotificationPolicy);
        if (work.NotificationPolicy != null) ValidateCommitmentPolicy(work.NotificationPolicy);
        RequireDependencyRequirements(state, work);
        state.DependentWork.Add(work); return work;
    }

    private DependentWorkItem ChangeDependentWork(LedgerState state, InboxMessage source, OperationRecord operation)
    {
        var spec = JsonSerializer.Deserialize<DependentWorkChange>(operation.ArgumentsJson, Json)!;
        var old = RequireDependentWork(state, source.Identity, spec.WorkItemId);
        if (old.Version != spec.ExpectedVersion || old.Target != operation.Task)
            throw Error(LedgerErrors.Conflict, "Dependent work version or target changed.");
        if (spec.Action != "update" && (spec.Instructions != null || spec.DeadlineUtc != null || spec.Requirements != null || spec.NotificationPolicy != null))
            throw new ArgumentException("This action cannot also edit the work.");
        var attempt = old.AttemptId is { } attemptId ? state.DependentAttempts.Single(a => a.Id == attemptId) : null;
        var stage = spec.Action switch
        {
            "pause" when old.Stage is DependentWorkStage.Waiting or DependentWorkStage.Dispatching or DependentWorkStage.Running => DependentWorkStage.Paused,
            "resume" when old.Stage == DependentWorkStage.Paused => old.Completion != null
                ? CompletionStage(old.Completion.State) : attempt?.Stage switch
                { DependentDispatchStage.Accepted => DependentWorkStage.Running, DependentDispatchStage.Dispatching => DependentWorkStage.Dispatching,
                    DependentDispatchStage.Unknown => DependentWorkStage.Unknown, DependentDispatchStage.Rejected => DependentWorkStage.NeedsDecision,
                    _ => DependentWorkStage.Waiting },
            "cancel" when old.Stage is not (DependentWorkStage.Completed or DependentWorkStage.Failed) => DependentWorkStage.Cancelled,
            "update" when (old.Stage is DependentWorkStage.Waiting or DependentWorkStage.Paused or DependentWorkStage.NeedsDecision
                || old.Stage == DependentWorkStage.Expired && spec.DeadlineUtc != null)
                && attempt?.Stage is not (DependentDispatchStage.Accepted or DependentDispatchStage.Dispatching or DependentDispatchStage.Unknown) =>
                old.Stage == DependentWorkStage.Paused ? DependentWorkStage.Paused : DependentWorkStage.Waiting,
            _ => throw new ArgumentException("Dependent work cannot make this transition.")
        };
        string instructions = spec.Instructions ?? old.Instructions;
        if (spec.Instructions != null) RequireDependencyInstructions(source, instructions);
        var deadline = spec.DeadlineUtc == null ? old.DeadlineUtc : Deadline(spec.DeadlineUtc)!.Value;
        if (spec.Action == "update" && (spec.Instructions == null && spec.DeadlineUtc == null && spec.Requirements == null && spec.NotificationPolicy == null || deadline <= _clock.UtcNow))
            throw new ArgumentException("An update needs changed fields and a future deadline.");
        var work = old with { Version = checked(old.Version + 1), Stage = stage, Instructions = instructions,
            Requirements = spec.Requirements ?? old.Requirements, DeadlineUtc = deadline, LastEvidence = [], Reason = null, UpdatedAt = _clock.UtcNow,
            NotificationPolicy = spec.NotificationPolicy ?? old.NotificationPolicy };
        if (work.NotificationPolicy != null) ValidateCommitmentPolicy(work.NotificationPolicy);
        if (spec.Requirements != null)
        { RequireLiteralDependencyCommands(source, work.Requirements); RequireDependencyRequirements(state, work); }
        Replace(state.DependentWork, old, work);
        SupersedeDependentNotices(state, work);
        ValidateDependencyGraph(state); return work;
    }

    public async Task<DependentWorkSnapshot> GetDependentWorkAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity); await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_state.DependentWork.Where(x => x.Identity == identity).ToImmutableArray(),
                _state.DependentAttempts.Where(x => x.Identity == identity).ToImmutableArray());
        }
        finally { _gate.Release(); }
    }
    private static DependentWorkItem RequireDependentWork(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.DependentWork.SingleOrDefault(x => x.Id == id && x.Identity == identity)
            ?? throw Error(LedgerErrors.NotFound, "Dependent work does not belong to this conversation.");
    private static void RequireDependencyTarget(TaskReference task, string fingerprint)
    {
        ValidateTask(task);
        if (string.IsNullOrWhiteSpace(task.TurnId) || fingerprint?.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Exact task turn and configuration fingerprint are required.");
    }
    private void RequireDependencyInstructions(InboxMessage source, string instructions)
    {
        ValidateBody(instructions);
        if (instructions.Length > 4000 || !source.Body.Contains(instructions, StringComparison.Ordinal))
            throw new ArgumentException("Use the authorized user's literal follow-up instructions.");
    }
    private static void RequireDependencyRequirements(LedgerState state, DependentWorkItem work)
    {
        if (work.Requirements.IsDefaultOrEmpty || work.Requirements.Length > 8 || work.Requirements.Distinct().Count() != work.Requirements.Length)
            throw new ArgumentException("Choose one to eight distinct predecessors.");
        foreach (var requirement in work.Requirements)
        {
            if (requirement.CommandSuccess is { } command) RequireDependencyCommand(command);
            if (requirement.Task != null && requirement.WorkItemId == null && requirement.WorkVersion == null)
                RequireDependencyTarget(requirement.Task, requirement.BindingFingerprint!);
            else if (requirement.Task == null && requirement.BindingFingerprint == null && requirement.WorkItemId is { } id
                && id != work.Id && requirement.WorkVersion is > 0)
            {
                var parent = RequireDependentWork(state, work.Identity, id);
                if (parent.Version != requirement.WorkVersion) throw Error(LedgerErrors.Conflict, "Predecessor version changed.");
            }
            else throw new ArgumentException("A predecessor is either one exact task turn or one work version.");
        }
        RequireContinueAfterTarget(state, work);
    }
    private static void RequireContinueAfterTarget(LedgerState state, DependentWorkItem work)
    {
        if (work.ContinueAfterWorkId is not { } id) return;
        var parent = RequireDependentWork(state, work.Identity, id);
        if (id == work.Id || !work.Requirements.Any(r => r.WorkItemId == id)
            || parent.Target != work.Target || parent.BindingFingerprint != work.BindingFingerprint)
            throw new ArgumentException("Continuation must explicitly depend on the named work on the same target and configuration.");
    }
    private static void ValidateDependencyGraph(LedgerState state)
    {
        var visiting = new HashSet<Guid>(); var done = new HashSet<Guid>();
        void Visit(DependentWorkItem work)
        {
            if (done.Contains(work.Id)) return;
            if (!visiting.Add(work.Id)) throw new ArgumentException("Dependency cycles are not allowed.");
            foreach (var requirement in work.Requirements.Where(r => r.WorkItemId != null))
                Visit(RequireDependentWork(state, work.Identity, requirement.WorkItemId!.Value));
            visiting.Remove(work.Id); done.Add(work.Id);
        }
        foreach (var work in state.DependentWork) Visit(work);
    }
}
