# GPU Hi-Z Occlusion Culling

[← Rendering Architecture index](README.md)

This document defines the GPU-driven Hi-Z occlusion path. The path uses GPU culling, optional GPU scene BVH traversal, Hi-Z depth, and indirect draw buffers without frame-loop CPU readback.

## Scope

The contract applies to `GPURenderPassCollection`, `GPUScene`, `GpuBvhTree`, the Hi-Z occlusion shaders, and the indirect draw builders. It covers the `GpuHiZ` mode on GPU dispatch strategies. CPU direct paths use [CPU Query Async Occlusion](../../developer-guides/rendering/cpu-query-async-occlusion.md) or [CPU Software Occlusion](cpu-software-occlusion.md).

## Runtime Actors
| Actor | Responsibility |
|---|---|
| `GPUScene` | Owns source command indices, draw metadata, bounds, view masks, GPU scene BVH resources, and cull control buffers. |
| `GPURenderPassCollection` | Owns pass-local cull buffers, count buffers, phase-one buffers, visibility history, Hi-Z pyramid resources, indirect buffers, and synchronization points. |
| `GPURenderCulling.comp` and `bvh_frustum_cull.comp` | Produce the first compact candidate list. The BVH path is a frustum accelerator. It is not yet an occlusion traversal authority. |
| `GPURenderOcclusionPhaseOne.comp` | Emits commands that were visible in the prior visibility history. New or identity-mismatched commands are visible. |
| `GPURenderOcclusionHiZ.comp` | Tests candidates against the active Hi-Z pyramid. In the late pass, it updates visibility history and emits newly visible commands. |
| `GPURenderBuildBatches.comp` and related builders | Convert the current compact visible list into indirect draw data and material-tier draw counts. |

## Cull, Occlusion, And Indirect Buffer Contract

The GPU path uses stable source command indices. GPU scene metadata and bounds are the source of truth. Compact buffers store draw ids, not copied render commands.

`Cull(scene, camera, deferGpuHiZ: true)` writes the candidate draw ids into `_culledSceneToRenderBuffer` and count data into `_culledCountBuffer`. The two-pass path first copies that count into `_twoPassCandidateCountBuffer`. It then clears the phase counters, overflow flag, and per-view draw counts.

The early pass reads `_culledSceneToRenderBuffer`, `_twoPassCandidateCountBuffer`, `scene.CullControlBuffer`, `scene.CullBoundsBuffer`, the command view masks, and `_twoPassVisibilityBuffer`. It writes early visible ids to `_twoPassPhaseOneCommandBuffer`, early counts to `_cullCountScratchBuffer`, and compact view masks to `TwoPassPhaseOneCulledCommandViewMaskBuffer`. The output count is copied back to `_culledCountBuffer` and `_twoPassPhaseOneCountBuffer` before indirect build.

The early draw temporarily binds the early command buffer and early view-mask stream as the active visible set. It builds indirect buffers and submits draws through the normal visible-set path. The draw writes depth and color.

The depth pyramid is built from the early draw depth. The late pass then resets the shared visible counters, reads the original candidate buffer and active Hi-Z texture, updates `_twoPassVisibilityBuffer`, writes late visible ids into `_occlusionCulledBuffer`, and copies the late count into `_culledCountBuffer`. `SwapCulledBufferAfterOcclusion()` makes the late list the active visible list. The late draw uses the normal visible-set path again.

Production zero-readback modes must not map visible counts, visibility bits, or overflow flags in the frame loop. Diagnostic readbacks can be queued through explicit delayed counter diagnostics.

## Two-Pass Flow

The current implementation prepares two-pass Hi-Z only when all conditions are true:

- `EOcclusionCullingMode.GpuHiZ` is the active mode.
- `EMeshSubmissionStrategy.GpuIndirectZeroReadback` is the active strategy.
- The pass has a nonzero command count.
- The pass is not an external shared-visibility VR filter.
- The owner pipeline is not a shadow pipeline.
- The pass does not use forward depth-normal material variants.
- Required shaders and buffers are available.
- The active depth input is current depth and not history depth.
- Current-depth self-cull risk does not force a bypass for the pass.

The flow is:

1. Evaluate temporal invalidation from the scene, camera, depth input, and view target.
2. Emit the early visible set. When temporal state is unsafe, force the early set visible so stale history cannot hide geometry.
3. Draw the early set.
4. Build the Hi-Z pyramid from the early depth.
5. Publish stable Hi-Z history for meshlet and diagnostic consumers.
6. Reset visible counters.
7. Test the candidate set against the current pyramid and update visibility history.
8. Draw the newly visible late set.
9. Publish diagnostic descriptors and stage timing without mapping production buffers.

`RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode` reports `two-phase-current-depth` after a successful current-depth two-pass run. The phase count telemetry reports the early candidate upper bound without reading the late GPU count in production mode.

## Visibility History

`_twoPassVisibilityBuffer` is currently allocated as two `uint` values per source draw id. The stored values are `RenderIdentityID + 1` and a visible flag. An identity mismatch is treated as visible, so recycled or new commands cannot disappear before they draw once.

