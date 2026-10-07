# GPU-Driven Occlusion Culling TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [GPU Hi-Z Occlusion Culling](../../../../architecture/rendering/gpu-hiz-occlusion-culling.md)  Design: [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)  
Validation: [Render Queries And Occlusion Validation](../../../testing/rendering/render-queries-and-occlusion-validation.md), [Math Intersections Occlusion Validation](../../../testing/rendering/math-intersections-occlusion-tests.md)

## Current State

`GPURenderPassCollection.TwoPassOcclusion.cs`, `GPURenderOcclusionPhaseOne.comp`, and `GPURenderOcclusionHiZ.comp` implement a current-depth two-pass Hi-Z flow for `GpuHiZ` with `GpuIndirectZeroReadback`. The code has early and late visibility streams, two-phase telemetry, diagnostic buffer descriptors, and a command-wide visibility history buffer. The buffer is not per view, GPU-side remap for command compaction is not implemented, BVH traversal is still frustum-only, meshlet late recovery is incomplete, and `XRE_GPU_HIZ_DIRTY_BYPASS` still exists for the legacy single-pass path.

## Open Code Items

### Visibility State

- [ ] Implement a persistent per-view visibility buffer. `GPURenderPassCollection`, `GPUScene`, and `GPURenderOcclusionPhaseOne.comp`. Done when each view or eye has independent visibility words and shared stereo ORs them before emit.
- [ ] Add GPU-side visibility remap for command add, remove, and compaction. `GPUScene` mutation hooks and a remap compute shader. Done when scene mutation does not reset all visibility and does not need CPU mirrors.
- [ ] Replace CPU `_temporalOcclusion` use on zero-readback GPU paths with GPU-written age or confidence state. `GPURenderPassCollection.Occlusion.cs` and Hi-Z shaders. Done when zero-readback modes have no CPU temporal visibility dictionary dependency.
- [ ] Keep CPU temporal filters only for instrumented diagnostics that intentionally read back counts. `GPURenderPassCollection.Occlusion.cs`. Done when production zero-readback and diagnostic readback paths are clearly separated.
- [ ] Add GPU-written visibility occupancy, visible, occluded, and aged-count stats. Stats buffers, telemetry, and editor profiler data. Done when the profiler receives delayed stats without frame-loop readback.

### Two-Pass Hi-Z

- [ ] Remove or hard-gate `XRE_GPU_HIZ_DIRTY_BYPASS`. `RenderDiagnosticsFlags` and `GPURenderPassCollection.Occlusion.cs`. Done when camera jumps and scene mutations use the early visible seed and late recovery path instead of a dirty passthrough branch.
- [ ] Remove the current-depth self-cull bypass where two-pass depth ownership makes it safe. `TryPrepareGpuHiZTwoPass`, `ShouldBypassCurrentDepthGpuHiZRefine`, and pass policy code. Done when forward depth-normal passes no longer need a full refine bypass or have an explicit documented exception.
- [ ] Publish accurate late-phase draw counts through delayed diagnostics. Two-pass count readback and profiler transport. Done when phase-one and late counts are visible in diagnostics without production frame-loop readback.
- [ ] Keep two-pass dispatch, barrier, and indirect calls reusable across frame slots. `GPURenderPassCollection` and backend command recording. Done when GPU-written visibility, commands, and counts do not invalidate warmed primary command buffers.
- [ ] Complete OpenGL parity or hard-gate it to diagnostics. OpenGL compute path and mode routing. Done when OpenGL either runs the same supported flow or reports a clear unsupported reason.

### BVH Occlusion Traversal

