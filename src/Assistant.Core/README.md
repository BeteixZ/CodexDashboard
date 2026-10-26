# Assistant persistence and execution core

This document explains the Core contracts and the feature gates introduced in successive revisions. Historical schema ranges and regression counts below describe those revisions; they are not a current whole-product acceptance summary. Current reader checks live in DurableConversationLedger.cs and its storage codecs. User-facing support limits are documented in [known limitations](../../docs/known-limitations.md).

`Assistant.Core` targets `net10.0` and has no UI, channel, model-provider, or native database dependency. It implements durable inbox, conversation events, generation revisions, authorization evidence, operations and outbox records. The host supplies the clock, encrypted storage protection and external adapters.

## Persistence decision and limits

The current repository had no SQLite package or established SQLite lifecycle. This first backend uses a bounded append-only journal of complete encrypted transaction snapshots, so it can be exercised without adding a native dependency. `IJournalStorage` permits replacing this backend with a transactional SQLite implementation later.

Each frame has a sequence number, bounded ciphertext length, checksum and commit marker. The journal uses an exclusive writer handle, `FileOptions.WriteThrough`, and `FileStream.Flush(true)`. A successful mutation returns only after that flush. No rename is used as a substitute for a durable write. `IJournalFaultInjector` exposes before-write, after-header, after-payload, before-flush and after-flush boundaries for tests.

**This is a bounded first backend, not an unlimited history database.** The plaintext snapshot remains limited to 8 MiB. Healthy physical journals approaching their configured capacity compact redundant full snapshots into an unchanged encrypted checkpoint; logical inbox IDs, operation outcomes, unknown results, history and active work are not deleted. The default data-bank limit is 512 MiB per bank. Two banks plus a separately bounded 1 MiB switch log and 56-byte anchor therefore have a steady physical upper bound of about 1 GiB, not 512 MiB in aggregate. Logical history growth can still reach the 8 MiB snapshot bound; retention, durable export, logical deletion and database migration remain separate work. A logical deletion alone would not erase copies from the other bank.

After an uncertain append failure, the open ledger refuses further writes. Reopening checks all frames and loads the last complete snapshot. An incomplete tail, invalid checksum, sequence gap or damaged header leaves the original file unchanged and the ledger read-only. Recovery plans then expose no automatically dispatchable operations or messages. There is intentionally no automatic truncate/rollback method: discarding a later control record could turn a previously dispatched operation back into an apparently queued one.

`ReadConversationDiagnosticsAsync` returns one locked snapshot of the requested
conversation and the whole ledger's logical capacity. The byte count measures
the validated in-memory state serialized by the current writer, not physical
file size or available disk space. It is cached after the first read and updated
from the existing buffer only after a successful append. An uncertain append
keeps the validated prefix's count and sets the read-only flag; it does not claim
to measure the potentially newer disk tail. Diagnostics perform no storage
write, include no other identity's records, and create no send permission.
`NearLimit` means at least 80% of the configured limit, not guaranteed admission
for any later transaction.

An existing empty file is treated as damaged rather than silently initialized as a new identity. A wrong encryption key fails closed and preserves the file. The tests prove application crash/restart behavior and flush ordering; they cannot prove every storage device's behavior under physical power loss.

### Physical checkpoint compaction

Small existing v1 files remain readable and writable without a content migration. When another frame approaches the capacity, `FileJournalStorage` creates fixed `.bank0`, `.bank1` and `.switch` side files using `CreateNew`. It copies the last committed ciphertext at its original logical sequence `N`, writes and flushes the selected checkpoint and activation record, then flushes a version/store-identity anchor into the original journal. Only after publication does it reclaim redundant physical v1 frames. The main file handle remains the exclusive writer lock for the entire lifetime, including every subsequent bank switch; old v1 implementations reject the new anchor instead of reopening old snapshots for writing.

Later compactions write only the inactive bank. A bank header binds the store UUID, unique bank generation and checkpoint base sequence. Frames continue at `N+1`; the snapshot's logical schema and DPAPI protection purposes do not change during compaction. The checkpoint is flushed and read back before its activation is appended to the switch journal and separately flushed. Readback checks copy integrity; it is not a substitute for the flush. No rename is used. The switch log has continuous epochs, exact checkpoint hashes and commit markers, and is never overwritten or compacted in this implementation.

Recovery uses only the bank named by the latest completely valid switch log. A damaged switch tail is read-only, even if an earlier record is valid. A damaged selected bank never falls back to the other bank. A partial inactive checkpoint can be ignored only when an intact switch log proves it was not published. Missing/empty required components, mismatched store or bank identities, and v2 artifacts beside an unpublished v1 anchor all fail closed. An interrupted migration may require explicit recovery rather than silently resuming v1. Faults during a switch block further mutations in that instance, so callers cannot perform external actions on the strength of an uncertain write.

`IJournalCompactionFaultInjector` exposes actual file-write/flush boundaries for migration, checkpoint copying, switch publication and anchor publication. The unchanged checkpoint and the next bounded frame must both fit; otherwise admission fails without weakening either frame or snapshot limits. The switch log also has a hard limit and is deliberately not silently rolled over.

The exact field layout and recovery ordering are documented in [JOURNAL_FORMAT.md](JOURNAL_FORMAT.md). After migration, the original file and all three side files form one storage unit; an offline backup must preserve the complete set consistently.

## Encryption and identity

```csharp
await using var ledger = await DurableConversationLedger.OpenAsync(
    new FileJournalStorage(journalPath),
    hostCurrentUserDpapiProtector,
    new SystemClock());
```

The host must supply `IStateProtector` with authenticated encryption. A Windows adapter should use current-user DPAPI, with the provided purpose bytes as entropy. The library provides no plaintext fallback or default implementation. Production must not use the test AES keys or a pass-through protector. All identity, source IDs, message bodies, operation arguments, receipts and conversation data are inside the protected snapshot; no plaintext search index is written. The core cannot verify whether a third-party protector honestly implements its contract.

Every record is scoped to `ConversationIdentity(UserId, BindingId, ConversationId)`. These are stable IDs, not rotating credentials. Read and mutation methods require the full identity; source message IDs and idempotency keys deduplicate only within that identity. Task references include `HostId`, `ThreadId`, optional `TurnId` and `ProfileId`; reports and events retain their task reference.

