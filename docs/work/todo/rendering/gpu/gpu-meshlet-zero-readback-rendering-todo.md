# GPU Meshlet Zero-Readback Rendering TODO

Last Updated: 2026-05-18
Owner: Rendering
Status: active planning tracker
Target Branch: `rendering-gpu-meshlet-zero-readback`

Source design:

- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)

Related docs:

- [Production rendering pipeline roadmap](production-rendering-pipeline-roadmap.md)
- [GPU rendering TODO](gpu-rendering.md)
- [Zero-readback GPU-driven rendering plan](../../../design/rendering/zero-readback-gpu-driven-rendering-plan.md)
- [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Model Import Cooked Asset Cache TODO](../../assets/model-import-binary-cache-todo.md)
- [Model Import Binary Cache Design](../../../design/assets/model-import-binary-cache-design.md)

## Goal

Promote `GpuMeshlet` from an experimental mesh-shader path into a production mesh submission strategy that shares the existing GPU-resident scene database and zero-readback render pipeline.

The production path should be:

- import/cache-generated meshes, LODs, and meshlets
- `GPUScene`-owned meshlet ranges, descriptors, vertex-reference indices, and triangle-local indices
- shared GPU culling, LOD selection, material table, transform, bounds, and visibility buffers
- GPU expansion from visible draw commands into meshlet task records
- backend mesh-task dispatch from GPU-written counts
- task/mesh shaders that perform pass-correct culling and material-table shading

`GpuIndirectZeroReadback` remains the shipping baseline and fallback where production mesh shader support is unavailable.

## Non-Negotiable Rules

- [ ] Create a dedicated branch before implementation, for example `rendering-gpu-meshlet-zero-readback`.
- [ ] Keep `GpuMeshlet` zero-readback in steady-state Release frames: no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, render-thread `WaitForGpu`, or equivalent count/visibility readback.
- [ ] Do not allow implicit CPU mesh rendering fallback from production `GpuMeshlet`.
- [ ] Unsupported production meshlet backends must fall back visibly to `GpuIndirectZeroReadback` when available.
- [ ] Keep direct CPU-count mesh task dispatch diagnostic-only; it must not satisfy the shipping `GpuMeshlet` strategy.
- [ ] Generate and cache meshlets during import/cache repair, not first render.
- [ ] Store production meshlet data in `GPUScene`, keyed by `MeshDataID` and LOD, not in `MeshletCollection` sidecar ownership.
- [ ] Share `DrawMetadataBuffer`, `TransformBuffer`, `PrevTransformBuffer`, `BoundsBuffer`, `MaterialStateBuffer`, `LODTableBuffer`, atlas tiers, material table, and GPU visibility results with the traditional indirect path.
- [ ] Use `SetField(...)` when hydrating or mutating any `XRBase`-derived type touched by import/cache work.
- [ ] Keep render submission, expansion, culling, and shader-binding hot paths allocation-free.
- [ ] New code must compile without new warnings.

## Success Criteria

- [ ] `GpuMeshlet` renders B1 and B2 with visual parity against `GpuIndirectZeroReadback`, within expected material/pass tolerances.
- [ ] `Engine.Rendering.Stats.GpuReadbackBytes` remains zero for steady-state `GpuMeshlet` Release frames.
- [ ] Meshlet data is stored in `GPUScene` and keyed by `MeshDataID`/LOD, with no production dependency on `MeshletCollection` sidecar ownership.
- [ ] Meshlet dispatch consumes GPU-written counts.
- [ ] Task shaders perform frustum culling, cone culling, and primary-view Hi-Z culling where enabled.
- [ ] Meshlet materials render through the same material-table/bindless policy as the zero-readback indirect path.
- [ ] Unsupported hardware falls back to `GpuIndirectZeroReadback` with clear diagnostics.
- [ ] Fresh model binary caches provide meshlets and LODs without reading the original third-party source.
- [ ] Dense geometry performance is at least within 10 percent of indirect rendering, and ahead on geometry-heavy scenes.

