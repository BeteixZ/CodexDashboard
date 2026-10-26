using System;
using System.Windows;

namespace CodexDashboard;

public partial class AssistantWindow : Window
{
    private readonly AssistantApp app;
    internal AssistantWindow(AssistantApp app)
    {
        InitializeComponent(); this.app = app;
        ArtifactButton.Visibility = app.FileFeaturesEnabled ? Visibility.Visible : Visibility.Collapsed;
        PreparedFilesButton.Visibility = ArtifactButton.Visibility;
        Closing += (_, e) => { if (!app.Exiting) { e.Cancel = true; Hide(); } };
    }
    internal void Update(string wechat, string model, int tasks, int queued, DateTimeOffset? refreshedAt, string? connection, string? ownedSummary = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Update(wechat, model, tasks, queued, refreshedAt, connection, ownedSummary)); return; }
        WeChatText.Text = wechat; ModelText.Text = model;
        TasksText.Text = $"数据源：{tasks} 个任务 · {queued} 条待投递指令"
            + (refreshedAt.HasValue ? $" · 最近读取 {refreshedAt.Value.ToLocalTime():HH:mm:ss}" : "")
            + "\n" + (ownedSummary ?? "助手自建任务：等待本地记录刷新");
        ConnectionText.Text = connection ?? "助手独立运行，监控台关闭后仍可工作。";
    }
    private void SettingsClicked(object sender, RoutedEventArgs e) => app.ShowSettings();
    private void ModelClicked(object sender, RoutedEventArgs e) => app.ShowModel();
    private async void ArtifactClicked(object sender, RoutedEventArgs e) => await app.SelectArtifactAsync();
    private void PreparedFilesClicked(object sender, RoutedEventArgs e) => app.ShowPreparedFiles();
    private void WorkClicked(object sender, RoutedEventArgs e) => app.ShowWork();
    private void HealthClicked(object sender, RoutedEventArgs e) => app.ShowHealth();
    private void MigrationClicked(object sender, RoutedEventArgs e) => app.ShowMigrationPreview();
    private void HideClicked(object sender, RoutedEventArgs e) => Hide();
}
