# Vulkan Optimization Workstreams 03-05 Validation TODO

Last Updated: 2026-08-03
Owner: Rendering / Vulkan / Performance Validation
Status: Blocked On Final Phase 0 Allocation Closure And The 03-05 Matrix
Sequence: Consolidated completion and validation gate after workstream 05 and
before workstream 06
Predecessor: Workstream 02 is complete; see the
[Vulkan framerate root-cause investigation](../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md)
Blocks: [06 - Forward+ Prepass And Render-Graph Cost](../../todo/rendering/optimization/06-forward-prepass-and-render-graph-cost-todo.md)

## Purpose

This is the single remaining execution checklist for numbered optimization
workstreams 03, 04, and 05. It replaces their three separate TODO documents
and supersedes the earlier decision to defer their acceptance until the final
01-08 closeout. Workstream 06 must not begin until every gate in this document
passes and this document is marked `Complete`.

No unchecked task or acceptance criterion was waived during consolidation.
Completed implementation detail was reduced to the invariants and evidence
index below; remaining implementation, validation, and promotion work is
retained here.

## Consolidation Assessment

The three source TODOs were not all validation-only:

| Workstream | Implementation state at consolidation | Remaining work |
| --- | --- | --- |
| 03 - true zero-readback submission | Complete for the bounded Vulkan material-table rung | Scaling, mutation, overflow, parity, primary-reuse, allocation, desktop/RVC, crossover, Vulkan-validation, and GPU-trace acceptance |
| 04 - next-frame preparation and collect-visible handoff | Producer-complete binding handoff and legacy cutover are materially implemented for the representative path | Remove the final `3360` steady-frame mesh-draw preparation bytes, then validate publication scaling, mutation, parity, lifetime, latency, and overlap |
| 05 - command-recording worker architecture | Complete for persistent worker mechanics | Prove real overlap and benefit, zero allocation, deterministic parity, fallback truth, and lifecycle safety |

The checklist remains named a validation TODO because it is the pre-06
acceptance gate. A broker worker characterized the `3360` bytes as a
validation-only optimization residual, but that recommendation does not
override this repository's hot-path allocation rule or the Phase 0 zero-allocation
exit criterion. The gate remains open for allocation closure and the required
validation before workstream 06.

## Evidence And Supporting Trackers

- [Vulkan Optimization 03-05 Validation Investigation](../../investigations/rendering/archive/vulkan-optimization-03-05-validation-2026-08-02.md)
  records the first live fail-fast pass, exact steady-frame counters, the
  verified broker analysis, and the ordered workstream-04 implementation
  slices that must close before the canonical matrix.
- [Vulkan Framerate Root-Cause Investigation](../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md)
  owns the workstream-03 implementation and acceptance evidence.
- [Next-Frame Preparation And Collect-Visible Handoff Progress](../../progress/rendering/next-frame-preparation-and-collect-visible-handoff-2026-07-29.md)
  records the workstream-04 package foundation.
- [Vulkan Editor Steady-Frame CPU Cost Investigation](../../investigations/rendering/archive/vulkan-editor-frame-time-spikes-2026-07-30.md)
  records why workstream 04 was reopened.
- [Vulkan Command Recording Architecture Optimization](../../todo/rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md)
  owns the detailed binding schema, prepared-frame, command-artifact, and
  worker implementation.
- [Vulkan Command Recording Worker Architecture Progress](../../progress/rendering/vulkan-command-recording-worker-architecture-2026-07-30.md)
  records the workstream-05 implementation.
- [Compact Zero-Readback Rendering](../../todo/rendering/optimization/compact-zero-readback-rendering-todo.md)
  owns compaction, overflow, barrier batching, indirect-count, and delayed
  diagnostics. Its Hi-Z phase remains deferred to workstream 07.
- [Material Table And Texture Binding Ladder](../../todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md)
  owns the selected binding rung and material-table representation.
- [01-08 Acceptance Closeout](01-08-optimization-acceptance-closeout.md)
  consumes the accepted 03-05 baseline later; it no longer owns or defers the
  work listed here.

## 2026-08-02 Live Fail-Fast Execution

The first isolated Vulkan execution deliberately stopped before Phase 1. It
proved that paying for the canonical matrix now would be invalid:

