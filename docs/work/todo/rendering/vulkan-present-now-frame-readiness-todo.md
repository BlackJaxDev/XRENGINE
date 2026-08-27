# Vulkan Present-Now Frame Readiness TODO

Last Updated: 2026-08-26
Owner: Rendering / Vulkan, with Data / Serialization cleanup
Status: Prior Desktop Acceptance Passed; Current Cold Sponza Fast-Path Fix Builds, Live Revalidation Pending

## 2026-08-26 Validation Checkpoint

The desktop Vulkan `PresentNow + BlockForExact` path passed the isolated Sponza
acceptance runs, including scheduling capacity forced to one. A later current-
tree run exposed a narrower prepared-cohort regression: when newly visible
`sponza_371` started a successful asynchronous pipeline compile, the matching-
cohort fast path treated its cold non-reusable entry as terminal after one
attempt. Source now retains that accepted foreground cohort and falls through
to the existing PresentNow wait/pump loop. The fix builds, but a fresh live
camera-to-Sponza recovery pass remains required before claiming current-tree
acceptance again.

Checkbox convention: implementation items are checked from source/build
evidence. Acceptance, live-validation, allocation, and fault-injection items
remain unchecked until their stated runtime evidence exists.

Latest targeted build validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -p:BuildProjectReferences=false
```

Result: 0 warnings, 0 errors on 2026-08-26 at 16:52 local time after preserving
foreground readiness across a prepared-cohort cold-entry miss.

Latest full editor validation:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Result: 0 warnings, 0 errors on 2026-08-26 at 16:14 local time. The
previously observed concurrent `UnityAnimImporter.cs` compile break is no
longer present. A later isolated editor build containing the Sponza fix also
compiled with 0 errors and nine existing OscCore warnings, but startup was
blocked by a concurrent settings-projection regression. Its owner subsequently
fixed that regression; the next incremental isolated build was invalidated by
concurrent session-retention cleanup deleting its artifact tree mid-build.

Validated implementation in the working tree:

- Runtime-only scene events and light-binding publication state are excluded
  from MemoryPack/YAML/asset-graph persistence boundaries.
- Explicit output work-class/readiness contracts propagate through the output
  DAG. Desktop terminal work uses `PresentNow` plus `BlockForExact`; OpenXR
  paths expose explicit deadline/failure semantics instead of silent reuse.
- Desktop frame order is now slot retirement, accepted logical-plan capture,
  exact foreground readiness, target compatibility revalidation, acquire,
  native reseal, record, submit, and present. Cold readiness no longer holds an
  acquired WSI image.
- A slot-owned `VulkanAcceptedFramePlan` carries bounded operation lanes,
  prepared mesh ingress, texture manifest, shadow manifest/result, output
  contract, planner snapshot, and target compatibility.
- Pipeline compile results are durable by exact pipeline key and dependency
  generation; PresentNow can synchronously claim/finish a cold compile.
- A prepared-cohort match containing a cold non-reusable entry no longer exits
  before foreground readiness. PresentNow retains the accepted requests and
  uses the same job-pump/wait path as ordinary cold mesh materialization.
- Required texture work is tracked by exact texture-generation manifests,
  uses a reserved foreground staging lane, chunks large mips, and waits only
  required transfer work. Devices without a dedicated transfer queue have an
  explicit graphics-queue foreground upload path.
- Exact shadow readiness bypasses background budgets; deadline fallback is
  limited to a fallback explicitly selected in the sealed shadow manifest.
- Mesh request storage has independent terminal, UI, main-scene, and shadow
  capacities. Foreground bypasses scheduling caps but not physical lane bounds,
  and overflow reports the precise lane and high-water requirement.
- Direct OpenXR capture, desktop drain, and explicit-output drain retain a
  typed capacity record with accepted-frame lane, mesh lane, configured,
  required, accepted, and rejected counts. Live overflow proof remains open.
- Frame-plan slot streams and planning scratch storage are preallocated at
  lifecycle construction. Active sealing returns typed capacity failures
  instead of resizing arrays.
- PresentNow recording policy forbids progressive deferral and artifact/old
  frame reuse at the source. `RecordedWithGpuFallback` counts as a fresh
  recording.
- Final presentation ledger entries now include frame provenance, accepted
  epoch/output generation, policy and fallback selection, acquire/record/
  submit/present results and timestamps, timeline serials, lane counts, target
  compatibility, and present-semaphore provenance.
- Exact pipeline, descriptor, buffer, texture-upload, and shadow dependency
  tickets are populated into the accepted plan and advance monotonically.
- Imported texture generations remain pinned through accepted-plan lifetime;
  publication, descriptor-table visibility, and native image layout are
  revalidated against the frozen generation before recording.
- Canonical GPU-scene publication pins are deduplicated while requests are
  published, then move into the exact accepted desktop/explicit-output frame
  slot on the complete foreground drain. Slot reuse and renderer teardown
  release them deterministically; background and pre-plan OpenXR capture keep
  the same bounded aggregate bridge until an accepted slot exists.
- OpenXR eye and mirror worker inputs/results carry logical-view identity,
  output index, and exact output contracts through bounded preallocated views.
- Native descriptor publication is transactional: native update occurs first,
  semantic publication follows, and an exception marks the touched sets unknown
  so consumers cannot claim a partially published generation.
- Desktop terminal settlement now publishes a typed failure record independent
  of its stage-specific orchestration reason. It distinguishes no-image,
  out-of-date, surface-lost, device-lost, host/device OOM, caller cancellation,
  admission, readiness, recording, submission, and presentation outcomes, and
  logs frame/slot/epoch/output/native-result/exception identity for genuine
  failures. Native allocation results survive readiness exception wrapping.

Live evidence:

- Primary isolated session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-104406-present-now-readiness/`.
- Capacity-one isolated session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-145940-present-now-capacity1-acceptance/`.
- Fresh integrated capacity-one isolated session built from the current tree:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-154722-present-now-capacity1-integrated/`.
- Frame-slot publication-pin capacity-one session built from the current tree:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-161446-present-now-pin-slot/`.
- Authored persistence and Sponza-failure diagnosis session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-163804-present-now-roundtrip-overflow/`.
- Durable screenshots and RenderDoc attempts:
  `Build/_AgentValidation/20260825-160514-present-now-readiness/`.
