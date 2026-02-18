# OpenXR VR Rendering

This document describes how XREngine integrates OpenXR for VR rendering — from session creation through per-eye swapchain management to frame submission — and how it hooks into both the OpenGL and Vulkan renderers.

## Table of Contents

- [Overview](#overview)
- [Source File Inventory](#source-file-inventory)
- [VR Startup Flow](#vr-startup-flow)
  - [Engine.InitializeVR()](#engineinitializevr)
  - [Engine.VRState](#enginevrstate)
  - [VR Runtime Selection](#vr-runtime-selection)
- [OpenXR Runtime State Machine](#openxr-runtime-state-machine)
  - [States and Transitions](#states-and-transitions)
  - [Session Creation](#session-creation)
  - [Graphics Binding Selection](#graphics-binding-selection)
  - [OpenGL Deferred Initialization](#opengl-deferred-initialization)
- [OpenXR Session Creation per Backend](#openxr-session-creation-per-backend)
  - [OpenGL Session (WGL)](#opengl-session-wgl)
  - [Vulkan Session](#vulkan-session)
- [Swapchain Creation](#swapchain-creation)
  - [OpenGL Swapchains](#opengl-swapchains)
  - [Vulkan Swapchains](#vulkan-swapchains)
- [Frame Lifecycle](#frame-lifecycle)
  - [Three-Phase Frame Model](#three-phase-frame-model)
  - [Phase 1: PrepareNextFrameOnRenderThread](#phase-1-preparenextframeonrenderthread)
  - [Phase 2: CollectVisible](#phase-2-collectvisible)
  - [Phase 3: RenderFrame](#phase-3-renderframe)
  - [Late-Pose Update](#late-pose-update)
- [Per-Eye Rendering](#per-eye-rendering)
  - [RenderEye()](#rendereye)
  - [OpenGL Eye Rendering Path](#opengl-eye-rendering-path)
  - [Mirror FBO and Blit Pipeline](#mirror-fbo-and-blit-pipeline)
- [Desktop Mirror Composition](#desktop-mirror-composition)
- [VR Camera Parameters](#vr-camera-parameters)
- [Input Integration](#input-integration)
- [VR Scene Graph](#vr-scene-graph)
- [Editor Integration](#editor-integration)

---

## Overview

The OpenXR integration is implemented as the `OpenXRAPI` partial class, split across 12 files under `XRENGINE/Rendering/API/Rendering/OpenXR/`. It provides:

- **Runtime-agnostic VR** via the OpenXR standard (works with SteamVR, Oculus, WMR, and other conformant runtimes)
- **Dual graphics backend support** — binds to either the OpenGL or Vulkan renderer via the `IXrGraphicsBinding` strategy pattern
- **Late-latch pose updates** — samples headset/controller poses twice per frame (predicted + late) to minimize motion-to-photon latency
- **Three-phase frame model** — separates frame preparation, visibility collection, and rendering across threads
- **Parallel eye rendering** — supports concurrent left/right eye rendering on Vulkan when multiple graphics queues are available

---

## Source File Inventory

| File | Purpose |
|------|---------|
| `OpenXRAPI.State.cs` | Core state: session, system, views, frame state, pose caches (483 lines) |
| `OpenXRAPI.EngineIntegration.cs` | Engine↔OpenXR bridge: window property, `Initialize()` (97 lines) |
| `OpenXRAPI.RuntimeStateMachine.cs` | State machine: Desktop → Instance → System → Session → Running (320 lines) |
| `OpenXRAPI.XrCalls.cs` | Low-level OpenXR calls: `CreateSystem`, `BeginFrame`, `WaitFrame`, `LocateViews` (329 lines) |
| `OpenXRAPI.FrameLifecycle.cs` | Frame lifecycle: `RenderFrame`, `RenderEye`, `CollectVisible`, `SwapBuffers` (825 lines) |
| `OpenXRAPI.OpenGL.cs` | OpenGL binding: session, swapchains, FBOs, blit pipeline, mirror (931 lines) |
| `OpenXRAPI.OpenGL.Wgl.cs` | WGL P/Invoke helpers (13 lines) |
| `OpenXRAPI.Vulkan.cs` | Vulkan binding: session, swapchains, parallel partitions (173 lines) |
| `OpenXRAPI.Input.cs` | Action sets, hand/tracker poses, interaction profile bindings (503 lines) |
| `OpenXRAPI.NativeLoader.cs` | Native DLL resolver: probes SteamVR, Oculus, registry paths (~170 lines) |
| `OpenXRAPI.IPD.cs` | IPD measurement from eye view positions (40 lines) |
| `XrGraphicsBindings.cs` | `IXrGraphicsBinding` interface + OpenGL/Vulkan implementations (109 lines) |

---

## VR Startup Flow

### Engine.InitializeVR()

During `Engine.Initialize()` (in `Engine.Lifecycle.cs`), VR is started asynchronously after windows are created:

```csharp
if (startupSettings is IVRGameStartupSettings vrSettings)
    Task.Run(async () => await InitializeVR(vrSettings, startupSettings.RunVRInPlace));
```

Windows must be created first because VR session creation requires a live graphics context (OpenGL) or device handles (Vulkan).

The `InitializeVR` method in `Engine.Networking.cs` dispatches based on the configured runtime:

| `EVRRuntime` | Behavior |
|---|---|
| `Auto` | Tries OpenXR first; if it fails, falls back to OpenVR |
| `OpenXR` | Forces OpenXR only |
| `OpenVR` | Forces OpenVR/SteamVR only |

### Engine.VRState

`Engine.VRState` is a static nested class in `Engine.VRState.cs` (~1454 lines) that serves as the central VR state manager. It tracks:

```csharp
public enum VRRuntime { None, OpenVR, OpenXR }
private static VRRuntime _activeRuntime = VRRuntime.None;

public static bool IsOpenVRActive => _activeRuntime == VRRuntime.OpenVR;
public static bool IsOpenXRActive => _activeRuntime == VRRuntime.OpenXR;

private static OpenXRAPI? _openXRApi;   // OpenXR API wrapper
private static VR? _openVRApi;           // OpenVR.NET wrapper
```

Key members:

| Member | Purpose |
|--------|---------|
| `ViewInformation` | Tuple of `(LeftEyeCamera, RightEyeCamera, World, HMDNode)` for the active VR rig |
| `RealWorldIPD` / `ScaledIPD` | Interpupillary distance from runtime or `xrLocateViews` |
| `RecalcMatrixOnDraw` | Event that VR transforms subscribe to for per-frame pose updates |
| `InitRenderCallbacks(window)` | Installs VR rendering callbacks on the engine timer |
| `Render()` | Dispatches to OpenXR or OpenVR render path |

### VR Runtime Selection

The `EVRRuntime` enum and `IVRGameStartupSettings` interface are defined in `XRENGINE/Settings/VRGameStartupSettings.cs`:

```csharp
public enum EVRRuntime { Auto, OpenXR, OpenVR }

public interface IVRGameStartupSettings
{
    VrManifest? VRManifest { get; set; }
    IActionManifest? ActionManifest { get; }
    EVRRuntime VRRuntime { get; set; }
    string GameName { get; set; }
    (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
}
```

---

## OpenXR Runtime State Machine

### States and Transitions

The runtime state machine in `OpenXRAPI.RuntimeStateMachine.cs` manages the full lifecycle:

```
┌──────────────┐     ┌──────────────────┐     ┌──────────────┐
│ DesktopOnly  │────→│ XrInstanceReady  │────→│ XrSystemReady│
│              │     │                  │     │              │
│ Probe runtime│     │ Create system    │     │ Create       │
│ Create inst. │     │                  │     │ session +    │
└──────────────┘     └──────────────────┘     │ swapchains   │
       ↑                                      └──────┬───────┘
       │                                             │
       │         ┌──────────────────┐     ┌──────────▼───────┐
       │         │ SessionStopping  │←────│ SessionRunning   │
       ├─────────│                  │     │                  │
       │         │ Teardown         │     │ Active VR        │
       │         └──────────────────┘     └──────────────────┘
       │                                             │
       │         ┌──────────────────┐                │
       └─────────│ SessionLost /    │←───────────────┘
                 │ RecreatePending  │   (on error)
                 └──────────────────┘
```

The state machine ticks on `Engine.Time.Timer.PreUpdateFrame` and probes the runtime with 1.5-second retry intervals when no runtime is available.

### Session Creation

`TryCreateSessionAndSwapchains()` is the critical transition from `XrSystemReady` → `SessionCreated`:

1. Resolve the active renderer from the engine window
2. Select the graphics binding based on renderer type
3. Create the OpenXR session (requires active graphics context)
4. Create the XR reference space (typically `Stage` or `Local`)
5. Create per-eye swapchains
6. Initialize input action sets and bindings
7. Transition to `SessionCreated`

### Graphics Binding Selection

The binding is selected via pattern matching on the active renderer:

```csharp
IXrGraphicsBinding? selectedBinding = renderer switch
{
    VulkanRenderer => new VulkanXrGraphicsBinding(),
    OpenGLRenderer => new OpenGLXrGraphicsBinding(),
    _ => null
};
```

`IXrGraphicsBinding` is a strategy interface defined in `XrGraphicsBindings.cs`:

```csharp
internal interface IXrGraphicsBinding
{
    string BackendName { get; }
    bool IsCompatible(AbstractRenderer renderer);
    bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer);
    void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer);
    void CleanupSwapchains(OpenXRAPI api);
    void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer);
    // ... acquire/wait/release swapchain image wrappers
}
```

### OpenGL Deferred Initialization

OpenGL session creation requires the WGL context to be current, which is only guaranteed on the render thread. The state machine handles this by **deferring** initialization:

1. The state machine (running on the update thread) detects `XrSystemReady` state
2. Instead of creating the session immediately, it hooks a one-shot delegate (`_deferredOpenGlInit`) into `Window.RenderViewportsCallback`
3. On the next render frame, the delegate fires on the GL thread:
   - Creates the OpenGL session (with WGL HDC/HGLRC)
   - Creates the reference space
   - Creates swapchains (with GL FBOs)
   - Initializes input
   - Transitions to `SessionCreated`
4. The delegate unhooks itself after execution

This is not needed for Vulkan, which can create sessions from any thread since device handles are thread-safe.

---

## OpenXR Session Creation per Backend

### OpenGL Session (WGL)

`CreateOpenGLSession()` in `OpenXRAPI.OpenGL.cs`:

1. Cache the `GL` API handle from the `OpenGLRenderer`
2. Get the current WGL context handles via P/Invoke:
   ```csharp
   IntPtr hdc = wglGetCurrentDC();
   IntPtr hglrc = wglGetCurrentContext();
   ```
3. Query `GraphicsRequirementsOpenGLKHR` via the `KHR_opengl_enable` extension to get min/max supported GL versions
4. Validate the current GL version against runtime requirements
5. Build the graphics binding:
   ```csharp
   GraphicsBindingOpenGLWin32KHR binding = new()
   {
       Type = StructureType.GraphicsBindingOpenglWin32Khr,
       HDC = hdc,
       HGlrc = hglrc
   };
   ```
6. Try session creation with both current-context and window-reported GL handles (some runtimes are picky about which handle pair they accept)
7. Call `xrCreateSession` with the binding

**Important note:** The code carefully avoids calling `MakeCurrent` because switching to a non-sharing context can break texture state. It captures whatever context is already current.

### Vulkan Session

`CreateVulkanSession()` in `OpenXRAPI.Vulkan.cs`:

1. Query `GraphicsRequirementsVulkanKHR` via `KHR_vulkan_enable`
2. Detect multi-queue support on the `VulkanRenderer` for parallel eye rendering
3. Build the graphics binding:
   ```csharp
   GraphicsBindingVulkanKHR binding = new()
   {
       Type = StructureType.GraphicsBindingVulkanKhr,
       Instance = renderer.instance.Handle,
       PhysicalDevice = renderer.physicalDevice.Handle,
       Device = renderer.device.Handle,
       QueueFamilyIndex = renderer.GraphicsQueueFamilyIndex,
       QueueIndex = 0
   };
   ```
4. Call `xrCreateSession` with the binding

The Vulkan path is simpler because all handles (`VkInstance`, `VkPhysicalDevice`, `VkDevice`) are plain integers that don't require thread-local context.

---

## Swapchain Creation

Both backends create **one swapchain per eye** using `ViewConfigurationType.PrimaryStereo` (2 views).

### OpenGL Swapchains

`InitializeOpenGLSwapchains()` in `OpenXRAPI.OpenGL.cs`:

```
For each eye (0 = left, 1 = right):
│
├─ 1. Enumerate supported swapchain formats
│     Prefer: GL_SRGB8_ALPHA8 → GL_RGBA8 → first available
│
├─ 2. Create swapchain
│     xrCreateSwapchain(session, {
│         usageFlags: ColorAttachmentBit,
│         format: selectedFormat,
│         width: recommendedWidth,
│         height: recommendedHeight,
│         sampleCount: 1,
│         faceCount: 1,
│         arraySize: 1,
│         mipCount: 1
│     })
│     Tries multiple format × usage × sample-count combos until one succeeds
│
├─ 3. Enumerate swapchain images
│     xrEnumerateSwapchainImages → SwapchainImageOpenGLKHR[]
│     Each image has a .Image field = GL texture name (owned by the runtime)
│
└─ 4. Create per-image FBOs
      For each swapchain image:
        glGenFramebuffer()
        glBindFramebuffer(Framebuffer, fbo)
        glFramebufferTexture(Framebuffer, ColorAttachment0, image.Image, 0)
        glDrawBuffers([ColorAttachment0])
        glReadBuffer(ColorAttachment0)
      Store in _swapchainFramebuffers[eye][imageIndex]
```

The FBOs allow the engine to render directly into runtime-owned textures. Each `xrAcquireSwapchainImage` returns an index into this FBO array.

### Vulkan Swapchains

`InitializeVulkanSwapchains()` in `OpenXRAPI.Vulkan.cs`:

```
For each eye (0 = left, 1 = right):
│
├─ 1. Create swapchain
│     xrCreateSwapchain(session, {
│         usageFlags: ColorAttachmentBit,
│         format: VK_FORMAT_R8G8B8A8_SRGB (37),
│         width: recommendedWidth,
│         height: recommendedHeight,
│         sampleCount: 1
│     })
│
├─ 2. Enumerate swapchain images
│     xrEnumerateSwapchainImages → SwapchainImageVulkan2KHR[]
│     Each image has a .Image field = VkImage handle (owned by the runtime)
│
└─ 3. Build view partitions for parallel rendering
      BuildVulkanViewPartitions() creates work items:
        FullLeft, FullRight, [FoveatedLeft, FoveatedRight], [MirrorCompose]
```

Vulkan swapchain images are `VkImage` handles. The engine records render commands targeting these images in secondary command buffers, which are submitted as part of the frame's primary command buffer.

---

## Frame Lifecycle

### Three-Phase Frame Model

OpenXR frames follow a three-phase pipeline split across threads:

```
┌─────────────── Render Thread ────────────────────────────────┐
│  Phase 1: PrepareNextFrameOnRenderThread()                   │
│    xrWaitFrame → xrBeginFrame → LocateViews(Predicted)       │
│    UpdateActionPoseCaches(Predicted)                          │
│    InvokeRecalcMatrixOnDraw(Predicted)                        │
│    → Sets _pendingXrFrame = 1                                │
└──────────────────────────────────────────────────────────────┘
                    ↓ published to visibility thread
┌─────────────── Visibility Thread ────────────────────────────┐
│  Phase 2: OpenXrCollectVisible()                             │
│    Build per-eye visibility buffers (cull by frustum)         │
│    (Vulkan: left/right in parallel if multi-queue)           │
│  OpenXrSwapBuffers()                                         │
│    Swap viewport buffers → Sets _framePrepared = 1           │
└──────────────────────────────────────────────────────────────┘
                    ↓ buffers ready for render
┌─────────────── Render Thread ────────────────────────────────┐
│  Phase 3: Window_RenderViewportsCallback()                   │
│    PollEvents() → process session state transitions          │
│    LocateViews(Late) → UpdateActionPoseCaches(Late)          │
│    InvokeRecalcMatrixOnDraw(Late)                            │
│    RenderFrame():                                            │
│      For each eye:                                           │
│        xrAcquireSwapchainImage → xrWaitSwapchainImage        │
│        Render to swapchain FBO                               │
│        xrReleaseSwapchainImage                               │
│      CompositionLayerProjection (2 views)                    │
│      xrEndFrame                                              │
│    PrepareNextFrameOnRenderThread() → next frame             │
└──────────────────────────────────────────────────────────────┘
```

### Phase 1: PrepareNextFrameOnRenderThread

Called after the previous frame's `xrEndFrame` completes. Prepares the next frame:

1. Clear stale flags (`_framePrepared`, `_pendingXrFrameCollected`)
2. `xrWaitFrame()` — blocks until the runtime is ready for a new frame; returns predicted display time
3. `xrBeginFrame()` — marks the start of GPU work for this frame
4. If `ShouldRender == 0` (runtime says skip), sets `_frameSkipRender = 1` and returns
5. `LocateViews(Predicted)` — gets predicted eye poses for the display time
6. `UpdateActionPoseCaches(Predicted)` — samples controller/tracker poses at predicted time
7. `InvokeRecalcMatrixOnDraw()` — updates VR rig node transforms to predicted poses
8. Sets `_pendingXrFrame = 1` — makes the frame available to the visibility thread

### Phase 2: CollectVisible

Runs on the visibility/collect thread. Builds per-eye render command lists:

1. Guard on `_sessionBegun`, `_pendingXrFrame != 0`; uses atomic CAS to prevent double-collection
2. Resolve source viewport, VR rig, base camera, and world from `Engine.VRState.ViewInformation`
3. Ensure per-eye `XRViewport` and `XRCamera` objects exist (`EnsureOpenXrViewports()`, `EnsureOpenXrEyeCameras()`)
4. Copy post-process settings from the base camera to eye cameras
5. Update eye camera transforms and FOV from OpenXR view poses
6. **Collect visibility:**
   - **Vulkan (parallel):** If `_parallelRenderingEnabled`, collects left and right eyes concurrently via `Task.Run`
   - **OpenGL (serial):** Collects left then right sequentially
7. `OpenXrSwapBuffers()` swaps the viewport render buffers and sets `_framePrepared = 1`

### Phase 3: RenderFrame

Runs on the render thread as part of `Window_RenderViewportsCallback()`:

1. **Save GL state** (for OpenGL) — captures current FBO bindings, scissor, blend, depth test, cull face, etc.
2. **Poll OpenXR events** — handles session state transitions (ready, stopping, lost, etc.)
3. **Late-pose update** — if a frame is pending:
   - `LocateViews(Late)` — re-samples eye poses with updated prediction
   - `UpdateActionPoseCaches(Late)` — re-samples controller poses
   - Logs pose delta between predicted and late samples
   - `InvokeRecalcMatrixOnDraw()` — updates transforms to latest poses
4. **`RenderFrame()`** — submits the frame:
   - For each eye: acquire swapchain image, render, release
   - Build `CompositionLayerProjection` with both views
   - `xrEndFrame` with the projection layer
5. **`PrepareNextFrameOnRenderThread()`** — immediately begins the next frame
6. **Restore GL state** — puts the GL context back to the state the engine expects

### Late-Pose Update

The dual-sample pose strategy minimizes motion-to-photon latency:

| Phase | Pose Type | Used For |
|-------|-----------|----------|
| `PrepareNextFrame` | **Predicted** | Visibility collection (frustum culling, shadow maps) |
| `RenderFrame` | **Late** | Final eye transforms applied just before rendering |

Poses are double-buffered under `_openXrPoseLock`:
- Predicted pose: `_openXrPredictedLeftEyePos/Rot`, `_openXrPredictedLeftEyeFov`
- Late pose: `_openXrLateLeftEyePos/Rot`, `_openXrLateLeftEyeFov`

`ApplyOpenXrEyePoseForRenderThread()` composes the late eye pose with the locomotion root matrix and calls `camera.Transform.SetRenderMatrix()`, bypassing the normal transform pipeline for minimum latency.

---

## Per-Eye Rendering

### RenderEye()

`RenderEye()` in `OpenXRAPI.FrameLifecycle.cs` handles the per-eye swapchain acquire → render → release cycle:

```
RenderEye(viewIndex, renderCallback)
│
├─ xrAcquireSwapchainImage(swapchain[viewIndex]) → imageIndex
├─ xrWaitSwapchainImage(swapchain[viewIndex], timeout = long.MaxValue)
│
├─ [OpenGL path]
│   ├─ Bind _swapchainFramebuffers[viewIndex][imageIndex]
│   ├─ glViewport(0, 0, width, height)
│   ├─ Disable scissor test
│   ├─ Reset color/depth masks to write-all
│   ├─ renderCallback(textureHandle, viewIndex)
│   └─ glFlush()  ← ensures commands are flushed before release
│
├─ Build CompositionLayerProjectionView:
│   ├─ Pose = _views[viewIndex].Pose
│   ├─ Fov = _views[viewIndex].Fov
│   ├─ Swapchain = _swapchains[viewIndex]
│   ├─ ImageRect = (0,0) → (width, height)
│   └─ ImageArrayIndex = 0
│
└─ finally: xrReleaseSwapchainImage(swapchain[viewIndex])
            ↑ Always releases, even on render failure
```

### OpenGL Eye Rendering Path

The OpenGL eye rendering uses a two-stage approach: render to mirror FBO, then blit to swapchain.

`RenderViewportsToSwapchain()` in `OpenXRAPI.OpenGL.cs` is the render callback passed to `RenderEye()`:

```
RenderViewportsToSwapchain(textureHandle, viewIndex)
│
├─ 1. Save current GL FBO and scissor state
│
├─ 2. Set eye viewport world override
│     eyeViewport.WorldInstanceOverride = _openXrFrameWorld
│
├─ 3. Apply late-pose eye transform
│     ApplyOpenXrEyePoseForRenderThread(viewIndex)
│       ├─ Update FOV angles from late-sampled data
│       ├─ Compose eye local pose × locomotion root matrix
│       └─ camera.Transform.SetRenderMatrix(finalMatrix)
│
├─ 4. Render eye viewport to intermediate mirror FBO
│     eyeViewport.Render(_viewportMirrorFbo, world, camera, shadowPass: false)
│       └─ Executes the full render pipeline into _viewportMirrorFbo
│
├─ 5. Get mirror color texture GL handle
│     mirrorTextureId = _viewportMirrorColor.GetGLBindingId()
│
├─ 6. Blit: mirror FBO → swapchain FBO
│     ├─ Attach mirrorTextureId to _blitReadFbo (ColorAttachment0)
│     ├─ Attach textureHandle to _blitDrawFbo (ColorAttachment0)
│     ├─ glBlitFramebuffer(mirrorSize → swapchainSize)
│     └─ Filter: Nearest (if same size) or Linear (if different)
│
└─ 7. Restore previous GL FBO and scissor state
```

**Why the intermediate mirror FBO?** The swapchain textures are runtime-owned and may have format/layout constraints. The mirror FBO allows the engine to render with its normal pipeline (including post-processing) to a known-format texture, then blit to whatever format the runtime provided.

### Mirror FBO and Blit Pipeline

`EnsureViewportMirrorTargets()` creates the intermediate render target:

```csharp
_viewportMirrorFbo = new XRFrameBuffer(
    colorAttachment:  new XRTexture2D(width, height, Rgba8, linear, clamp),
    depthAttachment:  new XRRenderBuffer(width, height, Depth24Stencil8)
);
```

Two additional FBOs serve as blit source/target:
- `_blitReadFbo` — has the mirror color texture attached
- `_blitDrawFbo` — has the swapchain texture (from `xrAcquireSwapchainImage`) attached

The blit uses `glBlitFramebuffer` to copy pixels from mirror to swapchain. If the WGL context changes (e.g., due to runtime interaction), the blit FBOs are regenerated.

---

## Desktop Mirror Composition

When VR is active, the desktop window can display a mirror of what the headset sees. `TryRenderDesktopMirrorComposition()` in `OpenXRAPI.OpenGL.cs`:

```
TryRenderDesktopMirrorComposition(targetWidth, targetHeight)
│
├─ Attach _viewportMirrorColor to _blitReadFbo
├─ Bind FBO 0 as draw target (default framebuffer = screen)
├─ glBlitFramebuffer(mirrorSize → targetWidth × targetHeight)
└─ Returns true if mirror was available
```

This is activated in `XRWindow.RenderCallback` when:
- `Engine.VRState.IsInVR == true`
- `Engine.Rendering.Settings.RenderWindowsWhileInVR == true`
- `Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures == true`

---

## VR Camera Parameters

Two camera parameter classes handle VR projection:

### XROVRCameraParameters

`XRENGINE/Rendering/Camera/XROVRCameraParameters.cs` — Supports both runtimes via dual code path:

```csharp
public override Matrix4x4 CalculateProjectionMatrix()
{
    if (Engine.VRState.IsOpenXRActive)
    {
        // Read asymmetric FOV angles from OpenXRAPI.TryGetEyeFovAngles()
        return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, near, far);
    }
    else
    {
        // OpenVR: use CVR.GetProjectionMatrix(eye, near, far)
        return openVRProjectionMatrix;
    }
}
```

### XROpenXRFovCameraParameters

`XRENGINE/Rendering/Camera/XROpenXRFovCameraParameters.cs` — Purpose-built for OpenXR's asymmetric FOV model:

```csharp
public float AngleLeft { get; set; }    // radians, from XrFovf
public float AngleRight { get; set; }
public float AngleUp { get; set; }
public float AngleDown { get; set; }

public override Matrix4x4 CalculateProjectionMatrix()
{
    float left  = NearZ * MathF.Tan(AngleLeft);
    float right = NearZ * MathF.Tan(AngleRight);
    float down  = NearZ * MathF.Tan(AngleDown);
    float up    = NearZ * MathF.Tan(AngleUp);
    return Matrix4x4.CreatePerspectiveOffCenter(left, right, down, up, NearZ, FarZ);
}
```

---

## Input Integration

`OpenXRAPI.Input.cs` manages VR input:

### Action Set

A single action set `"xre_input"` is created with:
- `hand_grip_pose` — `PoseInput` type with left/right subaction paths
- `tracker_pose` — `PoseInput` type with 12 Vive tracker role user paths (feet, knees, elbows, chest, waist, etc.)

### Interaction Profiles

Default bindings are suggested for multiple controller types:

| Profile | Controllers |
|---------|-------------|
| `khr/simple_controller` | Generic two-button |
| `oculus/touch_controller` | Meta Quest / Rift |
| `valve/index_controller` | Valve Index (Knuckles) |
| `htc/vive_controller` | HTC Vive wands |
| `microsoft/motion_controller` | WMR controllers |
| `htc/vive_tracker_htcx` | Vive body trackers |

### Per-Frame Updates

`UpdateActionPoseCaches()` runs twice per frame (predicted + late):
1. `xrSyncActions` — synchronizes the action state
2. For each hand: `xrLocateSpace(gripSpace, appSpace, time)` → position/orientation
3. For each tracker role: `xrLocateSpace(trackerSpace, appSpace, time)` → position/orientation
4. Poses are stored double-buffered under `_openXrPoseLock`

---

## VR Scene Graph

The VR rig is built from specialized scene graph components:

### Transform Types (`XRENGINE/Scene/Transforms/VR/`)

| Transform | Function |
|-----------|----------|
| `VRHeadsetTransform` | Driven by HMD tracking data |
| `VREyeTransform` | Per-eye offset from headset |
| `VRControllerTransform` | Hand controller tracking |
| `VRTrackerTransform` | Body tracker tracking |
| `VRActionPoseTransform` | Arbitrary action-driven poses |
| `VRDeviceTransformBase` | Base for all tracked device transforms |

### Components (`XRENGINE/Scene/Components/VR/`)

| Component | Function |
|-----------|----------|
| `VRHeadsetComponent` | HMD presence and settings |
| `VRControllerModelComponent` | Renders controller models |
| `VRPlayerCharacterComponent` | VR player avatar/body |
| `VRTrackerCollectionComponent` | Manages body trackers |
| `VRPlayerInputSet` | Input bindings for VR player |
| `VRHeightScaleComponent` | Scales avatar to match real-world height |

### VRGameMode

`VRGameMode` (`XRENGINE/Game Modes/VRGameMode.cs`) is the default game mode for VR. It spawns:
- A `CharacterPawnComponent` with `RigidBodyTransform` as the player root
- `VRPlayerInputSet` for input handling
- HMD child node with `VRHeadsetTransform` + `VRHeadsetComponent`
- Left/Right controller nodes with `VRControllerTransform` + `VRControllerModelComponent`
- A tracker collection node with `VRTrackerCollectionComponent`

---

## Editor Integration

### EditorVR.cs

`XREngine.Editor/EditorVR.cs` (~1162 lines) configures the Editor's VR action manifest:

- Defines action categories: `Global`, `OneHanded`, `QuickMenu`, `Menu`, `AvatarMenu`
- Defines actions: `Interact`, `Jump`, `ToggleMute`, etc. with localized names in 12 languages
- Provides default bindings for Valve Index controllers (from `bindings_knuckles.json`)
- Sets up `VrManifest` with `AppKey = "XRE.VR.Test"`

### EditorOpenXrPawnSwitcher.cs

`XREngine.Editor/EditorOpenXrPawnSwitcher.cs` (~80 lines) handles automatic pawn switching:

- Subscribes to `Engine.VRState.OpenXRSessionRunningChanged`
- When the OpenXR session starts: switches the local player to possess a VR pawn (one with `VRPlayerInputSet`)
- When the OpenXR session stops: switches back to the desktop `EditorFlyingCameraPawnComponent`
- Enables seamless transition between desktop editing and VR preview

---

## See Also

- [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) — How windows and renderers are created at startup
- [OpenGL Renderer](opengl-renderer.md) — OpenGL-specific rendering details
- [Vulkan Renderer](vulkan-renderer.md) — Vulkan-specific rendering details
- [OpenVR Rendering](openvr-rendering.md) — Legacy OpenVR/SteamVR rendering path
