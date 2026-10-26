using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// The resolver is syntax only. This adapter binds that resolution to the actual
// authenticated inbox, durable dispatch and the memory version being changed.
internal sealed class ManualMemoryTools(Settings settings, AssistantMemoryStore memory,
    DurableConversationLedger? ledger = null, Func<ConversationIdentity, bool>? currentBinding = null)
{
    internal static bool Handles(string name) => name is "remember" or "forget" or "update_memory";
    private bool CurrentBinding(ConversationIdentity identity) => currentBinding?.Invoke(identity) ?? false;
    internal async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = request.CurrentMessage;
        if (source.Trust != MessageTrust.AuthenticatedUser || !settings.ChatGpt.PersonalizationEnabled)
            return Denied("当前消息身份或记忆设置不允许修改。");
        if (ledger != null)
        {
            var snapshot = await ledger.ReadConversationAsync(source.Identity, ct);
            var actual = Current(snapshot, source.SourceMessageId, source.Revision);
            if (actual == null || actual with { Stage = source.Stage, GenerationId = source.GenerationId } != source)
                return Denied("当前消息或绑定已经变化。");
        }
        var captured = memory.CaptureManual(source.Body);
        if (!Matches(captured.Resolution, request.Call))
            return Denied(captured.Resolution.Message.Length > 0 ? captured.Resolution.Message : "请明确要保存或修改的完整内容与目标。");
        return new(true, Target(captured.Resolution, captured.Stamp), source.Body);
    }
    internal async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        if (ledger == null || !Handles(call.Name) || context.OperationId is not { } operationId
            || operationId == Guid.Empty || context.InvocationId == Guid.Empty
            || context.InputWorkId != null || context.ReadAtRevision != null
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || !AssistantToolExecutor.Catalog().TryGet(call.Name, out var descriptor)
            || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _))
            return Rejected("durable_memory_operation_required");
        var snapshot = await ledger.ReadConversationAsync(context.Identity, ct);
        var source = Current(snapshot, context.SourceMessageId, context.Revision);
        var operation = snapshot.Operations.SingleOrDefault(o => o.Id == operationId);
        var invocation = snapshot.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        var grant = operation == null ? null : snapshot.Authorizations.SingleOrDefault(a => a.Id == operation.AuthorizationId);
        if (source == null || operation == null || invocation == null || grant == null
            || operation.Identity != context.Identity || operation.SourceMessageId != source.SourceMessageId
            || operation.Revision != context.Revision || operation.Stage != OperationStage.Dispatching
            || operation.Action != call.Name || operation.ArgumentsJson != canonical
            || operation.IdempotencyKey != context.IdempotencyKey || operation.Task != null
            || invocation.Identity != context.Identity || invocation.SourceMessageId != source.SourceMessageId
            || invocation.Revision != context.Revision || invocation.CallId != call.Id || invocation.ToolName != call.Name
            || invocation.ArgumentsJson != canonical || !invocation.HasSideEffects
            || invocation.Stage != ToolInvocationStage.Executing || invocation.OperationId != operation.Id
            || grant.Identity != context.Identity || grant.SourceMessageId != source.SourceMessageId
            || grant.Revision != context.Revision || grant.Action != call.Name || grant.ArgumentsJson != canonical
            || grant.SourceExcerpt != source.Body || grant.TargetKey != operation.TargetKey || grant.Task != null)
            return Rejected("durable_memory_execution_mismatch");
        var generation = snapshot.Generations.SingleOrDefault(g => g.Id == invocation.GenerationId);
        if (generation == null || generation.Identity != context.Identity || generation.Revision != context.Revision
            || generation.Stage != GenerationStage.Processing || generation.InputWorkId != null
            || !generation.InboxIds.Contains(source.Id) || !AssistantMemoryDependencies.MatchesExecutionContext(context, generation, invocation))
            return Rejected("memory_generation_not_current");
        ct.ThrowIfCancellationRequested();
        if (!settings.ChatGpt.PersonalizationEnabled || !CurrentBinding(context.Identity))
            return Rejected("memory_binding_changed");
        var captured = memory.CaptureManual(source.Body);
        if (!Matches(captured.Resolution, call) || Target(captured.Resolution, captured.Stamp) != operation.TargetKey)
            return Rejected("memory_target_changed");
        if (memory.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsHistoricalSourceRevoked(source.Identity, source.SourceMessageId, source.Revision))
            return Rejected("memory_source_revoked");
        AssistantMemoryMutation<AssistantMemoryEntry?> mutation;
        try { mutation = memory.ApplyManualWithContext(captured.Resolution, captured.Stamp,
            AssistantMemoryProvenance.FromCurrentMessage("wechat_manual", memory.Profile, source, context), context.GenerationContext?.Dependencies); }
        catch (InvalidOperationException error) when (error.Message is "manual_memory_target_changed" or "memory_generation_context_changed")
        { return Rejected("memory_target_changed"); }
        var entry = mutation.Value;
        object facts = call.Name switch
        {
            "remember" => new { memoryId = entry!.Id, text = entry.Text, scope = "personal", saved = true },
            "update_memory" => new { memoryId = entry!.Id, text = entry.Text, updated = true },
            _ => new { memoryId = captured.Resolution.MemoryId, deletedFromActiveMemory = true,
                historicalDialogueErased = false, note = "已从当前长期记忆删除；对话记录和独立备份不是同一份数据。" }
        };
        return new(ToolExecutionState.Completed, JsonSerializer.SerializeToElement(facts), TargetKey: operation.TargetKey,
            ContextTransition: AssistantMemoryDependencies.Transition(context, mutation));
    }
    private InboxMessage? Current(ConversationSnapshot snapshot, string source, long revision)
        => snapshot.IsReadOnly || snapshot.Revision != revision || !CurrentBinding(snapshot.Identity) ? null
            : snapshot.Inbox.SingleOrDefault(i => i.SourceMessageId == source && i.Revision == revision
                && i.Trust == MessageTrust.AuthenticatedUser);
    private static bool Matches(MemoryCommandResolution resolved, ModelToolCall call)
    {
        if (!resolved.IsReady || resolved.Action != call.Name
            || !AssistantToolExecutor.Catalog().TryGet(call.Name, out var descriptor)
            || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _)) return false;
        using var document = JsonDocument.Parse(canonical);
        var args = document.RootElement;
        return call.Name switch
        {
            "remember" => args.GetProperty("text").GetString() == resolved.Text && args.GetProperty("scope").GetString() == "personal",
            "forget" => string.Equals(args.GetProperty("memoryId").GetString(), resolved.MemoryId, StringComparison.OrdinalIgnoreCase),
            "update_memory" => string.Equals(args.GetProperty("memoryId").GetString(), resolved.MemoryId, StringComparison.OrdinalIgnoreCase)
                && args.GetProperty("text").GetString() == resolved.Text,
            _ => false
        };
    }
    private static string Target(MemoryCommandResolution resolved, string stamp)
        => "memory/manual/" + WeChatText.Hash(JsonSerializer.Serialize(new
        { resolved.SourceText, resolved.Action, resolved.MemoryId, resolved.Text, resolved.TargetEntry, stamp }));
    private static UserAuthorizationDecision Denied(string reason) => new(false, Reason: reason);
    private static ToolExecutionResult Rejected(string code)
        => new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code, changed = false }));
}
