# Vulkan Present-Now Frame Readiness TODO

Last Updated: 2026-08-25
Owner: Rendering / Vulkan, with Data / Serialization cleanup
Status: Ready For Implementation; Architecture Decision Recorded; Live Validation Required Before Test Work

Related work:

- [Vulkan Command Recording Architecture Optimization](optimization/vulkan-command-recording-architecture-optimization-todo.md)
- [Vulkan Resident Draw Stream And Render Task Pool](optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)
- [Texture Runtime Streaming And Virtual Texturing](../texturing/texture-runtime-streaming-virtual-texturing-todo.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

## Goal

Make foreground presentation truthful and live under cold-resource pressure.
An output that must be presented now must produce newly recorded and submitted
GPU work, block while its declared required resources become ready, or fail
explicitly. It must never enter an admission-driven `RecordingDeferred` loop or
silently present the last completed frame because the currently visible scene
is large or cold.

The motivating reproduction is a cold Vulkan Sponza view. Looking toward
Sponza admitted a large visibility cohort and repeatedly rejected the complete
recording before `vkBeginCommandBuffer`; looking away reduced the cohort enough
for progress and apparent recovery. This is a CPU-side readiness/admission
livelock, not evidence of a Vulkan device crash.

This tracker also owns the two independent runtime-serialization warnings seen
in the same run so that the complete run becomes diagnostically clean.

## Recorded Product Decision

`PresentNow` is a distinct execution contract, not a higher priority inside
the existing deferred queues.

For a healthy renderer and successfully acquired output image:

> A `PresentNow` attempt records and submits new GPU work or returns an explicit
> failure. `RecordingDeferred`, queue-full rejection, and implicit old-frame
> replay are illegal outcomes.

Use these policies:

| Work class | Policy | Required behavior |
| --- | --- | --- |
| Desktop/editor foreground | `PresentNow` + `BlockForExact` | Freeze the frame snapshot, finish the resources required by its declared quality, then record, submit, and present. A first-view hitch is acceptable. |
| XR or another hard-deadline foreground | `PresentNow` + `MeetDeadlineWithGpuFallback` | Record a fresh frame by the cutoff using only explicitly declared resident GPU fallbacks. If the contract permits no fallback, report a missed-frame/fatal result. |
| Prewarm, probes, thumbnails, and other background work | `Prewarm` / `BackgroundMayDefer` | Time-slice, resume, coalesce, or supersede without poisoning foreground work. |

Exact full-quality cold rendering, an immovable display deadline, and no
fallback cannot all be guaranteed simultaneously. Desktop/editor presentation
therefore blocks for exact work. Deadline-bound XR must use explicit GPU
fallback resources or expose a failure; it must not silently claim that stale
content is the requested frame. Do not add a CPU renderer or silently switch
rendering APIs.

## Evidence From The 2026-08-25 Run

Source log session:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-25_15-31-44_pid49256/`

Observed evidence:

- A smaller Sponza-facing cohort reported 221 requests, 47 warm, 174 cold, and
  171 deferred.
- A denser view reported 836 requests, 47 warm, 789 cold, and 784 deferred.
- Hundreds of individual resources subsequently became warm, but later
  whole-frame cohorts were still rejected atomically instead of publishing
  accumulated progress.
- Rejected attempts stopped before `vkBeginCommandBuffer`, reported
  `submitResult=not-submitted`, and could still reach the old-content desktop
  presentation policy.
- No device-loss, Vulkan OOM, or native submission failure explained the
  incident. Vulkan validation was not enabled in that run, so a
  validation-enabled acceptance pass remains mandatory.
- Looking away reduced visible work and allowed the bounded queues/slices to
  make progress. It did not reset or repair the GPU.
- Frame-failure logs are throttled; recurring messages establish continued
  rejection but are not a one-to-one count of every failed attempt.

Current contributing behavior:

- `RenderOutputRequest.CreateDefault` allows cadence reduction and budget
  deferral for interactive and presentation classes even when completion is
  required before present.
- `VulkanMeshOperationRequestQueue` has a fixed 4,096-entry capacity. A queue
  publication or retained-lease failure can poison and clear the complete
  cohort.
- Cold mesh preparation is limited to approximately 4 ms per frame.
- Required primary-pipeline admission is scanned for approximately 2 ms.
- The pipeline compile queue defaults to one worker and eight active entries.
- Texture upload preparation is effectively limited to approximately 0.5 ms
  and one prepared upload per drain.
- Resource readiness is currently discovered after desktop swapchain acquire,
  so a cold frame can hold presentation resources while doing work that should
  have occurred before acquisition.

## Required Invariants

### Foreground result truthfulness

- [ ] Add a distinct foreground work class, such as
  `ERenderWorkClass.PresentNow`, rather than deriving urgency only from
  priority, fallback flags, or a nominal completion deadline.
- [ ] Add an explicit present policy, such as `BlockForExact` and
  `MeetDeadlineWithGpuFallback`.
- [ ] Keep `Prewarm` or `BackgroundMayDefer` as a separate work class whose
  pending state cannot propagate into foreground presentation.
- [ ] Give every frame a stable frame ID, scene epoch, output generation,
  submit serial, and presented-source-frame ID.
- [ ] Define `PresentedNew` to require all of the following:
  - a command buffer was recorded for that frame;
  - a new queue submission serial was produced; and
  - presentation waits on the completion signal for that submission.
- [ ] A frame with `submitResult=not-submitted` must never report
  `PresentedNew`.
- [ ] Legal `PresentNow` outcomes are `PresentedNew`, `FailedFrame`,
  `FailedRenderer`, and explicit caller cancellation.
- [ ] `Deferred`, `Superseded`, and `RepeatedOld` remain legal only for work
  whose policy explicitly allows them.

### Atomicity and progress

- [ ] Keep atomicity at the sealed-frame publication and submit boundary, not
  as an all-or-nothing resource-preparation transaction.
- [ ] Every successful preparation step must advance durable monotonic state
  for one resource generation.
- [ ] Queue pressure must not reset resource readiness or remove a draw from an
  already accepted foreground frame.
- [ ] A terminal failure for a `(resource key, generation)` must be cached and
  diagnosed; retry requires a new generation or an explicit recovery action.
- [ ] Foreground work must not be starved by continually admitted background
  work.
- [ ] Warm steady-state planning, readiness checking, recording, submission,
  and presentation must not allocate on the managed heap.

## Phase 1 - Remove The Two Serialization/Graph Boundary Warnings

These warnings are independent of the Vulkan liveness failure. Fix them as
contract leaks rather than teaching persistence to serialize live runtime
state.

### 1.1 Scene-node runtime events

Source: `XREngine.Runtime.Core/Scene/SceneNode.cs`

- [ ] Mark `ComponentAdded` and `ComponentRemoved` with `RuntimeOnly`,
  `YamlIgnore`, and `MemoryPackIgnore`.
- [ ] Audit `Activated` and `Deactivated`; if their compiler-generated backing
  fields are visible to any persistence path, apply the corresponding
  field-targeted runtime/serializer ignore attributes.
- [ ] Do not register a MemoryPack formatter for
  `XREvent<(SceneNode, XRComponent)>`. Its values contain live object/event
  references and are not authored scene data.
- [ ] Retain the cooked-binary reflection fallback as defensive routing, but
  make the ignore attributes the authoritative semantic contract.
- [ ] Verify the original `[MEMORYPACK SERIALIZE FAIL]` warning is absent while
  ordinary authored scene/component state still round-trips.

### 1.2 Runtime light-binding publication array

Sources:

- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `XREngine.Data/Core/Assets/XRAssetGraphUtility.cs`

- [ ] Mark `XRMeshRenderer.BindingPublishers` as `RuntimeOnly` and
  `YamlIgnore` in addition to its existing `MemoryPackIgnore`.
- [ ] Make `XRAssetGraphUtility.BuildAccessors` and its shared member filter
  honor `RuntimeOnly`, `YamlIgnore`, and `MemoryPackIgnore` consistently on
  fields and properties.
- [ ] Treat a type marked `RuntimeOnly` as outside the authored asset graph.
- [ ] Confirm the graph no longer reaches
  `DeferredLightBindingPublisher._deferredLights`, whose 1,024 entries are
  transient binding-publication slots rather than 1,024 scene lights.
- [ ] Keep the `> 1000` array safeguard. Do not raise the threshold as the fix.
- [ ] Improve any remaining large-array diagnostic to include the owning
  member/path and whether graph completion was affected.
- [ ] If a genuinely serialized asset-bearing collection is skipped, return an
  explicit incomplete/failure result instead of silently accepting a partial
  authored graph.

## Phase 2 - Introduce The Present-Now Output Contract

Primary source:

`XREngine.Runtime.Core/Settings/Contracts/Records/RenderOutputRequest.cs`

- [ ] Add the work-class and present-policy contracts described above without
  overloading `ERenderOutputCompletionRequirement` or the current fallback
  flags.
- [ ] Default `DesktopScene`, `EditorScenePanel`, and terminal `Present` work to
  `PresentNow` with `BlockForExact` in the desktop/editor path.
- [ ] Route OpenXR/OpenVR terminal work through `PresentNow` with an explicit
  deadline policy selected by the runtime integration.
- [ ] Keep mirrors, probes, thumbnails, and other auxiliary outputs deferable
  only when their own request explicitly permits it.
- [ ] Propagate the terminal present requirement through its complete producer
  dependency closure: visible meshes, required frame targets, material
  bindings, descriptors, pipelines, uploads, and required shadow work.
- [ ] Do not let an individual dependency retain `AllowBudgetDeferral` when its
  terminal consumer is `PresentNow` and that dependency has no declared
  fallback.
- [ ] Publish the selected work class, policy, deadline, and fallback use in
  frame telemetry.

Acceptance criteria:

- [ ] A foreground desktop output cannot legally select
  `ERenderOutputWorkDisposition.Deferred`.
- [ ] A dependency-policy mismatch is rejected before recording with the exact
  producer/consumer chain in the diagnostic.
- [ ] Background deferral behavior remains available and is observably
  separate from foreground readiness.

## Phase 3 - Build And Seal A Bounded Frame Plan

Split foreground execution into explicit stages:

```text
BuildFramePlan
  -> DriveFrameReadiness
  -> SealRecordingPlan
  -> AcquireOutputImage
  -> RecordPrimary
  -> Submit
  -> Present
```

- [ ] Capture an immutable camera, visibility, scene, material, light, and
  output-generation snapshot for the accepted frame.
- [ ] Compute the complete required dependency closure from that snapshot.
- [ ] Keep preparing that accepted frame even if a newer camera or scene epoch
  appears; the newer state becomes the next frame unless the caller explicitly
  cancels.
- [ ] Preserve completed resource preparation after cancellation or
  supersession so later frames can reuse it.
- [ ] Store the plan in a frame-slot-owned preallocated arena with declared
  limits for draws, dependencies, passes, descriptor writes, resource pins,
  and recording packets.
- [ ] Reserve independent capacity for terminal composition/UI, main-scene
  work, and shadows so shadow-caster multiplication cannot starve presentation.
- [ ] Grow arenas only at safe scene/topology or frame-slot boundaries, never
  through managed allocation in the render hot path.
- [ ] Return `FramePlanCapacityExceeded` with actual/configured counts when a
  declared limit is exceeded. Do not truncate, retry forever, or replay old
  content as success.
- [ ] Pin the required residency set through GPU retirement. If the declared
  simultaneous working set cannot fit after bounded reclamation, return
  `RequiredWorkingSetTooLarge`.
- [ ] Move format-independent readiness before desktop swapchain acquisition.
- [ ] Acquire late, revalidate target-dependent state, and keep the
  acquire-record-submit-present section short.
- [ ] On resize or swapchain-generation change, reseal only target-dependent
  state while retaining already prepared scene resources.

Acceptance criteria:

- [ ] Scene/camera mutation during preparation cannot change the accepted
  frame's contents.
- [ ] Output acquisition is not held while cold shaders, meshes, textures, or
  shadows are being prepared.
- [ ] Every overflow reports a bounded, actionable error rather than changing
  visible content.

## Phase 4 - Replace Cohort Poisoning With Monotonic Resource Tickets

Primary Vulkan surfaces include:

- `VulkanMeshOperationRequestQueue`
- `VkMeshRenderer.OnRenderRequested`
- `VulkanFrameLoop.PrimaryRecordingPreparation.DrainQueuedMeshRenderRequests`
- prepared mesh ingress and publication-lease handling

Use a generational resource state machine equivalent to:

```text
Declared
  -> CpuPrepared
  -> GpuAllocationReserved
  -> UploadSubmitted (with monotonic byte progress)
  -> Resident
  -> Ready

Any stage -> Failed(error)
```

- [ ] Make the request queue a scheduling optimization rather than the
  authority for whether a foreground frame exists.
- [ ] Replace queue-full/frame-poison behavior with typed scheduling results,
  such as `Scheduled`, `AlreadyScheduled`, `AlreadyReady`, `Backpressured`, and
  `TerminalFailure`.
- [ ] `Backpressured` must leave the ticket runnable and retain all completed
  progress.
- [ ] Remove `_publishedCohortRejected`-style whole-cohort poisoning.
- [ ] Remove the `DrainTo == -1` behavior that clears valid requests because
  one publication or lease failed. Dequeue only entries actually returned.
- [ ] Replace per-operation publication leases with sealed frame-plan slots and
  resource-generation pins where practical.
- [ ] Deduplicate resource work across foreground and background frames.
- [ ] Allow a foreground readiness driver to claim, promote, help execute, or
  wait for the exact tickets in its bounded dependency array.
- [ ] If a scheduling queue is full, drain/help it and retry the same ticket;
  do not discard the frame.
- [ ] Prevent workers from waiting for queue space while holding resource,
  allocator, or publication locks.

Acceptance criteria:

- [ ] Force queue capacity to 1 and prove a cold frame with hundreds of
  dependencies progresses monotonically to ready or a precise terminal
  failure.
- [ ] Looking away may reduce latency but is never required to restore
  liveness.
- [ ] Completed work is not repeated because a later dependency encountered
  backpressure.

## Phase 5 - Add Foreground Readiness Paths

The existing small time slices remain useful for background work. They are not
correctness gates for `PresentNow`.

### 5.1 Pipelines and recording artifacts

- [ ] Make terminal composition, UI, fallback, and error-reporting pipelines
  mandatory-resident during renderer/output initialization.
- [ ] Fail initialization visibly if a mandatory pipeline cannot compile.
- [ ] Keep ordinary material compilation asynchronous for prewarm/background
  work.
- [ ] When a foreground pipeline is cold, deduplicate and promote an in-flight
  job, synchronously help/compile it, or wait for its exact job according to
  the foreground policy.
- [ ] Make pipeline queue capacity limit concurrent workers, not the accepted
  logical dependency backlog.
- [ ] Cache permanent compile failures with shader, material, pass, variant
  key, program generation, and `VkResult` diagnostics.
- [ ] Remove the approximately 2 ms pipeline admission limit from foreground
  readiness; keep it for background prewarm.
- [ ] Treat secondary command buffers as an optimization. If a secondary
  artifact is unavailable at foreground recording time, encode the same
  resolved operations inline in the primary command buffer while preserving
  order.

### 5.2 Buffers and textures

- [ ] Classify uploads as required by the declared frame quality or explicitly
  optional streaming work.
- [ ] Reserve a foreground portion of the staging ring so background texture
  streaming cannot consume all upload capacity.
- [ ] Remove the approximately 0.5 ms preparation budget and one-job drain cap
  from required foreground uploads.
- [ ] Stream resources larger than the staging ring in bounded chunks instead
  of requiring an oversized temporary allocation.
- [ ] Force-flush required transfer batches and express readiness with timeline
  dependencies and barriers; avoid a global GPU-idle wait.
- [ ] Publish buffer/texture generations only after their timeline completion
  and bind them through the sealed frame snapshot.
- [ ] Ensure the thread waiting for readiness can pump the exact required jobs
  needed to make progress without spinning or deadlocking.

### 5.3 Shadows

- [ ] Give shadow updates independent scheduling/arena capacity so Sponza's
  caster multiplicity cannot consume the main-scene/terminal budget.
- [ ] Under `BlockForExact`, require current-content shadows unless an existing
  atlas entry exactly matches the captured light/caster/transform/quality key.
- [ ] Under `MeetDeadlineWithGpuFallback`, allow only an explicitly declared
  resident shadow fallback, such as a last-complete atlas entry with age
  metadata or an unshadowed/dummy tile.
- [ ] Never select stale/unshadowed shadow behavior merely because a queue is
  full.
- [ ] Report exact and fallback shadow use in frame telemetry.

Acceptance criteria:

- [ ] A deliberately slow but successful required compile or upload may exceed
  the old per-frame budget and then records the accepted desktop frame.
- [ ] Background work yields to foreground readiness and resumes with its
  earlier progress intact.
- [ ] Saturating shadow work cannot prevent terminal composition or main-scene
  recording.

## Phase 6 - Enforce Recording, Submission, And Failure Semantics

Primary surfaces include:

- `VulkanFrameLoop.PrimaryRecordingPreparation`
- `VulkanPrimaryCommandRecordingResult`
- `EVulkanPrimaryCommandRecordingDisposition`
- `VulkanRejectedDesktopFramePolicy`
- desktop acquire, frame-slot, submit, and present code

- [ ] Feed primary recording only a sealed plan with no unresolved required
  pipeline, mesh, upload, descriptor, or shadow ticket.
- [ ] Once foreground readiness succeeds, reach `vkBeginCommandBuffer` or
  return a concrete recording/device/WSI error.
- [ ] Replace the foreground result surface with `Recorded`,
  `RecordedWithGpuFallback`, or explicit failure. Keep `Deferred` only for
  background work.
- [ ] Remove `PresentLastCompletedContent` from ordinary foreground cold-work
  recovery.
- [ ] Retain old-content reuse only for an output policy that explicitly
  authorizes it and report the original source-frame ID.
- [ ] Distinguish no-image, out-of-date, surface-lost, device-lost, host OOM,
  device OOM, and caller cancellation from admission/readiness outcomes.
- [ ] Add a configurable liveness watchdog that reports frame stage, active
  ticket, dependency chain, elapsed time, and last monotonic progress.
- [ ] On watchdog expiry, fail/pause the renderer explicitly. Do not convert
  the event to deferred work, old-content replay, or a silent backend switch.
- [ ] Make arena/residency capacity failure terminal for the affected frame
  with exact high-water information rather than an infinite retry loop.
- [ ] Make actual `VK_ERROR_DEVICE_LOST` renderer-terminal until explicit
  Vulkan reinitialization.

Acceptance criteria:

- [ ] Every healthy acquired `PresentNow` image receives newly recorded and
  submitted commands.
- [ ] Every `PresentedNew(frameId)` has a nonzero submit serial belonging to
  that frame.
- [ ] Foreground `RecordingDeferred` and admission-driven
  `PresentLastCompletedContent` counters remain zero.
- [ ] Genuine failures stop retrying and expose enough identity to reproduce
  the failing resource or Vulkan operation.

## Phase 7 - Live Vulkan Validation Before Test Work

Repository policy requires the feature to work through the live/runtime path
before adding tests for this regression. Do not add or modify tests until this
phase passes and the user explicitly clears test work.

- [ ] Reserve one bounded task run under `Build/_AgentValidation/`.
- [ ] Start one uniquely named isolated MCP editor session with the Unit
  Testing World, Vulkan, Sponza, MCP, and Vulkan validation enabled.
- [ ] Use MCP to capture and inspect at least these camera cohorts:
  - looking completely away from Sponza;
  - first cold look toward a sparse portion;
  - first cold look toward the dense atrium;
  - rapid sweeps across and away from the model;
  - stable warm view after the cold frame completes.
- [ ] Confirm screenshots change with the camera and that the first newly
  visible Sponza frame is fresh rather than replayed content.
- [ ] Record per-frame IDs, scene epochs, request/dependency counts, ticket
  stages, fallback counts, command-buffer IDs, submit serials, and presented
  source-frame IDs.
- [ ] Assert zero foreground `RecordingDeferred`, zero whole-cohort poison,
  zero admission-driven old-frame replay, and no endlessly retried permanent
  failures.
- [ ] Confirm `vkBeginCommandBuffer`, submit, and present occur in the expected
  order for every successful foreground attempt.
- [ ] Inspect validation, rendering, general, and lighting logs; separate
  steady-state failures from shutdown-only teardown messages.
- [ ] Confirm both serialization/graph warnings from Phase 1 are absent.
- [ ] Stop only the named MCP session and preserve the exact evidence path in a
  rendering investigation/progress note.
- [ ] Once frames reach native submission, capture a representative RenderDoc
  frame and verify the current camera state, required targets, and any
  explicitly selected GPU fallback resources. RenderDoc is a post-submission
  validation tool for this defect; the original failure occurred before it
  could capture meaningful draw work.

Live acceptance gate:

- [ ] A completely cold Sponza-facing desktop frame may visibly hitch, but the
  next presented scene is the complete fresh accepted frame.
- [ ] Looking away changes preparation latency only and is not required for
  recovery.
- [ ] Warm steady-state presentation remains allocation-free on engine-managed
  render hot paths.
- [ ] Vulkan core and synchronization validation are clean.

## Phase 8 - Tests And Fault Injection After Explicit Clearance

- [ ] Add contract tests proving foreground results cannot be `Deferred` or
  falsely report `PresentedNew` without a matching submit serial.
- [ ] Exercise scheduling capacities 1, 8, 32, and the production value to
  prove correctness is independent of queue capacity.
- [ ] Reproduce the observed 221-request and 836-request visibility shapes.
- [ ] Inject slow pipeline compilation and uploads; verify monotonic progress
  beyond the old time slices.
- [ ] Inject shader compile failure, pipeline creation failure, descriptor
  exhaustion, frame-arena overflow, host/device OOM, device loss, timeline
  stall, resize, and swapchain recreation.
- [ ] Verify uploads larger than the staging ring complete through chunking.
- [ ] Mutate camera, transforms, materials, and lights while a foreground frame
  blocks; verify the submitted frame contains exactly one captured epoch.
- [ ] Saturate background uploads, pipeline work, and shadows; verify they
  cannot starve foreground presentation.
- [ ] Run a long warm soak with allocation counters and pool high-water marks;
  require zero engine/managed hot-path allocation and no unbounded pool growth.

## Explicit Non-Fixes

The following may reduce the reproduction rate but do not satisfy this TODO:

- increasing `VulkanMeshOperationRequestQueue.Capacity`;
- increasing `XRE_VULKAN_PIPELINE_COMPILE_WORKERS` without changing foreground
  progress semantics;
- removing the upload-preparation time budget while retaining the one-job
  drain cap;
- prewarming Sponza without fixing cold content generally;
- raising the asset-graph large-array threshold;
- serializing runtime `XREvent` listeners; or
- converting rejected foreground work into previous-frame replay.

Temporary local mitigation may use additional pipeline workers and scene
prewarm to shorten stalls while this architecture is implemented. Do not use
those settings as acceptance evidence.

## Recommended Execution Order

1. Remove the two serialization/asset-graph warnings and confirm their runtime
   state is excluded semantically.
2. Land the `PresentNow`/`Prewarm` contract and truthful frame-result telemetry.
3. Remove whole-cohort queue poisoning and introduce monotonic generational
   resource tickets.
4. Build the bounded immutable frame plan and move readiness before acquire.
5. Add foreground pipeline, upload, shadow, and inline-primary readiness paths.
6. Enforce the new recording/submission/presentation outcomes and watchdog.
7. Complete the isolated cold-Sponza live Vulkan acceptance pass.
8. After explicit clearance, add the targeted tests and fault-injection matrix.

The first architectural proof should deliberately use a tiny scheduling queue.
If cold visible Sponza completes with queue capacity 1, without reset, deferred
recording, or old-frame replay, then the design has removed the correctness
dependency on queue size rather than merely moving its threshold.
