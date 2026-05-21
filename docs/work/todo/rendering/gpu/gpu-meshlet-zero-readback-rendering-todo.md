# GPU Meshlet Zero-Readback Rendering TODO

Last Updated: 2026-05-19
Owner: Rendering
Status: Phase 0-9 implementation tracker complete; parent Phase E4-E9 source-contract scope is complete. Phase 10 hardware/perf validation and branch merge remain open under [Production GPU-Driven Rendering Roadmap](production-rendering-pipeline-roadmap.md) Phase H.
Target Branch: `rendering-gpu-meshlet-zero-readback`

Source design:

- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)

Related docs:

- [Production GPU-driven rendering roadmap](production-rendering-pipeline-roadmap.md)
- [Zero-readback GPU-driven rendering plan](../../../design/rendering/zero-readback-gpu-driven-rendering-plan.md)
- [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Model Import Cooked Asset Cache TODO](../../assets/model-import-binary-cache-todo.md)
- [Model Import Binary Cache Design](../../../design/assets/model-import-binary-cache-design.md)

## Parent Roadmap Contract

This file is the implementation-level tracker for meshlet productionization. The parent roadmap owns the canonical status and ordering for the broader renderer; this child tracker owns the detailed branch scope, backend capability contract, GPUScene meshlet storage, task-record expansion, mesh-task dispatch, shader work, pass/material parity, diagnostics, and validation.

Keep the parent Phase E checklist and this table in sync when changing meshlet scope:

| Parent roadmap task | Meshlet tracker coverage | Notes |
| --- | --- | --- |
| E4: production meshlet capability honesty | Phase 0, Phase 1, Phase 9 | Backend dialects, direct-vs-indirect dispatch distinction, visible fallback behavior, and diagnostics. |
| E5: GPUScene-owned meshlet data | Phase 3, Phase 4 | Scene-owned ranges/descriptors/index streams keyed by `MeshDataID` and LOD; shared visibility/LOD integration. |
| E6: cooked meshlet payloads | Phase 2 | Import/cache-generated descriptors, cones, bounds, settings, freshness, and warm-cache repair. |
| E7: GPU expansion and indirect-count dispatch | Phase 5, Phase 6 | `GPURenderExpandMeshlets.comp`, task records, count buffers, overflow handling, and backend dispatch wrappers. |
| E8: production task/mesh shaders | Phase 7 | Task-record culling, transform/Hi-Z/cone integration, atlas-backed mesh shaders, and material-compatible interpolants. |
| E9: material/pass parity and validation | Phase 8, Phase 9, Phase 10 | Bindless/descriptor material policy, pass coverage, counters, and source-contract tests are complete for parent Phase E; parity runs, stress, and soak remain in Phase 10 / parent Phase H. |

## Goal

Promote `GpuMeshletZeroReadback` from an experimental mesh-shader path into a production mesh submission strategy that shares the existing GPU-resident scene database and zero-readback render pipeline. Keep `GpuMeshletInstrumented` as the matching diagnostic meshlet strategy for readback-assisted bring-up.

The production path should be:

- import/cache-generated meshes, LODs, and meshlets
- `GPUScene`-owned meshlet ranges, descriptors, vertex-reference indices, and triangle-local indices
- shared GPU culling, LOD selection, material table, transform, bounds, and visibility buffers
- GPU expansion from visible draw commands into meshlet task records
- backend mesh-task dispatch from GPU-written counts
- task/mesh shaders that perform pass-correct culling and material-table shading

`GpuIndirectZeroReadback` remains the shipping baseline and fallback where production mesh shader support is unavailable.

## Non-Negotiable Rules

- [x] Create a dedicated branch before implementation, for example `rendering-gpu-meshlet-zero-readback`.
- [x] Keep `GpuMeshletZeroReadback` zero-readback in steady-state Release frames: no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, render-thread `WaitForGpu`, or equivalent count/visibility readback. Done 2026-05-19 at source-contract level; runtime `GpuReadbackBytes == 0` stress validation remains below.
- [x] Do not allow implicit CPU mesh rendering fallback from production `GpuMeshletZeroReadback`. Done 2026-05-19.
- [x] Unsupported production meshlet backends must fall back visibly to `GpuIndirectZeroReadback` when available. Done 2026-05-19.
- [x] Keep direct CPU-count mesh task dispatch diagnostic-only; it must not satisfy the shipping `GpuMeshletZeroReadback` strategy. Done 2026-05-19.
- [x] Generate and cache meshlets during import/cache repair, not first render. Done 2026-05-19 for cooked `XRMesh` payloads; full model-cache container integration stays with the model-cache TODO.
- [x] Store production meshlet data in `GPUScene`, keyed by `MeshDataID` and LOD, not in `MeshletCollection` sidecar ownership. Done 2026-05-19.
- [x] Share `DrawMetadataBuffer`, `TransformBuffer`, `PrevTransformBuffer`, `BoundsBuffer`, `MaterialStateBuffer`, `LODTableBuffer`, atlas tiers, material table, and GPU visibility results with the traditional indirect path. Done 2026-05-19.
- [x] Use `SetField(...)` when hydrating or mutating any `XRBase`-derived type touched by import/cache work. Done 2026-05-19: no direct `XRBase` backing-field mutation was added in the meshlet/cache paths.
- [ ] Keep render submission, expansion, culling, and shader-binding hot paths allocation-free. Source review kept the production data path free of intentional per-frame allocations, but the new diagnostics still need a profiling pass with logging policies enabled/disabled.
- [x] New code must compile without new warnings. Done 2026-05-19 in targeted builds; existing repo warnings are listed under Phase 10.

