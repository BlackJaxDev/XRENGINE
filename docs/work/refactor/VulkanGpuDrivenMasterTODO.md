# XREngine Vulkan GPU-Driven Rendering — Master TODO

## Purpose

This document synthesizes findings from all four LuminaEngine parity analysis reports
(LuminaParity1–4) against actual XREngine source code inspection. It provides a unified,
prioritized TODO list to bring XREngine's Vulkan GPU-driven rendering path to
Lumina-class execution speed while preserving XREngine's modularity, configurability,
and diagnostic richness.

The guiding principle: **fast by default, flexible by configuration, debuggable by opt-in.**

---

## Current State Summary (from code inspection)

| Subsystem | OpenGL | Vulkan |
|---|---|---|
| GPU frustum culling | Active | **Gated OFF** (`VulkanFeatureProfile`) |
| GPU indirect build | Active | **Gated OFF** |
| GPU batching/instancing | Active | **Gated OFF** |
| Hi-Z occlusion culling | Active (Phase 3) | **Gated OFF** |
| BVH frustum culling | Active | **Gated OFF** |
| Multi-view rendering | Active (64 views) | **Gated OFF** |
| IndirectCount draw | Active | Implemented but unreachable |
| Non-count fallback | N/A | Per-draw loop (`CmdDrawIndexedIndirect × N`) |
| GPU sorting | **Commented out entirely** | N/A |
| Meshlet pipeline | **Disabled** | N/A |
| Descriptor system | OpenGL SSBOs | **Minimal** — single UBO layout, no bindless |
| Barrier/sync | Memory barriers | Render graph + barrier planner (functional) |
| Command payload | 48 floats (192 bytes) per command | Same |

**Critical blocker:** `VulkanFeatureProfile` returns `!IsActive` for `EnableComputeDependentPasses`,
`EnableGpuRenderDispatch`, `EnableGpuBvh`, and `EnableImGui` — meaning **every GPU-driven
subsystem is hard-disabled** when Vulkan is the active backend.

---

## Phase 0 — Unblock Vulkan GPU-Driven Path (CRITICAL)

These items must be completed first. Everything else depends on Vulkan actually executing
the GPU-driven pipeline.

### 0.1 — Graduated Vulkan Feature Enablement
**Priority:** P0 | **Effort:** M | **Risk:** Medium

- [ ] Replace boolean `VulkanFeatureProfile` kill-switches with graduated enum levels:
  - `Disabled` — current behavior, CPU-driven only.
  - `Validation` — GPU-driven stages execute but with full diagnostics, readback parity
    checks, and overflow detection active. Used in dev/CI.
  - `Production` — GPU-driven stages execute with minimal overhead. No CPU readback,
    no fallback branches, no diagnostic probes on hot path.
- [ ] Add engine setting `VulkanGpuDrivenLevel` (default: `Validation` in debug, `Production`
  in release) to control the level.
- [ ] Keep per-feature granularity underneath (compute passes, GPU dispatch, BVH, ImGui)
  so individual subsystems can be toggled independently during bring-up.
- [ ] Log active profile fingerprint at startup: which features are enabled, which extensions
  are detected, which paths will execute.

### 0.2 — Vulkan Compute Pipeline Parity for GPU-Driven Shaders
**Priority:** P0 | **Effort:** L | **Risk:** Medium-High

- [ ] Verify all GPU-driven compute shaders compile and execute correctly under Vulkan
  SPIR-V compilation:
  - `GPURenderResetCounters.comp`
  - `GPURenderCulling.comp` / `GPURenderCullingSoA.comp`
  - `GPURenderIndirect.comp`
  - `GPURenderBuildKeys.comp`
  - `GPURenderBuildBatches.comp`
  - `bvh_frustum_cull.comp`
- [ ] Ensure SSBO binding indices used by these shaders map to valid Vulkan descriptor set
  bindings (currently OpenGL-oriented `binding = N` layout qualifiers).
