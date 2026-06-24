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

## Backend renderer folder taxonomy

OpenGL and Vulkan backend files now use a shared responsibility vocabulary under
`XREngine.Runtime.Rendering/Rendering/API/Rendering/<Backend>/`. Namespaces
intentionally remain stable as `XREngine.Rendering.OpenGL` and
`XREngine.Rendering.Vulkan`; this reorganization is a path move and split, not a
namespace migration.

Common backend folders:

- `Bootstrap/` - context/API creation, instance/device setup, extension probes,
  startup validation, and compatibility setup.
- `Frame/` - frame lifecycle, swapchain/present or frame-adjacent timing,
  synchronization, fences, stats, and retirement.
- `Commands/` - command buffers, draw submission, blits, readbacks, indirect
  draw, state application, command-chain scheduling, and dirty tracking.
- `RenderGraph/` - backend render-graph compilation, pass planning, barriers,
  resource planning, and graph refresh policy. Vulkan uses this today; OpenGL
  does not have backend-local graph planning yet.
- `Resources/` - backend resource allocation, staging/uploads, buffers,
  textures, framebuffers, memory, and non-wrapper resource orchestration.
- `Descriptors/` - Vulkan descriptors and OpenGL binding-equivalent policy such
  as texture, uniform, sampler, image, and bindless binding helpers.
- `Pipelines/` - pipeline/program caches, compile queues, prewarm databases,
  render target modes, and shader-link backend selection.
- `Shaders/` - compiler/linker-facing source helpers, source compatibility,
  reflection, rewrite/fixup passes, and shader artifact caches.
- `Features/` - optional backend features such as bindless, meshlets,
  raytracing, sparse textures, streaming, RTX IO, luminance, and upscaling.
- `UI/` - backend ImGui and editor UI renderer integration.
- `BackendObjects/` - API wrappers around engine resources.
- `Types/` - small backend-specific value types, enums, conversion helpers, and
  interop structs.

`BackendObjects` is intentionally explicit. It replaces ambiguous `Objects/` and
old OpenGL `Types/` wrapper folders while preserving the no-standalone-backend
`XRMesh` rule below: `GLMeshRenderer` and `VkMeshRenderer` own mesh draw
readiness, mesh data invalidation, buffer collection, and draw submission, while
`GLDataBuffer` and `VkDataBuffer` own backend buffer upload/readiness state.

Path changes should be reviewed as move-only diffs before semantic refactors.
Large-file splits should stay behavior-neutral except for access or partial-class
adjustments required by the split.

### Backend old-to-new map

