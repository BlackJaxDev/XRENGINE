# Vulkan Render Query System Upgrade Investigation

Date: 2026-07-22
Status: Implementation and automated validation in progress; live Vulkan validation blocked before device creation
Related TODO: [Vulkan render query system upgrade](../../todo/rendering/vulkan-render-query-system-upgrade-todo.md)
Related acceptance owner: [Vulkan CPU-query and Monado regressions](vulkan-cpu-query-monado-regressions-2026-07-14.md)

## Problem

The generic query API exposed OpenGL-numbered targets, mutable recording state,
scalar-only reads, and one native Vulkan query pool per engine handle. It could
not safely describe multi-value queries, multiview ranges, cached command-buffer
replay, or late results from prior submissions.

## Baseline And Consumer Decisions

- Source baseline: `f7ea7817a13dcae210efa04dcea66dddc139457b` on
  `rendering-vulkan-core-hardening`. The requested task explicitly kept the
  current branch, so the todo's dedicated-branch/merge steps do not apply.
- Scratch root:
  `Build/_AgentValidation/20260717-vulkan-p0/20260722-vulkan-render-query-upgrade/`.
- `AsyncOcclusionQueryManager`, `CpuRenderOcclusionCoordinator`,
  `VPRC_OcclusionQuery`, `GPURenderPassCollection`, `BvhGpuProfiler`, and both
  backend query wrappers migrate to immutable descriptors and typed tickets.
- Dense Vulkan frame timing and render-pipeline timing remain specialized
  renderer-owned arenas because their batching and publication lifecycle differ
  from general query handles.
- The initial focused suite ran 167 tests successfully and exposed one stale
  source-contract assertion. It did not identify a runtime query failure.
- The machine-readable baseline and validation ledger are in the task run's
  `reports/` directory. Those ignored files are evidence only; this document
  contains the durable conclusions.

## Implemented Design

The engine now has immutable query descriptors, explicit operation kinds,
result layouts, typed results, exact submission tickets, typed read statuses,
and allocation-free raw reads. OpenGL and Vulkan share engine semantics and
report unsupported multi-value operations explicitly.

Vulkan maps descriptors through one capability snapshot, owns bounded pool
arenas, allocates contiguous ranges, records queue-ordered resets, retains pools
through command-buffer submissions, and refuses stale tickets. The command
scheduler represents reset, begin, end, timestamp, property write, and result
copy operations explicitly; brackets are ordering barriers and incompatible
secondary lowering is excluded. Specialized property/performance/video families
require a registered subsystem provider and otherwise report
`SubsystemUnavailable`.

CPU async occlusion preserves its nonblocking, budgeted, fail-visible policy.
Pending handles are quarantined until their tickets resolve rather than being
destroyed or reused. BVH timestamp pairs preserve independently resolved ends
and have a bounded pending queue.

See [Render Queries](../../../developer-guides/rendering/render-queries.md) for
the final API and lifecycle contract.

## Validation Evidence

- `XREngine.Runtime.Rendering` builds with zero warnings.
- The final focused query/occlusion suite passed 188/188. Its query-specific
  subset passed 20/20, including bounded arena policy and an undersized
  transform-feedback result buffer.
- Deterministic coverage includes mapping and rejection reasons, result layouts,
  statistic order, timestamp wrap/conversion, semantic parity, contiguous slot
  allocation, pending-slot non-reuse, bounded arena growth, zero-allocation slot
  reuse, state transitions, and ticket identity.
- The broader `Vulkan` filter completed 819 tests: 730 passed, one skipped, and
  88 failed in existing cross-subsystem runtime-state/source-contract checks.
  Examples include missing pipeline-key files and stale texture/frame-retirement
  source tokens; the focused query assertions remained clean.
- An isolated full solution build succeeded with zero errors and nine existing
  OscCore submodule warnings. The complete unit-test project exceeded its
  five-minute bound without publishing a summary.

## Live Vulkan Blocker

The named isolated session `query-upgrade-0722` reached MCP readiness and then
fast-failed in `vulkan-1.dll` before logical-device creation, capability logging,
or the first query operation. Windows Error Reporting identifies BEX64,
exception `0xc0000409`, loader version 1.4.350, offset `0xdf445`; the loader also
reported `vkEnumerateDeviceExtensionProperties: Invalid physicalDevice`.

The same signature occurred in an independently launched physics-parity Vulkan
session, while `vulkaninfo --summary` succeeded. Disabling implicit layers and
selecting the NVIDIA driver did not change the failure. RenderDoc tooling passed
its doctor check, but a Vulkan capture could not begin before the loader crash;
the only accidental capture was OpenGL startup with no draws and was closed.

Because the crash precedes query capability initialization, no screenshot,
multi-camera comparison, validation-layer query result, desktop motion cohort,
or VR/SPS evidence can honestly be attributed to this implementation. These
live acceptance gates remain blocked on restoring Vulkan editor startup. Once
startup works, repeat the named MCP loop and the Phase 5.2.4b desktop/eye parity
cohorts; do not treat MCP success alone as visual evidence.
