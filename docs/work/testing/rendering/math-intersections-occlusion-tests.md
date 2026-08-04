# Math Intersections Occlusion Tests

Last updated: 2026-08-03
Owner: Rendering
Status: Interactive qualification rigs implemented

The Math Intersections Unit Testing World contains three deterministic occlusion
rigs built from the same scene: a large blue wall, twelve red targets behind the
wall, two cyan targets outside it, and one orange target that repeatedly moves
from behind the wall into view. Reusing the geometry makes behavior and
telemetry comparable across modes.

| Test | Requested configuration | Passing evidence |
|---|---|---|
| CPU Async Query Occlusion | `CpuQueryAsync` + `CpuDirect` | Asynchronous query decisions resolve and reject hidden render commands. |
| CPU Rasterized Occlusion | `CpuSoftwareOcclusion` + `CpuDirect` | The wall is selected and rasterized as a software occluder, AABBs are tested, and hidden bounds are rejected. |
| GPU Two-Pass Hi-Z + GPU BVH | `GpuHiZ` + `GpuIndirectZeroReadback` | The GPU BVH is ready, submissions remain zero-readback, and telemetry reports persistent phase-1/phase-2 visibility with non-stale disocclusion recovery. |

The GPU acceptance contract follows
[Two-Pass Occlusion Culling](https://medium.com/@mil_kru/two-pass-occlusion-culling-4100edcad501):
draw last-frame-visible geometry first, build the current frame's depth pyramid,
then retest and draw newly visible geometry in a second phase. The qualification
must fail when the renderer only performs a single Hi-Z refine; a mode name or
an active GPU dispatch alone is not sufficient.

## Running the rigs

1. Set `WorldKind` to `MathIntersections` in
   `Assets/UnitTestingWorldSettings.jsonc` and launch the editor with
   `--unit-testing`.
2. Select the Math Intersections root node and open **Math Intersections Test
   Controls**.
3. Enable one occlusion test. Root-UI toggles keep these three tests mutually
   exclusive because each one owns the process-wide occlusion and submission
   configuration.
4. Keep the root selected to use the expanded `<test> Properties` group, or
   select the active test node to use the same controls there. Both views retain
   the same field objects and therefore the same live state.
5. Inspect **Validation Status** and **Frame Telemetry**. For the GPU rig, also
   inspect **GPU BVH** and **Hi-Z Phases**.

Enabling a rig captures the current occlusion mode, forced mesh-submission
strategy, and CPU-SOC force-visible flag. Disabling the last active rig or
deactivating the controller restores those values. The CPU SOC rig temporarily
disables force-visible debug behavior so a visually populated frame cannot be
mistaken for a culling pass.

The orange target's motion can be paused, restarted, slowed, or widened from
either UI location. It starts behind the wall and crosses a side edge so stale
visibility and disocclusion behavior remain visually observable.

## Current live qualification (2026-08-03)

- CPU async query passed on Vulkan: 15 commands tested and 8 culled in the final
  sample, with asynchronous decision/latency telemetry active.
- CPU rasterization passed on Vulkan: 30 bounds tested and 24 culled in the
  final sample, with the wall rasterized as an occluder.
- The GPU strategy resolved to `GpuIndirectZeroReadback`; its strategy-driven
  GPU BVH was ready with 18 logical primitives and 35 nodes, and zero-readback
  submissions advanced.
- The GPU qualification correctly failed because Hi-Z telemetry reported
  `single-phase-current-depth`, one-phase frames, and zero phase-two draws.
  Persistent visibility and the second disocclusion draw remain architecture
  work tracked by
  [GPU-Driven Occlusion Culling Architecture TODO](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md).
- The activated GPU pipeline variant also encountered an existing Vulkan
  `DeferredLightingDir` rewrite/compile failure, so its blank viewport captures
  are not accepted as occlusion evidence. Configuration, BVH, zero-readback,
  and Hi-Z phase telemetry remain valid evidence for the qualification failure.

Live screenshots, MCP responses, and the investigation record are linked from
[Math Intersections Occlusion Qualification Investigation](../../investigations/rendering/math-intersections-occlusion-qualification-2026-08-03.md).

