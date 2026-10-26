using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Actual isolated journal + memory files, synthetic identities and scripted model.
// No settings/account loading, transport, production directory or network access.
internal static class ManualMemoryToolsTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--manual-memory-tools-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("explicit_output_directory_required");
            await RunAsync(args[index + 1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }
    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("new_absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        int failures = 0, fixtures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true }); }
            catch (Exception error)
            {
                failures++; checks.Add(new { name, passed = false, errorType = error.GetType().Name,
                    detail = error is LedgerException ledgerError ? ledgerError.Code
                        : error is InvalidOperationException ? error.Message : null });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "manual-memory-checks.json"),
                JsonSerializer.Serialize(new { count = checks.Count, failures, productionDataRead = false,
                    networkUsed = false, actualModelUsed = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Task<Fixture> New() => Fixture.CreateAsync(Path.Combine(directory, "fixture-" + ++fixtures));

        await Check("queued_unchanged_authorization_resumes_after_restart", async () =>
        {
            await using var f = await New();
            var queued = await f.Prepare("帮我记住以后先说结论", Remember("以后先说结论"), dispatch: false);
            var before = (await f.Read()).Operations.Single();
            await f.Ledger.EndGenerationAttemptAsync(queued.Lease);
            await f.Reopen();
            await f.Run(queued.Message.Body, queued.Call, queued.Message.SourceMessageId);
            var after = await f.Read();
            Require(f.Memory.Entries.Single().Text == "以后先说结论"
                && after.Operations.Single().Id == before.Id && after.Operations.Single().TargetKey == before.TargetKey
                && after.Operations.Single().Stage == OperationStage.Completed
                && after.ToolInvocations.Single().Stage == ToolInvocationStage.Completed);
        });
        foreach (string mutation in new[] { "delete", "update", "rules", "unrelated" })
            await Check("queued_remember_old_stamp_cannot_revive_after_" + mutation, async () =>
            {
                await using var f = await New();
                var prior = f.Memory.Add("原有偏好");
                var queued = await f.Prepare("记住：原有偏好", Remember("原有偏好"), dispatch: false);
                var before = (await f.Read()).Operations.Single();
                await f.Ledger.EndGenerationAttemptAsync(queued.Lease);
                if (mutation == "delete") f.Memory.Delete(prior.Id);
                else if (mutation == "update") f.Memory.Update(prior.Id, "用户更新的偏好");
                else if (mutation == "rules") f.Memory.SaveRules("合成测试表达规则已变更");
                else f.Memory.Add("另一条独立偏好");
                string stamp = f.Memory.Capture().Stamp;
                var entries = f.Memory.Entries.ToArray();
                await f.Reopen();
                await f.Run(queued.Message.Body, queued.Call, queued.Message.SourceMessageId);
                var after = await f.Read();
                Require(f.Memory.Capture().Stamp == stamp && f.Memory.Entries.SequenceEqual(entries)
                    && after.Operations.Single().Id == before.Id && after.Operations.Single().TargetKey == before.TargetKey);
                await f.RequireAbandoned(queued, before);
            });
        foreach (bool remove in new[] { false, true })
            await Check(remove ? "queued_forget_cannot_delete_changed_same_id" : "queued_update_cannot_replace_changed_same_id", async () =>
            {
                await using var f = await New();
                var entry = f.Memory.Add("旧内容");
                var call = remove ? Call("forget", new { memoryId = entry.Id })
                    : Call("update_memory", new { memoryId = entry.Id, text = "之前要求的新内容" });
                string source = remove ? "删除记忆 " + entry.Id : "修改记忆 " + entry.Id + " 之前要求的新内容";
                var queued = await f.Prepare(source, call, dispatch: false);
                var before = (await f.Read()).Operations.Single();
                await f.Ledger.EndGenerationAttemptAsync(queued.Lease);
                var changed = f.Memory.Update(entry.Id, "用户后来明确修改的内容");
                string stamp = f.Memory.Capture().Stamp;
                await f.Reopen();
                await f.Run(source, queued.Call, queued.Message.SourceMessageId);
                Require(f.Memory.Entries.Single() == changed && f.Memory.Capture().Stamp == stamp);
                await f.RequireAbandoned(queued, before);
            });
        await Check("queued_keyword_new_ambiguity_never_picks_first", async () =>
        {
            await using var f = await New();
            var prior = f.Memory.Add("汇报先说结论");
            var queued = await f.Prepare("删除记忆 先说结论", Call("forget", new { memoryId = prior.Id }), dispatch: false);
            var before = (await f.Read()).Operations.Single();
            await f.Ledger.EndGenerationAttemptAsync(queued.Lease);
            f.Memory.Add("回顾也先说结论");
            string stamp = f.Memory.Capture().Stamp;
            await f.Reopen();
            await f.Run(queued.Message.Body, queued.Call, queued.Message.SourceMessageId);
            Require(f.Memory.Entries.Count == 2 && f.Memory.Capture().Stamp == stamp);
            await f.RequireAbandoned(queued, before);
        });
        await Check("dispatching_restart_is_unknown_and_never_replayed", async () =>
        {
            await using var f = await New();
            var prepared = await f.Prepare("记住：先说结论", Remember("先说结论"));
            await f.Reopen();
            Require((await f.Read()).Operations.Single().Stage == OperationStage.Unknown);
            await f.Run(prepared.Message.Body, prepared.Call, prepared.Message.SourceMessageId);
            Require(f.Memory.Entries.Count == 0 && (await f.Read()).Operations.Single().Stage == OperationStage.Unknown);
        });
        await Check("persisted_effect_with_uncertain_receipt_is_not_replayed", async () =>
        {
            await using var f = await New();
            f.ThrowAfterSave = true;
            await f.Run("记住：先说结论", Remember("先说结论"), "uncertain-source", failFinal: true);
            Require(f.Memory.Entries.Single().Text == "先说结论" && (await f.Read()).Operations.Single().Stage == OperationStage.Unknown);
            string stamp = f.Memory.Capture().Stamp;
            f.ThrowAfterSave = false;
            await f.Reopen();
            await f.Run("记住：先说结论", Remember("先说结论"), "uncertain-source");
            Require(f.Memory.Capture().Stamp == stamp && f.Memory.Entries.Count == 1
                && (await f.Read()).Operations.Single().Stage == OperationStage.Unknown);
        });
        await Check("forged_invocation_call_and_context_cannot_borrow_real_dispatch", async () =>
        {
            await using var f = await New();
            var p = await f.Prepare("记住：先说结论", Remember("先说结论"));
            foreach (var forged in new[]
            {
                p.Context with { InvocationId = Guid.NewGuid() }, p.Context with { OperationId = Guid.NewGuid() },
                p.Context with { SourceMessageId = "forged-source" }, p.Context with { Revision = p.Context.Revision + 1 },
                p.Context with { IdempotencyKey = "forged-key" }, p.Context with { InputWorkId = Guid.NewGuid() },
                p.Context with { ReadAtRevision = p.Context.Revision }
            }) Require((await f.Guard.ExecuteAsync(forged, p.Call, default)).State == ToolExecutionState.Rejected);
            Require((await f.Guard.ExecuteAsync(p.Context, p.Call with { Id = "forged-call" }, default)).State == ToolExecutionState.Rejected);
            Require((await f.Guard.ExecuteAsync(p.Context, Remember("模型改写的内容"), default)).State == ToolExecutionState.Rejected);
            Require(f.Memory.Entries.Count == 0);
            Require((await f.Guard.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Completed);
            string stamp = f.Memory.Capture().Stamp;
            Require((await f.Guard.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Rejected
                && f.Memory.Capture().Stamp == stamp);
        });
        await Check("forged_source_body_cannot_authorize", async () =>
        {
            await using var f = await New();
            var message = await f.Receive("请解释这句话：记住先说结论");
            var call = Remember("先说结论");
            AssistantToolExecutor.Catalog().TryGet(call.Name, out var descriptor);
            Require(!(await f.Guard.AuthorizeAsync(new(message with { Body = "记住：先说结论" }, call, descriptor), default)).Allowed);
            Require(f.Memory.Entries.Count == 0);
        });
        foreach (string source in new[] { "引用：记住先说结论", "如果测试通过，记住先说结论", "记住先说结论，并且不要执行", "记住这句话是什么意思" })
            await Check("model_cannot_extract_authority_from_protected_source_" + checks.Count, async () =>
            {
                await using var f = await New();
                await f.Run(source, Remember("先说结论"));
                Require(f.Memory.Entries.Count == 0 && (await f.Read()).Operations.IsEmpty);
            });
        foreach (string invalidation in new[] { "binding", "message", "generation", "setting" })
            await Check("dispatch_guard_rechecks_" + invalidation, async () =>
            {
                await using var f = await New();
                var p = await f.Prepare("记住：先说结论", Remember("先说结论"));
                if (invalidation == "binding") f.BindingCurrent = false;
                else if (invalidation == "message") await f.Receive("这次不要保存");
                else if (invalidation == "generation") await f.Ledger.EndGenerationAttemptAsync(p.Lease);
                else f.Settings.ChatGpt.PersonalizationEnabled = false;
                Require((await f.Guard.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Rejected
                    && f.Memory.Entries.Count == 0);
            });
        await Check("model_off_manual_guard_requires_no_model", async () =>
        {
            await using var f = await New();
            f.Settings.ChatGpt.Enabled = false;
            var p = await f.Prepare("请记住：先说结论", Remember("先说结论"));
            Require((await f.Guard.ExecuteAsync(p.Context, p.Call, default)).State == ToolExecutionState.Completed
                && f.Memory.Entries.Single().Text == "先说结论");
        });
        await Check("store_gate_rejects_changed_target_between_capture_and_apply", async () =>
        {
            await using var f = await New();
            var entry = f.Memory.Add("旧内容");
            var expected = f.Memory.CaptureManual("删除记忆 " + entry.Id);
            var current = f.Memory.Update(entry.Id, "用户更新的内容");
            bool rejected = false;
            try { f.Memory.ApplyManual(expected.Resolution, expected.Stamp); }
            catch (InvalidOperationException error) when (error.Message == "manual_memory_target_changed") { rejected = true; }
            Require(rejected && f.Memory.Entries.Single() == current);
        });
        if (failures != 0) throw new InvalidOperationException("manual_memory_tools_failed");
    }
    private static readonly ConversationIdentity Identity = new("synthetic-manual-user", "synthetic-manual-binding", "main");
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    private static ModelToolCall Remember(string text) => Call("remember", new { text, scope = "personal" });
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("assertion_failed"); }
    private sealed record Prepared(InboxMessage Message, ModelToolCall Call, ToolExecutionContext Context, GenerationLease Lease);
    private sealed class Script(ModelToolCall call, bool failFinal) : IToolModel
    {
        private int turns;
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++turns == 1) return Task.FromResult(ToolModelResponse.Call(call));
            if (failFinal) throw new IOException("synthetic_final_model_unavailable");
            return Task.FromResult(ToolModelResponse.Reply("合成测试回复。"));
        }
    }
    private sealed class Fixture : IAsyncDisposable, IToolExecutor, IUserAuthorizationPolicy
    {
        internal Settings Settings = new() { ChatGpt = new() { Enabled = true, PersonalizationEnabled = true, ActiveAccount = "synthetic-manual-account" } };
        internal AssistantMemoryStore Memory = null!;
        internal DurableConversationLedger Ledger = null!;
        internal ManualMemoryTools Guard = null!;
        internal bool BindingCurrent = true, ThrowAfterSave;
        internal int Executions;
        private string ledgerPath = "";
        private int sources;
        internal static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var f = new Fixture { ledgerPath = Path.Combine(directory, "conversation.state") };
            f.Memory = new(Path.Combine(directory, "memory"), () => f.Settings.ChatGpt.ActiveAccount + "\nsynthetic-manual-binding");
            f.Memory.Initialize();
            f.Memory.Changed += () => { if (f.ThrowAfterSave) throw new IOException("synthetic_after_memory_commit"); };
            await f.Reopen();
            return f;
        }
        internal async Task Reopen()
        {
            if (Ledger != null) await Ledger.DisposeAsync();
            Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(ledgerPath), new WindowsStateProtector());
            Guard = new(Settings, Memory, Ledger, id => BindingCurrent && id == Identity);
        }
        internal Task<ConversationSnapshot> Read() => Ledger.ReadConversationAsync(Identity);
        internal async Task RequireAbandoned(Prepared prepared, OperationRecord before)
        {
            var saved = await Read(); var operation = saved.Operations.Single(); var invocation = saved.ToolInvocations.Single();
            using var evidence = JsonDocument.Parse(invocation.ResultJson ?? "{}");
            await File.WriteAllTextAsync(Path.ChangeExtension(ledgerPath, "cleanup-evidence.json"), JsonSerializer.Serialize(new
            {
                originalOperationId = before.Id, operationId = operation.Id, operationStage = operation.Stage.ToString(),
                invocationId = invocation.Id, expectedInvocationId = prepared.Context.InvocationId,
                invocationStage = invocation.Stage.ToString(), executions = Executions,
                exactOriginalTarget = operation.TargetKey == before.TargetKey, result = evidence.RootElement.Clone()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Require(operation.Stage == OperationStage.CancelledBeforeDispatch && invocation.Stage == ToolInvocationStage.Unavailable
                && invocation.Id == prepared.Context.InvocationId && invocation.OperationId == before.Id
                && invocation.SourceMessageId == prepared.Message.SourceMessageId && invocation.ArgumentsJson == prepared.Call.ArgumentsJson
                && (operation with { Stage = before.Stage, UpdatedAt = before.UpdatedAt, Receipt = before.Receipt }) == before
                && evidence.RootElement.GetProperty("status").GetString() == "not_started" && Executions == 0);
        }
        internal async Task<InboxMessage> Receive(string source, string? id = null)
            => (await Ledger.ReceiveAsync(Identity, id ?? "source-" + ++sources, source,
                MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow)).Message;
        internal async Task<ToolLoopResult> Run(string source, ModelToolCall call, string? id = null, bool failFinal = false)
        {
            var message = await Receive(source, id);
            var loop = new ToolAgentLoop(Ledger, new Script(call, failFinal), AssistantToolExecutor.Catalog(), this, this);
            return await loop.RunAsync(Identity, message.SourceMessageId);
        }
        internal async Task<Prepared> Prepare(string source, ModelToolCall proposed, bool dispatch = true)
        {
            var message = await Receive(source);
            var lease = await Ledger.BeginGenerationAsync(Identity, [message.SourceMessageId]);
            Require(AssistantToolExecutor.Catalog().TryGet(proposed.Name, out var descriptor));
            Require(descriptor.Parameters.TryValidate(proposed.ArgumentsJson, out var canonical, out _));
            var call = proposed with { ArgumentsJson = canonical };
            var permission = await Guard.AuthorizeAsync(new(message, call, descriptor), default);
            Require(permission.Allowed);
            var grant = await Ledger.AuthorizeCurrentUserRequestAsync(Identity, message.SourceMessageId,
                call.Name, permission.TargetKey!, canonical, permission.SourceExcerpt!);
            string key = "tool-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                message.SourceMessageId + "\0" + call.Name + "\0" + canonical)));
            var operation = await Ledger.QueueOperationAsync(Identity, grant.Id, key);
            var invocation = await Ledger.RegisterToolInvocationAsync(lease, message.SourceMessageId, call.Id, call.Name, canonical, true);
            if (dispatch) await Ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id);
            return new(message, call, new(Identity, message.SourceMessageId, message.Revision, invocation.Id, operation.Id, key), lease);
        }
        public ToolCapability GetCapability(string capability) => new(ManualMemoryTools.Handles(capability) && Settings.ChatGpt.PersonalizationEnabled);
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
        { Executions++; return Guard.ExecuteAsync(context, call, ct); }
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
            => Guard.AuthorizeAsync(request, ct);
        public async ValueTask DisposeAsync() => await Ledger.DisposeAsync();
    }
}
