# Window Creation & Renderer Initialization

This document describes how XREngine creates OS windows on startup, selects a graphics API (OpenGL or Vulkan), instantiates the appropriate renderer, and begins the render loop.

## Table of Contents

- [Entry Points](#entry-points)
- [Engine.Run() → Engine.Initialize()](#enginerun--engineinitialize)
- [Renderer API Selection](#renderer-api-selection)
- [Window Creation](#window-creation)
- [XRWindow Constructor](#xrwindow-constructor)
- [Deferred Renderer Initialization](#deferred-renderer-initialization)
- [The Render Loop](#the-render-loop)
- [Per-Frame Render Callback](#per-frame-render-callback)
- [Complete Call Chain](#complete-call-chain)
- [Class Hierarchy](#class-hierarchy)

---

## Entry Points

Both the Editor and Server applications share the same startup pattern: configure settings, create a world, then call `Engine.Run()`.

### Editor (`XREngine.Editor/Program.cs`)

```csharp
[STAThread]
private static void Main(string[] args)
{
    // ... load settings, create world ...
    var startupSettings = GetEngineSettings(targetWorld);
    var gameState = Engine.LoadOrGenerateGameState();
    Engine.Run(startupSettings, gameState);
}
```

### Server (`XREngine.Server/Program.cs`)

```csharp
private static void Main(string[] args)
{
    // ... WebAPI setup, load settings ...
    XRWorld targetWorld = CreateWorld();
    Engine.Run(GetEngineSettings(targetWorld), Engine.LoadOrGenerateGameState());
}
```

Both converge on `Engine.Run(GameStartupSettings, GameState)`.

---

## Engine.Run() → Engine.Initialize()

Defined in `XRENGINE/Engine/Engine.Lifecycle.cs`:

```csharp
public static void Run(GameStartupSettings startupSettings, GameState state)
{
    if (Initialize(startupSettings, state))
    {
        RunGameLoop();        // Starts update/physics threads
        BlockForRendering();  // Blocks main thread for render submission
    }
    Cleanup();
}
```

`Initialize()` performs these steps in order:

| Step | Description |
|------|-------------|
| 1 | Store `GameSettings` and `UserSettings` (includes `RenderLibrary` choice) |
| 2 | `ValidateGpuRenderingStartupConfiguration()` — checks for debug overrides |
| 3 | `ConfigureJobManager()` — sets up parallel processing |
| 4 | **`CreateWindows(startupSettings.StartupWindows)`** — creates OS windows and renderers |
| 5 | Initialize secondary GPU context if supported |
| 6 | Initialize VR asynchronously (if configured) |
| 7 | `Time.Initialize()` — start the timing system for update/render ticks |
| 8 | Initialize networking (if configured) |
| 9 | Wire up profiler UDP sender |
| 10 | `BeginPlayAllWorlds()` — activate all scenes |

After `Initialize()` returns, `RunGameLoop()` spawns update and physics threads, and `BlockForRendering()` takes over the main thread for render submission until all windows close.

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

The render library is stored in `UserSettings.RenderLibrary`:

```csharp
private ERenderLibrary _renderLibrary = ERenderLibrary.OpenGL;  // default
public ERenderLibrary RenderLibrary { get; set; }
```

This value is set from the startup configuration. For the Editor, it comes from the world settings JSON:

```csharp
DefaultUserSettings = new UserSettings()
{
    RenderLibrary = UnitTestingWorld.Toggles.RenderAPI,  // e.g. "Vulkan" or "OpenGL"
}
```

The settings JSON (`Assets/UnitTestingWorldSettings.json`) determines which API is used. The Editor typically defaults to Vulkan; the Server defaults to OpenGL.

---

## Window Creation

### `Engine.CreateWindow()` (`XRENGINE/Engine/Engine.Windows.cs`)

This is where the OS window and renderer are actually created:

```csharp
public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
{
    bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
    var options = GetWindowOptions(windowSettings, preferHdrOutput);

    XRWindow window;
    try
    {
        window = new XRWindow(options, windowSettings.UseNativeTitleBar);
    }
    catch (Exception ex) when (options.API.API == ContextAPI.Vulkan)
    {
        // Vulkan init failed → automatic fallback to OpenGL 4.6
        Debug.RenderingWarning($"Vulkan initialization failed, falling back to OpenGL: {ex.Message}");
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
            ContextFlags.ForwardCompatible, new APIVersion(4, 6));
        window = new XRWindow(options, windowSettings.UseNativeTitleBar);
    }

    window.PreferHDROutput = preferHdrOutput;
    CreateViewports(windowSettings.LocalPlayers, window);
    window.UpdateViewportSizes();
    _windows.Add(window);
    Rendering.ApplyRenderPipelinePreference();
    window.SetWorld(windowSettings.TargetWorld);
    return window;
}
```

Key points:

1. **`GetWindowOptions()`** translates `ERenderLibrary` into a Silk.NET `GraphicsAPI`:
   - `ERenderLibrary.Vulkan` → `ContextAPI.Vulkan` with API version 1.1
   - `ERenderLibrary.OpenGL` → `ContextAPI.OpenGL` with version 4.6, Core profile, forward-compatible
2. **Automatic Vulkan fallback**: If the `XRWindow` constructor throws while in Vulkan mode, the engine catches the exception and retries with OpenGL 4.6. This ensures the application can always start even on systems without Vulkan support.
3. **HDR surface**: For OpenGL, a 64-bit preferred bit depth is requested when HDR is enabled. Vulkan handles HDR through swapchain format negotiation instead.
4. After the window is created, viewports are created for local players and the target world is assigned.

### `GetWindowOptions()` Details

```csharp
private static WindowOptions GetWindowOptions(GameWindowStartupSettings windowSettings, bool preferHdrOutput)
{
    // Determine window state (Fullscreen, Windowed, Borderless)
    // ...

    bool requestHdrSurface = preferHdrOutput && UserSettings.RenderLibrary != ERenderLibrary.Vulkan;
    int preferredBitDepth = requestHdrSurface ? 64 : 24;

    return new WindowOptions(
        isVisible: true,
        position, size,
        updateRate: 0.0, frameRate: 0.0,
        api: UserSettings.RenderLibrary == ERenderLibrary.Vulkan
            ? new GraphicsAPI(ContextAPI.Vulkan, ...)
            : new GraphicsAPI(ContextAPI.OpenGL, ...),
        title, windowState, windowBorder,
        vSync, shouldSwapAutomatically: true,
        videoMode: VideoMode.Default,
        preferredBitDepth, preferredDepthBits: 8,
        ...
    );
}
```

---

## XRWindow Constructor

Defined in `XRENGINE/Rendering/API/XRWindow.cs`:

```csharp
public XRWindow(WindowOptions options, bool useNativeTitleBar)
{
    _viewports.CollectionChanged += ViewportsChanged;

    Silk.NET.Windowing.Window.PrioritizeGlfw();           // Prefer GLFW backend
    Window = Silk.NET.Windowing.Window.Create(options);    // Create Silk.NET IWindow
    UseNativeTitleBar = useNativeTitleBar;

    LinkWindow();          // Subscribe to window events (Render, FocusChanged, Closing, Load)
    Window.Initialize();   // Initialize the OS window + graphics context

    Renderer = Window.API.API switch
    {
        ContextAPI.OpenGL => new OpenGLRenderer(this, true),
        ContextAPI.Vulkan => new VulkanRenderer(this, true),
        _ => throw new Exception($"Unsupported API: {Window.API.API}"),
    };
}
```

Step by step:

| Step | What Happens |
|------|--------------|
| `PrioritizeGlfw()` | Tells Silk.NET to use the GLFW windowing backend (cross-platform, well-tested) |
| `Window.Create(options)` | Creates the underlying Silk.NET `IWindow` with the requested graphics API |
| `LinkWindow()` | Subscribes to `Render`, `FocusChanged`, `Closing`, `Load` events on the Silk.NET window |
| `Window.Initialize()` | Opens the OS window, creates the graphics context (GL context or Vulkan surface) |
| Pattern match on `ContextAPI` | Instantiates either `OpenGLRenderer` or `VulkanRenderer` |

The renderer is chosen by inspecting the **actual** `ContextAPI` from the initialized window, not from `ERenderLibrary` directly. This makes the Vulkan-to-OpenGL fallback in `CreateWindow()` work transparently — if the retry creates an OpenGL context, the pattern match will create an `OpenGLRenderer`.

### Renderer Constructor Side Effects

- **`OpenGLRenderer`**: The constructor calls `GetAPI()` → `GL.GetApi(Window.GLContext)` → `InitGL(api)`, which queries GPU info, enumerates extensions, enables multisampling, sets up debug callbacks, and reads the binary shader cache. All OpenGL state setup happens here.
- **`VulkanRenderer`**: The constructor only calls `Vk.GetApi()` to obtain the Vulkan API entry points. No Vulkan objects are created yet — that's deferred.

---

## Deferred Renderer Initialization

The full renderer initialization does **not** happen in the constructor. It is deferred until the window has both **viewports** and a **target world**.

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
    Engine.Time.Timer.SwapBuffers += SwapBuffers;  // Register for swap events
    Engine.Time.Timer.RenderFrame += RenderFrame;  // Register for render events
}
```

This is where:
- **OpenGL**: `Initialize()` is a no-op (all setup already done in constructor via `InitGL`)
- **Vulkan**: `Initialize()` creates the Vulkan instance, debug messenger, surface, picks a physical device, creates the logical device, command pool, descriptor set layout, entire swapchain with all dependent objects, and sync primitives

After `BeginTick()`, the window subscribes to the engine timer's `SwapBuffers` and `RenderFrame` events, entering the active render loop.

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
      → Window.DoEvents()     // Process OS window events
      → Window.DoRender()     // Triggers Silk.NET Render event
          → XRWindow.RenderCallback(delta)   // The main per-frame method
```

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
     • OpenGL: no-op (Silk.NET handles SwapBuffers automatically)
     • Vulkan: acquire image, record command buffer, submit, present
9. PostRenderViewportsCallback?.Invoke()       — Post-render external callbacks
```

The viewport rendering step dispatches differently based on context:
- **Normal mode**: Iterates all viewports, calling `viewport.Render()` which executes the render pipeline
- **Editor scene-panel mode**: Renders to an offscreen FBO for docking in the ImGui editor UI
- **VR mirror composition**: Delegates to OpenXR for desktop mirror rendering

A **circuit breaker** protects the render loop — if exceptions occur repeatedly, frames are temporarily skipped with exponential backoff (up to 5 seconds) to prevent log spam and runaway failures. Importantly, even when viewport rendering fails, the Vulkan `WindowRenderCallback` still runs to ensure the swapchain presents (otherwise the window shows uninitialized white content).

---

## Complete Call Chain

```
Program.Main()
  └─ Engine.Run(startupSettings, gameState)
       └─ Engine.Initialize()
            ├─ UserSettings = GameSettings.DefaultUserSettings
            │    └─ RenderLibrary = OpenGL | Vulkan
            ├─ ValidateGpuRenderingStartupConfiguration()
            ├─ ConfigureJobManager()
            ├─ CreateWindows(startupSettings.StartupWindows)
            │    └─ CreateWindow(windowSettings)
            │         ├─ GetWindowOptions()
            │         │    └─ ERenderLibrary → ContextAPI.OpenGL | ContextAPI.Vulkan
            │         ├─ new XRWindow(options)
            │         │    ├─ Window.PrioritizeGlfw()
            │         │    ├─ Silk.NET.Window.Create(options)   ← OS window created
            │         │    ├─ LinkWindow()                      ← subscribe events
            │         │    ├─ Window.Initialize()               ← graphics context created
            │         │    └─ Renderer = OpenGLRenderer | VulkanRenderer
            │         ├─ [catch: Vulkan fail → retry with OpenGL]
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
AbstractRenderer                              (XRENGINE/Rendering/API/Rendering/Generic/AbstractRenderer.cs)
  ├─ Window : IWindow                         (Silk.NET native window)
  ├─ XRWindow : XRWindow                      (engine window wrapper)
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
      │     GetAPI()        → GL.GetApi(Window.GLContext) + InitGL()
      │     Initialize()    → no-op (setup done in GetAPI)
      │     WindowRenderCallback() → no-op (Silk.NET handles swap)
      │
      └── VulkanRenderer : AbstractRenderer<Vk>
            GetAPI()        → Vk.GetApi()
            Initialize()    → Full Vulkan setup (instance → swapchain → sync)
            WindowRenderCallback() → Acquire/Record/Submit/Present
```

---

## See Also

- [OpenGL Renderer](opengl-renderer.md) — OpenGL-specific initialization and render loop details
- [Vulkan Renderer](vulkan-renderer.md) — Vulkan-specific initialization and render loop details
- [Rendering Code Map](RenderingCodeMap.md) — Full source file inventory
