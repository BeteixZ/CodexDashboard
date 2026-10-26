using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantLedgerInspection
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["--compare-recovery-reports", var currentPath, var candidatePath, var comparisonPath])
            {
                if (new[] { currentPath, candidatePath, comparisonPath }.Any(path => !Path.IsPathFullyQualified(path)))
                    throw new ArgumentException("Explicit absolute report paths required.");
                using var current = JsonDocument.Parse(await File.ReadAllTextAsync(currentPath));
                using var candidate = JsonDocument.Parse(await File.ReadAllTextAsync(candidatePath));
                var comparison = RecoveryComparison.Compare(current.RootElement, candidate.RootElement);
                await using var file = new FileStream(comparisonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(file, comparison, new JsonSerializerOptions { WriteIndented = true });
                return 0;
            }
            if (args is ["--verify-recovery-report", var copy, var reportPath])
            {
                bool matches = await VerifyAsync(copy, reportPath);
                Console.WriteLine(JsonSerializer.Serialize(new { snapshotMatches = matches, restoreAuthorized = false }));
                return matches ? 0 : 3;
            }
            if (args is not ["--inspect-ledger", var source, var output]) return 2;
            await WriteAsync(source, output);
            return 0;
        }
        catch (Exception error)
        {
            // Do not expose decrypted content or data paths in process diagnostics.
            Console.Error.WriteLine("Offline ledger inspection failed: " + error.GetType().Name);
            return 1;
        }
    }

    internal static async Task<bool> VerifyAsync(string source, string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new ArgumentException("Explicit absolute report path required.");
        await using var file = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var saved = await JsonDocument.ParseAsync(file);
        var report = saved.RootElement;
        if (report.GetProperty("version").GetInt32() != 6 || report.GetProperty("restoreAuthorized").GetBoolean()
            || !report.GetProperty("recoveryHoldPresent").GetBoolean())
            throw new InvalidDataException("A current held-copy inspection report is required.");
        var current = await ReadAsync(source, requireHold: true);
        var expected = report.GetProperty("snapshotFingerprint");
        var actual = current.GetProperty("snapshotFingerprint");
        return expected.GetProperty("algorithm").GetString() == actual.GetProperty("algorithm").GetString()
            && expected.GetProperty("Sha256").GetString() == actual.GetProperty("Sha256").GetString()
            && expected.GetProperty("FileCount").GetInt32() == actual.GetProperty("FileCount").GetInt32()
            && expected.GetProperty("Bytes").GetInt64() == actual.GetProperty("Bytes").GetInt64()
            && expected.GetProperty("excludes").EnumerateArray().Select(x => x.GetString())
                .SequenceEqual(actual.GetProperty("excludes").EnumerateArray().Select(x => x.GetString()));
    }

    internal static async Task WriteAsync(string source, string output)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException("Explicit absolute paths are required.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); output = Path.GetFullPath(output);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || output.Equals(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Report must be outside the copy.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(output)!); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are unsupported.");
        var report = await ReadAsync(source);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static async Task<JsonElement> ReadAsync(string source, bool requireHold = false)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Explicit absolute path required.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        foreach (var path in new[] { source })
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are unsupported.");
        using var lease = new FileStream(Path.Combine(source, ".codex-migration.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
        if (requireHold && !File.Exists(Path.Combine(source, Codex.Migration.MigrationStartup.RecoveryHoldName)))
            throw new InvalidOperationException("Recovery review requires a held copy.");
        if (new DirectoryInfo(source).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Any(item => (item.Attributes & FileAttributes.ReparsePoint) != 0)) throw new IOException("Linked data is unsupported.");
        var fingerprint = RecoverySnapshotFingerprint.Capture(source);
        await using var ledger = await AssistantLedgerStorage.OpenAsync(source, false, inspection: true);
        var conversations = new List<object>();
        var reconciliationRecords = new List<RecoveryRecord>();
        foreach (var identity in await ledger.ListConversationIdentitiesAsync())
        {
            var state = await ledger.ReadConversationAsync(identity);
            reconciliationRecords.AddRange(RecoveryComparison.Records(identity, state));
            conversations.Add(new
            {
                ordinal = conversations.Count + 1, revision = state.Revision,
                inboxCount = state.Inbox.Length, eventCount = state.Events.Length,
                generationCount = state.Generations.Length,
                operationStates = state.Operations.GroupBy(x => x.Stage).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                outboxStates = state.Outbox.GroupBy(x => x.Stage).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                operationsToReconcile = state.Operations.Where(x => x.Stage is OperationStage.Dispatching or OperationStage.Unknown
                    or OperationStage.Accepted or OperationStage.Running).Select(x => x.Id).ToArray(),
                outboxToReconcile = state.Outbox.Where(x => x.Stage is OutboxStage.Sending or OutboxStage.Unknown).Select(x => x.Id).ToArray(),
                pendingOutbox = state.Outbox.Count(x => x.Stage == OutboxStage.Pending),
                pendingWork = RecoveryPendingWork.Read(state)
            });
        }
        var memories = new List<object>();
        string profiles = Path.Combine(source, "assistant", "profiles");
        if (Directory.Exists(profiles))
            foreach (var directory in new DirectoryInfo(profiles).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                string path = Path.Combine(directory.FullName, "memory.state");
                if (!File.Exists(path)) continue;
                var memory = AssistantMemoryStore.ReadPersistedDocument(path, directory.Name);
                reconciliationRecords.Add(RecoveryComparison.Record("MemoryDocument", directory.Name, "document", memory));
                var references = memory.Entries.SelectMany(entry => entry.Metadata?.Events ?? []).ToArray();
                var referenceStates = references.Select(reference =>
                {
                    var evidence = ledger.ReadCommittedEventEvidence(reference.Identity, reference.EventId);
                    return evidence == null ? "missing_or_unavailable" :
                        new EpisodeSource(evidence.Event, evidence.Members).Reference == reference ? "fingerprint_matches" : "fingerprint_differs";
                }).GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
                memories.Add(new { ordinal = memories.Count + 1, formatVersion = memory.FormatVersion,
                    revision = memory.Revision, rulesRevision = memory.RulesRevision,
                    personalizationRevision = memory.PersonalizationRevision,
                    entryCount = memory.Entries.Count, revocationCount = memory.Revocations.Count,
                    invalidatedCount = memory.Entries.Count(e => e.Metadata?.InvalidatedAtUtc != null),
                    expiredCount = memory.Entries.Count(e => e.Metadata?.ExpiresAtUtc <= DateTimeOffset.UtcNow),
                    derivedCount = memory.Entries.Count(e => e.Metadata?.Dependencies.IsEmpty == false || e.Metadata?.Events.IsEmpty == false),
                    eventReferenceStates = referenceStates });
            }
        var report = new { version = 6, scope = "conversation-ledger-and-memory-documents", restoreAuthorized = false,
            snapshotFingerprint = new { algorithm = "sha256-relative-file-manifest-v1", fingerprint.Sha256,
                fingerprint.FileCount, fingerprint.Bytes, excludes = new[] { ".codex-migration.lock", "empty-directories" } },
            recoveryHoldPresent = File.Exists(Path.Combine(source, Codex.Migration.MigrationStartup.RecoveryHoldName)),
            inspectedAtUtc = DateTimeOffset.UtcNow, conversations, memories, reconciliationRecords,
            limitations = new[] { "No backend reconciliation performed.", "Credentials, rule text and other sidecar configuration were hashed as stored bytes, not decoded or validated.",
                "The snapshot fingerprint is not a signature, legacy-process exclusion proof or permission to restore.",
                "Event references compared with committed evidence including catch-up members; matching fingerprints do not establish current eligibility, profile ownership or external truth.",
                "Persisted states were not recovered; this report does not authorize replay or startup." } };
        return JsonSerializer.SerializeToElement(report);
    }
}
