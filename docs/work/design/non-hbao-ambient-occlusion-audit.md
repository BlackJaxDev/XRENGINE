# Non-HBAO Ambient Occlusion Audit

Last Updated: 2026-03-11
Scope: current and next-candidate non-HBAO AO families in the default render pipeline: `ScreenSpace`, `MultiViewAmbientOcclusion`, `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`, `SpatialHashRaytraced`, plus research-backed planning context for GTAO and VXAO.
Purpose: determine which current AO modes are canonical implementations, which are custom techniques, which are mislabeled, and which future non-HBAO families are worth supporting while HBAO/HBAO+ work proceeds.

## Executive Summary

The current non-HBAO AO modes do not all represent the named algorithms canonically.

- `ScreenSpace` is a straightforward legacy SSAO implementation. It is basic but correctly recognizable as SSAO.
- `MultiViewAmbientOcclusion` appears to be a repo-local custom AO variant, not a standard published algorithm with a stable canonical definition.
- `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance` currently route to one simplified pass that does not match canonical SAO as described by McGuire, Mara, and Luebke.
- `SpatialHashRaytraced` is an experimental compute AO path inspired by recent article/blog material and depth-buffer ray traversal ideas, not a mature canonical AO baseline.

For future non-HBAO work, the research now points to two credible additions:

- GTAO is the strongest candidate for a modern canonical screen-space AO path besides HBAO+.
- VXAO is a viable longer-range voxel AO family, but only if the engine is willing to pay for scene voxelization and the accompanying memory/performance budget.

The immediate code fixes completed alongside this audit are that the ImGui render-pipeline editor no longer exposes dead MSVO settings that the current runtime path does not consume, the misleading SAO selector entry is gone, and the live AO API now uses honest names with compatibility aliases for old enum values.

The next scaffolding steps are now in place: GTAO has a first real implementation on top of its dedicated enum/schema/pipeline slot, and VXAO now has an explicit enum/schema/pipeline scaffold so future voxel work no longer needs to borrow another AO family's slot.

## External Research Summary

### Scalable Ambient Obscurance (SAO)

Authoritative source material found:

- McGuire, Mara, Luebke, `Scalable Ambient Obscurance`, HPG 2012.
- NVIDIA research page for the paper.
- Eurographics Digital Library entry for the same paper.
- Casual Effects project page with paper, demo, and implementation context.

Canonical SAO characteristics from those sources:

- architecture-aware optimization of screen-space ambient obscurance
- explicit focus on fixed execution time and hard real-time predictability
- depth prefiltering / depth hierarchy to improve cache behavior
- high-precision reconstruction from depth rather than naïve repeated full reconstruction everywhere
- designed to generalize cleanly across forward and deferred renderers
- production-minded implementation details rather than just an academic integrand

What this means for XRENGINE:

- A canonical SAO-style implementation should not look like a single full-resolution fragment shader with four hardcoded radii and a legacy box blur.
- If the engine wants a true `ScalableAmbientObscurance` mode, it should be treated as a separate implementation task rather than assumed to already exist under the current MSVO pass.

### Multi-Scale / Volumetric Obscurance

Research outcome:

- I did not find strong evidence for a single standard algorithm family named exactly `Multi-Scale Volumetric Obscurance` that matches the current pass structure.
- The repo's current naming appears to blend concepts from ambient obscurance, multi-scale sampling, and volumetric-style interpretation, but the implementation is not clearly traceable to one canonical paper in the same way that SAO is.

Practical conclusion:

- In this repo, `MSVO` should currently be treated as an internal label for a simplified multi-radius obscurance shader, not as a verified canonical implementation of a published algorithm.

### Multi-View Ambient Occlusion (MVAO)

Research outcome:

- Web search did not reveal a clear, authoritative ambient occlusion paper or common production reference that establishes `Multi-View Ambient Occlusion` or `MVAO` as a standard AO algorithm family.
- The repo's current MVAO implementation therefore appears to be an internal/custom technique, not a canonical named algorithm comparable to SSAO, SAO, HBAO, or GTAO.

Practical conclusion:

- MVAO should be documented and judged as a custom engine AO mode.
- It should not be described externally as a standard industry AO algorithm unless a real source family is later identified.

### Screen-Space Ray Traversal / Spatial Hash AO Context

Relevant source material found:

- McGuire and Mara, `Efficient GPU Screen-Space Ray Tracing`, JCGT 2014.

Important takeaway:

- Robust depth-buffer ray traversal has known canonical techniques.
- The current `SpatialHashRaytraced` AO path is closer to an experimental hybrid that combines screen-space traversal with a temporal spatial hash reuse idea cited from a newer article/blog source inside the shader comments.

Practical conclusion:

- `SpatialHashRaytraced` is research/prototype terrain, not a conservative production default.

### Ground-Truth Ambient Occlusion (GTAO)

Authoritative source material found:

- Jimenez, Wu, Pesce, Jarabo, `Practical Real-Time Strategies for Accurate Indirect Occlusion`, Activision technical memo / presentation.

Canonical GTAO characteristics from those sources:

