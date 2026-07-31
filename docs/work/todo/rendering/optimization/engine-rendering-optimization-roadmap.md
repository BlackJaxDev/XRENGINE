# Engine Rendering Optimization Roadmap

Last Updated: 2026-07-30
Owner: Rendering
Status: Umbrella Index; Vulkan Desktop 200+ Hz And RVC 120 Hz Execution Delegated To Workstreams 01-08
Execution: This document does not authorize implementation out of the numbered
Vulkan sequence. Vulkan Phase 5.2 work uses the current worktree; do not create
or switch branches for that effort.

Design source:

- [Canonical Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md)
- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Zero-Readback GPU-Driven Rendering Plan](../../../design/rendering/zero-readback-gpu-driven-rendering-plan.md)
- [Production GPU-Driven Rendering Roadmap](../gpu/production-rendering-pipeline-roadmap.md)

## Goal

Turn the renderer optimization design into coordinated implementation work.
The renderer should hit VR frame budgets by keeping per-frame CPU submission
small, keeping GPU-driven paths compact, removing current-frame readbacks,
prewarming shader and material state, and exposing enough counters to explain
every performance result.

For the current Vulkan desktop 200+ Hz and RVC zero-readback 120 Hz
investigation, the following documents are the sole ordered execution and
performance-gate authority:

1. [01 - Performance Truth And Regression Gates](01-vulkan-performance-truth-and-regression-gates-todo.md)
2. [02 - Vulkan Primary Reuse Correctness](02-vulkan-primary-reuse-correctness-todo.md)
3. [03 - True GPU-Driven Zero-Readback Submission](03-vulkan-true-zero-readback-submission-todo.md)
4. [04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md)
5. [05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md)
6. [06 - Forward+ Prepass And Render-Graph Cost](06-forward-prepass-and-render-graph-cost-todo.md)
7. [07 - Occlusion Systems Performance](07-occlusion-systems-performance-todo.md)
8. [08 - Render Tail Latency](08-render-tail-latency-shadows-streaming-jobs-todo.md)

Implementation status: workstream 05 is
`Implementation Complete; Acceptance Deferred`. Workstream 06 is unblocked
for implementation; all workstream-05 performance, overlap, allocation, and
stress claims remain owned by the shared acceptance closeout.

They execute strictly in implementation order. By owner direction on
2026-07-29, the repeated long-form acceptance matrices are deferred to the
[01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md).
A later workstream may begin when its predecessor is marked
`Implementation Complete; Acceptance Deferred`. Targeted tests, narrow builds,
and implementation smokes remain mandatory, and any failure that reveals an
implementation defect still blocks progression. The phases below remain a
backend-neutral, VR, avatar, and future-renderer roadmap; duplicated Vulkan
implementation items are satisfied only through the numbered owner and must
not be executed as a parallel checklist.

Detailed implementation and longer-horizon design also live in:

- [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md)
- [CPU Async Hardware Query Occlusion TODO](../../COMPLETED/cpu-async-hardware-query-occlusion-todo.md)
- [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md)
- [Material Table And Texture Binding Ladder TODO](material-table-and-texture-binding-ladder-todo.md)
- [Deferred+ Render Path TODO](deferred-plus-render-path-todo.md)
- [Superseded Visibility Buffer Rendering TODO](visibility-buffer-rendering-todo.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Superseded Vulkan Primary Command Recording Fast Path TODO](vulkan-primary-command-recording-fast-path-todo.md)
- [Desktop And VR Shared Render-Thread Frame Pacing TODO](../../COMPLETED/desktop-vr-shared-render-thread-frame-pacing-todo.md)
- [Editor Profiler And UI Render Cost TODO](editor-profiler-ui-render-cost-todo.md)
- [OpenXR Vulkan Submit Fence Wait TODO](../vr/openxr-vulkan-submit-fence-wait-todo.md)
- [Default Pipeline GPU Hotspots TODO](default-pipeline-gpu-hotspots-todo.md)
- [Collect-Visible Render Wait Decoupling TODO](../../COMPLETED/collect-visible-render-wait-decoupling-todo.md)
- [Superseded Rendering Clean Performance Baseline Profile Contract TODO](rendering-clean-performance-baseline-profile-contract-todo.md)
- [Rendering Profiler And Benchmarking TODO](../../COMPLETED/rendering-profiler-and-benchmarking-todo.md)
- [Vulkan Headless MCP Component Profiling TODO](vulkan-headless-mcp-component-profiling-todo.md)

