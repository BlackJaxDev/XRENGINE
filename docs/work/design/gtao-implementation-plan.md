# GTAO Implementation Plan

Last Updated: 2026-03-11
Current Status: `AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion` now has a dedicated AO family slot, explicit schema controls, a real `VPRC_GTAOPass`, dedicated GTAO gather shaders, and a first edge-aware denoise path. The gather uses slice-wise horizon-angle visibility integration with the canonical `IntegrateArc` formula from Jimenez et al. 2016 (confirmed against Intel XeGTAO reference). The view-direction convention (`planeForwardBase = -normalize(centerPos)`) and the integration formula (`0.25 * (-cos(2h - n) + cos(n) + 2h*sin(n))`) were corrected during review to match the authoritative reference. Still experimental until validated at runtime.
Primary Goal: add a canonical GTAO screen-space AO path without overloading SSAO, MVAO, HBAO+, or the current prototype obscurance branch.

## Recommendation

Treat GTAO as the next serious non-HBAO screen-space AO implementation after the HBAO+ work stabilizes.

Reasoning:

- GTAO has stronger canonical grounding than the current prototype obscurance path.
- It fits the engine's existing deferred AO contract: one scalar AO visibility texture consumed before deferred light combine.
- It gives the renderer a modern non-voxel, non-ray-traced AO family without forcing the engine into VXAO-style voxel infrastructure.

## Current Engine Reality

The engine now has a first real GTAO rendering path.

- `AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion` exists.
- `AmbientOcclusionSettings` contains GTAO-specific settings:
  - `GTAOSliceCount`
  - `GTAOStepsPerSlice`
  - `GTAOFalloffStartRatio`
  - `GTAODenoiseEnabled`
  - `GTAODenoiseRadius`
  - `GTAODenoiseSharpness`
  - `GTAOUseInputNormals`
- `DefaultRenderPipeline.PostProcessing` exposes GTAO-only controls in the AO stage schema.
- `DefaultRenderPipeline` routes GTAO through a dedicated `VPRC_GTAOPass` plus a GTAO-specific resolve chain.
- Dedicated shader files now exist for mono and stereo GTAO gather/denoise.

What is still missing:

- no validation of GTAO parameter semantics against the final gather model
- no confidence yet that the current slice gather matches canonical GTAO quality closely enough across thin geometry, screen edges, and camera motion

## Target Architecture

GTAO should remain within the current AO contract:

- generate AO before deferred light combine
- write one scalar AO visibility texture
- avoid changing the downstream deferred-lighting interface

Suggested runtime shape:

1. Prepare depth and normal inputs for stable view-space reconstruction.
2. Run a dedicated GTAO gather pass using horizon/slice evaluation.
3. Apply a GTAO-specific edge-aware denoise pass.
4. Publish the filtered result to the standard AO texture consumed by deferred light combine.

## Settings Contract

The current implementation reserves and uses the following GTAO-specific settings:

- `Radius`
- `Bias`
- `Power`
- `GTAOSliceCount`
- `GTAOStepsPerSlice`
- `GTAOFalloffStartRatio`
- `GTAODenoiseEnabled`
- `GTAODenoiseRadius`
- `GTAODenoiseSharpness`
- `GTAOUseInputNormals`

Keep these semantics explicit. Do not fold GTAO back into the SSAO or HBAO+ controls.

## Phased Implementation

### Phase 0 - Scaffolding

Completed:

- explicit enum slot
- explicit schema visibility
- explicit pipeline branch
- dedicated resolve chain

### Phase 1 - Gather Pass

- [x] add `VPRC_GTAOPass`
- [x] add dedicated GTAO generation shader(s)
- [x] implement a first slice-based horizon gather using projected slice horizons, angular visibility integration, and per-pixel slice jitter
- [x] write into the standard AO texture path

### Phase 2 - Denoise Pass

- [x] add GTAO-specific edge-aware denoise
- [ ] validate thin geometry preservation and screen-edge stability
- [x] keep denoise controls scoped to GTAO only

### Phase 3 - Validation

- compare GTAO against legacy SSAO, MVAO, and HBAO+
- validate contact shadow quality indoors
- validate outdoor large-radius behavior
- validate stability under camera motion

## Non-Goals

- Do not implement GTAO by aliasing it to HBAO+.
- Do not overload the SSAO noise/kernal path and call it GTAO.
- Do not widen the AO output contract beyond the existing scalar AO texture.

## Key Files

- `XREngine/Rendering/Camera/AmbientOcclusionSettings.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`

## Suggested Next Step

The next GTAO code task should be validation, not more scaffolding: compare this gather against HBAO+, stress-test thin geometry and screen edges, and evaluate motion stability before treating GTAO as production-ready.