The current buffer is not yet a persistent per-view visibility buffer. It does not store independent left-eye and right-eye history words. It also does not yet include GPU-side remap data for command compaction. Stereo and command-remap work remains open.

## GPU Scene BVH Relationship

The GPU scene BVH accelerates frustum culling before Hi-Z. Its node layout and traversal contract are documented in [GPU Scene BVH](gpu-scene-bvh.md). The current BVH traversal does not sample Hi-Z and does not reject interior nodes by occlusion. Node-level occlusion must reuse the existing 48-byte node layout, primitive ranges, queue-pressure behavior, and zero-readback diagnostics.

## Meshlet Relationship

The meshlet path can consume the active Hi-Z texture through the existing meshlet culling hooks. Command-level two-pass Hi-Z must not reject a command whose meshlets could still survive unless meshlet-level recovery owns that command. Meshlet-level early and late counts remain a separate integration item.

## Synchronization Contract

Backends must preserve these ordering points:
| Boundary | Producer | Consumer | Required ordering |
|---|---|---|---|
| Cull to early preparation | GPU cull or BVH traversal writes candidate ids and counts. | Early phase reads candidate ids and count. | Shader-storage and command ordering before early compute and indirect-count use. |
| Early preparation to early raster | Early phase writes early ids, counts, view masks, and per-view lists. | Indirect builders and early draw consume those buffers. | Shader-storage and command ordering before indirect buffer generation and draw. |
| Early raster to depth pyramid | Early draw writes depth. | Hi-Z build samples or reads depth. | Framebuffer/depth-to-texture and shader-resource visibility. |
| Depth pyramid to late preparation | Hi-Z build writes pyramid mips or coarse tiles. | Late phase samples the pyramid and writes late ids. | Texture/image and shader-storage ordering. |
| Late preparation to late raster | Late phase writes late ids, counts, view lists, and visibility history. | Indirect builders and late draw consume those buffers. | Shader-storage and command ordering before indirect draw. |
| Late raster to consumers | Late draw writes color and depth. | Later render passes and diagnostics consume the frame. | Normal pass output ordering. |

OpenGL uses `AdvancedVisibilitySynchronizationContract.ApplyOpenGl(...)` and explicit `MemoryBarrier` masks. The current OpenGL barriers include `ShaderStorage` and `Command` for compute-to-indirect transitions. Vulkan must map the same boundaries to resource-specific barriers and synchronization2 scopes in the backend command stream.

The legacy single-pass dirty bypass remains controlled by `XRE_GPU_HIZ_DIRTY_BYPASS`. It is still present in code and defaults to enabled. That branch must issue an explicit shader-storage and command barrier before the indirect draw consumes cull output, because the skipped refine dispatch no longer provides the implicit barrier.

## Diagnostics

`OcclusionTelemetry` records GPU Hi-Z skip reasons, active mode, depth source, pass counts, dirty passthrough, and GPU elapsed timing. `RuntimeEngine.Rendering.Stats.GpuDriven` records Hi-Z mode, one-phase and two-phase frame counters, and phase draw counters. `EngineProfilerDataSource` exposes these counters to editor tooling.

`TryGetCompletedTwoPassDiagnostic` and `TryGetVisibilityDiagnostic` return GPU-owned buffer references only for the matching logical frame. They do not map or read GPU memory.

## Environment And Settings
| Setting or variable | Purpose |
|---|---|
| `GpuOcclusionCullingMode` | Selects `Disabled`, `CpuQueryAsync`, `CpuSoftwareOcclusion`, or `GpuHiZ`. |
| `ForceMeshSubmissionStrategy` / `XRE_FORCE_MESH_SUBMISSION_STRATEGY` | Selects `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, or meshlet strategies. |
| `CacheGpuHiZOcclusionOncePerFrame` | Reuses one Hi-Z pyramid per frame where the depth contract allows it. |
| `XRE_GPU_HIZ_DIRTY_BYPASS` | Controls the legacy single-pass dirty bypass. `0` or `false` disables it. |
| `XRE_HIZ_STAGE_LOGGING` | Writes Hi-Z stage timing summaries to the normal log directory. |
| `XRE_GPU_HIZ_COARSE_TILES` | Enables the bounded coarse-tile Hi-Z diagnostic path. |
| `XRE_GPU_HIZ_COARSE_TILES_CULL` | Enables culling against the coarse-tile path when the diagnostic path is active. |

## Known Limits

- Visibility history is command-wide, not per view.
- GPU-side visibility remap for command compaction is not implemented.
- BVH traversal is frustum-only and does not reject occluded interior nodes.
- Meshlet-level late recovery is not complete.
- External shared-visibility VR passes bypass GPU Hi-Z refinement.
- Shadow and depth-variant passes stay occlusion-exempt.
- Current-depth self-cull risk can still bypass the two-pass path for forward depth-normal passes.
- The legacy dirty bypass is still present and must remain visible in diagnostics until removed.
