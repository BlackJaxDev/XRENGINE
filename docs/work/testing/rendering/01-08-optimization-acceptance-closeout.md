# Vulkan Optimization Workstreams 01-08 Acceptance Closeout

Last Updated: 2026-08-01
Owner: Rendering / Vulkan / Performance Validation
Status: Blocked Until The Pre-06 Gate And Workstreams 06-08 Are Complete

Related execution roadmap:

- [Engine Rendering Optimization Roadmap](../../todo/rendering/optimization/engine-rendering-optimization-roadmap.md)

## Purpose

Run the final sequence-wide correctness, stability, performance, allocation,
Vulkan-validation, RenderDoc, desktop, and RVC regression matrix after
workstreams 06-08 complete implementation.

Workstreams 03-05 now close before workstream 06 through their dedicated
validation gate. This document consumes that accepted manifest as a frozen
baseline; it no longer duplicates or defers the 03-05 checklist.

## Implementation Progression Rule

- The [workstreams 03-05 validation gate](03-05-optimization-validation-todo.md)
  must be `Complete` before workstream 06 begins.
- Workstreams 06-08 remain ordered for implementation.
- A successor after workstream 06 may begin when its predecessor is marked
  `Implementation Complete; Acceptance Deferred` or `Complete`.
- Targeted tests, narrow builds, and implementation smokes still run in each
  workstream.
- Any targeted validation failure that reveals an implementation defect is an
  immediate blocker; only the final cross-workstream matrix may be deferred to
  this closeout.

## Accepted Pre-06 Baseline

The pre-06 baseline is owned by:

- [Vulkan Optimization Workstreams 03-05 Validation](03-05-optimization-validation-todo.md)

Before this closeout begins:

- [ ] The 03-05 gate is marked `Complete` with exact revision, hardware,
  runtime, settings, report, capture, test, and log paths.
- [ ] Its accepted zero-readback, frame-preparation/data-publication, and
  command-recording manifests are copied into or referenced by the final
  closeout manifest.
- [ ] Later implementation changes that affect a 03-05 invariant identify and
  rerun the exact invalidated local gate; unchanged 03-05 evidence is not
  repeated merely because this final closeout runs.
- [ ] The workstream-03 desktop 5.00 ms and RVC 8.33 ms absolute-budget results
  remain explicit workstream-08 inputs rather than being mistaken for a passed
  final whole-renderer promotion.

## Workstreams 06-08 Intake

When each later workstream reaches implementation complete, append:

- its unchecked acceptance and Exit Gate items;
- exact targeted build/test/smoke evidence already completed;
- canonical cohorts it changes or adds;
- handoffs to later workstreams; and
- any required hardware/runtime capability result.

Do not remove a criterion from its source TODO. This closeout is the final
execution manifest for workstreams 06-08 and the cross-workstream regression
surface, while each source TODO remains its owning contract.

## Final Execution Order

1. Verify the 03-05 gate is complete and import its accepted manifests.
2. Freeze the final implementation revision and dependency/runtime manifests.
3. Run all focused deterministic tests and Release builds.
4. Run standard Vulkan validation and required RenderDoc captures.
5. Run workstream-local mutation, overflow, resize, lifetime, and shutdown
   stress for workstreams 06-08 and any invalidated 03-05 contract.
6. Run the canonical desktop and RVC Gate cohorts with the required three
   repetitions and variance limits.
7. Run cross-workstream comparisons and absolute budget evaluation.
8. Update workstreams 06-08 and every invalidated earlier gate with exact
   evidence paths.
9. Record final promotion, remaining hardware exceptions, and workstream-08
   handoffs.

## Closeout Gate

- [ ] The workstreams 03-05 validation gate is complete and its evidence is
  still valid for the final revision, or every invalidated local gate has been
  rerun successfully.
- [ ] Workstreams 06-08 are implementation complete.
- [ ] Every deferred 06-08 criterion is mapped to an exact report, capture,
  test, or explicit capability result.
- [ ] No canonical comparison exceeds its variance or regression threshold.
- [ ] Desktop, RVC, allocation, validation, and RenderDoc evidence is valid.
- [ ] Every workstream 06-08 source TODO and invalidated earlier gate is updated
  and marked acceptance complete.
- [ ] The optimization sequence is promoted or explicitly rejected with
  retained evidence.
