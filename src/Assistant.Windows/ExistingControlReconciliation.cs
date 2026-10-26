using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed partial class ExistingControlRuntime
{
    internal async Task<string> ReconcileOperationAsync(InboxMessage query, Guid operationId, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await ledger.ReadConversationAsync(Identity, ct).ConfigureAwait(false);
            if (!CurrentQuery(saved, query, operationId)) return "当前查询或绑定已变化，没有连接远程服务。";
            var operation = saved.Operations.SingleOrDefault(o => o.Id == operationId);
            if (operation == null || !Handles(operation.Action)) return "当前绑定没有这条已有任务操作记录。";
            if (operation.Action != "interrupt_existing_turn") return "任务结束不能证明补充指令是否生效。这条纠偏操作保留原状态，不会重发。";
            if (operation.Stage is not (OperationStage.Accepted or OperationStage.Unknown)) return "这条操作不处于待核对状态，请查看“操作记录”。";
            using var args = JsonDocument.Parse(operation.ArgumentsJson);
            if (!profiles.TryGetValue(Text(args.RootElement, "configurationId"), out var profile)
                || !CanReconcile(profile))
                return "原控制范围未开放只读核对。请先核对可信配置，操作仍保留原状态。";
            var target = Target(profile, args.RootElement);
            if (operation.Task != Reference(target) || operation.TargetKey != target.Task.Key
                || !saved.Events.Any(item => MatchesDispatchScope(item, operation, profile)))
                return "当前配置与原操作的发送范围无法对应，没有连接或修改操作状态。";
            var wire = new ReconciliationConnection(await connect(profile, ct).ConfigureAwait(false));
            SharedExistingAppServerClient client;
            try { client = new(profile.Configuration, wire, clock, readOnly: true); }
            catch { await wire.DisposeAsync().ConfigureAwait(false); throw; }
            await using var ownedClient = client;
            await client.InitializeAsync(ct).ConfigureAwait(false);
            var read = await client.ReadTurnAsync(target, ct).ConfigureAwait(false);
            var current = await ledger.ReadConversationAsync(Identity, ct).ConfigureAwait(false);
            if (!CurrentQuery(current, query, operationId)) return "查询期间当前请求已变化，未更新操作记录。";
            var actual = current.Operations.Single(o => o.Id == operationId);
            if (actual.Stage is not (OperationStage.Accepted or OperationStage.Unknown)) return "操作已有较新的结果，请查看“操作记录”。";
            OperationStage? stage = read.Status switch
            {
                "interrupted" => OperationStage.Interrupted,
                "completed" or "failed" => OperationStage.Failed,
                _ => null
            };
            if (stage == null) return read.Status == null
                ? "原服务没有返回这条操作的准确轮次，保留原状态，不会重发。"
                : "原轮次仍在运行，尚未确认中断，保留原状态，不会重发。";
            await ledger.RecordOperationObservationAsync(Identity, operationId, new(stage.Value, JsonSerializer.Serialize(new
            {
                source = "shared_existing_read_reconciliation", operationId, configurationId = profile.Id,
                configurationDigest = profile.Configuration.ConfigurationDigest(), observedTask = target.Task,
                observedTurnId = target.TurnId, read.Status, read.ObservedAt, read.ConnectionEpoch, automaticRetry = false,
                independentlyVerified = false, causationVerified = false
            }), operation.TargetKey, operation.Task, Reconciliation: true), ct).ConfigureAwait(false);
            return stage == OperationStage.Interrupted
                ? "已只读核对：原任务的指定轮次确已中断，操作记录已更新。这次没有重发中断请求。"
                : "已只读核对：原轮次以其他状态结束，没有观察到中断结果，操作记录已更新。";
        }
        catch (Exception ex) when (ex is System.IO.IOException or ControlException or OperationCanceledException or JsonException)
        { return "这次无法完成只读核对，操作保留原状态，没有重发控制请求。"; }
        finally { gate.Release(); }
    }
    private bool CurrentQuery(ConversationSnapshot saved, InboxMessage query, Guid id)
        => Active && query.Identity == Identity && !saved.IsReadOnly && !query.HasUnresolvedReference
            && query.Trust == MessageTrust.AuthenticatedUser && saved.Revision == query.Revision
            && saved.Inbox.Any(m => m.SourceMessageId == query.SourceMessageId && m.Body == query.Body && m.Revision == query.Revision && !m.HasUnresolvedReference)
            && TryParseReconcile(query.Body, out var requested) && requested == id;
    internal static bool TryParseReconcile(string body, out Guid id)
    {
        string text = body.Trim(); id = default;
        return text.StartsWith("核对操作 ", StringComparison.Ordinal)
            && (Guid.TryParseExact(text[5..].Trim(), "N", out id) || Guid.TryParseExact(text[5..].Trim(), "D", out id));
    }
    private static bool MatchesDispatchScope(ConversationEvent item, OperationRecord operation, ConfiguredExistingProfile profile)
    {
        if (item.Source != ConversationSource.ToolResult || item.Task != operation.Task) return false;
        try
        {
            using var document = JsonDocument.Parse(item.Body); var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("source", out var source)) return false;
            bool dispatch = source.GetString() == "shared_existing_dispatch_scope";
            bool receipt = source.GetString() == "shared_existing_app_server" && item.RelatedOperationId == operation.Id;
            return (dispatch || receipt) && value.TryGetProperty("operationId", out var id) && id.TryGetGuid(out var parsed) && parsed == operation.Id
                && value.TryGetProperty("configurationId", out var config) && config.GetString() == profile.Id
                && value.TryGetProperty("configurationDigest", out var digest) && digest.GetString() == profile.Configuration.ConfigurationDigest();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
    private sealed class ReconciliationConnection(IJsonRpcConnection inner) : IJsonRpcConnection
    {
        public ConnectionIdentity Identity => inner.Identity;
        public bool IsConnected => inner.IsConnected;
        public event Action<RpcIncoming>? Incoming { add => inner.Incoming += value; remove => inner.Incoming -= value; }
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
            => method is "initialize" or "thread/read" ? inner.RequestAsync(method, parameters, ct)
                : throw new ControlException("read_only_reconciliation_connection", "read_only_reconciliation_connection");
        public Task NotifyAsync(string method, object parameters, CancellationToken ct)
            => method == "initialized" ? inner.NotifyAsync(method, parameters, ct)
                : throw new ControlException("read_only_reconciliation_connection", "read_only_reconciliation_connection");
        public Task RespondAsync(JsonElement id, object result, CancellationToken ct)
            => throw new ControlException("shared_approval_response_not_supported", "shared_approval_response_not_supported");
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
