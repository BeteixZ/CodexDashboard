using System;
using System.Linq;
using System.Windows.Controls;

namespace CodexDashboard;

public partial class AssistantWorkWindow
{
    private void ClearDependencyDetails()
    {
        DependencyDetailBox.Text = "选择一项依赖工作，查看前置条件、完整指令、截止时间及实际派发记录。";
        DependencyCommandsBox.Clear(); DependencyCommandsExpander.IsExpanded = false;
        DependencyDetailScroll.ScrollToTop();
    }
    private void ClearDependencyRecords()
    {
        DependenciesGrid.ItemsSource = null;
        DependencySearchBox.Clear(); DependencyCountText.Text = "";
        ClearDependencyDetails();
    }
    private void DependencySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (DependenciesGrid != null) ShowDependencies();
    }
    private void ShowDependencies()
    {
        InvalidateIfIdentityChanged();
        ClearDependencyDetails();
        var details = displayedSnapshot?.Dependencies;
        string query = DependencySearchBox.Text.Trim();
        var matches = details?.Where(d => d.Body.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        DependenciesGrid.SelectedItem = null;
        DependenciesGrid.ItemsSource = matches.Select(d => d.Row).ToArray();
        DependencyCountText.Text = $"匹配 {matches.Length} / {details?.Count ?? 0} 项 · 刷新记录以加载变化";
    }
    private void DependencySelected(object sender, SelectionChangedEventArgs e)
    {
        InvalidateIfIdentityChanged();
        ClearDependencyDetails();
        var snapshot = displayedSnapshot;
        if (snapshot?.Identity == null || DependenciesGrid.SelectedItem is not AssistantWorkspaceRow row) return;
        var detail = snapshot.Dependencies!.Single(d => d.Row.Id == row.Id);
        if (detail.Identity != snapshot.Identity) return;
        DependencyDetailBox.Text = detail.Body;
        DependencyCommandsBox.Text = detail.Commands;
    }
}
