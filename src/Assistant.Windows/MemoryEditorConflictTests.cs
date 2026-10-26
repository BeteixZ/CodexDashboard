using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class MemoryEditorConflictTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-editor-conflict-test");
        if (index < 0) return false;
        var checks = new Dictionary<string, bool>(); string directory = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(directory);
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "memory-editor-conflict-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(item => !item.Value), networkUsed = false,
                productionDataRead = false, actualEncryptedStore = true, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        try
        {
            Settings.SetDataDirectory(Path.Combine(directory, "isolated-settings"));
            StoreChecks(Path.Combine(directory, "stores"), Check);
            AutoMemoryPersistenceChecks(Path.Combine(directory, "auto-memory"), Check);
            await MemoryEditorWindowTests.RunAsync(Path.Combine(directory, "windows"), Check);
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); exit(1); }
        return true;
    }

    private static void StoreChecks(string directory, Action<string, bool> check)
    {
        string identity = "SYNTHETIC_EDITOR_OWNER_A";
        var store = new AssistantMemoryStore(Path.Combine(directory, "main"), () => identity);
        store.Initialize();
        var original = store.AddFromEditor("原始个人记忆全文😀");
        var initial = store.CaptureEditorSnapshot();
        check("snapshot_captures_exact_entry_rules_and_current_precondition", store.IsEditorSnapshotCurrent(initial)
            && initial.Entries.Single().Id == original.Id && initial.Entries.Single().Text == original.Text
            && initial.Rules == store.ReadRules());
        store.UpdateFromEditor(original.Id, "另一次明确修改后的正文");
        string bytes = File.ReadAllText(store.DataPath!);
        check("stale_update_rejects_without_overwriting_newer_version", Conflict(() => store.UpdateFromEditor(initial, original.Id, "旧草稿"))
            && File.ReadAllText(store.DataPath!) == bytes && store.Entries.Single().Metadata!.Version == 2
            && store.Entries.Single().Text == "另一次明确修改后的正文");
        check("stale_delete_rejects_without_deleting_newer_entry", Conflict(() => store.DeleteFromEditor(initial, original.Id))
            && File.ReadAllText(store.DataPath!) == bytes && store.Entries.Single().Id == original.Id);
        check("stale_add_is_not_committed_after_snapshot_changed", Conflict(() => store.AddFromEditor(initial, "旧页面新增草稿"))
            && File.ReadAllText(store.DataPath!) == bytes && store.Entries.Count == 1);
        var refreshed = store.CaptureEditorSnapshot(); int changed = 0;
        void CountChanged() => changed++;
        store.Changed += CountChanged;
        var updated = store.UpdateFromEditor(refreshed, original.Id, "刷新后明确保存的正文");
        store.Changed -= CountChanged;
        check("fresh_update_retains_id_and_desktop_provenance_and_revokes_prior_version", changed == 1
            && updated.Id == original.Id && updated.Metadata!.Version == 3 && updated.Provenance!.SourceKind == "desktop_editor"
            && store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(original.Id, 2));
        check("successful_write_invalidates_old_editor_precondition", !store.IsEditorSnapshotCurrent(refreshed)
            && Conflict(() => store.UpdateFromEditor(refreshed, original.Id, "不得重复提交")));
        var unrelated = store.CaptureEditorSnapshot(); store.AddFromEditor("另一条最新记忆"); bytes = File.ReadAllText(store.DataPath!);
        check("conservative_document_guard_does_not_silently_merge_unseen_changes",
            Conflict(() => store.UpdateFromEditor(unrelated, original.Id, "未核对其他变化")) && File.ReadAllText(store.DataPath!) == bytes);
        var rulesBefore = store.CaptureEditorSnapshot(); store.SaveRules("另一次规则修改");
        bytes = File.ReadAllText(store.DataPath!); string rulesBytes = File.ReadAllText(store.RulesPath!);
        check("rules_revision_conflict_preserves_newer_file_and_document", Conflict(() => store.SaveRulesFromEditor(rulesBefore, "旧规则草稿"))
            && File.ReadAllText(store.DataPath!) == bytes && File.ReadAllText(store.RulesPath!) == rulesBytes);
        var external = store.CaptureEditorSnapshot(); File.WriteAllText(store.RulesPath!, "外部编辑器直接修改的规则");
        check("actual_rules_fingerprint_detects_external_file_change_without_ledger_revision",
            Conflict(() => store.SaveRulesFromEditor(external, "旧规则草稿")) && File.ReadAllText(store.DataPath!) == bytes
            && store.ReadRules() == "外部编辑器直接修改的规则");
        var rulesFresh = store.CaptureEditorSnapshot(); store.SaveRulesFromEditor(rulesFresh, "重新读取后保存的规则");
        check("fresh_rules_save_uses_existing_version_fence_and_full_text", store.ReadRules() == "重新读取后保存的规则"
            && !store.IsEditorSnapshotCurrent(rulesFresh));
        var oldOwner = store.CaptureEditorSnapshot(); identity = "SYNTHETIC_EDITOR_OWNER_B";
        check("foreign_profile_cannot_use_old_snapshot_for_any_mutation",
            Conflict(() => store.AddFromEditor(oldOwner, "不应进入其他账户"))
            && Conflict(() => store.SaveRulesFromEditor(oldOwner, "不应进入其他规则")) && store.Entries.Count == 0);
        identity = "SYNTHETIC_EDITOR_OWNER_A"; store.AdvancePersonalizationRevision();
        check("identity_policy_fence_prevents_returned_profile_reusing_old_editor_snapshot",
            Conflict(() => store.UpdateFromEditor(oldOwner, original.Id, "返回后旧草稿")));
        var disk = store.CaptureEditorSnapshot(); bytes = File.ReadAllText(store.DataPath!); bool writeFailed = false;
        using (var locked = new FileStream(store.DataPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.UpdateFromEditor(disk, original.Id, "写盘失败草稿"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeFailed = true; }
        }
        check("actual_encrypted_file_write_failure_keeps_snapshot_and_original_bytes", writeFailed
            && File.ReadAllText(store.DataPath!) == bytes && store.IsEditorSnapshotCurrent(disk));
        var beforeObserver = store.CaptureEditorSnapshot();
        void ThrowObserver() => throw new IOException("SYNTHETIC_AFTER_COMMIT_OBSERVER");
        store.Changed += ThrowObserver; bool committedWarning = false;
        try { store.UpdateFromEditor(beforeObserver, original.Id, "已经写盘但通知失败"); }
        catch (AssistantMemoryCommittedNotificationException) { committedWarning = true; }
        finally { store.Changed -= ThrowObserver; }
        check("postcommit_memory_observer_failure_is_real_commit_and_cannot_repeat_old_write", committedWarning
            && store.Entries.Single(x => x.Id == original.Id).Text == "已经写盘但通知失败"
            && Conflict(() => store.UpdateFromEditor(beforeObserver, original.Id, "重复旧操作")));
        var beforeRuleFence = store.CaptureEditorSnapshot(); string priorRules = store.ReadRules();
        store.Changed += ThrowObserver; bool fenceFailed = false;
        try { store.SaveRulesFromEditor(beforeRuleFence, "规则文件尚未写入"); }
        catch (AssistantMemoryCommittedNotificationException) { fenceFailed = true; }
        finally { store.Changed -= ThrowObserver; }
        check("rules_fence_observer_failure_does_not_claim_new_rules_were_saved", fenceFailed
            && store.ReadRules() == priorRules && !store.IsEditorSnapshotCurrent(beforeRuleFence)
            && Conflict(() => store.SaveRulesFromEditor(beforeRuleFence, "不得盲目重复")));
        var deletion = store.CaptureEditorSnapshot(); store.DeleteFromEditor(deletion, original.Id);
        check("fresh_delete_preserves_revocation_history_and_does_not_add_authority", store.Entries.All(x => x.Id != original.Id)
            && store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(original.Id, 4));
        var reopened = new AssistantMemoryStore(Path.Combine(directory, "main"), () => identity);
        check("actual_dpapi_store_reopen_preserves_latest_rules_and_remaining_entries", reopened.ReadRules() == store.ReadRules()
            && reopened.Entries.Select(x => x.Text).SequenceEqual(store.Entries.Select(x => x.Text)));
    }
    private static bool Conflict(Action action)
    {
        try { action(); return false; }
        catch (AssistantMemoryEditorConflictException) { return true; }
    }

    private static void AutoMemoryPersistenceChecks(string directory, Action<string, bool> check)
    {
        Directory.CreateDirectory(directory);
        var settings = new Settings { LocalEnabled = false, CodexHome = Path.Combine(directory, "unused-home"),
            ChatGpt = new() { AutoMemoryEnabled = false } };
        settings.Save();
        using var model = new ChatGptAssistant(settings, settings.Save,
            new ChatGptAuth(Path.Combine(directory, "absent-synthetic.credentials"), new NeverNetwork()), new NeverNetwork());
        int changed = 0; model.Changed += () => changed++;
        bool enableFailed = false;
        using (var file = new FileStream(Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { model.SetAutoMemoryEnabled(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { enableFailed = true; }
        }
        check("automatic_memory_enable_disk_failure_rolls_back_live_value_without_publish", enableFailed
            && !settings.ChatGpt.AutoMemoryEnabled && !ReadAutoMemory() && changed == 0);
        settings.Save();
        check("later_normal_settings_save_does_not_silently_apply_failed_auto_memory_enable",
            !settings.ChatGpt.AutoMemoryEnabled && !ReadAutoMemory() && changed == 0);
        model.SetAutoMemoryEnabled(true);
        check("automatic_memory_success_is_saved_and_published_once", settings.ChatGpt.AutoMemoryEnabled
            && ReadAutoMemory() && changed == 1);
        bool disableFailed = false;
        using (var file = new FileStream(Settings.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { model.SetAutoMemoryEnabled(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { disableFailed = true; }
        }
        settings.Save();
        check("automatic_memory_disable_disk_failure_keeps_prior_enabled_state_on_later_save", disableFailed
            && settings.ChatGpt.AutoMemoryEnabled && ReadAutoMemory() && changed == 1);
    }
    private static bool ReadAutoMemory() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath), Settings.Json)!.ChatGpt.AutoMemoryEnabled;
    private sealed class NeverNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unexpected_network_in_memory_editor_test");
    }
}
