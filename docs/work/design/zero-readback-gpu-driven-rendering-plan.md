# Zero-Readback GPU-Driven Rendering Plan

## 1. Executive Summary

This document defines the target architecture for a fully GPU-driven rendering path in XRENGINE where the CPU is limited to:

- Publishing camera/view constants.
- Uploading dirty scene state ranges such as model transforms, previous transforms, and bone matrices.
- Managing asset lifetime and residency.
- Submitting a small number of fixed pass dispatches.

The GPU must handle the rest without CPU readback or CPU-side draw orchestration:

- Visibility classification.
- Frustum, distance, and occlusion culling.
- LOD selection.
- Transparency-domain classification.
- Sort-key generation.
- Material/state batching.
- Indirect draw generation.
- Draw count emission.
- Final draw submission.

The core change is architectural, not cosmetic: the current path still performs CPU-visible reads of GPU-produced counters and batch ranges, then iterates material batches on the CPU to issue indirect draws. That design preserves much of the synchronization cost of a traditional renderer while paying the complexity cost of a GPU-driven one. The target design removes all runtime CPU readbacks from the shipping GPU path.

This plan supersedes the hybrid assumptions in `docs/work/design/IndirectGPURendering-Design.md` where CPU batch iteration remained part of the render loop.

---

## 2. Problem Statement

## 2.1 Current behavior

The current GPU-driven path in `GPURenderPassCollection.Render(...)` and `HybridRenderingManager.RenderTraditionalBatched(...)` performs the following high-level sequence:

1. CPU prepares pass state.
2. GPU culls commands and builds indirect draw data.
3. CPU reads batch counts and batch ranges back from GPU-visible buffers.
4. CPU loops material batches.
5. CPU binds material state and issues indirect draws per batch.

Additional hot-path problems observed during investigation:

- GPU-built batch ranges are read back in `ReadGpuBatchRanges()` (`IndirectAndMaterials.cs:544`), called from the main `Render()` path — not a debug-only code path.
- GPU counters are read with mapped-buffer reads in `ReadUIntAt(...)` (`CullingAndSoA.cs:163`) at **16+ call sites** across the shipping path.
- Transparency domain counts (4 uints) are read back in `ClassifyTransparencyDomains()` (`IndirectAndMaterials.cs:407-410`) and used by the CPU to structure subsequent passes.
- Visible draw and instance counts are read back in `UpdateVisibleCountersFromBuffer()` (`CullingAndSoA.cs:337-338`).
- Per-view draw counts are read back (`ViewSet.cs:201`).
- The GPU skinned-bounds compute path (`SkinnedMeshBoundsCalculator`) calls `WaitForGpu()` then maps and reads the full skinned vertex position buffer back to CPU — a hidden GPU→CPU synchronization point.
- The batched indirect path previously read world matrices back out of the culled command buffer to populate a legacy `ModelMatrix` uniform (now removed).
- `CoalesceContiguousBatches()` in `HybridRenderingManager` merges adjacent material batches but operates on the batch list produced by `ReadGpuBatchRanges()`, making it transitively dependent on readback data.
- Debug and validation modes have historically caused extra persistent mapping, logging, and buffer inspection overhead.
- Real-time scene captures and probe updates can interleave with the same render path and distort timings if not isolated.

### 2.1.1 Complete readback inventory

| Readback | Location | Hot path? | Removal phase |
|----------|----------|-----------|---------------|
| Batch ranges | `ReadGpuBatchRanges()` IndirectAndMaterials.cs:544 | Yes | Phase 1 |
| Batch count | `ReadUIntAt()` IndirectAndMaterials.cs:549 | Yes | Phase 1 |
| Transparency domain counts (4 uints) | IndirectAndMaterials.cs:407-410 | Yes | Phase 2 |
| Visible draw/instance counts | CullingAndSoA.cs:337-338 | Yes | Phase 1 |
| Per-view draw counts | ViewSet.cs:201 | Yes | Phase 2 |
| Draw count (stats/fallback) | Core.cs:617-619 | Partial | Phase 1 |
| Occlusion input count | Occlusion.cs:649, 682 | Yes | Phase 2 |
| Skinned vertex positions | SkinnedMeshBoundsCalculator.cs:65-67 | Yes (animated) | Phase 4 |
| World matrix (legacy) | HybridRenderingManager (removed) | Eliminated | Done |

