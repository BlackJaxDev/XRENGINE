# Vulkan Zero-Readback Sponza Device-Loss Investigation

Date: 2026-07-17  
Status: P0 immediate gate closed 2026-07-18; production zero-readback stable and visible, with diagnostic allocation/parity and external tooling retained as post-P0 work

## Problem

`GpuIndirectZeroReadback` could show the editor and skybox without Sponza and
could reset the NVIDIA driver. The failure was content dependent: sky-only
views could remain stable while a Sponza-loaded run produced `nvlddmkm` event
153 followed by `vkCreateBuffer: Invalid device` fallout.

## Root Cause

The reset was not caused directly by the Vulkan indirect-count command. GPU
BVH construction can leave malformed parent connectivity, and two consumers
trusted that connectivity with unbounded loops:

- `bvh_refit.comp` followed parent pointers while propagating leaf bounds; and
- `bvh_frustum_cull.comp` launched one invocation per leaf, then walked that
  leaf's parent chain while it remained visible.

The second loop explains the misleading sky-only stability: an off-camera leaf
could return before reaching a cycle, while the same leaf became an infinite
GPU loop when the camera faced Sponza. It also explains why the original
dispatch isolation identified refit under the default view but did not prove
that refit was the only content-dependent loop.

`bvh_refit.comp` now:

- derives a safe node count from the node header and actual node-buffer length;
- validates node, range, Morton, AABB, counter, parent, and child indices before
  use; and
- caps bottom-up parent propagation to the safe node count.

This is a GPU-local safety guard. It adds no CPU readback, fence wait, device
drain, fallback, or additional frame resource, and malformed connectivity exits
instead of monopolizing the GPU.

`bvh_frustum_cull.comp` no longer follows parent pointers. That shader already
dispatches one invocation per leaf; after a leaf passes its AABB test, every
ancestor necessarily contains that leaf and cannot add rejection. The old
rootward walk was therefore both unsafe and redundant. The replacement:

- derives its leaf range from the safe node-buffer length;
- validates that the indexed node is a leaf;
- tests the refitted leaf AABB once, then retains the existing per-primitive
  bounds test; and
- removes the 32-entry local stack and all ancestor AABB tests.

This preserves the current leaf-parallel culling result while reducing shader
work. A true hierarchical optimization would require a separate root-down
dispatch model; it must not be simulated by walking ancestors from every leaf.

## Isolation Evidence

- A shader-identity breadcrumb at the actual Vulkan compute-dispatch recording
  point reproduced the reset at 15:39:31, about 18 seconds after launch.
- Skipping every `GPURender*` compute shader still reset the GPU at 15:40:20.
  This ruled out reset counters, hot-command build, LOD selection, transparency
  classification, key build, clear, and material scatter as the direct TDR
  source.
- Skipping all BVH shaders remained healthy for 110 seconds.
- Skipping only `bvh_refit` remained healthy for 113 seconds while BVH build,
  BVH frustum culling, the normal zero-readback programs, and indirect draws
  stayed enabled.
- With the bounded refit shader and no dispatch skipped, two independent traced
  Release launches remained healthy for 121 and 120 seconds.
- A trustworthy matched camera was then established from a `CpuDirect` Sponza
  control at position `(0.92583525, 20.693876, 38.539276)` and forward
  `(-0.03693097, -0.34202012, -0.93896663)`. Moving unmodified
  `GpuIndirectZeroReadback` to that pose reproduced `nvlddmkm` event 153 at
  16:18:39. Suppressing every indirect bucket draw still reproduced event 153
  at 16:20:30, proving this remaining reset was in compute rather than indirect
  draw consumption.
- Bounding the frustum-cull parent walk stopped that reset but rejected the
  cyclic leaves, yielding a stable sky-only frame. Removing the redundant
  parent walk retained the leaf and per-primitive tests and stayed healthy for
  120 seconds at the exact Sponza camera with no new NVIDIA event.
- Temporary dispatch trace/skip controls were removed after isolation. The
  clean Release performance launches and the later visible-editor launch also
  completed without an NVIDIA reset.

Raw breadcrumbs and redirected stderr are under
`Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/logs/`.

## Other Correctness Changes Retained

- Vulkan logical-device creation enables and gates `multiDrawIndirect` and
  `drawIndirectFirstInstance`; Vulkan 1.2+ uses core
  `vkCmdDrawIndexedIndirectCount`.
- Deferred compute dispatches snapshot immutable Vulkan buffer handles, ranges,
  and usage rather than re-resolving a mutable backend generation later.
- Zero-readback keeps a stable renderer per atlas tier so static Sponza commands
  cannot record against a later tier's geometry bindings.
- Material scatter bounds count reservation, validates atlas spans, and writes
  every compacted key. Atlas upload rejects triangle indices outside the source
  mesh vertex range.