## Primary Code Areas

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection*.cs`
- `XREngine.Runtime.Rendering/Rendering/Meshlets/`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs`
- `XREngine.Runtime.Rendering/Rendering/Models/Meshes/`
- `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp`
- `Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task`
- `Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh`
- `Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinned.mesh`
- `XREngine.UnitTests/`
- `docs/architecture/rendering/mesh-submission-strategies.md`
- `docs/features/model-import.md`

## Phase 0: Branch, Baseline, And Scope Lock

**Goal:** isolate the meshlet production work and record the current fallback and bring-up behavior before changing strategy resolution.

- [ ] Create the dedicated branch `rendering-gpu-meshlet-zero-readback`.
- [ ] Confirm current `GpuMeshlet` resolution behavior with the active renderer and record whether it falls back before `VPRC_RenderMeshesPassMeshlet` runs.
- [ ] Capture current `MeshletCollection` buffer ownership, direct `DrawMeshTask(0, numGroups)` dispatch behavior, and shader support.
- [ ] Capture current B1/B2 `GpuIndirectZeroReadback` baseline logs and `GpuReadbackBytes` status for comparison.
- [ ] Decide whether OpenGL NV mesh shaders are diagnostic-only for v1 if indirect-count task dispatch is unavailable.
- [ ] Decide whether streaming-tier/editable meshes route through traditional indirect rendering in the first production milestone.
- [ ] List hardware/backend validation targets available locally: OpenGL NV, OpenGL EXT, Vulkan EXT, or fallback-only.

### Exit Criteria

- [ ] Branch exists.
- [ ] Current meshlet bring-up path and fallback behavior are recorded.
- [ ] Backend scope for the first production milestone is explicit.

## Phase 1: Capability And Strategy Honesty

**Goal:** make `GpuMeshlet` resolution tell the truth about whether a backend can run a production zero-readback meshlet path.

- [ ] Add an explicit mesh shader dialect model: `None`, `OpenGLNV`, `OpenGLEXT`, and `VulkanEXT`.
- [ ] Split capability probes so direct mesh task dispatch and indirect-count mesh task dispatch are distinct capabilities.
- [ ] Return `SupportsMeshletDispatch()` only when the selected dialect has matching shader sources and production dispatch functions.
- [ ] Keep OpenGL NV direct dispatch available only under a diagnostic or bring-up mode if indirect-count dispatch is not available.
- [ ] Update strategy logs and profiler labels to report requested strategy, selected strategy, backend dialect, and fallback reason.
- [ ] Ensure forced `GpuMeshlet` on unsupported hardware warns and falls back to `GpuIndirectZeroReadback` when available.
- [ ] Ensure strict no-fallback profiles skip GPU mesh submission with a visible warning if neither production meshlet nor zero-readback indirect can run.
- [ ] Add resolver tests for supported, unsupported, diagnostic-only, and strict fallback combinations.
- [ ] Update [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md) if the capability contract changes.

### Exit Criteria

- [ ] `GpuMeshlet` cannot silently mean "experimental CPU-count mesh shader path."
- [ ] Unsupported `GpuMeshlet` requests produce visible, tested fallback behavior.

## Phase 2: Import-Generated Meshlet Payloads

**Goal:** make meshlet generation an import/cache responsibility instead of a first-render responsibility.

- [ ] Coordinate with [Model Import Cooked Asset Cache TODO](../../assets/model-import-binary-cache-todo.md) Phase 5 before changing runtime expectations.
- [ ] Define a serialized CPU meshlet descriptor distinct from the GPU descriptor.
- [ ] Add meshlet cone axis, cutoff, and apex data, or a documented compressed equivalent, to cached payloads.
- [ ] Include meshoptimizer version, meshlet generation settings, LOD settings, and source mesh identity in cache freshness.
- [ ] Serialize meshlet descriptors, vertex-reference indices, triangle-local indices, bounds, cones, settings, and meshoptimizer stats.
- [ ] Represent disabled meshlet generation as empty chunks plus an explicit manifest flag.
- [ ] Support stale or missing meshlet chunk repair from cached mesh chunks without parsing the third-party source model.
- [ ] Add tests proving warm-cache startup does not call `MeshletGenerator.Build` or equivalent render-startup generation.
- [ ] Update `docs/features/model-import.md` if cache behavior or user-visible import controls change.

