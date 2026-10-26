using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.Migration;

/// <summary>
/// Offline, explicit-directory transfer. Never starts processes, decrypts credentials, sends a
/// message or dispatches a command. A denied/missing receiver guard produces a plan-only result.
/// </summary>
public sealed class MigrationEngine(ILegacyReceiverGuard? guard = null, IMigrationFaultInjector? faults = null)
{
    private readonly ILegacyReceiverGuard guard = guard ?? new DenyMigrationGuard();
    private const string JournalName = "journal.json";
    private const long MaxTotalBytes = 64 * 1024 * 1024;
    private static readonly Regex ProfileFile = new(
        @"\Aassistant/profiles/[a-fA-F0-9]{64}/(AGENTS\.md|memory\.state)\z", RegexOptions.CultureInvariant);
    private static readonly Regex AssistantDataFile = new(
        @"\A(?:history-archive/(?:catalogue\.state|archive\.owner|[a-fA-F0-9]{32}\.volume)|owned-state/[a-fA-F0-9]{64}\.owned|(?:owned|existing)-acceptance/[A-Za-z0-9_-]+\.(?:owned|existing)-acceptance|attachments/[a-fA-F0-9]{64}/[a-fA-F0-9]{64}\.state)\z", RegexOptions.CultureInvariant);
    private static bool IsPayload(string path) => path is "settings.json" or "chatgpt.credentials" or "task-control.state"
        or "conversations.state" or "owned-control.json" or "existing-control.json"
        || ProfileFile.IsMatch(path) || AssistantDataFile.IsMatch(path);

    public MigrationPlan Plan(MigrationRequest request)
    {
        request = SafePaths.Normalize(request);
        var files = Inventory(request.SourceDirectory);
        long bytes = files.Sum(p => new FileInfo(SafePaths.Under(request.SourceDirectory, p)).Length);
        if (bytes > MaxTotalBytes) throw new InvalidOperationException("migration_too_large");
        return new(request, files, bytes,
            "Both receivers stopped, old/new writers excluded, and credential protection/application identity confirmed.");
    }

    public Task<MigrationResult> MigrateAsync(MigrationRequest request, CancellationToken cancellation = default)
        => ExecuteAsync(request, MigrationDirection.Forward, cancellation);

    public Task<MigrationResult> RollbackAsync(MigrationRequest request, CancellationToken cancellation = default)
        => ExecuteAsync(request, MigrationDirection.Rollback, cancellation);

    /// <summary>Reads bounded journal/marker metadata only. Never opens protected payloads or creates a directory.</summary>
    public MigrationInspection Inspect(MigrationRequest request)
    {
        bool transactionExists = false;
        try
        {
            request = SafePaths.Normalize(request);
            transactionExists = File.Exists(SafePaths.Under(request.TransactionDirectory, JournalName));
            var journal = LoadJournal(request);
            bool activated = File.Exists(SafePaths.Under(request.TargetDirectory, MigrationStartup.ActivationName));
            if (journal == null)
                return new(false, null, activated, false, RollbackAssessment.NoTransaction, "no_transaction");
            bool startup = journal.Phase == MigrationPhase.Completed
                && MarkerMatches(request.TargetDirectory, journal.Id, "ready");
            var rollback = activated ? RollbackAssessment.RequiresReconciliation : journal.Phase switch
            {
                MigrationPhase.Completed or MigrationPhase.Published => RollbackAssessment.NeedsReceiverGuardAndIntegrityCheck,
                MigrationPhase.RollbackDisablingTarget or MigrationPhase.RollbackRestoringSource => RollbackAssessment.ResumeRollbackWithGuard,
                MigrationPhase.RolledBack => RollbackAssessment.AlreadyRolledBack,
                _ => RollbackAssessment.FinishForwardRecovery
            };
            return new(true, journal.Phase, activated, startup, rollback, "metadata_only",
                journal.SourceFiles.Count, journal.TargetFiles.Count);
        }
        catch (Exception ex) when (ex is MigrationFailure or IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            string code = ex switch
            {
                MigrationFailure failure => failure.Code,
                JsonException => "invalid_json_preserved",
                UnauthorizedAccessException => "access_denied",
                IOException => "io_failure",
                _ => "invalid_input_preserved"
            };
            return new(transactionExists, null, false, false, RollbackAssessment.Unverified, code);
        }
    }

