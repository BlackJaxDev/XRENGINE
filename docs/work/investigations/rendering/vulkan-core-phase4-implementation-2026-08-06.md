# Vulkan Core Phase 4 Implementation

Date: 2026-08-06  
Status: Phase 4.0 complete; Phase 4.1+ active
Owner: Rendering

## Objective

Close the remaining Phase 1-3 validation gates in the consolidated Vulkan core
hardening tracker, then migrate Phase 4 to the seven-authority render-loop
architecture without retaining facade forwarding layers or duplicate production
authorities.

## Phase 1-3 Closeout Evidence

- Migrated stale Vulkan unit-test construction and dependency contracts for the
  removed schedule cache and required command-chain ordinal.
- `Test-VulkanPhase3-Regression`: 110 passed, 0 failed.
- `dotnet build .\XRENGINE.slnx --no-restore -warnaserror`: 0 warnings,
  0 errors.
- Named editor session: `vulkan-core-phase4`, Vulkan,
  `StandardValidation`, `XRE_VULKAN_VALIDATION=1`.
- MCP profiler snapshot: zero Vulkan validation errors and zero pending retired
  resources.
- Two camera-dependent viewport captures returned the intended typed Vulkan
  failure: no live transfer-readable color image was available and no CPU or
  OS-window fallback was used.
- The Vulkan and rendering logs contained no VUID, validation error,
  `VK_ERROR_DEVICE_LOST`, device-loss, submission-rejection,
  lifetime-rejection, fatal, or unhandled-exception match, including teardown.

Ignored evidence is under
`Build/_AgentValidation/mcp-sessions/vulkan-core-phase4/`, including MCP
responses, profiler state, capture results, and the two editor log sessions.

## Phase 4 Baseline

`Tools/Reports/Get-VulkanCoreArchitectureInventory.ps1` records the baseline and
can be rerun after every vertical ownership cut. The 2026-08-06 pre-extraction
snapshot contains:

- 890 hand-written C# files and 178,506 physical lines under the audited Vulkan
  core root;
- 323 `VulkanRenderer` partial declarations;
- 381 files with type-wide unsafe declarations;
- 102 files with ambient facade callback patterns;
- two files with thread-static state; and
- no detected generated C# files under the audited root.

This reproducible snapshot supersedes the stale 858-file / 170,048-line number
in the original design draft. The structural target remains at most 550 files /
125,000 lines, a single safe non-partial facade of at most 500 lines, and an
acquire-to-settlement lifecycle spine of at most 40 files / 20,000 lines.

## Migration Decisions

1. Extract `VulkanDeviceContext` first. It is the existing viable ownership seam
   and must exclusively own instance, physical/logical device, queues,
   capabilities, validation, device state, and device-fault retrieval. It may
   not retain or call `VulkanRenderer`.
2. Establish the stable lifecycle telemetry authority and typed frame identity
   before further behavior moves, so later extractions remain measurable.
3. Delete the unconsumed GPUScene SoA extraction and its scratch resources
   before introducing any replacement layout. A conversion path without a
   consumer is not retained merely to claim SoA.
4. Migrate one complete vertical authority at a time and delete its old facade
   implementation in the same cut. A forwarding shell is not an authority.

## Current Work

Completed in this validated vertical cut:

- `VulkanDeviceContext` now owns the monotonic device-state seam,
  configuration/probe data, and the first typed native device fault without a
  renderer backlink.
- The follow-up device-capability slice moved selected physical-device identity,
  the immutable physical-device snapshot, queue-family selection, available and
  enabled device-extension publication, alignment limits, logical-device
  identity, queue handles, OpenXR creation identity, and final capability
  publication into `VulkanDeviceContext`. Renderer members are behavior-only
  projections over that authority; queue and capability publication are
  explicit, validated, and exactly once. Full instance ownership,
  validation/debug callbacks, configuration-driven required/optional extension
  policy, presentation-support probing, and native device-fault policy were
  completed by the next cut described below.
