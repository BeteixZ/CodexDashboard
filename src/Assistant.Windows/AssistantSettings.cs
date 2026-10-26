using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CodexDashboard;

public sealed class AssistantSettings : ICollectorSettings
{
    public int Version { get; set; }
    public string PythonPath { get; set; } = FindPython();
    public string CodexHome { get; set; } = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public List<string> RemoteHosts { get; set; } = new();
    public bool LocalEnabled { get; set; } = true;
    public HostRegistryDocument HostRegistry { get; set; } = new();
    public int RefreshSeconds { get; set; } = 10;
    public int? MaxConcurrentExecutions { get; set; }
    public int InputMergeWindowMilliseconds { get; set; } = 1000;
    public bool HistoryArchiveEnabled { get; set; }
    public AssistantMediaOptions Media { get; set; } = new();
    private AssistantMediaFeatures? activeMedia;
    internal AssistantMediaFeatures ActiveMedia => activeMedia ??= AssistantMediaFeatures.Capture(Media);
    public WeChatOptions WeChat { get; set; } = new();
    public ChatGptOptions ChatGpt { get; set; } = new();
    private static readonly Lazy<string> directory = new(() => ResolveDataDirectory(Path.Combine(AppContext.BaseDirectory, "data-directory.txt"),
        CollectorEnvironment.DistributionDataDirectory("assistant", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAssistant"))));
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
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static bool ValidAlias(string alias) => Regex.IsMatch(alias, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z");

    public static Settings Load()
    {
        Settings result;
        if (File.Exists(FilePath))
        {
            try { result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException) { throw new InvalidDataException("助手配置无法读取；原文件保持不变，请修复或从备份恢复。", ex); }
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
        Media ??= new();
        if (Version > 1) throw new InvalidDataException("助手配置来自较新版本，未覆盖原文件。");
        Version = 1;
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 120);
        if (MaxConcurrentExecutions is < 1 or > 32)
            throw new InvalidDataException("同时执行上限必须留空或为 1 至 32，未改用不限模式。");
        if (InputMergeWindowMilliseconds is < 0 or > 3000)
            throw new InvalidDataException("连续补充等待时间必须为 0 至 3000 毫秒，未改用其他值。");
        RemoteHosts = (RemoteHosts ?? new()).Where(ValidAlias).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var registry = new HostRegistry(HostRegistry);
        registry.ReconcileConfiguration(LocalEnabled, RemoteHosts, CodexHome);
        HostRegistry = registry.Export();
        WeChat ??= new();
        ChatGpt ??= new();
        WeChat.RecentAttempts = (WeChat.RecentAttempts ?? new()).TakeLast(512).Distinct().ToList();
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