    private async Task<MigrationResult> ExecuteAsync(MigrationRequest request, MigrationDirection direction,
        CancellationToken cancellation)
    {
        MigrationJournal? journal = null;
        try
        {
            request = SafePaths.Normalize(request);
            await using var lease = await guard.TryAcquireAsync(request, direction, cancellation);
            if (lease is not { LegacyStoppedAndWritesBlocked: true, AssistantStoppedAndWritesBlocked: true,
                CredentialScopeCompatible: true } || !await lease.RevalidateAsync(cancellation))
                return new(MigrationPhase.Planned, false, false, "receiver_guard_required");

            // The guard excludes noncooperating legacy processes. File locks additionally exclude
            // simultaneous cooperating migration transactions, even with different journal dirs.
            Directory.CreateDirectory(request.TransactionDirectory);
            Directory.CreateDirectory(request.TargetDirectory);
            using var transactionLock = Lock(request.TransactionDirectory);
            using var sourceLock = Lock(request.SourceDirectory);
            using var targetLock = Lock(request.TargetDirectory);
            journal = LoadJournal(request);
            if (direction == MigrationDirection.Rollback)
            {
                if (journal == null) return new(MigrationPhase.Planned, false, false, "journal_required");
                await RollbackCoreAsync(journal, lease, cancellation);
            }
            else
            {
                journal ??= Begin(request);
                await ForwardCoreAsync(journal, lease, cancellation);
            }
            return new(journal.Phase, true, journal.Phase == MigrationPhase.Completed, "ok", journal.TargetFiles.Count);
        }
        catch (Exception ex) when (ex is MigrationFailure or IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            // Never include exception messages, settings, protected payloads or token material.
            string code = ex switch
            {
                MigrationFailure failure => failure.Code,
                OperationCanceledException => "cancelled_recoverable",
                JsonException => "invalid_json_preserved",
                UnauthorizedAccessException => "access_denied_recoverable",
                IOException => "io_failure_recoverable",
                _ => "invalid_input_preserved"
            };
            return new(journal?.Phase ?? MigrationPhase.Planned, false, false, code, journal?.TargetFiles.Count ?? 0);
        }
    }

