# Vulkan Desktop Frame Loop Decomposition TODO

Last Updated: 2026-07-09
Owner: Rendering / Vulkan
Status: Proposed
Target Branch: `rendering-vulkan-frame-loop-decomposition`

Related source:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.StreamlineInterop.cs`

Related documentation and work:

- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
- [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Window Creation And Renderer Init](../../../architecture/rendering/window-creation-and-renderer-init.md)
- [Rendering Code Map](../../../architecture/rendering/code-map.md)
- [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md)
- [Vulkan Upscale Bridge](../../../developer-guides/rendering/vulkan-upscale-bridge.md)
- [Vulkan Core Hardening And Device-Loss TODO](vulkan-core-hardening-and-device-loss-todo.md)
- [Vulkan Dynamic Rendering Migration TODO](vulkan-dynamic-rendering-migration-todo.md)
- [Vulkan Primary Command Recording Fast Path TODO](optimization/vulkan-primary-command-recording-fast-path-todo.md)
- [OpenXR Vulkan Submit Fence Wait TODO](vr/openxr-vulkan-submit-fence-wait-todo.md)
- [Vulkan Frame Loop Performance TODO](../COMPLETED/vulkan-frame-loop-performance-todo.md)
- [Backend Renderer Folder Organization TODO](../COMPLETED/backend-renderer-folder-organization-todo.md)
- [Vulkan Parallel Command Chain Refactor Design](../../design/rendering/vulkan-parallel-command-chain-refactor-design.md)
- [OpenXR Monado Framerate Investigation](../../investigations/rendering/openxr-monado-framerate-2026-07-06.md)
- [Editor Origin/Eye Camera Flicker Investigation](../../investigations/rendering/editor-origin-eye-camera-flicker-2026-06-28.md)

## Goal

Replace the monolithic desktop Vulkan `WindowRenderCallback` with a short,
readable frame-lifecycle coordinator backed by responsibility-specific
`VulkanRenderer` partial-class files.

The refactor must make frame ownership and every early-exit obligation explicit
without changing the intended desktop rendering, OpenXR coexistence,
Streamline/DLSS frame-generation, swapchain, resource-retirement, or
device-loss behavior. The steady-state hot path must remain allocation-free.

This is not complete if the current method is merely cut into partial files that
continue to communicate through undocumented renderer-wide mutable fields. The
target shape requires an explicit stack-only frame-attempt context, named phase
outcomes, a single post-acquire recovery policy, and a small shared contract for
OpenXR and device-loss diagnostics.

## Current Shape And Why This Refactor Is Needed

The July 9, 2026 working-tree audit found:

- `VulkanRenderer.FrameLoop.cs` is approximately 1,714 lines.
- `WindowRenderCallback` spans approximately 1,130 lines.
- The callback began as an approximately 82-line wait/acquire/record/submit/
  present loop and grew through inline resize, synchronization, overlay,
  Streamline, upload, recovery, profiling, and diagnostic changes.
- `TryPresentAbortedDirtyFrame`, currently a captured local function inside the
  callback, is approximately 196 lines by itself.
- The callback carries eleven lifecycle/detail `TimeSpan` accumulators plus
  start/stage timestamps and many mutable ownership flags whose legal
  combinations are implicit.
- Step comments have drifted and repeat numbers, so they no longer define a
  reliable lifecycle model.
- `FrameLoop.cs` also owns generic render-state API methods, frame-op API
  methods, and `CreateAPIRenderObject`; those are unrelated to desktop frame
  orchestration.
- OpenXR directly reads `_windowRenderCallbackInProgress`, `currentFrame`,
  `_vkDebugFrameCounter`, `_lastFrameCompletedTimestamp`, and frame-slot timeline
  values. Moving fields without first defining a shared contract would preserve
  or worsen the coupling.
- `currentFrame` can advance while `_windowRenderCallbackInProgress` is still
  set, so OpenXR can currently derive the next slot rather than the slot owned by
  the active callback.
- `_lastFrameCompletedTimestamp` is also written for several resize-skipped
  ticks, while OpenXR interprets a nonzero value as proof that a desktop frame
  completed. Its name and startup-gate semantics have drifted apart.
- OpenXR startup also requires four accepted callback attempts, observes the
  command-buffer dirty timestamp through a 250 ms quiet period with a bounded
  two-second bypass, and waits on pending desktop slot timelines with another
  bounded two-second bypass. Those are part of the shared contract even though
  they live outside `FrameLoop.cs`.
- Device-loss diagnostics directly read the mutable desktop frame slot and
  frame counter when constructing diagnostic context.
- The parameterless `WaitCurrentFrameSlotAndDrainRetiredResources` wrapper has
  no callers, while the main callback duplicates similar logic and additionally
  drains completed texture-upload publications.
- Moving submit/present calls into new helpers changes the `[CallerMemberName]`
  value recorded by the queue gateway unless stable operation labels are passed
  explicitly or the diagnostic change is intentional and tested.
- Several tests inspect literal source paths and method text in
  `VulkanRenderer.FrameLoop.cs`; a file split will otherwise create false
  failures or silently reduce coverage.
- `docs/architecture/rendering/vulkan-renderer.md` and the rendering README
  still describe an approximately 180-line callback and an older frame model.

The callback currently coordinates these responsibilities:

| Current responsibility | Approximate current range | Problem created by inline ownership |
|---|---:|---|
| Reentrancy, device state, frame identity, timing initialization | 583-630 | Shared state and cleanup are spread across nested `try/finally` blocks. |
| Surface, resize, swapchain, resource-generation, and DLSS preflight | 632-804 | Pre-acquire skip policy is mixed with swapchain mutation and logging. |
| Frame-slot wait and completed-resource retirement | 806-835 | Slot ownership is read from mutable renderer state instead of a captured attempt. |
| Native/Streamline image acquire and result policy | 851-959 | Vulkan result classification and ownership establishment are interleaved. |
| Upload cleanup, acquire-semaphore bridge, and aborted-image presentation | 961-1211 | Large captured local functions hide exactly-once ownership rules. |
| Image wait, timing query sample, and dynamic-ring reset | 1213-1234 | Per-image preparation has no named phase boundary. |
| Scene, ImGui, and dynamic-text overlay recording | 1236-1440 | Three command-buffer products and their failure paths share many locals. |
| Dirty-generation validation | 1442-1476 | Stale-recording rejection is coupled to presentation recovery. |
| Submit construction, diagnostics, timeline publication, and upload retirement | 1478-1588 | Stack Vulkan structs, ownership transitions, and diagnostics are inseparable. |
| Collect release and staging trim | 1590-1600 | A critical pacing invariant is easy to move accidentally. |
| Present, result handling, swapchain policy, and slot advance | 1612-1687 | Completion and recovery policy are duplicated with the abort path. |
| Timing publication and reentrancy release | 1689-1710 | Finalization depends on every earlier return reaching the correct `finally`. |

Line numbers are audit anchors, not durable identifiers; the active Vulkan
hardening work may move them before this TODO begins.

## Scope

- Decompose the desktop-window Vulkan frame lifecycle into named phases.
- Add responsibility-specific `VulkanRenderer` partial-class files under
  `Rendering/API/Rendering/Vulkan/Frame/`.
- Introduce an allocation-free frame-attempt context and typed phase outcomes.
- Make acquire-semaphore, swapchain-image, upload-command-buffer, and frame-slot
  ownership explicit.
- Centralize post-acquire abort and recovery behavior.
- Define a narrow, thread-safe desktop-frame activity contract for OpenXR,
  resource retirement, and diagnostics.
- Move unrelated generic renderer APIs and the render-object factory out of
  `FrameLoop.cs`.
- Preserve tracked queue submission/presentation and device-loss gating.
- Replace fragile source-location tests with behavioral or policy tests where
  practical; retain temporary source contracts only as migration guards.
- Update architecture and code-map documentation after paths and ownership
  settle.

## Non-Goals

- Do not rewrite command-buffer recording internals in
  `Commands/VulkanRenderer.CommandBufferRecording.cs`.
- Do not decompose the separate OpenXR eye frame loop as part of this work.
- Do not redesign the render graph, frame-op model, descriptor system,
  synchronization backend, or swapchain implementation.
- Do not change presentation mode, frames-in-flight count, DLSS quality policy,
  overlay ordering, or interactive-resize behavior without a separately
  recorded and validated correctness fix.
- Do not hide Vulkan failures behind OpenGL or CPU fallbacks.
- Do not add a heap-allocated frame object, delegate-based phase pipeline,
  per-frame dependency-injection container, or exception-driven normal control
  flow.
- Do not store pointers to `stackalloc` data or Vulkan submit/present structs in
  the frame-attempt context.
- Do not combine this structural refactor with broad formatting or unrelated
  renderer cleanup.

## Coordination With Active Vulkan Hardening

`VulkanRenderer.FrameLoop.cs`, `VulkanRenderer.Synchronization.cs`,
`VulkanRenderer.OpenXR.cs`, and device-loss diagnostics are active areas in the
Vulkan core-hardening work. This decomposition should use the hardening branch's
queue gateway, device-state machine, first-error preservation, and submission
diagnostic context as inputs rather than creating parallel abstractions.

- Base the decomposition branch on a named integration commit after the active
  queue-serialization/device-state changes are committed or otherwise frozen.
- Record the base commit and dirty-worktree state in the first progress note.
- If hardening must continue concurrently, agree on ownership by method/file:
  hardening changes queue/device behavior; this branch moves already-agreed
  behavior behind phase boundaries.
- Do not copy `SubmitToQueueTracked`, `TryPresentToQueueTracked`, device-loss
  transitions, or fault collection into frame-loop partials.
- Treat the hardening fix that prevents OpenXR batched-eye failure from falling
  through to sequential-eye rendering after confirmed device loss as a landing
  prerequisite, or carry an explicit regression test in this branch.
- Land mechanical moves before semantic cleanup so later hardening changes can
  be merged or cherry-picked with understandable conflicts.
- Treat any discovered ownership bug as a separately identified change with a
  failing test and validation evidence; do not bury it in a file-move commit.

## Behavioral And Synchronization Invariants

### Frame entry and identity

- Reentrant desktop callbacks are rejected before the frame counter advances.
- A frame attempt captures one immutable frame number and one immutable frame
  slot at entry; diagnostics for that attempt never read a later mutable slot.
- The active desktop frame slot is published atomically for OpenXR and
  retirement code and remains the captured slot even if the next slot is chosen
  before final cleanup.
- The reentrancy/activity marker is cleared from an outer `finally` on every
  return and exception.
- Confirm whether `delta` is intentionally unused; retain the override contract
  even if the implementation does not consume it.

### Acquire and image ownership

- No submit or present path runs unless an image was successfully acquired.
- `Success` and `SuboptimalKhr` from acquire are audited as image-acquiring
  results. A suboptimal image must not be abandoned with a signaled binary
  semaphore.
- Once an image is acquired, the acquire semaphore is consumed exactly once by
  the draw submit or by an explicit abort/recovery submit.
- Once an image is acquired, every non-device-loss exit either presents/releases
  it through a valid path or invalidates/recreates the swapchain under a proven
  ownership policy.
- `NotReady` and `Timeout` during interactive resize remain non-blocking skip
  outcomes.
- Image reuse waits on the last timeline value associated with that image before
  per-image state is reset or recorded.

### Recording and command ordering

- The submitted command-buffer order remains texture upload, scene primary,
  ImGui overlay, then dynamic text overlay when each is present.
- ImGui snapshot consumption and final swapchain layout ownership remain
  consistent: exactly one command buffer owns the final transition to
  `PresentSrcKhr`.
- A command buffer dirtied after recording is never submitted as current work.
- A freshly recorded primary with only a pre-existing dirty flag and no
  post-record generation change remains valid: clear the stale flag and submit
  the fresh primary rather than entering an abort/recreate loop.
- Texture-upload command buffers are submitted and retired once, or cancelled
  and freed once; no failure path can do both.
- Detailed render-graph and command recording implementation stays under
  `Commands/`; frame partials only coordinate it.

### Submit, present, and completion

- All queue submissions use `SubmitToQueueTracked` or its final shared gateway.
- All presentation uses `TryPresentToQueueTracked` or its final shared gateway.
- No new Vulkan queue operation begins after terminal device loss.
- Device loss currently clears frame-slot and swapchain-image timeline arrays to
  zero. Lost-device zero values must not be interpreted as successful completion
  that authorizes retirement, timeline publication, recovery waits, or further
  GPU API work.
- Submission diagnostics carry the captured frame id, frame slot, image index,
  frame-op signature/context, resource generation, and descriptor generation.
- Queue history and first-failure diagnostics retain stable operation/caller
  labels across helper extraction, or document and test an intentional label
  migration.
- Frame-slot and swapchain-image timeline values are published only after a
  successful submit.
- Graphics timeline signals remain monotonic using a value greater than both
  the previous graphics value and acquire-bridge value. Binary semaphore value
  entries remain zero in timeline submit metadata.
- Normal draw, abort-present, and consume-only bridge submits publish only the
  slot/image timeline values their ownership transition actually completes.
- Recorded texture uploads are published to the same successful timeline value
  as the frame that contains them.
- `MarkRenderFrameReadyForCollect` remains after successful command submission
  and before the potentially blocking desktop present. The abort-present path
  preserves the equivalent release point.
- Any fallible PCL marker call before a successful draw submit routes through
  normal acquired-image recovery.
- Immediately after a successful queue submit, commit acquire consumption,
  timeline/upload ownership, and submitted state before any later fallible PCL
  marker, diagnostic, staging trim, or presentation-preparation work.
- Failures after successful submit must not strand the submitted image by
  preventing present. Preserve the primary failure and defer propagation until
  required ownership/presentation bookkeeping is settled.
- After `QueuePresent` returns, record its result and complete submitted-image/
  slot bookkeeping before a fallible `PresentEnd` marker or diagnostic can
  throw.
- Frame-slot advancement happens in one named completion policy, not in
  unrelated catch blocks.
- Present result handling preserves distinct `Success`, `SuboptimalKhr`,
  `ErrorOutOfDateKhr`, `ErrorSurfaceLostKhr`, `ErrorDeviceLost`, and unexpected
  error behavior.

### Resize, retirement, and OpenXR coexistence

- Interactive resize never introduces an unbounded frame-slot or acquire wait.
- Zero-sized/minimized surfaces do not acquire or submit swapchain work.
- Skipped resize frames drain or retain frame ops according to one documented
  policy; queued work must not silently leak into an incompatible generation.
- Retired resources are destroyed only after the owning frame-slot timeline has
  completed.
- OpenXR resource draining never drains the atomically published active desktop
  frame slot.
- An atomic active-slot observation is not by itself a retirement exclusion
  mechanism. Prove desktop entry and OpenXR retirement drains are serialized on
  one render thread, or add a shared activity/drain lease held through the
  check-and-destroy interval with a tested lock order.
- A pending desktop slot causes that iteration to `continue`; it does not stop
  completed other slots from draining.
- OpenXR eye frame-data slots remain in their separate index domain after the
  desktop slots and are never mistaken for the two desktop in-flight slots.
- OpenXR runtime-session startup preserves the characterized gates for accepted
  desktop attempts, observed tick/completion timestamp, active activity,
  command-buffer dirty quiet time, and pending desktop timelines until their
  meanings are intentionally renamed or split.
- The 250 ms dirty quiet period and bounded two-second dirty/pending-timeline
  bypasses do not become unbounded waits during the refactor.
- The OpenXR exclusive runtime graphics transition retains its existing
  queue/device exclusion and lock order; splitting desktop phases must not turn
  that transition into separately locked calls with an interleaving window.
- Treat OpenXR's current session-start readiness check as advisory unless a real
  shared lifecycle gate is added. Do not imply that a check-then-act flag read
  provides mutual exclusion with a newly entering desktop frame.
- Swapchain recreation preserves Streamline proxy/native swapchain mode and DLSS
  feature composition.

### Diagnostics, telemetry, and performance

- Existing profiler scope names remain stable during the mechanical refactor so
  before/after captures are comparable.
- Lifecycle and detailed timing publication runs for every entered attempt,
  including early returns and exceptions.
- Timing publication and debug invariant checks in `finally` must not mask the
  original record, overlay, submit, present, or device-loss exception and must
  not start recovery after terminal loss.
- Success-path diagnostic strings are not formatted unless their diagnostic is
  enabled and will be emitted.
- No new steady-state managed allocations, LINQ, boxing, captured closures, or
  class-enumerator `foreach` are introduced.
- `DesktopFrameAttempt` is always passed by `ref`; it is never copied through a
  delegate, interface, collection, task, or async boundary.
- Vulkan pointers and `stackalloc` spans remain inside the helper that consumes
  them before returning.

## Target Orchestration

The intended control flow is:

```text
Try enter and capture immutable frame identity
  -> Preflight surface/resources/swapchain/Streamline mode
  -> Wait frame slot and retire completed resources
  -> Acquire swapchain image
  -> Wait image reuse and prepare per-image state
  -> Record scene and overlays
  -> Validate recording generation
  -> Submit and publish timeline/upload ownership
  -> Release collect-visible producer
  -> Present and classify result
  -> Complete/advance frame slot
  -> Publish timing and release activity marker

