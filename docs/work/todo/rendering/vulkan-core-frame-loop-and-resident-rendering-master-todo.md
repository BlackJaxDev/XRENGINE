# Vulkan Core Frame Loop, Resident Rendering, and High-Refresh Master TODO

Last Updated: 2026-08-28
Owner: Rendering / Vulkan / Frame Scheduling / Core Architecture  
Status: Active Master Implementation Tracker (Supersedes Present-Now Readiness, Core Hardening, Frame-Loop Stability, and Resident Draw Streams)  
Primary Target: Stable desktop rendering above 100 Hz, with a 120 Hz promotion gate and a 144 Hz stretch gate  
Secondary Target: Non-blocking OpenXR coexistence without transferring VR compositor stalls to the desktop render loop  
Scope: XRENGINE Vulkan frame loop, presentation, synchronization, resident rendering, resource publication, OpenXR lifecycle, and Advanced Render Pipeline cutover.

---

## 1. Executive Summary & Consolidated Architecture

This document is the **single authoritative implementation tracker** consolidating and superseding the execution tracks of:

1. [Vulkan Present-Now Frame Readiness TODO](vulkan-present-now-frame-readiness-todo.md) (foreground truthfulness and cold-entry liveness)
2. [Vulkan Core Hardening And Recording Code Changes TODO](vulkan-core-hardening-and-device-loss-todo.md) (core hardening, Forward+ simplification, tail latency, observability, and Advanced Render Pipeline phases 06–10)
3. [XRENGINE Vulkan Frame Loop Stability And High-Refresh Optimization TODO](optimization/xrengine-vulkan-frame-loop-stability-todo.md) (high-refresh pacing, wait attribution, sealed submission, and swapchain/OpenXR lifecycle)
4. [Vulkan Resident Draw Stream And Render Task Pool TODO](optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md) (canonical `AdvancedSharedGpuSceneDatabase` residency, stable bins, five submission strategies, asynchronous diagnostic sidecar, and centralized `EngineWorkScheduler`)

The source trackers remain provenance for implementation notes and historical evidence until Phase 9 archives them. They no longer own execution status. If a compressed master item appears less strict than a source invariant, rejection rule, or exit gate, the stricter source requirement applies and this master must be corrected before the source is archived.

Checkbox convention: an item is checked only when the source tracker records implementation or live evidence for the entire wording of the master item. Partial implementation is split into checked and unchecked rows rather than being represented by one ambiguous checkbox. Test and fault-injection rows remain unchecked until live/runtime validation is complete and the user explicitly clears test work.

### Consolidation Coverage Ledger

| Source tracker | Master ownership | Retained unique contracts |
|---|---|---|
| Present-Now readiness | Phase 0; Phase 9.2 | Accepted-frame immutability, bounded lane capacity, exact foreground reserve, generational tickets, fresh-submit provenance, typed terminal failure, fault injection |
| Core hardening / ARP 06–10 | Phases 0, 1, 5, 7, 9 | Deadline-aware output DAG, Forward+ simplification, device-fault observability, bounded occlusion, GPU classification/native shading/late passes/XR integration, production cutover |
| Frame-loop stability | Phases 1, 2, 5, 6, 8 | Matched benchmark contract, deliberate presentation profiles, causal wait taxonomy, sealed submission, local invalidation, lifecycle retirement, OpenXR completion, promotion gates |
| Resident draw stream / task pool | Phases 3, 4, 8, 9; Section 4 | Canonical generation-safe identity, SoA publication, direct-slot templates and native leases, stable bins, five strategy lanes, diagnostic sidecar, execution topology, worker sweeps |

The [Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md), [mesh submission strategy contract](../../../architecture/rendering/mesh-submission-strategies.md), and [core hardening testing tracker](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md) remain normative architecture or validation companions; they are not additional implementation trackers.

### Core Problem Statement

XRENGINE can exceed 100 Hz in clean Vulkan runs, but it does not yet hold the required deadline consistently across p95/p99 frames, repeated runs, mutation events, cold camera cuts, and mixed desktop/OpenXR workloads.

The root causes are **tail latency, admission livelocks, draw-centric overhead, and architectural fragmentation**:
- Cold camera cuts previously triggered prepared-cohort misses and admission-driven `RecordingDeferred` loops.
- Accidental Mailbox burst pacing masquerades as renderer instability and hides true CPU/GPU cost.
- Submission gateways perform expensive subresource dictionary scans, lifetime-pin acquisitions, and queue-ownership rechecks on unchanged frames.
- Non-draw structural mutations conservatively clear the entire resident template table due to missing reverse-dependency tracking.
- Multiple scene databases (`GPUScene`, `HybridRenderingManager`, `AdvancedSharedGpuSceneDatabase`) and duplicate worker domains compete for CPU resources.
- Synchronous OpenXR eye-submit fence waits introduce 70–100 ms stalls into the render thread.
- Classic G-Buffer, separate Forward+ overlays, and redundant full-resolution copy passes inflate bandwidth.

### Consolidated Target Architecture

The renderer will not be rewritten; rather, the resident data-oriented architecture will be finalized into a cohesive, high-performance pipeline:

1. **Truthful Foreground Execution:** `PresentNow` + `BlockForExact` for desktop and `MeetDeadlineWithGpuFallback` for XR; late acquire after format-independent readiness; monotonic generational resource tickets.
2. **Deliberate Presentation Pacing:** First-class `Stable` (FIFO) and `LowLatency` (Mailbox with hybrid sleep/spin limiter) profiles; attribute every wait above 0.1 ms and at least 99% of detailed frame-root wall time, with explicit gaps of at least $50\ \mu\text{s}$.
3. **Sealed Submission Fast Path:** `SealedSubmissionContract` validating static requirements once and executing clean submissions via compact generation checks (<0.25 ms CPU p95).
4. **Granular Reverse-Dependency Invalidation:** Surgical invalidation of material rows, textures, shaders, and geometry ranges without table-wide clears.
5. **Single Canonical Resident Authority:** `AdvancedSharedGpuSceneDatabase` using ABA-safe `AdvancedGpuHandle(Index, Generation)` handles and frequency-owned SoA streams feeding direct-slot `VulkanDrawTemplateTable`.
6. **Stable Bins & 5 Submission Strategy Lanes:** Numeric `VulkanRenderBinKey` and bin-level manifests feeding `CpuDirect`, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, and `GpuMeshletInstrumented`.
7. **Asynchronous Diagnostic Sidecar:** `GpuDiagnosticReadbackPlan` using a fixed-capacity staging ring with zero current-frame waits, strict zero-readback separation, and general-domain decoding.
8. **Process-Wide Execution Topology:** `EngineExecutionTopology` and pooled `EngineWorkScheduler` owning non-oversubscribed general and render lanes with lane-local command arenas.
9. **Bounded Graph, Streaming, & Tail Work:** Forward+ single normal/depth prepass gating; budgeted cascade updates; asynchronous chunked texture streaming; tombstoned swapchain lifecycle (zero normal `vkDeviceWaitIdle`).
10. **Asynchronous OpenXR Decoupling:** `OpenXrVulkanSubmissionTracker` eliminating the 70–100 ms eye fence-wait with timeline semaphore / fence-ring completion authorities.
11. **Advanced Render Pipeline (ARP 06–10):** GPU material classification, native opaque shading, clustered lighting, visibility-driven transparency/post, and complete legacy retirement.

---

## 2. Checkpoints, Baseline Constraints, & Safety Rules

### 2.1 Current Validated Checkpoints (through 2026-08-28)

- **PresentNow Cold Liveness Validation:** The desktop Vulkan `PresentNow + BlockForExact` path passed an isolated Sponza acceptance run with scheduling capacity forced to 1. The camera swept across 8 exterior, entrance, atrium, upper, and near-wall views. Monotonic progress continued across long shader compilations (~20–21 s) without livelock, renderer pause, old-content replay, or provenance violations.
- **Binary Texture-Cache Dispatch:** Feature-owned binary `XRTexture2D` cache payloads are claimed before generic YAML deserialization. The exact 178,958,379-byte `studio_small_09_4k` cache payload from the failing run loaded through MCP as an `XRTexture2D` with its original-source path intact and no `YamlDotNet`, unresolved-reference, or texture-load failure.
- **Foreground Staging Reserve:** Cold provisioning now creates protected staging entries directly rather than reacquiring an idle entry through the ordinary pool path. Isolated Vulkan evidence reported `configured=4`, `total=4`, `idle=4`, `distinctBuffers=4`, and `distinctGenerations=4`.
- **Build Status:** 0 warnings, 0 errors on targeted Vulkan (`XREngine.Runtime.Rendering.Vulkan.csproj`) and full editor (`XREngine.Editor.csproj`) builds.
- **Measured Performance Baseline:** Clean Release evidence reported render p50 6.959–7.716 ms and p95 8.241–9.175 ms, with Vulkan-frame p50 5.511–5.995 ms and p95 6.410–7.071 ms. One comparable run showed frame-slot wait p50/p95 4.791/7.728 ms and render p50 13.577 ms; an immediate rerun returned slot waits to 0.019/0.029 ms and render p50 to 7.716 ms. These values motivate causal pacing/slot attribution and are not a frozen promotion baseline.
- **Meshlet Prerequisite:** Cleared on 2026-08-22. Cooking, binary caching, and Vulkan EXT indirect-count mesh-task submission are validated.
- **Execution Topology & Scheduler (Phases 1A/1B):** `EngineExecutionTopology` and `EngineWorkScheduler` are implemented in working tree. `Engine.Jobs` and `RuntimeEngine.Jobs` share general lanes.
- **Canonical Publication (Resident Phase 2):** Bounded journals, tombstones, dirty owner ranges, acknowledgements, canonical handles, retained material/layout/kernel payloads, logical texture/sampler records, backend package metadata, and the first frame-slot Vulkan SoA/descriptor realization are implemented. Production shader/pipeline consumption, complete dual-feed parity, and legacy-array removal remain open.
- **Advanced Vulkan Descriptor ABI (Phase 3.1):** The binding-ready ABI is implemented: ordinary uniforms remain set 0, visibility/pass resources remain set 1, advanced sampled-image/sampler arrays use runtime-owned set 2, and advanced canonical tables use runtime-owned set 3. Exact advanced programs are link-time validated and the prepared frame binds the retained publication's native sets without allowing the legacy descriptor allocator to allocate, write, or fingerprint them. Shader-family capability promotion, real advanced stage execution, and parity remain open.
- **Vulkan Template Table & Native Leases (Phase 3):** Direct-slot template lookup and transactional native generation leases implemented.
- **Output Scheduling (Core Hardening Phase 5):** Deadline-aware output ordering, acquired-eye reservation, bounded optional-output policy, narrow queue-lock ownership, and frozen modal-resize presentation packages are implemented; long-duration acceptance remains open.
- **Forward+ Simplification (Section 6):** Complete-scene normal/depth target from deferred attachment 1 plus depth overlays forward opaque/masked surfaces once; contact-copy pair and merge replays removed.
- **Heavy-Load Phase 0/1 Revalidation:** The final isolated Sponza/Jax-Mitsuki run crossed a 21.679 s exact-readiness frame and continued beyond frame 1100. All 33 rate-limited correlated-tree records completed, none exceeded the frame root, and the stopped logs contained zero accepted-frame rejection, recording deferral, renderer pause, backpressure, device loss, YAML exception, VUID, or validation error. One command-generation mismatch was rejected before Vulkan acceptance while startup shadow-budget settings were restored; the next package was presented normally.

