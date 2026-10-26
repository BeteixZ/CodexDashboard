using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

// Runs before production Settings.Load/auth/channel startup. Every file below
// belongs to the new, explicit test directory and contains synthetic data only.
internal static class QueueHistoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--queue-history-test");
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
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        int failures = 0;
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; checks.Add(new { name, passed = false, errorType = ex.GetType().Name }); }
            File.WriteAllText(Path.Combine(directory, "queue-history-checks.json"),
                JsonSerializer.Serialize(new
                {
                    count = checks.Count, failures, networkUsed = false, realAccountUsed = false,
                    actualModelUsed = false, syntheticDpapiFilesOnly = true, checks
                }, Settings.Json));
        }
        string StatePath(string name) => Path.Combine(directory, name + ".state");

        await Check("queued_owner_aba_remains_suspended_and_source_deduplicates_after_restart", async () =>
        {
            string path = StatePath("queued-aba");
            var transport = new FakeTransport();
            var task = SyntheticTask(TaskStatusNames.Active);
            string originalId;
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A");
                await queue.SubmitAsync(task, "SYNTHETIC_QUEUED_PRIVATE_BODY", "source-queued", Snapshot(task), default);
                originalId = queue.Entries.Single().Id;
                queue.SetOwner("owner-B");
                Require(queue.Entries.Count == 0);
                var archived = ReadSynthetic(path).History.Single();
                Require(archived.Owner == "owner-A" && archived.Entries.Single().Id == originalId
                    && archived.Entries.Single().State == "suspended_identity_changed");
                queue.SetOwner("owner-A");
                task.Status = TaskStatusNames.Completed;
                await queue.PumpAsync(Snapshot(task), default);
                string duplicate = await queue.SubmitAsync(task, "different body cannot revive old source",
                    "source-queued", Snapshot(task), default);
                Require(queue.Entries.Count == 0 && queue.EntryForSource("source-queued") == null
                    && transport.Sent.Count == 0 && duplicate.Contains(originalId[..8], StringComparison.Ordinal));
                Require(!File.ReadAllText(path).Contains("SYNTHETIC_QUEUED_PRIVATE_BODY", StringComparison.Ordinal));
            }
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            await restored.SubmitAsync(task, "still cannot revive", "source-queued", Snapshot(task), default);
            await restored.PumpAsync(Snapshot(task), default);
            Require(transport.Sent.Count == 0 && restored.Entries.Count == 0
                && ReadSynthetic(path).History.Single().Entries.Single().Id == originalId);
        });
        foreach (string state in new[] { "submitted", "unconfirmed", "rejected" })
            await Check("historical_" + state + "_source_is_never_resubmitted", async () =>
            {
                string path = StatePath("terminal-" + state);
                var transport = new FakeTransport { Next = new(state, "synthetic result") };
                var task = SyntheticTask();
                await using var queue = new TaskControlQueue(Config(directory), transport, path);
                queue.SetOwner("owner-A");
                await queue.SubmitAsync(task, "synthetic original", "terminal-source", Snapshot(task), default);
                queue.SetOwner("owner-B"); queue.SetOwner("owner-A");
                await queue.SubmitAsync(task, "synthetic duplicate", "terminal-source", Snapshot(task), default);
                await queue.PumpAsync(Snapshot(task), default);
                Require(transport.Sent.Count == 1 && queue.Entries.Count == 0
                    && ReadSynthetic(path).History.Single().Entries.Single().State == state);
            });
        await Check("same_source_is_scoped_to_owner_and_history_never_becomes_active", async () =>
        {
            string path = StatePath("owner-scope");
            var transport = new FakeTransport();
            var task = SyntheticTask();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            await queue.SubmitAsync(task, "synthetic A", "same-source", Snapshot(task), default);
            string firstId = queue.Entries.Single().Id;
            queue.SetOwner("owner-B");
            await queue.SubmitAsync(task, "synthetic B", "same-source", Snapshot(task), default);
            string secondId = queue.Entries.Single().Id;
            queue.SetOwner("owner-A");
            await queue.SubmitAsync(task, "synthetic A duplicate", "same-source", Snapshot(task), default);
            Require(firstId != secondId && transport.Sent.Count == 2 && queue.Entries.Count == 0
                && ReadSynthetic(path).History.Select(h => h.Owner).SequenceEqual(new[] { "owner-A", "owner-B" }));
        });
        await Check("empty_owner_pending_entries_are_archived_and_cannot_replay", async () =>
        {
            string path = StatePath("empty-owner");
            var transport = new FakeTransport();
            var task = SyntheticTask(TaskStatusNames.Active);
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                await queue.SubmitAsync(task, "synthetic unknown owner", "empty-source", Snapshot(task), default);
                queue.SetOwner("owner-A");
                var archived = ReadSynthetic(path).History.Single();
                Require(archived.Owner == "" && archived.Entries.Single().State == "suspended_identity_changed");
            }
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            restored.SetOwner("");
            task.Status = TaskStatusNames.Completed;
            await restored.SubmitAsync(task, "cannot restore unknown owner pending work", "empty-source", Snapshot(task), default);
            await restored.PumpAsync(Snapshot(task), default);
            Require(transport.Sent.Count == 0 && restored.Entries.Count == 0
                && ReadSynthetic(path).History.Single().Owner == "");
        });
        await Check("inflight_owner_change_archives_unknown_and_late_receipt_cannot_restore", async () =>
        {
            string path = StatePath("inflight-aba");
            var entered = Signal();
            var release = Signal();
            var transport = new FakeTransport { Before = () => { entered.TrySetResult(); return release.Task; } };
            var task = SyntheticTask();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            var sending = queue.SubmitAsync(task, "synthetic in flight", "inflight-source", Snapshot(task), default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                queue.SetOwner("owner-B"); queue.SetOwner("owner-A");
                Require(ReadSynthetic(path).History.Single().Entries.Single().State == "unconfirmed"
                    && queue.Entries.Count == 0);
            }
            finally { release.TrySetResult(); await sending.WaitAsync(TimeSpan.FromSeconds(5)); }
            await queue.SubmitAsync(task, "late replay", "inflight-source", Snapshot(task), default);
            await queue.PumpAsync(Snapshot(task), default);
            Require(transport.Sent.Count == 1 && queue.Entries.Count == 0
                && ReadSynthetic(path).History.Single().Entries.Single().State == "unconfirmed");
        });
        await Check("display_compaction_retains_deduplication_history_across_restart", async () =>
        {
            string path = StatePath("compaction");
            var task = SyntheticTask();
            var transport = new FakeTransport();
            var journal = new ControlJournal { Owner = "owner-A" };
            for (int i = 0; i < 258; i++)
                journal.Entries.Add(new()
                {
                    Id = WeChatText.Hash("owner-A\ncompacted-" + i), TaskKey = task.Key,
                    State = "submitted", CreatedAt = DateTimeOffset.UtcNow, Detail = "synthetic accepted"
                });
            WriteSynthetic(path, journal);
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                Require(queue.Entries.Count == 256 && ReadSynthetic(path).History.Single().Entries.Count == 2);
                await queue.SubmitAsync(task, "duplicate beyond display window", "compacted-0", Snapshot(task), default);
                Require(transport.Sent.Count == 0 && queue.Entries.Count == 256);
            }
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            await restored.SubmitAsync(task, "still duplicate after restart", "compacted-1", Snapshot(task), default);
            Require(transport.Sent.Count == 0 && restored.Entries.Count == 256);
        });

        var malformed = new (string Name, string Json)[]
        {
            ("null_document", "null"),
            ("null_owner", """{"owner":null,"entries":[],"history":[]}"""),
            ("null_current_entries", """{"owner":"A","entries":null,"history":[]}"""),
            ("null_history", """{"owner":"A","entries":[],"history":null}"""),
            ("null_history_record", """{"owner":"A","entries":[],"history":[null]}"""),
            ("null_historical_owner", """{"owner":"A","entries":[],"history":[{"owner":null,"entries":[]}]}"""),
            ("null_historical_entries", """{"owner":"A","entries":[],"history":[{"owner":"A","entries":null}]}"""),
            ("null_historical_entry", """{"owner":"A","entries":[],"history":[{"owner":"A","entries":[null]}]}"""),
            ("short_historical_id", """{"owner":"A","entries":[],"history":[{"owner":"A","entries":[{"id":"bad"}]}]}"""),
            ("null_current_entry", """{"owner":"A","entries":[null],"history":[]}"""),
            ("wrong_history_type", """{"owner":"A","entries":[],"history":{}}"""),
            ("invalid_json", """{"owner":"A","entries":[],"history":[""")
        };
        foreach (var item in malformed)
            await Check("malformed_" + item.Name + "_fails_closed_preserving_original", async () =>
            {
                string path = StatePath("malformed-" + item.Name);
                File.WriteAllText(path, LocalSecret.Protect(item.Json));
                byte[] original = File.ReadAllBytes(path);
                var transport = new FakeTransport();
                bool rejected = false;
                try { await using var queue = new TaskControlQueue(Config(directory), transport, path); }
                catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException) { rejected = true; }
                Require(rejected && original.SequenceEqual(File.ReadAllBytes(path))
                    && transport.Sent.Count == 0 && !Directory.EnumerateFiles(directory, "malformed-" + item.Name + ".state.*.tmp").Any());
            });
        await Check("legacy_journal_without_history_remains_readable_without_replay", async () =>
        {
            string path = StatePath("legacy-no-history");
            var task = SyntheticTask();
            string id = WeChatText.Hash("owner-A\nlegacy-source");
            File.WriteAllText(path, LocalSecret.Protect(JsonSerializer.Serialize(new
            {
                owner = "owner-A", entries = new[] { new ControlEntry
                    { Id = id, TaskKey = task.Key, State = "submitted", CreatedAt = DateTimeOffset.UtcNow } }
            }, Settings.Json)));
            var transport = new FakeTransport();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            await queue.SubmitAsync(task, "synthetic duplicate", "legacy-source", Snapshot(task), default);
            Require(transport.Sent.Count == 0 && queue.Entries.Single().Id == id
                && ReadSynthetic(path).History.Count == 0);
        });

        await Check("save_failure_latches_every_control_entrypoint_even_after_unlock", async () =>
        {
            string path = StatePath("fault-all-entrypoints");
            var transport = new FakeTransport();
            var task = SyntheticTask(TaskStatusNames.Active);
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            await queue.SubmitAsync(task, "synthetic pending", "fault-seed", Snapshot(task), default);
            string id = queue.Entries.Single().Id;
            byte[] original = File.ReadAllBytes(path);
            using (LockReplacement(path)) ExpectIo(() => queue.Cancel("unknown-task"));
            task.Status = TaskStatusNames.Completed;
            await ExpectIoAsync(() => queue.SubmitAsync(task, "must not send", "fault-next", Snapshot(task), default));
            await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
            ExpectIo(() => queue.SetOwner("owner-A"));
            ExpectIo(() => queue.SetOwner("owner-B"));
            ExpectIo(() => queue.Cancel(null));
            ExpectIo(() => queue.CancelEntry(id));
            Require(queue.Entries.Single().State == "queued" && queue.EntryForSource("fault-seed")?.Id == id
                && original.SequenceEqual(File.ReadAllBytes(path)) && transport.Sent.Count == 0);
        });
        await Check("save_failure_before_dispatch_never_contacts_transport", async () =>
        {
            string path = StatePath("fault-before-dispatch");
            var transport = new FakeTransport();
            var task = SyntheticTask();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            byte[] original = File.ReadAllBytes(path);
            using (LockReplacement(path))
                await ExpectIoAsync(() => queue.SubmitAsync(task, "must remain unsent", "before-dispatch", Snapshot(task), default));
            await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
            Require(transport.Sent.Count == 0 && original.SequenceEqual(File.ReadAllBytes(path))
                && ReadSynthetic(path).Entries.Count == 0);
        });
        await Check("save_failure_during_owner_change_preserves_disk_and_disables_control", async () =>
        {
            string path = StatePath("fault-owner-change");
            var transport = new FakeTransport();
            var task = SyntheticTask(TaskStatusNames.Active);
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            await queue.SubmitAsync(task, "synthetic old owner", "old-owner", Snapshot(task), default);
            byte[] original = File.ReadAllBytes(path);
            using (LockReplacement(path)) ExpectIo(() => queue.SetOwner("owner-B"));
            task.Status = TaskStatusNames.Completed;
            await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
            await ExpectIoAsync(() => queue.SubmitAsync(task, "must not execute new owner", "new-owner", Snapshot(task), default));
            ExpectIo(() => queue.SetOwner("owner-A"));
            Require(transport.Sent.Count == 0 && original.SequenceEqual(File.ReadAllBytes(path))
                && ReadSynthetic(path).Owner == "owner-A");
        });
        await Check("receipt_save_failure_recovers_as_unconfirmed_without_replay", async () =>
        {
            string path = StatePath("fault-receipt");
            FileStream? held = null;
            var transport = new FakeTransport
            {
                Before = () => { held = LockReplacement(path); return Task.CompletedTask; }
            };
            var task = SyntheticTask();
            await using (var queue = new TaskControlQueue(Config(directory), transport, path))
            {
                queue.SetOwner("owner-A");
                try
                {
                    await ExpectIoAsync(() => queue.SubmitAsync(task, "synthetic sent once", "receipt-source", Snapshot(task), default));
                    Require(ReadSynthetic(path).Entries.Single().State == "sending");
                }
                finally { held?.Dispose(); held = null; }
                await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
                Require(transport.Sent.Count == 1);
            }
            transport.Before = null;
            await using var restored = new TaskControlQueue(Config(directory), transport, path);
            Require(restored.Entries.Single().State == "unconfirmed" && restored.Entries.Single().Instruction == "");
            await restored.PumpAsync(Snapshot(task), default);
            await restored.SubmitAsync(task, "cannot replay ambiguous receipt", "receipt-source", Snapshot(task), default);
            Require(transport.Sent.Count == 1 && restored.Entries.Single().State == "unconfirmed");
        });
        await Check("capacity_limit_fails_before_dispatch_and_retains_previous_journal", async () =>
        {
            string path = StatePath("capacity");
            var transport = new FakeTransport();
            var task = SyntheticTask();
            await using var queue = new TaskControlQueue(Config(directory), transport, path);
            queue.SetOwner("owner-A");
            byte[] original = File.ReadAllBytes(path);
            // Default System.Text.Json escapes this character to six ASCII bytes.
            // The synthetic serialized payload exceeds 64 MiB without DPAPI-writing it.
            string oversized = new('\u4e2d', 64 * 1024 * 1024 / 6 + 1);
            await ExpectIoAsync(() => queue.SubmitAsync(task, oversized, "capacity-source", Snapshot(task), default));
            await ExpectIoAsync(() => queue.PumpAsync(Snapshot(task), default));
            Require(transport.Sent.Count == 0 && original.SequenceEqual(File.ReadAllBytes(path))
                && ReadSynthetic(path).Entries.Count == 0);
        });
        if (failures != 0) throw new InvalidOperationException("queue_history_checks_failed");
    }

    private static Settings Config(string directory) => new()
    {
        CodexHome = Path.Combine(directory, "unused-synthetic-codex"),
        PythonPath = Path.Combine(directory, "never-run.exe"), RemoteHosts = new(),
        ChatGpt = new() { Enabled = true, TaskControlEnabled = true },
        WeChat = new() { ClawBotEnabled = true, PushPlusEnabled = false }
    };
    private static TaskRecord SyntheticTask(string status = TaskStatusNames.Completed) => new()
    {
        Id = "3b1e900a-80f9-494c-85f2-207021913da1", Host = "synthetic-host",
        Title = "Synthetic queue task", Project = "synthetic", Status = status
    };
    private static WeChatSnapshot Snapshot(TaskRecord task) => new(new[] { task }, DateTimeOffset.UtcNow,
        new[] { new HostSnapshot { Name = task.Host, LastSuccess = DateTimeOffset.UtcNow } });
    private static ControlJournal ReadSynthetic(string path)
        => JsonSerializer.Deserialize<ControlJournal>(LocalSecret.Unprotect(File.ReadAllText(path)), Settings.Json)
            ?? throw new InvalidOperationException("synthetic_journal_null");
    private static void WriteSynthetic(string path, ControlJournal journal)
        => File.WriteAllText(path, LocalSecret.Protect(JsonSerializer.Serialize(journal, Settings.Json)));
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
        internal Func<Task>? Before;
        public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string instruction, string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Enqueue((task.Key, instruction, id));
            if (Before != null) await Before();
            return Next;
        }
    }
}
