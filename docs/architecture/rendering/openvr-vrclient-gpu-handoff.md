# OpenVR VRClient GPU Handoff

Last updated: 2026-10-06

The OpenVR VRClient GPU handoff is the planned cross-process transport for rendered eye images from the engine process to `XREngine.VRClient`. The goal is zero CPU frame readback. The VRClient keeps OpenVR ownership, input polling, compositor submission, and `PostPresentHandoff()` timing. The engine keeps scene simulation, culling, and eye rendering.

Related docs:

- [OpenVR Rendering](openvr-rendering.md)
- [Vulkan Upscale Bridge](../../developer-guides/rendering/vulkan-upscale-bridge.md)
- [OpenVR VRClient GPU Handoff Validation](../../work/testing/xr/openvr-vrclient-gpu-handoff-validation.md)
- [OpenVR VRClient GPU Handoff TODO](../../work/todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md)

## Current Runtime Ownership

`RuntimeEngine.VRState` is a `RuntimeVrState` instance in `XREngine.Runtime.Rendering/Runtime/RuntimeVrState.cs`. It owns process-wide VR state and delegates startup or transport operations through `IRuntimeVrLifecycleServices`.

`XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` owns OpenVR lifecycle callbacks, eye render targets, local eye rendering, and the `SubmitRenders(...)` entry point. `SubmitRenders(...)` accepts OpenGL texture names for OpenVR. It rejects Vulkan handles when the active renderer is not OpenGL.

`XREngine.Runtime.XR.OpenVR/OpenVrCompositorBackend.cs` adapts to the native OpenVR compositor. It submits left and right OpenGL texture names through `IVRCompositor.Submit(...)` and calls `PostPresentHandoff()` once after the pair.

`XREngine.VRClient/Program.cs` starts the companion process and calls the VR lifecycle client startup path. It can pair with the editor process through `XRE_VRCLIENT_GAMENAME`.

## Transport Shape

The shared object is the underlying GPU allocation, not an OpenGL texture name. OpenGL texture names are process-local and context-local. The engine cannot send a GL texture ID to `XREngine.VRClient` and expect compositor submission to work.

The preferred shape is:

1. A Vulkan helper creates per-eye shared images and exports Win32 external-memory handles.
2. The producer duplicates handles into the VRClient process during create or recreate.
3. The engine imports the shared images into its OpenGL context and renders into them directly when supported.
4. If direct rendering is not ready, the engine performs one GPU copy or resolve from the existing OpenVR eye targets into the shared slot.
5. The VRClient imports the duplicated memory handles into its OpenGL context and creates local GL texture names.
6. External GPU semaphores synchronize producer-ready and consumer-release events.
7. The VRClient submits the local GL texture names to OpenVR as `ETextureType.OpenGL`.

The steady-state path transfers bounded metadata only. It does not transfer handles each frame.

## Required Interop Capabilities

Both OpenGL contexts must support:

- `GL_EXT_memory_object`
- `GL_EXT_memory_object_win32`
- `GL_EXT_semaphore`
- `GL_EXT_semaphore_win32`

The Vulkan sidecar or helper must support:

- `VK_KHR_external_memory`
- `VK_KHR_external_memory_win32`
- `VK_KHR_external_semaphore`
- `VK_KHR_external_semaphore_win32`

The OpenGL renderer, Vulkan helper, and SteamVR compositor must use the same physical GPU. A mismatch must reject the handoff with a diagnostic.

## Resource Model

The first milestone uses per-eye 2D color textures. Array texture or multiview sharing can come later. The MVP color target is SDR `RGBA8`. HDR `RGBA16f` is a follow-up unless the feature owner selects it for MVP.

A shared resource generation identifies a compatible set of slots. Each slot carries:

- protocol version
- resource generation
- slot index
- eye index
- width and height
- pixel format and color space
- memory size
- memory handle
- ready semaphore handle
- release semaphore handle
- producer process ID
- consumer process ID

Generation changes invalidate old imports. The consumer must not submit stale textures.

## Synchronization And Queueing

The producer signals a ready semaphore after it renders or resolves both eyes for a slot. The consumer waits on ready before compositor submission. The consumer signals release after OpenVR has consumed the slot enough for producer reuse.

The queue stays shallow to protect pose freshness. If the next slot is in use, the producer follows a documented low-latency drop or reuse policy. The normal path must not wait on the CPU for GPU completion.

## Pose And Timing

OpenVR input and prediction stay in the VRClient process. The VRClient sends predicted HMD and controller poses to the engine with frame IDs and prediction timing metadata. The engine renders against the pose sample intended for the slot that the VRClient will submit.

Instrumentation must report pose sample time, engine render start and end, ready signal time, VRClient ready wait, `PostPresentHandoff()` time, and compositor frame timing.

## Diagnostics And Recovery

The handoff reports these states with stable diagnostics:

- unsupported OpenGL interop
- unsupported Vulkan interop
- GPU mismatch
- handle duplication failure
- memory import failure
- semaphore import or wait failure
- stale generation
- slot starvation
- dropped frame
- stale frame submission
- compositor submit error
- broken pipe or disconnected client

The engine recreates shared resources when the HMD recommended size, color format, VR mode, GPU device, or client connection changes.

## Non-Goals

The handoff does not send pixels through named pipes, TCP, shared CPU memory, screenshots, or image encoders. It does not submit Vulkan opaque Win32 external-memory handles as OpenVR `DXGISharedHandle` values. It does not require moving the renderer to Vulkan. It does not move OpenVR lifetime into the main app. The first milestone does not target OpenXR swapchains, Linux FD handles, remote-machine streaming, or depth-assisted reprojection.

## Source Map
| Responsibility | Source |
|---|---|
| Process-wide VR state | `XREngine.Runtime.Rendering/Runtime/RuntimeVrState.cs` |
| OpenVR lifecycle, eye render targets, and submit entry point | `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` |
| VRClient startup | `XREngine.VRClient/Program.cs` |
| OpenVR compositor submission | `XREngine.Runtime.XR.OpenVR/OpenVrCompositorBackend.cs` |
| OpenGL external-memory import capability | `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Interop/OpenGLRenderer.ExternalInteropCapability.cs` |
| OpenGL external-memory texture storage | `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture2D.Storage.cs` |
| Reference external-memory sidecar | `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs` |
| Upscale bridge runtime control | `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.VulkanUpscaleBridge.cs` |
