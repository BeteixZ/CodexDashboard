using System;
using System.Linq;
using System.Windows;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CodexDashboard;

public partial class SettingsWindow : Window
{
    private readonly Settings settings;
    private bool resetPosition;
    private string pendingKey;
    private bool isSyncingKey;
    private bool isKeyRevealed;
    private string GetCurrentKey() => isKeyRevealed ? AccessKeyTextBox.Text.Trim() : AccessKeyBox.Password.Trim();
    private void SetCurrentKey(string val)
    {
        isSyncingKey = true;
        AccessKeyBox.Password = val;
        AccessKeyTextBox.Text = val;
        isSyncingKey = false;
    }

    public SettingsWindow(Settings settings)
    {
        InitializeComponent(); this.settings = settings;
        ActualDataDirectoryBox.Text = Settings.DirectoryPath;
        pendingKey = settings.WebAccessKey;
        CompactModeBox.IsChecked = settings.HudMode == HudModes.Compact;
        MiniModeBox.IsChecked = settings.HudMode == HudModes.Tray;
        WebEnabledBox.IsChecked = settings.WebEnabled;
        LanEnabledBox.IsChecked = settings.WebLanEnabled;
        PortBox.Text = settings.WebPort.ToString();
        SetCurrentKey(pendingKey);
        AddressBox.Text = ((App)Application.Current).WebConnectionAddress;
        WebStateText.Text = AddressBox.Text.Length > 0 ? "服务已启动" : "服务未启动；保存设置后生效";
        HudVisibleBox.IsChecked = settings.HudVisible;
        TopmostBox.IsChecked = settings.HudTopmost;
        ThroughBox.IsChecked = settings.HudClickThrough;
        OpacitySlider.Value = settings.HudOpacity;
        RowsSlider.Value = settings.HudRows;
        NotifyBox.IsChecked = settings.Notifications;
        SoundBox.IsChecked = settings.Sound;
        LocalBox.IsChecked = settings.LocalEnabled;
        PythonBox.Text = settings.PythonPath;
        HomeBox.Text = settings.CodexHome;
        HostsBox.Text = string.Join(", ", settings.RemoteHosts);
        RefreshSlider.Value = settings.RefreshSeconds;
        var lifetime = new CancellationTokenSource();
        Closed += (_, _) => lifetime.Cancel();
        Loaded += async (_, _) =>
        {
            try { AssistantStatusText.Text = await ((App)Application.Current).ReadAssistantStatusAsync(lifetime.Token); }
            catch (OperationCanceledException) { }
        };
    }
    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
    private void ResetPosition(object sender, RoutedEventArgs e) { resetPosition = true; ((System.Windows.Controls.Button)sender).Content = "保存后重置位置"; }
    private void AccessKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (isSyncingKey) return;
        isSyncingKey = true;
        AccessKeyTextBox.Text = AccessKeyBox.Password;
        isSyncingKey = false;
    }
    private void AccessKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (isSyncingKey) return;
        isSyncingKey = true;
        AccessKeyBox.Password = AccessKeyTextBox.Text;
        isSyncingKey = false;
    }
    private void ToggleVisibilityClicked(object sender, RoutedEventArgs e)
    {
        isKeyRevealed = !isKeyRevealed;
        if (isKeyRevealed)
        {
            AccessKeyBox.Visibility = Visibility.Collapsed;
            AccessKeyTextBox.Visibility = Visibility.Visible;
            ToggleVisibilityButton.Content = "隐藏明文";
            AccessKeyTextBox.Focus();
            AccessKeyTextBox.CaretIndex = AccessKeyTextBox.Text.Length;
        }
        else
        {
            AccessKeyTextBox.Visibility = Visibility.Collapsed;
            AccessKeyBox.Visibility = Visibility.Visible;
            ToggleVisibilityButton.Content = "显示明文";
            AccessKeyBox.Focus();
        }
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var hosts = HostsBox.Text.Split(new[] { ',', '，', ';', '\n', '\r', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(PortBox.Text, out int port) || port < 1024 || port > 65535) { MessageBox.Show(this, "端口必须在 1024–65535 之间。", "检查设置"); return; }
        if (hosts.Any(h => !Settings.ValidAlias(h))) { MessageBox.Show(this, "SSH 别名只能包含字母、数字、点、横线或下划线。", "检查设置"); return; }
        if (LocalBox.IsChecked == true && (string.IsNullOrWhiteSpace(PythonBox.Text) || string.IsNullOrWhiteSpace(HomeBox.Text))) { MessageBox.Show(this, "请填写 Python 路径和 Codex 数据目录。", "检查设置"); return; }
        string key = GetCurrentKey();
        if (WebEnabledBox.IsChecked == true && key.Length < 4) { MessageBox.Show(this, "访问密钥长度至少需要 4 个字符。", "检查设置"); return; }
        settings.HudVisible = HudVisibleBox.IsChecked == true;
        settings.HudMode = MiniModeBox.IsChecked == true ? HudModes.Tray : HudModes.Compact;
        settings.WebEnabled = WebEnabledBox.IsChecked == true;
        settings.WebLanEnabled = LanEnabledBox.IsChecked == true;
        settings.WebPort = port;
        settings.WebAccessKey = key;
        settings.HudTopmost = TopmostBox.IsChecked == true;
        settings.HudClickThrough = ThroughBox.IsChecked == true;
        settings.HudOpacity = OpacitySlider.Value;
        settings.HudRows = (int)RowsSlider.Value;
        settings.Notifications = NotifyBox.IsChecked == true;
        settings.Sound = SoundBox.IsChecked == true;
        settings.LocalEnabled = LocalBox.IsChecked == true;
        settings.PythonPath = PythonBox.Text.Trim();
        settings.CodexHome = HomeBox.Text.Trim();
        settings.RemoteHosts = hosts.ToList();
        settings.RefreshSeconds = (int)RefreshSlider.Value;
        if (resetPosition) { settings.HudLeft = -1; settings.HudTop = -1; }
        DialogResult = true;
    }
    private void CopyKeyClicked(object sender, RoutedEventArgs e)
    {
        string key = GetCurrentKey();
        if (key.Length > 0) Clipboard.SetText(key);
    }
    private void CopyLinkClicked(object sender, RoutedEventArgs e)
    {
        string link = ((App)Application.Current).WebShareLink;
        if (link.Length > 0) Clipboard.SetText(link);
        else MessageBox.Show(this, "请先启用网页监控并保存设置。", "网页监控");
    }
    private void RotateKeyClicked(object sender, RoutedEventArgs e)
    {
        pendingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        SetCurrentKey(pendingKey);
        RotateKeyButton.Content = "已生成，保存后生效";
    }
    private void OpenWebClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenWeb();
    private void OpenAssistantClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenAssistant();
    private void FirewallClicked(object sender, RoutedEventArgs e)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Enable-LanAccess.ps1");
        if (!File.Exists(script)) { MessageBox.Show(this, "请从发布目录启动程序。", "网页监控"); return; }
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "防火墙配置未执行。", "网页监控"); }
    }
}