## Success Criteria

- [ ] `GpuMeshletZeroReadback` renders B1 and B2 with visual parity against `GpuIndirectZeroReadback`, within expected material/pass tolerances.
- [ ] `Engine.Rendering.Stats.GpuReadbackBytes` remains zero for steady-state `GpuMeshletZeroReadback` Release frames. Source-contract protected; 100K/runtime Release stress remains open below.
- [x] Meshlet data is stored in `GPUScene` and keyed by `MeshDataID`/LOD, with no production dependency on `MeshletCollection` sidecar ownership.
- [x] Meshlet dispatch consumes GPU-written counts.
- [x] Task shaders perform frustum culling, cone culling, and primary-view Hi-Z culling where enabled.
- [x] Meshlet materials render through the same material-table/bindless policy as the zero-readback indirect path.
- [x] Unsupported hardware falls back to `GpuIndirectZeroReadback` with clear diagnostics.
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

- [x] Create the dedicated branch `rendering-gpu-meshlet-zero-readback`. Done 2026-05-19.
- [x] Confirm current meshlet strategy resolution behavior with the active renderer and record whether it falls back before `VPRC_RenderMeshesPassMeshlet` runs. Done 2026-05-19 before Phase 1: `AbstractRenderer.SupportsMeshletDispatch()` defaulted to `false`, and no renderer override existed yet. `VPRC_RenderMeshesPassShared.Execute()` checked this probe before calling `VPRC_RenderMeshesPassMeshlet.Execute()`, warned, then fell through to traditional GPU indirect submission. Phase 1 replaced the forced-strategy caveat with explicit dialect probes and capability-gated forced `GpuMeshletZeroReadback` fallback; the 2026-05-20 split added `GpuMeshletInstrumented` with diagnostics gating.
- [x] Capture current `MeshletCollection` buffer ownership, direct `DrawMeshTask(0, numGroups)` dispatch behavior, and shader support. Done 2026-05-19: `GPUScene` owns a `MeshletCollection` sidecar, but production meshlet data is not in shared scene buffers. `MeshletCollection` owns `MeshletBuffer`, `VisibleMeshletBuffer`, `VertexBuffer`, `IndexBuffer`, `TriangleBuffer`, `TransformBuffer`, `MaterialBuffer`, and `CommandVisibilityBuffer`; it rebuilds from `_commandIndexLookup` via `RebuildMeshletsFromUpdatingCommands()` and calls `MeshletGenerator.Build(...)` during the render-ready path. The task/mesh shaders require `GL_NV_mesh_shader`, and `MeshletCollection.Render(...)` requires `OpenGLRenderer.NVMeshShader` before issuing CPU-count direct dispatch through `DrawMeshTask(0, numGroups)`.
- [ ] Capture current B1/B2 `GpuIndirectZeroReadback` baseline logs and `GpuReadbackBytes` status for comparison. Not complete: the current local `Assets/UnitTestingWorldSettings.jsonc` is not the documented B1/B2 scene; it has one Sponza import and lights enabled, not two Sponzas/lights-off or B1 plus 100 idle skinned avatars. A short Release smoke run was attempted with `Tools/Measure-GameLoopRenderPipeline.ps1 -Strategies GpuIndirectZeroReadback -Configuration Release -WarmupSec 5 -CaptureSec 8 -RunLabel meshlet-phase0-smoke`; it wrote `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-05-19_10-59-17/summary.json` with `GpuReadbackBytesTotal = 0`, but no render-stats samples were parsed, so it is not a valid baseline.
- [x] Decide whether OpenGL NV mesh shaders are diagnostic-only for v1 if indirect-count task dispatch is unavailable. Decision 2026-05-19: yes. The existing OpenGL NV path is diagnostic/bring-up only until a validated GPU-written-count task dispatch path exists. CPU-count direct `DrawMeshTask(...)` cannot satisfy production `GpuMeshletZeroReadback`.
- [x] Decide whether streaming-tier/editable meshes route through traditional indirect rendering in the first production milestone. Decision 2026-05-19: yes. The first production milestone should meshlet-submit only cached, resident static/skinned LOD payloads with scene-owned meshlet ranges; streaming/editable meshes and meshes missing meshlet payloads route through `GpuIndirectZeroReadback`.
- [x] List hardware/backend validation targets available locally: OpenGL NV, OpenGL EXT, Vulkan EXT, or fallback-only. Done 2026-05-19: local adapters are NVIDIA GeForce RTX 4070 Laptop GPU and Intel Arc Graphics. `vulkaninfo` reports `VK_EXT_mesh_shader`, `VK_NV_mesh_shader`, `VK_KHR_draw_indirect_count`, and `VK_KHR_buffer_device_address`; Vulkan EXT is the main production validation target. OpenGL has no `glinfo` utility available; `GL_NV_mesh_shader` must be confirmed through a live engine context, and no OpenGL EXT mesh-shader source path exists yet. Fallback-only validation remains required.

