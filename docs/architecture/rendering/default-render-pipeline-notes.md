# Default Render Pipeline — Known Issues and Lessons Learned

This document records bugs that were found and fixed in `DefaultRenderPipeline` and `DefaultRenderPipeline2`. Its purpose is to prevent the same classes of mistake from being reintroduced. Read this before making non-trivial changes to the pipeline texture setup, FBO management, post-processing, or render pass ordering.

---

## Resource Generation Lifecycle

`DefaultRenderPipeline` declares stable pipeline-owned resources through
`DescribeResources(...)`. `XRRenderPipelineInstance` materializes those specs
into a pending `RenderResourceGeneration`, validates required resources,
texture-view source identity/ranges/aspect/target, and framebuffer attachment
identity/size/sample/format/backend completeness, then atomically commits the
generation before frame command execution.

Resize, HDR-output changes, and AA/MSAA changes must request a replacement
generation instead of emptying the active registry. Replacement generations are
debounced during interactive resize and prepared incrementally while the active
generation keeps rendering. A failed pending generation logs diagnostics and
leaves the active generation rendering. Both OpenGL and Vulkan consume the same
generation-owned descriptors; OpenGL creates concrete objects through the
existing factories, while Vulkan stages a pending physical resource plan before
swapping it into active renderer state.

Compatibility cache commands still exist for dynamic or branch-local resources
such as bloom chains, atmosphere/fog half-resolution chains, SMAA,
exact-transparency scratch resources, AO-dependent deferred light-combine
quad FBOs, and command-local fullscreen materials.
Do not add new stable core render targets to cache commands without also adding
them to the declared resource layout.

Full contract: [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md).

---

## Selecting `DefaultRenderPipeline2`

`DefaultRenderPipeline` remains the default production path. To opt into the parallel V2 pipeline for local validation, launch the editor or client with:

```powershell
$env:XRE_USE_PIPELINE_V2 = "1"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

The `XRE_USE_PIPELINE_V2=1` environment variable is read by the render pipeline factory, including stereo and OpenXR-created pipelines. Leave it unset to use the default pipeline.

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
- Default combine weights now use the tuned bloom profile (`StartMip=1`, `EndMip=4`, weights 0.0 / 1.0 / 0.649 / 0.397 / 0.102 on LOD0-4, `BloomStrength=0.5805`).

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

`BloomUpsample.fs` accumulates the full multi-scale bloom result into mip 1. Default bloom combine settings should therefore keep mip 1 as the full-strength foundation while blending in the tuned blurrier mip weights (`StartMip = 1`, `EndMip = 4`, `LOD1 Weight = 1.0`, `LOD2 Weight = 0.649`, `LOD3 Weight = 0.397`, `LOD4 Weight = 0.102`). Sampling only the coarse intermediates makes bloom read like a broad exposure lift and hides most threshold, soft-knee, radius, and scatter changes.

---

## 5. Auto-Exposure: Constructor vs Schema Defaults

**Rule:** `ColorGradingSettings` constructor defaults **must** match the pipeline schema defaults in `DefaultRenderPipeline.PostProcessing.cs` / `DefaultRenderPipeline2.PostProcessing.cs`. Keep the regression test `Defaults_MatchPipelineSchemaDefaults` in sync.

### What went wrong

The constructor used `Average` metering (biased by bright sky), `ExposureDividend = 0.18` (vs schema 0.1), `MinExposure = 0.01` (vs 0.0001), `MaxExposure = 500` (vs 100). Standalone or fallback paths using `new ColorGradingSettings()` ran with brighter, less stable exposure than pipeline-bound camera paths.

### Fix

Aligned constructor defaults. Added `ColorGradingSettingsTests.Defaults_MatchPipelineSchemaDefaults` regression test.

---

## 6. FBO Texture-Identity Recreation Predicates

**Rule:** Default-pipeline FBO attachments must come from the same declared resource generation. Command execution must never recreate an FBO or swap its texture identities.

### What went wrong

Standard deferred `LightCombine` quad FBOs used size-only caching. After pipeline invalidation, the cached `XRQuadFrameBuffer` kept stale references to recreated GBuffer textures (`AlbedoOpacity`, `Normal`, `RMSE`, AO, depth, lighting, BRDF`). Result: grayscale or incorrect deferred composite even though GBuffer writes were correct.

### Fix

The retained `DefaultRenderPipeline` now declares light-combine dependencies and validates their generation identity before commit. `DefaultRenderPipeline2` still uses the compatibility predicate pending its removal.

---

## 6. Resource Cache: Variable Alias Collisions

**Rule:** `DefaultRenderPipeline` commands may resolve declared resources but may not register or replace them. Legacy cache commands in other pipelines must consult `XRRenderPipelineInstance.Resources` directly until those commands are removed.

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

## 11. Mesh Submission Strategy

**Rule:** Default mesh passes must request `Engine.Rendering.ResolveMeshSubmissionStrategy()` through `MeshSubmissionStrategy`, while `PreRender` and `PostRender` stay CPU-only.

