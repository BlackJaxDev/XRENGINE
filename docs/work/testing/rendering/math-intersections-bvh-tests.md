# Math Intersections BVH Validation

Scope: Validate the Math Intersections Unit Testing World BVH rigs and benchmarks for CPU scene, GPU scene, legacy CPU mesh, and GPU mesh traversal.

Architecture: [CPU Scene BVH](../../../architecture/rendering/cpu-scene-bvh.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md). Code todos: [Production GPU-Driven Rendering Roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md). Parent validation: [GPU-Driven Submission Validation](gpu-driven-submission-validation.md).

## Setup

1. Set `WorldKind` to `MathIntersections` in `Assets/UnitTestingWorldSettings.jsonc`.
2. Start the editor with `--unit-testing` or the `Editor (Unit Testing World)` launch profile.
3. Enable one BVH test in **Math Intersections Test Controls**.
4. Use **Run Benchmark** for workload-only copies.
5. Use **Run Benchmark With Debug Displays** to include visualization cost.

Tasks: `Build-Editor`, `Generate-UnitTestingWorldSettings`, and `Test-VulkanPhase3-Regression`.

Debug controls: use the `<mode> BVH Debug Controls` component for node visibility, query shape, colors, result channels, and GPU debug-node limits. Keep exactly one test active when benchmarking. Lack of GPU readiness must stay visible. Do not substitute a CPU implementation for a GPU test.

## Checks

### Interactive BVH rigs

Architecture: [CPU Scene BVH](../../../architecture/rendering/cpu-scene-bvh.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| CPU Scene BVH rig | Enable CPU Scene BVH and run Box, Sphere, Frustum, and Raycast queries. | The flat `CpuBvhRenderTree` matches brute force on every update. | Passed | 2026-07-22 |
| GPU Scene BVH rig | Enable GPU Scene BVH and run all query shapes after resources are ready. | GPU aggregate and classified-node results match the CPU brute-force oracle. | Passed | 2026-07-22 |
| Legacy CPU Mesh BVH rig | Enable legacy CPU Mesh BVH and run point, line, triangle, and finite raycast queries. | Candidate traversal and nearest hit match brute force. | Passed | 2026-07-22 |
| GPU Mesh BVH rig | Enable GPU Mesh BVH and run the same mesh queries. | GPU point, line, triangle, count, and nearest-distance results match the CPU oracle. | Passed | 2026-07-22 |
| Debug status sphere | Observe the status sphere for each rig. | Green means the latest validation passed, orange means GPU readiness is pending, and red means validation failed. | Passed | 2026-07-22 |

### Benchmark rigs

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Workload-only benchmark | Run **Run Benchmark** for each rig. | The benchmark summary reports every copy ready and passing validation. | Passed | 2026-07-22 |
| Debug-display benchmark | Run **Run Benchmark With Debug Displays** for each rig. | Visualization cost is included and validation still passes. | Passed | 2026-07-22 |
| Transfer metrics | Inspect `math-intersections-benchmarks.log`. | The log includes frame-time, spawn, teardown, transfer, readiness, validation, build, update, refit, query, node, primitive, and last-hit totals. | Passed | 2026-07-22 |

### Backend qualification boundary

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Hardware qualification handoff | Run the focused hardware matrix in [GPU Scene BVH External-Hardware Qualification](gpu-scene-bvh-external-hardware-qualification.md). | Interactive rigs do not replace vendor, backend, scale, and RenderDoc qualification. | Open | none |

## Hardware Matrix

See [GPU Scene BVH External-Hardware Qualification](gpu-scene-bvh-external-hardware-qualification.md).

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| Hardware qualification handoff | A rig passes locally but a vendor/backend bucket has no external evidence. | Keep the bucket unqualified until the hardware matrix passes. |
