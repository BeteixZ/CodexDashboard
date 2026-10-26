using System.Collections.Immutable;
using System.Text.Json;

namespace Assistant.Core;

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ModelToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record ModelToolObservation(string CallId, string ToolName, string ResultJson,
    Guid? InvocationId = null, Guid? OperationId = null);
public sealed record ToolModelRequest(
    InboxMessage CurrentMessage, long Revision, ImmutableArray<ConversationEvent> Context,
    ImmutableArray<ModelToolDefinition> Tools, ImmutableArray<ModelToolObservation> Observations,
    ConversationContext? ConversationContext = null, InputWorkItem? DeferredWork = null, InboxMessage? LatestInput = null,
    GenerationContext? GenerationContext = null);
public sealed record ToolModelResponse(ImmutableArray<ModelToolCall> Calls, ImmutableArray<string> ReplySegments)
{
    public static ToolModelResponse Call(params ModelToolCall[] calls) => new(calls.ToImmutableArray(), []);
    public static ToolModelResponse Reply(params string[] segments) => new([], segments.ToImmutableArray());
}

/// <summary>The host adapts Responses function calls or strictly parsed action JSON to this interface.</summary>
public interface IToolModel
{
    Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken cancellationToken);
}

public enum ToolModelFailureKind { Unavailable, Authorization, UsageLimit, RateLimited, InvalidOutput, Timeout }
public sealed class ToolModelException(ToolModelFailureKind kind, Exception innerException)
    : Exception("Model request failed.", innerException)
{
    public ToolModelFailureKind Kind { get; } = kind;
}

/// <summary>Pure channel formatting before persistence. Each returned string is one actual send.</summary>
public interface IReplySegmenter
{
    ImmutableArray<string> Split(ImmutableArray<string> replySegments);
}

public sealed record ToolCapability(bool Available, string? Reason = null);
public enum ToolExecutionState { ReadOnlySuccess, Accepted, Running, Completed, Rejected, Unknown }
public sealed record ToolExecutionResult(ToolExecutionState State, JsonElement Facts, string? Reason = null,
    string? TargetKey = null, TaskReference? Task = null, ContextDependencyTransition? ContextTransition = null);
public sealed record ToolExecutionContext(
    ConversationIdentity Identity, string SourceMessageId, long Revision,
    Guid InvocationId, Guid? OperationId, string? IdempotencyKey,
    Guid? InputWorkId = null, long? ReadAtRevision = null, GenerationContext? GenerationContext = null,
    TaskReference? QuotedTask = null);

/// <summary>Adapters must preserve task identity, permissions and receipt semantics and honor cancellation.</summary>
public interface IToolExecutor
{
    ToolCapability GetCapability(string capability);
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken cancellationToken);
}

public sealed record UserAuthorizationRequest(InboxMessage CurrentMessage, ModelToolCall Call, ToolDescriptor Tool);
public sealed record UserAuthorizationDecision(bool Allowed, string? TargetKey = null,
    string? SourceExcerpt = null, TaskReference? Task = null, string? Reason = null);

/// <summary>
/// Trusted deterministic host policy, not the model. Decide from the current authenticated user
/// request and resolved target. Reports, tool output, memory and attachments cannot grant authority.
/// </summary>
public interface IUserAuthorizationPolicy
{
    ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken cancellationToken);
}

public enum ToolLoopState { Completed, NeedsConfirmation, BudgetExceeded, Superseded, ModelUnavailable, Failed, Cancelled }
public sealed record ToolLoopResult(ToolLoopState State, GenerationLease Generation,
    ImmutableArray<ModelToolObservation> Observations, ImmutableArray<OutboxMessage> Outbox, string? Reason = null,
    ToolModelFailureKind? ModelFailure = null);

