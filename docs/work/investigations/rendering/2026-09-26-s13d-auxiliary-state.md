# S13d: update material/draw auxiliary state only when its inputs change

Status: Validated for reachable scope (September 26, 2026): four owners
validated (per-update struct equality, unchanged-row rewrites on the update
path, transparency-stream publication, material-state row rewrites) and the
per-dependency mutation matrix run; limits recorded below. Gate record for
[S13d](../../todo/rendering/vulkan-stall-remediation-todo.md#s13d-update-materialdraw-auxiliary-state-only-when-its-inputs-change)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-094453-s13c-registration-retention/` (shared
with S13c; ignored, disposable; findings are copied here).

## Entry evidence

Carried from the [S13c record](2026-09-26-s13c-registration-retention.md):
with registration retained, a transform-only update of the 393-draw Sponza
fixture still allocates about 630 bytes per submesh update (443 to 493 KB per
move of `$MergedNode_0`, 707 to 786 submesh updates) and about 3.3 KB per move
of a one-submesh probe cube (two update calls per edit). Registration itself is
allocation-free on a hit, so the remainder lies in the rest of the per-submesh
update body: index lookup and validation, material id lookup, draw metadata,
transform and bounds computation, state-class resolution, flag and transparency
resolution, the write phase and the post-loop commit.

## Exact owning path

`GPUScene.TryUpdateMeshCommandCore`
(`XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs`)
and its callees `GetOrCreateMaterialID` (`GPUScene.MeshMaterialIds.cs`),
`ResolveStateClassId` (`GPUScene.Soa.cs`), `ComposeDrawFlags`,
`GPUTransparencyMetadata.FromMaterial`, `UpdateTransform`,
`ComputeRenderCullingBoundsGpu`, `WriteDrawMetadata`/`WriteBounds` and the
dirty-range commit.

## Attribution instrumentation (observation only)

The per-update observation now samples `GC.GetAllocatedBytesForCurrentThread()`
at nine phase boundaries while the S13a telemetry is enabled (membership check,
lookup/validation, material id, registration, metadata/transform/bounds,
state class, flags/transparency/residency, write, commit) and publishes them as
`meshUpdate*AllocatedBytes` counters in the S13a snapshot. The boundaries are
inside the existing telemetry guard, so the disabled path is unchanged.

## Attribution run (before any change)

Session `s13d-probe` (Release, current tree, telemetry on),
`reports/s13d-attribution-motion/` and the finer split in
`reports/s13d-attribution-split/`. Bytes per submesh update, all other phases
zero:

| Window | Total | Bounds compare phase | Write phase |
| --- | ---: | ---: | ---: |
| Cube add / re-add (2 updates) | 2,088 | 1,928 | 80 |
| Cube motion, 60 moves (120 updates) | 1,665 | 1,505 | 80 |
| Merged node motion, 10 moves (7,074 updates) | 627 | 467 | 80 |
| Cube remove (786 re-registrations) | 626 | 466 | 80 |

The bounds-compare phase contains only the bounds row read and
`existingBounds.Equals(updatedBounds)`; the row read allocates nothing (the
metadata phase reads rows the same way and shows zero). `BoundsGpu` declared no
typed equality, so the call is `ValueType.Equals(object)`: it boxes the argument
and, because the struct holds `Vector4` fields, takes the reflection path that
boxes each compared field, which is why the cost varies with how many fields
match before a difference (cube: 1.5 to 1.9 KB; merged node whose bounds differ
early: 467 bytes). The write phase's constant 80 bytes is the box from
`existing.Equals(updated)` on `DrawMetadata` (16 integer fields, bitwise path).
Membership, lookup, material id, registration, metadata/transform, state
class, flags, transparency and commit phases allocate nothing.

## Selected owner and acceptance (declared before editing)

- Owner: struct equality in the per-submesh update path (`BoundsGpu`,
  `DrawMetadata`). Change: implement `IEquatable<T>` with explicit field
  comparison (and matching `Equals(object)`/`GetHashCode`/operators) so the
  existing calls bind to the typed overload. Comparison semantics are
  unchanged (`Vector4.Equals` is exact component equality; integer fields
  compare bitwise).
