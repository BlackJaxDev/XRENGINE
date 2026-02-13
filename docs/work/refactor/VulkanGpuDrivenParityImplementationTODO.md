# XRENGINE Vulkan GPU-Driven Parity: Phase-Oriented Implementation TODO

## Intent
Bring XRENGINE's Vulkan GPU-driven path to Lumina-class execution tightness while preserving XRENGINE strengths (modularity, diagnostics, backend robustness).

This plan is source-audited against current implementation in:
- `XRENGINE/Rendering/API/Rendering/Vulkan/*`
- `XRENGINE/Rendering/Commands/GPURenderPassCollection.*`
- `XRENGINE/Rendering/HybridRenderingManager.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- compute shaders under `Build/CommonAssets/Shaders/Compute/*`
- test coverage under `XREngine.UnitTests/Rendering/*`

---

## Phase 0 — Establish an Explicit Vulkan Fast-Path Contract

### Goals
- Define exactly what "shipping Vulkan GPU-driven" means.
- Stop silent behavior drift caused by debug/fallback branches and implicit CPU safety nets.

### TODOs
1. **Create explicit runtime profiles for GPU-driven rendering**
   - Add profile enum/config: `ShippingFast`, `DevParity`, `Diagnostics`.
   - Wire profile into indirect/culling/occlusion path decisions (not scattered booleans).
   - Files:
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs`
     - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
     - `XRENGINE/Settings/GameStartupSettings.cs`

2. **Replace hard Vulkan forced-off dispatch policy with profile-gated policy**
   - Current Vulkan-active behavior forces GPU dispatch off in `ResolveGpuRenderDispatchPreference`.
   - Keep safe fallback profile, but allow `DevParity` and eventually `ShippingFast` to enable GPU path.
   - Files:
     - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
     - `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanFeatureProfile.cs`
     - `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`

3. **Declare per-pass fast-path invariants**
   - For each render pass: expected cull source buffer, count buffer, indirect draw buffer, barrier points, and submission API.
   - Add runtime assertions in debug builds.
   - Files:
     - `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`

### Exit criteria
- Vulkan can run GPU dispatch in controlled profile without code edits.
- A single profile switch changes behavior cleanly.
- No silent fallback from GPU path in `ShippingFast` profile.

---

## Phase 1 — Remove Vulkan Indirect Submission Throughput Bottleneck

### Goals
- Eliminate per-draw CPU command loop in non-count Vulkan path.

### TODOs
1. **Upgrade non-count fallback to true multi-draw call**
   - In `RecordIndirectDrawOp`, replace looped `CmdDrawIndexedIndirect(... drawCount=1)` with one call where possible:
     - `CmdDrawIndexedIndirect(commandBuffer, indirectBuffer, baseOffset, drawCount, stride)`
   - Keep loop only for explicit debug slicing/non-contiguous command windows.
   - File:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`

2. **Add path selection telemetry**
   - Track per frame:
     - count path vs non-count path usage
     - number of Vulkan indirect API calls
     - effective drawCount submitted
   - Files:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
     - `XRENGINE/Rendering/HybridRenderingManager.cs`
     - `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs` (or equivalent stats partial)

3. **Harden parity behavior between count and no-count modes**
   - Ensure count/no-count both consume same indirect ranges and produce same geometry.
   - Extend tests that already cover parity scaffolding.
   - Files:
     - `XREngine.UnitTests/Rendering/GpuRenderingBacklogTests.cs`
     - `XREngine.UnitTests/Rendering/IndirectMultiDrawTests.cs`
     - `XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs`

### Exit criteria
- No per-draw loop in normal no-count path.
- Lower command buffer recording overhead at high draw counts.
- Count and no-count parity tests pass.

---

## Phase 2 — Synchronization Ownership Unification (RenderGraph-First)

### Goals
- Remove split ownership between planned barriers and ad-hoc barriers.

### TODOs
1. **Define barrier ownership policy**
   - Render graph metadata + synchronization planner is authoritative.
   - Backend ad-hoc barriers only for documented exceptional transitions.
   - Files:
     - `XRENGINE/Rendering/RenderGraph/RenderGraphSynchronization.cs`
     - `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
     - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`

2. **Minimize duplicated barriers around indirect draw ops**
   - Today `RecordIndirectDrawOp` always emits manual compute/transfer→draw-indirect barrier.
   - Gate or remove this when equivalent barrier already emitted by pass planner.
   - File:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`

3. **Add barrier diagnostics and mismatch assertions**
   - Emit warnings when pass-local memory barrier masks overlap/conflict with planned barriers.
   - Add per-pass barrier summary logs under debug categories.
   - Files:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
     - `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`

4. **Queue ownership strategy rollout**
   - Start graphics-only ownership baseline.
   - Enable compute/transfer ownership transitions by explicit profile and measured gain.
   - Files:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
     - `XREngine.UnitTests/Rendering/VulkanTodoP2ValidationTests.cs`

### Exit criteria
- Planned barriers cover all normal cull→indirect→draw transitions.
- Ad-hoc barriers are exceptional and explainable.
- No redundant barrier churn in hot frame path.

---

## Phase 3 — Cull/Occlusion Pipeline Consolidation for Deterministic Fast Path

### Goals
- Keep rich modes, but establish one canonical production path.

### TODOs
1. **Canonical production culling mode policy**
   - Preferred order:
     - frustum or BVH frustum (depending on scene/provider readiness)
     - GPU Hi-Z refine
     - indirect build
   - Treat `ForcePassthroughCulling` and CPU query occlusion as diagnostics/tooling-only in shipping profile.
   - Files:
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.Occlusion.cs`

2. **Control CPU fallback semantics**
   - Current path can recover from GPU-zero results via CPU copy fallback in multiple places.
   - In `ShippingFast`, replace auto-recover with strict error counters + optional frame marker (no hidden path switch).
   - Files:
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
     - `XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderMeshesPass.cs`

3. **Decouple diagnostics from hot path**
   - Move heavy probes (`DumpSourceCommandProbe`, pass filter debug readbacks, map/unmap paths) behind profile checks.
   - Keep diagnostics available in `Diagnostics` profile only.
   - Files:
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`

4. **Per-view contract validation**
   - Validate view-mask, pass-mask, per-view offset/capacity invariants before dispatch.
   - Ensure `SourceViewId` and `PerViewDrawCount` flow is deterministic for stereo/foveated/mirror.
   - Files:
     - `XRENGINE/Rendering/Commands/RenderCommandCollection.cs`
     - `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
     - `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`

### Exit criteria
- One predictable cull/occlusion pipeline in shipping profile.
- No hidden CPU rescue paths in shipping profile.
- Stable multi-view visibility and submission behavior.

---

## Phase 4 — Hot-Path Data Compaction (48-float Payload Split)

### Goals
- Reduce bandwidth and shader pressure in cull + indirect build.

### TODOs
1. **Introduce hot/cold command data split**
   - Hot buffer: minimal cull/indirect fields (bounds, mesh/submesh/material ID, pass, instance count, flags, source index).
   - Cold buffer: world matrices, prev matrices, debug/extra metadata.
   - Files:
     - `XRENGINE/Rendering/Data/Rendering/GPUIndirectRenderCommand*` (struct definitions)
     - `XRENGINE/Rendering/Commands/GPURenderPassCollection.*`
     - `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp`
     - `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`

2. **Keep backwards-compatible debug lane**
   - Maintain existing 48-float path behind debug compatibility toggle until migration complete.

3. **Validate baseInstance/source-index mapping invariants after split**
   - `baseInstance` currently encodes source command index.
   - Ensure vertex shader fetch path remains correct with new layout.
   - Files:
     - `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp`
     - GPU indirect vertex shader paths in scene shaders

### Exit criteria
- Reduced memory traffic in cull/indirect shaders.
- No change in visible output correctness.

---

## Phase 5 — Bindless/Descriptor Fast-Path Maturity

### Goals
- Improve Vulkan descriptor stability and reduce binding churn.

### TODOs
1. **Formalize descriptor set contract by pass type**
   - Global, material, per-draw, optional bindless tables.
   - Auto-validation against shader-reflected descriptor layouts.
   - Files:
     - `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs`
     - Vulkan pipeline/descriptor management files

2. **Material-table first path for indirect draws**
   - Minimize per-batch rebinding in `HybridRenderingManager`.
   - Prefer table/ID-driven fetch where possible.
   - File:
     - `XRENGINE/Rendering/HybridRenderingManager.cs`

3. **Investigate optional Vulkan-only geometry fetch fast lane**
   - Prototype BDA-style path for heavy scenes, preserve cross-backend path.
   - Keep opt-in until validated.

### Exit criteria
- Descriptor writes/rebinds per frame drop in GPU-driven scenes.
- No descriptor contract regressions across compute/raster passes.

---

## Phase 6 — Async Compute and Queue Overlap (Measured Rollout)

### Goals
- Increase overlap only when net-positive.

### TODOs
1. **Document queue strategy and rollout flags**
   - `GraphicsOnly` baseline
   - `Graphics+Compute` experimental
   - `Graphics+Compute+Transfer` advanced

2. **Measure overlap efficacy**
   - Track queue ownership transfer count, barrier count, overlap windows, and frame time deltas.

3. **Promote overlap per pass only after perf gate passes**
   - Candidate passes: Hi-Z generation, occlusion refine, indirect build.

### Exit criteria
- Async overlap can be enabled scene-selectively with measurable gain.
- No unstable ownership churn.

---

## Phase 7 — CI Parity Matrix and Golden-Scene Validation

### Goals
- Make parity regressions impossible to miss.

### TODOs
1. **Golden scenes with expected counters**
   - expected culled count, indirect count, per-view counts, overflow/truncation markers.

2. **CI matrix dimensions**
   - Vulkan count extension: on/off
   - profiles: `DevParity` and `ShippingFast`
   - view modes: mono, stereo, foveated, mirror
   - culling modes: frustum and BVH

3. **Expand/realign existing tests**
   - Build on existing tests already present in:
     - `XREngine.UnitTests/Rendering/GpuRenderingBacklogTests.cs`
     - `XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs`
     - `XREngine.UnitTests/Rendering/VulkanTodoP2ValidationTests.cs`

4. **Add source-level tests for critical anti-patterns**
   - no per-draw Vulkan indirect loops in default fallback path
   - no unconditional CPU fallback in shipping profile

### Exit criteria
- CI catches parity/perf regressions across extension and profile permutations.

---

## Cross-Phase Guardrails

1. **Do not remove diagnostics; isolate them.**
   - Keep debug power in `Diagnostics`, keep hot path clean in `ShippingFast`.

2. **Prefer policy-centralization over boolean proliferation.**
   - Avoid ad-hoc checks across files.

3. **Every optimization must include a parity test + metric.**
   - Correctness first, then throughput.

4. **Preserve backend modularity.**
   - Vulkan fast path can be specialized without regressing OpenGL compatibility.

---

## Recommended immediate execution order (first 2 sprints)

### Sprint A
- Phase 0 tasks 1-3
- Phase 1 task 1
- Phase 7 task 4 (anti-pattern guard tests)

### Sprint B
- Phase 1 tasks 2-3
- Phase 2 tasks 1-2
- Phase 3 task 1

This sequencing gives the quickest path to tangible Vulkan throughput gain while improving determinism and keeping fallback behavior explicit.