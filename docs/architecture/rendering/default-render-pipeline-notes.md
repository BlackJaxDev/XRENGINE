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

## 4. Auto-Exposure: Constructor vs Schema Defaults

**Rule:** `ColorGradingSettings` constructor defaults **must** match the pipeline schema defaults in `DefaultRenderPipeline.PostProcessing.cs` / `DefaultRenderPipeline2.PostProcessing.cs`. Keep the regression test `Defaults_MatchPipelineSchemaDefaults` in sync.

### What went wrong

The constructor used `Average` metering (biased by bright sky), `ExposureDividend = 0.18` (vs schema 0.1), `MinExposure = 0.01` (vs 0.0001), `MaxExposure = 500` (vs 100). Standalone or fallback paths using `new ColorGradingSettings()` ran with brighter, less stable exposure than pipeline-bound camera paths.

### Fix

Aligned constructor defaults. Added `ColorGradingSettingsTests.Defaults_MatchPipelineSchemaDefaults` regression test.

---

## 5. FBO Texture-Identity Recreation Predicates

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

## 11. Transparency Scene Copy

**Rule:** `ForwardPassFBO` often has a bright-pass shader attached as its quad material. Do **not** use it as the source for transparency background copies. Use a dedicated `SceneCopyFBO` sampling `HDRSceneTex`.

---

## 12. Forward Lighting: Shadow Uniform Binding

**Rule:** Shadow tuning uniforms in `ForwardLighting.glsl` (`ShadowBase`, `ShadowMult`, `ShadowBias`, `ShadowSamples`, `ShadowFilterRadius`, plus contact-shadow controls) are **global**, not per-light array fields.

`Lights3DCollection.SetForwardLightingUniforms` must bind these globals from `DynamicDirectionalLights[0]`. Without this, the forward path silently uses shader-literal defaults even when per-light values differ.

---

## 13. Contact-Shadow Helper Parity

**Rule:** Deferred directional and spot lighting must use the shared contact-shadow helpers in `ShadowSampling.glsl`, the same as `ForwardLighting.glsl`. Do not keep separate screen-space contact-shadow marchers in the deferred shaders.

### What went wrong

`DeferredLightingDir.fs` and `DeferredLightingSpot.fs` had their own contact-shadow ray march against the current frame depth buffer. That path did not use the shared receiver normal offset, compare bias, or proportional thickness rejection already used by the forward path, so fully opaque deferred receivers could self-occlude into stable black speckle while the forward path stayed clean.

### Fix

- Route deferred directional and spot contact shadows through `XRENGINE_SampleContactShadow2D` / `XRENGINE_SampleContactShadowArray`.
- Reuse `XRENGINE_ResolveContactShadowSampleCount` so forward and deferred scale contact-shadow step counts identically.
- Pass the same receiver offset and compare bias inputs used by the forward path (`ShadowBiasMax` plus the angle-scaled shadow bias).

---

## 14. Fallback Sampler Unit Collisions

**Rule:** Forward-lighting mesh draws reserve many fixed sampler units (env/refl 12–13, AO 14 & 25, directional shadow 15–16, point shadows 17–20, spot shadows 21–24). Fallback sampler binding must allocate from genuinely unused units, not hard-coded offsets.

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
