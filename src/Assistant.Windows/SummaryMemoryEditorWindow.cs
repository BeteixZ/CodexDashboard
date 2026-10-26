using System;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexDashboard;

internal sealed class SummaryMemoryEditorWindow : Window
{
    private readonly AssistantMemoryStore store;
    private SummaryMemoryEditorSnapshot snapshot;
    private readonly DispatcherTimer profileCheck;
    private bool profileChanged, viewChanged, saving, closed;
    private DateTimeOffset? nextExpiry;
    internal ListBox SourceList { get; }
    internal TextBox BodyBox { get; }
    internal CheckBox ReviewedBox { get; }
    internal Button SaveButton { get; }
    internal TextBlock StatusText { get; }
    internal AssistantMemoryEntry? SavedEntry { get; private set; }
    internal string? SavedWarning { get; private set; }

    internal SummaryMemoryEditorWindow(AssistantMemoryStore store, string? summaryId = null)
    {
        this.store = store; snapshot = store.CaptureDerivedSummaryEditor(summaryId);
        Title = summaryId == null ? "整理记忆摘要" : "纠正记忆摘要";
        Width = 820; Height = 720; MinWidth = 620; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("ShellWindow");
        Brush text = (Brush)FindResource("Text"), panelBrush = (Brush)FindResource("Panel"), line = (Brush)FindResource("Line");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "选择 2–8 条来源，再亲自填写并审阅摘要。这是你整理的个人记忆，不代表任务经历、执行结果或新的操作授权。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });

        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        ReviewedBox = new CheckBox { Name = "ReviewedBox", Content = "我已核对所选来源和摘要正文，确认保存这份整理结果。",
            Foreground = text, Margin = new Thickness(0, 12, 0, 8) };
        bottom.Children.Add(ReviewedBox);
        StatusText = new TextBlock { Name = "StatusText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "来源被修改、删除或到期后，这份摘要会失效；失效正文不会自动恢复。" };
        bottom.Children.Add(StatusText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 86, Padding = new Thickness(15, 8, 15, 8),
            Margin = new Thickness(0, 0, 10, 0), Foreground = text, Background = panelBrush };
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        SaveButton = new Button { Name = "SaveButton", Content = summaryId == null ? "确认保存摘要" : "确认纠正摘要", MinWidth = 128,
            Padding = new Thickness(15, 8, 15, 8), Foreground = text, Background = panelBrush };
        SaveButton.Click += (_, _) => { if (TrySave()) DialogResult = true; }; buttons.Children.Add(SaveButton); bottom.Children.Add(buttons);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var sourceLabel = new TextBlock { Text = "来源记忆（点击选择，可多选）", Margin = new Thickness(0, 0, 0, 6) };
        body.Children.Add(sourceLabel);
        SourceList = new ListBox { Name = "SourcesList", SelectionMode = SelectionMode.Multiple, Foreground = text, Background = panelBrush,
            BorderBrush = line, Padding = new Thickness(6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var source in snapshot.Sources)
        {
            var row = new ListBoxItem { Tag = source, Padding = new Thickness(8), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = $"[{source.Id}] v{source.Metadata!.Version}\n{source.Text}", TextWrapping = TextWrapping.Wrap } };
            SourceList.Items.Add(row);
            if (snapshot.Summary?.Metadata?.Dependencies.Any(r => r.Id == source.Id && r.Version == source.Metadata.Version) == true)
                row.IsSelected = true;
        }
        Grid.SetRow(SourceList, 1); body.Children.Add(SourceList);
        var textLabel = new TextBlock { Text = $"摘要正文（最多 {AssistantMemoryStore.MaxEntryChars} 字符）", Margin = new Thickness(0, 12, 0, 6) };
        Grid.SetRow(textLabel, 2); body.Children.Add(textLabel);
        BodyBox = new TextBox { Name = "SummaryTextBox", Text = snapshot.Summary?.Text ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = text, Background = panelBrush,
            BorderBrush = line, Padding = new Thickness(10) };
        Grid.SetRow(BodyBox, 3); body.Children.Add(BodyBox); panel.Children.Add(body); Content = panel;
        SourceList.SelectionChanged += (_, _) => UpdateSaveAvailability();
        BodyBox.TextChanged += (_, _) => UpdateSaveAvailability();
        ReviewedBox.Checked += (_, _) => UpdateSaveAvailability(); ReviewedBox.Unchecked += (_, _) => UpdateSaveAvailability();
        profileCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        profileCheck.Tick += (_, _) => { if (CheckProfile() && nextExpiry <= DateTimeOffset.UtcNow) RefreshSources(); };
        store.Changed += MemoryChanged;
        Loaded += (_, _) => { CheckProfile(); profileCheck.Start(); };
        Closed += (_, _) => { closed = true; store.Changed -= MemoryChanged; profileCheck.Stop(); };
        if (snapshot.Sources.Length < 2) StatusText.Text = "至少需要两条明确确认、尚未到期的个人记忆。旧记录若未记录确认状态，不会被当作已确认来源。";
        UpdateSaveAvailability(); RefreshSources();
    }
    private void MemoryChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshSources();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshSources));
    }
    private void RefreshSources()
    {
        if (closed || profileChanged || saving || SavedEntry != null || !CheckProfile()) return;
        try
        {
            var view = store.CaptureMemoryView(); nextExpiry = view.NextExpiryUtc;
            var selected = SourceList.SelectedItems.Cast<ListBoxItem>().Select(row => (AssistantMemoryEntry)row.Tag).ToArray();
            bool clearDraft = snapshot.Summary != null && !view.ContainsVersion(snapshot.Summary)
                || selected.Any(entry => !view.ContainsVersion(entry));
            if (clearDraft)
            {
                viewChanged = true; BodyBox.Clear(); ReviewedBox.IsChecked = false;
                snapshot = snapshot with { Summary = null };
            }
            foreach (var row in SourceList.Items.Cast<ListBoxItem>().Where(row => !view.ContainsVersion((AssistantMemoryEntry)row.Tag)).ToArray())
                SourceList.Items.Remove(row);
            snapshot = snapshot with { Sources = snapshot.Sources.Where(view.ContainsVersion).ToImmutableArray() };
            if (clearDraft) StatusText.Text = "来源或摘要已删除、纠正或到期，已清除相关草稿。请重新打开并选择有效来源。";
            UpdateSaveAvailability();
        }
        catch (ChatGptException)
        {
            viewChanged = true; BodyBox.Clear(); SourceList.Items.Clear(); ReviewedBox.IsChecked = false;
            snapshot = snapshot with { Sources = [], Summary = null };
            StatusText.Text = "来源暂时无法读取，旧视图已清除。请重新打开核对。"; UpdateSaveAvailability();
        }
    }
    private void UpdateSaveAvailability() => SaveButton.IsEnabled = SavedEntry == null && !profileChanged && !viewChanged && !closed && !saving && ReviewedBox.IsChecked == true
        && SourceList.SelectedItems.Count is >= 2 and <= 8 && !string.IsNullOrWhiteSpace(BodyBox.Text)
        && BodyBox.Text.Trim().Length <= AssistantMemoryStore.MaxEntryChars;
    private bool CheckProfile()
    {
        if (!profileChanged && store.Profile == snapshot.Profile) return true;
        profileChanged = true; SourceList.Items.Clear(); BodyBox.Clear(); ReviewedBox.IsChecked = false;
        snapshot = snapshot with { Sources = [], Summary = null };
        StatusText.Text = "账户或微信绑定已变化，已清除这次编辑内容。请关闭后在正确账户重新打开。";
        SaveButton.IsEnabled = false; return false;
    }
    internal bool TrySave()
    {
        if (!CheckProfile()) return false;
        if (SavedEntry != null) return true;
        if (viewChanged || closed) return false;
        if (ReviewedBox.IsChecked != true)
        { StatusText.Text = "请先审阅来源与正文，并勾选确认。"; return false; }
        try
        {
            saving = true;
            var selected = SourceList.SelectedItems.Cast<ListBoxItem>().Select(row => (AssistantMemoryEntry)row.Tag)
                .Select(AssistantMemoryLayers.Reference).ToArray();
            SavedEntry = snapshot.Summary is { } prior
                ? store.CorrectDerivedSummaryFromEditor(prior.Id, prior.Metadata!.Version, BodyBox.Text, selected, snapshot.Stamp)
                : store.CreateDerivedSummaryFromEditor(BodyBox.Text, selected, snapshot.Stamp);
            UpdateSaveAvailability();
            return true;
        }
        catch (SummaryMemorySavedNotificationException committed)
        {
            SavedEntry = committed.SavedEntry;
            SavedWarning = $"摘要已保存（编号 {SavedEntry.Id}），但界面刷新通知失败。请重新打开记忆列表查看，不要重复保存。";
            StatusText.Text = SavedWarning;
            UpdateSaveAvailability();
            return true;
        }
        catch (Exception error)
        {
            SavedEntry = null;
            StatusText.Text = error is InvalidOperationException && error.Message.StartsWith("summary_", StringComparison.Ordinal)
                ? "来源、版本或账户已经变化，本次未保存。请关闭后重新选择来源并审阅。"
                : ChatGptAssistant.Explain(error);
            return false;
        }
        finally { saving = false; UpdateSaveAvailability(); }
    }
}