| Workstream | Narrow live result | Gate effect |
| --- | --- | --- |
| 03 | The static CPU-direct scene primary cleanly reused (`1` reuse, `0` records; all `121` chains reused), with bounded package age and zero live Vulkan validation errors. GPU-driven rendering was disabled. | Static reuse smoke only; zero-readback acceptance remains unproven. |
| 04 | The fast material payload path was active, but an unchanged frame still captured `120` binding snapshots/`4412` entries and allocated `1664` bytes in binding preparation, `576` bytes in material binding, and `1088` bytes in snapshot copy. | Phase 0 fails at the producer-complete binding handoff. |
| 05 | The stable frame reused every chain, so no worker was correctly queued or evaluated. Historical counters show workers ran during startup, not that dirty work overlapped. | Dirty-worker concurrency and benefit remain unproven. |

`FrameOpPreparation` also allocated `28064` bytes per steady frame. Keep it as
the immediately following bounded correction after the binding ownership
slice; neither defect is waived. Exact evidence, artifact paths, broker run
identity, and post-change counters are in the linked investigation.

## 2026-08-02 Implementation And Validation Continuation

The producer-artifact slice is now materially implemented, but its Phase 0
exit criteria are not yet met. On the warmed `validation-03-05-final` Vulkan
session, an unchanged frame reported `119` persistent artifact reuses and only
`5` explicit callback fallbacks, down from `120` snapshots on the original
baseline. The remaining fallbacks are named and conservative:

- directional deferred lighting;
- deferred light combine;
- dynamic procedural skybox;
- bloom source copy; and
- final presentation source copy.

Generation-owned publishers now cover pipeline variables, GTAO and its blur
passes, bloom downsample/upsample passes, FXAA, post-process settings, and the
final numeric post-process pass. The forward-lighting ownership catalog now
also includes its complete packed and array shadow state, so those values are
retained and participate in the persistent lighting signature instead of
appearing as unowned uniforms.

This reduced stable snapshot work to `5` captures and `267` entries and
eliminated stable material-binding allocation (`576` to `0` bytes). It did not
close the gate: representative stable allocation remained `1920` bytes in
mesh-draw binding preparation, `768` bytes in snapshot copy, and `28064` bytes
in frame-op preparation. Twelve reflected auto-uniform schema mismatches also
remain on the conservative path. The descriptor-aliasing callbacks must not be
declared reusable until a generation-owned descriptor-resource publication
contract exists.

The same continuation found and fixed a dynamic-UI command-buffer ownership
defect during async pipeline warmup. A deferred secondary previously returned
after marking its overlay primary as recording; the next frame rejected reset
and forced swapchain recovery. Tracking now begins only when native recording
will begin and is abandoned on every exceptional exit. Fresh session
`validation-03-05-lifecycle` reached frame `1275` with clean primary reuse,
`119` artifact reuses, zero Vulkan messages, and no matching render exception,
recording-state error, recovery, or VUID in its post-stop logs.

Two camera-separated Vulkan readbacks were visually inspected from the final
artifact build. They show distinct live views with non-black Sponza geometry
and editor overlays. This closes the narrow camera-separated smoke deficiency,
not the canonical parity/stress matrix. Workstream 06 remains blocked.

## Final Implementation Continuation And Shadow Closeout

The remaining five descriptor/publisher callbacks were migrated to explicit
generation-owned publication, the legacy qualifying path was removed, frame-op
planning storage was bounded, primary command planning was extended across the
renderer/OpenXR logical slots, and auto-uniform frequency metadata was made
explicit for the directional deferred-light path.

On the warmed `validation-03-05-broker-closeout` Vulkan session, the current
representative frame reported:

- `129` persistent program-binding artifact reuses, zero builds, and zero
  fallbacks;
- zero binding snapshots, snapshot entries, legacy binding snapshots, and
  snapshot-copy bytes;
- `204` auto-uniform fast-path draws, zero legacy fallback draws, zero reflected
  member scans/lookups, and zero block/frequency/parity schema mismatches;
- zero frame-op preparation, frame-data refresh, primary recording, secondary
  recording, submission, and command-cache allocation; and
- zero Vulkan validation messages/errors.

The warmed frame still attributed `3360` bytes to `MeshDrawPreparation`:
`1624` in resource preparation, `1520` in binding preparation (`128` publisher
state, `1264` artifact eligibility, and `128` artifact lookup), and `216`
outside those nested scopes. This remains a Phase 0 implementation defect under
the repository rule that warmed per-frame hot-path allocations are bugs. The
broker's narrower lifetime-correctness classification is retained as evidence,
not accepted as a waiver of the zero-allocation gate.

