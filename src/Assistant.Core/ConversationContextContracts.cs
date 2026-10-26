using System.Collections.Immutable;

namespace Assistant.Core;

public sealed record WorkFocus(TaskReference Task, string SourceMessageId, string SourceExcerpt, DateTimeOffset SetAt);
public sealed record RecentNotification(Guid EventId, TaskReference? Task, DateTimeOffset RecordedAt,
    Guid? LastAcceptedOutboxId = null);
public enum TaskSelectionPurpose { Focus, Inspect }
public sealed record TaskSelectionCandidate(TaskReference Task, string Label);
public sealed record PendingTaskSelection(Guid Id, long DialogueEpoch, string SourceMessageId,
    TaskSelectionPurpose Purpose, ImmutableArray<TaskSelectionCandidate> Candidates,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record TaskSelectionResult(TaskReference Task, TaskSelectionPurpose Purpose,
    string OriginalSourceMessageId, string SelectionSourceMessageId, Guid SelectionId, int ChoiceNumber);
/// <summary>Read-only context. This record is not approval authority or proof that a backend request is still live.</summary>
public sealed record PendingApprovalContext(Guid Id, long DialogueEpoch, Guid SourceEventId,
    string RequestId, TaskReference Task, Guid BackendEpoch, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Conversation identity remains stable across new dialogues. Context is data, never authorization.
/// Epoch changes only for an explicit new-dialogue boundary; Version also tracks independent slot updates.
/// </summary>
public sealed record ConversationContext(ConversationIdentity Identity, long Epoch, long Version, long StartRevision,
    string? ResetSourceMessageId = null, WorkFocus? Focus = null, RecentNotification? LastNotification = null,
    PendingTaskSelection? PendingSelection = null, PendingApprovalContext? PendingApproval = null,
    TaskSelectionResult? LastSelection = null);