### 2.2 Frame Deadline Budgets

| Refresh Target | Hard Frame Deadline | Engineering Target (p99) |
|---|---:|---:|
| **100 Hz** (Level A) | 10.000 ms | 8.5–9.0 ms |
| **120 Hz** (Level B - Promotion Gate) | 8.333 ms | 7.1–7.5 ms |
| **144 Hz** (Level C - Stretch Gate) | 6.944 ms | 5.9–6.25 ms |
| **165 Hz / 200 Hz** | 6.061 ms / 5.000 ms | Characterization / Long-term target |

### 2.3 Explicit Non-Fixes and Anti-Patterns

The following are strictly forbidden as solutions:
- Increasing queue capacities to mask admission livelocks.
- Increasing worker counts beyond the physical execution budget.
- Polling in tight loops or busy-spinning across the entire frame interval.
- Re-introducing CPU readbacks, full bucket scans, or synchronous diagnostic waits into zero-readback passes.
- Enabling `SIMULTANEOUS_USE_BIT` to avoid correct slot-owned command pool management.
- Creating a second parallel scene database or residency registry.
- Calling `vkDeviceWaitIdle` during normal resize or swapchain recreation.
- Committing or updating automated tests before live/runtime validation passes and explicit user clearance is granted.

---

## 3. Master Phased Execution Roadmap

```
Phase 0: In-Flight Checkpoint & Present-Now Live Revalidation
  │
Phase 1: Baseline Characterization, Telemetry Taxonomy, & Deliberate Pacing
  │
Phase 2: Submission Fast Path & Reverse-Dependency Invalidation
  │
Phase 3: Canonical GPUScene Residency, Stable Bins, & 5 Strategy Lanes
  │
Phase 4: Concurrency Closure & Multi-Lane Render Work Pool
  │
Phase 5: Render Graph Simplification, Streaming, & Tail Latency Bounds
  │
Phase 6: OpenXR Asynchronous Decoupling & Lifecycle Hardening
  │
Phase 7: Advanced Render Pipeline Modernization (Phases 06 Through 10)
  │
Phase 8: High-Refresh Promotion Gates & Full Validation Matrix
  │
Phase 9: Phase-Local Test Clearance, Legacy Deletion, & Closeout
```

Phase 9.1 is a recurring gate, not a requirement to postpone every cleared slice's tests until all ARP work is complete. Each feature slice must pass its relevant live/runtime path and receive explicit user clearance before its tests are added or changed. Final deletion and program closeout still wait for the complete integrated validation matrix.

The diagram expresses promotion order, not blanket serialization. After the benchmark/telemetry contract is frozen, sealed submission/invalidation, canonical residency, scheduler closure, and graph/publication work may proceed in parallel when ownership does not overlap. OpenXR decoupling depends on the measured completion/lifetime model; ARP cutover depends on canonical publication and resource contracts. Phase 8 promotion always uses one frozen integrated revision.

---

### Phase 0 - In-Flight Checkpoint & Present-Now Live Revalidation

**Goal:** Ensure the foreground presentation pipeline is truthful, livelock-free, and diagnostically clean under cold-resource pressure before executing high-refresh optimizations.

#### 0.1 Serialization & Asset-Graph Boundaries
- [x] Mark `SceneNode.ComponentAdded` and `ComponentRemoved` with `RuntimeOnly`, `YamlIgnore`, and `MemoryPackIgnore`.
- [x] Exclude compiler-generated event backing fields from persistence.
- [x] Mark `XRMeshRenderer.BindingPublishers` as `RuntimeOnly` and `YamlIgnore`.
- [x] Make `XRAssetGraphUtility.BuildAccessors` honor runtime-ignore attributes and exclude transient `DeferredLightBindingPublisher._deferredLights`.
- [x] Dispatch feature-owned binary `XRTexture2D` cache assets before generic YAML, claim incompatible recognized payloads without text fallback, and use the original source texture when a prospective cache is missing or unusable.
- [x] Verify original `[MEMORYPACK SERIALIZE FAIL]` and `> 1000 array` warnings are absent in live Sponza runs.
- [x] Verify authored scene/component state round-trips cleanly across export, deletion, and re-import.

#### 0.2 PresentNow Output Contract & Readiness
- [x] Add explicit `ERenderWorkClass.PresentNow` and present policies (`BlockForExact` for desktop, `MeetDeadlineWithGpuFallback` for XR).
- [x] Default `DesktopScene`, `EditorScenePanel`, and terminal `Present` work to `PresentNow` + `BlockForExact`.
- [x] Propagate terminal requirements through the complete producer closure (visible meshes, targets, bindings, descriptors, pipelines, uploads, shadows).
- [x] Disallow `ERenderOutputWorkDisposition.Deferred` for desktop foreground outputs.

#### 0.3 Bounded Frame Plan & Late Acquire
- [x] Capture immutable camera, visibility, material, light, and output-generation snapshots in `VulkanAcceptedFramePlan`.
- [x] Store frame plans in frame-slot preallocated arenas with independent capacities for UI, main scene, and shadows.
- [x] Move format-independent resource readiness before desktop swapchain acquisition.
- [x] Reseal target-dependent state on resize without invalidating prepared scene resources.

#### 0.4 Monotonic Resource Tickets
- [x] Replace whole-cohort poisoning (`_publishedCohortRejected`) with monotonic generational resource tickets (`Declared` $\rightarrow$ `CpuPrepared` $\rightarrow$ `GpuAllocationReserved` $\rightarrow$ `UploadSubmitted` $\rightarrow$ `Resident` $\rightarrow$ `Ready`).
- [x] Make `VulkanMeshOperationRequestQueue` a scheduling optimization; queue backpressure retains ticket runnability and completed progress.
- [x] Remove `DrainTo == -1` cohort clearing.

#### 0.5 Foreground Readiness Paths
- [x] Synchronously compile target-known mandatory pipelines (e.g. ImGui) during context initialization; fail visibly on error.
- [x] Allow foreground threads to claim, promote, or synchronously compile cold material pipelines.
- [x] Reserve foreground staging ring capacity for required uploads; chunk large textures to prevent allocation spikes.
- [x] Provide independent scheduling/arena capacity for shadow cascades; bypass background budgets under `BlockForExact`.
- [x] Encode missing secondary command buffers inline in the primary buffer.
- [x] Make staging recycle publication allocation-generation-aware and atomic with the reservation it makes visible.
- [x] Prove `EnsureForegroundReserve` creates the requested number of distinct staging slices rather than repeatedly validating one slice.
- [x] Prove background upload, compilation, and shadow work yields to exact foreground readiness and resumes without starvation.

#### 0.6 Truthful Recording & Failure Semantics
- [x] Feed primary recording only sealed plans with zero unresolved tickets; reach `vkBeginCommandBuffer` or emit concrete errors.
- [x] Define `PresentedNew(frameId)` to require command recording, a new submit serial, and presentation waiting on that submit.
- [x] Eliminate `PresentLastCompletedContent` from ordinary foreground recovery.
- [x] Implement configurable liveness watchdog reporting frame stage, active ticket, and elapsed time.
- [x] Publish typed terminal failure records distinguishing no-image, out-of-date, surface-lost, device-lost, OOM, admission, readiness, recording, submission, and presentation outcomes.
- [x] Publish one detailed terminal transition when `PresentNow` pauses exact readiness and one reproducible record for every genuine permanent failure.

#### 0.7 Live Vulkan Acceptance Gate
- [x] Run isolated capacity-one Sponza acceptance session (`Tools/Manage-McpEditorSession.ps1`).
- [x] Verify away-to-dense camera transitions succeed with fresh frames, monotonic graphics signals, zero `RecordingDeferred`, and zero cohort poisoning.
- [ ] Mutate camera and scene state while preparation is blocked; prove the accepted epoch remains immutable and exactly one captured epoch is submitted.
- [ ] Reproduce the observed 221-request and 836-request shapes, then naturally exceed a declared main-scene lane and verify one bounded `FramePlanCapacityExceeded` record with configured, required, accepted, and rejected counts.
- [ ] Exercise exact OpenXR deadline/fallback behavior on Monado and at least one available hardware runtime; desktop-only evidence does not close the XR contract.
- [ ] With RenderDoc 1.44 and its Vulkan layer already aligned, diagnose why direct frame-60/frame-450 launches observe no present, then capture a settled Sponza frame and verify bindings/draw order.

---

### Phase 1 - Baseline Characterization, Telemetry Taxonomy, & Deliberate Pacing

**Goal:** Establish reproducible, low-noise performance baselines, eliminate Mailbox burst pacing illusions, attribute every wait above 0.1 ms, and account for at least 99% of detailed frame-root wall time with explicit `Unattributed` gaps of at least $50\ \mu\text{s}$.

#### 1.1 Baseline Measurement Contract
- [x] Freeze benchmark manifest: exact revision and dependency manifests; build configuration; CPU/GPU/memory; OS and driver; monitor/refresh; power plan; window mode; resolution/render scale; scene/camera trajectory; submission strategy; and feature stack.
- [x] Record present mode, swapchain image and frame-slot counts, frame-generation state, validation state, render-target mode, and active OpenXR runtime in every manifest.
- [x] Create a dedicated `ReleaseBenchmark` or equivalent configuration. Disable validation, verbose Vulkan logging, profiler UI/graphs, screenshots, readbacks, RenderDoc injection, and frame capture for performance runs; run correctness and diagnostic captures separately.
- [x] Pre-warm shader compilation, pipeline caches, material tables, resident templates, imports, and swapchain storage.
- [x] Run three 60-second repetitions per comparison; reject runs if run-to-run p95 variance exceeds 7.5% (target $\le 5\%$).
- [x] Record p50, p95, p99, maximum, standard deviation, missed-deadline counts/streaks, a frame-interval histogram, and periodicity analysis for whole frame, render frame, Vulkan frame, GPU frame, and each CPU stage.
- [x] Record allocation, native-allocation, command-buffer reset/allocation, descriptor-write, resident hit/miss/invalidation, submit, readback/map, and forced-wait counts with each run.
- [ ] Capture matched static/moving desktop baselines for CPU direct, GPU indirect zero-readback, and GPU meshlet zero-readback; keep OpenXR baselines separate.
- [x] Record actual presentation intervals rather than inferring display cadence from CPU FPS.

#### 1.2 Presentation Profiles & Deliberate Pacing
- [x] Implement `Stable` profile: FIFO, refresh-paced, bounded latency, no frame generation (default for stability/editor).
- [x] Implement `LowLatency` profile: Mailbox with target-rate limiter and maximum 1 queued application frame (`frames_ahead <= 1`).
- [x] Implement `Uncapped` profile: Immediate mode for headroom diagnosis.
- [x] Implement `FrameGeneration` as a separate Streamline/DLSS-compatible presentation policy.
- [ ] Capture its separate benchmark and Streamline/DLSS promotion evidence.
- [x] Implement hybrid sleep/spin frame limiter for Mailbox to prevent CPU runaway and burst slot contention.
- [x] Move unavoidable frame-slot reuse waits to the earliest legal frame-authority boundary before visibility collection.
- [x] Publish slot readiness early enough for safe non-render gameplay work when game and render authority are separate.
- [x] Pace secondary ImGui platform-window swapchains independently so occluded tool windows cannot stall the primary viewport.
- [x] Coalesce resize events at the frame-authority boundary and disable frame generation/interop layers for native renderer baselines.
- [x] Capability-probe `VK_KHR_present_id`, `VK_KHR_present_wait`, and `VK_GOOGLE_display_timing`.

