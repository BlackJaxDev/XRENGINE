# OpenXR Vulkan Single-Pass Stereo Investigation - 2026-06-30

## Problem

OpenXR + Monado + Vulkan single-pass stereo was rendering black eye output. Earlier runs also showed only the stereo pre-render clear in the GPU profiler, shader compile exceptions, invalid Vulkan image-view validation, and intermittent device-loss fallout.

## Findings

- The true single-pass stereo command path now records and submits real render work. The final validated run reports `active_stereo_mode=openxr-true-single-pass-stereo` and `vr_view_render_implementation_path=TrueSinglePassStereo`.
- Vulkan rejected layered color/depth image views when a logical 2D texture was backed by two array layers. A `VK_IMAGE_VIEW_TYPE_2D` view with `layerCount=2` is invalid; these views must be promoted to `VK_IMAGE_VIEW_TYPE_2D_ARRAY`.
- Generated stereo vertex shaders emitted `layout(num_views = 2) in;`, which is the OpenGL/OVR multiview declaration. Vulkan multiview gets view count from the render pass/dynamic rendering view mask, so Vulkan GLSL should strip that declaration and keep `gl_ViewIndex`/`GL_EXT_multiview`.
- The active local unit-testing settings can start directly in single-pass stereo through `VR.ViewRenderMode`.
- Follow-up albedo-only symptom: true stereo routed `XRViewport.RenderStereo(...)` through a non-null engine-owned stereo staging FBO. The default pipeline's top-level command selector treated any non-null output FBO as the simplified FBO-target path, so it skipped the viewport command chain that runs AO, deferred light combine, bloom, temporal, post-process, and final output. Mesh passes still ran, which is why deferred and forward geometry showed mostly raw albedo.

## Changes

- `VkImageBackedTexture` and `VkTextureView` now normalize descriptor/image view types by layer count so multilayer 1D/2D images use array views.
- `VulkanShaderCompiler` now strips `layout(num_views = ...) in;` from Vulkan GLSL after both initial normalization and final rewrite, and still injects `GL_EXT_multiview` when `gl_ViewIndex` remains.
- Added a source-contract regression test covering Vulkan single-pass stereo shader and layered view wiring.
- Added `VR.ViewRenderMode: SinglePassStereo` to the local ignored unit-testing settings.
- Routed stereo output-FBO renders through the full viewport command chain in `DefaultRenderPipeline` and `DefaultRenderPipeline2`. Offscreen/simple FBO renders still use the FBO-target branch when they are not stereo passes.
- Added a regression contract proving stereo FBO renders depend on `RuntimeEngine.Rendering.State.IsStereoPass` instead of silently selecting the simplified FBO path.

## Validation

Final run session:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_14-51-15_pid7840`

Key evidence:

- `required extension not requested: 0`
- `Shader compile diagnostics: 0`
- `ShaderCompileFailed: 0`
- `VK_IMAGE_VIEW_TYPE_2D: 0`
- `layerCount (2): 0`
- `VK_ERROR: 0`
- `VUID: 0`
- `[ERROR]: 0`
- `[EXCEPTION]: 0`
- `NoRenderingCommands: 0`
- `True SinglePassStereo failed: 0`
- `Vulkan true stereo render completed: 35`
- `Vulkan stereo blit: 70`

Profiler samples from the same run show `openxr-true-single-pass-stereo`, `TrueSinglePassStereo`, nonzero mesh draws, and four Vulkan blits/swapchain writes per sampled frame.

Targeted validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests" -v:minimal`

Both passed. The test run still reports existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories.

