namespace Assistant.Core;

public sealed partial class DurableConversationLedger
{
    private static void ValidateObservationIdentity(OperationRecord operation, OperationObservation observation)
    {
        if (observation.TargetKey != operation.TargetKey || observation.Task != operation.Task)
            throw Error(LedgerErrors.IdentityMismatch, "The observation targets a different host, task, turn, profile or target.");
    }

    private static OperationStage MergeOperationStage(OperationStage current, OperationStage incoming,
        bool reconciliation, bool directReceipt)
    {
        if (incoming is not (OperationStage.Accepted or OperationStage.Running or OperationStage.Completed
            or OperationStage.Failed or OperationStage.Unknown or OperationStage.Interrupted))
            throw Error(LedgerErrors.InvalidTransition, "This is not an external operation observation.");
        if (current is OperationStage.Queued or OperationStage.CancelledBeforeDispatch)
            throw Error(LedgerErrors.InvalidTransition, "An undispatched operation cannot accept external observations.");
        if (current == incoming) return current;
        // Late direct responses cannot resolve an uncertainty discovered by an independent observer.
        if (current == OperationStage.Unknown)
        {
            if (reconciliation) return incoming;
            if (directReceipt) return current;
            throw Error(LedgerErrors.InvalidTransition, "An unknown operation requires explicit reconciliation.");
        }
        if (current is OperationStage.Completed or OperationStage.Failed or OperationStage.Interrupted)
        {
            if (directReceipt || incoming is OperationStage.Accepted or OperationStage.Running or OperationStage.Unknown)
                return current;
            throw Error(LedgerErrors.Conflict, "Conflicting terminal observations need explicit investigation.");
        }
        if (current == OperationStage.Running && incoming == OperationStage.Accepted) return current;
        if (directReceipt && incoming == OperationStage.Unknown && current is OperationStage.Accepted or OperationStage.Running)
            return current;
        return incoming;
    }
}