Any failure after Acquire
  -> DesktopFrameRecovery resolves semaphore/image/upload ownership
  -> Complete, recreate, throw, or stop for device loss as the typed outcome requires
  -> Publish timing and release activity marker
```

`WindowRenderCallback` should show this flow directly. It should not contain
Vulkan struct construction, swapchain-size arithmetic, overlay recording,
result-switch ladders, nested recovery functions, or detailed diagnostic
formatting.

A soft target is 60-120 lines including the outer guard and `try/finally`.
Responsibility clarity and explicit ownership are the acceptance criteria; line
count is a regression signal, not a reason to compress logic.

## Target Partial-File Map

Keep the coordinator at its current durable path and use explicit sibling
partials. Avoid `Helpers`, `Misc`, or `Utilities` files.

| Target file | Owns | Must not own |
|---|---|---|
| `Frame/VulkanRenderer.FrameLoop.cs` | `WindowRenderCallback` and the readable phase coordinator only | Vulkan submit/present structs, resize arithmetic, recording details, recovery implementation |
| `Frame/VulkanRenderer.FrameLoop.State.cs` | Cross-phase frame identity/slot/activity and explicit attempt/submitted/presented readiness timestamps, `DesktopFrameAttempt`, phase/flow/recovery/reason enums, timing accumulator | Phase-local resize/acquire policy fields, queue calls, window queries, logging policy |
| `Frame/VulkanRenderer.FrameLoop.Preflight.cs` | Surface snapshot, viewport resource blockers, Streamline-mode compatibility decision, pre-acquire skip cleanup | Swapchain recreation implementation, image acquire, command recording, submit/present |
| `Frame/VulkanRenderer.FrameLoop.SwapchainPolicy.cs` | Resize settle/debounce state, extent mismatch policy, recreate scheduling/result, surface-loss policy | Low-level swapchain creation/destruction, command recording |
| `Frame/VulkanRenderer.FrameLoop.FrameSlots.cs` | Captured-slot wait, completed-resource drain coordination, image-reuse wait, timing query sample, dynamic-ring reset | Swapchain acquire dispatch, scene recording, submit/present |
| `Frame/VulkanRenderer.FrameLoop.Acquire.cs` | Native/Streamline acquire dispatch, acquire result classification, ownership establishment, not-ready/timeout policy | Frame-slot retirement, scene/overlay recording, post-acquire abort presentation |
| `Frame/VulkanRenderer.FrameLoop.Recording.cs` | ImGui snapshot, scene-primary coordination, dynamic-text overlay coordination, dirty-generation validation, recording allocation measurement | Detailed command recording, queue submit/present, swapchain recreation implementation |
| `Frame/VulkanRenderer.FrameLoop.Recovery.cs` | Upload cancellation/free, acquire bridge, abort transition command buffer/submit, post-acquire failure cleanup, consumption of shared present outcomes | `PresentInfoKHR`, tracked presentation dispatch/result classification, normal frame submission, generic device-loss collection |
| `Frame/VulkanRenderer.FrameLoop.Submission.cs` | Submit command ordering, local Vulkan submit structs, diagnostic context, tracked submit, timeline publication, upload retirement, collect release, staging trim | Presentation result policy or recording internals |
| `Frame/VulkanRenderer.FrameLoop.Presentation.cs` | Local present structs, tracked native/Streamline present, PCL markers around present, result classification, swapchain response, slot completion | Scene recording or abort command construction |
| `Frame/VulkanRenderer.FrameLoop.Telemetry.cs` | Frame timing publication, frame-gap diagnostics, overlay output telemetry, gated lifecycle logs | Lifecycle ownership or OpenXR readiness timestamp mutation |
| `Commands/VulkanRenderer.FrameOpApi.cs` | `MemoryBarrier` and framebuffer-publication frame-op API | Desktop frame lifecycle |
| `Commands/VulkanRenderer.RenderStateApi.cs` | Color mask/clear color plus `CropRenderArea`, `SetRenderArea`, `ClearRenderArea`, and indexed viewport/scissor API | Desktop frame lifecycle |
| `BackendObjects/VulkanRenderer.RenderObjectFactory.cs` | `CreateAPIRenderObject` dispatch | Frame state or command submission |
| `Features/Upscaling/VulkanRenderer.StreamlineFrameLifecycle.cs` | Streamline frame-generation PCL marker dispatch used by submit/present phases | Generic frame telemetry, queue submission, presentation policy |

Existing `Swapchain`, `SyncObjects`, `Synchronization`, `ResourceRetirement`,
`FrameTiming`, and device-loss partials remain authoritative for their current
subsystems. Frame-loop partials call them; they do not duplicate them.

If the complete frame-loop sibling set makes the `Frame/` directory harder to scan,
move the complete set, including the coordinator, into a
`Frame/DesktopFrameLoop/` subfolder in one mechanical commit. Do not split the
set across two organizational schemes.

## Frame-Attempt State And Outcome Contract

### Stack-only attempt context

Add a nested `ref struct DesktopFrameAttempt` or equivalently named type. It
should group state that is currently represented by callback locals:

- immutable identity: frame number, captured frame slot, start timestamp;
- preflight snapshot: interactive-resize flag, live framebuffer/window/surface
  dimensions, swapchain dimensions;
- acquire ownership: image index, acquire semaphore, present semaphore, last
  acquire result, and one typed acquire/image ownership state;
- recording ownership: scene, upload, ImGui, and dynamic-text command buffers;
- upload command pool and one typed recorded/submitted/retired/cancelled state;
- swapchain layout after scene recording;
- command-buffer dirty generation and whether the scene primary was freshly
  recorded;
- graphics signal value plus typed submitted/presented/collect-released/slot-
  completion state;
- frame phase, flow disposition, recovery obligation, and reason enum;
- accumulated lifecycle timings.

Do not put pointers, `Span<T>` values backed by a helper's stack frame,
`SubmitInfo`, `PresentInfoKHR`, `TimelineSemaphoreSubmitInfo`, delegates, tasks,
or disposable renderer resources in this context.

### Typed state

Use named types rather than combinations of booleans and strings:

- `EDesktopFramePhase`: `Entered`, `PreflightComplete`, `SlotReady`,
  `ImageAcquired`, `ImageReady`, `Recorded`, `Validated`, `Submitted`,
  `Presented`, `Recovered`, `Finalized`.
- `EDesktopFrameFlow`: a small orthogonal flow result such as `Continue`,
  `Stop`, or `Completed`.
- `EDesktopFrameRecoveryAction`: `None`, consume/abort-present acquired work,
  recreate swapchain, recreate surface/restart renderer, or terminal device-loss
  cleanup. Recovery action is not overloaded into flow disposition.
- Resource-specific ownership states for the acquire semaphore/swapchain image
  and texture-upload command buffer. Do not represent legal ownership through
  independent `Acquired`, `Consumed`, `Submitted`, and `Cancelled` booleans.
- `EDesktopFrameReason`: stable reasons for resize, invalid resources, busy
  slot, acquire status, recording deferral/failure, dirty recording, submit
  failure, and present status.
- Small readonly phase-specific results such as acquire, submit, and present
  outcomes. Each carries only its relevant Vulkan `Result`, flow, reason, and
  recovery obligation; avoid one ambiguous renderer-wide `Result` field and
  avoid formatted reason strings on success paths.

Unexpected record/overlay exceptions and device-loss exceptions must propagate
from their detection site with their original stack and first-failure context.
Do not replace an exception with a `Throw` enum that cannot faithfully rethrow
it later.

Names may change during implementation, but the distinct concepts must remain.
Illegal phase transitions should fail in debug/test builds before a Vulkan call
is made.

### Atomic desktop activity contract

Replace direct external reads of `_windowRenderCallbackInProgress` plus the
mutable `currentFrame` with one coherent API. The preferred representation is
an atomically published active-slot value with a negative inactive sentinel:

- entry atomically claims/publishes the captured slot;
- OpenXR asks for the active captured slot through a method or property;
- completion atomically returns to the inactive sentinel;
- reentrancy testing uses the same state, avoiding two markers that can diverge.

If a separate reentrancy gate remains necessary, define and test the ordering
between gate acquisition, active-slot publication, slot advancement, and gate
release. OpenXR and diagnostics must never reconstruct an activity snapshot by
reading several unrelated mutable fields.

The atomic snapshot solves coherent observation, not mutual exclusion. Before
using it to authorize resource destruction, prove all desktop entry and OpenXR
drain calls are serialized by render-thread ownership. If they can race, add an
allocation-free activity/drain lease or equivalent lifecycle gate that covers
the entire retirement check-and-destroy interval and has a documented lock order
with the queue gateway and OpenXR runtime graphics transition.

### Required outcome matrix

The implementation must turn the following into table-driven policy tests:

| Event | Image considered acquired? | Required ownership action | Slot/completion policy |
|---|---:|---|---|
| Reentrant callback | No | No GPU work; log throttled skip | Do not increment frame id or slot |
| Zero/minimized surface | No | Skip acquire; drain/retain frame ops by preflight policy | Slot unchanged |
| Resize/resource mismatch | No | Set or service recreate request; skip incompatible work | Slot unchanged |
| Interactive frame slot still busy | No | Do not block; skip and account timing | Slot unchanged |
| Acquire `NotReady`/`Timeout` | No | Skip; update consecutive status policy | Slot unchanged |
| Acquire `ErrorOutOfDateKhr` | No | Schedule recreate | Slot unchanged |
| Acquire `ErrorSurfaceLostKhr` | No | Invoke explicit surface-loss policy: recreate `SurfaceKHR` plus swapchain if implemented, otherwise fail/restart visibly | Slot unchanged |
| Acquire reports device loss | Indeterminate/terminal | Preserve first failure; no recovery GPU work | Terminal device-loss completion |
| Acquire returns unexpected/OOM/validation failure | No | Preserve exact result and throw/fail visibly; do not pretend an image was acquired | Slot unchanged |
| Acquire `Success` | Yes | Enter post-acquire ownership state | Complete through submit/present or recovery |
| Acquire `SuboptimalKhr` | Yes | Resolve acquired image/semaphore, then schedule recreate | Explicitly tested; never abandon semaphore |
| Image preparation fails after acquire | Yes | Recover acquired semaphore/image and any per-image state before propagating | Named recovered completion |
| Recording deferred or throws | Yes | Cancel upload; recover acquired semaphore/image; recreate or throw by policy | Advance only through named recovery completion |
| Overlay recording throws | Yes | Same exactly-once recovery as scene recording failure | Advance only through named recovery completion |
| Fresh primary has a pre-existing dirty flag but generation did not change | Yes | Clear the stale flag and continue with the freshly recorded primary | Continue to submit |
| Cached primary is dirty or generation changes after record | Yes | Do not submit stale work; present/release skipped image or recreate safely | Named recovered completion |
| Submit succeeds | Yes; acquire consumed | Publish timeline and uploads; release collect; present | Advance through successful completion |
| Submit fails, device healthy | Yes; acquire remains unconsumed | Cancel unsubmitted uploads; consume/abort-present acquired work under tested recovery policy before throwing | Recreate/throw explicitly |
| Submit reports device loss | Indeterminate | Stop new GPU work; preserve first failure; no abort submit/present | Terminal device-loss completion |
| Fallible marker/trim/diagnostic fails after successful submit but before present | Submitted | Commit ownership first, still present/settle the image, then propagate the preserved failure | Advance through submitted-frame completion |
| Present `Success` | Submitted | Record presented image and completion | Advance slot once |
| Present `SuboptimalKhr` | Submitted | Record presentation; schedule recreate | Advance slot once |
| Present `ErrorOutOfDateKhr` | Submitted | Schedule recreate | Advance according to tested submitted-frame policy |
| Present `ErrorSurfaceLostKhr` | Submitted | Invoke explicit surface-loss policy; do not loop swapchain-only recreation indefinitely | Advance according to tested submitted-frame policy |
| Present reports device loss | Submitted/indeterminate | Stop new GPU work and preserve first failure | Terminal device-loss completion |
| Present returns an unexpected result | Submitted/indeterminate | Preserve result and submit bookkeeping; fail/restart visibly under tested policy | Never silently mark success |
| Fallible marker/diagnostic fails after present returns | Present result known | Commit result/image/slot bookkeeping before propagating the preserved failure | Complete once |

The policy table must be derived from Vulkan ownership rules and verified by
tests; do not preserve an unsafe early return solely because the old method had
one.

## Phase 0 - Branch, Integration Base, And Baseline

- [ ] **0.1** Create dedicated branch
  `rendering-vulkan-frame-loop-decomposition` from the agreed Vulkan-hardening
  integration commit.
- [ ] **0.2** Record the base commit, branch, dirty-worktree state, GPU/driver,
  Vulkan SDK/layer versions, and OpenXR runtime in a progress note under
  `docs/work/progress/rendering/`.
- [ ] **0.3** Inventory every `return`, `throw`, catch/finally block, queue call,
  swapchain recreate, frame-slot advance, timestamp write, timeline write,
  acquire-semaphore transition, upload-buffer transition, and collect-release
  call in the current callback.
- [ ] **0.4** Convert that inventory into a checked-in current-behavior matrix
  and compare it with the required outcome matrix above. Mark suspected
  correctness defects instead of silently treating them as desired behavior.
- [ ] **0.5** Record baseline focused test results for
  `VulkanP1ValidationTests`, `OpenXrTimingPipelineContractTests`, and
  `WindowOwnershipContractTests`, including pre-existing failures.
- [ ] **0.6** Build the editor and capture a normal Vulkan Unit Testing World
  frame, ImGui overlay, dynamic text overlay, rapid resize, minimize/restore,
  and shutdown log baseline.
- [ ] **0.7** Run `Tools/Measure-VulkanFrameLoop.ps1` and record frame-loop
  p50/p95/p99, command-recording allocations, acquire/present timing, and
  resource-retirement backlog.
- [ ] **0.8** Identify all source-contract tests that read
  `VulkanRenderer.FrameLoop.cs` and assign each assertion a behavioral test,
  pure-policy test, temporary multi-file source test, or deletion rationale.

Acceptance criteria:

- [ ] The branch starts from an unambiguous integration base.
- [ ] Every current early exit and ownership side effect appears in the audit
  matrix.
- [ ] Baseline test, runtime, visual, log, allocation, and timing evidence is
  durable enough for a before/after comparison.
- [ ] Suspected existing bugs are named and separated from mechanical moves.

## Phase 1 - Characterization Tests And Policy Seams

- [ ] **1.1** Add table-driven tests for acquire result classification,
  including the ownership implication of `SuboptimalKhr`.
- [ ] **1.2** Add table-driven tests for present result classification and
  recreate/surface-lost/device-lost reactions.
- [ ] **1.3** Add tests for legal frame-phase transitions and the cleanup
  obligation associated with each disposition.
- [ ] **1.4** Add tests proving an acquired semaphore/image cannot reach
  finalization as unresolved and cannot be consumed twice.
- [ ] **1.5** Add tests for upload command-buffer states: absent, recorded,
  submitted/deferred-free, cancelled/freed, and device-lost abandonment.
- [ ] **1.6** Add a concurrency test for atomic desktop activity publication:
  OpenXR observes either inactive or the captured active slot, never a later
  mutable slot.
- [ ] **1.7** Add or expose deterministic fault-injection seams at acquire,
  image preparation, record, overlay record, submit, post-submit auxiliary work,
  present, and post-present auxiliary work without using per-frame delegates in
  production.
- [ ] **1.8** Update temporary source-contract test readers so splitting the
  file does not accidentally make assertions inspect an empty or unrelated
  source string.
- [ ] **1.9** Prove whether desktop callback entry and OpenXR retirement drains
  are render-thread serialized. If not, add a concurrency test that fails until
  one activity/drain lease excludes entry through the full check-and-destroy
  interval.
- [ ] **1.10** Characterize OpenXR startup gates for accepted-attempt count,
  observed-tick/completion timestamp, active activity, the 250 ms dirty quiet
  period/two-second bypass, and pending-timeline two-second bypass.
- [ ] **1.11** Choose and test the `ErrorSurfaceLostKhr` policy. The existing
  `RecreateSwapChain` path does not recreate `SurfaceKHR`; either implement a
  platform-safe surface-plus-swapchain rebuild or fail/restart the renderer
  visibly without an endless swapchain-only retry loop.

Acceptance criteria:

- [ ] Result and ownership policy can be tested without a physical Vulkan
  device.
- [ ] The tests fail for unresolved or double-consumed acquired work.
- [ ] Fault injection does not allocate or add virtual/interface dispatch to the
  normal frame hot path.
- [ ] Existing source contracts retain coverage until behavioral replacements
  exist.
- [ ] The retirement exclusion mechanism and bounded OpenXR startup waits have
  concurrency/policy tests before raw shared fields move.
- [ ] Every architecture-affecting item in "Open Questions To Resolve In Phase 0
  Or 1" has a recorded decision before Phase 2 starts.

## Phase 2 - Establish File Ownership And Shared Frame State

- [ ] **2.1** Move frame-op API methods to
  `Commands/VulkanRenderer.FrameOpApi.cs` without changing bodies.
- [ ] **2.2** Move render-state API methods to
  `Commands/VulkanRenderer.RenderStateApi.cs` without changing bodies.
- [ ] **2.3** Move `CreateAPIRenderObject` to
  `BackendObjects/VulkanRenderer.RenderObjectFactory.cs` without changing
  dispatch.
- [ ] **2.4** Add `VulkanRenderer.FrameLoop.State.cs` and move only cross-phase
  identity/slot/activity fields into it. Move resize constants/state with
  `SwapchainPolicy` and acquire timeouts/counters with `Acquire`. Keep any
  OpenXR readiness/completion timestamp in State/completion policy; move only
  diagnostic-only timing data to Telemetry.
- [ ] **2.5** Replace ambiguous `currentFrame` use with a clearly named desktop
  frame-slot field or accessor. Audit every reference in resource retirement,
  command state, timing, sync-object creation, OpenXR, and device-loss
  diagnostics before renaming.
- [ ] **2.6** Introduce the atomic desktop activity API and migrate OpenXR and
  diagnostics away from direct gate-plus-slot reads.
- [ ] **2.7** Make retirement and diagnostic calls accept the captured frame
  slot/frame id where they describe a particular attempt rather than rereading
  mutable renderer state.
- [ ] **2.8** Update `Frame/README.md` with the target partial ownership map and
  reaffirm that command recording details remain under `Commands/`.
- [ ] **2.9** If Phase 1 does not prove render-thread serialization, implement a
  shared activity/drain lease and document its lock order with queue operations
  and OpenXR runtime transitions.
- [ ] **2.10** Replace OpenXR's direct frame-counter/timestamp reads with named
  accessors whose attempt/tick/submitted/presented semantics are explicit while
  preserving the characterized bounded startup policy.
- [ ] **2.11** Name and preserve the distinct desktop in-flight-slot and OpenXR
  eye frame-data-slot index domains; do not expose one ambiguous `FrameSlot`
  accessor for both.

Acceptance criteria:

- [ ] `FrameLoop.cs` contains no generic render-state API, frame-op API, or
  render-object factory code.
- [ ] OpenXR can query desktop activity without combining unsynchronized field
  reads.
- [ ] Diagnostics for a frame attempt use its captured slot and frame id.
- [ ] Mechanical moves compile and pass focused tests before phase extraction
  starts.
- [ ] OpenXR retirement entry cannot race a new desktop attempt into destroying
  that attempt's active slot resources.

## Phase 3 - Introduce The Stack-Only Attempt And Coordinator Skeleton

- [ ] **3.1** Add `DesktopFrameAttempt`, timing accumulator, phase,
  disposition, and reason types to `VulkanRenderer.FrameLoop.State.cs`.
- [ ] **3.2** Capture frame number, frame slot, and start timestamp once when the
  callback enters; stop reading the mutable slot for attempt-specific logs.
- [ ] **3.3** Pass the attempt by `ref` through phase methods and add a debug/test
  transition validator.
- [ ] **3.4** Add one outer `try/finally` that always publishes timings and
  releases desktop activity.
- [ ] **3.5** Move the existing body temporarily behind an
  `ExecuteDesktopFrame(ref DesktopFrameAttempt)` seam before extracting phases;
  keep this commit behavior-only-neutral and easy to review.
- [ ] **3.6** Prove through allocation measurement and generated-code inspection
  where needed that the attempt and phase results remain stack-only.
- [ ] **3.7** Prohibit local functions that capture the attempt. Use explicit
  private partial-class methods with `ref` parameters.

Acceptance criteria:

- [ ] One context owns attempt-local mutable state.
- [ ] No context copy, boxing, closure, task, or heap allocation occurs per
  frame.
- [ ] Finalization runs for every entered attempt.
- [ ] The temporary coordinator seam preserves baseline behavior before phase
  movement.

## Phase 4 - Extract Preflight And Swapchain Policy

- [ ] **4.1** Create `VulkanRenderer.FrameLoop.Preflight.cs`.
- [ ] **4.2** Create `VulkanRenderer.FrameLoop.SwapchainPolicy.cs` and keep
  low-level creation/destruction in the existing `VulkanRenderer.Swapchain.cs`.
- [ ] **4.3** Capture live framebuffer, window, surface, and swapchain sizes once
  into the attempt's preflight snapshot.
- [ ] **4.4** Move resize-pending tracking, settle/debounce policy, immediate
  recreate scheduling, and mismatched-extent policy into named methods.
- [ ] **4.5** Replace misleading `RecreateSwapchainImmediately` naming with a
  result-bearing API such as `TryRecreateSwapchainNow`; its current failure path
  schedules deferred recreation and therefore is not unconditionally immediate.
- [ ] **4.6** Move viewport resource-generation blocker checks and replace
  success-path formatted reasons with enums plus on-demand diagnostics.
- [ ] **4.7** Move zero-surface, incompatible-generation, and skipped-resize
  frame-op cleanup into explicit pre-acquire dispositions.
- [ ] **4.8** Move Streamline/DLSS swapchain-mode compatibility preflight into
  this phase while leaving actual swapchain creation in `Swapchain.cs`.
- [ ] **4.9** Test stable-size, live mismatch, active interactive resize,
  unsettled resize, zero surface, missing resource generation, compatible
  interactive display mismatch, and DLSS mode-change cases.

Acceptance criteria:

- [ ] Preflight never acquires an image or records/submits commands.
- [ ] Every pre-acquire skip has a named reason and tested frame-op policy.
- [ ] Interactive resize remains non-blocking.
- [ ] Swapchain implementation is not duplicated in the preflight partial.

## Phase 5 - Extract Frame Slots, Retirement, Acquire, And Image Preparation

- [ ] **5.1** Create `VulkanRenderer.FrameLoop.FrameSlots.cs` and
  `VulkanRenderer.FrameLoop.Acquire.cs`.
- [ ] **5.2** Move current-slot wait and post-completion resource drains behind a
  method that takes the captured slot explicitly.
- [ ] **5.3** Remove the unused parameterless
  `WaitCurrentFrameSlotAndDrainRetiredResources` wrapper and unify its remaining
  logic with the main path without losing
  `DrainCompletedRecordedTextureUploadPublications`.
- [ ] **5.4** Preserve the interactive-resize busy-slot skip rather than turning
  it into a blocking timeline wait.
- [ ] **5.5** Isolate native versus Streamline acquire dispatch from acquire
  result classification.
- [ ] **5.6** Set the attempt's image/semaphore ownership immediately when the
  Vulkan result indicates acquisition, before any later return can occur.
- [ ] **5.7** Correct the `SuboptimalKhr` acquire path as a successful
  image-acquiring result, cite the Vulkan reference in code/tests, and add an
  exactly-once cleanup test.
- [ ] **5.8** Move the consecutive `NotReady`/`Timeout` policy into a named
  method with tests for reset and recreate thresholds.
- [ ] **5.9** Move swapchain-image timeline wait, timing-query sample, and
  dynamic-uniform-ring reset into an image-preparation method.
- [ ] **5.10** Preserve OpenXR completed-slot draining semantics: a pending
  desktop slot uses `continue` so other completed slots still drain, and eye
  frame-data slot indices remain outside the desktop-slot domain.

Acceptance criteria:

- [ ] Acquire dispatch, result policy, and ownership establishment are distinct
  reviewable operations.
- [ ] No acquired image/semaphore can exit the phase without its ownership and
  required terminal action recorded in the attempt.
- [ ] Native and Streamline acquire paths produce the same typed outcomes.
- [ ] Resource retirement happens only after the captured slot completes.

## Phase 6 - Extract Scene And Overlay Recording

- [ ] **6.1** Create `VulkanRenderer.FrameLoop.Recording.cs`.
- [ ] **6.2** Move ImGui snapshot capture ahead of scene recording and preserve
  the overlay-owned final-layout decision.
- [ ] **6.3** Wrap `EnsureCommandBufferRecorded` without moving its detailed
  implementation out of `Commands/`.
- [ ] **6.4** Initialize upload command-buffer/pool ownership in the attempt
  before recording and transfer outputs directly into attempt-visible state (or
  through a result/finally visible on exception). Do not wait for a normal
  return before recovery can see partially produced upload work.
- [ ] **6.5** Move ImGui overlay and dynamic-text overlay coordination into
  separate named methods with a shared recording-failure outcome.
- [ ] **6.6** Move post-record dirty-flag/generation validation into a named
  validation phase that cannot submit stale work.
- [ ] **6.7** Preserve `RecordCommandBuffer` allocation accounting and overlay
  frame-output telemetry.
- [ ] **6.8** Test no-overlay, ImGui-only, dynamic-text-only where valid, both
  overlays, recording deferral, scene-record failure, each overlay failure, and
  dirty-after-record cases.
- [ ] **6.9** Test the current fresh-primary exception explicitly: a pre-existing
  dirty flag with no post-record generation change is cleared and the freshly
  recorded primary continues, while a dirty cached primary or generation change
  aborts.
- [ ] **6.10** Fault after an upload command buffer is recorded but before scene
  recording returns successfully; prove recovery can still cancel/free it once.

Acceptance criteria:

- [ ] Recording phase performs no queue submit or present.
- [ ] Command-buffer ordering and swapchain final-layout ownership match the
  baseline.
- [ ] Every recording failure returns one typed recovery obligation.
- [ ] Detailed command recording remains in the `Commands/` subsystem.

## Phase 7 - Extract And Unify Post-Acquire Recovery

- [ ] **7.1** Create `VulkanRenderer.FrameLoop.Recovery.cs`.
- [ ] **7.2** Replace `ReleaseUnsubmittedTextureUploadCommandBuffer` local
  function with an attempt-based exactly-once cleanup method.
- [ ] **7.3** Replace `ConsumeAcquireSemaphoreForAbortedFrame` local function
  with a tracked bridge-submit method that records ownership transition only on
  success.
- [ ] **7.4** Split `TryPresentAbortedDirtyFrame` into focused recovery
  operations: prepare the optional present-layout transition, submit abort work,
  publish timeline ownership, release collect, then call the shared Presentation
  primitive and consume its typed result. Keep `PresentInfoKHR`, tracked
  dispatch, Streamline/PCL wrapping, and result classification exclusively in
  `VulkanRenderer.FrameLoop.Presentation.cs`.
- [ ] **7.5** Remove all captured local recovery functions from
  `WindowRenderCallback` and its phase methods.
- [ ] **7.6** Route scene failure, overlay failure, recording deferral, dirty
  recording, and healthy-device submit failure through the same recovery state
  machine.
- [ ] **7.7** Ensure device-loss outcomes bypass all new submit/present/recreate
  calls and preserve the first failing API/context.
- [ ] **7.8** Add deterministic fault tests at every recovery operation,
  including abort command-buffer begin/end, bridge submit, skipped present, and
  swapchain recreate.
- [ ] **7.9** Preserve and test the abort-layout rule: clear conservative tracked
  layouts; transition a never-presented acquired image from `Undefined` to
  `PresentSrcKhr`; treat an already-presented acquired image as remaining in
  `PresentSrcKhr` unless tracking proves otherwise.
- [ ] **7.10** Fault after device loss clears timeline arrays and prove
  finalization/OpenXR retirement does not treat zero as completed work, destroy
  lost-device resources through a normal retirement path, wait, or submit
  recovery work.

Acceptance criteria:

- [ ] There is one auditable post-acquire recovery policy.
- [ ] Acquire semaphore, image, upload buffer, and abort command buffer each
  have exactly one terminal ownership transition.
- [ ] Recovery cannot issue GPU work after device loss.
- [ ] Lost-device timeline zeroing cannot authorize normal completion or
  retirement.
- [ ] The former approximately 196-line local recovery function no longer
  exists in any form as one monolithic replacement method.

## Phase 8 - Extract Submission, Presentation, And Slot Completion

- [ ] **8.1** Create `VulkanRenderer.FrameLoop.Submission.cs` and
  `VulkanRenderer.FrameLoop.Presentation.cs`.
- [ ] **8.2** Build `TimelineSemaphoreSubmitInfo`, `SubmitInfo`, semaphore arrays,
  stage arrays, and command-buffer arrays on the submission helper's stack and
  consume them before returning.
- [ ] **8.3** Preserve upload/scene/ImGui/dynamic-text submission order. Replace
  the implicit four-entry `stackalloc` assumption with a named capacity and
  checked append or a test/proof that count never exceeds capacity.
- [ ] **8.4** Build diagnostic metadata from the captured attempt and route the
  submit through the shared tracked queue gateway.
- [ ] **8.5** Pass stable explicit queue-operation labels where helper extraction
  would otherwise change `[CallerMemberName]` provenance used by queue history
  and first-failure diagnostics.
- [ ] **8.6** Publish acquire consumption, monotonic global/slot/image timeline
  values, recorded uploads, and submitted state immediately after submit
  succeeds and before any later fallible marker/diagnostic/trim work. Preserve
  zero timeline values for binary semaphore entries.
- [ ] **8.7** Preserve collect release before potentially blocking presentation.
  Ensure a staging-trim or post-submit marker failure cannot prevent the
  submitted image from reaching presentation/terminal bookkeeping.
- [ ] **8.8** Build and consume `PresentInfoKHR` entirely inside the presentation
  helper and route native/Streamline presentation through the tracked gateway.
- [ ] **8.9** Move the shared PCL marker wrapper to
  `Features/Upscaling/VulkanRenderer.StreamlineFrameLifecycle.cs` and preserve
  marker ordering around submit/present, including visible marker failures.
- [ ] **8.10** Centralize present result policy and frame-slot advancement in one
  completion method used by normal and recovered presentation.
- [ ] **8.11** Test submit/present success and every expected Vulkan error with
  fault injection, including first-error device-loss preservation.
- [ ] **8.12** Test healthy submit failure as acquired/unconsumed ownership:
  cancel unsubmitted upload work, resolve the acquire semaphore/image through
  the chosen recovery policy, then propagate the original failure.
- [ ] **8.13** Inject failures at `RenderSubmitStart`, `RenderSubmitEnd`, staging
  trim, `PresentStart`, and `PresentEnd`. Prove pre-submit failures recover
  acquired work, post-submit failures still settle/present, and post-present
  failures cannot suppress result/slot bookkeeping.
- [ ] **8.14** Test monotonic signal generation and the distinct global/slot/
  image publication obligations of normal draw, abort-present, and consume-only
  bridge submits.

Acceptance criteria:

- [ ] Submission and presentation have separate ownership boundaries.
- [ ] No stack pointer or Vulkan submit/present struct escapes its helper.
- [ ] Timeline publication, upload retirement, collect release, and slot advance
  occur once and in the required order.
- [ ] Auxiliary marker/diagnostic/trim failures preserve their visibility
  without stranding acquired or submitted presentation ownership.
- [ ] All queue operations use the hardened shared gateway.

## Phase 9 - Extract Telemetry And Finish The Coordinator

- [ ] **9.1** Create `VulkanRenderer.FrameLoop.Telemetry.cs`.
- [ ] **9.2** Consolidate lifecycle durations into the attempt timing structure
  while preserving existing profiler scope names and stats APIs.
- [ ] **9.3** Move frame-gap, size/acquire/submit/present, resize-skip, and overlay
  diagnostics into gated methods that receive immutable attempt data.
- [ ] **9.4** Ensure the timing `finally` records every entered attempt,
  including skip, recovery, exception, and device-loss outcomes, without
  throwing over or replacing the primary exception.
- [ ] **9.5** Reduce `WindowRenderCallback` to the phase coordinator and keep it
  within the 60-120-line soft target.
- [ ] **9.6** Remove obsolete numbered comments, duplicated result ladders,
  superseded fields, and the temporary `ExecuteDesktopFrame` migration seam.
- [ ] **9.7** Correct stale XML/comments discovered during extraction, including
  the claim that reentrancy throws and the recording-failure comment that says
  the acquire bridge already consumed the semaphore before the catch invokes it.
- [ ] **9.8** Run an allocation audit over every new phase method and eliminate
  closures, LINQ, boxing, unconditional interpolation, and accidental context
  copies.
- [ ] **9.9** Run `dotnet format ... --include <touched-files>` only if needed,
  or format the touched files through the narrow project command. Do not run an
  unscoped solution-wide formatter or create repository-wide churn.
- [ ] **9.10** Keep attempt/submitted/presented readiness timestamp publication
  in State/completion policy with explicit thread visibility. Telemetry may read
  those timestamps but must not define OpenXR synchronization semantics.

Acceptance criteria:

- [ ] The coordinator reads as the target lifecycle without opening another
  file to understand phase order.
- [ ] Each partial has one declared responsibility and no catch-all helper file
  exists.
- [ ] No method replaces the old monolith with another several-hundred-line
  phase.
- [ ] Profiler and stats comparisons remain possible across the refactor.

## Phase 10 - Automated Validation

- [ ] **10.1** Add focused behavioral tests in a new
  `VulkanDesktopFrameLoopTests` fixture or equivalently named fixture.
- [ ] **10.2** Update `VulkanP1ValidationTests` to stop assuming all lifecycle
  text lives in `VulkanRenderer.FrameLoop.cs`.
- [ ] **10.3** Update `OpenXrTimingPipelineContractTests` for the atomic desktop
  activity API and captured-slot retirement behavior.
- [ ] **10.4** Update `WindowOwnershipContractTests` across both Preflight and
  SwapchainPolicy owners without weakening mismatched-swapchain guarantees.
- [ ] **10.5** Migrate every other hardcoded frame-loop reader found by `rg`,
  including `GpuRenderingBacklogTests`, `VulkanP0ValidationTests`,
  `VulkanDeferredProbeGiFixesTests`, and OpenXR stereo/device-loss contract
  fixtures.
- [ ] **10.6** Keep `VulkanCoreHardeningPhase21Tests`, OpenXR stereo temporal
  isolation/completion tests, and queue-gateway/device-state tests green so the
  refactor cannot bypass hardening through a new partial.
- [ ] **10.7** Replace string-only tests for result policy, state transitions,
  reentrancy, and cleanup with callable internal policy tests using the existing
  `InternalsVisibleTo("XREngine.UnitTests")` relationship.
- [ ] **10.8** Retain a small temporary structure test that verifies the
  coordinator calls phases in order and contains no captured local recovery
  function; remove it when a behavioral orchestration harness provides the
  same protection.
- [ ] **10.9** Run focused tests:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanDesktopFrameLoopTests|FullyQualifiedName~VulkanP1ValidationTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~WindowOwnershipContractTests|FullyQualifiedName~VulkanCoreHardeningPhase21Tests|FullyQualifiedName~OpenXrStereoTemporalIsolationCompletionTests" -v:minimal
  ```

