# GPU Scene BVH Rollout Validation

Last updated: 2026-07-18
Status: Local implementation validation complete; external qualification pending

## Problem statement

Validate the implemented GPU scene BVH against the flat GPU culler across
correctness, performance, backend synchronization, zero-readback behavior, and
representative runtime scenes before promotion. The authoritative runtime
contract is [GPU Scene BVH](../../../architecture/rendering/gpu-scene-bvh.md);
the remaining adapter and representative-scene gates are tracked in
[GPU Scene BVH External-Hardware Qualification](../../testing/rendering/gpu-scene-bvh-external-hardware-qualification.md).

## Issues and constraints found

- The checked-in Unit Testing World settings currently have GPU render
  dispatch disabled, so runtime validation must enable it temporarily and
  restore the original file afterward.
- The Vulkan RenderDoc layer was not initially registered. It was enabled only
  for the bounded capture session and removed afterward; no persistent capture
  configuration is part of the engine workflow.
- Cross-vendor NVIDIA/AMD/Intel validation cannot be inferred from one machine.
  Every report must name the actual adapter/backend and leave unavailable
  hardware rows explicit.
- The initial forced-Vulkan GPU-BVH run rebuilt one retained scene tree every
  frame. Owner-labelled buffer diagnostics and correlated resource counts ruled
  out per-mesh first builds. The cause was a false-to-true shared-provider
  transition that dirtied ready topology when viewport strategies toggled.
- The same initial run submitted 28 compute operations with an invalid
  render-graph pass index and synthetic fallback ownership.

## Validation procedure

1. Build the editor and targeted tests from the current working tree.
2. Run deterministic and randomized flat-versus-BVH parity plus workload
   reports under `Build/_AgentValidation/20260718-gpu-bvh-scene-tree/`.
3. Launch the Unit Testing World with GPU dispatch and MCP enabled for OpenGL
   and Vulkan where supported.
4. Capture and view at least two camera positions per backend.
5. Inspect backend logs for shader, descriptor, synchronization, visibility,
   and fallback diagnostics.
6. Capture OpenGL with RenderDoc, then inspect dispatch order, bindings,
   buffers, and exported render targets in an open-work-close session.
7. Restore the original Unit Testing World settings and record exact evidence
   paths and unresolved environment limitations.

## Attempts and evidence

- 2026-07-18: Implementation and automated OpenGL shader coverage completed;
  targeted GPU BVH suite passed 59/59 before this rollout iteration.
- 2026-07-18: Runtime/backend and RenderDoc validation dispatched to a focused
  subagent. Evidence was written under the shared run root.
- 2026-07-18: Added allocation-free completed-build identity gating and stable
  owner/instance resource labels. A forced-Vulkan check proved the repeated
  uploads belonged to `GPUScene.CommandBvh:1`, then isolated the remaining
  false-to-true provider invalidation.
- 2026-07-18: Preserved ready retained topology across provider re-enable while
  keeping first enable and mutation-driven rebuilds intact. The final bounded
  Vulkan run enabled `VK_LAYER_KHRONOS_validation`, forced
  `GpuIndirectInstrumented`, and confirmed `GpuBvh=True`.
- 2026-07-18: Final runtime evidence recorded five overflow-flag uploads during
  startup/import, with no additional upload from `17:05:35.818` through the log
  close at `17:06:37.010`. Invalid render-graph pass warnings, `VUID-`,
  validation-error, and device-lost counts were all zero.
- 2026-07-18: Restored `Assets/UnitTestingWorldSettings.jsonc` exactly; SHA-256
  is `4E68E4F2EFD26D79A933015C61FF8564241C1DFF07C6495D56B614591D9886AB`.
  No editor process remained.
- 2026-07-18: A forced Vulkan RenderDoc build/traversal capture proved the four
  build dispatches retain distinct stage constants. It exposed an invalid
  leaf sourced from a command that predated BVH activation, leading to stable-
  bounds backfill before the first build. The final capture validates the
  48-byte compact layout, root header/connectivity, finite ordered leaves,
  producer/consumer bindings, and zero traversal overflow.
- 2026-07-18: The definitive capture is
  `Build/_AgentValidation/20260718-gpu-bvh-scene-tree/renderdoc/xrengine-vulkan-bvh-final-inband-reset-late_frame60.rdc`.
  Morton reset/generation events 94/98 and build events 108/112/116/120 use
  dispatch-local constants. Binding 8 is the same non-null 16-byte overflow
  resource through reset, build, and traversal. Buffer export at event 151
  decoded 251 reachable compact nodes, root 126, 126 leaves covering all 126
  commands, no invalid bounds/topology/ranges, and zero overflow flags. The
  capture debug log was empty.

## Result

The NVIDIA Vulkan runtime blockers discovered in the first validation pass are
resolved: static-frame overflow resets are bounded to startup/topology changes,
the reset executes in-band without replacing a captured descriptor, and compute
dispatches carry valid render-graph ownership. Standard Vulkan validation
reported no VUID, validation-error, or device-lost line in the final run.

OpenGL visual evidence, automated parity, and NVIDIA Vulkan BVH-event evidence
are available. AMD/Intel coverage, representative Vulkan traversal timings,
Vulkan raycast readback, and the full runtime scene matrix remain external
qualification work; do not infer those gates from the NVIDIA result. The
required matrix and promotion rules are preserved in
[GPU Scene BVH External-Hardware Qualification](../../testing/rendering/gpu-scene-bvh-external-hardware-qualification.md).
