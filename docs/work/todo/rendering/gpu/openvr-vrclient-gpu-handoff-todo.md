# OpenVR VRClient GPU Handoff TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenVR VRClient GPU Handoff](../../../../architecture/rendering/openvr-vrclient-gpu-handoff.md)
Validation: [OpenVR VRClient GPU Handoff Validation](../../../testing/xr/openvr-vrclient-gpu-handoff-validation.md)

## Current State

OpenVR runtime state now lives in `XREngine.Runtime.Rendering/Runtime/RuntimeVrState.cs`. OpenVR lifecycle, eye render targets, and `SubmitRenders(...)` live in `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs`. The VRClient starts through `XREngine.VRClient/Program.cs`, and `XREngine.Runtime.XR.OpenVR/OpenVrCompositorBackend.cs` submits OpenGL texture names and calls `PostPresentHandoff()`. The Vulkan upscale bridge proves same-process OpenGL/Vulkan external-memory interop in `XREngine.Runtime.Rendering.Vulkan`, but no `OpenVrGpuHandoffProducer`, `OpenVrGpuHandoffConsumer`, or cross-process eye-frame transport exists yet.

## Open Code Items

### Shared Transport Primitives

- [ ] Extract reusable Win32 external-memory and external-semaphore helpers from the Vulkan upscale bridge. Files or types: `VulkanUpscaleBridgeSidecar`, `VulkanUpscaleBridge`, a new neutral rendering interop layer. Done when desktop upscaling and OpenVR handoff can share the helper without enabling XR through the upscale bridge.
- [ ] Add target-process handle duplication with explicit ownership. Files or types: new rendering interop helper, handoff handshake types. Done when exported, duplicated, imported, and closed handles have one documented owner and unit coverage.
- [ ] Add a typed versioned resource handshake for OpenVR GPU handoff. Files or types: `RuntimeVrState`, `EngineVrLifecycle`, `XREngine.VRClient`, new transport DTOs. Done when the handshake replaces any ad-hoc VR state scaffold and carries protocol version, generation, slot index, eye index, size, format, color space, memory size, memory handle, ready semaphore, release semaphore, producer process ID, and consumer process ID.
- [ ] Send shared handles only during create and recreate. Files or types: producer and consumer transport services. Done when steady-state frames carry bounded metadata and never transfer memory or semaphore handles.
- [ ] Add reconnect handling. Files or types: producer and consumer transport services, `XREngine.VRClient/Program.cs`. Done when the engine destroys and recreates shared resources after VRClient exit or restart.
- [ ] Add one-time unsupported-state diagnostics. Files or types: producer and consumer diagnostics. Done when unsupported interop, import failure, GPU mismatch, stale generation, and broken pipe states each emit an actionable diagnostic without log spam.

### Engine Producer

- [ ] Add an engine-side OpenVR GPU handoff producer. Files or types: new `OpenVrGpuHandoffProducer`, `RuntimeVrState`, `EngineVrLifecycle`. Done when the engine can own shared per-eye frame slots for the OpenVR client path.
- [ ] Allocate shared per-eye frame slots with Vulkan external-image helpers. Files or types: producer service, neutral rendering interop helpers. Done when each eye has shared color slots with stable generation and slot metadata.
- [ ] Import shared images into the engine OpenGL renderer. Files or types: OpenGL texture import path, `XRTexture2D`, producer service. Done when shared images can be wrapped as renderable `XRTexture2D` objects.
- [ ] Build per-eye framebuffer wrappers for imported textures. Files or types: `XRFrameBuffer`, `XRMaterialFrameBuffer`, producer service. Done when the OpenVR two-pass path can render to shared left and right framebuffers.
- [ ] Render directly into shared per-eye framebuffers when supported. Files or types: `EngineVrLifecycle.RenderTwoPass`, producer service. Done when the active OpenVR two-pass path writes the shared eye textures directly.
- [ ] Add a fallback GPU copy or resolve path. Files or types: `EngineVrLifecycle`, producer service, OpenGL copy/resolve helper. Done when existing `VRLeftEyeRenderTarget` and `VRRightEyeRenderTarget` can be copied or resolved into a shared slot without CPU readback.
- [ ] Add a non-blocking slot policy. Files or types: producer service. Done when a busy slot causes a documented drop or reuse choice instead of a normal-path CPU wait.
- [ ] Track producer frame metadata. Files or types: producer service, transport DTOs. Done when frame index, slot index, predicted display time or offset, pose sample IDs, render resolution, and generation are recorded.
- [ ] Recreate producer resources on runtime changes. Files or types: producer service, `RuntimeVrState`, `EngineVrLifecycle`. Done when HMD recommended-size, color format, VR mode, GPU device, and client reconnect changes recreate shared resources safely.

### VRClient Consumer

