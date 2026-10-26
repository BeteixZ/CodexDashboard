using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

// Real DPAPI and isolated files, no settings/credentials/channel/model loading.
internal static class MemoryLayerTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-layer-test");
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
        int failures = 0, count = 0; var checks = new List<object>();
        async Task Check(string name, Action<Fixture> action)
        {
            try { action(new Fixture(Path.Combine(directory, "fixture-" + ++count))); checks.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error.GetType().Name + ":" + error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "memory-layer-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
        }
        await Check("legacy_v1_read_does_not_reclassify_or_rewrite", f =>
        {
            f.Seed(Entry("11111111", "旧版个人内容", metadata: null)); f.RewriteVersion(1);
            string bytes = f.Bytes; var capture = f.Store.Capture(new(Now)); var entry = f.Store.Entries.Single();
            Require(entry.Metadata == null && capture.Context.Contains("11111111") && f.Bytes == bytes);
            Require(f.Store.SourceDetails(entry.Id) is { Known: false } details && details.Detail.Contains("历史来源未记录")
                && details.Detail.Contains("历史记录未保存分层信息") && details.ScopeLabel.Contains("当前账户与微信绑定"));
        });
        await Check("first_write_promotes_v1_but_preserves_legacy_entry", f =>
        {
            f.Seed(Entry("11111111", "旧版个人内容", metadata: null)); f.RewriteVersion(1);
            f.Store.Add("新个人内容"); var document = f.Document();
            Require(document.FormatVersion == 2 && document.Entries.Single(e => e.Id == "11111111").Metadata == null
                && document.Entries.Single(e => e.Id != "11111111").Metadata?.Version == 1);
        });
        await Check("new_metadata_is_dpapi_encrypted_and_survives_reopen_equality", f =>
        {
            var entry = f.Store.AddFromEditor("合成的非秘密层级内容");
            Require(f.Document().FormatVersion == 2 && !f.Bytes.Contains(entry.Text)
                && f.Reopen().Entries.Single() == entry && entry.Provenance?.SourceKind == "desktop_editor");
        });
        await Check("volatile_clone_keeps_v2_and_versions", _ =>
        {
            var store = new AssistantMemoryStore(null, () => "synthetic-volatile");
            var entry = store.Add("第一版本"); var changed = store.Update(entry.Id, "第二版本");
            Require(store.Capture().Manifest!.IncludedReferences.Single().Version == 2
                && changed.Metadata!.Replaces == new AssistantMemoryReference(entry.Id, 1) && store.Entries.Single() == changed);
        });
        await Check("same_selection_stamp_stable_across_time_before_expiry", f =>
        {
            f.Seed(Entry("11111111", "尚未到期", Initial() with { ExpiresAtUtc = Now.AddHours(1) }));
            var one = f.Store.Capture(new(Now)); var two = f.Store.Capture(new(Now.AddMinutes(5)));
            Require(one.Stamp == two.Stamp && one.Manifest!.ValidUntilUtc == Now.AddHours(1)
                && f.Store.IsCaptureCurrent(one.Manifest, Now.AddMinutes(5)));
        });
        await Check("expiry_at_exact_instant_changes_selection_stamp_and_manifest", f =>
        {
            f.Seed(Entry("11111111", "即将过期", Initial() with { ExpiresAtUtc = Now.AddMinutes(1) }));
            var before = f.Store.Capture(new(Now)); var after = f.Store.Capture(new(Now.AddMinutes(1)));
            Require(after.Context == "" && before.Stamp != after.Stamp && after.Manifest!.IncludedReferences.Length == 0
                && !f.Store.IsCaptureCurrent(before.Manifest!, Now.AddMinutes(1)));
        });
        await Check("pending_excluded_until_explicit_metadata_update", f =>
        {
            f.Seed(Entry("11111111", "待确认内容", Initial() with { Confirmation = AssistantMemoryConfirmation.Pending }));
            var prior = f.Store.Capture(); var entry = f.Store.Entries.Single();
            Require(prior.Context == "");
            f.Store.UpdateMetadata(entry.Id, entry.Metadata! with { Confirmation = AssistantMemoryConfirmation.Explicit }, prior.Stamp);
            Require(f.Store.Capture().Context.Contains(entry.Id) && f.Store.Entries.Single().Metadata!.Version == 2
                && !f.Store.IsCaptureCurrent(prior.Manifest!, DateTimeOffset.UtcNow));
        });
        await Check("project_and_new_persona_types_not_injected_or_globalized", f =>
        {
            var project = new AssistantMemoryMetadata(AssistantMemoryKind.ProjectKnowledge,
                new(AssistantMemoryScopeKind.Project, "synthetic-project-reference"), 1, DerivedFrom: []);
            f.Seed(Entry("11111111", "项目秘密边界测试", project), Entry("22222222", "新类型人格尚未开放",
                Initial() with { Kind = AssistantMemoryKind.Persona }), Entry("33333333", "普通内容", Initial()));
            var captured = f.Store.Capture();
            Require(captured.Manifest!.IncludedReferences.SequenceEqual([new AssistantMemoryReference("33333333", 1)])
                && !captured.Context.Contains("11111111") && !captured.Context.Contains("22222222")
                && f.Store.SourceDetails("11111111")!.ScopeLabel.Contains("当前未开放项目检索"));
            Reject(() => f.Store.Update("11111111", "不能转为全局"));
        });
        foreach (string forgery in new[] { "project", "persona", "summary", "version", "replacement", "dependency", "invalidated" })
            await Check("metadata_edit_rejects_invented_" + forgery, f =>
            {
                var entry = f.Store.Add("普通内容"); var m = entry.Metadata!;
                m = forgery switch
                {
                    "project" => m with { Scope = new(AssistantMemoryScopeKind.Project, "not-trusted") },
                    "persona" => m with { Kind = AssistantMemoryKind.Persona },
                    "summary" => m with { Kind = AssistantMemoryKind.EpisodeSummary },
                    "version" => m with { Version = 99 },
                    "replacement" => m with { Replaces = new("FFFFFFFF", 1) },
                    "dependency" => m with { DerivedFrom = [new("FFFFFFFF", 1)] },
                    _ => m with { InvalidatedAtUtc = Now }
                };
                string bytes = f.Bytes; Reject(() => f.Store.UpdateMetadata(entry.Id, m, f.Store.Capture().Stamp)); Require(f.Bytes == bytes);
            });
        await Check("stale_metadata_editor_stamp_cannot_change_corrected_entry", f =>
        {
            var entry = f.Store.Add("旧内容"); string stamp = f.Store.Capture().Stamp;
            f.Store.Update(entry.Id, "用户纠正"); string bytes = f.Bytes;
            Reject(() => f.Store.UpdateMetadata(entry.Id, entry.Metadata! with { ExpiresAtUtc = Now.AddDays(1) }, stamp)); Require(f.Bytes == bytes);
        });
        await Check("derived_capture_requires_exact_dependency_and_explicit_confirmation", f =>
        {
            var source = Entry("11111111", "明确事实", Initial());
            f.Seed(source, Summary("22222222", "已确认摘要", source), Summary("33333333", "待确认摘要", source) with
            { Metadata = Summary("33333333", "待确认摘要", source).Metadata! with { Confirmation = AssistantMemoryConfirmation.Pending } });
            var manifest = f.Store.Capture().Manifest!;
            Require(manifest.IncludedReferences.Select(r => r.Id).SequenceEqual(["11111111", "22222222"])
                && f.Reopen().Entries.SequenceEqual(f.Store.Entries));
        });
        await Check("dependent_expiry_follows_source_without_rewriting_document", f =>
        {
            var source = Entry("11111111", "到期事实", Initial() with { ExpiresAtUtc = Now.AddMinutes(1) });
            f.Seed(source, Summary("22222222", "不得越过来源有效期", source)); string bytes = f.Bytes;
            var before = f.Store.Capture(new(Now)); var after = f.Store.Capture(new(Now.AddMinutes(1)));
            Require(before.Manifest!.IncludedReferences.Length == 2 && before.Manifest.ValidUntilUtc == source.Metadata!.ExpiresAtUtc
                && after.Context == "" && f.Bytes == bytes);
        });
        await Check("correction_invalidates_transitive_summaries_in_same_revision", f =>
        {
            var source = Entry("11111111", "旧事实", Initial()); var first = Summary("22222222", "旧派生一级正文", source);
            f.Seed(source, first, Summary("33333333", "旧派生二级正文", first)); var before = f.Document(); var capture = f.Store.Capture();
            f.Store.Update(source.Id, "纠正事实"); var after = f.Document();
            Require(after.Revision == before.Revision + 1 && after.Entries.Single(e => e.Id == source.Id).Metadata!.Version == 2
                && after.Entries.Where(e => e.Id != source.Id).All(e => e.Text == "" && e.Metadata!.InvalidatedAtUtc != null && e.Metadata.Version == 2)
                && !LocalSecret.Unprotect(f.Bytes).Contains("旧派生") && f.Reopen().Capture().Manifest!.IncludedReferences.Length == 1
                && !f.Store.IsCaptureCurrent(capture.Manifest!, DateTimeOffset.UtcNow));
        });
        await Check("delete_erases_derived_prose_and_keeps_only_invalid_audit_references", f =>
        {
            var source = Entry("11111111", "删除原始事实", Initial()); var first = Summary("22222222", "应清除派生正文", source);
            f.Seed(source, first, Summary("33333333", "应清除后续正文", first)); long revision = f.Document().Revision;
            f.Store.Delete(source.Id); var document = f.Document();
            Require(document.Revision == revision + 1 && document.Entries.Count == 2
                && document.Entries.All(e => e.Text == "" && e.Metadata!.InvalidatedAtUtc != null)
                && f.Reopen().Capture().Context == "" && !LocalSecret.Unprotect(f.Bytes).Contains("应清除")
                && f.Store.SourceDetails(first.Id)!.Detail.Contains("摘要内容已清除"));
        });
        await Check("metadata_expiry_change_also_invalidates_existing_summary", f =>
        {
            var source = Entry("11111111", "明确事实", Initial()); f.Seed(source, Summary("22222222", "依赖旧条件", source));
            f.Store.UpdateMetadata(source.Id, source.Metadata! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) }, f.Store.Capture().Stamp);
            Require(f.Document().Entries.Single(e => e.Id == "22222222").Text == ""
                && f.Store.Capture().Manifest!.IncludedReferences.Length == 1);
        });
        await Check("automatic_replacement_versions_and_invalidates_summary", f =>
        {
            var source = Entry("11111111", "我更喜欢分行", Initial() with { Kind = AssistantMemoryKind.Preference }) with
            { Origin = "automatic", PreferenceKey = "report_layout" };
            f.Seed(source, Summary("22222222", "旧偏好派生", source));
            Require(f.Store.LearnPreferences([new("report_layout", "我更喜欢段落回答")], f.Store.Capture().Stamp) == 1);
            var updated = f.Store.Entries.Single(e => e.Id == source.Id);
            Require(updated.Metadata!.Version == 2 && updated.Metadata.Replaces == AssistantMemoryLayers.Reference(source)
                && f.Store.Entries.Single(e => e.Id == "22222222").Text == "" && f.Reopen().Entries.Contains(updated));
        });
        await Check("manual_correction_prevents_later_automatic_overwrite", f =>
        {
            var source = Entry("11111111", "我更喜欢分行", Initial() with { Kind = AssistantMemoryKind.Preference }) with
            { Origin = "automatic", PreferenceKey = "report_layout" }; f.Seed(source);
            f.Store.Update(source.Id, "由我手动管理");
            f.Store.LearnPreferences([new("report_layout", "我更喜欢段落回答")], f.Store.Capture().Stamp);
            Require(f.Store.Entries.Single(e => e.Id == source.Id) is { Text: "由我手动管理", Origin: "explicit" }
                && f.Store.Entries.Single(e => e.Id == source.Id).Metadata!.Kind == AssistantMemoryKind.General);
        });
        await Check("delete_fences_late_extraction_and_old_manifest", f =>
        {
            var entry = f.Store.Add("明确记录"); var capture = f.Store.Capture(); f.Store.Delete(entry.Id);
            Require(f.Store.LearnPreferences([new("report_layout", "我更喜欢段落回答")], capture.Stamp) == 0
                && !f.Store.IsCaptureCurrent(capture.Manifest!, DateTimeOffset.UtcNow) && f.Reopen().Entries.Count == 0);
        });
        await Check("rules_change_invalidates_manifest_without_entry_mutation", f =>
        {
            f.Store.Add("明确事实"); var before = f.Store.Capture(); long revision = f.Document().Revision;
            f.Store.SaveRules("新的明确表达规则"); Require(!f.Store.IsCaptureCurrent(before.Manifest!, DateTimeOffset.UtcNow)
                && f.Document().Revision == revision + 1 && f.Document().RulesRevision == 1
                && before.Stamp != f.Store.Capture().Stamp);
        });
        await Check("manifest_identity_isolated_after_restart_and_switch", f =>
        {
            f.Store.Add("A 的个人内容"); var capture = f.Store.Capture(); f.Identity = "synthetic-layer-owner-B";
            f.Store.Add("B 的个人内容"); Require(!f.Store.IsCaptureCurrent(capture.Manifest!, DateTimeOffset.UtcNow)
                && !f.Reopen().Capture().Context.Contains(capture.Manifest!.IncludedReferences[0].Id));
            f.Identity = "synthetic-layer-owner-A";
            Require(f.Reopen().IsCaptureCurrent(capture.Manifest!, DateTimeOffset.UtcNow));
        });
        await Check("manifest_forged_selection_expiry_and_revision_rejected", f =>
        {
            f.Store.Add("个人事实"); var manifest = f.Store.Capture().Manifest!;
            Require(!f.Store.IsCaptureCurrent(manifest with { IncludedReferences = [] }, DateTimeOffset.UtcNow)
                && !f.Store.IsCaptureCurrent(manifest with { DocumentRevision = manifest.DocumentRevision + 1 }, DateTimeOffset.UtcNow)
                && !f.Store.IsCaptureCurrent(manifest with { ValidUntilUtc = DateTimeOffset.UtcNow.AddDays(1) }, DateTimeOffset.UtcNow));
        });
        await Check("identity_switch_between_metadata_capture_and_read_never_updates_same_id_in_B", f =>
        {
            var a = Entry("11111111", "A 原内容", Initial()); f.Seed(a);
            var capture = f.Store.Capture(); string bytesA = f.Bytes;
            f.Identity = "synthetic-layer-owner-B"; f.Store.Initialize(); f.Seed(Entry(a.Id, "B 原内容", Initial()));
            string bytesB = f.Bytes; int reads = 0;
            var changing = f.WithIdentity(() => ++reads == 1 ? "synthetic-layer-owner-A" : "synthetic-layer-owner-B");
            Reject(() => changing.UpdateMetadata(a.Id, a.Metadata! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) }, capture.Stamp));
            Require(f.Bytes == bytesB); f.Identity = "synthetic-layer-owner-A"; Require(f.Bytes == bytesA);
            reads = 0; changing = f.WithIdentity(() => ++reads <= 2 ? "synthetic-layer-owner-A" : "synthetic-layer-owner-B");
            Require(!changing.IsCaptureCurrent(capture.Manifest!, DateTimeOffset.UtcNow));
        });
        foreach (string corruption in new[] { "format", "v1_metadata", "kind", "scope", "confirmation", "expiry_offset", "expiry_zero",
            "version_zero", "missing_replace", "wrong_replace", "missing_dependency", "stale_dependency", "duplicate_dependency", "cycle", "scope_dependency", "invalidated_body" })
            await Check("corrupt_" + corruption + "_fails_closed_preserving_ciphertext", f =>
            {
                var source = Entry("11111111", "合成事实", Initial()); var summary = Summary("22222222", "合成摘要", source);
                var doc = new AssistantMemoryDocument { FormatVersion = 2, Profile = f.Store.Profile, Revision = 7, Entries = [source, summary] };
                switch (corruption)
                {
                    case "format": doc.FormatVersion = 99; break;
                    case "v1_metadata": doc.FormatVersion = 1; break;
                    case "kind": source = source with { Metadata = source.Metadata! with { Kind = (AssistantMemoryKind)999 } }; break;
                    case "scope": source = source with { Metadata = source.Metadata! with { Scope = new((AssistantMemoryScopeKind)999) } }; break;
                    case "confirmation": source = source with { Metadata = source.Metadata! with { Confirmation = (AssistantMemoryConfirmation)999 } }; break;
                    case "expiry_offset": source = source with { Metadata = source.Metadata! with { ExpiresAtUtc = Now.ToOffset(TimeSpan.FromHours(8)) } }; break;
                    case "expiry_zero": source = source with { Metadata = source.Metadata! with { ExpiresAtUtc = default(DateTimeOffset) } }; break;
                    case "version_zero": source = source with { Metadata = source.Metadata! with { Version = 0 } }; break;
                    case "missing_replace": source = source with { Metadata = source.Metadata! with { Version = 2 } }; break;
                    case "wrong_replace": source = source with { Metadata = source.Metadata! with { Version = 2, Replaces = new("FFFFFFFF", 1) } }; break;
                    case "missing_dependency": summary = summary with { Metadata = summary.Metadata! with { DerivedFrom = [new("FFFFFFFF", 1)] } }; break;
                    case "stale_dependency": summary = summary with { Metadata = summary.Metadata! with { DerivedFrom = [new(source.Id, 2)] } }; break;
                    case "duplicate_dependency": summary = summary with { Metadata = summary.Metadata! with { DerivedFrom = [new(source.Id, 1), new(source.Id, 1)] } }; break;
                    case "cycle": source = source with { Metadata = source.Metadata! with { Kind = AssistantMemoryKind.EpisodeSummary, DerivedFrom = [new(summary.Id, 1)] } }; break;
                    case "scope_dependency": source = source with { Metadata = source.Metadata! with { Scope = new(AssistantMemoryScopeKind.Project, "synthetic-project") } }; break;
                    case "invalidated_body": summary = summary with { Metadata = summary.Metadata! with { InvalidatedAtUtc = Now } }; break;
                }
                doc.Entries = [source, summary]; f.Write(doc); string bytes = f.Bytes;
                Reject(() => _ = f.Store.Entries); Require(bytes == f.Bytes);
            });
        await Check("invalid_capture_time_rejected", f =>
        {
            f.Store.Add("事实"); Reject(() => f.Store.Capture(new(default)));
            Reject(() => f.Store.Capture(new(Now.ToOffset(TimeSpan.FromHours(8)))));
        });
        await Check("legacy_null_metadata_can_be_explicitly_given_expiry", f =>
        {
            f.Seed(Entry("11111111", "历史内容", metadata: null)); f.RewriteVersion(1);
            var m = Initial() with { Confirmation = AssistantMemoryConfirmation.LegacyUnspecified, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
            var updated = f.Store.UpdateMetadata("11111111", m, f.Store.Capture().Stamp);
            Require(updated.Metadata!.Version == 2 && updated.Metadata.Replaces == new AssistantMemoryReference("11111111", 1)
                && f.Document().FormatVersion == 3 && f.Store.SourceDetails(updated.Id)!.Detail.Contains("历史确认状态未记录"));
        });
        if (failures != 0) throw new InvalidOperationException("memory_layer_checks_failed");
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static AssistantMemoryMetadata Initial() => AssistantMemoryLayers.Initial(AssistantMemoryKind.General);
    private static AssistantMemoryEntry Entry(string id, string text, AssistantMemoryMetadata? metadata) =>
        new(id, text, Now.AddHours(-1), Now.AddHours(-1), Metadata: metadata);
    private static AssistantMemoryEntry Summary(string id, string text, AssistantMemoryEntry source) => Entry(id, text,
        new(AssistantMemoryKind.EpisodeSummary, AssistantMemoryLayers.PersonalScope, 1, DerivedFrom: [AssistantMemoryLayers.Reference(source)]));
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (Exception e) when (e is ChatGptException or InvalidOperationException or ArgumentException or InvalidDataException) { rejected = true; }
        Require(rejected);
    }
    private sealed class Fixture
    {
        private readonly string root;
        internal string Identity = "synthetic-layer-owner-A";
        internal AssistantMemoryStore Store { get; }
        internal Fixture(string root) { this.root = root; Store = Reopen(); Store.Initialize(); }
        internal AssistantMemoryStore Reopen() => new(root, () => Identity);
        internal AssistantMemoryStore WithIdentity(Func<string> identity) => new(root, identity);
        internal string Bytes => File.ReadAllText(Store.DataPath!);
        internal AssistantMemoryDocument Document() => JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(Bytes), Settings.Json)!;
        internal void Write(AssistantMemoryDocument document) => File.WriteAllText(Store.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(document, Settings.Json)));
        internal void Seed(params AssistantMemoryEntry[] entries) => Write(new()
            { FormatVersion = 2, Profile = Store.Profile, Revision = 7, Entries = entries.ToList() });
        internal void RewriteVersion(int version) { var document = Document(); document.FormatVersion = version; Write(document); }
    }
}
