using System.IO;

namespace CodexDashboard;

internal static class BuiltinDialogueSkill
{
    // Embedded in the product: available to existing installations without a settings migration.
    internal static readonly string Instructions = Load();


    private static string Load()
    {
        using var stream = typeof(BuiltinDialogueSkill).Assembly.GetManifestResourceStream("CodexAssistant.Skills.NaturalDialogue")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
