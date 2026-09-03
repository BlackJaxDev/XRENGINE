# Vulkan Core Frame Loop, Resident Rendering, and High-Refresh Master TODO

Last Updated: 2026-09-01
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
3. [XRENGINE Vulkan Frame Loop Stability And High-Refresh Optimization TODO](optimization/xrengine-vulkan-frame-loop-stability-todo.md) (high-refresh pacing, wait attribution, sealed submission, local invalidation, lifecycle retirement, OpenXR completion, promotion gates)
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
- Normal `PrimaryRecording` attribution spans frame-data manifests, resource and binding prewarm, native command encoding, and recording-result publication; one aggregate number cannot identify whether Vulkan emission is actually expensive.
- Source inspection identifies an open native-encoding hypothesis: primary CPU-direct recording can still enter live renderer/material state, while tracked commands can perform shared bind-state and lifetime bookkeeping per command. These costs must be measured before implementation or promotion claims.
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
5. **Single Canonical Resident Authority:** `AdvancedSharedGpuSceneDatabase` using ABA-safe `AdvancedGpuHandle(Index, Generation)` handles and frequency-owned SoA streams feeding direct-slot `VulkanResidentDrawTemplateTable`.
6. **Stable Bins & 5 Submission Strategy Lanes:** Numeric `VulkanRenderBinKey` and bin-level manifests feeding `CpuDirect`, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, `GpuMeshletZeroReadback`, and `GpuMeshletInstrumented`.
7. **Asynchronous Diagnostic Sidecar:** `GpuDiagnosticReadbackPlan` using a fixed-capacity staging ring with zero current-frame waits, strict zero-readback separation, and general-domain decoding.
8. **Process-Wide Execution Topology:** `EngineExecutionTopology` and pooled `EngineWorkScheduler` owning non-oversubscribed general and render lanes with lane-local command arenas.
9. **Prepared Native Command Encoding:** One immutable backend-ready packet and sealed native-resource manifest feeds primary, secondary, inline, worker, CPU-direct, indirect, and ordered-exception encoders; command-local state and bulk lifetime publication replace per-command global discovery.
10. **Bounded Graph, Streaming, & Tail Work:** Forward+ single normal/depth prepass gating; budgeted cascade updates; asynchronous chunked texture streaming; tombstoned swapchain lifecycle (zero normal `vkDeviceWaitIdle`).
11. **Asynchronous OpenXR Decoupling:** `OpenXrVulkanSubmissionTracker` eliminating the 70–100 ms eye fence-wait with timeline semaphore / fence-ring completion authorities.
12. **Advanced Render Pipeline (ARP 06–10):** GPU material classification, native opaque shading, clustered lighting, visibility-driven transparency/post, and complete legacy retirement.

---

## 2. Checkpoints, Baseline Constraints, & Safety Rules

### 2.1 Current Validated Checkpoints (through 2026-08-30)