    private static FileStream Lock(string directory)
    {
        string path = SafePaths.Under(directory, MigrationStartup.LockName);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private MigrationJournal Begin(MigrationRequest request)
    {
        if (SafePaths.Files(request.TransactionDirectory).Any(p => Path.GetFileName(p) != MigrationStartup.LockName))
            throw new MigrationFailure("transaction_directory_not_empty");
        if (PayloadFiles(request.TargetDirectory).Any()
            || File.Exists(SafePaths.Under(request.TargetDirectory, MigrationStartup.MarkerName)))
            throw new MigrationFailure("target_not_empty");
        var files = Inventory(request.SourceDirectory);
        if (!files.Contains("settings.json")) throw new MigrationFailure("settings_missing");
        var journal = new MigrationJournal
        {
            Version = 1, Id = Guid.NewGuid().ToString("N"), Request = request, Phase = MigrationPhase.Preparing,
            SourceFiles = files.Select(p => Stamp(request.SourceDirectory, p)).ToList()
        };
        if (journal.SourceFiles.Sum(f => f.Length) > MaxTotalBytes) throw new MigrationFailure("migration_too_large");
        // Validate the settings before persisting a transaction or copying any payload.
        _ = SettingsTransfer.Assistant(SafePaths.Read(SafePaths.Under(request.SourceDirectory, "settings.json"), 1024 * 1024));
        Save(journal);
        faults?.At("journal_created");
        return journal;
    }

    private async Task ForwardCoreAsync(MigrationJournal journal, IReceiverExclusionLease lease,
        CancellationToken cancellation)
    {
        var request = journal.Request;
        if (journal.Phase is MigrationPhase.RolledBack or MigrationPhase.RollbackDisablingTarget or MigrationPhase.RollbackRestoringSource)
            throw new MigrationFailure("rollback_transaction_cannot_migrate");
        if (journal.Phase == MigrationPhase.Completed)
        {
            // Idempotent completion must not copy or overwrite data the new assistant has since added.
            VerifyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            if (!MarkerMatches(request.TargetDirectory, journal.Id, "ready")) throw new MigrationFailure("activation_marker_mismatch");
            return;
        }
        await CheckLeaseAsync(lease, cancellation);
        string backup = SafePaths.Under(request.TransactionDirectory, "backup");
        string stage = SafePaths.Under(request.TransactionDirectory, "staging");
        if (journal.Phase == MigrationPhase.Preparing)
        {
            VerifySet(request.SourceDirectory, journal.SourceFiles, Inventory(request.SourceDirectory));
            foreach (var file in journal.SourceFiles)
            {
                await CheckLeaseAsync(lease, cancellation);
                CopyVerified(request.SourceDirectory, backup, file, overwriteIdenticalOnly: true);
                faults?.At("backup_file:" + file.RelativePath);
            }
            byte[] original = SafePaths.Read(SafePaths.Under(backup, "settings.json"), 1024 * 1024);
            byte[] assistant = SettingsTransfer.Assistant(original, request.PauseTargetServices);
            journal.SanitizedSourceHash = Hash(SettingsTransfer.Disable(original));
            journal.SanitizedTargetHash = Hash(SettingsTransfer.Disable(assistant));
            foreach (var file in journal.SourceFiles)
            {
                await CheckLeaseAsync(lease, cancellation);
                if (file.RelativePath == "settings.json") SafePaths.AtomicWrite(SafePaths.Under(stage, "settings.json"), assistant);
                else CopyVerified(backup, stage, file, overwriteIdenticalOnly: true);
                faults?.At("staging_file:" + file.RelativePath);
            }
            journal.TargetFiles = journal.SourceFiles.Select(f => Stamp(stage, f.RelativePath)).ToList();
            Change(journal, MigrationPhase.Prepared);
        }
        if (journal.Phase == MigrationPhase.Prepared)
        {
            VerifySet(backup, journal.SourceFiles, journal.SourceFiles.Select(f => f.RelativePath));
            VerifySet(stage, journal.TargetFiles, journal.TargetFiles.Select(f => f.RelativePath));
            VerifySet(request.SourceDirectory, journal.SourceFiles, Inventory(request.SourceDirectory));
            if (PayloadFiles(request.TargetDirectory).Any()) throw new MigrationFailure("target_changed_before_transfer");
            await CheckLeaseAsync(lease, cancellation);
            WriteMarker(request.TargetDirectory, journal.Id, "pending");
            faults?.At("target_blocked");
            Change(journal, MigrationPhase.DisablingSource);
        }
        if (journal.Phase == MigrationPhase.DisablingSource)
        {
            await CheckLeaseAsync(lease, cancellation);
            Disable(request.SourceDirectory, backup, journal.SourceFiles, journal.SanitizedSourceHash);
            faults?.At("source_disabled");
            Change(journal, MigrationPhase.SourceDisabled);
        }
        if (journal.Phase == MigrationPhase.SourceDisabled)
        {
            VerifyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            Change(journal, MigrationPhase.Publishing);
        }
        if (journal.Phase == MigrationPhase.Publishing)
        {
            await CheckLeaseAsync(lease, cancellation);
            VerifyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            RequireMarker(request.TargetDirectory, journal.Id, "pending");
            var expected = journal.TargetFiles.Select(f => f.RelativePath).ToHashSet(SafePaths.Comparer);
            if (PayloadFiles(request.TargetDirectory).Any(p => !expected.Contains(p)))
                throw new MigrationFailure("unexpected_target_data");
            foreach (var file in journal.TargetFiles)
            {
                await CheckLeaseAsync(lease, cancellation);
                CopyVerified(stage, request.TargetDirectory, file, overwriteIdenticalOnly: true);
                faults?.At("published_file:" + file.RelativePath);
            }
            Change(journal, MigrationPhase.Published);
        }
        if (journal.Phase == MigrationPhase.Published)
        {
            await CheckLeaseAsync(lease, cancellation);
            VerifyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            VerifySet(request.TargetDirectory, journal.TargetFiles, PayloadFiles(request.TargetDirectory));
            if (!MarkerMatches(request.TargetDirectory, journal.Id, "pending")
                && !MarkerMatches(request.TargetDirectory, journal.Id, "ready"))
                throw new MigrationFailure("activation_marker_mismatch");
            WriteMarker(request.TargetDirectory, journal.Id, "ready");
            faults?.At("activation_released");
            Change(journal, MigrationPhase.Completed);
        }
    }

    private async Task RollbackCoreAsync(MigrationJournal journal, IReceiverExclusionLease lease,
        CancellationToken cancellation)
    {
        var request = journal.Request;
        string backup = SafePaths.Under(request.TransactionDirectory, "backup");
        string stage = SafePaths.Under(request.TransactionDirectory, "staging");
        if (journal.Phase == MigrationPhase.RolledBack) return;
        if (journal.Phase is not (MigrationPhase.Completed or MigrationPhase.Published
            or MigrationPhase.RollbackDisablingTarget or MigrationPhase.RollbackRestoringSource))
            throw new MigrationFailure("finish_forward_recovery_before_rollback");
        await CheckLeaseAsync(lease, cancellation);
        if (journal.Phase is MigrationPhase.Completed or MigrationPhase.Published)
        {
            // If it has ever run, files alone cannot prove no external side effect occurred.
            if (File.Exists(SafePaths.Under(request.TargetDirectory, MigrationStartup.ActivationName)))
                throw new MigrationFailure("rollback_requires_reconciliation_after_activation");
            VerifyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            VerifySet(request.TargetDirectory, journal.TargetFiles, PayloadFiles(request.TargetDirectory));
            VerifySet(backup, journal.SourceFiles, journal.SourceFiles.Select(f => f.RelativePath));
            WriteMarker(request.TargetDirectory, journal.Id, "rollback-pending");
            faults?.At("rollback_target_blocked");
            Change(journal, MigrationPhase.RollbackDisablingTarget);
        }
        if (journal.Phase == MigrationPhase.RollbackDisablingTarget)
        {
            await CheckLeaseAsync(lease, cancellation);
            RequireMarker(request.TargetDirectory, journal.Id, "rollback-pending");
            // The staging copy is immutable and retains the pre-rollback target, including secrets.
            VerifySet(stage, journal.TargetFiles, journal.TargetFiles.Select(f => f.RelativePath));
            Disable(request.TargetDirectory, stage, journal.TargetFiles, journal.SanitizedTargetHash);
            faults?.At("rollback_target_disabled");
            Change(journal, MigrationPhase.RollbackRestoringSource);
        }
        if (journal.Phase == MigrationPhase.RollbackRestoringSource)
        {
            await CheckLeaseAsync(lease, cancellation);
            VerifyDisabled(request.TargetDirectory, journal.TargetFiles, journal.SanitizedTargetHash);
            VerifySet(backup, journal.SourceFiles, journal.SourceFiles.Select(f => f.RelativePath));
            VerifyPartiallyDisabled(request.SourceDirectory, journal.SourceFiles, journal.SanitizedSourceHash);
            // Restore settings last: old executables cannot poll until their credentials are restored.
            foreach (var file in journal.SourceFiles.OrderBy(f => f.RelativePath == "settings.json" ? 1 : 0))
            {
                await CheckLeaseAsync(lease, cancellation);
                var bytes = SafePaths.Read(SafePaths.Under(backup, file.RelativePath));
                SafePaths.AtomicWrite(SafePaths.Under(request.SourceDirectory, file.RelativePath), bytes);
                faults?.At("rollback_restored_file:" + file.RelativePath);
            }
            VerifySet(request.SourceDirectory, journal.SourceFiles, Inventory(request.SourceDirectory));
            WriteMarker(request.TargetDirectory, journal.Id, "rolled-back");
            Change(journal, MigrationPhase.RolledBack);
        }
    }

    private void Disable(string directory, string immutableCopy, List<FileStamp> files, string disabledHash)
    {
        VerifyPartiallyDisabled(directory, files, disabledHash);
        string settingsPath = SafePaths.Under(directory, "settings.json");
        byte[] original = SafePaths.Read(SafePaths.Under(immutableCopy, "settings.json"), 1024 * 1024);
        byte[] disabled = SettingsTransfer.Disable(original);
        if (Hash(disabled) != disabledHash) throw new MigrationFailure("backup_hash_mismatch");
        SafePaths.AtomicWrite(settingsPath, disabled);
        faults?.At("settings_disabled:written");
        var credential = files.SingleOrDefault(f => f.RelativePath == "chatgpt.credentials");
        if (credential != null)
        {
            string path = SafePaths.Under(directory, credential.RelativePath);
            if (File.Exists(path))
            {
                if (Stamp(directory, credential.RelativePath) != credential) throw new MigrationFailure("credential_changed");
                File.Delete(path); // The verified protected backup is already durable.
            }
        }
        VerifyDisabled(directory, files, disabledHash);
    }

    private static void VerifyPartiallyDisabled(string root, List<FileStamp> files, string disabledHash)
    {
        var expected = files.Select(f => f.RelativePath).ToHashSet(SafePaths.Comparer);
        if (Inventory(root).Any(f => !expected.Contains(f))) throw new MigrationFailure("source_inventory_changed");
        foreach (var file in files)
        {
            string path = SafePaths.Under(root, file.RelativePath);
            if (file.RelativePath == "chatgpt.credentials" && !File.Exists(path)) continue;
            var actual = Stamp(root, file.RelativePath);
            if (actual == file || (file.RelativePath == "settings.json" && actual.Sha256 == disabledHash)) continue;
            throw new MigrationFailure("payload_changed_requires_reconciliation");
        }
    }

    private static void VerifyDisabled(string root, List<FileStamp> files, string disabledHash)
    {
        VerifyPartiallyDisabled(root, files, disabledHash);
        if (Stamp(root, "settings.json").Sha256 != disabledHash
            || File.Exists(SafePaths.Under(root, "chatgpt.credentials")))
            throw new MigrationFailure("source_credentials_not_disabled");
    }

    private static async ValueTask CheckLeaseAsync(IReceiverExclusionLease lease, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!lease.LegacyStoppedAndWritesBlocked || !lease.AssistantStoppedAndWritesBlocked
            || !lease.CredentialScopeCompatible || !await lease.RevalidateAsync(cancellation))
            throw new MigrationFailure("receiver_exclusion_lost");
    }

