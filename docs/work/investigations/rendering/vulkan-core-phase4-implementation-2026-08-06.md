# Vulkan Core Phase 4 Implementation

Date: 2026-08-06  
Status: Phase 4.0 and Phase 4.1 complete; Phase 4.2+ active
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

## Phase 4.1 Completion

Phase 4.1 closed on 2026-08-10. The production identity is one non-partial,
442-line `VulkanRenderer` facade. It owns exactly seven readonly authority-root
fields: device, output, frame loop, planner, resource, command, and telemetry.
All 207 former renderer partial declarations and their implementation behavior
were rehomed behind those authorities or focused typed ports.

The final cut also removed type-keyed and opaque planner/command state,
renderer-nested planner contracts, renderer-backed wrapper construction, and
facade callback/backlink paths. Deferred mesh requests now freeze their pass,
pipeline, frame context, producer target/raster state, and typed deferred
binding publication before the frame loop consumes them. This restored render
graph target attribution and preserved light-combine bindings after the
producer scope ended. Resource generation is command-scoped during publication,
image-backed textures can adopt a newly planned physical group, cold shader
creation crosses the resource authority, and final presentation descriptor and
readback publication use explicit typed ports.

The declaring-type-aware inventory archived at
`Build/_AgentValidation/20260809-phase41-facade-close/reports/architecture-inventory-final.json`
reports one non-partial renderer declaration, zero partial declarations, seven
authority-root fields, zero unapproved authority dependency edges, zero
renderer backlinks, zero facade callbacks, zero ambient renderer lookups, and
zero thread-static or ambient-thread-state escape hatches. The conservative
retained-type graph separately reports 22 multi-authority types and 28 broad
advisory flags; those are candidates for later hardening and are not substituted
for the exact Phase 4.1 dependency gates.

Warning-as-error builds passed with zero warnings and zero errors. Named isolated
Vulkan session `phase41-final-20260810` committed generation 1 with 51 textures
and 59 framebuffers. Two visually inspected screenshots at different camera
positions showed camera-dependent Sponza geometry and cyan debug overlays;
readback used alternating slots 0 and 1 with `R16G16B16A16Sfloat`. Steady-state
Vulkan/rendering logs contained no VUID, validation error, exception, device
loss, or frame failure. A single bounded startup rejection occurred while the
render pipeline warmed and did not recur. No tests were added or run because
live feature validation precedes test work under the repository policy.

### 4.1.1-4.1.4 Facade-Spine Completion Investigation

The final authority cut initially compiled but produced a black desktop frame.
RenderDoc captures at frames 200 and 1000 contained the fullscreen/post-process
draws but no geometry, proving the fault was upstream of presentation. Source
and live-log correlation found four authority-migration regressions:

- asynchronous mesh index-buffer wrappers were looked up but no longer created,
  leaving draw preparation permanently in `BuffersPending`;
- desktop command recording confused the sealed frame-plan slot with the
  swapchain descriptor slot;
- the dynamic-UI secondary path received the producer array instead of the
  frame plan's sealed operation sequence; and
- command encoders captured the Vulkan API before staged backend construction
  had published the device authority.

The fixes restore wrapper creation through `VulkanBackendObjectContext`, begin
prepared-frame recording with the plan-owned frame slot while retaining the
acquired image for descriptor publication, pass the sealed dynamic-overlay
sequence through reuse and recording, and create command encoders lazily after
authority publication. Related cleanup also moved compute-descriptor retirement
to `VulkanResourceRuntime`, corrected mapped-frame slot capacity, and routed
tracked command-buffer completion through `VulkanCommandRuntime`.

Named Vulkan session `phase4-core-hardening-final` subsequently rendered the
scene through a 32-operation frame plan. Two visually inspected screenshots at
different camera positions showed camera-dependent Sponza geometry and cyan
debug overlays. Transfer readback succeeded from alternating slots 0 and 1 in
`R16G16B16A16Sfloat`. Startup planner/presentation deferrals settled, and one
later asynchronous partition deferral recovered on the following frames rather
than becoming a persistent rejection. The old frame-slot mismatch, unsealed
secondary, null-reference, queued-submission, and black-output signatures did
not recur. Evidence is under
`Build/_AgentValidation/mcp-sessions/phase4-core-hardening-final/`.