#### 1.3 Causal Wait Attribution & `VulkanFrameTelemetry` Taxonomy
- [x] Replace disconnected counters with unified `VulkanFrameTelemetry` schema.
- [x] Define stable coarse stage taxonomy: Frame Pacing $\rightarrow$ Snapshot Handoff $\rightarrow$ Acquire $\rightarrow$ Plan $\rightarrow$ Resource Prep $\rightarrow$ Scheduling $\rightarrow$ Recording $\rightarrow$ Submit $\rightarrow$ Output Completion $\rightarrow$ Settlement.
- [x] Time and isolate desktop frame-slot and acquired-image waits, native acquire,
  queue admission vs. native submit, present admission vs. native present,
  frame-limiter sleep/spin, and contended command-pool, descriptor-arena,
  submission, queue-lease, lifetime, descriptor-publication, upload, pipeline-
  compiler, and synchronization authorities. Successful uncontended lock entry
  does not take a timestamp; only waits at least 0.1 ms enter the bounded causal
  payload.
- [ ] Prove exhaustive $>0.1$ ms coverage for compute, transfer, present, and
  OpenXR reuse authorities and for any Vulkan lock/arena sites outside the
  instrumented frame-critical authorities.
- [x] Capture causal payload for waits $>0.1$ ms without string formatting on successful frames: frame ID, slot, image index, semaphore target/completed values, queue family, pending commands, and concurrent worker activity.
- [ ] Attribute $\ge 99\%$ of frame-root wall time in detailed captures; emit `Unattributed` records for any gap $\ge 50\ \mu\text{s}$.
- [ ] Run diagnostic A/Bs for two vs. three frame slots, Mailbox limiter on/off, matched FIFO, reduced-resolution GPU headroom, and one-at-a-time suppression of compiler, streaming, secondary windows, and editor diagnostics.
- [ ] Prove every recurring slot wait has an exact producer/timeline owner and that uncapped GPU-headroom slot-wait p95 is approximately zero.

#### 1.4 Correlated Observability & Device-Fault Diagnostics
- [x] Publish one correlated allocation-free frame tree using stable engine/render/output IDs and inclusive/exclusive work, waits, native driver time, external runtime time, worker overlap, and required-output critical path.
- [x] Replace disconnected lifecycle counters and flat profiler leaves with the shared taxonomy in Section 4; expose the same IDs to editor timeline/tree views, capture output, logs, and MCP.
- [x] Add device-fault, TDR-risk, memory-budget, last-successful-submission breadcrumb, Vulkan/OpenXR submit, and descriptor-state diagnostics when supported.
- [x] Add interactive-resize liveness breadcrumbs for modal callback entry/exit, visibility publication, package selection, plan replacement, retirement, waits, submit, and present.
- [x] Keep aggregate frame-tree publication allocation-free.
- [ ] Measure observer overhead; detailed capture must attribute at least 99% of wall time and identify every gap of at least $50\ \mu\text{s}$.

**Phase 1 implementation checkpoint (2026-08-27):** The profile resolver,
hybrid limiter, early slot reuse boundary, native submit/present split, shared
frame taxonomy, bounded stage-qualified causal payloads, frame-critical lock/
arena contention instrumentation, stable submission IDs, device/memory/
descriptor diagnostics, manifest schema, prewarm path, matched cohort contract,
distribution/periodicity statistics, and allocation-free correlated frame tree
with shared profiler/log/MCP/capture representations are implemented. The
unchecked Phase 1 items are empirical or hardware/runtime closeout gates:
measured matched baselines, separate frame-generation promotion evidence,
exhaustive non-desktop authority coverage, 99% detailed-capture proof,
diagnostic A/Bs, approximately-zero uncapped slot-wait p95, and measured observer
overhead. They must remain unchecked until captured on the target hardware/
runtime matrix.

The integrated isolated smoke session
`20260827-010729-vulkan-phase1-stable` reused one build across all native
profiles. Stable resolved FIFO, LowLatency resolved Mailbox with a 60 Hz hybrid
limiter and `frames_ahead=1`, and Uncapped resolved Immediate without a
limiter. MCP observed non-zero submission serials and matching graphics timeline
values, valid device-local VMA budget snapshots, no device loss, and zero
validation errors. All three stopped-session logs contain zero `RendererPaused`,
`DesktopFrameFailure`, readiness exception, frame rejection, backpressure,
device-loss, unhandled-exception, VUID, or validation-error records. A Stable
sample attributed 82.38% of its 64.49 ms frame and explicitly published the
remaining 11.36 ms gap, correctly leaving the 99% empirical gate open.

The final isolated observability session
`20260827-013529-vulkan-phase1-observability` published the same correlated
engine/render/output/authority IDs and inclusive/exclusive category totals to
the allocation-free runtime publication, profiler transport and collapsible UI
tree, MCP/profile-capture schema, and rate-limited `[Vulkan][FrameTree]` logs.
The stopped-session logs contain zero renderer pause, desktop frame failure,
readiness exception, frame rejection, backpressure, device-loss, unhandled-
exception, VUID, validation-error, or YAML records. Cold Jax/Mitsuki authoring
still produced 11.093 s and 6.317 s root-exclusive samples while Poiyomi
conversion, shader-cache loading, texture streaming, and descriptor invalidation
were active, but the renderer recovered and continued advancing. These samples
keep the 99% attribution and later resident-publication/tail-work gates open;
they are not evidence of a remaining terminal renderer failure.

The follow-up `20260827-020459-vulkan-phase1-wait-attribution` session crossed a
27.184 s cold Jax/Mitsuki frame and continued beyond frame 4500 with zero
renderer pause, desktop frame failure, rejection, backpressure, device loss,
unhandled exception, YAML exception, VUID, or validation-error records. That run
also exposed a roughly 0.05 ms accounting overlap: the pre-collect next-slot
wait and acquire/submit maintenance details were being assigned to stages other
than the phase in which they executed. The corrected
`20260827-021317-vulkan-phase1-wait-attribution-fixed` run charges current-slot
and next-slot waits independently and keeps query sampling, uniform-ring reset,
and staging trim in their actual acquire/submit stages. A cold frame 592 then
reported 1282.2351 ms inclusive, 1220.5700 ms `ResourcePrepare`, 60.0963 ms
`CommandRecord`, 0.0083 ms root-exclusive, and 99.9994% attribution while
completing successfully. That session also exposed a separate post-import edge
at frame 584: reapplying an unchanged mesh-submission setting advanced the
pipeline command generation after package publication, so the accepted desktop
attempt deferred and was rejected by the fresh-output contract.

The command-chain settings boundary is now idempotent. A pipeline records the
requested strategy captured by its generated commands, backend capability
downgrades remain in the later resolver, and an unchanged settings cascade does
not rebuild the chain. Session
`20260827-022330-vulkan-phase0-generation-stability` crossed the same import
reapply boundary with zero accepted-frame deferrals or rejections. The final
session `20260827-023231-vulkan-phase0-generation-seeded` advanced past frame
1100, published 33 completed frame-tree samples with zero stage/root overlap,
and reported 99.9876% attribution on frame 1116. Its longest sampled frame was
21.679 s and still completed. The stopped logs contain zero renderer pause,
desktop frame failure, accepted-frame rejection, recording deferral,
backpressure, device loss, YAML exception, VUID, or validation error. One
collect-side `CommandGenerationMismatch` remains when the bootstrap restores
shadow-budget settings; it is rejected before Vulkan acceptance and immediately
replaced by a fresh package, which is the intended stale-snapshot guard rather
than a presentation failure.

These are strong implementation and live-runtime evidence, but they are not the
frozen detailed-capture and hardware/runtime matrix required to close the 99%
promotion gate or the other explicitly unchecked empirical rows.

---

### Phase 2 - Submission Fast Path & Granular Invalidation

**Goal:** Make normal-frame submission proportional to changed generations rather than recorded object graphs, and eliminate table-wide resident clears on local mutations.

#### 2.1 Tracked Submission Gateway Instrumentation
- [x] Instrument `SubmitToQueueTrackedWithDisposition` with allocation-free timers for: image contract, queue ownership, lifetime pins, state serialization, native submit, lifetime publish, image publish, diagnostic publish, and pin release.
- [ ] Determine exact gateway CPU p50/p95/p99 overhead.

#### 2.2 `SealedSubmissionContract` Fast Path
- [x] Add `SealedSubmissionContract` owned by each reusable resident command artifact.
- [ ] Pre-validate image transitions, queue families, resource generations, render scopes, nested artifacts, and native lifetimes at record time.
- [x] Store compact generation vector and immutable dependency manifest with the artifact.
- [ ] On stable hits, validate only the generation vector; bypass subresource dictionary scans and queue-ownership recomputations.
- [x] Use the full contract only for cold, dirty, instrumented, sampled-correctness, or dependency-generation changes; compare sampled full-path results against sealed results.
- [ ] Replace submit-time dictionaries with flat arrays keyed by stable resource indices.
- [x] Batch lifetime pins per dependency manifest instead of per-draw/subresource reacquisition.
- [x] Hold native queue lock strictly across `vkQueueSubmit` calls; never across logging, diagnostics, or retirement.
- [ ] Aggregate graphics work into one coarse submission per output where practical.
- [ ] Target unchanged submission CPU p95 $<0.25$ ms.

#### 2.3 Reverse-Dependency Manifests & Granular Invalidation
- [ ] Implement compact reverse-dependency arrays:
  - `Material` $\rightarrow$ resident draws
  - `Texture / Material Row` $\rightarrow$ materials
  - `Geometry` $\rightarrow$ resident draws
  - `Pipeline Layout / Pipeline` $\rightarrow$ resident variants
  - `Descriptor Layout / Table` $\rightarrow$ dependent variants
  - `Render Pass / Output` $\rightarrow$ command artifacts
  - `Shader Generation` $\rightarrow$ pipelines, materials, resident variants
  - `Shadow / Probe Publication` $\rightarrow$ dependent passes / material rows
- [ ] Give frame, view, pass, material, object, instance, texture, sampler, descriptor, pipeline-layout, and shader data independent version domains, with topology and content separated.
- [ ] Emit dirty ranges at the mutation point; eliminate table-wide clearing fallbacks.
- [x] Preserve tombstones and generation-safe reuse until all consumers acknowledge retirement.
- [ ] During migration only, retain a counted broad correctness fallback when a manifest is missing or inconsistent; record exact reason/domain/entry count and require zero broad fallbacks in every promotion scenario.
- [ ] Verify local mutations (1 material scalar, 1 texture binding, 1 geometry replacement, 1 shader reload) invalidate only exact dependent entries.
- [ ] Verify add/remove/re-add is generation safe, one shadow-cascade update leaves unrelated entries warm, and camera/object transforms cause zero structural or bin invalidations.

