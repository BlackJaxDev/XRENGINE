# Math Intersections BVH Tests

Last updated: 2026-07-20
Owner: Rendering
Status: Four interactive test and benchmark rigs implemented

The Math Intersections Unit Testing World exposes four BVH-specific tests. Each
rig owns its workload and debug view, so the existing world benchmark harness
can duplicate the same algorithm with visualization either enabled or disabled.

| Test | Workload and correctness check | Debug rendering |
|---|---|---|
| CPU Scene BVH | Builds the flat `CpuBvhRenderTree`, moves three AABBs per logic tick, publishes the refit, and compares a moving AABB query against brute force. | Source AABBs, query hits, query bounds, and visited tree nodes. |
| GPU Scene BVH | Builds `GpuBvhTree` from 75 AABBs, queues one AABB update per logic tick onto the render thread, and refits after the topology is ready. It validates GPU buffer readiness and the expected primitive/node topology without readback. | Animated source AABBs plus compact nodes rendered directly from the node SSBO. |
| Legacy CPU Mesh BVH | Builds the legacy `SimpleScene` triangle BVH over a 200-triangle wavy grid and compares an animated segment query's hit count and nearest hit against brute force. | Full tree, visited nodes, query segment, and nearest hit. |
| GPU Mesh BVH | Enables the renderable-owned `GpuMeshBvh`, prepares its compact triangle tree, and validates source identity, packed triangles, and expected primitive/node topology. | Compact nodes rendered directly from the renderable's node SSBO over the source mesh. |

The status sphere above each rig is green when the latest completed validation
passes, orange while GPU resources or shaders are becoming ready for the first
time, and red on a completed validation failure. A temporarily pending GPU mesh
refresh retains the latest completed result. GPU tests remain GPU-only: lack of
initial readiness is visible and does not silently substitute a CPU implementation.

## Running the tests

1. Set `WorldKind` to `MathIntersections` in
   `Assets/UnitTestingWorldSettings.jsonc` and launch the editor with
   `--unit-testing`.
2. In **Math Intersections Test Controls**, enable one of the four BVH tests.
3. Inspect the animated debug view and status sphere. Keep exactly one test
   active when benchmarking.
4. Choose **Run Benchmark** for workload-only copies or **Run Benchmark With
   Debug Displays** to include the visualization cost.

The benchmark status and `math-intersections-benchmarks.log` include frame-time,
spawn/teardown, and transfer metrics plus BVH readiness, validation, build,
update/refit, query, node, primitive, and last-hit totals. CPU structures are
built during factory spawning so build cost is included in spawn time. GPU
structures are created on the render thread and their build/refit work is
included in the sampled frames.

## Validation expectations

- CPU scene and legacy CPU mesh tests require exact parity with their brute-force
  oracle on every update.
- GPU tests require the compact topology and buffers to become ready. Their debug
  lines consume the GPU node buffer directly, with no synchronous CPU readback.
- A benchmark is valid only when its final BVH summary reports every copy ready
  and passing validation.
- Hardware/backend qualification still follows
  [GPU Scene BVH External-Hardware Qualification](gpu-scene-bvh-external-hardware-qualification.md);
  these interactive rigs are focused development coverage, not a substitute for
  the full vendor, backend, scale, and RenderDoc matrix.

## 2026-07-20 validation

- Editor build: succeeded with zero warnings and zero errors.
- Targeted BVH tests: 63 passed, 0 failed, 0 skipped.
- Live OpenGL component results:

| Case | Ready | Passed | Builds | Updates/refits | Queries | Primitives | Nodes |
|---|---:|---:|---:|---:|---:|---:|---:|
| CPU Scene BVH | yes | yes | 1 | 182 | 183 | 75 | 149 |
| GPU Scene BVH | yes | yes | 1 | 473 | 0 | 75 | 75 |
| Legacy CPU Mesh BVH | yes | yes | 1 | 181 | 182 | 200 | 399 |
| GPU Mesh BVH | yes | yes | 1 | 479 | 0 | 200 | 399 |

Visual screenshot acceptance was blocked in the validation worktree by an
unrelated render-graph self-cycle in `RenderMeshesTraditional`; both GPU
workloads were deliberately verified through their render-thread counters and
buffer/topology state instead of treating black captures as visual evidence.