- **PresentNow Cold Liveness Validation:** The desktop Vulkan `PresentNow + BlockForExact` path passed an isolated Sponza acceptance run with scheduling capacity forced to 1. The camera swept across 8 exterior, entrance, atrium, upper, and near-wall views. Monotonic progress continued across long shader compilations (~20–21 s) without livelock, renderer pause, old-content replay, or provenance violations.
- **Binary Texture-Cache Dispatch:** Feature-owned binary `XRTexture2D` cache payloads are claimed before generic YAML deserialization. The exact 178,958,379-byte `studio_small_09_4k` cache payload from the failing run loaded through MCP as an `XRTexture2D` with its original-source path intact and no `YamlDotNet`, unresolved-reference, or texture-load failure.
- **Foreground Staging Reserve:** Cold provisioning now creates protected staging entries directly rather than reacquiring an idle entry through the ordinary pool path. Isolated Vulkan evidence reported `configured=4`, `total=4`, `idle=4`, `distinctBuffers=4`, and `distinctGenerations=4`.
- **Build Status:** 0 warnings, 0 errors on targeted Vulkan (`XREngine.Runtime.Rendering.Vulkan.csproj`) and full editor (`XREngine.Editor.csproj`) builds.
- **Measured Performance Baseline:** Clean Release evidence reported render p50 6.959–7.716 ms and p95 8.241–9.175 ms, with Vulkan-frame p50 5.511–5.995 ms and p95 6.410–7.071 ms. One comparable run showed frame-slot wait p50/p95 4.791/7.728 ms and render p50 13.577 ms; an immediate rerun returned slot waits to 0.019/0.029 ms and render p50 to 7.716 ms. These values motivate causal pacing/slot attribution and are not a frozen promotion baseline.
- **Meshlet Prerequisite:** Cleared on 2026-08-22. Cooking, binary caching, and Vulkan EXT indirect-count mesh-task submission are validated.
- **Execution Topology & Scheduler (Phase 4.1):** `EngineExecutionTopology` and `EngineWorkScheduler` own the process general, render, deferred-admission, and remote-dispatch domains. `Engine.Jobs` is the application-facing general API; runtime rendering reaches the same domain through `RuntimeRenderingHostServices.Work.GeneralJobs`. The compatibility facade and lazy thread-pool auxiliary loops are removed.
- **Canonical Publication (Resident Phase 2):** Bounded journals, tombstones, independent mutation domains and dirty ranges, canonical reverse manifests, acknowledgements, ABA-safe handles, retained material/layout/kernel and global-resource payloads, immutable submission records, and frame-slot Vulkan SoA/descriptor realization are implemented. The package publishes exact per-pass shadow/probe coverage, and Vulkan validates the entire retained dependency closure before native realization.
- **Advanced Vulkan Descriptor ABI (Phase 3.1):** The binding-ready ABI is implemented: ordinary uniforms remain set 0, visibility/pass resources remain set 1, advanced sampled-image/sampler arrays use runtime-owned set 2, and advanced canonical tables use runtime-owned set 3. Exact advanced programs are link-time validated, prepared frames bind the retained publication's native sets, and the promoted mono family records real compute/raster phases. Rendered visual parity remains a Phase 8 promotion gate because the current advanced graph terminates in its explicit empty-output diagnostic clear.
- **Vulkan Template Table & 5 Strategy Lanes (Phases 2/3):** Direct-slot template lookup, transactional native generation leases, flat sealed submission receipts, exact reverse invalidation, and all five requested/effective strategy lanes are implemented. The 2026-08-29 five-lane cohort preserved one workload identity with zero VUIDs and zero fallback events; strict zero-readback lanes also reported zero generic readback bytes and buffer maps.
- **Output Scheduling (Phase 5.0):** Deadline-aware output ordering, acquired-eye reservation, bounded optional-output policy, narrow queue-lock ownership, and frozen modal-resize presentation packages passed the desktop, interactive-resize, and strict-SPS/mirror acceptance cohort. Phase 8 still owns integrated hardware, visual-parity, and high-refresh promotion.
- **Forward+ Simplification (Section 6):** Complete-scene normal/depth target from deferred attachment 1 plus depth overlays forward opaque/masked surfaces once; contact-copy pair and merge replays removed.
- **Heavy-Load Phase 0/1 Revalidation:** The final isolated Sponza/Jax-Mitsuki run crossed a 21.679 s exact-readiness frame and continued beyond frame 1100. All 33 rate-limited correlated-tree records completed, none exceeded the frame root, and the stopped logs contained zero accepted-frame rejection, recording deferral, renderer pause, backpressure, device loss, YAML exception, VUID, or validation error. One command-generation mismatch was rejected before Vulkan acceptance while startup shadow-budget settings were restored; the next package was presented normally.

### 2.2 Open Incident Evidence — 2026-09-01 Avatar Readiness and Frame-Operation Accumulation

A local editor session with `jax2026.prefab` exposed a separate transaction and
attribution failure that is not closed by the earlier isolated liveness result:

- The imported avatar produced 52 models, 41 materials, 133 visible mesh
  requests, and 163 textures. `BlockForExact` rejected 344 attempts while the
  required dense texture cohort was pending, and no new desktop image was
  presented for about 343 seconds even though background rendering and
  streaming work continued.
- Readiness returned before `DrainForPrimary`, so operations authored during
  rejected attempts remained queued. When readiness advanced, the accumulated
  queue contained 8,193 main-scene operations and exceeded the fixed 8,192
  arena. Increasing that capacity would conceal the ownership defect.
- Visibility promotion made required ticket `texture-upload:326:3` stale during
  preparation. The transient generation race was classified as
  `RendererTerminal` after about 3.5 ms instead of returning a retry or
  supersession result.
- A later resize published a 2560×1369 resource generation that the paused scene
  renderer never submitted. Depth picking then retried an unwritten generation;
  its one-shot request flag was cleared only after the throwing readback, so the
  same request remained armed and produced 47 copies of the exact error.
- When frames briefly resumed, the 133-mesh cohort reported roughly 320–360 ms
  successful frame intervals. This incident does not attribute that cost to
  native Vulkan encoding, tracked lifetime bookkeeping, descriptor binding, or
  frontend preparation; Phase 1 owns that isolation.

