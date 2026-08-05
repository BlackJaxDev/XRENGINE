# Vulkan Frame-Plan Recording Refactor TODO

Last Updated: 2026-08-04  
Owner: Rendering / Vulkan  
Status: Proposed

## Objective

Replace broad cross-frame Vulkan command-buffer reuse with a correctness-first
frame-plan model. Cache immutable GPU state and draw descriptions; rebuild the
ordered primary command stream for each frame attempt. Add parallel secondary
recording and narrowly scoped secondary reuse only after fresh recording is
stable and measured.

The model must support all mesh-submission strategies, desktop/capture outputs,
OpenXR stereo and 2--4-view foveated/quad-view output, shadow maps, prepasses,
post processing, UI, and presentation.

## Guardrails

- `FreshSerial` is the reference path: one fresh primary per completed frame
  slot/submission; all work recorded inline.
- Every optimized mode must consume exactly the same immutable frame plan as
  `FreshSerial`.
- Do not treat command-buffer cache-hit percentage as a performance goal.
- Do not add tests until a phase is functionally validated through the relevant
  live/runtime path and test work is explicitly cleared.
- Do not enable cached secondaries for shadows, UI, transfers, or late-pose VR
  work during this refactor's initial implementation.

## Target model

```text
scene/resource/view snapshots
          -> FramePlanBuilder
          -> immutable FramePlan
          -> fresh primary record + optional worker secondaries
          -> submit / complete / retire
```

The plan owns all current-attempt identities and order:

| Type | Required contents |
| --- | --- |
| `FramePlan` | render frame/slot ID, resource generation, view set, ordered submissions |
| `ViewSetPlan` | 1--4 views, output topology, optional multiview groups |
| `ViewPlan` | matrices, viewport/scissor, jitter/history, eye/foveation role, output layer |
| `PassPlan` | pass kind, attachments, load/store/clear policy, dependencies, render scope |
| `RecordPacket` | ordered draw/dispatch range, immutable bindings, entry/exit state, worker eligibility |
| `SubmissionPlan` | queue, ordered pass range, waits/signals, output handoff, completion token |

Mesh strategy decides packet contents, not frame-loop behavior:

- `CpuDirect`: direct sorted draw packets.
- `GpuIndirectInstrumented`: GPU cull/scatter plus explicitly diagnostic draws.
- `GpuIndirectZeroReadback`: GPU cull/scatter plus indirect-count draws; no CPU count/range readback.
- `GpuMeshletInstrumented`: diagnostic GPU meshlet packets.
- `GpuMeshletZeroReadback`: GPU-count mesh-task dispatch; no CPU visibility/count readback.

## Implementation checklist

### 0. Establish a reference baseline

- [ ] Add a `FreshSerial` recording mode.
  - Record fresh primaries from the existing sorted `FrameOp` stream.
  - Disable primary and secondary artifact reuse in this mode.
  - Keep one graphics-queue sequence initially; do not introduce async-compute changes here.

- [ ] Add comparable per-frame telemetry.
  - Capture plan/build, primary-record, worker-record/wait, submit, and present time.
  - Capture packet counts by pass and mesh strategy, queue submissions, stale-visibility decisions, and output/view-set identity.
  - Emit scalar counters in the existing profiler and profile-capture surfaces; avoid hot-path string allocations.

- [ ] Validate the baseline live.
  - Capture desktop mono with moving camera, moving light, animated/skinned casters, and UI.
  - Capture resize and swapchain recreation.
  - Capture OpenXR stereo and the supported foveated/quad-view configuration.
  - Require stable screenshots, no Vulkan validation errors, and no rejected submit/present attempt.

### 1. Define immutable frame-plan types

- [ ] Add the six target plan types and a `FramePlanBuilder`.
  - Freeze resource generation, exact output image identity, view data, packet order, and submission dependencies.
  - Do not retain live camera, scene, material, descriptor, swapchain, or OpenXR references in a plan.
  - Discard a plan after failed acquire/submit/present without publishing its recorded image-state overlay.

- [ ] Lower the current `FrameOp` stream into a plan.
  - Keep `FrameOp` as the frontend collection API during migration.
  - Make the existing inline primary recorder consume the plan.
  - Compare ordered operation traces with `FreshSerial` before moving more paths.

- [ ] Preserve frame-slot ownership.
  - Permit pool reset, descriptor mutation, upload-arena reuse, and artifact retirement only after the slot completion primitive signals.
  - Publish image state only after successful submit; publish completed state only after completion.

### 2. Generalize views and outputs

- [ ] Route desktop and presentationless/capture paths through `ViewSetPlan`.
  - Support one desktop/mono view and arbitrary offscreen/cubemap/layered requests.
  - Represent mirror composition as an explicit output pass, not an eye special case.