- Correctness invariants: metadata write counts per move unchanged (one write
  per changed row), draws unchanged, S13b rows pass.
- Budget: `meshUpdateAllocationBytes` per submesh update is zero for the cube
  motion, merged-node motion and cube-remove windows (all phase buckets zero).
  Same session type, fixture and windows as the attribution run.
- Falsifier: any remaining bytes in the bounds-compare or write buckets after
  the change.

## Change

`XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`: `BoundsGpu` and
`DrawMetadata` implement `IEquatable<T>` with explicit field comparison, matching
`Equals(object)`, `GetHashCode` and `==`/`!=`. The two call sites in
`TryUpdateMeshCommandCore` are unchanged and now bind to the typed overloads.
No other file changed for this owner. The meshlet-range comparisons in
`SetMeshletRange`/`ClearMeshletRange` (`GpuMeshletRange`, add and clear paths
only) still box once per call; they are outside the per-update path measured
here and were left alone.

## Result (same session type, fixture and windows)

Rebuilt `s13d-probe` (Release, telemetry on), `reports/s13d-fixed-motion/`:

| Window | Submesh updates | Bytes per update | Metadata writes | Body ms per unit |
| --- | ---: | ---: | ---: | ---: |
| Stationary 15 s | 0 | 0 | 0 | 0 |
| Cube add (3 updates) | 3 | 64 (material id phase) | 1 | 0.237 |
| Cube motion, 60 moves, run 1 / 2 | 120 / 120 | 0 / 0 | 60 / 60 | 0.038 / 0.032 |
| Merged node motion, 10 moves, run 1 / 2 | 7,074 / 7,860 | 0 / 0 | 3,537 / 3,930 | 0.770 / 0.849 |
| Cube remove (re-registrations) | 786 | 0 | 393 | 0.987 |
| Cube re-add | 2 | 0 | 1 | 0.027 |

Every phase bucket is zero on the motion, merged-node, remove and re-add
windows, so the predeclared budget is met and the falsifier did not fire. The
only remaining bytes are 64 per update in the material-id phase of the first
add of a new material, which is a real registration mutation (the material's
id entry is created), not the unchanged path. Metadata write counts per move
are unchanged from the attribution run (one per moved submesh; the second
update pass per edit writes nothing), draws return to 393 after removal.

Body time per unit is at or below the attribution run (cube 0.032 to 0.038 ms
versus 0.055 to 0.10 ms; merged node 0.77 to 0.85 ms versus 0.8 to 1.0 ms).

## Regression

S13b matrix rows on the fixed build (`reports/s13b-matrix-s13d-fixed/`,
`s13b_matrix.py`, warm-up 20 s): add-node, remove-node, re-add-node,
visibility-off, visibility-on, material-shared, material-edit-retention,
view-B-then-return-A and cleanup-stationary all **PASS**.
Filtered unit tests (`reports/unit-tests/s13d-tests.log`, the S13c filter,
496 tests): 435 pass, 61 fail, and the failing set is identical to the S13c run
(no new failures, none fixed; all pre-existing per the S13c partial-stash
baseline). No compiler warnings in the touched files. Session `s13d-probe` was
stopped after the runs.

## Remaining S13d work (not claimed by this increment)

The per-update path now allocates nothing on unchanged and transform-only
updates, and the material-id, state-class, flags and transparency phases are
allocation-free and cheap, so the remaining S13d checklist concerns writes,
not allocation. Observed: a transform-only move rewrites the draw-metadata row
even when `existing.Equals(updated)` is true, because the write is gated on
`transformChanged || boundsChanged` as well; whether that row write (and its
dirty range) is redundant depends on how the swap copies rotating render-side
buffers, which the todo's fourth checklist item requires deciding against each
destination's accepted generation. That is the next measured owner; it was not
changed here.

## Second owner: unchanged-row rewrites on the update path

Opened September 26, 2026 after the first owner closed. Same evidence root.

### Exact owning path

