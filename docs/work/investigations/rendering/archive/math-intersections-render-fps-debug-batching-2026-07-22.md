# Math Intersections Render FPS And Debug Batching

## Problem

The Math Intersections Unit Testing World tops out near 130 render FPS even
when the selected scenario shows only a few lines. The editor reports 19 or
more draw calls even when no individual math test appears to be rendering.

## Questions

- Is the frame rate limited by the timer, presentation, CPU submission, the
  editor UI, the default render pipeline, or debug primitive rendering?
- Which passes own the baseline draw calls?
- Are debug primitives already batched, and can the remaining debug work be
  reduced without hiding required GPU paths behind a CPU fallback?

## Findings

- Unit-test settings request unrestricted rendering (`RenderFPS = 0`) and
  disable VSync, so the observed ceiling was not a configured frame cap.
- With every selectable math scenario inactive, the ground crosshair is one
  `DebugDrawComponent` containing 23 lines.
- Ordinary debug points, lines, and triangles already converge into the
  scene-wide `DebugPrimitiveSceneState` and one `InstancedDebugVisualizer` per
  primitive topology. The 23 visible lines were already one instanced draw.
  Combining different point/line/triangle pipelines into one multi-draw
  indirect call would save at most two calls in a fully populated frame and
  would not address this workload.
- The original Vulkan frame contained 35 recorded operations and 18 scene
  mesh draws. The default pipeline was still executing three GTAO draws, light
  combine and copy draws, eight bloom draws, post-process/final-post draws,
  TSR, the debug overlay, and final presentation even though the scene had no
  regular test meshes.
- Baseline timing averaged 7.2213 ms per render (138.5 FPS). Dense GPU timing
  averaged 1.590 ms with a 1.716 ms p95, so the frame was render-thread,
  editor-overlay, and presentation bound rather than GPU bound.
- The debug overlay itself averaged about 0.014 ms on the GPU;
  `VPRC_RenderDebugShapes` averaged about 0.008 ms on the CPU. Further debug
  geometry batching could not explain or remove the roughly 5.6 ms gap.

## Root Causes

1. The Math Intersections world used `DefaultRenderPipeline`, whose deferred,
   lighting, AO, bloom, temporal, and presentation passes are largely fixed
   per-frame costs independent of visible line count.
2. The existing `DebugOpaqueRenderPipeline` was not usable as the Vulkan
   replacement because it rendered directly to the logical output without
   declaring owned color/depth resources for Vulkan frame-op recording.
3. Startup pipeline selection read `EditorUnitTests.Toggles` before the world
   factory synchronized those toggles from `RuntimeBootstrapState.Settings`.
4. MCP startup could persist editor preferences after the temporary unit-test
   choice, recomputing effective preferences and clearing that choice before
   window creation.
5. The default pipeline's pass gates treated every render command as mesh
   workload. The active Math Intersections frame had four commands, but all
   four were method callbacks and there were zero `IRenderCommandMesh`
   commands. Those callbacks caused the full deferred/Forward+ chain to run.
6. Auxiliary geometry replays such as velocity, full-overdraw, and the forward
   depth/normal prepass did not consistently exclude method callbacks. A
   callback that populates the global debug batch must run in its primary pass,
   not once for each mesh-only auxiliary pass.

## Implemented Solution

- `DebugOpaqueRenderPipeline` now owns an `Rgba16f` scene texture, a
  depth/stencil texture, and a scene FBO. It renders ordinary scene passes and
  the globally batched debug overlay into that FBO, then presents it with one
  fullscreen draw.
- Window presentation uses `VPRC_RenderToWindow` and its
  `FlipSourceYOnVulkan` contract. The first implementation used a generic FBO
  quad blit; its source texture captures were correct, but its physical Vulkan
  window output was vertically inverted.
- Math Intersections now prefers the lightweight pipeline automatically. The
  explicit `ForceDebugOpaquePipeline` setting remains available for other
  worlds, while the model-material safeguard applies only to the Default world
  that actually consumes `ModelsToImport`.