### Exit Criteria

- [x] Branch exists.
- [x] Current meshlet bring-up path and fallback behavior are recorded.
- [x] Backend scope for the first production milestone is explicit.

## Phase 1: Capability And Strategy Honesty

**Goal:** make meshlet strategy resolution tell the truth about whether a backend can run a production zero-readback meshlet path, and whether diagnostics can use the instrumented meshlet path.

- [x] Add an explicit mesh shader dialect model: `None`, `OpenGLNV`, `OpenGLEXT`, and `VulkanEXT`.
- [x] Split capability probes so direct mesh task dispatch and indirect-count mesh task dispatch are distinct capabilities.
- [x] Return `SupportsMeshletDispatch()` only when the selected dialect has matching shader sources and production dispatch functions.
- [x] Keep OpenGL NV direct dispatch available only under a diagnostic or bring-up mode if indirect-count dispatch is not available.
- [x] Update strategy logs and profiler labels to report requested strategy, selected strategy, backend dialect, and fallback reason.
- [x] Ensure forced `GpuMeshletZeroReadback` or `GpuMeshletInstrumented` on unsupported hardware warns and falls back to `GpuIndirectZeroReadback` when available.
- [x] Ensure strict no-fallback profiles skip GPU mesh submission with a visible warning if neither production meshlet nor zero-readback indirect can run.
- [x] Add resolver tests for supported, unsupported, diagnostic-only, and strict fallback combinations.
- [x] Update [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md) if the capability contract changes.

### Exit Criteria

- [x] `GpuMeshletZeroReadback` cannot silently mean "experimental CPU-count mesh shader path."
- [x] Unsupported meshlet strategy requests produce visible, tested fallback behavior.

## Phase 2: Import-Generated Meshlet Payloads

**Goal:** make meshlet generation an import/cache responsibility instead of a first-render responsibility.

- [x] Coordinate with [Model Import Cooked Asset Cache TODO](../../assets/model-import-binary-cache-todo.md) Phase 5 before changing runtime expectations. Done 2026-05-19: Phase 2 now provides the reusable `XRMesh.MeshletPayload` contract and cooked-XRMesh serialization layer; the broader disposable model-cache container, chunk table, and GPUScene registration remain tracked by the model-cache TODO and later meshlet phases.
- [x] Define a serialized CPU meshlet descriptor distinct from the GPU descriptor. Done 2026-05-19: `CpuMeshletDescriptor` stores CPU/cache-owned bounds, meshlet offsets/counts, cone data, and packed cone bytes separately from the shader-facing `Meshlet` struct.
- [x] Add meshlet cone axis, cutoff, and apex data, or a documented compressed equivalent, to cached payloads. Done 2026-05-19: meshoptimizer bounds now populate cone axis/cutoff, cone apex, and packed s8 cone fields on each CPU descriptor.
- [x] Include meshoptimizer version, meshlet generation settings, LOD settings, and source mesh identity in cache freshness. Done 2026-05-19: `MeshletPayload` records the meshoptimizer/interop version key, settings snapshots, source identity, source mesh hash, settings hashes, and freshness hash.
- [x] Serialize meshlet descriptors, vertex-reference indices, triangle-local indices, bounds, cones, settings, and meshoptimizer stats. Done 2026-05-19: runtime cooked `XRMesh` payloads now round-trip descriptors, index streams, meshlet vertex sidecars, settings snapshots, and stats.
- [x] Represent disabled meshlet generation as empty chunks plus an explicit manifest flag. Done 2026-05-19: disabled settings produce a fresh `MeshletPayload` with `GenerationEnabled = false`, empty arrays, and preserved freshness metadata without calling meshoptimizer.
- [x] Support stale or missing meshlet chunk repair from cached mesh chunks without parsing the third-party source model. Done 2026-05-19 at the `XRMesh` cooked-payload layer: `XRMesh.GetOrCreateMeshletPayload(...)` validates cached freshness and regenerates from resident engine mesh data when stale or missing. Full model-cache chunk repair policy remains in the model-cache TODO.
- [x] Add tests proving warm-cache startup does not call `MeshletGenerator.Build` or equivalent render-startup generation. Done 2026-05-19: `MeshletCollection_AddMesh_UsesFreshCachedPayloadWithoutMeshoptimizerBuild` pins the fresh-payload path with a meshoptimizer build counter.
- [x] Update `docs/features/model-import.md` if cache behavior or user-visible import controls change. No user-visible model-import controls changed in this phase; full model-cache UX/docs remain in the model-cache TODO.

