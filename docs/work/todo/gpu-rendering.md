# GPU-Driven Rendering Pipeline — Zero-Readback Architecture TODO

Last Updated: 2026-03-22
Status: Active development — core pipeline functional, LOD system and zero-readback completion remain.

## Executive Summary

The GPU-driven rendering pipeline must achieve **zero CPU readbacks** in the shipping render path. All culling (BVH traversal, frustum, occlusion), LOD selection, sort/batch generation, and draw dispatch must execute entirely on the GPU. The CPU's role is scene ingest (command add/remove/update via subdata) and issuing a fixed sequence of compute dispatches followed by `MultiDrawElementsIndirectCount` — nothing more.

Two rendering paths are supported:

1. **Traditional indirect multi-draw** — GPU builds `DrawElementsIndirectCommand` arrays, CPU issues `MultiDrawElementsIndirectCount` per material state group.
2. **Meshlet-based mesh shaders** — GPU task shaders cull meshlets, mesh shaders emit triangles. No indirect draw buffer needed.

Both paths share the same scene representation (`GPUScene`), BVH, and culling infrastructure.

---

## Table of Contents

1. [Current Pipeline Architecture (Audit Snapshot)](#current-pipeline-architecture)
2. [CPU Readback Audit](#cpu-readback-audit)
3. [LOD System Audit](#lod-system-audit)
4. [Meshlet System Audit](#meshlet-system-audit)
5. [Target Architecture](#target-architecture)
   - [Tiered Mesh Atlas Architecture](#tiered-mesh-atlas-architecture)
   - [Zero-Readback Draw Dispatch](#zero-readback-draw-dispatch-architecture)
   - [LOD Atlas Architecture](#lod-atlas-architecture)
   - [Meshlet Integration](#meshlet-integration-architecture)
6. [Phase Plan](#phase-plan)
7. [Completed Phases (Summary)](#completed-phases)
8. [Test Backlog](#test-backlog)

---

## Current Pipeline Architecture

### Pipeline Flow (as-built)

```
┌───────────────────────────────────────────────────────────────────┐
│ CPU: GPUScene — command add/remove/update (subdata to GPU)        │
│   192-byte GPUIndirectRenderCommand per renderable                │
│   (WorldMatrix, PrevWorldMatrix, BoundingSphere, MeshID,          │
│    SubmeshID, MaterialID, InstanceCount, RenderPass,              │
│    ShaderProgramID, RenderDistance, LayerMask, LODLevel, Flags)   │
└───────────────────────┬───────────────────────────────────────────┘
                        │ PushSubData
           ┌────────────▼─────────────┐
           │ bvh_aabb_from_commands   │  Sphere → AABB extraction
           └────────────┬─────────────┘
           ┌────────────▼─────────────┐
           │ bvh_build (4-stage LBVH) │  Morton sort → leaf → internal → parent → root
           └────────────┬─────────────┘
           ┌────────────▼─────────────┐
           │ bvh_refit (dynamic)      │  Bottom-up bounds propagation with atomics
           └────────────┬─────────────┘
           ┌────────────▼─────────────┐
           │ bvh_sah_refine (opt.)    │  SAH cost refinement for shallow nodes
           └────────────┬─────────────┘
           ┌────────────▼──────────────────────────────────┐
           │ bvh_frustum_cull                              │
           │   Stack-based DFS, plane slab tests           │
           │   Distance rejection, per-view append         │
           │   Atomic counters → CulledCount buffer        │
           └────────────┬──────────────────────────────────┘
           ┌────────────▼─────────────┐
           │ GPURenderBuildKeys       │  Encode (pass|shader|state|material) sort keys
           └────────────┬─────────────┘
           ┌────────────▼──────────────────────────────────┐
           │ GPURenderRadixIndexSort (4 LSB passes)        │
           │   Phase 0: histogram, Phase 1: prefix scan,   │
           │   Phase 2: scatter (×4 bytes)                 │
           └────────────┬──────────────────────────────────┘
           ┌────────────▼──────────────────────────────────┐
           │ GPURenderBuildBatches (single WG 1×1×1)       │
           │   Detect material boundaries                  │
           │   Emit DrawElementsIndirectCommand per batch   │
           │   Write BatchRangeBuffer + BatchCountBuffer   │
           │   Write InstanceTransformBuffer               │
           └────────────┬──────────────────────────────────┘
                        │
           ┌────────────▼──────────────────────────────────┐
           │ *** CPU READBACK BARRIER ***                   │
           │   ReadGpuBatchRanges(): MapBufferData on       │
           │     BatchRangeBuffer → List<DrawBatch>        │
           │   ReadUIntAt: transparency domain counts       │
           │   ReadUIntAt: visible command counts           │
           └────────────┬──────────────────────────────────┘
                        │
           ┌────────────▼──────────────────────────────────┐
           │ CPU: foreach (batch in batches)               │
           │   Resolve material from batch.MaterialID      │
           │   Bind shader program + uniforms              │
           │   MultiDrawElementsIndirectWithOffset(        │
           │     count=batch.Count,                        │
           │     offset=batch.Offset * stride)             │
           └───────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | File(s) | Role |
|-----------|---------|------|
| **GPUScene** | `XREngine/Rendering/Commands/GPUScene.cs` | Owns command buffers, mesh atlas (positions, normals, tangents, UV0, indices), mesh metadata buffer, BVH tree buffer. Append/remove with ref counting, incremental subdata updates. |
| **GPURenderPassCollection** | `XREngine/Rendering/Commands/GPURenderPassCollection.*.cs` (7 partials: Core, CullingAndSoA, IndirectAndMaterials, Occlusion, ShadersAndInit, Sorting, ViewSet) | Culling dispatch, sort key generation, radix sort, batch building, indirect command generation, Hi-Z occlusion, ViewSet management. |
| **HybridRenderingManager** | `XREngine/Rendering/HybridRenderingManager.cs` | Path selection (meshlet vs traditional), per-batch material binding, draw submission via `MultiDrawElementsIndirect[Count]`. |

### Mesh Atlas

Single scene-level atlas VAO with interleaved attribute streams:

- `binding=0` Positions (vec3)
- `binding=1` Normals (vec3)
- `binding=2` Tangents (vec4)
- `binding=3` UV0 (vec2)
- Element buffer: u32 indices

Meshes are appended incrementally with ref counting. Power-of-2 growth, `PushSubData` to GPU. `MeshDataBuffer` stores per-mesh metadata: `uint4 [IndexCount, FirstIndex, BaseVertex, Flags]`.

### Compute Shader Inventory (Indirect Pipeline)

| Shader | Purpose | Dispatch |
|--------|---------|----------|
| `GPURenderResetCounters.comp` | Zero atomic counters | 1×1×1 |
| `bvh_aabb_from_commands.comp` | Sphere→AABB extraction | N commands |
| `bvh_build.comp` | 4-stage LBVH construction | N commands |
| `bvh_refit.comp` | Bottom-up bounds propagation | N commands |
| `bvh_sah_refine.comp` | Shallow node SAH refinement | conditional |
| `bvh_frustum_cull.comp` | Stack-based BVH traversal + frustum | N commands |
| `GPURenderCulling.comp` | Flat frustum cull (non-BVH) | N commands |
| `GPURenderCullingSoA.comp` | SoA variant of frustum cull | N commands |
| `GPURenderHiZSoACulling.comp` | Hi-Z occlusion + frustum | N commands |
| `GPURenderBuildKeys.comp` | Sort key extraction | N visible |
| `GPURenderRadixIndexSort.comp` | 4-pass LSD radix sort | N visible |
| `GPURenderBuildBatches.comp` | Batch boundary detection + indirect command emit | 1×1×1 |
| `GPURenderBuildHotCommands.comp` | Optional SoA compaction | N visible |
| `GPURenderCopyCount3.comp` | Copy count for parameter buffer | 1×1×1 |
| `GPURenderCopyCommands.comp` | Command staging copy | N commands |
| `GPURenderClassifyTransparencyDomains.comp` | Classify opaque/masked/transparent | N visible |

### Draw Submission (Current)

`HybridRenderingManager.RenderTraditionalBatched()`:

1. Reads batch ranges from GPU → `List<DrawBatch>` (CPU readback)
2. Coalesces contiguous same-material batches
3. For each batch: resolves `XRMaterial`, binds shader program, sets uniforms, calls `MultiDrawElementsIndirectWithOffset`
4. Supports `MultiDrawElementsIndirectCount` via `GL_ARB_indirect_parameters` / `VK_KHR_draw_indirect_count` when batch ranges are NOT used

### Extension Support

| Extension | Status | Usage |
|-----------|--------|-------|
| `GL_ARB_indirect_parameters` | Active | `MultiDrawElementsIndirectCount` — GPU-sourced draw count |
| `GL_NV_mesh_shader` | Scaffolded | Task/mesh shader dispatch (not wired into pipeline) |
| `GL_ARB_buffer_storage` | Active | Persistent/coherent buffer mapping |
| `VK_KHR_draw_indirect_count` | Active | Vulkan equivalent of indirect parameters |

---

## CPU Readback Audit

Every site where the CPU reads data back from the GPU in the rendering hot path.

### Critical Path Readbacks (Always Executed)

| # | Location | Buffer | Data | Bytes/Frame | Purpose | Eliminable? |
|---|----------|--------|------|-------------|---------|-------------|
| 1 | `GPURenderPassCollection.CullingAndSoA.cs:334` | `_culledCountBuffer` | 2×uint (draws, instances) | 8 B | Determines dispatch sizes for downstream stages | **Yes** — use `CommandCapacity` as upper bound (already gated by `IsCpuReadbackCountDisabledForPass`) |
| 2 | `GPURenderPassCollection.IndirectAndMaterials.cs:440-443` | `_transparencyDomainCountBuffer` | 4×uint (opaque, masked, approx, exact) | 16 B | Routes geometry into separate transparency passes | **Yes** — split into 3 separate `MultiDrawElementsIndirectCount` calls with per-domain count buffers |
| 3 | `GPURenderPassCollection.IndirectAndMaterials.cs:459` | `_gpuBatchCountBuffer` | 1×uint | 4 B | Number of material batches | **Yes** — move to per-material `MultiDrawElementsIndirectCount` |
| 4 | `GPURenderPassCollection.IndirectAndMaterials.cs:474` | `_gpuBatchRangeBuffer` | N × `GPUBatchRangeEntry` (offset, count, materialID) | ~1.2 KB | Batch boundary info for CPU material loop | **Yes** — THE critical bottleneck. Requires architectural change. |
| 5 | `GPURenderPassCollection.Occlusion.cs:649` | `_culledCountBuffer` | 1×uint | 4 B | Hi-Z candidate count for next stage | **Yes** — use parameter buffer path |

### Conditional/Debug Readbacks

| # | Location | Buffer | Gate | Purpose |
|---|----------|--------|------|---------|
| 6 | `GPUScene.cs:879` | `_meshDataBuffer` | Fallback path | GetDataArrayRawAtIndex for mesh entry reads |
| 7 | `GPUScene.cs:1290` | Command buffer | Debug validation (budget=8) | Roundtrip command validation |
| 8 | `GPUScene.cs:1559/1589` | `_meshDataBuffer` | Remove/update path | Mesh data entry reads for ref count management |
| 9 | `HybridRenderingManager.cs:515-530` | Parameter buffer | Count path unavailable | Fallback draw count |
| 10 | Various `_statsBuffer` reads | Stats buffer | Debug/diagnostic flags | Performance counters |
| 11 | Various overflow flag reads | Overflow buffers | Diagnostic mode | Buffer overflow detection |

### Correctly Async Readbacks (Not Hot Path)

| Location | Buffer | Mechanism |
|----------|--------|-----------|
| `GPUPhysicsChainDispatcher.cs:1156-1181` | Physics particles | `FenceSync` + `ClientWaitSync` double-buffered |
| `BvhRaycastDispatcher.cs:229-254` | Raycast hits | Persistent `GL_ARB_buffer_storage` + async fence |

### The Core Problem

**Readback #4 is the architectural bottleneck.** The CPU reads back `GPUBatchRangeEntry` structs (materialID + offset + count) so it can iterate batches and bind the correct material/shader per batch before issuing `MultiDrawElementsIndirect`. This creates a full GPU→CPU sync point every frame.

The reason this exists: different materials require different shader programs and render state, and OpenGL/Vulkan have no mechanism to switch shader programs mid-draw-call. Each material boundary requires a separate draw call with the correct program bound.

---

## LOD System Audit

### What Exists

| Component | Status | Details |
|-----------|--------|---------|
| `SubMeshLOD` class | Exists | `XRMesh`, `XRMaterial`, `MaxVisibleDistance`, `GenerateAsync` |
| `SubMesh.LODs` | Exists | `SortedSet<SubMeshLOD>` sorted by distance |
| `GPUIndirectRenderCommand.LODLevel` field | Exists | uint at offset 176, always set to 0 |
| `GPUIndirectRenderFlags.LODEnabled` flag | Exists | Bit 15, never set |
| `GPUIndirectRenderCommandHot.LODLevel` | Exists | Propagated in Hot/Cold split |
| LOD selection unit tests | Exist | `LodSelection_NearDistance_HighestLod` etc. in `GpuIndirectRenderDispatchTests.cs` |
| `TerrainLOD.comp` | Exists | Distance-based terrain chunk LOD (separate system) |

### What Does NOT Exist

- **GPU-side LOD selection compute shader** — no shader reads `RenderDistance` and selects LOD level
- **Per-LOD mesh atlas entries** — all LODs for a mesh are not tracked in the atlas; only the single active mesh is loaded
- **LOD distance thresholds in GPU buffer** — no per-command LOD distance array on GPU
- **Dynamic LOD atlas residency** — no mechanism to load/unload LOD meshes from the atlas at runtime
- **LOD transition smoothing** — no dithered or morphed LOD transitions
- **Meshlet LOD** — no meshlet-level LOD (e.g., nanite-style cluster group merging)

### Gap Summary

The LOD infrastructure is CPU-side scaffolding only. The GPU pipeline renders exactly one mesh per command at LOD 0. There is no mechanism for the GPU to select a different LOD or for the atlas to contain multiple LODs per mesh that can be swapped dynamically.

---

## Meshlet System Audit

### What Exists

| Component | Status | Details |
|-----------|--------|---------|
| `Meshlet` struct (56 bytes) | Complete | Bounding sphere, vertex/triangle offset/count, mesh ID, material ID |
| `MeshletCollection` | Complete | SSBO management, task/mesh shader loading, `DrawMeshTask` dispatch |
| `MeshletGenerator` | Complete | Uses meshoptimizer via P/Invoke (64 verts, 124 tris default) |
| `MeshletMaterial` (48 bytes) | Complete | PBR material struct for meshlet SSBO |
| `MeshletCulling.task` shader | Complete | Per-meshlet frustum culling, atomic visibility counter |
| `MeshletRender.mesh` shader | Complete | Cooperative vertex/triangle output, MVP transform |
| `MeshletShading.fs` shader | Complete | PBR shading with directional light |
| `VPRC_RenderMeshesPassMeshlet` | **STUBBED** | Logs warning, falls back to traditional path |
| `HasMeshShaderExt` capability flag | Complete | Runtime detection of `GL_NV_mesh_shader` |

### What Does NOT Exist

- **Meshlet rendering integration** — `VPRC_RenderMeshesPassMeshlet` is a stub, no actual meshlet rendering occurs in the pipeline
- **Meshlet occlusion culling** — task shader does frustum only, no Hi-Z or per-meshlet occlusion
- **Meshlet LOD** — no cluster group / DAG-based LOD for meshlets
- **Meshlet BVH** — meshlet culling uses flat iteration, not BVH traversal
- **Vulkan mesh shaders** — commented out / WIP
- **`GL_EXT_mesh_shader`** — only NV extension is used; no EXT/KHR path

---

## Target Architecture

### Design Principles

1. **Zero CPU readbacks in shipping mode.** The CPU never reads GPU buffer contents during rendering. All data flows CPU→GPU only.
2. **Tiered mesh atlas.** Three atlas tiers — Static (write-once, match-lifetime), Dynamic (load/unload on demand), and Streaming (real-time per-vertex writes) — serve the full spectrum of mesh lifetime patterns without one-size-fits-all compromises.
3. **Dynamic LOD atlas.** Multiple LOD meshes per object are resident in the atlas (potentially spanning tiers). LOD selection happens on GPU based on screen-space size or distance.
4. **GPU-driven everything.** BVH build/refit, frustum+occlusion culling, LOD selection, sort, batch, and indirect command generation are all compute dispatches.
5. **Dual render path.** Traditional indirect multi-draw and meshlet mesh shaders share scene data and culling. The meshlet path is preferred when hardware supports it.
6. **Material state changes are finite and pre-bound.** The CPU pre-binds all distinct material programs before the frame. Draw dispatch uses per-material indirect command lists, eliminating the need for batch-range readback.

### Tiered Mesh Atlas Architecture

The current system uses a single monolithic mesh atlas. This is insufficient — different geometry has fundamentally different lifetime and mutation patterns. The atlas is split into **three tiers**, each backed by its own set of attribute + index buffers but sharing the same vertex format and binding layout so a single VAO can multiplex across them.

| Tier | Name | Lifetime | Mutation | Buffer Strategy | Use Case |
|------|------|----------|----------|-----------------|----------|
| **0** | **Static** | Scene/match lifetime | Never (write-once at load) | `GL_STATIC_DRAW` / `VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT` only. Immutable after upload. | World geometry, map architecture, static props, skybox meshes — anything that stays resident for the duration of a level/match. |
| **1** | **Dynamic** | On-demand | Rare (load/unload, never per-vertex edit) | `GL_DYNAMIC_DRAW` with ref-counted append/remove, power-of-2 growth, defragmentation pass on threshold. | Spawned/despawned objects, pickups, projectiles, characters entering/leaving, LOD meshes streamed in/out. |
| **2** | **Streaming** | Per-frame or continuous | Frequent per-vertex writes | `GL_STREAM_DRAW` or persistent coherent mapping (`GL_MAP_WRITE_BIT | GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT`). Triple-buffered to avoid pipeline stalls. | Real-time modeling tools, procedural mesh generation, cloth/softbody CPU output, deformable terrain sculpting. |

#### Design Rules

1. **Each tier is a separate set of attribute buffers + element buffer**, but all three share the same attribute layout (positions, normals, tangents, UV0, indices). They register into the same `MeshDataBuffer` with a tier tag so compute shaders consume all tiers uniformly.
2. **Commands reference mesh data entries regardless of tier.** The `MeshID` in `GPUIndirectRenderCommand` indexes into the unified `MeshDataBuffer`; the entry's `BaseVertex` and `FirstIndex` resolve into the correct tier's buffers. The draw call binds the appropriate tier's VAO (or sub-range of a combined VAO).
3. **Static tier is filled once during scene load** and is never touched again. No `PushSubData`, no resize, no defrag. This is the common case for most geometry in a shipped game and should be the fastest path — the driver can place it in device-local VRAM with no staging overhead after the initial upload.
4. **Dynamic tier uses the existing append/remove + ref-counting system** (current `GPUScene` atlas behavior). It grows via power-of-2 reallocation and uses `PushSubData` for incremental writes. A periodic defragmentation pass compacts holes left by removed meshes (copy surviving entries and update `MeshDataBuffer` offsets).
5. **Streaming tier is triple-buffered** with persistent coherent mapping. Frame N writes to buffer slot `N % 3`; the GPU reads from slot `(N - 2) % 3`.  The CPU writes vertices directly into the mapped pointer — no staging copies, no `PushSubData`. Meshes in this tier have a fixed maximum vertex/index count declared at registration; they cannot grow.
6. **BVH and culling are tier-agnostic.** All commands, regardless of which tier holds their mesh data, participate in the same BVH build/refit/cull pipeline. The only per-tier distinction is buffer binding at draw time.
7. **LOD entries can span tiers.** A static mesh's LOD 0 might live in the static tier (always resident), while its LOD 2 lives in the dynamic tier (streamed in only when needed at that distance). The `LODTableBuffer` records per-LOD mesh data IDs that may point into different tiers.

#### Per-Tier VAO Binding at Draw Time

Since all tiers share attribute format, the three VAOs differ only in which buffers are bound. During the material-scatter draw loop, the per-material indirect commands are further partitioned by tier (a 2-bit tier tag in `MeshDataBuffer.Flags`). The GPU scatter shader writes commands into `PerMaterial × PerTier` buckets, giving the CPU a fixed `Materials × 3` iteration:

```
foreach material in ActiveMaterials:
    bind material program + state
    foreach tier in [Static, Dynamic, Streaming]:
        bind tier's VAO
        bind (material, tier) indirect buffer segment
        bind (material, tier) count buffer as parameter
        MultiDrawElementsIndirectCount(maxDraws)
```

The inner tier loop is a constant 3 iterations — no GPU readback needed. Empty buckets (count = 0 on GPU) are free due to `MultiDrawElementsIndirectCount` reading 0 and issuing no draws.

#### Migration Between Tiers

Meshes can be promoted or demoted between tiers at runtime:

- **Dynamic → Static:** When a level finishes loading and geometry is known to be permanent, bulk-copy from dynamic to static tier buffers, update `MeshDataBuffer` entries, release dynamic slots. This is an offline operation (loading screen / async).
- **Dynamic → Streaming:** When a mesh becomes editable (e.g., user enters modeling mode), allocate a streaming slot, copy current geometry, update `MeshDataBuffer`, release dynamic slot.
- **Streaming → Dynamic:** When editing ends, copy final geometry to dynamic tier, release streaming slot.

Migration is always a copy + remap + release, never an in-place mutation. This keeps each tier's invariants (immutable, ref-counted, or triple-buffered) intact.

### Zero-Readback Draw Dispatch Architecture

The fundamental change: instead of one sorted indirect buffer with GPU-generated batch ranges read back to CPU, use **N per-material indirect buffers** with GPU-written draw counts. The CPU iterates a known list of materials (not read from GPU) and issues one `MultiDrawElementsIndirectCount` per material.

```
┌─────────────────────────────────────────────────────────────────┐
│ GPU COMPUTE PIPELINE (all dispatches, no readbacks)             │
│                                                                 │
│  Commands[N] ─→ BVH Build ─→ BVH Frustum Cull ─→ Hi-Z Cull   │
│                                                    │            │
│                                        CulledCommands[visible]  │
│                                                    │            │
│                              ┌─────────────────────▼──────────┐ │
│                              │ LOD Select (GPU compute)       │ │
│                              │ Per-command: distance → LOD    │ │
│                              │ Update MeshID/atlas offsets    │ │
│                              └─────────────────────┬──────────┘ │
│                                                    │            │
│                              ┌─────────────────────▼──────────┐ │
│                              │ Material Scatter (GPU compute) │ │
│                              │ Scatter visible commands into  │ │
│                              │ per-material indirect buffers  │ │
│                              │ Each buffer has own count      │ │
│                              └────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                         │
           ┌─────────────▼───────────────────────────────────┐
           │ CPU: foreach (material in scene.ActiveMaterials) │
           │   Bind material program + state                  │
           │   Bind material's IndirectBuffer                 │
           │   Bind material's CountBuffer as parameter       │
           │   MultiDrawElementsIndirectCount(maxDraws)       │
           │   // GPU reads actual count from CountBuffer     │
           └─────────────────────────────────────────────────┘
```

**Key insight:** The CPU knows which materials exist (it registered them). It does NOT need to know how many draws each material has — `MultiDrawElementsIndirectCount` reads that from the GPU parameter buffer. The GPU scatter shader writes commands directly into per-material buffers and atomically increments per-material counts.

### LOD Atlas Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ MESH ATLAS (single VAO, all LODs resident)                      │
│                                                                 │
│  ┌──────────┬──────────┬──────────┬──────────┬─────────────┐   │
│  │ Mesh A   │ Mesh A   │ Mesh A   │ Mesh B   │ Mesh B      │   │
│  │ LOD 0    │ LOD 1    │ LOD 2    │ LOD 0    │ LOD 1       │   │
│  │ 10K tri  │ 2K tri   │ 500 tri  │ 5K tri   │ 1K tri      │   │
│  └──────────┴──────────┴──────────┴──────────┴─────────────┘   │
│                                                                 │
│  MeshDataBuffer entry per LOD:                                  │
│    { IndexCount, FirstIndex, BaseVertex, Flags }                │
│                                                                 │
│  LODTableBuffer per logical mesh:                               │
│    { LOD0_MeshDataID, LOD1_MeshDataID, LOD2_MeshDataID,        │
│      LOD0_MaxDist, LOD1_MaxDist, LOD2_MaxDist, LODCount }     │
└─────────────────────────────────────────────────────────────────┘
```

Each logical mesh has multiple entries in `MeshDataBuffer` (one per LOD). LOD entries may reside in different atlas tiers — e.g., LOD 0 in the Static tier (always resident), LOD 2 in the Dynamic tier (streamed in on demand). A new `LODTableBuffer` maps logical mesh ID → per-LOD mesh data IDs + distance thresholds. The GPU LOD selection shader reads camera distance and picks the right `MeshDataID`, writing it into the command's `MeshID` field before the indirect build stage. The tier tag in `MeshDataBuffer.Flags` ensures the draw loop binds the correct VAO for whichever tier the selected LOD lives in.

### Meshlet Integration Architecture

```
Scene Commands ─→ BVH Cull ─→ LOD Select ─→ ┬─ Traditional Path
                                              │    (indirect multi-draw)
                                              │
                                              └─ Meshlet Path
                                                   │
                                         ┌─────────▼──────────┐
                                         │ Meshlet Expansion   │
                                         │ (GPU compute)       │
                                         │ Expand visible cmds │
                                         │ into meshlet ranges │
                                         └─────────┬──────────┘
                                                   │
                                         ┌─────────▼──────────┐
                                         │ Task Shader         │
                                         │ Per-meshlet frustum │
                                         │ + occlusion cull    │
                                         └─────────┬──────────┘
                                                   │
                                         ┌─────────▼──────────┐
                                         │ Mesh Shader         │
                                         │ Cooperative vertex  │
                                         │ + triangle output   │
                                         └─────────────────────┘
```

The meshlet path reuses the same BVH cull and LOD selection. After LOD selection, instead of building `DrawElementsIndirectCommand` arrays, visible commands are expanded into meshlet ranges and dispatched via `DrawMeshTasksIndirectCount`.

---

## Phase Plan

### Phase 7 — Zero-Readback Material Dispatch

**Outcome:** Eliminate ALL remaining CPU readbacks from the shipping render path. The CPU iterates a known material list and issues `MultiDrawElementsIndirectCount` per material — it never reads GPU buffers.

#### 7A — Per-Material Indirect Buffers

- [x] Add `MaterialSlotRegistry` to `GPURenderPassCollection` that maps each active MaterialID → a slot index (0..M-1), maintained incrementally on material add/remove.
- [x] Allocate a `PerMaterialIndirectDrawBuffer` as a single large SSBO logically partitioned into M segments, each holding up to `MaxDrawsPerMaterial` `DrawElementsIndirectCommand` entries.
- [x] Allocate a `PerMaterialDrawCountBuffer` (M × uint) — the parameter buffer source for each material's `MultiDrawElementsIndirectCount` call.
- [x] Push `MaterialSlotRegistry` mapping to GPU as a uniform buffer or SSBO.

Primary files:

- `XREngine/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs`

#### 7B — GPU Material × Tier Scatter Shader

- [x] Write `GPURenderMaterialScatter.comp` that:
  - Reads the sorted visible command list and per-command `MaterialID`.
  - Looks up material slot index from the registry buffer.
  - Reads tier tag (2-bit) from `MeshDataBuffer[meshID].Flags` to determine atlas tier.
  - Computes bucket index: `slot * 3 + tier`.
  - Atomically increments `PerMaterialTierDrawCountBuffer[bucket]`.
  - Writes `DrawElementsIndirectCommand` into `PerMaterialTierIndirectDrawBuffer[bucket][count]` using atlas offsets from `MeshDataBuffer`.
- [x] Dispatch after sort, replacing `GPURenderBuildBatches.comp` for the zero-readback path.
- [x] Add barrier: `ShaderStorage | Command` after scatter.
- [x] Initially, until the tiered atlas (Phase 8) ships, all meshes are tier 0 (Static) — the scatter shader still works, the inner loop just always hits one tier.

Primary files:

- New: `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp`
- `XREngine/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`

#### 7C — Zero-Readback Draw Submission

- [x] Add `RenderZeroReadback()` method to `HybridRenderingManager` that:
  - Iterates `MaterialSlotRegistry` (CPU-known list, no GPU reads).
  - Per material × per tier (constant 3 iterations):
    - Binds the tier's VAO.
    - Binds the `(material, tier)` segment of `PerMaterialTierIndirectDrawBuffer` as `GL_DRAW_INDIRECT_BUFFER`.
    - Binds `PerMaterialTierDrawCountBuffer[slot * 3 + tier]` as `GL_PARAMETER_BUFFER`.
    - Calls `MultiDrawElementsIndirectCount(maxDrawsPerMaterialTier, stride, byteOffset)`.
    - Empty buckets (GPU count = 0) issue zero draws — no CPU check needed.
  - Skips materials with statically-known zero commands (materials not in scene).
- [x] Make this the default path when `IsCpuReadbackCountDisabledForPass()` is true.
- [x] Keep existing batch-readback path as debug/fallback only.

Primary files:

- `XREngine/Rendering/HybridRenderingManager.cs`

#### 7D — Eliminate Remaining Count Readbacks

- [x] Transparency domain counts: split into 3 per-domain `MultiDrawElementsIndirectCount` calls with per-domain count buffers written by GPU compute. Remove `ReadUIntAt` on `_transparencyDomainCountBuffer`.
- [x] Visible command count: use `CommandCapacity` as conservative upper bound for all downstream dispatches (already supported by `IsCpuReadbackCountDisabledForPass()`). Ensure this flag is ON by default in shipping mode.
- [x] Hi-Z candidate count: use parameter buffer path, no CPU readback.
- [x] Remove `ReadGpuBatchRanges()` from default path entirely.

Primary files:

- `XREngine/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XREngine/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine/Rendering/Commands/GPURenderPassCollection.Occlusion.cs`

#### 7E — GPU Mesh Data Entry for Remove/Update

- [x] Move mesh data entry lookups (`GPUScene.cs:1559/1589`) to CPU-side cache instead of GPU readback. `GPUScene` already tracks `_atlasMeshOffsets` — use this dictionary instead of reading `_meshDataBuffer` from GPU.
- [x] Remove `GetDataArrayRawAtIndex` fallback from hot path.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`

Acceptance criteria:

- Zero `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, or any GPU→CPU data read during the rendering hot path in shipping mode.
- `Engine.Rendering.Stats.GpuReadbackBytes` reports 0 for a full frame in shipping config.
- Debug/diagnostic readbacks remain available behind explicit flags.

---

### Phase 8 — Tiered Mesh Atlas

**Outcome:** The monolithic mesh atlas is replaced by three purpose-built tiers (Static, Dynamic, Streaming), each with buffer strategies tuned to their mutation patterns. All tiers share attribute format and feed into the same compute/cull/draw pipeline.

#### 8A — Atlas Tier Infrastructure

- [x] Add `EAtlasTier` enum: `Static = 0`, `Dynamic = 1`, `Streaming = 2`.
- [x] Split current `GPUScene` atlas buffers into three sets of `(positions, normals, tangents, uv0, indices)` buffers, one per tier.
- [x] Add a 2-bit tier tag to `MeshDataBuffer.Flags` so compute shaders and the scatter shader can identify which tier a mesh entry belongs to.
- [x] Create per-tier VAO binding support that reconfigures the shared indirect renderer against the tier's specific buffers at draw time.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`
- `XREngine/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs`

#### 8B — Static Tier (Write-Once)

- [x] Allocate static tier buffers with `GL_STATIC_DRAW` / `VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT` semantics.
- [x] Provide `LoadStaticMeshBatch(meshes[])` API that bulk-uploads static geometry during scene load.
- [x] Static meshes get `MeshDataBuffer` entries tagged as static and participate in BVH/culling normally.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`

#### 8C — Dynamic Tier (Load/Unload)

- [x] Migrate current atlas append/remove behavior to the dynamic tier. This remains the ref-counted, power-of-2 growth system.
- [x] Add compaction-based defragmentation by removing holes immediately on mesh removal and rewriting affected offsets in `MeshDataBuffer`.
- [x] Keep the dynamic tier as the default residency target for follow-on LOD work.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`

#### 8D — Streaming Tier (Real-Time Writes)

- [x] Allocate streaming tier buffers with persistent coherent mapping (`GL_MAP_WRITE_BIT | GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT`).
- [x] Triple-buffer the streaming tier with rotating write/render slots.
- [x] Provide `RegisterStreamingMesh(maxVertexCount, maxIndexCount)` that pre-allocates a fixed slot in the streaming tier.
- [x] Provide `TryGetStreamingWritePointers(meshID)` and `CommitStreamingMesh(...)` for direct writes into the current streaming slot.
- [x] On `UnregisterStreamingMesh`, release the slot for reuse.
- [x] Keep the API surface suitable for real-time modeling and procedural mesh update paths.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`
- `XREngine/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` (persistent mapping)

#### 8E — Tier Migration

- [x] Add `MigrateMesh(meshID, fromTier, toTier)` API that allocates in the target tier, updates `MeshDataBuffer`, and releases the source slot.
- [x] Add bulk migration via `PromoteDynamicToStatic(meshIDs[])`.
- [x] Preserve tier-tagged mesh metadata so follow-on LOD table work can target the migrated entries.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`

Acceptance criteria:

- Static tier meshes have zero tier-management CPU work after upload.
- Dynamic tier supports add/remove/compaction without corrupting other tiers.
- Streaming tier exposes direct mapped-write entry points through the active write slot.
- BVH/culling/sort/material-scatter/draw pipeline consumes tier-tagged mesh data across all three tiers.
- Tier tag in `MeshDataBuffer.Flags` is consumed by the scatter shader to route indirect commands to the correct tier binding.

---

### Phase 9 — Dynamic LOD Atlas

**Outcome:** Multiple LOD levels per mesh are tracked in the atlas (across tiers). GPU selects the active LOD per command per frame. LOD meshes can be loaded/unloaded from the dynamic tier on demand.

#### 9A — LOD Table Buffer

- [x] Add `LODTableEntry` struct:

  ```csharp
  [StructLayout(LayoutKind.Sequential)]
  public struct LODTableEntry
  {
      public uint LODCount;           // Number of LOD levels (1-N)
      public uint LOD0_MeshDataID;    // MeshDataBuffer index for LOD 0
      public uint LOD1_MeshDataID;
      public uint LOD2_MeshDataID;
      public uint LOD3_MeshDataID;    // Max 4 LODs initially
      public float LOD0_MaxDistance;
      public float LOD1_MaxDistance;
      public float LOD2_MaxDistance;
      public float LOD3_MaxDistance;
  }
  ```

- [x] Add `_lodTableBuffer` SSBO to `GPUScene`.
- [x] Map logical mesh ID → LOD table entry. When a mesh with LODs is registered, all LOD meshes are appended to the atlas and the LOD table is populated.
- [x] Add `LogicalMeshID` field to `GPUIndirectRenderCommand` (repurpose `Reserved0`).

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`
- `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`

#### 9B — GPU LOD Selection Shader

- [x] Write `GPURenderLODSelect.comp` that:
  - Per visible command: reads camera position, computes distance to bounding sphere center.
  - Looks up `LODTableEntry` from `_lodTableBuffer[LogicalMeshID]`.
  - Selects LOD level based on distance thresholds (or screen-space projected size for more accuracy).
  - Writes selected `MeshDataID` into the command's `MeshID` field in the culled command buffer.
  - Writes selected `LODLevel` into the command.
- [x] Dispatch after frustum/BVH cull, before sort/batch.
- [x] If `LODCount == 1` or `LODEnabled` flag is not set, skip (pass through existing MeshID).

Primary files:

- New: `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp`
- `XREngine/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`

#### 9C — Dynamic LOD Atlas Residency

- [x] Extend the dynamic tier to track per-LOD entries separately (each LOD is a distinct atlas entry with its own ref count).
- [x] On mesh registration: load all known LOD meshes into the atlas while keeping higher LOD residency mutable so they can be released and requested back in later.
- [x] Add `RequestLODLoad(logicalMeshID, lodLevel)` and `ReleaseLOD(logicalMeshID, lodLevel)` to `GPUScene`.
- [x] GPU LOD selection writes "LOD requests" to a small request buffer. CPU reads request buffer outside the hot path and can trigger LOD streaming into the dynamic tier.
- [x] When a requested LOD mesh is loaded, update `MeshDataBuffer` and `LODTableBuffer` entries via subdata.

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`
- `XREngine/Models/Meshes/SubMesh.cs`
- `XREngine/Models/Meshes/SubMeshLOD.cs`

#### 9D — LOD Transition Smoothing

- [ ] Add dithered LOD transitions: during LOD switch, render both LODs with per-pixel dither pattern that cross-fades over N frames.
- [ ] Track `PreviousLODLevel` and `LODTransitionProgress` per command (use `Reserved1` or add to Hot struct).
- [ ] Fragment shader samples dither pattern and discards pixels based on transition progress.

Primary files:

- New: `Build/CommonAssets/Shaders/Common/lod_dither.glslinc`
- Fragment shader includes
- `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp` (transition tracking)

Acceptance criteria:

- GPU selects LOD per command, no CPU involvement in the new LOD-selection stage once visible commands are produced.
- Atlas contains multiple LOD meshes simultaneously.
- LOD transitions are smooth (no visible popping).
- LODs can be loaded/unloaded without stalling the render thread.

---

### Phase 10 — Meshlet Pipeline Integration

**Outcome:** Meshlet rendering path is fully functional as an alternative to indirect multi-draw, sharing scene data and culling.

#### 10A — Meshlet Data in Atlas

- [ ] Extend `GPUScene` to generate and store meshlets alongside traditional atlas data.
- [ ] Per mesh in atlas, store a meshlet range: `{ meshletOffset, meshletCount }` in `MeshDataBuffer` or a sidecar buffer.
- [ ] `MeshletGenerator.Build()` runs on mesh registration (can be async).
- [ ] Store meshlet vertex indices, triangle indices, and meshlet descriptors in dedicated SSBOs managed by `GPUScene` (not per-`MeshletCollection`).

Primary files:

- `XREngine/Rendering/Commands/GPUScene.cs`
- `XREngine/Rendering/Meshlets/MeshletGenerator.cs`
- `XREngine/Rendering/Meshlets/Meshlet.cs`

#### 10B — Implement VPRC_RenderMeshesPassMeshlet

- [ ] Replace stub with actual meshlet dispatch.
- [ ] After LOD selection, expand visible commands into meshlet ranges.
- [ ] Use `DrawMeshTasksIndirectCount` (or `DrawMeshTasksIndirect` with GPU-written count) to dispatch task shader groups.
- [ ] Task shader: per-meshlet frustum + occlusion cull (already exists in `MeshletCulling.task`, needs Hi-Z integration).
- [ ] Mesh shader: vertex/triangle output (already exists in `MeshletRender.mesh`).

Primary files:

- `XREngine/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine/Rendering/Meshlets/MeshletCollection.cs`
- `Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task`
- `Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh`

#### 10C — EXT_mesh_shader Support

- [ ] Add `GL_EXT_mesh_shader` path alongside existing `GL_NV_mesh_shader`.
- [ ] EXT uses different local size and dispatch semantics — abstract behind capability flag.
- [ ] Vulkan mesh shader support via `VK_EXT_mesh_shader`.
- [ ] Runtime fallback: EXT preferred → NV fallback → traditional indirect.

Primary files:

- `XREngine/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
- `XREngine/Rendering/Meshlets/MeshletCollection.cs`

#### 10D — Meshlet LOD (Future)

- [ ] Investigate nanite-style cluster group merging for continuous LOD at meshlet granularity.
- [ ] This is a stretch goal — traditional per-mesh LOD (Phase 8) is the default first.

Acceptance criteria:

- Meshlet path renders correctly on NV mesh shader hardware.
- No fallback to traditional path when mesh shaders are available and enabled.
- Meshlet path shares BVH cull and LOD selection with traditional path.
- Performance is equal or better than traditional path on mesh-shader-capable GPUs.

---

### Phase 11 — Production Hardening and Stress Testing

**Outcome:** Stable long-running behavior under stress with zero readbacks confirmed.

- [ ] Run stress tests:
  - [ ] 100K+ command scenes with all paths active.
  - [ ] Massive add/remove bursts (1000+ objects/frame).
  - [ ] Continuous transform animation on all objects.
  - [ ] Many-pass scenes (shadow + depth + forward + transparency).
  - [ ] High material diversity (500+ unique materials).
  - [ ] LOD streaming with rapid distance changes.
- [ ] Validate zero GPU readback bytes in shipping mode across all stress scenarios.
- [ ] Validate no memory leaks or stale resources after repeated scene load/unload.
- [ ] Add crash-safe handling for buffer overflow/truncation with diagnostics.
- [ ] Profile and optimize:
  - [ ] Material scatter shader occupancy and throughput.
  - [ ] Per-material indirect buffer memory usage (fragmentation).
  - [ ] LOD selection shader cost vs. saved triangle count.
  - [ ] Meshlet expansion overhead vs. traditional path.
- [ ] Finalize docs for runtime toggles, debug tools, and fallback behavior.

Acceptance criteria:

- 30+ minute stress run with zero readbacks, no corruption, no leak growth, no fallback thrashing.
- GPU stats dashboard shows 0 readback bytes in shipping config.

---

## Completed Phases

<details>
<summary>Phase 0–6 (completed, click to expand)</summary>

### Phase 0 — Baseline Safety and Switches ✓

- [x] Disabled passthrough default for non-debug configurations.
- [x] Added runtime log for frame mode (passthrough/frustum/BVH).
- [x] Added CPU fallback counters in GPU stats.
- [x] Added config validation on startup.

### Phase 1 — Correctness First (Scene → Atlas → Cull) ✓

- [x] Incremental command update API in `GPUScene` (world matrix, bounds, material, pass, flags).
- [x] Dirty command updates wired from scene/render-info collection.
- [x] World-space bounding sphere computation with conservative non-uniform scale radius.
- [x] Mesh atlas reference counting (increment on use, decrement on remove, free at zero).
- [x] `Destroy()` cleans atlas buffers and related state.

### Phase 2 — Remove CPU Stalls from Hot Path ✓

- [x] Removed CPU-side culled-buffer inspection for normal batch building.
- [x] CPU sanitizer/fallback is debug-only behind explicit setting.
- [x] GPU count buffer used directly for draw submission.
- [x] Profiling markers: mapped buffers/frame, readback bytes, CPU fallback events.

### Phase 3 — Occlusion Culling ✓

- [x] GPU Hi-Z path: depth prepass → Hi-Z pyramid → conservative sphere/AABB test.
- [x] CPU async query path with previous-frame-only latency.
- [x] Temporal hysteresis (N consecutive occluded frames before hiding).
- [x] Reset on camera jump / scene topology change.
- [x] Pass-aware (shadow/depth contributors not hidden).
- [x] Instrumentation (candidates, occluded, false-positive recoveries, temporal overrides).

### Phase 4 — Fully GPU-Driven Batching ✓

- [x] GPU key generation (pass|material|pipeline|mesh|state bits).
- [x] GPU radix sort pipeline with batch range output.
- [x] CPU `BuildMaterialBatches` replaced in default path.
- [x] True instancing aggregation (group identical mesh/material/pass, `instanceCount > 1`).
- [x] CPU batching path retained as debug fallback.

### Phase 5 — VR Stereo and Multi-View ✓

- [x] `ViewSet` model (left/right full, left/right foveated, desktop mirror).
- [x] Shared visibility with per-view lightweight refinement.
- [x] Per-command view/pass masks in sidecar buffers.
- [x] OpenGL: OVR multiview preferred, NV stereo fallback.
- [x] Vulkan: parallel secondary command-buffer fan-out per pass×view.
- [x] Foveated rendering: per-view tiers/regions, near-field forced to full-res.
- [x] Mirror: compose/blit from rendered eye textures by default.
- [x] VR telemetry (per-view visible/draw counts, command-build timing).

### Phase 6 — OpenGL/Vulkan Parity ✓

- [x] Parity checklist (draw indirect, parameter buffer, count draw, index sync).
- [x] Cross-backend integration tests (visible count, draw count, command signatures).
- [x] Backend capability matrix documented.
- [x] Runtime parity snapshot and validation in `GpuBackendParityValidator`.

</details>

---

## Test Backlog

### Completed Tests

<details>
<summary>Click to expand completed test list</summary>

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
- [x] `LodSelection_NearDistance_HighestLod`
- [x] `LodSelection_MediumDistance_MediumLod`
- [x] `LodSelection_FarDistance_LowestLod`
- [x] `LodSelection_BeyondAllDistances_MaxLod`

</details>

### Pending Tests — Phase 7 (Zero-Readback)

- [ ] `ZeroReadback_ShippingMode_ZeroGpuReadbackBytes_FullFrame`
- [ ] `ZeroReadback_PerMaterialScatter_CorrectDrawCounts`
- [ ] `ZeroReadback_PerMaterialScatter_EmptyMaterial_ZeroDraws`
- [ ] `ZeroReadback_TransparencyDomains_SplitWithoutReadback`
- [ ] `ZeroReadback_MaterialAddRemove_RegistryUpdatesCorrectly`
- [ ] `ZeroReadback_FallbackPath_StillFunctional_WhenEnabled`
- [ ] `ZeroReadback_LargeScene_NoStalls_NoReadbacks`

### Pending Tests — Phase 8 (Tiered Atlas)

- [ ] `TieredAtlas_StaticTier_ZeroCpuCostAfterUpload`
- [x] `TieredAtlas_StaticTier_BulkLoad_AllMeshesRendered`
- [ ] `TieredAtlas_DynamicTier_AddRemove_RefCountCorrect`
- [ ] `TieredAtlas_DynamicTier_Defragmentation_NoCorruption`
- [ ] `TieredAtlas_StreamingTier_PerFrameWrite_NoStall`
- [x] `TieredAtlas_StreamingTier_TripleBuffer_NoPipelineHazard`
- [x] `TieredAtlas_MigrateDynamicToStatic_MeshStillRendered`
- [ ] `TieredAtlas_MigrateDynamicToStreaming_EditAndMigrateBack`
- [x] `TieredAtlas_ScatterShader_CorrectTierBucket_PerDraw`
- [x] `TieredAtlas_AllTiersActive_SingleFrame_CorrectOutput`

### Pending Tests — Phase 9 (LOD)

- [ ] `LOD_GPUSelection_CorrectLevelByDistance`
- [x] `LOD_GPUSelection_FallbackToLOD0_WhenSingleLOD`
- [ ] `LOD_AtlasResidency_MultipleLODs_CoexistInAtlas`
- [x] `LOD_AtlasResidency_UnloadUnusedLOD_RefCountZero`
- [ ] `LOD_DitherTransition_NoPoppingOnSwitch`
- [x] `LOD_StreamingRequest_AsyncLoad_NoRenderStall`
- [x] `LOD_TableBuffer_CorrectMeshDataIDs_AfterAtlasRebuild`

### Pending Tests — Phase 10 (Meshlet)

- [ ] `Meshlet_RenderPath_ProducesCorrectOutput_NVMeshShader`
- [ ] `Meshlet_TaskShaderCull_MatchesBVHFrustumResults`
- [ ] `Meshlet_SharedBVHCull_ThenMeshletExpansion`
- [ ] `Meshlet_FallbackToTraditional_WhenMeshShaderUnavailable`
- [ ] `Meshlet_LODIntegration_CorrectMeshletRangePerLOD`

### Pending Tests — Phase 11 (Stress)

- [ ] `Stress_100KCommands_ZeroReadbacks_StableFrameTime`
- [ ] `Stress_MassAddRemove_1000PerFrame_NoCorruption`
- [ ] `Stress_LODStreaming_RapidDistanceChange_NoAtlasCorruption`
- [ ] `Stress_ThirtyMinute_AllPaths_NoLeaks_NoFallbackThrash`
- [ ] `Stress_HighMaterialDiversity_500Materials_CorrectBatching`

### Backlog (Carried Forward)

- [ ] `GPUScene_Destroy_CleansAtlasBuffers_AndState`
- [ ] `GPUScene_IncrementalUpdates_NoRemoveReaddChurn_ForTransformOnlyChanges`
- [ ] `GPUScene_AtlasRefCount_MassRemove_SharedMeshesRemainConsistent`
- [ ] `GPUCulling_PassMaskFiltering_RejectsWrongPassCommands`
- [ ] `GPUCulling_CountOverflow_SetsTruncationFlag_AndPreservesValidity`
- [ ] `IndirectBuild_CountBuffer_DrivesCountDraw_WhenSupported`
- [ ] `IndirectBuild_FallbackPath_UsesClampedCounts_WhenCountDrawUnsupported`
- [ ] `Batching_GPUGeneratedKeys_StableAcrossFrames_WithMaterialChurn`
- [ ] `Batching_InstancingAggregation_CombinesEquivalentMeshMaterialPass`

---

## Notes for Implementation

- **Shipping mode = zero readbacks.** All debug/diagnostic readbacks are behind explicit flags. The default path never reads GPU buffers.
- **Per-material indirect buffers** may use a single large SSBO with material-slot-based offsets to avoid many small buffer allocations. The key is that the CPU knows the offsets statically from the material registry.
- **LOD distance thresholds** should be configurable per-model at import time and overridable at runtime.
- **Meshlet generation** should happen at mesh import/registration time, not per-frame. Cache meshlet data alongside mesh atlas data.
- **Material scatter vs. batch sort:** The scatter approach (Phase 7) replaces the sort+batch approach for the zero-readback path. The old sort+batch path remains for debug/validation.
- **Memory budget:** Per-material indirect buffers need a configurable max-draws-per-material. Default to `CommandCapacity / ActiveMaterialCount` with headroom. Overflow triggers a conservative re-partition.
- Prefer subdata updates over full buffer uploads for incremental changes.
- Do not gate correctness on optional extensions.
- Keep debug features isolated so shipping mode stays GPU-driven.
- Prefer "cull once, fan out many views" over per-eye scene traversal.
- Keep OpenGL and Vulkan view/pipeline key layouts aligned so VR parity tests stay meaningful.
