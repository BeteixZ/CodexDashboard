using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// The ledger sees applicability proofs only. Actual rules and memory prose stay
// in this host and are frozen once for each generation/context epoch.
internal sealed class AssistantModelContextProvider : IModelContextProvider
{
    private const string ExplicitReadPolicy = "assistant-explicit-memory-read-policy";
    private const string ExplicitReadMemory = "assistant-explicit-memory-read";
    private readonly Settings settings;
    private readonly AssistantMemoryStore memory;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly Dictionary<(Guid Id, int Epoch), AssistantGenerationPersonalization> captures = new();
    private DurableConversationLedger? ledger;
    private IProjectMemoryResolver? projectResolver;

    internal AssistantModelContextProvider(Settings settings, AssistantMemoryStore memory, IClock? clock = null)
    {
        this.settings = settings; this.memory = memory;
        this.clock = () => (clock?.UtcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
    }

    internal void BindLedger(DurableConversationLedger value)
    {
        lock (gate)
        {
            if (ledger != null && !ReferenceEquals(ledger, value))
                throw new InvalidOperationException("The model context provider already belongs to another ledger.");
            ledger = value;
            memory.EpisodeSourceReader = (identity, eventId) => ReadEpisodeSource(value, identity, eventId);
            memory.EpisodeProjectCurrent = target =>
            {
                var binding = settings.WeChat.ReadBinding();
                if (binding == null || !settings.ChatGpt.IncludeTaskContext) return false;
                var identity = DurableAssistantRuntime.BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
                var context = value.ReadCommittedConversationContext(identity);
                return context is { PendingSelection: null, Focus: { } focus } && focus.Task == target.Task && ProjectCurrent(target);
            };
        }
    }

    private EpisodeSource? ReadEpisodeSource(DurableConversationLedger source, ConversationIdentity identity, Guid eventId)
    {
        if (!BindingMatches(identity)) return null;
        var evidence = source.ReadCommittedEventEvidence(identity, eventId);
        bool Usable(CommittedEventEvidence item) => !item.ToolNames.Any(name => name == "list_memories" || ConversationHistoryTools.Handles(name))
            && !item.InputTexts.Any(AssistantMemoryCommands.IsListCommand);
        if (evidence == null || !Usable(evidence)
            || !evidence.Members.IsDefaultOrEmpty && evidence.Members.Any(m => !Usable(m.Source))) return null;
        // Older generation records did not capture whether task context was included.
        // Do not reintroduce their generated prose while the user has disabled that context.
        if (!settings.ChatGpt.IncludeTaskContext && (evidence.Event.Task != null
            || evidence.Event.Source is ConversationSource.Assistant or ConversationSource.Notification or ConversationSource.ToolResult)) return null;
        return new(evidence.Event, evidence.Members);
    }

    internal async Task<EpisodeMemoryEditorSnapshot> CaptureEpisodeEditorAsync(string? summaryId, CancellationToken ct)
        => await CaptureEpisodeEditorAsync(summaryId, false, ct);

    internal async Task<EpisodeMemoryEditorSnapshot> CaptureProjectEpisodeEditorAsync(string? summaryId, CancellationToken ct)
        => await CaptureEpisodeEditorAsync(summaryId, true, ct);

    private async Task<EpisodeMemoryEditorSnapshot> CaptureEpisodeEditorAsync(string? summaryId, bool projectScope, CancellationToken ct)
    {
        var binding = settings.WeChat.ReadBinding() ?? throw new ChatGptException("请先连接当前微信绑定。");
        var identity = DurableAssistantRuntime.BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
        var saved = await Ledger.ReadConversationAsync(identity, ct);
        var project = projectScope ? await GetCurrentProjectAsync(ct) : null;
        if (projectScope && project == null) throw new ChatGptException("请先选定能够核对主机和目录的任务，再整理项目情景摘要。");
        if (!BindingMatches(identity)) throw new ChatGptException("账户或绑定已变化，请重新打开。");
        return memory.CaptureEpisodeSources(saved.Events.Select(e => ReadEpisodeSource(Ledger, identity, e.Id)).OfType<EpisodeSource>(), summaryId, project);
    }

    internal ImmutableArray<AssistantMemoryEntry> ReadEpisodeSummaries(GenerationContext context) =>
        memory.ReadGenerationEntries(context, clock()).Where(e => e.Metadata?.Events.IsEmpty == false).ToImmutableArray();

    internal EpisodeSource? ReadReviewedEpisodeSource(GenerationContext context, ConversationIdentity identity, Guid eventId) =>
        memory.ReadReviewedEpisodeSource(context, identity, eventId, clock());

    internal void BindProjectResolver(IProjectMemoryResolver value)
    {
        lock (gate)
        {
            if (projectResolver != null && !ReferenceEquals(projectResolver, value))
                throw new InvalidOperationException("The project resolver is already bound.");
            projectResolver = value;
        }
    }
    internal async Task<ProjectMemoryTarget?> GetCurrentProjectAsync(CancellationToken ct = default)
    {
        var binding = settings.WeChat.ReadBinding();
        if (binding == null) return null;
        var identity = DurableAssistantRuntime.BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
        var context = await Ledger.ReadConversationContextAsync(identity, ct);
        if (!BindingMatches(identity) || context.PendingSelection != null || context.Focus is not { } focus) return null;
        var target = projectResolver == null ? null : await projectResolver.PrepareAsync(focus.Task, ct);
        var latest = await Ledger.ReadConversationContextAsync(identity, ct);
        return BindingMatches(identity) && latest.PendingSelection == null && latest.Focus == focus ? target : null;
    }
    private bool ProjectCurrent(ProjectMemoryTarget? target)
    {
        if (target == null) return true;
        try
        {
            ProjectMemoryProof.Validate(target, clock());
            var current = projectResolver?.Resolve(target.Task);
            return current != null && ProjectMemoryProof.SameEvidence(target, current);
        }
        catch (Exception error) when (IsMemoryFailure(error)) { return false; }
    }

    public OutputDependencyValidation Validate(ConversationIdentity identity, OutputDependencies dependencies)
    {
        if (dependencies.Mode == OutputDependencyMode.Independent)
            return new(OutputDependencyStatus.Valid);
        if (dependencies.Mode != OutputDependencyMode.Bound)
            return new(OutputDependencyStatus.Unavailable, "output_context_unknown");
        try
        {
            if (!BindingMatches(identity)) return new(OutputDependencyStatus.Invalid, "memory_binding_changed");
            bool explicitRead = dependencies.Contexts.Any(item => item.Provider is ExplicitReadPolicy or ExplicitReadMemory);
            var project = ProjectMemoryProof.Read(dependencies);
            if (explicitRead && project != null || !ProjectCurrent(project))
                return new(OutputDependencyStatus.Invalid, "project_context_changed");
            var current = memory.CaptureForGeneration(explicitRead || settings.ChatGpt.PersonalizationEnabled, clock(), project);
            var actualDependencies = explicitRead ? ExplicitReadDependencies(current)
                : AssistantMemoryDependencies.ToOutputDependencies(current);
            return OutputDependencies.Equivalent(dependencies, actualDependencies)
                ? new(OutputDependencyStatus.Valid)
                : new(OutputDependencyStatus.Invalid, "memory_context_changed");
        }
        catch (Exception error) when (IsMemoryFailure(error))
        {
            return new(OutputDependencyStatus.Unavailable, "memory_context_unavailable");
        }
    }

    // Explicit management queries can read memory even when automatic
    // personalization is off. Bind the content actually read in that case.
    internal static OutputDependencies ExplicitReadDependencies(AssistantGenerationPersonalization capture)
    {
        if (!capture.Enabled || capture.Content == null)
            throw new InvalidOperationException("Explicit memory reads require a content capture.");
        var dependencies = AssistantMemoryDependencies.ToOutputDependencies(capture);
        return dependencies with { Contexts = dependencies.Contexts.Select(item => item with
        {
            Provider = item.Provider == AssistantMemoryDependencies.PolicyProvider ? ExplicitReadPolicy
                : item.Provider == AssistantMemoryDependencies.MemoryProvider ? ExplicitReadMemory : item.Provider
        }).ToImmutableArray() };
    }

    public async ValueTask<OutputDependencies> CaptureAsync(GenerationLease generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!BindingMatches(generation.Identity)) throw new ChatGptException("当前账户或微信绑定已经变化。");
        var savedLedger = Ledger;
        var saved = await savedLedger.ReadConversationAsync(generation.Identity, ct);
        var record = saved.Generations.Single(item => item.Id == generation.Id);
        var historical = memory.CaptureHistoryFilter(clock());
        // A deferred old question must not resurrect a revoked source merely by
        // becoming the current read-only work item.
        if (record.InputWorkId != null && record.InboxIds.Select(id => saved.Inbox.Single(item => item.Id == id))
            .Any(item => historical.IsHistoricalSourceRevoked(item.Identity, item.SourceMessageId, item.Revision)
                || historical.UnknownSourceHistoryCutoffUtc is { } cutoff && item.ReceivedAt <= cutoff))
            throw new ChatGptException("这条旧输入涉及已经撤销的记忆来源，需要用户重新明确当前问题。");
        var project = record.InputWorkId == null && saved.Context is { PendingSelection: null, Focus: { } focus }
            && settings.ChatGpt.PersonalizationEnabled && projectResolver != null
            ? await projectResolver.PrepareAsync(focus.Task, ct) : null;
        var latestContext = await savedLedger.ReadConversationContextAsync(generation.Identity, ct);
        if (project != null && (latestContext.PendingSelection != null || latestContext.Focus != saved.Context?.Focus))
            throw new ChatGptException("当前任务已经变化，这次没有继续使用旧项目内容。");
        var capture = memory.CaptureForGeneration(settings.ChatGpt.PersonalizationEnabled, clock(), project);
        if (!BindingMatches(generation.Identity)
            || !memory.IsHistoryFilterCurrent(historical, clock())
            || !ProjectCurrent(capture.Content?.Manifest?.Project)
            || !memory.IsGenerationPersonalizationCurrent(capture, settings.ChatGpt.PersonalizationEnabled, clock()))
            throw new ChatGptException("记忆或账户已经变化，这次没有继续使用旧内容。");
        lock (gate)
        {
            var key = (generation.Id, 0);
            if (captures.TryGetValue(key, out var existing))
            {
                if (!OutputDependencies.Equivalent(AssistantMemoryDependencies.ToOutputDependencies(existing),
                        AssistantMemoryDependencies.ToOutputDependencies(capture)))
                    throw new ChatGptException("这次生成的记忆快照已经失效。");
                return AssistantMemoryDependencies.ToOutputDependencies(existing);
            }
            captures.Add(key, capture);
        }
        return AssistantMemoryDependencies.ToOutputDependencies(capture);
    }