Follow-up validation for the albedo-only parity fix:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests" -v:minimal`
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal`
- `.\Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 60 -TimeoutSeconds 90 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight` with `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`

Both passed. The targeted runtime rendering build completed with `0 Warning(s)` and `0 Error(s)`; the focused test run still reports the pre-existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories.

Smoke evidence:

- `Build/_AgentValidation/20260630-180054-openxr-monado-smoke/reports/openxr-smoke-summary.normalized.json`
- `viewRenderImplementationPath=TrueSinglePassStereo`
- `viewRenderTemporalHistoryPolicy=StereoArrayLayer`
- `submittedFrameCount=58`
- `endFrameFailureCount=0`
- `warnings=[]`
- `failures=[]`

The referenced engine log session was `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_18-00-55_pid2292`. Its Vulkan log shows the full stereo viewport path compiling/recording stereo AO (`GTAOGenStereo.fs`, `GTAOBlurStereo.fs`), deferred light combine (`DeferredLightCombineStereo.fs`), and bloom (`BloomCopyStereo.fs`, `BloomDownsampleStereo.fs`, `BloomUpsampleStereo.fs`) with two-layer stereo resources and dynamic-rendering `viewMask=0x3`.

## 2026-06-30 Magenta / Direct-Lighting Follow-up

After the full stereo viewport command chain was restored, Monado single-pass stereo regressed to a magenta-looking final image with only faint deferred scene content. FBO captures showed the G-buffer and AO were valid, but `LightingAccumTexture` stayed cleared/black, so the issue had moved from command-chain selection to the deferred direct-light pass itself.

Finding:

- Stereo deferred resources were correctly allocated as two-layer images and bound through array texture views.
- Directional light uniforms and stereo shader rewrites were valid.
- The `LightingAccumFBO` pass opened with dynamic rendering `viewMask=0x3`, but the fullscreen directional light path was using the normal stereo mesh-version selector. Fullscreen lighting is screen-space work like post-process blits; in a multiview render pass it must keep `FullscreenTri.vs` and let the render pass broadcast the primitive to both view layers.

Fix:

- Directional deferred lights now render with `forceNoStereo: true`. Point and spot light volumes still use the normal stereo path because they need their actual light-volume vertex transforms.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal`: passed after cleanup. Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain.
- Debug light smoke: `XRE_DEFERRED_DEBUG=7`, `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`, `Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 105 -TimeoutSeconds 180 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight`.
  - Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_19-34-47_pid37712`.
  - `DefaultPipeline_05_LightingAccum.png` changed from the previous black 417-byte output to a written scene-shaped debug target.
- Normal smoke with the same single-pass stereo mode and FBO captures:
  - Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_19-36-02_pid38972`.
  - `DefaultPipeline_05_LightingAccum.png` captured a lit scene with shadows/material response.
  - `DefaultPipeline_08_BloomMip0.png`, `DefaultPipeline_12_PostProcessOutput.png`, `DefaultPipeline_13_FinalPostProcessOutput.png`, and `DefaultPipeline_14_TsrOutput.png` were non-empty lit outputs rather than magenta/black fallbacks.
- Final current-code smoke after removing temporary diagnostics:
  - Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_19-39-52_pid27604`.
  - `LightingAccum`, `ForwardPass`, `BloomMip0`, `PostProcessOutput`, `FinalPostProcessOutput`, and `TsrOutput` captures remained non-empty lit outputs.

## 2026-06-30 Upside-Down Publish Follow-up

After direct lighting/post-processing parity was restored, Monado's single-pass stereo preview was vertically inverted. The default pipeline captures remained coherent, which pointed at the final publish boundary rather than the stereo render pass itself.

Finding:

- True single-pass stereo renders into `OpenXRVulkanStereoColorArray`, then blits layer 0 and layer 1 into the acquired OpenXR swapchain images.
- The layer-to-swapchain blit was preserving destination Y ordering. For this Vulkan/OpenXR publish path, the compositor-facing swapchain image needs the destination Y range inverted.
- The later preview/mirror copy reads from the already-published swapchain image, so correcting the layer publish also keeps the desktop preview aligned with the submitted eye image.

Fix:

- `TryBlitTextureArrayLayerToOpenXrSwapchainImage` now accepts an optional `flipY` flag and reverses `DstOffsets` when requested.
- The true single-pass stereo left/right publish calls pass `flipY: true`. Other OpenXR copy paths keep their existing orientation defaults.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -c Debug -v:minimal`: passed. Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain.
- Current-code Monado smoke used a cloned editor output because a live editor process had the normal `Build/Editor/Debug/...` output locked. The cloned output was under `Build/Editor/AgentSmoke20260630_1956/...` with the freshly built `XREngine.Runtime.Rendering.dll`.
- Smoke command: `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_VULKAN_CAPTURE_EYE_OUTPUTS=1`, `Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 75 -TimeoutSeconds 150 -Configuration AgentSmoke20260630_1956 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight`.
- Summary: `Build/_AgentValidation/20260630-195526-openxr-stereo-upside-down/reports/openxr-smoke-summary.json`.
  - `rendererBackend=Vulkan`
  - `viewRenderModeRequested=SinglePassStereo`
  - `viewRenderImplementationPath=TrueSinglePassStereo`
  - `submittedFrameCount=73`
  - `perEyeAcquireCounts=[73,73]`
  - `perEyeReleaseCounts=[73,73]`
  - `warnings=[]`
  - `failures=[]`
