# Rendering Code Map

## Purpose

Define a clear, stable organization for rendering code so contributors can quickly locate:

- Traditional mesh rendering flow
- Meshlet rendering flow
- GPU compute shader stages by function
- Shared rendering infrastructure and policy layers

This document is the execution companion to Phase 11 in the Vulkan GPU-driven unified TODO.

---

## Design Goals

1. Path intent is obvious from file/folder names.
2. Host-side rendering code is grouped by responsibility.
3. Compute shaders are grouped by stage/function.
4. Backend-agnostic contracts stay separate from backend-specific implementations.
5. Migration can happen in small, safe batches without regressions.

---

## Top-Level Rendering Taxonomy

## 1) Mesh rendering path folders

Target path split:

- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Traditional/`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Meshlet/`
- `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/Shared/`

Rules:

- Files that issue traditional indexed/indirect mesh draws belong in `Traditional/`.
- Files that issue meshlet task/mesh/cluster style draws belong in `Meshlet/`.
- Cross-path utilities and orchestration belong in `Shared/`.
- Do not place path-specific behavior in `Shared/`.

## 2) Host-side GPU rendering domains

Target domains:

- `Policy/` — profiles, feature gates, path selection policy.
- `Dispatch/` — pass setup and dispatch orchestration.
- `Resources/` — buffers, descriptors, pools, lifetimes.
- `Validation/` — assertions, invariants, parity checks.
- `Telemetry/` — counters, timings, debug metrics.

Recommended shape (can be implemented incrementally under existing paths):

- `XRENGINE/Rendering/Commands/Policy/`
- `XRENGINE/Rendering/Commands/Dispatch/`
- `XRENGINE/Rendering/Commands/Resources/`
- `XRENGINE/Rendering/Commands/Validation/`
- `XRENGINE/Rendering/Commands/Telemetry/`

## 3) Compute shader grouping by function

Target shader folders:

- `Build/CommonAssets/Shaders/Compute/Culling/`
- `Build/CommonAssets/Shaders/Compute/Indirect/`
- `Build/CommonAssets/Shaders/Compute/Occlusion/`
- `Build/CommonAssets/Shaders/Compute/Sorting/`
- `Build/CommonAssets/Shaders/Compute/Debug/`

Examples of placement:

- Frustum/BVH/SoA culling shaders → `Culling/`
- Counter reset + indirect build shaders → `Indirect/`
- Depth pyramid + occlusion refine shaders → `Occlusion/`
- Radix/key sort shaders → `Sorting/`
- Probe/readback diagnostic helpers → `Debug/`

---

## Naming Convention

## File/Class intent suffixes

- Path intent:
  - `Traditional`
  - `Meshlet`
  - `Shared`
- Role intent:
  - `Policy`
  - `Dispatcher`
  - `Resources`
  - `Validator`
  - `Stats`

Examples:

- `MeshRenderTraditionalDispatcher.cs`
- `MeshRenderMeshletDispatcher.cs`
- `MeshRenderSharedPolicy.cs`
- `GpuIndirectResources.cs`
- `GpuCullingValidator.cs`

## Shader naming convention

Format:

- `<Subsystem><Operation>.comp`

Subsystem values:

- `Cull`
- `Indirect`
- `Occlusion`
- `Sort`
- `Debug`

Examples:

- `CullFrustum.comp`
- `CullBvh.comp`
- `IndirectResetCounters.comp`
- `IndirectBuildCommands.comp`
- `OcclusionBuildDepthPyramid.comp`
- `SortRadixPairs.comp`

Avoid generic names such as `Helpers`, `Misc`, `Temp`, or stage names without subsystem context.

---

## Ownership Boundaries

- `Shared` may depend on `Policy`, `Resources`, `Telemetry`, `Validation`.
- `Traditional` and `Meshlet` may depend on `Shared` but not on each other directly.
- Backend-specific Vulkan/OpenGL implementation files stay under backend API paths.
- Backend-agnostic interfaces/contracts stay in shared rendering paths.

Recommended dependency direction:

- `Policy` → influences `Dispatch`
- `Dispatch` → uses `Resources`
- `Validation`/`Telemetry` observe `Dispatch` + `Resources`
- Path-specific execution (`Traditional`/`Meshlet`) should not own global policy.

---

## Host → Compute → Indirect Draw Flow

The current high-level execution flow is:

1. Host-side pass orchestration (`GPURendering/*`) prepares per-frame resources and policy.
2. Compute stages execute in order:
  - culling (`Compute/Culling/*`)
  - optional occlusion/depth pyramid (`Compute/Occlusion/*`)
  - key/batch/command construction (`Compute/Indirect/*`)
  - optional ordering (`Compute/Sorting/*`)
3. Indirect command/count buffers are consumed by mesh rendering path dispatchers.
4. Traditional or meshlet draw submission executes from the prepared indirect buffers.

Primary ownership in this phase:

- Host orchestration: `XRENGINE/Rendering/Commands/GPURendering/`
- Path execution: `XRENGINE/Rendering/Pipelines/Commands/MeshRendering/{Traditional,Meshlet,Shared}/`
- Compute stages: `Build/CommonAssets/Shaders/Compute/`

---

## Migration Plan (Safe Batches)

## Batch 1 — Conventions and map

- Finalize naming conventions in this document.
- Add folder-level README files in target directories.
- Add temporary path-redirect map for moved shaders.

## Batch 2 — Shader re-homing

- Move compute shaders into functional folders.
- Keep compatibility references (aliases/redirects) during transition.
- Validate shader discovery and SPIR-V compile checks.

## Batch 3 — Host-side file split

