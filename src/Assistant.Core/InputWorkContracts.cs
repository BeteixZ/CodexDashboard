namespace Assistant.Core;

public enum InputWorkStage
{
    Queued, Reading, Prepared, ReplyPending, Completed, NeedsConfirmation, Failed, Superseded, Unknown, Cancelled
}

/// <summary>
/// An old input remains its original source. ReadAtRevision fences read-only reconsideration against
/// newer constraints; it never grants authority at that revision. Version identifies one read attempt.
/// </summary>
public sealed record InputWorkItem(Guid Id, ConversationIdentity Identity, string SourceMessageId,
    long SourceRevision, long DialogueEpoch, long Version, InputWorkStage Stage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long? ReadAtRevision = null,
    Guid? GenerationId = null, bool RequiresConfirmation = false, string? Reason = null,
    bool HasSendAttempt = false);
