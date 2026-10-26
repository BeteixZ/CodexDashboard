using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Isolated files and synthetic current messages only. The actual durable guards
// are used, without a channel, task transport, model service or account load.
internal static class MemoryProvenanceTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-provenance-test");
        if (index < 0) return false;
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("explicit_new_output_required");
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
        var checks = new List<object>();
        int failures = 0, fixtures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true }); }
            catch (Exception error)
            { failures++; checks.Add(new { name, passed = false, detail = error is LedgerException le ? le.Code : error.Message }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "memory-provenance-checks.json"),
                JsonSerializer.Serialize(new { count = checks.Count, failures, networkUsed = false, productionDataRead = false,
                    actualModelUsed = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Task<Fixture> New() => Fixture.CreateAsync(Path.Combine(directory, "fixture-" + ++fixtures));
        await Check("v1_without_provenance_stays_readable_and_honestly_unknown", async () =>
        {
            await using var f = await New();
            File.WriteAllText(f.Memory.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(new
            {
                formatVersion = 1, profile = f.Memory.Profile, revision = 7,
                entries = new[] { new { id = "11223344", text = "历史偏好", createdAt = f.Clock.UtcNow,
                    updatedAt = f.Clock.UtcNow, origin = "automatic", preferenceKey = "report_layout" } }
            }, Settings.Json)));
            string before = File.ReadAllText(f.Memory.DataPath!);
            var details = f.Memory.SourceDetails("11223344");
            Require(f.Memory.Entries.Single().Provenance == null && details is { Known: false }
                && details.Detail.Contains("历史来源未记录") && File.ReadAllText(f.Memory.DataPath!) == before);
        });
        await Check("desktop_editor_has_distinct_source_without_message_or_operation", async () =>
        {
            await using var f = await New();
            var entry = f.Memory.AddFromEditor("桌面明确录入的偏好");
            var updated = f.Memory.UpdateFromEditor(entry.Id, "桌面纠正后的偏好");
            var source = updated.Provenance!;
            Require(source.SourceKind == "desktop_editor" && source.Profile == f.Memory.Profile
                && source.Identity == null && source.SourceMessageId == null && source.OperationId == null
                && source.InvocationId == null && source.SourceRevision == null && updated.CreatedAt == entry.CreatedAt
                && f.Memory.SourceDetails(entry.Id) is { Known: true, OriginLabel: "桌面记忆编辑器录入或纠正" });
        });
        await Check("manual_source_is_exact_current_durable_invocation_and_survives_reopen", async () =>
        {
            await using var f = await New();
            var prepared = await f.Prepare("请记住：回答先说结论", Remember("回答先说结论"));
            await f.Execute(prepared);
            var entry = f.Memory.Entries.Single();
            VerifySource(entry, prepared, f.Memory.Profile, "wechat_manual");
            string stamp = f.Memory.Capture().Stamp;
            var details = f.Memory.SourceDetails(entry.Id)!;
            Require(details.Known && details.Detail.Contains(prepared.Message.SourceMessageId)
                && f.Memory.Capture().Stamp == stamp && details.CreatedAt == entry.CreatedAt);
            await f.Reopen();
            Require(f.Memory.Entries.Single() == entry && f.Memory.SourceDetails(entry.Id)?.Provenance == entry.Provenance);
        });
        foreach (string forged in new[] { "invocation", "operation", "source", "revision", "key", "call" })
            await Check("forged_" + forged + "_cannot_write_content_or_source", async () =>
            {
                await using var f = await New();
                var p = await f.Prepare("记住简短", Remember("简短"));
                var context = forged switch
                {
                    "invocation" => p.Context with { InvocationId = Guid.NewGuid() },
                    "operation" => p.Context with { OperationId = Guid.NewGuid() },
                    "source" => p.Context with { SourceMessageId = "not-the-authorized-message" },
                    "revision" => p.Context with { Revision = p.Context.Revision + 1 },
                    "key" => p.Context with { IdempotencyKey = "wrong-key" }, _ => p.Context
                };
                var call = forged == "call" ? p.Call with { Id = "wrong-call" } : p.Call;
                Require((await f.Manual.ExecuteAsync(context, call, default)).State == ToolExecutionState.Rejected
                    && f.Memory.Entries.Count == 0);
            });
        await Check("binding_switch_rejects_source_write", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("记住简短", Remember("简短"));
            f.BindingCurrent = false;
            Require((await f.Manual.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Rejected && f.Memory.Entries.Count == 0);
        });
        await Check("automatic_source_uses_captured_current_user_not_model_evidence", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("我更喜欢分行", Learn("我更喜欢分行"));
            await f.Execute(p);
            VerifySource(f.Memory.Entries.Single(), p, f.Memory.Profile, "wechat_preference");
            Require(f.Memory.Entries.Single().Origin == "automatic" && f.Memory.Entries.Single().PreferenceKey == "report_layout");
        });
        await Check("automatic_replacement_keeps_id_and_replaces_current_source", async () =>
        {
            await using var f = await New();
            var original = await f.Prepare("我更喜欢分行", Learn("我更喜欢分行"));
            await f.Execute(original);
            var first = f.Memory.Entries.Single();
            var next = await f.Prepare("我更喜欢段落回答", Learn("我更喜欢段落回答"));
            await f.Execute(next);
            var current = f.Memory.Entries.Single();
            VerifySource(current, next, f.Memory.Profile, "wechat_preference");
            Require(current.Id == first.Id && current.CreatedAt == first.CreatedAt && current.Text == "我更喜欢段落回答"
                && current.Provenance!.SourceMessageId != first.Provenance!.SourceMessageId);
        });
        await Check("manual_correction_replaces_automatic_source_and_category", async () =>
        {
            await using var f = await New();
            await f.Execute(await f.Prepare("我更喜欢分行", Learn("我更喜欢分行")));
            var first = f.Memory.Entries.Single();
            var next = await f.Prepare("修改记忆 " + first.Id + " 先说结论", Call("update_memory", new { memoryId = first.Id, text = "先说结论" }));
            await f.Execute(next);
            var current = f.Memory.Entries.Single();
            VerifySource(current, next, f.Memory.Profile, "wechat_manual");
            Require(current.Id == first.Id && current.Origin == "explicit" && current.PreferenceKey == "" && current.Text == "先说结论");
        });
        await Check("editor_correction_drops_old_message_source", async () =>
        {
            await using var f = await New();
            await f.Execute(await f.Prepare("记住简短", Remember("简短")));
            var current = f.Memory.UpdateFromEditor(f.Memory.Entries.Single().Id, "桌面修订");
            Require(current.Provenance is { SourceKind: "desktop_editor", Identity: null, OperationId: null, SourceMessageId: null });
        });
        await Check("legacy_correction_does_not_retain_false_original_provenance", async () =>
        {
            await using var f = await New();
            var current = f.Memory.AddFromEditor("先前内容");
            current = f.Memory.Update(current.Id, "未提供来源的旧接口修订");
            Require(current.Provenance == null && f.Memory.SourceDetails(current.Id) is { Known: false });
        });
        await Check("delete_removes_active_source_without_erasing_historical_dialogue", async () =>
        {
            await using var f = await New();
            var add = await f.Prepare("记住简短", Remember("简短"));
            await f.Execute(add);
            var entry = f.Memory.Entries.Single();
            var remove = await f.Prepare("删除记忆 " + entry.Id, Call("forget", new { memoryId = entry.Id }));
            await f.Execute(remove);
            Require(f.Memory.SourceDetails(entry.Id) == null && f.Memory.Entries.Count == 0
                && !f.Memory.Capture().Context.Contains(entry.Id)
                && (await f.Ledger.ReadConversationAsync(Identity)).Inbox.Any(m => m.SourceMessageId == add.Message.SourceMessageId));
        });
        await Check("late_automatic_result_cannot_revive_deleted_entry_or_provenance", async () =>
        {
            await using var f = await New();
            await f.Execute(await f.Prepare("我更喜欢分行", Learn("我更喜欢分行")));
            var id = f.Memory.Entries.Single().Id;
            var pending = await f.Prepare("我更喜欢段落回答", Learn("我更喜欢段落回答"));
            f.Memory.Delete(id);
            Require((await f.Automatic.ExecuteAsync(pending.Context, pending.Call, default)).State == ToolExecutionState.Rejected
                && f.Memory.SourceDetails(id) == null && f.Memory.Entries.Count == 0);
        });
        await Check("late_manual_correction_cannot_revive_deleted_entry_or_provenance", async () =>
        {
            await using var f = await New();
            var entry = f.Memory.AddFromEditor("旧内容");
            var pending = await f.Prepare("修改记忆 " + entry.Id + " 新内容", Call("update_memory", new { memoryId = entry.Id, text = "新内容" }));
            f.Memory.Delete(entry.Id);
            Require((await f.Manual.ExecuteAsync(pending.Context, pending.Call, default)).State == ToolExecutionState.Rejected
                && f.Memory.SourceDetails(entry.Id) == null && f.Memory.Entries.Count == 0);
        });
        await Check("old_stamp_cannot_apply_provenance_after_delete", async () =>
        {
            await using var f = await New();
            var pending = await f.Prepare("我更喜欢分行", Learn("我更喜欢分行"));
            string stamp = f.Memory.Capture().Stamp;
            var added = f.Memory.AddFromEditor("会被删除的内容"); f.Memory.Delete(added.Id);
            int changed = f.Memory.LearnPreferences([new("report_layout", "我更喜欢分行")], stamp,
                AssistantMemoryProvenance.FromCurrentMessage("wechat_preference", f.Memory.Profile, pending.Message, pending.Context));
            Require(changed == 0 && f.Memory.Entries.Count == 0);
        });
        await Check("profile_switch_and_restart_do_not_leak_source_details", async () =>
        {
            await using var f = await New();
            await f.Execute(await f.Prepare("记住简短", Remember("简短")));
            var entry = f.Memory.Entries.Single();
            string original = f.ProfileKey;
            f.ProfileKey = "synthetic-other-owner";
            Require(f.Memory.SourceDetails(entry.Id) == null && f.Memory.Entries.Count == 0);
            f.Memory.AddFromEditor("其他身份的偏好");
            await f.Reopen();
            Require(f.Memory.SourceDetails(entry.Id) == null && f.Memory.Entries.Single().Text == "其他身份的偏好");
            f.ProfileKey = original;
            Require(f.Memory.Entries.Single() == entry && f.Memory.SourceDetails(entry.Id)?.Provenance == entry.Provenance);
        });
        await Check("metadata_is_not_raw_message_or_personalization_prompt", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("请记住：回答先说结论", Remember("回答先说结论"));
            await f.Execute(p);
            string metadata = JsonSerializer.Serialize(f.Memory.Entries.Single().Provenance, Settings.Json);
            string prompt = f.Memory.Capture().Context;
            Require(!metadata.Contains("请记住") && !metadata.Contains("回答先说结论")
                && !metadata.Contains("credential", StringComparison.OrdinalIgnoreCase)
                && !prompt.Contains(p.Context.InvocationId.ToString()) && !prompt.Contains(p.Message.SourceMessageId)
                && !prompt.Contains("sourceMessageId", StringComparison.OrdinalIgnoreCase));
        });
        foreach (string corrupt in new[] { "profile", "message", "kind", "operation" })
            await Check("corrupt_" + corrupt + "_provenance_fails_closed_without_overwrite", async () =>
            {
                await using var f = await New();
                await f.Execute(await f.Prepare("记住简短", Remember("简短")));
                string path = f.Memory.DataPath!;
                var doc = JsonNode.Parse(LocalSecret.Unprotect(File.ReadAllText(path)))!.AsObject();
                var source = doc["entries"]![0]!["provenance"]!.AsObject();
                source[corrupt switch { "profile" => "profile", "message" => "sourceMessageId", "kind" => "sourceKind", _ => "operationId" }]
                    = corrupt switch { "profile" => "other-profile", "message" => null, "kind" => "model_inference", _ => Guid.Empty.ToString() };
                string bytes = LocalSecret.Protect(doc.ToJsonString()); File.WriteAllText(path, bytes);
                bool rejected = false;
                try { _ = f.Memory.Entries; } catch (ChatGptException) { rejected = true; }
                Require(rejected && File.ReadAllText(path) == bytes);
            });
        await Check("post_write_exception_keeps_body_and_provenance_in_same_saved_document", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("记住简短", Remember("简短"));
            f.ThrowAfterSave = true;
            bool failed = false;
            try { await f.Manual.ExecuteAsync(p.Context, p.Call, default); } catch (IOException) { failed = true; }
            f.ThrowAfterSave = false;
            await f.Reopen();
            VerifySource(f.Memory.Entries.Single(), p, f.Memory.Profile, "wechat_manual");
            Require(failed && f.Memory.Entries.Single().Text == "简短"
                && (await f.Ledger.ReadConversationAsync(Identity)).Operations.Single().Stage == OperationStage.Unknown);
        });
        await Check("source_reference_never_grants_new_memory_authority", async () =>
        {
            await using var f = await New();
            var original = await f.Prepare("记住简短", Remember("简短")); await f.Execute(original);
            var entry = f.Memory.Entries.Single();
            await f.Receive("这只是一条普通消息");
            var forged = Remember("模型编造的新要求");
            Require((await f.Manual.ExecuteAsync(original.Context, forged, default)).State == ToolExecutionState.Rejected
                && f.Memory.Entries.Single() == entry);
        });
        await Check("duplicate_remember_does_not_falsely_replace_original_source", async () =>
        {
            await using var f = await New();
            await f.Execute(await f.Prepare("记住简短", Remember("简短")));
            var first = f.Memory.Entries.Single();
            await f.Execute(await f.Prepare("请记住简短", Remember("简短")));
            Require(f.Memory.Entries.Single() == first);
        });
        await Check("identity_switch_during_read_uses_captured_profile_path", () =>
        {
            string path = Path.Combine(directory, "identity-read-race");
            var a = new AssistantMemoryStore(path, () => "synthetic-owner-A");
            var b = new AssistantMemoryStore(path, () => "synthetic-owner-B");
            var original = a.AddFromEditor("A 的记忆"); b.AddFromEditor("B 的记忆");
            string active = "synthetic-owner-A";
            bool armed = true;
            var changing = new AssistantMemoryStore(path, () =>
            {
                string captured = active;
                if (armed) { armed = false; active = "synthetic-owner-B"; }
                return captured;
            });
            Require(changing.Entries.Single() == original && active == "synthetic-owner-B");
            Require(changing.SourceDetails(original.Id) == null && b.Entries.Single().Text == "B 的记忆");
            return Task.CompletedTask;
        });
        await Check("identity_switch_after_write_validation_never_redirects_A_document_to_B", () =>
        {
            string path = Path.Combine(directory, "identity-write-race");
            var a = new AssistantMemoryStore(path, () => "synthetic-owner-A");
            var b = new AssistantMemoryStore(path, () => "synthetic-owner-B");
            a.Add("A 原有内容"); var savedB = b.AddFromEditor("B 原有内容");
            string bytesB = File.ReadAllText(b.DataPath!);
            string active = "synthetic-owner-A";
            int reads = 0;
            var changing = new AssistantMemoryStore(path, () =>
            {
                string captured = active;
                // First capture belongs to ReadDocument; the second is the
                // actual WriteDocument identity check. Switch immediately after
                // that check obtains A, before the data path is resolved.
                if (++reads == 2) active = "synthetic-owner-B";
                return captured;
            });
            var added = changing.Add("A 本次新内容");
            Require(active == "synthetic-owner-B" && a.Entries.Any(e => e == added)
                && a.Entries.Count == 2 && b.Entries.Single() == savedB && File.ReadAllText(b.DataPath!) == bytesB);
            return Task.CompletedTask;
        });
        await Check("identity_switch_before_write_validation_rejects_without_changing_either_profile", () =>
        {
            string path = Path.Combine(directory, "identity-write-reject");
            var a = new AssistantMemoryStore(path, () => "synthetic-owner-A");
            var b = new AssistantMemoryStore(path, () => "synthetic-owner-B");
            a.Add("A 原有内容"); b.Add("B 原有内容");
            string bytesA = File.ReadAllText(a.DataPath!), bytesB = File.ReadAllText(b.DataPath!);
            string active = "synthetic-owner-A";
            bool armed = true;
            var changing = new AssistantMemoryStore(path, () =>
            {
                string captured = active;
                if (armed) { armed = false; active = "synthetic-owner-B"; }
                return captured;
            });
            bool rejected = false;
            try { changing.Add("不应保存的内容"); } catch (ChatGptException) { rejected = true; }
            Require(rejected && File.ReadAllText(a.DataPath!) == bytesA && File.ReadAllText(b.DataPath!) == bytesB);
            return Task.CompletedTask;
        });
        await Check("personalization_capture_never_pairs_A_memory_with_B_rules", () =>
        {
            string path = Path.Combine(directory, "identity-capture-race");
            var a = new AssistantMemoryStore(path, () => "synthetic-owner-A");
            var b = new AssistantMemoryStore(path, () => "synthetic-owner-B");
            a.Add("A 偏好"); a.SaveRules("A 专用规则"); b.Add("B 偏好"); b.SaveRules("B 专用规则");
            string active = "synthetic-owner-A";
            bool armed = true;
            var changing = new AssistantMemoryStore(path, () =>
            {
                string captured = active;
                if (armed) { armed = false; active = "synthetic-owner-B"; }
                return captured;
            });
            var captured = changing.Capture();
            Require(captured.Stamp.StartsWith(a.Profile + ":", StringComparison.Ordinal)
                && captured.Rules == "A 专用规则" && !captured.Context.Contains("B 偏好")
                && active == "synthetic-owner-B");
            return Task.CompletedTask;
        });
        if (failures != 0) throw new InvalidOperationException("memory_provenance_checks_failed");
    }
    private static readonly ConversationIdentity Identity = new("synthetic-source-user", "synthetic-source-binding", "main");
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("assertion_failed"); }
    private static void VerifySource(AssistantMemoryEntry entry, Prepared p, string profile, string kind)
    {
        var source = entry.Provenance;
        Require(source != null && source.Profile == profile && source.SourceKind == kind && source.Identity == Identity
            && source.SourceMessageId == p.Message.SourceMessageId && source.SourceRevision == p.Message.Revision
            && source.OperationId == p.Context.OperationId && source.InvocationId == p.Context.InvocationId
            && source.SourceOccurredAtUtc == p.Message.OccurredAt && source.SourceReceivedAtUtc == p.Message.ReceivedAt
            && source.RecordedAtUtc != default);
    }
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    private static ModelToolCall Remember(string text) => Call("remember", new { text, scope = "personal" });
    private static ModelToolCall Learn(string evidence) => Call("learn_preference", new { preferences = new[] { new { key = "report_layout", evidence } } });
    private sealed record Prepared(InboxMessage Message, ModelToolCall Call, ToolExecutionContext Context, GenerationLease Lease);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Script(ModelToolCall call) : IToolModel
    {
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
            => Task.FromResult(ToolModelResponse.Call(call));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Settings Settings = new() { ChatGpt = new() { Enabled = true, PersonalizationEnabled = true,
            AutoMemoryEnabled = true, ActiveAccount = "synthetic-memory-account", Model = "synthetic-model" } };
        internal readonly Clock Clock = new();
        internal string ProfileKey = "synthetic-memory-account\nsynthetic-source-binding";
        internal AssistantMemoryStore Memory = null!;
        internal DurableConversationLedger Ledger = null!;
        internal ManualMemoryTools Manual = null!;
        internal AutomaticPreferenceTools Automatic = null!;
        internal bool BindingCurrent = true, ThrowAfterSave;
        private string root = "", ledgerPath = "";
        private long epoch;
        private int sources;
        internal static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var f = new Fixture { root = Path.Combine(directory, "memory"), ledgerPath = Path.Combine(directory, "conversation.state") };
            await f.Reopen(); return f;
        }
        internal async Task Reopen()
        {
            if (Ledger != null) await Ledger.DisposeAsync();
            Memory = new(root, () => ProfileKey); Memory.Initialize();
            Memory.Changed += () => { epoch++; if (ThrowAfterSave) throw new IOException("synthetic_after_memory_write"); };
            Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath), new WindowsStateProtector(), Clock);
            Manual = new(Settings, Memory, Ledger, id => BindingCurrent && id == Identity);
            Automatic = new(Settings, Memory, Ledger, id => BindingCurrent && id == Identity, () => epoch,
                enabled => Settings.ChatGpt.AutoMemoryEnabled = enabled);
        }
        internal async Task<InboxMessage> Receive(string text) => (await Ledger.ReceiveAsync(Identity,
            "source-" + ++sources, text, MessageTrust.AuthenticatedUser, Clock.UtcNow.AddMinutes(-1))).Message;
        internal async Task<Prepared> Prepare(string text, ModelToolCall proposed)
        {
            var source = await Receive(text);
            var lease = await Ledger.BeginGenerationAsync(Identity, [source.SourceMessageId]);
            bool automatic = proposed.Name == "learn_preference";
            ToolDescriptor descriptor;
            if (automatic) descriptor = AutomaticPreferenceTools.LearnDescriptor;
            else if (!AssistantToolExecutor.Catalog().TryGet(proposed.Name, out descriptor!))
                throw new InvalidOperationException("test_tool_missing");
            Require(descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out var canonical, out _));
            var call = proposed with { ArgumentsJson = canonical };
            if (automatic)
                await Automatic.WrapModel(new Script(call)).GenerateAsync(new(source, source.Revision, [], [], []), default);
            var decision = automatic ? await Automatic.AuthorizeAsync(new(source, call, descriptor), default)
                : await Manual.AuthorizeAsync(new(source, call, descriptor), default);
            Require(decision.Allowed);
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, source.SourceMessageId,
                call.Name, decision.TargetKey!, call.ArgumentsJson, decision.SourceExcerpt!);
            string key = "synthetic-provenance-" + Guid.NewGuid().ToString("N");
            var operation = await Ledger.QueueOperationAsync(Identity, grant.Id, key);
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, source.SourceMessageId, call.Id, call.Name, canonical, true);
            await Ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
            return new(source, call, new(Identity, source.SourceMessageId, source.Revision, invocation.Id, operation.Id, key), lease);
        }
        internal async Task Execute(Prepared p)
        {
            var result = p.Call.Name == "learn_preference" ? await Automatic.ExecuteAsync(p.Context, p.Call, default)
                : await Manual.ExecuteAsync(p.Context, p.Call, default);
            Require(result.State == ToolExecutionState.Completed);
            await Ledger.FinishToolInvocationAsync(Identity, p.Context.InvocationId, ToolInvocationStage.Completed,
                result.Facts.GetRawText(), OperationStage.Completed);
            await Ledger.EndGenerationAttemptAsync(p.Lease);
        }
        public async ValueTask DisposeAsync() => await Ledger.DisposeAsync();
    }
}