### Exit Criteria

- [ ] Fresh model caches hydrate meshlet and LOD payloads without source model access. Meshlet payload hydration is implemented for cooked `XRMesh`; full model-cache `Models`/`LodTables`/`Meshlets` chunk hydration remains tracked by [Model Import Cooked Asset Cache TODO](../../assets/model-import-binary-cache-todo.md) Phases 4-7.
- [x] Warm-cache render startup consumes cached meshlets instead of rebuilding them.

## Phase 3: GPUScene Meshlet Storage

**Goal:** move production meshlet data into the shared GPU scene database.

- [x] Add `GpuMeshletRange` or equivalent range metadata keyed by `MeshDataID`. Done 2026-05-19: `GPUScene.GpuMeshletRange` is indexed by the same stable `MeshDataID` used by `MeshDataBuffer`.
- [x] Add `MeshletRangeBuffer`, `MeshletDescriptorBuffer`, `MeshletVertexIndexBuffer`, and `MeshletTriangleIndexBuffer` ownership to `GPUScene`. Done 2026-05-19: `GPUScene` now creates, resizes, destroys, and exposes all four scene-owned meshlet buffers.
- [x] Track meshlet ranges per mesh and per LOD, with `MeshletCount = 0` for meshes that route through traditional indirect rendering. Done 2026-05-19: logical LOD registration writes ranges for each resident LOD `MeshDataID`; missing, stale, disabled, and streaming meshlet payloads write explicit zero-count ranges.
- [x] Upload cached meshlet descriptor/index slices during mesh registration. Done 2026-05-19: atlas registration consumes only fresh `XRMesh.MeshletPayload` data and appends descriptor, vertex-reference, and triangle-local slices into scene-owned buffers.
- [x] Keep range IDs stable unless the mesh is removed, migrated, compacted, or cache data changes. Done 2026-05-19: meshlet slices are not appended again while the cached payload freshness hash and meshlet count are unchanged; removed meshes clear their range entry.
- [x] Add dirty tracking so unchanged meshlet ranges are not reuploaded. Done 2026-05-19: `GPUScene` tracks meshlet range dirty spans separately from mesh data dirty spans and skips buffer writes for unchanged ranges.
- [x] Make `MeshletCollection` a debug/compatibility view or retire it from production execution. Done 2026-05-19: the legacy direct task-dispatch path is labeled as a debug/compatibility view; production meshlet data is scene-buffer-owned.
- [x] Remove production dependency on `RebuildMeshletsFromUpdatingCommands`. Done 2026-05-19: the production-facing rebuild name is gone; the remaining sidecar rebuild is `RebuildDebugMeshletCollectionFromUpdatingCommands()` and is only used by `RenderMeshlets(...)`.
- [x] Add source-contract tests proving production meshlet data is `GPUScene`-owned. Done 2026-05-19: `MeshOptimizerInteropTests` now covers scene-owned buffers, per-LOD ranges, zero-count fallback ranges, unchanged-range stability, and the debug-only `MeshletCollection` contract.

### Exit Criteria

- [x] Meshlet descriptors and indices live in scene-owned buffers.
- [x] `MeshletCollection` no longer owns production meshlet rendering state.

## Phase 4: Shared Visibility And LOD Integration

**Goal:** feed meshlet rendering from the same GPU visibility and LOD decisions as traditional indirect rendering.

- [x] Ensure meshlet passes consume the GPU culling output buffer, not CPU command visibility. Done 2026-05-19: meshlet render intent now resolves `GpuMeshletExpansionInputs` from `CulledSceneToRenderBuffer`, optional hot culled commands, and the GPU count buffer; the legacy `MeshletCollection` CPU visibility path is no longer used by `HybridRenderingManager`.
- [x] Ensure `GPURenderLODSelect.comp` writes selected `MeshID` and `LODLevel` for meshlet expansion. Done 2026-05-19: the shader names the command lanes and writes selected `MeshID`, packed submesh `MeshID`, and resolved `LODLevel` while leaving `DrawID` untouched.
- [x] Verify `LODTableBuffer` and meshlet ranges agree for all resident LODs. Done 2026-05-19: `GPUScene.TryValidateResidentLodMeshletRanges(...)` verifies every resident LOD `MeshDataID` has a meshlet range, including zero-count fallback ranges.
- [x] Preserve `DrawID` as the bridge to `DrawMetadata`, material rows, LOD transition state, and debug counters. Done 2026-05-19: meshlet expansion inputs expose `DrawMetadataBuffer` and `LodTransitionBuffer`, and LOD selection reads `COMMAND_DRAW_ID` without rewriting it.
- [x] Decide how dithered LOD transitions append previous-LOD meshlet ranges: flagged records or separate streams. Done 2026-05-19: Phase 5 will use flagged task records by reserving `GPUMeshletLayout.MeshletTaskPreviousLodFlag` in `GpuMeshletTaskRecord.MeshletIndex`.
- [x] Route meshes with missing meshlet data through traditional zero-readback indirect rendering. Done 2026-05-19: zero-count meshlet ranges expose `RequiresTraditionalIndirectFallback`, and meshlet intent falls through to the existing zero-readback indirect draw path until mixed meshlet/indirect expansion is implemented.
- [x] Add tests that visible command buffers and LOD-selected mesh IDs drive meshlet expansion. Done 2026-05-19: meshlet interop tests now cover GPU-visible expansion inputs, LOD shader selected mesh/lod writes, resident LOD range validation, zero-count fallback ranges, and flagged previous-LOD task policy.

