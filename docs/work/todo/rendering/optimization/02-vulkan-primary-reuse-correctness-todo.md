# 02 - Vulkan Primary Reuse Correctness TODO

Last Updated: 2026-07-28
Owner: Rendering / Vulkan Command Buffers
Status: Complete
Sequence: 02 of 08
Predecessor: [01 - Vulkan Performance Truth And Regression Gates](01-vulkan-performance-truth-and-regression-gates-todo.md)
Blocks: [03 - True GPU-Driven Zero-Readback Submission](03-vulkan-true-zero-readback-submission-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)

Related tracker:

- [Vulkan Primary Command Recording Fast Path](vulkan-primary-command-recording-fast-path-todo.md)

Canonical ownership:

- This document owns primary-reuse correctness and its completion gate.
- The linked fast-path tracker is a superseded umbrella; its measurement work
  belongs to workstream 01, preparation work to workstream 04, and worker work
  to workstream 05.

## Sequential Execution Contract

- Do not start this workstream until workstream 01 is marked `Complete`.
- Use only the canonical cohorts and trustworthy counters established by
  workstream 01 for acceptance decisions.
- Do not start workstream 03 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

## Goal

Make Vulkan primary-command-buffer reuse correct and stable. A static
CPU-direct scene must reuse both its mesh command chains and its primary
skeleton after the required per-swapchain-image initialization, while genuine
resource or layout changes must still invalidate reuse.

## Starting Evidence

- Deferred CPU-direct recorded 1,047 primaries and reused zero.
- Mesh command chains were reused, so secondary reuse did not imply primary
  reuse.
- The stable decision mask was `Recorded | PrimaryFrameState` (`1026`).
- Source tracing narrows the no-query failure to the image-entry-state reuse
  gate.
- Reused secondary state can leave the merged primary entry-state snapshot
  incomplete or conflicting with the prior primary state.

## Scope

- Primary image-entry and exit-state snapshots.
- Per-swapchain-image state ownership and generation tracking.
- Secondary-command-buffer state merge semantics.
- Complete versus unknown/incomplete resource state.
- Stable cache identity for framebuffer and render area, material/pipeline and
  mesh buffers, descriptor layout/set/publication, physical resource
  allocation, bounded frame slot and external target, dynamic-rendering
  inheritance, overlay topology, and debug topology.
- Legitimate invalidation from resize, render-graph changes, descriptor
  generations, resource recreation, overlays, and queries.
- Non-intrusive diagnostics for every reuse rejection.
- Isolation of volatile ImGui, text, profiler, streaming-upload, and debug work
  from otherwise reusable scene topology.

## Non-Goals

- Zero-readback material dispatch.
- Persistent parallel command recording.
- Render-graph pass reduction.
- Relaxing Vulkan correctness to obtain reuse.

## Phase 0 - Reproduce And Explain Every Rejection

- [x] Reproduce the zero-primary-reuse CPU-direct baseline in all canonical
  static cohorts.
- [x] Log the exact expected and actual state tuple for each
  `PrimaryFrameState` rejection.
- [x] Identify which secondary merge, transition, resource generation, or
  incomplete snapshot produced each mismatch.
- [x] Confirm diagnostics do not dirty the primary or alter reuse cadence.
- [x] Add focused tests for equal, unequal, incomplete, recreated, and
  per-image entry states.

Acceptance criteria:

- [x] Every primary record has a precise reason with no unexplained generic
  state bit.
- [x] The repeated stable-scene rejection is deterministic and covered by a
  failing characterization test.

## Phase 1 - Define The State Contract

- [x] Document ownership of image state before primary execution, during
  secondary execution, after primary completion, and across present/acquire.
- [x] Define when an incomplete secondary snapshot may be merged and when it
  must force a record.
- [x] Distinguish unknown state from conflicting state.
- [x] Define which resources are frame-local, swapchain-image-local, or shared
  across frames.