## 2.2 Why this is fundamentally expensive

Even when OpenGL reports no API errors, the path can still be slow because the CPU waits on memory the GPU just wrote. This creates implicit synchronization points:

- GPU writes batch count.
- CPU maps/reads batch count.
- GPU writes batch ranges.
- CPU maps/reads batch ranges.
- CPU uses those results to decide how many draw calls to issue.

This breaks the main advantage of GPU-driven rendering: the CPU should not need to observe intermediate visibility results in order to render the frame.

## 2.3 Design goal

The shipping path must support this invariant:

> After the CPU uploads dirty scene/camera state for the frame, all visibility, sorting, batching, and draw generation decisions remain entirely GPU-resident until final presentation.

---

## 3. Goals and Non-Goals

## 3.1 Goals

- Remove all CPU readbacks from the shipping GPU-driven render path.
- Keep camera state, model matrices, and bone matrices GPU-resident and updated by dirty range uploads only.
- Eliminate CPU-side material batch iteration for indirect draws.
- Preserve pass semantics for deferred, forward, masked, transparent, shadow, and on-top passes.
- Support stereo and multi-view without per-view CPU visibility work.
- Keep the engine core backend-agnostic.
- Make the render graph the source of truth for pass/resource ordering.
- Maintain deterministic debug tooling through optional explicit debug modes, not through the shipping path.

## 3.2 Non-goals

- This document does not require immediate mesh shader/meshlet unification.
- This document does not require bindless-only materials on day one, although bindless descriptors fit the long-term architecture better.
- This document does not require removal of every CPU renderer path. CPU rendering remains valid for fallback, headless, and diagnostics.
- This document does not attempt to redesign every material/shader permutation system in the same change.

---

## 4. Design Principles

1. No CPU visibility decisions after scene upload.
2. No CPU observation of GPU-produced counters in the shipping path.
3. Stable draw IDs and resource IDs across frames.
4. Dirty-range uploads instead of full-scene repacks whenever feasible.
5. GPU-friendly SoA layouts over fat AoS command structs in hot paths.
6. Separate shipping path from diagnostics path.
7. Render graph metadata must describe the same dependencies the backend executes.
8. Transform and animation data must be versioned so the GPU can derive previous-frame data for motion vectors and temporal effects.

---

## 5. Current Architecture Summary

Relevant current components:

- `GPUScene`
  - Stores `AllLoadedCommandsBuffer`, mesh atlas buffers, `MeshDataBuffer`, material maps, and command counts.
- `GPURenderPassCollection`
  - Owns culling, batching, indirect generation, pass-local buffers, and counters.
- `HybridRenderingManager`
  - Owns graphics program selection, material binding, VAO binding, and indirect submission.
- `RenderPipelineGpuProfiler`
  - Measures render command timing, but currently reports a path that includes both GPU work and CPU-side orchestration cost.

Current command layout:

- `GPUIndirectRenderCommand` (192 bytes, `StructLayout.Sequential`)
  - `WorldMatrix` (Matrix4x4, 64 bytes)
  - `PrevWorldMatrix` (Matrix4x4, 64 bytes)
  - `BoundingSphere` (Vector4)
  - `MeshID`, `SubmeshID`, `MaterialID`, `InstanceCount` (uint each)
  - `RenderPass`, `ShaderProgramID` (uint each)
  - `RenderDistance` (float)
  - `LayerMask`, `LODLevel`, `Flags` (uint each)
  - `Reserved0`, `Reserved1` (uint each)
- Existing partial SoA decomposition:
  - `GPUIndirectRenderCommandHot` (168 bytes) — bounds, IDs, flags, layer mask, LOD, source index. Consumed by SoA culling.
  - `GPUIndirectRenderCommandCold` (144 bytes) — transforms, shader program, render distance. Consumed by draw generation.
  - `ToHot()`, `ToCold()`, `FromHotCold()` conversion helpers.
  - `GPURenderCullingSoA.comp` and `GPURenderExtractSoA.comp` already consume these.

Current structural issues:

- Even the Hot/Cold split is still coarse: both contain fields unused by their primary consumer.
- A single command struct mixes static metadata, infrequently changing metadata, and per-frame transform state.
- The renderer derives batches on GPU but consumes those batches on CPU.

---

## 6. Target Architecture

