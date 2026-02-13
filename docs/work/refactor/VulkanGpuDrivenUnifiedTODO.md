# XREngine Vulkan GPU-Driven Rendering — Unified Comprehensive TODO

## Purpose

This document consolidates the two Vulkan GPU-driven TODO plans into one implementation-ready roadmap.
The goal is to deliver a Vulkan path that is:

See also: `docs/architecture/rendering/RenderingCodeMap.md`

- **Fast by default**
- **Configurable by policy**
- **Debuggable by opt-in diagnostics**
- **Parity-safe across backends and feature profiles**

---

## Current State Summary (from source audit)

| Subsystem | OpenGL | Vulkan |
|---|---|---|
| GPU frustum culling | Active | Gated off by Vulkan policy settings |
| GPU indirect build | Active | Gated off by Vulkan policy settings |
| GPU batching / instancing | Active | Gated off by Vulkan policy settings |
| Hi-Z occlusion culling | Active (phase-gated) | Gated off |
| BVH frustum culling | Active | Gated off |
| Multi-view rendering | Active (up to 64 views) | Gated off |
| IndirectCount draw path | Active | Implemented but not consistently selected |
| Non-count fallback | N/A | Per-draw `CmdDrawIndexedIndirect` loop in fallback path |
| GPU sorting | Commented out | Not active |
| Meshlet pipeline | Disabled | Not active |
| Descriptor architecture | OpenGL SSBO-oriented | Minimal Vulkan descriptor schema |
| Barrier/sync model | Barrier calls + pass structure | Render graph + planner + ad-hoc barriers (overlap) |
| Command payload | 48-float (192B) command layout | Same |

Primary blocker: Vulkan feature policy currently prevents normal GPU-driven execution on the fast path.

---

## RTX IO Vulkan Integration Status (2026-02)

- [x] Added Vulkan runtime negotiation for `VK_NV_memory_decompression` in logical-device creation.
- [x] Added Vulkan runtime negotiation for `VK_NV_copy_memory_indirect` in logical-device creation.
- [x] Added feature/profile gating for RTX IO-style Vulkan decompression (`VulkanFeatureProfile.EnableRtxIoVulkanDecompression`).
- [x] Added feature/profile gating for RTX IO-style Vulkan indirect copy (`VulkanFeatureProfile.EnableRtxIoVulkanCopyMemoryIndirect`).
- [x] Added engine runtime state flags (`HasVulkanMemoryDecompression`, `HasVulkanRtxIo`).
- [x] Added direct command helpers to submit `vkCmdDecompressMemoryNV` / `vkCmdDecompressMemoryIndirectCountNV` through the Vulkan renderer.
- [x] Added direct command helpers to submit `vkCmdCopyMemoryIndirectNV` / `vkCmdCopyMemoryToImageIndirectNV` through the Vulkan renderer.
- [x] Integrate compressed asset containers (GDeflate blocks + metadata) into mesh/texture import/build pipeline.
- [x] Wire decompression command calls into texture/mesh upload jobs when compressed payloads are detected.
- [x] Wire indirect copy command calls into texture/mesh upload jobs when command-buffer-driven copy batches are available.
- [x] Add perf telemetry for compressed bytes, decompressed bytes, and decompression command timing.

Notes:
- Indirect copy is now wired into Vulkan upload paths (`VkDataBuffer` buffer uploads and `VkTexture2D` buffer→image uploads), with automatic fallback to classic copy commands.
- Decompression call wiring is active for compressed mesh-buffer payloads tagged as GDeflate (`XRDataBuffer` compressed payload metadata + `VkDataBuffer` upload path), with fallback when unsupported.
- Authoring GDeflate payloads is now backed by DirectStorage's `IDStorageCompressionCodec` in `Compression.TryCompressGDeflate` / `Compression.TryDecompressGDeflate` on Windows; non-Windows paths still fall back to non-GDeflate encodings.

Current runtime-layer status: **finalized** for Vulkan RTX IO extension negotiation + command submission APIs and Windows content-pipeline GDeflate authoring/decode support; non-Windows authoring continues to use non-GDeflate fallbacks.