- [x] Define structural, binding-identity, and data-only changes. Camera,
  transform, animation, material values, frame-slot offsets, debug-line
  contents within capacity, GPU visibility, indirect commands, and count
  values must remain data-only when their recorded binding topology is stable.
- [x] Ensure state equality includes all correctness-relevant generation and
  subresource fields and excludes incidental telemetry.

Acceptance criteria:

- [x] The contract can predict reuse or record for each characterization case.
- [x] No state transition relies on stale data from a different swapchain
  image or frame generation.

## Phase 2 - Repair Reuse And Invalidation

- [x] Correct secondary-to-primary state merging.
- [x] Preserve a complete reusable entry snapshot after successful execution.
- [x] Avoid invalidating a primary for per-frame data that can be safely
  refreshed without encoding.
- [x] Preserve required records for changed pass structure, pipeline/layout,
  resource identity, query cadence, overlay structure, and resize.
- [x] Keep stable opaque geometry, skybox, fixed full-screen passes, and stable
  shadow ranges eligible for reuse while volatile overlays and uploads remain
  separately recordable.
- [x] Keep capacity-backed frame-indexed upload/storage arenas and stable
  descriptor or dynamic-offset bindings from invalidating compatible command
  ranges on ordinary value publication.
- [x] Ensure failed or abandoned frame attempts cannot publish reusable state.

Acceptance criteria:

- [x] The stable characterization test changes from repeated record to reuse.
- [x] Every seeded correctness change still causes the expected record.
- [x] Vulkan validation reports no new command-buffer, layout, lifetime, or
  synchronization errors.

## Phase 3 - Runtime Validation

- [x] Run static Deferred and Uber CPU-direct cohorts after warmup.
- [x] Run deterministic camera motion without scene or graph mutation.
- [x] Run resize, swapchain recreation, shader/pipeline replacement, overlay
  toggles, and query-mode transitions.
- [x] Validate desktop mono and available OpenXR/OpenVR paths, including
  dynamic and legacy render-target modes, with visual comparisons or GPU
  captures for reused and forced-record variants.
- [x] Retain `LinesBuffer` and another capacity-backed dynamic-buffer workload
  to prove logical-size changes do not recreate backing storage or dirty
  compatible primaries until capacity is exceeded.
- [x] Compare render p50/p95/p99, actual primary encoding, chain reuse, and
  frame-data refresh against workstream 01.
- [x] Confirm no per-frame allocation was added to reuse checks.

Acceptance criteria:

- [x] Eligible stable frames achieve at least 99% primary reuse after each
  swapchain image has been initialized.
- [x] Camera motion alone does not force a primary record unless a documented
  command structure or resource state actually changes.
- [x] Static render p95 improves or remains within the variance threshold; a
  correctness repair may not hide a performance regression.

## Exit Gate

- [x] Stable CPU-direct scenes reuse primary and secondary command buffers.
- [x] Exact state-rejection telemetry remains available in clean profiles at
  negligible measured overhead.
- [x] All legitimate invalidation tests pass.
- [x] Release build, focused tests, validation-layer diagnostic run, and
  canonical performance runs pass.
- [x] Evidence and remaining limitations are recorded in the investigation.
- [x] This document is marked `Complete`.

Completion evidence:

- Four canonical dynamic-rendering cohorts: 4,668 / 4,668 captured primaries
  reused, zero recorded; all also reused their secondary command chains.
- Legacy render-pass cohort: 712 / 714 captured primaries reused (99.72%).
- Final Deferred static cohort: 1,260 / 1,260 primary reuse, zero
  primary-recording allocation, zero submission rejection, and zero VUID.
- Focused Vulkan/OpenXR regression suite: 297 passed, zero failed.
- GPU-free benchmark evaluator fixtures: 6 passed.
- Detailed results, validation-layer evidence, XR-runtime availability, and
  visual-capture limitations are recorded in the linked investigation.

Only after this gate is complete may work begin on
[03 - True GPU-Driven Zero-Readback Submission](03-vulkan-true-zero-readback-submission-todo.md).