The authority extraction following that live diagnosis removes the frame-loop
facade callback, renderer-backed output and ImGui viewport lifetimes,
renderer-backed resource wrappers and pipeline contracts, and command-worker
renderer retention. Final post-extraction build, structural, and live-session
evidence completed on 2026-08-08. A final clean rebuild first exposed one
staged-bootstrap boundary error: the resource runtime attempted to publish a
null device context while the base renderer constructor was creating wrapper
identities. `VulkanResourceRuntime` now creates and publishes the renderer-free
backend-object context during that bootstrap phase, but defers device-dependent
service binding until the derived Vulkan constructor supplies the device
authority.

Both the Vulkan project and editor then built with warnings treated as errors
and reported zero warnings and zero errors. The rebuilt named session
`phase4-core-hardening-final` reached MCP readiness and rendered two inspected,
camera-dependent views. The captures used alternating readback slots 0 and 1
with `R16G16B16A16Sfloat`; neither was black. Steady-state Vulkan and general
logs contained no null-context exception, frame-slot mismatch, unsealed
secondary sequence, VUID, validation error, fatal error, or device loss. Final
captures are in the session's `captures-final/` directory. These results close
the implementation and integration gates for 4.1.1 through 4.1.4; later Phase
4.1 sections remain outside this cut.

### Post-Completion Performance and ImGui Regression Check

The first interactive review after the 4.1.1-4.1.4 cut reported an extremely
low frame rate and no ImGui editor. These are direct runtime regressions and
should be diagnosed before continuing the hardening sequence; later facade,
scheduling, and observability items do not themselves restore the missing UI or
remove the measured command-recording cost.

Live profiler snapshots from the rebuilt `phase4-core-hardening-final` Vulkan
session measured a 608.68 ms representative frame (1.71 Hz) and a later 172.78
ms frame (4.80 Hz). In the later sample, Vulkan command recording consumed
165.05 ms while presentation itself consumed 0.10 ms. The dominant CPU stages
were primary command encoding at 98.90 ms with 2,399,776 bytes allocated, frame
operation dispatch at 83.59 ms with 2,001,152 bytes allocated, primary prewarm
at 29.75 ms with 892,888 bytes allocated, and prepared-draw construction at
24.51 ms with 698,392 bytes allocated. Signature and command-buffer reuse paths
also allocated approximately 2 MiB each per sampled frame. This localizes the
observed slowdown to repeated CPU-side frame-plan lowering, preparation, and
command encoding rather than swapchain presentation. Unit-testing settings had
mesh-bound and transform-debug rendering enabled, which increased the visible
wireframe work and clutter but did not account for the dominant measured CPU
cost.

ImGui initialization succeeded: the startup path created the Dear ImGui node,
loaded the font, enabled the Vulkan ImGui profile, and selected
`FullViewportBehindImGuiUI`. However, the sampled CPU frame never entered
`DearImGuiComponent.RenderImGui` or `EditorImGuiUI.RenderEditor`. Vulkan output
telemetry reported zero ImGui commands, and snapshot/overlay recording took
only microseconds. The UI render package also encountered a resource-generation
mismatch during startup. The current working boundary is therefore upstream of
the Vulkan ImGui backend: the hidden screen-space UI command that invokes the
Dear ImGui component is not reaching execution, so no renderable ImGui snapshot
exists for overlay admission.

RenderDoc frame 50 independently confirmed the result. The capture contains
124 draw calls and no ImGui draw pass. Its final secondary command buffer has a
single 332-triangle, 166-instance draw, exactly matching the engine's 166-glyph
dynamic performance-text overlay; the visually inspected final render target
contains that text and the cyan scene debug overlay but no editor panels. The
capture and exported before/after render targets are under
`Build/_AgentValidation/mcp-sessions/phase4-core-hardening-final/renderdoc-perf-imgui/`.
No corrective code change was attempted during this diagnosis checkpoint.

### Structured CPU Attribution and Frozen-Plan Correlation

The subsequent CPU investigation added opt-in nested command-chain timings and
exact recorded-key incompleteness provenance. The detailed timers are gated by
`XRE_VULKAN_RECORDING_PROFILE_DETAIL`; the disabled path does not capture
allocation counters or format diagnostic text. Recorded packet diagnostics now
report the first incomplete program, descriptor-set, buffer, or render-target
identity instead of only returning an incomplete aggregate.

During sustained MCP camera interpolation, representative scene-command
recording rose to 240-345 ms. Packet lowering consumed 57-91 ms, including
29-50 ms of mesh compatibility/signature scanning, 1.5-2.6 ms of capacity
accounting, 1.9-4.2 ms of dependency aggregation, and 8-13 ms of exact recorded
key capture. Schedule evaluation consumed 29-55 ms and primary command encoding
73-119 ms. This rules out packet-capacity accounting as the primary bottleneck
and shows that camera motion is forcing complete schedule evaluation and primary
encoding rather than one expensive Vulkan API call.

