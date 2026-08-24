# Vulkan Presentation-Independent Refactor Phases 4-6

Status: implementation complete; Phase 7 cross-target validation remains.

Date: 2026-08-13.

## Problem

The production Vulkan graph could initialize presentationless targets, but its
desktop submission, final-output discovery, reusable-command identity, and
teardown remained coupled to desktop swapchain state. The target boundary also
needed to stay portable enough for the proposed WebGL2/WebGPU canvas renderers.

## Issues Found

- Desktop assembled queue-submit synchronization separately from explicit
  target modes.
- Primary recording assumed `PresentSrcKHR` as the final layout.
- Production pipeline resource state obtained final-output properties from a
  window rather than an acquired target.
- Explicit targets with more than two slots indexed desktop-sized retirement
  and arena state during teardown.
- The successful queue-submit hot path allocated an interpolated diagnostic
  string.
- Cleanup did not record partial initialization ownership as one explicit
  reverse-unwind contract or prevent new frame admission before teardown.

## Implemented Resolution

- Frozen desktop and explicit acquired outputs into `VulkanFrameTargetLease`
  and routed submission through one tracked, allocation-free gateway.
- Made final layout, target generation, extent, views, samples, and formats
  explicit recording/resource identities.
- Added `RenderFrameOutputDescription` and published it around ordinary
  viewport/pipeline execution without exposing native handles.
- Added lease-backed production graph submission to
  `VulkanExplicitTargetRendererHost` and borrowed target-driver-owned images.
- Derived frame-slot storage from the selected target and reopened mapped/frame
  arenas only after that slot's completion had been proven.
- Added staged initialization, quiescing with active-frame drain, idempotent
  aggregate cleanup, ordered readback/retirement drains, and device-loss forced
  retirement.

## Validation Evidence

- Runtime rendering, Vulkan rendering, and RenderBench builds completed with
  zero warnings and zero errors.
- A three-slot deterministic presentationless RenderBench run passed every
  stability gate with zero capture-thread/worker allocations and output hash
  `DEF598687A136FABA64832EF05E1E7DFAC2B0E0A703DA047C2E9556107726318`.
- The ordinary `DefaultRenderPipeline` ran four frames across each of three
  complete renderer lifecycles and returned the stable hash
  `62FB561C59D0CEA247FC588F3311EE665375F35D8675B186E2792CB7DFCFF88C`.
- Isolated desktop Vulkan editor readbacks from two camera positions completed
  on different queue slots and were visibly different. Its logs contained no
  Vulkan validation, device-loss, fatal, or unhandled-exception diagnostics.
- `rdc doctor` passed. Automatic external capture did not produce a frame for
  the presentationless process because it has no WSI frame boundary; no
  RenderDoc conclusion was inferred from that absence.

Disposable evidence is under
`Build/_AgentValidation/20260813-180927-vulkan-presentation-refactor/`. The
isolated desktop session is under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260813-184734-phase46-vulkan/`.

## Remaining Validation

Phase 7 retains injected partial-initialization failure coverage, full standard
and synchronization validation, desktop resize/surface-loss exercises, and
repeated headless-WSI/OpenXR lifecycles on systems where those runtimes are
available. No tests were added or run before explicit user clearance, per the
repository feature-first testing policy.
