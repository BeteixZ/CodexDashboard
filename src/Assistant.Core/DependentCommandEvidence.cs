namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private static void RequireLiteralDependencyCommands(InboxMessage source, System.Collections.Immutable.ImmutableArray<DependencyRequirement> requirements)
    {
        foreach (var requirement in requirements)
            if (requirement.CommandSuccess is { } command && !source.Body.Contains(command.Command, StringComparison.Ordinal))
                throw new ArgumentException("Use the user's literal prerequisite command.");
    }
    private static void RequireDependencyCommand(DependencyCommandRequirement command)
    {
        if (string.IsNullOrWhiteSpace(command.Command) || command.Command.Length > 2000
            || string.IsNullOrWhiteSpace(command.WorkingDirectory) || command.WorkingDirectory.Length > 4096)
            throw new ArgumentException("An exact command and working directory are required.");
    }

    private static void ValidateDependencyCommands(DependencyEvidence evidence)
    {
        if (evidence.Commands is not { } items) return;
        if (items.IsDefault || items.Length > 256 || items.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Command evidence must retain distinct ordered execution items.");
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId) || item.ItemId.Length > 512 || !Enum.IsDefined(item.State))
                throw new ArgumentException("A command execution item needs its backend identity and state.");
            RequireDependencyCommand(new(item.Command, item.WorkingDirectory));
        }
    }

    private static DependencyCommandState DependencyCommandCondition(DependencyRequirement requirement, DependencyEvidence evidence)
    {
        if (requirement.CommandSuccess is not { } expected) return DependencyCommandState.Completed;
        if (evidence.Observation.State != CommitmentTaskState.Completed || evidence.Commands is not { } items)
            return DependencyCommandState.Unknown;
        // A later retry supersedes an earlier result, including an unresolved retry.
        var last = items.LastOrDefault(i => i.Command == expected.Command && i.WorkingDirectory == expected.WorkingDirectory);
        if (last == null) return DependencyCommandState.Unknown;
        if (last.State == DependencyCommandState.Completed && last.ExitCode == 0) return DependencyCommandState.Completed;
        return last.State is DependencyCommandState.Failed or DependencyCommandState.Declined
            || last.State == DependencyCommandState.Completed && last.ExitCode is not null and not 0
            ? DependencyCommandState.Failed : DependencyCommandState.Unknown;
    }
}
