# S13e: prepare shared Advanced scene state once per compatible family

Status: Validated for reachable scope (September 26, 2026): the family
preparation runs the scene publication once per compatible key, verified on
one and three families per frame, across the incompatible-mutation cases,
restart and the full S13b matrix; limits recorded below. Gate record for
[S13e](../../todo/rendering/vulkan-stall-remediation-todo.md#evidence-index)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260926-162532-s13e-family-preparation/` (ignored,
disposable; findings are copied here). Fixture, camera and session type are
the S13a ones: 393-draw Sponza (`unit-world-s13a.jsonc`), camera A, named
isolated Release session with the S13a telemetry enabled.

## Exact owning path

`VulkanCommandRuntime.TryPrepareAdvancedVisibilityFamily`
(`XREngine.Runtime.Rendering.Vulkan/.../Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs`)
iterates every `AdvancedVisibility` operation of one family (visibility
preparation, raster, late compute, late raster, optional MSAA resolve, then
ambient occlusion, work classification and native opaque shading once per
frozen view) and calls `TryPrepareAdvancedVisibilityScenePublication` for each
of them. That call, per stage:

1. `Begin`/`AttachFramePlan` on the runtime's shared
   `_advancedVisibilityPublicationPreparation` recording;
2. `CaptureGlobalResourcesWithAuthoringViews`: copies the package's views,
   passes, pass coverage and diagnostic requests into the recording and
   acquires a canonical publication lease on the shared database;
3. `TryPrepareAdvancedScenePublication`: under the scene-resource runtime gate,
   finds the frame slot's existing native realization of the same
   (database, publication, views, frame, passes, coverage, diagnostics) entry
   and arms a new receipt/use on it, or realizes it when absent;
4. `TryTransferRetainedLifetimesTo`: preflights and adopts the lease and use
   into the frame-slot lifetimes, where an exact duplicate (same database,
   publication and native publication state) is disposed instead of adopted;
5. `Reset`.

The family loop then requires every stage's resulting
`VulkanAdvancedScenePublicationState` to be identical (a different state is a
`RendererTerminal` failure), which is the existing statement that the stages
share one publication.

## Compatibility key (todo item two)

Inputs the scene publication preparation actually consumes:

| Input | Source | Varies per stage? |
| --- | --- | --- |
| Renderer/device and scene-resource runtime | `ResourceRuntime.AdvancedSceneResources` | no |
| Backend package (current object) and its canonical database + publication reference | `request.BackendPackage.TryGetCurrent` | no by `MatchesFamily` (`BackendPackage`, `Publication`); the *current* object can still change if the package is superseded mid-recording, which the per-stage freshness test catches |
| Frame-plan generation and logical frame slot | `framePlan.Generation`, `framePlan.FrameSlot` | no (one sealed plan) |
| Resource (native) frame slot | `recordingState.FrameDataSlot` | no |
| Family reservation | `request.Reservation` | no by `MatchesFamily` |
| Authoring views | `request.Views` | no by `MatchesFamily` |
| Frame record, passes, pass coverage, diagnostics | the package | no |
| Native generation and descriptor sets | the frame slot's realization keyed by the row above | no |
| Stage, phase, target, native view index, resource-planner scope | the operation | yes, but not consumed by the scene publication preparation (the scene resource runtime never reads the thread planner scope) |

So one family has one compatible key: (runtime, current package object,
database, publication, frame plan generation and slot, resource frame slot,
reservation, views). Stage-varying inputs feed the target closures, bins,
pipelines and associations, which stay per stage.

## Attribution instrumentation (observation only)

Counters in the S13a telemetry snapshot, incremented from the Vulkan family
preparation under the existing telemetry guard: families prepared, stages
iterated, scene publication preparation calls, calls that newly realized a
native publication (`newlyAttempted`), stages that reused a family result, and
the ticks spent inside `TryPrepareAdvancedVisibilityScenePublication`. Per
frame normalization uses the GPU scene swap count from the same snapshot.

## Entry evidence (instrumented build, before any change)

Session `s13e-probe` (Release, telemetry on), `reports/s13e-entry/` (the S13c
motion probe with the family counters) and `reports/s13e-entry-matrix/`.
Per family (the desktop output is the only family on this fixture; the GPU
scene swaps twice per rendered frame, so families per swap is 0.5):

