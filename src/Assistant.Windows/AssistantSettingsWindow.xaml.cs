using System;
using System.Linq;
using System.Windows;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CodexDashboard;

public partial class AssistantSettingsWindow : Window
{
    private readonly Settings settings;
    private readonly Func<AssistantSettingsEdit, string?>? commit;
    private string pendingBinding;
    private string wakeQuietTimeZoneId = "";
    private string? replacementInput, replacementSourceCipher, replacementCipher;
    private Guid replacementSourceRoute, replacementRoute;
    private readonly CancellationTokenSource windowLifetime = new();

    private readonly WeChatService service;
    private readonly Action showModel;
    internal AssistantSettingsWindow(Settings settings, WeChatService service, Action showModel, Func<AssistantSettingsEdit, string?>? commit = null)
    {
        InitializeComponent(); this.settings = settings; this.service = service; this.showModel = showModel; this.commit = commit;
        ActualDataDirectoryBox.Text = Settings.DirectoryPath;
        HistoryArchiveBox.IsChecked = settings.HistoryArchiveEnabled;
        MediaAttachmentsBox.IsChecked = settings.Media.Attachments;
        MediaImagesBox.IsChecked = settings.Media.Images;
        MediaFilesBox.IsChecked = settings.Media.Files;
        MediaTypingBox.IsChecked = settings.Media.Typing;
        pendingBinding = settings.WeChat.ProtectedBinding;
        Closed += (_, _) => { windowLifetime.Cancel(); service.Changed -= UpdateWeChatStatus; };
        ClawEnabledBox.IsChecked = settings.WeChat.ClawBotEnabled;
        PushEnabledBox.IsChecked = settings.WeChat.PushPlusEnabled;
        WeChatCompletedBox.IsChecked = settings.WeChat.NotifyCompleted;
        WeChatFailedBox.IsChecked = settings.WeChat.NotifyFailed;
        WeChatWaitingBox.IsChecked = settings.WeChat.NotifyWaiting;
        WeChatSummaryBox.IsChecked = settings.WeChat.IncludeSummary;
        WeChatReportsBox.IsChecked = settings.WeChat.ReportQueriesEnabled;
        try { PushTokenBox.Password = settings.WeChat.ReadPushPlusToken(); }
        catch { PushTokenRecoveryPanel.Visibility = Visibility.Visible; }
        bool hasQuiet = !string.IsNullOrEmpty(settings.WeChat.PushPlusWakeQuietStart)
            || !string.IsNullOrEmpty(settings.WeChat.PushPlusWakeQuietEnd) || !string.IsNullOrEmpty(settings.WeChat.PushPlusWakeTimeZoneId);
        WakeQuietEnabledBox.IsChecked = hasQuiet;
        WakeQuietStartBox.Text = settings.WeChat.PushPlusWakeQuietStart ?? "";
        WakeQuietEndBox.Text = settings.WeChat.PushPlusWakeQuietEnd ?? "";
        wakeQuietTimeZoneId = hasQuiet ? settings.WeChat.PushPlusWakeTimeZoneId ?? "" : TimeZoneInfo.Local.Id;
        ShowWakeTimeZone();
        if (!settings.WeChat.TryReadWakeQuietHours(out _, out string? quietError)) WakeQuietValidationText.Text = quietError;
        UpdateWeChatStatus();
        service.Changed += UpdateWeChatStatus;
        LocalBox.IsChecked = settings.LocalEnabled;
        PythonBox.Text = settings.PythonPath;
        HomeBox.Text = settings.CodexHome;
        HostsBox.Text = string.Join(", ", settings.RemoteHosts);
        RefreshSlider.Value = settings.RefreshSeconds;
        ConcurrencyEnabledBox.IsChecked = settings.MaxConcurrentExecutions.HasValue;
        ConcurrencyLimitBox.Text = (settings.MaxConcurrentExecutions ?? 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InputMergeWindowBox.Text = settings.InputMergeWindowMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadInputMergeWindow(out int mergeWindow)) return;
        int? concurrencyLimit = null;
        if (ConcurrencyEnabledBox.IsChecked == true)
        {
            if (!int.TryParse(ConcurrencyLimitBox.Text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int requestedLimit) || requestedLimit is < 1 or > 32)
            { MessageBox.Show(this, "同时执行上限请填写 1 至 32。", "检查设置"); return; }
            concurrencyLimit = requestedLimit;
        }
        var hosts = HostsBox.Text.Split(new[] { ',', '，', ';', '\n', '\r', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Any(h => !Settings.ValidAlias(h))) { MessageBox.Show(this, "SSH 别名只能包含字母、数字、点、横线或下划线。", "检查设置"); return; }
        if (LocalBox.IsChecked == true && (string.IsNullOrWhiteSpace(PythonBox.Text) || string.IsNullOrWhiteSpace(HomeBox.Text))) { MessageBox.Show(this, "请填写 Python 路径和 Codex 数据目录。", "检查设置"); return; }
        if (ClawEnabledBox.IsChecked == true && pendingBinding.Length == 0) { MessageBox.Show(this, "请先扫码连接微信，或取消启用 ClawBot。", "微信通知"); return; }
        if (PushEnabledBox.IsChecked == true && string.IsNullOrWhiteSpace(PushTokenBox.Password)) { MessageBox.Show(this, "请填写 pushplus 令牌，或关闭公众号通知。", "微信通知"); return; }
        // Validate and prepare a candidate first. A failure must not partially
        // change the live route, token, quiet policy or notification switches.
        var push = PushCandidate();
        if (!push.TrySetWakeQuietHours(WakeQuietEnabledBox.IsChecked == true ? WakeQuietStartBox.Text.Trim() : null,
            WakeQuietEnabledBox.IsChecked == true ? WakeQuietEndBox.Text.Trim() : null,
            WakeQuietEnabledBox.IsChecked == true ? wakeQuietTimeZoneId : null, out string? error))
        { WakeQuietValidationText.Text = error; MessageBox.Show(this, error, "短唤醒安静时段"); return; }
        try
        {
            if (replacementCipher != null)
            {
                if (settings.WeChat.ProtectedPushPlusToken != replacementSourceCipher
                    || settings.WeChat.PushPlusWakeRouteId != replacementSourceRoute
                    || PushTokenBox.Password.Trim() != replacementInput)
                { MessageBox.Show(this, "令牌或设置已经变化，请重新点击“替换无法读取的令牌”；尚未保存。", "微信通知"); return; }
                push.ProtectedPushPlusToken = replacementCipher;
                push.PushPlusWakeRouteId = replacementRoute;
            }
            else push.WritePushPlusToken(PushTokenBox.Password);
        }
        catch (Exception failure) when (failure is FormatException or CryptographicException)
        {
            bool unreadable = false;
            try { _ = settings.WeChat.ReadPushPlusToken(); }
            catch (Exception readFailure) when (readFailure is FormatException or CryptographicException) { unreadable = true; }
            if (unreadable) PushTokenRecoveryPanel.Visibility = Visibility.Visible;
            MessageBox.Show(this, unreadable
                ? "原令牌无法读取，普通保存不会覆盖。请重新输入令牌，点击“替换无法读取的令牌”后再保存。"
                : "凭据加密失败，设置尚未保存。", "微信通知"); return;
        }
        catch (ArgumentException) { MessageBox.Show(this, "令牌格式无效，设置尚未保存。", "微信通知"); return; }
        var edit = new AssistantSettingsEdit(LocalBox.IsChecked == true, PythonBox.Text.Trim(), HomeBox.Text.Trim(),
            hosts, (int)RefreshSlider.Value, concurrencyLimit, mergeWindow,
            new(ClawEnabledBox.IsChecked == true, PushEnabledBox.IsChecked == true,
                WeChatCompletedBox.IsChecked == true, WeChatFailedBox.IsChecked == true, WeChatWaitingBox.IsChecked == true,
                WeChatSummaryBox.IsChecked == true, WeChatReportsBox.IsChecked == true,
                push.ProtectedPushPlusToken, push.PushPlusWakeRouteId, push.PushPlusWakeQuietStart,
                push.PushPlusWakeQuietEnd, push.PushPlusWakeTimeZoneId, bindingEdited ? pendingBinding : null),
            replacementCipher == null ? null : replacementSourceCipher,
            replacementCipher == null ? null : replacementSourceRoute,
            HistoryArchiveBox.IsChecked == true,
            new AssistantMediaOptions { Attachments = MediaAttachmentsBox.IsChecked == true, Images = MediaImagesBox.IsChecked == true,
                Files = MediaFilesBox.IsChecked == true, Typing = MediaTypingBox.IsChecked == true });
        string? saveError;
        try { saveError = commit == null ? "保存入口尚未就绪，设置尚未保存。" : commit(edit); }
        catch { saveError = "这次保存结果暂无法确认，请重新打开设置核对；没有继续更新连接。"; }
        if (saveError != null)
        {
            SaveStatusText.Text = saveError; SaveStatusText.Visibility = Visibility.Visible;
            return;
        }
        SaveStatusText.Text = ""; SaveStatusText.Visibility = Visibility.Collapsed;
        DialogResult = true;
    }
    internal bool TryReadInputMergeWindow(out int milliseconds)
    {
        if (int.TryParse(InputMergeWindowBox.Text.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out milliseconds) && milliseconds is >= 0 and <= 3000)
        {
            InputMergeValidationText.Text = "";
            return true;
        }
        InputMergeValidationText.Text = "请填写 0 至 3000 的整数毫秒数；0 表示不等待、不合并。尚未保存。";
        SettingsTabs.SelectedItem = ChatGptTab;
        InputMergeValidationText.BringIntoView();
        InputMergeWindowBox.Focus();
        return false;
    }
    private bool bindingEdited;
    private WeChatOptions PushCandidate() => new()
    {
        ProtectedPushPlusToken = settings.WeChat.ProtectedPushPlusToken,
        PushPlusWakeRouteId = settings.WeChat.PushPlusWakeRouteId,
        PushPlusWakeQuietStart = settings.WeChat.PushPlusWakeQuietStart,
        PushPlusWakeQuietEnd = settings.WeChat.PushPlusWakeQuietEnd,
        PushPlusWakeTimeZoneId = settings.WeChat.PushPlusWakeTimeZoneId
    };
    private void ReplaceUnreadablePushTokenClicked(object sender, RoutedEventArgs e)
    {
        var candidate = PushCandidate();
        string oldCipher = candidate.ProtectedPushPlusToken;
        Guid oldRoute = candidate.PushPlusWakeRouteId;
        try { candidate.ReplaceUnreadablePushPlusToken(PushTokenBox.Password); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or CryptographicException)
        { PushTokenRecoveryText.Text = "尚未准备替换：请重新输入非空令牌；只有原令牌确实无法读取时可使用此操作。"; return; }
        replacementInput = PushTokenBox.Password.Trim();
        replacementSourceCipher = oldCipher; replacementSourceRoute = oldRoute;
        replacementCipher = candidate.ProtectedPushPlusToken; replacementRoute = candidate.PushPlusWakeRouteId;
        PushTokenRecoveryText.Text = "替换已准备，保存设置后生效。此操作不会自动启用公众号通知。";
    }
    private void UseCurrentWakeTimeZoneClicked(object sender, RoutedEventArgs e)
    { wakeQuietTimeZoneId = TimeZoneInfo.Local.Id; ShowWakeTimeZone(); }
    private void ShowWakeTimeZone() => WakeQuietZoneText.Text = "时区：" + (wakeQuietTimeZoneId.Length == 0 ? "尚未设置" : wakeQuietTimeZoneId);
    private void UpdateWeChatStatus()
    {
        // Service belongs to this independent assistant host.
        if (!bindingEdited) ClawStateText.Text = service?.ClawStatus ?? "未启用；可扫码连接微信";
        PushStateText.Text = service?.PushStatus ?? "填写令牌后可发送测试通知";
    }
    internal void ShowWeChatTab() => SettingsTabs.SelectedItem = WeChatTab;
    private void ChatGptClicked(object sender, RoutedEventArgs e) => showModel();
    internal void ShowReportSettings() { ShowWeChatTab(); WeChatScrollViewer.ScrollToEnd(); }
    private void BindClawClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new WeChatBindingWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Binding is { } account)
        {
            try { var options = new WeChatOptions(); options.WriteBinding(account); pendingBinding = options.ProtectedBinding; bindingEdited = true; ClawEnabledBox.IsChecked = true; ClawStateText.Text = "连接成功；保存设置后，请在微信发送“帮助”。"; }
            catch { ClawStateText.Text = "凭据加密失败，请重新连接。"; }
        }
    }
    private void ClearClawClicked(object sender, RoutedEventArgs e)
    { pendingBinding = ""; bindingEdited = true; ClawEnabledBox.IsChecked = false; ClawStateText.Text = "保存后清除本机凭据。微信端连接可在 ClawBot 管理中解除。"; }
    private async void TestClawClicked(object sender, RoutedEventArgs e)
    {
        if (bindingEdited) { ClawStateText.Text = "请先保存新绑定，再发送测试通知。"; return; }
        // Service belongs to this independent assistant host.
        if (service == null) return;
        var button = (System.Windows.Controls.Button)sender; button.IsEnabled = false;
        try { ClawStateText.Text = await service.TestClawAsync(windowLifetime.Token); }
        finally { button.IsEnabled = true; }
    }
    private async void TestPushClicked(object sender, RoutedEventArgs e)
    {
        // Service belongs to this independent assistant host.
        if (service == null) return;
        var button = (System.Windows.Controls.Button)sender; button.IsEnabled = false;
        try { PushStateText.Text = "正在发送测试通知…"; PushStateText.Text = await service.TestPushAsync(PushTokenBox.Password.Trim(), windowLifetime.Token); }
        finally { button.IsEnabled = true; }
    }
    private void OpenPushPlusClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.pushplus.plus/") { UseShellExecute = true }); }
        catch { PushStateText.Text = "浏览器未打开，请访问 https://www.pushplus.plus/。"; }
    }
}