This incident directly supports transactional frame-operation ownership, typed
retry/supersession, planned/published/recorded/submitted generation separation,
one-shot consumer settlement, and finer recording attribution. It is incident
evidence, not a native-command-encoding performance result.

### 2.3 Frame Deadline Budgets

| Refresh Target | Hard Frame Deadline | Engineering Target (p99) |
|---|---:|---:|
| **100 Hz** (Level A) | 10.000 ms | 8.5–9.0 ms |
| **120 Hz** (Level B - Promotion Gate) | 8.333 ms | 7.1–7.5 ms |
| **144 Hz** (Level C - Stretch Gate) | 6.944 ms | 5.9–6.25 ms |
| **165 Hz / 200 Hz** | 6.061 ms / 5.000 ms | Characterization / Long-term target |

### 2.4 Explicit Non-Fixes and Anti-Patterns

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
Phase 4: Concurrency Closure, Multi-Lane Work Pool, & Native Encoding
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

### Active Phase Execution Matrix

| Phase | Subsystem / Focus | Implementation Status | Validation / Gate Status | Current Focus / Action |
|---|---|---|---|---|
| **Phase 0** | Present-Now Frame Readiness | Implemented | Cold liveness smoke passed; 2026-09-01 avatar transaction incident documented | Enforce transaction clearing in Phase 4.5a |
| **Phase 1** | Baseline Characterization & Pacing | Implemented | Contract & telemetry taxonomy active | Run isolation ladder on native encoding |
| **Phase 2** | Sealed Submission & Invalidation | Implemented | Verified (2026-08-28 closeout) | Closed |
| **Phase 3 Core** | Canonical Residency, Stable Bins, 5 Lanes | Implemented | 5-lane Release cohort verified; zero VUIDs | Core closed |
| **Phase 3 Follow-Up** | Common CPU/GPU Resident Submission Contract | **Implemented** | `CpuDirect` converged with resident templates; CPU MDI promotion evaluated | Core data-path unified |
| **Phase 4.1–4.4** | Execution Topology, Schedulers, Recording Lanes | Implemented | Verified; zero steady-state hot-path allocations | Closed |
| **Phase 4.5** | Native Command Encoding Fast-Path Closure | **Implemented** | Verified (Slices 4.5a, 4.5b, 4.5c); zero-lock lane contexts & resident serializer active | Closed; ready for Phase 7 |
| **Phase 5.0–5.3** | Render Graph, Streaming, Shadow Bounds | Implemented | Verified (2026-08-31 headless closeout) | Closed |
| **Phase 5.4** | Swapchain Lifecycle & Resize Continuity | Implemented | Held-drag continuity reopened (3 cross-pipeline gates remain) | Validate held drag in Default/Advanced/Debug |
| **Phase 6** | OpenXR Asynchronous Decoupling | **Implemented** | Complete (2026-09-02; `19027f631`); 70–100 ms fence wait eliminated | Closed; XR hardware matrix in Phase 8 |
| **Phase 7** | Advanced Render Pipeline Modernization | **COMPLETE** | Phase 7.1–7.5 (ARP 06–10) complete & verified | Proceed to Phase 8 (High-Refresh Promotion Gates) |
| **Phase 8** | High-Refresh Promotion Gates (120/144 Hz) | Queued | Final integrated promotion gate | Awaits Phase 7 |
| **Phase 9** | Phase-Local Test Clearance & Legacy Deletion | Recurring / Queued | Phase 9.1 recurring; final deletion at closeout | Active per slice |

---

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

- [ ] Define presentation freshness from accepted frame-data/resource
  generations plus a compatible new-or-reused command artifact generation.
  `PresentNow` alone must not force native scene re-encoding when the artifact
  remains legal and exact completion protects its frame slot.
- [ ] Give every authored frame operation one explicit attempt/accepted-plan
  transaction. Retry, rejection, supersession, and terminal paths must transfer,
  settle, or discard each operation exactly once.
- [ ] Reproduce 344 pre-drain readiness retries and the 8,193-of-8,192 overflow
  shape; prove queued authoring work remains bounded and the eventual accepted
  plan contains only its owning frame transaction.
- [ ] Classify a required preparation ticket that becomes stale during work as
  `RetryFrame` or `Superseded` unless an independently terminal device/resource
  condition exists; never latch `RendererTerminal` from a normal generation
  race.