---

## Guiding Contract

### Runtime Profiles

Create explicit profile policy for GPU-driven behavior:

- `ShippingFast`: production throughput, no silent CPU rescue, no hot-path diagnostics.
- `DevParity`: GPU-driven enabled with parity checks, bounded diagnostics, correctness-first.
- `Diagnostics`: maximal instrumentation, readbacks, probes, and fallback exploration.

### Principles

1. Centralize policy decisions in profile/config layers, not scattered booleans.
2. Keep diagnostics available, but isolate overhead from production.
3. Require parity tests and telemetry for each optimization.
4. Preserve backend modularity while allowing Vulkan fast-path specialization.
5. Keep rendering code organization explicit so path intent is obvious from file name and folder.

---

## Phase 0 — Unblock Vulkan GPU-Driven Execution (P0)

### 0.1 Policy and Feature Enablement

- [x] Replace binary Vulkan kill-switch behavior with profile-gated behavior (`ShippingFast`, `DevParity`, `Diagnostics`).
- [x] Add setting surface for profile selection with defaults:
  - Debug builds: `DevParity`
  - Release builds: `ShippingFast`
- [x] Preserve per-feature toggles under profile control (compute passes, GPU dispatch, BVH, ImGui, occlusion).
- [x] Log startup profile fingerprint (enabled features, available Vulkan extensions, effective dispatch path).

**Primary files:**
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XRENGINE/Settings/GameStartupSettings.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanFeatureProfile.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`

### 0.2 Compute Shader Vulkan Parity

- [x] Validate compile + dispatch for compute shaders under SPIR-V path:
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderResetCounters.comp`
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderCullingSoA.comp`
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderBuildKeys.comp`
  - [x] `Build/CommonAssets/Shaders/Compute/GPURenderBuildBatches.comp`
  - [x] `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp`
- [x] Verify binding index compatibility between shader layout declarations and Vulkan descriptor sets.
- [x] Add CI SPIR-V compilation checks for all GPU-driven compute shaders.

### 0.3 Descriptor Set Architecture Foundation

- [x] Implement descriptor set tiers:
  - Set 0: engine globals (camera/frame/scene constants)
  - Set 1: GPU-driven compute buffers (command, culled, indirect, count, mesh data, view constants, per-view indices)
  - Set 2: material/draw data
  - Set 3: per-pass occlusion resources (depth pyramid / Hi-Z / occlusion buffers)
- [x] Add descriptor layout cache keyed by schema hash.
- [x] Add persistent descriptor set support for stable tables (update-after-bind where supported).
- [x] Re-tune descriptor pool growth for high pass-count frames.

