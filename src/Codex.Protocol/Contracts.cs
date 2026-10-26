namespace Codex.Protocol;

public enum ProtocolProduct { Unknown, Dashboard, Assistant }
public enum Availability { Unknown, Available, Unavailable, Degraded }
public enum ConnectionState { Unknown, NotConfigured, Disconnected, Connecting, Connected, Degraded }

public sealed record ProtocolVersion
{
    public required int Major { get; init; }
    public required int Minor { get; init; }
    public static ProtocolVersion V1 { get; } = new() { Major = 1, Minor = 0 };
}

public sealed record ProtocolVersionRange
{
    public required int Major { get; init; }
    public required int MinimumMinor { get; init; }
    public required int MaximumMinor { get; init; }
    public static ProtocolVersionRange V1 { get; } = new() { Major = 1, MinimumMinor = 0, MaximumMinor = 0 };
}

/// <summary>Product is a declaration, not executable authentication. The OS authenticates the user.</summary>
public sealed record ProtocolPeerIdentity
{
    public required ProtocolProduct Product { get; init; }
    public required string ProductVersion { get; init; }
    public required Guid InstanceId { get; init; }
}

public sealed record HelloRequest
{
    public required ProtocolPeerIdentity Identity { get; init; }
    public required ProtocolVersionRange SupportedVersions { get; init; }
    public required int MaximumFrameBytes { get; init; }
}

public sealed record HelloResponse
{
    public required ProtocolPeerIdentity Identity { get; init; }
    public required ProtocolVersionRange SupportedVersions { get; init; }
    public required ProtocolVersion SelectedVersion { get; init; }
    public required int MaximumFrameBytes { get; init; }
}

/// <summary>Contains status only. Reason is a safe user-facing summary, never raw exception text.</summary>
public sealed record HealthStatus
{
    public required DateTimeOffset ObservedAt { get; init; }
    public required Availability Readiness { get; init; }
    public string? Reason { get; init; }
}

public sealed record CapabilityStatus
{
    public required string Name { get; init; }
    public required Availability Availability { get; init; }
    public string? Reason { get; init; }
}

public sealed record ProtocolCapabilities
{
    public required IReadOnlyList<CapabilityStatus> Capabilities { get; init; }
    public required int MaximumFrameBytes { get; init; }
    public required int MaximumConcurrentConnections { get; init; }
    public required int MaximumRequestsPerConnection { get; init; }
}

/// <summary>No credentials, user names, chat bodies, memory, task content or raw diagnostics are allowed here.</summary>
public sealed record AssistantStatus
{
    public required DateTimeOffset ObservedAt { get; init; }
    public required ConnectionState WeChat { get; init; }
    public required Availability Model { get; init; }
    public required int QueuedCount { get; init; }
    public required int AttentionCount { get; init; }
    public string? AttentionReason { get; init; }
}

/// <summary>
/// Read cached state only. Implementations must return promptly, honor cancellation and never initiate
/// login, model calls, message sends or Codex operations. No UI dispatcher or UI framework is required.
/// </summary>
public interface IProtocolStatusProvider
{
    ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken);
    ValueTask<AssistantStatus> GetAssistantStatusAsync(CancellationToken cancellationToken);
}

public static class ProtocolMethods
{
    public const string Hello = "hello";
    public const string Health = "health";
    public const string Capabilities = "capabilities";
    public const string AssistantStatus = "assistant.status";
}

public static class ProtocolErrors
{
    public const string InvalidMessage = "invalid_message";
    public const string HandshakeRequired = "handshake_required";
    public const string VersionMismatch = "version_mismatch";
    public const string IdentityMismatch = "identity_mismatch";
    public const string MethodNotFound = "method_not_found";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string FrameTooLarge = "frame_too_large";
    public const string Timeout = "timeout";
    public const string Disconnected = "disconnected";
    public const string Busy = "busy";
    public const string InternalError = "internal_error";
}

public sealed class ProtocolException(string code, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public string Code { get; } = code;
}