- [ ] Add node-level Hi-Z tests to `bvh_frustum_cull.comp`. GPU BVH traversal shader and stats. Done when occluded interior nodes are rejected without visiting leaves.
- [ ] Add a deferred-node list for late recovery. BVH traversal resources and late Hi-Z pass. Done when nodes rejected by previous depth are retested against current depth in the late pass.
- [ ] Add per-node last-visible frame state. `GpuBvhTree` resources and remap rules. Done when node visibility follows rebuild, refit, and compaction safely.
- [ ] Add deterministic BVH occlusion parity tests. Unit tests and test shaders. Done when BVH plus occlusion emits the same conservative visible set as flat cull plus command Hi-Z, apart from allowed late timing.

### Meshlet Integration

- [ ] Feed the same per-view pyramid and visibility model into `MeshletCulling.task` and `MeshletCullingExt.task`. Meshlet culling shaders and pass setup. Done when meshlet culling uses the active per-eye depth source.
- [ ] Add meshlet late recovery. Meshlet task dispatch or append path. Done when meshlets rejected in the early pass can reappear in the late pass without CPU readback.
- [ ] Add phase-one and late meshlet stats. Stats buffers and profiler UI. Done when the profiler reports early and late meshlet counts.

### Stereo And VR

- [ ] Address visibility by `GPUViewDescriptor.ViewId`. Visibility buffer, shaders, and view-set setup. Done when left and right eyes never share a mono verdict incorrectly.
- [ ] Build or bind one pyramid per eye, or use a layered pyramid. Hi-Z build and sampling code. Done when the right eye is never tested against the left eye depth.
- [ ] Validate multiview offsets in early and late batches. `GPUViewSet` buffers and Vulkan multiview path. Done when multiview works without mono collapse.

### Mode Routing And Settings

- [ ] Make `GpuHiZ` mean the two-pass implementation on GPU dispatch paths. Mode routing, settings text, and editor UI. Done when no new enum value is required and the behavior is documented.
- [ ] Remove GPU-dispatch `CpuQueryAsync` scaffolding or gate it to `GpuIndirectInstrumented` diagnostics. `GPURenderPassCollection.Occlusion.cs`. Done when zero-readback Vulkan cannot select a readback-dependent query path.
- [ ] Keep CPU SOC scoped to CPU direct and instrumented diagnostics. Mode routing and warning text. Done when zero-readback SOC no-op remains explicit.
- [ ] Add editor rows for phase-one count, late count, node rejection count, and visibility-buffer occupancy. `EditorImGuiUI.OcclusionPanel` and profiler data. Done when users can see why GPU Hi-Z did or did not cull.
- [ ] Update related rendering architecture docs after implementation changes. `mesh-submission-strategies.md`, `default-render-pipeline-notes.md`, and related feature docs. Done when stable docs match code behavior.

### Unit Test Code

- [ ] Add visibility-buffer remap tests. `XREngine.UnitTests/Rendering`. Done when command compaction preserves visibility safely.
- [ ] Add phase-ordering source-contract tests. `XREngine.UnitTests/Rendering`. Done when early, pyramid, late, and indirect order is covered.
- [ ] Add mode-routing matrix tests for backend, profile, strategy, and mode. `XREngine.UnitTests/Rendering`. Done when unsupported combinations report explicit reasons.
- [ ] Add stereo OR-combine tests for GPU visibility. `XREngine.UnitTests/Rendering`. Done when either-eye visibility keeps the command visible.

## Decisions Needed

- [ ] Decide if late recovery retests deferred BVH nodes only or all early rejects flat. Owner: Rendering.
- [ ] Decide if the visibility buffer is per view or per render pass with a pass mask. Owner: Rendering.
- [ ] Decide if Hi-Z pyramid build and late cull should use async compute. Owner: Rendering.
- [ ] Decide if a conservative screen-size floor is needed for near or large AABBs. Owner: Rendering.
- [ ] Decide if OpenGL keeps full two-pass parity or stays single-phase plus diagnostics. Owner: Rendering.

## Out Of Scope

- CPU-direct hardware query correctness.
- CPU masked software occlusion correctness.
- Live screenshots, RenderDoc captures, profiler runs, hardware matrices, and promotion evidence. They are in the validation docs.
