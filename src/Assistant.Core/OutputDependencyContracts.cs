using System.Collections.Immutable;

namespace Assistant.Core;

public enum OutputDependencyMode { LegacyUnknown, Independent, Bound }
public enum OutputDependencyStatus { Valid, Invalid, Unavailable }
public sealed record ContextItemVersion(string Id, long Version);
public sealed record ContextDependency(string Provider, string ScopeKey, long Revision, string Fingerprint,
    DateTimeOffset? ValidUntilUtc, ImmutableArray<ContextItemVersion> Items);
public sealed record OutputDependencies(OutputDependencyMode Mode, ImmutableArray<ContextDependency> Contexts)
{
    public static OutputDependencies Independent { get; } = new(OutputDependencyMode.Independent, []);
    public static OutputDependencies LegacyUnknown { get; } = new(OutputDependencyMode.LegacyUnknown, []);
    public static bool Equivalent(OutputDependencies? first, OutputDependencies? second)
    {
        first ??= LegacyUnknown; second ??= LegacyUnknown;
        if (first.Mode != second.Mode || first.Contexts.IsDefault || second.Contexts.IsDefault || first.Contexts.Length != second.Contexts.Length) return false;
        var a = first.Contexts.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ScopeKey, StringComparer.Ordinal).ToArray();
        var b = second.Contexts.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ScopeKey, StringComparer.Ordinal).ToArray();
        return a.Zip(b).All(pair => pair.First.Provider == pair.Second.Provider && pair.First.ScopeKey == pair.Second.ScopeKey
            && pair.First.Revision == pair.Second.Revision && pair.First.Fingerprint == pair.Second.Fingerprint
            && pair.First.ValidUntilUtc == pair.Second.ValidUntilUtc && !pair.First.Items.IsDefault && !pair.Second.Items.IsDefault
            && pair.First.Items.OrderBy(x => x.Id, StringComparer.Ordinal).SequenceEqual(pair.Second.Items.OrderBy(x => x.Id, StringComparer.Ordinal)));
    }
}
public sealed record OutputDependencyValidation(OutputDependencyStatus Status, string? Reason = null);
public sealed record GenerationContext(Guid GenerationId, int Epoch, OutputDependencies Dependencies);
/// <summary>Trusted executor evidence, outside model-visible Facts. It never grants new user authority.</summary>
public sealed record ContextDependencyTransition(Guid GenerationId, int ExpectedEpoch, Guid InvocationId, Guid OperationId,
    OutputDependencies Before, OutputDependencies After);
public sealed record ModelContextSelection(ImmutableArray<ConversationEvent> Context, ImmutableArray<ModelToolObservation> Observations,
    ConversationContext? ConversationContext = null);

/// <summary>Fast local validation only: no network, mutation or ledger reentry while its transaction lock is held.</summary>
public interface IOutputDependencyValidator
{
    OutputDependencyValidation Validate(ConversationIdentity identity, OutputDependencies dependencies);
}
public interface IModelContextProvider : IOutputDependencyValidator
{
    ValueTask<OutputDependencies> CaptureAsync(GenerationLease generation, CancellationToken cancellationToken);
    ValueTask<ModelContextSelection> FilterAsync(ConversationIdentity identity, GenerationContext context,
        ImmutableArray<ConversationEvent> events, ImmutableArray<ModelToolObservation> observations, CancellationToken cancellationToken);
    ValueTask<bool> AcceptTransitionAsync(ConversationIdentity identity, GenerationContext current,
        ContextDependencyTransition transition, CancellationToken cancellationToken);
    void Release(GenerationLease generation) { }
}