`GPUScene.TryUpdateMeshCommandCore` write block
(`GPUScene.CommandConversion.cs`): when `!existing.Equals(updated) ||
transformChanged || boundsChanged`, it calls `WriteDrawMetadata` (which also
rewrites the classification and visibility rows and marks their dirty ranges)
and `WriteBounds` unconditionally, then widens the draw-metadata range that the
post-loop commit pushes with `UpdatingDrawMetadataBuffer.CommitDirtyBytes`.
`SwapCommandBuffers` (`GPUScene.CommandBuffers.cs`) copies each stream's dirty
range from the updating buffer to the render-side buffer and commits it there,
so every row a dirty range covers is uploaded again by the backend.

### Dependency analysis (what a transform-only move must write)

- Transform row: written by `UpdateTransform` only when the matrix differs;
  own dirty range; published with a previous-transform copy for velocity.
- Bounds row: `ComputeRenderCullingBoundsGpu` depends on the model matrix, so
  it changes on a move; own dirty range.
- Tight AABB row: only when the internal BVH is enabled (`_useInternalBvh`,
  off by default); own dirty range.
- Draw-metadata, classification and visibility rows: pure functions of
  `updated` (ids, pass, layer, flags, instance count, state class). `Flags`
  can depend on the matrix (`Dynamic` for non-uniform or negative scale), and
  that dependency is already inside `existing.Equals(updated)`. When the
  comparison is equal the updating rows are byte-identical to what was last
  written and marked, so the rewrite carries no new information.
- Content version (`MarkUpdatingCommandsDirty`) and the return value: keep
  unchanged in this increment; the content version also gates a full
  transparency-stream copy at swap, which is recorded below as a separate
  candidate, not changed here.

Accepted-generation reasoning (todo item four): each render-side stream is one
`XRDataBuffer` whose committed revision is its accepted generation; the backend
owns frame-in-flight copies of a committed revision. Rows that never move are
already never rewritten, and their currency across initial population, buffer
growth (`Resize` copies the client image; the backend re-uploads a grown
allocation) and rotating frames rests on that contract. Skipping a rewrite
whose row is byte-identical to the last marked write relies on exactly the same
contract, so it adds no new assumption. The stable Hi-Z scene revision hashes
the bounds, transform, cull-control and material-state buffer revisions, so a
move still changes it through bounds and transform.

### Attribution instrumentation (observation only)

- `SwapCommandBuffers` reports, under the telemetry guard and before the
  copies clear the ranges, the element count of every stream dirty range it is
  about to publish (cull-control, bounds, classification, visibility,
  transform, previous transform, material state, optional AABB), whether the
  swap was content-dirty and whether any stream was dirty, and the bytes of the
  full transparency copy a content-dirty swap performs.
- The per-update observation counts bounds and transform row writes next to
  the existing metadata write count.

### Hypothesis and acceptance (declared before editing)

- Change: gate `WriteDrawMetadata` on `!existing.Equals(updated)` and
  `WriteBounds` on `boundsChanged`; widen the draw-metadata commit range only
  for rows whose metadata row was written; keep the transform write, the LOD
  transition reset, the tight AABB write, `anyChanged`, the content version
  and the commit sequence as they are.
