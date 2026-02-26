# Vulkan Work Report (Consolidated)

Last updated: 2026-02-26

This file is the canonical **report/history** document for Vulkan work. Active tasks are tracked in `vulkan-todo.md`.

## What was merged

This report consolidates and replaces historical status from:

- `vulkan-render-loop-improvements.md`
- `vulkan-command-buffer-refactor.md`
- `vulkan-rendering-correctness-next-steps.md`
- `vulkan-parity-report.md`
- `vulkan-no-fragments-investigation.md`
- `vulkan-ui-batch-rendering-investigation.md`

Deep architectural guidance and audit detail remain in `modern-vulkan-render-pipeline-summary.md`.

## Implemented since earlier reports

The following items were marked as planned/incomplete in older docs but are now implemented in code:

- Per-frame Vulkan render loop is wired and active.
- Swapchain depth attachment path exists.
- Dynamic rendering is in active use for the swapchain main path.
- Secondary command buffer infrastructure and parallel bucket recording are present.
- Indirect draw paths are implemented, including indirect-count support when available.
- Readback APIs exist (`GetDepth*`, `GetScreenshotAsync`, `CalcDotLuminance*`).
- Shader compatibility fix for `UnlitTexturedForward.fs` output location is present.
- Bind-state reset logic exists for command buffer re-record paths.

These implemented items were removed from the active TODO backlog to keep planning current.

## Current state snapshot

### Strong/working areas

- Render-graph planning and barrier planning architecture is in place.
- Descriptor set tiering and feature-profile gating are in place.
- Pipeline cache persistence and runtime cache structure are in place.
- Transfer queue upload path and timeline-based frame synchronization are in place.
- GPU-driven rendering enablers are present.

### Open correctness/robustness concerns still observed

- Render-graph pass index fallback warnings are still emitted for some `MeshDrawOp` paths.
- Memory allocation remains predominantly per-resource `AllocateMemory` (no global suballocator backbone yet).
- Readback memory path still lacks `HostCached` usage.
- Synchronization2/Submit2 migration has not been started.
- Broad `AllCommandsBit` stage masks are still used in multiple Vulkan paths.
- Descriptor pool lifecycle still relies heavily on destroy/recreate patterns and `FreeDescriptorSetBit` in hot paths.

## Historical investigation notes (preserved from deleted files)

These findings are not active TODOs but are valuable diagnostic context if related issues recur.

### BindVertexBuffersTracked / BindPipelineTracked hash-collision risk

The tracked-bind deduplication in `CommandBuffers.cs` uses `System.HashCode` truncated to `ulong` via `unchecked((ulong)hash.ToHashCode())`, which only produces 32 bits of entropy cast to 64. Two different VBO binding sets or pipelines could hash-collide and silently skip a necessary bind, producing "no fragments" symptoms. If invisible-geometry issues recur, bypass tracked binds as a first diagnostic step.

### UI batch rendering remaining hypotheses

During the UI batch rendering investigation (2026-02-20), the render-graph pass metadata bug was identified as the highest-value issue and is tracked in the TODO. The following lower-priority hypotheses were also identified but not resolved:

1. Non-batched transparent shader compatibility in Vulkan profiles (e.g., video/web/viewport materials).
2. Render pass / FBO target mismatch during the UI batch pass.
3. Camera / projection uniform state during `VPRC_RenderUIBatched` (invalid values could move batched geometry off-screen).
4. Descriptor fallback path silently binding placeholder zeroed buffers for SSBOs.

### OpenGL batch rendering was also broken

During the UI batch investigation, OpenGL batch rendering bugs were discovered and fixed (the `gl_InstanceIndex` vs `gl_InstanceID` mismatch meant visible OpenGL UI was coming from CPU fallback, not the native batch path).

## Prior backlog completion

`docs/work/backlog/vulkan_integration_TODO.md` has all items checked off. It represents the original parity report and command-buffer refactor scope and is fully complete.

## Cleanup decisions

- Removed per-phase/per-investigation files that mixed old plans with already-implemented work.
- Separated ongoing work into a single actionable backlog file (`vulkan-todo.md`).
- Preserved deep Vulkan architecture and audit reference in `modern-vulkan-render-pipeline-summary.md`.