- horizon-based formulation designed to better match ray-traced ground truth than older SSAO/HBAO-era heuristics
- near-field indirect occlusion focus, with extensions toward specular occlusion in the broader work
- explicitly positioned as superior to HBAO while remaining real-time practical
- intended for high-quality screen-space deployment rather than voxel or hardware ray-tracing infrastructure
- depends on robust denoising / filtering and stable depth-normal reconstruction to realize its quality bar in practice

What this means for XRENGINE:

- if the engine wants one modern canonical non-voxel screen-space AO path beyond HBAO+, GTAO is the best candidate
- GTAO should be treated as its own implementation family, not folded into the current SSAO, MVAO, or MSVO paths
- the most natural product position is `high-quality canonical screen-space AO`, sitting above legacy SSAO and alongside HBAO+

### VXAO

Authoritative source material found:

- NVIDIA VXAO technical article / GameWorks integration guidance.

Canonical VXAO characteristics from those sources:

- uses a world-space voxel representation of the scene instead of purely screen-space inputs
- performs three major stages: voxelization, voxel post-processing, and screen-space cone tracing through voxel data
- avoids many common screen-space AO failure modes such as off-screen missing occluders, border instability, and classic haloing
- behaves more like a long-range ambient visibility solution than a short-range contact AO effect
- typically wants a short-range SSAO-style companion effect for fine detail that voxels cannot represent well at practical resolutions

What this means for XRENGINE:

- VXAO is not a small extension of the current AO stage; it is a separate voxel-data pipeline feature
- the engine should only pursue VXAO if it is willing to own voxelization, voxel memory management, and fallback blending when voxel coverage confidence drops
- VXAO should be planned as an advanced or experimental world-space AO family, not as the immediate successor to the current screen-space paths

## Repo Implementation Audit

### 1. ScreenSpace

Primary implementation:

- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_SSAOPass.cs`
- `Build/CommonAssets/Shaders/Scene3D/SSAOGen.fs`
- `Build/CommonAssets/Shaders/Scene3D/SSAOBlur.fs`

Assessment:

- recognizable as basic kernel-based SSAO
- uses noise texture + TBN hemisphere sampling
- uses simple blur
- no strong claim to modern quality, but the algorithm identity is clear

Verdict:

- canonical enough to call `legacy SSAO`
- low-quality but correctly named

### 2. MultiViewAmbientOcclusion

Primary implementation:

- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_MVAOPass.cs`
- `Build/CommonAssets/Shaders/Scene3D/MVAOGen.fs`
- `Build/CommonAssets/Shaders/Scene3D/MVAOBlur.fs`

Observed behavior:

- hemisphere kernel samples in view space
- primary radius sample
- secondary radius sample
- tangent-direction left/right samples
- blend controls for combining forward hemisphere and tangent-view samples
- bilateral blur using depth and normals

Assessment:

- coherent as a custom AO design
- better filtered than the legacy SSAO path
- not traceable to a standard named AO paper from the research pass

Verdict:

- custom engine AO mode
- moderately coherent implementation
- should remain explicitly documented as custom, not canonical

### 3. ScalableAmbientObscurance / MultiScaleVolumetricObscurance

Primary implementation:

- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_MSVO.cs`
- `Build/CommonAssets/Shaders/Scene3D/MSVOGen.fs`
- blur currently reuses `Build/CommonAssets/Shaders/Scene3D/SSAOBlur.fs`

Observed behavior:

- one fragment shader evaluates obscurance at four fixed radii via `ScaleFactors`
- each scale uses 8 angular taps
- no depth hierarchy or depth prefilter path
- no explicit fixed-time SAO pipeline behavior beyond fixed loop counts
- blur reuses the simple SSAO box blur
- runtime uniform binding only sends `Bias` and `Intensity`

Mismatch with canonical SAO:

- no depth prefilter hierarchy
- no architecture-aware SAO pipeline structure
- no evidence of the canonical SAO position/normal reconstruction and fixed-cost design details
- editor previously exposed dead settings (`ResolutionScale`, `SamplesPerPixel`) that the pass did not consume

Verdict:

- not a proper canonical SAO implementation
- not something I would present as production-grade MSVO
- currently best described as a simplified multi-radius obscurance prototype with misleading naming

### 4. SpatialHashRaytraced

Primary implementation:

- `XREngine/Rendering/Pipelines/Commands/Features/VPRC_SpatialHashAOPass.cs`
- `Build/CommonAssets/Shaders/Compute/AO/SpatialHashAO.comp`

Observed behavior:

- compute shader path
- spatial hashing over quantized world position and normal
- temporal-ish cached hit/sample counts per cell
- hemisphere direction sampling
- depth-buffer ray marching for hit/miss AO accumulation
- parameterized by cell size, samples per pixel, jitter scale, thickness, step count, radius

Assessment:

- interesting experimental direction
- more sophisticated than simple SSAO in some respects
- depends on screen-space ray marching and a reuse heuristic rather than a clearly established mainstream AO baseline
- shader comments cite a recent article/blog-style source rather than a peer-reviewed canonical AO reference

Verdict:

- experimental / research path
- not ready to be considered the reference AO implementation for the engine

## Production Readiness Ranking

Excluding HBAO/HBAO+, the current shipped AO modes rank as follows for production readiness:

1. `ScreenSpace`
   Reason: basic, recognizable, predictable, and correctly named even if visually outdated.

2. `MultiViewAmbientOcclusion`
   Reason: custom but internally coherent, and it at least has an edge-aware blur and matching exposed controls.

3. `SpatialHashRaytraced`
   Reason: technically interesting and parameterized, but clearly experimental and harder to trust as a conservative default.

4. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`
   Reason: the current implementation is mislabeled relative to canonical SAO expectations and previously exposed dead settings.