- The primary run retained eight consecutive frames 11919-11926 and a
  64-frame sweep 14801-14864 with matching source/frame IDs, fresh provenance,
  monotonic graphics signals, no fallback, and no invariant failure.
- The capacity-one run retained 43 consecutive frames 3474-3516 and a second
  96-frame away-to-Sponza sweep 3989-4084. Both cohorts had zero stale/fallback/
  submit-present/provenance/dependency failures. Dependencies grew from 34 to
  127 and main-scene operations from 7 to 849 without losing liveness.
- The fresh integrated capacity-one rebuild retained another 96 consecutive
  settled Sponza frames 4176-4271. Every frame was freshly recorded and
  presented with matching frame/source IDs, native descriptor-write receipts,
  strictly increasing graphics signals and descriptor sequences, 127 ready
  dependencies, and zero invariant/fallback/provenance failures. Its frame-op
  trace contained 811 operations and active Sponza deferred draws; texture
  streaming was quiescent at 39 tracked textures with zero pending decode,
  transition, or upload work.
- The integrated away capture was black and the fixed dense capture showed the
  textured Sponza wall. Both PNGs were inspected at original resolution under
  `mcp-captures/capacity1-integrated-away/` and
  `mcp-captures/capacity1-integrated-dense/` in the durable evidence root.
- The frame-slot pin run repeated the capacity-one away-to-Sponza transition.
  Its black away capture and textured Sponza capture were inspected at original
  resolution under `mcp-captures/pin-slot-away-black/` and
  `mcp-captures/pin-slot-dense/`. Across 96 consecutive frames 4377-4472,
  frame/source IDs remained exact, graphics and descriptor sequences increased
  strictly, dependency counts ranged 34-127, scene operations ranged 7-845,
  and all failure/fallback/invariant counts were zero. The latest trace held
  845 operations with active Sponza deferred draws; 39 textures were quiescent
  with no pending transition, decode, or upload work.
