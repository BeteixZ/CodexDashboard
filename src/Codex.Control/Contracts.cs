using System.Collections.Immutable;
using System.Text.Json;
using CodexDashboard;

namespace Codex.Control;

public enum OwnedBackend { LocalStdio, SshStdio, SharedRemoteSocket }
public enum VerificationKind { SyntheticReplay, RuntimeAcceptance }
public enum ControlState { Dispatching, Accepted, Running, Completed, Failed, Interrupted, Unknown, CancelledBeforeDispatch }
public enum OwnedTaskState { Idle, Accepted, Running, WaitingApproval, Completed, Failed, Interrupted, Unknown, Archived }
public enum ApprovalDecision { Accept, AcceptForSession, Decline, Cancel }
public enum ApprovalState { Pending, Consumed, Resolved, Expired, Invalidated }
public enum OwnedTaskAccess { CreatedThisSession, RestoredReadOnly, ResumedThisSession }

public sealed record ConnectionIdentity(HostEndpointIdentity Endpoint, OwnedBackend Backend, bool Synthetic);

/// <summary>Trusted host configuration, never tool/model arguments. No runtime evidence ships enabled.</summary>
public sealed record RuntimeVerification(
    ConnectionIdentity Connection, string Version, string PlatformOs, string ConfigurationDigest,
    ImmutableHashSet<string> Methods, VerificationKind Kind, string EvidenceReference,
    DateTimeOffset VerifiedAt, DateTimeOffset ExpiresAt);

public sealed record OwnedServerConfiguration
{
    public required ConnectionIdentity Connection { get; init; }
    public required string ExpectedVersion { get; init; }
    public required string ExpectedPlatformOs { get; init; }
    public required string ExpectedCodexHome { get; init; }
    public required ImmutableDictionary<string, string> Projects { get; init; }
    public required ImmutableHashSet<string> Models { get; init; }
    public RuntimeVerification? Verification { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record CreateOwnedTask(string ProjectId, string Cwd, string Model, string ApprovalPolicy, string Sandbox);
public sealed record ControlReceipt(Guid OperationId, ControlState State, CanonicalTaskIdentity? Task, string? TurnId, string Code);
public sealed record ControlObservation(long Sequence, DateTimeOffset ObservedAt, Guid? OperationId,
    HostEndpointIdentity Endpoint, CanonicalTaskIdentity? Task, string? TurnId, string Method, ControlState State, string Code,
    bool Reconciliation = false, Guid ConnectionEpoch = default);
public sealed record OwnedTaskSnapshot(CanonicalTaskIdentity Identity, string ProjectId, string Cwd, string Model,
    string ApprovalPolicy, string Sandbox, string? CurrentTurnId, OwnedTaskState State, long Revision);

/// <summary>Only durably recorded creation or verified protected provenance can establish a handle.</summary>
public sealed class OwnedTaskHandle
{
    internal OwnedTaskHandle(OwnedTaskSnapshot snapshot, OwnedCreationEvidence creation, OwnedTaskAccess access)
    { Snapshot = snapshot; Creation = creation; Access = access; }
    internal OwnedTaskSnapshot Snapshot { get; set; }
    public CanonicalTaskIdentity Identity => Snapshot.Identity;
    public OwnedCreationEvidence Creation { get; }
    public OwnedTaskAccess Access { get; internal set; }
}

public sealed record PendingApproval(string Token, JsonElement RequestId, Guid ConnectionEpoch,
    CanonicalTaskIdentity Task, string TurnId, string ProjectId, string Cwd, string ItemId, string Method,
    string Summary, ImmutableArray<ApprovalDecision> Decisions, DateTimeOffset ExpiresAt,
    long FocusRevision, ApprovalState State, JsonElement? OperationDetails);

public sealed record ReconciliationRead(OwnedTaskSnapshot Task, JsonElement Thread, string EvidenceSource,
    bool WritesRemainBlocked, ControlObservation? Observation = null);
public sealed record OwnedReport(CanonicalTaskIdentity Task, string TurnId, string FinalText,
    ImmutableArray<JsonElement> Commands, ImmutableArray<JsonElement> FileChanges,
    string EvidenceSource, bool IndependentlyVerified);

public sealed class ControlException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record RpcIncoming(string Method, JsonElement Parameters, JsonElement? RequestId = null);
public sealed class RpcTransportException(bool mayHaveBeenWritten, string code, Exception? inner = null)
    : IOException(code, inner)
{
    public bool MayHaveBeenWritten { get; } = mayHaveBeenWritten;
    public string Code { get; } = code;
}
public sealed class RpcRemoteException(int code) : IOException("The server rejected the JSON-RPC request.")
{
    public int Code { get; } = code;
}

/// <summary>Callbacks are serialized by the receive pump; consumers must not synchronously issue RPC in them.</summary>
public interface IJsonRpcConnection : IAsyncDisposable
{
    string? ProjectSourceIdentity => null;
    ConnectionIdentity Identity { get; }
    bool IsConnected { get; }
    event Action<RpcIncoming>? Incoming;
    event Action? Disconnected;
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken);
    bool SupportsGuardedRequests => false;
    Task<JsonElement> RequestGuardedAsync(string method, object parameters,
        Func<CancellationToken, Task<bool>> beforeWrite, CancellationToken cancellationToken)
        => throw new RpcTransportException(false, "guarded_write_not_supported");
    Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken);
    Task RespondAsync(JsonElement requestId, object result, CancellationToken cancellationToken);
}
