# Vulkan Rendering Correctness Audit + Next Steps

_Last updated: 2026-01-13_

## Executive Summary
The Vulkan backend has the right *core mechanics* (instance/device/swapchain setup, per-frame fences+semaphores, primary command buffers, and a recording path that can encode mesh draws), but it has several *correctness blockers* that prevent it from reliably rendering engine content like the OpenGL backend.

### Highest-signal blockers
1. **Per-frame present path was not wired into the window render loop.**
   - Vulkan’s acquire/submit/present lives in `VulkanRenderer.WindowRenderCallback(...)`, but the window loop (`XRWindow.RenderCallback`) previously never invoked it.
   - Fix: call a public renderer hook once per frame (after viewports enqueue draws).

2. **Swapchain pass is color-only; depth attachment is currently stubbed.**
   - Even if `EnableDepthTest` / `DepthFunc` are tracked, the swapchain render pass/framebuffer has no depth target, so typical 3D scenes won’t render correctly.

3. **Render graph / framebuffer semantics are not yet compiled into Vulkan render passes.**
   - OpenGL can “just bind FBO + draw”; Vulkan needs explicit render passes and attachment layout transitions.
   - You already have the scaffolding (`VulkanResourcePlanner`/`VulkanResourceAllocator`/`VulkanBarrierPlanner`), but the engine still needs a compiler/executor step that turns pass metadata into real Vulkan render passes/subpasses and commands.

## What OpenGL Is Doing Differently (and why it appears to “just work”)
- **OpenGL**:
  - Immediate mode: engine code calls state/draw APIs and GL executes immediately.
  - Presentation is implicit: the windowing system swaps buffers around the render callback.
  - Therefore `OpenGLRenderer.WindowRenderCallback(...)` being empty is not necessarily a bug: the actual work happens during viewport/pipeline rendering, and swapping happens outside the renderer.

- **Vulkan**:
  - Requires explicit **AcquireNextImage → Submit → Present** every frame.
  - Requires explicit render pass/framebuffer/lifetime/layout transitions.
  - Therefore Vulkan *must* have a per-frame “end of window render” hook, even if all draw calls were already queued/recorded elsewhere.

## Comparison to SilkVulkanTutorial (dfkeenan)
Your Vulkan loop matches the tutorial’s structure conceptually:
- wait for in-flight fence
- acquire swapchain image
- submit command buffer with wait semaphore stage `ColorAttachmentOutput`
- present, recreate swapchain on out-of-date/suboptimal

Key differences that matter for engine parity:
- Tutorial records a known-good pipeline (single pass, known vertex format) and updates UBOs per image.
- XRENGINE’s Vulkan path must support a *graph* of passes, a *variety* of material pipelines, and resource hazards across passes.
- The tutorial’s “record per swapchain framebuffer” model works because there’s only one pass; XRENGINE needs a compilation step to map its pass metadata into Vulkan render passes/subpasses/barriers.

## Current Vulkan Recording Path (as implemented)
- Viewport/pipeline rendering enqueues draws via `VkMeshRenderer`’s `OnRenderRequested(...) → Renderer.QueueMeshDraw(...)`.
- Command buffer recording (`RecordCommandBuffer`) does:
  1. pending memory barriers
  2. planned image barriers (planner)
  3. begin swapchain render pass
  4. apply viewport/scissor
  5. `RenderQueuedMeshes(...)`
  6. ImGui hook (currently disabled)
  7. end render pass

This can render *something* once the per-frame submit/present is invoked, but it will not match OpenGL for typical scenes until depth + full pass graph are supported.

## Recommended Next Steps (Incremental Plan)

### Phase 0 — “Show Something On Screen” (correctness, minimal scope)
Goal: reliably render a basic scene (triangles) with correct presentation.

1. **Guarantee per-frame submit/present is invoked**
   - Ensure window loop calls the renderer’s per-window frame function once per frame.
   - Add debug counters/logging for: acquired image index, submitted frame index, presented image index.

2. **Add a swapchain depth attachment path**
   - Create a depth image per swapchain extent.
   - Update swapchain render pass to include depth attachment.
   - Update framebuffer creation to include depth view.
   - Track/clear depth correctly.

3. **Validation layer correctness gates**
   - Run with validation layers enabled by default in Debug.
   - Add `VK_EXT_debug_utils` markers around: begin frame, begin pass, mesh batch, end pass.

Acceptance criteria:
- With validation layers on, no errors during a simple forward pass.
- Depth-tested scene renders deterministically.

### Phase 1 — Render Graph Compilation (parity foundation)
Goal: make Vulkan execute the same pipeline structure as OpenGL.

1. **Introduce a Vulkan “graph compilation” step**
   - Input: render pipeline pass metadata (already exists).
   - Output: a linear execution plan:
     - render pass/subpass groupings
     - attachment descriptions (format/sample/load/store)
     - per-edge barriers (or subpass deps)

2. **BindFrameBuffer semantics**
   - Stop treating FBO binds as “just dirty command buffers”.
   - Instead, make FBO binds define the current planned pass context.

Acceptance criteria:
- A multi-pass pipeline (e.g., depth prepass + color) produces correct results.

### Phase 2 — Descriptors/Uniforms (materials parity)
Goal: material rendering parity with OpenGL.

1. **Define stable descriptor set schemas**
   - Set 0: engine globals (camera, time, exposure, etc.)
   - Set 1: material resources (textures + material constants)
   - Optional: set N: per-draw dynamic resources

2. **Descriptor pool & caching strategy**
   - Per-frame pools with recycling.
   - Cache descriptor sets by (layout, resource bindings) key.

3. **Uniform update cadence**
   - Per swapchain image: update global UBO/SSBO once.
   - Avoid per-draw `vkUpdateDescriptorSets` when possible.

Acceptance criteria:
- Common materials render correctly (albedo/normal/roughness) with stable performance.

### Phase 3 — Performance + Multithreaded Recording (your `vulkan-command-buffer-refactor.md`)
Goal: enable parallel recording safely once correctness is proven.

1. Add per-frame per-thread command pools.
2. Record secondary command buffers during collect-visible.
3. Execute secondary buffers from primary per swapchain image.

Acceptance criteria:
- Visual output matches sequential path.
- Reduced CPU frame time in draw-heavy scenes.

## Known Gaps / Non-Goals (for now)
- Vulkan ImGui is currently disabled; parity should be tackled after the main render graph is stable.
- Readback tools (picking/screenshot/depth reads) should follow once attachment lifetime + layout transitions are fully correct.

## Suggested Debug/Verification Checklist
- Capture a frame in RenderDoc and verify:
  - render pass has expected attachments and layouts
  - depth attachment exists and is written
  - pipelines are compatible with render pass
  - descriptor sets are bound for expected draws
- Add a “solid triangle” debug mode that bypasses most of the engine pipeline and renders one known mesh/material via Vulkan.
