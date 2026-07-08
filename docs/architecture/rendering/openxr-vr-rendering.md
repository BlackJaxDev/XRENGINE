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
  - [Timing Settings And Stats](#timing-settings-and-stats)
  - [Collect Pose And Tracking Policies](#collect-pose-and-tracking-policies)
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
| `OpenXRAPI.SmokeDiagnostics.cs` | Structured smoke summary for no-HMD OpenXR validation |
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

The Vulkan renderer supports two creation modes. For runtimes that work well with `XR_KHR_vulkan_enable2`, the renderer can let OpenXR create the `VkInstance` and `VkDevice`, then reuse that OpenXR instance for session creation. For SteamVR, `Auto` mode uses app-created Vulkan handles and the legacy `XR_KHR_vulkan_enable` binding by default because SteamVR can raise a native breakpoint inside `xrCreateVulkanInstanceKHR` on some installations. Override with `XRE_OPENXR_VULKAN_ENABLE2_BOOTSTRAP=Force` for diagnostics, or `Disable` to force app-created Vulkan handles on any runtime.

When the renderer owns Vulkan handle creation for an OpenXR launch, the Vulkan instance `ApiVersion` is resolved from the active runtime's `xrGetVulkanGraphicsRequirementsKHR` range. This prevents SteamVR from rejecting `xrCreateSession` because the app-created Vulkan instance requested a newer Vulkan version than the runtime advertises for OpenXR.

SteamVR also expects `xrGetVulkanGraphicsDeviceKHR` to be called on the same OpenXR instance that will later create the session. The app-owned Vulkan path validates the renderer's selected physical device with that session instance immediately before `xrCreateSession`; do not replace that with a cached result from an earlier bootstrap instance.

When SteamVR clamps the app Vulkan instance below Vulkan 1.3, renderer code that normally uses dynamic rendering or synchronization2 must call the loaded KHR extension commands (`vkCmdBeginRenderingKHR`, `vkCmdEndRenderingKHR`, `vkQueueSubmit2KHR`, and `vkCmdPipelineBarrier2KHR`). Direct Vulkan 1.3 entry points are not guaranteed to exist on that instance.

Vulkan OpenXR session creation waits for startup texture streaming and allocation pressure to settle before calling `xrCreateSession`. It also waits briefly for desktop command buffers to stop being dirtied and for submitted desktop frame slots to retire, but those desktop-idle gates are bounded so normal editor preview rendering cannot starve SteamVR session creation forever. The actual session transition still serializes with in-flight Vulkan work and idles the device before and after `xrCreateSession` and swapchain creation.

The Vulkan path is otherwise simpler because all handles (`VkInstance`, `VkPhysicalDevice`, `VkDevice`) are plain integers that don't require thread-local context.

Vulkan OpenXR renders directly to the runtime-owned eye swapchain by default. This keeps the normal viewport render pipeline active, including deferred G-buffer lighting and post-processing. Set `XRE_OPENXR_VULKAN_MIRROR_FBO=1` only for compatibility debugging; that path first renders each eye through an offscreen FBO command chain, so it does not match the full deferred viewport lighting path.

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

## OpenXR Vulkan Stereo Mode Matrix

`EVrViewRenderMode` is the requested setting. Runtime logs, smoke summaries,
renderer stats, and profile captures also report the effective implementation
path so `SinglePassStereo` never implies a path that was not actually used.

| Requested mode | Effective path | Resource model | Temporal policy |
|---|---|---|---|
| `SequentialViews` | Sequential per-eye swapchains | One runtime-owned 2D swapchain image per eye. | History-based TAA/TSR is disabled for external per-eye targets. |
| `ParallelCommandBufferRecording` | Parallel per-eye command-buffer recording | Same rendered output as `SequentialViews`; safe eye command-buffer work is prepared concurrently, then submitted through serialized Vulkan queue sections. | Same per-eye external-swapchain history policy as sequential. |
| `SinglePassStereo` | `TrueSinglePassStereo` when staged stereo is available | Engine-owned `XRTexture2DArray` color/depth staging target with layer 0 = left eye and layer 1 = right eye, then explicit layer publish/copy to OpenXR swapchains. | Stereo array-layer history is allowed for validated stereo-aware temporal resources. |
| `SinglePassStereo` | `OpenXrSinglePassCompatibility` fallback | Per-eye swapchain rendering in a batch, not true multiview. | External per-eye history remains disabled. |

SteamVR OpenXR Vulkan defaults to `OpenXrSinglePassCompatibility` even when the
engine has multiview support, because the current true-stereo staging publish
path blits into runtime-owned swapchain images and is not considered SteamVR
stable. Set `XRE_OPENXR_VULKAN_TRUE_STEREO=1` only for focused diagnostics or
RenderDoc work on that path.

`SequentialViews`, `ParallelCommandBufferRecording`, and
`OpenXrSinglePassCompatibility` all render the same external per-eye swapchain
resource model. `ParallelCommandBufferRecording` changes how eye command-buffer
work is prepared, not what the headset should see. If the SteamVR logs report
`OpenXrSinglePassCompatibility`, changing between those modes is expected to
look visually identical.

The Vulkan true-stereo path uses the engine stereo pipeline through
`XRViewport.RenderStereo(...)`; it does not render multiview directly into an
OpenXR array swapchain yet. The direct array-swapchain path is a future
optimization.

### Stereo Feature Policies

- Auto exposure uses `EVrAutoExposurePolicy.HeadsetShared` for VR v1. A
  stereo-array HDR source is averaged across eye layers into one 1x1 exposure
  texture. External per-eye swapchain sources skip auto exposure with a warning
  instead of letting the second eye win shared exposure state.
- TAA/TSR and motion-vector history are enabled only when the active temporal
  history policy says the resource model is stereo safe. OpenXR external
  per-eye modes keep history-based AA disabled.
- External OpenXR per-eye swapchain passes use the combined stereo visibility
  command set collected from both eye frusta. They must not run a second
  strict per-eye GPU frustum/BVH cull over that shared list, because doing so
  can reject commands that were collected for the other eye and cause left/right
  mesh popping. The GPU pass filter preserves the shared command list and lets
  the per-view indirect stage select the active eye's draw stream.
- Atmosphere and volumetric-fog temporal history are disabled in OpenXR stereo
  paths until their half-resolution resources and shaders are stereo arrays.
  Mono atmosphere/fog history remains camera-keyed.
- Vendor upscalers are intentionally unsupported for headset stereo today:
  native NVIDIA DLSS/DLAA, DLSS frame generation, Intel XeSS, Intel XeSS frame
  generation, and the OpenGL-to-Vulkan bridge all fail loudly when explicitly
  requested in VR. Ordinary fallback blit remains supported.

### Mode Switching And Diagnostics

Pipeline resource generations include stereo state, feature masks, HDR/AA/MSAA
state, and reserved view-count metadata. OpenXR external swapchain targets force
same-frame resource generation for the active eye extent; if the active
generation cannot match, the exception reports the required, active, and
pending resource keys.

Useful diagnostics:

- View-mode logs from `OpenXRAPI.Vulkan.cs` report requested mode, effective
  path, temporal history policy, parallel gate state, swapchain formats, and
  true-stereo support.
- `XRE_OPENXR_VULKAN_TRUE_STEREO=1` opts into the diagnostic true-stereo
  staging path for runtimes where it is guarded off by default.
- `XRE_OPENXR_VULKAN_ENABLE2_BOOTSTRAP=Force` tests OpenXR-created Vulkan
  handles through `XR_KHR_vulkan_enable2`; `Disable` forces app-created Vulkan
  handles for comparison. SteamVR uses the app-created path in `Auto`.
- `XRE_VULKAN_DIRECTIONAL_CASCADES=1` forces Vulkan directional cascades on;
  `0` forces them off. By default they are enabled for SteamVR and ordinary
  Vulkan sessions, but still guarded off for known Monado OpenXR runtimes until
  the layered cascade planner path is safe there. Legacy texture-array cascades
  use the requested layered mode when the backend exposes the required layered
  framebuffer and shader layer-output capabilities. SteamVR Vulkan can also use
  grouped directional atlas cascades when indexed viewport/scissor and shader
  viewport-output capabilities are available; Monado remains on the guarded
  sequential atlas path.
- `VPRC_ExposureUpdate` logs the VR exposure policy and source texture type.
- `VPRC_VendorUpscale` logs the VR vendor support matrix before throwing for
  explicit unsupported vendor requests.
- `VulkanRenderer.OpenXR` logs serialized queue-submit critical-section waits
  when parallel eye work contends on the Vulkan submit lock.

### Profiling Matrix Runner

Use `Tools/OpenXR/Run-OpenXrModeProfileMatrix.ps1` for repeatable CPU/GPU
profile captures across the OpenXR mode matrix. It writes CSV and JSON summaries
under `Build/_AgentValidation/<run>/reports/`, enables `XRE_PROFILE_CAPTURE=1`,
and delegates each case to `Run-OpenXrMonadoSmoke.ps1` with Vulkan OpenXR
profiling environment variables. Pass `-DryRun` to emit the matrix and report
paths without launching an OpenXR runtime.

Troubleshooting quick checks:

- If TSR is disabled in an OpenXR per-eye mode, confirm the log reports
  `DisabledExternalPerEyeSwapchain`; that is expected until per-eye TSR history
  resources exist.
- If true stereo is unavailable, inspect the structured view-mode diagnostic for
  the multiview support reason and the fallback path.
- If a vendor upscaler fails in VR, the failure is intentional unless a future
  support matrix entry says the vendor path owns per-eye or per-layer history.

---

## Frame Lifecycle

### Three-Phase Frame Model

OpenXR frames follow a three-phase pipeline split across threads:

Current ordering keeps OpenXR API calls render-thread-owned while delaying the next `xrWaitFrame` until after the desktop window has rendered by default:

```text
Render thread, RenderViewportsCallback:
  PollEvents
  LocateViews(Late)
  UpdateActionPoseCaches(Late)
  InvokeRecalcMatrixOnDraw(Late)
  RenderFrame -> xrAcquire/xrWait/xrRelease swapchain images -> xrEndFrame
  restore GL state

XRWindow desktop render:
  Render normal desktop viewports, mirror, and editor UI

Render thread, PostRenderViewportsCallback:
  PrepareNextFrameOnRenderThread
    xrWaitFrame -> xrBeginFrame -> LocateViews(Predicted)
    UpdateActionPoseCaches(Predicted)
    InvokeRecalcMatrixOnDraw(Predicted)
    publish _pendingXrFrame for CollectVisible

CollectVisible thread:
  OpenXrCollectVisible builds per-eye command buffers from the predicted pose cache
  OpenXrSwapBuffers publishes the buffers by setting _framePrepared
```

`OpenXrPrepareFrameAfterDesktopRender` controls the handoff. It defaults to `true`; setting it to `false` restores the older behavior where `PrepareNextFrameOnRenderThread()` runs at the end of `Window_RenderViewportsCallback`.

The older box diagram below is retained as a high-level thread-ownership sketch. For exact call order, use the ASCII ordering above.

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

Called on the render thread after desktop viewport/editor rendering by default. Prepares the next frame:

1. Clear stale flags (`_framePrepared`, `_pendingXrFrameCollected`)
2. `xrWaitFrame()` — blocks until the runtime is ready for a new frame; returns predicted display time
3. `xrBeginFrame()` — marks the start of GPU work for this frame
4. If `ShouldRender == 0` (runtime says skip), sets `_frameSkipRender = 1` and returns
5. `LocateViews(Predicted)` gets predicted eye poses for the display time and caches them under `_openXrPoseLock`
6. `UpdateActionPoseCaches(Predicted)` samples controller/tracker poses at the same predicted display time
7. `InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted)` updates VR rig node transforms to predicted poses
8. Sets `_pendingXrFrame = 1` to make the frame available to the visibility thread

### Phase 2: CollectVisible

Runs on the visibility/collect thread. Builds per-eye render command lists:

1. Guard on `_sessionBegun`, `_pendingXrFrame != 0`; uses atomic CAS to prevent double-collection
2. Resolve source viewport, VR rig, base camera, and world from `Engine.VRState.ViewInformation`
3. Ensure per-eye `XRViewport` and `XRCamera` objects exist (`EnsureOpenXrViewports()`, `EnsureOpenXrEyeCameras()`)
4. Copy post-process settings from the base camera to eye cameras
5. Update eye camera transforms and FOV from the lock-protected predicted pose cache
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
   - `UpdateActionPoseCaches(Late)` re-locates controller/tracker poses at the frame target time; `xrSyncActions` runs once per frame unless `OpenXrActionSyncPolicy` is `PredictedAndLate`
   - Logs pose delta between predicted and late samples
   - `InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)` updates transforms to latest poses
4. **`RenderFrame()`** — submits the frame:
   - For each eye: acquire swapchain image, render, release
   - Build `CompositionLayerProjection` with both views
   - `xrEndFrame` with the projection layer
5. **Restore GL state** - puts the GL context back to the state the engine expects
6. **Post-render prepare** - `Window_PostRenderViewportsCallback()` calls `PrepareNextFrameOnRenderThread()` when `OpenXrPrepareFrameAfterDesktopRender` is enabled

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

### Timing Settings And Stats

OpenXR timing is surfaced through `Engine.Rendering.Stats`, `ProfilerStatsPacket`, `EngineProfilerDataSource`, `Engine.ProfilerSender`, and `ProfilerPanelRenderer`.

| Stat | Meaning |
|------|---------|
| `VrXrWaitFrameBlockTimeMs` | Time spent blocked in `xrWaitFrame`. |
| `VrXrEndFrameSubmitTimeMs` | Time spent in `xrEndFrame`. |
| `VrXrPredictedDisplayLeadTimeMs` | `predictedDisplayTime` minus the current QPC time converted with `XR_KHR_win32_convert_performance_counter_time`; unavailable as NaN when the extension is missing. |
| `VrXrPredictedToLatePoseDeltaMillimeters` / `VrXrPredictedToLatePoseDeltaDegrees` | HMD delta between predicted and late samples for the same frame. |
| `VrXrMissedDeadlineFrames` | Count of frames where `xrEndFrame` completion, converted to `XrTime`, reached the target display time minus `OpenXrDeadlineSafetyMarginMs`. |
| `VrXrTrackingLossFrames` | Count of `xrLocateViews` calls whose view state did not contain valid orientation and position. |
| `VrXrRelocatePredictedTimeMs` | Cost of the optional predicted-view relocate policy. |
| `VrXrCollectFrustumExpansionDegrees` | Frustum padding applied during `CollectVisible`. |
| `VrXrPacingThreadIdleTimeMs` | Time the dedicated XR pacing thread spent waiting on its wake event between successive `xrWaitFrame` calls. Only populated when `OpenXrRenderPacingMode == DedicatedThread`. |
| `VrXrPacingHandoffStalls` | Count of frames where the render thread reached `RenderFrame` with no prepared XR frame published. Should remain near zero under steady state when `OpenXrRenderPacingMode == DedicatedThread`. |

`Tools/Reports/Find-NewAllocations.ps1` also flags formatted `Debug.Out`/`Console.WriteLine` candidates in `OpenXRAPI.*`; pass `-FailOnOpenXrHotPathAllocations` when the report should fail CI-like validation.

### Collect Pose And Tracking Policies

`OpenXrCollectVisiblePosePolicy` controls the predicted pose used for visibility:

| Policy | Behavior |
|--------|----------|
| `Predicted` | Use the predicted pose cached by `PrepareNextFrameOnRenderThread`. |
| `RelocatePredicted` | Re-issue `LocateViews(Predicted)` on the render thread before publishing the frame, then record relocate cost. |
| `PaddedFrustum` | Use the predicted pose cache and expand the asymmetric per-eye FOV by `OpenXrCollectVisibleFrustumPaddingDegrees` for culling only. |

`OpenXrTrackingLossPolicy` controls invalid `ViewStateFlags`: `FreezeLastValid` reuses the last valid view poses, `Identity` substitutes identity poses, and `SkipFrame` aborts the frame. `OpenXrActionSyncPolicy` defaults to `PredictedOnly`; `PredictedAndLate` restores the older two-sync behavior if an input backend needs it.

### Render Pacing Mode

`OpenXrRenderPacingMode` selects where `PrepareNextFrameOnRenderThread` (xrWaitFrame → xrBeginFrame → predicted-pose work) runs:

| Mode | Behavior |
|------|----------|
| `InRenderCallback` | Prep runs at the end of the eye-render callback (legacy). Blocks the render dispatch on `xrWaitFrame`. |
| `PostRenderCallback` (default) | Prep runs in the post-render callback after the desktop frame is presented. Still on the render dispatch, but the desktop mirror has already been submitted. |
| `DedicatedThread` | Prep runs on a dedicated `XR Pacing` thread. The render thread returns immediately after `xrEndFrame`, fully decoupling desktop FPS from the compositor cadence. |

External-sync invariant when `DedicatedThread` is selected: the pacing thread Waits on a `ManualResetEventSlim` before each prep iteration; the render thread Sets that event only after `xrEndFrame` (and on aborted-prep cleanup). This guarantees `xrWaitFrame`/`xrBeginFrame` on the pacing thread never overlap `xrEndFrame` or swapchain acquire/wait/release on the render thread. `xrEndFrame`, per-eye swapchain ops, and the Late `xrLocateViews` stay on the render thread; `xrWaitFrame`, `xrBeginFrame`, predicted `xrLocateViews`, `UpdateActionPoseCaches(Predicted)`, and `InvokeRecalcMatrixOnDraw(Predicted)` move to the pacing thread.

The pacing thread starts lazily on the first render callback after `_sessionBegun` becomes true, and stops on session `Stopping`/`Exiting`/`LossPending`, on `TearDownSessionResources`, on `EnableRuntimeMonitoring`, and during `CleanUp`. `VrXrPacingThreadIdleTimeMs` and `VrXrPacingHandoffStalls` are populated only in this mode.

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

When VR is active, the desktop window is governed by `VrMirrorMode`:
`Off`, `BlitSubmittedEye`, `CyclopeanReconstruct`, `LowRatePreview`, or
`FullIndependentRender`. The standard profiling/default posture is
`BlitSubmittedEye`, which keeps the desktop window alive but composes it from
the submitted XR eye output instead of recording another full desktop scene.
`FullIndependentRender` is opt-in for editor diagnostics that need a separate
desktop camera and visibility group. `LowRatePreview` keeps the mono/cyclopean
runtime-camera path available behind the output cadence scheduler, and
`CyclopeanReconstruct` is the depth-aware reconstruction target tracked in
[VR Mirror Cyclopean Reconstruction TODO](../../work/todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md).

`TryRenderDesktopMirrorComposition()` in `OpenXRAPI.OpenGL.cs`:

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
- `Engine.Rendering.Settings.VrMirrorMode` is `BlitSubmittedEye` or
  `CyclopeanReconstruct`

The profiler `Frame Outputs` manifest reports whether the desktop row is a
`DesktopMirror` or a separate `DesktopScene`, the active mirror mode, configured
desktop target rate, achieved rate, skip counts, and whole render-thread frame
budget band.

---

## Monado Smoke Diagnostics

The local no-HMD OpenXR smoke runner is `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1`.
It selects Monado per process with `XR_RUNTIME_JSON`, runs an OpenXR loader
preflight before editor startup, launches the Unit Testing World with
`VR.Mode=MonadoOpenXR`, then exits through the editor's bounded
`--smoke-frames N` path.

`OpenXRAPI.SmokeDiagnostics.cs` records the summary consumed by the runner:

- Runtime manifest path, runtime name/version, renderer, and enabled
  extensions.
- Reference space type, session-state transitions, and teardown completion.
- Swapchain metadata, located view count, submitted frame count, no-layer
  frames, and `xrEndFrame` failures.
- Per-eye acquire/wait/release counts.
- First predicted and late view/action pose cache updates.
- First desktop mirror composition.
- `perFrameAllocationsBytes`, currently zero unless a justified baseline is
  recorded.

Monado's Windows no-HMD path can return `ShouldRender == false` while still
advancing the OpenXR frame loop. In that case the engine submits valid
`xrEndFrame` calls with no projection layers; the runner counts these as
`noLayerFrameCount` and accepts them when no rendered layers were submitted.
The runner treats the structured summary as the authoritative result once it is
written. If the editor process lingers or returns the known post-summary native
shutdown code after a passing summary, the runner terminates/ignores that
post-summary process state and reports the smoke result from the summary.

A passing summary proves the instance, system, session, swapchain, view locate,
frame submit, pose-cache, and desktop mirror milestones all happened. Missing
required fields or failed milestones produce stable smoke exit codes:

| Code | Meaning |
|------|---------|
| 0 | Smoke passed |
| 21 | Startup or configuration failure |
| 22 | Frame timeout |
| 23 | Summary/assertion failure |
| 24 | Teardown failure |
| 25 | Engine exception |

Lane naming:

- Lane 1: scene-only VR through `VR.Mode=Emulated`; this does not emulate
  OpenXR API calls.
- Lane 2: Monado-backed OpenXR through `XR_RUNTIME_JSON`; this exercises the
  real loader, runtime, session, swapchains, poses, and frame submission.

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

Controller bindings are submitted as one complete table per interaction profile.
The table includes the hand grip pose, hand aim pose, runtime-neutral gameplay
actions, and haptics together so runtimes that replace same-profile suggestions
do not drop the grip pose binding.

### Per-Frame Updates

`UpdateActionPoseCaches()` is called for predicted and late timing. By default, `xrSyncActions` runs during the predicted call only and the late call re-locates spaces at the same XR frame target display time. `OpenXrActionSyncPolicy.PredictedAndLate` can opt back into synchronizing on both calls.

1. `xrSyncActions` synchronizes the action state once per frame by default
2. For each hand: `xrLocateSpace(gripSpace, appSpace, time)` returns position/orientation
3. For each tracker role: `xrLocateSpace(trackerSpace, appSpace, time)` returns position/orientation
4. Poses are stored double-buffered under `_openXrPoseLock`

Controller and tracker poses require both `PositionValidBit` and
`OrientationValidBit`. When valid, the cached local matrix is built from the
OpenXR orientation quaternion and position, so tracker rotation is preserved in
the same path as translation.

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

Controller model loading first uses the OpenXR `XR_MSFT_controller_model` extension when the active runtime exposes it. During an OpenXR launch, the engine does not start a separate OpenVR utility instance for SteamVR render-model fallback by default because that can native-crash in SteamVR while the OpenXR session is coming up or active. Set `XRE_OPENXR_ALLOW_OPENVR_RENDER_MODEL_FALLBACK=1` only when diagnosing or deliberately testing that mixed-API fallback.

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
