# Vulkan Present-Now Frame Readiness Investigation

Last updated: 2026-08-26
Status: current desktop capacity-one Sponza acceptance passed; explicit fault injection and representative RenderDoc capture remain open
Tracker: `docs/work/todo/rendering/vulkan-present-now-frame-readiness-todo.md`

## Problem statement

A cold Sponza-facing Vulkan frame admitted hundreds of visible mesh requests
but repeatedly rejected the whole recording before `vkBeginCommandBuffer`.
Looking away reduced the visible cohort enough for deferred background work to
make progress, which made the renderer appear to recover. The evidence pointed
to CPU readiness/admission livelock rather than device loss.

A later current-tree reproduction found a second, narrower form of the same
view-dependent symptom. Frame 5198 encountered a non-reusable cold entry inside
an otherwise matching prepared mesh cohort. `sponza_371` correctly returned
`ProgramsPending` and its asynchronous graphics pipeline compiled successfully
13.23 ms later, but the prepared-cohort fast path tried the entry only once and
immediately promoted the transient state to a sticky PresentNow terminal
failure. The process remained alive; the renderer was intentionally paused and
subsequent ticks emitted generic failed terminal records.

The same original run emitted two independent persistence-boundary warnings:

- MemoryPack attempted to serialize runtime `SceneNode` component events.
- Asset-graph traversal reached a 1,024-entry runtime light-binding array.

## Product contract

Desktop/editor `PresentNow + BlockForExact` freezes one accepted frame, drives
its exact required work to readiness before swapchain acquisition, then records,
submits, and presents fresh work or reports an explicit failure. It may hitch on
the first cold view. It may not defer recording or silently replay old content.

Deadline-bound XR uses `PresentNow + MeetDeadlineWithGpuFallback`; only a GPU
fallback explicitly captured by the accepted output contract is legal.

## Implemented resolution

- Runtime-only scene events and light-binding publication state are excluded
  from MemoryPack, YAML, and asset-graph persistence boundaries.
- Output work class, present policy, failure policy, and fallback contracts
  propagate through the producer/output dependency DAG.
- Desktop execution is ordered as frame-slot retirement, logical-plan capture,
  exact pre-acquire readiness, target revalidation/acquire, native reseal,
  record, submit, and present.
- `VulkanAcceptedFramePlan` owns bounded terminal/UI/main/shadow operation
  lanes, exact dependency tickets, texture/shadow manifests, descriptor
  receipts, output contracts, compatibility state, and lifetime pins.
- Pipeline, descriptor, buffer, texture-upload, and shadow tickets advance
  monotonically and retain terminal failure state by exact generation.
- Cold foreground pipelines can be claimed and completed synchronously;
  background compile limits no longer make PresentNow correctness deferable.
- A prepared-cohort match with a cold non-reusable entry now retains the exact
  accepted requests and falls through to foreground job pumping/waiting. It no
  longer bypasses PresentNow readiness merely because the cohort fast path saw
  the pipeline before its successful async compile completed.
- Required uploads use a foreground staging lane, bounded chunking, exact
  timeline completion, and graphics-queue transfer when no dedicated transfer
  queue exists.
- Texture publication is generation-specific and remains pinned through frame
  retirement. Sampled+storage imports publish the exact `General` layout.
- The canonical GPU-scene publication bridge now owns one pin per distinct
  publication and transfers the complete foreground cohort into the exact
  accepted desktop/explicit-output frame slot. Pins remain valid until slot
  retirement and are released by slot reuse or deterministic renderer teardown;
  background and pre-plan OpenXR capture retain the bounded aggregate bridge.
- Native descriptor updates precede semantic publication. Failed native updates
  mark touched sets unknown and prevent consumers from accepting partial state.
- Exact shadow readiness bypasses background budgets; only a fallback selected
  in the sealed output contract is legal.
- PresentNow primary recording no longer has a progressive/deferred path, and
  the obsolete `RecordPreparedExplicitPrimary` flow was removed.
- Presentation telemetry records frame/source IDs, epochs, output generation,
  command-buffer/submit/present provenance, dependency state, fallback use,
  timestamps, and invariant results.
- OpenXR eye/mirror worker inputs and results now carry logical view ID, output
  index, exact output contract, and typed worker failure through preallocated
  logical views. A live headset/runtime pass remains outstanding.
