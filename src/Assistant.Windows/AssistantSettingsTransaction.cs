using System;
using System.Linq;
using System.Text.Json;

namespace CodexDashboard;

// Only fields edited by the settings dialog. Null BindingReplacement means
// the live cursor/context/binding must be preserved, including token rotation.
internal sealed record AssistantWeChatSettingsEdit(
    bool ClawEnabled, bool PushEnabled, bool NotifyCompleted, bool NotifyFailed, bool NotifyWaiting,
    bool IncludeSummary, bool ReportQueriesEnabled, string ProtectedPushToken, Guid PushRouteId,
    string? QuietStart, string? QuietEnd, string? QuietTimeZoneId, string? BindingReplacement);

internal sealed record AssistantSettingsEdit(
    bool LocalEnabled, string PythonPath, string CodexHome, string[] RemoteHosts, int RefreshSeconds,
    int? MaximumExecutions, int MergeWindowMilliseconds, AssistantWeChatSettingsEdit WeChat,
    string? ExpectedUnreadablePushCipher = null, Guid? ExpectedUnreadablePushRoute = null,
    bool? HistoryArchiveEnabled = null, AssistantMediaOptions? Media = null)
{
    internal void ApplyTo(Settings target)
    {
        target.LocalEnabled = LocalEnabled; target.PythonPath = PythonPath; target.CodexHome = CodexHome;
        target.RemoteHosts = RemoteHosts.ToList(); target.RefreshSeconds = RefreshSeconds;
        target.MaxConcurrentExecutions = MaximumExecutions; target.InputMergeWindowMilliseconds = MergeWindowMilliseconds;
        if (HistoryArchiveEnabled is { } archive) target.HistoryArchiveEnabled = archive;
        if (Media is { } media) target.Media = new() { Attachments = media.Attachments, Images = media.Images, Files = media.Files, Typing = media.Typing };
        var live = target.WeChat; var edit = WeChat;
        live.ClawBotEnabled = edit.ClawEnabled; live.PushPlusEnabled = edit.PushEnabled;
        live.NotifyCompleted = edit.NotifyCompleted; live.NotifyFailed = edit.NotifyFailed; live.NotifyWaiting = edit.NotifyWaiting;
        live.IncludeSummary = edit.IncludeSummary; live.ReportQueriesEnabled = edit.ReportQueriesEnabled;
        live.ProtectedPushPlusToken = edit.ProtectedPushToken; live.PushPlusWakeRouteId = edit.PushRouteId;
        live.PushPlusWakeQuietStart = edit.QuietStart; live.PushPlusWakeQuietEnd = edit.QuietEnd;
        live.PushPlusWakeTimeZoneId = edit.QuietTimeZoneId;
        if (edit.BindingReplacement != null) live.ProtectedBinding = edit.BindingReplacement;
    }
}

internal static class AssistantSettingsTransaction
{
    // The application calls synchronously on its Dispatcher. Every channel cursor
    // callback uses that same Dispatcher: clone, persist and commit have no await.
    // The candidate starts from current live settings, never the dialog-open snapshot.
    internal static void Commit(Settings live, AssistantSettingsEdit edit, Action<Settings>? persist = null)
    {
        if (edit.ExpectedUnreadablePushCipher != null
            && (live.WeChat.ProtectedPushPlusToken != edit.ExpectedUnreadablePushCipher
                || live.WeChat.PushPlusWakeRouteId != edit.ExpectedUnreadablePushRoute))
            throw new InvalidOperationException("push_replacement_source_changed");
        var candidate = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(live, Settings.Json), Settings.Json)
            ?? throw new InvalidOperationException("settings_candidate_unavailable");
        edit.ApplyTo(candidate); candidate.Normalize();
        if (persist == null) candidate.Save(); else persist(candidate);
        // Preserve the live settings and WeChat object references used by running
        // services. Only the validated dialog fields and their normalization commit.
        var normalized = edit with { RemoteHosts = candidate.RemoteHosts.ToArray(), RefreshSeconds = candidate.RefreshSeconds };
        normalized.ApplyTo(live);
        live.Version = candidate.Version; live.HostRegistry = candidate.HostRegistry;
    }
}
