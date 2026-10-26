using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class EpisodeCatchUpTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--episode-catch-up-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed)
        {
            checks.Add(name, passed);
            File.WriteAllText(Path.Combine(directory, "episode-catch-up-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
            if (!passed) throw new InvalidOperationException(name);
        }
        try { await Run(directory, Check); exit(0); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }

    private static async Task Run(string directory, Action<string, bool> check)
    {
        await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(directory, "runtime"));
        var identity = f.Runtime.Identity!; await f.Runtime.StopSessionAsync();
        var memory = f.Model.Memory;
        var sourceMemory = memory.AddFromEditor("SYNTHETIC_PRIVATE_MEMORY");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        sourceMemory = memory.UpdateMetadata(sourceMemory.Id, sourceMemory.Metadata! with { ExpiresAtUtc = expiry }, memory.Capture().Stamp);
        var proof = AssistantMemoryDependencies.ToOutputDependencies(memory.CaptureForGeneration(true, DateTimeOffset.UtcNow));
        var first = await f.Ledger.EnqueueNotificationAsync(identity, "synthetic-first", "原消息甲：当时生成的说明。", null, outputDependencies: proof);
        string longBody = "原消息乙：" + string.Concat(Enumerable.Repeat("部分内容😀", 330)) + "PRIVATE_UNSELECTED_TAIL";
        var second = await f.Ledger.EnqueueNotificationAsync(identity, "synthetic-second", longBody, null, outputDependencies: OutputDependencies.Independent);
        await f.Ledger.ReceiveAsync(identity, "synthetic-return", "补报", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow,
            catchUp: new(CatchUpDisposition.AllowHistory, true, TimeZoneInfo.Local.Id));
        var prepared = await f.Ledger.PrepareCatchUpAsync(identity, "synthetic-return");
        check("actual_planner_creates_multi_source_partial_pages", prepared.Batch != null && prepared.Outbox.Length > 1
            && prepared.Batch.Members.Where(m => m.CatchUpOutboxId == prepared.Outbox[0].Id).Select(m => m.SourceEventId).Distinct().Count() == 2);
        var page = prepared.Outbox[0];
        await f.Ledger.BeginOutboxSendAsync(identity, page.Id);
        await f.Ledger.RecordOutboxOutcomeAsync(identity, page.Id, OutboxStage.Accepted, "synthetic channel acceptance only");
        var saved = await f.Ledger.ReadConversationAsync(identity);
        var accepted = saved.Events.Single(e => e.RelatedOutboxId == page.Id);
        var pageEvidence = f.Ledger.ReadCommittedEventEvidence(identity, accepted.Id)!;
        var batchEvidence = f.Ledger.ReadCommittedEventEvidence(identity, prepared.Batch!.DeliveryEventId)!;
        check("accepted_page_retains_only_its_own_exact_member_ranges", pageEvidence.Members.Length == 2
            && pageEvidence.Members.SelectMany(m => m.Spans).All(s => s.Text == saved.Outbox.Single(o => o.Id == s.OutboxId).Body.Substring(s.Start, s.Length))
            && pageEvidence.Members.SelectMany(m => m.Spans).Sum(s => s.Length) < batchEvidence.Members.SelectMany(m => m.Spans).Sum(s => s.Length)
            && !pageEvidence.Event.Body.Contains("PRIVATE_UNSELECTED_TAIL"));
        check("different_original_dependency_modes_are_preserved", pageEvidence.Event.OutputDependencies!.Mode == OutputDependencyMode.LegacyUnknown
            && pageEvidence.Members[0].Source.Event.OutputDependencies!.Mode == OutputDependencyMode.Bound
            && pageEvidence.Members[1].Source.Event.OutputDependencies!.Mode == OutputDependencyMode.Independent);
        var snapshot = await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None);
        var selected = snapshot.Sources.Single(s => s.Id == accepted.Id);
        check("host_exposes_composite_source_with_a_lineage_fingerprint", selected.IsCatchUp && selected.Members.Length == 2
            && selected.Reference != EpisodeEventReference.From(selected.Event));

        using var generator = DraftGenerator(directory, f.Settings, memory, accepted.Id, out var requests);
        var draft = await generator.GenerateEpisodeDraftAsync(snapshot, [selected.Reference], CancellationToken.None);
        using var request = JsonDocument.Parse(requests.Single());
        using var input = JsonDocument.Parse(request.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
        var sent = input.RootElement.GetProperty("events")[0];
        check("actual_draft_request_sends_selected_page_and_lineage_without_unselected_prose", sent.GetProperty("text").GetString() == accepted.Body
            && sent.GetProperty("sourceFragments").GetArrayLength() == 2 && !requests[0].Contains("PRIVATE_UNSELECTED_TAIL")
            && !requests[0].Contains("SYNTHETIC_PRIVATE_MEMORY") && draft.Sources.Single() == selected.Reference);
        var summary = memory.SaveEpisodeFromEditor(snapshot, draft.Text, draft.Sources);
        check("composite_summary_inherits_member_versions_and_deadline", summary.Metadata!.Dependencies.SequenceEqual([AssistantMemoryLayers.Reference(sourceMemory)])
            && summary.Metadata.ExpiresAtUtc == expiry && memory.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        check("future_member_expiry_excludes_summary_without_write", !memory.Capture(new(expiry.AddSeconds(1))).Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
        var window = new EpisodeMemoryEditorWindow(memory, await f.Model.EpisodeMemoryProvider!(summary.Id, CancellationToken.None));
        try
        {
            check("source_view_names_original_events_and_partial_ranges", window.Original.Text.Contains(first.Event.Id.ToString())
                && window.Original.Text.Contains(second.Event.Id.ToString()) && window.Original.Text.Contains("不代表完整原报告")
                && !window.Original.Text.Contains("PRIVATE_UNSELECTED_TAIL"));

            var provider = new AssistantModelContextProvider(f.Settings, memory); provider.BindLedger(f.Ledger);
            await f.Ledger.ReceiveAsync(identity, "synthetic-read", "核对摘要原文", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
            var lease = await f.Ledger.BeginGenerationAsync(identity, ["synthetic-read"]);
            var generation = await f.Ledger.BindGenerationContextAsync(lease, await provider.CaptureAsync(lease, CancellationToken.None));
            var tool = new ConversationHistoryTools(f.Ledger, provider, f.Settings);
            var context = new ToolExecutionContext(identity, "synthetic-read", lease.Revision, Guid.NewGuid(), null, null, GenerationContext: generation);
            var read = await tool.ExecuteAsync(context, new("source", "read_history_event", JsonSerializer.Serialize(new { eventId = accepted.Id })), CancellationToken.None);
            check("actual_history_tool_returns_reviewed_page_with_member_provenance", read.State == ToolExecutionState.ReadOnlySuccess
                && read.Facts.GetProperty("record").GetProperty("text").GetString() == accepted.Body
                && read.Facts.GetProperty("record").GetProperty("sourceFragments").GetArrayLength() == 2
                && !read.Facts.GetProperty("currentAuthorization").GetBoolean());

            f.Settings.ChatGpt.IncludeTaskContext = false;
            check("task_context_off_excludes_composite_generated_sources", !(await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None)).Sources.Any(s => s.Id == accepted.Id));
            f.Settings.ChatGpt.IncludeTaskContext = true;
            memory.AddFromEditor("独立新增条目");
            check("unrelated_addition_preserves_composite_summary", memory.Capture().Manifest!.IncludedReferences.Contains(AssistantMemoryLayers.Reference(summary)));
            memory.Delete(sourceMemory.Id);
            check("one_member_revocation_clears_summary_originals_and_open_draft", memory.Entries.Single(e => e.Id == summary.Id).Text == ""
                && window.Body.Text == "" && window.Original.Text == ""
                && !(await f.Model.EpisodeMemoryProvider!(null, CancellationToken.None)).Sources.Any(s => s.Id == accepted.Id));
            check("revocation_keeps_original_journal_and_transport_receipt", (await f.Ledger.ReadConversationAsync(identity)).Outbox.Single(o => o.Id == page.Id).Stage == OutboxStage.Accepted
                && f.Ledger.ReadCommittedEvent(identity, second.Event.Id)?.Body == longBody);
        }
        finally { window.Close(); }
    }

    private static ChatGptAssistant DraftGenerator(string directory, Settings settings, AssistantMemoryStore memory, Guid source, out List<string> requests)
    {
        settings.ChatGpt.Model = "synthetic-draft-model";
        string vault = Path.Combine(directory, "synthetic-draft.credentials");
        new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId = settings.ChatGpt.ActiveAccount, Subject = "synthetic-draft",
            AccessToken = "SYNTHETIC_ACCESS", Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } } }.Save(vault);
        var captured = new List<string>(); requests = captured;
        return new(settings, () => { }, new ChatGptAuth(vault), new Handler(async (request, ct) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Unexpected synthetic request.");
            captured.Add(await request.Content!.ReadAsStringAsync(ct));
            string answer = JsonSerializer.Serialize(new { summary = "两条历史消息的部分补报，尚未独立核验。", sourceEventIds = new[] { source } });
            string stream = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.done", text = answer })
                + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }), memory);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