Avatar asset transformation is tracked separately under
[Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md).

## Global Invariants

- CPU direct remains the correctness baseline and an explicitly selected
  fallback path. A requested accelerated strategy never enters it silently.
- `GpuIndirectZeroReadback` must not read GPU visibility, counters, ranges, or
  query results needed by the current frame.
- GPU-driven rendering must compact to active work. Full material, bucket, or
  meshlet scans are diagnostic or transitional only.
- Vulkan CPU-direct and GPU-driven paths must reuse generation-validated stable
  command topology. Data-only frame-slot, upload, visibility, indirect-command,
  and count changes do not invalidate compatible recorded ranges.
- Shader/program/pipeline work must be warmed before measured interactive
  frames and persisted to disk where the backend supports it.
- Render submission hot paths must avoid heap allocations, LINQ, captured
  closures, boxing, string concatenation, and `foreach` over class enumerators.
- Every optimization must publish counters for the thing it claims to improve.
- VR production paths must report the active stereo mode and benchmark against
  the whole submitted XR frame budget.
- Renderer paths must accept source models and optimized cooked variants as
  normal engine assets.

## Dependencies

The first row is the controlling dependency for current Vulkan desktop
performance work. The remaining rows describe broader subsystem dependencies
and do not override its serial gate.

| Workstream | Blocks | Depends On |
| --- | --- | --- |
| Vulkan desktop 200+ Hz / RVC 120 Hz workstreams 01-08 | Current Vulkan performance promotion | Prior numbered workstream |
| CPU direct fast path | Reliable baseline, editor diagnostics | Profiler counters and targeted builds |
| Profiler and benchmarking | All performance decisions | Existing profiler packet/log infrastructure |
| Compact zero-readback | Production GPU-driven rendering | GPUScene, material table, Hi-Z, command buffers |
| Material table ladder | Compact zero-readback and visibility buffer | Dynamic indirect material binding layout work |
| Visibility buffer | Hero avatars, material-diverse dense meshes | Material table, meshlet/indirect geometry IDs |
| VR performance contract | Production XR acceptance | OpenVR/OpenXR paths, ViewSet/multiview plumbing |

## Phase 0 - Baseline And Triage

Vulkan execution note: owned by workstream 01. This section remains an umbrella
inventory and must not create a second baseline contract.

- [ ] Coordinate Vulkan work through the canonical Phase 5.2A-5.2C gates in the
  current worktree; do not create an independent branch or promotion status.
- [ ] Confirm the active design docs are linked from this roadmap and from
  `docs/work/README.md`.
- [ ] Record current build status:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`.
- [ ] Capture a Release baseline for the unit-testing avatar scene:
  `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, and any
  available meshlet strategy.
- [ ] Capture a Release baseline for a high-object-count static scene and a
  material-diverse scene.
- [ ] Record active backend, GPU, driver version, stereo mode, validation-layer
  state, shader-cache state, texture-cache state, and build configuration in
  each baseline manifest.
- [ ] Add links from each focused TODO back to this roadmap.

Acceptance criteria:

- [ ] Baseline results are stored under `Build/Logs` or an adjacent testing
  note with enough launch settings to reproduce them.
- [ ] The roadmap can be read without opening every focused TODO.

## Phase 1 - Baseline First: CPU Direct And Profiler

