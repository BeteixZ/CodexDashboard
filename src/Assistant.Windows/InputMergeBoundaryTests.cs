using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Actual runtime + persistent ledger + synthetic model/channel only. The reply is
// held at model entry so the grouping assertion observes the original generation.
internal static class InputMergeBoundaryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--input-merge-boundary-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--input-merge-boundary-test") throw new ArgumentException("new_output_required");
            string directory = args[1];
            if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
                throw new ArgumentException("new_absolute_output_required");
            for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
            Directory.CreateDirectory(directory);
            var checks = new Dictionary<string, bool>();
            void Check(string name, bool value)
            {
                checks.Add(name, value);
                File.WriteAllText(Path.Combine(directory, "input-merge-boundary-checks.json"), JsonSerializer.Serialize(new
                { count = checks.Count, failures = checks.Count(x => !x.Value), productionDataRead = false,
                    actualNetworkRequests = 0, actualModelUsed = false, checks }, Settings.Json));
                if (!value) throw new InvalidOperationException(name);
            }
            await RunAsync(directory, Check); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory, Action<string, bool> check)
    {
        await AdjacentTopicCounterexampleAsync(Path.Combine(directory, "adjacent-topics"), check);
        await TimeBoundaryAsync(Path.Combine(directory, "anchored-window"), false, check);
        await TimeBoundaryAsync(Path.Combine(directory, "clock-backward"), true, check);
    }

    private static async Task AdjacentTopicCounterexampleAsync(string directory, Action<string, bool> check)
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, clock: clock,
            productionMergeWindowMilliseconds: 3000);
        var entered = new TaskCompletionSource<ToolModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<ToolModelRequest>();
        f.Script.Handler = (request, _) =>
        {
            requests.Enqueue(request);
            if (request.DeferredWork != null) return Task.FromResult(ToolModelResponse.Reply("这是先前独立话题的只读回复。"));
            entered.TrySetResult(request); return release.Task;
        };
        string[] sources = ["adjacent-topic-a", "adjacent-topic-b", "adjacent-topic-c"];
        string[] bodies = ["今天想聊一会儿日常生活", "请解释几何证明的思路", "补充：这个证明只分析，不修改任何文件"];
        try
        {
            for (int i = 0; i < sources.Length; i++) await f.InputAsync(sources[i], bodies[i]);
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var beforeReply = await f.ReadAsync();
            var inputs = beforeReply.Inbox.OrderBy(x => x.Revision).ToArray();
            var current = inputs.Single(x => x.SourceMessageId == WeChatText.Hash(sources[2]));
            var firstQuote = Guid.NewGuid();
            var secondQuote = Guid.NewGuid();
            check("different_resolved_quotes_are_not_merged", !DurableAssistantRuntime.CanGroup(
                inputs[1] with { ReferencedEventId = firstQuote }, current with { ReferencedEventId = secondQuote }));
            check("unquoted_addition_cannot_strip_quoted_context", !DurableAssistantRuntime.CanGroup(
                inputs[1] with { ReferencedEventId = firstQuote }, current));
            check("same_quote_explicit_addition_can_merge", DurableAssistantRuntime.CanGroup(
                inputs[1] with { ReferencedEventId = firstQuote }, current with { ReferencedEventId = firstQuote }));
            var generation = beforeReply.Generations.Single(x => x.Id == current.GenerationId);
            var expected = inputs.Where(x => x.SourceMessageId != WeChatText.Hash(sources[0])).Select(x => x.Id).ToArray();
            // Save facts before the decisive assertion, including on the old
            // defective algorithm. No credential/binding file is read.
            File.WriteAllText(Path.Combine(directory, "adjacent-group-evidence.json"), JsonSerializer.Serialize(new
            {
                inputs = inputs.Select(x => new { x.Id, x.SourceMessageId, x.Revision, x.Body, x.Task, x.ReceivedAt, x.OccurredAt }),
                generation = new { generation.Id, generation.Revision, generation.InboxIds, generation.InputWorkId, generation.Stage },
                expectedInboxIds = expected,
                ordinaryRequests = requests.Where(x => x.DeferredWork == null).Select(x => new { x.CurrentMessage.SourceMessageId, x.Revision }),
                pendingReply = !release.Task.IsCompleted,
                actualNetworkRequests = 0, productionDataRead = false
            }, Settings.Json));
            check("adjacent_counterexample_has_three_current_unfocused_inputs_and_one_held_latest_generation",
                inputs.Length == 3 && inputs.All(x => x.Task == null) && inputs.Select(x => x.Body).SequenceEqual(bodies)
                && inputs.Select(x => x.ReceivedAt).Distinct().Count() == 1
                && request.CurrentMessage.Id == current.Id && request.DeferredWork == null
                && requests.Count(x => x.DeferredWork == null) == 1 && !release.Task.IsCompleted);
            check("adjacent_addition_groups_only_b_and_c_and_does_not_absorb_independent_a",
                generation.InboxIds.SequenceEqual(expected) && generation.InputWorkId == null);
            check("separate_a_remains_original_without_becoming_a_control_authorization",
                inputs[0].GenerationId != generation.Id && beforeReply.Operations.IsEmpty
                && beforeReply.Authorizations.IsEmpty && f.Queue.Entries.Count == 0);
            release.TrySetResult(ToolModelResponse.Reply("这里只解释当前几何证明及补充要求。"));
            await OwnedHostRuntimeTests.WaitReplyAsync(f, sources[2]);
            await UntilAsync(async () => (await f.ReadAsync()).InputWork.Any(work =>
                work.SourceMessageId == WeChatText.Hash(sources[0]) && work.Stage == InputWorkStage.Completed));
            await f.Runtime.StopSessionAsync();
            var saved = await f.Ledger.ReadConversationAsync(DurableAssistantRuntime.BindingIdentity(f.Binding, f.Settings.ChatGpt.ActiveAccount));
            var accepted = saved.Outbox.Where(x => x.GenerationId == generation.Id).ToArray();
            check("latest_exact_group_is_accepted_once_and_all_original_inputs_remain",
                accepted.Length == 1 && accepted[0].Stage == OutboxStage.Accepted
                && saved.Generations.Single(x => x.Id == generation.Id).InboxIds.SequenceEqual(expected)
                && saved.Inbox.OrderBy(x => x.Revision).Select(x => x.Body).SequenceEqual(bodies)
                && f.Sent.Count(x => x == "这里只解释当前几何证明及补充要求。") == 1);
            var originalWork = saved.InputWork.Single(work => work.SourceMessageId == WeChatText.Hash(sources[0]));
            var originalGeneration = saved.Generations.Single(item => item.Id == originalWork.GenerationId);
            var originalRequest = requests.Single(item => item.DeferredWork?.Id == originalWork.Id);
            check("independent_a_completes_once_under_its_original_readonly_work_identity",
                originalWork.Stage == InputWorkStage.Completed && originalWork.SourceRevision == inputs[0].Revision
                && originalWork.ReadAtRevision == current.Revision && originalGeneration.InputWorkId == originalWork.Id
                && originalGeneration.InboxIds.SequenceEqual(new[] { inputs[0].Id })
                && originalRequest.CurrentMessage.SourceMessageId == inputs[0].SourceMessageId
                && originalRequest.CurrentMessage.Body == inputs[0].Body && originalRequest.CurrentMessage.OccurredAt == inputs[0].OccurredAt);
            check("latest_bc_reply_precedes_a_fifo_reply_without_write_authority",
                f.Sent.ToArray().SequenceEqual(new[] { "这里只解释当前几何证明及补充要求。", "这是先前独立话题的只读回复。" })
                && saved.Outbox.Count(item => item.Stage == OutboxStage.Accepted) == 2
                && saved.Operations.IsEmpty && saved.Authorizations.IsEmpty && f.Queue.Entries.Count == 0);
        }
        finally { release.TrySetResult(ToolModelResponse.Reply("测试结束后释放的旧回复。")); }
    }
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
    private static async Task TimeBoundaryAsync(string directory, bool backwards, Action<string, bool> check)
    {
        string prefix = backwards ? "clock_backward_" : "anchored_window_";
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddSeconds(-10));
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, clock: clock,
            productionMergeWindowMilliseconds: 3000);
        var entered = new TaskCompletionSource<ToolModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Script.Handler = (request, _) =>
        {
            if (request.DeferredWork != null) return Task.FromResult(ToolModelResponse.Reply("只读补答原问题。"));
            entered.TrySetResult(request); return release.Task;
        };
        string[] ids = backwards ? ["backward-a", "backward-b"] : ["anchor-a", "anchor-b", "anchor-c"];
        string[] bodies = backwards ? ["解释原始问题", "补充：只读解释"]
            : ["解释原始问题", "补充：保留边界条件", "而且说明未核对的部分"];
        try
        {
            for (int index = 0; index < ids.Length; index++)
            {
                if (index > 0) clock.Advance(backwards ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(2));
                await f.InputAsync(ids[index], bodies[index]);
            }
            var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var saved = await f.ReadAsync();
            var inputs = saved.Inbox.OrderBy(item => item.Revision).ToArray();
            var current = inputs[^1];
            var generation = saved.Generations.Single(item => item.Id == current.GenerationId);
            var expected = backwards ? new[] { current.Id } : inputs.Skip(1).Select(item => item.Id).ToArray();
            check(prefix + "fixture_reaches_actual_current_generation",
                inputs.Length == ids.Length && request.CurrentMessage.Id == current.Id && !release.Task.IsCompleted
                && (backwards ? current.ReceivedAt < inputs[0].ReceivedAt
                    : current.ReceivedAt - inputs[0].ReceivedAt == TimeSpan.FromSeconds(4)
                        && current.ReceivedAt - inputs[1].ReceivedAt == TimeSpan.FromSeconds(2)));
            check(prefix + "group_uses_current_anchor_and_nonnegative_adjacent_times",
                generation.InboxIds.SequenceEqual(expected));
            check(prefix + "original_inputs_are_retained_without_new_authority",
                inputs.Select(item => item.Body).SequenceEqual(bodies)
                && saved.Operations.IsEmpty && saved.Authorizations.IsEmpty && f.Queue.Entries.Count == 0);
            release.TrySetResult(ToolModelResponse.Reply("当前问题已按原时间范围整理。"));
            await OwnedHostRuntimeTests.WaitReplyAsync(f, ids[^1]);
        }
        finally
        {
            release.TrySetResult(ToolModelResponse.Reply("释放合成调用。"));
            await f.Runtime.StopSessionAsync();
        }
    }
    private sealed class FixedClock(DateTimeOffset now) : IClock
    { public DateTimeOffset UtcNow => now; }
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private long ticks = now.UtcTicks;
        public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
}
