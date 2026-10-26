using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace CodexDashboard;

public partial class WeChatBindingWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<WeChatApi> createApi;
    private TaskCompletionSource<string>? verification;
    private bool running;
    internal ClawBinding? Binding { get; private set; }
    public WeChatBindingWindow() : this(false) { }
    internal WeChatBindingWindow(bool preview, Func<WeChatApi>? createApi = null)
    {
        InitializeComponent();
        this.createApi = createApi ?? (() => new WeChatApi());
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            verification?.TrySetCanceled(lifetime.Token);
            verification = null;
            VerifyBox.Clear(); QrImage.Source = null; VerifyPanel.Visibility = Visibility.Collapsed;
            if (DialogResult != true) Binding = null;
        };
        if (preview) { ShowQr("https://example.com/codex-dashboard-preview"); LoginStatus.Text = "扫码界面预览 · 此二维码不能绑定账号"; }
        else Loaded += async (_, _) => await LoginAsync();
    }
    private void ShowQr(string content)
    {
        using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        using var stream = new MemoryStream(renderer.GetGraphic(8));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); QrImage.Source = image;
    }
    private async Task LoginAsync()
    {
        if (running || lifetime.IsCancellationRequested) return;
        running = true; RetryButton.IsEnabled = false; QrImage.Source = null;
        VerifyPanel.Visibility = Visibility.Collapsed; LoginStatus.Text = "正在向微信获取二维码…";
        using var api = createApi();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); attempt.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var login = await api.StartLoginAsync(attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            ShowQr(login.QrContent);
            LoginStatus.Text = "请用微信扫一扫，随后在手机确认连接。";
            string? code = null;
            while (!attempt.IsCancellationRequested)
            {
                var result = await api.PollLoginAsync(login, code, attempt.Token);
                attempt.Token.ThrowIfCancellationRequested();
                switch (result.Status)
                {
                    case "confirmed": Binding = result.Binding; DialogResult = true; return;
                    case "need_verifycode":
                        LoginStatus.Text = code == null ? "请填写手机显示的数字。" : "数字未通过验证，请核对后重新输入。";
                        VerifyPanel.Visibility = Visibility.Visible; VerifyBox.Clear(); VerifyBox.Focus();
                        verification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        code = await verification.Task.WaitAsync(attempt.Token);
                        attempt.Token.ThrowIfCancellationRequested();
                        VerifyPanel.Visibility = Visibility.Collapsed; break;
                    case "scaned": code = null; LoginStatus.Text = "已扫码，正在等待微信确认…"; break;
                    case "expired": throw new InvalidOperationException("二维码已过期，请重新获取。若手机提示环境风险，请停止扫码并查看微信提示。");
                    case "verify_code_blocked": throw new InvalidOperationException("验证次数过多，请稍后重新获取二维码。");
                    case "binded_redirect": throw new InvalidOperationException("微信提示已经绑定；请检查微信 ClawBot 的连接管理，再决定是否重新连接。");
                    case "wait": case "scaned_but_redirect": break;
                    default: throw new InvalidOperationException("微信返回了未支持的验证状态，请稍后重试。");
                }
                await Task.Delay(1000, attempt.Token);
            }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) LoginStatus.Text = "本次连接已超时，请重新获取二维码。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) LoginStatus.Text = WeChatApi.Explain(ex); }
        finally
        {
            running = false; verification = null;
            if (!lifetime.IsCancellationRequested)
            {
                RetryButton.IsEnabled = true; VerifyPanel.Visibility = Visibility.Collapsed;
                VerifyBox.Clear();
                if (Binding == null) QrImage.Source = null;
            }
        }
    }
    private void VerifyClicked(object sender, RoutedEventArgs e)
    {
        string code = VerifyBox.Password.Trim();
        if (code.Length == 0 || !code.All(c => c >= '0' && c <= '9')) { LoginStatus.Text = "请输入手机微信显示的数字。"; return; }
        verification?.TrySetResult(code); VerifyBox.Clear();
    }
    private async void RetryClicked(object sender, RoutedEventArgs e) => await LoginAsync();
    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
}
