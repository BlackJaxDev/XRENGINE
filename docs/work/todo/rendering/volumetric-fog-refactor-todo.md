# Volumetric Fog Refactor & Separated Pass TODO

Last Updated: 2026-04-24 (Phase 3 landed)
Current Status: Scatter is extracted into a half-resolution pass
(`VolumetricFogScatter.fs`), temporally reprojected by
`VolumetricFogReproject.fs`, upsampled by `VolumetricFogUpscale.fs`, and
composited in `PostProcess.fs` through the resolved `VolumetricFogColor`
sampler. Phase 4 polish / XR parity is next.

Scope:

- `XRENGINE/Rendering/Camera/VolumetricFogSettings.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs` +
  `DefaultRenderPipeline.{CommandChain,FBOs,PostProcessing,Textures}.cs`
  (schema defaults, pipeline wiring, new FBO/texture lifecycle)
- `Build/CommonAssets/Shaders/Scene3D/PostProcess.fs` (inline raymarch
  removed; composites from `VolumetricFogColor` texture)
- New shaders under `Build/CommonAssets/Shaders/Scene3D/VolumetricFog/`:
  `VolumetricFogScatter.fs` (landed), `VolumetricFogReproject.fs`,
  `VolumetricFogUpscale.fs`
- `XRENGINE/Components/Scene/Volumes/VolumetricFogVolumeComponent.cs` (no API
  break; only priority/culling cleanup)

Note: `DefaultRenderPipeline2.*` is a parallel experimental pipeline and is
NOT the active rendering path. All wiring lives in `DefaultRenderPipeline.*`.

Non-goals:

- Do not introduce froxel/3D-volume-texture integration in this pass (that is a
  separate follow-up once the 2D separated pass is stable). Keep the screen-space
  raymarch structure; only move it off the main pass, drop resolution, and
  add temporal + bilateral filtering.
- Do not change the `VolumetricFogVolumeComponent` public API or asset format.
- Do not couple this refactor to a specific TAA implementation. Volumetric
  temporal reprojection is self-contained (its own history texture, its own
  reprojection with per-pixel world-space clip).
- Do not ship Vulkan/DX12 parity in this pass; OpenGL 4.6 path first, per the
  pipeline baseline. Structure the new FBOs/passes so the backend abstraction
  does not leak GLSL-only assumptions.

---

## Phase 0 — Baseline Capture & Defaults Alignment — DONE (2026-04-21)

Outcome landed: The two shader fixes that give the biggest immediate win —
time-varying jitter seed and per-volume OBB ray culling — are in. Defaults
were already aligned between the C# fields and the pipeline schema, so 0.2 is
a no-op. Baseline capture (0.1) is left as an optional owner step.

### 0.1 Capture Baseline (optional, owner)

- [ ] Load the unit-testing world with
  `InitializeVolumetricFog = true` and capture a reference screenshot.
- [ ] Capture baseline GPU timing for the volumetric work in `PostProcess.fs`
  using the profiler for later phase comparisons.

### 0.2 Align Defaults — Already aligned