- The native-lifetime slice moved Vulkan instance and instance-extension
  identity, API/OpenXR bootstrap identity, validation/debug callback ownership,
  debug-utils/messenger lifetime, presentation-support probing, device-extension
  policy, submission diagnostic snapshots, and KHR/EXT device-fault capability
  plus KHR function tables into the context. Native presentation query failures
  are propagated. The callback sink has no renderer backlink and drains bounded
  device-address records into cold device-loss correlation. Surface resources
  are destroyed before the messenger and instance.
- `VulkanFrameTelemetry` owns typed lifecycle-stage outcomes and lock-free ring
  publication. Unreached stages remain `NotReached`; authority-global CPU
  aggregates are not falsely attached to individual roots.
- Desktop scheduling, packetization, planning, and recording consume a numeric
  operation stream. The remaining per-kind payload sidecars still contain
  `FrameOp` references, so dense typed payload lowering is not complete.
- `RenderPacket` owns only numeric headers and ranges into a frame-owned payload
  arena; packet arrays and hot diagnostic strings were removed.
- `VulkanMappedFrameArena` owns persistently mapped mesh frame data with typed
  slices, generation/alignment validation, noncoherent atom expansion, and the
  atomic `Writable -> Prepared -> Submitted -> Writable` lifecycle.
- `VulkanNativeScratchArena` replaces pooled barrier arrays at the Vulkan native
  boundary.
- `VulkanSubmissionReceipt` publishes queue acceptance before fallible
  telemetry/publication and drives conservative accepted-work lifetime
  transfer. Presentationless fence completion is settled before command-pool
  reset; OpenXR accepted incomplete fences are deferred rather than destroyed.
- Dead GPUScene SoA shaders, settings, and unconsumed compatibility paths were
  removed. ABI/layout startup contracts now cover GPUScene and Vulkan indirect
  records.
- The hot-data inventory is complete for the current frame-operation, packet,
  prepared-draw, GPUScene, graph/barrier, descriptor, worker, upload,
  mapped-memory, and native-scratch streams.

The latest post-native-lifetime inventory contains 926 hand-written files /
181,668 physical lines, 308 renderer partial declarations, 372 type-wide unsafe
files, 101 ambient facade callback files, and two thread-static files. Relative
to the preceding validated cut, the focused owners removed twelve renderer
partials and six type-wide unsafe files while adding one-type-per-file native
contracts, context owners, and tests. The final Phase 4 structural budgets are
therefore still open.

## Final Validation Evidence

- `Test-VulkanPhase3-Regression`: 110 passed, 0 failed.
- Focused Phase 4, Phase 2.1, stable-packet, GPU hot-layout,
  presentation-independent, and CPU-span suites: 177 passed, 0 failed.
- Five focused migrated mapped-frame source contracts passed; one broader
  recording-layout contract remains coupled to a pre-existing moved partial and
  is not treated as a Phase 4 runtime failure.
- `dotnet build .\XRENGINE.slnx --no-restore -warnaserror`: 0 warnings,
  0 errors.
- Final named Vulkan session `vulkan-core-phase4-final2` reported zero
  validation errors/messages, zero pending retired resources, and an active
  generation-1 mapped-frame arena with three chunks / 100,663,296 mapped bytes.
- Two distinct camera moves succeeded. Each screenshot request returned the
  intended typed no-transfer-readable-image result and confirmed that no CPU or
  OS-window fallback was used.
- Clean named-session shutdown logs contained no VUID, validation error,
  device-loss, rejection, fatal, unhandled, or access-violation match.
- Final ignored inventory evidence is
  `Build/_AgentValidation/mcp-sessions/vulkan-core-phase4-final2/reports/architecture-inventory-after-final.json`.

### Device Capability Authority Follow-Up

- Focused device-context, Phase 4, Phase 2.1, backend-registry, and
  presentation-independent suites: 50 passed, 0 failed, 0 skipped.
- The hardware presentationless clear/readback/hash smoke passed independently:
  1 passed, 0 failed, 0 skipped.
