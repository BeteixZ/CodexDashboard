using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

// Each scenario uses an actual AssistantApp in a separate, isolated WPF process.
// The collector, network and model are synthetic; channel ownership and storage are real.
internal static class AssistantRefreshLifecycleTests
{
    internal sealed record Check(string Name, bool Passed);
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int child = Array.IndexOf(args, "--assistant-refresh-lifecycle-child");
        if (child >= 0)
        {
            string directory = args[child + 2];
            try
            {
                ValidateNewDirectory(directory);
                var checks = await ((AssistantApp)Application.Current).RunRefreshLifecycleScenarioAsync(
                    args[child + 1], directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"),
                    JsonSerializer.Serialize(checks, Settings.Json));
                exit(checks.All(c => c.Passed) ? 0 : 1);
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "failure.json"),
                    JsonSerializer.Serialize(new { errorType = error.GetType().Name,
                        detail = error is InvalidOperationException ? error.Message : null }, Settings.Json));
                exit(1);
            }
            return true;
        }
        int run = Array.IndexOf(args, "--assistant-refresh-lifecycle-test");
        if (run < 0) return false;
        try { await RunAsync(args[run + 1]); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }

    private static void ValidateNewDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_output_directory_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
    }

    private static async Task RunAsync(string directory)
    {
        ValidateNewDirectory(directory);
        var all = new List<Check>();
        foreach (string mode in new[] { "collector", "notification", "notification-fault" })
        {
            string childDirectory = Path.Combine(directory, mode);
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing_executable");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            foreach (string value in new[] { "--assistant-refresh-lifecycle-child", mode, childDirectory })
                start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("child_not_started");
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                throw;
            }
            string path = Path.Combine(childDirectory, "checks.json");
            if (File.Exists(path))
                all.AddRange((JsonSerializer.Deserialize<Check[]>(await File.ReadAllTextAsync(path), Settings.Json) ?? [])
                    .Select(check => check with { Name = mode + "/" + check.Name }));
            all.Add(new(mode + "/child_completed_without_dispatcher_deadlock", process.ExitCode == 0));
            await File.WriteAllTextAsync(Path.Combine(directory, "refresh-lifecycle-checks.json"),
                JsonSerializer.Serialize(new { count = all.Count, failures = all.Count(c => !c.Passed),
                    actualApplication = true, networkUsed = false, productionAccountUsed = false, checks = all }, Settings.Json));
        }
        if (all.Any(c => !c.Passed)) throw new InvalidOperationException("refresh_lifecycle_failed");
    }

    internal sealed class Collector(bool block) : IHostCollector
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<HostSnapshot> ReadAsync(string host, ICollectorSettings settings, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            if (block) await Release.Task; // Deliberately does not obey cancellation.
            return new()
            {
                Name = host, LastSuccess = DateTimeOffset.UtcNow,
                Tasks = [new()
                {
                    Id = "synthetic-lifecycle-task", Title = "退出等待检查", Host = host, Project = "Synthetic",
                    TurnId = "synthetic-lifecycle-turn", StartedAt = 1792512000,
                    Status = call == 1 ? TaskStatusNames.Active : TaskStatusNames.Completed,
                    RecordedStatus = call == 1 ? TaskStatusNames.Active : TaskStatusNames.Completed,
                    FinalReport = "合成检查完成。", FinalReportTurnId = "synthetic-lifecycle-turn",
                    FinalReportSource = "synthetic report"
                }]
            };
        }
    }
    internal sealed class ProfileLease : IDisposable
    {
        internal int Releases;
        public void Dispose() => Interlocked.Increment(ref Releases);
    }
}

