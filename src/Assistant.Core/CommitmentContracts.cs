using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core;

public enum TrackedWorkKind { Reminder, Subscription }
public enum TrackedWorkStage { Active, Triggered, Paused, Completed, Cancelled, Expired, NeedsDecision }
public enum MissedTriggerPolicy { NotifyOnce, Skip, NeedsDecision }
public enum SubscriptionCondition { Completed, Failed, NeedsDecision, NoActivity }
public enum CommitmentTaskState { Running, NeedsDecision, Completed, Failed, Interrupted, Unknown }
public enum CommitmentRunStage { PendingCheck, Checking, ReadyToNotify, NotificationPending, Completed, Skipped, Cancelled, NeedsDecision }

public sealed record CommitmentPolicy(
    string? QuietStart = null, string? QuietEnd = null, string? TimeZoneId = null,
    int MinimumIntervalSeconds = 0, int MaximumNotificationsPer24Hours = 20,
    int MaximumCheckAttempts = 3, int MaximumModelPreparations = 2, int MaximumDeliveryAttempts = 3);

public sealed record ReminderSpecification(string Text, string LocalTime, string TimeZoneId,
    MissedTriggerPolicy MissedPolicy, string? DeadlineUtc = null, CommitmentPolicy? Policy = null);
public sealed record SubscriptionSpecification(string HostId, string ThreadId, string TurnId,
    SubscriptionCondition Condition, string? ProfileId = null, int? InactivitySeconds = null,
    string? DeadlineUtc = null, CommitmentPolicy? Policy = null);
public sealed record CommitmentChangeSpecification(string WorkItemId, long ExpectedVersion, string Action,
    string? Text = null, string? LocalTime = null, string? TimeZoneId = null,
    MissedTriggerPolicy? MissedPolicy = null, CommitmentPolicy? Policy = null);

/// <summary>A durable future promise. This is distinct from read-only reconsideration of an old inbox input.</summary>
public sealed record TrackedWorkItem(Guid Id, ConversationIdentity Identity, TrackedWorkKind Kind,
    string SourceMessageId, Guid CreationOperationId, long Version, TrackedWorkStage Stage,
    string Goal, TaskReference? Task, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DeadlineUtc, CommitmentPolicy Policy);
public sealed record ReminderSchedule(Guid WorkItemId, string LocalTime, string TimeZoneId,
    DateTimeOffset ReferenceTimeUtc, DateTimeOffset DueAtUtc, MissedTriggerPolicy MissedPolicy, Guid? FiredRunId = null);
public sealed record TaskSubscription(Guid WorkItemId, SubscriptionCondition Condition,
    int? InactivitySeconds, DateTimeOffset LastActivityAt, CommitmentTaskState LastState,
    DateTimeOffset NextCheckAt, string? LastDecisionId = null, DateTimeOffset? LastObservedAt = null,
    CommitmentObservationOrder? LastObservationOrder = null);
public sealed record CommitmentRun(Guid Id, Guid WorkItemId, ConversationIdentity Identity,
    long WorkVersion, string TriggerKey, DateTimeOffset TriggeredAt, CommitmentRunStage Stage,
    DateTimeOffset NotBefore, int CheckAttempts = 0, Guid? CheckClaimId = null,
    Guid? NotificationDraftId = null, Guid? NotificationEventId = null, string? Body = null,
    string? Reason = null, ImmutableArray<DateTimeOffset> DeliveryAttempts = default,
    Guid? SharedDecisionEventId = null, Guid? CatchUpDeliveryOutboxId = null);
public sealed record CommitmentMutationReceipt(Guid OperationId, Guid InvocationId, Guid WorkItemId,
    ConversationIdentity Identity, long WorkVersion, string ReceiptJson);
public sealed record CommitmentCheckLease(Guid RunId, Guid WorkItemId, ConversationIdentity Identity,
    long WorkVersion, Guid ClaimId, int Attempt, TaskReference Task);
