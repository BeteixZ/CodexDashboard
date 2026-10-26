using System;
using System.Linq;
using System.Text.Json;

namespace CodexDashboard;

internal static class AssistantSettingsTransactionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var live = new Settings
        {
            LocalEnabled = false, CodexHome = "synthetic-home", InputMergeWindowMilliseconds = 1000,
            WeChat = new() { ProtectedBinding = "opaque-current-binding", ProtectedPushPlusToken = "opaque-current-push",
                PushPlusWakeRouteId = Guid.NewGuid(), RecentAttempts = ["attempt-current"] },
            ChatGpt = new() { ActiveAccount = "synthetic-current-account" }
        };
        live.Normalize();
        var channel = live.WeChat; var model = live.ChatGpt; var registry = live.HostRegistry;
        var edit = new AssistantSettingsEdit(false, "synthetic-python", "synthetic-home", ["host-a", "HOST-A"], 20, 3, 1500,
            new(true, false, false, true, true, false, true, live.WeChat.ProtectedPushPlusToken,
                live.WeChat.PushPlusWakeRouteId, null, null, null, null), HistoryArchiveEnabled: true);
        string before = JsonSerializer.Serialize(live, Settings.Json);
        bool failed = false, sawCandidate = false;
        try
        {
            AssistantSettingsTransaction.Commit(live, edit, candidate =>
            {
                sawCandidate = !ReferenceEquals(candidate, live) && !ReferenceEquals(candidate.WeChat, live.WeChat)
                    && candidate.InputMergeWindowMilliseconds == 1500 && candidate.HistoryArchiveEnabled && !live.HistoryArchiveEnabled
                    && candidate.WeChat.ProtectedBinding == "opaque-current-binding"
                    && JsonSerializer.Serialize(live, Settings.Json) == before;
                throw new System.IO.IOException("synthetic_failed_persistence");
            });
        }
        catch (System.IO.IOException) { failed = true; }
        check("transaction_persists_candidate_without_mutating_live_first", sawCandidate && failed
            && JsonSerializer.Serialize(live, Settings.Json) == before && ReferenceEquals(registry, live.HostRegistry));
        live.WeChat.ProtectedBinding = "opaque-newer-binding-with-current-cursor";
        live.WeChat.RecentAttempts.Add("newer-attempt"); live.ChatGpt.Model = "current-model";
        bool persisted = false;
        AssistantSettingsTransaction.Commit(live, edit, candidate =>
        {
            persisted = candidate.WeChat.ProtectedBinding == live.WeChat.ProtectedBinding
                && candidate.WeChat.RecentAttempts.SequenceEqual(live.WeChat.RecentAttempts)
                && candidate.ChatGpt.Model == "current-model" && candidate.InputMergeWindowMilliseconds == 1500
                && live.InputMergeWindowMilliseconds == 1000;
        });
        check("unrelated_live_progress_and_model_changes_survive_commit_from_latest_snapshot", persisted
            && live.WeChat.ProtectedBinding == "opaque-newer-binding-with-current-cursor"
            && live.WeChat.RecentAttempts.SequenceEqual(new[] { "attempt-current", "newer-attempt" })
            && live.ChatGpt.Model == "current-model" && live.InputMergeWindowMilliseconds == 1500 && live.HistoryArchiveEnabled);
        check("successful_commit_keeps_live_object_references_and_normalizes_only_dialog_values",
            ReferenceEquals(channel, live.WeChat) && ReferenceEquals(model, live.ChatGpt)
            && live.RemoteHosts.SequenceEqual(new[] { "host-a" }) && live.MaxConcurrentExecutions == 3);
        bool wrote = false, invalid = false;
        before = JsonSerializer.Serialize(live, Settings.Json);
        try { AssistantSettingsTransaction.Commit(live, edit with { MergeWindowMilliseconds = 3001 }, _ => wrote = true); }
        catch (System.IO.InvalidDataException) { invalid = true; }
        check("invalid_candidate_is_not_persisted_or_published", invalid && !wrote && JsonSerializer.Serialize(live, Settings.Json) == before);
        var replacement = edit with { ExpectedUnreadablePushCipher = "old-unreadable-cipher", ExpectedUnreadablePushRoute = Guid.NewGuid() };
        bool stale = false;
        try { AssistantSettingsTransaction.Commit(live, replacement, _ => wrote = true); }
        catch (InvalidOperationException error) when (error.Message == "push_replacement_source_changed") { stale = true; }
        check("explicit_push_recovery_cannot_overwrite_changed_source", stale && !wrote && JsonSerializer.Serialize(live, Settings.Json) == before);
        AssistantSettingsTransaction.Commit(live, edit with { WeChat = edit.WeChat with { BindingReplacement = "" } }, _ => { });
        check("explicit_binding_clear_remains_distinct_from_not_edited", live.WeChat.ProtectedBinding.Length == 0);
        string[] variables = ["CODEX_ASSISTANT_ATTACHMENTS", "CODEX_ASSISTANT_IMAGES", "CODEX_ASSISTANT_FILE_SEND", "CODEX_ASSISTANT_TYPING"];
        var originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (string name in variables) Environment.SetEnvironmentVariable(name, null);
            var mediaLive = new Settings();
            var running = mediaLive.ActiveMedia;
            var mediaEdit = edit with { Media = new() { Attachments = true, Images = true, Files = true, Typing = true } };
            bool saveFailed = false;
            try { AssistantSettingsTransaction.Commit(mediaLive, mediaEdit, _ => throw new System.IO.IOException("synthetic")); }
            catch (System.IO.IOException) { saveFailed = true; }
            check("media_failed_save_keeps_settings_and_running_features", saveFailed && !mediaLive.Media.Files && !mediaLive.ActiveMedia.Files);
            string savedMedia = "";
            AssistantSettingsTransaction.Commit(mediaLive, mediaEdit, candidate => savedMedia = JsonSerializer.Serialize(candidate, Settings.Json));
            check("media_save_does_not_change_running_features", mediaLive.Media.Files && mediaLive.Media.Images && ReferenceEquals(running, mediaLive.ActiveMedia) && !running.Files && !running.Attachments);
            var restarted = JsonSerializer.Deserialize<Settings>(savedMedia, Settings.Json)!;
            check("media_saved_options_apply_on_new_host_settings", restarted.ActiveMedia is { Attachments: true, Images: true, Files: true, Typing: true });
            Environment.SetEnvironmentVariable("CODEX_ASSISTANT_ATTACHMENTS", "0");
            var overridden = JsonSerializer.Deserialize<Settings>(savedMedia, Settings.Json)!;
            check("explicit_environment_override_disables_dependent_image_feature", overridden.ActiveMedia is { Attachments: false, Images: false, Files: true });
        }
        finally { for (int i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], originals[i]); }

    }
}
