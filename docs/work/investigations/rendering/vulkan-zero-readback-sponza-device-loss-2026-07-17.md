# Vulkan Zero-Readback Sponza Device-Loss Investigation

Date: 2026-07-17  
Status: Both known BVH device-loss loops fixed; final Sponza visual parity and CPU recording cost remain open

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

## Remaining Work

1. Add opt-in delayed post-fence diagnostics for the Sponza command path:
   source input count, post-BVH visible count, post-key count,
   post-material-scatter per-bucket count, final published count, first/last
   indirect command, atlas tier and buffer generation, capacities, material,
   and draw-metadata ID. Do not add synchronous or steady-state readback.
2. Add the CPU-built indirect comparison and capture the zero-readback result at
   the recorded matched camera plus a second camera position.
3. Use the surviving workload to obtain a real RenderDoc/Nsight artifact and
   inspect whether Sponza commands are generated, consumed, clipped, or rejected
   before the final target.
4. Continue the CPU command-recording investigation; do not trade the fixed TDR
   for a global wait, serialization, or lower frames-in-flight policy.
5. Complete P0.7's identical three-by-60-second static/moving and occlusion
   matrix with StandardValidation and SyncValidation.
