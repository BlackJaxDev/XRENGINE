# OpenXR Runtime

XREngine includes an OpenXR runtime path alongside the older OpenVR path. OpenVR remains the currently tested day-to-day VR path, while OpenXR is implemented for engine integration, validation, and runtime portability work.

This feature doc promotes the implemented reference review from `docs/work/design/VR/openxr-implementation-comparison.md`.

## Startup Behavior

VR startup is controlled by `VRGameStartupSettings` and the selected `EVRRuntime`.

- `OpenXR` forces the OpenXR path. If initialization fails, VR startup fails visibly with diagnostics.
- `OpenVR` uses the existing OpenVR path.
- `Auto` can try OpenXR first and fall back to OpenVR when configured.

The Unit Testing World can request OpenXR with `UseOpenXR: true`.

## Frame Lifecycle

OpenXR follows the standard runtime-owned swapchain lifecycle:

1. Poll runtime events and session state.
2. Wait and begin the next frame.
3. Locate predicted views for visible collection.
4. Build per-eye visibility from predicted poses/FOV.
5. Locate late views near render time.
6. Acquire, wait, render, flush, and release each swapchain image.
7. Submit an OpenXR projection layer with per-eye pose and FOV.

The implementation keeps OpenXR calls on the render side while allowing the engine's visible-collection work to use predicted views.

## Pose Timing

The runtime maintains predicted and late pose caches for:

- HMD views,
- eye FOV,
- controllers,
- and trackers/user paths.

Callers pass an explicit runtime pose timing when asking VR transforms to update render matrices. This avoids process-global timing switches and lets update, collection, and rendering readers use the correct pose cache for their phase.

## OpenGL Swapchain Safety

The OpenXR OpenGL path avoids forced WGL context switching from arbitrary threads. Session setup is deferred until the render side can safely initialize GL-backed swapchains.

Per-eye rendering uses:

- acquire/wait/release discipline,
- release in `finally` paths,
- GL flush before release,
- viewport/scissor/mask sanitation,
- and state restoration to avoid contaminating desktop rendering.

## Tracking Loss And Diagnostics

Engine settings expose OpenXR pose, tracking-loss, action-sync, pacing, and diagnostic policies. Debug options include frame lifecycle logging, OpenGL diagnostics, eye-order testing, and clear-only eye rendering for swapchain verification.

## Implementation References

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Pacing.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs`
- `XREngine/Engine/Engine.VRState.cs`
- `XREngine/Engine/Engine.RuntimeVrStateServices.cs`
- `XREngine.Runtime.Rendering/Rendering/Camera/XROpenXRFovCameraParameters.cs`
- `XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRDeviceTransformBase.cs`
- `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs`