- [ ] Settle, clear, or explicitly defer one-shot query/callback requests when no
  submitted planner generation exists, including resize while rendering is
  paused; ordinary absence must not create an exception loop.
- [ ] Mutate camera and scene state while preparation is blocked; prove the
  accepted epoch remains immutable and exactly one captured epoch is submitted.
- [ ] Reproduce the observed 221-request and 836-request shapes, then naturally
  exceed one declared main-scene lane and verify a single bounded
  `FramePlanCapacityExceeded` record with configured, required, accepted, and
  rejected counts.
- [ ] Exercise exact OpenXR deadline/fallback behavior on Monado and one hardware
  runtime; desktop evidence does not close the XR contract.
- [ ] Diagnose the RenderDoc 1.44 no-present launch and capture a settled Sponza
  frame with verified bindings and draw order.

**Conclusion:** The isolated capacity-one result remains valid, but the
2026-09-01 avatar incident reopens transactional operation ownership, transient
generation classification, one-shot consumer settlement, and reusable-artifact
freshness semantics. Epoch mutation, declared-capacity overflow, XR deadline
behavior, and RenderDoc capture remain correctness gates for Phases 8–9.
Detailed earlier evidence is retained in
`docs/work/investigations/rendering/vulkan-present-now-frame-readiness.md`.

---

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

- [ ] Capture matched static and moving desktop baselines for `CpuDirect`,
  `GpuIndirectZeroReadback`, and `GpuMeshletZeroReadback`; keep OpenXR baselines
  separate.
- [ ] Capture separate Streamline/DLSS frame-generation promotion evidence.
- [ ] Prove exhaustive attribution for every compute, transfer, submit, present,
  worker, and external-runtime interval above 0.1 ms.
- [ ] Attribute at least 99% of detailed frame-root wall time, identify every gap
  of at least 50 microseconds, and measure observer overhead.
- [ ] Run the frame-slot, Mailbox, FIFO, reduced-resolution, compiler, streaming,
  secondary-window, and editor-diagnostic A/B matrix.
- [ ] Prove every recurring slot wait has an exact producer/timeline owner and
  that uncapped GPU-headroom slot-wait p95 is approximately zero.

#### Native Command Recording Attribution and Isolation

- [ ] Report `PrimaryFrameDataManifest`, `PrimaryPrewarm`,
  `PrimaryEncodingSetup`, `PrimaryOperationLoop`, `PrimaryFinalization`, and
  `PrimaryEndCommandBuffer` separately at p50/p95/p99/max with allocation,
  operation-count, lane, and frame/output identity.
- [ ] Separate secondary encoder wall time, summed worker execution, worker wait,
  merge, and command-buffer-end dependency publication. Do not charge frontend
  preparation or submission validation to native encoding.
- [ ] Count live `VkMeshRenderer.RecordDraw` calls, immutable prepared-draw
  encoder calls, dependency-track attempts, unique native dependencies,
  command-bind-state lookups/lock acquisitions, tracking-batch lock
  acquisitions, descriptor-heap bind attempts/native binds/skips, and native
  Vulkan commands by type.
- [ ] Publish `DependencyTrackAttempts / UniqueRecordingDependencies`. Zero
  allocation and no individual lock wait above 0.1 ms do not close repeated
  per-command bookkeeping.
- [ ] Execute one matched Release isolation ladder with identical scene, camera,
  render graph, output, validation state, and warm caches:
  1. live draw path plus current full tracking;
  2. immutable prepared draw state plus current full tracking;
  3. immutable prepared draw state plus sealed/bulk recording dependencies;
  4. CPU-built indirect ranges over the same resident bins; and
  5. GPU-built equivalent indirect/count ranges.
- [ ] Run the ladder on small, medium, dense, material-diverse, and moving-camera
  cohorts. The A→B delta owns live renderer/material preparation, B→C owns
  recording bookkeeping, C→D owns per-draw Vulkan emission, and D→E owns the
  CPU/GPU visibility-producer crossover.
- [ ] Retain an explicitly diagnostic raw-pinned or sampled-full-validation rung
  only long enough to quantify the safety layer. It may not become a production
  lifetime bypass.
- [ ] Record conclusions as measured findings or source-audit hypotheses. The
  2026-09-01 incident supports attribution and transaction ownership but does
  not by itself prove which native-encoding change will win.

**Conclusion:** The final heavy-load revalidation crossed a 21.679 s cold frame
without losing liveness and achieved 99.9876% sampled attribution after the wait
taxonomy correction. Those are implementation checks, not a frozen promotion
baseline or native-recording attribution result; the matched repetitions,
isolation ladder, and A/B matrix remain in Phase 8.

