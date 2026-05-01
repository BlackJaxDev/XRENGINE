# Default Render Pipeline — Known Issues and Lessons Learned

This document records bugs that were found and fixed in `DefaultRenderPipeline` and `DefaultRenderPipeline2`. Its purpose is to prevent the same classes of mistake from being reintroduced. Read this before making non-trivial changes to the pipeline texture setup, FBO management, post-processing, or render pass ordering.

---

## 1. Texture Storage: `Resizable` and Mip Chains

**Rule:** Any framebuffer texture that needs mip levels (auto-exposure source, bloom chain, etc.) **must** set `Resizable = false` so that `glTextureStorage2D` (immutable storage) is used and all mip levels are physically allocated.

### What went wrong

The non-stereo `HDRSceneTex` was created via `CreateFrameBufferTexture` which defaults `Resizable = true`. This prevented `GLTexture2D` from using immutable storage, so `_allocatedLevels` stayed at 0. `ResolveMaxMipLevel` then set `GL_TEXTURE_MAX_LEVEL` to 0. Consequences:

- `glGenerateTextureMipmap` became a no-op (max level = base level).
- `texelFetch(SourceTex, ivec2(0,0), 10)` in the auto-exposure compute shader read undefined/near-zero data.
- Luminance ≈ 0 → target exposure = `ExposureDividend / ~0` → clamped to `MaxExposure` every frame.
- The stereo path already had `Resizable = false` and worked correctly.

### Defensive fix applied

- Both `DefaultRenderPipeline.Textures.cs` and `DefaultRenderPipeline2.Textures.cs`: added `t.Resizable = false` to the non-stereo HDR texture.
- `GLTexture2D.ResolveMaxMipLevel` and `GLTexture2DArray.ResolveMaxMipLevel`: changed the mutable-texture fallback from `baseLevel` (0) to `naturalMaxLevel` (`SmallestMipmapLevel`) so `GL_TEXTURE_MAX_LEVEL` is correct even when immutable storage is not used.

### Checklist for new textures

- [ ] Does this texture need mipmaps? → Set `Resizable = false`, `AutoGenerateMipmaps = true`.
- [ ] If `UseDetailPreservingComputeMipmaps` is enabled, confirm the texture path is an eligible OpenGL 2D format; unsupported formats still fall back to `glGenerateTextureMipmap`.
- [ ] Is there parity between the stereo and non-stereo creation paths?
- [ ] After creation, does `SmallestMipmapLevel` return the expected value?

---

## 2. Bloom: Threshold Must Be Applied

**Rule:** The first bloom downsample level **must** apply a bright-pass threshold to extract only energy above the threshold. Without it, bloom = blurred full scene = uniform brightness increase.

### What went wrong

`BloomSettings.SetDownsampleUniforms` set `UseThreshold = false` for ALL downsample levels. `BloomDownsample.fs` had a built-in `BrightPass()` function gated by `UseThreshold`, but it was never activated. The bloom texture ended up being a blurred copy of the entire scene, adding brightness instead of producing artistic halos.

### Fix

- Set `UseThreshold = true` for the first downsample only (mip 0 → 1).
- Fixed `BrightPass()` to extract only excess energy above threshold (`contribution / brightness`) instead of passing almost the full highlight.
- Default combine weights now bias toward blurrier mips (`StartMip=2`, `EndMip=4`, weights 0.65 / 0.25 / 0.10 on LOD2/3/4).

---

## 3. Bloom: Mip FBO Creation at Small Resolutions

**Rule:** Bloom FBO creation must respect the actual mip count available for the current resolution. Do not create FBOs for mip levels that don't exist.

### What went wrong

`VPRC_BloomPass` always tried to create downsample/upsample FBOs for mips 1..4, even when the bloom texture only had mip 0 (e.g., 1×1 startup window). This triggered `GL_INVALID_VALUE` from `NamedFramebufferTexture` and incomplete FBOs.

