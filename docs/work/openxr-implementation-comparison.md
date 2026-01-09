# OpenXR Implementation Comparison

**Generated:** 2026-01-08  
**Status:** Implementation Review Complete

This document compares the XREngine OpenXR implementation against:
1. The official [OpenXR Tutorial](https://openxr-tutorial.com/android/vulkan/3-graphics.html)
2. The existing OpenVR implementation in the engine

---

## Summary

The OpenXR implementation is **architecturally sound** and follows the tutorial pattern correctly. Key fixes have been applied:
- Direct OpenXR pose usage for camera transforms
- Infinite swapchain wait timeout (`long.MaxValue`)
- GL flush before swapchain image release

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

---

## Key Implementation Details

### Frame Loop (OpenXrSwapBuffers)

```
1. PollEvents()           - Handle session state changes
2. WaitFrame()            - Get predicted display time, shouldRender flag
3. BeginFrame()           - Signal frame start to runtime
4. LocateViews()          - Get per-eye poses & FOV for predictedDisplayTime
5. UpdateOpenXrEyeCameraFromView() - Set camera transforms from OpenXR poses
6. CollectVisible()       - Engine culling/visibility
7. SwapBuffers()          - Engine buffer swap
8. Set _framePrepared=1   - Signal render thread
```

### Render Loop (RenderFrame → RenderEye)

```
1. Check _framePrepared
2. For each eye:
   a. AcquireSwapchainImage()
   b. WaitSwapchainImage(timeout=INFINITE)
   c. Bind FBO, set viewport
   d. Render scene with OpenXR camera
   e. Flush GL
   f. ReleaseSwapchainImage()
   g. Fill projectionViews[i] with pose/fov from _views[i]
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

Current code checks `_sessionBegun` and `_frameState.ShouldRender` but not specific session states.

### 3. Depth Swapchain

Tutorial creates both color AND depth swapchains. Current implementation only creates color swapchains for OpenXR. This could affect depth testing in certain scenarios.

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

---

## File References

| File | Purpose |
|------|---------|
| `XRENGINE/Rendering/API/Rendering/OpenXR/Init.cs` | Main OpenXR integration |
| `XRENGINE/Rendering/Camera/XROpenXRFovCameraParameters.cs` | Asymmetric FOV projection matrix |
| `XRENGINE/Engine/Engine.VRState.cs` | OpenVR integration (reference) |
| `XRENGINE/Scene/Transforms/VR/VRDeviceTransformBase.cs` | OpenVR device transforms |
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
