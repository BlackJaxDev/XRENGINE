# VXAO Implementation Plan

Last Updated: 2026-03-11
Status: planned scaffold only. The default render pipeline now exposes an explicit `VoxelAmbientOcclusion` / `VXAO` mode, but runtime behavior is still a neutral stub until the shared voxel cone tracing infrastructure is real.
Scope: define how XRENGINE should bring up voxel cone tracing and then layer VXAO on top of that same voxelization path instead of creating a separate voxel owner for AO.

## Executive Summary

VXAO should not own a separate voxelization pipeline. It should consume the same scene voxelization and derived voxel data that voxel cone tracing GI uses.

That changes the work order materially:

1. make the shared voxel cone tracing data path real
2. make that voxel data stable and queryable for lighting and visibility
3. add a VXAO cone-trace resolve that reads the same voxel representation
4. blend VXAO with a short-range screen-space AO layer for contact detail

The engine already has useful starting points:

- `DefaultRenderPipeline.UsesVoxelConeTracing`
- `VoxelConeTracingVolumeTextureName`
- `CreateVoxelConeTracingVolumeTexture()`
- `CreateVoxelConeTracingVoxelizationMaterial()`
- `VPRC_VoxelConeTracingPass` as the current integration hook

Those hooks justify a real shared-voxel plan, but they are not enough to claim either working VCT GI or working VXAO today.

## Current Reality

Relevant current code and docs:

- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_VoxelConeTracingPass.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `docs/features/gi/voxel-cone-tracing.md`
- `docs/work/todo/non-hbao-ambient-occlusion-remediation.md`

What exists today:

- a voxel GI mode switch in the render pipeline
- a 128 x 128 x 128 `RGBA8` 3D volume allocation
- a voxelization material using dedicated vertex, geometry, and fragment shaders
- a placeholder `VPRC_VoxelConeTracingPass` that can clear the volume, bind it as an image, render opaque passes with an override material, and generate mipmaps

What is still missing:

- a defined world-to-voxel transform contract shared by writers and readers
- a voxel payload definition that is sufficient for GI and AO use cases
- a completed voxelization correctness pass for opacity, albedo, emissive, and material handling
- a real cone-trace resolve pass for diffuse GI
- a real cone-trace resolve pass for specular GI if desired
- a real VXAO resolve pass reading the same voxel data
- update, invalidation, clipmap, or camera-centered residency rules
- temporal stabilization and validation for either GI or AO consumers

## Product Position

Recommended product position:

- voxel cone tracing is an advanced GI mode, not the default GI path
- VXAO is an advanced AO mode built on top of the same shared voxel infrastructure
- neither should be treated as production-default until memory, stability, and update costs are validated
- VXAO should complement HBAO+ or GTAO for fine contact detail rather than replace them outright

Recommended non-goal for the first version:

- do not fork a second voxel volume just for AO unless shared VCT data proves fundamentally insufficient

## Shared Voxel Architecture

The renderer should own one shared voxel scene representation that supports both:

- cone-traced indirect lighting
- cone-traced ambient visibility

That shared representation needs four layers of design:

1. voxel space definition
2. voxel payload definition
3. update and residency policy
4. consumer-specific resolve passes

### 1. Voxel Space Definition

The engine needs a single authoritative answer for:

- where the voxel volume is centered
- what world-space extent it covers
- how world positions map into voxel coordinates
- whether the volume is fixed, camera-centered, cascaded, or clipmapped

Recommended first implementation:

- one camera-centered volume with explicit world-space half-extent
- one shared transform uploaded to both voxelization and cone-trace passes
- one consistent convention for near-to-far mip usage during tracing

This should be exposed in engine-level VCT settings first, with VXAO reading those settings rather than inventing conflicting coverage rules.

### 2. Voxel Payload Definition

The current `RGBA8` volume is a placeholder-quality storage choice. Before VCT or VXAO can be considered correct, the engine needs to decide what each voxel stores.

Minimum shared payload questions:

- opacity / occupancy only, or opacity plus albedo
- whether emissive radiance is injected directly during voxelization
- whether normals, anisotropic radiance, or directional lobes are needed
- whether one texture is enough or multiple voxel textures are required

