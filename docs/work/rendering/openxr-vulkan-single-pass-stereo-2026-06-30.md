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
