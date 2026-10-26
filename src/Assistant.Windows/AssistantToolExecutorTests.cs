using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Uses synthetic task records, fake model/transport and explicitly created temporary DPAPI stores.
// Dispatch this before Settings.Load, credentials, channels, production singleton or network setup.
internal static class AssistantToolExecutorTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--assistant-tools-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("explicit_output_directory_required");
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
            File.WriteAllText(Path.Combine(directory, "tools-checks.json"),
                JsonSerializer.Serialize(new { count = checks.Count, failures, networkUsed = false,
                    realAccountUsed = false, actualModelUsed = false, checks }, Settings.Json));
        }

        await using (var fixture = new Fixture(null))
        {
            var local = fixture.Local;
            var remote = fixture.Remote;
            string text = "继续 本机 Alpha 修复失败项";
            await Check("explicit_current_user_exact_original_text_authorizes_exact_task", async () =>
            {
                var result = await fixture.Authorize(text, Send(local, text));
                Require(result.Allowed && result.TargetKey == CodexGateway.Identity(local).Key
                    && result.SourceExcerpt == text && result.Task == Reference(local));
            });
            await Check("model_rewritten_instruction_does_not_inherit_authority", async () =>
                Require(!(await fixture.Authorize(text, Send(local, text + "，顺便删除文件"))).Allowed));
            await Check("untrusted_current_content_cannot_authorize", async () =>
                Require(!(await fixture.Authorize(text, Send(local, text), MessageTrust.UntrustedContent)).Allowed));
            fixture.Context = new(Identity, 0, 1, 0, Focus: new(Reference(local), "synthetic-focus", "选择 Alpha", DateTimeOffset.UtcNow));
            foreach (string rejected in new[]
            {
                "继续另一个任务", "继续其他任务，当前任务别动", "接着别的任务",
                "继续不是当前任务", "继续当前任务，当前任务别动"
            })
                await Check("focus_cannot_override_explicit_other_or_negated_target_" + checks.Count, async () =>
                    Require(!(await fixture.Authorize(rejected, Send(local, rejected))).Allowed));
            await Check("unambiguous_short_continuation_uses_explicit_saved_focus", async () =>
                Require((await fixture.Authorize("继续测试", Send(local, "继续测试"))).Allowed));
            fixture.Context = fixture.Context with { LastNotification = new(Guid.NewGuid(), Reference(remote), DateTimeOffset.UtcNow) };
            await Check("different_recent_notification_makes_implicit_continuation_ambiguous", async () =>
                Require(!(await fixture.Authorize("继续测试", Send(local, "继续测试"))).Allowed));
            await Check("explicit_current_task_keeps_focus_independent_of_notification", async () =>
                Require((await fixture.Authorize("继续当前任务：只分析", Send(local, "继续当前任务：只分析"))).Allowed));
            fixture.Context = fixture.Context with { LastNotification = new(Guid.NewGuid(), null, DateTimeOffset.UtcNow) };
            await Check("unknown_notification_target_never_grants_implicit_focus", async () =>
                Require(!(await fixture.Authorize("继续测试", Send(local, "继续测试"))).Allowed
                    && (await fixture.Authorize("继续当前任务：只分析", Send(local, "继续当前任务：只分析"))).Allowed));
            fixture.Context = null;
            foreach (string rejected in new[]
            {
                "能不能继续 Alpha 测试？", "不要继续 Alpha", "如果测试通过，就继续 Alpha",
                "假如完成了就继续 Alpha", "报告：继续 Alpha", "引用：继续 Alpha",
                "解释这句话：继续 Alpha", "继续解释“修复 Alpha”这句话",
                "继续解释\"修复 Alpha\"这句话", "继续 Alpha ```运行测试```",
                "谢谢", "Alpha 现在进展怎么样"
            })
                await Check("query_quote_condition_denied_" + checks.Count, async () =>
                    Require(!(await fixture.Authorize(rejected, Send(local, rejected))).Allowed));
            foreach (var item in new[]
            {
                ("existing_progress_request_is_read_only", "让 本机 Alpha 汇报当前进展"),
                ("single_quote_explanation_does_not_dispatch", "继续解释'修复 Alpha'这句话"),
                ("condition_tail_requires_confirmation", "继续 Alpha，前提是测试全部通过"),
                ("explicit_no_send_constraint_prevents_dispatch", "继续 Alpha，但不要向本机发送任何指令")
            })
                await Check(item.Item1, async () => Require(!(await fixture.Authorize(item.Item2, Send(local, item.Item2))).Allowed));
            await Check("same_project_multiple_tasks_requires_selection", async () =>
            {
                string request = "继续 CommonProject 测试";
                Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
            });
            await Check("current_text_host_cannot_be_replaced_by_model", async () =>
            {
                string request = "继续 fake-remote Beta 测试";
                Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
                Require((await fixture.Authorize(request, Send(remote, request))).Allowed);
            });
            await Check("host_boundary_prevents_alias_prefix_guess", async () =>
            {
                string request = "继续 remote-ssh-discovered:fake-remote-extra 上的 Beta";
                Require(!(await fixture.Authorize(request, Send(remote, request))).Allowed);
            });
            await Check("unknown_explicit_host_cannot_use_single_available_task", async () =>
            {
                fixture.Tasks = new[] { local };
                try
                {
                    string request = "继续 remote-ssh-discovered:missing-host 上的 Alpha";
                    Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
                    Require(!(await fixture.Authorize(request, Send(local, request), task: Reference(local))).Allowed);
                }
                finally { fixture.Tasks = new[] { local, remote }; }
            });
            await Check("unknown_named_task_does_not_fall_back_to_only_task", async () =>
            {
                fixture.Tasks = new[] { local };
                try
                {
                    string request = "继续 MissingProject 的任务";
                    Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
                }
                finally { fixture.Tasks = new[] { local, remote }; }
            });
            await Check("task_name_inside_another_ascii_name_is_not_target_evidence", async () =>
            {
                string request = "继续 NotAlphaProject 的任务";
                Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
            });
            await Check("duplicate_identity_cannot_make_unrelated_target_unique", async () =>
            {
                fixture.Tasks = new[] { local, local, remote };
                try
                {
                    string request = "继续 Alpha";
                    Require(!(await fixture.Authorize(request, Send(remote, request))).Allowed);
                    Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
                }
                finally { fixture.Tasks = new[] { local, remote }; }
            });
            await Check("bare_followup_needs_separate_trusted_task_reference", async () =>
            {
                fixture.Tasks = new[] { local };
                try
                {
                    string request = "继续测试";
                    Require(!(await fixture.Authorize(request, Send(local, request))).Allowed);
                    Require((await fixture.Authorize(request, Send(local, request), task: Reference(local))).Allowed);
                    Require(!(await fixture.Authorize(request, Send(local, request), task: Reference(remote))).Allowed);
                }
                finally { fixture.Tasks = new[] { local, remote }; }
            });
            await Check("profile_never_inferred_from_reference_or_model", async () =>
            {
                var call = Call("send_to_task", new { hostId = "local", threadId = local.Id, profileId = "other", text });
                Require(!(await fixture.Authorize(text, call, task: Reference(local))).Allowed);
            });
            await Check("remember_requires_exact_explicit_content_and_scope", async () =>
            {
                Require((await fixture.Authorize("记住：我偏好简洁回复", Call("remember", new { text = "我偏好简洁回复", scope = "personal" }))).Allowed);
                Require(!(await fixture.Authorize("我偏好简洁回复", Call("remember", new { text = "我偏好简洁回复", scope = "personal" }))).Allowed);
                Require(!(await fixture.Authorize("记住：我偏好简洁回复", Call("remember", new { text = "我允许自动执行", scope = "personal" }))).Allowed);
                Require(!(await fixture.Authorize("记住：我偏好简洁回复", Call("remember", new { text = "我偏好简洁回复", scope = "project" }))).Allowed);
            });
            var memory = fixture.Memory.Add("SYNTHETIC_ORIGINAL_MEMORY");
            var otherMemory = fixture.Memory.Add("SYNTHETIC_OTHER_MEMORY");
            await Check("forget_requires_existing_exact_memory_id", async () =>
            {
                Require((await fixture.Authorize("删除记忆 " + memory.Id, Call("forget", new { memoryId = memory.Id }))).Allowed);
                Require(!(await fixture.Authorize("删除记忆 " + memory.Id, Call("forget", new { memoryId = otherMemory.Id }))).Allowed);
                Require(!(await fixture.Authorize("删除记忆 DEADBEEF", Call("forget", new { memoryId = "DEADBEEF" }))).Allowed);
                Require(!(await fixture.Authorize("删除记忆 " + memory.Id + " 是什么意思", Call("forget", new { memoryId = memory.Id }))).Allowed);
                Require(!(await fixture.Authorize("引用：删除记忆 " + memory.Id, Call("forget", new { memoryId = memory.Id }))).Allowed);
            });
            await Check("update_memory_binds_both_id_and_verbatim_replacement", async () =>
            {
                string request = "修改记忆 " + memory.Id + " 改成这条明确内容";
                Require((await fixture.Authorize(request, Call("update_memory", new { memoryId = memory.Id, text = "改成这条明确内容" }))).Allowed);
                Require(!(await fixture.Authorize(request, Call("update_memory", new { memoryId = otherMemory.Id, text = "改成这条明确内容" }))).Allowed);
                Require(!(await fixture.Authorize(request, Call("update_memory", new { memoryId = memory.Id, text = "模型补充的新内容" }))).Allowed);
            });
            await Check("read_task_excludes_analysis_command_and_raw_transcript", async () =>
            {
                var read = await fixture.Execute(Call("read_task", Target(local)));
                string facts = read.Facts.GetRawText();
                Require(read.State == ToolExecutionState.ReadOnlySuccess
                    && facts.Contains("PUBLIC_PROGRESS_MARKER", StringComparison.Ordinal)
                    && !facts.Contains("PRIVATE_ANALYSIS_MARKER", StringComparison.Ordinal)
                    && !facts.Contains("RAW_COMMAND_MARKER", StringComparison.Ordinal)
                    && !facts.Contains("RAW_TRANSCRIPT_MARKER", StringComparison.Ordinal));
            });
            await Check("read_task_and_list_are_observations_not_execution_verification", async () =>
            {
                var list = await fixture.Execute(Call("list_tasks", new { }));
                Require(!list.Facts.GetProperty("executionVerified").GetBoolean()
                    && list.Facts.GetProperty("tasks").EnumerateArray().All(t => !t.GetProperty("executionVerified").GetBoolean())
                    && fixture.Transport.Sent.Count == 0);
            });
            await Check("task_list_pages_reach_all_tasks_with_bounded_unicode_results", async () =>
            {
                var original = fixture.Tasks;
                try
                {
                    fixture.Tasks = Enumerable.Range(1, 85).Select(i => new TaskRecord {
                        Id = new Guid(i, 0, 0, new byte[8]).ToString(), Host = "本机", Title = "分页任务" + i,
                        Project = "分页项目", Status = TaskStatusNames.Completed,
                        OneLineSummary = string.Concat(Enumerable.Repeat("😀", 300)) }).ToArray();
                    int offset = 0, pages = 0; var seen = new HashSet<string>();
                    while (true)
                    {
                        var result = await fixture.Execute(Call("list_tasks", new { project = "分页项目", offset }));
                        var facts = result.Facts;
                        Require(facts.GetProperty("total").GetInt32() == 85
                            && facts.GetRawText().Length <= ToolResultJson.DefaultMaximumResultCharacters / 2);
                        foreach (var task in facts.GetProperty("tasks").EnumerateArray())
                            Require(seen.Add(task.GetProperty("identity").GetProperty("ThreadId").GetString()!));
                        pages++;
                        if (facts.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
                        int next = facts.GetProperty("nextOffset").GetInt32(); Require(next > offset && pages <= 85); offset = next;
                    }
                    Require(seen.SetEquals(fixture.Tasks.Select(t => t.Id)) && pages > 2 && fixture.Transport.Sent.Count == 0);
                }
                finally { fixture.Tasks = original; }
            });
            await Check("task_plan_pages_preserve_step_order_and_status", async () =>
            {
                var original = local.Plan;
                try
                {
                    local.Plan = Enumerable.Range(1, 29).Select(i => new PlanStep { Step = "第" + i + "步", Status = i < 10 ? "completed" : "pending" }).ToList();
                    int offset = 0; var steps = new List<string>(); var states = new List<string>();
                    while (true)
                    {
                        var result = await fixture.Execute(Call("read_task", new { hostId = "local", threadId = local.Id, planOffset = offset }));
                        var facts = result.Facts;
                        Require(facts.GetProperty("totalPlanSteps").GetInt32() == 29);
                        foreach (var step in facts.GetProperty("plan").EnumerateArray())
                        {
                            steps.Add(step.GetProperty("step").GetString()!); states.Add(step.GetProperty("Status").GetString()!);
                            Require(!step.GetProperty("truncated").GetBoolean());
                        }
                        if (facts.GetProperty("planNextOffset").ValueKind == JsonValueKind.Null) break;
                        int next = facts.GetProperty("planNextOffset").GetInt32(); Require(next > offset); offset = next;
                    }
                    Require(steps.SequenceEqual(local.Plan.Select(p => p.Step)) && states.SequenceEqual(local.Plan.Select(p => p.Status))
                        && fixture.Transport.Sent.Count == 0);
                }
                finally { local.Plan = original; }
            });
            await Check("task_progress_pages_include_older_records_without_private_analysis", async () =>
            {
                var original = local.Timeline;
                try
                {
                    local.Timeline = Enumerable.Range(1, 17).Select(i => new TimelineEntry {
                        Time = i, Kind = "进展", Text = string.Concat(Enumerable.Repeat("😀", 700)) }).ToList();
                    local.Timeline.Add(new() { Time = 99, Kind = "分析", Text = "PRIVATE_UNEXPOSED_ANALYSIS" });
                    int offset = 0; var seen = new HashSet<double>();
                    while (true)
                    {
                        var result = await fixture.Execute(Call("read_task", new { hostId = "local", threadId = local.Id, progressOffset = offset }));
                        var facts = result.Facts; var page = facts.GetProperty("progress");
                        Require(facts.GetProperty("totalProgress").GetInt32() == 17
                            && facts.GetRawText().Length <= ToolResultJson.DefaultMaximumResultCharacters / 2
                            && !facts.GetRawText().Contains("PRIVATE_UNEXPOSED_ANALYSIS"));
                        foreach (var record in page.EnumerateArray())
                            Require(seen.Add(record.GetProperty("at").GetDouble()) && record.GetProperty("truncated").GetBoolean());
                        if (facts.GetProperty("progressNextOffset").ValueKind == JsonValueKind.Null) break;
                        int next = facts.GetProperty("progressNextOffset").GetInt32(); Require(next == offset + page.GetArrayLength() && next > offset); offset = next;
                    }
                    Require(seen.SetEquals(Enumerable.Range(1,17).Select(i => (double)i)) && fixture.Transport.Sent.Count == 0);
                }
                finally { local.Timeline = original; }
            });
            await Check("long_report_pages_preserve_available_text_and_upstream_truncation", async () =>
            {
                string original = local.FinalReport; bool truncated = local.FinalReportTruncated;
                try
                {
                    local.FinalReport = new string('字', 11999) + string.Concat(Enumerable.Repeat("😀<>&", 2000)) + "报告结尾";
                    local.FinalReportTruncated = true;
                    var combined = new System.Text.StringBuilder(); int offset = 0, pages = 0;
                    while (true)
                    {
                        var result = await fixture.Execute(Call("read_report", new { hostId = "local", threadId = local.Id, turnId = local.TurnId, offset }));
                        var report = result.Facts.GetProperty("report");
                        string text = report.GetProperty("text").GetString()!;
                        Require(report.GetProperty("sourceTruncated").GetBoolean() && report.GetProperty("truncated").GetBoolean()
                            && result.Facts.GetRawText().Length <= ToolResultJson.DefaultMaximumResultCharacters / 2);
                        combined.Append(text); pages++;
                        if (report.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
                        int next = report.GetProperty("nextOffset").GetInt32(); Require(next == offset + text.Length && next > offset); offset = next;
                    }
                    Require(pages > 1 && combined.ToString() == local.FinalReport);
                    Require((await fixture.Execute(Call("read_report", new { hostId = "local", threadId = local.Id, turnId = local.TurnId, offset = 12000 })))
                        .Facts.GetProperty("code").GetString() == "invalid_report_offset");
                }
                finally { local.FinalReport = original; local.FinalReportTruncated = truncated; }
            });
            await Check("report_requires_exact_task_host_and_turn", async () =>
            {
                var valid = await fixture.Execute(Call("read_report", new { hostId = "local", threadId = local.Id, turnId = local.TurnId }));
                Require(valid.State == ToolExecutionState.ReadOnlySuccess
                    && valid.Facts.GetProperty("report").GetProperty("text").GetString() == "PUBLIC_FINAL_REPORT_MARKER");
                Require(!valid.Facts.GetProperty("report").GetProperty("independentlyVerified").GetBoolean());
                var wrong = await fixture.Execute(Call("read_report", new { hostId = "local", threadId = local.Id, turnId = "wrong-turn" }));
                Require(wrong.State == ToolExecutionState.Rejected
                    && wrong.Facts.GetProperty("report").ValueKind == JsonValueKind.Null);
            });
            await Check("missing_task_is_rejected_without_fallback_or_transport", async () =>
            {
                var missing = await fixture.Execute(Call("read_task", new
                    { hostId = "local", threadId = "c8a0cd71-d073-4e78-8ab7-a543ce09f2d3" }));
                Require(missing.State == ToolExecutionState.Rejected
                    && missing.Facts.GetProperty("task").ValueKind == JsonValueKind.Null
                    && missing.Facts.GetProperty("progress").ValueKind == JsonValueKind.Null
                    && fixture.Transport.Sent.Count == 0);
            });
            await Check("missing_report_is_rejected_without_cross_host_fallback", async () =>
            {
                var missing = await fixture.Execute(Call("read_report", new
                    { hostId = "remote-ssh-discovered:fake-remote", threadId = remote.Id, turnId = remote.TurnId }));
                Require(missing.State == ToolExecutionState.Rejected
                    && missing.Facts.GetProperty("report").ValueKind == JsonValueKind.Null
                    && fixture.Transport.Sent.Count == 0);
            });
            await Check("capability_disabled_read_is_rejected_without_transport", async () =>
            {
                fixture.Settings.ChatGpt.IncludeTaskContext = false;
                Require((await fixture.Execute(Call("list_tasks", new { }))).State == ToolExecutionState.Rejected);
                fixture.Settings.ChatGpt.IncludeTaskContext = true;
                Require(fixture.Transport.Sent.Count == 0);
            });
            await Check("commitments_read_does_not_invent_schedules_or_subscriptions", async () =>
            {
                var read = await fixture.Execute(Call("list_commitments", new { }));
                Require(!read.Facts.GetProperty("schedulesImplemented").GetBoolean()
                    && !read.Facts.GetProperty("subscriptionsImplemented").GetBoolean());
            });
            await Check("send_without_core_operation_id_rejected", async () =>
                Require((await fixture.Execute(Send(local, text))).State == ToolExecutionState.Rejected));
            local.Status = TaskStatusNames.Active;
            await fixture.Queue.SubmitAsync(local, "synthetic pending instruction", "seed-current-queue", fixture.Snapshot(), default);
            string queueId = fixture.Queue.Entries.Single().Id;
            await Check("cancel_exact_queue_id_or_unique_prefix_accepted", async () =>
            {
                Require((await fixture.Authorize("取消 " + queueId, Call("cancel_queued", new { operationId = queueId }))).Allowed);
                Require((await fixture.Authorize("取消 " + queueId[..8], Call("cancel_queued", new { operationId = queueId }))).Allowed);
            });
            foreach (string rejected in new[]
            {
                "取消 " + queueId + " 是什么意思？", "取消 " + queueId + "，如果检查通过",
                "引用：取消 " + queueId, "能不能取消 " + queueId, "不要取消 " + queueId
            })
                await Check("cancel_nonimperative_or_conditional_denied_" + checks.Count, async () =>
                    Require(!(await fixture.Authorize(rejected, Call("cancel_queued", new { operationId = queueId }))).Allowed));
            await Check("cancel_unknown_queue_id_rejected", async () =>
                Require(!(await fixture.Authorize("取消 " + new string('a', 64),
                    Call("cancel_queued", new { operationId = new string('a', 64) }))).Allowed));
        }

        await Check("full_loop_queued_acceptance_is_not_codex_acceptance_and_ids_link", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-queued"));
            f.Tasks = new[] { f.Local }; f.Local.Status = TaskStatusNames.Active;
            string body = "继续 本机 Alpha 修复失败项";
            var evidence = await Loop(f, "queued-current-user", body, [Send(f.Local, body)]);
            var operation = evidence.Snapshot.Operations.Single();
            var entry = f.Queue.EntryForSource(operation.Id.ToString("N"))
                ?? throw new InvalidOperationException("core_operation_queue_entry_missing");
            var facts = Facts(evidence.Result.Observations.Single());
            Require(evidence.Result.State == ToolLoopState.Completed && operation.Stage == OperationStage.Accepted
                && operation.SourceMessageId == "queued-current-user" && operation.TargetKey == CodexGateway.Identity(f.Local).Key
                && entry.State == "queued" && entry.Id == facts.GetProperty("queueId").GetString()
                && entry.TaskKey == f.Local.Key && f.Queue.EntryForSource("queued-current-user") == null
                && facts.GetProperty("acceptedBy").GetString() == "assistant_queue"
                && !facts.GetProperty("codexAccepted").GetBoolean() && !facts.GetProperty("runningObserved").GetBoolean()
                && !facts.GetProperty("completionObserved").GetBoolean() && f.Transport.Sent.Count == 0);
            await using var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(f.LedgerPath!), new WindowsStateProtector());
            var restored = await reopened.ReadConversationAsync(Identity);
            Require(restored.Operations.Single().Id == operation.Id && restored.Operations.Single().Receipt!.Contains(entry.Id, StringComparison.Ordinal));
        });
        await Check("full_loop_backend_acceptance_preserves_original_text_and_never_claims_running", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-submitted"));
            string body = "继续 本机 Alpha 修复失败项；保留原来的范围。";
            var evidence = await Loop(f, "submitted-current-user", body, [Send(f.Local, body)]);
            var operation = evidence.Snapshot.Operations.Single();
            var entry = f.Queue.EntryForSource(operation.Id.ToString("N"))!;
            var facts = Facts(evidence.Result.Observations.Single());
            Require(operation.Stage == OperationStage.Accepted && facts.GetProperty("codexAccepted").GetBoolean()
                && facts.GetProperty("acceptedBy").GetString() == "codex_backend"
                && !facts.GetProperty("runningObserved").GetBoolean() && !facts.GetProperty("completionObserved").GetBoolean()
                && f.Transport.Sent.Single().Text == body
                && f.Transport.Sent.Single().Id == "wechat-" + entry.Id[..32]
                && operation.Task == Reference(f.Local) && evidence.Snapshot.Authorizations.Single().SourceExcerpt == body
                && evidence.Snapshot.Outbox.Single().Stage == OutboxStage.Pending);
        });
        await Check("full_loop_same_thread_remote_target_stays_on_explicit_host", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-remote"));
            string body = "继续 fake-remote Beta 修复失败项";
            var evidence = await Loop(f, "remote-current-user", body, [Send(f.Remote, body)]);
            Require(evidence.Snapshot.Operations.Single().Task == Reference(f.Remote)
                && evidence.Snapshot.Operations.Single().TargetKey == CodexGateway.Identity(f.Remote).Key
                && f.Transport.Sent.Single().Key == f.Remote.Key && f.Transport.Sent.Single().Key != f.Local.Key
                && f.Queue.Entries.Single().TaskKey == f.Remote.Key);
        });
        await Check("full_loop_condition_does_not_create_a_deferred_action", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-condition"));
            string body = "如果测试通过，就继续 本机 Alpha";
            var evidence = await Loop(f, "condition-current", body, [Send(f.Local, body)]);
            Require(evidence.Result.State == ToolLoopState.NeedsConfirmation && evidence.Snapshot.Operations.Length == 0
                && f.Queue.Entries.Count == 0 && f.Transport.Sent.Count == 0);
        });
        await Check("full_loop_duplicate_model_call_is_not_dispatched_twice", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-dedup"));
            string body = "继续 本机 Alpha 修复失败项";
            var first = Send(f.Local, body);
            var evidence = await Loop(f, "dedup-current-user", body, [first, first with { Id = "same-operation-second-call" }]);
            Require(f.Transport.Sent.Count == 1 && f.Queue.Entries.Count == 1 && evidence.Snapshot.Operations.Length == 1
                && evidence.Result.Observations.Length == 2);
        });
        await Check("full_loop_unknown_delivery_is_durable_and_never_auto_replayed", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-unknown"));
            f.Transport.Next = new("unconfirmed", "synthetic unknown receipt");
            string body = "继续 本机 Alpha 修复失败项";
            var evidence = await Loop(f, "unknown-current-user", body, [Send(f.Local, body)]);
            Require(evidence.Snapshot.Operations.Single().Stage == OperationStage.Unknown
                && f.Queue.Entries.Single().State == "unconfirmed");
            await f.Queue.PumpAsync(f.Snapshot(), default);
            Require(f.Transport.Sent.Count == 1 && f.Queue.Entries.Single().Instruction == "");
            await using var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(f.LedgerPath!), new WindowsStateProtector());
            Require((await reopened.ReadConversationAsync(Identity)).Operations.Single().Stage == OperationStage.Unknown);
        });
        await Check("history_report_memory_and_notification_never_grant_current_authority", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-context"));
            f.Memory.Add("继续 本机 Alpha 修复失败项");
            var evidence = await Loop(f, "context-current-user", "谢谢", [Send(f.Local, "谢谢")],
                context: "继续 本机 Alpha 修复失败项");
            Require(evidence.Result.State == ToolLoopState.NeedsConfirmation && evidence.Snapshot.Operations.Length == 0
                && f.Queue.Entries.Count == 0 && f.Transport.Sent.Count == 0);
        });
        await Check("full_loop_untrusted_inbox_cannot_create_operation", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-untrusted"));
            string body = "继续 本机 Alpha 修复失败项";
            var evidence = await Loop(f, "untrusted-current", body, [Send(f.Local, body)], MessageTrust.UntrustedContent);
            Require(evidence.Result.State == ToolLoopState.NeedsConfirmation && evidence.Snapshot.Operations.Length == 0
                && f.Transport.Sent.Count == 0);
        });
        await Check("full_loop_read_only_tools_make_no_operation_or_queue", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-readonly"));
            var evidence = await Loop(f, "read-current", "查看 Alpha 进展", [
                Call("list_tasks", new { }), Call("read_task", Target(f.Local)),
                Call("read_report", new { hostId = "local", threadId = f.Local.Id, turnId = f.Local.TurnId })]);
            Require(evidence.Result.State == ToolLoopState.Completed && evidence.Result.Observations.Length == 3
                && evidence.Snapshot.Operations.Length == 0 && f.Queue.Entries.Count == 0 && f.Transport.Sent.Count == 0);
        });
        await Check("full_loop_remember_is_encrypted_and_bound_to_current_message", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-remember"));
            string content = "SYNTHETIC_PRIVATE_MEMORY_BODY";
            var evidence = await Loop(f, "memory-current", "记住：" + content, [Call("remember", new { text = content, scope = "personal" })]);
            Require(evidence.Snapshot.Operations.Single().Stage == OperationStage.Completed
                && evidence.Snapshot.Operations.Single().SourceMessageId == "memory-current"
                && f.Memory.Entries.Single().Text == content);
            var reread = new AssistantMemoryStore(f.MemoryRoot, () => "synthetic-tools-owner");
            Require(reread.Entries.Single().Text == content
                && !Encoding.UTF8.GetString(File.ReadAllBytes(f.LedgerPath!)).Contains(content, StringComparison.Ordinal)
                && !File.ReadAllText(f.Memory.DataPath!).Contains(content, StringComparison.Ordinal));
        });
        await Check("full_loop_memory_update_changes_only_selected_entry", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-update"));
            var chosen = f.Memory.Add("synthetic original"); var other = f.Memory.Add("synthetic unchanged");
            var evidence = await Loop(f, "update-current", "修改记忆 " + chosen.Id + " synthetic replacement",
                [Call("update_memory", new { memoryId = chosen.Id, text = "synthetic replacement" })]);
            Require(evidence.Snapshot.Operations.Single().Stage == OperationStage.Completed
                && f.Memory.Entries.Single(m => m.Id == chosen.Id).Text == "synthetic replacement"
                && f.Memory.Entries.Single(m => m.Id == other.Id).Text == "synthetic unchanged");
        });
        await Check("full_loop_forget_does_not_claim_dialogue_erasure", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-forget"));
            var chosen = f.Memory.Add("synthetic forget");
            var evidence = await Loop(f, "forget-current", "删除记忆 " + chosen.Id,
                [Call("forget", new { memoryId = chosen.Id })]);
            Require(evidence.Snapshot.Operations.Single().Stage == OperationStage.Completed && f.Memory.Entries.Count == 0
                && !Facts(evidence.Result.Observations.Single()).GetProperty("historicalDialogueErased").GetBoolean()
                && evidence.Snapshot.Inbox.Single().SourceMessageId == "forget-current");
        });
        await Check("full_loop_cancel_only_waiting_queue_and_not_running_task", async () =>
        {
            await using var f = new Fixture(Path.Combine(directory, "loop-cancel"));
            f.Local.Status = TaskStatusNames.Active;
            await f.Queue.SubmitAsync(f.Local, "synthetic waiting request", "seeded-queue-source", f.Snapshot(), default);
            string id = f.Queue.Entries.Single().Id;
            var evidence = await Loop(f, "cancel-current", "取消 " + id, [Call("cancel_queued", new { operationId = id })]);
            Require(evidence.Snapshot.Operations.Single().Stage == OperationStage.Completed
                && f.Queue.Entries.Single().State == "cancelled" && f.Transport.Sent.Count == 0
                && !Facts(evidence.Result.Observations.Single()).GetProperty("interruptedRunningTurn").GetBoolean());
        });
        if (failures != 0) throw new InvalidOperationException("assistant_tools_checks_failed");
    }

    private static readonly ConversationIdentity Identity = new("synthetic-tools-user", "synthetic-tools-binding", "main");
    private static object Target(TaskRecord task) => new { hostId = CodexGateway.Identity(task).HostId, threadId = task.Id };
    private static TaskReference Reference(TaskRecord task) => new(CodexGateway.Identity(task).HostId, task.Id);
    private static ModelToolCall Send(TaskRecord task, string text) => Call("send_to_task",
        new { hostId = CodexGateway.Identity(task).HostId, threadId = task.Id, text });
    private static ModelToolCall Call(string name, object arguments) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(arguments));
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("assertion_failed"); }
    private static JsonElement Facts(ModelToolObservation observation)
        => JsonSerializer.Deserialize<JsonElement>(observation.ResultJson).GetProperty("facts");

    private static async Task<LoopEvidence> Loop(Fixture fixture, string source, string body, ModelToolCall[] calls,
        MessageTrust trust = MessageTrust.AuthenticatedUser, string? context = null)
    {
        await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(fixture.LedgerPath!), new WindowsStateProtector());
        if (context != null)
        {
            await ledger.ReceiveAsync(Identity, "historical-user", context, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow.AddMinutes(-1));
            await ledger.AppendContextAsync(Identity, ConversationSource.Report, context, DateTimeOffset.UtcNow);
            await ledger.AppendContextAsync(Identity, ConversationSource.Notification, context, DateTimeOffset.UtcNow);
        }
        if (trust == MessageTrust.AuthenticatedUser)
            fixture.Queue.ObserveAuthenticatedInput(source, DurableAssistantRuntime.IsKnownReadonlyInput(body));
        await ledger.ReceiveAsync(Identity, source, body, trust, DateTimeOffset.UtcNow);
        var model = new ScriptedModel(calls);
        var executor = new AssistantToolExecutor(fixture.Settings, fixture.Gateway, fixture.Queue, fixture.Memory,
            fixture.Snapshot, (identity, _) => Task.FromResult(fixture.Context ?? new ConversationContext(identity, 0, 0, 0)),
            manualMemory: new(fixture.Settings, fixture.Memory, ledger, identity => identity == Identity));
        var loop = new ToolAgentLoop(ledger, model, AssistantToolExecutor.Catalog(), executor, executor);
        var result = await loop.RunAsync(Identity, source);
        Require(model.Calls == 2 && model.LastRequest?.CurrentMessage.SourceMessageId == source);
        return new(result, await ledger.ReadConversationAsync(Identity));
    }

    private sealed record LoopEvidence(ToolLoopResult Result, ConversationSnapshot Snapshot);
    private sealed class ScriptedModel(ModelToolCall[] tools) : IToolModel
    {
        internal int Calls;
        internal ToolModelRequest? LastRequest;
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; LastRequest = request;
            return Task.FromResult(Calls == 1 ? ToolModelResponse.Call(tools) : ToolModelResponse.Reply("这是合成测试的自然回复。"));
        }
    }
    private sealed class FakeTransport : ITaskTransport
    {
        internal List<(string Key, string Text, string Id)> Sent { get; } = new();
        internal TaskDelivery Next = new("submitted", "synthetic backend acceptance", "synthetic-next-turn");
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Sent.Add((task.Key, text, messageId)); return Task.FromResult(Next);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal Settings Settings { get; }
        internal TaskRecord Local { get; }
        internal TaskRecord Remote { get; }
        internal TaskRecord[] Tasks { get; set; }
        internal FakeTransport Transport { get; } = new();
        internal CodexGateway Gateway { get; }
        internal TaskControlQueue Queue { get; }
        internal AssistantMemoryStore Memory { get; }
        internal AssistantToolExecutor Executor { get; }
        internal string? LedgerPath { get; }
        internal string? MemoryRoot { get; }
        internal ConversationContext? Context { get; set; }
        internal Fixture(string? directory)
        {
            if (directory != null) Directory.CreateDirectory(directory);
            string syntheticHome = directory == null ? Path.Combine(AppContext.BaseDirectory, "unused-synthetic-codex-home")
                : Path.Combine(directory, "unused-synthetic-codex-home");
            Settings = new()
            {
                PythonPath = Path.Combine(syntheticHome, "never-start.exe"), CodexHome = syntheticHome,
                LocalEnabled = true, RemoteHosts = new() { "fake-remote" },
                ChatGpt = new() { Enabled = true, TaskControlEnabled = true, IncludeTaskContext = true, PersonalizationEnabled = true },
                WeChat = new() { ClawBotEnabled = true, PushPlusEnabled = false }
            };
            Settings.Normalize();
            Local = new()
            {
                Id = "70c9574d-9df0-4d2a-881f-af2c32a62a01", Host = "本机", Title = "Alpha", Project = "CommonProject",
                Cwd = Path.Combine(syntheticHome, "local-project"), TurnId = "synthetic-local-turn",
                Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed,
                OneLineSummary = "PUBLIC_SUMMARY_MARKER", FinalReport = "PUBLIC_FINAL_REPORT_MARKER",
                FinalReportTurnId = "synthetic-local-turn", FinalReportSource = "synthetic-final-event",
                Timeline = new()
                {
                    new() { Kind = "分析", Text = "PRIVATE_ANALYSIS_MARKER" },
                    new() { Kind = "命令", Text = "RAW_COMMAND_MARKER" },
                    new() { Kind = "raw", Text = "RAW_TRANSCRIPT_MARKER" },
                    new() { Kind = "进展", Text = "PUBLIC_PROGRESS_MARKER" }
                },
                Plan = new() { new() { Step = "PUBLIC_PLAN_MARKER", Status = "completed" } }
            };
            Remote = new()
            {
                Id = Local.Id, Host = "fake-remote", Title = "Beta", Project = "CommonProject",
                Cwd = Path.Combine(syntheticHome, "remote-project"), TurnId = "synthetic-remote-turn",
                Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed
            };
            Tasks = new[] { Local, Remote };
            Gateway = new(Settings, Snapshot, Transport);
            Queue = new(Settings, Gateway, directory == null ? null : Path.Combine(directory, "synthetic-queue.state"));
            Queue.SetOwner("synthetic-tools-owner");
            MemoryRoot = directory == null ? null : Path.Combine(directory, "synthetic-memory");
            Memory = new(MemoryRoot, () => "synthetic-tools-owner");
            Executor = new(Settings, Gateway, Queue, Memory, Snapshot,
                (identity, _) => Task.FromResult(Context ?? new ConversationContext(identity, 0, 0, 0)));
            LedgerPath = directory == null ? null : Path.Combine(directory, "synthetic-conversation.state");
        }
        internal WeChatSnapshot Snapshot()
        {
            var now = DateTimeOffset.UtcNow;
            return new(Tasks, now, Tasks.Select(t => t.Host).Distinct(StringComparer.Ordinal)
                .Select(h => new HostSnapshot { Name = h, LastSuccess = now }).ToArray());
        }
        internal Task<ToolExecutionResult> Execute(ModelToolCall call)
            => Executor.ExecuteAsync(new(Identity, "synthetic-direct-read", 1, Guid.NewGuid(), null, null), call, default);
        internal ValueTask<UserAuthorizationDecision> Authorize(string body, ModelToolCall call,
            MessageTrust trust = MessageTrust.AuthenticatedUser, TaskReference? task = null)
        {
            var now = DateTimeOffset.UtcNow;
            var message = new InboxMessage(Guid.NewGuid(), Identity, "synthetic-current", body, trust,
                now, now, 1, InboxStage.Processing, Task: task);
            Require(AssistantToolExecutor.Catalog().TryGet(call.Name, out var descriptor));
            return Executor.AuthorizeAsync(new(message, call, descriptor), default);
        }
        public ValueTask DisposeAsync() => Queue.DisposeAsync();
    }
}
