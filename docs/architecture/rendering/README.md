# Rendering Architecture

Documentation for XREngine's rendering system — how windows are created, graphics APIs are initialized, and frames are rendered.

## Documents

| Document | Description |
|----------|-------------|
| [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) | How the engine creates OS windows on startup, selects OpenGL or Vulkan, instantiates renderers, and begins the render loop. Start here for the full picture. |
| [Rendering Runtime Overview](runtime-overview.md) | How worlds, visibility collection, GPUScene, render pipelines, and pass execution fit together at runtime. |
| [Rendering Frame Lifecycle And Dispatch Paths](frame-lifecycle-and-dispatch-paths.md) | The end-to-end `CollectVisible -> SwapBuffers -> Render` lifecycle, how worlds/viewports/scenes hand buffers across threads, and how CPU, GPU, BVH, octree, quadtree, and meshlet-related paths fit together. |
| [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md) | Implemented contract for declared pipeline resources, generation-based materialization, staged resize, and atomic resource swaps. Design source: [proposal](../../work/design/rendering/render-pipeline-resource-lifecycle-design.md). |
| [World Shader Prewarm Graph](../../work/design/rendering/world-shader-prewarm-graph-design.md) | Design proposal for collecting world, component, transform, asset, render-pipeline, shader, and material dependencies into prewarmable shader program combinations. |
| [Mesh Submission Strategies](mesh-submission-strategies.md) | The `EMeshSubmissionStrategy` contract for CPU direct, instrumented GPU indirect, zero-readback GPU indirect, and meshlet submission. |
| [Engine Rendering Optimization](../../work/design/rendering/engine-optimization-and-avatar-optimizer-design.md) | Design for renderer performance architecture, draw-call and CPU/GPU-driven tradeoffs, zero-readback rendering, meshlets, visibility buffers, stereo paths, and profiling. Execution roadmap: [engine-rendering-optimization-roadmap.md](../../work/todo/rendering/optimization/engine-rendering-optimization-roadmap.md). |
| [Retinal Visibility Cache Rendering](../../work/design/rendering/retinal-visibility-cache-rendering-design.md) | Proposal for advanced OpenXR quad-view foveated VR rendering: compact visibility buffers, foveated shadelets, shared head-space lighting, stereo/layer reuse, and Forward+ transparency fallback. |
| [Avatar Optimization And Virtualized Rendering](../../work/design/rendering/avatar-optimization-and-virtualized-rendering-design.md) | Design for automatic in-editor avatar optimization, generated variants, cluster-virtualized skinned rendering, and Gaussian-splat distant-crowd LOD. Execution roadmap: [avatar-optimization-roadmap.md](../../work/todo/avatar/avatar-optimization-roadmap.md). |
| [Dynamic Indirect Material Bindings](../../work/design/rendering/dynamic-indirect-material-bindings.md) | Proposal for replacing hardcoded zero-readback material rows with pass-declared layouts and shader annotation-driven material-table variants. |
| [Uber Shader Varianting](uber-shader-varianting.md) | How Uber materials store authored feature/property state, generate fragment variants, and expose requested-vs-active status to the editor. |
| [Uber Shader UI Annotations](uber-shader-ui-annotations.md) | How `//@feature`, `//@property`, and related directives define the curated Uber inspector surface and how Uber-specific validation treats missing coverage. |
| [OpenGL Renderer](opengl-renderer.md) | OpenGL 4.6 renderer initialization, GL state management, draw call submission, indirect drawing, framebuffer management, and ImGui integration. |
| [Vulkan Renderer](vulkan-renderer.md) | Vulkan 1.3 renderer initialization (instance → swapchain → sync), the explicit frame loop (acquire → record → submit → present), render graph compilation, and resource management. |
| [OpenXR VR Rendering](openxr-vr-rendering.md) | OpenXR session lifecycle, graphics bindings (OpenGL / Vulkan), swapchain management, three-phase frame model, per-eye rendering, mirror blit pipeline, and late-pose updates. |
| [OpenVR (SteamVR) Rendering](openvr-rendering.md) | SteamVR initialization, render target creation (two-pass / single-pass stereo), compositor submission via OpenGL texture handles, prediction timing, and frame statistics. |
| [Rendering Code Map](code-map.md) | Source file organization for mesh rendering, meshlet rendering, GPU compute stages, and shared infrastructure. |

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

## Shared Post-Processing Defaults

Camera post-processing stages are authored through the pipeline schema, but some fallback paths instantiate settings classes directly. Auto-exposure constructor defaults are intentionally kept aligned with the schema defaults so standalone and fallback color-grading paths use the same baseline behavior: log-average metering, `ExposureDividend = 0.1`, and a `0.0001..100.0` exposure clamp range.