## 6.1 High-level frame flow

### CPU phase

The CPU performs only these per-frame responsibilities:

1. Update camera/view/projection constants.
2. Upload dirty object transforms.
3. Upload dirty previous transforms.
4. Upload dirty bone matrices and skinning state.
5. Upload dirty material-instance parameters if they changed.
6. Submit a fixed sequence of render-graph passes.

### GPU phase

For each render pass, the GPU performs:

1. Reset counters/work queues.
2. Read draw metadata, transform streams, and material metadata.
3. Derive world-space bounds if needed.
4. Perform culling.
5. Classify transparency domain.
6. Generate sort keys.
7. Sort visible draw IDs.
8. Build indirect draw records and per-state draw clusters.
9. Emit draw-count buffer(s).
10. Execute `MultiDraw*IndirectCount` or the Vulkan equivalent directly from GPU-produced buffers.

The CPU does not inspect any intermediate result.

## 6.2 Fixed pass submission model

The CPU submits a stable pass schedule, not a dynamic per-batch draw schedule. Example:

1. `ResetGpuDrivenPass`
2. `CullAndClassifyPass`
3. `SortVisiblePass`
4. `BuildIndirectPass`
5. `DrawOpaqueDeferred`
6. `DrawMaskedForward`
7. `DrawTransparentForward`

Each draw pass uses a GPU-generated draw-count buffer and GPU-generated indirect command buffer.

---

## 7. Data Model Refactor

## 7.1 Split the current command struct

The current `GPUIndirectRenderCommand` should be decomposed into separate streams.

### Proposed persistent streams

#### 1. `DrawMetadataBuffer`

Per-draw stable metadata:

- `DrawID`
- `MeshID`
- `SubmeshID`
- `MaterialID`
- `TransformID`
- `SkinID` or `BonePaletteRange`
- `RenderPassMask`
- `LayerMask`
- `Flags`
- `LodPolicy`
- `ShaderVariantID` or `PipelineClassID`

This data changes rarely.

#### 2. `TransformBuffer`

Per-transform current frame:

- `WorldMatrix`
- optional compressed/object-to-world representation later

#### 3. `PrevTransformBuffer`

Per-transform previous frame:

- `PrevWorldMatrix`

#### 4. `BoundsBuffer`

Per-draw current culling bounds:

- `BoundingSphere` and/or `AABB`
- `BoundsVersion`

For rigid draws, this can be derived from local bounds + current transform.
For skinned draws, this must come from GPU-updated skinned bounds.

#### 5. `SkinningPaletteBuffer`

Per-bone or per-skin-palette matrices:

- `BoneMatrix`
- optionally `PrevBoneMatrix`

#### 6. `MaterialStateBuffer`

Per-material or per-material-instance data:

- pipeline/state classification
- descriptor indices or bindless handles
- render options bits
- transparency mode

#### 7. `VisibleDrawIDsBuffer`

Compact list of visible draw IDs output by culling.

#### 8. `SortKeysBuffer`

Per-visible-draw sort key payload.

#### 9. `IndirectCommandBuffer`

GPU-generated indirect draw records.

#### 10. `IndirectCountBuffer`

GPU-generated draw counts for count-based submission.

## 7.2 Why split the data

Benefits:

- Dirty transform uploads do not rewrite static mesh/material metadata.
- Dirty bone uploads do not repack the command list.
- Culling consumes compact hot data.
- Draw generation can reference stable draw IDs instead of copying fat structs around.
- Previous-frame data becomes explicit and versionable.

---

## 8. CPU Responsibilities in the Target Model

## 8.1 Camera state

Each frame the CPU uploads camera state to a per-frame constant/uniform/storage buffer:

- view matrix
- projection matrix
- view-projection matrix
- inverse matrices
- previous view-projection matrix
- frustum planes
- camera position
- clip-space conventions
- viewport/scissor dimensions
- stereo view data when relevant

The CPU must not use camera state to decide visibility for the shipping GPU path.

## 8.2 Transform updates

When a node moves:

- mark its `TransformID` dirty
- write current matrix into `TransformBuffer`
- preserve or rotate previous matrix into `PrevTransformBuffer`
- optionally mark derived rigid bounds dirty if they are not recomputed fully on GPU

Updates should be range-batched or gathered into an upload list. Full-scene copies should be avoided once stable IDs exist.