### Fix

Compute max valid bloom mip from current width/height. Set `SmallestAllowedMipmapLevel` accordingly. Only create and execute FBOs for existing mip levels.

---

## 4. Bloom: Mip 0 Must Start From Raw HDR Scene

**Rule:** The progressive bloom path in `VPRC_BloomPass` expects mip 0 to start as a raw copy of the HDR scene. Do not feed the bloom chain a pre-thresholded bright-pass texture.

### What went wrong

`VPRC_BloomPass.Execute()` copies `InputFBO.Render()` into bloom mip 0, and the first downsample level then applies the bloom threshold. `ForwardPassFBO` still used `BrightPass.fs`, so mip 0 was already thresholded before the downsample chain started. The first downsample then thresholded that signal again, collapsing bloom energy back toward the original hot pixels and making the effect read like local brightness gain instead of light bleeding outward.

### Fix

Use the standard `SceneCopy.fs` / `SceneCopyStereo.fs` material for `ForwardPassFBO.Render()` so bloom mip 0 receives the raw HDR scene. Keep threshold extraction only in the first bloom downsample level.

### Combine contract

`BloomUpsample.fs` accumulates the full multi-scale bloom result into mip 1. Default bloom combine settings should therefore sample mip 1 by default (`StartMip = 1`, `EndMip = 1`, `LOD1 Weight = 1.0`) instead of combining the coarse intermediate mips 2-4. Sampling only the coarse intermediates makes bloom read like a broad exposure lift and hides most threshold, soft-knee, radius, and scatter changes.

---

## 5. Auto-Exposure: Constructor vs Schema Defaults

**Rule:** `ColorGradingSettings` constructor defaults **must** match the pipeline schema defaults in `DefaultRenderPipeline.PostProcessing.cs` / `DefaultRenderPipeline2.PostProcessing.cs`. Keep the regression test `Defaults_MatchPipelineSchemaDefaults` in sync.

### What went wrong

The constructor used `Average` metering (biased by bright sky), `ExposureDividend = 0.18` (vs schema 0.1), `MinExposure = 0.01` (vs 0.0001), `MaxExposure = 500` (vs 100). Standalone or fallback paths using `new ColorGradingSettings()` ran with brighter, less stable exposure than pipeline-bound camera paths.

### Fix

Aligned constructor defaults. Added `ColorGradingSettingsTests.Defaults_MatchPipelineSchemaDefaults` regression test.

---

## 6. FBO Texture-Identity Recreation Predicates

**Rule:** Cached FBOs (`VPRC_CacheOrCreateFBO`) must use a texture-identity recreation predicate, not just size checks. After pipeline invalidation or resize, the FBO may hold stale references to recreated textures.

### What went wrong