The closeout also found and fixed directional-shadow flicker/displacement in
the grouped `InstancedLayered` path. Mutable cascade/cubemap matrices had been
included in pass-frequency owner identity, moving the dynamic UBO reservation
while reusable secondary command buffers retained the previous offset. Owner
identity now contains only stable shadow-pass identity; mutable matrices remain
in pass content generation. A camera round trip that previously changed about
`94.6%` of pixels now returns within `190 / 2,073,600` pixels (`0.0092%`), with
the difference confined to transient editor overlays. The audit confirms four
grouped cascades and no sequential fallback. The linked investigation contains
the exact captures, logs, RenderDoc evidence, and source-level explanation.

The repaired broker was republished as an immutable deployment and passed its
MCP smoke test. After the requested Codex restart, the bounded broker review
`c21386c827144a9cbed4146ab178e8d2` completed with
`requested_model == actual_model == gpt-5.6-luna` and one read-only
`get_render_profiler_stats` call. It classified the `3360` bytes as
validation-only residue for lifetime correctness and confirmed the named shadow
owner-identity change is structurally correct. The coordinator's bounded source
audit found no other mutable shadow content in an owner-identity path, but the
`3360` bytes still fail the repository's Phase 0 hot-path allocation rule. The
workstream-06 gate therefore remains blocked pending allocation closure and a
rerun of the existing matrix.

## Implemented Baseline To Preserve

### Workstream 03

- The default Vulkan GPU-driven path uses bounded GPU-owned active-work
  compaction, three fixed atlas-tier pass groups, indirect-count draws, and
  clamped overflow diagnostics.
- Current-frame draw selection performs no active/count mapping, count
  readback, fence wait, CPU enumeration of GPU-produced work, or production
  full-capacity bucket scan.
- Required variants use the compact path or fail with a visible, counted
  unsupported result; they never silently fall back to a CPU/full scan.
- Runtime capability probing reports the selected material/texture binding
  rung and reason.
- Retained implementation and diagnostic evidence is rooted at
  `Build/_AgentValidation/20260728-vulkan-framerate-root-cause/` and
  `Build/_AgentValidation/20260728-workstream03-acceptance/`.

### Workstream 04

- The bounded handoff already publishes pass ordering, stable selection
  identity, revisions, freshness, and live command/mesh/material references.
- Package identity, bounded frame ownership, late/stale policy, and
  producer/consumer timing telemetry are implemented.
- The acceptance audit found that packed uniform data, frequency-owned payload
  handles, compiled copy plans, resolved descriptor identities, stable offsets,
  and dirty ranges were not fully carried by that package.
- The follow-on architecture tracker has since implemented linked binding
  schemas, frequency ownership, prepared mesh state, typed primary plans,
  recorded artifacts, worker arenas, and command-buffer-local image journals.
  Its remaining Phase-1 cutover and producer-boundary items are Phase 0 below.

### Workstream 05

- Persistent renderer-owned workers replace per-frame generic task dispatch.
- Workers use per-worker/per-frame-slot command pools and immutable prepared
  state, with deterministic merge and execution order.
- Conflict, timeout, exception, resize, device-loss, cancellation, and
  shutdown paths use explicit fallback or frame failure; partial submission is
  prohibited.
- Telemetry distinguishes scheduled, queued, active, completed, serial,
  reused, conflicting, failed, and timed-out work and records queue, overlap,
  record, merge, and render-thread wait time.

## Acceptance Evidence Contract

- Freeze the implementation revision, dependency/runtime manifests, canonical
  settings, hardware identity, resolution, scene, stereo mode, and power plan
  before performance capture.
- Use `ReleaseBenchmark`, warm caches, the workstream-01 stability gate, and
  three 60-second repetitions for every canonical `Gate` comparison.
- A run-to-run range greater than 7.5% invalidates the comparison. Repeat it
  after removing the environmental cause.
- The accepted regression threshold is 5%. For low-count workstream-03
  overhead, the allowed p95 delta is the greater of 0.25 ms or 5% of the
  matched CPU-direct value.
- Report each submission CPU metric independently; do not sum overlapping
  percentile values into a synthetic total.
- Diagnostic delayed-readback, validation-layer, and RenderDoc runs are
  correctness evidence and are excluded from performance totals.
- An unavailable required runtime, eye/view, image comparison, hardware
  measurement, or GPU capture blocks the corresponding gate. Mark a mode
  `NotApplicable` only from an explicit capability result.
- A targeted failure that reveals an implementation defect is fixed at its
  source and revalidated. Do not lower thresholds, reclassify eligible work,
  or hide a fallback to promote the result.
