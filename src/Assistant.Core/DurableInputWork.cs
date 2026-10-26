using System.Collections.Immutable;

namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    /// <summary>Claim previously unprocessed independent inputs after current input grouping is settled.</summary>
    public Task<ImmutableArray<InputWorkItem>> QueueUnprocessedInputsAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default) => QueueInputsCoreAsync(identity, false, cancellationToken);

    /// <summary>Bounded worker admission: claim only free capacity, retaining the remaining original inbox.</summary>
    public Task<ImmutableArray<InputWorkItem>> QueueAvailableInputWorkAsync(ConversationIdentity identity,
        CancellationToken cancellationToken = default) => QueueInputsCoreAsync(identity, true, cancellationToken);

    private Task<ImmutableArray<InputWorkItem>> QueueInputsCoreAsync(ConversationIdentity identity,
        bool availableCapacityOnly, CancellationToken cancellationToken) => ChangeAsync(state =>
    {
        ValidateIdentity(identity);
        var context = ContextFor(state, identity);
        var pending = state.InputWork.Count(x => x.Identity == identity && IsActiveInputWork(x.Stage));
        var result = ImmutableArray.CreateBuilder<InputWorkItem>();
        foreach (var source in state.Inbox.Where(x => x.Identity == identity && x.Revision < Revision(state, identity)
            && x.Revision >= context.StartRevision
            && x.Stage is InboxStage.Received or InboxStage.Grouped or InboxStage.Superseded or InboxStage.HandedToOutbox).OrderBy(x => x.Revision))
        {
            if (state.InputWork.Any(x => x.Identity == identity && x.SourceMessageId == source.SourceMessageId)) continue;
            // Registered operations already have their own durable work record, including cancelled
            // and unknown outcomes. Do not turn those submissions into a second input task.
            if (state.Operations.Any(x => x.Identity == identity && x.SourceMessageId == source.SourceMessageId)) continue;
            // A message already covered by an accepted/uncertain reply is never automatically answered again.
            var covered = state.Generations.Where(x => x.Identity == identity && x.InboxIds.Contains(source.Id)).ToArray();
            if (covered.Any(x => x.InputWorkId is null && x.Revision == Revision(state, identity)
                && x.Stage is GenerationStage.Processing or GenerationStage.Prepared or GenerationStage.HandedToOutbox)) continue;
            var generations = covered.Select(x => x.Id).ToHashSet();
            if (state.Outbox.Any(x => x.GenerationId is { } id && generations.Contains(id)
                && (x.ChannelReceipt is not null || x.Stage is OutboxStage.Sending or OutboxStage.Accepted or OutboxStage.Unknown))) continue;
            if (pending >= _options.MaximumPendingInputWork)
            {
                if (availableCapacityOnly) break;
                throw Error(LedgerErrors.CapacityExceeded, "Pending input work is full; original inbox content is retained.");
            }
            pending++;
            var now = _clock.UtcNow;
            var item = new InputWorkItem(Guid.NewGuid(), identity, source.SourceMessageId, source.Revision,
                context.Epoch, 0, InputWorkStage.Queued, now, now);
            state.InputWork.Add(item);
            result.Add(item);
        }
        return new Mutation<ImmutableArray<InputWorkItem>>(result.ToImmutable(), result.Count != 0);
    }, cancellationToken);

    public Task<GenerationLease> BeginReadOnlyWorkAsync(ConversationIdentity identity, Guid workId,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        var work = RequireInputWork(state, identity, workId);
        var context = ContextFor(state, identity);
        var source = RequireInbox(state, identity, work.SourceMessageId);
        if (work.Stage != InputWorkStage.Queued || work.DialogueEpoch != context.Epoch || source.Revision < context.StartRevision)
            throw Error(LedgerErrors.InvalidTransition, "Only queued work from this dialogue can be read.");
        if (state.InputWork.Any(x => x.Identity == identity && x.Stage == InputWorkStage.Reading)
            || state.InputWork.Any(x => x.Identity == identity && x.Stage == InputWorkStage.Queued && x.SourceRevision < work.SourceRevision))
            throw Error(LedgerErrors.Conflict, "Read-only input work must begin in FIFO order.");
        var generation = new GenerationRecord(Guid.NewGuid(), identity, source.Revision, [source.Id],
            GenerationStage.Processing, _clock.UtcNow, [], source.Task, work.Id, checked(work.Version + 1), Revision(state, identity));
        state.Generations.Add(generation);
        Replace(state.Inbox, source, source with { Stage = InboxStage.Processing, GenerationId = generation.Id });
        Replace(state.InputWork, work, work with
        {
            Stage = InputWorkStage.Reading, Version = generation.WorkVersion, GenerationId = generation.Id,
            ReadAtRevision = Revision(state, identity), UpdatedAt = _clock.UtcNow, Reason = null
        });
        return Changed(new GenerationLease(generation.Id, identity, source.Revision));
    }, cancellationToken);

    public Task MarkReadOnlyWorkNeedsConfirmationAsync(GenerationLease lease, string reason,
        CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateBody(reason);
        var generation = RequireGeneration(state, lease);
        var work = RequireInputWork(state, lease.Identity, generation.InputWorkId
            ?? throw Error(LedgerErrors.InvalidTransition, "This is not deferred input work."));
        if (work.RequiresConfirmation) return Unchanged(false);
        Replace(state.InputWork, work, work with { RequiresConfirmation = true, Reason = reason, UpdatedAt = _clock.UtcNow });
        return Changed(true);
    }, cancellationToken);

    /// <summary>Conclude unavailable/failed reads without repeatedly exhausting the model on restart.</summary>
    public Task FinishReadOnlyWorkAsync(GenerationLease lease, InputWorkStage outcome,
        string reason, CancellationToken cancellationToken = default) => ChangeAsync(state =>
    {
        ValidateBody(reason);
        if (outcome is not (InputWorkStage.Failed or InputWorkStage.NeedsConfirmation))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        var generation = state.Generations.SingleOrDefault(x => x.Identity == lease.Identity && x.Id == lease.Id
            && x.Revision == lease.Revision && x.InputWorkId is not null)
            ?? throw Error(LedgerErrors.NotFound, "Deferred generation was not found.");
        var work = RequireInputWork(state, lease.Identity, generation.InputWorkId!.Value);
        if (work.GenerationId != lease.Id || work.ReadAtRevision != Revision(state, lease.Identity)
            || work.DialogueEpoch != ContextFor(state, lease.Identity).Epoch) return Unchanged(false);
        if (work.Stage is not (InputWorkStage.Queued or InputWorkStage.Reading))
            return Unchanged(false);
        Replace(state.InputWork, work, work with
        {
            Stage = work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : outcome,
            RequiresConfirmation = work.RequiresConfirmation || outcome == InputWorkStage.NeedsConfirmation,
            Reason = reason, UpdatedAt = _clock.UtcNow
        });
        return Changed(true);
    }, cancellationToken);

    /// <summary>
    /// A host may use this only for a registered read-only tool. This never validates mutation authority.
    /// All identities, original source, invocation and the latest constraint revision must match.
    /// </summary>
    public async Task<bool> VerifyReadOnlyWorkExecutionAsync(ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReadOnly || context.InputWorkId is not { } id || context.OperationId is not null
                || context.IdempotencyKey is not null) return false;
            var work = _state.InputWork.SingleOrDefault(x => x.Identity == context.Identity && x.Id == id);
            var invocation = _state.ToolInvocations.SingleOrDefault(x => x.Identity == context.Identity && x.Id == context.InvocationId);
            return work is not null && WorkCurrent(_state, work) && work.Stage == InputWorkStage.Reading
                && work.SourceMessageId == context.SourceMessageId && work.SourceRevision == context.Revision
                && work.ReadAtRevision == context.ReadAtRevision && invocation is not null
                && invocation.GenerationId == work.GenerationId && !invocation.HasSideEffects
                && invocation.Stage == ToolInvocationStage.Executing && invocation.OperationId is null
                && invocation.SourceMessageId == work.SourceMessageId && invocation.Revision == work.SourceRevision;
        }
        finally { _gate.Release(); }
    }

    private static InputWorkItem RequireInputWork(LedgerState state, ConversationIdentity identity, Guid id) =>
        state.InputWork.SingleOrDefault(x => x.Identity == identity && x.Id == id)
        ?? throw Error(LedgerErrors.NotFound, "Input work does not belong to this conversation.");
    private static bool IsActiveInputWork(InputWorkStage stage) => stage is
        InputWorkStage.Queued or InputWorkStage.Reading or InputWorkStage.Prepared or InputWorkStage.ReplyPending;
    private static bool WorkCurrent(LedgerState state, InputWorkItem work) =>
        work.DialogueEpoch == ContextFor(state, work.Identity).Epoch
        && work.SourceRevision >= ContextFor(state, work.Identity).StartRevision
        && work.ReadAtRevision == Revision(state, work.Identity)
        && work.Stage is InputWorkStage.Reading or InputWorkStage.Prepared or InputWorkStage.ReplyPending
            or InputWorkStage.NeedsConfirmation or InputWorkStage.Completed;
    private static void RequireOutboxCurrent(LedgerState state, OutboxMessage message)
    {
        if (message.Kind == OutboxKind.ConversationReply) RequireCurrent(state, message.Identity, message.Revision);
        else if (message.Kind == OutboxKind.DeferredReply)
        {
            var work = RequireInputWork(state, message.Identity, message.InputWorkId!.Value);
            if (!WorkCurrent(state, work) || message.GenerationId != work.GenerationId || message.ReadAtRevision != work.ReadAtRevision)
                throw Error(LedgerErrors.StaleRevision, "Deferred reply was invalidated by newer constraints.");
        }
    }
    private static bool OutboxCurrent(ConversationSnapshot snapshot, OutboxMessage message) =>
        message.Kind == OutboxKind.Notification || message.Kind == OutboxKind.ConversationReply && message.Revision == snapshot.Revision
        || message.Kind == OutboxKind.DeferredReply && snapshot.InputWork.Any(x => x.Id == message.InputWorkId
            && x.GenerationId == message.GenerationId && x.ReadAtRevision == snapshot.Revision
            && x.DialogueEpoch == snapshot.Context!.Epoch && x.Stage is InputWorkStage.ReplyPending or InputWorkStage.NeedsConfirmation);

    private static void InvalidateInputWork(LedgerState state, ConversationIdentity identity, long revision, bool reset = false)
    {
        foreach (var work in state.InputWork.Where(x => x.Identity == identity
            && (reset || x.ReadAtRevision < revision) && x.Stage is not (InputWorkStage.Cancelled or InputWorkStage.Completed or InputWorkStage.Failed
                or InputWorkStage.Superseded or InputWorkStage.Unknown)).ToArray())
        {
            var messages = state.Outbox.Where(x => x.InputWorkId == work.Id).ToArray();
            var uncertain = messages.Any(x => x.Stage is OutboxStage.Sending or OutboxStage.Unknown);
            var accepted = messages.Any(x => x.Stage == OutboxStage.Accepted);
            foreach (var pending in messages.Where(x => x.Stage is OutboxStage.Pending or OutboxStage.Rejected))
                Replace(state.Outbox, pending, pending with { Stage = OutboxStage.Superseded });
            // Once any part could be visible, preserve the history without generating a replacement.
            var stage = reset ? InputWorkStage.Cancelled : uncertain ? InputWorkStage.Unknown
                : accepted || work.HasSendAttempt ? work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : InputWorkStage.Superseded
                : work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : InputWorkStage.Queued;
            Replace(state.InputWork, work, work with { Stage = stage });
        }
    }

    private static void RecoverInputWorkAttempt(LedgerState state, GenerationRecord generation, DateTimeOffset now)
    {
        if (generation.InputWorkId is not { } workId) return;
        var work = RequireInputWork(state, generation.Identity, workId);
        if (work.GenerationId == generation.Id && work.Stage == InputWorkStage.Reading)
            Replace(state.InputWork, work, work with
            {
                Stage = work.RequiresConfirmation ? InputWorkStage.NeedsConfirmation : InputWorkStage.Queued,
                UpdatedAt = now
            });
    }

    private static void ValidateInputWork(LedgerState state)
    {
        if (state.InputWork.Select(x => (x.Identity, x.SourceMessageId)).Distinct().Count() != state.InputWork.Count
            || state.InputWork.Select(x => x.Id).Distinct().Count() != state.InputWork.Count)
            throw Error(LedgerErrors.CorruptStorage, "Duplicate input work identity.");
        foreach (var work in state.InputWork)
        {
            var source = RequireInbox(state, work.Identity, work.SourceMessageId);
            if (work.Id == Guid.Empty || work.SourceRevision != source.Revision || !Enum.IsDefined(work.Stage)
                || work.Version < 0 || work.DialogueEpoch < 0 || work.DialogueEpoch > ContextFor(state, work.Identity).Epoch
                || work.ReadAtRevision is { } read && (read < work.SourceRevision || read > Revision(state, work.Identity))
                || work.Stage == InputWorkStage.Reading && work.GenerationId is null)
                throw Error(LedgerErrors.CorruptStorage, "Invalid input work scope.");
            if (work.GenerationId is { } generationId && !state.Generations.Any(x => x.Id == generationId
                && x.Identity == work.Identity && x.InputWorkId == work.Id && x.WorkVersion == work.Version
                && x.ReadAtRevision == work.ReadAtRevision && x.Revision == work.SourceRevision && x.InboxIds.SequenceEqual(new[] { source.Id })))
                throw Error(LedgerErrors.CorruptStorage, "Invalid input work generation.");
        }
        foreach (var generation in state.Generations.Where(x => x.InputWorkId is not null))
        {
            var work = RequireInputWork(state, generation.Identity, generation.InputWorkId!.Value);
            if (generation.WorkVersion < 1 || generation.WorkVersion > work.Version || generation.Revision != work.SourceRevision
                || generation.ReadAtRevision is not { } read || read < work.SourceRevision || read > Revision(state, work.Identity)
                || !generation.InboxIds.SequenceEqual(new[] { RequireInbox(state, work.Identity, work.SourceMessageId).Id }))
                throw Error(LedgerErrors.CorruptStorage, "Invalid deferred generation provenance.");
        }
        if (state.Generations.Any(x => x.InputWorkId is null && (x.WorkVersion != 0 || x.ReadAtRevision is not null)))
            throw Error(LedgerErrors.CorruptStorage, "Ordinary generation cannot claim deferred work.");
        foreach (var message in state.Outbox.Where(x => x.InputWorkId is not null
            && x.Stage is OutboxStage.Sending or OutboxStage.Accepted or OutboxStage.Unknown))
            if (!RequireInputWork(state, message.Identity, message.InputWorkId!.Value).HasSendAttempt)
                throw Error(LedgerErrors.CorruptStorage, "Deferred send attempt is missing from its work record.");
    }
}
