# Deferred+ Render Path TODO

Last Updated: 2026-07-01
Owner: Rendering
Status: Proposed
Target Branch: `rendering-deferred-plus-render-path`

Design source:

- [Deferred+ Render Path Design](../../../design/rendering/deferred-plus-render-path-design.md)
- [Visibility Buffer Rendering TODO](visibility-buffer-rendering-todo.md)
- [Material Table And Texture Binding Ladder TODO](material-table-and-texture-binding-ladder-todo.md)
- [Clustered Light Binning And Deferred MSAA Design](../../../design/rendering/clustered-light-binning-and-deferred-msaa-design.md)
- [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md)
- [Vulkan Descriptor Heap Optimization Design](../../../design/rendering/vulkan-descriptor-heap-optimization-design.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [XRE Virtual Geometry Design](../../../design/rendering/xre-virtual-geometry-design.md)

## Goal

Add `DeferredPlus` as an explicit opaque render path that writes compact
visibility first, classifies visible pixels into froxel/material work, defers
texture evaluation until after visibility is known, and shades through
material-region kernels using shared clustered froxel lighting.

The first implementation should prove the data contracts in compatibility mode
by resolving into the classic GBuffer. Native material-region shading follows
only after visibility, reconstruction, texture binding, fallback routing, and
diagnostics are stable.

## Scope

- Render-path selection, settings, diagnostics, and fallback reporting.
- Shared clustered froxel light lists for Forward+, deferred, and Deferred+.
- Deferred+ visibility resources and geometry pass.
- Visibility payload packing and unpacking.
- Froxel/material classification, material range maps, and overflow handling.
- Compatibility material resolve into `AlbedoOpacity`, `Normal`, and `RMSE`.
- Native material-region shading for standard opaque PBR, then more material
  families.
- Texture binding through the material table and active texture binding rung;
  on Vulkan, prefer descriptor heap over descriptor indexing when available.
- Meshlet, zero-readback indirect, and future virtual-geometry integration.
- Debug views, profiler counters, RenderDoc-friendly resources, and validation
  scenes.

## Non-Goals

- Do not remove the current deferred renderer during this work.
- Do not support alpha-blended transparency in Deferred+ phase 1.
- Do not make arbitrary custom shaders Deferred+ compatible automatically.
- Do not require mesh shaders, virtual geometry, MSAA, stereo, or VR for the
  first usable Deferred+ slice.
- Do not silently fall back from explicitly requested accelerated paths. Report
  the selected path and reason.

## Phase 0 - Branch, Baseline, And Contract

- [ ] Create dedicated branch `rendering-deferred-plus-render-path`.
- [ ] Confirm target validation scenes: `OpaqueDense`,
  `AvatarMaterialDiverse`, `MixedFallback`, `ClusteredLocalLights`, and
  `MaterialRangeStress`.
- [ ] Capture baseline images and profiler captures for current `Deferred`,
  `ForwardPlus`, and `DeferredTexturing` where available.
- [ ] Record current 2D Forward+ tile-light behavior so clustered froxel
  migration can be compared against it.
- [ ] Define `DeferredPlus` render-path enum value and mode selection policy.
- [ ] Define `EDeferredPlusMode`, `EDeferredPlusRegionBackend`,
  `EDeferredPlusDerivativeMode`, and local-light selection settings.
- [ ] Decide the phase-1 visibility payload format: 32-bit packed, 64-bit
  packed, or two 32-bit integer targets.
- [ ] Decide the phase-1 derivative strategy: stored gradients, analytical
  barycentrics, or conservative LOD bias.
- [ ] Define fallback behavior for unsupported payload formats, unsupported
  material classes, missing texture binding rungs, and bounded-list overflow.
- [ ] Define the Deferred+ binding ladder:
  `DescriptorHeap`, `DescriptorIndexing`, `OpenGLBindless`, homogeneous
  `TextureArray`, then coarse fallback.
- [ ] Record that descriptor heap does not merge incompatible render states;
  Deferred+ batching is one draw/dispatch per compatible material kernel,
  layout, and render state class.
- [ ] Add planned diagnostics fields before shader bring-up.

Acceptance criteria:

- [ ] The render-path name, settings, fallback policy, payload format, and
  derivative mode are documented before implementation begins.
- [ ] Baseline captures exist for the scenes used to judge Deferred+ quality and
  performance.

## Phase 1 - Shared Clustered Froxel Lighting

- [ ] Add or adopt backend-neutral clustered froxel lighting resource names:
  `ClusteredFroxelLightRecords`, `ClusteredFroxelLightGrid`,
  `ClusteredFroxelVisibleLightIndices`, `ClusteredFroxelLightCounts`, and
  `ClusteredFroxelOverflowCounters`.
- [ ] Add `ClusteredFroxels` to local light selection settings while retaining
  `TiledForwardPlus2D` as an explicit validation/fallback path.
- [ ] Build froxel light lists with `screen tile X/Y + depth slice Z + eye`.
- [ ] Update forward shaders to compute light-list index from screen XY and
  fragment view depth.
- [ ] Keep directional lights outside local froxel lists.
- [ ] Add debug views for selected froxel, light count per froxel, selected
  depth slice, max-over-Z tile heatmap, and overflow.
- [ ] Add counters for froxel count, light-reference count, max lights in a
  froxel, average lights per froxel, and overflowed froxels.

Acceptance criteria:

- [ ] Forward+ can consume clustered froxel light lists and match the legacy 2D
  tiled path within tolerance on simple local-light scenes.
- [ ] A near-only point light does not affect far-only geometry in the same XY
  tile, and the reverse case also holds.

## Phase 2 - Deferred+ Resource Layout And Selection

- [ ] Declare `DeferredPlusVisibility` as an integer texture with explicit
  format, dimensions, stereo policy, resize behavior, and debug name.
- [ ] Declare optional sidecar resources: `DeferredPlusBarycentrics`,
  `DeferredPlusUvGrad`, and `DeferredPlusNormalBasis`.
- [ ] Declare classification resources:
  `DeferredPlusFroxelGrid`, `DeferredPlusMaterialRangeMap`,
  `DeferredPlusMaterialDepthOrMask`, `DeferredPlusMaterialTileList`,
  `DeferredPlusPixelList`, `DeferredPlusRegionDispatchArgs`, and
  `DeferredPlusOverflowCounters`.
- [ ] Add resource aliases to classic GBuffer targets when compatibility mode is
  active.
- [ ] Add render-path selection plumbing for `Disabled`, `Auto`,
  `Compatibility`, `Native`, and `Required`.
- [ ] Fail visibly or report selected fallback when a requested Deferred+ mode
  lacks required backend features.
- [ ] Add profile capture fields for selected Deferred+ mode, region backend,
  payload format, derivative mode, descriptor backend, texture binding rung,
  fallback reason, and GPU timings.

Acceptance criteria:

- [ ] Deferred+ can be selected explicitly and reports whether it is disabled,
  compatibility, native, or fallback before any material shading ships.
- [ ] Deferred+ resources appear with stable names in RenderDoc or equivalent
  capture tooling.

## Phase 3 - Visibility Geometry Pass

- [ ] Add CPU-direct opaque visibility pass for static meshes.
- [ ] Preserve depth, transform/editor identity, material identity, and
  primitive or meshlet identity.
- [ ] Ensure the visibility pass samples no albedo, normal, roughness,
  metallic, emission, or custom material textures.
- [ ] Add skinned visibility support only when current and previous skinned
  positions are available for velocity and reconstruction.
- [ ] Add zero-readback indirect visibility path where active draw IDs are
  production-capable.
- [ ] Add meshlet visibility path where backend support exists.
- [ ] Route unsupported material/pass classes to current deferred or forward
  fallback with visible counters.
- [ ] Add debug views for visibility ID, material ID, transform ID, depth, and
  invalid payload.

Acceptance criteria:

- [ ] Every visible compatible opaque pixel maps back to a valid material,
  transform, and primitive or meshlet record.
- [ ] Unsupported materials fall back visibly and correctly.
- [ ] Visibility output can be inspected in a GPU capture without decoding
  hidden CPU-only state.

## Phase 4 - Froxel And Material Classification

- [ ] Implement compute classification that reads depth and visibility and
  builds per-froxel material metadata.
- [ ] Group material work by shader compatibility first:
  `shadingKernelId + materialLayoutHash + materialStateClass`, with optional
  `MaterialId` only when needed.
- [ ] Ensure material classification never groups by descriptor set object;
  descriptor heap and descriptor indexing paths must both group by stable
  kernel/layout/material-row data.
- [ ] Build `DeferredPlusMaterialTileList` for active screen tiles grouped by
  material kernel and optional material ID.
- [ ] Build `DeferredPlusPixelList` for high-diversity or high-overdraw tiles
  when the compute backend is enabled.
- [ ] Build `DeferredPlusRegionDispatchArgs` for indirect draw or dispatch.
- [ ] Build a conservative `DeferredPlusMaterialRangeMap` for graphics-region
  bring-up.
- [ ] Optionally write `DeferredPlusMaterialDepthOrMask` for graphics backend
  scoping.
- [ ] Define and implement overflow behavior: compatibility resolve, forward
  fallback, or conservative full-tile pass with visible counter.
- [ ] Add tests for empty scene, single-material scene, mixed-material scene,
  invalid payload, material range false positives, and overflow.

Acceptance criteria:

- [ ] Material dispatch work is bounded by visible material coverage, not by
  source material slot count.
- [ ] Material range maps are conservative: false positives are allowed, false
  negatives are caught by tests or debug validation.
- [ ] Overflow never silently drops pixels.

## Phase 5 - Attribute Reconstruction And Derivatives

- [ ] Define vertex attribute fetch layout for position, normal, tangent, UVs,
  color, skinning data, blendshape data, material ID, and previous-frame data.
- [ ] Reconstruct world position from depth and camera matrices, not from a
  stored world-position target.
- [ ] Implement primitive ID plus barycentric reconstruction for classic meshes.
- [ ] Implement meshlet ID plus local primitive reconstruction for meshlet
  payloads.
- [ ] Implement the chosen derivative mode for UV and normal-map sampling.
- [ ] Prefer analytical barycentric partial derivatives as the long-term path.
- [ ] Validate hard edges, UV seams, mirrored tangents, normal map handedness,
  flat/smooth normal boundaries, and ordinary material boundaries.
- [ ] Add debug views for reconstructed UVs, gradients, normals, tangents,
  roughness, metallic, albedo, and reconstruction error.

Acceptance criteria:

- [ ] Texture mip selection is stable at ordinary UV seams, material
  boundaries, primitive boundaries, and depth discontinuities.
- [ ] Standard opaque static meshes reconstruct attributes within configured
  visual/material tolerances.

## Phase 6 - Compatibility Material Resolve

- [ ] Add a standard opaque PBR material resolve kernel.
- [ ] Fetch material constants from the material table.
- [ ] Fetch texture references through the active texture binding rung,
  preferring descriptor heap resource/sampler heap indices on Vulkan.
- [ ] Store material texture references as backend-neutral resource binding
  refs so descriptor indexing can remain the fallback encoding.
- [ ] Do not rely on CPU-pushed per-material descriptor indices inside the
  resolve; the shader must load texture/resource indices from material rows or
  pass-resource tables.
- [ ] Resolve material textures after visibility is known.
- [ ] Reconstruct `AlbedoOpacity`, `Normal`, and `RMSE` outputs.
- [ ] Preserve `TransformId`, depth, and velocity inputs needed by downstream
  passes.
- [ ] Keep existing deferred decals, AO, GI, clustered deferred lighting, and
  post-processing after compatibility resolve.
- [ ] Add missing texture and nonresident texture fallback diagnostics.
- [ ] Validate normal mapped, emissive, rough/metal, missing texture, and mixed
  fallback scenes.

Acceptance criteria:

- [ ] Compatibility mode reconstructs classic GBuffer outputs well enough for
  existing decals, AO, and lighting to work.
- [ ] Standard opaque PBR materials defer texture sampling until the material
  pass without CPU material binding per source material slot.

## Phase 7 - Native Material-Region Shading

- [ ] Add direct HDR or lighting-output path for the standard PBR kernel.
- [ ] Consume `ClusteredFroxelLightGrid` from the material kernel for local
  point and spot lights.
- [ ] Bind descriptor heaps once per command-buffer scope where supported, then
  shade material regions through material-row heap indices rather than
  descriptor-set binds per material.
- [ ] Keep shared light records, shadow maps, probes, and froxel lists as the
  common lighting inputs.
- [ ] Add unlit/emissive native kernel.
- [ ] Add graphics-region backend with range-map/mask scoping for bring-up.
- [ ] Add compute pixel-list backend for production native Deferred+.
- [ ] Add difference/debug view against compatibility material resolve.
- [ ] Keep classic GBuffer reconstruction available on demand for debug or
  compatibility consumers.

Acceptance criteria:

- [ ] Native mode can shade at least one standard material kernel directly from
  visibility, material table, textures, and froxel light lists.
- [ ] Native and compatibility output match within configured tolerance on
  standard opaque PBR scenes.

## Phase 8 - More Material Families

- [ ] Add editor diagnostics for material Deferred+ compatibility and fallback
  reason.
- [ ] Add generated shader prewarm coverage for Deferred+ variants.
- [ ] Add skin PBR kernel or explicitly keep skin on fallback until a material
  contract is ready.
- [ ] Add one additional engine-owned material family after standard PBR:
  unlit, cloth, terrain, toon, or another validated family.
- [ ] Define material metadata for required vertex attributes, derivative
  requirements, lighting model, fallback path, feature flags, and layout hash.
- [ ] Reject unknown material uniforms as `PerMaterialRequired` or `Invalid`;
  do not silently pack them into Deferred+ material rows.

Acceptance criteria:

- [ ] Material compatibility is visible in the editor and profiler.
- [ ] Unsupported custom shaders and special material classes route to fallback
  with machine-readable reasons.

## Phase 9 - Meshlet, GPU-Driven, And Virtual Geometry Integration

- [ ] Emit Deferred+ visibility from zero-readback indirect draws.
- [ ] Emit Deferred+ visibility from meshlet paths where backend support exists.
- [ ] Integrate with virtual-geometry main/post HZB visibility producers when
  available.
- [ ] Require classic, meshlet, and virtual-geometry visibility producers to
  write the same Deferred+ payload contract.
- [ ] Require GPU-driven Deferred+ material paths to resolve descriptor heap
  indices from draw/material GPU data, not from CPU push data per indirect draw.
- [ ] Avoid CPU readbacks for material region counts, visible cluster counts,
  and dispatch counts in production mode.
- [ ] Add delayed-readback diagnostics only for counters and debugging.
- [ ] Validate with material-diverse avatar and dense static scenes.

Acceptance criteria:

- [ ] Production Deferred+ mode performs no same-frame CPU readbacks in the
  steady-state render path.
- [ ] Meshlet and CPU-direct visibility producers resolve through the same
  material classification and resolve path.

## Phase 10 - MSAA, Stereo, And VR Follow-Up

- [ ] Keep Deferred+ disabled in VR by default until per-eye captures and
  temporal inputs are proven correct.
- [ ] Add MSAA complex-pixel classification.
- [ ] Add per-sample visibility or explicit edge/complex-pixel masks.
- [ ] Add simple and complex tile lists for MSAA material shading.
- [ ] Add stereo-array or per-eye resource declarations.
- [ ] Validate one froxel grid per eye unless a proven shared head-space grid is
  available.
- [ ] Validate per-eye projection in attribute reconstruction.
- [ ] Validate motion vectors and temporal history isolation.

Acceptance criteria:

- [ ] Deferred+ stereo and MSAA paths remain opt-in until visual captures,
  logs, and GPU captures prove correctness.
- [ ] Per-eye captures show no shared-depth, shared-history, or wrong-projection
  artifacts.

## Final Validation And Merge

- [ ] Run source/unit tests for render-path selection, fallback policy,
  visibility payload packing, material classification, material range maps,
  material binding layout compatibility, and shader prewarm keys.
- [ ] Run runtime smoke scenes: static textured opaque mesh,
  material-diverse opaque scene, missing texture fallback, normal mapped
  material, clustered local lights in different depth slices, and mixed
  Deferred+/fallback content.
- [ ] Capture RenderDoc or equivalent GPU evidence for visibility payload,
  froxel grid, material region buffers, range map, optional material mask/depth
  target, reconstructed attributes, material texture descriptors, material
  resolve outputs, descriptor heap/resource table state where available, and
  native lighting output.
- [ ] Capture performance comparison against current deferred and Forward+:
  geometry bandwidth estimate, geometry pass GPU time, classification GPU time,
  material shading GPU time, lighting GPU time, material region count, overdraw,
  descriptor backend, descriptor bind/push count, and fallback pixel count.
- [ ] Update design, architecture, settings, diagnostics, and developer docs for
  final payload format, settings, fallback policy, validation scenes, and known
  limitations.
- [ ] Merge branch `rendering-deferred-plus-render-path` back into `main` after
  implementation, validation, and documentation updates are complete.
