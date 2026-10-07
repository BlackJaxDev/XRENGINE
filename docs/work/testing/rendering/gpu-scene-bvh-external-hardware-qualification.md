# GPU Scene BVH External-Hardware Qualification

Scope: Qualify GPU scene BVH correctness and performance on physical adapters, drivers, backends, and representative scenes.

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md). Code todos: [Production GPU-Driven Rendering Roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md). Parent validation: [GPU-Driven Submission Validation](gpu-driven-submission-validation.md).

## Setup

Use one bounded qualification session per hardware run. Store raw generated evidence in ignored validation output. Copy durable conclusions into a progress or investigation doc.

Tasks: `Build-Editor`, `Build-Editor-Release`, `Measurement-Baseline-CpuDirect`, `Measurement-Baseline-GpuIndirectInstrumented`, `Test-VulkanPhase3-Regression`, and `Test-SurfelGi`.

Launch profiles: `Editor (Unit Testing World)` and `Editor (Unit Testing World, Validation Layers)`.

Record adapter name, vendor and device IDs, VRAM, driver, OS build, power mode, backend, validation-layer state, build configuration, commit, scene, command count, view class, leaf capacity, deformation state, and occlusion state. Use `XRE_HIZ_CULL_TRACE=1` only for trace runs.

## Checks

### Adapter and backend parity

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run NVIDIA OpenGL and Vulkan parity. | Compare flat and BVH frustum results across representative inputs. | No false-negative visibility occurs. | Partial | 2026-07-18 |
| Run AMD OpenGL and Vulkan parity. | Repeat the same matrix on representative RDNA hardware. | No false-negative visibility occurs. | Open | none |
| Run Intel OpenGL and Vulkan parity. | Repeat the same matrix on representative Arc or Xe hardware. | No false-negative visibility occurs. | Open | none |
| Cover input shapes and leaf capacities. | Exercise zero, one, duplicate-center, degenerate, giant, invalid, clustered, moving, and rapidly expanding inputs with leaf capacities 1, 2, 4, 8, and 16. | Results match the flat oracle. | Open | none |

### Performance promotion

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Measure flat and BVH inputs. | Run 1K, 10K, 100K, and 1M commands where memory permits. | A bucket promotes only after BVH wins at two consecutive command counts by median timing. | Open | none |
| Measure distributions and dirty ratios. | Cover uniform, clustered, identical-center, long-thin, and giant-plus-small distributions with dirty ratios 0, 0.1, 1, 10, and 100 percent. | Worst frames fit the animated-scene GPU budget. | Open | none |
| Confirm no synchronous readback. | Inspect production submission counters. | BVH reports zero synchronous readback bytes. | Open | none |
| Reproduce promotion. | Repeat the winning bucket. | The second run reproduces the decision without validation errors or fallback. | Open | none |

### Runtime scenes

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Static scene | Let the scene stabilize. | No build, refit, AABB upload, overflow reset, or query allocation occurs after stabilization. | Open | none |
| Skinned or deformed scene | Run GPU-produced bounds and refit. | A direct-write failure restores CPU ownership and bounds without readback. | Open | none |
| Editor moved scene | Move transforms and culling offsets. | The correct dense AABB slot updates. | Open | none |
| Clustered scene | Run a clustered workload. | Internal-node rejection reduces visited commands in qualified buckets. | Open | none |
| Giant or expanding scene | Expand bounds and rebuild. | Normalization escape and retained-domain hysteresis rebuild without invalid nodes. | Open | none |
| Shadow, multi-view, and stereo | Run shadow views, multiple views, and stereo views. | Visibility matches the flat oracle and stays inside the frame budget. | Open | none |

### RenderDoc and backend diagnostics

Architecture: [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Inspect a BVH-containing capture when logs are not enough. | Verify build, refit, traversal, descriptors, buffers, and barriers. | Bounds are finite, compact nodes are reachable, and overflow paths are writable. | Partial | 2026-07-18 |
| Inspect backend validation logs. | Run with validation layers where applicable. | No VUID, synchronization, descriptor, device-loss, or render-graph ownership errors occur. | Partial | 2026-07-18 |

## Hardware Matrix
| Vendor | OpenGL 4.6 | Vulkan | Required result |
|---|---|---|---|
| NVIDIA | Partial on one RTX laptop. | Partial on one RTX laptop. | Complete both columns on a representative current driver. |
| AMD | Not tested. | Not tested. | Complete both columns on representative RDNA hardware. |
| Intel | Not tested. | Not tested. | Complete both columns on representative Arc or Xe hardware. |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| Performance promotion | One NVIDIA laptop run showed BVH won only the 1M-command, 10-percent-visible uniform cell. | Keep selector buckets on flat GPU culling until more hardware qualifies. |