- Dense Sponza produced 395 GPU commands: 361 opaque deferred and 32 masked.
  Texture streaming quiesced at 39 textures with no pending decode/upload work.
- PresentNow primary recording, frame-plan preparation, dependency snapshots,
  descriptor publication, submission, and operation loops reported zero
  managed allocation and zero allocation high-water.
- Vulkan validation messages/errors, `RecordingDeferred`, pipeline-pending,
  draw-not-ready, dropped-operation/draw, and missing-swapchain-write counters
  were all zero. The original MemoryPack and large-light-array warnings were
  absent.
- The integrated session's full logs likewise contained none of the target
  failure signatures. Existing unrelated rendering warnings remained for TSR
  rejecting unavailable temporal history and Vulkan GPU BVH raycast requests
  being unsupported; neither affected fresh-frame presentation.
- The authored persistence pass exported a scene containing a transformed
  point-light component, copied it to a new path to defeat path-cache reuse,
  deleted the source scene, imported from disk, and recovered the same node and
  component IDs, translation `(12.5, -3.25, 7.75)`, scale
  `(1.25, 2.5, 0.75)`, radius `42.75`, and brightness `6.5`. Once made visible,
  scene integrity reported zero errors and zero warnings.
- The same session reproduced the newly reported Sponza blackout. Frame 5198
  paused at `MeshMaterialization` after `sponza_371` returned
  `ProgramsPending`; its async graphics pipeline completed successfully 13.23
  ms later. The one-shot prepared-cohort path had already converted that
  transient state into `PresentNowReadinessFailed`, followed by generic failed
  terminal records on every render tick.

### Known incomplete work

- Live-revalidate the prepared-cohort cold-entry fix by moving from an away
  view into dense Sponza on a fresh isolated capacity-one session. The source
  fix and targeted Vulkan build pass; runtime proof was interrupted by a
  concurrent settings startup regression and then an isolated-artifact cleanup
  race.
- Validate camera/scene mutation during blocked preparation, naturally exceed
  a declared mesh lane to prove the new actionable overflow diagnostic, and
  inject permanent failures. These acceptance/fault-injection items remain
  unchecked.
- Exercise the OpenXR path on a real runtime/headset. Its contracts and bounded
  worker inputs build, but this desktop-only validation cannot prove deadline
  behavior.
- Capture a representative settled RenderDoc frame after aligning the installed
  RenderDoc and `rdc` module versions. The available 1.41 capture contains only
  the startup swapchain frame (11 events, zero draws); a settled 1.41 trigger
  stalls the intercepted graphics timeline, while installed RenderDoc 1.44
  crashes CoreCLR during startup before the configured frame is reached.
- Complete Phase 8 only after the user explicitly clears test additions.
- Do not begin Phase 8 test additions until Phase 7 passes and the user
  explicitly clears test work, as required by repository policy.

### Exact resume sequence

1. Do not stop or reuse another task's editor. Wait until
   `present-now-capacity1-integrated` is no longer live, confirm no isolated
   session build is running, then start a new uniquely named session with
   `settings/capacity1-session-environment.json`.
2. Clear the final-presentation ledger, capture an away frame, focus the Sponza
   root, and capture the dense frame. Inspect the PNG and verify logs contain no
   `RendererPaused` or `PresentNowReadinessFailed`; a cold `ProgramsPending`
   observation is acceptable only if the same accepted frame waits and then
   continues to fresh record/submit/present.
3. Retain at least 96 post-transition ledger entries and verify matching frame/
   source IDs, monotonic graphics signals, ready dependencies, no fallback or
   invariant failures, and no repeated generic terminal-failure records. If
   this passes, check the deliberately slow successful compile acceptance item.
