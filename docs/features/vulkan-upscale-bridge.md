# Vulkan Upscale Bridge

Last updated: 2026-04-28

The Vulkan upscale bridge lets the OpenGL renderer use Vulkan-only vendor upscalers without migrating the whole renderer to Vulkan. OpenGL remains authoritative for the frame graph and final present, while a per-viewport Vulkan sidecar owns shared bridge images, imports them through Win32 external-memory interop, runs DLSS or XeSS, and returns the upscaled output to OpenGL through external semaphore synchronization.

The bridge is implemented for the Windows desktop OpenGL path. Remaining work is hardware validation on compatible NVIDIA and Intel systems plus future expansion to XR, editor multi-viewport, Linux FD handles, optional Vulkan present, and XeSS frame generation after a DX12 swapchain path exists.

## Runtime Shape

The bridge path runs as a sidecar around the existing OpenGL render pipeline:

1. OpenGL renders the frame normally at internal resolution.
2. OpenGL copies or resolves source color, depth, motion, and exposure into shared bridge surfaces.
3. OpenGL signals an external semaphore for the sidecar.
4. Vulkan imports the shared resources, transitions them, and dispatches the selected vendor upscaler.
5. Vulkan writes the upscaled result into a shared output image.
6. OpenGL waits on the completion semaphore and samples or blits the output for final present.

The implementation uses Vulkan-owned shared images and exported Win32 handles, then imports those handles into explicit OpenGL bridge textures. This keeps the path zero-readback while matching the import-oriented OpenGL interop support available in the engine.

`VPRC_VendorUpscale` selects the best available path in this order:

- native `VulkanRenderer` vendor dispatch,
- OpenGL-to-Vulkan upscale bridge,
- fallback OpenGL blit.

When DLSS or XeSS is requested but unavailable, the command emits a one-time diagnostic and preserves the fallback blit behavior.

## Capability And Scope

The shipped bridge scope is intentionally narrow:

- Windows only.
- Mono desktop viewport first.
- Per-viewport bridge ownership.
- No XR, multiview, editor multi-viewport, server, or Linux FD-handle path yet.
- No frame generation in the OpenGL bridge milestone.
- No CPU readback, CPU staging, or hidden GPU-to-CPU-to-GPU copies.
- OpenGL remains the present owner in bridge mode.

Bridge availability is controlled by `XRE_ENABLE_VULKAN_UPSCALE_BRIDGE`. The bridge is enabled by default and accepts `0` or `false` to disable dispatch.

Startup capability probing reports why the bridge is unavailable instead of silently falling back. Required interop support includes:

- OpenGL `EXT_memory_object`
- OpenGL `EXT_memory_object_win32`
- OpenGL `EXT_semaphore`
- OpenGL `EXT_semaphore_win32`
- Vulkan `VK_KHR_external_memory`
- Vulkan `VK_KHR_external_memory_win32`
- Vulkan `VK_KHR_external_semaphore`
- Vulkan `VK_KHR_external_semaphore_win32`

The probe also rejects configurations where OpenGL and the Vulkan sidecar would land on different physical GPUs.

## Bridge Resources

The initial bridge surface set is explicit and shared across DLSS and XeSS:

| Resource | Resolution | Format | Notes |
|---|---:|---|---|
| Source color | Internal | `RGBA16f` | Pre-upscale color input. |
| Source depth | Internal | `Depth24Stencil8` | Preserves engine depth convention. |
| Source motion | Internal | `RG16f` | NDC-delta velocity; bridge applies vendor scale conversion. |
| Exposure | 1x1 | `R32f` | Shared GPU exposure when auto exposure is active, scalar fallback otherwise. |
| Output color | Display | `RGBA8` or `RGBA16f` | SDR uses `RGBA8`; HDR uses `RGBA16f`. |

`DefaultRenderPipeline` and `DefaultRenderPipeline2` both thread the bridge source names through the vendor upscale command so the bridge receives matching color, depth, motion, and exposure inputs.

Depth stays in the engine's `Depth24Stencil8` surface. Reversed-Z is communicated through the vendor dispatch contract instead of renormalizing the texture. Motion vectors are produced as `RG16f` NDC-delta motion and normalized with a `0.5` scale before DLSS or XeSS dispatch.

The bridge does not emit a dedicated reactive or transparency mask yet. The current shader-side reactive behavior remains the MVP choice, and a dedicated mask should only be added if hardware quality validation shows a need.

## Lifetime And Fault Handling

`VulkanUpscaleBridge` and `VulkanUpscaleBridgeSidecar` own per-viewport bridge state, shared images, Win32 handles, Vulkan image views, semaphores, layout transitions, and vendor session lifetime.

Bridge states are explicit:

- `Unsupported`
- `Disabled`
- `Initializing`
- `Ready`
- `NeedsRecreate`
- `Faulted`

