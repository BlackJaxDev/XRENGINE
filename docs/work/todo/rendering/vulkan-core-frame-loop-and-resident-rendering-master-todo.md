# Vulkan Core Frame Loop, Resident Rendering, and High-Refresh Master TODO

Updated: 2026-09-06
Baseline: `8b104bf7a` (2026-09-04)
Status: active program index; XR/Advanced implementation and validation incomplete.

September 6 stopping point: final working-tree editor build passes with zero
warnings/errors, and the task-owned editor is stopped. The last Advanced
desktop-plus-probe runtime failed; subsequent output-bank/policy/memory fixes
are built but unrun. The [active tracker stopping point](vulkan-xr-and-advanced-rendering-todo.md#current-stopping-point--2026-09-06)
separates completed evidence, partial implementations and remaining work.
No Phase 8/9 promotion or integrated completion is implied.

## Tracker ownership

| Document | Owns |
|---|---|
| [Completed foundation record](../../progress/rendering/vulkan-phases-0-5-completed.md) | 121 previously checked Phase 0–5 implementation items and their dated evidence. It does not certify remaining validation. |
| **This master** | 29 unfinished foundation requirements, normative architecture/budgets, and Phase 8/9 integrated promotion, test clearance, deletion, and closeout. |
| [OpenXR and Advanced rendering TODO](vulkan-xr-and-advanced-rendering-todo.md) | The single actionable checklist for Phases 6/7, with former 7R remediation merged into the owning tasks. Separate implementation, audit, and runtime-validation IDs. |
| [Phase 6/7 investigation](../../investigations/rendering/vulkan-phase67-implementation.md) | Durable implementation decisions, failed approaches, commands/results, and runtime evidence. |

Do not duplicate checkboxes between these documents. Close a bounded item when
its stated evidence exists, in the same change that records the result. A
checked implementation box does not close its separate runtime-validation
box. Missing hardware, failed readback, and untested profiles remain open.
New tests still require runtime validation and explicit user clearance under
Phase 9.1; reorganizing documentation authorizes no implementation or test run.

The [core-hardening contract ledger](vulkan-core-hardening-and-device-loss-todo.md)
retains specialized occlusion requirements. Superseded Present-Now, frame-loop,
resident and recording TODOs have been removed after carrying their remaining
contracts into this master. The
[September 6 coverage audit](../../progress/rendering/vulkan-todo-coverage-audit-2026-09-06.md)
records completed/superseded documents, retained child owners, and exclusions.
The [target architecture](../../design/rendering/vulkan-render-loop-target-architecture.md),
[mesh submission contract](../../../architecture/rendering/mesh-submission-strategies.md),
and [testing tracker](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md)
remain normative companions.

## 1. Architecture Contract

The renderer will not be rewritten; rather, the resident data-oriented architecture will be finalized into a cohesive, high-performance pipeline:

1. **Truthful Foreground Execution:** `PresentNow` + `BlockForExact` for desktop and `MeetDeadlineWithGpuFallback` for XR; late acquire after format-independent readiness; monotonic generational resource tickets.
2. **Deliberate Presentation Pacing:** First-class `Stable` (FIFO) and `LowLatency` (Mailbox with hybrid sleep/spin limiter) profiles; attribute every wait above 0.1 ms and at least 99% of detailed frame-root wall time, with explicit gaps of at least $50\ \mu\text{s}$.
3. **Sealed Submission Fast Path:** `SealedSubmissionContract` validating static requirements once and executing clean submissions via compact generation checks (<0.25 ms CPU p95).
4. **Granular Reverse-Dependency Invalidation:** Surgical invalidation of material rows, textures, shaders, and geometry ranges without table-wide clears.
5. **Single Canonical Resident Authority:** `AdvancedSharedGpuSceneDatabase` using ABA-safe `AdvancedGpuHandle(Index, Generation)` handles and frequency-owned SoA streams feeding direct-slot `VulkanResidentDrawTemplateTable`.
6. **Stable Bins & 5 Submission Strategy Lanes:** Numeric `VulkanRenderBinKey` and bin-level manifests feeding `CpuDirect`, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, and `GpuMeshletInstrumented`.
7. **Asynchronous Diagnostic Sidecar:** `GpuDiagnosticReadbackPlan` using a fixed-capacity staging ring with zero current-frame waits, strict zero-readback separation, and general-domain decoding.
8. **Process-Wide Execution Topology:** `EngineExecutionTopology` and pooled `EngineWorkScheduler` owning non-oversubscribed general and render lanes with lane-local command arenas.
9. **Prepared Native Command Encoding:** One immutable backend-ready packet and sealed native-resource manifest feeds primary, secondary, inline, worker, CPU-direct, indirect, and ordered-exception encoders; command-local state and bulk lifetime publication replace per-command global discovery.
10. **Bounded Graph, Streaming, & Tail Work:** Forward+ single normal/depth prepass gating; budgeted cascade updates; asynchronous chunked texture streaming; tombstoned swapchain lifecycle (zero normal `vkDeviceWaitIdle`).
11. **Asynchronous OpenXR Decoupling:** `OpenXrVulkanSubmissionTracker` eliminating the 70–100 ms eye fence-wait with timeline semaphore / fence-ring completion authorities.
12. **Advanced Render Pipeline (ARP 06–10):** GPU material classification, native opaque shading, clustered lighting, visibility-driven transparency/post, and complete legacy retirement.

---

## 2. Budgets and Constraints

| Refresh Target | Hard Frame Deadline | Engineering Target (p99) |
|---|---:|---:|
| **100 Hz** (Level A) | 10.000 ms | 8.5–9.0 ms |
| **120 Hz** (Level B - Promotion Gate) | 8.333 ms | 7.1–7.5 ms |
| **144 Hz** (Level C - Stretch Gate) | 6.944 ms | 5.9–6.25 ms |
| **165 Hz / 200 Hz** | 6.061 ms / 5.000 ms | Characterization / Long-term target |

### Explicit Non-Fixes and Anti-Patterns

The following are strictly forbidden as solutions:
- Increasing queue or arena capacities to mask admission livelocks or operations accumulated across rejected attempts.
- Increasing worker counts beyond the physical execution budget.
- Polling in tight loops or busy-spinning across the entire frame interval.
- Re-introducing CPU readbacks, full bucket scans, or synchronous diagnostic waits into zero-readback passes.
- Enabling `SIMULTANEOUS_USE_BIT` to avoid correct slot-owned command pool management.
- Creating a second parallel scene database or residency registry.
- Calling `vkDeviceWaitIdle` during normal resize or swapchain recreation.
- Declaring native command encoding optimized solely because managed allocation and lock waits above 0.1 ms are zero while repeated uncontended locks, dictionary lookups, hashes, generation checks, or dependency insertions remain per command.
- Forcing native scene re-encoding solely to claim `PresentNow` freshness when fresh accepted data can legally execute through a compatible completed artifact.
- Committing or updating automated tests before live/runtime validation passes and explicit user clearance is granted.

---

## 3. Execution and Promotion

| Area | Current status | Next owner |
|---|---|---|
| Foundation (legacy 0–5) | Completed implementation history separated; 29 explicit carryovers remain | Foundation rows below; integrated performance remains Phase 8 |
| Phase 6 OpenXR | Receipt/admission/child-lifetime repairs implemented; replacement recovery defects and clean runtime matrix open | [XR tasks](vulkan-xr-and-advanced-rendering-todo.md#phase-6) |
| Phase 7 Advanced | Native mono output and substantial shader/graph plumbing implemented; temporal acceptance, GI/decals, and profile/visual acceptance open | [Advanced tasks](vulkan-xr-and-advanced-rendering-todo.md#phase-7) |
| Former 7R | Merged by subsystem; no independent duplicate checklist | [Legacy requirement map](vulkan-xr-and-advanced-rendering-todo.md#legacy-requirement-map) |
| Phase 8 | Frozen-revision promotion; not passed | Required matrix and thresholds below |
| Phase 9 | Test clearance recurs per validated slice; production/deletion await their gates | Phase 9 below |

Complete one bounded implementation/validation slice at a time. Work may run
in parallel when ownership is independent; promotion always uses one frozen
integrated revision. A September 4 352-frame Monado SPS result contained VUIDs
and is not clean acceptance. Advanced shader compilation is now passing;
calling the native stages “unexecuted” or the 128-slot classifier “32-slot” is
obsolete. The active checklist records these distinctions explicitly.

<a id="foundation-carryovers"></a>
### Foundation carryovers from Phases 0–5

Completed implementation and dated evidence moved to the
[foundation completion record](../../progress/rendering/vulkan-phases-0-5-completed.md).
The following **29 open requirements** are preserved verbatim apart from IDs.
They were not completed by the documentation move. Their original acceptance
wording remains binding; split a compound row into independently provable
children before starting it. Phase 8 still owns integrated promotion even when
a narrower foundation check passes.

### Phase 0 - In-Flight Checkpoint & Present-Now Live Revalidation

Completed record: [Phase 0](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-0) — 8 previously checked items.

<a id="f0-01"></a>
- [ ] **F0-01** — Define presentation freshness from accepted frame-data/resource
  generations plus a compatible new-or-reused command artifact generation.
  `PresentNow` alone must not force native scene re-encoding when the artifact
  remains legal and exact completion protects its frame slot.

<a id="f0-02"></a>
- [ ] **F0-02** — Give every authored frame operation one explicit attempt/accepted-plan
  transaction. Retry, rejection, supersession, and terminal paths must transfer,
  settle, or discard each operation exactly once.

<a id="f0-03"></a>
- [ ] **F0-03** — Reproduce 344 pre-drain readiness retries and the 8,193-of-8,192 overflow
  shape; prove queued authoring work remains bounded and the eventual accepted
  plan contains only its owning frame transaction.

<a id="f0-04"></a>
- [ ] **F0-04** — Classify a required preparation ticket that becomes stale during work as
  `RetryFrame` or `Superseded` unless an independently terminal device/resource
  condition exists; never latch `RendererTerminal` from a normal generation
  race.

<a id="f0-05"></a>
- [ ] **F0-05** — Settle, clear, or explicitly defer one-shot query/callback requests when no
  submitted planner generation exists, including resize while rendering is
  paused; ordinary absence must not create an exception loop.

<a id="f0-06"></a>
- [ ] **F0-06** — Mutate camera and scene state while preparation is blocked; prove the
  accepted epoch remains immutable and exactly one captured epoch is submitted.

<a id="f0-07"></a>
- [ ] **F0-07** — Reproduce the observed 221-request and 836-request shapes, then naturally
  exceed one declared main-scene lane and verify a single bounded
  `FramePlanCapacityExceeded` record with configured, required, accepted, and
  rejected counts.

<a id="f0-08"></a>
- [ ] **F0-08** — Exercise exact OpenXR deadline/fallback behavior on Monado and one hardware
  runtime; desktop evidence does not close the XR contract.

<a id="f0-09"></a>
- [ ] **F0-09** — Diagnose the RenderDoc 1.44 no-present launch and capture a settled Sponza
  frame with verified bindings and draw order.

### Phase 1 - Baseline Characterization, Telemetry Taxonomy, & Deliberate Pacing

Completed record: [Phase 1](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-1) — 7 previously checked items.

<a id="f1-01"></a>
- [ ] **F1-01** — Capture matched static and moving desktop baselines for `CpuDirect`,
  `GpuIndirectZeroReadback`, and `GpuMeshletZeroReadback`; keep OpenXR baselines
  separate.

<a id="f1-02"></a>
- [ ] **F1-02** — Capture separate Streamline/DLSS frame-generation promotion evidence.

<a id="f1-03"></a>
- [ ] **F1-03** — Prove exhaustive attribution for every compute, transfer, submit, present,
  worker, and external-runtime interval above 0.1 ms.

<a id="f1-04"></a>
- [ ] **F1-04** — Attribute at least 99% of detailed frame-root wall time, identify every gap
  of at least 50 microseconds, and measure observer overhead.

<a id="f1-05"></a>
- [ ] **F1-05** — Run the frame-slot, Mailbox, FIFO, reduced-resolution, compiler, streaming,
  secondary-window, and editor-diagnostic A/B matrix.

<a id="f1-06"></a>
- [ ] **F1-06** — Prove every recurring slot wait has an exact producer/timeline owner and
  that uncapped GPU-headroom slot-wait p95 is approximately zero.

<a id="f1-07"></a>
- [ ] **F1-07** — Report `PrimaryFrameDataManifest`, `PrimaryPrewarm`,
  `PrimaryEncodingSetup`, `PrimaryOperationLoop`, `PrimaryFinalization`, and
  `PrimaryEndCommandBuffer` separately at p50/p95/p99/max with allocation,
  operation-count, lane, and frame/output identity.

<a id="f1-08"></a>
- [ ] **F1-08** — Separate secondary encoder wall time, summed worker execution, worker wait,
  merge, and command-buffer-end dependency publication. Do not charge frontend
  preparation or submission validation to native encoding.

<a id="f1-09"></a>
- [ ] **F1-09** — Count live `VkMeshRenderer.RecordDraw` calls, immutable prepared-draw
  encoder calls, dependency-track attempts, unique native dependencies,
  command-bind-state lookups/lock acquisitions, tracking-batch lock
  acquisitions, descriptor-heap bind attempts/native binds/skips, and native
  Vulkan commands by type.

<a id="f1-10"></a>
- [ ] **F1-10** — Publish `DependencyTrackAttempts / UniqueRecordingDependencies`. Zero
  allocation and no individual lock wait above 0.1 ms do not close repeated
  per-command bookkeeping.

<a id="f1-11"></a>
- [ ] **F1-11** — Execute one matched Release isolation ladder with identical scene, camera,
  render graph, output, validation state, and warm caches:
  1. live draw path plus current full tracking;
  2. immutable prepared draw state plus current full tracking;
  3. immutable prepared draw state plus sealed/bulk recording dependencies;
  4. CPU-built indirect ranges over the same resident bins; and
  5. GPU-built equivalent indirect/count ranges.

<a id="f1-12"></a>
- [ ] **F1-12** — Run the ladder on small, medium, dense, material-diverse, and moving-camera
  cohorts. The A→B delta owns live renderer/material preparation, B→C owns
  recording bookkeeping, C→D owns per-draw Vulkan emission, and D→E owns the
  CPU/GPU visibility-producer crossover.

<a id="f1-13"></a>
- [ ] **F1-13** — Retain an explicitly diagnostic raw-pinned or sampled-full-validation rung
  only long enough to quantify the safety layer. It may not become a production
  lifetime bypass.

<a id="f1-14"></a>
- [ ] **F1-14** — Record conclusions as measured findings or source-audit hypotheses. The
  2026-09-01 incident supports attribution and transaction ownership but does
  not by itself prove which native-encoding change will win.

### Phase 2 - Submission Fast Path & Granular Invalidation

Completed record: [Phase 2](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-2) — 8 previously checked items.

No subsection carryovers. The master Phase 8 integrated gates still apply.

### Phase 3 - Canonical GPUScene Residency, Stable Bins, & 5 Strategy Lanes

Completed record: [Phase 3](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-3) — 14 previously checked items.

<a id="f3-01"></a>
- [ ] **F3-01** — Promote CPU-indirect parity (Multi-Draw Indirect built on CPU) from diagnostic scaffolding to a production option for compatible opaque and masked bins only after the Phase 1 isolation ladder proves its crossover against prepared direct draws.

<a id="f3-02"></a>
- [ ] **F3-02** — Make GPU indirect and meshlet lanes populate the corresponding canonical bin/range streams without rebuilding the original per-draw CPU frontend.

<a id="f3-03"></a>
- [ ] **F3-03** — Select prepared direct, CPU-built MDI, GPU indirect-count, or mesh-task realization through measured candidate/bin/material/culling crossover policy; no strategy is required to win every scene size.

### Phase 4 - Concurrency Closure, Multi-Lane Render Work Pool, & Native Encoding

Completed record: [Phase 4](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-4) — 42 previously checked items.

No subsection carryovers. The master Phase 8 integrated gates still apply.

### Phase 5 - Render Graph Simplification, Streaming, & Tail Latency Bounds

Completed record: [Phase 5](../../progress/rendering/vulkan-phases-0-5-completed.md#phase-5) — 42 previously checked items.

<a id="f5-01"></a>
- [ ] **F5-01** — Accept the implemented release-continuity path: keep the last authored
  scene generation alive, replay it beneath current ImGui/FPS overlays, and
  complete the handoff only after an authored successor presents. Structural
  leases now pin the exact image/view/sampler generation, but the final live
  cross-pipeline cohort has not passed.

<a id="f5-02"></a>
- [ ] **F5-02** — Accept the implemented Advanced-path corrections: the indirect draw/range
  capacity is now 65,536 and every overlay/recovery presentation has either an
  authored replay or a defined clear base. Confirm that held resize no longer
  accumulates ImGui or dynamic-text history.

<a id="f5-03"></a>
- [ ] **F5-03** — Pass actual held-drag and release acceptance in Default, Advanced, and
  Debug Opaque, including an Advanced-to-Debug-Opaque live asset replacement.

### Phase 6/7 Wrap-up Checkpoint (2026-09-04)

Historical source/evidence: [September 4 wrap-up](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-04-wrap-up-and-resume-boundary).
The work was subsequently committed in `8b104bf7a`. Current task status lives
in the active checklist rather than this historical checkpoint.

### Phase 6 - OpenXR Asynchronous Decoupling & Lifecycle Hardening

Moved to the single [Phase 6 execution checklist](vulkan-xr-and-advanced-rendering-todo.md#phase-6).

### Phase 7 - Advanced Render Pipeline Modernization (Phases 06 Through 10)

Moved to the single [Phase 7 execution checklist](vulkan-xr-and-advanced-rendering-todo.md#phase-7).

### Phase 7R - Phase 6/7 Correctness Remediation & Runtime Completion

Merged into the active Phase 6/7 task IDs. The [requirement map](vulkan-xr-and-advanced-rendering-todo.md#legacy-requirement-map)
identifies each former subsection's owner; no remediation requirement is
waived and no duplicate phase-completion checkbox remains.

---

### Recovered acceptance contracts and specialized child ownership

These requirements were insufficiently explicit in the earlier consolidation.
The original 29 F carryovers remain unchanged. `RC-A` rows audit existing code
before assigning implementation work; `RC-V` rows require observed evidence.
Use the same dated revision/evidence/result closure rule as the XR/Advanced
checklist. An old unchecked implementation row is not proof that code is missing.

- [ ] **RC-V01** — Preempt background preparation with foreground readiness, then resume it. Close with earlier progress retained and no starvation or duplicate work. Source: Present-Now Phase 5.
- [ ] **RC-V02** — Saturate shadow preparation while authoring main-scene and terminal-composition work. Close with bounded shadow deferral and continued scene/composition progress. Source: Present-Now Phase 5.
- [ ] **RC-A01** — Audit latched terminal handling for genuine Present-Now failures. Close with one detailed transition containing failing resource/native-operation identity, stopped retries, and suppressed generic per-frame repeats; file any missing behavior as implementation work. Source: Present-Now Phase 6.
- [ ] **RC-V03** — Exercise a genuine terminal preparation/native failure after `RC-A01`. Close with reproducible identity, one terminal transition, and no retry/log storm. Automated injection remains subject to Phase 9.1 clearance.
- [ ] **RC-A02** — Inventory generic jobs, BVH work, physics preparation, capture preparation, and render-thread-affine increments. Close with owning worker/admission budget for each and specific tasks for any remaining unbounded render-thread work. Source: core-hardening section 7.
- [ ] **RC-A03** — Audit on-demand submit/descriptor dumps and context provenance. Close with last-successful submission state, planner owner, display/internal extents, registry/resource generations, physical allocation, and every attempted cross-context substitution represented without steady-state formatting cost. Source: core-hardening section 8.
- [ ] **RC-V04** — Capture those diagnostics during incompatible context/extent reuse. Close with a structured frame rejection and exact source/allocation provenance, rather than only a throttled warning. Source: core-hardening section 8.
- [ ] **RC-A04** — Produce the final reproducible facade, lifecycle-spine, full Vulkan source/line, directory-depth, file/method-size, dependency-direction, and single-authority inventory against the target architecture's budgets. Close with each budget's result and explicit remediation tasks for failures. Source: core-hardening section 14.4; do not replace its budgets with checkbox counts.
- [ ] **RC-A05** — Audit retained unsafe native/mapped-memory owners for lifetime, bounds, alignment, and concurrency; record why an equivalent safe span path is insufficient where unsafe code remains. Close with named owners and evidence requirements for every retained path. Source: core-hardening section 14.4.
- [ ] **RC-V05** — Measure hot-stream layouts and bytes touched against active work, including compatibility extraction/conversion passes. Close with no unconsumed conversion path and evidence that layout changes preserve or reduce owners, files, allocations, descriptor bindings, and lifetime transitions. Source: core-hardening section 14.4.
- [ ] **RC-V06** — Complete the resident-stream allocation matrix using warmed storage/pool high-water marks and measured managed-byte deltas. Close with its named matrix artifact; a generic zero-allocation assertion is insufficient. Source: resident-stream Phase 0.
- [ ] **RC-V07** — Exercise primary/secondary pending-state, command-pool external synchronization, query inheritance, and dynamic-rendering/retained legacy render-scope inheritance across desktop, OpenXR, resize, reload, churn, and shutdown. Close with completion-owned artifact retirement and clean validation for each applicable matrix entry. Source: command-recording acceptance; unsupported required entries remain blockers.
- [ ] **RC-V08** — Measure the approximately 647-draw Release stable-static cohort. Close at p95 with frontend binding/package <=0.15 ms, frame/view/pass publication <=0.15 ms, unchanged material/object publication <=0.05 ms, descriptor reuse <=0.10 ms, artifact reuse <=0.15 ms, and total Vulkan preparation/record/submit <=1.00 ms, excluding separately reported OS/GPU waits. Record full hardware/profile/count/byte/fallback metadata. Source: command-recording acceptance.
- [ ] **RC-V09** — Measure the declared Release moving-object cohort. Close with only dirty object ranges updated and total preparation/record/submit <=1.50 ms p95 excluding separately reported waits. Source: command-recording acceptance.
- [ ] **RC-V10** — Validate every retained unsafe path inventoried by `RC-A05` under its lifetime, bounds, alignment, and concurrency stress conditions. Close with end-to-end performance evidence justifying retention; compilation or ordinary Vulkan validation alone cannot close this gate.

F3-01/F3-03 additionally require the resident-stream stable-bin manifest and
visible-template/draw-range equivalence evidence for CPU-built indirect versus
GPU indirect, with the latter remaining zero-readback. The workstream-local
5.00 ms p95 CPU-direct desktop gate is retained by the optimization roadmap;
it is distinct from the master refresh-promotion levels.

Focused child documents retain detailed, independently scoped work below.
Do not copy their boxes into the XR/Advanced list or certify their completion
from a passing Advanced frame. The coverage audit lists every reviewed file.

| Active child owner | Requirements retained beyond this master's broad gates |
|---|---|
| [Occlusion policy](vulkan-core-hardening-and-device-loss-todo.md#9-make-occlusion-modes-bounded-and-effective), [GPU culling](gpu/gpu-driven-occlusion-culling-architecture-todo.md), [CPU queries](cpu-async-query-camera-motion-todo.md), [software occlusion](masked-software-occlusion-culling-todo.md) | Comparative production/opt-in/diagnostic/retired decisions; camera/hierarchy/eye correctness; two-phase Hi-Z and bounded overflow |
| [Render-query upgrade](vulkan-render-query-system-upgrade-todo.md) | Implemented query families; remaining timestamp, exact-count, specialized-provider, and physical-eye runtime acceptance |
| [Pipeline resource lifecycle](render-pipeline-resource-lifecycle-todo.md) | Cross-pipeline OpenGL/Vulkan atomic logical/physical commit, imported ownership, failed/superseded allocation cleanup, and rapid-resize hardening |
| [CPU direct](optimization/cpu-direct-fast-path-todo.md), [material ladder](optimization/material-table-and-texture-binding-ladder-todo.md), [compact submission](optimization/compact-zero-readback-rendering-todo.md), [GPU roadmap](gpu/production-rendering-pipeline-roadmap.md) | Backend-neutral rings, binding tiers, dirty domains, residency/LOD, and GPU culling/streaming details |
| [Default GPU hotspots](optimization/default-pipeline-gpu-hotspots-todo.md), [physics diagnostics](physics-debug-visualization-performance-todo.md) | Per-effect/quality GPU baselines and physics extraction/upload/draw-count budgets |
| [Editor observer cost](optimization/editor-profiler-ui-render-cost-todo.md), [component profiling](optimization/vulkan-headless-mcp-component-profiling-todo.md) | Visible/hidden observer overhead and presentation-independent measurement infrastructure |
| [VR performance](optimization/vr-rendering-performance-contract-todo.md), [Monado/hardware follow-ups](vr/openxr-monado-ci-hardware-followups-todo.md), [SteamVR/OpenVR parity](vr/openxr-steamvr-openvr-parity-todo.md) | Runtime-specific 72/90/120 Hz contracts, CI ownership, input/device/audio parity, and hardware acceptance beyond Vulkan submission |
| [Deferred/probe fixes](vulkan-deferred-and-probe-gi-fixes-todo.md), [atmosphere](atmospheric-scattering-component-todo.md), [resolved source optimizer](resolved-shader-source-optimization-todo.md) | Original-pipeline probe/deferred regression proof, atmosphere platform/quality work, and backend-neutral shader-source cleanup |

### Retained resident acceptance details

RC-V06, the Phase 8 hardware gates, and F3 strategy parity consume this matrix.
These reference criteria preserve the removed resident tracker’s exact scope;
update the owning task boxes, not a second legacy checklist. Historical incident
claims require current verification before assigning implementation work.

### Resident evidence matrix retained for RC-V06

Checkpoint (2026-08-17): original-laptop measurement, third-laptop checkpoint,
and source-audit results are in
[the Phase 0 investigation](../../investigations/rendering/vulkan-resident-draw-stream-phase0-2026-08-17.md).
The third-laptop run is recorded separately because its commit, workload
identity, power policy, and observed workload shape do not match the accepted
original-laptop baseline. Phase 0 remains open for a same-commit/same-workload
machine matrix including the desktop, elevated scheduler trace,
three-view/RenderDoc evidence, unavailable meshlet paths, and removal of the
instrumented path's synchronous one-shot fence wait.

- Required evidence: Capture matched Release dense-Sponza baselines on the laptop and
  7950X3D/RTX 3090 desktop: same commit, camera transform, window/internal
  resolution, render settings, present mode, validation state, warmup, and
  sample duration.
- Required evidence: Capture the same scene under `CpuDirect`, `GpuIndirectZeroReadback`,
  `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, and
  `GpuMeshletInstrumented` wherever capabilities permit. Record requested and
  resolved strategies rather than treating a visible downgrade as the named
  path.
- Required evidence: Capture CPU sampled traces including every engine-owned thread, .NET
  ThreadPool counters, context switches, core migration, QoS, and per-thread CPU
  time.
- Recorded implementation: Report every current worker pool/thread and whether it overlaps the
  render critical path.
- Required evidence: Freeze draw/pass/material/shadow/UI equivalence signatures and current
  screenshots from at least three camera positions.
- Recorded implementation: Add counters that distinguish raw-request drain, cohort match, hole
  materialization, binding validation, resource-use lowering, planning,
  indirect construction, native encoding, and waits.
- Required evidence: Inventory every field/allocator/map/SoA stream in legacy `GPUScene` and
  `HybridRenderingManager` against `AdvancedSharedGpuSceneDatabase`; designate
  the advanced table that replaces it or document the exact missing record to
  add. No duplicate registry work begins before this map is reviewed.
- Recorded implementation: Trace every GPU buffer map/read helper, fence/query retrieval, CPU
  fallback, delayed stats readback, and diagnostic command submission reachable
  from each strategy. Freeze zero-readback and instrumented source-contract
  signatures.

Exit gate:

- Required evidence: The desktop/laptop difference is separated into CPU work, scheduler/QoS,
  GPU execution, and presentation rather than inferred from FPS alone.
- Required evidence: Every planned O(draw) stage has a measured baseline count and time.
- Required evidence: The canonical database migration map has exactly one final owner for every
  scene/material identity and GPU upload stream.
- Required evidence: Both zero-readback modes demonstrate zero readback bytes/mappings/waits
  and CPU fallback; both instrumented modes report bounded expected diagnostic
  activity without a current-frame wait.

### Target hardware gates

Run at minimum:

1. the current Intel Core Ultra 9 185H / RTX 4070 Laptop system;
2. the Ryzen 9 7950X3D / RTX 3090 desktop;
3. NVIDIA and AMD Vulkan drivers where hardware is available; and
4. one integrated/tile-based device when available before declaring a portable
   descriptor/secondary policy.

For the matched dense Sponza profile:

- laptop Release whole-frame CPU p50 must be at or below 8.33 ms and p95 at or
  below 10 ms when the GPU/present path is not the limiter;
- desktop Release whole-frame CPU p50 must be at or below 5 ms and p95 at or
  below 6 ms under the same qualification;
- resident frame-operation preparation p50 must be at or below 2 ms on both
  named systems;
- GPU p95 may not regress more than 5% versus the equivalent direct-draw
  baseline without a separately accepted image-quality or scalability gain;
  and
- no frame-time claim is accepted without the stage timings, counts, CPU/GPU
  timeline, and present-mode evidence that explain it.

If hardware cannot meet an absolute budget because another measured stage is
limiting, keep the structural gates mandatory and record the blocker rather
than weakening or misattributing the result.

### Worker-count sweep

For small, medium, large-dirty, stable, and moving-camera cohorts, capture
`0`, `1`, `2`, `4`, `8`, and auto render workers. On lower-core systems, omit
counts rejected by the topology budget. Repeat for CPU direct, both GPU
indirect modes, and both meshlet modes where supported; do not assume the
worker policy that wins CPU direct also wins a stable GPU-driven frame.

- Auto must remain within 5% of the best valid p50 and within 10% of the best
  valid p95 for both named machines after the policy is tuned.
- A large dirty cohort on a system with at least eight effective processors
  must show two or more overlapping native-record intervals and at least a 20%
  p50 improvement over forced inline before parallel recording is promoted.
- Small and stable cohorts may dispatch zero workers; they must not regress
  forced inline by more than 3% at p50 or 5% at p95.
- Report scheduler queue/wake/merge cost so moving work off the render thread
  cannot be mistaken for eliminating it.

### Phase 8 - High-Refresh Promotion Gates & Full Validation Matrix

**Goal:** Prove performance, cadence, lifetime, and visual parity across a comprehensive multi-machine and multi-scenario validation matrix on one frozen integrated implementation.

**Entry gate:** Close the applicable implementation/audit/validation tasks in the active Phase 6/7 checklist (including former 7R findings) and the required foundation carryovers before promotion. No scenario or threshold below is waived.

#### 8.1 Required Scenario Matrix
- [ ] **Desktop Performance-Promotion Scenarios:**
  - Static camera and scene.
  - Continuous camera motion through dense Sponza.
  - Object transform and animation updates.
  - 1-value material mutation & 1-texture replacement.
  - Texture streaming promotion/demotion bursts.
  - Geometry reload and generation-safe slot reuse.
  - Shader hot reload outside measured interval followed by warm recovery.
  - Directional shadow movement and settle.
  - Reflection-probe/environment maintenance and settle.
  - Editor UI active vs. hidden; secondary ImGui platform windows.
  - 5 submission strategies (`CpuDirect`, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, `GpuMeshletInstrumented`).
  - Presentation profiles (`Stable` FIFO, `LowLatency` Mailbox limiter, `Uncapped`).
  - Dynamic rendering vs. legacy render-pass realization where both paths remain supported.
- [ ] **Correctness, Lifetime, & Feature-Parity Scenarios** (pass their own gates; do not apply a present-interval target to non-presenting or fault/recovery rows):
  - Resize, maximize, minimize, restore, internal/output resolution, HDR/format/MSAA changes, and repeated recreation.
  - Presentationless/offscreen, mirror, portal, probe, capture, transparent, UI, callback, query-bracket, and external-target outputs.
  - Pause/resume, failed acquire/submit/present, device loss/recovery, and repeated start/stop/shutdown.
  - Diagnostic ring wrap/full/late/generation-mismatch completion and device loss while diagnostic slots are pending.
- [ ] **OpenXR Performance & Lifecycle Scenarios:**
  - Static headset pose & continuous head motion.
  - Desktop + OpenXR simultaneous rendering.
  - In-flight image pressure and swapchain recreation.
  - Session stop and loss recovery on Monado and at least one hardware runtime.

#### 8.2 Correctness & Structural Gates
- [ ] Zero Vulkan validation errors / VUIDs in Standard and Synchronization validation.
- [ ] Zero device loss, stale descriptor, use-after-free, or command pool reuse errors.
- [ ] Camera-separated screenshots prove current camera-dependent output rather than a stale cached image.
- [ ] Strategy parity preserves draw order, visibility, materials, shadows, transparency, postprocess, UI, and requested/resolved strategy identity.
- [ ] One material scalar, one texture/sampler replacement, one geometry replacement, and one shader reload invalidate only exact dependents; add/remove/re-add remains generation safe, one shadow-cascade update leaves unrelated entries warm, camera/object transforms cause zero structural/bin invalidation, and broad resident fallback remains zero.
- [ ] Normal production captures contain no current-frame readback, mapping, host completion wait, or `vkDeviceWaitIdle`.
- [ ] Zero managed hot-path heap allocations after warmup.
- [ ] Zero per-draw material reconstruction, descriptor validation, or command-signature rebuilding.
- [ ] Warm production native encoding executes zero live `VkMeshRenderer.RecordDraw` calls, zero `_recordDrawSync` acquisitions, and zero shader/material reflection, pipeline creation, descriptor allocation/update, renderer prewarm, or render callback execution.
- [ ] Primary, secondary, inline, worker, CPU-direct, indirect, and ordered-exception encoders consume immutable backend-ready records and one sealed recording manifest.
- [ ] Steady command encoding performs zero global command-buffer bind-state discovery and zero shared bind-state lock acquisitions. Recording-local state is owned directly by its lane/frame-slot context.
- [ ] Recording dependency work scales with unique sealed manifest entries rather than raw pipeline/descriptor/buffer bind attempts or `vkCmd*` count; publish attempts, unique entries, and the ratio.
- [ ] Descriptor table or heap native binds scale with command buffers or compatible scopes, not draws, unless a separately accepted device-specific tier proves otherwise.
- [ ] Warm `PrimaryPrewarm` reports no visits or work outside explicitly classified mutation, streaming, cold-recovery, or diagnostic frames.
- [ ] Warm dense-Sponza `PrimaryCommandEncoding` p95 is at most 1.0 ms on the named desktop and 1.5 ms on the named laptop. Revise only from raw driver-attributed evidence, not by moving frontend preparation outside the counter.
- [ ] Complete the five-rung recording isolation ladder and retain raw profiles, native command counts, tracking counters, output parity, and before/after critical paths.
- [ ] Fresh accepted frame data can execute through a compatible completed reusable artifact without forcing native scene re-encoding; a new submit serial and exact generation provenance still define `PresentedNew`.
- [ ] CPU-direct prepared encoding and any promoted CPU-built indirect path consume the same resident template/bin/material backend as GPU indirect and meshlet strategies.
- [ ] Stable frame preparation scales with dirty ranges, not visible draw count.
- [ ] Sealed unchanged submission CPU p95 $<0.25$ ms.
- [ ] Slot-wait p95 $\approx 0$ ms in uncapped GPU-headroom tests.
- [ ] Run-to-run p95 spread $\le 7.5\%$ (target $\le 5\%$).
- [ ] Strict zero-readback strategies report `GpuReadbackBytes == 0`, 0 buffer maps, and 0 CPU fallbacks.
- [ ] Strict zero-readback strategies also report zero query-result retrievals, diagnostic-copy submissions, and readback-caused waits; external profiling does not alter in-engine submission.
- [ ] Each instrumented strategy matches its zero-readback pair's visual/draw/task identity and reports bounded source-tagged results without current-frame waits or feedback into production decisions.
- [ ] Saturating the diagnostic ring drops diagnostics only; diagnostics disabled creates zero reservations, copies/queries, decoder tasks, diagnostic variants, or measurable dormant-path cost.
- [ ] Hot-switch all requested strategies with prior slots both in flight and retired; exact generation leases preserve old artifacts while canonical handles and unrelated artifacts remain stable.
- [ ] Stable dense Sponza reports zero template rebuilds, rebinning, descriptor writes, command records, managed allocation, and legacy holes; camera motion performs view/culling publication only.
- [ ] Tenfold resident instances with unchanged bins do not produce tenfold CPU preparation; indirect/meshlet native draw commands scale with compatible bins/ranges rather than object count.
- [ ] GPU p95 does not regress more than 5% versus equivalent direct draw without a separately accepted image-quality or scalability gain.
- [ ] Every-N-frame cadence spike is absent or has an explicit measured and accepted cause.

#### 8.3 Promotion Level Gates

##### Level A - Stable 100 Hz
- [ ] Whole-frame p99 $< 10.000$ ms across all required desktop performance-promotion scenarios.
- [ ] Zero recurring unexplained $>10$ ms spikes.
- [ ] Correctness and lifecycle gates pass.

##### Level B - Stable 120 Hz (Promotion Gate)
- [ ] Whole-frame p99 $< 8.333$ ms (engineering target p99 $\le 7.5$ ms).
- [ ] Actual present intervals meet the 120 Hz profile.
- [ ] Laptop Release whole-frame CPU p50 $\le 8.33$ ms, p95 $\le 10.0$ ms when GPU/presentation is not the limiter.
- [ ] Desktop Release whole-frame CPU p50 $\le 5.0$ ms, p95 $\le 6.0$ ms under the same qualification.
- [ ] Resident frame-op prep p50 $\le 2.0$ ms on both systems.
- [ ] All desktop performance-promotion rows and separate correctness/lifetime/feature-parity gates pass; classify unavailable OpenXR hardware/runtime rows separately rather than weakening desktop promotion.

##### Level C - Stable 144 Hz (Stretch Gate)
- [ ] Whole-frame p99 $< 6.944$ ms (engineering target p99 $\le 6.25$ ms).
- [ ] GPU and CPU retain measurable headroom without disabling features.

#### 8.4 Hardware, Worker, & Evidence Gates
- [ ] Run the named Core Ultra 9 185H / RTX 4070 Laptop and Ryzen 9 7950X3D / RTX 3090 systems; add AMD Vulkan and one integrated/tile-based device where available before declaring portable descriptor/secondary policy.
- [ ] Benchmark stable descriptor sets/dynamic offsets against descriptor indexing on NVIDIA, AMD, and an available integrated GPU; prototype advertised descriptor-heap/device-generated-command tiers only when measured CPU, GPU, and tooling results beat the portable resident/MDI path.
- [ ] Sweep `0`, `1`, `2`, `4`, `8`, and auto render workers for small, medium, large-dirty, stable, and moving-camera cohorts across all supported strategies; omit counts rejected by topology.
- [ ] Tune auto within 5% of best valid p50 and 10% of best valid p95; require at least two overlapping native-record intervals and 20% p50 improvement over inline before promoting parallel recording for large dirty cohorts.
- [ ] Require small/stable cohorts to avoid regressing inline by more than 3% p50 or 5% p95; report queue, wake, execution, and merge cost so shifted CPU work is not called eliminated work.
- [ ] Freeze the accepted revision/manifests and publish raw reports, summaries, profiler/capture evidence, screenshots, validation logs, unsupported rows, and named follow-ups for every remaining tail source.

---

### Phase 9 - Phase-Local Test Clearance, Legacy Deletion, & Closeout

**Goal:** Apply explicit test clearance after each slice's live validation, execute the cleared automated/fault-injection work, then cut over production rendering, delete legacy code, and close out the integrated program.

#### 9.1 Explicit Test Clearance Gate
- [ ] For each feature/regression slice, complete its narrowest relevant live/runtime validation before beginning its test work.
- [ ] Request explicit user clearance for that slice before adding or modifying automated tests, per repository policy; clearance for one slice does not authorize unrelated test work.
- [ ] Before final cutover/deletion, complete the integrated live/runtime and cleared automated validation gates across Phases 0–8.

#### 9.2 Automated Test & Fault-Injection Matrix (Post-Clearance)
- [ ] Add contract tests proving `PresentNow` results cannot be `Deferred` or report `PresentedNew` without matching submit serials and accepted data/resource generations; compatible artifact reuse must not claim stale output.
- [ ] Exercise scheduling capacities 1, 8, 32, and production values.
- [ ] Reproduce the observed 221-request and 836-request visibility shapes and exercise bounded accepted-frame/UI/main-scene/shadow lane overflows.
- [ ] Reproduce repeated pre-drain readiness retries, including the 344-attempt and 8,193-operation shape; prove operations are transferred or settled exactly once and cannot accumulate into a later accepted frame.
- [ ] Inject a required upload generation change during preparation; require retry/supersession rather than renderer-terminal state.
- [ ] Request a one-shot depth/query readback while no submitted planner generation exists and across resize; require explicit defer/clear settlement with no exception loop.
- [ ] After live validation and explicit clearance, add source/contract tests proving the primary and inline mesh encoders use immutable prepared state rather than live `VkMeshRenderer.RecordDraw`.
- [ ] Validate sealed/bulk recording manifests against the sampled full tracker, including retirement races, image-access deltas, descriptor expansion, render-pass replacement, and secondary execution.
- [ ] Inject slow pipeline compiles, chunked large uploads, staging overflow, shader compile failures, descriptor exhaustion, frame arena overflow, host/device OOM, device loss, and timeline stalls.
- [ ] Prove uploads larger than the staging ring complete by chunking and that foreground reserve publishes the requested number of distinct allocation generations.
- [ ] Mutate camera, transforms, materials, and lights during blocked preparation; verify exactly one captured epoch is submitted.
- [ ] Saturate background uploads, compilation, and shadows; verify zero foreground starvation.
- [ ] Exercise failed acquire/submit/present, pause/resume, repeated start/stop/shutdown, diagnostic-ring wrap/full/late/generation-mismatch completion, and device loss with pending diagnostic slots.
- [ ] Run long warm soaks verifying zero managed allocations and bounded pool high-water marks.

#### 9.3 Production Cutover
- [x] Make `AdvancedRenderPipeline` the configured source default for new desktop camera assets while retaining camera-source authority and RVC-owned OpenXR eye outputs. This early source cutover is intentionally separate from shaded-output production readiness.
- [ ] After the affected gates pass, mark the desktop advanced source production-ready, extend the default to applicable offscreen profiles, and promote the RVC-owned OpenXR eye path only after its matching XR gates pass.
- [ ] Update engine settings, schemas, launch profiles, and unit-testing-world configurations.
- [ ] Remove development selectors, `DefaultRenderPipeline2`, and temporary environment variables.

#### 9.4 Legacy Architecture Deletion
- [ ] Delete `VulkanPreparedMeshOperationCohort` and `VulkanPreparedMeshIngress`.
- [ ] Delete duplicate `GPUScene` / `HybridRenderingManager` arrays and ID maps.
- [x] Delete the `RuntimeEngine.Jobs` compatibility facade.
- [ ] Delete separate command-chain workers and dedicated OpenXR eye threads after their Phase 4 render-domain migration.
- [ ] Delete the live object-oriented Vulkan CPU-direct encoding path after prepared direct/CPU-indirect parity and ordered-exception coverage pass.
- [ ] Delete per-command global bind-state/lifetime discovery after sealed recording-manifest parity, sampled validation, and retirement-race gates pass.
- [ ] Delete classic `DefaultRenderPipeline` (or rename to `LegacyDefaultRenderPipeline` with bounded removal gate if required by a named consumer).
- [ ] If a named required consumer temporarily blocks deletion, keep the renamed legacy path opt-in, record its owner/exact blocker/dated deletion gate, stop symmetric feature development, and keep this master active until deletion.
- [ ] Remove obsolete diagnostic aliases, duplicate telemetry, and transitional fallbacks.

#### 9.5 Documentation & Closeout
- [ ] Update `README.md`, runtime overview, rendering architecture, and MCP documentation.
- [ ] Create closeout record under `docs/work/progress/rendering/`.
- [ ] Archive superseded TODO documents and update links.

---

## 4. Configuration & Telemetry Contracts

### 4.1 Execution, Presentation, & Strategy Configuration

| Setting | Values | Default | Required Behavior |
|---|---|---|---|
| `PresentationProfile` | `Stable`, `LowLatency`, `Uncapped`, `FrameGeneration` | `Stable` | Selects pacing, queue depth, and limiter behavior. |
| `PresentationTargetHz` | `auto`, positive Hz | `auto` | Resolves the limiter/deadline from the active display or an explicit diagnostic target. |
| `MaxFramesAhead` | `1..2` | `1` for `LowLatency` | Bounds application queue depth; never silently increases frames in flight to hide waits. |
| `RenderWorkerThreadCount` | `-1` (auto), `0` (inline), `1..32` (fixed) | `0` until Phase 8 promotes measured auto policy | Background render workers, excluding the participating render thread; startup/restart scoped. |
| `RenderWorkerThreadCap` | `1..32` | `8` | Upper bound for auto worker selection. |
| `GeneralWorkerThreadCount` | `-1` (auto), `0` (inline), `1..32` (fixed) | `-1` | General domain worker count managed by `EngineWorkScheduler`; startup/restart scoped. |
| `GeneralWorkerThreadCap` | `1..32` | `16` | Upper bound for automatic general-domain selection. |
| `ReservedForegroundThreadCount` | `auto`, positive integer | `auto` | Reservations for render, collect-visible, update, window, audio. |
| `AllowCpuOversubscription` | `true`, `false` | `false` | Rejects configurations exceeding processor count when false. |
| `RenderWorkerQos` | `OsDefault`, `High` | `OsDefault` | Windows QoS policy for persistent background render workers; `High` remains diagnostic until measured, with no hard affinity and no production `Eco`. |
| `ForceMeshSubmissionStrategy` | `<auto>` (nullable), `CpuDirect`, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, `GpuMeshletInstrumented` | `<auto>` | Explicit strategy override through the existing resolver; `Auto` is not an `EMeshSubmissionStrategy` value. |
| `GpuDiagnosticReadbackCapacity` | bounded startup-only integer | existing generalized ring capacity | Preallocates instrumented slots; saturation drops diagnostics and never changes rendering. |

`RenderWorkerThreadCount`, caps, general-worker settings, oversubscription, and worker QoS are renderer-neutral `RenderExecutionSettings` and are startup/restart scoped. Presentation and strategy settings remain in their existing owners rather than being duplicated into that subtree. Expose effective settings through engine/project/user configuration; environment variables are launch-only diagnostic overrides. Startup must report requested/effective values, their source, processor/reservation budget, lane/thread IDs and QoS, queue capacities, and restart requirements; invalid values and oversubscription are never silently ignored. The existing strategy resolver remains the sole strategy authority—do not add scheduler-specific strategy toggles.

### 4.2 Canonical Invalidation Matrix

| Change | Data upload | Template / bin / recording effect |
|---|---|---|
| Camera/view motion | View stream only | No template rebuild, rebin, or rerecord; rerun selected culling only. |
| Object transform/bounds | Dirty object slots | No structural effect; advance previous transform independently. |
| Instance count within reserve | Instance/count range | Indirect data/count only. |
| Material scalar value | Dirty material slot | No template or bin change. |
| Texture/sampler replacement in stable slot | Resource-table slot | Acquire new lease before old release; no structural change. |
| Material layout/shader interface | Affected data | Rebuild/rebin/rerecord only dependent variants. |
| Fixed-function/render-option change | As required | New affected bin key and artifacts only. |
| Mesh content in stable allocation | Geometry range | No template/bin change; synchronize upload with reads. |
| Geometry layout/index type/buffer relocation | Geometry range | Rebuild/rebin/rerecord dependents; retire old lease after GPU completion. |
| Visibility/LOD result | Indirect data/count | No structural change. |
| Dense remap/compaction | Lookup/remap ranges | No structural change when logical identity/topology is unchanged. |
| Strategy change | Strategy/pass data | Change affected output variants only; preserve canonical scene handles/data. |
| Diagnostic request/ring full/late result | Diagnostic ranges or none | Never production rebin/retry; drop/count on saturation. |
| Unexpected GPU output overflow | None during the pass | Clamp safely, report asynchronously, and perform no same-frame rebuild/retry. |
| Swapchain target change | Frame/pass data | Rebuild only target-dependent scope; base templates remain resident. |
| OpenXR acquired image change | View/frame data | No template/rebin when compatible. |
| Pass compatibility/view-mask change | Pass data | Replace affected pass variant/bin/recording only. |
| Scene removal | Tombstone | Detach membership and recycle only after consumer/GPU acknowledgement. |
| Device loss | Republish all | O(1) table-generation invalidation followed by complete rebuild; no stale handle survives. |

### 4.3 `VulkanFrameTelemetry` Metric Schema

Aggregate metrics must be allocation-free and low-contention in performance builds. Detailed capture uses prewarmed bounded per-thread rings and stable cross-thread IDs; strings, export, and aggregation run off measured threads. Measure clean vs. aggregate vs. targeted-detailed observer overhead against the accepted baseline.

* **Frame Identity & Outcome:** engine/render/source/accepted frame IDs, accepted epoch, output/view/pass identity, frame slot, resource/output generations, span/parent/cross-thread link, thread/lane, work class, present policy/deadline/fallback, submit serial, presented-source ID, typed terminal stage/outcome, and first fault.
* **Foreground Plan & Failure:** `AcceptedFrameId`, `AcceptedEpoch`, `OutputGeneration`, `PresentWorkClass`, `ReadinessPolicy`, `FreshSubmitSerial`, `FrameOperationTransactionId`, authored/transferred/settled/discarded operation counts, queued-across-retry count, retry/supersession disposition, stale-ticket disposition, one-shot-consumer settlement, `FramePlanCapacityLane`, `FramePlanCapacityConfigured`, `FramePlanCapacityRequired`, `FramePlanCapacityAccepted`, `FramePlanCapacityRejected`, `ForegroundReserveRequested`, `ForegroundReserveDistinctSlices`, `TerminalStage`, `TerminalFailureKind`.
* **Device & Context:** device state/loss count, device-fault payload, TDR risk, memory budget, last successful submission breadcrumbs, context/display/internal extent/registry/resource-generation mismatches, and structured frame-rejection reason.
* **Presentation & Pacing:** `PresentationProfileRequested`, `PresentationProfileResolved`, `PresentMode`, `TargetRefreshHz`, `TargetFrameIntervalMs`, `ActualPresentIntervalMs`, `FramesAhead`, `LimiterSleepMs`, `LimiterSpinMs`, `AcquireMs`, `AcquireUnavailableCount`, `PresentQueueAdmissionMs`, `NativePresentMs`.
* **Frame-Slot & Completion:** `FrameSlotWaitMs`, `FrameSlotWaitQueue`, `FrameSlotWaitTargetValue`, `FrameSlotWaitCompletedValue`, `FrameSlotWaitAgeFrames`, `SwapchainImageWaitMs`, `CommandPoolReuseWaitMs`, `DescriptorArenaReuseWaitMs`.
* **Residency & Templates:** `ResidentDirectHits`, `ResidentColdMisses`, `ResidentReplacements`, `ResidentLocalInvalidations`, `ResidentBroadInvalidations`, `ResidentBroadInvalidationEntries`, `ResidentInvalidationReason`, `CanonicalDirtyOwnerCount`, `CanonicalDirtyRangeBytes`, `LegacyCompatibilityVisits`, canonical counts/capacities/duplicate bytes, topology/data deltas, template creates/rebuilds/generation mismatches/hash collisions/lease failures/evictions/retirements, and compatibility draws by reason.
* **Submission Gateway:** `SubmitImageContractMs`, `SubmitQueueOwnershipMs`, `SubmitLifetimePinsMs`, `SubmitStateGateWaitMs`, `SubmitQueueGateWaitMs`, `NativeQueueSubmitMs`, `SubmitLifetimePublishMs`, `SubmitImagePublishMs`, `SubmitDiagnosticPublishMs`, `SealedSubmissionHits`, `SealedSubmissionFallbacks`, `SealedSubmissionFallbackReason`.
* **Scheduler & Memory:** requested/resolved counts, active lanes/peak concurrency/thread IDs/QoS, built/queued/stolen/inline/lane-executed/cancelled items, `WorkerWakeCount`, empty wakes, queue-full fallback, faults/timeouts/quarantine, `WorkerQueueAgeMs`, `WorkerExecuteMs`, overlap/imbalance, `WorkerLockWaitMs`, merge cost, high-water marks, managed allocation by build/dispatch/execute/merge stage, `RenderThreadManagedAllocationBytes`, `GcPauseMs`, `PinnedObjectCount`, `OversubscriptionRejectedCount`.
* **Uploads & Streaming:** `UploadQueuedJobs`, `UploadOldestJobAgeMs`, `UploadStagingBytes`, `UploadStagingOverflowBytes`, `UploadCpuPrepMs`, `UploadStagingCopyMs`, `UploadVulkanAllocationMs`, `UploadTransferRecordMs`, `UploadTransferGpuMs`, `DescriptorPublicationMs`, `DescriptorPublicationItems`, `RetirementBacklogByClass`, `RetirementOldestAgeFrames`, deferred count, `RetirementDestroyedByClass`, `RetirementUncappedDrainCount`.
* **Native Command Encoding:** `PrimaryFrameDataManifestMs`, `PrimaryPrewarmMs`, `PrimaryEncodingSetupMs`, `PrimaryOperationLoopMs`, `PrimaryFinalizationMs`, `PrimaryEndCommandBufferMs`, secondary wall/summed-worker/wait/merge/end-publication time, `LiveMeshRecordDrawCalls`, `PreparedMeshEncodeCalls`, `DependencyTrackAttempts`, `UniqueRecordingDependencies`, dependency-attempt ratio, command-bind-state lookups/locks, tracking-batch locks, descriptor-heap bind attempts/native binds/skips, manifest entries, sampled-full-validation results, and native Vulkan command counts by type.
* **Bins, Recording, Render Graph, & GPU:** bin/dirty-bin/membership/manifest/resource counts; indirect buffer bytes/counts and MDI calls; primary/secondary records/reuses/resets/allocations; pipeline/descriptor/vertex/index/draw/submit API counts; `RenderGraphCacheHit`, `RenderGraphRecompiledPassCount`, `BarrierCount`, `BroadBarrierCount`, `OwnershipTransferCount`, `FullResolutionCopyBytes`, occlusion candidate/occluder/test/reject/age costs, `GpuPassP50P95P99`, `GpuFrameP50P95P99`.
* **Strategy & Diagnostics:** requested/resolved `MeshSubmissionStrategy`, capability/downgrade reason, per-strategy pass/draw/task counts, `GpuReadbackBytes`, `GpuReadbackBufferMaps`, query retrievals, `GpuReadbackWaits`, CPU fallback attempts, `DiagnosticRequestsAccepted`, copy bytes, `DiagnosticRingOccupancy`, completion latency/source generation, `DiagnosticDecodedResults`, generation-mismatch discards, `DiagnosticRingFullDrops`, decoder faults, diagnostic-only records/submits, and dormant overhead.
* **OpenXR Subsystem:** `OpenXrEyeSubmitMs`, eye completion-wait time, `OpenXrEyeInFlightCount`, tracker capacity/high-water, `OpenXrEyeOldestAgeFrames`, swapchain-image reuse age/release state, `OpenXrEyeForcedWaitMs`, `OpenXrEyeForcedWaitCount`, `OpenXrSwapchainReleaseDeferredCount`, `OpenXrRetiredGenerationCount`, `OpenXrMissedFrameCount`, `OpenXrLateFrameCount`, `OpenXrReprojectedFrameCount`.

---

## 5. Definition of Done

This master program is complete only when:

1. The desktop Vulkan renderer sustains **120 Hz (p99 $< 8.333$ ms, engineering target $\le 7.5$ ms)** across all required desktop performance-promotion scenarios on the target systems, while the separate correctness/lifetime matrix passes.
2. Actual presentation cadence matches the reported CPU/GPU timing story without hidden burst pacing.
3. Stable frames perform zero managed hot-path allocations, zero live per-draw material/descriptor reconstruction, and zero unnecessary scene-artifact re-recording; any required native recording is coarse and scales with passes, bins, dirty ranges, and ordered exceptions rather than visible objects.
4. Every authored frame operation settles inside one explicit frame transaction; retries cannot accumulate work into a later accepted plan, transient generation races do not latch renderer-terminal state, and one-shot consumers settle safely when no submitted generation exists.
5. Local mutations invalidate only exact reverse dependencies without whole-table resident clears.
6. Unchanged submission CPU p95 is below $0.25$ ms via `SealedSubmissionContract`.
7. Native encoding consumes immutable prepared records and prevalidated recording manifests through command-local state, with no per-command global bind-state discovery, shared bind-state lock, or lifetime-publication handshake; its p95 meets the Phase 8 budget.
8. All process execution domains are centralized, non-oversubscribed, and pooled.
9. OpenXR eye submission returns immediately, eliminating the 70–100 ms synchronous wait.
10. `AdvancedRenderPipeline` is the desktop and applicable-offscreen production default, with GPU material classification, native opaque shading, clustered lighting, and visibility-driven post/transparency. Production OpenXR eye output remains owned by `RvcRenderPipeline`, and that path is promoted only after its matching XR gates pass.
11. Standard and Synchronization Validation report zero errors/VUIDs, with no unresolved renderer warning or lifetime ambiguity accepted into closeout.
12. `GPUScene` mirrors, `VulkanPreparedMeshOperationCohort`, obsolete worker arrays, live object-oriented Vulkan CPU-direct encoding, per-command global recording discovery, `DefaultRenderPipeline2`, and the original default pipeline are deleted. A temporary opt-in `LegacyDefaultRenderPipeline` may unblock production cutover for one named consumer, but it keeps this master active until its dated deletion gate is complete.
13. The former Phase 7R review findings, now owned by the active Phase 6/7 checklist, are closed with executable capability, ownership, shader, and runtime evidence; production readiness and phase completion status reflect those results.
