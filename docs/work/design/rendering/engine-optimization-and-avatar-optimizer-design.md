# Engine Rendering Optimization Design

Last Updated: 2026-05-29
Status: design proposal
Scope: production renderer performance architecture, CPU/GPU submission strategy, zero-readback constraints, meshlets, visibility-buffer rendering, stereo rendering, and profiling.

Related docs:

- [Engine rendering optimization roadmap](../../todo/rendering/optimization/engine-rendering-optimization-roadmap.md)
- [Render submission performance debug plan](render-submission-perf-debug-plan.md)
- [Zero-readback GPU-driven rendering plan](zero-readback-gpu-driven-rendering-plan.md)
- [GPU meshlet zero-readback rendering design](gpu-meshlet-zero-readback-rendering-design.md)
- [Avatar optimization and virtualized rendering](avatar-optimization-and-virtualized-rendering-design.md)
- [Dynamic indirect material bindings](dynamic-indirect-material-bindings.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [World shader prewarm graph](../../../architecture/rendering/world-shader-prewarm-graph.md)
- [Model import binary cache design](../assets/model-import-binary-cache-design.md)
- [Texture runtime streaming and virtual texturing design](../texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [GPU-driven animation](gpu/gpu-driven-animation.md)
- [GPU skinning buffer compression](gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-accelerated modeling tools design](../modeling/gpu-accelerated-modeling-tools-design.md)

External references:

- Unity draw call optimization: <https://docs.unity.cn/Manual/optimizing-draw-calls.html>
- Unity SRP Batcher: <https://docs.unity.cn/Manual/SRPBatcher.html>
- Unity GPU instancing: <https://docs.unity3d.com/Manual/GPUInstancing.html>
- Unreal instanced static mesh components: <https://dev.epicgames.com/documentation/unreal-engine/instanced-static-mesh-component-in-unreal-engine>
- Unreal Nanite virtualized geometry: <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine>
- Unreal Nanite GPU-driven materials: <https://www.unrealengine.com/blog/take-a-deep-dive-into-nanite-gpu-driven-materials>
- Vulkan multi draw indirect sample: <https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html>
- Vulkan `vkCmdDrawIndexedIndirectCount`: <https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdDrawIndexedIndirectCount.html>
- Direct3D 12 `ExecuteIndirect`: <https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-executeindirect>
- NVIDIA descriptor and bindless guidance: <https://developer.nvidia.com/blog/advanced-api-performance-descriptors/>
- NVIDIA mesh shader performance guidance: <https://developer.nvidia.com/blog/advanced-api-performance-mesh-shaders/>
- NVIDIA Nsight GPU Trace overview: <https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html>
- AMD RDNA performance guide: <https://gpuopen.com/learn/rdna-performance-guide/>
- AMD Vulkan barriers explained: <https://gpuopen.com/learn/vulkan-barriers-explained/>
- Meta Quest frame budgets and profiling workflow: <https://developers.meta.com/horizon/documentation/unreal/po-perf-opt-mobile/>
- OpenXR frame timing (`XrFrameState`): <https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrFrameState.html>
- Unity single-pass instanced XR rendering: <https://docs.unity.cn/2018.2/Documentation/Manual/SinglePassInstancing.html>
- Ubisoft GPU-driven rendering pipelines (SIGGRAPH 2015): <https://advances.realtimerendering.com/s2015/aaltonenhaar_siggraph2015_combined_final_footer_220dpi.pdf>
- Nanite virtualized geometry deep dive (SIGGRAPH 2021): <https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>
- Frostbite compute-driven geometry pipeline (GDC 2016): <https://www.ea.com/frostbite/news/the-rendering-pipeline-challenges-next-steps>
- Visibility buffer rendering (Burns/Hunt): <https://jcgt.org/published/0002/02/04/>
- Filmic Worlds visibility-buffer notes: <http://filmicworlds.com/blog/visibility-buffer-rendering-with-material-graphs/>
- Unity SRP Batcher technical detail: <https://blog.unity.com/engine-platform/srp-batcher-speed-up-your-rendering>
- OpenGL `ARB_bindless_texture` spec: <https://registry.khronos.org/OpenGL/extensions/ARB/ARB_bindless_texture.txt>
- OpenGL `ARB_get_program_binary` spec: <https://registry.khronos.org/OpenGL/extensions/ARB/ARB_get_program_binary.txt>
- Vulkan pipeline cache: <https://docs.vulkan.org/spec/latest/chapters/pipelines.html#pipelines-cache>
- Vulkan subgroups: <https://docs.vulkan.org/guide/latest/subgroups.html>
- Vulkan pipeline barrier performance sample: <https://docs.vulkan.org/samples/latest/samples/performance/pipeline_barriers/README.html>
- NVIDIA async compute and overlap guidance: <https://developer.nvidia.com/blog/advanced-api-performance-async-compute-and-overlap/>
- OpenGL buffer object streaming and persistent mapping: <https://www.khronos.org/opengl/wiki/Buffer_Object_Streaming>
- Persistent-mapped buffers (Cass Everitt / NVIDIA AZDO): <https://www.khronos.org/assets/uploads/developers/library/2014-gdc/Khronos-OpenGL-Efficiency-GDC-Mar14.pdf>
- Multiview rendering (`GL_OVR_multiview2`): <https://registry.khronos.org/OpenGL/extensions/OVR/OVR_multiview2.txt>
- Vulkan multiview: <https://docs.vulkan.org/spec/latest/chapters/renderpass.html#renderpass-multiview>
- Variable rate shading (Vulkan): <https://docs.vulkan.org/samples/latest/samples/extensions/fragment_shading_rate/README.html>
- AMD FSR 2 temporal upscaling inputs: <https://gpuopen.com/manuals/fsr_sdk/techniques/super-resolution-temporal/>
- Intel XeSS motion-vector and jitter guidance: <https://www.intel.com/content/www/us/en/developer/articles/technical/xess-sr-developer-guide.html>
- ETW / `dotnet-trace` for sampled CPU profiling: <https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace>
- Superluminal sampling profiler: <https://superluminal.eu/>
- Intel VTune for GPU/CPU correlation: <https://www.intel.com/content/www/us/en/developer/tools/oneapi/vtune-profiler.html>

## 1. Summary

The fastest version of XRENGINE is not simply the version with the fewest draw calls. It is the version where:

- Renderable data is already in GPU-friendly layout before the frame starts.
- Shader programs, material tables, texture residency, and pipeline state are warmed and stable before measurement.
- The CPU submits a small number of coherent pass-level jobs instead of rebuilding state for every visible object.
- The GPU performs visibility, LOD, material classification, and command generation when the scene scale makes that worthwhile.
- The CPU never waits on GPU-produced visibility data in the shipping path.
- Asset-heavy cases arrive as renderer-visible cooked variants instead of being repaired in per-frame hot paths.

This document owns renderer-side performance architecture: CPU direct submission, GPU indirect submission, zero-readback constraints, material tables, shader prewarming, meshlets, visibility-buffer rendering, two-phase occlusion, stereo rendering, VRS, and profiling.

Avatar-specific asset transformation is owned by [Avatar Optimization And Virtualized Avatar Rendering Design](avatar-optimization-and-virtualized-rendering-design.md). The two designs intentionally meet at cooked asset boundaries: the renderer must expose the counters and fallback paths that make generated avatar variants measurable, while the optimizer must produce normal engine assets that CPU direct, GPU-driven, meshlet, visibility-buffer, cluster, and splat paths can consume.

## 2. Current XRENGINE Context

XRENGINE is Windows-first, OpenGL 4.6 is the primary production backend, and Vulkan/DX12-class explicit APIs are future-facing work. The current renderer supports:

- CPU direct mesh submission.
- Instrumented GPU indirect submission.
- Strict zero-readback GPU indirect submission.
- Deferred and forward material paths.
- Uber shader variants.
- GPU skinning and blendshape compute paths.
- GPU BVH, Hi-Z occlusion plumbing, meshlet experiments, and material-table work in progress.

### 2.1 Target form factor and frame budget

XRENGINE is a VR-first engine. Every performance number in this document is expressed against VR head-mounted display budgets, not desktop monitor budgets:

| Mode | Whole-frame app budget | Serial two-pass eye slice |
| --- | ---: | ---: |
| 72 Hz VR (Quest-class minimum) | 13.8 ms | ~6.9 ms |
| 90 Hz VR (Index/Vive baseline) | 11.1 ms | ~5.5 ms |
| 120 Hz VR (Index high / PSVR2) | 8.3 ms | ~4.1 ms |
| Desktop 60 Hz fallback | 16.6 ms | n/a |

The budget is for the complete submitted XR frame, not for one eye in isolation. The eye-slice column is only an internal warning for naive serial two-pass stereo and eye-dependent passes; single-pass stereo and shared compute work must be measured against the whole-frame budget. A render path that misses the 90 Hz whole-frame budget on a mid-range PC GPU is not a production VR path, regardless of how it looks on a 60 Hz desktop monitor.

### 2.2 Stereo rendering path

The production renderer must use a single-pass stereo strategy, not naive two-pass left/right:

- OpenGL: `GL_OVR_multiview2` for the geometry passes that support it; `gl_ViewID_OVR` selects per-view matrices.
- Vulkan: `VK_KHR_multiview` for geometry and depth passes; per-view masks for shadows when affordable.
- DX12: view-instancing where available, otherwise instanced stereo using `SV_RenderTargetArrayIndex`.
- Compute (culling, skinning, blendshape, Hi-Z build, splat sort): run once, not per-eye, where the producer is view-independent.

Any per-frame counter that counts "draw calls" must distinguish mono draws, multiview/view-instanced draws, and naive two-pass draws. Multiview primarily saves CPU submission and state setup; vertex, primitive, and pixel work still scale with the number of views depending on shader stage and hardware. The 270-draw avatar in the observed scene becomes 540 CPU-submitted draws under a naive two-pass renderer, which is non-trivial, so the doc treats single-pass stereo as a hard production target where backend and HMD support allow it.

### 2.3 Observed avatar baseline

Recent profiling around the unit-testing avatar scene (desktop monitor, mono) showed:

| Scenario | Observed result | Interpretation |
| --- | ---: | --- |
| CPU direct, no lights, skinned avatar active | about 16.9 ms render median | Roughly 59 Hz despite no direct lighting cost. |
| CPU direct, no lights, `AllowSkinning=false` | about 16.5 ms render median | Skeletal skinning was not the dominant steady-state cost. |
| GPU zero-readback, no lights | about 456 ms render median | Catastrophic path overhead despite zero readback bytes. |
| GPU zero-readback dry-run bucket dispatch | about 476 ms render median | Bucket draw dispatch alone did not explain the stall. |

The avatar case is important because it is representative of real user content:

- About 270 to 280 draw calls in the direct path.
- About 1.08 million triangles.
- 62 material slots in the indirect material-scatter path.
- Large texture/material/shader warmup pressure.
- Skinned and blendshape-capable mesh renderers.

Deferred shading and disabled lights remove lighting work, but they do not remove vertex processing, material binding, texture residency, shader warmup, submesh count, bone/blendshape buffer work, or draw submission overhead. The asset itself needs an engine-native optimization pass.

## 3. Performance Model

### 3.1 Draw calls are a symptom, not the root cause

A draw call is cheap only when all surrounding state is cheap. In practice, each draw can imply:

- Shader program or pipeline selection.
- Vertex array, index buffer, and vertex stream binding.
- Material parameter upload.
- Texture or descriptor binding.
- Uniform or SSBO range binding.
- Driver validation and hazard tracking.
- Backend command buffer work.
- Optional fallback shader compile or program link.

Reducing draw calls helps when those per-draw costs dominate. It can hurt when merging destroys culling granularity and sends too much invisible geometry to the GPU. The target is not "minimum draw count." The target is:

> Minimum expensive state changes while preserving enough culling granularity to avoid wasted GPU work.

### 3.2 CPU-driven rendering is still valid

CPU direct rendering should remain a first-class strategy for:

- Small to medium scenes.
- Editor diagnostics.
- Platforms or backends without reliable indirect-count support.
- Scenes with high material diversity but low object count.
- Debugging because the control flow is simple and inspectable.

A fast CPU path needs:

- No hot-path allocations.
- Stable command buffers handed from collection to rendering without copying.
- Material and program sorting when pass semantics allow it.
- Persistent-mapped or ring-buffered uploads.
- State-change counters and state caches.
- Shader prewarm before entering measurement.
- Texture upload budgets that cannot consume the frame.

### 3.3 GPU-driven rendering only wins when it is compact

GPU-driven rendering pays for extra compute stages and extra buffers. It wins when those stages replace large CPU submission work or reject large amounts of invisible work. It loses when it:

- Scans all possible materials, tiers, buckets, objects, or meshlets every frame.
- Uses upper-bound counts as real work.
- Performs broad barriers after every small dispatch.
- Reads GPU counters back to CPU in the same frame.
- Falls back to CPU safety-net draws during shader warmup.
- Requires per-frame material scatter for mostly static material layouts.
- Uses per-element `atomicAdd` for compaction instead of subgroup/wave partitioned prefix sum plus a single group-offset atomic.
- Has no defined behavior when an active-list / indirect-command buffer overflows its preallocated capacity (the contract must be: clamp, log, and surface a profiler warning, never silently truncate culled work).

Strict zero-readback means no CPU observation of GPU-produced visibility or count data in the shipping path. It does not automatically mean fast. A zero-readback path can still be slow if it replaces readbacks with broad full-scene scans or excessive state fan-out.

### 3.4 CPU and GPU bottlenecks look different

CPU-bound signatures:

- Render thread time is high while GPU timestamp time is low.
- `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData`, or uniform upload counts scale with visible object count.
- Frame time improves when draw calls or state changes are reduced.
- Frame time improves when shader/material warmup is complete.
- Nsight/RenderDoc show many tiny draws or repeated state setup.

GPU-bound signatures:

- GPU timestamp time tracks frame time.
- Vertex/primitive workload is high.
- Pixel overdraw, bandwidth, shadow, post, or compute passes dominate.
- Lowering resolution or MSAA changes frame time.
- LOD, culling, or material simplification improves frame time.

Synchronization-bound signatures:

- CPU appears blocked inside driver calls.
- Removing readbacks, query waits, or fences improves frame time.
- Barrier reductions improve frame time.
- GPU trace shows bubbles or poor overlap.

## 4. Renderer Design Principles

1. Treat renderable scene data as a database, not as per-frame objects.
2. Separate authored assets from cooked render data.
3. Keep hot-path data in SoA or compact indexed tables.
4. Keep material data GPU-resident and reference it by stable IDs.
5. Sort by expensive state when CPU-driven.
6. Generate compact active lists when GPU-driven; compact with subgroup prefix sums, not per-element atomics.
7. Never scan inactive material slots or buckets in production.
8. Never read GPU visibility, draw counts, or batch ranges back on the same frame in production.
9. Prewarm shader programs and pipeline state before a scene becomes interactive, and persist them to disk so subsequent runs skip link entirely.
10. Make diagnostics explicit opt-in modes, not hidden cost in shipping paths.
11. Preserve enough culling granularity to avoid exchanging CPU wins for GPU waste.
12. Keep profiling counters attached to the same concepts the renderer optimizes.
13. Render stereo single-pass (multiview / view-instancing). Two-pass stereo is a debug fallback.
14. Treat the depth/visibility prepass as the most valuable buffer in the engine; everything downstream (occlusion, SSAO, SSR, TAA/upscaler motion, deferred shading, virtual texturing feedback, splat sort) keys off it.

## 5. Target Renderer Architecture

### 5.1 Data flow

```text
Source assets
    -> import and analysis
    -> engine-native cooked model cache
    -> optional optimized variants and LODs
    -> optional cluster-virtualized avatar payload
    -> optional Gaussian-splat distant-LOD payload
    -> GPUScene registration
    -> material table and texture residency
    -> render graph (with transient-memory aliasing across passes)
    -> CPU direct, GPU indirect, visibility-buffer, meshlet, or splat strategy
```

The render graph is responsible for transient-resource lifetime and aliasing. Depth, velocity, SSAO/GTAO intermediates, bloom mips, splat sort buffers, and Hi-Z chains share physical memory wherever their lifetimes do not overlap; allocations should never be per-frame `glGenTextures` / `vkCreateImage` calls.

### 5.2 CPU responsibilities per frame

The CPU should do only the work that is naturally CPU-owned:

- Update camera and view constants.
- Upload dirty transforms.
- Upload dirty animation, bone, and blendshape state ranges.
- Upload dirty material parameters.
- Advance streaming budgets.
- Submit stable render-graph passes.
- Publish diagnostics from delayed, non-blocking reads.

The CPU should not:

- Rebuild all material tables every frame.
- Re-upload unchanged mesh or material state.
- Wait for GPU counters needed to draw the current frame.
- Link shader programs during visible frame work.
- Iterate every material slot or bucket when most are inactive.

### 5.3 GPU responsibilities per frame

The GPU should do work that benefits from massive parallelism:

- Frustum and distance culling.
- Hi-Z or occlusion culling when the depth source is valid (two-phase: cull against last-frame Hi-Z, draw phase 1, rebuild Hi-Z, cull rejected set against current-frame Hi-Z, draw phase 2).
- LOD selection.
- Active draw list compaction (subgroup partitioned prefix-sum + single group atomic).
- Per-pass classification.
- Indirect command generation.
- Optional material/state clustering.
- Meshlet expansion and per-meshlet culling.
- Cluster-virtualized avatar selection and per-cluster LOD.
- Gaussian-splat sort/composite for distant crowd LODs.

The GPU should not:

- Execute broad scans that exceed the cost of the direct CPU renderer.
- Run expensive barriers between every tiny stage.
- Recompute static material layouts unnecessarily.
- Generate work for inactive buckets because the CPU cannot observe counts.

### 5.4 Queue layout (Vulkan / DX12)

Work should be partitioned across queues:

- Graphics queue: depth prepass, opaque pass, transparency pass, post-process, present.
- Async compute queue: skinning, blendshape evaluation, two-phase Hi-Z build, GPU culling, indirect command generation, splat sort, virtual texture feedback, light-cluster build, particle simulation.
- Transfer queue: streaming uploads (textures, meshes, splat pages).

Async overlap is one way the renderer can recover otherwise idle GPU bubbles, especially when compute and graphics do not saturate the same units. It is not free and is not guaranteed: queue overlap must be proven in Nsight/RGP/PIX because async compute can regress when it competes for the same GPU resources or adds synchronization. OpenGL does not expose explicit queues; shared contexts can help overlap uploads, compilation, and some producer work, but they are not equivalent to Vulkan/DX12 queue scheduling.

## 6. CPU Direct Path Optimization

The CPU direct path is the baseline. It should be simple, correct, fast, and measurable before GPU-driven paths are judged.

Required properties:

- Visible collection produces stable `RenderCommandCollection` instances without per-frame allocation churn.
- `RenderCommandCollection.RenderCPU` submits draws in pass-compatible sorted order.
- `GLMeshRenderer.Render` avoids redundant program, VAO, texture, SSBO, and uniform updates.
- Per-material uniform and texture binding uses dirty version checks.
- Per-renderer bone/blendshape uploads use dirty ranges and shared buffers where possible.
- Texture streaming upload budgets are enforced after every chunk, not only before a large queue is drained.
- Shader and material variant adoption does not occur during measured render frames.

Optimization rules:

- Sort opaque draws by render pass, shader program, material layout, material ID, mesh/VAO.
- Keep transparent draw order separate and correctness-driven.
- Merge adjacent submesh draws only when material, skeleton, transform, pass, and culling behavior match.
- Use persistent mapped buffers with a 3-region ring guarded by `glFenceSync` / `VkFence`; the CPU writes region N while the GPU reads regions N-1 / N-2. Coherent mapping alone (`GL_MAP_COHERENT_BIT`) does not prevent CPU-on-GPU write hazards; the fence is load-bearing.
- Cache uniform locations, sampler bindings, and material layout bindings.
- Ban LINQ, captured closures, boxing, string formatting, and heap allocation in submission hot paths.
- Ban `foreach` over non-struct enumerators in submission hot paths (mirrors AGENTS.md hot-path rule). Iterate `Span<T>`, arrays by index, or `List<T>` via `CollectionsMarshal.AsSpan`.
- Batch barriers: when multiple compute dispatches share a sync requirement, issue one `glMemoryBarrier` / `vkCmdPipelineBarrier` covering the combined transition, not one per dispatch.
- Publish state counters so each optimization has a measurable effect.

### 6.1 SRP-batcher-equivalent constant buffer fast path

Before committing to a full material-table rewrite, the CPU direct path should adopt the cheaper Unity SRP-batcher pattern: a stable per-object constant block layout shared across all materials using the same shader. With a stable layout, the renderer only needs to rebind the per-object constant offset between draws of the same shader; per-material binds happen once per material, not once per draw.

Requirements:

- All materials authored for a given shader produce the same constant block layout (no reordered fields, no conditional members).
- Per-object constants (model matrix, prev-model matrix, bone palette offset, blendshape offset, material ID, custom-data slot) live in one persistent buffer; per-draw work is a single offset bind.
- Material constants live in a second persistent buffer addressed by material ID.
- Texture binds remain explicit until the bindless ladder in §7.3 is in place.

This path closes most of the gap with GPU-driven submission for scenes with low-to-medium object count, including the observed 270-draw avatar scene, without requiring a working indirect pipeline.

Acceptance targets:

- CPU direct should be the stable baseline for every benchmark scene.
- CPU direct should not allocate in steady-state render submission.
- Render-thread time should not spike because of shader linking, asset deserialization, or texture upload queue drain.

## 7. GPU Indirect And Zero-Readback Path

### 7.1 Required invariant

The production zero-readback path must satisfy:

> After the CPU uploads dirty scene and camera state, all visibility, batching, and draw-count decisions remain GPU-resident until presentation.

Allowed CPU work:

- Submit fixed render-graph passes.
- Bind stable global buffers and material tables.
- Issue fixed indirect-count draw calls.
- Consume delayed diagnostic readbacks outside the current frame dependency chain.

Disallowed CPU work:

- Reading visible draw counts to decide loop counts.
- Reading batch ranges.
- Reading material bucket counts.
- Mapping GPU-written buffers for current-frame decisions.
- Falling back to CPU mesh safety-net draws because the GPU path was not warmed.

Production two-phase occlusion:

Single-phase Hi-Z either over-draws (when culling against last-frame depth only) or stalls the GPU (when waiting for current-frame depth before any draw). The production zero-readback target uses the Nanite-style two-pass HZB pattern:

```text
1. Cull all visible candidates against LAST-frame Hi-Z
2. Draw phase-1 visible set (depth + opaque)
3. Build CURRENT-frame Hi-Z from phase-1 depth
4. Re-cull candidates rejected in step 1 against current-frame Hi-Z
5. Draw phase-2 set (the disocclusions / newly-visible objects)
6. Continue rest of the frame against the full depth buffer
```

This is the target for GPU-driven opaque geometry and cluster/meshlet paths. A single-phase path is still acceptable for early bring-up, diagnostics, editor views, or content classes where the second pass costs more than it saves; production VR profiles must record whether they used one-phase or two-phase occlusion.

### 7.2 Active-list generation instead of full bucket scans

The zero-readback path must not use "scan every material slot by every tier by every pass" as its steady-state architecture. That creates a fixed cost proportional to possible work, not visible work.

Target flow:

```text
Cull visible draw IDs
    -> classify pass/material/tier/state class
    -> emit compact active draw records
    -> build compact active bucket list
    -> sort or cluster if needed
    -> emit indirect commands and count buffers
    -> CPU issues fixed indirect-count calls per coarse state class
```

If the backend cannot issue one fully generic draw for all materials, the GPU should still compact the list of active buckets. The CPU may issue a fixed maximum number of coarse pass calls, but it must not iterate all theoretical material slots.

Compaction primitive contract:

- Use subgroup / wave (`KHR_shader_subgroup_arithmetic`, SM6 wave ops) partitioned prefix sums for stream compaction. One atomic per workgroup writes the group's base offset; lanes within the group derive their slot from the prefix sum locally.
- Do not use `atomicAdd` per surviving element. That serializes the compaction stage and is the most common cause of "the GPU-driven path is somehow slower than CPU."
- Allocate compaction output buffers from a pool sized to `max(historical_visible_count) * 1.5`. The shader must reserve output slots with an explicit capacity check. If capacity would overflow, clamp the written count, increment a profiler-visible `GpuCompactionOverflow` counter, and render a conservative fallback where possible (for example a coarser parent cluster or CPU-direct fallback next frame). Resize across frames, never mid-frame, and never silently truncate visible work without a diagnostic.

### 7.3 Material table path

Material diversity should be handled by indexed material tables, not by CPU rebinding per material where possible.

Target material model:

- Each material instance has a stable `MaterialID`.
- Shader variants declare their required material layout.
- Generated packers write material rows into GPU buffers.
- Texture references are descriptor indices, bindless handles, array indices, or virtual texture page references depending on backend.
- Draws carry `MaterialID`; shaders fetch material state from tables.
- Material rows are updated by dirty ranges, not rebuilt globally.

OpenGL can use:

- SSBO material tables.
- Texture arrays for compatible textures.
- Bindless texture handles where supported and allowed.
- Coarse fallback binding for unsupported cases.

OpenGL texture-binding fallback ladder (explicit, deterministic):

1. **Texture arrays** for groups of textures with identical format, mip count, and dimensions. This is the universal rung; always available, no extensions required.
2. **Bindless texture handles** (`ARB_bindless_texture`) when the driver advertises support and the runtime preference allows it. Solid on NVIDIA, partial/absent on Intel and AMD GL drivers — must runtime-probe, never assume. Bindless handles live in an SSBO indexed by material ID.
3. **Sparse texture pages** for very large atlas-equivalent backings when `ARB_sparse_texture` is available, paired with the virtual texturing design doc.
4. **Coarse bucket binding** as the last rung: group draws by texture-set identity and bind once per bucket. This is the rung that always works, including on machines where 1–3 fail.

The ladder is not optional; the renderer probes at startup, picks the highest available rung per backend/driver, and the profiler reports which rung is active. Avatar material consolidation is what makes rung 4 cheap when rungs 2-3 are not available; see [Avatar Optimization And Virtualized Avatar Rendering Design](avatar-optimization-and-virtualized-rendering-design.md).

Vulkan/DX12 should use:

- Descriptor indexing / bindless tables.
- Pipeline caches.
- Indirect-count drawing.
- Explicit barrier scheduling through render-graph metadata.

### 7.4 Shader and pipeline warmup

GPU-driven rendering cannot be benchmarked while shader programs are still being generated, linked, or adopted. Warmup must be scene-aware:

- Collect visible material/shader/pass combinations from the world.
- Include depth, shadow, deferred, forward, velocity, editor ID, and override passes.
- Include skinned, blendshape, instanced, and meshlet variants.
- Compile/link asynchronously before interactive measurement starts.
- Surface missing or failed variants in editor status.
- Avoid CPU fallback rendering once the scene enters a strict GPU path.

Shader warmup must be treated as part of asset loading, not as render-thread work.

Persistent pipeline cache:

Link results must survive across runs. The first time a project is opened, shader/program linking is expensive; subsequent runs must skip it.

- OpenGL: `glGetProgramBinary` / `glProgramBinary` (`ARB_get_program_binary`), keyed by source hash + driver version + GPU vendor/device string. Cache lives under `Build/Cache/ShaderBinary/<vendor>/<driver>/`.
- Vulkan: `VkPipelineCache` serialized to disk per device UUID + driver version under `Build/Cache/PipelineCache/`.
- DX12: PSO library (`ID3D12PipelineLibrary`) keyed similarly.
- Cache invalidation: on driver version change, on engine shader-source hash change, or on explicit `--clear-shader-cache` CLI flag.
- Caches must be cleared between cold-start benchmark runs and preserved between warm-start benchmark runs; the bench harness toggle is mandatory.

### 7.5 Barrier and synchronization policy

Explicit APIs and OpenGL compute paths both need a minimal synchronization model:

- Batch barriers by resource transition and stage. Concretely: when N adjacent compute dispatches all produce SSBO writes consumed by the next draw, issue ONE `glMemoryBarrier(GL_SHADER_STORAGE_BARRIER_BIT | GL_COMMAND_BARRIER_BIT)` after all N, not one per dispatch.
- Avoid a memory barrier after every tiny compute dispatch when a later grouped barrier is sufficient.
- Use delayed query readback for diagnostics.
- Do not call synchronous `glGet*`, blocking map, query wait, or fence wait in the render hot path.
- Keep debug validation modes opt-in and clearly reported in profiler manifests.
- GPU timestamp queries (`glQueryCounter`, `vkCmdWriteTimestamp`) can themselves perturb timing on some drivers (partial pipeline serialization around small dispatches). Timestamps must be gated by an opt-in profiler flag; production frames issue at most begin-of-pass and end-of-pass timestamps.
- Validation layers (Vulkan), `KHR_debug` callbacks, and `glEnable(GL_DEBUG_OUTPUT_SYNCHRONOUS)` must never be enabled in benchmark runs; the profiler manifest records which were active.

## 8. Meshlets, Visibility Buffer, And Virtual Geometry Direction

The long-term renderer should move toward cluster/meshlet and visibility-buffer rendering for dense and material-diverse assets:

- Import generates LODs and meshlets.
- GPUScene owns meshlet buffers.
- GPU culling expands visible draws to visible meshlet task records.
- Mesh/task shaders or equivalent compute+indirect paths cull and draw at cluster granularity.
- Material tables remain shared with traditional indirect rendering.
- Meshlets improve culling granularity after material/draw count has been consolidated.

### 8.1 Visibility buffer rendering

For scenes (and avatars) with high material diversity, a visibility buffer decouples geometry submission from shading. The depth/visibility prepass writes a single 32-bit (or 64-bit packed) target encoding `{InstanceID, ClusterID, TriangleIndex}` per pixel. Shading runs as a deferred full-screen compute pass that:

1. Reads `{InstanceID, TriangleIndex}` per pixel.
2. Fetches material ID via the instance table.
3. Tile-classifies pixels by material ID using subgroup ballot, producing per-material pixel lists.
4. Launches one compute shading dispatch per material tile that re-derives vertex attributes via barycentrics (Burns/Hunt analytical derivatives or screen-space derivatives of the synthesized attributes) and executes the material shader.

Why this matters for avatars: 62 material slots on a 1.08M-triangle avatar collapse to a material-independent visibility pass plus N material tile-shade dispatches where N is bounded by the number of materials that actually cover screen pixels at the current view. Depending on backend, that visibility pass may still be multiple indirect or meshlet draws, but material binding no longer fans out the geometry submission path.

Visibility-buffer rendering requires:

- A render-graph that can deliver `{InstanceID, ClusterID}` per-pixel cheaply (mesh shaders or cluster software rasterizer for small clusters).
- A material classification compute capable of fast tile-bucketing (`KHR_shader_subgroup_ballot` or DX12 wave ops).
- An analytical or finite-difference attribute interpolation path (Nanite uses analytical; this is the harder part to implement correctly with skinning).

### 8.2 Strategy ladder

This should not block the current CPU direct or traditional indirect work. The engine needs a ladder, and a scene may use different rungs for different objects in the same frame:

1. Optimized CPU direct (with SRP-batcher-equivalent per-object constant blocks).
2. Compact GPU indirect (two-phase Hi-Z, prefix-sum compaction, material tables).
3. Meshlet zero-readback where backend support is strong.
4. Visibility-buffer rendering for material-diverse opaque content (including hero avatars).
5. Cluster-virtualized avatar pipeline (see [avatar design §16](avatar-optimization-and-virtualized-rendering-design.md#16-cluster-virtualized-avatar-pipeline-nanite-class-for-skinned-characters)) for any avatar over budget.
6. Gaussian-splat impostors (see [avatar design §17](avatar-optimization-and-virtualized-rendering-design.md#17-gaussian-splat-distant-crowd-lod)) for distant avatars / crowds.
7. Virtualized geometry (Nanite-class) for extreme environment assets.

### 8.3 VR-specific rendering concerns

VR-only concerns that influence every other section:

#### 8.3.1 Stereo path

Mandatory single-pass stereo (multiview / view-instancing) where supported. Cost model:

- CPU draw submission and state setup: close to mono for well-implemented single-pass stereo.
- Vertex/primitive work: view-dependent; multiview amortizes submission but does not magically make two eye projections free.
- Geometry/tess/task/mesh shading: can scale poorly under stereo and must be profiled per backend.
- Pixel shading: roughly proportional to total covered eye pixels, mitigated by VRS / foveation.
- Shadow maps: typically rendered mono and sampled per-eye; per-eye shadow rendering is wasted work for shadows further than ~1m from the head.

#### 8.3.2 Variable Rate Shading and foveated rendering

VRS tier-2 (`VK_KHR_fragment_shading_rate`, DX12 VRS tier 2, NVIDIA `GL_NV_shading_rate_image`) can provide meaningful pixel-shading wins in VR by reducing peripheral shading rate, but the gain is content-, hardware-, and quality-threshold-dependent. Integration:

- Foveation rate image computed per-eye each frame.
- Eye-tracked HMDs (Quest Pro, Index extensions, PSVR2): VRS rate image driven by gaze.
- Untracked HMDs: fixed concentric-ring rate image, biased lower in the lens-distortion periphery.
- Vendor extensions (NVIDIA VRS Wrapper, OpenXR `XR_FB_foveation`) are alternates where available.

VRS is a renderer-side lever; the avatar optimizer does not implement it. But avatar screen-space-error metrics must be computed against effective shading rate, not raw pixel count.

#### 8.3.3 Motion vectors and upscaler contract

VR runs hot enough that vendor upscalers (DLSS, FSR2/3, XeSS) and TAA are load-bearing. Both require dense, correct motion vectors. The avatar pipeline must:

- Maintain a **previous-frame skinning output buffer** per skinned mesh (double-buffered position SSBO).
- Maintain a **previous-frame transform** per instance.
- Generate motion vectors in the velocity pass from `(currentClipPos - prevClipPos)` using the previous-frame skinned position, not the current-frame skinned position transformed by the previous matrix (that approximation ghosts on animated avatars).
- Follow the active temporal upscaler's jitter convention exactly. FSR2/XeSS-style integrations generally expect motion vectors without camera jitter unless a specific jitter-cancellation flag/path is enabled. If the optimizer reduces influences or quantizes weights differently between current and previous, motion vectors diverge and the upscaler ghosts.

This contract applies to every avatar rung (CPU direct, indirect, visibility buffer, cluster-virtualized, splat). Splats produce motion vectors from per-splat displacement; the pipeline must store splat positions at frame N and N-1.

#### 8.3.4 Reprojection-friendliness

VR runtimes (SteamVR motion smoothing, Oculus ASW, OpenXR space-warp) reproject when the app misses frame. The renderer must keep depth and motion-vector quality usable to runtime reprojection:

- Depth must be valid for every visible pixel including splats (write splat depth, not infinity).
- Velocity must be valid for every visible pixel, including UI and editor overlays (write zero, not undefined).
- Reprojection-incompatible passes (subpixel-sampled effects without TAA, certain post stacks) must be opt-out per VR project.

## 9. Measurement And Diagnostics

Every optimization must publish the counters it claims to improve.

### 9.1 Required per-frame counters

Renderer:

- Draw calls.
- Multi-draw calls.
- Indirect-count calls.
- Shader program switches.
- Pipeline switches.
- VAO binds.
- Buffer binds by target.
- SSBO/UBO binds.
- Texture binds or descriptor-table changes.
- Uniform calls.
- Buffer subdata / persistent upload bytes.
- Memory barriers by kind.
- Readback bytes.
- Mapped-buffer reads.
- Fallback events.

Scene and assets:

- Visible renderer count.
- Visible submesh count.
- Triangle count.
- Material slot count.
- Active material count.
- Texture count and resident texture memory.
- Texture upload jobs and upload time.
- Shader variants requested, warming, linked, failed.
- Skinning renderer count.
- Bone matrix upload bytes.
- Blendshape weight upload bytes.
- Skinned/blendshape compute dispatch count.

GPU-driven:

- Culled command count.
- Active bucket count.
- Empty bucket skips.
- Full bucket scans.
- Material scatter dispatches.
- Indirect command generation time.
- GPU cull time.
- GPU sort/compact time.
- Draw count buffer values through delayed diagnostics.
- `GpuCompactionOverflow` events (active-list / indirect-command buffer hit clamp).
- Active texture-binding rung (`TextureArray`, `Bindless`, `Sparse`, `CoarseBucket`).
- Active stereo path (`Mono`, `Multiview`, `ViewInstance`, `TwoPass`).
- Phase-1 vs phase-2 occlusion draw counts.

VR:

- Per-eye render time.
- Reprojection events from runtime.
- VRS shading-rate distribution (1x1, 2x1, 2x2, 4x4 pixel counts).
- Motion-vector validity coverage.

### 9.2 Benchmark discipline

Rules:

- Use Release/profile builds for performance targets.
- Warm shader and texture caches unless measuring cold startup.
- Clear caches between A/B variants when measuring cold behavior.
- Validate every env var override before launch.
- Keep the camera, scene, lights, and viewport identical between variants.
- Capture enough frames after warmup for stable medians and p10/p90.
- Separate render-thread wall time from GPU timestamp time.
- Report startup and steady-state separately.
- Report against a stated VR budget (§2.1) not just relative %.
- Disable all validation layers, GL debug callbacks, and synchronous debug output.
- Pin GPU clock to a stable state (`nvidia-smi -lgc`, AMD WattMan or driver overlay) when comparing within 5%.
- Disable Windows GameMode and HAGS state changes between runs, or pin them.

Invalid comparisons include:

- Measuring one strategy after another without equal warm cache policy.
- Comparing Debug and Release numbers.
- Measuring while shader variants are still linking.
- Treating no-drop logs as success without confirming process lifetime and frame samples.
- Treating zero readback bytes as proof of a fast GPU-driven path.
- Comparing mono desktop frame times against VR whole-frame budgets or serial two-pass eye slices without stating the stereo path.
- Treating GPU-timestamp totals as render-thread totals (and vice versa).

### 9.3 Sampling profiler integration

Counters tell you *what* changed; they do not tell you *which function* was responsible. CPU-bound diagnosis requires a sampling profiler:

- ETW + PerfView or `dotnet-trace` + SpeedScope: free, always-available on Windows.
- Superluminal: best signal-to-noise for hot-path C# sampling, supports custom annotations via the engine's profiler markers.
- Intel VTune: for cross-API GPU/CPU correlation when Nsight is not enough.
- Nsight Graphics / Nsight Systems: for GPU-side bubble and overlap analysis.

The engine's existing profiler markers must remain ETW/Superluminal-compatible. New hot-path systems should add named scopes.

## 10. Avatar Asset Pipeline Integration

Avatar optimization is now owned by [Avatar Optimization And Virtualized Avatar Rendering Design](avatar-optimization-and-virtualized-rendering-design.md). This renderer document keeps only the integration contract:

- The renderer must expose per-asset counters for draw calls, submeshes, material slots, shader variants, texture residency, active meshlets, skinning cost, blendshape cost, and active visibility-buffer/cluster/splat path.
- CPU direct, zero-readback GPU-driven, meshlet, and visibility-buffer paths must accept optimized avatar variants as normal engine assets.
- Material tables, texture streaming, meshlet caches, GPUScene records, and shader prewarm must be keyed by cooked variant identity, not by mutable source-import paths.
- The avatar optimizer may reduce material fan-out, generate meshlets, publish cluster-virtualized payloads, or publish distant LOD payloads, but renderer fallback paths must remain valid for unoptimized source assets.
- Runtime selection must report whether an avatar is using source mesh, optimized LOD, meshlet, visibility-buffer, cluster-virtualized, octahedral impostor, or Gaussian-splat representation.


## 11. Implementation Phases

### Phase 0: Instrumentation and shader-cache persistence

- Add stable counters for active materials, submeshes, draw calls, texture memory, influences, blendshapes, shader variants, meshlets, active texture rung, stereo path, compaction overflow, and one-phase vs two-phase Hi-Z draw counts.
- Add profiler rows that correlate model assets and cooked variants with render cost.
- Add delayed GPU timestamp coverage for indirect setup, material scatter, culling, compaction, draw, visibility-buffer, and stereo-dependent passes.
- Wire persistent shader/pipeline disk cache (§7.4) so all later phase measurements compare warm-start frame times consistently.
- Wire ETW / Superluminal / dotnet-trace markers around hot-path systems so sampled CPU profiles correlate with renderer concepts.

### Phase 1: CPU direct fast path

- Make steady-state CPU direct submission allocation-free.
- Add SRP-batcher-equivalent per-object constant-block uploads.
- Add persistent-mapped ring-buffer uploads with fence sync.
- Sort by expensive state where pass semantics allow it.
- Ensure shader linking, asset deserialization, and texture upload do not occur in measured render frames.

### Phase 2: Compact zero-readback GPU-driven path

- Replace full inactive bucket scans with compact active-list generation.
- Use subgroup prefix-sum compaction with explicit overflow clamp and profiler diagnostics.
- Implement production two-phase Hi-Z where measured beneficial, with one-phase fallback reported.
- Batch barriers and verify no current-frame GPU visibility/count readbacks occur in production.

### Phase 3: Material table and texture-binding ladder

- Finish material-table layouts for deferred, forward, depth, shadow, velocity, and visibility-buffer passes.
- Runtime-probe texture binding rungs: texture arrays, bindless resident handles, sparse/virtual texture handles, coarse CPU bucket fallback.
- Report the active rung per backend/driver.
- Keep shader variants and material table rows prewarmed and stable before interactive measurement.

### Phase 4: Meshlets, visibility buffer, and virtual geometry

- Generate renderer-ready meshlet payloads from cooked model variants.
- Implement visibility-buffer prepass and material tile shading for material-diverse opaque content.
- Keep CPU direct, indirect, meshlet, and visibility-buffer strategies selectable through the same strategy resolver.
- Add reference-path validation against deferred and forward shading.

### Phase 5: VR stereo and temporal contract

- Make single-pass stereo the production path where backend/HMD support exists.
- Report active stereo path (`Mono`, `Multiview`, `ViewInstance`, `TwoPass`) in profiler output.
- Generate dense, correct motion vectors for every strategy, including skinned and visibility-buffer paths.
- Validate against whole-frame XR budgets, not desktop mono frame times.

### Phase 6: Avatar integration contract

- Accept optimized avatar variants, cluster payloads, and distant-LOD payloads as normal engine assets.
- Keep unoptimized source-avatar fallback valid for editor diagnostics.
- Report active avatar representation per instance: source mesh, optimized LOD, meshlet, visibility buffer, cluster-virtualized, octahedral impostor, or Gaussian splat.
- Keep implementation details for the optimizer, cluster avatars, and splat LODs in the avatar design doc.

## 12. Acceptance Criteria

- CPU direct is stable, allocation-free in steady-state submission, and no longer performs render-thread shader linking or asset deserialization during measured frames.
- Zero-readback path performs no current-frame readbacks and does not full-scan inactive material buckets in production.
- Zero-readback path uses subgroup prefix-sum compaction, reports compaction overflow, and reports whether it used one-phase or two-phase Hi-Z occlusion.
- GPU-driven path is within 20 percent of CPU direct on scenes where it cannot win, and faster than CPU direct on high-object-count scenes where culling and compaction are effective.
- Single-pass stereo (multiview / view-instancing) is the VR production default where backend support exists; two-pass is a compatibility/debug fallback that must be reported.
- Persistent shader/pipeline disk cache makes warm-start frame 0 free of link work.
- Material-table and texture-binding ladder selection is visible in profiler output.
- Visibility-buffer rendering can shade material-diverse opaque content without geometry submission scaling linearly with material slot count.
- Optimized avatar variants, cluster payloads, and distant-LOD payloads enter the renderer as normal cooked assets with reference fallback paths.
- Profiler output explains whether a frame is CPU-bound, GPU-bound, synchronization-bound, or asset-streaming-bound, with sampling-profiler correlation.

## 13. Risks

- GPU-driven paths can become slower than CPU direct if active-list compaction is replaced by broad scans, or if compaction uses per-element atomics instead of subgroup prefix sums.
- Profiling can produce false conclusions if shader warmup, texture streaming, VRS settings, validation layers, or stereo path differ between variants.
- Bindless texture handles on non-NVIDIA GL drivers may regress silently; the fallback ladder must runtime-probe, not assume.
- Barrier placement can erase GPU-driven wins if every small dispatch forces a broad memory dependency.
- GPU timestamp instrumentation can perturb small passes if always-on query density is too high.
- Visibility-buffer shading can move cost from geometry submission into material tile classification and compute shading if material kernels are too divergent.
- Renderer/avatar integration can hide asset problems if the profiler reports only frame totals and not active representation per asset.
- VR-specific: failing the XR frame budget causes runtime reprojection that can mask the problem until users report discomfort.

Mitigation:

- Keep CPU direct as the correctness and baseline performance path.
- Keep renderer counters tied to asset and cooked-variant IDs.
- Gate cluster-virtualized and splat paths behind feature flags with reference fallback to traditional LOD and meshlet paths.
- Bench against the full XR frame budget and explicitly report the stereo path, not just desktop mono frame time.
- Validate every GPU-driven optimization with CPU wall time, GPU timestamps, and at least one sampling or vendor profiler capture.

## 14. Design Decisions

- CPU direct remains the correctness and baseline performance path.
- Zero-readback is a production path only when it is compact, warmed, and free of current-frame readbacks.
- Two-phase Hi-Z occlusion is the production target for GPU-driven opaque/cluster paths, with one-phase permitted only when measured cheaper and reported.
- Single-pass stereo (multiview / view-instancing) is mandatory for VR production backends that expose it; unsupported backends fall back explicitly rather than pretending two-pass has the same cost.
- Material tables are the preferred long-term solution for material diversity.
- Visibility-buffer rendering is the preferred renderer path for material-diverse opaque content; deferred and forward are retained for material classes incompatible with visibility-buffer reconstruction.
- Full material-slot and tier scans are diagnostic or transitional, not a production architecture.
- Meshlets and virtual-geometry payloads are cooked asset representations consumed by the renderer; asset-generation policy lives in the relevant asset or avatar design doc.
- Avatar optimization, cluster-virtualized avatars, and Gaussian-splat distant LODs are owned by [Avatar Optimization And Virtualized Avatar Rendering Design](avatar-optimization-and-virtualized-rendering-design.md).
- Distant-avatar or cluster-avatar paths must remain visible in renderer counters so performance conclusions do not collapse multiple representations into one opaque draw count.