The bridge recreates resources for viewport resize, output format changes, HDR toggles, AA changes that alter source resources, bridge enable/disable changes, and vendor selection changes. Recreate paths rebuild shared resources without leaving stale handles. Fault reporting is centralized so one failed import or dispatch does not spam logs every frame.

The sidecar keeps a lightweight Vulkan device rather than relying on an active `VulkanRenderer`. It enables vendor-specific required Vulkan instance/device requirements when a vendor bridge path requests them.

## DLSS Bridge

DLSS bridge dispatch uses the real NVIDIA Streamline path on the sidecar Vulkan device. `StreamlineNative` owns Streamline initialization, shutdown, Vulkan info setup, feature function resolution, resource allocation/free, resource tagging, constants upload, and `slEvaluateFeature`.

Per-frame DLSS inputs include:

- input size and output size,
- jitter offsets from temporal state,
- normalized motion-vector scale,
- sharpness,
- reset-history flag,
- exposure texture or scalar fallback,
- reversed-Z flag,
- HDR/SDR mode,
- frame index.

DLSS history resets on resize, camera or scene changes, bridge resource recreation, vendor switches, output-mode flips, and large camera cuts. Output-size and HDR flips rebuild shared frame slots and free Streamline DLSS viewport resources while keeping the sidecar Vulkan device alive.

Streamline / NGX v2.9 rejects `NVSDK_NGX_PerfQuality_Value_UltraQuality`, so the engine's DLSS Ultra Quality preset keeps the higher internal render scale but submits Streamline `MaxQuality` mode.

## XeSS Bridge

XeSS bridge dispatch uses the real Vulkan `xessVK` integration on the same sidecar resource path. `IntelXessNative` loads Vulkan XeSS exports, queries required extension and feature chains, creates the bridge XeSS context, initializes it for the sidecar output size, and records `xessVKExecute` work against shared bridge images.

XeSS receives the same engine-driven per-frame inputs where the API supports them:

- jitter,
- normalized velocity scale,
- reversed-Z,
- HDR/SDR initialization flags,
- exposure texture or scalar fallback,
- reset-history state,
- active quality mode.

Runtime `XessCustomScale` changes flow through `ApplyIntelXessPreference()`. The public XeSS API does not expose native sharpening, so requested XeSS sharpening is applied on the OpenGL fallback quad during bridge present.

XeSS frame generation remains intentionally out of scope for this bridge because it requires a DX12 swapchain path.

## Diagnostics And Validation

The bridge path emits capability and fallback diagnostics through `Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(...)` and one-time vendor fallback warnings from `VPRC_VendorUpscale`. Per-dispatch timing is reported as `DispatchMs=...` so live import and vendor evaluation cost can be captured on hardware.

Automated coverage lives in:

- `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs`
- `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs`

The tests cover capability snapshot reporting, pipeline source mapping, resize/recreate contracts, sidecar surface formats, DLSS and XeSS bridge parameter wiring, OpenGL fallback behavior, and native vendor export checks.

Manual hardware validation is still required for final runtime confidence:

- OpenGL + bridge + DLSS on Windows with a compatible NVIDIA GPU/driver and deployed Streamline runtime.
- OpenGL + bridge + XeSS on Windows with compatible Intel XeSS runtime deployment.
- Repeated resize, runtime vendor toggles, camera-cut history reset, alt-tab, minimize, and restore behavior.
- Validation-layer cleanliness on the sidecar Vulkan path.
- Scene-by-scene image quality, motion stability, first-frame activation, and import/dispatch timing.

## Key Files

- `XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridge.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeSidecar.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeProbe.cs`
- `XRENGINE/Rendering/DLSS/StreamlineNative.cs`
- `XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs`
- `XRENGINE/Rendering/XeSS/IntelXessNative.cs`
- `XRENGINE/Rendering/XeSS/IntelXessManager.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.VulkanUpscaleBridge.cs`
- `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs`
- `XREngine.UnitTests/Rendering/VulkanUpscaleBridgeTodoCompletionTests.cs`

## Future Expansion

- Broader HDR color-space validation across vendor runtimes and displays.
- XR and stereo bridge support.
- Editor multi-viewport support.
- Optional direct Vulkan present path instead of returning output to OpenGL.
- Shared-GPU OpenVR VRClient handoff built from the bridge's external-memory and semaphore primitives.
- Linux FD-handle interop path.
- XeSS frame generation after a DX12 swapchain path exists.

## Related Documentation

- [Default Render Pipeline Notes](../architecture/rendering/default-render-pipeline-notes.md)
- [OpenVR VRClient GPU Handoff TODO](../work/todo/openvr-vrclient-gpu-handoff-todo.md)
- [Vulkan Backlog](../work/todo/vulkan.md)
- [ReSTIR GI](gi/restir-gi.md)
