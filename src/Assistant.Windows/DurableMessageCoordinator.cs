using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record DurableFastReply(ImmutableArray<string> Segments, OutputDependencies? Dependencies = null);
internal sealed record DurableFastContext(InboxMessage Message, DurableConversationLedger Ledger, Func<bool> StillCurrent);
internal sealed record DurableDeliveryRequest(OutboxMessage Message, Func<bool> StillCurrent);
internal sealed record DurableDeliveryResult(OutboxStage Stage, string? Receipt = null,
    OutboxRejectionReason RejectionReason = OutboxRejectionReason.None);
internal sealed record DurableCoordinatorError(string Code, DateTimeOffset At, ConversationIdentity Identity, string? SourceMessageId = null);

internal sealed record DurableMessageOptions
{
    internal TimeSpan MergeWindow { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan FastPathTimeout { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal int MaximumGroupedMessages { get; init; } = 32;
    // A host with target/topic knowledge should reject unrelated messages here.
    internal Func<InboxMessage, InboxMessage, bool>? CanGroup { get; init; }
    internal Func<InboxMessage, InboxMessage, bool>? ReplacesPendingReply { get; init; }
    internal Func<ToolLoopResult, ImmutableArray<string>?>? FailureReplyFactory { get; init; }
}

/// <summary>
/// Thread-independent inbox, fast-path, generation and outbox coordination. The owner authenticates
/// channel inputs and owns the ledger lifetime. A delivery delegate performs exactly one send.
/// </summary>
internal sealed class DurableMessageCoordinator : IAsyncDisposable
{
    private readonly DurableConversationLedger ledger;
    private readonly ToolAgentLoop loop;
    private readonly Func<ConversationIdentity?> identityProvider;
    private readonly Func<DurableDeliveryRequest, CancellationToken, Task<DurableDeliveryResult>> delivery;
    private readonly Func<InboxMessage, CancellationToken, Task<DurableFastReply?>>? readonlyFastHandler;
    private readonly Func<DurableFastContext, CancellationToken, Task<DurableFastReply?>>? trustedFastHandler;
    private readonly IReplySegmenter? replySegmenter;
    private readonly IClock clock;
    private readonly DurableMessageOptions options;
    private long mergeWindowTicks;
    private readonly Action<DurableCoordinatorError>? onError;
    private readonly Func<ConversationEvent, bool>? requiredDecisionValidator;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim fastSlots = new(1, 1);
    private readonly SemaphoreSlim deliverySlots = new(1, 1);
    private readonly object externalGate = new();
    private readonly HashSet<Task> externalTasks = new();
    private readonly List<Task> stoppedWorkers = new();
    private Session? session;
    private int externalCalls;
    private int disposed;

    internal DurableMessageCoordinator(DurableConversationLedger ledger, ToolAgentLoop loop,
        Func<ConversationIdentity?> identityProvider,
        Func<DurableDeliveryRequest, CancellationToken, Task<DurableDeliveryResult>> delivery,
        IClock? clock = null,
        Func<InboxMessage, CancellationToken, Task<DurableFastReply?>>? readonlyFastHandler = null,
        Func<DurableFastContext, CancellationToken, Task<DurableFastReply?>>? trustedFastHandler = null,
        IReplySegmenter? replySegmenter = null, DurableMessageOptions? options = null,
        Action<DurableCoordinatorError>? onError = null,
        Func<ConversationEvent, bool>? requiredDecisionValidator = null)
    {
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.loop = loop ?? throw new ArgumentNullException(nameof(loop));
        this.identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        this.clock = clock ?? new SystemClock();
        this.readonlyFastHandler = readonlyFastHandler;
        this.trustedFastHandler = trustedFastHandler;
        this.replySegmenter = replySegmenter;
        this.options = options ?? new();
        this.onError = onError;
        this.requiredDecisionValidator = requiredDecisionValidator;
        if (this.options.MergeWindow < TimeSpan.Zero || this.options.MergeWindow > TimeSpan.FromSeconds(10)
            || this.options.FastPathTimeout <= TimeSpan.Zero || this.options.FastPathTimeout > TimeSpan.FromSeconds(30)
            || this.options.DeliveryTimeout <= TimeSpan.Zero || this.options.DeliveryTimeout > TimeSpan.FromMinutes(2)
            || this.options.DrainTimeout <= TimeSpan.Zero || this.options.DrainTimeout > TimeSpan.FromSeconds(30)
            || this.options.MaximumGroupedMessages is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options));
        mergeWindowTicks = this.options.MergeWindow.Ticks;
    }

    internal void SetMergeWindow(TimeSpan window)
    {
        if (window < TimeSpan.Zero || window > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(window));
        ThrowIfDisposed();
        Interlocked.Exchange(ref mergeWindowTicks, window.Ticks);
    }

    internal async Task ActivateSessionAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (identityProvider() != identity)
                throw new LedgerException(LedgerErrors.IdentityMismatch, "The requested session is not the active channel binding.");
            if (session is { } existing && existing.Identity == identity && !existing.Lifetime.IsCancellationRequested)
                return;
            await StopSessionAsync(cancellationToken).ConfigureAwait(false);
            await ledger.RefreshOutputDependenciesAsync(identity, cancellationToken).ConfigureAwait(false);
            var snapshot = await ledger.ReadConversationAsync(identity, cancellationToken).ConfigureAwait(false);
            if (identityProvider() != identity)
                throw new LedgerException(LedgerErrors.IdentityMismatch, "The binding changed while opening the session.");
            var next = new Session(identity, snapshot.Revision);
            var recovery = await ledger.GetRecoveryPlanAsync(identity, cancellationToken).ConfigureAwait(false);
            await PrepareCatchUpWindowAsync(identity, cancellationToken).ConfigureAwait(false);
            foreach (var prepared in recovery.PreparedReplies)
                if (ledger.ValidateOutputDependencies(identity, prepared.GenerationContext?.Dependencies
                        ?? OutputDependencies.LegacyUnknown).Status == OutputDependencyStatus.Valid)
                    await ledger.EnqueuePreparedReplyAsync(new(prepared.Id, identity, prepared.Revision), cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();
            if (identityProvider() != identity)
                throw new LedgerException(LedgerErrors.IdentityMismatch, "The binding changed during recovery.");
            Volatile.Write(ref session, next);
            next.Workers = [Task.Run(() => FastWorkerAsync(next)), Task.Run(() => NormalWorkerAsync(next)),
                Task.Run(() => DeferredWorkerAsync(next)), Task.Run(() => SendWorkerAsync(next))];
            Signal(next.Fast);
            Signal(next.Send);
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>
    /// Return only after durable acceptance. This does not claim generation, processing or delivery.
    /// Caller must not advance its seen/cursor state when this method fails.
    /// </summary>
    internal async Task<ReceiveResult> AcceptAsync(ConversationIdentity identity, string sourceMessageId,
        string text, DateTimeOffset occurredAt, CancellationToken cancellationToken = default,
        CatchUpInputOptions? catchUp = null, bool hasUnresolvedReference = false, Guid? referencedEventId = null)
    {
        ThrowIfDisposed();
        var active = Volatile.Read(ref session);
        if (active is null || active.Identity != identity || !IsSessionCurrent(active))
            throw new LedgerException(LedgerErrors.IdentityMismatch, "No active session matches this authenticated input.");
        var received = await ledger.ReceiveAsync(identity, sourceMessageId, text,
            MessageTrust.AuthenticatedUser, occurredAt, cancellationToken: cancellationToken, catchUp: catchUp,
            hasUnresolvedReference: hasUnresolvedReference, referencedEventId: referencedEventId).ConfigureAwait(false);
        if (IsSessionCurrent(active))
        {
            active.Advance(received.CurrentRevision);
            await PrepareCatchUpWindowAsync(identity, cancellationToken).ConfigureAwait(false);
            Signal(active.Fast);
            Signal(active.Send);
        }
        return received;
    }

    /// <summary>Stops scheduling and cancels current work. False means an adapter ignored cancellation.</summary>
    internal async Task<bool> DrainSessionAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StopSessionAsync(cancellationToken).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    internal Task<bool> CancelSessionAsync(CancellationToken cancellationToken = default) => DrainSessionAsync(cancellationToken);

    internal async Task<NotificationEnqueueResult> EnqueueNotificationAsync(ConversationIdentity identity,
        string eventKey, string body, TaskReference? task, ConversationSource source = ConversationSource.Notification,
        CancellationToken cancellationToken = default, bool isDefaultTaskNotification = false)
    {
        ThrowIfDisposed();
        var active = Volatile.Read(ref session);
        if (active is null || active.Identity != identity || !IsSessionCurrent(active))
            throw new LedgerException(LedgerErrors.IdentityMismatch, "A notification requires its original active binding.");
        var segments = replySegmenter?.Split([body]) ?? ImmutableArray.Create(body);
        var result = await ledger.EnqueueNotificationAsync(identity, eventKey, body, task, source, segments,
            cancellationToken, isDefaultTaskNotification, outputDependencies: OutputDependencies.Independent).ConfigureAwait(false);
        if (IsSessionCurrent(active)) Signal(active.Send);
        return result;
    }

    internal async Task RetryRejectedNotificationsAsync(ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var active = Volatile.Read(ref session);
        if (active is null || active.Identity != identity || !IsSessionCurrent(active))
            throw new LedgerException(LedgerErrors.IdentityMismatch, "A retry requires the original active binding.");
        await ledger.RetryRejectedNotificationsAsync(identity, cancellationToken).ConfigureAwait(false);
        if (IsSessionCurrent(active)) Signal(active.Send);
    }

    internal async Task<DecisionNotificationResult> EnsureRequiredDecisionNotificationAsync(
        ConversationIdentity identity, string eventKey, TaskReference task, RequiredDecisionNotice decision,
        string body, IReadOnlyList<string> segments, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var active = Volatile.Read(ref session);
        if (active is null || active.Identity != identity || !IsSessionCurrent(active))
            throw new LedgerException(LedgerErrors.IdentityMismatch, "An approval notice requires its original active binding.");
        var result = await ledger.EnsureRequiredDecisionNotificationAsync(identity, eventKey, task,
            decision, body, segments, cancellationToken).ConfigureAwait(false);
        if (IsSessionCurrent(active)) Signal(active.Send);
        return result;
    }

    /// <summary>Wake already-pending sends after a channel becomes available. Never retries Unknown.</summary>
    internal void NotifyDeliveryAvailable(ConversationIdentity identity)
    {
        var active = Volatile.Read(ref session);
        if (active is not null && active.Identity == identity && IsSessionCurrent(active)) Signal(active.Send);
    }

    /// <summary>
    /// No internal timeout. Receiver ownership must remain held until this succeeds after cancelling
    /// the session, even if DrainSessionAsync returned false. It never starts a new external action.
    /// </summary>
    internal async Task WaitForSessionDrainAsync(CancellationToken cancellationToken = default)
    {
        Task[] workers;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { workers = stoppedWorkers.ToArray(); }
        finally { lifecycle.Release(); }
        await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false);
        await loop.WaitForExternalDrainAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            Task[] pending;
            lock (externalGate) pending = externalTasks.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending.Select(x => x.ContinueWith(done => { _ = done.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> StopSessionAsync(CancellationToken cancellationToken)
    {
        var previous = Interlocked.Exchange(ref session, null);
        if (previous is not null)
        {
            previous.Cancel();
            stoppedWorkers.AddRange(previous.Workers);
            try { await Task.WhenAll(previous.Workers).WaitAsync(options.DrainTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { return false; }
        }
        if (Volatile.Read(ref externalCalls) != 0) return false;
        var toolDrain = loop.WaitForExternalDrainAsync(cancellationToken);
        try { await toolDrain.WaitAsync(options.DrainTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return true;
    }

    private async Task FastWorkerAsync(Session active)
    {
        await WorkerAsync(active, active.Fast, async () =>
        {
            var snapshot = await ledger.ReadConversationAsync(active.Identity, active.Lifetime.Token).ConfigureAwait(false);
            var current = CurrentInput(snapshot);
            if (current is null)
            {
                if (!snapshot.Inbox.Any(x => x.Revision == snapshot.Revision && x.Stage is InboxStage.Processing or InboxStage.Prepared))
                    SettleCurrent(active, snapshot.Revision);
                return;
            }
            if (current.Revision <= active.ClassifiedRevision) return;
            active.Advance(snapshot.Revision);
            var token = active.RevisionToken(current.Revision);
            if (!StillCurrent(active, current.Revision)) return;
            active.ClassifiedRevision = current.Revision;
            DurableFastReply? reply = null;
            try
            {
                if (readonlyFastHandler is not null)
                    reply = await ExternalAsync(ct => readonlyFastHandler(current, ct), fastSlots, options.FastPathTimeout, token).ConfigureAwait(false);
                if (reply is null && trustedFastHandler is not null)
                    reply = await ExternalAsync(ct => trustedFastHandler(new(current, ledger, () => StillCurrent(active, current.Revision)), ct),
                        fastSlots, options.FastPathTimeout, token).ConfigureAwait(false);
            }
            catch (AdapterBusyException)
            {
                active.ClassifiedRevision = current.Revision - 1;
                Report("fast_path_busy", active.Identity, current.SourceMessageId);
                return;
            }
            token.ThrowIfCancellationRequested();
            if (reply is null)
            {
                Interlocked.Exchange(ref active.NormalRevision, current.Revision);
                Signal(active.Normal);
                return;
            }
            var lease = await ledger.BeginGenerationAsync(active.Identity, [current.SourceMessageId], current.Task, token,
                outputDependencies: reply.Dependencies ?? OutputDependencies.Independent).ConfigureAwait(false);
            try
            {
                var segments = replySegmenter?.Split(reply.Segments) ?? reply.Segments;
                if (segments.IsDefaultOrEmpty || segments.Any(string.IsNullOrWhiteSpace))
                    throw new LedgerException(LedgerErrors.InvalidTransition, "The fast handler returned an invalid reply.");
                await ledger.PrepareReplyAsync(lease, segments, token).ConfigureAwait(false);
                await ledger.EnqueuePreparedReplyAsync(lease, token).ConfigureAwait(false);
                Signal(active.Send);
                SettleCurrent(active, current.Revision);
            }
            finally { await ledger.EndGenerationAttemptAsync(lease, CancellationToken.None).ConfigureAwait(false); }
        }).ConfigureAwait(false);
    }

    private async Task NormalWorkerAsync(Session active)
    {
        await WorkerAsync(active, active.Normal, async () =>
        {
            var revision = Interlocked.Read(ref active.NormalRevision);
            if (!StillCurrent(active, revision)) return;
            var token = active.RevisionToken(revision);
            // Use one captured value for both the wait and the eligible input
            // interval. Saving settings never stretches an already-started wait.
            var mergeWindow = TimeSpan.FromTicks(Interlocked.Read(ref mergeWindowTicks));
            if (mergeWindow > TimeSpan.Zero) await Task.Delay(mergeWindow, token).ConfigureAwait(false);
            var snapshot = await ledger.ReadConversationAsync(active.Identity, token).ConfigureAwait(false);
            var current = CurrentInput(snapshot);
            if (current is null || current.Revision != revision || !StillCurrent(active, revision)) return;
            var groupedInputs = new List<InboxMessage> { current };
            // Follow adjacent additions, stopping at the first independent or
            // already claimed input. A, B, "add to B" must not absorb A merely
            // because the latest message begins with an addition marker.
            foreach (var earlier in snapshot.Inbox.Where(x => x.Revision < revision).OrderByDescending(x => x.Revision))
            {
                var replacesPending = groupedInputs.Count == 1
                    && options.ReplacesPendingReply?.Invoke(earlier, current) == true
                    && snapshot.Generations.Any(g => g.InboxIds.Contains(earlier.Id) && g.Stage == GenerationStage.Superseded)
                    && !snapshot.Operations.Any(o => o.SourceMessageId == earlier.SourceMessageId)
                    && !snapshot.Outbox.Any(o => snapshot.Generations.Any(g => g.Id == o.GenerationId && g.InboxIds.Contains(earlier.Id)));
                if (groupedInputs.Count >= options.MaximumGroupedMessages
                    || !IsResumable(earlier.Stage)
                    || snapshot.InputWork.Any(work => work.SourceMessageId == earlier.SourceMessageId)
                    || earlier.Revision < (snapshot.Context?.StartRevision ?? 0)
                    || current.ReceivedAt < earlier.ReceivedAt
                    || (!replacesPending && (current.ReceivedAt - earlier.ReceivedAt > mergeWindow
                        || !CanGroup(earlier, groupedInputs[^1], mergeWindow)))) break;
                groupedInputs.Add(earlier);
                if (replacesPending) break;
            }
            var grouped = groupedInputs.OrderBy(x => x.Revision).Select(x => x.SourceMessageId).ToArray();
            if (grouped.Length > 1) await ledger.GroupMessagesAsync(active.Identity, grouped, token).ConfigureAwait(false);
            var result = await loop.RunAsync(active.Identity, current.SourceMessageId, grouped, token).ConfigureAwait(false);
            if (!result.Outbox.IsEmpty) Signal(active.Send);
            if (result.State is ToolLoopState.Failed or ToolLoopState.ModelUnavailable or ToolLoopState.BudgetExceeded)
            {
                Report(result.State.ToString(), active.Identity, current.SourceMessageId);
                var fallback = options.FailureReplyFactory is { } factory
                    ? factory(result)
                    : FailureReply(result);
                if (fallback is { IsDefaultOrEmpty: false } && StillCurrent(active, revision))
                {
                    var lease = await ledger.BeginGenerationAsync(active.Identity, grouped, current.Task, token,
                        outputDependencies: OutputDependencies.Independent).ConfigureAwait(false);
                    try
                    {
                        var segments = replySegmenter?.Split(fallback.Value) ?? fallback.Value;
                        await ledger.PrepareReplyAsync(lease, segments, token).ConfigureAwait(false);
                        await ledger.EnqueuePreparedReplyAsync(lease, token).ConfigureAwait(false);
                        Signal(active.Send);
                    }
                    finally { await ledger.EndGenerationAttemptAsync(lease, CancellationToken.None).ConfigureAwait(false); }
                }
            }
            SettleCurrent(active, revision);
        }).ConfigureAwait(false);
    }
    internal static ImmutableArray<string> FailureReply(ToolLoopResult result)
    {
        foreach (var observation in result.Observations.Reverse())
        {
            if (observation.ToolName != "create_task" || observation.OperationId is not { } operationId) continue;
            try
            {
                using var document = JsonDocument.Parse(observation.ResultJson);
                var root = document.RootElement;
                if (root.GetProperty("status").GetString() != "accepted") continue;
                var facts = root.GetProperty("facts");
                if (facts.GetProperty("operationId").GetGuid() != operationId
                    || facts.GetProperty("newlyBoundTaskEvidence").GetProperty("createOperationId").GetGuid() != operationId) continue;
                var task = facts.GetProperty("observedTask");
                string? thread = task.GetProperty("ThreadId").GetString();
                var bound = facts.GetProperty("newlyBoundTaskEvidence").GetProperty("task");
                if (!Guid.TryParse(thread, out _) || bound.GetProperty("ThreadId").GetString() != thread) continue;
                return [$"任务已创建，编号：{thread}。\n后续回复整理中断，请勿重复创建。可发“工作列表”查看后续状态。"];
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        string text = result.ModelFailure switch
        {
            ToolModelFailureKind.Authorization => "模型登录或授权暂时不可用，请在电脑端助手的设置里重新登录。",
            ToolModelFailureKind.UsageLimit => "模型额度已用完，可以在账户用量页查看恢复时间。",
            ToolModelFailureKind.RateLimited => "请求暂时太频繁了，稍等一会儿再聊。",
            ToolModelFailureKind.InvalidOutput => "这次回复的格式出了问题，没能正常读取。",
            ToolModelFailureKind.Timeout => "这次回复超时了，没能答完。",
            _ => result.State switch
        {
            ToolLoopState.BudgetExceeded => "这次处理步骤较多，还没整理完。可以把问题拆小一点。",
            ToolLoopState.ModelUnavailable => "这次没能收到可用的模型回复，暂时答不了。",
            _ => "这次没能整理好回复。"
        }
        };
        if (result.Observations.Any(observation => observation.OperationId != null))
            text += "已有工作操作记录，请先发“工作列表”核对结果。";
        return [text];
    }

    private async Task DeferredWorkerAsync(Session active)
    {
        await WorkerAsync(active, active.Deferred, async () =>
        {
            while (IsSessionCurrent(active) && Interlocked.Read(ref active.SettledRevision) == active.Revision)
            {
                var token = active.RevisionToken(active.Revision);
                var snapshot = await ledger.ReadConversationAsync(active.Identity, token).ConfigureAwait(false);
                if (snapshot.IsReadOnly || !StillCurrent(active, snapshot.Revision)) return;
                await ledger.QueueAvailableInputWorkAsync(active.Identity, token).ConfigureAwait(false);
                snapshot = await ledger.ReadConversationAsync(active.Identity, token).ConfigureAwait(false);
                var work = snapshot.InputWork.Where(x => x.Stage == InputWorkStage.Queued)
                    .OrderBy(x => x.SourceRevision).FirstOrDefault();
                if (work is null) return;
                var result = await loop.RunReadOnlyWorkAsync(active.Identity, work.Id, token).ConfigureAwait(false);
                if (!result.Outbox.IsEmpty) Signal(active.Send);
                if (result.State is ToolLoopState.Cancelled or ToolLoopState.Superseded) return;
                if (result.State is ToolLoopState.Failed or ToolLoopState.ModelUnavailable or ToolLoopState.BudgetExceeded)
                    Report("deferred_" + result.State, active.Identity, work.SourceMessageId);
            }
        }).ConfigureAwait(false);
    }

    private async Task SendWorkerAsync(Session active)
    {
        await WorkerAsync(active, active.Send, async () =>
        {
            var suppressed = new HashSet<Guid>();
            while (IsSessionCurrent(active))
            {
                await ledger.RefreshOutputDependenciesAsync(active.Identity, active.Lifetime.Token).ConfigureAwait(false);
                await PrepareCatchUpWindowAsync(active.Identity, active.Lifetime.Token).ConfigureAwait(false);
                var snapshot = await ledger.ReadConversationAsync(active.Identity, active.Lifetime.Token).ConfigureAwait(false);
                if (snapshot.IsReadOnly) return;
                active.Advance(snapshot.Revision);
                var policyReady = (await ledger.GetRecoveryPlanAsync(active.Identity, active.Lifetime.Token)
                    .ConfigureAwait(false)).PendingOutbox.Select(x => x.Id).ToHashSet();
                var pending = snapshot.Outbox.Where(x => SendEligible(snapshot, x) && x.Stage == OutboxStage.Pending
                    && policyReady.Contains(x.Id) && !suppressed.Contains(x.Id) && NoticeStillCurrent(active, snapshot, x))
                    .OrderBy(x => DurableConversationLedger.OutboxPriority(snapshot, x)).ThenBy(x => x.CreatedAt).ThenBy(x => x.SegmentIndex)
                    .FirstOrDefault(x => snapshot.Outbox.Where(prior => DurableConversationLedger.SameOutboxGroup(prior, x) && prior.SegmentIndex < x.SegmentIndex)
                        .All(prior => prior.Stage == OutboxStage.Accepted));
                if (pending is null || !DeliveryCurrent(active, pending)) return;
                var outputDependencies = DurableConversationLedger.ResolveOutputDependencies(snapshot, pending);
                if (ledger.ValidateOutputDependencies(active.Identity, outputDependencies).Status != OutputDependencyStatus.Valid)
                {
                    suppressed.Add(pending.Id);
                    continue;
                }
                var token = pending.Kind == OutboxKind.Notification ? active.Lifetime.Token : active.RevisionToken(pending.ReadAtRevision ?? pending.Revision);
                // Reserve before declaring Sending. A still-running old binding must keep this slot.
                if (!await deliverySlots.WaitAsync(0, token).ConfigureAwait(false)) return;
                OutboxMessage sending;
                try { sending = await ledger.BeginOutboxSendAsync(active.Identity, pending.Id, token).ConfigureAwait(false); }
                catch (LedgerException error) when (error.Code is LedgerErrors.NotificationSuppressed or LedgerErrors.BudgetDeferred)
                {
                    deliverySlots.Release();
                    suppressed.Add(pending.Id);
                    continue; // A new subscription/pause can invalidate the pending send.
                }
                catch { deliverySlots.Release(); throw; }
                var attempt = new OutboxAttemptLease(sending.Identity, sending.Id,
                    sending.SendAttempts, sending.AttemptTrustedInputRevision);
                OutboxMessage saved;
                try
                {
                    // Track both the real channel call and its durable result.
                    // Cancelling our wait does not discard a later definite receipt.
                    saved = await ExternalAsync(ct => DeliverAndRecordAsync(new(sending,
                        () => DeliveryCurrent(active, sending) && NoticeStillCurrent(active, snapshot, sending)
                            && ledger.IsDependentNotificationCurrent(sending)
                            && ledger.ValidateOutputDependencies(active.Identity, outputDependencies).Status == OutputDependencyStatus.Valid), attempt, ct),
                        deliverySlots, options.DeliveryTimeout, token, slotReserved: true).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    saved = await ledger.RecordOutboxAttemptOutcomeAsync(attempt, OutboxStage.Unknown,
                        "The channel outcome could not be confirmed.", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                // Capacity can be released by this durable transition, after the external-call
                // continuation already signalled. Wake again only after the record is committed.
                if (IsSessionCurrent(active)) Signal(active.Deferred);
                if (saved.Stage != OutboxStage.Accepted || !DeliveryCurrent(active, sending)) return;
            }
        }).ConfigureAwait(false);
    }

    private async Task<OutboxMessage> DeliverAndRecordAsync(DurableDeliveryRequest request,
        OutboxAttemptLease attempt, CancellationToken cancellationToken)
    {
        DurableDeliveryResult outcome;
        try
        {
            outcome = await delivery(request, cancellationToken).ConfigureAwait(false);
            if (outcome is null || outcome.Stage is not (OutboxStage.Accepted or OutboxStage.Rejected or OutboxStage.Unknown)
                || outcome.Receipt?.Length > 16 * 1024 || !Enum.IsDefined(outcome.RejectionReason)
                || outcome.Stage != OutboxStage.Rejected && outcome.RejectionReason != OutboxRejectionReason.None)
                outcome = new(OutboxStage.Unknown, "Invalid channel receipt.");
        }
        catch (Exception) { outcome = new(OutboxStage.Unknown, "The channel outcome could not be confirmed."); }
        // The lease keeps the original identity and attempt even after a session
        // switch. This records history only; it never re-dispatches a message.
        return await ledger.RecordOutboxAttemptOutcomeAsync(attempt, outcome.Stage,
            string.IsNullOrWhiteSpace(outcome.Receipt) ? "No channel receipt text." : outcome.Receipt,
            outcome.RejectionReason, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WorkerAsync(Session active, Channel<bool> signals, Func<Task> step)
    {
        try
        {
            await foreach (var _ in signals.Reader.ReadAllAsync(active.Lifetime.Token).ConfigureAwait(false))
            {
                if (!IsSessionCurrent(active)) return;
                try { await step().ConfigureAwait(false); }
                catch (OperationCanceledException) when (!active.Lifetime.IsCancellationRequested) { }
                catch (OperationCanceledException) when (active.Lifetime.IsCancellationRequested) { return; }
                catch (LedgerException error) { Report(error.Code, active.Identity); }
                catch (TimeoutException) { Report("adapter_timeout", active.Identity); }
                catch (Exception) { Report("coordinator_failure", active.Identity); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<T> ExternalAsync<T>(Func<CancellationToken, Task<T>> invoke, SemaphoreSlim slots,
        TimeSpan timeout, CancellationToken cancellationToken, bool slotReserved = false)
    {
        if (!slotReserved && !await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new AdapterBusyException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<T> pending;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            pending = invoke(deadline.Token) ?? Task.FromException<T>(new InvalidOperationException("Adapter returned no task."));
        }
        catch { slots.Release(); throw; }
        Interlocked.Increment(ref externalCalls);
        lock (externalGate) externalTasks.Add(pending);
        _ = pending.ContinueWith(completed =>
        {
            _ = completed.Exception;
            Interlocked.Decrement(ref externalCalls);
            slots.Release();
            lock (externalGate) externalTasks.Remove(completed);
            var current = Volatile.Read(ref session);
            if (current is not null)
            {
                Signal(current.Send);
                Signal(current.Fast);
                Signal(current.Deferred);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private bool CanGroup(InboxMessage earlier, InboxMessage current, TimeSpan window) =>
        window > TimeSpan.Zero && current.ReceivedAt >= earlier.ReceivedAt
        && current.ReceivedAt - earlier.ReceivedAt <= window
        && earlier.ReferencedEventId == current.ReferencedEventId
        && !earlier.HasUnresolvedReference && !current.HasUnresolvedReference
        && (options.CanGroup?.Invoke(earlier, current) ?? earlier.Task == current.Task);

    private bool IsSessionCurrent(Session active) => Volatile.Read(ref session) == active
        && !active.Lifetime.IsCancellationRequested && identityProvider() == active.Identity;
    private bool StillCurrent(Session active, long revision) => IsSessionCurrent(active) && active.Revision == revision;
    private bool DeliveryCurrent(Session active, OutboxMessage message) => IsSessionCurrent(active)
        && (message.Kind == OutboxKind.Notification || active.Revision == (message.ReadAtRevision ?? message.Revision));
    private bool NoticeStillCurrent(Session active, ConversationSnapshot snapshot, OutboxMessage message)
    {
        if (message.Kind != OutboxKind.Notification) return true;
        if (!snapshot.CatchUpBatches.IsDefault && snapshot.CatchUpBatches.SingleOrDefault(
                batch => batch.DeliveryEventId == message.NotificationEventId) is { } batch)
        {
            var window = snapshot.CatchUpWindows.Single(item => item.Id == batch.WindowId);
            if (window.TrustedInputRevision != active.Revision) return false;
        }
        var notice = snapshot.Events.SingleOrDefault(item => item.Id == message.NotificationEventId);
        // Older stores can contain detailed owned approvals without the newer
        // marker. Only a live provider observation may adopt that exact event.
        if (notice?.RequiredDecision is null
            && notice?.ExternalEventKey?.StartsWith("owned-approval/", StringComparison.Ordinal) == true)
            return false;
        return notice is not null && (notice.RequiredDecision is null || requiredDecisionValidator?.Invoke(notice) == true);
    }
    private async Task PrepareCatchUpWindowAsync(ConversationIdentity identity, CancellationToken ct)
    {
        var snapshot = await ledger.ReadConversationAsync(identity, ct).ConfigureAwait(false);
        if (snapshot.IsReadOnly || snapshot.CatchUpWindows.IsDefaultOrEmpty) return;
        var window = snapshot.CatchUpWindows.SingleOrDefault(item => item.TrustedInputRevision == snapshot.Revision
            && item.Stage != CatchUpWindowStage.Superseded);
        if (window is null) return;
        if (identityProvider() != identity)
            throw new LedgerException(LedgerErrors.IdentityMismatch, "The binding changed before preparing historical notices.");
        var verified = !window.Input.HasFreshChannelContext || window.DialogueEpoch != snapshot.Context?.Epoch
            ? Array.Empty<Guid>()
            : snapshot.Events.Where(notice => notice.RequiredDecision is { } decision && decision.ExpiresAt > clock.UtcNow
                && notice.RequiredDecisionSuppressedAt == null
                && snapshot.Outbox.Any(page => page.NotificationEventId == notice.Id
                    && page.Stage is OutboxStage.Pending or OutboxStage.Rejected)
                && requiredDecisionValidator?.Invoke(notice) == true).Select(notice => notice.Id).ToArray();
        await ledger.PrepareCatchUpAsync(identity, window.SourceMessageId, ct, verified).ConfigureAwait(false);
    }
    private static bool SendEligible(ConversationSnapshot snapshot, OutboxMessage message) =>
        message.Kind == OutboxKind.Notification
        || message.Kind == OutboxKind.ConversationReply && message.Revision == snapshot.Revision
        || message.Kind == OutboxKind.DeferredReply && message.ReadAtRevision == snapshot.Revision
            && snapshot.InputWork.Any(x => x.Id == message.InputWorkId && x.GenerationId == message.GenerationId
                && x.DialogueEpoch == snapshot.Context!.Epoch && x.Stage is InputWorkStage.ReplyPending or InputWorkStage.NeedsConfirmation);
    private void SettleCurrent(Session active, long revision)
    {
        if (!StillCurrent(active, revision)) return;
        Interlocked.Exchange(ref active.SettledRevision, revision);
        Signal(active.Deferred);
    }
    private static InboxMessage? CurrentInput(ConversationSnapshot snapshot) =>
        snapshot.Inbox.SingleOrDefault(x => x.Revision == snapshot.Revision && IsResumable(x.Stage));
    private static bool IsResumable(InboxStage stage) => stage is InboxStage.Received or InboxStage.Grouped or InboxStage.Superseded;
    private static void Signal(Channel<bool> channel) => channel.Writer.TryWrite(true);
    private void Report(string code, ConversationIdentity identity, string? source = null)
    {
        try { onError?.Invoke(new(code, clock.UtcNow, identity, source)); }
        catch { /* Diagnostics never change durable processing outcomes. */ }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private sealed class AdapterBusyException : Exception;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await DrainSessionAsync().ConfigureAwait(false);
        // External tasks may still own slots. Their continuations release them safely; never dispose
        // a semaphore or token source while a non-cooperative adapter can still be observing it.
    }

    private sealed class Session(ConversationIdentity identity, long initialRevision)
    {
        private readonly object gate = new();
        private long revision = initialRevision;
        private CancellationTokenSource revisionCancellation = new();
        internal ConversationIdentity Identity { get; } = identity;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal Channel<bool> Fast { get; } = Signals();
        internal Channel<bool> Normal { get; } = Signals();
        internal Channel<bool> Deferred { get; } = Signals();
        internal Channel<bool> Send { get; } = Signals();
        internal Task[] Workers { get; set; } = [];
        internal long ClassifiedRevision { get; set; } = -1;
        internal long NormalRevision;
        internal long SettledRevision = -1;
        internal long Revision { get { lock (gate) return revision; } }
        internal void Advance(long value)
        {
            CancellationTokenSource? old = null;
            lock (gate)
            {
                if (value <= revision) return;
                revision = value;
                old = revisionCancellation;
                revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            }
            SafeCancel(old);
        }
        internal CancellationToken RevisionToken(long value)
        {
            lock (gate) return value == revision && !Lifetime.IsCancellationRequested ? revisionCancellation.Token : new(true);
        }
        internal void Cancel()
        {
            SafeCancel(Lifetime);
            lock (gate) SafeCancel(revisionCancellation);
            Fast.Writer.TryComplete();
            Normal.Writer.TryComplete();
            Deferred.Writer.TryComplete();
            Send.Writer.TryComplete();
        }
        private static void SafeCancel(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (AggregateException) { /* A callback cannot undo durable inbox acceptance. */ }
        }
        private static Channel<bool> Signals() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }
}
