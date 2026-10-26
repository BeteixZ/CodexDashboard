using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using Codex.Protocol;

namespace CodexDashboard;

public partial class App : Application
{
    private Settings settings = null!;
    private DashboardViewModel vm = null!;
    private MainWindow dashboard = null!;
    private HudWindow hud = null!;
    private TrayService? tray;
    private DispatcherTimer? timer;
    private Mutex? singleInstance;
    private EventWaitHandle? activateEvent;
    private RegisteredWaitHandle? activationWait;
    private WebMonitorServer? web;
    private ProductStatusBridge? status;
    private EventWaitHandle? exitEvent;
    private RegisteredWaitHandle? exitWait;
    private bool smoke;
    private int cleanedUp;
    public bool Exiting { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ProductLifecycleTest.IsRequested(e.Args))
        {
            Shutdown(await ProductLifecycleTest.RunAsync(e.Args, ProtocolProduct.Dashboard));
            return;
        }
        var startupFlags = new[] { "--smoke-test", "--data-dir", "--protocol-profile", "--exit",
            "--background", "--wechat-settings", "--chatgpt-connect" };
        if (e.Args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) && !startupFlags.Contains(arg)))
        {
            // Assistant diagnostics belong to its own executable. A misspelled
            // or obsolete test flag must never silently start normal monitoring.
            Console.Error.WriteLine("Unrecognized Dashboard option; no configuration was opened.");
            Shutdown(2); return;
        }
        smoke = e.Args.Contains("--smoke-test");
        int dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0)
        {
            if (dataIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
            Settings.SetDataDirectory(e.Args[dataIndex + 1]);
        }
        if (smoke && dataIndex < 0) { Shutdown(2); return; }
        bool isolatedDistribution = File.Exists(Path.Combine(AppContext.BaseDirectory, "distribution.json"));
        string instancePrefix = DashboardInstanceIdentity.Prefix(dataIndex >= 0 || isolatedDistribution ? Settings.DirectoryPath : null);
        if (e.Args.Contains("--exit"))
        {
            try { using var requested = EventWaitHandle.OpenExisting(instancePrefix + ".Exit"); requested.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        if (!smoke)
        {
            singleInstance = new Mutex(true, instancePrefix + ".Singleton", out bool created);
            if (!created)
            {
                try { using var existing = EventWaitHandle.OpenExisting(instancePrefix + ".Activate"); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                singleInstance.Dispose(); singleInstance = null; Shutdown(); return;
            }
            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instancePrefix + ".Activate");
            activationWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, Timeout.Infinite, false);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instancePrefix + ".Exit");
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(ExitApplication), null, Timeout.Infinite, false);
        }
        try
        {
            settings = Settings.Load();
            vm = new DashboardViewModel(settings, smoke ? new DashboardSmokeCollector() : null);
            web = new WebMonitorServer();
            vm.Updated += () =>
            {
                web.Publish(vm);
                status?.Publish(new()
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    Readiness = vm.Hosts.Any(h => h.Error != null) ? Availability.Degraded : Availability.Available
                });
            };
            dashboard = new MainWindow(vm); MainWindow = dashboard;
            hud = new HudWindow(settings, vm);
            dashboard.Show();
            if (settings.HudVisible && settings.HudMode == "compact") hud.Show();
            if (smoke)
            {
                int index = Array.IndexOf(e.Args, "--smoke-test");
                string directory = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(AppContext.BaseDirectory, "smoke");
                await RunSmokeTest(directory); ExitApplication(); return;
            }
            tray = new TrayService(this, vm, settings);
            string profile = dataIndex >= 0 ? TextSafety.Hash(Settings.DirectoryPath.ToUpperInvariant())[..24] : "default";
            int profileIndex = Array.IndexOf(e.Args, "--protocol-profile");
            if (profileIndex >= 0 && profileIndex + 1 < e.Args.Length) profile = e.Args[profileIndex + 1];
            status = new(ProtocolProduct.Dashboard, profile);
            status.Start();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.RefreshSeconds) };
            timer.Tick += async (_, _) =>
            {
                try
                {
                    await vm.RefreshAsync();
                }
                catch (Exception ex) { Debug.WriteLine($"定时刷新异常: {ex.Message}"); }
            };
            timer.Start();
            await ConfigureWeb();
            SaveSettings();
            await vm.RefreshAsync();
            if (e.Args.Contains("--wechat-settings")) ShowSettings(true);
            if (e.Args.Contains("--chatgpt-connect")) ShowChatGpt(true);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            File.WriteAllText(Path.Combine(Settings.DirectoryPath, "startup-error.txt"), ex.ToString());
            if (!smoke) MessageBox.Show("启动失败：" + ex.Message, "Codex Dashboard");
            Exiting = true; Shutdown(1);
        }
    }
    public void ShowDashboard()
    {
        if (dashboard == null || Exiting) return;
        dashboard.Show(); dashboard.WindowState = WindowState.Normal; dashboard.Activate();
    }
    public void ToggleHud(bool show)
    {
        settings.HudVisible = show;
        if (show) { settings.HudMode = "compact"; hud.Show(); hud.PlaceOnScreen(); } else hud.Hide();
        tray?.Update(); SaveSettings();
    }
    public void SetHudMode(string mode)
    {
        settings.HudMode = mode == HudModes.Tray ? HudModes.Tray : HudModes.Compact;
        settings.HudVisible = true;
        ApplyHudSettings();
        tray?.Update(); SaveSettings();
    }
    public void OpenWeb()
    {
        if (web == null || string.IsNullOrEmpty(web.LocalUrl)) { ShowSettings(); return; }
        Process.Start(new ProcessStartInfo(web.LocalUrl + "/#token=" + Uri.EscapeDataString(settings.WebAccessKey)) { UseShellExecute = true });
    }
    public string WebConnectionAddress => web?.LanUrls.FirstOrDefault() ?? web?.LocalUrl ?? "";
    public string WebShareLink => WebConnectionAddress.Length > 0 ? WebConnectionAddress + "/#token=" + settings.WebAccessKey : "";
    private async Task ConfigureWeb()
    {
        if (web == null || Exiting) return;
        try
        {
            await web.ConfigureAsync(settings, smoke);
            vm.SetWebStatus(web.Status, WebConnectionAddress);
        }
        catch (Exception ex)
        {
            vm.SetWebStatus("网页监控启动失败：" + ex.Message, "");
            Debug.WriteLine($"Web服务配置异常: {ex}");
        }
    }
    public void ShowSettings(bool wechatTab = false)
    {
        if (wechatTab) { OpenAssistant(); return; }
        ShowDashboard();
        var dialog = new SettingsWindow(settings) { Owner = dashboard };
        if (dialog.ShowDialog() == true) _ = ApplySettingsAsync();
    }
    public void ShowChatGpt(bool connect = false) => OpenAssistant();
    public void OpenAssistant() => AssistantLauncher.Open(dashboard);
    internal async Task<string> ReadAssistantStatusAsync(CancellationToken ct)
    {
        var current = status == null ? null : await status.ReadAssistantAsync(ct);
        if (current == null) return "助手未连接；可以独立打开助手。";
        if (DateTimeOffset.UtcNow - current.ObservedAt > TimeSpan.FromSeconds(45))
            return "助手状态尚未刷新；请在助手中查看当前情况。";
        string connection = current.WeChat switch
        {
            ConnectionState.Connected => "微信已连接",
            ConnectionState.Connecting => "微信连接中",
            ConnectionState.NotConfigured => "微信未配置",
            ConnectionState.Disconnected => "微信已断开",
            ConnectionState.Degraded => "微信需要处理",
            _ => "微信状态尚未确认"
        };
        return $"{connection} · {current.QueuedCount} 条待处理 · {current.AttentionCount} 项需关注";
    }
    public void ApplySettings() => _ = ApplySettingsAsync();
    public async Task ApplySettingsAsync()
    {
        settings.Normalize(); ApplyHudSettings();
        if (timer != null) timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        vm.Filter(); tray?.Update(); SaveSettings();
        try { await vm.RefreshAsync(); } catch (Exception ex) { Debug.WriteLine($"应用设置后刷新异常: {ex.Message}"); }
        try { await ConfigureWeb(); } catch (Exception ex) { Debug.WriteLine($"应用设置后配置Web异常: {ex.Message}"); }
    }
    private void ApplyHudSettings()
    {
        hud.ApplySettings();
        if (settings.HudVisible && settings.HudMode == HudModes.Compact) { hud.Show(); hud.PlaceOnScreen(); } else hud.Hide();
    }
    public void SaveSettings()
    {
        if (smoke || settings == null) return;
        try { settings.Save(); }
        catch (IOException ex) { MessageBox.Show("设置未能保存：" + ex.Message, "Codex Dashboard"); }
    }
    private void CleanupCore()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0) return;
        Exiting = true;
        SaveSettings();
        try { timer?.Stop(); } catch { }
        try { tray?.Dispose(); tray = null; } catch { }
        try { vm?.Dispose(); } catch { }
        try { activationWait?.Unregister(null); } catch { }
        try { activateEvent?.Dispose(); } catch { }
        try { exitWait?.Unregister(null); } catch { }
        try { exitEvent?.Dispose(); } catch { }
        try
        {
            if (singleInstance != null)
            {
                singleInstance.ReleaseMutex();
                singleInstance.Dispose();
                singleInstance = null;
            }
        }
        catch { }
    }
    public async void ExitApplication()
    {
        if (Exiting && cleanedUp != 0) return;
        CleanupCore();
        if (web != null)
        {
            try { await web.DisposeAsync(); } catch { }
            web = null;
        }
        if (status != null) { await status.DisposeAsync(); status = null; }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        CleanupCore();
        if (status != null) { _ = status.DisposeAsync(); status = null; }
        if (web != null)
        {
            try { web.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            web = null;
        }
        base.OnExit(e);
    }
    private async Task RunSmokeTest(string directory)
    {
        Directory.CreateDirectory(directory);
        settings.Notifications = false;
        await vm.RefreshAsync();
        var webChecks = await WebSmokeTests.RunAsync(web!, settings, vm);
        vm.SetWebStatus(web!.Status, WebConnectionAddress);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        dashboard.UpdateLayout(); hud.Show(); hud.UpdateLayout();
        bool overviewVisible = vm.SelectedTask == null || dashboard.OverviewTextBlock.Text == vm.SelectedTask.OverviewText && !string.IsNullOrWhiteSpace(vm.SelectedTask.OneLineSummary);
        SaveImage(dashboard, Path.Combine(directory, "dashboard.png"));
        SaveImage(hud, Path.Combine(directory, "hud.png"));
        var settingsPreview = new SettingsWindow(settings) { Owner = dashboard };
        settingsPreview.Show(); settingsPreview.UpdateLayout();
        SaveImage(settingsPreview, Path.Combine(directory, "settings.png"));
        settingsPreview.Close();
        var handle = new WindowInteropHelper(hud).Handle;
        long original = HudWindow.GetWindowLongPtr(handle, -20).ToInt64();
        HudWindow.SetClickThrough(handle, true);
        bool through = (HudWindow.GetWindowLongPtr(handle, -20).ToInt64() & 0x20) != 0;
        HudWindow.SetClickThrough(handle, false);
        bool restored = (HudWindow.GetWindowLongPtr(handle, -20).ToInt64() & 0x20) == 0;
        bool layered = (original & 0x80000) == (HudWindow.GetWindowLongPtr(handle, -20).ToInt64() & 0x80000);
        vm.Search = "__no_matching_task__"; vm.Filter();
        bool search = vm.VisibleTasks.Count == 0;
        vm.Search = ""; vm.Filter();
        using var testTray = new TrayService(this, vm, settings);
        testTray.Update(); testTray.Update();
        bool fluentBackground = dashboard.Background is SolidColorBrush b && b.Color.R > 200;
        bool compactDimensions = hud.ActualWidth <= 300 && hud.ActualHeight <= 230;
        double hudWidth = hud.ActualWidth, hudHeight = hud.ActualHeight;
        string initialMode = settings.HudMode;
        SetHudMode("tray");
        bool miniHidesDesktopHud = !hud.IsVisible;
        SetHudMode("compact");
        bool compactRestoresHud = hud.IsVisible;
        settings.HudMode = initialMode;
        var dotColors = new[] { System.Drawing.Color.Green, System.Drawing.Color.Goldenrod, System.Drawing.Color.Red, System.Drawing.Color.DodgerBlue };
        bool coloredDots;
        using (var dots = TrayService.RenderDots(dotColors))
        {
            coloredDots = Enumerable.Range(0, 4).All(i => dots.GetPixel(i % 2 == 0 ? 9 : 23, i < 2 ? 9 : 23).ToArgb() == dotColors[i].ToArgb());
            dots.Save(Path.Combine(directory, "tray-dots.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var liveHosts = vm.Hosts.Select(h => new { h.Name, h.Error, taskCount = h.Tasks.Count, reportCount = h.Tasks.Count(t => t.FinalReport.Length > 0) }).ToArray();
        bool reportTurnsMatch = vm.AllTasks.All(t => t.FinalReport.Length == 0 || t.TurnId.Length > 0 && t.FinalReportTurnId == t.TurnId && t.Status is TaskStatusNames.Completed or TaskStatusNames.Failed or TaskStatusNames.Interrupted);
        int liveTaskCount = vm.AllTasks.Count;
        var liveStatuses = vm.AllTasks.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count());
        // Force a failed local read, verify cached tasks become unknown/offline,
        // then recover. No model or remote service is started or stopped.
        string python = settings.PythonPath;
        var remotes = settings.RemoteHosts;
        settings.RemoteHosts = new();
        settings.PythonPath = Path.Combine(directory, "nonexistent-python.exe");
        await vm.RefreshAsync();
        bool failureHandled = !settings.LocalEnabled || (vm.Hosts.Single().Error != null && vm.AllTasks.All(t => t.Disconnected));
        int recoveryNotifications = 0;
        vm.Notify += _ => recoveryNotifications++;
        settings.PythonPath = python;
        await vm.RefreshAsync();
        bool recovered = vm.Hosts.All(h => h.Error == null) && vm.AllTasks.All(t => !t.Disconnected) && recoveryNotifications == 0;
        settings.RemoteHosts = remotes;
        File.WriteAllText(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(new
        {
            passed = through && restored && layered && search && fluentBackground && compactDimensions && miniHidesDesktopHud && compactRestoresHud && coloredDots && failureHandled && recovered && overviewVisible && reportTurnsMatch && liveHosts.All(h => h.Error == null) && webChecks.Values.All(v => v),
            clickThroughEnabled = through, clickThroughDisabled = restored, layeredPreserved = layered,
            searchFilter = search, fluentBackground, overviewVisible, compactDimensions, hudWidth, hudHeight, miniHidesDesktopHud, compactRestoresHud, coloredDots, webChecks, failureHandled, recovered, taskCount = liveTaskCount,
            hosts = liveHosts, statusCounts = liveStatuses, reportTurnsMatch
        }, Settings.Json));
    }
    private static void SaveImage(Window window, string path)
    {
        var visual = window.Content as FrameworkElement ?? window;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
