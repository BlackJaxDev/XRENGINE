# Vulkan Stall Remediation TODO

Last Updated: 2026-09-18
Owner: Rendering, with Profiler, Runtime Core, and ImGui Editor owners per item
Status: S00/S00a/S01/S02 Validated; S03 Active with implementation complete and validation partial
Execution: One fix at a time, with a mandatory validation gate after each fix

## Purpose And Ownership

Address the priorities in the [September 17 stall research](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#L121)
without turning source-level candidates into assumed root causes. This is the
execution checklist for that investigation, not authorization to implement every
possible optimization regardless of measurements.

The [Vulkan frame-loop master](vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
continues to own architectural contracts and integrated promotion. Reuse its
existing machinery and cross-reference completed work instead of creating a
second pipeline queue, cache, telemetry system, or publication model. Related
contracts remain in the [hardening ledger](vulkan-core-hardening-and-device-loss-todo.md)
and [resource-lifecycle checklist](render-pipeline-resource-lifecycle-todo.md).

This document schedules future work only. Creating it does not authorize test
changes, dependency upgrades, storage migrations, or changes to launch flows.
Keep durable results in the linked investigation and concise gate status here.

## Evidence That Must Not Be Lost

- The original report describes repeated 153-165 ms CPU recording and TSR
  ghosting. Its original logs are unavailable locally; causal attribution is open.
- The resumed probe instead has warmed sampled recording of 27.1-42.6 ms, GPU
  time of 17.81-24.85 ms, and output of 15-20 Hz. These are sparse overlay samples,
  not all-frame percentiles or a demonstrated reproduction of the original issue.
- Seven 500-ms watchdog detections include visibility preparation, UI, upload and
  Vulkan recording scopes. An active leaf observation does not explain an entire
  no-completed-render interval.
- `GetHottestPath` pairs a descendant path with its root duration. Neither the
  reported 1,285.837 ms mesh endpoint nor 92.077 ms update endpoint measures the
  named method's exclusive cost.
- The resumed probe presented `TsrOutputTexture`. That proves participation in
  the presentation path, not correct history identity or absence of ghosting.
- The historical repaired Release matrix failed motion p99 observer overhead.
  A superseding current-HEAD matrix at revision `a1f9408e5` completed three
  alternating profiler-off/on pairs with the same workload identity, verified
  camera motion, admitted images, 99.625-99.643% motion GPU coverage, zero
  diagnostic loss, and passing retention/backlog gates. Paired motion p99 deltas
  were -1.00%, +3.94%, and +6.43%, below the 13.67% disabled-run spread.
  S00 is Validated; this does not make the measured motion smooth.
- The current matrix confirms a separate periodic motion hitch with either
  profiler state: frames above 55 ms recur every 0.282-0.301 seconds median,
  usually four render frames apart with adjacent-frame bursts. Render time
  correlates 0.98-1.00 with CPU Vulkan recording and only 0.03-0.10 with GPU
  time. Slow frames add about 28.5 ms in primary recording, dominated by the
  primary operation loop, prewarm, packet lowering, and `vkEndCommandBuffer`.
  This is entry evidence for S02, not observer overhead.
- S02 localized recurring warmed allocation to generated value equality in the
  Advanced visibility raster lane. Explicit handle/scalar identity checks removed
  245,232 bytes from the median Advanced visibility operation (337,176 -> 58,376)
  while preserving the warmed 393-draw workload. A matched profile-capture A/B
  reduced primary-recording median from 16.601 to 7.097 ms and p95 from 58.656
  to 36.934 ms. Rare whole-frame tails and the 18-25 ms GPU workload remain.
- Controlled TSR motion produced ready history and finite nonzero velocity, but
  the viewed path was not discriminating enough to resolve the reported ghosting.
  Frame-authoritative profiler telemetry now reports the active TSR mode instead
  of a launch-cached FXAA value; that telemetry fix is not a visual TSR fix.
- Existing native caches, background compilation, resident-table reuse, family
  leases and transactional resource generations must be audited and reused.
  Unchecked historical backlog entries do not prove those mechanisms are absent.

## Mandatory One-By-One Protocol

**Only one implementation item may be Active. Do not begin the next fix until
the current fix has passed its focused build, live behavior, performance and
applicable regression checks, with evidence recorded.** A section containing
multiple independent changes is not an exemption: split them into child items
and validate each child before starting the next.

Read-only evidence gathering may run independently. Overlapping edits, runtime
mutations and A/B measurements must remain serialized. Do not combine readiness,
invalidation, resource publication and UI changes in one unvalidated patch.

For every item below, complete this gate:

- [ ] Record the entry evidence, exact owning path, one falsifiable hypothesis,
  affected workloads, smallest coherent change, and a check that could reject it.
- [ ] Define acceptance before editing: correctness invariants, target metric,
  numeric budget/tolerance, sample count, observation window and observer overhead.
  Use measured baseline variability and existing master contracts; do not choose
  a convenient threshold after seeing the result.
- [ ] Record ownership/thread affinity, source/configuration generations,
  cancellation semantics, lock order, publication and retirement dependencies.
  Require an explicit design review before changing concurrent/native lifetimes.
- [ ] Implement only this item. Identify this exact source diff and the binaries
  used for validation; a build from before the edit is not evidence for the fix.
- [ ] Immediately run the narrowest useful check. Run the owning project build
  with no new warnings, then the relevant isolated editor path. A successful
  build, quiet log or `git diff --check` alone cannot close a runtime item.
- [ ] Exercise the original trigger plus the item's cold/warm, pending/failure,
  mutation and lifetime cases below. Actually view saved images when rendering
  or UI behavior is involved; tool success is not image validation.
- [ ] Compare baseline and changed captures under matching conditions. Confirm
  the targeted mechanism changed, performance met the predeclared budget and
  neighboring stages did not absorb the removed cost or accumulate backlog.
- [ ] Check affected regressions, warnings, allocation/retention growth and
  teardown separately from steady-state behavior. Explain every new diagnostic.
- [ ] Record pass/fail, evidence, remaining risks and user feedback. Only then
  mark the item Validated and select the next item.

### Failure And Deferral Rules

- On failure, stop progression. Repair the same item and rerun its checks. If the
  hypothesis was falsified, record that result and reassess the nearest owner.
  Do not build another fix on top of a known failing change.
- A flaky or ambiguous result is not a pass. Repeat with a discriminating capture;
  if evidence remains insufficient, mark Blocked and report the blocker.
- If a candidate's entry condition is disproved, mark Deferred/Not Applicable
  with evidence and the condition for reopening it. Do not mark it Fixed.
  Advancing past an unvalidated behavioral change requires an explicit scope
  decision; do not silently waive a failed gate.
- Any rollback must be scoped to this item's own edits. Preserve unrelated user
  work. Do not reset the worktree or switch branches to obtain an A/B baseline.
- A change to instrumentation or measurement settings requires a comparable
  baseline before accepting performance results from subsequent items.
- A reordering requires a written evidence-based reason and satisfied dependency
  gates. The measured steady-state owner may take priority over cold-only work;
  this must not become simultaneous implementation.

### Status And Test Clearance

Use Pending, Active, Blocked, Validated, Closed, or Deferred/Not Applicable. Checked
implementation boxes are not closure. Validated means the item's live gates
passed; Closed additionally means its required follow-ups and applicable test
work/user confirmation are resolved. Record test clearance separately.

Do not add or modify regression tests until live feature validation succeeds and
the user explicitly clears test work, as required by repository policy. Existing
tests may support diagnosis or verify a sound change when applicable, but must
not replace live validation. A clearance request or this TODO is not clearance.
Pending test approval remains visible and does not permit claiming full closure.
Once cleared, add only focused deterministic coverage in the existing test project
and rerun the corresponding focused checks before closing the item.

## Execution Ledger

All items start Pending. Conditional items require an explicit disposition if
their entry condition is not met. Every row, including a split child, has its own
gate record. No item is complete merely because this checklist was written.

| ID | Item | Entry dependency | Initial status |
| --- | --- | --- | --- |
| S00 | [Comparable baseline and evidence manifest](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s00-gate-record-comparable-baseline-and-evidence-manifest) | None | Validated (current-HEAD three-pair matrix passes observer, coverage, loss, retention, and backlog gates) |
| S00a | Export existing coarse GPU timing provenance | S00 instrumentation prerequisite | Validated (GPU identity and symmetric clean profiler toggle proven live) |
| S01 | [Correct profiler duration/identity reporting](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s01-gate-record-correct-profiler-duration-and-identity-reporting) | S00 | Validated (normal, linked async/parallel, and lifecycle identity proven) |
| S02 | Attribute warmed recording and waits | S01 | Validated (boxed stable-bin identity comparisons identified and removed; matched live A/B passed) |
| S03 | Nonblocking Advanced pipeline readiness | S02, confirmed cold-path trigger | Active (implementation complete; timing and unavailable-capability gates remain) |
| S04 | Dependency-scoped compile invalidation | S03, lifetime design review | Pending |
| S05 | Cache publication and foreground native creation | S04, measured remaining cost | Pending |
| S06 | Bounded initial resource materialization | S02; default after S05 disposition | Pending |
| S07 | CPU mesh preparation and wrapper publication | S06, measured construction cost | Pending |
| S08 | Index preparation before draw admission | S07 disposition, measured join | Pending |
| S09 | Shared immutable helper geometry | S07-S08 dispositions, measured duplication | Pending |
| S10 | Toolbar icon preparation | S02; default after S09 disposition | Pending |
| S11 | Camera inspector metadata/discovery | S10 disposition, measured cost | Pending |
| S12 | Shared Advanced extraction/publication | S02; default after S11 disposition | Pending |
| S13 | Recurring recording/source/upload preparation | S02; default after S12 disposition | Pending |
| S14 | Actual Core update callbacks/registration | S02; default after S13 disposition | Pending |
| S15 | Temporal correctness and original-regression decision | Baseline plus each affected runtime gate | Pending |
| S16 | Integrated acceptance and closeout | All applicable prior gates | Pending |

## S00. Establish A Comparable Baseline

- [x] Preserve the original report separately from the resumed probe. Recover the
  original logs/configuration if available; otherwise label reproduction status
  unknown and never use a different scene to declare the original issue fixed.
- [x] Record source revision plus local diff, build configuration, SDK/runtime,
  device/driver, Vulkan validation settings, present mode, profiler/logging mode,
  backend/submission mode, scene content, camera path and viewport dimensions.
- [x] Define cold process, persisted-cache warm restart, warmed stationary and
  controlled-motion workloads. Record actual scene readiness, accepted content
  and cache state; elapsed time alone does not prove warm-up is complete.
- [x] Capture all-frame counts and CPU/GPU distributions, recording/resource
  preparation child timings, queue delay, successful presentations, missing GPU
  samples, stall thresholds and dropped/suppressed diagnostic events.
- [x] Use at least three matched runs per performance condition and a warmed
  window of at least 60 seconds by default. Record any justified alternative
  before comparison. Define stage/tail-latency and retained-memory budgets here.
- [x] Compare instrumented and minimal-observer configurations. Do not mix Debug,
  Release, profiler modes or validation-layer settings in one claimed speedup.

Gate: evidence is reproducible and budgets are recorded. If the 153-165 ms
regression cannot be reproduced, retain that unresolved result while addressing
independently confirmed mechanisms. Do not delete user caches to force cold runs.
Status: Validated. S00a's clean profiler toggle prerequisite and the superseding
current-HEAD Release matrix pass. Three matched 60-second off/on pairs retained
one workload identity, verified camera motion, exceeded 99% coarse-GPU coverage,
and passed exact diagnostic-loss, settled managed/private-memory, native-resource,
descriptor-set, and required-job/retire backlog gates. Paired motion p99 changes
of -1.00%, +3.94%, and +6.43% remain within the 13.67% disabled-run spread;
positive paired mean changes are at most +0.116 ms. Preserve the original CPU
regression, image corruption, TSR ghosting, and newly quantified periodic
recording hitch as unresolved. S01 may proceed; S02 remains blocked on S01 (see
[S00 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s00-gate-record-comparable-baseline-and-evidence-manifest)).

## S01. Correct Profiler Attribution

Anchor: [Engine.CodeProfiler.cs](../../../../XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.CodeProfiler.cs#L1551).

- [x] Define root-inclusive, selected-child-inclusive, synchronous self-time and
  active-scope elapsed fields with matching name, kind, frame/thread and timestamp.
- [x] Keep wall-clock self-time distinct from on-CPU execution. Treat overlapping
  cross-thread spans, incomplete roots and stale snapshots explicitly.
- [x] Update affected log/export/UI consumers so a new leaf duration cannot be
  misread as the old root field. Reuse the shared telemetry contract and document
  changed field meanings; avoid an unrelated profiler rewrite.
- [x] Validate a captured completed hierarchy with a larger parent and smaller
  child, sibling work, explicit waiting, and an active/incomplete scope. Recompute
  the expected fields from captured spans rather than trusting the formatted log.
- [x] Validate concurrent thread/frame identity and enabled/disabled profiling;
  confirm no new per-frame allocations or unacceptable observer overhead.

Gate: labels and values describe the same scope, self-time accounting is coherent,
and historical root-only values remain clearly identified. Do not add a new test
method or synthetic test suite before test clearance; use captured runtime spans.
Status: Validated. Explicit scope, parent,
producer-thread, logical-thread, session-epoch, publication and frame identity are
implemented and propagated through dumps, packets and UI. A 1,395-node live dump
had no duplicate IDs, missing parents, hierarchy mismatches, logical-thread
mismatches, invalid intervals, children outside parent intervals or incomplete
published nodes. A profiler off/on cycle advanced epochs 2 -> 3 -> 4 and surfaced
13 then 24 rejected stale completions without attaching them to the new session.
Focused `InvokeAsync` and `InvokeParallel` validation now proves linked listener
parentage, logical/producer thread identity, timestamp containment, completion,
publication deferral and synchronous self-time exclusion. These APIs remain
explicit opt-ins for independent, thread-safe listeners; no existing engine
event has a sufficiently broad thread-safety contract for automatic conversion.
See the
[S01 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s01-gate-record-correct-profiler-duration-and-identity-reporting).

## S02. Attribute The Actual Warmed Bottleneck

Anchors: [primary recording](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs)
and the [profiler guide](../../../developer-guides/diagnostics/profiler.md).

- [x] Capture a completed warmed long frame, with coarse recording children and
  operation kind/count/bytes. Add only the missing bounded instrumentation and
  validate that instrumentation as its own change before optimization.
- [x] Correlate lock/worker waits, native calls, allocation/GC pauses, JIT,
  scheduling, queue pressure and frame/output/generation identities.
- [x] Use managed wait/contention/GC evidence and, when needed, Windows ETW native
  stacks/context switches. EventPipe stack sampling alone is not on-CPU evidence.
  Check installed tool versions; operator-run elevation may be required for ETW.
- [x] Identify the critical-path owner rather than summing overlapping CPU/GPU
  intervals or assigning downstream `WaitForRender` time to useful update work.
- [x] For detailed captures, satisfy the existing hardening contract: account for
  at least 99% of the root interval and expose unattributed gaps of 50 us or more.
  If collection cannot support that, retain an explicit attribution blocker.
- [x] Select the next measured fix and record whether it affects startup, reload,
  warm recording, GPU work, or multiple regimes. Separate the warmed 18-25 ms GPU
  workload from CPU remedies; do not promise 60 Hz from a CPU-only change.

Gate: enough correctly attributed evidence exists to choose a bounded change.
If waiting/GC dominates, reject an unsupported algorithmic optimization and route
the next item to that owner with the same one-by-one protocol.
Status: Validated. Sparse and dense managed-allocation captures first separated
profile-row observer cost from render-thread work. Operation-family and raster
substage counters then identified deterministic 528-byte closure-validation and
96-byte stable-bin-lowering allocations for each of 393 records. Both came from
generated record-struct equality traversing nested Silk.NET Vulkan handle values;
explicit scalar/handle comparisons preserve the same identities without boxing.
The final detailed window had zero bytes in validation, lowering, binding and
CPU-direct draw substages, at least 99.942% frame attribution, and no unattributed
gap above 7 us. The matched labels-off/profiler-off CleanProfile capture improved
CPU recording and GC distributions without changing CpuDirect workload identity.
This closes attribution and the bounded warm-recording correction, not the
original unreproduced 153-165 ms report, rare whole-frame tails, TSR correctness,
or the independent GPU budget. S03 remains Pending because no recurring cold
pipeline-readiness trigger was observed. See the
[S02 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s02-gate-record-warmed-recording-allocation-attribution).

## S03. Make Advanced Readiness Nonblocking

Anchors: [Advanced capabilities](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.AdvancedPipelineCapabilities.cs#L407),
[visibility programs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.cs#L31),
and [program linking](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Linking.cs#L23).

- [ ] Measure source compilation, linking, native pipeline work and foreground
  joins separately during the first required Advanced family and after reload.
- [x] Inventory the complete required family: early/late/native compute, opaque/
  masked raster and supported view/mesh variants. Establish preparation ownership
  outside readiness polling, reusing existing queues and prewarm records.
- [x] Implement explicit Missing/Pending/Ready/Failed transitions. Polling must
  not synchronously link, compile or wait, and repeated polls must not duplicate
  requests or reset progress. Avoid changing only the async boolean while the
  enclosing synchronous-preparation scope still overrides it.
- [x] Publish only a complete compatible family. Preserve previous output only
  where its contract permits; otherwise report pending/loading. Never silently
  omit required draws or substitute a CPU/backend fallback.
- [ ] Validate cold miss, warm hit, delayed completion, unavailable capability,
  compile failure, shader reload, repeated polling and exact frame admission.
  A queued compile must eventually publish or report failure, not remain pending
  forever; shutdown must not strand preparation jobs.

Gate: readiness does no foreground compilation/join, outcomes remain correct,
target stalls improve within budget, and missing/failed work remains visible.
Split preparation and admission changes into child gates if independently staged.

Status: Active. The generation-owned family preparation task, explicit readiness
states, nonblocking shader artifact polling, complete-family publication, reload
identity handling, failed-revision recovery, superseded-task draining, and
idempotent shutdown are implemented. Cold, warm, delayed double-reload, injected
compile failure, same-process recovery, exact admission, repeated polling, and
shutdown passed in isolated Vulkan editor sessions. The remaining closure work is
to record separate source/link/native/foreground-join timings for cold and reload
paths and exercise a genuinely unavailable device capability. Target-specific
native graphics pipeline creation remains S05; global invalidation maintenance
and mutation-scope localization remain S04. The independent canonical texture
`SourceMismatch` still prevents a visual-quality pass and is not an S03 readiness
failure. See [S03 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s03-gate-record-nonblocking-advanced-readiness).

## S04. Localize Compile Invalidation Safely

Anchor: [VulkanPipelineCompileQueue.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs#L624).

- [ ] Measure mutation reason, affected owners, global invalidations, drained jobs,
  publication waits and stale completions. Document lock order and all dependency
  lifetimes before replacing the global mutation gate.
- [ ] Distinguish additive cold program creation from shader/interface replacement,
  layout destruction, renderer shutdown and device-wide invalidation.
- [ ] Introduce only the missing owner/dependency generation checks and immutable
  retention. Reuse existing leases and preserve necessary device-wide barriers.
- [ ] Reject stale results and release each result/dependency exactly once.
  Abandoning a managed task cannot cancel native compilation; retirement must
  wait for both compiler and GPU users, including cache-publication users.
- [ ] Validate an unrelated new program while other jobs are pending, replacement
  while an old job is queued/running/completed-unpublished, repeated reload,
  window/renderer teardown and the available isolated recovery path.
- [ ] Inspect drain counts, compile progress, stale-result disposal, deadlocks,
  live native handles and retirement backlog across repeated cycles. Do not induce
  a machine-wide GPU reset merely to exercise device loss.

Gate: unrelated additive work does not invalidate/drain unaffected owners, all
required dependencies remain retained, and memory/backlogs settle after use.
Unvalidated lifetime behavior blocks advancement even if frame time improves.

## S05. Bound Remaining Cache And Native-Creation Work

Anchor: [VulkanPipelineCache.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs).

- [ ] Measure foreground native creation, foreground/background cache locks,
  merge, capture and persistence separately. Retain existing isolated caches.
- [ ] If foreground misses matter, implement capability-gated cache-only probes
  with queued preparation and Pending results, never an immediate blocking retry.
  Check `pipelineCreationCacheControl`; even a cache hit is not a hard latency bound.
- [ ] Separately, if publication/capture contention matters, schedule or batch it
  without weakening merge, destruction, shutdown or persistence synchronization.
  Treat this as another child fix with its own validation, not the same A/B.
- [ ] Validate persisted/runtime hits, misses, unsupported capability, publication
  overlapping other jobs, missing/rejected cache data and orderly teardown using
  isolated inputs. Do not alter the cache storage format without approval.
- [ ] Verify needed entries survive restart, compile progress is not starved,
  cache memory stays bounded and foreground cost is not merely moved into another
  foreground lock. Keep the conservative worker count unless measured evidence
  justifies a separate concurrency change.

Gate: each measured source of blocking meets its budget without losing cache or
lifetime correctness. If absent from the trace, defer the candidate with evidence.

## S06. Budget Initial Resource Materialization

Anchor: [XRRenderPipelineInstance.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs#L1810).

- [ ] Identify initial versus replacement builds and per-spec/factory duration;
  confirm the initial `TimeSpan.MaxValue` / `int.MaxValue` bypass is on the trigger.
- [ ] Define bounded first-generation preparation or an explicit loading phase,
  with a responsive editor and no partially published resource generation.
- [ ] Preserve stale-key rejection, transactional commit, imported-resource
  ownership and active/pending/retired separation. Do not run installed build/view
  contexts or renderer-affine factories wholesale on worker threads.
- [ ] If one indivisible factory exceeds budget, split only that factory and
  validate it separately. Inter-spec checks cannot bound its internal work.
- [ ] Validate first creation, replacement, repeated resize/pipeline change during
  preparation, cancellation/supersession, failure and recovery. Check both first
  valid output and retirement after superseded builds.

Gate: slice and worst-spec cost meet the chosen budget, no partial generations
escape, and time-to-first-valid-frame is reported alongside frame pacing. Moving
work into loading is not evidence that total preparation cost decreased.

## S07. Separate Mesh CPU Data And Wrapper Publication

Anchors: [XRMesh.BufferInit.cs](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.BufferInit.cs)
and [GenericRenderObject.cs](../../../../XREngine.Runtime.Rendering/RenderObjects/GenericRenderObject.cs#L95).

- [ ] Separate allocation/zero fill, buffer callbacks, vertex population, wrapper
  creation and wrapper-lock waits, recording vertex counts and bytes.
- [ ] Reuse importer CPU-preparation/suppression patterns only where measurements
  justify them. Define the owning publication thread and an immutable completion
  boundary; suppression alone must not expose partially initialized objects.
- [ ] Preserve all constructor initialization, revision notifications and failure
  cleanup. Do not replace constructors with an apparent fast path that omits
  subscriptions or reintroduce nested `Parallel.For` starvation.
- [ ] Validate small helpers and a large imported mesh, material/buffer changes,
  revision during preparation, failure/disposal before publication and multiple
  consumers. For backend-neutral changes, verify both Vulkan and OpenGL.

Gate: correct geometry/bounds/attributes and revision behavior, no partial object
discovery, bounded owner-thread publication and no new allocation/retention leak.

## S08. Prepare Indices Before Draw Admission

Anchor: [XRMesh.Geometry.cs](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Geometry.cs#L358).

- [ ] Establish which cold/revised indexed meshes synchronously join preparation.
- [ ] Request work before admission using immutable topology and exact revision
  tickets. Preserve explicit pending/failure outcomes and completion ownership.
- [ ] Validate cold indexed geometry, unchanged reuse, topology mutation while
  pending, stale completion, disposal, nonindexed geometry and renderer switching
  where shared code changes. Confirm submitted draw ranges remain correct.

Gate: normal draw admission no longer joins index preparation, stale indices are
never used, and required geometry is not silently skipped. If no join matters,
record a conditional deferral instead of performing a speculative rewrite.

## S09. Share Helper Geometry Only When Justified

- [ ] Count duplicate fullscreen/debug/light-volume constructions and establish
  their actual cost after S07-S08. Audit existing reuse before adding more.
- [ ] If justified, define immutable shared geometry with per-consumer material/
  renderer state, explicit ownership and safe retirement. Apply one geometry
  category at a time and validate it before expanding reuse.
- [ ] Validate multiple windows/pipelines, owner teardown, custom shaders, debug
  primitives, relevant stereo views, and OpenGL/Vulkan behavior.
- [ ] Keep procedural fullscreen drawing as a separate deferred design unless
  measured need warrants it; it requires explicit topology/count and shader/view
  compatibility, not simply deleting a dummy mesh.

Gate: sharing reduces measured construction without mutable cross-consumer state,
premature disposal or new retained lifetime. Unchanged visual output is required.

## S10. Remove Cold Toolbar Work From Drawing

Anchor: [EditorImGuiUI.Icons.cs](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs#L41).

- [ ] Distinguish path lookup, SVG parse, Skia rasterization, texture construction
  and upload. Check whether the first-use delay explains the observed toolbar scope.
- [ ] Prepare pixels before use or on bounded workers, then publish/upload on the
  correct owner. Do not move mutable caches/counters or the whole current texture
  factory onto arbitrary workers. One icon per frame is not a time bound.
- [ ] Validate first toolbar display, all icons warmed, multiple sizes/windows,
  unavailable/malformed isolated icon inputs, shutdown during preparation and
  repeated requests. Preserve useful pending/failure behavior and bounded retries.
- [ ] Inspect actual toolbar images and interaction, pixel-buffer ownership,
  upload budgets and cached resource retirement on both affected backends.

Gate: no synchronous file/parse/raster work remains on the normal draw path,
icons eventually render correctly, and warm allocation/latency stays within budget.

## S11. Bound Inspector Discovery And Metadata Work

Anchor: [CameraComponentEditor.cs](../../../../XREngine.Editor/ComponentEditors/CameraComponentEditor.cs#L343).

- [ ] Measure first-use create/replace type discovery separately from warmed
  property drawing, enum arrays, attribute lookup and tooltips.
- [ ] First, defer or precompute measured cold discovery. Validate first panel
  open, create/replace menus, pending discovery, errors and script reload before
  changing warmed metadata handling.
- [ ] Then, if material, cache immutable setting descriptors/enum labels and avoid
  tooltip metadata work until hovered. Validate this as a separate child item.
- [ ] Key invalidation to script/assembly generations; do not indefinitely retain
  collectible assemblies. Preserve settings mutation notifications, undo behavior,
  labels, enum options, asset creation and failure diagnostics.
- [ ] Validate different camera/pipeline settings, repeated panel opening,
  changed script types, warmed no-hover drawing and tooltips. Inspect the UI and
  confirm allocations and retained metadata return to their expected baseline.

Gate: no measured cold discovery blocks passive drawing, cached settings remain
current after reload, and visible editing behavior is unchanged.

## S12. Improve Shared Advanced Preparation Safely

Anchor: [AdvancedSharedPreparationService.cs](../../../../XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedSharedPreparationService.cs).

- [ ] Measure cache misses, cold capacity growth, extraction, deformation,
  publication/copy bytes and lock waits separately. Establish actual world/view
  consumers before assuming single-publication thrashing.
- [ ] If growth matters, pre-size/reuse suitable storage first and validate it.
  Only then consider moving measured construction out of the shared critical
  section as a separate lifetime-reviewed item.
- [ ] Retain coherent immutable generations and consumer leases. Never expose
  mutable extractor spans to remove copies, and never reuse storage while a
  deferred consumer still reads it.
- [ ] Validate stationary/moving scenes, changed geometry/materials, multiple
  views and actual alternating worlds, deformation where active, supersession
  and consumer teardown. Introduce per-world caching only if evidence warrants it.

Gate: extraction and contention meet budget; every consumer gets the right scene,
view and generation; copied data remains coherent and retired storage is bounded.
Do not mislabel nonblocking deformation polling as a proven GPU wait.

## S13. Reduce Recurring Recording And Source Preparation

Use the S02 trace to choose one owner, not a batch of speculative micro-optimizations.

- [ ] Attribute command scans, interface fingerprints, source metadata checks,
  descriptors and texture readiness by count/bytes/hit/miss/dirty generation.
  Account for resident-table and family-lease reuse already present.
- [ ] For a demonstrated recurring cost, cache or patch by real mutation identity:
  shader configuration, layout/device, material/texture content and metadata,
  sampler epochs and accepted output/view generation. Do not remove frame IDs
  without an equivalent freshness proof.
- [ ] Validate unchanged reuse and one mutation of each relevant input, resize,
  view changes, reload and stale completion. Confirm unchanged inputs reduce work
  and changed inputs invalidate precisely the required artifacts.
- [ ] If remaining cost is cold canonical raster PSO admission, pre-admit that
  work through existing queues as a separate item with pending/failure validation.
- [ ] If required texture upload/finalization is responsible, separately prepare
  and budget it using the upload queue/generation ledger. Validate transfer
  completion before descriptor publication, failed/stale uploads and retirement.
- [ ] Check that reducing recording does not increase resource-preparation time,
  worker backlog, GPU time, missing content or successful-present latency.

Gate: a measured repeated cost decreases within the declared budget without stale
bindings or data. Each chosen owner/mutation contract gets its own child gate.
If the warm GPU workload remains limiting, record a separately scoped GPU task
instead of declaring CPU changes sufficient or adding unvalidated GPU edits.

## S14. Address The Actual Core Update Owner

Anchor: [RuntimeWorldLifecycle.cs](../../../../XREngine.Runtime.Core/World/RuntimeWorldLifecycle.cs#L59).

- [ ] Profile actual Normal/Late callbacks, tick order, pending registration drain
  and callback identity. Do not instrument only the unrelated legacy tick list or
  treat XREvent listener indices as world IDs.
- [ ] If pending registration dominates, retain ordered dispatch and improve
  membership/application cost. Define a coherent batch boundary, snapshot sizing
  under the owning lock and activation/deactivation semantics before adding a cap.
- [ ] Validate duplicate registration, add/remove order, changes made during a
  callback, bulk activation, Play transitions and teardown; compare callback
  sequences and final membership, not only timing.
- [ ] If a callback/wait dominates instead, create one item for that exact owner.
  Verify actual probe/physics consumers, worker dependency and timer debt before
  changing scheduling. Do not drop simulation steps or move app-thread publication
  based only on a broad world-update label.

Gate: update ordering and play semantics remain correct, actual work/pressure is
distinguished, and the measured cause improves. A GC/descheduling explanation or
negligible warm update cost defers unrelated tick optimizations.

## S15. Preserve Temporal Correctness And Resolve The Original Report

This track remains separate from stall-removal claims. Run its relevant checks
after every change that affects frame/view identity, admission or publication;
do not wait until integrated closeout to discover broken TSR history.

- [ ] Baseline and recapture stationary detail, controlled camera motion,
  disocclusion, camera cut, resize and pipeline/view switches with matching settings.
- [ ] Inspect saved images/sequences and correlate exact history/view/frame IDs,
  jitter, previous/current matrices, velocity, depth and reset/publication events.
  Check multiple camera positions so stale output cannot masquerade as success.
- [ ] Confirm deferred TAA/TSR bindings still consume the correct immutable
  pipeline-owned snapshot after admission/lifetime changes. Quiet warnings and
  a `TsrOutputTexture` name are insufficient.
- [ ] If a temporal defect is isolated, create one separately reviewed fix with
  the same build/live/A/B/regression gate. Do not lower history feedback, disable
  picking, hide warnings or change quality to mask it.
- [ ] Record whether the original long-recording trigger now reproduces, what
  exact change explains any improvement, and the user's ghosting/performance
  confirmation. Keep unavailable original evidence explicitly unresolved.

Gate: classify temporal behavior as validated, still failing, or unverified with
evidence. Unverified/failing temporal behavior blocks closing the original report,
even when individual CPU items have passed.

## Validation Operations

These are instructions for future item execution, not commands run while writing
this TODO. Select the narrow owning build, then build/run an isolated editor that
contains that exact change. Typical owning projects are:

```powershell
dotnet build .\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Do not run all five for every item. Shared-code changes require the relevant
backend/dependent validation; a Vulkan-only change does not require unrelated
application builds. Use isolated outputs if normal editor outputs are locked.

- [ ] Follow repository scratch retention and reserve one bounded task run before
  creating evidence. Use its `logs/`, `reports/`, `mcp-output/`, `mcp-captures/` and,
  when necessary, `renderdoc/` folders. Never rely on disposable evidence as the
  sole durable record of a gate result.
- [ ] Verify requested backend/scene/settings before launch. Use a unique named
  session per live iteration through the session manager; never manipulate the
  user's editor by process name or assume a default MCP endpoint belongs to it.
- [ ] Use `pwsh` where installed; the recorded Windows environment also supports
  this explicit Windows PowerShell session-manager invocation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Start -Name <unique-item-session>
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Invoke-Mcp.ps1 -Session <unique-item-session> -Method ping
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Stop -Name <unique-item-session>
```

- [ ] Capture CPU profiles and state through that exact session. Store viewport
  captures under the current task run and view the PNGs. For visually ambiguous
  pass/resource failures, use the repository RenderDoc workflow; do not treat
  capture/replay timings as an unperturbed performance benchmark.
- [ ] Review the owned session's logs, separating steady-state issues from
  shutdown noise. Restore temporary settings, stop only owned sessions, close
  capture tools and record cleanup before completing the gate.

### Per-Item Gate Record

Copy this record into the investigation for each item/child; link it from the
execution ledger when its status changes. Use repository-relative evidence paths.

```text
Item / owner / status:
Prior validated item and any approved reordering:
Hypothesis and disconfirming check:
Baseline source/configuration/cache/scene identity:
Change scope and dependency/lifetime invariants:
Predeclared metrics, budgets, tolerance, repetitions and window:
Changed source diff and validated binary/session identity:
Focused build command and result, including warnings:
Live scenarios and exact evidence paths:
Before/after distributions, sample validity and observer overhead:
Correctness/images, failure cases, retention and adjacent regressions:
Pass/fail decision and reason; does it explain the original symptom?:
Test clearance state, approved focused checks and results:
User confirmation / remaining risks / next permitted item:
Temporary settings and owned session cleanup:
```

## S16. Integrated Acceptance And Closeout

Do this only after individual gates pass; it must not be the first validation of
any fix. Compare both the previous validated increment and the original baseline
so cumulative cost shifts are visible.

- [ ] Complete matched cold-start/cache-warm restart and warmed still/moving-camera
  runs; report all-frame p50/p95/p99/max, successful-present intervals, dropped
  timing samples, preparation, recording, waits, GPU and loading time separately.
- [ ] Complete repeated resize, shader/pipeline reload, mesh/index/texture admission,
  multi-view ownership and teardown cycles, including the affected failure cases.
- [ ] Validate Play entry/exit, probe refresh, repeated redraw/picking, attachment
  metadata and idle BVH diagnostics so prior fixes remain intact.
- [ ] Validate first-use/warmed toolbar and camera settings, plus OpenGL/shared
  backend and XR/stereo paths where changed code affects them. Missing hardware
  evidence remains an explicit blocker for that claim, not a presumed pass.
- [ ] Inspect temporal sequences and record user confirmation; retain any original
  symptom that is not reproduced or explained as a separate unresolved issue.
- [ ] Confirm no new hot-path allocations, unbounded native/managed retention,
  queue starvation, unsafe disposal, silent fallback or missing required draws.
- [ ] After explicit test clearance, run/add only the appropriate focused
  regression coverage and record results. Keep uncleared gates visibly pending.
- [ ] Update the investigation with attempted fixes, successes/failures and exact
  validation evidence. Update related master/child items only for demonstrated
  coverage, not by blanket-closing their broader contracts.
- [ ] Assign every deferred/blocked item an explanation and reopening condition;
  distinguish individual validated improvements from closure of the original
  CPU/TSR regression. Record final settings restoration and owned-session cleanup.

Final acceptance: all applicable per-item gates and integrated checks pass, any
remaining exclusions are explicit, and the original report is closed only with
supporting reproduction/correction evidence and explicit user confirmation.
