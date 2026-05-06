# Voxel Cone Tracing And VXAO Implementation TODO

Last Updated: 2026-03-11
Current Status: the renderer has a placeholder voxel volume allocation, a voxelization material, and a placeholder `VPRC_VoxelConeTracingPass` that can populate a 3D texture and generate mipmaps. The default AO pipeline now also exposes a planned `VoxelAmbientOcclusion` / `VXAO` mode, but both working voxel cone tracing GI and working VXAO are still unimplemented.
Scope: bring up a real shared voxel scene representation for voxel cone tracing GI first, then add VXAO as a second resolve path reading that same voxel data.

## Current Reality

What is already true:

- `EGlobalIlluminationMode.VoxelConeTracing` exists and routes through `DefaultRenderPipeline`.
- `VoxelConeTracingVolumeTextureName` is allocated as a 128 cubed `RGBA8` 3D texture with mip generation enabled.
- `CreateVoxelConeTracingVoxelizationMaterial()` builds a dedicated voxelization material using voxelization shaders.
- `VPRC_VoxelConeTracingPass` can clear the shared volume, render opaque passes with the override material, and generate mipmaps.
- `AmbientOcclusionSettings` now exposes a planned `VoxelAmbientOcclusion` / `VXAO` AO family with explicit settings and an honest stub path.

What remains missing:

- no explicit world-to-voxel transform contract shared by voxel writers and cone-trace readers
- no agreed shared voxel payload contract for occupancy, albedo, emissive, and radiance
- no verified voxel debug workflow proving the written volume is spatially correct
- no working diffuse voxel cone tracing resolve integrated into GI composition
- no working VXAO resolve integrated into AO composition
- no update or residency policy beyond optional per-frame clearing
- no documented budget for resolution, coverage, memory, or stereo cost

## Target Outcome

At the end of this work:

- `EGlobalIlluminationMode.VoxelConeTracing` produces visible, inspectable diffuse GI
- VXAO uses the same shared voxelization and volume settings as VCT
- the engine has one authoritative voxel coverage and update policy rather than separate GI and AO voxel ownership
- the editor and docs describe voxel features honestly as advanced, high-cost techniques with explicit limits

## Non-Goals

- Do not create a separate AO-only voxelization pipeline unless the shared voxel path is proven insufficient.
- Do not attempt to make VXAO replace short-range HBAO+ or GTAO contact shadowing.
- Do not optimize partial updates before the first correct full-rebuild baseline exists.
- Do not introduce specular cone tracing in the first milestone unless diffuse VCT is already stable.

## Phase 0 - Lock The Shared Voxel Contract

Outcome: the engine has one explicit shared voxel ownership model for both VCT and VXAO.

- [ ] document that VXAO consumes the same shared voxelization path as VCT
- [ ] choose the first shared volume model: fixed volume, camera-centered volume, cascade, or clipmap
- [ ] define the world-space coverage parameters that own voxel bounds
- [ ] define the world-to-voxel transform data that all voxel shaders and resolve passes will use
- [ ] identify the canonical engine settings owner for shared voxel resolution and coverage

Acceptance criteria:

- VCT GI and VXAO no longer imply separate voxel ownership in code or docs
- one shared settings contract exists for voxel resolution, coverage, and transform data

## Phase 1 - Make Voxelization Spatially Correct

Outcome: the shared voxel volume contains correct occupancy and base material data.

- [ ] verify the current voxelization shaders write to correct voxel coordinates
- [ ] define which render passes contribute to voxelization and why
- [ ] verify opaque material handling for occupancy, base color, alpha cutout, and emissive contribution
- [ ] test thin geometry coverage and conservative write behavior
- [ ] add a debug view or inspection path for the voxel volume contents
- [ ] document the expected first-pass limitations of the voxelization path

Acceptance criteria:

- a debug workflow can prove the shared voxel scene is spatially correct
- occupied space, empty space, and major material contributions are visible and explainable

## Phase 2 - Define The Shared Voxel Payload

Outcome: the engine knows what data the shared voxel scene actually stores.

- [ ] decide whether the first payload is occupancy only, occupancy plus albedo, or occupancy plus radiance-ready data
- [ ] decide whether `RGBA8` is an acceptable first payload or only a temporary placeholder
- [ ] define how emissive energy is injected into the shared voxel scene
- [ ] define whether normals or directional radiance information are required for the first GI milestone
- [ ] split the voxel payload into multiple textures if one shared texture is not sufficient

Acceptance criteria:

- the shared voxel format is documented in terms of channel meaning, precision, and intended consumers
- both VCT and VXAO can point to the same voxel payload contract

## Phase 3 - Establish Filtering And Mip Policy

