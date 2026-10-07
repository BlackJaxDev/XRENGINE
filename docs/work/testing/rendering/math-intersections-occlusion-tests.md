# Math Intersections Occlusion Validation

Scope: Validate the three occlusion rigs in the Math Intersections Unit Testing World. The rigs use the same wall, hidden targets, visible controls, and moving disocclusion target so the modes are comparable.

Architecture: [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md), [CPU Software Occlusion](../../../architecture/rendering/cpu-software-occlusion.md), [GPU Hi-Z Occlusion Culling](../../../architecture/rendering/gpu-hiz-occlusion-culling.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md)  
Code todos: [CPU Async Query Camera Motion](../../todo/rendering/cpu-async-query-camera-motion-todo.md), [Masked Software Occlusion Culling](../../todo/rendering/masked-software-occlusion-culling-todo.md), [GPU-Driven Occlusion Culling Architecture](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md)

## Setup

- Build task: `Build-Editor`.
- Launch profile: `Editor (Unit Testing World)`.
- Environment variables: `XRE_WORLD_MODE=UnitTesting` and `XRE_UNIT_TEST_WORLD_KIND=MathIntersections`.
- Alternative setup: set `WorldKind` to `MathIntersections` in `Assets/UnitTestingWorldSettings.jsonc` and launch the editor with `--unit-testing`.
- In the editor, select the Math Intersections root node. Open **Math Intersections Test Controls**.
- Enable only one occlusion test at a time. The controls make the three rigs mutually exclusive because each rig owns process-wide occlusion and submission settings.
- Keep the root selected to use the expanded properties group, or select the active test node. Both views retain the same live fields.
- Inspect **Validation Status** and **Frame Telemetry**. For the GPU rig, also inspect **GPU BVH** and **Hi-Z Phases**.

## Checks

### CPU Async Query Occlusion Rig

Architecture: [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run CPU async query rig. | Enable **CPU Async Query Occlusion Test**. It requests `CpuQueryAsync` and `CpuDirect`. | Hardware queries submit and resolve asynchronously. Hidden render commands are rejected after warmup. | Open | 2026-08-03 passed on Vulkan with 15 commands tested and 8 culled in the final sample. |
| Move the orange target. | Pause, restart, slow, or widen the orange target motion from either UI location. | The target starts behind the wall and crosses a side edge. Stale visibility and disocclusion remain visible in telemetry. | Open | none |
| Restore settings after the rig. | Disable the last active rig or deactivate the controller. | The previous occlusion mode, mesh submission strategy, and CPU SOC force-visible value are restored. | Open | none |

### CPU Rasterized Occlusion Rig

Architecture: [CPU Software Occlusion](../../../architecture/rendering/cpu-software-occlusion.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run CPU rasterized occlusion rig. | Enable **CPU Rasterized Occlusion Test**. It requests `CpuSoftwareOcclusion` and `CpuDirect`. | The wall is selected and rasterized as a software occluder. AABBs are tested. Hidden bounds are rejected. | Open | 2026-08-03 passed on Vulkan with 30 bounds tested and 24 culled in the final sample. |
| Confirm debug bypass is disabled for the rig. | Enable the CPU SOC rig when `CpuSocDebugForceVisible` was on. | The rig disables force-visible behavior while it runs, so visible frames cannot be mistaken for culling. | Open | none |

### GPU Two-Pass Hi-Z + GPU BVH Rig

Architecture: [GPU Hi-Z Occlusion Culling](../../../architecture/rendering/gpu-hiz-occlusion-culling.md), [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md)
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run GPU two-pass rig. | Enable **GPU Two-Pass Hi-Z + GPU BVH Test**. It requests `GpuHiZ` and `GpuIndirectZeroReadback`. | GPU BVH is ready. Zero-readback submissions advance. Hi-Z reports two-phase current-depth execution. | Open | 2026-08-03 GPU BVH was ready with 18 logical primitives and 35 nodes. The rig correctly failed because the renderer reported `single-phase-current-depth`. |
| Verify two-pass acceptance. | Inspect **Hi-Z Phases** in the active test UI. | The rig passes only when telemetry reports two-phase GPU Hi-Z. A mode name or an active dispatch is not enough. | Open | none |
| Verify visual evidence. | View screenshots from more than one camera position. | The viewport is not accepted as evidence if a separate shader compile or pipeline error produces a blank image. | Open | 2026-08-03 blank viewport captures were not accepted because a `DeferredLightingDir` rewrite or compile failure was present. |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
| GPU two-pass qualification | The 2026-08-03 run reported `single-phase-current-depth`, one-phase frames, and zero late draws. | [Math Intersections Occlusion Qualification Investigation](../../investigations/rendering/archive/math-intersections-occlusion-qualification-2026-08-03.md), [GPU-driven occlusion todo](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md) |
| GPU viewport capture | The activated GPU pipeline variant hit a `DeferredLightingDir` rewrite or compile failure. Blank captures were not accepted as occlusion evidence. | [Math Intersections Occlusion Qualification Investigation](../../investigations/rendering/archive/math-intersections-occlusion-qualification-2026-08-03.md) |