### Exit Criteria

- [ ] Fresh model caches hydrate meshlet and LOD payloads without source model access.
- [ ] Warm-cache render startup consumes cached meshlets instead of rebuilding them.

## Phase 3: GPUScene Meshlet Storage

**Goal:** move production meshlet data into the shared GPU scene database.

- [ ] Add `GpuMeshletRange` or equivalent range metadata keyed by `MeshDataID`.
- [ ] Add `MeshletRangeBuffer`, `MeshletDescriptorBuffer`, `MeshletVertexIndexBuffer`, and `MeshletTriangleIndexBuffer` ownership to `GPUScene`.
- [ ] Track meshlet ranges per mesh and per LOD, with `MeshletCount = 0` for meshes that route through traditional indirect rendering.
- [ ] Upload cached meshlet descriptor/index slices during mesh registration.
- [ ] Keep range IDs stable unless the mesh is removed, migrated, compacted, or cache data changes.
- [ ] Add dirty tracking so unchanged meshlet ranges are not reuploaded.
- [ ] Make `MeshletCollection` a debug/compatibility view or retire it from production execution.
- [ ] Remove production dependency on `RebuildMeshletsFromUpdatingCommands`.
- [ ] Add source-contract tests proving production meshlet data is `GPUScene`-owned.

### Exit Criteria

- [ ] Meshlet descriptors and indices live in scene-owned buffers.
- [ ] `MeshletCollection` no longer owns production meshlet rendering state.

## Phase 4: Shared Visibility And LOD Integration

**Goal:** feed meshlet rendering from the same GPU visibility and LOD decisions as traditional indirect rendering.

- [ ] Ensure meshlet passes consume the GPU culling output buffer, not CPU command visibility.
- [ ] Ensure `GPURenderLODSelect.comp` writes selected `MeshID` and `LODLevel` for meshlet expansion.
- [ ] Verify `LODTableBuffer` and meshlet ranges agree for all resident LODs.
- [ ] Preserve `DrawID` as the bridge to `DrawMetadata`, material rows, LOD transition state, and debug counters.
- [ ] Decide how dithered LOD transitions append previous-LOD meshlet ranges: flagged records or separate streams.
- [ ] Route meshes with missing meshlet data through traditional zero-readback indirect rendering.
- [ ] Add tests that visible command buffers and LOD-selected mesh IDs drive meshlet expansion.

### Exit Criteria

- [ ] Meshlet rendering does not rebuild visibility on CPU.
- [ ] Meshlet range selection follows GPU LOD output.

## Phase 5: GPU Meshlet Expansion

**Goal:** convert visible draw commands into compact meshlet task records entirely on the GPU.

- [ ] Add `GPURenderExpandMeshlets.comp`.
- [ ] Define and upload `GpuMeshletTaskRecord` with meshlet index, `DrawID`, `TransformID`, and `MaterialID`.
- [ ] Add per-pass `VisibleMeshletTaskBuffer`, `VisibleMeshletTaskCountBuffer`, and `MeshletDispatchIndirectBuffer`.
- [ ] Reset meshlet task counters on GPU at pass start.
- [ ] Expand one task record per visible selected-LOD meshlet.
- [ ] Append previous-LOD meshlet records for active transitions once transition policy is ready.
- [ ] Add conservative bounds checks and an overflow flag instead of writing past capacity.
- [ ] Add task capacity sizing policy and overflow telemetry.
- [ ] Add tests for empty ranges, large ranges, LOD ranges, overflow, and draw metadata preservation.

### Exit Criteria

- [ ] Meshlet task records are generated by compute from GPU-visible commands.
- [ ] Expansion overflow is detected and reported without memory corruption.

## Phase 6: Indirect-Count Mesh Task Dispatch

**Goal:** submit task shaders from GPU-written counts through backend wrappers.

