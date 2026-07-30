# 04 - Visibility Buffer Resources And Geometry TODO

Last Updated: 2026-07-29
Owner: Rendering
Status: Phase-Owned Implementation Complete - live backend execution and GPU captures pending pipeline activation
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

## Completion Boundary

This slice now owns the frozen visibility ABI, immutable render resources,
five geometry-producer contracts, raster/coverage/mesh shaders, early/HZB/late
sequence, synchronization states, diagnostics, and focused tests. Depth-changing
displacement is rejected until a producer-complete specialized variant exists;
it cannot silently use the non-displaced raster path.

The advanced backend remains capability-gated. Its command currently publishes
the render-graph resource usages and acquires shared preparation, but the
phase-02 canonical scene/material database is not yet published and bound as
live GPU tables and the phase-05+ consumers are not installed. Enabling the
backend before that integration would execute these shaders against missing
tables. The unchecked items below are therefore live execution, image-parity,
and OpenGL/Vulkan capture evidence rather than missing phase-owned ABI or shader
assets.

## TODO

### 1. Format And Capacity Decision

- [x] Inventory maximum simultaneous draw, instance, primitive, meshlet,
  material, view, and editor-ID ranges for target scenes.
- [x] Compare two `R32_UINT` attachments, one `RG32_UINT` attachment, packed
  64-bit identity, and narrower sidecar options on OpenGL and Vulkan.
- [x] Define exact bit/field layouts, invalid values, overflow behavior, and
  clear values.
- [x] Decide whether barycentrics are reconstructed, generated from a fragment
  barycentric capability, or stored in an optional compact attachment.
- [x] Define the payload version and include it in shader and pipeline cache
  keys.
- [x] Document how classic triangles, indexed draws, meshlets, and future
  cluster producers encode primitive identity.
- [x] Reject payload overflow visibly; do not wrap IDs.

### 2. Declared Resources

- [x] Declare stable resource names for visibility identity, depth/stencil,
  optional barycentrics/coverage, and optional editor-selection sidecars.
- [x] Specify format, usage flags, clear value, internal resolution, array
  layers, samples, lifetime, resize policy, and debug name.
- [x] Declare early/late phase indirect arguments and counters as frame-slot
  resources.
- [x] Declare depth-pyramid levels with current/previous history ownership.
- [x] Add explicit attachment-to-sampled/storage transitions and matching
  OpenGL barriers.
- [x] Ensure debug and capture modes add resources through the immutable
  resource profile rather than frame-time creation.

### 3. Visibility Shader Contract

- [x] Add shared vertex/mesh/fragment visibility shader interfaces.
- [x] Fetch draw, instance, mesh, transform, and deformation records from the
  canonical scene tables.
- [x] Emit position and identity without sampling ordinary material color,
  normal, roughness, metalness, emission, or lighting textures.
- [x] Define front-face, double-sided, depth bias, clipping, and cull-mode
  classes.
- [x] Add an alpha-coverage-only variant for masked materials using the same
  cutoff, UV transform, texture reference, and sampling convention as native
  material shading.
- [x] Define specialized displacement visibility variants only when
  displacement changes final depth; otherwise mark the material unsupported.
- [x] Keep shader variants keyed by vertex format, coverage class, deformation
  mode, view mode, and backend encoding, not material instance.

### 4. Geometry Producers

- [x] Implement CPU-direct static indexed visibility as the first correctness
  producer.
- [x] Add CPU-direct pre-skinned visibility using shared current/previous
  deformation arenas.
- [x] Add zero-readback indirect indexed visibility.
- [x] Add static and skinned meshlet visibility producers.
- [x] Preserve instance, material-section, primitive, and editor identity
  across every producer.
- [x] Add deterministic producer-parity tests: the same scene rendered through
  each producer must decode to the same logical payload.
- [x] Keep producer selection in the mesh-submission strategy resolver without
  changing the shading architecture.

### 5. Early/Late Visibility Sequence

- [x] Clear visibility to the invalid sentinel and depth to the active camera's
  correct clear value.
- [ ] Draw the early-visible indirect set.
- [x] Build the current per-view depth pyramid once.
- [ ] Re-test deferred candidates and draw only newly visible geometry in the
  late visibility phase.
- [x] Preserve one authoritative final visibility/depth result after both
  phases.
- [x] Ensure late recovery neither shades early pixels twice nor invalidates
  early identity.
- [x] Handle camera cuts, resize, newly resident geometry, and missing history
  conservatively.

### 6. Depth, Selection, And Motion Inputs

- [x] Define jittered versus unjittered position use in visibility raster.
- [x] Preserve depth convention across normal, reversed, mono, stereo, capture,
  and shadow-related consumers.
- [x] Prove every valid payload can resolve transform/editor identity without a
  CPU lookup.
- [x] Preserve current and previous clip-space inputs needed for dense velocity
  reconstruction in document 05.
- [x] Define velocity invalidation for new, teleported, topology-changed, and
  history-reset surfaces.

### 7. Debugging And Validation

- [x] Add views for raw payload words, decoded draw ID, primitive ID, material
  ID, kernel ID, selection ID, early/late origin, invalid payload, and depth.
- [x] Add delayed counters for valid pixels, invalid pixels, overflow, masked
  coverage, early draws, late draws, and recovered candidates.
- [x] Add a GPU validation mode that bounds-checks every decoded table index.
- [ ] Capture static, overlapping, masked, skinned, meshlet, camera-cut, and
  mixed-producer scenes from at least two camera positions.
- [ ] Verify attachment contents and barriers in OpenGL and Vulkan captures.

## Acceptance Criteria

- [ ] Every visible compatible pixel resolves to a valid scene surface and
  editor identity.
- [ ] Traditional, indirect, meshlet, static, and skinned producers emit the
  same logical payload.
- [x] The pass samples no ordinary material textures.
- [ ] Masked coverage agrees with native material shading.
- [ ] Late visibility recovers disocclusion in the current frame.
- [ ] The final visibility/depth targets are stable, named, and inspectable.