- Record exact report, capture, test, and log paths in the linked durable
  investigation or progress document.

## Phase 0 - Close Workstream 04 Implementation

- [ ] Select and implement persistent-mapped or equivalently bounded payload
  storage for each frequency domain from representative hardware evidence;
  document the chosen dynamic UBO, SSBO/table, push-constant,
  descriptor-buffer, or capability-specific layouts.
- [ ] Build pure selection and binding inputs on the workstream-04 producer
  side. Materialize only thread-affine Vulkan handles on their legal owner
  before worker dispatch.
- [ ] Make the immutable upcoming-frame package carry complete resource-plan,
  descriptor, uniform, payload-handle, stable-offset, dirty-range, dependency,
  and lifetime inputs required by steady-state Vulkan consumption.
- [ ] Make stable package consumption perform zero live-material traversal,
  program-dictionary emission, binding-snapshot copy, reflected auto-uniform
  template construction/full-block copy/member scan, and full visible-draw
  descriptor refresh.
- [ ] Make payload and descriptor publication dirty-owner/range-driven with a
  bounded generation-only stable path; do not move the scan or allocation to
  package production, merge, or submission.
- [ ] Compare the new and legacy paths for uniform bytes, descriptor resource
  identity, offsets, dynamic state, fallback decisions, draw order, output,
  lifetime, and synchronization.
- [ ] Remove the legacy path from qualifying draws after parity passes. Keep
  unsupported shaders/callbacks on an explicit, counted conservative path and
  make canonical acceptance fail if it is used silently.
- [ ] Update documentation and environment-variable references to match the
  accepted path.

Phase 0 exit criteria:

- [ ] Workstream-04 payload publication is zero-allocation after warmup and
  scales with dirty owners/ranges rather than visible draw count.
- [ ] Storage and descriptor growth stay bounded across frame slots, unique
  owners/layouts, resize, scene churn, shader reload, and shutdown.
- [ ] A material texture change updates only affected material resource records
  and dependent generations.
- [ ] An unchanged frame performs zero material/object serialization,
  descriptor writes, per-draw descriptor validation, and reusable-draw visits
  for frame-data refresh.
- [ ] The package is complete before publication, cannot be overwritten while
  in flight, and is the only steady-state data source consumed by Vulkan.
- [ ] The architecture tracker has no remaining implementation or cutover item
  required by workstreams 04 or 05; its remaining unchecked items are mapped
  into the validation phases below.

## Phase 1 - Freeze And Run Targeted Validation

- [ ] Freeze the exact revision and write the validation manifest.
- [ ] Build the Vulkan runtime and Release editor with zero compiler errors or
  new warnings.
- [ ] Run the focused zero-readback, material-scatter, overflow, binding-schema,
  frequency-isolation, dirty-publication, prepared-frame, command-plan,
  command-artifact, worker, primary-reuse, image-journal, and lifecycle tests.
- [ ] Resolve any stale or unrelated test failure that prevents the focused
  selection from executing, or record it as an explicit external blocker.
- [x] Run a narrow Vulkan smoke before paying for the canonical matrix; require
  live camera-separated output, zero validation messages, no forbidden
  fallback, and internally consistent CPU-stage accounting.

## Workstream 03 Validation

- [ ] Reproduce the retained 93.98% primary-reuse cohort with both
  `XRE_VULKAN_FRAME_DATA_REUSE_DIAG=1` and
  `XRE_VULKAN_RECORDING_DIAG=1`; fix the first frame-data mask-66 transition at
  its source and resolve the two CPU-stage reconciliation discrepancies.
- [ ] Repeat the narrow cohort until every stable window reports at least 99%
  eligible primary reuse, zero workstream-03-owned allocation, and zero
  current-frame readback, mapping, full scan, and forbidden fallback.
- [ ] Retain runtime/counter/output proof for material mutation, streamed
  texture publication, overrides, depth/normal, shadow, supported transparent
  content, and explicit rejection of unsupported variants.
- [ ] Retain empty, exact-capacity, overflow, delayed-diagnostic, and optional
  visibility-input bypass evidence. Overflow must clamp every count, preserve
  declared conservative work or reject explicitly, increment its delayed
  diagnostic, leave guard regions intact, and emit no out-of-bounds indirect
  work or validation error.
- [ ] Run the selected 1x/4x/16x `Gate` matrix with three repetitions per
  cohort, first holding active work fixed while capacity changes and then
  holding capacity fixed while active work changes.