- [ ] Route OpenXR through `ViewSetPlan`.
  - Support independent left/right views and 2--4 views for wide/inset/foveated configurations.
  - Treat multiview as an optional grouping optimization, never as a correctness requirement.
  - Keep exact acquired OpenXR image/layer identity in each output contract.

- [ ] Validate topology transitions.
  - Change view count/mode, resize outputs, and recreate swapchains during a live session.
  - Confirm all changes create new resource/recording generations rather than executing a prior view's work.

### 3. Move pass scheduling into the plan

- [ ] Build explicit pass dependencies and deterministic order.
  - Schedule shared uploads, skinning, GPU scene/BVH work, and light publication before dependent passes.
  - Schedule shadow atlas passes before consumers; schedule per-view prepass, opaque, lighting, transparency, post, UI, and output handoff in dependency order.
  - Start with one graphics queue; introduce a separate compute submission only with an explicit ownership/wait contract.

- [ ] Centralize primary-owned operations.
  - Keep barriers, render scopes, clears, resolves, queries, timestamps, debug labels, ownership transfers, and present transitions in the fresh primary.
  - Never allow a worker secondary to establish global image state.

- [ ] Verify every mesh strategy.
  - Run the same plan and pass ordering for CPU direct, instrumented GPU indirect, zero-readback indirect, and supported meshlet modes.
  - Fail a selected GPU/meshlet strategy visibly; do not fall through to another strategy while recording.

### 4. Add deterministic parallel recording

- [ ] Add worker recording from `RecordPacket` snapshots.
  - Give each worker a command pool owned by its frame slot.
  - Return a recorded secondary and validated packet entry/exit-state contract.
  - Merge by immutable packet ordinal, never by worker completion order.

- [ ] Start with low-risk packet classes.
  - Enable only sufficiently large desktop opaque CPU-direct packets first.
  - Then enable compatible GPU indirect/meshlet packets and independent per-view opaque packets.
  - Keep shadows, UI, transfers, readbacks, presentation, and late-pose composition inline.

- [ ] Provide failure-safe fallback.
  - On worker rejection, timeout, or allocation failure, record the exact packet inline from the same plan.
  - Report the rejection reason; never reuse stale output or abandon the rest of the frame.

- [ ] Validate serial/parallel equivalence.
  - Compare ordered plan/packet traces and screenshots for desktop mono, stereo, and quad/foveated output.
  - Measure p50/p95/p99 frame time and CPU record time; retain parallelism only where it improves a relevant percentile.

### 5. Add narrow optional secondary reuse

- [ ] Define one `RecordedPacketKey`.
  - Include render-scope inheritance, target/resource generation, pipeline/layout generation, immutable draw-packet identity, descriptor-instance identity, exact view identity, and frame-slot arena generation.
  - Do not use independent dirty masks or broad primary variants as cache authority.

- [ ] Enable reuse for one static desktop opaque cohort.
  - Bound the cache per frame slot and retire artifacts through slot completion.
  - Treat every miss as normal fresh recording.
  - Require output equivalence and a measurable p95 CPU/frame-time benefit before widening eligibility.

- [ ] Keep sensitive passes uncached.
  - Do not cache shadow atlas/per-light shadow packets, UI/debug, transfers/readbacks, present, or late-pose-sensitive VR packets.
  - Add any future exception only with a dedicated experiment, visual evidence, and an explicit acceptance gate.

### 6. Remove superseded cache machinery

- [ ] Remove primary command-buffer variant caches and their cache-only scheduling branches.
  - Preserve only data needed for current-plan recording, bounded secondary reuse, and resource retirement.
  - Collapse duplicated dirty-reason logic into packet eligibility/identity diagnostics.

- [ ] Update architecture documentation.
  - Revise `vulkan-command-recording.md` and `vulkan-primary-command-buffer-reuse.md` to describe the implemented model.
  - Remove legacy reuse-ratio acceptance gates and obsolete settings/documentation.

## Final acceptance

- [ ] `FreshSerial` is stable for desktop, capture, OpenXR stereo, and supported 2--4-view foveated/quad-view output.
- [ ] Every mesh strategy uses the same frame-plan and submission model, with no forbidden zero-readback CPU reads.
- [ ] Parallel mode matches serial output and ordering with no validation errors.
- [ ] No command pool, descriptor, image, or artifact is reused before frame-slot completion.
- [ ] Swapchain/resource/view-topology changes do not execute stale recordings.
- [ ] Optional secondary reuse improves measured p95 CPU/frame time; otherwise it remains disabled.
