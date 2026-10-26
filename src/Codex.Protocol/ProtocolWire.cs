using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.Protocol;

internal sealed record RequestEnvelope
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

internal sealed record ResponseEnvelope
{
    public required string Id { get; init; }
    public JsonElement? Result { get; init; }
    public ErrorPayload? Error { get; init; }
}

internal sealed record ErrorPayload
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

internal static class ProtocolWire
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static async ValueTask<T?> ReadAsync<T>(Stream stream, int maxBytes, CancellationToken cancellationToken) where T : class
    {
        var header = new byte[sizeof(int)];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0)
            throw new ProtocolException(ProtocolErrors.InvalidMessage, "Frame length must be positive.");
        if (length > maxBytes)
            throw new ProtocolException(ProtocolErrors.FrameTooLarge, "Frame exceeds the negotiated size limit.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json)
                ?? throw new ProtocolException(ProtocolErrors.InvalidMessage, "A JSON object is required.");
        }
        catch (JsonException)
        {
            // Never include raw parser output: it may contain input or private diagnostics.
            throw new ProtocolException(ProtocolErrors.InvalidMessage, "Invalid JSON or missing required fields.");
        }
    }

    internal static async ValueTask WriteAsync<T>(Stream stream, T value, int maxBytes, CancellationToken cancellationToken)
    {
        // Serialize into a bounded stream so a provider cannot allocate an unbounded wire response.
        using var buffer = new BoundedBuffer(maxBytes);
        JsonSerializer.Serialize(buffer, value, Json);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)buffer.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static ResponseEnvelope Success(string id, object result) => new()
    {
        Id = id, Result = JsonSerializer.SerializeToElement(result, Json)
    };

    internal static ResponseEnvelope Failure(string id, string code, string message) => new()
    {
        Id = id, Error = new() { Code = code, Message = message }
    };

    internal static T Deserialize<T>(JsonElement? element) where T : class
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            throw new ProtocolException(ProtocolErrors.InvalidMessage, "A result or parameter object is required.");
        try
        {
            return element.Value.Deserialize<T>(Json)
                ?? throw new ProtocolException(ProtocolErrors.InvalidMessage, "A JSON object is required.");
        }
        catch (JsonException)
        {
            throw new ProtocolException(ProtocolErrors.InvalidMessage, "Invalid JSON or missing required fields.");
        }
    }

    private sealed class BoundedBuffer(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (Position + count > maximumBytes)
                throw new ProtocolException(ProtocolErrors.FrameTooLarge, "Frame exceeds the negotiated size limit.");
        }
    }
}