- [ ] **10.10** Run the narrow editor build and check whitespace/errors:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  git diff --check
  ```

- [ ] **10.11** Run the broader unit-test project before final promotion and
  report unrelated pre-existing failures separately.
- [ ] **10.12** Add OpenXR coexistence tests for activity/drain exclusion,
  pending-slot `continue`, completed-other-slot drain, distinct eye/desktop slot
  domains, accepted-attempt/readiness semantics, dirty quiet-period bypass, and
  pending-timeline bypass.
- [ ] **10.13** Keep the hardening regression that prevents a batched OpenXR
  render failure from falling through to sequential-eye rendering after
  confirmed device loss, or record it as a passing prerequisite suite owned by
  the hardening branch.
- [ ] **10.14** Cover acquire device loss/unexpected failures, image-preparation
  failure, post-submit/pre-present failure, unexpected present results, and
  post-present auxiliary failure in the fault matrix.

Acceptance criteria:

- [ ] Focused policy, ownership, fault, OpenXR-coexistence, and source-migration
  tests pass.
- [ ] The editor builds with no new warnings.
- [ ] No test passes merely because it stopped reading the moved code.
- [ ] Fault tests cover every post-acquire phase boundary.

## Phase 11 - Runtime, Visual, Stress, And Performance Validation

- [ ] **11.1** Create one bounded validation root under
  `Build/_AgentValidation/<timestamp>-vulkan-frame-loop-decomposition/` with
  `logs/`, `mcp-captures/`, `mcp-output/`, `reports/`, and `renderdoc/`
  subfolders as needed. First prune stale immediate run roots so no more than ten
  remain under `Build/_AgentValidation/`.
- [ ] **11.2** Launch both normal Vulkan editor startup and the Vulkan Unit
  Testing World with MCP. Capture at least two Unit Testing World camera views
  and inspect the actual PNGs.
- [ ] **11.3** Compare final output, ImGui, dynamic text, viewport sizing, and
  startup presentation against the baseline.
- [ ] **11.4** Exercise resize by dragging, maximizing, restoring, minimizing,
  restoring, and changing DPI/display where available. Confirm no blocking
  interactive wait and no persistent stale generation.
- [ ] **11.5** Exercise texture uploads and command-buffer invalidation while
  frames are being recorded; use deterministic fault injection for otherwise
  rare deferred/dirty paths.
- [ ] **11.6** Run standard validation and synchronization validation; group
  VUIDs by signature and distinguish steady-state errors from shutdown-only
  teardown noise.
- [ ] **11.7** Validate OpenXR Vulkan with the desktop mirror active. Confirm
  OpenXR never drains the active desktop slot and session startup does not race
  desktop callback completion. If MCP is known to capture false-black output,
  cross-check with a native-window or eye-preview capture rather than accepting
  the tool result.
- [ ] **11.8** Validate native and Streamline/DLSS frame-generation swapchains on
  supported NVIDIA hardware. Record unsupported hardware as unvalidated rather
  than silently passing the matrix.
- [ ] **11.9** Exercise acquire/present out-of-date, suboptimal, surface-lost,
  timeout/not-ready, submit failure, and device-loss policies through fault
  injection where a real driver event is not deterministic.
- [ ] **11.10** Run `Tools/Measure-VulkanFrameLoop.ps1` under the same baseline
  scene/settings and compare p50/p95/p99, allocations, queue-submit count,
  present time, and retired-resource backlog.
- [ ] **11.11** Escalate to RenderDoc only if visual output, layout ownership, or
  pass ordering differs and MCP/log evidence does not identify the cause.
- [ ] **11.12** Record exact commands, settings, commit, environment, screenshots,
  log sessions, and result summaries in a tracked progress/validation note.
- [ ] **11.13** Run an available OpenVR smoke because it is the currently tested
  XR path, or explicitly record why the desktop Vulkan decomposition cannot
  affect that backend/path and leave it outside the required matrix.

Acceptance criteria:

- [ ] Desktop Vulkan output matches the baseline from more than one camera
  position.
- [ ] Resize/minimize/restore and overlay paths remain stable.
- [ ] No new validation error, device loss, semaphore misuse, invalid command
  buffer, stale descriptor, or swapchain ownership error appears.
- [ ] OpenXR plus desktop mirror/resource retirement remains stable.
- [ ] Steady-state managed allocation does not regress.
- [ ] Frame-loop p95/p99 and submit/present counts do not materially regress;
  any difference is explained with evidence.

## Phase 12 - Documentation And Closeout

- [ ] **12.1** Update `docs/architecture/rendering/vulkan-renderer.md` to replace
  the stale `Drawing.cs`, approximately 180-line, fence-based flow with the final
  partial map, timeline synchronization, typed outcomes, and recovery branch.
- [ ] **12.2** Update `docs/architecture/rendering/README.md` so its OpenGL versus
  Vulkan callback comparison points to the short coordinator and no longer
  reports the obsolete line count.
- [ ] **12.3** Update `docs/architecture/rendering/code-map.md` with every final
  frame-loop, command API, BackendObjects factory, and Streamline lifecycle
  source path.
- [ ] **12.4** Update
  `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md` and
  `window-creation-and-renderer-init.md` with the final entry/phase/finalization
  contract and circuit-breaker interaction.
- [ ] **12.5** Update `docs/architecture/rendering/openxr-vr-rendering.md` with
  the coherent desktop activity/readiness API, retirement exclusion mechanism,
  distinct slot domains, bounded startup gates, and device-loss behavior.
- [ ] **12.6** Update the Vulkan Upscale Bridge guide if proxy acquire/present or
  PCL marker ownership/ordering moved or changed.
- [ ] **12.7** Replace the provisional `Frame/README.md` map from Phase 2 with
  the exact final owners and dependency direction.
- [ ] **12.8** Finish the tracked progress/validation note with base/final
  commits, decisions, test commands, runtime matrix, visual/log evidence,
  allocation/performance comparison, risks, and any explicitly unvalidated
  hardware path.

Acceptance criteria:

- [ ] Durable architecture docs describe the code that actually shipped, not
  the pre-refactor callback.
- [ ] A contributor can locate each frame phase, understand its allowed
  dependencies, and trace normal/recovery/OpenXR coexistence flow.
- [ ] Streamline/DLSS, device-loss, and validation limitations are explicit.

## Validation Matrix

| Area | Required cases | Primary evidence |
|---|---|---|
| Build/tests | Editor build, focused policy/contracts, broader unit tests | Build/test logs |
| Normal desktop | Default startup and Unit Testing World | MCP/native screenshots, rendering/Vulkan logs |
| Surface lifecycle | Drag resize, maximize/restore, minimize/restore, DPI change | Screenshots, phase diagnostics, no blocking wait |
| Acquire | Success, not-ready, timeout, out-of-date, suboptimal, surface lost | Fault tests plus runtime logs where reproducible |
| Recording | Fresh, cached, deferred, scene failure, ImGui failure, text failure, dirtied after record | Fault tests, ownership assertions, allocation stats |
| Submission | Success, healthy failure, device loss | Queue history, first-error context, timeline state |
| Presentation | Success, out-of-date, suboptimal, surface lost, device loss | Present policy tests and runtime/fault logs |
| Uploads | None, submitted, cancelled, failure before submit | Upload publication/retirement assertions |
| Overlays | None, ImGui, dynamic text, both | Visual comparison and command-order assertions |
| OpenXR coexistence | Startup attempt/timestamp/quiet-period/pending-slot gates, desktop mirror, activity/drain exclusion, distinct eye/desktop slots | OpenXR logs/tests and VR run |
| OpenVR | Available smoke or explicit unaffected/out-of-scope rationale | Launch log and renderer/backend identification |
| Streamline | Disabled, frame generation enabled, DLSS composition change | Supported NVIDIA run and PCL/queue logs |
| Performance | Warm steady state and churn/resize state | Frame-loop report, allocations, p50/p95/p99 |
| Shutdown | Normal and device-loss/fault-injected, including zeroed timelines and no sequential-eye fallback | No false retirement, post-loss queue operation, fallback render, or teardown deadlock |

## Expected Files To Change

Primary implementation:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs`
- New `VulkanRenderer.FrameLoop.*.cs` partials listed in the target map
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.cs`
- New/moved command API partials and the `BackendObjects` render-object factory
  partial listed in the target map
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/README.md`

