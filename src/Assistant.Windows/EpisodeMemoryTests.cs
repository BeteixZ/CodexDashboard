using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core;

namespace CodexDashboard;

internal static class EpisodeMemoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length == 3 && args[0] == "--archived-episode-reopen-test")
        { await ReopenProcess(args[1], args[2], exit); return true; }
        if (args.Length != 2 || (args[0] != "--episode-memory-test" && args[0] != "--archived-episode-memory-test"
            && args[0] != "--archived-memory-lifecycle-test")) return false;
        bool archived = args[0] == "--archived-episode-memory-test";
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "episode-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            if (args[0] == "--archived-memory-lifecycle-test") await ArchivedLifecycle(directory, Check);
            else
            {
                if (!archived) Store(Path.Combine(directory, "store"), Check);
                await Runtime(Path.Combine(directory, "runtime"), Check, archived);
            }
            exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
    private static async Task ArchivedLifecycle(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "runtime"), archiveLedger: true, persistMemory: true);
        await f.InputAsync("correct-source", "记住：我希望称呼我为 SYNTHETIC_CORRECTION_SOURCE");
        await ConversationHistoryRuntimeTests.Settled(f, "correct-source");
        var memory = f.Model.Memory;
        var original = memory.Entries.Single(e => e.Text.Contains("SYNTHETIC_CORRECTION_SOURCE"));
        var source = (await f.ReadAsync()).Events.Single(e => e.Source == ConversationSource.TrustedUser && e.Body.Contains("SYNTHETIC_CORRECTION_SOURCE"));
        var summary = memory.SaveEpisodeFromEditor(memory.CaptureEpisodeEditor([source]), "旧称呼的审阅摘要", [EpisodeEventReference.From(source)]);
        var captured = memory.CaptureForGeneration(true, DateTimeOffset.UtcNow);
        var stale = memory.CaptureEpisodeEditor([source], summary.Id);
        memory.Update(original.Id, "我希望称呼我为 SYNTHETIC_CORRECTED_NAME");
        check("archive_correction_invalidates_captured_answer_and_summary", !memory.IsGenerationPersonalizationCurrent(captured, true, DateTimeOffset.UtcNow)
            && memory.Entries.Single(e => e.Id == summary.Id).Text == "");
        bool rejected = false;
        try { memory.SaveEpisodeFromEditor(stale, "试图保存旧称呼", [EpisodeEventReference.From(source)]); }
        catch (InvalidOperationException) { rejected = true; }
        catch (ChatGptException) { rejected = true; }
        check("archive_correction_rejects_stale_editor_and_keeps_original_event", rejected
            && (await f.ReadAsync()).Events.Single(e => e.Id == source.Id) == source);
        var corrected = memory.Entries.Single(e => e.Id == original.Id);
        check("archive_correction_preserves_identity_and_advances_version", corrected.Id == original.Id
            && corrected.Metadata!.Version > original.Metadata!.Version && corrected.Text.Contains("SYNTHETIC_CORRECTED_NAME"));

        await f.InputAsync("expiry-source", "记住：我希望回复包含 SYNTHETIC_EXPIRY_SOURCE");
        await ConversationHistoryRuntimeTests.Settled(f, "expiry-source");
        File.WriteAllText(Path.Combine(directory, "expiry-input-state.json"), JsonSerializer.Serialize(new
        { entries = memory.Entries, conversation = await f.ReadAsync() }, Settings.Json));
        var timed = memory.Entries.Single(e => e.Text.Contains("SYNTHETIC_EXPIRY_SOURCE"));
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        memory.UpdateMetadata(timed.Id, timed.Metadata! with { ExpiresAtUtc = expiry }, memory.Capture().Stamp);
        f.Script.Handler = (_, _) => Task.FromResult(ToolModelResponse.Reply("SYNTHETIC_TIMED_ANSWER"));
        await f.InputAsync("expiry-answer", "请简单回应这条普通消息。");
        await ConversationHistoryRuntimeTests.Settled(f, "expiry-answer");
        var timedEvent = (await f.ReadAsync()).Events.Single(e => e.Source == ConversationSource.Assistant && e.Body == "SYNTHETIC_TIMED_ANSWER");
        var timedSources = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        var timedSource = timedSources.Sources.Single(item => item.Id == timedEvent.Id);
        var timedSummary = memory.SaveEpisodeFromEditor(timedSources, "限时偏好的摘要", [EpisodeEventReference.From(timedSource)]);
        var timedCapture = memory.CaptureForGeneration(true, DateTimeOffset.UtcNow);
        check("archived_summary_inherits_source_expiry", timedSummary.Metadata!.ExpiresAtUtc == expiry
            && timedCapture.Content!.Manifest!.ValidUntilUtc == expiry);
        check("archive_expiry_invalidates_answer_and_excludes_summary_without_waiting", !memory.IsGenerationPersonalizationCurrent(timedCapture, true, expiry)
            && memory.Capture(new(expiry)).Manifest!.IncludedReferences.All(item => item.Id != timedSummary.Id)
            && memory.CaptureHistoryFilter(expiry).IsEntryVersionRevoked(timedSummary.Id, 1));
        var reloaded = new AssistantMemoryStore(Path.Combine(directory, "runtime", "synthetic-memory"),
            () => f.Settings.ChatGpt.ActiveAccount + "\n" + f.Binding.BaseUrl + "\n" + f.Binding.BotId + "\n" + f.Binding.UserId)
        { EpisodeSourceReader = memory.EpisodeSourceReader };
        check("reloaded_protected_memory_preserves_correction_and_expiry", reloaded.Entries.Single(e => e.Id == original.Id).Text == corrected.Text
            && reloaded.Entries.Single(e => e.Id == summary.Id).Text == ""
            && reloaded.Capture(new(expiry)).Manifest!.IncludedReferences.All(item => item.Id != timedSummary.Id));
    }
    private static void Store(string directory, Action<string, bool> check)
    {
        string profile = "synthetic-episode-owner";
        var identity = new ConversationIdentity("user", "binding", "main");
        var now = DateTimeOffset.UtcNow;
        var original = new ConversationEvent(Guid.NewGuid(), identity, ConversationSource.Report, "报告自称完成，未独立核验。", now, now, 1,
            Task: new("local", "synthetic-task", "turn"));
        ConversationEvent? current = original;
        ConversationEvent? Read(ConversationIdentity owner, Guid id) => owner == identity && id == original.Id ? current : null;
        var store = new AssistantMemoryStore(directory, () => profile) { EpisodeSourceReader = (owner, id) => Read(owner, id) };
        var snapshot = store.CaptureEpisodeEditor([original]);
        var summary = store.SaveEpisodeFromEditor(snapshot, "本次报告自称完成。", [EpisodeEventReference.From(original)]);
        var reopened = new AssistantMemoryStore(directory, () => profile) { EpisodeSourceReader = (owner, id) => Read(owner, id) };
        var doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        check("protected_v4_preserves_exact_event_references_after_reopen", doc.FormatVersion == 4
            && reopened.Entries.Single() == summary && !File.ReadAllText(store.DataPath!).Contains(summary.Text)
            && summary.Metadata!.Events.Single() == EpisodeEventReference.From(original));
        var capture = reopened.CaptureForGeneration(true, now);
        check("summary_is_bound_but_not_automatically_injected", capture.Content!.Manifest!.IncludedReferences.Contains(new(summary.Id, 1))
            && !capture.Content.Context.Contains(summary.Text));
        var stale = reopened.CaptureEpisodeEditor([original], summary.Id);
        current = original with { Body = "报告已经纠正，原结论无效。" };
        bool rejected = false;
        try { reopened.SaveEpisodeFromEditor(stale, "旧结论", [EpisodeEventReference.From(original)]); }
        catch (InvalidOperationException) { rejected = true; }
        check("changed_original_invalidates_capture_and_rejects_stale_save", rejected
            && !reopened.IsGenerationPersonalizationCurrent(capture, true, DateTimeOffset.UtcNow)
            && reopened.Capture().Manifest!.IncludedReferences.IsEmpty);
        current = original;
        var editor = new EpisodeMemoryEditorWindow(reopened, reopened.CaptureEpisodeEditor([original], summary.Id));
        try
        {
            check("editor_shows_full_original_and_requires_review", editor.Original.Text.Contains(original.Body)
                && editor.Original.Text.Contains(original.Id.ToString()) && !editor.Save.IsEnabled);
            var visual = (FrameworkElement)editor.Content;
            visual.Measure(new Size(650, 610)); visual.Arrange(new Rect(0, 0, 650, 610)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(650, 610, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var canvas = drawing.RenderOpen())
            {
                canvas.DrawRectangle(editor.Background, null, new Rect(0, 0, 650, 610));
                canvas.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, 650, 610));
            }
            bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(directory, "episode-editor.png"))) png.Save(output);
            editor.Body.Text = "纠正后的审阅摘要，仍属报告自述。"; editor.Reviewed.IsChecked = true;
            check("editor_correction_persists_version_and_does_not_revoke_original", editor.TrySave()
                && editor.SavedEntry!.Metadata!.Version == 2 && reopened.Capture().Manifest!.IncludedReferences.Contains(new(summary.Id, 2))
                && reopened.CaptureHistoryFilter(DateTimeOffset.UtcNow).UnknownSourceHistoryCutoffUtc == null);
        }
        finally { editor.Close(); }
        var switched = new EpisodeMemoryEditorWindow(reopened, reopened.CaptureEpisodeEditor([original], summary.Id));
        try
        {
            profile = "synthetic-other-owner"; switched.Reviewed.IsChecked = true;
            check("binding_switch_clears_editor_and_prevents_save", !switched.TrySave() && switched.Body.Text.Length == 0
                && switched.Original.Text.Length == 0 && switched.Sources.Items.Count == 0 && reopened.Entries.Count == 0);
        }
        finally { switched.Close(); profile = "synthetic-episode-owner"; }
        reopened.Delete(summary.Id);
        doc = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(store.DataPath!)), Settings.Json)!;
        check("deleting_last_episode_keeps_v4_and_removes_summary_without_hiding_source", doc.FormatVersion == 4 && doc.Entries.Count == 0
            && !File.ReadAllText(store.DataPath!).Contains(summary.Text) && reopened.CaptureEpisodeEditor([original]).Sources.Length == 1);
    }
    private static async Task Runtime(string directory, Action<string, bool> check, bool archived = false)
    {
        const string marker = "SYNTHETIC_EPISODE_SOURCE";
        ConversationSnapshot beforeReopen;
        ConversationIdentity identity;
        string ledgerPath;
        Settings settings;
        AssistantMemoryStore persistedMemory;
        await using (var f = await DurableRuntimeTests.Fixture.CreateAsync(directory, archiveLedger: archived, persistMemory: archived))
        {
        await f.InputAsync("remember", "记住：我希望回复先给结论，称呼我为 " + marker);
        await ConversationHistoryRuntimeTests.Settled(f, "remember");
        var memory = f.Model.Memory;
        var sourceMemory = memory.Entries.Single(e => e.Text.Contains(marker));
        var source = (await f.ReadAsync()).Events.Single(e => e.Source == ConversationSource.TrustedUser && e.Body.Contains(marker));
        var report = await f.Ledger.AppendContextAsync(f.Runtime.Identity!, ConversationSource.Report,
            "测试任务报告声称测试完成。", DateTimeOffset.UtcNow, new("local", "task-a", "turn-a"));
        var foreign = await f.Ledger.AppendContextAsync(new("foreign", "foreign", "main"), ConversationSource.Report,
            "另一绑定的报告", DateTimeOffset.UtcNow);
        var snapshot = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        var summary = memory.SaveEpisodeFromEditor(snapshot, marker + "：用户要求先给结论；报告自称完成。",
            [EpisodeEventReference.From(source), EpisodeEventReference.From(report)]);
        check("host_snapshot_is_bound_and_summary_tracks_original_memory_dependency", !snapshot.Sources.Any(e => e.Id == foreign.Id)
            && summary.Metadata!.Dependencies.Contains(AssistantMemoryLayers.Reference(sourceMemory)));
        ToolModelRequest? last = null;
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.Length switch
            {
                0 => ToolModelResponse.Call(Call("summary", "search_episode_summaries", new { query = marker })),
                1 => ToolModelResponse.Call(Call("source", "read_history_event", new { eventId = report.Id })),
                _ => ToolModelResponse.Reply("这是用户审阅的摘要，报告自称完成，尚未独立核验。")
            });
        };
        await f.InputAsync("lookup", "查查整理过的那段工作和来源。");
        await ConversationHistoryRuntimeTests.Settled(f, "lookup");
        using var lookup = JsonDocument.Parse(last!.Observations[0].ResultJson);
        var row = lookup.RootElement.GetProperty("facts").GetProperty("summaries")[0];
        using var read = JsonDocument.Parse(last.Observations[1].ResultJson);
        check("actual_tool_chain_returns_summary_and_original_without_execution_authority", row.GetProperty("id").GetString() == summary.Id
            && row.GetProperty("sources").GetArrayLength() == 2 && !row.GetProperty("independentlyVerified").GetBoolean()
            && !row.GetProperty("currentAuthorization").GetBoolean()
            && read.RootElement.GetProperty("facts").GetProperty("record").GetProperty("eventId").GetGuid() == report.Id
            && (await f.ReadAsync()).Operations.All(o => o.Action != "send_to_task"));
        f.Settings.ChatGpt.IncludeTaskContext = false;
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.IsEmpty
                ? ToolModelResponse.Call(Call("private", "search_episode_summaries", new { query = marker })) : ToolModelResponse.Reply("没有适用摘要。"));
        };
        await f.InputAsync("private", "再查一下那段摘要。"); await ConversationHistoryRuntimeTests.Settled(f, "private");
        using var privateRead = JsonDocument.Parse(last!.Observations.Single().ResultJson);
        check("task_context_switch_excludes_task_derived_summaries", privateRead.RootElement.GetProperty("facts").GetProperty("summaries").GetArrayLength() == 0);
        f.Settings.ChatGpt.IncludeTaskContext = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Script.Handler = async (request, _) =>
        {
            if (request.Observations.IsEmpty) return ToolModelResponse.Call(Call("pending", "search_episode_summaries", new { query = marker }));
            entered.TrySetResult(); await release.Task; return ToolModelResponse.Reply(marker);
        };
        int sent = f.Sent.Count;
        await f.InputAsync("pending", "回忆一下那段称呼和工作。");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); memory.Delete(sourceMemory.Id); release.TrySetResult();
            await ConversationHistoryRuntimeTests.WaitAsync(async () => (await f.ReadAsync()).Generations.All(g => g.Stage != GenerationStage.Processing));
            var invalidated = memory.Entries.Single(e => e.Id == summary.Id);
            check("source_delete_clears_derived_prose_and_stops_pending_reply", invalidated.Text == "" && invalidated.Metadata!.InvalidatedAtUtc != null
                && !f.Sent.Skip(sent).Any(t => t.Contains(marker)));
        }
        finally { release.TrySetResult(); }
        f.Script.Handler = (request, _) =>
        {
            last = request;
            return Task.FromResult(request.Observations.IsEmpty
                ? ToolModelResponse.Call(Call("after", "search_episode_summaries", new { query = marker })) : ToolModelResponse.Reply("这份摘要已失效。"));
        };
        await f.InputAsync("after", "还能找到摘要吗？"); await ConversationHistoryRuntimeTests.Settled(f, "after");
        using var empty = JsonDocument.Parse(last!.Observations.Single().ResultJson);
        check("revoked_summary_and_lookup_answer_do_not_reenter_context", empty.RootElement.GetProperty("facts").GetProperty("summaries").GetArrayLength() == 0
            && !last.Context.Any(e => e.Body.Contains(marker)));
        identity = f.Runtime.Identity!;
        if (archived)
        {
            await f.StopChannelAsync();
            await f.Runtime.StopSessionAsync();
        }
        beforeReopen = await f.Ledger.ReadConversationAsync(identity);
        ledgerPath = f.LedgerPath;
        settings = f.Settings;
        persistedMemory = f.Model.Memory;
        if (archived)
            File.WriteAllText(Path.Combine(directory, "synthetic-reopen-probe.json"), JsonSerializer.Serialize(new ReopenProbe(
                settings, settings.ChatGpt.ActiveAccount + "\n" + f.Binding.BaseUrl + "\n" + f.Binding.BotId + "\n" + f.Binding.UserId,
                identity, summary.Id, sourceMemory.Id, source.Id, report.Id), Settings.Json));
        }
        if (archived)
        {
            var archive = await HistoryArchiveSet.OpenAsync(Path.Combine(directory, "history-archive"), new WindowsStateProtector());
            var provider = new AssistantModelContextProvider(settings, persistedMemory);
            await using var reopened = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath),
                new WindowsStateProtector(), dependencyValidator: provider, historyArchive: archive);
            provider.BindLedger(reopened);
            var restored = await reopened.ReadConversationAsync(identity);
            File.WriteAllText(Path.Combine(directory, "state-before-reopen.json"), JsonSerializer.Serialize(beforeReopen));
            File.WriteAllText(Path.Combine(directory, "state-after-reopen.json"), JsonSerializer.Serialize(restored));
            File.WriteAllText(Path.Combine(directory, "events-before-reopen.json"), JsonSerializer.Serialize(beforeReopen.Events));
            File.WriteAllText(Path.Combine(directory, "events-after-reopen.json"), JsonSerializer.Serialize(restored.Events));
            check("archive_reopen_preserves_every_original_event_and_source_identity",
                JsonSerializer.Serialize(restored.Events) == JsonSerializer.Serialize(beforeReopen.Events));
            check("archive_reopen_preserves_generation_and_delivery_state",
                JsonSerializer.Serialize(restored.Generations) == JsonSerializer.Serialize(beforeReopen.Generations)
                && JsonSerializer.Serialize(restored.Outbox) == JsonSerializer.Serialize(beforeReopen.Outbox));
        }
    }
    private sealed record ReopenProbe(Settings Settings, string MemoryIdentity, ConversationIdentity Identity,
        string SummaryId, string SourceMemoryId, Guid SourceEventId, Guid ReportEventId);
    private static async Task ReopenProcess(string source, string output, Action<int> exit)
    {
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        { checks.Add(name, passed); if (!passed) throw new InvalidOperationException(name); }
        try
        {
            var probe = JsonSerializer.Deserialize<ReopenProbe>(File.ReadAllText(Path.Combine(source, "synthetic-reopen-probe.json")), Settings.Json)!;
            var memory = new AssistantMemoryStore(Path.Combine(source, "synthetic-memory"), () => probe.MemoryIdentity);
            var provider = new AssistantModelContextProvider(probe.Settings, memory);
            var archive = await HistoryArchiveSet.OpenAsync(Path.Combine(source, "history-archive"), new WindowsStateProtector());
            await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(source, "synthetic-dialogue.state")),
                new WindowsStateProtector(), dependencyValidator: provider, historyArchive: archive);
            provider.BindLedger(ledger);
            var state = await ledger.ReadConversationAsync(probe.Identity);
            var summary = memory.Entries.Single(entry => entry.Id == probe.SummaryId);
            Check("fresh_process_loads_revocation_from_protected_memory_file", File.Exists(memory.DataPath)
                && summary.Text == "" && summary.Metadata!.InvalidatedAtUtc != null
                && memory.Entries.All(entry => entry.Id != probe.SourceMemoryId));
            Check("fresh_process_restores_all_original_archived_events", JsonSerializer.Serialize(state.Events)
                == File.ReadAllText(Path.Combine(source, "events-before-reopen.json")));
            var editor = memory.CaptureEpisodeEditor(state.Events);
            Check("fresh_process_excludes_revoked_source_but_keeps_independent_report", editor.Sources.All(item => item.Id != probe.SourceEventId)
                && editor.Sources.Any(item => item.Id == probe.ReportEventId));
            bool rejected = false;
            try { memory.CaptureEpisodeEditor(state.Events, probe.SummaryId); }
            catch (ChatGptException) { rejected = true; }
            Check("fresh_process_cannot_reopen_revoked_summary_for_reuse", rejected);
            var capture = memory.CaptureForGeneration(true, DateTimeOffset.UtcNow);
            Check("fresh_generation_capture_excludes_revoked_summary", capture.Content!.Manifest!.IncludedReferences.All(item => item.Id != probe.SummaryId));
            exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        finally { File.WriteAllText(Path.Combine(output, "reopen-checks.json"), JsonSerializer.Serialize(checks, Settings.Json)); }
    }
    private static ModelToolCall Call(string id, string name, object args) => new(id, name, JsonSerializer.Serialize(args));
}
