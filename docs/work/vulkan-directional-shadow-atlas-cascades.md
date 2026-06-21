# Vulkan Directional Shadow Atlas Cascades

## Problem

With Vulkan, directional light cascade render modes have been narrowed through a
mode-specific failure matrix. Sequential and geometry-shader cascaded rendering are
stable with the directional atlas enabled, and non-atlas layered cascade paths were
fixed by honoring texture-array framebuffer layers. A remaining order-specific issue
was reported after those fixes: if the directional atlas is enabled while cascades
are on, then cascades are disabled, no directional shadow map renders.

## Initial Hypothesis

The first failure was in the atlas request/allocation/published-uniform state machine
for directional lights. The remaining failures are Vulkan-specific layered rendering
contract issues:

- Non-atlas layered cascades write `gl_Layer`, but Vulkan FBO creation and dynamic
  rendering began with one framebuffer layer even when the attached target was a
  whole texture array.
- Instanced atlas cascades depend on `CascadeLayerCount` and
  `CascadeViewProjectionMatrices` uniforms. Vulkan queues mesh draws and records
  command buffers later, after the live shadow-pass state may have been popped, so
  instanced draws could reuse stale or missing shadow uniforms.

## Evidence

- User-reported latest matrix: atlas sequential and atlas geometry are stable; atlas
  instanced layered flickers; atlas disabled only sequential cascades work; cascades
  off is stable with atlas on or off.
- Latest log session did not show an obvious shadow/atlas validation error.
- Code inspection found two mismatches:
  - Forward directional binding discarded the primary legacy shadow texture whenever
    directional atlas was requested, even if the atlas tile was not sampleable yet.
  - Deferred directional atlas binding treated any resident cascade tile as sufficient,
    so one sampleable cascade could enable atlas sampling for all cascades.
- Vulkan code inspection found two additional mismatches:
  - `VkFrameBuffer` and FBO dynamic rendering hard-coded one layer for texture-array
    framebuffer attachments.
  - Deferred Vulkan mesh draw recording applied shadow uniforms from live render state
    instead of the state that was active when the draw was queued.
- Follow-up inspection for the atlas-on, cascades-off transition found that
  `ClearCascadeShadows()` also cleared the primary directional atlas slot. Cascade
  cleanup runs whenever cascades are disabled, but the primary atlas slot belongs to
  the non-cascaded directional shadow path and must not be retired by cascade-only
  state cleanup.
- After that slot-lifetime fix, the remaining latest run showed Vulkan validation
  errors when binding the non-cascaded primary directional shadow FBO:
  `DirectionalShadow.*.PrimaryRasterDepth` was `VK_FORMAT_X8_D24_UNORM_PACK32`, but
  `TransitionFboAttachmentsForDynamicRendering()` transitioned it with
  `VK_IMAGE_ASPECT_COLOR_BIT` and `VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL`.
  The primary directional shadow material lists raster depth before the fallback
  color texture, while `VkFrameBuffer.BuildAttachmentInfos()` correctly reorders
  Vulkan attachments to color first and depth last. Dynamic-rendering barriers and
  layout tracking were still indexing `XRFrameBuffer.Targets` in original source
  order, so signature slot 0 (color) was applied to target slot 0 (depth).

## Fixes Applied

- Legacy fallback decisions now verify the directional atlas page array exists and
  that each required slot points at a valid page before suppressing legacy shadow
  rendering.
- Forward lighting keeps primary legacy directional textures bound as fallback even
  when atlas is requested.
- Deferred lighting requires all active cascade atlas slots to be sampleable before
  enabling the directional atlas cascade path.
- Source-level regression coverage now locks these fallback rules.
- Vulkan framebuffers now derive `FramebufferLayers` from attachment views and pass
  that layer count to `vkCreateFramebuffer`, dynamic FBO `vkCmdBeginRendering`, and
  explicit FBO clears.
- Vulkan pending mesh draws now snapshot layered shadow uniform state at enqueue time
  and reuse that snapshot when recording command buffers.
- Directional atlas slot cleanup is now split between all-atlas-frame cleanup and
  cascade-only cleanup. Atlas diagnostics still clear primary and cascade slots
  before publishing a new atlas frame, while `ClearCascadeShadows()` only clears
  cascade bounds/slices/cascade slots so disabling cascades does not erase the
  primary atlas slot.
- Vulkan framebuffers now keep ordered attachment target metadata alongside ordered
  signatures and image views. Dynamic-rendering FBO barriers, live layout queries,
  and physical-image layout tracking use that ordered metadata instead of the
  original `XRFrameBuffer.Targets` array, so mixed color/depth FBOs remain coherent
  even when the source material lists depth before color.

