using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Assistant.Core;

namespace CodexDashboard;

internal static class ProjectMemoryRuntimeTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (Array.IndexOf(args, "--project-memory-runtime-test") < 0) return false;
        try { if (args.Length != 2 || args[0] != "--project-memory-runtime-test") throw new ArgumentException("explicit_new_output_required"); await RunAsync(args[1]); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); } return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory)
            || Application.Current?.Dispatcher.CheckAccess() != true) throw new ArgumentException("new_absolute_wpf_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool pass)
        {
            checks.Add(name, pass); File.WriteAllText(Path.Combine(directory, "project-memory-runtime-checks.json"), JsonSerializer.Serialize(new
                { count = checks.Count, failures = checks.Count(c => !c.Value), actualHost = true, actualModelUsed = false,
                    productionDataRead = false, actualNetworkRequests = 0, actualCodexProcesses = 0, checks }, Settings.Json));
            if (!pass) throw new InvalidOperationException(name);
        }
        await ScopeHistoryAsync(Path.Combine(directory, "scope-history"), Check);
        await ToolScopeAsync(Path.Combine(directory, "tool-scope"), Check);
        await PreferenceScopeAsync(Path.Combine(directory, "preference-scope"), false, Check);
        await PreferenceScopeAsync(Path.Combine(directory, "preference-capacity"), true, Check);
        await PreferenceSameEvidenceAsync(Path.Combine(directory, "preference-same-evidence"), Check);
        await InvalidatedGenerationAsync(Path.Combine(directory, "mapping-race"), true, Check);
        await InvalidatedGenerationAsync(Path.Combine(directory, "correction-race"), false, Check);
        await TransitionAsync(Path.Combine(directory, "legitimate-transition"), Check);
        await UnknownRestartAsync(Path.Combine(directory, "unknown-restart"), Check);
    }
    private static async Task PreferenceScopeAsync(string path, bool capacity, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        f.Settings.ChatGpt.AutoMemoryEnabled = true;
        await InputAsync(f, "preference-scope-select", "切换到 Alpha");
        var project = await AddAsync(f, "以后先说结论。");
        if (capacity)
            for (int i = 0; i < AssistantMemoryStore.MaxEntries - 2; i++) f.Model.Memory.AddFromEditor("SYNTHETIC_FILLER_" + i);
        var before = f.Model.Memory.Entries.ToArray(); string stamp = f.Model.Memory.Capture().Stamp; int rounds = 0;
        ToolModelRequest? observed = null;
        f.Script.Handler = (request, _) =>
        {
            if (++rounds == 1)
            {
                var preferences = capacity ? new[] { new { key = "report_order", evidence = "以后先说结论。" },
                    new { key = "report_layout", evidence = "我更喜欢分行。" } }
                    : new[] { new { key = "report_order", evidence = "以后先说结论。" } };
                return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("project-identical-preference", "learn_preference",
                    JsonSerializer.Serialize(new { preferences }))));
            }
            observed = request; return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_PREFERENCE_SCOPE_REPLY"));
        };
        await InputAsync(f, "preference-identical-source", capacity ? "以后先说结论。我更喜欢分行。" : "以后先说结论。");
        if (rounds != 2 || observed == null) throw new InvalidOperationException("expected_preference_tool_roundtrip");
        var saved = await f.ReadAsync(); var operation = saved.Operations.Single();
        using var response = JsonDocument.Parse(observed.Observations.Single().ResultJson);
        var facts = response.RootElement.GetProperty("facts");
        using var persistedResponse = JsonDocument.Parse(saved.ToolInvocations.Single().ResultJson!);
        var persistedFacts = persistedResponse.RootElement.GetProperty("facts");
        if (capacity)
        {
            check("same_text_project_entry_does_not_bypass_personal_preference_batch_capacity",
                facts.GetProperty("code").GetString() == "memory_capacity_exceeded"
                && operation.Stage == OperationStage.Failed && f.Model.Memory.Capture().Stamp == stamp);
            check("near_capacity_multi_preference_rejection_is_atomic_and_preserves_every_scope",
                f.Model.Memory.Entries.SequenceEqual(before) && f.Model.Memory.Entries.Count == AssistantMemoryStore.MaxEntries - 1
                && !f.Model.Memory.Entries.Any(entry => entry.Origin == "automatic") && f.Queue.Entries.Count == 0);
        }
        else
        {
            string memoryId = facts.GetProperty("saved")[0].GetProperty("memoryId").GetString()!;
            var personal = f.Model.Memory.Entries.Single(entry => entry.Id == memoryId);
            check("same_text_project_and_personal_preference_have_distinct_exact_receipt_ids",
                memoryId != project.Id && personal.Text == project.Text && personal.Metadata?.Scope == AssistantMemoryLayers.PersonalScope
                && personal.Origin == "automatic" && operation.Stage == OperationStage.Completed);
            check("preference_same_text_never_rewrites_project_or_grants_task_authority",
                f.Model.Memory.Entries.Single(entry => entry.Id == project.Id) == project && f.Model.Memory.Entries.Count == 2
                && persistedFacts.GetProperty("changed").GetInt32() == 1 && !facts.GetProperty("permissionGranted").GetBoolean()
                && !facts.TryGetProperty("changed", out _) && facts.GetProperty("detailsOmitted").GetBoolean()
                && f.Queue.Entries.Count == 0 && !f.Settings.ChatGpt.TaskControlEnabled);
        }
    }
    private static async Task PreferenceSameEvidenceAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        f.Settings.ChatGpt.AutoMemoryEnabled = true;
        for (int i = 0; i < AssistantMemoryStore.MaxEntries - 1; i++) f.Model.Memory.AddFromEditor("SYNTHETIC_IDENTICAL_FILLER_" + i);
        var before = f.Model.Memory.Entries.ToArray(); int rounds = 0;
        const string evidence = "我更喜欢简短的语气。";
        f.Script.Handler = (_, _) => Task.FromResult(++rounds == 1
            ? ToolModelResponse.Call(new ModelToolCall("same-evidence-two-keys", "learn_preference", JsonSerializer.Serialize(new
                { preferences = new[] { new { key = "tone", evidence }, new { key = "detail_level", evidence } } })))
            : ToolModelResponse.Reply("SYNTHETIC_IDENTICAL_EVIDENCE_REPLY"));
        await InputAsync(f, "same-evidence-source", evidence);
        var state = await f.ReadAsync(); using var receipt = JsonDocument.Parse(state.ToolInvocations.Single().ResultJson!);
        var facts = receipt.RootElement.GetProperty("facts");
        string[] savedIds = facts.GetProperty("saved").EnumerateArray().Select(item => item.GetProperty("memoryId").GetString()!).ToArray();
        var created = f.Model.Memory.Entries.Except(before).Single();
        check("near_capacity_same_evidence_two_keys_reserve_one_slot_with_exact_shared_receipt",
            rounds == 2 && state.Operations.Single().Stage == OperationStage.Completed
            && f.Model.Memory.Entries.Count == AssistantMemoryStore.MaxEntries && savedIds.Length == 2
            && savedIds.All(id => id == created.Id) && created.Text == evidence
            && created.Metadata?.Scope == AssistantMemoryLayers.PersonalScope
            && facts.GetProperty("changed").GetInt32() == 1 && before.All(entry => f.Model.Memory.Entries.Contains(entry))
            && f.Queue.Entries.Count == 0);
    }
    private static async Task ToolScopeAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        string otherPath = Path.Combine(path, "project-B"); Directory.CreateDirectory(otherPath);
        f.ObservedTasks = [f.ObservedTask, new TaskRecord { Id = "tool-scope-B", Title = "Beta", Host = "本机", Cwd = otherPath,
            Project = "same-display-label", TurnId = "tool-scope-turn-B", Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed }];
        var personal = f.Model.Memory.AddFromEditor("PERSONAL_TOOL_SCOPE_SENTINEL");
        await InputAsync(f, "tool-select-A", "切换到 Alpha"); var a = await AddAsync(f, "PROJECT_A_TOOL_PROSE_SENTINEL");
        await InputAsync(f, "tool-select-B", "切换到 Beta"); var b = await AddAsync(f, "PROJECT_B_TOOL_PROSE_SENTINEL");
        async Task<(ToolModelRequest Request, ToolInvocationRecord Invocation)> ReadAsync(string source)
        {
            ToolModelRequest? observed = null; int rounds = 0;
            f.Script.Handler = (request, _) =>
            {
                rounds++;
                if (rounds == 1) return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("list-" + source, "list_memories", "{}")));
                observed = request; return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_LIST_REPLY_" + source));
            };
            await InputAsync(f, source, "请用工具读取本轮适用的长期资料");
            var saved = await f.ReadAsync();
            if (rounds != 2 || observed == null) throw new InvalidOperationException("expected_actual_tool_roundtrip");
            return (observed, saved.ToolInvocations.Single(item => item.CallId == "list-" + source));
        }
        static string[] MemoryIds(ToolModelRequest request)
        {
            using var result = JsonDocument.Parse(request.Observations.Single().ResultJson);
            if (result.RootElement.GetProperty("status").GetString() != "readonlysuccess") throw new InvalidOperationException("list_not_successful");
            return result.RootElement.GetProperty("facts").GetProperty("memories").EnumerateArray()
                .Select(item => item.GetProperty("memoryId").GetString()!).Order(StringComparer.Ordinal).ToArray();
        }
        await InputAsync(f, "tool-return-A", "切换到 Alpha"); var fromA = await ReadAsync("tool-read-A");
        check("model_list_memories_actual_roundtrip_A_reads_only_frozen_A_and_personal_ids",
            MemoryIds(fromA.Request).SequenceEqual(new[] { a.Id, personal.Id }.Order(StringComparer.Ordinal))
            && fromA.Request.Observations.Single().ResultJson.Contains(a.Text, StringComparison.Ordinal)
            && !fromA.Request.Observations.Single().ResultJson.Contains(b.Text, StringComparison.Ordinal)
            && fromA.Invocation.Stage == ToolInvocationStage.Completed && !fromA.Invocation.HasSideEffects
            && ProjectMemoryProof.Read(fromA.Invocation.GenerationContext?.Dependencies)?.Project
                == ProjectMemoryProof.Read(fromA.Request.GenerationContext?.Dependencies)?.Project);
        ToolModelRequest? laterA = null;
        f.Script.Handler = (request, _) => { laterA = request; return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_LATER_A_REPLY")); };
        await InputAsync(f, "later-A-question", "聊聊下一步怎么整理资料");
        check("historical_management_tool_results_are_not_reused_even_in_same_project",
            laterA != null && !laterA.Context.Any(item => item.RelatedInvocationId == fromA.Invocation.Id)
            && !laterA.Context.Any(item => item.Body.Contains("SYNTHETIC_LIST_REPLY_tool-read-A", StringComparison.Ordinal))
            && laterA.Observations.IsEmpty);
        await InputAsync(f, "tool-to-B", "切换到 Beta"); var fromB = await ReadAsync("tool-read-B");
        check("model_list_memories_actual_roundtrip_B_never_reads_A_body_or_old_tool_history",
            MemoryIds(fromB.Request).SequenceEqual(new[] { b.Id, personal.Id }.Order(StringComparer.Ordinal))
            && !fromB.Request.Observations.Single().ResultJson.Contains(a.Text, StringComparison.Ordinal)
            && !fromB.Request.Context.Any(item => item.Body.Contains(a.Text, StringComparison.Ordinal))
            && !fromB.Request.Context.Any(item => item.RelatedInvocationId == fromA.Invocation.Id));
        await InputAsync(f, "tool-new-dialogue", "新对话"); var noFocus = await ReadAsync("tool-read-unfocused");
        check("model_list_memories_without_focus_returns_personal_manifest_only",
            MemoryIds(noFocus.Request).SequenceEqual([personal.Id])
            && ProjectMemoryProof.Read(noFocus.Request.GenerationContext?.Dependencies) == null
            && !noFocus.Request.Observations.Single().ResultJson.Contains(a.Text, StringComparison.Ordinal)
            && !noFocus.Request.Observations.Single().ResultJson.Contains(b.Text, StringComparison.Ordinal));
        await InputAsync(f, "management-select-A", "切换到 Alpha");
        int sentBefore = f.Sent.Count;
        await InputAsync(f, "management-list", "查看记忆");
        var managementState = await f.ReadAsync();
        var managementInput = managementState.Inbox.Single(item => item.SourceMessageId == WeChatText.Hash("management-list"));
        var managementGeneration = managementState.Generations.Single(item => item.InboxIds.Contains(managementInput.Id));
        var managementOutput = managementState.Outbox.Single(item => item.GenerationId == managementGeneration.Id);
        string wire = string.Join("\n", f.Sent.Skip(sentBefore));
        check("wechat_memory_management_lists_project_ids_and_scope_without_project_prose",
            wire.Contains(a.Id, StringComparison.Ordinal) && wire.Contains(b.Id, StringComparison.Ordinal)
            && wire.Contains(personal.Text, StringComparison.Ordinal) && wire.Contains("正文请在桌面", StringComparison.Ordinal)
            && !wire.Contains(a.Text, StringComparison.Ordinal) && !wire.Contains(b.Text, StringComparison.Ordinal)
            && managementOutput.Stage == OutboxStage.Accepted);
        await InputAsync(f, "management-switch-B", "切换到 Beta"); ToolModelRequest? afterList = null;
        f.Script.Handler = (request, _) => { afterList = request; return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_AFTER_MANAGEMENT_B_REPLY")); };
        await InputAsync(f, "management-later-question", "按当前项目聊聊资料");
        var final = await f.ReadAsync();
        check("wechat_management_reply_never_reenters_B_model_history_or_grants_control",
            afterList != null && !afterList.Context.Any(item => item.RelatedOutboxId == managementOutput.Id)
            && !afterList.Context.Any(item => item.Body.Contains(a.Text, StringComparison.Ordinal))
            && afterList.Observations.IsEmpty && final.Operations.IsEmpty && f.Queue.Entries.Count == 0
            && final.Outbox.Single(item => item.Id == managementOutput.Id) == managementOutput);
    }
    private static async Task ScopeHistoryAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        string otherPath = Path.Combine(path, "project-B"); Directory.CreateDirectory(otherPath);
        var other = new TaskRecord { Id = "synthetic-B", Title = "Beta", Host = "本机", Cwd = otherPath,
            Project = "Same display label allowed", TurnId = "synthetic-turn-B", Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed };
        f.ObservedTasks = [f.ObservedTask, other];
        await InputAsync(f, "select-A", "切换到 Alpha"); var a = await AddAsync(f, "PROJECT_A_ONLY_SENTINEL");
        await InputAsync(f, "select-B", "切换到 Beta"); var b = await AddAsync(f, "PROJECT_B_ONLY_SENTINEL");
        await InputAsync(f, "select-A-again", "切换到 Alpha");
        AssistantGenerationPersonalization? captureA = null, captureB = null; ToolModelRequest? requestB = null;
        f.Script.Handler = (request, _) =>
        {
            captureA = f.Model.GenerationPersonalizationProvider!(request.GenerationContext!);
            return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_ANSWER_FROM_PROJECT_A"));
        };
        await InputAsync(f, "ask-A", "聊聊这个项目的约定");
        var first = await f.ReadAsync(); var outputA = first.Outbox.Single(o => o.Body == "SYNTHETIC_ANSWER_FROM_PROJECT_A");
        check("actual_focused_model_capture_contains_exact_project_not_other_project",
            captureA?.Content?.Manifest?.Project?.Task.ThreadId == f.ObservedTask.Id
            && captureA.Content.Context.Contains(a.Id) && !captureA.Content.Context.Contains(b.Id)
            && outputA.Stage == OutboxStage.Accepted
            && ProjectMemoryProof.Read(DurableConversationLedger.ResolveOutputDependencies(first, outputA))?.Project
                == captureA.Content.Manifest.Project.Project);
        await InputAsync(f, "return-B", "切换到 Beta");
        f.Script.Handler = (request, _) =>
        {
            requestB = request; captureB = f.Model.GenerationPersonalizationProvider!(request.GenerationContext!);
            return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_ANSWER_FROM_PROJECT_B"));
        };
        await InputAsync(f, "ask-B", "聊聊当前项目的约定");
        check("other_project_assistant_history_is_filtered_even_when_memory_document_unchanged",
            captureB?.Content?.Context.Contains(b.Id) == true && !captureB.Content.Context.Contains(a.Id)
            && requestB != null && !requestB.Context.Any(e => e.Body.Contains("SYNTHETIC_ANSWER_FROM_PROJECT_A", StringComparison.Ordinal))
            && (await f.ReadAsync()).Outbox.Single(o => o.Id == outputA.Id) == outputA);
        check("ordinary_summary_capture_never_borrows_current_project",
            !f.Model.Memory.Capture().Context.Contains(a.Id) && !f.Model.Memory.Capture().Context.Contains(b.Id)
            && f.Model.Memory.Capture().Manifest!.Project == null);
        await InputAsync(f, "new-dialogue", "新对话");
        AssistantGenerationPersonalization? unbound = null;
        f.Script.Handler = (request, _) =>
        {
            unbound = f.Model.GenerationPersonalizationProvider!(request.GenerationContext!);
            return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_NO_PROJECT_REPLY"));
        };
        await InputAsync(f, "unfocused-question", "聊一下日常安排");
        check("new_dialogue_without_focus_has_no_project_injection_or_control",
            unbound?.Content?.Manifest?.Project == null && unbound?.Content?.Context.Contains(a.Id) == false
            && (await f.ReadAsync()).Operations.IsEmpty && f.Queue.Entries.Count == 0 && !f.Settings.ChatGpt.TaskControlEnabled);
        await InputAsync(f, "pending-project-selection", "任务列表");
        await InputAsync(f, "ambiguous-project-question", "聊一下还没选定的项目");
        check("pending_selection_never_guesses_project", unbound?.Content?.Manifest?.Project == null
            && (await f.ReadAsync()).Context?.PendingSelection != null);
    }
    private static async Task InvalidatedGenerationAsync(string path, bool mapping, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        await InputAsync(f, "select", "切换到 Alpha"); var entry = await AddAsync(f, "SYNTHETIC_OLD_PROJECT_KNOWLEDGE");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolModelRequest? modelRequest = null;
        f.Script.Handler = async (request, _) => { modelRequest = request; entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply("SYNTHETIC_STALE_PROJECT_ANSWER"); };
        try
        {
            await f.InputAsync("project-race-question", "聊聊当前的项目知识"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            if (mapping)
            {
                string changed = Path.Combine(path, "changed-directory"); Directory.CreateDirectory(changed); f.ObservedTask.Cwd = changed;
            }
            else
            {
                var target = (await f.Model.ProjectMemoryTargetProvider!(CancellationToken.None))!;
                var snapshot = f.Model.Memory.CaptureProjectEditor(target, f.Model.ProjectMemoryResolver!, entry.Id);
                f.Model.Memory.SaveProjectFromEditor(snapshot, "SYNTHETIC_CORRECTED_PROJECT_KNOWLEDGE", f.Model.ProjectMemoryResolver!);
            }
            release.TrySetResult();
            await WaitAsync(async () => (await f.ReadAsync()).Generations.All(g => g.Stage != GenerationStage.Processing));
            var state = await f.ReadAsync();
            check((mapping ? "directory_mapping" : "knowledge_correction") + "_during_inference_retires_old_output_before_prepare",
                modelRequest?.GenerationContext != null && ProjectMemoryProof.Read(modelRequest.GenerationContext.Dependencies) != null
                && !f.Sent.Contains("SYNTHETIC_STALE_PROJECT_ANSWER") && !state.Outbox.Any(o => o.Body == "SYNTHETIC_STALE_PROJECT_ANSWER")
                && state.Operations.IsEmpty && f.Queue.Entries.Count == 0);
            f.Script.Handler = (request, _) =>
            {
                var captured = f.Model.GenerationPersonalizationProvider!(request.GenerationContext!);
                bool hasOld = captured.Content!.Context.Contains("SYNTHETIC_OLD_PROJECT_KNOWLEDGE", StringComparison.Ordinal);
                return Task.FromResult(ToolModelResponse.Reply(hasOld ? "UNEXPECTED_OLD_PROJECT_CONTENT" : "SYNTHETIC_FRESH_PROJECT_CONTEXT"));
            };
            await InputAsync(f, "after-project-change", "聊聊现在的约定");
            check((mapping ? "directory_mapping" : "knowledge_correction") + "_next_current_generation_never_resurrects_old_content",
                f.Sent.Contains("SYNTHETIC_FRESH_PROJECT_CONTEXT") && !f.Sent.Contains("UNEXPECTED_OLD_PROJECT_CONTENT"));
        }
        finally { release.TrySetResult(); }
    }
    private static async Task TransitionAsync(string path, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(path);
        f.Settings.ChatGpt.AutoMemoryEnabled = true;
        await InputAsync(f, "transition-select", "切换到 Alpha"); var entry = await AddAsync(f, "SYNTHETIC_PROJECT_TRANSITION_CONTEXT");
        var captures = new List<GenerationContext>();
        f.Script.Handler = (request, _) =>
        {
            captures.Add(request.GenerationContext!);
            if (captures.Count == 1) return Task.FromResult(ToolModelResponse.Call(
                new("project-pref", "learn_preference", JsonSerializer.Serialize(new { preferences = new[] { new { key = "report_order", evidence = "以后先说结论。" } } })),
                new("stale-extra", "remember", JsonSerializer.Serialize(new { scope = "personal", text = "UNAUTHORIZED_SAME_RESPONSE_WRITE" }))));
            return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_PROJECT_TRANSITION_REPLY"));
        };
        await InputAsync(f, "project-preference", "以后先说结论。"); var saved = await f.ReadAsync();
        check("legitimate_preference_transition_keeps_exact_project_scope_and_increments_epoch",
            captures.Count == 2 && captures[0].GenerationId == captures[1].GenerationId && captures[1].Epoch == captures[0].Epoch + 1
            && ProjectMemoryProof.Read(captures[0].Dependencies) == ProjectMemoryProof.Read(captures[1].Dependencies)
            && f.Model.Memory.Entries.Single(e => e.Id == entry.Id) == entry && f.Model.Memory.Entries.Any(e => e.PreferenceKey == "report_order")
            && saved.Operations.Length == 1 && saved.Operations.Single().Stage == OperationStage.Completed);
        check("typed_project_transition_drops_remaining_old_model_calls_and_grants_no_task_permission",
            !saved.ToolInvocations.Any(i => i.CallId == "stale-extra") && f.Model.Memory.Entries.Count == 2
            && !f.Model.Memory.Entries.Any(e => e.Text == "UNAUTHORIZED_SAME_RESPONSE_WRITE")
            && f.Queue.Entries.Count == 0 && !f.Settings.ChatGpt.TaskControlEnabled);
    }
    private static async Task UnknownRestartAsync(string path, Action<string, bool> check)
    {
        ClawBinding binding; OutboxMessage unknown;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(path))
        {
            await InputAsync(f, "unknown-project-select", "切换到 Alpha"); var entry = await AddAsync(f, "SYNTHETIC_UNKNOWN_PROJECT_SOURCE");
            f.SendOutcome = "{}"; f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_UNKNOWN_PROJECT_REPLY"));
            await f.InputAsync("unknown-project-question", "聊聊项目里的做法");
            await WaitAsync(async () => (await f.ReadAsync()).Outbox.Any(o => o.Body == "SYNTHETIC_UNKNOWN_PROJECT_REPLY" && o.Stage == OutboxStage.Unknown));
            unknown = (await f.ReadAsync()).Outbox.Single(o => o.Body == "SYNTHETIC_UNKNOWN_PROJECT_REPLY");
            f.Model.Memory.Delete(entry.Id); await f.Ledger.RefreshOutputDependenciesAsync(f.Runtime.Identity!);
            check("unknown_project_receipt_is_not_rewritten_after_memory_delete", (await f.ReadAsync()).Outbox.Single(o => o.Id == unknown.Id) == unknown);
            binding = f.Settings.WeChat.ReadBinding()!;
        }
        await using var reopened = await DurableRuntimeTests.Fixture.CreateAsync(path, savedBinding: binding);
        await InputAsync(reopened, "unknown-project-fresh-context", "状态"); await Task.Delay(1100);
        check("real_protected_ledger_reopen_does_not_replay_unknown_project_reply",
            (await reopened.ReadAsync()).Outbox.Single(o => o.Id == unknown.Id) == unknown
            && !reopened.Sent.Contains(unknown.Body) && reopened.Script.Calls == 0 && reopened.Queue.Entries.Count == 0);
    }
    private static async Task<AssistantMemoryEntry> AddAsync(DurableRuntimeTests.Fixture f, string text)
    {
        var target = await (f.Model.ProjectMemoryTargetProvider ?? throw new InvalidOperationException("project_provider_not_wired"))(CancellationToken.None)
            ?? throw new InvalidOperationException("expected_fresh_local_project");
        var resolver = f.Model.ProjectMemoryResolver ?? throw new InvalidOperationException("project_resolver_not_wired");
        return f.Model.Memory.SaveProjectFromEditor(f.Model.Memory.CaptureProjectEditor(target, resolver), text, resolver);
    }
    private static async Task InputAsync(DurableRuntimeTests.Fixture f, string source, string text)
    {
        int count = (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted && o.Kind != OutboxKind.Notification);
        await f.InputAsync(source, text);
        await WaitAsync(async () => (await f.ReadAsync()).Outbox.Count(o => o.Stage == OutboxStage.Accepted && o.Kind != OutboxKind.Notification) > count);
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("project_memory_runtime_condition"); await Task.Delay(20); }
    }
}