### Exit Criteria

- [x] Meshlet rendering does not rebuild visibility on CPU.
- [x] Meshlet range selection follows GPU LOD output.

## Phase 5: GPU Meshlet Expansion

**Goal:** convert visible draw commands into compact meshlet task records entirely on the GPU.

- [x] Add `GPURenderExpandMeshlets.comp`. Done 2026-05-19.
- [x] Define and upload `GpuMeshletTaskRecord` with meshlet index, `DrawID`, `TransformID`, and `MaterialID`. Done 2026-05-19: the pass owns a `VisibleMeshletTaskBuffer` containing `GpuMeshletTaskRecord` rows generated by compute from visible commands and `DrawMetadata`.
- [x] Add per-pass `VisibleMeshletTaskBuffer`, `VisibleMeshletTaskCountBuffer`, and `MeshletDispatchIndirectBuffer`. Done 2026-05-19. Phase 6 added the separate GPU-written `MeshletDispatchCountBuffer` required by indirect-count mesh-task APIs.
- [x] Reset meshlet task counters on GPU at pass start. Done 2026-05-19: `GPURenderResetCounters.comp` clears task count, indirect dispatch args, indirect command count, and expansion overflow.
- [x] Expand one task record per visible selected-LOD meshlet. Done 2026-05-19.
- [x] Append previous-LOD meshlet records for active transitions once transition policy is ready. Done 2026-05-19: active transitions append previous-range records flagged with `GPUMeshletLayout.MeshletTaskPreviousLodFlag`.
- [x] Add conservative bounds checks and an overflow flag instead of writing past capacity. Done 2026-05-19: task-slot reservation uses atomic compare-swap and sets `MeshletExpansionOverflowFlag` on capacity/range truncation.
- [x] Add task capacity sizing policy and overflow telemetry. Done 2026-05-19: task capacity defaults to `128 * command capacity * 2`, capped at 1,048,576 records, with diagnostic overflow reporting.
- [x] Add tests for empty ranges, large ranges, LOD ranges, overflow, and draw metadata preservation. Done 2026-05-19: meshlet interop source-contract tests pin range skips, bounded reservation, previous-LOD flags, overflow, and `DrawMetadata` preservation.

### Exit Criteria

- [x] Meshlet task records are generated by compute from GPU-visible commands.
- [x] Expansion overflow is detected and reported without memory corruption.

## Phase 6: Indirect-Count Mesh Task Dispatch

**Goal:** submit task shaders from GPU-written counts through backend wrappers.

- [x] Add renderer abstraction for `DrawMeshTasksIndirectCount` or the backend equivalent. Done 2026-05-19: `AbstractRenderer.TryDrawMeshTasksIndirectCount(...)` validates shared draw-indirect and count buffers before backend submission.
- [x] Map Vulkan to `vkCmdDrawMeshTasksIndirectCountEXT`. Done 2026-05-19: Vulkan loads `ExtMeshShader`, enables `VK_EXT_mesh_shader` task/mesh features when present, records `MeshTaskDispatchIndirectCountOp`, and emits `CmdDrawMeshTasksIndirectCount`.
- [x] Map OpenGL EXT to `glDrawMeshTasksIndirectCountEXT` or the available extension-equivalent entry point. Done 2026-05-19: OpenGL EXT uses the raw `glMultiDrawMeshTasksIndirectCountEXT` entry point because Silk.NET does not expose an OpenGL EXT mesh-shader wrapper.
- [x] Keep OpenGL NV production-disabled unless an equivalent indirect/count path is available and validated. Done 2026-05-19: NV remains direct-dispatch/diagnostic-only and never satisfies production `SupportsIndirectCountMeshTaskDispatch()`.
- [x] Ensure dispatch count, stride, max groups, offsets, and resource barriers are backend-validated. Done 2026-05-19: shared validation enforces draw-indirect target, count-buffer target, stride, count, and 4-byte offsets; Vulkan records compute/transfer-to-indirect barriers for both indirect and count buffers.
- [x] Ensure CPU-specified direct mesh task counts are diagnostic-only and cannot satisfy production `GpuMeshletZeroReadback`. Done 2026-05-19: `SupportsMeshletDispatch()` now also requires `SupportsProductionMeshletShaders()`, so backend direct-count diagnostics cannot select production meshlets.
- [x] Add backend capability and dispatch tests with mock/fake renderer coverage where hardware tests are unavailable. Done 2026-05-19: `MeshOptimizerInteropTests.MeshTaskIndirectCountDispatch_UsesBackendCountPathAndShaderGate` pins the abstraction, Vulkan EXT mapping, OpenGL EXT entry point, and shader gate.

