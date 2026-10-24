# Native Asset Creation Recovery Proposal

Status: Proposed; owner approval pending
Scope: Native asset creation and its Undo/Redo only
Related work: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Investigation: [Retained UI integration](../../investigations/ui/retained-ui-integration.md)

## Decision

Add a versioned recovery journal for native asset creation. Existing `.asset`
and `.meta` formats do not change. No existing asset migration or new dependency
is proposed. The journal adds persistent state and startup recovery behavior;
it needs owner review before implementation.

Raw backup pairs are insufficient. Missing working files can mean successful
publication or failure after both moves. A missing public asset can mean Undo,
rename, deletion, or partial failure. File hashes cannot identify the operation
that still owns a path. The unpublished prototype is preserved separately and
is not available through the native asset browser.

## Persistent Records

Use `.xr-native-asset-recovery/v1/<operation-id>/` under the project root, outside
watched asset, metadata, cache, and temporary roots. Reject linked paths and
paths outside the captured project. Each operation contains:

- `manifest.json`: schema version, operation ID, relative roots and paths, asset
  GUID, byte lengths, and SHA-256 hashes.
- `original.asset` and `original.meta`: immutable original bytes. Undo and Redo
  never consume these copies.
- `working.asset` and `working.meta`: staged or retained files.
- `journal.jsonl`: append-only sequence, intended transition, observed locations,
  and completion records.

Unknown or malformed records preserve their files and require recovery. They
cannot authorize a write. A cooperating-writer lock protects journal and quota
changes. It does not claim control over arbitrary external writers.

## State And Operation Ordering

States are `Preparing`, `Prepared`, `PublishPending`, `Published`,
`RetainPending`, `Retained`, `RestorePending`, `RecoveryRequired`, and `Released`.
Write and verify the manifest and immutable pair before publication. Flush the
intent record before each filesystem move. Check exact bytes before and after
each nonoverwriting move. Record completion only after both files and conditional
cache ownership pass validation. Serialize outside metadata locks and invoke
property/user callbacks outside locks and path gates that could deadlock reentry.

After interruption, inspect all recorded locations. A partial record, split pair,
changed payload, or ambiguous state requires recovery. Startup must not overwrite,
delete, or automatically roll back an ambiguous file. These rules cover process
interruption. Full power-loss durability needs separate platform directory-flush
guarantees and is not claimed.

## Watchers, Ordinary Edits, And Unload

Read recovery records before enabling project watchers or metadata synchronization.
Pending and recovery-required operations protect their public paths from automatic
metadata rewriting, deletion, rename handling, reload, and save.

A completed creation keeps its Undo receipt until a normal save, rename, move, or
delete invalidates it. Before that edit, append `Released`, invalidate replay, and
release path protection. Then run the existing canonical edit. Keep the immutable
backup. Unchanged delayed watcher events remain no-ops. A detected foreign change
to an active receipt invalidates replay and requires explicit acceptance or
recovery; it must not trigger metadata normalization.

Stop project metadata mutations before dropping the context on unload. Recovery
ownership belongs to the recorded project operation, not the current ticket.
Reopening reconstructs protection from that project's records. Older versions
that do not understand unresolved journals cannot safely author that project.

## Capacity And Cleanup

Reserve space before staging or appending an operation intent. Count immutable
copies, working files, journal growth, unknown entries, and pending reservations
against 512 MiB and 1,024 files. Refuse work before exceeding either limit.
Cancellation, unload, and Undo-history eviction do not purge backups. Explicit
cleanup may remove released operations after checking their ownership. Unknown,
changed, or recovery-required entries must remain available for recovery.

## Guarantees And Validation

Generic filesystem calls do not provide atomic compare-and-rename against an
arbitrary external writer. The contract is narrower: never overwrite destinations
or delete files as compensation; preserve original bytes and all payloads moved
by the operation; report detected conflicts and their locations. Concurrent
modification of the recovery store itself is outside this cooperative protocol.

Required deterministic tests cover publication, Undo/Redo, GUID and byte identity,
collision, every interrupted state, context/backend replacement, callback reentry,
load/save admission, cancellation, delayed watchers, ordinary rename, unload and
restart recovery, foreign edits before and between moves, and quota reconstruction.
UI creation stays unavailable until the core protocol passes review and tests.
Windows asset-browser workflows remain a separate live acceptance check.

## Approval Scope And Alternative

Approval covers only this journal schema and lifecycle, startup recovery gating,
receipt invalidation during normal edits, cleanup/quota ownership, and the stated
compatibility and durability limits. It does not cover a general asset-storage
rewrite, new dependency, or migration of existing assets.

The alternative is to defer native asset creation and its Undo/Redo command,
retain existing browser behavior, and leave that work open. That alternative does
not complete the full native UI TODO.
