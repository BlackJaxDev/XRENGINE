# OpenVR VRClient GPU Handoff TODO

Last updated: 2026-04-28

This tracker covers a zero-readback GPU handoff from the main engine process to `XREngine.VRClient` so the legacy OpenVR companion process can upload rendered eye frames to SteamVR with minimal latency.

The Vulkan upscale bridge proves the core interop shape: Vulkan-owned images, exported Win32 external-memory handles, OpenGL imports, and external semaphore synchronization. This work turns that pattern into a cross-process VR frame transport. The current upscale bridge should be treated as the reference implementation for resource ownership and synchronization, not as the runtime object to enable directly.

## Goal

Deliver rendered eye images from the engine app to the OpenVR client app without CPU readback, CPU staging, CPU upload, image files, sockets carrying pixels, or per-frame handle churn.

Target runtime flow:

1. `XREngine.VRClient` owns the SteamVR/OpenVR lifetime, input polling, compositor submission, and `PostPresentHandoff()` timing.
2. The main engine process owns scene simulation, culling, and eye rendering.
3. The two processes share per-eye GPU image slots through duplicated Win32 handles.
4. The engine renders directly into shared eye textures when possible, or performs one GPU copy/resolve from existing eye FBOs when direct rendering is not ready.
5. The VRClient imports the shared images into its local OpenGL context, obtains local GL texture names, and submits those names to `IVRCompositor.Submit(..., ETextureType.OpenGL, ...)`.
6. External GPU semaphores synchronize producer readiness and consumer release without blocking either process on CPU work in the steady state.

## Non-Goals

- Do not send frame pixels through named pipes, TCP, shared CPU memory, screenshots, or image encoders.
- Do not submit Vulkan opaque Win32 external-memory handles as OpenVR `DXGISharedHandle`; they are not DXGI shared D3D texture handles.
- Do not require migrating the whole renderer to Vulkan.
- Do not fold OpenVR lifetime back into the main app. The separate client exists to isolate legacy OpenVR startup/shutdown behavior.
- Do not target XR multiview, OpenXR swapchains, Linux FD handles, or remote-machine streaming in the first milestone.

## Architecture Notes

OpenGL texture names are process-local and context-local. The engine cannot send a GL texture ID to `XREngine.VRClient` and expect OpenVR submission to work. The shared object must be the underlying GPU allocation, imported separately by each process into its own graphics API objects.

The preferred MVP is Vulkan-owned shared images exported as `OpaqueWin32` external memory, matching the existing upscale bridge. Both processes then import those allocations into OpenGL. This keeps the engine's OpenGL render path authoritative while avoiding the ambiguity of OpenGL-created memory-object export paths.

Use per-eye 2D textures for the first implementation. Multiview array texture sharing can come later after the basic two-eye path is stable and measured.

## Phase 0 - Feasibility And Contracts

- [ ] Confirm target hardware support in both processes:
  - [ ] `GL_EXT_memory_object`
  - [ ] `GL_EXT_memory_object_win32`
  - [ ] `GL_EXT_semaphore`
  - [ ] `GL_EXT_semaphore_win32`
  - [ ] `VK_KHR_external_memory`
  - [ ] `VK_KHR_external_memory_win32`
  - [ ] `VK_KHR_external_semaphore`
  - [ ] `VK_KHR_external_semaphore_win32`
- [ ] Reuse or extend the Vulkan upscale bridge GPU identity check so OpenGL, Vulkan sidecar, and OpenVR submission land on the same physical GPU.
- [ ] Confirm OpenVR compositor submission remains stable with GL textures whose storage comes from imported external memory.
- [ ] Define MVP formats:
  - [ ] Eye color: `RGBA8` SDR first.
  - [ ] HDR eye color: `RGBA16f` follow-up.
  - [ ] Depth: out of scope for MVP, optional for future `Submit_TextureWithDepth` experiments.
- [ ] Choose the initial buffering model: double-buffered first, triple-buffered if compositor or producer stalls require it.
- [ ] Decide whether direct rendering into shared FBOs is required for MVP or whether one GPU blit/resolve is acceptable for the first hardware validation pass.

