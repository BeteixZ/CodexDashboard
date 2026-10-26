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

// Actual isolated DPAPI files and durable invocation guards; no account, channel or model service.
internal static class MemoryRevocationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-revocation-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("explicit_new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)) throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory) || Directory.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        int failures = 0; var checks = new List<object>();
        async Task Check(string name, Func<Fixture, Task> action)
        {
            try
            {
                await using var f = await Fixture.CreateAsync(Path.Combine(directory, "fixture-" + (checks.Count + 1)));
                await action(f); checks.Add(new { name, passed = true });
            }
            catch (Exception error)
            { failures++; checks.Add(new { name, passed = false, detail = error is LedgerException le ? le.Code : error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "memory-revocation-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
        }
        await Check("delete_commits_bodyless_tombstone_and_reopens", async f =>
        {
            var entry = f.Memory.Add("合成需要删除的正文", f.Source()); f.Memory.Delete(entry.Id);
            var doc = f.Document(); string json = JsonSerializer.Serialize(doc, Settings.Json);
            Require(doc.FormatVersion == 3 && doc.Entries.Count == 0 && doc.Revocations.Single().EntryId == entry.Id
                && !json.Contains(entry.Text) && !f.Bytes.Contains(entry.Text));
            await f.Reopen(); Require(f.Memory.Entries.Count == 0 && f.Memory.CaptureHistoryFilter(Now).IsEntryVersionRevoked(entry.Id, 1));
        });
        await Check("revocation_has_exact_source_identity_revision_and_execution", f =>
        {
            var source = f.Source(); var entry = f.Memory.Add("精确来源", source); f.Memory.Delete(entry.Id);
            var r = f.Document().Revocations.Single();
            Require(r.Profile == f.Memory.Profile && r.SourceIdentity == Identity && r.SourceMessageId == source.SourceMessageId
                && r.SourceRevision == source.SourceRevision && r.OperationId == source.OperationId && r.InvocationId == source.InvocationId);
            return Task.CompletedTask;
        });
        await Check("new_message_same_text_and_other_identity_are_not_revoked", f =>
        {
            var source = f.Source(); var entry = f.Memory.Add("同一正文", source); f.Memory.Delete(entry.Id);
            var filter = f.Memory.CaptureHistoryFilter(Now);
            Require(filter.IsHistoricalSourceRevoked(Identity, source.SourceMessageId!, source.SourceRevision!.Value)
                && !filter.IsHistoricalSourceRevoked(Identity, "new-source", source.SourceRevision.Value + 1)
                && !filter.IsHistoricalSourceRevoked(Identity with { BindingId = "other-binding" }, source.SourceMessageId!, source.SourceRevision.Value)
                && !filter.IsHistoricalSourceRevoked(Identity, source.SourceMessageId!, source.SourceRevision.Value + 1));
            var fresh = f.Memory.Add("同一正文", f.Source("new-source", 2)); Require(fresh.Id != entry.Id);
            return Task.CompletedTask;
        });
        await Check("old_invocation_only_is_filtered", f =>
        {
            var source = f.Source(); var entry = f.Memory.Add("调用来源", source); f.Memory.Delete(entry.Id);
            var filter = f.Memory.CaptureHistoryFilter(Now);
            Require(filter.IsHistoricalInvocationRevoked(source.InvocationId!.Value)
                && !filter.IsHistoricalInvocationRevoked(Guid.NewGuid()) && !filter.IsHistoricalInvocationRevoked(Guid.Empty));
            return Task.CompletedTask;
        });
        await Check("correction_revokes_old_version_and_keeps_new_source", f =>
        {
            var old = f.Memory.Add("旧偏好", f.Source()); var next = f.Memory.Update(old.Id, "新偏好", f.Source("new-source", 2));
            var filter = f.Memory.CaptureHistoryFilter(Now);
            using var context = JsonDocument.Parse(f.Memory.Capture().Context);
            var injected = context.RootElement.GetProperty("entries").EnumerateArray().Single();
            Require(next.Metadata!.Version == 2 && filter.IsEntryVersionRevoked(old.Id, 1)
                && !filter.IsEntryVersionRevoked(old.Id, 2) && !filter.IsHistoricalSourceRevoked(Identity, "new-source", 2)
                && injected.GetProperty("id").GetString() == old.Id && injected.GetProperty("text").GetString() == "新偏好");
            return Task.CompletedTask;
        });
        await Check("automatic_replacement_revokes_previous_evidence", f =>
        {
            Require(f.Memory.LearnPreferences([new("report_layout", "以后回答先说结论")], f.Memory.Capture().Stamp, f.Source() with { SourceKind = "wechat_preference" }) == 1);
            var old = f.Memory.Entries.Single();
            Require(f.Memory.LearnPreferences([new("report_layout", "以后回答先列依据")], f.Memory.Capture().Stamp, f.Source("next", 2) with { SourceKind = "wechat_preference" }) == 1);
            var current = f.Memory.Entries.Single(); var r = f.Document().Revocations.Single();
            Require(current.Id == old.Id && current.Metadata!.Version == 2 && r.Reason == "preference_replaced"
                && r.SourceMessageId == "source" && current.Provenance!.SourceMessageId == "next"); return Task.CompletedTask;
        });
        await Check("delete_clears_transitive_summary_body_in_same_document", f =>
        {
            f.Seed(Entry("11111111", "原始正文", source: f.Source()), Summary("22222222", "一级派生正文", "11111111"),
                Summary("33333333", "二级派生正文", "22222222"));
            f.Memory.Delete("11111111"); var doc = f.Document();
            Require(doc.Entries.Count == 2 && doc.Entries.All(e => e.Text == "" && e.Metadata!.InvalidatedAtUtc != null)
                && doc.Revocations.Count == 3 && doc.Revocations.All(r => r.Version == 1)
                && f.Memory.Capture().Context == ""); return Task.CompletedTask;
        });
        await Check("expiry_boundary_excludes_exact_source_without_rewrite", f =>
        {
            f.Seed(Entry("11111111", "过期正文", Initial() with { ExpiresAtUtc = Now.AddMinutes(1) }, f.Source()));
            string bytes = f.Bytes; var before = f.Memory.CaptureHistoryFilter(Now); var after = f.Memory.CaptureHistoryFilter(Now.AddMinutes(1));
            Require(before.Revocations.Length == 0 && after.IsHistoricalSourceRevoked(Identity, "source", 1)
                && !f.Memory.IsHistoryFilterCurrent(before, Now.AddMinutes(1)) && f.Bytes == bytes); return Task.CompletedTask;
        });
        await Check("source_expiry_also_excludes_transitive_summary_history", f =>
        {
            f.Seed(Entry("11111111", "会过期的源", Initial() with { ExpiresAtUtc = Now.AddMinutes(1) }, f.Source()),
                Summary("22222222", "依赖摘要", "11111111", f.Source("summary", 2)),
                Summary("33333333", "二级摘要", "22222222", f.Source("summary-two", 3)));
            var filter = f.Memory.CaptureHistoryFilter(Now.AddMinutes(1));
            Require(filter.Revocations.Length == 3 && filter.IsHistoricalSourceRevoked(Identity, "summary", 2)
                && filter.IsHistoricalSourceRevoked(Identity, "summary-two", 3)
                && f.Memory.Capture(new(Now.AddMinutes(1))).Context == ""); return Task.CompletedTask;
        });
        await Check("legacy_unknown_source_is_an_explicit_cutoff_not_guessed_source", f =>
        {
            var entry = f.Memory.Add("没有历史来源"); f.Memory.Delete(entry.Id);
            var filter = f.Memory.CaptureHistoryFilter(Now);
            Require(filter.UnknownSourceHistoryCutoffUtc != null && filter.Revocations.Single().SourceIdentity == null
                && !filter.IsHistoricalSourceRevoked(Identity, "source", 1)); return Task.CompletedTask;
        });
        await Check("known_source_does_not_add_global_history_cutoff", f =>
        {
            var entry = f.Memory.Add("明确来源", f.Source()); f.Memory.Delete(entry.Id);
            Require(f.Memory.CaptureHistoryFilter(Now).UnknownSourceHistoryCutoffUtc == null); return Task.CompletedTask;
        });
        foreach (int version in new[] { 1, 2 })
            await Check("legacy_v" + version + "_reads_without_rewrite", f =>
            {
                var doc = new AssistantMemoryDocument { FormatVersion = version, Profile = f.Memory.Profile, Revision = 2,
                    Entries = [Entry("11111111", "旧格式内容", version == 1 ? null : Initial())] };
                f.SeedDocument(doc); string bytes = f.Bytes;
                Require(f.Memory.Entries.Single().Text == "旧格式内容" && f.Memory.CaptureHistoryFilter(Now).Revocations.Length == 0
                    && f.Bytes == bytes); return Task.CompletedTask;
            });
        foreach (string corrupt in new[] { "format", "duplicate", "profile", "partial_source", "version", "v2_tombstone", "negative_policy", "null_records" })
            await Check("corrupt_" + corrupt + "_fails_closed_and_preserves_file", f =>
            {
                var entry = f.Memory.Add("会被删除", f.Source()); f.Memory.Delete(entry.Id); var doc = f.Document(); var r = doc.Revocations.Single();
                switch (corrupt)
                {
                    case "format": doc.FormatVersion = 99; break;
                    case "duplicate": doc.Revocations.Add(r); break;
                    case "profile": doc.Revocations[0] = r with { Profile = "wrong" }; break;
                    case "partial_source": doc.Revocations[0] = r with { SourceIdentity = null }; break;
                    case "version": doc.Revocations[0] = r with { Version = 0 }; break;
                    case "v2_tombstone": doc.FormatVersion = 2; break;
                    case "negative_policy": doc.PersonalizationRevision = -1; break;
                    default: doc.Revocations = null!; break;
                }
                f.SeedDocument(doc); string bytes = f.Bytes; Reject(() => f.Memory.Capture()); Require(f.Bytes == bytes); return Task.CompletedTask;
            });
        await Check("disabled_capture_ignores_unrelated_memory_and_rules_edits", f =>
        {
            var before = f.Memory.CaptureForGeneration(false, Now); f.Memory.Add("新增条目"); f.Memory.SaveRules("新规则");
            var after = f.Memory.CaptureForGeneration(false, Now);
            Require(before.Content == null && after.Content == null && f.Memory.IsGenerationPersonalizationCurrent(before, false, Now)
                && OutputDependencies.Equivalent(Deps(before), Deps(after)) && Deps(before).Contexts.Length == 1); return Task.CompletedTask;
        });
        await Check("policy_aba_changes_monotonic_revision_across_restart", async f =>
        {
            var before = f.Memory.CaptureForGeneration(true, Now); f.Memory.AdvancePersonalizationRevision(); f.Memory.AdvancePersonalizationRevision();
            await f.Reopen(); var after = f.Memory.CaptureForGeneration(true, Now);
            Require(after.PersonalizationRevision == before.PersonalizationRevision + 2 && !OutputDependencies.Equivalent(Deps(before), Deps(after))
                && !f.Memory.IsGenerationPersonalizationCurrent(before, true, Now) && f.Document().FormatVersion == 3);
        });
        await Check("rules_aba_changes_bound_fingerprint_across_restart", async f =>
        {
            string rules = f.Memory.ReadRules(); var before = f.Memory.CaptureForGeneration(true, Now);
            f.Memory.SaveRules("中间规则"); f.Memory.SaveRules(rules); await f.Reopen();
            var after = f.Memory.CaptureForGeneration(true, Now);
            Require(before.Content!.Rules == after.Content!.Rules && after.Content.Manifest!.RulesRevision == 2
                && before.Content.Stamp != after.Content.Stamp && !OutputDependencies.Equivalent(Deps(before), Deps(after)));
        });
        await Check("same_rules_is_noop_and_keeps_stamp", f =>
        {
            var before = f.Memory.CaptureForGeneration(true, Now); string bytes = f.Bytes; f.Memory.SaveRules(f.Memory.ReadRules());
            Require(f.Bytes == bytes && OutputDependencies.Equivalent(Deps(before), Deps(f.Memory.CaptureForGeneration(true, Now)))); return Task.CompletedTask;
        });
        await Check("rules_write_failure_keeps_persisted_invalidation_fence", f =>
        {
            var before = f.Memory.CaptureForGeneration(true, Now); string rules = f.Memory.ReadRules();
            using (var held = new FileStream(f.Memory.RulesPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => f.Memory.SaveRules("无法替换时仍保持旧回答失效"));
            Require(f.Memory.ReadRules() == rules && f.Document().RulesRevision == 1
                && !f.Memory.IsGenerationPersonalizationCurrent(before, true, Now)); return Task.CompletedTask;
        });
        await Check("capture_selection_stable_before_expiry_then_changes", f =>
        {
            f.Seed(Entry("11111111", "短期内容", Initial() with { ExpiresAtUtc = Now.AddMinutes(1) }));
            var before = f.Memory.CaptureForGeneration(true, Now); var same = f.Memory.CaptureForGeneration(true, Now.AddSeconds(10));
            var after = f.Memory.CaptureForGeneration(true, Now.AddMinutes(1));
            Require(OutputDependencies.Equivalent(Deps(before), Deps(same)) && !OutputDependencies.Equivalent(Deps(before), Deps(after))
                && Deps(before).Contexts.Single(c => c.Provider == AssistantMemoryDependencies.MemoryProvider).ValidUntilUtc == Now.AddMinutes(1));
            return Task.CompletedTask;
        });
        await Check("history_filter_rejects_same_profile_changed_revision", f =>
        {
            var before = f.Memory.CaptureHistoryFilter(Now); Require(f.Memory.IsHistoryFilterCurrent(before, Now));
            f.Memory.Add("新增内容"); Require(!f.Memory.IsHistoryFilterCurrent(before, Now)); return Task.CompletedTask;
        });
        await Check("profile_switch_does_not_expose_other_profile_tombstones", f =>
        {
            var entry = f.Memory.Add("A内容", f.Source()); f.Memory.Delete(entry.Id); var a = f.Memory.CaptureHistoryFilter(Now);
            f.ProfileKey = "synthetic-profile-B"; f.Memory.Initialize();
            Require(f.Memory.CaptureHistoryFilter(Now).Revocations.Length == 0 && !f.Memory.IsHistoryFilterCurrent(a, Now));
            f.ProfileKey = "synthetic-profile-A"; Require(f.Memory.CaptureHistoryFilter(Now).IsEntryVersionRevoked(entry.Id, 1)); return Task.CompletedTask;
        });
        await Check("stale_bound_store_context_rejects_before_mutation", f =>
        {
            var before = f.Memory.CaptureForGeneration(true, DateTimeOffset.UtcNow); var command = f.Memory.CaptureManual("记住新内容");
            f.Memory.SaveRules("变化的规则"); string bytes = f.Bytes;
            Reject(() => f.Memory.ApplyManualWithContext(command.Resolution, command.Stamp, f.Source(), Deps(before)));
            Require(f.Bytes == bytes && f.Memory.Entries.Count == 0); return Task.CompletedTask;
        });
        await Check("manual_bound_write_returns_exact_transition_after_durable_write", async f =>
        {
            var p = await f.Prepare("记住蓝色偏好", Remember("蓝色偏好")); var result = await f.Manual.ExecuteAsync(p.Context, p.Call, default);
            var t = result.ContextTransition;
            Require(result.State == ToolExecutionState.Completed && t != null && t.GenerationId == p.Lease.Id && t.ExpectedEpoch == 0
                && t.InvocationId == p.Context.InvocationId && t.OperationId == p.Context.OperationId
                && OutputDependencies.Equivalent(t.Before, p.Context.GenerationContext!.Dependencies)
                && OutputDependencies.Equivalent(t.After, Deps(f.Memory.CaptureForGeneration(true, DateTimeOffset.UtcNow)))
                && f.Document().Entries.Single().Text == "蓝色偏好" && !result.Facts.TryGetProperty("contextTransition", out _));
        });
        await Check("transition_requires_completed_receipt_then_advances_once", async f =>
        {
            var p = await f.Prepare("记住持久偏好", Remember("持久偏好")); var result = await f.Manual.ExecuteAsync(p.Context, p.Call, default);
            Require(result.ContextTransition != null);
            await RejectAsync(() => f.Ledger.AdvanceGenerationContextAsync(p.Lease, result.ContextTransition!));
            await f.Complete(p, result); var next = await f.Ledger.AdvanceGenerationContextAsync(p.Lease, result.ContextTransition!);
            Require(next.Epoch == 1 && OutputDependencies.Equivalent(next.Dependencies, result.ContextTransition!.After));
            await RejectAsync(() => f.Ledger.AdvanceGenerationContextAsync(p.Lease, result.ContextTransition!));
            var snapshot = await f.Ledger.ReadConversationAsync(Identity);
            Require(snapshot.Generations.Single(g => g.Id == p.Lease.Id).ContextTransitions!.Value.Length == 1);
        });
        foreach (string forgery in new[] { "generation", "epoch", "dependencies", "missing" })
            await Check("manual_forged_" + forgery + "_context_cannot_write", async f =>
            {
                var p = await f.Prepare("记住禁止伪上下文", Remember("禁止伪上下文")); var gc = p.Context.GenerationContext!;
                GenerationContext? wrong = forgery switch
                {
                    "generation" => gc with { GenerationId = Guid.NewGuid() }, "epoch" => gc with { Epoch = gc.Epoch + 1 },
                    "dependencies" => gc with { Dependencies = OutputDependencies.Independent }, _ => null
                };
                string bytes = f.Bytes; var result = await f.Manual.ExecuteAsync(p.Context with { GenerationContext = wrong }, p.Call, default);
                Require(result.State == ToolExecutionState.Rejected && result.ContextTransition == null && f.Bytes == bytes);
            });
        await Check("same_remember_is_completed_without_fake_transition", async f =>
        {
            f.Memory.Add("相同偏好"); var p = await f.Prepare("记住相同偏好", Remember("相同偏好")); string bytes = f.Bytes;
            var result = await f.Manual.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed && result.ContextTransition == null && f.Bytes == bytes);
        });
        await Check("actual_automatic_write_carries_transition_and_keeps_first_stamp", async f =>
        {
            const string evidence = "以后回答先说结论";
            var p = await f.Prepare(evidence, Learn(evidence)); var result = await f.Automatic.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed && result.ContextTransition != null && f.Memory.Entries.Single().Text == evidence);
            await f.Complete(p, result); var next = await f.Ledger.AdvanceGenerationContextAsync(p.Lease, result.ContextTransition!);
            var repeat = Learn(evidence); var descriptor = AutomaticPreferenceTools.LearnDescriptor;
            Require(descriptor.Parameters.TryValidate(repeat.ArgumentsJson, out var canonical, out _)); repeat = repeat with { ArgumentsJson = canonical };
            await f.Automatic.WrapModel(new Script(repeat)).GenerateAsync(new(p.Source, p.Source.Revision, [], [], [], GenerationContext: next), default);
            var decision = await f.Automatic.AuthorizeAsync(new(p.Source, repeat, descriptor), default);
            Require(!decision.Allowed);
        });
        await Check("deleted_source_cannot_be_relearned_in_same_current_message", async f =>
        {
            const string evidence = "以后回答先说结论";
            var p = await f.Prepare(evidence, Learn(evidence)); var result = await f.Automatic.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed); f.Memory.Delete(f.Memory.Entries.Single().Id);
            var filter = f.Memory.CaptureHistoryFilter(DateTimeOffset.UtcNow);
            Require(filter.IsHistoricalSourceRevoked(Identity, p.Source.SourceMessageId, p.Source.Revision));
            var decision = await f.Automatic.AuthorizeAsync(new(p.Source, p.Call, AutomaticPreferenceTools.LearnDescriptor), default);
            Require(!decision.Allowed && f.Memory.Entries.Count == 0);
        });
        await Check("postwrite_event_failure_never_returns_fabricated_success", async f =>
        {
            var p = await f.Prepare("记住真实已写入", Remember("真实已写入")); f.ThrowAfterSave = true;
            await RejectAsync(() => f.Manual.ExecuteAsync(p.Context, p.Call, default));
            Require(f.Document().Entries.Single().Text == "真实已写入");
            var snapshot = await f.Ledger.ReadConversationAsync(Identity);
            Require(snapshot.Operations.Single().Stage == OperationStage.Dispatching
                && snapshot.ToolInvocations.Single().Stage == ToolInvocationStage.Executing);
        });
        await Check("new_current_message_may_explicitly_remember_same_deleted_text", async f =>
        {
            var old = f.Memory.Add("可重新明确保存", f.Source("old-source", 1)); f.Memory.Delete(old.Id);
            var p = await f.Prepare("记住可重新明确保存", Remember("可重新明确保存"));
            var result = await f.Manual.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed && f.Memory.Entries.Single().Id != old.Id
                && !f.Memory.CaptureHistoryFilter(Now).IsHistoricalSourceRevoked(Identity, p.Source.SourceMessageId, p.Source.Revision));
        });
        if (failures != 0) throw new InvalidOperationException("memory_revocation_checks_failed");
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ConversationIdentity Identity = new("synthetic-revocation-user", "synthetic-revocation-binding", "main");
    private static AssistantMemoryMetadata Initial() => AssistantMemoryLayers.Initial(AssistantMemoryKind.General);
    private static AssistantMemoryEntry Entry(string id, string text, AssistantMemoryMetadata? metadata = null, AssistantMemoryProvenance? source = null)
        => new(id, text, Now, Now, Provenance: source, Metadata: metadata);
    private static AssistantMemoryEntry Summary(string id, string text, string dependency, AssistantMemoryProvenance? source = null)
        => Entry(id, text, new(AssistantMemoryKind.EpisodeSummary, AssistantMemoryLayers.PersonalScope, 1,
            DerivedFrom: [new(dependency, 1)]), source);
    private static OutputDependencies Deps(AssistantGenerationPersonalization capture) => AssistantMemoryDependencies.ToOutputDependencies(capture);
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("assertion_failed"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is ChatGptException or InvalidOperationException or IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is LedgerException or InvalidOperationException or IOException) { return; }
        throw new InvalidOperationException("expected_rejection");
    }
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    private static ModelToolCall Remember(string text) => Call("remember", new { text, scope = "personal" });
    private static ModelToolCall Learn(string evidence) => Call("learn_preference", new { preferences = new[] { new { key = "report_order", evidence } } });
    private sealed record Prepared(InboxMessage Source, GenerationLease Lease, ModelToolCall Call, ToolExecutionContext Context);
    private sealed class Script(ModelToolCall call) : IToolModel
    {
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
            => Task.FromResult(ToolModelResponse.Call(call));
    }
    private sealed class Fixture : IAsyncDisposable, IOutputDependencyValidator
    {
        internal readonly Settings Settings = new() { ChatGpt = new() { Enabled = true, PersonalizationEnabled = true,
            AutoMemoryEnabled = true, ActiveAccount = "synthetic-account", Model = "synthetic-model" } };
        internal AssistantMemoryStore Memory = null!;
        internal ManualMemoryTools Manual = null!;
        internal AutomaticPreferenceTools Automatic = null!;
        internal DurableConversationLedger Ledger = null!;
        internal string ProfileKey = "synthetic-profile-A";
        internal bool ThrowAfterSave;
        private string root = "", journal = "";
        private int sourceNumber;
        private long epoch;
        internal string Bytes => File.ReadAllText(Memory.DataPath!);
        internal AssistantMemoryDocument Document() => JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(Bytes), CodexDashboard.AssistantSettings.Json)!;
        internal void SeedDocument(AssistantMemoryDocument document) => File.WriteAllText(Memory.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(document, CodexDashboard.AssistantSettings.Json)));
        internal void Seed(params AssistantMemoryEntry[] entries) => SeedDocument(new() { FormatVersion = 2, Profile = Memory.Profile, Revision = 1, Entries = entries.ToList() });
        internal AssistantMemoryProvenance Source(string source = "source", long revision = 1) => new("wechat_manual", Memory.Profile,
            Now, Identity, source, revision, Guid.NewGuid(), Guid.NewGuid(), Now, Now);
        internal static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory); var f = new Fixture { root = Path.Combine(directory, "memory"), journal = Path.Combine(directory, "journal") };
            await f.Reopen(); return f;
        }
        internal async Task Reopen()
        {
            if (Ledger != null) await Ledger.DisposeAsync();
            Memory = new(root, () => ProfileKey); Memory.Initialize();
            if (!File.Exists(Memory.DataPath!)) SeedDocument(new() { Profile = Memory.Profile });
            Memory.Changed += () => { epoch++; if (ThrowAfterSave) throw new IOException("synthetic_after_write"); };
            Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(journal), new WindowsStateProtector(), dependencyValidator: this);
            Manual = new(Settings, Memory, Ledger, id => id == Identity);
            Automatic = new(Settings, Memory, Ledger, id => id == Identity, () => epoch, enabled => Settings.ChatGpt.AutoMemoryEnabled = enabled);
        }
        public OutputDependencyValidation Validate(ConversationIdentity identity, OutputDependencies dependencies)
        {
            if (identity != Identity) return new(OutputDependencyStatus.Invalid, "identity_changed");
            var actual = Deps(Memory.CaptureForGeneration(Settings.ChatGpt.PersonalizationEnabled, DateTimeOffset.UtcNow));
            return OutputDependencies.Equivalent(actual, dependencies) ? new(OutputDependencyStatus.Valid) : new(OutputDependencyStatus.Invalid, "memory_changed");
        }
        internal async Task<Prepared> Prepare(string text, ModelToolCall proposed)
        {
            var source = (await Ledger.ReceiveAsync(Identity, "current-" + ++sourceNumber, text, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow)).Message;
            var lease = await Ledger.BeginGenerationAsync(Identity, [source.SourceMessageId]);
            var gc = await Ledger.BindGenerationContextAsync(lease, Deps(Memory.CaptureForGeneration(true, DateTimeOffset.UtcNow)));
            bool automatic = proposed.Name == "learn_preference";
            ToolDescriptor descriptor;
            if (automatic) descriptor = AutomaticPreferenceTools.LearnDescriptor;
            else if (!AssistantToolExecutor.Catalog().TryGet(proposed.Name, out descriptor!)) throw new InvalidOperationException("test_tool_missing");
            Require(descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out string canonical, out _));
            var call = proposed with { ArgumentsJson = canonical };
            if (automatic) await Automatic.WrapModel(new Script(call)).GenerateAsync(new(source, source.Revision, [], [], [], GenerationContext: gc), default);
            var decision = automatic ? await Automatic.AuthorizeAsync(new(source, call, descriptor), default)
                : await Manual.AuthorizeAsync(new(source, call, descriptor), default);
            Require(decision.Allowed);
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, source.SourceMessageId, call.Name, decision.TargetKey!, canonical, decision.SourceExcerpt!);
            string key = "synthetic-revocation-" + Guid.NewGuid().ToString("N"); var operation = await Ledger.QueueOperationAsync(Identity, grant.Id, key);
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, source.SourceMessageId, call.Id, call.Name, canonical, true);
            await Ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
            return new(source, lease, call, new(Identity, source.SourceMessageId, source.Revision, invocation.Id, operation.Id, key, GenerationContext: gc));
        }
        internal Task Complete(Prepared p, ToolExecutionResult result) => Ledger.FinishToolInvocationAsync(Identity, p.Context.InvocationId,
            ToolInvocationStage.Completed, result.Facts.GetRawText(), OperationStage.Completed);
        public async ValueTask DisposeAsync() => await Ledger.DisposeAsync();
    }
}
