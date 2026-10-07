# S13f: retain plan-derived operation metadata

Status: Deferred for its declared mechanism (September 26, 2026): the
recording-time family discovery and per-family stage scans of the sealed plan
cost about 0.4 microseconds per primary recording, allocate nothing, and are
below the declared entry threshold; the per-plan-generation rebuild that the
measurement did expose (stable-bin sealing and raster pipeline preparation,
about 3.9 ms and 360 KB per recording) is bound to the per-frame scene
realization and cannot be retained across generations under the current
lifetime contract, so it is recorded here as attributed handoff evidence rather
than fixed. Gate record for
[S13f](../../progress/rendering/vulkan-stall-remediation-results.md)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-180440-s13f-plan-metadata/` (ignored,
disposable; findings are copied here). Fixture, camera and session type are
the S13a ones: 393-draw Sponza2 OBJ import without `OptimizeMeshes` or
post-import merging (`scratch/unit-world-s13f.jsonc`, derived from the current
generated settings with the locally enabled avatar import disabled; SHA256
`4533B5F71F644DF075CC8665EB5FBBE8640FEDADBADF6BBF8A608DB49052FB17`), camera A
`(-20, 2, 4)` looking at `(-20, 2, -8)`, named isolated Release session with
the S13a telemetry enabled, Advanced pipeline, CpuDirect, 393 resident draws.

## Exact owning path (todo item one)

A desktop frame reaches the sealed plan and its recording-time consumers as
follows:

1. `VulkanFrameLoop.PrimaryRecordingPreparation`: every fresh frame authors
   its operation arrays and calls `FramePlanBuilder.BuildAndSeal`, which lowers
   them into the frame slot's preallocated `FrameOperationStream`s, orders
   them, compiles the output DAG, rebuilds operation keys and planner context
   plans, and publishes the `FramePlan` with a new `Generation` (a
   monotonically increasing counter, one per seal). A retried accepted frame
   reuses `acceptedPlan.LogicalPlan` instead of resealing. There is no
   cross-frame plan retention: the same slot is reset and republished when it
   next begins a frame, and `Publish` thaws the slot's Advanced family bins.
2. `FramePlan.AttachAdvancedVisibilityPlanLeases` (at seal): scans the static,
   dynamic-overlay and texture-upload streams once, collects the distinct
   `AdvancedVisibilityFamilyReservation`s into the plan
   (`_advancedPlanLeaseReservations`, bounded by the output capacity) and
   acquires the bank leases. This is the plan-derived family metadata the
   phase names, and it already exists once per sealed plan.
3. `VulkanCommandRuntime.TryPrepareAdvancedVisibilityOperations` (primary
   recording, inside `AdvancedVisibilityStorageGate`): re-derives the family
   set by scanning every header of the recording stream, with an additional
   check that no two Advanced operations share an output with different
   reservations, then calls `TryPrepareAdvancedVisibilityFamily` per family.
4. `TryPrepareAdvancedVisibilityFamily`: scans every header of the recording
   stream twice per family (stage classification/preparation, then stage
   association) and, per stage, calls `FramePlan.HoldsAdvancedVisibilityPlanLease`
   (a lock plus a linear search over the sealed reservations). The raster
   stage of each family also seals the slot-owned stable bins
   (`TrySealAdvancedVisibilityBins`: geometry stream, manifests, submission
   plans), retains templates, prepares the raster pipelines per bin header and
   realizes the set-1 family resources for the frame slot.

Other per-recording passes over the whole stream, each a single bounded loop
over numeric headers, exist for required-producer outcome capacity and source
ranges, progressive command-chain admission (two passes, only when deferral is
possible), the operation census, inline query preparation, the initial
primary context, mesh-task pipeline association (warm manifests), the plan's
native-recording validation, output-completion verification per receipt, and
the recording loop itself. They consume per-frame state (chains, queries,
receipts, targets), not only structural metadata, and are listed for the
visit count rather than as S13f candidates.

## Existing reuse audit (todo item one)

| Mechanism | Where | Reuse today |
| --- | --- | --- |
| Sealed frame plan | `FramePlanBuilder.BuildAndSeal`, `FramePlan.Publish` | Rebuilt per fresh frame in slot-owned storage that grows only to a high-water mark; operation keys, planner context plans, output DAG and Advanced reservations sealed once per generation; retained only for retries of an accepted foreground frame (`acceptedPlan.LogicalPlan`). |
| Pipeline variant manifest | `VulkanPipelineManager.GetOrBuildVariantManifest` | Cached by (render-graph plan signature, pipeline demand signature, submission strategy, dynamic rendering); `Build` (two operation passes) runs only on a miss; `WarmupCompleted` short-circuits `TryAdmitPrimaryGraphicsPipelines` to mesh-task association. |
| Admitted frame data | `TryAdmitPrimaryFrameDataStructures` | Iterates manifest requirements (not the stream); a ledger signature set (`AdmittedFrameDataPreparationSignatures`) skips already prepared draws; runs only when progressive deferral is possible. |
| Advanced family bins | `FramePlan.GetAdvancedVisibilityFamilyBins`, `VulkanPreparedStableBinStream` | Slot-owned, capacity-preallocated bin streams; thawed at every `Publish` and `Reset`, so the geometry stream, headers, submission plans and raster pipelines are rebuilt at every plan generation. Bin resource manifests are cached by (topology generation, bin key) but canonical visibility headers use stream-owned manifest views instead. |
| Scene publication per family | S13e | One preparation per compatible family key; stages reuse the immutable state. |

So the only plan-derived structural metadata that is computed at seal and then
recomputed by recording is the Advanced family set (step 2 versus step 3), plus
the per-family stage passes (step 4) that a per-plan index of Advanced
operation ordinals could replace. The family bins are plan-held metadata that
is rebuilt per generation; whether they can be retained is decided below.

## Entry threshold and disposition rule (declared before measurement)

- Mechanism: the recording-time structural scans of one sealed plan for
  Advanced visibility: the family discovery scan (step 3), the two per-family
  stage passes and per-stage lease checks (step 4), measured against the
  seal-time collection (step 2) that already holds the family set.
- Actionable only if, on the unchanged stationary window or the moving window,
  the discovery scan plus the per-family stage passes and lease checks cost at
  least 0.10 ms per primary recording (about 1.4 percent of the S13a warmed
  7.1 ms primary-recording median), or the discovery-and-preparation path
  allocates on the unchanged path, or any plan-derived structural metadata is
  rebuilt more than once per sealed-plan generation beyond the discovery scan
  itself.
- Otherwise S13f is Deferred with the measured cost recorded: every fresh
  frame seals a new plan generation, so an index retained per generation could
  save at most the discovery and stage passes of that one recording, and that
  saving would be below the threshold.
- Falsifier for a deferral: a measured scan cost at or above the threshold, a
  nonzero allocation per recording on the unchanged path, or a second
  rebuild of the family set per generation other than the discovery scan.

## Attribution instrumentation (observation only)

Counters in the S13a telemetry snapshot, incremented under the existing
telemetry guard: per family discovery (one per primary recording) the sealed
operations scanned, Advanced operations among them, families found, headers
visited, gate-wait ticks, discovery ticks, discovery-plus-preparation ticks
inside the gate and bytes allocated by the recording thread across discovery
and every family preparation; per family the headers visited by the two stage
passes and the lease checks made; per sealed plan the headers visited by the
seal-time reservation collection; and, because the first probe showed the
gated path costing milliseconds, per-step calls, ticks and allocated bytes for
the family preparation steps (`S13aAdvancedFamilyStep`: target closure, scene
publication, bin sealing with its three sub-steps, template retention, raster
pipelines, stage association, raster realization, compute pipelines, state
association, native compute, late closure). Read through
`get_s13a_publication_telemetry`. Default-off behavior is preserved at every
branch; the only unconditional additions are one stack-local integer
increment per Advanced operation in the discovery loop and a handful of
boolean locals where a call was hoisted out of an `if` condition so it could
be timed.

## Build environment and evidence provenance

The main checkout was being edited concurrently by another session during this
work (VR pawn and OpenXR files, unrelated to rendering), and three isolated
session builds from it failed on transient mid-edit states. The evidence
binary was therefore built from a detached git worktree at HEAD `9fee4b983`
with exactly this phase's four-file instrumentation patch applied
(`scratch/s13f-instrumentation.patch`, then the per-step and sub-step probe
patches), the OpenVR.NET and OscCore submodule sources copied from the main
checkout, the untracked repository-managed native bridge and Steam Audio
binaries copied in, and the session environment pointing `XRE_GAME_ASSETS_PATH`
and `XRE_ENGINE_ASSETS_PATH` at the main checkout's asset folders. The session
is `s13f-wt` under the worktree's shared MCP session folder; it built with zero
compiler warnings. The measured code is identical to the committed rendering
code plus the observation counters.

## Entry evidence (instrumented build, no behavior change)

Driver: `scratch/s13f_plan_probe.py`, reports `reports/s13f-entry2/`
(per-step) and `reports/s13f-entry3/` (bin-sealing sub-steps). Captures in
`mcp-captures/s13f-entry2/` were viewed: camera A looks along the exterior
Sponza wall with the sky visible, exposure dark as recorded for this camera in
S13a; the probe cube appears with its transform gizmo after the add. Per
primary recording (one family discovery per recording; the desktop output is
the only family; about 68 to 76 recordings per second):

| Window | Ops per recording | Advanced ops | Families | Discovery visits | Family scan visits | Lease checks per family | Seal visits per seal | Gate wait us | Discovery us | Discovery plus preparation us | Allocated bytes | Scene prepare calls per family | Reuses per family | Failures |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Stationary 20 s | 24 | 7 | 1 | 24 | 48 | 7 | 25 | 0.11 | 0.44 | 4,417 | 404,536 | 1.0 | 6.0 | 0 |
| Cube added, settled 5 s | 37 | 7 | 1 | 37 | 74 | 7 | 38 | 0.13 | 0.44 | 4,459 | 405,459 | 1.0 | 6.0 | 0 |
| Cube motion, 60 moves | 37 | 7 | 1 | 37 | 74 | 7 | 38 | 0.12 | 0.43 | 4,352 | 405,463 | 1.0 | 6.0 | 0 |
| Post-motion 8 s | 37 | 7 | 1 | 37 | 74 | 7 | 38 | 0.13 | 0.48 | 4,471 | 405,459 | 1.0 | 6.0 | 0 |
| Cube removed 5 s | 37 | 7 | 1 | 37 | 74 | 7 | 38 | 0.11 | 0.49 | 4,576 | 404,541 | 1.0 | 6.0 | 0 |

Scans (the declared mechanism): the discovery scan visits every header once
(24 to 37 per recording) and the two per-family stage passes visit each header
twice per family, so 48 to 74 header reads plus 7 lease checks per family; the
seal-time collection visits 25 to 38 headers per plan. Together they cost about
0.4 to 0.5 microseconds per recording and allocate nothing. The S13e counters
reproduce (one scene preparation and six reuses per family, zero failures).
On the still scene no publication changed (zero mesh updates, zero content-dirty
swaps, one publication reuse per recording); cube motion produced 60 content
dirty swaps and 114 command mutations, as S13d recorded.

Per-step attribution of the gated discovery-and-preparation path, stationary
window (the other windows agree within a few percent):

| Step | Calls per recording | us per recording | Bytes per recording |
| --- | ---: | ---: | ---: |
| Target closure (raster and late raster) | 2 | 65 | 800 |
| Scene publication (S13e path) | 1 | 183 | 152 |
| Bin sealing (`TrySealAdvancedVisibilityBins`) | 1 | 2,562 | 0 |
| Template retention | 1 | 21 | 0 |
| Raster pipelines (`TryPrepareVisibilityRasterPipelines`) | 1 | 1,319 | 361,560 |
| Stage association (target, resolve, publication) | 7 | 24 | 0 |
| Raster realization (payloads and set-1 frame-slot state) | 1 | 55 | 0 |
| Compute pipeline lookup | 1 | 5 | 920 |
| State association | 7 | 18 | 0 |
| Native compute (closure, descriptors, pipelines) | 3 | 87 | 20,552 |
| Late closure (target, pipelines, descriptors) | 1 | 22 | 920 |
| Sum of steps | | 4,361 | 384,903 |
| Discovery plus preparation total | | 4,417 | 404,536 |

Bin sealing sub-steps (`reports/s13f-entry3/`, stationary window):

| Sub-step | us per recording | Bytes per recording |
| --- | ---: | ---: |
| Geometry stream and freeze ordering (`TryBuildVisibilityGeometryStream`) | 2,242 | 0 |
| Header grouping and manifest resolution (`TryResolveManifests`) | 110 | 0 |
| Submission-plan sealing (`TrySealSubmissionPlans`) | 277 | 0 |
| Bin sealing total | 2,630 | 0 |

The geometry stream dominates: about 5.7 microseconds per payload for 393
payloads, spent in the per-payload draw and geometry lookups, closure
validation, manifest reset, record append and the insertion-sort freeze of
the full record structs. The cube windows (394 payloads) and the moving window
agree within five percent, so on this fixture the cost is per payload and
independent of motion.

## Why the family bins are not retained across generations here

The bins are rebuilt every frame because `FramePlan.Publish` and `Reset` thaw
them, but keeping them sealed would only help if a later generation could
prove the sealed content still applies. Every sealing input is a value type
or a span that can be compared exactly (payloads, deformation slices, indirect
ranges and payload indices, the deformation publication scalars, the
canonical publication's epoch, topology generation and table sequences, the
submission resolution, lane capabilities, output policy, diagnostic node, pass
index, view mask, bin context compatibility, target closure and the raster
program link generations), so an exact retained-seal key is expressible. The
blocker is the scene realization: each geometry closure and bin key binds to
`VulkanAdvancedScenePublicationState.NativeGeneration`, which the scene
resource runtime assigns from `NextNativeGeneration()` at every realization,
and S13e measured one realization per family per frame (the frame record is
part of the realization entry). A retained seal would therefore mismatch on
every frame unless the bins' binding contract were changed to a
publication-stable geometry identity (buffer identities, slice generations,
epoch, topology and geometry sequences) with the per-frame realization
validated separately. That is a lifetime-contract change to the stable-bin
stream, the geometry closure validation and the raster recording's checks,
which the todo assigns to an explicit concurrency and lifetime review rather
than to this phase. The same binding is why the raster pipelines are prepared
again per frame: the headers are new each frame, so `TryPrepareVisibilityRasterPipelines`
finds no prepared pipeline and rebuilds the graphics pipeline key per header
(the 920-byte allocation per header, 393 headers, is the 361,560 bytes).

## Disposition

Deferred for the declared mechanism, with the following recorded:

- Unavoidable per-frame visits, counted and justified: one discovery pass
  (24 to 37 headers) so the recording can reject a reservation that changed
  within a sealed plan, two stage passes per family (48 to 74 headers) that
  classify and associate stages, seven lease checks per family, and one
  seal-time collection (25 to 38 headers). Total about 0.4 to 0.5 microseconds
  and zero bytes per recording, below the 0.10 ms threshold by more than two
  orders of magnitude; consuming the plan's collected reservations instead
  would remove the discovery pass but not the within-plan consistency check.
- The falsifier's allocation clause fired for the gated path as a whole, but
  the allocation is attributed entirely to steps outside the scans: raster
  pipeline preparation (361,560 bytes), native compute closures (20,552),
  target closures (800), and small constant amounts in scene publication,
  compute pipeline lookup and late closure. None is plan-derived structural
  metadata.
- Handoff to S13g (bound warmed pipeline-readiness work): raster pipeline
  preparation costs 1.32 ms and 361,560 bytes per recording on the unchanged
  path because the per-header graphics pipeline key is rebuilt each frame
  (`VulkanCanonicalVisibilityPipelineFactory.TryPrepare`) even though the
  pipeline object itself is cached; native compute closures add 87 us and
  20,552 bytes. The exact accepted dependency generation for reuse is
  (program reference, program link generation, coverage, cull mode, target
  closure).
- Handoff for the stable-bin rebuild (2.6 ms per recording, allocation
  free): retention across plan generations needs the publication-stable
  geometry identity described above. The sub-step split shows the geometry
  stream construction and its insertion-sort freeze at 2.2 ms (85 percent),
  so a bounded algorithmic reduction of that per-payload path (sorting an
  index array instead of moving full records, hoisting per-payload lookups
  that repeat for an unchanged publication) is the cheaper next decision and
  needs no lifetime-contract change; it belongs to S13h's measured-bottleneck
  selection or a follow-up under S13, not to this phase.
- Not exercised: multiple output families (the emulated stereo session was
  not repeated; the scan cost scales linearly with families and operations
  and would stay far below threshold at three families and 37 operations),
  changed operation order beyond the cube add/remove (24 to 37 operations,
  correctly rescanned), resize and MSAA change (no driver on this fixture).
