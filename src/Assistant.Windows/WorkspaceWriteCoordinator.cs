using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexDashboard;

internal enum WorkspaceTaskState { Idle, Running, WaitingApproval, Completed, Failed, Interrupted, Unknown }
internal enum WorkspaceEvidenceKind { CreationOnly, Collector, LiveBackend }
internal sealed record WorkspaceLocation(string HostId, string Platform, string Cwd);
internal sealed record WorkspaceScope(string HostId, string? Path, bool HostWide);
internal sealed record WorkspaceTaskEvidence(CanonicalTaskIdentity Task, WorkspaceLocation Location,
    string? TurnId, WorkspaceTaskState State, DateTimeOffset ObservedAt, bool Fresh,
    WorkspaceEvidenceKind Kind, bool Reconciliation = false, string? OwnerScope = null);
internal sealed record WorkspaceClaimEvidence(string ClaimId, CanonicalTaskIdentity? Task,
    WorkspaceLocation Location, string? TurnId, DateTimeOffset DispatchedAt, bool RequireReconciliation = true, string? OwnerScope = null);
internal sealed record WorkspaceAdmission(bool Allowed, string Code, WorkspaceReservation? Reservation = null);
internal sealed record WorkspaceBlocker(string ClaimId, string HostId, string? Path, string State);
internal sealed record WorkspaceConcurrencySnapshot(int? Limit, int Occupied, int Unknown, int Reserved, bool RecoveryReady);

internal interface IWorkspacePathResolver
{
    WorkspaceScope? Resolve(WorkspaceLocation location);
}