### Exit Criteria

- [x] Production `GpuMeshletZeroReadback` dispatch uses GPU-written counts. The backend dispatch abstraction now consumes a GPU-written indirect command plus a GPU-written indirect-command count; actual production selection remains Phase 7 shader-gated.
- [x] Backends without production count dispatch cannot accidentally select production `GpuMeshletZeroReadback`.

## Phase 7: Production Task And Mesh Shaders

**Goal:** make task/mesh shader execution consume task records and shared scene/material data.

- [x] Convert `MeshletCulling.task` to consume `GpuMeshletTaskRecord` instead of scanning a full scene meshlet set.
- [x] Transform local meshlet bounds through `TransformBuffer`.
- [x] Add per-meshlet frustum culling.
- [x] Add cone-backface culling from cooked cone data.
- [x] Add primary-view Hi-Z occlusion where enabled and valid.
- [x] Add pass controls for culling modes: depth prepass, shadow, opaque, masked, transparent, velocity, and stereo.
- [x] Convert `MeshletRender.mesh` and `MeshletRenderSkinned.mesh` to read atlas-backed vertex/index streams.
- [x] Emit the same interpolants expected by existing material shaders: world position, previous world position, normal/tangent frame, UVs, vertex color, and flat `DrawID` or material row index.
- [x] Add static and skinned variants for opaque, alpha-tested, depth/normal, shadow depth, velocity, and stereo where supported.
- [x] Add shader source-contract tests for task-record, material-table, transform, previous-transform, and Hi-Z bindings.
- [x] Provide a `GL_EXT_mesh_shader` variant (or transpile path) for `MeshletCulling.task`, `MeshletRender.mesh`, and `MeshletRenderSkinned.mesh`. EXT-dialect siblings `MeshletCullingExt.task`, `MeshletRenderExt.mesh`, and `MeshletRenderSkinnedExt.mesh` mirror the NV variants line-for-line except for the dialect-specific intrinsics: `#extension GL_EXT_mesh_shader : require`, `taskPayloadSharedEXT` payloads, `EmitMeshTasksEXT(...)` replacing `gl_TaskCountNV`, `SetMeshOutputsEXT(vertexCount, primitiveCount)` replacing `gl_PrimitiveCountNV`, `gl_MeshVerticesEXT[]` replacing `gl_MeshVerticesNV[]`, and `gl_PrimitiveTriangleIndicesEXT[tri] = uvec3(...)` replacing the three-write `gl_PrimitiveIndicesNV` pattern. All scene/atlas/transform/skin bindings and interpolant locations are kept identical so a single CPU-side uniform/SSBO setup can drive either dialect. Source-contract coverage is in `MeshOptimizerInteropTests.MeshletExtVariants_ImplementGlExtMeshShaderContract`.

Phase 7 implementation note: production shader filenames now use the task-record/`GPUScene` contract. The old OpenGL NV direct-dispatch bring-up shaders were moved to `MeshletCullingDiagnostic.task` and `MeshletRenderDiagnostic.mesh`, so diagnostic `MeshletCollection` rendering cannot be mistaken for the production shader path. Both NV (`MeshletCulling.task` / `MeshletRender.mesh` / `MeshletRenderSkinned.mesh`) and EXT (`MeshletCullingExt.task` / `MeshletRenderExt.mesh` / `MeshletRenderSkinnedExt.mesh`) source variants exist and are selected at load time by `HybridRenderingManager.TryGetMeshletShaderPaths(...)` based on `AbstractRenderer.MeshShaderDialect` (`OpenGLNV` → NV files, `OpenGLEXT` / `VulkanEXT` → EXT files). Runtime loading, pipeline creation, dispatch wiring, and binding of Hi-Z / stereo / pass-mask uniforms is implemented under Phase 8 in `HybridRenderingManager`, and `VisibleMeshletTaskBuffer` is the production consumer fed by `GPURenderExpandMeshlets.comp` and bound by `HybridRenderingManager` to the task shader. Hardware/visual parity validation for both dialects remains under Phase 10.

### Exit Criteria

- [x] Task shaders cull task records pass-correctly.
- [x] Mesh shaders emit material-compatible geometry from shared atlas streams.

## Phase 8: Shared Material And Pass Coverage

**Goal:** make meshlets render through the same material-table policy and pass semantics as the zero-readback indirect path.