- Engine log session: `Build/Logs/AgentSmoke20260630_1956_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_19-59-30_pid34136`.
  - `log_rendering.log` repeatedly reports `ViewRenderMode requested=SinglePassStereo effective=SinglePassStereo ... path=TrueSinglePassStereo`.
  - `log_vulkan.log` repeatedly reports stereo layer blits from `OpenXRVulkanStereoColorArray` layer 0/2 and layer 1/2 into `true stereo left/right eye swapchain image`.
  - FBO captures for `AmbientOcclusion`, `LightingAccum`, `ForwardPass`, `BloomMip0`, `PostProcessOutput`, `FinalPostProcessOutput`, and `TsrOutput` were produced during the smoke.

Known unrelated noise in that run:

- Vulkan SDK validation reports unsupported/unknown newer pNext structures against the installed 1.3.239 validation layer.
- Resource-planner warnings still report optional/disabled framebuffer names for some passes.
- VR headset-shared auto exposure logs that external per-eye swapchain targets are skipped. The smoke summary had no OpenXR failures.

## 2026-07-01 Crash Follow-up for Last Run

Last failing session:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_21-57-09_pid36788`

Findings:

- `log_rendering.log` repeatedly reported `NullReferenceException` in `VPRC_CacheOrCreateFBO[MsaaLightCombineFBO]` during AA/profile changes. The failing path was `DefaultRenderPipeline.DescribeTexture` from `CreateMsaaLightCombineFBO`, which was assuming all sampled MSAA textures were already concrete and non-null while the profile change was rebuilding resources.
- `log_vulkan.log` repeatedly reported `VUID-vkCmdClearAttachments-pRects-06937` from the `OpenXR mirror primary command buffer variant`. The active target was `OpenXRVulkanStereoFBO` with dynamic-rendering multiview `viewMask=0x3`, but clear commands were using the physical array layer count. In Vulkan multiview dynamic rendering, clear rect layers must fit the render pass layer count, which is `1` for a nonzero view mask.
- The fatal native crash happened at `vkCmdEndRendering` after those invalid clear commands, so the clear-layer mismatch was the most likely native crash trigger.

Fix:

- Multiview FBO clears now use `ClearRect.LayerCount = 1` whenever the target FBO has a nonzero multiview view mask.
- `DefaultRenderPipeline.CreateMsaaLightCombineFBO` now ensures the sampled MSAA/post-process textures exist before creating the light-combine framebuffer, instead of relying on raw nullable `GetTexture(... )!` lookups during profile churn.
- Default pipeline resource diagnostics now describe null attachments/textures without throwing, preserving the useful rebuild log instead of turning diagnostics into a secondary crash.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal -m:1 -nr:false -p:OutDir=Build/_AgentValidation/20260701-player-camera-panel-build/temp-build-crashfix-runtime/`: passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" -v:minimal -m:1 -nr:false -p:OutDir=Build/_AgentValidation/20260701-player-camera-panel-build/temp-build-crashfix-tests/`: passed, 51 tests.
- A broader editor build was attempted first but hit local D: free-space limits while copying large native DLLs. The narrow runtime build and focused tests completed successfully after redirecting output into the agent validation run root. Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain.

## 2026-07-01 Bloom / AO / Skybox Follow-up

Symptoms:

- Monado single-pass stereo had no visible bloom or AO contribution even though the main stereo scene was otherwise lit.
- The skybox fullscreen triangle appeared as a large world-space blue triangle that failed to behave like background.

Findings:

- The skybox material supplies its own clip-space vertex shader, but `XRMeshRenderer.GetVersion` could still choose a generated stereo vertex shader whenever the material did not provide a matching multiview variant. That replaced the authored fullscreen shader and transformed the triangle like scene geometry.
- Stereo bloom writes a two-layer texture array with a mip chain. Vulkan `VkTexture2DArray.BuildAttachmentViewKey` returned the primary image view for all-layer attachments, which is only safe for single-mip array attachments. Bloom mip FBOs therefore did not get a one-mip array view for mip 1+.
- Fullscreen/internal post-process passes can temporarily clear the active rendering camera. Bloom and AO setting lookups were too dependent on `RenderState.SceneCamera`, so eye pipeline settings could be ignored or AO could be disabled at light-combine uniform time even when the stereo AO texture had been rendered.

Fix:

- Stereo mesh version selection now uses generated stereo vertex shaders only when the material has no authored vertex shader. Materials with authored vertex shaders still use a matching authored stereo variant when present, but otherwise preserve the default authored vertex stage.
- Vulkan 2D-array framebuffer attachments now create explicit full-layer, single-mip `ImageViewType.Type2DArray` views when attaching nonzero mips or any multi-mip array texture.
- Default pipeline post-process, bloom, and AO settings now resolve through the effective current pipeline camera: scene camera, rendering camera override/current camera, then retained last scene/rendering cameras.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal -m:1 -nr:false -p:OutDir=Build/_AgentValidation/20260701-player-camera-panel-build/temp-build-stereo-parity-runtime/`: passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" -v:minimal -m:1 -nr:false -p:OutDir=Build/_AgentValidation/20260701-player-camera-panel-build/temp-build-stereo-parity-tests/`: passed, 54 tests.
- Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain in build/test output.

## 2026-07-01 GTAO / Bloom Stereo Follow-up

Symptoms:

- Monado true single-pass stereo still showed no visible GTAO contribution per eye.
- Bloom was applied as a smeared/offset stereo overlay instead of matching the eye image.
- The Monado preview showed a large black band at the top.

Findings:

- The last-run Vulkan log still contained `VUID-vkCmdClearAttachments-pRects-06937` for OpenXR mirror clears. That was the black-band explanation: the invalid clear rect was using the stereo array layer count against a multiview render pass whose effective layer count is one. The current source already has the clear-layer fix, so a rebuild should stop new padding accumulation.
- GTAO resource sizing and uniforms were reading the global render area and local pipeline camera fields. In the true single-pass path, those can be empty while `ActiveRenderCommandExecutionState` still has the eye cameras and stereo state.
- Bloom mip copy/downsample/upsample shaders were deriving source UVs from `gl_FragCoord` plus render-area uniforms. That made the mip chain sensitive to the active dynamic-rendering rectangle and preview framing. These passes are full-screen texture passes, so they should map `FragPos.xy` directly to framebuffer texture UVs.

Fix:

- GTAO now resolves dimensions from the active pipeline render region first, then falls back to the global render area. Its camera/right-eye lookup also falls back to `ActiveRenderCommandExecutionState`.
- Default pipeline AO settings resolution now considers the active render-command cameras before giving up, so light combine does not silently disable AO during nested fullscreen/FBO work.
- Bloom uniform callbacks now publish the active pass viewport dimensions explicitly, and bloom copy/downsample/upsample shaders now sample with `XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy)` in both mono and stereo variants.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal -m:1 -nr:false -p:OutDir=Build/_AgentValidation/20260701-openxr-sps-ao-bloom/temp-build-runtime/`: passed.
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -v:minimal -m:1 -nr:false`: passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-build --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" -v:minimal`: passed, 55 tests.
- An attempted test run with a redirected `OutDir` filled the local D: drive while copying dependency outputs. Removed only ignored `Build/_AgentValidation/20260701-openxr-sps-ao-bloom` project-local scratch outputs and the two oldest root validation folders; final free space was about 2 GB.
- Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain in build/test output.

## 2026-07-01 Same-Issues Follow-up

The previous fix still left the same visible issues in user testing. A rebuilt short Monado smoke run reproduced the real blocker in logs before any further guessing:

