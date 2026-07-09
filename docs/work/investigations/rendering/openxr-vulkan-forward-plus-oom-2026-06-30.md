# OpenXR Vulkan Forward+ OOM - 2026-06-30

## Problem

The Vulkan/OpenXR editor session crashed after running single-pass stereo for a short time:

- `VulkanOutOfMemoryException`
- `VK_ERROR_DEVICE_LOST`
- failed OpenXR Vulkan logical-device recreation after device loss

Source log session:
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_15-00-38_pid40936`

Agent evidence:
`Build/_AgentValidation/20260630-150138-vulkan-oom-leak/logs/forward-plus-buffer-lines.txt`

## Findings

The first failing pattern was repeated Forward+ SSBO allocation, not the later OpenXR teardown errors.

`ForwardPlusVisibleIndices` was being recreated every frame, commonly at about 28.9 MB, with a paired host-visible staging allocation. The logs show `recreate=True` on each upload and tracked Vulkan memory growing until device loss. The editor view and HMD view use different render areas, so exact-size buffer ownership caused the Forward+ pass to churn whenever views alternated.

The `vkDeviceWaitIdle` invalid-device and Monado swapchain cleanup warnings happened after device loss and should be treated as crash fallout unless they persist after allocation churn is fixed.

## Fix

`VPRC_ForwardPlusLightCullingPass` now treats Forward+ SSBO sizes as capacities:

- local-light, visible-index, and tile-count buffers grow to the next power of two
- buffers are recreated only when required element count exceeds existing capacity
- viewport-size changes that fit the existing capacity reuse the buffer
- local-light upload uses bounded `PushSubData` instead of resizing/uploading the full backing store

Regression coverage was added in `GpuIndirectPhase7ZeroReadbackTests` to reject the old exact-size reallocation pattern.

## Validation

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "ForwardPlusLightCulling"`
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- Short OpenXR/Vulkan smoke run with Unit Testing World, Monado, and single-pass stereo:
  - summary: `Build/_AgentValidation/20260630-150138-vulkan-oom-leak/reports/openxr-smoke-summary.json`
  - engine log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-30_15-14-38_pid45580`
  - submitted frames: 18
  - end-frame failures: 0
  - view render path: `TrueSinglePassStereo`
  - max tracked Vulkan VRAM in this run: 519,682,096 bytes
  - no `VulkanOutOfMemoryException`, `VK_ERROR_DEVICE_LOST`, or `ErrorDeviceLost` messages found
  - Forward+ visible-index allocations occurred during startup/pipeline setup and then stopped

The test and build passed. The smoke wrapper hit the outer command timeout after the summary was written, so it is evidence for the render/OOM path, not a full process-exit validation. The build still reports existing NuGet advisory warnings for `Magick.NET-Q16-HDRI-AnyCPU`.

## Follow-Up

Single-pass stereo black-eye rendering is a separate issue. This fix removes the OOM/device-loss path that was masking later stereo diagnostics.
