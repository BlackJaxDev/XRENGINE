# Production GPU-Driven Rendering Roadmap

Last Updated: 2026-05-19
Status: Canonical production GPU-driven rendering roadmap. Supersedes the old broad [gpu-rendering.md](gpu-rendering.md) checklist, which is now a redirect into this roadmap and the dedicated meshlet tracker.

Source documents folded in:

- External report: "Fully GPU-Resident Scene Rendering for Vulkan and OpenGL in C# Silk.NET"
- [gpu-rendering.md](gpu-rendering.md) — retired internal zero-readback + meshlet architecture TODO, folded into this roadmap on 2026-05-19
- [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) — dedicated production meshlet execution tracker
- [../../design/rendering/zero-readback-gpu-driven-rendering-plan.md](../../../design/rendering/zero-readback-gpu-driven-rendering-plan.md)
- [../../design/rendering/render-submission-perf-debug-plan.md](../../../design/rendering/render-submission-perf-debug-plan.md)
- [../../../architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)
- [../../../architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md)

Spot-checked code:

- [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)
- [VPRC_RenderMeshesPassMeshlet.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs)
- [GPURenderMaterialScatter.comp](../../../../Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp)
- [GPURenderLODSelect.comp](../../../../Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp)
- [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [MeshletCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Meshlets/MeshletCollection.cs)
- `EnableZeroReadbackMaterialScatter` / `EZeroReadbackMaterialDrawPath` settings surfaces

2026-05-19 consolidation note:

- Accepted as implemented from the retired `gpu-rendering.md`: zero-readback material scatter/indirect-count submission, tiered mesh atlas infrastructure, `LODTableBuffer`, logical mesh IDs, LOD request/release APIs, basic GPU LOD selection dispatch, and indirect-path dithered LOD transition plumbing.
- Rewritten as remaining production work: production `GpuMeshletZeroReadback` capability honesty, `GpuMeshletInstrumented` diagnostics, `GPUScene`-owned meshlet ranges/descriptors/index streams, GPU meshlet expansion, indirect-count mesh-task dispatch, task-shader cone/Hi-Z culling, meshlet material-table parity, and stress validation.
- Rejected as production-complete: the current `MeshletCollection` path. It is useful bring-up/debug scaffolding, but it still owns sidecar SSBOs, scans CPU-built meshlet data, and uses direct OpenGL NV task dispatch rather than a zero-readback production meshlet path.

---

## 1. Executive Synthesis

The external report and the internal plans agree on the core architecture, with one important framing difference and a number of concrete gaps.

**Where we are aligned with the report:**

- Compute culling + indirect-count draw is the mandatory baseline.
- Mesh shaders are an *optional* enhancement, not a foundation.
- OpenGL mesh shading is non-portable (`GL_NV_mesh_shader` only) and must remain opportunistic.
- Hybrid geometry: coarse LODs with meshlets *inside* each LOD is the correct production target.
- Materials should be a GPU table, with per-draw indirections. Bindless on OpenGL, descriptor indexing on Vulkan.
- Coarse PSO families (opaque deferred, opaque forward, alpha-tested, shadow, transparent) — not "one PSO per material."
- One render thread owns the GL context; Vulkan parallelizes via per-thread command pools.
- Meshlet sizing is a *cooked asset parameter*, not a hardcoded engine constant.

**Where we are misaligned or behind the report's recommendations:**

1. **Fixed in Phase C: fat 192-byte `GPUIndirectRenderCommand`.** The report calls for a split SoA scene database (InstanceGpu / SubmeshLodGpu / MeshletGpu / MaterialGpu, plus separate Transform/PrevTransform/Bounds/Skinning streams). Done 2026-05-13: `GPUScene` now owns `DrawMetadataBuffer`, `TransformBuffer`, `PrevTransformBuffer`, `BoundsBuffer`, `MaterialStateBuffer`, and a compact 20-lane compatibility command envelope.
2. **Single production-ineligible mesh-shader bring-up path (`GL_NV_mesh_shader`).** Report flags this as vendor-locked. The current `MeshletCollection` path is direct-dispatch bring-up scaffolding; there is no production `GL_EXT_mesh_shader` or `VK_EXT_mesh_shader` indirect-count path yet.
3. **Bindless / descriptor-indexed material table is wired, but dynamic row layouts remain.** Phase D landed handle population and descriptor-indexing plumbing. The remaining correctness work is pass-declared material row layout generation, tracked in [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md).
4. **No streaming/residency story above the atlas tier split.** Report calls out suballocated arenas + sparse residency + score-based eviction as the production residency model. We have static/dynamic/streaming atlas tiers but no eviction heuristic, no sparse resource path, no page-table abstraction.
5. **Vulkan `bufferDeviceAddress` is enabled but not consumed.** Phase D5 requests the feature and exposes addresses; Phase H9 must route at least one production Vulkan draw path through those addresses.
6. **Skinned bounds readback is fixed for shipping, but diagnostic paths still read.** The zero-readback strategy bypasses `SkinnedMeshBoundsCalculator.WaitForGpu()` via GPU command-AABB direct writes. Diagnostic/non-shipping compute-bounds readbacks remain available by design.
7. **HZB shadow / second-pass occlusion.** Report's culling stack lists hierarchical occlusion as a depth-pass / shadow-pass strength. We deferred the shadow-HZB work (perf doc C-GPU-6) and even the primary HZB only just started actually running this week (the depth-view type bug from P8 was silently no-op'ing it for the entire prior measurement series).
8. **Live frame-budget regressions are not architecture problems.** P8 in the perf-debug doc shows that the current measurable bottleneck on B1 (two Sponzas, lights off, Debug build) is `XRWindow.ProcessPendingUploads` + cold `AssetManager.DeserializeAsset` stalls — not draw submission. The zero-readback architectural rewrite must not regress these.

