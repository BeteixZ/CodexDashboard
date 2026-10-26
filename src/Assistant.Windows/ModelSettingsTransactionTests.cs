using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CodexDashboard;

internal static class ModelSettingsTransactionTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--model-settings-transaction-test")) return false;
        if (args.Length != 2 || args[0] != "--model-settings-transaction-test") throw new ArgumentException("explicit_isolated_output_required");
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidOperationException("new_absolute_output_required");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory); Settings.SetDataDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) { checks.Add(name, passed); if (!passed) throw new InvalidOperationException(name); }
        int result = 1;
        try { await RunAsync(directory, Check); result = 0; }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); }
        finally { File.WriteAllText(Path.Combine(directory, "model-settings-checks.json"), JsonSerializer.Serialize(new { passed = result == 0, checks }, Settings.Json)); }
        exit(result); return true;
    }

    private static async Task RunAsync(string directory, Action<string, bool> check)
    {
        var settings = new Settings { LocalEnabled = false, CodexHome = Path.Combine(directory, "unused-home"),
            ChatGpt = new() { ActiveAccount = "synthetic-account-a", Model = "synthetic-model-a", Enabled = true,
                IncludeTaskContext = true, CompletionSummariesEnabled = false, TaskControlEnabled = false } };
        settings.Save();
        string vaultPath = Path.Combine(directory, "synthetic.credentials");
        new ChatGptVault { Accounts = new()
        {
            new() { ClientId = "synthetic-account-a", AccessToken = "SYNTHETIC_A", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            new() { ClientId = "synthetic-account-b", AccessToken = "SYNTHETIC_B", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }
        } }.Save(vaultPath);
        var memory = new AssistantMemoryStore(Path.Combine(directory, "synthetic-memory"), () => settings.ChatGpt.ActiveAccount + "/synthetic-owner");
        var network = new NoNetwork(); int writes = 0, legacyWrites = 0;
        string expectedLiveAtWrite = Fingerprint(settings);
        bool liveWasUnchangedAtEachWrite = true;
        using var assistant = new ChatGptAssistant(settings, () => { legacyWrites++; settings.Save(); },
            new ChatGptAuth(vaultPath, network), network, memory, candidate =>
            {
                writes++; liveWasUnchangedAtEachWrite &= Fingerprint(settings) == expectedLiveAtWrite;
                candidate.Save();
            });
        var captured = memory.CaptureForGeneration(true, DateTimeOffset.UtcNow);
        string original = Fingerprint(settings), diskOriginal = File.ReadAllText(Settings.FilePath);
        bool failed;
        using (HoldSettings()) failed = SaveRejected(() => assistant.Configure("synthetic-account-b", "synthetic-model-b", false, false, true, true));
        check("actual_file_lock_rejects_candidate_write", failed && writes == 1);
        check("failed_candidate_keeps_all_live_model_fields", Fingerprint(settings) == original);
        check("failed_candidate_keeps_exact_existing_settings_bytes", File.ReadAllText(Settings.FilePath) == diskOriginal);
        check("candidate_writer_never_observes_failed_values_in_live_settings", liveWasUnchangedAtEachWrite && legacyWrites == 0);
        check("failed_configuration_retains_conservative_personalization_fence", !memory.IsGenerationPersonalizationCurrent(captured, true, DateTimeOffset.UtcNow)
            && memory.CaptureForGeneration(true, DateTimeOffset.UtcNow).PersonalizationRevision > captured.PersonalizationRevision);
        var reopenedMemory = new AssistantMemoryStore(Path.Combine(directory, "synthetic-memory"), () => settings.ChatGpt.ActiveAccount + "/synthetic-owner");
        check("personalization_fence_survives_actual_file_reopen", !reopenedMemory.IsGenerationPersonalizationCurrent(captured, true, DateTimeOffset.UtcNow));
        settings.WeChat.RecentAttempts.Add("synthetic-cursor-save-marker"); settings.Save();
        var saved = Read();
        check("later_cursor_style_save_preserves_old_model_values", Fingerprint(saved) == original
            && saved.WeChat.RecentAttempts.Contains("synthetic-cursor-save-marker"));
        var oldOptions = settings.ChatGpt;
        assistant.Configure("synthetic-account-b", "synthetic-model-b", false, false, true, true);
        check("successful_candidate_commits_all_six_fields_and_preserves_live_object", ReferenceEquals(oldOptions, settings.ChatGpt)
            && settings.ChatGpt.ActiveAccount == "synthetic-account-b" && settings.ChatGpt.Model == "synthetic-model-b"
            && !settings.ChatGpt.Enabled && !settings.ChatGpt.IncludeTaskContext && settings.ChatGpt.CompletionSummariesEnabled && settings.ChatGpt.TaskControlEnabled);
        check("successful_candidate_matches_actual_file_and_preserves_unedited_fields", Fingerprint(Read()) == Fingerprint(settings)
            && Read().WeChat.RecentAttempts.Contains("synthetic-cursor-save-marker") && liveWasUnchangedAtEachWrite);
        expectedLiveAtWrite = Fingerprint(settings);
        assistant.Configure("synthetic-account-a", "synthetic-model-a", true, true, false, false);
        expectedLiveAtWrite = Fingerprint(settings);
        check("return_to_original_configuration_cannot_revive_old_generation", !memory.IsGenerationPersonalizationCurrent(captured, true, DateTimeOffset.UtcNow));

        using (var compatibility = new ChatGptAssistant(settings, settings.Save,
            new ChatGptAuth(Path.Combine(directory, "absent-compat.credentials"), new NoNetwork()), new NoNetwork()))
        {
            using (HoldSettings()) failed = SaveRejected(() => compatibility.Configure("compat-new", "compat-model", false, false, true, true));
            settings.Save();
            check("legacy_injected_writer_failure_rolls_back_all_fields_before_later_save", failed
                && Fingerprint(settings) == original && Fingerprint(Read()) == original);
        }

        int signInCalls = 0; string? requestedSignInClient = "not_called";
        var window = new ChatGptWindow(assistant, settings, signIn: (client, _) =>
        {
            signInCalls++; requestedSignInClient = client;
            return Task.FromResult(assistant.Auth.Find("synthetic-account-b")!);
        });
        try
        {
            var account = Find<ComboBox>(window, "AccountBox"); var model = Find<ComboBox>(window, "ModelBox");
            var enabled = Find<CheckBox>(window, "EnabledBox"); var context = Find<CheckBox>(window, "ContextBox");
            var summary = Find<CheckBox>(window, "CompletionSummaryBox"); var control = Find<CheckBox>(window, "TaskControlBox");
            var result = Find<TextBlock>(window, "ResultText");
            Sync(window, () => { model.ItemsSource = new[] { new ChatGptModel("synthetic-model-a", "A"), new ChatGptModel("synthetic-model-b", "B") };
                model.SelectedIndex = 1; enabled.IsChecked = false; context.IsChecked = false; summary.IsChecked = true; control.IsChecked = true; });
            bool escaped = false;
            using (HoldSettings()) { try { control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); } catch { escaped = true; } }
            check("actual_checkbox_event_handles_file_failure_without_unhandled_exception", !escaped && result.Text.Contains("尚未保存"));
            check("failed_ui_save_restores_all_options_and_model_selection", enabled.IsChecked == true && context.IsChecked == true
                && summary.IsChecked == false && control.IsChecked == false && (model.SelectedItem as ChatGptModel)?.Slug == "synthetic-model-a"
                && Fingerprint(settings) == original);
            settings.WeChat.RecentAttempts.Add("synthetic-later-save"); settings.Save();
            check("later_normal_save_does_not_commit_rejected_ui_selection", Fingerprint(Read()) == original && Read().WeChat.RecentAttempts.Contains("synthetic-later-save"));
            using (HoldSettings())
            {
                account.SelectedItem = assistant.Auth.Find("synthetic-account-b");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            check("account_switch_failure_restores_original_selection", (account.SelectedItem as ChatGptAccount)?.ClientId == "synthetic-account-a"
                && (model.SelectedItem as ChatGptModel)?.Slug == "synthetic-model-a" && Fingerprint(settings) == original);
            check("failed_account_switch_does_not_refresh_models_or_make_http", network.Calls == 0 && result.Text.Contains("尚未保存"));
            using (HoldSettings())
            {
                Find<Button>(window, "SignOutButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            check("signout_configuration_failure_does_not_revoke_or_clear_credentials", network.Calls == 0
                && assistant.Auth.Find("synthetic-account-a")?.AccessToken == "SYNTHETIC_A" && result.Text.Contains("本次未退出账户"));
            check("signout_failure_keeps_settings_and_reenables_controls", Fingerprint(settings) == original && Fingerprint(Read()) == original
                && Find<Button>(window, "SignOutButton").IsEnabled && control.IsChecked == false);

            int writesBeforeSignIn = writes;
            using (HoldSettings())
            {
                Find<Button>(window, "AddButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            check("signin_authorization_success_with_save_failure_has_no_tail_model_read_or_second_write", signInCalls == 1
                && requestedSignInClient == null && network.Calls == 0 && writes == writesBeforeSignIn + 1);
            check("signin_save_failure_restores_ui_live_and_disk_without_revoking_new_authorization",
                (account.SelectedItem as ChatGptAccount)?.ClientId == "synthetic-account-a" && (model.SelectedItem as ChatGptModel)?.Slug == "synthetic-model-a"
                && enabled.IsChecked == true && context.IsChecked == true && summary.IsChecked == false && control.IsChecked == false
                && Fingerprint(settings) == original && Fingerprint(Read()) == original
                && assistant.Auth.Find("synthetic-account-b")?.AccessToken == "SYNTHETIC_B");
            check("signin_failure_reports_saved_authorization_and_unsaved_settings_without_leaving_busy",
                result.Text.Contains("账户授权已完成") && result.Text.Contains("设置尚未保存")
                && Find<Button>(window, "AddButton").IsEnabled && Find<Button>(window, "CancelLoginButton").Visibility == Visibility.Collapsed);

            Action observerFailure = () => { if (settings.ChatGpt.TaskControlEnabled) throw new InvalidOperationException("synthetic_view_refresh_failure"); };
            int laterObserver = 0; assistant.Changed += observerFailure; assistant.Changed += () => { if (settings.ChatGpt.TaskControlEnabled) laterObserver++; };
            control.IsChecked = true; control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check("postcommit_observer_failure_keeps_saved_configuration", settings.ChatGpt.TaskControlEnabled && Read().ChatGpt.TaskControlEnabled
                && Fingerprint(settings) == Fingerprint(Read()));
            check("postcommit_warning_reports_saved_and_other_observers_run", assistant.ConfigurationSavedWarning != null
                && result.Text.Contains("已保存") && !result.Text.Contains("尚未保存") && laterObserver == 1);
            assistant.Changed -= observerFailure;
            settings.Save();
            check("ordinary_save_after_committed_warning_keeps_successful_change", Read().ChatGpt.TaskControlEnabled);
        }
        finally { window.Close(); }
        var loginEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginRelease = new TaskCompletionSource<ChatGptAccount>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateWindow = new ChatGptWindow(assistant, settings, signIn: (_, _) => { loginEntered.TrySetResult(); return loginRelease.Task; });
        int writesBeforeClose = writes;
        string savedBeforeClose = Fingerprint(settings);
        var loginTask = (Task)typeof(ChatGptWindow).GetMethod("SignInAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lateWindow, new object[] { true })!;
        await loginEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        string textBeforeClose = Find<TextBlock>(lateWindow, "ResultText").Text;
        lateWindow.Close();
        loginRelease.TrySetResult(assistant.Auth.Find("synthetic-account-b")!);
        await loginTask.WaitAsync(TimeSpan.FromSeconds(3));
        check("closed_window_discards_late_authorization_before_configuration", writes == writesBeforeClose
            && Fingerprint(settings) == savedBeforeClose && Fingerprint(Read()) == savedBeforeClose);
        check("closed_window_does_not_refresh_or_change_result_after_late_authorization", network.Calls == 0
            && Find<TextBlock>(lateWindow, "ResultText").Text == textBeforeClose && !lateWindow.IsVisible);
        check("entire_actual_ui_and_file_suite_uses_zero_external_http", network.Calls == 0);
        check("all_real_candidate_writes_kept_live_fields_unchanged_until_commit", liveWasUnchangedAtEachWrite && legacyWrites == 0);
    }
    private static FileStream HoldSettings() => new(Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static bool SaveRejected(Action action) { try { action(); return false; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; } }
    private static Settings Read() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath), Settings.Json)!;
    private static string Fingerprint(Settings value) => JsonSerializer.Serialize(new { value.ChatGpt.ActiveAccount, value.ChatGpt.Model, value.ChatGpt.Enabled,
        value.ChatGpt.IncludeTaskContext, value.ChatGpt.CompletionSummariesEnabled, value.ChatGpt.TaskControlEnabled });
    private static T Find<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static void Sync(Window window, Action action)
    {
        var field = typeof(ChatGptWindow).GetField("syncing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(window, true); try { action(); } finally { field.SetValue(window, false); }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("unexpected_http_in_model_settings_test"); }
    }
}
