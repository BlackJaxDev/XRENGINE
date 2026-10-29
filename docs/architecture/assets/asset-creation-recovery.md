# Asset Creation Recovery

Native editor asset creation uses a project-local recovery journal. The journal
lets Undo and Redo move created files without loss. Existing `.asset` and `.meta`
formats do not change. The approved proposal is the
[native asset creation recovery design](../../work/design/ui/native-asset-creation-recovery-design.md).

## Type Map

| Type | Project | Responsibility |
|---|---|---|
| `AssetCreationRecoveryStore` | `XREngine.Runtime.Core` | Reads and writes the journal, checks bytes, protects paths, and enforces the quota. |
| `AssetCreationRecoveryOperation` | `XREngine.Runtime.Core` | One creation and its current state. The editor keeps it as an Undo receipt. |
| `EAssetCreationRecoveryState` | `XREngine.Runtime.Core` | Journal states. |
| `AssetManager.CreationRecovery` | `XREngine.Runtime.Core` | The store of the configured project, or null. |
| `NativeAssetCreation` | `XREngine.Editor` | Creates files and metadata through the store and records one structural Undo step. |
| `MaterialTextureSlotAction` | `XREngine.Editor` | Replaces one material texture slot as one guarded Undo step. |

## Storage

The store is `<project-root>/.xr-native-asset-recovery/v1/<operation-id>/`. Each
operation directory holds:

- `manifest.json`: schema version, operation ID, asset GUID, and one entry per
  file. Each entry has the project-relative path, byte length, and SHA-256 hash.
- `original-<index>.bin`: the immutable original bytes. Undo and Redo never move
  these files.
- `working-<index>.bin`: the copy that Undo retains and Redo publishes. It is
  absent while the file is published.
- `journal.jsonl`: one JSON record per line, with a sequence number, a state,
  a UTC time, and an optional note.

One creation can hold more than one file. The editor adds the metadata file for
each created file as its own entry. A metadata root outside the project root
cannot be journaled, so creation fails there.

## States And Ordering

The states are `Preparing`, `Prepared`, `PublishPending`, `Published`,
`RetainPending`, `Retained`, `RestorePending`, `RecoveryRequired`, and
`Released`. `RestorePending` is reserved.

- Prepare checks the paths and the quota, writes `Preparing`, writes the
  originals, working copies, and manifest as new files, verifies their bytes,
  and writes `Prepared`.
- Publish checks that each public path is free and each working copy matches.
  Then it writes `PublishPending`, moves each file without overwrite, verifies
  each destination, and writes `Published`.
- Retain checks each public file. A changed public file before any move writes
  `Released`, keeps all files, and throws `AssetRecovery.ForeignChange`.
  Otherwise it writes `RetainPending`, moves each file back without overwrite,
  verifies it, and writes `Retained`.
- A failure after the first move writes `RecoveryRequired` and keeps all files.
  The store never deletes a file as compensation.

Each journal record is flushed before the next filesystem move. A lock file
serializes cooperating writers. The store does not control other writers and
does not claim power-loss durability.

## Startup And Protection

`AssetManager.ConfigureAuthoringRoots` opens the store before it starts the
project watcher. Opening reads all records and writes nothing. A pending state,
a broken journal sequence, a malformed manifest, a changed immutable copy, or a
file that is not where the journal records it becomes `RecoveryRequired` in
memory. The asset manager logs each such operation with its protected paths and
its record directory.

`Preparing`, the pending states, and `RecoveryRequired` protect their public
paths and the folders that contain them. For a protected path:

- watcher events are ignored, so no metadata rewrite, reload, rename handling,
  or auto import runs;
- metadata synchronization and stale-metadata pruning skip it;
- `AssetManager` saves throw `AssetRecovery.Protected`;
- native asset browser moves and renames throw `AssetRecovery.Protected`.

A malformed operation without a manifest protects no path, because it records
none. It keeps its files and cannot authorize a write.

## Undo, Normal Edits, And Cleanup

`NativeAssetCreation.Create` requires an open user change scope. It prepares and
publishes the files, marks each public path as recently written so delayed
watcher events stay no-ops, and records one structural step. Undo retains the
files. Redo publishes them again and refuses an occupied path. When Undo history
drops the step, the receipt is released and every file stays.

A normal save, rename, move, or delete of a published path releases its receipt
first. Then the normal edit runs. A retained path stays owned until its receipt
is released, so another creation cannot claim it.

`AcceptRecovery` records an explicit decision for a `RecoveryRequired`
operation with a readable manifest. `RemoveReleased` deletes a released
operation directory only when it holds known file names. Unknown entries stay.

## Quota

The store counts all files and bytes under its root, plus the reservation of
the operation being prepared. The reservation is two copies of each file, a
manifest, a journal, and a 4 KiB journal reserve. The limits are 512 MiB and
1,024 files. Prepare throws `AssetRecovery.QuotaExceeded` before it writes when
a limit would be exceeded.

## Native Asset Browser

The asset row menu has a **Create New...** submenu. It lists the creatable asset
types by category. The new asset goes into the row folder, or into the folder
that contains the row file, through `NativeAssetCreation.CreateAsset`. Each
creation is one `Create Asset` Undo action. The submenu is disabled for engine
assets and when no recovery journal is open.

The **Accept Recovery** command records an explicit acceptance for each
recovery-required operation with a readable manifest and logs its paths. The
files stay where they are. The **Clean Recovery** command removes released
operations that pass the ownership check.

## Viewport File Drop

`EditorFileDropHandler` resolves the target material before it creates any
file. It imports the dropped image as an `XRTexture2D` asset. A source outside
the assets root is copied in. The new files go through `NativeAssetCreation`,
and the first material texture slot changes through
`MaterialTextureSlotAction`. Both steps are one `Drop Texture` Undo action.
Undo restores the slot first and then retains the files.

Texture slot writes use `XRMaterialBase.SetTextureSlotGuarded` and
`EventList.SetIndexValidated`. They validate the exact material, list, slot,
resource lifetimes, and caller admission between callbacks. A newer mutation
intent, including an equal-value assignment, rejects an older write or replay.
Compensation uses the same guarded path. It cannot replace a newer callback edit.

## Tests

`AssetCreationRecoveryStoreTests` covers publish, Undo and Redo bytes, reopen,
path checks, occupied paths, foreign changes, interrupted publication, accepted
recovery, malformed manifests, changed originals, normal-edit release, path
ownership, quota, and cleanup. `MaterialTextureSlotActionTests` covers the slot
Undo contract. Windows asset-browser and viewport-drop workflows are in the
[native UI validation plan](../../work/testing/ui/native-ui-validation.md).