The strategy contract is documented in [Mesh Submission Strategies](mesh-submission-strategies.md). In short, `GpuIndirectInstrumented` is the only GPU path that may perform CPU readbacks or CPU mesh safety-net fallback. `GpuIndirectZeroReadback` is the production path and must not call count, batch, or indirect-buffer readback helpers during steady-state render submission.

When adding a mesh pass to `DefaultRenderPipeline` or `DefaultRenderPipeline2`, use:

```csharp
c.Add<VPRC_RenderMeshesPass>().SetOptions(renderPass, MeshSubmissionStrategy);
```

Use `false` only for known CPU-only passes such as `PreRender` and `PostRender`.

---

## 12. Empty-Scene Pass Gates

**Rule:** Expensive pass groups that only prepare resources around optional scene work must be gated by the current frame's mesh-command and feature-state presence. A method/callback command is not proof that geometry exists.

The active `DefaultRenderPipeline` path currently skips these groups when they would otherwise be no-op setup:

- `RenderCommandCollection` publishes total and per-pass mesh-command counts with the render-side snapshot. The count is collected during the existing membership-signature scan; do not rescan the command lists from each pipeline gate.
- A callback-only frame with no mesh commands can use the lightweight scene branch, which preserves the `OpaqueForward` and `OnTopForward` callbacks needed by debug producers while skipping GBuffer, AO, deferred lighting, Forward+, velocity, bloom, fog, and overdraw setup.
- The full branch remains mandatory for shadow/stereo/capture/light-probe/mirror/MSAA/voxel-cone work, atmospheric or fog work, relevant debug visualizations, Forward+ diagnostics, any scene mesh, or callbacks in passes the lightweight branch does not explicitly preserve.
- Forward depth prepass and GBuffer restore run only when the debug preference is enabled and the current frame has `OpaqueForward` or `MaskedForward` mesh commands.
- Weighted/exact transparency resource work runs only when the current frame has transparent render commands, or when transparency/depth-peeling debug visualizations need those buffers.
- Mono atmospheric aerial perspective runs only when the stage is enabled, requests aerial/debug output, and the current camera has an active atmosphere with aerial perspective enabled.
- Mono volumetric fog runs only when the stage is enabled and at least one active renderable fog volume exists.

Auxiliary geometry passes must replay `IRenderCommandMesh` commands only. Do not invoke method callbacks from velocity, overdraw, depth/normal, shadow, or similar mesh replays; callback side effects belong to their primary scene pass.

When adding a new optional pass group, prefer a named `VPRC_IfElse` gate so GPU pipeline dumps explain why the group is absent or active. Keep debug visualization modes in the condition if the visualization depends on otherwise-empty resources.

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

## 11. Atmospheric Scattering: Sky And Aerial Perspective Chain

**Rule:** Planetary atmosphere is a separated sky/background and aerial-perspective path. `PostProcess.fs` only composites the precomputed `AtmosphereColor` texture and must not contain atmosphere raymarching math.

The mono OpenGL path runs before local volumetric fog and before exposure:

`half-depth downsample -> half-res atmosphere scatter -> half-res temporal reprojection -> full-res bilateral upscale -> PostProcess atmosphere composite -> VolumetricFogColor composite`.

### Invariants

- `AtmosphericScatteringComponent` owns sky-background rendering in `EDefaultRenderPass.Background` and selects one active atmosphere per camera.
- `AtmosphericScatteringSettings` is exposed through the `atmosphericScattering` post-process stage in both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.
- `AtmosphereAerialPerspective.fs` writes raw scatter/transmittance to `AtmosphereHalfScatter`.
- `AtmosphereReproject.fs` samples `AtmosphereHalfScatter`, `AtmosphereHalfHistory`, and `AtmosphereHalfDepth`, then writes `AtmosphereHalfTemporal`.
- `AtmosphereUpscale.fs` samples `AtmosphereHalfTemporal`, not raw scatter, so temporal filtering is included before the full-resolution composite.
- Disabled, no-active, and far-depth/sky pixels must output neutral `(0,0,0,1)` so the composite is a no-op and sky pixels are not double-atmosphered.
- `PostProcess.fs` composites atmosphere before local volumetric fog with `hdrSceneColor = hdrSceneColor * atmosphere.a + atmosphere.rgb`.
- FBO recreation predicates must verify attachment texture identity for all atmosphere FBOs, not only size.
- `VPRC_AtmosphereHistoryPass` resets history on first frame, size change, camera cut, active atmosphere switch, atmosphere revision changes, and AA resource invalidation.
- The public camera-cut hook is `DefaultRenderPipeline.InvalidateAtmosphereHistory(camera)`.

### Defaults

The component uses SI-like Earth defaults (`GroundRadius = 6,371,000`, `AtmosphereHeight = 100,000`) while treating the component origin as the local ground point. This keeps authoring convenient without requiring the whole scene to be placed at planet-center coordinates.

