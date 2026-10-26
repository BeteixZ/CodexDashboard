using Assistant.Core;

internal static partial class OutputDependencyTests
{
    private static async Task SpanCatchRevocation()
    {
        await using var f = new F(); var l = await f.Open(); string body = new('文', 2500);
        var original = (await l.EnqueueNotificationAsync(Identity, "span-dependency", body, null,
            outputDependencies: Bound())).Outbox.Single();
        await Input(l, "first-window", true); var batch = await l.PrepareCatchUpAsync(Identity, "first-window");
        Equal(3, batch.Outbox.Length);
        var before = await l.ReadConversationAsync(Identity);
        foreach (var page in batch.Outbox)
            Check(OutputDependencies.Equivalent(Bound(), DurableConversationLedger.ResolveOutputDependencies(before, page)));
        await Outcome(l, batch.Outbox[0], OutboxStage.Accepted);
        await l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id);
        f.Provider.Current = Bound(2); await l.RefreshOutputDependenciesAsync(Identity);
        var changed = await l.ReadConversationAsync(Identity);
        Equal(OutboxStage.Accepted, changed.Outbox.Single(x => x.Id == batch.Outbox[0].Id).Stage);
        Equal(OutboxStage.Sending, changed.Outbox.Single(x => x.Id == batch.Outbox[1].Id).Stage);
        Equal(OutboxStage.Superseded, changed.Outbox.Single(x => x.Id == batch.Outbox[2].Id).Stage);
        await l.RecordOutboxOutcomeAsync(Identity, batch.Outbox[1].Id, OutboxStage.Unknown, "no peer receipt");
        await l.DisposeAsync(); l = await f.Open();
        await Input(l, "after-change", true); Equal(0, (await l.PrepareCatchUpAsync(Identity, "after-change")).Outbox.Length);
        var progress = (await l.GetCatchUpProgressAsync(Identity)).Single();
        Equal(batch.Batch!.Members[0].Sources.Sum(x => x.Length!.Value), progress.AcceptedCharacters);
        Check(progress.HasUnknown && progress.RemainingCharacters > 0); Equal(0, progress.PendingCharacters);
        var restored = await l.ReadConversationAsync(Identity);
        Equal(body, restored.Outbox.Single(x => x.Id == original.Id).Body);
        foreach (var page in batch.Outbox)
            Check(OutputDependencies.Equivalent(Bound(), DurableConversationLedger.ResolveOutputDependencies(restored,
                restored.Outbox.Single(x => x.Id == page.Id))));
    }

    private static async Task SpanCatchRestartExpiry()
    {
        await using var f = new F(); f.Provider.Current = Bound(expiry: f.Clock.UtcNow.AddMinutes(1));
        var l = await f.Open();
        await l.EnqueueNotificationAsync(Identity, "span-expiry", new string('文', 1400), null, outputDependencies: f.Provider.Current);
        await Input(l, "window", true); var batch = await l.PrepareCatchUpAsync(Identity, "window");
        Equal(2, batch.Outbox.Length); await Outcome(l, batch.Outbox[0], OutboxStage.Accepted);
        int accepted = (await l.GetCatchUpProgressAsync(Identity)).Single().AcceptedCharacters;
        await l.DisposeAsync(); l = await f.Open(validator: false);
        await Error(LedgerErrors.DependencyUnavailable, () => l.BeginOutboxSendAsync(Identity, batch.Outbox[1].Id));
        Equal(OutboxStage.Pending, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == batch.Outbox[1].Id).Stage);
        await l.DisposeAsync(); f.Clock.UtcNow += TimeSpan.FromMinutes(1); f.Provider.Forced = OutputDependencyStatus.Valid;
        l = await f.Open(); await l.RefreshOutputDependenciesAsync(Identity);
        Equal(OutboxStage.Superseded, (await l.ReadConversationAsync(Identity)).Outbox.Single(x => x.Id == batch.Outbox[1].Id).Stage);
        await Input(l, "expired-window", true); Equal(0, (await l.PrepareCatchUpAsync(Identity, "expired-window")).Outbox.Length);
        var progress = (await l.GetCatchUpProgressAsync(Identity)).Single();
        Equal(accepted, progress.AcceptedCharacters); Equal(1400 - accepted, progress.RemainingCharacters);
    }
}
