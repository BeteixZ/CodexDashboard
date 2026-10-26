using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexDashboard;

internal static class AssistantHostSmoke
{
    internal static async Task RunAsync(string output, AssistantWindow window, Settings settings,
        WeChatService wechat, ChatGptAssistant? model, AssistantTaskMonitor monitor)
    {
        if (settings.WeChat.ClawBotEnabled || settings.WeChat.PushPlusEnabled || settings.ChatGpt.Enabled
            || settings.LocalEnabled || settings.RemoteHosts.Count != 0)
            throw new InvalidOperationException("Host smoke requires an explicitly offline configuration.");
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(window, Path.Combine(output, "assistant.png"));
        var dialog = new AssistantSettingsWindow(settings, wechat, () => { }) { Owner = window };
        dialog.Show(); dialog.UpdateLayout();
        Capture(dialog, Path.Combine(output, "settings.png"));
        dialog.WakeQuietEnabledBox.BringIntoView();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var pushTop = dialog.PushEnabledBox.TransformToAncestor(dialog.WeChatScrollViewer).Transform(new Point()).Y;
        dialog.WeChatScrollViewer.ScrollToVerticalOffset(Math.Max(0, dialog.WeChatScrollViewer.VerticalOffset + pushTop - 12));
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(dialog, Path.Combine(output, "wake-settings.png"));
        checks["wakeQuietDefaultsPreserved"] = dialog.WakeQuietEnabledBox.IsChecked != true
            && dialog.PushEnabledBox.IsChecked != true && settings.WeChat.PushPlusWakeRouteId == Guid.Empty
            && settings.WeChat.TryReadWakeQuietHours(out var quiet, out _) && quiet == null;
        checks["wakeQuietControlsVisible"] = new FrameworkElement[]
            { dialog.WakeQuietStartBox, dialog.WakeQuietEndBox, dialog.WakeQuietZoneText }.All(element =>
            {
                var bounds = element.TransformToAncestor(dialog.WeChatScrollViewer)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return bounds.Top >= 0 && bounds.Bottom <= dialog.WeChatScrollViewer.ViewportHeight;
            });
        dialog.ShowReportSettings();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(dialog, Path.Combine(output, "reports-settings.png"));
        dialog.SettingsTabs.SelectedItem = dialog.SourcesTab;
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(dialog, Path.Combine(output, "data-directory.png"));
        checks["actualDataDirectoryVisible"] = dialog.ActualDataDirectoryBox.Text == Settings.DirectoryPath
            && dialog.ActualDataDirectoryBox.IsReadOnly && dialog.ActualDataDirectoryBox.IsVisible;
        dialog.SettingsTabs.SelectedItem = dialog.ChatGptTab;
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
        dialog.ConcurrencyLimitBox.BringIntoView();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(dialog, Path.Combine(output, "concurrency-settings-minimum.png"));
        checks["concurrencyDefaultPreservesUnlimitedBehavior"] = settings.MaxConcurrentExecutions == null
            && dialog.ConcurrencyEnabledBox.IsChecked == false && dialog.ConcurrencyLimitBox.Text == "2"
            && !dialog.ConcurrencyLimitBox.IsEnabled;
        checks["concurrencySettingReachableAtMinimumSize"] = IsInside(dialog.ConcurrencyEnabledBox, dialog)
            && IsInside(dialog.ConcurrencyLimitBox, dialog);
        dialog.InputMergeWindowBox.BringIntoView();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(dialog, Path.Combine(output, "input-merge-settings-minimum.png"));
        checks["inputMergeDefaultAndControlReachableAtMinimumSize"] = settings.InputMergeWindowMilliseconds == 1000
            && dialog.InputMergeWindowBox.Text == "1000" && IsInside(dialog.InputMergeWindowBox, dialog);
        dialog.InputMergeWindowBox.Text = "3001";
        checks["invalidInputMergeSettingDoesNotMutateLiveSettings"] = !dialog.TryReadInputMergeWindow(out _)
            && settings.InputMergeWindowMilliseconds == 1000 && dialog.InputMergeValidationText.Text.Contains("尚未保存");
        dialog.InputMergeWindowBox.Text = "0";
        checks["zeroInputMergeSettingCanBeReviewedBeforeSave"] = dialog.TryReadInputMergeWindow(out int requestedMerge)
            && requestedMerge == 0 && dialog.InputMergeValidationText.Text == "" && settings.InputMergeWindowMilliseconds == 1000;
        dialog.Close();
        var app = (AssistantApp)Application.Current;
        var collectedBefore = monitor.LastRefreshAt;
        int completedBefore = model?.CompletedRequests ?? 0;
        var health = new AssistantHealthWindow(ct => app.ReadHealthSnapshotAsync(ct), identity => identity == null) { Owner = window };
        health.Show();
        await health.RefreshAsync();
        await health.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(health, Path.Combine(output, "health-offline.png"));
        checks["actualHostDiagnosticsReadOfflineStateWithoutActivatingServices"] =
            health.ConnectionText.Text.Contains("尚未启用或配置") && health.ConnectionText.Text.Contains("问答已关闭")
            && health.RecordsText.Text.Contains("没有载入其他绑定")
            && health.DetailsBox.Text.Contains(Settings.DirectoryPath)
            && monitor.LastRefreshAt == collectedBefore && (model?.CompletedRequests ?? 0) == completedBefore
            && wechat.ConnectionState == Codex.Protocol.ConnectionState.NotConfigured
            && window.HealthButton.IsVisible;
        string healthExportPath = Path.Combine(output, "health-diagnostics.json");
        var exported = health.ExportSnapshot(() => healthExportPath);
        using (var exportedHealth = JsonDocument.Parse(await File.ReadAllTextAsync(healthExportPath)))
        {
            var exportedRoot = exportedHealth.RootElement;
            checks["actualHostDiagnosticsExportUsesExistingSnapshotWithoutPrivateDirectory"] =
                exported == AssistantHealthExportResult.Saved
                && !exportedRoot.TryGetProperty("DataDirectory", out _)
                && !exportedRoot.TryGetProperty("identity", out _)
                && exportedRoot.GetProperty("ledger").GetProperty("state").GetString() == "inactive"
                && exportedRoot.GetProperty("channel").GetProperty("state").GetString() == "not_configured"
                && !exportedRoot.GetRawText().Contains(Settings.DirectoryPath, StringComparison.Ordinal)
                && monitor.LastRefreshAt == collectedBefore && (model?.CompletedRequests ?? 0) == completedBefore
                && wechat.ConnectionState == Codex.Protocol.ConnectionState.NotConfigured;
        }
        health.Close();
        var populatedHealth = new AssistantHealthWindow(_ => Task.FromResult(AssistantHealthTests.Sample()), _ => true) { Owner = window };
        populatedHealth.Show();
        await populatedHealth.RefreshAsync();
        await populatedHealth.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(populatedHealth, Path.Combine(output, "health-populated.png"));
        checks["diagnosticsSeparateUnknownOccupancyAndObservedRunning"] =
            populatedHealth.RecordsText.Text.Contains("执行结果待核对 1 项")
            && populatedHealth.ConcurrencyText.Text.Contains("占用不等于正在运行")
            && populatedHealth.DetailsExpander.IsExpanded == false && populatedHealth.DetailsBox.IsReadOnly;
        populatedHealth.Width = populatedHealth.MinWidth; populatedHealth.Height = populatedHealth.MinHeight;
        populatedHealth.UpdateLayout();
        await populatedHealth.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(populatedHealth, Path.Combine(output, "health-minimum.png"));
        checks["diagnosticsMinimumSizeKeepsRefreshAndScrollableContentReachable"] =
            IsInside(populatedHealth.RefreshButton, populatedHealth) && IsInside(populatedHealth.ResultText, populatedHealth)
            && populatedHealth.DiagnosticScroll.ViewportHeight >= 200
            && populatedHealth.DiagnosticScroll.ScrollableHeight > 0;
        checks["diagnosticsMinimumSizeKeepsExportActionReachable"] =
            IsInside(populatedHealth.ExportButton, populatedHealth) && populatedHealth.ExportButton.IsEnabled;
        populatedHealth.RecordsText.BringIntoView(new Rect(0, Math.Max(0, populatedHealth.RecordsText.ActualHeight - 140),
            populatedHealth.RecordsText.ActualWidth, Math.Min(140, populatedHealth.RecordsText.ActualHeight)));
        await populatedHealth.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var recordsEnd = populatedHealth.RecordsText.TranslatePoint(
            new Point(0, populatedHealth.RecordsText.ActualHeight - 4), populatedHealth.DiagnosticScroll);
        checks["capacityWarningCanBeScrolledIntoMinimumDiagnosticViewport"] =
            recordsEnd.Y >= 0 && recordsEnd.Y <= populatedHealth.DiagnosticScroll.ViewportHeight
            && populatedHealth.RecordsText.Text.Contains("接近容量上限");
        Capture(populatedHealth, Path.Combine(output, "health-capacity-minimum.png"));
        populatedHealth.Width = 760; populatedHealth.Height = 720;
        populatedHealth.DetailsExpander.IsExpanded = true;
        await Task.Delay(350);
        populatedHealth.UpdateLayout(); populatedHealth.DiagnosticScroll.ScrollToEnd();
        await populatedHealth.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Capture(populatedHealth, Path.Combine(output, "health-details.png"));
        populatedHealth.Close();
        var work = new AssistantWorkWindow(_ => Task.FromResult(new AssistantWorkspaceSnapshot(
            "已有任务可以按当前请求接续。自建任务的控制仍需要对应运行验收。\n打开此页不会连接 Codex。\n\n"
                + AssistantChannelCapabilities.Describe(false, false, false, Codex.Protocol.ConnectionState.NotConfigured),
            "当前交流目标：local / synthetic-layout-thread", "合成展示数据，仅用于界面检查。", DateTimeOffset.UtcNow,
            [new("synthetic-operation", "原任务接续", "等待核对最新要求", "本机 / Synthetic 项目 / Alpha", "10-21 03:00:00")],
            [new("b93af2c66d1047ae98068e17969bfc01", "中断", "已受理，尚未确认执行结果", "local / synthetic-layout-thread", "10-21 03:00:00")],
            [new("synthetic-outbox", "主动通知", "渠道接口已受理，手机送达未核实", "这项工作的本轮处理已经结束。", "10-21 03:00:00")],
            [new("2bd1199c6d0647ae98068e17969bfc02", "一次性提醒", "等待触发",
                "查看测试结果 · 2026-10-21 09:00:00 +08:00 (Taipei Standard Time)", "10-21 03:00:00")],
            [new("synthetic-history-input", "你", "回复已交给发送记录", "请解释已有的测试结果。\n保留尚未验证的地方。",
                "local / synthetic-layout-thread", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-2), "接收时间"),
             new("synthetic-history-unknown", "助手回复", "发送结果未知，不自动重发",
                "隔离检查已通过。真实手机收信还没有验证，因此暂时不能说手机已收到。\n这段正文保留在原记录里，可以查看完整内容。",
                "local / synthetic-layout-thread", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "准备时间")],
            ConversationRecordCount: 2)))
            { Owner = window };
        work.Show();
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "commitment-records.png"));
        checks["workspaceReadOnly"] = work.WorkGrid.IsReadOnly && work.OperationsGrid.IsReadOnly && work.MessagesGrid.IsReadOnly
            && work.CommitmentsGrid.IsReadOnly && work.CommitmentsGrid.Items.Count == 1
            && work.WorkGrid.Items.Count == 1 && work.OperationsGrid.Items.Count == 1 && work.MessagesGrid.Items.Count == 1;
        work.WorkTabs.SelectedIndex = 1;
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "work-records.png"));
        work.WorkTabs.SelectedIndex = 3;
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "delivery-records.png"));
        checks["deliveryNotClaimed"] = work.MessagesGrid.Items.Cast<AssistantWorkspaceRow>().Single().State.Contains("手机送达未核实");
        work.WorkTabs.SelectedIndex = 4;
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "channel-capabilities.png"));
        work.ConversationTab.IsSelected = true;
        work.ConversationGrid.SelectedIndex = 1;
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "conversation-history.png"));
        checks["conversationHistoryIsReadOnlyWithFullOriginalBody"] = work.ConversationGrid.IsReadOnly
            && work.ConversationDetailBox.IsReadOnly && work.ConversationGrid.Items.Count == 2
            && work.ConversationDetailBox.Text.StartsWith("隔离检查已通过。真实手机收信还没有验证")
            && work.ConversationDetailBox.Text.Contains("这段正文保留在原记录里，可以查看完整内容。")
            && work.ConversationDetailBox.Text.Contains("发送结果未知，不自动重发");
        work.Width = work.MinWidth; work.Height = work.MinHeight; work.UpdateLayout();
        await work.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Capture(work, Path.Combine(output, "conversation-history-minimum.png"));
        checks["conversationHistoryMinimumLayoutKeepsListAndBodyReachable"] =
            work.ConversationGrid.ActualHeight > 60 && work.ConversationDetailBox.ActualHeight > 60
            && work.ConversationDetailBox.TransformToAncestor(work).Transform(new Point(0, work.ConversationDetailBox.ActualHeight)).Y
                <= work.ActualHeight;
        work.Close();
        if (model != null)
        {
            var entry = model.Memory.AddFromEditor("合成界面检查：平时用简洁中文回复。");
            var memory = new AssistantMemoryWindow(model, settings) { Owner = window };
            memory.Show(); memory.MemoryTab.IsSelected = true;
            memory.MemoryList.SelectedItem = memory.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(item => item.Id == entry.Id);
            await memory.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(memory, Path.Combine(output, "memory-source-summary.png"));
            var details = model.Memory.SourceDetails(entry.Id)!;
            var source = new AssistantMemorySourceWindow(details.Detail) { Owner = memory };
            source.Show();
            await source.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(source, Path.Combine(output, "memory-source-detail.png"));
            checks["memorySourceIsActualEditorRecord"] = details.Known && details.Provenance?.SourceKind == "desktop_editor"
                && memory.MemorySourceSummary.Text == entry.SourceSummary && source.DetailsBox.IsReadOnly
                && source.DetailsBox.Text == details.Detail;
            source.Close();
            var second = model.Memory.AddFromEditor("合成界面检查：遇到不确定结果时说明已验证的事实。");
            const string summaryText = "平时简洁地用中文交流，遇到不确定结果时明确说明已经验证的事实。";
            var created = RunSummaryEditor(memory, memory.SummaryMemoryButton, editor =>
            {
                checks["summaryStartsWithoutGeneratedText"] = editor.BodyBox.Text.Length == 0
                    && editor.SavedEntry == null && editor.ReviewedBox.IsChecked != true && !editor.SaveButton.IsEnabled;
                foreach (ListBoxItem row in editor.SourceList.Items)
                    row.IsSelected = row.Tag is AssistantMemoryEntry item && (item.Id == entry.Id || item.Id == second.Id);
                editor.BodyBox.Text = summaryText;
                checks["summaryRequiresPersonalReview"] = !editor.SaveButton.IsEnabled;
                editor.ReviewedBox.IsChecked = true;
                checks["summaryTwoSelectedSourcesCanSave"] = editor.SourceList.SelectedItems.Count == 2 && editor.SaveButton.IsEnabled;
                Capture(editor, Path.Combine(output, "memory-summary-editor.png"));
                editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; editor.UpdateLayout();
                Capture(editor, Path.Combine(output, "memory-summary-editor-minimum.png"));
                checks["summaryMinimumControlsVisible"] = new FrameworkElement[] { editor.SourceList, editor.BodyBox,
                    editor.ReviewedBox, editor.SaveButton, editor.StatusText }.All(element => IsInside(element, editor))
                    && editor.SourceList.ActualHeight >= 60 && editor.BodyBox.ActualHeight >= 60
                    && editor.ReviewedBox.Foreground is SolidColorBrush reviewText && editor.BodyBox.Foreground is SolidColorBrush bodyText
                    && reviewText.Color == bodyText.Color;
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var summary = created.SavedEntry ?? throw new InvalidOperationException("Synthetic summary editor did not save.");
            checks["summaryActualDialogCreatesOnlyReviewedSummary"] = summary.Text == summaryText
                && summary.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary
                && summary.Metadata.Confirmation == AssistantMemoryConfirmation.Explicit
                && summary.Metadata.Dependencies.Select(reference => reference.Id).Order().SequenceEqual(new[] { entry.Id, second.Id }.Order())
                && model.Memory.Entries.Count == 3 && model.CompletedRequests == 0;
            checks["summaryManagementUsesDedicatedCorrection"] = memory.MemoryList.SelectedItem is AssistantMemoryEntry selected
                && selected.Id == summary.Id && memory.MemoryText.IsReadOnly && !memory.AddMemoryButton.IsEnabled
                && Equals(memory.UpdateMemoryButton.Content, "纠正所选摘要…");
            memory.Width = memory.MinWidth; memory.Height = memory.MinHeight; memory.UpdateLayout();
            memory.UpdateMemoryButton.BringIntoView();
            await memory.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(memory, Path.Combine(output, "memory-summary-management-minimum.png"));
            checks["summaryManagementActionsReachableAtMinimumSize"] = memory.MemoryList.ActualHeight >= 90
                && new FrameworkElement[] { memory.SummaryMemoryButton, memory.UpdateMemoryButton, memory.DeleteMemoryButton }
                    .All(element => IsInside(element, memory.MemoryScrollViewer));
            var corrected = RunSummaryEditor(memory, memory.UpdateMemoryButton, editor =>
            {
                checks["summaryCorrectionKeepsExactSourcesAndRequiresReview"] = editor.BodyBox.Text == summaryText
                    && editor.SourceList.SelectedItems.Cast<ListBoxItem>().Select(row => ((AssistantMemoryEntry)row.Tag).Id)
                        .Order().SequenceEqual(new[] { entry.Id, second.Id }.Order())
                    && editor.ReviewedBox.IsChecked != true && !editor.SaveButton.IsEnabled;
                editor.BodyBox.Text = "用简洁中文交流；存在不确定性时，区分已验证事实与尚待核对的部分。";
                editor.ReviewedBox.IsChecked = true;
                Action failingObserver = () => throw new IOException("synthetic_summary_committed_refresh_fault");
                model.Changed += failingObserver;
                try { editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { model.Changed -= failingObserver; }
            });
            checks["summaryCorrectionAdvancesVersion"] = corrected.SavedEntry?.Id == summary.Id
                && corrected.SavedEntry.Metadata?.Version == summary.Metadata!.Version + 1
                && model.Memory.Entries.Count == 3;
            checks["summaryPostCommitWarningStillReportsSavedId"] = !string.IsNullOrWhiteSpace(corrected.SavedWarning)
                && memory.MemoryStatus.Text.Contains("已保存", StringComparison.Ordinal)
                && memory.MemoryStatus.Text.Contains(summary.Id, StringComparison.Ordinal)
                && memory.MemoryStatus.Text == corrected.SavedWarning;
            memory.DeleteMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["summaryDeletePreservesOriginalSources"] = model.Memory.Entries.Count == 2
                && model.Memory.Entries.All(item => item.Id == entry.Id || item.Id == second.Id)
                && model.CompletedRequests == 0;
            memory.Close();
        }
        var binding = new WeChatBindingWindow(true) { Owner = window };
        binding.Show(); binding.UpdateLayout();
        Capture(binding, Path.Combine(output, "binding-preview.png"));
        binding.Close();
        checks["noDashboardAssembly"] = !typeof(AssistantApp).Assembly.GetReferencedAssemblies().Any(name => name.Name == "CodexDashboard");
        checks["ownsCollectorScript"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "collector.py"));
        checks["ownsRemoteProxy"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "remote_socket_proxy.py"));
        checks["offlineSnapshot"] = monitor.AllTasks.Count == 0 && monitor.Hosts.Count == 0 && monitor.LastRefreshAt.HasValue;
        checks["channelNotStarted"] = wechat.ConnectionState == Codex.Protocol.ConnectionState.NotConfigured;
        checks["modelConfiguredWithoutNetwork"] = model != null && model.CompletedRequests == 0 && model.Auth.Accounts.Count == 0;
        string json = JsonSerializer.Serialize(settings, Settings.Json);
        checks["noDashboardSettings"] = !json.Contains("\"hud") && !json.Contains("\"web");
        checks["independentDataFile"] = Settings.FilePath == Path.Combine(Settings.DirectoryPath, "settings.json") && File.Exists(Settings.FilePath);
        checks["windowContent"] = window.IsVisible && window.ActualWidth >= 520 && window.ActualHeight >= 370;
        checks["footerVisible"] = window.FooterText.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, window.FooterText.ActualWidth, window.FooterText.ActualHeight)).Bottom <= window.ActualHeight;
        await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
        if (checks.Values.Any(value => !value)) throw new InvalidOperationException("Assistant host smoke failed.");
    }
    private static SummaryMemoryEditorWindow RunSummaryEditor(AssistantMemoryWindow owner, Button command,
        Action<SummaryMemoryEditorWindow> interact)
    {
        SummaryMemoryEditorWindow? editor = null;
        Exception? failure = null;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                editor = Application.Current.Windows.OfType<SummaryMemoryEditorWindow>().Single(window => window.Owner == owner);
                interact(editor);
                if (editor.IsVisible) throw new InvalidOperationException("Synthetic summary save did not close the dialog.");
            }
            catch (Exception ex) { failure = ex; if (editor?.IsVisible == true) editor.Close(); }
        }));
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure != null) throw new InvalidOperationException("Synthetic summary dialog interaction failed.", failure);
        return editor ?? throw new InvalidOperationException("Summary management did not open its editor.");
    }
    private static bool IsInside(FrameworkElement element, FrameworkElement ancestor)
    {
        var bounds = element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0
            && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= ancestor.ActualWidth + 1 && bounds.Bottom <= ancestor.ActualHeight + 1;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        image.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
