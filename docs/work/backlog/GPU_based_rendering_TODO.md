# GPU-Based Rendering TODO (OpenGL + Vulkan)

Last Updated: 2026-02-11
Current Status: Not production-ready
Primary Objective: Ship a fully GPU-driven, VR-first render path with minimal CPU stalls and backend parity across OpenGL and Vulkan.

## Vision

The render thread should submit work, not build work.

Target runtime behavior:
1. Scene changes stream to GPU buffers with subdata updates and no per-frame full rebuilds.
2. Culling, pass filtering, occlusion culling, command compaction, material/pipeline binning, and draw count generation run on GPU.
3. CPU does not map/read large GPU buffers in the hot path.
4. Multi-draw indirect is used consistently on OpenGL and Vulkan with equivalent behavior.
5. One visibility/cull flow fans out efficiently to VR stereo outputs (full + foveated) and desktop mirror without duplicate scene traversal.

## Production Gates

The pipeline is production-ready only when all gates are true:

- Correctness
  - No missing or wrong draws during object move/add/remove/material changes.
  - No invalid draw command references after atlas changes.
  - Pass filtering, frustum/BVH culling, and occlusion culling produce deterministic, valid command sets.
- Performance
  - No per-frame CPU readback/mapping in shipping mode for culling/batching decisions.
  - No forced CPU fallback path in shipping mode.
  - Frame time remains stable under high command counts and frequent streaming.
  - VR path avoids per-eye CPU scene traversal and per-eye command rebuild in default mode.
- Backend parity
  - OpenGL and Vulkan produce equivalent visibility and draw results on the same scenes.
  - Count-draw and fallback behavior are explicitly tested per backend.
  - VR stereo/multiview fallbacks are explicit and produce equivalent visible sets.
- Test coverage
  - Unit and integration tests cover ingest, cull, occlusion, indirect build, batching, and remove/move edge cases.
  - VR tests cover single-pass stereo, multiview/NV fallback, foveated outputs, and mirror output correctness.

## Current Reality (Code Snapshot)

What exists now:
- GPU scene command buffers, mesh atlas, mesh metadata buffer.
- Compute shaders for reset, culling, indirect command generation.
- Material batching path and MDI submission.
- OpenGL and Vulkan indirect API hooks are implemented.

Current blockers:
- Culling path is effectively forced to passthrough when debug preference is unset.
  - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- Command payloads are created on add/remove, but no robust per-frame command update path exists for transform/material churn.
  - `XRENGINE/Rendering/Commands/GPUScene.cs`
  - `XRENGINE/Rendering/VisualScene3D.cs`
- Bounding sphere values used for culling/BVH are not guaranteed world-space transformed.
  - `XRENGINE/Rendering/Commands/GPUScene.cs`
  - `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
  - `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_aabb_from_commands.comp`
- Shared mesh atlas lifetime is unsafe without mesh reference counting on remove.
  - `XRENGINE/Rendering/Commands/GPUScene.cs`
- Batching still depends on CPU-side material grouping and optional CPU sort.
  - `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- Hi-Z occlusion assets exist but are not integrated into the active render path.
  - `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`
  - `XRENGINE/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs`
  - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
  - `Build/CommonAssets/Shaders/Compute/GPURenderHiZSoACulling.comp`
- VR stereo plumbing exists, but a unified GPU-driven per-view fan-out path is not locked down.
  - OpenGL: OVR multiview and NV stereo extension paths need consistent integration with culled command output.
  - Vulkan: parallel secondary command generation exists but needs explicit scheduling for full + foveated + mirror outputs.
- Atlas buffers are not fully cleaned up in scene destroy path.
  - `XRENGINE/Rendering/Commands/GPUScene.cs`

## Target Pipeline (End State)

1. Scene ingest
  - Renderable/submesh registration writes compact command records and mesh/material IDs.
  - Dirty commands are updated incrementally each frame.
2. Atlas and metadata maintenance
  - Mesh atlas uses ref-counted residency.
  - Mesh metadata is always valid for every referenced mesh ID.
3. GPU culling by pass
  - BVH/frustum culling consumes camera data and pass ID.
  - Output is a compact visible command list with count buffer.