**Where the report is weaker than our internal plans:**

- It says little about tiered mesh atlases for *content lifetime patterns* (static vs dynamic vs streaming/editable). Our atlas-tier design is more concrete than the report's "big arenas" advice.
- It does not address VR / multi-view fan-out, which we already have working (ViewSet, OVR multiview).
- It does not address our current CPU-direct safety-net path (`GpuIndirectInstrumented` mode). For a v1-ready engine that has not shipped, keeping that diagnostic path is correct and the report does not say otherwise.
- It does not address render-graph metadata; we already have explicit pass metadata and resource declarations.

---

## 2. What We've Done Well

| Area | Evidence | Notes |
| --- | --- | --- |
| GPU BVH with build / refit / SAH refinement | `bvh_build.comp`, `bvh_refit.comp`, `bvh_sah_refine.comp`, `bvh_frustum_cull.comp` | Matches the "scene acceleration structure on GPU" recommendation from §9 of the architecture plan and the report's call for hierarchical scene partitioning. |
| Zero-readback material scatter exists | `GPURenderMaterialScatter.comp` + `EZeroReadbackMaterialDrawPath.FullBucketScan` is the production-default zero-readback path | This is the §10 "Model A" submission model the architecture plan recommends. |
| `MultiDrawElementsIndirectCount` parameter-buffer path active on GL and Vulkan | `GL_ARB_indirect_parameters`, `VK_KHR_draw_indirect_count` | Report calls these out as required baseline; we have them. |
| Atlas tier split shipped | Static / Dynamic / Streaming with tier tag in `MeshDataBuffer.Flags`, scatter routes per tier | More structured than the report's "big arenas" guidance. Solves an authoring/lifetime problem the report doesn't address. |
| Persistent coherent mapped streaming tier with triple buffering | Retired `gpu-rendering.md` Phase 8D, now folded here | Matches Vulkan and GL persistent-mapped upload patterns. |
| Basic GPU LOD table + selection path exists | `GPUScene.LODTableBuffer`, `GPURenderLODSelect.comp`, `LODRequestBuffer`, `LodTransitionBuffer` | The implementation is real, but the selection metric still needs to become projected screen-space radius for production acceptance. |
| Indirect-path dithered LOD transition plumbing exists | `GPURenderLODSelect.comp`, `GPURenderMaterialScatter.comp`, generated indirect fragment shader augmentation in `HybridRenderingManager` | The retired Phase 9D work is no longer a blank slate for indirect rendering; meshlet transition expansion remains future work. |
| Render-graph metadata is real | `XREngine.Runtime.Rendering/RenderGraph/` | The report's "render graph with explicit resource declarations" recommendation is already in place. |
| GPU Hi-Z occlusion + CPU async query, with temporal hysteresis and pass-aware exclusion | `GPURenderHiZSoACulling.comp`, `GPURenderPassCollection.Occlusion.cs` | Matches Phase 3 of the internal plan. **Now actually executing**: the silent no-op due to depth-view typing has been fixed (`TryResolveHiZDepthSource` accepts `XRTexture2D`, `XRTexture2DView`, and single-layer `XRTexture2DArrayView`). |
| ViewSet + multi-view fan-out, OVR multiview preferred / NV stereo fallback | Phase 5 (complete) | Goes beyond the report. |
| Mesh submission strategy is an *explicit*, switchable contract | `EMeshSubmissionStrategy { CpuDirect, GpuIndirectInstrumented, GpuIndirectZeroReadback, GpuMeshletInstrumented, GpuMeshletZeroReadback }` and per-strategy invariants in [mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md) | Lets us A/B regressions cleanly. Report has no equivalent because it doesn't address pre-v1 engines. |
| `GpuBackendParityValidator` runtime snapshot | Phase 6 (complete) | Maintains OpenGL/Vulkan parity confidence. |
| `MeshletGenerator` via meshoptimizer P/Invoke | `XREngine/Rendering/Meshlets/MeshletGenerator.cs` | Matches the report's "use meshoptimizer over reimplementing" advice. |
| Strategy-gated readbacks | `GpuIndirectInstrumented` owns diagnostic readbacks; `GpuIndirectZeroReadback` is forbidden from readback in steady state | Separation of debug-vs-shipping concerns the report's §15 also calls out. |

---

## 3. What We've Done Wrong / Inverted From the Recommended Architecture

Concrete deviations from the report and from our own internal plans that should be fixed.

