using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

internal static class BindingSessionRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length == 0 || args[0] != "--binding-session-runtime-test") return false;
        if (args.Length is not (2 or 3) || args.Length == 3 && args[2] != "--expect-defect")
            throw new ArgumentException("exact isolated arguments required");
        string directory = args[1]; bool expectDefect = args.Length == 3;
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("fresh absolute output required");
        Directory.CreateDirectory(directory); Settings.SetDataDirectory(Path.Combine(directory, "isolated-settings"));
        var checks = new Dictionary<string, bool>(); int code = 1;
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "binding-session-checks.json"), JsonSerializer.Serialize(new
                { expectDefect, checks }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            await ReplacementDuringCommitAsync(Path.Combine(directory, "replacement"), false, expectDefect, Check);
            await ReplacementDuringCommitAsync(Path.Combine(directory, "clear"), true, expectDefect, Check);
            if (!expectDefect)
            {
                await UnchangedBindingDrainAsync(Path.Combine(directory, "ordinary-stop"), false, Check);
                await UnchangedBindingDrainAsync(Path.Combine(directory, "persist-tail-failure"), false, Check, failAfterSave: true);
                await UnchangedBindingDrainAsync(Path.Combine(directory, "configure-drain"), true, Check);
                await PartialBatchAsync(Path.Combine(directory, "partial-batch"), Check);
                await SameValueRebindAsync(Path.Combine(directory, "same-value"), Check);
                await LatePollFailureAsync(Path.Combine(directory, "late-http-error"), false, Check);
                await LatePollFailureAsync(Path.Combine(directory, "late-io-error"), true, Check);
            }
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "binding-session-checks.json"), JsonSerializer.Serialize(new
                { passed = code == 0, expectDefect, checks }, Settings.Json));
        }
        exit(code); return true;
    }

    private static async Task ReplacementDuringCommitAsync(string directory, bool clear, bool expectDefect,
        Action<string, bool> check)
    {
        DurableRuntimeTests.Fixture? host = null;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            receiveMessages: true, channelPersistAction: () => host!.Settings.Save());
        host = f; f.Settings.Save();
        string prefix = clear ? "clear_" : "replacement_";
        var oldIdentity = f.Runtime.Identity!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int blocked = 0;
        string source = "synthetic-commit-race-" + prefix;
        string sourceHash = WeChatText.Hash(source);
        var ledgerGate = (SemaphoreSlim)typeof(DurableConversationLedger).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Ledger)!;
        var bindingField = typeof(WeChatService).GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task? configure = null; bool gateHeld = false;
        f.Fault.ObserveCommitted = state =>
        {
            if (!state.GetProperty("inbox").EnumerateArray().Any(x => x.GetProperty("sourceMessageId").GetString() == sourceHash)
                || Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("synthetic_after_flush_release");
        };
        try
        {
            // Force a genuine pending ledger wait before the actual receiver's
            // transaction. Its storage continuation then runs off the UI thread.
            await ledgerGate.WaitAsync(); gateHeld = true;
            f.PollResponses.Writer.TryWrite(f.Batch(source, "状态", "SYNTHETIC_OLD_CURSOR_AFTER", "SYNTHETIC_INBOX_ENTERED"));
            await WaitAsync(() => Task.FromResult((bindingField.GetValue(f.Channel) as ClawBinding)?.ContextToken == "SYNTHETIC_INBOX_ENTERED"));
            ledgerGate.Release(); gateHeld = false;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            check(prefix + "real_receiver_is_paused_after_original_inbox_flush", blocked == 1
                && Application.Current.Dispatcher.CheckAccess() && f.PersistedBinding().SeenMessages.Count == 0);

            var replacement = new ClawBinding { BotId = "synthetic-replacement-" + Guid.NewGuid().ToString("N"),
                UserId = "synthetic-replacement-user", Token = "SYNTHETIC_REPLACEMENT_TOKEN", BaseUrl = WeChatApi.DefaultBaseUrl,
                Cursor = "SYNTHETIC_NEW_CURSOR", ContextToken = "SYNTHETIC_NEW_CONTEXT" };
            if (clear) { f.Settings.WeChat.ProtectedBinding = ""; f.Settings.WeChat.ClawBotEnabled = false; }
            else f.Settings.WeChat.WriteBinding(replacement);
            f.Settings.Save(); string committedReplacement = f.Settings.WeChat.ProtectedBinding;
            configure = f.Channel.ConfigureAsync();
            check(prefix + "replacement_was_saved_before_old_receiver_drained", !configure.IsCompleted
                && f.Runtime.Identity == null && ReadSettings().WeChat.ProtectedBinding == committedReplacement);
            release.Set();
            await configure.WaitAsync(TimeSpan.FromSeconds(12));
            var old = await f.Ledger.ReadConversationAsync(oldIdentity);
            var live = f.Settings.WeChat.ReadBinding(); var saved = ReadSettings().WeChat.ReadBinding();
            bool kept = f.Settings.WeChat.ProtectedBinding == committedReplacement
                && ReadSettings().WeChat.ProtectedBinding == committedReplacement;
            bool overwritten = live?.BotId == f.Binding.BotId && saved?.BotId == f.Binding.BotId
                && live.SeenMessages.Contains(sourceHash) && saved.Cursor == "SYNTHETIC_OLD_CURSOR_AFTER";
            File.WriteAllText(Path.Combine(directory, "race-evidence.json"), JsonSerializer.Serialize(new
            {
                clear, expectDefect, oldInboxCount = old.Inbox.Length,
                oldSourceCount = old.Inbox.Count(x => x.SourceMessageId == sourceHash),
                replacementPreserved = kept, oldBindingOverwroteReplacement = overwritten,
                liveBindingIsOld = live?.BotId == f.Binding.BotId, savedBindingIsOld = saved?.BotId == f.Binding.BotId,
                runtimeIsReplacement = !clear && f.Runtime.Identity == DurableAssistantRuntime.BindingIdentity(replacement, f.Settings.ChatGpt.ActiveAccount),
                runtimeInactive = f.Runtime.Identity == null, old.Operations.Length, oldOutboxCount = old.Outbox.Length,
                fakeSends = f.Sent.Count, f.StartHints, f.StopHints
            }, Settings.Json));
            check(prefix + (expectDefect ? "old_source_demonstrably_overwrites_saved_replacement" : "new_saved_binding_cannot_be_overwritten_by_old_source"), expectDefect ? overwritten : kept);
            check(prefix + "original_inbox_is_retained_without_operation_or_delivery", old.Inbox.Count(x => x.SourceMessageId == sourceHash) == 1
                && old.Operations.IsEmpty && old.Outbox.IsEmpty && f.Sent.IsEmpty && f.Script.Calls == 0);
            check(prefix + "activation_uses_requested_target", clear ? f.Runtime.Identity == null
                : f.Runtime.Identity == DurableAssistantRuntime.BindingIdentity(replacement, f.Settings.ChatGpt.ActiveAccount));
        }
        finally
        {
            if (gateHeld) ledgerGate.Release();
            release.Set(); f.Fault.ObserveCommitted = null;
            if (configure != null) try { await configure.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
    }

    private static async Task UnchangedBindingDrainAsync(string directory, bool reactivate, Action<string, bool> check,
        bool failAfterSave = false)
    {
        DurableRuntimeTests.Fixture? host = null;
        int injected = 0;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            receiveMessages: true, channelPersistAction: () =>
            {
                host!.Settings.Save();
                if (failAfterSave && host.Settings.WeChat.ReadBinding()!.SeenMessages.Count > 0
                    && Interlocked.Exchange(ref injected, 1) == 0)
                    throw new IOException("synthetic_failure_after_progress_was_saved");
            });
        host = f; f.Settings.Save(); var identity = f.Runtime.Identity!;
        var persistTrace = CapturePersistTrace(f.Channel);
        string prefix = failAfterSave ? "persist_tail_failure_" : reactivate ? "configure_drain_" : "ordinary_stop_";
        string source = prefix + "source";
        using var boundary = await PauseInboxAsync(f, source, f.Batch(source, "状态", "SYNTHETIC_FINAL_CURSOR", "SYNTHETIC_PAUSE"));
        Task stopping;
        if (reactivate)
        {
            f.Settings.WeChat.ReportQueriesEnabled = !f.Settings.WeChat.ReportQueriesEnabled;
            f.Settings.Save(); stopping = f.Channel.ConfigureAsync();
        }
        else stopping = f.Channel.DisposeAsync().AsTask();
        check(prefix + "actual_shutdown_waits_for_original_committed_input", !stopping.IsCompleted && f.Runtime.Identity == null);
        boundary.Release(); await stopping.WaitAsync(TimeSpan.FromSeconds(12));
        if (!reactivate) await f.Channel.WhenStopped.WaitAsync(TimeSpan.FromSeconds(12));
        var state = await f.Ledger.ReadConversationAsync(identity); var disk = ReadSettings().WeChat.ReadBinding()!;
        File.WriteAllText(Path.Combine(directory, "stop-fields.json"), JsonSerializer.Serialize(new
        {
            cursor = disk.Cursor, seen = disk.SeenMessages, expectedSource = WeChatText.Hash(source),
            liveCursor = f.Settings.WeChat.ReadBinding()?.Cursor,
            liveSeen = f.Settings.WeChat.ReadBinding()?.SeenMessages,
            inbox = state.Inbox.Select(x => new { x.Id, x.SourceMessageId, x.Revision, x.Stage }),
            operations = state.Operations.Select(x => new { x.Id, x.Stage }),
            outbox = state.Outbox.Select(x => new { x.Id, x.Stage }), sentCount = f.Sent.Count,
            f.StartHints, f.StopHints, stopped = f.Channel.WhenStopped.IsCompletedSuccessfully,
            identityNull = f.Runtime.Identity == null,
            diskEqualsLiveCipher = ReadSettings().WeChat.ProtectedBinding == f.Settings.WeChat.ProtectedBinding,
            persistTrace = persistTrace.ToArray()
        }, Settings.Json));
        check(prefix + "committed_seen_and_complete_batch_cursor_survive_stop", disk.Cursor == "SYNTHETIC_FINAL_CURSOR"
            && disk.SeenMessages.SequenceEqual(new[] { WeChatText.Hash(source) })
            && state.Inbox.Count(x => x.SourceMessageId == WeChatText.Hash(source)) == 1
            && state.Operations.IsEmpty && state.Outbox.IsEmpty && f.Sent.IsEmpty);
        if (reactivate)
        {
            await WaitAsync(() => Task.FromResult(f.PollCursors.Count >= 2));
            var active = ActiveAccount(f.Channel)!;
            check(prefix + "new_activation_reads_postdrain_cursor_and_seen", active.Cursor == "SYNTHETIC_FINAL_CURSOR"
                && active.SeenMessages.SequenceEqual(disk.SeenMessages)
                && f.PollCursors.ToArray()[^1] == "SYNTHETIC_FINAL_CURSOR"
                && f.StartHints == 2 && f.StopHints == 1);
        }
        else check(prefix + "receiver_finishes_before_lease_release", f.Channel.WhenStopped.IsCompletedSuccessfully
            && f.StopHints == 1 && f.Runtime.Identity == null);
        if (failAfterSave) check(prefix + "actual_persist_callback_failed_once", injected == 1
            && persistTrace.Count(x => x.Stage == "threw") == 1);
    }

    private static async Task PartialBatchAsync(string directory, Action<string, bool> check)
    {
        DurableRuntimeTests.Fixture? host = null;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            receiveMessages: true, channelPersistAction: () => host!.Settings.Save());
        host = f; f.Settings.Save(); var identity = f.Runtime.Identity!;
        const string first = "synthetic-partial-first", second = "synthetic-partial-second";
        using var firstJson = JsonDocument.Parse(f.Batch(first, "状态", context: "SYNTHETIC_PAUSE"));
        using var secondJson = JsonDocument.Parse(f.Batch(second, "状态", context: "SYNTHETIC_PAUSE"));
        string batch = JsonSerializer.Serialize(new { ret = 0, get_updates_buf = "SYNTHETIC_WHOLE_BATCH_CURSOR",
            msgs = new[] { firstJson.RootElement.GetProperty("msgs")[0], secondJson.RootElement.GetProperty("msgs")[0] } });
        using var boundary = await PauseInboxAsync(f, first, batch);
        f.Settings.WeChat.ReportQueriesEnabled = !f.Settings.WeChat.ReportQueriesEnabled;
        f.Settings.Save(); var configure = f.Channel.ConfigureAsync();
        check("partial_batch_reconfiguration_waits_for_first_inbox_commit", !configure.IsCompleted && f.Runtime.Identity == null);
        boundary.Release(); await configure.WaitAsync(TimeSpan.FromSeconds(12));
        var beforeReplay = await f.Ledger.ReadConversationAsync(identity); var saved = ReadSettings().WeChat.ReadBinding()!;
        check("partial_batch_stop_keeps_first_seen_but_does_not_commit_unprocessed_cursor", beforeReplay.Inbox.Length == 1
            && beforeReplay.Inbox.Single().SourceMessageId == WeChatText.Hash(first)
            && saved.SeenMessages.SequenceEqual(new[] { WeChatText.Hash(first) })
            && saved.Cursor == "SYNTHETIC_CURSOR_INITIAL" && beforeReplay.Operations.IsEmpty && beforeReplay.Outbox.IsEmpty);
        f.PollResponses.Writer.TryWrite(batch);
        await WaitAsync(() => Task.FromResult(ReadSettings().WeChat.ReadBinding()?.Cursor == "SYNTHETIC_WHOLE_BATCH_CURSOR"));
        var after = await f.Ledger.ReadConversationAsync(identity);
        check("partial_batch_replay_accepts_only_remaining_input_once", after.Inbox.Length == 2
            && after.Inbox.Count(x => x.SourceMessageId == WeChatText.Hash(first)) == 1
            && after.Inbox.Count(x => x.SourceMessageId == WeChatText.Hash(second)) == 1
            && ReadSettings().WeChat.ReadBinding()!.SeenMessages.Distinct().Count() == 2 && after.Operations.IsEmpty);
    }

    private static async Task SameValueRebindAsync(string directory, Action<string, bool> check)
    {
        DurableRuntimeTests.Fixture? host = null;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            receiveMessages: true, channelPersistAction: () => host!.Settings.Save());
        host = f; f.Settings.Save(); var previous = ActiveAccount(f.Channel)!; var oldIdentity = f.Runtime.Identity!;
        string oldCipher = f.Settings.WeChat.ProtectedBinding;
        f.Settings.WeChat.WriteBinding(f.Settings.WeChat.ReadBinding()!); f.Settings.Save();
        check("same_value_explicit_rebind_has_new_opaque_source", oldCipher != f.Settings.WeChat.ProtectedBinding);
        await f.Channel.ConfigureAsync();
        await WaitAsync(() => Task.FromResult(f.StartHints == 2));
        check("same_value_rebind_replaces_activation_instead_of_signature_early_return", !ReferenceEquals(previous, ActiveAccount(f.Channel))
            && f.Runtime.Identity == oldIdentity && !ReferenceEquals(f.Runtime.Identity, oldIdentity) && f.StopHints == 1);
        using var stale = JsonDocument.Parse(f.Batch("synthetic-old-account", "继续 Alpha"));
        bool rejected = false;
        try { await f.Channel.ProcessUpdatesAsync(previous, stale.RootElement, default); }
        catch (InvalidOperationException) { rejected = true; }
        check("old_activation_object_cannot_accept_or_persist_after_same_value_rebind", rejected
            && (await f.ReadAsync()).Inbox.IsEmpty && f.Script.Calls == 0 && f.Sent.IsEmpty);
        f.PollResponses.Writer.TryWrite(f.Batch("synthetic-new-account", "状态", "SYNTHETIC_REBOUND_CURSOR"));
        await WaitAsync(() => Task.FromResult(ReadSettings().WeChat.ReadBinding()?.Cursor == "SYNTHETIC_REBOUND_CURSOR"));
        var state = await f.ReadAsync();
        check("new_activation_can_persist_progress_after_same_value_rebind", state.Inbox.Length == 1
            && state.Inbox.Single().SourceMessageId == WeChatText.Hash("synthetic-new-account")
            && ReadSettings().WeChat.ReadBinding()!.SeenMessages.SequenceEqual(new[] { WeChatText.Hash("synthetic-new-account") }));
    }

    private static async Task LatePollFailureAsync(string directory, bool ioFailure, Action<string, bool> check)
    {
        DurableRuntimeTests.Fixture? host = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int poll = 0, staleErrors = 0; Task? configure = null;
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false,
            receiveMessages: true, channelPersistAction: () => host!.Settings.Save(), channelRequestOverride: async (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/getupdates", StringComparison.Ordinal)
                    || Interlocked.Increment(ref poll) != 1) return null;
                entered.TrySetResult(); await release.Task;
                if (ioFailure) throw new IOException("synthetic_late_poll_failure");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            });
        host = f; f.Settings.Save();
        string prefix = ioFailure ? "late_io_" : "late_http_";
        void Changed()
        {
            if (f.Runtime.Identity == null && f.Channel.ConnectionState is Codex.Protocol.ConnectionState.Degraded
                or Codex.Protocol.ConnectionState.Disconnected) staleErrors++;
        }
        f.Channel.Changed += Changed;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = f.Settings.WeChat.ReadBinding()!;
            replacement.BotId = "synthetic-late-replacement-" + Guid.NewGuid().ToString("N");
            f.Settings.WeChat.WriteBinding(replacement); f.Settings.Save();
            string expected = f.Settings.WeChat.ProtectedBinding;
            configure = f.Channel.ConfigureAsync();
            check(prefix + "replacement_waits_for_noncooperative_actual_poll", !configure.IsCompleted && f.Runtime.Identity == null);
            release.TrySetResult(); await configure.WaitAsync(TimeSpan.FromSeconds(12));
            check(prefix + "old_poll_error_does_not_publish_stale_connection_state", staleErrors == 0
                && f.Settings.WeChat.ProtectedBinding == expected && ReadSettings().WeChat.ProtectedBinding == expected);
            check(prefix + "replacement_receiver_starts_without_old_message_or_send", f.StartHints == 2 && f.StopHints == 1
                && (await f.ReadAsync()).Inbox.IsEmpty && f.Sent.IsEmpty && f.Script.Calls == 0);
        }
        finally
        {
            release.TrySetResult(); f.Channel.Changed -= Changed;
            if (configure != null) try { await configure.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
    }

    private sealed class InboxBoundary(DurableRuntimeTests.Fixture host) : IDisposable
    {
        internal readonly ManualResetEventSlim Signal = new();
        internal void Release() { Signal.Set(); host.Fault.ObserveCommitted = null; }
        public void Dispose() { Release(); Signal.Dispose(); }
    }
    private static async Task<InboxBoundary> PauseInboxAsync(DurableRuntimeTests.Fixture f, string source, string batch)
    {
        var boundary = new InboxBoundary(f); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int blocked = 0;
        f.Fault.ObserveCommitted = state =>
        {
            if (!state.GetProperty("inbox").EnumerateArray().Any(x => x.GetProperty("sourceMessageId").GetString() == WeChatText.Hash(source))
                || Interlocked.CompareExchange(ref blocked, 1, 0) != 0) return;
            entered.TrySetResult();
            if (!boundary.Signal.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("synthetic_drain_after_flush_release");
        };
        var gate = (SemaphoreSlim)typeof(DurableConversationLedger).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Ledger)!;
        bool held = false;
        try
        {
            await gate.WaitAsync(); held = true;
            f.PollResponses.Writer.TryWrite(batch);
            await WaitAsync(() => Task.FromResult(ActiveAccount(f.Channel)?.ContextToken == "SYNTHETIC_PAUSE"));
            gate.Release(); held = false;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            return boundary;
        }
        catch { boundary.Dispose(); throw; }
        finally { if (held) gate.Release(); }
    }
    private static ClawBinding? ActiveAccount(WeChatService service) => (ClawBinding?)typeof(WeChatService)
        .GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

    // Observation only: call the actual fixture callback once, preserve its
    // return/exception, and defer diagnostic file IO until the receiver drains.
    private static ConcurrentQueue<PersistObservation> CapturePersistTrace(WeChatService service)
    {
        var records = new ConcurrentQueue<PersistObservation>();
        var field = typeof(WeChatService).GetField("persist", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Action)field.GetValue(service)!;
        field.SetValue(service, (Action)(() =>
        {
            var account = ActiveAccount(service);
            string? cursor = account?.Cursor; int seen = account?.SeenMessages.Count ?? 0;
            records.Enqueue(new("entered", cursor, seen, null, null));
            try { original(); records.Enqueue(new("returned", cursor, seen, null, null)); }
            catch (Exception error)
            {
                records.Enqueue(new("threw", cursor, seen, error.GetType().FullName, error.ToString()));
                throw;
            }
        }));
        return records;
    }
    private sealed record PersistObservation(string Stage, string? Cursor, int SeenCount, string? ExceptionType, string? Exception);

    private static Settings ReadSettings() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath), Settings.Json)!;
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("synthetic_receiver_boundary");
    }
}
