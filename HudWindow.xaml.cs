using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CodexDashboard;

public partial class HudWindow : Window
{
    private readonly Settings settings;
    private readonly DashboardViewModel vm;
    private bool restoring;
    public HudWindow(Settings settings, DashboardViewModel vm)
    {
        this.settings = settings; this.vm = vm;
        InitializeComponent(); DataContext = vm;
        Width = settings.HudWidth;
        SourceInitialized += (_, _) => ApplySettings();
        Loaded += (_, _) => PlaceOnScreen();
        LocationChanged += (_, _) => RememberPosition();
        SizeChanged += (_, _) => { if (!restoring && IsLoaded) settings.HudWidth = ActualWidth; };
        vm.Updated += () => EmptyHint.Visibility = vm.HudTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, e) => { if (!((App)Application.Current).Exiting) { e.Cancel = true; ((App)Application.Current).ToggleHud(false); } };
    }
    public void ApplySettings()
    {
        Topmost = settings.HudTopmost;
        Opacity = settings.HudOpacity;
        SetClickThrough(new WindowInteropHelper(this).Handle, settings.HudClickThrough);
    }
    public void PlaceOnScreen()
    {
        restoring = true;
        var work = SystemParameters.WorkArea;
        // Preserve valid positions on any connected monitor, including negative coordinates.
        double left = settings.HudLeft == -1 ? work.Right - Width - 24 : settings.HudLeft;
        double top = settings.HudTop == -1 ? work.Top + 40 : settings.HudTop;
        var proposed = new Rect(left, top, Width, Math.Max(ActualHeight, 100));
        double scale = VisualTreeHelperScale();
        bool visible = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var r = screen.WorkingArea;
            var area = new Rect(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
            if (area.Contains(new Point(proposed.Left + 40, proposed.Top + 20))) { visible = true; break; }
        }
        Left = visible ? left : work.Right - Width - 24;
        Top = visible ? top : work.Top + 40;
        restoring = false;
    }
    private double VisualTreeHelperScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
    private void RememberPosition()
    {
        if (!restoring && IsLoaded) { settings.HudLeft = Left; settings.HudTop = Top; }
    }
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); ((App)Application.Current).SaveSettings(); } }
    private void SettingsClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();
    private void HideClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).ToggleHud(false);
    private void DashboardClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowDashboard();
    private void MiniClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).SetHudMode("tray");
    private void TaskClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskRecord task }) { vm.Select(task); ((App)Application.Current).ShowDashboard(); }
    }
    public static void SetClickThrough(IntPtr handle, bool enabled)
    {
        if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        // Keep WPF's existing WS_EX_LAYERED and all unrelated flags intact.
        style = enabled ? style | 0x20L | 0x08000000L : style & ~0x20L & ~0x08000000L;
        SetWindowLongPtr(handle, -20, new IntPtr(style));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
