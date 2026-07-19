# GPU BVH Scene-Tree Implementation

Last updated: 2026-07-18

## Implemented

- Routed world bounds through `VisualScene3D.SetBounds` to `GPUScene`.
- Added finite/degenerated-domain validation, live-AABB reduction, margin,
  configured-bounds selection, escape rebuilds, and retained-domain hysteresis.
- Made command snapshot publication revision-aware and command-AABB uploads
  change-aware and coalesced.
- Added the explicit GPU-produced AABB revision/refit path used by skinned bounds.
- Added power-of-two retained capacity and header-only idempotent clearing.
- Replaced leaf-oriented culling with bounded, plane-masked, cooperative
  root-down traversal and conservative queue-pressure recovery.
- Added the flat-cull small-workload selector and removed the duplicate explicit
  post-dispatch barriers.
- Made refit dispatch proportional to internal and leaf counts.
- Replaced large-count bitonic sorting with a deterministic stable radix path.
- Compacted nodes from 80 bytes plus an 8-byte range entry to one 48-byte node.
- Disabled unsafe SAH refinement, added refit-age rebuild policy, and updated
  ray child ordering, stack-pressure recovery, and packet-lane masking.
- Added per-stage CPU/GPU timing, traversal counters, lifecycle/capacity
  diagnostics, shader compilation coverage, and source-contract coverage.
- Added GPU-resident Morton-prefix/LCP and normalized overlap/SAH diagnostics,
  exact refit transfer accounting, rebuild-reason counters, and zero-readback
  accounting.
- Added deterministic multi-workgroup subtree partitioning for traversal,
  retaining conservative per-workgroup queue recovery.
- Added backend/view/visibility selector calibration. Uncalibrated buckets stay
  on flat GPU culling; a bucket requires two consecutive measured BVH wins.
- Added compact-node GPU ray parity, stack occupancy/overflow diagnostics,
  conservative recovery, inactive-packet masking, and an always-writable
  diagnostics binding.
- Cached immutable mesh-triangle packing until source-buffer revision changes.
- Scoped acceleration-structure compute to its registered render-graph pass and
  preserved valid retained topology across viewport provider toggles.
- Made Vulkan compute-uniform ownership dispatch-specific so multi-stage BVH
  builds cannot alias their stage constants.
- Made dense swap-removal move command AABBs with command/bounds metadata,
  restored CPU bounds after failed skinned direct writes, and seeded commands
  that predate BVH activation from the stable bounds snapshot.
- Made overflow state a stable mandatory descriptor and reset it in-band before
  Morton generation, eliminating Vulkan buffer replacement after descriptor
  capture.

## Validation

- `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj --no-restore`
  succeeded. Two pre-existing Surfel GI `CS0649` warnings were present in the
  narrow runtime build.
- `dotnet build XREngine.UnitTests/XREngine.UnitTests.csproj --no-restore`
  succeeded with zero warnings and zero errors.
- The final aggregate GPU-BVH/profiler slice passed 110/110. Dedicated hardware
  suites also passed compact-node ray parity 3/3 and optimized multi-workgroup
  frustum parity 8/8.
- OpenGL integration coverage compiles and links build, refit, traversal, and
  radix shaders, executes build/refit, and validates root bounds and layout.
- Vulkan shader-rewrite diagnostics passed 11/12. The remaining unrelated
  directional-light reflection diagnostic expects `CascadeCount` at byte 1376,
  while the current reflected layout places it at byte 2656 after the rendered
  cascade arrays.

The final forced-Vulkan NVIDIA run used `GpuIndirectInstrumented`, enabled the
standard Vulkan validation layer, and reported zero invalid render-graph pass
warnings, VUIDs, validation errors, or device loss. Five overflow-buffer resets
occurred during startup/topology growth, followed by approximately 61 seconds
with no clean-frame reset. Unit-world settings were restored exactly afterward.

The bounded production-shader OpenGL benchmark measured 20 representative
cells on an RTX 4070 Laptop GPU. Multi-workgroup traversal removed queue
pressure from all measured cells and won the 1M-command, 10%-visible uniform
cell by 3.17x (0.1976 ms versus 0.6257 ms). Flat culling won the other 19 cells,
so no selector bucket met the two-consecutive-win promotion rule and the
uncalibrated runtime policy remains flat. Raw results are in
`Build/_AgentValidation/20260718-gpu-bvh-scene-tree/reports/gpu-bvh-flat-vs-root-down-opengl.csv`.

A second five-row 10K maintenance sweep covered dirty ratios 0/0.1/1/10/100%,
leaf capacities 1/2/4/8/16, visibility 0/10/50/100%, and view classes 1/2/3.
It measured real hierarchy-build/refit GPU time, exact dirty upload bytes,
traversal/emission, dispatches, barriers, retained capacity, and overflow. All
GPU-built trees matched the flat visibility oracle with zero build/traversal
overflow. The bounds-policy sweep corrected configured-domain dilution from 8x
to 2x per axis (one octree level/8x volume) and confirmed the 4x retained-volume
hysteresis boundary.

The NVIDIA Vulkan path now has a BVH-containing RenderDoc correctness capture.
Its production build chain used distinct reset/generation and stage 0/1/2/3
uniforms, retained a non-null overflow binding, and produced 251/251 reachable
compact nodes for 126 commands with no invalid bounds, topology, ranges, or
overflow. AMD/Intel measurements, representative Vulkan traversal timings,
full runtime scene coverage, and promotion-grade animated budgets remain
external evidence. They are specified in
[GPU Scene BVH External-Hardware Qualification](../../testing/rendering/gpu-scene-bvh-external-hardware-qualification.md),
belong under `Build/_AgentValidation/<run>/`, and are not an engine runtime
dependency.