Phase 2 resource-domain continuation (2026-08-27): canonical texture and sampler
tables now have independent generation-safe owners, dirty ranges, lookup
publication, and backend-ready dirty-owner mapping. The whole-scene material
transition owns exact texture/sampler reference multiplicities and publishes
resource changes without a table-wide material clear. Retained texture/sampler
deltas now also enter backend template projection as resource-table mutations.
The parent rows remain unchecked until Vulkan descriptor/table realization
supplies the corresponding reverse manifests and the local-mutation matrix
proves zero broad fallback.

Phase 2 native-resource continuation (2026-08-28): exact pinned texture/sampler
publications now lower into frame-slot-owned descriptor-indexing arrays and
immutable lookup/storage slices, with native receipts retired by the existing
GPU publication completion authority. This supplies the concrete Vulkan table
generation needed by later reverse-dependency wiring without introducing a
parallel lifetime graph. The Phase 2 rows remain unchecked because production
pipelines do not yet consume this resource set, descriptor/table-to-variant
reverse manifests are not complete, and the exact local-mutation/zero-broad-
fallback matrix has not run.

Phase 2 implementation checkpoint (2026-08-27): the tracked gateway now reports
allocation-free stage histograms plus reason-coded seal/fallback and exact/broad
invalidation telemetry. Every reusable graphics-primary command lifetime owns
its contract, which is sealed automatically at a successful recording boundary.
Presealed contracts carry compact generation vectors, stable ABA-safe
native-resource slots, complete descriptor-resource closure, and ordered image
entry/exit manifests; sampled full-path parity records both acceptance
directions. Descriptor resource-closure and image-payload generations are
independently published. Canonical mutation domains and the current resident-draw
reverse graph are implemented with a counted broad correctness fallback. Exact
detached swapchain-image generations preserve their lifetime slots until all
recorded, descriptor, and submission pins drain. See
`docs/work/investigations/rendering/vulkan-frame-loop-phase2-2026-08-27.md`.

Follow-up seal correction (2026-08-27): sampled full validation no longer
permanently discards a reusable seal when its refreshed descriptor/resource
closure still matches exactly. `MissingContract` and `ResourceVector` are now
separate fallback reasons, and the profiler exposes a direct allocation-free
`gateway_total` p50/p95/p99 histogram instead of requiring component-stage
sums. Two forced-full sample boundaries retained zero parity mismatches and
continued stable hits. The aggregate Debug live sample measured 0.2048 ms p50,
0.4096 ms p95, and 0.4096 ms p99, but was dominated by freshly recorded
`MissingContract` submissions; the empirical rows above remain unchecked until
the frozen Release benchmark and unchanged-sealed cohort are measured.

**Phase 2 wrap boundary (2026-08-28):**

- **Done:** tracked gateway stage/fallback telemetry; reusable sealed contracts
  with compact generation vectors and dependency manifests; sampled full-path
  acceptance parity; batched native lifetime pins; canonical texture/sampler
  generation domains; frame-slot native table/descriptor generations; flat
  ABA-safe command/image-subresource indices on the sealed normal path;
  transitive native pipeline/descriptor/shader/render-pass/output/artifact
  invalidation; and proportional resident table/lookup publication.
- **Still open:** the unchecked rows above. In particular, full image-state and
  cold-fallback dictionary replacement, coarse submission aggregation, the
  remaining logical material/texture/shadow/probe reverse edges, exact
  local-mutation validation, zero broad fallbacks, the frozen Release `<0.25 ms`
  stable-hit gate, and hardware/OpenXR coverage remain unproven.
- **Dependency:** output-aware Phase 3 family scheduling and five-lane parity
  must exist before the remaining logical reverse edges and their mutation
  matrix can be closed honestly.

---

### Phase 3 - Canonical GPUScene Residency, Stable Bins, & 5 Strategy Lanes

**Goal:** Make `AdvancedSharedGpuSceneDatabase` the sole resident authority, eliminate draw-centric preparation, build stable numeric bins, and unify all 5 submission strategies over a single substrate with an async diagnostic sidecar.

#### 3.1 Canonical `AdvancedSharedGpuSceneDatabase` Publication
- [x] Implement bounded delta journals, tombstones, dirty owner ranges, and consumer acknowledgement in `AdvancedSharedGpuSceneDatabase`.
- [x] Retain `AdvancedGpuHandle(Index, Generation)` as the sole ABA-safe logical handle across all tables (draw, instance, transform, deformation, state, geometry, material).
- [ ] Dual-feed legacy `GPUScene` / `HybridRenderingManager` and canonical package projections; verify handles/dense remaps, membership/order/pass/selection/instance identity, material/geometry/shadow/dependency signatures, and visual/output identity for every selected strategy before canonical production cutover.
- [x] Publish packed material constant words, texture/sampler bindings, material-layout rows, and shading-kernel rows.
- [x] Publish canonical deltas, view/pass records, strategy assignments, dirty ranges, and compact exception records through `BackendReadyFramePackage`.
- [x] Lower each exact retained publication into frame-slot-owned Vulkan material/resource/lookup slices, a per-publication canonical table descriptor set, and a frame-slot sampled-image/sampler descriptor set with completion-owned native receipts.
- [x] Define and enforce the binding-ready Vulkan ABI for exact advanced programs, use runtime-owned set layouts, bind the retained native sets during prepared recording, and exclude those sets from legacy descriptor allocation/writes/fingerprints.
- [ ] Remove live `BackendReadyMeshSelection` managed arrays after parity and make the immutable backend-ready package the only normal-frame Vulkan input.
- [ ] Remove legacy `GPUScene` storage and ID maps after dual-publication parity.

Phase 3.1 wrap checkpoint (2026-08-27): canonical generational texture and
sampler tables now participate in capacity planning, lookup publication,
publication sealing, reclamation, compaction, and growth. The legacy material
feed also uses a renderer-neutral numeric/source encoder, and the material
database has bounded fixed-slot payload storage plus an immutable payload
snapshot primitive. A focused shared-material publisher exists as an unwired
integration draft. Rows 3.1.3 and 3.1.4 remain unchecked: material payloads are
not yet attached to the retained scene-publication ring or backend-ready
package, draw registration does not acquire the shared material/resource
owners, Vulkan lowering still lacks a frozen publication-owned dependency
closure, and the five-strategy dual-feed/output parity matrix has not run.

Phase 3.1 retained-payload continuation (2026-08-27): every scene-publication
ring slot now owns and transactionally seals the immutable packed material
payload image with the material/layout/kernel and texture/sampler tables. A
sequence mismatch rejects the complete publication before its ring tail becomes
visible. The shared material publisher now has generation-aware direct handle
lookup, local removal repair instead of a full hash rebuild, exact bounded
capacity rejection, and immutable variant identity. Database-owned compound
creation now interns exact layout/kernel schema, preflights all missing schema
and material capacity before the first write, and creates one independently
owned material row per publisher variant. Publisher updates reuse retained
schema handles rather than mutating schema. The backend-ready package
intentionally does not expose ring-owned payload spans after releasing its
package lease. The scene capacity profile now reserves the three supported
layouts, their members, fixed-stride payloads, and logical resource rows instead
of the former one-empty-layout/zero-payload profile. A live isolated Vulkan run
produced fresh camera-dependent readbacks and continued publishing beyond frame
1936 with no canonical publication rejection or Vulkan validation VUID.
Production draw/resource acquisition remains ordered behind a refcounted logical
texture/sampler publisher; invalid placeholder bindings are not accepted as
parity. Vulkan GPU-pin SoA consumption, five-strategy dual-feed parity, and a
separately observed recoverable startup framebuffer-backing race remain open. See
`docs/work/investigations/rendering/vulkan-frame-loop-phase3-material-publication-2026-08-27.md`.

Phase 3.1 logical-resource continuation (2026-08-27): a scene-boundary-owned
logical resource publisher now assigns texture identity by `XRTexture` reference
and value-interns immutable sampler rows. Whole-batch acquire, release, and
acquire-before-release replacement preflight peak publisher counts, exact
reference multiplicities, metadata conflicts, and the combined canonical table
journals before writing. The source contract deliberately accepts only full,
non-rectangle, non-MSAA `XRTexture2D` resources matching the current material
layouts. Stable explicitly numbered format/compare enums and normalized sampler
keys preserve filter, LOD, anisotropy, comparison, address, and border state
without implying Vulkan residency or descriptor realization. Publisher registry
growth now follows the shared frame-boundary capacity profile. The row remains
unchecked because canonical draw registration still needs the preallocated
whole-scene transition plan that acquires these bindings, publishes shared
materials/draw replacements, and drains old ownership in that order.

Phase 3.1 whole-scene ownership continuation (2026-08-27): production canonical
scene publication now builds one preallocated transition plan before opening the
database transaction. It captures mutable inputs once, deduplicates command and
material identities, encodes each unique variant once, and aggregate-preflights
scene, schema, material, logical-resource, journal, and reference-count capacity.
Commit order is resource acquire, shared-material upsert/retain, draw add/switch,
draw tombstone, material retirement, then resource release. Unsupported pass,
legacy-state, and resource translations remain ordered compatibility exceptions
with typed reasons. Exact GLSL value kinds, coverage/double-sided state, texture
feature flags, resource dirty owners, and native opaque/masked eligibility are
now canonical data rather than shallow per-draw placeholders. The material
publication row above is therefore complete. A shared-ownership/update/refresh/
retirement smoke passed, and a fresh Vulkan session produced two camera-dependent
readbacks through frame 2644 with no publication rejection, exception, VUID,
validation error, device loss, or OOM. The run also confirms why Phase 3.1 itself
is not closed: Vulkan still reports that advanced rendering is unavailable until
GPU-addressable texture indirection exists. GPU publication leasing, frame-slot
SoA/descriptor realization, five-strategy parity, legacy-array removal, and
production cutover remain open. See
`docs/work/investigations/rendering/vulkan-frame-loop-phase3-material-publication-2026-08-27.md`.

Phase 3.1 publication-safety closeout (2026-08-27): publication visibility is
now staged as reserve, snapshot prepare, lookup commit, and ring commit. Prepared
snapshots remain invisible until lookup rows are complete under the publication
lock; any post-begin failure permanently faults/quarantines the database instead
of exposing or reusing partial producer state. Rejected/faulted scenes cannot
reproject stale backend packages. Every prior and current renderer source is
planned before compatibility resolution, so unsupported or removed primitive
zero is explicitly republished invalid. Texture/sampler snapshot deltas reach
backend template projection, and resource compatibility reports stable typed
subreasons for type, shape, empty content, sampler numeric state, format,
address, comparison operation, and comparison/depth mismatches. The expanded
scene smoke passed invalidation, exact-reason, and recovery cases; a max-effort
architecture re-review found no blocking correctness issue. Fresh named Vulkan
session `phase31-staged-commit-20260827` built with zero warnings/errors,
produced two distinct inspected 1920x1080 readbacks, and advanced through frame
520 without canonical rejection/fault or a validation VUID. Its one startup
`XRTexture2DArray` backing failure recovered on the next frame and matches an
independent pre-change session, so it remains a separate bootstrap lifetime
issue. Phase 3.1's remaining boundary is Vulkan GPU-addressable texture/sampler
realization plus GPU-lease SoA lowering and the five-strategy parity/cutover
matrix.