- [ ] Add renderer abstraction for `DrawMeshTasksIndirectCount` or the backend equivalent.
- [ ] Map Vulkan to `vkCmdDrawMeshTasksIndirectCountEXT`.
- [ ] Map OpenGL EXT to `glDrawMeshTasksIndirectCountEXT` or the available extension-equivalent entry point.
- [ ] Keep OpenGL NV production-disabled unless an equivalent indirect/count path is available and validated.
- [ ] Ensure dispatch count, stride, max groups, offsets, and resource barriers are backend-validated.
- [ ] Ensure CPU-specified direct mesh task counts are diagnostic-only and cannot satisfy production `GpuMeshlet`.
- [ ] Add backend capability and dispatch tests with mock/fake renderer coverage where hardware tests are unavailable.

### Exit Criteria

- [ ] Production `GpuMeshlet` dispatch uses GPU-written counts.
- [ ] Backends without production count dispatch cannot accidentally select production `GpuMeshlet`.

## Phase 7: Production Task And Mesh Shaders

**Goal:** make task/mesh shader execution consume task records and shared scene/material data.

- [ ] Convert `MeshletCulling.task` to consume `GpuMeshletTaskRecord` instead of scanning a full scene meshlet set.
- [ ] Transform local meshlet bounds through `TransformBuffer`.
- [ ] Add per-meshlet frustum culling.
- [ ] Add cone-backface culling from cooked cone data.
- [ ] Add primary-view Hi-Z occlusion where enabled and valid.
- [ ] Add pass controls for culling modes: depth prepass, shadow, opaque, masked, transparent, velocity, and stereo.
- [ ] Convert `MeshletRender.mesh` and `MeshletRenderSkinned.mesh` to read atlas-backed vertex/index streams.
- [ ] Emit the same interpolants expected by existing material shaders: world position, previous world position, normal/tangent frame, UVs, vertex color, and flat `DrawID` or material row index.
- [ ] Add static and skinned variants for opaque, alpha-tested, depth/normal, shadow depth, velocity, and stereo where supported.
- [ ] Add shader source-contract tests for task-record, material-table, transform, previous-transform, and Hi-Z bindings.

### Exit Criteria

- [ ] Task shaders cull task records pass-correctly.
- [ ] Mesh shaders emit material-compatible geometry from shared atlas streams.

## Phase 8: Shared Material And Pass Coverage

**Goal:** make meshlets render through the same material-table policy and pass semantics as the zero-readback indirect path.

- [ ] Remove production dependency on `MeshletMaterial`.
- [ ] Fetch material state through `MaterialStateBuffer`, material texture handle table, and bindless/descriptor-indexed texture paths.
- [ ] Preserve render-pass filtering, state class, layer/editor visibility, material identity, transforms, skinning, shadows, depth prepass, velocity, and stereo semantics.
- [ ] Validate opaque static and skinned meshlet material-table rendering.
- [ ] Validate alpha-tested static and skinned meshlet material-table rendering.
- [ ] Validate shadow depth and depth/normal meshlet passes.
- [ ] Validate velocity output with `PrevTransformBuffer`.
- [ ] Validate stereo behavior, including Hi-Z disable or stereo-safe Hi-Z rules.
- [ ] Keep unsupported passes routed through traditional zero-readback indirect rendering with visible counters.

### Exit Criteria

- [ ] Meshlet path and indirect path share material-table/bindless policy.
- [ ] Pass coverage is explicit; unsupported pass routing is visible.

## Phase 9: Diagnostics, Counters, And Tests

**Goal:** make correctness, fallback, readback violations, and performance visible.

