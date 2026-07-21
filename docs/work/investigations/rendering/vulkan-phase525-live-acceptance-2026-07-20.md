# Vulkan Phase 5.2.5 Live Acceptance - 2026-07-20

Status: paused at requested wrap-up; final implementation unverified

Related implementation ledger:
[Phase 5.2.5 progress](../../progress/rendering/vulkan-core-hardening-phase525-2026-07-20.md)

## Problem Statement

Close every acceptance criterion in Vulkan core-hardening Phase 5.2.5 after the
versioned plan, nonblocking lifetime, command-reuse, dynamic-data, pipeline
readiness, and render-graph implementations landed.

## Required Evidence

- Deterministic automated coverage for generation divergence, graph validation,
  external-target rotation, command dependency invalidation, buffer capacity,
  and required/optional pipeline readiness.
- Successful editor and unit-test builds.
- A Vulkan Unit Testing World run with standard validation, GTAO, repeated
  desktop resize, and OpenXR eyes when an OpenXR runtime is available.
- Viewport captures from more than one camera position and log review proving
  stable generation convergence, no global drains, bounded planner/resource
  state, no validation error, and no device loss.
- Reuse-enabled versus forced-record equivalence and steady-state telemetry.

## Attempts And Evidence

### Implementation validation

- `XREngine.Runtime.Rendering` built successfully on 2026-07-20.
- The isolated Phase 5.2.5 suite passed 44/44 tests.
- The initial full test dependency build reported `CS1628` in the concurrently
  added math-BVH Unit Testing World source. The current source no longer captures
  the `out` parameter; the editor/full-suite rebuild is pending in this run.

### GPU tooling

- `rdc doctor` is unavailable because `rdc-cli` is not installed.
- RenderDoc itself is installed at
  `C:\Program Files\RenderDoc\renderdoccmd.exe`; use this fallback only if MCP
  captures and Vulkan logs do not identify a remaining failure.

## Current Run Output

Ignored evidence root:
`Build/_AgentValidation/20260720-155600-vulkan-phase525/`

Expected subfolders: `mcp-captures/`, `mcp-output/`, `logs/`, `reports/`, and
`renderdoc/`.

## Wrap-Up Result

Acceptance is not closed. Deterministic Phase 5.2.5 coverage passed before the
final audit (41/41 direct tests and 68/68 focused acceptance tests), and the
editor built with zero errors at that point. The later audit added bounded,
fence-retired desktop swapchain generations and removed additional normal-path
global waits from uploads, queries, Vulkan resource eviction, upscale frame-slot
recreation, and impostor capture. Work was stopped before those latest changes
could be built or tested.

The Monado runner can perform exact Win32 client resizes and records
`desktop-resize-ledger.json`, while the smoke summary now records the whole frame
window's wait/flush and swapchain-retirement counters. The runner still needs an
exit gate for missing, unsuccessful, or extent-mismatched resizes and Phase 5.2.5
minimum extent/cycle requirements. No reuse-enabled or forced-record live cohort,
multi-camera MCP capture, visual-parity comparison, or final log review was run.

The canonical tracker's Phase 5.2.5 acceptance section lists the exact remaining
build, test, runner, live GPU, visual, and log-validation work. Its acceptance
boxes intentionally remain unchecked.