- Pipeline selection reads the already-loaded runtime settings and runs as the
  final pre-window initialization step, after MCP/world preference changes.
- `RenderCommandCollection` now publishes total and per-pass mesh-command
  counts alongside its existing render-side command snapshot. Classification
  is folded into the existing command-signature scan, so it adds no second
  traversal or per-frame allocation.
- `DefaultRenderPipeline` now selects a conservative callback-only scene path
  when the frame has no regular scene meshes and no feature that requires the
  full pipeline. It clears the forward target, preserves the
  `OpaqueForward`/`OnTopForward` callback hooks used by debug producers, and
  skips GBuffer, GTAO, forward depth prepass, deferred lighting, Forward+,
  velocity, bloom, fog, and overdraw setup.
- Motion-vector, full-overdraw, and forward depth/normal auxiliary passes now
  test and replay actual mesh commands only. This prevents debug callbacks from
  masquerading as geometry or running multiple times.

## Validation Evidence

- Isolated Release editor build succeeded. The two reported warnings are
  pre-existing unused Surfel GI fields; the changed code introduced none.
- `PublishedWorkloadCounts_DistinguishMeshesFromCallbacks` passed, verifying
  that callback-only passes publish zero mesh workload while mixed passes
  publish their actual mesh count.
- With local `ForceDebugOpaquePipeline = false`, bootstrap reported
  `WorldKind=MathIntersections` and `UseDebugOpaquePipeline=true`, and the live
  viewport reported `DebugOpaqueRenderPipeline`.
- The final Vulkan trace contains four operations: two clears, one debug
  overlay mesh draw with 23 instances, and one `RenderToWindow` presentation
  draw. Vulkan validation reported zero errors.
- The final 60-sample steady-state run averaged 2.2686 ms per render (440.8
  FPS), with a 2.1826 ms median and 2.9660 ms p95. This is about 3.2x the
  baseline FPS in the same Vulkan/ImGui workload.
- Upright viewport evidence:
  `Build/_AgentValidation/mcp-sessions/math-fps-batching-20260722/mcp-captures/Screenshot_20260722_153451_206_0d5fb9b62ad64edbb4872ac5a58762c1.png`.
- Two direct scene-texture captures from different camera positions also
  changed with the camera and showed the complete grid/crosshair, ruling out a
  stale or uninitialized source texture.

## Default-Pipeline Workload-Elision Follow-Up

The default pipeline was forced back on for an apples-to-apples comparison.
The render-side snapshot contained four callbacks and zero mesh commands. The
workload-aware branch produced these steady-state results:

| Metric | Original default | Workload-aware default | Change |
| --- | ---: | ---: | ---: |
| Vulkan frame operations | 35 | 12 | -65.7% |
| Mesh draws | 18 | 5 | -72.2% |
| GPU frame average | 1.640 ms | 0.807 ms | -50.8% |
| GPU frame p95 | 1.842 ms | 0.826 ms | -55.2% |
| Observed uninstrumented render rate | 138.5 FPS | about 214.8 FPS | +55.1% |

The optimized 12-operation trace contains one forward-target clear, temporal
history passthrough, exposure compute, post-process, the single 23-instance
debug-line draw, final post-process, TSR/history, and presentation. Vulkan
validation reported zero errors. Captures from two camera positions remained
upright and changed with the camera.

This explains why the workload-aware default path is still slower than the
two-draw debug pipeline: it deliberately retains the camera's HDR/exposure,
post-process, temporal, TSR, and final-output contract. Safely collapsing an
arbitrary callback-only frame all the way to the debug pipeline requires an
explicit callback workload contract (for example, "only produces late debug
primitives" versus "draws HDR scene color"). Inferring that from a delegate
would risk dropping legitimate callback rendering.

## User Confirmation

- The user reported that the initial physical window presentation was upside
  down while direct texture capture was upright. The presentation path was
  replaced with the engine's Vulkan-aware window presenter; confirmation of
  the relaunched physical window is pending.