4. In a separate fresh session, overlap one duplicate Sponza hierarchy to
   naturally exceed the main-scene lane. Verify one bounded diagnostic reports
   accepted-frame lane, mesh lane, configured, required, accepted, and rejected
   counts, then check the Phase 3 overflow and Phase 6 genuine-failure items if
   the terminal record remains stable and fully identified.
5. Run the targeted Vulkan build and full editor build after concurrent working-
   tree changes settle. Preserve and report unrelated failures.
6. Align RenderDoc capture and replay versions, then repeat the fixed-camera
   capacity-one capture and inspect/export a settled Sponza frame.
7. Validate OpenXR on an available runtime/headset.
8. Ask the user for explicit test clearance, then execute the still-unchecked
   Phase 8 contract/fault-injection/mutation/soak matrix.

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

- [x] Add a distinct foreground work class, such as
  `ERenderWorkClass.PresentNow`, rather than deriving urgency only from
  priority, fallback flags, or a nominal completion deadline.
- [x] Add an explicit present policy, such as `BlockForExact` and
  `MeetDeadlineWithGpuFallback`.
- [x] Keep `Prewarm` or `BackgroundMayDefer` as a separate work class whose
  pending state cannot propagate into foreground presentation.
- [x] Give every frame a stable frame ID, scene epoch, output generation,
  submit serial, and presented-source-frame ID.
- [x] Define `PresentedNew` to require all of the following:
  - a command buffer was recorded for that frame;
  - a new queue submission serial was produced; and
  - presentation waits on the completion signal for that submission.
- [x] A frame with `submitResult=not-submitted` must never report
  `PresentedNew`.
- [x] Legal `PresentNow` outcomes are `PresentedNew`, `FailedFrame`,
  `FailedRenderer`, and explicit caller cancellation.
- [x] `Deferred`, `Superseded`, and `RepeatedOld` remain legal only for work
  whose policy explicitly allows them.

### Atomicity and progress

- [x] Keep atomicity at the sealed-frame publication and submit boundary, not
  as an all-or-nothing resource-preparation transaction.
- [x] Every successful preparation step must advance durable monotonic state
  for one resource generation.
- [x] Queue pressure must not reset resource readiness or remove a draw from an
  already accepted foreground frame.
- [x] A terminal failure for a `(resource key, generation)` must be cached and
  diagnosed; retry requires a new generation or an explicit recovery action.
- [x] Foreground work must not be starved by continually admitted background
  work.
- [x] Warm steady-state planning, readiness checking, recording, submission,
  and presentation must not allocate on the managed heap.

## Phase 1 - Remove The Two Serialization/Graph Boundary Warnings

These warnings are independent of the Vulkan liveness failure. Fix them as
contract leaks rather than teaching persistence to serialize live runtime
state.

### 1.1 Scene-node runtime events

Source: `XREngine.Runtime.Core/Scene/SceneNode.cs`

- [x] Mark `ComponentAdded` and `ComponentRemoved` with `RuntimeOnly`,
  `YamlIgnore`, and `MemoryPackIgnore`.
- [x] Audit `Activated` and `Deactivated`; if their compiler-generated backing
  fields are visible to any persistence path, apply the corresponding
  field-targeted runtime/serializer ignore attributes.
- [x] Do not register a MemoryPack formatter for
  `XREvent<(SceneNode, XRComponent)>`. Its values contain live object/event
  references and are not authored scene data.
- [x] Retain the cooked-binary reflection fallback as defensive routing, but
  make the ignore attributes the authoritative semantic contract.
- [x] Verify the original `[MEMORYPACK SERIALIZE FAIL]` warning is absent in a
  live Sponza session.
- [x] Verify ordinary authored scene/component state still round-trips.

### 1.2 Runtime light-binding publication array

Sources:

- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `XREngine.Data/Core/Assets/XRAssetGraphUtility.cs`

- [x] Mark `XRMeshRenderer.BindingPublishers` as `RuntimeOnly` and
  `YamlIgnore` in addition to its existing `MemoryPackIgnore`.
- [x] Make `XRAssetGraphUtility.BuildAccessors` and its shared member filter
  honor `RuntimeOnly`, `YamlIgnore`, and `MemoryPackIgnore` consistently on
  fields and properties.
