# Vulkan Foundation — Completed Implementation Record (Legacy Phases 0–5)

Updated: 2026-09-06
Source snapshot: `8b104bf7a` (2026-09-04)
Status: historical completed implementation/evidence; no active task ownership.

This record preserves **121 previously checked foundation items**, their
contracts, and dated evidence narratives. It does not claim that all foundation
acceptance is complete. **29 previously open items** remain in the
[master tracker](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#foundation-carryovers)
with stable `F0-*`, `F1-*`, `F3-*`, and `F5-*` IDs. No open requirement was
closed by this move. Phase 2 structural completion does not waive its final
submission-time target; Phase 8 retains that integrated performance gate.

The text below is historical evidence, not a new source/runtime certification.
Older statements such as “active gateway,” “phase not complete,” or “ready for
Phase 7” describe their original checkpoints. Open task text now has one owner
in the master; the replacement links here preserve its former location.
For current XR/Advanced work, use the [active Phase 6/7 checklist](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md).

| Legacy phase | Previously checked items retained | Open items carried to master |
|---|---:|---:|
| 0 | 8 | 9 |
| 1 | 7 | 14 |
| 2 | 8 | 0 |
| 3 | 14 | 3 |
| 4 | 42 | 0 |
| 5 | 42 | 3 |

---

<a id="phase-0"></a>
### Phase 0 - In-Flight Checkpoint & Present-Now Live Revalidation

**Status:** Desktop implementation and the capacity-one live smoke are complete.
The remaining rows are cross-condition validation and the 2026-09-01
transaction-ownership incident, not a request to replace the frame-loop
architecture.

**Contract:** A `PresentNow` output either submits and presents the exact accepted
frame, returns a typed pre-acquire retry while a dependency is making progress,
or fails with a typed terminal reason. Cold readiness cannot replay stale
content, poison later work, lose the captured epoch, accumulate authoring work
from rejected attempts, or use exceptions as an ordinary retry protocol.

#### Completed implementation

- [x] Keep runtime-only events, binding publishers, and transient light state out
  of asset persistence; dispatch recognized binary texture-cache payloads before
  YAML and fall back only to the original source asset.
- [x] Propagate `PresentNow + BlockForExact` through the full desktop producer
  closure and forbid ordinary foreground `Deferred` results.
- [x] Capture immutable camera, visibility, material, light, output, and resource
  generations in a preallocated `VulkanAcceptedFramePlan`; move
  format-independent readiness before swapchain acquisition.
- [x] Replace cohort poisoning with monotonic resource tickets and generation-safe
  staging publication; queue pressure preserves accepted work and completed
  progress.
- [x] Give mandatory pipelines, uploads, shadows, and missing secondary recording
  explicit foreground completion paths with bounded capacities and visible
  failures.
- [x] Require unresolved-ticket count zero before native recording and require
  every `PresentedNew(frameId)` result to name the matching submit serial and
  presentation dependency.
- [x] Publish allocation-free liveness breadcrumbs and typed terminal failures for
  acquire, readiness, recording, submission, presentation, device, and memory
  failures.
- [x] Complete the isolated capacity-one Sponza camera sweep with monotonic
  submission progress, fresh frames, and no cohort poisoning, renderer pause,
  device loss, VUID, or validation error.

#### Carried validation gates

> Open requirement moved to [F0-01](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-01); its completion status remains active there.
> Open requirement moved to [F0-02](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-02); its completion status remains active there.
> Open requirement moved to [F0-03](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-03); its completion status remains active there.
> Open requirement moved to [F0-04](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-04); its completion status remains active there.
> Open requirement moved to [F0-05](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-05); its completion status remains active there.
> Open requirement moved to [F0-06](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-06); its completion status remains active there.
> Open requirement moved to [F0-07](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-07); its completion status remains active there.
> Open requirement moved to [F0-08](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-08); its completion status remains active there.
> Open requirement moved to [F0-09](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f0-09); its completion status remains active there.

**Conclusion:** The isolated capacity-one result remains valid, but the
2026-09-01 avatar incident reopens transactional operation ownership, transient
generation classification, one-shot consumer settlement, and reusable-artifact
freshness semantics. Epoch mutation, declared-capacity overflow, XR deadline
behavior, and RenderDoc capture remain correctness gates for Phases 8–9.
Detailed earlier evidence is retained in
`docs/work/investigations/rendering/vulkan-present-now-frame-readiness.md`.

---

<a id="phase-1"></a>
### Phase 1 - Baseline Characterization, Telemetry Taxonomy, & Deliberate Pacing

**Status:** Benchmark, presentation-policy, and correlated telemetry
infrastructure are implemented. Matched multi-run promotion baselines and the
native recording isolation matrix remain open.

**Contract:** Every reported frame cost has a stable lifecycle owner. Performance
runs are isolated from validation/capture overhead, use explicit presentation
policy, and report actual present intervals rather than inferred CPU cadence.
An aggregate `PrimaryRecording` or frame-root value is never sufficient evidence
for a native command-encoding conclusion.

#### Completed implementation

- [x] Freeze benchmark manifests with revision/dependencies, machine/driver,
  power/display/window state, scene/camera, feature stack, strategy, Vulkan
  configuration, validation state, and active OpenXR runtime.
- [x] Provide `ReleaseBenchmark`-equivalent runs, warm shader/pipeline/material/
  resident/import/swapchain state, and report p50/p95/p99/max, deviation,
  periodicity, deadlines, allocations, native work, submissions, readback, maps,
  and waits.
- [x] Implement `Stable` FIFO, `LowLatency` Mailbox with bounded limiter,
  `Uncapped` Immediate, and separate frame-generation presentation policies.
- [x] Attribute frame-slot reuse at the earliest legal authority boundary,
  publish readiness for non-render work, independently pace secondary ImGui
  swapchains, and coalesce resize at the frame boundary.
- [x] Capability-probe present ID/wait/display timing and record actual
  presentation intervals.
- [x] Publish one allocation-free correlated frame tree spanning pacing,
  handoff, acquire, planning, preparation, scheduling, recording, submission,
  output completion, and settlement, with causal wait payloads and device/
  memory/submission diagnostics.
- [x] Preserve stable IDs and the same lifecycle taxonomy across logs, captures,
  editor views, and MCP.

#### Carried benchmark and attribution gates

> Open requirement moved to [F1-01](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-01); its completion status remains active there.
> Open requirement moved to [F1-02](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-02); its completion status remains active there.
> Open requirement moved to [F1-03](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-03); its completion status remains active there.
> Open requirement moved to [F1-04](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-04); its completion status remains active there.
> Open requirement moved to [F1-05](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-05); its completion status remains active there.
> Open requirement moved to [F1-06](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-06); its completion status remains active there.

#### Native Command Recording Attribution and Isolation

> Open requirement moved to [F1-07](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-07); its completion status remains active there.
> Open requirement moved to [F1-08](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-08); its completion status remains active there.
> Open requirement moved to [F1-09](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-09); its completion status remains active there.
> Open requirement moved to [F1-10](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-10); its completion status remains active there.
> Open requirement moved to [F1-11](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-11); its completion status remains active there.
> Open requirement moved to [F1-12](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-12); its completion status remains active there.
> Open requirement moved to [F1-13](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-13); its completion status remains active there.
> Open requirement moved to [F1-14](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f1-14); its completion status remains active there.

**Conclusion:** The final heavy-load revalidation crossed a 21.679 s cold frame
without losing liveness and achieved 99.9876% sampled attribution after the wait
taxonomy correction. Those are implementation checks, not a frozen promotion
baseline or native-recording attribution result; the matched repetitions,
isolation ladder, and A/B matrix remain in Phase 8.

---

<a id="phase-2"></a>
### Phase 2 - Submission Fast Path & Granular Invalidation

**Status:** Submission-side implementation complete. The measured sealed-hit
percentile misses the promotion target, which remains unchanged in Phase 8.
Recording-side manifest closure remains in Phase 4.5.

**Contract:** Stable submission is proportional to compact generation/state
vectors, and local mutation invalidates exact reverse dependents. Full discovery
and broad invalidation are explicit cold/correctness paths.

#### Completed implementation

- [x] Instrument the tracked submit gateway with allocation-free stage,
  seal/fallback, parity, exact-invalidation, and broad-invalidation histograms.
- [x] Attach an immutable `SealedSubmissionContract` to reusable command
  artifacts with ABA-safe command/resource slots, descriptor generations,
  image entry/exit versions, render-target scope, nested artifacts, queries, and
  native lifetime closure.
- [x] Use flat retained batch receipts and direct resource records on stable hits;
  keep dictionary discovery and full validation only for cold, dirty,
  instrumented, sampled-correctness, ownership-transfer, or generation-change
  paths.
- [x] Batch lifetime pins by dependency manifest, serialize through the existing
  submission-state authority, hold the queue lock only across native submit, and
  aggregate each output's prepared command vector into one coarse tracked
  submission where practical.
- [x] Publish independent topology/content/lookup domains and exact dirty ranges
  for frame, view, pass, draw/object/instance, material, geometry, texture,
  sampler, descriptor, pipeline/layout, shader, shadow, and probe state.
- [x] Maintain compact logical and Vulkan resident/native reverse graphs for
  material/resource and pipeline/layout/descriptor/shader/render-pass/output
  dependencies; preserve tombstones and generation-safe reuse through retirement.
- [x] Keep a migration-only broad correctness fallback with typed reason, owner,
  domain, affected-entry count, and publication sequence.
- [x] Route material, texture, geometry, shader, shadow/probe, camera, and object
  mutations through exact owner deltas; retain the integrated mutation proof as
  a Phase 8 gate.

#### Evidence and conclusion

The final Release cohort recorded 79 sealed hits, 36 `MissingContract` cold
fallbacks, zero `ResourceVector` fallbacks, and zero broad resident
invalidations. Sealed-hit gateway timing measured 0.4096 ms p50, p95 in the
0.8192–1.6384 ms histogram bucket, and 6.5536 ms p99. Phase 2 is structurally
closed, but the `<0.25 ms` p95 requirement is not met and remains unchecked in
Phase 8. Detailed implementation evidence is in
`docs/work/investigations/rendering/vulkan-frame-loop-phase2-2026-08-27.md` and
`docs/work/investigations/rendering/vulkan-frame-loop-phase23-finalization-2026-08-28.md`.

Phase 2 closes the submit gateway only. It does not establish that command
recording consumes a prevalidated bulk native manifest or avoids per-command
resource-generation discovery, dependency insertion, command-buffer lookup, or
shared bind-state synchronization. Phase 4.5 owns that distinct closure.

---

<a id="phase-3"></a>
### Phase 3 - Canonical GPUScene Residency, Stable Bins, & 5 Strategy Lanes

**Status:** Canonical residency and five-lane sealing are implemented. Common
CPU/GPU submission convergence, rendered parity, and portable promotion remain
open.

**Contract:** `AdvancedSharedGpuSceneDatabase` is the normal-frame Vulkan
resident authority. One immutable publication and SoA image feeds stable bins
and all five resolved strategy lanes; diagnostics are asynchronous sidecars and
never production feedback.

#### Completed implementation

- [x] Publish bounded delta journals, tombstones, acknowledgements, ABA-safe
  handles, immutable submission rows, dirty owner ranges, reverse manifests,
  packed material/resource/layout/kernel/global records, and compact exceptions.
- [x] Lower exact retained publications into frame-slot-owned Vulkan table,
  lookup, sampled-image, and sampler storage with runtime-owned descriptor sets,
  ABI validation, generation leases, and completion-owned receipts.
- [x] Remove `BackendReadyMeshSelection` and all mutable legacy-selection
  authority from normal Vulkan input. Keep unrelated OpenGL, RVC, BVH, GI, and
  physics `GPUScene` consumers for their explicit Phase 9 cutover.
- [x] Publish complete frequency-owned SoA streams and up to 32 exact mapped
  dirty ranges with typed conservative-collapse telemetry.
- [x] Resolve direct-slot resident templates through structural/content/table/
  recording generations, transactional native leases, exact reverse eviction,
  and completion-owned lifetime pins.
- [x] Maintain numeric stable bins, intrusive membership, immutable bin/template
  manifests, target-late lowering, ordered exception streams, and direct/CPU-
  indirect parity scaffolding.
- [x] Seal all five lanes before worker execution: `CpuDirect`,
  `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`,
  `GpuMeshletZeroReadback`, and `GpuMeshletInstrumented`. Capacity, downgrade,
  output-family, and compatibility failures are explicit.
- [x] Attach diagnostic plans only to instrumented passes, copy through a bounded
  completion-owned ring, poll/decode off the render path, and drop diagnostics
  without changing output when saturated.
- [x] Prove bounded strict zero-readback operation for indirect and meshlet lanes
  with zero generic readback bytes, buffer maps, CPU fallback, or readback-
  caused waits.
- [x] Publish immutable per-pass shadow/probe coverage from the retained
  submission image and reject any sequence, count, pass, generation, dirty-range,
  or use mismatch before Vulkan native realization.
- [x] Keep descriptor-indexing alternatives, descriptor heap, device-generated
  commands, buffer-device-address, and mesh-shader tiers capability-gated and
  outside baseline promotion.

#### Common CPU/GPU Resident Submission Follow-Up (Data-Path & Residency Contract)

This track owns the **data-path and residency contract** unifying CPU and GPU submission strategies. The corresponding **command recording execution engine** (lane recording context, removing `VkMeshRenderer.RecordDraw`, and zero per-draw locks) is owned and executed under **Phase 4.5**.

- [x] Unify `CpuDirect` data ingress to consume the canonical resident templates (`VulkanResidentDrawTemplateTable`), material tables, geometry ranges, view/pass records, and stable bins (`VulkanPreparedStableBinStream`) used by GPU strategies instead of maintaining a second draw-oriented backend path.
> Open requirement moved to [F3-01](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f3-01); its completion status remains active there.
> Open requirement moved to [F3-02](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f3-02); its completion status remains active there.
- [x] Keep transparent, UI, callbacks, queries, and semantically ordered work in explicit bounded exception streams with independent cost and compatibility telemetry.
> Open requirement moved to [F3-03](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f3-03); its completion status remains active there.
- [x] Coordinate with Phase 4.5 to ensure all production encoders consume immutable backend-ready records with zero live renderer/material traversal.

#### Evidence and conclusion

The final five-lane Release cohort resolved every requested strategy, preserved
workload hash `12941640762020391990`, and recorded zero fallback events and zero
VUIDs. Both zero-readback lanes reported zero generic readback bytes and maps;
the indirect lane requested/consumed 2403 draws per sample, while the meshlet
lane emitted 292 task records across two produced frame operations. The
post-coverage smoke repeated 2403 requested/consumed draws with no dependency
rejection or broad invalidation.

This proves publication, resolution, dispatch, lifetime, and diagnostic
separation. It does not prove shaded-output parity or common CPU/GPU encoder
convergence because the current promoted advanced graph deliberately terminates
in its empty-output diagnostic clear and CPU-indirect remains scaffolding.
Rendered five-lane parity, the common backend, mutation matrix, diagnostic
saturation, cross-vendor descriptor tiers, hardware/OpenXR coverage, and
performance promotion remain in Phase 8. The durable closeout is
`docs/work/investigations/rendering/vulkan-frame-loop-phase23-finalization-2026-08-28.md`.

---

<a id="phase-4"></a>
### Phase 4 - Concurrency Closure, Multi-Lane Render Work Pool, & Native Encoding

**Goal:** Centralize process thread budgets, eliminate worker oversubscription,
provide zero-allocation pooled batches, migrate command recording to lane-affine
render workers, and finish the production encoder as an immutable command-local
serializer rather than a live draw-preparation path.

#### 4.1 Execution Topology & Thread Budget
- [x] Centralize foreground reservations, general/render domains, the retained compiler lane, auxiliary job lanes, and other dedicated lanes in immutable `EngineExecutionTopology` diagnostics.
- [x] Reject explicit configurations that oversubscribe processor count after foreground and dedicated-lane reservations.
- [x] Implement deterministic startup auto-sizing with render-thread participation and no hidden worker when a domain resolves to zero.
- [x] Remove the `RuntimeEngine.Jobs` compatibility facade and route runtime rendering general work through the installed `IRuntimeRenderWorkServices` capability.
- [x] Replace `JobManager`'s lazy deferred-enqueue and remote-dispatch thread-pool loops with topology-owned, signal-blocking scheduler lanes and bounded joins.
- [x] Keep driver-blocking Vulkan pipeline compilation on its topology-reserved below-normal background lane until it can be safely budgeted; migrate Vulkan/OpenXR recording onto the render domain under Phase 4.3.

**First Phase 4 slice (2026-08-29):** seven call sites across six
runtime-rendering types now schedule through
`RuntimeRenderingHostServices.Work.GeneralJobs`; the compatibility source file
is deleted, and startup validation proves the host capability and `Engine.Jobs`
resolve the same process-owned manager. The Release editor build completed with
zero warnings and errors. A bounded CPU-direct Vulkan smoke preserved workload
identity `12941640762020391990` across 26 capture samples, completed frame 1309,
reported zero fallback and forbidden-policy events, reached live MCP
diagnostics, and shut down cleanly. This is lifecycle evidence, not a Phase 8
performance result. The then-remaining command-chain and OpenXR eye-record
workers were migrated onto lane-local render-domain state in Phase 4.3.

**Second Phase 4 slice (2026-08-29):**
`EngineJobAuxiliaryWorkDomain` now owns persistent deferred-admission and remote-
dispatch lanes with coalesced signal-only wakes, explicit metrics, and the same
bounded lifecycle deadline as the general domain. `JobManager` no longer lazily
creates either loop through `Task.Run` or `TaskCreationOptions.LongRunning`.
Topology diagnostics name both lanes, startup validation requires both to be
live, and `VulkanPipelineCompileTask` now matches its documented below-normal
priority. Runtime.Core and full Release editor builds completed with zero
warnings and errors. A CPU-direct Vulkan smoke preserved workload identity
`12941640762020391990` across 27 capture samples, completed frame 1259, reported
zero fallback and forbidden-policy events, reached live MCP diagnostics, and
shut down cleanly. This is lifecycle evidence, not a Phase 8 performance result.

#### 4.2 Allocation-Free Pooled Render Batches
- [x] Implement pooled, generation-checked batch/item storage, stable lane IDs, dependencies, cancellation, bounded teardown, render-thread participation, and backend attachment registration in `EngineWorkScheduler`.
- [x] Dispatch renderer-neutral batches through `IRenderWorkExecutor` without one managed `Task` or job object per item.
- [x] Use bounded queues with inline execution, lane affinity, and work stealing for eligible preparation work.
- [x] Ensure idle workers block on signal-only waits (no periodic polling wakes).
- [x] Fault batches atomically and quarantine the domain on worker exceptions.
- [x] Prove build/rent, dispatch, execute, and merge allocate zero managed bytes after warmup; do not infer this from functional scheduler completion.
- [x] Bound preparation to at most $4 \times (\text{renderWorkers} + 1)$ migratable tasks per phase; dispatch only with at least two independent tasks and predicted savings greater than measured queue + wake + merge cost plus hysteresis.

**Third Phase 4 slice (2026-08-29):** `RenderWorkDomain` now admits at
most four migratable items per logical lane, deterministically pins surplus or
unprofitable work to lane 0, and leaves explicit lane affinity mandatory. Its
allocation-free policy requires two initially independent migratable items and
compares predicted saved work against measured queue-operation, signal-to-wake,
and merge cost plus 25%/50-us minimum hysteresis. Normalized item cost is
converted through an execution-time EWMA, and the new decision/cost counters
are exposed in `RenderWorkDomainMetrics`. Startup warms the pool, then requires
32 consecutive batches to increase every build, dispatch, execute, and merge
operation counter without increasing any corresponding managed-byte counter.
Runtime.Core and full Release editor builds completed with zero warnings and
errors. A Release Vulkan unit-testing startup with two general and two render
workers reported three logical render lanes, a 12-item migration cap, 132
inline items in the allocation probe, one unprofitable over-cap probe with one
exactly pinned surplus item, and zero post-warmup bytes in all four stages;
evidence is in `Build/_AgentValidation/20260829-014400-phase23-closeout/logs/`
(`phase42-work-scheduler.log` and `phase42-editor-bootstrap.log`).
Phase 4.2 is complete; Phase 4.3 subsequently attached native Vulkan lane state
and moved command recording onto those lanes.

#### 4.3 Multi-Lane Vulkan Command Recording
- [x] Attach transient command pools and retained-artifact arenas per logical render lane, frame slot, and queue family; reusable artifacts must never live in a transient-reset pool.
- [x] Replace persistent command-chain thread array and OpenXR eye threads with render-domain lane-affine tasks.
- [x] Enforce measured coarse-task rules: never dispatch fewer than 10 draws/dispatches per secondary, target at least 32 where it wins, and cap secondaries per scope at $2 \times (\text{renderWorkers} + 1)$.
- [x] Dispatch only immutable prepared ranges; workers never traverse live materials, renderers, callbacks, or mutable planner state.
- [x] Inline small batches directly on the render thread.
- [x] Merge secondary command buffers in canonical bin/range order independent of worker completion order.
- [x] Allow adjacent bins to share a secondary only when render scope, inheritance, query, ordering, and queue-family contracts match.
- [x] Keep one reusable artifact instance per in-flight slot unless exact completion proves the prior instance is no longer pending.

**Fourth Phase 4 slice (2026-08-29):** Vulkan now registers a distinct
transient/retained command-faeCommand-chain recording and paired OpenXR
eye-primary recording use lane-affine `RenderWorkBatch` items; the old persistent
command-chain array, OpenXR eye threads, private worker pools, and wait handles
are removed. Mesh packetization enforces the 10-draw floor, the automatic
dispatch gate targets 32 eligible operations, and one scope admits at most
`2 * LogicalLaneCount` secondaries. Lane executors consume only frozen prepared
streams, while source-indexed result slots preserve canonical execution order
and exact compatibility gates prevent unsafe adjacent-bin coalescing. Reusablewd
artifacts live in retained pools keyed by frame slot; pending instances are
retired and replaced unless completion is proven.

The targeted Vulkan and full Release editor builds completed with zero warnings
and errors. An isolated Release Vulkan session with two background render lanes
reached completed frame 864, frame slot 1, successful submission serial 1059,
an operational device, and zero Vulkan validation messages or errors. Its five
resident draws correctly remained below the coarse-dispatch floor and executed
inline. The desktop run did not exercise an OpenXR runtime; OpenXR hardware and
performance acceptance remain in the Phase 8 matrix.

#### 4.4 Hot-Path Allocation & Interference Closure
- [x] Zero managed heap allocation during steady-state build, dispatch, execute, merge, submit, and present.
- [x] Replace dictionaries, LINQ, and closures with pre-sized arrays, spans, and struct enumerators.
- [x] Throttle background compiler and editor jobs during high-refresh active rendering.
- [x] Verify zero unexplained worker wakeups or lock waits $>0.1$ ms.

**Fifth Phase 4 slice (2026-08-29):** hot-path telemetry now measures
managed bytes independently for render-batch build/dispatch/execute/merge and
desktop submit/present, while scheduler, resource-lifetime, image-layout, and
submission gates report thresholded lock waits. Stable render work uses bounded
preallocated storage and noncapturing lane executors; staging retirement no
longer creates trim-time lists. The execution topology also propagates active
high-refresh state to compiler/editor auxiliary work, suppressing background
admission until foreground rendering exits the protected interval.

The final Release Vulkan soak passed the full-model startup transition and then
held submission allocation bytes at 22,904 and present allocation bytes at
9,528 across 8,746 additional frame-loop invocations: both steady-state deltas
were zero. Build/dispatch/execute/merge allocation counters were also zero,
unexplained worker wakes were zero, and no scheduler queue, Vulkan lifetime, or
image-layout lock wait exceeded 0.1 ms. The device remained operational, native
submit/present remained accepted, and Vulkan validation reported zero errors.

That soak also exposed and closed a retry liveness defect: a retryable canonical
texture-descriptor miss could reject an acquired `PresentNow` frame, suppress
the fresh submission that carried its pending upload, and freeze the UI. A
retryable healthy acquired frame may now record a fresh initialization clear,
pending upload, and current UI overlay; terminal failures still do not present,
and no stale scene command buffer is replayed. The advanced graph still owns no
shaded-output producer, so its solid-red empty-output diagnostic is expected
until Phase 8 implements and validates rendered output parity.

The Release editor and Debug unit-test project both built with zero warnings and
errors. All 110 focused Phase 3/4 Vulkan contract tests passed after their
source-layout assertions were updated for the canonical draw-ID streams,
render-domain lane scheduler, profiler authority, and split ImGui recorder.
Another 66 directly affected advanced-pipeline, geometry, visibility, package,
and lane-arena contract tests also passed.

#### 4.5 Native Command Encoding Fast-Path Closure

**Status:** ACTIVE IMPLEMENTATION GATEWAY (Slices 4.5a, 4.5b, 4.5c).
Phase 4.1–4.4 established process execution topology, allocation-free scheduler batches, multi-lane command pools, and zero steady-state hot-path allocations. However, the 2026-09-01 avatar benchmark revealed that command emission itself still performs live scene object traversal (`VkMeshRenderer.RecordDraw`), takes per-draw monitor locks (`_recordDrawSync`), queries global dictionaries (`TrackingBatches`, `_commandBindStates`), and performs per-command lifetime tracking.

Phase 4.5 transforms command recording into a pure, immutable command-local serializer structured across three execution slices:

##### Phase 4.5a — Frame-Operation Transaction Boundaries & Retry Classification
- [x] Enforce transactional lifecycle in `VulkanAcceptedFramePlan`: reset/drain authored operation queues on any rejected readiness attempt, preventing queue accumulation across retries and eliminating the 8,192 overflow.
- [x] Reclassify transient ticket generation staleness (e.g., `texture-upload:X:Y` stale during visibility promotion) as typed `EDesktopFrameFlow.RetryFrame` / `RecoverAfterStateChange` rather than latching `RendererTerminal`.
- [x] Clear depth-picking one-shot request flags in a `finally` block even when readback throws or encounters an unwritten generation, terminating repeating 47-exception loops.
- [x] Ensure readiness failures cleanly preserve accepted work without leaking incomplete draw operations into subsequent plans.

##### Phase 4.5b — Command-Local Recording Context & Pre-Sealed Manifests
- [x] Introduce `VulkanLaneRecordingContext` allocated per logical render lane and frame slot:
  - Command buffer handle and lane index.
  - Command-local direct bind state (last bound graphics pipeline, compute pipeline, vertex buffers, index buffer, push constants, dynamic viewport/scissor) with zero lock or dictionary overhead.
  - Pre-allocated flat buffer for image-access deltas.
  - Flat bitset / compact array for tracked native resource lifetime keys.
- [x] Remove global `_commandBindStates` dictionary lookups and `_commandBindStateLock` monitor acquisition from steady primary and secondary recording.
- [x] Seal and acquire one exact native resource manifest (`VulkanRecordingResourceManifest`) before `vkBeginCommandBuffer`; eliminate per-`vkCmd*` dictionary lookup in `Runtime.CommandBuffers.TrackingBatches` and per-command monitor locks.
- [x] Publish dependencies, image-access deltas, queue ownership, and artifact identity once as a sealed recording receipt (`SealedRecordingReceipt`) at command buffer completion (`vkEndCommandBuffer`).
- [x] Bind global descriptor tables (Set 2 / Set 3) once per command buffer, compatible scope, or required secondary boundary; eliminate per-draw descriptor re-binding.
- [x] Keep transient command pools per lane/frame slot separate from retained artifact pools, reset only after exact completion, and allocate no warmed command buffers.

##### Phase 4.5c — Direct Resident Mesh Serialization (Bypassing `VkMeshRenderer.RecordDraw`)
- [x] Implement `VulkanResidentMeshEncoder`: a stateless serializer reading directly from `VulkanResidentDrawTemplate` and `VulkanResidentDrawTemplateNativeState`.
- [x] Emit pure Vulkan commands (`vkCmdBindPipeline`, `vkCmdBindVertexBuffers`, `vkCmdBindIndexBuffer`, `vkCmdPushConstants`, `vkCmdDrawIndexed`) directly into the command buffer.
- [x] Forbid production command recording from entering `VkMeshRenderer.RecordDraw`, `RecordDrawNoLock`, or acquiring `_recordDrawSync`.
- [x] Eliminate renderer prewarm, shader reflection, dynamic descriptor allocation/update, pipeline creation, and live object locks from the command-emission interval.
- [x] Migrate dynamic skinning bone matrix uploads (`PushBoneMatricesToGPU`) and blendshape weight uploads (`PushBlendshapeWeightsToGPU`) out of the recording loop and into the worker preparation/upload phase (`VulkanFrameLoop.PrimaryRecordingPreparation.cs`).
- [x] Demonstrate that adding visible draws inside existing compatible bins primarily changes argument/data buffers; recording cost scales with passes, bins, and dirty ranges—not raw visible object count.
- [x] Complete the Phase 1 isolation ladder, retaining before/after profiles to prove that safety work was removed or bulk-published rather than shifted into begin/end or another worker.

This completes Phase 4.1–4.4 scheduler/lifecycle work and positions Phase 4.5 as the active execution gateway alongside Phase 3 Follow-Up; Phase 8 owns subsequent integrated performance, shaded-output, cross-vendor, and OpenXR promotion gates.

**Pipeline-source follow-up (2026-08-29):** the post-window capability pass no
longer creates a viewport-only pipeline override. New desktop cameras configure
`AdvancedRenderPipeline` as their source under the default `Available` policy;
camera-synchronized viewports retain that exact object, while the physical
`XRRenderPipelineInstance` owns its output-specific Vulkan reservation. Protected
sources still receive backend binding, and one failed/shared output cannot
downgrade another output by replacing the camera asset.

---

<a id="phase-5"></a>
### Phase 5 - Render Graph Simplification, Streaming, & Tail Latency Bounds

**Goal:** Reduce GPU deadline pressure, eliminate full-resolution copy passes, bound directional cascade and streaming spikes, and ensure safe swapchain recreation.

#### 5.0 Deadline-Aware Output Scheduling
- [x] Build one output manifest/DAG for uploads, shadows, desktop, OpenXR eyes, mirror, probes, captures, and publication; reserve acquired OpenXR critical work before optional outputs.
- [x] Use bounded, observable cadence/deferral/stale-reuse policy for optional work, narrow queue-lock ownership, and frozen modal-resize presentation packages.
- [x] Complete long-duration, performance, interactive-resize, and multi-output acceptance in the validation matrix.

#### 5.1 Render Graph & GPU Pass Stabilization
- [x] Preserve the implemented complete-scene normal/depth target (deferred attachment 1 + depth) with one forward opaque/masked overlay and no contact-copy/merge replay pair.
- [x] Execute the depth/normal path only when visible materials and active AO/contact-shadow consumers require it.
- [x] Eliminate the implemented redundant G-buffer restore/contact-copy pairs and full-resolution merge replays through declared graph transitions.
- [x] Cache compiled render graph; recompile only dirty subgraphs on local mutation.
- [x] Batch barriers by stage/access; replace broad `AllCommands` barriers with precise masks; coalesce adjacent subresource transitions.
- [x] Keep physical attachment aliasing fail-closed until asynchronous lifetime proof exists; then A/B transient aliasing/lazy allocation only for proven non-overlapping targets.

**Phase 5.0/5.1 closeout (2026-08-30):** the compiler now retains immutable
connected subgraphs and rebuilds only components whose pass identity or
revision changed. Synchronization2 barriers are emitted once per pass from
precise stage/access scopes and merge only exact adjacent image ranges; missing
frozen authority fails the frame instead of widening to `AllCommands`.
Transient alias/lazy allocation stays disabled in every mode. Analyze reports
eligibility; ProofGated explicitly reports the missing native handoff,
initialization, and positive-path validation contract. Declared interval
separation is not asynchronous lifetime proof. The conditional positive A/B
activation cannot proceed until that proof exists; no aliasing speedup is claimed.

The acceptance matrix covered 1,232 warmed desktop samples over 60 seconds, live Win32 modal
resize/recreate, Baseline/Analyze/ProofGated allocation policy, and a 240-frame
Monado cohort with strict single-pass stereo, mirror output, and six scripted
desktop resizes. All 160 retained XR frames submitted; the complete cohort had
163 submissions, with zero sequential fallback, end-frame failure, global in-flight
wait, forced flush, final pending retirement, or reported validation failure.
Full evidence and the runtime defects found during validation are recorded
in `docs/work/investigations/rendering/vulkan-phase5-output-scheduling-validation.md`.

#### 5.2 Bounded Shadows, Probes, & Occlusion
- [x] Define directional-cascade invalidation from camera, light, caster, receiver, atlas, and quality state; stabilize projections, reuse unaffected recording/data, and enforce a bounded update budget with explicit temporal policy.
- [x] Share GPU shadow records across all material kernels instead of large uniform arrays.
- [x] Stagger reflection probe and environment capture refreshes across frames.
- [x] Instrument occlusion candidates, occluders, tested/rasterized/rejected bounds, query age, Hi-Z build/test cost, CPU/GPU time, and false-positive/negative diagnostics in representative open, moderate, occluder-heavy, masked, static, and moving scenes.
- [x] Bound CPU software-occlusion candidate selection/sort/rasterization; define query latency/refresh/stale-result/camera-motion policy and bypass when estimated benefit cannot exceed cost.
- [x] GPU Hi-Z occlusion: persistent minimal-format Reverse-Z resources, one or two reduction/test dispatches, zero per-mip host work, measured crossover thresholds, visibility hysteresis, conservative bypass on camera cuts, and current-frame visibility kept on GPU.
- [x] Retain forced modes and a conservative no-occlusion fallback for diagnosis; do not promote any mode without measured crossover and visual parity evidence.

2026-08-31 closeout: **Phase 5.2 implementation and bounded acceptance complete**.
Shared shadows, probes, CPU occlusion and conservative R32F tiled Hi-Z retain
their existing budgets. Headless normal/reversed, two-cold-repeat validation
covers six representative workloads plus the original moving/cut fixture:
2,016 completed frames in passing cohorts, zero false occlusion or missing
visible output, and exact cold-repeat images. Moving-mask trajectories include
motion and settled tails; the deliberately conservative policy keeps visibility
on view changes and resumes culling after settling. Raster and compute now use
the same frozen physical planner generation; masked deferred rows honor coverage.

Actual native C−1/C/C+1 growth, after-seal rejection/retry, in-flight retention,
descriptor release and natural reclamation pass separately at 4096² in both
depth modes, including validation-enabled repeats with zero native errors.
The original warm deterministic-clear allocation gate also returns zero bytes.
Standard/synchronization validation passes the focused production lanes; loader
duplicate-layer warnings are recorded separately. No desktop control was used.

Earlier textured OpenGL controls and Vulkan's 1,080 calibration samples remain
valid; all six crossover buckets select `Disabled / NoMeasuredWin`. Diagnostic
readbacks never feed production visibility, and these correctness runs make no
new performance/default promotion or native Advanced shaded-output claim.
The investigation retains earlier failing runs and identifies their repairs.
Run instructions: `docs/developer-guides/rendering/renderbench-phase52-scenarios.md`.
See
`docs/work/investigations/rendering/vulkan-phase52-bounded-shadows-probes-occlusion.md`.

#### 5.3 Asynchronous Texture Streaming & Pipelines
- [x] Phase 5.2 prerequisite: defer OpenGL bindless handle publication until progressive mip upload has finalized sampler state; preserve pending/retry semantics. Cold normal and reversed-depth Disabled/full-Hi-Z/coarse-Hi-Z/Disabled-return controls matched textured raw albedo after the explicit bounded Pending upload interval; see the Phase 5.2 investigation.
- [x] Phase 5.2 prerequisite: prepare compute descriptors under the sealed operation's exact physical planner generation, including dynamic-stream contexts, without per-dispatch planner allocation.
- [x] Phase 5.2 prerequisite: give repeated compute occurrences distinct stable stream/occurrence identities across preparation, refresh, serial/secondary recording and reuse; never use the thin-primary ordinal to identify their descriptors or uniform data.
- [x] Keep imported texture decode/cache parsing, resize/mip generation and Vulkan image/staging preparation on workers. Bounded owned tasks survive cancellation/priority changes and retirement; legacy false flags cannot restore synchronous preparation. Cold worker-only upload and textured albedo acceptance pass; see `docs/work/progress/rendering/vulkan-phase53-worker-texture-preparation.md` for scope and unexercised fault-injection cases.
- [x] Coalesce uploads into bounded transfer submissions; reserve foreground staging ring capacity.
- [x] Stream textures larger than staging ring in bounded chunks.
- [x] Publish texture generations at deterministic frame boundaries with narrow descriptor updates.
- [x] Meter decode/prep, staging copy, Vulkan allocation, transfer recording/GPU, descriptor publication, queue age, and bytes/items; keep bursts within explicit publication/retirement budgets.
- [x] Prove one material scalar and one texture/sampler replacement update only their dependent ranges with zero stable per-draw descriptor validation or writes.
- [x] Bound stable material/descriptor-table growth with spare capacity, asynchronous staging/publication, and only a visible counted emergency wait.
- [x] Precompile common pipelines during warmup; persist `VkPipelineCache` keyed by GPU, driver, engine revision, render-target mode, and shader fingerprint.
- [x] Never synchronously compile pipelines on the render thread during steady state.

2026-08-31 closeout: **Phase 5.3 implementation and headless acceptance complete**.
Normal/reversed, two-repeat streaming and material matrices pass: exact native
mip/row contents, bounded large required uploads with fresh-plan retries,
coalesced submissions, cancellation-safe ownership and actual GPU timestamps.
Scalar and texture/sampler mutations each write one dependent row; warmed idle
frames perform no material page writes, descriptor writes or closure acquisition.
Eight cold/warm pipeline children pass cache provenance and zero steady-state
compile/create/wait gates. Focused Phase 5.2 visibility/native-lifetime and
zero-allocation clear regressions pass; editor and RenderBench build cleanly.
Native validation reports zero errors (loader warnings recorded separately).
No desktop control, live OpenXR acceptance or performance/default promotion is
claimed. Details and run guides:
`docs/work/progress/rendering/vulkan-phase53-headless-completion.md`.

#### 5.4 Resource Retirement & Swapchain Lifecycle
- [x] Phase 5.2 prerequisite: initialize/preserve auto-exposure history before capturing the pending generation's immutable descriptor manifest; retain strict commit validation. Live 1920x1080 → 1279x719 → 1920x1080 completes with normal/reversed-depth mode parity at the odd extent.
- [x] Phase 5.2 prerequisite: exclude logically tombstoned draw/material owners from new reverse-dependency snapshots while preserving physically retained rows and ACK-based reclamation; live scene deactivate/reactivate and selected-primitive mutation checkpoints pass.
- [x] Phase 5.2 prerequisite: refreeze required keyed native-buffer barriers on buffer publication changes, carry exact generations into recording pins, and reject superseded accepted packets for a fresh-frame retry without image/structural replanning. Headless normal/reversed 4096² runs prove C−1/C/C+1 growth, after-seal rejection before acquisition, fresh retry, recorded/in-flight retention, and natural reclamation after bounded dependent retirement rotations; validation-enabled repeats report zero native errors.
- [x] Phase 5.2 prerequisite: retain immutable read-only storage publications in captured operations and lower them into exact frame-slot/arena epochs; include slice identity in descriptor reuse and release capture ownership on retirement.
- [x] Phase 5.2 prerequisite: preserve retained capture ownership across scoped program binding resets, defer indirect descriptor lowering until prepared storage authority exists, and pass the acquired frame-data slot explicitly into indirect recording.
- [x] Phase 5.2 prerequisite: publish query capabilities to the live resource authority and recycle delayed timestamp pairs only after completion or proven unrecorded/abandoned epochs; expose bounded saturation and rejection diagnostics.
- [x] Meter destruction by resource class (images/views, buffers, pipelines, framebuffers, samplers, descriptors, command artifacts, callbacks) with per-frame caps and a reported high-water memory-safety drain policy.
- [x] Destroy retired resources outside global retirement locks.
- [x] Retire resources only after all relevant queue timeline values or fences complete.
- [x] Asynchronous swapchain-generation retirement: coalesce resize events, create replacement generation from newest extent, and tombstone old generations.
- [x] Keep one command pool per recording lane/frame slot, reset it only after exact completion, allocate no warmed command buffers, and preserve the separate dynamic ImGui overlay command buffer.
- [x] Bound concurrent old/new swapchain generations, inherit the strongest prior completion authority for reused mapped frame-data storage, and retire secondary ImGui swapchains independently.
- [x] Zero normal-frame `vkDeviceWaitIdle` during resize, minimize, restore, or swapchain recreation.

2026-08-31 implementation closeout: shared per-class
retirement budgets, exact queue/WSI proof, bounded asynchronous generations,
independent detached ImGui retirement, and warmed command reuse are implemented.
Live validation reaches 25 desktop generations with no normal-frame device-idle
calls or native validation errors; a 1,559-frame steady interval allocates no
command buffers. Cumulative retirement p99 is 0.084 ms through resize/restore
and 0.052–0.306 ms across four streaming children, below this cohort's 0.5 ms
stage target. Material, pipeline, native-buffer lifetime, and warmed allocation
regressions pass. This closes lifecycle acceptance, not Phase 6 XR or Phase 8
performance promotion; separate imported-scene/Advanced limitations are recorded
in the [implementation and validation evidence](../../investigations/rendering/vulkan-phase54-retirement-and-swapchain-lifecycle.md).

Follow-up validation correction (2026-08-31): the lifecycle cohort above used
discrete window resizes, not a held Win32 sizing drag. A user-reported live
relayout regression was reproduced and repaired. Actual held width/height
drags now render fresh scene/UI work in `DefaultRenderPipeline` (42 operations,
one compute dispatch, no package rejection). The final rebuilt cohort reports
zero native validation errors/device-idle calls and retirement p99 0.087 ms;
see [live resize investigation](../../investigations/rendering/vulkan-live-window-resize-relayout.md).

Release-continuity attempt (2026-08-31): recording now honors the frame's
latched interactive state, and a generation-explicit handoff retains the last
complete held presentation until a complete authored successor is presented.
Semantic-empty, clear-only, overlay-only, stale, and superseded successors cannot
replace it. Two actual held drags preserve the full ImGui layout and 3D scene at
mouse-up; the acceptance interval has no fresh full-surface clear, native VUID,
validation error, or device-idle call. Subsequent user testing showed that these
static checkpoints hid a 17- to 53-second pre-acquire presentation freeze, so
this did not close release continuity. The same user run exposed undefined
overlay accumulation when Advanced has no authored scene writer and a terminal
required-upload failure after switching to Debug Opaque.

**Phase 5.4 live acceptance and therefore the Phase 5 closeout are reopened.**
The lifecycle implementation rows above remain complete; these cross-pipeline
presentation gates remain:

> Open requirement moved to [F5-01](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f5-01); its completion status remains active there.
> Open requirement moved to [F5-02](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f5-02); its completion status remains active there.
- [x] Accept required-texture upload progress without a PresentNow terminal
  pause. Upload scheduling carries the exact renderer owner and backend
  generation, supports a bounded direct pre-frame drain, and cannot lose its
  scheduling edge when a worker completes or a scheduled drain faults. The
  final exact run advanced beyond frame 21,000 through repeated any-to-any
  pipeline replacements and stayed live well past the old 30/45-second failure
  windows with no terminal upload watchdog or delayed preparation drain.
> Open requirement moved to [F5-03](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#f5-03); its completion status remains active there.

Deadline handoff (2026-08-31 17:05 local): the Vulkan project builds with zero
warnings and zero errors, but Phase 5 is **not complete**. Before the final
published-generation change, repeated Default runs reached frame 92-103 and
then spent about 30 seconds in `RequiredUploadCompletion` with
`prepQueued=1, prepActive=0`. The queued preparation drain ran only after the
watchdog stored a `RendererPaused` terminal transition. The last post-change
run was stopped at the requested cutoff after 25 seconds with frame 93 still
`Completed/Success`; that interval is shorter than the prior failure window and
is not acceptance evidence.

Next work, in order:

1. Run a fresh isolated Default session for at least 45 seconds after Sponza's
   64-to-1024 texture promotion. Require advancing frame IDs, no
   `RendererPaused`, no `RequiredUploadCompletion` watchdog, and no upload-prep
   drain delayed by roughly 30 seconds.
2. If the stall recurs, instrument the exact required manifest ticket and its
   `_pendingPrepJobs` entry: sequence, streaming generation, state,
   `NotBeforeTimestamp`, worker task, pending upload, and in-flight count.
   Identify why the matching ticket cannot advance before changing policy again.
3. Once Default remains healthy, perform a real held bottom-right drag and
   capture start, held, released, and released-plus-two-seconds states. Require
   an extent change while `MouseHeld=true`, continuous frame-ID progress, and
   visible scene, ImGui, and FPS text without a black frame or ghost history.
4. Repeat that drag with `XRE_ADVANCED_RENDER_PIPELINE_MODE=Required`; require
   no range exhaustion, no missing authored base, and no overlay accumulation.
5. Validate `XRE_FORCE_DEBUG_OPAQUE_PIPELINE=1` from cold start, then perform the
   exact Advanced-to-Debug-Opaque asset replacement. Require no terminal
   transition, device loss, or Vulkan validation error.
6. Mark 5.4 and Phase 5 complete only after all three paths pass. Add or run
   regression tests only after the user clears test work under repository policy.

Resize/pipeline-asset implementation update (2026-08-31 19:44 local): the
viewport now publishes display size, camera internal-resolution policy, and
pipeline AA/upscale policy as one render-thread resource profile. Default and
Advanced both use the instance-owned generation path, so a settled native
resize produces one latest-wins display/internal generation rather than the old
internal-then-display pair. Window resize completion now validates each
viewport's actual display and scaled internal extents instead of assuming every
viewport renders at the full pending window extent.

Editor-camera pipeline replacement is now an atomic, render-thread-owned
transition. Requests collapse to the latest asset, each real asset reference
advances an instance-local pipeline revision, command publications are force
reset even for equivalent pass layouts, and generations retain the exact asset
owner used for destruction callbacks. This covers cross-type, same-type but
different-asset, layoutless, and strict same-reference no-op transitions. A new
`set_editor_camera_render_pipeline_asset` MCP action exercises the same public
camera replacement API as the ImGui asset picker.

Live acceptance for this implementation used the isolated
`pipeline-resize-swap` session. Advanced survived actual held width and height
border drags (`1920x1080 -> 1499x1080 -> 1499x819`) with matching active
generations and no pending/failure state. A Default resize initially exposed a
separate Vulkan buffer-policy defect: a logical buffer below 64 KiB rounded to
a 64 KiB device-local capacity while retaining host-visible metadata. The next
generation tried to map that device-local allocation. Memory policy and backing
selection now use the same planned capacity; the replay committed
`1920x1080 -> 2560x1369` in 208.87 ms with no generation failure. Fresh
Advanced-to-Debug and Default-to-Debug presentation swaps both changed the
visible output and installed only Debug-owned enabled passes. The state cohort
also passed Advanced-to-Debug-to-Default, Default-to-Advanced, two different
Advanced assets, and same-reference no-op assignment.

This does **not** close Phase 5. The pre-existing
`RequiredUploadCompletion` failure still reproduces after roughly 30 seconds in
fresh Default/Advanced sessions, after otherwise successful resize generations,
and stores a `RendererPaused` terminal transition. Long-duration acceptance in
steps 1-2 and the final full cross-pipeline cohort therefore remain open. The
implementation and evidence are detailed in the
[live resize investigation](../../investigations/rendering/vulkan-live-window-resize-relayout.md).

The repeated failing run is under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-123719-live-resize-regression/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-31_17-01-36_pid10200/`.
The stopped post-change sample is summarized in
`Build/_AgentValidation/20260831-123635-vulkan-live-resize/reports/default-published-generation-25s-summary.json`.

Root-cause evidence and the correction to the prior screenshot-based acceptance
are in the [live resize investigation](../../investigations/rendering/vulkan-live-window-resize-relayout.md).

Final resize/transition hardening update (2026-08-31 20:36 local): delayed
shared-asset callbacks now verify asset owner, pipeline revision, and command
generation while holding the transition lock. Vetoed property changes cannot
publish partial ownership, layoutless authority is retained until cleanup
succeeds, terminal teardown and notification callbacks are exception-isolated,
resize readiness rejects outstanding transitions, and output binding uses a
coherent request/applying/applied target.

The exact `pipeline-final-vk` build passed Advanced -> Default -> Debug Opaque ->
Advanced plus strict same-reference no-op assignment. Native maximize produced
matching `2560x1369` active generations for both Advanced (six textures, one
FBO) and Default (31 textures, 30 FBOs). The final Default replay visibly
presented the scene/editor UI after resize, committed its managed graph in
42.42 ms, converged the swapchain in 216.423 ms, ran for more than 75 seconds
after the asset change, and advanced beyond frame 2560. It recorded no scoped
transition/cleanup/resource-description failures, host-visibility failure,
device loss, `RendererPaused`, `RequiredUploadCompletion`, or validation VUID.
The explicit Sponza 64-to-1024 promotion marker was not identified, so the named
long-duration gate and remaining held Advanced/Debug rows stay open rather than
closing Phase 5 from this cohort alone.

Three-piece follow-up update (2026-08-31 21:50 local): Advanced scene storage
now declares and validates a fixed 32 MiB-per-slot reservation against the
shared 1 GiB frame-arena guard; Default upload preparation owns exception and
worker-completion rearm edges; and PresentNow admission distinguishes
`RetryFrame`, bounded `RecoverAfterStateChange`, and immutable
`RendererTerminal` failures. Pipeline replacement requests a recovery probe
only after the successor asset is fully applied, and recovery is published only
after a fresh recorded/submitted/accepted PresentNow frame.
Advanced set-1/set-2/set-3 capacity/integrity/native failures and typed upload
ledger terminal failures remain hard at the late recording boundary; recovery
requests arriving during a failed probe retain their sequence for the next
bounded admission attempt.

The final isolated replay passed Advanced -> Default -> Advanced -> Default,
same-reference no-op assignments, and more than 21,000 frames. It then recorded
two real Win32 modal resize cycles to `1436x699` and `1243x688`; each committed
the matching Default generation and resumed accepted presentation. There were
zero renderer-terminal transitions, in-flight descriptor update failures,
desktop-frame failures, validation errors, or VUIDs. The held intervals were
shorter than the readiness log's one-second sampling cadence, so formal
held-frame visual capture and the remaining Debug Opaque row stay open.

Debug Opaque CPU/exception closeout (2026-08-31 22:35 local): a hierarchical
Sponza trace showed that 9.331 ms of the reported CPU cost was unconditional
canonical Advanced resident-scene publication, while visible collection itself
was below 1 ms. Canonical publication is now pipeline-demand-driven and
coalesced per scene; Debug Opaque opts out while Default, Advanced, and RVC opt
in. The final Debug Opaque trace measured 0.343 ms collect, 0.039 ms swap, and
0.803 ms render CPU. The 11.914 ms wall interval was 10.977 ms presentation
pacing, with only 0.048 ms of render wait for collection. Ordinary PresentNow
readiness retries are typed values rather than thrown exceptions, and the
progressed-but-incomplete upload path returns immediately instead of waiting for
the watchdog window. Reflection discovery is cached once per assembly, and
OpenVR.NET is source-built instead of copying a stale ImageSharp-dependent
binary. The isolated Vulkan run reported zero loader failures, validation
errors, VUIDs, or targeted exception records. Details are in the
[Debug Opaque CPU and exception investigation](../../investigations/rendering/vulkan-debug-opaque-cpu-exception-storm-2026-08-31.md).

---

