# GPU Scene BVH External-Hardware Qualification

Last updated: 2026-07-18
Owner: Rendering
Status: Qualification pending on representative adapters and scenes

This document is the promotion gate for the implementation described in
[GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md). It records
evidence that necessarily depends on physical adapters, drivers, backends, and
representative runtime content. Missing hardware is reported as **not tested**;
results from one vendor or backend must never be inferred for another.

The engine implementation is complete. Until a bucket qualifies, the selector
keeps that bucket on flat GPU culling. The observable flat fallback and runtime
strategy kill switch remain part of the production architecture.

## Evidence contract

Create one bounded run root per qualification session under
`Build/_AgentValidation/<timestamp>-gpu-bvh-qualification/`. Record:

- adapter name, vendor/device IDs, VRAM, driver, OS build, and power mode;
- backend, validation layers, build configuration, commit, and scene revision;
- command count, distribution, dirty and visible ratios, view class, leaf
  capacity, and whether deformation or occlusion was active;
- build, refit, traversal, command-emission, transfer, barrier, queue-pressure,
  retained-capacity, worst-frame, and synchronous-readback measurements;
- exact log, report, screenshot, and RenderDoc capture paths.

Durable conclusions belong in
`docs/work/progress/rendering/gpu-bvh-scene-tree-implementation.md`; raw captures
and generated tables remain disposable evidence under the run root.

## Adapter and backend matrix

| Vendor | OpenGL 4.6 | Vulkan | Required result |
|---|---|---|---|
| NVIDIA | Partial: RTX 4070 Laptop benchmark and parity evidence | Partial: validated runtime and BVH-containing RenderDoc capture on RTX 4070 Laptop | Complete both columns on a representative current driver |
| AMD | Not tested | Not tested | Complete both columns on representative RDNA hardware |
| Intel | Not tested | Not tested | Complete both columns on representative Arc/Xe hardware |

For each cell, randomized flat-versus-BVH frustum parity must cover zero, one,
duplicate-center, degenerate, giant, invalid, clustered, moving, and rapidly
expanding inputs; leaf capacities 1/2/4/8/16; low/medium/high visibility; and
single, multiple, and stereo views. No false-negative visibility is permitted.

## Performance promotion rule

Measure identical flat and BVH inputs at 1K, 10K, 100K, and 1M commands where
memory permits. Cover uniform, clustered, identical-center, long-thin, and
giant-plus-many-small distributions; dirty ratios 0/0.1/1/10/100%; visible
ratios near 0/10/50/100%; and one, multiple, and stereo views.

A selector bucket may be promoted only when:

1. BVH culling wins at two consecutive command counts using median timings;
2. its build/refit/quality-maintenance worst frames fit the recorded animated-
   scene GPU budget;
3. queue or ray-stack pressure remains observable and conservatively recovers;
4. production submission reports zero synchronous BVH readback bytes; and
5. a second run reproduces the decision without validation errors or fallback.

Do not encode a universal command-count threshold from a single-adapter result.

## Runtime scene matrix

Every backend/vendor cell must exercise:

| Scene class | Required observation |
|---|---|
| Static | No build, refit, AABB upload/copy, overflow reset, or query allocation after stabilization |
| Skinned/deformed | GPU-produced bounds request refit without synchronous readback; direct-write failure restores CPU ownership and bounds |
| Editor moved | Transform and culling-offset changes update the correct dense AABB slot |
| Clustered | Internal-node rejection materially reduces visited commands in qualifying buckets |
| Giant/expanding | Normalization escape and retained-domain hysteresis rebuild without invalid nodes |
| Shadow | Shadow-view visibility matches the flat oracle |
| Multi-view | Independent view masks reuse one topology without false negatives |
| Stereo/XR | Both eyes remain correct under the stereo selector bucket and frame budget |

Capture at least two materially different camera positions for every visual
runtime validation. An artifact that does not move with the camera must be
treated as possible stale or uninitialized sampling until disproved.

## RenderDoc and backend validation

Use backend logs first. When logs are inconclusive, inspect a BVH-containing
RenderDoc capture in an open-work-close session and verify:

- build stages execute in order with dispatch-local constants and descriptor
  ranges belonging to that dispatch;
- command AABBs are finite and ordered, compact nodes use the documented
  48-byte layout, the root has no parent, and all leaves are reachable;
- build/refit producers are visible to traversal consumers with the expected
  barriers;
- overflow and diagnostics bindings are writable even when a caller omits an
  optional target;
- traversal overflow, ray-stack pressure, and flat fallback are observable;
- Vulkan logs contain no VUID, synchronization, descriptor, device-loss, or
  invalid render-graph ownership errors.

Export suspicious buffers or render targets and record their event IDs. Close
the replay session and restore any temporary validation layer, environment
override, or Unit Testing World setting after the run.

## Current qualification record

The 2026-07-18 RTX 4070 Laptop/OpenGL benchmark covered 20 production-shader
cells. BVH won only the 1M-command, 10%-visible uniform cell (3.17x); flat won
the other 19, so no selector bucket qualified. A five-row 10K maintenance sweep
covered every requested dirty ratio and leaf capacity across representative
visibility and view classes; all rows matched the flat oracle with no build or
traversal overflow.

The 2026-07-18 RTX 4070 Laptop/Vulkan runtime used the standard validation layer
and completed a forced BVH build/traversal RenderDoc inspection. The definitive
capture is
`Build/_AgentValidation/20260718-gpu-bvh-scene-tree/renderdoc/xrengine-vulkan-bvh-final-inband-reset-late_frame60.rdc`.
It established dispatch-local uniform ownership, a non-null stable overflow
descriptor, ordered reset/build/traversal events, 251 reachable compact nodes,
126 valid command leaves, and zero invalid bounds, topology, ranges, overflow,
or RenderDoc log messages. This is correctness evidence for that adapter only;
it does not promote a performance bucket or satisfy AMD/Intel qualification.
