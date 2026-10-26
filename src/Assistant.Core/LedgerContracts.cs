using System.Collections.Immutable;

namespace Assistant.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

/// <summary>
/// A host-supplied, authenticated encryption boundary. Windows hosts should inject current-user
/// DPAPI protection with the supplied purpose as entropy. There is no plaintext implementation
/// or default protector in this library. Protect/Unprotect must fail closed.
/// </summary>
public interface IStateProtector
{
    string Scheme { get; }
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose);
}

public sealed record ConversationIdentity(string UserId, string BindingId, string ConversationId);
public sealed record TaskReference(string HostId, string ThreadId, string? TurnId = null, string? ProfileId = null);
public enum MessageTrust { UntrustedContent, AuthenticatedUser }
public enum InboxStage { Received, Grouped, Processing, Prepared, HandedToOutbox, Cancelled, Superseded, Failed }
public enum ConversationSource { TrustedUser, UntrustedContent, Assistant, Notification, ToolResult, Report, System }
public enum GenerationStage { Processing, Prepared, HandedToOutbox, Superseded, Interrupted }
public enum OperationStage { Queued, Dispatching, Accepted, Running, Completed, Failed, CancelledBeforeDispatch, Interrupted, Unknown }
public enum OutboxStage { Pending, Sending, Accepted, Rejected, Unknown, Superseded }
public enum OutboxKind { ConversationReply, Notification, DeferredReply }
public enum NotificationDraftStage { Captured, Preparing, Finalized }
public enum ToolInvocationStage { Prepared, Executing, Completed, Failed, Unknown, NeedsConfirmation, Unavailable, InvalidArguments, Interrupted }

public sealed record InboxMessage(
    Guid Id, ConversationIdentity Identity, string SourceMessageId, string Body,
    MessageTrust Trust, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt,
    long Revision, InboxStage Stage, Guid? GenerationId = null,
    TaskReference? Task = null, Guid? ReferencedEventId = null, bool HasUnresolvedReference = false);

public sealed record ConversationEvent(
    Guid Id, ConversationIdentity Identity, ConversationSource Source, string Body,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, long Revision,
    string? SourceMessageId = null, TaskReference? Task = null,
    Guid? RelatedOperationId = null, Guid? RelatedOutboxId = null, Guid? ReferencedEventId = null,
    string? ExternalEventKey = null, ImmutableArray<string>? NotificationSegments = null,
    bool IsDefaultTaskNotification = false, RequiredDecisionNotice? RequiredDecision = null,
    ImmutableArray<DecisionNoticeSubscriber>? DecisionSubscribers = null,
    DateTimeOffset? RequiredDecisionSuppressedAt = null, string? RequiredDecisionSuppressionReason = null,
    OutputDependencies? OutputDependencies = null, Guid? RelatedInvocationId = null, DependentWorkNotice? DependentWorkNotice = null);

public sealed record ConversationState(ConversationIdentity Identity, long Revision);
public sealed record GenerationLease(Guid Id, ConversationIdentity Identity, long Revision);
public sealed record GenerationRecord(
    Guid Id, ConversationIdentity Identity, long Revision, ImmutableArray<Guid> InboxIds,
    GenerationStage Stage, DateTimeOffset StartedAt, ImmutableArray<string> PreparedSegments,
    TaskReference? Task = null, Guid? InputWorkId = null, long WorkVersion = 0, long? ReadAtRevision = null,
    GenerationContext? GenerationContext = null, ImmutableArray<ContextDependencyTransition>? ContextTransitions = null,
    string? DependencyInvalidationReason = null);

/// <summary>
/// Created only through AuthorizeCurrentUserRequest, after the host has verified explicit user
/// intent. The literal source excerpt and exact action/target are audit evidence, not a semantic
/// classifier. Never expose that method as a model-controlled tool.
/// </summary>
public sealed record UserAuthorization(
    Guid Id, ConversationIdentity Identity, string SourceMessageId, long Revision,
    string Action, string TargetKey, string ArgumentsJson, string SourceExcerpt, DateTimeOffset CreatedAt,
    TaskReference? Task = null);

public sealed record OperationRecord(
    Guid Id, ConversationIdentity Identity, Guid AuthorizationId, string SourceMessageId,
    long Revision, string IdempotencyKey, string Action, string TargetKey, string ArgumentsJson,
    OperationStage Stage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    TaskReference? Task = null, string? Receipt = null);

/// <summary>Receipts from a control adapter must carry the exact immutable target and task identity.</summary>
public sealed record OperationObservation(OperationStage Stage, string Evidence,
    string TargetKey, TaskReference? Task, bool Reconciliation = false);

/// <summary>Accepted means channel API acceptance only. This type deliberately has no Delivered flag.</summary>
public sealed record OutboxMessage(
    Guid Id, ConversationIdentity Identity, Guid? GenerationId, long Revision,
    int SegmentIndex, string Body, OutboxStage Stage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ChannelReceipt = null,
    OutboxKind Kind = OutboxKind.ConversationReply, Guid? NotificationEventId = null,
    Guid? InputWorkId = null, long? ReadAtRevision = null, int SendAttempts = 0,
    long? AttemptTrustedInputRevision = null, OutboxRejectionReason RejectionReason = OutboxRejectionReason.None,
    string? DependencyInvalidationReason = null);

