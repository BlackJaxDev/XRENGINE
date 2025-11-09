# Rendering Architecture

XRENGINE renders each world through a staged pipeline that separates scene updates, visibility gathering, GPU command construction, and pass execution. The system is built around GPU-driven multi-draw indirect rendering but retains CPU fallbacks for debugging and platforms that do not support the compute path.

## Frame Ownership
- `XRWorldInstance` owns the live scene for a window. It wires engine timers (`Time.Timer`) so update ticks, visibility collection, buffer swaps, and render submission happen on deterministic hooks.
- Transform changes are queued from gameplay threads via `AddDirtyTransform`. During `PostUpdate` each depth bucket is recalculated and the resulting render matrices are transferred to the render thread during `GlobalSwapBuffers`.
- `GlobalPreRender` and `GlobalPostRender` wrap the frame on the render thread, giving the world a chance to prepare shared resources (shadow maps, probe captures) before and after draw submission.

## Scene Visibility Collection
- Every world owns a `VisualScene` (usually `VisualScene3D`) that tracks renderable proxies (`RenderInfo3D`). It exposes both an octree (`RenderTree`) for CPU collection and a GPU-friendly command buffer (`GPUCommands`).
- The engine toggles between the CPU tree and GPU-driven path via `Engine.UserSettings.GPURenderDispatch`. When disabled the octree performs frustum tests per renderable; when enabled the same data is mirrored into `GPUScene` buffers so the GPU can cull and sort.
- `CollectRenderedItems` is invoked per camera to filter objects against the view volume, assemble `RenderCommand` entries, and enqueue them into the `RenderCommandCollection`. Mirrors and other secondary views plug in through the same call.
- `GlobalSwapBuffers` double-buffers the render tree and command queues so update and render threads never race. `RenderCommandCollection.SwapBuffers` swaps the CPU/GPU command sets and clears the update-side containers for the next frame.

## GPUScene and Indirect Rendering
- `GPUScene` owns the authoritative GPU command buffers. It maintains a mesh atlas (positions, normals, tangents, UVs, indices) and a `MeshDataBuffer` that describes where each mesh resides inside the atlas. New meshes are appended lazily and flagged as “dirty” so rebuilds happen just before rendering.
- Materials are assigned unique IDs and mirrored in a concurrent lookup (`MaterialMap`) so compute shaders can batch by material without touching CPU state mid-frame.
- When a render pass executes, `GPURenderPassCollection`:
  - Resets visible/indirect counters via compute shaders.
  - Dispatches a GPU frustum cull (and optional distance sort) against the `GPUScene` command buffer.
  - Builds the indirect draw buffer (`DrawElementsIndirectCommand` records) per pass and material.
  - Hands the prepared buffers to `HybridRenderingManager`, which issues `glMultiDrawElementsIndirect` calls or falls back to CPU submission when debug flags request it.
- Extensive diagnostics (`IndirectDebugSettings`) allow forcing CPU rebuilds, reading back counts, or dumping command samples without modifying runtime code.

## Render Pipelines and Passes
- A render pipeline is expressed by a `RenderPipeline` subclass. It defines a `ViewportRenderCommandContainer` chain that pushes commands such as visibility collection, shadow pass preparation, main pass rendering, post processing, and UI composition.
- `RenderPipeline.PassIndicesAndSorters` maps integer pass IDs to custom `IComparer<RenderCommand>` instances. The engine uses these IDs to route commands into CPU and GPU buckets with predictable ordering (e.g., depth-prepass, opaque, transparent, UI).
- `XRRenderPipelineInstance` is the per-viewport execution context. It owns the active `RenderCommandCollection`, tracks per-frame textures/FBOs, and exposes a descriptive `DebugDescriptor` for GPU debugging overlays.
- During `Render`, the instance binds itself as the current pipeline (`Engine.Rendering.State.PushRenderingPipeline`), pushes the render state (camera, viewport, shadow/stereo flags), and calls `Pipeline.CommandChain.Execute()` to fire the configured passes.

## Resource Management
- Framebuffer and texture resources registered through `XRRenderPipelineInstance.SetFBO` / `SetTexture` are cached per pipeline instance. Resizing a viewport simply invalidates the cache so the next frame can rebuild dependent render targets.
- The render pipeline base class provides helpers (`NeedsRecreateTextureInternalSize`, `GetDesiredFBOSizeInternal`, etc.) that standardize how internal and display resolutions cascade through passes.
- `GPUScene.EnsureAtlasBuffers` grows SSBOs and VBOs on demand. Uploads occur via `PushSubData`, and the atlas never shrinks during runtime to avoid realloc churn.

## Stereo and VR Support
- Stereo cameras feed `XRRenderPipelineInstance.Render` with both left and right eye views. The render state marks stereo frames so pipelines can choose single-pass instanced, sequential, or custom VR rendering paths.
- `XRViewport` exposes per-eye render targets while `RenderState.ShadowPass` allows the same pipeline graph to execute VR shadow maps without duplicating logic.
- Editor and runtime VR preview windows share the same pipeline infrastructure; only the active command chain differs.

## Extending the System
- Implement a new `RenderPipeline` subclass to define alternative pass ordering or post-processing. Override `GenerateCommandChain` to register the nodes in your pipeline and `GetPassIndicesAndSorters` to map pass IDs to sorters.
- Custom renderables should subclass an appropriate `RenderInfo` type so they integrate with both the octree and `GPUScene` command emission. When GPU dispatch is enabled, make sure you provide mesh data compatible with the atlas builder.
- Advanced projects can add compute stages by extending `GPURenderPassCollection` (new partial class) or by creating bespoke `ViewportRenderCommand` nodes that run before or after the built-in GPU cull.
- Use the indirect debug flags during development to verify draw counts, detect overflow in the culling buffers, or compare GPU and CPU paths when investigating rendering glitches.

## Further Reading
- [Scene System](scene.md)
- [Physics System](physics.md)
- [Animation System](animation.md)
- [VR Development](vr-development.md)