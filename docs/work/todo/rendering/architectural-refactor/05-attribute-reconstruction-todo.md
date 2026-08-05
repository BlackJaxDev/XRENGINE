# 05 - Attribute Reconstruction TODO

Last Updated: 2026-08-05
Owner: Rendering
Status: Phase-Owned Implementation Complete - live pipeline validation pending activation
Depends On: [04 - Visibility Buffer Resources And Geometry](04-visibility-buffer-resources-and-geometry-todo.md)
Next: [Visible Material Work Classification](../vulkan-core-hardening-and-device-loss-todo.md#10-classify-visible-material-work-on-the-gpu)

## Goal

Recover material-ready surface attributes from visibility identity with stable
texture derivatives, correct tangent frames, and current/previous temporal
data. This contract is shared by every native material kernel.

## Completion Boundary

This slice now owns the versioned shader-only surface ABI, stable-table decode,
indexed and meshlet primitive decode, static and shared pre-skinned vertex
decode, perspective-correct interpolation, tangent-frame reconstruction,
analytical derivatives, conservative mip fallback, per-eye temporal validity,
immutable diagnostics, synchronization boundaries, and focused CPU/shader
tests. Production reconstruction is on demand inside material kernels and
does not materialize a classic GBuffer.

The advanced backend remains capability-gated. Phase 04 is not yet executing
visibility against live published scene/material GPU tables, and phase 06 has
not installed classification and native material kernels. The unchecked items
below are therefore live image parity, motion-scene evidence, measured GPU
cost, and OpenGL/Vulkan capture evidence rather than missing phase-owned ABI,
resource, or shader implementation.

## TODO

### 1. Geometry Decode

- [x] Decode draw, mesh, instance, material, primitive, and view records from
  the visibility payload.
- [x] Fetch triangle indices and vertex records from the scene-owned geometry
  database.
- [x] Support indexed classic geometry, meshlet-local geometry, static
  vertices, and shared pre-skinned vertices.
- [x] Bounds-check and report invalid generations in diagnostic mode.
- [x] Return a defined invalid-surface result rather than reading arbitrary
  storage.

### 2. Barycentrics And Interpolation

- [x] Implement the selected hardware, reconstructed, or stored barycentric
  path.
- [x] Match perspective-correct interpolation for UVs, colors, normals,
  tangents, and custom declared attributes.
- [x] Reconstruct world position from depth and camera matrices unless a
  material explicitly requires deformed geometric position from vertices.
- [x] Handle degenerate, clipped, back-facing, and extremely small triangles.
- [x] Preserve flat-qualified attributes and per-primitive data.
- [x] Add numeric reference tests against raster interpolants.

### 3. Normal And Tangent Frames

- [x] Reconstruct geometric and authored normals under current instance
  transforms.
- [x] Apply correct inverse-transpose/cofactor behavior for non-uniform,
  negative, and mirrored scale.
- [x] Reconstruct tangent and bitangent handedness using the engine's
  MikkTSpace convention.
- [x] Validate hard edges, smooth boundaries, UV seams, mirrored UV islands,
  and flat-shaded faces.
- [x] Keep normal-map sampling orientation identical across OpenGL and Vulkan.

### 4. Texture Derivatives

- [x] Select and document the production derivative method.
- [x] Implement analytical barycentric derivatives as the preferred
  cross-material-boundary-safe path.
- [x] If finite differences or neighbor lookup remain as a fallback, reject
  neighbors with different surface identity and apply a defined conservative
  mip rule.
- [x] Support explicit gradients for material texture sampling from compute.
- [x] Validate minification, anisotropy, UV discontinuities, tiny triangles,
  oblique surfaces, and rapidly changing LOD.
- [x] Add derivative-error and selected-mip debug views.

### 5. Temporal Reconstruction

- [x] Resolve current and previous instance transforms.
- [x] Resolve current and previous deformed positions from shared arenas.
- [x] Compute per-pixel motion in the active upscaler's coordinate convention.
- [x] Emit validity/reactive information for new, teleported, topology-changed,
  masked-edge, and history-reset pixels.
- [ ] Validate rigid, skinned, blendshape, camera-only, object-only, and
  combined motion.
- [x] Preserve independent per-eye temporal data.

### 6. Reconstructed Surface Interface

- [x] Define a compact shader-only `AdvancedSurface` interface containing
  position, depth, geometric normal, shading normal basis, UV sets, vertex
  color, material row, view, current/previous positions, and validity flags.
- [x] Generate only attributes requested by a kernel's required-attribute
  mask.
- [x] Keep the interface logical; do not materialize it as a full-screen
  classic GBuffer.
- [x] Add a diagnostic-only surface dump mode for selected attributes.
- [x] Add one fullscreen reconstruction/reference shader for correctness
  bring-up, clearly labeled non-production.

### 7. Validation

- [x] Add CPU/GPU fixtures for triangle decode and interpolation.
- [ ] Add image comparisons against the original pipeline for static,
  skinned, normal-mapped, mirrored, masked, and UV-stress meshes.
- [x] Add invalid payload, missing geometry, nonresident buffer, and stale
  generation tests.
- [ ] Measure reconstruction cost separately from classification and shading.
- [ ] Inspect attributes and derivatives in GPU captures on OpenGL and Vulkan.

## Acceptance Criteria

- [ ] Standard material attributes match raster reference within documented
  numeric and image tolerances.
- [ ] Texture LOD is stable across primitive and material boundaries.
- [ ] Static and deformed current/previous positions produce dense valid
  velocity.
- [ ] Material kernels share one reconstruction contract.
- [x] No production path requires a materialized classic GBuffer.

