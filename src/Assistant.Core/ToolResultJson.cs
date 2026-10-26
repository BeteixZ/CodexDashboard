using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Assistant.Core;

public static class ToolResultJson
{
    public const int DefaultMaximumResultCharacters = 64 * 1024;
    // Keep readable Unicode within the existing result budget. HTML-sensitive
    // characters remain escaped; this is JSON, never markup.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement FitText(string text, Func<string, JsonElement> create)
    {
        int length = text.Length;
        while (true)
        {
            var result = create(text[..length]);
            if (result.GetRawText().Length <= DefaultMaximumResultCharacters / 2) return result;
            if (length == 0) throw new ArgumentException("Tool result metadata exceeds the result budget.");
            length /= 2;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        }
    }
}