- Smoke session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_23-38-53_pid45340`.
- `DeferredLightCombineStereo.fs` failed to compile with `FragPos : undeclared identifier`. This meant the stereo deferred light-combine pass was not linked, so the GTAO output could not be composited into the eye images.
- The same smoke no longer required another broad pipeline rewrite: the GTAO and bloom stereo shaders were being loaded, but the final stereo light/post-process composite was still the deciding path.

Additional fix:

- `DeferredLightCombineStereo.fs` now declares the fullscreen triangle varying `layout(location = 0) in vec3 FragPos` before using `XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy)`.
- `PostProcessStereo.fs` now samples with clip-space UVs in the final bloom/HDR composite path, so the bloom mip chain and final composition use the same fullscreen texture coordinate basis.
- `DepthUtils.glsl` owns the clip-space/framebuffer-texture UV conversion helpers used by the stereo post passes.
- OpenXR mirror clears now pass the active dynamic-rendering view mask through to `RecordClearOp`, so multiview clears resolve to `ClearRect.LayerCount = 1` even when the active framebuffer metadata still advertises two array layers.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal -m:1 -nr:false`: passed against the normal editor output path.
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore -v:minimal -m:1 -nr:false`: passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-build --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests|FullyQualifiedName~RenderPipelineResourceLifecycleTests" -v:minimal`: passed, 55 tests.
- Follow-up smoke command used `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_OPENXR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_RENDER_API=Vulkan`, and `Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 45 -TimeoutSeconds 120 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight`.
- Follow-up smoke summary: `Build/_AgentValidation/20260701-openxr-sps-ao-bloom/reports/monado-smoke-after-deferred-combine-input.json`.
  - `viewRenderModeRequested=SinglePassStereo`
  - `viewRenderImplementationPath=TrueSinglePassStereo`
  - `submittedFrameCount=43`
  - `perEyeAcquireCounts=[43,43]`
  - `perEyeReleaseCounts=[43,43]`
  - `warnings=[]`
  - `failures=[]`
- Follow-up smoke log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_23-40-57_pid5112`.
  - `DeferredLightCombineStereo`, `GTAOGenStereo`, `GTAOBlurStereo`, `BloomCopyStereo`, `BloomDownsampleStereo`, `BloomUpsampleStereo`, `PostProcessStereo`, and `FinalPostProcessStereo` compiled/linked.
  - No `VUID-vkCmdClearAttachments-pRects-06937` clear validation errors were present.
  - `GTAORawTexture`, `GTAOBlurIntermediateTexture`, `AmbientOcclusionTexture`, and `BloomBlurTexture` were allocated as stereo-compatible two-layer resources.

## 2026-07-01 Framebuffer-Local UV Follow-up

Symptoms:

- The same AO/bloom issues persisted after the `FragPos` compile fix.
- Bloom looked like a large smeared/offset overlay in both eyes.
- The skybox/background mask appeared offset against scene geometry.

Findings:

- The stereo AO, bloom, light-combine, post-process, and final-process passes were present and recording with `viewMask=0x3`; this was no longer a missing-command-chain issue.
- The remaining visible failure matched a coordinate basis mismatch: fullscreen-triangle interpolants were being used as texture UVs in passes whose active render area can differ from the physical Monado preview/window framing.
- Deferred light volume shaders now sample G-buffer/depth data using framebuffer-local coordinates, but their render parameters were still only requesting camera and clip-space uniforms. That left `ScreenWidth`, `ScreenHeight`, and `ScreenOrigin` vulnerable to stale or fallback values during stereo command recording.

Fix:

- AO, bloom copy/downsample/upsample, deferred stereo light-combine, stereo post-process, and stereo final-process shaders now sample screen-space textures from `gl_FragCoord` normalized by `ScreenOrigin`/`ScreenWidth`/`ScreenHeight`.
- Deferred light render parameters now request `ViewportDimensions` in addition to camera and clip-space uniforms, so the G-buffer/depth/shadow sampling path gets the same render-area contract as the fullscreen pipeline passes.
- Added regression assertions for framebuffer-local UV sampling and the deferred light viewport-uniform requirement.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=StereoAoAndBloomPasses_UseActiveCommandStateAndFramebufferUvSampling|Name=FullscreenCompositePasses_UseFramebufferUvForScreenAlignedSampling|Name=FragmentShader_CompilesToSpirv_ForVulkan" -v:minimal -m:1 -nr:false`: passed, 51 tests.
- The test run rebuilt `XREngine.Editor` into `Build/Editor/Debug/AnyCPU/Debug/net10.0-windows7.0`.
- Monado smoke command used `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_OPENXR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_RENDER_API=Vulkan`, and `Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 75 -TimeoutSeconds 130 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight`.
- Smoke summary: `Build/_AgentValidation/20260701-0100-openxr-sps-framebuffer-uv-light-uniforms/reports/monado-smoke-framebuffer-uv-light-uniforms.json`.
  - `viewRenderModeEffective=SinglePassStereo`
  - `viewRenderImplementationPath=TrueSinglePassStereo`
  - `submittedFrameCount=73`
  - `perEyeAcquireCounts=[73,73]`
  - `perEyeReleaseCounts=[73,73]`
  - `endFrameFailureCount=0`
  - `warnings=[]`
  - `failures=[]`