- [ ] Validate compute descriptor set creation covers all buffers consumed by GPU-driven
  passes (command buffer, culled buffer, indirect buffer, count buffer, mesh data buffer,
  view constants, per-view visible indices).
- [ ] Add SPIR-V compilation CI check for all compute shaders.

### 0.3 — Vulkan Descriptor Set Architecture for GPU-Driven Passes
**Priority:** P0 | **Effort:** L | **Risk:** High

Current Vulkan descriptor set layout is a single UBO at binding 0 (vertex stage only).
This is completely insufficient for GPU-driven passes.

- [ ] Design and implement descriptor set layout tiers:
  - **Set 0 — Engine globals:** camera UBO, frame UBO, scene constants.
  - **Set 1 — GPU-driven compute:** command SSBO, culled SSBO, indirect SSBO, count SSBO,
    mesh data SSBO, view constants SSBO, per-view visible indices SSBO.
  - **Set 2 — Material/draw:** material UBO/SSBO, per-draw instance data, bindless texture
    table (when enabled).
  - **Set 3 — Per-pass resources:** depth pyramid sampler, Hi-Z texture, occlusion buffers.
- [ ] Implement `DescriptorSetLayoutCache` keyed by schema hash to avoid redundant layout
  creation.
- [ ] Implement persistent descriptor sets with `VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT`
  for stable global/material tables that change infrequently.
- [ ] Pool sizing: compute pools must accommodate per-frame descriptor set allocation for
  all GPU-driven dispatch stages (currently pool grows by 16 — may need tuning for
  high-pass-count frames).

---

## Phase 1 — Indirect Submission Efficiency (P0)

The Vulkan non-count fallback path currently loops `CmdDrawIndexedIndirect(..., drawCount=1, stride)`
per command — confirmed in `CommandBuffers.cs`. This is the single largest Vulkan throughput
bottleneck at scale.

### 1.1 — Contiguous Multi-Draw for Non-Count Path
**Priority:** P0 | **Effort:** M | **Risk:** Low

- [ ] When indirect buffer is contiguous (normal case), issue a single
  `CmdDrawIndexedIndirect(buffer, offset, drawCount=N, stride)` call.
- [ ] Only retain per-draw loop for exceptional cases: non-contiguous ranges, debug
  slicing, or per-batch segmented submission.
- [ ] Add explicit reason tagging when loop fallback is used (log category + telemetry
  counter).

### 1.2 — IndirectCount as Preferred Path
**Priority:** P0 | **Effort:** S | **Risk:** Low

- [ ] `VK_KHR_draw_indirect_count` detection is already implemented — ensure it is the
  default path selected when available.
- [ ] Add telemetry: count-draw vs contiguous-multi-draw vs looped-fallback selection
  per frame.
- [ ] Validate `maxDrawCount` parameter correctness (must match buffer capacity, not
  actual visible count).

### 1.3 — Batched Material Indirect Submission
**Priority:** P1 | **Effort:** M | **Risk:** Medium

- [ ] Current `HybridRenderingManager.RenderTraditionalBatched` iterates `DrawBatch`
  list and calls `DispatchRenderIndirect` per batch. On Vulkan, each call becomes a
  separate command buffer record.
- [ ] Evaluate merging compatible batches (same pipeline state, same descriptor sets)
  into single multi-draw-indirect calls with sub-buffer offsets.
- [ ] Track batch merge ratio as a performance metric.

### 1.4 — Secondary Command Buffer Strategy
**Priority:** P1 | **Effort:** M | **Risk:** Medium

- [ ] `_enableSecondaryCommandBuffers` exists but needs validation for indirect draw
  recording efficiency.
- [ ] Profile primary-only vs secondary CB recording for batched indirect draws.
- [ ] If secondary CBs help, enable parallel recording for independent material batches
  within the same render pass.

---

## Phase 2 — Synchronization and Barrier Consolidation (P0/P1)

