using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal enum OwnedProfileMode { Disabled, ReadOnly, VerifiedControl }

// Trusted host configuration only. None of these objects are model tool arguments.
internal sealed record ConfiguredOwnedProfile(string Id, string Label, OwnedServerConfiguration Configuration,
    CreateOwnedTask Creation, OwnedProcessLaunch? Launch = null, OwnedProfileMode Mode = OwnedProfileMode.Disabled);
internal sealed record OwnedRuntimeTask(string ConfigurationId, OwnedTaskHandle Handle, OwnedTaskSnapshot Snapshot,
    DateTimeOffset? ObservedAt, string EvidenceSource);
internal sealed record OwnedCatalogueTask(string ConfigurationId, OwnedCreationFact Fact);
internal sealed record OwnedCatalogue(IReadOnlyList<OwnedCatalogueTask> Tasks, IReadOnlyList<string> UnavailableConfigurations);
internal sealed record OwnedQuerySnapshot(string Id, string Query, string Target, string? TurnId, int PageSize,
    DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt, ImmutableArray<JsonElement> Pages);
internal sealed record OwnedRuntimeEvent(string ConfigurationId, ControlObservation Observation,
    CommitmentTaskObservation? CommitmentObservation = null);
internal sealed record PresentedOwnedApproval(ConversationIdentity Identity, Guid EventId, string ConfigurationId,
    PendingApproval Request, long FocusRevision, ConversationEvent Notice);

/// <summary>
/// One runtime belongs to one authenticated channel binding. Host shutdown must await DisposeAsync
/// before releasing its runtime-directory lease or disposing the shared ledger. No construction,
/// capability query or local listing starts a process.
/// </summary>
internal sealed partial class OwnedControlRuntime : IAsyncDisposable
{
    internal sealed class Session(ConfiguredOwnedProfile profile, OwnedAppServerClient client, OwnedProvenanceStore store)
    {
        internal readonly ConfiguredOwnedProfile Profile = profile;
        internal readonly OwnedAppServerClient Client = client;
        internal readonly OwnedProvenanceStore Store = store;
        internal readonly Dictionary<string, OwnedTaskHandle> Tasks = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, DateTimeOffset> Observed = new(StringComparer.Ordinal);
        internal Task Pump = Task.CompletedTask;
        internal readonly SemaphoreSlim ObservationRegistration = new(1, 1);
        internal CommitmentObservationLease? ObservationLease;
        internal readonly Dictionary<string, (Guid Epoch, long Sequence, ControlState State)> CommitmentStates = new(StringComparer.Ordinal);
    }
    private readonly object sync = new();
    private readonly string dataDirectory;
    private readonly ImmutableDictionary<string, ConfiguredOwnedProfile> profiles;
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> openGates;
    private readonly Dictionary<Guid, OperationRecord> dispatched = new();
    private readonly Dictionary<Guid, PresentedOwnedApproval> presented = new();
    private readonly HashSet<string> startingDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedQuerySnapshot> querySnapshots = new(StringComparer.Ordinal);
    private readonly WorkspaceWriteCoordinator? workspace;
    private readonly SemaphoreSlim workspaceRecovery = new(1, 1);
    private bool workspacePrepared;
    private string? currentReport;
    private int currentReportPage;
    private readonly HashSet<string> acknowledgedEmptyTasks = new(StringComparer.Ordinal);
    private readonly Func<ConversationIdentity?> activeIdentity;
    private readonly Func<ConfiguredOwnedProfile, CancellationToken, Task<IJsonRpcConnection>> connect;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<OwnedRuntimeEvent> events = Channel.CreateBounded<OwnedRuntimeEvent>(256);
    private int inFlight;
    private bool stopping;
    private TaskCompletionSource idle = Completed();
    private Task? stopped;
    private string? failure;
    private CanonicalTaskIdentity? focusedTask;
    private long focusedRevision;
    internal DurableConversationLedger Ledger { get; }
    internal ConversationIdentity Identity { get; }
    internal string? Failure { get { lock (sync) return failure; } }
    internal IReadOnlyList<ConfiguredOwnedProfile> Profiles => profiles.Values.ToArray();
    internal bool WorkspaceReady => workspace?.Ready ?? true;
    // A candidate file makes local inspection useful; only ReadCatalogueAsync authenticates it.
    // File presence never establishes task ownership or enables a connection.
    internal bool HasLocalProvenance => profiles.Keys.Any(id => File.Exists(ProvenancePath(id)));