- Smoke log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_00-54-00_pid13408`.
  - `GTAOGenStereo`, `GTAOBlurStereo`, `DeferredLightingDir.fs`, `DeferredLightCombineStereo`, `BloomCopyStereo`, `BloomDownsampleStereo`, `BloomUpsampleStereo`, `PostProcessStereo`, `FinalPostProcessStereo`, and `OpenXRVulkanStereoFBO` were all present.
  - `VUID-vkCmdClearAttachments-pRects-06937: 0`
  - `InvalidImageLayout: 0`
  - `CmdEndRendering: 0`
  - `renderPass-06053: 0`
  - `renderPass-06054: 0`
  - `pLibraries-06627: 0`
  - `ShaderCompileFailed: 0`
  - `FragPos : undeclared: 0`
  - `0xC0000005: 0`
  - `True SinglePassStereo failed: 0`

Notes:

- The old startup-only Vulkan SDK pNext validation noise remains unrelated to the stereo frame path.
- Existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability advisories remain in build/test output.

## 2026-07-01 Upside-Down Regression After Framebuffer UV Fix

Symptom:

- Monado true single-pass stereo eyes were upside down again after the framebuffer-local UV fix.

Finding:

- The true single-pass stereo publish path still flipped destination Y when blitting `OpenXRVulkanStereoColorArray` layers into the OpenXR swapchain images.
- That flip was correct for the earlier clip-space/interpolated fullscreen path, but became stale after the stereo final image was brought back into the same framebuffer-local orientation contract as the other Vulkan OpenXR publish paths.

Fix:

- True single-pass stereo left/right layer publish now uses `flipY: false`, matching the other Vulkan OpenXR swapchain publish paths.
- Added a source contract assertion that the true stereo left/right publish call sites do not re-enable the flip.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=OpenXrViewRenderModeContractsStayWired" -v:minimal -m:1 -nr:false`: rebuilt the editor output but matched no tests due to a stale filter name.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-build --filter "Name=SourceContracts_SurfaceViewModeAndFoveationSettings" -v:minimal -m:1 -nr:false`: passed, 1 test.
- Monado smoke command used `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_OPENXR_VIEW_RENDER_MODE=SinglePassStereo`, `XRE_RENDER_API=Vulkan`, and `Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 60 -TimeoutSeconds 120 -NoBuild -SkipAllocationAudit -SkipLoaderPreflight`.
- Smoke summary: `Build/_AgentValidation/20260701-0110-openxr-sps-no-publish-flip/reports/monado-smoke-no-publish-flip.json`.
  - `viewRenderModeEffective=SinglePassStereo`
  - `viewRenderImplementationPath=TrueSinglePassStereo`
  - `submittedFrameCount=58`
  - `perEyeAcquireCounts=[58,58]`
  - `perEyeReleaseCounts=[58,58]`
  - `endFrameFailureCount=0`
  - `warnings=[]`
  - `failures=[]`
- Smoke log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_01-05-53_pid29180`.
  - `true stereo left eye swapchain image: 12`
  - `true stereo right eye swapchain image: 12`
  - `VUID-vkCmdClearAttachments-pRects-06937: 0`
  - `InvalidImageLayout: 0`
  - `CmdEndRendering: 0`
  - `renderPass-06053: 0`
  - `renderPass-06054: 0`
  - `pLibraries-06627: 0`
  - `ShaderCompileFailed: 0`
  - `FragPos : undeclared: 0`
  - `0xC0000005: 0`
  - `True SinglePassStereo failed: 0`