Expected tests:

- New `XREngine.UnitTests/Rendering/VulkanDesktopFrameLoopTests.cs`
- `XREngine.UnitTests/Rendering/VulkanP1ValidationTests.cs`
- `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs`
- `XREngine.UnitTests/Rendering/WindowOwnershipContractTests.cs`
- `XREngine.UnitTests/Rendering/VulkanP0ValidationTests.cs`
- `XREngine.UnitTests/Rendering/VulkanDeferredProbeGiFixesTests.cs`
- `XREngine.UnitTests/Rendering/VulkanCoreHardeningPhase21Tests.cs`
- The `GpuRenderingBacklogTests` and OpenXR stereo/device-loss fixtures found by
  the Phase 0 source-reader inventory

Expected durable docs:

- `docs/architecture/rendering/vulkan-renderer.md`
- `docs/architecture/rendering/README.md`
- `docs/architecture/rendering/code-map.md`
- `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`
- `docs/architecture/rendering/openxr-vr-rendering.md` if the shared activity
  contract changes its documented behavior
- `docs/developer-guides/rendering/vulkan-upscale-bridge.md` if proxy or PCL
  lifecycle ownership changes
- A progress/validation note under `docs/work/progress/rendering/`

The final diff may touch shared synchronization or swapchain files only when an
explicit ownership API is required. It should not duplicate or replace their
subsystems.

