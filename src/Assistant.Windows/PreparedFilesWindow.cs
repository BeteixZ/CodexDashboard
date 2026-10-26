using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core;

namespace CodexDashboard;
internal sealed class PreparedFilesWindow : Window
{
    private readonly Func<int, string?, CancellationToken, Task<ArtifactPage>> read;
    private readonly Func<bool> current;
    private readonly CancellationTokenSource stop = new();
    private readonly TextBox search = new() { MinWidth = 180, Margin = new Thickness(0,0,10,0) };
    private readonly ListBox files = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10) };
    private readonly Button previous = new() { Content = "上一页", Margin = new Thickness(0,0,10,0) };
    private readonly Button next = new() { Content = "下一页", Margin = new Thickness(0,0,10,0) };
    private readonly Button copy = new() { Content = "复制微信查找指令", IsEnabled = false };
    private int page = 1, revision;
    private string appliedQuery = "";
    internal PreparedFilesWindow(Func<int, string?, CancellationToken, Task<ArtifactPage>> read, Func<bool> current)
    {
        this.read = read; this.current = current;
        SetResourceReference(StyleProperty, "ShellWindow");
#pragma warning disable WPF0001 // Match the existing assistant XAML windows.
        ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
        Title = "已准备文件"; Width = 680; Height = 560; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ScrollViewer.SetHorizontalScrollBarVisibility(files, ScrollBarVisibility.Disabled);
        var panel = new DockPanel { Margin = new Thickness(20) };
        panel.SetResourceReference(Panel.BackgroundProperty, "Bg");
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "这里查看已准备的文件。在微信发“待发送文件”，再按微信列表序号发送，例如“发送文件 1”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        var filter = new DockPanel(); var find = new Button { Content = "按名称查找" };
        DockPanel.SetDock(find, Dock.Right); filter.Children.Add(find); filter.Children.Add(search); header.Children.Add(filter); header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var navigation = new WrapPanel { Margin = new Thickness(0,12,0,0) };
        navigation.Children.Add(previous); navigation.Children.Add(next); navigation.Children.Add(copy);
        DockPanel.SetDock(navigation, Dock.Bottom); panel.Children.Add(navigation); panel.Children.Add(files); Content = panel;
        find.Click += async (_, _) => { appliedQuery = search.Text.Trim(); page = 1; await RefreshAsync(); };
        search.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { appliedQuery = search.Text.Trim(); page = 1; await RefreshAsync(); } };
        previous.Click += async (_, _) => { page--; await RefreshAsync(); };
        next.Click += async (_, _) => { page++; await RefreshAsync(); };
        files.SelectionChanged += (_, _) => copy.IsEnabled = current() && files.SelectedItem is ListBoxItem;
        copy.Click += (_, _) => { if (!current()) { InvalidateIfIdentityChanged(); return; } if (files.SelectedItem is ListBoxItem { Tag: string command }) Clipboard.SetText(command); };
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { revision++; stop.Cancel(); stop.Dispose(); };
    }
    internal void InvalidateIfIdentityChanged()
    {
        if (current()) return;
        revision++; files.Items.Clear(); copy.IsEnabled = previous.IsEnabled = next.IsEnabled = false;
        status.Text = "微信绑定已变化，请关闭窗口后重新打开。";
    }
    internal async Task RefreshAsync()
    {
        if (!current()) { InvalidateIfIdentityChanged(); return; }
        int request = ++revision;
        files.Items.Clear(); copy.IsEnabled = previous.IsEnabled = next.IsEnabled = false; status.Text = "正在读取…";
        try
        {
            var result = await read(page, appliedQuery, stop.Token);
            if (request != revision) return;
            if (!current()) { InvalidateIfIdentityChanged(); return; }
            status.Text = result.Total == 0 ? "没有找到已准备的文件。" : "第 " + result.Page + "/" + result.TotalPages + " 页，共 " + result.Total + " 份";
            foreach (var file in result.Items)
            {
                string text = file.Name + "（" + file.Length + " 字节）\n" + DurableAssistantRuntime.FileSendSummary(result.Sends[file.Id])
                    + (file.Task == null ? "" : "\n来源主机：" + file.Task.HostId);
                files.Items.Add(new ListBoxItem { Tag = LookupCommand(file.Name), Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) } });
            }
            previous.IsEnabled = page > 1; next.IsEnabled = page < result.TotalPages;
        }
        catch (OperationCanceledException) { if (request == revision) InvalidateIfIdentityChanged(); }
        catch (Exception error) when (error is IOException or LedgerException or System.Security.Cryptography.CryptographicException)
        { if (request == revision) status.Text = "暂时无法读取文件记录，请稍后重新查询。"; }
    }
    internal static string LookupCommand(string name) => name.Length <= 200 && name.IndexOfAny(['\r', '\n', ';', '；']) < 0
        ? "待发送文件 查找 " + name : "待发送文件";
}
