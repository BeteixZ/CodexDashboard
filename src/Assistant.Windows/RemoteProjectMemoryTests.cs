using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class RemoteProjectMemoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--remote-project-memory-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "remote-project-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try { Store(Path.Combine(directory, "store"), Check); await Runtime(Path.Combine(directory, "runtime"), Check); exit(0); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
    private sealed class NoTransport : ITaskTransport
    {
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
            => throw new InvalidOperationException("remote_memory_must_not_control_tasks");
    }
    private sealed class NoWindowsPaths : IWorkspacePathResolver
    {
        public WorkspaceScope? Resolve(WorkspaceLocation location) => throw new InvalidOperationException("remote_path_must_not_use_windows_resolution");
    }
    private static TaskRecord Record()
    {
        // Exercise the real collector JSON contract before the gateway takes its immutable copy.
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, tasks = new[] { new { id = "synthetic-remote-task", title = "Alpha",
            cwd = "/alias/Project", turnId = "synthetic-turn", status = "completed", recordedStatus = "completed",
            projectEvidence = new { version = 1, kind = "linux-directory/v1", sourceIdentity = new string('a', 64),
                requestedDirectory = "/alias/Project", canonicalDirectory = "/work/Project", directoryIdentity = "8:c" } } } });
        var task = JsonSerializer.Deserialize<CollectorResult>(json, SharedJson.Options)!.Tasks.Single();
        task.Host = "SYNTHETIC_REMOTE"; return task;
    }
    private static void Store(string directory, Action<string, bool> check)
    {
        var task = Record(); var proof = task.ProjectEvidence!;
        var now = DateTimeOffset.UtcNow;
        var host = new HostSnapshot { Name = task.Host, LastSuccess = now };
        bool configured = true;
        var gateway = new CodexGateway(new Settings(), () => new([task], now, [host]), new NoTransport(), () => now);
        var resolver = new ProjectMemoryResolver(gateway, () => null, new NoWindowsPaths(), () => now,
            remoteConfigured: _ => configured);
        var reference = new TaskReference(HostIdentity.ToHostId(task.Host), task.Id, task.TurnId);
        var target = resolver.Resolve(reference)!;
        check("collector_json_and_gateway_copy_preserve_linux_identity", target?.Project is { RemoteSource: not null, DirectoryIdentity: "8:c" }
            && target.Project.CanonicalDirectory == "/work/Project" && target.Project.RemoteSource == proof.SourceIdentity
            && ProjectMemoryProof.ParseReference(target.Project.Reference) == target.Project);
        var store = new AssistantMemoryStore(directory, () => "synthetic-remote-memory-owner"); store.Initialize();
        var entry = store.SaveProjectFromEditor(store.CaptureProjectEditor(target!, resolver), "SYNTHETIC_REMOTE_KNOWLEDGE", resolver);
        var captured = store.CaptureForGeneration(true, DateTimeOffset.UtcNow, target);
        var doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        check("remote_knowledge_protected_v6_and_current_generation_bound", doc.FormatVersion == 6 && !File.ReadAllText(store.DataPath!).Contains(entry.Text)
            && captured.Content!.Manifest!.Project!.Project == target!.Project && captured.Content.Context.Contains(entry.Text)
            && !store.Capture().Context.Contains(entry.Text));
        check("remote_scope_ui_names_remote_host_and_canonical_directory", ProjectMemoryScopeConverter.Describe(entry).Contains(task.Host)
            && ProjectMemoryScopeConverter.Describe(entry).Contains("/work/Project") && !ProjectMemoryScopeConverter.Describe(entry).Contains("本机"));
        var reopened = new AssistantMemoryStore(directory, () => "synthetic-remote-memory-owner");
        check("remote_identity_roundtrips_from_protected_store", reopened.Entries.Single() == entry && reopened.Capture(new(DateTimeOffset.UtcNow, target)).Context.Contains(entry.Text));
        var before = store.CaptureProjectEditor(target!, resolver, entry.Id);
        task.Cwd = "/other-alias/Project"; task.ProjectEvidence = proof with { RequestedDirectory = task.Cwd };
        check("same_opened_directory_alias_keeps_scope", ProjectMemoryProof.SameEvidence(target!, resolver.Resolve(reference)!));
        task.Cwd = proof.RequestedDirectory;
        task.ProjectEvidence = proof with { SourceIdentity = new string('b', 64) };
        var replacement = resolver.Resolve(reference)!;
        bool rejected = false;
        try { store.SaveProjectFromEditor(before, "STALE_REMOTE_WRITE", resolver); }
        catch (InvalidOperationException) { rejected = true; }
        check("same_alias_changed_machine_account_or_configuration_cannot_reuse_memory", rejected && replacement.Project != target!.Project
            && !store.Capture(new(DateTimeOffset.UtcNow, replacement)).Context.Contains(entry.Text) && store.Entries.Single() == entry);
        task.ProjectEvidence = proof with { DirectoryIdentity = "8:d" };
        check("replaced_directory_identity_cannot_reuse_memory", !store.Capture(new(DateTimeOffset.UtcNow, resolver.Resolve(reference))).Context.Contains(entry.Text));
        task.ProjectEvidence = proof with { CanonicalDirectory = "/work/project", DirectoryIdentity = "8:e" };
        check("linux_case_is_not_folded_with_windows_rules", resolver.Resolve(reference)!.Project.CanonicalDirectory == "/work/project"
            && !store.Capture(new(DateTimeOffset.UtcNow, resolver.Resolve(reference))).Context.Contains(entry.Text));
        task.ProjectEvidence = proof;
        task.Host = host.Name = "SYNTHETIC_OTHER_ALIAS";
        var aliasTarget = resolver.Resolve(reference with { HostId = HostIdentity.ToHostId(task.Host) })!;
        check("different_configured_alias_never_automatically_shares_saved_scope", aliasTarget.Project.Reference != target!.Project.Reference
            && !store.Capture(new(DateTimeOffset.UtcNow, aliasTarget)).Context.Contains(entry.Text));
        task.Host = host.Name = "SYNTHETIC_REMOTE";
        foreach (var invalid in new ProjectDirectoryEvidence?[] { null, proof with { Version = 2 }, proof with { Kind = "macos-directory/v1" },
            proof with { RequestedDirectory = "/different/path" }, proof with { SourceIdentity = "unverified" }, proof with { CanonicalDirectory = "/work/../Project" } })
        {
            task.ProjectEvidence = invalid;
            if (resolver.Resolve(reference) != null) throw new InvalidOperationException("invalid_remote_proof_accepted");
        }
        check("old_unknown_mismatched_or_noncanonical_evidence_has_no_path_fallback", true);
        task.ProjectEvidence = proof; task.Disconnected = true;
        check("disconnected_cached_task_cannot_supply_project_context", resolver.Resolve(reference) == null);
        task.Disconnected = false; host.Error = "synthetic offline";
        check("host_error_cannot_refresh_cached_project_evidence", resolver.Resolve(reference) == null);
        host.Error = null; now = now.AddMinutes(3);
        check("stale_observation_cannot_supply_project_context", resolver.Resolve(reference) == null);
        now = DateTimeOffset.UtcNow; host.LastSuccess = now; configured = false;
        check("removed_remote_configuration_stops_scope_without_waiting_for_refresh", resolver.Resolve(reference) == null);
        configured = true;
        check("unknown_remote_profile_not_guessed_from_default_collector", resolver.Resolve(reference with { ProfileId = "other-profile" }) == null);
        var owner = new ConversationIdentity("synthetic", "remote-episode", "main");
        var source = new ConversationEvent(Guid.NewGuid(), owner, ConversationSource.Report, "SYNTHETIC_REMOTE_REPORT", now, now, 1,
            Task: reference, OutputDependencies: AssistantMemoryDependencies.ToOutputDependencies(store.CaptureForGeneration(true, now, target)));
        store.EpisodeSourceReader = (identity, id) => identity == owner && id == source.Id ? source : null;
        store.EpisodeProjectCurrent = expected => resolver.Resolve(expected.Task) is { } actual && ProjectMemoryProof.SameEvidence(expected, actual);
        var episode = store.SaveEpisodeFromEditor(store.CaptureEpisodeSources([(EpisodeSource)source!], project: target),
            "远程报告的历史说法，尚未独立核验。", [EpisodeEventReference.From(source)]);
        check("remote_episode_inherits_exact_project_memory_dependency", episode.Metadata!.Scope.ProjectReference == target!.Project.Reference
            && episode.Metadata.Dependencies.Single() == AssistantMemoryLayers.Reference(entry)
            && store.Capture(new(DateTimeOffset.UtcNow, target)).Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(episode)));
        var updated = store.SaveProjectFromEditor(store.CaptureProjectEditor(target!, resolver, entry.Id), "CORRECTED_REMOTE_KNOWLEDGE", resolver);
        check("remote_knowledge_correction_clears_derived_episode_prose", updated.Metadata!.Version == 2 && store.Entries.Single(e => e.Id == episode.Id).Text == "");
        store.AddFromEditor("PERSONAL_AFTER_REMOTE"); store.Delete(entry.Id); store.Delete(episode.Id);
        doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        check("personal_writes_and_deletion_preserve_v6", doc.FormatVersion == 6 && doc.Entries.Count == 1);
    }
    private static async Task Runtime(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, syntheticTask: Record());
        async Task Input(string id, string text) { await f.InputAsync(id, text); await ConversationHistoryRuntimeTests.Settled(f, id); }
        await Input("select-remote", "切换到 Alpha");
        var editor = await f.Model.CreateProjectMemoryEditorAsync();
        editor.BodyBox.Text = "SYNTHETIC_REMOTE_RUNTIME_KNOWLEDGE"; editor.ReviewedBox.IsChecked = true;
        bool saved = await editor.TrySaveAsync(); var entry = editor.SavedEntry!;
        check("actual_remote_editor_saves_only_explicitly_reviewed_current_scope", saved && editor.TargetText.Text.Contains("SYNTHETIC_REMOTE")
            && editor.ScopeDetailsText.Text.Contains(f.ObservedTask.ProjectEvidence!.SourceIdentity) && !f.Settings.ChatGpt.TaskControlEnabled);
        editor.Close();
        ToolModelRequest? last = null;
        AssistantGenerationPersonalization? capture = null;
        f.Script.Handler = (request, _) => { last = request; capture = f.Model.GenerationPersonalizationProvider!(request.GenerationContext!);
            return Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_REMOTE_ANSWER")); };
        await Input("remote-answer", "说说这个项目的约定。");
        check("actual_generation_uses_remote_identity_bound_memory", capture!.Content!.Context.Contains(entry.Text)
            && capture.Content.Manifest!.Project!.Project.RemoteSource == f.ObservedTask.ProjectEvidence!.SourceIdentity);
        var source = (await f.ReadAsync()).Events.Single(e => e.Source == ConversationSource.Assistant && e.Body == "SYNTHETIC_REMOTE_ANSWER");
        var snapshot = await f.Model.ProjectEpisodeMemoryProvider!(null, CancellationToken.None);
        var summary = f.Model.Memory.SaveEpisodeFromEditor(snapshot, "REMOTE_SUMMARY：原回答所说的约定。", [snapshot.Sources.Single(e => e.Id == source.Id).Reference]);
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.Length switch
            {
                0 => ToolModelResponse.Call(new ModelToolCall("remote-summary", "search_episode_summaries", "{\"query\":\"REMOTE_SUMMARY\"}")),
                1 => ToolModelResponse.Call(new ModelToolCall("remote-source", "read_history_event", JsonSerializer.Serialize(new { eventId = source.Id }))),
                _ => ToolModelResponse.Reply("这是原记录中的说法。")
            });
        };
        await Input("remote-lookup", "回顾这个项目的摘要和原文。");
        using var lookup = JsonDocument.Parse(last!.Observations[0].ResultJson);
        using var read = JsonDocument.Parse(last.Observations[1].ResultJson);
        check("actual_remote_summary_and_source_tool_roundtrip", lookup.RootElement.GetProperty("facts").GetProperty("summaries")[0].GetProperty("id").GetString() == summary.Id
            && read.RootElement.GetProperty("facts").GetProperty("record").GetProperty("eventId").GetGuid() == source.Id);
        var episodeEditor = await f.Model.CreateEpisodeMemoryEditorAsync(summary.Id, CancellationToken.None);
        var originalProof = f.ObservedTask.ProjectEvidence!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Script.Handler = async (request, _) => { last = request; entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply("PRIVATE_OLD_REMOTE_ANSWER"); };
        try
        {
            await f.InputAsync("remote-race", "继续说说项目约定。"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            f.ObservedTask.ProjectEvidence = originalProof with { SourceIdentity = new string('c', 64) };
            episodeEditor.RefreshSources();
            check("alias_retarget_clears_open_remote_episode_editor", episodeEditor.Body.Text == "" && episodeEditor.Original.Text == "" && !episodeEditor.Save.IsEnabled);
            bool captureRejected = false;
            try { f.Model.GenerationPersonalizationProvider!(last!.GenerationContext!); }
            catch (ChatGptException) { captureRejected = true; }
            check("inflight_generation_proof_rejected_after_remote_identity_change", captureRejected);
        }
        finally { release.TrySetResult(); episodeEditor.Close(); }
        await ConversationHistoryRuntimeTests.WaitAsync(async () => (await f.ReadAsync()).Generations.All(g => g.Stage != GenerationStage.Processing));
        check("retarget_during_generation_never_prepares_or_sends_old_prose", !(await f.ReadAsync()).Outbox.Any(o => o.Body.Contains("PRIVATE_OLD_REMOTE_ANSWER"))
            && !f.Sent.Any(s => s.Contains("PRIVATE_OLD_REMOTE_ANSWER")));
        f.Script.Handler = (request, _) => { last = request; return Task.FromResult(request.Observations.IsEmpty
            ? ToolModelResponse.Call(new ModelToolCall("retarget-summary", "search_episode_summaries", "{\"query\":\"REMOTE_SUMMARY\"}"))
            : ToolModelResponse.Reply("当前项目范围没有该摘要。")); };
        await Input("retarget-lookup", "再查这个项目的摘要。");
        using var changed = JsonDocument.Parse(last!.Observations.Single().ResultJson);
        check("actual_retargeted_host_lookup_never_reuses_old_project_prose", changed.RootElement.GetProperty("facts").GetProperty("total").GetInt32() == 0
            && !last.Context.Any(e => e.Body.Contains(summary.Text)));
        f.ObservedTask.ProjectEvidence = originalProof;
        var secondEditor = await f.Model.CreateProjectMemoryEditorAsync(entry.Id);
        f.Settings.RemoteHosts.Clear();
        check("removed_host_configuration_clears_open_remote_knowledge_editor", !await secondEditor.RefreshTargetAsync()
            && secondEditor.BodyBox.Text == "" && !secondEditor.SaveButton.IsEnabled);
        secondEditor.Close();
        check("remote_memory_did_not_create_control_operations", (await f.ReadAsync()).Operations.All(o => o.Action != "send_to_task") && f.Queue.Entries.Count == 0);
    }
}
