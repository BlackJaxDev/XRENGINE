# Voxel Cone Tracing And VXAO TODO

Last Updated: 2026-10-06
Status: Planned (VCT provider unavailable; VXAO is a stub)
Architecture: [Voxel Cone Tracing guide](../../../../developer-guides/gi/voxel-cone-tracing.md), [Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md)  Design: [VXAO Implementation Plan](../../../design/global-illumination/vxao-implementation-plan.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#voxel-cone-tracing-and-vxao), [Ambient Occlusion Validation](../../../testing/rendering/ambient-occlusion-validation.md)

## Current State

`EGlobalIlluminationMode.VoxelConeTracing` is an unavailable descriptor with no module factory. No C# VCT pass exists; the old `VPRC_VoxelConeTracingPass` was removed with the host GI flags. Old shaders remain under `Build/CommonAssets/Shaders/Scene3D/VoxelConeTracing/` (`voxelization.vert/.geom/.frag`, `voxel_cone_tracing.vert/.frag`, `voxel_visualization.vert/.frag`). `AmbientOcclusionSettings.EType.VoxelAmbientOcclusion` and `VoxelAmbientOcclusionSettings` exist. `DefaultRenderPipeline.CreateVXAOPassCommands` adds a `VPRC_AODisabledPass` with a "not implemented" diagnostic. VXAO and VCT will share one voxel scene.

## Open Code Items

### Shared Voxel Contract

- [ ] Choose the first volume model (fixed, camera-centered, cascade, or clipmap) and define the coverage parameters and the world-to-voxel transform used by every writer and reader. Put the shared resolution and coverage settings in one settings owner. Done when: one settings type and one transform block exist, and VCT and VXAO both read them.
- [ ] Define the voxel payload: channels, precision, emissive injection, and whether normals or directional radiance are needed. Split into several textures if one is not enough. Done when: the payload layout is in code and in the VCT guide.
- [ ] Define the mip and filter policy for occupancy and radiance, with custom filtering if they need different treatment. Done when: the mip generation path is explicit in code.

### Voxelization

- [ ] Port the voxelization shaders into a VCT-owned GI module that declares its own resources and passes with neutral host inputs only. Done when: the module is registered as unsupported and inactive selection allocates nothing.
- [ ] Make voxelization write correct coordinates with conservative thin-geometry coverage, and handle occupancy, base color, alpha cutout, and emission. Done when: the voxel writes use the shared transform.
- [ ] Add a voxel volume debug view (occupancy and material slices). Done when: the view is selectable in the editor.

### Diffuse VCT Resolve

- [ ] Add a VCT resolve pass that reconstructs position and normal from the G-buffer, traces a small set of diffuse cones through the mip chain, and outputs material-shaded linear HDR diffuse for neutral composition. Done when: the module contributes the resolve at the `SurfaceResolve` anchor.
- [ ] Add a raw VCT contribution debug mode. Done when: the mode isolates the VCT term.

### VXAO

- [ ] Replace the VXAO stub with a resolve that traces visibility cones through the shared voxel volume and writes the scalar AO contract used by deferred light combine. Done when: `CreateVXAOPassCommands` no longer adds `VPRC_AODisabledPass`.
- [ ] Add a raw VXAO debug view. Done when: the view is selectable.
- [ ] Blend VXAO with a screen-space detail AO through `VXAOCombineWithScreenSpaceDetail` and `VXAODetailBlend`. Done when: both settings change the output.

### Settings Ownership

- [ ] Split shared voxel settings from VXAO resolve settings, remove duplicated voxel budgets, and label editor controls by scope. Done when: no voxel resolution or coverage setting exists in two places.

## Decisions Needed

- [ ] Choose GTAO or HBAO+ as the default detail companion for VXAO. Owner: rendering lead.
- [ ] Decide whether one shared volume is enough or clipmaps are needed, after the budget checks. Owner: rendering lead.
- [ ] Set the support level of VCT and VXAO (experimental, advanced, or research-only). Owner: rendering lead.

## Out Of Scope

- A separate AO-only voxelization pipeline.
- VXAO as a replacement for short-range HBAO+ or GTAO contact shadows.
- Partial voxel updates before a correct full-rebuild baseline exists.
- Specular cone tracing before diffuse VCT is stable.
