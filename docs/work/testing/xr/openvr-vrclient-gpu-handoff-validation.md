# OpenVR VRClient GPU Handoff Validation

Scope: Validate a zero-readback GPU handoff from the engine process to `XREngine.VRClient` for OpenVR compositor submission.

Architecture links:

- [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md)
- [OpenVR Rendering](../../../architecture/rendering/openvr-rendering.md)
- [Vulkan Upscale Bridge](../../../developer-guides/rendering/vulkan-upscale-bridge.md)

Code todo links:

- [OpenVR VRClient GPU Handoff TODO](../../todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md)

## Setup

Use Windows with SteamVR and the legacy `XREngine.VRClient` companion process. Use the ImGui editor as the engine process. Use OpenGL compositor submission in `XREngine.VRClient`. Use the Vulkan external-memory helper path only as the shared-allocation mechanism.

Tasks from `.vscode/tasks.json`:

- `Build-Editor`: build the engine/editor process.
- `Build-VRClient`: build `XREngine.VRClient`.
- `Prep-DebugVRClient-WithEditor`: start the editor without debug, then build `XREngine.VRClient`.

Launch profiles from `.vscode/launch.json`:

- `Editor (Default World)`: start the editor process.
- `Debug VRClient (Editor runs separately)`: start `XREngine.VRClient` with `XRE_VRCLIENT_GAMENAME=XREngine.Editor`.

Settings and environment variables:

- Use `XRE_VRCLIENT_GAMENAME=XREngine.Editor` to pair the VRClient with the editor process.
- Record any future handoff enable flag, slot-count setting, direct-render setting, blit setting, and verbose-diagnostics flag here after implementation.
- Use SteamVR compositor timing for OpenVR timing checks.
- Use vendor GPU tools or RenderDoc only after the process-pair handoff is stable.

## Checks

### Interop Capability

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Probe OpenGL external memory in both processes. | Start the editor and `XREngine.VRClient`. Record support for `GL_EXT_memory_object` and `GL_EXT_memory_object_win32`. | Both processes report the required OpenGL memory-object support, or a visible unsupported reason appears. | Pending. | none |
| Probe OpenGL external semaphores in both processes. | Start the editor and `XREngine.VRClient`. Record support for `GL_EXT_semaphore` and `GL_EXT_semaphore_win32`. | Both processes report the required OpenGL semaphore support, or a visible unsupported reason appears. | Pending. | none |
| Probe Vulkan external memory and semaphores. | Record support for `VK_KHR_external_memory`, `VK_KHR_external_memory_win32`, `VK_KHR_external_semaphore`, and `VK_KHR_external_semaphore_win32`. | The producer path reports support, or a visible unsupported reason appears. | Pending. | none |
| Validate GPU identity. | Compare the OpenGL renderer GPU, Vulkan sidecar physical GPU, and SteamVR compositor adapter. | All paths use the same physical GPU, or the handoff is rejected with a visible GPU mismatch reason. | Pending. | none |
| Validate imported memory with OpenVR submission. | Submit GL textures whose storage comes from imported external memory. | SteamVR accepts left and right textures. The compositor result is recorded. | Pending. | none |

### Resource Handshake

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate create and recreate handshake. | Start both processes, then trigger resize or format change. | Handles transfer only during create or recreate. Per-frame handle transfer does not occur. | Pending. | none |
| Validate handle ownership. | Inspect logs for exported, duplicated, imported, and closed handles across normal shutdown. | Each handle has one owner at each step and is closed once. | Pending. | none |
| Validate generation changes. | Recreate resources while frames are in flight. | The VRClient rejects stale generation data and does not submit stale textures. | Pending. | none |
| Validate client reconnect. | Stop and restart `XREngine.VRClient` while the editor keeps running. | The engine recreates shared resources and the client imports the new generation. Neither process crashes. | Pending. | none |