Phase 3.1 retained-replay continuation (2026-08-27): every retained canonical
scene publication now owns immutable physical record/handle images and the
authoritative logical lookup image for draw, material, kernel, layout, texture,
and sampler tables. It also owns exact layout-member, constant-word, and
texture-binding ranges plus strong `XRTexture` source references for logically
resident texture handles. This closes the replay gap that previously forced a
Vulkan resident dependency manifest to consult mutable live databases after
acquiring a publication lease. `VulkanResidentDrawDependencyManifest` now
resolves exclusively from the exact pinned publication and rejects missing,
stale, or sequence-mismatched data. Backend-ready frame packages remain compact
identity/delta projections instead of copying payload or source objects. A
three-publication add/replace/tombstone smoke caught and fixed logical-handle
resurrection from physically retained tombstone rows. Renderer-neutral, Vulkan,
and isolated full-editor builds pass with zero warnings/errors; fresh named
Vulkan validation reached frame 853 with 19 resident-template creations, 9 exact
invalidations, zero dependency rejects, zero validation messages/errors, and
zero dropped frame/draw operations. The next boundary is a frame-slot-owned
`VulkanAdvancedSceneResourceRuntime` with separate sampled-image and sampler
descriptor-indexing tables, native-generation receipts carried by the existing
publication lease, and correct logical sampler resolution in the advanced
shader. Descriptor-heap mode remains explicitly unsupported until implemented
and measured; five-strategy parity/cutover remains open. See
`docs/work/investigations/rendering/vulkan-frame-loop-phase3-material-publication-2026-08-27.md`.

Phase 3.1 native-resource continuation (2026-08-28):
`VulkanAdvancedSceneResourceRuntime` now lowers each distinct exact pinned
publication once per frame slot into a fixed 8 MiB `AdvancedSceneStorage` lane,
immutable material/resource/lookup slices, and separate fixed-capacity sampled-
image and sampler descriptor-indexing arrays. Dense-plus-one encoded references
reserve zero as invalid/fallback, and logical sampler lookup is independent of
image identity. Whole-publication preflight revalidates the retained strong
texture source image and requires an existing ready Vulkan descriptor; it never
creates wrappers, uploads synchronously, or exposes partial native state. Typed
native receipts travel beside the canonical GPU publication lease through
prepared-frame transfer and resident frame-slot retention, then retire under the
same completion authority. Logical-device publication now finalizes the live
descriptor backend after allocator startup, correcting the previous bootstrap/
runtime split where descriptor indexing was enabled but the manager remained on
`DescriptorSets`. Standard-Validation session
`phase31-native-validation-20260827` reached canonical sequence 717 / native
generation 1 with 31 textures and 5 samplers after progressive streaming
settled; frame 998 reported zero package/dependency/capacity/broad-fallback/
dropped-operation failures and zero active validation messages/errors. Two
fresh camera-dependent 1920x1080 Vulkan readbacks were visually verified. The
final ownership build was repeated in named session
`phase31-native-retirement-20260828`: its full isolated editor build passed with
zero warnings/errors, canonical sequence 595 lowered successfully, and frame
843 again reported zero active validation, package, dependency, capacity,
descriptor-binding, dropped-operation, or pending-retirement failures. The
rows remain unchecked because the native resource set is not yet bound into the
production advanced shader/pipeline families and the five-strategy dual-feed
parity/cutover matrix has not run. Active-frame validation was clean; shutdown
still names five image views and one pipeline layout in the separate known
device-teardown debt. See
`docs/work/investigations/rendering/vulkan-frame-loop-phase3-material-publication-2026-08-27.md`.

#### Phase 2/3 Implementation Wrap Boundary (2026-08-28)

The retained publication, Vulkan binding substrate, and one exact mono
visibility-family implementation now exist end to end. This is an implementation
checkpoint, not a production capability promotion: global shader-family
advertisement remains fail-closed until output-family cardinality is represented
at capability-selection time.

| Vulkan set | Owner at this boundary | Wrap status |
|---:|---|---|
| 0 | Existing ordinary/auto-uniform path | Preserved; not owned by the advanced runtime. |
| 1 | Visibility and pass-local resources | Implemented for one immutable mono preparation/raster/late family, including per-operation late descriptors and persistent history. Stereo and multiple independently selected output families remain unsupported. |
| 2 | Advanced sampled-image and separate-sampler arrays | Implemented as one runtime-owned fixed-capacity set per frame slot. |
| 3 | Advanced canonical storage tables | Implemented as one runtime-owned set per retained publication. Frame, view, pass, draw, instance, mesh/geometry, transform, deformation, render-state, material, global-resource, encoded-reference, layout, kernel, constant, binding, and lookup slices are real. |

**Completed through the final implementation slice:**

- Vulkan shader preambles publish an exact fixed resource-descriptor capacity,
  so SPIR-V array counts and the runtime-owned set-2 layout cannot silently
  disagree.
- Link-time ABI validation requires the complete advanced signature and exact
  table/resource descriptor types and counts. The full three-binding signature
  prevents unrelated legacy high binding numbers from being misclassified.
- The advanced resource runtime owns both layouts, preallocates bounded global
  sets, writes all 25 canonical table bindings without per-frame heap growth,
  and carries both native sets in the publication state.
- Prepared mesh recording requires a valid retained canonical publication for
  any program that opts into those externally owned sets, then binds set 2 and
  set 3 from that exact publication. Failure is explicit; it cannot silently
  substitute legacy descriptors.
- Legacy descriptor allocation, writes, draw-slot invariance checks, and
  resource/binding fingerprints ignore externally owned advanced sets.
- Production compute, indexed-raster, and mesh-raster shader/program families
  compile against the exact set 1/2/3 ABI. Stable bins resolve all five strategy
  lanes before sealing; zero-readback lanes issue count-indirect commands and
  instrumented lanes attach only the bounded asynchronous diagnostic sidecar.
- Set-1 allocation is exact and rollback-safe. One preparation/raster/late trio
  shares one immutable family seal; mutable extraction content has its own
  monotonic generation and is validated before and after upload.
- All advanced descriptor batches pass through descriptor lifetime authority.
  Per-operation late image-view closures therefore remain pinned through the
  recorded/submitted command lifetime rather than relying on interner references.
- Resident delta patching is allowed only for the same canonical database epoch
  and a table sequence covered by the retained journal floor. An owner whose
  retained native capacity still fits but whose epoch/journal proof changed is
  fully rewritten in place at the completed-slot boundary; actual capacity growth
  triggers a transactional all-owner packed rebuild before the first immutable
  entry. Later same-generation publications remain copy-on-write.
- Unchanged resident table owners retain their frame-slot image and advance only
  the applied publication stamp. The logical lookup image is likewise resident:
  twelve fixed owner segments retain stable shader offsets, and only owners whose
  lookup generation changed are cleared/copied. The retained mapped Vulkan ranges
  are the sole data authority; the former 27 managed CPU mirrors and their
  completed-boundary `Array.Resize` operations are gone. Exact table rows, byte
  ranges, lookup segments, and truncation tails open exact write sub-slices, and
  every reuse predicate proves the retained native byte capacity before publishing.
- Resident owners and lookup segments are allocated before fallback/view/frame/
  encoded-reference transients, producing one deterministic prefix. The sealed
  plan is all-retain or all-rebuild, charges the initial alignment pad, and checks
  the aligned mapped-arena cursor against the planned end before descriptor
  publication. Empty owners keep a valid one-element sentinel slice while their
  logical count remains zero.
- Arena rollback is now a transaction-integrity boundary. Failed cursor restore
  quarantines the affected advanced-scene or visibility frame slot and reports an
  explicit transaction failure; it cannot clear resident metadata and continue
  with unknown allocation state.
- The late raster stage may reuse the raster pipeline only when both stages seal
  the same exact dynamic-rendering target closure. Legacy clear/load render-pass
  handles remain fail-closed before command-buffer recording.
- Sealed stable submissions resolve flat ABA-safe command and image-subresource
  slots on the normal path; dictionaries remain only in cold/full/sampled
  validation. Native dependency invalidation is transitive across registered
  pipeline, descriptor, shader, render-pass/output, and command-artifact edges.
- Renderer-neutral, Vulkan, and isolated validation-only editor builds pass with
  zero warnings/errors, and all six advanced shaders compile. No automated test
  was added or run under the repository's explicit-clearance policy.
- Live Vulkan validation exposed and closed two ordinary-frame authority defects:
  non-promoted scheduled mesh secondaries no longer demand an unavailable
  advanced canonical publication, and stable-bin copy compares ordered-exception
  count against fixed capacity rather than the just-cleared current count. The
  corrected session completed frames without validation/transaction/capacity
  failures and produced distinct textured readbacks from two camera positions.
- The final rebuilt `phase23-final-0828` session completed through publication
  3629 after the resident-prefix/dirty-write closeout. The sampled profiler
  reported three packages prepared, two published and consumed, zero package or
  output rejection, zero forbidden CPU fallback, zero submission managed bytes,
  zero resident-template capacity/dependency rejection, and zero Vulkan validation
  messages. Log filtering found no transaction-integrity, storage-capacity,
  quarantine, frame-plan-capacity, renderer-pause, VUID, device-loss, OOM, or
  exception record. Camera-separated Vulkan readbacks at `(0,6,18)` and `(12,6,0)`
  again showed distinct textured geometry. This is an ordinary-frame smoke, not
  an advanced-family promotion result.
- `EAdvancedShaderFamily` intentionally remains `None` globally. The realized
  path admits exactly one mono family per primary plan, while current capability
  selection cannot reserve that family for one output or distinguish a second
  mono mirror/capture/offscreen selection. Advertising it globally would allow a
  configuration that fails only at primary preflight.

**Implementation boxes reconciled in this wrap:** Phase 3.2 now checks the
complete SoA image and shared upload substrate; Phase 3.4 checks numeric bins,
intrusive membership, immutable manifests, target lowering, and exact exception
retention; Phase 3.5 checks the resolver plus CPU-direct/indexed zero-readback and
instrumented indexed lanes; and Phase 3.6 checks the bounded asynchronous sidecar
implementation. Mesh-task lane completion, rendered parity, strict readback
evidence, output policy, and dirty-range promotion proof remain unchecked.

**Remaining promotion and empirical gates:**

1. Add output-aware advanced-family reservation or bounded multi-family state,
   then promote the mono shader-family capability. Add a true `gl_ViewIndex`/
   layer-specific indirect/raster ABI before advertising stereo.
2. Run material/resource/ordering/output parity for `CpuDirect`, CPU indirect,
   GPU indirect, mesh-task indirect, and fully GPU-driven lanes. Keep the
   canonical and legacy feeds side by side until all five pass.
3. Prove shadow, explicit-output, OpenXR, mirror, capture, and external-output
   policy on the same canonical residency/bin substrate, including strict
   zero-readback and diagnostic saturation/retirement evidence.
4. After parity, remove live `BackendReadyMeshSelection` arrays and legacy
   `GPUScene` storage/ID maps, then close the dependent Phase 2 reverse-
   dependency and exact-mutation gates.
5. Measure the frozen Release sealed-hit gateway percentile, run the exact local
   mutation/zero-broad-fallback matrix, and complete hardware/OpenXR coverage.
6. Separately close the known startup framebuffer-backing race and the five-
   image-view/one-pipeline-layout teardown debt under Standard Validation.
