using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexDashboard;

internal sealed class ProjectMemoryEditorWindow : Window
{
    private readonly AssistantMemoryStore store;
    private readonly ProjectMemoryEditorSnapshot snapshot;
    private readonly IProjectMemoryResolver resolver;
    private readonly Func<CancellationToken, Task<ProjectMemoryTarget?>> provider;
    private readonly Func<bool> contextCurrent;
    private readonly DispatcherTimer targetCheck;
    private readonly CancellationTokenSource lifetime = new();
    private volatile bool invalidated;
    private bool checking, saving, closed;
    internal TextBlock TargetText { get; }
    internal Expander ScopeDetails { get; }
    internal TextBlock ScopeDetailsText { get; }
    internal TextBox BodyBox { get; }
    internal CheckBox ReviewedBox { get; }
    internal Button SaveButton { get; }
    internal TextBlock StatusText { get; }
    internal AssistantMemoryEntry? SavedEntry { get; private set; }
    internal string? SavedWarning { get; private set; }

    internal ProjectMemoryEditorWindow(AssistantMemoryStore store, ProjectMemoryEditorSnapshot snapshot,
        IProjectMemoryResolver resolver, Func<CancellationToken, Task<ProjectMemoryTarget?>> provider,
        Func<bool> contextCurrent)
    {
        this.store = store; this.snapshot = snapshot; this.resolver = resolver;
        this.provider = provider; this.contextCurrent = contextCurrent;
        Title = snapshot.Entry == null ? "保存项目记忆" : "纠正项目记忆";
        Width = 820; Height = 720; MinWidth = 640; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("ShellWindow");
        Brush text = (Brush)FindResource("Text"), panelBrush = (Brush)FindResource("Panel"), line = (Brush)FindResource("Line");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "亲自填写并审阅，只在下面这个项目中使用。这些记忆不会授予文件操作、执行任务或跨项目控制的权限。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        TargetText = new TextBlock { Name = "ProjectTargetText", Text = DescribeTarget(snapshot), TextWrapping = TextWrapping.Wrap,
            Foreground = text, Margin = new Thickness(0, 0, 0, 4) };
        ScopeDetailsText = new TextBlock { Name = "ProjectScopeDetailsText", Text = DescribeDetails(snapshot), TextWrapping = TextWrapping.Wrap,
            Foreground = text, Margin = new Thickness(18, 5, 0, 8), FontSize = 12 };
        ScopeDetails = new Expander { Name = "ProjectScopeDetails", IsExpanded = false, Foreground = text,
            Header = new TextBlock { Text = "范围详情", Foreground = text }, Content = ScopeDetailsText };
        var scopePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        scopePanel.Children.Add(TargetText); scopePanel.Children.Add(ScopeDetails);
        heading.Children.Add(new ScrollViewer { Content = scopePanel, MaxHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        ReviewedBox = new CheckBox { Name = "ReviewedBox", Foreground = text, Margin = new Thickness(0, 12, 0, 8),
            Content = new TextBlock { Text = "我已核对项目范围与正文，确认只为此项目保存。", TextWrapping = TextWrapping.Wrap, Foreground = text } };
        bottom.Children.Add(ReviewedBox);
        StatusText = new TextBlock { Name = "StatusText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "项目来源、当前任务轮次或账户变化后，本次编辑内容会清除。正文不会由助手自动填写。" };
        bottom.Children.Add(StatusText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 86, Padding = new Thickness(15, 8, 15, 8),
            Margin = new Thickness(0, 0, 10, 0), Foreground = text, Background = panelBrush };
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        SaveButton = new Button { Name = "SaveButton", Content = snapshot.Entry == null ? "确认保存项目记忆" : "确认纠正项目记忆",
            MinWidth = 150, Padding = new Thickness(15, 8, 15, 8), Foreground = text, Background = panelBrush };
        SaveButton.Click += async (_, _) => { if (await TrySaveAsync() && IsVisible) DialogResult = true; };
        buttons.Children.Add(SaveButton); bottom.Children.Add(buttons);
        var body = new DockPanel();
        var label = new TextBlock { Text = $"项目记忆正文（最多 {AssistantMemoryStore.MaxEntryChars} 字符）", Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Top); body.Children.Add(label);
        BodyBox = new TextBox { Name = "ProjectMemoryTextBox", Text = snapshot.Entry?.Text ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = text,
            Background = panelBrush, BorderBrush = line, Padding = new Thickness(10) };
        body.Children.Add(BodyBox); panel.Children.Add(body); Content = panel;
        BodyBox.TextChanged += (_, _) => UpdateSaveAvailability();
        ReviewedBox.Checked += (_, _) => UpdateSaveAvailability(); ReviewedBox.Unchecked += (_, _) => UpdateSaveAvailability();
        targetCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        targetCheck.Tick += async (_, _) => { if (!saving && !checking && SavedEntry == null) await RefreshTargetAsync(); };
        Loaded += async (_, _) => { targetCheck.Start(); await RefreshTargetAsync(); };
        Closed += (_, _) => { closed = true; targetCheck.Stop(); lifetime.Cancel(); };
        UpdateSaveAvailability();
    }
    private static string DescribeTarget(ProjectMemoryEditorSnapshot snapshot)
    {
        var target = snapshot.Target; var project = target.Project;
        return "范围：" + HostIdentity.DisplayName(project.HostId) + " · " + ProjectMemoryScopeConverter.BackendLabel(project.Backend)
            + "\n任务配置：" + (project.ProfileId ?? "桌面默认配置")
            + "\n项目目录标识：" + project.CanonicalDirectory
            + "\n当前任务：" + target.Task.ThreadId
            + (snapshot.Entry is { } entry ? $"\n正在纠正：{entry.Id} v{entry.Metadata!.Version}" : "");
    }
    private static string DescribeDetails(ProjectMemoryEditorSnapshot snapshot)
    {
        var target = snapshot.Target; var project = target.Project;
        return "主机标识：" + project.HostId + "\n后端标识：" + project.Backend
            + "\n配置标识：" + (project.ProfileId ?? "无") + "\n账户范围标识：" + snapshot.Profile
            + "\n核对来源：" + target.EvidenceSource + "\n当前轮次标识：" + target.Task.TurnId
            + (project.RemoteSource == null ? "" : "\n远程主机与配置标识：" + project.RemoteSource + "\n目录标识：" + project.DirectoryIdentity);
    }
    private void UpdateSaveAvailability()
    {
        SaveButton.IsEnabled = !invalidated && !closed && !checking && !saving && SavedEntry == null
            && ReviewedBox.IsChecked == true && !string.IsNullOrWhiteSpace(BodyBox.Text)
            && BodyBox.Text.Trim().Length <= AssistantMemoryStore.MaxEntryChars;
        BodyBox.IsReadOnly = invalidated || saving || SavedEntry != null;
        ReviewedBox.IsEnabled = !invalidated && !saving && SavedEntry == null;
    }
    internal void InvalidateContext()
    {
        invalidated = true;
        void Clear()
        {
            BodyBox.Clear(); ReviewedBox.IsChecked = false; BodyBox.IsReadOnly = true; ReviewedBox.IsEnabled = false;
            TargetText.Text = "项目来源或账户已变化，原范围已停用。";
            ScopeDetailsText.Text = ""; ScopeDetails.IsExpanded = false; ScopeDetails.IsEnabled = false;
            StatusText.Text = SavedEntry is { } saved ? $"项目记忆已保存（{saved.Id}）。当前范围已变化，请回到原账户核对。"
                : "项目、当前任务轮次、来源或账户已变化，已清除本次编辑内容。请关闭后重新选择并核对。";
            UpdateSaveAvailability();
        }
        if (Dispatcher.CheckAccess()) Clear();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Clear));
    }
    internal void CheckContextNow()
    {
        if (closed) return;
        // This notification only checks the captured scope. It never calls the
        // async provider, captures a replacement target, or signs new evidence.
        if (Dispatcher.CheckAccess()) _ = CheckScopeState();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!closed) _ = CheckScopeState(); }));
    }
    private bool CheckScopeState()
    {
        if (invalidated || closed) return false;
        try
        {
            if (!contextCurrent() || store.Profile != snapshot.Profile)
            { InvalidateContext(); return false; }
            var current = resolver.Resolve(snapshot.Target.Task);
            if (current == null || !ProjectMemoryProof.SameEvidence(snapshot.Target, current))
            { InvalidateContext(); return false; }
            ProjectMemoryProof.Validate(current, DateTimeOffset.UtcNow);
            return true;
        }
        catch { InvalidateContext(); return false; }
    }
    private bool CheckLocalState()
    {
        if (!CheckScopeState()) return false;
        try
        {
            if (store.Capture().Stamp == snapshot.Stamp) return true;
        }
        catch { }
        InvalidateContext(); return false;
    }
    internal async Task<bool> RefreshTargetAsync()
    {
        if (checking || SavedEntry != null) return false;
        checking = true; UpdateSaveAvailability();
        try
        {
            if (!CheckLocalState()) return false;
            var current = await provider(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!CheckLocalState() || current == null || !ProjectMemoryProof.SameEvidence(snapshot.Target, current))
            { InvalidateContext(); return false; }
            ProjectMemoryProof.Validate(current, DateTimeOffset.UtcNow);
            return true;
        }
        catch { InvalidateContext(); return false; }
        finally { checking = false; UpdateSaveAvailability(); }
    }
    internal async Task<bool> TrySaveAsync()
    {
        if (SavedEntry != null) return true; // A committed result is never another write attempt.
        if (saving || checking || closed || invalidated) return false;
        if (ReviewedBox.IsChecked != true)
        { StatusText.Text = "请先核对项目范围与正文，并勾选确认。"; return false; }
        string reviewedText = BodyBox.Text;
        saving = true; UpdateSaveAvailability();
        try
        {
            if (!await RefreshTargetAsync() || !CheckLocalState()) return false;
            lifetime.Token.ThrowIfCancellationRequested();
            if (ReviewedBox.IsChecked != true || BodyBox.Text != reviewedText)
            { StatusText.Text = "正文或确认状态已变化，请重新审阅后再保存。"; return false; }
            SavedEntry = store.SaveProjectFromEditor(snapshot, reviewedText, resolver);
            return true;
        }
        catch (ProjectMemorySavedNotificationException committed)
        {
            SavedEntry = committed.SavedEntry;
            SavedWarning = $"项目记忆已保存（编号 {SavedEntry.Id}），但界面刷新通知失败。请重新打开列表核对，不要重复保存。";
            StatusText.Text = SavedWarning; return true;
        }
        catch (Exception error)
        {
            if (error is InvalidOperationException && error.Message.StartsWith("project_memory_", StringComparison.Ordinal))
                InvalidateContext();
            else StatusText.Text = ChatGptAssistant.Explain(error);
            return false;
        }
        finally { saving = false; UpdateSaveAvailability(); }
    }
}
