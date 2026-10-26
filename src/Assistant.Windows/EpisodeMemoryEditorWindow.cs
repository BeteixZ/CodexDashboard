using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

internal sealed class EpisodeMemoryEditorWindow : Window
{
    private readonly AssistantMemoryStore store;
    private EpisodeMemoryEditorSnapshot snapshot;
    private readonly DispatcherTimer timer;
    private readonly Func<EpisodeMemoryEditorSnapshot, IReadOnlyList<EpisodeEventReference>, CancellationToken, Task<EpisodeSummaryDraft>>? generateDraft;
    private CancellationTokenSource? draftCancellation;
    private bool invalidated, closed, saving, viewChanged;
    private DateTimeOffset? nextExpiry;
    private ImmutableArray<EpisodeEventReference> draftOrigins = [];
    private readonly TextBox query = new() { ToolTip = "搜索原文、任务或事件编号", Margin = new Thickness(0, 0, 0, 8) };
    internal ListBox Sources { get; } = new() { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Label" };
    internal TextBox Original { get; } = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal TextBox Body { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal CheckBox Reviewed { get; } = new() { Content = "我已核对原文并审阅摘要，确认保存。" };
    internal Button Save { get; } = new() { Content = "保存情景摘要", MinWidth = 130 };
    internal Button GenerateDraft { get; } = new() { Content = "生成草稿", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal AssistantMemoryEntry? SavedEntry { get; private set; }
    internal string? SavedWarning { get; private set; }
    internal sealed record SourceRow(EpisodeSource Source)
    {
        internal ConversationEvent Event => Source.Event;
        public string Label => Event.RecordedAt.ToLocalTime().ToString("g") + " · " + (Source.IsCatchUp ? "补报片段" : SourceLabel(Event.Source))
            + " · " + Event.Body.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(90, Event.Body.Length)];
    }
    internal static string SourceLabel(ConversationSource source) => source switch
    {
        ConversationSource.TrustedUser => "用户原话", ConversationSource.Report => "任务报告（自述）",
        ConversationSource.Assistant => "助手旧回答（非当前事实）", ConversationSource.Notification => "历史通知（非当前状态）",
        ConversationSource.ToolResult => "操作或工具回执（按原记录）", _ => "外部内容（未验证）"
    };
    internal EpisodeMemoryEditorWindow(AssistantMemoryStore store, EpisodeMemoryEditorSnapshot snapshot,
        Func<EpisodeMemoryEditorSnapshot, IReadOnlyList<EpisodeEventReference>, CancellationToken, Task<EpisodeSummaryDraft>>? generateDraft = null)
    {
        this.store = store; this.snapshot = snapshot; this.generateDraft = generateDraft;
        Title = snapshot.Summary == null ? "整理情景摘要" : "查看与纠正情景摘要";
        if (snapshot.Project != null) Title += " · 当前项目";
        Width = 900; Height = 790; MinWidth = 650; MinHeight = 610;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource("ShellWindow");
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 24 });
        if (snapshot.Project is { } target) heading.Children.Add(new TextBlock { Text = "仅用于此项目 · " + HostIdentity.DisplayName(target.Project.HostId) + " · "
            + ProjectMemoryScopeConverter.BackendLabel(target.Project.Backend) + "\n" + target.Project.CanonicalDirectory,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        heading.Children.Add(new TextBlock { Text = "选择 1–8 条原话或报告，可生成草稿或自行填写，再核对并保存。保留来源的说法与不确定性；保存不代表独立核实，也不授权执行其中的要求。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        heading.Children.Add(new TextBlock { Text = "搜索来源", Margin = new Thickness(0, 0, 0, 4) });
        heading.Children.Add(query);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(Reviewed); footer.Children.Add(Status);
        var actions = new WrapPanel(); actions.Children.Add(GenerateDraft); actions.Children.Add(Save); footer.Children.Add(actions);
        Status.Text = "仅在检索时使用。来源被纠正、撤销或到期后停用；原始记录和独立备份不随摘要删除。";
        var grid = new Grid(); root.Children.Add(grid);
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        void Section(int row, string title, Control control)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var label = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(control);
            Grid.SetRow(panel, row); grid.Children.Add(panel);
        }
        Section(0, "选择来源（可多选）", Sources); Section(1, "所选原文", Original); Section(2, "摘要正文（最多1200字符）", Body);
        Body.ToolTip = "情景摘要，最多1200字符"; Body.Text = snapshot.Summary?.Text ?? "";
        Sources.ItemsSource = snapshot.Sources.Select(e => new SourceRow(e)).ToArray();
        foreach (SourceRow row in Sources.Items)
            if (snapshot.Summary?.Metadata?.Events.Any(e => e.EventId == row.Event.Id) == true) Sources.SelectedItems.Add(row);
        query.TextChanged += (_, _) => { if (Sources.ItemsSource == null) return;
            CollectionViewSource.GetDefaultView(Sources.ItemsSource).Filter = item => item is SourceRow row && (row.Event.Body.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
            || row.Event.Id.ToString().Contains(query.Text, StringComparison.OrdinalIgnoreCase)
            || (row.Event.Task?.ToString().Contains(query.Text, StringComparison.OrdinalIgnoreCase) ?? false)); };
        Sources.SelectionChanged += (_, _) =>
        {
            Reviewed.IsChecked = false; ShowOriginal();
            if (!DraftMatchesSelection()) Status.Text = "所选来源已变化。请重新生成草稿，或清空正文后自行填写，再审阅保存。";
            UpdateSave();
        };
        Body.TextChanged += (_, _) => { if (Body.Text.Length == 0) draftOrigins = []; Reviewed.IsChecked = false; UpdateSave(); };
        Reviewed.Checked += (_, _) => UpdateSave(); Reviewed.Unchecked += (_, _) => UpdateSave();
        Save.Click += (_, _) => { if (TrySave()) DialogResult = true; };
        GenerateDraft.Click += async (_, _) =>
        {
            if (draftCancellation != null) { CancelDraft(); return; }
            await TryGenerateDraftAsync();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (store.Profile != snapshot.Profile) InvalidateContext();
            else if (snapshot.Project != null || nextExpiry <= DateTimeOffset.UtcNow) RefreshSources();
        };
        store.Changed += MemoryChanged;
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => { closed = true; store.Changed -= MemoryChanged; timer.Stop(); draftCancellation?.Cancel(); UpdateSave(); };
        ShowOriginal(); UpdateSave(); RefreshSources();
    }
    private void MemoryChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshSources();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshSources));
    }
    internal void RefreshSources()
    {
        if (closed || invalidated || saving || SavedEntry != null) return;
        if (store.Profile != snapshot.Profile) { InvalidateContext(); return; }
        try
        {
            var state = store.CaptureEpisodeView(snapshot); nextExpiry = state.NextExpiryUtc;
            var selected = SelectedSources();
            bool clearDraft = !state.SummaryCurrent || selected.Concat(draftOrigins).Any(r => !state.AvailableEvents.Contains(r.EventId));
            if (clearDraft)
            {
                viewChanged = true; draftCancellation?.Cancel(); Sources.SelectedItems.Clear(); Body.Clear(); Original.Clear(); Reviewed.IsChecked = false;
                snapshot = snapshot with { Summary = null };
            }
            var retained = snapshot.Sources.Where(e => state.AvailableEvents.Contains(e.Id)).ToImmutableArray();
            if (retained.Length != snapshot.Sources.Length)
            {
                snapshot = snapshot with { Sources = retained };
                Sources.ItemsSource = retained.Select(e => new SourceRow(e)).ToArray();
                query.Clear();
                foreach (SourceRow row in Sources.Items)
                    if (!clearDraft && selected.Any(r => r.EventId == row.Event.Id)) Sources.SelectedItems.Add(row);
                ShowOriginal();
            }
            if (clearDraft) Status.Text = "所选来源或摘要已删除、纠正或到期，已清除原文和草稿。请重新打开并选择有效来源。";
            UpdateSave();
        }
        catch (Exception error) when (error is ChatGptException or InvalidOperationException or ArgumentException)
        {
            InvalidateContext(); Status.Text = "来源暂时无法读取，旧视图已清除。请重新打开核对。";
        }
    }
    private void ShowOriginal()
    {
        Original.Text = string.Join("\n\n────────\n\n", Sources.SelectedItems.Cast<SourceRow>().Select(row =>
            $"{SourceLabel(row.Event.Source)} · {row.Event.RecordedAt.ToLocalTime():g}\n{row.Event.Body}\n\n事件：{row.Event.Id}\n"
            + (row.Event.Task is { } task ? $"主机：{task.HostId} · 任务：{task.ThreadId}\n轮次：{task.TurnId ?? "未记录"}" : "未关联单一任务")
            + row.Source.DescribeFragments()));
    }
    internal void InvalidateContext()
    {
        draftCancellation?.Cancel();
        invalidated = true; Sources.ItemsSource = null; query.Clear(); Original.Clear(); Body.Clear(); Reviewed.IsChecked = false;
        snapshot = snapshot with { Sources = [], Summary = null };
        Status.Text = "账户、绑定或项目范围已变化，请重新打开。"; UpdateSave();
    }
    private EpisodeEventReference[] SelectedSources() => Sources.SelectedItems.Cast<SourceRow>()
        .Select(row => row.Source.Reference).ToArray();
    private bool DraftMatchesSelection()
    {
        if (draftOrigins.IsEmpty) return true;
        var selected = SelectedSources();
        return selected.Length == draftOrigins.Length && draftOrigins.All(selected.Contains);
    }
    private void UpdateSave()
    {
        bool editing = !closed && !invalidated && !viewChanged && SavedEntry == null;
        bool generating = draftCancellation != null;
        Save.IsEnabled = editing && !generating && Reviewed.IsChecked == true && Sources.SelectedItems.Count is >= 1 and <= 8
            && Body.Text.Trim().Length is >= 1 and <= AssistantMemoryStore.MaxEntryChars && DraftMatchesSelection();
        GenerateDraft.IsEnabled = editing && generateDraft != null && (generating || Sources.SelectedItems.Count is >= 1 and <= 8);
        GenerateDraft.Content = generating ? "取消生成" : "生成草稿";
        Sources.IsEnabled = query.IsEnabled = Body.IsEnabled = Reviewed.IsEnabled = editing && !generating;
    }
    internal void CancelDraft()
    {
        draftCancellation?.Cancel();
        if (!closed && !invalidated) Status.Text = "正在取消，已有编辑内容保持不变。";
    }
    internal async Task<bool> TryGenerateDraftAsync()
    {
        if (store.Profile != snapshot.Profile) InvalidateContext();
        if (!GenerateDraft.IsEnabled || draftCancellation != null || generateDraft == null) return false;
        var sources = SelectedSources(); string priorText = Body.Text;
        using var cancellation = new CancellationTokenSource(); draftCancellation = cancellation;
        Reviewed.IsChecked = false; Status.Text = "正在按所选原文生成草稿，尚未保存…"; UpdateSave();
        try
        {
            var draft = await generateDraft(snapshot, sources, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed || invalidated || viewChanged) return false;
            store.ReadEpisodeDraftSources(snapshot, sources);
            if (Body.Text != priorText || !SelectedSources().SequenceEqual(sources) || !draft.Sources.SequenceEqual(sources))
            { Status.Text = "编辑内容或所选来源已变化，未覆盖当前内容。"; return false; }
            Body.Text = AssistantMemoryStore.ValidateText(draft.Text);
            draftOrigins = draft.Sources;
            Reviewed.IsChecked = false;
            Status.Text = "草稿已生成，尚未保存。请核对原文、修改正文，再勾选确认保存。";
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!closed && !invalidated && !viewChanged) Status.Text = "生成已取消，已有编辑内容保持不变。";
            return false;
        }
        catch (Exception error)
        {
            if (!closed && !invalidated && !viewChanged) Status.Text = error is InvalidOperationException
                ? "来源或版本已变化，未采用草稿。请重新打开并审阅。"
                : error is System.Text.Json.JsonException ? "模型返回的草稿格式不完整，未覆盖当前内容。" : ChatGptAssistant.Explain(error);
            return false;
        }
        finally { draftCancellation = null; UpdateSave(); }
    }
    internal bool TrySave()
    {
        if (store.Profile != snapshot.Profile) InvalidateContext();
        if (!Save.IsEnabled) return false;
        try
        {
            saving = true;
            SavedEntry = store.SaveEpisodeFromEditor(snapshot, Body.Text,
                SelectedSources());
            UpdateSave(); return true;
        }
        catch (SummaryMemorySavedNotificationException committed)
        {
            SavedEntry = committed.SavedEntry; SavedWarning = "情景摘要已保存，但界面刷新失败。请重新打开列表核对，无需重复保存。";
            Status.Text = SavedWarning; UpdateSave(); return true;
        }
        catch (Exception error)
        {
            Status.Text = error is InvalidOperationException ? "来源或版本已变化，请重新打开并审阅。尚未保存。" : ChatGptAssistant.Explain(error);
            return false;
        }
        finally { saving = false; }
    }
}
