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

internal static class EpisodeProjectTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--episode-project-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "project-episode-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try { await Store(Path.Combine(directory, "store"), Check); await Runtime(Path.Combine(directory, "runtime"), Check); exit(0); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
    private sealed class Resolver : IProjectMemoryResolver
    {
        internal ProjectMemoryTarget? Target;
        public ProjectMemoryTarget? Resolve(TaskReference task) => Target?.Task == task ? Target : null;
    }
    private static async Task Store(string directory, Action<string, bool> check)
    {
        var identity = new ConversationIdentity("synthetic", "project-episode", "main");
        var events = new Dictionary<Guid, ConversationEvent>();
        var a = ProjectMemoryTests.Target("A"); var b = ProjectMemoryTests.Target("B");
        var resolver = new Resolver { Target = a };
        var store = new AssistantMemoryStore(directory, () => "project-episode-owner")
        {
            EpisodeSourceReader = (owner, id) => owner == identity ? events.GetValueOrDefault(id) : null,
            EpisodeProjectCurrent = target => resolver.Target is { } current && ProjectMemoryProof.SameEvidence(target, current)
        };
        store.Initialize();
        var personal = store.AddFromEditor("SYNTHETIC_PERSONAL_SOURCE");
        var project = store.SaveProjectFromEditor(store.CaptureProjectEditor(a, resolver), "SYNTHETIC_PROJECT_A_SOURCE", resolver);
        resolver.Target = b;
        store.SaveProjectFromEditor(store.CaptureProjectEditor(b, resolver), "PRIVATE_PROJECT_B", resolver);
        resolver.Target = a;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        int index = doc.Entries.FindIndex(e => e.Id == personal.Id);
        doc.Entries[index] = personal = personal with { Metadata = personal.Metadata! with { ExpiresAtUtc = deadline } };
        File.WriteAllText(store.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(doc, Settings.Json)));
        var recorded = DateTimeOffset.UtcNow.AddMinutes(-10);
        var original = a with { ValidUntilUtc = recorded.AddMinutes(2) };
        OutputDependencies Capture(ProjectMemoryTarget target) => AssistantMemoryDependencies.ToOutputDependencies(store.CaptureForGeneration(true, recorded, target));
        ConversationEvent Source(OutputDependencies? proof, TaskReference? task = null, string body = "SYNTHETIC_OLD_PROJECT_REPORT")
        {
            var e = new ConversationEvent(Guid.NewGuid(), identity, ConversationSource.Report, body, recorded, recorded, 1, Task: task, OutputDependencies: proof);
            events.Add(e.Id, e); return e;
        }
        var source = Source(Capture(original), original.Task);
        var foreign = Source(Capture(b with { ValidUntilUtc = recorded.AddMinutes(2) }), b.Task, "PRIVATE_OTHER_PROJECT_REPORT");
        var wrongTask = Source(source.OutputDependencies, b.Task);
        var unexplainedDeadline = Source(source.OutputDependencies! with { Contexts = source.OutputDependencies!.Contexts.Select(c =>
            c.Provider == AssistantMemoryDependencies.MemoryProvider ? c with { ValidUntilUtc = recorded.AddMinutes(1) } : c).ToImmutableArray() }, a.Task);
        var unproven = Source(null, a.Task);
        var personalSource = Source(OutputDependencies.Independent, body: "SYNTHETIC_PERSONAL_HISTORY");
        EpisodeMemoryEditorSnapshot Snapshot(string? id = null) => store.CaptureEpisodeSources(events.Values.Select(e => (EpisodeSource)e!), id, a);
        var snapshot = Snapshot();
        check("expired_original_project_proof_does_not_expire_archived_source", snapshot.Sources.Any(e => e.Id == source.Id));
        check("cross_project_task_mismatch_and_unproven_task_sources_excluded", new[] { foreign, wrongTask, unproven }.All(e => !snapshot.Sources.Any(s => s.Id == e.Id)));
        check("unexplained_memory_deadline_not_discarded_as_project_proof_expiry", !snapshot.Sources.Any(s => s.Id == unexplainedDeadline.Id));
        check("project_sources_never_relabelled_as_personal", !store.CaptureEpisodeEditor(events.Values).Sources.Any(e => e.Id == source.Id));
        var summary = store.SaveEpisodeFromEditor(snapshot, "项目历史摘要：仅为原报告的说法。", [EpisodeEventReference.From(source), EpisodeEventReference.From(personalSource)]);
        check("stable_project_scope_and_real_memory_deadline_saved", summary.Metadata!.Scope.ProjectReference == a.Project.Reference
            && summary.Metadata.ExpiresAtUtc == deadline && summary.Metadata.Dependencies.Select(d => d.Id).ToHashSet().SetEquals([personal.Id, project.Id]));
        bool Included(ProjectMemoryTarget? target, DateTimeOffset? when = null) => store.Capture(new(when ?? DateTimeOffset.UtcNow, target)).Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary));
        check("summary_read_requires_current_matching_project", Included(a) && !Included(null) && !Included(b));
        var task2 = a.Task with { ThreadId = "new-task-same-project", TurnId = "new-turn" };
        var sameProject = a with { Task = task2, EvidenceFingerprint = ProjectMemoryProof.Fingerprint(a.Project, task2, a.EvidenceSource) };
        check("new_task_in_same_exact_project_can_retrieve_archived_summary", Included(sameProject));
        check("summary_is_on_demand_and_old_receipt_not_current_output", !store.Capture(new(DateTimeOffset.UtcNow, a)).Context.Contains(summary.Text)
            && !OutputDependencies.Equivalent(source.OutputDependencies, AssistantMemoryDependencies.ToOutputDependencies(store.CaptureForGeneration(true, DateTimeOffset.UtcNow, a))));
        store.AddFromEditor("UNRELATED_NEW_PERSONAL_MEMORY");
        check("unrelated_memory_addition_preserves_project_archive", Included(a));
        doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        var reopened = new AssistantMemoryStore(directory, () => "project-episode-owner") { EpisodeSourceReader = store.EpisodeSourceReader, EpisodeProjectCurrent = store.EpisodeProjectCurrent };
        check("protected_v5_roundtrip_survives_later_personal_write", doc.FormatVersion == 5 && reopened.Entries.Single(e => e.Id == summary.Id) == summary
            && !File.ReadAllText(store.DataPath!).Contains(summary.Text) && reopened.Capture(new(DateTimeOffset.UtcNow, a)).Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        check("actual_source_expiry_still_removes_project_summary", !Included(a with { ValidUntilUtc = deadline.AddMinutes(2) }, deadline.AddSeconds(1)));
        bool expiredRejected = false;
        try { store.CaptureEpisodeSources(events.Values.Select(e => (EpisodeSource)e!), project: original); }
        catch (ArgumentException) { expiredRejected = true; }
        check("expired_current_proof_cannot_open_project_editor", expiredRejected);
        snapshot = Snapshot(summary.Id);
        var pending = new TaskCompletionSource<EpisodeSummaryDraft>();
        var editor = new EpisodeMemoryEditorWindow(store, snapshot, (_, refs, _) => pending.Task);
        var draftTask = editor.TryGenerateDraftAsync();
        resolver.Target = b; editor.RefreshSources();
        pending.SetResult(new("LATE_PRIVATE_PROJECT_DRAFT", summary.Metadata.Events));
        check("project_switch_clears_original_and_draft_and_rejects_late_result", !await draftTask && editor.Original.Text == "" && editor.Body.Text == "" && !editor.Save.IsEnabled);
        bool switchedRejected = false;
        try { store.SaveEpisodeFromEditor(snapshot, "STALE_PROJECT_WRITE", summary.Metadata.Events); }
        catch (InvalidOperationException) { switchedRejected = true; }
        check("project_switch_blocks_stale_save", switchedRejected && store.Entries.Single(e => e.Id == summary.Id) == summary);
        editor.Close(); resolver.Target = a;
        var corrected = store.SaveEpisodeFromEditor(Snapshot(summary.Id), "经再次审阅的项目历史说法。", summary.Metadata.Events);
        check("correction_preserves_project_and_exact_sources", corrected.Metadata!.Version == 2 && corrected.Metadata.Scope == summary.Metadata.Scope
            && corrected.Metadata.Events.SequenceEqual(summary.Metadata.Events));
        store.Update(personal.Id, "CORRECTED_PERSONAL_SOURCE");
        check("personal_source_correction_clears_project_derived_prose", store.Entries.Single(e => e.Id == summary.Id).Text == ""
            && !Snapshot().Sources.Any(e => e.Id == source.Id));
        var onlyPersonal = store.SaveEpisodeFromEditor(Snapshot(), "此项目的明确个人讨论整理。", [EpisodeEventReference.From(personalSource)]);
        store.Delete(project.Id);
        var selected = store.CaptureForGeneration(true, DateTimeOffset.UtcNow, a);
        check("project_summary_alone_still_binds_generation_to_project", selected.Content!.Manifest!.Project != null
            && selected.Content.Manifest.IncludedReferences.Contains(AssistantMemoryLayers.Reference(onlyPersonal)));
        store.Delete(onlyPersonal.Id);
        doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        check("deletion_preserves_v5_and_original_journal", doc.FormatVersion == 5 && events[source.Id] == source);
    }
    private static async Task Runtime(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory);
        string otherPath = Path.Combine(directory, "project-B"); Directory.CreateDirectory(otherPath);
        f.ObservedTasks = [f.ObservedTask, new TaskRecord { Id = "episode-project-B", Title = "Beta", Host = "本机", Cwd = otherPath,
            TurnId = "episode-project-turn-B", Status = TaskStatusNames.Completed, RecordedStatus = TaskStatusNames.Completed }];
        async Task Input(string id, string text) { await f.InputAsync(id, text); await ConversationHistoryRuntimeTests.Settled(f, id); }
        await Input("project-select", "切换到 Alpha");
        var memory = f.Model.Memory;
        var target = (await f.Model.ProjectMemoryTargetProvider!(CancellationToken.None))!;
        var resolver = f.Model.ProjectMemoryResolver!;
        var project = memory.SaveProjectFromEditor(memory.CaptureProjectEditor(target, resolver), "SYNTHETIC_RUNTIME_PROJECT_CONTEXT", resolver);
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_PROJECT_ANSWER"));
        await Input("project-answer", "说明本项目的约定。");
        var source = (await f.ReadAsync()).Events.Single(e => e.Source == ConversationSource.Assistant && e.Body == "SYNTHETIC_PROJECT_ANSWER");
        var snapshot = await f.Model.ProjectEpisodeMemoryProvider!(null, CancellationToken.None);
        check("actual_host_project_provider_reads_project_output_evidence", snapshot.Project?.Project == target.Project && snapshot.Sources.Any(e => e.Id == source.Id)
            && !(await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None)).Sources.Any(e => e.Id == source.Id));
        var summary = memory.SaveEpisodeFromEditor(snapshot, "SYNTHETIC_PROJECT_SUMMARY：原回答中的项目约定。", [snapshot.Sources.Single(e => e.Id == source.Id).Reference]);
        ToolModelRequest? last = null;
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.Length switch
            {
                0 => ToolModelResponse.Call(new ModelToolCall("project-summary", "search_episode_summaries", "{\"query\":\"SYNTHETIC_PROJECT_SUMMARY\"}")),
                1 => ToolModelResponse.Call(new ModelToolCall("project-source", "read_history_event", JsonSerializer.Serialize(new { eventId = source.Id }))),
                _ => ToolModelResponse.Reply("原记录是历史说法，未独立核验。")
            });
        };
        await Input("project-lookup", "查刚整理的项目摘要和原文。");
        using var lookup = JsonDocument.Parse(last!.Observations[0].ResultJson);
        using var original = JsonDocument.Parse(last.Observations[1].ResultJson);
        var row = lookup.RootElement.GetProperty("facts").GetProperty("summaries")[0];
        check("actual_tool_roundtrip_returns_scoped_summary_and_exact_original", row.GetProperty("id").GetString() == summary.Id
            && row.GetProperty("projectScope").GetString() == target.Project.Reference
            && original.RootElement.GetProperty("facts").GetProperty("record").GetProperty("eventId").GetGuid() == source.Id
            && !row.GetProperty("currentAuthorization").GetBoolean());
        var editor = await f.Model.CreateEpisodeMemoryEditorAsync(summary.Id, CancellationToken.None);
        check("reopen_routes_existing_summary_to_project_editor", editor.Title.Contains("当前项目") && editor.Body.Text == summary.Text);
        await Input("project-switch-B", "切换到 Beta");
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.IsEmpty
                ? ToolModelResponse.Call(new ModelToolCall("other-project-summary", "search_episode_summaries", "{\"query\":\"SYNTHETIC_PROJECT_SUMMARY\"}"))
                : ToolModelResponse.Reply("当前范围没有这条摘要。"));
        };
        await Input("other-project-lookup", "查刚整理的项目摘要。");
        using var other = JsonDocument.Parse(last!.Observations.Single().ResultJson);
        check("actual_other_project_lookup_omits_summary_and_prior_tool_prose", other.RootElement.GetProperty("facts").GetProperty("total").GetInt32() == 0
            && !last.Context.Any(e => e.Body.Contains(summary.Text)));
        await f.Runtime.StopSessionAsync();
        var identity = source.Identity;
        await f.Ledger.ReceiveAsync(identity, "synthetic-focus-change", "开始新对话", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
        await f.Ledger.BeginNewDialogueAsync(identity, "synthetic-focus-change");
        editor.RefreshSources();
        check("committed_focus_change_clears_open_project_episode_editor", editor.Body.Text == "" && editor.Original.Text == "" && !editor.Save.IsEnabled
            && f.Ledger.ReadCommittedConversationContext(identity)?.Focus == null);
        editor.Close();
        memory.Delete(project.Id);
        check("project_source_deletion_clears_summary_but_keeps_accepted_history", memory.Entries.Single(e => e.Id == summary.Id).Text == ""
            && f.Ledger.ReadCommittedEvent(identity, source.Id) == source && (await f.Ledger.ReadConversationAsync(identity)).Outbox.Any(o => o.Stage == OutboxStage.Accepted));
    }
}