## 8.3 Bone matrix updates

When a skeleton animates:

- upload dirty palette range(s) to `SkinningPaletteBuffer`
- rotate previous palette data when required for motion vectors or animated bounds
- mark skinned bounds dirty for GPU refresh

The CPU should not recompute skinned world bounds as part of the shipping path.

## 8.4 Asset lifetime and residency

The CPU still owns:

- mesh atlas residency or mesh buffer residency
- material/resource lifetime
- descriptor/bindless handle validity
- stable ID assignment

But it must not read GPU-generated visibility/batching results to manage those assets during the frame.

---

## 9. GPU Responsibilities in the Target Model

## 9.1 Bounds derivation

### Rigid meshes

For rigid draws, the GPU can derive world-space bounds from:

- local bind-pose bounds
- current transform matrix

This can occur:

- lazily in culling kernels, or
- in a prepass that writes `BoundsBuffer`

### Skinned meshes

Skinned bounds must be GPU-computed. Two viable approaches:

#### Option A. GPU bounds reduction from skinned vertices

- compute skinning or fetch bone matrices
- transform relevant vertices
- reduce to bounds/AABB/sphere

Pros:

- accurate

Cons:

- expensive for large animated meshes if done naively every frame

#### Option B. GPU per-bone or per-cluster bounds composition

- precompute bind-pose sub-bounds per bone or cluster offline
- transform those sub-bounds on GPU using current bone matrices
- reduce to object bounds

Pros:

- much cheaper than per-vertex reduction

Cons:

- requires new asset preprocessing and runtime data structures

Recommended direction:

- Start with Option A for correctness and debugging.
- Migrate to Option B for shipping scalability.

## 9.2 Culling

The culling kernel consumes:

- `DrawMetadataBuffer`
- `TransformBuffer`
- `BoundsBuffer` or local bounds + transform
- camera constants
- optional Hi-Z pyramid
- optional GPU BVH or scene acceleration structures

It writes:

- visible draw IDs
- visibility counters
- optional per-view visible lists

## 9.3 Sorting and batching

The GPU generates sort keys using only GPU-resident metadata. Suggested sort-key fields:

- pass class
- pipeline/material state class
- depth bucket or strict depth key depending on pass
- draw ID

The GPU then sorts visible draw IDs and emits indirect draw segments that are already grouped by the pipeline/material state class required for submission.

## 9.4 Indirect draw generation

The GPU emits:

- `DrawElementsIndirectCommand` or backend-equivalent records
- one count buffer per submission stream
- optional range metadata for debugging only

Shipping path requirement:

- debug-only range metadata may exist, but the shipping renderer must not read it.

---

## 10. Submission Model Without CPU Readbacks

## 10.1 Backend capability (already implemented)

The engine **already supports** count-based indirect submission:

- OpenGL: `glMultiDrawElementsIndirectCount` is implemented in `OpenGLRenderer.cs` and used as the primary submission path in `DispatchRenderIndirectRange()`. The GPU writes `DrawCount` from compute shaders (`GPURenderIndirect.comp`, `GPURenderBuildBatches.comp`), and the parameter buffer is bound for count-based submission.
- Vulkan: `vkCmdDrawIndexedIndirectCount` equivalent will follow the same pattern.

The draw command buffer and count buffer already stay GPU-resident. The remaining problem is that **the CPU also reads the same count back** via `ReadUIntAt()` for stats and fallback logic. The fix is to stop reading GPU-written counts on CPU in the shipping path, not to add new submission capabilities.

## 10.2 Material/state binding strategy

The key blocker to removing CPU batch iteration is state binding. The CPU currently loops material batches because it binds programs and material state per batch.

This must be replaced with one of these models:

### Model A. State-class partitioned submission

The GPU builds a separate indirect stream per pipeline/material state class, and the CPU submits one indirect call per known class.

CPU work becomes:

- bind state class N
- submit indirect count stream N

The CPU does not inspect counts. It just submits the pass-defined streams.

This is the recommended first shipping architecture.

### Model B. Bindless/material-table single pipeline family

The GPU encodes material resource indices into per-draw state, and a smaller number of generalized pipelines fetch material state dynamically.

CPU work becomes:

- bind deferred pipeline family
- submit opaque deferred stream
- bind forward masked pipeline family
- submit masked stream
- bind transparent family
- submit transparent stream