- Vulkan enables core `robustBufferAccess` when supported.

## Validation Evidence

- The earlier clean focused suite passed 23 tests. The follow-up frustum-cull
  change passed four targeted tests covering shader compile/link,
  build-plus-refit root bounds, bounded refit propagation, and the invariant
  that leaf-parallel frustum culling does not traverse malformed parents.
- Clean Release editor build after the frustum-cull change: 0 errors; 216
  existing package/compiler warnings. The first sandboxed build attempt failed
  because Visual C++ `FileTracker` was denied access; the approved native build
  completed normally.
- Clean StandardValidation performance runs used workload identity
  `6325129635686552578`, 26,044 draw calls, 25 multi-draw calls, and 297,557
  triangles per sampled frame. Both reported zero VUIDs, readback bytes, mapped
  buffers, fallbacks, and forbidden fallbacks:
  - `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_15-58-20/summary.json`
  - `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_16-02-20/summary.json`
- The representative second run recorded 29.525 ms Vulkan GPU command-buffer
  p50, matching the retained 28.712-29.001 ms baseline. The first run recorded
  an anomalous 4.799 ms p50. The shader fix therefore shows no measured GPU
  throughput regression.
- End-to-end CPU recording remains unacceptable and noisy: the two clean runs
  recorded 242.927-267.402 ms render p50 and the first recorded 222.402 ms
  command-recording p50, versus retained 201.586-241.650 ms render and
  170.521-177.526 ms record p50. This is not attributed to the shader guard and
  remains a P0.3/P0.7 blocker.

## Visual Status

MCP found 383 Sponza-named nodes, including the active root at
`Root Node/Static Model Root/sponza`; the scene is loaded rather than absent.
Synchronous Vulkan MCP viewport readback is intentionally rejected because its
transfer path independently caused a watchdog reset.

Trustworthy OS-window captures are:

- `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/mcp-captures/matched-camera-cpudirect-focused2.png`
  - matched `CpuDirect` control; Sponza is visibly rendered and MCP reports 361
    opaque plus 32 masked commands;
- `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/mcp-captures/matched-camera-zero-readback-focused.png`
  - same camera with only a bounded parent walk; stable, but sky-only because
    malformed cyclic leaves were rejected;

- `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/mcp-captures/fixed-bvh-refit-os-window.png`
  - editor camera, skybox visible, Sponza not visible;
- `Build/_AgentValidation/20260717-vulkan-device-loss-renderdoc/mcp-captures/fixed-bvh-refit-play-focused-sponza.png`
  - play mode, floor and other scene geometry visible, Sponza not visible.

The final leaf-only cull launch remained responsive for 120 seconds at the same
matched camera. A final OS-window capture was blocked by the desktop approval
quota, but the user directly observed that Sponza was not visible in that
window. Treat this as valid operator evidence that P0.6 remains open, while
still obtaining tool-captured evidence during the next diagnostic pass. The
next step is delayed post-fence stage/bucket diagnostics rather than another
stability-only launch.

## Why RenderDoc Evidence Was Not Valid

No `.rdc` exists. `rdc-cli` was unavailable. An early native fallback attempt
did not establish a valid capture boundary, another misparsed the spaced dotnet
path, and the corrected `RenderDocFriendly` launch injected successfully but
the GPU reset before RenderDoc serialized a frame. The output is therefore not
a bad or empty captured frame; it is no capture artifact at all.

Now that both known BVH TDR loops are fixed, RenderDoc can be retried against a
stable missing-Sponza frame alongside the delayed bucket diagnostics. Verify
the `.rdc` exists before inspecting draws, arguments, descriptors, atlas
buffers, or render targets.

## Final P0 Stability And Performance Checkpoint

Four matching warmed Release desktop StandardValidation cohorts completed on
2026-07-17: static and moving camera paths, each with occlusion disabled and
`CpuQueryAsync`, with three 60-second repetitions per cohort. All 12 runs were
stable with no early exit, device loss, VUID, submission rejection, GPU
readback, mapped-buffer use, or stale collection reuse. Visibility generation
age remained at most one frame and requested/consumed draw totals matched in
every run.

Machine-readable manifests:

- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-04-21/summary.json`
- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-09-09/summary.json`
- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-13-58/summary.json`
- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-18-51/summary.json`

Across those runs, render p50 was 31.507-34.035 ms, render p95 was
34.747-37.182 ms, and command-record p50/p95/max was
24.014-25.428/25.705-27.847/32.881-40.617 ms. The prior
242.927-267.402 ms render p50 and 222.402 ms record p50 are no longer present.
Vulkan GPU command p50 remained 1.192-1.215 ms.

The forced-primary path is not allocation-free: it retains approximately
0.92 MiB of managed allocation per sampled frame. Removing the unused copied
layout-state arrays reduced that cost, while EventPipe now attributes the
remainder to framebuffer attachment planning, image-access delta growth,
descriptor sampling transitions, and resource-lifetime collections.