XREngine has three partially overlapping barrier/sync systems:
1. Render graph synchronization planner (`RenderGraphSynchronizationPlanner`)
2. Vulkan barrier planner (`VulkanBarrierPlanner`)
3. Inline ad-hoc barriers (e.g., in `RecordIndirectDrawOp`)

### 2.1 — Render Graph as Single Source of Truth
**Priority:** P0 | **Effort:** M | **Risk:** Medium

- [ ] Define policy: render graph declares all resource dependencies and required states.
  `VulkanBarrierPlanner` translates these into Vulkan barriers. No other code path
  should inject barriers except through tagged exceptions.
- [ ] Audit current inline barrier in `RecordIndirectDrawOp` (`ShaderWriteBit | TransferWriteBit
  → IndirectCommandReadBit`). This should be expressed as a render graph edge
  (compute-cull-pass → indirect-draw-pass resource dependency) instead.
- [ ] Add debug-build validation that detects unregistered barrier injections outside
  the planner.

### 2.2 — Barrier Metrics and Optimization
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Expose per-frame counters:
  - Image barriers emitted
  - Buffer barriers emitted
  - Queue ownership transfers
  - Redundant/merged barriers (barriers that were planned but collapsed)
  - Pipeline stage flushes
- [ ] Set target budgets per frame class (simple scene, complex scene, stress test) and
  alert on regression.

### 2.3 — Queue Ownership Transfer Minimization
**Priority:** P2 | **Effort:** M | **Risk:** Medium

- [ ] `VulkanBarrierPlanner` already has `QueueOwnershipConfig` with compute/graphics/transfer
  families. Add heuristics:
  - Skip ownership transfer if source and destination are on the same queue family.
  - Skip async compute overlap if the compute workload is small enough that transfer
    overhead exceeds overlap savings.
- [ ] Instrument actual transfer count and cost per frame.

---

## Phase 3 — Hot-Path Command Payload Optimization (P1)

The 48-float (192-byte) per-command payload is the heaviest part of the GPU-driven
pipeline. Every culling thread reads 192 bytes; every indirect build thread reads it
again. At 100K commands, that is ~18 MB of bandwidth per pass, per read.

### 3.1 — Hot/Cold Data Split
**Priority:** P1 | **Effort:** L | **Risk:** Medium-High

- [ ] Define hot struct (culling + indirect critical only):
  ```
  HotCommand (target: 48–64 bytes)
  ├── BoundingSphere    vec4  (16B) — culling
  ├── MeshID            uint  (4B)  — indirect build
  ├── SubmeshID         uint  (4B)  — indirect build
  ├── MaterialID        uint  (4B)  — batching
  ├── InstanceCount     uint  (4B)  — validity
  ├── RenderPass        uint  (4B)  — pass filter
  ├── Flags             uint  (4B)  — transparency/shadow/skinned
  ├── LayerMask         uint  (4B)  — view filter
  ├── RenderDistanceSq  float (4B)  — distance cull
  └── LODLevel          uint  (4B)  — LOD select
  Total: 52 bytes → pad to 64 bytes
  ```
- [ ] Define cold struct (per-draw extended data, read only by vertex shader / debug):
  ```
  ColdCommand (remaining ~128 bytes)
  ├── WorldMatrix       mat4 (64B)
  ├── PrevWorldMatrix   mat4 (64B)  — motion vectors
  └── ShaderProgramID, Reserved, etc.
  ```
- [ ] Culling shader reads only hot buffer. Indirect build reads hot buffer + mesh data
  buffer. Vertex shader reads cold buffer via `gl_BaseInstance` index.
- [ ] This reduces culling bandwidth from 192B to 64B per command (3× reduction).

### 3.2 — SoA Culling Path Enablement
**Priority:** P1 | **Effort:** M | **Risk:** Low

- [ ] `GPURenderCullingSoA.comp` already exists and reads pre-extracted SoA arrays
  (bounding spheres, metadata). The config flags (`UseSoA`, `UseHiZ`) are commented
  out in `GPURenderPassCollection.Sorting.cs`.