| Window | Families per swap | Stages per family | Scene publication preparation calls per family | Reuses | Milliseconds per call | Milliseconds per family |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Stationary 15 s | 0.496 | 7.0 | 7.0 | 0 | 0.033 | 0.23 |
| Cube add | 0.494 | 7.0 | 7.0 | 0 | 0.021 | 0.15 |
| Cube motion, 60 moves | 0.500 | 7.0 | 7.0 | 0 | 0.018 | 0.13 |
| Merged node motion, 10 moves | 0.500 | 7.0 | 7.0 | 0 | 0.018 | 0.13 |
| Cube remove / re-add | 0.5 | 7.0 | 7.0 | 0 | 0.018 to 0.020 | 0.13 to 0.14 |

Every family iterates seven stages (visibility preparation, raster, late
compute, late raster, ambient occlusion, work classification, native opaque
shading; no MSAA resolve on this fixture) and runs the scene publication
preparation seven times. The `newlyAttempted` flag from the shared prepared
recording is true on every call because each call resets that recording, so it
does not distinguish a frame slot's existing native realization from a new
one; the next instrumentation step adds that split at the scene-resource
runtime (slot hit versus realization). The repeated cost per stage is small in
absolute terms (about 0.02 to 0.03 ms), but it is exactly the repeated work
this phase names: six of the seven calls acquire a lease and arm a native use
that the frame-slot transfer then discards as an exact duplicate.

## Hypothesis and acceptance (declared before editing)

- Change: in `TryPrepareAdvancedVisibilityFamily`, resolve the stage's current
  backend package first (the same freshness test the per-stage preparation
  performs); when the family already holds a valid scene state prepared from
  that same package object, reuse it for the stage and skip the preparation;
  otherwise prepare as today and let the existing family equality check decide.
  The per-stage target closures, bin sealing, pipeline readiness, associations
  and the resource-planner scope stay per stage. No lifetime path changes: the
  one preparation transfers its lease and native use exactly as the first
  stage does today, and no duplicate is created for later stages.
- Budget: on every unchanged-path window, scene publication preparation calls
  per family fall from 7.0 to 1.0 and reuses rise to 6.0 with stages per
  family unchanged at 7.0; scene-resource slot realizations per frame are
  unchanged from the instrumented entry rerun (they are the real work) and slot
  hits fall by six per family; preparation milliseconds per family fall to
  about one call's worth (at most 0.05 ms); scene publication failures stay
  zero.
- Correctness: full S13b matrix passes; the S13e family matrix passes on the
  unchanged path, a moving object, a shader reload, a TSR render-scale change
  and restore, and an armed publication rejection (presents advance, zero new
  validation errors, image continuity where required); renderer restart with
  work in flight recovers to the same image; the emulated two-pass stereo
  session shows two families per frame each prepared once, with both eyes
  rendering the probe cube; no unit test regresses.
- Falsifier: any window with more than one preparation call per family on the
  unchanged path, any scene publication failure, any deferred family
  (`RecordingDeferredReason`) on the unchanged path, a validation error, or a
  matrix row that passed at entry and fails after the change.

## Entry matrix (instrumented build, before any change)

`scratch/s13e_family_matrix.py`, `reports/s13e-entry-matrix/`. Every row
ran seven preparation calls per family with zero reuses; presents advanced,
no validation errors, frame outcome Completed, no scene publication failure:

| Case | Families per swap | Calls per family | Preparation ms per swap | Image diff versus before |
| --- | ---: | ---: | ---: | ---: |
| Stationary 20 s | 0.500 | 7.0 | 0.062 | 0.006 |
| Moving cube (30 moves) | 0.499 | 7.0 | 0.060 | |
| Shader reload | 0.453 | 7.0 | 0.059 | 0.004 |
| TSR render scale 0.75 | 0.485 | 7.0 | 0.062 | 0.015 |
| TSR render scale restored | 0.486 | 7.0 | 0.062 | 0.005 |
| Armed publication rejection (three) with moves | 0.500 | 7.0 | 0.063 | |

These rows are the correctness reference for the same cases after the change.

## Lifetime ownership (todo item four)

- Reuse: the family's single preparation acquires one canonical publication
  lease and one native use, and `TryTransferRetainedLifetimesTo` moves both
  into the frame slot's retained lifetimes before recording; every later stage
  consumes the immutable state value and adds no lease, use or retain. The
  frame slot releases the retained lifetimes when it retires, exactly as the
  first stage's transfer does today; the six duplicate acquire-then-discard
  cycles per family disappear rather than being retained.
