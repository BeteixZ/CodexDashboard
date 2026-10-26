using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Synthetic ledger, fixed clock, fake gateway and in-memory continuation queue.
// Never loads settings, credentials, production tasks, model or channel transport.
internal static class CommitmentToolAdapterTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--commitment-tools-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("explicit_output_directory_required");
            await RunAsync(args[index + 1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory)
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
            catch (Exception error)
            {
                failures++; checks.Add(new { name, passed = false, errorType = error.GetType().Name,
                    detail = error is LedgerException ledgerError ? ledgerError.Code : error.Message });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "commitment-tools-checks.json"),
                JsonSerializer.Serialize(new { count = checks.Count, failures, productionDataRead = false,
                    networkUsed = false, actualModelUsed = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Task<Fixture> New() => Fixture.CreateAsync(Path.Combine(directory, "fixture-" + ++fixtures));
        await Check("model_off_reminder_uses_original_received_time_and_actual_receipt", async () =>
        {
            await using var f = await New(); f.Settings.ChatGpt.Enabled = false;
            var p = await f.Prepare("10分钟后提醒我看结果");
            f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(3);
            var result = await f.Adapter.ExecuteAsync(p.Context, p.Call, default);
            var saved = await f.Ledger.GetCommitmentsAsync(Identity);
            Require(result.State == ToolExecutionState.Completed && saved.WorkItems.Length == 1
                && saved.Schedules.Single().DueAtUtc == p.Message.ReceivedAt.AddMinutes(10)
                && saved.Schedules.Single().ReferenceTimeUtc == p.Message.ReceivedAt
                && CommitmentToolAdapter.DescribeReceipt(p.Call, result).Contains(saved.WorkItems.Single().Id.ToString("D"))
                && f.Gateway.TransportCalls == 0);
        });
        foreach (string mutation in new[] { "text", "time", "zone", "policy", "deadline", "missed" })
            await Check("model_cannot_insert_or_replace_" + mutation, async () =>
            {
                await using var f = await New();
                var message = await f.Receive("10分钟后提醒我看结果");
                var original = (await f.Adapter.ResolveAsync(message)).Call!;
                var arguments = JsonNode.Parse(original.ArgumentsJson)!.AsObject();
                if (mutation == "text") arguments["text"] = "模型私自安排执行";
                else if (mutation == "time") arguments["localTime"] = "2026-10-21T10:00:00";
                else if (mutation == "zone") arguments["timeZoneId"] = "Asia/Taipei";
                else if (mutation == "policy") arguments["policy"] = JsonSerializer.SerializeToNode(new { maximumNotificationsPer24Hours = 100 });
                else if (mutation == "deadline") arguments["deadlineUtc"] = "2026-10-22T00:00:00Z";
                else arguments["missedPolicy"] = "skip";
                var altered = original with { ArgumentsJson = arguments.ToJsonString() };
                Require(!(await f.Authorize(message, altered)).Allowed && (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.IsEmpty);
            });
        foreach (string source in new[] { "引用：明早9点提醒我看结果", "如果通过，10分钟后提醒我看结果", "明早9点提醒我看结果，然后继续任务", "每天9点提醒我看结果" })
            await Check("protected_reminder_source_never_authorizes_" + checks.Count, async () =>
            {
                await using var f = await New();
                var message = await f.Receive(source);
                var call = new ModelToolCall("synthetic-forged", "schedule", JsonSerializer.Serialize(new
                { text = "看结果", localTime = "2026-10-22T09:00:00", timeZoneId = "UTC", missedPolicy = "notifyOnce" }));
                Require(!(await f.Authorize(message, call)).Allowed);
            });
        await Check("forged_source_and_stale_revision_do_not_authorize", async () =>
        {
            await using var f = await New();
            var message = await f.Receive("10分钟后提醒我看结果");
            var call = (await f.Adapter.ResolveAsync(message)).Call!;
            Require(!(await f.Authorize(message with { Body = "20分钟后提醒我看结果" }, call)).Allowed);
            await f.Receive("先不要安排");
            Require(!(await f.Authorize(message, call)).Allowed);
        });
        await Check("untrusted_current_message_cannot_create_reminder", async () =>
        {
            await using var f = await New();
            var message = await f.Receive("10分钟后提醒我看结果", MessageTrust.UntrustedContent);
            Require((await f.Adapter.ResolveAsync(message)).Call == null);
        });
        foreach (var condition in new[] { ("Alpha跑完告诉我", "completed"), ("Alpha失败才叫我", "failed"),
            ("Alpha需要决定时叫我", "needsDecision"), ("Alpha十分钟没动静检查一下", "noActivity") })
            await Check("subscription_binds_exact_active_turn_" + condition.Item2, async () =>
            {
                await using var f = await New();
                var p = await f.Prepare(condition.Item1);
                using var args = JsonDocument.Parse(p.Call.ArgumentsJson);
                Require(args.RootElement.GetProperty("turnId").GetString() == "turn-alpha"
                    && args.RootElement.GetProperty("condition").GetString() == condition.Item2);
                if (condition.Item2 == "noActivity") Require(args.RootElement.GetProperty("inactivitySeconds").GetInt32() == 600);
                var result = await f.Adapter.ExecuteAsync(p.Context, p.Call, default);
                Require(result.State == ToolExecutionState.Completed && (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single().Task
                    == new TaskReference("local", "task-alpha", "turn-alpha") && f.Gateway.TransportCalls == 0);
            });
        foreach (string reason in new[] { "completed", "unknown", "stale", "missing-turn", "duplicate", "future-observation" })
            await Check("subscription_rejects_unverified_target_" + reason, async () =>
            {
                await using var f = await New();
                var task = f.Gateway.Tasks.Single();
                if (reason is "completed" or "unknown") task.Record.Status = reason;
                else if (reason == "stale") f.Gateway.Tasks = [task with { Fresh = false }];
                else if (reason == "missing-turn") task.Record.TurnId = "";
                else if (reason == "duplicate") f.Gateway.Tasks = [task, task];
                else f.Gateway.Tasks = [task with { ObservedAt = f.Clock.UtcNow.AddMinutes(5) }];
                Require((await f.Adapter.ResolveAsync(await f.Receive("Alpha跑完告诉我"))).Call == null);
            });
        await Check("same_named_tasks_require_exact_host", async () =>
        {
            await using var f = await New();
            f.Gateway.Tasks = [.. f.Gateway.Tasks, ObservedTask("remote-ssh-discovered:other", "task-beta", "turn-beta", "Alpha", "other", f.Clock.UtcNow)];
            Require((await f.Adapter.ResolveAsync(await f.Receive("Alpha失败才叫我"))).Call == null);
            var selected = (await f.Adapter.ResolveAsync(await f.Receive("本机 Alpha失败才叫我"))).Call;
            Require(selected != null && selected.ArgumentsJson.Contains("task-alpha", StringComparison.Ordinal));
            Require((await f.Adapter.ResolveAsync(await f.Receive("MissingTask失败才叫我"))).Call == null);
        });
        await Check("focus_notification_conflict_requires_explicit_current_task", async () =>
        {
            await using var f = await New();
            var focus = await f.Receive("选择 Alpha");
            await f.Ledger.SetWorkFocusAsync(Identity, focus.SourceMessageId, new("local", "task-alpha", "turn-alpha"), focus.Body);
            await f.Ledger.EnqueueNotificationAsync(Identity, "synthetic-other-notice", "合成通知", new("remote", "other", "other-turn"));
            Require((await f.Adapter.ResolveAsync(await f.Receive("这个跑完告诉我"))).Call == null);
            Require((await f.Adapter.ResolveAsync(await f.Receive("当前任务跑完告诉我"))).Call?.Name == "subscribe");
        });
        await Check("implicit_subscription_has_no_single_task_fallback_or_numeric_selection", async () =>
        {
            await using var f = await New();
            Require((await f.Adapter.ResolveAsync(await f.Receive("失败才叫我"))).Call == null);
            Require(!CommitmentToolAdapter.IsCandidate("1") && !CommitmentToolAdapter.IsCandidate("选1"));
            Require((await f.Adapter.ResolveAsync(await f.Receive("1跑完告诉我"))).Call == null);
        });
        await Check("turn_change_after_authorization_cannot_retarget_at_execution", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("Alpha跑完告诉我");
            f.Gateway.Tasks.Single().Record.TurnId = "different-turn";
            Require((await f.Adapter.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Rejected
                && (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.IsEmpty);
        });
        await Check("forged_invocation_and_binding_switch_cannot_apply", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("10分钟后提醒我看结果");
            Require((await f.Adapter.ExecuteAsync(p.Context with { InvocationId = Guid.NewGuid() }, p.Call, default)).State == ToolExecutionState.Rejected);
            Require((await f.Adapter.ExecuteAsync(p.Context, p.Call with { Id = "forged" }, default)).State == ToolExecutionState.Rejected);
            f.Current = false;
            Require((await f.Adapter.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Rejected
                && (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.IsEmpty);
        });
        await Check("full_id_update_pause_resume_cancel_preserve_scope_and_truthful_receipts", async () =>
        {
            await using var f = await New();
            await f.Execute("10分钟后提醒我看结果");
            var work = (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single();
            Require((await f.Adapter.ResolveAsync(await f.Receive("暂停提醒 " + work.Id.ToString("N")[..8]))).Call == null);
            var update = await f.Execute("修改提醒 " + work.Id.ToString("D") + " 明天09:00提醒我检查新结果");
            var afterUpdate = await f.Ledger.GetCommitmentsAsync(Identity);
            Require(afterUpdate.WorkItems.Single().Goal == "检查新结果" && afterUpdate.Schedules.Single().LocalTime == "2026-10-22T09:00:00"
                && update.State == ToolExecutionState.Completed);
            var pause = await f.Prepare("暂停提醒 " + work.Id.ToString("D"));
            var paused = await f.Adapter.ExecuteAsync(pause.Context, pause.Call, default);
            Require(CommitmentToolAdapter.DescribeReceipt(pause.Call, paused).Contains("任务没有被中断"));
            await f.Finish(pause, paused);
            await f.Execute("恢复提醒 " + work.Id.ToString("D"));
            var cancel = await f.Prepare("取消提醒 " + work.Id.ToString("D"));
            var cancelled = await f.Adapter.ExecuteAsync(cancel.Context, cancel.Call, default);
            Require((await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage == TrackedWorkStage.Cancelled
                && CommitmentToolAdapter.DescribeReceipt(cancel.Call, cancelled).Contains("结果未知的消息仍保留原状态"));
        });
        await Check("reminder_content_can_pause_resume_reschedule_and_cancel_without_model", async () =>
        {
            await using var f = await New(); f.Settings.ChatGpt.Enabled = false;
            await f.Execute("10分钟后提醒我喝水");
            var original = (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single();
            await f.Execute("暂停提醒 喝水");
            Require((await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage == TrackedWorkStage.Paused);
            await f.Execute("恢复提醒 喝水");
            await f.Execute("修改提醒 喝水 改为 明天09:00提醒我站起来活动");
            var changed = await f.Ledger.GetCommitmentsAsync(Identity);
            Require(changed.WorkItems.Single().Id == original.Id && changed.WorkItems.Single().Goal == "站起来活动"
                && changed.Schedules.Single().LocalTime == "2026-10-22T09:00:00");
            Require((await f.Adapter.ResolveAsync(await f.Receive("取消提醒 喝水"))).Call == null);
            await f.Execute("取消提醒 站起来活动");
            Require((await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single().Stage == TrackedWorkStage.Cancelled
                && f.Gateway.TransportCalls == 0);
        });
        await Check("same_content_reminders_require_explicit_id_and_do_not_match_substrings", async () =>
        {
            await using var f = await New();
            await f.Execute("10分钟后提醒我喝水");
            await f.Execute("20分钟后提醒我喝水");
            var works = (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems;
            var ambiguous = await f.Adapter.ResolveAsync(await f.Receive("取消提醒 喝水"));
            Require(ambiguous.Call == null && works.All(work => ambiguous.Reply!.Contains(work.Id.ToString("D"))));
            Require((await f.Adapter.ResolveAsync(await f.Receive("取消提醒 喝"))).Call == null);
            await f.Execute("取消提醒 " + works[0].Id.ToString("D"));
            await f.Execute("取消提醒 喝水");
            Require((await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.All(work => work.Stage == TrackedWorkStage.Cancelled));
        });
        await Check("reminder_search_paginates_all_matches_without_changing_records", async () =>
        {
            await using var f = await New(); f.Settings.ChatGpt.Enabled = false;
            for (int i = 0; i < 9; i++) await f.Execute("10分钟后提醒我喝水" + i);
            await f.Execute("10分钟后提醒我站起来");
            var before = await f.Ledger.GetCommitmentsAsync(Identity);
            var first = await f.Adapter.ResolveAsync(await f.Receive("查提醒 喝水"));
            var second = await f.Adapter.ResolveAsync(await f.Receive("查提醒 喝水 第2页"));
            Require(first.Call == null && second.Call == null && first.Reply!.Contains("查提醒 喝水 第2页")
                && first.Reply.Contains("共 9 项") && second.Reply!.Contains("第 2/2 页")
                && !first.Reply.Contains("站起来") && !second.Reply.Contains("站起来")
                && before.WorkItems.Where(work => work.Goal.StartsWith("喝水")).All(work =>
                    first.Reply.Contains(work.Id.ToString("D")) != second.Reply.Contains(work.Id.ToString("D"))));
            var after = await f.Ledger.GetCommitmentsAsync(Identity);
            Require(before.WorkItems.SequenceEqual(after.WorkItems) && (await f.Ledger.ReadConversationAsync(Identity)).Operations.Length == 10);
        });
        await Check("list_aliases_show_real_records_and_binding_switch_hides_them", async () =>
        {
            await using var f = await New();
            await f.Execute("10分钟后提醒我看结果");
            var id = (await f.Ledger.GetCommitmentsAsync(Identity)).WorkItems.Single().Id.ToString("D");
            foreach (string alias in new[] { "提醒列表", "订阅列表", "事项列表", "自动化列表", "你还记着哪些事" })
            {
                var result = await f.Adapter.ResolveAsync(await f.Receive(alias));
                Require(result.Candidate && result.Call == null && result.Reply!.Contains(id));
            }
            f.Current = false;
            Require(!(await f.Adapter.FormatListAsync(Identity)).Contains(id));
            Require(f.Gateway.TransportCalls == 0 && f.Gateway.ReportReads == 0);
        });
        if (failures != 0) throw new InvalidOperationException("commitment_adapter_checks_failed");
    }
    private static readonly ConversationIdentity Identity = new("synthetic-commitment-user", "synthetic-commitment-binding", "main");
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static GatewayTask ObservedTask(string host, string id, string turn, string title, string hostLabel, DateTimeOffset now)
        => new(new(host, id), new() { Id = id, Title = title, Project = "Synthetic", Host = hostLabel, TurnId = turn,
            Status = TaskStatusNames.Active, RecordedStatus = TaskStatusNames.Active, Source = "synthetic-observation" }, now, true, "synthetic-observation");
    private sealed record Prepared(InboxMessage Message, ModelToolCall Call, GenerationLease Lease, ToolExecutionContext Context);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Gateway : ICodexGateway
    {
        internal GatewayTask[] Tasks = [];
        internal int TransportCalls, ReportReads;
        public IReadOnlyList<GatewayTask> ListTasks() => Tasks;
        public GatewayRead<GatewayTask> ReadTask(CanonicalTaskIdentity identity) => new(Tasks.SingleOrDefault(t => t.Identity == identity), "synthetic", "synthetic");
        public GatewayRead<GatewayReport> ReadReport(CanonicalTaskIdentity identity, string turnId)
        { ReportReads++; throw new InvalidOperationException("unexpected_report_read"); }
        public IReadOnlyList<GatewayCapability> Capabilities(CanonicalTaskIdentity identity) => [];
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { TransportCalls++; throw new InvalidOperationException("unexpected_external_dispatch"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal Settings Settings = new() { ChatGpt = new() { Enabled = false, IncludeTaskContext = true } };
        internal Clock Clock = new();
        internal Gateway Gateway = new();
        internal TaskControlQueue Queue = null!;
        internal DurableConversationLedger Ledger = null!;
        internal CommitmentToolAdapter Adapter = null!;
        internal bool Current = true;
        private int sources;
        internal static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var f = new Fixture();
            f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "conversation.state")), new WindowsStateProtector(), f.Clock);
            f.Gateway.Tasks = [ObservedTask("local", "task-alpha", "turn-alpha", "Alpha", "本机", f.Clock.UtcNow)];
            f.Queue = new(f.Settings, f.Gateway, null); f.Queue.SetOwner("synthetic-owner");
            f.Adapter = new(f.Settings, f.Ledger, f.Gateway, f.Queue, () => null, id => f.Current && id == Identity, () => TimeZoneInfo.Utc, () => f.Clock.UtcNow);
            return f;
        }
        internal async Task<InboxMessage> Receive(string text, MessageTrust trust = MessageTrust.AuthenticatedUser)
            => (await Ledger.ReceiveAsync(Identity, "source-" + ++sources, text, trust, Clock.UtcNow)).Message;
        internal ValueTask<UserAuthorizationDecision> Authorize(InboxMessage source, ModelToolCall call)
        {
            var descriptor = CommitmentTools.Descriptors().Single(d => d.Name == call.Name);
            return Adapter.AuthorizeAsync(new(source, call, descriptor), default);
        }
        internal async Task<Prepared> Prepare(string text)
        {
            var message = await Receive(text);
            var resolved = await Adapter.ResolveAsync(message);
            Require(resolved.Call != null);
            var call = resolved.Call!;
            var permission = await Authorize(message, call);
            Require(permission.Allowed);
            var lease = await Ledger.BeginGenerationAsync(Identity, [message.SourceMessageId]);
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, message.SourceMessageId,
                call.Name, permission.TargetKey!, call.ArgumentsJson, permission.SourceExcerpt!, permission.Task);
            string key = "synthetic-commitment-" + Guid.NewGuid().ToString("N");
            var operation = await Ledger.QueueOperationAsync(Identity, grant.Id, key);
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, message.SourceMessageId, call.Id, call.Name, call.ArgumentsJson, true);
            await Ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
            return new(message, call, lease, new(Identity, message.SourceMessageId, message.Revision, invocation.Id, operation.Id, key));
        }
        internal async Task<ToolExecutionResult> Execute(string text)
        {
            var prepared = await Prepare(text);
            var result = await Adapter.ExecuteAsync(prepared.Context, prepared.Call, default);
            Require(result.State == ToolExecutionState.Completed);
            await Finish(prepared, result);
            return result;
        }
        internal async Task Finish(Prepared p, ToolExecutionResult result)
        {
            await Ledger.FinishToolInvocationAsync(Identity, p.Context.InvocationId, ToolInvocationStage.Completed,
                result.Facts.GetRawText(), OperationStage.Completed);
            await Ledger.EndGenerationAttemptAsync(p.Lease);
        }
        public async ValueTask DisposeAsync() { await Ledger.DisposeAsync(); await Queue.DisposeAsync(); }
    }
}