- [ ] Un-comment and wire up `UseSoA` configuration.
- [ ] SoA layout for culling hot data: separate `vec4[]` for bounding spheres, `uint[]`
  for flags/pass/layer, `float[]` for render distance. This maximizes cache line
  utilization during brute-force frustum checks.
- [ ] Benchmark AoS (current) vs hot/cold split vs full SoA on representative scenes
  (1K, 10K, 100K commands).

### 3.3 — Overflow and Tail-Command Handling
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Overflow flag and sentinel writing exist in `GPURenderCulling.comp` and
  `GPURenderIndirect.comp`. Verify these paths execute correctly on Vulkan.
- [ ] Tail clearing of stale indirect commands is fully commented out in
  `HybridRenderingManager`. Evaluate whether Vulkan path needs explicit zeroing
  of unused indirect command slots (important for non-count path where `drawCount`
  is fixed at buffer capacity).
- [ ] If using count-draw path, tail clearing is unnecessary (GPU skips unused slots).
  Document this as an invariant.

---

## Phase 4 — Bindless and Descriptor Indexing (P1/P2)

LuminaEngine uses `GL_EXT_buffer_reference` for buffer device address-based geometry
fetch and descriptor indexing for material access. XREngine currently has no bindless
support.

### 4.1 — Descriptor Indexing Extension Enablement
**Priority:** P1 | **Effort:** M | **Risk:** Medium

- [ ] Enable `VK_EXT_descriptor_indexing` (core in Vulkan 1.2):
  - `shaderSampledImageArrayNonUniformIndexing`
  - `descriptorBindingSampledImageUpdateAfterBind`
  - `descriptorBindingPartiallyBound`
  - `runtimeDescriptorArray`
- [ ] This enables a bindless texture table: one large descriptor array of sampled images,
  indexed by material texture ID in shader.
- [ ] Material ID in the hot/cold command maps to an entry in this table.

### 4.2 — Bindless Material Table
**Priority:** P1 | **Effort:** L | **Risk:** Medium

- [ ] Create a global material texture table as a large descriptor array (e.g., 4096
  sampled images).
- [ ] `GPUScene._materialIDMap` already assigns stable IDs starting at 1. Use these as
  indices into the bindless table.
- [ ] Update policy: update-after-bind for additions, deferred compaction for removals.
- [ ] Residency guarantee: all referenced textures must be resident before draw submission.
  Track via `_materialIDMap` lifecycle events.

### 4.3 — Buffer Device Address for Geometry Fetch (Experimental)
**Priority:** P2 | **Effort:** L | **Risk:** High

This is Lumina's approach: `GL_EXT_buffer_reference` / `VK_KHR_buffer_device_address`
for direct pointer-style vertex/index buffer access in shaders.

- [ ] Prototype: store buffer device addresses in per-mesh metadata SSBO, indexed by
  MeshID. Vertex shader fetches position/normal/tangent/UV via address + offset.
- [ ] Compare against current atlas-based approach (`_atlasPositions`, `_atlasNormals`, etc.
  in `GPUScene`) for:
  - Draw call setup overhead (no VAO/VBO binding needed with BDA)
  - Memory fragmentation characteristics
  - Shader complexity and maintenance burden
- [ ] Go/no-go decision: if atlas approach with bindless textures achieves target
  performance, BDA adds complexity without proportional gain. XREngine's atlas model
  already provides good locality.

---

## Phase 5 — Occlusion and Visibility Pipeline (P1)

### 5.1 — GpuHiZ as Production Default
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Set `GpuHiZ` as default occlusion mode in production profile.
- [ ] Demote `CpuQueryAsync` to tooling/diagnostic mode only.
- [ ] Ensure Hi-Z pyramid generation runs as a dedicated compute pass with proper
  render graph dependency on depth prepass output.

### 5.2 — Depth Pyramid Generation Pass
**Priority:** P1 | **Effort:** M | **Risk:** Medium

- [ ] Verify depth pyramid compute pass generates correct mip chain on Vulkan backend.
- [ ] Each mip level write requires UAV barrier before next level read (Lumina does this
  explicitly per mip). Ensure XREngine's pyramid builder does the same.
