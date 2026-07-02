# Engine Rendering Optimization Roadmap

Last Updated: 2026-07-01
Owner: Rendering
Status: Active roadmap
Target Branch: `rendering-engine-optimization-roadmap`

Design source:

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

This roadmap is the coordination document. Detailed implementation lives in:

- [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md)
- [CPU Async Hardware Query Occlusion TODO](cpu-async-hardware-query-occlusion-todo.md)
- [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md)
- [Material Table And Texture Binding Ladder TODO](material-table-and-texture-binding-ladder-todo.md)
- [Visibility Buffer Rendering TODO](visibility-buffer-rendering-todo.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Vulkan Primary Command Recording Fast Path TODO](vulkan-primary-command-recording-fast-path-todo.md)
- [Desktop And VR Shared Render-Thread Frame Pacing TODO](desktop-vr-shared-render-thread-frame-pacing-todo.md)
- [Editor Profiler And UI Render Cost TODO](editor-profiler-ui-render-cost-todo.md)
- [OpenXR Vulkan Submit Fence Wait TODO](../vr/openxr-vulkan-submit-fence-wait-todo.md)
- [Default Pipeline GPU Hotspots TODO](default-pipeline-gpu-hotspots-todo.md)
- [Collect-Visible Render Wait Decoupling TODO](collect-visible-render-wait-decoupling-todo.md)
- [Rendering Clean Performance Baseline Profile Contract TODO](rendering-clean-performance-baseline-profile-contract-todo.md)
- [Rendering Profiler And Benchmarking TODO](rendering-profiler-and-benchmarking-todo.md)

Avatar asset transformation is tracked separately under
[Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md).

## Global Invariants

- CPU direct remains the correctness baseline and the fallback path.
- `GpuIndirectZeroReadback` must not read GPU visibility, counters, ranges, or
  query results needed by the current frame.
- GPU-driven rendering must compact to active work. Full material, bucket, or
  meshlet scans are diagnostic or transitional only.
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

| Workstream | Blocks | Depends On |
| --- | --- | --- |
| CPU direct fast path | Reliable baseline, editor diagnostics | Profiler counters and targeted builds |
| Profiler and benchmarking | All performance decisions | Existing profiler packet/log infrastructure |
| Compact zero-readback | Production GPU-driven rendering | GPUScene, material table, Hi-Z, command buffers |
| Material table ladder | Compact zero-readback and visibility buffer | Dynamic indirect material binding layout work |
| Visibility buffer | Hero avatars, material-diverse dense meshes | Material table, meshlet/indirect geometry IDs |
| VR performance contract | Production XR acceptance | OpenVR/OpenXR paths, ViewSet/multiview plumbing |

## Phase 0 - Branch, Baseline, And Triage

- [ ] Create dedicated branch `rendering-engine-optimization-roadmap`.
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

- [ ] Complete Phase 0 and Phase 1 of
  [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md).
- [ ] Complete Phase 0 and Phase 1 of
  [Rendering Profiler And Benchmarking TODO](rendering-profiler-and-benchmarking-todo.md).
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

- [ ] Complete active-list compaction, overflow handling, and barrier batching
  in [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md).
- [ ] Verify `GpuIndirectZeroReadback` does not full-scan inactive material
  buckets in production mode.
- [ ] Verify `GpuIndirectZeroReadback` emits `GpuCompactionOverflow` when active
  output capacity is exceeded and never silently truncates visible work.
- [ ] Verify one-phase vs two-phase Hi-Z mode is visible in profiler output.
- [ ] Compare CPU direct vs zero-readback on low-count, high-count, and heavily
  occluded scenes.

Acceptance criteria:

- [ ] Zero-readback is within 20 percent of CPU direct where it cannot win.
- [ ] Zero-readback beats CPU direct on scenes where GPU culling and compaction
  reject enough work to pay for their setup cost.

## Phase 3 - Material Tables And Texture Binding

Goal: make material diversity data-driven instead of CPU-binding-driven.

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

- [ ] Complete geometry ID format, visibility pass, material tile
  classification, and attribute reconstruction work in
  [Visibility Buffer Rendering TODO](visibility-buffer-rendering-todo.md).
- [ ] Integrate visibility-buffer strategy selection into the existing mesh
  submission strategy resolver.
- [ ] Validate material-diverse hero-avatar and dense opaque content against
  forward/deferred reference paths.
- [ ] Keep transparent, special forward-only, and incompatible material classes
  on explicit fallback paths.

Acceptance criteria:

- [ ] A 60+ material opaque avatar can render through a material-independent
  visibility pass plus bounded material tile shading.
- [ ] Visibility-buffer output has correct depth, material identity, motion
  vectors, and editor selection identity.

## Phase 5 - VR Production Contract

Goal: make every renderer path report and respect XR frame constraints.

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

## Final Validation And Merge

- [ ] Run targeted rendering unit/source-contract tests touched by the focused
  TODOs.
- [ ] Run at least one Release editor smoke for CPU direct and
  `GpuIndirectZeroReadback`.
- [ ] Run at least one VR or stereo smoke when hardware/runtime is available.
- [ ] Update this roadmap with completed status and links to evidence.
- [ ] Merge branch `rendering-engine-optimization-roadmap` back into `main`
  after implementation, validation, and documentation updates are complete.