**Primary files:**
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs`
- Vulkan pipeline/descriptor management files under `XRENGINE/Rendering/API/Rendering/Vulkan/*`

### Exit Criteria

- [ ] Vulkan GPU-driven dispatch runs via profile switch only (no code edits).
- [ ] No unconditional Vulkan forced-off policy remains in fast/ parity profiles.
- [x] Compute passes are descriptor-complete and shader-valid on Vulkan.

---

## Phase 1 — Indirect Submission Throughput (P0)

### 1.1 Non-Count Path Multi-Draw Upgrade

- [x] Replace normal fallback per-draw loops with contiguous multi-draw call:
  - `CmdDrawIndexedIndirect(..., drawCount=N, stride)`
- [x] Keep per-draw loop only for explicit debug slicing or non-contiguous windows.
- [x] Emit reason tag + telemetry when loop fallback is used.

**Primary file:**
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`

### 1.2 IndirectCount Preferred Path

- [x] Make count-based draw path default when extension support is available.
- [x] Validate `maxDrawCount` uses buffer capacity rather than visible count.
- [x] Add per-frame telemetry for path usage and effective draw counts.

### 1.3 Batched Submission Refinement

- [x] Merge compatible material batches into fewer Vulkan indirect submissions where state compatibility allows.
- [x] Track merge ratio and API-call reduction.

**Primary file:**
- `XRENGINE/Rendering/HybridRenderingManager.cs`

### 1.4 Secondary Command Buffer Evaluation

- [ ] Compare primary-only vs secondary command recording for high batch counts.
- [ ] Enable parallel secondary recording only if net-positive.

### Exit Criteria

- [ ] No per-draw loop in normal non-count path.
- [ ] Count and non-count modes are parity-consistent.
- [ ] Command recording overhead is reduced in high draw-count scenes.

---

## Phase 2 — Synchronization Ownership Unification (P0/P1)

### 2.1 RenderGraph-First Barrier Ownership

- [x] Define synchronization ownership policy: render graph + planner are authoritative.
- [x] Remove or gate ad-hoc indirect-draw barriers when planner already covers transitions.
- [x] Keep backend-local barriers only for documented exceptional transitions.

**Primary files:**
- `XRENGINE/Rendering/RenderGraph/RenderGraphSynchronization.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`

### 2.2 Barrier Diagnostics and Metrics

- [x] Add counters: image barriers, buffer barriers, queue ownership transfers, merged/redundant barriers, stage flushes.
- [x] Warn on overlap/conflicts between planned and ad-hoc barriers in debug profile.
- [x] Add per-pass barrier summaries under debug categories.

### 2.3 Queue Ownership Policy Rollout

- [x] Start with graphics-family baseline.
- [x] Add profile-gated compute/transfer ownership transitions.
- [x] Skip transfers when queues are identical or overlap overhead outweighs benefits.

### Exit Criteria

- [ ] Normal cull→indirect→draw transitions are planner-owned.
- [ ] Ad-hoc barriers are minimal, justified, and measurable.

---

## Phase 3 — Deterministic Culling/Occlusion Fast Path (P1)

### 3.1 Canonical Production Path

- [x] Define shipping culling pipeline order:
  1. Frustum or BVH-frustum
  2. GPU Hi-Z refine
  3. Indirect build
- [x] Mark CPU query occlusion and passthrough culling as diagnostics tooling in shipping profile.

**Primary files:**
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.Occlusion.cs`

### 3.2 CPU Fallback Semantics

- [x] In `ShippingFast`, replace silent CPU auto-recovery with strict counters + warnings.
- [x] Keep fallback experimentation in `Diagnostics` profile only.

**Primary files:**
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderMeshesPass.cs`

### 3.3 Per-View Contract Validation

- [x] Assert per-view offsets/capacities, view mask/pass mask, source view IDs, and deterministic draw-count flow.
- [x] Validate stereo/foveated/mirror behavior for deterministic submission.

**Primary files:**
- `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`
- `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
- `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`

### 3.4 Depth Pyramid Reliability

- [x] Validate Vulkan depth pyramid mip chain correctness and inter-mip synchronization.
- [x] Ensure depth pyramid is shared per frame unless multi-view constraints require separation.
- [x] Add temporal invalidation for teleport/projection jumps/scene loads.

### Exit Criteria

- [x] One predictable shipping cull/occlusion pipeline.
- [x] No hidden CPU rescue path in shipping profile.

---

## Phase 4 — Hot-Path Data Compaction (P1)

### 4.1 Hot/Cold Command Layout Split

- [x] Add compact hot command payload and hot buffer allocation/wiring in render-pass lifecycle.
- [x] Add hot command build compute stage for source/culled paths.
- [x] Keep vertex fetch path correct via source-index/baseInstance mapping.
- [x] Add hot-data consumption path in culling/occlusion/indirect shaders with guarded fallback.
- [x] Complete full cold-buffer migration for matrices/extended metadata.
- [x] Switch production path to hot-only (remove legacy 48-float fallback from shipping path).

**Primary files:**
- `XRENGINE/Rendering/Data/Rendering/GPUIndirectRenderCommand*`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.*`
- `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
- `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`

### 4.2 SoA Culling Enablement

- [x] Route SoA extraction pipeline to consume hot command buffers when available.
- [x] Re-enable SoA configuration/policy toggle for runtime selection.
- [x] Benchmark AoS vs hot/cold vs SoA on representative command counts.

### 4.3 Overflow and Tail Handling

- [x] Validate overflow flag/sentinel handling on Vulkan path.
- [x] Clarify tail-clearing policy for non-count mode vs count mode.
- [x] Enforce growth policy on overflow (bounded doubling, no crash).

### Exit Criteria

- [x] Reduced culling/indirect bandwidth pressure.
- [x] Output parity preserved.

---

## Phase 5 — Descriptor Indexing and Material Fast Path (P1/P2)

### 5.1 Descriptor Indexing Enablement

- [x] Enable Vulkan descriptor indexing features needed for large runtime descriptor arrays.
- [x] Add capability checks and profile-gated feature enablement.

### 5.2 Bindless Material Table

- [x] Implement global material texture table indexed by stable material IDs.
- [x] Use update-after-bind policy for additions and controlled compaction for removals.
- [x] Enforce residency guarantees prior to draw submission.

### 5.3 Descriptor Contract Validation

- [x] Formalize descriptor contract per pass type (global/material/per-draw/per-pass).
- [x] Validate against reflected shader layout to prevent schema drift.

### 5.4 Optional Vulkan Geometry Fetch Fast Lane (Experimental)

- [x] Prototype buffer-device-address style geometry fetch path.
- [x] Compare against atlas-based baseline for setup overhead, memory behavior, and maintainability.
- [x] Keep opt-in unless clearly superior.

### Exit Criteria

- [x] Descriptor churn and rebind cost reduced on GPU-driven scenes.
- [x] Descriptor contract stable across compute and raster passes.

---

## Phase 6 — GPU Sorting Reintroduction (P1/P2)

### 6.1 Need Assessment

- [x] Decide required sorting domains:
  - opaque front-to-back
  - transparent back-to-front
  - material/state grouping

### 6.2 Restore Chosen GPU Sort Path

- [x] Restore one production algorithm (prefer radix for large-N).
- [x] Re-enable host dispatch and key encoding flow.
- [x] Validate deterministic correctness on controlled scenes.

### Exit Criteria

- [x] Sorting is either intentionally omitted with evidence, or restored and validated.

---

## Phase 7 — Pipeline State and Submission Churn (P1/P2)

### 7.1 Pipeline Cache Maturity

- [x] Audit/implement Vulkan pipeline cache usage for graphics and compute.
- [x] Key cache by pass/shader/layout/state hashes.
- [x] Prewarm common permutations; persist cache to disk.

### 7.2 State Churn Reduction

- [x] Profile pipeline binds, descriptor binds, push constants, vertex buffer binds.
- [x] Collapse per-material binding churn with material-table + index-driven fetch.

### 7.3 Dynamic Rendering Evaluation

- [x] Evaluate dynamic rendering adoption feasibility and compatibility tradeoffs.

### Exit Criteria

- [x] Stable pipeline cache behavior and reduced per-batch state churn.

---

## Phase 8 — Async Compute/Queue Overlap (P2)

### 8.1 Overlap Policy

- [x] Define staged queue modes:
  - `GraphicsOnly`
  - `Graphics+Compute`
  - `Graphics+Compute+Transfer`
- [x] Identify candidate overlap passes (Hi-Z generation, occlusion refine, indirect build).

### 8.2 Metrics-First Promotion

- [x] Track overlap windows, transfer costs, barrier counts, and frame-time deltas.
- [x] Promote overlap only when gain passes threshold and remains stable.

### Exit Criteria

- [x] Overlap is scene-selective, measurable, and net-positive.

---

## Phase 9 — Validation, Testing, and Telemetry (P0/P1)

### 9.1 Golden-Scene Parity Suite

- [x] Add deterministic golden scenes with expected counts:
  - requested
  - culled
  - visible
  - emitted indirect
  - consumed draws
  - overflow/truncation markers
- [x] Validate across:
  - backend parity (OpenGL vs Vulkan)
  - count vs non-count parity
  - profile parity (`DevParity` vs `ShippingFast`)
  - view modes (mono/stereo/foveated/mirror)

### 9.2 Timing and Stage Profiling

- [x] Add Vulkan timestamp queries around reset/cull/occlusion/indirect/draw stages.
- [x] Report per-stage GPU ms per frame.

### 9.3 Indirect Effectiveness Telemetry

- [x] Track per-frame counters:
  - requested draws
  - culled draws
  - emitted indirect draws
  - consumed draws (diagnostic mode readback)
  - overflow count
- [x] Report cull efficiency and regression alerts.

### 9.4 Anti-Pattern and Invariant Tests

- [x] Add source-level tests to guard against:
  - default-path per-draw Vulkan indirect loops
  - unconditional CPU fallback in shipping profile
- [x] Assert frame-op dependency order, no circular dependencies, descriptor schema consistency, pass-order determinism.

**Primary tests:**
- `XREngine.UnitTests/Rendering/GpuRenderingBacklogTests.cs`
- `XREngine.UnitTests/Rendering/IndirectMultiDrawTests.cs`
- `XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs`
- `XREngine.UnitTests/Rendering/VulkanTodoP2ValidationTests.cs`

### Exit Criteria

- [x] CI matrix catches parity/perf regressions before merge.

---

## Phase 10 — Production Hardening (P0/P1)

### 10.1 No Silent Fallback in Shipping Profile

- [x] Any forbidden fallback increments explicit counters and emits bounded warnings.
- [x] CI fails when forbidden fallback counters are non-zero on golden scenes.

### 10.2 Diagnostic Cost Isolation

- [x] Ensure debug toggles are evaluated at pass setup, not per-draw.
- [x] Ensure disabled diagnostics allocate nothing and perform no readbacks.

### 10.3 Capacity and Overflow Policy

- [x] Document command/indirect/count buffer capacities and growth behavior.
- [x] Enforce bounded growth and graceful truncation behavior under overflow.

Reference:
- `docs/work/refactor/VulkanGpuDrivenCapacityPolicy.md`

### Exit Criteria

- [x] Shipping profile is deterministic, fast, and fallback-explicit.

---

## Phase 11 — Code Organization and File Intent Clarity (P0/P1)

### 11.1 Mesh Rendering Path Separation

- [x] Split mesh rendering command/pipeline files by explicit path intent:
  - Traditional path files use `Traditional` in class/file naming.
  - Meshlet path files use `Meshlet` in class/file naming.
  - Shared orchestration remains in neutral `MeshRendering` files.
- [x] Remove ambiguous names where behavior is path-specific.
- [x] Add thin path-router entry points so callers do not encode path details.

**Target organization (example):**
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Traditional/*`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Meshlet/*`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Shared/*`

### 11.2 Compute Shader Grouping by Function

- [x] Reorganize compute shaders into function-based folders:
  - `Culling` (frustum/BVH/SoA)
  - `Indirect` (reset/build/count support)
  - `Occlusion` (Hi-Z/depth pyramid/refine)
  - `Sorting` (radix/keys/gather)
  - `Debug` (diagnostic-only shader utilities)
- [x] Keep shader names prefixed by subsystem and operation, using one naming scheme.
- [x] Add compatibility include/alias map during migration to avoid immediate breakage.

**Target organization (example):**
- `Build/CommonAssets/Shaders/Compute/Culling/*`
- `Build/CommonAssets/Shaders/Compute/Indirect/*`
- `Build/CommonAssets/Shaders/Compute/Occlusion/*`
- `Build/CommonAssets/Shaders/Compute/Sorting/*`
- `Build/CommonAssets/Shaders/Compute/Debug/*`

### 11.3 Host-Side File Taxonomy for GPU Rendering

- [x] Group host-side GPU rendering code by domain, not by historical growth:
  - `Policy` (profiles, feature gates, settings)
  - `Dispatch` (pass setup and dispatch wiring)
  - `Resources` (buffers/descriptors/pools)
  - `Validation` (parity checks/assertions)
  - `Telemetry` (timers/counters/diagnostics)
- [x] Keep backend-agnostic interfaces near shared domain code; backend-specific implementations under backend folders.
- [x] Ensure each file has a single primary responsibility.

### 11.4 Naming and Suffix Convention Standardization

- [x] Introduce naming rules for renderer files and shader files:
  - Path intent suffixes: `Traditional`, `Meshlet`, `Shared`.
  - Role suffixes: `Policy`, `Dispatcher`, `Resources`, `Validator`, `Stats`.
  - Shader operation prefixes: `Cull`, `Indirect`, `Occlusion`, `Sort`, `Debug`.
- [x] Ban generic file names that hide behavior (for example: `Helpers`, `Misc`, `Temp`).
- [x] Add CI lint/check to flag new files violating naming convention.

### 11.5 Documentation and Discoverability

- [x] Add a rendering code map document that explains:
  - where to modify traditional rendering flow
  - where to modify meshlet rendering flow
  - where each compute shader stage lives
  - how data flows host → compute → indirect draw
- [x] Add folder-level README files in major rendering and compute directories.
- [x] Maintain migration table (old path → new path) until all references are updated.

**Primary docs:**
- `docs/architecture/rendering/RenderingCodeMap.md` (new)
- `docs/work/refactor/VulkanGpuDrivenUnifiedTODO.md` (this plan)

### 11.6 Migration Execution Safety

- [x] Perform moves in small batches with build + shader compile checks at each batch.
- [x] Keep temporary compatibility hooks for moved shaders/files until all call sites are migrated.
- [x] Add tests that validate resolved shader paths and pass registration after reorganization.
- [ ] Remove compatibility hooks only after parity matrix passes.

### Exit Criteria

- [x] A new contributor can locate traditional vs meshlet rendering code paths in under 5 minutes.
- [x] Compute shaders are grouped by function with no ambiguous placement.
- [x] Naming conventions are enforced by CI checks.
- [ ] Refactor causes no regression in parity or performance baselines.

---

## Dependency Graph

```text
Phase 0 (Unblock and contract)
├──→ Phase 1 (Indirect submission)
├──→ Phase 2 (Sync ownership)
├──→ Phase 3 (Culling/occlusion canonical path)
├──→ Phase 11 (Code organization clarity)
├──→ Phase 5 (Descriptor indexing/material table)
├──→ Phase 9 (Validation/telemetry) [parallel]
└──→ Phase 10 (Hardening) [parallel]

Phase 4 (Hot-path compaction) starts after stable Phase 1/3 behavior.
Phase 6 (GPU sorting) can begin after Phase 0.
Phase 7 (Pipeline/state churn) follows descriptor and submission stabilization.
Phase 8 (Async overlap) follows sync ownership maturity.
Phase 11 should start early and run incrementally with all implementation phases.
```

---

## Suggested First Two Sprints

### Sprint A

- [ ] Phase 0.1 profile contract + policy wiring
- [ ] Phase 0.2 compute shader SPIR-V validation baseline
- [ ] Phase 1.1 non-count multi-draw upgrade
- [ ] Phase 9.4 anti-pattern guard tests
- [ ] Phase 11.1/11.2 folder taxonomy and naming convention definition

### Sprint B

- [ ] Phase 1.2/1.3 telemetry + parity hardening
- [ ] Phase 2.1/2.2 barrier ownership + diagnostics
- [x] Phase 3.1 canonical production cull/occlusion policy
- [ ] Phase 11.3/11.6 first migration batch (host-side + shader path compatibility)

---

## Consolidated Exit Criteria (Production Readiness)

- [ ] `ShippingFast` executes full GPU-driven Vulkan path with zero forbidden fallbacks on golden scenes.
- [ ] OpenGL and Vulkan produce equivalent visible geometry on parity scenes.
- [ ] Count and non-count indirect modes produce equivalent visible geometry.
- [ ] CPU recording overhead for high draw count scenes stays within performance budget.
- [ ] Descriptor writes and barrier counts remain within defined frame budgets.
- [ ] Cull efficiency stays above target range on representative scenes.
- [ ] No Vulkan validation layer errors in parity matrix scenes.
- [ ] Pipeline cache hit rate reaches target after warmup.
