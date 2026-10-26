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
using System.Windows.Threading;
using Assistant.Core;
using ChannelConnectionState = Codex.Protocol.ConnectionState;

namespace CodexDashboard;

// Real host coordinator, DPAPI ledger and channel parser; all HTTP and model
// calls are injected. This entry point runs before production configuration.
internal static class DurableRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--durable-runtime-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Explicit output directory required.");
            await RunAsync(args[index + 1]); exit(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("Absolute output directory required.");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("New absolute output directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output directory rejected.");
        if (Application.Current?.Dispatcher.CheckAccess() != true)
            throw new InvalidOperationException("The fixture requires the application's active WPF dispatcher.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "runtime-checks.json"), JsonSerializer.Serialize(new
            { networkUsed = false, realAccountUsed = false, actualModelUsed = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        foreach (bool enabled in new[] { false, true })
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, enabled ? "cancel-missing-model-on" : "cancel-missing-model-off"), modelEnabled: enabled);
            await f.InputAsync("cancel-missing", "取消 deadbeef");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            var saved = await f.ReadAsync();
            Check(enabled ? "missing_cancel_is_fixed_with_model" : "missing_cancel_is_fixed_without_model",
                f.Sent.Last().Contains("没有找到") && f.Sent.Last().Contains("工作列表")
                && saved.Operations.IsEmpty && f.Queue.Entries.Count == 0 && f.Script.Calls == 0);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "work-list-pages"), modelEnabled: false);
            f.Settings.ChatGpt.TaskControlEnabled = true;
            f.ObservedTask.Status = TaskStatusNames.Active;
            await f.InputAsync("work-list-source", "让 Alpha 修改代码");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            for (int i = 0; i < 25; i++)
                await f.Queue.SubmitDurableAsync(f.ObservedTask, "合成排队要求 " + i, "work-list-operation-" + i,
                    WeChatText.Hash("work-list-source"), f.CurrentSnapshot(), default);
            var entries = f.Queue.Entries;
            Check("work_list_fixture_has_25_queued_entries", entries.Count == 25 && entries.All(e => e.State == "queued"));
            for (int page = 1; page <= 3; page++)
            {
                await f.InputAsync("work-page-" + page, "工作列表 " + page);
                await WaitAsync(async () =>
                {
                    var state = await f.ReadAsync();
                    var input = state.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("work-page-" + page));
                    var generation = state.Generations.SingleOrDefault(g => g.InboxIds.Contains(input.Id));
                    if (generation == null || generation.PreparedSegments.IsEmpty) return false;
                    var output = state.Outbox.Where(o => o.GenerationId == generation.Id).ToArray();
                    return output.Length == generation.PreparedSegments.Length && output.All(o => o.Stage == OutboxStage.Accepted);
                });
                var state = await f.ReadAsync();
                var input = state.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("work-page-" + page));
                var generation = state.Generations.Single(g => g.InboxIds.Contains(input.Id));
                string reply = string.Join("\n", state.Outbox.Where(o => o.GenerationId == generation.Id).OrderBy(o => o.SegmentIndex).Select(o => o.Body));
                File.WriteAllText(Path.Combine(directory, "work-page-" + page + ".txt"), reply);
                Check("work_page_" + page + "_contains_only_requested_complete_ids",
                    reply.Contains("第 " + page + "/3 页") && entries.Select((e, i) =>
                        reply.Contains(e.Id) == (i / 12 + 1 == page)).All(value => value)
                    && entries.Skip((page - 1) * 12).Take(12).All(e => reply.Contains(e.Instruction)));
            }
            Check("work_pages_do_not_hold_or_dispatch_queue", f.Queue.Entries.All(e => e.State == "queued") && f.Script.Calls == 0);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "operation-pages"), modelEnabled: false);
            for (int i = 0; i < 13; i++)
            {
                await f.InputAsync("operation-seed-" + i, i % 2 == 0 ? "开启自动记忆" : "关闭自动记忆");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == i + 1);
            }
            var operations = (await f.ReadAsync()).Operations.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id).ToArray();
            for (int page = 1; page <= 2; page++)
            {
                await f.InputAsync("operation-page-" + page, "操作记录 第" + page + "页");
                await WaitAsync(async () =>
                {
                    var saved = await f.ReadAsync();
                    var input = saved.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("operation-page-" + page));
                    var generation = saved.Generations.SingleOrDefault(g => g.InboxIds.Contains(input.Id));
                    if (generation == null || generation.PreparedSegments.IsEmpty) return false;
                    var outputs = saved.Outbox.Where(o => o.GenerationId == generation.Id).ToArray();
                    return outputs.Length == generation.PreparedSegments.Length && outputs.All(o => o.Stage == OutboxStage.Accepted);
                });
                var saved = await f.ReadAsync();
                var input = saved.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("operation-page-" + page));
                var generation = saved.Generations.Single(g => g.InboxIds.Contains(input.Id));
                string reply = string.Join("\n", saved.Outbox.Where(o => o.GenerationId == generation.Id).OrderBy(o => o.SegmentIndex).Select(o => o.Body));
                Check("operation_page_" + page + "_contains_requested_ids_only",
                    reply.Contains("第 " + page + "/2 页") && operations.Select((o, i) =>
                        reply.Contains(o.Id.ToString("N")) == (i / 12 + 1 == page)).All(value => value));
            }
            Check("operation_pages_are_readonly_and_no_model", (await f.ReadAsync()).Operations.Length == 13 && f.Script.Calls == 0
                && DurableAssistantRuntime.IsKnownReadonlyInput("操作记录 第2页")
                && !DurableAssistantRuntime.IsKnownReadonlyInput("操作记录 第2页，继续任务"));
        }
        const string privateBody = "SYNTHETIC_PRIVATE_INBOX_ASCII_72c401ce";
        string acceptedLedger;
        ConversationIdentity acceptedIdentity;
        Guid acceptedInboxId;
        long acceptedRevision;
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "accept"));
            await f.InputAsync("natural-1", privateBody);
            var accepted = await f.ReadAsync();
            Check("inbox_committed_before_seen_persistence", f.PersistObservedCommittedInbox
                && f.PersistNeverPrecedesInbox && accepted.Inbox.Single().Body == privateBody);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            var replied = await f.ReadAsync();
            Check("real_runtime_model_to_channel_outbox", f.Script.Calls == 1 && f.Sent.Single() == "我在。"
                && replied.Events.Any(e => e.Source == ConversationSource.Assistant));
            Check("actual_send_uses_new_inbound_context_token", f.SentContexts.Single() == "SYNTHETIC_NEW_CONTEXT"
                && f.PersistedBinding().ContextToken == "SYNTHETIC_NEW_CONTEXT");
            await f.InputAsync("natural-1", privateBody);
            Check("duplicate_channel_message_keeps_one_inbox", (await f.ReadAsync()).Inbox.Length == 1);
            acceptedIdentity = f.Runtime.Identity!;
            acceptedInboxId = accepted.Inbox.Single().Id;
            acceptedRevision = accepted.Revision;
            acceptedLedger = f.LedgerPath;
            // Bypass the channel Seen list to exercise the production ledger's own
            // source-id deduplication path through Runtime.AcceptAsync.
            var replay = await f.Runtime.AcceptAsync(f.Settings.WeChat.ReadBinding()!, WeChatText.Hash("natural-1"),
                privateBody, DateTimeOffset.UtcNow, default);
            Check("ledger_duplicate_replay_keeps_original_identity_and_revision",
                replay.IsDuplicate && replay.Message.Id == acceptedInboxId && replay.CurrentRevision == acceptedRevision);
            await f.Runtime.StopSessionAsync();
            var drained = await f.Ledger.ReadConversationAsync(acceptedIdentity);
            Check("duplicate_message_never_regenerates_after_real_drain",
                f.Script.Calls == 1 && f.Sent.Count == 1 && drained.Inbox.Length == 1 && drained.Outbox.Length == 1);
            using var stored = new FileStream(f.LedgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bytes = new MemoryStream();
            await stored.CopyToAsync(bytes);
            Check("raw_private_body_encrypted", drained.Inbox.Single().Body == privateBody
                && !Encoding.UTF8.GetString(bytes.ToArray()).Contains(privateBody, StringComparison.Ordinal));
        }
        await using (var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(acceptedLedger), new WindowsStateProtector()))
        {
            var replay = await reopened.ReceiveAsync(acceptedIdentity, WeChatText.Hash("natural-1"), privateBody,
                MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var saved = await reopened.ReadConversationAsync(acceptedIdentity);
            Check("ledger_duplicate_after_file_reopen_remains_one_completed_reply",
                replay.IsDuplicate && replay.Message.Id == acceptedInboxId && replay.CurrentRevision == acceptedRevision
                && saved.Inbox.Length == 1 && saved.Outbox.Length == 1 && saved.Outbox.Single().Stage == OutboxStage.Accepted
                && saved.Operations.IsEmpty);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "quick"), modelEnabled: false);
            await f.InputAsync("quick-1", "状态");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            Check("fixed_query_works_without_model", f.Script.Calls == 0 && f.Sent.Count == 1);
            await f.InputAsync("quick-2", "先停一下");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 2);
            Check("stop_fast_path_does_not_claim_backend_interrupt", f.Script.Calls == 0 && f.Sent.Last().Contains("不能从这里中断"));
            foreach (string query in new[] { "渠道能力", "媒体能力" })
            {
                string source = "quick-" + query;
                await f.InputAsync(source, query);
                await OwnedHostRuntimeTests.WaitReplyAsync(f, source);
                Check("capability_query_without_model_" + query, f.Script.Calls == 0
                    && DurableAssistantRuntime.IsKnownReadonlyInput(query)
                    && f.Sent.Last().Contains("模型问答：尚未开启")
                    && f.Sent.Last().Contains("语音回复：当前尚未开放")
                    && !f.Sent.Last().Contains(f.Binding.Token, StringComparison.Ordinal)
                    && !f.Sent.Last().Contains(f.Binding.ContextToken, StringComparison.Ordinal)
                    && (await f.ReadAsync()).Operations.IsEmpty && f.Queue.Entries.Count == 0);
            }
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "automatic-memory-fast"), modelEnabled: false);
            f.Settings.ChatGpt.AutoMemoryEnabled = false;
            var kept = f.Model.Memory.Add("SYNTHETIC_MANUALLY_SAVED_PREFERENCE");
            await f.InputAsync("automatic-enable", "开启自动记忆");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 1);
            var enabled = await f.ReadAsync();
            Check("automatic_memory_toggle_works_without_model_with_durable_authority",
                f.Settings.ChatGpt.AutoMemoryEnabled && f.Script.Calls == 0
                && enabled.Operations.Single().Action == "set_auto_memory"
                && enabled.Operations.Single().Stage == OperationStage.Completed
                && enabled.ToolInvocations.Single().OperationId == enabled.Operations.Single().Id
                && enabled.Authorizations.Single().SourceMessageId == WeChatText.Hash("automatic-enable"));
            await f.InputAsync("automatic-disable", "关闭自动记忆");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 2);
            Check("automatic_memory_disable_preserves_existing_manual_memory",
                !f.Settings.ChatGpt.AutoMemoryEnabled && f.Model.Memory.Entries.Single().Id == kept.Id
                && f.Sent.Last().Contains("已有记忆") && (await f.ReadAsync()).Operations.Length == 2);
            await f.InputAsync("automatic-enable", "开启自动记忆");
            Check("replayed_toggle_cannot_undo_a_later_user_choice",
                !f.Settings.ChatGpt.AutoMemoryEnabled && (await f.ReadAsync()).Operations.Length == 2);
            await f.InputAsync("automatic-quoted", "解释“开启自动记忆”是什么意思");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 3);
            Check("quoted_toggle_without_model_does_not_mutate_settings",
                !f.Settings.ChatGpt.AutoMemoryEnabled && f.Script.Calls == 0
                && (await f.ReadAsync()).Operations.Length == 2 && f.Model.Memory.Entries.Single().Id == kept.Id);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "local-memory-fast"), modelEnabled: false);
            await f.InputAsync("local-remember", "记住：以后汇报先说结论");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 1);
            var remembered = await f.ReadAsync();
            var entry = f.Model.Memory.Entries.Single();
            var operation = remembered.Operations.Single();
            Check("manual_memory_without_model_has_full_invocation_audit",
                f.Script.Calls == 0 && entry.Text == "以后汇报先说结论" && operation.Stage == OperationStage.Completed
                && remembered.ToolInvocations.Single().OperationId == operation.Id
                && remembered.ToolInvocations.Single().Stage == ToolInvocationStage.Completed
                && remembered.Authorizations.Single().SourceMessageId == WeChatText.Hash("local-remember"));
            await f.InputAsync("local-remember", "记住：以后汇报先说结论");
            Check("duplicate_manual_memory_has_one_operation",
                f.Model.Memory.Entries.Count == 1 && (await f.ReadAsync()).Operations.Length == 1);
            await f.InputAsync("local-list-page", "记忆 1");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 2);
            Check("memory_page_alias_is_readonly_without_model", f.Script.Calls == 0
                && f.Sent.Last().Contains(entry.Id) && (await f.ReadAsync()).Operations.Length == 1
                && DurableAssistantRuntime.IsKnownReadonlyInput("记忆 1"));
            await f.InputAsync("local-edit", "修改记忆 " + entry.Id + " 以后按问题分别回答");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 3);
            Check("manual_memory_edit_preserves_id_and_audits_source",
                f.Model.Memory.Entries.Single().Id == entry.Id && f.Model.Memory.Entries.Single().Text == "以后按问题分别回答"
                && (await f.ReadAsync()).Operations.Single(o => o.Action == "update_memory").SourceMessageId == WeChatText.Hash("local-edit"));
            await f.InputAsync("local-operation", "操作记录 " + operation.Id.ToString("N")[..8]);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 4);
            Check("operation_query_returns_full_id_without_new_operations", f.Sent.Last().Contains(operation.Id.ToString("N"))
                && f.Sent.Last().Contains("登记：") && (await f.ReadAsync()).Operations.Length == 2 && f.Script.Calls == 0);
            await f.InputAsync("local-forget", "删除记忆 " + entry.Id);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 5);
            var forgotten = await f.ReadAsync();
            Check("manual_memory_delete_retains_truthful_history_boundary", f.Model.Memory.Entries.Count == 0
                && f.Sent.Last().Contains("历史对话") && forgotten.Inbox.Length == 5
                && forgotten.ToolInvocations.Length == 3 && forgotten.ToolInvocations.All(i => i.OperationId != null));
            var desktop = await f.Runtime.ReadWorkspaceAsync();
            Check("desktop_workspace_reads_actual_audit_without_actions", desktop.Operations.Count == 3
                && desktop.Operations.Any(item => item.Id == operation.Id.ToString("N"))
                && desktop.Messages.Count == 5 && desktop.Messages.All(item => item.State.Contains("手机送达未核实"))
                && (await f.ReadAsync()).Operations.Length == 3 && f.Script.Calls == 0);
            await f.InputAsync("local-read-not-control", "查看记忆，删除全部");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 6);
            Check("readonly_memory_syntax_does_not_accept_control_suffix",
                !DurableAssistantRuntime.IsKnownReadonlyInput("查看记忆，删除全部")
                && (await f.ReadAsync()).Operations.Length == 3 && f.Script.Calls == 0);
            await f.Runtime.StopSessionAsync();
            var stoppedDesktop = await f.Runtime.ReadWorkspaceAsync();
            Check("desktop_workspace_does_not_show_old_binding_after_stop",
                stoppedDesktop.Operations.Count == 0 && stoppedDesktop.Work.Count == 0 && stoppedDesktop.Messages.Count == 0);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "natural-manual-memory"), modelEnabled: false);
            await f.InputAsync("natural-memory-add", "帮我记住以后技术汇报先说结论");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 1);
            var first = f.Model.Memory.Entries.Single();
            Check("natural_manual_memory_keeps_exact_content_without_model",
                first.Text == "以后技术汇报先说结论" && f.Script.Calls == 0
                && (await f.ReadAsync()).Authorizations.Single().SourceExcerpt == "帮我记住以后技术汇报先说结论");
            await f.InputAsync("natural-memory-edit", "把记忆 " + first.Id + " 改为 以后技术汇报包含验证范围");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 2);
            Check("natural_manual_update_preserves_stable_id",
                f.Model.Memory.Entries.Single().Id == first.Id && f.Model.Memory.Entries.Single().Text == "以后技术汇报包含验证范围");
            await f.InputAsync("natural-memory-delete", "忘记关于技术汇报的记忆");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 3);
            Check("unique_keyword_delete_is_bound_to_resolved_memory",
                f.Model.Memory.Entries.Count == 0 && (await f.ReadAsync()).Operations.Single(o => o.Action == "forget").Stage == OperationStage.Completed);
            var one = f.Model.Memory.Add("共同关键词：项目甲");
            var two = f.Model.Memory.Add("共同关键词：项目乙");
            await f.InputAsync("natural-memory-ambiguous", "删除记忆 共同关键词");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 4);
            Check("ambiguous_memory_lists_ids_without_creating_task_selection_or_operation",
                f.Model.Memory.Entries.Count == 2 && f.Sent.Last().Contains(one.Id) && f.Sent.Last().Contains(two.Id)
                && (await f.ReadAsync()).Operations.Length == 3
                && (await f.Ledger.ReadConversationContextAsync(f.Runtime.Identity!)).PendingSelection == null);
            await f.InputAsync("natural-memory-digit", "1");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 5);
            Check("ambiguous_memory_does_not_authorize_numeric_followup",
                f.Model.Memory.Entries.Count == 2 && (await f.ReadAsync()).Operations.Length == 3 && f.Script.Calls == 0);
            await f.InputAsync("natural-memory-explicit", "删除记忆 " + two.Id);
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 6);
            Check("clarified_memory_requires_new_explicit_id_request",
                f.Model.Memory.Entries.Single().Id == one.Id && (await f.ReadAsync()).Operations.Length == 4);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "memory-target-race"), modelEnabled: false);
            var chosen = f.Model.Memory.Add("SYNTHETIC_ORIGINAL_TARGET");
            var received = await f.Ledger.ReceiveAsync(f.Runtime.Identity!, "memory-target-source",
                "删除记忆 " + chosen.Id, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            bool observedDispatch = false;
            f.Fault.ObserveCommitted = committed =>
            {
                if (!observedDispatch && committed.GetProperty("operations").EnumerateArray().Any(operation =>
                        operation.GetProperty("sourceMessageId").GetString() == "memory-target-source"
                        && operation.GetProperty("stage").GetString() == nameof(OperationStage.Dispatching)))
                {
                    observedDispatch = true;
                    f.Model.Memory.Update(chosen.Id, "SYNTHETIC_REPLACEMENT_MUST_SURVIVE");
                }
            };
            var execute = typeof(DurableAssistantRuntime).GetMethod("ExecuteLocalFastAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<DurableFastReply>)execute.Invoke(f.Runtime,
                [new DurableFastContext(received.Message, f.Ledger, () => true),
                    new ModelToolCall("memory-target-call", "forget", JsonSerializer.Serialize(new { memoryId = chosen.Id })),
                    CancellationToken.None])!;
            f.Fault.ObserveCommitted = null;
            var saved = await f.ReadAsync();
            File.WriteAllText(Path.Combine(directory, "memory-target-race-observed.json"), JsonSerializer.Serialize(new
            {
                observedDispatch, saved.Operations, saved.ToolInvocations, saved.Generations,
                entries = f.Model.Memory.Entries, syntheticDataOnly = true
            }, Settings.Json));
            Check("changed_memory_after_durable_dispatch_is_not_deleted",
                observedDispatch && f.Model.Memory.Entries.Single().Id == chosen.Id
                && f.Model.Memory.Entries.Single().Text == "SYNTHETIC_REPLACEMENT_MUST_SURVIVE"
                && saved.Operations.Single().Stage == OperationStage.Failed
                && saved.ToolInvocations.Single().ResultJson!.Contains("memory_target_changed"));
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "memory-binding-race"), modelEnabled: false);
            var originalIdentity = f.Runtime.Identity!;
            string originalAccount = f.Settings.ChatGpt.ActiveAccount;
            f.Model.Memory.Add("SYNTHETIC_OLD_OWNER_MEMORY");
            f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_SECOND_ACCOUNT";
            f.Model.Memory.Add("SYNTHETIC_NEW_OWNER_MEMORY");
            f.Settings.ChatGpt.ActiveAccount = originalAccount;
            var received = await f.Ledger.ReceiveAsync(originalIdentity, "race-memory-source",
                "记住：以后按问题分别回答", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            bool observedDispatch = false;
            f.Fault.ObserveCommitted = committed =>
            {
                // Switch after the invocation+operation are durably marked as
                // dispatching, before the synchronous memory adapter executes.
                if (!observedDispatch && committed.GetProperty("operations").EnumerateArray().Any(operation =>
                        operation.GetProperty("sourceMessageId").GetString() == "race-memory-source"
                        && operation.GetProperty("stage").GetString() == nameof(OperationStage.Dispatching)))
                {
                    observedDispatch = true;
                    f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_SECOND_ACCOUNT";
                }
            };
            var execute = typeof(DurableAssistantRuntime).GetMethod("ExecuteLocalFastAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<DurableFastReply>)execute.Invoke(f.Runtime,
                [new DurableFastContext(received.Message, f.Ledger, () => true),
                    new ModelToolCall("race-call", "remember", """{"text":"以后按问题分别回答","scope":"personal"}"""),
                    CancellationToken.None])!;
            f.Fault.ObserveCommitted = null;
            var saved = await f.Ledger.ReadConversationAsync(originalIdentity);
            Check("binding_change_after_durable_dispatch_prevents_local_memory_mutation",
                observedDispatch && f.Model.Memory.Entries.Single().Text == "SYNTHETIC_NEW_OWNER_MEMORY"
                && saved.Operations.Single().Stage == OperationStage.Failed
                && saved.ToolInvocations.Single().ResultJson!.Contains("local_fast_source_changed"));
            var readonlyCall = typeof(DurableAssistantRuntime).GetMethod("ReadonlyFastAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            bool blocked = false;
            try
            {
                await (Task<DurableFastReply?>)readonlyCall.Invoke(f.Runtime,
                    [received.Message with { Body = "查看记忆" }, CancellationToken.None])!;
            }
            catch (OperationCanceledException) { blocked = true; }
            Check("stale_memory_read_cannot_cross_into_new_profile", blocked && f.Sent.Count == 0 && f.Script.Calls == 0);
            f.Settings.ChatGpt.ActiveAccount = originalAccount;
            Check("rejected_late_memory_does_not_mutate_original_profile",
                f.Model.Memory.Entries.Single().Text == "SYNTHETIC_OLD_OWNER_MEMORY");
            var changedBinding = f.Settings.WeChat.ReadBinding()!;
            changedBinding.UserId = "SYNTHETIC_SECOND_USER";
            f.Settings.WeChat.WriteBinding(changedBinding);
            bool inputBlocked = false;
            try { await f.Runtime.AcceptAsync(f.Binding, "stale-channel-source", "查看记忆", DateTimeOffset.UtcNow, default); }
            catch (InvalidOperationException) { inputBlocked = true; }
            Check("settings_binding_change_fences_old_receiver_before_runtime_stop",
                inputBlocked && (await f.Ledger.ReadConversationAsync(originalIdentity)).Inbox.Length == 1);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "constraint-fence"));
            f.Settings.ChatGpt.TaskControlEnabled = true;
            f.ObservedTask.Status = TaskStatusNames.Active;
            await f.InputAsync("fence-original", "让 Alpha 修改代码");
            await f.Queue.SubmitDurableAsync(f.ObservedTask, "让 Alpha 修改代码", "synthetic-fence-operation",
                WeChatText.Hash("fence-original"), f.CurrentSnapshot(), default);
            Check("runtime_current_input_allows_busy_task_to_queue", f.Queue.Entries.Single().State == "queued");
            await f.InputAsync("fence-readonly", "状态");
            Check("runtime_fixed_query_keeps_existing_queue_authority", f.Queue.Entries.Single().State == "queued");
            await f.InputAsync("fence-readonly-report", "报告 Alpha");
            await f.InputAsync("fence-readonly-inspect", "查看 Alpha");
            Check("runtime_parameter_queries_keep_existing_queue_authority", f.Queue.Entries.Single().State == "queued");
            await f.InputAsync("fence-dependencies", "依赖列表");
            await f.InputAsync("fence-dependencies-page", " 依赖列表 2 ");
            Check("dependency_list_and_pages_keep_existing_queue_authority", f.Queue.Entries.Single().State == "queued");
            Check("dependency_list_control_suffix_is_not_readonly",
                !DurableAssistantRuntime.IsKnownReadonlyInput("依赖列表 2，暂停 Alpha")
                && !DurableAssistantRuntime.IsKnownReadonlyInput("依赖列表 2\n不要执行"));
            await f.InputAsync("fence-query-and-pause", "报告 Alpha，暂停");
            Check("runtime_query_with_new_control_clause_closes_dispatch_fence",
                f.Queue.Entries.Single().State == "held_constraints_changed");
            await f.InputAsync("fence-restriction", "只分析，别修改代码");
            f.ObservedTask.Status = TaskStatusNames.Completed;
            await f.Queue.PumpAsync(f.CurrentSnapshot(), default);
            var entry = f.Queue.Entries.Single();
            Check("runtime_new_constraint_holds_old_instruction_before_channel_ack",
                entry.State == "held_constraints_changed" && entry.Instruction == "让 Alpha 修改代码"
                && f.PersistNeverPrecedesInbox && (await f.ReadAsync()).Inbox.Any(i => i.Body == "只分析，别修改代码"));
            await f.Queue.SubmitDurableAsync(f.ObservedTask, "旧生成迟到", "synthetic-late-operation",
                WeChatText.Hash("fence-original"), f.CurrentSnapshot(), default);
            Check("runtime_old_generation_cannot_enter_queue_after_new_input", f.Queue.Entries.Count == 1);
        }
        {
            var attentionTransport = new ControlReceiptRuntimeTests.CapturingTransport();
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "context-commands"), modelEnabled: false,
                taskTransport: attentionTransport);
            var identity = f.Runtime.Identity!;
            await f.InputAsync("context-list", "任务列表");
            await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).PendingSelection != null);
            var listed = await f.Ledger.ReadConversationContextAsync(identity);
            Check("task_list_persists_frozen_selection_without_changing_focus",
                listed.Focus == null && listed.PendingSelection!.Candidates.Single().Task.ThreadId == f.ObservedTask.Id);
            await f.InputAsync("context-number", "1");
            await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).Focus != null);
            var chosen = await f.Ledger.ReadConversationContextAsync(identity);
            Check("literal_number_sets_focus_without_authorization_or_operation",
                chosen.Focus!.Task.ThreadId == f.ObservedTask.Id && chosen.PendingSelection == null
                && (await f.ReadAsync()).Operations.IsEmpty && (await f.ReadAsync()).Authorizations.IsEmpty);
            await f.Runtime.EnqueueNotificationAsync("synthetic-other-notice", "另一项工作有结果了。",
                new("local", "other-synthetic-thread", "other-synthetic-turn"), default);
            await f.InputAsync("context-inspect", "查看 其他任务");
            var afterRead = await f.Ledger.ReadConversationContextAsync(identity);
            Check("notification_and_read_query_do_not_replace_explicit_focus",
                afterRead.Focus == chosen.Focus && afterRead.LastNotification?.Task?.ThreadId == "other-synthetic-thread");
            await f.InputAsync("context-reset", "新对话");
            await WaitAsync(async () => (await f.Ledger.ReadConversationContextAsync(identity)).Epoch == 1);
            var reset = await f.ReadAsync();
            Check("new_dialogue_is_durable_boundary_without_history_deletion_or_identity_change",
                reset.Context!.Focus == null && reset.Context.PendingSelection == null && reset.Context.LastNotification == null
                && reset.Context.ResetSourceMessageId == WeChatText.Hash("context-reset")
                && reset.Inbox.Any(i => i.SourceMessageId == WeChatText.Hash("context-list"))
                && reset.Events.Any(e => e.ExternalEventKey == "synthetic-other-notice") && f.Runtime.Identity == identity);
            await f.InputAsync("context-number-after-reset", "1");
            await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("没有待选择"))));
            Check("old_selection_cannot_reappear_after_new_dialogue", (await f.Ledger.ReadConversationContextAsync(identity)).Focus == null
                && (await f.ReadAsync()).Operations.IsEmpty && f.Script.Calls == 0);
            f.Settings.ChatGpt.Enabled = f.Settings.ChatGpt.TaskControlEnabled = true;
            await f.Queue.SubmitDurableAsync(f.ObservedTask, "SYNTHETIC_ATTENTION_SOURCE", "synthetic-attention-operation",
                WeChatText.Hash("context-number-after-reset"), f.CurrentSnapshot(), default);
            f.Settings.ChatGpt.Enabled = f.Settings.ChatGpt.TaskControlEnabled = false;
            var attentionProof = attentionTransport.Provenance ?? throw new InvalidOperationException("Missing attention dispatch proof.");
            var attentionTask = attentionProof.Task with { TurnId = attentionTransport.Next.TurnId };
            await f.Runtime.EnqueueControlReceiptAsync(new("synthetic-attention-id", "另一项工作在等输入。", attentionTask,
                AttentionScope: new(attentionProof, attentionTransport.Next.TurnId)), default);
            var withAttention = await f.ReadAsync();
            Check("attention_receipt_preserves_canonical_task_without_inventing_turn_or_operation",
                withAttention.Context!.LastNotification?.Task == attentionTask
                && withAttention.Events.Any(e => e.ExternalEventKey == "control/synthetic-attention-id" && e.Task == attentionTask)
                && withAttention.Operations.IsEmpty);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "selection-label"), modelEnabled: false);
            f.ObservedTask.Title = "Alpha\nsecond\tpart";
            await f.InputAsync("multiline-list", "任务列表");
            await WaitAsync(async () => (await f.ReadAsync()).Context!.PendingSelection != null);
            var choice = (await f.ReadAsync()).Context!.PendingSelection!.Candidates.Single();
            Check("selection_label_is_single_line_without_changing_original_task",
                !choice.Label.Any(char.IsControl) && choice.Label.Contains("Alpha second part", StringComparison.Ordinal)
                && f.ObservedTask.Title == "Alpha\nsecond\tpart");
            await f.InputAsync("multiline-choose", "1");
            await WaitAsync(async () => (await f.ReadAsync()).Context!.Focus != null);
            Check("multiline_task_remains_selectable_by_saved_canonical_identity",
                (await f.ReadAsync()).Context!.Focus!.Task.ThreadId == f.ObservedTask.Id && (await f.ReadAsync()).Operations.IsEmpty);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "selection-conflict"), modelEnabled: false);
            f.ObservedTasks = [f.ObservedTask, new TaskRecord { Id = f.ObservedTask.Id, Host = f.ObservedTask.Host,
                Title = "conflicting duplicate", Status = TaskStatusNames.Completed, Cwd = f.ObservedTask.Cwd }];
            await f.InputAsync("duplicate-list", "任务列表");
            await WaitAsync(() => Task.FromResult(f.Sent.Any(s => s.Contains("身份冲突", StringComparison.Ordinal))));
            Check("conflicting_duplicate_tasks_are_not_arbitrarily_selected",
                (await f.ReadAsync()).Context!.PendingSelection == null && (await f.ReadAsync()).Context!.Focus == null
                && f.Script.Calls == 0);
        }
        {
            var blocked = new TaskCompletionSource<ToolModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "supersede"));
            f.Script.Handler = (_, _) => blocked.Task;
            try
            {
                await f.InputAsync("long-1", "帮我解释一下这个问题");
                await WaitAsync(() => Task.FromResult(f.Script.Calls == 1));
                await f.InputAsync("query-2", "任务列表");
                await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
                Check("query_not_blocked_by_ignoring_cancellation_model", f.Sent.Count == 1 && f.Sent.First().Contains("Alpha"));
            }
            finally { blocked.TrySetResult(ToolModelResponse.Reply("OLD_REPLY_MUST_NOT_SEND")); }
            await f.Runtime.StopSessionAsync();
            Check("superseded_generation_never_sends_old_reply", !f.Sent.Contains("OLD_REPLY_MUST_NOT_SEND"));
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "unknown"));
            f.SendOutcome = "{}";
            await f.InputAsync("uncertain-1", "在吗");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Unknown));
            await f.Runtime.StopSessionAsync();
            await f.Runtime.ActivateAsync(f.Settings.WeChat.ReadBinding()!, default);
            f.SendOutcome = """{"ret":0}""";
            await f.InputAsync("after-restart", "状态");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            var identity = f.Runtime.Identity!;
            await f.Runtime.StopSessionAsync();
            var drained = await f.Ledger.ReadConversationAsync(identity);
            Check("unknown_channel_receipt_not_replayed_on_session_restart", f.Sent.Count == 2
                && f.Sent.Count(body => body == "我在。") == 1
                && drained.Outbox.Count(o => o.Stage == OutboxStage.Unknown) == 1
                && drained.Outbox.Count(o => o.Stage == OutboxStage.Accepted) == 1);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "binding"));
            var original = f.Runtime.Identity;
            var rotated = f.Settings.WeChat.ReadBinding()!;
            rotated.Token = "SYNTHETIC_ROTATED";
            Check("token_rotation_preserves_conversation_identity",
                DurableAssistantRuntime.BindingIdentity(rotated, f.Settings.ChatGpt.ActiveAccount) == original);
            rotated.UserId = "SYNTHETIC_OTHER_USER";
            await f.Runtime.StopSessionAsync();
            await f.Runtime.ActivateAsync(rotated, default);
            Check("different_user_isolated_conversation", f.Runtime.Identity != original);
            bool rejected = false;
            try { await f.Runtime.AcceptAsync(f.Binding, "stale-binding", "继续 Alpha", DateTimeOffset.UtcNow, default); }
            catch (InvalidOperationException) { rejected = true; }
            Check("old_binding_rejected", rejected);
            var oldState = await f.Ledger.ReadConversationAsync(original!);
            var newState = await f.ReadAsync();
            Check("wrong_binding_creates_no_inbox_or_operation", oldState.Inbox.IsEmpty && oldState.Operations.IsEmpty
                && newState.Inbox.IsEmpty && newState.Operations.IsEmpty && f.Script.Calls == 0 && f.Sent.Count == 0);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "failed-storage"));
            f.Fault.Armed = true;
            bool rejected = false;
            try { await f.InputAsync("not-committed", "继续 Alpha"); }
            catch (IOException) { rejected = true; }
            Check("inbox_fault_propagates", rejected);
            Check("failed_inbox_not_marked_seen", !f.Settings.WeChat.ReadBinding()!.SeenMessages.Contains(WeChatText.Hash("not-committed"))
                && !f.PersistedBinding().SeenMessages.Contains(WeChatText.Hash("not-committed"))
                && f.Script.Calls == 0 && f.Sent.Count == 0);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "missing-runtime"));
            await f.Runtime.StopSessionAsync();
            f.Channel.DurableRuntime = null;
            try
            {
                bool rejected = false;
                try { await f.InputAsync("unread", "状态"); }
                catch (IOException) { rejected = true; }
                Check("missing_runtime_fails_before_seen", rejected);
                Check("missing_runtime_preserves_unread_message",
                    f.Settings.WeChat.ReadBinding()!.SeenMessages.Count == 0
                    && f.PersistedBinding().SeenMessages.Count == 0 && f.Sent.Count == 0);
            }
            finally { f.Channel.DurableRuntime = f.Runtime; }
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "receive-success"),
                modelEnabled: false, receiveMessages: true);
            f.PollResponses.Writer.TryWrite(f.Batch("polled-success", "状态", "SYNTHETIC_CURSOR_NEXT", "SYNTHETIC_POLLED_CONTEXT"));
            await WaitAsync(() => Task.FromResult(f.PersistedBinding().Cursor == "SYNTHETIC_CURSOR_NEXT"));
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
            await WaitAsync(() => Task.FromResult(f.PollCursors.Count >= 2));
            var state = await f.ReadAsync();
            Check("real_receive_loop_commits_inbox_before_seen_and_cursor", f.PersistNeverPrecedesInbox
                && f.PersistObservedCommittedInbox && f.CursorAdvancedAfterCommittedInbox
                && state.Inbox.Single().SourceMessageId == WeChatText.Hash("polled-success")
                && f.PersistedBinding().SeenMessages.Contains(WeChatText.Hash("polled-success")));
            Check("next_real_poll_uses_committed_cursor", f.PollCursors.ToArray().Take(2)
                .SequenceEqual(new[] { "SYNTHETIC_CURSOR_INITIAL", "SYNTHETIC_CURSOR_NEXT" }));
            Check("polled_message_uses_fresh_context_in_actual_send", f.SentContexts.Single() == "SYNTHETIC_POLLED_CONTEXT"
                && f.Script.Calls == 0);
            await f.StopChannelAsync();
            await using var reacquired = ReceiverLease.TryAcquire(f.Binding.BotId);
            Check("isolated_receiver_loop_drains_and_releases_its_lease",
                f.StartHints == 1 && f.StopHints == 1 && f.Channel.WhenStopped.IsCompletedSuccessfully
                && reacquired != null);
        }
        {
            await using var f = await Fixture.CreateAsync(Path.Combine(directory, "receive-failed-inbox"),
                modelEnabled: false, receiveMessages: true);
            f.Fault.Armed = true;
            f.PollResponses.Writer.TryWrite(f.Batch("polled-failed", "继续 Alpha", "SYNTHETIC_MUST_NOT_COMMIT"));
            await WaitAsync(() => Task.FromResult(f.Channel.ConnectionState == ChannelConnectionState.Disconnected));
            // The actual receiver waits five seconds after a storage failure, then
            // asks again using its retained cursor. The second request is blocked
            // by our fake handler until shutdown cancellation.
            await WaitAsync(() => Task.FromResult(f.PollCursors.Count >= 2));
            var state = await f.ReadAsync();
            Check("receive_storage_failure_keeps_seen_and_cursor_uncommitted",
                f.PersistedBinding().Cursor == "SYNTHETIC_CURSOR_INITIAL"
                && f.PersistedBinding().SeenMessages.Count == 0
                && f.Settings.WeChat.ReadBinding()!.Cursor == "SYNTHETIC_CURSOR_INITIAL"
                && state.Inbox.IsEmpty && state.Operations.IsEmpty && f.Sent.Count == 0 && f.Script.Calls == 0);
            Check("actual_receiver_retry_keeps_original_cursor", f.PollCursors.ToArray().Take(2)
                .SequenceEqual(new[] { "SYNTHETIC_CURSOR_INITIAL", "SYNTHETIC_CURSOR_INITIAL" }));
            await f.StopChannelAsync();
        }
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Synthetic host condition not reached.");
            await Task.Delay(20);
        }
    }
    internal sealed class ScriptedModel : IToolModel
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Func<ToolModelRequest, CancellationToken, Task<ToolModelResponse>> Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("我在。"));
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls); return Handler(request, cancellationToken); }
    }
    internal sealed class Fault : IJournalFaultInjector
    {
        internal volatile bool Armed;
        internal Action<JournalWriteStage>? Observe;
        internal Action<JsonElement>? ObserveCommitted;
        internal JsonElement? CandidateSnapshot;
        public void At(JournalWriteStage stage, long sequence)
        {
            Observe?.Invoke(stage);
            if (stage == JournalWriteStage.AfterFlush && CandidateSnapshot is { } committed)
                ObserveCommitted?.Invoke(committed);
            if (Armed && stage == JournalWriteStage.BeforeWrite) throw new IOException("synthetic_inbox_flush_failure");
        }
    }
    // Test-only observation of the exact synthetic transaction after its real
    // Flush(true). Keep the real DPAPI implementation and protection purpose.
    private sealed class ObservedProtector(Fault fault) : IStateProtector
    {
        private readonly WindowsStateProtector inner = new();
        public string Scheme => inner.Scheme;
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
        {
            fault.CandidateSnapshot = null;
            if (fault.ObserveCommitted != null)
            {
                using var document = JsonDocument.Parse(plaintext.ToArray());
                fault.CandidateSnapshot = document.RootElement.Clone();
            }
            return inner.Protect(plaintext, purpose);
        }
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose) =>
            inner.Unprotect(ciphertext, purpose);
    }
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request, ct);
    }
    private sealed class NoTransport : ITaskTransport
    {
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
            => throw new InvalidOperationException("These tests must not invoke task control.");
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal Settings Settings = null!;
        internal ClawBinding Binding = null!;
        internal WeChatService Channel = null!;
        internal DurableAssistantRuntime Runtime = null!;
        internal DurableConversationLedger Ledger = null!;
        internal TaskControlQueue Queue = null!;
        internal ChatGptAssistant Model = null!;
        internal ChatGptAuth Auth = null!;
        internal TaskRecord ObservedTask = null!;
        internal TaskRecord[] ObservedTasks = [];
        internal Func<WeChatSnapshot> CurrentSnapshot = null!;
        internal readonly ScriptedModel Script = new();
        internal readonly Fault Fault = new();
        internal readonly ConcurrentQueue<string> Sent = new();
        internal readonly ConcurrentQueue<string> SentContexts = new();
        internal readonly ConcurrentQueue<string> PollCursors = new();
        internal readonly System.Threading.Channels.Channel<string> PollResponses
            = System.Threading.Channels.Channel.CreateUnbounded<string>();
        internal string SendOutcome = """{"ret":0}""";
        internal string LedgerPath = "";
        internal string BindingPath = "";
        internal bool PersistObservedCommittedInbox;
        internal bool PersistNeverPrecedesInbox = true;
        internal bool CursorAdvancedAfterCommittedInbox;
        private int startHints, stopHints;
        internal int StartHints => Volatile.Read(ref startHints);
        internal int StopHints => Volatile.Read(ref stopHints);
        internal static async Task<Fixture> CreateAsync(string directory, bool modelEnabled = true, bool receiveMessages = false,
            ICompletionSummarizer? completionSummarizer = null, ClawBinding? savedBinding = null, bool completionSummariesEnabled = false,
            OwnedHostOptions? ownedOptions = null, ITaskTransport? taskTransport = null, IClock? clock = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? channelRequestOverride = null,
            LedgerOptions? ledgerOptions = null, Action? channelPersistAction = null,
            DurableMessageOptions? coordinatorOptions = null, TaskRecord? syntheticTask = null,
            int? productionMergeWindowMilliseconds = null, ExistingHostOptions? existingOptions = null, bool archiveLedger = false,
            bool persistMemory = false, AssistantInboundAttachments? attachments = null, AssistantArtifactStore? artifacts = null, Func<string, AssistantImageInput, CancellationToken, Task<string>>? imageAnalyzer = null)
        {
            if (Application.Current?.Dispatcher.CheckAccess() != true)
                throw new InvalidOperationException("Fixture access must start on the WPF dispatcher.");
            Directory.CreateDirectory(directory);
            var f = new Fixture();
            f.Settings = new()
            {
                CodexHome = Path.Combine(directory, "unused-home"),
                InputMergeWindowMilliseconds = productionMergeWindowMilliseconds ?? 1000,
                RemoteHosts = syntheticTask != null && syntheticTask.Host != "本机" ? new() { syntheticTask.Host } : new(), LocalEnabled = true,
                ChatGpt = new() { Enabled = modelEnabled, IncludeTaskContext = true, ActiveAccount = "SYNTHETIC_ACCOUNT",
                    TaskControlEnabled = false, PersonalizationEnabled = true, CompletionSummariesEnabled = completionSummariesEnabled },
                WeChat = new() { ClawBotEnabled = true, PushPlusEnabled = false }
            };
            f.Settings.Normalize();
            // The receiver mutex is real, but its bot identity is freshly generated
            // for this fixture and cannot be a configured production binding.
            f.Binding = savedBinding ?? new() { BotId = "synthetic-runtime-" + Guid.NewGuid().ToString("N"),
                UserId = "synthetic-user", Token = "SYNTHETIC_TOKEN",
                Cursor = "SYNTHETIC_CURSOR_INITIAL", ContextToken = "SYNTHETIC_CONTEXT", BaseUrl = WeChatApi.DefaultBaseUrl };
            f.Settings.WeChat.WriteBinding(f.Binding);
            f.BindingPath = Path.Combine(directory, "synthetic-binding.state");
            File.WriteAllText(f.BindingPath, f.Settings.WeChat.ProtectedBinding);
            var task = syntheticTask ?? new TaskRecord { Id = "synthetic-task", Title = "Alpha", Host = "本机", Status = TaskStatusNames.Completed,
                RecordedStatus = TaskStatusNames.Completed, TurnId = "synthetic-turn", Cwd = directory, Project = "Synthetic" };
            string observedHost = syntheticTask?.Host ?? "本机";
            WeChatSnapshot Snapshot() => new(f.ObservedTasks, clock?.UtcNow ?? DateTimeOffset.UtcNow,
                new[] { new HostSnapshot { Name = observedHost, LastSuccess = clock?.UtcNow ?? DateTimeOffset.UtcNow } });
            f.ObservedTask = task; f.ObservedTasks = [task]; f.CurrentSnapshot = Snapshot;
            var gateway = new CodexGateway(f.Settings, Snapshot, taskTransport ?? new NoTransport(),
                clock == null ? null : () => clock.UtcNow);
            f.Queue = new(f.Settings, gateway, Path.Combine(directory, "synthetic-queue.state"),
                workspace: ownedOptions?.Workspace);
            f.Auth = new(Path.Combine(directory, "absent-synthetic.credentials"),
                new Stub((_, _) => throw new InvalidOperationException("No authentication network expected.")));
            f.Model = new(f.Settings, () => { }, f.Auth,
                new Stub((_, _) => throw new InvalidOperationException("Scripted IToolModel must be used.")),
                memoryStore: persistMemory ? new AssistantMemoryStore(Path.Combine(directory, "synthetic-memory"),
                    () => f.Settings.ChatGpt.ActiveAccount + "\n" + f.Binding.BaseUrl + "\n" + f.Binding.BotId + "\n" + f.Binding.UserId) : null);
            var contextProvider = new AssistantModelContextProvider(f.Settings, f.Model.Memory, clock);
            f.LedgerPath = Path.Combine(directory, "synthetic-dialogue.state");
            HistoryArchiveSet? archive = null;
            if (archiveLedger)
            {
                string archiveDirectory = Path.Combine(directory, "history-archive");
                Directory.CreateDirectory(archiveDirectory);
                archive = await HistoryArchiveSet.OpenAsync(archiveDirectory, new WindowsStateProtector(), createNew: true);
            }
            f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(f.LedgerPath, faultInjector: f.Fault),
                new ObservedProtector(f.Fault), clock: clock,
                options: (ledgerOptions ?? new()) with { RequireOutputDependencies = true }, dependencyValidator: contextProvider,
                historyArchive: archive);
            f.Channel = new(f.Settings, Snapshot, () =>
            {
                channelPersistAction?.Invoke();
                // The service's persisted Seen marker is allowed only after the
                // corresponding source is visible in the committed ledger.
                var saved = f.Settings.WeChat.ReadBinding()!;
                if (saved.SeenMessages.Count > 0 && f.Runtime?.Identity is { } identity)
                {
                    var committed = f.Ledger.ReadConversationAsync(identity).GetAwaiter().GetResult();
                    bool known = saved.SeenMessages.All(id => committed.Inbox.Any(m => m.SourceMessageId == id));
                    f.PersistObservedCommittedInbox |= known;
                    f.PersistNeverPrecedesInbox &= known;
                    if (saved.Cursor != "SYNTHETIC_CURSOR_INITIAL") f.CursorAdvancedAfterCommittedInbox |= known;
                }
                else if (saved.Cursor != "SYNTHETIC_CURSOR_INITIAL") f.PersistNeverPrecedesInbox = false;
                // Exercise actual persistence of the synthetic binding only; do
                // not invoke Settings.Save or its default application directory.
                File.WriteAllText(f.BindingPath, f.Settings.WeChat.ProtectedBinding);
            }, new Stub(async (request, ct) =>
            {
                if (channelRequestOverride != null && await channelRequestOverride(request, ct) is { } injected)
                    return injected;
                string route = request.RequestUri!.AbsolutePath;
                if (route.EndsWith("/sendmessage", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var message = body.RootElement.GetProperty("msg");
                    f.Sent.Enqueue(message.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString()!);
                    f.SentContexts.Enqueue(message.GetProperty("context_token").GetString()!);
                    return Response(f.SendOutcome);
                }
                if (receiveMessages && route.EndsWith("/getupdates", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    f.PollCursors.Enqueue(body.RootElement.GetProperty("get_updates_buf").GetString()!);
                    // Cancellation terminates this fake long poll during actual
                    // WeChatService shutdown. No live request is ever delegated.
                    return Response(await f.PollResponses.Reader.ReadAsync(ct));
                }
                if (receiveMessages && route.EndsWith("/notifystart", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref f.startHints);
                    return Response("""{"ret":0}""");
                }
                if (receiveMessages && route.EndsWith("/notifystop", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref f.stopHints);
                    return Response("""{"ret":0}""");
                }
                throw new InvalidOperationException("Unexpected network route in isolated fixture.");
            }), receiveMessages: receiveMessages, assistant: f.Model, summarizer: completionSummarizer, requireDurableAssistant: true);
            f.Runtime = new(f.Settings, f.Model, f.Queue, gateway, f.Channel, Dispatcher.CurrentDispatcher, f.Ledger, Snapshot,
                f.Script, coordinatorOptions ?? (productionMergeWindowMilliseconds.HasValue ? null
                    : new() { MergeWindow = TimeSpan.FromMilliseconds(20), DeliveryTimeout = TimeSpan.FromSeconds(3) }),
                ownedOptions, clock, contextProvider, existingOptions, attachments, artifacts, imageAnalyzer);
            f.Channel.DurableRuntime = f.Runtime;
            await f.Channel.ConfigureAsync();
            return f;
        }
        internal Task InputAsync(string id, string body)
        {
            using var root = JsonDocument.Parse(Batch(id, body));
            return Channel.ProcessUpdatesAsync(root.RootElement.Clone(), default);
        }
        internal string Batch(string id, string body, string cursor = "", string context = "SYNTHETIC_NEW_CONTEXT")
            => JsonSerializer.Serialize(new
            {
                ret = 0, get_updates_buf = cursor,
                msgs = new[] { new { from_user_id = Binding.UserId, to_user_id = Binding.BotId, message_type = 1,
                    message_id = id, create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), context_token = context,
                    item_list = new[] { new { type = 1, text_item = new { text = body } } } } }
            });
        private static HttpResponseMessage Response(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        internal ClawBinding PersistedBinding()
            => JsonSerializer.Deserialize<ClawBinding>(LocalSecret.Unprotect(File.ReadAllText(BindingPath)), Settings.Json)!;
        internal Task<ConversationSnapshot> ReadAsync() => Ledger.ReadConversationAsync(Runtime.Identity!);
        internal async Task StopChannelAsync()
        {
            await Channel.DisposeAsync();
            await Channel.WhenStopped;
        }
        public async ValueTask DisposeAsync()
        {
            await StopChannelAsync();
            await Runtime.DisposeAsync(); await Queue.DisposeAsync(); Model.Dispose(); Auth.Dispose();
        }
    }
}
