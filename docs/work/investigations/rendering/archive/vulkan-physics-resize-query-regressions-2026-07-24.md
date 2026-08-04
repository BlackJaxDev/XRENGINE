# Vulkan Physics Resize And Query Regression Investigation

Date: 2026-07-24
Status: Resolved for the reported regressions

## Problem

The Vulkan editor running the Physics Testing world showed low focused-frame
throughput, repeated timestamp-query arena exhaustion, foreign render-graph pass
warnings, and handled `InvalidOperationException` failures while interactively
resizing the window.

## Baseline Evidence

The reported run is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-24_15-53-07_pid51868`.

- Focused UI submission summaries were approximately 22-32 FPS before resize.
- The general Vulkan query arena exhausted all 16 chunks of 256 timestamp
  entries shortly after rendering began.
- Query and submission-marker operations inherited synthetic scene pass
  `100087` while carrying the nested UI pipeline's seven-pass metadata set.
- During resize, a single global frozen planner extent was reused across the
  downscaled scene and one-to-one UI contexts. The planner rejected those
  mismatched contexts and dropped 37 frame operations.
- The run also enabled RenderDoc-friendly diagnostics and command-buffer labels,
  so it is not a clean performance baseline.

## Root Causes And Fixes

1. Interactive resize used one renderer-wide extent tuple. It now retains a
   bounded, preallocated snapshot per stable planner context, including pipeline,
   viewport, context kind, and output identity.
2. Generic command-scope profiling treated Vulkan like an immediate query-object
   backend. Vulkan now keeps that path disabled and uses its renderer-owned
   frame-slot timestamp pools; OpenGL remains on the immediate query path.
3. A render-graph pass stack stored only an integer. When a scene pass wrapped a
   nested UI pipeline, captured operations paired the scene pass with UI
   metadata. Pass scopes now retain their owning pipeline, and Vulkan captures
   frame-op resources and metadata from that owner. The permissive foreign-pass
   validation exception is removed so future ownership mistakes remain visible.

## Validation

- The exact focused regression selection passed 120 tests, with one intentional
  CI-lane skip and zero failures. Results:
  `Build/_AgentValidation/p48b-testresults/vulkan-query-resize-fixes/reports/vulkan-query-resize-pass-focused.trx`.
- The isolated editor build completed with zero errors. The only build warnings
  were the existing `Magick.NET-Q16-HDRI-AnyCPU` `NU1902` advisories.
- The named Vulkan Physics Testing smoke session was
  `Build/_AgentValidation/mcp-sessions/p48b-vk-regression-fix/`. It used the same
  RenderDoc-friendly preset and command-buffer labels as the reported run.
  GPU render-pipeline profiling was enabled during the steady-state and resize
  checks, then restored to its original disabled value.
- A four-step interactive resize produced distinct frozen planner snapshots:
  the scene context froze at `1920x1080/1286x723`, while the nested UI context
  froze at `1920x1080/1920x1080`. After convergence, they independently froze
  at `2378x1294/1593x866` and `2378x1294/2378x1294`.
- The complete run logs contain zero query-arena exhaustions, arena allocation
  failures, invalid render-graph pass indices, query frame-op recording
  failures, cached planner-context mismatches, `InvalidOperationException`
  instances, resize-cache capacity warnings, Vulkan VUIDs, device-loss
  messages, or stack overflows.
- Two presentation ticks were intentionally discarded while the swapchain
  settled (`Swapchain resize/recreate pending` and `AcquireNextImage NotReady`);
  these were normal WSI skips, not planner exceptions. The settled profiler
  sample reported zero dropped frame, draw, and compute operations.
- In the comparable resize window, physical-image allocations fell from 610 to
  92, physical-buffer allocations from 56 to zero, signature and physical-plan
  changes from 24 each to four each, deferred plan retirements from six to two,
  and planner-cache evictions from 17 to one.
- With the same diagnostic overhead enabled, the UI submission proxy measured
  about 62 FPS across the resize interval and 98.23 FPS over the settled
  30-second window, compared with roughly 4.6-5.4 FPS during resize and
  22-32 FPS settled in the reported run. A settled profiler sample measured
  4.341 ms for the Vulkan frame lifecycle and a 9.629 ms render cadence.
- Viewport captures from two camera positions both rendered fresh Physics
  Testing world content before and after resize:
  `Build/_AgentValidation/p48b-testresults/vulkan-query-resize-fixes/mcp-captures/`.
- `rdc doctor` passes with the Vulkan layer registered. A RenderDoc capture was
  not needed because the failures were CPU-side ownership/allocation defects,
  the corrected logs contain none of their signatures, and both fresh viewport
  captures are visually coherent.
