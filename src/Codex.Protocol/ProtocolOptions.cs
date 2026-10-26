using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Codex.Protocol;

public sealed record ProtocolOptions
{
    public ProtocolVersionRange SupportedVersions { get; init; } = ProtocolVersionRange.V1;
    public int MaximumFrameBytes { get; init; } = 64 * 1024;
    public int MaximumConcurrentConnections { get; init; } = 4;
    public int MaximumRequestsPerConnection { get; init; } = 256;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ValidateRange(SupportedVersions);
        if (MaximumFrameBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameBytes));
        if (MaximumConcurrentConnections is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentConnections));
        if (MaximumRequestsPerConnection is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestsPerConnection));
        ValidateTimeout(ConnectTimeout);
        ValidateTimeout(HandshakeTimeout);
        ValidateTimeout(RequestTimeout);
        ValidateTimeout(IdleTimeout);
    }

    internal static void ValidateRange(ProtocolVersionRange? range)
    {
        if (range is null || range.Major < 1 || range.MinimumMinor < 0 || range.MaximumMinor < range.MinimumMinor)
            throw new ArgumentException("A valid protocol version range is required.");
    }

    internal static void ValidateIdentity(ProtocolPeerIdentity? identity)
    {
        if (identity is null || identity.Product is not (ProtocolProduct.Dashboard or ProtocolProduct.Assistant)
            || identity.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(identity.ProductVersion)
            || identity.ProductVersion.Length > 64 || identity.ProductVersion.Any(char.IsControl))
            throw new ArgumentException("A valid product, product version and instance identifier are required.");
    }

    internal static void ValidatePipeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Use a local pipe name of at most 180 ASCII letters, digits, dots, hyphens or underscores.", nameof(name));
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
}

public static class LocalProtocolEndpoint
{
    /// <summary>Stable endpoint within one Windows user and installation profile. Does not expose the SID.</summary>
    public static string ForCurrentUser(ProtocolProduct product, string profile = "default")
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The v1 local protocol requires Windows.");
        if (product is not (ProtocolProduct.Dashboard or ProtocolProduct.Assistant))
            throw new ArgumentOutOfRangeException(nameof(product));
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 256)
            throw new ArgumentException("A nonempty installation profile is required.", nameof(profile));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows user is unavailable.");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\0" + profile)))[..24];
        return $"codex-{product.ToString().ToLowerInvariant()}-v1-{suffix}";
    }
}