- [ ] Add counters for requested meshlet strategy frames, production meshlet frames, and meshlet fallback frames.
- [ ] Add counters for task records emitted and records culled by frustum, cone, and Hi-Z.
- [ ] Add counters for expansion overflow, meshlet buffer bytes resident, and cache hit/miss/stale counts.
- [ ] Add structured logs for `Meshlet.BackendSelected`, `Meshlet.BackendUnsupported`, `Meshlet.SceneBufferUpload`, `Meshlet.ExpandOverflow`, `Meshlet.DispatchSkipped`, `Meshlet.CacheMissing`, and `Meshlet.CacheStale`.
- [ ] Include render pass, requested strategy, selected strategy, backend dialect, source model/cache path, command count, meshlet count, and capacity where relevant.
- [ ] Add source-contract tests forbidding readback helpers in production meshlet paths.
- [ ] Add tests proving `GpuMeshlet` does not CPU-fallback render meshes.
- [ ] Add integration tests for GL NV/EXT and Vulkan EXT where hardware is available.
- [ ] Add fallback-only tests for environments without mesh shader support.

### Exit Criteria

- [ ] Unsupported, overflow, stale-cache, and skipped-dispatch cases are visible in logs and counters.
- [ ] Test coverage protects zero-readback and no-CPU-fallback contracts.

## Phase 10: Validation And Closeout

**Goal:** prove the production strategy is correct, stable, and worth enabling where supported.

- [ ] Run targeted unit/source-contract tests for capability selection, fallback, no readbacks, GPUScene storage, LOD range selection, task-record expansion, shader bindings, and material-table routing.
- [ ] Run B1 two-Sponzas visual parity against `GpuIndirectZeroReadback`.
- [ ] Run B2 B1-plus-idle-skinned-avatars visual parity against `GpuIndirectZeroReadback`.
- [ ] Run high-material-diversity validation.
- [ ] Run dense static geometry validation and record performance versus `GpuIndirectZeroReadback`.
- [ ] Run masked foliage validation.
- [ ] Run stereo OpenVR/OpenXR smoke when backend support is available.
- [ ] Run 100K command stress with `Stats.GpuReadbackBytes == 0`.
- [ ] Run a 30 minute `GpuMeshlet` Release soak.
- [ ] Run OpenGL/Vulkan parity tests where backend support is available.
- [ ] Run the narrowest useful build or test command if full validation is blocked.
- [ ] Report unrelated build/test failures instead of hiding them in this tracker.
- [ ] Merge `rendering-gpu-meshlet-zero-readback` back into `main` after implementation, validation, and documentation updates are complete.

## Suggested Test Names

- [ ] `GpuMeshlet_BackendUnsupported_FallsBackToZeroReadback`
- [ ] `GpuMeshlet_DiagnosticDirectDispatch_DoesNotSatisfyProductionStrategy`
- [ ] `GpuMeshlet_NoCpuRenderFallback`
- [ ] `GpuMeshlet_NoReadbackHelpersInShippingPath`
- [ ] `GpuMeshlet_StatsGpuReadbackBytesRemainZero`
- [ ] `MeshletData_StoredInGPUScene_NotMeshletCollectionOwner`
- [ ] `MeshletRange_PerLODMeshDataID`
- [ ] `MeshletRegistration_UsesCachedPayload_NoRuntimeBuild`
- [ ] `MeshletExpand_UsesCulledCommandBufferAndLODSelection`
- [ ] `MeshletExpand_OverflowSetsFlagWithoutWritingPastCapacity`
- [ ] `MeshletDispatch_UsesGpuWrittenCount`
- [ ] `MeshletTaskShader_ConsumesTaskRecords`
- [ ] `MeshletTaskShader_ConeCullUsesCookedConeData`
- [ ] `MeshletTaskShader_HiZBindingsPresentWhenEnabled`
- [ ] `Meshlet_RenderPath_ProducesCorrectOutput_NVMeshShader`
- [ ] `Meshlet_RenderPath_ProducesCorrectOutput_EXTMeshShader`
- [ ] `Meshlet_VulkanEXT_Parity`
- [ ] `Meshlet_TaskShaderCull_MatchesBVHFrustumResults`
- [ ] `Meshlet_SharedBVHCull_ThenMeshletExpansion`
- [ ] `Meshlet_LODIntegration_CorrectMeshletRangePerLOD`
- [ ] `Meshlet_BindlessMaterials_TextureCorrect`
- [ ] `Meshlet_SkinnedMesh_UsesGpuResidentSkinningAndBounds`
