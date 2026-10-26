using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class OperationDetailsRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--operation-details-runtime-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--operation-details-runtime-test")
                throw new ArgumentException("Explicit isolated output required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private sealed class NoControl : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("No task dispatch permitted in this fixture."); }
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Fresh absolute output required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "operation-details-runtime-checks.json"), JsonSerializer.Serialize(new
            { actualHost = true, networkUsed = false, actualModelUsed = false, productionDataRead = false, checks }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await LocalOperationAsync(Path.Combine(directory, "local-operation"), Check);
        await RecordedStagesAsync(Path.Combine(directory, "saved-stages"), Check);
    }
    private static async Task LocalOperationAsync(string path, Action<string, bool> check)
    {
        var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, taskTransport: control);
        string original = "两小时后提醒我核对操作记录 " + new string('文', 690) + "😀 原话末尾";
        await f.InputAsync("actual-local-operation", original);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, "actual-local-operation");
        var saved = await f.ReadAsync(); var operation = saved.Operations.Single();
        var invocation = saved.ToolInvocations.Single(item => item.OperationId == operation.Id);
        int sends = f.Sent.Count;
        var workspace = await f.Runtime.ReadWorkspaceAsync();
        var detail = workspace.OperationDetails!.Single();
        check("actual_workspace_message_details_preserve_saved_body_and_receipt_boundary", workspace.MessageDetails!.Count == saved.Outbox.Length
            && saved.Outbox.All(message => workspace.MessageDetails.Any(item => item.Identity == saved.Identity
                && item.Id == message.Id.ToString("N") && item.Body.Contains(message.Body)
                && item.Body.Contains("接口受理不等于手机送达"))));
        check("actual_fast_tool_operation_and_detail_are_same_exact_saved_identity_and_id",
            workspace.Identity == saved.Identity && workspace.Operations.Single().Id == operation.Id.ToString("N")
            && detail.Identity == saved.Identity && detail.OperationId == operation.Id
            && detail == OperationDetails.Read(saved, operation.Id));
        check("actual_saved_user_request_is_complete_and_not_the_preview",
            detail.Body.Contains(original, StringComparison.Ordinal) && original.Length > 680
            && detail.Body.Contains("原话末尾") && detail.Body.Contains("这里展示历史原话，不表示它仍然适用"));
        check("completed_local_step_is_not_claimed_as_project_or_test_success",
            operation.Stage == OperationStage.Completed && invocation.Stage == ToolInvocationStage.Completed
            && detail.Body.Contains("操作已结束，验证结果须另核对") && detail.Body.Contains("工具步骤已结束")
            && detail.Body.Contains("操作结束不等于整个项目完成"));
        _ = await f.Runtime.ReadWorkspaceAsync(); _ = await f.Runtime.ReadWorkspaceAsync();
        var after = await f.ReadAsync();
        check("operation_detail_reads_leave_entire_actual_ledger_and_all_external_adapters_unchanged",
            JsonSerializer.Serialize(saved, Settings.Json) == JsonSerializer.Serialize(after, Settings.Json)
            && f.Sent.Count == sends && f.Script.Calls == 0 && control.Calls == 0 && f.Queue.Entries.Count == 0);
        check("operation_detail_preserves_commitment_and_conversation_pages",
            workspace.CommitmentDetails!.Single().Body.Contains(saved.WorkItems.Single().Goal)
            && workspace.Conversation!.Any(row => row.Body == original)
            && workspace.ConversationRecordCount == saved.Inbox.Length + saved.Outbox.Length);
        string account = f.Settings.ChatGpt.ActiveAccount;
        f.Settings.ChatGpt.ActiveAccount = "SYNTHETIC_OPERATION_ACCOUNT_B";
        bool blocked = false;
        try { _ = await f.Runtime.ReadWorkspaceAsync(); }
        catch (InvalidOperationException error) when (error.Message == "workspace_binding_changed") { blocked = true; }
        catch (OperationCanceledException error) when (error.Message == "当前微信绑定已变化。") { blocked = true; }
        check("changed_account_cannot_return_old_operation_or_original_request", blocked
            && (await f.Ledger.ReadConversationAsync(saved.Identity)).Operations.Single() == operation
            && f.Script.Calls == 0 && control.Calls == 0);
        await f.Runtime.ActivateAsync(f.Binding, default);
        var other = await f.Runtime.ReadWorkspaceAsync();
        check("new_identity_operation_list_and_details_do_not_borrow_prior_records",
            other.Identity != saved.Identity && other.Operations.Count == 0 && other.OperationDetails!.Count == 0);
        f.Settings.ChatGpt.ActiveAccount = account;
    }
    private static async Task RecordedStagesAsync(string path, Action<string, bool> check)
    {
        var control = new NoControl();
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path, modelEnabled: false, taskTransport: control);
        var identity = f.Runtime.Identity!;
        var stageRecords = new Dictionary<OperationStage, OperationRecord>();
        // These are synthetic saved facts committed through the real ledger API.
        // No remote dispatch or result is fabricated by the production reader.
        foreach (var stage in new[] { OperationStage.Queued, OperationStage.Accepted, OperationStage.Running,
            OperationStage.Completed, OperationStage.Failed, OperationStage.Interrupted, OperationStage.Unknown })
        {
            string source = "synthetic-recorded-" + stage;
            string body = "隔离记录：请接续目标 " + stage + "\n完整请求末尾 😀";
            var task = new TaskReference("synthetic-local", "synthetic-thread-" + stage, "synthetic-turn-" + stage, "synthetic-profile");
            await f.Ledger.ReceiveAsync(identity, source, body, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow, task: task);
            var grant = await f.Ledger.AuthorizeCurrentUserRequestAsync(identity, source, "send_to_task", source,
                "{\"text\":\"RAW_OPERATION_ARGUMENTS_SHOULD_NOT_APPEAR\"}", "请接续目标 " + stage, task);
            var operation = await f.Ledger.QueueOperationAsync(identity, grant.Id, source);
            if (stage != OperationStage.Queued)
            {
                await f.Ledger.BeginOperationDispatchAsync(identity, operation.Id);
                operation = await f.Ledger.RecordOperationObservationAsync(identity, operation.Id, stage,
                    "RAW_OPERATION_RECEIPT_SHOULD_NOT_APPEAR");
            }
            stageRecords.Add(stage, operation);
        }
        var before = await f.ReadAsync(); int sends = f.Sent.Count;
        var workspace = await f.Runtime.ReadWorkspaceAsync();
        string Body(OperationStage stage) => workspace.OperationDetails!.Single(item => item.OperationId == stageRecords[stage].Id).Body;
        check("saved_stage_matrix_details_keep_exact_thread_turn_and_raw_fields_private",
            workspace.OperationDetails!.Count == stageRecords.Count && stageRecords.All(pair =>
                Body(pair.Key).Contains(pair.Value.Task!.ThreadId) && Body(pair.Key).Contains(pair.Value.Task!.TurnId!)
                && Body(pair.Key).Contains("完整请求末尾 😀") && !Body(pair.Key).Contains("RAW_OPERATION_")));
        check("saved_unknown_remains_unknown_and_is_never_presented_as_failure_or_retry",
            Body(OperationStage.Unknown).Contains("结果未知，等待核对")
            && Body(OperationStage.Unknown).Contains("不会自动重发")
            && !Body(OperationStage.Unknown).Contains("操作失败"));
        check("accepted_and_running_are_not_relabelled_completed",
            Body(OperationStage.Accepted).Contains("尚不能据此认定已经运行、完成或测试通过")
            && Body(OperationStage.Running).Contains("原任务之后的变化可能尚未反映在这里")
            && !Body(OperationStage.Accepted).Contains("当前保存的状态：操作已结束")
            && !Body(OperationStage.Running).Contains("当前保存的状态：操作已结束"));
        check("interrupted_and_failed_guidance_does_not_reexecute_or_claim_process_shutdown",
            Body(OperationStage.Interrupted).Contains("不代表所有派生进程都已结束")
            && Body(OperationStage.Failed).Contains("失败记录不会自动授权重试"));
        _ = await f.Runtime.ReadWorkspaceAsync();
        check("saved_unknown_and_all_other_records_are_bitwise_unchanged_by_actual_workspace_reads",
            JsonSerializer.Serialize(before, Settings.Json) == JsonSerializer.Serialize(await f.ReadAsync(), Settings.Json)
            && f.Script.Calls == 0 && control.Calls == 0 && f.Sent.Count == sends && f.Queue.Entries.Count == 0);
    }
}