| # | Issue | Source of evidence | Severity |
| --- | --- | --- | --- |
| W1 | Fixed in Phase C: `GPUIndirectRenderCommand` is now a compact 20-lane compatibility envelope; world/previous matrices live in `TransformBuffer` / `PrevTransformBuffer`, and culling reads metadata + bounds. | Report §7.1, internal zero-readback plan §7.1, [gpu-rendering.md](gpu-rendering.md) Current Pipeline Architecture | Resolved |
| W2 | Fixed in Phase E: `GPURenderLODSelect.comp` now selects from explicit projected screen-space radius thresholds using camera projection scale plus active viewport/render-area dimensions while preserving `LODTableBuffer`, request masks, and transition state writes. | Retired `gpu-rendering.md` Phase 9 vs current shader/code | Resolved |
| W3 | Fixed in Phase B: `VPRC_RenderMeshesPassMeshlet` is documented as wired and no longer runs the CPU mesh pass before the GPU meshlet path. Remaining meshlet work is atlas/LOD/Hi-Z integration. | Code spot-check vs internal doc | Resolved |
| W4 | Fixed in Phase C at the batching layer: material scatter and sort keys now use `DrawMetadata.StateClassID`. Phase D still needs the final per-draw bindless material table for texture-correct arbitrary materials. | Report §"materials, descriptors, bindless"; internal plan §14.1 | Resolved / D follow-up |
| W5 | Bindless / descriptor-indexing material table now has real handle population, but the next correctness step is replacing fixed opaque-deferred row assumptions with pass-declared dynamic layouts. | [gpu-rendering.md](gpu-rendering.md) 2026-05-08 update; [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md) | D follow-up |
| W6 | Fixed in Phase B for shipping: `GpuIndirectZeroReadback` uses GPU command-AABB direct writes for skinned bounds and skips the `WaitForGpu()` readback path. Diagnostic/non-shipping compute-bounds readback remains available. | Internal zero-readback plan §13.4 | Resolved |
| W7 | Fixed in Phase B for shipping: `GPURenderBuildBatches.comp`, `DispatchBuildGpuBatches`, and `ReadGpuBatchRanges()` are behind `XRE_DEBUG_BATCH_RANGE_READBACK`; `GpuIndirectZeroReadback` scatters directly. | Internal plan §2.1, Phase 7 acceptance criteria | Resolved |
| W8 | Static-scene short-circuit (skip cull/sort if camera + topology + BVH gen unchanged) is missing. The recurring cost of recomputing HZB and redispatching cull on a static two-Sponza scene is exactly this hole. | Perf doc P7 (since retracted) still identified the redundancy; report §"hierarchical occlusion" implies temporal reuse | Medium |
| W9 | Vulkan `bufferDeviceAddress` is enabled but has no production consumers yet. Pointer-like scene-database access still needs to replace at least one descriptor-bound path. | Report Vulkan feature bundle, Phase D5/H9 | Low (Vulkan still WIP) |
| W10 | No sparse-residency / virtualized-geometry story even as a stub. We are deferring this, which is correct, but we should write the API boundary now so the atlas+page-table interface is not refactored twice. | Report §"streaming, residency, eviction" | Low |
| W11 | `ProcessPendingUploads` is on the render thread without a true budget cap; spikes of 53–114 ms have been recorded. This is orthogonal to GPU-driven rendering but it dominates the live perf measurement so any architecture comparison is currently noisy. | Perf doc §10.5 C-UPL-1, opengl-program-linking docs | High (for measurement validity) |
| W12 | Cold shader/asset cache deserialization (`AssetManager.DeserializeAsset .../Atmosphere/*.fs.asset`) is happening in the render hot path on first frames. Should be moved to async preload. | Perf doc P8, C-CACHE-1 | High (for measurement validity) |
| W13 | Meshlets do not yet live in the mesh atlas. `MeshletCollection` owns its own SSBOs separately from `GPUScene`'s atlas. Hybrid coarse-LOD-then-meshlet (report's recommended architecture) requires meshlet data colocated with the atlas. | Internal plan Phase 10A, report §"meshlet generation and packing" | Medium |
| W14 | Texture-array vs bindless usage is not policy-controlled. Report explicitly: texture arrays only for homogeneous classes (decals, terrain splats, light cookies); bindless for arbitrary materials. We currently mix. | Report §"materials, descriptors" | Low |
| W15 | Cluster / cone backface culling is not implemented even though meshoptimizer cone data is generated. Report and meshoptimizer guidance both call this out for shadow / depth-prepass benefit. | Report §"meshlet culling approaches" | Low |
| W16 | `bvh_frustum_cull.comp` writes per-view appends. There is no documented Hi-Z-aware cluster cull pass *for shadow passes specifically*; per-light HZB is the right architecture for shadow occlusion (C-GPU-6 backlog) and is currently deferred. | Perf doc C-GPU-6 backlog | Medium |

---

## 4. Phased Plan

Phases are ordered by dependency. Each phase has acceptance criteria. Items the existing internal docs have already completed are retained as **prerequisite** and not duplicated.

### Phase A — Stop the bleeding on measurement validity

Goal: every architectural change after this phase must be measured on a workload that is not dominated by upload-queue stalls and cold asset deserialization. Without this, any A/B test of zero-readback vs CpuDirect is meaningless.

