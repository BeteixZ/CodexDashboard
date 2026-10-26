using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class EpisodeSourceTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--episode-source-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "source-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try { await Store(Path.Combine(directory, "store"), Check); await Runtime(Path.Combine(directory, "runtime"), Check); exit(0); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }

    private static async Task Store(string directory, Action<string, bool> check)
    {
        var identity = new ConversationIdentity("synthetic", "source-proof", "main");
        var events = new Dictionary<Guid, ConversationEvent>();
        var store = new AssistantMemoryStore(directory, () => "source-proof-owner")
            { EpisodeSourceReader = (owner, id) => owner == identity ? events.GetValueOrDefault(id) : null };
        store.Initialize(); var memory = store.AddFromEditor("原始明确偏好");
        OutputDependencies Capture() => AssistantMemoryDependencies.ToOutputDependencies(store.CaptureForGeneration(true, DateTimeOffset.UtcNow));
        ConversationEvent Source(ConversationSource kind, OutputDependencies? dependencies, string body = "SYNTHETIC_DERIVED_SOURCE")
        {
            var now = DateTimeOffset.UtcNow;
            var source = new ConversationEvent(Guid.NewGuid(), identity, kind, body, now, now, 1, OutputDependencies: dependencies);
            events.Add(source.Id, source); return source;
        }
        AssistantMemoryEntry Save(ConversationEvent source, string text = "经审阅的历史说法") => store.SaveEpisodeFromEditor(
            store.CaptureEpisodeEditor(events.Values), text, [EpisodeEventReference.From(source)]);
        bool Available(ConversationEvent source) => store.CaptureEpisodeEditor(events.Values).Sources.Any(e => e.Id == source.Id);

        var proof = Capture();
        var answer = Source(ConversationSource.Assistant, proof);
        var notice = Source(ConversationSource.Notification, proof);
        var receipt = Source(ConversationSource.ToolResult, proof);
        var report = Source(ConversationSource.Report, proof);
        check("four_derived_categories_with_proven_context_are_selectable", new[] { answer, notice, receipt, report }.All(Available));
        var summary = Save(answer);
        store.AddFromEditor("新增无关内容");
        check("summary_inherits_exact_memory_versions_and_survives_own_save_and_unrelated_addition",
            summary.Metadata!.Dependencies.SequenceEqual([AssistantMemoryLayers.Reference(memory)])
            && Available(answer) && store.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        var reopened = new AssistantMemoryStore(directory, () => "source-proof-owner") { EpisodeSourceReader = store.EpisodeSourceReader };
        check("derived_source_reopens_with_original_fingerprint_and_dependency_graph", reopened.Entries.Single(e => e.Id == summary.Id) == summary
            && reopened.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        var next = Source(ConversationSource.Assistant, Capture(), "使用前一摘要的回答");
        var child = Save(next);
        check("second_generation_summary_inherits_first_summary", child.Metadata!.Dependencies.Contains(AssistantMemoryLayers.Reference(summary)));
        bool cycleRejected = false;
        try { store.SaveEpisodeFromEditor(store.CaptureEpisodeEditor(events.Values, summary.Id), "循环纠正", [EpisodeEventReference.From(next)]); }
        catch (ChatGptException) { cycleRejected = true; }
        check("correction_cannot_depend_on_itself", cycleRejected);
        var editor = new EpisodeMemoryEditorWindow(store, store.CaptureEpisodeEditor(events.Values, child.Id));
        store.Delete(memory.Id);
        check("original_deletion_clears_transitive_summary_prose_and_open_editor", store.Entries.Where(e => e.Id == summary.Id || e.Id == child.Id).All(e => e.Text == "")
            && editor.Body.Text == "" && editor.Original.Text == "" && !Available(answer));
        editor.Close();

        var fresh = store.AddFromEditor("新的偏好");
        var corrected = Source(ConversationSource.Report, Capture()); var correctedSummary = Save(corrected);
        store.Update(fresh.Id, "已纠正偏好");
        check("correction_invalidates_derived_report_summary", !Available(corrected) && store.Entries.Single(e => e.Id == correctedSummary.Id).Text == "");

        var rulesSource = Source(ConversationSource.Notification, Capture()); var rulesSummary = Save(rulesSource);
        store.SaveRules("以新的用户规则表达。");
        check("rule_change_excludes_old_generated_sources_and_summary", !Available(rulesSource)
            && !store.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(rulesSummary))
            && store.CaptureMemoryView().Entries.Single(e => e.Id == rulesSummary.Id).Text == "");
        var policySource = Source(ConversationSource.Assistant, Capture()); store.AdvancePersonalizationRevision();
        check("policy_revision_change_does_not_resurrect_old_source", !Available(policySource));

        var current = Capture();
        var unknown = Source(ConversationSource.Assistant, OutputDependencies.LegacyUnknown);
        var missing = Source(ConversationSource.ToolResult, null);
        var forged = Source(ConversationSource.Assistant, current with { Contexts = current.Contexts.Select(c => c.Provider == AssistantMemoryDependencies.MemoryProvider ? c with { Fingerprint = new string('0', 64) } : c).ToImmutableArray() });
        var project = Source(ConversationSource.Report, current with { Contexts = current.Contexts.Add(new("project-proof", store.Profile, 1, "opaque", null, [])) });
        check("unknown_missing_tampered_and_project_contexts_are_not_relabelled_personal", new[] { unknown, missing, forged, project }.All(s => !Available(s)));
        var independent = Source(ConversationSource.ToolResult, OutputDependencies.Independent);
        check("explicitly_independent_receipt_has_no_invented_memory_dependencies", Save(independent).Metadata!.Dependencies.IsEmpty);

        var timedProof = Capture();
        var expiry = DateTimeOffset.UtcNow.AddMilliseconds(450);
        var timed = Source(ConversationSource.Notification, timedProof with { Contexts = timedProof.Contexts.Select(c => c with { ValidUntilUtc = expiry }).ToImmutableArray() });
        var timedSummary = Save(timed);
        await Task.Delay(650);
        check("source_context_expiry_is_inherited_without_another_write", timedSummary.Metadata!.ExpiresAtUtc == expiry
            && !Available(timed) && !store.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(timedSummary)));
        check("source_labels_preserve_claim_and_receipt_distinctions", EpisodeMemoryEditorWindow.SourceLabel(ConversationSource.Assistant).Contains("旧回答")
            && EpisodeMemoryEditorWindow.SourceLabel(ConversationSource.Notification).Contains("非当前状态")
            && EpisodeMemoryEditorWindow.SourceLabel(ConversationSource.ToolResult).Contains("回执"));
    }

    private static async Task Runtime(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory);
        var memory = f.Model.Memory; var original = memory.AddFromEditor("SYNTHETIC_RUNTIME_SOURCE");
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("旧回答仅代表当时说法。"));
        await f.InputAsync("plain", "谈谈本次安排。"); await ConversationHistoryRuntimeTests.Settled(f, "plain");
        var saved = await f.ReadAsync();
        var accepted = saved.Events.Last(e => e.Source == ConversationSource.Assistant);
        var projected = f.Ledger.ReadCommittedEventEvidence(f.Runtime.Identity!, accepted.Id)!;
        var snapshot = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        var selected = snapshot.Sources.Single(e => e.Id == accepted.Id);
        check("accepted_reply_uses_actual_producing_generation_dependencies", accepted.OutputDependencies == null
            && projected.Event.OutputDependencies!.Mode == OutputDependencyMode.Bound && selected.Event == projected.Event);
        var summary = memory.SaveEpisodeFromEditor(snapshot, "用户审阅旧回答。", [EpisodeEventReference.From(selected)]);
        check("actual_host_saves_derived_reply_and_preserves_it_after_commit", summary.Metadata!.Dependencies.Contains(AssistantMemoryLayers.Reference(original))
            && memory.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));

        ToolModelRequest? observed = null;
        f.Script.Handler = (request, _) =>
        {
            observed = request;
            return Task.FromResult(request.Observations.IsEmpty
                ? ToolModelResponse.Call(new ModelToolCall("source", "read_history_event", JsonSerializer.Serialize(new { eventId = accepted.Id })))
                : ToolModelResponse.Reply("这是摘要所用的旧回答。"));
        };
        await f.InputAsync("read-source", "核对这份摘要的原回答。"); await ConversationHistoryRuntimeTests.Settled(f, "read-source");
        using var sourceRead = JsonDocument.Parse(observed!.Observations.Single().ResultJson);
        check("reviewed_summary_original_is_readable_after_its_own_save", sourceRead.RootElement.GetProperty("facts").GetProperty("record")
            .GetProperty("text").GetString() == accepted.Body);
        f.Settings.ChatGpt.IncludeTaskContext = false;
        check("task_context_off_excludes_older_generated_sources_without_claiming_their_original_context_is_known",
            !(await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None)).Sources.Any(e => e.Id == accepted.Id)
            && !memory.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        f.Settings.ChatGpt.IncludeTaskContext = true;

        f.Script.Handler = (request, _) => Task.FromResult(request.Observations.IsEmpty
            ? ToolModelResponse.Call(new ModelToolCall("tasks", "list_tasks", "{}")) : ToolModelResponse.Reply("这是查询时的任务列表。"));
        await f.InputAsync("tasks", "查看任务列表。"); await ConversationHistoryRuntimeTests.Settled(f, "tasks");
        snapshot = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        check("actual_tool_result_is_available_with_invocation_context", snapshot.Sources.Any(e => e.Event.Source == ConversationSource.ToolResult
            && e.Event.RelatedInvocationId != null && e.Event.OutputDependencies?.Mode == OutputDependencyMode.Bound));

        await f.InputAsync("memory-list", "查看记忆"); await ConversationHistoryRuntimeTests.Settled(f, "memory-list");
        saved = await f.ReadAsync();
        var listGeneration = saved.Generations.Single(g => g.InboxIds.Any(id => saved.Inbox.Single(i => i.Id == id).SourceMessageId == WeChatText.Hash("memory-list")));
        var listOutputs = saved.Outbox.Where(o => o.GenerationId == listGeneration.Id).Select(o => o.Id).ToHashSet();
        snapshot = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        check("memory_management_replies_are_not_reusable_episode_sources", saved.Events.Any(e => e.RelatedOutboxId is { } id && listOutputs.Contains(id))
            && snapshot.Sources.All(e => e.Event.RelatedOutboxId is not { } id || !listOutputs.Contains(id)));
        memory.Delete(original.Id);
        check("actual_host_deletion_clears_summary_and_removes_derived_sources", memory.Entries.Single(e => e.Id == summary.Id).Text == ""
            && !(await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None)).Sources.Any(e => e.Id == accepted.Id));

        await f.Runtime.StopSessionAsync();
        // A separate synthetic identity has no active catch-up window. This test exercises the committed
        // approval projection, not the runtime's independent approval-provider revalidation policy.
        var identity = new ConversationIdentity("synthetic-approval-user", "synthetic-approval-binding", "main");
        var decision = await f.Ledger.EnsureRequiredDecisionNotificationAsync(identity, "synthetic-decision",
            new("local", "synthetic-task", "synthetic-turn"),
            new("synthetic-provider", "synthetic-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2)), "需要决定的合成通知");
        var output = decision.Notification.Outbox.Single();
        await f.Ledger.BeginOutboxSendAsync(identity, output.Id);
        await f.Ledger.RecordOutboxOutcomeAsync(identity, output.Id, OutboxStage.Accepted, "synthetic transport acceptance");
        var deliveredDecision = (await f.Ledger.ReadConversationAsync(identity)).Events.Single(e => e.RelatedOutboxId == output.Id);
        var decisionEvidence = f.Ledger.ReadCommittedEventEvidence(identity, deliveredDecision.Id)!;
        check("accepted_approval_notice_keeps_approval_identity_and_cannot_become_summary_source",
            decisionEvidence.Event.RequiredDecision == decision.Notification.Event.RequiredDecision
            && !AssistantMemoryStore.EpisodeSourceEligible(decisionEvidence.Event));
    }
}
