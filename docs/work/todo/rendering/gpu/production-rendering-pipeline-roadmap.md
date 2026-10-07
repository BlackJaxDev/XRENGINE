# Production GPU-Driven Rendering TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md), [GPU Record Layouts](../../../../architecture/rendering/gpu-record-layouts.md), [Material Binding Policy](../../../../architecture/rendering/material-binding-policy.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md)

## Current State
`GPUScene` owns the split scene database, meshlet ranges, LOD tables, material state, transform buffers, bounds buffers, and view data. `GpuIndirectZeroReadback` consumes GPU-written counts without current-frame CPU readback. `GpuMeshletZeroReadback` has a completed Vulkan EXT production closeout for the unconditional meshlet gate. Two-pass Hi-Z occlusion code exists in `GPURenderPassCollection.TwoPassOcclusion.cs`, and the Vulkan scene-database buffer-device-address prototype has shader consumers through `HybridRenderingManager` and `ISceneDatabaseDeviceAddressBackendCapability`.

## Open Code Items

### Measurement and diagnostics
- [ ] Record `GpuReadbackBytes` at every instrumented readback site. `GPURenderPassCollection.CullingAndSoA.cs`, `GPURenderPassCollection.IndirectAndMaterials.cs`, `GPUScene.CommandBuffers.cs`. Done when: each instrumented `MapBufferData` read records bytes and zero-readback paths record none.
- [ ] Add render-graph metadata for GPU-driven preparation stages. `XREngine.Runtime.Rendering/RenderGraph/`, `GPURenderPassCollection`. Done when: counter reset, cull, LOD selection, transparency classification, sort and key build, material-tier scatter, and indirect-count draw submission are named passes with resource declarations.
- [ ] Split fixed GPU work from instrumented CPU readback time. `RenderPipelineGpuProfiler`. Done when: profiler output shows readback time in its own group.
- [ ] Add a GPU pass test that executes a zero-readback pass and asserts `GpuReadbackBytes == 0`. `XREngine.UnitTests/Rendering/`. Done when: the test executes a real zero-readback pass. Needs owner clearance for new test code.
- [ ] Add a positive test for instrumented readback accounting. `XREngine.UnitTests/Rendering/`. Done when: `GpuIndirectInstrumented` with readbacks enabled reports nonzero `GpuReadbackBytes`. Needs owner clearance for new test code.

### Temporal and occlusion
- [ ] Add static-scene visibility reuse. `GPURenderPassCollection`, `GPUScene`. Done when: unchanged BVH generation, visible count, camera view-projection, and pass set reuse a rotated previous visibility buffer without aliasing.
- [ ] Add per-light shadow Hi-Z culling. `GPURenderPassCollection`, shadow passes, Hi-Z shaders. Done when: each active shadow caster can use a shadow HZB and reports accepted and rejected draw counts.
- [ ] Add stereo Hi-Z texture-array support. `GPURenderPassCollection.Occlusion.cs`, Hi-Z shaders. Done when: stereo array views do not route through `DepthUnsupportedView` and render without mono-only occlusion artifacts.

### Residency and streaming
- [ ] Define a page-table buffer abstraction over mesh atlas tiers. `GPUScene`, mesh atlas ownership. Done when: GPU consumers use page IDs that can later map to sparse resources.
- [ ] Add score-based dynamic-tier mesh eviction. `GPUScene`, LOD streaming service. Done when: eviction uses predicted reuse, screen impact, pass importance, and temporal stability.
- [ ] Add a Vulkan sparse-residency proof of concept for `XREngine.VRClient` behind a feature flag. Vulkan renderer, resource lifetime code. Done when: `vkQueueBindSparse` and rebind run through render-graph synchronization without production enablement.
- [ ] Add streaming-tier upload telemetry. Profiler and mesh LOD streaming code. Done when: bytes per frame, request-to-visible latency, and oldest pending age appear in profile output.
- [ ] Add GPU LOD usage feedback. `GPURenderLODSelect.comp`, `GPUScene`. Done when: the CPU can drain last-used or resolved-level feedback without a current-frame stall.
- [ ] Replace direct LOD request mapping with a fence-gated readback ring. `GPUScene.ServiceLodStreamingRequests()`. Done when: requests and usage are consumed N frames late behind an `XRGpuFence`.
- [ ] Add mesh LOD eviction and hysteresis. `GPUScene`, LOD table ownership. Done when: nonessential LODs evict under memory pressure while LOD0 and active command meshes remain resident.
- [ ] Split per-LOD cooked payloads and lazy CPU residency. Asset import and `RenderableMesh`. Done when: nonresident LOD CPU data can be dropped and reloaded from cooked data on demand.
- [ ] Route geometry uploads through a unified budget. Mesh and texture streaming schedulers. Done when: mesh and texture uploads share one bytes-per-frame render-work budget.
- [ ] Reclaim atlas ranges for evicted LODs. Mesh atlas tier storage. Done when: evicted LOD vertex and index ranges return to a free list or a compaction path.
- [ ] Add mesh streaming telemetry parity with texture streaming. Profiler and diagnostics. Done when: resident bytes, latency, pending age, resolved-versus-selected histogram, eviction count, and thrash count are visible.

### Meshlet and zero-readback test code
- [ ] Add `GpuMeshletZeroReadback_StatsGpuReadbackBytesRemainZero`. `XREngine.UnitTests/Rendering/`. Done when: a meshlet zero-readback frame asserts zero readback bytes. Needs owner clearance for new test code.
- [ ] Add `Meshlet_TaskShaderCull_MatchesBVHFrustumResults`. `XREngine.UnitTests/Rendering/`. Done when: task-shader frustum results match `bvh_frustum_cull.comp` for the same fixture. Needs owner clearance for new test code.
- [ ] Add `Meshlet_SharedBVHCull_ThenMeshletExpansion`. `XREngine.UnitTests/Rendering/`. Done when: BVH culling output feeds `GPURenderExpandMeshlets.comp`. Needs owner clearance for new test code.
- [ ] Add hardware-skipping meshlet integration tests for NVIDIA mesh shader, EXT mesh shader, and Vulkan EXT parity. `XREngine.UnitTests/Rendering/`. Done when: each test runs on matching hardware and skips with a reason elsewhere. Needs owner clearance for new test code.
- [ ] Backfill zero-readback material scatter, tiered-atlas, LOD transition, and meshlet fallback tests. `XREngine.UnitTests/Rendering/`. Done when: empty-bucket, draw-count, dynamic add/remove, defrag, streaming no-stall, dither, and fallback cases have deterministic coverage.
- [ ] Add a live GPU fence test for BVH overflow readback. `XREngine.UnitTests/Rendering/GpuBvhAsyncOverflowReadbackTests.cs`. Done when: OpenGL and Vulkan hardware run or skip with a reason. Needs owner clearance for new test code.

### Model cache handoff
- [ ] Extract the remaining reusable mesh-core codec for broad model and prefab hydration. Asset binary cache and meshlet codec code. Done when: standalone and model-container mesh-core serialization share one codec.
- [ ] Hydrate meshlets and LODs from broad model binary warm hits. Model binary cache and runtime hydration. Done when: a warm hit reports zero parser and builder work for meshlets and LODs.

## Decisions Needed
- [ ] Choose the v1 policy for OpenGL sparse mesh residency. Owner: rendering lead.
- [ ] Choose which meshlet lanes must pass the meshlet promotion gate for v1 and which lanes are deferred. Owner: rendering lead.

## Out Of Scope
- Texture virtual page residency. See texturing work.
- VR frame pacing and mirror policy. See VR rendering performance work.
