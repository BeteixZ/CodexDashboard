using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

public partial class AssistantHealthWindow : Window
{
    private readonly Func<CancellationToken, Task<AssistantHealthSnapshot>> read;
    private readonly Func<ConversationIdentity?, bool> isCurrent;
    private readonly CancellationTokenSource lifetime = new();
    private ConversationIdentity? displayedIdentity;
    private bool hasSnapshot;
    private long viewVersion;
    private AssistantHealthSnapshot? displayedSnapshot;

    internal AssistantHealthWindow(Func<CancellationToken, Task<AssistantHealthSnapshot>> read,
        Func<ConversationIdentity?, bool> isCurrent)
    {
        InitializeComponent(); this.read = read; this.isCurrent = isCurrent;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { lifetime.Cancel(); Clear("窗口已关闭。"); };
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void ExportClicked(object sender, RoutedEventArgs e)
        => ExportSnapshot(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出无正文诊断摘要", Filter = "诊断摘要 (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
                FileName = "assistant-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                OverwritePrompt = false, CheckPathExists = true
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
    internal AssistantHealthExportResult ExportSnapshot(Func<string?> selectDestination)
    {
        var captured = displayedSnapshot; long version = viewVersion;
        if (!hasSnapshot || captured == null || lifetime.IsCancellationRequested) return AssistantHealthExportResult.SnapshotChanged;
        ExportButton.IsEnabled = false;
        try
        {
            string? path = selectDestination();
            var result = AssistantHealthExport.WriteNew(path, captured, () => !lifetime.IsCancellationRequested
                && hasSnapshot && viewVersion == version && ReferenceEquals(displayedSnapshot, captured) && isCurrent(captured.Identity));
            ResultText.Text = result switch
            {
                AssistantHealthExportResult.Saved => "已保存诊断摘要：只有状态、计数和时间，不含目录、身份、正文或凭据。",
                AssistantHealthExportResult.Cancelled => "已取消导出，没有写入文件。",
                _ => "状态或绑定已经变化，没有导出旧摘要。请刷新后重试。"
            };
            return result;
        }
        catch (Exception)
        {
            ResultText.Text = "摘要未保存完成。请选新的本机文件；已有文件和重定向目录不会写入。";
            return AssistantHealthExportResult.Failed;
        }
        finally { ExportButton.IsEnabled = hasSnapshot && !lifetime.IsCancellationRequested; }
    }
    internal async Task RefreshAsync()
    {
        if (!RefreshButton.IsEnabled || lifetime.IsCancellationRequested) return;
        long version = viewVersion;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await read(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (version != viewVersion || !isCurrent(snapshot.Identity))
            { Clear("账户或微信绑定已经变化，请刷新当前状态。"); return; }
            displayedIdentity = snapshot.Identity; hasSnapshot = true; displayedSnapshot = snapshot; viewVersion++;
            ExportButton.IsEnabled = true;
            ReadAtText.Text = "读取于 " + snapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            ConnectionText.Text = snapshot.ConnectionSummary;
            RecordsText.Text = snapshot.RecordsSummary;
            ConcurrencyText.Text = snapshot.ConcurrencySummary;
            SourcesText.Text = snapshot.SourcesSummary;
            DetailsBox.Text = snapshot.Details;
            ResultText.Text = "只查看当前状态；刷新不会发送消息、调用模型或操作 Codex。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Clear("当前状态无法读取，或绑定正在变化。原文件保持不变。"); }
        finally { RefreshButton.IsEnabled = true; }
    }
    internal void InvalidateIfIdentityChanged()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(InvalidateIfIdentityChanged); return; }
        if (lifetime.IsCancellationRequested) return;
        try
        {
            if (hasSnapshot && !isCurrent(displayedIdentity))
            { viewVersion++; Clear("账户或微信绑定已经变化，旧状态已清除。请刷新当前状态。"); }
        }
        catch { viewVersion++; Clear("当前身份无法核对，旧状态已清除。"); }
    }
    private void Clear(string reason)
    {
        viewVersion++; hasSnapshot = false; displayedIdentity = null; displayedSnapshot = null; ExportButton.IsEnabled = false;
        ReadAtText.Text = ConnectionText.Text = RecordsText.Text = ConcurrencyText.Text = SourcesText.Text = "";
        DetailsBox.Clear(); DetailsExpander.IsExpanded = false; ResultText.Text = reason;
    }
}