7. Fix the retained-prefix false-rejection edge: preflight both retain and exact
   compact footprints, select a cold all-owner rebuild when historical retained
   capacity plus transients exceeds the 8 MiB lane but current compact data fits,
   and compact lookup segment capacities instead of preserving obsolete maxima.
8. Raise or redesign the fixed dirty-range set (`MaxRanges == 8`), or publish an
   explicit collapse reason/counter and require zero collapses for promotion.
   Today a ninth disjoint exact write conservatively coalesces the chunk to one
   broad flush, so the implementation is memory-safe but the proportional-dirty
   promotion claim remains unchecked.
9. Skip journal deltas whose publication generation is already at or below the
   resident's applied sequence before issuing native row writes. Metadata already
   ignores them, but the mapped write path currently dirties those old rows again.
10. Repair async texture-source republishing/parity. The final live run repeatedly
    rejected canonical texture `1:1` because retained metadata still described
    the 64x64, one-mip import placeholder after `sponza_thorn_diff` became 256x256
    with nine mips. The fail-closed ordered legacy selection is correct, but this
    `SourceMismatch` prevents native dual-feed parity and canonical cutover.

#### 3.2 Frequency-Owned Structure-of-Arrays (SoA) Data
- [x] Complete SoA streams: frame constants; view matrices/frusta/jitter; pass constants; material blocks and resource tables; object transforms/bounds/IDs; instance, skinning, deformation, and visibility ranges; geometry identities/offsets/formats; and bin indirect buffers.
- [x] Ensure the same uploaded SoA records feed CPU direct, GPU indirect, and GPU meshlet strategies without repacking.
- [ ] Stream dirty ranges to persistently mapped or staging memory without locks.

#### 3.3 Direct-Slot `VulkanDrawTemplateTable` & Native Leases
- [x] Implement direct-slot lookup via `VulkanDrawTemplateHandle(Slot, Generation)` derived from canonical draw + sealed pass/pipeline variant, with full structural comparison only on create/replace.
- [x] Implement transactional native dependency acquisition in `VulkanDrawTemplateDependencySet` (program, pipeline, geometry, material table leases).
- [x] Separate content, table, topology, and recording generations and key strategy/instrumentation variants explicitly.
- [x] Evict slots on exact draw-owner deltas; retire native handles only after exact GPU completion authorities release their generation pins.
- [x] Keep texture/sampler ownership in material/resource tables and frame/swapchain/OpenXR target leases in frame scope; resident templates retain neither ownership domain.

#### 3.4 Stable Bins & Bin-Level Resource Manifests
- [x] Build numeric `VulkanRenderBinKey` (pass compatibility, pipeline variant, geometry page/index type, topology/state, descriptor model, view mask, ordering class).
- [x] Maintain bin membership with slot-indexed intrusive arrays; update membership only on topology changes.
- [x] Replace per-draw `FrameOpResourceUseList` lowering with immutable `VulkanTemplateResourceManifest` and `VulkanBinResourceManifest`.
- [x] Lower target-dependent pass state only after context coalescing.
- [x] Preserve compact ordered exception streams for transparency, UI, callbacks, and unsupported custom work; every legacy draw reports an exact retained reason.
- [ ] Keep a direct-draw parity mode and a CPU-built indirect scaffold that compare template IDs, draw parameters, material/object indices, order, and rendered output without becoming a fallback for GPU zero-readback paths.

#### 3.5 Unified 5 Submission Strategy Lanes
- [x] Maintain `EMeshSubmissionStrategy` resolver before plan sealing; never resolve dynamically in workers.
- [x] **`CpuDirect`:** Canonical handles feed direct draws or CPU-built indirect parity streams.
- [x] **`GpuIndirectZeroReadback`:** Canonical records feed GPU culling and fixed compact `vkCmdDrawIndexedIndirectCount` ranges (zero readbacks, mappings, or CPU fallbacks).
- [x] **`GpuIndirectInstrumented`:** Same GPU indirect inputs with explicit diagnostic sidecar; planned CPU safety-net draw only when explicitly configured.
- [ ] **`GpuMeshletZeroReadback`:** Canonical records feed GPU mesh-task generation and `vkCmdDrawMeshTasksIndirectCountEXT` (zero readbacks or fallbacks).
- [ ] **`GpuMeshletInstrumented`:** Same meshlet stream with explicit diagnostic sidecar.
- [x] Resolve capabilities, downgrade reasons, crossovers, and any explicitly allowed instrumented CPU safety net before sealing; never silently change a sealed strategy in a worker.
- [ ] Prove source/output capacity from resident counts and declared worst-case expansion before dispatch; clamp unexpected GPU overflow for memory safety and report it asynchronously without same-frame retry.
- [ ] Preserve shadow, explicit-output, OpenXR, mirror, capture, and external-output pass policy over the same canonical scene residency and bins.

#### 3.6 Asynchronous `GpuDiagnosticReadbackPlan` Sidecar
- [x] Represent diagnostic requests as immutable `GpuDiagnosticReadbackPlan` nodes attached only to instrumented passes.
- [x] Copy diagnostic data into a fixed-capacity host-visible staging ring after producer completion.
- [x] Poll completion non-blockingly at frame retirement; decode on general/telemetry worker domain.
- [x] Ensure render workers never block, spin, or wait on diagnostic GPU fences.
- [x] Drop and count requests on ring saturation without stalling or altering render output.
- [x] Ensure diagnostics never influence later strategy resolution, capacity, visibility, binning, cache generations, or output; disabled diagnostics create zero ring, command, decoder, or pipeline work.
- [ ] Strict zero-readback evidence requires `GpuReadbackBytes == 0`, 0 buffer maps, and 0 readback-caused waits.

#### 3.7 Optional Capability Tiers (Post-Baseline Only)
- [ ] Benchmark stable descriptor sets/dynamic offsets against descriptor indexing on NVIDIA, AMD, and an available integrated GPU.
- [ ] Prototype `VK_EXT_descriptor_heap` and `VK_EXT_device_generated_commands` only on advertising drivers and only where measured CPU/GPU/tooling results beat the portable resident/MDI path.
- [ ] Keep buffer-device-address geometry fetching and mesh shaders capability-gated; no optional tier is a prerequisite for the baseline or promoted from one vendor/result.

---

### Phase 4 - Concurrency Closure & Multi-Lane Render Work Pool

**Goal:** Centralize process thread budgets, eliminate worker oversubscription, provide zero-allocation pooled batches, and migrate command recording to lane-affine render workers.

#### 4.1 Execution Topology & Thread Budget
- [x] Centralize foreground reservations, general/render domains, retained compiler/Vulkan/OpenXR workers, and other dedicated lanes in immutable `EngineExecutionTopology` diagnostics.
- [x] Reject explicit configurations that oversubscribe processor count after foreground and dedicated-lane reservations.
- [x] Implement deterministic startup auto-sizing with render-thread participation and no hidden worker when a domain resolves to zero.
- [ ] Eliminate `RuntimeEngine.Jobs` and independent worker pools. Driver-blocking pipeline compilation remains a topology-owned below-normal background domain until it can be safely budgeted; it never occupies a render-critical lane.

#### 4.2 Allocation-Free Pooled Render Batches
- [x] Implement pooled, generation-checked batch/item storage, stable lane IDs, dependencies, cancellation, bounded teardown, render-thread participation, and backend attachment registration in `EngineWorkScheduler`.
- [x] Dispatch renderer-neutral batches through `IRenderWorkExecutor` without one managed `Task` or job object per item.
- [x] Use bounded queues with inline execution, lane affinity, and work stealing for eligible preparation work.
- [x] Ensure idle workers block on signal-only waits (no periodic polling wakes).
- [x] Fault batches atomically and quarantine the domain on worker exceptions.
- [ ] Prove build/rent, dispatch, execute, and merge allocate zero managed bytes after warmup; do not infer this from functional scheduler completion.
- [ ] Bound preparation to at most $4 \times (\text{renderWorkers} + 1)$ migratable tasks per phase; dispatch only with at least two independent tasks and predicted savings greater than measured queue + wake + merge cost plus hysteresis.

#### 4.3 Multi-Lane Vulkan Command Recording
- [ ] Attach transient command pools and retained-artifact arenas per logical render lane, frame slot, and queue family; reusable artifacts must never live in a transient-reset pool.
- [ ] Replace persistent command-chain thread array and OpenXR eye threads with render-domain lane-affine tasks.
- [ ] Enforce measured coarse-task rules: never dispatch fewer than 10 draws/dispatches per secondary, target at least 32 where it wins, and cap secondaries per scope at $2 \times (\text{renderWorkers} + 1)$.
- [ ] Dispatch only immutable prepared ranges; workers never traverse live materials, renderers, callbacks, or mutable planner state.
- [ ] Inline small batches directly on the render thread.
- [ ] Merge secondary command buffers in canonical bin/range order independent of worker completion order.
- [ ] Allow adjacent bins to share a secondary only when render scope, inheritance, query, ordering, and queue-family contracts match.
- [ ] Keep one reusable artifact instance per in-flight slot unless exact completion proves the prior instance is no longer pending.

#### 4.4 Hot-Path Allocation & Interference Closure
- [ ] Zero managed heap allocation during steady-state build, dispatch, execute, merge, submit, and present.
- [ ] Replace dictionaries, LINQ, and closures with pre-sized arrays, spans, and struct enumerators.
- [ ] Throttle background compiler and editor jobs during high-refresh active rendering.
- [ ] Verify zero unexplained worker wakeups or lock waits $>0.1$ ms.

---

### Phase 5 - Render Graph Simplification, Streaming, & Tail Latency Bounds

**Goal:** Reduce GPU deadline pressure, eliminate full-resolution copy passes, bound directional cascade and streaming spikes, and ensure safe swapchain recreation.

#### 5.0 Deadline-Aware Output Scheduling
- [x] Build one output manifest/DAG for uploads, shadows, desktop, OpenXR eyes, mirror, probes, captures, and publication; reserve acquired OpenXR critical work before optional outputs.
- [x] Use bounded, observable cadence/deferral/stale-reuse policy for optional work, narrow queue-lock ownership, and frozen modal-resize presentation packages.
- [ ] Complete long-duration, performance, interactive-resize, and multi-output acceptance in the validation matrix.

#### 5.1 Render Graph & GPU Pass Stabilization
- [x] Preserve the implemented complete-scene normal/depth target (deferred attachment 1 + depth) with one forward opaque/masked overlay and no contact-copy/merge replay pair.
- [x] Execute the depth/normal path only when visible materials and active AO/contact-shadow consumers require it.
- [x] Eliminate the implemented redundant G-buffer restore/contact-copy pairs and full-resolution merge replays through declared graph transitions.
- [ ] Cache compiled render graph; recompile only dirty subgraphs on local mutation.
- [ ] Batch barriers by stage/access; replace broad `AllCommands` barriers with precise masks; coalesce adjacent subresource transitions.
- [ ] Keep physical attachment aliasing fail-closed until asynchronous lifetime proof exists; then A/B transient aliasing/lazy allocation only for proven non-overlapping targets.

