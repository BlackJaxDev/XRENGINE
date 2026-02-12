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
| (fill in) | (fill in) | (1-5) | Planned |

---

## Completion Checklist

- [ ] Traditional vs meshlet file paths are explicit and consistent.
- [ ] Compute shaders are grouped by function.
- [ ] Naming conventions are applied to new files.
- [ ] Folder README files exist in core rendering and compute directories.
- [ ] CI checks cover naming and shader path resolution.
- [ ] Temporary compatibility mappings removed after migration.
