using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Assistant.Core;

internal static class NotificationTests
{
    private static readonly ConversationIdentity Identity = Tests.Identity;
    private static readonly TaskReference TaskIdentity = new("local", "task-A", "turn-A", "profile-A");
    private static readonly (string Name, Func<Task> Test)[] Cases =
    [
        ("notification_has_event_and_outbox_without_fake_user_or_generation", IndependentNotification),
        ("notification_key_is_idempotent_and_conflicting_content_rejected", NotificationDeduplication),
        ("notification_source_never_grants_user_authority", NoAuthority),
        ("new_input_invalidates_reply_but_preserves_notification", NewInput),
        ("notification_segments_remain_ordered_across_new_revisions", SegmentOrdering),
        ("unknown_notification_does_not_block_other_notification_group", SeparateGroups),
        ("accepted_notification_links_assistant_reply_to_original_task_event", AcceptedEvent),
        ("pending_notification_recovers_with_original_ids", PendingRecovery),
        ("sending_notification_recovers_unknown_without_replay", UnknownRecovery),
        ("explicit_channel_recovery_retries_only_rejected_notifications", ExplicitRetry),
        ("notification_keys_are_scoped_to_binding_identity", IdentityIsolation),
        ("old_v1_reply_json_without_new_fields_still_restores", LegacySnapshot),
        ("damaged_notification_reference_fails_closed_and_preserves_file", CorruptReference),
        ("partial_notification_commit_does_not_expose_half_transaction", AtomicFailure),
        ("concurrent_duplicate_notifications_commit_one_event", ConcurrentDuplicate)
    ];