    internal AssistantGenerationPersonalization GetPersonalization(GenerationContext context)
    {
        AssistantGenerationPersonalization capture;
        lock (gate)
            capture = captures.TryGetValue((context.GenerationId, context.Epoch), out var value)
                ? value : throw new ChatGptException("没有这次生成对应的记忆快照。");
        if (!OutputDependencies.Equivalent(context.Dependencies, AssistantMemoryDependencies.ToOutputDependencies(capture))
            || !ProjectCurrent(capture.Content?.Manifest?.Project)
            || !memory.IsGenerationPersonalizationCurrent(capture, settings.ChatGpt.PersonalizationEnabled, clock()))
            throw new ChatGptException("这次生成使用的记忆已经变化。");
        return capture;
    }

    public void Release(GenerationLease generation)
    {
        lock (gate)
            foreach (var key in captures.Keys.Where(key => key.Id == generation.Id).ToArray()) captures.Remove(key);
    }

    public ValueTask<bool> AcceptTransitionAsync(ConversationIdentity identity, GenerationContext context,
        ContextDependencyTransition transition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (transition.GenerationId != context.GenerationId || transition.ExpectedEpoch != context.Epoch
            || transition.InvocationId == Guid.Empty || transition.OperationId == Guid.Empty
            || !OutputDependencies.Equivalent(transition.Before, context.Dependencies) || !BindingMatches(identity))
            return ValueTask.FromResult(false);
        try
        {
            AssistantGenerationPersonalization before;
            lock (gate)
                if (!captures.TryGetValue((context.GenerationId, context.Epoch), out before!))
                    return ValueTask.FromResult(false);
            if (!OutputDependencies.Equivalent(AssistantMemoryDependencies.ToOutputDependencies(before), transition.Before))
                return ValueTask.FromResult(false);
            var project = before.Content?.Manifest?.Project;
            var nextProject = ProjectMemoryProof.Read(transition.After);
            if (!ProjectCurrent(project) || nextProject != null && nextProject != project) return ValueTask.FromResult(false);
            var after = memory.CaptureForGeneration(settings.ChatGpt.PersonalizationEnabled, clock(), project);
            if (!OutputDependencies.Equivalent(AssistantMemoryDependencies.ToOutputDependencies(after), transition.After)
                || !BindingMatches(identity)) return ValueTask.FromResult(false);
            lock (gate)
            {
                var key = (context.GenerationId, checked(context.Epoch + 1));
                if (captures.TryGetValue(key, out var existing)
                    && !OutputDependencies.Equivalent(AssistantMemoryDependencies.ToOutputDependencies(existing), transition.After))
                    return ValueTask.FromResult(false);
                captures[key] = after;
            }
            return ValueTask.FromResult(true);
        }
        catch (Exception error) when (IsMemoryFailure(error))
        {
            return ValueTask.FromResult(false);
        }
    }