Recommended staged approach:

- Stage A: get stable opacity plus base color voxelization working
- Stage B: add the minimum radiance injection needed for diffuse cone tracing
- Stage C: extend payload only if specular VCT or higher-quality VXAO actually requires it

For VXAO specifically, opacity and conservative occupancy matter more than high-fidelity radiance. For VCT GI, radiance storage and filtering become mandatory.

### 3. Update And Residency Policy

The current pass can optionally clear every frame, but that is not a real residency strategy.

The engine needs explicit policy for:

- full rebuild versus partial updates
- static versus dynamic geometry contribution
- camera motion handling
- clipmap recentering or volume snapping
- when mipmaps are regenerated
- how invalid regions are tracked, if at all

Recommended first implementation:

- full rebuild of a single shared volume whenever VCT is enabled
- clear every frame initially for correctness
- postpone partial updates until the resolve path works and performance is measured

That is less efficient, but it is the fastest path to a correct baseline shared by both VCT and VXAO.

## Bring-Up Plan For Working Voxel Cone Tracing

VXAO should come after this sequence, not before it.

### Phase 0: Honest Scaffolding

Outcome: the engine exposes planned VXAO cleanly without pretending the voxel renderer exists.

- add explicit `VoxelAmbientOcclusion` enum and `VXAO` alias
- expose VXAO-only tuning fields in `AmbientOcclusionSettings`
- expose VXAO selector and planned controls in the post-process schema
- route VXAO through a dedicated neutral stub branch in the pipeline

This phase is complete.

### Phase 1: Make Shared Voxelization Correct

Outcome: the voxel volume contains stable scene occupancy and material data.

Required work:

- define and upload the world-to-voxel transform
- verify the current voxelization shaders write into the correct coordinates
- define what opaque materials contribute to occupancy, albedo, and emissive channels
- verify thin geometry behavior and conservative coverage expectations
- decide whether `RGBA8` remains temporary or must be replaced immediately
- document exactly which render passes voxelize into the shared volume

Deliverable:

- a debug view that shows the populated voxel volume is spatially correct

### Phase 2: Build Shared Voxel Filtering And Mip Policy

Outcome: cone tracing can sample stable coarser representations instead of raw nearest voxels.

Required work:

- define mip generation correctness requirements for the shared volume
- decide whether plain hardware mipmaps are acceptable for the first pass
- add custom voxel filtering if GI leakage or AO over-darkening proves unacceptable
- define how radiance and occupancy channels are filtered differently if necessary

Deliverable:

- a documented mip/filter policy used by both VCT GI and VXAO

### Phase 3: Implement Diffuse VCT Resolve

Outcome: `EGlobalIlluminationMode.VoxelConeTracing` produces visible diffuse indirect lighting.

Required work:

- add a dedicated screen-space VCT resolve pass
- reconstruct world position and normal from the GBuffer
- trace a small set of diffuse cones through the shared voxel volume
- accumulate indirect radiance using the shared voxel mip chain
- composite the result into the existing GI/light combine path

Optional later extension:

- add specular cone tracing after diffuse VCT is stable

Deliverable:

- VCT mode affects the frame with measurable diffuse GI instead of only populating a hidden volume

### Phase 4: Validate VCT Stability And Budget

Outcome: the shared voxel path has a real operating envelope.

Validation areas:

- memory cost at 64 cubed, 128 cubed, and higher tiers
- stability under camera motion
- dynamic object update artifacts
- light leaking through thin walls
- stereo cost and correctness
- whether a single shared volume is adequate or clipmaps are required

Deliverable:

- a documented baseline VCT quality and performance profile

## VXAO On Top Of Shared VCT Infrastructure

Once Phases 1 through 4 above exist, VXAO becomes a consumer of the same voxel scene.

### Phase 5: Add VXAO Resolve Against Shared Voxels

Outcome: a dedicated VXAO pass traces ambient visibility through the same shared voxel volume used by VCT.

Pass requirements:

- reconstruct shaded point and normal from the GBuffer
- trace several cones through the shared voxel volume
- accumulate ambient visibility rather than radiance
- produce AO in the scalar contract already consumed by deferred light combine

