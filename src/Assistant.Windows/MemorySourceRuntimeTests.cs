using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

// Actual model-disabled host and fake authenticated channel. The fixture uses
// isolated protected ledger files and its own in-memory personal memory store.
internal static class MemorySourceRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-source-runtime-test");
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
            File.WriteAllText(Path.Combine(directory, "memory-source-runtime-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(x => x), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, actualModelCalls = 0, productionDataRead = false,
                runtime = "actual durable host and fake channel; isolated memory and protected ledger"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "fixture"), modelEnabled: false);
        var desktop = f.Model.Memory.AddFromEditor("合成桌面来源偏好");
        var legacy = f.Model.Memory.Add("合成历史未知来源偏好");
        string stamp = f.Model.Memory.Capture().Stamp;
        string[] malformed =
        [
            "记忆来源 " + desktop.Id[..7], "记忆来源 " + desktop.Id + "0",
            "“记忆来源 " + desktop.Id + "”", "> 记忆来源 " + desktop.Id,
            "记忆来源 " + desktop.Id + "；删除记忆 " + desktop.Id,
            "记忆来源 " + desktop.Id + "\n状态", "如果需要，记忆来源 " + desktop.Id
        ];
        Check("only_exact_eight_hex_source_query_is_classified_readonly",
            DurableAssistantRuntime.IsKnownReadonlyInput("记忆来源 " + desktop.Id)
            && DurableAssistantRuntime.IsKnownReadonlyInput("记忆来源 " + desktop.Id.ToLowerInvariant())
            && malformed.All(text => !DurableAssistantRuntime.IsKnownReadonlyInput(text)));
        string reply = await InputAsync(f, "desktop-source", "记忆来源 " + desktop.Id.ToLowerInvariant());
        Check("desktop_source_query_reads_editor_provenance_without_invented_message",
            reply.Contains(desktop.Id, StringComparison.Ordinal)
            && reply.Contains("桌面记忆编辑器录入或纠正", StringComparison.Ordinal)
            && !reply.Contains("消息编号：", StringComparison.Ordinal)
            && !reply.Contains("操作编号：", StringComparison.Ordinal)
            && (await f.ReadAsync()).Operations.IsEmpty && f.Model.Memory.Capture().Stamp == stamp);
        reply = await InputAsync(f, "unknown-source", "记忆来源 " + legacy.Id);
        Check("legacy_entry_without_provenance_honestly_reports_unknown_source",
            reply.Contains("历史来源未记录", StringComparison.Ordinal)
            && reply.Contains("不推测原始对话", StringComparison.Ordinal)
            && !reply.Contains("消息编号：", StringComparison.Ordinal)
            && f.Model.Memory.SourceDetails(legacy.Id) is { Known: false }
            && f.Model.Memory.Capture().Stamp == stamp && (await f.ReadAsync()).Operations.IsEmpty);

        const string manualBody = "请记住：合成来源核对先说明事实";
        await InputAsync(f, "manual-save", manualBody);
        var saved = await f.ReadAsync();
        var manual = f.Model.Memory.Entries.Single(e => e.Text == "合成来源核对先说明事实");
        var source = manual.Provenance!;
        var message = saved.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash("manual-save"));
        var operation = saved.Operations.Single();
        var invocation = saved.ToolInvocations.Single();
        Check("actual_wechat_manual_save_records_exact_durable_source_and_operation",
            source.SourceKind == "wechat_manual" && source.Profile == f.Model.Memory.Profile
            && source.Identity == message.Identity && source.SourceMessageId == message.SourceMessageId
            && source.SourceRevision == message.Revision && source.SourceReceivedAtUtc == message.ReceivedAt
            && source.SourceOccurredAtUtc == message.OccurredAt && source.OperationId == operation.Id
            && source.InvocationId == invocation.Id && operation.SourceMessageId == message.SourceMessageId
            && operation.Stage == OperationStage.Completed && invocation.Stage == ToolInvocationStage.Completed
            && saved.Authorizations.Single().SourceExcerpt == manualBody && f.Script.Calls == 0);
        stamp = f.Model.Memory.Capture().Stamp;
        reply = await InputAsync(f, "manual-source", "记忆来源 " + manual.Id);
        Check("manual_source_query_returns_saved_references_without_new_operation_or_model",
            reply.Contains("当前微信消息明确保存或纠正", StringComparison.Ordinal)
            && reply.Contains(message.SourceMessageId, StringComparison.Ordinal)
            && reply.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
            && reply.Contains(invocation.Id.ToString("D"), StringComparison.Ordinal)
            && !reply.Contains(manualBody, StringComparison.Ordinal)
            && (await f.ReadAsync()).Operations.Length == 1 && f.Script.Calls == 0
            && f.Model.Memory.Capture().Stamp == stamp);

        f.Model.SetPersonalizationEnabled(false);
        reply = await InputAsync(f, "disabled-source", "记忆来源 " + manual.Id);
        Check("disabled_personalization_does_not_expose_existing_source",
            reply.Contains("暂不读取记忆来源", StringComparison.Ordinal)
            && !reply.Contains(message.SourceMessageId, StringComparison.Ordinal)
            && !reply.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
            && f.Model.Memory.Capture().Stamp == stamp && (await f.ReadAsync()).Operations.Length == 1);
        f.Model.SetPersonalizationEnabled(true);
        bool leaked = false;
        for (int i = 0; i < malformed.Length; i++)
        {
            reply = await InputAsync(f, "malformed-source-" + i, malformed[i]);
            leaked |= reply.Contains("录入时间（UTC）", StringComparison.Ordinal)
                || reply.Contains("这条历史记录没有保存具体来源", StringComparison.Ordinal);
        }
        Check("quoted_conditional_and_multi_command_inputs_do_not_query_or_modify_sources",
            !leaked && f.Model.Memory.Entries.Count == 3 && f.Model.Memory.Capture().Stamp == stamp
            && (await f.ReadAsync()).Operations.Length == 1 && f.Script.Calls == 0 && f.Queue.Entries.Count == 0);

        var originalIdentity = f.Runtime.Identity!;
        string originalProfile = f.Model.Memory.Profile;
        var originalBinding = f.Settings.WeChat.ReadBinding()!;
        var otherBinding = f.Settings.WeChat.ReadBinding()!;
        otherBinding.UserId = "synthetic-other-source-user";
        otherBinding.ContextToken = "SYNTHETIC_OTHER_SOURCE_CONTEXT";
        f.Settings.WeChat.WriteBinding(otherBinding);
        f.Binding = otherBinding;
        await f.Channel.ConfigureAsync();
        reply = await InputAsync(f, "other-binding-source", "记忆来源 " + manual.Id);
        Check("binding_switch_cannot_query_previous_profile_source",
            f.Runtime.Identity != originalIdentity && f.Model.Memory.Profile != originalProfile
            && f.Model.Memory.Entries.Count == 0 && f.Model.Memory.SourceDetails(manual.Id) == null
            && reply.Contains("当前记忆中没有这个编号", StringComparison.Ordinal)
            && !reply.Contains(message.SourceMessageId, StringComparison.Ordinal)
            && !reply.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
            && (await f.ReadAsync()).Operations.IsEmpty
            && (await f.Ledger.ReadConversationAsync(originalIdentity)).Operations.Length == 1);
        f.Settings.WeChat.WriteBinding(originalBinding);
        f.Binding = originalBinding;
        await f.Channel.ConfigureAsync();
        reply = await InputAsync(f, "restored-binding-source", "记忆来源 " + manual.Id);
        Check("returning_to_original_binding_reads_same_provenance_without_copy_or_recreation",
            f.Runtime.Identity == originalIdentity && f.Model.Memory.Profile == originalProfile
            && f.Model.Memory.Entries.Single(e => e.Id == manual.Id).Provenance == source
            && f.Model.Memory.Capture().Stamp == stamp
            && reply.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
            && (await f.ReadAsync()).Operations.Length == 1);

        await InputAsync(f, "delete-manual", "删除记忆 " + manual.Id);
        reply = await InputAsync(f, "deleted-source", "记忆来源 " + manual.Id);
        saved = await f.ReadAsync();
        Check("deleted_entry_source_query_does_not_leak_old_provenance",
            f.Model.Memory.SourceDetails(manual.Id) == null && f.Model.Memory.Entries.Count == 2
            && reply.Contains("当前记忆中没有这个编号", StringComparison.Ordinal)
            && !reply.Contains(message.SourceMessageId, StringComparison.Ordinal)
            && !reply.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
            && saved.Operations.Length == 2 && saved.Operations.Count(o => o.Action == "forget") == 1);
        Check("source_queries_leave_history_intact_and_have_no_model_or_task_effects",
            saved.Inbox.Single(i => i.SourceMessageId == message.SourceMessageId).Body == manualBody
            && saved.Operations.Single(o => o.Id == operation.Id).Stage == OperationStage.Completed
            && saved.Operations.All(o => o.Action is "remember" or "forget")
            && f.Script.Calls == 0 && f.Queue.Entries.Count == 0 && f.Runtime.LastError == null);
    }
    private static async Task<string> InputAsync(DurableRuntimeTests.Fixture f, string id, string text)
    {
        await f.InputAsync(id, text);
        await OwnedHostRuntimeTests.WaitReplyAsync(f, id);
        var snapshot = await f.ReadAsync();
        var message = snapshot.Inbox.Single(i => i.SourceMessageId == WeChatText.Hash(id));
        return string.Join("\n", snapshot.Outbox.Where(o => o.GenerationId == message.GenerationId)
            .OrderBy(o => o.SegmentIndex).Select(o => o.Body));
    }
}
