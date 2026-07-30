# Vulkan Optimization Workstreams 01-08 Acceptance Closeout

Last Updated: 2026-07-30
Owner: Rendering / Vulkan / Performance Validation
Status: Deferred Until Workstreams 01-08 Are Implementation Complete

Related execution roadmap:

- [Engine Rendering Optimization Roadmap](../../todo/rendering/optimization/engine-rendering-optimization-roadmap.md)

## Purpose

Run the long-form correctness, stability, performance, allocation, Vulkan
validation, RenderDoc, desktop, and RVC acceptance matrix once all numbered
optimization workstreams have completed implementation.

This is an owner-authorized sequencing change. It avoids repeatedly paying for
the same canonical Gate matrix while workstreams 04-08 intentionally change
frame preparation, command recording, render-graph cost, occlusion, and tail
latency. It does not waive, weaken, or silently mark any acceptance criterion
as passed.

## Implementation Progression Rule

- Workstreams 01-08 remain ordered for implementation.
- A successor may begin when its predecessor is marked
  `Implementation Complete; Acceptance Deferred`.
- Targeted tests, narrow builds, and implementation smokes still run in each
  workstream.
- Canonical Gate captures and promotion claims remain prohibited until this
  closeout is complete.
- Any targeted validation failure that indicates an implementation defect
  remains an immediate blocker; only the long-form acceptance matrix is
  deferred.

## Workstream 03 Deferred Acceptance

Carry forward every unchecked Phase 0-3 criterion and Exit Gate item from:

- [03 - True GPU-Driven Zero-Readback Submission](../../todo/rendering/optimization/03-vulkan-true-zero-readback-submission-todo.md)

Required retained work:

- diagnose the 93.98% primary-reuse result and the two CPU-stage
  reconciliation discrepancies;
- retain runtime proof for mutation/streaming, required variants,
  empty/exact/overflow, delayed diagnostics, and visibility bypass;
- run the 1x/4x/16x scaling matrix with three 60-second Gate repetitions;
- run the retained high-count CPU-direct/zero-readback/FullBucketScan
  crossover with three Gate repetitions;
- run the full desktop Deferred/Uber static/moving and RVC Deferred/Uber
  foveation Off/Fixed Gate matrix;
- run the matched CPU-direct primary-reuse cohorts;
- repeat StandardValidation and focused RenderDoc inspection if later
  implementation changes recording, descriptor publication, or
  synchronization;
- require the workstream-03-local comparator and evaluator to pass before
  promoting zero-readback submission.

The existing evidence and invalidated attempts remain recorded in the
[Vulkan framerate root-cause investigation](../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md).

## Workstream 04 Deferred Acceptance

Carry forward every unchecked acceptance and Exit Gate criterion from:

- [04 - Next-Frame Preparation And Collect-Visible Handoff](../../todo/rendering/optimization/04-next-frame-preparation-and-collect-visible-handoff-todo.md)

Required retained work:

- canonical stable allocation captures for package production, publication,
  validation, and consumption;
- static, moving, mutation, streaming, resize, pause/resume, failed-submit,
  and shutdown stress;
- collect/render overlap and Mode A/Mode B backpressure comparison;
- input-latency and package-generation-age comparison against the predecessor;
- proof that Vulkan non-encoding preparation meets its workstream-01 budget;
- deterministic image/draw-order parity;
- closure of the 40,384-byte frame-data-refresh handoff without moving the
  allocation to another stage.

Targeted implementation evidence already retained for closeout comparison:

- the final isolated editor smoke reported zero late-prepared and rejected
  packages, package age within the one-frame policy, and zero frame-data
  refresh allocation in its sampled Vulkan frame;
- canonical cohorts must still determine whether those results hold under the
  deferred desktop/RVC mutation and stress matrix.

## Workstream 05 Deferred Acceptance

Carry forward every unchecked acceptance and Exit Gate criterion from:

- [05 - Vulkan Command Recording Worker Architecture](../../todo/rendering/optimization/05-vulkan-command-recording-worker-architecture-todo.md)

Required retained work:

- compare serial and persistent-worker recording with identical prepared
  inputs on small, medium, large, and stable dirty-chain cohorts;
- prove at least two worker intervals overlap and tune or confirm the
  two-independent-chain dispatch floor on target desktop and RVC hardware;
- compare p50/p95/p99 worker record, active span, overlap, merge, render-thread
  wait, and total render time against the workstream-01 threshold;
- prove zero steady-state managed allocation in both primary and secondary
  command encoding without moving allocation into preparation, merge, or
  submission;
- validate CPU-direct behavior and the explicit primary-command ownership
  quarantine for mutable zero-readback indirect/count streams;
- run StandardValidation, resize, shader hot reload, scene churn, device-loss,
  shutdown, and repeated start/stop stress;
- verify worker exception/timeout quarantine never permits partial submission
  or destroys an in-use command pool; and
- confirm exact pass, transparent, draw, primary reuse, and secondary reuse
  ordering plus visual parity.

Targeted implementation evidence already retained for closeout comparison:

- the Release Vulkan project and isolated editor session built successfully;
- the 256-box deterministic Unit Testing World cohort exercised command-chain
  recording with validation enabled;
- sampled frames reported zero validation errors, worker failures, worker wait
  timeouts, and secondary-recording allocation;
- frames with no dispatched worker correctly reported zero worker activation,
  record, active-span, overlap, and wait metrics; and
- the focused unit-test project could not execute because unrelated stale
  Vulkan/OpenXR tests do not currently compile.

Detailed implementation evidence is in
[Vulkan Command Recording Worker Architecture Progress](../../progress/rendering/vulkan-command-recording-worker-architecture-2026-07-30.md).

## Workstreams 06-08 Intake

When each later workstream reaches implementation complete, append:

- its unchecked acceptance and Exit Gate items;
- exact targeted build/test/smoke evidence already completed;
- canonical cohorts it changes or adds;
- handoffs to later workstreams;
- any required hardware/runtime capability result.

Do not remove a criterion from its source TODO. This closeout is the execution
manifest; the source TODO remains the owning contract.

## Final Execution Order

1. Freeze the implementation revision and dependency/runtime manifests.
2. Run all focused deterministic tests and Release builds.
3. Run standard Vulkan validation and required RenderDoc captures.
4. Run workstream-local mutation, overflow, resize, lifetime, and shutdown
   stress.
5. Run the canonical desktop and RVC Gate cohorts with the required three
   repetitions and variance limits.
6. Run cross-workstream comparisons and absolute budget evaluation.
7. Update every numbered source TODO with exact evidence paths.
8. Mark workstreams 01-08 acceptance complete only when all local criteria
   pass.
9. Record final promotion, remaining hardware exceptions, and workstream-08
   handoffs.

## Closeout Gate

- [ ] Workstreams 01-08 are implementation complete.
- [ ] Every deferred criterion is mapped to an exact report, capture, test, or
  explicit capability result.
- [ ] No canonical comparison exceeds its variance or regression threshold.
- [ ] Desktop, RVC, allocation, validation, and RenderDoc evidence is valid.
- [ ] Every source TODO is updated and marked acceptance complete.
- [ ] The optimization sequence is promoted or explicitly rejected with
  retained evidence.
