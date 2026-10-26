using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

public sealed class MonitorService : IDisposable
{
    private readonly ICollectorSettings settings;
    private readonly IHostCollector collector;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, HostSnapshot> previous = new(StringComparer.OrdinalIgnoreCase);
    public MonitorService(ICollectorSettings settings, IHostCollector? collector = null)
    {
        this.settings = settings;
        this.collector = collector ?? new ProcessHostCollector();
    }

    public async Task<List<HostSnapshot>?> RefreshAsync()
    {
        if (!await gate.WaitAsync(0, lifetime.Token)) return null;
        try
        {
            var names = settings.RemoteHosts.ToList();
            if (settings.LocalEnabled) names.Insert(0, "本机");
            var snapshots = await Task.WhenAll(names.Select(name => collector.ReadAsync(name, settings, lifetime.Token)));
            foreach (var host in snapshots)
            {
                if (host.Error != null && previous.TryGetValue(host.Name, out var old))
                {
                    host.LastSuccess = old.LastSuccess;
                    host.Tasks = old.Tasks;
                    foreach (var task in host.Tasks) task.Disconnected = true;
                }
                previous[host.Name] = host;
            }
            foreach (var removed in previous.Keys.Except(names).ToArray()) previous.Remove(removed);
            return snapshots.ToList();
        }
        finally { gate.Release(); }
    }

    public void Dispose() { lifetime.Cancel(); }
}

public sealed class ProcessHostCollector : IHostCollector
{
    public async Task<HostSnapshot> ReadAsync(string name, ICollectorSettings settings, CancellationToken cancellationToken)
    {
        var host = new HostSnapshot { Name = name };
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "collector.py");
            var start = new ProcessStartInfo
            {
                FileName = name == "本机" ? CollectorEnvironment.ResolvePython(settings.PythonPath) : "ssh.exe",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            if (name == "本机")
            {
                foreach (string arg in new[] { "-u", script, "--home", settings.CodexHome }) start.ArgumentList.Add(arg);
                start.Environment["PYTHONIOENCODING"] = "utf-8";
            }
            else
            {
                if (!HostIdentity.ValidAlias(name)) throw new InvalidOperationException("SSH 别名格式不正确");
                foreach (string arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=1", name, "python3 -" }) start.ArgumentList.Add(arg);
            }
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("无法启动采集器");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                if (name != "本机") await process.StandardInput.WriteAsync((await File.ReadAllTextAsync(script, timeout.Token)).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string output = await stdout;
                string error = await stderr;
                if (process.ExitCode != 0)
                {
                    string message = name == "本机" ? "本机采集失败，请检查 Python 路径" : "SSH 采集失败，请检查连接和远程 python3";
                    if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)) message = "SSH 主机密钥未通过验证，请在终端核对主机";
                    else if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) message = "SSH 登录失败，请检查现有密钥配置";
                    throw new InvalidOperationException(message);
                }
                // Login banners are permitted before the final JSON response.
                string json = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => x.TrimStart().StartsWith('{')) ?? "";
                var result = JsonSerializer.Deserialize<CollectorResult>(json, SharedJson.Options) ?? throw new InvalidOperationException("采集器返回空数据");
                if (result.SchemaVersion != 1) throw new InvalidOperationException("采集器版本不兼容");
                if (result.Error != null) throw new InvalidOperationException(result.Error);
                foreach (var task in result.Tasks) task.Host = name;
                host.Tasks = result.Tasks;
                host.LastSuccess = DateTimeOffset.UtcNow;
                host.Note = string.Join("；", result.Warnings) + (result.Truncated ? "；展示最近 120 个主任务" : "");
            }
            finally
            {
                if (!process.HasExited)
                {
                    // Only our collector/SSH client process tree; never the Codex app-server.
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { host.Error = "读取超时，等待下次刷新"; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        { host.Error = ex.Message; }
        return host;
    }
}