    private static List<string> Inventory(string root)
    {
        var files = new List<string>();
        foreach (string name in new[] { "settings.json", "chatgpt.credentials", "task-control.state", "conversations.state", "owned-control.json", "existing-control.json" })
            if (File.Exists(SafePaths.Under(root, name))) files.Add(name);
        foreach (string folder in new[] { "assistant/profiles", "history-archive", "owned-state", "owned-acceptance", "existing-acceptance", "attachments" })
        {
            string profiles = SafePaths.Under(root, folder);
            if (!Directory.Exists(profiles)) continue;
            foreach (string path in SafePaths.Files(profiles))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (IsPayload(relative)) files.Add(relative);
                else if (folder != "assistant/profiles") throw new MigrationFailure("unrecognized_assistant_data_requires_review");
                if (files.Count > 2048) throw new MigrationFailure("too_many_files");
            }
        }
        return files.Order(SafePaths.Comparer).ToList();
    }

    private static IEnumerable<string> PayloadFiles(string root)
        => SafePaths.Files(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => p is not (MigrationStartup.MarkerName or MigrationStartup.LockName));

    private static FileStamp Stamp(string root, string relative)
    {
        byte[] bytes = SafePaths.Read(SafePaths.Under(root, relative));
        return new(relative, bytes.LongLength, Hash(bytes));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void VerifySet(string root, List<FileStamp> expected, IEnumerable<string> actualNames)
    {
        if (!actualNames.Order(SafePaths.Comparer).SequenceEqual(expected.Select(f => f.RelativePath).Order(SafePaths.Comparer), SafePaths.Comparer))
            throw new MigrationFailure("inventory_changed_requires_reconciliation");
        foreach (var file in expected)
            if (Stamp(root, file.RelativePath) != file) throw new MigrationFailure("payload_changed_requires_reconciliation");
    }

    private static void CopyVerified(string source, string destination, FileStamp expected, bool overwriteIdenticalOnly)
    {
        byte[] bytes = SafePaths.Read(SafePaths.Under(source, expected.RelativePath));
        if (bytes.LongLength != expected.Length || Hash(bytes) != expected.Sha256)
            throw new MigrationFailure("payload_changed_requires_reconciliation");
        string target = SafePaths.Under(destination, expected.RelativePath);
        if (File.Exists(target))
        {
            if (Stamp(destination, expected.RelativePath) == expected) return;
            if (overwriteIdenticalOnly) throw new MigrationFailure("existing_payload_mismatch");
        }
        SafePaths.AtomicWrite(target, bytes);
        if (Stamp(destination, expected.RelativePath) != expected) throw new MigrationFailure("copy_verification_failed");
    }

    private static MigrationJournal? LoadJournal(MigrationRequest request)
    {
        string path = SafePaths.Under(request.TransactionDirectory, JournalName);
        if (!File.Exists(path)) return null;
        byte[] journalBytes = SafePaths.Read(path, 2 * 1024 * 1024);
        _ = SettingsTransfer.Read(journalBytes);
        var journal = JsonSerializer.Deserialize<MigrationJournal>(journalBytes, SettingsTransfer.Json)
            ?? throw new MigrationFailure("invalid_journal");
        if (journal.Version != 1) throw new MigrationFailure("unsupported_journal_version");
        if (journal.Request != request || !Guid.TryParseExact(journal.Id, "N", out _))
            throw new MigrationFailure("journal_identity_mismatch");
        if (!Enum.IsDefined(journal.Phase) || journal.Phase == MigrationPhase.Planned
            || journal.SourceFiles == null || journal.TargetFiles == null
            || journal.SourceFiles.Count is 0 or > 2048 || journal.TargetFiles.Count > 2048
            || journal.SourceFiles.Any(f => f == null) || journal.TargetFiles.Any(f => f == null)
            || journal.SourceFiles.Count(f => f.RelativePath == "settings.json") != 1
            || journal.SourceFiles.Select(f => f.RelativePath).Distinct(SafePaths.Comparer).Count() != journal.SourceFiles.Count
            || journal.TargetFiles.Select(f => f.RelativePath).Distinct(SafePaths.Comparer).Count() != journal.TargetFiles.Count)
            throw new MigrationFailure("invalid_journal");
        if (journal.SourceFiles.Sum(f => f.Length) > MaxTotalBytes || journal.TargetFiles.Sum(f => f.Length) > MaxTotalBytes)
            throw new MigrationFailure("migration_too_large");
        if (journal.Phase != MigrationPhase.Preparing
            && !journal.SourceFiles.Select(f => f.RelativePath).Order(SafePaths.Comparer)
                .SequenceEqual(journal.TargetFiles.Select(f => f.RelativePath).Order(SafePaths.Comparer), SafePaths.Comparer))
            throw new MigrationFailure("invalid_journal_inventory");
        foreach (var file in journal.SourceFiles.Concat(journal.TargetFiles))
        {
            if (!IsPayload(file.RelativePath))
                throw new MigrationFailure("journal_path_not_allowlisted");
            if (file.Length is < 0 or > 16 * 1024 * 1024 || !Regex.IsMatch(file.Sha256, @"\A[A-F0-9]{64}\z"))
                throw new MigrationFailure("invalid_journal_stamp");
        }
        return journal;
    }

    private void Change(MigrationJournal journal, MigrationPhase phase)
    {
        journal.Phase = phase;
        Save(journal);
        faults?.At("phase:" + phase);
    }
    private static void Save(MigrationJournal journal)
        => SafePaths.AtomicWrite(SafePaths.Under(journal.Request.TransactionDirectory, JournalName),
            JsonSerializer.SerializeToUtf8Bytes(journal, SettingsTransfer.Json));

    private static bool MarkerMatches(string root, string id, string state)
    {
        string path = SafePaths.Under(root, MigrationStartup.MarkerName);
        if (!File.Exists(path)) return false;
        byte[] bytes = SafePaths.Read(path, 4096);
        _ = SettingsTransfer.Read(bytes);
        var marker = JsonSerializer.Deserialize<StartupMarker>(bytes);
        return marker is { Version: 1 } && marker.TransactionId == id && marker.State == state;
    }
    private static void RequireMarker(string root, string id, string state)
    {
        if (!MarkerMatches(root, id, state)) throw new MigrationFailure("activation_marker_mismatch");
    }
    private static void WriteMarker(string root, string id, string state)
        => SafePaths.AtomicWrite(SafePaths.Under(root, MigrationStartup.MarkerName),
            JsonSerializer.SerializeToUtf8Bytes(new StartupMarker(1, id, state)));
}