public partial class AssistantApp
{
    internal async Task<IReadOnlyList<AssistantRefreshLifecycleTests.Check>> RunRefreshLifecycleScenarioAsync(
        string mode, string directory)
    {
        if (!Dispatcher.CheckAccess() || settings != null || mode is not ("collector" or "notification" or "notification-fault"))
            throw new InvalidOperationException("isolated_startup_scenario_required");
        var checks = new List<AssistantRefreshLifecycleTests.Check>();
        void Check(string name, bool passed) => checks.Add(new(name, passed));
        var fixture = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"),
            modelEnabled: false, receiveMessages: true);
        var source = new AssistantRefreshLifecycleTests.Collector(mode == "collector");
        var profileLease = new AssistantRefreshLifecycleTests.ProfileLease();
        var notifyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings = fixture.Settings; wechat = fixture.Channel; durable = fixture.Runtime;
        queue = fixture.Queue; model = fixture.Model; monitor = new(settings, source); runtimeLease = profileLease;
        var identity = fixture.Runtime.Identity!;
        int notifications = 0;
        bool lateLedgerReadable = false, notificationCaptured = false;
        monitor.Notify += async task =>
        {
            notifications++;
            notifyEntered.TrySetResult();
            await notifyRelease.Task;
            // A queued Dispatcher continuation must remain free to run while Stop awaits refresh.
            await Dispatcher.InvokeAsync(() => { }).Task;
            var before = await fixture.Ledger.ReadConversationAsync(identity);
            lateLedgerReadable = before.Identity == identity && profileLease.Releases == 0;
            await fixture.Channel.ObserveAsync(task);
            notificationCaptured = (await fixture.Ledger.ReadConversationAsync(identity)).NotificationDrafts.Length == 1;
            if (mode == "notification-fault") throw new IOException("synthetic_notify_failure");
        };
        Task? refreshingTask = null, stopTask = null;
        try
        {
            if (mode != "collector") await RefreshAsync(); // Establish the actual monitor's active baseline.
            refreshingTask = RefreshAsync();
            await (mode == "collector" ? source.Entered.Task : notifyEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            int calls = source.Calls;
            await RefreshAsync();
            Check("concurrent_refresh_is_not_started", source.Calls == calls && !refreshingTask.IsCompleted);
            stopTask = StopAsync();
            Check("repeated_stop_reuses_same_task", ReferenceEquals(stopTask, StopAsync()));
            if (mode == "collector") await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("stop_retains_profile_and_dependencies_until_actual_refresh_finishes",
                !stopTask.IsCompleted && profileLease.Releases == 0 && wechat == fixture.Channel
                && durable == fixture.Runtime && queue == fixture.Queue);
            Check("ledger_still_readable_during_shutdown",
                (await fixture.Ledger.ReadConversationAsync(identity)).Identity == identity);
            await using (var duplicate = ReceiverLease.TryAcquire(fixture.Binding.BotId))
                Check("receiver_lease_still_owned_during_refresh_drain", duplicate == null);
            await RefreshAsync();
            Check("exiting_rejects_new_refresh", source.Calls == calls);
            source.Release.TrySetResult();
            notifyRelease.TrySetResult();
            await refreshingTask.WaitAsync(TimeSpan.FromSeconds(5));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check("actual_refresh_drained_before_dependencies_and_profile_release",
                profileLease.Releases == 1 && wechat == null && durable == null && queue == null
                && refreshCompletion?.Task.IsCompletedSuccessfully == true && !refreshing);
            await using (var acquired = ReceiverLease.TryAcquire(fixture.Binding.BotId))
                Check("receiver_lease_released_after_actual_drain", acquired != null);
            if (mode == "collector")
                Check("collector_can_return_after_cancel_without_starting_notification", notifications == 0);
            else
            {
                Check("late_notify_keeps_ledger_available_and_captures_before_disposal",
                    notifications == 1 && lateLedgerReadable && notificationCaptured);
                await using var recovered = await DurableConversationLedger.OpenAsync(
                    new FileJournalStorage(fixture.LedgerPath), new WindowsStateProtector());
                var saved = await recovered.ReadConversationAsync(identity);
                Check("captured_notification_survives_shutdown", saved.NotificationDrafts.Length == 1);
                if (mode == "notification-fault")
                    Check("notify_exception_does_not_strand_refresh_completion", stopTask.IsCompletedSuccessfully);
            }
        }
        finally
        {
            source.Release.TrySetResult(); notifyRelease.TrySetResult();
            if (refreshingTask != null) await refreshingTask.WaitAsync(TimeSpan.FromSeconds(10));
            await StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Auth.Dispose();
        }
        return checks;
    }
}
