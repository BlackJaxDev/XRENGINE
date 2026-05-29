# Visibility Buffer Rendering TODO

Last Updated: 2026-05-29
Owner: Rendering
Status: Active
Target Branch: `rendering-visibility-buffer`

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Avatar Optimization And Virtualized Rendering Design](../../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md)

## Goal

Add a visibility-buffer strategy for dense, opaque, material-diverse content.
The depth/visibility pass should write geometry identity, then shading should
classify visible pixels by material and shade through material-table data. This
is the renderer path that makes high-material-count hero avatars and dense
opaque virtual geometry viable without CPU binding fan-out.

## Scope

- Visibility target format and render-graph lifetime.
- Geometry ID emission from CPU direct, indirect, meshlet, and cluster paths.
- Material tile classification.
- Attribute reconstruction from triangle identity.
- Material compute shading.
- Motion vectors, depth, editor IDs, and fallback paths.

## Non-Goals

- Do not replace deferred or forward rendering for every material class.
- Do not include transparent/OIT rendering in the first visibility-buffer path.
- Do not make visibility-buffer shading the only path for editor diagnostics.
- Do not require cluster-virtualized avatars before the basic visibility buffer
  can validate on traditional meshes.

## Phase 0 - Branch, Baseline, And Contract

- [ ] Create dedicated branch `rendering-visibility-buffer`.
- [ ] Define initial target scenes: material-diverse avatar, dense static
  opaque scene, and mixed fallback scene.
- [ ] Capture forward/deferred reference images and profiler captures for each
  target scene.
- [ ] Define visibility payload format: `InstanceID`, `TriangleIndex` or
  `ClusterID`, optional primitive/local triangle ID, depth, and editor identity.
- [ ] Decide first implementation format: 32-bit packed, 64-bit packed, or
  multiple render targets.
- [ ] Define fallback behavior for payload overflow or unsupported formats.
- [ ] Add render-graph resource declarations and transient aliasing metadata.

Acceptance criteria:

- [ ] The visibility target format and fallback behavior are documented before
  shader work begins.

## Phase 1 - Visibility Geometry Pass

- [ ] Add a CPU direct visibility-pass variant for static opaque meshes.
- [ ] Add skinned visibility-pass variant that consumes current and previous
  skinned positions where motion vectors are needed.
- [ ] Add zero-readback indirect visibility-pass path using active draw IDs.
- [ ] Add meshlet visibility-pass path where production meshlet support exists.
- [ ] Ensure unsupported material/pass classes route to deferred or forward
  fallback with visible counters.
- [ ] Write valid depth for every visibility pixel.
- [ ] Write enough identity to recover material, transform, mesh, triangle, and
  editor selection ID.

Acceptance criteria:

- [ ] Visibility pass output can be inspected in RenderDoc/Nsight and maps
  every visible opaque pixel back to a valid draw/material/triangle record.

## Phase 2 - Material Tile Classification

- [ ] Implement tile classification compute that reads visibility payloads and
  groups pixels by material ID or material shading kernel.
- [ ] Use subgroup ballot or equivalent where available.
- [ ] Provide fallback classification for hardware without subgroup ballot.
- [ ] Bound per-tile material lists and define overflow behavior.
- [ ] Skip empty tiles and empty material lists.
- [ ] Add counters for classified pixels, active tiles, material tile dispatches,
  classification overflows, and classification time.
- [ ] Add tests for empty screen, single material, many materials, overflow, and
  invalid payload handling.

Acceptance criteria:

- [ ] Material tile dispatch count is bounded by visible material coverage, not
  source material slot count.

## Phase 3 - Attribute Reconstruction

- [ ] Define vertex attribute fetch layout for positions, normals, tangents,
  UVs, colors, skinning data, blendshape data, and material IDs.
- [ ] Implement barycentric reconstruction from triangle identity.
- [ ] Implement analytical derivatives or finite-difference derivatives for UV
  and normal-map sampling.
- [ ] Validate MikkTSpace tangent compatibility after UV remap or generated
  optimized assets.
- [ ] Add skinned attribute reconstruction using current skinned vertex buffers.
- [ ] Add previous-frame attribute or position reconstruction for velocity.
- [ ] Validate hard edges, UV seams, mirrored tangents, normal map handedness,
  and flat/smooth normal boundaries.

Acceptance criteria:

- [ ] Visibility-buffer shading matches forward/deferred reference within
  configured image and material tolerances for static and skinned opaque meshes.

## Phase 4 - Material Compute Shading

- [ ] Generate or select material shading kernels from pass-declared material
  layouts.
- [ ] Fetch material rows by material ID.
- [ ] Fetch texture references through the active texture-binding rung.
- [ ] Support opaque deferred-equivalent shading first.
- [ ] Add masked material support only when alpha coverage and depth behavior
  are correct.
- [ ] Add customization/per-instance material buffers needed by avatar paths.
- [ ] Write color, normal, roughness/metalness or equivalent lighting inputs,
  velocity, and any required post-process inputs.
- [ ] Preserve lighting model parity with deferred/forward references.

Acceptance criteria:

- [ ] Material-diverse opaque content shades without CPU material binding per
  source material slot.

## Phase 5 - Renderer Integration And Fallbacks

- [ ] Add visibility-buffer strategy to the strategy resolver.
- [ ] Add settings and diagnostics for forcing or disabling visibility-buffer
  rendering per scene/profile.
- [ ] Keep deferred and forward fallback paths for unsupported material classes.
- [ ] Preserve editor overlays, selection ID, gizmos, debug views, and capture
  tooling.
- [ ] Add profile capture fields for active visibility-buffer frames, fallback
  frames, material tile dispatch count, and reconstruction time.
- [ ] Ensure render-graph transient resources alias safely with deferred,
  forward, post, Hi-Z, and velocity resources.

Acceptance criteria:

- [ ] Mixed scenes can use visibility-buffer rendering for compatible opaque
  content and fallback paths for incompatible content in the same frame.

## Phase 6 - VR, Motion, And Upscaler Contract

- [ ] Validate multiview/view-instanced visibility pass where backend supports
  it.
- [ ] Ensure view-independent compute producers run once where possible.
- [ ] Ensure per-eye visibility/depth is correct under stereo.
- [ ] Write dense velocity for visibility-buffer pixels.
- [ ] Follow active upscaler jitter convention exactly.
- [ ] Validate reprojection-friendly depth and motion vectors under head motion.
- [ ] Add VR counters for per-eye visibility cost, material tile dispatches,
  velocity coverage, and fallback mode.

Acceptance criteria:

- [ ] Visibility-buffer rendering can be evaluated against the whole-frame XR
  budget with correct active stereo mode reporting.

## Final Validation And Merge

- [ ] Run targeted render graph, material table, meshlet/indirect, and shader
  tests.
- [ ] Capture visual parity against forward/deferred references.
- [ ] Capture CPU and GPU timing for material-diverse avatar and dense opaque
  scene.
- [ ] Update design docs if payload format or fallback contracts changed.
- [ ] Merge branch `rendering-visibility-buffer` back into `main` after
  implementation, validation, and documentation updates are complete.