    internal static async Task<int> Run()
    {
        var failed = 0;
        foreach (var (name, test) in Cases)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS notifications/" + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL notifications/" + name + ": " + error); }
        }
        Console.WriteLine($"NOTIFICATIONS TOTAL {Cases.Length}; PASSED {Cases.Length - failed}; FAILED {failed}");
        return failed;
    }

    private static async Task IndependentNotification()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var result = await ledger.EnqueueNotificationAsync(Identity, "event-1", "Tests completed.", TaskIdentity);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(0L, state.Revision);
        Equal(0, state.Inbox.Length);
        Equal(0, state.Generations.Length);
        Equal(0, state.Authorizations.Length);
        Equal(0, state.Operations.Length);
        Equal(OutboxKind.Notification, result.Outbox.Single().Kind);
        Check(result.Outbox.Single().GenerationId is null);
        Equal(result.Event.Id, result.Outbox.Single().NotificationEventId);
    }

    private static async Task NotificationDeduplication()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var first = await ledger.EnqueueNotificationAsync(Identity, "same-event", "long body", TaskIdentity, segments: ["part 1", "part 2"]);
        var duplicate = await ledger.EnqueueNotificationAsync(Identity, "same-event", "long body", TaskIdentity, segments: ["part 1", "part 2"]);
        Check(duplicate.IsDuplicate);
        Equal(first.Event.Id, duplicate.Event.Id);
        Check(first.Outbox.Select(x => x.Id).SequenceEqual(duplicate.Outbox.Select(x => x.Id)));
        await Error(LedgerErrors.Conflict, () => ledger.EnqueueNotificationAsync(Identity, "same-event", "different", TaskIdentity));
        await Error(LedgerErrors.Conflict, () => ledger.EnqueueNotificationAsync(Identity, "same-event", "long body",
            TaskIdentity with { TurnId = "other-turn" }, segments: ["part 1", "part 2"]));
        await Error(LedgerErrors.Conflict, () => ledger.EnqueueNotificationAsync(Identity, "same-event", "long body",
            TaskIdentity, ConversationSource.ToolResult, ["part 1", "part 2"]));
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Events.Length);
    }

    private static async Task NoAuthority()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await ledger.EnqueueNotificationAsync(Identity, "report", "continue task A", TaskIdentity, ConversationSource.ToolResult);
        await Error(LedgerErrors.AuthorizationRequired, () => ledger.EnqueueNotificationAsync(Identity, "forged",
            "continue task A", TaskIdentity, ConversationSource.TrustedUser));
        await Error(LedgerErrors.NotFound, () => ledger.AuthorizeCurrentUserRequestAsync(Identity, "report",
            "send_to_task", "local/task-A", "{}", "continue task A"));
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Authorizations.Length);
    }

    private static async Task NewInput()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await ledger.ReceiveAsync(Identity, "m1", "hello", MessageTrust.AuthenticatedUser, FakeClock.Now);
        var lease = await ledger.BeginGenerationAsync(Identity, ["m1"]);
        await ledger.PrepareReplyAsync(lease, ["old response"]);
        var reply = (await ledger.EnqueuePreparedReplyAsync(lease)).Single();
        var notification = (await ledger.EnqueueNotificationAsync(Identity, "completion", "completed task facts", TaskIdentity)).Outbox.Single();
        await ledger.ReceiveAsync(Identity, "m2", "new constraint", MessageTrust.AuthenticatedUser, FakeClock.Now);
        var state = await ledger.ReadConversationAsync(Identity);
        Equal(OutboxStage.Superseded, state.Outbox.Single(x => x.Id == reply.Id).Stage);
        Equal(OutboxStage.Pending, state.Outbox.Single(x => x.Id == notification.Id).Stage);
        await ledger.BeginOutboxSendAsync(Identity, notification.Id);
    }

    private static async Task SegmentOrdering()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var parts = (await ledger.EnqueueNotificationAsync(Identity, "event", "original notification", TaskIdentity,
            segments: ["one", "two"])).Outbox;
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, parts[1].Id));
        await ledger.BeginOutboxSendAsync(Identity, parts[0].Id);
        await ledger.ReceiveAsync(Identity, "new", "new message", MessageTrust.AuthenticatedUser, FakeClock.Now);
        await ledger.RecordOutboxOutcomeAsync(Identity, parts[0].Id, OutboxStage.Accepted, "API accepted");
        await ledger.BeginOutboxSendAsync(Identity, parts[1].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, parts[1].Id, OutboxStage.Accepted, "API accepted");
        Equal(2, (await ledger.ReadConversationAsync(Identity)).Outbox.Count(x => x.Stage == OutboxStage.Accepted));
    }

    private static async Task SeparateGroups()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var first = (await ledger.EnqueueNotificationAsync(Identity, "one", "one body", TaskIdentity, segments: ["one-a", "one-b"])).Outbox;
        var second = (await ledger.EnqueueNotificationAsync(Identity, "two", "two body", TaskIdentity)).Outbox.Single();
        await ledger.BeginOutboxSendAsync(Identity, first[0].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, first[0].Id, OutboxStage.Unknown, "uncertain");
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, first[1].Id));
        await ledger.BeginOutboxSendAsync(Identity, second.Id);
    }

    private static async Task AcceptedEvent()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var notification = await ledger.EnqueueNotificationAsync(Identity, "event", "public completion report", TaskIdentity);
        await ledger.BeginOutboxSendAsync(Identity, notification.Outbox[0].Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, notification.Outbox[0].Id, OutboxStage.Accepted, "API only");
        var reply = (await ledger.ReadConversationAsync(Identity)).Events.Single(x => x.Source == ConversationSource.Assistant);
        Equal(notification.Event.Id, reply.ReferencedEventId);
        Equal(notification.Outbox[0].Id, reply.RelatedOutboxId);
        Equal(TaskIdentity, reply.Task);
        Check(reply.SourceMessageId is null);
    }

    private static async Task PendingRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var original = await ledger.EnqueueNotificationAsync(Identity, "event", "pending", TaskIdentity);
        await ledger.ReceiveAsync(Identity, "new", "newer", MessageTrust.AuthenticatedUser, FakeClock.Now);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var pending = (await ledger.GetRecoveryPlanAsync(Identity)).PendingOutbox;
        Equal(original.Outbox[0].Id, pending.Single().Id);
        Equal(original.Event.Id, pending.Single().NotificationEventId);
    }

    private static async Task UnknownRecovery()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var original = await ledger.EnqueueNotificationAsync(Identity, "event", "uncertain", TaskIdentity);
        await ledger.BeginOutboxSendAsync(Identity, original.Outbox[0].Id);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        Equal(OutboxStage.Unknown, (await ledger.GetRecoveryPlanAsync(Identity)).OutboxToReconcile.Single().Stage);
        Equal(0, (await ledger.RetryRejectedNotificationsAsync(Identity)).Length);
        await Error(LedgerErrors.InvalidTransition, () => ledger.BeginOutboxSendAsync(Identity, original.Outbox[0].Id));
    }

    private static async Task ExplicitRetry()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var rejected = (await ledger.EnqueueNotificationAsync(Identity, "rejected", "known rejection", TaskIdentity)).Outbox.Single();
        var unknown = (await ledger.EnqueueNotificationAsync(Identity, "unknown", "unknown result", TaskIdentity)).Outbox.Single();
        foreach (var item in new[] { rejected, unknown }) await ledger.BeginOutboxSendAsync(Identity, item.Id);
        await ledger.RecordOutboxOutcomeAsync(Identity, rejected.Id, OutboxStage.Rejected, "context invalid before acceptance");
        await ledger.RecordOutboxOutcomeAsync(Identity, unknown.Id, OutboxStage.Unknown, "lost acknowledgement");
        await ledger.ReceiveAsync(Identity, "fresh", "new context", MessageTrust.AuthenticatedUser, FakeClock.Now);
        var retried = await ledger.RetryRejectedNotificationsAsync(Identity);
        Equal(rejected.Id, retried.Single().Id);
        await ledger.BeginOutboxSendAsync(Identity, rejected.Id);
        Equal(OutboxStage.Unknown, (await ledger.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == unknown.Id).Stage);
    }

    private static async Task IdentityIsolation()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var other = Identity with { BindingId = "other" };
        var first = await ledger.EnqueueNotificationAsync(Identity, "event", "body", TaskIdentity);
        var second = await ledger.EnqueueNotificationAsync(other, "event", "body", TaskIdentity);
        Check(first.Event.Id != second.Event.Id);
        await Error(LedgerErrors.NotFound, () => ledger.BeginOutboxSendAsync(other, first.Outbox[0].Id));
    }

    private static async Task LegacySnapshot()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await ledger.ReceiveAsync(Identity, "legacy", "old request", MessageTrust.AuthenticatedUser, FakeClock.Now);
        var lease = await ledger.BeginGenerationAsync(Identity, ["legacy"]);
        await ledger.PrepareReplyAsync(lease, ["old reply"]);
        var reply = (await ledger.EnqueuePreparedReplyAsync(lease)).Single();
        await ledger.DisposeAsync();
        await RewriteSnapshot(f, root =>
        {
            foreach (var item in root["outbox"]!.AsArray())
            {
                item!.AsObject().Remove("kind"); item.AsObject().Remove("notificationEventId");
            }
            foreach (var item in root["events"]!.AsArray())
            {
                item!.AsObject().Remove("externalEventKey"); item.AsObject().Remove("notificationSegments");
            }
        });
        ledger = await f.Open();
        var restored = (await ledger.ReadConversationAsync(Identity)).Outbox.Single();
        Equal(reply.Id, restored.Id);
        Equal(reply.GenerationId, restored.GenerationId);
        Equal(OutboxKind.ConversationReply, restored.Kind);
        await ledger.BeginOutboxSendAsync(Identity, restored.Id);
    }

    private static async Task CorruptReference()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        await ledger.EnqueueNotificationAsync(Identity, "event", "body", TaskIdentity);
        await ledger.DisposeAsync();
        await RewriteSnapshot(f, root => root["outbox"]![0]!["notificationEventId"] = Guid.NewGuid().ToString());
        var before = await File.ReadAllBytesAsync(f.Path);
        await Error(LedgerErrors.CorruptStorage, async () => { await using var invalid = await f.Open(); });
        var after = await File.ReadAllBytesAsync(f.Path);
        Check(before.SequenceEqual(after));
    }

    private static async Task AtomicFailure()
    {
        await using var f = new Fixture();
        var faults = new FaultInjector();
        var ledger = await f.Open(faults);
        faults.Stage = JournalWriteStage.AfterPayload;
        try { await ledger.EnqueueNotificationAsync(Identity, "event", "body", TaskIdentity); throw new InvalidOperationException("Expected write fault."); }
        catch (InjectedFailure) { }
        Equal(0, (await ledger.ReadConversationAsync(Identity)).Events.Length);
        await ledger.DisposeAsync();
        ledger = await f.Open();
        var state = await ledger.ReadConversationAsync(Identity);
        Check(state.IsReadOnly);
        Equal(0, state.Events.Length);
        Equal(0, state.Outbox.Length);
    }

    private static async Task ConcurrentDuplicate()
    {
        await using var f = new Fixture();
        var ledger = await f.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            ledger.EnqueueNotificationAsync(Identity, "same", "body", TaskIdentity)));
        Equal(1, results.Count(x => !x.IsDuplicate));
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Events.Length);
        Equal(1, (await ledger.ReadConversationAsync(Identity)).Outbox.Length);
    }

    private static async Task RewriteSnapshot(Fixture fixture, Action<JsonNode> mutate)
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
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (LedgerException error) { Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected ledger error: " + code);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