- Successful transfer: unchanged path (`TryAdoptCanonicalPublication`).
- Partial failure: a failed preparation returns before any stage association,
  and the prepared recording's `Reset` releases whatever it acquired; the
  family holds no state (`familySceneState` stays invalid), so nothing is
  cached as ready. A failure is now counted (`AdvancedScenePublicationFailures`)
  instead of being indistinguishable from a cache miss.
- Retry: the next recording attempt starts with no family state and prepares
  again; there is no cross-frame cache to invalidate.
- Cancellation and frame-slot retirement: unchanged; the retained lifetimes
  belong to the frame slot, not to the family loop.
- Supersession: a stage whose current backend package differs from the one the
  family state was prepared from does not reuse; it prepares against its own
  package and the pre-existing family equality check rejects a mismatched
  state as before.

## Entry rerun with the slot split (before the change)

`reports/s13e-entry2/`. Per family: 7.0 stages, 7.0 preparation calls, 7.0
recording attempts, 0 reuses; per GPU scene swap: 2.97 to 3.00 scene-resource
slot hits and 0.50 slot realizations (one realization per family, the six
other calls arm the existing realization); 0.13 to 0.18 ms per family; zero
failures. This confirms that six of the seven calls per family are exact hits
on the frame slot's realization, discarded again by the lifetime transfer.

## Change

`VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs`,
`TryPrepareAdvancedVisibilityFamily`: each stage first resolves its current
backend package (the freshness test the preparation performed internally);
when the family already holds a valid scene state prepared from that same
package object, the stage reuses it, otherwise it prepares as before and the
existing family equality check applies. The family records the package object
alongside its scene state. `TryPrepareAdvancedVisibilityScenePublication` now
also returns whether the recording newly attempted the native realization, and
the family reports stages, calls, attempts, reuses, ticks and failures to the
telemetry; the scene-resource runtime reports slot hits and realizations.

## Result (fixed build, same session type, fixture and windows)

Motion probe `reports/s13e-fixed/` and family matrix
`reports/s13e-fixed-matrix/`:

| Window | Families per swap | Stages per family | Calls per family (entry, now) | Reuses per family | Slot hits per swap (entry, now) | Slot realizations per swap (entry, now) | Failures | Milliseconds per family (entry, now) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Stationary 15 s | 0.484 | 7.0 | 7.0, 1.0 | 6.0 | 2.97, 0.0 | 0.50, 0.48 | 0 | 0.180, 0.143 |
| Cube add | 0.495 | 7.0 | 7.0, 1.0 | 6.0 | 2.98, 0.0 | 0.50, 0.50 | 0 | 0.131, 0.100 |
| Cube motion, 60 moves | 0.500 | 7.0 | 7.0, 1.0 | 6.0 | 3.00, 0.0 | 0.50, 0.50 | 0 | 0.126, 0.092 |
| Merged node motion, 10 moves | 0.500 | 7.0 | 7.0, 1.0 | 6.0 | 3.00, 0.0 | 0.50, 0.50 | 0 | 0.135, 0.102 |
| Cube remove / re-add | 0.50 | 7.0 | 7.0, 1.0 | 6.0 | 2.99, 0.0 | 0.50, 0.50 | 0 | 0.130, 0.099 / 0.127, 0.090 |

Family matrix (every row PASS): stationary, moving object, shader reload, TSR
render scale 0.75 and restore, and the armed publication rejection all show
1.0 preparation call and 6.0 reuses per family, zero failures, presents
advancing, zero new validation errors, frame outcome Completed, and image
continuity where required (shader reload 0.0001, TSR restore 0.004 changed
fraction versus before). Slot realizations per swap stayed at one per family
on every window, so the remaining call per family is the real work.

The budget is met and the falsifier did not fire. Milliseconds per family fell
by about a quarter to a third rather than by six sevenths, because the
remaining call is the realizing one (about 0.09 ms) and the six removed calls
were the cheap hits (about 0.018 ms each); the phase's gate is the once-per-key
mechanism, which the counters establish, and the recording thread cost is
recorded as measured.

## Multiple families, views and teardown

