using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Codex.Protocol;
using Codex.Migration;

namespace CodexDashboard;

public partial class AssistantApp : Application
{
    private Settings settings = null!;
    private AssistantTaskMonitor monitor = null!;
    private AssistantWindow window = null!;
    private AssistantTrayService? tray;
    private ChatGptAssistant? model;
    private WeChatService? wechat;
    private TaskTransport? transport;
    private ICodexGateway? gateway;
    private DurableAssistantRuntime? durable;
    private TaskControlQueue? queue;
    private WorkspaceWriteCoordinator? workspaceWrites;
    private DispatcherTimer? timer;
    private ProductStatusBridge? status;
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? singleton;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;
    private EventWaitHandle? exitRequest;
    private RegisteredWaitHandle? exitWait;
    private IDisposable? runtimeLease;
    private Task? stopping;
    private TaskCompletionSource? refreshCompletion;
    private bool exiting, refreshing;
    private OwnedControlRuntime? countedOwnedRuntime;
    private string? ownedTaskSummary;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--request-exit"))
        {
            if (e.Args is not ["--request-exit", "--data-dir", var exitDirectory]
                || !Path.IsPathFullyQualified(exitDirectory)) { Shutdown(2); return; }
            try
            {
                using var target = EventWaitHandle.OpenExisting(ExitEventName(exitDirectory));
                target.Set(); Shutdown(0);
            }
            catch (WaitHandleCannotBeOpenedException) { Shutdown(3); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Exit request failed: " + ex.GetType().Name); Shutdown(2); }
            return;
        }
        if (e.Args.Contains("--hold-recovery"))
        {
            try
            {
                if (e.Args is not ["--hold-recovery", var directory]) { Shutdown(2); return; }
                MigrationStartup.HoldForRecovery(directory); Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine("Recovery hold failed: " + ex.GetType().Name); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--review-recovery"))
        {
            if (e.Args is not ["--review-recovery", var source]) { Shutdown(2); return; }
            try
            {
                var report = await Task.Run(() => AssistantLedgerInspection.ReadAsync(source, requireHold: true));
                var review = new RecoveryReviewWindow(report);
                MainWindow = review;
                review.ShowDialog(); Shutdown();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Recovery review failed: " + ex.GetType().Name);
                MessageBox.Show("无法审阅。请使用准备恢复工具生成的暂停副本，并确认没有其他程序正在写入。副本未被修改。", "恢复副本审阅");
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Contains("--inspect-ledger") || e.Args.Contains("--verify-recovery-report") || e.Args.Contains("--compare-recovery-reports"))
        {
            Shutdown(await AssistantLedgerInspection.RunAsync(e.Args));
            return;
        }
        // Preview is an independent read-only entry point: no application data
        // directory resolution, settings, credentials, receiver or model startup.
        if (e.Args.Contains("--migration-preview"))
        {
            if (e.Args.Length != 1)
            {
                Console.Error.WriteLine("Migration preview accepts no additional startup options.");
                Shutdown(2); return;
            }
            try
            {
                var preview = new MigrationPreviewWindow();
                MainWindow = preview;
                preview.ShowDialog();
                Shutdown();
            }
            catch (Exception ex) { Console.Error.WriteLine("Migration preview failed: " + ex.GetType().Name); Shutdown(1); }
            return;
        }
        if (ProductLifecycleTest.IsRequested(e.Args))
        {
            Shutdown(await ProductLifecycleTest.RunAsync(e.Args, ProtocolProduct.Assistant));
            return;
        }
        try
        {
            int dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dataIndex >= 0)
            {
                if (dataIndex + 1 >= e.Args.Length) throw new ArgumentException("缺少数据目录。");
                Settings.SetDataDirectory(e.Args[dataIndex + 1]);
            }
            // Existing isolated test entry points run before configuration/authentication.
            if (await AssistantTestDispatch.TryRunAsync(e.Args, Shutdown)) return;
            var startupFlags = new[] { "--data-dir", "--background", "--host-smoke-test", "--protocol-profile",
                "--chatgpt-connect", "--wechat-settings" };
            if (e.Args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) && !startupFlags.Contains(arg)))
            {
                Console.Error.WriteLine("Unrecognized startup option; no settings or credentials were opened.");
                Shutdown(2); return;
            }
            string key = TextSafety.Hash(Settings.DirectoryPath.ToUpperInvariant())[..24];
            string instanceName = "Local\\CodexAssistant." + key;
            singleton = new Mutex(true, instanceName, out bool created);
            if (!created)
            {
                try { using var other = EventWaitHandle.OpenExisting(instanceName + ".Activate"); other.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                singleton.Dispose(); singleton = null; Shutdown(); return;
            }
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Activate");
            activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(ShowAssistant), null, Timeout.Infinite, false);
            // This lease also prevents a migration from changing files under a running host.
            runtimeLease = MigrationStartup.AcquireRuntimeLease(Settings.DirectoryPath);
            settings = Settings.Load();
            _ = settings.ActiveMedia;
            if (e.Args.Contains("--host-smoke-test") && (dataIndex < 0 || settings.LocalEnabled
                || settings.RemoteHosts.Count != 0 || settings.ChatGpt.Enabled
                || settings.WeChat.ClawBotEnabled || settings.WeChat.PushPlusEnabled))
                throw new InvalidOperationException("Host smoke requires isolated offline settings.");
            monitor = new(settings);
            try
            {
                model = new(settings, SaveSettings, persistConfigurationCandidate: candidate => candidate.Save());
                model.Changed += PublishStatus;
                transport = new(settings);
                gateway = new CodexGateway(settings, monitor.Snapshot, transport);
                workspaceWrites = new(recoverySources: ["legacy", "owned"],
                    maxConcurrentExecutions: settings.MaxConcurrentExecutions);
                queue = new(settings, gateway, Path.Combine(Settings.DirectoryPath, "task-control.state"), workspace: workspaceWrites);
                workspaceWrites.RefreshExternalEvidence = () => queue.RefreshWorkspace(monitor.Snapshot());
                model.Changed += ModelChanged;
                queue.Notify = (receipt, ct) => Dispatcher.InvokeAsync(
                    () => wechat?.SendControlReceiptAsync(receipt, ct) ?? Task.CompletedTask,
                    DispatcherPriority.Normal, ct).Task.Unwrap();
            }
            catch (Exception)
            {
                settings.ChatGpt.TaskControlEnabled = false;
                model?.ReportError(new IOException("任务控制记录或模型凭据无法读取；原文件保持不变。"));
            }
            wechat = new(settings, monitor.Snapshot, SaveSettings, assistant: model, summarizer: model, requireDurableAssistant: true);
            if (model != null && queue != null && gateway != null)
            {
                try
                {
                    durable = await DurableAssistantRuntime.OpenAsync(settings, model, queue, gateway, wechat, Dispatcher,
                        monitor.Snapshot, workspaceWrites);
                    wechat.DurableRuntime = durable;
                }
                catch (Exception)
                {
                    model.ReportError(new IOException("持久对话记录无法读取；原文件保持不变，微信收件暂停。"));
                }
            }
            wechat.Changed += PublishStatus;
            wechat.CompletionPrepared += _ => PublishStatus();
            monitor.Notify += wechat.ObserveAsync;
            string profile = "default";
            int profileIndex = Array.IndexOf(e.Args, "--protocol-profile");
            if (profileIndex >= 0 && profileIndex + 1 < e.Args.Length) profile = e.Args[profileIndex + 1];
            else if (dataIndex >= 0) profile = key;
            status = new(ProtocolProduct.Assistant, profile);
            status.Start();
            window = new(this); MainWindow = window;
            tray = new(ShowAssistant, ExitAssistant);
            if (!e.Args.Contains("--background")) window.Show();
            await wechat.ConfigureAsync();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.RefreshSeconds) };
            timer.Tick += async (_, _) => await RefreshAsync();
            timer.Start();
            await RefreshAsync();
            SaveSettings();
            exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName(Settings.DirectoryPath));
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitRequest,
                (_, _) => Dispatcher.BeginInvoke(ExitAssistant), null, Timeout.Infinite, true);
            if (e.Args.Contains("--host-smoke-test"))
            {
                int index = Array.IndexOf(e.Args, "--host-smoke-test");
                await AssistantHostSmoke.RunAsync(e.Args[index + 1], window, settings, wechat, model, monitor);
                await StopAsync(); Shutdown(); return;
            }
            if (e.Args.Contains("--chatgpt-connect")) ShowModel(true);
            else if (e.Args.Contains("--wechat-settings")) ShowSettings();
        }
        catch (Exception ex)
        {
            // Do not include settings, credential text, or raw protocol bodies in diagnostics.
            try
            {
                Directory.CreateDirectory(Settings.DirectoryPath);
                File.WriteAllText(Path.Combine(Settings.DirectoryPath, "startup-error.txt"), ex.GetType().Name);
            }
            catch { }
            if (!e.Args.Contains("--background")) MessageBox.Show("助手启动失败，原配置保持不变。请检查数据目录及本机日志。", "Codex 助手");
            try { await StopAsync(); } catch { /* Process exit retains the original failure and releases OS handles. */ }
            Shutdown(1);
        }
    }
    private async Task RefreshAsync()
    {
        if (exiting || refreshing) return;
        refreshing = true;
        // The collector can return after cancellation, and its awaited Notify
        // delegates can still use the channel and ledger. Own the whole refresh.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshCompletion = completed;
        try
        {
            await monitor.RefreshAsync();
            if (exiting) return;
            if (durable?.OwnedRuntime is { } owned)
            {
                var catalogue = await owned.ReadCatalogueAsync(lifetime.Token);
                if (durable.OwnedRuntime == owned)
                {
                    countedOwnedRuntime = owned;
                    ownedTaskSummary = $"助手自建：{catalogue.Tasks.Count} 条任务记录（含历史）"
                        + (catalogue.UnavailableConfigurations.Count > 0 ? " · 部分范围暂不可读" : "");
                }
            }
            wechat?.PublishReports();
            if (durable?.Identity != null && queue != null) await queue.PumpAsync(monitor.Snapshot(), lifetime.Token);
            if (transport != null && wechat != null)
            {
                foreach (var notice in transport.Remote.DrainAttention())
                {
                    if (!settings.ChatGpt.Enabled || !settings.ChatGpt.TaskControlEnabled || notice.Scope == null) continue;
                    var task = monitor.AllTasks.FirstOrDefault(t => t.Key == notice.TaskKey);
                    if (task != null)
                    {
                        var receipt = notice.ToReceipt(WeChatText.Clean(task.Title, 100)
                            + " 正在等待审批或补充输入，请在 Codex 中处理。");
                        if (receipt != null) await wechat.SendControlReceiptAsync(receipt, lifetime.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { model?.ReportError(ex, "refresh"); }
        finally
        {
            refreshing = false;
            try { PublishStatus(); }
            finally { completed.TrySetResult(); }
        }
    }
    private void PublishStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(PublishStatus); return; }
        if (settings == null) return;
        var now = DateTimeOffset.UtcNow;
        var assistant = new AssistantStatus
        {
            ObservedAt = now,
            WeChat = wechat?.ConnectionState ?? ConnectionState.Unknown,
            Model = !settings.ChatGpt.Enabled ? Availability.Unavailable
                : model?.LastFailure != null ? Availability.Degraded
                : model?.LastCompletedAt != null ? Availability.Available : Availability.Unknown,
            QueuedCount = queue?.Entries.Count(entry => entry.State == "queued") ?? 0,
            AttentionCount = queue?.Entries.Count(entry => entry.State is "unconfirmed" or "failed" or "held_constraints_changed" or "held_target_changed") ?? 0
        };
        status?.Publish(new() { ObservedAt = now, Readiness = Availability.Available }, assistant);
        if (window != null) window.Update(wechat?.ClawStatus ?? "微信尚未启动", model?.Status ?? "模型未配置",
            monitor?.AllTasks.Count ?? 0, assistant.QueuedCount, monitor?.LastRefreshAt, status?.EndpointFailure,
            countedOwnedRuntime == durable?.OwnedRuntime ? ownedTaskSummary : null);
    }
    private void SaveSettings()
    {
        if (settings == null) return;
        settings.Save();
        if (exiting) return; // Final receiver cursor may persist, but stopped runtime services are not reconfigured.
        workspaceWrites?.SetLimit(settings.MaxConcurrentExecutions);
        durable?.ApplyInputMergeWindow(settings.InputMergeWindowMilliseconds);
        PublishStatus();
    }
    private async void ModelChanged()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(ModelChanged); return; }
        if (exiting || wechat == null) return;
        try { await wechat.ConfigureAsync(); }
        catch (Exception) { /* The channel keeps its failed/stopping state and the old ownership lease. */ }
        PublishStatus();
    }
    internal void ShowAssistant()
    {
        if (exiting || window == null) return;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }
    internal void ShowSettings()
    {
        if (exiting || wechat == null) return;
        var dialog = new AssistantSettingsWindow(settings, wechat, () => ShowModel(), CommitSettingsEdit) { Owner = window };
        if (dialog.ShowDialog() == true) _ = ApplySettingsAsync();
    }
    private string? CommitSettingsEdit(AssistantSettingsEdit edit)
    {
        if (!Dispatcher.CheckAccess() || exiting) return "助手正在退出，设置尚未保存。请下次启动后再修改。";
        try { AssistantSettingsTransaction.Commit(settings, edit); }
        catch (Exception error)
        {
            model?.ReportError(error, "settings_save");
            return "设置尚未保存，请检查数据目录后重试。当前运行设置保持不变。";
        }
        // No await separates persistence, publication of edited fields and these setters.
        workspaceWrites?.SetLimit(settings.MaxConcurrentExecutions);
        durable?.ApplyInputMergeWindow(settings.InputMergeWindowMilliseconds);
        PublishStatus();
        return null;
    }
    private async Task ApplySettingsAsync()
    {
        try
        {
            if (exiting) return;
            if (timer != null) timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
            if (wechat != null) await wechat.ConfigureAsync();
            if (!exiting) await RefreshAsync();
        }
        catch (Exception ex) { model?.ReportError(ex); PublishStatus(); }
    }
    internal void ShowModel(bool connect = false)
    {
        if (model == null) { MessageBox.Show(window, "模型凭据无法读取，请修复或恢复助手数据目录中的原文件。", "Codex 助手"); return; }
        new ChatGptWindow(model, settings, connect) { Owner = window }.ShowDialog();
        PublishStatus();
    }
    internal async Task SelectArtifactAsync()
    {
        if (durable?.CanSelectArtifact != true || durable.Identity is not { } identity)
        { MessageBox.Show(window, "请先连接微信，再准备文件。", "准备文件"); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要准备的文件", Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(window) != true) return;
        try
        {
            var choices = durable.ArtifactTaskChoices();
            var chooser = new System.Windows.Controls.ComboBox { Margin = new Thickness(0,12,0,12) };
            chooser.Items.Add("不关联任务");
            foreach (var choice in choices) chooser.Items.Add(choice.Record.Title + " · " + choice.Identity.HostId + " · " + choice.Identity.ThreadId);
            chooser.SelectedIndex = 0;
            var selectionPanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
            selectionPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "可以为文件记录一个来源任务。这是你选择的关联，不代表已经核验文件由该任务生成。", TextWrapping = TextWrapping.Wrap });
            selectionPanel.Children.Add(chooser);
            var selectionWindow = new Window { Owner = window, Title = "文件来源", Width = 560, SizeToContent = SizeToContent.Height, Content = selectionPanel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var confirm = new System.Windows.Controls.Button { Content = "保存文件快照", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Left };
            confirm.Click += (_, _) => selectionWindow.DialogResult = true;
            selectionPanel.Children.Add(confirm);
            if (selectionWindow.ShowDialog() != true) return;
            Assistant.Core.TaskReference? sourceTask = chooser.SelectedIndex > 0
                ? new(choices[chooser.SelectedIndex - 1].Identity.HostId, choices[chooser.SelectedIndex - 1].Identity.ThreadId,
                    choices[chooser.SelectedIndex - 1].Record.TurnId, choices[chooser.SelectedIndex - 1].Identity.ProfileId) : null;
            var selected = await durable.ImportArtifactAsync(identity, picker.FileName, CancellationToken.None, sourceTask);
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "已保存文件快照：" + selected.Name + "（" + selected.Length + " 字节）\n尚未发送。在微信发“待发送文件”查看，再按微信列表序号发送，例如“发送文件 1”。", TextWrapping = TextWrapping.Wrap });
            var copy = new System.Windows.Controls.Button { Content = "复制微信查找指令", HorizontalAlignment = HorizontalAlignment.Left };
            copy.Click += (_, _) => Clipboard.SetText(PreparedFilesWindow.LookupCommand(selected.Name));
            panel.Children.Add(copy);
            new Window { Owner = window, Title = "文件已准备", Width = 520, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Assistant.Core.LedgerException)
        { MessageBox.Show(window, "文件未完成准备，请检查文件是否可读、是否超过 8 MiB，以及微信连接是否仍有效。", "准备文件"); }
    }
    internal bool FileFeaturesEnabled => settings.ActiveMedia.Files;
    internal void ShowPreparedFiles()
    {
        if (durable?.CanSelectArtifact != true || durable.Identity is not { } identity)
        { MessageBox.Show(window, "请先连接微信并启用文件准备功能。", "已准备文件"); return; }
        var runtime = durable;
        var records = new PreparedFilesWindow((page, query, ct) => runtime.ReadPreparedFilesAsync(identity, page, query, ct),
            () => ReferenceEquals(runtime, durable) && runtime.IsCurrentBinding(identity)) { Owner = window };
        void Changed() => records.InvalidateIfIdentityChanged();
        if (wechat != null) wechat.Changed += Changed;
        if (model != null) model.Changed += Changed;
        try { records.ShowDialog(); }
        finally
        {
            if (wechat != null) wechat.Changed -= Changed;
            if (model != null) model.Changed -= Changed;
        }
    }
    internal void ShowWork()
    {
        if (durable == null)
        {
            MessageBox.Show(window, "持久记录尚未就绪。请先在连接设置中核对实际数据目录及连接状态。", "工作与记录");
            return;
        }
        var records = new AssistantWorkWindow(durable.ReadWorkspaceAsync,
            identity => identity == null ? durable.Identity == null
                : ReferenceEquals(identity, durable.Identity) && durable.IsCurrentBinding(identity),
            durable.ReadConversationPageAsync, durable.ReadConversationExportAsync, durable.SearchConversationAsync) { Owner = window };
        void Changed() => records.InvalidateIfIdentityChanged();
        if (wechat != null) wechat.Changed += Changed;
        if (model != null) model.Changed += Changed;
        try { records.ShowDialog(); }
        finally
        {
            if (wechat != null) wechat.Changed -= Changed;
            if (model != null) model.Changed -= Changed;
        }
    }
    internal void ShowMigrationPreview()
    {
        if (!exiting) new MigrationPreviewWindow { Owner = window }.ShowDialog();
    }
    internal bool Exiting => exiting;
    private static string ExitEventName(string directory) => "Local\\CodexAssistant."
        + TextSafety.Hash(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant())[..24] + ".Exit";
    internal async void ExitAssistant()
    {
        try { await StopAsync(); Shutdown(); }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Settings.DirectoryPath, "shutdown-error.txt"), ex.GetType().Name); } catch { }
            Shutdown(1);
        }
    }
    private Task StopAsync() => stopping ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        exiting = true; lifetime.Cancel(); timer?.Stop(); monitor?.Dispose();
        exitWait?.Unregister(null); exitRequest?.Dispose(); exitRequest = null;
        tray?.Dispose(); tray = null;
        // Await on the Dispatcher so in-flight Notify/queue callbacks can finish.
        // Cancellation alone does not mean the collector or callbacks have stopped.
        if (refreshCompletion != null) await refreshCompletion.Task;
        if (wechat != null)
        {
            await wechat.DisposeAsync();
            // Dispose may return after a bounded wait. Its dependencies and both
            // ownership leases must remain alive until actual work has stopped.
            await wechat.WhenStopped;
            wechat = null;
        }
        if (durable != null) { await durable.DisposeAsync(); durable = null; }
        if (queue != null) { await queue.DisposeAsync(); queue = null; }
        if (transport != null) { await transport.DisposeAsync(); transport = null; }
        model?.Dispose(); model = null;
        if (status != null) { await status.DisposeAsync(); status = null; }
        activationWait?.Unregister(null); activate?.Dispose();
        if (singleton != null) { singleton.ReleaseMutex(); singleton.Dispose(); singleton = null; }
        runtimeLease?.Dispose(); runtimeLease = null;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel(); timer?.Stop(); tray?.Dispose(); monitor?.Dispose();
        base.OnExit(e);
    }
}
