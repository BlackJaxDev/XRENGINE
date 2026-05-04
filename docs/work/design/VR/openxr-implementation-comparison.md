# OpenXR Implementation Comparison

**Generated:** 2026-01-13  
**Status:** Implementation Review Updated

This document compares the XREngine OpenXR implementation against:
1. The official [OpenXR Tutorial](https://openxr-tutorial.com/android/vulkan/3-graphics.html)
2. The existing OpenVR implementation in the engine

---

## Summary

The OpenXR implementation is **architecturally sound** and follows the tutorial pattern correctly. Recent updates made it significantly more robust in the engine's multi-threaded render model:
- Predicted **and** late pose sampling (`LocateViews(Predicted)` for culling, `LocateViews(Late)` right before rendering)
- Central pose/FOV caches (HMD, per-eye, controllers, trackers) with a timing selector (`PoseTimingForRecalc`)
- Safer OpenGL execution (no forced WGL context switching, deferred GL session init on the render thread)
- Stronger swapchain safety (infinite wait timeout, always-release-on-finally, GL flush)
- GL state isolation to avoid contaminating desktop rendering and cross-eye state leakage

---

## Comparison Table: Correctly Implemented

| Aspect | Tutorial | Current Code | OpenVR Equivalent |
|--------|----------|--------------|-------------------|
| **Frame Timing** | `xrWaitFrame` → `xrBeginFrame` → render → `xrEndFrame` | ✅ `WaitFrame()` → `BeginFrame()` → render → `Api.EndFrame()` | `WaitGetPoses()` → render → `Submit()` |
| **View Location** | `xrLocateViews` with `predictedDisplayTime` and `space` | ✅ `LocateViews()` uses `_frameState.PredictedDisplayTime` & `_appSpace` | `WaitGetPoses()` returns device poses |
| **Reference Space** | `XR_REFERENCE_SPACE_TYPE_LOCAL` with identity pose | ✅ `ReferenceSpaceType.Local` with identity | `TrackingUniverseStanding` |
| **Projection Layer** | `XrCompositionLayerProjection` with `Space = referenceSpace` | ✅ `layer.Space = _appSpace` | N/A (implicit in runtime) |
| **ProjectionView Setup** | `.Pose = views[i].pose`, `.Fov = views[i].fov` | ✅ `projectionViews[i].Pose/Fov = _views[i].Pose/Fov` | N/A (implicit) |
| **Swapchain Timeout** | `XR_INFINITE_DURATION` | ✅ `long.MaxValue` | N/A |
| **Camera Pose** | Render from `views[i].pose` position/orientation | ✅ `UpdateOpenXrEyeCameraFromView` sets camera transform from `_views[viewIndex].Pose` | `RenderDeviceToAbsoluteTrackingMatrix` × `GetEyeToHeadTransform` |
| **Projection Matrix** | Use `views[i].fov` angles | ✅ `XROpenXRFovCameraParameters.SetAngles(...)` uses FOV from `_views` | `CVR.GetProjectionMatrix()` |
| **GPU Sync Before Release** | `EndRendering()` blocks until GPU done | ✅ `_gl.Flush()` before release | N/A |
| **Swapchain Safety** | Always release acquired image | ✅ Release in `finally` (even on exception) | N/A |
| **Late Pose Update** | Sample poses close to render | ✅ `LocateViews(Late)` + action pose cache refresh on render thread | Typical `WaitGetPoses()` timing |
| **Device Pose Integration** | HMD/controllers tracked per-frame | ✅ VR transforms pull from OpenXR caches (HMD, controllers, trackers via user paths) | Device matrices from OpenVR |
| **GL State Isolation** | Keep engine state stable | ✅ GL snapshot restore + sanitation; per-eye scissor/mask guards | N/A |

---

## Key Implementation Details

### Frame Loop (Engine Thread Split)

```
Render thread (end of frame):
1. PollEvents()                    - Handle session state changes
2. (Optional) LocateViews(Late)    - Late-update tracked poses & FOV
3. InvokeRecalcMatrixOnDraw(Late)  - Let VR transforms refresh render matrices
4. RenderFrame()                   - Acquire/Wait/Render/Release per-eye swapchain images
5. EndFrame()                      - Submit CompositionLayerProjection
6. PrepareNextFrameOnRenderThread():
    a. WaitFrame()                  - Get predictedDisplayTime + shouldRender
    b. BeginFrame()                 - Signal frame start to runtime
    c. LocateViews(Predicted)       - Predicted per-eye view poses & FOV
    d. UpdateActionPoseCaches(Predicted) - Predicted controller/tracker poses
    e. InvokeRecalcMatrixOnDraw(Predicted) - Update VR rig for CollectVisible

CollectVisible thread:
7. OpenXrCollectVisible()           - Build per-eye buffers using predicted views
8. OpenXrSwapBuffers()              - Publish buffers; set _framePrepared=1
```

### Render Loop (RenderFrame → RenderEye)

```
1. Check _framePrepared
2. For each eye:
   a. AcquireSwapchainImage()
   b. WaitSwapchainImage(timeout=INFINITE)
   c. Bind FBO, set viewport
    d. Sanitize per-eye GL state (scissor/masks)
    e. Render scene with OpenXR camera
    f. Flush GL
    g. ReleaseSwapchainImage() (always, via `finally`)
    h. Fill projectionViews[i] with pose/fov from _views[i]
3. EndFrame() with CompositionLayerProjection
```

### Camera Transform Setup

**OpenXR approach** (current implementation):
```csharp
// UpdateOpenXrEyeCameraFromView() - line ~1445
Vector3 eyePos = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
Quaternion eyeRot = Quaternion.Normalize(new Quaternion(
    pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W));

Matrix4x4 eyeWorldMatrix = Matrix4x4.CreateFromQuaternion(eyeRot);
eyeWorldMatrix.Translation = eyePos;

camera.Transform.SetRenderMatrix(eyeWorldMatrix, recalcAllChildRenderMatrices: false);
```

**OpenVR approach** (for reference):
```csharp
// VRDeviceTransformBase.VRState_RecalcMatrixOnDraw()
Matrix4x4 mtx = device.RenderDeviceToAbsoluteTrackingMatrix;  // HMD pose
// Then VREyeTransform applies GetEyeToHeadTransform() offset
```

**Key difference:** OpenXR returns complete per-eye poses directly from `xrLocateViews()`. OpenVR requires composing HMD pose with eye-to-head transform.

**VR rig integration (new):** if the app provides a VR rig (e.g., `VREyeTransform` / `VRHeadsetTransform`), the engine now:
- Updates predicted/late pose caches in `OpenXRAPI`
- Lets transforms pull from the correct cache via `PoseTimingForRecalc`
- Still applies a late render-matrix update to minimize latency

---

## Potential Improvements (Non-Critical)

### 1. ViewState Flags Check

The tutorial recommends checking tracking validity flags:

```csharp
// Current: Not checked
var viewState = new ViewState { Type = StructureType.ViewState };
Api.LocateView(..., &viewState, ...);

// Recommended addition:
if ((viewState.ViewStateFlags & ViewStateFlags.PositionValidBit) == 0 ||
    (viewState.ViewStateFlags & ViewStateFlags.OrientationValidBit) == 0)
{
    // Handle lost tracking
}
```

### 2. Session State Check Before Rendering

Tutorial pattern:
```csharp
bool sessionActive = (m_sessionState == XR_SESSION_STATE_SYNCHRONIZED || 
                      m_sessionState == XR_SESSION_STATE_VISIBLE || 
                      m_sessionState == XR_SESSION_STATE_FOCUSED);
if (sessionActive && frameState.shouldRender) {
    // Render
}
```

Current code gates on `_sessionBegun` and `_frameState.ShouldRender` (and ends frames with **no layers** when `ShouldRender == 0`), but does not explicitly check `SYNCHRONIZED/VISIBLE/FOCUSED` before attempting to prepare/render.

### 3. Depth Swapchain

Tutorial creates both color AND depth swapchains. Current implementation only creates color swapchains for OpenXR. This could affect depth testing in certain scenarios.

### 4. ViewState-Driven Tracking Loss Handling

Now that device/controller/tracker transforms are driven from OpenXR pose caches, it may be worth defining a consistent policy for invalid tracking:
- Freeze last valid pose vs. identity vs. hide/disable renderable
- Clear controller validity flags and tracker entries when the runtime reports invalid

---

## Architectural Comparison: OpenVR vs OpenXR

| Aspect | OpenVR | OpenXR |
|--------|--------|--------|
| **Pose Acquisition** | `WaitGetPoses()` returns HMD pose, then apply `GetEyeToHeadTransform()` | `xrLocateViews()` returns complete per-eye poses |
| **Pose Submission** | Implicit in `Submit()` call timing | Explicit in `CompositionLayerProjectionView.Pose` |
| **Frame Sync** | `WaitGetPoses()` blocks | `xrWaitFrame()` blocks |
| **Swapchain Management** | App-owned textures, submitted via handles | Runtime-owned swapchains, acquire/wait/release pattern |
| **Projection Matrix** | `GetProjectionMatrix(eye, near, far)` | FOV angles from `xrLocateViews()`, build matrix yourself |
| **Threading Model** | Single-threaded typical | Explicitly supports split CollectVisible/Render threads |

OpenXR in XREngine specifically keeps **xr* calls on the render thread** while allowing the engine's CollectVisible thread to build buffers from the predicted views.

---

## File References

| File | Purpose |
|------|---------|
| `XRENGINE/Rendering/API/Rendering/OpenXR/Init.cs` | OpenXR API construction + loader handling |
| `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs` | Render/CollectVisible thread split + frame submission |
| `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs` | Pose/FOV caches (predicted + late) + device pose API |
| `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs` | Core xr calls (`WaitFrame`, `BeginFrame`, `LocateViews`, events) |
| `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.NativeLoader.cs` | `openxr_loader.dll` resolution + active runtime detection |
| `XRENGINE/Rendering/Camera/XROpenXRFovCameraParameters.cs` | Asymmetric FOV projection matrix |
| `XRENGINE/Engine/Engine.VRState.cs` | OpenVR integration (reference) |
| `XRENGINE/Scene/Transforms/VR/VRDeviceTransformBase.cs` | VR transforms (OpenVR matrices and OpenXR pose cache integration) |
| `XRENGINE/Scene/Transforms/VR/VREyeTransform.cs` | OpenVR eye-to-head offset |
| `XRENGINE/Rendering/Camera/XROVRCameraParameters.cs` | OpenVR projection matrix |

---

## Coordinate Systems

Both OpenXR and XREngine use the same convention:
- **Right-handed** coordinate system
- **+Y** = Up
- **+X** = Right  
- **-Z** = Forward

Defined in `XREngine.Data/Globals.cs`:
```csharp
public static readonly Vector3 Forward = -Vector3.UnitZ;
public static readonly Vector3 Up = Vector3.UnitY;
public static readonly Vector3 Right = Vector3.UnitX;
```

This matches OpenXR's default coordinate system, so no coordinate conversion is needed.

---

## Troubleshooting

If rendering still doesn't look correct after these fixes, check:

1. **Frustum/Culling** - Is the render pipeline culling against the correct camera frustum?
2. **Matrix Usage** - Is the pipeline reading `camera.Transform.InverseRenderMatrix` for the view matrix?
3. **GL Context** - Are textures valid in the render thread's GL context?
4. **Debug Output** - Enable `OpenXrDebugClearOnly = true` to verify swapchain submission works with solid colors

---

## References

- [OpenXR Tutorial Chapter 3: Graphics](https://openxr-tutorial.com/android/vulkan/3-graphics.html)
- [OpenXR Specification - Frame Submission](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html)
- [OpenXR Specification - View Configurations](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html#view_configurations)