Goal: make the simplest path trustworthy and measurable before chasing
GPU-driven complexity.

Vulkan execution note: measurement is owned by workstream 01, primary reuse by
workstream 02, and next-frame handoff by workstream 04. Supporting documents
provide implementation detail only.

- [ ] Complete Phase 0 and Phase 1 of
  [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md).
- [ ] Complete Phase 0 and Phase 1 of
  [Rendering Profiler And Benchmarking TODO](../../COMPLETED/rendering-profiler-and-benchmarking-todo.md).
- [ ] Confirm CPU direct render submission is allocation-free in steady state
  for at least one static scene and one skinned-avatar scene.
- [ ] Confirm shader linking, asset deserialization, and texture upload spikes
  are not occurring during measured steady-state render frames.
- [ ] Publish draw/state/upload counters for CPU direct frames.

Acceptance criteria:

- [ ] CPU direct is fast enough to serve as a meaningful baseline.
- [ ] A slow CPU direct frame can be explained as CPU-bound, GPU-bound,
  synchronization-bound, or asset-streaming-bound.

## Phase 2 - Compact Zero-Readback

Goal: make strict zero-readback a compact production path rather than a broad
scan that happens not to read back.

Vulkan execution note: submission and compaction are owned by workstream 03;
Hi-Z effectiveness and promotion are owned by workstream 07.

- [ ] Complete active-list compaction, overflow handling, and barrier batching
  in [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md).
- [x] Verify `GpuIndirectZeroReadback` does not full-scan inactive material
  buckets in production mode.
- [ ] Verify `GpuIndirectZeroReadback` emits `GpuCompactionOverflow` when active
  output capacity is exceeded and never silently truncates visible work.
- [ ] Verify one-phase vs two-phase Hi-Z mode is visible in profiler output.
- [ ] Compare CPU direct vs zero-readback on low-count, high-count, and heavily
  occluded scenes.

Workstream-03 note (2026-07-28): the bounded Vulkan implementation now uses
three fixed GPU-owned tier groups, workgroup prefix-scan compaction, reported
bindless/compaction rungs, and indirect-count submission with zero
capture-window readback or full scans. Monado RVC and a usable production
RenderDoc capture are now available and verified. Promotion remains open
because matched Uber CPU-direct is faster, the full
Gate/foveation/scaling/parity matrix has not passed, submission still reports
136 allocated bytes, and exact transparency remains explicitly unsupported.
The same RVC capture's 40,384 frame-data-refresh bytes are owned by workstream
04, its 3,255,936 primary-recording bytes by workstream 05, and its
109.139 ms RVC render p95 against 8.33 ms by final promotion in workstream 08.

Acceptance criteria:

- [ ] Zero-readback meets the explicit low-count overhead and crossover budgets
  defined by workstream 03 using workstream 01's canonical variance,
  regression, and evidence rules.
- [ ] Zero-readback beats CPU direct in the retained high-count or occluded
  scenes selected for production promotion.

## Phase 3 - Material Tables And Texture Binding

Goal: make material diversity data-driven instead of CPU-binding-driven.

Vulkan execution note: workstream 03 owns the bounded production rung required
to eliminate material-slot/tier CPU fan-out. Sparse and virtual-texture feature
work remains independently owned by the material/texturing roadmaps.

- [ ] Complete runtime capability probing and active texture-binding rung
  reporting in
  [Material Table And Texture Binding Ladder TODO](material-table-and-texture-binding-ladder-todo.md).
- [ ] Coordinate with
  [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md)
  so pass-declared material row layouts remain the source of truth.
- [ ] Ensure texture arrays are used only for compatible homogeneous groups.
- [ ] Ensure bindless texture handles are runtime-probed and never assumed.
- [ ] Ensure sparse/virtual texture handle paths defer to the texture runtime
  streaming design.
- [ ] Ensure coarse bucket fallback is deterministic and visibly reported.

Acceptance criteria:

- [ ] Active texture binding rung is visible in every performance capture.
- [ ] Adding a texture-only material does not require a new shader program or
  pipeline family when the material-table path supports it.

## Phase 4 - Visibility Buffer And Virtual Geometry Direction

Goal: decouple geometry submission from material-diverse shading where it pays.

This is future renderer architecture, not an alternate way to satisfy any
numbered workstream. It begins only after the current Vulkan sequence or an
explicit owner-approved reprioritization.

- [ ] Complete the ordered
  [Advanced Render Pipeline Architectural Refactor](../architectural-refactor/00-advanced-render-pipeline-refactor-todo.md),
  including geometry identity, visibility raster, reconstruction,
  classification, native material/lighting shading, integration, validation,
  and cutover. The former standalone and monolithic future-path trackers are
  superseded history.
- [ ] Integrate advanced visibility producers with the existing mesh submission
  strategy resolver without creating a second shading architecture.
- [ ] Validate material-diverse hero-avatar and dense opaque content against
  forward/deferred reference paths.
- [ ] Keep transparent and genuinely special late material classes on explicit
  paths; required-mode incompatible opaque materials must fail visibly rather
  than silently using the old opaque renderer.

Acceptance criteria:

- [ ] A 60+ material opaque avatar can render through a material-independent
  visibility pass plus bounded material tile shading.
- [ ] Visibility-buffer output has correct depth, material identity, motion
  vectors, and editor selection identity.

## Phase 5 - VR Production Contract

Goal: make every renderer path report and respect XR frame constraints.

The VR contract is a cross-cutting acceptance overlay. It does not replace the
desktop 200+ Hz or Vulkan RVC zero-readback 120 Hz gates and cannot be used to
waive a numbered exit gate.

- [ ] Complete single-pass stereo, per-eye counters, motion-vector contract,
  VRS/foveation, and reprojection diagnostics in
  [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md).
- [ ] Confirm all benchmark reports state whether the frame is mono,
  multiview, view-instanced, or two-pass.
- [ ] Confirm compute producers that are view-independent run once per frame,
  not once per eye.
- [ ] Confirm motion vectors remain valid for skinned meshes, visibility-buffer
  shading, and avatar distant LODs.

Acceptance criteria:

- [ ] No renderer path is considered VR-production-ready unless it reports
  whole-frame XR budget compliance and active stereo mode.

## Phase 6 - Integration With Avatar And Asset Pipelines

Goal: treat optimized variants, meshlets, cluster payloads, and distant LODs as
normal renderer-visible assets.

This longer-horizon asset integration remains independent after the numbered
Vulkan execution sequence.

- [ ] Coordinate runtime representation counters with
  [Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md).
- [ ] Ensure renderer stats distinguish source mesh, optimized LOD, meshlet,
  visibility-buffer, cluster-virtualized, octahedral impostor, and Gaussian
  splat representations.
- [ ] Ensure cooked variant identity participates in shader prewarm, material
  table rows, texture streaming, meshlet ranges, and profiler reports.
- [ ] Keep unoptimized source asset fallback available for editor diagnostics.

Acceptance criteria:

- [ ] Performance captures can explain whether an avatar slowdown is renderer
  strategy, asset content, material fan-out, texture residency, skinning,
  blendshapes, visibility-buffer shading, cluster rendering, or splat rendering.

## Final Validation And Closeout

- [ ] Run targeted rendering unit/source-contract tests touched by the focused
  TODOs.
- [ ] Run at least one Release editor smoke for CPU direct and
  `GpuIndirectZeroReadback`.
- [ ] Run at least one VR or stereo smoke when hardware/runtime is available.
- [ ] Update this roadmap with completed numbered-workstream statuses and links
  to evidence.
- [ ] Close Vulkan-owned desktop performance work only when workstream 08
  records completion of all preceding numbered gates and the canonical Phase
  5.2 promotion document records the same evidence or an explicit v1
  removal/deferral decision.