#### 5.2 Bounded Shadows, Probes, & Occlusion
- [ ] Define directional-cascade invalidation from camera, light, caster, receiver, atlas, and quality state; stabilize projections, reuse unaffected recording/data, and enforce a bounded update budget with explicit temporal policy.
- [ ] Share GPU shadow records across all material kernels instead of large uniform arrays.
- [ ] Stagger reflection probe and environment capture refreshes across frames.
- [ ] Instrument occlusion candidates, occluders, tested/rasterized/rejected bounds, query age, Hi-Z build/test cost, CPU/GPU time, and false-positive/negative diagnostics in representative open, moderate, occluder-heavy, masked, static, and moving scenes.
- [ ] Bound CPU software-occlusion candidate selection/sort/rasterization; define query latency/refresh/stale-result/camera-motion policy and bypass when estimated benefit cannot exceed cost.
- [ ] GPU Hi-Z occlusion: persistent minimal-format Reverse-Z resources, one or two reduction/test dispatches, zero per-mip host work, measured crossover thresholds, visibility hysteresis, conservative bypass on camera cuts, and current-frame visibility kept on GPU.
- [ ] Retain forced modes and a conservative no-occlusion fallback for diagnosis; do not promote any mode without measured crossover and visual parity evidence.

#### 5.3 Asynchronous Texture Streaming & Pipelines
- [ ] Keep texture decode, transcode, and mip prep on background workers.
- [ ] Coalesce uploads into bounded transfer submissions; reserve foreground staging ring capacity.
- [ ] Stream textures larger than staging ring in bounded chunks.
- [ ] Publish texture generations at deterministic frame boundaries with narrow descriptor updates.
- [ ] Meter decode/prep, staging copy, Vulkan allocation, transfer recording/GPU, descriptor publication, queue age, and bytes/items; keep bursts within explicit publication/retirement budgets.
- [ ] Prove one material scalar and one texture/sampler replacement update only their dependent ranges with zero stable per-draw descriptor validation or writes.
- [ ] Bound stable material/descriptor-table growth with spare capacity, asynchronous staging/publication, and only a visible counted emergency wait.
- [ ] Precompile common pipelines during warmup; persist `VkPipelineCache` keyed by GPU, driver, engine revision, render-target mode, and shader fingerprint.
- [ ] Never synchronously compile pipelines on the render thread during steady state.

#### 5.4 Resource Retirement & Swapchain Lifecycle
- [ ] Meter destruction by resource class (images/views, buffers, pipelines, framebuffers, samplers, descriptors, command artifacts, callbacks) with per-frame caps and a reported high-water memory-safety drain policy.
- [ ] Destroy retired resources outside global retirement locks.
- [ ] Retire resources only after all relevant queue timeline values or fences complete.
- [ ] Asynchronous swapchain-generation retirement: coalesce resize events, create replacement generation from newest extent, and tombstone old generations.
- [ ] Keep one command pool per recording lane/frame slot, reset it only after exact completion, allocate no warmed command buffers, and preserve the separate dynamic ImGui overlay command buffer.
- [ ] Bound concurrent old/new swapchain generations, inherit the strongest prior completion authority for reused mapped frame-data storage, and retire secondary ImGui swapchains independently.
- [ ] Zero normal-frame `vkDeviceWaitIdle` during resize, minimize, restore, or swapchain recreation.

---

### Phase 6 - OpenXR Asynchronous Decoupling & Lifecycle Hardening

**Goal:** Decouple OpenXR submission and swapchain retirement from render-thread fences, eliminating the historical 70–100 ms eye-submit wait while preserving application and runtime safety.

#### 6.1 Current OpenXR Lifetime Contract Map
- [ ] Identify every resource whose safety currently depends on the synchronous post-submit wait: eye command buffers/pools, frame-data and descriptor arenas, staging ranges, image views/framebuffers, resident/native pins, transient graph resources, and acquire/release state.
- [ ] Record eye submit/completion wait, forced waits, in-flight count/age, image reuse age, missed deadlines, and the last producer/completion authority.
- [ ] Verify Monado and at least one hardware runtime; explicitly determine release-before-application-completion legality, timeline-semaphore observability, fence-ring requirements, and the bounded fallback when a runtime requires completion before release.
- [ ] Do not assume an application timeline semaphore/fence is visible to the OpenXR runtime unless the active graphics binding and runtime contract explicitly establish that visibility.

#### 6.2 `OpenXrVulkanSubmissionTracker`
- [ ] Implement bounded tracker keyed by engine frame ID, display time, swapchain image, command pools, arenas, descriptors, staging, and completion primitives.
- [ ] Submit eye work and return immediately without waiting for GPU completion.
- [ ] Register ownership payload atomically upon submission.
- [ ] Poll completion non-blockingly at the start of subsequent frames before recycling pools or arenas.
- [ ] Keep the in-flight bound explicit; use only a short counted recovery wait after every safe reuse/defer path is exhausted, and count late/missed/reprojected frames.

#### 6.3 Non-Blocking XR Frame-Loop Integration
- [ ] Preserve `xrWaitFrame` as the XR pacing gate; keep `xrBeginFrame`, acquire, render, release, and `xrEndFrame` ordered correctly.
- [ ] Build view-independent visibility, materials, and plans once per XR frame; publish compact per-eye / multiview records.
- [ ] Use multiview/single-pass stereo only when supported and semantically correct.
- [ ] Keep desktop swapchain acquisition non-blocking while OpenXR owns the frame deadline.
- [ ] Route forced waits into bounded retirement release authorities with explicit telemetry counters.

#### 6.4 OpenXR Swapchain Recreation & Deferred Destruction
- [ ] Detect recommended dimension changes through runtime event/query policies.
- [ ] Tombstone old swapchains and dependent Vulkan views with the highest application completion value.
- [ ] Track both application GPU completion and OpenXR runtime release before destruction.
- [ ] Create replacement swapchain without device-wide idle when overlapping swapchains are supported.
- [ ] Bound retired generations and publish a visible fallback when the bound is reached; do not infer a resize solely from session-state events.
- [ ] On `XR_SESSION_STATE_STOPPING` / `LOSS_PENDING`, drain outstanding work safely before destroying devices.

---

### Phase 7 - Advanced Render Pipeline Modernization (Phases 06 Through 10)

**Goal:** Transition from the classic G-Buffer / Forward+ hybrid to the backend-neutral Advanced Render Pipeline: OpenGL and Vulkan share logical visibility, material, view, resource-generation, and output contracts; Vulkan alone owns its hardening and native encoding. Deliver GPU material work classification, native opaque shading, clustered lighting, visibility-driven transparency/post, and multi-view integration.

#### 7.1 Classify Visible Material Work on the GPU (ARP 06)
- [ ] Select tile dimensions from measured occupancy; define mono and per-eye addressing.
- [ ] Define bounded records/capacities for active tiles, kernel-tile membership, and optional compact pixels from screen-size and worst-case diversity; exclude empty/background pixels explicitly.
- [ ] Classify visible pixels by shading kernel, material layout, coverage class, derivative mode, and view mode without atomics proportional to total registered materials; material-row ID is data and descriptor-set object identity is never a classification key.
- [ ] Build active tiles and per-kernel tile membership; use subgroup ballot/scan with bounded shared-memory fallbacks.
- [ ] Construct indirect dispatch arguments entirely on the GPU; compact kernel/tile/pixel ranges and publish only resource-specific barriers.
- [ ] Keep many material rows sharing common kernel dispatches, order kernels to reduce pipeline changes, prewarm engine-owned variants, and define pending/rare/custom kernel behavior.
- [ ] Handle each capacity independently; clamp safely, never drop pixels silently, use conservative full-tile recovery in automatic mode, and surface structured failure in required mode.
- [ ] Add capture-stable resource names and views/counters for tile, kernel, material, mixed-density, overflow, recovery, and per-eye classification cost.

#### 7.2 Native Opaque Shading, Clustered Lighting, & Shadows (ARP 07)
- [ ] Implement standard opaque and masked PBR kernels receiving `AdvancedSurface`, material rows, view records, light ranges, and shadow tables.
- [ ] Define the material-family kernel interface, texture-table access, output contract, missing/pending/invalid-layout fallback, permutation budget, and standard opaque/masked/unlit/emissive priority order.
- [ ] Shade directly into native opaque HDR, dense velocity, and temporal/reactive sidecars; eliminate classic G-Buffer and light-combine passes.
- [ ] Clustered lighting: backend-neutral froxel grid (screen-tile X/Y, depth-slice Z) with GPU-built point/spot lists, bounded directional list, overflow recovery, and occupancy diagnostics.
- [ ] Publish directional/point/spot/cascade/atlas/filter/fallback GPU shadow records; consume them via unified convention-safe sampling with machine-readable missing/stale fallback reasons.
- [ ] Advanced Ambient Occlusion: adapt supported AO providers to final visibility depth + reconstructed normals.
- [ ] Per-tile/froxel decal lists applied as material/surface modifiers before lighting.
- [ ] Publish IBL/probes through shared GPU records and a narrow `IAdvancedGlobalIlluminationProvider`; select one contributing GI mode unless an explicitly authored composition mode exists.
- [ ] Shade visibility-sentinel pixels through the selected sky/background contract with explicit clear/alpha/HDR/capture behavior; keep custom background geometry as an explicit compatible lane.
- [ ] Add reconstructed material/lighting/shadow/AO/GI diagnostic views, an optional difference view against the legacy pipeline, stable capture names, and per-family GPU timings.

#### 7.3 Transparency, Special Passes, & Post Chain (ARP 08)
- [ ] Define explicit late-pass metadata and reject advanced-compatible opaque/masked work that attempts to use legacy `OpaqueForward` / `MaskedForward`; required unsupported work renders an observable error surface.
- [ ] Classify late draws: sorted alpha, participating transparency, refraction, weighted blended OIT, PPLL, depth peeling, volumetrics, special effects, on-top overlays, and UI.
- [ ] Publish native opaque HDR and visibility depth as the base; create a scene-color snapshot only when visible refraction/feedback requires it and never sample an attachment while writing it without a legal feedback path.
- [ ] Port OIT paths with declared capacities/overflow diagnostics and no same-frame readback recovery; preserve light/shadow/probe/fog access through shared tables.
- [ ] Give water, hair, particles, trails, beams, portals, mirrors, and geometry-displacing effects explicit compatible or special lanes with editor-visible unsupported reasons.
- [ ] Atmosphere and volumetric fog adapted to visibility depth and native HDR.
- [ ] Dense motion vectors: merge reconstructed opaque velocity with participating transparent velocity; generate reactive/disocclusion masks.
- [ ] Reconnect temporal accumulation, motion blur, DoF, bloom, tone mapping, color grading, TSR, and vendor upscalers to advanced resource names.
- [ ] Reset temporal/history state explicitly for resize, pipeline switch, camera cut, view-count, render-scale, HDR/format, shader generation, and resource-generation replacement.
- [ ] Add pass/category overlays and views for scene-color snapshot, OIT accumulators, refraction, fog, motion/reactive masks, history validity, and late-pass capacity/recovery.

