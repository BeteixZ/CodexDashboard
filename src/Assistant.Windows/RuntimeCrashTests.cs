using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// The only process terminated is a child created by this isolated test runner.
// Its channel/model handlers are synthetic and production startup is bypassed.
internal static class RuntimeCrashTests
{
    private static readonly string[] Phases =
        ["inbox-before-write", "inbox-committed", "generating", "prepared", "sending", "http-started", "accepted"];
    private const string SourceId = "synthetic-crash-original-input";
    private const string SourceBody = "帮我解释这条隔离崩溃检查记录";
    private sealed record ChildSpec(string Nonce, string Phase);
    private sealed record ChildInfo(ConversationIdentity Identity, string Phase);
    private sealed record Wire(string ClientId, string Body);

    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        bool parent = args.Contains("--runtime-crash-test");
        bool child = args.Contains("--runtime-crash-child");
        if (!parent && !child) return false;
        try
        {
            if (parent && !child && args.Length == 2 && args[0] == "--runtime-crash-test")
                await RunAsync(args[1]);
            else if (child && !parent && args.Length == 4 && args[0] == "--runtime-crash-child")
                await RunChildAsync(args[1], args[2], args[3]);
            else throw new ArgumentException("Explicit isolated crash-test arguments required.");
            exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static string ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("Absolute isolated path required.");
        path = Path.GetFullPath(path);
        for (var part = new DirectoryInfo(path); part != null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        return path;
    }

    private static async Task RunAsync(string directory)
    {
        directory = ValidatePath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Fresh output required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        var processes = new List<object>();
        void Check(string name, bool condition)
        {
            checks.Add(name, condition);
            File.WriteAllText(Path.Combine(directory, "runtime-crash-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, productionDataRead = false, actualModelUsed = false, checks, processes }, Settings.Json));
            if (!condition) throw new InvalidOperationException(name);
        }

        foreach (var phase in Phases)
        {
            var path = Path.Combine(directory, phase);
            Directory.CreateDirectory(path);
            string nonce = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(path, "child-spec.json"), JsonSerializer.Serialize(new ChildSpec(nonce, phase), Settings.Json));
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--runtime-crash-child"); start.ArgumentList.Add(path);
            start.ArgumentList.Add(phase); start.ArgumentList.Add(nonce);
            string temp = Path.Combine(path, "tmp"); Directory.CreateDirectory(temp);
            start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
            var began = DateTimeOffset.UtcNow;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Synthetic child did not start.");
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            bool timedOut = false;
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException)
            {
                timedOut = true;
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            }
            string errors = await stderr;
            File.WriteAllText(Path.Combine(path, "child.stdout.log"), await stdout);
            File.WriteAllText(Path.Combine(path, "child.stderr.log"), errors);
            processes.Add(new { phase, processId = child.Id, child.ExitCode, timedOut, began, ended = DateTimeOffset.UtcNow });
            Check(phase + "_actual_child_exits_at_requested_crash_boundary",
                !timedOut && child.ExitCode == 81 + Array.IndexOf(Phases, phase) && errors.Length == 0
                && File.ReadAllText(Path.Combine(path, "crash-marker.txt")) == phase);

            string fixturePath = Path.Combine(path, "fixture");
            var info = JsonSerializer.Deserialize<ChildInfo>(File.ReadAllText(Path.Combine(path, "child-info.json")), Settings.Json)!;
            var binding = JsonSerializer.Deserialize<ClawBinding>(LocalSecret.Unprotect(
                File.ReadAllText(Path.Combine(fixturePath, "synthetic-binding.state"))), Settings.Json)!;
            string sourceHash = WeChatText.Hash(SourceId);
            ConversationSnapshot recovered;
            await using (var ledger = await DurableConversationLedger.OpenAsync(
                new FileJournalStorage(Path.Combine(fixturePath, "synthetic-dialogue.state")), new WindowsStateProtector(),
                options: new() { RequireOutputDependencies = true }))
                recovered = await ledger.ReadConversationAsync(info.Identity);
            File.WriteAllText(Path.Combine(path, "recovery-observation.json"), JsonSerializer.Serialize(new
            {
                recovered.IsReadOnly, recovered.RecoveryReason, inbox = recovered.Inbox.Select(x => new { x.Id, x.SourceMessageId, x.Stage }),
                generations = recovered.Generations.Select(x => new { x.Id, x.Stage }),
                outbox = recovered.Outbox.Select(x => new { x.Id, x.Stage, x.SendAttempts, x.ChannelReceipt })
            }, Settings.Json));
            if (phase == "inbox-before-write")
                Check("uncommitted_input_has_no_durable_work_or_channel_acknowledgement",
                    !recovered.IsReadOnly && recovered.RecoveryReason == null && recovered.Inbox.IsEmpty
                    && recovered.Generations.IsEmpty && recovered.Outbox.IsEmpty && recovered.Operations.IsEmpty
                    && recovered.Authorizations.IsEmpty && !binding.SeenMessages.Contains(sourceHash));
            else
                Check(phase + "_durable_source_is_preserved_and_not_silently_removed",
                    !recovered.IsReadOnly && recovered.RecoveryReason == null && recovered.Inbox.Length == 1
                    && recovered.Inbox[0].SourceMessageId == sourceHash && recovered.Inbox[0].Body == SourceBody
                    && recovered.Operations.IsEmpty && recovered.Authorizations.IsEmpty);
            if (phase == "inbox-committed")
                Check("inbox_commit_precedes_channel_seen_marker_even_across_process_exit",
                    !binding.SeenMessages.Contains(sourceHash) && recovered.Inbox[0].Stage == InboxStage.Received
                    && recovered.Generations.IsEmpty && recovered.Outbox.IsEmpty);
            if (phase == "generating")
                Check("interrupted_inference_has_no_output_or_write_and_remains_recoverable",
                    recovered.Generations.Single().Stage == GenerationStage.Interrupted
                    && recovered.Inbox[0].Stage == InboxStage.Received && recovered.Outbox.IsEmpty);
            if (phase == "prepared")
                Check("prepared_reply_survives_without_an_attempt",
                    recovered.Generations.Single().Stage == GenerationStage.Prepared
                    && recovered.Generations.Single().PreparedSegments.Single() == PreparedBody(phase)
                    && recovered.Outbox.IsEmpty);
            if (phase is "sending" or "http-started")
                Check(phase + "_inflight_receipt_recovers_as_unknown_not_rejected_or_accepted",
                    recovered.Outbox.Single().Stage == OutboxStage.Unknown && recovered.Outbox.Single().SendAttempts == 1
                    && recovered.Outbox.Single().Body == PreparedBody(phase)
                    && (phase == "http-started") == File.Exists(Path.Combine(path, "child-wire.json")));
            if (phase == "accepted")
                Check("committed_acceptance_survives_process_exit_with_original_receipt",
                    recovered.Outbox.Single().Stage == OutboxStage.Accepted && recovered.Outbox.Single().SendAttempts == 1
                    && recovered.Outbox.Single().ChannelReceipt != null && File.Exists(Path.Combine(path, "child-wire.json")));

            var wires = new List<Wire>();
            await using var resumed = await DurableRuntimeTests.Fixture.CreateAsync(fixturePath, savedBinding: binding,
                channelRequestOverride: async (request, ct) =>
                {
                    if (await ReadWireAsync(request, ct) is { } wire) wires.Add(wire);
                    return null;
                });
            if (phase == "inbox-before-write")
            {
                await Task.Delay(1100);
                var idle = await resumed.ReadAsync();
                Check("uncommitted_input_is_not_invented_by_recovery",
                    idle.Inbox.IsEmpty && idle.Outbox.IsEmpty && resumed.Script.Calls == 0 && wires.Count == 0
                    && !resumed.PersistedBinding().SeenMessages.Contains(sourceHash));
                // The channel has not acknowledged this source. A redelivery is
                // the first committed receipt, not a replay of an execution.
                await resumed.InputAsync(SourceId, SourceBody);
            }
            if (phase is "inbox-before-write" or "inbox-committed" or "generating" or "prepared")
            {
                await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(x => x.Stage == OutboxStage.Accepted));
                var after = await resumed.ReadAsync();
                Check(phase + "_recovery_finishes_the_original_input_once",
                    after.Inbox.Length == 1 && after.Outbox.Length == 1 && after.Outbox[0].Stage == OutboxStage.Accepted
                    && after.Outbox[0].SendAttempts == 1 && wires.Count == 1
                    && wires[0].Body == (phase == "prepared" ? PreparedBody(phase) : "我在。")
                    && resumed.Script.Calls == (phase == "prepared" ? 0 : 1));
            }
            else
            {
                await resumed.InputAsync("query-after-crash", "状态");
                await WaitAsync(async () => (await resumed.ReadAsync()).Outbox.Any(x =>
                    x.Revision == 2 && x.Kind == OutboxKind.ConversationReply && x.Stage == OutboxStage.Accepted));
                var after = await resumed.ReadAsync();
                var prior = recovered.Outbox.Single();
                Check(phase + "_fresh_input_works_without_replaying_old_attempt",
                    after.Outbox.Single(x => x.Id == prior.Id) == prior
                    && !wires.Any(x => x.ClientId == "assistant-outbox-" + prior.Id.ToString("N"))
                    && !wires.Any(x => x.Body == prior.Body) && resumed.Script.Calls == 0);
            }
            var beforeDuplicate = await resumed.ReadAsync();
            int wireCount = wires.Count;
            await resumed.InputAsync(SourceId, SourceBody);
            await Task.Delay(1100);
            var final = await resumed.ReadAsync();
            Check(phase + "_redelivered_channel_source_cannot_create_duplicate_work",
                final.Inbox.Length == beforeDuplicate.Inbox.Length && final.Revision == beforeDuplicate.Revision
                && final.Outbox.Length == beforeDuplicate.Outbox.Length && wires.Count == wireCount
                && final.Operations.IsEmpty && final.Authorizations.IsEmpty
                && resumed.PersistedBinding().SeenMessages.Contains(sourceHash));
            // Most phases already persisted a channel Seen marker. Exercise the
            // ledger's independent deduplication as well, without removing or
            // rewriting any original channel fact merely to make the test pass.
            string beforeLedgerDuplicate = ExecutionProjection(final);
            var repeated = await resumed.Ledger.ReceiveAsync(info.Identity, sourceHash, SourceBody,
                MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var afterLedgerDuplicate = await resumed.ReadAsync();
            var original = phase == "inbox-before-write"
                ? beforeDuplicate.Inbox.Single(x => x.SourceMessageId == sourceHash) : recovered.Inbox[0];
            Check(phase + "_ledger_duplicate_keeps_original_identity_and_execution_records",
                repeated.IsDuplicate && repeated.Message.Id == original.Id
                && repeated.Message.OccurredAt == original.OccurredAt
                && repeated.Message.ReceivedAt == original.ReceivedAt
                && ExecutionProjection(afterLedgerDuplicate) == beforeLedgerDuplicate);
        }
    }

    private static string ExecutionProjection(ConversationSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        snapshot.Revision, snapshot.Inbox, snapshot.Generations, snapshot.Outbox, snapshot.InputWork,
        snapshot.ToolInvocations, snapshot.AgentBudgets, snapshot.Operations, snapshot.Authorizations
    }, Settings.Json);