---

## 11. Transparency Scene Copy

**Rule:** `ForwardPassFBO` often has a bright-pass shader attached as its quad material. Do **not** use it as the source for transparency background copies. Use a dedicated `SceneCopyFBO` sampling `HDRSceneTex`.

---

## 12. Forward Lighting: Shadow Uniform Binding

**Rule:** Primary directional shadow tuning uniforms in `ForwardLighting.glsl` (`ShadowBiasParams`, `ShadowBiasProjectionParams`, `ShadowBlockerSamples`, `ShadowFilterSamples`, `ShadowFilterRadius`, `ShadowBlockerSearchRadius`, penumbra clamps, plus contact-shadow controls) are **global base settings**, not per-light array fields.

`Lights3DCollection.SetForwardLightingUniforms` must bind these globals from `DynamicDirectionalLights[0]`. Without this, the forward path silently uses shader-literal defaults even when per-light values differ.

Forward shading reserves two directional shadow slots. `DirectionalShadowMaps[0..1]`, `DirectionalShadowMapArrays[0..1]`, and the flattened directional atlas metadata cover only `DynamicDirectionalLights[0]` and `[1]`; additional directional lights still shade but do not sample forward shadow maps. The global shadow-filter controls remain sourced from light 0, while `DirectionalShadowBiasProjectionParams[0..1]` carries the per-light primary projection bias needed by non-cascaded maps.

Cascaded directional shadows publish per-cascade effective bias values on the directional light struct (`CascadeBiasMin`, `CascadeBiasMax`, `CascadeReceiverOffsets`). Automatic values are derived from the light's texel bias controls plus cascade texel size, light-space depth span, and shadow-map resolution. `CascadeBiasMin` is the constant depth floor, `CascadeBiasMax` is the slope scale in texels, and `CascadeReceiverOffsets` is the world-space normal offset.

Forward cascade receivers must not sample the last cascade after the view-space depth has passed that cascade's far split. The last cascade fades to the contact/lit fallback over its configured blend width, and fragments beyond the final split use the same fallback instead of reading undefined/stale far-cascade depth.

Live forward and deferred shadow receivers use three user-facing bias controls: `ShadowDepthBiasTexels`, `ShadowSlopeBiasTexels`, and `ShadowNormalBiasTexels`. The shaders convert those texel values to the active shadow map, cascade, atlas tile, spot projection, or cubemap face instead of relying on fixed absolute compare-bias numbers.

Atlas tiles also publish `requestedResolution / allocatedResolution` in their depth metadata. Receiver shaders use that scale to recover the authored texel size for bias math, while atlas PCF/PCSS taps remain in local shadow-map UV space and are clamped inside the tile before converting to atlas page UVs. This keeps bias and contact-hardening settings tuned for a standalone `4096` map from being double-scaled or contaminated by neighboring atlas tiles when the allocator demotes the tile to `2048`, `1024`, etc. Directional atlas receivers sample the page raster depth attachment, not the atlas color attachment, so directional atlas compares use the same depth precision/encoding class as the non-atlased directional path.

Directional lights expose `CascadeShadowRenderMode` for both cascade backends: legacy texture-array cascades and directional atlas page cascades. `Auto` is the default request path so capable Vulkan/OpenGL backends use grouped atlas or layered cascade rendering without an explicit opt-in; `Sequential` remains selectable for debugging and compatibility. On the legacy texture-array backend, `GeometryShader` renders all active cascades through the layered framebuffer using `DirectionalCascadeShadowDepth.gs`, and `InstancedLayered` renders one layered pass when the active backend reports vertex-stage layer writes. On the atlas backend, the same requested modes become grouped atlas cascade passes only when all active cascade allocations are resident on one atlas page and the backend exposes indexed viewport/scissor state plus the required shader-stage viewport writes. The atlas geometry path uses `DirectionalCascadeAtlasShadowDepth.gs`; the atlas instanced path uses a generated vertex shader that writes `gl_ViewportIndex`. Meshes that already use draw instancing or mesh-deform state fall back to the matching geometry-shader shadow variant inside grouped passes so their instance semantics are not stolen by the cascade dimension. `Auto` selects instanced grouped/layered first, then geometry shader, then sequential when capabilities are missing. `EffectiveCascadeShadowRenderMode`, `EffectiveCascadeShadowRenderBackend`, and `CascadeShadowRenderFallbackReason` expose the most recent decision. Any grouped atlas precondition failure leaves the existing per-tile sequential atlas renderer in charge.