#### 7.4 Stereo, Multiview, & Editor View Integration (ARP 09)
- [ ] Specialize immutable `ViewSetPlan` with view count, layer mapping, jitter, region, per-view resources/history, and explicit conservative union rules only for genuinely shared work.
- [ ] Layered visibility, depth, barycentrics when enabled, HDR, velocity, and post histories for RVC two-pass, OpenGL single-pass stereo, and Vulkan multiview; never reuse one eye's occlusion verdict for another.
- [ ] Preserve OpenXR predicted-pose, late-latching, motion, camera-cut, deadline, and swapchain contracts; define foveated/variable-rate visibility and shading with conservative peripheral derivatives/LOD.
- [ ] Offscreen views (mirrors, portals, probes, thumbnails, depth/visibility-only captures) consume advanced capability-based profiles without executing unrequested main-view post work.
- [ ] Resolve transform/component/mesh-section/material/primitive/meshlet identity; implement asynchronous picking/GPU selection and preserve outlines, hover, gizmos, bounds, icons, physics debug, and on-top overlays.
- [ ] Add editor inspection, MCP-visible mode/capability/fallback state, viewport screenshot support, stable capture names, and RenderDoc-friendly annotations/resources for every major phase.

#### 7.5 Production Cutover & Program Completion (ARP 10)
- [ ] Begin cutover only after correctness, stability, performance, allocation, readback, desktop, offscreen, and XR evidence passes for the affected profile.
- [ ] Make `AdvancedRenderPipeline` the desktop/applicable-offscreen default and retain production OpenXR eye ownership in `RvcRenderPipeline`; route compatible opaque/masked work through visibility plus native shading.
- [ ] Remove the advanced graph's classic G-Buffer, deferred-light accumulation, ordinary opaque Forward+, light-combine stages, and all `DefaultRenderPipeline2` selectors/aliases.
- [ ] Meet the target architecture's facade, lifecycle spine, dependency direction, source organization, canonical-layout, allocation, unsafe-code, and single-authority budgets with a reproducible final inventory.
- [ ] Prove cost was not moved into waits, descriptors, retirement, another output, GPU regression, or tail latency, and that a developer can explain a slow frame from the correlated lifecycle tree.
- [ ] Execute deletion, documentation, evidence publication, and archival through Phase 9 only after these gates pass.

---

### Phase 8 - High-Refresh Promotion Gates & Full Validation Matrix

**Goal:** Prove performance, cadence, lifetime, and visual parity across a comprehensive multi-machine and multi-scenario validation matrix on one frozen integrated implementation.

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
- [ ] Normal production captures contain no current-frame readback, mapping, host completion wait, or `vkDeviceWaitIdle`.
- [ ] Zero managed hot-path heap allocations after warmup.
- [ ] Zero per-draw material reconstruction, descriptor validation, or command-signature rebuilding.
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
- [ ] Add contract tests proving `PresentNow` results cannot be `Deferred` or report `PresentedNew` without matching submit serials.
- [ ] Exercise scheduling capacities 1, 8, 32, and production values.
- [ ] Reproduce the observed 221-request and 836-request visibility shapes and exercise bounded accepted-frame/UI/main-scene/shadow lane overflows.
- [ ] Inject slow pipeline compiles, chunked large uploads, staging overflow, shader compile failures, descriptor exhaustion, frame arena overflow, host/device OOM, device loss, and timeline stalls.
- [ ] Prove uploads larger than the staging ring complete by chunking and that foreground reserve publishes the requested number of distinct allocation generations.
- [ ] Mutate camera, transforms, materials, and lights during blocked preparation; verify exactly one captured epoch is submitted.
- [ ] Saturate background uploads, compilation, and shadows; verify zero foreground starvation.
- [ ] Exercise failed acquire/submit/present, pause/resume, repeated start/stop/shutdown, diagnostic-ring wrap/full/late/generation-mismatch completion, and device loss with pending diagnostic slots.
- [ ] Run long warm soaks verifying zero managed allocations and bounded pool high-water marks.

#### 9.3 Production Cutover
- [ ] Make `AdvancedRenderPipeline` the desktop and applicable-offscreen default only after those gates pass; promote the RVC-owned OpenXR eye path only after its matching XR gates pass.
- [ ] Update engine settings, schemas, launch profiles, and unit-testing-world configurations.
- [ ] Remove development selectors, `DefaultRenderPipeline2`, and temporary environment variables.

#### 9.4 Legacy Architecture Deletion
- [ ] Delete `VulkanPreparedMeshOperationCohort` and `VulkanPreparedMeshIngress`.
- [ ] Delete duplicate `GPUScene` / `HybridRenderingManager` arrays and ID maps.
- [ ] Delete `RuntimeEngine.Jobs`, separate command-chain workers, and dedicated OpenXR eye threads.
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
* **Foreground Plan & Failure:** `AcceptedFrameId`, `AcceptedEpoch`, `OutputGeneration`, `PresentWorkClass`, `ReadinessPolicy`, `FreshSubmitSerial`, `FramePlanCapacityLane`, `FramePlanCapacityConfigured`, `FramePlanCapacityRequired`, `FramePlanCapacityAccepted`, `FramePlanCapacityRejected`, `ForegroundReserveRequested`, `ForegroundReserveDistinctSlices`, `TerminalStage`, `TerminalFailureKind`.
* **Device & Context:** device state/loss count, device-fault payload, TDR risk, memory budget, last successful submission breadcrumbs, context/display/internal extent/registry/resource-generation mismatches, and structured frame-rejection reason.
* **Presentation & Pacing:** `PresentationProfileRequested`, `PresentationProfileResolved`, `PresentMode`, `TargetRefreshHz`, `TargetFrameIntervalMs`, `ActualPresentIntervalMs`, `FramesAhead`, `LimiterSleepMs`, `LimiterSpinMs`, `AcquireMs`, `AcquireUnavailableCount`, `PresentQueueAdmissionMs`, `NativePresentMs`.
* **Frame-Slot & Completion:** `FrameSlotWaitMs`, `FrameSlotWaitQueue`, `FrameSlotWaitTargetValue`, `FrameSlotWaitCompletedValue`, `FrameSlotWaitAgeFrames`, `SwapchainImageWaitMs`, `CommandPoolReuseWaitMs`, `DescriptorArenaReuseWaitMs`.
* **Residency & Templates:** `ResidentDirectHits`, `ResidentColdMisses`, `ResidentReplacements`, `ResidentLocalInvalidations`, `ResidentBroadInvalidations`, `ResidentBroadInvalidationEntries`, `ResidentInvalidationReason`, `CanonicalDirtyOwnerCount`, `CanonicalDirtyRangeBytes`, `LegacyCompatibilityVisits`, canonical counts/capacities/duplicate bytes, topology/data deltas, template creates/rebuilds/generation mismatches/hash collisions/lease failures/evictions/retirements, and compatibility draws by reason.
* **Submission Gateway:** `SubmitImageContractMs`, `SubmitQueueOwnershipMs`, `SubmitLifetimePinsMs`, `SubmitStateGateWaitMs`, `SubmitQueueGateWaitMs`, `NativeQueueSubmitMs`, `SubmitLifetimePublishMs`, `SubmitImagePublishMs`, `SubmitDiagnosticPublishMs`, `SealedSubmissionHits`, `SealedSubmissionFallbacks`, `SealedSubmissionFallbackReason`.
* **Scheduler & Memory:** requested/resolved counts, active lanes/peak concurrency/thread IDs/QoS, built/queued/stolen/inline/lane-executed/cancelled items, `WorkerWakeCount`, empty wakes, queue-full fallback, faults/timeouts/quarantine, `WorkerQueueAgeMs`, `WorkerExecuteMs`, overlap/imbalance, `WorkerLockWaitMs`, merge cost, high-water marks, managed allocation by build/dispatch/execute/merge stage, `RenderThreadManagedAllocationBytes`, `GcPauseMs`, `PinnedObjectCount`, `OversubscriptionRejectedCount`.
* **Uploads & Streaming:** `UploadQueuedJobs`, `UploadOldestJobAgeMs`, `UploadStagingBytes`, `UploadStagingOverflowBytes`, `UploadCpuPrepMs`, `UploadStagingCopyMs`, `UploadVulkanAllocationMs`, `UploadTransferRecordMs`, `UploadTransferGpuMs`, `DescriptorPublicationMs`, `DescriptorPublicationItems`, `RetirementBacklogByClass`, `RetirementOldestAgeFrames`, deferred count, `RetirementDestroyedByClass`, `RetirementUncappedDrainCount`.
* **Bins, Recording, Render Graph, & GPU:** bin/dirty-bin/membership/manifest/resource counts; indirect buffer bytes/counts and MDI calls; primary/secondary records/reuses/resets/allocations; pipeline/descriptor/vertex/index/draw/submit API counts; `RenderGraphCacheHit`, `RenderGraphRecompiledPassCount`, `BarrierCount`, `BroadBarrierCount`, `OwnershipTransferCount`, `FullResolutionCopyBytes`, occlusion candidate/occluder/test/reject/age costs, `GpuPassP50P95P99`, `GpuFrameP50P95P99`.
* **Strategy & Diagnostics:** requested/resolved `MeshSubmissionStrategy`, capability/downgrade reason, per-strategy pass/draw/task counts, `GpuReadbackBytes`, `GpuReadbackBufferMaps`, query retrievals, `GpuReadbackWaits`, CPU fallback attempts, `DiagnosticRequestsAccepted`, copy bytes, `DiagnosticRingOccupancy`, completion latency/source generation, `DiagnosticDecodedResults`, generation-mismatch discards, `DiagnosticRingFullDrops`, decoder faults, diagnostic-only records/submits, and dormant overhead.
* **OpenXR Subsystem:** `OpenXrEyeSubmitMs`, eye completion-wait time, `OpenXrEyeInFlightCount`, tracker capacity/high-water, `OpenXrEyeOldestAgeFrames`, swapchain-image reuse age/release state, `OpenXrEyeForcedWaitMs`, `OpenXrEyeForcedWaitCount`, `OpenXrSwapchainReleaseDeferredCount`, `OpenXrRetiredGenerationCount`, `OpenXrMissedFrameCount`, `OpenXrLateFrameCount`, `OpenXrReprojectedFrameCount`.

---

## 5. Definition of Done

This master program is complete only when:

1. The desktop Vulkan renderer sustains **120 Hz (p99 $< 8.333$ ms, engineering target $\le 7.5$ ms)** across all required desktop performance-promotion scenarios on the target systems, while the separate correctness/lifetime matrix passes.
2. Actual presentation cadence matches the reported CPU/GPU timing story without hidden burst pacing.
3. Stable frames perform zero managed hot-path allocations, zero per-draw material/descriptor reconstruction, and zero command buffer re-recording.
4. Local mutations invalidate only exact reverse dependencies without whole-table resident clears.
5. Unchanged submission CPU p95 is below $0.25$ ms via `SealedSubmissionContract`.
6. All process execution domains are centralized, non-oversubscribed, and pooled.
7. OpenXR eye submission returns immediately, eliminating the 70–100 ms synchronous wait.
8. `AdvancedRenderPipeline` is the desktop and applicable-offscreen production default, with GPU material classification, native opaque shading, clustered lighting, and visibility-driven post/transparency. Production OpenXR eye output remains owned by `RvcRenderPipeline`, and that path is promoted only after its matching XR gates pass.
9. Standard and Synchronization Validation report zero errors/VUIDs, with no unresolved renderer warning or lifetime ambiguity accepted into closeout.
10. `GPUScene` mirrors, `VulkanPreparedMeshOperationCohort`, obsolete worker arrays, `DefaultRenderPipeline2`, and the original default pipeline are deleted. A temporary opt-in `LegacyDefaultRenderPipeline` may unblock production cutover for one named consumer, but it keeps this master active until its dated deletion gate is complete.
