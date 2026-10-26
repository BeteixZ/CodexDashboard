using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodexDashboard;

namespace Codex.Control;

/// <summary>
/// Controls durably owned handles; restored handles require explicit, individual recovery.
/// The caller owns durable authorization and the
/// operation ledger. This class retains bounded wire correlations, not a second business ledger.
/// </summary>
public sealed class OwnedAppServerClient : IAsyncDisposable
{
    private sealed class Operation(Guid id, string method, OwnedTaskHandle? task, string? turn,
        Guid connectionEpoch, IJsonRpcConnection wire)
    {
        internal readonly Guid Id = id;
        internal readonly string Method = method;
        internal OwnedTaskHandle? Task = task;
        internal string? Turn = turn;
        internal ControlState State = ControlState.Dispatching;
        internal readonly Guid ConnectionEpoch = connectionEpoch;
        internal readonly IJsonRpcConnection Wire = wire;
    }
    private readonly object sync = new();
    private readonly OwnedServerConfiguration configuration;
    private readonly Func<DateTimeOffset> clock;
    private readonly OwnedProvenanceStore? provenance;
    private readonly Dictionary<string, OwnedTaskHandle> tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControlObservation> turnObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> commandReadSequences = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Operation> operations = new();
    private readonly Dictionary<string, PendingApproval> approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> approvalOperations = new(StringComparer.Ordinal);
    private readonly HashSet<string> starting = new(StringComparer.Ordinal);
    private readonly HashSet<string> metadataPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, OwnedTaskState>> seenTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Task, string Turn, string Item), JsonElement> approvalItems = new();
    private readonly Channel<ControlObservation> observations = Channel.CreateBounded<ControlObservation>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private IJsonRpcConnection connection;
    private Guid epoch = Guid.NewGuid();
    private bool initialized, reconnectReadOnly, restoredReadOnly, disposed, observationOverflow;
    private long sequence, focusRevision;
    private CanonicalTaskIdentity? focus;
    private Action<RpcIncoming>? incomingHandler;
    private Action? disconnectedHandler;

    public OwnedAppServerClient(OwnedServerConfiguration configuration, IJsonRpcConnection connection,
        Func<DateTimeOffset>? clock = null, OwnedProvenanceStore? provenance = null)
    {
        OwnedVerification.Validate(configuration);
        if (configuration.Connection != connection.Identity) throw OwnedVerification.Error("connection_identity_mismatch");
        if (provenance != null)
        {
            if (!provenance.Matches(configuration)) throw OwnedVerification.Error("provenance_configuration_mismatch");
            provenance.Attach();
        }
        this.provenance = provenance;
        this.configuration = configuration; this.connection = connection; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Subscribe(connection);
    }

    public Guid ConnectionEpoch { get { lock (sync) return epoch; } }
    public bool IsConnected { get { lock (sync) return !disposed && initialized && connection.IsConnected; } }
    public string? ProjectSourceIdentity { get { lock (sync) return connection.ProjectSourceIdentity; } }

    public ControlObservation? LatestTurnObservation(OwnedTaskHandle task)
    {
        lock (sync)
        {
            RequireOwned(task);
            var value = turnObservations.GetValueOrDefault(task.Identity.Key);
            return value?.ConnectionEpoch == epoch ? value : null;
        }
    }

    public IAsyncEnumerable<ControlObservation> ReadObservationsAsync(CancellationToken cancellationToken = default)
        => observations.Reader.ReadAllAsync(cancellationToken);

    public bool IsCommandReadCurrent(OwnedTaskHandle task, long readSequence)
    {
        lock (sync)
        {
            RequireOwned(task);
            var latest = turnObservations.GetValueOrDefault(task.Identity.Key);
            return latest?.ConnectionEpoch == epoch && latest.Sequence == readSequence
                && commandReadSequences.GetValueOrDefault(task.Identity.Key) == readSequence;
        }
    }

    public string? CapabilityFailure(string method)
    {
        lock (sync)
        {
            if (disposed || !initialized || !connection.IsConnected) return "not_initialized_or_disconnected";
            if (observationOverflow) return "observation_consumer_overflow";
            if (reconnectReadOnly && method is not ("thread/resume" or "thread/unarchive")
                && !tasks.Values.Any(t => t.Access == OwnedTaskAccess.ResumedThisSession))
                return "reconnected_read_only_reconciliation";
            if (restoredReadOnly && method is not ("thread/resume" or "thread/start" or "thread/unarchive")
                && !tasks.Values.Any(t => t.Access is OwnedTaskAccess.CreatedThisSession or OwnedTaskAccess.ResumedThisSession))
                return "restored_provenance_read_only";
            string? failure = OwnedVerification.CapabilityFailure(configuration, method, clock());
            if (failure != null) return failure;
            if (method == "thread/start" && provenance == null) return "provenance_store_required";
            if (provenance?.Faulted == true) return "provenance_recovery_required";
            return null;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IJsonRpcConnection wire;
        Guid requestEpoch;
        lock (sync)
        {
            ThrowDisposed(); if (initialized) throw OwnedVerification.Error("already_initialized");
            wire = connection; requestEpoch = epoch;
        }
        using var timeout = Timeout(cancellationToken);
        var result = await wire.RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_assistant_owned", title = "Codex Assistant", version = "1.0.0" },
            capabilities = new { experimentalApi = false }
        }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        string agent = Text(result, "userAgent");
        if (!OwnedVerification.AgentVersionMatches(agent, configuration.ExpectedVersion)
            || Text(result, "platformOs") != configuration.ExpectedPlatformOs
            || Text(result, "platformFamily") != (configuration.ExpectedPlatformOs == "windows" ? "windows" : "unix")
            || !OwnedVerification.SamePath(Text(result, "codexHome"), configuration.ExpectedCodexHome, configuration.ExpectedPlatformOs))
            throw OwnedVerification.Error("initialize_version_platform_or_profile_mismatch");
        lock (sync) { if (requestEpoch != epoch) throw OwnedVerification.Error("stale_initialize_response"); }
        await wire.NotifyAsync("initialized", new { }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        lock (sync)
        {
            ThrowDisposed(); if (requestEpoch != epoch || !wire.IsConnected) throw OwnedVerification.Error("stale_initialize_response");
            initialized = true;
        }
    }

    /// <summary>Reconnect never resumes, replays, creates, or enables writes. Read reconciliation is explicit.</summary>
    public async Task ReconnectForReadAsync(IJsonRpcConnection replacement, CancellationToken cancellationToken = default)
    {
        if (replacement.Identity != configuration.Connection) throw OwnedVerification.Error("connection_identity_mismatch");
        IJsonRpcConnection previous;
        lock (sync)
        {
            ThrowDisposed(); previous = connection;
            MarkDisconnected();
            foreach (var task in tasks.Values)
            {
                task.Access = OwnedTaskAccess.RestoredReadOnly;
                Update(task, task.Snapshot.CurrentTurnId, OwnedTaskState.Unknown);
            }
            Unsubscribe(previous);
            connection = replacement; epoch = Guid.NewGuid(); initialized = false; reconnectReadOnly = true;
            Subscribe(replacement);
        }
        await previous.DisposeAsync().ConfigureAwait(false);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public OwnedTaskSnapshot Snapshot(OwnedTaskHandle task)
    {
        lock (sync) { RequireOwned(task); return task.Snapshot; }
    }

    /// <summary>
    /// Reads only authenticated host storage; no owner records are accepted from callers or models.
    /// Restored handles remain read-only until individually and explicitly resumed.
    /// Ownership proof does not assert current task state; use explicit ReconcileAsync afterwards.
    /// The host owns and disposes the store after all client work has stopped.
    /// </summary>
    public async Task<IReadOnlyList<OwnedTaskHandle>> RestoreReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        Guid requestEpoch;
        lock (sync)
        {
            RequireReadable();
            if (provenance == null) throw OwnedVerification.Error("provenance_store_required");
            if (tasks.Count != 0 || operations.Count != 0) throw OwnedVerification.Error("restore_requires_empty_client");
            restoredReadOnly = true; requestEpoch = epoch;
        }
        var records = await provenance.ReadVerifiedAsync(clock(), cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            RequireReadable(); cancellationToken.ThrowIfCancellationRequested();
            if (requestEpoch != epoch || tasks.Count != 0) throw OwnedVerification.Error("stale_restore");
            var restored = records.Select(entry => new OwnedTaskHandle(
                new(entry.Task, entry.ProjectId, entry.Cwd, entry.Model, entry.ApprovalPolicy, entry.Sandbox,
                    null, OwnedTaskState.Unknown, 1), entry.Creation, OwnedTaskAccess.RestoredReadOnly)).ToArray();
            foreach (var task in restored) tasks.Add(task.Identity.Key, task);
            return restored;
        }
    }

    public ControlReceipt GetOperation(Guid operationId)
    {
        lock (sync)
        {
            if (!operations.TryGetValue(operationId, out var operation)) throw OwnedVerification.Error("operation_not_in_wire_correlation_cache");
            return Receipt(operation, "wire_observation_only_use_outer_durable_ledger");
        }
    }

    /// <summary>Explicit host-authorized resume of one durable owned task; never starts or replays a turn.</summary>
    public async Task<ControlReceipt> ResumeAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (sync)
        {
            RequireCapability("thread/resume"); RequireOwned(task); RequireRevision(task, expectedRevision);
            if (task.Access != OwnedTaskAccess.RestoredReadOnly) throw OwnedVerification.Error("restored_task_required");
            if (task.Snapshot.State is not (OwnedTaskState.Idle or OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted))
                throw OwnedVerification.Error("thread_not_observed_idle");
            if (metadataPending.Contains(task.Identity.Key)) throw OwnedVerification.Error("thread_control_in_flight");
            operation = Begin(operationId, "thread/resume", task, null);
            metadataPending.Add(task.Identity.Key);
        }
        try
        {
            var snapshot = task.Snapshot;
            return await CallAsync(operation, new { threadId = task.Identity.ThreadId, cwd = snapshot.Cwd,
                model = snapshot.Model, approvalPolicy = snapshot.ApprovalPolicy, approvalsReviewer = "user", sandbox = snapshot.Sandbox }, result =>
            {
                var thread = result.GetProperty("thread"); ValidateReadThread(task, thread);
                var sandbox = result.GetProperty("sandbox");
                if (!OwnedVerification.SamePath(Text(result, "cwd"), snapshot.Cwd, configuration.ExpectedPlatformOs)
                    || Text(result, "model") != snapshot.Model || Text(result, "approvalPolicy") != snapshot.ApprovalPolicy
                    || Text(result, "approvalsReviewer") != "user"
                    || Text(sandbox, "type") != (snapshot.Sandbox == "read-only" ? "readOnly" : "workspaceWrite")
                    || sandbox.TryGetProperty("networkAccess", out var network) && network.ValueKind != JsonValueKind.False
                    || sandbox.TryGetProperty("writableRoots", out var roots) && roots.EnumerateArray().Any(r => !OwnedVerification.SamePath(r.GetString() ?? "", snapshot.Cwd, configuration.ExpectedPlatformOs)))
                    throw OwnedVerification.Error("resumed_thread_configuration_mismatch");
                var turns = thread.GetProperty("turns");
                if (turns.GetArrayLength() > 0) ObserveTurn(task, turns.EnumerateArray().Last(), "thread/read", null);
                else Update(task, null, OwnedTaskState.Idle);
                if (task.Snapshot.State is not (OwnedTaskState.Idle or OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted))
                    throw OwnedVerification.Error("thread_not_observed_idle");
                task.Access = OwnedTaskAccess.ResumedThisSession;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (sync) metadataPending.Remove(task.Identity.Key); }
    }

    public long SetFocus(OwnedTaskHandle? task)
    {
        lock (sync)
        {
            ThrowDisposed();
            if (task != null) RequireOwned(task);
            if (focus == task?.Identity) return focusRevision;
            focus = task?.Identity; focusRevision++;
            InvalidateApprovals(ApprovalState.Invalidated);
            return focusRevision;
        }
    }

    public IReadOnlyList<PendingApproval> PendingApprovals()
    {
        lock (sync)
        {
            ExpireApprovals();
            return approvals.Values.Where(x => x.State == ApprovalState.Pending).ToArray();
        }
    }

    public async Task<(ControlReceipt Receipt, OwnedTaskHandle? Task)> CreateAsync(Guid operationId,
        CreateOwnedTask request, CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (sync)
        {
            RequireCapability("thread/start");
            ValidateCreate(request);
        }
        await provenance!.EnsureCanCreateAsync(operationId, clock(), cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            RequireCapability("thread/start");
            operation = Begin(operationId, "thread/start", null, null);
        }
        OwnedTaskHandle? created = null;
        var receipt = await CallAsync(operation, new
        {
            cwd = request.Cwd, model = request.Model, approvalPolicy = request.ApprovalPolicy,
            approvalsReviewer = "user", sandbox = request.Sandbox, ephemeral = false
        }, _ =>
        {
            if (created == null || tasks.ContainsKey(created.Identity.Key)) throw OwnedVerification.Error("created_thread_identity_reused");
            tasks.Add(created.Identity.Key, created); operation.Task = created;
        }, cancellationToken, async (result, ct) =>
        {
            var thread = result.GetProperty("thread");
            string id = Text(thread, "id");
            OwnedVerification.RequireText(id, 512);
            string expectedSandbox = request.Sandbox == "read-only" ? "readOnly" : "workspaceWrite";
            var sandbox = result.GetProperty("sandbox");
            if (!OwnedVerification.SamePath(Text(result, "cwd"), request.Cwd, configuration.ExpectedPlatformOs)
                || !OwnedVerification.SamePath(Text(thread, "cwd"), request.Cwd, configuration.ExpectedPlatformOs)
                || Text(result, "model") != request.Model || Text(result, "approvalPolicy") != request.ApprovalPolicy
                || Text(result, "approvalsReviewer") != "user" || Text(sandbox, "type") != expectedSandbox
                || sandbox.TryGetProperty("networkAccess", out var network) && network.ValueKind != JsonValueKind.False)
                throw OwnedVerification.Error("created_thread_configuration_mismatch");
            if (sandbox.TryGetProperty("writableRoots", out var roots)
                && roots.EnumerateArray().Any(root => !OwnedVerification.SamePath(root.GetString() ?? "", request.Cwd, configuration.ExpectedPlatformOs)))
                throw OwnedVerification.Error("created_thread_extra_write_roots");
            var identity = new CanonicalTaskIdentity(configuration.Connection.Endpoint.HostId, id,
                configuration.Connection.Endpoint.ProfileId);
            // Hash only the validated safe projection, never preview, user text, auth or arbitrary reply fields.
            string receiptDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                threadId = id, cwd = request.Cwd, model = request.Model, approvalPolicy = request.ApprovalPolicy,
                approvalsReviewer = "user", sandbox = request.Sandbox, networkAccess = false,
                writableRoots = sandbox.TryGetProperty("writableRoots", out var writeRoots)
                    ? writeRoots.EnumerateArray().Select(x => x.GetString()).ToArray() : []
            })));
            var evidence = new OwnedCreationEvidence(operationId, clock(), configuration.ExpectedVersion, receiptDigest, operation.ConnectionEpoch);
            await provenance.RecordCreatedAsync(identity, request, evidence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            created = new(new(identity, request.ProjectId, request.Cwd, request.Model,
                request.ApprovalPolicy, request.Sandbox, null, OwnedTaskState.Idle, 1), evidence, OwnedTaskAccess.CreatedThisSession);
        }).ConfigureAwait(false);
        return (receipt, operation.Task);
    }

    public bool SupportsGuardedRequests { get { lock (sync) return connection?.SupportsGuardedRequests == true; } }
    public async Task<ControlReceipt> StartTurnAsync(Guid operationId, OwnedTaskHandle task,
        long expectedRevision, string text, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? beforeWrite = null)
    {
        OwnedVerification.RequireText(text);
        Operation operation;
        lock (sync)
        {
            RequireCapability("turn/start"); RequireWritableOwned(task); RequireRevision(task, expectedRevision);
            if (task.Snapshot.State is not (OwnedTaskState.Idle or OwnedTaskState.Completed or OwnedTaskState.Interrupted or OwnedTaskState.Failed)
                || starting.Contains(task.Identity.Key) || metadataPending.Contains(task.Identity.Key)) throw OwnedVerification.Error("thread_not_observed_idle");
            operation = Begin(operationId, "turn/start", task, null); starting.Add(task.Identity.Key);
        }
        try
        {
            return await CallAsync(operation, new
            {
                threadId = task.Identity.ThreadId,
                input = new[] { new { type = "text", text } },
                clientUserMessageId = operationId.ToString("D")
            }, result =>
            {
                var turn = result.GetProperty("turn");
                string turnId = Text(turn, "id"); OwnedVerification.RequireText(turnId, 512);
                if (operation.Turn != null && operation.Turn != turnId) throw OwnedVerification.Error("turn_response_identity_mismatch");
                operation.Turn = turnId;
                if (operation.State == ControlState.Dispatching)
                    Update(task, turnId, OwnedTaskState.Accepted); // Response status never substitutes for a lifecycle notification.
            }, cancellationToken, beforeWrite: beforeWrite == null ? null : async ct =>
            {
                if (!await beforeWrite(ct).ConfigureAwait(false)) return false;
                lock (sync) return initialized && operation.ConnectionEpoch == epoch && task.Snapshot.Revision == expectedRevision;
            }).ConfigureAwait(false);
        }
        finally { lock (sync) starting.Remove(task.Identity.Key); }
    }

    public Task<ControlReceipt> SteerAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision,
        string expectedTurnId, string text, CancellationToken cancellationToken = default)
    {
        OwnedVerification.RequireText(text);
        Operation operation;
        lock (sync)
        {
            RequireCapability("turn/steer"); RequireActive(task, expectedRevision, expectedTurnId);
            operation = Begin(operationId, "turn/steer", task, expectedTurnId);
        }
        return CallAsync(operation, new { threadId = task.Identity.ThreadId, expectedTurnId,
            input = new[] { new { type = "text", text } }, clientUserMessageId = operationId.ToString("D") },
            result => { if (Text(result, "turnId") != expectedTurnId) throw OwnedVerification.Error("steer_turn_mismatch"); }, cancellationToken);
    }

    public Task<ControlReceipt> InterruptAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision,
        string turnId, CancellationToken cancellationToken = default)
    {
        Operation operation;
        lock (sync)
        {
            RequireCapability("turn/interrupt"); RequireActive(task, expectedRevision, turnId);
            operation = Begin(operationId, "turn/interrupt", task, turnId);
        }
        return CallAsync(operation, new { threadId = task.Identity.ThreadId, turnId }, _ => { }, cancellationToken);
    }

    public Task<ControlReceipt> RenameAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision,
        string name, CancellationToken cancellationToken = default)
    {
        OwnedVerification.RequireText(name, 256);
        return MetadataAsync(operationId, task, expectedRevision, "thread/name/set", new { threadId = task.Identity.ThreadId, name }, cancellationToken);
    }
    public Task<ControlReceipt> ArchiveAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision, CancellationToken cancellationToken = default)
        => MetadataAsync(operationId, task, expectedRevision, "thread/archive", new { threadId = task.Identity.ThreadId }, cancellationToken);
    public Task<ControlReceipt> UnarchiveAsync(Guid operationId, OwnedTaskHandle task, long expectedRevision, CancellationToken cancellationToken = default)
        => MetadataAsync(operationId, task, expectedRevision, "thread/unarchive", new { threadId = task.Identity.ThreadId }, cancellationToken);

    private async Task<ControlReceipt> MetadataAsync(Guid operationId, OwnedTaskHandle task, long revision,
        string method, object parameters, CancellationToken ct)
    {
        Operation operation;
        lock (sync)
        {
            RequireCapability(method);
            if (method == "thread/unarchive") RequireOwned(task); else RequireWritableOwned(task);
            RequireRevision(task, revision);
            if (task.Snapshot.State is OwnedTaskState.Accepted or OwnedTaskState.Running or OwnedTaskState.WaitingApproval or OwnedTaskState.Unknown)
                throw OwnedVerification.Error("thread_not_observed_idle");
            if (starting.Contains(task.Identity.Key) || metadataPending.Contains(task.Identity.Key)) throw OwnedVerification.Error("thread_control_in_flight");
            operation = Begin(operationId, method, task, null);
            metadataPending.Add(task.Identity.Key);
        }
        try
        {
            return await CallAsync(operation, parameters, result =>
            {
                if (method == "thread/unarchive")
                {
                    var thread = result.GetProperty("thread");
                    ValidateReadThread(task, thread);
                }
                // Archive/unarchive acknowledgement is administrative acceptance, not a completed Codex turn.
                if (method == "thread/archive") Update(task, task.Snapshot.CurrentTurnId, OwnedTaskState.Archived);
                if (method == "thread/unarchive") Update(task, null, OwnedTaskState.Idle);
            }, ct).ConfigureAwait(false);
        }
        finally { lock (sync) metadataPending.Remove(task.Identity.Key); }
    }

    public async Task<ControlReceipt> RespondApprovalAsync(Guid operationId, string token,
        CanonicalTaskIdentity target, string turnId, string projectId, long expectedFocusRevision,
        ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        PendingApproval approval;
        Operation operation;
        lock (sync)
        {
            ExpireApprovals();
            if (!approvals.TryGetValue(token, out approval!) || approval.State != ApprovalState.Pending)
                throw OwnedVerification.Error("approval_expired_consumed_or_missing");
            RequireCapability(approval.Method);
            if (approval.ConnectionEpoch != epoch || approval.Task != target || approval.TurnId != turnId
                || approval.ProjectId != projectId || approval.FocusRevision != expectedFocusRevision
                || expectedFocusRevision != focusRevision || focus != target
                || !approval.Decisions.Contains(decision))
                throw OwnedVerification.Error("approval_context_mismatch");
            var task = tasks[target.Key]; RequireActive(task, task.Snapshot.Revision, turnId);
            operation = Begin(operationId, approval.Method, task, turnId);
            approvals[token] = approval with { State = ApprovalState.Consumed }; // Reserve exactly once, before attempting the wire write.
            approvalOperations[token] = operationId;
        }
        using var timeout = Timeout(cancellationToken);
        try
        {
            await operation.Wire.RespondAsync(approval.RequestId, new { decision = DecisionText(decision) }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (operation.State == ControlState.Dispatching)
                    SetOperation(operation, ControlState.Unknown, "approval_response_written_awaiting_resolution");
                return Receipt(operation, "approval_response_requires_resolution");
            }
        }
        catch (RpcTransportException ex)
        {
            lock (sync)
            {
                SetOperation(operation, ex.MayHaveBeenWritten ? ControlState.Unknown : ControlState.Failed, ex.Code);
                return Receipt(operation, ex.Code);
            }
        }
        catch
        {
            lock (sync) { SetOperation(operation, ControlState.Unknown, "approval_response_unknown"); return Receipt(operation, "approval_response_unknown"); }
        }
    }

    public async Task<ReconciliationRead> ReconcileAsync(OwnedTaskHandle task, CancellationToken cancellationToken = default)
    {
        IJsonRpcConnection wire; Guid requestEpoch;
        lock (sync) { RequireOwned(task); RequireReadable(); wire = connection; requestEpoch = epoch; }
        using var timeout = Timeout(cancellationToken);
        // The transport owns cancellation of its actual read. Do not detach an
        // unfinished adapter task: the host holds a read-budget slot and its
        // shutdown lease until this await really ends.
        var result = await wire.RequestAsync("thread/read",
            new { threadId = task.Identity.ThreadId, includeTurns = true }, timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (requestEpoch != epoch || !initialized) throw OwnedVerification.Error("stale_read_response");
            var thread = result.GetProperty("thread"); ValidateReadThread(task, thread);
            ControlObservation? readObservation = null;
            long beforeReadSequence = sequence;
            if (thread.TryGetProperty("turns", out var turns) && turns.GetArrayLength() > 0)
            {
                ObserveTurn(task, turns.EnumerateArray().Last(), "thread/read", null);
                var latest = turnObservations.GetValueOrDefault(task.Identity.Key);
                if (latest?.Sequence > beforeReadSequence)
                { readObservation = latest; commandReadSequences[task.Identity.Key] = latest.Sequence; }
                else commandReadSequences.Remove(task.Identity.Key);
            }
            else { Update(task, null, OwnedTaskState.Idle); commandReadSequences.Remove(task.Identity.Key); }
            return new(task.Snapshot, thread.Clone(), "owned_app_server_thread_read", task.Access == OwnedTaskAccess.RestoredReadOnly,
                readObservation);
        }
    }

    public async Task<OwnedReport> ReadReportAsync(OwnedTaskHandle task, string turnId, CancellationToken cancellationToken = default)
    {
        OwnedVerification.RequireText(turnId, 512);
        var read = await ReconcileAsync(task, cancellationToken).ConfigureAwait(false);
        var turn = read.Thread.GetProperty("turns").EnumerateArray().SingleOrDefault(x => Text(x, "id") == turnId);
        if (turn.ValueKind != JsonValueKind.Object) throw OwnedVerification.Error("requested_turn_not_observed");
        var items = turn.GetProperty("items").EnumerateArray().ToArray();
        string report = string.Join("\n", items.Where(x => Text(x, "type") == "agentMessage" && Text(x, "phase") == "final_answer").Select(x => Text(x, "text")));
        return new(task.Identity, turnId, report,
            items.Where(x => Text(x, "type") == "commandExecution").Select(x => x.Clone()).ToImmutableArray(),
            items.Where(x => Text(x, "type") == "fileChange").Select(x => x.Clone()).ToImmutableArray(),
            "owned_app_server_recorded_items", false);
    }

    private async Task<ControlReceipt> CallAsync(Operation operation, object parameters, Action<JsonElement> accepted, CancellationToken ct,
        Func<JsonElement, CancellationToken, Task>? beforeAccept = null, Func<CancellationToken, Task<bool>>? beforeWrite = null)
    {
        using var timeout = Timeout(ct);
        try
        {
            var request = beforeWrite == null ? operation.Wire.RequestAsync(operation.Method, parameters, timeout.Token)
                : operation.Wire.RequestGuardedAsync(operation.Method, parameters, beforeWrite, timeout.Token);
            var result = await request.WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (operation.ConnectionEpoch != epoch || !initialized)
                {
                    SetOperation(operation, ControlState.Unknown, "reply_from_disconnected_epoch");
                    return Receipt(operation, "reply_from_disconnected_epoch");
                }
            }
            if (beforeAccept != null)
            {
                // No state lock spans persistence. Do not detach an unfinished append on timeout.
                await beforeAccept(result, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
            }
            lock (sync)
            {
                if (operation.ConnectionEpoch != epoch || !initialized)
                {
                    SetOperation(operation, ControlState.Unknown, "reply_from_disconnected_epoch");
                    return Receipt(operation, "reply_from_disconnected_epoch");
                }
                accepted(result);
                if (operation.State == ControlState.Dispatching) SetOperation(operation, ControlState.Accepted, "server_response_accepted");
                return Receipt(operation, "server_response_accepted");
            }
        }
        catch (ProvenanceException ex)
        {
            lock (sync)
            {
                SetOperation(operation, ControlState.Unknown, ex.Code);
                return Receipt(operation, ex.Code);
            }
        }
        catch (RpcRemoteException)
        {
            lock (sync) { SetOperation(operation, ControlState.Failed, "server_rejected"); return Receipt(operation, "server_rejected"); }
        }
        catch (RpcTransportException ex)
        {
            lock (sync)
            {
                var state = ex.MayHaveBeenWritten ? ControlState.Unknown : ct.IsCancellationRequested || ex.Code == "request_guard_rejected"
                    ? ControlState.CancelledBeforeDispatch : ControlState.Failed;
                SetOperation(operation, state, ex.Code);
                if (state == ControlState.Unknown && operation.State == ControlState.Unknown && operation.Task != null && operation.ConnectionEpoch == epoch)
                    Update(operation.Task, operation.Turn, OwnedTaskState.Unknown);
                return Receipt(operation, ex.Code);
            }
        }
        catch
        {
            lock (sync)
            {
                SetOperation(operation, ControlState.Unknown, "response_not_verified");
                if (operation.State == ControlState.Unknown && operation.Task != null && operation.ConnectionEpoch == epoch)
                    Update(operation.Task, operation.Turn, OwnedTaskState.Unknown);
                return Receipt(operation, "response_not_verified");
            }
        }
    }

    private void OnIncoming(RpcIncoming message, IJsonRpcConnection source, Guid sourceEpoch)
    {
        lock (sync)
        {
            if (disposed || !initialized || !ReferenceEquals(source, connection) || sourceEpoch != epoch) return;
            string threadId = Text(message.Parameters, "threadId");
            var identity = threadId.Length == 0 ? null : new CanonicalTaskIdentity(configuration.Connection.Endpoint.HostId,
                threadId, configuration.Connection.Endpoint.ProfileId);
            if (identity == null || !tasks.TryGetValue(identity.Key, out var task)) return; // Notifications never establish ownership.
            if (message.RequestId.HasValue)
            {
                RegisterApproval(task, message);
                return;
            }
            switch (message.Method)
            {
                case "turn/started":
                case "turn/completed":
                    ObserveTurn(task, message.Parameters.GetProperty("turn"), message.Method, null);
                    break;
                case "thread/status/changed":
                    var status = message.Parameters.GetProperty("status");
                    if (Text(status, "type") == "active" && status.TryGetProperty("activeFlags", out var flags)
                        && task.Snapshot.CurrentTurnId != null
                        && task.Snapshot.State is OwnedTaskState.Running or OwnedTaskState.WaitingApproval)
                        Update(task, task.Snapshot.CurrentTurnId, flags.EnumerateArray().Any(x => x.GetString() == "waitingOnApproval")
                            ? OwnedTaskState.WaitingApproval : OwnedTaskState.Running);
                    break;
                case "serverRequest/resolved":
                    ResolveApproval(task, message.Parameters.GetProperty("requestId"));
                    break;
                case "item/started":
                case "item/completed":
                    string turnId = Text(message.Parameters, "turnId");
                    var item = message.Parameters.GetProperty("item");
                    string itemId = Text(item, "id");
                    // A command changed after a read: that read cannot release a command prerequisite.
                    if (turnId == task.Snapshot.CurrentTurnId && Text(item, "type") == "commandExecution")
                        commandReadSequences.Remove(task.Identity.Key);
                    if (turnId == task.Snapshot.CurrentTurnId && itemId.Length > 0
                        && Text(item, "type") is "fileChange" or "commandExecution"
                        && item.GetRawText().Length <= 128 * 1024 && approvalItems.Count < 256)
                        approvalItems[(task.Identity.Key, turnId, itemId)] = item.Clone();
                    break;
            }
        }
    }

    private void ObserveTurn(OwnedTaskHandle task, JsonElement turn, string source, Guid? operationId)
    {
        string id = Text(turn, "id"); OwnedVerification.RequireText(id, 512);
        string status = Text(turn, "status");
        var state = status switch
        {
            "inProgress" => OwnedTaskState.Running, "completed" => OwnedTaskState.Completed,
            "interrupted" => OwnedTaskState.Interrupted, "failed" => OwnedTaskState.Failed,
            _ => OwnedTaskState.Unknown
        };
        if (!seenTurns.TryGetValue(task.Identity.Key, out var seen))
            seenTurns[task.Identity.Key] = seen = new(StringComparer.Ordinal);
        bool previouslyObserved = seen.TryGetValue(id, out var previousState);
        // A repeated delayed started event cannot reopen an already terminal turn.
        if (previouslyObserved && previousState is OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted
            && state == OwnedTaskState.Running) return;
        var start = operations.Values.LastOrDefault(x => x.Task == task && x.Method == "turn/start"
            && (x.Turn == id || x.Turn == null && !previouslyObserved && source != "thread/read"
                && x.ConnectionEpoch == epoch && x.State != ControlState.Unknown && starting.Contains(task.Identity.Key)));
        if (start != null && start.Turn == null) start.Turn = id;
        // Late older-turn events cannot overwrite a currently observed different turn.
        if (source != "thread/read" && task.Snapshot.CurrentTurnId != null && task.Snapshot.CurrentTurnId != id
            && !(start != null && starting.Contains(task.Identity.Key))) return;
        seen[id] = state;
        Update(task, id, state);
        ControlState observed = state switch
        {
            OwnedTaskState.Running => ControlState.Running, OwnedTaskState.Completed => ControlState.Completed,
            OwnedTaskState.Interrupted => ControlState.Interrupted, OwnedTaskState.Failed => ControlState.Failed,
            _ => ControlState.Unknown
        };
        if (start != null)
        {
            if (start.State == ControlState.Unknown && source != "thread/read")
                Publish(null, task, id, source, observed, "turn_observed_operation_still_unknown");
            long beforeReadSequence = sequence;
            SetOperation(start, observed, source == "thread/read" ? "read_reconciliation" : "turn_lifecycle_observed", source == "thread/read");
            // A successful exact read is fresh evidence even when it does not
            // change an operation state. Keep its own order/time for consumers.
            if (source == "thread/read" && sequence == beforeReadSequence)
                Publish(null, task, id, source, observed, "read_snapshot", true);
        }
        else Publish(operationId, task, id, source, observed,
            source == "thread/read" ? "read_snapshot" : "turn_lifecycle_observed");
        if (state is OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted)
        {
            foreach (var interrupt in operations.Values.Where(x => x.Task == task && x.Method == "turn/interrupt" && x.Turn == id))
                SetOperation(interrupt, state == OwnedTaskState.Interrupted ? ControlState.Interrupted : ControlState.Failed,
                    state == OwnedTaskState.Interrupted ? "interruption_observed" : "turn_ended_without_interruption", source == "thread/read");
            foreach (var approval in approvals.Values.Where(x => x.Task == task.Identity && x.TurnId == id && x.State == ApprovalState.Pending).ToArray())
                approvals[approval.Token] = approval with { State = ApprovalState.Invalidated };
            foreach (var key in approvalItems.Keys.Where(x => x.Task == task.Identity.Key && x.Turn == id).ToArray())
                approvalItems.Remove(key);
        }
    }

    private void RegisterApproval(OwnedTaskHandle task, RpcIncoming message)
    {
        if (task.Access == OwnedTaskAccess.RestoredReadOnly) return;
        if (message.Method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval")) return;
        if (approvals.Count >= 256) { observationOverflow = true; throw OwnedVerification.Error("approval_capacity"); }
        string turnId = Text(message.Parameters, "turnId"), item = Text(message.Parameters, "itemId");
        if (turnId != task.Snapshot.CurrentTurnId || task.Snapshot.State is not (OwnedTaskState.Running or OwnedTaskState.WaitingApproval)
            || item.Length == 0) return;
        string requestKey = message.RequestId!.Value.GetRawText();
        if (approvals.Values.Any(x => x.ConnectionEpoch == epoch && x.RequestId.GetRawText() == requestKey)) return;
        string cwd = Text(message.Parameters, "cwd");
        if (cwd.Length > 0 && !OwnedVerification.SamePath(cwd, task.Snapshot.Cwd, configuration.ExpectedPlatformOs)) return;
        long milliseconds = message.Parameters.TryGetProperty("startedAtMs", out var timestamp) && timestamp.TryGetInt64(out long ms) ? ms : 0;
        DateTimeOffset started;
        try { started = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { return; }
        if (started > clock().AddSeconds(30) || started + configuration.ApprovalLifetime <= clock()) return;
        string token = Guid.NewGuid().ToString("N");
        string summary = Text(message.Parameters, "command");
        JsonElement? details = approvalItems.TryGetValue((task.Identity.Key, turnId, item), out var operationDetails) ? operationDetails : null;
        bool commandApproval = message.Method == "item/commandExecution/requestApproval";
        bool canAccept = commandApproval ? summary.Length > 0 : details.HasValue
            && Text(details.Value, "type") == "fileChange" && details.Value.TryGetProperty("changes", out var changes)
            && changes.ValueKind == JsonValueKind.Array && changes.GetArrayLength() > 0
            && changes.EnumerateArray().All(change => Text(change, "path").Length > 0
                && change.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String
                && change.TryGetProperty("kind", out var kind) && Text(kind, "type") is "add" or "update" or "delete")
            && Text(message.Parameters, "grantRoot").Length == 0;
        if (commandApproval) details = message.Parameters.Clone();
        else if (canAccept) summary = "File changes: " + string.Join(", ",
            details!.Value.GetProperty("changes").EnumerateArray().Select(x => Text(x, "path")));
        if (summary.Length == 0) summary = Text(message.Parameters, "reason");
        if (!canAccept) summary = "Operation details incomplete or permission expansion requested. Approval is unavailable. " + summary;
        if (summary.Length > 8192) summary = summary[..8192];
        approvals[token] = new(token, message.RequestId.Value.Clone(), epoch, task.Identity, turnId,
            task.Snapshot.ProjectId, task.Snapshot.Cwd, item, message.Method, summary,
            canAccept ? [ApprovalDecision.Accept, ApprovalDecision.AcceptForSession, ApprovalDecision.Decline, ApprovalDecision.Cancel]
                : [ApprovalDecision.Decline, ApprovalDecision.Cancel],
            started + configuration.ApprovalLifetime, focusRevision, ApprovalState.Pending, details);
        Update(task, turnId, OwnedTaskState.WaitingApproval);
        Publish(null, task, turnId, message.Method, ControlState.Running, "approval_requested");
    }

    private void ResolveApproval(OwnedTaskHandle task, JsonElement requestId)
    {
        foreach (var approval in approvals.Values.Where(x => x.Task == task.Identity && x.ConnectionEpoch == epoch
            && x.RequestId.GetRawText() == requestId.GetRawText()).ToArray())
        {
            approvals[approval.Token] = approval with { State = ApprovalState.Resolved };
            if (approvalOperations.TryGetValue(approval.Token, out var id))
                SetOperation(operations[id], ControlState.Accepted, "server_request_resolved", reconciliation: true);
        }
    }
    private void ExpireApprovals()
    {
        foreach (var approval in approvals.Values.Where(x => x.State == ApprovalState.Pending && x.ExpiresAt <= clock()).ToArray())
            approvals[approval.Token] = approval with { State = ApprovalState.Expired };
    }
    private void InvalidateApprovals(ApprovalState state)
    {
        foreach (var approval in approvals.Values.Where(x => x.State == ApprovalState.Pending).ToArray())
            approvals[approval.Token] = approval with { State = state };
    }
    private void MarkDisconnected()
    {
        initialized = false; InvalidateApprovals(ApprovalState.Invalidated);
        foreach (var task in tasks.Values.Where(x => x.Snapshot.State is OwnedTaskState.Accepted or OwnedTaskState.Running or OwnedTaskState.WaitingApproval))
            Update(task, task.Snapshot.CurrentTurnId, OwnedTaskState.Unknown);
        foreach (var operation in operations.Values.Where(x => x.State is ControlState.Dispatching or ControlState.Running
            || x.State == ControlState.Accepted && x.Method == "turn/start"))
            SetOperation(operation, ControlState.Unknown, "connection_lost_requires_read_reconciliation");
    }
    private Operation Begin(Guid id, string method, OwnedTaskHandle? task, string? turn)
    {
        if (id == Guid.Empty || operations.ContainsKey(id)) throw OwnedVerification.Error("operation_id_missing_or_reused");
        if (operations.Count >= 1024) throw OwnedVerification.Error("wire_correlation_capacity");
        var operation = new Operation(id, method, task, turn, epoch, connection); operations.Add(id, operation);
        Publish(id, task, turn, method, ControlState.Dispatching, "dispatch_requested");
        return operation;
    }
    private void SetOperation(Operation operation, ControlState state, string code, bool reconciliation = false)
    {
        if (operation.State is ControlState.Completed or ControlState.Interrupted or ControlState.Failed) return;
        if (operation.State == ControlState.Unknown && state != ControlState.Unknown && !reconciliation) return;
        if (operation.State == ControlState.Running && state == ControlState.Accepted) return;
        if (operation.State == state && state != ControlState.Unknown) return;
        operation.State = state; Publish(operation.Id, operation.Task, operation.Turn, operation.Method, state, code, reconciliation);
    }
    private void Publish(Guid? id, OwnedTaskHandle? task, string? turn, string method, ControlState state, string code, bool reconciliation = false)
    {
        if (disposed) return;
        var observation = new ControlObservation(++sequence, clock(), id, configuration.Connection.Endpoint,
            task?.Identity, turn, method, state, code, reconciliation, epoch);
        if (task != null && turn != null && code is "turn_lifecycle_observed" or "read_reconciliation"
            or "read_snapshot" or "turn_observed_operation_still_unknown" or "interruption_observed")
            turnObservations[task.Identity.Key] = observation;
        if (!observations.Writer.TryWrite(observation))
        { observationOverflow = true; throw OwnedVerification.Error("observation_consumer_overflow"); }
    }
    private static ControlReceipt Receipt(Operation operation, string code)
        => new(operation.Id, operation.State, operation.Task?.Identity, operation.Turn, code);
    private static void Update(OwnedTaskHandle task, string? turn, OwnedTaskState state)
    {
        if (task.Snapshot.CurrentTurnId == turn && task.Snapshot.State == state) return;
        task.Snapshot = task.Snapshot with { CurrentTurnId = turn, State = state, Revision = task.Snapshot.Revision + 1 };
    }
    private void ValidateCreate(CreateOwnedTask request)
    {
        if (!configuration.Projects.TryGetValue(request.ProjectId, out string? cwd)
            || cwd != request.Cwd
            || !configuration.Models.Contains(request.Model)) throw OwnedVerification.Error("unconfigured_project_directory_or_model");
        if (request.ApprovalPolicy is not ("untrusted" or "on-request") || request.Sandbox is not ("read-only" or "workspace-write"))
            throw OwnedVerification.Error("explicit_conservative_permissions_required");
    }
    private void ValidateReadThread(OwnedTaskHandle task, JsonElement thread)
    {
        if (Text(thread, "id") != task.Identity.ThreadId
            || !OwnedVerification.SamePath(Text(thread, "cwd"), task.Snapshot.Cwd, configuration.ExpectedPlatformOs))
            throw OwnedVerification.Error("read_thread_identity_or_directory_mismatch");
    }
    private void RequireActive(OwnedTaskHandle task, long revision, string turn)
    {
        RequireWritableOwned(task); RequireRevision(task, revision);
        if (string.IsNullOrWhiteSpace(turn) || task.Snapshot.CurrentTurnId != turn
            || task.Snapshot.State is not (OwnedTaskState.Running or OwnedTaskState.WaitingApproval))
            throw OwnedVerification.Error("expected_active_turn_mismatch");
    }
    private static void RequireRevision(OwnedTaskHandle task, long revision)
    { if (task.Snapshot.Revision != revision) throw OwnedVerification.Error("stale_task_revision"); }
    private void RequireOwned(OwnedTaskHandle task)
    {
        ThrowDisposed();
        if (!tasks.TryGetValue(task.Identity.Key, out var known) || !ReferenceEquals(known, task)
            || task.Identity.Endpoint != configuration.Connection.Endpoint)
            throw OwnedVerification.Error("assistant_owned_handle_required");
    }
    private void RequireWritableOwned(OwnedTaskHandle task)
    {
        RequireOwned(task);
        if (task.Access is not (OwnedTaskAccess.CreatedThisSession or OwnedTaskAccess.ResumedThisSession)) throw OwnedVerification.Error("restored_provenance_read_only");
    }
    private void RequireCapability(string method)
    { string? failure = CapabilityFailure(method); if (failure != null) throw OwnedVerification.Error(failure); }
    private void RequireReadable()
    { ThrowDisposed(); if (!initialized || !connection.IsConnected) throw OwnedVerification.Error("not_initialized_or_disconnected"); }
    private void ThrowDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(configuration.RequestTimeout); return timeout;
    }
    private void Subscribe(IJsonRpcConnection target)
    {
        Guid subscribedEpoch = epoch;
        incomingHandler = message => OnIncoming(message, target, subscribedEpoch);
        disconnectedHandler = () => { lock (sync) { if (ReferenceEquals(target, connection) && subscribedEpoch == epoch) MarkDisconnected(); } };
        target.Incoming += incomingHandler; target.Disconnected += disconnectedHandler;
    }
    private void Unsubscribe(IJsonRpcConnection target)
    {
        target.Incoming -= incomingHandler; target.Disconnected -= disconnectedHandler;
        incomingHandler = null; disconnectedHandler = null;
    }
    private static string Text(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static string DecisionText(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "accept", ApprovalDecision.AcceptForSession => "acceptForSession",
        ApprovalDecision.Decline => "decline", ApprovalDecision.Cancel => "cancel", _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            try { MarkDisconnected(); }
            catch (ControlException ex) when (ex.Code == "observation_consumer_overflow") { }
            disposed = true; Unsubscribe(connection);
        }
        await connection.DisposeAsync().ConfigureAwait(false);
        observations.Writer.TryComplete();
    }
}