If planned-but-not-yet-implemented families are included in the strategic ranking, the intended target order should be:

1. GTAO
   Reason: strongest modern canonical screen-space candidate with good literature support and realistic integration cost.

2. `ScreenSpace`
   Reason: simple fallback that already exists and is honestly understood.

3. `MultiViewAmbientOcclusion`
   Reason: custom but internally coherent.

4. `SpatialHashRaytraced`
   Reason: interesting research path, but higher validation risk.

5. VXAO
   Reason: highest ceiling among non-ray-traced AO families here, but much larger infrastructure and performance cost.

6. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`
   Reason: current implementation remains the most misleading relative to canonical expectations.

## Correctness Ranking Relative To Claimed Names

Ranking by how honestly the current code matches the name shown in the editor:

1. `ScreenSpace`
2. `SpatialHashRaytraced`
3. `MultiViewAmbientOcclusion`
4. `ScalableAmbientObscurance` / `MultiScaleVolumetricObscurance`

The MSVO/SAO family ranks last because the implementation is the most misleading relative to what the name suggests.

## Immediate Fixes Completed

Completed in code as part of this audit:

- removed dead MSVO-only UI controls for `ResolutionScale` and `SamplesPerPixel` from the AO schema because the current `VPRC_MSVO` path does not consume them
- relabeled the AO method selector so legacy, custom, alias, prototype, and experimental non-HBAO modes are not presented as equally canonical
- replaced the live API names used by the editor/runtime/docs with honest names while keeping old enum values available as compatibility aliases

This keeps the ImGui render-pipeline editor honest until a real SAO/MSVO implementation exists.

## Recommended Naming Policy

Short term:

- keep `ScreenSpace` as the legacy fallback
- keep `MultiViewAmbientOcclusion` explicitly documented as a custom AO mode
- keep `SpatialHashRaytraced` explicitly documented as experimental
- do not market the current MSVO path as canonical SAO/MSVO

Preferred medium-term direction:

- either implement a real SAO path and map `ScalableAmbientObscurance` to it
- or rename the current MSVO pass to something less canonical-sounding if the implementation remains simplified

## Recommended Next Actions

1. Separate `ScalableAmbientObscurance` from `MultiScaleVolumetricObscurance` conceptually instead of treating both as one finished implementation.
2. Decide whether MVAO remains a first-class custom AO mode or is eventually replaced by HBAO+ plus legacy SSAO.
3. Keep the spatial-hash AO path behind an experimental framing unless it gets stronger validation and robustness work.
4. Treat GTAO as the preferred future canonical non-HBAO screen-space AO target if an additional modern AO family is added.
5. Treat VXAO as a separate advanced roadmap item that depends on voxel infrastructure, not as a quick post-process add-on.

## Research Sources

- McGuire, Mara, Luebke, `Scalable Ambient Obscurance`, HPG 2012
  - NVIDIA Research page: https://research.nvidia.com/publication/2012-06_scalable-ambient-obscurance
  - Eurographics Digital Library entry: https://diglib.eg.org/items/8c96d57d-3df3-43da-8663-07b3ecd60dde
  - Casual Effects project page: https://casual-effects.com/research/McGuire2012SAO/index.html
- McGuire and Mara, `Efficient GPU Screen-Space Ray Tracing`, JCGT 2014
  - JCGT page: https://jcgt.org/published/0003/04/04/
- Jimenez, Wu, Pesce, Jarabo, `Practical Real-Time Strategies for Accurate Indirect Occlusion`
   - Activision Research page: https://research.activision.com/publications/archives/practical-real-time-strategies-for-accurate-indirect-occlusion
   - Mirror / text-viewable PDF page: https://docslib.org/doc/8509379/practical-realtime-strategies-for-accurate-indirect-occlusion
- NVIDIA, `VXAO: Voxel Ambient Occlusion`
   - NVIDIA Developer article: https://developer.nvidia.com/vxao-voxel-ambient-occlusion

Research caveat:

- I did not find an authoritative canonical paper matching the repo's `MultiViewAmbientOcclusion` name.
- I did not find evidence that the current `MSVO` path corresponds cleanly to a standard algorithm family as implemented here; the strongest canonical external anchor is SAO, and the current pass does not match it closely enough to claim correctness.
- The VXAO source material available here is vendor documentation rather than an academic paper, but it is still authoritative enough to define the integration model and tradeoffs.