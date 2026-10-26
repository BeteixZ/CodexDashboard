using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

// Real AssistantApp, settings modal, receiver and durable storage. Only the
// collector, HTTP transport and model are synthetic; no production data is read.
internal static class InputSettingsLifecycleTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--input-settings-lifecycle-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--input-settings-lifecycle-test") throw new ArgumentException("new_output_required");
            string directory = args[1];
            if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
                throw new ArgumentException("new_absolute_output_required");
            for (var part = new DirectoryInfo(directory); part != null; part = part.Parent)
                if (part.Exists && part.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
            Directory.CreateDirectory(directory);
            Settings.SetDataDirectory(Path.Combine(directory, "settings"));
            await ((AssistantApp)Application.Current).RunInputSettingsLifecycleChecksAsync(directory); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    internal sealed class Collector : IHostCollector
    {
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken cancellationToken)
        {
            if (Block)
            {
                using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult(); await Release.Task; // Deliberately returns after cancellation.
            }
            return new() { Name = host, LastSuccess = DateTimeOffset.UtcNow, Tasks = [] };
        }
    }
    internal static int ActualWindow(DurableAssistantRuntime runtime)
    {
        var coordinator = typeof(DurableAssistantRuntime).GetField("coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        long ticks = (long)coordinator.GetType().GetField("mergeWindowTicks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        return (int)TimeSpan.FromTicks(ticks).TotalMilliseconds;
    }
    internal static Button SaveButton(AssistantSettingsWindow dialog)
    {
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var child = VisualTreeHelper.GetChild(node, index); yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        return Descendants(dialog).OfType<Button>().Single(button => button.Content?.ToString() == "保存设置");
    }
    internal static void ClickSave(AssistantSettingsWindow dialog)
        => SaveButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal static bool Inside(FrameworkElement element, Window window)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    internal static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
}

