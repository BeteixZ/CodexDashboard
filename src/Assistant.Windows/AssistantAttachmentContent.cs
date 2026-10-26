using System;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexDashboard;

internal sealed record AttachmentContent(string Kind, string? Text, bool Truncated, int? TotalCharacters, int Offset = 0, int? NextOffset = null, string? ExtractionNote = null);
internal static class AssistantAttachmentContent
{
    internal static AttachmentContent Read(byte[] content, int maximumCharacters = 16000, int offset = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCharacters is < 1 or > 128000) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return new("image/png", null, false, null);
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return new("image/jpeg", null, false, null);
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return new("image/gif", null, false, null);
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return new("image/webp", null, false, null);
        if (bytes.StartsWith("%PDF-"u8))
        {
            var pdf = AssistantPdfContent.Read(content, cancellationToken);
            return Page(pdf.Text, maximumCharacters, offset, "application/pdf", pdf.Note);
        }
        if (bytes.StartsWith("PK\u0003\u0004"u8))
        {
            string? word = AssistantWordContent.Read(content, cancellationToken);
            return word == null ? new("application/zip", null, false, null)
                : Page(word, maximumCharacters, offset, AssistantWordContent.Kind, AssistantWordContent.Note);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.StartsWith(new byte[] { 239, 187, 191 }) ? bytes[3..] : bytes); }
        catch (DecoderFallbackException) { return new("application/octet-stream", null, false, null); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return new("application/octet-stream", null, false, null);
        return Page(text, maximumCharacters, offset, "text/plain; charset=utf-8", null);
    }
    private static AttachmentContent Page(string text, int maximumCharacters, int offset, string kind, string? note)
    {
        if (offset > text.Length || offset < text.Length && char.IsLowSurrogate(text[offset]))
            throw new ArgumentOutOfRangeException(nameof(offset));
        int length = Math.Min(text.Length - offset, maximumCharacters);
        if (offset + length < text.Length && length > 0 && char.IsHighSurrogate(text[offset + length - 1])) length--;
        // A one-character page at a surrogate pair must still make progress.
        if (length == 0 && offset < text.Length) length = 2;
        int end = offset + length;
        return new(kind, text.Substring(offset, length), end < text.Length, text.Length,
            offset, end < text.Length ? end : null, note);
    }
}
