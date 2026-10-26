using System.Collections.Immutable;

namespace Assistant.Core;

public enum CatchUpDisposition { AllowHistory, HoldHistory }
public enum CatchUpWindowStage { Captured, Prepared, Deferred, Empty, Superseded }
/// <summary>Pure trusted-host interpretation of the current authenticated input; never a control grant.</summary>
public sealed record CatchUpInputOptions(CatchUpDisposition Disposition, bool HasFreshChannelContext, string DisplayTimeZoneId);
public sealed record CatchUpOptions
{
    public int MaximumEvents { get; init; } = 12;
    public int MaximumPages { get; init; } = 3;
    public int MaximumPageCharacters { get; init; } = 900;
    public int MaximumWindows { get; init; } = 4096;
}
public sealed record CatchUpWindow(Guid Id, ConversationIdentity Identity, string SourceMessageId,
    long TrustedInputRevision, long DialogueEpoch, DateTimeOffset CapturedAt, CatchUpInputOptions Input,
    CatchUpWindowStage Stage, ImmutableArray<Guid> CandidateEventIds, ImmutableArray<Guid> RequiredDecisionEventIds,
    ImmutableArray<Guid> VerifiedRequiredDecisionEventIds, int DeferredEventCount = 0);
public sealed record CatchUpSource(Guid OutboxId, int SendAttempts, string BodyHash, int Start = 0, int? Length = null);
public sealed record CatchUpMember(Guid SourceEventId, ImmutableArray<CatchUpSource> Sources,
    Guid CatchUpOutboxId, string GroupKey, Guid? WorkRunId = null, long WorkVersion = 0);
/// <summary>Immutable, lossless lineage. Accepted and Unknown pages retain exclusive source coverage.</summary>
public sealed record CatchUpBatch(Guid Id, Guid WindowId, ConversationIdentity Identity, Guid DeliveryEventId,
    DateTimeOffset CreatedAt, ImmutableArray<CatchUpMember> Members, int ManifestVersion = 1);
public sealed record CatchUpCaptureResult(CatchUpWindow Window, CatchUpBatch? Batch, ImmutableArray<OutboxMessage> Outbox);
public sealed record CatchUpSnapshot(ImmutableArray<CatchUpWindow> Windows, ImmutableArray<CatchUpBatch> Batches);
/// <summary>Character counts describe API acceptance, never phone delivery. Remaining includes uncertain and pending content.</summary>
public sealed record CatchUpEventProgress(Guid SourceEventId, TaskReference? Task, int TotalCharacters,
    int AcceptedCharacters, int PendingCharacters, int RemainingCharacters, bool HasSending, bool HasUnknown,
    ImmutableArray<CatchUpSourceProgress> Sources);
public sealed record CatchUpSourceProgress(Guid OutboxId, int TotalCharacters, int AcceptedCharacters,
    int PendingCharacters, bool HasSending, bool HasUnknown);
