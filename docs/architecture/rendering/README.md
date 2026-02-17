# Rendering Architecture

Documentation for XREngine's rendering system — how windows are created, graphics APIs are initialized, and frames are rendered.

## Documents

| Document | Description |
|----------|-------------|
| [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) | How the engine creates OS windows on startup, selects OpenGL or Vulkan, instantiates renderers, and begins the render loop. Start here for the full picture. |
| [OpenGL Renderer](opengl-renderer.md) | OpenGL 4.6 renderer initialization, GL state management, draw call submission, indirect drawing, framebuffer management, and ImGui integration. |
| [Vulkan Renderer](vulkan-renderer.md) | Vulkan 1.3 renderer initialization (instance → swapchain → sync), the explicit frame loop (acquire → record → submit → present), render graph compilation, and resource management. |
| [OpenXR VR Rendering](openxr-vr-rendering.md) | OpenXR session lifecycle, graphics bindings (OpenGL / Vulkan), swapchain management, three-phase frame model, per-eye rendering, mirror blit pipeline, and late-pose updates. |
| [OpenVR (SteamVR) Rendering](openvr-rendering.md) | SteamVR initialization, render target creation (two-pass / single-pass stereo), compositor submission via OpenGL texture handles, prediction timing, and frame statistics. |
| [Rendering Code Map](RenderingCodeMap.md) | Source file organization for mesh rendering, meshlet rendering, GPU compute stages, and shared infrastructure. |

## Quick Architecture Overview

```
Program.Main()
  └─ Engine.Run()
       ├─ Engine.Initialize()
       │    ├─ UserSettings.RenderLibrary    ← OpenGL or Vulkan
       │    ├─ CreateWindows()
       │    │    └─ for each window:
       │    │         ├─ Silk.NET.Window.Create()     ← OS window
       │    │         ├─ Window.Initialize()          ← Graphics context
       │    │         ├─ Renderer = OpenGLRenderer     ← (or VulkanRenderer)
       │    │         │    └─ [Vulkan fallback → OpenGL if init fails]
       │    │         ├─ CreateViewports()
       │    │         └─ SetWorld() → BeginTick()
       │    │              └─ Renderer.Initialize()   ← Full API setup
       │    ├─ Time.Initialize()
       │    ├─ BeginPlayAllWorlds()
       │    └─ [VR enabled?]
       │         ├─ OpenXR: OpenXRAPI.Startup() → session → render on RenderViewportsCallback
       │         └─ OpenVR: InitSteamVR() → InitRender() → timer callbacks
       ├─ RunGameLoop()          ← Update/physics threads
       └─ BlockForRendering()    ← Main thread render loop
```

### Key Differences: OpenGL vs Vulkan

| Aspect | OpenGL | Vulkan |
|--------|--------|--------|
| **Context** | Obtained from Silk.NET GL context | `Vk.GetApi()` entry points |
| **Initialization** | Eager (in constructor via `InitGL`) | Deferred (in `Initialize()`: instance → device → swapchain) |
| **Frame completion** | Automatic (Silk.NET SwapBuffers) | Explicit (acquire → record → submit → present) |
| **Command model** | Immediate-mode state machine | Command buffer recording + deferred submission |
| **Synchronization** | Implicit (driver-managed) | Explicit (semaphores + fences, double-buffered) |
| **Shader format** | GLSL (compiled by driver) | GLSL → SPIR-V (compiled at build/runtime) |
| **Pipeline state** | Mutable global state | Immutable pipeline objects (cached to disk) |
| **`WindowRenderCallback`** | Empty (no-op) | Full frame lifecycle (~180 lines) |