Point lights expose `ShadowRenderMode` for the legacy cubemap shadow path: `Sequential`, `InstancedLayered`, and `GeometryShader`. `InstancedLayered` is the default request path; `Sequential` renders one cubemap face at a time when selected or required as fallback. `GeometryShader` renders selected faces through `PointLightShadowDepth.gs`, using a six-bit `PointShadowFaceMask` so non-relevant cubemap layers do not emit geometry. `InstancedLayered` renders selected faces in one layered pass on OpenGL drivers with vertex-stage `gl_Layer` support; compact face indices keep partial masks from stealing mesh-instance semantics. Meshes that already use draw instancing or mesh-deform state fall back to the geometry-shader point-shadow variant inside that layered pass. `EffectiveShadowRenderMode` and `ShadowRenderFallbackReason` expose the most recent decision. Point atlas mode uses the same render-mode selection when same-page face groups are available; sequential direct-to-atlas face-tile rendering remains the compatibility fallback.

Dynamic shadow atlas page ownership is light-family scoped. Each live atlas family/encoding owns atlas page resources where `PageIndex` is the array layer. Page index alone is not globally unique, so consumers must use `AtlasKind` + encoding + page index, or the packed `AtlasId`. `MaxShadowAtlasPages` is honored per family/encoding, with a runtime default of `1`; backing array layers grow geometrically as pages are created and memory-budget checks account for actual allocated layers instead of the full page limit. Directional depth atlas pages are depth-only: they attach the raster-depth array to the page FBO and skip the unused color/moment array. When `UseDirectionalShadowAtlas` is true, directional atlas mode is authoritative once the required tiles are resident: forward and deferred passes bind dummy legacy directional maps and do not fall back to `ShadowMap` / `ShadowMapArray` just because a cascade missed the atlas. The allocator must instead reduce directional cascade tile resolutions until every required active cascade for a light is resident in the directional atlas. Vulkan directional depth receivers sample the raster-depth atlas page, while directional VSM/EVSM atlas sampling remains disabled until Vulkan moment-atlas rendering is implemented. Receiver binding enables the directional atlas for a light only when its required active directional tiles are sampleable; each slot still publishes fallback metadata so skipped or stale tiles have defined behavior.

Shadow atlas request planning runs on the collect-visible side before light caster collection. The planner drains tile-completion feedback, builds requests, solves allocations, publishes frame metadata, and freezes an immutable `ShadowAtlasRenderPlan`. `Lights3DCollection.SwapBuffers()` publishes the plan and `ShadowAtlasFrameData` by reference swap; the render thread interprets the frozen plan only, applies tile/time budgets, issues GPU work, and enqueues `(key, contentHash, frameId)` completions. Completion feedback is intentionally visible to planning one frame later, so first-render allocations publish a fallback until the next plan reconciles their `LastRenderedFrame`.

The atlas solve path classifies submitted requests once into light-family/encoding buckets, then solves each bucket with a persistent fixed-level buddy allocator backed by per-level slot bitsets. Prior resident slots stay allocated across frames and are reused without rebuilding the whole page occupancy map; departed residents are freed on TTL expiry, explicit repack, settings shape changes, or resolution changes. A feasibility waterline demotes lower-relevance entries before placement, and single placement failures repair locally instead of resetting every page. Down-sized tiles keep an aligned sub-rect of their old slot when possible, and failed in-place upgrades keep the previous tile for another frame instead of migrating. Stale `NotRelevant` allocations are freed before live placement and re-reserved only after live allocations claim their regions; if a live tile needs the old space, the stale tile publishes a non-resident fallback instead of overlapping. Forced demotion remains relevance/priority driven with a sticky cooldown so point faces and equal-priority locals do not rotate victims under steady contention.

`ShadowAtlasManager.SolveAllocations` publishes per-frame solve diagnostics through `ShadowAtlasFrameData`, render stats packets, and the ImGui profiler. The counters split plan/solve time from shadow rendering time: classified request counts, balanced-solve attempts, failed candidates, demotions, waterline demotions, incremental reuse, prior-slot reuse, page allocation/clear activity, and directional/point group publishing work are reported together. Slow-solve warnings are controlled by `XRE_SHADOW_ATLAS_SOLVE_WARN_MS` and should include the same counters. Keep allocator changes deterministic and bounded: over-budget solves must demote in stable batches, avoid unbounded restarts, and avoid reintroducing one-failed-candidate-per-full-restart behavior.

Spot and point atlas pages are currently depth-only. A local light whose resolved `ShadowMapEncoding` is VSM or EVSM bypasses the local atlas even when the matching atlas toggle is enabled, renders the standalone moment shadow map, regenerates its mip chain when `ShadowMomentUseMipmaps` is enabled, and samples those mips with explicit LOD instead of main-camera screen-space derivatives. Local shadow relevance gates the standalone spot render before mip generation: a non-relevant spot frustum skips both the render and mip regeneration, then regenerates mips normally after the next relevant render. Moment atlas pages remain tracked by the VSM/EVSM shadow-filtering plan.

