# VR Rendering Performance Contract TODO

Last Updated: 2026-07-28
Owner: Rendering / XR
Status: Active
Target Branch: `rendering-vr-performance-contract`

Cross-cutting relationship:

- This tracker is an independent acceptance overlay across every renderer
  strategy. It is not a child phase of the Default pipeline, Deferred+, or any
  one backend.
- [Forward+ render-graph code changes](../vulkan-core-hardening-and-device-loss-todo.md#6-simplify-the-forward-render-graph)
  owns current Default-pipeline prepass/copy/replay topology; this tracker owns
  whether that resulting graph satisfies whole-frame XR budgets and per-eye
  correctness.
- [Deferred+ Render Path](deferred-plus-render-path-todo.md) owns Deferred+
  implementation; its stereo and headset claims must satisfy this contract.

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenVR Rendering](../../../../architecture/rendering/openvr-rendering.md)
- [OpenXR No-HMD Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Future Work TODO](../vr/openxr-future-work-todo.md)

## Goal

Make VR performance requirements explicit across every renderer strategy.
Renderer paths should report the active stereo mode, benchmark against the
whole submitted XR frame budget, produce correct depth and motion vectors, and
avoid hidden two-pass stereo regressions.

For the Vulkan RVC production lane with `GpuIndirectZeroReadback`, the minimum
promotion target is 120 Hz, or 8.33 ms p95 for the complete frame. The measured
workload must contain at least one desktop render plus both RVC eye renders.
This is one whole-frame budget, not an allowance per eye or per render, and it
applies whether foveation is disabled or enabled. The separate desktop-only
promotion target is at least 200 Hz, or 5.00 ms p95.

## Scope

- Whole-frame XR frame budgets.
- Single-pass stereo and fallback reporting.
- Per-eye/per-view resource correctness.
- Motion-vector and temporal upscaler contract.
- VRS/foveation integration.
- Reprojection-friendly depth and velocity.
- VR benchmarking and diagnostics.
- Cross-render-path acceptance for current Deferred/Forward+, Deferred+,
  visibility-producing, CPU-direct, indirect, meshlet, and fallback paths.

## Non-Goals

- Do not require every development backend to support single-pass stereo on day
  one.
- Do not hide two-pass fallback. It is allowed only when reported.
- Do not implement vendor upscalers in this TODO; define the data contract they
  need.

## Phase 0 - Branch, Baseline, And Runtime Matrix

- [ ] Create dedicated branch `rendering-vr-performance-contract`.
- [ ] Record the active render-path tracker and graph revision for every
  capture, including workstream 06's accepted Default graph or the selected
  Deferred+ mode.
- [ ] Inventory active VR paths: OpenVR, OpenXR, no-HMD/Monado test lane, editor
  stereo, and desktop mono mirror.
- [ ] Record which backends support `GL_OVR_multiview2`, Vulkan multiview,
  DX12 view instancing, or instanced stereo fallback.
- [ ] Capture current frame budgets and measured timings for 72 Hz, 90 Hz, and
  120 Hz target modes where hardware/runtime is available.
- [ ] Include the Vulkan RVC zero-readback promotion workload with at least
  three executed renders per frame: desktop, left eye, and right eye.
- [ ] Add profile manifest fields for XR runtime, HMD, refresh rate, render
  resolution, stereo path, foveation/VRS state, and reprojection state.

Acceptance criteria:

- [ ] A VR profile capture can explain its target budget and active stereo
  implementation without external notes.

## Phase 1 - Stereo Mode Contract

- [ ] Add a canonical `StereoMode` stat with values such as `Mono`,
  `Multiview`, `ViewInstance`, `InstancedStereo`, and `TwoPass`.
- [ ] Report stereo mode per frame and per pass where a pass differs from the
  frame default.
- [ ] Mark two-pass stereo as compatibility/debug fallback in diagnostics.
- [ ] Ensure draw-call counters distinguish mono draws, multiview/view-instanced
  draws, and two-pass CPU-submitted draws.
- [ ] Ensure compute producers that are view-independent run once per frame,
  not once per eye.
- [ ] Ensure view-dependent passes state why they run per eye.

Acceptance criteria:

- [ ] A scene cannot silently double CPU submission by falling into two-pass
  stereo without profiler reporting it.

## Phase 2 - Multiview / View-Instancing Integration

- [ ] Validate OpenGL `GL_OVR_multiview2` geometry passes that can support it.
- [ ] Add or validate `gl_ViewID_OVR` matrix selection and per-view output
  routing.
- [ ] Validate Vulkan `VK_KHR_multiview` render pass/subpass setup where Vulkan
  path supports it.
- [ ] Document DX12 view-instancing requirements for future backend parity.
- [ ] Add fallback to instanced stereo or explicit two-pass when single-pass
  backend support is missing.
