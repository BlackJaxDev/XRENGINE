# OpenXR Vulkan Single-Pass Stereo Investigation - 2026-06-30

## Problem

OpenXR + Monado + Vulkan single-pass stereo was rendering black eye output. Earlier runs also showed only the stereo pre-render clear in the GPU profiler, shader compile exceptions, invalid Vulkan image-view validation, and intermittent device-loss fallout.

## Findings

- The true single-pass stereo command path now records and submits real render work. The final validated run reports `active_stereo_mode=openxr-true-single-pass-stereo` and `vr_view_render_implementation_path=TrueSinglePassStereo`.
- Vulkan rejected layered color/depth image views when a logical 2D texture was backed by two array layers. A `VK_IMAGE_VIEW_TYPE_2D` view with `layerCount=2` is invalid; these views must be promoted to `VK_IMAGE_VIEW_TYPE_2D_ARRAY`.
- Generated stereo vertex shaders emitted `layout(num_views = 2) in;`, which is the OpenGL/OVR multiview declaration. Vulkan multiview gets view count from the render pass/dynamic rendering view mask, so Vulkan GLSL should strip that declaration and keep `gl_ViewIndex`/`GL_EXT_multiview`.
- The active local unit-testing settings can start directly in single-pass stereo through `VR.ViewRenderMode`.

## Changes

- `VkImageBackedTexture` and `VkTextureView` now normalize descriptor/image view types by layer count so multilayer 1D/2D images use array views.
- `VulkanShaderCompiler` now strips `layout(num_views = ...) in;` from Vulkan GLSL after both initial normalization and final rewrite, and still injects `GL_EXT_multiview` when `gl_ViewIndex` remains.
- Added a source-contract regression test covering Vulkan single-pass stereo shader and layered view wiring.
- Added `VR.ViewRenderMode: SinglePassStereo` to the local ignored unit-testing settings.

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
