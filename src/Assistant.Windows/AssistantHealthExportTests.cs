using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantHealthExportTests
{
    private sealed class DirectoryProbe(Func<string, AssistantHealthExport.DirectoryFact, AssistantHealthExport.DirectoryFact> transform)
        : AssistantHealthExport.IDirectoryProbe
    {
        private readonly AssistantHealthExport.NativeDirectoryProbe native = new();
        public AssistantHealthExport.IDirectoryLease Open(string path)
        {
            var opened = native.Open(path);
            try { return new Lease(opened, transform(path, opened.Fact)); }
            catch { opened.Dispose(); throw; }
        }
        private sealed class Lease(AssistantHealthExport.IDirectoryLease original, AssistantHealthExport.DirectoryFact fact)
            : AssistantHealthExport.IDirectoryLease
        { public AssistantHealthExport.DirectoryFact Fact => fact; public void Dispose() => original.Dispose(); }
    }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--assistant-health-export-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--assistant-health-export-test") throw new ArgumentException("new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_output_required");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "assistant-health-export-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(c => !c.Value), networkUsed = false, actualModelUsed = false,
                productionDataRead = false, checks }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var identity = new ConversationIdentity("PRIVATE_EXPORT_USER", "PRIVATE_EXPORT_BINDING", "PRIVATE_EXPORT_CONVERSATION");
        var snapshot = AssistantHealthTests.Sample(identity, @"C:\PRIVATE_EXPORT_DIRECTORY\PRIVATE_TASK_BODY_PRIVATE_TOKEN");
        string path = Path.Combine(directory, "actual-summary.json");
        var outcome = AssistantHealthExport.WriteNew(path, snapshot, () => true);
        byte[] bytes = File.ReadAllBytes(path); string json = Encoding.UTF8.GetString(bytes);
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        Check("export_writes_real_new_json_and_exact_top_level_whitelist", outcome == AssistantHealthExportResult.Saved
            && Keys(root, "formatVersion", "readAtUtc", "appVersion", "protocolVersion", "ledgerReaderSchemas", "channel", "model",
                "collection", "ledger", "concurrency", "statusServiceStarted"));
        Check("export_has_exact_nested_whitelists_no_hidden_source_objects",
            Keys(root.GetProperty("protocolVersion"), "major", "minor")
            && Keys(root.GetProperty("channel"), "state", "receiverRunning", "notificationQueueRunning", "receiverLeaseHeld", "stopping")
            && Keys(root.GetProperty("model"), "initialized", "enabled", "completedRequestsThisProcess", "lastCompletedAtUtc", "lastFailureAtUtc")
            && Keys(root.GetProperty("collection"), "configuredSources", "observedSources", "failedSources", "observedTasks", "lastCollectionAtUtc")
            && Keys(root.GetProperty("ledger"), "state", "waitingInputs", "inputDecisions", "generatingInputs",
                "rawPendingMessagesIncludingHistoricalAndCatchUpSources", "sendingMessages", "unknownMessages", "acceptedOperations",
                "runningOperations", "unknownOperations", "hasRuntimeError", "capacity")
            && Keys(root.GetProperty("ledger").GetProperty("capacity"), "scope", "logicalSnapshotBytes",
                "maximumSnapshotBytes", "journalSequence", "readOnly", "nearLimit")
            && Keys(root.GetProperty("concurrency"), "limit", "occupied", "unknown", "reserved", "recoveryReady"));
        Check("export_excludes_identity_paths_body_credentials_and_raw_exception_fields",
            !json.Contains("PRIVATE_", StringComparison.Ordinal) && !AllKeys(root).Any(key =>
                new[] { "identity", "userId", "bindingId", "conversationId", "taskId", "threadId", "messageId", "token", "body",
                    "dataDirectory", "path", "exception", "error", "detail", "summary", "settings" }.Contains(key, StringComparer.OrdinalIgnoreCase)));
        Check("export_preserves_snapshot_times_unknown_counts_and_reader_schema_meaning",
            root.GetProperty("readAtUtc").GetDateTimeOffset() == snapshot.ReadAt
            && root.GetProperty("collection").GetProperty("lastCollectionAtUtc").GetDateTimeOffset() == snapshot.LastCollectionAt
            && root.GetProperty("ledger").GetProperty("unknownMessages").GetInt32() == 2
            && root.GetProperty("ledger").GetProperty("unknownOperations").GetInt32() == 1
            && root.GetProperty("ledgerReaderSchemas").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual([1, 2, 3, 4, 5])
            && !AllKeys(root).Any(x => x.Contains("delivered", StringComparison.OrdinalIgnoreCase) || x == "currentLedgerSchema"));
        Check("capacity_export_has_only_explicit_numbers_scope_and_recovery_flags",
            root.GetProperty("ledger").GetProperty("capacity").GetProperty("logicalSnapshotBytes").GetInt32() == 7 * 1024 * 1024
            && root.GetProperty("ledger").GetProperty("capacity").GetProperty("maximumSnapshotBytes").GetInt32() == 8 * 1024 * 1024
            && root.GetProperty("ledger").GetProperty("capacity").GetProperty("journalSequence").GetInt64() == 123
            && root.GetProperty("ledger").GetProperty("capacity").GetProperty("nearLimit").GetBoolean()
            && root.GetProperty("ledger").GetProperty("capacity").GetProperty("scope").GetString() == "whole_ledger_validated_logical_snapshot");
        using var archiveCapacity = JsonDocument.Parse(AssistantHealthExport.Serialize(snapshot with
        { Ledger = snapshot.Ledger! with { Capacity = new(9 * 1048576, 64 * 1048576, 123, false, 1024, 8 * 1048576, new(3145728, 4, 2, 128)) } }));
        var manifest = archiveCapacity.RootElement.GetProperty("ledger").GetProperty("capacity").GetProperty("manifest");
        Check("archived_export_versions_manifest_numbers_without_identity_or_content", archiveCapacity.RootElement.GetProperty("formatVersion").GetInt32() == 2
            && root.GetProperty("formatVersion").GetInt32() == 1 && Keys(manifest, "scope", "bytes", "maximumBytes")
            && manifest.GetProperty("bytes").GetInt32() == 1024 && manifest.GetProperty("maximumBytes").GetInt32() == 8 * 1048576
            && !archiveCapacity.RootElement.GetRawText().Contains("PRIVATE_"));
        var archiveStorageFields = archiveCapacity.RootElement.GetProperty("ledger").GetProperty("capacity").GetProperty("archiveStorage");
        Check("archive_storage_export_has_only_scoped_measurements", Keys(archiveStorageFields, "scope", "fileBytes", "fileCount", "registeredVolumes", "maximumVolumes")
            && archiveStorageFields.GetProperty("scope").GetString() == "archive_directory_top_level_file_lengths"
            && archiveStorageFields.GetProperty("fileBytes").GetInt64() == 3145728 && archiveStorageFields.GetProperty("fileCount").GetInt32() == 4
            && archiveStorageFields.GetProperty("registeredVolumes").GetInt32() == 2 && archiveStorageFields.GetProperty("maximumVolumes").GetInt32() == 128);
        using var noCapacity = JsonDocument.Parse(AssistantHealthExport.Serialize(
            snapshot with { Ledger = snapshot.Ledger! with { Capacity = null } }));
        Check("missing_capacity_stays_null_instead_of_claiming_zero_size",
            noCapacity.RootElement.GetProperty("ledger").GetProperty("capacity").ValueKind == JsonValueKind.Null);
        using var absent = JsonDocument.Parse(AssistantHealthExport.Serialize(snapshot with { Channel = null, Ledger = null, Concurrency = null }));
        Check("unavailable_health_remains_null_not_zero_or_connected",
            absent.RootElement.GetProperty("channel").ValueKind == JsonValueKind.Null
            && absent.RootElement.GetProperty("ledger").ValueKind == JsonValueKind.Null
            && absent.RootElement.GetProperty("concurrency").ValueKind == JsonValueKind.Null);
        int callbacks = 0; string[] before = Directory.GetFileSystemEntries(directory);
        Check("dialog_cancel_calls_no_guard_and_writes_nothing",
            AssistantHealthExport.WriteNew(null, snapshot, () => { callbacks++; return true; }) == AssistantHealthExportResult.Cancelled
            && callbacks == 0 && Directory.GetFileSystemEntries(directory).Order().SequenceEqual(before.Order()));
        string stale = Path.Combine(directory, "stale.json");
        Check("changed_identity_before_export_creates_no_file",
            AssistantHealthExport.WriteNew(stale, snapshot, () => false) == AssistantHealthExportResult.SnapshotChanged && !File.Exists(stale));
        int currentChecks = 0; string changedAtCreate = Path.Combine(directory, "changed-before-create.json");
        Check("changed_snapshot_after_directory_validation_creates_no_file",
            AssistantHealthExport.WriteNew(changedAtCreate, snapshot, () => ++currentChecks == 1) == AssistantHealthExportResult.SnapshotChanged
            && currentChecks == 2 && !File.Exists(changedAtCreate));
        bool refused = false;
        try { AssistantHealthExport.WriteNew(path, snapshot with { CompletedRequests = 99 }, () => true); }
        catch (IOException) { refused = true; }
        Check("existing_destination_is_never_overwritten", refused && File.ReadAllBytes(path).SequenceEqual(bytes));
        foreach (var invalid in new[] { @"relative.json", @"\\server\share\diagnostics.json", @"C:\tmp\..\diagnostics.json",
            Path.Combine(directory, "alternate.json:secret"), Path.Combine(directory, "missing", "file.json") })
        {
            bool rejected = false;
            try { AssistantHealthExport.WriteNew(invalid, snapshot, () => true); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("unverified_export_path_accepted");
        }
        Check("relative_network_traversal_ads_and_missing_parent_are_rejected", !Directory.Exists(Path.Combine(directory, "missing")));
        var filesBeforeReservedNames = Directory.GetFileSystemEntries(directory).Order().ToArray();
        foreach (string reserved in new[] { "NUL.json", "con.json", "PRN.any.json", "AUX", "CONIN$.json", "CONOUT$.json",
            "COM1.json", "COM9.json", "LPT1.json", "LPT9.json", "COM¹.json", "LPT².json", "COM³.json", "NUL .json" })
        {
            bool denied = false;
            try { AssistantHealthExport.WriteNew(Path.Combine(directory, reserved), snapshot, () => true); }
            catch (ArgumentException) { denied = true; }
            if (!denied) throw new InvalidOperationException("reserved_dos_name_not_rejected");
        }
        Check("reserved_dos_names_never_become_extended_path_files",
            Directory.GetFileSystemEntries(directory).Order().SequenceEqual(filesBeforeReservedNames));
        foreach (bool physical in new[] { false, true })
        {
            string blocked = Path.Combine(directory, physical ? "physical-redirected.json" : "logical-redirected.json");
            bool denied = false;
            var probe = new DirectoryProbe((opened, fact) => opened.StartsWith(@"\\?\", StringComparison.Ordinal) == physical
                ? fact with { ReparsePoint = true } : fact);
            try { AssistantHealthExport.WriteNew(blocked, snapshot, () => true, probe); }
            catch (IOException) { denied = true; }
            Check((physical ? "physical" : "logical") + "_ancestor_reparse_observation_refuses_before_file_creation", denied && !File.Exists(blocked));
        }
        string noProof = Path.Combine(directory, "unavailable-parent-proof.json"); bool queryRefused = false;
        try { AssistantHealthExport.WriteNew(noProof, snapshot, () => true,
            new DirectoryProbe((_, _) => throw new IOException("SYNTHETIC_NATIVE_QUERY_UNAVAILABLE"))); }
        catch (IOException) { queryRefused = true; }
        Check("unavailable_native_parent_query_never_defaults_to_safe", queryRefused && !File.Exists(noProof));
        string junctionTarget = Path.Combine(directory, "junction-target"), junction = Path.Combine(directory, "junction");
        Directory.CreateDirectory(junctionTarget);
        if (CreateIsolatedJunction(junction, junctionTarget))
        {
            bool redirectedRefused = false;
            try { AssistantHealthExport.WriteNew(Path.Combine(junction, "must-not-exist.json"), snapshot, () => true); }
            catch (IOException) { redirectedRefused = true; }
            Check("actual_junction_parent_is_rejected_without_writing_target",
                File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint) && redirectedRefused
                && !File.Exists(Path.Combine(junctionTarget, "must-not-exist.json")));
        }
        await WindowAsync(directory, snapshot, Check);
    }
    private static async Task WindowAsync(string directory, AssistantHealthSnapshot snapshot, Action<string, bool> check)
    {
        var a = snapshot.Identity!; var b = new ConversationIdentity("PRIVATE_OTHER_USER", "PRIVATE_OTHER_BINDING", "main");
        ConversationIdentity current = a; var displayed = snapshot; int reads = 0;
        var window = new AssistantHealthWindow(_ => { reads++; return Task.FromResult(displayed); }, id => id == current);
        try
        {
            check("export_disabled_before_snapshot", !window.ExportButton.IsEnabled);
            await window.RefreshAsync();
            int readBefore = reads; string saved = Path.Combine(directory, "window-current.json");
            var result = window.ExportSnapshot(() => saved);
            check("export_uses_displayed_snapshot_without_read_refresh_or_connection",
                result == AssistantHealthExportResult.Saved && File.Exists(saved) && reads == readBefore && window.ExportButton.IsEnabled);
            var files = Directory.GetFileSystemEntries(directory).Order().ToArray();
            result = window.ExportSnapshot(() => null);
            check("window_cancel_does_not_write_or_refresh", result == AssistantHealthExportResult.Cancelled && reads == readBefore
                && Directory.GetFileSystemEntries(directory).Order().SequenceEqual(files));
            string wrongIdentity = Path.Combine(directory, "window-old-identity.json");
            result = window.ExportSnapshot(() => { current = b; window.InvalidateIfIdentityChanged(); return wrongIdentity; });
            check("identity_change_inside_dialog_invalidates_snapshot_and_export_button",
                result == AssistantHealthExportResult.SnapshotChanged && !File.Exists(wrongIdentity) && !window.ExportButton.IsEnabled
                && window.ConnectionText.Text == "" && window.DetailsBox.Text == "");
            current = a; displayed = snapshot; await window.RefreshAsync();
            string oldVersion = Path.Combine(directory, "window-old-view.json");
            result = window.ExportSnapshot(() =>
            {
                displayed = snapshot with { CompletedRequests = 51 }; window.RefreshAsync().GetAwaiter().GetResult(); return oldVersion;
            });
            check("refresh_same_identity_inside_dialog_rejects_old_view_version",
                result == AssistantHealthExportResult.SnapshotChanged && !File.Exists(oldVersion) && window.ExportButton.IsEnabled);
            string rawError = "PRIVATE_INTERNAL_EXCEPTION_BODY";
            result = window.ExportSnapshot(() => throw new IOException(rawError));
            check("export_error_is_distinct_from_cancel_without_raw_exception_or_paths",
                result == AssistantHealthExportResult.Failed && !window.ResultText.Text.Contains(rawError)
                && !window.ResultText.Text.Contains(directory) && window.ExportButton.IsEnabled);
            string closed = Path.Combine(directory, "window-closed.json");
            result = window.ExportSnapshot(() => { window.Close(); return closed; });
            check("closing_window_inside_dialog_prevents_export",
                result == AssistantHealthExportResult.SnapshotChanged && !File.Exists(closed) && !window.ExportButton.IsEnabled);
        }
        finally { window.Close(); }
    }
    private static bool CreateIsolatedJunction(string link, string target)
    {
        // Both paths are newly allocated below the validated isolated test directory.
        // Single-quoted PowerShell literals are escaped before UTF-16 encoding; no shell
        // interpolation, elevation, global setting, existing link or production path is used.
        string script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"
            + link.Replace("'", "''", StringComparison.Ordinal) + "' -Target '"
            + target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("junction_test_not_started");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("junction_test_timeout"); }
        string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
        bool enabled = process.ExitCode == 0 && Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(link)!, "junction-platform-capability.json"), JsonSerializer.Serialize(new
        {
            isolatedOnly = true, enabled, exitCode = process.ExitCode, stdout, stderr,
            limitation = enabled ? null : "Actual junction creation unavailable; native-backed fault injection verifies logical and physical reparse rejection."
        }, Settings.Json));
        return enabled;
    }
    private static bool Keys(JsonElement value, params string[] expected)
        => value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(expected.Order());
    private static IEnumerable<string> AllKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            { yield return property.Name; foreach (string nested in AllKeys(property.Value)) yield return nested; }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (string nested in AllKeys(item)) yield return nested;
    }
}