4. Occlusion culling
  - GPU mode: depth prepass + Hi-Z pyramid + conservative sphere/AABB tests.
  - CPU-compatible mode: async hardware occlusion query path with conservative temporal hysteresis.
5. GPU binning and ordering
  - Visible commands are keyed by material/pipeline/pass and sorted or bucketed on GPU.
  - Batch ranges are GPU-generated.
6. GPU indirect build
  - Draw commands are built from visible/sorted command buffers.
  - Count buffer feeds `MultiDrawElementsIndirectCount` where supported.
7. VR view fan-out
  - Shared visible list expands into per-view workloads (left/right, full/foveated, mirror).
  - Stereo-capable paths avoid duplicated CPU submission.
8. Submission
  - Per-batch/state dispatch with minimal CPU involvement.
  - OpenGL and Vulkan follow the same logical flow.

## Phase Plan

## Phase 0 - Baseline Safety and Switches

Outcome: stop shipping with debug behavior and establish measurable baseline.

- [x] Change passthrough default to off for non-debug/shipping configurations.
- [x] Keep passthrough available only via explicit debug override.
- [x] Add one runtime log line per frame mode: passthrough, frustum, or BVH culling.
- [x] Add counters for CPU fallback usage and expose them in GPU stats/debug UI.
- [x] Add config validation on startup to warn on unsafe defaults.

## Phase 1 - Correctness First (Scene -> Atlas -> Cull)

Outcome: all data written to GPU is valid and stays valid through add/remove/move.

- [x] Implement command update API in `GPUScene` for existing commands.
  - [x] Update world matrix, previous world matrix, bounds, material ID, pass, instance count, flags.
  - [x] Avoid remove/re-add churn for simple transform updates.
- [x] Hook dirty command updates from scene/render-info collection.
- [x] Fix bounding sphere computation to world space (including scale).
  - [x] Use conservative radius for non-uniform scale.
- [x] Add mesh atlas reference counting.
  - [x] Increment on first use by command.
  - [x] Decrement on command removal.
  - [x] Remove atlas data only at zero ref count.
- [x] Ensure `Destroy()` cleans atlas buffers and related state.

Acceptance criteria:
- Moving objects remain correctly culled and rendered across many frames.
- Removing one renderer that shares a mesh does not corrupt other draws.
- No stale material/pass data after runtime changes.

## Phase 2 - Remove CPU Stalls from Hot Path

Outcome: render loop no longer depends on CPU readback/mapping for core decisions.

- [x] Remove CPU-side culled-buffer inspection for normal batch building.
- [x] Keep CPU sanitizer/fallback as debug-only path guarded by explicit setting.
- [x] Avoid per-frame mapping of count/command buffers in shipping mode.
- [x] Use GPU count buffer directly for draw submission.
- [x] Add profiling markers that report:
  - [x] number of mapped buffers in frame
  - [x] bytes read back from GPU
  - [x] number of CPU fallback events

Acceptance criteria:
- Shipping mode performs zero large-buffer readbacks for culling/batching.
- CPU fallback count remains zero in normal scenes.

## Phase 3 - Occlusion Culling (GPU + CPU-Compatible)

Outcome: add robust occlusion culling that works in both GPU-dispatch and CPU-compatible modes without introducing stalls.

- [x] Define occlusion mode matrix and runtime switch:
  - [x] `Disabled`
  - [x] `GPU_HiZ`
  - [x] `CPU_QueryAsync`
- [x] GPU path (`GPU_HiZ`):
  - [x] Add depth-only prepass for opaque occluders.
  - [x] Build Hi-Z pyramid every frame from prepass depth.
  - [x] Integrate Hi-Z compute into active culling flow after frustum/BVH reject and before indirect build.
  - [x] Use conservative sphere-first test, then AABB refinement for borderline cases.
  - [x] Keep uncertain results visible (never hard-cull on ambiguous depth tests).
- [x] CPU-compatible path (`CPU_QueryAsync`):
  - [x] Add backend-agnostic async occlusion query manager using existing render query abstractions.
  - [x] Use previous-frame query results only (no same-frame wait/read stalls).
  - [x] Batch query submission for occlusion candidates (not all draws).
  - [x] Default to visible when query data is unavailable/late.
