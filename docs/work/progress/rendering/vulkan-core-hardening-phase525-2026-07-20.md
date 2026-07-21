# Vulkan Core Hardening Phase 5.2.5 - 2026-07-20

Status: implementation present; final audit changes unverified; acceptance open.

## Implemented

- Deterministic per-output resource settings snapshots and revisions, including
  explicit viewport/instance AO resolution and bounded divergence diagnostics.
- Stable physical-plan identity across external target rotation, immutable graph
  generations, timeline-retired planner/texture resources, incremental eviction,
  bounded arenas, and dedicated physical-plan telemetry.
- One immutable dependency signature for primary, secondary, and command-chain
  reuse with structural, binding-identity, and data-only invalidation classes.
- Capacity-backed Vulkan buffers, mapped frame-slot CPU-direct dynamic records,
  dirty byte ranges, and compatible opaque packet bucketing.
- Explicit plan-derived pipeline readiness, bounded manifests, local optional-node
  deferral, shared-pipeline publication generations, and timeline retirement.
- Versioned render-graph resources with overlap-aware subresource dependencies,
  cycle/uninitialized-read validation, queue submissions/waits/signals, output
  contracts, scheduled lifetimes, stable compatibility identity, and graph dumps.
- Bounded desktop swapchain-generation retirement (maximum eight), tracked
  graphics/present marker fences, `OldSwapchain` handoff, dependency-aware old
  generation destruction, present-semaphore rotation, and teardown-only forced
  drain.
- Removal of remaining normal-path queue/device-wide waits from synchronous
  buffer/texture uploads, render-query completion, Vulkan pipeline-resource
  eviction, upscale frame-slot recreation, and octahedral impostor capture.
- Whole-frame Phase 5.2.5 OpenXR telemetry for global waits, force flushes, and
  swapchain retirement, plus retained steady-state pipeline/resource/reuse gates.

## Validation

- Before the final nonblocking-wait/swapchain audit, `git diff --check` passed and
  `XREngine.Editor` built with zero errors (two pre-existing `VPRC_SurfelGIPass`
  unused-field warnings were visible in the parent build).
- The isolated Phase 5.2.5 test project under
  `Build/_AgentValidation/20260720-155600-vulkan-phase525/temp-build/`:
  passed 41/41 direct Phase 5.2.5 tests.
- The focused acceptance slice passed 68/68 and the exact modified regression
  slice passed 16/16 before the final audit edits.
- The Phase 3 regression slice reached 105/110; all five failures were stale test
  expectations and were edited, but the final rerun was interrupted.
- No build or test completed after the final swapchain retirement and remaining
  no-global-wait changes. Two additional stale tests still need marker updates:
  `VulkanCpuDirectOcclusionTests` and
  `RetirementBackpressure_WaitsForEveryRendererBackend`.

## Remaining Acceptance

Follow the explicit remaining-validation list in the canonical tracker. In
particular, finish resize-ledger exit enforcement, compile/test the latest edits,
then run repeated GTAO desktop resizing with active OpenXR eyes in reuse and
forced-record modes, validation-clean mono/multiview/async-compute lanes, visual
parity captures, and steady-state allocation/re-recording/pipeline counters.