## Risks And Rollback Boundaries

### Risks

- Partial classes can create cosmetic organization while preserving invisible
  access to every private renderer field. Review method signatures and owned
  fields, not only filenames.
- Converting locals into a large struct can cause expensive copies. Pass the
  attempt by `ref` and inspect call sites/generated behavior.
- Moving an early return across a phase boundary can leave a binary semaphore,
  swapchain image, upload command buffer, or active-slot marker unresolved.
- `SuboptimalKhr` acquire handling may expose a pre-existing ownership bug; it
  must be fixed explicitly, not hidden as refactor noise.
- Publishing active state through multiple atomics can give OpenXR an
  inconsistent slot snapshot. Prefer one atomic slot/sentinel contract.
- Exception cleanup can issue invalid recovery GPU work after device loss.
- A source-contract test may appear green after its target moved if it reads the
  wrong or empty slice.
- Additional helper boundaries can accidentally format strings, box enums, copy
  the attempt, or allocate closures in the frame hot path.
- Moving profiler scopes can make performance comparisons meaningless even when
  behavior is correct.
- Concurrent hardening changes can produce conflict resolutions that silently
  bypass the queue gateway or first-error device-loss state.
- Too many tiny partials can obscure the flow. Keep files aligned with lifecycle
  ownership, not individual helper methods.

