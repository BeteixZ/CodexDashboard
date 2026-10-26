using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexDashboard;

internal sealed class AssistantMemorySourceWindow : Window
{
    internal TextBox DetailsBox { get; }
    internal AssistantMemorySourceWindow(string details)
    {
        Title = "记忆来源";
        Width = 660; Height = 470; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("ShellWindow");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var close = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(24, 8, 24, 8), Margin = new Thickness(0, 12, 0, 0),
            Foreground = (Brush)FindResource("Text"), Background = (Brush)FindResource("Panel") };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var title = new TextBlock { Text = "这条记忆从哪里来", FontSize = 23, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        DetailsBox = new TextBox { Text = details, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12),
            Foreground = (Brush)FindResource("Text"), Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Line") };
        panel.Children.Add(DetailsBox);
        Content = panel;
    }
}