public sealed record NotificationEnqueueResult(ConversationEvent Event,
    ImmutableArray<OutboxMessage> Outbox, bool IsDuplicate);

public sealed record DurableNotificationDraft(
    Guid Id, ConversationIdentity Identity, Guid EventId, string EventKey,
    string ContextBody, string PayloadJson, TaskReference? Task, ConversationSource Source,
    NotificationDraftStage Stage, int PreparationAttempts, int MaximumPreparationAttempts,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? PreparationId = null, string? PreparedBody = null,
    Guid? WorkRunId = null, bool Cancelled = false);
public sealed record NotificationCaptureResult(DurableNotificationDraft Draft, ConversationEvent Event, bool IsDuplicate);
public sealed record NotificationPreparationLease(Guid DraftId, ConversationIdentity Identity, Guid ClaimId, int Attempt);

public sealed record ConversationSnapshot(
    ConversationIdentity Identity, long Revision, ImmutableArray<InboxMessage> Inbox,
    ImmutableArray<ConversationEvent> Events, ImmutableArray<GenerationRecord> Generations,
    ImmutableArray<UserAuthorization> Authorizations, ImmutableArray<OperationRecord> Operations,
    ImmutableArray<OutboxMessage> Outbox, bool IsReadOnly, string? RecoveryReason,
    ImmutableArray<ToolInvocationRecord> ToolInvocations = default,
    ImmutableArray<AgentBudgetRecord> AgentBudgets = default,
    ImmutableArray<DurableNotificationDraft> NotificationDrafts = default,
    ConversationContext? Context = null, ImmutableArray<InputWorkItem> InputWork = default,
    ImmutableArray<TrackedWorkItem> WorkItems = default, ImmutableArray<ReminderSchedule> Schedules = default,
    ImmutableArray<TaskSubscription> Subscriptions = default, ImmutableArray<CommitmentRun> CommitmentRuns = default,
    ImmutableArray<CommitmentBudgetReservation> CommitmentBudgetReservations = default,
    ImmutableArray<ChannelWakeRecord> ChannelWakes = default, WakeChannelConfiguration? WakeChannel = null,
    ImmutableArray<CatchUpWindow> CatchUpWindows = default, ImmutableArray<CatchUpBatch> CatchUpBatches = default,
    bool OutputDependenciesRequired = false, DependentWorkSnapshot? DependentWork = null);

public sealed record AgentBudgetRecord(ConversationIdentity Identity, string SourceMessageId,
    long Revision, int ModelTurns, int ToolCalls);

public sealed record ToolInvocationRecord(
    Guid Id, ConversationIdentity Identity, string SourceMessageId, long Revision,
    Guid GenerationId, string CallId, string ToolName, string ArgumentsJson, bool HasSideEffects,
    ToolInvocationStage Stage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? OperationId = null, string? ResultJson = null, GenerationContext? GenerationContext = null);

public sealed record ReceiveResult(InboxMessage Message, bool IsDuplicate, long CurrentRevision);
public sealed record RecoveryPlan(
    ImmutableArray<InboxMessage> ResumableInbox,
    ImmutableArray<GenerationRecord> PreparedReplies,
    ImmutableArray<OperationRecord> QueuedOperations,
    ImmutableArray<OperationRecord> OperationsToReconcile,
    ImmutableArray<OutboxMessage> PendingOutbox,
    ImmutableArray<OutboxMessage> OutboxToReconcile,
    ImmutableArray<DurableNotificationDraft> PendingNotificationDrafts = default);

public sealed record LedgerOptions
{
    public int MaximumBodyCharacters { get; init; } = 128 * 1024;
    public int MaximumSnapshotBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumArchivedSnapshotBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumSegments { get; init; } = 64;
    public int MaximumPendingNotificationDrafts { get; init; } = 128;
    public int MaximumPendingInputWork { get; init; } = 128;
    public int MaximumActiveCommitments { get; init; } = 128;
    public int MaximumCommitmentRuns { get; init; } = 4096;
    public CommitmentBudgetOptions CommitmentBudgets { get; init; } = new();
    public int MaximumChannelWakes { get; init; } = 4096;
    public int WakeAggregationSeconds { get; init; } = 5;
    public CatchUpOptions CatchUp { get; init; } = new();
    public bool RequireOutputDependencies { get; init; }
}

public class LedgerException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class LedgerErrors
{
    public const string Conflict = "conflict";
    public const string StaleRevision = "stale_revision";
    public const string InvalidTransition = "invalid_transition";
    public const string AuthorizationRequired = "authorization_required";
    public const string IdentityMismatch = "identity_mismatch";
    public const string RecoveryRequired = "recovery_required";
    public const string CorruptStorage = "corrupt_storage";
    public const string ProtectionFailed = "protection_failed";
    public const string CapacityExceeded = "capacity_exceeded";
    public const string NotFound = "not_found";
    public const string PreparationBudgetExceeded = "preparation_budget_exceeded";
    public const string NotificationSuppressed = "notification_suppressed";
    public const string BudgetDeferred = "budget_deferred";
    public const string DependencyInvalidated = "dependency_invalidated";
    public const string DependencyUnavailable = "dependency_unavailable";
}