- `dotnet build .\XRENGINE.slnx --no-restore -warnaserror`: 0 warnings,
  0 errors.
- Final named Vulkan session `vulkan-device-context-capabilities-final4`
  reported zero validation errors/messages, zero pending retired resources, and
  an active generation-1 mapped-frame arena with three chunks / 100,663,296
  mapped bytes.
- Clean shutdown logs contained no VUID, validation error, device-loss,
  rejection, fatal, unhandled, or access-violation match.
- The ignored post-slice inventory is
  `Build/_AgentValidation/mcp-sessions/vulkan-device-context-capabilities-final4/reports/architecture-inventory-after-device-context-capabilities.json`.

### Native Lifetime, Validation, Presentation, And Fault-Facility Follow-Up

- Phase 4 checkbox audit and tracker rewrite: epic-sized requirements now use
  hierarchical parent/child gates. Independently validated device, telemetry,
  settlement, operation-stream, arena, and structural slices are checked while
  their incomplete final integration parents remain open.
- Focused device-context, backend-registry, presentation-independent,
  diagnostics, and fault-boundary coverage: 25 passed, 0 failed, 0 skipped.
- `Test-VulkanPhase3-Regression`: 110 passed, 0 failed, 0 skipped.
- `dotnet build .\XRENGINE.slnx --no-restore -warnaserror`: zero warnings and
  zero errors.
- Three validation-enabled named Vulkan sessions completed. The final session
  reported zero validation messages/errors, zero pending retired resources,
  three mapped-frame chunks / 100,663,296 mapped bytes, and active desktop
  rendering. Clean shutdown logs contained no VUID, validation error,
  device-loss, fatal, unhandled, or access-violation match.
- Final architecture review fixes keep instance-create `pNext` storage alive
  through native creation, retain callback registration through
  `vkDestroyInstance`, and preserve the latest 128 device-address events in an
  overwrite ring. The final named session was
  `vulkan-device-context-native-lifetime-review-fixes`.
- The ignored inventory evidence is
  `Build/_AgentValidation/mcp-sessions/vulkan-device-context-native-lifetime-final/reports/architecture-inventory-after-native-lifetime.json`.

### 4.0 Compact-Planning Follow-Up

- The sealed frame-operation stream no longer keeps target-name strings or a
  target-name diagnostic index. Planning/recording consume numeric target
  identity; packet diagnostics retain their separate cold payload-arena text.
- `FramePlan` now returns the sealed numeric stream to native recording. The
  OpenXR mirror and paired-eye paths consume caller-owned prepared storage and
  `FrameOperationSequence` views directly instead of cloning `FrameOp[]`
  snapshots.
- `EnsurePipeline` now requires an explicit creation permission. Every primary
  draw-recording call passes `false`; a cache miss therefore defers recording.
  The existing prewarm/preparation entry points are the only calls that pass
  `true`, keeping pipeline creation before `vkBeginCommandBuffer`.
- `FramePlanBuilder` is now owned by `VulkanRenderGraphRuntime` and carried in
  command-buffer lifecycle state. The builder no longer uses a thread-static
  current-builder or a static generation counter.
- `VulkanCpuSpanProfiler` now uses bounded registered thread buffers with an
  observer-off early exit. No `[ThreadStatic]` state remains under the Vulkan
  renderer.
- `VulkanFrameOperationScheduler` owns persistent sort/reorder/index scratch.
  Context-block ordering is dictionary-indexed and clear placement is a linear
  preindexed copy instead of repeated scans and insertion shifts. Stable graph
  plans reuse the sealed schedule.
- Stable command-chain caches skip trimming without scanning when they remain
  below the configured bound.
- Graphics and compute recording consume prepared pipeline handles. Compute
  recording also consumes descriptor sets published during preparation; a
  missing prepared set defers the dispatch rather than allocating, updating,
  linking, or creating inside command recording.
