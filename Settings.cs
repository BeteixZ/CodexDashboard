using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CodexDashboard;

public sealed class DashboardSettings : ICollectorSettings
{
    public int Version { get; set; }
    public string PythonPath { get; set; } = FindPython();
    public string CodexHome { get; set; } = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public List<string> RemoteHosts { get; set; } = new();
    public bool LocalEnabled { get; set; } = true;
    public HostRegistryDocument HostRegistry { get; set; } = new();
    public int RefreshSeconds { get; set; } = 10;
    public bool HudVisible { get; set; } = true;
    public bool HudTopmost { get; set; } = true;
    public bool HudClickThrough { get; set; }
    public double HudOpacity { get; set; } = 0.94;
    public double HudLeft { get; set; } = -1;
    public double HudTop { get; set; } = -1;
    public double HudWidth { get; set; } = 240;
    public string HudMode { get; set; } = "compact";
    public int HudRows { get; set; } = 4;
    public bool Notifications { get; set; }
    public bool Sound { get; set; }
    public bool WebEnabled { get; set; } = true;
    public bool WebLanEnabled { get; set; }
    public int WebPort { get; set; } = 4317;
    public string WebAccessKey { get; set; } = "";
    private static readonly Lazy<string> directory = new(() => ResolveDataDirectory(Path.Combine(AppContext.BaseDirectory, "data-directory.txt"),
        CollectorEnvironment.DistributionDataDirectory("dashboard", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDashboard"))));
    private static string? explicitDirectory;
    public static string DirectoryPath => explicitDirectory ?? directory.Value;
    public static void SetDataDirectory(string path)
    {
        if (directory.IsValueCreated || explicitDirectory != null)
            throw new InvalidOperationException("数据目录只能在启动读取配置之前设置一次。");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("数据目录必须为绝对路径。", nameof(path));
        explicitDirectory = Path.GetFullPath(path);
    }
    internal static string ResolveDataDirectory(string selectionFile, string fallback)
    {
        if (!File.Exists(selectionFile)) return fallback;
        if (new FileInfo(selectionFile).Length > 8192) throw new InvalidDataException("data-directory.txt 过大，未改用其他数据目录。");
        string selected = File.ReadAllText(selectionFile, new System.Text.UTF8Encoding(false, true)).Trim();
        if (selected.Length == 0 || !Path.IsPathFullyQualified(selected) || selected.Any(char.IsControl))
            throw new InvalidDataException("data-directory.txt 必须是一个绝对目录路径，未改用其他数据目录。");
        return Path.GetFullPath(selected);
    }
    public static string FilePath => Path.Combine(DirectoryPath, "dashboard.settings.json");
    public static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static bool ValidAlias(string alias) => Regex.IsMatch(alias, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z");

    public static Settings Load()
    {
        Settings result;
        string source = File.Exists(FilePath) ? FilePath : Path.Combine(DirectoryPath, "settings.json");
        if (File.Exists(source))
        {
            try { result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(source), Json) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException) { result = new(); }
        }
        else
        {
            result = new();
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.CodexHome, ".codex-global-state.json")));
                if (document.RootElement.TryGetProperty("codex-managed-remote-connections", out var hosts))
                    foreach (var host in hosts.EnumerateArray())
                        if (host.TryGetProperty("alias", out var value) && value.GetString() is string alias && ValidAlias(alias))
                            result.RemoteHosts.Add(alias);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        result.Normalize();
        return result;
    }
    public void Normalize()
    {
        if (Version < 2) { HudWidth = 240; Version = 2; }
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 120);
        HudRows = Math.Clamp(HudRows, 1, 6);
        HudOpacity = double.IsFinite(HudOpacity) ? Math.Clamp(HudOpacity, 0.35, 1) : .94;
        HudWidth = double.IsFinite(HudWidth) ? Math.Clamp(HudWidth, 190, 300) : 240;
        if (HudMode != HudModes.Tray) HudMode = HudModes.Compact;
        WebPort = Math.Clamp(WebPort, 1024, 65535);
        if (string.IsNullOrWhiteSpace(WebAccessKey) || WebAccessKey.Length < 4)
            WebAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        HudLeft = double.IsFinite(HudLeft) ? HudLeft : -1;
        HudTop = double.IsFinite(HudTop) ? HudTop : -1;
        RemoteHosts = (RemoteHosts ?? new()).Where(ValidAlias).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var registry = new HostRegistry(HostRegistry);
        registry.ReconcileConfiguration(LocalEnabled, RemoteHosts, CodexHome);
        HostRegistry = registry.Export();
    }
    private static readonly object saveLock = new();
    public void Save()
    {
        lock (saveLock)
        {
            Normalize();
            Directory.CreateDirectory(DirectoryPath);
            string temp = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
                File.Move(temp, FilePath, true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }
    }
    private static string FindPython()
    {
        return CollectorEnvironment.FindPython();
    }
}
