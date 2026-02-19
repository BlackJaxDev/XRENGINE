# Vulkan "No Fragments" Investigation — Current Suspicions

**Date:** 2026-02-18  
**Status:** Active investigation  
**Symptom:** `CmdDrawIndexed(3)` and `CmdDraw(3,1,0,0)` produce zero visible fragments for the skybox fullscreen triangle, despite everything appearing correct.

## What Has Been Proven Working

- **Render pass is active:** Injecting `CmdClearAttachments(green)` immediately after the draw call produces visible green (R=0 G=255 B=0). The swapchain attachment is writable and the command buffer is submitted correctly.
- **VBO data is correct:** Hex dump shows `(-1,-1,0), (3,-1,0), (-1,3,0)` — a standard fullscreen triangle.
- **Pipeline stages are correct:** Only `VertexBit + FragmentBit`, no geometry shader on the skybox pipeline.
- **Pipeline state looks correct:** `CullModeNone`, `DepthTest=Always`, `DepthWrite=False`, `ColorWrite=RGBA`, `RasterizerDiscardEnable=False`.
- **Viewport/scissor:** `(0, 1080, 1920, -1080)` / `(0, 0, 1920, 1080)` — negative height is the Y-flip convention.
- **GLSL source is correct:** Dumped the rewritten GLSL after auto-uniform processing. VS: `gl_Position = vec4(Position.xy, 0.5, 1.0)`. FS: `OutColor = vec4(0.0, 1.0, 0.0, 1.0)`. Both are exactly what we expect.
- **SPIR-V compiled successfully:** VS = 696 bytes, FS = 304 bytes, shader modules created with valid handles.
- **No Vulkan validation errors** on the skybox pipeline (only the known GS topology mismatch from `InstancedDebugVisualizer`, which is a separate issue).
- **Pipeline cache cleared** — stale cache ruled out.
- **Depth value z=0.5 tested** — changed from z=1.0 to rule out depth clipping at the far plane. Still no fragments.

## Remaining Suspicions (Ranked by Likelihood)

### 1. **BindVertexBuffersTracked deduplication bug** (HIGH)
The `BindVertexBuffersTracked` method uses a hash-based signature to skip redundant `CmdBindVertexBuffers` calls. If the signature from a *previous* draw (e.g., the failed InstancedDebugVisualizer draw) happens to collide with the skybox draw's signature, the actual `CmdBindVertexBuffers` call would be skipped — meaning the skybox draw would execute with the *wrong* VBO bound (or none at all). The hex dump reads CPU-side data, not what the GPU actually sees after the bind-tracking logic.

### 2. **BindPipelineTracked deduplication bug** (HIGH)
Same concern as above but for `BindPipelineTracked`. If the pipeline bind is incorrectly deduplicated, the draw could execute against a completely wrong pipeline (e.g., one with `RasterizerDiscardEnable` or incompatible vertex input state).

### 3. **Command buffer bind state not reset between frames** (MEDIUM-HIGH)
The `_commandBindStates` dictionary tracks what's currently bound per command buffer. If it's not cleared between frames (or between command buffer re-recordings), stale entries could cause the tracker to believe buffers/pipelines are already bound when they aren't — the command buffer itself was reset but the tracking dictionary still holds old state.

### 4. **Descriptor set binding order / layout mismatch** (MEDIUM)
The skybox FS has an auto-uniform block at `set=0, binding=64`. If `EnsureDescriptorSets` allocates or writes descriptor sets incorrectly (wrong pool, wrong layout), binding them could cause the pipeline to silently malfunction. However, the VS has *no* descriptors, which means a descriptor issue shouldn't prevent vertex processing.

### 5. **Vertex input state mismatch with actual VBO** (MEDIUM)
`BuildVertexInputState()` constructs `_vertexBindings` and `_vertexAttributes` based on the mesh's buffer layout. If there's a mismatch between the binding index used in `BuildVertexInputState` vs what `BindVertexBuffersForCurrentPipeline` actually binds, the vertex shader would read garbage or zeros, potentially placing all vertices at the origin (which would still produce fragments, though).

### 6. **ImGui's CmdBeginRendering/CmdEndRendering interferes** (LOW-MEDIUM)
ImGui renders after scene content using its own hardcoded pipeline. If ImGui's render pass accidentally overlaps or resets the swapchain attachment state, it could overwrite the scene's output. However, the `CmdClearAttachments` green test was visible even with ImGui running, so this seems unlikely.

### 7. **Swapchain image layout transition issue** (LOW)
The `BeginRenderPassForTarget(null)` transitions the swapchain image from `PresentSrcKhr → ColorAttachmentOptimal`. If this transition is incorrect or if there's a missing barrier, the draw could write to an image in the wrong layout. But `CmdClearAttachments` works in the same render pass, so the layout should be fine.

## Recommended Next Steps

1. **Bypass `BindVertexBuffersTracked` and `BindPipelineTracked`** for swapchain draws — call `CmdBindVertexBuffers` and `CmdBindPipeline` directly to rule out the deduplication logic.
2. **Log bind state resets** — verify `_commandBindStates` is cleared when command buffers are reset/re-recorded.
3. **Use RenderDoc or Nsight** to capture a frame and inspect the actual GPU-side draw call state (bound VBOs, pipeline, descriptors).
4. **Try a hardcoded SPIR-V test** — replace the skybox shader modules with pre-compiled SPIR-V (like ImGui uses) to draw a simple colored triangle, bypassing the entire GLSL→SPIR-V pipeline.
