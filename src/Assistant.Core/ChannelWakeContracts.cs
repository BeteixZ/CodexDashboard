using System.Collections.Immutable;

namespace Assistant.Core;

/// <summary>Only the first two contextual failures qualify for a short secondary-channel wake.</summary>
public enum OutboxRejectionReason { None, ContextUnavailable, ContextExpired, BindingUnavailable, OtherRejected }
public enum ChannelWakeStage { Pending, Sending, Accepted, Rejected, Unknown, Cancelled, NotStarted }
public enum WakeDeliveryOutcome { Accepted, Rejected, Unknown, NotStarted }

/// <summary>Trusted host settings. RouteId is a random nonsecret identity, never a provider token.</summary>
public sealed record WakeChannelConfiguration(Guid RouteId, bool Enabled, string TimeZoneId,
    string? QuietStart = null, string? QuietEnd = null);
public sealed record ConfiguredWakeChannel(ConversationIdentity Identity, WakeChannelConfiguration Configuration);
public sealed record ChannelWakeSource(Guid OutboxId, int SendAttempt, long TrustedInputRevision,
    OutboxRejectionReason RejectionReason);
public sealed record ChannelWakeRecord(Guid Id, ConversationIdentity Identity, long EpisodeRevision, Guid RouteId,
    ImmutableArray<ChannelWakeSource> Sources, ChannelWakeStage Stage, DateTimeOffset CreatedAt, DateTimeOffset NotBefore,
    Guid? ClaimId = null, int ReservedAttempts = 0, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null,
    string? Evidence = null, string? PlatformReceipt = null, DateTimeOffset? ReleasedAt = null)
{
    public const string ShortBody = "有待补报记录，回我一句再查看。";
}
public sealed record ChannelWakeLease(Guid WakeId, ConversationIdentity Identity, Guid RouteId,
    long EpisodeRevision, Guid ClaimId);
public sealed record ChannelWakeBudgetStatus(DateTimeOffset ObservedAt, int StartedLastHour,
    int StartedLast24Hours, int InFlight, DateTimeOffset? NextWindowAt);