This is the long-term ideal because it minimizes pipeline rebinding and maximizes GPU autonomy.

### Model C. ExecuteIndirect-style state changes

Not currently portable in the engine’s abstraction and not a practical first target for OpenGL/Vulkan parity.

Not recommended as the primary design target.

## 10.3 Recommended submission model

Adopt Model A first, then migrate toward Model B where material systems allow it.

In practice:

- partition draws by pass and state class on GPU
- keep a fixed table of submission streams per pass
- CPU binds a known program/pipeline family for each stream
- CPU issues count-based multi-draw without reading the count

This removes CPU dependence on GPU-produced batch metadata while keeping pipeline-state management tractable.

---

## 11. Pass Graph and Render Graph Integration

The engine already has a render graph infrastructure in `XREngine.Runtime.Rendering/RenderGraph/` including `RenderPassMetadata`, `RenderPassBuilder`, `ERenderGraphPassStage` (Graphics/Compute/Transfer), `RenderGraphSynchronization`, and `RenderPassMetadataCollection`. The `DefaultRenderPipeline` already registers passes with metadata. The work here is to **harden and extend** this system, not build it from scratch.

Each GPU-driven pass must be expressible in the render graph as explicit compute/graphics stages.

### Example opaque deferred pass chain

1. `ResetGpuDrivenOpaqueDeferred`
   - writes counters, worklists, indirect counts
2. `UpdateSkinnedBounds`
   - reads bone matrices and writes bounds for animated draws
3. `CullOpaqueDeferred`
   - reads metadata/transforms/bounds/camera
   - writes visible IDs
4. `SortOpaqueDeferred`
   - reads visible IDs
   - writes sorted IDs and sort keys
5. `BuildIndirectOpaqueDeferred`
   - reads sorted IDs and mesh/material metadata
   - writes indirect command stream and count buffer(s)
6. `DrawOpaqueDeferred`
   - reads indirect command stream and count buffer(s)
   - writes GBuffer

Render-graph metadata must declare:

- all buffers read/written
- synchronization edges
- whether resources are transient or persistent

---

## 12. Dirty Update Model

## 12.1 Stable IDs

The architecture depends on stable identifiers:

- `DrawID`
- `TransformID`
- `SkinID`
- `MaterialID`
- `MeshID`

These IDs must remain stable across frames unless the object is destroyed.

## 12.2 Dirty tracking

Maintain CPU-side dirty sets for:

- transforms
- previous transforms
- bone ranges
- material parameter blocks
- local bounds metadata when authoring changes occur

The upload system should merge dirty IDs into contiguous upload ranges where possible.

## 12.3 Buffering strategy

Recommended:

- persistent GPU-resident storage for scene data
- per-frame staging/upload buffers or ring allocators
- explicit copy/upload commands into persistent buffers

Avoid:

- mapping large persistent scene buffers for CPU reads in shipping path
- rewriting the entire command table every frame when only a few transforms changed

---

## 13. Animation and Skinning Strategy

## 13.1 Model matrices

Rigid draws consume `TransformBuffer[TransformID]` in the vertex shader or in the culling/build kernels.

## 13.2 Bone matrices

Skinned draws consume:

- `SkinningPaletteBuffer` range referenced by `SkinID` or palette start/count

## 13.3 Previous-frame animation state

For motion vectors and temporal reconstruction, skinned paths need previous-frame animation state. Options:

- previous bone matrices
- previous skinned object transform plus current/previous palette

Recommended:

- keep previous bone matrices for skinned draws that write motion vectors
- allow opt-out for passes that do not require temporal data

## 13.4 Skinned bounds and culling

The current engine has CPU-side skinned bounds logic and a GPU skinned bounds calculator (`SkinnedMeshBoundsCalculator`). However, the GPU path **still reads results back to CPU**: it dispatches a compute shader, calls `WaitForGpu()`, then maps the output buffer and reads `Vector3[]` positions plus AABB bounds back to CPU memory. This is itself a GPU→CPU synchronization point and must be eliminated.

The target design moves skinned bounds computation fully into the GPU path used for culling, with bounds remaining GPU-resident:

- no CPU `EnsureSkinnedBounds()` dependency in the shipping path
- no GPU→CPU readback of skinned vertex positions or bounds
- no `WaitForGpu()` synchronization for bounds computation
- no CPU throttling interval controlling correctness of animated visibility
- no CPU BVH/bounds refresh blocking render submission

