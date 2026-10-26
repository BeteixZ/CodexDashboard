using System.Collections.Immutable;
using Assistant.Core;

internal static partial class DependentWorkTests
{
    private static readonly DependencyCommandRequirement TestCommand = new("dotnet test --no-restore", "C:\\synthetic-project");
    private static DependentWorkSpecification CommandSpec(F f) => Spec(f, [new(Predecessor, Fingerprint, CommandSuccess: TestCommand)]);
    private static Task<DependentWorkItem> CommandWork(DurableConversationLedger l, F f) =>
        Execute(l, f, "schedule_dependency", CommandSpec(f), TestCommand.Command + " succeeds, finish documentation");
    private static DependencyCommandResult Command(string id = "test-item", DependencyCommandState state = DependencyCommandState.Completed, int? code = 0) =>
        new(id, TestCommand.Command, TestCommand.WorkingDirectory, state, code);
    private static ImmutableArray<DependencyEvidence> CommandProof(F f, CommitmentObservationLease source,
        ImmutableArray<DependencyCommandResult>? commands, long sequence = 1) =>
        Evidence(f, source, sequence).SetItem(1, Proof(f, source, Predecessor, CommitmentTaskState.Completed, sequence) with { Commands = commands });

    private static async Task CommandCondition()
    {
        foreach (var items in new ImmutableArray<DependencyCommandResult>?[]
        { null, [], [Command() with { Command = "echo all tests passed" }], [Command() with { WorkingDirectory = "C:\\another-project" }],
            [Command(code: null)], [Command(state: DependencyCommandState.Unknown)], [Command(state: DependencyCommandState.Running)] })
        {
            await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f); var source = await Source(l);
            var result = await Evaluate(l, work, CommandProof(f, source, items), true);
            Check(!result.Ready && result.Lease == null && result.Work.Stage == DependentWorkStage.Waiting);
            var ready = await Evaluate(l, work, CommandProof(f, source, [Command()], 2), true);
            Check(ready.Ready && ready.Lease != null);
            Check(await l.IsDependentDispatchCurrentAsync(ready.Lease!));
            await l.DisposeAsync();
        }
        foreach (var command in new[] { Command(code: 1), Command(state: DependencyCommandState.Failed), Command(state: DependencyCommandState.Declined) })
        {
            await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f);
            var result = await Evaluate(l, work, CommandProof(f, await Source(l), [command]), true);
            Equal("predecessor_command_failed", result.Code); Equal(DependentWorkStage.NeedsDecision, result.Work.Stage);
            Check(result.Lease == null); await l.DisposeAsync();
        }
    }
    private static async Task CommandLastAttempt()
    {
        foreach (var last in new[] { Command("retry", code: 2), Command("retry", code: null), Command("retry", state: DependencyCommandState.Running), Command("retry") })
        {
            await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f); var source = await Source(l);
            var items = ImmutableArray.Create(Command(), last, Command("unrelated", code: 3) with { Command = "echo something else" });
            var result = await Evaluate(l, work, CommandProof(f, source, items), true);
            Equal(last.State == DependencyCommandState.Completed && last.ExitCode == 0, result.Ready);
            await l.DisposeAsync();
        }
        await using var retry = new F(); var ledger = await retry.Open(); var w = await CommandWork(ledger, retry);
        Check((await Evaluate(ledger, w, CommandProof(retry, await Source(ledger), [Command(code: 1), Command("retry")]), true)).Ready);
        await ledger.DisposeAsync();
    }
    private static async Task CommandOrdering()
    {
        await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f); var source = await Source(l);
        var success = CommandProof(f, source, [Command()], 1);
        Check((await Evaluate(l, work, success)).Ready);
        Check(!(await Evaluate(l, work, CommandProof(f, source, [], 2))).Ready);
        Equal("evidence_out_of_order", (await Evaluate(l, work, success, true)).Code);
        var current = await Source(l); Check(!(await Evaluate(l, work, success, true)).Ready);
        var lease = (await Evaluate(l, work, CommandProof(f, current, [Command()]), true)).Lease!;
        await Change(l, f, work, "cancel"); Check(!await l.IsDependentDispatchCurrentAsync(lease));
        await l.DisposeAsync();
    }
    private static async Task CommandAuthorization()
    {
        await using var f = new F(); var l = await f.Open();
        await Throws<ArgumentException>(() => Execute(l, f, "schedule_dependency", CommandSpec(f), "finish documentation"));
        var work = await CommandWork(l, f); var source = await Source(l);
        var lease = (await Evaluate(l, work, CommandProof(f, source, [Command()]), true)).Lease!;
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.NotDispatched, "synthetic before write cancellation");
        work = await Change(l, f, work, "update", "different instructions");
        Check(work.Requirements.Single().CommandSuccess == TestCommand);
        work = await Change(l, f, work, "update", requirements: [new(Predecessor, Fingerprint)]);
        await l.DisposeAsync(); l = await f.Open();
        Equal(3L, (await l.GetDependentWorkAsync(Identity)).WorkItems.Single().Version);
        Check((await l.GetDependentWorkAsync(Identity)).Attempts.Single().Evidence[1].Commands!.Value.Single() == Command());
        await l.DisposeAsync();
    }
    private static async Task CommandWorkChain()
    {
        await using var f = new F(); var l = await f.Open(); var parent = await Create(l, f); var source = await Source(l);
        var lease = (await Evaluate(l, parent, Evidence(f, source), true)).Lease!;
        var result = Target with { TurnId = "tested-result" };
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "accepted", result);
        parent = await l.ObserveDependentCompletionAsync(Identity, parent.Id, Proof(f, source, result, CommitmentTaskState.Completed, 2).Observation);
        var spec = Spec(f, [new(WorkItemId: parent.Id, WorkVersion: parent.Version, CommandSuccess: TestCommand)]) with { ContinueAfterWorkId = parent.Id };
        var child = await Execute(l, f, "schedule_dependency", spec, TestCommand.Command + " then finish documentation");
        var noProof = ContinuationEvidence(f, source, result, 3);
        Check(!(await Evaluate(l, child, noProof, true)).Ready);
        var exact = ContinuationEvidence(f, source, result, 4);
        exact = exact.SetItem(1, exact[1] with { Commands = [Command()] });
        var dispatch = (await Evaluate(l, child, exact, true)).Lease!;
        Equal(result, dispatch.Target); await l.DisposeAsync();
    }
    private static async Task CommandRecovery()
    {
        await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f);
        var lease = (await Evaluate(l, work, CommandProof(f, await Source(l), [Command()]), true)).Lease!;
        await l.DisposeAsync(); l = await f.Open();
        var saved = await l.GetDependentWorkAsync(Identity);
        Equal(DependentDispatchStage.Unknown, saved.Attempts.Single().Stage);
        Equal(Command(), saved.Attempts.Single().Evidence[1].Commands!.Value.Single());
        Check(!(await Evaluate(l, saved.WorkItems.Single(), CommandProof(f, await Source(l), [Command()]), true)).Ready);
        await l.FinishDependentDispatchAsync(lease, DependentDispatchStage.Accepted, "exact late acceptance", Target with { TurnId = "actual-result" });
        await l.DisposeAsync(); l = await f.Open();
        Equal(1, (await l.GetDependentWorkAsync(Identity)).Attempts.Length); await l.DisposeAsync();
    }
    private static async Task CommandCorruption()
    {
        foreach (string edit in new[] { "schema", "missing", "exit", "directory", "turn" })
        {
            await using var f = new F(); var l = await f.Open(); var work = await CommandWork(l, f);
            await Evaluate(l, work, CommandProof(f, await Source(l), [Command()]), true); await l.DisposeAsync();
            await f.Rewrite(root =>
            {
                if (edit == "schema") root["schemaVersion"] = 10;
                else
                {
                    var evidence = root["dependentAttempts"]![0]!["evidence"]![1]!;
                    if (edit == "missing") evidence.AsObject().Remove("commands");
                    else if (edit == "turn") evidence["observation"]!["task"]!["turnId"] = "other-turn";
                    else evidence["commands"]![0]![edit == "exit" ? "exitCode" : "workingDirectory"] = edit == "exit"
                        ? System.Text.Json.Nodes.JsonValue.Create(1) : System.Text.Json.Nodes.JsonValue.Create("C:\\other");
                }
            });
            await Error(LedgerErrors.CorruptStorage, () => f.Open());
        }
    }
}
