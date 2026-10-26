using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual host, approval pump and protected ledger; all model/RPC/HTTP peers are synthetic.
internal static class RequiredDecisionRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--required-decision-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit new synthetic directory required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true)
            throw new ArgumentException("New absolute WPF test directory required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "required-decision-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual host with synthetic owned RPC, model, HTTP and protected ledger"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await ExactProofAndCommitRaceAsync(Path.Combine(directory, "proof-and-commit"), Check);
        await ExpiryAndCompletionAsync(Path.Combine(directory, "expiry-and-completion"), Check);
        await DisconnectionAndRestartAsync(Path.Combine(directory, "disconnection-and-restart"), Check);
        await LateAcceptedReceiptAsync(Path.Combine(directory, "late-accepted"), Check);
        await ApprovalAfterInputWindowAsync(Path.Combine(directory, "approval-after-input-window"), Check);
    }

    private static async Task ExactProofAndCommitRaceAsync(string directory, Action<string, bool> check)
    {
        await using var f = await OwnedFixture.CreateAsync(directory);
        var runtime = f.Host.Runtime.OwnedRuntime!;
        var approval = f.EmitApproval(901);
        var notice = await f.AcceptedAsync(approval);
        check("live_accepted_detail_binds_exact_pending_request_and_epoch",
            runtime.TryValidateRequiredDecision(notice, out var live, out var reason) && reason == ""
            && live.Token == approval.Token && live.ConnectionEpoch == approval.ConnectionEpoch
            && runtime.Presented(notice.Id)?.Request == live
            && (await f.Host.Ledger.ReadConversationContextAsync(runtime.Identity)).PendingApproval?.SourceEventId == notice.Id);

        var proof = notice.RequiredDecision!;
        var task = notice.Task!;
        check("forged_scope_token_epoch_expiry_and_notification_key_are_rejected",
            !runtime.TryValidateRequiredDecision(notice with { RequiredDecision = proof with { ProviderScope = "unknown" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { RequiredDecision = proof with { DecisionToken = "different" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { RequiredDecision = proof with { ConnectionEpoch = Guid.NewGuid() } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { RequiredDecision = proof with { ExpiresAt = proof.ExpiresAt.AddSeconds(1) } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { ExternalEventKey = "unrelated-event" }, out _, out _));
        check("missing_marker_foreign_identity_host_profile_thread_and_turn_are_rejected",
            !runtime.TryValidateRequiredDecision(notice with { RequiredDecision = null }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { Identity = notice.Identity with { BindingId = "different" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { Task = task with { HostId = "different" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { Task = task with { ProfileId = "different" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { Task = task with { ThreadId = "different" } }, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice with { Task = task with { TurnId = "different" } }, out _, out _));

        f.Resolve(901);
        bool bindResolved = await runtime.TryBindPresentedApprovalAsync("demo", approval.Token, notice.Id);
        await f.WaitNoPendingAsync();
        check("resolved_request_cannot_rebind_presented_or_keep_core_pending",
            !bindResolved && !runtime.TryValidateRequiredDecision(notice, out _, out _)
            && runtime.Presented(notice.Id) == null && f.Host.Runtime.LastError == null);

        var next = f.EmitApproval(902);
        var nextNotice = await f.AcceptedAsync(next);
        check("new_token_on_same_task_does_not_revive_previous_detail",
            next.Token != approval.Token && runtime.TryValidateRequiredDecision(nextNotice, out _, out _)
            && !runtime.TryValidateRequiredDecision(notice, out _, out _)
            && runtime.Presented(nextNotice.Id)?.Request.Token == next.Token);

        var pending = (await f.Host.Ledger.ReadConversationContextAsync(runtime.Identity)).PendingApproval!;
        await f.Host.Ledger.ResolvePendingApprovalAsync(runtime.Identity, pending.Id);
        int committed = 0;
        bool retained;
        f.Host.Fault.Observe = stage =>
        {
            if (stage == JournalWriteStage.AfterFlush && Interlocked.Increment(ref committed) == 1)
                f.Resolve(902); // The peer resolves the request during the durable pending-slot commit.
        };
        try { retained = await runtime.TryRecordPendingApprovalAsync(nextNotice); }
        finally { f.Host.Fault.Observe = null; }
        await f.WaitNoPendingAsync();
        check("resolution_during_core_pending_commit_cleans_only_the_stale_slot",
            committed > 0 && !retained && runtime.Presented(nextNotice.Id) == null
            && !runtime.TryValidateRequiredDecision(nextNotice, out _, out _) && f.Host.Runtime.LastError == null);

        var focused = f.EmitApproval(903);
        var focusedNotice = await f.AcceptedAsync(focused);
        runtime.ClearFocus();
        await f.WaitNoPendingAsync();
        check("focus_reset_invalidates_accepted_approval_without_control_or_model_calls",
            !await runtime.TryRecordPendingApprovalAsync(focusedNotice) && runtime.Presented(focusedNotice.Id) == null
            && f.NoAdditionalExecution(await f.Host.ReadAsync()) && f.Host.Runtime.LastError == null);
    }

    private static async Task ExpiryAndCompletionAsync(string directory, Action<string, bool> check)
    {
        await using var f = await OwnedFixture.CreateAsync(directory);
        var runtime = f.Host.Runtime.OwnedRuntime!;
        // Exercise the real deadline against the same clock as the backend and host.
        var expiring = f.EmitApproval(904, DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(3));
        ConversationEvent? notice = null;
        await WaitAsync(async () =>
        {
            notice = (await f.Host.ReadAsync()).Events.SingleOrDefault(e => e.ExternalEventKey
                == OwnedControlRuntime.ApprovalNotificationKey("demo", expiring.Token));
            return notice != null;
        });
        await WaitAsync(() => Task.FromResult(DateTimeOffset.UtcNow >= expiring.ExpiresAt));
        await f.WaitNoPendingAsync();
        check("expired_persisted_marker_is_not_a_live_backend_approval",
            !runtime.TryValidateRequiredDecision(notice!, out _, out _)
            && !await runtime.TryBindPresentedApprovalAsync("demo", expiring.Token, notice!.Id)
            && !await runtime.TryRecordPendingApprovalAsync(notice) && runtime.Presented(notice.Id) == null
            && f.Host.Runtime.LastError == null);

        var completing = f.EmitApproval(905);
        var completionNotice = await f.AcceptedAsync(completing);
        f.Rpc.Emit("turn/completed", new { threadId = f.Thread,
            turn = OwnedHostRuntimeTests.FakeRpc.Turn(f.Turn, "completed") });
        await f.WaitNoPendingAsync();
        check("completed_turn_invalidates_approval_even_before_its_deadline",
            DateTimeOffset.UtcNow < completing.ExpiresAt && !runtime.TryValidateRequiredDecision(completionNotice, out _, out _)
            && runtime.Presented(completionNotice.Id) == null && f.NoAdditionalExecution(await f.Host.ReadAsync())
            && f.Host.Runtime.LastError == null);
    }

    private static async Task DisconnectionAndRestartAsync(string directory, Action<string, bool> check)
    {
        var f = await OwnedFixture.CreateAsync(directory);
        ClawBinding binding;
        ConversationEvent notice;
        try
        {
            var approval = f.EmitApproval(906);
            notice = await f.AcceptedAsync(approval);
            var runtime = f.Host.Runtime.OwnedRuntime!;
            await f.Rpc.DisposeAsync();
            await f.WaitNoPendingAsync();
            check("disconnected_loaded_session_cannot_represent_unexpired_request",
                DateTimeOffset.UtcNow < approval.ExpiresAt && !runtime.TryValidateRequiredDecision(notice, out _, out _)
                && runtime.Presented(notice.Id) == null
                && !await runtime.TryBindPresentedApprovalAsync("demo", approval.Token, notice.Id)
                && f.NoAdditionalExecution(await f.Host.ReadAsync()) && f.Host.Runtime.LastError == null);
            binding = f.Host.Settings.WeChat.ReadBinding()!;
        }
        finally { await f.DisposeAsync(); }
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(f.HostDirectory,
            modelEnabled: false, savedBinding: binding, ownedOptions: f.Options);
        var saved = await reopened.ReadAsync();
        var original = saved.Events.Single(e => e.Id == notice.Id);
        var restored = reopened.Runtime.OwnedRuntime!;
        check("restart_facts_do_not_connect_or_reconstruct_live_approval_authority",
            !restored.TryValidateRequiredDecision(original, out _, out _)
            && !await restored.TryBindPresentedApprovalAsync("demo", notice.RequiredDecision!.DecisionToken, notice.Id)
            && restored.Tasks().Count == 0 && f.Connections == 1 && reopened.Script.Calls == 0
            && (await reopened.Ledger.ReadConversationContextAsync(reopened.Runtime.Identity!)).PendingApproval == null
            && saved.Operations.Length == f.InitialOperations && f.Rpc.Responses == 0);
    }

    private static async Task LateAcceptedReceiptAsync(string directory, Action<string, bool> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage?> Intercept(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string body = json.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                    .GetProperty("text_item").GetProperty("text").GetString()!;
                if (body.Contains("这项自建任务正在等审批", StringComparison.Ordinal))
                { entered.TrySetResult(); await release.Task; }
            }
            return null;
        }
        await using var f = await OwnedFixture.CreateAsync(directory, Intercept);
        var approval = f.EmitApproval(907);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Resolve(907);
        }
        finally { release.TrySetResult(); }
        await WaitAsync(async () =>
        {
            var state = await f.Host.ReadAsync();
            var notice = state.Events.SingleOrDefault(e => e.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token));
            return notice != null && state.Outbox.Any(o => o.NotificationEventId == notice.Id && o.Stage == OutboxStage.Accepted);
        });
        await f.WaitNoPendingAsync();
        var saved = await f.Host.ReadAsync();
        var detail = saved.Events.Single(e => e.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token));
        check("late_real_acceptance_is_preserved_without_recreating_expired_authority",
            saved.Outbox.Where(o => o.NotificationEventId == detail.Id).All(o => o.Stage == OutboxStage.Accepted && o.SendAttempts == 1)
            && f.Host.Sent.Count(b => b.Contains(OwnedControlToolAdapter.ApprovalReference("demo", approval))) == 1
            && f.Host.Runtime.OwnedRuntime!.Presented(detail.Id) == null
            && !await f.Host.Runtime.OwnedRuntime.TryRecordPendingApprovalAsync(detail)
            && f.NoAdditionalExecution(saved) && f.Host.Runtime.LastError == null);
    }

    private static async Task ApprovalAfterInputWindowAsync(string directory, Action<string, bool> check)
    {
        const string barrierBody = "SYNTHETIC_REQUIRED_DECISION_WINDOW_BARRIER";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage?> Intercept(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string body = json.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                    .GetProperty("text_item").GetProperty("text").GetString()!;
                if (body == barrierBody) { entered.TrySetResult(); await release.Task; }
            }
            return null;
        }
        await using var f = await OwnedFixture.CreateAsync(directory, Intercept);
        await f.Host.Runtime.EnqueueNotificationAsync("required/window-barrier", barrierBody, null, default);
        ConversationEvent? detail = null;
        PendingApproval? approval = null;
        Guid windowId = Guid.Empty;
        bool pendingBeforeResolution = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first real sender is still on the wire, so both the current reply and the
            // subsequently created approval remain Pending rather than crossing their send gates.
            await f.Host.InputAsync("query-before-approval", "状态");
            var before = await f.Host.ReadAsync();
            var window = before.CatchUpWindows.Single(w => w.SourceMessageId == WeChatText.Hash("query-before-approval"));
            windowId = window.Id;
            approval = f.EmitApproval(908);
            await WaitAsync(async () =>
            {
                var state = await f.Host.ReadAsync();
                detail = state.Events.SingleOrDefault(e => e.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token));
                return detail != null && state.Outbox.Any(o => o.NotificationEventId == detail.Id);
            });
            var pending = await f.Host.ReadAsync();
            pendingBeforeResolution = detail!.RecordedAt >= window.CapturedAt
                && !window.RequiredDecisionEventIds.Contains(detail.Id)
                && pending.Outbox.Where(o => o.NotificationEventId == detail.Id)
                    .All(o => o.Stage == OutboxStage.Pending && o.SendAttempts == 0);
            f.Resolve(908);
        }
        finally { release.TrySetResult(); }
        await OwnedHostRuntimeTests.WaitReplyAsync(f.Host, "query-before-approval");
        var saved = await f.Host.ReadAsync();
        var activeWindow = saved.CatchUpWindows.Single(w => w.Id == windowId);
        check("new_pending_approval_after_window_resolved_does_not_block_current_query",
            pendingBeforeResolution && !activeWindow.VerifiedRequiredDecisionEventIds.Contains(detail!.Id)
            && saved.Outbox.Where(o => o.NotificationEventId == detail.Id)
                .All(o => o.Stage == OutboxStage.Pending && o.SendAttempts == 0)
            && !f.Host.Sent.Any(b => b.Contains(OwnedControlToolAdapter.ApprovalReference("demo", approval!)))
            && f.Host.Runtime.OwnedRuntime!.Presented(detail.Id) == null
            && f.NoAdditionalExecution(saved) && f.Host.Runtime.LastError == null);
    }

    private sealed class OwnedFixture : IAsyncDisposable
    {
        internal DurableRuntimeTests.Fixture Host = null!;
        internal OwnedHostRuntimeTests.FakeRpc Rpc = null!;
        internal OwnedHostOptions Options = null!;
        internal string HostDirectory = "", Thread = "", Turn = "", Cwd = "";
        internal TaskReference Target = null!;
        internal int Connections, InitialMethods, InitialModelCalls, InitialOperations;
        internal static async Task<OwnedFixture> CreateAsync(string directory,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? intercept = null)
        {
            var f = new OwnedFixture { HostDirectory = Path.Combine(directory, "host") };
            var profile = OwnedHostRuntimeTests.Profile(f.HostDirectory, verified: true);
            f.Options = new(Path.Combine(directory, "owned"), [profile], (configured, _) =>
            {
                f.Connections++;
                f.Rpc = new(configured.Configuration, async () => (await f.Host.ReadAsync()).Operations.Any(o => o.Stage == OperationStage.Dispatching));
                return Task.FromResult<IJsonRpcConnection>(f.Rpc);
            });
            f.Host = await DurableRuntimeTests.Fixture.CreateAsync(f.HostDirectory, ownedOptions: f.Options,
                channelRequestOverride: intercept);
            try
            {
                f.Host.Settings.ChatGpt.TaskControlEnabled = true;
                await OwnedHostRuntimeTests.SendToolAsync(f.Host, "create", "新建 demo",
                    _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
                var runtime = f.Host.Runtime.OwnedRuntime!;
                f.Thread = runtime.Tasks().Single().Handle.Identity.ThreadId;
                string text = "继续 " + f.Thread + " 检查合成审批真实性";
                await OwnedHostRuntimeTests.SendToolAsync(f.Host, "start", text,
                    _ => OwnedHostRuntimeTests.ContinueCall(runtime.Tasks().Single(), text));
                var task = runtime.Tasks().Single();
                f.Turn = task.Snapshot.CurrentTurnId!; f.Cwd = task.Snapshot.Cwd;
                f.Target = new(task.Handle.Identity.HostId, f.Thread, f.Turn, task.Handle.Identity.ProfileId);
                f.Rpc.Emit("turn/started", new { threadId = f.Thread, turn = OwnedHostRuntimeTests.FakeRpc.Turn(f.Turn, "inProgress") });
                await WaitAsync(() => Task.FromResult(runtime.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
                f.Host.Settings.ChatGpt.Enabled = false;
                f.InitialMethods = f.Rpc.Methods.Count; f.InitialModelCalls = f.Host.Script.Calls;
                f.InitialOperations = (await f.Host.ReadAsync()).Operations.Length;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal PendingApproval EmitApproval(int id, DateTimeOffset? started = null)
        {
            Rpc.Emit("item/commandExecution/requestApproval", new
            {
                threadId = Thread, turnId = Turn, itemId = "synthetic-decision-" + id,
                command = "echo SYNTHETIC_REQUIRED_DECISION", cwd = Cwd,
                startedAtMs = (started ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()
            }, id);
            return Host.Runtime.OwnedRuntime!.LoadedSession("demo")!.Client.PendingApprovals().Single(a => a.RequestId.GetInt32() == id);
        }
        internal void Resolve(int id) => Rpc.Emit("serverRequest/resolved", new { threadId = Thread, requestId = id });
        internal async Task<ConversationEvent> AcceptedAsync(PendingApproval approval)
        {
            ConversationEvent? found = null;
            await WaitAsync(async () =>
            {
                var state = await Host.ReadAsync();
                found = state.Events.SingleOrDefault(e => e.ExternalEventKey == OwnedControlRuntime.ApprovalNotificationKey("demo", approval.Token));
                return found != null && state.Outbox.Any(o => o.NotificationEventId == found.Id)
                    && state.Outbox.Where(o => o.NotificationEventId == found.Id).All(o => o.Stage == OutboxStage.Accepted)
                    && state.Context?.PendingApproval?.SourceEventId == found.Id;
            });
            return found!;
        }
        internal Task WaitNoPendingAsync() => WaitAsync(async () =>
            (await Host.Ledger.ReadConversationContextAsync(Host.Runtime.Identity!)).PendingApproval == null);
        internal bool NoAdditionalExecution(ConversationSnapshot state)
            => Rpc.Responses == 0 && Rpc.Methods.Count == InitialMethods && Host.Script.Calls == InitialModelCalls
                && state.Operations.Length == InitialOperations;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic required decision condition not reached.");
            await Task.Delay(20);
        }
    }
}
