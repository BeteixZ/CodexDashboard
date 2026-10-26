using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

// Actual WPF controls and isolated DPAPI files; local fake project evidence only.
internal static class ProjectMemoryEditorTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--project-memory-editor-test"); if (index < 0) return false;
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
        async Task Check(string name, Func<Fixture, Task> action)
        {
            try
            {
                using var fixture = new Fixture(Path.Combine(directory, "fixture-" + (checks.Count + 1)));
                await action(fixture); Require(fixture.Http.Calls == 0);
                checks.Add(new { name, passed = true });
            }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error.GetType().Name + ": " + error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "project-memory-editor-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
        }
        await Check("new_editor_displays_exact_scope_and_never_prefills_or_confirms", async f =>
        {
            var editor = await f.Editor(); var target = f.Target!;
            Require(editor.BodyBox.Text == "" && editor.ReviewedBox.IsChecked != true && !editor.SaveButton.IsEnabled
                && new[] { "本机", "助手自建任务", "项目目录标识", target.Project.ProfileId!, target.Project.CanonicalDirectory,
                    target.Task.ThreadId }.All(editor.TargetText.Text.Contains)
                && !editor.TargetText.Text.Contains(target.Project.Backend) && !editor.TargetText.Text.Contains(f.Store.Profile)
                && !editor.TargetText.Text.Contains(target.EvidenceSource) && !editor.ScopeDetails.IsExpanded);
            editor.ScopeDetails.IsExpanded = true;
            Require(new[] { target.Project.HostId, target.Project.Backend, target.Project.ProfileId!, target.EvidenceSource,
                target.Task.TurnId!, f.Store.Profile }.All(editor.ScopeDetailsText.Text.Contains));
        });
        await Check("explicit_review_is_required_before_any_write", async f =>
        {
            var editor = await f.Editor(); editor.BodyBox.Text = "项目约定：构建前先核对测试。"; string bytes = f.Bytes;
            Require(!await editor.TrySaveAsync() && f.Bytes == bytes && editor.SavedEntry == null && !editor.SaveButton.IsEnabled);
        });
        await Check("invalid_body_is_not_truncated_or_saved", async f =>
        {
            var editor = await f.Editor(); string bytes = f.Bytes; editor.ReviewedBox.IsChecked = true;
            foreach (string body in new[] { " ", new string('字', AssistantMemoryStore.MaxEntryChars + 1), "禁止\0控制符" })
            {
                editor.BodyBox.Text = body;
                Require(!await editor.TrySaveAsync() && f.Bytes == bytes && editor.SavedEntry == null);
            }
        });
        await Check("actual_confirm_button_saves_exact_project_and_reopens_dpapi", async f =>
        {
            var editor = await f.Editor(); Require(SaveInWindow(editor, "此项目使用独立测试目录。") == true);
            var saved = editor.SavedEntry!; var restored = f.Reopen().Entries.Single(e => e.Id == saved.Id);
            Require(restored == saved && saved.Text == "此项目使用独立测试目录。"
                && saved.Metadata is { Kind: AssistantMemoryKind.ProjectKnowledge, Confirmation: AssistantMemoryConfirmation.Explicit, Version: 1 }
                && saved.Metadata.Scope.ProjectReference == f.Target!.Project.Reference && !f.Bytes.Contains(saved.Text)
                && !f.Store.Capture().Context.Contains(saved.Text) && !editor.SaveButton.IsEnabled);
            string bytes = f.Bytes; Require(await editor.TrySaveAsync() && bytes == f.Bytes);
        });
        await Check("correction_prefills_original_but_requires_a_new_explicit_review", async f =>
        {
            var saved = f.Save(); var editor = await f.Editor(saved.Id); string bytes = f.Bytes;
            Require(editor.BodyBox.Text == saved.Text && editor.TargetText.Text.Contains(saved.Id + " v1")
                && editor.ReviewedBox.IsChecked != true && !await editor.TrySaveAsync() && f.Bytes == bytes);
        });
        await Check("actual_correction_preserves_scope_id_and_revokes_old_version", async f =>
        {
            var original = f.Save(); var editor = await f.Editor(original.Id);
            Require(SaveInWindow(editor, "纠正后的项目知识。") == true);
            var saved = editor.SavedEntry!;
            Require(saved.Id == original.Id && saved.Metadata!.Version == 2 && saved.Metadata.Scope == original.Metadata!.Scope
                && f.Reopen().Entries.Single(e => e.Id == original.Id) == saved
                && f.Store.CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(original.Id, 1));
        });
        await Check("profile_change_clears_body_and_never_writes_other_profile", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string path = f.Store.DataPath!, bytes = f.Bytes;
            f.Identity = "synthetic-project-editor-B";
            Require(!await editor.RefreshTargetAsync() && !await editor.TrySaveAsync() && editor.BodyBox.Text == ""
                && editor.ReviewedBox.IsChecked != true && !editor.SaveButton.IsEnabled
                && File.ReadAllText(path) == bytes && !File.Exists(f.Store.DataPath!));
        });
        await Check("same_project_new_turn_is_not_silently_retargeted", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes;
            f.Target = MakeTarget(f.Target!.Project, f.Target.Task with { TurnId = "turn-B" });
            Require(!await editor.RefreshTargetAsync() && editor.BodyBox.Text == "" && !await editor.TrySaveAsync() && f.Bytes == bytes);
        });
        await Check("provider_replacement_and_aba_immediately_clear_open_editor", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; var original = f.Assistant.ProjectMemoryTargetProvider;
            f.Assistant.ProjectMemoryTargetProvider = _ => Task.FromResult(f.Target);
            Require(editor.BodyBox.Text == "" && !editor.SaveButton.IsEnabled);
            f.Assistant.ProjectMemoryTargetProvider = original;
            Require(!await editor.TrySaveAsync() && f.Bytes == bytes);
        });
        await Check("resolver_replacement_immediately_clears_open_editor", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes;
            f.Assistant.ProjectMemoryResolver = new Resolver(() => f.Target);
            Require(editor.BodyBox.Text == "" && !editor.SaveButton.IsEnabled && !await editor.TrySaveAsync() && f.Bytes == bytes);
        });
        await Check("missing_current_focus_is_fail_closed", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; f.ProviderTarget = () => null;
            Require(!await editor.RefreshTargetAsync() && !await editor.TrySaveAsync() && f.Bytes == bytes && editor.BodyBox.Text == "");
        });
        await Check("local_resolver_loses_evidence_before_save", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; f.Target = null;
            Require(!await editor.TrySaveAsync() && f.Bytes == bytes && editor.BodyBox.Text == "");
        });
        await Check("expired_project_evidence_clears_body_before_write", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes;
            f.Target = f.Target! with { ValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
            Require(!await editor.RefreshTargetAsync() && !await editor.TrySaveAsync() && f.Bytes == bytes && editor.BodyBox.Text == "");
        });
        await Check("failed_provider_does_not_leave_editable_stale_scope", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes;
            f.ProviderTarget = () => throw new IOException("synthetic_provider_failed");
            Require(!await editor.RefreshTargetAsync() && !editor.SaveButton.IsEnabled && editor.BodyBox.Text == "" && f.Bytes == bytes);
        });
        await Check("factory_rejects_provider_replacement_while_awaiting", async f =>
        {
            var release = new TaskCompletionSource<ProjectMemoryTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Assistant.ProjectMemoryTargetProvider = _ => release.Task;
            var opening = f.Assistant.CreateProjectMemoryEditorAsync();
            f.Assistant.ProjectMemoryTargetProvider = _ => Task.FromResult(f.Target);
            release.SetResult(f.Target);
            await RejectAsync(async () => { var editor = await opening; editor.Close(); });
            Require(f.Projects.Length == 0);
        });
        await Check("identity_change_during_save_provider_await_cannot_commit", async f =>
        {
            var release = new TaskCompletionSource<ProjectMemoryTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delayed = false; f.Assistant.ProjectMemoryTargetProvider = _ => delayed ? release.Task : Task.FromResult(f.Target);
            var editor = await f.Editor(); Fill(editor); string path = f.Store.DataPath!, bytes = f.Bytes; delayed = true;
            var saving = editor.TrySaveAsync(); Require(!saving.IsCompleted && !editor.SaveButton.IsEnabled);
            f.Identity = "synthetic-project-editor-B"; release.SetResult(f.Target);
            Require(!await saving && editor.BodyBox.Text == "" && File.ReadAllText(path) == bytes && !File.Exists(f.Store.DataPath!));
        });
        await Check("body_change_during_provider_await_requires_review_again", async f =>
        {
            var release = new TaskCompletionSource<ProjectMemoryTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delayed = false; f.Assistant.ProjectMemoryTargetProvider = _ => delayed ? release.Task : Task.FromResult(f.Target);
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; delayed = true;
            var saving = editor.TrySaveAsync(); Require(!saving.IsCompleted && editor.BodyBox.IsReadOnly);
            editor.BodyBox.Text = "程序注入的不同正文也不能沿用旧确认。"; release.SetResult(f.Target);
            Require(!await saving && editor.SavedEntry == null && f.Bytes == bytes);
        });
        await Check("closed_editor_cannot_complete_a_delayed_save", async f =>
        {
            var release = new TaskCompletionSource<ProjectMemoryTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delayed = false; f.Assistant.ProjectMemoryTargetProvider = _ => delayed ? release.Task : Task.FromResult(f.Target);
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; delayed = true;
            var saving = editor.TrySaveAsync(); Require(!saving.IsCompleted); editor.Close(); release.SetResult(f.Target);
            Require(!await saving && editor.SavedEntry == null && f.Bytes == bytes);
        });
        await Check("stale_corrected_entry_is_cleared_instead_of_overwriting", async f =>
        {
            var original = f.Save(); var editor = await f.Editor(original.Id); Fill(editor);
            f.Store.SaveProjectFromEditor(f.Store.CaptureProjectEditor(f.Target!, f.Resolver, original.Id), "另一窗口先纠正", f.Resolver);
            string bytes = f.Bytes;
            Require(!await editor.TrySaveAsync() && editor.BodyBox.Text == "" && f.Bytes == bytes
                && f.Reopen().Entries.Single(e => e.Id == original.Id).Text == "另一窗口先纠正");
        });
        await Check("historical_entry_cannot_be_corrected_under_another_project", async f =>
        {
            var original = f.Save(); string bytes = f.Bytes;
            f.Target = MakeTarget(f.Target!.Project with { CanonicalDirectory = f.Target.Project.CanonicalDirectory + "-other" }, f.Target.Task);
            await RejectAsync(async () => { var editor = await f.Editor(original.Id); editor.Close(); });
            Require(f.Bytes == bytes && f.Reopen().Entries.Single(e => e.Id == original.Id) == original);
        });
        await Check("actual_postcommit_observer_failure_reports_saved_id_without_retry", async f =>
        {
            f.Store.Changed += () => throw new IOException("synthetic_project_observer");
            var editor = await f.Editor(); Require(SaveInWindow(editor, "已经落盘的项目知识") == true);
            var saved = editor.SavedEntry!; string bytes = f.Bytes;
            Require(editor.SavedWarning?.Contains(saved.Id) == true && editor.SavedWarning.Contains("已保存")
                && !editor.SaveButton.IsEnabled && await editor.TrySaveAsync() && bytes == f.Bytes
                && f.Reopen().Entries.Single(e => e.Metadata?.Kind == AssistantMemoryKind.ProjectKnowledge) == saved);
        });
        await Check("actual_correction_observer_failure_keeps_committed_version", async f =>
        {
            var original = f.Save(); f.Store.Changed += () => throw new IOException("synthetic_project_correction_observer");
            var editor = await f.Editor(original.Id); Require(SaveInWindow(editor, "确实写入的纠正正文") == true);
            var saved = editor.SavedEntry!; string bytes = f.Bytes;
            Require(saved.Id == original.Id && saved.Metadata!.Version == 2 && editor.SavedWarning?.Contains(saved.Id) == true
                && await editor.TrySaveAsync() && f.Bytes == bytes && f.Reopen().Entries.Single(e => e.Id == saved.Id) == saved);
        });
        await Check("actual_disk_failure_never_claims_saved_or_observer_warning", async f =>
        {
            var editor = await f.Editor(); string bytes = f.Bytes; int changed = 0; f.Store.Changed += () => changed++;
            using (var locked = new FileStream(f.Store.DataPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(SaveInWindow(editor, "无法落盘的正文") != true && editor.SavedEntry == null && editor.SavedWarning == null);
            Require(changed == 0 && f.Bytes == bytes && f.Projects.Length == 0);
        });
        await Check("management_selected_project_cannot_use_general_add", async f =>
        {
            var saved = f.Save(); var manager = f.Manager(); manager.MemoryList.SelectedItem = manager.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == saved.Id);
            string bytes = f.Bytes;
            Require(manager.MemoryText.IsReadOnly && !manager.AddMemoryButton.IsEnabled
                && manager.UpdateMemoryButton.Content.ToString()!.Contains("项目记忆")
                && manager.MemorySourceSummary.Text.Contains(f.Target!.Project.CanonicalDirectory));
            manager.AddMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(f.Bytes == bytes && f.Store.Entries.Count == 3);
            await Task.CompletedTask;
        });
        await Check("management_general_add_and_update_remain_available", async f =>
        {
            var manager = f.Manager(); manager.MemoryText.Text = "普通个人记忆";
            Require(manager.AddMemoryButton.IsEnabled && !manager.MemoryText.IsReadOnly);
            manager.AddMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = f.Store.Entries.Single(e => e.Text == "普通个人记忆");
            manager.MemoryList.SelectedItem = manager.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == saved.Id);
            manager.MemoryText.Text = "纠正个人记忆"; manager.UpdateMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(f.Store.Entries.Single(e => e.Id == saved.Id).Text == "纠正个人记忆"
                && f.Store.Entries.Single(e => e.Id == saved.Id).Metadata!.Scope.Kind == AssistantMemoryScopeKind.Profile);
            await Task.CompletedTask;
        });
        await Check("management_project_delete_keeps_exact_revocation", async f =>
        {
            var saved = f.Save(); var manager = f.Manager();
            manager.MemoryList.SelectedItem = manager.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == saved.Id);
            manager.DeleteMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(f.Projects.Length == 0 && f.Store.Entries.Count == 2
                && f.Reopen().CaptureHistoryFilter(DateTimeOffset.UtcNow).IsEntryVersionRevoked(saved.Id, 1));
            await Task.CompletedTask;
        });
        await Check("project_entry_does_not_expand_personal_summary_sources", async f =>
        {
            var saved = f.Save(); var sources = f.Store.CaptureDerivedSummaryEditor().Sources;
            Require(sources.Length == 2 && sources.All(e => e.Metadata?.Scope.Kind == AssistantMemoryScopeKind.Profile)
                && sources.All(e => e.Id != saved.Id));
            await Task.CompletedTask;
        });
        await Check("memory_notification_clears_changed_profile_in_the_same_ui_turn", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string oldPath = f.Store.DataPath!, bytes = f.Bytes; int calls = f.ProviderCalls;
            f.Identity = "synthetic-project-editor-B";
            f.Store.AddFromEditor("B 账户独立新增的个人记忆");
            Require(editor.BodyBox.Text == "" && !editor.SaveButton.IsEnabled && editor.ReviewedBox.IsChecked != true
                && File.ReadAllText(oldPath) == bytes && f.ProviderCalls == calls && f.Projects.Length == 0);
        });
        await Check("assistant_notification_clears_invalid_evidence_without_async_retarget", async f =>
        {
            var editor = await f.Editor(); Fill(editor); string bytes = f.Bytes; int calls = f.ProviderCalls;
            f.Target = null; f.Assistant.ReportError(new InvalidOperationException("synthetic_status_change"), "synthetic");
            Require(editor.BodyBox.Text == "" && !editor.SaveButton.IsEnabled && f.ProviderCalls == calls && f.Bytes == bytes);
        });
        await Check("own_commit_notifications_preserve_success_without_false_failure", async f =>
        {
            var editor = await f.Editor(); Fill(editor); Require(await editor.TrySaveAsync());
            Require(editor.SavedEntry != null && editor.SavedWarning == null && editor.BodyBox.Text == editor.SavedEntry.Text
                && editor.TargetText.Text.Contains(f.Target!.Project.CanonicalDirectory));
        });
        await Check("actual_management_buttons_create_and_correct_project_with_saved_warning", async f =>
        {
            var manager = f.Manager(); ShowHidden(manager); manager.MemoryTab.IsSelected = true;
            var created = RunFromManager(manager, manager.ProjectMemoryButton, "从管理窗口实际保存");
            var original = created.SavedEntry!;
            Require(manager.MemoryList.SelectedItem is AssistantMemoryEntry selected && selected.Id == original.Id
                && manager.MemoryStatus.Text.Contains(original.Id) && manager.MemoryText.IsReadOnly);
            f.Store.Changed += () => throw new IOException("synthetic_manager_postcommit");
            var corrected = RunFromManager(manager, manager.UpdateMemoryButton, "从管理窗口实际纠正");
            Require(corrected.SavedEntry?.Id == original.Id && corrected.SavedEntry.Metadata!.Version == 2
                && manager.MemoryStatus.Text.Contains("已保存") && manager.MemoryStatus.Text.Contains(original.Id)
                && manager.MemoryStatus.Text.Contains("不要重复保存") && f.Projects.Length == 1);
            await Task.CompletedTask;
        });
        await Check("actual_project_dialog_default_layout_and_screenshot", async f =>
        {
            var editor = await f.Editor(); ShowHidden(editor); Fill(editor, "此项目的发布说明需要列出已验证项目与尚未验证项目。\n这是人工审阅的项目背景，不是执行授权。");
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            editor.UpdateLayout();
            Require(IsInside(editor.BodyBox, editor) && IsInside(editor.ReviewedBox, editor) && IsInside(editor.SaveButton, editor)
                && editor.BodyBox.ActualHeight >= 80 && editor.TargetText.Text.Contains(f.Target!.Project.CanonicalDirectory)
                && !editor.ScopeDetails.IsExpanded && !editor.ScopeDetailsText.IsVisible);
            Capture(editor, Path.Combine(directory, "project-memory-editor-default.png"));
            editor.ScopeDetails.IsExpanded = true;
            // Fluent expands the content with an animation; a dispatcher idle
            // callback alone may render an intermediate clipped frame.
            await Task.Delay(350);
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); editor.UpdateLayout();
            Require(editor.ScopeDetailsText.IsVisible && editor.ScopeDetailsText.Text.Contains(f.Store.Profile)
                && editor.ScopeDetailsText.Text.Contains(f.Target!.EvidenceSource) && IsInside(editor.SaveButton, editor));
            Capture(editor, Path.Combine(directory, "project-memory-editor-details.png"));
            DependencyObject? scopeAncestor = editor.ScopeDetails;
            while (scopeAncestor != null && scopeAncestor is not ScrollViewer)
                scopeAncestor = VisualTreeHelper.GetParent(scopeAncestor);
            var scopeScroll = scopeAncestor as ScrollViewer ?? throw new InvalidOperationException("scope_scroll_missing");
            scopeScroll.ScrollToBottom();
            await Task.Delay(350);
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); editor.UpdateLayout();
            var detailsBounds = editor.ScopeDetailsText.TransformToAncestor(scopeScroll)
                .TransformBounds(new Rect(0, 0, editor.ScopeDetailsText.ActualWidth, editor.ScopeDetailsText.ActualHeight));
            Require(scopeScroll.VerticalOffset >= scopeScroll.ScrollableHeight - 1
                && detailsBounds.Top >= -1 && detailsBounds.Bottom <= scopeScroll.ActualHeight + 1
                && editor.ScopeDetailsText.Text.Contains(f.Target!.EvidenceSource)
                && editor.ScopeDetailsText.Text.Contains(f.Target.Task.TurnId!));
            Capture(editor, Path.Combine(directory, "project-memory-editor-details-bottom.png"));
        });
        await Check("actual_project_dialog_minimum_layout_and_screenshot", async f =>
        {
            var editor = await f.Editor(); editor.Width = editor.MinWidth; editor.Height = editor.MinHeight;
            ShowHidden(editor); Fill(editor, "最小尺寸下仍需完整核对范围、正文和确认操作。");
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); editor.UpdateLayout();
            Require(IsInside(editor.BodyBox, editor) && IsInside(editor.ReviewedBox, editor) && IsInside(editor.SaveButton, editor)
                && editor.BodyBox.ActualHeight >= 44 && !editor.ScopeDetails.IsExpanded && !editor.ScopeDetailsText.IsVisible);
            Capture(editor, Path.Combine(directory, "project-memory-editor-minimum.png"));
        });
        await Check("actual_management_list_shows_saved_project_scope_before_editing", async f =>
        {
            var saved = f.Save(); var manager = f.Manager(); ShowHidden(manager); manager.MemoryTab.IsSelected = true;
            manager.MemoryList.SelectedItem = manager.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == saved.Id);
            manager.MemoryList.ScrollIntoView(manager.MemoryList.SelectedItem);
            await manager.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); manager.UpdateLayout();
            Require(VisualTexts(manager.MemoryList).Any(t => t.Contains(f.Target!.Project.CanonicalDirectory))
                && manager.MemorySourceSummary.Text.Contains(f.Target!.Project.CanonicalDirectory));
            Capture(manager, Path.Combine(directory, "project-memory-manager.png"));
            manager.Width = manager.MinWidth; manager.Height = manager.MinHeight; manager.UpdateLayout();
            manager.ProjectMemoryButton.BringIntoView();
            await manager.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); manager.UpdateLayout();
            Capture(manager, Path.Combine(directory, "project-memory-manager-minimum.png"));
        });
        if (failures != 0) throw new InvalidOperationException("project_memory_editor_checks_failed");
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static void Fill(ProjectMemoryEditorWindow editor, string text = "经用户核对的项目约定。")
    { editor.BodyBox.Text = text; editor.ReviewedBox.IsChecked = true; }
    private static bool? SaveInWindow(ProjectMemoryEditorWindow editor, string text)
    {
        Require(Application.Current?.Dispatcher.CheckAccess() == true);
        editor.ShowInTaskbar = false; editor.ShowActivated = false; editor.Opacity = 0; Fill(editor, text);
        Exception? clickError = null;
        editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Require(editor.SaveButton.IsEnabled); editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            catch (Exception error) { clickError = error; }
            finally { if (editor.IsVisible) editor.Close(); }
        }), DispatcherPriority.ContextIdle);
        var result = editor.ShowDialog();
        if (clickError != null) throw clickError;
        return result;
    }
    private static void ShowHidden(Window window)
    { window.ShowInTaskbar = false; window.ShowActivated = false; window.Opacity = 0; window.Show(); }
    private static ProjectMemoryEditorWindow RunFromManager(AssistantMemoryWindow manager, Button command, string text)
    {
        ProjectMemoryEditorWindow? editor = null; Exception? failure = null;
        manager.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                editor = Application.Current.Windows.OfType<ProjectMemoryEditorWindow>().Single(w => w.Owner == manager);
                Fill(editor, text); Require(editor.SaveButton.IsEnabled);
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!editor.IsVisible && editor.SavedEntry != null);
            }
            catch (Exception error) { failure = error; if (editor?.IsVisible == true) editor.Close(); }
        }));
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure != null) throw new InvalidOperationException("project_management_dialog_failed", failure);
        return editor ?? throw new InvalidOperationException("project_management_dialog_missing");
    }
    private static bool IsInside(FrameworkElement element, Window window)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1;
    }
    private static IEnumerable<string> VisualTexts(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var value in VisualTexts(VisualTreeHelper.GetChild(parent, i))) yield return value;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds); context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        image.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or OperationCanceledException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private static ProjectMemoryTarget MakeTarget(ProjectMemoryIdentity? project = null, TaskReference? task = null)
    {
        project ??= new("local", "assistant-owned", "synthetic-profile", @"\\?\Volume{11111111-1111-1111-1111-111111111111}\synthetic-project");
        task ??= new("local", "synthetic-thread", "turn-A", project.ProfileId);
        const string source = "synthetic-local-project-evidence";
        return new(project, task, source, ProjectMemoryProof.Fingerprint(project, task, source), DateTimeOffset.UtcNow.AddMinutes(5));
    }
    private sealed class Resolver(Func<ProjectMemoryTarget?> current) : IProjectMemoryResolver
    {
        public ProjectMemoryTarget? Resolve(TaskReference exactTask)
        { var target = current(); return target?.Task == exactTask ? target : null; }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("unexpected_external_request"); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly List<Window> windows = new();
        internal string Identity = "synthetic-project-editor-A";
        internal ProjectMemoryTarget? Target = MakeTarget();
        internal Func<ProjectMemoryTarget?> ProviderTarget;
        internal int ProviderCalls;
        internal readonly NoNetwork Http = new();
        internal readonly Settings Settings = new();
        internal readonly Resolver Resolver;
        internal AssistantMemoryStore Store { get; }
        internal ChatGptAssistant Assistant { get; }
        internal Fixture(string root)
        {
            this.root = root; Store = Reopen(); Store.Initialize();
            Store.AddFromEditor("个人偏好：简洁中文。"); Store.AddFromEditor("个人偏好：附上可核对依据。");
            ProviderTarget = () => Target; Resolver = new(() => Target);
            Assistant = new(Settings, () => { }, new ChatGptAuth(Path.Combine(root, "synthetic-auth.json"), Http), Http, Store)
            { ProjectMemoryResolver = Resolver, ProjectMemoryTargetProvider = _ => { ProviderCalls++; return Task.FromResult(ProviderTarget()); } };
        }
        internal AssistantMemoryStore Reopen() => new(root, () => Identity);
        internal string Bytes => File.ReadAllText(Store.DataPath!);
        internal AssistantMemoryEntry[] Projects => Store.Entries.Where(e => e.Metadata?.Kind == AssistantMemoryKind.ProjectKnowledge).ToArray();
        internal AssistantMemoryEntry Save() => Store.SaveProjectFromEditor(Store.CaptureProjectEditor(Target!, Resolver), "原来的项目知识。", Resolver);
        internal async Task<ProjectMemoryEditorWindow> Editor(string? id = null)
        { var editor = await Assistant.CreateProjectMemoryEditorAsync(id); windows.Add(editor); return editor; }
        internal AssistantMemoryWindow Manager()
        { var manager = new AssistantMemoryWindow(Assistant, Settings); windows.Add(manager); return manager; }
        public void Dispose()
        { foreach (var window in windows) window.Close(); Assistant.Dispose(); }
    }
}