When `UsePointShadowAtlas` is true and the resolved encoding is depth, dynamic point lights render independent `PointFace` requests directly into the point atlas as 2D face tiles. The request pass intersects each point face frustum against the active local shadow relevance camera set and forces `SkipReason.NotRelevant` for faces that cannot affect any consuming camera. Same-page dirty faces are grouped and published in `ShadowAtlasFrameData` when the selected point render mode and OpenGL capabilities support indexed viewport/scissor output: the atlas instanced path writes `gl_ViewportIndex` from the compact face slot, and the atlas GS path fans triangles to the selected face slots. Sequential direct-to-atlas rendering remains the compatibility fallback. Once the render scheduler starts refreshing one relevant face for a point light, adjacent dirty relevant faces for that same light are allowed to continue past the soft per-frame budget, matching directional cascade set behavior and preventing interactive light transforms from updating one cube face at a time. Forward and deferred receivers select the face by major axis, convert the receiver direction to face-local UV, and compare radial normalized depth against that face's tile metadata. Point atlas filtering is cubemap-seam aware: each PCF/PCSS/Vogel tap perturbs the receiver direction, re-selects the owning face, and samples that face's atlas metadata, so taps crossing a face edge do not clamp against the original tile. Missing, demoted, or non-relevant faces use their published fallback, usually a stale tile when a previous render exists or contact-only before first render, instead of sampling undefined atlas texels. The legacy point cubemap path remains available when point atlas mode is disabled or the point light resolves to a moment encoding.

Local point and spot lights use SSBO-backed per-light metadata for the same tuning values. Forward+ local light records also carry the source light index plus the atlas shadow record index so atlas-enabled local lights do not depend on the fixed four-entry legacy sampler arrays. Keep `ForwardPointShadowData` / `ForwardSpotShadowData` in `ForwardLighting.glsl` and the matching `ForwardPointShadowGpu` / `ForwardSpotShadowGpu` upload structs in `Lights3DCollection.ForwardLighting.cs` in sync whenever a new shadow control is added. Do not move this metadata back to large uniform arrays; NVIDIA's OpenGL uniform constant path can exceed the 1024-register limit.

---

## 13. Screen-Space Contact Shadows

**Rule:** Deferred and forward directional, spot, and point lighting use the shared screen-space contact-shadow helper in `ShadowSampling.glsl` when a full-resolution scene depth source is available. The normal shadow-map sample still provides the large-scale shadow term, but the short-range contact multiplier is resolved from screen-space depth instead of the directional cascade, spot map, or point cubemap.

### What went wrong

The old deferred screen-space contact-shadow march did not use a shared receiver normal offset, compare-bias clamp, ray-distance thickness test, or screen-edge fade, so fully opaque deferred receivers could self-occlude into stable black speckle. The later light-space helper fixed those artifacts, but its quality was capped by the shadow map or cascade texel density and could look lower resolution than the regular filtered shadow.

### Fix

- Route deferred directional, spot, and point contact shadows through `XRENGINE_SampleContactShadowScreenSpace` using G-buffer depth.
- Route forward directional, spot, and point contact shadows through `XRENGINE_SampleForwardContactShadowScreenSpace` when the active default render pipeline's `ForwardDepthPrePassEnabled` setting has produced a stable `ForwardContactDepthView`/`ForwardContactNormal` snapshot. The snapshot is copied from the completed forward depth-normal merge prepass before the forward color pass binds the main depth attachment, avoiding an OpenGL framebuffer feedback loop where the Uber shader samples the same depth texture it is rendering into. Mono uses 2D samplers; stereo uses layered array samplers selected by the forward view index. Forward falls back to the older light-space contact helpers when those pre-pass textures are unavailable.
- Reconstruct contact-shadow scene positions from raw depth with the matching `InverseProjMatrix`; do not flip reversed-Z depth before inverse projection. Only the far-depth validity check is depth-mode aware.
- Keep the screen-space march in the shared snippet, not per-light local shader code, so all light types use the same bias clamp, normal offset, ray-thickness rejection, MSAA depth sampling, screen-edge fade, and optional pre-pass normal rejection.
- Reuse `XRENGINE_ResolveContactShadowSampleCount` so forward and deferred scale contact-shadow step counts identically.

---

## 14. Fallback Sampler Unit Collisions

**Rule:** Forward-lighting mesh draws reserve many fixed sampler units: directional atlas array 9, env/refl 12-13, AO 14 and 27, directional 2D shadows 15-16, directional cascade arrays 17-18, point shadows 19-22, spot shadows 23-26, forward contact depth/normal 28-29, forward contact depth/normal arrays 30-31, spot atlas array 32, and point atlas array 34. Fallback sampler binding must allocate from genuinely unused units, not hard-coded offsets.

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
- `6` = directional shadow factor
- `7` = directional shadow receiver depth
- `8` = directional shadow sample depth
- `9` = directional shadow single-tap lit result
- `10` = ambient occlusion sampled by light-combine

---

## 19. Ambient Occlusion Coordinate Convention

**Rule:** AO shaders store and sample texture UVs in framebuffer texture space, but all projection/reconstruction helpers must convert through the active clip-space Y convention.

