using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed record ExistingHostOptions(IReadOnlyList<ConfiguredExistingProfile> Profiles,
    Func<ConfiguredExistingProfile, CancellationToken, Task<IJsonRpcConnection>>? ConnectionFactory = null);
internal sealed record ExistingRuntimeEvent(string ConfigurationId, ControlObservation Observation);

/// <summary>One binding's existing-task controls. Durable operations are registered before connecting or writing.</summary>
internal sealed partial class ExistingControlRuntime : IToolExecutor, IUserAuthorizationPolicy, IAsyncDisposable
{
    private sealed class Session(ConfiguredExistingProfile profile, SharedExistingAppServerClient client)
    {
        internal readonly ConfiguredExistingProfile Profile = profile;
        internal readonly SharedExistingAppServerClient Client = client;
        internal Task Pump = Task.CompletedTask;
        internal readonly SemaphoreSlim Persistence = new(1, 1);
    }
    private readonly DurableConversationLedger ledger;
    private readonly Func<ConversationIdentity?> activeIdentity;
    private readonly Func<bool> enabled;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<TaskReference, GatewayTask?>? readFocusedTask;
    private readonly Func<ConfiguredExistingProfile, CancellationToken, Task<IJsonRpcConnection>> connect;
    private readonly ImmutableDictionary<string, ConfiguredExistingProfile> profiles;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, OperationRecord> dispatched = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<ExistingRuntimeEvent> events = Channel.CreateBounded<ExistingRuntimeEvent>(256);
    private int stopping, faulted;
    internal ConversationIdentity Identity { get; }
    internal bool Active => Volatile.Read(ref stopping) == 0 && activeIdentity() == Identity;
    internal bool CanReconcile(ConfiguredExistingProfile profile) => Active && (profile.ReadOnly
        || profile.Enabled && profile.Configuration.CapabilityFailure("turn/interrupt", clock()) == null);
    internal string? Failure => Volatile.Read(ref faulted) != 0 ? "existing_control_observation_failed" : null;
    internal IReadOnlyList<ConfiguredExistingProfile> Profiles => profiles.Values.OrderBy(p => p.Id).ToArray();
    private static readonly ToolCatalog Catalog = new(Descriptors());
    internal ExistingControlRuntime(ConversationIdentity identity, Func<ConversationIdentity?> activeIdentity,
        DurableConversationLedger ledger, ExistingHostOptions options, Func<bool> enabled, Func<DateTimeOffset> clock,
        Func<TaskReference, GatewayTask?>? readFocusedTask = null)
    {
        Identity = identity; this.activeIdentity = activeIdentity; this.ledger = ledger; this.enabled = enabled; this.clock = clock;
        this.readFocusedTask = readFocusedTask;
        profiles = options.Profiles.ToImmutableDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var profile in profiles.Values)
        {
            OwnedControlConfiguration.Identifier(profile.Id); profile.Configuration.Validate();
            if (profile.Configuration.Directories.Count != 1) throw new ArgumentException("one_directory_per_existing_profile");
        }
        connect = options.ConnectionFactory ?? (async (profile, ct) => await SharedRemoteControlConnection.ConnectAsync(profile.Configuration.Connection, ct));
    }
    internal static IReadOnlyList<ToolDescriptor> Descriptors()
    {
        ToolSchema Target(params (string, ToolSchema, bool)[] extra) => ToolSchema.Object(
            [("configurationId", ToolSchema.String(64), true), ("threadId", ToolSchema.String(512), true), ("turnId", ToolSchema.String(512), true), .. extra]);
        return [new("list_existing_controls", "列出已有远程任务的可信控制范围与能力，不连接服务。不包含新建任务配置；创建任务应先用list_owned_tasks查询configurations。", ToolSchema.Object(), false, "existing.list_existing_controls"),
            new("steer_existing_turn", "按本条明确命令补充已有远程任务的精确轮次；受理不证明生效。", Target(("text", ToolSchema.String(4000), true)), true, "existing.steer_existing_turn"),
            new("interrupt_existing_turn", "按本条明确命令中断已有远程任务的精确轮次；受理不等于已中断。", Target(), true, "existing.interrupt_existing_turn")];
    }
    internal static bool Handles(string tool) => Catalog.TryGet(tool, out _);
    internal static string Method(string tool) => tool == "steer_existing_turn" ? "turn/steer" : "turn/interrupt";
    internal string? WriteFailure(ConfiguredExistingProfile profile, string method) => !Active ? "binding_changed"
        : !enabled() ? "existing_control_disabled" : Failure ?? (!profile.Enabled ? "profile_disabled"
        : profile.Configuration.CapabilityFailure(method, clock())
            ?? (sessions.TryGetValue(profile.Id, out var session) ? session.Client.CapabilityFailure(method) : null));
    internal GatewayCapability Capability(CanonicalTaskIdentity task, string cwd, CodexCapability capability)
    {
        string method = capability == CodexCapability.Steer ? "turn/steer" : "turn/interrupt";
        var matches = profiles.Values.Where(p => p.Configuration.Connection.Endpoint == task.Endpoint && p.Configuration.Directories.Contains(cwd)).ToArray();
        bool available = matches.Length == 1 && WriteFailure(matches[0], method) == null;
        return new(capability, available, available ? "已有控制范围可用；需明确配置、任务、轮次，发送前再核对原服务。"
            : "没有唯一可用的已有控制范围；发“已有控制”核对。", true);
    }
    internal string TargetCommands(GatewayTask task)
    {
        if (CodexGateway.Backend(task.Identity) != CodexBackendKind.SharedRemoteExisting) return "";
        var matches = profiles.Values.Where(p => p.Configuration.Connection.Endpoint == task.Identity.Endpoint
            && p.Configuration.Directories.Contains(task.Record.Cwd)).ToArray();
        if (matches.Length != 1) return "\n没有唯一对应的已有控制配置，请用“已有控制”核对主机、配置和目录。";
        if (!task.Fresh) return "\n任务记录已过期，暂不生成控制命令。";
        if (string.IsNullOrWhiteSpace(task.Record.TurnId) || task.Record.Status is not (TaskStatusNames.Active or TaskStatusNames.Waiting))
            return "\n尚未读到正在运行的确切轮次，暂不生成控制命令。";
        var profile = matches[0];
        string target = profile.Id + "/" + task.Identity.ThreadId + "/" + task.Record.TurnId;
        string interrupt = "中断已有 " + target;
        string steer = "纠偏已有 " + target + "：";
        if (ParseCommand(interrupt) == null) return "\n任务编号无法用于当前命令格式，暂不生成控制命令。";
        var lines = new List<string>();
        if (WriteFailure(profile, "turn/interrupt") == null) lines.Add(interrupt);
        if (WriteFailure(profile, "turn/steer") == null) lines.Add(steer + "原样指令");
        return lines.Count == 0 ? "\n这项任务的纠偏和中断尚未开放。"
            : "\n如需操作，请复制下面的命令；纠偏时替换冒号后的指令：\n" + string.Join("\n", lines)
                + "\n这里只提供当前记录中的轮次，尚未执行操作；发送时会再核对原服务。";
    }
    public ToolCapability GetCapability(string capability)
    {
        string tool = capability.StartsWith("existing.", StringComparison.Ordinal) ? capability[9..] : capability;
        if (!Handles(tool) || !Active) return new(false, "已有任务控制当前不可用。");
        if (tool == "list_existing_controls") return new(true);
        return new(profiles.Values.Any(p => WriteFailure(p, Method(tool)) == null), "已有任务控制未启用或缺少对应运行验收。");
    }
    internal static ModelToolCall? ParseCommand(string body)
    {
        var match = Regex.Match(body.Trim(), @"\A(?<action>纠偏已有|中断已有)\s+(?<config>[A-Za-z0-9_-]{1,64})/(?<thread>[^\s/:：]+)/(?<turn>[^\s/:：]+)(?:(?:：|:)\s*(?<text>[\s\S]+))?\z", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        bool steer = match.Groups["action"].Value == "纠偏已有";
        if (steer != match.Groups["text"].Success) return null;
        object args = steer
            ? new { configurationId = match.Groups["config"].Value, threadId = match.Groups["thread"].Value, turnId = match.Groups["turn"].Value, text = match.Groups["text"].Value }
            : new { configurationId = match.Groups["config"].Value, threadId = match.Groups["thread"].Value, turnId = match.Groups["turn"].Value };
        return new("existing-explicit", steer ? "steer_existing_turn" : "interrupt_existing_turn", JsonSerializer.Serialize(args));
    }
    public async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
    {
        if (!Catalog.TryGet(request.Call.Name, out var descriptor) || !descriptor.HasSideEffects
            || !descriptor.Parameters.TryValidate(request.Call.ArgumentsJson, out string canonical, out _)) return Deny();
        var saved = await ledger.ReadConversationAsync(Identity, ct).ConfigureAwait(false);
        var message = saved.Inbox.SingleOrDefault(m => m.SourceMessageId == request.CurrentMessage.SourceMessageId);
        if (!Active || saved.IsReadOnly || request.CurrentMessage.Identity != Identity || message == null
            || message != request.CurrentMessage || message.Revision != saved.Revision || message.HasUnresolvedReference
            || message.Trust != MessageTrust.AuthenticatedUser) return Deny();
        var explicitCall = ParseCommand(message.Body) ?? FocusedCommand(message.Body, saved.Context?.Focus);
        if (explicitCall?.Name != request.Call.Name
            || !descriptor.Parameters.TryValidate(explicitCall.ArgumentsJson, out string expected, out _) || expected != canonical) return Deny();
        using var document = JsonDocument.Parse(canonical); var args = document.RootElement;
        if (!profiles.TryGetValue(Text(args, "configurationId"), out var profile) || WriteFailure(profile, Method(request.Call.Name)) != null)
            return Deny("该已有任务控制范围尚未启用或缺少运行验收。");
        var target = Target(profile, args);
        return new(true, target.Task.Key, message.Body, Reference(target));
    }
    private static UserAuthorizationDecision Deny(string reason = "请明确写出配置、任务和轮次编号；引用、旧消息和模型推测不能授权。") => new(false, Reason: reason);
    private async Task<OperationRecord?> VerifyAsync(ToolExecutionContext context, ModelToolCall call, ToolDescriptor descriptor, CancellationToken ct)
    {
        var saved = await ledger.ReadConversationAsync(Identity, ct).ConfigureAwait(false);
        var message = saved.Inbox.SingleOrDefault(m => m.SourceMessageId == context.SourceMessageId);
        var operation = saved.Operations.SingleOrDefault(o => o.Id == context.OperationId);
        var invocation = saved.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        if (!Active || context.Identity != Identity || context.InputWorkId != null || saved.IsReadOnly || message == null
            || operation == null || invocation == null || saved.Revision != context.Revision
            || operation.Stage != OperationStage.Dispatching || operation.Identity != context.Identity
            || operation.SourceMessageId != context.SourceMessageId || operation.Revision != context.Revision
            || operation.Action != call.Name || operation.ArgumentsJson != call.ArgumentsJson || operation.IdempotencyKey != context.IdempotencyKey
            || invocation.Stage != ToolInvocationStage.Executing || invocation.OperationId != operation.Id
            || invocation.CallId != call.Id || invocation.ToolName != call.Name || invocation.ArgumentsJson != call.ArgumentsJson
            || invocation.SourceMessageId != context.SourceMessageId || invocation.Revision != context.Revision) return null;
        var authorization = saved.Authorizations.SingleOrDefault(a => a.Id == operation.AuthorizationId);
        if (authorization == null || authorization.Identity != operation.Identity || authorization.SourceMessageId != operation.SourceMessageId
            || authorization.Action != operation.Action || authorization.ArgumentsJson != operation.ArgumentsJson
            || authorization.TargetKey != operation.TargetKey || authorization.Task != operation.Task
            || !message.Body.Contains(authorization.SourceExcerpt, StringComparison.Ordinal)) return null;
        var decision = await AuthorizeAsync(new(message, call, descriptor), ct).ConfigureAwait(false);
        return decision.Allowed && decision.TargetKey == operation.TargetKey && decision.Task == operation.Task ? operation : null;
    }
    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        if (!Active || context.Identity != Identity || !Catalog.TryGet(call.Name, out var descriptor)
            || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _)) return Reject("invalid_existing_control_call");
        if (!descriptor.HasSideEffects)
        {
            if (context.OperationId != null || context.IdempotencyKey != null) return Reject("read_only_context_required");
            return new(ToolExecutionState.ReadOnlySuccess, JsonSerializer.SerializeToElement(Profiles.Select(p => new
            { p.Id, p.Label, hostId = p.Configuration.Connection.Endpoint.HostId, profileId = p.Configuration.Connection.Endpoint.ProfileId,
                cwd = p.Configuration.Directories.Single(), steer = WriteFailure(p, "turn/steer"), interrupt = WriteFailure(p, "turn/interrupt"),
                readReconciliation = CanReconcile(p), approval = "shared_approval_response_not_supported", connectionNotStartedByQuery = true })));
        }
        call = call with { ArgumentsJson = canonical };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operation = await VerifyAsync(context, call, descriptor, ct).ConfigureAwait(false);
            if (operation == null || !dispatched.TryAdd(operation.Id, operation)) return Reject("durable_current_authorization_required");
            using var document = JsonDocument.Parse(canonical); var args = document.RootElement;
            var profile = profiles[Text(args, "configurationId")]; var target = Target(profile, args);
            bool invoked = false;
            try
            {
                if (!sessions.TryGetValue(profile.Id, out var session))
                {
                    var wire = await connect(profile, ct).ConfigureAwait(false);
                    SharedExistingAppServerClient client;
                    try
                    {
                        client = new(profile.Configuration, wire, clock);
                        await client.InitializeAsync(ct).ConfigureAwait(false);
                    }
                    catch { await wire.DisposeAsync().ConfigureAwait(false); throw; }
                    session = new(profile, client); sessions[profile.Id] = session;
                    session.Pump = ObserveAsync(session);
                }
                if (await VerifyAsync(context, call, descriptor, ct).ConfigureAwait(false) == null) return Reject("authorization_changed_before_control");
                async Task<bool> Guard(CancellationToken token)
                {
                    if (!await GuardAsync(context, call, descriptor, token).ConfigureAwait(false)) return false;
                    await ledger.AppendContextAsync(Identity, ConversationSource.ToolResult, JsonSerializer.Serialize(new
                    {
                        source = "shared_existing_dispatch_scope", operationId = operation.Id, configurationId = profile.Id,
                        configurationDigest = profile.Configuration.ConfigurationDigest(), task = operation.Task,
                        cwd = target.Cwd, connectionEpoch = session.Client.ConnectionEpoch
                    }), clock(), operation.Task, cancellationToken: token).ConfigureAwait(false);
                    return await GuardAsync(context, call, descriptor, token).ConfigureAwait(false);
                }
                invoked = true;
                var receipt = call.Name == "steer_existing_turn"
                    ? await session.Client.SteerAsync(operation.Id, target, Text(args, "text"), Guard, ct).ConfigureAwait(false)
                    : await session.Client.InterruptAsync(operation.Id, target, Guard, ct).ConfigureAwait(false);
                var observation = new ControlObservation(0, clock(), operation.Id, target.Task.Endpoint, target.Task,
                    target.TurnId, Method(call.Name), receipt.State, receipt.Code, ConnectionEpoch: session.Client.ConnectionEpoch);
                await PersistAsync(session, observation).ConfigureAwait(false);
                return new(Map(receipt.State), Facts(session, observation), TargetKey: operation.TargetKey, Task: operation.Task);
            }
            catch (Exception) { return Reject(invoked ? "existing_control_outcome_unknown" : "existing_control_not_dispatched", invoked); }
        }
        finally { gate.Release(); }
    }
    private async Task<bool> GuardAsync(ToolExecutionContext context, ModelToolCall call, ToolDescriptor descriptor, CancellationToken ct)
        => await VerifyAsync(context, call, descriptor, ct).ConfigureAwait(false) != null;
    private async Task PersistAsync(Session session, ControlObservation observation)
    {
        await session.Persistence.WaitAsync().ConfigureAwait(false);
        try
        {
        if (observation.OperationId is not { } id || !dispatched.TryGetValue(id, out var operation)
            || observation.Task == null || operation.Task != new TaskReference(observation.Task.HostId, observation.Task.ThreadId, observation.TurnId, observation.Task.ProfileId)) return;
        var current = (await ledger.ReadConversationAsync(Identity).ConfigureAwait(false)).Operations.Single(o => o.Id == id);
        bool reconciliation = observation.Code is "target_turn_observed_interrupted" or "target_turn_ended_without_interruption";
        if (current.Stage == OperationStage.Unknown && !reconciliation && observation.State != ControlState.Unknown) return;
        await ledger.RecordOperationObservationAsync(Identity, id, new(Stage(observation.State), Facts(session, observation).GetRawText(),
            operation.TargetKey, operation.Task, reconciliation)).ConfigureAwait(false);
        }
        finally { session.Persistence.Release(); }
    }
    private async Task ObserveAsync(Session session)
    {
        try
        {
            await foreach (var observation in session.Client.ReadObservationsAsync().ConfigureAwait(false))
            {
                await PersistAsync(session, observation).ConfigureAwait(false);
                if (!events.Writer.TryWrite(new(session.Profile.Id, observation))) throw new InvalidOperationException("existing_events_full");
            }
        }
        catch (Exception) { Interlocked.Exchange(ref faulted, 1); }
    }
    internal IAsyncEnumerable<ExistingRuntimeEvent> ReadEventsAsync(CancellationToken ct) => events.Reader.ReadAllAsync(ct);
    private static JsonElement Facts(Session session, ControlObservation observation) => JsonSerializer.SerializeToElement(new
    {
        source = "shared_existing_app_server", configurationId = session.Profile.Id,
        configurationDigest = session.Profile.Configuration.ConfigurationDigest(), operationId = observation.OperationId,
        observedTask = observation.Task, observedTurnId = observation.TurnId, phase = observation.State.ToString(),
        observation.Code, observation.Method, observation.Sequence, observation.ConnectionEpoch, observation.ObservedAt,
        automaticRetry = false, independentlyVerified = false
    });
    private static ExistingTurnTarget Target(ConfiguredExistingProfile profile, JsonElement args) => new(
        new(profile.Configuration.Connection.Endpoint.HostId, Text(args, "threadId"), profile.Configuration.Connection.Endpoint.ProfileId),
        profile.Configuration.Directories.Single(), Text(args, "turnId"));
    private static TaskReference Reference(ExistingTurnTarget target) => new(target.Task.HostId, target.Task.ThreadId, target.TurnId, target.Task.ProfileId);
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static ToolExecutionResult Reject(string code, bool unknown = false) => new(unknown ? ToolExecutionState.Unknown : ToolExecutionState.Rejected,
        JsonSerializer.SerializeToElement(new { code, automaticRetry = false, dispatched = unknown ? (bool?)null : false }));
    private static OperationStage Stage(ControlState state) => state switch
    { ControlState.Accepted => OperationStage.Accepted, ControlState.Interrupted => OperationStage.Interrupted,
        ControlState.Failed or ControlState.CancelledBeforeDispatch => OperationStage.Failed, _ => OperationStage.Unknown };
    private static ToolExecutionState Map(ControlState state) => state switch
    { ControlState.Accepted or ControlState.Interrupted => ToolExecutionState.Accepted,
        ControlState.Failed or ControlState.CancelledBeforeDispatch => ToolExecutionState.Rejected, _ => ToolExecutionState.Unknown };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        lifetime.Cancel(); await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in sessions.Values) await session.Client.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(sessions.Values.Select(s => s.Pump)).ConfigureAwait(false);
            events.Writer.TryComplete();
        }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}