- [ ] Depth pyramid should be a shared per-frame resource, not regenerated per view
  (unless stereo/multi-view requires separate pyramids).

### 5.3 — Two-Phase Occlusion Culling
**Priority:** P2 | **Effort:** M | **Risk:** Medium

Lumina's approach: cull pass optionally samples depth pyramid from previous frame.
Full two-phase approach:

- [ ] Phase 1: Cull against previous frame's depth pyramid → draw visible objects →
  generate new depth pyramid.
- [ ] Phase 2: Re-cull previously rejected objects against new depth pyramid → draw
  newly visible objects.
- [ ] This eliminates one-frame-lag occlusion artifacts for fast camera movement.
- [ ] XREngine's occlusion scaffold already has ping-pong buffer support — verify it
  can support two-phase without major rework.

### 5.4 — Temporal Invalidation Hardening
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] On camera teleport / large projection change / scene load, invalidate depth
  pyramid and skip occlusion culling for that frame.
- [ ] Add heuristic: if camera velocity exceeds threshold, widen occlusion acceptance
  margin to reduce false rejections.

---

## Phase 6 — GPU Sorting Restoration (P1/P2)

All GPU sorting is currently commented out (`GPURenderPassCollection.Sorting.cs`):
bitonic sort, radix sort, merge sort, histogram buffers, gather program, radix index
sort compute shader.

### 6.1 — Evaluate Sorting Need for Vulkan Path
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Determine if GPU sorting of draw commands is needed for:
  - Front-to-back opaque rendering (reduces overdraw)
  - Back-to-front transparent rendering (correctness)
  - Material/pipeline state sorting (reduces state changes)
- [ ] If needed, choose one algorithm to restore and validate:
  - **Radix sort** is typically best for large key-value sorts on GPU
  - Bitonic sort is simpler but less efficient for large N

### 6.2 — Restore and Port Radix Sort
**Priority:** P2 | **Effort:** M | **Risk:** Medium

- [ ] Un-comment and re-enable radix sort compute shader and host-side dispatch.
- [ ] Ensure sort key encoding supports depth (for front-to-back), material ID
  (for state coherence), and pass ID (for pass grouping).
- [ ] Sort operates on the culled command list, reordering indices before indirect
  build consumes them.
- [ ] Validate sort correctness with deterministic test scenes.

---

## Phase 7 — Pipeline State Management (P1)

### 7.1 — Pipeline Cache Keyed by Pass/Material/Vertex Layout
**Priority:** P1 | **Effort:** M | **Risk:** Low

- [ ] Implement or audit `VkPipelineCache` usage for:
  - Graphics pipelines (vertex layout + shader + render pass + blend/depth state)
  - Compute pipelines (shader + specialization constants)
- [ ] Key by: `(renderPassHash, shaderHash, vertexLayoutHash, blendStateHash)`.
- [ ] Prewarm high-frequency permutations at scene load time to avoid first-hit stalls.
- [ ] Serialize pipeline cache to disk for faster subsequent loads.

### 7.2 — Dynamic Rendering Consolidation
**Priority:** P2 | **Effort:** M | **Risk:** Medium

- [ ] Evaluate `VK_KHR_dynamic_rendering` (core in Vulkan 1.3) to eliminate render pass
  object management overhead.
- [ ] Benefits: no VkRenderPass/VkFramebuffer objects, simpler pass begin/end, easier
  dynamic attachment management.
- [ ] LuminaEngine enables this — evaluate parity benefit and minimum Vulkan version
  requirement tradeoff.

### 7.3 — Reduce Per-Batch State Churn
**Priority:** P1 | **Effort:** M | **Risk:** Medium

- [ ] Profile state changes per frame: pipeline binds, descriptor set binds, push constant
  updates, vertex buffer binds.
- [ ] With bindless textures + material SSBO, most per-material state can collapse to a
  single pipeline + single descriptor set + material index push constant.
