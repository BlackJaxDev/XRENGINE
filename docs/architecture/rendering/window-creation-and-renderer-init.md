# Window Creation & Renderer Initialization

This document describes how XREngine creates OS windows on startup, selects a graphics API (OpenGL or Vulkan), instantiates the appropriate renderer, and begins the render loop.

## Table of Contents

- [Entry Points](#entry-points)
- [Engine.Run() → Engine.Initialize()](#enginerun--engineinitialize)
- [Renderer API Selection](#renderer-api-selection)
- [Target-First Renderer Contexts](#target-first-renderer-contexts)
- [Window Creation](#window-creation)
- [XRWindow Constructor](#xrwindow-constructor)
- [Deferred Renderer Initialization](#deferred-renderer-initialization)
- [The Render Loop](#the-render-loop)
- [Window Ownership And Render Thread](#window-ownership-and-render-thread)
- [Interactive Resize](#interactive-resize)
- [Per-Frame Render Callback](#per-frame-render-callback)
- [Complete Call Chain](#complete-call-chain)
- [Class Hierarchy](#class-hierarchy)

---

## Entry Points

The Editor and Server both call `Engine.Run()` after configuring startup state. The Editor normally creates a desktop window; the Server requests `RunWithoutWindows` with an empty `StartupWindows` list, so only windowed startup enters the path described below.

### Editor (`XREngine.Editor/Program.cs`)

```csharp
[STAThread]
private static void Main(string[] args)
{
    // ... load settings, create world ...
    var startupSettings = GetEngineSettings(targetWorld);
    var gameState = Engine.LoadOrGenerateGameState();
    Engine.Run(startupSettings, gameState, beginPlayingAllWorlds: false);
}
```

### Server (`XREngine.Server/Program.cs`)

```csharp
private static void Main(string[] args)
{
    // ... server setup; GetEngineSettings() requests RunWithoutWindows ...
    Engine.Run(GetEngineSettings(), Engine.LoadOrGenerateGameState());
}
```

Both converge on `Engine.Run(GameStartupSettings, GameState)`.

---

## Engine.Run() → Engine.Initialize()

Defined in `XREngine.Runtime.Bootstrap/Engine/Engine.Lifecycle.cs`:

```csharp
public static void Run(
    GameStartupSettings startupSettings,
    GameState state,
    bool beginPlayingAllWorlds)
{
    bool initialized = Initialize(startupSettings, state, beginPlayingAllWorlds);
    if (initialized)
    {
        if (!beginPlayingAllWorlds)
            BeginEditAllWorlds();
        RunGameLoop();
        if (startupSettings.RunWithoutWindows)
            BlockWithoutRendering();
        else
            BlockForRendering();
    }
    Cleanup();
}
```

`Initialize()` performs these steps in order:

| Step | Description |
|------|-------------|
| 1 | Store `GameSettings` and `UserSettings` (includes `PreferredRenderBackend` / compatibility `RenderLibrary` choice) |
| 2 | `ValidateGpuRenderingStartupConfiguration()` — checks for debug overrides |
| 3 | `ConfigureJobManager()` — sets up parallel processing |
| 4 | **`CreateWindows(startupSettings.StartupWindows)`** — creates OS windows and renderers |
| 5 | Initialize secondary GPU context if supported |
| 6 | Initialize VR asynchronously (if configured) |
| 7 | `Time.Initialize()` — start the timing system for update/render ticks |
| 8 | Initialize networking (if configured) |
| 9 | Wire up profiler UDP sender |
| 10 | `BeginPlayAllWorlds()` for standalone play; editor startup enters the edit lifecycle before the game loop |

After `Initialize()` returns, `RunGameLoop()` starts the timer's update and physics work. Windowed startup uses `BlockForRendering()`; headless startup uses `BlockWithoutRendering()`.

---

## Renderer API Selection

### The `ERenderLibrary` Enum

Defined in `XREngine.Data/Core/Enums/ERenderLibrary.cs`:

```csharp
public enum ERenderLibrary
{
    OpenGL,
    Vulkan,
    // D3D12,  (reserved for future)
}
```

### Configuration Flow

The preferred render backend is stored in `UserSettings.PreferredRenderBackend`.
`UserSettings.RenderLibrary` remains a compatibility alias for existing callers
and serialized settings:

```csharp
private ERenderLibrary _renderLibrary = ERenderLibrary.OpenGL;  // default
public ERenderLibrary PreferredRenderBackend { get; set; }
public ERenderLibrary RenderLibrary { get; set; } // compatibility alias
```

This value is set from startup configuration. Unit-testing world JSON uses
the grouped `Rendering.RenderBackend` property. The legacy top-level
`RenderAPI` property is hidden from generated JSONC/schema output, but is still
read from old files and migrated into the grouped setting on settings
regeneration:

```csharp
Rendering = new UnitTestingRenderSettings
{
    RenderBackend = ERenderLibrary.Vulkan,
    BackendFallbackPolicy = RenderBackendFallbackPolicy.RequireRequested,
}
```

Backend fallback is explicit. `Engine.EffectiveSettings.RenderBackendFallbackPolicy`
resolves the engine default from `Engine.Rendering.Settings.Vulkan.Startup.FallbackPolicy`
plus project/user overrides. `RequireRequested` fails visibly when Vulkan cannot
initialize. `FallbackWithWarning` and `AutoPreferRequested` may retry with OpenGL
after logging the requested backend, fallback policy, and exception summary.

### Render Settings Ownership

Render settings are grouped by owner. Backend policy stays in its backend group.

| Owner | Holds |
|---|---|
| `UserSettings` | Personal preferences: preferred backend, display, audio, quality, and `UserRenderingOverrides`. |
| `GameStartupSettings` | Project defaults and required overrides, grouped in `GameRenderingOverrides`. |
| `Engine.Rendering.Settings` | Engine default runtime policy. `OpenGL` is an `OpenGLRenderSettings` with `Context`, `ShaderLinking`, `TextureUpload`, and `Diagnostics` groups. `Vulkan` is a `VulkanRenderSettings` with `Startup`, `TargetMode`, `GpuDriven`, `Descriptors`, `Synchronization`, `Memory`, `Robustness`, and `Diagnostics` groups. |
| `EditorPreferences` | Editor workflow and diagnostics. `EditorViewportPreferences`, `EditorSelectionPreferences`, and `EditorDiagnosticsPreferences` (with `General`, `Visualization`, `RenderPipeline`, `Culling`, `Exceptions`, `OpenGL`, `Vulkan`, and `Profiler` groups). |

Renderer code reads effective policy through `Engine.EffectiveSettings` or
`RuntimeEngine.EffectiveSettings`, not by walking user, project, or editor
settings. `Engine.EffectiveSettings.RenderSnapshot` returns an
`EffectiveRenderSettingsSnapshot` with `Common`, `OpenGL`, and `Vulkan`
parts. Flat properties remain as compatibility aliases for saved settings and
older callers; new code uses the grouped properties. A diagnostic that changes
startup behavior, such as the OpenGL debug context, must state that it needs a
restart.

---

## Target-First Renderer Contexts

Window creation remains the desktop application path, but renderer construction
is no longer intrinsically window-first. A backend factory receives a
`RendererBackendCreateContext`, validates the requested presentation target,
and freezes it into a renderer-owned `RendererHostContext`. The stable context
contains the execution mode, presentation target, backend generation, and
fixed-output properties where applicable. Desktop services are available only
through `IRendererDesktopWindowServices`; non-window modes never receive a
synthetic `XRWindow`.

| Execution mode | Presentation target and initialization contract |
|---|---|
| `DesktopWsi` | `DesktopWindowRenderTarget`; the desktop host owns the native window and render loop, while the backend target driver owns surface/swapchain policy. |
| `Presentationless` | `PresentationlessRenderTarget`; fixed engine-owned color/depth slots, no surface, swapchain, acquire, or present. |
| `Component` | `ComponentRenderTarget`; fixed output with the same presentationless device path for isolated profiling workloads. |
| `HeadlessWsi` | `HeadlessWsiRenderTarget`; requires `VK_EXT_headless_surface`, a present-capable queue, and swapchain support. Unsupported requests fail explicitly and are not silently replaced with presentationless rendering. |
| `OpenXr` | `OpenXrRenderTarget`; runtime-owned images are leased to the renderer and returned through OpenXR acquire/wait/release ordering rather than desktop presentation. |
| `BrowserCanvas` | Reserved portable host contract for future WebGL2/WebGPU backends. It carries browser-canvas output state without exposing JavaScript or browser-native handles through the shared renderer API. |

`VulkanRendererBackendFactory` constructs Vulkan exclusively from the frozen
host context. OpenGL accepts the same context boundary but currently requires
the desktop-window capability. BrowserCanvas is an architectural reservation,
not an implemented backend.

---

## Window Creation

### `Engine.CreateWindow()` (`XREngine.Runtime.Bootstrap/RenderingHost/Engine.Windows.cs`)

This is where the OS window and renderer are actually created:

```csharp
public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
{
    bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
    var resizeStrategy = ResolveInteractiveResizeStrategy(windowSettings);
    RuntimeWindowCreateOptions options = GetWindowOptions(windowSettings, preferHdrOutput, resizeStrategy);
    bool useWindowPumpHost = RuntimeWindowApplicationServices.Current.ShouldCreateWindowOnHost(...);
    XRWindow window = useWindowPumpHost
        ? RuntimeWindowApplicationServices.Current.CreateWindow(
            () => CreateWindowInstance(options, RenderBackendFallbackPolicy.RequireRequested), ...)
        : CreateWindowInstance(options, EffectiveSettings.RenderBackendFallbackPolicy);
    FinishWindowCreation(windowSettings, window, preferHdrOutput);
    return window;
}
```

Key points:

1. **`GetWindowOptions()`** translates `ERenderLibrary` into `RuntimeGraphicsApiKind` and packages neutral `RuntimeWindowCreateOptions`; the desktop leaf translates that request into Silk.NET options.
2. **Explicit Vulkan fallback**: If `XRWindow` construction fails in Vulkan mode, `CreateWindowInstance` retries with OpenGL 4.6 only when `RenderBackendFallbackPolicy` permits fallback. The split window-pump path requires Vulkan and does not retry. Required Vulkan startup fails with a visible diagnostic.
3. **HDR surface**: For OpenGL, a 64-bit preferred bit depth is requested when HDR is enabled. Vulkan handles HDR through swapchain format negotiation instead.
4. After the window is created, viewports are created for local players and the target world is assigned.

### `GetWindowOptions()` Details

```csharp
private static RuntimeWindowCreateOptions GetWindowOptions(
    GameWindowStartupSettings windowSettings,
    bool preferHdrOutput,
    EInteractiveWindowResizeStrategy resizeStrategy)
{
    // Borderless uses the primary display extent; other states use authored position/size.
    bool requestHdrSurface = preferHdrOutput && Engine.EffectiveSettings.PreferredRenderBackend != ERenderLibrary.Vulkan;
    return new RuntimeWindowCreateOptions(
        Startup: ToRuntimeWindowValues(windowSettings, resizeStrategy),
        GraphicsApi: Engine.EffectiveSettings.PreferredRenderBackend == ERenderLibrary.Vulkan
            ? RuntimeGraphicsApiKind.Vulkan : RuntimeGraphicsApiKind.OpenGL,
        ResizeStrategy: resizeStrategy,
        Purpose: RuntimeWindowPurpose.Presentation,
        Position: position,
        Size: size,
        ColorBits: requestHdrSurface ? 64 : 24,
        DepthBits: 0,
        StencilBits: 8,
        OpenGlMajorVersion: 4,
        OpenGlMinorVersion: 6,
        SwapAutomatically: true,
        /* visibility, VSync, HDR, framebuffer and GL-context flags */
    );
}
```

---

## XRWindow Constructor

Defined in `XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs`:

```csharp
public XRWindow(RuntimeWindowCreateOptions options)
{
    _viewports.CollectionChanged += ViewportsChanged;
    _desktopBackend = RuntimeWindowBackendRegistry.RequireFactory().Create(in options);
    LinkWindow(); // Engine play-mode transitions.
    _desktopBackend.Initialize(new DesktopWindowEventSink(this));
    PublishWindowSurfaceSnapshot(...);
    _renderer = CreateRendererForCurrentWindow("initial desktop window construction");
}
```

Step by step:

| Step | What Happens |
|------|--------------|
| `RuntimeWindowBackendRegistry.RequireFactory()` | Resolves the installed desktop window factory; `DesktopSilkWindowBackendFactory` owns Silk.NET creation. |
| `LinkWindow()` | Registers engine play-mode transition callbacks. |
| `_desktopBackend.Initialize(...)` | Opens the OS window and graphics context, routing native events through `DesktopWindowEventSink`. |
| Snapshot publication | Records effective window/framebuffer extents and event state after native initialization. |
| `CreateRendererForCurrentWindow(...)` | Resolves the OpenGL or Vulkan renderer from the registered renderer-backend factory using the initialized backend's actual API kind. |

The renderer is chosen from the initialized desktop backend's **actual** `RuntimeGraphicsApiKind`. This makes explicit Vulkan-to-OpenGL retry in `CreateWindowInstance()` work when fallback is allowed: the retry creates an OpenGL backend and the factory creates an `OpenGLRenderer`.

### Backend Escape Hatches And Safe Access

`XRWindow` no longer exposes the Silk.NET window or input context. Gameplay and editor code use:

- `FileDropped`, `ClosingRequested`, `FramebufferResized`, `RequestClose()`,
  `RequestMouseCapture(bool)`, `WindowTitle`, and `WindowSizeSnapshot` for
  common window interactions.
- `LatestWindowSurfaceSnapshot`, `LatestWindowEventSnapshot`, and
  `LatestWindowInputSnapshot` for state consumption.
- `DesktopGlContext` and `DesktopVulkanSurface` are typed, borrowed backend services for GL context and Vulkan WSI operations; backend code must preserve their owner-thread and generation rules.
- `IRuntimeRenderingHostServices.EnqueueWindowThreadTask` /
  `InvokeWindowThreadTask<T>` for native window mutations.
- `IRuntimeRenderingHostServices.EnqueueRenderThreadTask` /
  `InvokeRenderThreadTask<T>` for renderer, swapchain, GPU-resource, preview,
  and readback work.

Local player input is bound from `IRuntimeLocalPlayerViewport.InputSnapshot`.
Snapshot-backed keyboard and mouse adapters feed the existing
`LocalInputInterface` registration model without exposing Silk input devices to
gameplay or editor possession code. Mouse capture/hide requests are sent through
`IRuntimeLocalPlayerViewport.RequestMouseCapture(...)`, preserving native
window-thread ownership.

### Renderer Constructor Side Effects

- **`OpenGLRenderer`**: The constructor calls `GetAPI()` → `GL.GetApi(XRWindow.DesktopGlContext.GetProcAddress)` → `InitGL(api)`, which queries GPU info, enumerates extensions, enables multisampling, sets up debug callbacks, and reads the binary shader cache. All OpenGL state setup happens here.
- **`VulkanRenderer`**: The constructor freezes the target driver and creates
  managed subsystem owners, but creates no native Vulkan objects. Native
  initialization remains deferred.

---

## Deferred Renderer Initialization

The full renderer initialization does **not** happen in the constructor. For a
desktop host it is deferred until the window has both **viewports** and a
**target world**. Presentationless, component, headless-WSI, and OpenXR hosts
invoke the same backend initialization through their explicit host lifecycle.

### VerifyTick / BeginTick

After `CreateWindow()` calls `window.SetWorld(targetWorld)`, this triggers a property change notification that calls `VerifyTick()`:

```csharp
private void VerifyTick()
{
    if (ShouldBeRendering())  // Viewports.Count > 0 && TargetWorldInstance != null
    {
        if (!IsTickLinked) { IsTickLinked = true; BeginTick(); }
    }
    else
    {
        if (IsTickLinked) { IsTickLinked = false; EndTick(); }
    }
}

private void BeginTick()
{
    Renderer.Initialize();                         // API-specific init
    _rendererInitialized = true;
    RuntimeRenderingHostServices.Scheduling.SubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);
}
```

This is where:
- **OpenGL**: `Initialize()` is a no-op (all setup already done in constructor via `InitGL`)
- **Vulkan**: `Initialize()` creates the Vulkan instance, debug messenger, surface, picks a physical device, creates the logical device, command pool, descriptor set layout, entire swapchain with all dependent objects, and sync primitives

After `BeginTick()`, host scheduling connects the window's swap and render callbacks to the engine timer, entering the active render loop.

---

## The Render Loop

Once `Engine.Initialize()` completes, the main thread enters:

```
RunGameLoop()        → starts update/physics threads via Engine.Time.Timer.RunGameLoop()
BlockForRendering()  → blocks main thread via Engine.Time.Timer.BlockForRendering(IsEngineStillActive)
```

The timer drives the render loop. Each frame, for each registered window:

```
Timer fires RenderFrame event
  → XRWindow.RenderFrame()
      → ConsumeLatestWindowSurfaceSnapshotForRenderFrame()
      → ProcessPendingFramebufferResize()
      → _desktopBackend.DispatchRender() // Desktop backend delivers render callback
          → XRWindow.RenderCallback(delta)   // The main per-frame method
```

The window owner separately pumps native events through `_desktopBackend.PumpEvents()` and publishes surface, close/focus, and input snapshots. The render path consumes those snapshots; it does not call a raw Silk `IWindow`.

---

## Window Ownership And Render Thread

The window thread and the render thread are separate roles, even when one OS thread holds both. New code must not treat "main thread" and "render thread" as the same term; `EnqueueMainThreadTask(...)`-style names are compatibility names for render-thread work.

| Role | Owns |
|---|---|
| Window thread | Native window creation and destruction, event pumping, callbacks, title, size, focus, cursor, and raw input configuration. |
| Render thread | Graphics context, renderer, swapchain, render-pipeline state, GPU resources, readback, and present. |

`RuntimeRenderThreadHost` (`XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs`) owns the render loop behind `Engine.BlockForRendering()` and stamps `RenderThreadId` from inside the render thread. It has two modes:

- `CollapsedWindowRenderThread` (default): the process entry thread pumps native events and runs the render loop. GLFW window and event APIs stay on the process main thread. Vulkan-to-OpenGL fallback follows the normal fallback policy.
- `SplitWindowPumpPrototype`: `RuntimeWindowPumpHost` (`XREngine.Runtime.Platform.Desktop`) pumps an SDL-backed window on its own thread, and the render loop runs on a dedicated render thread. `XRE_WINDOW_PUMP_HOST=sdl-prototype` (or `sdl`, `1`) requests it. It is Windows-only, Vulkan-only, and requires native-backend resize for every startup window (`EInteractiveWindowResizeStrategy.SdlBackend`). The split path does not fall back to OpenGL. `Engine.CreateWindows(...)` waits on a startup attachment barrier before `Engine.Initialize(...)` continues.

Mailboxes connect the roles:

- `IRuntimeRenderingHostServices.EnqueueWindowThreadTask` posts window work without blocking. `InvokeWindowThreadTask<T>` is the request/reply form for startup and shutdown ordering.
- `EnqueueRenderThreadTask` and `InvokeRenderThreadTask<T>` route renderer, swapchain, preview, and readback work.
- `WindowMailboxDiagnostics` reports queue depth, wait time, wrong-thread calls, blocked waits, flushes, flush timeouts, shutdown drains, and stopping state.

App and editor code reads immutable snapshots from `RuntimeWindowOwnership.cs`: `WindowSurfaceSnapshot` (sequence, client extent, DPI scale, focus, minimized, interactive-resize state, timestamp), `WindowEventSnapshot`, and `WindowInputSnapshot` (key, mouse, and text transitions, pointer position and delta, scroll delta, capture state). The window thread publishes surface snapshots latest-only with increasing sequence numbers; the render frame consumes at most the newest one and reports dropped sequences. `WindowInputSnapshotAccumulator` resets per-publication deltas. Snapshot publication does not allocate in the resize path.

Close and dispose are split for externally pumped windows: renderer teardown runs on the render thread, native input and window teardown run on the window thread, and engine window removal returns to the render thread. Shutdown flushes the window mailbox before it completes the pump queue.

Keep the process entry thread STA-capable. ImGui file dialogs, clipboard, drag-and-drop, and native UI bootstrap still depend on it until each has a thread-affinity adapter.

---

## Interactive Resize

`WindowResizeController` (render-thread owned) tracks four extents in `WindowResizeExtents`:

| Extent | Meaning |
|---|---|
| `NativeClientExtent` | Latest native client size from the window thread. Updates camera and display aspect only. |
| `PresentationExtent` | Swapchain or default-framebuffer extent. |
| `PipelineOutputExtent` | Final output region. `XRViewport.SetPresentationOutputExtent(...)` changes it cheaply during a drag. |
| `FullInternalExtent` | Render-pipeline internal resources. `XRViewport.SetFullInternalExtent(...)` changes it. |

During a live drag, the render frame updates the presentation and output extents first. GBuffer, depth, velocity, lighting, AO, bloom, shadows, volumetrics, and temporal histories do not rebuild on each drag tick. The controller requests a full internal generation only on a threshold crossing, a pause, a maximum lag time, or the settled signal (`WM_EXITSIZEMOVE` or equivalent). It commits `FullInternalExtent` only after each viewport's active `XRRenderPipelineInstance` generation matches the target extent, and it rejects stale pending generations.

The Win32 Vulkan swapchain is not a lag layer. Without present scaling, Vulkan recreates the swapchain at the current surface extent, or it skips or coalesces the present tick (minimized, zero extent, or recreation busy). `CanPresentMismatchedSwapchainExtent(...)` stays false until `VkSwapchainPresentScalingCreateInfoKHR` is queried and validated. Vulkan swapchain recreation does not pump native events. Dynamic-rendering areas and blit regions clamp to the live source and destination image extents. A frame whose scene pipeline skipped for resize catch-up does not acquire or present, so the last valid image stays visible.

### Resize strategies

`EInteractiveWindowResizeStrategy` selects how a window behaves during a native border drag. `XRWindow` owns one `IInteractiveResizeStrategy`, logs the resolved strategy and the actual windowing backend, and routes callback renders through one guarded `RenderInteractiveResizeFrame(...)` helper. `ProcessPendingFramebufferResize()` is the only code that changes viewport sizes and calls `Renderer.FrameBufferInvalidated()`.

| Strategy | `XRE_INTERACTIVE_RESIZE_STRATEGY` | Behavior |
|---|---|---|
| `Default` | `default` | No callbacks, hooks, or backend change. Baseline. |
| `GlfwRefreshCallback` | `glfw-refresh` | Renders from the GLFW window refresh callback. |
| `GlfwResizeCallbackRender` | `glfw-resize` | Renders from Silk.NET resize callbacks when the backend delivers them during a drag. Rate-limited and size-coalesced. |
| `SdlBackend` | `sdl` | Creates the window through the Silk.NET SDL backend. A change takes effect only after window recreation. Required for the split window pump. |
| `Win32ModalLoopTimer` | `win32-timer` | Windows only. Subclasses the window procedure. `WM_SIZING`/`WM_SIZE` record the latest client size; a modal-loop timer and a low-priority `WM_PAINT` sequence keep frames moving while the cursor is held still. Full internal resize waits for `WM_EXITSIZEMOVE`. `XRE_WIN32_INTERACTIVE_RESIZE_TIMER_MS` (1 to 250) tunes the timer. |
| `EngineBorderlessResize` | `borderless` | Engine-owned resize grips on the `UseNativeTitleBar=false` path. It does not yet give continuous live resize. |

The parser also accepts the enum names and short aliases (`win32`, `sdlbackend`, `engineborderless`) and ignores case, hyphens, and underscores. Resolution order: environment variable, per-window `GameWindowStartupSettings.InteractiveResizeStrategy` (default `Default`), then `EditorPreferences.InteractiveResizeStrategy` (default `Win32ModalLoopTimer`). The editor Engine State window shows the strategy, the backend, the four extents, pending generations, snapshot sequences, mailbox diagnostics, and per-strategy counters (callbacks, interactive renders, suppressed renders, queued resizes, last reason).

In the collapsed mode, the Win32 modal loop holds the thread that also renders. A strategy can either render synchronously from the modal loop, which slows the native drag, or defer, which shows the new size only after mouse-up. Smooth chrome with a live trailing framebuffer needs the split window pump or a real engine-owned resize pump.

---

## Per-Frame Render Callback

`RenderCallback(double delta)` in `XRWindow.cs` is the core of each frame:

```
1. Engine.Rendering.Stats.BeginFrame()        — Reset per-frame statistics
2. Renderer.ProcessPendingUploads()            — Upload queued buffers/textures
3. Set Renderer as active, set AbstractRenderer.Current
4. TargetWorldInstance.GlobalPreRender()        — Pre-render hooks (lighting, shadows, etc.)
5. RenderViewportsCallback?.Invoke()           — External callbacks (e.g. ImGui)
6. RenderWindowViewports()                     — Render all viewports via their pipelines
7. TargetWorldInstance.GlobalPostRender()       — Post-render hooks
8. Renderer.RenderWindow(delta)                — API-specific frame completion:
     • OpenGL: no-op (desktop backend performs automatic swap)
     • Vulkan: acquire image, record command buffer, submit, present
9. PostRenderViewportsCallback?.Invoke()       — Post-render external callbacks
```

The viewport rendering step dispatches differently based on context:
- **Normal mode**: Iterates all viewports, calling `viewport.Render()` which executes the render pipeline
- **Editor scene-panel mode**: Renders to an offscreen FBO for docking in the ImGui editor UI
- **VR mirror composition**: Delegates to OpenXR for desktop mirror rendering

A **circuit breaker** protects the window render loop. Exceptions that escape
the complete callback temporarily suppress later callbacks with a linear
100 ms-per-consecutive-failure backoff capped at five seconds. Viewport and
world pre/post-render failures are isolated before the Vulkan end-of-frame
work, so `WindowRenderCallback` still runs and the swapchain can present known
content instead of remaining uninitialized.

### Vulkan desktop frame coordinator

Vulkan's `WindowRenderCallback` is a short coordinator, not the implementation
of every end-of-frame operation. It creates one stack-only
`DesktopFrameAttempt`, then calls responsibility-specific phases:

1. preflight the live surface, resize policy, and resource generations;
2. prepare the attempt's captured desktop frame slot;
3. acquire and prepare a swapchain image;
4. record scene and volatile overlay command buffers;
5. submit;
6. present or apply the required recovery policy; and
7. publish telemetry and finalize acquire/upload ownership.

The attempt captures its frame number and desktop in-flight slot at entry.
Those values do not change even if global renderer state advances later.
Acquire and upload ownership transitions are explicit, and finalization fails
if an acquired image or upload batch remains unresolved. The outer failure
boundary settles a granted acquire before propagating a primary exception;
telemetry cleanup cannot hide that primary exception.

Entry also publishes a coherent atomic desktop activity snapshot:
`IsActive`, `FrameNumber`, and `FrameSlot`. Reentrant callbacks are rejected
without consuming a frame number. The matching publication token is required
to clear activity, so an old exit cannot clear a newer attempt. OpenXR uses the
same snapshot when deciding whether desktop retirement work is safe. Desktop
entry/exit and OpenXR's complete retirement check-and-drain interval hold the
same `_desktopFrameRetirementGate`, closing the cross-thread race between
classifying a desktop slot as inactive and destroying its retired resources.

The circuit breaker remains an `XRWindow` policy around this backend
coordinator. Device loss does not use normal backoff recovery: Vulkan records
the first failing operation, marks the logical device terminal, fails pending
completion markers/readbacks, and stops further submission. `XRWindow` detects
that terminal state before and during rendering and attempts to recreate the
renderer on the existing window. A successful recreation resets the circuit
breaker and invalidates renderer-dependent resources.

---

## Complete Call Chain

```
Program.Main()
  └─ Engine.Run(startupSettings, gameState)
       └─ Engine.Initialize()
            ├─ UserSettings = GameSettings.DefaultUserSettings
            │    └─ PreferredRenderBackend = OpenGL | Vulkan
            ├─ ValidateGpuRenderingStartupConfiguration()
            ├─ ConfigureJobManager()
            ├─ CreateWindows(startupSettings.StartupWindows)
            │    └─ CreateWindow(windowSettings)
            │         ├─ GetWindowOptions()
            │         │    └─ ERenderLibrary → RuntimeGraphicsApiKind.OpenGL | Vulkan
            │         ├─ new XRWindow(options)
            │         │    ├─ RuntimeWindowBackendRegistry.RequireFactory().Create(options)
            │         │    ├─ LinkWindow()                      ← play-mode subscriptions
            │         │    ├─ Desktop backend Initialize()     ← OS window/context created
            │         │    └─ renderer factory → OpenGLRenderer | VulkanRenderer
            │         ├─ [catch: Vulkan fail → retry with OpenGL only when fallback policy permits]
            │         ├─ CreateViewports(localPlayers)
            │         └─ window.SetWorld(targetWorld)
            │              └─ VerifyTick()
            │                   └─ BeginTick()
            │                        ├─ Renderer.Initialize()   ← full API init
            │                        └─ Subscribe to Timer events
            ├─ Time.Initialize()
            ├─ InitializeNetworking()
            └─ BeginPlayAllWorlds()
       └─ RunGameLoop()          ← starts update/physics threads
       └─ BlockForRendering()    ← main thread render loop until shutdown
       └─ Cleanup()              ← dispose all resources
```

---

## Class Hierarchy

```
AbstractRenderer                              (XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs)
  ├─ XRWindow : XRWindow                      (neutral desktop-window facade)
  │    ├─ DesktopGlContext : IRuntimeWindowGlContext?
  │    └─ DesktopVulkanSurface : IRuntimeWindowVulkanSurface?
  ├─ abstract Initialize()
  ├─ abstract CleanUp()
  ├─ abstract WindowRenderCallback(delta)
  ├─ RenderWindow(delta)                      → calls WindowRenderCallback(delta)
  ├─ ProcessPendingUploads()                  → virtual, overridden per API
  ├─ Render object cache                      (GenericRenderObject → AbstractRenderAPIObject)
  └─ abstract CreateAPIRenderObject()
      │
      ▼
AbstractRenderer<TAPI> where TAPI : NativeAPI
  ├─ Api : TAPI                               (lazy-initialized via GetAPI())
  └─ abstract GetAPI()
      │
      ├── OpenGLRenderer : AbstractRenderer<GL>
      │     GetAPI()        → GL.GetApi(DesktopGlContext.GetProcAddress) + InitGL()
      │     Initialize()    → no-op (setup done in GetAPI)
      │     WindowRenderCallback() → no-op (desktop backend handles automatic swap)
      │
      └── VulkanRenderer : AbstractRenderer<Vk>
            GetAPI()        → Vk.GetApi()
            Initialize()    → Full Vulkan setup (instance → swapchain → sync)
            WindowRenderCallback() → short phase coordinator:
                                     Preflight/Slot/Acquire/Record/Submit/Present/Finalize
```

---

## See Also

- [OpenGL Renderer](opengl-renderer.md) — OpenGL-specific initialization and render loop details
- [Vulkan Renderer](vulkan-renderer.md) — Vulkan-specific initialization and render loop details
- [Rendering Code Map](code-map.md) — Full source file inventory
