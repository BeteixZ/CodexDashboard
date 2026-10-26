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

internal static class ProjectMemoryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (Array.IndexOf(args, "--project-memory-test") < 0) return false;
        try { if (args.Length != 2 || args[0] != "--project-memory-test") throw new ArgumentException("new_output_required"); await RunAsync(args[1]); exit(0); }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); } return true;
    }
    private sealed class Resolver : IProjectMemoryResolver
    {
        internal ProjectMemoryTarget? Target;
        public ProjectMemoryTarget? Resolve(TaskReference task) => Target?.Task == task ? Target : null;
    }
    private sealed class NoTransport : ITaskTransport
    {
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
            => throw new InvalidOperationException("project_path_tests_must_not_control_tasks");
    }
    private sealed class Paths(string canonical) : IWorkspacePathResolver
    { public WorkspaceScope? Resolve(WorkspaceLocation location) => new("local", canonical, false); }
    private sealed class PathVerifier(bool safe) : IProjectMemoryPathVerifier
    {
        internal string? Source, Canonical;
        public bool IsCaseInsensitiveLocalDirectory(string sourceDirectory, string expectedCanonicalDirectory)
        { Source = sourceDirectory; Canonical = expectedCanonicalDirectory; return safe; }
    }
    private static ProjectMemoryTarget? ResolvePath(PathVerifier verifier)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new TaskRecord { Id = "synthetic-path-task", Host = "本机", Cwd = @"Q:\A", TurnId = "turn-1" };
        var snapshot = new WeChatSnapshot([task], now, [new HostSnapshot { Name = "本机", LastSuccess = now }]);
        var gateway = new CodexGateway(new Settings(), () => snapshot, new NoTransport(), () => now);
        return new ProjectMemoryResolver(gateway, () => null, new Paths(Target("A").Project.CanonicalDirectory),
            () => now, verifier).Resolve(new("local", task.Id, task.TurnId));
    }
    private sealed class Fixture
    {
        internal string Identity = "synthetic-project-memory-A";
        internal readonly AssistantMemoryStore Store;
        internal readonly Resolver Resolver = new();
        internal readonly string Root;
        internal Fixture(string root) { Root = root; Store = Reopen(); Store.Initialize(); Resolver.Target = Target("A"); }
        internal AssistantMemoryStore Reopen() => new(Root, () => Identity);
        internal AssistantMemoryEntry Add(string body = "项目 A 使用合成命令 dotnet test")
            => Store.SaveProjectFromEditor(Store.CaptureProjectEditor(Resolver.Target!, Resolver), body, Resolver);
        internal string Bytes => File.ReadAllText(Store.DataPath!);
        internal AssistantMemoryDocument Document() => JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(Bytes), Settings.Json)!;
        internal void Write(AssistantMemoryDocument value) => File.WriteAllText(Store.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(value, Settings.Json)));
    }
    internal static ProjectMemoryTarget Target(string suffix)
    {
        var project = new ProjectMemoryIdentity("local", "desktop-existing", null, @"\\?\VOLUME{01234567-89AB-CDEF-0123-456789ABCDEF}\" + suffix);
        var task = new TaskReference("local", "synthetic-task-" + suffix, "synthetic-turn");
        const string source = "synthetic-project-proof/v1";
        return new(project, task, source, ProjectMemoryProof.Fingerprint(project, task, source), DateTimeOffset.UtcNow.AddMinutes(2));
    }
    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory); var checks = new List<object>(); int failed = 0;
        async Task Check(string name, Action<Fixture> action)
        {
            try { action(new(Path.Combine(directory, "case-" + checks.Count))); checks.Add(new { name, passed = true }); }
            catch (Exception error) { failed++; checks.Add(new { name, passed = false, detail = error.ToString() }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "project-memory-checks.json"), JsonSerializer.Serialize(new
                { count = checks.Count, failures = failed, productionDataRead = false, networkUsed = false, actualModelUsed = false, checks }, Settings.Json));
        }
        await Check("explicit_project_save_encrypted_v3_and_reopen_exact", f =>
        {
            var entry = f.Add(); Require(entry.Metadata is { Kind: AssistantMemoryKind.ProjectKnowledge, Confirmation: AssistantMemoryConfirmation.Explicit, Version: 1 }
                && entry.Metadata.Scope.ProjectReference == f.Resolver.Target!.Project.Reference && entry.Provenance?.SourceKind == "desktop_editor"
                && !f.Bytes.Contains(entry.Text) && f.Document().FormatVersion == 3 && f.Reopen().Entries.Single() == entry);
        });
        await Check("ordinary_capture_and_summary_remain_personal_only", f =>
        {
            var project = f.Add(); var personal = f.Store.AddFromEditor("日常偏好简洁");
            Require(f.Store.Capture().Manifest!.IncludedReferences.SequenceEqual([new AssistantMemoryReference(personal.Id, 1)])
                && !f.Store.CaptureDerivedSummaryEditor().Sources.Any(e => e.Id == project.Id));
        });
        await Check("exact_project_capture_has_only_matching_project_and_personal_entries", f =>
        {
            var first = f.Add(); var a = f.Resolver.Target!; f.Resolver.Target = Target("B"); var second = f.Add("项目 B 仅用第二种约定");
            var personal = f.Store.AddFromEditor("个人偏好"); var capture = f.Store.Capture(new(DateTimeOffset.UtcNow, a));
            Require(capture.Manifest!.IncludedReferences.Select(r => r.Id).Order().SequenceEqual(new[] { first.Id, personal.Id }.Order())
                && !capture.Context.Contains(second.Id) && capture.Context.Contains("confirmed_project_knowledge"));
        });
        await Check("manifest_roundtrip_keeps_scope_task_source_and_exact_expiry", f =>
        {
            f.Add(); var target = f.Resolver.Target!; var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, target);
            var dependencies = AssistantMemoryDependencies.ToOutputDependencies(capture);
            Require(ProjectMemoryProof.Read(dependencies) == target && dependencies.Contexts.Length == 3
                && capture.Content!.Manifest!.ValidUntilUtc == target.ValidUntilUtc);
        });
        await Check("project_capture_expiry_is_exact_and_no_stale_prose_current", f =>
        {
            f.Add(); var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            Require(!f.Store.IsGenerationPersonalizationCurrent(capture, true, f.Resolver.Target!.ValidUntilUtc)
                && !f.Store.IsCaptureCurrent(capture.Content!.Manifest!, f.Resolver.Target.ValidUntilUtc));
        });
        await Check("expired_project_entry_not_injected", f =>
        {
            var entry = f.Add(); var doc = f.Document(); doc.Entries[0] = entry with { Metadata = entry.Metadata! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) } }; f.Write(doc);
            Require(f.Store.Capture(new(DateTimeOffset.UtcNow, f.Resolver.Target)).Manifest!.IncludedReferences.IsEmpty);
        });
        await Check("pending_and_unknown_legacy_project_entries_not_injected", f =>
        {
            var entry = f.Add(); var doc = f.Document(); doc.Entries[0] = entry with { Metadata = entry.Metadata! with { Confirmation = AssistantMemoryConfirmation.Pending } }; f.Write(doc);
            Require(f.Store.Capture(new(DateTimeOffset.UtcNow, f.Resolver.Target)).Manifest!.IncludedReferences.IsEmpty);
            doc.Entries[0] = entry with { Provenance = null }; f.Write(doc);
            Require(f.Store.Capture(new(DateTimeOffset.UtcNow, f.Resolver.Target)).Manifest!.IncludedReferences.IsEmpty);
        });
        await Check("project_correction_retains_scope_and_revokes_prior_version", f =>
        {
            var first = f.Add(); var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            var snapshot = f.Store.CaptureProjectEditor(f.Resolver.Target!, f.Resolver, first.Id);
            var second = f.Store.SaveProjectFromEditor(snapshot, "用户确认的修订约定", f.Resolver);
            Require(second.Id == first.Id && second.Metadata!.Scope == first.Metadata!.Scope && second.Metadata.Version == 2
                && second.Metadata.Replaces == new AssistantMemoryReference(first.Id, 1)
                && f.Document().Revocations.Any(r => r.EntryId == first.Id && r.Version == 1)
                && !f.Store.IsGenerationPersonalizationCurrent(capture, true, DateTimeOffset.UtcNow));
        });
        await Check("delete_revokes_without_resurrecting_project_on_reopen", f =>
        {
            var entry = f.Add(); var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            f.Store.Delete(entry.Id); Require(f.Reopen().Entries.Count == 0 && f.Document().Revocations.Single().EntryId == entry.Id
                && !f.Store.IsGenerationPersonalizationCurrent(capture, true, DateTimeOffset.UtcNow));
        });
        await Check("ordinary_update_cannot_globalize_project", f =>
        { var entry = f.Add(); string bytes = f.Bytes; Reject(() => f.Store.UpdateFromEditor(entry.Id, "错误全局化")); Require(bytes == f.Bytes); });
        await Check("stale_editor_revision_fails_without_write", f =>
        { f.Add(); var snapshot = f.Store.CaptureProjectEditor(f.Resolver.Target!, f.Resolver); f.Store.AddFromEditor("新个人输入"); string bytes = f.Bytes; Reject(() => f.Store.SaveProjectFromEditor(snapshot, "过期保存", f.Resolver)); Require(bytes == f.Bytes); });
        await Check("identity_change_never_writes_new_profile", f =>
        { var snapshot = f.Store.CaptureProjectEditor(f.Resolver.Target!, f.Resolver); f.Identity = "synthetic-project-memory-B"; Reject(() => f.Store.SaveProjectFromEditor(snapshot, "不应保存", f.Resolver)); Require(f.Store.Entries.Count == 0); });
        await Check("mapping_change_and_disconnect_reject_editor", f =>
        { var snapshot = f.Store.CaptureProjectEditor(f.Resolver.Target!, f.Resolver); f.Resolver.Target = Target("B"); Reject(() => f.Store.SaveProjectFromEditor(snapshot, "不应转存其他项目", f.Resolver)); f.Resolver.Target = null; Reject(() => f.Store.SaveProjectFromEditor(snapshot, "不应离线保存", f.Resolver)); Require(f.Store.Entries.Count == 0); });
        await Check("same_text_other_project_is_distinct_and_same_scope_idempotent", f =>
        { var first = f.Add(); var repeated = f.Add(); f.Resolver.Target = Target("B"); var second = f.Add(); Require(first.Id == repeated.Id && second.Id != first.Id && f.Store.Entries.Count == 2); });
        await Check("scope_correction_cannot_move_existing_entry", f =>
        { var first = f.Add(); f.Resolver.Target = Target("B"); Reject(() => f.Store.CaptureProjectEditor(f.Resolver.Target, f.Resolver, first.Id)); Require(f.Store.Entries.Single() == first); });
        await Check("committed_callback_failure_reports_saved_entry_once", f =>
        {
            f.Store.Changed += () => throw new IOException("synthetic-observer");
            ProjectMemorySavedNotificationException? failure = null;
            try { f.Add(); } catch (ProjectMemorySavedNotificationException error) { failure = error; }
            Require(failure != null && f.Reopen().Entries.Single() == failure.SavedEntry);
        });
        await Check("oversize_scope_and_body_reject_without_truncation", f =>
        { Reject(() => _ = (f.Resolver.Target!.Project with { CanonicalDirectory = f.Resolver.Target.Project.CanonicalDirectory + new string('X', 600) }).Reference); Reject(() => f.Add(new string('X', 1201))); Require(f.Store.Entries.Count == 0); });
        await Check("capacity_rejection_preserves_existing_memory", f =>
        { for (int i = 0; i < AssistantMemoryStore.MaxEntries; i++) f.Store.AddFromEditor("个人记录 " + i); string bytes = f.Bytes; Reject(() => f.Add()); Require(f.Bytes == bytes); });
        await Check("project_dependencies_cannot_be_forged_as_remote_or_other_profile", f =>
        {
            var target = f.Resolver.Target!;
            Reject(() => _ = (target.Project with { HostId = "remote-ssh-discovered:synthetic" }).Reference);
            Reject(() => _ = (target.Project with { ProfileId = "unknown-profile" }).Reference);
            var proof = ProjectMemoryProof.Dependency(target);
            Reject(() => ProjectMemoryProof.Read(new(OutputDependencyMode.Bound, [proof with { Revision = 2 }])));
        });
        await Check("personalization_off_never_carries_project_context", f =>
        { f.Add(); var capture = f.Store.CaptureForGeneration(false, DateTimeOffset.UtcNow, f.Resolver.Target); Require(capture.Content == null && ProjectMemoryProof.Read(AssistantMemoryDependencies.ToOutputDependencies(capture)) == null); });
        await Check("focus_without_selected_project_prose_does_not_shorten_personal_capture_lifetime", f =>
        {
            f.Store.AddFromEditor("日常个人偏好");
            var capture = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            Require(capture.Content!.Manifest!.Project == null && capture.Content.Manifest.ValidUntilUtc == null
                && ProjectMemoryProof.Read(AssistantMemoryDependencies.ToOutputDependencies(capture)) == null
                && f.Store.IsGenerationPersonalizationCurrent(capture, true, f.Resolver.Target!.ValidUntilUtc.AddSeconds(1)));
        });
        await Check("personal_manual_mutation_preserves_bound_project_transition", f =>
        {
            f.Add(); var captured = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            var command = f.Store.CaptureManual("记住：以后先说结论");
            var provenance = new AssistantMemoryProvenance("desktop_editor", f.Store.Profile, DateTimeOffset.UtcNow);
            var mutation = f.Store.ApplyManualWithContext(command.Resolution, command.Stamp, provenance, AssistantMemoryDependencies.ToOutputDependencies(captured));
            Require(mutation.Changed && mutation.Before.Content!.Manifest!.Project == f.Resolver.Target
                && mutation.After.Content!.Manifest!.Project == f.Resolver.Target
                && ProjectMemoryProof.Read(AssistantMemoryDependencies.ToOutputDependencies(mutation.After)) == f.Resolver.Target);
        });
        await Check("wrong_project_before_stamp_cannot_mutate_personal_memory", f =>
        {
            f.Add(); var before = f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target);
            var command = f.Store.CaptureManual("记住：以后先说结论"); string bytes = f.Bytes;
            var dependencies = AssistantMemoryDependencies.ToOutputDependencies(before);
            var tampered = dependencies with { Contexts = dependencies.Contexts.Select(c => c.Provider == ProjectMemoryProof.Provider ? c with { Fingerprint = "forged" } : c).ToImmutableArray() };
            Reject(() => f.Store.ApplyManualWithContext(command.Resolution, command.Stamp, new("desktop_editor", f.Store.Profile, DateTimeOffset.UtcNow), tampered)); Require(f.Bytes == bytes);
        });
        await Check("model_memory_read_uses_only_exact_captured_references", f =>
        {
            var a = f.Add(); var targetA = f.Resolver.Target!;
            f.Resolver.Target = Target("B"); var b = f.Add("MODEL_OTHER_PROJECT_BODY"); var personal = f.Store.AddFromEditor("个人资料");
            var context = new GenerationContext(Guid.NewGuid(), 0,
                AssistantMemoryDependencies.ToOutputDependencies(f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, targetA)));
            var selected = f.Store.ReadGenerationEntries(context, DateTimeOffset.UtcNow);
            Require(selected.Select(item => item.Id).Order().SequenceEqual(new[] { a.Id, personal.Id }.Order())
                && !selected.Any(item => item.Id == b.Id));
        });
        await Check("model_memory_read_rejects_missing_manifest_or_changed_document", f =>
        {
            f.Add(); var context = new GenerationContext(Guid.NewGuid(), 0,
                AssistantMemoryDependencies.ToOutputDependencies(f.Store.CaptureForGeneration(true, DateTimeOffset.UtcNow, f.Resolver.Target)));
            Reject(() => f.Store.ReadGenerationEntries(context with { Dependencies = OutputDependencies.Independent }, DateTimeOffset.UtcNow));
            f.Store.AddFromEditor("本轮捕获之后的个人资料");
            Reject(() => f.Store.ReadGenerationEntries(context, DateTimeOffset.UtcNow));
        });
        await Check("wechat_management_list_never_serializes_project_body", f =>
        {
            var project = f.Add("PROJECT_MANAGEMENT_PROSE_SENTINEL"); var personal = f.Store.AddFromEditor("PERSONAL_MANAGEMENT_PROSE_SENTINEL");
            var commands = new AssistantMemoryCommands(f.Store);
            foreach (string command in new[] { "查看记忆", "我的记忆", "你记住了什么？", "记忆 1" })
                Require(commands.TryHandle(command, out string text) && text.Contains(project.Id)
                    && text.Contains(personal.Text) && text.Contains("正文请在桌面") && !text.Contains(project.Text));
        });
        await Check("project_resolver_requires_path_verifier_and_preserves_personal_memory", f =>
        {
            f.Add(); var personal = f.Store.AddFromEditor("个人知识不受项目目录能力限制");
            var verifier = new PathVerifier(false); var target = ResolvePath(verifier);
            Require(target == null && verifier.Source == @"Q:\A"
                && verifier.Canonical == Target("A").Project.CanonicalDirectory
                && f.Store.Capture(new(DateTimeOffset.UtcNow, target)).Manifest!.IncludedReferences
                    .SequenceEqual([new AssistantMemoryReference(personal.Id, 1)]));
        });
        await Check("project_resolver_verified_normal_path_keeps_existing_scope", _ =>
        {
            var verifier = new PathVerifier(true); var target = ResolvePath(verifier);
            Require(target?.Project == Target("A").Project && verifier.Source == @"Q:\A");
        });
        await ProjectMemoryPathTests.RunAsync(directory, (name, action) => Check(name, _ => action()));
        if (failed != 0) throw new InvalidOperationException("project_memory_checks_failed:" + failed);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("project_memory_assertion"); }
    private static void Reject(Action action)
    { bool rejected = false; try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or ChatGptException or JsonException) { rejected = true; } Require(rejected); }
}
