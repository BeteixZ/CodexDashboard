using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal static class OwnedDependencyCommands
{
    internal static ImmutableArray<DependencyCommandResult> Read(ReconciliationRead read, TaskReference expected)
    {
        if (read.WritesRemainBlocked || read.Observation?.Method != "thread/read" || read.Observation.TurnId != expected.TurnId
            || read.Task.Identity.HostId != expected.HostId || read.Task.Identity.ProfileId != expected.ProfileId
            || read.Task.Identity.ThreadId != expected.ThreadId || read.Task.CurrentTurnId != expected.TurnId) return [];
        var turns = read.Thread.GetProperty("turns").EnumerateArray().Where(t => Text(t, "id") == expected.TurnId).ToArray();
        if (turns.Length != 1 || !turns[0].TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var commands = ImmutableArray.CreateBuilder<DependencyCommandResult>();
        foreach (var item in items.EnumerateArray().Where(i => Text(i, "type") == "commandExecution"))
        {
            string id = Text(item, "id"), command = Text(item, "command"), cwd = Text(item, "cwd");
            // Never drop an unreadable retry and accidentally reuse an earlier success.
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || string.IsNullOrWhiteSpace(command) || command.Length > 2000
                || string.IsNullOrWhiteSpace(cwd) || cwd.Length > 4096 || commands.Count == 256 || commands.Any(c => c.ItemId == id)) return [];
            int? exit = item.TryGetProperty("exitCode", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int code) ? code : null;
            var state = Text(item, "status") switch
            {
                "completed" => DependencyCommandState.Completed, "failed" => DependencyCommandState.Failed,
                "declined" => DependencyCommandState.Declined, "inProgress" => DependencyCommandState.Running,
                _ => DependencyCommandState.Unknown
            };
            commands.Add(new(id, command, cwd, state, exit));
        }
        return commands.ToImmutable();
    }
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