## Phase 1 - Shared Transport Primitives

- [ ] Extract reusable Win32 external-memory and external-semaphore helpers from `VulkanUpscaleBridgeSidecar` into a neutral rendering interop layer.
- [ ] Support duplicating exported handles into a target process with clear ownership and `CloseHandle` rules.
- [ ] Replace the rough `Engine.VRState.ExportAndSendHandles(...)` scaffold with a typed, versioned handshake.
- [ ] Define transport metadata:
  - [ ] protocol version,
  - [ ] resource generation,
  - [ ] slot index,
  - [ ] eye index,
  - [ ] width and height,
  - [ ] pixel format and color space,
  - [ ] memory size,
  - [ ] memory handle,
  - [ ] ready semaphore handle,
  - [ ] release semaphore handle,
  - [ ] producer process ID,
  - [ ] consumer process ID.
- [ ] Send handles only during create/recreate handshakes, not every frame.
- [ ] Add reconnect handling so the engine can destroy and recreate shared resources when the VRClient exits or restarts.
- [ ] Add one-time diagnostics for unsupported interop, import failure, mismatched GPU, stale generation, and broken pipe states.

## Phase 2 - Engine Producer

- [ ] Add an `OpenVrGpuHandoffProducer` or similarly named component/service in the engine runtime.
- [ ] Allocate shared per-eye frame slots using the extracted Vulkan external-image helper.
- [ ] Import shared images into the engine OpenGL renderer as renderable `XRTexture2D` objects.
- [ ] Build per-eye `XRFrameBuffer` wrappers around the imported textures.
- [ ] Render directly into shared per-eye FBOs when the OpenVR two-pass path is active.
- [ ] Add a fallback GPU copy/resolve path from existing `VRLeftEyeRenderTarget` / `VRRightEyeRenderTarget` into the shared slot.
- [ ] Avoid CPU waits in the normal path; if the next slot is still in use, prefer dropping/reusing according to a documented low-latency policy.
- [ ] Signal the per-slot ready semaphore after all rendering or copy work for both eyes is complete.
- [ ] Track frame metadata:
  - [ ] frame index,
  - [ ] slot index,
  - [ ] predicted display time or OpenVR prediction offset,
  - [ ] left/right pose sample IDs,
  - [ ] render resolution,
  - [ ] generation.
- [ ] Recreate resources on HMD recommended-size changes, color format changes, VR mode changes, GPU device changes, and client reconnects.

## Phase 3 - VRClient Consumer

- [ ] Add an `OpenVrGpuHandoffConsumer` or similarly named service to `XREngine.VRClient`.
- [ ] Receive the create/recreate handshake and import each duplicated memory handle into the VRClient OpenGL context.
- [ ] Create local GL texture objects whose storage is backed by the imported memory objects.
- [ ] Import ready and release semaphores into the VRClient OpenGL context.
- [ ] Wait on the ready semaphore for the selected slot before compositor submission.
- [ ] Submit the local left/right GL texture names through `Engine.VRState.SubmitRenders(...)` with `ETextureType.OpenGL`.
- [ ] Call `PostPresentHandoff()` immediately after the right-eye submit, preserving the current OpenVR behavior.
- [ ] Signal the release semaphore after OpenVR submission has consumed the slot enough for producer reuse.
- [ ] Handle generation changes without submitting stale textures.
- [ ] Tear down imported GL objects and external memory objects on disconnect, resize, format change, or process shutdown.

## Phase 4 - Pose And Timing Alignment

- [ ] Keep OpenVR input and prediction in the VRClient process.
- [ ] Send predicted HMD/controller poses from VRClient to the engine with frame IDs and prediction timing metadata.
- [ ] Render against the pose sample intended for the frame slot being submitted.
- [ ] Keep the producer queue depth intentionally shallow to avoid showing old poses.
- [ ] Add instrumentation for:
  - [ ] pose sample time,
  - [ ] engine render start/end,
  - [ ] ready semaphore signal,
  - [ ] VRClient ready wait,
  - [ ] OpenVR submit time,
  - [ ] `PostPresentHandoff()` time,
  - [ ] compositor frame timing.
