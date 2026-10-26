using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Assistant.Core;

public enum ToolValueType { Object, String, Boolean, Integer, Number, Array }

/// <summary>A strict, bounded JSON-schema subset. Unsupported keywords cannot silently be accepted.</summary>
public sealed record ToolSchema
{
    public required ToolValueType Type { get; init; }
    public ImmutableDictionary<string, ToolSchema> Properties { get; init; } = ImmutableDictionary<string, ToolSchema>.Empty;
    public ImmutableHashSet<string> Required { get; init; } = ImmutableHashSet<string>.Empty;
    public ToolSchema? Items { get; init; }
    public ImmutableArray<string> AllowedStrings { get; init; } = [];
    public int MaximumLength { get; init; } = 512;
    public int MinimumLength { get; init; } = 1;
    public int MaximumItems { get; init; } = 64;
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }

    public static ToolSchema String(int maximumLength = 512) => new() { Type = ToolValueType.String, MaximumLength = maximumLength };
    public static ToolSchema Object(params (string Name, ToolSchema Schema, bool Required)[] properties) => new()
    {
        Type = ToolValueType.Object,
        Properties = properties.ToImmutableDictionary(x => x.Name, x => x.Schema, StringComparer.Ordinal),
        Required = properties.Where(x => x.Required).Select(x => x.Name).ToImmutableHashSet(StringComparer.Ordinal)
    };

    internal void ValidateDefinition(int depth = 0)
    {
        if (depth > 8 || !Enum.IsDefined(Type) || Properties.Count > 32 || MaximumLength is < 1 or > 128 * 1024
            || MinimumLength < 0 || MinimumLength > MaximumLength || MaximumItems is < 1 or > 256
            || Minimum is { } minimum && !double.IsFinite(minimum) || Maximum is { } maximum && !double.IsFinite(maximum)
            || Minimum > Maximum)
            throw new ArgumentException("Invalid or unbounded tool schema.");
        if (Required.Any(name => !Properties.ContainsKey(name))
            || Properties.Keys.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 64))
            throw new ArgumentException("Invalid tool schema properties.");
        if (Type == ToolValueType.Array && Items is null)
            throw new ArgumentException("Array schemas need an explicit item schema.");
        if (Type != ToolValueType.Object && (Properties.Count != 0 || Required.Count != 0)
            || Type != ToolValueType.Array && Items is not null
            || Type != ToolValueType.String && !AllowedStrings.IsDefaultOrEmpty)
            throw new ArgumentException("Schema properties do not match their value type.");
        foreach (var property in Properties.Values) property.ValidateDefinition(depth + 1);
        Items?.ValidateDefinition(depth + 1);
    }

    public bool TryValidate(string argumentsJson, out string canonicalJson, out string? reason)
    {
        canonicalJson = "";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new() { MaxDepth = 16 });
            if (!Matches(document.RootElement, out reason)) return false;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Canonicalize(writer, document.RootElement);
            canonicalJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            reason = "Arguments are not valid JSON.";
            return false;
        }
    }

    private bool Matches(JsonElement value, out string? reason)
    {
        reason = "Arguments do not match the declared tool schema.";
        switch (Type)
        {
            case ToolValueType.Object:
                if (value.ValueKind != JsonValueKind.Object) return false;
                var found = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (!found.Add(property.Name) || !Properties.TryGetValue(property.Name, out var schema)
                        || !schema.Matches(property.Value, out _)) return false;
                if (!Required.IsSubsetOf(found)) return false;
                break;
            case ToolValueType.Array:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumItems
                    || value.EnumerateArray().Any(item => !Items!.Matches(item, out _))) return false;
                break;
            case ToolValueType.String:
                if (value.ValueKind != JsonValueKind.String) return false;
                var text = value.GetString()!;
                if (text.Length < MinimumLength || text.Length > MaximumLength
                    || !AllowedStrings.IsDefaultOrEmpty && !AllowedStrings.Contains(text, StringComparer.Ordinal)) return false;
                break;
            case ToolValueType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                break;
            case ToolValueType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer)
                    || integer < Minimum || integer > Maximum) return false;
                break;
            case ToolValueType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
                    || number < Minimum || number > Maximum) return false;
                break;
            default: return false;
        }
        reason = null;
        return true;
    }

    public JsonElement ToJsonSchema()
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = Type.ToString().ToLowerInvariant()
        };
        switch (Type)
        {
            case ToolValueType.Object:
                schema["properties"] = Properties.ToDictionary(x => x.Key, x => x.Value.ToJsonSchema(), StringComparer.Ordinal);
                schema["required"] = Required.Order(StringComparer.Ordinal).ToArray();
                schema["additionalProperties"] = false;
                break;
            case ToolValueType.String:
                schema["minLength"] = MinimumLength;
                schema["maxLength"] = MaximumLength;
                if (!AllowedStrings.IsDefaultOrEmpty) schema["enum"] = AllowedStrings;
                break;
            case ToolValueType.Array:
                schema["items"] = Items!.ToJsonSchema();
                schema["maxItems"] = MaximumItems;
                break;
            case ToolValueType.Number:
            case ToolValueType.Integer:
                if (Minimum is not null) schema["minimum"] = Minimum;
                if (Maximum is not null) schema["maximum"] = Maximum;
                break;
        }
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void Canonicalize(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Canonicalize(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Canonicalize(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else writer.WriteNumberValue(element.GetDouble());
                break;
            default: element.WriteTo(writer); break;
        }
    }
}