## Inbox and generation flow

```csharp
await ledger.ReceiveAsync(identity, sourceId, body,
    MessageTrust.AuthenticatedUser, occurredAt);
var lease = await ledger.BeginGenerationAsync(identity, [sourceId]);

// Model work happens outside the ledger lock.
await ledger.PrepareReplyAsync(lease, replySegments);
var messages = await ledger.EnqueuePreparedReplyAsync(lease);
```

`ReceiveAsync` atomically records the inbox item, conversation event and incremented revision. Duplicate content with the same source ID returns the existing record without advancing the revision; conflicting content is rejected. Grouped inputs preserve each original message and timestamp.

A new message supersedes active older generations and their pending reply segments. Preparation and dispatch require the current revision. Already sending messages cannot be unsent: their eventual factual receipt can still be recorded, while the remaining old segments are blocked. Interrupted, unanswered messages remain available in the recovery plan so a later generation can include their constraints.

Preparing a reply persists its segments before outbox handoff. A crash between preparation and handoff resumes the handoff without asking the model to regenerate. Handoff is idempotent. A generation interrupted before preparation returns its inbox items to resumable state after restart.

Only the authenticated channel adapter may mark an incoming message `AuthenticatedUser`. Tool results, reports, memory, notifications and attached content must use untrusted/context paths. `AppendContextAsync` cannot create a trusted-user event.

## Authorization and operations

```csharp
var authorization = await ledger.AuthorizeCurrentUserRequestAsync(
    identity, sourceId, action, exactTargetKey, exactArgumentsJson,
    literalUserExcerpt, taskReference);
var operation = await ledger.QueueOperationAsync(
    identity, authorization.Id, stableIdempotencyKey);

var dispatch = await ledger.BeginOperationDispatchAsync(identity, operation.Id);
// Only after the call above succeeds may the host invoke the external system.
await externalAdapter.ExecuteAsync(dispatch);
await ledger.RecordOperationObservationAsync(
    identity, operation.Id, OperationStage.Accepted, receiptEvidence);
```

`AuthorizeCurrentUserRequestAsync` is a trusted host boundary, never a model tool. The host must first verify that the current user explicitly requested this action. The ledger checks the authenticated inbox source, current revision, literal evidence excerpt, identity and exact action/target/arguments binding. It does **not** infer the meaning of natural language or decide whether an excerpt actually authorizes a command. That decision belongs to the deterministic host authorization policy.

The durable operation copies the exact authorized arguments. Reusing an idempotency key for another authorization is rejected. Queue cancellation requires a separate current-user authorization targeting the exact queued operation; it cannot claim to interrupt a dispatched operation.

`BeginOperationDispatchAsync` saves `Dispatching` before returning. On reopen, `Dispatching` becomes `Unknown`, even if the crash actually happened just before the network call. This conservative state must be reconciled; it cannot be sent again. `Accepted` and `Running` remain distinct from `Completed`. A later observation can resolve `Unknown` only through the explicit reconciliation path. A new input revision blocks a still-queued operation from automatically using outdated context.

Control adapters should use the `OperationObservation` overload, which requires the exact immutable target and task reference, including host, turn and profile. A wrong target or turn is rejected. The simpler stage/evidence overload is a trusted internal path for a caller that already resolved the operation identity.

Independent events can arrive before the direct dispatch receipt. Their merge is monotonic: late `Accepted` cannot lower `Running` or a terminal state. A late direct receipt also cannot change `Unknown` without explicit reconciliation. The invocation still completes and records the original direct result as an event; its returned result includes the authoritative operation state when it differs. Conflicting terminal observations require investigation instead of silently replacing one another.

## Provider-neutral tool loop

```csharp
var loop = new ToolAgentLoop(ledger, modelAdapter, ToolCatalog.Initial(),
    toolExecutor, deterministicUserAuthorizationPolicy,
    new ToolLoopOptions(), replySegmenter);
var result = await loop.RunAsync(identity, currentSourceMessageId,
    groupedSourceMessageIds, cancellationToken);
```

`IToolModel` receives the current inbox item, provenance-bearing conversation events, explicitly enabled JSON schemas and structured tool observations. Its response contains tool calls or final reply segments. The host can implement Responses function calls or strictly parsed action JSON; this library does not infer provider support.

The initial catalog defines `list_tasks`, `read_task`, `read_report`, `send_to_task`, `cancel_queued`, `remember` and `forget`. These are schemas and contracts, not working backend implementations. `IToolExecutor.GetCapability` must disable any unsupported capability. Unknown tools, extra or duplicate object properties, wrong types and missing required parameters fail closed. Valid arguments are canonicalized before deduplication.

The model only proposes calls. Before any side effect, `IUserAuthorizationPolicy` must deterministically verify the **current authenticated inbox message**, exact target and literal request excerpt. Tool output, reports, memory, attachments and older inputs provide no authority. A missing grant produces a persisted `needs_confirmation` observation and makes no external call. The core does not claim that a model-written final reply is itself permission.

Call IDs are durable within the source message. The same ID cannot change tool or arguments. Side effects additionally deduplicate by source ID, tool name and canonical arguments, so changing a model call ID or JSON property order does not repeat the effect. A queued operation can resume its first dispatch after revalidating the same authorization; a previously dispatched/unknown operation is never replayed.

The model-turn and tool-call budgets are durable per source message and survive restart. Model and tool calls have deadlines and bounded concurrency. An adapter that ignores cancellation keeps its slot until it actually completes, rather than permitting unlimited replacement calls. A read-only exception becomes failure, and an exception or timeout after side-effect dispatch becomes unknown. Production adapters should populate `ToolExecutionResult.TargetKey` and `Task` for direct receipt identity validation.

A newer revision cancels the old loop and blocks reply preparation. An external effect already dispatched still records its factual/unknown receipt. Reply preparation and outbox handoff occur only after the final model answer. `IReplySegmenter` transforms the model's at most three conversational bubbles into actual channel send segments **before** persistence; each resulting outbox record must correspond to exactly one network send. The ledger limits the resulting segment count independently.

`ToolLoopState.Completed` means that the loop prepared a response and handed it to the outbox. It does not mean that the channel accepted it, a phone displayed it, or a Codex task completed. Those conclusions require their own receipts.

