using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Isolated queue state-machine tests. Configurations are constructed in memory;
// DPAPI files contain synthetic data below a fresh, explicitly supplied directory.
internal static class QueueFenceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--queue-fence-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("explicit_output_required");
            await RunAsync(args[index + 1]);
            exit(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); exit(1); }
        return true;
    }

    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        int failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; checks.Add(new { name, passed = false, errorType = ex.GetType().Name }); }
            File.WriteAllText(Path.Combine(directory, "queue-fence-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, failures, networkUsed = false, realAccountUsed = false,
                actualModelUsed = false, syntheticDpapiFilesOnly = true, checks
            }, Settings.Json));
        }
        string StatePath(string name) => Path.Combine(directory, name + ".state");

        await Check("new_constraints_hold_queued_original_and_survive_restart_without_dispatch", async () =>
        {
            string path = StatePath("hold-restart");
            const string instruction = "SYNTHETIC_PRIVATE_FENCE_BODY Preserve this exact original.";
            var task = SyntheticTask(directory, TaskStatusNames.Active);
            var transport = new FakeTransport();
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A");
                Require(queue.ObserveAuthenticatedInput("input-A", false));
                await queue.SubmitDurableAsync(task, instruction, "operation-A", "input-A", Snapshot(task), default);
                Require(queue.Entries.Single().State == "queued" && transport.Sent.Count == 0);
                Require(queue.ObserveAuthenticatedInput("input-B", false));
                var entry = queue.Entries.Single();
                Require(entry.State == "held_constraints_changed" && entry.Instruction == instruction
                    && entry.RequestSourceId == "input-A" && TaskControlQueue.IsPending(entry));
                var saved = ReadSynthetic(path);
                Require(saved.LatestInputId == "input-B" && saved.ConstraintVersion == 2
                    && saved.Entries.Single().Instruction == instruction);
                Require(!File.ReadAllText(path).Contains("SYNTHETIC_PRIVATE_FENCE_BODY", StringComparison.Ordinal));
            }
            task.Status = TaskStatusNames.Completed;
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            Require(!restored.ObserveAuthenticatedInput("input-B", false));
            await restored.PumpAsync(Snapshot(task), default);
            await restored.SubmitDurableAsync(task, "duplicate cannot overwrite", "operation-A", "input-A", Snapshot(task), default);
            Require(transport.Sent.Count == 0 && restored.Entries.Single().State == "held_constraints_changed"
                && restored.Entries.Single().Instruction == instruction);
        });

        await Check("read_only_input_preserves_registered_queue_and_does_not_change_constraints", async () =>
        {
            string path = StatePath("read-only");
            var task = SyntheticTask(directory, TaskStatusNames.Active);
            var transport = new FakeTransport();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            queue.ObserveAuthenticatedInput("work", false);
            await queue.SubmitDurableAsync(task, "synthetic queued work", "op", "work", Snapshot(task), default);
            long version = ReadSynthetic(path).ConstraintVersion;
            Require(queue.ObserveAuthenticatedInput("query", true));
            Require(queue.Entries.Single().State == "queued" && ReadSynthetic(path).ConstraintVersion == version
                && ReadSynthetic(path).LatestInputId == "query");
            task.Status = TaskStatusNames.Completed;
            await queue.PumpAsync(Snapshot(task), default);
            Require(transport.Sent.Count == 1 && queue.Entries.Single().State == "submitted");
        });

        await Check("late_or_unobserved_source_cannot_create_new_durable_entry", async () =>
        {
            var task = SyntheticTask(directory);
            var transport = new FakeTransport();
            await using var queue = new TaskControlQueue(Config(directory), transport, StatePath("stale-source"));
            queue.SetOwner("owner-A");
            queue.ObserveAuthenticatedInput("old", false);
            queue.ObserveAuthenticatedInput("latest", true);
            foreach (string source in new[] { "old", "unobserved", "" })
                await queue.SubmitDurableAsync(task, "must remain unsent", "op-" + source, source, Snapshot(task), default);
            Require(queue.Entries.Count == 0 && transport.Sent.Count == 0);
            await queue.SubmitDurableAsync(task, "current request", "current-op", "latest", Snapshot(task), default);
            Require(transport.Sent.Count == 1 && queue.EntryForSource("current-op")?.RequestSourceId == "latest");
        });

        await Check("duplicate_authenticated_input_cannot_hold_or_rewind_new_work_even_after_restart", async () =>
        {
            string path = StatePath("duplicate-input");
            var task = SyntheticTask(directory, TaskStatusNames.Active);
            var transport = new FakeTransport();
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A");
                queue.ObserveAuthenticatedInput("old", false);
                queue.ObserveAuthenticatedInput("new", false);
                await queue.SubmitDurableAsync(task, "synthetic newest work", "new-op", "new", Snapshot(task), default);
                Require(!queue.ObserveAuthenticatedInput("old", false) && !queue.ObserveAuthenticatedInput("new", false));
                Require(queue.Entries.Single().State == "queued" && ReadSynthetic(path).LatestInputId == "new"
                    && ReadSynthetic(path).ConstraintVersion == 2);
            }
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            Require(!restored.ObserveAuthenticatedInput("old", false) && restored.Entries.Single().State == "queued");
            task.Status = TaskStatusNames.Completed;
            await restored.PumpAsync(Snapshot(task), default);
            Require(transport.Sent.Count == 1);
        });

        foreach (string receiptState in new[] { "busy", "unavailable" })
            await Check("inflight_" + receiptState + "_after_new_constraints_is_held_without_retry", async () =>
            {
                string path = StatePath("inflight-" + receiptState);
                var task = SyntheticTask(directory);
                var entered = Signal(); var release = Signal();
                var transport = new FakeTransport
                {
                    Next = new(receiptState, "synthetic transient result"),
                    Before = _ => { entered.TrySetResult(); return release.Task; }
                };
                await using (var queue = new TaskControlQueue(Config(directory), transport, path))
                {
                    queue.SetOwner("owner-A");
                    queue.ObserveAuthenticatedInput("old", false);
                    Task<string> sending = queue.SubmitDurableAsync(task, "SYNTHETIC_RETAINED_INFLIGHT_BODY",
                        "inflight-op", "old", Snapshot(task), default);
                    try
                    {
                        await entered.Task.WaitAsync(Timeout);
                        queue.ObserveAuthenticatedInput("new", false);
                        Require(queue.Entries.Single().State == "sending" && !transport.LastToken.IsCancellationRequested);
                        Require(!queue.CancelEntry(queue.Entries.Single().Id));
                        queue.Cancel(null);
                        Require(queue.Entries.Single().State == "sending");
                    }
                    finally { release.TrySetResult(); await sending.WaitAsync(Timeout); }
                    Require(queue.Entries.Single().State == "held_constraints_changed"
                        && queue.Entries.Single().Instruction == "SYNTHETIC_RETAINED_INFLIGHT_BODY");
                    await queue.PumpAsync(Snapshot(task), default);
                }
                transport.Before = null;
                await using var restored = new TaskControlQueue(Config(directory), transport, path);
                await restored.PumpAsync(Snapshot(task), default);
                Require(transport.Sent.Count == 1 && restored.Entries.Single().State == "held_constraints_changed");
            });

        await Check("inflight_terminal_and_unknown_receipts_are_not_cancelled_or_replayed", async () =>
        {
            foreach (string receiptState in new[] { "submitted", "unconfirmed" })
            {
                string path = StatePath("terminal-" + receiptState);
                var task = SyntheticTask(directory);
                var entered = Signal(); var release = Signal();
                var transport = new FakeTransport
                {
                    Next = new(receiptState, "synthetic terminal result"),
                    Before = _ => { entered.TrySetResult(); return release.Task; }
                };
                await using (var queue = new TaskControlQueue(Config(directory), transport, path))
                {
                    queue.SetOwner("owner-A");
                    queue.ObserveAuthenticatedInput("old", false);
                    Task<string> sending = queue.SubmitDurableAsync(task, "synthetic sent once", "op", "old", Snapshot(task), default);
                    try
                    {
                        await entered.Task.WaitAsync(Timeout);
                        queue.ObserveAuthenticatedInput("new", false);
                        Require(queue.Entries.Single().State == "sending" && !transport.LastToken.IsCancellationRequested);
                    }
                    finally { release.TrySetResult(); await sending.WaitAsync(Timeout); }
                    Require(queue.Entries.Single().State == receiptState && queue.Entries.Single().Instruction == "");
                }
                transport.Before = null;
                await using var restored = new TaskControlQueue(Config(directory), transport, path);
                restored.ObserveAuthenticatedInput("third", false);
                await restored.SubmitDurableAsync(task, "duplicate cannot retry", "op", "third", Snapshot(task), default);
                await restored.PumpAsync(Snapshot(task), default);
                Require(transport.Sent.Count == 1 && restored.Entries.Single().State == receiptState);
            }
        });

        await Check("held_entries_can_be_cancelled_by_entry_task_and_all_without_dispatch", async () =>
        {
            string path = StatePath("cancel-held");
            var first = SyntheticTask(directory, TaskStatusNames.Active);
            var second = SyntheticTask(directory, TaskStatusNames.Active, "019bd600-0000-4000-8000-000000000002");
            var third = SyntheticTask(directory, TaskStatusNames.Active, "019bd600-0000-4000-8000-000000000003");
            var transport = new FakeTransport();
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A");
                queue.ObserveAuthenticatedInput("work", false);
                foreach (var task in new[] { first, second, third })
                    await queue.SubmitDurableAsync(task, "synthetic cancellable", task.Id, "work", Snapshot(task), default);
                queue.ObserveAuthenticatedInput("changed", false);
                Require(queue.Entries.All(e => e.State == "held_constraints_changed"));
                Require(queue.CancelEntry(queue.EntryForSource(first.Id)!.Id));
                queue.Cancel(second.Key);
                Require(queue.EntryForSource(third.Id)?.State == "held_constraints_changed");
                queue.Cancel(null);
                Require(queue.Entries.All(e => e.State == "cancelled" && e.Instruction == ""));
            }
            first.Status = second.Status = third.Status = TaskStatusNames.Completed;
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            await restored.PumpAsync(Snapshot(first, second, third), default);
            Require(transport.Sent.Count == 0 && restored.Entries.All(e => e.State == "cancelled"));
        });

        await Check("gate_waiter_rechecks_source_after_later_input_before_creating_entry", async () =>
        {
            var task = SyntheticTask(directory);
            var entered = Signal(); var release = Signal();
            var transport = new FakeTransport { Before = _ => { entered.TrySetResult(); return release.Task; } };
            var coordinator = new SerialStateCoordinator();
            await using var queue = new TaskControlQueue(Config(directory), transport, StatePath("gate-source"), coordinator);
            queue.SetOwner("owner-A");
            queue.ObserveAuthenticatedInput("old", false);
            Task<string> first = queue.SubmitDurableAsync(task, "first in flight", "first", "old", Snapshot(task), default);
            Task<string>? waiting = null;
            try
            {
                await entered.Task.WaitAsync(Timeout);
                waiting = queue.SubmitDurableAsync(task, "must fail after gate", "waiter", "old", Snapshot(task), default);
                // FIFO owner callback runs only after SubmitCore has captured
                // its generation and yielded at the already-held dispatch gate.
                coordinator.Invoke(() => { });
                Require(!waiting.IsCompleted && queue.EntryForSource("waiter") == null);
                queue.ObserveAuthenticatedInput("new", false);
            }
            finally
            {
                release.TrySetResult();
                await first.WaitAsync(Timeout);
                if (waiting != null) await waiting.WaitAsync(Timeout);
            }
            Require(transport.Sent.Count == 1 && queue.EntryForSource("waiter") == null);
            await queue.SubmitDurableAsync(task, "current work allowed", "latest-op", "new", Snapshot(task), default);
            Require(transport.Sent.Count == 2);
        });

        await Check("owner_change_fences_gate_waiters_and_retains_nonreplayable_history", async () =>
        {
            string path = StatePath("gate-owner");
            var task = SyntheticTask(directory);
            var entered = Signal(); var release = Signal();
            var transport = new FakeTransport { Before = _ => { entered.TrySetResult(); return release.Task; } };
            var coordinator = new SerialStateCoordinator();
            await using var queue = new TaskControlQueue(Config(directory), transport, path, coordinator);
            queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("same-source", false);
            Task<string> first = queue.SubmitDurableAsync(task, "owner A in flight", "op-A", "same-source", Snapshot(task), default);
            Task<string>? waiting = null;
            try
            {
                await entered.Task.WaitAsync(Timeout);
                waiting = queue.SubmitDurableAsync(task, "owner A waiting", "wait-A", "same-source", Snapshot(task), default);
                coordinator.Invoke(() => { });
                Require(!waiting.IsCompleted);
                queue.SetOwner("owner-B");
                Require(ReadSynthetic(path).ObservedInputIds.Count == 0 && ReadSynthetic(path).LatestInputId == "");
                Require(queue.ObserveAuthenticatedInput("same-source", false));
            }
            finally
            {
                release.TrySetResult();
                await first.WaitAsync(Timeout);
                if (waiting != null) await waiting.WaitAsync(Timeout);
            }
            Require(queue.Entries.Count == 0 && transport.Sent.Count == 1);
            task.Status = TaskStatusNames.Active;
            await queue.SubmitDurableAsync(task, "owner B queued", "op-B", "same-source", Snapshot(task), default);
            queue.ObserveAuthenticatedInput("B-change", false);
            queue.SetOwner("owner-A");
            queue.ObserveAuthenticatedInput("A-new", false);
            task.Status = TaskStatusNames.Completed;
            await queue.SubmitDurableAsync(task, "A duplicate cannot revive", "op-A", "A-new", Snapshot(task), default);
            await queue.PumpAsync(Snapshot(task), default);
            var saved = ReadSynthetic(path);
            Require(queue.Entries.Count == 0 && transport.Sent.Count == 1
                && saved.History.Single(h => h.Owner == "owner-A").Entries.Single().State == "unconfirmed"
                && saved.History.Single(h => h.Owner == "owner-B").Entries.Single().State == "suspended_identity_changed");
        });

        await Check("input_fence_save_failure_is_not_acknowledged_and_latches_all_future_control", async () =>
        {
            string path = StatePath("input-save-fault");
            var task = SyntheticTask(directory, TaskStatusNames.Active);
            var transport = new FakeTransport();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("old", false);
            await queue.SubmitDurableAsync(task, "synthetic original", "old-op", "old", Snapshot(task), default);
            byte[] original = File.ReadAllBytes(path);
            bool acknowledged = false;
            using (LockReplacement(path))
                ExpectIo(() => acknowledged = queue.ObserveAuthenticatedInput("new", false));
            Require(!acknowledged && !queue.IsWritable && original.SequenceEqual(File.ReadAllBytes(path)));
            task.Status = TaskStatusNames.Completed;
            ExpectIo(() => queue.ObserveAuthenticatedInput("new", false));
            ExpectIo(() => queue.ObserveAuthenticatedInput("query", true));
            ExpectIo(() => queue.SetOwner("owner-B"));
            ExpectIo(() => queue.Cancel(null));
            ExpectIo(() => queue.CancelEntry(queue.Entries.Single().Id));
            await ExpectIoAsync(() => queue.SubmitDurableAsync(task, "must not dispatch", "new-op", "new", Snapshot(task), default));
            await ExpectIoAsync(() => queue.SubmitAsync(task, "legacy also stopped", "legacy-op", Snapshot(task), default));
            await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
            Require(transport.Sent.Count == 0 && ReadSynthetic(path).LatestInputId == "old"
                && original.SequenceEqual(File.ReadAllBytes(path)));
        });

        await Check("sending_crash_snapshot_recovers_unknown_and_preserves_fence_without_replay", async () =>
        {
            string path = StatePath("crash-live"), crashPath = StatePath("crash-snapshot");
            var task = SyntheticTask(directory);
            var entered = Signal(); var release = Signal();
            var transport = new FakeTransport { Before = _ => { entered.TrySetResult(); return release.Task; } };
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("old", false);
                Task<string> sending = queue.SubmitDurableAsync(task, "synthetic in-flight", "op", "old", Snapshot(task), default);
                try
                {
                    await entered.Task.WaitAsync(Timeout);
                    queue.ObserveAuthenticatedInput("new", false);
                    Require(ReadSynthetic(path).Entries.Single().State == "sending");
                    // Copy only this test's encrypted journal at a committed
                    // in-flight point; this models abrupt process loss.
                    File.Copy(path, crashPath, false);
                }
                finally { release.TrySetResult(); await sending.WaitAsync(Timeout); }
            }
            var recoveredTransport = new FakeTransport();
            await using var recovered = new TaskControlQueue(Config(directory), recoveredTransport, crashPath);
            Require(recovered.Entries.Single().State == "unconfirmed" && recovered.Entries.Single().Instruction == ""
                && ReadSynthetic(crashPath).LatestInputId == "new" && !recovered.ObserveAuthenticatedInput("new", false));
            await recovered.SubmitDurableAsync(task, "duplicate cannot replay", "op", "new", Snapshot(task), default);
            await recovered.PumpAsync(Snapshot(task), default);
            Require(recoveredTransport.Sent.Count == 0 && transport.Sent.Count == 1);
        });

        await Check("late_receipt_save_failure_restores_unknown_instead_of_retrying", async () =>
        {
            string path = StatePath("receipt-save-fault");
            var task = SyntheticTask(directory);
            var entered = Signal(); var release = Signal();
            var transport = new FakeTransport { Next = new("busy", "synthetic busy"), Before = _ => { entered.TrySetResult(); return release.Task; } };
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("old", false);
                Task<string> sending = queue.SubmitDurableAsync(task, "synthetic original", "op", "old", Snapshot(task), default);
                try
                {
                    await entered.Task.WaitAsync(Timeout);
                    queue.ObserveAuthenticatedInput("new", false);
                    using (LockReplacement(path))
                    {
                        release.TrySetResult();
                        await ExpectIoAsync(() => sending.WaitAsync(Timeout));
                    }
                    Require(!queue.IsWritable && ReadSynthetic(path).Entries.Single().State == "sending");
                    await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
                }
                finally
                {
                    release.TrySetResult();
                    try { await sending.WaitAsync(Timeout); } catch (IOException) { }
                }
            }
            transport.Before = null;
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            Require(restored.Entries.Single().State == "unconfirmed" && restored.Entries.Single().Instruction == "");
            await restored.PumpAsync(Snapshot(task), default);
            await restored.SubmitDurableAsync(task, "cannot replay", "op", "new", Snapshot(task), default);
            Require(transport.Sent.Count == 1);
        });

        await Check("legacy_schema_remains_readable_and_new_input_holds_legacy_pending_work", async () =>
        {
            foreach (bool changeConstraints in new[] { false, true })
            {
                string path = StatePath("legacy-" + changeConstraints);
                var task = SyntheticTask(directory);
                string id = WeChatText.Hash("owner-A\nlegacy-op");
                File.WriteAllText(path, LocalSecret.Protect(JsonSerializer.Serialize(new
                {
                    owner = "owner-A",
                    entries = new[] { new { id, taskKey = task.Key, instruction = "synthetic legacy work",
                        state = "queued", detail = "", turnId = "", createdAt = DateTimeOffset.UtcNow } }
                }, Settings.Json)));
                var transport = new FakeTransport();
                await using var queue = new TaskControlQueue(Config(directory), transport, path);
                Require(queue.Entries.Single().ConstraintVersion == 0 && queue.Entries.Single().RequestSourceId == "");
                if (changeConstraints) queue.ObserveAuthenticatedInput("new-input", false);
                await queue.PumpAsync(Snapshot(task), default);
                Require(queue.Entries.Single().State == (changeConstraints ? "held_constraints_changed" : "submitted"));
                Require(transport.Sent.Count == (changeConstraints ? 0 : 1));
            }
        });

        await Check("legacy_submit_api_retains_operation_deduplication_with_current_input_fence", async () =>
        {
            var task = SyntheticTask(directory);
            var transport = new FakeTransport();
            await using var queue = new TaskControlQueue(Config(directory), transport, StatePath("legacy-api"));
            queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("current-input", false);
            await queue.SubmitAsync(task, "synthetic legacy", "legacy-op", Snapshot(task), default);
            await queue.SubmitAsync(task, "duplicate must not overwrite", "legacy-op", Snapshot(task), default);
            Require(transport.Sent.Count == 1 && queue.Entries.Single().State == "submitted"
                && queue.Entries.Single().RequestSourceId == "");
        });

        await Check("malformed_fence_metadata_fails_closed_without_changing_original_file", async () =>
        {
            var malformed = new[]
            {
                """{"owner":"A","observedInputIds":null}""",
                """{"owner":"A","observedInputIds":["same","same"],"latestInputId":"same"}""",
                """{"owner":"A","observedInputIds":[],"latestInputId":"not-observed"}""",
                """{"owner":"A","constraintVersion":-1}""",
                """{"owner":"A","observedInputIds":["bad\nid"],"latestInputId":"bad\nid"}""",
                """{"owner":"A","latestInputId":null}"""
            };
            for (int i = 0; i < malformed.Length; i++)
            {
                string path = StatePath("bad-fence-" + i);
                File.WriteAllText(path, LocalSecret.Protect(malformed[i]));
                byte[] original = File.ReadAllBytes(path);
                var transport = new FakeTransport();
                bool rejected = false;
                try { await using var queue = new TaskControlQueue(Config(directory), transport, path); }
                catch (IOException) { rejected = true; }
                Require(rejected && original.SequenceEqual(File.ReadAllBytes(path)) && transport.Sent.Count == 0);
            }
        });

        foreach (string changedField in new[] { "cwd", "turn" })
            await Check("durable_queued_target_" + changedField + "_change_holds_without_reauthorizing", async () =>
            {
                string path = StatePath("changed-target-" + changedField);
                var task = SyntheticTask(directory, TaskStatusNames.Active);
                var transport = new FakeTransport();
                await using (var queue = new TaskControlQueue(Config(directory), transport, path))
                {
                    queue.SetOwner("owner-A"); queue.ObserveAuthenticatedInput("work", false);
                    await queue.SubmitDurableAsync(task, "SYNTHETIC_FROZEN_TARGET_BODY", "op", "work", Snapshot(task), default);
                    if (changedField == "cwd") task.Cwd = Path.Combine(directory, "different-synthetic-workspace");
                    else task.TurnId = "different-synthetic-turn";
                    task.Status = TaskStatusNames.Completed;
                    await queue.PumpAsync(Snapshot(task), default);
                    Require(transport.Sent.Count == 0 && queue.Entries.Single().State == "held_target_changed"
                        && queue.Entries.Single().Instruction == "SYNTHETIC_FROZEN_TARGET_BODY"
                        && TaskControlQueue.IsPending(queue.Entries.Single()));
                }
                await using var restored = new TaskControlQueue(Config(directory), transport, path);
                await restored.PumpAsync(Snapshot(task), default);
                Require(transport.Sent.Count == 0 && restored.Entries.Single().State == "held_target_changed");
                Require(restored.CancelEntry(restored.Entries.Single().Id)
                    && restored.Entries.Single().State == "cancelled" && restored.Entries.Single().Instruction == "");
            });

        if (failures != 0) throw new InvalidOperationException("queue_fence_checks_failed");
    }

    private static Settings Config(string directory) => new()
    {
        CodexHome = Path.Combine(directory, "unused-synthetic-codex"),
        PythonPath = Path.Combine(directory, "never-run.exe"), RemoteHosts = new(),
        ChatGpt = new() { Enabled = true, TaskControlEnabled = true },
        WeChat = new() { ClawBotEnabled = true, PushPlusEnabled = false }
    };
    private static TaskRecord SyntheticTask(string directory, string status = TaskStatusNames.Completed,
        string id = "019bd600-0000-4000-8000-000000000001") => new()
    {
        Id = id, Host = HostIdentity.LocalDisplayName, Cwd = Path.Combine(directory, "synthetic-workspace"),
        TurnId = "synthetic-original-turn", Title = "Synthetic fence task", Project = "synthetic", Status = status
    };
    private static WeChatSnapshot Snapshot(params TaskRecord[] tasks) => new(tasks, DateTimeOffset.UtcNow,
        tasks.Select(t => t.Host).Distinct(StringComparer.Ordinal).Select(host => new HostSnapshot
            { Name = host, LastSuccess = DateTimeOffset.UtcNow }).ToArray());
    private static ControlJournal ReadSynthetic(string path)
        => JsonSerializer.Deserialize<ControlJournal>(LocalSecret.Unprotect(File.ReadAllText(path)), Settings.Json)
            ?? throw new InvalidOperationException("synthetic_journal_null");
    private static FileStream LockReplacement(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("assertion_failed"); }
    private static void ExpectIo(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("expected_io_failure");
    }
    private static async Task ExpectIoAsync(Func<Task> action)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("expected_io_failure");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class FakeTransport : ITaskTransport
    {
        internal ConcurrentQueue<(string Task, string Text, string Id)> Sent { get; } = new();
        internal TaskDelivery Next = new("submitted", "synthetic acceptance", "synthetic-next-turn");
        internal Func<CancellationToken, Task>? Before;
        internal CancellationToken LastToken;
        public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string instruction, string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastToken = ct;
            Sent.Enqueue((task.Key, instruction, id));
            // Intentionally permit an already-sent fake request to return after
            // owner cancellation, exercising late-receipt fencing.
            if (Before != null) await Before(ct);
            return Next;
        }
    }
}
