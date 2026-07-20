# Vulkan Primary Command Recording Fast Path TODO

Last Updated: 2026-07-20
Owner: Rendering / Vulkan
Status: Active supporting tracker for Vulkan Core Hardening Phase 5.2A-5.2C
Execution: Current worktree only; do not create or switch branches for this effort.

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-render-stalls.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-32-2026-07-01-12-48-23-916-ba9dd90f.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-28-2026-07-01-12-48-21-719-86905924.log`

Related local docs:

- [Canonical Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md)
- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md)
- [Compact Zero-Readback Rendering TODO](compact-zero-readback-rendering-todo.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Vulkan Dynamic Rendering Migration TODO](../vulkan-dynamic-rendering-migration-todo.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)

## Goal

Make Vulkan primary command buffer recording cheap enough that desktop and VR
frames are not dominated by CPU-side recording. GPU work can still be optimized
afterward, but the first target is the measured render-thread hot path:
`Vulkan.RecordPrimary.MainOpLoop`.

## Issue

The July 1 frame logs show `Vulkan.RecordPrimary.MainOpLoop` as the dominant
FPS-drop leaf:

- 706 drop records reached `Vulkan.RecordPrimary.MainOpLoop`.
- Average drop frame for that leaf was about 162 ms.
- The worst recorded drop reached about 507 ms.
- In the VR/stereo pipeline dump, GPU pipeline time averaged about 20 ms while
  render-thread wall time averaged about 239 ms.
- In the desktop pipeline dump, GPU pipeline time averaged about 7 ms while
  render-thread wall time averaged about 149 ms.

This means the renderer is spending far more time building or coordinating
Vulkan work than the GPU spends executing the named pipeline work. The primary
recording loop covers frame op iteration, render pass/context changes, barrier
emission, descriptor/pipeline binding, draw recording, and texture upload ops.
The current profiler label is too broad to identify which of those sub-costs is
responsible in steady state.

The current source also keeps primary reuse behind
`VulkanPrimaryCommandBufferReuseSafe = false` because mutable descriptor and
GPU-publication generations are not complete in the variant key. Static
`CpuDirect` frames therefore force fresh primaries, while GPU-driven frames may
also force fresh recording merely because their GPU-resident outputs are marked
mutable. Removing that quarantine safely is a Phase 5.2A deliverable, not an
optional tuning experiment.

## Why This Matters

VR budgets are around 11.1 ms at 90 Hz and 13.9 ms at 72 Hz. A 150-250 ms
render-thread frame is not a tuning problem; it is a frame-pacing failure. It
also hides true GPU bottlenecks because GPU timestamps are small compared with
the CPU gap.

If this path stays expensive, later work on GTAO, light combine, meshlets,
visibility buffers, or XR frame pacing will not show their real benefit.

## Fix Direction

- Split `Vulkan.RecordPrimary.MainOpLoop` into durable sub-scopes:
  op dispatch, context change, render pass begin/end, barrier planning,
  barrier emission, descriptor writes/binds, pipeline binds, draw calls,
  upload ops, secondary command buffer execution, and debug label emission.
- Add per-frame counters for op count, context changes, render pass switches,
  barrier count, descriptor bind count, pipeline bind count, draw count,
  command-buffer bytes or command count where available, and render-thread
  allocations.
- Make steady-state recording allocation-free. Treat new heap allocations,
  LINQ, captured closures, boxing, string formatting, and class enumerator
  `foreach` inside command recording as bugs.
- Cache static render work where safe. Static skybox, static opaque meshes,
  stable full-screen passes, and unchanged shadow passes should be candidates
  for secondary command buffer reuse or precompiled frame-op ranges.
- Validate every cached primary/secondary range with the canonical immutable
  command dependency signature. Distinguish structural, binding-identity, and
  data-only changes; value publication into a safe frame slot must preserve
  compatible recorded topology.
- Keep Vulkan dynamic data in capacity-backed, frame-indexed upload/storage
  arenas with stable bindings. Ordinary transform/material/debug-line updates
  must not recreate exact-sized buffers or dirty static ranges.
- Treat stable GPU pass dispatches, barriers, and indirect-count calls as
  reusable topology. GPU-written visibility, command, and count values changing
  each frame is not itself a command-recording mutation.
- Remove or gate expensive debug behavior in measured frames: command labels,
  detailed barrier summaries, verbose planner diagnostics, and per-pass warning
  construction should not run in production profiling mode.
- Reduce redundant state changes. Sort or bucket frame ops so compatible
  target/context/pipeline state stays active longer without changing output.
- Move frame-graph/resource-planner validation out of the hot recording loop
  wherever possible. Compile the plan when resources or settings change, then
  record from compact prepared data.
- Keep failures visible. If cached recording cannot be used because resources,
  material state, or swapchain generation changed, report the reason and fall
  back to uncached recording for that frame.

## Phase 0 - Instrument The Broad Scope

- [ ] Execute and report this supporting work through the canonical Phase 5.2
  promotion gates; do not create a separate branch or independent acceptance
  status.
- [ ] Add sub-scopes under `Vulkan.RecordPrimary.MainOpLoop`.
- [ ] Add counters for frame ops, context changes, render pass switches,
  barrier counts, descriptor binds, pipeline binds, draw calls, and allocations.
- [ ] Capture a clean desktop frame and a clean OpenXR/Monado stereo frame with
  profiler UI disabled.
- [ ] Confirm which sub-scope explains at least 80 percent of the CPU time in
  the worst steady-state frames.

Acceptance criteria:

- [ ] A new frame dump can say whether primary recording is dominated by frame
  op dispatch, barriers, descriptors, pass switching, draw recording, uploads,
  or debug/profiler overhead.
- [ ] The added counters do not allocate in the recording hot path.

## Phase 1 - Remove Hot-Path Waste

- [ ] Audit command recording for allocations and convert obvious offenders to
  spans, pooled lists, cached delegates, precomputed strings, or struct
  enumerators.
- [ ] Stop building diagnostic strings unless the diagnostic will be emitted.
- [ ] Pre-resolve repeated pass/resource lookups into compact per-frame data.
- [ ] Cache material/pipeline binding decisions that are stable for a command
  list generation.
- [ ] Ensure render graph planner warnings are produced at plan-build time, not
  every measured recording frame.
- [ ] Add Vulkan frame-indexed upload/storage arenas and stable dynamic-offset
  bindings for ordinary per-view, per-object, material, skinning, and debug data.
- [ ] Convert resizable dynamic buffers to capacity growth plus subrange updates;
  include `LinesBuffer` in the retained regression workload.
- [ ] Move required pipeline/shader creation out of primary recording and define
  a declared warmup boundary with explicit pending/deferred counters.

Acceptance criteria:

- [ ] Steady-state primary recording produces zero or near-zero managed
  allocations in the validation scenes.
- [ ] Primary recording p95 improves without changing rendered output.
- [ ] After warmup, required pipeline compilation, exact-size dynamic-buffer
  recreation, and pipeline-caused whole-frame deferral are zero.

## Phase 2 - Cache Stable Command Ranges

- [ ] Identify frame-op ranges that are stable across frames: static opaque
  geometry, skybox, full-screen fixed post passes, and stable shadow passes.
- [ ] Add invalidation keys for cached command ranges: framebuffer generation,
  render area, material/pipeline generation, mesh buffer generation, descriptor
  layout/set/publication generation, resource allocation generation, bounded
  frame-slot/external-target variant, dynamic-rendering inheritance, and debug
  topology mode.
- [ ] Build keys from immutable prepared recording snapshots and classify misses
  as structural, binding-identity, or data-only. Data-only changes do not miss.
- [ ] Record cacheable ranges into secondary command buffers where Vulkan state
  rules allow it.
- [ ] Keep dynamic ranges separate: editor UI, dynamic text overlays,
  transforms, streaming uploads, and swapchain-dependent presentation.
- [ ] Add cache hit/miss counters and miss reasons.
- [ ] Replace the compile-time primary-reuse hard-off gate with the completed
  dependency validation contract. A setting/environment override may select
  forced-record diagnostics but is not the production correctness mechanism.
- [ ] Record stable GPU-driven dispatch/barrier/indirect-count topology once;
  changing GPU-written buffer contents must not set a generic mutable-frame-op
  reason or force primary rerecording.

Acceptance criteria:

- [ ] Static scene frames reuse command ranges after warmup.
- [ ] Cache misses are visible and attributable.
- [ ] Cache invalidation never reuses commands across incompatible resources or
  swapchain generations.
- [ ] Primary reuse is enabled in the normal production policy, reaches the
  canonical >=99% warmed static-scene target, and requires neither a hard-coded
  safety override nor an environment flag.
- [ ] Camera/transform/material-value/debug-line and GPU indirect/count updates
  preserve compatible cached ranges; every actual miss identifies the changed
  dependency field.

## Phase 3 - Validate Desktop And VR

- [ ] Validate desktop mono, desktop mirror while VR is active, OpenXR
  true-single-pass stereo, and any OpenVR path available locally.
- [ ] Run matched Release Vulkan/OpenGL `CpuDirect` low-, medium-, and high-count
  cohorts plus the opened GPU-indirect/meshlet cohorts required by the canonical
  Phase 5.2B/5.2C gates.
- [ ] Compare screenshots or RenderDoc captures before and after caching.
- [ ] Confirm GPU pipeline time remains comparable while render-thread wall
  time falls.
- [ ] Confirm command recording does not hide GPU/accelerated paths behind CPU
  fallback.
- [ ] Separate managed planning/recording time, native Vulkan call time, and GPU
  execution in every retained result; record pipeline readiness, dynamic-buffer
  growth, cache reuse, and miss-reason counters.

Acceptance criteria:

- [ ] `Vulkan.RecordPrimary.MainOpLoop` is no longer the top recurring
  100-200 ms FPS-drop leaf in the validation runs.
- [ ] Any remaining slow frame has enough sub-scope data to choose the next
  root-cause fix.