`WaitForExternalDrainAsync` waits for the actual model/tool tasks after the caller has cancelled the loop. A timeout or cancelled await is not proof that an adapter has stopped. Receiver or runtime ownership must not be released until this real drain has completed.

## Outbox flow

```csharp
var sending = await ledger.BeginOutboxSendAsync(identity, message.Id);
var attempt = new OutboxAttemptLease(sending.Identity, sending.Id,
    sending.SendAttempts, sending.AttemptTrustedInputRevision);
// Channel I/O happens after the persisted Sending state.
await channel.SendAsync(sending.Body);
await ledger.RecordOutboxAttemptOutcomeAsync(
    attempt, OutboxStage.Accepted, apiReceipt);
```

- Restart converts `Sending` to `Unknown`.
- `Unknown` never returns to pending automatically.

The Windows coordinator captures an `OutboxAttemptLease` from the durable send
record: identity, outbox ID, attempt number and original trusted input revision.
`RecordOutboxAttemptOutcomeAsync` permits the definite result of that exact
still-running call to reconcile its own `Unknown` record. The actual transport
call and the receipt commit remain one tracked task until real drain completes.
A cancelled waiter cannot overwrite a concurrently committed definite receipt.
Repeated identical receipts do not append events or consume another budget;
conflicting definite receipts and stale attempts are rejected. No second network
call is made for reconciliation, and the original input/output dependencies stay
attached. A genuinely missing result remains unknown, including after restart.
This narrowly fenced outbox path does not relax operation/turn reconciliation
rules or prove that a phone displayed the message.

- `Accepted` records channel API acceptance; it does not prove phone delivery.
- A definitively rejected message may be retried explicitly while its revision is current.
- Later segments wait for earlier segments to be accepted.
- Sending or retrying a response never queues or repeats its associated Codex operation.

## Independent notifications

`EnqueueNotificationAsync(identity, eventKey, body, task, source, segments)` atomically saves a data event and its actual send segments. Permitted sources are `Notification` and `ToolResult`. It creates no inbox entry, generation, operation or authorization. The event key is idempotent within the complete conversation identity; changed body, task, source or segmentation under the same key is rejected so the original evidence is preserved.

Notification outbox entries use `Kind = Notification`, `GenerationId = null` and `NotificationEventId` pointing to the event/segment manifest. The recorded revision is provenance, not permission to send: a newer user input supersedes ordinary response segments but leaves notifications intact. Segment ordering applies within the original notification event. An unknown first segment blocks its later segments without blocking unrelated notification groups.

API acceptance writes a normal assistant conversation event that references the original notification and its task. Pending notification records recover for their original identity; sending recovers as unknown and is not replayed. `RetryRejectedNotificationsAsync` is an explicit channel-recovery action for definitely rejected notifications only. It neither retries unknown outcomes nor revives old conversational replies.

The snapshot version remains v1 with additive fields. Missing `Kind` defaults to `ConversationReply`, and old non-null generation GUIDs deserialize as nullable GUIDs without migration. Notification reference, identity, manifest and segment corruption fail closed. The Windows coordinator uses session cancellation for notifications and revision cancellation for conversational replies, checks binding identity before the actual send, and prioritizes pending conversational replies between notification segments.

### Capture before preparing notification text

When a model prepares notification wording, the host must persist the facts **before** queuing or invoking that model:

```csharp
var captured = await ledger.CaptureNotificationAsync(identity, eventKey,
    contextBody, publicPayloadJson, task, maximumPreparationAttempts: 3);
var claim = await ledger.BeginNotificationPreparationAsync(identity, captured.Draft.Id);
try
{
    var body = await readOnlySummaryModel.PrepareAsync(captured.Draft.PayloadJson);
    await ledger.FinalizeNotificationAsync(claim, body, actualChannelSegments);
}
finally { await ledger.EndNotificationPreparationAsync(claim); }
```

Capture atomically writes the source context event and `DurableNotificationDraft`, with no fake user message or generation. The original context body is immutable; finalized model text is saved separately as `PreparedBody` and actual outbox segments. The payload must be a strict JSON object of depth at most 16, with no duplicate fields, comments or trailing commas. Its character bound is `MaximumBodyCharacters` (default 128 Ki characters). The host must construct this payload from public notification/report fields and exclude credentials or transport metadata; JSON validation alone cannot identify secrets.

`BeginNotificationPreparationAsync` durably claims one draft and increments its attempt count before model work. Only one claim is active. `EndNotificationPreparationAsync` releases the claim without refunding the attempt; a late result cannot finalize a released, recovered or replaced claim. A successful finalization is atomic with all actual outbox segments, and repeating that finalization with the same claim/body/segments returns the existing IDs.

Restart converts `Preparing` to `Captured` and preserves its attempt count. `GetPendingNotificationDraftsAsync` and the recovery plan return only captured drafts for the requested identity, including exhausted drafts so the host can choose a fallback. An exhausted budget blocks another model preparation. `FinalizeNotificationFallbackAsync` can finalize factual host text from `Captured` even after exhaustion; an active preparation must first release its claim. Finalized notifications, including those whose sends are pending or unknown, never reenter model preparation.

The configured budget is 1–16 attempts per draft (default 3). `MaximumPendingNotificationDrafts` bounds unfinished drafts across the entire ledger (default 128, allowed 1–1024); duplicate capture can still return the original at capacity. Finalized history is retained under the snapshot/file byte bounds. An identical key with changed context, payload, task, source or budget is rejected. A legacy final-only notification key cannot be recaptured as a draft.

Old v1 snapshots omit the new draft array and restore it as empty. Draft/event identity, payload, stage, claim, attempt and segment associations are checked on restore. A damaged or uncertain journal stays read-only, exposes no dispatchable draft and is never automatically truncated. The host owns cancellation and real drain of its summary model calls, and must wake the delivery coordinator after successful finalization.

## Concurrency and host responsibilities

### Persisted dialogue context

`ConversationSnapshot.Context` and `ReadConversationContextAsync` expose a stable-identity context with a dialogue epoch, boundary revision, work focus, recent notification, task selection and approval context. The slots are independent data. Reading task B does not change focus A; capturing a notification updates only the notification slot. Acceptance records an outbox ID without asserting phone delivery.

