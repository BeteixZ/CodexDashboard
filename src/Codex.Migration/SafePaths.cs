using System.Text;
using System.Text.Json;

namespace Codex.Migration;

internal sealed class MigrationFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class SafePaths
{
    internal static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static readonly StringComparer Comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Absolute(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || value.Any(char.IsControl)) throw new MigrationFailure("absolute_path_required");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (full == Path.GetPathRoot(full)) throw new MigrationFailure("root_directory_forbidden");
        RejectLinks(full);
        return full;
    }

    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new MigrationFailure("reparse_point_forbidden");
        }
    }

    internal static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "" or "." or ".."))
            throw new MigrationFailure("unsafe_relative_path");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, Comparison))
            throw new MigrationFailure("path_escape");
        RejectLinks(full);
        return full;
    }

    internal static MigrationRequest Normalize(MigrationRequest request)
    {
        var result = new MigrationRequest(Absolute(request.SourceDirectory), Absolute(request.TargetDirectory),
            Absolute(request.TransactionDirectory), request.PauseTargetServices);
        string[] paths = [result.SourceDirectory, result.TargetDirectory, result.TransactionDirectory];
        for (int i = 0; i < paths.Length; i++)
            for (int j = i + 1; j < paths.Length; j++)
                if (paths[i].Equals(paths[j], Comparison)
                    || paths[i].StartsWith(paths[j] + Path.DirectorySeparatorChar, Comparison)
                    || paths[j].StartsWith(paths[i] + Path.DirectorySeparatorChar, Comparison))
                    throw new MigrationFailure("overlapping_directories");
        if (!Directory.Exists(result.SourceDirectory)) throw new MigrationFailure("source_missing");
        return result;
    }

    internal static byte[] Read(string path, long maxBytes = 16 * 1024 * 1024)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new MigrationFailure("file_too_large");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new MigrationFailure("file_changed_during_read");
        return bytes;
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            RejectLinks(path);
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;
        RejectLinks(root);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root).Order(Comparer))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var child in Files(entry)) yield return child;
            }
            else yield return entry;
        }
    }
}

public static class MigrationDiscovery
{
    /// <summary>Only metadata is inspected; settings/credential contents are never read.</summary>
    public static IReadOnlyList<DataDirectoryCandidate> Locate(IEnumerable<string> explicitlySuppliedDirectories)
        => explicitlySuppliedDirectories.Select(SafePaths.Absolute).Distinct(SafePaths.Comparer)
            .Select(path => new DataDirectoryCandidate(path, Directory.Exists(path),
                File.Exists(Path.Combine(path, "settings.json")),
                File.Exists(Path.Combine(path, "chatgpt.credentials")),
                File.Exists(Path.Combine(path, "task-control.state")),
                Directory.Exists(Path.Combine(path, "assistant", "profiles")))).ToArray();

    /// <summary>Reads only the explicit installation's non-secret data-directory.txt selector.</summary>
    public static string ResolveDataDirectory(string installationDirectory, string fallbackDirectory)
    {
        string installation = SafePaths.Absolute(installationDirectory);
        string fallback = SafePaths.Absolute(fallbackDirectory);
        string selector = SafePaths.Under(installation, "data-directory.txt");
        if (!File.Exists(selector)) return fallback;
        var encoding = new UTF8Encoding(false, true);
        string selected = encoding.GetString(SafePaths.Read(selector, 8192)).Trim().TrimStart('\uFEFF');
        return SafePaths.Absolute(selected);
    }
}

/// <summary>Every assistant entry point must check this before opening credentials or polling.</summary>
public static class MigrationStartup
{
    internal const string MarkerName = ".codex-migration.json";
    internal const string ActivationName = ".codex-migration-activated.json";
    internal const string LockName = ".codex-migration.lock";
    public const string RecoveryHoldName = ".codex-recovery-hold.json";

    /// <summary>Persistently blocks cooperating hosts before configuration or receiver startup.
    /// This is not a legacy-process exclusion proof or permission to resume.</summary>
    public static void HoldForRecovery(string dataDirectory)
    {
        string directory = SafePaths.Absolute(dataDirectory);
        using var lease = new FileStream(SafePaths.Under(directory, LockName), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        string marker = SafePaths.Under(directory, RecoveryHoldName);
        if (File.Exists(marker)) return;
        using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new { version = 1, heldAtUtc = DateTimeOffset.UtcNow });
        file.Flush(true);
    }

    public static bool CanStart(string dataDirectory)
    {
        try
        {
            if (File.Exists(SafePaths.Under(SafePaths.Absolute(dataDirectory), RecoveryHoldName))) return false;
            var path = SafePaths.Under(SafePaths.Absolute(dataDirectory), MarkerName);
            if (!File.Exists(path)) return true; // Fresh installation.
            byte[] bytes = SafePaths.Read(path, 4096);
            _ = SettingsTransfer.Read(bytes);
            var marker = JsonSerializer.Deserialize<StartupMarker>(bytes);
            return marker is { Version: 1, State: "ready" }
                && Guid.TryParseExact(marker.TransactionId, "N", out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or MigrationFailure or ArgumentException) { return false; }
    }

    /// <summary>
    /// Call before starting ANY receiver/model/control work. Once activated, unattended rollback is
    /// conservatively rejected even if payload files happen to retain their original hashes.
    /// </summary>
    public static void RecordActivation(string dataDirectory)
    {
        string directory = SafePaths.Absolute(dataDirectory);
        if (!CanStart(directory)) throw new InvalidOperationException("migration_not_ready");
        string markerPath = SafePaths.Under(directory, MarkerName);
        if (!File.Exists(markerPath)) return;
        SafePaths.AtomicWrite(SafePaths.Under(directory, ActivationName),
            JsonSerializer.SerializeToUtf8Bytes(new { version = 1, activatedAt = DateTimeOffset.UtcNow }));
    }

    /// <summary>
    /// The host should retain this lease until all receivers and control writers have stopped.
    /// This excludes cooperating migration processes and records activation before credentials
    /// are loaded. It does not replace the external guard for old executables.
    /// </summary>
    public static IDisposable AcquireRuntimeLease(string dataDirectory)
    {
        string directory = SafePaths.Absolute(dataDirectory);
        Directory.CreateDirectory(directory);
        var lease = new FileStream(SafePaths.Under(directory, LockName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read);
        try
        {
            RecordActivation(directory);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
}
