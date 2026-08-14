# Vulkan Presentation-Independent Renderer Validation

Status: Ready for validation

Owner: Rendering / Vulkan / Validation

Created: 2026-08-13

Related work:

- [Completed Vulkan Presentation-Independent Renderer Refactor](../../todo/COMPLETED/vulkan-presentation-independent-renderer-refactor-todo.md)
- [Implementation progress and existing evidence](../../progress/rendering/vulkan-presentation-independent-renderer-refactor-progress.md)
- [Vulkan Headless MCP Component Profiling TODO](../../todo/rendering/optimization/vulkan-headless-mcp-component-profiling-todo.md)
- [Vulkan renderer architecture](../../../architecture/rendering/vulkan-renderer.md)

## Purpose

Own the validation work separated from Phase 7 of the completed implementation
TODO. This plan does not reopen the target-first architecture. It verifies the
completed implementation across automated, GPU integration, performance, and
runtime lifecycle lanes without claiming results before they are run.

Future WebGL2/WebGPU work is out of scope for this Vulkan test matrix. Portable
contract tests should nevertheless continue to reject backend-native handles in
`RenderFrameOutputDescription` so browser backends can implement the same host
and output boundary later.

## Accepted Starting Evidence

The progress ledger records the implementation-level evidence already accepted
for closeout:

- Runtime Rendering, Vulkan Rendering, and RenderBench builds passed with zero
  warnings and zero errors on 2026-08-13.
- A 64x64 presentationless RenderBench run used three frame slots, six warmup,
  three stability, and six capture frames on an NVIDIA GeForce RTX 4070 Laptop
  GPU. It reported zero capture-thread and fixture-worker allocations and
  SHA-256 `DEF598687A136FABA64832EF05E1E7DFAC2B0E0A703DA047C2E9556107726318`.
- The unmodified `DefaultRenderPipeline` ran through three complete
  windowless create/render/destroy lifecycles with stable SHA-256
  `62FB561C59D0CEA247FC588F3311EE665375F35D8675B186E2792CB7DFCFF88C`.
- An isolated desktop Vulkan editor session produced distinct viewport
  readbacks on queue slots 0 and 1 with no Vulkan validation, device-loss,
  fatal, or unhandled-exception diagnostics.
- `rdc doctor` passed with RenderDoc 1.44. Automatic capture did not emit an
  `.rdc` because presentationless execution has no WSI frame boundary.

These observations are useful baselines, not substitutes for the remaining
matrix below.

## Focused Automated Tests

- [ ] Test target-first renderer context validation.
- [ ] Test target-driver extension and queue requirements.
- [ ] Test presentationless creation without `XRWindow`.
- [ ] Test deterministic production render-graph submission and output hash.
- [ ] Test fixed frame-slot rotation and fence/timeline ownership.
- [ ] Test that zero-readback submission never calls the readback path.
- [ ] Test explicit unsupported headless-WSI diagnostics.
- [ ] Test partial-initialization cleanup using injected failures.
- [ ] Test renderer module generation propagation in every target mode.
- [ ] Test target-generation invalidation of reusable command buffers.
- [ ] Test that portable frame-output state exposes no Vulkan/native handles.

## GPU Integration Validation

- [ ] Run presentationless Deferred and Uber fixtures.
- [ ] Run desktop equivalents with the same scene, camera, resolution, format,
  deterministic seed, and frame count.
- [ ] Compare final output identity within documented format/color-space
  differences.
- [ ] Run standard Vulkan validation.
- [ ] Run synchronization validation.
- [ ] Verify presentationless logs contain no surface, swapchain, acquire, or
  present operation.
- [ ] Verify headless WSI logs contain acquire and no-op present operations.
- [ ] Verify desktop logs still contain compositor presentation.
- [ ] Exercise desktop resize, minimize/restore, HDR selection, and
  surface-loss recovery.
- [ ] Exercise OpenXR session start, frame acquisition/release, and shutdown.
- [ ] Exercise repeated create/render/destroy cycles for desktop WSI, headless
  WSI, and OpenXR in addition to the proven presentationless lifecycle.

## Performance Validation

- [ ] Warm the presentationless renderer to steady state.
- [ ] Measure managed allocations across the submission interval.
- [ ] Verify no per-frame resource or shader creation.
- [ ] Verify no per-frame `vkDeviceWaitIdle`.
- [ ] Verify no current-frame GPU-to-CPU readback.
- [ ] Compare command-buffer cache hit/rebuild behavior with desktop.
- [ ] Record CPU and GPU frame-time distributions for the same fixture.

## Exit Criteria

- [ ] Targeted tests pass.
- [ ] Validation and synchronization validation introduce no new messages.
- [ ] Deterministic Deferred or Uber output is stable and comparable across
  presentationless and desktop modes within documented differences.
- [ ] Presentationless steady-state zero-allocation, zero-churn,
  zero-current-frame-readback, and no-device-wide-wait gates pass.
- [ ] Desktop, headless-WSI, and OpenXR lifecycle and behavior regressions are
  ruled out on supported runtimes.
- [ ] Commands, hardware, driver, hashes, logs, and any unsupported lanes are
  recorded in the progress ledger.