- ImGui font-atlas descriptors and pipeline retirement now use the renderer's
  descriptor/lifetime authorities rather than bypassing native publication.
- Target-known terminal UI work compiles at the output/context initialization
  boundary and logs output-generation/format/dynamic-rendering identity before
  propagating any compile failure. Frame-dependent composition/fallback
  variants are mandatory members of the sealed pre-acquire pipeline manifest;
  empty-terminal clear and failure reporting are pipeline-free by design.
- Desktop settlement publishes a separate typed failure record while retaining
  the exact orchestration reason. Native results and exception identity now
  distinguish no-image, out-of-date, surface loss, device loss, host/device
  OOM, caller cancellation, admission, readiness, recording, submission, and
  presentation. Genuine failures log frame, slot, scene epoch, output
  generation, native result, exception type, and detail.
- Mesh-lane overflow records now preserve accepted-frame lane, mesh lane,
  configured, required, accepted, and rejected counts through desktop,
  explicit-output, and direct OpenXR capture paths. The natural-overflow live
  acceptance remains open.

## Live validation evidence

Durable evidence root:

`Build/_AgentValidation/20260825-160514-present-now-readiness/`

Session-specific settings (the user's root settings were not modified):

`Build/_AgentValidation/20260825-160514-present-now-readiness/settings/UnitTestingWorldSettings.jsonc`

### Primary Vulkan/Sponza session

Named session:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-104406-present-now-readiness/`

Representative Vulkan log:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-104406-present-now-readiness/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-26_13-46-48_pid58572/log_vulkan.log`

Observed results:

- The session used Vulkan with validation and frame-op tracing enabled.
- Away, sparse, dense-cold, dense-interior, rapid-sweep, and warm-dense PNGs
  were captured under the evidence root's `mcp-captures/` directory and viewed.
  Completely-away captures were black; Sponza-facing captures showed changing,
  textured geometry rather than a stale fixed image.
- Eight consecutive retained frames 11919-11926 had matching frame/source IDs,
  fresh presentation provenance, nonzero monotonic graphics signals, and no
  fallback or presentation invariant failure.
- A 64-frame sweep 14801-14864 had zero stale/fallback/provenance/primary/
  dependency failures and strictly increasing graphics timeline signals.
- Frozen frame 14982 carried 127 dependencies and exact phase timestamps with
  no readiness or presentation failure.
- The dense cohort submitted 395 GPU commands: 361 opaque deferred and 32
  alpha-tested.
- Texture streaming quiesced at 39 textures with zero pending decode or upload
  work.
- `RecordingDeferred`, required-pipeline-pending, draw-not-ready, queue-reject,
  dropped-frame-op/draw, missing-scene-swapchain-write, validation-message, and
  validation-error counters were all zero.
- Neither original persistence warning appeared. One unrelated Khronos advisory
  concerned an unused UIText vertex attribute location.
- The named session was stopped without stopping any unrelated editor process.

### Capacity-one acceptance

Capacity configuration:

`Build/_AgentValidation/20260825-160514-present-now-readiness/settings/capacity1-session-environment.json`

Final named session:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-145940-present-now-capacity1-acceptance/`

Representative screenshots:

- `mcp-captures/capacity1-acceptance-away/Screenshot_20260826_150725_125_7ce15ec21b0949f08a3b37c5ee6556ad.png`
- `mcp-captures/capacity1-acceptance-dense/Screenshot_20260826_150749_539_61dfa794129d46e68017879d3e2c8e92.png`

Observed results:

- After clearing diagnostic ledger state, 43 consecutive frames 3474-3516 had
  zero freshness, submit/present, provenance, primary, dependency, fallback, or
  invariant failures. Graphics signals increased strictly and all 43 descriptor
  sequences matched native writes.
- A second deliberate away-to-Sponza sweep retained 96 frames 3989-4084 with
  the same zero-failure result. Dependencies grew from 34 to 127 and main-scene
  operations from 7 to 849 without requiring the camera to look away again.
- The live frame-op trace contained 847 operations and the expected Sponza
  deferred draws.
- PresentNow primary-recording, frame-plan preparation, manifest/dependency
  capture, descriptor publication, operation-loop, submission, and boundary
  allocation counters and high-water values were all zero.
- `RecordingDeferred`, required-pipeline-pending, draw-not-ready, descriptor
  fallback/binding failure, validation, dropped-operation/draw, and missing
  swapchain-write counters were all zero.
- Full-log searches found no renderer pause, descriptor-table unavailability,
  tracked-submission rejection, unknown native publication, VUID, MemoryPack,
  large-array, PresentNow readiness, or presentation invariant failure.

Earlier capacity-one attempts exposed and led to fixes for three real
integration gaps: ImGui font-atlas native descriptor registration, inclusion of
pending texture generations in the cold accepted closure, and authoritative
presentation-ledger synthesis after a diagnostic clear. The final acceptance
above includes all three fixes.

### Fresh integrated capacity-one rebuild

Named session built from the current working tree:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-154722-present-now-capacity1-integrated/`

Representative screenshots:

- `mcp-captures/capacity1-integrated-away/Screenshot_20260826_155210_647_3cea4af7ba554102bce5b0e29a052822.png`
- `mcp-captures/capacity1-integrated-dense/Screenshot_20260826_155243_609_80686c8293db4dc1befd56505ef41ab8.png`

Observed results:

- The session's isolated editor build completed with zero errors. Its nine
  warnings came from the existing `OscCore` submodule rather than renderer
  changes.
- The away capture was black and the fixed dense capture showed a textured
  Sponza wall when inspected at original resolution.
- Another 96 consecutive settled frames 4176-4271 had zero stale/fallback/
  submit/present/provenance/target/descriptor/invariant failures. Frame/source
  IDs matched for all frames, while graphics signals and native descriptor
  publication sequences increased strictly.
- All retained frames carried 127 ready dependencies. The live frame-op trace
  contained 811 operations and active Sponza deferred draws.
- Texture streaming was quiescent at 39 tracked textures with zero pending
  transition, decode, or GPU-upload work.
- Full-log searches found no renderer pause, missing descriptor table, tracked
  submission rejection, unknown native publication, VUID, validation error,
  `RecordingDeferred`, persistence warning, PresentNow readiness exception, or
  presentation invariant failure.
- Existing unrelated warnings remained for unavailable TSR temporal history
  and the explicitly unsupported Vulkan GPU BVH raycast path. They did not
  interrupt fresh presentation.
- The named session was stopped through the session manager.

### Accepted frame-slot publication-pin validation

Named session rebuilt from the current working tree with scheduling capacity
forced to one:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-161446-present-now-pin-slot/`

Representative screenshots inspected at original resolution:

- `mcp-captures/pin-slot-away-black/Screenshot_20260826_162019_263_559eae9e1db1444fbc78897ab1f48b21.png`
- `mcp-captures/pin-slot-dense/Screenshot_20260826_162037_325_339fff9326ef479fbbaea7e39dd447e7.png`

Observed results:

- The deliberately away frame was all black. Refocusing the imported Sponza
  root immediately produced a textured model without restarting or requiring
  another away cycle.
- Ninety-six consecutive settled frames 4377-4472 had zero freshness,
  submission, presentation, descriptor-publication, target-compatibility,
  fallback, or invariant failures. Frame/source IDs matched throughout;
  graphics timeline values and native descriptor sequences increased strictly.
- Dependency counts changed from 34 to 127 and main-scene operation counts from
  7 to 845 across the transition. The latest trace contained 845 operations
  with active Sponza deferred draws.
- Texture streaming was quiescent at 39 textures with zero pending transition,
  decode, or GPU-upload work.
- Full-log searches found no renderer pause, terminal-pipeline initialization
  failure, descriptor-table loss, tracked-submission rejection, unknown native
  publication, VUID, validation error, `RecordingDeferred`, persistence
  warning, PresentNow failure, presentation invariant failure, pin-transfer
  error, or cleanup-step failure.
- The known unrelated bitmap-font, unused UIText vertex-attribute, TSR history,
  and unsupported Vulkan GPU-BVH warnings remained. The exact session-manager
  stop completed cleanly, exercising accepted-plan teardown.

### Authored persistence round-trip and current Sponza regression

Named session:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-163804-present-now-roundtrip-overflow/`

Authored round-trip artifact:

`Build/_AgentValidation/20260825-160514-present-now-readiness/mcp-output/serialization-roundtrip/PresentNowSerializationRoundTripReload.asset`

Observed persistence results:

- A new authored scene contained `RoundTripPointLightNode` with translation
  `(12.5, -3.25, 7.75)`, scale `(1.25, 2.5, 0.75)`, and a
  `PointLightComponent` with radius `42.75` and brightness `6.5`.
- The production `export_scene` path wrote the asset. It was copied to a new
  path, the original in-memory values were changed to sentinels, and the source
  scene was deleted before importing the copied path. This avoided proving only
  an in-memory/path-cache hit.
- Disk import restored the same scene, node, and component IDs plus all authored
  transform and light values. Making the imported scene visible restored its
  runtime world binding, after which scene integrity reported zero errors and
  zero warnings.
- This closes the Phase 1 authored scene/component round-trip checkbox.

Observed Sponza failure results:

- Moving dense Sponza work into view first failed at frame 5198 with
  `stage=MeshMaterialization`, ticket `visible-mesh-generation`, and detail
  `Prepared mesh-operation cohort legacy hole could not be materialized.`
- The immediately preceding renderer diagnostic identified mesh `sponza_371`
  and reason `ProgramsPending`. The corresponding async graphics pipeline then
  completed successfully in 13.23 ms.
- The failure was not device loss or a native Vulkan crash. The editor process
  stayed ready, while `_presentNowTerminalFailure` prevented further readiness
  work. The first terminal record retained frame/slot/epoch/detail identity;
  later ticks repeatedly published generic `ReadinessFailed` records with no
  detail.
- Root cause was the `allowPreparedCohort && preparedCohortMatched` early return:
  a failed cold hole invalidated the fast path and cleared the accepted requests
  before the ordinary foreground retry loop could pump the successful compile.
- Source now uses that early return only for non-foreground work. PresentNow
  falls through with its accepted request cohort intact. The targeted Vulkan
  project builds with zero warnings and zero errors.
- Initial live revalidation attempts were blocked first by a concurrent
  settings-projection `XRWorld` clone regression and then by isolated-session
  artifact cleanup during compilation. The closeout pass below supersedes
  those incomplete attempts. No other task's live editor was stopped.

## 2026-08-26 current-tree Sponza closeout

The prepared-cohort cold-entry fix is now live-validated. The isolated
capacity-one session is:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-182037-present-now-closeout-v6/`

Reaching that path exposed and resolved three adjacent current-tree blockers:

- Effective startup settings were deep-cloning nested `XRAsset` instances into
  an attached asset graph, so marking the root as a runtime projection threw.
  Projection construction now suppresses object-cache registration, detaches
  nested startup/build/user settings after overlay, clears projection-only
  embedded-asset edges, and marks the complete projection transient.
- Opaque-uniform hoisting moved a forward-descriptor declaration above the
  `XRENGINE_FORWARD_DESCRIPTOR_SET` definition. Macro-dependent layouts now
  stay in preprocessing order.
- Source reflection did not understand the forward-descriptor macro form. Its
  qualifier is now normalized to descriptor set 3 before binding parsing. An
  ignored isolated probe reflected `DirectionalShadowMaps` as set 3, binding
  15 and exited successfully.

The camera then moved through eight outside, entrance, left, inside, right,
upper, deep, and near-wall Sponza positions with 0.65-second transitions. Cold
required fragment pipeline/library compilations took approximately 20-21
seconds. The accepted frame remained in foreground readiness, reported
monotonic watchdog progress, and recovered without looking away or converting
the cold dependency into a terminal frame failure.

After settlement, the final-presentation ledger retained 128 consecutive
frames 327-454. Each record had matching frame/source/accepted-epoch identity,
a strictly increasing graphics timeline signal, a matching successful native
descriptor write, `PresentedNew`, no fallback, and no invariant failure. The
logs contained no `RendererPaused`, `PresentNowReadinessFailed`,
`DesktopFrameFailure`, shader compilation failure, or Vulkan validation error.

The inspected settled image is:

`Build/_AgentValidation/20260826-175504-present-now-closeout/mcp-captures/v6-settled-sponza/`

It shows a complete textured Sponza stone wall rather than the stale red/blue
pre-fix image. The session was stopped through its exact named MCP session; no
other editor process was stopped.

Targeted Vulkan and full editor builds pass with zero warnings and zero errors.
A targeted `UberShaderCompilationTests` invocation cannot currently compile the
existing unit-test project because unrelated tests still call the removed
`UnitTestingWorldSettingsStore.ApplyStartupOverrides` API and an obsolete
`VulkanImportedTextureUploadRequest` constructor. No tests were added or
modified on top of that broken baseline.

## 2026-08-26 18:59 staging-pool exception regression

The later user run was:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-26_18-59-18_pid11472/`

Its exception census was 47 `FileNotFoundException` records, two occurrences
of one `InvalidOperationException`, and three
`VulkanPresentNowReadinessException` records. The mapped-memory failure paused
the renderer at frame 437 and produced 3,638 later
`PresentNowReadinessFailed` records. Those later records were the terminal
failure latch being re-reported, not 3,638 independent failures.

The `FileNotFoundException` records were separate, expected cold-cache misses.
All 45 prospective cooked `.XRTexture2D.asset` paths were absent, every source
texture existed, and source fallback subsequently uploaded 43 of them before
the log ended. `AssetTextureStreamingSource` now checks for an absent
prospective cache before reading it, latches source fallback without warning or
exception-driven control flow, and still warns for an existing but unreadable
or corrupt cache. A disappearance between the check and read receives the same
benign cache-miss treatment.

The terminal Vulkan chain began while preparing required ticket
`texture-upload:81:3` for `sponza_fabric_diff.png`. The worker reached
`VulkanStagingManager.Acquire`, then `VulkanBufferResourceService.UpdateFromVoidPtr`
rejected the pooled buffer's mapped-memory identity. This was not a native VMA
map failure: host-visible VMA allocations are persistently mapped and the run
contained no `VMA map failed` warning.

Root cause was retirement identity loss. VMA buffer suballocations may share a
single `VkDeviceMemory` block, while the retirement queue deduplicates that
memory handle. Later buffers in the same block could therefore reach
`DrainRetiredBuffers` with `Memory=0`. The old drain skipped
`VulkanStagingManager.TryRelease`, destroyed the per-buffer allocation, and
left its pool entry behind. Later Vulkan handle reuse could make that stale
entry appear reusable and fail mapped-slice validation.

The drain now recovers the authoritative memory from the tracked per-buffer
allocation before attempting pool release. Every buffer that is actually
destroyed is also forgotten by buffer identity before its allocation is
removed. A genuine mapping failure remains terminal; no dedicated-memory retry
or CPU fallback hides an invalid pool/allocation invariant. Mapping diagnostics
now distinguish invalid identity, missing tracking, non-host-visible memory,
memory mismatch, and out-of-range writes.

Fresh exact-workload validation used the isolated session:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-191905-last-run-exceptions-v2/`

The camera interpolated through central, side, and opposing Sponza viewpoints,
forcing visibility-driven texture work. One inspected view showed the imported
stone wall and the captures changed with camera position. The session passed
the original frame-437 failure point, then settled at frame 825 with 223
tracked textures, zero pending transitions, zero active decodes, zero active
GPU uploads, and no frozen streaming state. The presentation ledger remained
unfrozen and reported a fresh accepted frame with a successful descriptor
write, matching frame/source identity, and no invariant failure.

After the named session was stopped and logs were flushed, full-log counts were
zero for `FileNotFoundException`, `InvalidOperationException`,
`VulkanPresentNowReadinessException`, `PresentNowReadinessFailed`,
`RendererPaused`, mapped-memory failures, VUIDs, and Vulkan validation errors.
The isolated editor build had zero errors; its nine warnings were the existing
`OscCore` submodule warnings.

Residual hardening remains explicit rather than folded into this regression
fix: suppress repeated generic frame-failure records after one terminal pause
transition, and make the foreground-reserve builder prove that it provisioned
distinct reserve entries.

## 2026-08-26 atomic staging recycle publication

The first remaining staging hardening slice is complete. Each pooled staging
entry now records the exact published Vulkan buffer resource generation and has
an explicit `Idle`, `InUse`, or `Retiring` state. A completed upload can become
reusable only through one staging-lock transaction: validate buffer, memory,
generation, and `InUse` state; quarantine the entry as `Retiring`; reactivate
the matching pending-retirement lifetime under its exact old generation;
publish a fresh nonzero resource generation; update the entry generation; and
publish `Idle` last. `Acquire` therefore cannot observe the allocation in the
old release/reactivation gap.

The subsequent reserve-provisioning slice removed the direct no-retirement
release path from `EnsureForegroundReserve`: cold reserve entries are now
created directly in `Idle` state with their published allocation generation.
Forced retirement drains and device loss still deliberately decline
reactivation, leave the entry quarantined, then use the retirement ticket's
exact generation to guard pool forgetting and physical destruction.
Retirement still recovers authoritative per-buffer VMA memory identity when
several suballocations share one `VkDeviceMemory` block.

Targeted validation on 2026-08-26:

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
  completed with zero warnings and zero errors.
- Isolated session
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-214445-staging-recycle-slice1/`
  built and stopped cleanly. Its editor build had zero errors and the nine
  existing `OscCore` warnings.
- Three materially different Vulkan camera views were captured and inspected.
  The streaming query settled at frame 798 with 133 tracked textures, zero
  pending transitions, zero active decodes, zero queued decodes, zero active
  GPU uploads, and `vulkan_frozen=false`; the log later reached a ready
  PresentNow frame 1073 before shutdown.
- Full-log searches found no lifecycle/recycle `InvalidOperationException`,
  mapped-memory failure, `VulkanPresentNowReadinessException`,
  `PresentNowReadinessFailed`, renderer pause, device loss, OOM, or VUID record.
  The existing run settings had Vulkan validation layers disabled, so this is
  runtime/log evidence rather than a validation-layer pass.

No tests were added or changed: repository policy requires the live feature
path to pass first and then explicit user clearance before regression-test
work. The completed reserve-provisioning follow-up is recorded below.

## 2026-08-26 binary texture-cache dispatch and distinct reserve provisioning

The YAML exception in the 21:53 editor run was a format-dispatch failure, not
malformed authored YAML. The affected
`studio_small_09_4k.exr.XREngine.Rendering.XRTexture2D.asset` cache file is a
178,958,379-byte cooked binary streaming payload whose first bytes were passed
to `XRAssetDeserializer` as a YAML scalar. Generic asset loading now asks the
registered feature codec to claim a direct asset file before opening a text
reader. A recognized binary texture payload is claimed even when stale or
incompatible, so raw bytes cannot fall through to YAML. Missing or rejected
prospective cache entries retain the original source texture as their
recoverable authority instead of assigning a filler texture. Cache replacement
I/O and access races are also classified as recoverable asset-load failures;
out-of-memory and cancellation semantics are not hidden. The Runtime.Core
source directory is explicitly unignored so the dispatcher cannot disappear
from an ordinary patch or fresh checkout.

The foreground reserve bug was also confirmed: the old loop acquired and
released through the ordinary pool, so the first exact-fit idle entry could be
reacquired for every nominal reserve slot. Cold provisioning now creates the
missing protected entries directly, serializes only that provisioning path,
counts protected `Idle`, `InUse`, and `Retiring` entries toward the configured
total, and leaves ordinary foreground spill allocations trim-eligible. Its
allocation-free snapshot verifies nonzero, pairwise-distinct buffer handles
and allocation generations and emits one cold-path identity signature.

Validation evidence:

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
  completed with zero warnings and zero errors.
- Isolated session
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260826-220419-yaml-reserve-slice2/`
  reported `[Vulkan.StagingReserve] configured=4 created=4 total=4 idle=4
  inUse=0 retiring=0 distinctBuffers=4 distinctGenerations=4`.
- The exact 178,958,379-byte cache payload from the failing run was copied into
  the isolated cache and loaded through MCP `find_asset`. It returned one
  `XREngine.Rendering.XRTexture2D` with the binary cache as `filePath` and
  `Build/CommonAssets/Textures/studio_small_09_4k.exr` as `originalPath`.
  Full-log searches found no `YamlDotNet`, `YamlException`, unresolved
  `XRTexture2D` scalar, or texture-load failure.
- Before the separate capacity failure described below, a Vulkan viewport
  capture was inspected and showed the textured stone-wall scene. Texture
  streaming later reported zero pending transitions, decodes, and GPU uploads,
  with `promotions_blocked=false` and `vulkan_frozen=false`.

No tests were added or changed because explicit post-live-validation test
clearance has not been given.

## 2026-08-26 interactive resize non-recovery diagnosis

The reported resize symptom has two causal parts. During a Win32 sizing modal
loop the configured `Win32ModalLoopTimer` strategy freezes ordinary engine
relayout and swapchain recreation, so Windows temporarily stretches the last
presented image. The session published its first framebuffer resize only on
`win32-exit-size-move`, after which the managed render pipeline rebuilt and
committed 1905x922 resources. Vulkan then recreated the swapchain from
1920x1080 to 1905x922 in 179.219 ms with zero final extent divergence.

The permanent failure began on the first post-resize exact-readiness frame,
not in WSI convergence. Rebuilding the dense scene produced a main-scene
manifest beyond its declared lane capacity:

```text
FramePlanCapacityExceeded lane=MainScene meshLane=MainScene
actual=1537 configured=1536 accepted=1536 rejected=2306
```

`PausePresentNowRenderer` stored this exception in
`_presentNowTerminalFailure`. The guard at the start of every later readiness
attempt then stopped before acquire, record, submit, or present, while generic
`DesktopFrameFailure` logging repeated on every callback. Thus the stretch
during dragging is current modal-resize policy; failure to recover after
release is the one-request frame-plan overflow combined with a permanent
terminal-failure latch and an unbounded follow-on log storm. This run naturally
reproduced the overflow portion of the open acceptance item, but it does not
close that item because one bounded detailed failure record and recovery policy
are still missing.

## RenderDoc investigation

`rdc doctor` now reports an aligned RenderDoc 1.44 Python module,
`renderdoccmd`, and registered Vulkan layer. The earlier mismatched-tooling
observations remain useful history but are no longer the current blocker.

- `renderdoc/present-now-capacity1-sponza.rdc` is a valid Vulkan capture, but it
  contains only the startup swapchain frame: 11 events and zero draws. It is
  not representative Sponza acceptance evidence.
- That startup capture reports one `HOST_READ`/`ALL_COMMANDS` buffer-barrier
  VUID at EID 3, which is outside the replayable event range (events begin at
  EID 4). The engine's only `HOST_READ` barrier is a global memory barrier paired
  with `HOST_BIT`, not the reported buffer-barrier shape; the message belongs to
  capture-boundary/initial-content instrumentation rather than an identified
  engine command.
- A second 1.41 target-control run waited for Sponza and the fixed camera, then
  froze Vulkan progress after capture was requested. The CPU/editor loop kept
  advancing, while the renderer correctly remained in frame-slot completion
  maintenance waiting for the intercepted graphics timeline value. The helper
  timed out and terminated only its owned PID.
- Two new direct RenderDoc 1.44 editor launches targeted frames 450 and 60 with
  a 240-second timeout. Neither observed a present, both timed out, and neither
  produced a `.rdc`. No owned target process remained afterward.

The representative RenderDoc checkbox remains open. Resume it only after the
direct-launch/no-present behavior is understood; do not weaken frame-slot reuse
waits to work around capture-layer interception.

## Build status

Latest targeted validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -p:BuildProjectReferences=false
```

Result: 0 warnings and 0 errors on 2026-08-26 after the prepared-cohort
cold-entry foreground fallback was added.

Latest full editor validation:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Result: 0 warnings and 0 errors on 2026-08-26 at approximately 18:41 local
time, including the startup-projection, shader preprocessing, and descriptor-
source reflection changes used for closeout.

## Remaining work / resume sequence

1. Prove background upload, compilation, and shadow work yields to exact
   foreground readiness and resumes without starvation.
2. Emit one detailed renderer-pause transition for a terminal PresentNow
   failure and suppress the later generic per-frame failure storm.
3. Define and implement the recovery policy for a main-scene manifest that
   exceeds a declared lane during post-resize reseal; do not hide the defect by
   merely increasing the fixed capacity.
4. In a separate isolated session, mutate camera/scene state while foreground
   preparation is blocked. Verify captured-epoch immutability and one stable
   typed capacity failure.
5. Repair or rebase the existing unit-test API drift, then run the unchecked
   Phase 8 contract, capacity, mutation, fault-injection, and allocation-soak
   matrix without changing the live feature first.
6. Diagnose the aligned RenderDoc 1.44 direct-launch/no-present behavior, then
   inspect a representative settled Sponza capture.
7. Validate OpenXR deadline/fallback behavior on an available runtime/headset.

## User-reported result

The reported current-tree blackout was reproduced, traced to the prepared-
cohort cold-entry early return, fixed, and visually revalidated. The capacity-
one closeout session moved throughout Sponza, waited through genuinely slow
successful driver compiles, and recovered to 128 consecutive fresh frames with
stable provenance and no renderer pause. Remaining unchecked items are explicit
mutation/overflow/failure injection, the broader test matrix, representative
RenderDoc capture, and real OpenXR validation rather than the reported desktop
Sponza liveness defect.