- Emulated two-pass stereo session (`reports/s13b-stereo-s13e/`, the S13a
  emulated VR environment): 1.50 families per swap, that is three families per
  frame (desktop output and the two eye outputs), each with 7.0 stages, 1.0
  preparation call and 6.0 reuses, zero failures; the stereo-eyes row passes
  (both eye captures change when the probe cube is added, draws 394).
- Renderer restart with work in flight (`reports/s13b-restart-s13e/`): the
  renderer-restart row passes (A/B/A views after a transactional Vulkan
  restart, zero identity notifications, no pending retirement, image return
  within tolerance).
- Alternating frame slots and early/late visibility stages are exercised by
  every frame of every window above (seven stages per family, slot
  realizations one per family per generation).
- Resize/AA change: the TSR render-scale change stands in for a resource
  generation change; a window resize and an MSAA sample-count change were not
  driven (no MCP tool for either on this fixture), so a family whose MSAA
  sample count changes mid-run is reasoned (its `MatchesFamily` key changes,
  which today's per-stage validation already treats as a different family)
  rather than exercised.
- Material/texture replacement: the S13b material rows (value edit, shared
  material, edit retention) pass on the fixed build.

## Regression

- Full S13b matrix on the fixed build (`reports/s13b-matrix-s13e/`):
  stationary-A-60s, add-node, remove-node, re-add-node, visibility-off,
  visibility-on, repeated-edits, material-value-cube, material-shared,
  material-edit-retention, velocity-camera-object, view-B-then-return-A and
  cleanup-stationary **PASS**; renderer-restart **PASS**
  (`reports/s13b-restart-s13e/`); stereo-eyes **PASS**
  (`reports/s13b-stereo-s13e/`). The full-matrix run's publication-rejection row failed once: its end-of-window render-state read returned an empty, rejected publication eight seconds after the three injected rejections had been consumed (`reports/s13b-matrix-s13e/`), while the rows before and after it passed and presents kept advancing. Two reruns of the same row on the same build (`reports/s13b-rejection-recheck3/`, `recheck4/`) passed, and the S13e matrix's own armed-rejection window passed on the fixed build with zero scene publication failures, so the single failure is recorded as a transient diagnostic read of the publication between a rejection and its retry, not as a regression; it is the same class of transient read as the render-state failure recorded under S13d.
- Unit tests (`reports/s13e-tests.log`, the S13d filter plus every test whose
  name contains Vulkan, 675 tests): 583 pass, 92 fail. Against the S13d final
  run (64 failures) the additional failures are all outside this change: ten
  Vulkan command-chain and packet-volatility tests pass when the Vulkan filter
  runs alone on the same tree (`s13e-vulkan-current-tests.log`, an ordering
  interaction of the wider filter), three device/registry isolation tests fail
  identically with the two changed Vulkan files stashed
  (`s13e-four-tests-prechange.log`), and `AbstractRendererCurrent_IsThreadScopedForOpenXrEyeWorkers`
  is a source-contract test on `AbstractRenderer.cs` and the OpenXR eye
  workers, files this change does not touch. Three baseline failures now pass
  (`BitmapFontAtlas_ExtractsAlphaCoverageIntoR8RedChannel`,
  `MeshGeometryLayoutFeedsGpuSceneIndirectAndMeshletRecords`,
  `VulkanIndirectGeometryUsesRobustAccessAndRejectsInvalidAtlasIndices`),
  which this change does not explain either; they are noted, not claimed. No
  compiler warnings in the touched files.

## Disposition

Validated for reachable scope. Met: one scene publication preparation per
compatible family key on the unchanged path (1.0 calls and 6.0 reuses per
family on every window, zero slot hits, one slot realization per family), the
remaining per-stage work identified and left per stage (target closures, bin
sealing, pipelines, associations), every incompatible mutation exercised
refreshing its state (shader reload, TSR scale, rejection, restart) with zero
failures, no validation errors and image continuity, lease and use counts
unchanged in mechanism (one transfer per family, no duplicates), failures
counted separately from hits. Not exercised: a window resize and an MSAA
sample-count change (no MCP driver on this fixture; reasoned through the
family key), teardown with work in flight beyond the transactional restart,
and physical XR hardware (emulated two-pass stereo stands in). The recording
thread saving is about a quarter to a third of the family's preparation time,
not six sevenths, because the retained call is the realizing one.