| Old Path | New Path |
|---|---|
| `Vulkan/Init.cs` | `Vulkan/Bootstrap/VulkanRenderer.Initialization.cs` |
| `Vulkan/Extensions.cs` | `Vulkan/Bootstrap/VulkanExtensions.cs` |
| `Vulkan/PhysicalDevice.cs` | `Vulkan/Bootstrap/VulkanRenderer.PhysicalDevice.cs` |
| `Vulkan/Validation.cs` | `Vulkan/Bootstrap/VulkanRenderer.Validation.cs` |
| `Vulkan/Objects/*Device/Instance/Surface*.cs` | `Vulkan/Bootstrap/VulkanRenderer.*.cs` |
| `Vulkan/Drawing.Core.cs` | `Vulkan/Frame/VulkanRenderer.FrameLoop.cs` |
| `Vulkan/SwapChain.cs` | `Vulkan/Frame/VulkanRenderer.Swapchain.cs` |
| `Vulkan/VulkanSynchronization.cs` | `Vulkan/Frame/VulkanRenderer.Synchronization.cs` |
| `Vulkan/Objects/CommandBuffers.cs` | `Vulkan/Commands/VulkanRenderer.CommandBuffer*.cs` |
| `Vulkan/VulkanCommandChain*.cs` | `Vulkan/Commands/VulkanCommandChain*.cs` |
| `Vulkan/VulkanRenderer.State.cs` | `Vulkan/Commands/VulkanRenderer.StateTracking.cs`, `Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs`, `Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs`, `Vulkan/Resources/VulkanRenderer.ResourceRegistration.cs` |
| `Vulkan/VulkanRenderGraphCompiler.cs` | `Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs` |
| `Vulkan/VulkanBarrierPlanner.cs` | `Vulkan/RenderGraph/VulkanBarrierPlanner.cs` |
| `Vulkan/VulkanResourcePlanner.cs` | `Vulkan/RenderGraph/VulkanResourcePlanner.cs` |
| `Vulkan/VulkanResourceAllocator.cs` | `Vulkan/Resources/VulkanResourceAllocator.cs` |
| `Vulkan/VulkanStagingManager.cs` | `Vulkan/Resources/Uploads/VulkanStagingManager.cs` |
| `Vulkan/Memory/*` | `Vulkan/Resources/Memory/*` |
| `Vulkan/VulkanDescriptor*.cs` | `Vulkan/Descriptors/VulkanDescriptor*.cs` |
| `Vulkan/VulkanShaderTools.cs` | `Vulkan/Shaders/VulkanShaderAutoUniforms.cs`, `Vulkan/Shaders/VulkanShaderCompiler.cs`, `Vulkan/Shaders/VulkanShaderReflection.cs`, `Vulkan/Shaders/VulkanShaderSourceFixups.cs`, `Vulkan/Shaders/VulkanShaderTransformFeedback.cs`, `Vulkan/Shaders/VulkanShaderTypes.cs` |
| `Vulkan/VulkanPipeline*.cs` | `Vulkan/Pipelines/VulkanPipeline*.cs` |
| `Vulkan/VulkanUpscaleBridge*.cs` | `Vulkan/Features/Upscaling/VulkanUpscaleBridge*.cs` |
| `Vulkan/Objects/Types/*` | `Vulkan/BackendObjects/*` or `Vulkan/Types/*` |
| `OpenGL/OpenGLRenderer.cs` | `OpenGL/Bootstrap/OpenGLRenderer.cs` |
| `OpenGL/OpenGLRenderer.DebugTracking.cs` | `OpenGL/Frame/OpenGLRenderer.DebugTracking.cs` |
| `OpenGL/OpenGLRenderer.DrawSubmission.cs` | `OpenGL/Commands/OpenGLRenderer.DrawSubmission.cs` |
| `OpenGL/OpenGLRenderer.TextureBindings.cs` | `OpenGL/Descriptors/OpenGLRenderer.TextureBindings.cs` |
| `OpenGL/OpenGLRenderer.AsyncPrograms.cs` | `OpenGL/Pipelines/OpenGLRenderer.AsyncPrograms.cs` |
| `OpenGL/OpenGLShaderLinkBackendSelector.cs` | `OpenGL/Pipelines/OpenGLShaderLinkBackendSelector.cs` |
| `OpenGL/OpenGLRenderer.ImGui*.cs` | `OpenGL/UI/OpenGLRenderer.ImGui*.cs` |
| `OpenGL/OpenGLRenderer.Luminance.cs` | `OpenGL/Features/Luminance/OpenGLRenderer.Luminance*.cs` |
| `OpenGL/Types/Buffers/*` | `OpenGL/BackendObjects/Buffers/*` or `OpenGL/Resources/Uploads/*` |
| `OpenGL/Types/Textures/*` | `OpenGL/BackendObjects/Textures/*` and `OpenGL/BackendObjects/Samplers/*` |
| `OpenGL/Types/Render Targets/*` | `OpenGL/BackendObjects/Framebuffers/*` |
| `OpenGL/Types/Mesh Renderer/*` | `OpenGL/BackendObjects/MeshRendering/*` |
| `OpenGL/Types/Meshes/GLRenderProgram.Linking.cs` | `OpenGL/BackendObjects/Programs/GLRenderProgram.Link*.cs`, `GLRenderProgram.CompileInputs.cs`, `GLRenderProgram.AsyncResults.cs`, `GLRenderProgram.HazardDetection.cs`, `GLRenderProgram.BinaryCacheInteraction.cs` |
| `OpenGL/Enums/*` | `OpenGL/Types/*` |

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
- `XRMesh` has no standalone OpenGL or Vulkan API wrapper. `GLMeshRenderer` and `VkMeshRenderer` own mesh draw readiness, mesh data invalidation, vertex/index/deformation buffer collection, and topology-specific draw submission; `GLDataBuffer` and `VkDataBuffer` own the underlying buffer upload/readiness state.
- Keep the no-standalone-wrapper decision until geometry layout lifetime is duplicated across CPU direct, GPU indirect, material-table, and meshlet paths enough that a real backend mesh resource is cleaner.

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
