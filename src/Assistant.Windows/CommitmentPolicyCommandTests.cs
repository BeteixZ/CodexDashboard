using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Current-message adapter + actual durable ledger, synthetic tasks and clock.
// No real model, channel, task transport, settings or credential files are used.
internal static class CommitmentPolicyCommandTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-policy-commands-test");
        if (index < 0) return false;
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)) throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        int failures = 0, fixtures = 0;
        var checks = new List<object>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error is LedgerException le ? le.Code : error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "commitment-policy-command-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Json));
        }
        Task<Fixture> New() => Fixture.CreateAsync(Path.Combine(directory, "fixture-" + ++fixtures));
        foreach (string kind in new[] { "提醒", "订阅", "事项" })
            await Check("delete_alias_cancels_but_retains_history_" + kind, async () =>
            {
                await using var f = await New();
                var original = await f.Seed(kind == "订阅");
                var result = await f.Execute("删除" + kind + " " + original.Id);
                var saved = await f.Snapshot();
                string receipt = CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result);
                Require(saved.WorkItems.Single().Id == original.Id && saved.WorkItems.Single().Stage == TrackedWorkStage.Cancelled
                    && receipt.Contains("历史记录保留，没有物理删除") && receipt.Contains("任务没有被中断")
                    && (await f.Resolve("删除" + kind + " " + original.Id)).Call == null && f.Gateway.TransportCalls == 0);
            });
        await Check("quiet_hours_capture_zone_and_preserve_every_other_limit", async () =>
        {
            await using var f = await New();
            var work = await f.Seed();
            var prior = new CommitmentPolicy(MinimumIntervalSeconds: 91, MaximumNotificationsPer24Hours: 7,
                MaximumCheckAttempts: 5, MaximumModelPreparations: 3, MaximumDeliveryAttempts: 6);
            await f.SetFixturePolicy(work.Id, prior);
            f.Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            var result = await f.Execute("设置事项 " + work.Id + " 安静时段 22:00-08:00");
            var updated = (await f.Snapshot()).WorkItems.Single();
            Require(updated.Policy == prior with { QuietStart = "22:00", QuietEnd = "08:00", TimeZoneId = f.Zone.Id }
                && updated.Id == work.Id && updated.Goal == work.Goal
                && CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("安静时段 22:00-08:00 [" + f.Zone.Id + "]"));
        });
        await Check("clear_quiet_hours_preserves_limits_and_missed_policy", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            await f.Execute("设置提醒 " + work.Id + " 安静时段 22:00-08:00");
            await f.Execute("设置事项 " + work.Id + " 每24小时最多3次");
            await f.Execute("设置提醒 " + work.Id + " 错过时跳过");
            var before = await f.Snapshot();
            var result = await f.Execute("清除事项 " + work.Id + " 安静时段");
            var after = await f.Snapshot();
            Require(after.WorkItems.Single().Policy == before.WorkItems.Single().Policy with { QuietStart = null, QuietEnd = null, TimeZoneId = null }
                && after.Schedules.Single().MissedPolicy == MissedTriggerPolicy.Skip
                && CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("未设置安静时段"));
        });
        foreach (var pair in new[] { ("错过时跳过", MissedTriggerPolicy.Skip), ("恢复后提醒一次", MissedTriggerPolicy.NotifyOnce), ("需要我决定", MissedTriggerPolicy.NeedsDecision) })
            await Check("missed_policy_change_preserves_goal_due_and_notification_policy_" + pair.Item2, async () =>
            {
                await using var f = await New(); var work = await f.Seed();
                await f.Execute("设置事项 " + work.Id + " 每24小时最多3次");
                var before = await f.Snapshot();
                var result = await f.Execute("设置提醒 " + work.Id + " " + pair.Item1);
                var after = await f.Snapshot();
                Require(after.WorkItems.Single().Goal == work.Goal && after.WorkItems.Single().Policy == before.WorkItems.Single().Policy
                    && after.Schedules.Single().DueAtUtc == before.Schedules.Single().DueAtUtc && after.Schedules.Single().MissedPolicy == pair.Item2
                    && CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("已更新这项提醒的错过时间处理")
                    && !CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("已保存这项订阅"));
            });
        foreach (int limit in new[] { 1, 3, 100 })
            await Check("per_item_frequency_cannot_raise_shared_twenty_limit_" + limit, async () =>
            {
                await using var f = await New(); var work = await f.Seed();
                await f.Execute("设置提醒 " + work.Id + " 安静时段 22:00-08:00");
                var before = (await f.Snapshot()).WorkItems.Single().Policy;
                await f.Execute("设置事项 " + work.Id + " 每24小时最多" + limit + "次");
                var current = (await f.Snapshot()).WorkItems.Single().Policy;
                var budget = await f.Ledger.GetCommitmentBudgetStatusAsync(Identity);
                Require(current == before with { MaximumNotificationsPer24Hours = limit }
                    && budget.Windows.Single(w => w.Kind == CommitmentBudgetKind.Notification && w.Window == TimeSpan.FromDays(1)).Limit == 20
                    && f.Gateway.TransportCalls == 0);
            });
        await Check("subscription_policy_updates_keep_exact_target_and_pause", async () =>
        {
            await using var f = await New(); var work = await f.Seed(true);
            await f.Execute("暂停订阅 " + work.Id);
            var result = await f.Execute("设置订阅 " + work.Id + " 安静时段 22:00-08:00");
            await f.Execute("设置订阅 " + work.Id + " 每24小时最多2次");
            var updated = (await f.Snapshot()).WorkItems.Single();
            Require(updated.Stage == TrackedWorkStage.Paused && updated.Task == work.Task && updated.Policy.MaximumNotificationsPer24Hours == 2
                && CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("已更新这项事项的通知规则")
                && !CommitmentToolAdapter.DescribeReceipt(result.Call, result.Result).Contains("已保存这项订阅"));
        });
        string[] invalid =
        [
            "设置事项 SHORT 安静时段 22:00-08:00", "设置事项 ID 安静时段 24:00-08:00",
            "设置事项 ID 安静时段 22:60-08:00", "设置事项 ID 安静时段 22:00-22:00",
            "设置事项 ID 安静时段 9:00-08:00", "设置事项 ID 安静时段",
            "清除事项 ID 安静时段 22:00-08:00", "清除事项 ID 每24小时最多3次",
            "设置事项 ID 每24小时最多0次", "设置事项 ID 每24小时最多101次",
            "设置订阅 ID 安静时段 22:00-08:00", "设置事项 ID 错过时跳过",
            "引用：设置事项 ID 每24小时最多3次", "如果通过就设置事项 ID 每24小时最多3次",
            "设置事项 ID 每24小时最多3次，然后继续任务", "设置事项 ID\n安静时段 22:00-08:00",
            "“删除提醒 ID”", "不要删除提醒 ID", "删除提醒 ID 并继续任务", "设置提醒 ID 需要我决定吗"
        ];
        for (int index = 0; index < invalid.Length; index++)
        {
            string expression = invalid[index];
            await Check("invalid_or_protected_policy_command_" + index, async () =>
            {
                await using var f = await New(); var work = await f.Seed();
                string command = expression.Replace("SHORT", work.Id.ToString("N")[..8], StringComparison.Ordinal)
                    .Replace("ID", work.Id.ToString("D"), StringComparison.Ordinal);
                Require((await f.Resolve(command)).Call == null && (await f.Snapshot()).WorkItems.Single() == work);
            });
        }
        foreach (string field in new[] { "minimumIntervalSeconds", "maximumCheckAttempts", "maximumModelPreparations", "maximumDeliveryAttempts", "timeZoneId", "maximumNotificationsPer24Hours", "text", "globalLimit" })
            await Check("model_cannot_add_or_replace_unrequested_policy_field_" + field, async () =>
            {
                await using var f = await New(); var work = await f.Seed();
                var source = await f.Receive("设置事项 " + work.Id + " 安静时段 22:00-08:00");
                var call = (await f.Adapter.ResolveAsync(source)).Call!;
                var args = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                if (field == "text") args["text"] = "模型自行更改提醒内容";
                else if (field == "globalLimit") args["globalLimit"] = 100;
                else if (field == "timeZoneId") args["policy"]![field] = "Asia/Taipei";
                else args["policy"]![field] = field == "maximumModelPreparations" ? 3 : 8;
                var changed = call with { ArgumentsJson = args.ToJsonString() };
                Require(!(await f.Authorize(source, changed)).Allowed && (await f.Snapshot()).WorkItems.Single() == work);
            });
        await Check("timezone_change_after_authorization_rejects_without_reinterpreting", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            var pending = await f.Prepare("设置事项 " + work.Id + " 安静时段 22:00-08:00");
            f.Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            Require((await f.Adapter.ExecuteAsync(pending.Context, pending.Call, default)).State == ToolExecutionState.Rejected
                && (await f.Snapshot()).WorkItems.Single() == work);
        });
        await Check("version_change_with_same_source_fences_old_policy_dispatch", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            var pending = await f.Prepare("设置事项 " + work.Id + " 每24小时最多3次");
            // A trusted fixture transaction races the dispatch without advancing
            // the inbox revision, isolating the work-version check from freshness.
            var changedPolicy = work.Policy with { MaximumNotificationsPer24Hours = 4 };
            var raw = Call("change_commitment", new { workItemId = work.Id.ToString("D"), expectedVersion = work.Version,
                action = "update", policy = changedPolicy });
            await f.ApplyFixtureTransaction(pending.Message, pending.Lease, raw, work.Task);
            Require((await f.Adapter.ExecuteAsync(pending.Context, pending.Call, default)).State == ToolExecutionState.Rejected
                && (await f.Snapshot()).WorkItems.Single().Policy.MaximumNotificationsPer24Hours == 4);
        });
        foreach (bool triggered in new[] { false, true })
            await Check("missed_policy_rejects_" + (triggered ? "already_triggered" : "past_due_untriggered"), async () =>
            {
                await using var f = await New(); var work = await f.Seed();
                f.Clock.UtcNow = f.Clock.UtcNow.AddHours(1);
                if (triggered) await f.Ledger.AdvanceCommitmentsAsync(Identity, 32);
                var before = await f.Snapshot();
                Require((await f.Resolve("设置提醒 " + work.Id + " 错过时跳过")).Call == null
                    && (await f.Snapshot()).Schedules.Single() == before.Schedules.Single());
            });
        await Check("cancelled_or_wrong_binding_cannot_be_modified", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            var pending = await f.Prepare("设置事项 " + work.Id + " 每24小时最多3次");
            f.Current = false;
            Require((await f.Adapter.ExecuteAsync(pending.Context, pending.Call, default)).State == ToolExecutionState.Rejected
                && (await f.Snapshot()).WorkItems.Single() == work);
            f.Current = true;
            await f.Execute("取消事项 " + work.Id);
            Require((await f.Resolve("设置事项 " + work.Id + " 每24小时最多3次")).Call == null);
            var other = new ConversationIdentity("synthetic-other", "synthetic-other-binding", "main");
            var message = (await f.Ledger.ReceiveAsync(other, "other-source", "设置事项 " + work.Id + " 每24小时最多3次",
                MessageTrust.AuthenticatedUser, f.Clock.UtcNow)).Message;
            Require((await f.Adapter.ResolveAsync(message)).Call == null);
        });
        await Check("vague_global_quiet_request_is_clarification_only", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            var result = await f.Resolve("晚上别打扰");
            Require(result.Candidate && result.Call == null && result.Reply!.Contains("尚未更改全局")
                && (await f.Snapshot()).WorkItems.Single() == work);
        });
        await Check("list_and_desktop_summary_show_saved_rules_and_real_shared_budget", async () =>
        {
            await using var f = await New(); var work = await f.Seed();
            await f.Execute("设置事项 " + work.Id + " 安静时段 22:00-08:00");
            await f.Execute("设置事项 " + work.Id + " 每24小时最多3次");
            await f.Execute("设置提醒 " + work.Id + " 错过时跳过");
            var state = await f.Snapshot();
            string summary = CommitmentToolAdapter.PolicySummary(state.WorkItems.Single(), state.Schedules.Single());
            string list = await f.Adapter.FormatListAsync(Identity);
            Require(summary.Contains("安静时段 22:00-08:00 [UTC]") && summary.Contains("每24小时最多3次")
                && summary.Contains("错过时跳过") && !summary.Contains(work.Id.ToString()) && !summary.Contains(work.Goal)
                && list.Contains(summary) && list.Contains("提醒与订阅通知还受共享预算限制：滚动24小时最多20次"));
        });
        if (failures != 0) throw new InvalidOperationException("commitment_policy_checks_failed");
    }
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
    private static readonly ConversationIdentity Identity = new("synthetic-policy-user", "synthetic-policy-binding", "main");
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args, Json));
    private sealed record Prepared(InboxMessage Message, ModelToolCall Call, GenerationLease Lease, ToolExecutionContext Context);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Gateway(Clock clock) : ICodexGateway
    {
        internal int TransportCalls;
        public IReadOnlyList<GatewayTask> ListTasks() => [new(new("local", "task-alpha"), new()
        { Id = "task-alpha", Title = "Alpha", Project = "Synthetic", Host = "本机", TurnId = "turn-alpha",
            Status = TaskStatusNames.Active, RecordedStatus = TaskStatusNames.Active }, clock.UtcNow, true, "synthetic")];
        public GatewayRead<GatewayTask> ReadTask(CanonicalTaskIdentity identity) => new(ListTasks().SingleOrDefault(t => t.Identity == identity), "synthetic", "synthetic");
        public GatewayRead<GatewayReport> ReadReport(CanonicalTaskIdentity identity, string turnId) => throw new InvalidOperationException("unexpected_report_read");
        public IReadOnlyList<GatewayCapability> Capabilities(CanonicalTaskIdentity identity) => [];
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { TransportCalls++; throw new InvalidOperationException("unexpected_transport"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Settings Settings = new() { ChatGpt = new() { Enabled = false, IncludeTaskContext = true } };
        internal readonly Clock Clock = new();
        internal Gateway Gateway = null!;
        internal TimeZoneInfo Zone = TimeZoneInfo.Utc;
        internal bool Current = true;
        internal DurableConversationLedger Ledger = null!;
        internal TaskControlQueue Queue = null!;
        internal CommitmentToolAdapter Adapter = null!;
        private int sources;
        internal static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory); var f = new Fixture(); f.Gateway = new(f.Clock);
            f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "conversation.state")), new WindowsStateProtector(), f.Clock);
            f.Queue = new(f.Settings, f.Gateway, null); f.Queue.SetOwner("synthetic-policy-owner");
            f.Adapter = new(f.Settings, f.Ledger, f.Gateway, f.Queue, () => null, id => f.Current && id == Identity, () => f.Zone, () => f.Clock.UtcNow);
            return f;
        }
        internal Task<CommitmentSnapshot> Snapshot() => Ledger.GetCommitmentsAsync(Identity);
        internal async Task<InboxMessage> Receive(string text) => (await Ledger.ReceiveAsync(Identity, "source-" + ++sources, text, MessageTrust.AuthenticatedUser, Clock.UtcNow)).Message;
        internal async Task<CommitmentCommandResolution> Resolve(string text) => await Adapter.ResolveAsync(await Receive(text));
        internal ValueTask<UserAuthorizationDecision> Authorize(InboxMessage source, ModelToolCall call)
            => Adapter.AuthorizeAsync(new(source, call, CommitmentTools.Descriptors().Single(d => d.Name == call.Name)), default);
        internal async Task<TrackedWorkItem> Seed(bool subscription = false)
        {
            await Execute(subscription ? "Alpha失败才叫我" : "10分钟后提醒我看结果");
            return (await Snapshot()).WorkItems.Single();
        }
        internal async Task<Prepared> Prepare(string text)
        {
            var source = await Receive(text); var resolved = await Adapter.ResolveAsync(source);
            Require(resolved.Call != null); var call = resolved.Call!;
            var permission = await Authorize(source, call); Require(permission.Allowed);
            var lease = await Ledger.BeginGenerationAsync(Identity, [source.SourceMessageId]);
            return await PrepareCall(source, lease, call, permission.TargetKey!, permission.Task);
        }
        private async Task<Prepared> PrepareCall(InboxMessage source, GenerationLease lease, ModelToolCall call, string target, TaskReference? task)
        {
            var descriptor = CommitmentTools.Descriptors().Single(d => d.Name == call.Name);
            Require(descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _)); call = call with { ArgumentsJson = canonical };
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, source.SourceMessageId, call.Name, target, canonical, source.Body, task);
            string key = "synthetic-policy-" + Guid.NewGuid().ToString("N");
            var op = await Ledger.QueueOperationAsync(Identity, grant.Id, key);
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, source.SourceMessageId, call.Id, call.Name, canonical, true);
            await Ledger.BeginToolInvocationAsync(lease, invocation.Id, op.Id);
            return new(source, call, lease, new(Identity, source.SourceMessageId, source.Revision, invocation.Id, op.Id, key));
        }
        internal async Task<(ModelToolCall Call, ToolExecutionResult Result)> Execute(string text)
        {
            var p = await Prepare(text); var result = await Adapter.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed); await Finish(p, result, true); return (p.Call, result);
        }
        internal async Task SetFixturePolicy(Guid id, CommitmentPolicy policy)
        {
            var work = (await Snapshot()).WorkItems.Single(w => w.Id == id);
            var source = await Receive("Synthetic trusted fixture policy setup");
            var lease = await Ledger.BeginGenerationAsync(Identity, [source.SourceMessageId]);
            await ApplyFixtureTransaction(source, lease, Call("change_commitment", new
            { workItemId = id.ToString("D"), expectedVersion = work.Version, action = "update", policy }), work.Task);
            await Ledger.EndGenerationAttemptAsync(lease);
        }
        internal async Task ApplyFixtureTransaction(InboxMessage source, GenerationLease lease, ModelToolCall call, TaskReference? task)
        {
            var p = await PrepareCall(source, lease, call, CommitmentTools.TargetKey(call.Name, call.ArgumentsJson), task);
            var result = await Ledger.ApplyCommitmentToolAsync(p.Context); Require(result.State == ToolExecutionState.Completed);
            await Finish(p, result, false);
        }
        private async Task Finish(Prepared p, ToolExecutionResult result, bool endGeneration)
        {
            await Ledger.FinishToolInvocationAsync(Identity, p.Context.InvocationId, ToolInvocationStage.Completed, result.Facts.GetRawText(), OperationStage.Completed);
            if (endGeneration) await Ledger.EndGenerationAttemptAsync(p.Lease);
        }
        public async ValueTask DisposeAsync() { await Ledger.DisposeAsync(); await Queue.DisposeAsync(); }
    }
}
