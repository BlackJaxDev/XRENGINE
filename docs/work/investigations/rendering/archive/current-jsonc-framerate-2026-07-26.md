# Current JSONC Framerate Investigation

## Problem

Determine why the current `Assets/UnitTestingWorldSettings.jsonc` Vulkan desktop
configuration starts slowly and renders well below its 60 Hz target.

## Conclusion

The configuration is CPU/render-thread bound, not GPU bound.

At a static camera, the unchanged configuration sustains about 30.8 FPS. The
GPU command buffer takes only 3.9 ms, while Vulkan scene command preparation,
recording, frame-data refresh, and tracked submission consume about 25 ms. The
collect thread then blocks for about 30 ms waiting for the render thread.

Camera motion is a second, larger CPU bottleneck. Visibility changes and four
directional shadow cascades dirty command chains and force large scene/shadow
re-records. Motion drops to about 10.5 FPS even though the GPU remains at
4.3 ms.

The enabled bounds and transform diagnostics add measurable work and obscure
the viewport, but they are not the main bottleneck. Disabling both only raises
the static result from 30.8 to 34.4 FPS and does not improve the moving-camera
result.

## Reproduction Configuration

- Backend: Vulkan, requested backend required.
- Build: Debug.
- World: default ImGui unit-testing world.
- Viewport: 1920x1080.
- Scene: one deferred Sponza import with
  `SpatiallyPartitionMeshesForOcclusion`.
- Directional light and procedural sky enabled.
- VSync disabled; render/update targets are 60 Hz.
- `GPURenderDispatch` disabled, producing the `CpuDirect` submission strategy.
- Effective CPU occlusion mode disabled despite the model partitioning flag.
- Mesh-bounds rendering enabled.
- Transform debug rendering, points, and lines enabled.
- Profiler frame logging disabled by the JSONC; the isolated diagnostic launch
  temporarily enabled `XRE_PROFILE_CAPTURE=1`.

## Steady-State Baseline

Controlled static cohorts used the same isolated process and camera:

| Configuration | Viewport commands | Derived FPS | Frame | GPU | Record | Submit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Current JSONC debug options | 216 | 30.85 | 32.23 ms | 3.92 ms | 13.22 ms | 12.03 ms |
| Transform debug off | 206 | 33.43 | 29.73 ms | 3.92 ms | 11.90 ms | 11.19 ms |
| Transform and mesh bounds off | 105 | 34.42 | 28.94 ms | 3.87 ms | 11.83 ms | 10.83 ms |

The current view contains 101 deferred Sponza partition commands and 112
`OnTopForward` debug method commands. Transform diagnostics account for ten of
the debug commands; mesh bounds account for 101. The GPU duration is effectively
unchanged across the A/B runs, so the debug cost is CPU command/frame-data
overhead rather than shader cost.

Other baseline measurements:

- update: 0.11 ms;
- visible collection: 1.59 ms;
- collect waiting for render: 30.38 ms;
- actual Vulkan present: 0.27 ms;
- steady scene: 124 maximum reported draws and 218,780 triangles;
- Vulkan validation errors: zero;
- steady pipeline compiles and texture uploads: zero.

The outer `Window present` CPU event includes the complete backend callback and
must not be interpreted as `vkQueuePresentKHR` time. The actual measured Vulkan
present is about 0.27 ms.

Even on clean primary-command-buffer reuse frames, the backend retains roughly
467-476 mesh descriptor variants, 1,401-1,428 allocated descriptor sets,
3,193-3,213 frame-data reservations, and 96 MiB of mapped frame-data arenas.
The scene's 101 CPU-direct partitions amplify that frame-data and submission
bookkeeping.

## Camera-Motion Bottleneck

The same four-second camera move was profiled with and without the visual debug
options:

| Configuration | Derived FPS | Frame | GPU | Scene record | Submit | Typical/max draws | Typical/max triangles |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Debug overlays on | 10.36 | 94.90 ms | 4.29 ms | 64.45 ms | 17.59 ms | 262/271 | 1.19M/1.21M |
| Debug overlays off | 10.47 | 93.81 ms | 4.29 ms | 61.72 ms | 20.02 ms | 267/267 | 1.21M/1.21M |
| Debug overlays and directional light off | 19.17 | 51.89 ms | 2.77 ms | 27.93 ms | 15.93 ms | 142/142 | 263K/263K |