`AOCommon.glsl` owns that conversion with `AOTextureUVFromClipXY` and `AOClipXYFromTextureUV`. Do not hand-roll `clipXY * 0.5 + 0.5` in individual AO passes or resolve shaders. Vulkan and OpenGL differ in clip-space orientation through `ClipSpaceYDirection`; skipping the helper makes AO appear vertically flipped at the deferred light-combine point.

Use deferred debug view `10` to validate the final AO sampling orientation against raw albedo or depth before changing individual AO kernels.

---

## 20. Temporal AA: Static Edge Rejection and Debug Views

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
- The Temporal AA controls only affect the live output when the active camera's effective AA mode is `TAA` or `TSR`; if the camera is using `None`, `MSAA`, `FXAA`, `SMAA`, or `DLAA`, the temporal resolve settings are intentionally inert. `DLAA` still uses temporal jitter and motion/depth inputs, but the resolve is owned by NVIDIA DLSS/Streamline rather than the engine's temporal accumulation shader.

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

The prepass follows the resolved mesh submission strategy, not merely the requested GPU-dispatch boolean. If the effective lit path is `CpuDirect`, the depth-normal prepass is also CPU, which keeps AO/depth and color coverage from diverging when a forced strategy or backend profile downgrades GPU dispatch. If the effective lit path is `GpuMeshlet*`, the prepass uses the corresponding traditional GPU indirect strategy for now because the direct meshlet material-table shader cannot consume override/depth-normal material variants.

The lit `OpaqueForward` and `MaskedForward` GPU passes must not use the current `DepthView` as a command-level Hi-Z occlusion source while this prepass is enabled. That depth can already contain the same forward candidates; using it for occlusion can reject whole commands before meshlet expansion, leaving AO/depth silhouettes without matching color. Those passes keep frustum/BVH results and allow meshlet task-shader frustum/cone culling, but current-depth Hi-Z refine is skipped for color parity.

The scene BVH feeding those passes uses the compact, root-down contract in
[GPU Scene BVH](gpu-scene-bvh.md). Its hierarchy is reused across views; flat
GPU frustum culling remains the explicit small-workload and unavailable-resource
fallback. Queue or ray-stack pressure is conservative and observable, never a
reason to hide geometry.

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

---

## 23. Texture Runtime Diagnostics And Upload Budgeting

**Rule:** Runtime texture residency, OpenGL upload safety, and texture/shadow render-work contention should be diagnosable from `log_textures.log`.

File logging creates `log_textures.log` beside the existing per-session logs under `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`. Category logs use the same `.log` extension and default color metadata as the editor bootstrap/console diagnostics. The texture log receives residency, cache, fallback, and upload events such as `Texture.CacheHit`, `Texture.CacheMiss`, `Texture.CacheStale`, `Texture.CacheFallbackToSource`, `Texture.CacheWrite`, `Texture.CacheRead`, `Texture.ImportPreviewQueued`, `Texture.TransitionQueued`, `Texture.TransitionCoalesced`, `Texture.TransitionCanceled`, `Texture.UploadChunk`, `Texture.UploadSlow`, `Texture.FallbackBound`, `Texture.UploadValidationFailed`, `Texture.VramPressure`, and `Texture.VramSummary`.

The rendering settings expose `TextureLogMode` with `Disabled`, `Summary`, `SlowOnly`, and `Verbose` modes. Summary mode is the normal diagnostic default; verbose mode is for short repro captures only. Slow thresholds are configurable for CPU decode/resize, mip build, upload chunks, full transitions, queue wait, and per-frame texture upload budget.

CPU-side imported texture preparation is logged to `log_textures.log` as `Texture.UploadSlow` with `backend=CPU` whenever the decode, clone, resize, mip-build, or total resident-build time crosses its slow threshold. These events include `decodeMs`, `cloneMs`, `resizeMs`, `mipBuildMs`, `totalMs`, and `totalThresholdMs` so raw source decode stalls can be separated from resize/mip generation stalls.

Fresh third-party texture caches are the preferred streaming authority after the first source import. Texture cache paths include a texture-streaming payload variant key for the current cooked RGBA8/uncompressed residency payload, so future payload changes do not silently reuse old cache files. Warm-cache streaming uses `AssetTextureStreamingSource` and records `Texture.CacheRead` or `Texture.CacheReadSlow` with `cacheReadMs`, `cacheParseMs`, `totalMs`, cache path, original source path, requested residency, mip count, and whether the streamable cooked payload was used.

Texture transition timing now separates `queueWaitMs`, `activeUploadMs`, and `lifecycleMs`. `lifecycleMs` remains the user-visible latency from queue to completion, while `activeUploadMs` tracks actual upload work where the backend can measure it. `Texture.VramSummary` includes stage counters for visible textures without previews, no data, preview queued/resident, promotion queued/promoted, fallback binds, cancellations, and failures.

OpenGL `TexSubImage2D` upload paths must validate allocated storage, mip level, upload rectangle, sparse state, and storage generation before touching the driver. Validation failures are mirrored into the texture log and still emit OpenGL context so `log_opengl.log` and `log_textures.log` can be correlated.

