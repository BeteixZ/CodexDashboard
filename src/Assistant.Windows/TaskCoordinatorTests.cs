using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class TaskCoordinatorTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--task-coordinator-test");
        if (index < 0) return false;
        try { await RunAsync(args[index + 1]); exit(0); }
        catch { exit(1); }
        return true;
    }

    private sealed class Transport : ITaskTransport
    {
        internal Func<CancellationToken, Task>? Before { get; set; }
        internal ConcurrentQueue<(string Key, string Instruction, string MessageId)> Sent { get; } = new();
        public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string instruction, string id, CancellationToken ct)
        {
            Sent.Enqueue((task.Key, instruction, id));
            if (Before != null) await Before(ct);
            return new("submitted", "synthetic acceptance", "synthetic-turn");
        }
    }
    private static Settings Config() => new()
    {
        ChatGpt = new() { Enabled = true, TaskControlEnabled = true },
        WeChat = new() { ClawBotEnabled = true }
    };
    private static TaskRecord TaskRecord(string id, string status = TaskStatusNames.Completed)
        => new() { Id = id, Host = "synthetic-host", Title = id, Project = "synthetic", Status = status };
    private static WeChatSnapshot Snapshot(params TaskRecord[] tasks)
        => new(tasks, DateTimeOffset.UtcNow, new[] { new HostSnapshot { Name = "synthetic-host", LastSuccess = DateTimeOffset.UtcNow } });
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class ReleaseOnExit(TaskCompletionSource signal) : IDisposable
    {
        public void Dispose() => signal.TrySetResult();
    }

    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool result)
        {
            checks.Add(name, result);
            if (!result) throw new InvalidOperationException(name);
        }
        try
        {
            await using var actor = new SerialStateCoordinator();
            int count = 0;
            var owners = new ConcurrentBag<int>();
            await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => actor.Invoke(() =>
            {
                owners.Add(Environment.CurrentManagedThreadId);
                count++;
            }))));
            Check("concurrent state mutations have one owner", count == 50 && owners.Distinct().Count() == 1);
            Check("owner invoke is reentrant", actor.Invoke(() => actor.Invoke(() => actor.IsOwnerThread)));
            var yielded = Signal(); var release = Signal();
            using var releaseActor = new ReleaseOnExit(release);
            int before = 0, after = 0;
            var active = actor.InvokeAsync(async () =>
            {
                before = Environment.CurrentManagedThreadId;
                yielded.SetResult();
                await release.Task;
                after = Environment.CurrentManagedThreadId;
            });
            await yielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("awaited I/O leaves owner available", actor.Invoke(() => ++count) == 51);
            var shutdown = actor.DisposeAsync().AsTask();
            Check("coordinator shutdown waits for admitted async operation", !shutdown.IsCompleted);
            bool rejected = false;
            try { actor.Invoke(() => count++); } catch (ObjectDisposedException) { rejected = true; }
            Check("shutdown rejects new operations", rejected);
            release.SetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Check("async continuation returns to same owner during shutdown", before == after && owners.Contains(after));

            var idle = TaskRecord("idle");
            var transport = new Transport { Before = _ => Task.Delay(20) };
            await using (var queue = new TaskControlQueue(Config(), transport, null))
            {
                queue.SetOwner("owner");
                var replies = await Task.WhenAll(Enumerable.Range(0, 25)
                    .Select(_ => Task.Run(() => queue.SubmitAsync(idle, "same instruction", "same-message", Snapshot(idle), default))));
                Check("concurrent duplicate message only dispatches once", transport.Sent.Count == 1 && queue.Entries.Count == 1);
                Check("concurrent duplicates retain stable operation identity",
                    replies.Distinct().Count() == 1 && transport.Sent.Single().MessageId == "wechat-" + queue.Entries.Single().Id[..32]);
                var copy = queue.Entries.Single(); copy.State = "queued"; copy.Instruction = "external mutation";
                Check("entry snapshots cannot mutate actor journal", queue.Entries.Single().State == "submitted" && queue.Entries.Single().Instruction.Length == 0);
            }

            var pending = TaskRecord("pending", TaskStatusNames.Active);
            var blockedEntered = Signal(); var blockedRelease = Signal();
            var blockedTransport = new Transport { Before = _ => { blockedEntered.SetResult(); return blockedRelease.Task; } };
            await using (var queue = new TaskControlQueue(Config(), blockedTransport, null))
            {
                using var unblock = new ReleaseOnExit(blockedRelease);
                queue.SetOwner("owner");
                await queue.SubmitAsync(pending, "cancel me", "queued", Snapshot(pending), default);
                var sending = queue.SubmitAsync(idle, "in progress", "sending", Snapshot(idle), default);
                await blockedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                string cancelled = await Task.Run(() => queue.Cancel(pending.Key)).WaitAsync(TimeSpan.FromSeconds(2));
                Check("cancel runs while transport is awaiting I/O", cancelled.Contains("1 条") && !sending.IsCompleted);
                Check("cancel only affects queued entries", queue.Entries.Single(e => e.TaskKey == pending.Key).State == "cancelled"
                    && queue.Entries.Single(e => e.TaskKey == idle.Key).State == "sending");
                blockedRelease.SetResult(); await sending;
            }

            var abaEntered = Signal(); var abaRelease = Signal();
            var abaTransport = new Transport { Before = _ => { abaEntered.TrySetResult(); return abaRelease.Task; } };
            await using (var queue = new TaskControlQueue(Config(), abaTransport, null))
            {
                using var unblock = new ReleaseOnExit(abaRelease);
                var task = TaskRecord("aba", TaskStatusNames.Active);
                var notices = new ConcurrentQueue<string>();
                queue.Notify = (receipt, _) => { notices.Enqueue(receipt.Id); return Task.CompletedTask; };
                queue.SetOwner("A");
                await queue.SubmitAsync(task, "old owner instruction", "old-message", Snapshot(task), default);
                task.Status = TaskStatusNames.Completed;
                var pump = queue.PumpAsync(Snapshot(task), default);
                await abaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() => { queue.SetOwner("B"); queue.SetOwner("A"); }).WaitAsync(TimeSpan.FromSeconds(2));
                Check("owner change proceeds while transport is awaiting I/O", !pump.IsCompleted && queue.Entries.Count == 0);
                abaRelease.SetResult(); await pump;
                Check("ABA owner change discards late reply and receipt", queue.Entries.Count == 0 && notices.Count == 0);
            }

            var notifyEntered = Signal(); var notifyRelease = Signal();
            var notifyTransport = new Transport();
            await using (var queue = new TaskControlQueue(Config(), notifyTransport, null))
            {
                using var unblock = new ReleaseOnExit(notifyRelease);
                queue.SetOwner("receipt-owner");
                var one = TaskRecord("first-receipt", TaskStatusNames.Active);
                var two = TaskRecord("next-after-receipt", TaskStatusNames.Active);
                await queue.SubmitAsync(one, "first instruction", "first-receipt", Snapshot(one, two), default);
                await queue.SubmitAsync(two, "must stay with old owner", "next-receipt", Snapshot(one, two), default);
                queue.Notify = async (_, _) => { notifyEntered.TrySetResult(); await notifyRelease.Task; };
                one.Status = two.Status = TaskStatusNames.Completed;
                var pump = queue.PumpAsync(Snapshot(one, two), default);
                await notifyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                queue.SetOwner("next-receipt-owner");
                notifyRelease.SetResult(); await pump;
                Check("identity change during receipt stops remaining old batch", notifyTransport.Sent.Count == 1 && queue.Entries.Count == 0);
            }

            var waitingEntered = Signal(); var waitingRelease = Signal();
            var waitingTransport = new Transport { Before = _ => { waitingEntered.TrySetResult(); return waitingRelease.Task; } };
            var waitingActor = new SerialStateCoordinator();
            await using (var queue = new TaskControlQueue(Config(), waitingTransport, null, waitingActor))
            {
                using var unblock = new ReleaseOnExit(waitingRelease);
                queue.SetOwner("first-owner");
                var first = queue.SubmitAsync(idle, "first", "first", Snapshot(idle), default);
                await waitingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stale = queue.SubmitAsync(idle, "must not use next identity", "stale", Snapshot(idle), default);
                waitingActor.Invoke(() => { }); // Admission barrier: stale Submit is now awaiting the delivery gate.
                queue.SetOwner("second-owner");
                waitingRelease.SetResult();
                await first;
                string staleReply = await stale;
                Check("submission waiting across identity change is not rebound", waitingTransport.Sent.Count == 1
                    && queue.Entries.Count == 0 && staleReply.Contains("没有投递"));
            }

            var pumpingEntered = Signal(); var pumpingRelease = Signal();
            var pumpingTransport = new Transport { Before = _ => { pumpingEntered.TrySetResult(); return pumpingRelease.Task; } };
            await using (var queue = new TaskControlQueue(Config(), pumpingTransport, null))
            {
                using var unblock = new ReleaseOnExit(pumpingRelease);
                queue.SetOwner("pumping");
                var task = TaskRecord("pumping", TaskStatusNames.Active);
                int receipts = 0;
                queue.Notify = (_, _) => { Interlocked.Increment(ref receipts); return Task.CompletedTask; };
                await queue.SubmitAsync(task, "dispatch once", "pumping", Snapshot(task), default);
                task.Status = TaskStatusNames.Completed;
                var pumps = Enumerable.Range(0, 25).Select(_ => Task.Run(() => queue.PumpAsync(Snapshot(task), default))).ToArray();
                await pumpingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("concurrent pumps reserve one delivery", pumpingTransport.Sent.Count == 1);
                pumpingRelease.SetResult(); await Task.WhenAll(pumps);
                Check("concurrent pumps produce one receipt", receipts == 1 && queue.Entries.Single().State == "submitted");
            }

            string journal = Path.Combine(directory, "shutdown-queue.state");
            var closingEntered = Signal();
            var closingTransport = new Transport { Before = async ct => { closingEntered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
            await using var closing = new TaskControlQueue(Config(), closingTransport, journal);
            closing.SetOwner("closing");
            var submission = closing.SubmitAsync(idle, "synthetic private instruction", "closing", Snapshot(idle), default);
            await closingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            string last = await submission;
            Check("shutdown cancels and drains in-flight delivery", last.Contains("未确认"));
            Check("shutdown journal protects instruction text", !File.ReadAllText(journal).Contains("synthetic private instruction"));
            var restartedTransport = new Transport();
            await using (var restarted = new TaskControlQueue(Config(), restartedTransport, journal))
            {
                Check("cancelled dispatch persists as unknown", restarted.Entries.Single().State == "unconfirmed"
                    && restarted.Entries.Single().Instruction.Length == 0);
                await restarted.PumpAsync(Snapshot(idle), default);
                Check("shutdown recovery does not replay unknown operation", restartedTransport.Sent.Count == 0);
            }
        }
        catch (Exception ex)
        {
            checks["test failure: " + ex.GetType().Name] = false;
            await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), ex.ToString());
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "coordinator-checks.json"), JsonSerializer.Serialize(new
            {
                passed = checks.Count > 0 && checks.Values.All(v => v), count = checks.Count, checks,
                realCodexOperations = 0, productionCredentialsRead = false
            }, SharedJson.Options));
        }
    }
}
