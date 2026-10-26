using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

internal static class MemoryOutputRuntimeTests
{
    private const string Secret = "SYNTHETIC_REVOKED_MEMORY_BODY";
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-output-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Explicit output directory required.");
            await RunAsync(args[index + 1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }

    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output directory required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("Fresh directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output rejected.");
        if (Application.Current?.Dispatcher.CheckAccess() != true) throw new InvalidOperationException("Dispatcher required.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "memory-output-runtime-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, realAccountUsed = false, actualModelUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "generation"));
            var entry = f.Model.Memory.Add(Secret);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolModelRequest? captured = null;
            f.Script.Handler = async (request, _) =>
            {
                captured = request; entered.TrySetResult(); await release.Task;
                return ToolModelResponse.Reply(Secret);
            };
            try
            {
                await f.InputAsync("ask", "聊一下刚才的内容");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                f.Model.Memory.Delete(entry.Id);
                release.TrySetResult();
                await WaitAsync(async () => (await f.ReadAsync()).Generations.All(g => g.Stage != GenerationStage.Processing));
                var saved = await f.ReadAsync();
                Check("actual_runtime_model_receives_one_bound_generation_context",
                    captured?.GenerationContext?.Dependencies.Mode == OutputDependencyMode.Bound && saved.OutputDependenciesRequired);
                Check("deleted_memory_after_inference_cannot_be_prepared_or_sent",
                    !f.Sent.Any(body => body.Contains(Secret, StringComparison.Ordinal))
                    && !saved.Outbox.Any(body => body.Body.Contains(Secret, StringComparison.Ordinal))
                    && saved.Operations.IsEmpty);
                await f.InputAsync("status-after-delete", "状态");
                await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
                Check("independent_current_status_survives_memory_invalidation",
                    (await f.ReadAsync()).Outbox.Any(page => page.Stage == OutboxStage.Accepted)
                    && !f.Sent.Any(body => body.Contains(Secret, StringComparison.Ordinal)));
            }
            finally { release.TrySetResult(); }
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "prepared"), modelEnabled: false);
            var entry = f.Model.Memory.Add(Secret);
            var identity = f.Runtime.Identity!;
            await f.Runtime.StopSessionAsync();
            await f.Ledger.ReceiveAsync(identity, "prepared-input", "当前独立问题", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var dependencies = AssistantMemoryDependencies.ToOutputDependencies(f.Model.Memory.CaptureForGeneration(true, DateTimeOffset.UtcNow));
            var lease = await f.Ledger.BeginGenerationAsync(identity, ["prepared-input"], outputDependencies: dependencies);
            await f.Ledger.PrepareReplyAsync(lease, [Secret]);
            f.Model.Memory.Update(entry.Id, "SYNTHETIC_REPLACEMENT");
            await f.Runtime.ActivateAsync(f.Binding, default);
            await WaitAsync(async () => (await f.ReadAsync()).Generations.Single(g => g.Id == lease.Id).Stage == GenerationStage.Superseded);
            var saved = await f.ReadAsync();
            Check("prepared_recovery_supersedes_old_context_without_relabelling_it",
                saved.Generations.Single(g => g.Id == lease.Id).DependencyInvalidationReason != null
                && !saved.Outbox.Any(page => page.GenerationId == lease.Id)
                && !f.Sent.Contains(Secret));
            Check("prepared_recovery_keeps_original_inbox_and_memory_correction",
                saved.Inbox.Any(input => input.SourceMessageId == "prepared-input" && input.Body == "当前独立问题")
                && f.Model.Memory.Entries.Single().Text == "SYNTHETIC_REPLACEMENT");
        }

        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "actual-gate"),
                channelRequestOverride: async (request, ct) =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
                    string body = await WireBodyAsync(request, ct);
                    if (!body.Contains("synthetic memory gate primer", StringComparison.Ordinal)) return null;
                    entered.TrySetResult(); await release.Task;
                    return Response("""{"ret":0}""");
                });
            var entry = f.Model.Memory.Add(Secret);
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(Secret));
            Task<bool?>? primer = null;
            try
            {
                primer = f.Channel.SendAiTestAsync("synthetic memory gate primer", default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await f.InputAsync("gate-ask", "说说当前的想法");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page => page.Body == Secret && page.Stage == OutboxStage.Sending));
                f.Model.Memory.Delete(entry.Id);
                release.TrySetResult(); await primer;
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Single(page => page.Body == Secret).Stage != OutboxStage.Sending);
                await f.Ledger.RefreshOutputDependenciesAsync(f.Runtime.Identity!);
                var rejected = (await f.ReadAsync()).Outbox.Single(page => page.Body == Secret);
                Check("memory_change_inside_actual_gate_produces_known_zero_http_rejection",
                    rejected.Stage == OutboxStage.Superseded && rejected.DependencyInvalidationReason != null
                    && rejected.SendAttempts == 1 && rejected.RejectionReason == OutboxRejectionReason.BindingUnavailable
                    && !f.Sent.Contains(Secret));
                await f.InputAsync("gate-status", "状态");
                await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
                var saved = await f.ReadAsync();
                Check("known_unsent_stale_reply_is_persistently_retired_after_gate",
                    saved.Outbox.Single(page => page.Id == rejected.Id).Stage == OutboxStage.Superseded
                    && saved.Outbox.Single(page => page.Id == rejected.Id).DependencyInvalidationReason != null
                    && !f.Sent.Contains(Secret));
            }
            finally { release.TrySetResult(); if (primer != null) await primer; }
        }

        foreach (bool rulesQuery in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string primerBody = rulesQuery ? "synthetic disabled rules primer" : "synthetic disabled memory primer";
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(
                Path.Combine(directory, rulesQuery ? "disabled-rules-read" : "disabled-memory-read"), modelEnabled: false,
                channelRequestOverride: async (request, ct) =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
                    if (!(await WireBodyAsync(request, ct)).Contains(primerBody, StringComparison.Ordinal)) return null;
                    entered.TrySetResult(); await release.Task;
                    return Response("""{"ret":0}""");
                });
            var entry = f.Model.Memory.Add(Secret);
            if (rulesQuery) f.Model.Memory.SaveRules(Secret);
            f.Settings.ChatGpt.PersonalizationEnabled = false;
            Task<bool?>? primer = null;
            try
            {
                primer = f.Channel.SendAiTestAsync(primerBody, default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await f.InputAsync("explicit-read", rulesQuery ? "查看规则" : "查看记忆");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page =>
                    page.Body.Contains(Secret, StringComparison.Ordinal) && page.Stage == OutboxStage.Sending));
                if (rulesQuery) f.Model.Memory.SaveRules("SYNTHETIC_REPLACEMENT_RULES");
                else f.Model.Memory.Delete(entry.Id);
                release.TrySetResult(); await primer;
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page =>
                    page.Body.Contains(Secret, StringComparison.Ordinal) && page.Stage != OutboxStage.Sending));
                await f.Ledger.RefreshOutputDependenciesAsync(f.Runtime.Identity!);
                var saved = await f.ReadAsync();
                var page = saved.Outbox.Single(page => page.Body.Contains(Secret, StringComparison.Ordinal));
                Check(rulesQuery ? "disabled_personalization_rules_read_still_binds_actual_content"
                        : "disabled_personalization_memory_read_still_binds_actual_content",
                    page.Stage == OutboxStage.Superseded && page.DependencyInvalidationReason != null
                    && page.RejectionReason == OutboxRejectionReason.BindingUnavailable && page.SendAttempts == 1
                    && !f.Sent.Any(body => body.Contains(Secret, StringComparison.Ordinal)) && f.Script.Calls == 0
                    && DurableConversationLedger.ResolveOutputDependencies(saved, page).Contexts
                        .Any(item => item.Provider == "assistant-explicit-memory-read"));
            }
            finally { release.TrySetResult(); if (primer != null) await primer; }
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "before-dispatch"), modelEnabled: false);
            var entry = f.Model.Memory.Add(Secret);
            var received = await f.Ledger.ReceiveAsync(f.Runtime.Identity!, "before-dispatch-source",
                "删除记忆 " + entry.Id, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            bool observedQueue = false;
            f.Fault.ObserveCommitted = committed =>
            {
                if (!observedQueue && committed.GetProperty("operations").EnumerateArray().Any(operation =>
                        operation.GetProperty("sourceMessageId").GetString() == "before-dispatch-source"
                        && operation.GetProperty("stage").GetString() == nameof(OperationStage.Queued)))
                {
                    observedQueue = true;
                    f.Model.Memory.Update(entry.Id, "SYNTHETIC_REPLACEMENT_BEFORE_DISPATCH");
                }
            };
            try
            {
                var execute = typeof(DurableAssistantRuntime).GetMethod("ExecuteLocalFastAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var reply = await (Task<DurableFastReply>)execute.Invoke(f.Runtime,
                    [new DurableFastContext(received.Message, f.Ledger, () => true),
                        new ModelToolCall("before-dispatch-call", "forget", JsonSerializer.Serialize(new { memoryId = entry.Id })),
                        CancellationToken.None])!;
                var saved = await f.ReadAsync();
                Check("memory_change_before_dispatch_closes_exact_queued_operation_without_execution",
                    observedQueue && saved.Operations.Single().Stage == OperationStage.CancelledBeforeDispatch
                    && saved.ToolInvocations.Single().Stage == ToolInvocationStage.Unavailable
                    && f.Model.Memory.Entries.Single().Text == "SYNTHETIC_REPLACEMENT_BEFORE_DISPATCH"
                    && reply.Segments.Single().Contains("没有执行修改", StringComparison.Ordinal));
            }
            finally { f.Fault.ObserveCommitted = null; }
        }

        {
            DurableRuntimeTests.Fixture? current = null;
            string? memoryId = null;
            var actualWire = new ConcurrentQueue<string>();
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "segments"),
                channelRequestOverride: async (request, ct) =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/sendmessage", StringComparison.Ordinal)) return null;
                    var body = await WireBodyAsync(request, ct);
                    actualWire.Enqueue(body);
                    if (body.StartsWith(Secret, StringComparison.Ordinal))
                    {
                        current!.Model.Memory.Delete(memoryId!);
                        return Response("""{"ret":0}""");
                    }
                    return null;
                });
            current = f; memoryId = f.Model.Memory.Add(Secret).Id;
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(Secret + new string('甲', 1800) + "TAIL_SENTINEL"));
            await f.InputAsync("multi", "展开讲讲这个话题");
            await WaitAsync(async () =>
            {
                var rows = (await f.ReadAsync()).Outbox;
                return rows.Any(page => page.Stage == OutboxStage.Accepted)
                    && rows.Any(page => page.Stage == OutboxStage.Superseded);
            });
            var saved = await f.ReadAsync();
            var pages = saved.Outbox.OrderBy(page => page.SegmentIndex).ToArray();
            Check("first_segment_real_acceptance_survives_memory_change_during_http",
                pages[0].Stage == OutboxStage.Accepted && pages[0].SendAttempts == 1
                && actualWire.Count(body => body == pages[0].Body) == 1);
            Check("remaining_segments_recheck_memory_before_each_actual_send",
                pages.Length >= 3 && pages.Skip(1).All(page => page.Stage == OutboxStage.Superseded && page.SendAttempts == 0)
                && !actualWire.Any(body => body.Contains("TAIL_SENTINEL", StringComparison.Ordinal)));
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "unknown"));
            var entry = f.Model.Memory.Add(Secret);
            f.SendOutcome = "{}";
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply(Secret));
            await f.InputAsync("unknown-ask", "和我说点什么");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(page => page.Body == Secret && page.Stage == OutboxStage.Unknown));
            var original = (await f.ReadAsync()).Outbox.Single(page => page.Body == Secret);
            f.Model.Memory.Delete(entry.Id); f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("unknown-status", "状态");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            Check("memory_deletion_does_not_rewrite_or_repeat_unknown_delivery",
                (await f.ReadAsync()).Outbox.Single(page => page.Id == original.Id) == original
                && f.Sent.Count(body => body == Secret) == 1);
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "history"),
                ownedOptions: new OwnedHostOptions(Path.Combine(directory, "history-owned"), [],
                    (_, _) => throw new InvalidOperationException("Operation history must not connect to a task service.")));
            await f.InputAsync("remember-source", "记住：" + Secret);
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var entry = f.Model.Memory.Entries.Single();
            await f.InputAsync("remember-noop-source", "记住：" + Secret);
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("旧生成包含 " + Secret));
            await f.InputAsync("old-generated", "描述一下刚才的话题");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            await f.InputAsync("delete-source", "删除记忆 " + entry.Id);
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            ToolModelRequest? observed = null;
            f.Script.Handler = (request, _) => { observed = request; return Task.FromResult(ToolModelResponse.Reply("当前新消息已读取。")); };
            await f.InputAsync("same-text-new-source", Secret);
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var saved = await f.ReadAsync();
            Check("revoked_source_and_generated_history_are_absent_from_next_model_input",
                observed != null && !observed.Context.Any(item => item.SourceMessageId == WeChatText.Hash("remember-source"))
                && !observed.Context.Any(item => item.Source == ConversationSource.Assistant && item.Body.Contains(Secret, StringComparison.Ordinal))
                && !observed.Context.Any(item => item.Source == ConversationSource.ToolResult && item.Body.Contains(Secret, StringComparison.Ordinal)));
            Check("same_text_in_new_authenticated_input_is_not_keyword_filtered",
                observed?.CurrentMessage.Body == Secret
                && observed.Context.Any(item => item.SourceMessageId == WeChatText.Hash("same-text-new-source") && item.Body == Secret));
            Check("historical_noop_memory_source_is_filtered_by_its_invocation_without_text_matching",
                observed != null && !observed.Context.Any(item => item.SourceMessageId == WeChatText.Hash("remember-noop-source"))
                && saved.Inbox.Any(item => item.SourceMessageId == WeChatText.Hash("remember-noop-source")));
            Check("history_filter_keeps_audit_and_original_messages_on_disk",
                saved.Inbox.Any(item => item.SourceMessageId == WeChatText.Hash("remember-source") && item.Body.Contains(Secret, StringComparison.Ordinal))
                && saved.Events.Any(item => item.Source == ConversationSource.Assistant && item.Body.Contains(Secret, StringComparison.Ordinal))
                && f.Model.Memory.Entries.Count == 0);
            ToolModelRequest? receiptRequest = null;
            f.Script.Handler = (request, _) =>
            {
                if (request.Observations.IsEmpty)
                    return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("read-operation-history", "read_operations", "{}")));
                receiptRequest = request;
                return Task.FromResult(ToolModelResponse.Reply("之前的操作状态已核对。"));
            };
            await f.InputAsync("operation-history", "核对一下之前各次操作");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            Check("rereading_memory_operations_cannot_reintroduce_deleted_receipt_text",
                receiptRequest?.Observations.Length == 1
                && !receiptRequest.Observations.Single().ResultJson.Contains(Secret, StringComparison.Ordinal)
                && receiptRequest.Observations.Single().ResultJson.Contains("detailsOmitted", StringComparison.Ordinal)
                && saved.Operations.Where(item => item.Action is "remember" or "forget")
                    .All(item => receiptRequest.Observations.Single().ResultJson.Contains(item.Id.ToString(), StringComparison.Ordinal))
                && saved.Operations.Any(item => item.Receipt?.Contains(Secret, StringComparison.Ordinal) == true));
        }

        {
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "tool-transition"));
            f.Settings.ChatGpt.AutoMemoryEnabled = true;
            var requests = new ConcurrentQueue<ToolModelRequest>();
            f.Script.Handler = (request, _) =>
            {
                requests.Enqueue(request);
                if (requests.Count == 1)
                    return Task.FromResult(ToolModelResponse.Call(
                        new("save-preference", "learn_preference", """{"preferences":[{"key":"report_layout","evidence":"我更喜欢分行"}]}"""),
                        new("stale-tail", "list_tasks", "{}")));
                return Task.FromResult(ToolModelResponse.Reply("记住了，之后会分行说。"));
            };
            await f.InputAsync("prefer", "我更喜欢分行");
            await WaitAsync(async () => HasCurrentReply(await f.ReadAsync()));
            var saved = await f.ReadAsync();
            var frames = requests.ToArray();
            Check("legitimate_memory_tool_advances_exact_generation_epoch",
                frames.Length == 2 && frames[0].GenerationContext is { Epoch: 0 } firstContext
                && frames[1].GenerationContext is { Epoch: 1 } secondContext
                && firstContext.GenerationId == secondContext.GenerationId
                && f.Model.Memory.Entries.Single().Text == "我更喜欢分行");
            Check("old_model_response_tail_is_not_executed_after_context_transition",
                saved.ToolInvocations.Length == 1 && saved.ToolInvocations.Single().ToolName == "learn_preference"
                && saved.Operations.Single().Stage == OperationStage.Completed);
            Check("post_write_observation_keeps_success_and_ids_without_old_prose",
                frames[1].Observations.Single().ResultJson.Contains("completed", StringComparison.Ordinal)
                && frames[1].Observations.Single().ResultJson.Contains(f.Model.Memory.Entries.Single().Id, StringComparison.Ordinal)
                && !frames[1].Observations.Single().ResultJson.Contains("我更喜欢分行", StringComparison.Ordinal));
            Check("final_reply_depends_on_actual_after_write_memory_snapshot",
                saved.Outbox.Single().Stage == OutboxStage.Accepted
                && OutputDependencies.Equivalent(DurableConversationLedger.ResolveOutputDependencies(saved, saved.Outbox.Single()),
                    AssistantMemoryDependencies.ToOutputDependencies(f.Model.Memory.CaptureForGeneration(true, DateTimeOffset.UtcNow))));
        }
    }

    private static bool HasCurrentReply(ConversationSnapshot saved) => saved.Outbox.Any(page =>
        page.Kind == OutboxKind.ConversationReply && page.Revision == saved.Revision && page.Stage == OutboxStage.Accepted);
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic memory/output condition not reached.");
            await Task.Delay(20);
        }
    }
    private static async Task<string> WireBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        return payload.RootElement.GetProperty("msg").GetProperty("item_list")[0]
            .GetProperty("text_item").GetProperty("text").GetString()!;
    }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