The GPU skinned-bounds compute shader already exists; the change is to write its output into `BoundsBuffer` for direct consumption by the culling kernel instead of reading it back to CPU.

---

## 14. Materials, State Classes, and Shader Variants

## 14.1 Separate material identity from state class

For GPU-driven submission, the sorting/batching key should prefer a compact `StateClassID` instead of raw material ID.

`StateClassID` groups draws that share:

- pipeline family
- render state bits
- descriptor layout requirements
- shader variant class

`MaterialID` still indexes textures/constants.

This allows many materials to be submitted under one fixed pipeline family without CPU inspection.

## 14.2 Descriptor strategy

Preferred long-term direction:

- material table in GPU memory
- textures and samplers accessed through descriptor indices or bindless handles
- per-draw material index fetch in shaders

This removes the need for CPU per-batch texture binding in the shipping path.

---

## 15. Debugging and Diagnostics Model

Zero-readback must apply to the shipping path, not necessarily to tooling.

Recommended modes:

### Shipping mode

- no CPU readbacks from GPU-driven intermediate buffers
- no per-pass mapped-buffer inspection
- no debug logging that forces synchronization

### Debug mode

- optional explicit readback passes
- optional debug gather buffers copied after frame completion
- optional validation-only CPU parity path

Critical rule:

- debug mode must be opt-in and visually labeled, not silently enabled in unit test or editor startup defaults.

---

## 16. Profiling Requirements

The profiler must separate:

- CPU time spent enqueueing fixed GPU-driven passes
- GPU compute time for culling/sorting/building
- GPU graphics time for indirect draw execution
- optional debug/readback time when diagnostics are enabled

Recommended GPU profiler breakdown:

- `GpuDriven.Reset`
- `GpuDriven.SkinnedBounds`
- `GpuDriven.Cull`
- `GpuDriven.Sort`
- `GpuDriven.BuildIndirect`
- `GpuDriven.DrawOpaqueDeferred`
- `GpuDriven.DrawMaskedForward`
- `GpuDriven.DrawTransparentForward`

Do not attribute CPU synchronization cost to generic `RenderMeshesPass` once the path is decomposed.

---

## 17. Migration Plan

## Phase 0. Stop accidental regressions

- keep GPU indirect debug logging off by default
- keep unit-test startup from forcing validation/readback-heavy settings
- keep readback diagnostics explicitly opt-in

## Phase 1. Remove shipping-path readbacks already identified

- ~~remove world-matrix readbacks from indirect submission~~ (done)
- stop reading batch ranges on CPU in the shipping path (`ReadGpuBatchRanges`)
- stop reading GPU-produced draw/instance counts for submission decisions on CPU (`UpdateVisibleCountersFromBuffer`, `ReadUIntAt` in Core.cs)
- remove `CoalesceContiguousBatches()` dependency on CPU-read batch data (move coalescing into GPU batch builder or eliminate it)

## Phase 2. Introduce state-class partitioned GPU submission streams

- add `StateClassID` (no equivalent exists today — current batching uses raw `MaterialID`)
- move transparency domain classification counts into GPU-side stream partitioning (currently read back as 4 uints in `ClassifyTransparencyDomains`)
- move per-view draw counts into GPU-resident per-view streams (currently read back in `ViewSet.cs`)
- move occlusion input counts into GPU-resident buffers (currently read back in `Occlusion.cs`)
- build one indirect stream per pass/state class on GPU
- switch CPU submission from dynamic per-batch iteration to fixed stream submission

## Phase 3. Refactor scene data layout

- extend the existing Hot/Cold decomposition (`GPUIndirectRenderCommandHot`/`Cold`) into the full target stream set: metadata, transform, previous-transform, bounds, and skinning streams
- the existing `GPURenderCullingSoA.comp` and `GPURenderExtractSoA.comp` shaders provide the starting point — generalize them for the new buffer layout
- add stable IDs and dirty-range upload plumbing
- minimize full-buffer rewrites during swap

## Phase 4. Move skinned bounds fully onto GPU

