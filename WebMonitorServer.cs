using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodexDashboard;

public sealed class WebMonitorServer : IAsyncDisposable
{
    private WebApplication? application;
    private readonly SemaphoreSlim gate = new(1);
    private byte[] snapshot = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"readAt\":null,\"tasks\":[],\"hosts\":[]}");
    private string configuration = "";
    private volatile bool disposed;
    public string LocalUrl { get; private set; } = "";
    public string Status { get; private set; } = "网页监控未开启";
    public IReadOnlyList<string> LanUrls { get; private set; } = Array.Empty<string>();

    public void Publish(DashboardViewModel vm)
    {
        var data = new
        {
            schemaVersion = 1, readAt = vm.LastRefreshAt, refreshSeconds = vm.RefreshSeconds,
            source = "会话记录", hosts = vm.Hosts.Select(h => new { name = h.Name, connected = h.Error == null, lastSuccess = h.LastSuccess, error = h.Error }),
            tasks = vm.AllTasks.Select(t => new
            {
                id = t.Id, key = t.Key, title = t.Title, project = t.Project, host = t.Host,
                status = t.EffectiveStatus, statusLabel = t.ShortStatus, recordedStatus = t.RecordedStatus,
                lastActivity = t.LastActivity, startedAt = t.StartedAt, completedAt = t.CompletedAt,
                summary = t.Summary, oneLineSummary = t.OverviewText, summarySource = t.OverviewSource,
                activity = t.Activity, source = t.Source, model = t.Model,
                plan = t.Plan.Select(p => new { step = p.Step, status = p.Status }),
                timeline = t.Timeline.Select(e => new { time = e.Time, kind = e.Kind, text = e.Text })
            })
        };
        // Immutable serialized snapshots keep HTTP clients off the UI thread and
        // cannot trigger fresh collectors or see concurrent mutable model state.
        Volatile.Write(ref snapshot, JsonSerializer.SerializeToUtf8Bytes(data, Settings.Json));
    }

    public async Task ConfigureAsync(Settings settings, bool testMode = false)
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            string signature = $"{settings.WebEnabled}:{settings.WebLanEnabled}:{settings.WebPort}:{settings.WebAccessKey}:{testMode}";
            if (configuration == signature && application != null) return;
            await StopCore();
            configuration = signature;
            if (!settings.WebEnabled) { Status = "网页监控未开启"; return; }
            byte[] expectedKey = Encoding.UTF8.GetBytes(settings.WebAccessKey);
            if (expectedKey.Length < 4) throw new InvalidOperationException("访问密钥长度不足");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 1024; k.Limits.MaxRequestHeadersTotalSize = 8192; });
            builder.WebHost.UseUrls(testMode ? "http://127.0.0.1:0" : $"http://{(settings.WebLanEnabled ? "0.0.0.0" : "127.0.0.1")}:{settings.WebPort}");
            var app = builder.Build();
            application = app;
            var files = new Dictionary<string, (byte[] Body, string Mime)>(StringComparer.Ordinal)
            {
                ["/"] = (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "index.html")), "text/html; charset=utf-8"),
                ["/app.js"] = (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "app.js")), "text/javascript; charset=utf-8"),
                ["/model.mjs"] = (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "model.mjs")), "text/javascript; charset=utf-8"),
                ["/app.css"] = (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "app.css")), "text/css; charset=utf-8")
            };
            app.Run(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                if (!AllowedHost(context.Request.Host.Host) || !IsPrivateAddress(context.Connection.RemoteIpAddress))
                { context.Response.StatusCode = 403; return; }
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                { context.Response.StatusCode = 405; context.Response.Headers.Allow = "GET, HEAD"; return; }
                string path = context.Request.Path.Value ?? "/";
                if (path == "/api/snapshot")
                {
                    if (!Authorized(context.Request.Headers.Authorization.ToString(), expectedKey))
                    { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Bearer"; return; }
                    context.Response.ContentType = "application/json; charset=utf-8";
                    if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(Volatile.Read(ref snapshot), context.RequestAborted);
                    return;
                }
                if (!files.TryGetValue(path, out var file)) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = file.Mime;
                if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(file.Body, context.RequestAborted);
            });
            await app.StartAsync();
            int port = new Uri(app.Urls.First()).Port;
            LocalUrl = $"http://127.0.0.1:{port}";
            LanUrls = settings.WebLanEnabled && !testMode ? GetLanAddresses().Select(a => $"http://{a}:{port}").ToArray() : Array.Empty<string>();
            Status = settings.WebLanEnabled && !testMode ? "网页监控 · 局域网 / VPN" : "网页监控 · 仅本机";
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            await StopCore();
            Status = "网页服务启动失败：" + ex.Message;
        }
        finally { gate.Release(); }
    }

    public static bool Authorized(string header, byte[] key)
    {
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        byte[] supplied = Encoding.UTF8.GetBytes(header[7..]);
        return supplied.Length == key.Length && CryptographicOperations.FixedTimeEquals(supplied, key);
    }
    public static bool IsPrivateAddress(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (bytes[0] & 0xFE) == 0xFC;
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && IsPrivateAddress(a))
            .Distinct().ToArray();
    }
    private static bool AllowedHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        return IPAddress.IsLoopback(address) || GetLanAddresses().Contains(address);
    }
    private async Task StopCore()
    {
        if (application != null)
        {
            var old = application; application = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await old.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
            await old.DisposeAsync();
        }
        LocalUrl = ""; LanUrls = Array.Empty<string>();
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await gate.WaitAsync();
        try { await StopCore(); } finally { gate.Release(); }
    }
}