public sealed record ToolLoopOptions
{
    public int MaximumModelTurns { get; init; } = 8;
    public int MaximumToolCalls { get; init; } = 16;
    public int MaximumContextEvents { get; init; } = 40;
    public int MaximumContextCharacters { get; init; } = 64 * 1024;
    public int MaximumArgumentsCharacters { get; init; } = 16 * 1024;
    public int MaximumResultCharacters { get; init; } = ToolResultJson.DefaultMaximumResultCharacters;
    public int MaximumReplySegments { get; init; } = 3;
    public int MaximumConcurrentExternalCalls { get; init; } = 4;
    public TimeSpan ModelTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record ToolDescriptor(string Name, string Description, ToolSchema Parameters, bool HasSideEffects, string Capability);

/// <summary>Explicit schemas only. Additional object properties and unlisted tools are always rejected.</summary>
public sealed class ToolCatalog
{
    private readonly ImmutableDictionary<string, ToolDescriptor> _tools;
    public ToolCatalog(IEnumerable<ToolDescriptor> tools)
    {
        var array = tools.ToArray();
        if (array.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(tools));
        foreach (var tool in array)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 64
                || tool.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                || string.IsNullOrWhiteSpace(tool.Description) || tool.Description.Length > 2048
                || string.IsNullOrWhiteSpace(tool.Capability))
                throw new ArgumentException("Invalid tool descriptor.", nameof(tools));
            if (tool.Parameters.Type != ToolValueType.Object)
                throw new ArgumentException("Tool parameters must be an object schema.", nameof(tools));
            tool.Parameters.ValidateDefinition();
        }
        _tools = array.ToImmutableDictionary(x => x.Name, StringComparer.Ordinal);
    }
    public bool TryGet(string name, out ToolDescriptor descriptor) => _tools.TryGetValue(name, out descriptor!);
    public ImmutableArray<ModelToolDefinition> AvailableDefinitions(IToolExecutor executor, bool readOnly = false) =>
        _tools.Values.Where(x => (!readOnly || !x.HasSideEffects) && executor.GetCapability(x.Capability).Available)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new ModelToolDefinition(x.Name, x.Description, x.Parameters.ToJsonSchema())).ToImmutableArray();

    public static ToolCatalog Initial() => new(
    [
        Definition("list_tasks", false, "List tasks using current observations. Follow nextOffset to read later pages.", ToolSchema.Object(
            ("hostId", ToolSchema.String(), false), ("project", ToolSchema.String(), false), ("query", ToolSchema.String(), false),
            ("offset", new() { Type = ToolValueType.Integer, Minimum = 0, Maximum = 1000000 }, false))),
        Definition("read_task", false, "Read a task without starting a turn. Use progressNextOffset for older progress blocks and planNextOffset for later plan steps.", TaskSchema(
            ("progressOffset", new() { Type = ToolValueType.Integer, Minimum = 0, Maximum = 1000000 }, false),
            ("planOffset", new() { Type = ToolValueType.Integer, Minimum = 0, Maximum = 1000000 }, false))),
        Definition("read_report", false, "Read observed report text from the exact task and turn. Follow nextOffset for remaining available text; sourceTruncated means the collector did not provide the full report.", TaskSchema(
            ("turnId", ToolSchema.String(), true), ("offset", new() { Type = ToolValueType.Integer, Minimum = 0, Maximum = 1000000 }, false))),
        Definition("send_to_task", true, "Continue the identified original task.", TaskSchema(("text", ToolSchema.String(maximumLength: 16384), true))),
        Definition("cancel_queued", true, "Cancel a queued operation; this does not interrupt a running turn.",
            ToolSchema.Object(("operationId", ToolSchema.String(), true))),
        Definition("remember", true, "Save a user-requested memory.", ToolSchema.Object(
            ("text", ToolSchema.String(maximumLength: 8192), true), ("scope", ToolSchema.String(), true))),
        Definition("forget", true, "Delete a user-selected memory.", ToolSchema.Object(("memoryId", ToolSchema.String(), true)))
    ]);

    private static ToolDescriptor Definition(string name, bool effects, string description, ToolSchema schema) =>
        new(name, description, schema, effects, name);
    private static ToolSchema TaskSchema(params (string, ToolSchema, bool)[] extra) => ToolSchema.Object(
        [("hostId", ToolSchema.String(), true), ("threadId", ToolSchema.String(), true),
            ("profileId", ToolSchema.String(), false), .. extra]);
}