- [x] **A1.** Move `AssetManager.DeserializeAsset` for shader-cache entries (e.g. atmosphere shaders) out of the render hot path. Async preload during scene load; render thread sees only the cached handle. (Perf doc C-CACHE-1.)
- [x] **A2.** Cap `XRWindow.ProcessPendingUploads` per-frame budget hard. Log per-chunk ms and dequeued items at debug verbosity. Verify `BoostBudgetUntilDrained` doesn't overshoot mid-frame. (Perf doc C-UPL-1.)
- [x] **A3.** Replace `Tools/Diagnose-ZeroReadbackHz.ps1` env-var parsing so invalid enum values fail loud instead of silently running the previous mode. Add per-variant cache clear (or single-process-per-variant) to prevent warm-cache contamination. (Perf doc C-MEAS-1.)
- [x] **A4.** Add an explicit Release-config measurement target to the perf-debug doc. Debug-build CPU dispatch overhead is masking the actual architecture cost.
- [ ] **A5.** Re-baseline B1 (two Sponzas, lights off) and B2 (B1 + 100 idle skinned avatars) under Release after A1–A4 land. Record CpuDirect / GpuIndirectInstrumented / GpuIndirectZeroReadback fps and `glUseProgram`/`glBindBufferBase`/MDEIC counts per frame. Compare against the report's "compute + indirect-count" expectation.

Acceptance: Release-config CpuDirect on B1 is back near the original ~80 fps target (Debug regressed it to ~25 fps). Drop logs distinguish *no drops* from *no process*. Variant scripts cannot silently run the wrong configuration.

---

### Phase B — Finish the zero-readback shipping path (retired `gpu-rendering.md` Phase 7)

Goal: zero CPU readbacks in the shipping path. The old Phase 7 checklist has been folded into this phase.

- [x] **B1.** Delete `GPURenderBuildBatches.comp` + `ReadGpuBatchRanges()` from shipping builds. Move them under a `#define XRE_DEBUG_BATCH_RANGE_READBACK` (or equivalent build flag) and exclude from `GpuIndirectZeroReadback`. Done 2026-05-13: shader load, dispatch, buffer allocation, and batch-range readback are compile-flagged; zero-readback scatters directly.
- [x] **B2.** Remove the CPU `RenderCPU` call from `VPRC_RenderMeshesPassMeshlet.Execute`. Done 2026-05-13.
- [x] **B3.** Retire [gpu-rendering.md](gpu-rendering.md) as a stale active checklist. Done 2026-05-19: it now redirects to this roadmap and the dedicated meshlet tracker.
- [x] **B4.** Confirm `Engine.Rendering.Stats.GpuReadbackBytes == 0` for a full Release frame on B1 under `GpuIndirectZeroReadback`. Add a unit test asserting this on a representative frame. Done 2026-05-13: unit/source-contract coverage added; live Release B1 capture remains part of A5/H1 measurement runs.
- [x] **B5.** Skinned bounds on GPU without readback (internal W6, plan §13.4). Write skinned bounds into a GPU-resident `BoundsBuffer` consumed by culling directly. Remove `SkinnedMeshBoundsCalculator.WaitForGpu()` from the shipping path. Done 2026-05-13: zero-readback uses the GPU command-AABB buffer direct-write path and skips the CPU readback result path. `SkinnedMeshBoundsCalculator.WaitForGpu()` calls remain in diagnostic/non-shipping compute-bounds paths only; the zero-readback strategy bypasses that class entirely via `RenderableMesh.ApplyGpuResidentSkinnedBoundsDispatchLocked → DispatchPathADirectWrite`.

Acceptance: shipping path has zero `MapBufferData`, `ReadUIntAt`, `GetDataArrayRawAtIndex`, or `WaitForGpu` calls in the render hot path. Source-contract grep tests + a no-op stats-contract test (`GpuIndirectPhase7ZeroReadbackTests`) guard the invariants in CI; live `GpuReadbackBytes == 0` confirmation on B1 and B2 under Release is deferred to A5 / H1 via `Measurement-Baseline-Release-All`.

> **Phase B caveat — meshlet descriptors still outside the SoA scene database.** B3 confirmed `VPRC_RenderMeshesPassMeshlet` dispatches the GPU meshlet path with `UseMeshletPipeline = true`, but meshlet vertex / triangle / descriptor data still lives in `MeshletCollection` rather than `GPUScene`'s atlas. That migration is tracked under E3 and is not a Phase B regression — flagging it here so readers don't conclude meshlets are "done" after Phase B.

> **Phase B caveat — GPU BVH overflow flag is now async outside zero-readback.** As of 2026-05-19, `GpuBvhTree` enqueues an OpenGL fence after the build dispatch chain and only maps the 4-byte overflow flag after a later non-blocking poll reports the fence signaled. `GpuIndirectZeroReadback` still skips the flag read entirely. Vulkan/DX12 backends that do not expose `InsertGpuFence()` retain the old synchronous fallback until backend-native fence plumbing lands.

---

### Phase C — Data model split (close the report's §7 recommendation)

Goal: replace the 192-byte fat AoS command with an SoA scene database that culling, LOD, scatter, and draw all consume from. This is the foundational change for everything past Phase D.

Status: Complete as of 2026-05-13. The remaining material-texture indirection work belongs to Phase D.

The target layout, harmonized between the report and internal plan §7:

