using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

// Actual runtime/queue/channel with explicit synthetic model, HTTP and RPC only.
internal static class DurableInputBurstRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--durable-input-burst-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit synthetic directory required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("New absolute test directory required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "durable-input-burst-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(x => x), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual durable runtime with production grouping predicate and fake owned RPC"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await BurstAsync(Path.Combine(directory, "burst"), Check);
        await SeparateInputsAsync(Path.Combine(directory, "separate"), Check);
        await NaturalHeldAsync(Path.Combine(directory, "held-target"), false, Check);
        await NaturalHeldAsync(Path.Combine(directory, "held-constraints"), true, Check);
        await VagueCancellationAsync(Path.Combine(directory, "vague-cancellation"), Check);
        await StopBypassesModelAsync(Path.Combine(directory, "stop"), Check);
        CheckMergeSettings(Check);
        await ConfiguredMergeAsync(Path.Combine(directory, "configured-merge"), Check);
        await DisabledMergeAsync(Path.Combine(directory, "disabled-merge"), Check);
        await LiveMergeSettingsAsync(Path.Combine(directory, "live-merge"), Check);
    }
    private static void CheckMergeSettings(Action<string, bool> check)
    {
        var missing = JsonSerializer.Deserialize<Settings>("{}", Settings.Json)!;
        missing.Normalize();
        check("missing_merge_setting_preserves_one_second", missing.InputMergeWindowMilliseconds == 1000);
        foreach (int value in new[] { 0, 500, 1000, 2000, 3000 })
        {
            var original = new Settings { InputMergeWindowMilliseconds = value, LocalEnabled = false };
            original.Normalize();
            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(original, Settings.Json), Settings.Json)!;
            restored.Normalize();
            check("merge_setting_roundtrip_" + value, restored.InputMergeWindowMilliseconds == value);
        }
        foreach (int value in new[] { -1, 3001, int.MaxValue })
        {
            var invalid = new Settings { InputMergeWindowMilliseconds = value };
            bool refused = false;
            try { invalid.Normalize(); } catch (InvalidDataException) { refused = true; }
            check("invalid_merge_setting_rejected_" + value, refused && invalid.InputMergeWindowMilliseconds == value);
        }
    }
    private static async Task ConfiguredMergeAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, productionMergeWindowMilliseconds: 3000);
        var requests = new ConcurrentQueue<ToolModelRequest>();
        f.Script.Handler = (request, _) =>
        { requests.Enqueue(request); return Task.FromResult(ToolModelResponse.Reply("两条原话都保留了，只解释。")); };
        await f.InputAsync("configured-first", "请解释 Alpha 的测试结果");
        await Task.Delay(1150);
        await f.InputAsync("configured-addition", "补充：只解释，不修改代码");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "configured-addition");
        await f.Runtime.StopSessionAsync();
        var state = await f.Ledger.ReadConversationAsync(DurableAssistantRuntime.BindingIdentity(f.Binding, f.Settings.ChatGpt.ActiveAccount));
        var inputs = state.Inbox.OrderBy(x => x.Revision).ToArray();
        var delta = inputs[1].ReceivedAt - inputs[0].ReceivedAt;
        check("configured_three_second_window_reaches_actual_production_grouping",
            delta > TimeSpan.FromSeconds(1) && delta < TimeSpan.FromSeconds(3)
            && requests.Count == 1 && state.Generations.Single(x => x.Stage == GenerationStage.HandedToOutbox).InboxIds.Length == 2);
        check("configured_group_keeps_original_authority_and_constraints",
            requests.Single().CurrentMessage.SourceMessageId == WeChatText.Hash("configured-addition")
            && requests.Single().Context.Where(x => inputs.Any(input => input.Body == x.Body)).Select(x => x.Body)
                .SequenceEqual(inputs.Select(x => x.Body))
            && state.Operations.IsEmpty && f.Queue.Entries.Count == 0 && f.Sent.Count == 1);
    }
    private static async Task DisabledMergeAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, productionMergeWindowMilliseconds: 0);
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("保留这条问题，只解释。"));
        await f.InputAsync("disabled-first", "请解释 Alpha 的测试结果");
        await f.InputAsync("disabled-addition", "补充：只解释，不修改代码");
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(x => x.Stage == OutboxStage.Accepted) == 2);
        await f.Runtime.StopSessionAsync();
        var state = await f.Ledger.ReadConversationAsync(DurableAssistantRuntime.BindingIdentity(f.Binding, f.Settings.ChatGpt.ActiveAccount));
        check("zero_window_does_not_group_even_explicit_rapid_additions",
            state.Inbox.Length == 2 && state.Generations.Where(x => x.Stage == GenerationStage.HandedToOutbox)
                .All(x => x.InboxIds.Length == 1) && f.Sent.Count == 2);
        check("zero_window_preserves_both_questions_and_creates_no_write",
            state.Inbox.Select(x => x.SourceMessageId).Order()
                .SequenceEqual(new[] { WeChatText.Hash("disabled-first"), WeChatText.Hash("disabled-addition") }.Order())
            && state.Operations.IsEmpty && f.Queue.Entries.Count == 0);
    }
    private static async Task LiveMergeSettingsAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, productionMergeWindowMilliseconds: 0);
        var entered = new TaskCompletionSource<ToolModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredEntered = new TaskCompletionSource<ToolModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<ToolModelRequest>();
        var release = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Script.Handler = (request, _) =>
        {
            requests.Enqueue(request);
            if (request.DeferredWork != null)
            {
                deferredEntered.TrySetResult(request);
                return Task.FromResult(ToolModelResponse.Reply("按当前限制重新解释先前的问题。"));
            }
            entered.TrySetResult(request); return release.Task;
        };
        try
        {
        await f.InputAsync("live-model", "只解释 Alpha 的测试结果");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Runtime.ApplyInputMergeWindow(3000);
        var originalIdentity = f.Runtime.Identity;
        await f.InputAsync("live-status", "状态");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "live-status");
        await deferredEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observed = await f.ReadAsync();
        File.WriteAllText(Path.Combine(directory, "live-query-evidence.json"), JsonSerializer.Serialize(new
        {
            originalIdentity, currentIdentity = f.Runtime.Identity, releaseCompleted = release.Task.IsCompleted,
            calls = f.Script.Calls,
            requests = requests.Select(x => new { x.CurrentMessage.SourceMessageId, deferred = x.DeferredWork != null }),
            inbox = observed.Inbox.Select(x => new { x.SourceMessageId, x.Stage, x.Revision }),
            generations = observed.Generations.Select(x => new { x.Id, x.Stage, x.InboxIds, x.Revision }),
            work = observed.InputWork.Select(x => new { x.SourceMessageId, x.Stage }),
            outbox = observed.Outbox.Select(x => new { x.Id, x.GenerationId, x.Stage })
        }, Settings.Json));
        check("changing_merge_window_keeps_session_and_fast_query_bypasses_blocked_model",
            f.Runtime.Identity == originalIdentity && !release.Task.IsCompleted
            && requests.Count(x => x.DeferredWork == null) == 1
            && requests.All(x => x.CurrentMessage.SourceMessageId == WeChatText.Hash("live-model"))
            && observed.Inbox.Single(x => x.SourceMessageId == WeChatText.Hash("live-status")).Stage == InboxStage.HandedToOutbox);
        var reconsidered = await deferredEntered.Task;
        check("concurrent_reconsideration_is_original_readonly_work_under_new_query",
            requests.Count == 2 && reconsidered.DeferredWork!.SourceMessageId == WeChatText.Hash("live-model")
            && reconsidered.DeferredWork.ReadAtRevision == observed.Revision
            && reconsidered.LatestInput?.SourceMessageId == WeChatText.Hash("live-status")
            && observed.Authorizations.IsEmpty && observed.Operations.IsEmpty);
        // A fresh read-only reconsideration is a different generation and may
        // answer the older question. Only the original superseded output is banned.
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("按当前限制重新解释先前的问题。"));
        release.TrySetResult(ToolModelResponse.Reply("OBSOLETE_MERGE_SETTING_REPLY"));
        await WaitAsync(async () => (await f.ReadAsync()).InputWork.Any(x =>
            x.SourceMessageId == WeChatText.Hash("live-model") && x.Stage == InputWorkStage.Completed));
        check("new_query_still_invalidates_old_generation_after_setting_change",
            !f.Sent.Contains("OBSOLETE_MERGE_SETTING_REPLY"));
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("新设置下的回复。"));
        f.Runtime.ApplyInputMergeWindow(0);
        await f.InputAsync("live-next", "另一个独立问题");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "live-next");
        check("live_zero_setting_applies_without_reopening_or_writing_control",
            f.Runtime.Identity == originalIdentity && f.Sent.Last() == "新设置下的回复。"
            && (await f.ReadAsync()).Operations.IsEmpty && f.Queue.Entries.Count == 0);
        bool refused = false;
        try { f.Runtime.ApplyInputMergeWindow(3001); } catch (ArgumentOutOfRangeException) { refused = true; }
        check("invalid_live_setting_cannot_replace_valid_window", refused);
        }
        finally
        {
            // Even a failed assertion must release the deliberately non-cooperative
            // fake before fixture disposal waits for the actual call to drain.
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("隔离测试清理。"));
            release.TrySetResult(ToolModelResponse.Reply("OBSOLETE_MERGE_SETTING_REPLY"));
        }
    }
    private static DurableMessageOptions ProductionGrouping() => new()
    {
        MergeWindow = TimeSpan.FromSeconds(1), CanGroup = DurableAssistantRuntime.CanGroup,
        DeliveryTimeout = TimeSpan.FromSeconds(3)
    };
    private static async Task BurstAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, coordinatorOptions: ProductionGrouping());
        var requests = new ConcurrentQueue<ToolModelRequest>();
        f.Script.Handler = (request, _) =>
        { requests.Enqueue(request); return Task.FromResult(ToolModelResponse.Reply("这三条要求已保留，这里只解释，不修改任务。")); };
        string[] ids = ["burst-base", "burst-limit", "burst-extra"];
        string[] bodies = ["请解释 Alpha 的测试结果", "补充：只分析，不修改代码", "而且把尚未验证的地方说清楚"];
        var occurred = new List<DateTimeOffset>();
        for (int i = 0; i < ids.Length; i++) occurred.Add(await InputAtAsync(f, ids[i], bodies[i]));
        await OwnedHostRuntimeTests.WaitReplyAsync(f, ids[^1]);
        await f.Runtime.StopSessionAsync();
        var state = await f.Ledger.ReadConversationAsync(DurableAssistantRuntime.BindingIdentity(f.Binding, f.Settings.ChatGpt.ActiveAccount));
        var generation = state.Generations.Single(x => x.Stage == GenerationStage.HandedToOutbox);
        var inbox = state.Inbox.OrderBy(x => x.Revision).ToArray();
        check("production_addition_burst_has_one_generation_and_all_source_ids", requests.Count == 1
            && generation.InboxIds.Order().SequenceEqual(inbox.Select(x => x.Id).Order()) && generation.InboxIds.Length == 3);
        check("grouping_preserves_each_original_body_time_and_message_identity", inbox.Select(x => x.Body).SequenceEqual(bodies)
            && inbox.Select(x => x.OccurredAt).SequenceEqual(occurred)
            && inbox.Select(x => x.SourceMessageId).SequenceEqual(ids.Select(WeChatText.Hash)));
        var request = requests.Single();
        check("actual_model_sees_ordered_constraints_without_synthetic_merged_authority", request.CurrentMessage.Body == bodies[^1]
            && request.Context.Where(x => bodies.Contains(x.Body)).Select(x => x.Body).SequenceEqual(bodies)
            && state.Operations.IsEmpty && f.Queue.Entries.Count == 0);
        check("addition_burst_sends_one_natural_reply", f.Sent.Count == 1 && state.Outbox.Single().Stage == OutboxStage.Accepted);
    }
    private static async Task SeparateInputsAsync(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, coordinatorOptions: ProductionGrouping());
        var requests = new ConcurrentQueue<ToolModelRequest>();
        f.Script.Handler = (request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult(ToolModelResponse.Reply(request.DeferredWork == null ? "这是后一个话题的答复。" : "这是先前未回答的问题。"));
        };
        await f.InputAsync("separate-a", "请解释 Alpha 的测试结果");
        await f.InputAsync("separate-b", "今天想聊一会儿日常生活");
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(x => x.Stage == OutboxStage.Accepted) == 2);
        await f.Runtime.StopSessionAsync();
        var identity = DurableAssistantRuntime.BindingIdentity(f.Binding, f.Settings.ChatGpt.ActiveAccount);
        var state = await f.Ledger.ReadConversationAsync(identity);
        check("production_different_topics_are_not_grouped", requests.Count == 2
            && state.Generations.Where(x => x.Stage == GenerationStage.HandedToOutbox).All(x => x.InboxIds.Length == 1)
            && requests.Count(x => x.DeferredWork == null) == 1 && requests.Count(x => x.DeferredWork != null) == 1);
        check("old_input_is_processed_under_its_readonly_work_identity", requests.Single(x => x.DeferredWork != null).CurrentMessage.SourceMessageId
                == WeChatText.Hash("separate-a")
            && state.InputWork.Single().SourceMessageId == WeChatText.Hash("separate-a")
            && state.Operations.IsEmpty && f.Queue.Entries.Count == 0);
        check("latest_reply_precedes_the_deferred_original_reply", f.Sent.ToArray().SequenceEqual(new[]
            { "这是后一个话题的答复。", "这是先前未回答的问题。" }));
    }
    private static async Task NaturalHeldAsync(string directory, bool constraints, Action<string, bool> check)
    {
        var transport = new NoWrites();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, taskTransport: transport);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        f.ObservedTask.Status = TaskStatusNames.Active;
        await f.InputAsync("queued-source", "让 Alpha 修改代码");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "queued-source");
        await f.Queue.SubmitDurableAsync(f.ObservedTask, "让 Alpha 修改代码", "synthetic-held-operation",
            WeChatText.Hash("queued-source"), f.CurrentSnapshot(), default);
        var queued = f.Queue.Entries.Single();
        f.Queue.Notify = (receipt, ct) => Application.Current.Dispatcher.InvokeAsync(
            () => f.Runtime.EnqueueControlReceiptAsync(receipt, ct)).Task.Unwrap();
        if (constraints)
        {
            await f.InputAsync("new-constraints", "不要修改代码，只解释结果");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, "new-constraints");
            var held = f.Queue.Entries.Single();
            // This entry point is also used by host queue callbacks. The state
            // comes from the actual authenticated-input fence, not a test setter.
            await f.Runtime.EnqueueControlReceiptAsync(f.Queue.CaptureReceipt(held.Id)
                ?? throw new InvalidOperationException("Held receipt was not captured."), default);
        }
        else
        {
            f.ObservedTask.TurnId = "synthetic-different-turn";
            f.ObservedTask.Status = TaskStatusNames.Completed;
            await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
        }
        await WaitAsync(async () =>
        {
            var state = await f.ReadAsync();
            var notice = state.Events.SingleOrDefault(x => x.ExternalEventKey == "control/" + queued.Id);
            return notice != null && state.Outbox.Any(x => x.NotificationEventId == notice.Id && x.Stage == OutboxStage.Accepted);
        });
        var snapshot = await f.ReadAsync();
        var entry = f.Queue.Entries.Single();
        var detail = snapshot.Events.Single(x => x.ExternalEventKey == "control/" + queued.Id);
        string kind = constraints ? "constraint" : "target";
        check(kind + "_hold_receipt_describes_not_dispatched_without_fixed_wrapper", entry.State == (constraints ? "held_constraints_changed" : "held_target_changed")
            && detail.Body.Contains("还没投递", StringComparison.Ordinal)
            && !detail.Body.Contains("任务接续", StringComparison.Ordinal) && !detail.Body.Contains("接续状态", StringComparison.Ordinal)
            && !detail.Body.Contains("held_", StringComparison.Ordinal) && !detail.Body.Contains("指令编号", StringComparison.Ordinal));
        check(kind + "_receipt_retains_original_task_and_never_dispatches", transport.Calls == 0 && entry.Instruction == "让 Alpha 修改代码"
            && detail.Task?.HostId == queued.Target!.HostId && detail.Task.ThreadId == queued.Target.ThreadId
            && detail.Task.TurnId == queued.Target.TurnId && snapshot.Operations.IsEmpty);
    }
    private static async Task VagueCancellationAsync(string directory, Action<string, bool> check)
    {
        var transport = new NoWrites();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, modelEnabled: false, taskTransport: transport);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await f.InputAsync("cancel-no-target", "取消刚才那条");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-no-target");
        check("vague_cancel_without_queue_is_local_clarification_without_operation", CancellationClarification(f.Sent.Last())
            && f.Script.Calls == 0 && (await f.ReadAsync()).Operations.IsEmpty && f.Queue.Entries.Count == 0);
        await QueueAsync("only-pending", "synthetic-only-operation");
        var single = f.Queue.Entries.Single();
        await f.InputAsync("cancel-one-target", "取消刚才那条");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-one-target");
        var one = await f.ReadAsync();
        check("vague_cancel_never_infers_even_a_single_pending_queue_target", CancellationClarification(f.Sent.Last())
            && TaskControlQueue.IsPending(f.Queue.Entries.Single()) && f.Queue.Entries.Single().Id == single.Id
            && one.Operations.IsEmpty && one.ToolInvocations.IsEmpty && f.Script.Calls == 0 && transport.Calls == 0);
        await f.InputAsync("cancel-exact-id", "取消 " + single.Id);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-exact-id");
        var exact = await f.ReadAsync();
        check("complete_queue_id_still_requires_and_uses_current_durable_authority", f.Queue.Entries.Single().State == "cancelled"
            && exact.Operations.Single().Action == "cancel_queued" && exact.Operations.Single().Stage == OperationStage.Completed
            && exact.Operations.Single().SourceMessageId == WeChatText.Hash("cancel-exact-id")
            && exact.Operations.Single().TargetKey == "queue/" + single.Id && f.Script.Calls == 0 && transport.Calls == 0);
        await QueueAsync("pending-first", "synthetic-first-operation");
        await QueueAsync("pending-second", "synthetic-second-operation");
        var pendingIds = f.Queue.Entries.Where(TaskControlQueue.IsPending).Select(x => x.Id).Order().ToArray();
        await f.InputAsync("cancel-many-targets", "取消刚才那条");
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "cancel-many-targets");
        check("vague_cancel_keeps_all_ambiguous_pending_targets", pendingIds.Length == 2 && CancellationClarification(f.Sent.Last())
            && f.Queue.Entries.Where(TaskControlQueue.IsPending).Select(x => x.Id).Order().SequenceEqual(pendingIds)
            && (await f.ReadAsync()).Operations.Length == 1 && f.Script.Calls == 0 && transport.Calls == 0);
        string[] unsafeInputs = ["引用：“取消刚才那条”", "不要取消刚才那条", "取消刚才那条，然后中断 Alpha"];
        for (int i = 0; i < unsafeInputs.Length; i++)
        {
            string source = "cancel-nonexact-" + i;
            await f.InputAsync(source, unsafeInputs[i]);
            await OwnedHostRuntimeTests.WaitReplyAsync(f, source);
            check("nonexact_cancel_is_not_the_literal_shortcut_" + i, !CancellationClarification(f.Sent.Last())
                && (await f.ReadAsync()).Operations.Length == 1 && f.Script.Calls == 0 && transport.Calls == 0
                && f.Queue.Entries.Where(TaskControlQueue.IsPending).Select(x => x.Id).Order().SequenceEqual(pendingIds));
        }
        async Task QueueAsync(string source, string operation)
        {
            await f.InputAsync(source, "让 Alpha 修改代码");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, source);
            await f.Queue.SubmitDurableAsync(f.ObservedTask, "让 Alpha 修改代码", operation,
                WeChatText.Hash(source), f.CurrentSnapshot(), default);
        }
    }
    private static bool CancellationClarification(string body) => body.Contains("请明确要取消哪一项", StringComparison.Ordinal)
        && body.Contains("取消 编号", StringComparison.Ordinal) && !body.Contains("已取消", StringComparison.Ordinal);
    private static async Task StopBypassesModelAsync(string directory, Action<string, bool> check)
    {
        RecordingRpc? wire = null;
        var profile = OwnedHostRuntimeTests.Profile(directory, verified: true);
        var options = new OwnedHostOptions(Path.Combine(directory, "owned"), [profile], (configured, _) =>
        {
            wire = new(new OwnedHostRuntimeTests.FakeRpc(configured.Configuration, () => Task.FromResult(true)));
            return Task.FromResult<IJsonRpcConnection>(wire);
        });
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, ownedOptions: options);
        f.Settings.ChatGpt.TaskControlEnabled = true;
        await OwnedHostRuntimeTests.SendToolAsync(f, "owned-create", "新建 demo", _ => OwnedHostRuntimeTests.Call("create_task", new { configurationId = "demo" }));
        var owned = f.Runtime.OwnedRuntime!;
        var task = owned.Tasks().Single();
        string continueText = "继续 " + task.Handle.Identity.ThreadId + " 检查测试";
        await OwnedHostRuntimeTests.SendToolAsync(f, "owned-run", continueText, _ => OwnedHostRuntimeTests.ContinueCall(task, continueText));
        task = owned.Tasks().Single();
        string turn = task.Snapshot.CurrentTurnId!;
        wire!.Inner.Emit("turn/started", new { threadId = task.Handle.Identity.ThreadId, turn = OwnedHostRuntimeTests.FakeRpc.Turn(turn, "inProgress") });
        await WaitAsync(() => Task.FromResult(owned.Tasks().Single().Snapshot.State == OwnedTaskState.Running));
        await f.InputAsync("owned-focus", "切换到 demo/" + task.Handle.Identity.ThreadId);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "owned-focus");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelSources = new ConcurrentQueue<string>();
        f.Script.Handler = (request, _) => { modelSources.Enqueue(request.CurrentMessage.SourceMessageId); entered.TrySetResult(); return release.Task; };
        var identity = f.Runtime.Identity!;
        try
        {
            await f.InputAsync("slow-answer", "帮我详细解释测试思路");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await f.InputAsync("priority-vague-cancel", "取消刚才那条");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, "priority-vague-cancel");
            var clarified = await f.ReadAsync();
            check("vague_cancel_bypasses_blocked_model_without_interrupting_running_focus", !release.Task.IsCompleted
                && CancellationClarification(f.Sent.Last()) && wire.Interrupts.IsEmpty
                && owned.Tasks().Single().Snapshot.State == OwnedTaskState.Running
                && clarified.Operations.Length == 2
                && !clarified.ToolInvocations.Any(x => x.SourceMessageId == WeChatText.Hash("priority-vague-cancel"))
                && !modelSources.Contains(WeChatText.Hash("priority-vague-cancel")));
            await f.InputAsync("priority-stop", "先停一下");
            await OwnedHostRuntimeTests.WaitReplyAsync(f, "priority-stop");
            var snapshot = await f.ReadAsync();
            var operation = snapshot.Operations.Single(x => x.Action == "interrupt_turn");
            check("focused_stop_dispatches_before_noncooperative_model_finishes", !release.Task.IsCompleted
                && wire.Interrupts.Count == 1 && operation.Stage == OperationStage.Accepted);
            var interrupt = wire.Interrupts.Single();
            check("priority_stop_binds_exact_persisted_focus_thread_and_turn", interrupt.GetProperty("threadId").GetString() == task.Handle.Identity.ThreadId
                && interrupt.GetProperty("turnId").GetString() == turn && operation.Task?.ThreadId == task.Handle.Identity.ThreadId
                && operation.Task.TurnId == turn && snapshot.Context!.Focus!.Task.ThreadId == task.Handle.Identity.ThreadId);
            check("stop_receipt_reports_acceptance_without_claiming_completed_interrupt", f.Sent.Last().Contains("中断请求已受理", StringComparison.Ordinal)
                && f.Sent.Last().Contains("等待轮次状态确认", StringComparison.Ordinal)
                && owned.Tasks().Single().Snapshot.State == OwnedTaskState.Running);
        }
        finally
        {
            release.TrySetResult(ToolModelResponse.Reply("OBSOLETE_SLOW_MODEL_REPLY_MUST_NOT_SEND"));
            await f.Runtime.StopSessionAsync();
        }
        var final = await f.Ledger.ReadConversationAsync(identity);
        check("late_obsolete_model_result_cannot_send_after_stop_request", !f.Sent.Contains("OBSOLETE_SLOW_MODEL_REPLY_MUST_NOT_SEND")
            && final.Operations.Count(x => x.Action == "interrupt_turn") == 1 && wire.Interrupts.Count == 1);
    }
    private static async Task<DateTimeOffset> InputAtAsync(DurableRuntimeTests.Fixture f, string id, string body)
    {
        using var batch = JsonDocument.Parse(f.Batch(id, body));
        var occurred = DateTimeOffset.FromUnixTimeMilliseconds(batch.RootElement.GetProperty("msgs")[0].GetProperty("create_time_ms").GetInt64());
        await f.Channel.ProcessUpdatesAsync(batch.RootElement.Clone(), default);
        return occurred;
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 400; i++) { if (await condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Synthetic durable input runtime condition was not reached.");
    }
    private sealed class NoWrites : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string id, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Held work must never reach transport."); }
    }
    private sealed class RecordingRpc(OwnedHostRuntimeTests.FakeRpc inner) : IJsonRpcConnection
    {
        internal OwnedHostRuntimeTests.FakeRpc Inner => inner;
        internal ConcurrentQueue<JsonElement> Interrupts { get; } = new();
        public ConnectionIdentity Identity => inner.Identity;
        public bool IsConnected => inner.IsConnected;
        public event Action<RpcIncoming>? Incoming { add => inner.Incoming += value; remove => inner.Incoming -= value; }
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            if (method == "turn/interrupt") Interrupts.Enqueue(JsonSerializer.SerializeToElement(parameters));
            return inner.RequestAsync(method, parameters, ct);
        }
        public Task NotifyAsync(string method, object parameters, CancellationToken ct) => inner.NotifyAsync(method, parameters, ct);
        public Task RespondAsync(JsonElement id, object result, CancellationToken ct) => inner.RespondAsync(id, result, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
