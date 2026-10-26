using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexDashboard;

public partial class AssistantMemoryWindow : Window
{
    private readonly ChatGptAssistant assistant;
    private readonly Settings settings;
    private readonly string profile;
    private readonly CancellationTokenSource lifetime = new();
    private AssistantMemoryEditorSnapshot? rulesSnapshot, memorySnapshot;
    private AssistantMemorySourceWindow? sourceWindow;
    private AssistantMemoryEditorSnapshot? sourceSnapshot;
    private Window? childEditor;
    private bool closed, replacingView, writing;
    private DateTimeOffset? nextMemoryExpiry;
    private readonly DispatcherTimer expiryCheck = new() { Interval = TimeSpan.FromSeconds(1) };
    private int identityInvalidated, projectRequest;
    private string memoryQuery = "";
    private const string ConflictMessage = "内容已在别处变化，尚未保存。请先重新读取并核对；当前编辑内容仍保留。";
    private const string IdentityMessage = "账户或微信绑定已变化，已清除旧内容。请关闭后重新打开。";

    internal AssistantMemoryWindow(ChatGptAssistant assistant, Settings settings)
    {
        InitializeComponent(); this.assistant = assistant; this.settings = settings; profile = assistant.Memory.Profile;
        UseMemoryBox.IsChecked = settings.ChatGpt.PersonalizationEnabled;
        AutoMemoryBox.IsChecked = settings.ChatGpt.AutoMemoryEnabled;
        ReloadRules(); ReloadMemories();
        assistant.Memory.Changed += ExternalChanged;
        assistant.Changed += ExternalChanged;
        assistant.MemoryContextChanged += ExternalChanged;
        Closed += WindowClosed;
        expiryCheck.Tick += (_, _) => { if (nextMemoryExpiry <= DateTimeOffset.UtcNow) ObserveExternalState(); };
        Loaded += (_, _) => expiryCheck.Start();
        ExternalChanged();
    }
    private bool SameProfile()
    {
        if (closed) return false;
        bool same;
        try { same = Volatile.Read(ref identityInvalidated) == 0 && profile == assistant.Memory.Profile
            && (Application.Current is not AssistantApp app || !app.Exiting); }
        catch { same = false; }
        if (same) return true;
        Interlocked.Exchange(ref identityInvalidated, 1);
        lifetime.Cancel(); ClearIdentityContent(); return false;
    }
    private void ExternalChanged()
    {
        // Capture a mismatched identity at notification time, not after a queued UI
        // callback; switching A -> B -> A must not revive the old editor.
        try
        {
            if (profile != assistant.Memory.Profile || Application.Current is AssistantApp app && app.Exiting)
                Interlocked.Exchange(ref identityInvalidated, 1);
        }
        catch { Interlocked.Exchange(ref identityInvalidated, 1); }
        if (Dispatcher.CheckAccess()) ObserveExternalState();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ObserveExternalState));
    }
    private void ObserveExternalState()
    {
        if (!SameProfile()) return;
        UseMemoryBox.IsChecked = settings.ChatGpt.PersonalizationEnabled;
        AutoMemoryBox.IsChecked = settings.ChatGpt.AutoMemoryEnabled;
        if (sourceWindow != null && (sourceSnapshot == null || !SnapshotCurrent(sourceSnapshot))) CloseSourceWindow();
        if (writing) return;
        bool removedSelection = RemoveWithdrawnMemories();
        if (rulesSnapshot != null && !SnapshotCurrent(rulesSnapshot)) RulesStatus.Text = ConflictMessage;
        if (removedSelection) MemoryStatus.Text = "所选记忆已删除或失效，已清除旧内容。请刷新列表后继续。";
        else if (memorySnapshot != null && !SnapshotCurrent(memorySnapshot)) MemoryStatus.Text = ConflictMessage;
    }
    private bool RemoveWithdrawnMemories()
    {
        if (memorySnapshot == null) return false;
        MemoryViewState view;
        try { view = assistant.Memory.CaptureMemoryView(); }
        catch (ChatGptException) { return false; }
        if (view.Profile != profile || !SameProfile()) return false;
        nextMemoryExpiry = view.NextExpiryUtc;
        var retained = memorySnapshot.Entries.Where(old => view.Entries.Any(current => current.Id == old.Id))
            .Select(old => view.Entries.Single(current => current.Id == old.Id) is { Text.Length: 0 } inactive ? inactive : old).ToImmutableArray();
        if (retained.SequenceEqual(memorySnapshot.Entries)) return false;
        string? selected = (MemoryList.SelectedItem as AssistantMemoryEntry)?.Id;
        bool removed = selected != null && retained.All(e => e.Id != selected || e.Text.Length == 0);
        replacingView = true;
        try
        {
            // Keep the original stamp: removing withdrawn rows never signs an unseen update.
            memorySnapshot = memorySnapshot with { Entries = retained };
            ApplyMemoryFilter(selected);
            if (removed) { MemoryText.Clear(); MemorySourceSummary.Text = "所选记忆已删除或失效。"; }
        }
        finally { replacingView = false; }
        UpdateMemoryCount();
        return removed;
    }
    private bool SnapshotCurrent(AssistantMemoryEditorSnapshot snapshot)
    {
        try { return assistant.Memory.IsEditorSnapshotCurrent(snapshot); }
        catch { return false; }
    }
    private void ClearIdentityContent()
    {
        replacingView = true;
        try
        {
            rulesSnapshot = memorySnapshot = sourceSnapshot = null;
            MemoryList.ItemsSource = null; MemoryText.Clear(); RulesBox.Clear(); RulesLocation.Text = "";
            MemorySearchBox.Clear(); memoryQuery = "";
            MemorySourceSummary.Text = ""; MemoryCountText.Text = "";
            UseMemoryBox.IsChecked = AutoMemoryBox.IsChecked = false;
            UseMemoryBox.IsEnabled = AutoMemoryBox.IsEnabled = EditorTabs.IsEnabled = false;
            MemoryStatus.Text = RulesStatus.Text = IdentityMessage;
            CloseSourceWindow();
            if (childEditor is SummaryMemoryEditorWindow summary)
            { summary.SourceList.Items.Clear(); summary.BodyBox.Clear(); summary.ReviewedBox.IsChecked = false; summary.SaveButton.IsEnabled = false; }
            if (childEditor is ProjectMemoryEditorWindow project) project.InvalidateContext();
            if (childEditor is EpisodeMemoryEditorWindow episode) episode.InvalidateContext();
            childEditor?.Close(); childEditor = null;
        }
        finally { replacingView = false; }
    }
    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true; expiryCheck.Stop(); lifetime.Cancel(); projectRequest++;
        assistant.Memory.Changed -= ExternalChanged; assistant.Changed -= ExternalChanged;
        assistant.MemoryContextChanged -= ExternalChanged;
        ClearIdentityContent();
    }
    private void CloseSourceWindow()
    {
        var current = sourceWindow; sourceWindow = null; sourceSnapshot = null;
        if (current == null) return;
        current.DetailsBox.Clear(); current.Close();
    }
    private void ReloadRules()
    {
        if (!SameProfile()) return;
        try
        {
            var captured = assistant.Memory.CaptureEditorSnapshot();
            if (!SameProfile() || captured.Profile != profile) return;
            string location = assistant.Memory.RulesPath ?? "隔离测试中的临时规则";
            if (!SameProfile()) return;
            rulesSnapshot = captured; RulesBox.Text = captured.Rules; RulesLocation.Text = location;
            RulesStatus.Text = "已重新读取当前规则。";
        }
        catch (Exception ex) { if (SameProfile()) RulesStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private bool ReloadMemories()
    {
        if (!SameProfile()) return false;
        try
        {
            var captured = assistant.Memory.CaptureEditorSnapshot();
            if (!SameProfile() || captured.Profile != profile) return false;
            string? selectedId = (MemoryList.SelectedItem as AssistantMemoryEntry)?.Id;
            replacingView = true;
            try { memorySnapshot = captured; ApplyMemoryFilter(selectedId); MemoryText.Clear(); }
            finally { replacingView = false; }
            RemoveWithdrawnMemories();
            MemoryList.SelectedItem = MemoryList.Items.Cast<AssistantMemoryEntry>().SingleOrDefault(entry => entry.Id == selectedId);
            UpdateMemorySelection();
            UpdateMemoryCount();
            MemoryStatus.Text = "已重新读取当前记忆。";
            return true;
        }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); return false; }
    }
    private void ApplyMemoryFilter(string? selected)
    {
        var entries = memorySnapshot?.Entries ?? [];
        MemoryList.ItemsSource = entries.Where(entry => memoryQuery.Length == 0
            || entry.Text.Contains(memoryQuery, StringComparison.OrdinalIgnoreCase)
            || entry.Id.Contains(memoryQuery, StringComparison.OrdinalIgnoreCase)
            || entry.SourceSummary.Contains(memoryQuery, StringComparison.OrdinalIgnoreCase)
            || ProjectScopeSummary(entry).Contains(memoryQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        MemoryList.SelectedItem = MemoryList.Items.Cast<AssistantMemoryEntry>().SingleOrDefault(entry => entry.Id == selected);
    }
    private void UpdateMemoryCount() => MemoryCountText.Text =
        $"显示 {MemoryList.Items.Count} / {memorySnapshot?.Entries.Length ?? 0} 条 · 最多 {AssistantMemoryStore.MaxEntries} 条，每条 {AssistantMemoryStore.MaxEntryChars} 字符。记忆由当前 Windows 用户加密保存。";
    internal void SearchMemories()
    {
        if (!SameProfile()) return;
        var selected = MemoryList.SelectedItem as AssistantMemoryEntry;
        if (MemoryText.Text.Length > 0 && MemoryText.Text != selected?.Text)
        { MemoryStatus.Text = "编辑内容尚未保存。请先保存，或刷新列表后再搜索。"; return; }
        memoryQuery = MemorySearchBox.Text.Trim();
        replacingView = true;
        try { ApplyMemoryFilter(selected?.Id); if (MemoryList.SelectedItem == null) MemoryText.Clear(); }
        finally { replacingView = false; }
        CloseSourceWindow(); UpdateMemorySelection(); UpdateMemoryCount();
        MemoryStatus.Text = memorySnapshot != null && !SnapshotCurrent(memorySnapshot) ? ConflictMessage
            : MemoryList.Items.Count == 0 ? "没有匹配的记忆；清空搜索可查看全部。" : "已筛选当前列表；搜索不会修改记忆。";
    }
    private void SearchMemoriesClicked(object sender, RoutedEventArgs e) => SearchMemories();
    private void ClearMemorySearchClicked(object sender, RoutedEventArgs e) { MemorySearchBox.Clear(); SearchMemories(); }
    private void MemorySearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; SearchMemories(); } }

    private void EnabledChanged(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) assistant.SetPersonalizationEnabled(UseMemoryBox.IsChecked == true); }
        catch (Exception ex) { if (SameProfile()) RulesStatus.Text = ChatGptAssistant.Explain(ex); }
        if (SameProfile()) UseMemoryBox.IsChecked = settings.ChatGpt.PersonalizationEnabled;
    }
    private void AutoMemoryChanged(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) assistant.SetAutoMemoryEnabled(AutoMemoryBox.IsChecked == true); }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
        if (SameProfile()) AutoMemoryBox.IsChecked = settings.ChatGpt.AutoMemoryEnabled;
    }
    private void SaveRulesClicked(object sender, RoutedEventArgs e)
    {
        if (!SameProfile()) return;
        try
        {
            if (rulesSnapshot == null) { RulesStatus.Text = "请先重新读取规则后再保存。"; return; }
            writing = true; assistant.Memory.SaveRulesFromEditor(rulesSnapshot, RulesBox.Text);
            if (!SameProfile()) return;
            var captured = assistant.Memory.CaptureEditorSnapshot();
            if (!SameProfile()) return;
            if (captured.Rules != RulesBox.Text)
            { RulesStatus.Text = "本次规则已保存，但保存后又有更新。请重新读取核对；当前编辑内容仍保留。"; return; }
            rulesSnapshot = captured;
            RulesStatus.Text = "规则已保存，下次回答生效。";
        }
        catch (AssistantMemoryEditorConflictException ex) { if (SameProfile()) RulesStatus.Text = ex.Message; }
        catch (Exception)
        { if (SameProfile()) RulesStatus.Text = "本次保存未能正常结束，请重新读取规则核对；编辑内容仍保留。"; }
        finally
        {
            writing = false;
            if (SameProfile() && memorySnapshot != null && !SnapshotCurrent(memorySnapshot)) MemoryStatus.Text = ConflictMessage;
        }
    }
    private void ReloadRulesClicked(object sender, RoutedEventArgs e) { if (SameProfile()) ReloadRules(); }
    private void OpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile() && assistant.Memory.ProfileDirectory is string directory) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { directory } }); }
        catch { if (SameProfile()) RulesStatus.Text = "未能打开文件夹，请按上方路径手动打开。"; }
    }
    private void MemorySelected(object sender, SelectionChangedEventArgs e) => UpdateMemorySelection();
    private void UpdateMemorySelection()
    {
        if (replacingView || !SameProfile()) return;
        bool summary = MemoryList.SelectedItem is AssistantMemoryEntry { Metadata.Kind: AssistantMemoryKind.EpisodeSummary };
        bool project = MemoryList.SelectedItem is AssistantMemoryEntry { Metadata.Scope.Kind: AssistantMemoryScopeKind.Project };
        MemoryText.IsReadOnly = summary || project;
        AddMemoryButton.IsEnabled = !summary && !project;
        UpdateMemoryButton.Content = project ? "纠正所选项目记忆…" : summary ? "纠正所选摘要…" : "保存所选修改";
        if (memorySnapshot != null && !SnapshotCurrent(memorySnapshot))
        { MemoryStatus.Text = ConflictMessage; return; }
        if (MemoryList.SelectedItem is AssistantMemoryEntry entry)
        {
            MemoryText.Text = entry.Text;
            MemorySourceSummary.Text = entry.SourceSummary + (project ? "\n" + ProjectScopeSummary(entry) : "");
        }
        else MemorySourceSummary.Text = "选择一条记忆，可查看它的来源。";
    }
    private async void SourceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SameProfile()) return;
            if (MemoryList.SelectedItem is not AssistantMemoryEntry entry)
            { MemoryStatus.Text = "请先选择要查看来源的记忆。"; return; }
            if (memorySnapshot == null || !SnapshotCurrent(memorySnapshot))
            { MemoryStatus.Text = "这条记忆已经变化或删除，请刷新列表。当前编辑内容仍保留。"; return; }
            if (entry.Metadata?.Events.IsEmpty == false && entry.Metadata.InvalidatedAtUtc == null)
            { await ShowEpisodeEditorAsync(entry.Id); return; }
            var details = assistant.Memory.SourceDetails(entry.Id);
            if (!SameProfile()) return;
            if (details == null || !SnapshotCurrent(memorySnapshot))
            { MemoryStatus.Text = "这条记忆已经变化或删除，请刷新列表。当前编辑内容仍保留。"; return; }
            var viewer = new AssistantMemorySourceWindow(details.Detail) { Owner = this };
            sourceWindow = viewer; sourceSnapshot = memorySnapshot;
            try { viewer.ShowDialog(); }
            finally { if (ReferenceEquals(sourceWindow, viewer)) { sourceWindow = null; sourceSnapshot = null; } viewer.DetailsBox.Clear(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private void AddClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SameProfile()) return;
            if (MemoryList.SelectedItem is AssistantMemoryEntry { Metadata.Scope.Kind: AssistantMemoryScopeKind.Project })
            { MemoryStatus.Text = "项目记忆请使用专用入口核对项目范围，不能从此处另存为个人记忆。"; return; }
            if (memorySnapshot == null) { MemoryStatus.Text = "请先刷新列表后再保存。"; return; }
            writing = true;
            var saved = assistant.Memory.AddFromEditor(memorySnapshot, MemoryText.Text);
            ShowMemorySaved("已保存 [" + saved.Id + "]，重启后保留。");
        }
        catch (AssistantMemoryEditorConflictException ex) { if (SameProfile()) MemoryStatus.Text = ex.Message; }
        catch (AssistantMemoryCommittedNotificationException) { ShowMemoryNotificationFailure(); }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
        finally
        {
            writing = false;
            if (SameProfile() && rulesSnapshot != null && !SnapshotCurrent(rulesSnapshot)) RulesStatus.Text = ConflictMessage;
        }
    }
    private async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SameProfile()) return;
            if (MemoryList.SelectedItem is not AssistantMemoryEntry entry) { MemoryStatus.Text = "请先选择要修改的条目。"; return; }
            if (entry.Metadata?.Scope.Kind == AssistantMemoryScopeKind.Project) { await ShowProjectEditorAsync(entry.Id); return; }
            if (entry.Metadata?.Events.IsEmpty == false) { await ShowEpisodeEditorAsync(entry.Id); return; }
            if (entry.Metadata?.Kind == AssistantMemoryKind.EpisodeSummary) { ShowSummaryEditor(entry.Id); return; }
            if (memorySnapshot == null) { MemoryStatus.Text = "请先刷新列表后再保存。"; return; }
            writing = true; assistant.Memory.UpdateFromEditor(memorySnapshot, entry.Id, MemoryText.Text);
            ShowMemorySaved("所选记忆已修改。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AssistantMemoryEditorConflictException ex) { if (SameProfile()) MemoryStatus.Text = ex.Message; }
        catch (AssistantMemoryCommittedNotificationException) { ShowMemoryNotificationFailure(); }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
        finally
        {
            writing = false;
            if (SameProfile() && rulesSnapshot != null && !SnapshotCurrent(rulesSnapshot)) RulesStatus.Text = ConflictMessage;
        }
    }
    private void ShowMemorySaved(string message)
    {
        if (!SameProfile()) return;
        if (!ReloadMemories()) message += " 列表暂时未能刷新，请重新打开核对，无需重复保存。";
        if (SameProfile()) MemoryStatus.Text = message;
    }
    private void ShowMemoryNotificationFailure()
    {
        if (SameProfile()) MemoryStatus.Text = "记忆修改已保存，但界面刷新通知未完成。请刷新列表核对，无需重复保存。";
    }
    private void SummaryClicked(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) ShowSummaryEditor(null); }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private async void EpisodeClicked(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) await ShowEpisodeEditorAsync(null); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private async void ProjectEpisodeClicked(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) await ShowEpisodeEditorAsync(null, true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private async Task ShowEpisodeEditorAsync(string? id, bool projectScope = false)
    {
        var editor = await assistant.CreateEpisodeMemoryEditorAsync(id, lifetime.Token, projectScope);
        if (!SameProfile()) { editor.InvalidateContext(); editor.Close(); return; }
        childEditor = editor; editor.Owner = this;
        bool saved;
        try { saved = editor.ShowDialog() == true; }
        finally { if (ReferenceEquals(childEditor, editor)) childEditor = null; }
        if (SameProfile() && saved) ShowMemorySaved(editor.SavedWarning ?? "情景摘要已保存，检索时可核对原始事件。");
    }
    private async void ProjectClicked(object sender, RoutedEventArgs e)
    {
        try { if (SameProfile()) await ShowProjectEditorAsync(null); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
    }
    private static string ProjectScopeSummary(AssistantMemoryEntry entry)
        => ProjectMemoryScopeConverter.Describe(entry);
    private async Task ShowProjectEditorAsync(string? entryId)
    {
        int request = ++projectRequest;
        ProjectMemoryButton.IsEnabled = false; UpdateMemoryButton.IsEnabled = false;
        try
        {
            var editor = await assistant.CreateProjectMemoryEditorAsync(entryId, lifetime.Token);
            if (!SameProfile() || request != projectRequest)
            { editor.InvalidateContext(); editor.Close(); return; }
            childEditor = editor; editor.Owner = this;
            bool savedResult;
            try { savedResult = editor.ShowDialog() == true; }
            finally { if (ReferenceEquals(childEditor, editor)) childEditor = null; }
            if (!SameProfile() || !savedResult) return;
            if (editor.SavedEntry is not { } saved)
            { MemoryStatus.Text = "保存结果暂时无法核对，请刷新列表后再操作。"; return; }
            string message = editor.SavedWarning ?? $"项目记忆已保存（{saved.Id}），只在已核对的项目范围使用。";
            try
            {
                if (!SameProfile()) return;
                if (ReloadMemories())
                    MemoryList.SelectedItem = MemoryList.Items.Cast<AssistantMemoryEntry>().SingleOrDefault(item => item.Id == saved.Id);
                else message += " 列表暂时未能刷新，请重新打开核对，无需重复保存。";
            }
            catch { message += " 列表暂时未能刷新，请重新打开核对，无需重复保存。"; }
            if (SameProfile()) MemoryStatus.Text = message;
        }
        finally
        {
            if (SameProfile() && request == projectRequest)
            { ProjectMemoryButton.IsEnabled = true; UpdateMemoryButton.IsEnabled = true; }
        }
    }
    private void ShowSummaryEditor(string? summaryId)
    {
        var editor = assistant.CreateSummaryMemoryEditor(summaryId);
        if (!SameProfile()) { editor.SourceList.Items.Clear(); editor.BodyBox.Clear(); editor.Close(); return; }
        childEditor = editor; editor.Owner = this;
        bool savedResult;
        try { savedResult = editor.ShowDialog() == true; }
        finally { if (ReferenceEquals(childEditor, editor)) childEditor = null; }
        if (!SameProfile() || !savedResult) return;
        if (editor.SavedEntry is not { } saved)
        { MemoryStatus.Text = "保存结果暂时无法核对，请刷新列表后再操作。"; return; }
        string message = editor.SavedWarning ?? (summaryId == null
            ? $"记忆摘要已保存（{saved.Id}）；来源变化后停用。"
            : $"记忆摘要已保存（{saved.Id}），所选摘要已纠正，来源版本已重新核对。");
        try
        {
            if (!SameProfile()) return;
            if (ReloadMemories())
                MemoryList.SelectedItem = MemoryList.Items.Cast<AssistantMemoryEntry>().SingleOrDefault(item => item.Id == saved.Id);
            else message += " 列表暂时未能刷新，请重新打开后核对，无需重复保存。";
        }
        catch { message += " 列表暂时未能刷新，请重新打开后核对，无需重复保存。"; }
        if (SameProfile()) MemoryStatus.Text = message;
    }
    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SameProfile()) return;
            if (MemoryList.SelectedItem is not AssistantMemoryEntry entry) { MemoryStatus.Text = "请先选择要删除的条目。"; return; }
            if (memorySnapshot == null) { MemoryStatus.Text = "请先刷新列表后再删除。"; return; }
            writing = true; assistant.Memory.DeleteFromEditor(memorySnapshot, entry.Id);
            ShowMemorySaved("所选长期记忆已删除。");
        }
        catch (AssistantMemoryEditorConflictException)
        { if (SameProfile()) MemoryStatus.Text = "内容已在别处变化，尚未删除。请先刷新列表并核对；当前编辑内容仍保留。"; }
        catch (AssistantMemoryCommittedNotificationException) { ShowMemoryNotificationFailure(); }
        catch (Exception ex) { if (SameProfile()) MemoryStatus.Text = ChatGptAssistant.Explain(ex); }
        finally
        {
            writing = false;
            if (SameProfile() && rulesSnapshot != null && !SnapshotCurrent(rulesSnapshot)) RulesStatus.Text = ConflictMessage;
        }
    }
    private void ReloadMemoryClicked(object sender, RoutedEventArgs e) { if (SameProfile()) ReloadMemories(); }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
