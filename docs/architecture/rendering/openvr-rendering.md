# OpenVR (SteamVR) Rendering

This document describes how XREngine integrates the OpenVR/SteamVR runtime for VR rendering — from initialization through per-eye rendering to compositor submission — and how it hooks into the OpenGL renderer to present eye views.

## Table of Contents

- [Overview](#overview)
- [Initialization](#initialization)
  - [InitSteamVR()](#initsteamvr)
  - [InitRender()](#initrender)
  - [Render Target Creation](#render-target-creation)
  - [InitRenderCallbacks()](#initrendercallbacks)
- [Rendering Modes](#rendering-modes)
  - [Two-Pass Rendering](#two-pass-rendering)
  - [Single-Pass Stereo Rendering](#single-pass-stereo-rendering)
- [Per-Frame Render Loop](#per-frame-render-loop)
  - [Update() — Prediction Timing](#update--prediction-timing)
  - [CollectVisible](#collectvisible)
  - [SwapBuffers](#swapbuffers)
  - [Render()](#render)
  - [Render Target Resizing](#render-target-resizing)
- [Compositor Submission](#compositor-submission)
  - [OpenGL Texture Handle Flow](#opengl-texture-handle-flow)
  - [SubmitRenders()](#submitrenders)
  - [Texture Bounds](#texture-bounds)
- [Frame Timing & Statistics](#frame-timing--statistics)
- [VR Modes (Local, Client, Server)](#vr-modes-local-client-server)
- [OpenVR Input](#openvr-input)
- [Relationship to OpenXR](#relationship-to-openxr)

---

## Overview

The OpenVR integration uses the `OpenVR.NET` wrapper library (a submodule at `Build/Submodules/OpenVR.NET/`) which wraps Valve's `Valve.VR` C# bindings. All OpenVR VR state management lives in `Engine.VRState` (`XRENGINE/Engine/Engine.VRState.cs`, ~1454 lines).

Key characteristics:
- **OpenGL-centric** — Submits native GL texture names directly to the `IVRCompositor`
- **Two rendering modes** — Two-pass (render each eye separately) or single-pass stereo (multi-view array texture)
- **Prediction-driven timing** — Computes `fSecondsFromNow` from display frequency and vsync timing for accurate pose prediction
- **Engine-owned render targets** — Unlike OpenXR (where the runtime owns swapchain textures), OpenVR renders to engine-created FBOs and submits texture handles to the compositor

---

## Initialization

### InitSteamVR()

The SteamVR runtime is initialized asynchronously via `InitSteamVR()`:

```csharp
private static async Task<bool> InitSteamVR(IActionManifest actionManifest, VrManifest vrManifest)
{
    var vr = OpenVRApi;
    vr.DeviceDetected += OnDeviceDetected;
    if (!vr.TryStart(EVRApplicationType.VRApplication_Scene))
    {
        Debug.LogWarning("Failed to initialize SteamVR.");
        return false;
    }
    _openVRApi = vr;
    _activeRuntime = VRRuntime.OpenVR;

    InstallApp(vrManifest);                    // Register app manifest with SteamVR
    vr.SetActionManifest(actionManifest);       // Load action definitions
    CreateActions(actionManifest, vr);          // Bind actions to engine input
    Time.Timer.PreUpdateFrame += Update;        // Hook per-frame prediction timing
    IsInVR = true;
    return true;
}
```

This calls `EVRApplicationType.VRApplication_Scene` which tells SteamVR this is a rendering application (not an overlay or utility).

### InitRender()

Called after `InitSteamVR()` succeeds to set up rendering:

```csharp
private static void InitRender(XRWindow window)
{
    _openVrRuntimeActiveForRender = true;
    InitRenderCallbacks(window);

    uint rW = 0u, rH = 0u;
    OpenVRApi.CVR.GetRecommendedRenderTargetSize(ref rW, ref rH);

    var left = MakeFBOTexture(rW, rH);
    var right = MakeFBOTexture(rW, rH);

    if (Stereo)
        InitSinglePass(window, rW, rH, left, right);
    else
        InitTwoPass(window, rW, rH, left, right);
}
```

The HMD's recommended per-eye resolution is queried via `GetRecommendedRenderTargetSize()` and used to size all render targets.

### Render Target Creation

Two rendering modes create different target configurations:

#### Two-Pass Targets

```
Per-Eye FBO (×2):
┌──────────────────────────────────────┐
│  XRMaterialFrameBuffer               │
│  ├─ XRTexture2D (color, RGB8)        │ ← Engine-owned, per-eye
│  └─ Depth/stencil renderbuffer       │
│                                      │
│  XRViewport (per-eye)                │
│  ├─ AutomaticallyCollectVisible=false │
│  ├─ AutomaticallySwapBuffers=false    │
│  └─ Resolution = HMD recommended     │
└──────────────────────────────────────┘
```

Each eye gets its own `XRMaterialFrameBuffer` wrapping a `XRTexture2D` color attachment and viewport. The `XRRenderPipelineInstance` is shared between both eyes, using a stereo culling frustum for visibility.

#### Single-Pass Stereo Targets

```
Stereo Array FBO (×1):
┌──────────────────────────────────────┐
│  XRFrameBuffer                       │
│  ├─ XRTexture2DArray (2 layers, RGB8)│ ← Array texture for multi-view
│  │    ├─ Layer 0 = Left eye          │
│  │    └─ Layer 1 = Right eye         │
│  └─ OVRMultiViewParameters(0, 2)     │
│                                      │
│  XRTexture2DArrayView (×2)           │
│  ├─ StereoLeftViewTexture  (layer 0) │ ← View into layer 0
│  └─ StereoRightViewTexture (layer 1) │ ← View into layer 1
│                                      │
│  Single XRViewport                   │
│  └─ RenderPipeline = DefaultRenderPipeline(stereo=true)
└──────────────────────────────────────┘
```

Single-pass stereo uses a `XRTexture2DArray` with `OVRMultiViewParameters` for `GL_OVR_multiview` rendering. Two `XRTexture2DArrayView` objects expose individual layers as separate textures for compositor submission.

#### Texture Creation

```csharp
private static XRTexture2D MakeFBOTexture(uint rW, uint rH)
    => XRTexture2D.CreateFrameBufferTexture(
        rW, rH,
        EPixelInternalFormat.Rgb,
        EPixelFormat.Bgr,
        EPixelType.UnsignedByte);
```

### InitRenderCallbacks()

Installs three VR rendering callbacks on the engine timer:

```csharp
private static void InitRenderCallbacks(XRWindow window)
{
    AttachRenderCallback(window);    // Render() → window.RenderViewportsCallback
    Renderer = window.Renderer;

    bool wantStereo = Stereo;
    if (wantStereo)
    {
        Engine.Time.Timer.CollectVisible += CollectVisibleStereo;
        Engine.Time.Timer.SwapBuffers += SwapBuffersStereo;
    }
    else
    {
        Engine.Time.Timer.CollectVisible += CollectVisibleTwoPass;
        Engine.Time.Timer.SwapBuffers += SwapBuffersTwoPass;
    }
}
```

The callbacks can be hot-swapped at runtime if the stereo mode changes — old handlers are removed and new ones installed.

---

## Rendering Modes

### Two-Pass Rendering

Renders each eye separately using a shared render pipeline:

```
Visibility Collection:
  CollectVisibleTwoPass()
    └─ scene.CollectRenderedItems(
         _twoPassRenderPipeline.MeshRenderCommands,
         leftEyeCamera,
         stereoCullingFrustum × HMDNode.RenderMatrix)
       Uses a combined left+right frustum to cull once for both eyes

Rendering:
  RenderTwoPass()
    ├─ _twoPassRenderPipeline.Render(scene, leftCamera,  ..., LeftEyeViewport,  VRLeftEyeRenderTarget)
    ├─ _twoPassRenderPipeline.Render(scene, rightCamera, ..., RightEyeViewport, VRRightEyeRenderTarget)
    └─ SubmitRenders(leftTextureHandle, rightTextureHandle)
```

Two-pass mode uses a **shared stereo culling frustum** (`_stereoCullingFrustum`) that encompasses both eyes' view frustums. Visibility is collected once, then the same render commands are drawn twice with different view/projection matrices.

### Single-Pass Stereo Rendering

Renders both eyes in a single draw call using multi-view:

```
Visibility Collection:
  CollectVisibleStereo()
    └─ scene.CollectRenderedItems(
         StereoViewport.RenderPipelineInstance.MeshRenderCommands,
         stereoCullingFrustum × HMDNode.RenderMatrix,
         leftEyeCamera)

Rendering:
  RenderSinglePass()
    ├─ StereoViewport.RenderStereo(VRStereoRenderTarget, leftCamera, rightCamera, world)
    │    └─ Renders to XRTexture2DArray via GL_OVR_multiview
    └─ SubmitRenders(StereoLeftViewTexture.handle, StereoRightViewTexture.handle)
```

`RenderStereo()` on `XRViewport` drives the render pipeline in stereo mode:

```csharp
public void RenderStereo(XRFrameBuffer? targetFbo, XRCamera? leftCamera,
    XRCamera? rightCamera, XRWorldInstance? worldOverride = null)
{
    _renderPipeline.Render(
        world.VisualScene,
        leftCamera,
        rightCamera,       // second camera triggers stereo path in pipeline
        this,
        targetFbo,
        screenSpaceUI,
        shadowPass: false,
        isStereo: true,
        null);

    if (!uiThroughPipeline)
        RenderScreenSpaceUIOverlay(targetFbo);
}
```

When the pipeline receives both a left and right camera, it enables `GL_OVR_multiview` to render both eye views in a single geometry pass with per-view projection matrices.

---

## Per-Frame Render Loop

### Update() — Prediction Timing

Runs on `PreUpdateFrame` to compute accurate pose prediction:

```csharp
private static void Update()
{
    if (OpenVRApi.Headset is null)
    {
        OpenVRApi.UpdateInput(0);
        return;
    }

    float secondsSinceLastVsync = 0.0f;
    ulong frameCount = 0uL;
    OpenVRApi.CVR.GetTimeSinceLastVsync(ref secondsSinceLastVsync, ref frameCount);

    float displayFrequency = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(
        deviceIndex, Prop_DisplayFrequency_Float, ref error);
    float motionToPhoton = OpenVRApi.CVR.GetFloatTrackedDeviceProperty(
        deviceIndex, Prop_SecondsFromVsyncToPhotons_Float, ref error);

    float frameDuration = 1.0f / displayFrequency;
    float fSecondsFromNow = frameDuration - secondsSinceLastVsync + motionToPhoton;

    OpenVRApi.UpdateInput(fSecondsFromNow);
    OpenVRApi.Update();
}
```

The prediction time accounts for:
- **Display frequency** — How long until the next vsync
- **Time since last vsync** — Where we are in the current frame
- **Motion-to-photon latency** — How long between pose sample and photon emission

### CollectVisible

Both modes check for OpenXR first (for unified callback routing), then run OpenVR visibility:

**Two-pass mode:**
```csharp
private static void CollectVisibleTwoPass()
{
    if (IsOpenXRActive) { OpenXRApi?.EngineCollectVisibleTick(); return; }

    ViewInformation.World?.VisualScene?.CollectRenderedItems(
        _twoPassRenderPipeline.MeshRenderCommands,
        ViewInformation.LeftEyeCamera,
        cullWithFrustum: true,
        customFrustum: _stereoCullingFrustum.Value.TransformedBy(hmdNode.Transform.RenderMatrix),
        sortResults: true);
}
```

**Stereo mode:**
```csharp
private static void CollectVisibleStereo()
{
    if (IsOpenXRActive) { OpenXRApi?.EngineCollectVisibleTick(); return; }

    scene.CollectRenderedItems(
        StereoViewport.RenderPipelineInstance.MeshRenderCommands,
        _stereoCullingFrustum.Value.TransformedBy(hmdNode.Transform.RenderMatrix),
        ViewInformation.LeftEyeCamera,
        sortResults: true);
}
```

Both use a **stereo culling frustum** — a combined frustum that conservatively encompasses both eyes' view cones, so objects visible to either eye are included.

### SwapBuffers

Swaps the double-buffered render command lists:

```csharp
private static void SwapBuffersTwoPass()
{
    if (IsOpenXRActive) { OpenXRApi?.EngineSwapBuffersTick(); return; }
    _twoPassRenderPipeline?.MeshRenderCommands?.SwapBuffers();
}

private static void SwapBuffersStereo()
{
    if (IsOpenXRActive) { OpenXRApi?.EngineSwapBuffersTick(); return; }
    StereoViewport?.SwapBuffers();
}
```

### Render()

The main render callback, invoked via `window.RenderViewportsCallback`:

```
Render()
│
├─ [OpenXR active?] → OpenXRApi.EngineRenderTick() → return
│
├─ Check/resize render targets if HMD resolution changed
│
├─ OpenVRApi.UpdateDraw(Origin)
│     Begin HMD frame, get latest tracked device poses
│
├─ RecalcMatrixOnDraw?.Invoke()
│     Update VR rig transforms (headset, controllers, trackers)
│
├─ [Check IsPowerSaving from ShouldApplicationReduceRenderingWork()]
│
├─ [Stereo mode?]
│   ├─ true:  RenderSinglePass()
│   └─ false: RenderTwoPass()
│
└─ [Log VR frame times?] → ReadStats()
```

### Render Target Resizing

If the HMD resolution changes (e.g., supersampling slider), render targets are rebuilt:

```csharp
if (rW != _lastRenderWidth || rH != _lastRenderHeight)
{
    if (Stereo)
    {
        StereoViewport?.Resize(rW, rH);
        VRStereoRenderTarget?.Resize(rW, rH);
    }
    else
    {
        var left = MakeFBOTexture(rW, rH);
        var right = MakeFBOTexture(rW, rH);
        RemakeTwoPass(Renderer.XRWindow, rW, rH, left, right);
        _twoPassRenderPipeline?.DestroyCache();
    }
}
```

---

## Compositor Submission

### OpenGL Texture Handle Flow

The complete chain from engine texture to OpenVR compositor:

```
1. Engine creates XRTexture2D (or XRTexture2DArrayView) at HMD resolution
2. When first accessed on the render thread, the OpenGL renderer creates a GLTexture2D:
     glGenTexture() → BindingId = GL texture name (e.g., 42)
3. GLObjectBase.GetHandle() returns (nint)BindingId → IntPtr pointing to GL name 42
4. SubmitRenders() sets Texture_t.handle = IntPtr(42), Texture_t.eType = ETextureType.OpenGL
5. IVRCompositor.Submit() receives the GL texture name and reads it directly via shared GL context
```

The key code that extracts the GL handle:

```csharp
nint? leftHandle = VRLeftEyeViewTexture?.APIWrappers?.FirstOrDefault()?.GetHandle();
// APIWrappers = the AbstractRenderAPIObject[] wrapping this generic object
// GetHandle() on GLObjectBase returns (nint)BindingId (the GL texture name)
```

### SubmitRenders()

Submits both eyes to the OpenVR compositor:

```csharp
public static void SubmitRenders(
    IntPtr leftEyeHandle,
    IntPtr rightEyeHandle,
    ETextureType apiType = ETextureType.OpenGL,
    EColorSpace colorSpace = EColorSpace.Auto,
    EVRSubmitFlags flags = EVRSubmitFlags.Submit_Default)
{
    _eyeTex.eColorSpace = colorSpace;
    _eyeTex.eType = apiType;

    var comp = Valve.VR.OpenVR.Compositor;

    _eyeTex.handle = leftEyeHandle;
    comp.Submit(EVREye.Eye_Left, ref _eyeTex, ref _singleTexBounds, flags);

    _eyeTex.handle = rightEyeHandle;
    comp.Submit(EVREye.Eye_Right, ref _eyeTex, ref _singleTexBounds, flags);

    comp.PostPresentHandoff();
}
```

`PostPresentHandoff()` signals SteamVR that the application has finished submitting and the compositor can begin compositing. This reduces latency by allowing the compositor to start work as soon as possible.

### Texture Bounds

```csharp
private static VRTextureBounds_t _singleTexBounds = new()
{
    uMin = 0.0f, uMax = 1.0f,
    vMin = 0.0f, vMax = 1.0f,
};
```

Each eye texture uses the full `[0,1]` UV range (the entire texture represents one eye). The codebase has commented-out `_leftEyeTexBounds`/`_rightEyeTexBounds` with split UVs (`0-0.5` / `0.5-1.0`) for a side-by-side layout, and a commented-out `SubmitRender()` path that uses `Submit_GlArrayTexture` for array texture submission — both are unused in favor of the per-eye texture approach.

---

## Frame Timing & Statistics

### Prediction Calculation

```
fSecondsFromNow = (1/displayFrequency) - secondsSinceLastVsync + motionToPhoton
```

For a 90 Hz display with 11ms frame duration, 4ms since last vsync, and 20ms motion-to-photon:
```
fSecondsFromNow = 11.1ms - 4ms + 20ms = 27.1ms into the future
```

This prediction time is passed to `OpenVRApi.UpdateInput()` which feeds it to `GetDeviceToAbsoluteTrackingPose()` for predicted pose sampling.

### ReadStats()

When `Rendering.Settings.LogVRFrameTimes` is enabled, frame statistics are read from the compositor:

```csharp
Valve.VR.OpenVR.Compositor.GetFrameTiming(ref currentFrame, 0);  // Current frame
Valve.VR.OpenVR.Compositor.GetFrameTiming(ref previousFrame, 1); // Previous frame

// Average over frames since last sample:
GpuFrametime  = currentFrame.m_flTotalRenderGpuMs;
CpuFrametime  = currentFrame.m_flNewFrameReadyMs
              - currentFrame.m_flNewPosesReadyMs
              + currentFrame.m_flCompositorRenderCpuMs;
TotalFrametime = (current.SystemTime - previous.SystemTime) * 1000;
Framerate      = 1000 / TotalFrametime;
```

---

## VR Modes (Local, Client, Server)

OpenVR supports three operational modes:

| Mode | Method | Description |
|------|--------|-------------|
| **Local** | `InitializeLocal()` | Input and rendering handled by this process. The standard mode. |
| **Client** | `IninitializeClient()` | Input handled locally, rendered frames are received from a server. |
| **Server** | `InitializeServer()` | Receives input from client, sends rendered frames. Currently unimplemented (`return false`). |

```csharp
public enum VRMode { Server, Client, Local }
```

For the `Auto` runtime selection with `runVRInPlace = false`, the engine uses client mode. With `runVRInPlace = true`, it uses local mode.

---

## OpenVR Input

Input is managed through the OpenVR action system:

1. **Action manifest** — Defines action sets and actions (configured by `EditorVR.cs` for the Editor)
2. **`CreateActions()`** — Binds manifest actions to engine input delegates
3. **`Update()`** — Called on `PreUpdateFrame`:
   - Computes prediction time from vsync/frequency/photon-latency
   - `OpenVRApi.UpdateInput(fSecondsFromNow)` — polls input with predicted poses
   - `OpenVRApi.Update()` — processes device events

Device detection is handled via the `DeviceDetected` event on the `VR` instance, which fires when controllers, trackers, or other devices are connected/disconnected.

---

## Relationship to OpenXR

The OpenVR and OpenXR paths share the same `Engine.VRState` callback infrastructure. Every VR callback (`CollectVisible`, `SwapBuffers`, `Render`) checks `IsOpenXRActive` first and delegates to the OpenXR API if active:

```csharp
private static void Render()
{
    if (IsOpenXRActive) { OpenXRApi?.EngineRenderTick(); return; }
    // ... OpenVR path follows ...
}
```

This means:
- Only one VR runtime is active at a time (`_activeRuntime` is either `OpenVR` or `OpenXR`, never both)
- The same timer callback slots are used regardless of runtime
- OpenXR completely bypasses the OpenVR render target, compositor, and input code

Key architectural differences between the two paths:

| Aspect | OpenVR | OpenXR |
|--------|--------|--------|
| **Swapchain ownership** | Engine-owned FBOs | Runtime-owned swapchain textures |
| **Submission** | `IVRCompositor.Submit()` + `PostPresentHandoff()` | `xrEndFrame()` with `CompositionLayerProjection` |
| **Pose sampling** | Single prediction on `PreUpdateFrame` | Dual-sample: predicted (visibility) + late (render) |
| **Eye rendering** | Two-pass or multi-view single-pass | Render to mirror FBO → blit to swapchain |
| **Graphics API** | OpenGL (GL texture names submitted directly) | OpenGL (WGL binding) or Vulkan (device handle binding) |
| **Session lifecycle** | `TryStart()` / shutdown | Full state machine (Desktop → Instance → System → Session → Running) |

---

## See Also

- [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) — How windows and renderers are created at startup
- [OpenGL Renderer](opengl-renderer.md) — OpenGL-specific rendering details
- [Vulkan Renderer](vulkan-renderer.md) — Vulkan-specific rendering details
- [OpenXR VR Rendering](openxr-vr-rendering.md) — OpenXR rendering path (the modern alternative)