/// <summary>Host-verified public observations are facts, never authorization to start or approve work.</summary>
public sealed record CommitmentTaskObservation(TaskReference Task, CommitmentTaskState State,
    DateTimeOffset ObservedAt, DateTimeOffset? LastActivityAt, bool Fresh, bool Connected,
    string Source, string? DecisionId = null, CommitmentObservationOrder? Order = null,
    bool IsCurrentSourceEvidence = false);
public sealed record CommitmentSnapshot(ImmutableArray<TrackedWorkItem> WorkItems,
    ImmutableArray<ReminderSchedule> Schedules, ImmutableArray<TaskSubscription> Subscriptions,
    ImmutableArray<CommitmentRun> Runs);

public static class CommitmentTools
{
    public static bool Handles(string name) => name is "schedule" or "subscribe" or "change_commitment";
    public static ImmutableArray<ToolDescriptor> Descriptors() =>
    [
        new("schedule", "创建一次性提醒；时间与时区必须由当前用户原文和宿主确定，落盘成功后才可说已安排。",
            ToolSchema.Object(("text", ToolSchema.String(1000), true), ("localTime", ToolSchema.String(32), true),
                ("timeZoneId", ToolSchema.String(128), true), ("missedPolicy", Choice("notifyOnce", "skip", "needsDecision"), true),
                ("deadlineUtc", ToolSchema.String(64), false), ("policy", PolicySchema(), false)), true, "schedule"),
        new("subscribe", "订阅明确任务轮次的完成、失败、需要决定或无活动检查；只允许通知和只读检查，不授权后续执行。",
            ToolSchema.Object(("hostId", ToolSchema.String(), true), ("threadId", ToolSchema.String(), true),
                ("turnId", ToolSchema.String(), true), ("profileId", ToolSchema.String(), false),
                ("condition", Choice("completed", "failed", "needsDecision", "noActivity"), true),
                ("inactivitySeconds", Integer(1, 604800), false), ("deadlineUtc", ToolSchema.String(64), false),
                ("policy", PolicySchema(), false)), true, "subscribe"),
        new("change_commitment", "按当前用户要求暂停、恢复、取消或修改真实承诺；必须提供记录编号和版本，不取消已发送消息或已运行任务。",
            ToolSchema.Object(("workItemId", ToolSchema.String(36), true), ("expectedVersion", Integer(1, int.MaxValue), true),
                ("action", Choice("pause", "resume", "cancel", "update"), true),
                ("text", ToolSchema.String(1000), false), ("localTime", ToolSchema.String(32), false),
                ("timeZoneId", ToolSchema.String(128), false), ("missedPolicy", Choice("notifyOnce", "skip", "needsDecision"), false),
                ("policy", PolicySchema(), false)), true, "change_commitment")
    ];
    public static string TargetKey(string name, string argumentsJson)
    {
        var descriptor = Descriptors().SingleOrDefault(d => d.Name == name);
        if (descriptor == null || !descriptor.Parameters.TryValidate(argumentsJson, out var canonical, out _))
            throw new ArgumentException("Commitment arguments do not match the strict schema.");
        return "commitment/" + name + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    public static DateTimeOffset ResolveLocalTime(string localTime, string timeZoneId)
    {
        if (!DateTime.TryParseExact(localTime, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) throw new ArgumentException("Local time must have seconds and no offset.");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ArgumentException("The local time is missing or ambiguous; ask for an explicit unambiguous time.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
    private static ToolSchema Choice(params string[] choices) => ToolSchema.String(64) with { AllowedStrings = [.. choices] };
    private static ToolSchema Integer(int minimum, int maximum) => new()
        { Type = ToolValueType.Integer, Minimum = minimum, Maximum = maximum };
    internal static ToolSchema PolicySchema() => ToolSchema.Object(
        ("quietStart", ToolSchema.String(5), false), ("quietEnd", ToolSchema.String(5), false),
        ("timeZoneId", ToolSchema.String(128), false), ("minimumIntervalSeconds", Integer(0, 604800), false),
        ("maximumNotificationsPer24Hours", Integer(1, 100), false), ("maximumCheckAttempts", Integer(1, 8), false),
        ("maximumModelPreparations", Integer(1, 3), false), ("maximumDeliveryAttempts", Integer(1, 8), false));
}
