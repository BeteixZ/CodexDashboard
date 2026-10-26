using System;
using System.Collections.Immutable;
using System.Linq;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantMemoryDependencies
{
    internal const string PolicyProvider = "assistant-personalization-policy", MemoryProvider = "assistant-memory";
    internal static string ContentStamp(string profile, long revision, long rulesRevision, long policyRevision,
        string rulesHash, ImmutableArray<AssistantMemoryReference> references, ProjectMemoryTarget? project = null)
    {
        string stamp = profile + ":projection2:" + revision + ":" + rulesRevision + ":" + policyRevision + ":" + rulesHash + ":"
            + WeChatText.Hash(string.Join("|", references.Select(r => r.Id + ":" + r.Version)));
        return project == null ? stamp : stamp + ":project1:" + project.EvidenceFingerprint;
    }
    internal static OutputDependencies ToOutputDependencies(AssistantGenerationPersonalization capture)
    {
        var policy = new ContextDependency(PolicyProvider, capture.Profile, capture.PersonalizationRevision,
            WeChatText.Hash(capture.Enabled ? "enabled" : "disabled"), null, []);
        if (!capture.Enabled)
        {
            if (capture.Content != null) throw new InvalidOperationException("disabled_capture_contains_memory");
            return new(OutputDependencyMode.Bound, [policy]);
        }
        var content = capture.Content ?? throw new InvalidOperationException("personalization_capture_missing");
        var manifest = content.Manifest ?? throw new InvalidOperationException("personalization_manifest_missing");
        if (manifest.Profile != capture.Profile || manifest.PersonalizationRevision != capture.PersonalizationRevision)
            throw new InvalidOperationException("personalization_capture_scope_mismatch");
        var contexts = ImmutableArray.Create(new ContextDependency(MemoryProvider, manifest.Profile,
            manifest.DocumentRevision, WeChatText.Hash(content.Stamp), manifest.ValidUntilUtc,
            manifest.IncludedReferences.Select(r => new ContextItemVersion(r.Id, r.Version)).ToImmutableArray()), policy);
        if (manifest.Project != null) contexts = contexts.Add(ProjectMemoryProof.Dependency(manifest.Project));
        return new(OutputDependencyMode.Bound, contexts);
    }
    internal static ContextDependencyTransition? Transition<T>(ToolExecutionContext context, AssistantMemoryMutation<T> mutation)
    {
        if (context.GenerationContext is not { } generation || !mutation.Changed) return null;
        if (context.OperationId is not { } operation || operation == Guid.Empty || context.InvocationId == Guid.Empty)
            throw new InvalidOperationException("memory_transition_execution_missing");
        var before = ToOutputDependencies(mutation.Before); var after = ToOutputDependencies(mutation.After);
        if (!OutputDependencies.Equivalent(before, generation.Dependencies))
            throw new InvalidOperationException("memory_transition_before_mismatch");
        return new(generation.GenerationId, generation.Epoch, context.InvocationId, operation, before, after);
    }
    internal static bool MatchesExecutionContext(ToolExecutionContext context, GenerationRecord generation, ToolInvocationRecord invocation)
    {
        var requested = context.GenerationContext; var recorded = generation.GenerationContext; var called = invocation.GenerationContext;
        if (requested == null) return recorded == null && called == null;
        return recorded != null && called != null && requested.GenerationId == generation.Id
            && recorded.GenerationId == generation.Id && called.GenerationId == generation.Id
            && requested.Epoch == recorded.Epoch && requested.Epoch == called.Epoch
            && OutputDependencies.Equivalent(requested.Dependencies, recorded.Dependencies)
            && OutputDependencies.Equivalent(requested.Dependencies, called.Dependencies);
    }
}