- [ ] Prove capacity growth does not change CPU pass groups, reusable frame-op
  count, current-frame mappings, or full-scan count and keeps each submission
  CPU p95 within the low-count allowance.
- [ ] Prove active GPU input/survivor/executed counts track the intended active
  increase while CPU submission remains bounded by fixed pass-group/tier
  topology.
- [ ] Run the matched high-count `Gate` crossover for zero-readback,
  CPU-direct, and diagnostic FullBucketScan with at least 4,096 backend
  commands. Zero-readback `render_dispatch_ms` p95 must remain at least 5%
  lower than both references and each strategy's p95 range must be at most
  7.5%.
- [ ] Run desktop Deferred/Uber static/moving plus RVC Deferred/Uber with
  foveation Off and every supported enabled mode. Run matched CPU-direct
  primary-reuse cohorts from identical manifests.
- [ ] Prove every desktop sample contains a fresh desktop render and every
  submitted XR projection frame contains two fresh eye renders recurring at
  the runtime-owned cadence; one-eye or reused output cannot masquerade as a
  render.
- [ ] Prove object/material/selection identity and finite-depth coverage are
  bit-exact. In deterministic non-temporal regions, require linear-color RMSE
  at most 0.5/255 and no channel difference above 2/255; retain seeded
  omitted-draw and material-row-swap negative controls.
- [ ] Prove opaque, masked, override, depth/normal, shadow, and supported
  transparent draw/material counters match CPU direct and that material or
  texture changes appear at the declared safe frame boundary without stale
  rows/descriptors or data-only primary invalidation.
- [ ] Prove delayed diagnostics and optional-visibility bypass do not change
  current-frame mappings, waits, dispatch topology, draw counts, or output.
- [ ] In every canonical desktop cohort, prove each zero-readback submission
  CPU p95 is no more than the matched CPU-direct p95 plus the low-count
  allowance.
- [ ] Prove the workstream-03 portions of the compact-zero-readback child:
  active-list/pass-group scaling, bounded non-per-survivor-atomic compaction,
  empty/exact/overflow safety, barrier batching, indirect-count submission,
  and non-critical-path delayed diagnostics. Its Hi-Z phase remains owned by
  workstream 07.
- [ ] Prove every stable sample has zero GPU readback bytes, mappings, waits,
  full scans, forbidden fallbacks, and workstream-03-owned managed allocation.
- [ ] Inspect a GPU capture showing bounded compaction, specific
  compute-to-indirect synchronization, clamped indirect counts, indirect-count
  consumption, and no hidden per-material CPU submission.
- [ ] Run StandardValidation and repeat focused RenderDoc inspection after any
  change to recording, descriptor publication, or synchronization.

Workstream 03 exit criteria:

- [ ] The comparator and evaluator accept every local scaling, correctness,
  relative-cost, allocation, reuse, desktop, and RVC gate.
- [ ] Record the 5.00 ms desktop and 8.33 ms RVC whole-frame results as
  workstream-08 handoffs; missing either absolute target alone does not fail
  workstream 03 when every local gate passes.
- [ ] Preserve the generic frame-data-refresh and command-encoding allocation
  attribution as workstream-04/05 gates; an allocation traced to
  workstream-03 code remains a workstream-03 blocker.

## Workstream 04 Completion And Validation

- [ ] Capture canonical stable allocation and timing evidence for package
  production, publication, validation, consumption, binding-data publication,
  descriptor publication, and remaining backend frame-data refresh.
- [ ] Stress static and moving scenes, camera-only and object-only motion,
  material and streaming publication, viewport resize, shader hot reload,
  pause/resume, late or missing packages, failed submit, scene churn,
  device loss, shutdown, and repeated start/stop.
- [ ] Capture representative render targets and viewport images for the static,
  moving, camera-only, material-mutation, resize, and shader-reload cohorts.
- [ ] Prove deterministic draw ordering and visual/data equivalence against the
  authoritative path.
- [ ] Measure package production, publication, wait, freshness validation,
  consumption, generation age, and input latency separately against the
  predecessor handoff.
- [ ] Preserve the `BlockUntilFresh` default and explicitly count every
  authorized previous-visibility reuse, dropped stale package, package wait,
  and policy fallback.
- [ ] Prove useful preparation overlaps rendering and distinguish Mode A
  collect-waiting-on-render backpressure from Mode B render-starved-by-collect;
  do not claim benefit from merely moving the producer farther ahead.