- [ ] Decide whether late-latching-style pose updates are possible in the current OpenVR path or should remain future work.

## Phase 5 - Tests And Diagnostics

- [ ] Add source-verifiable tests for transport metadata serialization and version handling.
- [ ] Add tests that assert the VR handoff path does not use CPU image readback or per-frame handle transfer.
- [ ] Add tests for generation mismatch rejection and reconnect state transitions.
- [ ] Add diagnostics for slot starvation, dropped frames, stale frame submission, and compositor submit errors.
- [ ] Add profiler counters:
  - [ ] handoff slots allocated,
  - [ ] current slot index,
  - [ ] ready-wait duration,
  - [ ] producer blocked/dropped count,
  - [ ] GPU copy/resolve duration,
  - [ ] submit-to-handoff timing.
- [ ] Document environment toggles after implementation, including enable/disable, slot count, direct-render versus blit mode, and verbose diagnostics.

## Manual Hardware Validation

- [ ] Launch editor/main engine and `XREngine.VRClient` as separate processes on Windows.
- [ ] Confirm the VRClient owns OpenVR initialization and compositor submission.
- [ ] Confirm the main engine renders per-eye frames into shared GPU slots.
- [ ] Confirm no CPU readback, staging texture upload, screenshot path, or pixel pipe is active.
- [ ] Validate repeated startup/shutdown of VRClient while the engine keeps running.
- [ ] Validate HMD recommended render target resize and SteamVR supersampling changes.
- [ ] Validate alt-tab, minimize, restore, and monitor sleep/wake behavior.
- [ ] Validate compositor errors are reported once with actionable state.
- [ ] Compare latency against current local OpenVR rendering and any existing client-mode transport.
- [ ] Capture GPU timings for direct shared-FBO render and fallback GPU blit/resolve.

## Risks And Open Questions

- [ ] Imported external-memory-backed GL textures may expose driver-specific limitations when submitted to OpenVR. Hardware validation is mandatory.
- [ ] The current Vulkan upscale bridge intentionally rejects stereo/XR pipelines, so shared primitives must be factored out instead of enabling that bridge directly.
- [ ] Cross-process handle lifetime is easy to get wrong. Every exported, duplicated, imported, and closed handle needs a single documented owner.
- [ ] Multi-GPU systems can silently break the path if OpenGL, Vulkan, and SteamVR choose different adapters.
- [ ] A one-frame queue can minimize latency but risks missed compositor deadlines; a two or three slot queue is safer but can show older poses if not managed carefully.
- [ ] The first milestone should use color-only submission. Depth-assisted reprojection can be evaluated only after the color handoff is stable.

## Acceptance Criteria

- [ ] With compatible Windows hardware, the VRClient can submit engine-rendered eye frames to OpenVR without CPU frame readback or CPU upload.
- [ ] Shared handles are exchanged only during resource creation/recreation.
- [ ] Steady-state frame handoff uses GPU semaphores and bounded slot metadata only.
- [ ] The system recovers from resize, client restart, and unsupported capability states without crashing either process.
- [ ] Diagnostics clearly identify whether failures are caused by missing GL interop, missing Vulkan interop, GPU mismatch, handle duplication/import failure, semaphore wait/signal failure, or OpenVR compositor submit errors.

## Key Files

- `XREngine.VRClient/Program.cs`
- `XRENGINE/Engine/Engine.VRState.cs`
- `XRENGINE/Engine/Engine.Networking.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.VulkanUpscaleBridge.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridge.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanUpscaleBridgeSidecar.cs`
- `docs/architecture/rendering/openvr-rendering.md`
- `docs/features/vulkan-upscale-bridge.md`

## Related Documentation

- [OpenVR Rendering](../../architecture/rendering/openvr-rendering.md)
- [Vulkan Upscale Bridge](../../features/vulkan-upscale-bridge.md)
- [VR Development](../../api/vr-development.md)
- [Vulkan Manual Validation Guide](vulkan.md)