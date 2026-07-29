# 09 - Stereo, XR, Capture, And Editor Integration TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed - output-purpose foundation landed early; view/payload integration pending
Depends On: [08 - Transparency, Special Passes, And Post-Processing](08-transparency-special-passes-and-post-processing-todo.md)
Next: [10 - Validation, Performance, Cutover, And Retirement](10-validation-performance-cutover-and-retirement-todo.md)

## Goal

Make views other than a single desktop camera first-class consumers of shared
scene and visual-feature contracts. Stereo, XR, scene capture, mirrors, probes,
editor selection, diagnostics, and UI must use explicit view/resource
contracts rather than concrete default-pipeline type checks.

OpenXR eye outputs are owned by `RvcRenderPipeline`; desktop scene outputs are
owned by `AdvancedRenderPipeline` after promotion. They must consume compatible
scene, mesh, material, GI, temporal, froxel, and post-processing contracts while
keeping per-output pipelines, resources, histories, and output topology
independent.

Early foundation:

- [Output-Purpose And Feature-Contract Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-output-purpose-and-feature-contract-slice-2026-07-28.md)

## TODO

### 1. View-Set Contract

- [ ] Define a stable view-set record with view count, layer mapping, current/
  previous matrices, jitter, render region, foveation region, and output target.
- [ ] Give each view independent visibility, depth pyramid, history validity,
  material work, velocity, and temporal state.
- [ ] Share view-independent scene, material, animation, deformation, light,
  and immutable geometry preparation.
- [ ] Define conservative union rules only for work genuinely shared across
  views.
- [ ] Never reuse one eye's occlusion or depth verdict as the other eye's
  authoritative result.

### 2. Stereo And Multiview

- [ ] Declare layered visibility, depth, optional barycentric, HDR, velocity,
  reactive, and post-process histories.
- [ ] Add the required RVC two-pass, OpenGL single-pass stereo, and Vulkan
  parallel-recording/multiview eye variants.
- [ ] Add layered classification and native shading with explicit eye/layer
  addressing.
- [ ] Preserve per-eye derivatives, depth conventions, motion, and temporal
  reprojection.
- [ ] Validate transparent, fog, atmosphere, shadow, probe, and post-processing
  layer selection.
- [ ] Report selected stereo mode and any structured fallback reason.

### 3. XR Timing And Foveation

- [ ] Preserve runtime wait/begin/acquire/render/release/end ordering.
- [ ] Ensure RVC compute/graphics work fits the existing XR frame
  lifecycle without hidden queue/device waits.
- [ ] Support runtime-provided swapchains and image-array layers as imported
  resources.
- [ ] Define foveated and variable-rate visibility/shading behavior without
  invalidating identity reconstruction.
- [ ] Keep periphery derivative and texture-LOD behavior conservative.
- [ ] Validate late latching, predicted poses, motion vectors, and camera cuts.
- [ ] Record CPU and GPU timing against the canonical XR budget with capture
  overhead identified.

### 4. Offscreen And Secondary Views

- [ ] Update scene-capture, mirror, portal, reflection, light-probe, impostor,
  thumbnail, and test viewport creation to select the advanced pipeline through
  capabilities rather than concrete V2 checks.
- [ ] Define minimal capture profiles that omit temporal/post/late stages not
  requested by the consumer.
- [ ] Define depth-only and visibility-only capture profiles where useful.
- [ ] Preserve external target ownership, synchronization, and output format.
- [ ] Avoid executing the main-view post chain for probe or shadow captures.
- [ ] Validate nested and repeated captures without resource-name collisions.

### 5. Editor Identity And Selection

- [ ] Resolve transform, component, mesh section, material, primitive, meshlet,
  and instance identity from visibility records.
- [ ] Route selection picking through asynchronous readback or GPU selection
  queries, never a frame-blocking full visibility readback.
- [ ] Preserve outlines, hover, gizmos, bounds, icons, physics debug, and
  on-top overlays.
- [ ] Add an inspector panel for decoded visibility payload and material-kernel
  eligibility.
- [ ] Replace editor checks for `DefaultRenderPipeline`/
  `DefaultRenderPipeline2` with focused provider interfaces.
- [ ] Ensure editor platform windows and previews do not reuse stale pipeline
  generations.

### 6. Debug And Capture Tooling

- [ ] Register stable capture names for every advanced resource.
- [ ] Add command annotations for each early/late visibility, classification,
  shading, transparency, temporal, post, and output phase.
- [ ] Add MCP-visible settings and state for selected advanced mode, capability
  result, fallback/error reason, and debug view.
- [ ] Make viewport screenshots capture final advanced output without relying
  on legacy diagnostic FBO names.
- [ ] Add RenderDoc-friendly inspection of visibility payloads, draw records,
  material work lists, and indirect arguments.
- [ ] Keep delayed profiler readback bounded and explicitly excluded from
  benchmark captures when necessary.

### 7. Validation

- [ ] Run desktop mono, emulated stereo, two-pass stereo, single-pass stereo,
  OpenXR, OpenVR, scene capture, mirror, probe, and editor-platform-window
  matrices.
- [ ] Capture at least two camera/head positions for each visual issue.
- [ ] Validate eye independence using deliberately asymmetric occluders and
  per-eye content.
- [ ] Validate resize, view-count change, runtime restart, swapchain recreation,
  and pipeline hot selection.
- [ ] Add source/behavior tests preventing new concrete V2/default-pipeline
  type checks in shared integrations.

## Acceptance Criteria

- [ ] Desktop Advanced, OpenXR RVC, and capture consumers use one logical
  scene/mesh/material and visual-feature contract despite their different
  opaque/output execution paths.
- [ ] Per-eye visibility, motion, and histories remain independent.
- [ ] Shared preparation runs once only where correctness permits.
- [ ] Editor picking and diagnostics resolve advanced identity without
  blocking the frame.
- [ ] No live integration depends on `DefaultRenderPipeline2`.
