using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Codex.Control;

/// <summary>
/// Steers or interrupts one exact existing turn on the original shared service. Never resumes, creates,
/// changes permissions, answers approvals, reconnects or replays. The host owns durable authorization
/// and the operation ledger; the required guard runs under the transport's write gate.
/// </summary>
public sealed class SharedExistingAppServerClient : IAsyncDisposable
{
    private sealed class Operation(Guid id, string method, ExistingTurnTarget target, string? text)
    {
        internal readonly Guid Id = id;
        internal readonly string Method = method;
        internal readonly ExistingTurnTarget Target = target;
        internal readonly string? Text = text;
        internal bool Writing;
        internal ControlState State = ControlState.Dispatching;
        internal string Code = "preparing_existing_turn";
        internal ControlReceipt Receipt => new(Id, State, Target.Task, Target.TurnId, Code);
    }
    private readonly object sync = new();
    private readonly SharedExistingConfiguration configuration;
    private readonly IJsonRpcConnection connection;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<Guid, Operation> operations = new();
    private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal);
    private readonly Channel<ControlObservation> observations = Channel.CreateBounded<ControlObservation>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Guid epoch = Guid.NewGuid();
    private long sequence;
    private bool initialized, disconnected, disposed, overflow;
    private readonly bool readOnly;

    public SharedExistingAppServerClient(SharedExistingConfiguration configuration, IJsonRpcConnection connection,
        Func<DateTimeOffset>? clock = null, bool readOnly = false)
    {
        configuration.Validate();
        if (configuration.Connection != connection.Identity) throw OwnedVerification.Error("connection_identity_mismatch");
        this.configuration = configuration; this.connection = connection; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.readOnly = readOnly;
        connection.Incoming += OnIncoming; connection.Disconnected += OnDisconnected;
    }

    public Guid ConnectionEpoch => epoch;
    public IAsyncEnumerable<ControlObservation> ReadObservationsAsync(CancellationToken cancellationToken = default)
        => observations.Reader.ReadAllAsync(cancellationToken);
    public ControlReceipt GetOperation(Guid operationId)
    { lock (sync) return operations.TryGetValue(operationId, out var op) ? op.Receipt : throw OwnedVerification.Error("operation_not_found"); }
    public string? CapabilityFailure(string method)
    {
        lock (sync)
        {
            if (disposed || disconnected || !initialized || !connection.IsConnected) return "not_initialized_or_disconnected";
            if (overflow) return "observation_consumer_overflow";
            if (readOnly) return "read_only_reconciliation_connection";
            if (!connection.SupportsGuardedRequests) return "guarded_write_not_supported";
            return configuration.CapabilityFailure(method, clock());
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || disconnected || initialized) throw OwnedVerification.Error("cannot_initialize_connection");
        }
        using var timeout = Timeout(cancellationToken);
        var result = await connection.RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_assistant_existing", title = "Codex Assistant", version = "1.0.0" },
            capabilities = new { experimentalApi = false }
        }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        if (!OwnedVerification.AgentVersionMatches(Text(result, "userAgent"), configuration.ExpectedVersion)
            || Text(result, "platformOs") != configuration.ExpectedPlatformOs || Text(result, "platformFamily") != "unix"
            || Text(result, "codexHome") != configuration.ExpectedCodexHome)
            throw OwnedVerification.Error("initialize_version_platform_or_profile_mismatch");
        await connection.NotifyAsync("initialized", new { }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        lock (sync)
        {
            if (disposed || disconnected || !connection.IsConnected) throw OwnedVerification.Error("stale_initialize_response");
            initialized = true;
        }
    }

    public Task<ControlReceipt> SteerAsync(Guid operationId, ExistingTurnTarget target, string text,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken = default)
    {
        OwnedVerification.RequireText(text, 4000);
        return SendAsync(operationId, "turn/steer", target, text, beforeWrite, cancellationToken);
    }
    public Task<ControlReceipt> InterruptAsync(Guid operationId, ExistingTurnTarget target,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken = default)
        => SendAsync(operationId, "turn/interrupt", target, null, beforeWrite, cancellationToken);

    private async Task<ControlReceipt> SendAsync(Guid operationId, string method, ExistingTurnTarget target, string? text,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeWrite);
        Operation operation;
        lock (sync)
        {
            if (operationId == Guid.Empty) throw OwnedVerification.Error("operation_id_required");
            if (operations.TryGetValue(operationId, out var prior))
            {
                if (prior.Method != method || prior.Target != target || prior.Text != text)
                    throw OwnedVerification.Error("operation_identity_conflict");
                return prior.Receipt;
            }
            ValidateTarget(target);
            if (CapabilityFailure(method) is { } failure) throw OwnedVerification.Error(failure);
            operation = new(operationId, method, target, text); operations.Add(operationId, operation);
        }
        using var timeout = Timeout(cancellationToken);
        try
        {
            long revision;
            lock (sync) revision = revisions.GetValueOrDefault(target.Task.ThreadId);
            var thread = await ReadAsync(target, timeout.Token).ConfigureAwait(false);
            var turns = thread.GetProperty("turns");
            if (turns.GetArrayLength() == 0 || Text(turns[turns.GetArrayLength() - 1], "id") != target.TurnId
                || Text(turns[turns.GetArrayLength() - 1], "status") != "inProgress"
                || !thread.TryGetProperty("status", out var status) || Text(status, "type") != "active")
                throw OwnedVerification.Error("expected_active_turn_mismatch");
            object parameters = method == "turn/steer"
                ? new { threadId = target.Task.ThreadId, expectedTurnId = target.TurnId, input = new[] { new { type = "text", text } } }
                : new { threadId = target.Task.ThreadId, turnId = target.TurnId };
            var result = await connection.RequestGuardedAsync(method, parameters, async ct =>
            {
                lock (sync)
                    if (CapabilityFailure(method) != null || revisions.GetValueOrDefault(target.Task.ThreadId) != revision) return false;
                if (!await beforeWrite(ct).ConfigureAwait(false)) return false;
                lock (sync)
                {
                    ct.ThrowIfCancellationRequested();
                    if (CapabilityFailure(method) != null || revisions.GetValueOrDefault(target.Task.ThreadId) != revision) return false;
                    operation.Writing = true;
                    return true;
                }
            }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (operation.State is ControlState.Interrupted or ControlState.Failed) return operation.Receipt;
                if (result.ValueKind != JsonValueKind.Object || (method == "turn/steer" && Text(result, "turnId") != target.TurnId))
                    Set(operation, ControlState.Unknown, "incompatible_write_receipt");
                else Set(operation, ControlState.Accepted, method == "turn/steer" ? "steer_accepted_effect_not_verified" : "interrupt_accepted_awaiting_observation");
                return operation.Receipt;
            }
        }
        catch (Exception ex) when (ex is ControlException or IOException or OperationCanceledException or TimeoutException
            or JsonException or InvalidOperationException)
        {
            lock (sync)
            {
                if (operation.State is ControlState.Interrupted or ControlState.Failed) return operation.Receipt;
                bool uncertain = operation.Writing && (ex is not RpcTransportException transport || transport.MayHaveBeenWritten);
                if (ex is RpcRemoteException) Set(operation, ControlState.Failed, "server_rejected_request");
                else if (uncertain) Set(operation, ControlState.Unknown, "write_outcome_unknown_no_replay");
                else Set(operation, ex is OperationCanceledException || ex is RpcTransportException { Code: "request_guard_rejected" or "request_cancelled" }
                    ? ControlState.CancelledBeforeDispatch : ControlState.Failed,
                    ex is ControlException control ? control.Code : "not_dispatched");
                return operation.Receipt;
            }
        }
    }

    /// <summary>Read-only reconciliation for an interrupt. A steer acknowledgement never proves its effect.</summary>
    public async Task<ControlReceipt> ReconcileAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (sync)
        {
            operation = operations.GetValueOrDefault(operationId) ?? throw OwnedVerification.Error("operation_not_found");
            if (operation.Method != "turn/interrupt" || !operation.Writing
                || operation.State is not (ControlState.Accepted or ControlState.Unknown)) return operation.Receipt;
        }
        using var timeout = Timeout(cancellationToken);
        var thread = await ReadAsync(operation.Target, timeout.Token).ConfigureAwait(false);
        lock (sync)
        {
            foreach (var turn in thread.GetProperty("turns").EnumerateArray())
                if (Text(turn, "id") == operation.Target.TurnId) ObserveEnd(operation, Text(turn, "status"), true);
            return operation.Receipt;
        }
    }

    public async Task<ExistingTurnRead> ReadTurnAsync(ExistingTurnTarget target, CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        using var timeout = Timeout(cancellationToken);
        var thread = await ReadAsync(target, timeout.Token).ConfigureAwait(false);
        var turns = thread.GetProperty("turns").EnumerateArray().Where(turn => Text(turn, "id") == target.TurnId).ToArray();
        if (turns.Length > 1) throw OwnedVerification.Error("ambiguous_turn_read");
        string? status = turns.Length == 0 ? null : Text(turns[0], "status");
        if (status is not (null or "inProgress" or "completed" or "failed" or "interrupted"))
            throw OwnedVerification.Error("unsupported_turn_status");
        lock (sync)
        {
            if (disposed || disconnected || !connection.IsConnected) throw OwnedVerification.Error("stale_turn_read");
            return new(target, status, clock(), epoch);
        }
    }

    private async Task<JsonElement> ReadAsync(ExistingTurnTarget target, CancellationToken cancellationToken)
    {
        lock (sync)
            if (disposed || disconnected || !initialized || !connection.IsConnected) throw OwnedVerification.Error("not_initialized_or_disconnected");
        var result = await connection.RequestAsync("thread/read", new { threadId = target.Task.ThreadId, includeTurns = true },
            cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("thread", out var thread) || Text(thread, "id") != target.Task.ThreadId
            || Text(thread, "cwd") != target.Cwd || !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
            throw OwnedVerification.Error("read_thread_identity_or_directory_mismatch");
        return thread;
    }
    private void ValidateTarget(ExistingTurnTarget target)
    {
        if (target.Task.Endpoint != configuration.Connection.Endpoint || !configuration.Directories.Contains(target.Cwd))
            throw OwnedVerification.Error("target_outside_configured_scope");
        OwnedVerification.RequireText(target.TurnId, 512);
    }
    private void OnIncoming(RpcIncoming incoming)
    {
        // Shared approvals belong to the existing clients; they are never response authority here.
        if (incoming.RequestId != null || incoming.Method is not ("turn/started" or "turn/completed" or "thread/status/changed")) return;
        lock (sync)
        {
            if (disposed || disconnected) return;
            string thread = Text(incoming.Parameters, "threadId");
            if (thread.Length == 0 || !operations.Values.Any(op => op.Target.Task.ThreadId == thread)) return;
            revisions[thread] = revisions.GetValueOrDefault(thread) + 1;
            if (incoming.Method != "turn/completed" || !incoming.Parameters.TryGetProperty("turn", out var turn)) return;
            foreach (var operation in operations.Values)
                if (operation.Target.Task.ThreadId == thread && operation.Target.TurnId == Text(turn, "id"))
                    ObserveEnd(operation, Text(turn, "status"), false);
        }
    }
    private void ObserveEnd(Operation operation, string status, bool reconciliation)
    {
        if (operation.Method != "turn/interrupt" || !operation.Writing
            || operation.State is not (ControlState.Dispatching or ControlState.Accepted or ControlState.Unknown)) return;
        if (status == "interrupted") Set(operation, ControlState.Interrupted, "target_turn_observed_interrupted", reconciliation);
        else if (status is "completed" or "failed") Set(operation, ControlState.Failed, "target_turn_ended_without_interruption", reconciliation);
    }
    private void OnDisconnected()
    {
        lock (sync)
        {
            disconnected = true;
            foreach (var operation in operations.Values)
                if (operation.Writing && (operation.State == ControlState.Dispatching
                    || operation.Method == "turn/interrupt" && operation.State == ControlState.Accepted))
                    Set(operation, ControlState.Unknown, "connection_lost_no_replay");
        }
    }
    private void Set(Operation operation, ControlState state, string code, bool reconciliation = false)
    {
        operation.State = state; operation.Code = code;
        if (!observations.Writer.TryWrite(new(++sequence, clock(), operation.Id, configuration.Connection.Endpoint,
            operation.Target.Task, operation.Target.TurnId, operation.Method, state, code, reconciliation, epoch))) overflow = true;
    }
    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); result.CancelAfter(configuration.RequestTimeout); return result;
    }
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            OnDisconnected(); disposed = true;
            connection.Incoming -= OnIncoming; connection.Disconnected -= OnDisconnected;
            observations.Writer.TryComplete();
        }
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