- refactor `SkinnedMeshBoundsCalculator` to write output into `BoundsBuffer` instead of reading back to CPU
- remove `WaitForGpu()` synchronization and mapped-buffer reads from the skinned bounds path
- correctness-first GPU bounds path (Option A: per-vertex reduction, staying GPU-resident)
- then optimized cluster/bone-bounds path (Option B)

## Phase 5. Bindless/material-table modernization

- reduce CPU-side pipeline/material churn
- collapse state classes where possible

## Phase 6. Render-graph hardening and telemetry

- harden existing render graph infrastructure (`RenderPassMetadata`, `RenderPassBuilder`, etc.) with explicit pass/resource metadata for every GPU-driven stage
- profiler split by stage
- validation path only in opt-in debug mode

---

## 18. Backend and Platform Notes

## OpenGL

- Must rely on count-based indirect draws and careful barrier discipline.
- Debug readback paths can be disproportionately expensive due to mapping and synchronization.
- Persistent mapped write paths are acceptable; read-mapped shipping paths are not.

## Vulkan

- Better fit for explicit GPU-driven submission.
- Render graph and barrier planner should become the canonical synchronization authority.
- Count-based indirect submission maps naturally to the target model.

## Windows/.NET constraints

- Dirty-range tracking should avoid excessive per-frame allocations.
- The engine’s hot-path allocation discipline applies: dirty lists, upload packets, and submission stream tables should be pooled or preallocated.

---

## 19. Risks and Tradeoffs

## 19.1 Complexity moves from CPU control flow into GPU data design

True zero-readback is not just “delete readbacks.” It requires a deliberate GPU data model and submission model.

## 19.2 Skinned bounds are the hardest correctness problem

Rigid object culling is straightforward. Animated bounds are not. The migration must not regress animated visibility.

## 19.3 Material/state diversity can block full autonomy

If every material demands unique CPU binding behavior, the renderer cannot become truly GPU-driven. State-class normalization is therefore a first-class design requirement, not an optimization.

## 19.4 Debuggability can regress if diagnostics are not redesigned

Removing readbacks from the shipping path is correct, but the engine still needs an intentional debug path for inspecting GPU-produced visibility and batch state.

---

## 20. Validation Strategy

Validation should proceed in layers:

1. Data-model correctness tests
   - stable ID mapping
   - dirty-range uploads
   - transform and previous-transform rotation
2. GPU parity tests
   - rigid visibility parity against CPU reference
   - animated visibility parity against CPU reference in debug mode
3. Submission tests
   - zero-readback path renders with count-based indirect streams only
   - no mapped GPU reads in shipping configuration
4. Performance tests
   - frame time reduction in `RenderMeshesPass`
   - reduced mapped-buffer counts
   - reduced CPU frame time variance

Success criteria:

- no CPU readback in the shipping GPU path
- no material-batch CPU loop driven by GPU-produced range data
- animated meshes remain correctly visible
- profiler clearly attributes GPU-driven work by stage

---

## 21. Concrete Code Areas Affected

Primary files and systems likely touched:

- `XRENGINE/Rendering/Commands/GPUScene.cs`
- `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.*.cs`
- `XRENGINE/Rendering/HybridRenderingManager.cs`
- `Build/CommonAssets/Shaders/Compute/Culling/*`
- `Build/CommonAssets/Shaders/Compute/Indirect/*`
- `XRENGINE/Rendering/Pipelines/RenderPipelineGpuProfiler.cs`
- `XREngine.Profiler.UI/ProfilerPanelRenderer.cs`
- render-graph metadata and pass declaration paths under `Rendering/Pipelines/Commands/`

---

## 22. Recommended Immediate Next Steps

1. Lock the shipping rule that GPU-driven rendering may not read GPU-produced batch/count data on CPU.
2. Introduce `StateClassID` and fixed indirect submission streams per pass.
3. Refactor scene command storage into stable metadata plus dirty transform/bone streams.
4. Define the first GPU-native skinned bounds path and get animated visibility correctness under tests.
5. Split profiler timings so `RenderMeshesPass` no longer conflates GPU work and CPU synchronization.

---

## 23. Final Architecture Statement

The final GPU-driven renderer should behave like this:

- CPU uploads only dirty scene state and camera state.
- GPU derives visibility, order, and draw structure.
- CPU submits fixed pass streams without observing GPU-generated intermediate results.
- GPU executes all rendering from its own generated command streams.

That is the standard required to call the path truly GPU-driven.