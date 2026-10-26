using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

// Real isolated DPAPI storage, exact editor snapshots, no model/account/channel requests.
internal static class SummaryMemoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--summary-memory-test"); if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)) throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory) || Directory.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); int failures = 0;
        async Task Check(string name, Action<Fixture> action)
        {
            try { action(new(Path.Combine(directory, "fixture-" + (checks.Count + 1)))); checks.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error.GetType().Name + ": " + error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "summary-memory-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
        }
        await Check("editor_snapshot_contains_exact_confirmed_sources", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor();
            Require(captured.Profile == f.Store.Profile && captured.Stamp == f.Store.Capture().Stamp && captured.Summary == null
                && captured.Sources.Length == 2 && captured.Sources.All(e => e.Metadata!.Confirmation == AssistantMemoryConfirmation.Explicit));
        });
        await Check("create_summary_is_explicit_profile_scoped_and_dpapi_durable", f =>
        {
            var entry = f.Create(); var reopened = f.Reopen().Entries.Single(e => e.Id == entry.Id);
            Require(reopened == entry && f.Document().FormatVersion == 3 && !f.Bytes.Contains(entry.Text)
                && entry.Metadata is { Kind: AssistantMemoryKind.EpisodeSummary, Version: 1, Confirmation: AssistantMemoryConfirmation.Explicit }
                && entry.Metadata.Scope == AssistantMemoryLayers.PersonalScope && entry.Provenance?.SourceKind == "desktop_editor"
                && entry.Provenance.Identity == null && entry.Provenance.OperationId == null);
        });
        await Check("source_details_distinguish_user_summary_from_task_history", f =>
        {
            var entry = f.Create(); var details = f.Store.SourceDetails(entry.Id)!;
            Require(details.Known && details.OriginLabel.Contains("用户在桌面整理") && details.Detail.Contains("不代表助手核实过的工作经历")
                && f.Refs().All(r => details.Detail.Contains(r.Id + " v" + r.Version)));
        });
        await Check("model_projection_marks_summary_and_binds_its_version", f =>
        {
            var entry = f.Create(); var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow);
            using var json = JsonDocument.Parse(capture.Content!.Context);
            var row = json.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == entry.Id);
            Require(row.GetProperty("text").GetString() == entry.Text && row.GetProperty("category").GetString() == "derived_memory_summary"
                && capture.Content.Manifest!.IncludedReferences.Contains(new(entry.Id, 1))
                && AssistantMemoryStore.Instructions("fixed", "rules").Contains("不代表已经核实的任务经历"));
        });
        await Check("summary_cannot_select_itself_or_another_summary", f =>
        {
            var entry = f.Create(); string bytes = f.Bytes;
            Reject(() => f.Store.CreateDerivedSummaryFromEditor("摘要套摘要不在本次范围", [f.Refs()[0], new(entry.Id, 1)], f.Store.Capture().Stamp));
            Reject(() => f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 1, "不能引用自己", [f.Refs()[0], new(entry.Id, 1)], f.Store.Capture().Stamp));
            Require(f.Bytes == bytes && !f.Store.CaptureDerivedSummaryEditor().Sources.Any(s => s.Id == entry.Id));
        });
        foreach (string malformed in new[] { "zero", "one", "nine", "duplicate", "unknown", "wrong_version", "lowercase", "null" })
            await Check("reject_exact_source_" + malformed + "_without_write", f =>
            {
                for (int i = 2; i < 9; i++) f.Store.AddFromEditor("额外有效来源" + i);
                var refs = f.Refs(); IReadOnlyList<AssistantMemoryReference> selected = malformed switch
                {
                    "zero" => [], "one" => refs.Take(1).ToArray(), "nine" => refs,
                    "duplicate" => [refs[0], refs[0]], "unknown" => [refs[0], new("FFFFFFFF", 1)],
                    "wrong_version" => [refs[0], refs[1] with { Version = 999 }],
                    "lowercase" => [refs[0], new("abcdef01", 1)], _ => null!
                };
                string bytes = f.Bytes;
                Reject(() => f.Store.CreateDerivedSummaryFromEditor("不应保存", selected, f.Store.Capture().Stamp)); Require(f.Bytes == bytes);
            });
        await Check("eight_distinct_valid_sources_are_supported", f =>
        {
            for (int i = 2; i < 8; i++) f.Store.AddFromEditor("额外有效来源" + i);
            var entry = f.Create(); Require(entry.Metadata!.Dependencies.Length == 8);
        });
        await Check("confirmed_preference_is_an_eligible_source", f =>
        {
            Require(f.Store.LearnPreferences([new("detail_level", "我更喜欢简洁的说明")], f.Store.Capture().Stamp) == 1);
            var preference = f.Store.Entries.Single(e => e.Metadata?.Kind == AssistantMemoryKind.Preference);
            var editor = f.Store.CaptureDerivedSummaryEditor();
            var summary = f.Store.CreateDerivedSummaryFromEditor("经审阅的偏好整理", [AssistantMemoryLayers.Reference(preference),
                AssistantMemoryLayers.Reference(editor.Sources.First(e => e.Id != preference.Id))], editor.Stamp);
            Require(summary.Metadata!.Dependencies.Contains(AssistantMemoryLayers.Reference(preference)));
        });
        foreach (string kind in new[] { "legacy", "pending", "unrecorded_confirmation", "expired", "project", "persona" })
            await Check("ineligible_" + kind + "_source_is_hidden_and_rejected", f =>
            {
                var document = f.Document(); var old = document.Entries[0]; var metadata = old.Metadata!;
                metadata = kind switch
                {
                    "legacy" => null!, "pending" => metadata with { Confirmation = AssistantMemoryConfirmation.Pending },
                    "unrecorded_confirmation" => metadata with { Confirmation = AssistantMemoryConfirmation.LegacyUnspecified },
                    "expired" => metadata with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
                    "project" => metadata with { Kind = AssistantMemoryKind.ProjectKnowledge, Scope = new(AssistantMemoryScopeKind.Project, "synthetic-project") },
                    _ => metadata with { Kind = AssistantMemoryKind.Persona }
                };
                document.Entries[0] = old with { Metadata = metadata }; f.Write(document);
                var capture = f.Store.CaptureDerivedSummaryEditor(); string bytes = f.Bytes;
                Require(capture.Sources.Length == 1 && capture.Sources.All(e => e.Id != old.Id));
                Reject(() => f.Store.CreateDerivedSummaryFromEditor("不应保存", document.Entries.Select(AssistantMemoryLayers.Reference).ToArray(), capture.Stamp));
                Require(f.Bytes == bytes);
            });
        await Check("source_edit_after_editor_capture_rejects_stale_save", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor(); f.Store.UpdateFromEditor(captured.Sources[0].Id, "已纠正的来源");
            string bytes = f.Bytes; Reject(() => f.Store.CreateDerivedSummaryFromEditor("旧内容的摘要", captured.Sources.Select(AssistantMemoryLayers.Reference).ToArray(), captured.Stamp));
            Require(f.Bytes == bytes && !f.Store.Entries.Any(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary));
        });
        await Check("source_delete_after_editor_capture_rejects_stale_save", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor(); f.Store.Delete(captured.Sources[0].Id);
            string bytes = f.Bytes; Reject(() => f.Store.CreateDerivedSummaryFromEditor("旧内容的摘要", captured.Sources.Select(AssistantMemoryLayers.Reference).ToArray(), captured.Stamp)); Require(f.Bytes == bytes);
        });
        await Check("source_expiry_bounds_summary_and_capture_validity", f =>
        {
            var expires = DateTimeOffset.UtcNow.AddHours(1); var source = f.Store.Entries[0];
            f.Store.UpdateMetadata(source.Id, source.Metadata! with { ExpiresAtUtc = expires }, f.Store.Capture().Stamp);
            var summary = f.Create(); var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow);
            Require(summary.Metadata!.ExpiresAtUtc == expires && capture.Content!.Manifest!.ValidUntilUtc == expires
                && !f.Store.IsGenerationPersonalizationCurrent(capture, true, expires)
                && !f.Store.Capture(new(expires)).Manifest!.IncludedReferences.Any(r => r.Id == summary.Id)
                && f.Store.CaptureHistoryFilter(expires).IsEntryVersionRevoked(summary.Id, 1));
        });
        await Check("correction_preserves_id_and_advances_version_with_revocation", f =>
        {
            var entry = f.Create(); var editor = f.Store.CaptureDerivedSummaryEditor(entry.Id);
            var updated = f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 1, "由用户重新审阅的摘要正文", f.Refs(), editor.Stamp);
            Require(updated.Id == entry.Id && updated.CreatedAt == entry.CreatedAt && updated.Metadata!.Version == 2
                && updated.Metadata.Replaces == new AssistantMemoryReference(entry.Id, 1)
                && f.Store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(entry.Id, 1)
                && f.Reopen().Entries.Single(e => e.Id == entry.Id) == updated);
        });
        await Check("correction_can_explicitly_reselect_current_sources", f =>
        {
            var entry = f.Create(); var third = f.Store.AddFromEditor("另一条已确认来源");
            var refs = new[] { f.Refs()[0], AssistantMemoryLayers.Reference(third) }.Distinct().ToArray();
            if (refs.Length != 2) refs = [f.Refs().First(r => r.Id != third.Id), AssistantMemoryLayers.Reference(third)];
            var updated = f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 1, "重新选定来源的摘要", refs, f.Store.Capture().Stamp);
            Require(updated.Metadata!.Dependencies.OrderBy(r => r.Id).SequenceEqual(refs.OrderBy(r => r.Id)));
        });
        await Check("same_correction_is_noop_and_does_not_fake_new_version", f =>
        {
            var entry = f.Create(); string bytes = f.Bytes;
            var same = f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 1, entry.Text, f.Refs().Reverse().ToArray(), f.Store.Capture().Stamp);
            Require(same == entry && f.Bytes == bytes);
        });
        await Check("wrong_summary_version_rejected_even_with_current_stamp", f =>
        {
            var entry = f.Create(); string bytes = f.Bytes;
            Reject(() => f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 99, "不应保存", f.Refs(), f.Store.Capture().Stamp)); Require(f.Bytes == bytes);
        });
        await Check("general_record_cannot_be_retyped_by_summary_correction", f =>
        {
            var entry = f.Store.Entries[0]; string bytes = f.Bytes;
            Reject(() => f.Store.CorrectDerivedSummaryFromEditor(entry.Id, 1, "不可改型", f.Refs(), f.Store.Capture().Stamp)); Require(f.Bytes == bytes);
        });
        await Check("source_correction_clears_body_and_prevents_restart_revival", f =>
        {
            var summary = f.Create(); var before = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow);
            f.Store.UpdateFromEditor(f.Refs()[0].Id, "来源已经纠正"); var reopened = f.Reopen(); var dead = reopened.Entries.Single(e => e.Id == summary.Id);
            Require(dead.Text == "" && dead.Metadata!.InvalidatedAtUtc != null && dead.Metadata.Version == 2
                && reopened.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(summary.Id, 1)
                && !reopened.IsGenerationPersonalizationCurrent(before, true, DateTimeOffset.UtcNow)
                && !reopened.Capture().Manifest!.IncludedReferences.Any(r => r.Id == summary.Id));
        });
        await Check("source_delete_clears_summary_and_preserves_only_audit_refs", f =>
        {
            var summary = f.Create(); f.Store.Delete(f.Refs()[0].Id); var doc = f.Document();
            var dead = doc.Entries.Single(e => e.Id == summary.Id);
            Require(dead.Text == "" && dead.Metadata!.Dependencies.Length == 2 && !JsonSerializer.Serialize(doc, Settings.Json).Contains(summary.Text)
                && doc.Revocations.Any(r => r.EntryId == summary.Id && r.Version == 1));
        });
        await Check("invalidated_summary_cannot_be_reactivated_by_correction", f =>
        {
            var summary = f.Create(); f.Store.UpdateFromEditor(f.Refs()[0].Id, "新的来源事实"); string bytes = f.Bytes;
            Reject(() => f.Store.CaptureDerivedSummaryEditor(summary.Id));
            Reject(() => f.Store.CorrectDerivedSummaryFromEditor(summary.Id, 2, "复活旧摘要", f.Refs(), f.Store.Capture().Stamp)); Require(f.Bytes == bytes);
            var fresh = f.Create("用户另建并审阅的新摘要"); Require(fresh.Id != summary.Id && f.Store.Entries.Single(e => e.Id == summary.Id).Text == "");
        });
        await Check("deleting_summary_does_not_delete_its_original_sources", f =>
        {
            var summary = f.Create(); var ids = f.Refs().Select(r => r.Id).ToArray(); f.Store.Delete(summary.Id);
            Require(f.Reopen().Entries.Select(e => e.Id).Order().SequenceEqual(ids.Order())
                && f.Store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(summary.Id, 1));
        });
        await Check("ordinary_update_cannot_drop_summary_dependencies", f =>
        {
            var summary = f.Create(); string bytes = f.Bytes;
            Reject(() => f.Store.UpdateFromEditor(summary.Id, "试图变成普通记忆"));
            Reject(() => f.Store.UpdateMetadata(summary.Id, summary.Metadata! with { Kind = AssistantMemoryKind.General, DerivedFrom = [] }, f.Store.Capture().Stamp));
            Require(f.Bytes == bytes);
        });
        await Check("double_submit_with_old_stamp_creates_only_one_summary", f =>
        {
            var editor = f.Store.CaptureDerivedSummaryEditor(); var refs = f.Refs();
            f.Store.CreateDerivedSummaryFromEditor("唯一摘要", refs, editor.Stamp); string bytes = f.Bytes;
            Reject(() => f.Store.CreateDerivedSummaryFromEditor("唯一摘要", refs, editor.Stamp));
            Require(f.Bytes == bytes && f.Store.Entries.Count(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary) == 1);
        });
        await Check("profile_switch_rejects_old_editor_without_cross_profile_write", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor(); string aPath = f.Store.DataPath!, aBytes = f.Bytes;
            f.Identity = "synthetic-summary-B"; f.Store.Initialize(); f.Store.AddFromEditor("B自己的记忆"); string bBytes = f.Bytes;
            Reject(() => f.Store.CreateDerivedSummaryFromEditor("A摘要不能写入B", captured.Sources.Select(AssistantMemoryLayers.Reference).ToArray(), captured.Stamp));
            Require(File.ReadAllText(aPath) == aBytes && f.Bytes == bBytes);
        });
        await Check("identity_change_during_capture_cannot_redirect_write", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor(); string path = f.Store.DataPath!, bytes = f.Bytes; bool armed = true;
            var changing = f.WithIdentity(() => { string value = f.Identity; if (armed) { armed = false; f.Identity = "synthetic-summary-B"; } return value; });
            Reject(() => changing.CreateDerivedSummaryFromEditor("不能跨身份写入", captured.Sources.Select(AssistantMemoryLayers.Reference).ToArray(), captured.Stamp));
            Require(File.ReadAllText(path) == bytes && !File.Exists(changing.DataPath));
        });
        await Check("capacity_failure_keeps_original_document_and_source_text", f =>
        {
            for (int i = 2; i < AssistantMemoryStore.MaxEntries; i++) f.Store.AddFromEditor("容量来源" + i);
            string bytes = f.Bytes; Reject(() => f.Store.CreateDerivedSummaryFromEditor("超出容量", f.Refs().Take(2).ToArray(), f.Store.Capture().Stamp));
            Require(f.Bytes == bytes && f.Store.Entries.Count == AssistantMemoryStore.MaxEntries);
        });
        await Check("invalid_body_never_truncates_or_mutates", f =>
        {
            string bytes = f.Bytes;
            foreach (var body in new[] { " ", new string('字', AssistantMemoryStore.MaxEntryChars + 1), "含\0控制符" })
                Reject(() => f.Store.CreateDerivedSummaryFromEditor(body, f.Refs(), f.Store.Capture().Stamp));
            Require(f.Bytes == bytes);
        });
        await Check("postwrite_error_cannot_duplicate_on_stale_retry", f =>
        {
            var captured = f.Store.CaptureDerivedSummaryEditor(); var refs = f.Refs();
            f.Store.Changed += () => throw new IOException("synthetic_after_summary_write");
            SummaryMemorySavedNotificationException? committed = null;
            try { f.Store.CreateDerivedSummaryFromEditor("确实写入的摘要", refs, captured.Stamp); }
            catch (SummaryMemorySavedNotificationException error) { committed = error; }
            Require(committed != null && committed.Profile == f.Store.Profile && committed.Revision == f.Document().Revision
                && committed.SavedEntry == f.Reopen().Entries.Single(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary)
                && committed.InnerException?.InnerException is IOException { Message: "synthetic_after_summary_write" });
            Require(f.Document().Entries.Count(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary) == 1);
            string bytes = f.Bytes; Reject(() => f.Store.CreateDerivedSummaryFromEditor("确实写入的摘要", refs, captured.Stamp)); Require(f.Bytes == bytes);
        });
        await Check("actual_editor_postcommit_observer_error_reports_saved_id_without_retry", f =>
        {
            f.Store.Changed += () => throw new IOException("synthetic_summary_observer");
            var editor = new SummaryMemoryEditorWindow(f.Store);
            Require(SaveInWindow(editor, "窗口中实际保存的摘要") == true);
            var saved = editor.SavedEntry ?? throw new InvalidOperationException("saved_entry_missing");
            Require(editor.SavedWarning?.Contains(saved.Id, StringComparison.Ordinal) == true
                && editor.SavedWarning?.Contains("已保存", StringComparison.Ordinal) == true && !editor.SaveButton.IsEnabled);
            string bytes = f.Bytes; Require(editor.TrySave() && f.Bytes == bytes
                && f.Reopen().Entries.Count(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary) == 1);
            var reopenedEditor = new SummaryMemoryEditorWindow(f.Reopen(), saved.Id);
            try { Require(reopenedEditor.BodyBox.Text == saved.Text); }
            finally { reopenedEditor.Close(); }
        });
        await Check("actual_correction_observer_error_preserves_saved_version_and_warning", f =>
        {
            var original = f.Create(); f.Store.Changed += () => throw new IOException("synthetic_correction_observer");
            var editor = new SummaryMemoryEditorWindow(f.Store, original.Id);
            Require(SaveInWindow(editor, "窗口中实际纠正的摘要") == true);
            var saved = editor.SavedEntry ?? throw new InvalidOperationException("saved_entry_missing");
            Require(saved.Id == original.Id && saved.Metadata!.Version == 2
                && editor.SavedWarning?.Contains(original.Id, StringComparison.Ordinal) == true
                && !editor.SaveButton.IsEnabled);
            var restored = f.Reopen().Entries.Single(e => e.Id == original.Id);
            string bytes = f.Bytes;
            Require(restored == editor.SavedEntry && editor.TrySave() && f.Bytes == bytes
                && f.Store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(original.Id, 1));
        });
        await Check("actual_editor_disk_failure_never_claims_committed_or_saved_warning", f =>
        {
            var editor = new SummaryMemoryEditorWindow(f.Store); string bytes = f.Bytes; int callbacks = 0;
            f.Store.Changed += () => callbacks++;
            using (var locked = new FileStream(f.Store.DataPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(SaveInWindow(editor, "磁盘失败不能伪称已保存") != true && editor.SavedEntry == null && editor.SavedWarning == null);
            Require(callbacks == 0 && f.Bytes == bytes
                && !f.Reopen().Entries.Any(e => e.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary));
        });
        await Check("summary_change_changes_bound_output_dependencies", f =>
        {
            var summary = f.Create(); var before = AssistantMemoryDependencies.ToOutputDependencies(f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow));
            f.Store.CorrectDerivedSummaryFromEditor(summary.Id, 1, "新的摘要正文", f.Refs(), f.Store.Capture().Stamp);
            var after = AssistantMemoryDependencies.ToOutputDependencies(f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow));
            Require(!OutputDependencies.Equivalent(before, after)
                && after.Contexts.Single(c => c.Provider == AssistantMemoryDependencies.MemoryProvider).Items.Contains(new(summary.Id, 2)));
        });
        await Check("disabled_personalization_does_not_depend_on_summary_edits", f =>
        {
            var before = f.Store.CaptureForGeneration(false, DateTimeOffset.UtcNow); f.Create();
            Require(f.Store.IsGenerationPersonalizationCurrent(before, false, DateTimeOffset.UtcNow)
                && OutputDependencies.Equivalent(AssistantMemoryDependencies.ToOutputDependencies(before),
                    AssistantMemoryDependencies.ToOutputDependencies(f.Store.CaptureForGeneration(false, DateTimeOffset.UtcNow))));
        });
        if (failures != 0) throw new InvalidOperationException("summary_memory_checks_failed");
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static bool? SaveInWindow(SummaryMemoryEditorWindow editor, string text)
    {
        Require(Application.Current?.Dispatcher.CheckAccess() == true);
        editor.ShowInTaskbar = false; editor.ShowActivated = false; editor.Opacity = 0;
        editor.SourceList.SelectAll(); editor.BodyBox.Text = text; editor.ReviewedBox.IsChecked = true;
        Require(editor.SaveButton.IsEnabled);
        Exception? clickError = null;
        editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            catch (Exception error) { clickError = error; }
            finally { if (editor.IsVisible) editor.Close(); }
        }), DispatcherPriority.Loaded);
        var result = editor.ShowDialog();
        if (clickError != null) throw clickError;
        return result;
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ChatGptException or InvalidOperationException or ArgumentException or IOException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private sealed class Fixture
    {
        private readonly string root;
        internal string Identity = "synthetic-summary-A";
        internal AssistantMemoryStore Store { get; }
        internal Fixture(string root)
        {
            this.root = root; Store = Reopen(); Store.Initialize();
            Store.AddFromEditor("我喜欢简明的中文解释"); Store.AddFromEditor("我希望结论后面附上依据");
        }
        internal AssistantMemoryStore Reopen() => new(root, () => Identity);
        internal AssistantMemoryStore WithIdentity(Func<string> identity) => new(root, identity);
        internal string Bytes => File.ReadAllText(Store.DataPath!);
        internal AssistantMemoryDocument Document() => JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(Bytes), Settings.Json)!;
        internal void Write(AssistantMemoryDocument document) => File.WriteAllText(Store.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(document, Settings.Json)));
        internal AssistantMemoryReference[] Refs() => Store.CaptureDerivedSummaryEditor().Sources.Select(AssistantMemoryLayers.Reference).ToArray();
        internal AssistantMemoryEntry Create(string text = "我偏好简明中文，并希望结论附有依据")
        {
            var captured = Store.CaptureDerivedSummaryEditor();
            return Store.CreateDerivedSummaryFromEditor(text, captured.Sources.Select(AssistantMemoryLayers.Reference).ToArray(), captured.Stamp);
        }
    }
}