| Stream | Owner | Content | Update pattern |
| --- | --- | --- | --- |
| `DrawMetadataBuffer` | `GPUScene` | `DrawID`, `MeshID`, `SubmeshID`, `MaterialID`, `TransformID`, `SkinID`, `RenderPassMask`, `LayerMask`, `Flags`, `LodPolicy`, `StateClassID` | Rare |
| `TransformBuffer` | `GPUScene` | `WorldMatrix` indexed by `TransformID` | Dirty-range upload |
| `PrevTransformBuffer` | `GPUScene` | `PrevWorldMatrix` indexed by `TransformID` | Rotated each frame |
| `BoundsBuffer` | `GPUScene` | World-space `BoundingSphere` + `AABB` + `BoundsVersion` | GPU-derived (rigid) or GPU-written (skinned) |
| `SkinningPaletteBuffer` | `GPUScene` | Bone matrices indexed by `SkinID` range | Dirty-range upload |
| `MaterialStateBuffer` | `GPUScene` | Pipeline/state class id, descriptor/handle indices, options bits, transparency mode | Rare |
| `VisibleDrawIDsBuffer` | `GPURenderPassCollection` | Compact visible draw ID list | GPU-written per frame |
| `IndirectCommandBuffer` | `GPURenderPassCollection` | Per-state-class indirect records | GPU-written per frame |
| `IndirectCountBuffer` | `GPURenderPassCollection` | Per-state-class counts | GPU-written per frame |

Subtasks:

- [x] **C1.** Introduce `DrawMetadata`, `TransformGpu`, `BoundsGpu`, `MaterialStateGpu` POD structs. All blittable, `LayoutKind.Sequential`. `GPUIndirectRenderCommand` remains only as a compact compatibility envelope for downstream command builders while the rest of the pipeline consumes SoA buffers.
- [x] **C2.** Add `TransformID`, `SkinID`, `StateClassID` allocators to `GPUScene` with stable ID semantics (recycled on remove, never silently shifted).
- [x] **C3.** Migrate culling kernels (`bvh_frustum_cull.comp`, `GPURenderCullingSoA.comp`, `GPURenderHiZSoACulling.comp`) to read `DrawMetadata` + `BoundsBuffer` instead of fat command structs.
- [x] **C4.** Migrate `GPURenderMaterialScatter.comp` to read `DrawMetadata.StateClassID` and write into per-StateClass indirect buffers (not per-MaterialID).
- [x] **C5.** Wire `StateClassID` derivation from CPU material/pipeline registry. Document the rule (opaque-deferred, opaque-forward, alpha-tested, shadow, transparent + small set of true exceptions). (Report §"shader permutation strategy".)
- [x] **C6.** Remove `GPUIndirectRenderCommand.WorldMatrix` / `PrevWorldMatrix`. Vertex shaders fetch from `TransformBuffer[TransformID]`. (Report §"data model refactor".)
- [x] **C7.** Dirty-range upload on `TransformBuffer` / `PrevTransformBuffer` / `SkinningPaletteBuffer`. Replace full-scene repacks with range merges.

Phase C caveat: the zero-readback material scatter path now batches by `StateClassID` and uses a representative CPU material for each class. Phase D restores fully arbitrary per-draw material data through the bindless/descriptor-indexed material table.

Acceptance: zero references to `WorldMatrix` inside `GPUIndirectRenderCommand`. Culling kernels operate on bounds + metadata only. State-class is the primary batching key; material is a data indirection.

---

### Phase D — Materials and bindless

Goal: arbitrary per-submesh materials resolve through GPU indirections under a small number of state classes. This is the report's "one material table per frame" recommendation and the long-term version of Model B in the internal plan.

- [x] **D1.** Finish the `BindlessMaterialTable` `EZeroReadbackMaterialDrawPath` path: actual `GL_ARB_bindless_texture` handle creation and residency tracking. Two-level table: `MaterialGpu` → handle index → 64-bit handle. (Report §"recommended material layout"; internal W5.) Done 2026-05-13: `GPUMaterialTable` owns `MaterialTable` plus `MaterialTextureHandleTable`; OpenGL resolves 2D texture handles through `ARB_bindless_texture`, makes them resident, and retires unused handles. The follow-up dynamic row-layout work is tracked in [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md).
- [x] **D2.** Add Vulkan descriptor-indexing equivalent (`VK_EXT_descriptor_indexing`, `VK_DESCRIPTOR_BINDING_VARIABLE_DESCRIPTOR_COUNT`, `nonuniformEXT`). One large texture array binding consumed by all opaque/transparent fragment shaders. (Report §"descriptor and bindless model comparison".) Done 2026-05-13: Vulkan descriptor layout creation recognizes the material bindless texture array as a variable descriptor-count binding, allocates descriptor sets with `DescriptorSetVariableDescriptorCountAllocateInfo`, and documents the `nonuniformEXT` shader include contract.
- [x] **D3.** Texture-array policy: texture arrays only for genuinely homogeneous classes (decals, terrain splats, light cookies). Document and enforce. (Internal W14.) Done 2026-05-13: `RenderingParameters.TextureArrayPolicy` defaults to arbitrary-material mode; material-table residency rejects texture arrays unless explicitly marked homogeneous. See [material-binding-policy.md](../../../../architecture/rendering/material-binding-policy.md).
- [x] **D4.** Shader permutation reduction. Audit the current PSO matrix and collapse to the report's recommended small set: opaque-static, opaque-skinned, alpha-tested-static, alpha-tested-skinned, shadow-only variants, plus a documented allowlist of exceptional PSOs. (Internal W4, report §"shader permutation strategy".) Done 2026-05-13: material-table rendering remains one generated draw program per renderer/bindless mode, state classes are documented as the PSO family boundary, and shadow passes now resolve to the explicit `Shadow` state class.
- [x] **D5.** Vulkan `bufferDeviceAddress` for `MaterialStateBuffer` and other scene-database SSBOs (Internal W9, report Vulkan feature bundle). Lets us pass pointers in pNext chains and indirect-draw structures without descriptor rebinds. Done 2026-05-13: `VK_KHR_buffer_device_address` is requested when available, scene-database SSBOs opt into `ShaderDeviceAddressBit`, and `VkDataBuffer` exposes the resolved device address.

