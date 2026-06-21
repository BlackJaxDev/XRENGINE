# Vulkan Device Loss Recovery Investigation

## Problem

- The editor was running with Vulkan while the NVIDIA graphics driver was upgraded.
- The driver restart reset the GPU, Vulkan reported device loss, and the renderer stopped producing new frames.
- The debugger showed repeated first-chance `NullReferenceException` and `TargetInvocationException` messages after the loss.

## Findings

- The latest editor log directory, `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-19_19-35-29_pid18932`, only contains `editor_bootstrap.log` with startup entries. `log_vulkan.log` and `log_rendering.log` are zero bytes, so the debugger output is the only captured evidence for the device-loss run.
- Vulkan logical device loss is terminal for that `VkDevice`. The current logical device, swapchain, command buffers, descriptors, buffers, images, and pipeline objects cannot be made valid again.
- Practical recovery requires abandoning the old renderer/device-owned wrappers and creating a new Vulkan renderer/device. At the start of the investigation this was blocked by `XRWindow.Renderer` being readonly; the implemented fix keeps the same Silk window and makes the renderer replaceable inside `XRWindow`.
- The existing Vulkan backend marked some `ErrorDeviceLost` paths, but not every acquire/present path. `XRWindow` then treated the renderer as a normal backoff failure and could keep invoking frame work that cannot succeed.

## Attempted Fixes

- Added a backend-neutral `IRuntimeRendererHost.IsDeviceLost` surface and exposed it from `AbstractRenderer` and `VulkanRenderer`.
- Centralized Vulkan device-loss marking with a one-time diagnostic that explains the renderer/window must be recreated.
- Mark and throw explicit terminal device-loss exceptions for `AcquireNextImage`, Streamline acquire/present, acquire-bridge submit, draw submit, queue present, generic queue submit, and async depth-readback fence/submit loss paths.
- Added an initial `XRWindow` terminal device-loss guard so the window stops treating lost-device failures as ordinary render backoff failures.
- Moved the render-frame upload/readback preamble inside the render exception guard so device-loss fallout there is captured instead of escaping the circuit breaker.
- Replaced that terminal endpoint with same-window renderer recovery:
  - `XRWindow.Renderer` remains externally get-only but is internally replaceable.
  - Device-loss checkpoints recreate a renderer for the existing Silk window, initialize a fresh Vulkan instance/device/swapchain/sync set, and return so the next frame renders through the new backend.
  - The lost renderer is cleaned up with the normal renderer teardown sequence, but each cleanup step is best-effort so `ErrorDeviceLost` cannot block replacement.
  - Scene-panel and viewport pipeline resources are invalidated after recovery so stale editor panel textures and renderer-owned wrappers rebuild on the new device.
  - Recovery is bounded to three attempts per window; repeated failure still permanently disables rendering with diagnostics instead of spamming frame exceptions.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore` passed with 0 warnings and 0 errors.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` initially compiled the rendering project but failed to copy editor output because an existing unit-testing editor process held the DLLs open. After stopping that repo-local `dotnet .\Build\Editor\...\XREngine.Editor.dll --unit-testing --mcp ...` process, the editor build passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~VulkanDynamicRenderingMigrationTests"` passed: 24 tests.

## Follow-Ups

- Validate recovery against a real GPU reset/driver restart. The implementation is built and unit-tested, but the terminal `VK_ERROR_DEVICE_LOST` path still needs live editor confirmation.
- Consider an editor UX notification for terminal renderer failure with a restart renderer/window action.
