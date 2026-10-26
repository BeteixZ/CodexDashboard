using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexDashboard;

// A local popup makes the app's sound toggle deterministic, independent of
// Windows' notification-area balloon sounds and notification settings.
public sealed class ToastWindow : Window
{
    public ToastWindow(TaskRecord task, App app)
    {
        Width = 350; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = task.Project + " · " + task.StatusLabel,
            FontWeight = FontWeights.SemiBold, Foreground = (Brush)new BrushConverter().ConvertFromString(DashboardPresentation.ColorFor(task.EffectiveStatus))! });
        content.Children.Add(new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 64, Margin = new Thickness(0, 10, 0, 12) });
        var close = new Button { Content = "查看监控台", HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => { app.ShowDashboard(); Close(); };
        content.Children.Add(close);
        Content = new Border { Background = (Brush)new BrushConverter().ConvertFromString("#FFFFFF")!,
            CornerRadius = new CornerRadius(12), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Child = content };
        Loaded += (_, _) => { Left = SystemParameters.WorkArea.Right - ActualWidth - 20; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 20; };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