Acceptance: B1+B2 render under ≤6 graphics PSO families. Number of `glUseProgram` / `vkCmdBindPipeline` calls per frame is ≤ (number of state classes × number of passes). Adding a new texture-only material does not change the PSO count.

---

### Phase E — Hybrid LOD + meshlet integration (close internal Phase 9 + 10)

Goal: implement the report's recommended geometry strategy — coarse object/submesh LODs with meshlets inside each LOD — sharing the new SoA scene database.

Merged status from retired `gpu-rendering.md`: Phase 9A/9C and the basic Phase 9B/9D plumbing are real. The Phase E implementation path is now source-contract complete; hardware parity, stress, and soak validation remain in Phase H and the meshlet tracker Phase 10.

- [x] **E1.** Upgrade `GPURenderLODSelect.comp` from raw distance thresholds to *projected screen-space radius* selection. Done 2026-05-19: `SubMeshLOD.MinProjectedScreenRadiusPixels` is the explicit GPU LOD threshold, `GPUScene.LODTableBuffer` lanes store minimum projected radius pixels with conservative defaults for unset thresholds, the LOD pass binds projection scale and active render-area/viewport size, and the shader selects by projected radius while preserving request/transition contracts. Tests cover source bindings, the LOD table contract, and large-near versus small-near selection.
- [x] **E2.** `LODTableBuffer` with clean `LogicalMeshID` mapping, resident LOD mesh IDs, `RequestLODLoad`, and `ReleaseLOD`. Done before consolidation: `GPUScene.LODTableEntry`, `LODTableBuffer`, `LODRequestBuffer`, and logical mesh registration are present and covered by backlog tests.
- [x] **E3.** Basic dithered LOD transition state for the indirect path. Done before consolidation: `LodTransitionBuffer`, `GPURenderLODSelect.comp` transition progress, previous-LOD duplicate indirect draws, and generated fragment-shader Bayer dither are wired. Meshlet transition expansion was completed under E7.
- [x] **E4.** Production meshlet capability honesty. Split direct mesh-task dispatch from production indirect/count meshlet dispatch; model dialects as `None`, `OpenGLNV`, `OpenGLEXT`, and `VulkanEXT`; make unsupported `GpuMeshletZeroReadback`/`GpuMeshletInstrumented` visibly fall back to `GpuIndirectZeroReadback`. Done 2026-05-19 in [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) Phase 1, then split into the explicit instrumented-vs-zero-readback enum values on 2026-05-20.
- [x] **E5.** Move meshlet vertex/triangle/descriptor data into `GPUScene` ownership. Done 2026-05-19 in [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) Phases 3-4: `GPUScene` owns meshlet ranges, descriptors, vertex-reference indices, and triangle-local indices keyed by `MeshDataID`/LOD; `MeshletCollection` is now diagnostic/compatibility ownership, not the production source.
- [x] **E6.** Add cooked meshlet descriptor payloads and generation settings. Done 2026-05-19: `XRMesh` owns a cooked `MeshletPayload` with CPU descriptors, cone data, settings/freshness hashes, disabled-generation manifests, runtime cooked-binary round trip, warm-cache render startup reuse, and GPU layout handoff. The disposable model-cache chunk/container remains owned by [model-import-binary-cache-todo.md](../../assets/model-import-binary-cache-todo.md) Phase 5 and is no longer a blocker for renderer Phase E.
- [x] **E7.** GPU meshlet expansion + indirect-count task dispatch. Done 2026-05-19 in [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) Phases 5-6: `GPURenderExpandMeshlets.comp`, `GpuMeshletTaskRecord`, task-count/dispatch buffers, overflow handling, previous-LOD task records, and backend indirect-count mesh-task dispatch wrappers are wired; missing meshlet ranges route through traditional zero-readback indirect rendering.
- [x] **E8.** Production task/mesh shader integration. Done 2026-05-19 in [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) Phase 7: production shader filenames consume task records, transform bounds through `TransformBuffer`, perform pass-gated frustum/cone/primary-view Hi-Z culling, and emit atlas-backed material-compatible mesh outputs. The old direct-dispatch NV shaders are now explicitly diagnostic files.
- [x] **E9.** Material/pass parity and validation for `GpuMeshletZeroReadback`/`GpuMeshletInstrumented`: opaque, alpha-tested, shadow/depth, velocity, skinned, stereo, bindless/descriptor material table, fallback-only environments, and zero-readback/instrumented stats. Done 2026-05-19 at renderer/source-contract scope and refined 2026-05-20 for split strategy stats: runtime meshlet dispatch uses the shared material-table policy where supported, binds `MaterialStateBuffer`/texture handle tables, disables unsafe stereo Hi-Z, routes override/depth-normal/shadow/skinned unsupported cases through traditional zero-readback indirect with visible warnings, publishes meshlet counters/profile-capture stats, and records instrumented visible/dispatched/overflow/dispatch/readback-byte telemetry only under diagnostics. Hardware visual parity, fallback smoke, 100K/readback stress, performance, and soak validation remain tracked by Phase H plus [gpu-meshlet-zero-readback-rendering-todo.md](gpu-meshlet-zero-readback-rendering-todo.md) Phase 10.