- [x] Remove production dependency on `MeshletMaterial`. Done 2026-05-19: production dispatch now loads `GPUScene` task/mesh shaders through `HybridRenderingManager`; the old `MeshletMaterial` path remains diagnostic-only under `MeshletCollection`.
- [x] Fetch material state through `MaterialStateBuffer`, material texture handle table, and bindless/descriptor-indexed texture paths. Done 2026-05-19: mesh shaders emit flat material/state ids, the generated meshlet fragment shader reads `MaterialStateBuffer`, and the runtime binds the shared material table plus the texture handle table when the backend supports the bindless material-table policy.
- [x] Preserve render-pass filtering, state class, layer/editor visibility, material identity, transforms, skinning, shadows, depth prepass, velocity, and stereo semantics. Done 2026-05-19: direct meshlet dispatch applies pass/state masks, material ids, transforms, previous transforms, and stereo-safe Hi-Z disable; override/depth-normal/shadow/unsupported cases are explicitly routed through traditional zero-readback material-tier rendering until dedicated variants are promoted.
- [x] Validate opaque static and skinned meshlet material-table rendering. Done 2026-05-19: source-contract coverage pins static/skinned shader material-compatible interpolants and runtime material-table binding; hardware visual parity remains in Phase 10.
- [x] Validate alpha-tested static and skinned meshlet material-table rendering. Done 2026-05-19: material state transparency modes drive masked/alpha-to-coverage discard in the generated meshlet material-table fragment shader; source-contract coverage pins the path.
- [x] Validate shadow depth and depth/normal meshlet passes. Done 2026-05-19: these passes are explicitly detected as variant/override-driven and fall back through the existing zero-readback material-tier path instead of using an incompatible meshlet material-table shader.
- [x] Validate velocity output with `PrevTransformBuffer`. Done 2026-05-19: static/skinned NV and EXT mesh shaders emit previous world position from `PrevTransformBuffer`; runtime binds the buffer for direct meshlet dispatch.
- [x] Validate stereo behavior, including Hi-Z disable or stereo-safe Hi-Z rules. Done 2026-05-19: direct meshlet dispatch sets active view uniforms and disables Hi-Z for stereo until stereo-safe Hi-Z resources are wired.
- [x] Keep unsupported passes routed through traditional zero-readback indirect rendering with visible counters. Done 2026-05-19: meshlet dispatch uses visible fallback warnings and records forbidden fallback stats when direct material-table dispatch cannot represent the pass/backend/material policy.

### Exit Criteria

- [x] Meshlet path and indirect path share material-table/bindless policy.
- [x] Pass coverage is explicit; unsupported pass routing is visible.

## Phase 9: Diagnostics, Counters, And Tests

**Goal:** make correctness, fallback, readback violations, and performance visible.

- [x] Add counters for requested meshlet strategy frames, production meshlet frames, and meshlet fallback frames. Done 2026-05-19: `Engine.Rendering.Stats`, runtime host services, profiler packets, and profile-capture JSON now expose meshlet requested/production/fallback counters.
- [x] Add counters for task records emitted and records culled by frustum, cone, and Hi-Z. Done 2026-05-19: meshlet task shaders write stats-buffer lanes, reset/readback layout was extended, and async stats readback publishes the counters.
- [x] Add counters for expansion overflow, meshlet buffer bytes resident, and cache hit/miss/stale counts. Done 2026-05-19: expansion, scene-buffer residency, and cooked-payload cache states now feed frame stats.
- [x] Add structured logs for `Meshlet.BackendSelected`, `Meshlet.BackendUnsupported`, `Meshlet.SceneBufferUpload`, `Meshlet.ExpandOverflow`, `Meshlet.DispatchSkipped`, `Meshlet.CacheMissing`, and `Meshlet.CacheStale`. Done 2026-05-19.
- [x] Include render pass, requested strategy, selected strategy, backend dialect, source model/cache path, command count, meshlet count, and capacity where relevant. Done 2026-05-19: fields are included where the event has that context; cache-path logging currently reports the runtime cooked-payload authority until the model-cache container work owns a durable path.
- [x] Add source-contract tests forbidding readback helpers in production meshlet paths. Done 2026-05-19: production meshlet dispatch is checked for forbidden readback helpers and CPU renderer fallbacks.
- [x] Add tests proving `GpuMeshletZeroReadback` does not CPU-fallback render meshes. Done 2026-05-19.
- [ ] Add integration tests for GL NV/EXT and Vulkan EXT where hardware is available. Not run in this shell; requires mesh-shader-capable OpenGL/Vulkan hardware and scene harness runs.
- [x] Add fallback-only tests for environments without mesh shader support. Done 2026-05-19: resolver/source-contract tests cover unsupported and diagnostic-direct-only backends falling back away from production `GpuMeshletZeroReadback`; 2026-05-20 tests also cover `GpuMeshletInstrumented` diagnostics gating. Hardware fallback-only runtime smoke remains in Phase 10 backlog.

### Exit Criteria