Runtime-managed imported-texture promotions are chunked for CPU-pointer mips where the format allows row uploads. Partial rows stay hidden by clamping the sampled mip range until the mip is complete. Pending transitions carry queue timing and are coalesced when the target residency/page selection is unchanged.

During active imports, textures bound by material uniform setup can act as a fallback priority source before a visibility snapshot exists. Related textures on the same material get a small shared residency floor so albedo, normal, and roughness detail tiers do not diverge obviously during startup.

Visible preview loads are prioritized ahead of high-res promotions. Non-critical promotions are delayed while any visible or recently-bound texture lacks a resident preview. Superseded resident data is kept in a short-lived reuse cache keyed by authority path, source timestamp/length, requested resident dimension, mip-chain flag, and the current cooked payload format so canceled sparse/tiered transitions can reuse CPU-prepared mips instead of decoding the same source repeatedly.

Preview paths that cannot provide their intended texture must bind an explicit role-aware fallback instead of leaving shader samplers unbound. Light probe preview materials bind a visible fallback for `Texture0` and log `Texture.FallbackBound` once for that missing-preview path.

Texture uploads and shadow atlas tile rendering publish queue and budget counters through the shared render-work budget coordinator. Profiler FPS-drop and render-stall logs include those counters, and the shadow atlas can defer lower-priority tiles when urgent visible texture repair is pending. Startup boost is bounded by frame/time limits so it cannot turn into multi-second starvation.

The ImGui Texture Streaming panel shows tracked textures with backend, committed bytes, priority, queue wait, last upload duration, visibility, pending state, pressure-demotion state, and validation-failure state. Use **Dump Summary** from that panel to force an immediate `Texture.VramSummary` event.

---

## 24. Tonemapping Operators

**Rule:** Tonemapping options exposed by `TonemappingSettings.Tonemapping` must stay aligned with `Build/CommonAssets/Shaders/Snippets/ToneMapping.glsl`.

The Default Render Pipeline tonemapping stage now exposes AgX and GT7 in addition to the earlier Linear, Gamma, Clip, Reinhard, Hable, Mobius, ACES, Neutral, and Filmic operators. `PostProcess.fs`, `PostProcessStereo.fs`, and `TonemapStandalone.fs` all route through the shared snippet, so new operators should be added there first and then covered by `TonemappingShaderContractTests`.

`GT7` is the lightweight Uchimura/Gran Turismo per-channel curve intended for the engine's SDR post-process path. It is not the full GT7 physical HDR/display pipeline with target-display adaptation and perceptual gamut mapping.

---

## 25. ImGui Platform Viewport Disposal

**Rule:** Runtime ImGui platform viewport close must not call native window disposal from the viewport disposal pump.

Closing a detached ImGui viewport hides and detaches the Silk/GLFW window, releases the managed ImGui viewport handle, and keeps the native window quarantined for process shutdown. This avoids freezes observed inside native `IWindow.Dispose()` when ImGui retires a platform viewport during the frame. Set `XRE_IMGUI_VIEWPORT_DISPOSE_NATIVE=1` only for local diagnostics when testing the underlying Silk/GLFW close path.

---

## 26. Debug Visualization Post-Process Stage

**Rule:** The camera post-process stage key remains `gpuBvhDebug`, but the editor label is `Debug Visualization`.

The stage owns broad render-pipeline diagnostics, not only GPU BVH wireframes. It currently includes:

- `Full Overdraw`: renders all visible mesh render passes into `FullOverdrawCountTex` using additive `1.0` writes with depth disabled, then presents `FullOverdrawDebugFBO` as a heatmap over `PostProcessOutputTexture`. It resolves the same mesh submission strategy as the main pass: `CpuDirect` redraws the CPU mesh list, while active GPU submission draws GPU-eligible meshes through the GPU render path and leaves forced-CPU / `ExcludeFromGpuIndirect` meshes on the CPU fallback path.
- `GPU BVH`: the existing zero-readback BVH wireframe controls.
- `Meshlet Debug Display`: requests meshlet debug colors through production meshlet dispatch when available, or through the diagnostic direct-dispatch overlay on OpenGL NV mesh-shader hardware. Keep `VPRC_RenderMeshletDebugDisplay` wired in both default pipelines so the inspector toggle is visible on the active pipeline as well as V2.

Keep the full-overdraw pass mono-only (`!Stereo`) and after post-process resource caching but before final output. The count pass is intentionally debug-only mesh submission with a forced simple material, so it should not be used for performance numbers except to locate screen regions with repeated pixel coverage.

---

## 27. DLAA And MFAA AA Modes

`DLAA` is a first-class AA mode in the default render pipeline. It requests the existing NVIDIA DLSS vendor path at native internal resolution, bypasses the in-engine FXAA/SMAA/TSR post-AA chain, and fails visibly when the DLSS runtime or compatible NVIDIA path is unavailable.