Acceptance: Phase E source contracts are complete: LOD selection is projected-radius based, meshlets share the SoA scene database, production dispatch uses GPU-written counts, and unsupported material/pass/backend cases visibly fall back to zero-readback indirect. Hardware parity/performance acceptance is carried by Phase H.

---

### Phase F — Temporal + occlusion improvements

Goal: stop redoing work that did not change, and extend Hi-Z to shadows.

- [ ] **F1.** Static-scene short-circuit. If BVH generation id + visible command count + camera VP + active pass set are unchanged, reuse last frame's visibility list. (Internal W8; this is the safe version of the retracted P7 attempt.) Requires that the visibility-list buffer is rotated, not aliased, so the previous frame's data is still valid.
- [ ] **F2.** Per-light shadow HZB cull pass (perf-doc C-GPU-6 backlog). One HZB per active shadow caster; mesh atlas + indirect path reused. (Internal W16.)
- [ ] **F3.** Previous-frame depth-only occluder pass for primary view (two-pass occlusion). Standard GPU-driven pattern; eliminates first-frame popping for newly-visible occluders. (Report §"hierarchical occlusion using previous-frame depth or Hi-Z".)
- [ ] **F4.** Stereo HZB. Currently `XRTexture2DArrayView` with `NumLayers > 1` falls through to `Exit.DepthUnsupportedView`. Wire a `sampler2DArray` HiZ pyramid path for the stereo case. (Code comment in `GPURenderPassCollection.Occlusion.cs:547` — "C-DRP-2, deferred".)

Acceptance: B1 stable at >60 fps Release with HZB enabled (currently borderline post-fix). Shadow draws on B2 see ≥40% draw reduction with shadow HZB on. Stereo HiZ renders correctly with no occlusion artifacts vs mono.

---

### Phase G — Residency, streaming, and sparse resources

Goal: bring the residency story up to the report's level without locking into Vulkan-only features prematurely.

- [ ] **G1.** Define a `PageTableBuffer` abstraction over the atlas tiers. Even if everything is contiguous on day one, the GPU view should already be page-id-indexed so the future sparse path doesn't require a second migration.
- [ ] **G2.** Score-based eviction in the dynamic tier: `priority = predictedReuse * screenImpact * passImportance * temporalStability`. Drive `screenImpact` from the LOD selection output (projected radius). (Report §"eviction and prioritization".)
- [ ] **G3.** Vulkan sparse residency proof-of-concept on `XREngine.VRClient` only, behind a feature flag. Validate `vkQueueBindSparse` + rebind under our render-graph synchronization model. Do not enable in production. (Report §"when to use sparse residency".)
- [ ] **G4.** GL sparse buffer/texture path explicitly *not implemented*. Document that OpenGL stays on arena allocators per the report's recommendation, with the contract that the engine API surface is identical.
- [ ] **G5.** Streaming-tier upload telemetry: bytes/frame, latency from request to GPU-visible, oldest-pending-request age. Surface in profiler. (Hooks into A2.)

Acceptance: dynamic tier evicts under stress without thrashing visible-frame data. Sparse POC runs B1 with virtualized vertex/index buffers without correctness regression. Profiler exposes upload backpressure metrics.

---

### Phase H — Production hardening (internal Phase 11, extended)

