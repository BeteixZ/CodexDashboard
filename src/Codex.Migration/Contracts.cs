namespace Codex.Migration;

public enum MigrationPhase
{
    Planned, Preparing, Prepared, DisablingSource, SourceDisabled, Publishing, Published, Completed,
    RollbackDisablingTarget, RollbackRestoringSource, RolledBack
}

public enum MigrationDirection { Forward, Rollback }

/// <summary>All directories must be explicitly selected, absolute, distinct and non-overlapping.</summary>
public sealed record MigrationRequest(string SourceDirectory, string TargetDirectory, string TransactionDirectory,
    bool PauseTargetServices = false);

public sealed record DataDirectoryCandidate(string Directory, bool Exists, bool HasSettings,
    bool HasCredentialStore, bool HasQueue, bool HasProfiles);

public sealed record MigrationPlan(MigrationRequest Request, IReadOnlyList<string> RelativeFiles,
    long TotalBytes, string RequiredGate);

public sealed record MigrationResult(MigrationPhase Phase, bool Succeeded, bool ActivationAllowed,
    string Code, int Files = 0);

public enum RollbackAssessment
{
    NoTransaction, FinishForwardRecovery, NeedsReceiverGuardAndIntegrityCheck,
    RequiresReconciliation, ResumeRollbackWithGuard, AlreadyRolledBack, Unverified
}

/// <summary>
/// Read-only transaction metadata, never an execution grant. Payload hashes and receiver
/// exclusion have NOT been verified. A false activation flag is not proof of no side effects.
/// </summary>
public sealed record MigrationInspection(bool TransactionExists, MigrationPhase? Phase,
    bool ActivationRecorded, bool StartupAllowed, RollbackAssessment Rollback,
    string Code, int SourceFiles = 0, int TargetFiles = 0);

/// <summary>
/// Implementations must stop BOTH old and new receivers and exclude their writers for the entire
/// lease, including old executables which do not honor a new mutex. A mutex alone is not proof.
/// This library ships no production implementation and defaults to denying execution.
/// </summary>
public interface ILegacyReceiverGuard
{
    ValueTask<IReceiverExclusionLease?> TryAcquireAsync(MigrationRequest request,
        MigrationDirection direction, CancellationToken cancellation);
}

public interface IReceiverExclusionLease : IAsyncDisposable
{
    /// <summary>True only after stopped processes cannot restore credentials from memory.</summary>
    bool LegacyStoppedAndWritesBlocked { get; }
    bool AssistantStoppedAndWritesBlocked { get; }
    /// <summary>Windows user/protection scope and OAuth application registration remain compatible.</summary>
    bool CredentialScopeCompatible { get; }
    ValueTask<bool> RevalidateAsync(CancellationToken cancellation);
}

public sealed class DenyMigrationGuard : ILegacyReceiverGuard
{
    public ValueTask<IReceiverExclusionLease?> TryAcquireAsync(MigrationRequest request,
        MigrationDirection direction, CancellationToken cancellation)
        => ValueTask.FromResult<IReceiverExclusionLease?>(null);
}

/// <summary>Test hook. Production callers should leave this unset.</summary>
public interface IMigrationFaultInjector
{
    void At(string point);
}

internal sealed record FileStamp(string RelativePath, long Length, string Sha256);

internal sealed class MigrationJournal
{
    public int Version { get; set; }
    public string Id { get; set; } = "";
    public MigrationRequest Request { get; set; } = new("", "", "");
    public MigrationPhase Phase { get; set; }
    public List<FileStamp> SourceFiles { get; set; } = [];
    public List<FileStamp> TargetFiles { get; set; } = [];
    public string SanitizedSourceHash { get; set; } = "";
    public string SanitizedTargetHash { get; set; } = "";
}

internal sealed record StartupMarker(int Version, string TransactionId, string State);