## Validation

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.DirectionalShadowAtlasFallbackTests"`:
  passed 6/6 after adding the cascade-disable/primary-atlas-slot regression.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.DirectionalShadowAtlasFallbackTests"`:
  passed 7/7 after adding the ordered Vulkan FBO target regression. The
  `BuildProjectReferences=false` form was used because the editor was running and
  locking `Build\Editor` binaries.
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`:
  passed with 0 warnings and 0 errors after the Vulkan layered and ordered-FBO
  target fixes.
- A broader run of `CascadedShadowDefaultsAndForwardShaderTests` is not a useful
  signal right now; it still has unrelated stale shader/source expectations and a
  runtime-host setup failure outside this atlas fallback patch.

## 2026-06-18 Follow-Up: Atlas Still Not Rendering

Latest user report: cascades disabled after atlas use still produced no visible
directional shadows, and GTAO showed a dark floor plus screen-edge halos.

Latest log session:

`Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-18_20-31-11_pid34244`

New evidence:

- `log_lighting.log` repeatedly reported:
  `Grouped directional cascade render failed for 'TestDirectionalLightNode', cascades=4, page=0; leaving atlas tiles stale instead of falling back to sequential.`
- The same frame summaries showed `Deferred 4 shadow request(s) after rendering 0/16 tile(s)`,
  so the user-facing failure was stale or empty atlas tiles, not a sampling-only
  issue.
- `log_vulkan.log` no longer showed the previous color/depth aspect validation
  failure for `PrimaryRasterDepth`; only known HDR mip/autoe exposure planner
  warnings remained.
- `log_rendering.log` also reported descriptor parity mismatches for `GBufferFBO`:
  the declared resource is internal-resolution `AmbientOcclusionTexture`, while
  GTAO registered its runtime output FBO with an absolute current-pixel size after
  resize.

Fixes applied in this pass:

- `ShadowAtlasManager.TryRenderDirectionalCascadeGroup()` now attempts grouped
  atlas rendering first, then immediately renders each cascade into its allocated
  atlas tile with `RenderCascadeShadowAtlasTile()` if the grouped path fails.
  Successful fallback marks the group tiles rendered and records the frame as a
  directional sequential fallback instead of leaving the atlas stale.
- The grouped-failure warning now only says tiles are stale when sequential fallback
  also fails.
- GTAO generation now uses `AOViewPosFromDepth()` for center, normal fallback, and
  forward/backward samples in mono and stereo shaders so it follows the same AO
  clip/depth convention as the other AO passes.
- GTAO blur now skips taps that leave the framebuffer instead of clamping them to
  the edge texel. The previous clamp repeated edge AO values across the denoise
  kernel and created visible screen-edge halos.
- GTAO's final dynamic output FBO is registered with an internal-resolution
  descriptor, matching the declared `GBufferFBO` resource shape and removing the
  resize descriptor mismatch.

GTAO floor-darkness diagnosis:

- The previous GTAO tuning was intrinsically strong for a broad flat floor:
  radius `4.052`, power `2.503`, denoise radius `8`, and visibility-bitmask
  thickness `1.5002`.
- Deferred combine applies `pow(rawAO, AmbientOcclusionPower)`, so a moderate raw
  AO value becomes much darker after the default exponent. For example, a raw AO
  value around `0.6` becomes roughly `0.28`.
- The current defaults have been reduced to radius `2.2`, power `1.35`, bias
  `0.06`, denoise radius `5`, denoise sharpness `10.0`, and bitmask thickness
  `0.12`.
- GTAO generation now fades raw visibility back toward unoccluded at screen
  borders, proportional to the projected radius, so missing off-screen samples do
  not darken the edge of the frame.

Remaining directional-shadow evidence after the user's latest "no luck" report:

- The newest available logs show successful directional atlas requests and grouped
  cascade fallback attempts, but they do not include a clean post-toggle
  cascades-off frame summary with `requests=1`.
- No steady-state Vulkan validation error currently identifies the missing
  receiver shadow. The remaining failure is likely in published receiver state,
  stale atlas sampleability, or the primary non-cascaded receiver texture path,
  rather than in the old grouped-cascade render failure.

Follow-up validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`:
  passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.DirectionalShadowAtlasFallbackTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionShaderUvConventionTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionGtaoDefaultsTests"`:
  passed 40/40.
- `git diff --check`: passed; Git only reported CRLF normalization warnings.

## 2026-06-18 Follow-Up: Deferred Fallback And Weighted GTAO Sectors

Latest user report: the previous pass still did not restore visible shadows after
the atlas-on then cascades-off sequence, and GTAO still showed the floor too dark
with edge halos.

Additional shadow finding:

- `VPRC_LightCombinePass` only bound the primary `ShadowMap` sampler when the
  directional atlas path was not requested. That left the deferred shader no real
  primary fallback even if the light's atlas record was invalid or stale.
- `DeferredLightingDir.fs` also returned contact-only shadowing from
  `ReadShadowMap2D()` and `ReadCascadeShadowMap()` whenever
  `DirectionalShadowAtlasEnabled` was true but the per-light atlas metadata did
  not pass validation. In the cascades-off-after-atlas transition, that means a
  stale atlas state could make receiver shadows disappear even after the primary
  shadow map was rendered.

Shadow fixes applied:

- Deferred light combine now keeps the real primary `ShadowMap` sampler bound
  whenever a primary `XRTexture2D` exists, even while directional atlas sampling
  is requested.
- The deferred directional lighting shader now falls through to the legacy
  `ShadowMap` or `ShadowMapArray` path when a directional atlas record is
  invalid instead of returning contact-only visibility.

