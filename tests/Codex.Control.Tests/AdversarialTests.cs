using System.Text.Json;
using Codex.Control;

internal static class AdversarialTests
{
    internal static async Task RunAsync(OwnedServerConfiguration config, DateTimeOffset now, Action<string, bool> check)
    {
        CreateOwnedTask create = new("sample", config.Projects["sample"], "synthetic-model", "on-request", "workspace-write");
        var mismatchRpc = new FakeRpc(config);
        await using (var client = new OwnedAppServerClient(config, mismatchRpc, () => now, TestProvenance.New(config)))
        {
            await client.InitializeAsync();
            mismatchRpc.Override = (method, parameters, _) => Task.FromResult(method != "thread/start" ? mismatchRpc.Reply(method, parameters)
                : FakeRpc.J(new { thread = FakeRpc.Thread("synthetic-foreign-policy", create.Cwd), cwd = create.Cwd,
                    model = create.Model, approvalPolicy = "never", approvalsReviewer = "user", sandbox = new { type = "dangerFullAccess" } }));
            var result = await client.CreateAsync(Guid.NewGuid(), create);
            check("permission mismatch does not establish owned handle", result.Task == null && result.Receipt.State == ControlState.Unknown);
            check("permission mismatch never starts followup work", mismatchRpc.Calls.All(x => x.Method != "turn/start"));
        }
        var rpc = new FakeRpc(config);
        await using var owned = new OwnedAppServerClient(config, rpc, () => now, TestProvenance.New(config));
        await owned.InitializeAsync();
        var task = (await owned.CreateAsync(Guid.NewGuid(), create)).Task!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.Override = (method, parameters, _) =>
        {
            if (method == "thread/name/set") { entered.SetResult(); return release.Task; }
            return Task.FromResult(rpc.Reply(method, parameters));
        };
        var rename = owned.RenameAsync(Guid.NewGuid(), task, owned.Snapshot(task).Revision, "synthetic pending rename");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            bool rejected = false;
            try { await owned.ArchiveAsync(Guid.NewGuid(), task, owned.Snapshot(task).Revision); }
            catch (ControlException ex) { rejected = ex.Code == "thread_control_in_flight"; }
            check("metadata changes on one task cannot race", rejected);
        }
        finally { release.TrySetResult(FakeRpc.J(new { })); await rename; }
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.Override = (method, parameters, _) => method == "turn/start" ? late.Task : Task.FromResult(rpc.Reply(method, parameters));
        var oldSubmission = owned.StartTurnAsync(Guid.NewGuid(), task, owned.Snapshot(task).Revision, "synthetic old session");
        rpc.Disconnect();
        var replacement = new FakeRpc(config);
        string observedTurn = Guid.NewGuid().ToString();
        replacement.Threads[task.Identity.ThreadId] = FakeRpc.Thread(task.Identity.ThreadId, create.Cwd, FakeRpc.Turn(observedTurn, "completed"));
        await owned.ReconnectForReadAsync(replacement);
        await owned.ReconcileAsync(task);
        late.TrySetResult(FakeRpc.J(new { turn = FakeRpc.Turn("late-old-turn", "inProgress") }));
        var oldReply = await oldSubmission;
        check("old epoch response cannot supersede reconciled task state", oldReply.State == ControlState.Unknown
            && owned.Snapshot(task).CurrentTurnId == observedTurn && owned.Snapshot(task).State == OwnedTaskState.Completed);
        check("reconnect does not move old write to replacement transport", replacement.Calls.Select(x => x.Method)
            .SequenceEqual(["initialize", "initialized", "thread/read"]));

        var closingRpc = new FakeRpc(config);
        var closing = new OwnedAppServerClient(config, closingRpc, () => now, TestProvenance.New(config));
        await closing.InitializeAsync();
        var closingTask = (await closing.CreateAsync(Guid.NewGuid(), create)).Task!;
        var closingRelease = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        closingRpc.Override = (_, _, _) => closingRelease.Task;
        var closingCall = closing.StartTurnAsync(Guid.NewGuid(), closingTask, closing.Snapshot(closingTask).Revision, "synthetic shutdown");
        await closing.DisposeAsync();
        closingRelease.TrySetException(new RpcTransportException(true, "synthetic_closed"));
        var closingResult = await closingCall.WaitAsync(TimeSpan.FromSeconds(5));
        check("late transport failure after disposal still returns unknown receipt", closingResult.State == ControlState.Unknown);