### Rollback boundaries

- Keep unrelated API/factory moves, state-contract introduction, and each phase
  extraction in separate commits.
- Require focused build/tests after every mechanical move and every extracted
  phase.
- Preserve profiler labels until final validation is complete.
- Do not delete old code in the same commit that introduces an unvalidated
  semantic replacement; first route one path through the replacement, validate,
  then remove the old path.
- If a phase causes a regression, revert that phase's routing while retaining
  earlier tested mechanical organization. Do not reset or discard unrelated
  hardening work.

## Open Questions To Resolve In Phase 0 Or 1

- [ ] Choose the engine policy after a successful `SuboptimalKhr` acquire:
  continue record/submit/present and then recreate, or explicitly consume/release
  before recreating. Treat the image and semaphore as acquired in either policy.
- [ ] Choose the `ErrorSurfaceLostKhr` policy: implement platform-safe
  `SurfaceKHR` plus swapchain recreation, or visibly fail/restart the renderer.
  Do not describe the current swapchain-only rebuild as surface recreation.
- [ ] Decide whether the active desktop slot sentinel alone replaces
  `_windowRenderCallbackInProgress` or whether a separate gate remains with a
  formally tested publication order. Preferred: one atomic state when feasible.
- [ ] Decide whether phase/policy types remain nested inside the renderer or use
  small internal top-level types for direct unit testing. Preferred: nested
  renderer types unless a pure policy has value outside orchestration.
