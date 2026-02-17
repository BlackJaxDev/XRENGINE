# OpenGL Renderer

This document describes how the OpenGL 4.6 renderer is initialized, how it manages GPU state, and how the per-frame render loop works.

## Table of Contents

- [Overview](#overview)
- [Source File Inventory](#source-file-inventory)
- [Initialization](#initialization)
  - [GetAPI() and InitGL()](#getapi-and-initgl)
  - [Extension Probing](#extension-probing)
  - [Debug Callback Setup](#debug-callback-setup)
- [The Render Loop](#the-render-loop)
  - [Frame Flow](#frame-flow)
  - [Viewport Rendering](#viewport-rendering)
  - [Draw Call Submission](#draw-call-submission)
  - [Indirect Drawing](#indirect-drawing)
- [Render Object Factory](#render-object-factory)
- [Framebuffer Management](#framebuffer-management)
- [State Management](#state-management)
- [Async Uploads](#async-uploads)
- [ImGui Integration](#imgui-integration)
- [Shader Management](#shader-management)

---

## Overview

`OpenGLRenderer` is a partial class that extends `AbstractRenderer<GL>` (where `GL` is Silk.NET's OpenGL 4.6 binding). It obtains an OpenGL context from the Silk.NET window's `GLContext` and wraps all engine render objects with GL-specific API wrappers.

```csharp
// XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs
public partial class OpenGLRenderer : AbstractRenderer<GL>
```

A key design decision: **OpenGL's `WindowRenderCallback` is empty**. Unlike Vulkan, which requires explicit acquire/submit/present, Silk.NET handles `SwapBuffers` automatically for OpenGL after the render event returns. All actual rendering happens through the viewport pipeline system invoked by `XRWindow.RenderCallback`.

---

## Source File Inventory

The OpenGL renderer spans approximately 52 files under `XRENGINE/Rendering/API/Rendering/OpenGL/`:

| Category | Key Files |
|----------|-----------|
| **Core** | `OpenGLRenderer.cs` (~3800 lines), `OpenGLRenderer.DebugTracking.cs` |
| **Buffers** | `GLDataBuffer.cs`, `GLDataBufferView.cs`, `GLUploadQueue.cs` |
| **Render Targets** | `GLFrameBuffer.cs`, `GLRenderBuffer.cs` |
| **Textures** | `GLTexture1D/2D/3D.cs`, `GLTextureCube.cs`, `GLTexture2DArray.cs`, `GLTextureBuffer.cs`, `GLTextureRectangle.cs`, `GLTextureView.cs`, `GLSampler.cs` |
| **Mesh Rendering** | `GLMeshRenderer.cs`, `.Buffers.cs`, `.Debug.cs`, `.Lifecycle.cs`, `.Rendering.cs`, `.Shaders.cs` |
| **Materials/Shaders** | `GLMaterial.cs`, `GLShader.cs` |
| **Programs** | `GLRenderProgram.cs`, `GLRenderProgramPipeline.cs` |
| **Base Types** | `GLObject.cs`, `GLObjectBase.cs`, `IGLObject.cs` |
| **Queries** | `GLRenderQuery.cs`, `GLTransformFeedback.cs` |
| **Enums** | 10+ enum translation files (wrap modes, sampler parameters, filters, etc.) |

---

## Initialization

### GetAPI() and InitGL()

All OpenGL initialization happens in the constructor, triggered by the lazy `Api` property. When `OpenGLRenderer` is constructed, the base class accesses `Api` which calls `GetAPI()`:

```csharp
protected override GL GetAPI()
{
    var api = GL.GetApi(Window.GLContext);
    InitGL(api);
    return api;
}
```

`GL.GetApi(Window.GLContext)` obtains the OpenGL 4.6 function pointers from the Silk.NET window's GL context. Then `InitGL()` performs all one-time setup:

```csharp
private static void InitGL(GL api)
{
    // 1. Query and log GPU information
    string version = glGetString(GL_VERSION);
    string vendor  = glGetString(GL_VENDOR);      // "NVIDIA", "Intel", "AMD", etc.
    string renderer = glGetString(GL_RENDERER);
    string glsl    = glGetString(GL_SHADING_LANGUAGE_VERSION);

    // 2. Set engine-wide GPU capability flags
    Engine.Rendering.State.IsNVIDIA = vendor.Contains("NVIDIA");
    Engine.Rendering.State.IsIntel  = vendor.Contains("Intel");
    Engine.Rendering.State.IsVulkan = false;

    // 3. Enumerate all extensions
    int extCount = glGetInteger(GL_NUM_EXTENSIONS);
    for (uint i = 0; i < extCount; i++)
        extensions[i] = glGetString(GL_EXTENSIONS, i);
    Engine.Rendering.State.OpenGLExtensions = extensions;

    // 4. Load binary shader cache (from previous runs)
    GLRenderProgram.ReadBinaryShaderCache(version);

    // 5. Set default GL state
    api.Enable(EnableCap.Multisample);
    api.Enable(EnableCap.TextureCubeMapSeamless);
    api.FrontFace(FrontFaceDirection.Ccw);
    api.Disable(EnableCap.Dither);
    api.ClipControl(LowerLeft, NegativeOneToOne);
    api.PixelStore(PackAlignment, 1);
    api.PixelStore(UnpackAlignment, 1);
    api.PointSize(1.0f);
    api.LineWidth(1.0f);
    api.UseProgram(0);

    // 6. Setup debug output
    SetupDebug(api);
}
```

The constructor also probes for an OpenGL ES API handle and checks for numerous extensions:
- `OVR_multiview` — multi-view rendering for VR
- `NV_MeshShader` — mesh shading support
- `NV_GpuShader5` — advanced shader intrinsics
- `NV_ViewportArray` — multiple viewports
- `EXT_MemoryObject` / `EXT_Semaphore` / `EXT_Win32` — Vulkan interop
- `NV_BindlessMultiDrawIndirectCount` — bindless indirect drawing
- `ARB_MultiDrawIndirect` — multi-draw indirect
- `NV_PathRendering` — GPU-accelerated path rendering

### Extension Probing

Extensions are queried once and cached in `Engine.Rendering.State.OpenGLExtensions`. Individual extension booleans are stored on the renderer instance for fast runtime checks:

```csharp
HasOvrMultiviewExt     // GL_OVR_multiview
HasMeshShaderExt       // GL_NV_mesh_shader
HasGpuShader5Ext       // GL_NV_gpu_shader5
HasViewportArrayExt    // GL_NV_viewport_array
// ... etc.
```

### Debug Callback Setup

`SetupDebug()` enables the OpenGL debug output extension (`GL_KHR_debug` or `GL_ARB_debug_output`):

```csharp
private static void SetupDebug(GL api)
{
    // Check for debug extension support
    bool supportsDebugOutput = extensions.Any(e =>
        e == "GL_KHR_debug" || e == "GL_ARB_debug_output");

    if (!supportsDebugOutput) return;

    api.Enable(EnableCap.DebugOutput);
    api.Enable(EnableCap.DebugOutputSynchronous);
    api.DebugMessageCallback(DebugCallback, null);

    // Filter out noisy driver messages
    foreach (var id in _ignoredMessageIds)
        api.DebugMessageControl(source, type, severity, 1, &id, false);
}
```

The debug callback categorizes messages by severity and source, filtering out known noisy messages (buffer memory info, mipmap generation notices, etc.) to keep log output actionable.

### Note on `Initialize()`

The `Initialize()` override is **empty**:

```csharp
public override void Initialize() { }
```

This is because all OpenGL setup is done eagerly in `GetAPI()` / `InitGL()` during construction. By the time `BeginTick()` calls `Initialize()`, the GL context is already fully configured.

---

## The Render Loop

### Frame Flow

OpenGL rendering follows this per-frame flow:

```
Engine Timer fires RenderFrame
  └─ XRWindow.RenderFrame()
       ├─ Window.DoEvents()                    // Process OS window events (input, resize, etc.)
       └─ Window.DoRender()                    // Silk.NET fires the Render event
            └─ XRWindow.RenderCallback(delta)
                 ├─ Stats.BeginFrame()          // Reset per-frame counters
                 ├─ Renderer.ProcessPendingUploads()
                 │    ├─ UploadQueue.ProcessUploads()
                 │    └─ MeshGenerationQueue.ProcessGeneration()
                 ├─ WorldInstance.GlobalPreRender()
                 ├─ RenderViewportsCallback()   // External hooks (ImGui NewFrame, etc.)
                 ├─ RenderWindowViewports()     // Main rendering work
                 │    └─ for each viewport:
                 │         viewport.Render()     // Execute render pipeline passes
                 ├─ WorldInstance.GlobalPostRender()
                 ├─ Renderer.RenderWindow(delta) // → WindowRenderCallback() → NO-OP
                 └─ PostRenderViewportsCallback()
            [Silk.NET automatically calls SwapBuffers after Render event returns]
```

Because `WindowRenderCallback()` is empty for OpenGL, the frame is complete once the render event handler returns. Silk.NET's window implementation calls `SwapBuffers` (or the equivalent `eglSwapBuffers` / `glfwSwapBuffers`) automatically.

### Viewport Rendering

Each viewport executes the render pipeline configured for the current world:

```csharp
public void RenderViewports()
{
    foreach (var viewport in Viewports)
        viewport.Render();
}
```

A viewport's `Render()` method:
1. Sets the GL viewport rectangle via `SetRenderArea()`
2. Iterates through the render pipeline passes (shadow maps, G-buffer, lighting, post-processing, etc.)
3. Each pass binds its framebuffer, sets state, and draws the relevant meshes

### Draw Call Submission

Individual meshes are drawn through `GLMeshRenderer`:

```
GLMeshRenderer.Render()
  ├─ Select active material / shader program
  ├─ Bind vertex array object (VAO)
  ├─ Bind shader storage buffers (SSBOs) for skinning, etc.
  ├─ Set per-object uniforms (model matrix, material parameters)
  └─ RenderCurrentMesh(instances)
       ├─ DrawElementsInstanced(Triangles, triCount, elementType, null, instances)
       ├─ DrawElementsInstanced(Lines, lineCount, elementType, null, instances)
       └─ DrawElementsInstanced(Points, pointCount, elementType, null, instances)
```

The draw calls use `glDrawElementsInstanced` for standard rendering, supporting triangles, lines, and points in a single mesh.

### Indirect Drawing

For GPU-driven rendering, the OpenGL renderer supports multiple indirect draw paths with automatic fallback:

| Extension | Function | Description |
|-----------|----------|-------------|
| `NV_BindlessMultiDrawIndirectCount` | `NVBindlessMultiDrawIndirectCount` | Fastest path — bindless, count from buffer |
| `ARB_MultiDrawIndirect` | `MultiDrawElementsIndirect` | Standard multi-draw indirect |
| `GL_ARB_draw_indirect` | `MultiDrawElementsIndirectCount` | Count-from-buffer variant |
| *(fallback)* | `DrawElementsInstanced` | Per-object draw calls |

The renderer probes for these extensions at startup and selects the best available path.

---

## Render Object Factory

`CreateAPIRenderObject()` maps engine-generic render objects to OpenGL-specific wrappers:

| Generic (Engine) | OpenGL Wrapper |
|-------------------|----------------|
| `XRMaterial` | `GLMaterial` |
| `XRShader` | `GLShader` |
| `XRMeshRenderer.BaseVersion` | `GLMeshRenderer` |
| `XRRenderProgram` | `GLRenderProgram` |
| `XRDataBuffer` | `GLDataBuffer` |
| `XRFrameBuffer` | `GLFrameBuffer` |
| `XRTexture2D` | `GLTexture2D` |
| `XRTexture3D` | `GLTexture3D` |
| `XRTextureCube` | `GLTextureCube` |
| `XRTexture2DArray` | `GLTexture2DArray` |
| `XRTextureBuffer` | `GLTextureBuffer` |
| `XRRenderBuffer` | `GLRenderBuffer` |
| `XRRenderQuery` | `GLRenderQuery` |
| `XRTransformFeedback` | `GLTransformFeedback` |
| `XRSampler` | `GLSampler` |
| `XRTextureView` | `GLTextureView` |

These wrappers are lazily created on first access and cached in a `ConcurrentDictionary` on the base `AbstractRenderer`. The `GenericToAPI<T>()` helper retrieves the cached API object for a given generic object.

---

## Framebuffer Management

`GLFrameBuffer` wraps OpenGL framebuffer objects (FBOs):

```csharp
// GLFrameBuffer.cs
class GLFrameBuffer : GLObject<XRFrameBuffer>
{
    void Bind(EFramebufferTarget target)    // glBindFramebuffer (Read, Draw, or Both)
    void VerifyAttached()                    // Lazily verify color/depth/stencil attachments
    void CheckStatus()                       // glCheckFramebufferStatus
}
```

Key design:
- Attachment changes are **deferred** — they are queued and applied lazily in `VerifyAttached()` before any draw call that uses the FBO
- The renderer provides `Blit()` for framebuffer-to-framebuffer copies using `glBlitNamedFramebuffer`
- The default framebuffer (backbuffer) is represented by binding FBO 0

---

## State Management

The renderer provides thin wrappers around OpenGL state calls:

| Method | GL Call |
|--------|---------|
| `SetRenderArea(rect)` | `glViewport(x, y, w, h)` |
| `CropRenderArea(rect)` | `glScissor(x, y, w, h)` |
| `SetCroppingEnabled(bool)` | `glEnable/Disable(GL_SCISSOR_TEST)` |
| `EnableDepthTest(bool)` | `glEnable/Disable(GL_DEPTH_TEST)` |
| `DepthFunc(comparison)` | `glDepthFunc(func)` |
| `AllowDepthWrite(bool)` | `glDepthMask(flag)` |
| `StencilMask(uint)` | `glStencilMask(mask)` |
| `Clear(color, depth, stencil)` | `glClear(mask)` |
| `ClearColor(r, g, b, a)` | `glClearColor(r, g, b, a)` |
| `ColorMask(r, g, b, a)` | `glColorMask(r, g, b, a)` |
| `MemoryBarrier(mask)` | `glMemoryBarrier(mask)` |
| `WaitForGpu()` | `glFinish()` |

These are called by the render pipeline passes to configure GL state before draw calls.

---

## Async Uploads

To avoid stalling the render thread, buffer and mesh data uploads are spread across frames:

```csharp
public override void ProcessPendingUploads()
{
    _frameCounter++;
    UploadQueue.ProcessUploads();            // GLUploadQueue — buffers, textures
    MeshGenerationQueue.ProcessGeneration(); // GLMeshGenerationQueue — procedural meshes
}
```

This runs at the start of each frame (before any rendering) with a time budget to prevent frame drops. Uploads that exceed the budget are deferred to subsequent frames.

The upload queue handles:
- Texture data uploads (`glTexSubImage2D`, etc.)
- Buffer data uploads (`glBufferSubData`, `glMapBuffer`)
- Procedural mesh generation (CPU-side mesh building, then GPU upload)

---

## ImGui Integration

OpenGL uses the `ImGuiController` from Silk.NET's ImGui extension:

```csharp
private ImGuiController? GetImGuiController()
{
    // Creates controller on first use with the GL context,
    // window handle, and input context.
    // Registers with ImGuiContextTracker for multi-window support.
}
```

The `OpenGLImGuiBackend` implements `IImGuiRendererBackend`:
- `MakeCurrent()` — Sets the ImGui context
- `Update(deltaSeconds)` — Calls `ImGuiController.Update(delta)` to process input
- `Render()` — Calls `ImGuiController.Render()` which issues GL draw calls for the ImGui draw data

ImGui rendering happens within the viewport render callback, managed by the base `AbstractRenderer.TryRenderImGui()` method which handles context switching and thread safety with a lock.

---

## Shader Management

`GLRenderProgram` handles GLSL shader compilation and linking:

- **Binary shader cache**: On first compilation, shader binaries are saved to disk. On subsequent runs, `ReadBinaryShaderCache(version)` loads pre-compiled binaries, skipping recompilation if the GL version matches.
- **Hot reload**: Shaders can be recompiled at runtime when source files change.
- **Uniform management**: Uniforms are queried via `glGetUniformLocation` and cached. The program tracks active uniforms, uniform blocks, and SSBOs.

---

## See Also

- [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) — How windows and renderers are created at startup
- [Vulkan Renderer](vulkan-renderer.md) — Vulkan-specific initialization and render loop
- [Rendering Code Map](RenderingCodeMap.md) — Full source file inventory