- [ ] Prove generation age stays within the bounded policy with zero unreported
  stale reuse, producer overwrite, mutable ownership race, or in-flight
  lifetime violation.
- [ ] Prove render-thread non-encoding preparation meets its workstream-01
  budget and the representative approximately-647-draw Release stable-static
  binding/publication/descriptor sub-budgets in the architecture tracker.
- [ ] Prove the canonical CPU-direct desktop render path remains at or below
  the 5.00 ms workstream-01 product gate, or retain an evidence-backed blocker
  without relaxing the frequency/scaling invariants.
- [ ] Prove the declared moving-object cohort updates only dirty object ranges
  and meets its publication CPU/byte budget without touching unrelated
  domains.
- [ ] Close the retained 40,384-byte frame-data-refresh allocation handoff with
  zero steady-state managed allocation in package and publication hot paths.
- [ ] Prove core, synchronization, and best-practices validation are clean
  through resource replacement and retirement.

Workstream 04 exit criteria:

- [ ] A documented immutable backend-ready frame package is produced alongside
  collect-visible and consumed by Vulkan.
- [ ] The render thread primarily performs bounded validation, encoding,
  submission, and presentation.
- [ ] All frequency, dirty-owner, descriptor-topology, allocation, scaling,
  latency, overlap, parity, lifetime, and stress gates pass.

## Workstream 05 Validation

- [ ] Reuse predecessor characterization cases for structural/frame-data
  signatures, descriptor/resource/pipeline generations, primary-owned
  secondaries, inheritance, and volatile overlays.
- [ ] Capture serial and persistent-worker baselines with identical immutable
  prepared inputs on small, medium, large, and stable dirty-chain cohorts.
- [ ] Prove two or more workers actually encode concurrently; disabled,
  rejected, conflicted, and serial-fallback work must never appear as parallel
  work.
- [ ] Tune or confirm the two-independent-chain dispatch floor on target
  desktop and RVC hardware.
- [ ] Compare p50/p95/p99 worker queue, record, active span, overlap, merge,
  render-thread wait, and total render time against workstream-01 thresholds.
- [ ] Confirm stable cohorts reuse command buffers instead of invoking workers
  and small workloads remain within declared regression variance.
- [ ] Prove zero steady-state managed allocation in serial primary and worker
  secondary recording without moving allocation to preparation, merge, or
  submission.
- [ ] Validate CPU-direct recording and the explicit primary-command quarantine
  for mutable zero-readback indirect/count streams.
- [ ] Verify exact render-graph/pass, transparent, draw, primary-reuse, and
  secondary-reuse order plus visual parity.
- [ ] Run core and synchronization validation for every enabled worker family,
  dynamic-rendering and legacy inheritance matrices, resize, shader reload,
  scene churn, device loss, shutdown, and repeated start/stop stress.
- [ ] Stress primary/secondary pending state, deferred retirement, command-pool
  ownership, exception, timeout, and quarantine; partial submission and reset,
  free, or reuse of an in-flight command buffer must remain impossible.
- [ ] Prove each enabled worker family has a measured benefit on representative
  hardware or retain it behind an explicit, truthful serial disposition.

Workstream 05 exit criteria:

- [ ] Large dirty workloads show overlapping worker intervals and meet the
  accepted improvement threshold.
- [ ] Small and stable workloads do not regress beyond declared variance.
- [ ] No global planner lock, hot-path allocation, ownership hazard, silent
  fallback, partial submission, or Vulkan validation regression remains.

## Combined Exit Gate

- [ ] Every Phase 0 implementation item is complete and the implementation
  revision used for acceptance is frozen.
- [ ] Every workstream-local checkbox above maps to exact retained evidence.
- [ ] Focused tests and Release builds pass without new warnings.
- [ ] Standard Vulkan validation and required RenderDoc inspections pass.
- [ ] Desktop and required RVC `Gate` matrices pass their repetition,
  stability, variance, parity, allocation, reuse, and fallback checks.
- [ ] Cross-workstream handoffs are reconciled: workstream 03 owns compact
  submission, workstream 04 owns preparation/data publication, workstream 05
  owns command encoding, workstream 07 owns Hi-Z, and workstream 08 owns final
  absolute whole-frame promotion.
- [ ] The linked investigation/progress documents, optimization roadmap,
  01-08 closeout, and workstream 06 status contain the accepted evidence and
  final disposition.
- [ ] This document is marked `Complete` and workstream 06 is changed from
  `Blocked` to `Ready For Implementation`.