The first-incomplete-field trace isolated a recurring compute packet at pass
`100065`: its logical snapshot contained one descriptor set, its pre-binding
recorded identity was incomplete, and no prepared command-chain authority was
published. Live secondary-eligibility counters then classified compute as
`BarrierPlanUnavailable` during camera motion. Broader log classification found
that this was not an isolated compute defect: many main-viewport passes were
unknown to the frozen barrier plan and therefore emitted conservative barriers.

A structured context/plan correlation diagnostic now records the operation's
pass name and context identity alongside the frozen plan revision, generation,
and pass count. The first rebuilt run proved the authority mismatch directly:
main-viewport operations used pipeline 10 / viewport 2149160 metadata containing
100 passes, while primary recording alternated between frozen graph generations
6 and 7 containing only 5-7 passes. Examples included `OpaqueDeferred`,
`OnTopForward`, and synthetic GTAO passes, all present in the operation context
but absent from the frozen plan. The warning uses one value-type payload and a
constant rate-limit key, avoiding the previous per-pass interpolated-key
allocation on every re-record.

The next corrective boundary is therefore the frozen planner input contract:
primary recording must consume the render-graph/barrier publication belonging
to each sealed frame-operation context, rather than the last globally published
context plan. Until that is corrected, non-graphics secondaries remain
ineligible, conservative barriers are emitted repeatedly, command-chain
prepared authority cannot settle, and the sub-1 ms CPU target is not attainable.

### Per-Context Frozen Render-Graph Authority Fix

The corrective boundary above was implemented and validated on 2026-08-09.
Each planner generation now freezes a render-graph plan only after resolving its
native image and buffer bindings. The sealed frame plan publishes an
allocation-free context-to-plan table and combined signature, so primary
preparation, queue-transfer scheduling, pipeline-manifest construction,
secondary eligibility, and primary operation recording all resolve the exact
plan owned by each operation context. A mixed-context frame is no longer
permitted to fall back to the latest globally published plan; the legacy
single-plan fallback is restricted to genuinely single-context frames.

This exposed two valid absent-resource cases during startup. Structural pass
metadata may still mention a buffer belonging to a disabled conditional feature
such as ReSTIR, and an external import such as `LightProbePositions` may have no
live resource in a scene without probes. The freezer now omits those unbound
barriers for that generation. Managed planner resources must still resolve to a
native allocation before publication, and a later resource-generation change
republishes the barrier when an optional resource becomes bound. This preserves
strict failure behavior for real planner defects without manufacturing barriers
for resources that do not exist.

The rebuilt named session `vulkan-frame-loop-cpu-fix` exercised two simultaneous
operation contexts while the camera moved. No `BarrierPlanUnavailable`, context
plan mismatch, unknown-pass fallback, graph-plan precondition failure, Vulkan
validation error, or engine exception occurred. The compatibility/signature
scan that previously consumed 29-50 ms measured 0 ms. Camera-motion scene
recording improved from the previous 240-345 ms range to 75.50 ms average and
109.35 ms maximum across 36 samples. Packet lowering fell from 57-91 ms to a
22.31 ms maximum, and primary native command encoding fell from 73-119 ms to a
46.75 ms maximum. The improvement is material, but it does not satisfy the
sub-1 ms CPU target.

After camera motion settled, 24 samples measured 3.15 ms average scene-command
recording, 3.39 ms average total command recording, and 3.87 ms average frame
time. ImGui recording averaged 0.204 ms and remained visibly present. An
independent CPU-frame dump captured the VPRC authoring program at only 0.298 ms,
which places the remaining camera-dirty cost after authoring, in Vulkan packet
lowering and native primary command encoding.

Visual inspection confirmed a lit, camera-dependent Sponza view rather than a
stale or black frame. A full-window capture confirmed the ImGui menu, transform
toolbar, hierarchy, inspector, and play controls composited over the scene.
Evidence is in
`Build/_AgentValidation/20260801-vulkan-command-recording-finish/mcp-captures/`
and the session logs are in
`Build/_AgentValidation/mcp-sessions/vulkan-frame-loop-cpu-fix/logs/`.

The next measured fix should prevent camera-only data changes from rebuilding,
lowering, and natively re-encoding structurally identical command artifacts.
Camera matrices and other per-frame values should refresh through frame-owned
data while the stable frame-operation topology, packet structure, prepared
chains, and secondary command buffers remain reusable. That boundary directly
targets the remaining 22.31 ms lowering and 46.75 ms encoding peaks rather than
returning to the now-eliminated graph-plan compatibility path.
