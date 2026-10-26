using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexDashboard;

public interface ICollectorSettings
{
    string PythonPath { get; }
    string CodexHome { get; }
    List<string> RemoteHosts { get; }
    bool LocalEnabled { get; }
}

/// <summary>Reads only public task records; never submits or resumes a Codex task.</summary>
public interface IHostCollector
{
    Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken cancellationToken);
}

public static class SharedJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public static class HostIdentity
{
    public const string LocalHostId = "local";
    public const string LocalDisplayName = "本机";
    public const string RemotePrefix = "remote-ssh-discovered:";

    public static bool ValidAlias(string? alias) => alias != null
        && Regex.IsMatch(alias, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z");

    public static string ToHostId(string displayName)
    {
        if (displayName == LocalDisplayName) return LocalHostId;
        if (!ValidAlias(displayName)) throw new ArgumentException("SSH 别名格式不正确。", nameof(displayName));
        return RemotePrefix + displayName;
    }

    public static string DisplayName(string hostId)
    {
        if (hostId == LocalHostId) return LocalDisplayName;
        if (hostId.StartsWith(RemotePrefix, StringComparison.Ordinal)
            && ValidAlias(hostId[RemotePrefix.Length..])) return hostId[RemotePrefix.Length..];
        throw new ArgumentException("Codex 主机标识无效。", nameof(hostId));
    }
}

public static class CollectorEnvironment
{
    public static string DistributionDataDirectory(string product, string legacyFallback, string? packageDirectory = null)
    {
        string marker = Path.Combine(packageDirectory ?? AppContext.BaseDirectory, "distribution.json");
        if (!File.Exists(marker)) return legacyFallback;
        using var document = JsonDocument.Parse(File.ReadAllText(marker));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("mode").GetString() != "portable"
            || root.GetProperty("product").GetString() != product
            || product is not ("dashboard" or "assistant"))
            throw new InvalidDataException("发行包标记无效，未改用其他数据目录。");
        string folder = product == "dashboard" ? "CodexDashboardPortable" : "CodexAssistantPortable";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    public static string FindPython(string? packageDirectory = null)
    {
        string executable = OperatingSystem.IsWindows() ? "python.exe" : "python3";
        string bundled = Path.Combine(packageDirectory ?? AppContext.BaseDirectory, "python", executable);
        if (File.Exists(bundled)) return "python";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), executable));
        return paths.FirstOrDefault(p => File.Exists(p)
            && !p.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) ?? executable;
    }

    // A saved default must follow the package when a versioned installation moves.
    // Explicit custom interpreters outside our bundled python directory are retained.
    public static string ResolvePython(string configured, string? packageDirectory = null)
    {
        string bundled = Path.Combine(packageDirectory ?? AppContext.BaseDirectory, "python", "python.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(bundled)) return configured;
        if (string.IsNullOrWhiteSpace(configured) || configured is "python" or "python.exe") return bundled;
        if (Path.IsPathFullyQualified(configured)
            && string.Equals(Path.GetFileName(configured), "python.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(configured)), "python", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(configured)) return bundled;
        return configured;
    }
}
