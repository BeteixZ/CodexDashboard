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

internal static class ModelWindowLifecycleTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--model-window-lifecycle-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--model-window-lifecycle-test")
                throw new ArgumentException("Explicit isolated output required.");
            string output = Path.GetFullPath(args[1]);
            if (!Path.IsPathFullyQualified(args[1]) || args[1].Any(char.IsControl)
                || Directory.Exists(output) || File.Exists(output))
                throw new ArgumentException("A new absolute directory is required.");
            for (var parent = new DirectoryInfo(output); parent != null; parent = parent.Parent)
                if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Redirected output rejected.");
            Directory.CreateDirectory(output);
            var checks = new Dictionary<string, bool>();
            void Check(string name, bool passed)
            {
                checks.Add(name, passed);
                File.WriteAllText(Path.Combine(output, "model-window-lifecycle-checks.json"),
                    JsonSerializer.Serialize(new { checks, actualNetworkRequests = 0, productionDataRead = false }, Settings.Json));
                if (!passed) throw new InvalidOperationException(name);
            }
            await ClosedReadAsync(Path.Combine(output, "late-error"), false, Check);
            await ClosedReadAsync(Path.Combine(output, "late-success"), true, Check);
            await OpenFailureAsync(Path.Combine(output, "open-error"), Check);
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task ClosedReadAsync(string directory, bool success, Action<string, bool> check)
    {
        string prefix = success ? "late_success_" : "late_error_";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(directory, (_, _) => { entered.TrySetResult(); return release.Task; });
        var window = new ChatGptWindow(fixture.Assistant, fixture.Settings);
        Task refresh = Refresh(window);
        window.Show();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string status = fixture.Assistant.Status;
            string settings = JsonSerializer.Serialize(fixture.Settings, Settings.Json);
            string displayed = window.ResultText.Text;
            window.Close();
            check(prefix + "actual_window_closed_while_read_was_pending", !window.IsVisible && !refresh.IsCompleted);
            if (success) release.TrySetResult(Json(new { models = new[] { new { slug = "late-model", display_name = "Late", visibility = "list" } } }));
            else release.TrySetException(new HttpRequestException("Synthetic late failure; no request was sent."));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            File.WriteAllText(Path.Combine(directory, "late-read-evidence.json"),
                JsonSerializer.Serialize(new { success, beforeStatus = status, afterStatus = fixture.Assistant.Status,
                    persistCalls = fixture.PersistCalls, statusChanged = fixture.Assistant.Status != status,
                    recordedFailure = fixture.Assistant.LastFailure != null, windowVisible = window.IsVisible,
                    settingsChanged = JsonSerializer.Serialize(fixture.Settings, Settings.Json) != settings }, Settings.Json));
            check(prefix + "closed_window_does_not_publish_late_failure_or_status",
                fixture.Assistant.Status == status && fixture.Assistant.LastFailure == null);
            check(prefix + "closed_window_does_not_save_or_change_configuration",
                fixture.PersistCalls == 0 && JsonSerializer.Serialize(fixture.Settings, Settings.Json) == settings);
            check(prefix + "closed_window_does_not_replace_display_text", window.ResultText.Text == displayed);
        }
        finally
        {
            release.TrySetException(new OperationCanceledException("Synthetic cleanup."));
            if (window.IsVisible) window.Close();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task OpenFailureAsync(string directory, Action<string, bool> check)
    {
        using var fixture = new Fixture(directory, (_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic visible failure.")));
        var window = new ChatGptWindow(fixture.Assistant, fixture.Settings);
        try
        {
            await Refresh(window);
            check("open_window_still_reports_current_failure",
                fixture.Assistant.LastFailure != null && window.ResultText.Text.Length > 0);
            check("failed_current_read_does_not_save_configuration", fixture.PersistCalls == 0);
        }
        finally { window.Close(); }
    }

    private static Task Refresh(ChatGptWindow window) => (Task)typeof(ChatGptWindow)
        .GetMethod("RefreshModelsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
    private sealed class Fixture : IDisposable
    {
        internal Settings Settings { get; } = new() { LocalEnabled = false };
        internal ChatGptAssistant Assistant { get; }
        internal int PersistCalls;
        internal Fixture(string directory, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Directory.CreateDirectory(directory);
            string vault = Path.Combine(directory, "synthetic.credentials");
            var account = new ChatGptAccount { ClientId = "synthetic-window-account", AccessToken = "SYNTHETIC_ACCESS",
                RefreshToken = "SYNTHETIC_REFRESH", Scope = "chatgpt.tokens.use.direct",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Welcomed = true };
            new ChatGptVault { Accounts = [account] }.Save(vault);
            Settings.ChatGpt.ActiveAccount = account.ClientId;
            Settings.ChatGpt.Model = "original-model";
            Settings.ChatGpt.Enabled = true;
            var auth = new ChatGptAuth(vault, new Handler((_, _) =>
                throw new InvalidOperationException("Synthetic auth must not need a transport.")));
            Assistant = new(Settings, () => PersistCalls++, auth, new Handler(handler));
        }
        public void Dispose() => Assistant.Dispose();
    }
}