- [ ] Decide whether all frame-loop partials remain direct `Frame/` siblings or
  move together into `Frame/DesktopFrameLoop/`. Preferred initially: siblings;
  move the complete set only if directory scanning becomes worse.
- [ ] Define the exact healthy-device policy after a non-device-lost submit
  failure when an image was acquired but the acquire semaphore was not consumed.
- [ ] Confirm whether advancing the frame slot after each recovered/failed path
  is intentional or historical. Encode the final decision in the outcome tests.
- [ ] Decide whether `_lastFrameCompletedTimestamp` means callback tick observed,
  GPU submit completed, or image presented. Preferred: split/rename timestamps
  so OpenXR startup gates state their actual requirement.
- [ ] Decide whether OpenXR session-start readiness remains advisory or becomes
  a real shared lifecycle gate. If a gate is added, define a lock order that
  cannot deadlock with the exclusive runtime graphics transition and queue
  gateway.
- [ ] Confirm which profiler/log labels are external tooling contracts and must
  remain byte-for-byte stable.

## External Technical References

- [Khronos `vkAcquireNextImageKHR` reference](https://docs.vulkan.org/refpages/latest/refpages/source/vkAcquireNextImageKHR.html)
- [Khronos Vulkan synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [Khronos Vulkan swapchain extension reference](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_swapchain.html)

## Final Acceptance Criteria

- [ ] `WindowRenderCallback` is a readable 60-120-line coordinator with named
  phases and one outer finalization path.
- [ ] Responsibility-specific partial files match the target ownership map; no
  `Helpers`, `Misc`, or replacement monolith exists.
- [ ] Generic render-state/frame-op APIs and the render-object factory no longer
  live in the frame-loop file.
- [ ] A stack-only attempt context captures immutable frame identity and all
  post-acquire ownership state.
- [ ] Every early exit has a named, tested disposition and cleanup obligation.
- [ ] Acquire semaphore, swapchain image, upload command buffer, timeline value,
  and frame slot each transition exactly once.
- [ ] Ownership is represented by typed states/transition methods rather than
  contradictory boolean combinations.
- [ ] `SuboptimalKhr` acquire behavior is correct and covered by tests.
- [ ] `ErrorSurfaceLostKhr` follows an explicit surface-recreate or visible
  restart/failure policy rather than an endless swapchain-only retry.
- [ ] Dirty-after-record work is never submitted.
- [ ] Queue submit/present paths use the hardened shared gateway and stop after
  device loss.
- [ ] OpenXR reads a coherent desktop activity/slot contract and never drains
  the active desktop slot.
- [ ] OpenXR's exclusive runtime graphics transition and session-start behavior
  are preserved or intentionally strengthened with concurrency tests.
- [ ] Queue-operation caller labels and first-failure diagnostic provenance are
  stable or have a documented, tested migration.
- [ ] Fallible marker, trimming, telemetry, and invariant work cannot strand an
  acquired/submitted image or mask the primary exception.
- [ ] Collect-visible release remains before potentially blocking desktop
  present.
- [ ] Native and Streamline/DLSS frame-generation paths retain behavior on
  supported hardware.
- [ ] Focused tests, broader unit tests, and the editor build pass with no new
  warnings.
- [ ] Runtime resize, overlay, upload, recovery, OpenXR coexistence, and shutdown
  validation is clean.
- [ ] No new steady-state managed allocations or material frame-loop timing
  regression is measured.
- [ ] Architecture docs, rendering README, code map, Frame README, and progress
  evidence describe the final source layout and lifecycle accurately.

## Final Promotion

- [ ] Review the final diff by lifecycle responsibility and ownership transition,
  not only by file move statistics.
- [ ] Rebase or merge the latest Vulkan-hardening integration branch and rerun
  focused tests plus the desktop/OpenXR smoke matrix. If integration touches a
  decomposed lifecycle, queue, synchronization, or device-state file, also
  rerun the affected validation-layer, fault-injection, allocation, and
  frame-loop performance gates so evidence postdates the merge.
- [ ] Record final validation evidence and any hardware paths that remain
  explicitly unvalidated.
- [ ] Merge `rendering-vulkan-frame-loop-decomposition` back into `main` only
  after all final acceptance criteria are satisfied.
