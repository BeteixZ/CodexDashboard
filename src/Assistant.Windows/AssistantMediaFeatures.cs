using System;
namespace CodexDashboard;
public sealed class AssistantMediaOptions
{
    public bool Attachments { get; set; }
    public bool Images { get; set; }
    public bool Files { get; set; }
    public bool Typing { get; set; }
}
internal sealed record AssistantMediaFeatures(bool Attachments, bool Images, bool Files, bool Typing)
{
    internal static AssistantMediaFeatures Capture(AssistantMediaOptions options)
    {
        bool Enabled(string name, bool saved) => Environment.GetEnvironmentVariable(name) switch { "1" => true, "0" => false, _ => saved };
        bool attachments = Enabled("CODEX_ASSISTANT_ATTACHMENTS", options.Attachments);
        return new(attachments, attachments && Enabled("CODEX_ASSISTANT_IMAGES", options.Images),
            Enabled("CODEX_ASSISTANT_FILE_SEND", options.Files), Enabled("CODEX_ASSISTANT_TYPING", options.Typing));
    }
}
