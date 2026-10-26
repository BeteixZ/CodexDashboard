namespace Assistant.Core;

/// <summary>
/// Identifies one already-persisted channel attempt. It is an in-memory receipt
/// fence, not permission to send or retry. The channel client ID is derived from
/// the unchanged OutboxId.
/// </summary>
public sealed record OutboxAttemptLease(
    ConversationIdentity Identity, Guid OutboxId, int Number, long? TrustedInputRevision);