GTAO findings:

- The earlier bitmask tuning reduced angular thickness, but sector coverage was
  still binary after a sample touched a sector. Weak, far, or same-plane floor
  samples could therefore consume full occlusion sectors and darken the floor
  more than the classic horizon path.
- The raw gather edge fade scaled up to a wide band, and the denoise edge fade
  could be even wider. That made the anti-halo fade itself visible at the sides
  of the frame.

GTAO fixes applied:

- Visibility-bitmask gather now accumulates newly covered sectors weighted by the
  sample falloff instead of deriving visibility from only
  `bitCount(occludedSectors)`.
- Mono and stereo raw GTAO edge fades are capped to a narrow 2-8 pixel band.
- Mono and stereo denoise edge fades are capped to a narrow 1-4 pixel band.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`:
  passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.DirectionalShadowAtlasFallbackTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionShaderUvConventionTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionGtaoDefaultsTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionVisibilityBitmaskTests"`:
  passed 50/50.
- `git diff --check`: passed; Git only reported CRLF normalization warnings.

Notes:

- A broad `CascadedShadowDefaultsAndForwardShaderTests` run is still not clean
  because it contains stale forward-shader source expectations outside the
  deferred atlas fallback change.
- A failed concurrent test attempt hit native bridge path-file locks under
  `obj\`. The sequential narrow test above is the useful signal.

## 2026-06-18 Follow-Up: Surface-Detail Normal Root Cause

Latest user report: both symptoms were still visible, and the floor, pillars,
and lion looked like their normals were fundamentally wrong.

New evidence:

- Latest `log_meshes.log` showed the affected Sponza materials importing
  `*_bump.png` files as `TextureType.Height`, for example `lion_bump.png`,
  `sponza_column_a_bump.png`, `spnza_bricks_a_bump.png`, and
  `vase_round_bump.png`.
- Those materials were routed to `TexturedNormal*Deferred` shaders. If a height
  map is decoded as an RGB tangent-space normal map, grayscale bump texels become
  near-horizontal or inward-facing tangent normals. That explains the bad floor,
  pillar, and lion lighting, and it also gives GTAO and shadow bias the wrong
  receiver normals.
- The importer had enough information to know these were height maps, but the
  deferred material path relied on runtime `NormalMapMode = 1`.
- A first fix to create `XRENGINE_HEIGHTMAP_MODE` shader variants exposed another
  issue: `XRMaterial.NormalizeTransparencyShaders()` replaced already-deferred
  fragment shaders with the canonical deferred shader during transparency-mode
  resolution, stripping the import-time height-map define.

Fixes applied:

- `ModelImporter` now selects a `XRENGINE_HEIGHTMAP_MODE` shader variant for
  imported deferred and non-Uber forward materials when their surface-detail slot
  is a known height/bump map.
- `XRMaterial.NormalizeTransparencyShaders()` now leaves existing deferred
  shaders untouched when the material remains in the deferred base path, so
  non-transparency shader variants such as height-map mode survive transparency
  normalization.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=MakeMaterialDeferred_HeightTexturesUseCompileTimeHeightMapMode|Name=MakeMaterialDeferred_NormalCameraTexturesCountAsNormalMaps"`:
  passed 2/2.
- A broader `ImportedDeferredMaterialTests` run still has unrelated pre-existing
  Uber-material failures under the dummy test shader service; the targeted
  deferred height-map contracts pass.
- `git diff --check`: passed; Git only reported CRLF normalization warnings.

## 2026-06-18 Follow-Up: GTAO Camera-Relative Plane Bias

Latest user report: rotating Sponza 90 degrees made GTAO look bad on the same
up/down planes relative to the camera, rather than on the original floor mesh.

Finding:

- The G-buffer octahedral normal encode/decode path is not the likely root
  cause: GTAO decodes the normal and transforms it from world space into view
  space, matching the other AO generators.
- GTAO's slice samples are taken in framebuffer texture-UV directions, but the
  analytic slice tangent was built as if those directions were already clip
  directions. Under Vulkan's negative-height viewport policy, framebuffer
  texture Y is opposite clip Y. That makes vertical slices use the wrong
  handedness, so the artifact follows camera-relative up/down planes.
- The depth-derived fallback normal had the same texture-Y assumption, although
  input G-buffer normals are the default path.

Fixes applied:

- `AOCommon.glsl` now exposes `AOClipDirectionFromTextureDirection()`.
- Mono and stereo GTAO use that helper when deriving the view-space slice
  tangent from a texture-space sample direction.
- Mono and stereo GTAO also flip the depth-derived fallback Y derivative when
  framebuffer texture Y is inverted.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionShaderUvConventionTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionGtaoDefaultsTests|FullyQualifiedName~XREngine.UnitTests.Rendering.AmbientOcclusionVisibilityBitmaskTests"`:
  passed 40/40.
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -p:BuildProjectReferences=false`:
  passed with 0 warnings and 0 errors.
- A first test run without `BuildProjectReferences=false` was blocked by the
  currently running editor/debug adapter holding `Build\Editor\...\XREngine.dll`;
  the rerun above avoided the editor output copy.
