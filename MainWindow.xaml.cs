using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CodexDashboard;

public partial class MainWindow : Window
{
    private DashboardViewModel? vm;
    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        vm = viewModel;
        DataContext = viewModel;
        viewModel.Updated += SyncSelection;
        Closing += OnClosing;
        SearchBox.ToolTip = "输入关键词搜索；留空显示所有符合条件的任务";
        SyncSelection();
    }
    private void SyncSelection()
    {
        if (vm == null) return;
        if (SearchBox.Text != vm.Search) SearchBox.Text = vm.Search;
        if (StatusBox.SelectedIndex != vm.StatusFilter) StatusBox.SelectedIndex = vm.StatusFilter;
        ProjectsList.SelectedItem = vm.Projects.Find(p => p.Name == vm.Project);
        HostBox.SelectedItem = vm.Host;
        TasksList.SelectedItem = vm.SelectedTask;
        EmptyState.Visibility = vm.VisibleTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = vm.SelectedTask == null ? Visibility.Collapsed : Visibility.Visible;
        SelectHint.Visibility = vm.SelectedTask == null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ProjectChanged(object sender, SelectionChangedEventArgs e) { if (vm != null && ProjectsList.SelectedItem is ProjectFilter p) { vm.Project = p.Name; vm.Filter(); } }
    private void HostChanged(object sender, SelectionChangedEventArgs e) { if (vm != null && HostBox.SelectedItem is string host) { vm.Host = host; vm.Filter(); } }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (vm != null) { vm.Search = SearchBox.Text; vm.Filter(); } }
    private void StatusChanged(object sender, SelectionChangedEventArgs e) { if (vm != null) { vm.StatusFilter = StatusBox.SelectedIndex; vm.Filter(); } }
    private void TaskChanged(object sender, SelectionChangedEventArgs e) { if (vm != null && TasksList.SelectedItem is TaskRecord task) { vm.SelectedTask = task; Detail.Visibility = Visibility.Visible; SelectHint.Visibility = Visibility.Collapsed; } }
    private void ShowHud(object sender, RoutedEventArgs e) => ((App)Application.Current).ToggleHud(true);
    private void OpenSettings(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();
    private void OpenWebClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenWeb();
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (vm == null) return;
        try { await vm.RefreshAsync(); }
        catch (Exception ex) { MessageBox.Show(this, "刷新未完成：" + ex.Message, "Codex Dashboard"); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!((App)Application.Current).Exiting) { e.Cancel = true; Hide(); }
    }
}
