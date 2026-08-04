# Two-Pass Occlusion Culling Progress

Last updated: 2026-08-03  
Owner: Rendering  
Status: Implementation in progress; live GPU visual qualification is failing

## Objective

Add comparable Math Intersections Unit Testing World rigs for CPU asynchronous
queries, CPU software rasterization, and GPU two-pass Hi-Z occlusion. The GPU
target must use `GpuIndirectZeroReadback`, retain visibility on the GPU, use
GPU-BVH-accelerated candidate culling, and follow the early/late algorithm from
[Two-Pass Occlusion Culling](https://medium.com/@mil_kru/two-pass-occlusion-culling-4100edcad501).

The root test controller must also show an enabled test's exact component
properties without requiring the user to select the child scene node.

## Current Result

The interactive rigs, root property projection, renderer resources, shaders,
and early/late frame-operation sequence are implemented. CPU query and CPU
rasterized qualification passed. The exact GPU configuration is active and its
GPU BVH is ready, but the GPU viewport is blank: persistent visibility converges
to an empty early list and the late Hi-Z phase does not recover any commands.

Do not describe the GPU implementation as verified or complete yet.

## Work Completed

### Unit Testing World and editor UI

- Added one deterministic occluder/hidden-target/reveal-target rig for each
  occlusion mode.
- Added `MathOcclusionCullingTestComponent` controls and live telemetry for the
  requested/effective mode, mesh submission strategy, zero-readback activity,
  GPU-BVH state, and Hi-Z phase state.
- Made the three occlusion rigs mutually exclusive and made the root controller
  capture, apply, reconcile, and restore their process-wide renderer settings.
- Added `CustomUIGroupField` and ImGui rendering support. The root controller
  projects the child test component's existing field objects into a conditional
  group, so edits from the root and child inspectors operate on the same state.
- Added the three scenarios to the Math Intersections test list and documented
  how to activate and inspect them.

### GPU two-pass renderer path

The implemented frame sequence is:

1. Run the existing GPU scene/GPU-BVH candidate cull.
2. Preserve the full candidate count on the GPU.
3. Compact commands whose persistent `{ render identity, visible }` record was
   visible in the preceding frame. New or recycled identities start visible.
4. Build material-tier indirect commands into dedicated phase-one buffers and
   submit the early raster draw.
5. Apply the early-raster-to-depth-pyramid synchronization boundary and build a
   current-frame Hi-Z pyramid.
6. Reset the reusable late-output counters, test every candidate against Hi-Z,
   update persistent visibility, and emit only newly visible commands.
7. Build the normal material-tier indirect buffers and submit the late draw.

Dedicated early command, hot-command, material-tier indirect, and draw-count
buffers were added so phase two cannot overwrite phase-one resources before the
early draw consumes them. The exact path remains GPU-resident and performs no
production count readback. Diagnostic readbacks are gated by the existing
Vulkan trace environment variables.

The main implementation is in:

- `Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionPhaseOne.comp`
- `Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.TwoPassOcclusion.cs`
- the `Core`, `CullingAndSoA`, `IndirectAndMaterials`, `Occlusion`, and
  `ShadersAndInit` `GPURenderPassCollection` partials

### Validation completed

- The targeted runtime rendering and editor builds completed successfully; the
  observed warning was unrelated to this work.
- Both modified compute shaders passed standalone GLSL syntax validation.
- CPU asynchronous-query live qualification passed, with the final sample
  testing 15 commands and culling 8.
- CPU software-rasterized qualification passed, with the final sample testing
  30 bounds and culling 24.
- The GPU test resolved to `GpuHiZ + GpuIndirectZeroReadback`; component
  configuration validation passed, and the strategy-driven GPU BVH reported 18
  logical primitives and 35 nodes.
- Vulkan frame-operation tracing confirms the intended order: phase-one
  compute/material scatter, early indirect draw, Hi-Z build, late compute and
  material scatter, then late indirect draw.
- The early and late indirect operations use distinct Vulkan indirect/count
  buffer handles. A sampled early indirect command was structurally valid
  (`indexCount=36`, `instanceCount=1`).
- Initial pipeline compilation deferred some draws, but compilation completed;
  both indirect draws are subsequently recorded every frame. Persistent
  pipeline deferral is therefore not the current blocker.

## Blocking Failure and Evidence

The GPU feature is not visually working.

- A 14-frame sequence remained blank apart from the editor grid; there was no
  transient geometry or disocclusion recovery.
- A depth visualization was effectively clear (`min=0.9987413`, `max=1.0`), so
  the early pass is not producing useful current-frame occluder depth.
- Delayed diagnostics eventually stabilize at 16 candidate draws plus 16
  instances, while phase-one count, phase-one material-tier count, and the late
  output count all remain zero.
- Control runs render correctly with CPU rasterization and with zero-readback
  submission when occlusion is disabled. The blank output is therefore isolated
  to the new two-pass Hi-Z behavior rather than the shared scene or the base
  zero-readback path.

The strongest current hypothesis is an empty-visibility feedback loop: the late
test marks every command invisible, the next phase one has no occluders, and the
late test still fails to recover commands from the effectively clear pyramid.
The exact cause is not proven. The highest-value checks are the reversed-Z
contract and the graphics descriptor snapshot used by each indirect draw.

Evidence is retained under:

- `Build/_AgentValidation/20260803-two-pass-occlusion/mcp-captures/`
- `Build/_AgentValidation/mcp-sessions/two-pass-occlusion/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-03_14-09-24_pid30504/`
- Sequence contact sheet:
  `Build/_AgentValidation/20260803-two-pass-occlusion/mcp-captures/ViewportSequence_20260803_210004_682_12fb1fb8abd0463c9184b03b7e04c58f/contact-sheet.png`
- Depth view:
  `Build/_AgentValidation/20260803-two-pass-occlusion/mcp-captures/gpu-two-pass-depth/RenderPipeline_DepthView_20260803_140252.png`

`rdc doctor` passed, but the attempted RenderDoc launches did not produce an
`.rdc` capture. No RenderDoc-based conclusion should be inferred.

## Next Work

1. Verify the complete depth convention as one contract: camera
   `IsReversedDepth`, actual depth clear value, `HiZGen.UseMinReduction`, the
   supplied view-projection matrix, and both nearest-depth comparisons in
   `GPURenderOcclusionHiZ.comp`. Export mip 0 and a reduced mip for inspection.
2. Prevent an empty or deferred early raster from poisoning persistent
   visibility. At minimum, invalidate/reseed visibility after activation,
   camera discontinuities, pipeline readiness changes, and unusable depth.
3. Inspect each indirect draw's captured graphics-program snapshot and confirm
   binding 7 references the dedicated phase-one command SSBO for the early draw
   and the normal late command SSBO for the late draw.
4. Confirm `baseInstance` indexes the command buffer actually bound to the
   graphics shader after compaction; fix the scatter contract if it still
   carries a source draw ID where a compact index is required.
5. Repeat live validation until phase one remains nonzero, the depth pyramid
   contains occluder depth, hidden targets remain rejected, and the moving
   orange target is emitted by phase two when revealed. Capture two camera
   angles and a sequence longer than one visibility cycle.
6. Review Vulkan logs for validation, descriptor, skipped-dispatch, overflow,
   and forbidden-fallback errors. Obtain a RenderDoc capture if screenshots and
   counter traces remain inconclusive.
7. Only after the live/runtime path passes, add and run deterministic automated
   tests in `XREngine.UnitTests`, per repository testing policy. No automated
   test was added in this iteration because the feature has not passed runtime
   validation.
8. Update the scenario description, qualification investigation, and testing
   guide from `single-phase/not implemented` to the final verified result only
   after that acceptance run succeeds.

## Related Records

- `docs/work/investigations/rendering/math-intersections-occlusion-qualification-2026-08-03.md`
- `docs/work/testing/rendering/math-intersections-occlusion-tests.md`
- `docs/work/todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md`