Trusted host fast paths may call `SetWorkFocusAsync` and `OpenTaskSelectionAsync` after validating the current authenticated user request. Selection candidates freeze canonical identities and numbering for at most one hour, with at most 40 choices. `SelectTaskCandidateAsync` requires the exact selection ID, a current user source and a literal excerpt. Focus selections update focus; inspection selections return a target without changing focus. Neither creates authorization or executes the earlier request. A completed selection result is kept for idempotent recovery of the same input. Expired or old-dialogue selections are rejected.

`RecordPendingApprovalAsync` stores an exact backend request/turn and source event as context only, with a bounded expiry and dialogue epoch. Actual approval still requires a separately verified live backend request, current user authority and supported capability. Resolving one approval ID does not clear another or alter a task selection.

`BeginNewDialogueAsync` is a trusted host operation for an explicit current-user reset. It atomically increments the epoch, clears temporary context, writes a boundary event, cancels prior unprocessed inputs and invalidates old conversation generation/reply segments. It preserves historical events, registered operations, independent notifications and the stable conversation identity. It does not delete long-term memory or claim to interrupt running tasks. Repeating the same current reset input is idempotent. The model loop filters automatic prompt events to the boundary revision and receives the separate `ToolModelRequest.ConversationContext`; explicit historical report queries remain host capabilities.

The v1 schema adds a default-empty context array. Restore validates source identities, authenticated focus/selection provenance, exact notification and approval associations, bounds and epochs. No persisted context object grants permission, and `AuthorizeCurrentUserRequestAsync` still requires the current input revision.

### Independent inputs and read-only FIFO

`QueueUnprocessedInputsAsync` claims earlier independent inputs in the current dialogue as `InputWorkItem` records, retaining the exact source message and revision. Current-message grouping is settled first, and an input already claimed by work cannot join an ordinary generation. Sources already covered by the current grouped reply cannot be claimed again while it waits to send. Registered operation sources and inputs with an accepted, uncertain or definitely rejected sending attempt are excluded: their existing operation/outbox history remains authoritative. An ordinary prepared reply superseded before any send can be reconsidered. The default capacity is 128 automatically processable work items per identity; the original inbox remains intact if the capacity transaction fails. The coordinator uses `QueueAvailableInputWorkAsync` to claim only available capacity and retain the rest for subsequent batches; it wakes again after an outbox outcome is durably committed, so a full backlog does not prevent existing FIFO work from progressing.

`BeginReadOnlyWorkAsync` starts the earliest queued source and records its work version, dialogue epoch and `ReadAtRevision`. Its generation revision still identifies the original source. `ToolAgentLoop.RunReadOnlyWorkAsync` advertises only registered read-only tools and separately refuses any model-proposed effect before invoking authorization or the executor. The ledger execution boundary also forbids effects in a deferred generation. Such work records `RequiresConfirmation` and `NeedsConfirmation`; neither the model, a report, a remembered request nor a task selection can revive old permission. A current complete user request must establish any new grant through the ordinary path.

`ToolModelRequest.DeferredWork` identifies this restricted mode. `LatestInput` carries the most recent input separately from the bounded event context so large reports cannot hide the latest constraint. Host model adapters must include these fields, keep their source/trust labels, and avoid assigning a new focus to an old ambiguous pronoun. `ToolExecutionContext.InputWorkId` and `ReadAtRevision` allow host executors to distinguish these queries. The host may permit a registered read-only descriptor with an old source only after `VerifyReadOnlyWorkExecutionAsync` succeeds. Mutation paths must keep their existing current-source checks; the verification method is never mutation authority.

Tool-call identities/results are separated by work version, while model/tool budgets continue accumulating against the original source across cancellation and restart. Retrying a read therefore neither refunds its budget nor silently reuses a past result as a fresh observation. Exhaustion or model failure leaves a queryable terminal work record. Already registered unknown effects are never retried through this worker.

`DeferredReply` outbox records reference their real generation, work ID and read revision. Preparing and enqueueing actual channel segments are atomic as for ordinary replies. A new input cancels the read and invalidates remaining segments. Work with no send attempt can be queued again against the new constraints; any accepted/uncertain/definitely rejected attempt prevents automatic replacement. A late receipt remains factual history and cannot reopen remaining segments. Restart requeues an interrupted read, hands off an existing prepared manifest, and converts sending to unknown without replay. New-dialogue boundaries cancel unfinished old work without changing completed history or unknown operation facts.

Missing work fields in old v1 snapshots default to empty/null. Restore checks exact source, generation, epoch, version, read revision, complete segment manifests and send-attempt associations. The fourth coordinator worker runs this FIFO after the current fast/normal response attempt; fixed queries still run independently. Session cancellation and actual external drain include the new worker.

One asynchronous coordinator serializes local state transactions. Model calls, channel calls and Codex calls occur outside that lock. Storage also rejects competing writers, and the supplied clock makes timestamps testable. Public snapshots use immutable records and arrays.

The host still owns the single real WeChat receiver, model capability probes, deterministic fast paths, tool adapters, stop-priority routing, UI dispatch, production DPAPI adapter, corruption recovery decisions, retention and actual phone/Codex acceptance. Do not mark all P2 behavior complete based on this library alone.

The separate Windows `DurableMessageCoordinator` supplies bounded wakeup workers for fixed queries, normal generation, deferred read-only input work and delivery. `ActivateSessionAsync` explicitly selects a stable user/binding/conversation and recovers only that identity. `AcceptAsync` returns after inbox persistence and never marks generation/delivery complete. Failure must not advance the channel's seen/cursor state. The owner injects a thread-safe identity provider and any UI dispatcher adapter itself.

Short-message merging defaults to a configurable one-second window and matching task identity. `DurableMessageOptions.CanGroup` lets the host reject unrelated topics; eligible earlier inputs excluded by the predicate enter the deferred read-only FIFO. The normal prompt context retains their individual provenance. Semantic grouping, exact target resolution and user-facing confirmation of old action requests remain host responsibilities.

Read-only fast handlers cannot authorize effects. The distinct trusted fast handler receives the ledger and current input so it can run explicit deterministic authorization/operation paths. Failed ordinary model turns may produce a neutral, durable fallback reply through `FailureReplyFactory`; this reply makes no claim that an earlier tool did not execute. Corrupt/read-only storage prevents a fallback write as well.

