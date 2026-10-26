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

internal sealed partial class ChatGptAssistant
{
    internal AssistantMemoryStore Memory { get; }
    // Scope-only UI notification. It must not feed the general Changed event,
    // whose host listeners can reconfigure the channel.
    internal event Action? MemoryContextChanged;
    internal void NotifyMemoryContextChanged()
    {
        var listeners = MemoryContextChanged?.GetInvocationList();
        if (listeners == null) return;
        foreach (Action listener in listeners)
        {
            try { listener(); }
            catch { /* A view observer cannot stop the connection lifecycle. */ }
        }
    }
    internal SummaryMemoryEditorWindow CreateSummaryMemoryEditor(string? summaryId = null) => new(Memory, summaryId);
    internal Func<string?, CancellationToken, Task<EpisodeMemoryEditorSnapshot>>? EpisodeMemoryProvider { get; set; }
    internal Func<string?, CancellationToken, Task<EpisodeMemoryEditorSnapshot>>? ProjectEpisodeMemoryProvider { get; set; }
    internal async Task<EpisodeMemoryEditorWindow> CreateEpisodeMemoryEditorAsync(string? id, CancellationToken ct, bool projectScope = false)
    {
        if (id != null) projectScope = Memory.Entries.Single(e => e.Id == id).Metadata?.Scope.Kind == AssistantMemoryScopeKind.Project;
        Func<string?, CancellationToken, Task<EpisodeMemoryEditorSnapshot>>? CurrentProvider() => projectScope ? ProjectEpisodeMemoryProvider : EpisodeMemoryProvider;
        var provider = CurrentProvider() ?? throw new ChatGptException("情景历史尚未连接，请先启动助手。");
        string profile = Memory.Profile;
        var snapshot = await provider(id, ct);
        if (profile != Memory.Profile || !ReferenceEquals(provider, CurrentProvider()))
            throw new ChatGptException("历史来源已经切换，请重新打开。");
        return new(Memory, snapshot, (captured, sources, cancellation) => GenerateEpisodeDraftAsync(captured, sources,
            cancellation, () => ReferenceEquals(provider, CurrentProvider())));
    }
    private readonly AssistantMemoryCommands memoryCommands;
    private string MemoryIdentity()
    {
        var binding = settings.WeChat.ReadBinding();
        // Tokens/cursors rotate; scope depends only on stable owner identity.
        return settings.ChatGpt.ActiveAccount + "\n" + (binding?.BaseUrl ?? "") + "\n" + (binding?.BotId ?? "") + "\n" + (binding?.UserId ?? "");
    }
    private void MemoryChanged() { Reset(); Changed?.Invoke(); }
    internal int MemoryCount { get { try { return Memory.Entries.Count; } catch { return -1; } } }
    internal static bool IsAutoMemoryCommand(string text) => text is "开启自动记忆" or "关闭自动记忆" or "自动记忆状态";
    public bool IsMemoryCommand(string text) => IsAutoMemoryCommand(text) || memoryCommands.CanHandle(text);
    public bool TryMemoryCommand(string text, out string reply)
    {
        if (!IsAutoMemoryCommand(text)) return memoryCommands.TryHandle(text, out reply);
        if (text != "自动记忆状态") SetAutoMemoryEnabled(text == "开启自动记忆");
        reply = "自动记忆：" + (settings.ChatGpt.AutoMemoryEnabled ? "开启" : "关闭")
            + (settings.ChatGpt.PersonalizationEnabled ? "" : "（规则与长期记忆未启用，暂不提取）")
            + "。仅从当前微信消息提取长期偏好，不保存临时任务要求。发送“查看记忆”可核对、修改或删除。";
        return true;
    }
    bool IWeChatLocalCommands.CanReplyLocally(string text) => IsLocalCommand(text) || IsMemoryCommand(text);
    internal void SetPersonalizationEnabled(bool enabled)
    {
        if (settings.ChatGpt.PersonalizationEnabled == enabled) return;
        Memory.AdvancePersonalizationRevision(); // Fence the old setting before changing it, including failed persistence.
        bool prior = settings.ChatGpt.PersonalizationEnabled;
        settings.ChatGpt.PersonalizationEnabled = enabled; Reset();
        try { persist(); }
        catch { settings.ChatGpt.PersonalizationEnabled = prior; throw; }
        Changed?.Invoke();
    }
    internal void SetAutoMemoryEnabled(bool enabled)
    {
        if (settings.ChatGpt.AutoMemoryEnabled == enabled) return;
        bool prior = settings.ChatGpt.AutoMemoryEnabled;
        settings.ChatGpt.AutoMemoryEnabled = enabled; Reset();
        try { persist(); }
        catch { settings.ChatGpt.AutoMemoryEnabled = prior; throw; }
        Changed?.Invoke();
    }
    private AutomaticMemoryGuard? CaptureAutoMemory(string text)
    {
        if (!Enabled || !settings.ChatGpt.PersonalizationEnabled || !settings.ChatGpt.AutoMemoryEnabled || !AutomaticPreferenceText.MayLearn(text)) return null;
        try { return new(Memory.Capture().Stamp, settings.ChatGpt.Model, progressRevision); }
        catch (Exception ex) when (ex is ChatGptException or IOException or UnauthorizedAccessException) { return null; }
    }
    private bool LearnPreferences(AutomaticMemoryGuard? guard, IReadOnlyList<AutomaticPreference> preferences)
    {
        if (guard == null || !Enabled || !settings.ChatGpt.AutoMemoryEnabled || !settings.ChatGpt.PersonalizationEnabled
            || guard.Model != settings.ChatGpt.Model || guard.ConversationRevision != progressRevision) return false;
        try { return Memory.LearnPreferences(preferences, guard.Stamp) > 0; }
        catch (Exception ex) when (ex is ChatGptException or IOException or UnauthorizedAccessException) { return false; }
    }
}