- [x] Treat a type marked `RuntimeOnly` as outside the authored asset graph.
- [x] Confirm the graph no longer reaches
  `DeferredLightBindingPublisher._deferredLights`, whose 1,024 entries are
  transient binding-publication slots rather than 1,024 scene lights.
- [x] Keep the `> 1000` array safeguard. Do not raise the threshold as the fix.
- [x] Improve any remaining large-array diagnostic to include the owning
  member/path and whether graph completion was affected.
- [x] If a genuinely serialized asset-bearing collection is skipped, return an
  explicit incomplete/failure result instead of silently accepting a partial
  authored graph.

## Phase 2 - Introduce The Present-Now Output Contract

Primary source:

`XREngine.Runtime.Core/Settings/Contracts/Records/RenderOutputRequest.cs`

- [x] Add the work-class and present-policy contracts described above without
  overloading `ERenderOutputCompletionRequirement` or the current fallback
  flags.
- [x] Default `DesktopScene`, `EditorScenePanel`, and terminal `Present` work to
  `PresentNow` with `BlockForExact` in the desktop/editor path.
- [x] Route OpenXR/OpenVR terminal work through `PresentNow` with an explicit
  deadline policy selected by the runtime integration.
- [x] Keep mirrors, probes, thumbnails, and other auxiliary outputs deferable
  only when their own request explicitly permits it.
- [x] Propagate the terminal present requirement through its complete producer
  dependency closure: visible meshes, required frame targets, material
  bindings, descriptors, pipelines, uploads, and required shadow work.
- [x] Do not let an individual dependency retain `AllowBudgetDeferral` when its
  terminal consumer is `PresentNow` and that dependency has no declared
  fallback.
- [x] Publish the selected work class, policy, deadline, and fallback use in
  frame telemetry.

Acceptance criteria:

- [x] A foreground desktop output cannot legally select
  `ERenderOutputWorkDisposition.Deferred`.
- [x] A dependency-policy mismatch is rejected before recording with the exact
  producer/consumer chain in the diagnostic.
- [x] Background deferral behavior remains available and is observably
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

- [x] Capture an immutable camera, visibility, scene, material, light, and
  output-generation snapshot for the accepted frame.
- [x] Compute the complete required dependency closure from that snapshot.
- [x] Keep preparing that accepted frame even if a newer camera or scene epoch
  appears; the newer state becomes the next frame unless the caller explicitly
  cancels.
- [x] Preserve completed resource preparation after cancellation or
  supersession so later frames can reuse it.
- [x] Store the plan in a frame-slot-owned preallocated arena with declared
  limits for draws, dependencies, passes, descriptor writes, resource pins,
  and recording packets.
- [x] Reserve independent capacity for terminal composition/UI, main-scene
  work, and shadows so shadow-caster multiplication cannot starve presentation.
- [x] Grow arenas only at safe scene/topology or frame-slot boundaries, never
  through managed allocation in the render hot path.
- [x] Return `FramePlanCapacityExceeded` with actual/configured counts when a
  declared limit is exceeded. Do not truncate, retry forever, or replay old
  content as success.
- [x] Pin the required residency set through GPU retirement. If the declared
  simultaneous working set cannot fit after bounded reclamation, return
  `RequiredWorkingSetTooLarge`.
- [x] Move format-independent readiness before desktop swapchain acquisition.
- [x] Acquire late, revalidate target-dependent state, and keep the
  acquire-record-submit-present section short.
- [x] On resize or swapchain-generation change, reseal only target-dependent
  state while retaining already prepared scene resources.

Acceptance criteria:

- [ ] Scene/camera mutation during preparation cannot change the accepted
  frame's contents.
- [x] Output acquisition is not held while cold shaders, meshes, textures, or
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

- [x] Make the request queue a scheduling optimization rather than the
  authority for whether a foreground frame exists.
- [x] Replace queue-full/frame-poison behavior with typed scheduling results,
  such as `Scheduled`, `AlreadyScheduled`, `AlreadyReady`, `Backpressured`, and
  `TerminalFailure`.