Delivery receives one outbox record and a `StillCurrent` predicate. The adapter must recheck that predicate immediately before its one actual send. `NotifyDeliveryAvailable` wakes pending records only; it does not retry rejected or unknown records. A sender that already began when a binding/revision changes may have an unknown result, which is kept as such. `DrainSessionAsync` has a bounded wait and can return false. The owner must retain its receiver lease and then await `WaitForSessionDrainAsync` before releasing ownership; the latter includes real model/tool/channel/fast-handler tasks that ignored cancellation. `DisposeAsync` alone is not an unconditional proof of real drain.

Run the standalone suite:

```powershell
dotnet run --project tests/Assistant.Core.Tests/Assistant.Core.Tests.csproj
```

## Durable reminders and exact-turn subscriptions

`CommitmentTools.Descriptors()` defines the strict `schedule`, `subscribe` and `change_commitment` tools.
The host resolves the literal current request, exact target and local time before authorizing the tool.
`ApplyCommitmentToolAsync` reads the arguments from the already-dispatching operation and verifies its
current authenticated inbox, ordinary generation, executing invocation and exact grant. Creating,
pausing, resuming, cancelling and updating a commitment each persist the new state and a completed local
mutation receipt atomically. Replaying that same operation only returns its saved receipt. The receipt
can prove a completed local transaction across a crash or late timeout; ordinary external effects keep
their existing Unknown recovery behavior. These commitments never authorize starting, continuing,
approving or interrupting a task later. Deferred input work cannot create or change them.

`TrackedWorkItem` is separate from the earlier `InputWorkItem` FIFO. The first supported slice includes
one-time reminders, exact host/profile/thread/turn subscriptions to completion, failure or a decision,
and bounded read-only inactivity checks. `GetCommitmentsAsync` returns work, schedule, subscription and
run records for listing and inspection. Cancellation of a commitment is not task cancellation.

Reminder creation stores an explicit local timestamp, timezone identifier, original inbox receipt time
and resolved UTC due time. `ResolveLocalTime` rejects a DST gap or repeated local time; the host must ask
for an unambiguous time. Relative expressions must be resolved from the original `ReceivedAt`, not a
later retry. A due time at most one minute late gets normal polling grace. Beyond that interval the
persisted missed policy either sends once, skips with an Expired record, or leaves NeedsDecision with
a factual notice. Deadline expiry prevents future sends. Recurrence and dependency-triggered execution
are intentionally outside this slice.

The host calls `AdvanceCommitmentsAsync` on its bounded timer and `ObserveCommitmentTaskAsync` for
verified task facts. Observations require an exact task/turn, Fresh and Connected, a non-Unknown state,
and an observation time within the last five minutes (at most thirty seconds of forward clock skew).
`LastActivityAt` is evidence of task activity, never the time a status read succeeded. Missing activity
keeps the prior anchor. Repeated unchanged observations do not append snapshots. A NoActivity trigger
only schedules a read: `BeginCommitmentCheckAsync` durably claims and counts it before host I/O, then
`CompleteCommitmentCheckAsync` records the outcome. An unusable result retries after thirty seconds,
up to the persisted budget; exhaustion leaves NeedsDecision and a factual inability-to-check notice.
Restart invalidates the old claim while retaining attempts. An activity-anchor change skips the old
run before considering a separate new trigger. No inactivity result claims a task is stuck.

Trigger consumption, run creation, context event and `DurableNotificationDraft.WorkRunId` linkage are
one transaction. Use `GetPendingCommitmentNotificationsAsync` to distinguish these factual drafts from
other host notification payloads. The host may finalize their factual body directly, splitting into
actual channel sends before finalization; no model is required. Existing draft preparation limits,
prepared outbox recovery and unknown-send rules still apply. A new input does not invalidate the
promise, whereas a binding switch stops that identity's host worker.

Quiet hours, rate limits, per-day limits and maximum delivery attempts are checked again by
`BeginOutboxSendAsync`, not just when the draft is prepared. A send reservation is persisted before I/O.
Accepted still means channel acceptance only. Unknown consumes the reservation and is never retried.
An explicit retry of a definitely rejected send consumes another attempt; actual segments of the
same first send share a reservation. Once a delivery budget is exhausted, the rejected record remains
queryable and cannot be silently replayed. The host must wake the delivery worker when quiet hours or
rate delay ends. It should choose candidates from `GetRecoveryPlanAsync().PendingOutbox`, which applies
the same policies, and avoid a busy loop if a policy changes between lookup and send.

Pause preserves pending records but invalidates active check/model claims; resume updates the run's
work version. Cancel or deadline expiry supersedes only unstarted Pending/Rejected sends. Sending and
Unknown keep their factual outcome. A late acceptance while a one-time reminder is paused is retained;
resuming that already accepted reminder marks it completed rather than sending it again.

For ordinary default task notices, pass `isDefaultTaskNotification: true` to capture or enqueue. The
ledger atomically rejects the capture with `notification_suppressed` when any explicit subscription
exists for that exact task/turn, including a paused/cancelled/completed subscription. The actual send
boundary repeats this check for notices queued before the preference changed. Other turns and
explicit approval/safety receipts are separate. Do not mark an approval prompt as a default status
notification merely to silence it. Existing already-Sending notices cannot be recalled.

Old v1 journals omit these collections and read them as empty; explicit null, orphaned records,
cross-identity associations and invalid local mutation proof fail closed. Logical history, deduplication,
Unknown outcomes and commitments are never pruned by this slice. Defaults bound active commitments to
128 and total run history to 4096, alongside the existing 8 MiB snapshot and journal bounds. Capacity
failure commits no partial trigger or draft. Long-term retention, recurring schedules, richer subscription
editing and production acceptance remain separate work. Exact required approval sharing is described below.

### Shared commitment budgets and actual read drain

`LedgerOptions.CommitmentBudgets` is trusted host configuration, never model-controlled arguments.
Defaults apply independently to each full user/binding/conversation identity across all of its
commitments: 60 read-check starts per rolling hour, at most two actual read checks in flight, 20
notification starts per rolling 24 hours and five per rolling minute, and 20 model-preparation starts
per rolling 24 hours. These are rolling UTC durations, not local calendar days. Per-work limits
continue to apply as well. The scope is only linked commitment runs: ordinary approval prompts,
control receipts and unrelated notification preparation are not silently hidden by this quota.