// This host guard captures read-only provenance before inference. Only the explicit,
// audited tool execution below may change memory or its automatic-learning setting.
internal sealed class AutomaticPreferenceTools
{
    private sealed record Capture(InboxMessage Message, string? Stamp, string Model, long Revision);
    private readonly Settings settings;
    private readonly AssistantMemoryStore memory;
    private readonly DurableConversationLedger ledger;
    private readonly Func<ConversationIdentity, bool> isCurrentBinding;
    private readonly Func<long> generationRevision;
    private readonly Action<bool> setAutoMemory;
    private readonly object gate = new();
    private Capture? capture;
    private readonly HashSet<Guid> attemptedOperations = new();
    private ConversationIdentity? attemptedIdentity;
    private long attemptedRevision;
    internal AutomaticPreferenceTools(Settings settings, AssistantMemoryStore memory, DurableConversationLedger ledger,
        Func<ConversationIdentity, bool> isCurrentBinding, Func<long> generationRevision, Action<bool> setAutoMemory)
    {
        this.settings = settings; this.memory = memory; this.ledger = ledger;
        this.isCurrentBinding = isCurrentBinding; this.generationRevision = generationRevision; this.setAutoMemory = setAutoMemory;
    }
    internal static ToolDescriptor LearnDescriptor => new("learn_preference",
        "从当前用户完整原文分句保存1–2项清楚表达的长期偏好或称呼。无需说记住，但必须为本人的稳定设定；不能保存问题、引用、临时要求或权限。仅工具成功回执后才能说已保存。",
        ToolSchema.Object(("preferences", new ToolSchema
        {
            Type = ToolValueType.Array, MaximumItems = 2,
            Items = ToolSchema.Object(
                ("key", ToolSchema.String(32) with { AllowedStrings = AutomaticPreferenceText.AllowedKeys }, true),
                ("evidence", ToolSchema.String(400), true))
        }, true)), true, "learn_preference");
    internal static ToolDescriptor ToggleDescriptor => new("set_auto_memory",
        "仅按当前用户完整的“开启自动记忆”或“关闭自动记忆”设置自动偏好学习；不删除已有记忆。",
        ToolSchema.Object(("enabled", new ToolSchema { Type = ToolValueType.Boolean }, true)), true, "set_auto_memory");
    internal bool LearningAvailable => settings.ChatGpt.Enabled && settings.ChatGpt.PersonalizationEnabled
        && settings.ChatGpt.AutoMemoryEnabled;
    internal IToolModel WrapModel(IToolModel inner) => new CapturingModel(this, inner);
    private sealed class CapturingModel(AutomaticPreferenceTools owner, IToolModel inner) : IToolModel
    {
        public async Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
        {
            await owner.CaptureBeforeGenerationAsync(request, ct);
            return await inner.GenerateAsync(request, ct);
        }
    }
    private async Task CaptureBeforeGenerationAsync(ToolModelRequest request, CancellationToken ct)
    {
        if (request.DeferredWork != null) return;
        var current = await ReadCurrentAsync(request.CurrentMessage.Identity,
            request.CurrentMessage.SourceMessageId, request.Revision, ct);
        if (current == null || !SameSource(current, request.CurrentMessage)) return;
        lock (gate)
        {
            // A later tool/model turn cannot recapture a newer stamp after deletion,
            // an editor change, an opt-out or a previous tool's own memory write.
            if (capture?.Message.Identity == current.Identity && capture.Message.SourceMessageId == current.SourceMessageId
                && capture.Message.Revision == current.Revision) return;
            string? stamp = null;
            if (LearningAvailable && AutomaticPreferenceText.MayLearn(current.Body) && isCurrentBinding(current.Identity))
            {
                try { stamp = memory.Capture().Stamp; }
                catch (Exception error) when (error is ChatGptException or IOException or UnauthorizedAccessException) { }
            }
            capture = new(current, stamp, settings.ChatGpt.Model, generationRevision());
        }
    }
    internal async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
    {
        var descriptor = Descriptor(request.Call.Name);
        if (descriptor == null || !descriptor.Parameters.TryValidate(request.Call.ArgumentsJson, out _, out _))
            return Deny("偏好参数不完整或包含未定义字段。");
        var current = await ReadCurrentAsync(request.CurrentMessage.Identity, request.CurrentMessage.SourceMessageId,
            request.CurrentMessage.Revision, ct);
        if (current == null || !SameSource(current, request.CurrentMessage)) return Deny("当前用户或微信绑定已变化。");
        using var document = JsonDocument.Parse(request.Call.ArgumentsJson);
        if (request.Call.Name == "set_auto_memory")
        {
            bool enabled = document.RootElement.GetProperty("enabled").GetBoolean();
            if (current.Body.Trim() != (enabled ? "开启自动记忆" : "关闭自动记忆"))
                return Deny("需要用户本条明确开启或关闭自动记忆。");
            return new(true, "settings/auto_memory", current.Body);
        }
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!ValidCapture(current) || !AutomaticPreferenceText.TryReadTool(document.RootElement, current.Body, out var preferences))
                return Deny("只保存当前用户完整原文中可核对的长期偏好；这次未保存。");
            return new(true, PreferenceTarget(preferences), current.Body);
        }
    }
    internal async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        var descriptor = Descriptor(call.Name);
        if (descriptor == null || context.InputWorkId != null || context.ReadAtRevision != null
            || context.OperationId is not { } operationId || operationId == Guid.Empty || context.InvocationId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _))
            return Rejected("durable_operation_required");
        var snapshot = await ledger.ReadConversationAsync(context.Identity, ct);
        var current = Current(snapshot, context.SourceMessageId, context.Revision);
        var invocation = snapshot.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        var operation = snapshot.Operations.SingleOrDefault(o => o.Id == operationId);
        var grant = operation == null ? null : snapshot.Authorizations.SingleOrDefault(a => a.Id == operation.AuthorizationId);
        if (current == null || invocation == null || operation == null || grant == null
            || invocation.Identity != context.Identity || invocation.SourceMessageId != current.SourceMessageId
            || invocation.Revision != context.Revision || invocation.CallId != call.Id || invocation.ToolName != call.Name
            || invocation.ArgumentsJson != canonical || !invocation.HasSideEffects || invocation.Stage != ToolInvocationStage.Executing
            || invocation.OperationId != operation.Id || operation.Stage != OperationStage.Dispatching
            || operation.IdempotencyKey != context.IdempotencyKey || operation.Action != call.Name
            || operation.ArgumentsJson != canonical || operation.SourceMessageId != current.SourceMessageId
            || operation.Identity != context.Identity || operation.Revision != context.Revision
            || grant.Action != call.Name || grant.ArgumentsJson != canonical || grant.Identity != context.Identity
            || grant.SourceMessageId != current.SourceMessageId || grant.Revision != context.Revision
            || grant.SourceExcerpt != current.Body || grant.TargetKey != operation.TargetKey
            || grant.Task != null || operation.Task != null)
            return Rejected("durable_execution_mismatch");
        var generation = snapshot.Generations.SingleOrDefault(g => g.Id == invocation.GenerationId);
        if (generation == null || generation.Stage != GenerationStage.Processing || generation.InputWorkId != null
            || generation.Identity != context.Identity || generation.Revision != context.Revision
            || !generation.InboxIds.Contains(current.Id) || !AssistantMemoryDependencies.MatchesExecutionContext(context, generation, invocation))
            return Rejected("generation_not_current");
        ct.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(canonical);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!isCurrentBinding(context.Identity)) return Rejected("binding_changed");
            // Bound the in-process second-call fence to the current input. Persisted
            // Operation/Invocation stages remain the authority across restart.
            if (attemptedIdentity != context.Identity || attemptedRevision != context.Revision)
            {
                attemptedOperations.Clear();
                attemptedIdentity = context.Identity; attemptedRevision = context.Revision;
            }
            if (attemptedOperations.Contains(operationId)) return Rejected("operation_already_attempted");
            if (attemptedOperations.Count >= 32) return Rejected("preference_attempt_limit");
            if (call.Name == "set_auto_memory")
            {
                bool enabled = document.RootElement.GetProperty("enabled").GetBoolean();
                if (operation.TargetKey != "settings/auto_memory" || current.Body.Trim() != (enabled ? "开启自动记忆" : "关闭自动记忆"))
                    return Rejected("toggle_source_mismatch");
                attemptedOperations.Add(operationId);
                capture = null; // Disabling then re-enabling cannot validate a result from an earlier inference.
                setAutoMemory(enabled);
                if (settings.ChatGpt.AutoMemoryEnabled != enabled) return Rejected("setting_not_applied");
                return new(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new
                {
                    automaticMemoryEnabled = settings.ChatGpt.AutoMemoryEnabled,
                    personalizationEnabled = settings.ChatGpt.PersonalizationEnabled, existingMemoriesDeleted = false
                }), TargetKey: operation.TargetKey);
            }
            if (!ValidCapture(current) || !AutomaticPreferenceText.TryReadTool(document.RootElement, current.Body, out var preferences)
                || operation.TargetKey != PreferenceTarget(preferences))
                return Rejected("preference_guard_changed");
            attemptedOperations.Add(operationId);
            string stamp = capture!.Stamp!;
            var before = memory.Entries;
            var projected = before.ToList();
            foreach (var preference in preferences)
            {
                if (projected.Any(e => AssistantMemoryStore.CanSatisfyPersonalPreference(e)
                    && AutomaticPreferenceText.Normalize(e.Text) == AutomaticPreferenceText.Normalize(preference.Evidence))) continue;
                var previous = projected.FirstOrDefault(e => AssistantMemoryStore.CanReplacePersonalPreference(e, preference.Key));
                // Simulate the same ordered personal-scope replacement and text
                // deduplication as the atomic store. These placeholders are never saved.
                var candidate = new AssistantMemoryEntry(previous?.Id ?? "preflight-only", preference.Evidence,
                    default, default, "automatic", preference.Key, Metadata: AssistantMemoryLayers.Initial(AssistantMemoryKind.Preference));
                if (previous == null) projected.Add(candidate); else projected[projected.IndexOf(previous)] = candidate;
                if (projected.Count > AssistantMemoryStore.MaxEntries || projected.Sum(entry => entry.Text.Length) > AssistantMemoryStore.MaxTotalChars)
                    return Rejected("memory_capacity_exceeded");
            }
            ct.ThrowIfCancellationRequested();
            AssistantMemoryMutation<int> mutation;
            try { mutation = memory.LearnPreferencesWithContext(preferences, stamp,
                AssistantMemoryProvenance.FromCurrentMessage("wechat_preference", memory.Profile, current, context), context.GenerationContext?.Dependencies); }
            catch (InvalidOperationException error) when (error.Message == "memory_generation_context_changed")
            { return Rejected("preference_guard_changed"); }
            int changed = mutation.Value;
            var after = memory.Entries;
            var saved = preferences.Select(p => new
            {
                key = p.Key, evidence = p.Evidence,
                memoryId = after.FirstOrDefault(e => AssistantMemoryStore.CanSatisfyPersonalPreference(e)
                    && AutomaticPreferenceText.Normalize(e.Text) == AutomaticPreferenceText.Normalize(p.Evidence))?.Id
            }).ToArray();
            if (saved.Any(p => p.memoryId == null))
                return new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new
                {
                    code = "preference_not_fully_saved", changed, saved, automaticRetry = false
                }), TargetKey: operation.TargetKey);
            return new(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(new
            {
                saved, changed, alreadyPresent = changed == 0 && before.Count == after.Count,
                permissionGranted = false, taskControlChanged = false
            }), TargetKey: operation.TargetKey, ContextTransition: AssistantMemoryDependencies.Transition(context, mutation));
        }
    }
    private async Task<InboxMessage?> ReadCurrentAsync(ConversationIdentity identity, string source, long revision, CancellationToken ct)
        => Current(await ledger.ReadConversationAsync(identity, ct), source, revision);
    private static bool SameSource(InboxMessage current, InboxMessage proposed)
        => current with { Stage = proposed.Stage, GenerationId = proposed.GenerationId } == proposed;
    private InboxMessage? Current(ConversationSnapshot snapshot, string source, long revision)
    {
        if (snapshot.IsReadOnly || snapshot.Revision != revision || !isCurrentBinding(snapshot.Identity)) return null;
        return snapshot.Inbox.SingleOrDefault(i => i.SourceMessageId == source && i.Revision == revision
            && i.Trust == MessageTrust.AuthenticatedUser);
    }
    private bool ValidCapture(InboxMessage current)
    {
        var saved = capture;
        if (saved?.Stamp == null || saved.Message.Identity != current.Identity
            || saved.Message.SourceMessageId != current.SourceMessageId || saved.Message.Body != current.Body
            || saved.Message.Revision != current.Revision || !LearningAvailable || !isCurrentBinding(current.Identity)
            || saved.Model != settings.ChatGpt.Model || saved.Revision != generationRevision()) return false;
        try { return memory.Capture().Stamp == saved.Stamp
            && !memory.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsHistoricalSourceRevoked(current.Identity, current.SourceMessageId, current.Revision); }
        catch (Exception error) when (error is ChatGptException or IOException or UnauthorizedAccessException) { return false; }
    }
    private string PreferenceTarget(ImmutableArray<AutomaticPreference> preferences)
        // Persist the pre-inference memory revision in the authorization target.
        // A queued operation surviving restart must not adopt a fresh stamp after
        // the user deleted or edited memory while this operation was pending.
        => "memory/preferences/" + memory.Profile + "/" + WeChatText.Hash(capture!.Stamp!)
            + "/" + string.Join(",", preferences.Select(p => p.Key).Order(StringComparer.Ordinal));
    private static ToolDescriptor? Descriptor(string name) => name switch
    {
        "learn_preference" => LearnDescriptor, "set_auto_memory" => ToggleDescriptor, _ => null
    };
    private static UserAuthorizationDecision Deny(string reason) => new(false, Reason: reason);
    private static ToolExecutionResult Rejected(string code)
        => new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code, saved = false, automaticRetry = false }));
}
