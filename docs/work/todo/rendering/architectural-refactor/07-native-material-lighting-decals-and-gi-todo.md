# 07 - Native Material, Lighting, Decal, And GI Shading TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [06 - Visible Material Work Classification](06-visible-material-work-classification-todo.md)
Next: [08 - Transparency, Special Passes, And Post-Processing](08-transparency-special-passes-and-post-processing-todo.md)

## Goal

Shade compatible opaque and masked surfaces directly into the advanced opaque
HDR output using reconstructed attributes, material rows, visible material
work, clustered lights, shadows, decals, probes, and the selected GI provider.
Do not reconstruct the classic deferred-lighting graph as the production path.

## TODO

### 1. Native Kernel Interface

- [ ] Define a generated/authored kernel interface receiving
  `AdvancedSurface`, material row, view record, light/decal ranges, shadow
  tables, environment/probe data, and GI resources.
- [ ] Define outputs for opaque HDR, dense velocity, temporal/reactive masks,
  exposure/luminance inputs, and only the minimal optional sidecars required by
  later effects.
- [ ] Load textures through material-row references and the active global
  texture-indirection rung.
- [ ] Bind global scene/material/light/texture tables once per compatible
  command scope.
- [ ] Compile one kernel per material family/layout/feature contract, not per
  material instance.
- [ ] Add explicit missing-kernel, pending-compile, invalid-layout, and
  nonresident-texture behavior.

### 2. Standard Material Families

- [ ] Implement standard opaque PBR first.
- [ ] Add masked PBR using the coverage decision already established by the
  visibility pass.
- [ ] Add unlit/emissive.
- [ ] Add the next engine-owned families in measured priority order: skin,
  cloth, terrain, toon, hair cards, or other production requirements.
- [ ] Define custom-material opt-in metadata and reject undeclared arbitrary
  shader state.
- [ ] Add kernel prewarm and permutation-budget telemetry.

### 3. Clustered Lighting

- [ ] Define one backend-neutral froxel grid per view using screen tile X/Y and
  depth slice Z.
- [ ] Build local point and spot light lists on GPU.
- [ ] Keep directional lights in a bounded global list.
- [ ] Share the same light records and froxel indexing across all native
  material kernels.
- [ ] Define overflow and conservative recovery without dropping light
  contribution silently.
- [ ] Add froxel occupancy, light count, overflow, and selected-light debug
  views.

### 4. Shadows

- [ ] Publish directional, point, spot, cascade, atlas, filter, and fallback
  metadata through GPU shadow records rather than large per-program uniform
  sets.
- [ ] Preserve existing relevance, dirty-tile, stale-tile, and contact-shadow
  policies where they remain valid.
- [ ] Make every material kernel use shared shadow sampling helpers.
- [ ] Consume reconstructed screen position/depth consistently under normal and
  reversed depth.
- [ ] Add machine-readable missing/stale/unavailable shadow fallback.
- [ ] Validate cascade transitions, atlas edges, cubemap seams, filter modes,
  and stereo addressing.

### 5. Ambient Occlusion

- [ ] Decide the advanced AO contract: depth-only plus reconstructed normal,
  compact normal sidecar, or provider-specific visibility sampling.
- [ ] Run AO before the lighting contribution that consumes it.
- [ ] Avoid recreating a multi-channel GBuffer solely for AO compatibility.
- [ ] Adapt supported AO providers to declared advanced inputs.
- [ ] Mark unsupported providers unavailable for the advanced pipeline rather
  than silently invoking legacy resources.
- [ ] Validate coordinates, depth convention, half/full resolution, stereo,
  temporal history, and camera cuts.

### 6. Decals And Surface Modifiers

- [ ] Build per-tile/froxel decal lists.
- [ ] Apply compatible decals as material/surface modifiers before lighting,
  using reconstructed position and normal basis.
- [ ] Define ordering, blend semantics, normal blending, material filters, and
  overflow.
- [ ] Route geometry-changing or unsupported decals to an explicit special
  path or error state.
- [ ] Do not require classic deferred decal GBuffer writes.

### 7. Environment, Probes, And GI

- [ ] Publish IBL and light-probe lookup through shared GPU records.
- [ ] Define a narrow `IAdvancedGlobalIlluminationProvider` contract for
  radiance/irradiance queries and optional screen-space outputs.
- [ ] Adapt supported probe, surfel, radiance-cascade, voxel, ReSTIR, or other
  providers without full-frame light-combine compositing.
- [ ] Ensure only one selected GI mode contributes unless an explicitly
  documented blend is requested.
- [ ] Expose unavailable providers and required resources before rendering.
- [ ] Validate invalid history, missing probes, provider switches, and stereo.

### 8. Background And Uncovered Pixels

- [ ] Shade visibility-sentinel pixels through the selected sky/background
  contract.
- [ ] Preserve atmospheric sky inputs without drawing an ordinary opaque
  forward background mesh where a compute/background kernel suffices.
- [ ] Define clear color, alpha, HDR encoding, and external capture behavior.
- [ ] Keep procedural/custom background geometry as an explicit compatible
  visibility producer or special pass.

### 9. Debugging And Validation

- [ ] Add views for reconstructed albedo, normal, roughness, metalness,
  emission, AO, direct light, indirect light, shadow factor, decal contribution,
  kernel ID, and final opaque HDR.
- [ ] Add a diagnostic difference view against the original pipeline without
  using it in production execution.
- [ ] Add material/light/shadow/GI fixtures and image tolerances.
- [ ] Record GPU time per classification, kernel family, lighting, shadow, AO,
  decal, and GI stage.
- [ ] Validate OpenGL and Vulkan with captures showing no classic GBuffer or
  deferred light-combine dependency.

## Acceptance Criteria

- [ ] Standard opaque and masked PBR shade directly from visibility to HDR.
- [ ] Lighting work uses shared froxel and shadow records.
- [ ] Decals modify native surface shading without classic GBuffer writes.
- [ ] Supported AO and GI providers consume advanced inputs through explicit
  contracts.
- [ ] Material instances sharing a kernel require no per-instance pipeline or
  descriptor bind.
- [ ] The production advanced path contains no classic deferred light
  accumulation or light-combine stage.

