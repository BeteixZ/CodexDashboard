using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core;

namespace CodexDashboard;

public partial class AssistantWorkWindow : Window
{
    private readonly Func<CancellationToken, Task<AssistantWorkspaceSnapshot>> read;
    private readonly Func<ConversationIdentity?, bool>? isCurrent;
    private readonly Func<ConversationIdentity, AssistantConversationCursor?, CancellationToken, Task<AssistantConversationPage>>? readConversationPage;
    private readonly Func<ConversationIdentity, CancellationToken, Task<AssistantConversationPage>>? readConversationExport;
    private bool exportingConversation;
    private readonly Func<ConversationIdentity, string, AssistantConversationCursor?, CancellationToken, Task<AssistantConversationPage>>? searchConversation;
    private string conversationQuery = "";
    private AssistantConversationPage? conversationPage;
    private AssistantConversationRow[] conversationRows = [];
    private CancellationTokenSource? conversationRequest;
    private readonly CancellationTokenSource lifetime = new();
    private ConversationIdentity? displayedIdentity;
    private long viewVersion;
    private bool hasSnapshot;
    private AssistantWorkspaceSnapshot? displayedSnapshot;
    private AssistantCommitmentPage? commitmentPage;
    internal AssistantWorkWindow(Func<CancellationToken, Task<AssistantWorkspaceSnapshot>> read,
        Func<ConversationIdentity?, bool>? isCurrent = null,
        Func<ConversationIdentity, AssistantConversationCursor?, CancellationToken, Task<AssistantConversationPage>>? readConversationPage = null,
        Func<ConversationIdentity, CancellationToken, Task<AssistantConversationPage>>? readConversationExport = null,
        Func<ConversationIdentity, string, AssistantConversationCursor?, CancellationToken, Task<AssistantConversationPage>>? searchConversation = null)
    {
        InitializeComponent(); this.read = read; this.isCurrent = isCurrent; this.readConversationPage = readConversationPage;
        this.readConversationExport = readConversationExport;
        this.searchConversation = searchConversation;
        UpdateCommitmentButtons();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { lifetime.Cancel(); ClearRecords("窗口已关闭。"); };
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void WorkTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkTabs) || ConversationTab == null || ConversationDiagnostics == null || DiagnosticText == null) return;
        bool conversation = ConversationTab.IsSelected || DependenciesTab?.IsSelected == true;
        DiagnosticText.Visibility = conversation ? Visibility.Collapsed : Visibility.Visible;
        ConversationDiagnostics.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
        ConversationDiagnostics.IsExpanded = false;
    }
    internal async Task RefreshAsync()
    {
        if (!RefreshButton.IsEnabled || lifetime.IsCancellationRequested) return;
        RefreshButton.IsEnabled = false;
        long expectedVersion = ++viewVersion;
        displayedSnapshot = null;
        conversationQuery = ""; ConversationSearchBox.Clear();
        commitmentPage = null;
        UpdateCommitmentButtons();
        ClearConversationPage("正在刷新对话记录，请稍候。");
        CommitmentsGrid.SelectedItem = null;
        ClearCommitmentDetails("正在刷新安排，请稍候。");
        OperationsGrid.SelectedItem = null;
        MessagesGrid.SelectedItem = null; MessageDetailBox.Clear();
        ClearOperationDetails("正在刷新操作记录，请稍候。");
        ClearDependencyRecords();
        try
        {
            var snapshot = await read(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (viewVersion != expectedVersion || isCurrent?.Invoke(snapshot.Identity) == false)
            { ClearRecords("账户或微信绑定已经变化，请刷新当前记录。"); return; }
            displayedIdentity = snapshot.Identity; hasSnapshot = true; displayedSnapshot = snapshot; viewVersion++;
            ReadAtText.Text = "读取于 " + snapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                + " · 此页只查看记录，刷新不会发送消息或执行任务";
            ContextText.Text = snapshot.Context;
            DiagnosticText.Text = snapshot.Diagnostic;
            CapabilitiesText.Text = snapshot.Capabilities;
            WorkGrid.ItemsSource = snapshot.Work;
            OperationSearchBox.Clear(); ShowOperationPage("", 0);
            MessageSearchBox.Clear(); ShowMessagePage("", 0);
            CommitmentsGrid.ItemsSource = snapshot.Commitments;
            CommitmentSearchBox.Clear();
            if (snapshot.CommitmentHistory != null) ShowCommitmentPage("", 0);
            else CommitmentCountText.Text = "当前读取 " + (snapshot.Commitments?.Count ?? 0) + " 项安排。";
            ClearCommitmentDetails();
            ClearOperationDetails();
            ShowDependencies();
            if (snapshot.ConversationPage is { } page) ApplyConversationPage(page, snapshot.Identity);
            else
            {
                conversationRows = snapshot.Conversation?.ToArray() ?? [];
                if (conversationRows.Any(row => row == null) || conversationRows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != conversationRows.Length)
                    throw new InvalidOperationException("Conversation rows could not be verified.");
                ConversationGrid.ItemsSource = conversationRows;
                ConversationCountText.Text = "当前保存 " + snapshot.ConversationRecordCount + " 条收发记录，本页显示最近 "
                    + conversationRows.Length + " 条。历史正文可能已不适用；接口受理不代表手机已收到。";
                ConversationReadAtText.Text = "对话读取于 " + snapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
                UpdateConversationButtons();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            ClearRecords("当前记录无法读取，或绑定已经变化。");
        }
        finally { RefreshButton.IsEnabled = true; }
    }
    internal void InvalidateIfIdentityChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            // Remember a binding mismatch when it is reported, even if the UI
            // dispatcher runs after an A -> B -> A change.
            var observed = displayedSnapshot; bool invalid;
            try { invalid = hasSnapshot && isCurrent?.Invoke(displayedIdentity) == false; }
            catch { invalid = true; }
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (invalid && ReferenceEquals(observed, displayedSnapshot) && !lifetime.IsCancellationRequested)
                    ClearRecords("账户或微信绑定已经变化，旧记录已从页面清除。请刷新当前记录。");
                else InvalidateIfIdentityChanged();
            });
            return;
        }
        if (lifetime.IsCancellationRequested) return;
        try
        {
            if (hasSnapshot && isCurrent?.Invoke(displayedIdentity) == false)
            { viewVersion++; ClearRecords("账户或微信绑定已经变化，旧记录已从页面清除。请刷新当前记录。"); }
        }
        catch { viewVersion++; ClearRecords("当前身份无法核对，旧记录已从页面清除。"); }
    }
    private void ClearRecords(string reason)
    {
        viewVersion++; hasSnapshot = false; displayedIdentity = null; displayedSnapshot = null;
        conversationQuery = ""; ConversationSearchBox.Clear();
        commitmentPage = null;
        CommitmentSearchBox.Clear(); CommitmentCountText.Text = "";
        OperationSearchBox.Clear(); OperationCountText.Text = "";
        MessageSearchBox.Clear(); MessageCountText.Text = ""; MessageDetailBox.Clear();
        messageQuery = ""; messagePage = 0;
        PreviousMessagesButton.IsEnabled = NextMessagesButton.IsEnabled = false;
        operationQuery = ""; operationPage = 0;
        PreviousOperationsButton.IsEnabled = NextOperationsButton.IsEnabled = false;
        UpdateCommitmentButtons();
        WorkGrid.ItemsSource = OperationsGrid.ItemsSource = MessagesGrid.ItemsSource
            = CommitmentsGrid.ItemsSource = ConversationGrid.ItemsSource = null;
        ClearConversationPage("");
        ConversationDiagnostics.IsExpanded = false;
        ClearCommitmentDetails();
        ClearOperationDetails();
        ClearDependencyRecords();
        ReadAtText.Text = ""; ContextText.Text = reason; CapabilitiesText.Text = "";
        DiagnosticText.Text = "原记录保持不变。请核对连接和实际数据目录后刷新。";
    }
    private void ClearCommitmentDetails(string message = "选择一项安排，查看完整内容、触发规则与实际发送记录。")
    {
        CommitmentDetailBox.Text = message; CommitmentTechnicalBox.Clear(); CommitmentTechnicalExpander.IsExpanded = false;
        CommitmentDetailScroll.ScrollToTop();
    }
    private void SearchCommitmentsClicked(object sender, RoutedEventArgs e) => ShowCommitmentPage(CommitmentSearchBox.Text, 0);
    private void CommitmentSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true; ShowCommitmentPage(CommitmentSearchBox.Text, 0);
    }
    private void PreviousCommitmentsClicked(object sender, RoutedEventArgs e)
    {
        if (commitmentPage is { HasPrevious: true } page) ShowCommitmentPage(page.Query, page.PageIndex - 1);
    }
    private void NextCommitmentsClicked(object sender, RoutedEventArgs e)
    {
        if (commitmentPage is { HasNext: true } page) ShowCommitmentPage(page.Query, page.PageIndex + 1);
    }
    private void ShowCommitmentPage(string query, int pageIndex)
    {
        InvalidateIfIdentityChanged();
        if (displayedSnapshot?.CommitmentHistory is not { } history || lifetime.IsCancellationRequested) return;
        var page = history.ReadPage(query, pageIndex);
        CommitmentsGrid.SelectedItem = null;
        commitmentPage = page;
        CommitmentsGrid.ItemsSource = page.Rows;
        ClearCommitmentDetails();
        int start = page.PageIndex * AssistantCommitmentHistory.PageSize + 1;
        CommitmentCountText.Text = page.TotalCount == 0 ? "没有找到符合条件的安排。留空查找可查看全部。"
            : $"本次读取匹配 {page.TotalCount} 项 · 显示第 {start}–{start + page.Rows.Count - 1} 项 · 刷新记录以加载变化";
        UpdateCommitmentButtons();
    }
    private void UpdateCommitmentButtons()
    {
        SearchCommitmentsButton.IsEnabled = CommitmentSearchBox.IsEnabled = displayedSnapshot?.CommitmentHistory != null;
        PreviousCommitmentsButton.IsEnabled = commitmentPage?.HasPrevious == true;
        NextCommitmentsButton.IsEnabled = commitmentPage?.HasNext == true;
    }
    private void CommitmentSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            InvalidateIfIdentityChanged();
            var snapshot = displayedSnapshot; long expectedVersion = viewVersion;
            ClearCommitmentDetails();
            if (lifetime.IsCancellationRequested || !hasSnapshot || snapshot?.Identity == null
                || CommitmentsGrid.SelectedItem is not AssistantWorkspaceRow row) return;
            if (!Guid.TryParseExact(row.Id, "N", out var id) && !Guid.TryParseExact(row.Id, "D", out id))
            { ClearCommitmentDetails("这项安排没有可核对的完整编号，请刷新记录。"); return; }
            var matches = (commitmentPage?.Details ?? snapshot.CommitmentDetails)?.Where(detail => detail.WorkItemId == id).ToArray() ?? [];
            if (matches.Length != 1 || matches[0].Identity != snapshot.Identity || matches[0].WorkVersion < 1)
            { ClearCommitmentDetails("当前记录中没有可核对的事项详情，请刷新记录。"); return; }
            if (expectedVersion != viewVersion || !ReferenceEquals(snapshot, displayedSnapshot)
                || isCurrent?.Invoke(snapshot.Identity) == false)
            { ClearRecords("账户或微信绑定已经变化，请刷新当前记录。"); return; }
            CommitmentDetailBox.Text = matches[0].Body;
            CommitmentTechnicalBox.Text = "完整编号：" + matches[0].WorkItemId.ToString("N")
                + "\n记录版本：" + matches[0].WorkVersion + "\n仅用于核对记录，不会执行或重试这项安排。";
        }
        catch { ClearRecords("当前事项详情无法核对，请刷新当前记录。"); }
    }
    private void ClearOperationDetails(string message = "选择一项操作，查看原始请求、实际处理结果与回执说明。")
    {
        OperationDetailBox.Text = message; OperationTechnicalBox.Clear(); OperationTechnicalExpander.IsExpanded = false;
        OperationDetailScroll.ScrollToTop();
    }
    private void OperationSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            InvalidateIfIdentityChanged();
            var snapshot = displayedSnapshot; long expectedVersion = viewVersion;
            ClearOperationDetails();
            if (lifetime.IsCancellationRequested || !hasSnapshot || snapshot?.Identity == null
                || OperationsGrid.SelectedItem is not AssistantWorkspaceRow row) return;
            if (!Guid.TryParseExact(row.Id, "N", out var id) && !Guid.TryParseExact(row.Id, "D", out id))
            { ClearOperationDetails("这项操作没有可核对的完整编号，请刷新记录。"); return; }
            var matches = snapshot.OperationDetails?.Where(detail => detail.OperationId == id).ToArray() ?? [];
            if (matches.Length != 1 || matches[0].Identity != snapshot.Identity)
            { ClearOperationDetails("当前记录中没有可核对的操作详情，请刷新记录。"); return; }
            if (expectedVersion != viewVersion || !ReferenceEquals(snapshot, displayedSnapshot)
                || isCurrent?.Invoke(snapshot.Identity) == false)
            { ClearRecords("账户或微信绑定已经变化，请刷新当前记录。"); return; }
            OperationDetailBox.Text = matches[0].Body;
            OperationTechnicalBox.Text = "完整编号：" + matches[0].OperationId.ToString("N")
                + "\n仅用于核对记录，不会执行、重试或确认这项操作。";
        }
        catch { ClearRecords("当前操作详情无法核对，请刷新当前记录。"); }
    }
    private void ClearConversationPage(string reason)
    {
        var oldRequest = conversationRequest; conversationRequest = null;
        oldRequest?.Cancel();
        conversationPage = null; conversationRows = [];
        ConversationGrid.SelectedItem = null; ConversationGrid.ItemsSource = null;
        ConversationDetailBox.Clear(); ConversationCountText.Text = reason; ConversationReadAtText.Text = "";
        OlderConversationButton.IsEnabled = LatestConversationButton.IsEnabled = false;
        ExportConversationButton.IsEnabled = false;
        SearchConversationButton.IsEnabled = ConversationSearchBox.IsEnabled = false;
    }
    private void ApplyConversationPage(AssistantConversationPage page, ConversationIdentity? expectedIdentity)
    {
        var rows = page.Rows?.ToArray() ?? throw new InvalidOperationException("Missing conversation page.");
        if (page.Identity != expectedIdentity || page.ReadAt == default || page.TotalCount < 0
            || rows.Length > AssistantConversationHistory.Limit || rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id))
            || rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length
            || rows.Length == 0 && (page.StartIndex != 0 || page.EndIndex != 0 || page.OlderCursor != null)
            || rows.Length > 0 && (expectedIdentity == null || page.StartIndex < 1
                || (long)page.EndIndex != (long)page.StartIndex + rows.Length - 1 || page.EndIndex > page.TotalCount
                || (page.EndIndex < page.TotalCount) != (page.OlderCursor != null))
            || rows.Zip(rows.Skip(1), (first, second) => first.CreatedAt < second.CreatedAt
                || first.CreatedAt == second.CreatedAt && StringComparer.Ordinal.Compare(first.Id, second.Id) >= 0).Any(invalid => invalid)
            || page.OlderCursor is { } cursor && (cursor.Identity != expectedIdentity || rows.Length == 0
                || cursor.BeforeCreatedAt != rows[^1].CreatedAt || cursor.BeforeId != rows[^1].Id || page.EndIndex >= page.TotalCount))
            throw new InvalidOperationException("Conversation page identity, rows or range could not be verified.");
        conversationPage = page; conversationRows = rows;
        ConversationGrid.SelectedItem = null; ConversationGrid.ItemsSource = rows;
        ConversationDetailBox.Text = "选择一条记录，查看保存的完整正文。";
        ConversationCountText.Text = rows.Length == 0 ? "本次读取没有符合范围的对话记录。"
            : $"本次读取共 {page.TotalCount} 条 · 显示第 {page.StartIndex}–{page.EndIndex} 条 · 最新记录在上";
        ConversationReadAtText.Text = "本页读取于 " + page.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            + "。本页独立读取，不与上次读取拼接；最新记录在上。接口受理不代表手机已收到。";
        UpdateConversationButtons();
    }
    private void UpdateConversationButtons()
    {
        bool enabled = !lifetime.IsCancellationRequested && conversationRequest == null && hasSnapshot
            && displayedSnapshot?.Identity != null && readConversationPage != null;
        OlderConversationButton.IsEnabled = enabled && conversationPage?.OlderCursor != null;
        LatestConversationButton.IsEnabled = enabled;
        ExportConversationButton.IsEnabled = enabled && !exportingConversation && readConversationExport != null;
        SearchConversationButton.IsEnabled = ConversationSearchBox.IsEnabled = enabled && searchConversation != null;
    }
    private async void SearchConversationClicked(object sender, RoutedEventArgs e) => await SearchConversationAsync();
    internal async Task SearchConversationAsync()
    {
        if (searchConversation == null || conversationRequest != null) return;
        conversationQuery = ConversationSearchBox.Text.Trim();
        await ReadConversationPageAsync(null);
    }
    private async void ExportConversationClicked(object sender, RoutedEventArgs e)
        => await ExportConversationAsync(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出完整对话历史（含正文及任务关联）",
                Filter = "对话历史 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, CheckPathExists = true,
                OverwritePrompt = false, FileName = "assistant-history-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
    internal async Task<AssistantHealthExportResult> ExportConversationAsync(Func<string?> selectDestination)
    {
        var identity = displayedIdentity; long version = viewVersion;
        if (identity == null || !hasSnapshot || exportingConversation || readConversationExport == null)
            return AssistantHealthExportResult.SnapshotChanged;
        bool Current() => !lifetime.IsCancellationRequested && hasSnapshot && version == viewVersion
            && ReferenceEquals(displayedIdentity, identity) && isCurrent?.Invoke(identity) != false;
        exportingConversation = true; UpdateConversationButtons();
        try
        {
            string? path = selectDestination();
            if (path == null) return AssistantHealthExportResult.Cancelled;
            if (!Current()) return AssistantHealthExportResult.SnapshotChanged;
            var snapshot = await readConversationExport(identity, lifetime.Token);
            var result = AssistantConversationExport.WriteNew(path, snapshot,
                () => Current() && ReferenceEquals(snapshot.Identity, identity));
            if (Current()) ConversationCountText.Text = result == AssistantHealthExportResult.Saved
                ? $"已导出 {snapshot.Rows.Count} 条完整历史（含正文及任务关联）。账本未删除；导出文件不参与后续记忆清理。"
                : "绑定或记录视图已变化，未导出旧历史。";
            return result;
        }
        catch (Exception)
        {
            if (Current()) ConversationCountText.Text = "历史未导出完成。请使用新的本机文件路径，已有文件不会覆盖。";
            return AssistantHealthExportResult.Failed;
        }
        finally { exportingConversation = false; UpdateConversationButtons(); }
    }
    private async void OlderConversationClicked(object sender, RoutedEventArgs e)
    {
        if (conversationPage?.OlderCursor is { } cursor) await ReadConversationPageAsync(cursor);
    }
    private async void LatestConversationClicked(object sender, RoutedEventArgs e) => await ReadConversationPageAsync(null);
    internal async Task ReadConversationPageAsync(AssistantConversationCursor? cursor)
    {
        InvalidateIfIdentityChanged();
        var snapshot = displayedSnapshot;
        if (lifetime.IsCancellationRequested || conversationRequest != null || readConversationPage == null
            || !hasSnapshot || snapshot?.Identity is not { } identity || isCurrent?.Invoke(identity) == false) return;
        if (cursor != null && (conversationPage?.OlderCursor != cursor || cursor.Identity != identity)) return;
        long expectedVersion = ++viewVersion;
        ClearConversationPage("正在读取对话记录，请稍候。");
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        conversationRequest = request;
        try
        {
            var page = conversationQuery.Length == 0 ? await readConversationPage(identity, cursor, request.Token)
                : await searchConversation!(identity, conversationQuery, cursor, request.Token);
            if (lifetime.IsCancellationRequested || request.IsCancellationRequested || viewVersion != expectedVersion
                || !ReferenceEquals(displayedSnapshot, snapshot)) return;
            if (isCurrent?.Invoke(identity) == false)
            { ClearRecords("账户或微信绑定已经变化，旧记录已从页面清除。请刷新当前记录。"); return; }
            ApplyConversationPage(page, identity);
            if (page.Query.Length != 0) ConversationCountText.Text = "搜索结果 · " + ConversationCountText.Text;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!lifetime.IsCancellationRequested && viewVersion == expectedVersion && ReferenceEquals(displayedSnapshot, snapshot))
            {
                ClearConversationPage("这页对话记录暂时无法核对，请返回最近记录后重试。原记录保持不变。");
                UpdateConversationButtons();
            }
        }
        finally
        {
            if (ReferenceEquals(conversationRequest, request))
            { conversationRequest = null; if (!lifetime.IsCancellationRequested) UpdateConversationButtons(); }
            request.Dispose();
        }
    }
    private void ConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            InvalidateIfIdentityChanged();
            var snapshot = displayedSnapshot; var page = conversationPage; long expectedVersion = viewVersion;
            ConversationDetailBox.Text = "选择一条记录，查看保存的完整正文。";
            if (lifetime.IsCancellationRequested || !hasSnapshot || snapshot == null || conversationRequest != null
                || ConversationGrid.SelectedItem is not AssistantConversationRow row
                || !conversationRows.Any(item => ReferenceEquals(item, row))) return;
            if (expectedVersion != viewVersion || !ReferenceEquals(snapshot, displayedSnapshot)
                || !ReferenceEquals(page, conversationPage) || isCurrent?.Invoke(snapshot.Identity) == false)
            { ClearRecords("账户或微信绑定已经变化，请刷新当前记录。"); return; }
            ConversationDetailBox.Text = row.Detail;
        }
        catch { ClearRecords("当前对话正文无法核对，请刷新当前记录。"); }
    }
}