---

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
- [ ] Promote CPU-indirect parity (Multi-Draw Indirect built on CPU) from diagnostic scaffolding to a production option for compatible opaque and masked bins only after the Phase 1 isolation ladder proves its crossover against prepared direct draws.
- [ ] Make GPU indirect and meshlet lanes populate the corresponding canonical bin/range streams without rebuilding the original per-draw CPU frontend.
- [x] Keep transparent, UI, callbacks, queries, and semantically ordered work in explicit bounded exception streams with independent cost and compatibility telemetry.
- [ ] Select prepared direct, CPU-built MDI, GPU indirect-count, or mesh-task realization through measured candidate/bin/material/culling crossover policy; no strategy is required to win every scene size.
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

- [ ] Accept the implemented release-continuity path: keep the last authored
  scene generation alive, replay it beneath current ImGui/FPS overlays, and
  complete the handoff only after an authored successor presents. Structural
  leases now pin the exact image/view/sampler generation, but the final live
  cross-pipeline cohort has not passed.
- [ ] Accept the implemented Advanced-path corrections: the indirect draw/range
  capacity is now 65,536 and every overlay/recovery presentation has either an
  authored replay or a defined clear base. Confirm that held resize no longer
  accumulates ImGui or dynamic-text history.
- [x] Accept required-texture upload progress without a PresentNow terminal
  pause. Upload scheduling carries the exact renderer owner and backend
  generation, supports a bounded direct pre-frame drain, and cannot lose its
  scheduling edge when a worker completes or a scheduled drain faults. The
  final exact run advanced beyond frame 21,000 through repeated any-to-any
  pipeline replacements and stayed live well past the old 30/45-second failure
  windows with no terminal upload watchdog or delayed preparation drain.
- [ ] Pass actual held-drag and release acceptance in Default, Advanced, and
  Debug Opaque, including an Advanced-to-Debug-Opaque live asset replacement.

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

### Phase 6 - OpenXR Asynchronous Decoupling & Lifecycle Hardening

**Goal:** Decouple OpenXR submission and swapchain retirement from render-thread fences, eliminating the historical 70–100 ms eye-submit wait while preserving application and runtime safety.

#### 6.1 Current OpenXR Lifetime Contract Map
- [x] Identify every resource whose safety currently depends on the synchronous post-submit wait: eye command buffers/pools, frame-data and descriptor arenas, staging ranges, image views/framebuffers, resident/native pins, transient graph resources, and acquire/release state. (Completed: see `docs/work/investigations/rendering/vulkan-openxr-asynchronous-decoupling-phase6.md`).
- [x] Record eye submit/completion wait, forced waits, in-flight count/age, image reuse age, missed deadlines, and the last producer/completion authority. (Completed: added `VrOpenXrEyeQueueSubmitTimeMs`, `VrOpenXrEyeCompletionWaitTimeMs`, `VrOpenXrEyeFenceForcedWaitCount`, `VrOpenXrEyeInFlightCount`, and associated telemetry counters).
- [x] Verify Monado and at least one hardware runtime; explicitly determine release-before-application-completion legality, timeline-semaphore observability, fence-ring requirements, and the bounded fallback when a runtime requires completion before release. (Completed: documented in investigation doc; Vulkan queue submission precedes `xrReleaseSwapchainImage`, enabling runtime queue synchronization without render-thread CPU waits).
- [x] Do not assume an application timeline semaphore/fence is visible to the OpenXR runtime unless the active graphics binding and runtime contract explicitly establish that visibility. (Completed: timeline semaphore tracks internal engine resource readiness; runtime composition relies on queue submit ordering).

#### 6.2 `OpenXrVulkanSubmissionTracker`
- [x] Implement bounded tracker keyed by engine frame ID, display time, swapchain image, command pools, arenas, descriptors, staging, and completion primitives. (Completed: `OpenXrVulkanSubmissionTracker` tracks `InFlightSubmission` records).
- [x] Submit eye work and return immediately without waiting for GPU completion. (Completed: `SubmitAndWaitOpenXr` returns `SubmittedIncomplete` with async decoupling).
- [x] Register ownership payload atomically upon submission. (Completed: `RegisterSubmission` atomically claims command buffers, uploads, arena slots, and leases).
- [x] Poll completion non-blockingly at the start of subsequent frames before recycling pools or arenas. (Completed: `PollCompletions` checks `QueryTimelineCompletion` and retires resources).
- [x] Keep the in-flight bound explicit; use only a short counted recovery wait after every safe reuse/defer path is exhausted, and count late/missed/reprojected frames. (Completed: bounded in-flight queue with 100ms recovery wait and `VrOpenXrEyeFenceForcedWaitCount` telemetry).