Each successful begin operation atomically appends a `CommitmentBudgetReservation` before returning
permission for external I/O. Unknown, cancellation after begin, a crash or a definite rejection never
refunds spent window budget. A retry of a rejected send charges again; later first-attempt pages of
that same notification share its original reservation. Clock rollback cannot erase future-dated
reservations. Reservations remain in protected history; no pruning or inferred historical charges
are performed. Old journals without the collection start with no recorded shared charges.

`budget_deferred` means the operation did not begin, its attempt counter did not increase, and the
existing pending record remains. Pending-check and outbox recovery queries exclude unavailable
shared budgets, so a bounded host timer can wake them when a slot or time window becomes available.
Model budget exhaustion blocks `BeginNotificationPreparationAsync`; factual fallback remains
available and pending commitment drafts remain visible for that purpose.

`GetCommitmentBudgetStatusAsync` returns read-only window usage/limits, the earliest currently known
window availability, and real in-flight read occupancy. An occupied read slot has no predicted finish
time. This query writes nothing and includes no conversation body or another identity's records.

In-flight reads are explicit reservations, not inferred from run stage. Pausing, cancelling or
updating a commitment invalidates its claim but does not pretend a noncooperative external read has
stopped. The host must put `ReleaseCommitmentCheckAsync(lease, CancellationToken.None)` in a finally
that runs only after the actual read task ends. Successful `CompleteCommitmentCheckAsync` also closes
that occupancy; the final release is idempotent. An ended read with no committed observation returns
its still-matching Checking run to PendingCheck after a thirty-second delay, preserving attempts.
Releasing an older claim closes only its old occupancy and cannot alter a newer run's result.
If the exact task becomes terminal while a read is still running, its occupancy remains until
the actual read ends; final release or process recovery marks that obsolete check Skipped.
Recovery closes the previous process's read occupancy without refunding any time-window charge.
This also lets a stopped binding recover a check in the same process after its actual worker drains.

### Sharing required approval notices with decision subscriptions

`EnsureRequiredDecisionNotificationAsync` is a host-only API for a verified, still-pending approval.
The host supplies its stable provider scope, public decision token, connection epoch, UTC expiry,
exact task turn, immutable detailed text and actual channel segments. These facts are never a tool,
user message, authorization or substitute for the control adapter's live approval checks.

The transaction ensures one independent notification event/outbox group and captures matching active
NeedsDecision work IDs/versions. Each run points to `SharedDecisionEventId`; the required notice has
no draft or `WorkRunId`. Thus subscription pause, cancellation, deadlines, quiet hours and commitment
budgets cannot suppress the necessary approval. Multiple subscriptions share the same actual sends.
Completing a run records channel acceptance only, never approval execution or phone delivery.

Repeated identical calls write nothing. A same-key conflict or a second key for the same provider,
epoch, token and exact target is rejected. Existing plain, direct notifications may be adopted after
the host re-verifies that exact live approval; their original event, segments and receipts remain.
Model-prepared or commitment-owned drafts cannot be promoted through this API. Old journals lacking
these optional fields are unchanged; invalid shared references, scope or acceptance proofs fail closed.

When all actual pages are Accepted, that receipt transaction completes every noncancelled linked run.
A subscription registered later may reuse the existing accepted receipt without sending again.
Sending recovery becomes Unknown as before; a new subscription or session never replays it. Explicit
channel recovery may retry a definitively Rejected page; Unknown requires exact receipt reconciliation.
No approval-context registration or grant is created by linking or delivery.

Run capacity does not block the independent required notice: captured subscriber/version facts remain
on its event and the result exposes deferred work IDs. Recovery and Advance may fill these exact
captured links when capacity permits; they do not infer later subscriptions or revive cancelled or
revised work. Snapshot/storage failures still stop the durable write; no unrecorded send is allowed.

Historical short decision notices are superseded only if their entire group never began sending.
Their original draft/event remain as cancelled audit history, and a late preparation claim cannot
finalize. If any send was attempted, its history and old run remain separate; an already-sent duplicate
cannot be retrospectively removed. The detailed approval still remains necessary and independent.

### Secondary-channel short wakes

Wake records are separate from Claw outbox messages and commitment runs. Only a Notification's
structured `ContextUnavailable` or `ContextExpired` rejection can become a source; receipt strings,
binding failures, generic rejection, Unknown and ordinary conversational replies never qualify.
`BeginOutboxSendAsync` captures the latest authenticated input revision for that specific attempt.
The wake key is the full conversation identity plus that input episode, independent of provider route.
New authenticated input atomically cancels Pending wakes; duplicate and untrusted input does not.
Late older Claw rejection cannot create a wake under the new episode.

The trusted host configures a nonsecret route GUID and validated time zone/quiet hours, then calls
`EnsurePendingWakeAsync`. Within the fixed five-second aggregation period it may add source references,
without modifying original event bodies, send receipts or authorization. No qualifying source remaining
cancels a pending wake; quiet hours merely defer it. Route changes/disable cancel only Pending records.
Cancelled, Accepted, Rejected, NotStarted and Unknown episode tombstones are never automatically rearmed.

The host must await its actual transport throttle before `BeginWakeSendAsync`, which rechecks route,
input, source pause/cancellation/deadline/quiet policy and global quiet hours. It atomically reserves
one attempt and the entire ledger's fixed limits: one start per rolling hour, three per rolling day,
and one actual request in flight. Denial consumes no attempt. These limits are separate from Claw
delivery budgets and are not model-controlled. Records remain protected by the normal journal and
bounded snapshot limits; storage/capacity failures cannot authorize an unrecorded send.

Only send `ChannelWakeRecord.ShortBody`. Record a structured Accepted/Rejected/Unknown/NotStarted
outcome; Accepted requires an actual platform receipt and does not prove phone delivery or delivery
of any Claw report. Outcome recording does not release real occupancy. In a finally after the actual
request has ended, call `ReleaseWakeSendAsync(lease, CancellationToken.None)`. Release without a receipt
records Unknown; no result refunds rolling quota. Recovery closes the former process's occupancy,
turning Sending into Unknown without replay. Exact reconciliation may resolve Unknown but never sends.
Configuring a different route cannot erase these facts. Missing new fields in version-one journals
mean empty wake history and unclassified old rejections, which cannot silently qualify.