`MFAA` is not exposed as a selectable engine AA mode. NVIDIA MFAA is a driver-side enhancement for MSAA rather than an OpenGL/Vulkan render-pipeline pass the engine can require. Use `MSAA` in XRENGINE and enable MFAA in the NVIDIA driver when testing that path; do not label plain MSAA as MFAA in engine diagnostics.

---

## 28. Editor Preview GPU Handles

**Rule:** Editor preview and debug UI code must resolve renderer-specific texture
handles through the render-thread invocation service.

Texture/material/model/light/scene-panel previews may still branch on OpenGL
versus Vulkan because ImGui needs backend-specific texture handles. The
GPU-affine work inside those branches must use `EditorRenderThread.Invoke(...)`,
which forwards to `IRuntimeRenderingHostServices.InvokeRenderThreadTask<T>`.
That includes Vulkan `RegisterImGuiTexture(...)` calls and OpenGL
`GetOrCreateAPIRenderObject(...)` / `GenericToAPI<T>(...)` wrapper creation.

Pure inspection of an already-resolved wrapper may stay local when the caller is
already on the render thread, but new preview/readback helpers should keep the
renderer mutation/readback setup behind the same service boundary.

---

## 29. OpenXR Stereo Temporal Isolation

**Rule:** OpenXR stereo features must either use a proven per-eye/layer resource
model or be disabled with an explicit policy diagnostic. Do not hide an unsafe
headset path behind a mono or CPU fallback.

Current policy:

- `EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain` keeps TAA/TSR and
  other history-based effects disabled for OpenXR external per-eye swapchain
  targets.
- `EVrTemporalHistoryPolicy.StereoArrayLayer` is the only OpenXR VR policy that
  may use history-based temporal resources, and only for resources/shaders that
  are stereo arrays.
- Auto exposure uses `EVrAutoExposurePolicy.HeadsetShared`: stereo-array HDR
  sources are averaged across both eye layers into one shared exposure value.
  Per-eye external swapchain sources skip auto exposure to avoid last-eye-wins
  mutation of the shared 1x1 exposure texture.
- Atmosphere and volumetric-fog temporal history stay mono-only until their
  half-resolution color/depth/history resources and reprojection/upscale shaders
  are stereo-array aware.
- Vendor upscalers and frame generation are unsupported in headset stereo until
  their sessions, history-valid flags, color/depth/motion/exposure inputs, and
  outputs are isolated per eye or per stereo layer. Explicit DLSS/DLAA/XeSS
  requests in VR must fail loudly; fallback blit is allowed when no vendor path
  was requested.

Resource-generation diagnostics must include stereo state, reserved view count,
feature mask, HDR, AA, MSAA, and dimensions so mode toggles explain whether the
pipeline rebuilt for mono, per-eye external swapchain, or true stereo-array
rendering.

---

## 30. Offscreen Capture Policy

**Rule:** Scene captures must apply a `RenderCapturePolicy` to their viewport.
Do not toggle `UseDirectFboTargetCommandsWhenRenderingToFbo` ad hoc from capture
components.

`RenderCapturePolicy` defines presets for generic scene captures, light probes,
reflection probes, GI probes, thumbnail/UI previews, and diagnostic FBO
captures. `XRViewport.ApplyCapturePolicy(...)` applies the viewport and camera
invariants and emits a `[CapturePolicy]` diagnostic containing the effective
passes, post-process exclusions, backend, clip-space direction, depth range,
and framebuffer-texture Y direction.

Capture is a variant of the existing default pipelines rather than a separate
`RenderPipeline` type. Both `DefaultRenderPipeline` and
`DefaultRenderPipeline2` route minimal policies through the same caller-owned
FBO command shape: optional pre-render hooks, background, policy-selected
deferred/forward/masked/transparent geometry, optional on-top/debug/UI work,
and post-render hooks. The branch does not execute temporal history, auto
exposure, bloom, TAA/TSR, vendor upscaling, or viewport final output.

`DefaultRenderPipeline` reserves the `MinimalDirectCapture` resource-profile
bit for this path. When set, `DescribeResources(...)` returns before declaring
the viewport G-buffer, post-process, temporal, bloom, exposure, AA, or final
output resources. The caller-owned color/depth attachments and mesh-command
resources are sufficient for the direct branch. `DefaultRenderPipeline2`
already allocates its legacy command resources lazily by selected branch, so
selecting its direct branch provides the same capture behavior.

Capture textures retain backend framebuffer-native row orientation and do not
receive a capture-specific vertical flip. OpenGL and Vulkan use the engine's
configured clip-space policy; shaders sampling framebuffer textures must use
`RenderClipSpacePolicy.FramebufferTextureYDirection(...)`. Screenshot/readback
row flipping is a separate CPU export policy.

Light-probe cubemap faces use `RenderCapturePolicy.LightProbe`. Cubemap mip
generation and cubemap-to-octahedral, irradiance, and prefilter work happen only
after face capture through explicit `XRQuadFrameBuffer` fullscreen passes; they
are not part of the scene-capture command chain.
