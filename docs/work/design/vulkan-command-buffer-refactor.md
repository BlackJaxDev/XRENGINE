# Vulkan Command Buffer Refactor: Parallel Recording from Collect-Visible Pass

## Goals
- Reduce main-thread work by recording draw calls during collect-visible (visibility) phase.
- Enable multithreaded command buffer (CB) recording with secondary CBs per worker.
- Keep render submission stable: primary CB per swapchain image executes secondary CBs.
- Minimize per-frame allocations: reuse per-frame command pools and descriptor resources.

## High-Level Approach
1. **Per-frame resources**: For each frame-in-flight, maintain:
   - Primary command buffer (already exists) per swapchain image.
   - A set of thread-local command pools + secondary CB arrays.
2. **Secondary CB recording** (collect-visible pass):
   - Partition visible draw calls into jobs (e.g., by render pass/subpass and pipeline/material bucket).
   - Each worker thread pulls jobs, begins a secondary CB with `VK_COMMAND_BUFFER_USAGE_RENDER_PASS_CONTINUE_BIT` and inheritance info (render pass, subpass, framebuffer, viewport/scissor).
   - Record binds and draws for its batch, end CB, publish handle.
3. **Primary CB assembly** (render phase per swapchain image):
   - Set dynamic state (viewport/scissor), begin render pass.
   - Execute secondary CBs in desired order (e.g., depth prepass first, main pass next, UI last).
   - End render pass.
4. **Descriptor strategy**:
   - Prefer per-frame descriptor pools; allocate descriptor sets before recording jobs.
   - Use dynamic offsets or per-frame uniform buffers for engine uniforms; avoid per-draw updates in primary.
5. **Pipeline strategy**:
   - Pre-create / cache pipelines by topology and material state; do not build pipelines during job recording.
   - Keep dynamic state minimal: viewport/scissor (already dynamic).
6. **Synchronization**:
   - Worker threads only own their command pools/secondary CBs; no cross-thread pool use.
   - Primary thread handles submit/fences/semaphores; workers signal completion via lightweight latches/queues.
7. **Fallback**: Keep existing sequential path available behind a feature flag until stabilized.

## Data Structures to Add
- `struct FrameRecordingContext` per frame-in-flight:
  - `CommandBuffer Primary;`
  - `List<ThreadRecordingContext> Threads;`
- `struct ThreadRecordingContext` per worker:
  - `CommandPool Pool;`
  - `List<CommandBuffer> SecondaryPerPass;`
- Job queue item (produced during collect-visible):
  - Render pass/subpass id, framebuffer handle, pipeline/material key, draw list slice, pointer to descriptor sets, inherited viewport/scissor.

## Flow Changes
1. **Frame start**: Reset per-frame command pools for all threads; clear secondary CB lists.
2. **Collect-visible phase**: Build job queue buckets; enqueue to worker threads.
3. **Worker record**: For each job, begin secondary CB with inheritance, bind pipeline/descriptors/buffers, record draws, end CB.
4. **Primary record**: For the current swapchain image, begin render pass, execute collected secondary CBs in pass order, end render pass.
5. **Submit**: Unchanged (wait fence, submit primary, present).

## Multithreading Guidelines
- One command pool per thread per frame-in-flight; reset at frame start.
- Avoid allocating in hot paths: pre-size secondary CB arrays; reuse descriptor sets when possible.
- Keep job granularity coarse enough to amortize CB overhead (e.g., per material bucket, not per draw).

## Incremental Rollout Plan
1. **Scaffolding**: Add per-frame/thread command pools and secondary CB support; gate via `UseSecondaryRecording` flag.
2. **Single-pass prototype**: Main color pass only, triangles; verify correctness.
3. **Extend coverage**: Add depth prepass, lines/points, UI pass secondary CBs.
4. **Performance validation**: Measure CPU frame time and CB submission overhead; tune job sizing.
5. **Cleanup**: Remove legacy path or keep as fallback; document toggles.

## Risks / Mitigations
- **Pipeline/descriptor availability**: Ensure all pipelines/descriptors are ready before job recording; fail-fast logging.
- **Framebuffer inheritance**: Secondary CBs must target the correct framebuffer per swapchain image; ensure inheritance info is set.
- **Ordering-sensitive passes**: Maintain deterministic pass ordering when executing secondary CBs.
- **Line width / dynamic state**: If dynamic line width is needed, enable feature and set via dynamic state; otherwise fixed to 1.0.

## Acceptance Criteria
- Primary thread does no per-draw recording in the main pass when feature is enabled.
- No regressions in visual output for triangles, lines, points.
- Measurable CPU time reduction in scenes with many draws.
- Build passes and feature flag can be toggled at runtime or startup.

---

# Sequential Vulkan Path: Remaining Work Checklist
- **Indirect draws**: `MultiDrawElementsIndirect*` and parameter buffer hooks are TODO.
- **Descriptor uniform path**: Engine/material uniforms rely on descriptor updates per frame; ensure no per-draw CPU stalls (may need consolidation).
- **Screenshot/depth readback**: `GetScreenshotAsync`, `GetDepth*`, `CalcDotLuminance*` are not implemented.
- **Render queue integration**: Command buffer recording currently happens late (`EnsureCommandBufferRecorded`) with placeholder pass; integrate actual render queue/passes.
- **Validation of line/point rendering**: Newly added line/point support needs runtime validation for topology and stats; wide line feature not enabled.
- **Pipeline cache lifecycle**: Confirm pipeline cache invalidation on material/mesh changes is sufficient when multiple topologies are used.
- **Resource barriers**: Barrier planner covers swapchain; verify other attachments/textures for correctness in multi-pass scenarios.
- **Fallback path**: Keep current sequential path functioning while introducing secondary CB feature flag.