- [x] `Backpressured` must leave the ticket runnable and retain all completed
  progress.
- [x] Remove `_publishedCohortRejected`-style whole-cohort poisoning.
- [x] Remove the `DrainTo == -1` behavior that clears valid requests because
  one publication or lease failed. Dequeue only entries actually returned.
- [x] Replace per-operation publication leases with sealed frame-plan slots and
  resource-generation pins where practical.
- [x] Deduplicate resource work across foreground and background frames.
- [x] Allow a foreground readiness driver to claim, promote, help execute, or
  wait for the exact tickets in its bounded dependency array.
- [x] If a scheduling queue is full, drain/help it and retry the same ticket;
  do not discard the frame.
- [x] Prevent workers from waiting for queue space while holding resource,
  allocator, or publication locks.

Acceptance criteria:

- [x] Force queue capacity to 1 and prove a cold frame with hundreds of
  dependencies progresses monotonically to ready or a precise terminal
  failure.
- [x] Looking away may reduce latency but is never required to restore
  liveness.
- [x] Completed work is not repeated because a later dependency encountered
  backpressure.

## Phase 5 - Add Foreground Readiness Paths

The existing small time slices remain useful for background work. They are not
correctness gates for `PresentNow`.

### 5.1 Pipelines and recording artifacts

- [x] Make every target-known terminal dependency mandatory at its earliest
  valid boundary: compile the ImGui pipeline at output/context initialization,
  require frame-dependent composition/fallback variants in the sealed
  pre-acquire pipeline manifest, and keep empty-terminal clear/error reporting
  pipeline-free so diagnostics cannot depend on a shader compiler.
- [x] Fail initialization visibly if a mandatory target-known pipeline cannot
  compile; log exact output-generation/format/dynamic-rendering identity before
  propagating the initialization exception.
- [x] Keep ordinary material compilation asynchronous for prewarm/background
  work.
- [x] When a foreground pipeline is cold, deduplicate and promote an in-flight
  job, synchronously help/compile it, or wait for its exact job according to
  the foreground policy.
- [x] Make pipeline queue capacity limit concurrent workers, not the accepted
  logical dependency backlog.
- [x] Cache permanent compile failures with shader, material, pass, variant
  key, program generation, and `VkResult` diagnostics.
- [x] Remove the approximately 2 ms pipeline admission limit from foreground
  readiness; keep it for background prewarm.
- [x] Treat secondary command buffers as an optimization. If a secondary
  artifact is unavailable at foreground recording time, encode the same
  resolved operations inline in the primary command buffer while preserving
  order.

### 5.2 Buffers and textures

- [x] Classify uploads as required by the declared frame quality or explicitly
  optional streaming work.
- [x] Reserve a foreground portion of the staging ring so background texture
  streaming cannot consume all upload capacity.
- [x] Remove the approximately 0.5 ms preparation budget and one-job drain cap
  from required foreground uploads.
- [x] Stream resources larger than the staging ring in bounded chunks instead
  of requiring an oversized temporary allocation.
- [x] Force-flush required transfer batches and express readiness with timeline
  dependencies and barriers; avoid a global GPU-idle wait.
- [x] Publish buffer/texture generations only after their timeline completion
  and bind them through the sealed frame snapshot.
- [x] Ensure the thread waiting for readiness can pump the exact required jobs
  needed to make progress without spinning or deadlocking.

### 5.3 Shadows

- [x] Give shadow updates independent scheduling/arena capacity so Sponza's
  caster multiplicity cannot consume the main-scene/terminal budget.
- [x] Under `BlockForExact`, require current-content shadows unless an existing
  atlas entry exactly matches the captured light/caster/transform/quality key.
- [x] Under `MeetDeadlineWithGpuFallback`, allow only an explicitly declared
  resident shadow fallback, such as a last-complete atlas entry with age
  metadata or an unshadowed/dummy tile.
- [x] Never select stale/unshadowed shadow behavior merely because a queue is
  full.
- [x] Report exact and fallback shadow use in frame telemetry.

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