public partial class AssistantApp
{
    internal async Task RunInputSettingsLifecycleChecksAsync(string directory)
    {
        if (settings != null || !Dispatcher.CheckAccess()) throw new InvalidOperationException("isolated_startup_required");
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "input-settings-lifecycle-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(item => !item.Value), actualApplication = true,
                actualSettingsWindow = true, actualReceiver = true, networkUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        AssistantSettingsTransactionTests.Run(Check);
        static Settings OnDisk() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath), Settings.Json)!;
        var source = new InputSettingsLifecycleTests.Collector();
        int finalSaves = 0; bool lateRuntimeAlive = true; bool finalSetterUntouched = true;
        var fixture = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"),
            modelEnabled: false, receiveMessages: true, productionMergeWindowMilliseconds: 1000,
            channelPersistAction: () =>
            {
                bool stoppingNow = exiting;
                if (stoppingNow) { finalSaves++; lateRuntimeAlive &= durable != null; }
                SaveSettings();
                if (stoppingNow) finalSetterUntouched &= durable != null && InputSettingsLifecycleTests.ActualWindow(durable) == 500;
            });
        settings = fixture.Settings; model = fixture.Model; wechat = fixture.Channel; durable = fixture.Runtime;
        queue = fixture.Queue; monitor = new(settings, source);
        var identity = fixture.Runtime.Identity!;
        Task? refreshingTask = null, stopTask = null;
        try
        {
            SaveSettings();
            await InputSettingsLifecycleTests.UntilAsync(() => Task.FromResult(fixture.StartHints == 1 && fixture.PollCursors.Count > 0));
            Check("initial_setting_is_1000_in_disk_live_and_actual_coordinator", OnDisk().InputMergeWindowMilliseconds == 1000
                && settings.InputMergeWindowMilliseconds == 1000 && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 1000);
            string initialCipher = settings.WeChat.ProtectedBinding;
            string latestCipher = "";
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = new AssistantSettingsWindow(settings, wechat, () => throw new InvalidOperationException("model_ui_not_used"), CommitSettingsEdit)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            dialog.InputMergeWindowBox.Text = "3000";
            Check("archive_option_opens_off_without_mutating_settings", dialog.HistoryArchiveBox.IsChecked == false && !settings.HistoryArchiveEnabled);
            dialog.HistoryArchiveBox.IsChecked = true;
            var activeMediaBefore = settings.ActiveMedia;
            Check("media_controls_open_with_saved_defaults", dialog.MediaAttachmentsBox.IsChecked == false && dialog.MediaImagesBox.IsChecked == false
                && dialog.MediaFilesBox.IsChecked == false && dialog.MediaTypingBox.IsChecked == false);
            dialog.MediaAttachmentsBox.IsChecked = dialog.MediaImagesBox.IsChecked = dialog.MediaFilesBox.IsChecked = dialog.MediaTypingBox.IsChecked = true;
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    fixture.PollResponses.Writer.TryWrite(fixture.Batch("settings-open-input", "状态", "SYNTHETIC_CURSOR_WHILE_OPEN", "SYNTHETIC_CONTEXT_WHILE_OPEN"));
                    await InputSettingsLifecycleTests.UntilAsync(() => Task.FromResult(settings.WeChat.ReadBinding()?.Cursor == "SYNTHETIC_CURSOR_WHILE_OPEN"));
                    Check("actual_receiver_updates_binding_while_edit_window_remains_open", dialog.IsVisible
                        && settings.WeChat.ProtectedBinding != initialCipher && fixture.CursorAdvancedAfterCommittedInbox
                        && fixture.PersistNeverPrecedesInbox && settings.InputMergeWindowMilliseconds == 1000);
                    using (var locked = new FileStream(Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        InputSettingsLifecycleTests.ClickSave(dialog);
                        Check("locked_file_save_keeps_window_open_with_exact_failure_explanation", dialog.IsVisible && dialog.DialogResult != true
                            && dialog.SaveStatusText.Visibility == Visibility.Visible
                            && dialog.SaveStatusText.Text == "设置尚未保存，请检查数据目录后重试。当前运行设置保持不变。");
                        Check("failed_save_changes_neither_disk_live_nor_coordinator", OnDisk().InputMergeWindowMilliseconds == 1000
                            && settings.InputMergeWindowMilliseconds == 1000 && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 1000
                            && !OnDisk().HistoryArchiveEnabled && !settings.HistoryArchiveEnabled);
                        Check("media_controls_failed_save_does_not_publish_pending_choices", !settings.Media.Attachments && !settings.Media.Images
                            && !settings.Media.Files && !settings.Media.Typing && !OnDisk().Media.Files && ReferenceEquals(settings.ActiveMedia, activeMediaBefore));
                        Check("failed_save_keeps_underlying_failure_for_diagnostics", model.LastFailure?.ExceptionType != null);
                        dialog.SettingsTabs.SelectedItem = dialog.ChatGptTab;
                        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                        dialog.UpdateLayout();
                        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        await Task.Delay(350);
                        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        dialog.UpdateLayout();
                        Check("minimum_failure_window_keeps_explanation_and_retry_button_visible_and_enabled",
                            InputSettingsLifecycleTests.Inside(dialog.SaveStatusText, dialog)
                            && InputSettingsLifecycleTests.Inside(InputSettingsLifecycleTests.SaveButton(dialog), dialog)
                            && InputSettingsLifecycleTests.SaveButton(dialog).IsEnabled
                            && dialog.SaveStatusText.Text == "设置尚未保存，请检查数据目录后重试。当前运行设置保持不变。");
                        InputSettingsLifecycleTests.Capture(dialog, Path.Combine(directory, "settings-save-failure-minimum.png"));
                        dialog.SettingsTabs.SelectedItem = dialog.SourcesTab;
                        dialog.HistoryArchiveBox.BringIntoView();
                        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        dialog.UpdateLayout();
                        Check("archive_option_explains_restart_and_nonreversible_format", dialog.HistoryArchiveHelp.Text.Contains("下次启动")
                            && dialog.HistoryArchiveHelp.Text.Contains("取消勾选不能转回旧格式")
                            && InputSettingsLifecycleTests.Inside(dialog.HistoryArchiveBox, dialog));
                        InputSettingsLifecycleTests.Capture(dialog, Path.Combine(directory, "archive-settings-minimum.png"));
                    }
                    fixture.PollResponses.Writer.TryWrite(fixture.Batch("settings-after-failure", "状态", "SYNTHETIC_CURSOR_AFTER_FAILURE", "SYNTHETIC_CONTEXT_AFTER_FAILURE"));
                    await InputSettingsLifecycleTests.UntilAsync(() => Task.FromResult(settings.WeChat.ReadBinding()?.Cursor == "SYNTHETIC_CURSOR_AFTER_FAILURE"));
                    Check("later_real_cursor_save_does_not_silently_commit_failed_edit", OnDisk().InputMergeWindowMilliseconds == 1000
                        && settings.InputMergeWindowMilliseconds == 1000 && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 1000);
                    latestCipher = settings.WeChat.ProtectedBinding;
                    Check("latest_input_seen_markers_remain_durable_before_retry", fixture.PersistNeverPrecedesInbox
                        && settings.WeChat.ReadBinding()!.SeenMessages.Contains(WeChatText.Hash("settings-open-input"))
                        && settings.WeChat.ReadBinding()!.SeenMessages.Contains(WeChatText.Hash("settings-after-failure"))
                        && OnDisk().WeChat.ProtectedBinding == latestCipher);
                    InputSettingsLifecycleTests.ClickSave(dialog);
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); dialog.Close(); }
            };
            bool? saved = dialog.ShowDialog();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check("retry_success_closes_modal_and_applies_exact_saved_3000", saved == true && !dialog.IsVisible
                && OnDisk().InputMergeWindowMilliseconds == 3000 && settings.InputMergeWindowMilliseconds == 3000
                && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 3000);
            Check("retry_preserves_live_binding_received_after_window_open", latestCipher.Length > 0
                && settings.WeChat.ProtectedBinding == latestCipher && OnDisk().WeChat.ProtectedBinding == latestCipher);
            Check("archive_preference_saves_without_switching_active_ledger", settings.HistoryArchiveEnabled && OnDisk().HistoryArchiveEnabled
                && (await fixture.Ledger.ReadConversationDiagnosticsAsync(identity)).Capacity.MaximumStoredSnapshotBytes == null);
            Check("media_controls_save_all_choices_without_switching_running_features", settings.Media is { Attachments: true, Images: true, Files: true, Typing: true }
                && OnDisk().Media is { Attachments: true, Images: true, Files: true, Typing: true }
                && ReferenceEquals(settings.ActiveMedia, activeMediaBefore));
            int starts = fixture.StartHints, stops = fixture.StopHints;
            await ApplySettingsAsync();
            Check("merge_only_apply_does_not_restart_receiver_or_change_conversation_identity", starts == 1 && stops == 0
                && fixture.StartHints == starts && fixture.StopHints == stops && fixture.Runtime.Identity == identity);
            Check("settings_lifecycle_uses_no_model_or_task_execution", fixture.Script.Calls == 0
                && (await fixture.ReadAsync()).Operations.IsEmpty && fixture.Queue.Entries.Count == 0);

            // A different runtime value is a setter observation probe only. It is
            // not a claim that production normally has a mismatched setting.
            fixture.Runtime.ApplyInputMergeWindow(500);
            source.Block = true; refreshingTask = RefreshAsync();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopTask = StopAsync();
            await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("stop_is_waiting_for_real_inflight_collector_before_channel_disposal", exiting && !stopTask.IsCompleted
                && durable == fixture.Runtime && wechat == fixture.Channel && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 500);
            fixture.PollResponses.Writer.TryWrite(fixture.Batch("settings-final-cursor", "状态", "SYNTHETIC_CURSOR_DURING_STOP", "SYNTHETIC_CONTEXT_DURING_STOP"));
            await InputSettingsLifecycleTests.UntilAsync(() => Task.FromResult(settings.WeChat.ReadBinding()?.Cursor == "SYNTHETIC_CURSOR_DURING_STOP"));
            Check("real_receiver_final_save_is_durable_while_stop_drains", finalSaves > 0 && lateRuntimeAlive
                && OnDisk().WeChat.ProtectedBinding == settings.WeChat.ProtectedBinding
                && settings.WeChat.ReadBinding()!.SeenMessages.Contains(WeChatText.Hash("settings-final-cursor"))
                && OnDisk().InputMergeWindowMilliseconds == 3000 && fixture.PersistNeverPrecedesInbox);
            Check("exiting_final_save_does_not_reapply_runtime_setter", finalSetterUntouched
                && InputSettingsLifecycleTests.ActualWindow(fixture.Runtime) == 500 && !stopTask.IsCompleted);
            source.Release.TrySetResult();
            await refreshingTask.WaitAsync(TimeSpan.FromSeconds(5));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check("actual_stop_drains_receiver_before_disposing_runtime_and_model", durable == null && wechat == null && queue == null
                && model == null && fixture.StopHints == 1 && fixture.Channel.WhenStopped.IsCompletedSuccessfully);
            await using (var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(fixture.LedgerPath), new WindowsStateProtector()))
            {
                var state = await reopened.ReadConversationAsync(identity);
                Check("final_input_survives_real_file_reopen_without_new_authority", !state.IsReadOnly
                    && state.Inbox.Count(item => item.SourceMessageId == WeChatText.Hash("settings-final-cursor")) == 1
                    && state.Operations.IsEmpty && state.Authorizations.IsEmpty);
            }
            await using (var released = ReceiverLease.TryAcquire(fixture.Binding.BotId))
                Check("receiver_lease_is_released_only_after_completed_stop", released != null);
        }
        finally
        {
            source.Release.TrySetResult();
            if (refreshingTask != null) await refreshingTask.WaitAsync(TimeSpan.FromSeconds(10));
            await StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Auth.Dispose();
        }
    }
}
