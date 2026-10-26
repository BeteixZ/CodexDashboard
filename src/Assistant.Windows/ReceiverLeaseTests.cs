using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Codex.Protocol;

namespace CodexDashboard;

internal static class ReceiverLeaseTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int child = Array.IndexOf(args, "--receiver-lease-child");
        if (child >= 0)
        {
            string statusPath = args[child + 2];
            try
            {
                using var lease = ReceiverLease.TryAcquire(args[child + 1]);
                await WriteChildStateAsync(statusPath, lease == null ? "busy" : "acquired");
                if (lease != null && args[child + 3] == "hold") await Task.Delay(Timeout.Infinite);
                exit(0);
            }
            catch (Exception ex)
            {
                await WriteChildStateAsync(statusPath, "error:" + ex.GetType().Name);
                exit(1);
            }
            return true;
        }
        int run = Array.IndexOf(args, "--receiver-lease-test");
        if (run < 0) return false;
        try { await RunAsync(args[run + 1]); exit(0); }
        catch { exit(1); }
        return true;
    }

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
    private static HttpResponseMessage Json(string text = """{"ret":0,"msgs":[]}""") => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static bool IsPoll(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/getupdates", StringComparison.Ordinal);
    private static Settings Config(string bot)
    {
        var settings = new Settings();
        settings.WeChat.ClawBotEnabled = true;
        settings.WeChat.WriteBinding(new() { BotId = bot, UserId = "synthetic-user", Token = "synthetic-token", ContextToken = "synthetic-context" });
        return settings;
    }
    private static WeChatSnapshot Snapshot() => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>());
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Synthetic condition was not reached.");
            await Task.Delay(20);
        }
    }
    private static Process StartChild(string bot, string path, bool hold)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        // A different profile must still contend for the same bot lease.
        foreach (var value in new[] { "--data-dir", Path.Combine(Path.GetDirectoryName(path)!, "child-profile-" + Guid.NewGuid().ToString("N")),
            "--receiver-lease-child", bot, path, hold ? "hold" : "probe" }) info.ArgumentList.Add(value);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start synthetic lease child.");
    }
    private static async Task<string> WaitChildStateAsync(string path, Process process)
    {
        await UntilAsync(() => File.Exists(path) || process.HasExited);
        if (!File.Exists(path)) throw new InvalidOperationException("Lease child exited before reporting.");
        return await File.ReadAllTextAsync(path);
    }
    private static async Task WriteChildStateAsync(string path, string value)
    {
        string temporary = path + ".pending";
        await File.WriteAllTextAsync(temporary, value);
        File.Move(temporary, path);
    }
    private static async Task KillTestChildAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }

    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        directory = Path.GetFullPath(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            if (!passed) throw new InvalidOperationException(name);
        }
        string prefix = "synthetic-lease-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var first = ReceiverLease.TryAcquire(prefix))
            {
                Check("first lease acquired", first != null);
                using var nested = ReceiverLease.TryAcquire(prefix);
                Check("same process reentry rejected", nested == null);
                using var different = ReceiverLease.TryAcquire(prefix + "-other");
                Check("different bot independent", different != null);
                string state = Path.Combine(directory, "probe-" + Guid.NewGuid().ToString("N") + ".txt");
                using var probe = StartChild(prefix, state, false);
                try
                {
                    Check("another process and profile cannot acquire same bot", await WaitChildStateAsync(state, probe) == "busy");
                    await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    Check("probe exits successfully", probe.ExitCode == 0);
                }
                finally { await KillTestChildAsync(probe); }
            }
            var crossThread = ReceiverLease.TryAcquire(prefix)!;
            await Task.Run(crossThread.Dispose);
            using (var again = ReceiverLease.TryAcquire(prefix)) Check("async continuation may release from another thread", again != null);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { ReceiverLease.TryAcquire(prefix, cancelled.Token); Check("cancelled acquire throws", false); }
                catch (OperationCanceledException) { checks.Add("cancelled acquire throws", true); }
            }
            using (var again = ReceiverLease.TryAcquire(prefix)) Check("cancelled acquire leaves no lease", again != null);
            try
            {
                using var scoped = ReceiverLease.TryAcquire(prefix);
                throw new InvalidDataException("synthetic exception");
            }
            catch (InvalidDataException) { }
            using (var again = ReceiverLease.TryAcquire(prefix)) Check("exception scope returns lease", again != null);

            string crashState = Path.Combine(directory, "crash-" + Guid.NewGuid().ToString("N") + ".txt");
            using (var childProcess = StartChild(prefix, crashState, true))
            {
                try
                {
                    Check("child owns lease", await WaitChildStateAsync(crashState, childProcess) == "acquired");
                    using (var blocked = ReceiverLease.TryAcquire(prefix)) Check("parent blocked by live child", blocked == null);
                    await KillTestChildAsync(childProcess);
                    // Confirm recovery within a bound after observed process exit;
                    // do not require zero-delay kernel-object cleanup.
                    await UntilAsync(() =>
                    {
                        using var recovered = ReceiverLease.TryAcquire(prefix);
                        return recovered != null;
                    });
                    using var afterCrash = ReceiverLease.TryAcquire(prefix);
                    Check("process crash automatically releases lease", afterCrash != null);
                }
                finally { await KillTestChildAsync(childProcess); }
            }

            using (var realLease = ReceiverLease.TryAcquire(prefix))
            await using (var simulation = new WeChatService(Config(prefix), Snapshot, () => { },
                new Stub((_, _) => Task.FromResult(Json())), receiveMessages: false))
            {
                await simulation.ConfigureAsync();
                Check("send-only isolated test does not contend or claim connected", simulation.ConnectionState == ConnectionState.Unknown);
            }
            await using (var empty = new WeChatService(new Settings(), Snapshot, () => { },
                new Stub((_, _) => throw new InvalidOperationException("Unconfigured channel must not call HTTP."))))
            {
                await empty.ConfigureAsync();
                Check("unconfigured channel is explicit", empty.ConnectionState == ConnectionState.NotConfigured);
            }
            int conflictCalls = 0;
            using (var realLease = ReceiverLease.TryAcquire(prefix))
            await using (var contender = new WeChatService(Config(prefix), Snapshot, () => { },
                new Stub((_, _) => { conflictCalls++; return Task.FromResult(Json()); })))
            {
                await contender.ConfigureAsync();
                Check("conflicting service does not start any network request",
                    contender.ConnectionState == ConnectionState.Degraded && conflictCalls == 0);
            }

            var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pollRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var service = new WeChatService(Config(prefix), Snapshot, () => { }, new Stub(async (request, ct) =>
            {
                if (!IsPoll(request)) return Json();
                pollEntered.TrySetResult();
                await pollRelease.Task.WaitAsync(ct);
                return Json();
            })))
            {
                await service.ConfigureAsync();
                await pollEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check("starting long poll is only connecting", service.ConnectionState == ConnectionState.Connecting);
                pollRelease.SetResult();
                await UntilAsync(() => service.ConnectionState == ConnectionState.Connected);
                Check("successful getupdates confirms connected", service.ConnectionState == ConnectionState.Connected);
            }
            using (var reacquire = ReceiverLease.TryAcquire(prefix)) Check("normal cancellation returns lease", reacquire != null);

            await using (var failed = new WeChatService(Config(prefix), Snapshot, () => { }, new Stub((request, _) =>
            {
                if (IsPoll(request)) throw new HttpRequestException("synthetic failure");
                return Task.FromResult(Json());
            })))
            {
                await failed.ConfigureAsync();
                await UntilAsync(() => failed.ConnectionState == ConnectionState.Disconnected);
                Check("network failure does not look connected", failed.ConnectionState == ConnectionState.Disconnected);
            }
            await using (var rejected = new WeChatService(Config(prefix), Snapshot, () => { },
                new Stub((request, _) => Task.FromResult(Json(IsPoll(request) ? """{"ret":-14}""" : """{"ret":0}""")))))
            {
                await rejected.ConfigureAsync();
                await UntilAsync(() => rejected.ConnectionState == ConnectionState.Degraded);
                Check("invalid credentials degrade channel", rejected.ConnectionState == ConnectionState.Degraded);
            }

            var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int slowCalls = 0;
            var slowSettings = Config(prefix);
            var slow = new WeChatService(slowSettings, Snapshot, () => { }, new Stub(async (request, _) =>
            {
                if (!IsPoll(request)) return Json();
                Interlocked.Increment(ref slowCalls);
                slowEntered.TrySetResult();
                await slowRelease.Task; // Deliberately ignores cancellation to exercise bounded shutdown.
                return Json("""{"ret":0,"msgs":[],"get_updates_buf":"must-not-save-after-cancel"}""");
            })) { StopTimeout = TimeSpan.FromMilliseconds(80) };
            try
            {
                await slow.ConfigureAsync();
                await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var replacement = slowSettings.WeChat.ReadBinding()!;
                replacement.BotId = prefix + "-replacement";
                slowSettings.WeChat.WriteBinding(replacement);
                await slow.ConfigureAsync();
                using (var held = ReceiverLease.TryAcquire(prefix))
                    Check("reconfigure timeout retains old receiver lease", held == null);
                Check("reconfigure timeout starts no replacement receiver", slowCalls == 1 && slow.ConnectionState == ConnectionState.Degraded);
                using (var unusedNew = ReceiverLease.TryAcquire(replacement.BotId))
                    Check("replacement binding not claimed before old receiver stops", unusedNew != null);
                await slow.DisposeAsync();
                Check("host can await actual shutdown after bounded dispose", slow.HasPendingStop && !slow.WhenStopped.IsCompleted);
                using (var held = ReceiverLease.TryAcquire(prefix))
                    Check("bounded dispose does not release a still-running poll", held == null);
                slowRelease.TrySetResult();
                await UntilAsync(() =>
                {
                    using var returned = ReceiverLease.TryAcquire(prefix);
                    return returned != null;
                });
                Check("lease released after delayed poll actually ends", true);
                Check("cancelled late poll cannot overwrite new binding or cursor",
                    slowSettings.WeChat.ReadBinding() is { Cursor: "" } saved && saved.BotId == replacement.BotId);
            }
            finally
            {
                slowRelease.TrySetResult();
                await slow.DisposeAsync();
            }

            foreach (bool manual in new[] { false, true })
            {
                var sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var sendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var sending = new WeChatService(Config(prefix), Snapshot, () => { }, new Stub(async (request, ct) =>
                {
                    if (IsPoll(request)) { await Task.Delay(Timeout.Infinite, ct); return Json(); }
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return Json();
                    sendEntered.TrySetResult();
                    await sendRelease.Task;
                    return Json("""{"ret":0}""");
                })) { StopTimeout = TimeSpan.FromMilliseconds(80) };
                Task? directSend = null;
                string mode = manual ? "manual operation" : "notification sender";
                try
                {
                    await sending.ConfigureAsync();
                    if (manual) directSend = sending.SendAiTestAsync("synthetic test", CancellationToken.None);
                    else sending.Observe(new TaskRecord { Id = prefix, TurnId = "failed-round", Status = TaskStatusNames.Failed, Project = "synthetic" });
                    await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await sending.DisposeAsync();
                    using (var held = ReceiverLease.TryAcquire(prefix))
                        Check(mode + " must drain before lease release", held == null && sending.HasPendingStop);
                    sendRelease.TrySetResult();
                    if (directSend != null) { try { await directSend; } catch (OperationCanceledException) { } }
                    await sending.WhenStopped.WaitAsync(TimeSpan.FromSeconds(8));
                    using var returned = ReceiverLease.TryAcquire(prefix);
                    Check(mode + " returns lease after completion", returned != null && !sending.HasPendingStop);
                }
                finally
                {
                    sendRelease.TrySetResult();
                    await sending.DisposeAsync();
                }
            }

            var setup = new WeChatService(Config(prefix), Snapshot, () => { },
                new Stub((_, _) => Task.FromResult(Json())));
            bool threw = false;
            setup.Changed += () => throw new InvalidDataException("synthetic setup observer");
            try { await setup.ConfigureAsync(); }
            catch (InvalidDataException) { threw = true; }
            await setup.DisposeAsync();
            Check("setup exception is not hidden", threw);
            using (var returned = ReceiverLease.TryAcquire(prefix)) Check("setup exception drains work and returns lease", returned != null);
        }
        catch (Exception ex)
        {
            checks["test failure: " + ex.GetType().Name] = false;
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString());
            throw;
        }
        finally
        {
            var result = new { passed = checks.Count > 0 && checks.Values.All(v => v), count = checks.Count, checks,
                realWeChatCalls = 0, productionCredentialsRead = false };
            await File.WriteAllTextAsync(Path.Combine(directory, "receiver-lease-checks.json"), JsonSerializer.Serialize(result, SharedJson.Options));
        }
    }
}