Outcome: cone tracing uses a deliberate shared filtering policy instead of accidental texture behavior.

- [ ] define how the shared volume generates and uses mip levels
- [ ] decide whether hardware mip generation is acceptable for the first baseline
- [ ] test whether current mip behavior causes unacceptable GI leaking or AO over-darkening
- [ ] add custom filtering if occupancy and radiance need different treatment
- [ ] document the tracing assumptions for near versus far mip usage

Acceptance criteria:

- shared mip/filter policy is explicit and documented
- both VCT and VXAO resolve work can target the same sampling rules

## Phase 4 - Implement Diffuse VCT Resolve

Outcome: voxel cone tracing GI visibly affects the frame.

- [ ] add a dedicated VCT resolve pass that reconstructs world position and normal from the GBuffer
- [ ] trace a small set of diffuse cones through the shared voxel volume
- [ ] accumulate indirect radiance from the shared voxel mip chain
- [ ] composite the diffuse VCT result into the existing GI or light-combine path
- [ ] add at least one debug mode to inspect raw VCT contribution

Acceptance criteria:

- enabling `EGlobalIlluminationMode.VoxelConeTracing` changes the rendered frame with visible diffuse GI
- the GI contribution can be isolated or debugged without guessing

## Phase 5 - Validate VCT Budget And Stability

Outcome: the shared voxel GI path has a real operating envelope.

- [ ] measure memory cost for 64 cubed, 128 cubed, and at least one higher tier
- [ ] test stability under camera motion and scene changes
- [ ] test dynamic object update artifacts with full rebuilds
- [ ] evaluate leakage through thin walls and small geometry loss
- [ ] validate stereo correctness and performance
- [ ] decide whether a single shared volume is sufficient or whether clipmaps are required later

Acceptance criteria:

- the team has a documented baseline for cost, stability, and known artifact classes
- any need for clipmaps or different volume ownership is justified by evidence rather than guesswork

## Phase 6 - Implement VXAO Resolve On Shared Voxels

Outcome: VXAO becomes a visibility-oriented consumer of the same voxel scene used by VCT.

- [ ] add a dedicated VXAO resolve pass reading the shared voxel volume
- [ ] reconstruct shaded point and normal from the GBuffer for VXAO sampling
- [ ] trace ambient-visibility cones instead of radiance cones
- [ ] output AO in the scalar contract already consumed by deferred light combine
- [ ] add a raw VXAO debug view or inspection mode

Acceptance criteria:

- selecting `VoxelAmbientOcclusion` uses shared voxel data instead of the current stub
- VXAO is visibly distinct from screen-space AO in off-screen occluder cases

## Phase 7 - Add Hybrid Detail AO Blending

Outcome: shared-voxel VXAO handles broad visibility while screen-space AO restores contact detail.

- [ ] decide whether GTAO or HBAO+ is the default short-range detail companion for VXAO
- [ ] implement blending controlled by `VXAOCombineWithScreenSpaceDetail`
- [ ] implement blending controlled by `VXAODetailBlend`
- [ ] validate that hybrid blending reduces coarse voxel loss on contact shadows

Acceptance criteria:

- VXAO no longer has to carry all fine contact shadow detail by itself
- hybrid quality is explainable and tunable in the editor

## Phase 8 - Reconcile Shared Settings Ownership

Outcome: GI and AO panels do not fight over voxel settings.

- [ ] decide which settings stay engine-level VCT settings and which remain AO-facing tuning knobs
- [ ] remove or remap any VXAO settings that duplicate shared VCT ownership poorly
- [ ] ensure editor wording makes it clear which settings affect the shared voxel scene versus only the VXAO resolve
- [ ] update docs to reflect the final ownership model

Acceptance criteria:

- there is no contradictory voxel budget exposed between GI and AO settings
- users can tell which controls affect shared voxelization and which affect only the AO resolve

## Phase 9 - Product Decision

Outcome: voxel features get a deliberate exposure level.

- [ ] decide whether VCT remains experimental, advanced, or production-supported
- [ ] decide whether VXAO is exposed by default, as advanced only, or as research-only
- [ ] mirror the final readiness and support level into stable docs if these modes remain user-facing

Acceptance criteria:

- both VCT and VXAO have explicit support language and documented expectations

## Key Files

- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_VoxelConeTracingPass.cs`
- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs`
- `docs/features/gi/voxel-cone-tracing.md`
- `docs/work/design/vxao-implementation-plan.md`

## Suggested Next Step

The next concrete implementation task after this TODO should be Phase 0: lock the shared voxel contract and then Phase 1: add a voxel debug workflow that proves the current voxelization pass writes into the correct shared volume with correct coverage.