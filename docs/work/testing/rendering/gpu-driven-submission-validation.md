# GPU-Driven Submission Validation

Scope: runtime, hardware, profiler, benchmark, and soak checks for mesh submission strategies, GPU meshlet rendering, and GPU BVH overflow readback.

Architecture: [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md)

Code todos: [Production GPU-Driven Rendering Roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## Imported Checks

### From default-pipeline-gpu-submission-strategy-todo.md

- [ ] Run the targeted unit tests for GPU indirect dispatch and mesh submission strategy resolution.
- [ ] Run the Unit Testing World in the editor with GPU dispatch enabled. Expected: the resolved strategy renders the scene.
- [ ] Run the Unit Testing World in stereo and in at least one OpenVR session under `GpuIndirectZeroReadback`. Expected: per-view counters and indirect dispatch are correct, with no readback regressions.
- [ ] Sample a fixed number of frames of the Unit Testing World under `GpuIndirectZeroReadback`. Expected: frame time is equal to or better than the earlier `GPURenderDispatch` path. Record the numbers.
- [ ] Run `Report-NewAllocations` and any readback-detection report over the mesh submission paths. Expected: no new per-frame allocations in the production strategy hot path.
- [ ] Inspect `Build/Logs/.../profiler-gpu-pipeline-*.log`. Expected: profiler output and logs identify the active mesh submission strategy, with no unexpected readback markers.
- [ ] Run steady-state frames under `GpuIndirectZeroReadback`. Expected: `GpuReadbackBytes` stays at zero, with no `ReadUIntAt`, `MapBufferData`, or `GetDataArrayRawAtIndex` readback in the hot render path.

### From gpu-meshlet-zero-readback-rendering-todo.md

- [ ] Capture B1 and B2 `GpuIndirectZeroReadback` baseline logs and `GpuReadbackBytes` status for comparison. B1 is two Sponzas with lights off; B2 is B1 plus 100 idle skinned avatars.
- [ ] Profile the meshlet submission, expansion, culling, and shader-binding hot paths with logging policies enabled and disabled. Expected: no per-frame heap allocations.
- [ ] Run B1 under `GpuMeshletZeroReadback`. Expected: visual parity with `GpuIndirectZeroReadback` within material and pass tolerances.
- [ ] Run B2 under `GpuMeshletZeroReadback`. Expected: visual parity with `GpuIndirectZeroReadback`.
- [ ] Run high-material-diversity validation under `GpuMeshletZeroReadback`.
- [ ] Run dense static geometry validation. Expected: performance within 10 percent of `GpuIndirectZeroReadback`, and better on geometry-heavy scenes. Record the numbers.
- [ ] Run masked foliage validation under `GpuMeshletZeroReadback`.
- [ ] Run a stereo OpenVR or OpenXR smoke under `GpuMeshletZeroReadback` when backend support is available.
- [ ] Run a 100K command stress. Expected: `Stats.GpuReadbackBytes == 0`.
- [ ] Run a 30-minute `GpuMeshletZeroReadback` Release soak.
- [ ] Run OpenGL and Vulkan meshlet parity checks where backend support is available.

### From gpu-meshlet-strategy-split-todo.md

- [ ] Start the editor with `--unit-testing` under each of `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, `GpuMeshletInstrumented`, and `GpuMeshletZeroReadback` through `XRE_FORCE_MESH_SUBMISSION_STRATEGY`. Keep the logs. Expected: both meshlet strategies render the same image with `MeshletDebugDisplayEnabled = false`; `GpuMeshletInstrumented` reports nonzero meshlet statistics and, with `EnableGpuIndirectDebugLogging`, nonzero readback bytes; `GpuMeshletZeroReadback` reports `GpuReadbackBytes == 0` in steady state.

### From gpu-bvh-async-overflow-readback-todo.md

- [ ] Capture the GPU and CPU time of `GpuBvhTree.Build(...)` per call under `Measurement-Baseline-CpuDirect` and `Measurement-Baseline-GpuIndirectInstrumented`. Expected: `Build(...)` does not stall on the overflow-flag map.
- [ ] Run `Test-VulkanPhase3-Regression` and `Test-SurfelGi`. Expected: no BVH-related regressions.
- [ ] Force an overflow with a small `MaxLeafPrimitives` value or a large primitive count. Expected: the overflow warning appears within one or two frames.
- [ ] Set `XRE_HIZ_CULL_TRACE=1` and force an overflow. Expected: the trace line shows correct capacity and required figures.
- [ ] Run steady-state frames under `GpuIndirectZeroReadback`. Expected: the BVH records zero `GpuReadbackBytes`.
- [ ] Spot-check non-blocking fence polls on AMD and Intel drivers. Expected: a zero-timeout poll does not become a blocking wait under load.

### From rendering-profiler-and-benchmarking-todo.md

- [ ] Generate a profile capture with `Tools/Measure-GameLoopRenderPipeline.ps1` under `GpuMeshletZeroReadback`. Expected: the frame samples report the meshlet strategy as active, not a fallback to `GpuIndirectZeroReadback`. The last capture fell back.
