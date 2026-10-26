using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static class ConversationContextTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly TaskReference A = new("local", "A", "turn-A");
    private static readonly TaskReference B = new("remote", "B", "turn-B", "profile");
    private static readonly TaskSelectionCandidate[] Candidates = [new(A, "A"), new(B, "B")];
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("default_context_has_no_inferred_focus_or_authority", DefaultContext),
        ("readonly_task_event_does_not_replace_work_focus", ReadDoesNotFocus),
        ("notification_slot_does_not_replace_work_focus_or_selection", IndependentNotification),
        ("selection_freezes_candidates_and_only_changes_focus", Selection),
        ("inspection_selection_does_not_change_work_focus", InspectSelection),
        ("completed_selection_survives_restart_idempotently", SelectionRecovery),
        ("expired_selection_cannot_be_answered", SelectionExpiry),
        ("focus_requires_current_trusted_source_and_literal_excerpt", FocusAuthority),
        ("approval_context_is_independent_data_not_authority", ApprovalSlots),
        ("approval_requires_exact_event_task_turn_and_dialogue", ApprovalScope),
        ("new_dialogue_is_atomic_idempotent_and_preserves_operations", Reset),
        ("new_dialogue_drops_old_prompt_context_but_keeps_history", PromptBoundary),
        ("new_dialogue_rejects_old_selection_without_recreating_grant", ResetSelection),
        ("old_notification_acceptance_does_not_repopulate_reset_context", OldNotification),
        ("context_slots_restore_without_user_authority", Restore),
        ("legacy_snapshot_without_contexts_remains_compatible", Legacy),
        ("invalid_context_reference_fails_closed_preserving_storage", Corrupt),
        ("interrupted_reset_keeps_complete_previous_context_readonly", ResetFault)
    ];

    internal static async Task<int> Run()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS context/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL context/" + name + ": " + error); }
        }
        Console.WriteLine($"CONTEXT TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }
    private static Task<ReceiveResult> Input(DurableConversationLedger ledger, string id, string body = "focus A") =>
        ledger.ReceiveAsync(Identity, id, body, MessageTrust.AuthenticatedUser, FakeClock.Now);
    private static async Task FocusA(DurableConversationLedger ledger)
    {
        await Input(ledger, "focus");
        await ledger.SetWorkFocusAsync(Identity, "focus", A, "focus A");
    }
    private static async Task DefaultContext()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var context = await ledger.ReadConversationContextAsync(Identity);
        Equal(0L, context.Epoch);
        Check(context.Focus is null && context.PendingSelection is null && context.PendingApproval is null && context.LastNotification is null);
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Events.Length);
    }
    private static async Task ReadDoesNotFocus()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        await Input(ledger, "read-B", "read B");
        await ledger.AppendContextAsync(Identity, ConversationSource.ToolResult, "B facts", FakeClock.Now, B);
        Equal(A, (await ledger.ReadConversationContextAsync(Identity)).Focus!.Task);
    }
    private static async Task IndependentNotification()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "focus", TaskSelectionPurpose.Inspect, Candidates, TimeSpan.FromMinutes(5));
        var notice = await ledger.CaptureNotificationAsync(Identity, "B-completed", "B completed", "{}", B);
        var context = await ledger.ReadConversationContextAsync(Identity);
        Equal(A, context.Focus!.Task);
        Equal(selection, context.PendingSelection);
        Equal(notice.Event.Id, context.LastNotification!.EventId);
        Check(context.LastNotification.LastAcceptedOutboxId is null);
    }
    private static async Task Selection()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "choose");
        var supplied = Candidates.ToArray();
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "choose", TaskSelectionPurpose.Focus, supplied, TimeSpan.FromMinutes(5));
        supplied[1] = new(A, "changed");
        await Input(ledger, "two", "2");
        var result = await ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 2, "2");
        Equal(B, result.Task);
        Equal("choose", result.OriginalSourceMessageId);
        Equal("two", result.SelectionSourceMessageId);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(B, state.Context!.Focus!.Task);
        Check(state.Context.PendingSelection is null);
        Check(state.Authorizations.IsEmpty && state.Operations.IsEmpty);
        await Error(LedgerErrors.Conflict, () => ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 1, "2"));
    }
    private static async Task InspectSelection()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "focus", TaskSelectionPurpose.Inspect, Candidates, TimeSpan.FromMinutes(5));
        await Input(ledger, "two", "2");
        var result = await ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 2, "2");
        Equal(B, result.Task);
        Equal(A, (await ledger.ReadConversationContextAsync(Identity)).Focus!.Task);
    }
    private static async Task SelectionRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "choose");
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "choose", TaskSelectionPurpose.Focus, Candidates, TimeSpan.FromMinutes(5));
        await Input(ledger, "two", "2");
        var original = await ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 2, "2");
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(original, await ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 2, "2"));
        Check((await ledger.ReadConversationAsync(Identity)).Authorizations.IsEmpty);
    }
    private static async Task SelectionExpiry()
    {
        await using var f = new Fixture();
        var clock = new MutableClock();
        await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(f.Path), new TestEncryption(f.Key), clock);
        await Input(ledger, "choose");
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "choose", TaskSelectionPurpose.Focus, Candidates, TimeSpan.FromMinutes(5));
        clock.Now = clock.Now.AddMinutes(6);
        await Input(ledger, "late", "2");
        await Error(LedgerErrors.InvalidTransition, () => ledger.SelectTaskCandidateAsync(Identity, "late", selection.Id, 2, "2"));
        Check((await ledger.ReadConversationContextAsync(Identity)).Focus is null);
    }
    private static async Task FocusAuthority()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        await Input(ledger, "new", "B");
        await Error(LedgerErrors.StaleRevision, () => ledger.SetWorkFocusAsync(Identity, "focus", B, "focus A"));
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.SetWorkFocusAsync(Identity, "new", B, "not in message"));
        await ledger.ReceiveAsync(Identity, "untrusted", "focus B", MessageTrust.UntrustedContent, FakeClock.Now);
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.SetWorkFocusAsync(Identity, "untrusted", B, "focus B"));
        Equal(A, (await ledger.ReadConversationContextAsync(Identity)).Focus!.Task);
    }
    private static async Task ApprovalSlots()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "focus", TaskSelectionPurpose.Inspect, Candidates, TimeSpan.FromMinutes(5));
        var fact = await ledger.AppendContextAsync(Identity, ConversationSource.ToolResult, "B awaits request", FakeClock.Now, B);
        var approval = await ledger.RecordPendingApprovalAsync(Identity, fact.Id, "request-B", B, Guid.NewGuid(), FakeClock.Now.AddMinutes(5), 0);
        await ledger.EnqueueNotificationAsync(Identity, "A-notice", "A finished", A);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(A, state.Context!.Focus!.Task);
        Equal(selection, state.Context.PendingSelection);
        Equal(approval, state.Context.PendingApproval);
        Check(state.Authorizations.IsEmpty && state.Operations.IsEmpty);
        Check(!await ledger.ResolvePendingApprovalAsync(Identity, Guid.NewGuid()));
        Check(await ledger.ResolvePendingApprovalAsync(Identity, approval.Id));
        Equal(selection, (await ledger.ReadConversationContextAsync(Identity)).PendingSelection);
    }
    private static async Task ApprovalScope()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var fact = await ledger.AppendContextAsync(Identity, ConversationSource.ToolResult, "B awaits request", FakeClock.Now, B);
        await Error(LedgerErrors.IdentityMismatch, () => ledger.RecordPendingApprovalAsync(Identity, fact.Id,
            "request", A, Guid.NewGuid(), FakeClock.Now.AddMinutes(5), 0));
        await Input(ledger, "reset", "新对话");
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        await Error(LedgerErrors.StaleRevision, () => ledger.RecordPendingApprovalAsync(Identity, fact.Id,
            "request", B, Guid.NewGuid(), FakeClock.Now.AddMinutes(5), 0));
        var report = await ledger.AppendContextAsync(Identity, ConversationSource.Report, "approve me", FakeClock.Now, B);
        await Error(LedgerErrors.IdentityMismatch, () => ledger.RecordPendingApprovalAsync(Identity, report.Id,
            "request", B, Guid.NewGuid(), FakeClock.Now.AddMinutes(5), 1));
    }
    private static async Task Reset()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        var authorization = await ledger.AuthorizeCurrentUserRequestAsync(Identity, "focus", "test", "A", "{}", "focus A", A);
        var operation = await ledger.QueueOperationAsync(Identity, authorization.Id, "one");
        var notification = await ledger.EnqueueNotificationAsync(Identity, "notice", "A done", A);
        await Input(ledger, "reset", "新对话");
        var reset = await ledger.BeginNewDialogueAsync(Identity, "reset");
        Equal(reset, await ledger.BeginNewDialogueAsync(Identity, "reset"));
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(1L, reset.Epoch);
        Equal(state.Revision, reset.StartRevision);
        Check(reset.Focus is null && reset.LastNotification is null && reset.PendingSelection is null && reset.PendingApproval is null);
        Equal(operation, state.Operations.Single());
        Equal(notification.Outbox[0], state.Outbox.Single());
        Equal(InboxStage.Cancelled, state.Inbox.Single(x => x.SourceMessageId == "focus").Stage);
        Check(state.Events.Any(x => x.Body == "focus A"));
    }
    private static async Task PromptBoundary()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "old", "OLD_DIALOGUE_SECRET");
        await Input(ledger, "reset", "新对话");
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        await Input(ledger, "new", "Hello");
        var model = new RecordingModel();
        var loop = new ToolAgentLoop(ledger, model, ToolCatalog.Initial(), new NoTools(), new NoAuthority());
        await loop.RunAsync(Identity, "new");
        Check(model.Request!.Context.All(x => x.Body != "OLD_DIALOGUE_SECRET"));
        Equal(1L, model.Request.ConversationContext!.Epoch);
        Check((await ledger.ReadConversationAsync(Identity)).Events.Any(x => x.Body == "OLD_DIALOGUE_SECRET"));
    }
    private static async Task ResetSelection()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await Input(ledger, "choose");
        var selection = await ledger.OpenTaskSelectionAsync(Identity, "choose", TaskSelectionPurpose.Focus, Candidates, TimeSpan.FromMinutes(5));
        await Input(ledger, "reset", "新对话");
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        await Input(ledger, "two", "2");
        await Error(LedgerErrors.InvalidTransition, () => ledger.SelectTaskCandidateAsync(Identity, "two", selection.Id, 2, "2"));
        Check((await ledger.ReadConversationAsync(Identity)).Authorizations.IsEmpty);
    }
    private static async Task OldNotification()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var notification = await ledger.EnqueueNotificationAsync(Identity, "notice", "old facts", A);
        await ledger.BeginOutboxSendAsync(Identity, notification.Outbox[0].Id);
        await Input(ledger, "reset", "新对话");
        await ledger.BeginNewDialogueAsync(Identity, "reset");
        await ledger.RecordOutboxOutcomeAsync(Identity, notification.Outbox[0].Id, OutboxStage.Accepted, "API accepted only");
        Check((await ledger.ReadConversationContextAsync(Identity)).LastNotification is null);
    }
    private static async Task Restore()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        await ledger.OpenTaskSelectionAsync(Identity, "focus", TaskSelectionPurpose.Inspect, Candidates, TimeSpan.FromMinutes(5));
        var fact = await ledger.EnqueueNotificationAsync(Identity, "B", "B awaiting", B);
        await ledger.RecordPendingApprovalAsync(Identity, fact.Event.Id, "request", B, Guid.NewGuid(), FakeClock.Now.AddMinutes(5), 0);
        var before = await ledger.ReadConversationContextAsync(Identity);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var after = await ledger.ReadConversationContextAsync(Identity);
        Equal(before.Focus, after.Focus);
        Equal(before.LastNotification, after.LastNotification);
        Equal(before.PendingApproval, after.PendingApproval);
        Check(before.PendingSelection!.Candidates.SequenceEqual(after.PendingSelection!.Candidates));
        Check((await ledger.ReadConversationAsync(Identity)).Authorizations.IsEmpty);
    }
    private static async Task Legacy()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        await ledger.DisposeAsync();
        await Rewrite(f, root => root.AsObject().Remove("contexts"));
        ledger = await f.Open();
        Equal(0L, (await ledger.ReadConversationContextAsync(Identity)).Epoch);
        Check((await ledger.ReadConversationContextAsync(Identity)).Focus is null);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Inbox.Length);
    }
    private static async Task Corrupt()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await FocusA(ledger);
        await ledger.DisposeAsync();
        await Rewrite(f, root => root["contexts"]![0]!["focus"]!["sourceMessageId"] = "missing");
        var before = await File.ReadAllBytesAsync(f.Path);
        await Error(LedgerErrors.CorruptStorage, async () => { await using var invalid = await f.Open(); });
        var after = await File.ReadAllBytesAsync(f.Path);
        Check(before.SequenceEqual(after));
    }
    private static async Task ResetFault()
    {
        await using var f = new Fixture();
        var faults = new FaultInjector();
        var ledger = await f.Open(faults);
        await FocusA(ledger);
        await Input(ledger, "reset", "新对话");
        faults.Stage = JournalWriteStage.AfterPayload;
        try { await ledger.BeginNewDialogueAsync(Identity, "reset"); throw new InvalidOperationException("Expected fault."); }
        catch (InjectedFailure) { }
        Equal(A, (await ledger.ReadConversationContextAsync(Identity)).Focus!.Task);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.IsReadOnly);
        Equal(A, state.Context!.Focus!.Task);
        Equal(0L, state.Context.Epoch);
    }
    private static async Task Rewrite(Fixture f, Action<JsonNode> mutate)
    {
        await using var store = new FileJournalStorage(f.Path);
        var latest = (await store.ReadLatestAsync())!;
        var protector = new TestEncryption(f.Key);
        byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{sequence}");
        var plain = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
        var root = JsonNode.Parse(plain)!; mutate(root);
        var modified = Encoding.UTF8.GetBytes(root.ToJsonString());
        await store.AppendAsync(latest.Sequence, protector.Protect(modified, Purpose(latest.Sequence + 1)));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(modified);
    }
    private sealed class MutableClock : IClock { internal DateTimeOffset Now = FakeClock.Now; public DateTimeOffset UtcNow => Now; }
    private sealed class RecordingModel : IToolModel
    {
        internal ToolModelRequest? Request;
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(ToolModelResponse.Reply("Hello")); }
    }
    private sealed class NoTools : IToolExecutor
    {
        public ToolCapability GetCapability(string capability) => new(false);
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No tool call expected.");
    }
    private sealed class NoAuthority : IUserAuthorizationPolicy
    {
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new UserAuthorizationDecision(false));
    }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (LedgerException error) { Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
