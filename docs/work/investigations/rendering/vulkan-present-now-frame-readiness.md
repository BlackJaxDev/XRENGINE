# Vulkan Present-Now Frame Readiness Investigation

Last updated: 2026-08-26
Status: prior desktop acceptance passed; current prepared-cohort Sponza fix builds, live revalidation pending
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
- Live revalidation is still required. One fresh isolated editor build compiled
  but startup encountered a concurrent settings-projection `XRWorld` clone
  regression; its owner fixed that boundary. A subsequent incremental session
  build was invalidated when concurrent session retention removed its artifact
  tree during compilation. No other task's live editor was stopped.

## RenderDoc investigation

`rdc doctor` passed, but this machine has mismatched capture installations:
the `rdc` Python module/layer is RenderDoc 1.41 while the installed desktop
RenderDoc is 1.44.

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
- A direct RenderDoc 1.44 launch crashed CoreCLR during editor startup before
  the configured capture frame was reached. No engine capture-trigger message
  was emitted and no `.rdc` was produced.

The representative RenderDoc checkbox remains open. Resume it only after the
capture and replay versions are aligned; do not weaken frame-slot reuse waits to
work around a capture-layer timeline stall.

## Build status

Latest targeted validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -p:BuildProjectReferences=false
```

Result: 0 warnings and 0 errors on 2026-08-26 at 16:52 local time after the
prepared-cohort cold-entry foreground fallback was added.

Latest full editor validation:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Result: 0 warnings and 0 errors on 2026-08-26 at 16:14 local time. The earlier
concurrent `UnityAnimImporter.cs` compile break was resolved by its owning work;
this investigation did not alter or revert those changes. A later isolated
editor build containing the Sponza fix compiled with zero errors and nine
existing OscCore warnings, but did not reach MCP because of the concurrent
settings clone regression described above. The current full working tree still
needs one clean build after concurrent changes settle.

## Remaining work / resume sequence

1. Leave any other task's live editor untouched. After
   `present-now-capacity1-integrated` stops and no isolated build is running,
   start a fresh uniquely named capacity-one session.
2. Reproduce the away-to-dense-Sponza transition. Inspect the dense PNG and
   retain at least 96 ledger entries proving fresh matching frame/source IDs,
   monotonic graphics signals, ready dependencies, and no renderer pause,
   fallback, invariant failure, or generic terminal-failure storm.
3. If the cold pipeline completes inside foreground readiness, check the Phase
   5 deliberately slow successful compile criterion in the TODO.
4. In a separate session, overlap one duplicate Sponza hierarchy to naturally
   exceed the main-scene lane. Verify the typed bounded-capacity diagnostic and
   stable terminal identity before checking the Phase 3/6 acceptance items.
5. Rerun the targeted Vulkan and full editor builds after concurrent source
   changes settle.
6. Align RenderDoc 1.41/1.44 capture and replay tooling, repeat the fixed-camera
   capacity-one capture, and inspect/export a settled Sponza frame.
7. Validate OpenXR deadline/fallback behavior on an available runtime/headset.
8. Obtain explicit user clearance before adding or modifying tests, then run
   the unchecked Phase 8 contract, capacity, mutation, fault-injection, and
   allocation-soak matrix.

## User-reported result

The user reported that current runs still black out and never recover when the
camera enters Sponza. The live log reproduction above explains that report and
the source fix is implemented, but no post-fix visual result has been recorded
yet. Earlier automated desktop Vulkan acceptance remains valid evidence for the
broader architecture; current-tree Sponza recovery is explicitly pending.
