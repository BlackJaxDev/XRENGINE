# Contact Shadow Optimizations — TODO

> Status: **not started**
> Scope: per-light screen-space contact shadow ray march used by deferred and forward shading.

## Target Outcome

Reduce per-pixel cost of contact shadows so they can stay enabled by default on
multi-light scenes without dragging frame time.

Today, `XRENGINE_SampleContactShadowScreenSpace` in
[ShadowSampling.glsl](../../../../Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl)
runs a **world-space** ray march. Per sample it executes roughly four
`mat4 * vec4` reconstructions:

- `viewProjectionMatrix * worldPos` — project current sample to clip
- `inverseProjMatrix * clipPos` — reconstruct view-space from sampled depth
- `inverseViewMatrix * viewPos` — back to world to compare distances
- `viewMatrix * samplePosWS` — recompute the sample's view depth

Default sample counts:
[`DirectionalLightComponent.ContactShadowSamples = 16`](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs),
[`SpotLightComponent.ContactShadowSamples = 16`](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs),
`LightComponent` base (point inherits) `= 4`.

So worst-case per-pixel-per-light is ~16 samples × ~4 mat4×vec4 + a depth fetch
+ a few dot products, which compounds quickly across multiple shadow-casting
lights.

## Source Context

- [ShadowSampling.glsl](../../../../Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl)
  — `XRENGINE_SampleContactShadowScreenSpace`,
  `XRENGINE_EvaluateContactShadowScreenSpaceHit`,
  `XRENGINE_TryProjectContactShadowWorldPos`,
  `XRENGINE_ContactShadowViewPosFromDepth`. Four overloads exist (deferred Dir,
  Spot, Point and forward path) and all share the same per-sample cost
  pattern.
- [DirectionalLightComponent.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs)
  / [SpotLightComponent.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs)
  / [PointLightComponent.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs)
  — `ContactShadowSamples`, `ContactShadowDistance`, `ContactShadowEnabled`
  defaults.
- [LightComponent.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs)
  — base `_contactShadowSamples = 4`.
- `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
  — currently asserts `ContactShadowSamples = 16` for directional and spot;
  must update if defaults change.

## Tasks

### 0. Branch setup

- [ ] Create branch `rendering/contact-shadow-optimizations` and move all
  subsequent work onto it.

### 1. Refactor to pure screen-space marching (highest gain)

- [ ] Replace per-sample world-space reprojection with a constant per-step UV
  + clip-depth delta:
  - Compute start clip position (`viewProjectionMatrix * worldPos`) **once**.
  - Compute end clip position (`viewProjectionMatrix * (worldPos + rayDir * maxDist)`) **once**.
  - Convert to screen UV + clip-depth, derive `vec2 duv` and `float dz` per
    step at function entry.
  - Loop body: `uv += duv; rayClipDepth += dz; sceneDepth = textureLod(SceneDepth, uv, 0); compare`.
- [ ] Drop `inverseViewMatrix` and the redundant `viewMatrix * samplePosWS`
  per-sample multiplications; keep `inverseProjMatrix` only if still needed
  for thickness compare, and prefer linear depth comparison instead.
- [ ] Move all four overloads to a shared internal helper to keep the four
  entry points thin.

### 2. Compare in view space, not world space

- [ ] Build the ray once in view space (`viewMatrix * worldPos`,
  `viewMatrix * lightDir`) at function entry, then compare against linearized
  scene depth without ever reconstructing a sample world position.
- [ ] Keep the existing thickness / fade parameters; only the coordinate
  system changes.

### 3. Lower default sample counts

- [ ] `DirectionalLightComponent.ContactShadowSamples` 16 → 8.
- [ ] `SpotLightComponent.ContactShadowSamples` 16 → 8.
- [ ] `LightComponent` base 4 → 6 (point lights gain a bit; cost still capped
  by short `ContactShadowDistance` defaults).
- [ ] Update
  `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
  expectations (lines around 1075 / 1216 at time of writing).
- [ ] Add a short note in
  [docs/architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md)
  documenting the new defaults and rationale.

### 4. Tighter early-outs

- [ ] Skip the sample loop when `dot(N, L) <= 0` (already partially done in
  some paths — audit all four overloads).
- [ ] Skip the loop when `viewDepth > contactFadeEnd` **before** computing the
  ray start/delta.
- [ ] Add a per-light tile-size early-out: if the light's clip-space bound
  doesn't overlap the fragment's screen tile, skip contact shadow entirely.
  (Optional; revisit after items 1–3.)

### 5. (Optional follow-up) Per-light screen-space contact-shadow pass

- [ ] Evaluate moving contact shadows to a half-res compute pass that writes a
  per-light occlusion texture, sampled as a single texture fetch in lighting.
  Useful when many shadow-casters overlap on screen.
- [ ] Out of scope until items 1–4 land and are profiled.

### 6. Final merge

- [ ] After validation, merge `rendering/contact-shadow-optimizations` back
  into `main`.

## Validation Plan

1. Build editor: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
2. Boot `Start-Editor-NoDebug` with the unit testing world; confirm contact
   shadows still appear under directional + at least one spot and one point
   light.
3. Run `Test-SurfelGi` and any forward-lighting / deferred shading smoke
   tests.
4. Capture before/after profiler GPU traces in a multi-light scene
   (`profiler-render-stalls.log`, `profiler-fps-drops.log`).
5. Update or add a test in
   `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
   (or a new contact-shadow-specific file) that asserts the new sample-count
   defaults.

## Risks / Notes

- The four `XRENGINE_SampleContactShadowScreenSpace` overloads must stay in
  sync — refactor one, port the same pattern to the others in the same
  commit.
- View-space vs world-space switch can subtly change thickness behavior for
  long rays (`ContactShadowDistance > ~5m`); keep the fade parameters in the
  same units and visually compare at distance.
- Sample-count changes are user-visible defaults; surface in patch notes.