/// <summary>
/// No network discovery. Local directory ancestors must exist and contain no reparse points.
/// Remote paths are not resolved with Windows rules: absent independently verified canonical
/// path evidence, all work on that exact configured host shares one conservative scope.
/// Profile IDs never partition the filesystem. Host aliases are not guessed from path text.
/// </summary>
internal sealed class WorkspacePathResolver : IWorkspacePathResolver
{
    public WorkspaceScope? Resolve(WorkspaceLocation location)
    {
        try
        {
            _ = new HostEndpointIdentity(location.HostId);
            if (location.Cwd.Length is < 1 or > 4096 || location.Cwd.Any(char.IsControl)) return null;
            if (location.HostId != "local")
            {
                if (location.Platform is not ("linux" or "macos") || !location.Cwd.StartsWith('/')
                    || location.Cwd.Contains('\\') || location.Cwd.Split('/').Any(p => p is "." or "..")) return null;
                return new(location.HostId, null, true);
            }
            if (location.Platform != "windows" || location.Cwd.Length < 3 || !char.IsAsciiLetter(location.Cwd[0])
                || location.Cwd[1] != ':' || location.Cwd[2] is not ('\\' or '/')
                || location.Cwd[2..].Contains(':') || location.Cwd.Split('\\', '/').Any(p => p is "." or ".."
                    || p.EndsWith('.') || p.EndsWith(' '))) return null;
            if (!OperatingSystem.IsWindows()) return null;
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Cwd));
            if (!Directory.Exists(path)) return null;
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            // Use the filesystem's normalized name and volume identity. GetFullPath alone does
            // not unify an 8.3 name, a SUBST drive or two drive letters for the same volume.
            // UNC/device paths and aliases we cannot resolve are refused, never guessed.
            using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & (uint)FileAttributes.Directory) == 0
                || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
            var name = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 1); // VOLUME_NAME_GUID
            if (length == 0 || length >= name.Capacity) return null;
            string canonical = name.ToString();
            if (!canonical.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                || canonical.IndexOf(@"}\", StringComparison.Ordinal) < 0 || canonical.Any(char.IsControl)) return null;
            return new(location.HostId, canonical.TrimEnd('\\').ToUpperInvariant(), false);
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

/// <summary>
/// Host-lifetime admission guard shared by every transport. This is not a business ledger or an
/// OS mutex. The host must import unresolved Core/queue/provenance facts before completing each
/// registered recovery source. A reservation survives acknowledgements, caller cancellation,
/// identity changes and shutdown; only known non-dispatch or exact terminal evidence releases it.
/// A new process reconstructs those facts from the original durable stores before admitting work.
/// </summary>
internal sealed class WorkspaceWriteCoordinator
{
    private sealed record Claim(string Id, CanonicalTaskIdentity? Task, WorkspaceScope Scope,
        string? TurnId, DateTimeOffset AdmittedAt, string State, string? OwnerScope);
    private sealed record Observation(WorkspaceTaskEvidence Evidence, WorkspaceScope Scope);
    private readonly object sync = new();
    private readonly IWorkspacePathResolver resolver;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<string> pendingRecovery;
    private readonly Dictionary<string, Claim> claims = new(StringComparer.Ordinal);
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<CanonicalTaskIdentity, Observation> observations = new();
    private string? recoveryFailure;
    private int? maxConcurrentExecutions;
    internal WorkspaceWriteCoordinator(IWorkspacePathResolver? resolver = null,
        IEnumerable<string>? recoverySources = null, Func<DateTimeOffset>? clock = null, int? maxConcurrentExecutions = null)
    {
        this.resolver = resolver ?? new WorkspacePathResolver();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        pendingRecovery = new(recoverySources ?? [], StringComparer.Ordinal);
        SetLimit(maxConcurrentExecutions);
    }
    internal void SetLimit(int? limit)
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (sync) maxConcurrentExecutions = limit;
    }
    internal WorkspaceConcurrencySnapshot Snapshot()
    {
        lock (sync)
        {
            var groups = ExecutionGroups().ToArray();
            return new(maxConcurrentExecutions, groups.Length, groups.Count(g => g.Any(c => c.State == "Unknown")),
                groups.Count(g => g.All(c => c.State == "Reserved")), pendingRecovery.Count == 0 && recoveryFailure == null);
        }
    }
    // Only exact backend task/turn bindings merge claims. Unbound attempts remain separate,
    // and observations alone (including creation-only provenance) are not execution claims.
    private IEnumerable<IGrouping<(string? OwnerScope, CanonicalTaskIdentity? Task, string? Turn, string? UnboundId), Claim>> ExecutionGroups()
        => claims.Values.GroupBy(c => (c.OwnerScope, c.Task, c.TurnId,
            !string.IsNullOrWhiteSpace(c.OwnerScope) && c.Task != null && !string.IsNullOrWhiteSpace(c.TurnId) ? null : c.Id));
    internal bool Ready { get { lock (sync) return pendingRecovery.Count == 0 && recoveryFailure == null; } }
    internal Action? RefreshExternalEvidence { get; set; }
    internal void Refresh()
    {
        try { RefreshExternalEvidence?.Invoke(); }
        catch { FailRecovery("workspace_external_evidence_unavailable"); }
    }
    internal void RequireRecovery(string source) { lock (sync) pendingRecovery.Add(source); }
    internal void CompleteRecovery(string source) { lock (sync) pendingRecovery.Remove(source); }
    internal void FailRecovery(string code) { lock (sync) recoveryFailure ??= code; }
    internal IReadOnlyList<WorkspaceBlocker> Blockers()
    {
        lock (sync) return claims.Values.Select(c => new WorkspaceBlocker(c.Id, c.Scope.HostId, c.Scope.Path, c.State)).ToArray();
    }
    internal void RestoreUnresolved(WorkspaceClaimEvidence evidence)
    {
        var scope = ResolveOrHostWide(evidence.Location);
        lock (sync)
        {
            if (scope == null || string.IsNullOrWhiteSpace(evidence.ClaimId) || evidence.DispatchedAt == default)
            { recoveryFailure ??= "workspace_recovery_identity_unknown"; return; }
            if (claims.TryGetValue(evidence.ClaimId, out var previous))
            {
                if (previous.Task != evidence.Task || previous.Scope != scope || previous.OwnerScope != evidence.OwnerScope
                    || previous.TurnId != evidence.TurnId && evidence.TurnId != null)
                    recoveryFailure ??= "workspace_recovery_conflict";
                return;
            }
            // Completed and rejected operation IDs cannot be recycled by a later source import.
            if (attempted.Contains(evidence.ClaimId)) return;
            attempted.Add(evidence.ClaimId);
            claims.Add(evidence.ClaimId, new(evidence.ClaimId, evidence.Task, scope, evidence.TurnId,
                evidence.DispatchedAt, !evidence.RequireReconciliation && evidence.TurnId != null
                    && !string.IsNullOrWhiteSpace(evidence.OwnerScope) ? "Accepted" : "Unknown", evidence.OwnerScope));
        }
    }
    internal void Observe(WorkspaceTaskEvidence evidence)
    {
        var scope = ResolveOrHostWide(evidence.Location);
        lock (sync)
        {
            if (scope == null || evidence.Task.HostId != evidence.Location.HostId || evidence.ObservedAt == default)
            { recoveryFailure ??= "workspace_observation_scope_unknown"; return; }
            if (observations.TryGetValue(evidence.Task, out var prior)
                && (evidence.Kind == WorkspaceEvidenceKind.CreationOnly
                    || evidence.ObservedAt < prior.Evidence.ObservedAt)) return;
            observations[evidence.Task] = new(evidence, scope);
            if (!IsFresh(evidence) || !Terminal(evidence.State) || string.IsNullOrWhiteSpace(evidence.TurnId)
                || string.IsNullOrWhiteSpace(evidence.OwnerScope)) return;
            foreach (var claim in claims.Values.Where(c => c.Task == evidence.Task && c.TurnId == evidence.TurnId
                && c.OwnerScope == evidence.OwnerScope
                && c.AdmittedAt <= evidence.ObservedAt && (c.State != "Unknown" || evidence.Reconciliation)).ToArray())
                claims.Remove(claim.Id);
        }
    }
    internal WorkspaceAdmission TryReserve(string claimId, CanonicalTaskIdentity? task, WorkspaceLocation location, string? ownerScope = null)
    {
        var scope = resolver.Resolve(location);
        lock (sync)
        {
            if (recoveryFailure != null || pendingRecovery.Count != 0) return new(false, "workspace_recovery_required");
            if (scope == null || task != null && task.HostId != location.HostId) return new(false, "workspace_scope_unverified");
            if (string.IsNullOrWhiteSpace(ownerScope)) return new(false, "workspace_owner_unverified");
            if (string.IsNullOrWhiteSpace(claimId) || attempted.Contains(claimId)) return new(false, "workspace_operation_already_attempted");
            if (Blocked(scope, task, null, allowCurrentTurn: false)) return new(false, "workspace_busy_or_unknown");
            if (maxConcurrentExecutions is { } limit && ExecutionGroups().Count() >= limit)
                return new(false, "workspace_global_capacity");
            if (attempted.Count >= 100000) return new(false, "workspace_capacity_requires_restart");
            attempted.Add(claimId);
            claims.Add(claimId, new(claimId, task, scope, null, clock(), "Reserved", ownerScope));
            return new(true, "workspace_reserved", new(this, claimId));
        }
    }
    internal WorkspaceAdmission CheckStart(CanonicalTaskIdentity? task, WorkspaceLocation location, WorkspaceReservation? reservation = null, string? ownerScope = null)
    {
        var scope = resolver.Resolve(location);
        lock (sync)
        {
            if (recoveryFailure != null || pendingRecovery.Count != 0) return new(false, "workspace_recovery_required");
            if (scope == null || task != null && task.HostId != location.HostId) return new(false, "workspace_scope_unverified");
            string? ownClaim = null;
            if (reservation != null)
            {
                if (!ReferenceEquals(reservation.Owner, this) || !claims.TryGetValue(reservation.Id, out var claim)
                    || claim.State != "Reserved" || claim.Task != task || claim.Scope != scope
                    || string.IsNullOrWhiteSpace(ownerScope) || claim.OwnerScope != ownerScope)
                    return new(false, "workspace_reservation_not_current");
                ownClaim = claim.Id;
            }
            if (Blocked(scope, task, null, false, ownClaim)) return new(false, "workspace_busy_or_unknown");
            // A previously admitted reservation keeps its slot when the user lowers the limit.
            if (ownClaim == null && maxConcurrentExecutions is { } limit && ExecutionGroups().Count() >= limit)
                return new(false, "workspace_global_capacity");
            return new(true, "workspace_available");
        }
    }
    internal bool CanStart(CanonicalTaskIdentity? task, WorkspaceLocation location)
        => CheckStart(task, location).Allowed;
    internal bool CanAmend(CanonicalTaskIdentity task, string turnId, WorkspaceLocation location, string? ownerScope = null)
    {
        var scope = resolver.Resolve(location);
        lock (sync)
        {
            if (recoveryFailure != null || pendingRecovery.Count != 0 || scope == null || string.IsNullOrWhiteSpace(turnId)
                || string.IsNullOrWhiteSpace(ownerScope)
                || !observations.TryGetValue(task, out var current) || !IsFresh(current.Evidence)
                || current.Evidence.OwnerScope != ownerScope
                || current.Evidence.TurnId != turnId || current.Evidence.State is not (WorkspaceTaskState.Running or WorkspaceTaskState.WaitingApproval))
                return false;
            return !Blocked(scope, task, turnId, true, ownerScope: ownerScope);
        }
    }
    private bool Blocked(WorkspaceScope scope, CanonicalTaskIdentity? target, string? turn, bool allowCurrentTurn, string? exceptClaim = null, string? ownerScope = null)
    {
        if (claims.Values.Any(c => c.Id != exceptClaim && Overlaps(c.Scope, scope) && !(allowCurrentTurn && c.Task == target
            && c.OwnerScope == ownerScope && ownerScope != null && c.TurnId == turn && c.State is "Accepted" or "Running"))) return true;
        foreach (var observed in observations.Values.Where(o => Overlaps(o.Scope, scope)))
        {
            var evidence = observed.Evidence;
            bool busy = !IsFresh(evidence) || evidence.Kind == WorkspaceEvidenceKind.CreationOnly
                || evidence.State is WorkspaceTaskState.Running or WorkspaceTaskState.WaitingApproval or WorkspaceTaskState.Unknown;
            if (busy && !(allowCurrentTurn && evidence.OwnerScope == ownerScope && ownerScope != null
                && evidence.Task == target && evidence.TurnId == turn && IsFresh(evidence)
                && evidence.State is WorkspaceTaskState.Running or WorkspaceTaskState.WaitingApproval)) return true;
        }
        return false;
    }
    private bool IsFresh(WorkspaceTaskEvidence evidence) => evidence.Fresh
        && evidence.ObservedAt <= clock().AddSeconds(30) && clock() - evidence.ObservedAt <= TimeSpan.FromMinutes(2);
    private static bool Terminal(WorkspaceTaskState state)
        => state is WorkspaceTaskState.Completed or WorkspaceTaskState.Failed or WorkspaceTaskState.Interrupted;
    private WorkspaceScope? ResolveOrHostWide(WorkspaceLocation location)
    {
        var resolved = resolver.Resolve(location);
        if (resolved != null) return resolved;
        try { _ = new HostEndpointIdentity(location.HostId); return new(location.HostId, null, true); }
        catch { return null; }
    }
    private static bool Overlaps(WorkspaceScope a, WorkspaceScope b)
    {
        if (a.HostId != b.HostId) return false;
        if (a.HostWide || b.HostWide || a.Path == null || b.Path == null) return true;
        return a.Path == b.Path || a.Path.StartsWith(b.Path.TrimEnd('\\') + "\\", StringComparison.Ordinal)
            || b.Path.StartsWith(a.Path.TrimEnd('\\') + "\\", StringComparison.Ordinal);
    }
    internal void Accepted(string id, CanonicalTaskIdentity? task, string? turn)
    {
        lock (sync)
        {
            if (!claims.TryGetValue(id, out var claim)) return; // An exact early terminal may already have released it.
            if (claim.Task != null && task != null && claim.Task != task)
            { recoveryFailure ??= "workspace_receipt_identity_mismatch"; return; }
            var accepted = claim with { Task = task ?? claim.Task, TurnId = string.IsNullOrWhiteSpace(turn) ? claim.TurnId : turn,
                State = claim.State == "Unknown" ? "Unknown" : "Accepted" };
            claims[id] = accepted;
            if (accepted.Task != null && accepted.TurnId != null && observations.TryGetValue(accepted.Task, out var prior)
                && prior.Evidence.TurnId == accepted.TurnId && Terminal(prior.Evidence.State) && IsFresh(prior.Evidence)
                && accepted.OwnerScope != null && prior.Evidence.OwnerScope == accepted.OwnerScope
                && prior.Evidence.ObservedAt >= accepted.AdmittedAt && (accepted.State != "Unknown" || prior.Evidence.Reconciliation))
                claims.Remove(id);
        }
    }
    internal void Unknown(string id)
    {
        lock (sync) if (claims.TryGetValue(id, out var claim)) claims[id] = claim with { State = "Unknown" };
    }
    internal void UnknownTurn(string ownerScope, CanonicalTaskIdentity task, string turnId)
    {
        lock (sync)
            foreach (var claim in claims.Values.Where(c => c.OwnerScope == ownerScope && c.Task == task && c.TurnId == turnId).ToArray())
                claims[claim.Id] = claim with { State = "Unknown" };
    }
    internal void ReleaseNotDispatched(string id)
    {
        lock (sync) if (claims.TryGetValue(id, out var claim) && claim.State == "Reserved")
        { claims.Remove(id); attempted.Remove(id); }
    }
}

internal sealed class WorkspaceReservation(WorkspaceWriteCoordinator coordinator, string id)
{
    internal WorkspaceWriteCoordinator Owner => coordinator;
    internal string Id => id;
    internal void Accepted(CanonicalTaskIdentity? task, string? turnId) => coordinator.Accepted(id, task, turnId);
    internal void Unknown() => coordinator.Unknown(id);
    // Only the transport caller knows that no message was written, or that create returned an
    // empty thread without submitting work. Disposal/cancellation is never a release operation.
    internal void NotDispatched() => coordinator.ReleaseNotDispatched(id);
}
