# S13c: retain logical mesh/LOD registration by real mutation identity

Status: Active (opened September 26, 2026). Gate record for
[S13c](../../progress/rendering/vulkan-stall-remediation-results.md)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-094453-s13c-registration-retention/` (ignored,
disposable; findings are copied here).

## Entry evidence

Measured on the S13a/S13b final binary (isolated session `s13-final-0925i`,
PID 29204, `XREngine.Runtime.Rendering.dll`
`62F2861AC1040824D04DD8E1BCB80D4B569B1B60B44A8FE8E126C51D4568B34A`) with the
393-draw Sponza fixture at view A, using the S13a publication telemetry
(`get_s13a_publication_telemetry` deltas; `mcp-output/session-i-s13c-baseline-replies/`
in the S13 closeout run):

| Workload | Mesh-update calls | Registration attempts | Registration time | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Stationary 15 s | 0 | 0 | 0 ms | 0 |
| Add one probe cube (settled 4 s) | 3 | 3 | 1.16 ms | 9,304 |
| Move the probe cube 60 times (transform only) | 120 | 120 | 46.08 ms (0.77 ms per move) | 300,720 (5,012 per move) |
| Move `$MergedNode_0` 10 times (transform only, about 354 submeshes) | 7,074 | 7,074 | 1,059.10 ms (105.9 ms per move) | 11,395,632 (1.14 MB per move) |

Registration is 93 to 98 percent of the mesh-update body time in every
mutation workload, and every transform-only update re-registers each submesh
twice (one changed and one unchanged pass per edit). Identity-only dirty
notifications stay at zero throughout, so this is the remaining S13b-visible
cost, not publication feedback. The September 26 elevated capture of the same
binary shows zero samples in these owners while stationary, so the cost exists
only when commands are legitimately updated.

## Exact owning path

`GPUScene.TryUpdateMeshCommandCore` calls `ResolveLogicalMeshRegistration` for
every submesh of every updated command
(`XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs`).
`ResolveLogicalMeshRegistration`
(`GPUScene.AtlasManagement.cs`) then, on every call:

1. `CollectRenderableLodSet`: `RenderableMesh.GetLodSnapshot()` (array copy of
   the LOD list), `XRMeshRenderer.GetMeshes()` per LOD (array per level), and a
   `List<(XRMesh, float)>`.
2. `TryPopulateLogicalMeshState`: a second `List`, four temporary arrays
   (`previousMeshIds`, `newMeshIds`, `newMeshes`, `newMinProjectedRadiusPixels`),
   two `HashSet<uint>` when the state is referenced, and for every resident
   level `EnsureSubmeshInAtlas` → `AppendMeshToAtlas` → `EnsureAtlasBuffers` →
   `SyncLegacyDynamicAtlasState`, which clears and recopies the whole dynamic
   atlas triangle list and rebuilds the mesh-offset dictionary before
   `AppendMeshToAtlas` reaches its already-packed early return. It ends with an
   unconditional `UpdateLogicalMeshTableEntry` GPU write.
3. `AcquireLogicalMeshResidency`/`ReleaseLogicalMeshResidency` allocate a
   `HashSet<uint>` each time a logical mesh id changes.

## Hypothesis

A transform-only update re-registers a logical mesh whose complete dependency
set is unchanged. If the registration result is retained on the existing
`LogicalMeshState` together with a signature of that dependency set, and the
signature is re-checked without allocation on every registration attempt, a
current hit returns the retained ids with zero temporary lists/arrays/hash
sets, zero atlas-ensure calls and zero logical-table writes, while every real
dependency mutation still rebuilds exactly as today. Falsifier: a transform-only
motion window on the changed build that still shows registration allocation or
atlas-ensure calls, or any mutation case below whose ids, LOD selection or
dirty ranges differ from the baseline build.

## Dependency set of a retained registration

| Input | Owner that advances it | How the hit check observes it |
| --- | --- | --- |
| Renderable and submesh index | `GPUScene._renderableLogicalMeshIdMap` (per renderable/submesh) | the state itself is keyed by it |
| LOD list membership, order and thresholds | `RenderableMesh` under `_lodsLock` (construction, destroy, LOD mesh/material replacement) | new `RenderableMesh.LodRegistrationVersion` |
| Per-level mesh identity for the submesh | `XRMeshRenderer` submesh list / `Mesh` | non-allocating `RenderableMesh.TryCollectLodMeshes` refill compared by reference against `LogicalMeshState.Meshes` |
| Per-level geometry/topology | `XRMesh.GeometryRevision` | stored per level and compared |
| Mandatory resident mesh (the command's own mesh) | caller | stored and compared by reference |
| Streaming policy | `RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand` | stored as a flag and compared |
| Residency and atlas relocation | `AppendMeshToAtlas`, `RemoveSubmeshFromAtlas`, `MigrateMesh`, streaming reserve/commit/unregister, LOD streaming load/evict | new `GPUScene._atlasResidencyEpoch` bumped at every allocation change; LOD streaming writes invalidate the state directly |

## Acceptance (declared before editing)

- Correctness invariants: on a hit, `meshId`, `logicalMeshId` and `lodCount`
  equal what a rebuild would return; every mutation case rebuilds and produces
  the same LOD table entry, residency reference counts and dirty ranges as the
  baseline build; a failed registration leaves no retained signature.
- Target metric: transform-only motion registration cost per move
  (`meshUpdateRegistrationTicks`, `meshUpdateAllocationBytes`) and new counters
  for registration hits, rebuilds and atlas-ensure calls.
- Budget: on the changed build, the 60-move cube window and the 10-move merged
  node window show zero registration allocation attributable to registration
  (mesh-update allocation per move below 512 bytes, which is the remaining
  non-registration allocation measured on the baseline), zero atlas-ensure
  calls and zero logical-table writes on hits, and registration time per move
  at least ten times lower than the baseline. Same session, same fixture, same
  windows, three repeats each.
- Observer overhead: the S13a telemetry is already enabled in both baseline and
  changed sessions; no new per-frame instrumentation.
- Regression: S13b matrix rows add/remove/re-add, visibility, shared material,
  material-edit retention and view transition pass on the changed build with
  zero identity feedback; draws return to 393; native/descriptor endpoints flat.

## Baseline on the unchanged registration code (current tree)

Session `s13c-baseline` was built from the working tree before any S13c edit
(the same tree as the September 26 closeout plus the emulated-stereo and
push-descriptor fixes; registration code identical to the final binary).
Driver: `scratch/s13c_motion_probe.py`, report `reports/s13c-baseline-motion/`.
Per-unit values are per move (or per add/remove); three repeats each.

| Window | Registration attempts | Registration ms | Mesh-update body ms | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Cube add (settled) | 3 | 1.49 | 1.70 | 9,384 |
| Cube motion, 60 moves, repeats 1 to 3 | 2.00 | 0.87 / 0.85 / 0.88 | 0.94 / 0.92 / 0.95 | 4,981 / 4,958 / 4,886 |
| Merged node motion, 10 moves, repeats 1 to 3 | 707 / 786 / 786 | 93.1 / 102.3 / 100.5 | 94.9 / 104.4 / 102.5 | 1,139,563 / 1,266,430 / 1,266,168 |
| Cube remove | 783 | 101.1 | 103.2 | 1,261,392 |
| Cube re-add | 2 | 0.97 | 1.04 | 5,840 |

Two additional observations from the baseline: removing one probe cube
re-registers every other command (783 attempts, 101 ms) because atlas removal
marks the remaining commands for update, and the 60 cube moves in the first
merged-node repeat overlap the tail of the previous window (707 versus 786
attempts), so the second and third repeats are the comparable ones. Identity-only
dirty notifications stayed at zero in every window and draws returned to 393.

## Change under test

- `RenderableMesh`: `LodRegistrationVersion` advances on LOD list construction,
  destruction and LOD mesh replacement; `CollectLodMeshes` fills spans for one
  submesh index without allocating.
- `GPUScene`: `LogicalMeshState` retains its registration signature (LOD
  version, streaming policy, required resident mesh, command mesh id, per-level
  geometry revisions); `ResolveLogicalMeshRegistration` collects levels into a
  bounded lock-protected scratch, reuses a retained registration when the
  signature, level meshes, thresholds and per-level atlas residency all match,
  and otherwise rebuilds through a span-based `TryPopulateLogicalMeshStateCore`
  with stack-bounded scratch and no hash sets. Any rebuild clears the retained
  signature first. `AcquireLogicalMeshResidency`/`ReleaseLogicalMeshResidency`
  no longer allocate hash sets. `AppendMeshToAtlas` returns before ensuring
  atlas buffers when the mesh is already packed and the tier buffers exist, so a
  rebuild of an already resident mesh no longer resynchronizes the legacy
  dynamic-atlas mirror.
- Telemetry: `meshUpdateRegistrationHits`, `meshUpdateRegistrationRebuilds`,
  `meshUpdateAtlasEnsureCalls` and `meshUpdateLogicalTableWrites` join the S13a
  snapshot; atlas-ensure and table-write counts are sampled as deltas around
  each registration.

## Changed build: motion probe

Session `s13c-changed` (same tree plus the change under test; Release; build
clean with zero warnings). Same driver, same fixture, same windows,
`reports/s13c-changed-motion/`. New counters per unit: hits / rebuilds /
atlas-ensure calls / logical-table writes.

| Window | Registration attempts | Registration ms (baseline) | Mesh-update body ms (baseline) | Allocated bytes (baseline) | Hits / rebuilds / ensures / table writes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Cube add (settled) | 3 | 0.021 (1.49) | 0.36 (1.70) | 7,000 (9,384) | 3 / 0 / 0 / 0 (add-path rebuild not yet attributed, see below) |
| Cube motion, 60 moves, repeats 1 to 3 | 2.00 | 0.009 / 0.006 / 0.007 (0.87 / 0.85 / 0.88) | 0.080 / 0.065 / 0.067 (0.94 / 0.92 / 0.95) | 3,415 / 3,258 / 3,222 (4,981 / 4,958 / 4,886) | 2 / 0 / 0 / 0 |
| Merged node motion, 10 moves, repeats 1 to 3 | 707 / 786 / 786 | 0.160 / 0.173 / 0.184 (93.1 / 102.3 / 100.5) | 1.00 / 1.14 / 1.50 (94.9 / 104.4 / 102.5) | 443,189 / 492,636 / 492,713 (1,139,563 / 1,266,430 / 1,266,168) | 707 / 786 / 786 hits, 0 / 0 / 0 |
| Cube remove (re-registers the remaining commands) | 786 | 0.166 (101.1) | 1.17 (103.2) | 492,312 (1,261,392) | 786 / 0 / 0 / 0 |
| Cube re-add | 2 | 0.006 (0.97) | 0.070 (1.04) | 4,176 (5,840) | 2 / 0 / 0 / 0 (add-path rebuild not yet attributed) |

Outcome against the declared acceptance:

- Registration-attributable work on warmed unchanged registrations is zero:
  every transform-only attempt is a hit, with zero rebuilds, zero atlas-ensure
  calls and zero logical-table writes; registration time per move fell about
  120 times for the cube and about 550 times for the merged node, far beyond
  the declared ten-times budget. Identity-only dirty notifications stayed at
  zero; draws returned to 393.
- The declared allocation budget (below 512 bytes per mesh update) is **not
  met as written**: the cube still allocates about 3.3 KB per move (two update
  calls) and the merged node about 630 bytes per submesh update. That
  remainder is not registration (registration is now allocation-free by
  construction and its hit path performs no atlas or table work); it sits in
  the rest of the per-submesh update body (draw metadata, state class,
  transparency and flag resolution, snapshot capture), which is the S13d
  owner. The 512-byte figure was a wrong prediction of the baseline's
  non-registration allocation, recorded here rather than re-fitted.
- The first cube-add and re-add windows show hits only because the add path
  (`GPUScene.Add`) attributed its registration to a discarded local
  observation; the rebuilds happened (the new cubes rendered) but were not
  counted. Fixed by threading the caller's observation through `Add`; the
  rerun below records the attributed values.

## Changed build: S13b regression rows

`reports/s13b-matrix-s13c-changed/` (`s13b_matrix.py`, warm-up 20 s): add-node,
remove-node, re-add-node, visibility-off/on, material-shared,
material-edit-retention, view-B-then-return-A and cleanup-stationary all
**PASS** with zero identity-only dirty notifications; draws 394 with the cube
and 393 after removal/cleanup; every registration in those rows was a hit with
zero rebuilds, ensures and table writes (the add/re-add rebuilds were on the
unattributed add path, see above).

## Rerun after the add-path attribution fix

Session `s13c-changed` rebuilt with the caller's observation threaded through
`GPUScene.Add` (`reports/s13c-changed2-motion/`, two repeats): cube motion
0.009 / 0.004 ms and 3,301 / 3,348 bytes per move, merged node 0.144 / 0.163 ms
per move, all hits with zero rebuilds, ensures and table writes. The add and
re-add windows still report hits only: a newly created node's first
registration runs through the scene-level `GPUScene.Add(RenderInfo)` entry
outside the mesh-update telemetry scope, so it is not counted by these
counters; the rebuild is evidenced by the new command appearing (draws 394)
and rendering. The update-path re-add (`TryUpdateMeshCommandCore` →
`Add`) is now attributed. The cube-remove window in this rerun re-registered
393 commands rather than 786 because the previous window's second update pass
had already settled.

## Mutation cases

`scratch/s13c_mutation_probe.py`, `reports/s13c-changed2-mutation/`:

- Geometry replacement: setting `BoxMeshComponent.Box` through
  `set_component_property` rebuilds the component's mesh list, which creates a
  new `RenderableMesh` with a new `XRMesh`; the new command registered (draws
  394 → 395) and the resized cube rendered (`mcp-captures/s13c-changed2-mutation/`).
  The single update in that window was a hit on the previous command; the new
  renderable's registration is a fresh state (never retained), so it rebuilt
  through the scene-level add path as above. Twenty moves after the
  replacement are hits again (80 attempts, 80 hits, 0.094 ms total).
- Removal and re-addition: covered by the motion probe (`cube-remove`,
  `cube-re-add`) and by the matrix rows add-node/remove-node/re-add-node
  (screenshots return to baseline, draws 393/394).
- Atlas relocation: removing the cube compacts the dynamic atlas and
  re-registers every remaining command; on the changed build those are hits
  (their meshes keep atlas residency at adjusted offsets) and the matrix
  return-to-baseline image comparisons pass.
- Shared meshes/materials and material edits: matrix rows material-shared and
  material-edit-retention pass with hits only (material edits are not a
  registration dependency).
- Failed registration and retry: `TryPopulateLogicalMeshStateCore` clears the
  retained signature before any atlas work and returns without retaining on
  failure, so the next attempt rebuilds; reviewed in code, not exercised live
  (the fixture has no failing mesh).
- Not exercisable on this fixture (all renderables have one LOD; streaming is
  off): multiple LODs, threshold edits, active LOD changes, streaming load and
  eviction. Their dependency inputs are part of the signature
  (`LodRegistrationVersion`, per-level meshes and thresholds, streaming policy
  flag, per-level residency, the required mesh's level) and LOD load/evict
  mutate `LogicalMeshState.MeshIds` in place, which the hit check reads, so a
  hit after those mutations returns the same layout a rebuild would keep. This
  remains a recorded limit of the live validation, not a waiver.

Separate finding (not S13c, confirmed pre-existing and fixed September 26):
after the `Box` replacement and node deletion the scene kept 394 resident draws
instead of 393, so one command outlived the shape rebuild. The leak probe
(`scratch/s13c_leak_probe.py`, `reports/s13c-prechange-leak/`) reproduced it on
a build with every S13c file stashed (control add/delete 393 → 394 → 393; case
add, `Box` replacement, delete 393 → 394 → 394 → 394), so registration
retention is not the cause. Root cause: `RenderableComponent.Meshes_PostAnythingRemoved`
only unregistered a removed renderable's render info when its recorded world
instance matched the component's `World`, but registration stores the world's
render registration target (`ActiveRenderWorld`) there, so the comparison never
matched and `ShapeMeshComponent.RebuildMeshWhenAttached`'s clear-and-re-add left
the old command registered. The fix clears the recorded instance unconditionally, which
unregisters the render info from the scene (`RenderableComponent.cs`). On the fixed build the same probe
reports 393 → 394 → 394 → 393 (`reports/s13d-leak-fixed/`), and the S13d
motion probe's add/remove/re-add windows still return to 393.

## Regression tests

Filtered run on the changed tree (`reports/unit-tests/s13c-tests.log`, 496
tests): 435 pass, 61 fail. The same filter on the pre-change source (the S13c
files stashed, `s13c-pre-change-check.log`) fails the same GPU-scene,
LOD-streaming and contract tests (`LOD_RequestBuffer_DrainReturnsAndClearsRequestedMasks`,
`LOD_StreamOnDemand_ServicePump_LoadsRequestedLevelsAndClearsMask`,
`GPUScene_CullingBounds_UseRenderInfoBasisWhenAvailable`,
`VulkanIndirectGeometryUsesRobustAccessAndRejectsInvalidAtlasIndices`,
`MeshGeometryLayoutFeedsGpuSceneIndirectAndMeshletRecords`,
`Vulkan_ImGuiOverlay_UsesExplicitSwapchainLayoutHandoff`), so no failure is
introduced by this change; the other failing classes in the wider filter are
the pre-existing shadow-atlas, lifecycle and OpenXR contract drift already
recorded for the S13 closeout. One source contract was updated for this change
(`GpuRenderingBacklogTests` LOD threshold resolution text) and passes.

## Disposition

**Validated** for the reachable scope on September 26, 2026 (Release, Vulkan
Advanced, CpuDirect, 393-draw Sponza fixture): registration-attributable work
on warmed unchanged registrations is zero (hits only; zero rebuilds,
atlas-ensure calls and logical-table writes), transform-only registration time
fell about 120 times (cube) and about 550 times (merged node), the S13b rows
still pass with zero identity feedback, and no unit test regressed. The
declared allocation budget is not met by the surrounding mesh-update body
(about 630 bytes per submesh update remain outside registration), which is
carried into S13d as the measured next owner rather than absorbed here. Live
coverage of multi-LOD, threshold, active-LOD and streaming cases is a recorded
limit. Test clearance for new focused coverage is not yet requested.