Suggested first-pass constraints:

- modest cone count
- limited distance horizon
- predictable step schedule
- optional temporal stabilization behind a toggle

### Phase 6: Blend VXAO With Fine Detail AO

Outcome: VXAO covers medium and longer-range occlusion while a screen-space path restores contact detail.

Recommended strategy:

- keep VXAO responsible for broad ambient visibility
- blend in short-range GTAO or HBAO+ detail near surfaces
- expose blend weight in the AO settings rather than burying it in shader constants

This is why the current scaffold already includes:

- `VXAOCombineWithScreenSpaceDetail`
- `VXAODetailBlend`

### Phase 7: Validate VXAO-Specific Behavior

Outcome: VXAO has an explicit quality and cost bar distinct from GI.

Validation areas:

- off-screen occluder benefit versus screen-space AO
- halo reduction versus HBAO+ and GTAO
- over-darkening in dense interiors
- hybrid blend quality with fine-detail AO
- whether AO needs different voxel resolution from GI in practice

The default assumption should remain that shared voxels are good enough until profiling and visual validation prove otherwise.

### Phase 8: Product Decision

Outcome: VXAO gets a stable product role.

Choose one:

- keep as experimental advanced mode
- ship as a high-end AO option alongside HBAO+ and GTAO
- keep internal and research-only

## Settings Contract

The current scaffold reserves these VXAO settings:

- `Radius`
- `Power`
- `VXAOVoxelGridResolution`
- `VXAOCoverageExtent`
- `VXAOVoxelOpacityScale`
- `VXAOTemporalReuseEnabled`
- `VXAOCombineWithScreenSpaceDetail`
- `VXAODetailBlend`

Interpretation:

- `Radius`: broad AO reach or cone-trace influence radius
- `Power`: final contrast shaping
- `VXAOVoxelGridResolution`: requested shared voxel density for VXAO-quality use, which should map onto shared VCT volume sizing rather than creating a separate AO volume
- `VXAOCoverageExtent`: requested world-space coverage for the shared voxel volume
- `VXAOVoxelOpacityScale`: opacity weighting during voxel accumulation or trace response
- `VXAOTemporalReuseEnabled`: whether temporal stabilization is allowed for the VXAO resolve
- `VXAOCombineWithScreenSpaceDetail`: whether a short-range detail AO stage is blended in
- `VXAODetailBlend`: blend weight for that detail layer

These settings should eventually reconcile with engine-level voxel cone tracing settings so the renderer does not expose contradictory voxel budgets in GI and AO panels.

## Implementation Order Recommendation

Recommended order of work:

1. keep the current explicit VXAO scaffold and honest docs
2. make shared voxelization spatially correct and debuggable
3. define voxel payload and filtering for real cone tracing
4. implement diffuse VCT resolve and make VCT visibly work
5. validate VCT memory, update behavior, and stereo cost
6. add VXAO resolve using the same shared voxel volume
7. blend VXAO with GTAO or HBAO+ detail
8. decide whether shared voxels are sufficient or whether a higher-quality variant is needed later

## Risks

Main risks:

- shared voxel memory cost is too high for default use
- `RGBA8` payload is too weak for acceptable VCT quality
- dynamic scene updates produce visible lag or popping
- thin geometry leaks light and under-occludes AO
- stereo cost is materially worse than screen-space techniques
- shared settings between GI and AO become confusing unless ownership is made explicit
- coarse shared voxels lose contact detail unless hybrid blending is done carefully

## Recommendation

The engine should explicitly treat VXAO as a downstream consumer of working voxel cone tracing infrastructure.

The near-term priority is not a bespoke VXAO implementation. It is getting the shared voxel path to the point where `EGlobalIlluminationMode.VoxelConeTracing` actually produces correct, inspectable, cone-traced GI. Once that exists, VXAO becomes a smaller and cleaner addition:

- reuse the same voxelization
- reuse the same coverage and residency rules
- add a visibility-oriented resolve instead of a radiance-oriented resolve
- blend the result with a short-range screen-space AO layer for detail
