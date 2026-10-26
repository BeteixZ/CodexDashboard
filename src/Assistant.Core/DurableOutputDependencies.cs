using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private readonly IOutputDependencyValidator? _dependencyValidator;

    public OutputDependencyValidation ValidateOutputDependencies(ConversationIdentity identity, OutputDependencies dependencies) =>
        DependencyValidation(identity, dependencies, _state.OutputDependenciesRequired || _options.RequireOutputDependencies);

    private OutputDependencyValidation DependencyValidation(ConversationIdentity identity, OutputDependencies? dependencies, bool required)
    {
        dependencies ??= OutputDependencies.LegacyUnknown;
        try { ValidateDependencyDefinition(dependencies); }
        catch (ArgumentException) { return new(OutputDependencyStatus.Invalid, "The output dependency manifest is invalid."); }
        if (dependencies.Mode == OutputDependencyMode.Independent) return new(OutputDependencyStatus.Valid);
        if (dependencies.Mode == OutputDependencyMode.LegacyUnknown)
            return new(required ? OutputDependencyStatus.Unavailable : OutputDependencyStatus.Valid, "The original output has no proven dependency manifest.");
        if (dependencies.Contexts.Any(x => x.ValidUntilUtc <= _clock.UtcNow))
            return new(OutputDependencyStatus.Invalid, "An output context dependency has expired.");
        if (_dependencyValidator == null) return new(OutputDependencyStatus.Unavailable, "The host has not provided a current dependency validator.");
        try
        {
            var result = _dependencyValidator.Validate(identity, dependencies);
            return result != null && Enum.IsDefined(result.Status) && (result.Reason == null || result.Reason.Length <= 1024)
                ? result
                : new(OutputDependencyStatus.Unavailable, "The host dependency validator returned an invalid result.");
        }
        catch { return new(OutputDependencyStatus.Unavailable, "The current context could not be verified."); }
    }

    public static OutputDependencies ResolveOutputDependencies(ConversationSnapshot snapshot, OutboxMessage output)
    {
        if (output.Identity != snapshot.Identity || !snapshot.Outbox.Any(x => x.Id == output.Id && x == output))
            throw Error(LedgerErrors.IdentityMismatch, "The output is not part of this snapshot.");
        if (!snapshot.CatchUpBatches.IsDefault && snapshot.CatchUpBatches.SingleOrDefault(x => x.DeliveryEventId == output.NotificationEventId) is { } batch)
            return MergeOutputDependencies(batch.Members.Where(x => x.CatchUpOutboxId == output.Id).SelectMany(x => x.Sources)
                .Select(x => ResolveOutputDependencies(snapshot, snapshot.Outbox.Single(o => o.Id == x.OutboxId))));
        return output.GenerationId is { } id
            ? snapshot.Generations.Single(x => x.Id == id).GenerationContext?.Dependencies ?? OutputDependencies.LegacyUnknown
            : snapshot.Events.Single(x => x.Id == output.NotificationEventId).OutputDependencies ?? OutputDependencies.LegacyUnknown;
    }

    public static OutputDependencies MergeOutputDependencies(IEnumerable<OutputDependencies> sources)
    {
        var all = sources.ToArray();
        foreach (var item in all) ValidateDependencyDefinition(item);
        if (all.Length == 0 || all.Any(x => x.Mode == OutputDependencyMode.LegacyUnknown)) return OutputDependencies.LegacyUnknown;
        var contexts = ImmutableArray.CreateBuilder<ContextDependency>();
        foreach (var group in all.SelectMany(x => x.Contexts).GroupBy(x => (x.Provider, x.ScopeKey)))
        {
            var first = group.First();
            if (group.Any(x => !OutputDependencies.Equivalent(new(OutputDependencyMode.Bound, [first]), new(OutputDependencyMode.Bound, [x]))))
                return OutputDependencies.LegacyUnknown; // Conflicting contexts cannot be signed as independent or a newer version.
            contexts.Add(first);
        }
        return contexts.Count == 0 ? OutputDependencies.Independent : new(OutputDependencyMode.Bound, contexts.ToImmutable());
    }

    public Task<GenerationContext> BindGenerationContextAsync(GenerationLease lease, OutputDependencies dependencies,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateDependencyDefinition(dependencies);
        var generation = RequireGeneration(state, lease);
        if (generation.GenerationContext is { } existing)
        {
            if (existing.Epoch != 0 || !OutputDependencies.Equivalent(existing.Dependencies, dependencies))
                throw Error(LedgerErrors.Conflict, "The initial generation capture cannot be replaced.");
            return Unchanged(existing);
        }
        if (generation.Stage != GenerationStage.Processing || state.ToolInvocations.Any(x => x.GenerationId == lease.Id))
            throw Error(LedgerErrors.InvalidTransition, "Capture context before the first model or tool request.");
        RequireValidDependency(DependencyValidation(lease.Identity, dependencies, state.OutputDependenciesRequired));
        var context = new GenerationContext(lease.Id, 0, dependencies);
        PromoteDependencySchema(state, dependencies);
        Replace(state.Generations, generation, generation with { GenerationContext = context, ContextTransitions = [] });
        return Changed(context);
    }, cancellationToken);

    public async Task<GenerationContext> GetGenerationContextAsync(GenerationLease lease, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadConversationAsync(lease.Identity, cancellationToken).ConfigureAwait(false);
        var generation = snapshot.Generations.SingleOrDefault(x => x.Id == lease.Id && x.Revision == lease.Revision)
            ?? throw Error(LedgerErrors.IdentityMismatch, "Generation context does not belong to this lease.");
        return generation.GenerationContext ?? new(lease.Id, 0, OutputDependencies.LegacyUnknown);
    }

    public Task<OutputDependencyValidation> ValidateGenerationContextAsync(GenerationLease lease,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var generation = RequireGeneration(state, lease);
        var validation = DependencyValidation(lease.Identity, generation.GenerationContext?.Dependencies, state.OutputDependenciesRequired);
        bool changed = validation.Status == OutputDependencyStatus.Invalid && InvalidateGenerationDependency(state, generation, validation.Reason);
        return new Mutation<OutputDependencyValidation>(validation, changed);
    }, cancellationToken);

    public Task<GenerationContext> AdvanceGenerationContextAsync(GenerationLease lease, ContextDependencyTransition transition,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ArgumentNullException.ThrowIfNull(transition); ValidateDependencyDefinition(transition.Before); ValidateDependencyDefinition(transition.After);
        var generation = RequireGeneration(state, lease);
        var current = generation.GenerationContext;
        if (current == null || generation.Stage != GenerationStage.Processing || current.Epoch >= 64
            || transition.GenerationId != lease.Id || transition.ExpectedEpoch != current.Epoch
            || transition.InvocationId == Guid.Empty || transition.OperationId == Guid.Empty
            || !OutputDependencies.Equivalent(current.Dependencies, transition.Before)
            || OutputDependencies.Equivalent(transition.Before, transition.After)
            || transition.Before.Mode != OutputDependencyMode.Bound || transition.After.Mode != OutputDependencyMode.Bound)
            throw Error(LedgerErrors.Conflict, "The typed context transition does not match the captured generation.");
        var invocation = RequireInvocation(state, lease.Identity, transition.InvocationId);
        var operation = RequireOperation(state, lease.Identity, transition.OperationId);
        if (invocation.GenerationId != lease.Id || invocation.OperationId != operation.Id || !invocation.HasSideEffects
            || invocation.Stage != ToolInvocationStage.Completed || operation.Stage != OperationStage.Completed
            || invocation.GenerationContext?.Epoch != current.Epoch
            || !OutputDependencies.Equivalent(invocation.GenerationContext?.Dependencies, transition.Before)
            || invocation.SourceMessageId != operation.SourceMessageId || invocation.ToolName != operation.Action
            || invocation.ArgumentsJson != operation.ArgumentsJson)
            throw Error(LedgerErrors.AuthorizationRequired, "A context transition requires the exact completed durable tool operation.");
        RequireValidDependency(DependencyValidation(lease.Identity, transition.After, true));
        try { ValidateTransitionProgress(transition); }
        catch (ArgumentException) { throw Error(LedgerErrors.Conflict, "A changed context must advance its provider revision."); }
        var next = new GenerationContext(lease.Id, checked(current.Epoch + 1), transition.After);
        Replace(state.Generations, generation, generation with { GenerationContext = next,
            ContextTransitions = (generation.ContextTransitions ?? []).Add(transition) });
        PromoteDependencySchema(state, transition.After);
        return Changed(next);
    }, cancellationToken);

    public Task<int> RefreshOutputDependenciesAsync(ConversationIdentity identity, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            ValidateIdentity(identity); int changed = RefreshDependencies(state, identity);
            return new Mutation<int>(changed, changed > 0);
        }, cancellationToken);

    private int RefreshDependencies(LedgerState state, ConversationIdentity? identity = null)
    {
        int changed = 0;
        foreach (var generation in state.Generations.Where(x => (identity == null || x.Identity == identity)
            && x.Stage is GenerationStage.Processing or GenerationStage.Prepared).ToArray())
        {
            var validation = DependencyValidation(generation.Identity, generation.GenerationContext?.Dependencies, state.OutputDependenciesRequired);
            if (validation.Status == OutputDependencyStatus.Invalid && InvalidateGenerationDependency(state, generation, validation.Reason)) changed++;
        }
        foreach (var output in state.Outbox.Where(x => (identity == null || x.Identity == identity)
            && x.Stage is OutboxStage.Pending or OutboxStage.Rejected).ToArray())
        {
            var validation = DependencyValidation(output.Identity, ResolveOutputDependencies(SnapshotOf(state, output.Identity), output), state.OutputDependenciesRequired);
            if (validation.Status != OutputDependencyStatus.Invalid) continue;
            Replace(state.Outbox, output, output with { Stage = OutboxStage.Superseded,
                DependencyInvalidationReason = DependencyReason(validation.Reason), UpdatedAt = _clock.UtcNow }); changed++;
        }
        return changed;
    }
    private bool InvalidateGenerationDependency(LedgerState state, GenerationRecord generation, string? reason)
    {
        if (generation.Stage is not (GenerationStage.Processing or GenerationStage.Prepared)) return false;
        Replace(state.Generations, generation, generation with { Stage = GenerationStage.Superseded, DependencyInvalidationReason = DependencyReason(reason) });
        SetInboxStage(state, generation, InboxStage.Superseded);
        if (generation.InputWorkId is { } workId)
        {
            var work = RequireInputWork(state, generation.Identity, workId);
            if (work.GenerationId == generation.Id && work.Stage is InputWorkStage.Reading or InputWorkStage.Prepared)
                Replace(state.InputWork, work, work with { Stage = InputWorkStage.Failed,
                    Reason = DependencyReason(reason), UpdatedAt = _clock.UtcNow });
        }
        return true;
    }
    private static string DependencyReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? "The captured context is no longer current." : reason;
    private static void RequireValidDependency(OutputDependencyValidation validation)
    {
        if (validation.Status != OutputDependencyStatus.Valid)
            throw Error(validation.Status == OutputDependencyStatus.Invalid ? LedgerErrors.DependencyInvalidated : LedgerErrors.DependencyUnavailable,
                DependencyReason(validation.Reason));
    }
    private bool OutputDependencyReady(ConversationSnapshot snapshot, OutboxMessage output) =>
        DependencyValidation(snapshot.Identity, ResolveOutputDependencies(snapshot, output), snapshot.OutputDependenciesRequired).Status == OutputDependencyStatus.Valid;
    private static void PromoteDependencySchema(LedgerState state, OutputDependencies dependencies)
    {
        state.SchemaVersion = Math.Max(3, state.SchemaVersion);
        if (dependencies.Mode == OutputDependencyMode.Bound) state.OutputDependenciesRequired = true;
    }

    private static void ValidateTransitionProgress(ContextDependencyTransition transition)
    {
        if (OutputDependencies.Equivalent(transition.Before, transition.After))
            throw new ArgumentException("A context transition must change the captured context.");
        foreach (var before in transition.Before.Contexts)
        {
            var after = transition.After.Contexts.SingleOrDefault(x => x.Provider == before.Provider && x.ScopeKey == before.ScopeKey);
            if (after == null) continue; // A host may remove a scope when personalization is disabled.
            bool same = OutputDependencies.Equivalent(new(OutputDependencyMode.Bound, [before]), new(OutputDependencyMode.Bound, [after]));
            if (after.Revision < before.Revision || !same && after.Revision <= before.Revision)
                throw new ArgumentException("A changed context must advance its durable provider revision.");
        }
    }

    private async Task<T> DependencyChangeAsync<T>(ConversationIdentity identity,
        Func<LedgerState, OutputDependencies?> select, Func<LedgerState, Mutation<T>> change, CancellationToken cancellationToken)
    {
        var admitted = await ChangeAsync(state =>
        {
            var validation = DependencyValidation(identity, select(state), state.OutputDependenciesRequired);
            if (validation.Status != OutputDependencyStatus.Valid)
            {
                bool changed = validation.Status == OutputDependencyStatus.Invalid && RefreshDependencies(state, identity) > 0;
                return new Mutation<(T? Value, OutputDependencyValidation Validation)>((default, validation), changed);
            }
            var mutation = change(state);
            return new Mutation<(T? Value, OutputDependencyValidation Validation)>((mutation.Value, validation), mutation.Changed);
        }, cancellationToken).ConfigureAwait(false);
        RequireValidDependency(admitted.Validation); return admitted.Value!;
    }

    private static void ValidateDependencyDefinition(OutputDependencies dependencies)
    {
        if (dependencies == null || !Enum.IsDefined(dependencies.Mode) || dependencies.Contexts.IsDefault || dependencies.Contexts.Length > 16
            || (dependencies.Mode == OutputDependencyMode.Bound) != (dependencies.Contexts.Length > 0)) throw new ArgumentException("Invalid output dependency mode or contexts.");
        if (dependencies.Contexts.Any(x => x == null) || dependencies.Contexts.Select(x => (x.Provider, x.ScopeKey)).Distinct().Count() != dependencies.Contexts.Length)
            throw new ArgumentException("Duplicate dependency scope.");
        foreach (var context in dependencies.Contexts)
        {
            ValidateToken(context.Provider); ValidateToken(context.ScopeKey); ValidateToken(context.Fingerprint);
            if (context.Revision < 0 || context.ValidUntilUtc is { } expiry && (expiry == default || expiry.Offset != TimeSpan.Zero)
                || context.Items.IsDefault || context.Items.Length > 2048 || context.Items.Any(x => x == null)
                || context.Items.Select(x => x.Id).Distinct().Count() != context.Items.Length) throw new ArgumentException("Invalid context revision or item references.");
            foreach (var item in context.Items) { ValidateToken(item.Id); if (item.Version < 1) throw new ArgumentException("Invalid context item version."); }
        }
    }

    private static void ValidateOutputDependencyState(LedgerState state)
    {
        try
        {
            bool enabled = state.OutputDependenciesRequired || state.Events.Any(x => x.OutputDependencies != null || x.RelatedInvocationId != null)
                || state.Generations.Any(x => x.GenerationContext != null || x.ContextTransitions != null || x.DependencyInvalidationReason != null)
                || state.ToolInvocations.Any(x => x.GenerationContext != null) || state.Outbox.Any(x => x.DependencyInvalidationReason != null);
            if (enabled && state.SchemaVersion < 3) throw new ArgumentException("Missing dependency feature version gate.");
            foreach (var entry in state.Events)
            {
                if (entry.OutputDependencies != null) ValidateDependencyDefinition(entry.OutputDependencies);
                if (entry.RelatedInvocationId is { } id)
                {
                    var invocation = state.ToolInvocations.SingleOrDefault(x => x.Id == id && x.Identity == entry.Identity);
                    if (invocation == null || entry.Source != ConversationSource.ToolResult || entry.RelatedOperationId != invocation.OperationId
                        || !OutputDependencies.Equivalent(entry.OutputDependencies, invocation.GenerationContext?.Dependencies))
                        throw new ArgumentException("Tool result dependency provenance is invalid.");
                }
            }
            foreach (var generation in state.Generations)
            {
                if (generation.GenerationContext == null)
                { if (generation.ContextTransitions is { Length: > 0 }) throw new ArgumentException("Unbound context transitions."); continue; }
                var context = generation.GenerationContext;
                ValidateDependencyDefinition(context.Dependencies);
                var transitions = generation.ContextTransitions ?? [];
                if (context.GenerationId != generation.Id || context.Epoch < 0 || context.Epoch != transitions.Length || context.Epoch > 64)
                    throw new ArgumentException("Invalid generation context epoch.");
                OutputDependencies? previous = null;
                foreach (var (transition, index) in transitions.Select((item, index) => (item, index)))
                {
                    ValidateDependencyDefinition(transition.Before); ValidateDependencyDefinition(transition.After);
                    ValidateTransitionProgress(transition);
                    var invocation = state.ToolInvocations.SingleOrDefault(x => x.Id == transition.InvocationId && x.Identity == generation.Identity);
                    var operation = state.Operations.SingleOrDefault(x => x.Id == transition.OperationId && x.Identity == generation.Identity);
                    if (transition.GenerationId != generation.Id || transition.ExpectedEpoch != index || invocation?.GenerationId != generation.Id
                        || invocation.OperationId != operation?.Id || invocation.Stage != ToolInvocationStage.Completed || !invocation.HasSideEffects
                        || operation?.Stage != OperationStage.Completed || invocation.GenerationContext?.Epoch != index
                        || invocation.SourceMessageId != operation.SourceMessageId || invocation.ToolName != operation.Action
                        || invocation.ArgumentsJson != operation.ArgumentsJson
                        || !OutputDependencies.Equivalent(invocation.GenerationContext?.Dependencies, transition.Before)
                        || previous != null && !OutputDependencies.Equivalent(previous, transition.Before)
                        || transition.Before.Mode != OutputDependencyMode.Bound || transition.After.Mode != OutputDependencyMode.Bound)
                        throw new ArgumentException("Invalid completed operation context proof.");
                    previous = transition.After;
                }
                if (previous != null && !OutputDependencies.Equivalent(previous, context.Dependencies)) throw new ArgumentException("Context epoch chain is inconsistent.");
            }
            foreach (var invocation in state.ToolInvocations.Where(x => x.GenerationContext != null))
            {
                var generation = state.Generations.Single(x => x.Id == invocation.GenerationId);
                var context = invocation.GenerationContext!; ValidateDependencyDefinition(context.Dependencies);
                if (context.GenerationId != generation.Id || context.Epoch < 0 || context.Epoch > generation.GenerationContext?.Epoch)
                    throw new ArgumentException("Invalid invocation capture.");
                var expected = context.Epoch == generation.GenerationContext?.Epoch ? generation.GenerationContext.Dependencies
                    : generation.ContextTransitions!.Value[context.Epoch].Before;
                if (!OutputDependencies.Equivalent(context.Dependencies, expected)) throw new ArgumentException("Invocation context does not match its actual epoch.");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        { throw new LedgerException(LedgerErrors.CorruptStorage, "Invalid durable output dependencies.", error); }
    }
}