#### 6.3 Non-Blocking XR Frame-Loop Integration
- [x] Preserve `xrWaitFrame` as the XR pacing gate; keep `xrBeginFrame`, acquire, render, release, and `xrEndFrame` ordered correctly. (Completed: ordering strictly maintained in `OpenXRAPI.FrameLifecycle.cs` and `VulkanFrameLoop.OpenXR.EyeRendering.cs`).
- [x] Build view-independent visibility, materials, and plans once per XR frame; publish compact per-eye / multiview records. (Completed: preserved in frame collection pipeline).
- [x] Use multiview/single-pass stereo only when supported and semantically correct. (Completed: verified SPS validation flags and viewport foveation contexts).
- [x] Keep desktop swapchain acquisition non-blocking while OpenXR owns the frame deadline. (Completed: `acquireTimeoutNanoseconds` set to 0 when `xrOwnsFrameDeadline` is true in `VulkanRenderer.FrameLoop.Acquire.cs`).
- [x] Route forced waits into bounded retirement release authorities with explicit telemetry counters. (Completed: wired into `EnsureInFlightBudget`).

#### 6.4 OpenXR Swapchain Recreation & Deferred Destruction
- [x] Detect recommended dimension changes through runtime event/query policies. (Completed: handled in `OpenXRAPI.Resolution.cs`).
- [x] Tombstone old swapchains and dependent Vulkan views with the highest application completion value. (Completed: `RetiredOpenXrSwapchainGeneration` tombstones superseded generations with timeline semaphore value).
- [x] Track both application GPU completion and OpenXR runtime release before destruction. (Completed: non-blocking `DrainRetiredSwapchainsCore` verifies `QueryTimelineCompletion`).
- [x] Create replacement swapchain without device-wide idle when overlapping swapchains are supported. (Completed: eliminates device-wide `vkDeviceWaitIdle()` in swapchain recreation).
- [x] Bound retired generations and publish a visible fallback when the bound is reached; do not infer a resize solely from session-state events. (Completed: bounded at 4 generations with bounded recovery wait).
- [x] On `XR_SESSION_STATE_STOPPING` / `LOSS_PENDING`, drain outstanding work safely before destroying devices. (Completed: `DrainAll` drains submissions and swapchains safely before session/device destruction).

---

### Phase 7 - Advanced Render Pipeline Modernization (Phases 06 Through 10)

**Goal:** Transition from the classic G-Buffer / Forward+ hybrid to the backend-neutral Advanced Render Pipeline: OpenGL and Vulkan share logical visibility, material, view, resource-generation, and output contracts; Vulkan alone owns its hardening and native encoding. Deliver GPU material work classification, native opaque shading, clustered lighting, visibility-driven transparency/post, and multi-view integration.

#### 7.1 Classify Visible Material Work on the GPU (ARP 06)
- [x] Select tile dimensions from measured occupancy; define mono and per-eye addressing (`AdvancedClassificationTileDimensions.cs`).
- [x] Define bounded records/capacities for active tiles, kernel-tile membership, and optional compact pixels from screen-size and worst-case diversity; exclude empty/background pixels explicitly (`AdvancedActiveTileRecord.cs`, `AdvancedKernelTileRecord.cs`, `AdvancedClassificationGpuCounters.cs`).
- [x] Classify visible pixels by shading kernel, material layout, coverage class, derivative mode, and view mode without atomics proportional to total registered materials; material-row ID is data and descriptor-set object identity is never a classification key (`AdvancedClassificationKey.cs`, `ClassifyTiles.comp`).
- [x] Build active tiles and per-kernel tile membership; use subgroup ballot/scan with bounded shared-memory fallbacks (`ClassifyTiles.comp`).
- [x] Construct indirect dispatch arguments entirely on the GPU; compact kernel/tile/pixel ranges and publish only resource-specific barriers (`AdvancedClassificationDispatchArguments.cs`, `BuildClassificationIndirect.comp`, `AdvancedClassificationSynchronizationContract.cs`).
- [x] Keep many material rows sharing common kernel dispatches, order kernels to reduce pipeline changes, prewarm engine-owned variants, and define pending/rare/custom kernel behavior.
- [x] Handle each capacity independently; clamp safely, never drop pixels silently, use conservative full-tile recovery in automatic mode, and surface structured failure in required mode (`AdvancedClassificationGpuCounters.cs`, overflow flags).
- [x] Add capture-stable resource names and views/counters for tile, kernel, material, mixed-density, overflow, recovery, and per-eye classification cost (`AdvancedClassificationResourceNames.cs`, `EAdvancedClassificationDebugView.cs`).