- [x] Shared temporal policy (both modes):
  - [x] Track per-command visibility history.
  - [x] Apply hysteresis: require N consecutive occluded frames before hiding.
  - [x] Immediately re-test when camera jump/FOV change/object transform delta exceeds threshold.
  - [x] Reset temporal state on scene load, teleport, or large topology changes.
- [x] Integrate pass-awareness:
  - [x] Ensure occlusion tests operate per render pass and do not hide required shadow/depth-only contributors.
- [x] Add instrumentation:
  - [x] candidates tested
  - [x] occluded accepted
  - [x] false-positive recoveries
  - [x] temporal overrides

Acceptance criteria:
- Occlusion is stable (no obvious popping) under camera motion and animated transforms.
- GPU mode adds no CPU readback stalls.
- CPU-compatible mode uses async query latency and does not block the render thread.
- OpenGL and Vulkan produce equivalent visible-set behavior for the same test scenes.

## Phase 4 - Fully GPU-Driven Batching and Instancing

Outcome: material/pass grouping and instancing are GPU generated.

- [x] Implement GPU key generation for visible commands.
  - [x] Key includes pass, material/pipeline, mesh, and required render-state bits.
- [x] Implement GPU sort or bucket pipeline and output batch ranges.
- [x] Replace CPU `BuildMaterialBatches` dependency in default path.
- [x] Implement true instancing aggregation:
  - [x] group identical mesh/material/pass
  - [x] emit one indirect draw with instance count > 1
  - [x] store per-instance transforms in instance data buffer
- [x] Keep CPU batching path as emergency debug fallback only.

Acceptance criteria:
- Batch counts no longer depend on CPU sort toggle.
- Large scenes show reduced draw command count via instancing aggregation.

## Phase 5 - VR-First Stereo and Multi-View Efficiency

Outcome: one scene/cull flow efficiently drives stereo full, stereo foveated, and desktop mirror outputs.

- [x] Define a `ViewSet` model for all outputs in a frame:
  - [x] left eye full
  - [x] right eye full
  - [x] left eye foveated
  - [x] right eye foveated
  - [x] desktop mirror
- [x] Build visibility once (shared frustum/BVH + occlusion), then derive per-view visibility with lightweight refinement.
- [x] Add per-command view/pass masks and per-view constants in GPU buffers.
- [x] OpenGL stereo path:
  - [x] Make OVR multiview the preferred single-pass route when available.
  - [x] Keep NV stereo shader extension path as explicit fallback.
  - [x] Ensure both paths consume the same culled/indirect command data.
- [x] Vulkan stereo path:
  - [x] Build secondary command buffers in parallel for pass x view partitions.
  - [x] Schedule full + foveated + mirror outputs without render-thread blocking.
  - [x] Use multiview render paths where available; keep a parity fallback path.
- [x] Foveated rendering integration:
  - [x] Define per-view foveation tiers/regions and shading-rate policy.
  - [x] Reuse visibility where safe; add conservative margin to avoid edge popping.
  - [x] Force near-field/UI/critical layers into full-res path.
- [x] Mirror integration:
  - [x] Build mirror from already rendered eye textures by default (compose/blit), not a full extra scene render.
  - [x] Keep full scene mirror render as opt-in debug/quality mode only.
- [x] CPU-stall safeguards:
  - [x] Use per-frame ring buffers for view constants and avoid per-eye realloc/map churn.
  - [x] Eliminate same-frame waits for GPU-generated per-view command counts.
- [x] Add VR telemetry:
  - [x] per-view visible counts
  - [x] per-view draw counts
  - [x] command-buffer build time by worker
  - [x] render-thread submit time for VR frame

Acceptance criteria:
- VR frame uses one scene ingest/cull flow for all outputs.
- CPU render-thread cost scales sublinearly with number of views/outputs.
- No stereo mismatch/pop artifacts introduced by per-view refinement.
- OpenGL and Vulkan produce equivalent visible-set behavior in VR test scenes.

### Phase 5 Ticket Breakdown (File/Class Map)