- Budget: on the cube-motion and merged-node windows, swap-published
  cull-control, classification and visibility elements per move fall to zero
  and metadata writes per move fall to zero; bounds and transform elements per
  move stay equal to the entry run (the moved rows' span); body time per unit
  does not rise.
- Correctness: every S13b matrix row passes (all ten, including velocity,
  repeated-edits, rejection and material-cube), draws return to 393, the
  add/remove/re-add windows still register and render, and no unit test
  regresses.
- Falsifier: any non-zero cull-control, classification or visibility element
  count on a transform-only window, any change in bounds/transform counts, or
  a failed matrix row.

### Entry evidence (instrumented build, before the change)

Session `s13d-probe` (Release, telemetry on), `reports/s13d-writes-entry/`.
Elements published per move by the swap (dirty-range span copied to the
render-side stream), and row writes per move on the update path:

| Window | Submesh updates per move | Metadata / bounds / transform row writes per move | Cull-control / classification / visibility elements per move | Bounds / transform / previous-transform elements per move | Material-state elements per move | Transparency copy bytes per move |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Cube motion, 60 moves (two runs) | 2 | 1 / 1 / 1 | 1 / 1 / 1 | 1 / 1 / 1 | 1.9 | 6,352 |
| Merged node motion, 10 moves, run 1 | 707 | 354 / 354 / 354 | 354 / 354 / 354 | 354 / 354 / 354 | 5.1 | 5,717 |
| Merged node motion, 10 moves, run 2 | 786 | 393 / 393 / 393 | 393 / 393 / 393 | 393 / 393 / 393 | 6.0 | 6,352 |
| Stationary 15 s (1,606 swaps) | 0 | 0 | 0 | 0 | 0 | 0 |

Every moved submesh rewrites its draw-metadata row (and with it the
classification and visibility rows) although the row is unchanged, so on the
merged node the swap republishes the full 64-byte cull-control, 32-byte
classification and 4-byte visibility spans (about 39 KB per move) next to the
bounds and transform spans that a move genuinely needs. Two further redundant
publications are visible but are not this owner: the content-dirty swap copies
and commits the whole transparency stream (about 6.3 KB per move), and
`ResolveStateClassId` marks the material-state row dirty on every update (two
to six 32-byte elements per move). Both are recorded as later candidates.

### Change

`GPUScene.CommandConversion.cs` (`TryUpdateMeshCommandCore`): the draw-metadata
row is written only when `!existing.Equals(updated)`, the bounds row only when
`boundsChanged`, and the post-loop draw-metadata commit range is widened only
by rows whose metadata row was written (and is skipped when none was). The
transform write, LOD-transition reset, tight-AABB write, `anyChanged`, the
content-version bump and the rest of the commit sequence are unchanged.

### Result (same session type, fixture and windows)

Rebuilt `s13d-probe`, `reports/s13d-writes-fixed/`:

| Window | Metadata / bounds / transform row writes per move | Cull-control / classification / visibility elements per move | Bounds / transform / previous-transform elements per move | Body ms per unit (entry, fixed) |
| --- | ---: | ---: | ---: | ---: |
| Cube motion, 60 moves (two runs) | 0 / 1 / 1 | 0 / 0 / 0 | 1 / 1 / 1 | 0.043 to 0.053, 0.041 to 0.047 |
| Merged node motion, 10 moves, run 1 | 0 / 354 / 354 | 0 / 0 / 0 | 354 / 354 / 354 | 0.998, 0.999 |
| Merged node motion, 10 moves, run 2 | 0 / 393 / 393 | 0 / 0 / 0 | 393 / 393 / 393 | 1.041, 0.975 |
| Stationary 15 s | 0 | 0 | 0 | 0 |

The budget is met and the falsifier did not fire: transform-only motion no
longer publishes the cull-control, classification or visibility streams, and
the bounds, transform and previous-transform counts are identical to the entry
run. Metadata writes per move are zero; the removal window still publishes the
one zeroed cull-control row of the deleted command, and the add and re-add
windows still publish their rows through the add path (22 elements), so initial
population and re-registration are unaffected. Per-update allocation stays at
zero. Body time per unit is within run-to-run noise of the entry run; the
saving from this owner is in the swap copies and backend uploads that the
stream counters measure, not in the update body. Draws return to 393.

### Regression

Full S13b matrix on the fixed build (`reports/s13b-matrix-s13d-writes/`,
warm-up 20 s): stationary-A-60s, add-node, remove-node, re-add-node,
visibility-off, visibility-on, repeated-edits, publication-rejection,
material-value-cube, material-shared, material-edit-retention,
velocity-camera-object, view-B-then-return-A and cleanup-stationary all
**PASS**. Filtered unit tests (`reports/unit-tests/s13d-writes-tests.log`, the
S13c filter, 496 tests): 435 pass, 61 fail, identical failing set to the S13c
baseline. No compiler warnings in the touched files. Session stopped.

### Remaining S13d candidates (measured, not changed)

- A content-dirty swap copies and commits the entire transparency stream
  (`SwapCommandBuffers`, about 6.3 KB per move on this fixture) although a
  transform-only move changes no transparency row. The content-version gate
  is coarser than the transparency dirty state; the update path already knows
  `transparencyChanged`, so a transparency dirty range could replace the full
  copy. Needs its own gate.
- `ResolveStateClassId` rewrites and dirty-marks the material-state row on
  every submesh update (two to six 32-byte elements per move here). Comparing
  the composed `MaterialStateGpu` against `_materialStateByClass` before
  writing would remove it. Needs its own gate.
- Items two, three (key epochs) and five of the todo checklist (stable ids
  with source revisions, texture/sampler epochs in the key, and the
  per-dependency mutation matrix beyond what the S13b rows cover) remain open.

## Third owner: full transparency-stream copy on every content-dirty swap

Opened September 26, 2026 after the second owner closed. Same evidence root.

### Exact owning path

`GPUScene.SwapCommandBuffers` (`GPUScene.CommandBuffers.cs`): when the command
content version advanced since the last swap, it moves the first
`_updatingCommandCount` transparency rows from the updating buffer to the
render-side buffer and commits the whole span as dirty, regardless of which
rows changed. Every write to the updating transparency rows happens on four
paths: the add path (`GPUScene.AddRemove.cs`, per new command), removal
compaction (the last row moves into the removed slot), capacity growth
(`ZeroUpdatingTransparencyMetadataRange`) and the update path
(`TryUpdateMeshCommandCore`, only when `transparencyChanged`).

### Entry evidence

From `reports/s13d-writes-entry/` and `reports/s13d-writes-fixed/`: 6,352
bytes per cube move and 5,717 to 6,352 bytes per merged-node move (397 rows of
16 bytes), on windows where no transparency row changed
(`meshUpdateTransparencyWrites` 0). The stationary window copies nothing
because its swaps are not content-dirty.

### Hypothesis and acceptance (declared before editing)

- Change: give the transparency stream its own dirty range, marked at all four
  updating-row write sites and cleared on the lifecycle resets that clear the
  other ranges, and publish it in the swap's stream copy exactly like the
  other draw-indexed streams (range copy, destination resize when smaller,
  committed as dirty). The content-version gate no longer drives a full copy.
  The swap telemetry reports the copied range instead of the full span.
- Budget: transparency bytes copied per move fall to zero on the cube-motion
  and merged-node windows; the add and re-add windows still copy their new
  rows; a transparency-mode edit on the cube material copies that row (16
  bytes) and the rendered cube changes; reverting the edit copies it again.
- Correctness: full S13b matrix passes; the S13d mutation matrix's
  transparency, index-reuse and removal cases pass; no unit test regresses.
- Falsifier: non-zero transparency bytes on a transform-only window, a
  transparency edit that does not reach the render side (image unchanged or
  zero bytes copied), or a failed matrix row.

## Fourth owner: material-state row rewritten on every submesh update

Opened September 26, 2026. Same evidence root.

### Exact owning path

`GPUScene.ResolveStateClassId` (`GPUScene.Soa.cs`): composes the per-class
`MaterialStateGpu` row (state class, the resolving material's id, pipeline key,
transparency mode, transparent-like flag) and unconditionally stores it,
writes the updating material-state row and marks the material-state dirty
range, on every submesh update of every command.

### Entry evidence

From `reports/s13d-writes-entry/`: 1.9 to 2.0 material-state elements per
cube move and 5.1 to 6.0 per merged-node move are published by the swap
although no material changed.

### Row semantics (defined, unchanged by this owner)

The row for a state class is a function of the class and of the material that
most recently resolved into it (last writer): its `MaterialID` and
`TransparencyMode` follow that material. The generated meshlet shader reads the
row by state class and uses `MaterialID` only as a fallback when the per-draw
material id is zero, and `TransparencyMode` for the alpha-test decision, where
the two masked modes are treated identically. The representative-material map
(`StateClassMaterialMap`) keeps the first material instead, so the two can
disagree today; that inconsistency is recorded as a separate finding and is
not changed here.

### Hypothesis and acceptance (declared before editing)

- Change: `MaterialStateGpu` gets typed equality; `ResolveStateClassId` writes
  and marks the row only when the composed row differs from the stored row for
  that class (or the class has no row yet). Semantics stay last-writer.
- Budget: material-state elements per move fall to zero on cube motion (one
  material, one class). On merged-node motion they fall to the number of
  class rows whose last-writing material changes within the move; with
  several materials in one class that is not necessarily zero, and the result
  is recorded as measured. First resolution of a class and a real transparency
  change still write the row.
- Correctness: full S13b matrix and the S13d mutation matrix pass; the
  transparency-mode edit still updates the row (the cube's class changes, so
  its new class row is written); no unit test regresses.
- Falsifier: non-zero material-state elements on cube motion, or a material
  edit whose class row is not written.

## Column dependency set (todo items two and three)

Each destination the per-submesh update path can write, the inputs that
determine it, the change test that now gates the write, and how it reaches the
render side. Structural registration (mesh, LOD, atlas residency: S13c) stays
separate from this dynamic data.

| Destination | Inputs | Change test | Publication |
| --- | --- | --- | --- |
| Draw-metadata row (cull-control) | draw index; mesh, submesh and logical-mesh ids from registration; material instance id; transform and skin ids; render pass and pass mask; owner layer mask; flags (material transparent-like, owner cast/receive shadows, mesh skinning and blend shapes, instance count above one, LOD count, material cull mode, model-matrix scale sign and uniformity, CPU-fallback request, editor highlight bits); LOD policy; state class; instance count; render identity; bounds id | typed row equality | own dirty range, copied and committed at swap; the classification row (state class, material id, mesh id, LOD, draw id, flags) and the visibility seed (instance count) are derived from this row and written with it |
| Bounds row | mesh bounds, model matrix, owner culling basis | typed row equality | own dirty range |
| Transform row | model matrix | matrix equality in `UpdateTransform` | own dirty range; the previous-transform copy at swap keeps the frame-coherent current/previous pair for velocity |
| Transparency row | material transparency mode and domain, sort priority, alpha cutoff, flags | field comparison (`transparencyChanged`) | own dirty range (third owner) |
| Material-state class row | state class and the material that most recently resolved into it (its id, transparency mode, transparent-like flag) | typed row equality (fourth owner) | own dirty range |
| LOD-transition row | mesh id or logical-mesh id change | id comparison | reset and queued with the metadata write |
| Tight AABB row (internal BVH only) | owner bounds, model matrix | written on any row change while the BVH is enabled (off on this fixture) | own dirty range |
| Command content version | any of the above changed, or a transparency change | set when any row changed | swap-clean skip, transparency copy no longer depends on it, Advanced publication reuse decided by S13b's publication path |
| Skinning palette and blend-shape streams | deformation state | not touched by this path (only the Skinned and BlendShapes flags above) | own dirty ranges |

Texture and sampler epochs, the consumed material interface and layout/pass
dependencies (todo item three): no GPUScene row carries descriptor, texture or
sampler state (`DescriptorStart` and `DescriptorCount` are always zero), so
there is no per-draw key at this layer to extend. Those epochs are consumed by
the Advanced material-table publication, whose material-mutation and native
texture-replacement handling the S13b record validated (its sampled fingerprint
component and native texture capture sections). Adding epochs to a key with no
consumer here would be speculative; the disposition is not applicable at this
layer, with the dependency owned by the publication path.

## Third and fourth owners: change and result

### Change

- Transparency stream (`GPUScene.CommandBuffers.cs`, `GPUScene.AddRemove.cs`,
  `GPUScene.CommandConversion.cs`, `GPUScene.Lifecycle.cs`): a
  `_transparencyDirtyRange` marked by the add path, removal compaction,
  capacity-growth zeroing and the update path, cleared with the other ranges on
  reset, and published by the swap through the same range copy the other
  draw-indexed streams use. The content-version-gated full copy is gone; the
  swap telemetry reports the copied range.
- Material-state row (`GPUIndirectRenderCommand.cs`, `GPUScene.Soa.cs`):
  `MaterialStateGpu` has typed equality and `ResolveStateClassId` writes and
  marks the class row only when the composed row differs from the stored one.

Both owners were built and measured together because their budgets are read
from distinct counters (transparency bytes; material-state elements), so the
attribution of each result is unambiguous.

### Result (`reports/s13d-owners-ab/`, same session type, fixture and windows)

| Window | Transparency bytes per move (entry, now) | Material-state elements per move (entry, now) | Cull-control elements per move | Bounds elements per move | Body ms per unit | Bytes per update |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Cube motion, run 1 | 6,352, 0 | 1.9, 0.0 | 0.0 | 1.0 | 0.036 | 0.0 |
| Cube motion, run 2 | 6,352, 0 | 1.9, 0.0 | 0.0 | 1.0 | 0.029 | 0.0 |
| Merged node motion, run 1 | 5,717, 0 | 5.1, 5.4 | 0.0 | 353.7 | 0.795 | 0.0 |
| Merged node motion, run 2 | 6,352, 0 | 6.0, 6.0 | 0.0 | 393.0 | 1.041 | 0.0 |
| Cube add / re-add (per add) | 12,704 / 6,352, 352 / 352 | 7 / 6, 5 / 5 | 22 / 22 | 23 / 22 | 0.136 / 0.027 | 64 / 0 |
| Stationary 15 s | 0, 0 | 0, 0 | 0 | 0 | 0 | 0 |

Third owner: transparency bytes per move are zero on every transform-only
window (budget met, falsifier not fired); an add or re-add still copies the
add path's marked span (22 rows, 352 bytes) instead of the whole 397-row
stream, and the mutation matrix below shows a real transparency edit copying
exactly its one row (16 bytes) and reaching the image.

Fourth owner: material-state elements per move are zero on cube motion (budget
met). On merged-node motion they stay at 5.4 to 6.0 per move, as predicted for
a node whose submeshes resolve several materials into the same class under the
last-writer row definition; each of those writes changes the row content, so
they are real under the semantics recorded above. Closing them needs the
class-row definition changed to a stable representative, which is the
separate finding below, not a redundant write of this path. The per-update
resolution counter (`meshUpdateStateClassWrites`) still counts resolutions,
one per update; the published elements are the write measure.

Per-update allocation stays at zero on every window; body time per unit is
within the range of the previous runs on this fixture.

## Per-dependency mutation matrix (todo item five)

`scratch/s13d_mutation_matrix.py`, final run `reports/s13d-mutation3/`
(screenshots in `mcp-captures/s13d-mutation3/`), one probe cube on the
393-draw fixture at camera A, final build with all four owners. Row writes are
per case; stream elements are what the swap published during the case.

| Case | Verdict | Metadata / bounds / transform / transparency row writes | Cull-control / classification / visibility / bounds / transform elements | Material-state elements | Transparency bytes | Draws | Evidence |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| Add node (initial population) | PASS | 0 / 1 / 1 / 0 (add path writes outside the counters) | 22 / 22 / 22 / 22 / 23 | 5 | 352 | 394 | image changes, zero identity notifications |
| Transform-only moves (ten) | PASS | 0 / 10 / 10 / 0 | 0 / 0 / 0 / 10 / 10 | 0 | 0 | 394 | only bounds and transform publish |
| Stopped object (eight seconds) | PASS | 0 | 0 | 0 | 0 | 394 | nothing publishes after motion stops |
| Eight edits before a swap | PASS | 0 / 3 / 3 / 0 | 0 / 0 / 0 / 3 / 3 | 0 | 0 | 394 | burst image equals a single direct move; edits coalesce to the swaps that observed them |
| Opacity/pass transition (Opaque to AlphaBlend) | PASS | 1 / 0 / 0 / 1 | 1 / 1 / 1 / 0 / 0 | 1 | 16 | 393 | exactly the cube's transparency row, metadata row (new state class) and the new class row publish; the cube renders translucent; the transparent draw leaves the canonical resident scene |
| Revert to Opaque | PASS | 1 / 0 / 0 / 1 | 1 / 1 / 1 / 0 / 0 | 0 | 16 | 394 | row republished, image back within 0.6 percent |
| Render layer (component `MeshLayer` override) | PASS | 1 / 0 / 0 / 0 | 1 / 1 / 1 / 0 / 0 | 0 | 0 | 394 | one mesh update, metadata row only |
| Remove node | PASS | 0 | 1 / 1 / 1 / 1 / 1 | 0 | 0 | 393 | the freed slot's rows are zeroed, image back to base |
| Reused index with a different material | PASS (visual) | 0 / 1 / 1 / 0 (add path) | 22 / 22 / 22 / 22 / 23 | 5 | 352 | 394 | the re-added red cube renders red at the index the green cube held (screenshots viewed); the script's automatic hue test was miscalibrated for a lit surface and is corrected for future runs |
| Instance count | NOT_EXERCISABLE | | | | | | no MCP path sets `RenderCommandMesh3D.Instances` on a shape component |
| Material override swap on a live command | NOT_EXERCISABLE | | | | | | `assign_component_asset_property` only resolves registered assets and the donor material is a runtime instance; creating an asset would write to the project; the material-id and state-class columns are covered by the opacity and reused-index cases |
| Texture/sampler replacement | NOT_EXERCISABLE | | | | | | no MCP texture assignment on the probe material; native texture replacement is validated on the publication path (S13b record) |
| Skinning/deformation | NOT_EXERCISABLE | | | | | | fixture has no skinned or blend-shaped renderable |

In every exercised case the unrelated 393 draws kept their rows (cull-control
elements equal the affected row count, identity notifications zero, draws
unchanged apart from the add/remove cases) and the affected records reached the
image. Shared materials, material edits with retention, visibility, view
transitions and moving/stopping objects are covered by the S13b matrix rows
below. The scene-node layer tool (`set_layer`) does not reach render infos
(zero mesh updates, zero notifications); the render layer lives on the
component override, which is what the case drives. That gap is noted as a
separate tooling finding.

## Final regression

- Full S13b matrix on the final build (`reports/s13b-matrix-s13d-final/`):
  stationary-A-60s, add-node, remove-node, re-add-node, visibility-off,
  visibility-on, repeated-edits, publication-rejection, material-value-cube,
  material-shared, material-edit-retention, velocity-camera-object,
  view-B-then-return-A and cleanup-stationary all **PASS**.
- Motion probe on the final code (`reports/s13d-owners-ab/`): zero bytes per
  submesh update, zero cull-control/classification/visibility/transparency
  publication on transform-only motion, draws back to 393.
- Unit tests (`reports/unit-tests/s13d-final-tests.log`, the S13c filter plus
  the meshlet interop and MCP classes, 564 tests): 500 pass, 64 fail. The 61
  S13c-baseline failures are unchanged; the three additional failures
  (`McpServer_CanPersistAndLaunchWithPermissionPolicy`,
  `McpServer_ExposesViewportScreenshotTool`,
  `VulkanViewportCapture_UsesBoundedNonblockingFencePolling`) are
  source-contract tests that fail identically with the MCP registry change
  stashed (`s13d-mcp-prechange-check.log`), so they pre-exist. No compiler
  warnings in the touched files.

## Tooling finding: render-state tool failure without a stack

During the first mutation run the `get_render_state` tool failed with an
`ArgumentOutOfRange` index error after the opacity transition and kept
failing for the rest of that session (the S13b matrix that followed could not
run). Release editor builds compile exception logging out, so no stack was
recorded, and a Debug session did not reproduce it (six calls across the same
transitions succeeded, `reports/s13d-render-state-repro-debug/`). The tool
registry now attaches the exception type and stack to a failed tool response
(`McpToolRegistry.cs`, documented in the MCP protocol guide), and two further
Release runs of the same matrix plus the full S13b matrix did not reproduce
the failure. It remains an open, unattributed tool defect; the next
occurrence will carry its stack.

## Separate findings (not S13d)

- The material-state class row follows the last material that resolved into
  the class while `StateClassMaterialMap` keeps the first; the two disagree
  whenever a class holds several materials. Making the row follow the stable
  representative would also remove the remaining five to six material-state
  elements per merged-node move. Rendering is unaffected today because the
  shader treats both masked modes alike and only falls back to the row's
  material id when the per-draw id is zero.
- `set_layer` changes the scene node's layer without reaching render infos.
- The `get_render_state` failure above.

## Disposition

Validated for reachable scope. Met: the S13d gate for every selected
unchanged-state path (zero redundant writes and zero heap allocation on the
update path for unchanged rows; transparency and material-state rows publish
only when they change), initialization and real changes reach the render side
(mutation matrix and S13b rows), and no cost moved into uploads (per-stream
publication fell; nothing rose). Not exercised live: instance count, live
material override swap, texture/sampler replacement, skinning/deformation,
buffer growth beyond the initial capacity and retry after a failed
registration (reasoned in the second owner's section).