This is a durable host mechanism for an already-configured secondary channel. It does not establish
that a real platform sending window or phone wake has been verified. Full report grouping/recovery
remains distinct from this short hint; no original outbox body is rewritten to simulate a merged send.

### Authenticated catch-up windows and lossless continuation (slices A and B)

`ReceiveAsync(..., catchUp: CatchUpInputOptions)` atomically records the authenticated input and
its backlog window before the host advances a channel cursor. HoldHistory and missing fresh channel
context immediately hold old notification sources. Any later authenticated input supersedes only
unstarted batch pages, including when an older caller omits catch-up options. Sending, Accepted and
Unknown facts stay intact. Windows are data/presentation fences, never authorization or task execution.

The host calls `PrepareCatchUpAsync` after receive and on same-identity recovery. It copies the remaining
source spans into immutable, bounded pages, with exact task/kind grouping and local record timestamps.
These timestamps describe when the ledger recorded the event, not an unverified business completion
time. The default limit is three pages of 900 UTF-16 characters each, including headings. Source text
is never summarized, trimmed or truncated, and a Unicode surrogate pair is never split. A long source
continues across pages and later authenticated windows; content beyond this window's limit remains in
the original record. Original Accepted pages and Accepted catch-up spans are excluded from continuation.
Any Sending or Unknown original or catch-up page blocks reassembly of that entire source event, even
when other ranges are definitely unsent. Original stages, bodies and receipts are never rewritten to
pretend a batch was the original send. The host must send each persisted page exactly, without cleaning
or repagination at the transport boundary.

Manifest version 2 binds each original outbox ID, full-body hash, captured send attempt, UTF-16 Start
and Length, run/version and actual delivery page. Recovery verifies bounds, scalar boundaries, source
and page order, nonoverlapping live coverage, and an exact rendering from the original text. Accepted
spans are independent coverage proofs; Unknown spans retain exclusive coverage across new inputs and
restarts. Rejected batches require a new authenticated input; repeated preparation cannot recreate
them. Old direct retry APIs cannot bypass captured-source holds.

Each distinct commitment run reserves one new attempt at its first actual send in a batch. Later pages
of that same batch reuse the charge; a new batch in a later window requires a new attempt. All new run
charges for a page are atomic and obey shared and per-work aggregate limits. Packing separates run
combinations that can never be reserved together because of an absolute quota or a work's minimum
interval. Temporary quota exhaustion remains a send-time gate: it consumes nothing and permits the
current ordinary reply. Quiet periods defer; changed/cancelled/expired source facts retire only
unstarted pages. A linked active run completes only when original Accepted pages plus Accepted spans
cover every original character. `CatchUpDeliveryOutboxId` records the acceptance that completed that
coverage; partial acceptance cannot complete the run, grant approval, or turn original Rejected pages
into Accepted.

`GetCatchUpProgress(snapshot)` and `GetCatchUpProgressAsync(identity)` use the same union calculation.
They expose per-event and per-original-page total, accepted and pending character counts, plus
Sending/Unknown flags. Remaining is total minus accepted, including uncertain and pending content;
it is not a claim that every remaining range is retryable. All acceptance figures describe API
acceptance, never phone delivery. The static helper permits UI labels from one immutable snapshot.

Required approvals are never ordinary batch members. Supply only freshly live-verified exact event
IDs through `verifiedRequiredDecisionEventIds`; the default empty set revokes presentation eligibility.
The host must include new required events created after the input as well as older window candidates,
and recheck live provider state at its real transport gate. Expiry or a dialogue reset between the
host read and preparation revokes stale qualification without rejecting the input. Foreign/nonapproval
IDs fail closed. Each current input permits at most one requeue of a definitely Rejected approval.
Current live required notices take priority, then eligible batch pages, ordinary replies, and other
notifications. Ineligible quiet/budget/offline notices do not permanently block the ordinary reply.
`BeginOutboxSendAsync` enforces this order independently of host sorting.

For the v1 host's reserved `owned-approval/` event key, an absent RequiredDecision marker means
unclassified approval history: no automatic original send, catch-up inclusion or wake is permitted.
The host must first reverify and call `EnsureRequiredDecisionNotificationAsync`. A plain event captured
but not yet included in a batch can be reclassified atomically; an existing immutable batch association
causes Conflict without writing. Temporary offline state should clear verified IDs, not permanently
suppress a notice. `SuppressRequiredDecisionAsync` is only for an exact host-confirmed resolved or
invalid request and preserves all delivery facts.

First use of a catch-up window or permanent approval suppression promotes the protected snapshot to
at least schema version 2; creating a span manifest promotes it to schema 4. Current readers accept
schemas 1, 2, 3, 4 and 5, and an absent ManifestVersion remains legacy version 1 with complete-page
coverage. Existing full-page batches keep their original rendering and semantics. A version-2 span
manifest in a schema below 4 fails closed. Older readers reject schema 4 rather than interpreting a
partial span as an entire accepted page and losing or replaying content. This is a one-way feature gate,
not a destructive migration. Journal framing, encryption purpose, sequence and size limits are unchanged.
The host must retain updated binaries with this ledger; downgrading requires explicit recovery rather
than opening it with an older application. This implements long and partially accepted source recovery;
it does not establish real platform-window or phone-delivery acceptance, nor complete every P5 item.

The final Core regression contains 458 passing checks (431 prior checks plus 25 span checks and two
span/dependency checks), with zero build warnings or errors. It includes actual temporary FileStream
AfterFlush/reopen cases for prepare, send admission and acceptance; old manifests, corruption, Unicode,
partial coverage, Unknown, budget splitting and context revocation. The result report and adjacent build/check logs are local development evidence and are not distributed with the source. These are isolated synthetic tests with temporary
protected journals, not live WeChat or production-data validation.

### Captured output dependencies and revocation

Production hosts open the ledger with `RequireOutputDependencies = true` and a pure local
`IOutputDependencyValidator`. This requirement is persisted and cannot be disabled by reopening with
older options. New dependency records promote the protected snapshot to at least schema 3; readers
accept versions 1, 2, 3, 4 and 5. Span manifests require schema 4. No path downgrades the feature version.
Older readers reject unsupported versions,
instead of silently ignoring dependency checks. The journal framing, protection purpose and size
bounds are unchanged. Legacy hosts may opt out only before the requirement has been persisted;
missing metadata still resolves to `LegacyUnknown`, never to `Independent`.