    internal OwnedControlRuntime(ConversationIdentity identity, Func<ConversationIdentity?> activeIdentity,
        DurableConversationLedger ledger, string dataDirectory, IEnumerable<ConfiguredOwnedProfile> profiles,
        Func<ConfiguredOwnedProfile, CancellationToken, Task<IJsonRpcConnection>>? connectionFactory = null,
        Func<DateTimeOffset>? clock = null, WorkspaceWriteCoordinator? workspace = null)
    {
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("absolute_owned_data_directory_required");
        Identity = identity; this.activeIdentity = activeIdentity; Ledger = ledger;
        this.dataDirectory = dataDirectory; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.workspace = workspace;
        var configured = profiles.ToArray();
        foreach (var profile in configured)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64
                || profile.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                || string.IsNullOrWhiteSpace(profile.Label) || profile.Label.Length > 128 || !Enum.IsDefined(profile.Mode)
                || !profile.Configuration.Projects.TryGetValue(profile.Creation.ProjectId, out var cwd) || cwd != profile.Creation.Cwd
                || !profile.Configuration.Models.Contains(profile.Creation.Model)
                || profile.Creation.ApprovalPolicy is not ("on-request" or "untrusted")
                || profile.Creation.Sandbox is not ("read-only" or "workspace-write"))
                throw new ArgumentException("invalid_trusted_owned_profile");
        }
        this.profiles = configured.ToImmutableDictionary(p => p.Id, StringComparer.Ordinal);
        openGates = configured.ToDictionary(p => p.Id, _ => new SemaphoreSlim(1, 1), StringComparer.Ordinal);
        connect = connectionFactory ?? ((profile, ct) => OwnedProcessConnection.StartAsync(
            profile.Configuration.Connection, profile.Launch ?? throw new InvalidOperationException("owned_launch_not_configured"), ct));
    }
    internal bool Active => !stopping && activeIdentity() == Identity;
    internal ConfiguredOwnedProfile? Profile(string id) => profiles.GetValueOrDefault(id);
    internal string? WriteFailure(string configurationId, string method)
    {
        lock (sync)
        {
            if (!Active) return "owned_binding_inactive";
            if (failure != null) return failure;
            if (!profiles.TryGetValue(configurationId, out var profile) || profile.Mode != OwnedProfileMode.VerifiedControl)
                return "owned_control_disabled";
            string? gate = OwnedVerification.CapabilityFailure(profile.Configuration, method, clock());
            if (gate != null) return gate;
            if (sessions.TryGetValue(configurationId, out var session)) return session.Client.CapabilityFailure(method);
            return method is not ("thread/resume" or "thread/start" or "thread/unarchive") && File.Exists(ProvenancePath(configurationId)) ? "owned_existing_provenance_read_only_recovery" : null;
        }
    }
    internal IAsyncEnumerable<OwnedRuntimeEvent> ReadEventsAsync(CancellationToken ct = default) => events.Reader.ReadAllAsync(ct);
    internal IReadOnlyList<OwnedRuntimeTask> Tasks()
    {
        lock (sync)
            return sessions.Values.SelectMany(s => s.Tasks.Values.Select(t => new OwnedRuntimeTask(s.Profile.Id, t,
                s.Client.Snapshot(t), s.Observed.GetValueOrDefault(t.Identity.Key) is { } at && at != default ? at : null,
                t.Access == OwnedTaskAccess.RestoredReadOnly ? "protected_owned_provenance_last_observation" : "owned_app_server_last_observation"))).ToArray();
    }
    internal OwnedRuntimeTask? FindTask(string configurationId, string threadId)
        => Tasks().SingleOrDefault(t => t.ConfigurationId == configurationId && t.Handle.Identity.ThreadId == threadId);
    internal Session? LoadedSession(string configurationId) { lock (sync) return sessions.GetValueOrDefault(configurationId); }

    internal async Task PrepareWorkspaceAsync(CancellationToken ct = default)
    {
        if (workspace == null || workspacePrepared) return;
        await workspaceRecovery.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (workspacePrepared) return;
            workspace.RequireRecovery("owned");
            var identities = (await Ledger.ListConversationIdentitiesAsync(ct).ConfigureAwait(false)).Append(Identity).Distinct().ToArray();
            var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proofs = new Dictionary<(ConversationIdentity, string), IReadOnlyList<OwnedCreationFact>>();
            foreach (var identity in identities)
            {
                var history = await Ledger.ReadConversationAsync(identity, ct).ConfigureAwait(false);
                foreach (var profile in profiles.Values)
                {
                    string path = ProvenancePath(identity, profile.Id);
                    knownFiles.Add(path);
                    if (!File.Exists(path)) continue;
                    IReadOnlyList<OwnedCreationFact> facts;
                    if (identity == Identity && LoadedSession(profile.Id) is { } loaded)
                        facts = await loaded.Store.ReadCreationFactsAsync(clock(), ct).ConfigureAwait(false);
                    else
                    {
                        await using var store = new OwnedProvenanceStore(profile.Configuration, new FileOwnedProvenanceStorage(path, true),
                            new BindingProtector(identity, profile.Id));
                        facts = await store.ReadCreationFactsAsync(clock(), ct).ConfigureAwait(false);
                    }
                    proofs.Add((identity, profile.Id), facts);
                    foreach (var fact in facts)
                    {
                        // An acknowledged empty creation with no execution intent does not
                        // occupy the workspace. Unknown creation/dispatch still recovers below.
                        bool neverStarted = history.Operations.Any(o => o.Id == fact.Creation.CreateOperationId
                            && o.Action == "create_task" && o.Stage == OperationStage.Accepted)
                            && !history.Operations.Any(o => o.Task?.ThreadId == fact.Task.ThreadId
                                && o.Task.HostId == fact.Task.HostId && o.Task.ProfileId == fact.Task.ProfileId
                                && o.Action is "continue_owned_task" or "steer_turn" or "respond_approval");
                        if (neverStarted)
                        {
                            if (identity == Identity) lock (sync) acknowledgedEmptyTasks.Add(fact.Task.Key);
                            continue;
                        }
                        workspace.Observe(new(fact.Task, Location(profile, fact.Cwd), null, WorkspaceTaskState.Unknown,
                            fact.Creation.CreatedAt, false, WorkspaceEvidenceKind.CreationOnly, OwnerScope: WorkspaceOwner(identity)));
                    }
                }
                var terminals = ReadWorkspaceTerminals(identity, history, proofs);
                foreach (var operation in history.Operations.Where(o => OwnedControlToolAdapter.Handles(o.Action)))
                {
                    if (operation.Action is not ("create_task" or "continue_owned_task" or "steer_turn" or "respond_approval")) continue;
                    using var arguments = JsonDocument.Parse(operation.ArgumentsJson);
                    if (!arguments.RootElement.TryGetProperty("configurationId", out var field)
                        || !profiles.TryGetValue(field.GetString()!, out var profile))
                    { workspace.FailRecovery("owned_historical_configuration_missing"); continue; }
                    var facts = proofs.GetValueOrDefault((identity, profile.Id)) ?? [];
                    if (operation.Action == "create_task" && operation.Stage == OperationStage.Accepted
                        && !facts.Any(f => f.Creation.CreateOperationId == operation.Id))
                        workspace.FailRecovery("owned_accepted_creation_proof_missing");
                    if (operation.Stage is not (OperationStage.Dispatching or OperationStage.Accepted or OperationStage.Running or OperationStage.Unknown)) continue;
                    // A known accepted create made an empty thread, not an execution turn.
                    if (operation.Action == "create_task" && operation.Stage == OperationStage.Accepted) continue;
                    if (operation.Action == "respond_approval" && arguments.RootElement.TryGetProperty("decision", out var decision)
                        && decision.GetString() is "decline" or "cancel") continue;
                    CanonicalTaskIdentity? task = operation.Task == null ? null
                        : new(operation.Task.HostId, operation.Task.ThreadId, operation.Task.ProfileId);
                    if (task != null && !facts.Any(f => f.Task == task))
                        workspace.FailRecovery("owned_dispatched_task_proof_missing");
                    // A start authorizes the existing task; any prior turn in that immutable
                    // authorization is not the newly accepted turn. Only actual receipt
                    // binding may establish which turn occupies the directory.
                    string? turn = operation.Action == "continue_owned_task" ? null : operation.Task?.TurnId;
                    if (operation.Action == "continue_owned_task" && task != null)
                    {
                        var boundTurns = ReadWorkspaceObservations(identity, history, operation, profile, task)
                            .Where(o => o.Method == "turn/start" && o.Code is "server_response_accepted" or "turn_lifecycle_observed" or "read_reconciliation")
                            .Select(o => o.TurnId).Distinct(StringComparer.Ordinal).ToArray();
                        // An old receipt's unproved turn string must not borrow another
                        // operation's terminal evidence, even for the same task and owner.
                        if (boundTurns.Length == 1) turn = boundTurns[0];
                    }
                    if (task != null && turn != null && terminals.Any(t => t.ConfigurationId == profile.Id
                        && t.Task == task && t.TurnId == turn && t.ObservedAt >= operation.CreatedAt
                        && (operation.Stage != OperationStage.Unknown || t.Reconciliation))) continue;
                    workspace.RestoreUnresolved(new("owned/" + operation.Id.ToString("N"), task,
                        Location(profile, profile.Creation.Cwd), turn, operation.CreatedAt,
                        RequireReconciliation: operation.Stage == OperationStage.Unknown, OwnerScope: WorkspaceOwner(identity)));
                }
            }
            foreach (var identity in identities) await RestoreDependentWorkspaceAsync(identity, ct).ConfigureAwait(false);
            if (Directory.Exists(dataDirectory) && Directory.EnumerateFiles(dataDirectory, "*.owned", SearchOption.TopDirectoryOnly)
                .Any(path => !knownFiles.Contains(path))) workspace.FailRecovery("owned_unidentified_historical_proof");
            workspacePrepared = true;
            workspace.CompleteRecovery("owned");
            RefreshWorkspace();
        }
        catch (OperationCanceledException) { throw; }
        catch { workspace.FailRecovery("owned_workspace_recovery_unverified"); workspacePrepared = true; }
        finally { workspaceRecovery.Release(); }
    }
    private sealed record WorkspaceTerminal(string ConfigurationId, CanonicalTaskIdentity Task, string TurnId,
        DateTimeOffset ObservedAt, bool Reconciliation);
    private sealed record WorkspaceObservation(string Method, string Code, string TurnId, string Phase,
        DateTimeOffset ObservedAt, bool Reconciliation);
    private IReadOnlyList<WorkspaceObservation> ReadWorkspaceObservations(ConversationIdentity identity, ConversationSnapshot history,
        OperationRecord operation, ConfiguredOwnedProfile profile, CanonicalTaskIdentity task)
    {
        var result = new List<WorkspaceObservation>();
        if (operation.Identity != identity || operation.Task == null || task.Endpoint != profile.Configuration.Connection.Endpoint) return result;
        var evidence = history.Events.Where(e => e.Identity == identity && e.Source == ConversationSource.ToolResult
            && e.RelatedOperationId == operation.Id && e.Task == operation.Task).Select(e => e.Body);
        if (operation.Receipt != null) evidence = evidence.Prepend(operation.Receipt);
        foreach (string body in evidence)
        {
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || Field(value, "source") != "owned_app_server"
                    || Field(value, "configurationId") != profile.Id
                    || Field(value, "configurationDigest") != OwnedVerification.ConfigurationDigest(profile.Configuration)
                    || !value.TryGetProperty("operationId", out var operationId) || operationId.GetGuid() != operation.Id
                    || !value.TryGetProperty("endpoint", out var endpoint) || endpoint.Deserialize<HostEndpointIdentity>() != task.Endpoint
                    || !value.TryGetProperty("observedTask", out var observed) || observed.Deserialize<CanonicalTaskIdentity>() != task
                    || !value.TryGetProperty("observedAt", out var atValue) || !atValue.TryGetDateTimeOffset(out var at)
                    || at < operation.CreatedAt || at == default
                    || string.IsNullOrWhiteSpace(Field(value, "observedTurnId"))) continue;
                result.Add(new(Field(value, "Method") ?? "", Field(value, "Code") ?? "", Field(value, "observedTurnId")!,
                    Field(value, "phase") ?? "", at, value.TryGetProperty("Reconciliation", out var reconciled)
                        && reconciled.ValueKind == JsonValueKind.True));
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException) { }
        }
        return result;
    }
    private IReadOnlyList<WorkspaceTerminal> ReadWorkspaceTerminals(ConversationIdentity identity, ConversationSnapshot history,
        Dictionary<(ConversationIdentity, string), IReadOnlyList<OwnedCreationFact>> proofs)
    {
        var result = new List<WorkspaceTerminal>();
        foreach (var operation in history.Operations.Where(o => o.Identity == identity && o.Task != null
            && o.Action is "continue_owned_task" or "interrupt_turn"
            && o.Stage is OperationStage.Completed or OperationStage.Failed or OperationStage.Interrupted))
        {
            using var arguments = JsonDocument.Parse(operation.ArgumentsJson);
            if (!arguments.RootElement.TryGetProperty("configurationId", out var configuration)
                || !profiles.TryGetValue(configuration.GetString()!, out var profile)) continue;
            var task = new CanonicalTaskIdentity(operation.Task!.HostId, operation.Task.ThreadId, operation.Task.ProfileId);
            if (task.Endpoint != profile.Configuration.Connection.Endpoint
                || !(proofs.GetValueOrDefault((identity, profile.Id)) ?? []).Any(f => f.Task == task)) continue;
            foreach (var observation in ReadWorkspaceObservations(identity, history, operation, profile, task))
            {
                bool validTerminal = operation.Action == "continue_owned_task" && observation.Method == "turn/start"
                        && observation.Code is "turn_lifecycle_observed" or "read_reconciliation"
                    || operation.Action == "interrupt_turn" && observation.Method == "turn/interrupt"
                        && observation.Code is "interruption_observed" or "turn_ended_without_interruption";
                if (!validTerminal || operation.Action == "interrupt_turn" && operation.Task.TurnId != observation.TurnId
                    || observation.Phase != operation.Stage.ToString()) continue;
                result.Add(new(profile.Id, task, observation.TurnId, observation.ObservedAt, observation.Reconciliation));
            }
        }
        return result;
    }
    private static string? Field(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private WorkspaceLocation Location(ConfiguredOwnedProfile profile, string cwd)
        => new(profile.Configuration.Connection.Endpoint.HostId, profile.Configuration.ExpectedPlatformOs, cwd);
    internal string WorkspaceOwnerScope => WorkspaceOwner(Identity);
    private static string WorkspaceOwner(ConversationIdentity identity)
        => "owned-owner/" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
    internal void RefreshWorkspace()
    {
        if (workspace == null) return;
        workspace.Refresh();
        foreach (var task in Tasks())
            ObserveWorkspaceTask(task, reconciliation: false);
    }
    private void ObserveWorkspaceTask(OwnedRuntimeTask task, bool reconciliation)
    {
        lock (sync)
        {
            if (task.ObservedAt == null && acknowledgedEmptyTasks.Contains(task.Handle.Identity.Key)) return;
            if (task.ObservedAt != null) acknowledgedEmptyTasks.Remove(task.Handle.Identity.Key);
        }
        workspace?.Observe(new(task.Handle.Identity, Location(profiles[task.ConfigurationId], task.Snapshot.Cwd),
            task.Snapshot.CurrentTurnId, WorkspaceState(task.Snapshot.State), task.ObservedAt ?? task.Handle.Creation.CreatedAt,
            task.ObservedAt != null, task.ObservedAt != null ? WorkspaceEvidenceKind.LiveBackend : WorkspaceEvidenceKind.CreationOnly,
            reconciliation, WorkspaceOwnerScope));
    }
    private static WorkspaceTaskState WorkspaceState(OwnedTaskState state) => state switch
    {
        OwnedTaskState.Idle => WorkspaceTaskState.Idle, OwnedTaskState.Running => WorkspaceTaskState.Running,
        OwnedTaskState.WaitingApproval => WorkspaceTaskState.WaitingApproval, OwnedTaskState.Completed => WorkspaceTaskState.Completed,
        OwnedTaskState.Failed => WorkspaceTaskState.Failed, OwnedTaskState.Interrupted => WorkspaceTaskState.Interrupted,
        _ => WorkspaceTaskState.Unknown
    };
    internal WorkspaceAdmission ReserveWorkspace(string configurationId, Guid operationId, OwnedRuntimeTask? task)
    {
        if (workspace == null) return new(true, "workspace_compatibility_guard");
        RefreshWorkspace();
        return workspace.TryReserve("owned/" + operationId.ToString("N"), task?.Handle.Identity,
            Location(profiles[configurationId], task?.Snapshot.Cwd ?? profiles[configurationId].Creation.Cwd), WorkspaceOwnerScope);
    }
    internal bool CanAmendWorkspace(OwnedRuntimeTask task, string turnId)
    {
        RefreshWorkspace();
        return workspace == null || workspace.CanAmend(task.Handle.Identity, turnId, Location(profiles[task.ConfigurationId], task.Snapshot.Cwd), WorkspaceOwnerScope);
    }
    internal WorkspaceAdmission CheckCreateWorkspace(string configurationId, WorkspaceReservation? reservation = null)
    {
        RefreshWorkspace();
        return workspace?.CheckStart(null, Location(profiles[configurationId], profiles[configurationId].Creation.Cwd), reservation, WorkspaceOwnerScope)
            ?? new(true, "workspace_compatibility_guard");
    }
    internal bool CanCreateWorkspace(string configurationId) => CheckCreateWorkspace(configurationId).Allowed;

    internal async Task<OwnedCatalogue> ReadCatalogueAsync(CancellationToken ct)
    {
        if (!Active) throw new OperationCanceledException("owned_binding_changed");
        var facts = new List<OwnedCatalogueTask>();
        var unavailable = new List<string>();
        foreach (var profile in profiles.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<OwnedCreationFact> records;
                if (LoadedSession(profile.Id) is { } session)
                    records = await session.Store.ReadCreationFactsAsync(clock(), ct).ConfigureAwait(false);
                else
                {
                    string path = ProvenancePath(profile.Id);
                    if (!File.Exists(path)) continue;
                    await using var store = new OwnedProvenanceStore(profile.Configuration,
                        new FileOwnedProvenanceStorage(path, readOnly: true), new BindingProtector(Identity, profile.Id));
                    records = await store.ReadCreationFactsAsync(clock(), ct).ConfigureAwait(false);
                }
                facts.AddRange(records.Select(record => new OwnedCatalogueTask(profile.Id, record)));
            }
            catch (OperationCanceledException) { throw; }
            catch { unavailable.Add(profile.Id); } // No damaged proof is partially adopted or exposed.
        }
        if (!Active) throw new OperationCanceledException("owned_binding_changed");
        return new(facts.ToArray(), unavailable.ToArray());
    }

    internal OwnedQuerySnapshot SaveQuerySnapshot(string query, string target, string? turnId, int pageSize,
        IEnumerable<JsonElement> pages)
    {
        var immutable = pages.Select(p => p.Clone()).ToImmutableArray();
        if (immutable.Length is < 1 or > 1024 || immutable.Sum(p => (long)p.GetRawText().Length) > 2 * 1024 * 1024)
            throw new InvalidOperationException("owned_read_snapshot_capacity");
        lock (sync)
        {
            if (!Active) throw new OperationCanceledException("owned_binding_changed");
            foreach (string key in querySnapshots.Where(p => p.Value.ExpiresAt <= clock()).Select(p => p.Key).ToArray())
                querySnapshots.Remove(key);
            while (querySnapshots.Count >= 8)
                querySnapshots.Remove(querySnapshots.Values.OrderBy(p => p.CapturedAt).First().Id);
            var snapshot = new OwnedQuerySnapshot(Guid.NewGuid().ToString("N"), query, target, turnId, pageSize,
                clock(), clock().AddMinutes(10), immutable);
            querySnapshots.Add(snapshot.Id, snapshot);
            if (query == "read_owned_report") { currentReport = snapshot.Id; currentReportPage = 1; }
            return snapshot;
        }
    }

    internal OwnedQuerySnapshot? FindQuerySnapshot(string id, string query, string target, string? turnId, int? pageSize)
    {
        lock (sync)
        {
            if (!Active || !querySnapshots.TryGetValue(id, out var snapshot) || snapshot.ExpiresAt <= clock()
                || snapshot.Query != query || snapshot.Target != target || snapshot.TurnId != turnId
                || pageSize != null && pageSize != snapshot.PageSize) return null;
            return snapshot;
        }
    }

    internal string ReadReportPage(string id, int page)
    {
        lock (sync)
        {
            if (!Active || !querySnapshots.TryGetValue(id, out var snapshot)
                || snapshot.Query != "read_owned_report" || snapshot.ExpiresAt <= clock())
                return "这份报告的阅读记录已过期或因重启清除。请重新查询原任务的报告。";
            if (page < 1 || page > snapshot.Pages.Length)
                return $"这份报告共有 {snapshot.Pages.Length} 页，请使用范围内的页码。";
            currentReport = id; currentReportPage = page;
            return OwnedReportText.Render(snapshot.Pages[page - 1], page, snapshot.Pages.Length, snapshot.Id);
        }
    }
    internal string ReadCurrentReportPage(int? page = null)
    {
        lock (sync)
            return currentReport == null ? "还没有正在阅读的报告，请先查询任务结果。"
                : ReadReportPage(currentReport, page ?? currentReportPage + 1);
    }

    internal async Task<Session> EnsureSessionAsync(string configurationId, string method, bool readOnly, CancellationToken ct)
    {
        if (!Active || !profiles.TryGetValue(configurationId, out var profile)) throw new InvalidOperationException("owned_profile_or_binding_unavailable");
        if (profile.Mode == OwnedProfileMode.Disabled) throw new InvalidOperationException("owned_control_disabled");
        if (readOnly && LoadedSession(configurationId) is { } loaded && loaded.Client.IsConnected) return loaded;
        if (!readOnly && WriteFailure(configurationId, method) is { } denied) throw new InvalidOperationException(denied);
        if (readOnly && profile.Mode != OwnedProfileMode.ReadOnly
            && OwnedVerification.CapabilityFailure(profile.Configuration, "thread/read", clock()) != null)
            throw new InvalidOperationException("owned_read_connection_not_explicitly_configured");
        await openGates[configurationId].WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Active) throw new OperationCanceledException("owned_binding_changed");
            if (LoadedSession(configurationId) is { } existing)
            {
                if (readOnly && !existing.Client.IsConnected)
                {
                    var replacement = await connect(profile, ct).ConfigureAwait(false);
                    await existing.Client.ReconnectForReadAsync(replacement, ct).ConfigureAwait(false);
                    lock (sync) existing.Observed.Clear();
                }
                return existing;
            }
            if (!readOnly && WriteFailure(configurationId, method) is { } currentDenied) throw new InvalidOperationException(currentDenied);
            string path = ProvenancePath(configurationId);
            bool hasPrevious = File.Exists(path);
            if (readOnly && !hasPrevious) throw new InvalidOperationException("owned_provenance_not_loaded");
            Directory.CreateDirectory(dataDirectory);
            var store = new OwnedProvenanceStore(profile.Configuration, new FileOwnedProvenanceStorage(path, profile.Mode == OwnedProfileMode.ReadOnly),
                new BindingProtector(Identity, configurationId));
            OwnedAppServerClient? client = null;
            IJsonRpcConnection? wire = null;
            try
            {
                // Validate every historical proof before a connection is considered. Discovery is
                // entirely local; malformed/configuration-mismatched evidence cannot trigger RPC.
                if (hasPrevious)
                    await store.ReadCreationFactsAsync(clock(), ct).ConfigureAwait(false);
                wire = await connect(profile, ct).ConfigureAwait(false);
                client = new(profile.Configuration, wire, clock, store);
                await client.InitializeAsync(ct).ConfigureAwait(false);
                var restored = hasPrevious || readOnly ? await client.RestoreReadOnlyAsync(ct).ConfigureAwait(false) : [];
                var session = new Session(profile, client, store);
                foreach (var task in restored) session.Tasks.Add(task.Identity.Key, task);
                lock (sync)
                {
                    if (!Active) throw new OperationCanceledException("owned_binding_changed");
                    sessions.Add(configurationId, session);
                    session.Pump = System.Threading.Tasks.Task.Run(() => ObserveAsync(session));
                }
                return session;
            }
            catch
            {
                if (client != null) await client.DisposeAsync().ConfigureAwait(false);
                else if (wire != null) await wire.DisposeAsync().ConfigureAwait(false);
                await store.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { openGates[configurationId].Release(); }
    }

    internal string ProvenancePath(string configurationId) => ProvenancePath(Identity, configurationId);
    private string ProvenancePath(ConversationIdentity identity, string configurationId) => Path.Combine(dataDirectory,
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Identity = identity, configurationId }))) + ".owned");
    internal void RegisterDispatch(OperationRecord operation)
    {
        lock (sync)
        {
            if (!Active || failure != null || operation.Identity != Identity || operation.Stage != OperationStage.Dispatching
                || !dispatched.TryAdd(operation.Id, operation)) throw new InvalidOperationException("owned_dispatch_not_current_or_already_attempted");
            if (operation.Task is { } target && operation.Action is "continue_owned_task" or "steer_turn" or "respond_approval")
                foreach (var task in Tasks().Where(t => t.Handle.Identity.ThreadId == target.ThreadId
                    && t.Handle.Identity.HostId == target.HostId && t.Handle.Identity.ProfileId == target.ProfileId))
                    acknowledgedEmptyTasks.Remove(task.Handle.Identity.Key);
        }
    }
    internal void Adopt(Session session, OwnedTaskHandle task)
    {
        lock (sync)
        {
            session.Tasks.Add(task.Identity.Key, task);
            session.Observed[task.Identity.Key] = clock();
        }
        if (FindTask(session.Profile.Id, task.Identity.ThreadId) is { } adopted) ObserveWorkspaceTask(adopted, false);
    }
    internal void Observed(Session session, OwnedTaskHandle task) { lock (sync) session.Observed[task.Identity.Key] = clock(); }
    internal IDisposable ReserveDirectory(OwnedRuntimeTask target)
    {
        // The adapter already acquired the shared atomic reservation before task mutation.
        if (workspace != null) return new Scope(() => { });
        lock (sync)
        {
            string key = DirectoryKey(target);
            if (!WorkingDirectoryAvailable(target))
                throw new InvalidOperationException("owned_working_directory_busy_or_unknown");
            startingDirectories.Add(key);
            return new Scope(() => { lock (sync) startingDirectories.Remove(key); });
        }
    }
    internal WorkspaceAdmission CheckWorkingDirectory(OwnedRuntimeTask target, WorkspaceReservation? reservation = null)
    {
        if (workspace != null)
        {
            RefreshWorkspace();
            return workspace.CheckStart(target.Handle.Identity, Location(profiles[target.ConfigurationId], target.Snapshot.Cwd), reservation, WorkspaceOwnerScope);
        }
        lock (sync)
        {
            string key = DirectoryKey(target);
            bool available = !startingDirectories.Contains(key) && !Tasks().Any(other => other.Handle != target.Handle
                && DirectoryKey(other) == key
                && other.Snapshot.State is OwnedTaskState.Accepted or OwnedTaskState.Running or OwnedTaskState.WaitingApproval or OwnedTaskState.Unknown);
            return new(available, available ? "workspace_available" : "workspace_busy_or_unknown");
        }
    }
    internal bool WorkingDirectoryAvailable(OwnedRuntimeTask target) => CheckWorkingDirectory(target).Allowed;
    private string DirectoryKey(OwnedRuntimeTask task)
        // Different Codex profiles on one host still write the same filesystem directory.
        => task.Handle.Identity.HostId + "\n" + (profiles[task.ConfigurationId].Configuration.ExpectedPlatformOs == "windows"
            ? task.Snapshot.Cwd.ToUpperInvariant() : task.Snapshot.Cwd);

    // This is an observation of the already loaded backend, never authority to answer an approval.
    // Call again after any await (including the actual channel gate). In particular, provenance
    // discovery must not open a connection or make an old token current again.
    internal bool TryValidateRequiredDecision(ConversationEvent notice, out PendingApproval current, out string reason)
    {
        lock (sync)
        {
            current = null!;
            reason = "owned_approval_binding_inactive";
            if (!Active || notice.Identity != Identity) return false;
            reason = "owned_approval_notice_mismatch";
            if (notice.Source != ConversationSource.Notification || notice.RequiredDecision is not { } proof
                || notice.Task?.TurnId is null || proof.ConnectionEpoch == Guid.Empty
                || string.IsNullOrWhiteSpace(proof.ProviderScope) || string.IsNullOrWhiteSpace(proof.DecisionToken)
                || notice.ExternalEventKey != ApprovalNotificationKey(proof.ProviderScope, proof.DecisionToken)) return false;
            reason = "owned_approval_expired";
            if (proof.ExpiresAt <= clock()) return false;
            reason = "owned_approval_session_not_loaded";
            if (!sessions.TryGetValue(proof.ProviderScope, out var session)) return false;
            var approval = session.Client.PendingApprovals().SingleOrDefault(a => a.Token == proof.DecisionToken);
            reason = "owned_approval_not_pending";
            if (approval == null) return false;
            reason = "owned_approval_context_mismatch";
            if (approval.ConnectionEpoch != proof.ConnectionEpoch || approval.ExpiresAt != proof.ExpiresAt
                || notice.Task != ApprovalTask(approval)) return false;
            reason = WriteFailure(proof.ProviderScope, approval.Method) ?? "";
            if (reason.Length > 0) return false;
            reason = "owned_approval_task_not_current";
            if (!session.Tasks.TryGetValue(approval.Task.Key, out var task)
                || task.Access is not (OwnedTaskAccess.CreatedThisSession or OwnedTaskAccess.ResumedThisSession)) return false;
            var snapshot = session.Client.Snapshot(task);
            if (snapshot.Identity != approval.Task || snapshot.CurrentTurnId != approval.TurnId
                || snapshot.ProjectId != approval.ProjectId || snapshot.Cwd != approval.Cwd
                || snapshot.State is not (OwnedTaskState.Running or OwnedTaskState.WaitingApproval)) return false;
            // Control events have their own lock. Re-read after the other observations so a
            // consumed/resolved request cannot be returned from an earlier immutable snapshot.
            reason = "owned_approval_no_longer_pending";
            if (!session.Client.PendingApprovals().Any(a => a == approval)) return false;
            current = approval;
            reason = "";
            return true;
        }
    }

    private static TaskReference ApprovalTask(PendingApproval approval)
        => new(approval.Task.HostId, approval.Task.ThreadId, approval.TurnId, approval.Task.ProfileId);

    internal async Task<bool> TryBindPresentedApprovalAsync(string configurationId, string token,
        Guid notificationEventId, CancellationToken ct = default)
    {
        var snapshot = await Ledger.ReadConversationAsync(Identity, ct).ConfigureAwait(false);
        var notice = snapshot.Events.SingleOrDefault(e => e.Id == notificationEventId);
        var segments = snapshot.Outbox.Where(o => o.NotificationEventId == notificationEventId).ToArray();
        lock (sync)
        {
            if (notice?.RequiredDecision is not { } proof || proof.ProviderScope != configurationId || proof.DecisionToken != token
                || notice.Revision < (snapshot.Context?.StartRevision ?? 0)
                || segments.Length == 0 || segments.Any(o => o.Stage != OutboxStage.Accepted)
                || !TryValidateRequiredDecision(notice, out var approval, out _) || !IsFocused(approval)
                || snapshot.Context is { } context && !ContextFocusMatches(context, approval))
            {
                presented.Remove(notificationEventId);
                return false;
            }
            presented[notificationEventId] = new(Identity, notificationEventId, configurationId, approval, approval.FocusRevision, notice);
            return true;
        }
    }

    // Retains the explicit binding API's failure contract for callers that require a binding.
    // Background observation uses TryBind/TryRecord: ordinary expiry is not a delivery fault.
    internal async Task BindPresentedApprovalAsync(string configurationId, string token, Guid notificationEventId, CancellationToken ct = default)
    {
        if (!await TryBindPresentedApprovalAsync(configurationId, token, notificationEventId, ct).ConfigureAwait(false))
            throw new InvalidOperationException("owned_approval_notification_not_current_and_accepted_for_exact_task");
    }

    internal async Task<bool> TryRecordPendingApprovalAsync(ConversationEvent notice, CancellationToken ct = default)
    {
        if (notice.RequiredDecision is not { } proof
            || !await TryBindPresentedApprovalAsync(proof.ProviderScope, proof.DecisionToken, notice.Id, ct).ConfigureAwait(false)) return false;
        PendingApprovalContext? recorded = null;
        bool retained = false;
        try
        {
            var context = await Ledger.ReadConversationContextAsync(Identity, ct).ConfigureAwait(false);
            if (notice.Revision < context.StartRevision || !TryValidateRequiredDecision(notice, out var approval, out _)
                || !IsFocused(approval) || !ContextFocusMatches(context, approval)) return false;
            string requestId = OwnedControlToolAdapter.ApprovalReference(proof.ProviderScope, approval);
            recorded = await Ledger.RecordPendingApprovalAsync(Identity, notice.Id, requestId, ApprovalTask(approval),
                approval.ConnectionEpoch, approval.ExpiresAt, context.Epoch, ct).ConfigureAwait(false);
            var after = await Ledger.ReadConversationContextAsync(Identity, ct).ConfigureAwait(false);
            retained = after.Epoch == context.Epoch && after.StartRevision <= notice.Revision && after.PendingApproval?.Id == recorded.Id
                && TryValidateRequiredDecision(notice, out var latest, out _) && IsFocused(latest)
                && ContextFocusMatches(after, latest)
                && recorded.RequestId == OwnedControlToolAdapter.ApprovalReference(proof.ProviderScope, latest)
                && recorded.Task == ApprovalTask(latest) && recorded.BackendEpoch == latest.ConnectionEpoch
                && recorded.ExpiresAt == latest.ExpiresAt;
        }
        catch (LedgerException error) when (error.Code == LedgerErrors.StaleRevision) { return false; }
        catch (ArgumentException) when (!TryValidateRequiredDecision(notice, out _, out _)) { return false; }
        finally
        {
            // Only remove our exact slot. A new request/identity must never be cleared by a late
            // completion from an older observation. Do not cancel this cleanup during shutdown.
            if (!retained || !TryValidateRequiredDecision(notice, out var latest, out _) || !IsFocused(latest))
            {
                retained = false;
                lock (sync) presented.Remove(notice.Id);
                if (recorded != null)
                    await Ledger.ResolvePendingApprovalAsync(Identity, recorded.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        return retained;
    }

    internal static bool ContextFocusMatches(ConversationContext context, PendingApproval approval)
        => context.Focus is not { } focus || focus.Task.HostId == approval.Task.HostId
            && focus.Task.ThreadId == approval.Task.ThreadId && focus.Task.ProfileId == approval.Task.ProfileId;
    internal static string ApprovalNotificationKey(string configurationId, string token)
        => "owned-approval/" + configurationId + "/" + token;
    internal PresentedOwnedApproval? Presented(Guid eventId)
    {
        lock (sync)
        {
            if (!presented.TryGetValue(eventId, out var shown)) return null;
            if (TryValidateRequiredDecision(shown.Notice, out var current, out _)
                && current == shown.Request && IsFocused(current)) return shown;
            presented.Remove(eventId);
            return null;
        }
    }
    internal void ClearFocus()
    {
        lock (sync)
        {
            presented.Clear();
            focusedTask = null;
            focusedRevision++;
            foreach (var session in sessions.Values) session.Client.SetFocus(null);
        }
    }
    internal long SetFocus(string configurationId, string threadId)
    {
        var target = FindTask(configurationId, threadId) ?? throw new InvalidOperationException("owned_task_not_loaded");
        var session = LoadedSession(configurationId)!;
        lock (sync)
        {
            if (focusedTask == target.Handle.Identity) return focusedRevision;
            presented.Clear();
            foreach (var other in sessions.Values.Where(s => s != session)) other.Client.SetFocus(null);
            focusedRevision = session.Client.SetFocus(target.Handle);
            focusedTask = target.Handle.Identity;
            return focusedRevision;
        }
    }
    internal bool IsFocused(PendingApproval approval)
    { lock (sync) return approval.Task == focusedTask && approval.FocusRevision == focusedRevision; }

    private async Task ObserveAsync(Session session)
    {
        try
        {
            await foreach (var observation in session.Client.ReadObservationsAsync().ConfigureAwait(false))
            {
                lock (sync)
                    if (observation.Task != null) session.Observed[observation.Task.Key] = clock();
                OperationRecord? operation = null;
                lock (sync) if (observation.OperationId is { } id) dispatched.TryGetValue(id, out operation);
                if (operation != null && observation.State != ControlState.Dispatching)
                {
                    var current = (await Ledger.ReadConversationAsync(Identity).ConfigureAwait(false)).Operations.Single(o => o.Id == operation.Id);
                    // Ordinary late events do not resolve an operation already recorded Unknown by an outer timeout.
                    if (current.Stage != OperationStage.Unknown || observation.Reconciliation || observation.State == ControlState.Unknown)
                        await Ledger.RecordOperationObservationAsync(Identity, operation.Id, new(
                            Stage(observation.State), JsonSerializer.Serialize(new { source = "owned_app_server",
                                configurationId = session.Profile.Id, configurationDigest = OwnedVerification.ConfigurationDigest(session.Profile.Configuration),
                                endpoint = observation.Endpoint, operationId = operation.Id, observedAt = observation.ObservedAt,
                                phase = observation.State.ToString(),
                                observation.Code, observation.Method, observedTask = observation.Task, observedTurnId = observation.TurnId,
                                observation.Sequence, observation.Reconciliation }),
                            operation.TargetKey, operation.Task, observation.Reconciliation)).ConfigureAwait(false);
                }
                if (workspace != null && observation.Task != null && FindTask(session.Profile.Id, observation.Task.ThreadId) is { } task)
                {
                    if (operation != null && observation.State == ControlState.Unknown && !string.IsNullOrWhiteSpace(observation.TurnId))
                        workspace.UnknownTurn(WorkspaceOwnerScope, observation.Task, observation.TurnId);
                    if (observation.Method == "turn/start" && observation.OperationId is { } operationId)
                    {
                        string claim = "owned/" + operationId.ToString("N");
                        if (observation.State == ControlState.Unknown) workspace.Unknown(claim);
                        else if (observation.State == ControlState.CancelledBeforeDispatch) workspace.ReleaseNotDispatched(claim);
                        else if (observation.State != ControlState.Dispatching && !string.IsNullOrWhiteSpace(observation.TurnId))
                            workspace.Accepted(claim, observation.Task, observation.TurnId);
                    }
                    // Only actual turn observations (or explicit reads) can release an execution.
                    bool terminalEvidence = observation.Code is "turn_lifecycle_observed" or "read_reconciliation"
                        or "read_snapshot" or "interruption_observed" or "turn_observed_operation_still_unknown";
                    if (terminalEvidence || task.Snapshot.State is not (OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted))
                        ObserveWorkspaceTask(task, observation.Reconciliation || observation.Method == "thread/read");
                }
                var commitmentObservation = await CommitmentObservationAsync(session, observation, true, CancellationToken.None).ConfigureAwait(false);
                if (!events.Writer.TryWrite(new(session.Profile.Id, observation, commitmentObservation)))
                    throw new InvalidOperationException("owned_event_consumer_overflow");
            }
        }
        catch { lock (sync) failure = "owned_event_persistence_or_consumer_failed"; }
    }
    internal static OperationStage Stage(ControlState state) => state switch
    {
        ControlState.Accepted => OperationStage.Accepted, ControlState.Running => OperationStage.Running,
        ControlState.Completed => OperationStage.Completed, ControlState.Failed => OperationStage.Failed,
        ControlState.Interrupted => OperationStage.Interrupted, _ => OperationStage.Unknown
    };
    internal IDisposable Enter()
    {
        lock (sync)
        {
            if (!Active) throw new InvalidOperationException("owned_binding_inactive");
            if (inFlight++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Scope(() => { lock (sync) if (--inFlight == 0) idle.TrySetResult(); });
        }
    }
    internal CancellationToken StopToken => lifetime.Token;
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (stopped != null) return new(stopped);
            stopping = true; lifetime.Cancel();
            stopped = StopCoreAsync(idle.Task);
            return new(stopped);
        }
    }
    private async Task StopCoreAsync(Task drained)
    {
        await drained.ConfigureAwait(false);
        Session[] current; lock (sync) current = sessions.Values.ToArray();
        foreach (var session in current)
        {
            await session.Client.DisposeAsync().ConfigureAwait(false);
            await session.Pump.ConfigureAwait(false);
            await session.Store.DisposeAsync().ConfigureAwait(false);
        }
        events.Writer.TryComplete();
        lifetime.Dispose();
    }
    private static TaskCompletionSource Completed()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    private sealed class Scope(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    private sealed class BindingProtector(ConversationIdentity identity, string profile) : IOwnedProvenanceProtector
    {
        private readonly WindowsStateProtector protector = new();
        private readonly string binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { identity, profile })));
        public string Scheme => "windows-dpapi-owned-binding/v1";
        private byte[] Purpose(ReadOnlySpan<byte> purpose) => Encoding.UTF8.GetBytes("CodexAssistant.Owned/v1/" + binding + "/"
            + Convert.ToBase64String(purpose));
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose) => protector.Protect(plaintext, Purpose(purpose));
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose) => protector.Unprotect(ciphertext, Purpose(purpose));
    }
}