- [ ] Validate shadow maps render mono unless a pass explicitly requires
  per-eye shadowing.
- [ ] Add tests or source-contract checks for multiview shader defines and
  binding layout.

Acceptance criteria:

- [ ] Compatible geometry/depth/visibility passes render single-pass stereo on
  supported backend/HMD combinations.

## Phase 3 - Per-Eye Resource Correctness

- [ ] Validate depth, normal, velocity, visibility, post-process, and mirror
  resources for mono, stereo array, and multiview layouts.
- [ ] Add stereo-safe Hi-Z source resolution: per-eye chain, array sampler
  variant, or explicit conservative fallback.
- [ ] Ensure per-eye view/projection and previous view/projection state are
  double-buffered correctly.
- [ ] Ensure editor overlays and UI write valid velocity or explicit zero.
- [ ] Ensure mirror blit does not mutate eye textures or force synchronization
  in measured frames.
- [ ] Add diagnostics for stale camera state shared across eyes.

Acceptance criteria:

- [ ] Both eyes see the same scene state with correct per-eye projection and no
  stale shared resource hazards.

## Phase 4 - Motion Vectors And Upscaler Contract

- [ ] Maintain previous-frame transform per instance.
- [ ] Maintain previous-frame skinned position buffers for skinned meshes.
- [ ] Generate velocity from current clip position minus previous clip position,
  using previous skinned position for animated vertices.
- [ ] Define velocity behavior for CPU direct, zero-readback indirect, meshlet,
  visibility-buffer, cluster avatar, splat, UI, and editor overlay paths.
- [ ] Follow the active upscaler jitter convention exactly; document whether
  motion vectors include or exclude camera jitter for each integration.
- [ ] Add velocity validity coverage counter.
- [ ] Add debug view for missing, NaN, or out-of-range velocity.

Acceptance criteria:

- [ ] Temporal AA/upscaler/reprojection inputs are dense and correct enough that
  skinned avatar motion does not ghost due to previous-position approximation.

## Phase 5 - VRS And Foveation

- [ ] Add or validate VRS/foveation capability probes per backend.
- [ ] Add fixed foveation rate image path for non-eye-tracked HMDs where
  supported.
- [ ] Add eye-tracked rate image path behind runtime capability checks where
  supported.
- [ ] Report VRS shading-rate distribution per frame.
- [ ] Ensure screen-space-error metrics for LOD/avatar systems account for
  effective shading rate where used.
- [ ] Add fallback when VRS is unsupported or disabled.
- [ ] Validate VRS does not break UI readability, selection, or debug overlays.

Acceptance criteria:

- [ ] VRS/foveation is a measurable, reported option, not a hidden assumption in
  performance claims.

## Phase 6 - Reprojection Friendliness

- [ ] Write valid depth for every visible pixel, including splats and impostors.
- [ ] Write valid zero velocity for static UI/editor overlays, not undefined
  memory.
- [ ] Mark reprojection-incompatible post effects with explicit VR opt-out or
  fallback.
- [ ] Add runtime reprojection/motion-smoothing event counters where APIs expose
  them.
- [ ] Add profile manifest fields for runtime reprojection state.
- [ ] Add warnings when app frame time appears acceptable only because runtime
  reprojection is masking misses.

Acceptance criteria:

- [ ] Missed XR budgets are visible in profiler output even when runtime
  reprojection keeps the display moving.

## Phase 7 - Benchmark Discipline

- [ ] Standardize VR benchmark scenes and camera paths.
- [ ] Report whole-frame budget, not per-eye budget, in all benchmark notes.
- [ ] Require Vulkan RVC `GpuIndirectZeroReadback` p95 <= 8.33 ms for the
  complete minimum-three-render frame in both supported foveation states.
- [ ] Include serial two-pass eye-slice estimate only as a warning for naive
  two-pass paths.
- [ ] Disable validation layers, synchronous debug output, and debug callbacks
  during benchmark runs.
- [ ] Capture p50, p90, p99, dropped frames, reprojection events, and stereo
  mode.
- [ ] Keep shader and texture cache policy explicit: cold-start or warm-start.

Acceptance criteria:

- [ ] VR performance reports are comparable across renderer strategies because
  budget, stereo path, cache state, and validation state are explicit.

## Final Validation And Merge

- [ ] Run OpenVR or OpenXR smoke where hardware/runtime is available.
- [ ] Run no-HMD OpenXR/Monado timing smoke where configured.
- [ ] Run desktop mono regression to ensure stereo changes do not break mono.
- [ ] Update architecture docs if stereo or velocity contracts change.
- [ ] Merge branch `rendering-vr-performance-contract` back into `main` after
  implementation, validation, and documentation updates are complete.