    private static string PreparedBody(string phase) => "崩溃前准备的原回复 " + phase + " 😀  \n";
    private static async Task RunChildAsync(string directory, string phase, string nonce)
    {
        directory = ValidatePath(directory);
        if (!Phases.Contains(phase) || !Guid.TryParseExact(nonce, "N", out _))
            throw new ArgumentException("Known synthetic phase and nonce required.");
        var spec = JsonSerializer.Deserialize<ChildSpec>(File.ReadAllText(Path.Combine(directory, "child-spec.json")), Settings.Json);
        if (spec != new ChildSpec(nonce, phase)) throw new ArgumentException("Synthetic parent marker mismatch.");
        string fixturePath = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixturePath) || File.Exists(fixturePath)) throw new ArgumentException("Fresh child fixture required.");
        void Crash()
        {
            File.WriteAllText(Path.Combine(directory, "crash-marker.txt"), phase);
            Environment.Exit(81 + Array.IndexOf(Phases, phase));
        }
        await using var fixture = await DurableRuntimeTests.Fixture.CreateAsync(fixturePath,
            channelRequestOverride: async (request, ct) =>
            {
                if (await ReadWireAsync(request, ct) is { } wire)
                {
                    File.WriteAllText(Path.Combine(directory, "child-wire.json"), JsonSerializer.Serialize(wire, Settings.Json));
                    if (phase == "http-started") Crash();
                }
                return null;
            });
        File.WriteAllText(Path.Combine(directory, "child-info.json"),
            JsonSerializer.Serialize(new ChildInfo(fixture.Runtime.Identity!, phase), Settings.Json));
        fixture.Script.Handler = (_, _) =>
        {
            if (phase == "generating") Crash();
            return Task.FromResult(ToolModelResponse.Reply(PreparedBody(phase)));
        };
        fixture.Fault.ObserveCommitted = snapshot =>
        {
            bool Matches(string collection, string state) => snapshot.GetProperty(collection).EnumerateArray()
                .Any(x => x.GetProperty("stage").GetString() == state);
            if (phase == "inbox-committed" && snapshot.GetProperty("inbox").EnumerateArray()
                .Any(x => x.GetProperty("sourceMessageId").GetString() == WeChatText.Hash(SourceId))) Crash();
            if (phase == "prepared" && Matches("generations", "Prepared")) Crash();
            if (phase == "sending" && Matches("outbox", "Sending")) Crash();
            if (phase == "accepted" && Matches("outbox", "Accepted")) Crash();
        };
        fixture.Fault.Observe = stage =>
        {
            if (phase == "inbox-before-write" && stage == JournalWriteStage.BeforeWrite
                && fixture.Fault.CandidateSnapshot is { } snapshot
                && snapshot.GetProperty("inbox").EnumerateArray()
                    .Any(x => x.GetProperty("sourceMessageId").GetString() == WeChatText.Hash(SourceId)))
                Crash();
        };
        await fixture.InputAsync(SourceId, SourceBody);
        await Task.Delay(TimeSpan.FromSeconds(15));
        throw new InvalidOperationException("Requested synthetic crash boundary was not reached.");
    }

    private static async Task<Wire?> ReadWireAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var message = json.RootElement.GetProperty("msg");
        return new(message.GetProperty("client_id").GetString()!,
            message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic recovery condition not reached.");
            await Task.Delay(20);
        }
    }
}