- Split path-specific mesh rendering files into `Traditional/` and `Meshlet/`.
- Move shared orchestration/helpers into `Shared/`.
- Keep behavior unchanged during move-only PRs.

## Batch 4 — Domain cleanup

- Gradually move host files into `Policy/Dispatch/Resources/Validation/Telemetry` domains.
- Remove redundant wrappers once all callsites are migrated.

## Batch 5 — Compatibility removal

- Remove temporary redirects and compatibility aliases.
- Run full parity matrix + perf sanity checks.

---

## PR Rules for Reorganization

1. Prefer move-only PRs before behavior-changing PRs.
2. Keep each PR scoped to one migration batch.
3. Require build + targeted rendering test pass per reorg PR.
4. Keep old→new path mapping table updated in this doc until migration is complete.

---

## Old-to-New Mapping Table (fill during migration)

| Old Path | New Path | Batch | Status |
|---|---|---|---|
| `Build/CommonAssets/Shaders/Compute/LightVolumes.comp` | `Build/CommonAssets/Shaders/Compute/GI/LightVolumes/LightVolumes.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/LightVolumesStereo.comp` | `Build/CommonAssets/Shaders/Compute/GI/LightVolumes/LightVolumesStereo.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/RadianceCascades.comp` | `Build/CommonAssets/Shaders/Compute/GI/RadianceCascades/RadianceCascades.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/RadianceCascadesStereo.comp` | `Build/CommonAssets/Shaders/Compute/GI/RadianceCascades/RadianceCascadesStereo.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/InitialSampling.comp` | `Build/CommonAssets/Shaders/Compute/GI/RESTIR/InitialSampling.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GIResampling.comp` | `Build/CommonAssets/Shaders/Compute/GI/RESTIR/GIResampling.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/FinalShading.comp` | `Build/CommonAssets/Shaders/Compute/GI/RESTIR/FinalShading.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/Init.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/Init.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/Spawn.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/Spawn.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/Recycle.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/Recycle.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/Shade.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/Shade.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/BuildGrid.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/BuildGrid.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/ResetGrid.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/ResetGrid.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/DebugGrid.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/DebugGrid.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/SurfelGI/DebugCircles.comp` | `Build/CommonAssets/Shaders/Compute/GI/SurfelGI/DebugCircles.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp` | `Build/CommonAssets/Shaders/Compute/Culling/GPURenderCulling.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderCullingSoA.comp` | `Build/CommonAssets/Shaders/Compute/Culling/GPURenderCullingSoA.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderExtractSoA.comp` | `Build/CommonAssets/Shaders/Compute/Culling/GPURenderExtractSoA.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderHiZSoACulling.comp` | `Build/CommonAssets/Shaders/Compute/Culling/GPURenderHiZSoACulling.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderIndirect.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderIndirect.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderBuildKeys.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderBuildKeys.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderBuildBatches.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderBuildBatches.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderBuildHotCommands.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderBuildHotCommands.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderCopyCommands.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderCopyCommands.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderCopyCount3.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderCopyCount3.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderResetCounters.comp` | `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderResetCounters.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderOcclusionHiZ.comp` | `Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderHiZInit.comp` | `Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderHiZInit.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/HiZGen.comp` | `Build/CommonAssets/Shaders/Compute/Occlusion/HiZGen.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderRadixIndexSort.comp` | `Build/CommonAssets/Shaders/Compute/Sorting/GPURenderRadixIndexSort.comp` | 2 | Moved |
| `Build/CommonAssets/Shaders/Compute/GPURenderGather.comp` | `Build/CommonAssets/Shaders/Compute/Debug/GPURenderGather.comp` | 2 | Moved |
| `XRENGINE/Rendering/Commands/GpuSortPolicy.cs` | `XRENGINE/Rendering/Commands/GPURendering/Policy/GpuSortPolicy.cs` | 4 | Moved |
| `XRENGINE/Rendering/Commands/GPUBatchingLayout.cs` | `XRENGINE/Rendering/Commands/GPURendering/Resources/GPUBatchingResources.cs` | 4 | Moved/Renamed |
| `XRENGINE/Rendering/Commands/GpuBackendParitySnapshot.cs` | `XRENGINE/Rendering/Commands/GPURendering/Validation/GpuBackendParityValidator.cs` | 4 | Moved/Renamed |
| `Build/CommonAssets/Shaders/Compute/Skinning.comp` | `Build/CommonAssets/Shaders/Compute/Unused/Skinning.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/HiZCull.comp` | `Build/CommonAssets/Shaders/Compute/Unused/HiZCull.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/GPURenderSorting.comp` | `Build/CommonAssets/Shaders/Compute/Unused/GPURenderSorting.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/GPURenderRadixSort.comp` | `Build/CommonAssets/Shaders/Compute/Unused/GPURenderRadixSort.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/MeshSDFGen_Advanced.comp` | `Build/CommonAssets/Shaders/Compute/Unused/MeshSDFGen_Advanced.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/ApplyConstraints.comp` | `Build/CommonAssets/Shaders/Compute/Unused/ApplyConstraints.comp` | 5 | Quarantined |
| `Build/CommonAssets/Shaders/Compute/CalculateParticles.comp` | `Build/CommonAssets/Shaders/Compute/Unused/CalculateParticles.comp` | 5 | Quarantined |

---

## Completion Checklist

- [ ] Traditional vs meshlet file paths are explicit and consistent.
- [ ] Compute shaders are grouped by function.
- [ ] Naming conventions are applied to new files.
- [ ] Folder README files exist in core rendering and compute directories.
- [ ] CI checks cover naming and shader path resolution.
- [ ] Temporary compatibility mappings removed after migration.