Disabling the directional-light node also removes its cascaded shadow work, so
this A/B includes the light pass itself. The approximately 42 ms CPU reduction,
versus only 1.5 ms of GPU reduction, and the removal of about one million
shadow-pass triangles identify directional cascade recording as the major
motion amplifier.

During motion:

- primary command buffers are recorded instead of cleanly reused on about half
  of the frames;
- up to 121-134 command chains are recorded;
- scene-recording p95 reaches 120-134 ms;
- visible collection rises to 6-9 ms with the light active;
- two cold-view frames were rejected at `RecordDeferred` and reused the last
  completed content while newly visible pipelines were prepared.

The shadow log independently reports:

- 43 ms initial atlas allocation;
- grouped four-cascade renders of 34 ms and 352 ms during startup/warm-up;
- a configured shadow budget of 0.5-2.0 ms that those operations exceed.

Turning the light off does not materially cure the static backend cost. A later
static cohort with bounds and light disabled still averaged 31.5 FPS, 12.7 ms
recording, and 11.9 ms submission. Shadows therefore explain the large
camera-motion collapse, while the CPU Vulkan backend explains the low static
ceiling.

## Startup Costs

The existing matching run at
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-26_21-55-45_pid10268/`
reported its first rendered frame after 24.3 seconds. The isolated warm-cache
launch reported 16.8 seconds.

Startup is dominated by:

1. importing and spatially partitioning the Sponza OBJ;
2. texture/material and graphics-pipeline creation for the partitions;
3. directional shadow-atlas allocation and the first four-cascade render;
4. Debug-build and profiler instrumentation overhead.

Verbose FBX diagnostics and the missing extra texture-search directory create
startup log noise, but are not steady-state frame bottlenecks.

## Interaction Hitch

Vulkan GPU BVH raycast/readback is not implemented. The editor retries it about
every five seconds, rejects the request, and uses exact CPU mesh picking. On a
Sponza-sized scene this can create hover/click hitches, but it is not the
continuous 30 FPS bottleneck.

## Visual Evidence

Two baseline captures from different camera positions showed live, dense cyan
partition bounds and transform diagnostics covering Sponza. A capture after the
in-memory A/B disabled those options showed the scene without the cyan overlay.
The screenshot readback itself took 70-158 ms of GPU completion wait plus
158-219 ms of CPU processing; that is diagnostic-only overhead and is excluded
from normal frame cohorts.

Scratch evidence is under
`Build/_AgentValidation/20260726-current-jsonc-framerate/`. Isolated editor logs
are under
`Build/_AgentValidation/mcp-sessions/jsonc-framerate-baseline-0726/logs/`.

## Suggested Solutions

In priority order:

1. Profile and reduce Vulkan CPU work in scene command recording, frame-data
   refresh, and tracked submission/lifetime/layout publication. Clean command
   buffer reuse currently still costs roughly 12 ms to record/prepare and
   another 11-12 ms to submit.
2. Reduce directional-cascade invalidation on camera motion. Preserve and reuse
   shadow command chains, avoid re-recording all four cascades when their
   contents are unchanged, and make the configured shadow frame budget
   effective.
3. Test the GPU-driven submission path against this exact scene.
   `GPURenderDispatch=false` currently forces per-partition CPU-direct work.
4. Disable `RenderMeshBounds` and `RenderTransformDebugInfo` for ordinary use.
   This is an immediate 2-3 ms improvement and restores a readable viewport,
   but it is not a full fix.
5. Add Vulkan BVH picking/readback or throttle/avoid exact CPU mesh picking
   during passive hover.
6. Cache or preprocess the imported/partitioned Sponza scene to reduce launch
   time.

## RenderDoc Decision

`rdc doctor` passed, including RenderDoc 1.44 and Vulkan-layer checks. A frame
capture was not taken because engine GPU timestamps already isolate the GPU to
2.8-5.0 ms while CPU recording reaches 130 ms. RenderDoc replay would help
inspect an incorrect GPU resource or shader pass, but it cannot explain this
engine-side CPU recording/submission cost.

## Attempted Solutions

None were persisted. Debug preferences and the directional-light state were
changed only in the isolated process for controlled A/B measurements. The JSONC
remains unchanged, and the process was stopped after capture.

## Status

Diagnosis complete. No fix implemented.
