using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodexDashboard;

// Exercises the actual login JSON parser and WPF window with a synthetic HTTP
// handler. No settings, credential store, receiver or real network is created.
internal static class WeChatBindingLifecycleTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--wechat-binding-lifecycle-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--wechat-binding-lifecycle-test"
                || !Path.IsPathFullyQualified(args[1]) || args[1].Any(char.IsControl))
                throw new ArgumentException("New absolute synthetic output required.");
            string directory = Path.GetFullPath(args[1]);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Evidence exists.");
            for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Redirected output rejected.");
            Directory.CreateDirectory(directory);
            var checks = new Dictionary<string, bool>();
            var failures = new List<string>();
            void Check(string name, bool passed)
            {
                checks.Add(name, passed);
                if (!passed) failures.Add(name);
                File.WriteAllText(Path.Combine(directory, "wechat-binding-lifecycle-checks.json"),
                    JsonSerializer.Serialize(new { checks, failures, actualNetworkRequests = 0,
                        productionDataRead = false, credentialsPersisted = false }, Settings.Json));
            }
            await LateResponseAsync(directory, false, Check);
            await LateResponseAsync(directory, true, Check);
            await PendingVerificationAsync(Check);
            await CurrentConfirmationAsync(Check);
            if (failures.Count != 0) throw new InvalidOperationException(string.Join(", ", failures));
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static Task Login(WeChatBindingWindow window) => (Task)typeof(WeChatBindingWindow)
        .GetMethod("LoginAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object Qr() => new { qrcode = "SYNTHETIC_QR_ONLY",
        qrcode_img_content = "https://example.com/synthetic-login-no-network" };
    private static object Confirmation() => new { status = "confirmed", bot_token = "SYNTHETIC_TOKEN_ONLY",
        ilink_bot_id = "synthetic-bot", ilink_user_id = "synthetic-user", baseurl = WeChatApi.DefaultBaseUrl };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private static async Task LateResponseAsync(string directory, bool confirmed, Action<string, bool> check)
    {
        string prefix = confirmed ? "late_confirmation_" : "late_qr_";
        var entered = Pending<bool>();
        var release = Pending<HttpResponseMessage>();
        int calls = 0, factories = 0;
        var window = new WeChatBindingWindow(false, () =>
        {
            factories++;
            return new WeChatApi(new Handler((_, _) =>
            {
                int number = Interlocked.Increment(ref calls);
                if (confirmed && number == 1) return Task.FromResult(Json(Qr()));
                entered.TrySetResult(true);
                return release.Task;
            }));
        });
        Task login = Login(window);
        window.Show();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string displayed = window.LoginStatus.Text;
            window.Close();
            check(prefix + "actual_window_closed_while_request_pending", !window.IsVisible && !login.IsCompleted);
            release.TrySetResult(Json(confirmed ? Confirmation() : Qr()));
            await login.WaitAsync(TimeSpan.FromSeconds(5));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            File.WriteAllText(Path.Combine(directory, prefix + "evidence.json"),
                JsonSerializer.Serialize(new { displayed, after = window.LoginStatus.Text,
                    bindingPresent = window.Binding != null, calls, factories, qrPresent = window.QrImage.Source != null,
                    visible = window.IsVisible }, Settings.Json));
            check(prefix + "closed_window_cannot_accept_binding", window.Binding == null);
            check(prefix + "late_result_does_not_rewrite_closed_status", window.LoginStatus.Text == displayed);
            check(prefix + "closed_window_has_no_qr_or_verification_text",
                window.QrImage.Source == null && window.VerifyBox.Password.Length == 0);
            int previous = calls;
            await Login(window);
            check(prefix + "closed_retry_does_not_start_another_request", calls == previous && factories == 1);
            check(prefix + "only_expected_synthetic_requests", calls == (confirmed ? 2 : 1));
        }
        finally
        {
            release.TrySetException(new OperationCanceledException("Synthetic cleanup."));
            if (window.IsVisible) window.Close();
            await login.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task PendingVerificationAsync(Action<string, bool> check)
    {
        int calls = 0;
        var window = new WeChatBindingWindow(false, () => new WeChatApi(new Handler((_, _) =>
            Task.FromResult(Json(Interlocked.Increment(ref calls) == 1 ? Qr() : new { status = "need_verifycode" })))));
        Task login = Login(window);
        window.Show();
        try
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (window.VerifyPanel.Visibility != System.Windows.Visibility.Visible && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(10);
            check("verification_prompt_uses_actual_login_parser",
                window.VerifyPanel.Visibility == System.Windows.Visibility.Visible && calls == 2);
            window.VerifyBox.Password = "123456";
            window.Close();
            await login.WaitAsync(TimeSpan.FromSeconds(5));
            check("close_cancels_verification_without_third_request", calls == 2 && window.Binding == null);
            check("close_clears_unsent_verification_digits", window.VerifyBox.Password.Length == 0);
        }
        finally { if (window.IsVisible) window.Close(); await login.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CurrentConfirmationAsync(Action<string, bool> check)
    {
        int calls = 0;
        var release = Pending<HttpResponseMessage>();
        var window = new WeChatBindingWindow(false, () => new WeChatApi(new Handler((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromResult(Json(Qr())) : release.Task)));
        Task login = Login(window);
        _ = window.Dispatcher.BeginInvoke(() => release.TrySetResult(Json(Confirmation())), DispatcherPriority.Background);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => window.Close();
        bool? result;
        timeout.Start();
        try { result = window.ShowDialog(); }
        finally { timeout.Stop(); if (window.IsVisible) window.Close(); }
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        check("current_modal_confirmation_still_returns_binding", result == true
            && window.Binding?.BotId == "synthetic-bot" && window.Binding.UserId == "synthetic-user");
        check("current_confirmation_performs_only_login_requests", calls == 2);
    }
}
