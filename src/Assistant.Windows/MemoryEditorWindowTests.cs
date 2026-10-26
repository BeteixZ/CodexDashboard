using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

internal static class MemoryEditorWindowTests
{
    private const string Conflict = "内容已在别处变化，尚未保存。请先重新读取并核对；当前编辑内容仍保留。";
    internal static async Task RunAsync(string directory, Action<string, bool> check)
    {
        if (Application.Current?.Dispatcher.CheckAccess() != true)
            throw new InvalidOperationException("The real editor checks require the application dispatcher.");
        Directory.CreateDirectory(directory);
        await UpdateAsync(Path.Combine(directory, "update"), check);
        await RulesAsync(Path.Combine(directory, "rules"), check);
        DeleteAndAdd(Path.Combine(directory, "delete-add"), check);
        IdentityAndSource(Path.Combine(directory, "identity-source"), check);
        await ClosedProjectAsync(Path.Combine(directory, "closed-project"), check);
        await BindingNotificationsAsync(Path.Combine(directory, "binding-context"), check);
    }

    private static async Task UpdateAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var original = f.Store.AddFromEditor("旧记忆：回答使用简洁中文。");
        var window = f.Open(); Select(window, original.Id);
        window.MemoryText.Text = "尚未保存的桌面草稿：先说明依据。";
        window.RulesBox.Text = "尚未保存的独立规则草稿。";
        var current = f.Store.UpdateFromEditor(original.Id, "外部新记忆：保留完整验证结果。");
        string bytes = f.Bytes;
        check("window_external_update_keeps_both_drafts_and_shows_conflict",
            window.MemoryText.Text == "尚未保存的桌面草稿：先说明依据。"
            && window.RulesBox.Text == "尚未保存的独立规则草稿。" && window.MemoryStatus.Text == Conflict);
        Click(window.UpdateMemoryButton);
        check("window_stale_same_id_update_cannot_overwrite_newer_memory",
            f.Bytes == bytes && f.Store.Entries.Single().Text == current.Text
            && f.Store.Entries.Single().Metadata!.Version == current.Metadata!.Version
            && window.MemoryText.Text == "尚未保存的桌面草稿：先说明依据。" && window.MemoryStatus.Text == Conflict);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        window.MemoryTab.IsSelected = true; window.MemoryStatus.BringIntoView();
        await RenderAsync(window);
        check("window_conflict_visible_at_minimum_size_and_reload_available",
            Inside(window.MemoryStatus, window) && Inside(window.ReloadMemoryButton, window)
            && window.MemoryStatus.Text == Conflict && window.ReloadMemoryButton.IsEnabled);
        Capture(window, Path.Combine(directory, "memory-conflict-minimum.png"));
        window.MemorySearchBox.BringIntoView();
        await RenderAsync(window);
        check("memory_search_available_at_minimum_window_size", Inside(window.MemorySearchBox, window)
            && window.MemorySearchBox.ActualWidth > 100);
        Capture(window, Path.Combine(directory, "memory-search-minimum.png"));