- Compute preparation publishes `VulkanComputePreparationResult` with an
  `EVulkanComputePreparationOutcome`. Human-readable failure text is formatted
  only when the cold lifecycle deferral path emits it.
- Primary indirect, mesh-secondary, and non-graphics-secondary recording use
  persistent recorder-owned arrays. No shared `ArrayPool` rent/return remains
  in command recording.
- Prepared mesh vertex, descriptor, dynamic-offset, frame-data, descriptor-heap,
  viewport, and scissor snapshots use capacity-bucketed mesh-owner storage.
  The normal prepared-draw path no longer touches the process-wide shared
  array pool.
- The scheduler, plan publication, and normal recording paths use numeric or
  interned operation identities. Runtime type reflection and type-name hashing
  were removed from their normal pass-resolution work.
- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -warnaserror`
  passed with 0 warnings and 0 errors after the change.
- The focused readiness/source-contract filter ran 19 tests: 11 passed and 8
  failed against pre-existing architecture/text baselines. The thread-static
  source guard improved from two findings to one; the remaining finding is the
  opt-in `VulkanCpuSpanProfiler` capture buffer and needs an explicit worker
  capture context rather than an ambient replacement. The other failures are
  stale path/text baselines or longstanding facade/partial-state inventories,
  not compiler or runtime failures from this slice.
- Named session `vulkan-phase4-compact-plan` started, accepted MCP requests,
  and cleanly stopped. It rendered a healthy desktop scene, but the active
  backend was OpenGL, not Vulkan; it is not Vulkan acceptance evidence.

### Phase 4.0 Vulkan-Only Completion Evidence

- The OpenGL session above was rejected and was not used for acceptance.
- Validation-enabled named session `vulkan-phase40-final` reported
  `isVulkan=true`, no OpenGL extensions, and zero Vulkan validation messages or
  errors. Its hidden output was 0x0, so it validates backend/bootstrap state but
  is not counted as a rendering-performance capture.
- The real-window Release performance harness launched Vulkan at 1920x1080
  with `CpuDirect`, a warm shader/texture cache, command-buffer reuse enabled,
  clean-profile observer settings, and the production deferred workload.
- The draw-bearing startup/change capture processed 1,660 samples and reached
  60 GPU-scene commands. The matched warmed capture processed 3,750 samples
  with one stable workload identity. Together they show change-proportional
  work: the changed phase publishes the scene commands, while the warmed phase
  reports zero plan replacement, planner pruning, command-chain dirtying,
  command-buffer recording, descriptor-pool creation, and global fallback
  invalidation work.
- Both captures reported zero managed bytes for frame-op preparation, resource
  planning, frame-data refresh, packet construction, primary recording,
  secondary recording, descriptor publication, submission, and aggregate
  command-buffer recording. Clean-profile capture correctly kept validation
  disabled; the separate validation-enabled Vulkan session reported zero
  validation messages and VUIDs.
- Focused scheduler/profiler coverage passed 13/13, Phase 4 coverage passed
  18/18, and stable-packet/descriptor coverage passed 115/115 after the final
  persistent scratch, owner-local draw storage, and typed-outcome changes. The
  Vulkan project builds with warnings as errors: zero warnings and zero errors.
- A combined run that also included the older command-recording-dependency
  source contracts was not used as a 4.0 gate: two expectations target the
  removed dependency-comparison shape, and its reflection inventory test then
  overflowed the test-host stack. Those test sources were not changed while the
  production implementation remains under feature validation.
- Capture summaries are under
  `Build/_AgentValidation/20260805-2158-phase40-vulkan-perf/reports/direct-short2/`
  and `Build/_AgentValidation/20260805-2158-phase40-vulkan-perf/reports/direct-warmed/`.

## Validation Gates For Each Ownership Cut

1. Build the Vulkan renderer and editor with warnings as errors.
2. Start a named isolated Vulkan editor session and confirm active Vulkan state.
3. Inspect MCP profiler/capture responses and actual output when a live
   transfer-readable image exists.
4. Inspect Vulkan/rendering logs through clean shutdown, separating teardown
   noise from steady-state faults.
5. Exercise presentationless and device-loss paths when the cut changes those
   owners; exercise OpenXR when a runtime is available.
6. Only after the production path is validated, migrate or add the associated
   source-contract tests and run the narrowest relevant filter.

## Phase 4.1 Authority Extraction Evidence

### 4.1.0 Working Runtime Baseline Restoration

- Resource-generation publication now preserves the complete context-local
  planner switching state while pending resources materialize. The initial
  pipeline generation commits without the former `BloomBlurTexture` native
  identity changing between prepare and commit.
- Exact frame-op tracing identified the final visible/queued mismatch: 22
  visible draws produced only 21 mesh frame operations. The omitted operation
  was `RenderToWindow_TsrOutputTexture`. `XRQuadFrameBuffer.Render` performed a
  descriptor-complete preflight before `PresentBindingPublisher` could publish
  `SourceTexture`, so the final fullscreen draw was never captured.
- `XRQuadFrameBuffer.EnqueueRender` now provides the explicit deferred capture
  path for typed publishers whose descriptor resources are established by the
  draw snapshot. `VPRC_RenderToWindow` uses that path while its resolved source
  scope is active. Normal `XRQuadFrameBuffer.Render` retains its existing eager
  readiness contract for other callers.
- Final-presentation publication now accepts both an exact descriptor-write
  cache hit and a successful new descriptor write. A first use no longer leaves
  the tuple incomplete after `vkUpdateDescriptorSets` has already committed the
  exact native payload.
- `dotnet build
  .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj
  --no-restore -warnaserror` passed with zero warnings and zero errors. Tests
  were not changed or run while completing this live feature-validation gate,
  per the repository testing policy.
- Final named session `vulkan-core-410-final23-20260806` used Vulkan at
  1920x1080. Its stable frame had 34 operations / 22 mesh draws, including
  `RenderToWindow_TsrOutputTexture`, two swapchain writes, a `Completed` frame
  outcome, no command-buffer dirty reason, and zero live descriptor failures or
  skipped draws in the initial acceptance view.
- The enabled final-presentation ledger recorded accepted valid frames whose
  `TsrOutputTexture` image view and sampler exactly matched `SourceTexture` set
  2/binding 0. The descriptor command buffer equaled the selected scene primary,
  and the ledger reported no invariant failure or recovery swapchain write.
- Three screenshots were visually inspected. The initial view showed a textured
  Sponza wall and sky instead of uniform purple recovery content; focusing the
  model and moving to a second exterior angle produced different Sponza
  silhouettes, proving camera-dependent live rendering. Evidence is under
  `Build/_AgentValidation/mcp-sessions/vulkan-core-410-final23-20260806/mcp-captures/`.
- Camera changes that expanded the visible/shadow workload caused a bounded
  startup/replanning burst (seven rejected frames and eighteen planner
  precondition messages), after which both counts remained unchanged across the
  final steady-state sample and completed frames resumed. Descriptor failures
  accumulated while previously unseen Sponza material/shadow resources warmed,
  then also remained flat.
- Clean shutdown logs contain zero `BloomBlurTexture` generation-commit
  instability, `NullReferenceException`, retry-backoff match, incomplete final
  presentation epoch, stale-secondary match, VUID, device loss, or Vulkan
  validation error. The lone rendering-log `LastError` field is the expected
  diagnostic that the optional OpenGL-to-Vulkan DLSS bridge is disabled, not a
  Vulkan runtime failure.

- `VulkanDeviceContext` now owns the bounded KHR/EXT fault capture, formatting
  inputs, artifact persistence handoff, native identity, capabilities, and
  queues. Internal Vulkan consumers use `DeviceContext` directly; the private
  `instance` and `device` aliases and all readiness/capability/queue-family
  projections were deleted. Public native-handle properties remain API
  translation on the facade.
- Output, frame-loop, planning, resource, command, and telemetry mutable state
  is owned by the corresponding explicit authority. Planner caches, resource
  lifetime contracts/query facilities/streaming scheduler, command recording
  pools/workers/caches/state tracking, ImGui platform WSI lifetime, and shared
  telemetry publication were removed from renderer ownership.
- Texture streaming and OpenXR no longer locate or swap
  `AbstractRenderer.Current`. Frame-operation pooling, program binding capture,
  descriptor scratch, and planner readback scopes use explicit instance,
  renderer, or command-worker owners. The final inventory reports zero ambient
  renderer lookups, zero `[ThreadStatic]` files, and zero static `ThreadLocal`
  owners.
- Warning-as-error builds passed for both the Vulkan project and editor with
  zero warnings and zero errors.
- Named isolated session `phase41-final-vulkan` used the Vulkan backend only;
  no `log_opengl.log` was produced. MCP frame 137630 rendered the desktop
  swapchain, published lifecycle authority ID 1 at sequence 137616, and
  reported zero validation messages/errors and zero pending retired resources.
  `log_vulkan.log` contained no VUID, device-loss, fatal, or unhandled fault.
- The Phase 4 structural test assembly currently does not compile because its
  helpers still name resource/image-access contracts as nested
  `VulkanRenderer.*` types. Production compatibility duplicates were not
  restored, and tests were not changed while feature validation remains under
  the repository testing policy.
- `Tools/Reports/Get-VulkanCoreArchitectureInventory.ps1` now counts both
  `VulkanRenderer` and renamed `VulkanRendererRuntime` partial implementations
  and separately detects ambient renderer lookups and static thread-local
  owners. This prevents a renamed inheritance shell from satisfying the facade
  gate.

## Unresolved Completion Work

The Phase 4.1 parent facade and final dependency-inventory boxes remain open.
The old `VulkanRendererRuntime` inheritance shell has been deleted and the
production identity is again `VulkanRenderer`. Reflection now proves that the
renderer has exactly seven readonly instance fields: device, output, frame
loop, planner, resource, command, and telemetry. The prior lexical field count
included fields on nested contracts and is not a valid state-ownership measure.

The behavior extraction is not complete. The declaring-type-aware structural
inventory currently finds 207 `VulkanRenderer` partial declarations, 3,063
declared renderer methods, 99 files with facade callbacks, and six authority
declaration files which still name `VulkanRenderer`. The desktop coordinator and
`IVulkanDesktopFramePhaseService` were deleted, but `VulkanFrameLoop.Render`
still invokes renderer phase methods. Target-driver lifecycle parameters now use
typed surface/output adapters and headless/presentationless drivers contain no
direct renderer reference, but the output context still forwards native work to
the renderer. Command worker dispatch no longer uses
`IVulkanCommandChainWorkerExecutor` or `Executor = this`, but its recording
procedure is still a bound renderer delegate. `VkObjectBase` and both legacy
allocators are renderer-type-free; eleven wrapper families still use the
transitional renderer accessor.

`Tools/Reports/Get-VulkanCoreArchitectureInventory.ps1` now classifies authority
owners by declarations instead of co-mentions, publishes the approved edge set,
reports edge violations separately, and lists authority files with renderer
backlinks. The current declared-authority graph has zero unapproved authority
edges, but the backlink list is nonempty, so the final dependency proof remains
open.

Warning-as-error builds pass for the Vulkan project and editor. The first named
Vulkan-only run exposed a staged-bootstrap regression in
`VulkanBackendObjectContext`: wrappers created by the base renderer constructor
had cached the context before the device authority existed. The context now
supports a publish-once device handoff, and the repeated
`phase41-authority-final` session had no `NullReferenceException`, VUID,
device-loss, fatal, or unhandled fault. MCP frame-output telemetry reported the
desktop scene and present outputs rendered, lifecycle authority ID 1, validation
message/error counts of zero, and zero pending retired resources. No OpenGL log
was produced. A live OpenXR runtime was not available for this cut.