- [x] Feed primary recording only a sealed plan with no unresolved required
  pipeline, mesh, upload, descriptor, or shadow ticket.
- [x] Once foreground readiness succeeds, reach `vkBeginCommandBuffer` or
  return a concrete recording/device/WSI error.
- [x] Replace the foreground result surface with `Recorded`,
  `RecordedWithGpuFallback`, or explicit failure. Keep `Deferred` only for
  background work.
- [x] Remove `PresentLastCompletedContent` from ordinary foreground cold-work
  recovery.
- [x] Retain old-content reuse only for an output policy that explicitly
  authorizes it and report the original source-frame ID.
- [x] Distinguish no-image, out-of-date, surface-lost, device-lost, host OOM,
  device OOM, and caller cancellation from admission/readiness outcomes.
- [x] Add a configurable liveness watchdog that reports frame stage, active
  ticket, dependency chain, elapsed time, and last monotonic progress.
- [x] On watchdog expiry, fail/pause the renderer explicitly. Do not convert
  the event to deferred work, old-content replay, or a silent backend switch.
- [x] Make arena/residency capacity failure terminal for the affected frame
  with exact high-water information rather than an infinite retry loop.
- [x] Make actual `VK_ERROR_DEVICE_LOST` renderer-terminal until explicit
  Vulkan reinitialization.

Acceptance criteria:

- [x] Every healthy acquired `PresentNow` image receives newly recorded and
  submitted commands.
- [x] Every `PresentedNew(frameId)` has a nonzero submit serial belonging to
  that frame.
- [x] Foreground `RecordingDeferred` and admission-driven
  `PresentLastCompletedContent` counters remain zero.
- [ ] Genuine failures stop retrying and expose enough identity to reproduce
  the failing resource or Vulkan operation.

## Phase 7 - Live Vulkan Validation Before Test Work

Repository policy requires the feature to work through the live/runtime path
before adding tests for this regression. Do not add or modify tests until this
phase passes and the user explicitly clears test work.

- [x] Reserve one bounded task run under `Build/_AgentValidation/`.
- [x] Start one uniquely named isolated MCP editor session with the Unit
  Testing World, Vulkan, Sponza, MCP, and Vulkan validation enabled.
- [x] Use MCP to capture and inspect at least these camera cohorts:
  - looking completely away from Sponza;
  - first cold look toward a sparse portion;
  - first cold look toward the dense atrium;
  - rapid sweeps across and away from the model;
  - stable warm view after the cold frame completes.
- [x] Confirm screenshots change with the camera and that the first newly
  visible Sponza frame is fresh rather than replayed content.
- [x] Record per-frame IDs, scene epochs, request/dependency counts, ticket
  stages, fallback counts, command-buffer IDs, submit serials, and presented
  source-frame IDs.
- [x] Assert zero foreground `RecordingDeferred`, zero whole-cohort poison,
  zero admission-driven old-frame replay, and no endlessly retried permanent
  failures.
- [x] Confirm `vkBeginCommandBuffer`, submit, and present occur in the expected
  order for every successful foreground attempt.
- [x] Inspect validation, rendering, general, and lighting logs; separate
  steady-state failures from shutdown-only teardown messages.
- [x] Confirm both serialization/graph warnings from Phase 1 are absent.
- [x] Stop only the named MCP session and preserve the exact evidence path in a
  rendering investigation/progress note.
- [ ] Once frames reach native submission, capture a representative RenderDoc
  frame and verify the current camera state, required targets, and any
  explicitly selected GPU fallback resources. RenderDoc is a post-submission
  validation tool for this defect; the original failure occurred before it
  could capture meaningful draw work.

Live acceptance gate:

- [x] A completely cold Sponza-facing desktop frame may visibly hitch, but the
  next presented scene is the complete fresh accepted frame.
- [x] Looking away changes preparation latency only and is not required for
  recovery.
- [x] Warm steady-state presentation remains allocation-free on engine-managed
  render hot paths.
- [x] Vulkan core and synchronization validation are clean.

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