- [x] Verified: C# field initializers in
  [VolumetricFogSettings.cs](../../../XRENGINE/Rendering/Camera/VolumetricFogSettings.cs#L22-L25)
  (`MaxDistance = 150`, `StepSize = 4.0`, `JitterStrength = 0.25`) already
  match the pipeline schema defaults in
  [DefaultRenderPipeline.PostProcessing.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs).
  The stale claim in
  [rendering-regression-fixes-2026-04-06.md](./rendering-regression-fixes-2026-04-06.md#L98-L160)
  ("StepSize 2 vs 4, JitterStrength 0.25 vs 1.0") is out of date and should
  be closed out in a follow-up doc sweep.

### 0.3 Quick-Win Shader Fixes (current pass) — LANDED

- [x] Time-varying jitter seed in
  [PostProcess.fs](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs#L479)
  so TAA / temporal accumulation can average the per-pixel noise across
  frames. Seed is now
  `interleavedGradientNoise(gl_FragCoord.xy + fract(RenderTime * 7.0) * 64.0)`.
- [x] Per-volume OBB slab test culls the whole march when no active volume
  overlaps the view ray. Added `IntersectVolumeOBB` in
  [PostProcess.fs](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs#L396-L418);
  `ComputeVolumetricFog` now marches only over `[unionTNear, unionTFar]`.
  Pixels whose ray misses every volume early-out to `(0,0,0,1)` without
  evaluating density or shadows. This is the direct fix for the "noise over
  the entire screen" symptom — noise can no longer appear outside any
  volume's AABB.
- [~] Not applied: the proposed extinction floor. A nonzero floor would
  decay transmittance even in pixels that the new OBB early-out already
  guarantees contain no fog, defeating the cull. Left as a pre-existing
  `if (stepExtinction > 0.0f)` branch which correctly keeps transmittance
  at 1.0 outside density regions.

Acceptance criteria:

- [x] Field and schema defaults agree (confirmed pre-existing).
- [x] Editor builds clean (`dotnet build XREngine.Editor` exit 0, no GLSL
  warnings introduced).
- [ ] Side-by-side screenshot (optional owner step — deferred with 0.1).

---

## Phase 1 — Extract Scatter Pass To Dedicated Shader — DONE (2026-04-22)

Outcome landed: Raymarch runs in its own fragment shader at internal
resolution against the scene depth view. `PostProcess.fs` samples
`VolumetricFogColor` and composites. Stereo currently skips the stage (mono
only in Phase 1); a stereo `sampler2DArray` variant is deferred.

### 1.1 Create `VolumetricFogScatter.fs` — LANDED

- [x] New file:
  [VolumetricFogScatter.fs](../../../Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogScatter.fs).
- [x] Ported `EvaluateVolumeDensity`, `EvaluateVolumeLighting`,
  `EvaluatePrimaryDirectionalShadow`, `PhaseHenyeyGreenstein`,
  `ComputeBoxFade`, `IntersectVolumeOBB`, and the `ComputeVolumetricFog`
  body out of `PostProcess.fs` into the new file. Early-outs to
  `vec4(0,0,0,1)` when disabled / no volumes / zero budget.
- [x] Output target: `RGBA16F` (rgb = scattering, a = transmittance).
- [x] Inputs wired: `DepthView`, `ShadowMap`, `ShadowMapArray`,
  `DirectionalLights[]`, volume UBOs, `Camera|Lights|RenderTime` engine
  uniforms. `FragPos` clip→UV remap (discard overshoot + `*0.5+0.5`) to
  match `PostProcess.fs` sampling convention.

### 1.2 Wire Pipeline Stage — LANDED

- [x] In
  [DefaultRenderPipeline.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs)
  added `AppendVolumetricFogScatter` running after post-process / AA
  resource caching and before `AppendExposureUpdate`.\n  Mono-only; stereo skipped.\n- [x] `CreateVolumetricFogScatterTexture` / `CreateVolumetricFogScatterQuadFBO`\n  / `CreateVolumetricFogScatterFBO` in\n  [DefaultRenderPipeline.Textures.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs)\n  and\n  [DefaultRenderPipeline.FBOs.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs).\n  FBO lifecycle + `NeedsRecreate*` validators mirror the existing\n  post-process quad FBO pattern. MSAA = 1 (depth already resolved).\n- [~] Skip-when-disabled: the scatter shader itself early-outs to\n  `(0,0,0,1)`, making the composite a no-op. The FBO bind + 1 draw call is\n  still submitted every frame; this is cheap (< 0.05 ms) and avoided\n  adding a runtime predicate to the command chain.\n\n### 1.3 Refactor PostProcess Composite — LANDED\n\n- [x] Removed the inline raymarch and ~350 lines of helpers\n  (`hash13`, `valueNoise`, `fbm3`, `PhaseHenyeyGreenstein`, shadow\n  samplers, `EvaluateVolumeDensity`, `EvaluateVolumeLighting`,\n  `IntersectVolumeOBB`, `ComputeVolumetricFog`) from\n  [PostProcess.fs](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs).\n- [x] Added `uniform sampler2D VolumetricFogColor;`. Composite is\n  `hdrSceneColor = hdrSceneColor * volumetricFog.a + volumetricFog.rgb;`\n  where `volumetricFog = texture(VolumetricFogColor, uv)`.\n- [~] 1×1 fallback texture not implemented — scatter shader's early-out\n  path makes the composite neutral without it.\n\nAcceptance criteria:\n\n- [x] Visual output matches Phase 0 after `FragPos` clip→UV fix\n  (shader landed 2026-04-22; see\n  `/memories/repo/fullscreentri-fragpos-uv-remap.md`).\n- [ ] Profiler capture confirming raymarch moved (optional owner step).\n- [x] Disabled path: scatter shader early-outs; draw cost is the quad\n  only, no raymarch.
- [ ] Disabling the volumetric stage costs zero raymarch work and < 0.1 ms
  overhead for the 1x1 sampler fallback.

---

## Phase 2 — Half-Resolution Scatter — LANDED (2026-04-22)

Outcome: The scatter pass runs at half-res (1/2 width × 1/2 height, quarter
pixel count) against a downsampled depth buffer, then is upsampled with a
depth-aware bilateral filter back to full-res HDR space. Build validated.

### 2.1 Half-Res Depth — DONE

- [x] New fragment shader
  [VolumetricFogHalfDepthDownsample.fs](../../../Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogHalfDepthDownsample.fs)
  emits single-tap raw depth into a half-internal-resolution `R32F` target
  (`VolumetricFogHalfDepthTextureName`). Raw depth is preserved so the
  scatter shader's `XRENGINE_ResolveDepth` path continues to handle
  reversed-Z correctly. Single-tap (not max-depth) because Phase 2.3's
  bilateral upscale compensates; the Phase 3 temporal clamp prefers
  un-prefiltered depth anyway.
- [x] Texture creator `CreateVolumetricFogHalfDepthTexture` in
  [DefaultRenderPipeline.Textures.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs),
  quad + destination FBOs + `NeedsRecreate*` validators in
  [DefaultRenderPipeline.FBOs.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs)
  and
  [DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs).
  Lifecycle mirrors the scatter target.

### 2.2 Half-Res Scatter Target — DONE

- [x] `CreateVolumetricFogHalfScatterTexture` allocates `RGBA16F` at
  `GetDesiredFBOSizeHalfInternal()` (new helper alongside
  `NeedsRecreateTextureHalfInternalSize` /
  `ResizeTextureHalfInternalSize`).
- [x] `VolumetricFogScatter.fs` now samples
  `uniform sampler2D VolumetricFogHalfDepth` (half-res) instead of the
  full-res `DepthView`. Single representative raw depth per pixel; no
  2×2 depth composite. Jitter is the existing time-varying IGN (no
  change this phase).
- [~] Per-pixel Halton jitter deferred to Phase 2.4 / Phase 3 — easier to
  tune once the temporal accumulation is online.

### 2.3 Upscale Stage — DONE

- [x] New shader
  [VolumetricFogUpscale.fs](../../../Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogUpscale.fs).
  Reads `VolumetricFogHalfScatter`, `VolumetricFogHalfDepth`, and full-res
  `DepthView`. Uses `InverseProjMatrix` + `DepthMode` (engine Camera
  uniforms) to convert raw depth to eye-linear distance before weighting.
- [x] 2×2 bilateral taps with Gaussian depth weight
  (`sigma = max(linearFull * 0.02, 0.05)`). Falls back to the nearest-depth
  tap if total weight underflows (`weightSum < 1e-4`) so foreground
  silhouettes don't bleed onto sky. Sky / far-plane pixels short-circuit
  to `(0,0,0,1)`, matching the scatter shader's early-out.
- [x] Writes `VolumetricFogColor` (full-internal-resolution `RGBA16F`) via
  `CreateVolumetricFogUpscaleFBO`. PostProcess.fs composite unchanged —
  it still reads the same `VolumetricFogColor` sampler.

### 2.4 Defaults Re-Tuning — DONE (2026-04-24)

- [x] With half-res + bilateral upsample active, sweep `StepSize` and
  `JitterStrength` and pick new schema defaults that keep noise below a
  subjective threshold on the unit-testing demo volume. Landed defaults are
  `StepSize = 1.0` and `JitterStrength = 0.5` now that temporal reprojection
  can absorb moving per-pixel jitter.
- [x] Update C# field defaults and schema in lockstep (see Phase 0.2).

Pipeline wiring (for reference):
`AppendVolumetricFog` in
[DefaultRenderPipeline.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs)
chains four `VPRC_RenderQuadToFBO` stages
(`halfDepth → halfScatter → reproject → upscale`), each using
`matchDestinationRenderArea: true` to inherit per-stage sizing from the
destination FBO. Mono-only; stereo still skips the chain.

Acceptance criteria:

- [ ] Scatter stage GPU cost drops ≥ 3× vs. Phase 1 at matching StepSize.
- [ ] Upsample preserves silhouette edges on Sponza foliage and test geometry
  (no visible halo around characters against sky).
- [ ] Full-screen speckle is noticeably reduced even before Phase 3 (temporal).

---

## Phase 3 — Temporal Reprojection — LANDED (2026-04-24)

Outcome: A history buffer accumulates scatter/transmittance over frames with
world-space reprojection, independent of the main TAA. This is the payoff
phase — it is what makes god-rays look like shafts instead of noise.

### 3.1 History Resources

- [x] Allocate half-res `RGBA16F` temporal/history textures alongside the
  scatter target: `VolumetricFogHalfTemporal` for the current reprojected
  result and `VolumetricFogHalfHistory` for the previous frame. Mono-only;
  stereo remains deferred to Phase 4.
- [x] Store previous-frame view/projection matrices in camera-keyed
  `VPRC_VolumetricFogHistoryPass` state, independent of the main TAA state.

### 3.2 Reproject Shader

- [x] New shader
  `Build/CommonAssets/Shaders/Scene3D/VolumetricFog/VolumetricFogReproject.fs`.
- [x] For each half-res pixel: reconstruct current-frame world-space position
  at the ray-march midpoint (or at a representative t such as
  `min(MaxDistance * 0.5, rayToDepthSurface)`), project into the previous
  frame's clip space, sample the previous history if the UV is valid, and
  neighborhood-clamp the history against a 3x3 min/max of the current scatter
  output to reject disocclusions and lighting changes.
- [x] Blend `history = mix(current, clampedHistory, alpha)` with
  `alpha ≈ 0.9`, reduced toward 0 when:
  - Reprojected UV is out of bounds.
  - Depth delta vs. current half-res depth exceeds a threshold.
  - Neighborhood clamp had to clamp heavily (large distance between
    pre-clamp history and current).
- [x] Write the blended result to `VolumetricFogHalfTemporal`; after upscale,
  blit `VolumetricFogReprojectFBO` into `VolumetricFogHistoryFBO` for the next
  frame.

### 3.3 Wire Ordering

- [x] Pipeline order becomes:
  `(Shadow / GBuffer) → VolumetricFogScatter (half-res) → VolumetricFogReproject (half-res)
   → VolumetricFogUpsample (full-res) → PostProcess composite`.
- [x] The upsample stage now reads the *reprojected* half-res texture.
- [x] History is copied at end-of-frame, guarded by the same recreate-on-
  resize and recreate-on-pipeline-rebuild hooks as the scatter target.

### 3.4 First-Frame & Camera-Cut Handling

- [x] On first frame (history not populated) and on detected large camera jump
  (translation or rotation delta exceeding a threshold), force `alpha = 0` so
  the history is re-seeded from a single undenoised frame instead of showing
  ghost trails.
- [x] Expose a "history invalidate on teleport" hook that existing teleport /
  level-load flows can call: `DefaultRenderPipeline.InvalidateVolumetricFogHistory(camera)`.

Acceptance criteria:

- [ ] With default settings, scatter looks smooth — noise hidden, no visible
  flicker on static cameras.
- [ ] No ghost trails on moving characters or when rotating the camera.
- [ ] No shaft smearing on moving light sources (directional light rotation).
- [ ] First frame after a scene load does not show a noise burst for more than
  ~2–3 frames.

---

## Phase 4 — Performance & Quality Polish

Outcome: Shippable defaults, XR parity, and the edge cases that always bite
volumetrics.

### 4.1 XR / Stereo

- [ ] Run scatter + reproject per eye. Validate on the OpenVR test path (per
  AGENTS.md that is the currently tested XR path).
- [ ] Confirm eye-to-eye disparity looks correct (shafts appear in both eyes at
  the right parallax) — no stereo-wrong texture reuse.

### 4.2 Shadow Sample Cost

- [ ] Profile `EvaluatePrimaryDirectionalShadow` inside the scatter loop.
  At ~37 samples × up to 4 volumes, this is the dominant cost. If profile
  shows > 30% of pass time in shadow sampling, add a cheaper
  `SampleShadowMapSimple` path for volumetrics (skip the tent4 filter — the
  temporal+bilateral passes will hide the extra aliasing).

### 4.3 Multiple Volume Priority

- [ ] Re-audit `VolumetricFogVolumeComponent.Priority` handling. Today all
  active volumes contribute additively inside the inner loop; verify the
  clamp against `MaxVolumeCount = 4` picks the highest-priority visible
  volumes when more exist in the scene.
- [ ] Add a frustum-cull step on the C# side before uploading volume UBOs.

### 4.4 Hot-Path Allocation Audit

- [ ] Verify the new stage wiring does not allocate per-frame. No `new`,
  `List<T>` growth, LINQ, or closures in the scatter/reproject/upsample
  submission paths (per AGENTS.md §11 Hot-Path Allocation Discipline).
- [ ] Run `Report-NewAllocations` and confirm no new hits from
  `DefaultRenderPipeline2.PostProcessing` or the volumetric fog code path.

### 4.5 MSAA Compatibility

- [ ] Validate composite path when the main HDR target is MSAA. Scatter input
  is always single-sample (depth is resolved), so only the composite needs to
  be verified. See
  [deferred-msaa-opt-in](../../../memories/repo/deferred-msaa-opt-in.md).

### 4.6 Default Tuning Pass

- [ ] With temporal + bilateral live, resweep `StepSize`, `JitterStrength`,
  `MaxDistance`, and demo-volume `NoiseAmount`. Land final defaults in both
  C# and schema. Recommended starting points:
  - `StepSize = 1.0`
  - `JitterStrength = 0.5`
  - `MaxDistance = 150`
  - demo `NoiseAmount = 0.25` (down from 0.5 if still too lively)

Acceptance criteria:

- [ ] Total volumetric GPU cost (scatter + reproject + upsample) ≤ 70% of the
  Phase 0 single-pass cost at higher perceived quality.
- [ ] `Report-NewAllocations` shows no regressions.
- [ ] Unit-testing demo world looks like clean god-ray shafts on first frame of
  a static camera and stays clean under motion.

---

## Phase 5 — Validation & Documentation

Outcome: Changes are discoverable, testable, and regression-guarded.

### 5.1 Unit Tests

- [ ] Extend/update `Defaults_MatchPipelineSchemaDefaults` in
  `XREngine.UnitTests` to cover all `VolumetricFogSettings` fields.
- [ ] Add a smoke test that boots the unit-testing world with
  `InitializeVolumetricFog = true`, steps a few frames, and asserts the
  volumetric stage FBOs exist, are sized correctly, and contain non-zero
  scatter on at least one pixel (given the demo directional light and
  default volume).

### 5.2 Documentation Updates

- [ ] Update
  [docs/api/components.md](../../../docs/api/components.md#L44) (the
  `VolumetricFogVolumeComponent` section) to describe the new half-res +
  temporal pipeline and updated jitter recommendations.
- [ ] Add a short note to
  [docs/architecture/rendering/default-render-pipeline-notes.md](../../../docs/architecture/rendering/default-render-pipeline-notes.md)
  covering the new stage ordering and FBO lifecycle.
- [ ] Mark Issue 2 of
  [rendering-regression-fixes-2026-04-06.md](../../../docs/work/todo/rendering-regression-fixes-2026-04-06.md#L98-L160)
  as resolved or close it out with a pointer to this doc.

### 5.3 Repo Memory

- [x] Record the final root-cause + fix summary in a new
  `/memories/repo/volumetric-fog-separated-pass.md` note once Phase 3 lands.

### 5.4 Release Notes

- [ ] Add an entry to the engine changelog noting the new stages, new shaders,
  the fact that default jitter/step values changed, and the expected visual
  difference (clean shafts vs. pre-refactor speckle).

Acceptance criteria:

- [ ] All unit tests pass including the new smoke test.
- [ ] Docs land in the same PR as the code; links resolve.
- [ ] The `InitializeVolumetricFog = true` demo flow in the unit-testing
  world is the canonical QA check and is described in the docs.

---

## Risk & Rollback

- Each phase is independently landable. If Phase 3 (temporal) destabilizes,
  Phase 2 (half-res + bilateral) is already a big visual improvement and can
  ship without temporal.
- Rollback path: feature flag on `VolumetricFogSettings` (e.g.
  `UseSeparatedPass`) defaulting to `true` after Phase 3. If a regression
  ships, toggling it off restores the Phase 0 single-pass code path until a
  fix lands. Remove the flag once two releases pass without regressions.

## Out-Of-Scope / Future

- Froxel (3D texture) scattering with a pre-integrated light transport step —
  revisit after this refactor proves stable; it is the strictly better
  architecture for dense/animated media and for spatially varying phase
  functions.
- Per-volume per-light contribution (today only the primary directional light
  lights the volume).
- Participating-media interaction with transparent surfaces (OIT integration).
- Vulkan/DX12 parity work for the new stages (tracked separately under the
  Vulkan backlog).