- [x] `VR-01` Define `ViewSet` frame model and lifecycle owner.
  - Scope: create a single per-frame model for full-eye, foveated-eye, and mirror outputs.
  - Primary files/classes:
    - `XRENGINE/Engine/Engine.VRState.cs` (`CollectVisibleStereo`, `SwapBuffersStereo`, `Render`)
    - `XRENGINE/Rendering/XRViewport.cs` (`RenderStereo`)
    - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs` (runtime mode switches)
  - Implementation status:
    - [x] Added `GPUViewFlags`, `GPUViewMask`, `GPUViewDescriptor`, and `GPUViewConstants` schema in `XRENGINE/Rendering/Commands/GPUViewSet.cs`.
    - [x] Added runtime layout validation (`Marshal.SizeOf` assertions) in `XRENGINE/Rendering/Commands/GPUViewSet.cs` and `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`.
    - [x] Added `ConfigureViewSet(...)` and view-capacity tracking in `XRENGINE/Rendering/Commands/GPURenderPassCollection.ViewSet.cs`.
    - [x] Wire per-frame `ViewSet` construction from VR frame lifecycle (`Engine.VRState` / `XRViewport`).
    - [x] Route per-frame `ViewSet` data into active cull/render dispatch.
  - Done when:
    - One frame-level `ViewSet` object is built once per frame and handed to GPU culling/render stages.
    - No per-eye CPU scene traversal is required for default VR path.

- [x] `VR-02` Add per-command view eligibility without overloading command payload semantics.
  - Scope: avoid abusing `GPUIndirectRenderCommand.Reserved*`; use sidecar view-mask buffer.
  - Primary files/classes:
    - `XRENGINE/Rendering/Commands/GPUIndirectRenderCommand.cs`
    - `XRENGINE/Rendering/Commands/GPUScene.cs` (command add/remove/update path)
    - `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
    - `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp`
  - Done when:
    - command-view mask data is generated/updated incrementally with command changes.
    - culling/refinement stages can filter by view mask without CPU readback.

- [x] `VR-03` Allocate/bind `ViewSet` GPU buffers with stable binding contracts.
  - Scope: add view descriptor/constants buffers and per-view visible/count buffers.
  - Primary files/classes:
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs`
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
  - Implementation status:
    - [x] Added buffer scaffolding: `ViewDescriptorBuffer`, `ViewConstantsBuffer`, `CommandViewMaskBuffer`, `PerViewVisibleIndicesBuffer`, `PerViewDrawCountBuffer` in `XRENGINE/Rendering/Commands/GPURenderPassCollection.ViewSet.cs`.
    - [x] Added buffer binding slot constants (`11`-`15`) in `XRENGINE/Rendering/Commands/GPUViewSet.cs`.
    - [x] Integrated allocation/regeneration into `RegenerateBuffers(...)` in `XRENGINE/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs`.
    - [x] Integrated reset/disposal lifecycle hooks in `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`.
    - [x] Bind and consume these buffers in culling/indirect compute paths.
    - [x] Validate full OpenGL/Vulkan parity for the new view buffers.
  - Done when:
    - all new buffers are created, resized, reset, and rebound with deterministic binding points.
    - OpenGL and Vulkan run identical logical binding layout.

- [x] `VR-04` Implement shared-cull then per-view refinement compute flow.
  - Scope: one main visible list, then lightweight per-view refinement and per-view draw counts.
  - Primary files/classes:
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
    - `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
    - `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp`
    - `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`
  - Done when:
    - one cull dispatch produces shared candidates.
    - per-view refinement dispatches output view-partitioned ranges and indirect counts.

- [x] `VR-05` OpenGL single-pass stereo integration with unified command data.
  - Scope: keep OVR multiview preferred, NV stereo fallback, both using same culled/indirect buffers.
  - Primary files/classes:
    - `XRENGINE/Rendering/XRMeshRenderer.cs`
    - `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
    - `XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/XRTexture2DArray.cs`
    - `XRENGINE/Rendering/API/Rendering/Objects/Render Targets/XRFrameBuffer.cs`
  - Done when:
    - OVR and NV paths select shader variants differently but consume same draw payload source.

- [x] `VR-06` Vulkan parallel command-buffer fan-out for pass x view partitions.
  - Scope: schedule secondary command recording per pass/view, then execute in primary without render-thread stalls.
  - Primary files/classes:
    - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandPool.cs`
    - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
    - `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`
  - Implementation status:
    - [x] Added per-thread Vulkan command pools for parallel recording contexts in `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandPool.cs`.
    - [x] Added parallel secondary command-buffer batch recording for blit/indirect ops in `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`.
    - [x] Wire explicit OpenXR pass x view partition recording/execution on Vulkan runtime path.
  - Done when:
    - secondary command recording uses per-thread pools and avoids per-frame queue-idle waits in hot path.
    - full + foveated + mirror view partitions are recordable in parallel.