### Engine Producer

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate shared slot allocation. | Enable the handoff and start both processes. | The engine creates per-eye shared slots with the selected buffer count and format. | Pending. | none |
| Validate direct shared-FBO rendering. | Run the OpenVR two-pass path in direct-render mode when implemented. | The engine renders left and right eyes into shared FBOs without a CPU copy. | Pending. | none |
| Validate fallback GPU blit or resolve. | Run the fallback path from the existing eye render targets. | One GPU copy or resolve writes each eye into the shared slot. No CPU readback or upload occurs. | Pending. | none |
| Validate slot reuse policy. | Stall the consumer or run under load. | The producer drops or reuses slots according to the documented low-latency policy. It does not wait on the CPU in the normal path. | Pending. | none |
| Validate producer metadata. | Inspect frame metadata. | Frame index, slot index, predicted display time or offset, pose sample IDs, render resolution, and generation are present. | Pending. | none |

### VRClient Consumer

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Launch separate processes. | Run `Editor (Default World)` and `Debug VRClient (Editor runs separately)`. | The editor and `XREngine.VRClient` run as separate processes. | Pending. | none |
| Validate VRClient OpenVR ownership. | Inspect startup and runtime logs. | `XREngine.VRClient` owns OpenVR initialization, input polling, compositor submission, and `PostPresentHandoff()`. | Pending. | none |
| Validate imported texture creation. | Inspect VRClient import logs. | The VRClient creates local GL texture objects backed by imported memory objects. | Pending. | none |
| Validate semaphore wait and release signal. | Inspect GPU synchronization logs or capture. | The VRClient waits on the ready semaphore before submission and signals release after submission. | Pending. | none |
| Validate compositor submission. | Inspect compositor results. | The VRClient submits local left/right GL texture names with `ETextureType.OpenGL` and calls `PostPresentHandoff()` after the pair. | Pending. | none |
| Validate teardown. | Disconnect, resize, format-change, and exit the VRClient. | Imported GL objects, semaphores, and memory objects are destroyed without stale use. | Pending. | none |

### Pose And Timing

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate pose transfer. | Record predicted HMD and controller pose metadata from the VRClient to the engine. | The engine renders against the pose sample for the submitted frame slot. | Pending. | none |
| Validate shallow queue depth. | Run under load and inspect slot age. | The producer queue stays shallow and avoids old poses. | Pending. | none |
| Validate timing instrumentation. | Inspect timing counters. | Pose sample time, engine render start/end, ready signal, VRClient wait, `PostPresentHandoff()` time, and compositor timing are recorded. | Pending. | none |
| Evaluate late-latching feasibility. | Run a timing experiment after the basic handoff is stable. | The result states whether late-latching-style pose updates are possible or future work. | Pending. | none |

### Runtime Behavior And Recovery

Feature architecture: [OpenVR VRClient GPU Handoff](../../../architecture/rendering/openvr-vrclient-gpu-handoff.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate repeated startup and shutdown. | Start and stop `XREngine.VRClient` repeatedly while the editor keeps running. | The system recovers and does not leak stale shared resources. | Pending. | none |
| Validate render-target resize. | Change HMD recommended size or SteamVR supersampling. | Shared resources recreate at the new size. Both processes continue. | Pending. | none |
| Validate window state changes. | Alt-tab, minimize, restore, and sleep/wake the monitor. | The handoff reports recoverable state changes and continues or disables itself safely. | Pending. | none |
| Validate compositor errors. | Force or observe compositor submit errors. | Each error is reported once with actionable state. | Pending. | none |
| Compare latency. | Compare the handoff with current local OpenVR rendering and any existing client-mode transport. | Latency data shows the cost or benefit of the handoff. | Pending. | none |
| Capture GPU timings. | Measure direct shared-FBO render and fallback GPU blit/resolve. | GPU timing data is recorded for both paths. | Pending. | none |

## Hardware Matrix

| Target | Required hardware or runtime | Checks |
|---|---|---|
| Windows OpenGL plus Vulkan interop | GPU and driver with GL memory objects, GL semaphores, Vulkan external memory, and Vulkan external semaphores | Capability, resource handshake, producer, consumer |
| SteamVR/OpenVR HMD | SteamVR runtime with OpenVR compositor access | VRClient ownership, compositor submission, timing, recovery |
| Multi-GPU system | System with more than one adapter | GPU identity rejection and diagnostics |
| Resize and lifecycle stress | HMD supersampling, window state changes, process restart | Resource recreate, reconnect, teardown |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
