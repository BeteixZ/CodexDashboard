using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Codex.Protocol;
using Microsoft.Win32.SafeHandles;

namespace CodexDashboard;

internal enum AssistantHealthExportResult { Cancelled, Saved, SnapshotChanged, Failed }

internal static class AssistantHealthExport
{
    // A deliberately separate whitelist projection. Never serialize the health snapshot,
    // Identity, DataDirectory, UI summaries, settings, a diagnostic object, or exceptions.
    internal static byte[] Serialize(AssistantHealthSnapshot snapshot)
    {
        var channel = snapshot.Channel; var ledger = snapshot.Ledger; var concurrency = snapshot.Concurrency;
        var document = new
        {
            formatVersion = 1,
            readAtUtc = snapshot.ReadAt.ToUniversalTime(),
            appVersion = typeof(AssistantHealthExport).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            protocolVersion = new { major = ProtocolVersion.V1.Major, minor = ProtocolVersion.V1.Minor },
            // Supported readers, not a claim about the active file's logical format.
            ledgerReaderSchemas = new[] { 1, 2, 3, 4, 5 },
            channel = channel == null ? null : new
            {
                state = channel.State switch
                {
                    ConnectionState.NotConfigured => "not_configured", ConnectionState.Disconnected => "disconnected",
                    ConnectionState.Connecting => "connecting", ConnectionState.Connected => "connected",
                    ConnectionState.Degraded => "degraded", _ => "unknown"
                },
                receiverRunning = channel.ReceiverRunning, notificationQueueRunning = channel.SenderRunning,
                receiverLeaseHeld = channel.ReceiverLeaseHeld, stopping = channel.Stopping
            },
            model = new
            {
                initialized = snapshot.ModelInitialized, enabled = snapshot.ModelEnabled,
                completedRequestsThisProcess = snapshot.CompletedRequests,
                lastCompletedAtUtc = snapshot.LastModelCompletedAt?.ToUniversalTime(),
                lastFailureAtUtc = snapshot.LastModelFailureAt?.ToUniversalTime()
            },
            collection = new
            {
                configuredSources = snapshot.ConfiguredSources, observedSources = snapshot.ObservedSources,
                failedSources = snapshot.FailedSources, observedTasks = snapshot.ObservedTasks,
                lastCollectionAtUtc = snapshot.LastCollectionAt?.ToUniversalTime()
            },
            ledger = ledger == null ? null : new
            {
                state = ledger.State switch { AssistantLedgerHealthState.Inactive => "inactive",
                    AssistantLedgerHealthState.Available => "available", AssistantLedgerHealthState.ReadOnly => "read_only", _ => "unknown" },
                waitingInputs = ledger.WaitingInputs, inputDecisions = ledger.InputDecisions,
                generatingInputs = ledger.GeneratingInputs,
                rawPendingMessagesIncludingHistoricalAndCatchUpSources = ledger.PendingMessages,
                sendingMessages = ledger.SendingMessages, unknownMessages = ledger.UnknownMessages,
                acceptedOperations = ledger.AcceptedOperations, runningOperations = ledger.RunningOperations,
                unknownOperations = ledger.UnknownOperations, hasRuntimeError = ledger.HasRuntimeError,
                capacity = ledger.Capacity == null ? null : new
                {
                    scope = "whole_ledger_validated_logical_snapshot",
                    logicalSnapshotBytes = ledger.Capacity.LogicalSnapshotBytes,
                    maximumSnapshotBytes = ledger.Capacity.MaximumSnapshotBytes,
                    journalSequence = ledger.Capacity.JournalSequence,
                    readOnly = ledger.Capacity.IsReadOnly, nearLimit = ledger.Capacity.NearLimit
                }
            },
            concurrency = concurrency == null ? null : new
            {
                limit = concurrency.Limit, occupied = concurrency.Occupied, unknown = concurrency.Unknown,
                reserved = concurrency.Reserved, recoveryReady = concurrency.RecoveryReady
            },
            statusServiceStarted = snapshot.StatusServiceStarted
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        if (ledger?.Capacity is { MaximumStoredSnapshotBytes: { } maximum } size)
        {
            var archived = JsonSerializer.SerializeToNode(document)!;
            archived["formatVersion"] = 2;
            archived["ledger"]!["capacity"]!["manifest"] = JsonSerializer.SerializeToNode(new
            { scope = "validated_ledger_reference_manifest", bytes = size.StoredSnapshotBytes, maximumBytes = maximum });
            if (size.ArchiveStorage is { } storage)
                archived["ledger"]!["capacity"]!["archiveStorage"] = JsonSerializer.SerializeToNode(new
                {
                    scope = "archive_directory_top_level_file_lengths", fileBytes = storage.FileBytes,
                    fileCount = storage.FileCount, registeredVolumes = storage.RegisteredVolumes, maximumVolumes = storage.MaximumVolumes
                });
            return JsonSerializer.SerializeToUtf8Bytes(archived, options);
        }
        return JsonSerializer.SerializeToUtf8Bytes(document, options);
    }

    internal static AssistantHealthExportResult WriteNew(string? selectedPath, AssistantHealthSnapshot snapshot, Func<bool> stillCurrent,
        IDirectoryProbe? directoryProbe = null)
    {
        if (selectedPath == null) return AssistantHealthExportResult.Cancelled;
        if (!stillCurrent()) return AssistantHealthExportResult.SnapshotChanged;
        byte[] contents = Serialize(snapshot);
        if (contents.Length > 32768) throw new InvalidOperationException("health_export_size_limit");
        using var destination = LocalNewFileDestination.Lock(selectedPath, directoryProbe ?? new NativeDirectoryProbe());
        // Synchronous and small: no UI await between the final identity/view check and write.
        // The directory handles prevent renaming the selected physical ancestry during create.
        if (!stillCurrent()) return AssistantHealthExportResult.SnapshotChanged;
        using var output = new FileStream(destination.PhysicalFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        output.Write(contents); output.Flush(flushToDisk: true);
        return AssistantHealthExportResult.Saved;
    }

    internal sealed record DirectoryFact(string PhysicalPath, string FileIdentity, bool Directory, bool ReparsePoint);
    internal interface IDirectoryLease : IDisposable { DirectoryFact Fact { get; } }
    internal interface IDirectoryProbe { IDirectoryLease Open(string path); }
    internal sealed class NativeDirectoryProbe : IDirectoryProbe
    {
        public IDirectoryLease Open(string path)
        {
            var handle = CreateFile(path, 0x80 /* READ_ATTRIBUTES */, 1 /* READ only: block new WRITE/DELETE handles */,
                IntPtr.Zero, 3, 0x02200000 /* BACKUP_SEMANTICS|OPEN_REPARSE_POINT */, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new IOException("health_export_parent_unavailable");
                var name = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 1);
                if (length == 0 || length >= name.Capacity) throw new IOException("health_export_physical_parent_unavailable");
                var result = new DirectoryLease(handle, new(name.ToString(), $"{info.VolumeSerial:X8}/{info.IndexHigh:X8}{info.IndexLow:X8}",
                    (info.Attributes & (uint)FileAttributes.Directory) != 0, (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0));
                handle = null!; return result;
            }
            finally { handle?.Dispose(); }
        }
        private sealed class DirectoryLease(SafeFileHandle handle, DirectoryFact fact) : IDirectoryLease
        { public DirectoryFact Fact => fact; public void Dispose() => handle.Dispose(); }
    }

    internal sealed class LocalNewFileDestination(IDirectoryProbe probe) : IDisposable
    {
        private readonly List<IDirectoryLease> handles = [];
        internal string PhysicalFile { get; private set; } = "";
        internal static LocalNewFileDestination Lock(string selected, IDirectoryProbe probe)
        {
            var result = new LocalNewFileDestination(probe);
            try { result.Initialize(selected); return result; }
            catch { result.Dispose(); throw; }
        }
        private void Initialize(string selected)
        {
            if (!OperatingSystem.IsWindows() || selected.Length is < 4 or > 4096 || selected.Any(char.IsControl)
                || !char.IsAsciiLetter(selected[0]) || selected[1] != ':' || selected[2] is not ('\\' or '/')
                || selected[2..].Contains(':') || selected.Split('\\', '/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
                throw new ArgumentException("health_export_local_path_required");
            string full = Path.GetFullPath(selected), name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedDosName(name))
                throw new ArgumentException("health_export_file_required");
            string parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("health_export_parent_required");
            var logical = new List<string>();
            for (string? part = parent; part != null; part = Path.GetDirectoryName(part)) logical.Add(part);
            logical.Reverse();
            foreach (string part in logical) OpenDirectory(part);
            var selectedParent = handles[^1];
            string physical = selectedParent.Fact.PhysicalPath;
            string[] ancestry = PhysicalAncestors(physical);
            foreach (string part in ancestry) OpenDirectory(part);
            // Keep the original directory identity; a drive alias cannot redirect the write.
            if (selectedParent.Fact.FileIdentity != handles[^1].Fact.FileIdentity)
                throw new IOException("health_export_parent_changed");
            PhysicalFile = physical.TrimEnd('\\') + "\\" + name;
        }
        private static bool ReservedDosName(string name)
        {
            // Extended VolumeGUID paths bypass Win32 DOS-name normalization.
            // Refuse those names instead of changing the selected path's meaning.
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3]);
        }
        private void OpenDirectory(string path)
        {
            var handle = probe.Open(path);
            handles.Add(handle);
            if (!handle.Fact.Directory || handle.Fact.ReparsePoint || string.IsNullOrWhiteSpace(handle.Fact.FileIdentity))
                throw new IOException("health_export_redirected_parent");
        }
        private static string[] PhysicalAncestors(string path)
        {
            const string prefix = @"\\?\Volume{";
            int end = path.IndexOf(@"}\", StringComparison.Ordinal);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || end < prefix.Length
                || !Guid.TryParseExact(path[prefix.Length..end], "D", out _) || path.Any(char.IsControl) || path.Contains('/'))
                throw new IOException("health_export_nonlocal_parent");
            string root = path[..(end + 2)], rest = path[(end + 2)..].TrimEnd('\\');
            var parts = new List<string> { root }; string current = root.TrimEnd('\\');
            if (rest.Length != 0) foreach (string part in rest.Split('\\'))
            {
                if (part.Length == 0 || part is "." or ".." || part.Contains(':')) throw new IOException("health_export_invalid_parent");
                current += "\\" + part; parts.Add(current);
            }
            return parts.ToArray();
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