Fallback telemetry was also corrected so a legitimate zero-visible cull is not
reported as an attempted CPU recovery. The post-fix moving-camera
`CpuQueryAsync` run at
`Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-25-01/summary.json`
reports zero full-run/capture fallback events, zero readback, zero VUIDs, and
exact requested/consumed parity.

A final validation-clean `CpuDirect` control at
`Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-25-53/summary.json`
reports 49 direct draw calls and 301,924 triangles. The zero-readback matrix
reports 25 multi-draw calls and 262,617 triangles. Because direct and bucketed
draw-call counters have different meanings, and the triangle counts differ,
these numbers do not supersede the user's matched-camera visual observation.

`rdc doctor` now passes. The only serialized capture produced during the retry
was nevertheless OpenGL because its child launch inherited stale copied
settings; the corrected Vulkan launch did not serialize an `.rdc`. No visual or
Vulkan-state claim is based on that artifact.

The final focused Vulkan timing, packet, and indirect-policy selection passes
73/73 in Release. The build retains existing NuGet vulnerability warnings but
introduces no test failure.

## P0 Closeout - 2026-07-18

The production zero-readback failure is closed. The final result did not come
from hiding the path behind CPU rendering:

- Imported-texture streaming now records and compares the actual physical image
  storage layout and format. Incompatible progressive-mip storage is not reused,
  removing the observed copy-layout/format VUIDs and streaming device loss.
- Delayed post-fence statistics exposed a corrupt raw GPU BVH node-count header.
  The production Vulkan zero-readback lane now uses flat GPU frustum culling
  while the versioned BVH publication repair remains later work. This retains
  GPU culling and adds no CPU readback or fallback.
- The matched Sponza camera now visibly renders geometry. A second pose and the
  motion contact sheet change with camera motion, ruling out stale or
  uninitialized output. Delayed counts reached 46-50 culled commands and
  247,765-262,267 triangles.
- Incremental command-local dependency publication now clears already-published
  dependencies, and shader invalidation is marshalled to the main thread. The
  original SyncValidation shader hot-reload retirement crash no longer
  reproduces.
- The clean SyncValidation interaction session
  Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-07-18_05-59-49_pid1852
  survived streaming, camera motion, resize, shader hot reload, and normal
  shutdown. The later 406-sample manifest at
  Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-18_13-54-06/summary.json
  reports zero VUIDs, readbacks, mappings, fallbacks, submission rejections, and
  stale collection reuse. It crossed two workload hashes during streaming and
  is not used as a stable performance result.
- Reusing command tracking capacity and eliminating transient framebuffer
  attachment/layout/signature collections reduced forced-primary allocation
  from approximately 888.5 KiB/frame to 322.7 KiB/frame. The final comparable
  manifest is
  Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-18_13-01-09/summary.json.
  Cached stable production recording already reaches zero; the remaining forced
  diagnostic allocation is assigned to Phase 5.2.5.
- XRE_FORCE_CPU_INDIRECT_BUILD=1 now selects the CPU-built reference only for
  GpuIndirectInstrumented. The material-batched path rebuilds commands and
  material runs rather than submitting stale/empty buffers. This diagnostic
  path intentionally maps/reads its reference data and is not a fallback for
  the accepted zero-readback lane.
- RenderDoc 1.44/rdc-cli 0.5.6 passes preflight but did not serialize a bounded
  Vulkan capture. The earlier Nsight Systems 2026.3.1 report contained 31
  Vulkan frames and 564,644 sampled callchains but no exported Debug Utils
  marker table. The engine regions remain implemented; external visualization
  is not claimed.
- The integrated Release editor build completes with zero errors. The focused
  P0 policy, lifetime, shader, allocation, and CPU-reference suite passes 56/56.

Accepted visual evidence:

- Build/_AgentValidation/20260718-vulkan-p0-closeout/mcp-captures/flat-gpu-zero-readback-matched-camera-printwindow.png
- Build/_AgentValidation/20260718-vulkan-p0-closeout/mcp-captures/motion-valid-contact-sheet.jpg

## Post-P0 Follow-Ups

1. Eliminate the remaining approximately 322.7 KiB/frame forced-primary
   diagnostic allocation under Phase 5.2.5.
2. Keep parallel command-chain workers quarantined until immutable recording
   snapshots exist and a repeated validation/performance matrix approves them.
3. Complete the full matched CpuDirect / CPU-built / GPU-built diagnostic visual
   matrix across motion, CPU-query occlusion, resize, and streaming.
4. Obtain an external Vulkan marker/action tree when a compatible Nsight or
   RenderDoc export path can serialize and expose the engine labels.
5. Repair and version GPU BVH publication so zero-readback can leave the flat
   GPU-culling quarantine without accepting corrupt header state.