- [ ] Target: for N material batches, reduce from N pipeline binds + N descriptor binds
  to ≤ K pipeline binds (where K = unique pipeline permutations) + 1 descriptor bind
  + N push constant updates.

---

## Phase 8 — Async Compute Overlap (P2)

### 8.1 — Overlap Policy Design
**Priority:** P2 | **Effort:** M | **Risk:** Medium

- [ ] Identify candidate overlaps:
  - Depth pyramid generation (compute) overlapped with non-depth-dependent raster work.
  - GPU culling (compute) overlapped with shadow map rendering (raster).
  - Cluster/light culling (compute) overlapped with depth prepass (raster).
- [ ] Design requires separate compute queue family and timeline semaphore coordination.
- [ ] XREngine already has queue ownership transfer support in `VulkanBarrierPlanner`.

### 8.2 — Metrics-First Enablement
**Priority:** P2 | **Effort:** S | **Risk:** Low

- [ ] Never enable async overlap without measuring:
  - Frame time with/without overlap
  - Barrier + ownership transfer cost
  - GPU bubble utilization
- [ ] Only promote overlap if net frame-time improvement > 5%.

---

## Phase 9 — Validation, Testing, and Telemetry (P0/P1)

### 9.1 — GPU-Driven Parity Test Suite
**Priority:** P0 | **Effort:** M | **Risk:** Low

- [ ] Create deterministic golden scenes with known geometry counts.
- [ ] For each scene, record expected values:
  - Total commands submitted to GPU
  - Culled command count (after frustum)
  - Visible command count (after occlusion)
  - Indirect draw count emitted
  - Draw count consumed by raster
- [ ] Run parity tests for:
  - OpenGL vs Vulkan (same visible geometry)
  - Count-draw vs non-count-draw (same visible geometry)
  - Production vs diagnostic profile (same visible geometry, different overhead)

### 9.2 — Per-Pass GPU Timing
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Implement Vulkan timestamp queries around major GPU-driven stages:
  - Counter reset
  - Culling dispatch
  - Occlusion refinement
  - Indirect build dispatch
  - Indirect draw submission
- [ ] Report per-stage GPU milliseconds per frame.
- [ ] `BvhGpuProfiler` already exists for BVH timing — extend pattern.

### 9.3 — Indirect Effectiveness Telemetry
**Priority:** P0 | **Effort:** S | **Risk:** Low

- [ ] Track per frame:
  - `RequestedDrawCount` — total commands before culling
  - `CulledDrawCount` — commands surviving frustum + occlusion
  - `EmittedIndirectCount` — indirect commands written by build shader
  - `ConsumedDrawCount` — actual draws executed by GPU (from count buffer readback
    in diagnostic mode only)
  - `OverflowCount` — truncated commands due to buffer capacity
- [ ] Cull efficiency = `1 - CulledDrawCount / RequestedDrawCount`.
- [ ] Target: >50% cull efficiency on typical scenes (indicates culling is working).

### 9.4 — Frame-Op Sorting Invariant Validation
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Add debug-build assertions that frame-op ordering after render graph compilation
  satisfies:
  - All compute dispatches for a pass complete before dependent raster passes begin.
  - No circular dependencies between frame ops.
  - Descriptor schema consistency: same pass index always uses same descriptor layout.
  - Pass index determinism: same scene state produces same pass ordering.

---

## Phase 10 — Production Hardening (P1)

### 10.1 — Eliminate Silent CPU Fallback in Production
**Priority:** P0 | **Effort:** S | **Risk:** Low

- [ ] In production profile, any fallback to CPU batching, CPU indirect rebuild, or CPU
  readback-driven dispatch must:
  - Log a warning (budget-limited).
  - Increment a forbidden-fallback counter.
  - CI test fails if counter > 0 on golden scenes.
- [ ] Current code has `IndirectDebug.EnableCpuBatching`, `IndirectDebug.DisableCpuReadbackCount`,
  etc. — production profile must hard-set these to no-fallback values.