        Click(window.ReloadMemoryButton);
        check("window_explicit_memory_reload_shows_current_entry_and_preserves_rule_draft",
            window.MemoryText.Text == current.Text && window.MemoryList.SelectedItem is AssistantMemoryEntry selected
            && selected.Id == current.Id && window.RulesBox.Text == "尚未保存的独立规则草稿。");
        Click(window.SaveRulesButton);
        check("window_memory_reload_does_not_refresh_unread_rules_precondition",
            f.Store.ReadRules() == Fixture.OriginalRules && window.RulesStatus.Text == Conflict
            && window.RulesBox.Text == "尚未保存的独立规则草稿。");
        window.MemoryText.Text = "已核对新版后重新填写的桌面记忆。";
        Click(window.UpdateMemoryButton);
        var saved = f.Store.Entries.Single();
        check("window_explicit_reload_then_update_saves_exact_new_version",
            saved.Id == current.Id && saved.Text == "已核对新版后重新填写的桌面记忆。"
            && saved.Metadata!.Version == current.Metadata!.Version + 1 && saved.Provenance?.SourceKind == "desktop_editor"
            && window.MemoryStatus.Text == "所选记忆已修改。");
        check("window_update_uses_real_protected_store_without_network",
            !f.Bytes.Contains(saved.Text, StringComparison.Ordinal) && f.Reopen().Entries.Single().Text == saved.Text && f.Http.Calls == 0);
    }

    private static async Task RulesAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var original = f.Store.AddFromEditor("规则测试中的原记忆。");
        var window = f.Open(); Select(window, original.Id);
        window.RulesBox.Text = "桌面尚未提交的规则草稿。";
        window.MemoryText.Text = "桌面尚未提交的记忆草稿。";
        f.Store.SaveRules("外部刚保存的新规则。");
        string before = f.Bytes;
        Click(window.SaveRulesButton);
        check("window_stale_rules_save_preserves_file_and_draft",
            f.Store.ReadRules() == "外部刚保存的新规则。" && f.Bytes == before
            && window.RulesBox.Text == "桌面尚未提交的规则草稿。" && window.RulesStatus.Text == Conflict);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        window.EditorTabs.SelectedIndex = 0;
        await RenderAsync(window);
        check("window_rule_conflict_and_reload_visible_at_minimum_size",
            Inside(window.RulesStatus, window) && Inside(window.ReloadRulesButton, window) && window.ReloadRulesButton.IsEnabled);
        Capture(window, Path.Combine(directory, "rules-conflict-minimum.png"));
        Click(window.ReloadRulesButton);
        check("window_explicit_rules_reload_preserves_separate_memory_draft",
            window.RulesBox.Text == "外部刚保存的新规则。" && window.MemoryText.Text == "桌面尚未提交的记忆草稿。");
        Click(window.UpdateMemoryButton);
        check("window_rules_reload_does_not_refresh_stale_memory_precondition",
            f.Store.Entries.Single().Text == original.Text && window.MemoryText.Text == "桌面尚未提交的记忆草稿。"
            && window.MemoryStatus.Text == Conflict);
        window.RulesBox.Text = "核对以后确认的最终规则。";
        Click(window.SaveRulesButton);
        check("window_explicit_reload_then_rules_save_succeeds",
            f.Store.ReadRules() == "核对以后确认的最终规则。" && f.Reopen().ReadRules() == "核对以后确认的最终规则。"
            && window.RulesStatus.Text == "规则已保存，下次回答生效。" && f.Http.Calls == 0);
    }

    private static void DeleteAndAdd(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var entry = f.Store.AddFromEditor("原始待删除记忆。");
        var window = f.Open(); Select(window, entry.Id);
        window.MemoryText.Text = "未保存的桌面草稿不能覆盖他处修改。";
        var current = f.Store.UpdateFromEditor(entry.Id, "用户在另一入口纠正后的记忆。");
        string before = f.Bytes;
        Click(window.DeleteMemoryButton);
        check("window_stale_selection_cannot_delete_newer_version",
            f.Bytes == before && f.Store.Entries.Single().Text == current.Text
            && window.MemoryText.Text == "未保存的桌面草稿不能覆盖他处修改。"
            && window.MemoryStatus.Text == "内容已在别处变化，尚未删除。请先刷新列表并核对；当前编辑内容仍保留。");
        Click(window.AddMemoryButton);
        check("window_stale_add_does_not_silently_publish_an_old_draft",
            f.Bytes == before && f.Store.Entries.Count == 1 && window.MemoryStatus.Text == Conflict
            && window.MemoryText.Text == "未保存的桌面草稿不能覆盖他处修改。");
        Click(window.ReloadMemoryButton); Click(window.DeleteMemoryButton);
        check("window_explicit_reload_allows_exact_current_deletion",
            f.Store.Entries.Count == 0 && f.Reopen().Entries.Count == 0 && window.MemoryStatus.Text == "所选长期记忆已删除。");
        window.MemoryText.Text = "重新读取后明确新增的个人记忆。"; Click(window.AddMemoryButton);
        check("window_fresh_add_remains_available",
            f.Store.Entries.Single().Text == "重新读取后明确新增的个人记忆。"
            && f.Store.Entries.Single().Provenance?.SourceKind == "desktop_editor" && f.Http.Calls == 0);
    }

    private static void IdentityAndSource(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var entry = f.Store.AddFromEditor("只属于合成账户 A 的正文。");
        var window = f.Open(); Select(window, entry.Id);
        AssistantMemorySourceWindow? source = null;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                source = Application.Current.Windows.OfType<AssistantMemorySourceWindow>().Single(w => w.Owner == window);
                check("window_actual_source_dialog_contains_current_source", source.IsVisible && source.DetailsBox.Text.Length > 0);
                f.Assistant.Configure("synthetic-account-B", "", false, false);
                check("window_identity_change_clears_editor_and_source_immediately",
                    window.MemoryText.Text.Length == 0 && window.RulesBox.Text.Length == 0 && window.MemoryList.Items.Count == 0
                    && window.MemorySourceSummary.Text.Length == 0 && window.RulesLocation.Text.Length == 0
                    && !window.EditorTabs.IsEnabled && !source.IsVisible && source.DetailsBox.Text.Length == 0);
                f.Assistant.Configure("synthetic-account-A", "", false, false);
                check("window_identity_aba_never_reactivates_old_editor",
                    !window.EditorTabs.IsEnabled && window.MemoryText.Text.Length == 0 && window.RulesBox.Text.Length == 0
                    && f.Store.Entries.Single().Text == entry.Text && window.MemoryList.Items.Count == 0);
                string before = f.Bytes;
                Click(window.UpdateMemoryButton); Click(window.AddMemoryButton); Click(window.SaveRulesButton);
                check("window_invalidated_actions_do_not_write_or_restore_old_scope", f.Bytes == before && window.MemoryList.Items.Count == 0 && f.Http.Calls == 0);
            }
            catch (Exception error) { failure = error; }
            finally { if (source?.IsVisible == true) source.Close(); }
        }));
        Click(window.SourceMemoryButton);
        if (failure != null) throw failure;
        if (source == null) throw new InvalidOperationException("The actual source modal did not open.");
    }

    private static async Task ClosedProjectAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var entry = f.Store.AddFromEditor("关闭窗口前的合成记忆。");
        var project = new ProjectMemoryIdentity("local", "assistant-owned", "synthetic-project-profile",
            @"\\?\Volume{11111111-1111-1111-1111-111111111111}\synthetic-project");
        var task = new TaskReference("local", "synthetic-thread", "synthetic-turn", project.ProfileId);
        const string source = "synthetic-local-project-proof";
        var target = new ProjectMemoryTarget(project, task, source, ProjectMemoryProof.Fingerprint(project, task, source), DateTimeOffset.UtcNow.AddMinutes(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProjectMemoryTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        f.Assistant.ProjectMemoryResolver = new Resolver(target);
        f.Assistant.ProjectMemoryTargetProvider = async ct =>
        {
            requestToken = ct; entered.TrySetResult();
            var value = await release.Task; returned.TrySetResult(); return value;
        };
        var window = f.Open(); Select(window, entry.Id);
        Click(window.ProjectMemoryButton);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close(); string before = f.Bytes;
            check("window_close_cancels_actual_pending_project_factory", requestToken.IsCancellationRequested && !window.IsVisible);
            release.TrySetResult(target);
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            check("window_late_project_result_does_not_open_an_orphan_editor",
                !Application.Current.Windows.OfType<ProjectMemoryEditorWindow>().Any(w => w.Owner == window)
                && f.Bytes == before && !window.IsVisible && window.MemoryText.Text.Length == 0);
            f.Store.UpdateFromEditor(entry.Id, "窗口关闭后到达的新记忆。");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            check("window_closed_editor_stays_clear_after_late_memory_notifications",
                window.MemoryText.Text.Length == 0 && window.RulesBox.Text.Length == 0 && window.MemoryList.Items.Count == 0 && f.Http.Calls == 0);
        }
        finally { release.TrySetResult(target); }
    }

    private static async Task BindingNotificationsAsync(string directory, Action<string, bool> check)
    {
        int transportCalls = 0;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "changed"),
            modelEnabled: false, receiveMessages: false, channelRequestOverride: (_, _) =>
            { transportCalls++; throw new InvalidOperationException("No transport expected while checking a binding-only switch."); }))
        {
            int throwingCalls = 0, followingCalls = 0, generalCalls = 0;
            void Throwing() { throwingCalls++; throw new InvalidOperationException("Synthetic broken view observer."); }
            void Following() => followingCalls++;
            void General() => generalCalls++;
            var entry = f.Model.Memory.AddFromEditor("仅旧微信绑定可见的合成记忆。");
            f.Model.MemoryContextChanged += Throwing;
            var window = new AssistantMemoryWindow(f.Model, f.Settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000 };
            window.Show(); Select(window, entry.Id);
            f.Model.MemoryContextChanged += Following; f.Model.Changed += General;
            try
            {
                var next = new ClawBinding { BaseUrl = f.Binding.BaseUrl, BotId = f.Binding.BotId,
                    UserId = "synthetic-user-new-binding", Token = "SYNTHETIC_REPLACEMENT_TOKEN",
                    Cursor = "SYNTHETIC_REPLACEMENT_CURSOR", ContextToken = "SYNTHETIC_REPLACEMENT_CONTEXT" };
                f.Settings.WeChat.WriteBinding(next);
                Task changing = f.Channel.ConfigureAsync();
                bool clearedBeforeAwait = window.MemoryText.Text.Length == 0 && window.RulesBox.Text.Length == 0
                    && window.MemoryList.Items.Count == 0 && !window.EditorTabs.IsEnabled;
                await changing.WaitAsync(TimeSpan.FromSeconds(5));
                check("window_binding_only_configure_clears_old_scope_before_async_drain",
                    clearedBeforeAwait && f.Runtime.Identity == DurableAssistantRuntime.BindingIdentity(next, f.Settings.ChatGpt.ActiveAccount) && generalCalls == 0 && transportCalls == 0);
                check("window_scope_notification_isolates_throwing_listener_and_continues_configure",
                    throwingCalls == 1 && followingCalls == 1 && f.Runtime.Identity == DurableAssistantRuntime.BindingIdentity(next, f.Settings.ChatGpt.ActiveAccount));
                f.Settings.WeChat.WriteBinding(f.Binding);
                await f.Channel.ConfigureAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check("window_binding_aba_does_not_restore_old_view",
                    !window.EditorTabs.IsEnabled && window.MemoryList.Items.Count == 0 && window.MemoryText.Text.Length == 0
                    && f.Model.Memory.Entries.Single().Text == entry.Text && transportCalls == 0);
            }
            finally
            {
                window.Close(); f.Model.MemoryContextChanged -= Throwing;
                f.Model.MemoryContextChanged -= Following; f.Model.Changed -= General;
            }
        }
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "corrupt"),
            modelEnabled: false, receiveMessages: false, channelRequestOverride: (_, _) =>
            { transportCalls++; throw new InvalidOperationException("No transport expected for a corrupt synthetic binding."); }))
        {
            var entry = f.Model.Memory.AddFromEditor("绑定损坏前打开的合成记忆。");
            var window = new AssistantMemoryWindow(f.Model, f.Settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000 };
            window.Show(); Select(window, entry.Id);
            try
            {
                f.Settings.WeChat.ProtectedBinding = "SYNTHETIC_NOT_A_DPAPI_ENVELOPE";
                await f.Channel.ConfigureAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check("window_corrupt_binding_configure_also_clears_old_scope_without_transport",
                    window.MemoryText.Text.Length == 0 && window.RulesBox.Text.Length == 0 && window.MemoryList.Items.Count == 0
                    && !window.EditorTabs.IsEnabled && f.Channel.ConnectionState == Codex.Protocol.ConnectionState.Degraded && transportCalls == 0);
            }
            finally { window.Close(); }
        }
    }

    private static void Select(AssistantMemoryWindow window, string id)
    {
        window.MemoryTab.IsSelected = true;
        window.MemoryList.SelectedItem = window.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == id);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task RenderAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(350);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
    }
    private static bool Inside(FrameworkElement element, Window window)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1;
    }
    private static void Capture(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content;
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
    private sealed class Resolver(ProjectMemoryTarget target) : IProjectMemoryResolver
    { public ProjectMemoryTarget? Resolve(TaskReference exactTask) => exactTask == target.Task ? target : null; }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("No real network request is permitted in the memory editor checks."); }
    }
    private sealed class Fixture : IDisposable
    {
        internal const string OriginalRules = "合成初始规则：按已核实事实回答。";
        private readonly string root;
        private readonly List<Window> windows = new();
        internal Settings Settings { get; } = new() { LocalEnabled = false };
        internal NoNetwork Http { get; } = new();
        internal AssistantMemoryStore Store { get; }
        internal ChatGptAssistant Assistant { get; }
        internal Fixture(string directory)
        {
            Directory.CreateDirectory(directory); root = directory;
            Settings.ChatGpt.ActiveAccount = "synthetic-account-A"; Settings.ChatGpt.Enabled = false;
            Store = Reopen(); Store.Initialize(); Store.SaveRules(OriginalRules);
            Assistant = new(Settings, () => { }, new ChatGptAuth(Path.Combine(directory, "absent-synthetic-auth.json"), Http), Http, Store);
        }
        internal AssistantMemoryStore Reopen() => new(root, () => Settings.ChatGpt.ActiveAccount);
        internal string Bytes => File.ReadAllText(Store.DataPath!);
        internal AssistantMemoryWindow Open()
        {
            var window = new AssistantMemoryWindow(Assistant, Settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000 };
            windows.Add(window); window.Show(); return window;
        }
        public void Dispose() { foreach (var window in windows) window.Close(); Assistant.Dispose(); }
    }
}