    public async ValueTask<ModelContextSelection> FilterAsync(ConversationIdentity identity, GenerationContext context,
        ImmutableArray<ConversationEvent> events, ImmutableArray<ModelToolObservation> observations, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Validate(identity, context.Dependencies).Status != OutputDependencyStatus.Valid)
            throw new ChatGptException("用于回答的记忆已经变化或暂时无法读取。");
        var saved = await Ledger.ReadConversationAsync(identity, ct);
        var generation = saved.Generations.Single(item => item.Id == context.GenerationId);
        var history = memory.CaptureHistoryFilter(clock());
        var selectedProject = ProjectMemoryProof.Read(context.Dependencies);
        bool SameProject(OutputDependencies? dependencies)
        {
            try
            {
                var prior = ProjectMemoryProof.Read(dependencies);
                return prior == null || selectedProject != null && prior.Project == selectedProject.Project;
            }
            catch (Exception error) when (IsMemoryFailure(error)) { return false; }
        }
        bool Applicable(OutputDependencies dependencies) => SameProject(dependencies)
            && Validate(identity, dependencies).Status == OutputDependencyStatus.Valid;
        if (generation.InputWorkId != null && generation.InboxIds.Select(id => saved.Inbox.Single(item => item.Id == id))
            .Any(item => history.IsHistoricalSourceRevoked(item.Identity, item.SourceMessageId, item.Revision)
                || history.UnknownSourceHistoryCutoffUtc is { } cutoff && item.ReceivedAt <= cutoff))
            throw new ChatGptException("这条旧输入涉及已经撤销的记忆来源，需要用户重新明确当前问题。");
        var current = generation.InputWorkId == null
            ? saved.Inbox.SingleOrDefault(item => item.Revision == generation.Revision) : null;
        bool IsCurrentSource(ConversationEvent item) => current != null
            && item.SourceMessageId == current.SourceMessageId && item.Revision == current.Revision;
        bool BeforeUnknownBoundary(DateTimeOffset at) =>
            history.UnknownSourceHistoryCutoffUtc is { } cutoff && at <= cutoff;
        bool InvocationRevoked(ToolInvocationRecord invocation) =>
            history.IsHistoricalInvocationRevoked(invocation.Id)
            || history.IsHistoricalSourceRevoked(identity, invocation.SourceMessageId, invocation.Revision)
            || BeforeUnknownBoundary(invocation.UpdatedAt)
            || !Applicable(invocation.GenerationContext?.Dependencies ?? OutputDependencies.LegacyUnknown);
        ImmutableArray<AssistantMemoryEntry>? activeEntries = null;
        bool ActiveOriginalSource(ConversationEvent entry, ToolInvocationRecord invocation)
        {
            if (!context.Dependencies.Contexts.Any(item => item.Provider == AssistantMemoryDependencies.MemoryProvider)) return false;
            activeEntries ??= memory.ReadGenerationEntries(context, clock());
            return activeEntries.Value.Any(item => item.Provenance is { } source && source.Identity == identity
                && source.SourceMessageId == entry.SourceMessageId && source.SourceRevision == entry.Revision
                && source.InvocationId == invocation.Id);
        }
        bool HistoricalManagementOutput(OutboxMessage output)
        {
            if (output.GenerationId is not { } generationId) return false;
            var producedBy = saved.Generations.SingleOrDefault(item => item.Id == generationId);
            return producedBy != null && (saved.Inbox.Any(item => producedBy.InboxIds.Contains(item.Id)
                    && AssistantMemoryCommands.IsListCommand(item.Body))
                || saved.ToolInvocations.Any(item => item.GenerationId == generationId && (item.ToolName == "list_memories" || ConversationHistoryTools.Handles(item.ToolName))));
        }
        bool KeepEvent(ConversationEvent item)
        {
            if (item.Source is ConversationSource.TrustedUser or ConversationSource.UntrustedContent
                && IsCurrentSource(item)) return true;
            if (item.SourceMessageId is { } source
                && history.IsHistoricalSourceRevoked(identity, source, item.Revision)) return false;
            // A save changes the memory version. Its original user source remains
            // usable only while an applicable entry still names that exact source
            // and invocation; repeated/no-op requests keep their dependency checks.
            if (item.Source == ConversationSource.TrustedUser && saved.ToolInvocations.Any(invocation =>
                invocation.SourceMessageId == item.SourceMessageId && invocation.Revision == item.Revision
                && invocation.ToolName is "remember" or "update_memory" or "forget" or "learn_preference"
                && InvocationRevoked(invocation) && !ActiveOriginalSource(item, invocation))) return false;
            if (item.Source is ConversationSource.TrustedUser or ConversationSource.UntrustedContent
                && BeforeUnknownBoundary(item.RecordedAt)) return false;
            if (item.Source == ConversationSource.Assistant || item.SourceMessageId?.StartsWith("channel-reference/", StringComparison.Ordinal) == true)
            {
                var output = saved.Outbox.SingleOrDefault(row => row.Id == item.RelatedOutboxId);
                // Management reads are not reusable model context. Older versions
                // could include project prose with only a personal dependency.
                return output != null && !HistoricalManagementOutput(output)
                    && Applicable(DurableConversationLedger.ResolveOutputDependencies(saved, output));
            }
            if (item.ExternalEventKey?.StartsWith("catch-up/", StringComparison.Ordinal) == true)
            {
                var pages = saved.Outbox.Where(output => output.NotificationEventId == item.Id).ToArray();
                return pages.Length > 0 && pages.All(output => Applicable(DurableConversationLedger.ResolveOutputDependencies(saved, output)));
            }
            if (item.OutputDependencies is { } dependencies
                && !Applicable(dependencies)) return false;
            if (item.Source == ConversationSource.Notification && item.NotificationSegments != null
                && item.OutputDependencies == null) return false;
            if (item.Source == ConversationSource.ToolResult)
            {
                var related = item.RelatedInvocationId is { } invocationId
                    ? saved.ToolInvocations.SingleOrDefault(invocation => invocation.Id == invocationId) : null;
                return related != null && !((related.ToolName == "list_memories" || ConversationHistoryTools.Handles(related.ToolName)) && related.GenerationId != context.GenerationId)
                    && !BeforeUnknownBoundary(item.RecordedAt) && !InvocationRevoked(related);
            }
            return true;
        }
        var filteredObservations = observations.Select(observation =>
        {
            var invocation = observation.InvocationId is { } id
                ? saved.ToolInvocations.SingleOrDefault(item => item.Id == id) : null;
            if (invocation == null)
                return observation with { ResultJson = JsonSerializer.Serialize(new
                { detailsOmitted = true, reason = "historical_invocation_identity_unavailable" }) };
            if (!InvocationRevoked(invocation)
                && !((invocation.ToolName == "list_memories" || ConversationHistoryTools.Handles(invocation.ToolName)) && invocation.GenerationId != context.GenerationId)) return observation;
            // Keep the durable operation outcome without returning revoked prose.
            return observation with { ResultJson = RedactedObservation(invocation) };
        }).ToImmutableArray();
        var filteredEvents = events.Where(KeepEvent).ToImmutableArray();
        var projectedContext = saved.Context;
        if (projectedContext?.Focus is { } focus)
        {
            var source = saved.Inbox.SingleOrDefault(item => item.SourceMessageId == focus.SourceMessageId);
            if (source == null || history.IsHistoricalSourceRevoked(identity, focus.SourceMessageId, source.Revision)
                || BeforeUnknownBoundary(source.ReceivedAt))
                projectedContext = projectedContext with { Focus = focus with { SourceExcerpt = "" } };
        }
        if (!memory.IsHistoryFilterCurrent(history, clock())
            || Validate(identity, context.Dependencies).Status != OutputDependencyStatus.Valid)
            throw new ChatGptException("记忆来源在整理上下文时已经变化。");
        return new(filteredEvents, filteredObservations, projectedContext);
    }

    private DurableConversationLedger Ledger => ledger ?? throw new InvalidOperationException("A durable ledger is required.");
    private static string RedactedObservation(ToolInvocationRecord invocation)
    {
        var facts = new Dictionary<string, object?> { ["detailsOmitted"] = true, ["operationId"] = invocation.OperationId };
        // Only structural outcome facts survive. Never carry memory prose,
        // evidence excerpts, arguments, arbitrary reason text or tool output.
        if (invocation.ToolName is "remember" or "update_memory" or "forget" or "learn_preference")
        {
            try
            {
                using var document = JsonDocument.Parse(invocation.ResultJson ?? "{}");
                var original = document.RootElement;
                if (original.TryGetProperty("facts", out var nested)) original = nested;
                static string? MemoryId(JsonElement item) => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("memoryId", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: 8 } id && id.All(char.IsAsciiHexDigit) ? id : null;
                if (MemoryId(original) is { } id) facts["memoryId"] = id;
                foreach (var key in new[] { "saved", "updated", "deletedFromActiveMemory", "permissionGranted", "taskControlChanged" })
                    if (original.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        facts[key] = value.GetBoolean();
                if (original.TryGetProperty("saved", out var list) && list.ValueKind == JsonValueKind.Array)
                    facts["saved"] = list.EnumerateArray().Select(MemoryId).Where(id => id != null)
                        .Select(id => new { memoryId = id }).ToArray();
            }
            catch (JsonException) { }
        }
        return JsonSerializer.Serialize(new
        {
            status = invocation.Stage.ToString().ToLowerInvariant(), facts,
            reason = "historical_context_details_withheld"
        });
    }

    private bool BindingMatches(ConversationIdentity identity)
    {
        var binding = settings.WeChat.ReadBinding();
        return binding != null && DurableAssistantRuntime.BindingIdentity(binding, settings.ChatGpt.ActiveAccount) == identity;
    }
    private static bool IsMemoryFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or JsonException or FormatException or ChatGptException or ArgumentException;
}