### 10.2 — Debug/Diagnostic Cost Isolation
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Ensure all diagnostic features are zero-cost when disabled:
  - `IndirectDebugSettings` 15+ toggle flags should be evaluated at pass setup time,
    not per-command or per-draw.
  - Category-based logging with budget-limited output is already good — verify no
    allocation occurs when category is disabled.
  - Readback buffers should not be allocated in production profile.

### 10.3 — Capacity and Overflow Policy
**Priority:** P1 | **Effort:** S | **Risk:** Low

- [ ] Document and enforce buffer capacity policy:
  - Command buffer: initial capacity from `GPUScene.MinCommandCount`, grows to
    `MaxLoadedCommands`.
  - Indirect buffer: sized to max visible commands.
  - Count buffer: single uint per view.
  - Overflow: overflow flag set in shader, logged on CPU readback (diagnostic only),
    buffer grown for next frame.
- [ ] Growth policy: double capacity on overflow, cap at engine maximum.
- [ ] Never crash on overflow — degrade gracefully (truncate draws, log warning).

---

## Dependency Graph

```
Phase 0 (Unblock)
├── 0.1 Feature enablement
├── 0.2 Compute shader parity
└── 0.3 Descriptor architecture
    │
    ├──→ Phase 1 (Indirect submission) ──→ Phase 3 (Hot/cold payload)
    ├──→ Phase 2 (Sync/barriers)       ──→ Phase 8 (Async compute)
    ├──→ Phase 4 (Bindless/descriptors)──→ Phase 7 (Pipeline state)
    ├──→ Phase 5 (Occlusion)
    ├──→ Phase 9 (Validation/telemetry) — runs in parallel with all phases
    └──→ Phase 10 (Production hardening) — runs in parallel with all phases

Phase 6 (GPU sorting) is independent, can start after Phase 0.
```

---

## Quick Reference: What LuminaEngine Has That XREngine Lacks

| Lumina Feature | XREngine Status | Phase |
|---|---|---|
| GPU-driven path active on Vulkan | **Gated OFF** | 0.1 |
| Compute shaders validated on SPIR-V | **Untested** | 0.2 |
| Multi-tier descriptor set layouts | **Single UBO only** | 0.3 |
| Single-call multi-draw indirect | **Per-draw loop fallback** | 1.1 |
| Buffer device address geometry fetch | **Not implemented** (atlas instead) | 4.3 |
| Descriptor indexing / bindless textures | **Not implemented** | 4.1, 4.2 |
| Integrated depth pyramid feedback | **Scaffolded, gated off** | 5.2 |
| Two-phase occlusion culling | **Not implemented** | 5.3 |
| Dynamic rendering (`VK_KHR_dynamic_rendering`) | **Not implemented** | 7.2 |
| Compact per-instance command layout | **192B per command** | 3.1 |
| Timeline semaphores for async compute | **Not implemented** | 8.1 |
| Active GPU front-to-back sorting | **Commented out** | 6.1, 6.2 |

---

## Quick Reference: What XREngine Has That LuminaEngine Lacks

These are strategic advantages to preserve:

| XREngine Advantage | Preserve? |
|---|---|
| Multi-backend abstraction (OpenGL + Vulkan) | Yes — but don't let it slow Vulkan hot path |
| BVH-accelerated frustum culling | Yes — superior scalability for large scenes |
| Multi-view rendering (64 views) | Yes — essential for VR/XR |
| Foveated rendering awareness in culling | Yes — critical for XR performance |
| Rich per-stage diagnostic instrumentation | Yes — but isolate from production profile |
| `IndirectParityChecklist` runtime path selection | Yes — valuable safety net |
| Mesh atlas with reference counting | Yes — good locality, simpler than BDA |
| CPU async occlusion query fallback | Yes — useful diagnostic tool |
| Double-buffered update/render command storage | Yes — proper threading model |
| Material program generation from material definition | Yes — flexible shader authoring |

---

## Effort Estimates

