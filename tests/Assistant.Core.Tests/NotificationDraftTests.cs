using System.Text;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static class NotificationDraftTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly TaskReference Target = new("local", "thread", "turn", "profile");
    private const string Payload = """{"task":"public title","status":"completed","report":"test facts"}""";
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("capture_persists_facts_without_authority_or_send", Capture),
        ("capture_before_model_survives_restart", CaptureRecovery),
        ("preparation_claim_is_exclusive_and_durable", ExclusiveClaim),
        ("restart_recovers_preparing_and_preserves_budget", PreparationRecovery),
        ("stale_claim_cannot_release_or_finalize_new_claim", StaleClaim),
        ("same_key_duplicates_preserve_original_payload_and_event", Duplicates),
        ("concurrent_capture_commits_exactly_one_draft", ConcurrentCapture),
        ("finalize_atomically_preserves_context_and_actual_segments", Finalize),
        ("finalize_same_claim_is_idempotent_without_new_outbox", FinalizeIdempotence),
        ("finalized_pending_or_unknown_never_reenters_preparation", FinalizedRecovery),
        ("exhausted_budget_allows_factual_fallback_only", ExhaustionFallback),
        ("fallback_cannot_race_an_active_claim", FallbackClaim),
        ("input_revision_does_not_invalidate_preparing_notification", RevisionIndependent),
        ("identity_mismatch_cannot_prepare_or_finalize", IdentityScope),
        ("payload_rejects_size_nonobject_duplicate_depth_comments_trailing", PayloadValidation),
        ("pending_capacity_preserves_existing_and_duplicate_can_read", Capacity),
        ("capture_partial_commit_has_no_half_event", CaptureFault),
        ("finalize_partial_commit_has_no_half_outbox", FinalizeFault),
        ("finalize_after_flush_uncertainty_recovers_existing_outbox", FinalizeAfterFlush),
        ("cancelled_claim_does_not_consume_budget", CancelledClaim),
        ("legacy_v1_missing_drafts_recovers_without_loss", Legacy),
        ("damaged_draft_association_payload_state_fail_closed", CorruptDraft),
        ("old_final_notification_key_cannot_be_recaptured", FinalKeyConflict)
    ];

    internal static async Task<int> Run()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS notification-drafts/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL notification-drafts/" + name + ": " + error); }
        }
        Console.WriteLine($"NOTIFICATION DRAFTS TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    private static Task<NotificationCaptureResult> CaptureOne(DurableConversationLedger ledger, string key = "event",
        int maximumAttempts = 3) => ledger.CaptureNotificationAsync(Identity, key, "original task facts", Payload,
            Target, maximumPreparationAttempts: maximumAttempts);

    private static async Task Capture()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var captured = await CaptureOne(ledger);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(NotificationDraftStage.Captured, captured.Draft.Stage);
        Equal(captured.Draft.EventId, captured.Event.Id);
        Equal(0, state.Inbox.Length + state.Generations.Length + state.Authorizations.Length + state.Operations.Length + state.Outbox.Length);
        Equal(0L, state.Revision);
        Equal(1, state.Events.Length);
        Check(captured.Event.NotificationSegments is null);
        await Error(LedgerErrors.NotFound, () => ledger.AuthorizeCurrentUserRequestAsync(Identity, "event", "send_to_task", "target", "{}", "facts"));
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.CaptureNotificationAsync(Identity,
            "forged", "do this", Payload, Target, ConversationSource.TrustedUser));
    }

    private static async Task CaptureRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var original = await CaptureOne(ledger);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(original.Draft, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Single());
        Equal(original.Draft, (await ledger.GetRecoveryPlanAsync(Identity)).PendingNotificationDrafts.Single());
    }

    private static async Task ExclusiveClaim()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        async Task<bool> Attempt()
        {
            try { await ledger.BeginNotificationPreparationAsync(Identity, draft.Id); return true; }
            catch (LedgerException error) when (error.Code == LedgerErrors.InvalidTransition) { return false; }
        }
        Equal(1, (await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Attempt()))).Count(x => x));
        var saved = (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Single();
        Equal(1, saved.PreparationAttempts);
        Equal(NotificationDraftStage.Preparing, saved.Stage);
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
    }

    private static async Task PreparationRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger, maximumAttempts: 2)).Draft;
        var previous = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var recovered = (await ledger.GetPendingNotificationDraftsAsync(Identity)).Single();
        Equal(1, recovered.PreparationAttempts);
        Check(recovered.PreparationId is null);
        await Error(LedgerErrors.InvalidTransition, () => ledger.FinalizeNotificationAsync(previous, "stale summary"));
        var current = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        Equal(2, current.Attempt);
        Check(previous.ClaimId != current.ClaimId);
        await ledger.EndNotificationPreparationAsync(current);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        await Error(LedgerErrors.PreparationBudgetExceeded, () => ledger.BeginNotificationPreparationAsync(Identity, draft.Id));
    }

    private static async Task StaleClaim()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        var first = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await ledger.EndNotificationPreparationAsync(first);
        var second = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await Error(LedgerErrors.InvalidTransition, () => ledger.EndNotificationPreparationAsync(first));
        await Error(LedgerErrors.InvalidTransition, () => ledger.FinalizeNotificationAsync(first, "late old text"));
        await ledger.FinalizeNotificationAsync(second, "current text");
    }

    private static async Task Duplicates()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var first = await CaptureOne(ledger);
        var copy = await CaptureOne(ledger);
        Check(copy.IsDuplicate);
        Equal(first.Draft, copy.Draft);
        await Error(LedgerErrors.Conflict, () => ledger.CaptureNotificationAsync(Identity, "event", "changed", Payload, Target));
        await Error(LedgerErrors.Conflict, () => ledger.CaptureNotificationAsync(Identity, "event", "original task facts", "{}", Target));
        await Error(LedgerErrors.Conflict, () => ledger.CaptureNotificationAsync(Identity, "event", "original task facts", Payload, Target with { TurnId = "other" }));
        await Error(LedgerErrors.Conflict, () => CaptureOne(ledger, maximumAttempts: 4));
        Equal(first.Draft, (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Single());
    }

    private static async Task ConcurrentCapture()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => CaptureOne(ledger)));
        Equal(1, results.Count(x => !x.IsDuplicate));
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Events.Length);
        Equal(1, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
    }

    private static async Task Finalize()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var captured = await CaptureOne(ledger);
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, captured.Draft.Id);
        var result = await ledger.FinalizeNotificationAsync(claim, "model wording", ["actual first", "actual second"]);
        Equal("original task facts", result.Event.Body);
        Check(result.Outbox.Select(x => x.Body).SequenceEqual(new[] { "actual first", "actual second" }));
        var state = await ledger.ReadConversationAsync(Identity);
        Equal("model wording", state.NotificationDrafts.Single().PreparedBody);
        Equal(NotificationDraftStage.Finalized, state.NotificationDrafts.Single().Stage);
        Equal(0, state.Inbox.Length + state.Generations.Length + state.Authorizations.Length + state.Operations.Length);
        await ledger.BeginOutboxSendAsync(Identity, result.Outbox[0].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, result.Outbox[0].Id, OutboxStage.Accepted, "API accepted");
        Equal(captured.Event.Id, (await ledger.ReadConversationAsync(Identity)).Events.Single(x => x.Source == ConversationSource.Assistant).ReferencedEventId);
    }

    private static async Task FinalizeIdempotence()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        var first = await ledger.FinalizeNotificationAsync(claim, "prepared", ["one", "two"]);
        var copy = await ledger.FinalizeNotificationAsync(claim, "prepared", ["one", "two"]);
        Check(copy.IsDuplicate);
        Check(first.Outbox.SequenceEqual(copy.Outbox));
        await Error(LedgerErrors.Conflict, () => ledger.FinalizeNotificationAsync(claim, "changed", ["one", "two"]));
        await Error(LedgerErrors.Conflict, () => ledger.FinalizeNotificationAsync(claim, "prepared", ["other"]));
        await ledger.EndNotificationPreparationAsync(claim); // harmless finally after a successful finalization
        Equal(2, (await ledger.ReadConversationAsync(Identity)).Outbox.Length);
    }

    private static async Task FinalizedRecovery()
    {
        foreach (var beginSend in new[] { false, true })
        {
            await using var f = new Fixture();
            var ledger = await f.Open();
            var draft = (await CaptureOne(ledger)).Draft;
            var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
            var original = await ledger.FinalizeNotificationAsync(claim, "prepared");
            if (beginSend) await ledger.BeginOutboxSendAsync(Identity, original.Outbox[0].Id);
            await ledger.DisposeAsync();
            ledger = await f.Open();
            Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
            await Error(LedgerErrors.InvalidTransition, () => ledger.BeginNotificationPreparationAsync(Identity, draft.Id));
            var copy = await ledger.FinalizeNotificationAsync(claim, "prepared");
            Check(copy.IsDuplicate);
            Equal(original.Outbox[0].Id, copy.Outbox[0].Id);
            Equal(beginSend ? OutboxStage.Unknown : OutboxStage.Pending, copy.Outbox[0].Stage);
        }
    }

    private static async Task ExhaustionFallback()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger, maximumAttempts: 1)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await ledger.EndNotificationPreparationAsync(claim);
        await Error(LedgerErrors.PreparationBudgetExceeded, () => ledger.BeginNotificationPreparationAsync(Identity, draft.Id));
        var fallback = await ledger.FinalizeNotificationFallbackAsync(Identity, draft.Id, "Facts: tests completed.");
        Equal(1, fallback.Outbox.Length);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Single().PreparationAttempts);
        Check((await ledger.FinalizeNotificationFallbackAsync(Identity, draft.Id, "Facts: tests completed.")).IsDuplicate);
    }

    private static async Task FallbackClaim()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await Error(LedgerErrors.InvalidTransition, () => ledger.FinalizeNotificationFallbackAsync(Identity, draft.Id, "fallback"));
        await ledger.EndNotificationPreparationAsync(claim);
        await ledger.FinalizeNotificationFallbackAsync(Identity, draft.Id, "fallback");
        await Error(LedgerErrors.InvalidTransition, () => ledger.FinalizeNotificationAsync(claim, "late model"));
    }

    private static async Task RevisionIndependent()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await ledger.ReceiveAsync(Identity, "new", "new request", MessageTrust.AuthenticatedUser, FakeClock.Now);
        var result = await ledger.FinalizeNotificationAsync(claim, "prepared");
        await ledger.BeginOutboxSendAsync(Identity, result.Outbox[0].Id);
        Equal(0L, result.Event.Revision);
    }

    private static async Task IdentityScope()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var other = Identity with { BindingId = "other" };
        var draft = (await CaptureOne(ledger)).Draft;
        await Error(LedgerErrors.NotFound, () => ledger.BeginNotificationPreparationAsync(other, draft.Id));
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        await Error(LedgerErrors.NotFound, () => ledger.FinalizeNotificationAsync(claim with { Identity = other }, "wrong binding"));
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(other)).Length);
    }

    private static async Task PayloadValidation()
    {
        await using var f = new Fixture();
        var ledger = await f.Open(options: new() { MaximumBodyCharacters = 100 });
        var invalid = new[] { "[]", "null", """{"a":1,"a":2}""", """{"nest":[{"a":1,"a":2}]}""",
            """{"a":1,}""", """{/*comment*/"a":1}""", """{"a":1}junk""",
            new string('[', 18) + "0" + new string(']', 18), "{\"x\":\"" + new string('x', 100) + "\"}" };
        foreach (var payload in invalid)
            await Throws<ArgumentException>(() => ledger.CaptureNotificationAsync(Identity, "invalid", "facts", payload, Target));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Events.Length);
        await ledger.CaptureNotificationAsync(Identity, "valid", "facts", """{"x":[{"z":1}],"y":true}""", Target);
    }

    private static async Task Capacity()
    {
        await using var f = new Fixture();
        var ledger = await f.Open(options: new() { MaximumPendingNotificationDrafts = 1 });
        var draft = (await CaptureOne(ledger)).Draft;
        Check((await CaptureOne(ledger)).IsDuplicate);
        await Error(LedgerErrors.CapacityExceeded, () => CaptureOne(ledger, "other"));
        await Error(LedgerErrors.CapacityExceeded, () => ledger.CaptureNotificationAsync(
            Identity with { BindingId = "other" }, "event", "facts", "{}", Target));
        await ledger.FinalizeNotificationFallbackAsync(Identity, draft.Id, "facts");
        await CaptureOne(ledger, "other");
        Equal(2, (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Length);
    }

    private static async Task CaptureFault()
    {
        await using var f = new Fixture();
        var faults = new FaultInjector();
        var ledger = await f.Open(faults);
        faults.Stage = JournalWriteStage.AfterPayload;
        await Throws<InjectedFailure>(() => CaptureOne(ledger));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Events.Length);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Check((await ledger.ReadConversationAsync(Identity)).IsReadOnly);
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
    }

    private static async Task FinalizeFault()
    {
        await using var f = new Fixture();
        var faults = new FaultInjector();
        var ledger = await f.Open(faults);
        var draft = (await CaptureOne(ledger)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        faults.Stage = JournalWriteStage.AfterPayload;
        await Throws<InjectedFailure>(() => ledger.FinalizeNotificationAsync(claim, "prepared"));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Outbox.Length);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.IsReadOnly);
        Equal(NotificationDraftStage.Preparing, state.NotificationDrafts.Single().Stage);
        Equal(0, state.Outbox.Length);
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
    }

    private static async Task FinalizeAfterFlush()
    {
        await using var f = new Fixture();
        var faults = new FaultInjector();
        var ledger = await f.Open(faults);
        var draft = (await CaptureOne(ledger)).Draft;
        var claim = await ledger.BeginNotificationPreparationAsync(Identity, draft.Id);
        faults.Stage = JournalWriteStage.AfterFlush;
        await Throws<InjectedFailure>(() => ledger.FinalizeNotificationAsync(claim, "prepared", ["one", "two"]));
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
        Equal(NotificationDraftStage.Finalized, (await ledger.ReadConversationAsync(Identity)).NotificationDrafts.Single().Stage);
        Equal(2, (await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.Length);
        Check((await ledger.FinalizeNotificationAsync(claim, "prepared", ["one", "two"])).IsDuplicate);
    }

    private static async Task CancelledClaim()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var draft = (await CaptureOne(ledger)).Draft;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Throws<OperationCanceledException>(() => ledger.BeginNotificationPreparationAsync(Identity, draft.Id, cancellation.Token));
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Single().PreparationAttempts);
    }

    private static async Task Legacy()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var original = await ledger.EnqueueNotificationAsync(Identity, "legacy", "body", Target);
        await ledger.DisposeAsync();
        await Rewrite(f, root => root.AsObject().Remove("notificationDrafts"));
        ledger = await f.Open();
        Equal(original.Outbox[0].Id, (await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox.Single().Id);
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
        await CaptureOne(ledger);
    }

    private static async Task CorruptDraft()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["notificationDrafts"]![0]!["eventId"] = Guid.NewGuid().ToString(),
            root => root["notificationDrafts"]![0]!["payloadJson"] = """{"x":1,"x":2}""",
            root => root["notificationDrafts"]![0]!["preparationAttempts"] = 20,
            root => root["notificationDrafts"]![0]!["stage"] = "Finalized",
            root => root["notificationDrafts"]![0]!["contextBody"] = "changed facts"
        ];
        foreach (var mutate in mutations)
        {
            await using var f = new Fixture();
            var ledger = await f.Open();
            await CaptureOne(ledger);
            await ledger.DisposeAsync();
            await Rewrite(f, mutate);
            var before = await File.ReadAllBytesAsync(f.Path);
            await Error(LedgerErrors.CorruptStorage, async () => { await using var invalid = await f.Open(); });
            var after = await File.ReadAllBytesAsync(f.Path);
            Check(before.SequenceEqual(after));
        }
    }

    private static async Task FinalKeyConflict()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await ledger.EnqueueNotificationAsync(Identity, "event", "original task facts", Target);
        await Error(LedgerErrors.Conflict, () => CaptureOne(ledger));
        Equal(0, (await ledger.GetPendingNotificationDraftsAsync(Identity)).Length);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Outbox.Length);
    }

    private static async Task Rewrite(Fixture fixture, Action<JsonNode> mutate)
    {
        await using var storage = new FileJournalStorage(fixture.Path);
        var latest = (await storage.ReadLatestAsync())!;
        var protector = new TestEncryption(fixture.Key);
        byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{sequence}");
        var plain = protector.Unprotect(latest.Payload, Purpose(latest.Sequence));
        var root = JsonNode.Parse(plain)!;
        mutate(root);
        var modified = Encoding.UTF8.GetBytes(root.ToJsonString());
        await storage.AppendAsync(latest.Sequence, protector.Protect(modified, Purpose(latest.Sequence + 1)));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(modified);
    }
    private static async Task Error(string code, Func<Task> action) => Equal(code, (await Throws<LedgerException>(action)).Code);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
