# Forward Lighting Shader Optimizations — TODO

Tracks remaining optimization opportunities in
[Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl](../../../Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl).

The "high-quality" localized passes were already applied (probe-loop invariant
hoisting, Cramer's-rule barycentric, branchless `safeDir` in `ApplyParallax`,
reused normalized light vector in point-shadow paths). Items below require
broader refactors and should land on a dedicated branch with editor runtime
validation, since GLSL is not compiled at C# build time.

## Cross-cutting refactors (highest expected gain)

- [x] **Cache `XRENGINE_GetForwardViewIndex()` once per fragment.**
  Currently invoked from `GetForwardViewMatrix`, `GetForwardInverseViewMatrix`,
  `GetForwardInverseProjMatrix`, `GetForwardProjMatrix`,
  `GetForwardViewProjectionMatrix`, and `XRENGINE_SampleForwardContactShadowScreenSpace`.
  Each call repeats `clamp(round(FragViewIndex), …)`. Compute once at the top
  of the shading entry point and pass the resolved matrices (or store the index
  in a `flat` varying / shared local).

- [x] **Cache `XRENGINE_ViewDepthFromWorldPos(fragPos)` once per fragment.**
  Currently recomputed inside every `XRENGINE_ReadShadowMap*`, every cascade
  iteration, and every contact-shadow path. Each call performs `mat4 * vec4`.
  Pass it down as a parameter from the top-level light loop.

- [x] **Cache `XRENGINE_GetForwardCameraPosition()` once per fragment.**
  `XRENGINE_CalculateDirectPbrLight` calls it for every directional / point /
  spot light. Each call rebuilds an `inverseView`. Compute once at the top of
  forward shading and thread through.

- [x] **Add a direct camera-position accessor.**
  `XRENGINE_GetForwardCameraPosition` returns a full `mat4` and indexes column 3.
  Add an accessor that returns just the `vec3` translation column to avoid the
  matrix copy.

## Shadow sampler-array dispatch consolidation

- [x] **Consolidate `switch(shadowSlot)` helpers** so a single switch captures
  all needed scalars (face size, texel size, sample radius, etc.) into locals,
  then passes them to subsequent calls. Today, 3–5 of these helpers are hit per
  fragment per local light, each redoing the same dynamic branch:
  - `XRENGINE_GetPointShadowSampleRadiusForSlot`
  - `XRENGINE_GetPointShadowFaceSizeForSlot`
  - `XRENGINE_GetPointShadowTexelRelativeBiasForSlot`
  - `XRENGINE_ReadPointShadowCenterDepthForSlot`
  - `XRENGINE_SamplePointShadowCubeSlot`
  - `XRENGINE_SamplePointContactShadowCubeSlot`
  - `XRENGINE_SampleSpotShadow*ForSlot` (multiple variants)
  - `XRENGINE_GetSpotShadowTexelSizeForSlot`
  - `XRENGINE_ReadSpotShadowCenterDepthForSlot`

- [x] **Cache `textureSize(...)` results** on shadow atlases. Each fragment hits
  `textureSize(DirectionalShadowAtlas, 0).z` multiple times (per cascade and
  per fallback path); same for `PointLightShadowAtlas` and `SpotLightShadowAtlas`.
  Cache the `.z` (layer count) once at function entry.

## Local CSE / micro-optimizations

- [x] **Macro-expansion CSE.** `#define ShadowBlockerSamples ShadowPackedI0.x`
  etc. expand at every use site. Cascade calls repeat
  `ShadowBlockerSearchRadius * cascadeScale`, `ShadowMaxPenumbra * cascadeScale`,
  etc. Capture into local `ivec4 _sI0 = ShadowPackedI0; vec4 _sP0 = ShadowParams0;`
  copies at function entry to make CSE explicit and avoid macro re-evaluation.

- [x] **Fast path for `XRENGINE_GetShadowBiasRange` / `XRENGINE_GetLocalShadowBias`.**
  Both call `pow(...)` with a uniform exponent. Add a branch to skip `pow` when
  `abs(ShadowMult - 1.0) < 1e-3` (the common case). Saves a `pow` per cascade
  per light.

- [x] **Drop unused locals in shadow path entry blocks.**
  `XRENGINE_ReadPointAtlasShadowMap`, `XRENGINE_ReadShadowMapPoint`, and
  `XRENGINE_ReadShadowMapSpot` all read every `Params*` field into locals; some
  (e.g., `shadowF0` in the atlas path) are unused. Drop them so the compiler
  can skip the SSBO loads.

- [x] **Earlier `farPlane` early-out in spot/point shadow paths.**
  Move the `if (lightDist >= farPlaneDist) return 1.0;` check before the
  `normalOffset` / `offsetPosWS` computation rather than after.

- [x] **Audit unused uniforms.**
  `PrimaryDirLightWorldToLightInvViewMatrix` and
  `PrimaryDirLightWorldToLightProjMatrix` are declared (line 109–110) but not
  referenced inside the snippet. Verify usage across other snippets that include
  this file; if unused, drop both uniforms (saves 32 floats of uniform space)
  and remove the matching CPU upload in
  [Lights3DCollection.ForwardLighting.cs](../../../XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs)
  and the constants in
  [EngineShaderBindingNames.cs](../../../XREngine.Runtime.Rendering/Resources/Shaders/EngineShaderBindingNames.cs)
  / [EUniformRequirements.cs](../../../XREngine.Runtime.Rendering/Materials/Options/EUniformRequirements.cs).

## Documentation / safety

- [x] **Pin `XRENGINE_ResolveProbeWeights` (debug fallback) as debug-only.**
  Already gated by `XRENGINE_PROBE_DEBUG_FALLBACK`, but the O(ProbeCount²)
  insertion-sort loop should carry an inline comment noting it must never be
  enabled in shipping builds.

## Validation plan

1. Land each cross-cutting refactor on a dedicated branch.
2. Boot the editor (`Start-Editor-NoDebug`) and confirm shader compilation
   succeeds across all forward materials (PBR, transparent, hair, skin, etc.).
3. Run `Test-SurfelGi` and any forward-lighting visual-regression scenes.
4. Capture before/after profiler GPU traces for a scene with multiple
   shadow-casting local lights and active reflection probes (target:
   `profiler-render-stalls.log`).

Implementation note: the shader/runtime cleanup is complete in-source. Targeted
runtime-rendering and editor builds pass. Focused shader contract tests are
currently blocked before execution by the existing `Engine` type ambiguity
between `XREngine.Runtime.Rendering` and `XREngine`; editor boot and GPU profiler
trace capture still require local graphics runtime validation.