- [ ] Add a VRClient-side OpenVR GPU handoff consumer. Files or types: new `OpenVrGpuHandoffConsumer`, `XREngine.VRClient/Program.cs`. Done when the VRClient receives handshakes and owns imported objects for compositor submission.
- [ ] Import duplicated memory handles into the VRClient OpenGL context. Files or types: consumer service, OpenGL texture import path. Done when each duplicated memory handle backs a local GL texture object.
- [ ] Import ready and release semaphores into the VRClient OpenGL context. Files or types: consumer service, OpenGL semaphore import helper. Done when the consumer can wait and signal GPU semaphores.
- [ ] Wait on ready semaphores before compositor submission. Files or types: consumer service, `OpenVrCompositorBackend`. Done when a slot is submitted only after producer rendering is complete.
- [ ] Submit imported local texture names through the existing OpenVR compositor path. Files or types: `OpenVrCompositorBackend`, consumer service. Done when `SubmitEyes(...)` receives local OpenGL texture names and the compositor uses `ETextureType.OpenGL`.
- [ ] Preserve `PostPresentHandoff()` timing. Files or types: `OpenVrCompositorBackend`, consumer service. Done when `PostPresentHandoff()` still runs immediately after the right-eye submit.
- [ ] Signal release semaphores after OpenVR submission. Files or types: consumer service. Done when the producer can safely reuse a slot after the consumer release signal.
- [ ] Reject stale generations. Files or types: consumer service. Done when generation changes cannot submit stale textures.
- [ ] Tear down imported objects on disconnect, resize, format change, and shutdown. Files or types: consumer service. Done when imported GL objects, memory objects, and semaphores are disposed deterministically.

### Pose And Timing Alignment

- [ ] Keep OpenVR input and prediction in the VRClient process. Files or types: `XREngine.VRClient/Program.cs`, OpenVR input services, transport DTOs. Done when the handoff does not move OpenVR input ownership into the engine process.
- [ ] Send predicted poses from VRClient to the engine. Files or types: VRClient transport, engine producer transport. Done when HMD and controller poses include frame IDs and prediction timing metadata.
- [ ] Bind rendered frames to pose sample IDs. Files or types: producer service, transport DTOs. Done when each shared slot identifies the pose sample used for rendering.
- [ ] Keep the producer queue shallow. Files or types: producer service. Done when queue depth is configurable or fixed by policy and prevents old-pose display.
- [ ] Add timing instrumentation. Files or types: producer service, consumer service, VR stats. Done when pose sample time, render start/end, ready signal, ready wait, `PostPresentHandoff()` time, and compositor timing are reported.

### Tests And Diagnostics

- [ ] Add tests for transport metadata serialization and version handling. Files or types: `XREngine.UnitTests`, transport DTOs. Done when incompatible protocol versions and missing fields are rejected.
- [ ] Add tests that forbid CPU image readback and per-frame handle transfer in the handoff path. Files or types: `XREngine.UnitTests`, producer and transport services. Done when tests fail if the path uses readback APIs or per-frame handle transfer.
- [ ] Add tests for generation mismatch and reconnect transitions. Files or types: `XREngine.UnitTests`, producer and consumer state machines. Done when stale generations and reconnects are covered.
- [ ] Add runtime diagnostics for slot starvation, dropped frames, stale submission, and compositor submit errors. Files or types: producer service, consumer service, `OpenVrCompositorBackend`, VR stats. Done when each condition has a stable diagnostic key.
- [ ] Add profiler counters for the handoff. Files or types: VR stats, producer service, consumer service. Done when slot count, current slot, ready-wait duration, producer blocked/dropped count, GPU copy/resolve duration, and submit-to-handoff timing are reported.
- [ ] Document implementation environment toggles after they exist. Files or types: relevant user or developer guide. Done when enable/disable, slot count, direct-render versus blit mode, and verbose diagnostics settings are documented.

## Decisions Needed

- [ ] Choose the MVP color formats. Owner: Rendering / XR.
- [ ] Decide when `RGBA16f` HDR eye color is required. Owner: Rendering / XR.
- [ ] Choose the initial buffering model. Owner: Rendering / XR.
- [ ] Decide whether direct shared-FBO rendering is required for MVP or whether one GPU blit/resolve is acceptable for first hardware validation. Owner: Rendering / XR.
- [ ] Decide whether late-latching-style pose updates are possible in the current OpenVR path or future work. Owner: Rendering / XR.

## Out Of Scope

- CPU pixel pipes, named-pipe pixel transfer, TCP pixel transfer, shared CPU-memory frame transfer, screenshots, and image encoders.
- Submitting Vulkan opaque Win32 external-memory handles as OpenVR `DXGISharedHandle` values.
- Migrating the whole renderer to Vulkan.
- Moving OpenVR lifetime back into the main app.
- XR multiview, OpenXR swapchains, Linux FD handles, and remote-machine streaming for the first milestone.
- Depth-assisted reprojection until color handoff is stable.
- Manual hardware validation, latency measurement, and compositor recovery checks. These live in the validation doc.
