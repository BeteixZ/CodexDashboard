using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core;

public enum DependentWorkStage { Waiting, Dispatching, Running, Completed, Failed, Paused, Cancelled, Expired, NeedsDecision, Unknown }
public enum DependentDispatchStage { Dispatching, Accepted, NotDispatched, Rejected, Unknown }

// Exactly one predecessor: an observed task turn, or a version of another planned work item.
// Completed means the backend completed the exact turn; it does not assert that tests passed.
public sealed record DependencyRequirement(TaskReference? Task = null, string? BindingFingerprint = null,
    Guid? WorkItemId = null, long? WorkVersion = null, DependencyCommandRequirement? CommandSuccess = null);
public sealed record DependencyCommandRequirement(string Command, string WorkingDirectory);
public enum DependencyCommandState { Unknown, Running, Completed, Failed, Declined }
// Ordered command items from the same exact turn/read as DependencyEvidence.Observation.
public sealed record DependencyCommandResult(string ItemId, string Command, string WorkingDirectory,
    DependencyCommandState State, int? ExitCode);
public sealed record DependentWorkSpecification(TaskReference Target, string BindingFingerprint, string Instructions,
    ImmutableArray<DependencyRequirement> Requirements, string DeadlineUtc, Guid? ContinueAfterWorkId = null, CommitmentPolicy? NotificationPolicy = null);
public sealed record DependentWorkChange(Guid WorkItemId, long ExpectedVersion, string Action,
    string? Instructions = null, string? DeadlineUtc = null, ImmutableArray<DependencyRequirement>? Requirements = null, CommitmentPolicy? NotificationPolicy = null);
public sealed record DependencyEvidence(CommitmentTaskObservation Observation, bool CanStartTurn = false,
    ImmutableArray<DependencyCommandResult>? Commands = null);
public sealed record DependentWorkItem(Guid Id, ConversationIdentity Identity, string SourceMessageId,
    Guid CreationOperationId, long Version, DependentWorkStage Stage, TaskReference Target, string BindingFingerprint,
    string Instructions, ImmutableArray<DependencyRequirement> Requirements, DateTimeOffset DeadlineUtc,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? AttemptId = null, TaskReference? ResultTask = null,
    ImmutableArray<DependencyEvidence> LastEvidence = default, CommitmentTaskObservation? Completion = null,
    string? Reason = null, CommitmentTaskObservation? LastResultObservation = null, Guid? ContinueAfterWorkId = null, CommitmentPolicy? NotificationPolicy = null);
public sealed record DependentWorkNotice(Guid WorkItemId, long WorkVersion, DependentWorkStage Stage,
    Guid? RecoveryOfEventId = null, Guid? AttemptId = null);
public sealed record DependentDispatchAttempt(Guid Id, Guid WorkItemId, ConversationIdentity Identity, long WorkVersion,
    DependentDispatchStage Stage, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    ImmutableArray<DependencyEvidence> Evidence, string? Receipt = null, TaskReference? ResultTask = null, Guid? RecoveryOfEventId = null);
public sealed record DependentDispatchLease(Guid AttemptId, Guid WorkItemId, ConversationIdentity Identity,
    long WorkVersion, TaskReference Target, string BindingFingerprint, string Instructions);
public sealed record DependencyReadiness(DependentWorkItem Work, string Code, bool Ready, DependentDispatchLease? Lease = null,
    ImmutableArray<TaskReference> EvidenceTargets = default);
public sealed record DependentWorkSnapshot(ImmutableArray<DependentWorkItem> WorkItems, ImmutableArray<DependentDispatchAttempt> Attempts);
internal sealed record DependentWorkMutation(Guid OperationId, Guid InvocationId, Guid WorkItemId, long WorkVersion, string ReceiptJson);

public static class DependentWorkTools
{
    public static bool Handles(string name) => name is "schedule_dependency" or "change_dependency";
    private static ToolSchema TaskSchema() => ToolSchema.Object(("hostId", ToolSchema.String(), true),
        ("threadId", ToolSchema.String(), true), ("turnId", ToolSchema.String(), true), ("profileId", ToolSchema.String(), false));
    private static ToolSchema Requirements() => new() { Type = ToolValueType.Array, MaximumItems = 8,
        Items = ToolSchema.Object(("task", TaskSchema(), false), ("bindingFingerprint", ToolSchema.String(64), false),
            ("workItemId", ToolSchema.String(36), false), ("workVersion", new() { Type = ToolValueType.Integer, Minimum = 1 }, false),
            ("commandSuccess", ToolSchema.Object(("command", ToolSchema.String(2000), true), ("workingDirectory", ToolSchema.String(4096), true)), false)) };
    public static ImmutableArray<ToolDescriptor> Descriptors() =>
    [
        new("schedule_dependency", "登记明确授权的后续指令、精确前置轮次和截止时间。任务结束不证明测试通过，受理不证明完成。",
            ToolSchema.Object(("target", TaskSchema(), true), ("bindingFingerprint", ToolSchema.String(64), true),
                ("instructions", ToolSchema.String(4000), true), ("requirements", Requirements(), true),
                ("deadlineUtc", ToolSchema.String(64), true), ("continueAfterWorkId", ToolSchema.String(36), false),
                ("notificationPolicy", CommitmentTools.PolicySchema(), false)), true, "schedule_dependency"),
        new("change_dependency", "按用户明确要求暂停、恢复、取消或修改依赖工作；不撤销已发送的任务指令。",
            ToolSchema.Object(("workItemId", ToolSchema.String(36), true),
                ("expectedVersion", new() { Type = ToolValueType.Integer, Minimum = 1 }, true),
                ("action", ToolSchema.String(16) with { AllowedStrings = ["pause", "resume", "cancel", "update"] }, true),
                ("instructions", ToolSchema.String(4000), false), ("deadlineUtc", ToolSchema.String(64), false),
                ("requirements", Requirements(), false), ("notificationPolicy", CommitmentTools.PolicySchema(), false)), true, "change_dependency")
    ];
    public static string TargetKey(string name, string arguments)
    {
        var descriptor = Descriptors().SingleOrDefault(d => d.Name == name);
        if (descriptor == null || !descriptor.Parameters.TryValidate(arguments, out var canonical, out _))
            throw new ArgumentException("Invalid dependent work arguments.");
        return "dependent-work/" + name + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