An `OutputDependencies` manifest is explicitly Independent for deterministic facts, LegacyUnknown
when the original capture is unavailable, or Bound to exact provider/scope revision, fingerprint,
expiry and item/version references. The Core understands no memory text or Windows settings. A Bound
manifest requires the host validator on every model/tool admission, preparation and send admission.
Expired or invalid captures retire only Processing/Prepared generations and Pending/Rejected outputs.
Sending, Accepted and Unknown records retain their actual outcomes. Unavailable validation holds
output without inventing revocation; recovery cannot silently reclassify old content as independent.

`IModelContextProvider.CaptureAsync` freezes the actual prompt context once per generation. Its
FilterAsync may remove precise revoked events, tool observations and the model's conversation-context
projection. A null projected ConversationContext means omit it; it does not restore the unfiltered
focus excerpt. The host must use the exact generation/epoch capture when constructing the real model
request and revalidate at any actual channel gate after waiting. The validator itself must not perform
I/O, mutation or ledger reentry. `Release` clears only ephemeral frozen prompt caches after the loop;
its failure cannot erase a durable result.

Every tool invocation captures its actual epoch and manifest. Model observations and persisted tool
events carry the exact invocation ID even for read-only tools with no operation. This lets hosts filter
old tool content without inferring authority from a shared source message. A legitimate local context
write returns `ContextDependencyTransition` outside model-visible Facts. Only the exact durable
Completed side-effect invocation and Completed operation can advance the generation epoch; changed
provider scopes must increase their revisions. The host must independently attest the Before/After
capture against its actual store. The loop discards other calls from the former model response and
captures neither a new user grant nor a replacement automatic-learning authorization stamp.

Prepared replies inherit generation dependencies, finalized model notifications retain their actual
capture, and factual fallback/required-decision notices are explicitly Independent. Catch-up pages
derive their dependency union from immutable original source references. Conflicting captures remain
unverifiable rather than being signed with a newer context. No manifest changes original bodies,
accepted receipts, unknown outcomes or exclusive coverage. The same checks apply to every span page
and after restart: missing validation holds it, expiry or revocation supersedes only unstarted output,
and actual Accepted/Sending/Unknown range facts remain intact.

Before tool dispatch, the loop atomically abandons a known Prepared invocation and its exact Queued
operation when admission fails (including changed dependencies, cancellation, new input or a withdrawn
capability). `AbandonPreparedToolInvocationAsync` records Unavailable/CancelledBeforeDispatch and a
`not_started` fact. It is host failure cleanup, not the user-facing `cancel_queued` command, and does
not fabricate a cancellation grant. It accepts a stale lease only for that lease's exact invocation;
rebound invocations, already dispatched operations and uncertain outcomes cannot be changed. A null
operation ID never causes a search-and-cancel of an existing matching operation. Recovered Prepared
calls are explicitly rebound before admission, so a late former lease cannot dispatch them.

Storage uncertainty remains a separate boundary. If a queue/dispatch write may have committed,
cleanup cannot proceed on that faulted ledger instance. Reopening recovers a committed dispatch as
Unknown. If an abandonment write never committed, the remaining Queued record is retained honestly;
it is not reported as cancelled and its original context cannot pass a stale Begin check. The host
must not automatically execute recovery-plan queue entries as a substitute for fresh deterministic
admission. The Windows coordinator does not dispatch that recovery list.


## Trusted observation order (logical schema 5)

Owned task subscriptions retain the original wall-clock observation time and a separate host-verified
connection order. `RegisterCommitmentObservationSourceAsync` registers an authenticated conversation,
configured source key, host/profile, configuration fingerprint and actual connection epoch. Registration
is host-only; neither model arguments nor stored prose can supply a lease. The current registration is
bounded to 256 identity/source pairs per ledger. Replacing a connection increments its durable generation;
re-registering the identical active connection is a no-op. The host serializes replacement and drains old
read/event workers before registering the next runtime. The Core API does not invent that external proof.

A subscription accepts a strictly greater sequence only from the exact current registration and exact
host/profile/task/turn. A real current event or exact fresh read is required to establish the first ordered
watermark or enter a new generation. Cached observations cannot establish that baseline. Same-sequence,
older-sequence, old-generation and missing-order observations cannot overwrite an ordered subscription.
Unordered legacy observations retain their existing wall-clock watermark rules. Original timestamps are
not rewritten to make a clock rollback appear chronological. A semantic lifecycle state change may
advance task activity; a same-state higher sequence advances ordering only, not the inactivity deadline.
A real `thread/read` never invents activity from its read time. The Windows regular polling path does not
resubmit the Owned cache: genuine lifecycle events and fresh reads pass through the serialized bridge.
A fresh read whose exact ordered terminal fact already arrived through that bridge can settle only its
still-matching check claim as skipped. Different returned facts, cancelled work or changed versions do
not enter this branch; exact repeat settlement is a no-op backed by the released reservation.

Reopening the ledger deactivates previous connection registrations. A current connection with a new
epoch must be registered before further ordered facts are accepted. Unknown remains unknown: observation
ordering never grants a write or reconciles an operation; existing exact operation-reconciliation rules
remain in force. Freshness bounds, deadline policy, delivery attempts and notification budgets still apply.

The first registration promotes the logical snapshot to schema 5. Readers accept schemas 1–5; schemas
1–4 have an empty source registry and absent order fields. New ordering fields without schema 5, incomplete
leases and mismatched scope/generation references fail closed. Older readers reject schema 5; do not use
an older application to open an upgraded ledger. The physical append/checkpoint format is unchanged.

The isolated candidate retained all 482 previous Core checks and passed 15 additional ordering scenarios
(clock rollback, duplicate/stale sequence, watermark without activity, reconnect/reopen, scope rejection,
Unknown, no-op registration, valid schema 1–4 migration, malformed fields, and stale check claims).
These are synthetic protected-journal checks, not a live Codex or system-clock-change acceptance test.
The fix is limited to observation order. Source creation-time validation, configuration expiry, freshness
bounds and reminder deadlines retain their separate clock-sensitive rules; it does not establish that
every system-clock change is transparent to the application.