- [x] `VR-07` Foveated output policy and safe visibility margining.
  - Scope: define tiering/regions and conservative refinement rules to avoid stereo/foveation popping.
  - Primary files/classes:
    - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
    - `XRENGINE/Engine/Engine.VRState.cs`
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
  - Implementation status:
    - [x] Added per-view foveation policy settings and ViewSet descriptor population.
    - [x] Add conservative visibility margin policy for edge-pop suppression.
    - [x] Force near-field/UI-critical layers into full-res path.
  - Done when:
    - per-view foveation parameters are written each frame.
    - near-field/UI-critical meshes are forced to full-res path.

- [x] `VR-08` Mirror output defaults to composition, not full extra scene render.
  - Scope: compose/blit mirror from already rendered eye textures in default mode.
  - Primary files/classes:
    - `XRENGINE/Rendering/API/XRWindow.cs`
    - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
    - `XRENGINE/Engine/Engine.VRState.cs`
  - Done when:
    - desktop mirror can be shown while in VR without second full scene traversal by default.

- [x] `VR-09` Telemetry and guardrails for VR frame health.
  - Scope: expose per-view visibility/draw counts and command-build timing.
  - Primary files/classes:
    - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs`
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
    - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
  - Done when:
    - stats show per-view counts and command build timing in debug UI/profiler.
    - regressions are visible without GPU readback in shipping path.

### ViewSet Data Layout and GPU Buffer Schema (Phase 5 v0)

Source-of-truth intent:
- One `ViewSet` per frame.
- Command payload (`GPUIndirectRenderCommand`) remains stable; per-view data lives in sidecar buffers.
- Same binding contract on OpenGL and Vulkan.

Proposed C# CPU mirror layout (implementation target):

```csharp
[Flags]
public enum GPUViewFlags : uint
{
    None = 0,
    StereoEyeLeft = 1u << 0,
    StereoEyeRight = 1u << 1,
    FullRes = 1u << 2,
    Foveated = 1u << 3,
    Mirror = 1u << 4,
    UsesSharedVisibility = 1u << 5
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUViewDescriptor
{
    public uint ViewId;           // Stable index in this frame's ViewSet
    public uint ParentViewId;     // 0xFFFFFFFF if none (e.g., full-res roots)
    public uint Flags;            // GPUViewFlags
    public uint RenderPassMaskLo; // Pass mask bits [0..31]

    public uint RenderPassMaskHi; // Pass mask bits [32..63]
    public uint OutputLayer;      // Texture array layer / attachment slice
    public uint ViewRectX;        // Pixel rect origin
    public uint ViewRectY;

    public uint ViewRectW;        // Pixel rect size
    public uint ViewRectH;
    public uint VisibleOffset;    // Offset into PerViewVisibleIndices buffer
    public uint VisibleCapacity;  // Capacity reserved for this view

    public Vector4 FoveationA;    // xy=centerUV, z=innerRadius, w=outerRadius
    public Vector4 FoveationB;    // x=innerRate, y=midRate, z=outerRate, w=reserved
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUViewConstants
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Matrix4x4 ViewProjection;
    public Matrix4x4 PrevViewProjection;
    public Vector4 CameraPositionAndNear; // xyz + near
    public Vector4 CameraForwardAndFar;   // xyz + far
}
```

Proposed GPU sidecar buffers (new, in addition to current command/indirect buffers):
- `binding = 11` `ViewDescriptorBuffer` (`GPUViewDescriptor[]`)
  - Producer: CPU once per frame.
  - Consumer: cull refinement + indirect build + backend submit path.
- `binding = 12` `ViewConstantsBuffer` (`GPUViewConstants[]`)
  - Producer: CPU once per frame.
  - Consumer: per-view culling/refinement and shaders requiring per-view matrices.
- `binding = 13` `CommandViewMaskBuffer` (`uint2[]` bitmask per command)
  - `x`: view bits [0..31], `y`: view bits [32..63].
  - Producer: CPU incremental updates on add/remove/material/pass changes.
  - Consumer: cull/refinement filters.
- `binding = 14` `PerViewVisibleIndicesBuffer` (`uint[]`)
  - Producer: GPU refinement stage writes compact command indices per view.
  - Consumer: indirect build stage.
- `binding = 15` `PerViewDrawCountBuffer` (`uint[]`)
  - Producer: GPU indirect build writes final draw counts per view.
  - Consumer: draw submission (`*IndirectCount` path where available).

Update cadence and ownership:
- Frame-begin CPU writes:
  - `ViewDescriptorBuffer`, `ViewConstantsBuffer`.
- Scene-dirty CPU writes:
  - `CommandViewMaskBuffer` subdata ranges only.
- GPU per-pass writes:
  - reset per-view counters -> refine visibility -> build per-view indirect counts.

Backend constraints:
- OpenGL and Vulkan must use the same binding indices and struct packing.
- Add startup/assert checks for `Marshal.SizeOf<GPUViewDescriptor>()` and `Marshal.SizeOf<GPUViewConstants>()` matching shader expectations.
- Keep `GPUIndirectRenderCommand.Reserved1` semantics unchanged (currently used as source index during compaction paths).

## Phase 6 - OpenGL/Vulkan Parity Lockdown

Outcome: same logical behavior and validated output on both backends.

- [x] Build parity checklist for indirect features:
  - [x] draw indirect buffer binding
  - [x] parameter buffer binding
  - [x] count draw support and fallback behavior
  - [x] index-buffer validation and sync
- [x] Add cross-backend integration tests that compare:
  - [x] visible command count
  - [x] draw count
  - [x] sampled command signatures (mesh/material/pass)
- [x] Resolve any backend-specific stride/offset/count differences.
- [x] Document known extension requirements and runtime capability matrix.

Implementation notes:
- Runtime parity checklist and dispatch-path selection are centralized in `XRENGINE/Rendering/HybridRenderingManager.cs` (`IndirectParityChecklist`, `BuildIndirectParityChecklist`, and dispatch logging).
- Cross-backend parity snapshot/signature comparison utilities are in `XRENGINE/Rendering/Commands/GpuBackendParitySnapshot.cs`.
- Phase 6 test coverage is in `XREngine.UnitTests/Rendering/GpuBackendParityTests.cs`.
- Backend capability matrix doc: `docs/features/gpu_indirect_backend_capability_matrix.md`.

Acceptance criteria:
- Same test scenes pass on OpenGL and Vulkan with equivalent results.

## Phase 7 - Production Hardening

Outcome: stable long-running behavior under stress.

- [ ] Run stress tests:
  - [ ] massive add/remove bursts
  - [ ] continuous transform animation
  - [ ] many-pass scenes
  - [ ] high material diversity
- [ ] Validate no memory leaks or stale resources after repeated scene load/unload.
- [ ] Add crash-safe handling for buffer overflow/truncation with diagnostics.
- [ ] Finalize docs for runtime toggles, debug tools, and fallback behavior.

Acceptance criteria:
- 30+ minute stress run with no corruption, leak growth, or fallback thrashing.

## Immediate Sprint Start (Recommended Order)

1. Disable forced passthrough default and prove frustum/BVH path is active.
2. Add incremental command update path in `GPUScene` and wire dirty updates.
3. Fix world-space bounds generation for culling and BVH AABB build.
4. Implement mesh atlas reference counting and safe remove behavior.
5. Land occlusion Phase 3 baseline (`GPU_HiZ` + `CPU_QueryAsync` scaffolding) with telemetry.
6. Land `VR-01` + `VR-03`: `ViewSet` model + buffer scaffolding.
7. Land `VR-02` + `VR-04`: command view-mask sidecar + per-view refinement flow.
8. Land `VR-05` + `VR-06`: OpenGL single-pass integration and Vulkan parallel fan-out.
9. Land `VR-07` + `VR-08`: foveation policy and mirror-compose default path.
10. Add regression tests (`VR_*`) + telemetry (`VR-09`) before parity hardening.

## Test Backlog (Must Add)

- [x] `GPUScene_AddRemove_SharedMeshRefCount_RemainsValid`
- [x] `GPUScene_UpdateCommand_TransformChange_UpdatesCullingBounds`
- [x] `GPURenderPass_BvhCull_UsesRealCullingPath_WhenEnabled`
- [x] `GPURenderPass_NoCpuFallback_InShippingConfig`
- [x] `Occlusion_HiZ_GPUPath_CullsAndRecovers_Correctly`
- [x] `Occlusion_CPUQueryAsync_NoRenderThreadStall`
- [x] `Occlusion_TemporalHysteresis_ReducesPopping`
- [x] `Occlusion_OpenGL_Vulkan_Parity_BasicScene`
- [x] `IndirectPipeline_OpenGL_Vulkan_Parity_BasicScene`
- [x] `IndirectPipeline_OpenGL_Vulkan_Parity_MultiPass`
- [x] `VR_ViewSet_SharedCull_FansOut_AllOutputs`
- [x] `VR_OpenGL_Multiview_And_NVFallback_UseSameVisibleSet`
- [x] `VR_Vulkan_ParallelSecondaryCommands_NoRenderThreadBlock`
- [x] `VR_Foveated_PerViewRefinement_NoStereoPopping`
- [x] `VR_Mirror_Compose_NoExtraSceneTraversal_DefaultMode`

## Test Backlog (Next Wave)

- [ ] `GPUScene_Destroy_CleansAtlasBuffers_AndState`
- [ ] `GPUScene_IncrementalUpdates_NoRemoveReaddChurn_ForTransformOnlyChanges`
- [ ] `GPUScene_AtlasRefCount_MassRemove_SharedMeshesRemainConsistent`
- [ ] `GPUCulling_PassMaskFiltering_RejectsWrongPassCommands`
- [ ] `GPUCulling_CountOverflow_SetsTruncationFlag_AndPreservesValidity`
- [ ] `GPUCulling_DebugPassthrough_RequiresExplicitOverride`
- [ ] `Occlusion_GPUHiZ_AmbiguousDepth_KeepsVisible`
- [ ] `Occlusion_CPUQueryAsync_UsesPreviousFrameResults_WithoutSameFrameWait`
- [ ] `Occlusion_ResetOnCameraJump_AndSceneTopologyChange`
- [ ] `IndirectBuild_CountBuffer_DrivesCountDraw_WhenSupported`
- [ ] `IndirectBuild_FallbackPath_UsesClampedCounts_WhenCountDrawUnsupported`
- [ ] `IndirectBuild_CommandSignatureParity_OpenGL_Vulkan_LargeScene`
- [ ] `Batching_GPUGeneratedKeys_StableAcrossFrames_WithMaterialChurn`
- [ ] `Batching_InstancingAggregation_CombinesEquivalentMeshMaterialPass`
- [ ] `ViewSet_CommandViewMaskUpdates_TrackMaterialPassAndLayerChanges`
- [ ] `VR_ViewSet_PerViewCounters_MatchVisibleRanges`
- [ ] `VR_OpenGL_OVR_NVFallback_Parity_MultiPassFoveatedMirror`
- [ ] `VR_Vulkan_ParallelRecording_NoQueueIdleInHotPath`
- [ ] `VR_MirrorCompose_DefaultPath_AvoidsExtraSceneTraversal`
- [ ] `Stress_ThirtyMinute_AddRemoveAnimate_ManyPasses_NoFallbackThrash`

## Notes for Implementation

- Keep debug features, but isolate them so shipping mode stays GPU-driven.
- Prefer subdata updates over full buffer uploads for incremental changes.
- Do not gate correctness on optional extensions.
- Treat CPU sanitizer/fallback as diagnostics, not primary execution path.
- Prefer "cull once, fan out many views" over per-eye scene traversal.
- Keep OpenGL and Vulkan view/pipeline key layouts aligned so VR parity tests stay meaningful.