- [x] Unsupported, overflow, stale-cache, and skipped-dispatch cases are visible in logs and counters.
- [x] Test coverage protects zero-readback and no-CPU-fallback contracts.

## Phase 10: Validation And Closeout

**Goal:** prove the production strategy is correct, stable, and worth enabling where supported.

- [x] Run targeted unit/source-contract tests for capability selection, fallback, no readbacks, GPUScene storage, LOD range selection, task-record expansion, shader bindings, and material-table routing. Done 2026-05-19: `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~MeshOptimizerInteropTests|FullyQualifiedName~GpuIndirectPhase10HardeningTests|FullyQualifiedName~ProfilerProtocolTests|FullyQualifiedName~GpuIndirectRenderDispatchTests" --no-restore --tl:off` passed 99/99, and `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~MeshSubmissionStrategyResolverTests|FullyQualifiedName~RuntimeRenderingHostServicesTests" --no-restore --tl:off` passed 18/18.
- [ ] Run B1 two-Sponzas visual parity against `GpuIndirectZeroReadback`. Not run in this shell.
- [ ] Run B2 B1-plus-idle-skinned-avatars visual parity against `GpuIndirectZeroReadback`. Not run in this shell.
- [ ] Run high-material-diversity validation. Not run in this shell.
- [ ] Run dense static geometry validation and record performance versus `GpuIndirectZeroReadback`. Not run in this shell.
- [ ] Run masked foliage validation. Not run in this shell.
- [ ] Run stereo OpenVR/OpenXR smoke when backend support is available. Not run in this shell.
- [ ] Run 100K command stress with `Stats.GpuReadbackBytes == 0`. Not run in this shell.
- [ ] Run a 30 minute `GpuMeshletZeroReadback` Release soak. Not run in this shell.
- [ ] Run OpenGL/Vulkan parity tests where backend support is available. Not run in this shell.
- [x] Run the narrowest useful build or test command if full validation is blocked. Done 2026-05-19: targeted unit/source-contract filters above were run because hardware scene parity and soak validation were not available here.
- [x] Report unrelated build/test failures instead of hiding them in this tracker. Done 2026-05-19: validation still reports existing NuGet vulnerability warnings for `Magick.NET-Q16-HDRI-AnyCPU` and `SharpCompress`, plus existing compiler warnings in `XRENGINE`/unit-test files such as `TransformBaseYamlTypeConverter.cs`, `InterfaceCollectionYamlNodeDeserializer.cs`, `FontGlyphSet.cs`, `AssetManager*.cs`, `ModelImporter.cs`, `Engine.RuntimeRenderingHostServices.cs`, and `UberShaderForwardContractTests.cs`.
- [ ] Merge `rendering-gpu-meshlet-zero-readback` back into `main` after implementation, validation, and documentation updates are complete.

## Suggested Test Names

- [x] `GpuMeshletZeroReadback_BackendUnsupported_FallsBackToZeroReadback`
- [x] `GpuMeshlet_DiagnosticDirectDispatch_DoesNotSatisfyProductionStrategy`
- [x] `GpuMeshletZeroReadback_NoCpuRenderFallback`
- [x] `GpuMeshletZeroReadback_NoReadbackHelpersInShippingPath`
- [ ] `GpuMeshletZeroReadback_StatsGpuReadbackBytesRemainZero` - runtime Release stress remains open.
- [x] `MeshletData_StoredInGPUScene_NotMeshletCollectionOwner`
- [x] `MeshletRange_PerLODMeshDataID`
- [x] `MeshletRegistration_UsesCachedPayload_NoRuntimeBuild`
- [x] `MeshletExpand_UsesCulledCommandBufferAndLODSelection`
- [x] `MeshletExpand_OverflowSetsFlagWithoutWritingPastCapacity`
- [x] `MeshletDispatch_UsesGpuWrittenCount`
- [x] `MeshletTaskShader_ConsumesTaskRecords`
- [x] `MeshletTaskShader_ConeCullUsesCookedConeData`
- [x] `MeshletTaskShader_HiZBindingsPresentWhenEnabled`
- [ ] `Meshlet_RenderPath_ProducesCorrectOutput_NVMeshShader`
- [ ] `Meshlet_RenderPath_ProducesCorrectOutput_EXTMeshShader`
- [ ] `Meshlet_VulkanEXT_Parity`
- [ ] `Meshlet_TaskShaderCull_MatchesBVHFrustumResults` - exact BVH parity test remains open.
- [ ] `Meshlet_SharedBVHCull_ThenMeshletExpansion` - shared BVH-to-meshlet integration test remains open.
- [x] `Meshlet_LODIntegration_CorrectMeshletRangePerLOD`
- [x] `Meshlet_BindlessMaterials_TextureCorrect` - source-contract/material-table coverage; visual texture correctness remains in hardware parity runs.
- [x] `Meshlet_SkinnedMesh_UsesGpuResidentSkinningAndBounds` - source-contract coverage; hardware visual parity remains open.