| Phase | Effort | Priority |
|---|---|---|
| 0 — Unblock Vulkan GPU-Driven | XL (4–6 weeks) | P0 |
| 1 — Indirect Submission | M (1–2 weeks) | P0 |
| 2 — Sync/Barriers | M (1–2 weeks) | P0/P1 |
| 3 — Hot/Cold Payload | L (2–4 weeks) | P1 |
| 4 — Bindless/Descriptors | L (2–4 weeks) | P1/P2 |
| 5 — Occlusion | M (1–2 weeks) | P1 |
| 6 — GPU Sorting | M (1–2 weeks) | P1/P2 |
| 7 — Pipeline State | M (1–2 weeks) | P1/P2 |
| 8 — Async Compute | M (1–2 weeks) | P2 |
| 9 — Validation/Telemetry | M (1–2 weeks) | P0/P1 |
| 10 — Production Hardening | S (3–5 days) | P0/P1 |

**Total estimated:** ~16–28 weeks of focused rendering engineering work.

---

## Sprint-Level Execution Plan (10 Sprints)

### Sprint 1 — Foundation
- 0.1 graduated feature enablement design + implementation
- 0.2 SPIR-V compilation validation for all compute shaders
- 9.3 indirect effectiveness telemetry (lightweight, unblocks measurement)

### Sprint 2 — Descriptor Architecture
- 0.3 multi-tier descriptor set layout implementation
- 0.2 compute descriptor binding validation end-to-end

### Sprint 3 — First Light (Vulkan GPU-Driven Executing)
- Enable `Validation` level for Vulkan GPU-driven pipeline
- 1.1 contiguous multi-draw for non-count path
- 1.2 IndirectCount as preferred path validation
- 9.1 golden scene parity tests (OpenGL vs Vulkan)

### Sprint 4 — Sync Consolidation
- 2.1 render graph as sync source of truth
- 2.2 barrier metrics
- 9.4 frame-op sorting invariants
- 10.1 forbidden fallback counters

### Sprint 5 — Hot Path Optimization
- 3.1 hot/cold command data split (design + shader changes)
- 3.2 SoA culling path enablement
- 5.1 GpuHiZ production default

### Sprint 6 — Bindless Foundation
- 4.1 descriptor indexing extension enablement
- 4.2 bindless material texture table
- 7.1 pipeline cache audit

### Sprint 7 — Occlusion and Sorting
- 5.2 depth pyramid generation validation
- 5.4 temporal invalidation hardening
- 6.1 GPU sorting need evaluation
- 6.2 radix sort restoration (if needed)

### Sprint 8 — State and Submission Polish
- 1.3 batched material submission optimization
- 1.4 secondary command buffer evaluation
- 7.3 per-batch state churn reduction
- 10.2 diagnostic cost isolation

### Sprint 9 — Advanced Features
- 5.3 two-phase occlusion culling
- 7.2 dynamic rendering evaluation
- 8.1 async compute overlap design
- 4.3 buffer device address experiment (go/no-go)

### Sprint 10 — Production Validation
- 8.2 async compute metrics-first enablement
- 2.3 queue ownership transfer minimization
- 9.2 per-pass GPU timing
- 10.3 capacity and overflow policy finalization
- Full parity test suite sign-off

---

## Exit Criteria

All must pass before declaring Vulkan GPU-driven path production-ready:

- [ ] Production profile executes full GPU-driven pipeline with zero forbidden fallbacks
  on all golden scenes.
- [ ] OpenGL and Vulkan paths produce identical visible geometry on golden scenes.
- [ ] Count-draw and non-count-draw paths produce identical visible geometry.
- [ ] CPU indirect submission overhead is < 1ms for 100K draw commands.
- [ ] Per-frame descriptor writes are bounded and within target budget.
- [ ] Barrier count is within target budget (no redundant barriers).
- [ ] Cull efficiency > 50% on representative scenes.
- [ ] No GPU validation layer errors on any golden scene.
- [ ] Pipeline cache hit rate > 95% after warmup frame.