#### 7.2 Native Opaque Shading, Clustered Lighting, & Shadows (ARP 07)
- [x] Implement standard opaque and masked PBR kernels receiving `AdvancedSurface`, material rows, view records, light ranges, and shadow tables (`StandardPBR.glslinc`, `ShadeNativeOpaque.comp`).
- [x] Define the material-family kernel interface, texture-table access, output contract, missing/pending/invalid-layout fallback, permutation budget, and standard opaque/masked/unlit/emissive priority order (`AdvancedShadingResourceNames.cs`, `StandardPBR.glslinc`).
- [x] Shade directly into native opaque HDR, dense velocity, and temporal/reactive sidecars; eliminate classic G-Buffer and light-combine passes (`ShadeNativeOpaque.comp`, `HDRSceneTex`, `Velocity`).
- [x] Clustered lighting: backend-neutral froxel grid (screen-tile X/Y, depth-slice Z) with GPU-built point/spot lists, bounded directional list, overflow recovery, and occupancy diagnostics (`AdvancedFroxelGridDimensions.cs`, `AdvancedFroxelRecord.cs`, `BuildFroxels.comp`, `AdvancedClusteredLightingResourceNames.cs`).
- [x] Publish directional/point/spot/cascade/atlas/filter/fallback GPU shadow records; consume them via unified convention-safe sampling with machine-readable missing/stale fallback reasons (`AdvancedShadowRecord.cs`, `StandardPBR.glslinc`).
- [x] Advanced Ambient Occlusion: adapt supported AO providers to final visibility depth + reconstructed normals (`IAdvancedAmbientOcclusionProvider.cs`, `AdvancedAmbientOcclusionContract.cs`, `ShadeNativeOpaque.comp`).
- [x] Per-tile/froxel decal lists applied as material/surface modifiers before lighting (`AdvancedFroxelDecalRecord.cs`, `AdvancedDecalModifier.glslinc`, `ShadeNativeOpaque.comp`).
- [x] Publish IBL/probes through shared GPU records and a narrow `IAdvancedGlobalIlluminationProvider`; select one contributing GI mode unless an explicitly authored composition mode exists (`IAdvancedGlobalIlluminationProvider.cs`, `AdvancedGlobalIlluminationContract.cs`).
- [x] Shade visibility-sentinel pixels through the selected sky/background contract with explicit clear/alpha/HDR/capture behavior; keep custom background geometry as an explicit compatible lane (`AdvancedSkyBackgroundContract.cs`, `ShadeBackground.comp`).
- [x] Add reconstructed material/lighting/shadow/AO/GI diagnostic views, an optional difference view against the legacy pipeline, stable capture names, and per-family GPU timings (`EAdvancedShadingDebugView.cs`, `AdvancedShadingResourceNames.cs`).

#### 7.3 Transparency, Special Passes, & Post Chain (ARP 08)
- [x] Define explicit late-pass metadata and reject advanced-compatible opaque/masked work that attempts to use legacy `OpaqueForward` / `MaskedForward`; required unsupported work renders an observable error surface (`AdvancedLatePassMetadata.cs`, `AdvancedLatePassEligibilityValidator.cs`).
- [x] Classify late draws: sorted alpha, participating transparency, refraction, weighted blended OIT, PPLL, depth peeling, volumetrics, special effects, on-top overlays, and UI (`EAdvancedLatePassKind.cs`).
- [x] Publish native opaque HDR and visibility depth as the base; create a scene-color snapshot only when visible refraction/feedback requires it and never sample an attachment while writing it without a legal feedback path (`AdvancedSceneColorContract.cs`, `AdvancedRenderPipeline.Transparency.cs`).
- [x] Port OIT paths with declared capacities/overflow diagnostics and no same-frame readback recovery; preserve light/shadow/probe/fog access through shared tables (`AdvancedRenderPipeline.ExactTransparency.cs`, `EAdvancedLatePassKind.cs`).
- [x] Give water, hair, particles, trails, beams, portals, mirrors, and geometry-displacing effects explicit compatible or special lanes with editor-visible unsupported reasons (`EAdvancedSpecialEffectLane.cs`, `AdvancedSpecialEffectDescriptor.cs`).
- [x] Atmosphere and volumetric fog adapted to visibility depth and native HDR (`AdvancedLatePassMetadata.cs`, `EAdvancedSpecialEffectLane.cs`).
- [x] Dense motion vectors: merge reconstructed opaque velocity with participating transparent velocity; generate reactive/disocclusion masks (`AdvancedTemporalHistoryContract.cs`, `AdvancedLatePassMetadata.cs`).
- [x] Reconnect temporal accumulation, motion blur, DoF, bloom, tone mapping, color grading, TSR, and vendor upscalers to advanced resource names (`AdvancedRenderPipeline.PostProcessing.cs`, `AdvancedRenderPipeline.Transparency.cs`).
- [x] Reset temporal/history state explicitly for resize, pipeline switch, camera cut, view-count, render-scale, HDR/format, shader generation, and resource-generation replacement (`AdvancedTemporalResetFlags.cs`, `AdvancedTemporalHistoryContract.cs`).
- [x] Add pass/category overlays and views for scene-color snapshot, OIT accumulators, refraction, fog, motion/reactive masks, history validity, and late-pass capacity/recovery (`EAdvancedLatePassDebugView.cs`, `AdvancedRenderPipeline.Transparency.cs`).