Standard deferred `LightCombine` quad FBOs used size-only caching. After pipeline invalidation, the cached `XRQuadFrameBuffer` kept stale references to recreated GBuffer textures (`AlbedoOpacity`, `Normal`, `RMSE`, AO, depth, lighting, BRDF`). Result: grayscale or incorrect deferred composite even though GBuffer writes were correct.

### Fix

Added `NeedsRecreateLightCombineFbo(...)` predicates that verify FBO color/depth attachment identity matches the current pipeline textures. Applied to both pipelines.

---

## 6. Resource Cache: Variable Alias Collisions

**Rule:** `VPRC_CacheOrCreateTexture` / `VPRC_CacheOrCreateFBO` must consult `XRRenderPipelineInstance.Resources` directly, not the broader variable-aware lookup (`TryGetTexture` / `TryGetFBO`).

### What went wrong

Same-name variable aliases could masquerade as cached resources after cache invalidation. The command re-registered a descriptor but skipped rebinding a real instance. Symptom: play-mode restore followed by `texture must be FBO-attachable`, missing FBOs, and black frames.

---

## 7. Forward Pass: FBO Clear State

**Rule:** `ForwardPassFBO` must be cleared before the forward geometry pass regardless of MSAA state.

### What went wrong

When MSAA was disabled, `DynamicClearColor` / `DynamicClearDepth` were `false`, so `ForwardPassFBO` retained stale data from the previous frame. This caused ghosting artifacts and corrupted history buffers for temporal accumulation.

### Fix

Ensure the non-MSAA forward pass FBO binding always clears color and depth.

---

## 8. Deferred: GBuffer FBO vs AO FBO Separation

**Rule:** Deferred geometry **must not** bind `AmbientOcclusionFBOName` for its geometry pass. AO feature commands use that FBO as a quad source.

### What went wrong

Reusing the AO FBO for geometry let AO overwrite `AlbedoOpacity` with grayscale intensity. Dedicated `DeferredGBufferFBOName` keeps geometry and AO passes isolated.

---

## 9. Deferred: Opacity in GBuffer Alpha

**Rule:** All deferred fragment shaders must write the material `Opacity` uniform to `AlbedoOpacity.a` — never raw texture alpha (`texColor.a * Opacity`).

### Why

Many albedo textures carry non-transparency data in alpha (smoothness, AO, padding). Using texture alpha causes dithered-discard stippling at distance and `DeferredTransparencyBlur` misreading pixels as translucent.

---

## 10. MSAA: DeferredLightCombine Alpha

**Rule:** `DeferredLightCombine.fs` must output `vec4` (not `vec3`) with alpha = 1.0.

### What went wrong

`out vec3 OutLo` left alpha undefined in the MSAA renderbuffer. After MSAA resolve, deferred pixels had alpha = 0, making them appear transparent to post-processing.

---

## 11. Volumetric Fog: Separated Half-Res Temporal Chain

**Rule:** In the active `DefaultRenderPipeline` path, volumetric fog is not part of `PostProcess.fs`. Keep the stage ordering as:

`half-depth downsample -> half-res scatter -> half-res temporal reprojection -> full-res bilateral upscale -> PostProcess composite`.

### Invariants

- `VolumetricFogScatter.fs` writes raw half-res scatter/transmittance to `VolumetricFogHalfScatter`.
- `VolumetricFogVolumeComponent.EdgeFade` is a local-space distance, not a normalized fraction. The scatter shader applies it to both local box-face density falloff and ray entry/exit feathering, then erodes that fade band with the volume noise when `NoiseAmount > 0` so selected bounds do not read as hard or perfectly linear clipping planes.
- `VolumetricFogReproject.fs` samples `VolumetricFogHalfScatter`, `VolumetricFogHalfHistory`, and `VolumetricFogHalfDepth`, then writes `VolumetricFogHalfTemporal`.
- Shadowed fog keeps a low `GlobalAmbient` fill term in addition to primary directional scattering; unlit volume samples should not collapse to extinction-only darkening.
- `VolumetricFogReproject.fs` must pass through neutral current pixels `(0,0,0,1)` without history blending so stale fog cannot persist after the current ray misses every volume.
- `VolumetricFogUpscale.fs` must sample `VolumetricFogHalfTemporal`, not the raw scatter texture, so temporal filtering is included before the full-res composite.
- `VolumetricFogUpscale.fs` also re-tests the full-resolution pixel ray against `VolumetricFogWorldToLocal` / `VolumetricFogHalfExtentsEdgeFade` before bilateral filtering. This fades normal-mode output to the current volume silhouettes, using the same noise erosion as scatter, and prevents half-res taps from smearing fog outside selected bounds.
- After upscale, `VolumetricFogReprojectFBO` is blitted into `VolumetricFogHistoryFBO` and `VPRC_VolumetricFogHistoryPass` commits the current camera matrices for the next frame.
- All half-res fog textures use `GetDesiredFBOSizeHalfInternal()` and `NeedsRecreateTextureHalfInternalSize`; history is reset on size changes, AA resource invalidation, first frame, and camera cuts.
- The public camera-cut hook is `DefaultRenderPipeline.InvalidateVolumetricFogHistory(camera)`.

### Defaults

The shared `VolumetricFogSettings` constructor defaults and both pipeline schemas should stay aligned at `MaxDistance = 150`, `StepSize = 1.0`, and `JitterStrength = 0.5` unless a tuning pass updates all three places together.

---

## 11. Transparency Scene Copy

**Rule:** `ForwardPassFBO` often has a bright-pass shader attached as its quad material. Do **not** use it as the source for transparency background copies. Use a dedicated `SceneCopyFBO` sampling `HDRSceneTex`.

---

## 12. Forward Lighting: Shadow Uniform Binding

**Rule:** Primary directional shadow tuning uniforms in `ForwardLighting.glsl` (`ShadowBiasParams`, `ShadowBiasProjectionParams`, `ShadowBlockerSamples`, `ShadowFilterSamples`, `ShadowFilterRadius`, `ShadowBlockerSearchRadius`, penumbra clamps, plus contact-shadow controls) are **global base settings**, not per-light array fields.

`Lights3DCollection.SetForwardLightingUniforms` must bind these globals from `DynamicDirectionalLights[0]`. Without this, the forward path silently uses shader-literal defaults even when per-light values differ.

Cascaded directional shadows publish per-cascade effective bias values on the directional light struct (`CascadeBiasMin`, `CascadeBiasMax`, `CascadeReceiverOffsets`). Automatic values are derived from the light's texel bias controls plus cascade texel size, light-space depth span, and shadow-map resolution. `CascadeBiasMin` is the constant depth floor, `CascadeBiasMax` is the slope scale in texels, and `CascadeReceiverOffsets` is the world-space normal offset.

Live forward and deferred shadow receivers use three user-facing bias controls: `ShadowDepthBiasTexels`, `ShadowSlopeBiasTexels`, and `ShadowNormalBiasTexels`. The shaders convert those texel values to the active shadow map, cascade, atlas tile, spot projection, or cubemap face instead of relying on fixed absolute compare-bias numbers.

Local point and spot lights use SSBO-backed per-light metadata for the same tuning values. Keep `ForwardPointShadowData` / `ForwardSpotShadowData` in `ForwardLighting.glsl` and the matching `ForwardPointShadowGpu` / `ForwardSpotShadowGpu` upload structs in `Lights3DCollection.ForwardLighting.cs` in sync whenever a new shadow control is added. Do not move this metadata back to large uniform arrays; NVIDIA's OpenGL uniform constant path can exceed the 1024-register limit.

---

## 13. Screen-Space Contact Shadows

**Rule:** Deferred and forward directional, spot, and point lighting use the shared screen-space contact-shadow helper in `ShadowSampling.glsl` when a full-resolution scene depth source is available. The normal shadow-map sample still provides the large-scale shadow term, but the short-range contact multiplier is resolved from screen-space depth instead of the directional cascade, spot map, or point cubemap.

### What went wrong

The old deferred screen-space contact-shadow march did not use a shared receiver normal offset, compare-bias clamp, ray-distance thickness test, or screen-edge fade, so fully opaque deferred receivers could self-occlude into stable black speckle. The later light-space helper fixed those artifacts, but its quality was capped by the shadow map or cascade texel density and could look lower resolution than the regular filtered shadow.

### Fix

- Route deferred directional, spot, and point contact shadows through `XRENGINE_SampleContactShadowScreenSpace` using G-buffer depth.
- Route forward directional, spot, and point contact shadows through `XRENGINE_SampleForwardContactShadowScreenSpace` when `ForwardDepthPrePassEnabled` has produced a stable `ForwardContactDepthView`/`ForwardContactNormal` snapshot. The snapshot is copied from the completed forward depth-normal merge prepass before the forward color pass binds the main depth attachment, avoiding an OpenGL framebuffer feedback loop where the Uber shader samples the same depth texture it is rendering into. Mono uses 2D samplers; stereo uses layered array samplers selected by the forward view index. Forward falls back to the older light-space contact helpers when those pre-pass textures are unavailable.
- Reconstruct contact-shadow scene positions from raw depth with the matching `InverseProjMatrix`; do not flip reversed-Z depth before inverse projection. Only the far-depth validity check is depth-mode aware.
- Keep the screen-space march in the shared snippet, not per-light local shader code, so all light types use the same bias clamp, normal offset, ray-thickness rejection, MSAA depth sampling, screen-edge fade, and optional pre-pass normal rejection.
- Reuse `XRENGINE_ResolveContactShadowSampleCount` so forward and deferred scale contact-shadow step counts identically.

---

## 14. Fallback Sampler Unit Collisions

**Rule:** Forward-lighting mesh draws reserve many fixed sampler units (env/refl 12–13, AO 14 & 25, directional shadow 15–16, point shadows 17–20, spot shadows 21–24, forward contact depth/normal 26–27, forward contact depth/normal arrays 28–29). Fallback sampler binding must allocate from genuinely unused units, not hard-coded offsets.

---

## 15. Pipeline Event Teardown

**Rule:** Pipelines subscribe to `Engine.Rendering.SettingsChanged` and `AntiAliasingSettingsChanged` in their constructors. They **must** unsubscribe in `OnDestroying()`.

Settings handlers that queue `Engine.InvokeOnMainThread(...)` should guard both before queueing and inside the queued lambda with `IsDestroyed` to prevent a destroyed pipeline from rebuilding its command chain.

---

## 16. Camera HDR Output Target Recreation

**Rule:** When per-camera `OutputHDROverride` changes, all format-dependent targets need `NeedsRecreate` delegates — size-only checks leave stale LDR targets.

Affected FBOs: `PostProcessOutputFBO`, `FxaaFBO`, `TsrHistoryColorFBO`, `TsrUpscaleFBO`.

If a quad FBO writes to an explicitly assigned output attachment, create it with `deriveRenderTargetsFromMaterial: false`. Leaving derivation enabled makes the cache predicate classify the FBO as stale immediately and can force unnecessary recreate/destroy churn in the post-AA path.

`RenderTextureResource.Bind()` **destroys** the old texture when replacing. FBOs holding destroyed textures become incomplete → black screen. Always verify FBO attachment identity after format changes.

Use `ResolveOutputHDR()` (which reads `RenderingPipelineState.SceneCamera`) rather than `Engine.Rendering.Settings.OutputHDR`, because `XRQuadFrameBuffer.Render()` calls `PushRenderingCamera(null)` during uniform setting.

---

## 17. Deferred Geometry Normals Must Be Normalized Before Encoding

**Rule:** Deferred fragment shaders that encode the interpolated geometric normal directly must call `normalize(FragNorm)` before `XRENGINE_EncodeNormal(...)`.

### What went wrong

Several deferred material variants wrote `XRENGINE_EncodeNormal(FragNorm)` straight into the GBuffer. On imported geometry, interpolated normals were not guaranteed to remain unit length, so octahedral encoding stored skewed directions. Result: deferred lighting could collapse toward black or heavily distorted shading on materials that relied on geometric normals instead of tangent-space normal maps.

### Fix

Normalize `FragNorm` in the direct-geometry deferred shaders (`ColoredDeferred`, `TexturedDeferred`, `TexturedAlphaDeferred`, `TexturedSpecDeferred`, `TexturedSpecAlphaDeferred`, `TexturedMetallicDeferred`, `TexturedMetallicRoughnessDeferred`, `TexturedRoughnessDeferred`, `TexturedEmissiveDeferred`).

---

## 18. Deferred Debug Output Switch

**Rule:** When diagnosing deferred composite issues, use the render pipeline's `Deferred Debug View` setting in the ImGui inspector instead of ad-hoc shader edits.

`DeferredLightCombine.fs` exposes debug modes via the `DeferredDebugMode` uniform, wired from the `Deferred Debug View` property on `DefaultRenderPipeline` / `DefaultRenderPipeline2`.

Current modes:

- `1` = raw albedo
- `2` = accumulated direct lighting (`InLo`)
- `3` = RMSE buffer
- `4` = decoded normal
- `5` = depth

---

## 19. Temporal AA: Static Edge Rejection and Debug Views

**Rule:** Temporal reprojection needs to stay conservative on moving or disoccluded pixels, but static silhouettes must still accumulate enough history to hide the camera jitter.

### What went wrong

The temporal resolve already used motion vectors, depth rejection, neighborhood clipping, and reactive masks, but the static-edge path was still too distrustful. The combination of a low minimum history weight, a tight depth reject threshold, and an aggressive depth-discontinuity scale left many static silhouettes sampling mostly the current jittered frame instead of converging to a stable anti-aliased edge.

### Fix

- Raised the default temporal history floor and max history weight.
- Relaxed the depth reject threshold and reduced depth discontinuity amplification.
- Boosted the confidence floor for static pixels while keeping moving geometry governed by the existing reactive and motion masks.
- Added a `Temporal AA -> Debug View` enum so the resolve can visualize history weight, velocity, geometry instability, reactive masking, and history acceptance directly from the shader.

### Practical debugging guidance

- Use `HistoryWeight` to verify stable edges are actually accumulating.
- Use `Velocity` to confirm reprojection is receiving non-zero motion where expected.
- Use `GeometryInstability` to find edges being over-rejected by depth discontinuities.
- Use `HistoryAcceptance` to distinguish outright history rejection from low-confidence blending.
- In the editor, these Temporal AA controls only affect the camera that is actually driving the active viewport. If the scene panel is rendering through the editor flying camera, changing a different camera component's Temporal AA settings will not change the scene panel output.
- The Temporal AA controls only affect the live output when the active camera's effective AA mode is `TAA` or `TSR`; if the camera is using `None`, `MSAA`, `FXAA`, or `SMAA`, the temporal stage settings are intentionally inert.

---

## 20. Light Shadow Inspector Naming

**Rule:** Keep the light inspector labels aligned with the actual shader paths.

- `Hard / PCF` is the crisp compare path with small PCF/tent fallbacks.
- `Fixed Soft (Poisson)` maps to `ESoftShadowMode.FixedPoisson`. It is not true PCSS; it is fixed-radius filtering.
- `PCSS / Contact Hardening` maps to `ESoftShadowMode.ContactHardeningPcss` and is the blocker-search variable-penumbra path.
- `Fixed Soft (Vogel)` is a fixed-radius golden-angle disk filter.
- `PCSS / Contact Hardening` exposes separate blocker samples, filter samples, blocker-search radius, penumbra clamps, and source radius. Blocker search and the variable filter pass use rotated Vogel taps to reduce repeated low-sample patterns.
- `Short-Range Contact Shadows` are separate from contact-hardening soft shadows. They are multiplied with the normal shadow-map result for directional, spot, and point lights.

Contact shadows expose distance, sample count, thickness, fade range, normal offset, and jitter strength. Use the fade range to limit near-field detail work to the camera distances where the extra ray march is visible.

Directional lights default to short-range contact shadows enabled with distance `1.0`, `16` samples, thickness `2.0`, fade range `10` to `40`, zero normal offset, and full jitter.

Spot lights default to `512x512` shadow maps, PCSS/contact-hardening filtering, `8` blocker samples, `8` filter samples, blocker-search radius `0.1`, penumbra clamp `0.0002` to `0.05`, source radius `0.1`, and short-range contact shadows with distance `0.1`, `16` samples, thickness `1.0`, fade range `10` to `40`, normal offset `0.036`, and full jitter.

Point and spot lights enable `Auto From Light Radius` by default in the PCSS/contact-hardening inspector. Point lights derive from their attenuation radius, while spot lights derive from the outer cone radius at their configured distance. The automatic source radius is capped at `0.25` to preserve hard contact detail, and the manual source-radius value is preserved and used again if the toggle is disabled.

---

## 21. Forward Depth-Normal Transform IDs

**Rule:** The shared forward depth-normal prepass must update depth, normal, and `TransformId` for the same forward surface.

The prepass uses a compact local color layout: `Normal` writes to color attachment 0 and `TransformId` writes to color attachment 1. This intentionally differs from the deferred GBuffer shader convention where `TransformId` is color attachment 3; the merge FBO still attaches the main `TransformId` texture, just at the prepass-local slot.

Keep `ForwardDepthPrePassMergeFBO` bound with color/depth/stencil clears disabled so deferred IDs survive where forward geometry does not draw. The dedicated `ForwardDepthPrePassFBO` must use its own `ForwardPrePassTransformId` texture when debug-only ID output is needed, because that FBO is allowed to clear before rendering.

Generated vertex shaders are allowed to trim `FragTransformId` for ordinary passes, but the forward depth-normal prepass must push `RequireGeneratedVertexTransformId`. Without that render-state requirement, a generated vertex program cached for a normal forward material can be reused with a depth-normal fragment variant that consumes `FragTransformId`, causing pipeline interface mismatches.

---

## 22. Point-Light Cubemap Shadow Regression

**Rule:** Point-light shadow cubemaps are base-mip shadow targets. The OpenGL cube texture object must apply the engine sampler state and must clamp the exposed mip range to the rendered mip.

### What went wrong

`GLTextureCube` was leaving OpenGL cube textures on default sampler/mip behavior: the texture upload path hard-coded `GL_TEXTURE_MAX_LEVEL = 1000`, and cube textures did not apply `MinFilter`, `MagFilter`, or cube wrap modes in `SetParameters()`. Point-light shadow maps render only mip 0, but the R16f radial-depth cubemap could still be sampled as though lower mips existed. That produced scene-independent false shadows, including shadows when the visible base-level shadow map was empty or correct.

### Fix

- `GLTextureCube` now uses `LargestMipmapLevel`, `SmallestAllowedMipmapLevel`, `MinLOD`, and `MaxLOD` instead of hard-coded LOD bounds.
- `GLTextureCube.SetParameters()` applies LOD bias, min/mag filters, and S/T/R wrap modes.
- Point-light depth and R16f radial-depth cubemaps set `SmallestAllowedMipmapLevel = 0` because the shadow renderer clears and renders only mip 0.

### Point-Light Shadow Capture Invariants

- Do not disable point-light contact shadows to hide cubemap errors. Deferred and forward point lights should keep short-range contact shadows available; the contact term is separate from the large-scale cubemap visibility.
- The geometry-shader path renders all six cubemap faces from one viewport and culls against the point light influence sphere. Keep that sphere and the shadow camera parent render transform synchronized to `Transform.RenderTranslation` before collection and rendering.
- Shared opaque casters usually render with the point shadow material itself as the global override. `GLMaterial` must call `OnSettingShadowUniforms` for that material during shadow passes so `LightPos`, `FarPlaneDist`, and geometry-shader `ViewProjectionMatrices[]` are populated. Alpha/transparent point-shadow variants route through `ShadowUniformSourceMaterial`; if the global override path skips shadow uniforms, transparent casters can appear while shared opaque casters write invalid or no point-shadow data.
- The six-pass fallback renders each cubemap face with its own shadow camera and the same point-shadow material. If it captures only transparent forward meshes, inspect opaque-pass command collection and point-shadow material override/variant selection; filtering mode and contact shadows are not the root cause.
