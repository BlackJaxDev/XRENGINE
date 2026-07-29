# 05 - Attribute Reconstruction TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [04 - Visibility Buffer Resources And Geometry](04-visibility-buffer-resources-and-geometry-todo.md)
Next: [06 - Visible Material Work Classification](06-visible-material-work-classification-todo.md)

## Goal

Recover material-ready surface attributes from visibility identity with stable
texture derivatives, correct tangent frames, and current/previous temporal
data. This contract is shared by every native material kernel.

## TODO

### 1. Geometry Decode

- [ ] Decode draw, mesh, instance, material, primitive, and view records from
  the visibility payload.
- [ ] Fetch triangle indices and vertex records from the scene-owned geometry
  database.
- [ ] Support indexed classic geometry, meshlet-local geometry, static
  vertices, and shared pre-skinned vertices.
- [ ] Bounds-check and report invalid generations in diagnostic mode.
- [ ] Return a defined invalid-surface result rather than reading arbitrary
  storage.

### 2. Barycentrics And Interpolation

- [ ] Implement the selected hardware, reconstructed, or stored barycentric
  path.
- [ ] Match perspective-correct interpolation for UVs, colors, normals,
  tangents, and custom declared attributes.
- [ ] Reconstruct world position from depth and camera matrices unless a
  material explicitly requires deformed geometric position from vertices.
- [ ] Handle degenerate, clipped, back-facing, and extremely small triangles.
- [ ] Preserve flat-qualified attributes and per-primitive data.
- [ ] Add numeric reference tests against raster interpolants.

### 3. Normal And Tangent Frames

- [ ] Reconstruct geometric and authored normals under current instance
  transforms.
- [ ] Apply correct inverse-transpose/cofactor behavior for non-uniform,
  negative, and mirrored scale.
- [ ] Reconstruct tangent and bitangent handedness using the engine's
  MikkTSpace convention.
- [ ] Validate hard edges, smooth boundaries, UV seams, mirrored UV islands,
  and flat-shaded faces.
- [ ] Keep normal-map sampling orientation identical across OpenGL and Vulkan.

### 4. Texture Derivatives

- [ ] Select and document the production derivative method.
- [ ] Implement analytical barycentric derivatives as the preferred
  cross-material-boundary-safe path.
- [ ] If finite differences or neighbor lookup remain as a fallback, reject
  neighbors with different surface identity and apply a defined conservative
  mip rule.
- [ ] Support explicit gradients for material texture sampling from compute.
- [ ] Validate minification, anisotropy, UV discontinuities, tiny triangles,
  oblique surfaces, and rapidly changing LOD.
- [ ] Add derivative-error and selected-mip debug views.

### 5. Temporal Reconstruction

- [ ] Resolve current and previous instance transforms.
- [ ] Resolve current and previous deformed positions from shared arenas.
- [ ] Compute per-pixel motion in the active upscaler's coordinate convention.
- [ ] Emit validity/reactive information for new, teleported, topology-changed,
  masked-edge, and history-reset pixels.
- [ ] Validate rigid, skinned, blendshape, camera-only, object-only, and
  combined motion.
- [ ] Preserve independent per-eye temporal data.

### 6. Reconstructed Surface Interface

- [ ] Define a compact shader-only `AdvancedSurface` interface containing
  position, depth, geometric normal, shading normal basis, UV sets, vertex
  color, material row, view, current/previous positions, and validity flags.
- [ ] Generate only attributes requested by a kernel's required-attribute
  mask.
- [ ] Keep the interface logical; do not materialize it as a full-screen
  classic GBuffer.
- [ ] Add a diagnostic-only surface dump mode for selected attributes.
- [ ] Add one fullscreen reconstruction/reference shader for correctness
  bring-up, clearly labeled non-production.

### 7. Validation

- [ ] Add CPU/GPU fixtures for triangle decode and interpolation.
- [ ] Add image comparisons against the original pipeline for static,
  skinned, normal-mapped, mirrored, masked, and UV-stress meshes.
- [ ] Add invalid payload, missing geometry, nonresident buffer, and stale
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
- [ ] No production path requires a materialized classic GBuffer.

