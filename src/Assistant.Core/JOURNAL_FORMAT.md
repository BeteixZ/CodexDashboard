# Encrypted journal physical format

This describes the file-storage implementation, independently of the logical ledger schema. Compaction copies the latest encrypted snapshot byte for byte at its existing sequence, retaining the logical schema carried by that snapshot, its protection purpose and all logical records. Dependency records require at least schema 3; catch-up span manifests require schema 4. Trusted owned-observation registration requires schema 5. The feature-gate discussion below describes logical schemas through version 5; later logical features are validated by the current reader code. These feature gates and the compatibility rules in README.md are separate from the physical v1/v2 formats below.

Integers are little endian. GUIDs use .NET `Guid.TryWriteBytes` representation. Checksums are SHA-256. The checksums detect damaged framing; the host-supplied `IStateProtector` authenticates and protects the snapshot contents.

## Files and limits

The original journal path is the lifetime writer-lock file. It stays open with `FileShare.Read`, including during migration and bank rotation. Only the owner can write; no rename or path replacement is part of publication.

Before migration it contains the existing v1 format. Afterwards the complete storage unit consists of the original path plus `.bank0`, `.bank1` and `.switch`. An offline backup or explicit recovery must preserve this set consistently. Copying only the original path after migration does not preserve the conversation.

Each bank is limited by `maximumJournalBytes`, normally 512 MiB. The two banks can therefore occupy about 1 GiB in total, plus the 56-byte anchor and the switch log. The switch log defaults to 1 MiB, is bounded independently and is never compacted. The plaintext logical snapshot remains limited to 8 MiB. Physical compaction does not supply history retention or logical deletion, and a deleted logical value could remain in the inactive bank until a later rotation.

## Existing v1 file

The first eight bytes are `ASLEDG01`. They are followed by frames with consecutive sequences starting at 1:

| Field | Bytes |
| --- | ---: |
| Sequence | 8 |
| Encrypted payload length | 4 |
| SHA-256 of sequence bytes, length bytes and encrypted payload | 32 |
| Encrypted payload | Declared length |
| `COMMIT01` | 8 |

A valid v1 file remains writable until physical compaction is needed. Existing empty files, invalid framing and incomplete tails are not initialized or truncated automatically. The presence of any v2 side file beside a v1 anchor also makes that store read-only, because publication may have been interrupted.

## Published v2 anchor

The original path contains these 56 bytes:

| Offset | Field | Bytes |
| ---: | --- | ---: |
| 0 | `ASBANK02` | 8 |
| 8 | Store GUID | 16 |
| 24 | SHA-256 of bytes 0–23 | 32 |

The store GUID must be nonempty and match the switch and selected bank. A healthy interrupted migration may leave redundant old v1 bytes after the complete anchor. Only a successfully validated v2 store may finish truncating that redundant tail. A missing anchor with any side files is an error; opening it cannot create a fresh store.

## Data banks

Each bank begins with an 80-byte header:

| Offset | Field | Bytes |
| ---: | --- | ---: |
| 0 | `ASBNK002` | 8 |
| 8 | Store GUID | 16 |
| 24 | Unique bank-generation GUID | 16 |
| 40 | Checkpoint sequence | 8 |
| 48 | SHA-256 of bytes 0–47 | 32 |

The selected bank begins with the unchanged checkpoint frame at sequence `N`, followed by ordinary frames `N+1`, `N+2`, and so on. Frames use the v1 frame encoding above. Its final 64 bytes are:

| Offset | Field | Bytes |
| ---: | --- | ---: |
| 0 | `ASEND002` | 8 |
| 8 | Last sequence | 8 |
| 16 | Bank-generation GUID | 16 |
| 32 | SHA-256 of bytes 0–31 | 32 |

An ordinary append overwrites the previous end marker with the next frame and a new end marker, sets the resulting length, and calls `Flush(true)` before reporting success. If the overwrite stops partway through, a valid older prefix alone cannot make the selected bank writable. A complete matching end marker at the exact end is required.

## Append-only switch journal

The switch header is 56 bytes: `ASSWCH02`, the 16-byte store GUID and SHA-256 of those first 24 bytes. Each activation record is 120 bytes:

| Offset | Field | Bytes |
| ---: | --- | ---: |
| 0 | Activation epoch, starting at 1 | 8 |
| 8 | Bank index, 0 or 1 | 4 |
| 12 | Reserved, zero | 4 |
| 16 | Bank-generation GUID | 16 |
| 32 | Checkpoint sequence | 8 |
| 40 | Checkpoint payload length | 4 |
| 44 | Reserved, zero | 4 |
| 48 | SHA-256 of encrypted checkpoint payload | 32 |
| 80 | SHA-256 of store GUID bytes followed by record bytes 0–79 | 32 |
| 112 | `COMMIT01` | 8 |

Epochs must be consecutive, bank indexes alternate, generation GUIDs are unique, and checkpoint sequences never decrease. Recovery validates the entire switch log, then uses only its last selected bank. The selected header and first frame must match the record exactly.

## Commit ordering and interruption

Initial migration creates all side files with `CreateNew`. It writes and flushes the checkpoint, verifies it by reading it back, appends and flushes the activation, then writes and flushes the anchor. Only then may it reclaim the original file's redundant v1 frames. The incoming transaction is still a separate append at `N+1`; publishing a checkpoint does not commit that new transaction.

Rotation first rewrites the inactive bank with the unchanged checkpoint, flushes and verifies it, then appends and flushes its activation. Subsequent appends use that bank. Readback detects copy damage; it does not replace the durable flush.

Compaction begins near the physical limit when reclaimable old frames exist. The checkpoint and incoming frame must both fit in one bank, and another activation must fit in the switch log. Capacity failures do not loosen either limit or remove logical records.

Any failed or uncertain write poisons the current instance: it refuses further mutations, and therefore cannot return a successful execution or send boundary. A new instance must validate the files before becoming writable. On reopen:

- A complete selected bank may recover the last fully committed append, including one whose caller did not receive success.
- A partial inactive copy is ignored only when the intact switch log proves that the other bank remains selected.
- A damaged switch tail is not rolled back to an earlier activation.
- A damaged selected bank never causes selection of the inactive bank.
- Missing, empty, foreign or mismatched required files fail closed.
- A readable committed prefix may remain available for diagnosis, but recovery-required storage exposes no automatic dispatch plan.

This implementation has no automatic recovery that discards uncertain bytes. Tests cover injected write failures, real file reopen, and process exit after flush. They do not establish every device's behavior under physical power loss.

The schema-4 Core regression passed all 458 checks, including 32 physical-compaction checks and the
catch-up prepare/send/accept FileStream AfterFlush recovery cases. The result report and adjacent build/check logs are local development evidence and are not distributed with the source. The evidence uses temporary protected journals and synthetic
channel observations; it does not verify production storage or real phone delivery.


Logical schema 5 adds the bounded current observation-source registry and per-subscription ordered
watermarks. This does not alter physical frames, checkpoint activation, hashes, sequence numbers or
protection purposes. Reopening deactivates old connection leases by an ordinary atomic logical transaction;
new connection registration is separately durable. Old schema 1–4 records load with an empty registry.
Readers that do not support schema 5 must reject the upgraded snapshot rather than ignore its ordering
fence. The 15 new ordering scenarios run in addition to the retained 482 Core checks.
