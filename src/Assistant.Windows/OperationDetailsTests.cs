using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class OperationDetailsTests
{
    internal static Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--operation-details-test")) return Task.FromResult(false);
        try
        {
            if (args.Length != 2 || args[0] != "--operation-details-test")
                throw new ArgumentException("Explicit isolated output required.");
            Run(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return Task.FromResult(true);
    }
    private static void Run(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("A fresh absolute directory is required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected test output is not allowed.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "operation-details-checks.json"),
                JsonSerializer.Serialize(new { checks, networkUsed = false, productionDataRead = false }, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        var identity = new ConversationIdentity("synthetic-user", "synthetic-binding", "main");
        var other = identity with { BindingId = "other-binding" };
        var task = new TaskReference("local", "synthetic-thread", "synthetic-turn", "synthetic-profile");
        var now = DateTimeOffset.Parse("2026-10-20T18:00:00Z");
        string body = "请继续原任务，只检查这一个问题。\n" + new string('长', 1200) + "\n保留末尾原话。";
        var input = new InboxMessage(Guid.NewGuid(), identity, "synthetic-source", body,
            MessageTrust.AuthenticatedUser, now.AddMinutes(-1), now, 1, InboxStage.HandedToOutbox, Task: task);
        const string privateArgs = """{"text":"RAW_ARGUMENTS_MUST_NOT_APPEAR"}""";
        var authorization = new UserAuthorization(Guid.NewGuid(), identity, input.SourceMessageId, 1,
            "continue_owned_task", "synthetic-target", privateArgs, "请继续原任务", now, task);
        var operation = new OperationRecord(Guid.NewGuid(), identity, authorization.Id, input.SourceMessageId, 1,
            "PRIVATE_IDEMPOTENCY_KEY", authorization.Action, authorization.TargetKey, privateArgs,
            OperationStage.Accepted, now, now.AddSeconds(1), task, "RAW_RECEIPT_MUST_NOT_APPEAR");
        var invocation = new ToolInvocationRecord(Guid.NewGuid(), identity, input.SourceMessageId, 1,
            Guid.NewGuid(), "PRIVATE_CALL_ID", operation.Action, privateArgs, true, ToolInvocationStage.Completed,
            now, now.AddSeconds(2), operation.Id, "MALFORMED_RESULT_MUST_NOT_APPEAR{");
        var saved = new ConversationSnapshot(identity, 1, [input], [], [], [authorization], [operation],
            [], false, null, ToolInvocations: [invocation]);
        string Records() => JsonSerializer.Serialize(new
            { saved.Identity, saved.Revision, saved.Inbox, saved.Authorizations, saved.Operations, saved.ToolInvocations }, Settings.Json);
        string before = Records();
        string Detail(ConversationSnapshot snapshot) => OperationDetails.Read(snapshot, operation.Id)!.Body;
        string text = Detail(saved);
        Check("exact_current_identity_operation_shows_complete_original_input",
            text.Contains(body, StringComparison.Ordinal) && text.Contains(task.HostId)
            && text.Contains(task.ThreadId) && text.Contains(task.TurnId!));
        Check("accepted_operation_and_completed_tool_are_distinct_facts",
            text.Contains("接口已受理，执行结果仍需核对") && text.Contains("工具步骤已结束")
            && !text.Contains("操作已结束，验证结果须另核对"));
        Check("raw_arguments_receipts_results_and_authorization_metadata_are_never_rendered",
            !new[] { "RAW_ARGUMENTS", "RAW_RECEIPT", "MALFORMED_RESULT", "PRIVATE_IDEMPOTENCY", "PRIVATE_CALL_ID",
                authorization.Id.ToString(), identity.BindingId }.Any(text.Contains));
        Check("projection_does_not_mutate_snapshot", Records() == before);
        Check("unknown_record_cannot_claim_failure_or_authorize_replay",
            Detail(saved with { Operations = [operation with { Stage = OperationStage.Unknown }] })
                .Contains("当前不能确定这项操作是否生效，不会自动重发"));
        Check("interruption_does_not_claim_descendant_processes_stopped",
            Detail(saved with { Operations = [operation with { Stage = OperationStage.Interrupted }] })
                .Contains("不代表所有派生进程都已结束"));
        Check("cancelled_before_dispatch_does_not_claim_running_task_stopped",
            Detail(saved with { Operations = [operation with { Stage = OperationStage.CancelledBeforeDispatch }] })
                .Contains("不能据此推断其他指令或正在运行的任务也已停止"));

        var invalidSources = new (string Name, ConversationSnapshot Snapshot)[]
        {
            ("authorization_identity", saved with { Authorizations = [authorization with { Identity = other }] }),
            ("authorization_revision", saved with { Authorizations = [authorization with { Revision = 2 }] }),
            ("authorization_task", saved with { Authorizations = [authorization with { Task = task with { TurnId = "another-turn" } }] }),
            ("authorization_arguments", saved with { Authorizations = [authorization with { ArgumentsJson = "{}" }] }),
            ("authorization_excerpt", saved with { Authorizations = [authorization with { SourceExcerpt = "ABSENT_EXCERPT" }] }),
            ("input_identity", saved with { Inbox = [input with { Identity = other }] }),
            ("input_revision", saved with { Inbox = [input with { Revision = 2 }] }),
            ("input_trust", saved with { Inbox = [input with { Trust = MessageTrust.UntrustedContent }] }),
            ("input_source", saved with { Inbox = [input with { SourceMessageId = "another-source" }] })
        };
        foreach (var invalid in invalidSources)
        {
            string result = Detail(invalid.Snapshot);
            Check("invalid_" + invalid.Name + "_cannot_supply_original_request",
                !result.Contains(body, StringComparison.Ordinal) && result.Contains("关联暂无法完整核对"));
        }
        Check("other_binding_operation_is_not_projected",
            OperationDetails.Read(saved with { Operations = [operation with { Identity = other }] }, operation.Id) == null);
        Check("unrelated_tool_result_is_not_associated_by_time_or_title",
            !Detail(saved with { ToolInvocations = [invocation with { OperationId = Guid.NewGuid() }] }).Contains("关联的工具处理记录"));
        Check("same_operation_wrong_source_tool_is_not_associated",
            !Detail(saved with { ToolInvocations = [invocation with { SourceMessageId = "different-input" }] }).Contains("关联的工具处理记录"));
        Check("missing_tool_array_still_shows_operation_evidence",
            Detail(saved with { ToolInvocations = default }).Contains(body));
        bool ambiguousRefused = false;
        try { _ = Detail(saved with { Authorizations = [authorization, authorization] }); }
        catch (InvalidOperationException) { ambiguousRefused = true; }
        Check("ambiguous_source_fails_closed_instead_of_guessing", ambiguousRefused);
        Check("selected_ids_are_unique_and_missing_ids_do_not_borrow_records",
            OperationDetails.Read(saved, new[] { operation.Id, Guid.NewGuid(), operation.Id }).Count == 1);
        Check("indexed_projection_preserves_invalid_source_decisions", invalidSources.All(test =>
            OperationDetails.Read(test.Snapshot, new[] { operation.Id }).Single().Body == Detail(test.Snapshot)));
        bool indexedAmbiguousRefused = false;
        try { _ = OperationDetails.Read(saved with { Authorizations = [authorization, authorization] }, new[] { operation.Id }); }
        catch (InvalidOperationException) { indexedAmbiguousRefused = true; }
        Check("indexed_projection_preserves_ambiguous_source_rejection", indexedAmbiguousRefused);
        var inputs = Enumerable.Range(0, 2048).Select(i => input with { Id = Guid.NewGuid(), SourceMessageId = "synthetic-" + i,
            Body = "请继续原任务 " + i }).ToImmutableArray();
        var authorizations = inputs.Select(item => authorization with { Id = Guid.NewGuid(), SourceMessageId = item.SourceMessageId }).ToImmutableArray();
        var operations = authorizations.Select(item => operation with { Id = Guid.NewGuid(), AuthorizationId = item.Id,
            SourceMessageId = item.SourceMessageId }).ToImmutableArray();
        var invocations = operations.Select(item => invocation with { Id = Guid.NewGuid(), OperationId = item.Id,
            SourceMessageId = item.SourceMessageId }).ToImmutableArray();
        var large = saved with { Inbox = inputs, Authorizations = authorizations, Operations = operations, ToolInvocations = invocations };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var expected = operations.Select(item => OperationDetails.Read(large, item.Id)!).ToArray();
        double scanMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
        var actual = OperationDetails.Read(large, operations.Select(item => item.Id));
        double indexedMs = timer.Elapsed.TotalMilliseconds;
        Check("indexed_2048_operation_projection_matches_independent_single_reads", actual.SequenceEqual(expected));
        File.WriteAllText(Path.Combine(directory, "projection-measurements.json"), JsonSerializer.Serialize(new
        { records = operations.Length, repeatedScanMilliseconds = scanMs, indexedMilliseconds = indexedMs,
            scope = "single_synthetic_process_observation_not_production_benchmark" }, Settings.Json));
    }
}