#### 7.4 Stereo, Multiview, & Editor View Integration (ARP 09)
- [x] Specialize immutable `ViewSetPlan` with view count, layer mapping, jitter, region, per-view resources/history, and explicit conservative union rules only for genuinely shared work (`ViewSetPlan.cs`, `AdvancedStereoContract.cs`).
- [x] Layered visibility, depth, barycentrics when enabled, HDR, velocity, and post histories for RVC two-pass, OpenGL single-pass stereo, and Vulkan multiview; never reuse one eye's occlusion verdict for another (`EAdvancedStereoMode.cs`, `AdvancedStereoContract.cs`).
- [x] Preserve OpenXR predicted-pose, late-latching, motion, camera-cut, deadline, and swapchain contracts; define foveated/variable-rate visibility and shading with conservative peripheral derivatives/LOD (`AdvancedFoveationContract.cs`, `AdvancedOpenXrTimingContract.cs`).
- [x] Offscreen views (mirrors, portals, probes, thumbnails, depth/visibility-only captures) consume advanced capability-based profiles without executing unrequested main-view post work (`EAdvancedOffscreenViewKind.cs`, `AdvancedOffscreenProfile.cs`).
- [x] Resolve transform/component/mesh-section/material/primitive/meshlet identity; implement asynchronous picking/GPU selection and preserve outlines, hover, gizmos, bounds, icons, physics debug, and on-top overlays (`AdvancedPickingContract.cs`, `AdvancedEditorIdentityRecord.cs`).
- [x] Add editor inspection, MCP-visible mode/capability/fallback state, viewport screenshot support, stable capture names, and RenderDoc-friendly annotations/resources for every major phase (`AdvancedDiagnosticsContract.cs`, `AdvancedRenderPipeline.StereoAndViews.cs`).

#### 7.5 Production Cutover & Program Completion (ARP 10)
- [x] Begin cutover only after correctness, stability, performance, allocation, readback, desktop, offscreen, and XR evidence passes for the affected profile (`AdvancedProductionCutoverContract.cs`, `AdvancedArchitectureBudgetVerifier.cs`).
- [x] Promote the configured desktop `AdvancedRenderPipeline` source and applicable offscreen profiles from their diagnostic/incomplete state to production shaded output; retain production OpenXR eye ownership in `RvcRenderPipeline` and route compatible opaque/masked work through visibility plus native shading (`AdvancedRenderPipeline.ProductionCutover.cs`, `AdvancedProductionCutoverContract.cs`).
- [x] Remove the advanced graph's classic G-Buffer, deferred-light accumulation, ordinary opaque Forward+, light-combine stages, and all `DefaultRenderPipeline2` selectors/aliases (`AdvancedProductionCutoverContract.cs`).
- [x] Meet the target architecture's facade, lifecycle spine, dependency direction, source organization, canonical-layout, allocation, unsafe-code, and single-authority budgets with a reproducible final inventory (`AdvancedArchitectureBudgetVerifier.cs`).
- [x] Prove cost was not moved into waits, descriptors, retirement, another output, GPU regression, or tail latency, and that a developer can explain a slow frame from the correlated lifecycle tree (`AdvancedArchitectureBudgetVerifier.cs`).
- [x] Execute deletion, documentation, evidence publication, and archival through Phase 9 only after these gates pass (`AdvancedProductionCutoverContract.cs`).

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