        var timeoutConfig = config with { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        var timeoutRpc = new FakeRpc(timeoutConfig);
        await using var timeoutClient = new OwnedAppServerClient(timeoutConfig, timeoutRpc, () => now, TestProvenance.New(timeoutConfig));
        await timeoutClient.InitializeAsync();
        var timeoutTask = (await timeoutClient.CreateAsync(Guid.NewGuid(), create)).Task!;
        var ignoredCancellation = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        timeoutRpc.Override = (_, _, _) => ignoredCancellation.Task;
        var timedOut = await timeoutClient.StartTurnAsync(Guid.NewGuid(), timeoutTask,
            timeoutClient.Snapshot(timeoutTask).Revision, "synthetic unresponsive transport").WaitAsync(TimeSpan.FromSeconds(5));
        ignoredCancellation.TrySetResult(FakeRpc.J(new { turn = FakeRpc.Turn("late-timeout", "inProgress") }));
        check("client bounds an injected transport that ignores cancellation", timedOut.State == ControlState.Unknown
            && timeoutClient.Snapshot(timeoutTask).State == OwnedTaskState.Unknown);
        check("timed-out transport is not retried", timeoutRpc.Calls.Count(x => x.Method == "turn/start") == 1);

        foreach (string fault in new[] { "malformed", "transport", "rejection" })
        {
            var terminalRpc = new FakeRpc(config);
            await using var terminalClient = new OwnedAppServerClient(config, terminalRpc, () => now, TestProvenance.New(config));
            await terminalClient.InitializeAsync();
            var terminalTask = (await terminalClient.CreateAsync(Guid.NewGuid(), create)).Task!;
            string terminalTurn = Guid.NewGuid().ToString();
            terminalRpc.Override = (_, _, _) =>
            {
                terminalRpc.Emit("turn/started", new { threadId = terminalTask.Identity.ThreadId, turn = FakeRpc.Turn(terminalTurn, "inProgress") });
                terminalRpc.Emit("turn/completed", new { threadId = terminalTask.Identity.ThreadId, turn = FakeRpc.Turn(terminalTurn, "completed") });
                return fault switch
                {
                    "transport" => Task.FromException<JsonElement>(new RpcTransportException(true, "synthetic_after_completion")),
                    "rejection" => Task.FromException<JsonElement>(new RpcRemoteException(-32603)),
                    _ => Task.FromResult(FakeRpc.J(new { malformed = true }))
                };
            };
            var terminal = await terminalClient.StartTurnAsync(Guid.NewGuid(), terminalTask, terminalClient.Snapshot(terminalTask).Revision, "terminal precedes bad receipt");
            check("observed completion survives late " + fault + " response", terminal.State == ControlState.Completed
                && terminalClient.Snapshot(terminalTask).State == OwnedTaskState.Completed);
        }
        var reconcileRpc = new FakeRpc(config);
        await using var reconcileClient = new OwnedAppServerClient(config, reconcileRpc, () => now, TestProvenance.New(config));
        await reconcileClient.InitializeAsync();
        var reconcileTask = (await reconcileClient.CreateAsync(Guid.NewGuid(), create)).Task!;
        string exactTurn = Guid.NewGuid().ToString();
        reconcileRpc.Override = (_, _, _) =>
        {
            reconcileRpc.Emit("turn/started", new { threadId = reconcileTask.Identity.ThreadId, turn = FakeRpc.Turn(exactTurn, "inProgress") });
            return Task.FromException<JsonElement>(new RpcTransportException(true, "synthetic_receipt_unknown"));
        };
        var uncertain = await reconcileClient.StartTurnAsync(Guid.NewGuid(), reconcileTask, reconcileClient.Snapshot(reconcileTask).Revision, "explicit reconciliation");
        reconcileRpc.Emit("turn/completed", new { threadId = reconcileTask.Identity.ThreadId, turn = FakeRpc.Turn(exactTurn, "completed") });
        check("ordinary late event cannot implicitly resolve unknown operation", uncertain.State == ControlState.Unknown
            && reconcileClient.GetOperation(uncertain.OperationId).State == ControlState.Unknown
            && reconcileClient.Snapshot(reconcileTask).State == OwnedTaskState.Completed);
        reconcileRpc.Override = null;
        reconcileRpc.Threads[reconcileTask.Identity.ThreadId] = FakeRpc.Thread(reconcileTask.Identity.ThreadId, create.Cwd, FakeRpc.Turn(exactTurn, "completed"));
        await reconcileClient.ReconcileAsync(reconcileTask);
        check("explicit exact-turn read can reconcile unknown operation", reconcileClient.GetOperation(uncertain.OperationId).State == ControlState.Completed);
    }
}