- [ ] **H1.** 100K+ command stress under `GpuIndirectZeroReadback` + Release, zero readbacks confirmed, ≥30 minute soak.
- [ ] **H2.** High-material-diversity stress (500+ unique materials, ≤6 state classes). Validate `glUseProgram` / `vkCmdBindPipeline` count is bounded by state-class count, not material count.
- [ ] **H3.** Skinned-stress (B2 with 1000 avatars, all animating). Validate skinned-bounds GPU path has no readback and no `WaitForGpu`.
- [ ] **H4.** OpenGL/Vulkan parity tests extended to meshlet + bindless paths.
- [ ] **H5.** Crash-safe diagnostics on overflow (per-state-class indirect buffer overflow, per-material handle exhaustion, atlas-tier eviction failure). Surface as `Engine.Rendering.Stats` counters, not silent.
- [x] **H6.** Finalize `mesh-submission-strategies.md` to document the split meshlet modes: `CpuDirect` (debug-only), `GpuIndirectInstrumented` (diagnostic readbacks allowed), `GpuIndirectZeroReadback` (shipping indirect), `GpuMeshletInstrumented` (diagnostic meshlets), and `GpuMeshletZeroReadback` (shipping meshlets where supported). Done 2026-05-20.
- [ ] **H7.** Retire `GPURenderBuildBatches.comp` and `ReadGpuBatchRanges()` from the tree entirely once H1 has cleared a soak test.
- [x] **H8.** Eliminate the BVH overflow-flag readback in `GpuBvhTree.ConsumeOverflowFlag`. Done 2026-05-19 for OpenGL: `Build(...)` now enqueues an `XRGpuFence`, `PollPendingOverflow()` consumes signaled fences without blocking, `GPUScene.PrepareBvhForCulling(...)` polls even when the BVH is already clean, and `GpuIndirectZeroReadback` performs no overflow-flag readback.
- [ ] **H9.** Wire `VkDataBuffer.DeviceAddress` consumers. Phase D5 enabled `VK_KHR_buffer_device_address` on the eight scene-database SSBOs (`VulkanSceneDatabaseAddresses.SceneDatabaseDeviceAddressBuffers`) and resolves each buffer's device address via `GetBufferDeviceAddress`, but `VkDataBuffer.TryGetDeviceAddress` has zero callers in the engine. Pass scene-DB SSBO addresses through `pNext` chains, push constants, or indirect-draw structures so descriptor rebinds for these buffers can be skipped on the Vulkan path. Acceptance: at least one production-path Vulkan draw submission consumes a resolved `DeviceAddress` instead of a descriptor binding for one of the eight enumerated SSBOs.
- [ ] **H10.** Backfill the narrowed validation backlog from the retired `gpu-rendering.md`: zero-readback material scatter draw-count/empty-bucket tests, tiered-atlas dynamic add/remove + defrag tests, streaming no-stall validation, LOD transition dither tests, meshlet fallback tests, and 100K/high-material-diversity/rapid-LOD stress coverage. Projected-radius LOD source/logic tests landed in E1 on 2026-05-19; runtime rapid-LOD stress remains here.

Acceptance: 30-minute Release stress on all five modes with zero corruption, zero readbacks in shipping modes, stable frame time, no fallback thrashing.

---

## 5. Cross-Reference Matrix

For traceability — which subtasks in this doc map back to which source.

| Phase / Task | Retired gpu-rendering.md | zero-readback plan | Report section | Perf doc |
| --- | --- | --- | --- | --- |
| A (measurement) | — | — | — | §10.5 C-DRP-1/C-UPL-1/C-CACHE-1/C-MEAS-1 |
| B (close ZR shipping) | Phase 7, accepted as implemented | §10, §13.4 | §"submission models" | — |
| C (data model split) | partial (Hot/Cold) | §7 | §"data model" + §"recommended architecture" | — |
| D (materials/bindless) | partial (`BindlessMaterialTable`) | §10.2, §14 | §"materials, descriptors, bindless" | — |
| E (LOD + meshlets) | Phase 9 partially accepted; Phase 10 rewritten around production meshlet TODO | §13 | §"geometry, meshlets, culling, dynamic LOD" | C-GPU-3 (dirty-frame) interacts |
| F (temporal + occlusion) | — | — | §"meshlet culling approaches" + §"hierarchical occlusion" | C-GPU-6 backlog |
| G (residency/sparse) | Tiered atlas (Phase 8), accepted as implemented baseline | — | §"streaming, residency, eviction" | C-UPL-1 |
| H (hardening) | Phase 11 stress/tests, narrowed and carried forward | — | §"open questions and limitations" | — |

---

## 6. Open Questions

- Resolved 2026-05-13: Phase C landed as an in-place v1 breaking change. `GPUIndirectRenderCommand` is a compact compatibility envelope, while `GPUScene` owns stable SoA scene buffers for all GPU-driven paths.
- Vulkan mesh-shader path (E7) — keep it gated on `VK_EXT_mesh_shader` only, or also support the older `VK_NV_mesh_shader`? Report says EXT is the formal feature; NV value is unclear for a new engine.
- Sparse residency POC scope (G3) — vertex/index only, or also material textures? Textures are a much bigger win (BC7 megapages) but a much bigger correctness surface.
- Static-scene short-circuit (F1) — is the visibility-list buffer rotated already? If not, F1 has a prerequisite buffer-rotation task that should land first.
- Cooked meshlet size (E6) — use one profile per asset first, with the profile name/settings persisted in the cooked payload. Multiple vendor-selected profiles are deferred until profiling proves a real win.

---

## 7. Non-Goals (Explicit)

- Device-generated commands (Vulkan `VK_EXT_device_generated_commands`). Report classifies as opportunistic; we agree and explicitly defer.
- Nanite-style cluster-DAG / continuous meshlet LOD. Defer until the coarse-LOD plus meshlet-per-LOD production path is stable.
- Replacing OpenGL with Vulkan-only. AGENTS.md says OpenGL 4.6 remains primary; Vulkan/DX12 are WIP.
- Meshlet compression. Defer until profiling proves geometry bandwidth (not shader occupancy or pixel cost) is the bottleneck. (Report §"streaming and residency".)
- Bindless on OpenGL pre-`GL_ARB_bindless_texture`. No fallback to per-draw texture binding once D1 lands.
