# 04 - Visibility Buffer Resources And Geometry TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [03 - GPU Visibility Preparation And Deformation](03-gpu-visibility-preparation-and-deformation-todo.md)
Next: [05 - Attribute Reconstruction](05-attribute-reconstruction-todo.md)

## Goal

Rasterize the nearest compatible opaque or masked surface into compact identity
and depth targets. Every geometry producer must emit the same logical payload,
and the pass must avoid material evaluation except where coverage or
displacement changes which surface is visible.

## Required Payload Semantics

The final packing is selected after capacity and bandwidth measurements, but the
logical payload must resolve:

- stable draw/instance identity;
- primitive identity, or meshlet/cluster plus local primitive identity;
- material and shading-kernel identity through the draw/material tables;
- current and previous geometry sources;
- transform and editor-selection identity;
- view/layer identity where not implicit;
- background/invalid sentinel.

## TODO

### 1. Format And Capacity Decision

- [ ] Inventory maximum simultaneous draw, instance, primitive, meshlet,
  material, view, and editor-ID ranges for target scenes.
- [ ] Compare two `R32_UINT` attachments, one `RG32_UINT` attachment, packed
  64-bit identity, and narrower sidecar options on OpenGL and Vulkan.
- [ ] Define exact bit/field layouts, invalid values, overflow behavior, and
  clear values.
- [ ] Decide whether barycentrics are reconstructed, generated from a fragment
  barycentric capability, or stored in an optional compact attachment.
- [ ] Define the payload version and include it in shader and pipeline cache
  keys.
- [ ] Document how classic triangles, indexed draws, meshlets, and future
  cluster producers encode primitive identity.
- [ ] Reject payload overflow visibly; do not wrap IDs.

### 2. Declared Resources

- [ ] Declare stable resource names for visibility identity, depth/stencil,
  optional barycentrics/coverage, and optional editor-selection sidecars.
- [ ] Specify format, usage flags, clear value, internal resolution, array
  layers, samples, lifetime, resize policy, and debug name.
- [ ] Declare early/late phase indirect arguments and counters as frame-slot
  resources.
- [ ] Declare depth-pyramid levels with current/previous history ownership.
- [ ] Add explicit attachment-to-sampled/storage transitions and matching
  OpenGL barriers.
- [ ] Ensure debug and capture modes add resources through the immutable
  resource profile rather than frame-time creation.

### 3. Visibility Shader Contract

- [ ] Add shared vertex/mesh/fragment visibility shader interfaces.
- [ ] Fetch draw, instance, mesh, transform, and deformation records from the
  canonical scene tables.
- [ ] Emit position and identity without sampling ordinary material color,
  normal, roughness, metalness, emission, or lighting textures.
- [ ] Define front-face, double-sided, depth bias, clipping, and cull-mode
  classes.
- [ ] Add an alpha-coverage-only variant for masked materials using the same
  cutoff, UV transform, texture reference, and sampling convention as native
  material shading.
- [ ] Define specialized displacement visibility variants only when
  displacement changes final depth; otherwise mark the material unsupported.
- [ ] Keep shader variants keyed by vertex format, coverage class, deformation
  mode, view mode, and backend encoding, not material instance.

### 4. Geometry Producers

- [ ] Implement CPU-direct static indexed visibility as the first correctness
  producer.
- [ ] Add CPU-direct pre-skinned visibility using shared current/previous
  deformation arenas.
- [ ] Add zero-readback indirect indexed visibility.
- [ ] Add static and skinned meshlet visibility producers.
- [ ] Preserve instance, material-section, primitive, and editor identity
  across every producer.
- [ ] Add deterministic producer-parity tests: the same scene rendered through
  each producer must decode to the same logical payload.
- [ ] Keep producer selection in the mesh-submission strategy resolver without
  changing the shading architecture.

### 5. Early/Late Visibility Sequence

- [ ] Clear visibility to the invalid sentinel and depth to the active camera's
  correct clear value.
- [ ] Draw the early-visible indirect set.
- [ ] Build the current per-view depth pyramid once.
- [ ] Re-test deferred candidates and draw only newly visible geometry in the
  late visibility phase.
- [ ] Preserve one authoritative final visibility/depth result after both
  phases.
- [ ] Ensure late recovery neither shades early pixels twice nor invalidates
  early identity.
- [ ] Handle camera cuts, resize, newly resident geometry, and missing history
  conservatively.

### 6. Depth, Selection, And Motion Inputs

- [ ] Define jittered versus unjittered position use in visibility raster.
- [ ] Preserve depth convention across normal, reversed, mono, stereo, capture,
  and shadow-related consumers.
- [ ] Prove every valid payload can resolve transform/editor identity without a
  CPU lookup.
- [ ] Preserve current and previous clip-space inputs needed for dense velocity
  reconstruction in document 05.
- [ ] Define velocity invalidation for new, teleported, topology-changed, and
  history-reset surfaces.

### 7. Debugging And Validation

- [ ] Add views for raw payload words, decoded draw ID, primitive ID, material
  ID, kernel ID, selection ID, early/late origin, invalid payload, and depth.
- [ ] Add delayed counters for valid pixels, invalid pixels, overflow, masked
  coverage, early draws, late draws, and recovered candidates.
- [ ] Add a GPU validation mode that bounds-checks every decoded table index.
- [ ] Capture static, overlapping, masked, skinned, meshlet, camera-cut, and
  mixed-producer scenes from at least two camera positions.
- [ ] Verify attachment contents and barriers in OpenGL and Vulkan captures.

## Acceptance Criteria

- [ ] Every visible compatible pixel resolves to a valid scene surface and
  editor identity.
- [ ] Traditional, indirect, meshlet, static, and skinned producers emit the
  same logical payload.
- [ ] The pass samples no ordinary material textures.
- [ ] Masked coverage agrees with native material shading.
- [ ] Late visibility recovers disocclusion in the current frame.
- [ ] The final visibility/depth targets are stable, named, and inspectable.

