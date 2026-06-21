# Vulkan Parallel Command Chain Refactor TODO

Last Updated: 2026-06-18
Owner: Rendering
Status: Proposed
Target Branch: `vulkan-parallel-command-chain-refactor`
Design Doc: `docs/work/design/rendering/vulkan-parallel-command-chain-refactor-design.md`

## Purpose

Implement the Vulkan parallel command-chain architecture described in the design
doc. The end state is a Vulkan backend where independent views and passes can
produce immutable render packets, record command chains on worker threads, reuse
static scene command buffers across frames, and keep volatile overlay/text work
isolated from the main scene.

The refactor must preserve visual correctness first. Parallelism is useful only
after the single-thread packet/chain path produces the same output as the
current Vulkan renderer.

## Success Criteria

- [ ] Vulkan can build immutable visibility/render packets for main, shadow,
  overlay, and VR eye views.
- [ ] Vulkan can record normal mesh draw chains into secondary command buffers
  inside legacy render passes and dynamic rendering scopes.
- [ ] Static scene command chains are reused during camera movement.
- [ ] Dynamic UI/text/profiler changes record only small volatile chains.
- [ ] Render-thread primary recording is reduced to pass orchestration,
  barriers, secondary execution, queries, submit, and present.
- [ ] Worker-thread recording can be enabled for independent command chains.
- [ ] Parallel and single-thread modes produce equivalent screenshots and pass
  outputs.
- [ ] Release settled-scene `Vulkan.FrameLifecycle.RecordCommandBuffer` p50 is
  below 2 ms and p95 is below 5 ms on the target validation scene.
- [ ] No steady-state descriptor pool retirement, resource-plan replacement, or
  command-buffer structural dirtying occurs during ordinary camera movement.

## Guardrails

- [ ] Keep a single-thread command-chain mode available for bisection.
- [ ] Keep existing Vulkan GPU/accelerated paths explicit; do not hide failures
  behind OpenGL or CPU fallbacks.
- [ ] Do not mutate scene, material, descriptor, FBO, or resource planner state
  from visibility or recording workers.
- [ ] Do not create one secondary command buffer per tiny draw unless a measured
  case proves it is beneficial.
- [ ] Keep barrier planning centralized in the render graph/compiler path.
- [ ] Treat descriptor lifetime bugs as correctness blockers, not perf
  follow-ups.
- [ ] Add diagnostics before enabling parallel work by default.
- [ ] Every phase must have a visual gate and at least one targeted build/test
  gate.

## Feature Flags And Diagnostics

- [ ] Add `XRE_VULKAN_COMMAND_CHAINS=0/1` to enable the packet/chain path during
  migration.
- [ ] Add `XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD=1` to force deterministic
  single-thread chain recording.
- [ ] Add `XRE_VULKAN_COMMAND_CHAIN_VALIDATE=1` for expensive signature,
  descriptor generation, and resource-plan assertions.
- [ ] Add `XRE_VULKAN_COMMAND_CHAIN_TRACE=1` for first-dirty-reason chain logs.
- [ ] Add `XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING=1` as the final
  user-facing bisection flag once parallel recording exists.
- [ ] Surface command-chain counters in runtime stats, profiler packets, editor
  profiler UI, and NDJSON profile capture:
  - chains scheduled;
  - chains recorded;
  - chains reused;
  - chains frame-data-refreshed;
  - volatile chains recorded;
  - primary command buffers reused;
  - primary command buffers recorded;
  - chain worker record time;
  - render-thread wait-for-workers time;
  - first structural dirty reason;
  - first descriptor generation mismatch;
  - first resource plan revision mismatch.

## Phase 0 - Branch, Baseline, And Safety Net

- [ ] Create dedicated branch `vulkan-parallel-command-chain-refactor` from the
  current integration branch.
- [ ] Confirm the target validation scene and GPU configuration.
- [ ] Record a no-env Vulkan baseline using
  `Tools/Measure-VulkanFrameLoop.ps1`.
- [ ] Capture baseline MCP screenshots for:
  - main viewport;
  - `AlbedoOpacity`;
  - `Normal`;
  - `RMSE`;
  - `DepthView`;
  - `AmbientOcclusionTexture`;
  - `LightingAccumTexture`;
  - `HDRSceneTex`;
  - final AA/post-process output.
- [ ] Record baseline command-buffer cache stats, retired resource stats,
  descriptor pool retirement, resource-plan replacement, and frame-op census.
- [ ] Document any pre-existing visual defects separately so they do not block
  command-chain work unless they regress.
- [ ] Add a branch-local status section to this TODO after each implementation
  phase with evidence links.

Acceptance criteria:

- [ ] Baseline run directory and screenshot directory are linked in this TODO.
- [ ] Current visual defects are named and separated from refactor regressions.
- [ ] The branch exists before source changes begin.

## Phase 1 - Instrumentation And Command-Chain Metrics

- [ ] Add command-chain metric fields to `Engine.Rendering.Stats.Vulkan`.
- [ ] Extend profiler packet serialization/deserialization tests for the new
  fields.
- [ ] Surface command-chain counters in `EngineProfilerDataSource`.
- [ ] Add profiler UI rows/columns for command-chain reuse and worker timing.
- [ ] Extend NDJSON profile capture with command-chain metrics.
- [ ] Add first-dirty-reason aggregation for chains, matching the existing
  command-buffer dirty reason model.
- [ ] Add log throttling for chain diagnostics so validation mode is useful but
  not log-spammy.
- [ ] Update `Tools/Measure-VulkanFrameLoop.ps1` and
  `Tools/Measure-GameLoopRenderPipeline.ps1` summary output with the new
  counters.

Acceptance criteria:

- [ ] Existing profiler protocol tests pass.
- [ ] A no-behavior-change editor run reports zero command chains while the
  feature flag is disabled.
- [ ] Enabling trace flags without command chains does not crash or allocate
  unbounded logs.

## Phase 2 - Packet Data Model Without Behavior Change

- [ ] Add `RenderViewKind`.
- [ ] Add `RenderViewKey`.
- [ ] Add immutable `VisibilityPacket`.
- [ ] Add immutable `RenderPacket`.
- [ ] Add `DrawPacket` and `DispatchPacket` snapshots with only stable handles,
  IDs, and value snapshots.
- [ ] Add `RenderPacketVolatility`.
- [ ] Add `CommandChainKey`.
- [ ] Add `CommandChain`.
- [ ] Add `RenderPassChainGroup`.
- [ ] Add `CommandChainSchedule`.
- [ ] Use pooled backing arrays or frame-owned buffers for packet lists.
- [ ] Add debug-only ownership checks so packet memory cannot be returned to a
  pool before frame retirement.
- [ ] Add unit tests for equality/hash stability on keys and volatility values.

Acceptance criteria:

- [ ] The new types compile but are not yet used by the active renderer.
- [ ] Packet/key tests are deterministic.
- [ ] No new warnings are introduced.

## Phase 3 - Lower Existing FrameOps Into Packets

- [ ] Add a `FrameOp` to `RenderPacket` lowering path behind
  `XRE_VULKAN_COMMAND_CHAINS=1`.
- [ ] Preserve current render ordering exactly:
  - render graph pass order;
  - scheduling identity;
  - target grouping;
  - transparent draw ordering;
  - original same-pass ordering where required.
- [ ] Compute `StructuralSignature` for lowered packets.
- [ ] Compute `FrameDataSignature` for lowered packets.
- [ ] Add validation that lowered packet signatures explain the current
  frame-op signature.
- [ ] Add a packet dump mode for one frame of:
  - pass index;
  - target;
  - pipeline identity;
  - viewport identity;
  - draw count;
  - volatility;
  - structural signature;
  - frame-data signature.
- [ ] Keep actual command recording on the old path in this phase.

Acceptance criteria:

- [ ] With command chains enabled, packet dumps match the old frame-op census.
- [ ] With command chains disabled, behavior is byte-for-byte unchanged except
  for dormant code.
- [ ] No visual output changes.

## Phase 4 - Volatility Classification And Static/Dynamic Split

- [ ] Classify lowered packets as:
  - `StaticStructural`;
  - `FrameDataOnly`;
  - `DynamicCommand`;
  - `StructuralDirty`.
- [ ] Move UI text packet classification to `DynamicCommand`.
- [ ] Move profiler/ImGui/editor gizmo packets to `DynamicCommand` where
  practical.
- [ ] Remove camera matrices, model matrices, material constants, and other
  refreshable values from static structural signatures.
- [ ] Keep descriptor layout and descriptor set handle stability in structural
  signatures.
- [ ] Add diagnostics when a packet is classified dynamic because draw count,
  instance count, or descriptor handle count changed.
- [ ] Add tests for known volatility examples:
  - static mesh with moving camera;
  - static mesh with changing material constant;
  - UI text with changing instance count;
  - shader variant change;
  - FBO attachment format change.

Acceptance criteria:

- [ ] Dynamic UI/text no longer dirties static packet structural signatures.
- [ ] Static packet structural signatures remain stable during camera movement.
- [ ] Real structural changes still dirty the correct packets.

## Phase 5 - Resource Planner Freeze And Descriptor Snapshots

- [ ] Define the exact point where the Vulkan resource plan freezes for the
  current frame.
- [ ] Prevent worker recording from triggering resource planner replacement.
- [ ] Add descriptor/resource binding snapshots to `RenderPacket` or
  chain-record input.
- [ ] Track descriptor generation per chain.
- [ ] Track physical resource plan revision per chain.
- [ ] Track pipeline generation per chain.
- [ ] Convert descriptor refresh blockers into explicit chain dirty reasons.
- [ ] Ensure descriptor pool retirement is frame-slot/timeline based for any
  descriptor set referenced by reusable chains.
- [ ] Add validation assertions for stale descriptor sets, stale physical image
  handles, stale framebuffers, and stale pipeline handles.

Acceptance criteria:

- [ ] Resource planner changes invalidate affected chains explicitly.
- [ ] Workers can read a frozen plan without locks that serialize recording.
- [ ] No command buffer references retired descriptor pools in validation mode.

## Phase 6 - Chain Cache And Frame-Data Refresh

- [ ] Add a per-frame-slot command-chain cache.
- [ ] Add chain cache lookup by `CommandChainKey`.
- [ ] Track chain structural signature, resource plan revision, descriptor
  generation, pipeline generation, and profiler/query mode.
- [ ] Add a `TryRefreshReusableCommandChainFrameData` path for:
  - engine uniforms;
  - auto uniforms;
  - object transforms;
  - camera/view data;
  - material constants in refreshable buffers;
  - dynamic uniform/storage offsets.
- [ ] Keep the old command-buffer refresh path intact until chain refresh is
  proven.
- [ ] Add dirty reason values:
  - structure;
  - resource-plan;
  - descriptor-generation;
  - pipeline-generation;
  - profiler-mode;
  - frame-data-refresh-failed;
  - volatile-command.
- [ ] Add tests for chain cache reuse and frame-data refresh.

Acceptance criteria:

- [ ] A static scene with moving camera refreshes frame data without recording
  equivalent static chains.
- [ ] Dirty reason diagnostics identify the first non-reusable chain.
- [ ] Existing command-buffer cache metrics remain correct during migration.

## Phase 7 - Secondary Command Buffers For Graphics Draw Chains

- [ ] Extend secondary command-buffer helpers beyond blits/indirect draws.
- [ ] Support mesh draw recording in secondary command buffers.
- [ ] Add legacy render-pass inheritance:
  - render pass;
  - framebuffer;
  - subpass;
  - `RenderPassContinueBit`.
- [ ] Add dynamic-rendering inheritance:
  - color attachment formats;
  - depth attachment format;
  - stencil attachment format;
  - sample count;
  - view mask;
  - `RenderPassContinueBit`.
- [ ] Make primary pass begin support secondary-command-buffer contents where
  needed.
- [ ] Avoid mixing inline draw commands with secondary-only pass contents in a
  way that violates Vulkan validation.
- [ ] Add a minimal secondary graphics chain for one stable mesh pass.
- [ ] Add a volatile secondary chain for UI text/profiler overlay.
- [ ] Add validation logs for secondary inheritance mismatches.

Acceptance criteria:

- [ ] Vulkan validation logs contain no secondary-command-buffer inheritance
  errors.
- [ ] The minimal secondary mesh pass visually matches the old inline path.
- [ ] UI text/profiler overlay can record as a dynamic secondary without dirtying
  the static scene chain.

## Phase 8 - Primary Command-Buffer Orchestration

- [ ] Add primary recording from `CommandChainSchedule`.
- [ ] Emit centralized pass barriers before each chain group.
- [ ] Begin render pass/dynamic rendering for each group.
- [ ] Execute ordered secondary command buffers for the group.
- [ ] End render pass/dynamic rendering.
- [ ] Execute volatile overlay chains after main scene groups and before final
  present.
- [ ] Transition swapchain to present exactly once.
- [ ] Keep timing/GPU profiler query behavior correct.
- [ ] Keep primary command-buffer dirty reasons separate from secondary chain
  dirty reasons.
- [ ] Reuse primary command buffers when pass group structure and secondary
  handles are stable.

Acceptance criteria:

- [ ] Primary recording can run the command-chain path for a representative
  frame.
- [ ] Primary reuse is reported separately from secondary reuse.
- [ ] Dynamic secondary re-recording does not require primary re-recording when
  secondary handles are stable.

## Phase 9 - Single-Thread Command-Chain Renderer

- [ ] Route a full representative Vulkan frame through packets, chains,
  secondary recording, and primary orchestration on the render thread only.
- [ ] Keep `XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD=1` equivalent to this mode.
- [ ] Compare screenshots and pass outputs against the old path.
- [ ] Compare command-chain stats against old command-buffer stats.
- [ ] Add fallback kill switch to return to old frame-op recording during this
  phase.
- [ ] Fix all visual regressions before adding worker-thread recording.

Acceptance criteria:

- [ ] Single-thread command-chain output matches the old path.
- [ ] Logs contain no new Vulkan validation errors.
- [ ] Static/dynamic chain reuse works in single-thread mode.

## Phase 10 - Recording Worker Pool

- [ ] Add a bounded Vulkan recording worker pool.
- [ ] Add per-worker graphics command pools.
- [ ] Add optional per-worker compute command pools if compute chains are
  enabled later.
- [ ] Add per-worker scratch arenas for temporary sorting/binding data.
- [ ] Add worker-safe command-buffer bind-state tracking.
- [ ] Dispatch independent chain recordings after resource planning.
- [ ] Record worker wait time separately from worker record time.
- [ ] Add a deterministic mode that records workers in schedule order for
  debugging.
- [ ] Add cancellation/teardown handling for swapchain recreation and device
  loss.
- [ ] Ensure no command pool is reset while GPU work using its command buffers is
  still in flight.

Acceptance criteria:

- [ ] Parallel worker recording can be toggled at runtime or startup.
- [ ] Single-thread and parallel modes produce equivalent images.
- [ ] Worker recording improves high draw-count record p95 without adding
  visible hitches.

## Phase 11 - Parallel Visibility And Packet Build

- [ ] Freeze a read-only scene/render snapshot before visibility jobs begin.
- [ ] Build visibility packets in parallel for independent views.
- [ ] Add view jobs for:
  - main editor/game view;
  - VR left eye;
  - VR right eye;
  - directional shadow cascades;
  - point/spot shadow views where applicable;
  - reflection/probe views where applicable.
- [ ] Build render packets from visibility packets in parallel.
- [ ] Ensure scene graph and component access is read-only during worker
  collection.
- [ ] Add deterministic sorting after packet build so output order is stable.
- [ ] Add validation mode comparing parallel visibility output to single-thread
  output for selected scenes.

Acceptance criteria:

- [ ] Parallel visibility can be enabled independently from parallel recording.
- [ ] Main/shadow/VR packet counts are stable and deterministic.
- [ ] No scene mutation occurs from visibility worker threads.

## Phase 12 - VR And Shadow Chain Specialization

- [ ] Add explicit `RenderViewKind.VREye` handling in command-chain keys.
- [ ] Record separate left/right eye chains first.
- [ ] Validate VR eye ordering and swapchain/image ownership.
- [ ] Add explicit `RenderViewKind.Shadow` handling in command-chain keys.
- [ ] Record independent shadow map/cascade chains in parallel.
- [ ] Keep shadow atlas packing changes as structural dirty reasons.
- [ ] Add stale-tile/fallback shadow behavior to validation gates so parallel
  shadow recording cannot reintroduce one-frame shadow disable flicker.
- [ ] Evaluate multiview chain recording only after separate eye chains are
  correct and measured.

Acceptance criteria:

- [ ] VR eye chains can record independently without changing output.
- [ ] Shadow chains can record independently and reuse when light/caster sets
  are stable.
- [ ] Shadow-map visual gates pass while moving and settling the camera.

## Phase 13 - Optional Multi-Queue Scheduling

- [ ] Keep this phase disabled until single-queue command chains are correct and
  fast.
- [ ] Identify chains eligible for sidecar queue submission:
  - async compute culling/compaction;
  - async skinning/blendshape compute;
  - transfer uploads;
  - shadow rendering on a second graphics queue if hardware benefits.
- [ ] Add queue dependency nodes to the command-chain schedule.
- [ ] Add timeline semaphore waits/signals per sidecar submission.
- [ ] Add queue-family ownership transfers where needed.
- [ ] Add single-queue fallback for every multi-queue schedule.
- [ ] Add queue overlap diagnostics and GPU timestamp ranges.

Acceptance criteria:

- [ ] Multi-queue mode is never required for correctness.
- [ ] Multi-queue mode produces equivalent images.
- [ ] Multi-queue mode is kept only where measurements show a real win.

## Phase 14 - Validation, Documentation, And Cleanup

- [ ] Run `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- [ ] Run command-chain unit tests.
- [ ] Run profiler protocol tests.
- [ ] Run render graph ordering tests.
- [ ] Run `Test-VulkanPhase3-Regression`.
- [ ] Run Unit Testing World Vulkan with MCP enabled.
- [ ] Capture visual gates from at least two camera positions.
- [ ] Export and inspect critical render targets:
  - `AlbedoOpacity`;
  - `Normal`;
  - `RMSE`;
  - `DepthView`;
  - `AmbientOcclusionTexture`;
  - `LightingAccumTexture`;
  - `HDRSceneTex`;
  - final post-process/AA output.
- [ ] Run `Tools/Measure-VulkanFrameLoop.ps1` in Release.
- [ ] Compare against the Phase 0 baseline.
- [ ] Update `docs/architecture/rendering/vulkan-renderer.md` with the final
  command-chain contract.
- [ ] Update `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`
  with the new render-thread/worker split.
- [ ] Update `docs/architecture/rendering/mesh-submission-strategies.md` if
  command-chain volatility changes mesh strategy contracts.
- [ ] Remove or retire obsolete frame-op recording code only after the
  command-chain path is the default and validated.
- [ ] Merge `vulkan-parallel-command-chain-refactor` back into `main` after
  completion and validation.

Acceptance criteria:

- [ ] All required builds/tests pass or failures are documented as unrelated.
- [ ] Visual output remains correct.
- [ ] No new Vulkan validation errors appear in logs.
- [ ] Release measurements meet or move materially toward success criteria.
- [ ] Architecture docs describe the final implemented behavior.

## Suggested Implementation Order

1. Branch and capture baseline.
2. Add metrics and dormant data types.
3. Lower old frame ops into packets without changing rendering.
4. Classify volatility and split dynamic overlay/text work.
5. Freeze resource plans and descriptor snapshots.
6. Add chain cache and frame-data refresh.
7. Record graphics mesh chains as secondary command buffers.
8. Switch primary recording to schedule orchestration in single-thread mode.
9. Enable worker-thread secondary recording.
10. Move visibility and packet building to workers.
11. Specialize VR and shadow views.
12. Consider multi-queue only after single-queue wins are proven.
13. Validate, update architecture docs, retire obsolete paths, and merge back.

## Open Questions To Resolve During Implementation

- [ ] Should `FrameOp` become a compatibility lowering layer, or should it be
  removed after packets are stable?
- [ ] Which render-packet fields are allowed to be frame-data-only instead of
  structural?
- [ ] Which descriptor generations can be refreshed without command
  re-recording?
- [ ] How should transparent order be represented without blocking opaque
  parallelism?
- [ ] Should VR multiview be a first-class packet mode or a later optimization?
- [ ] Which shadow atlas changes should dirty only affected chains instead of
  all shadow chains?
- [ ] How should GPU profiler queries interact with reusable secondary command
  buffers?
- [ ] What is the minimum chain size where secondary command buffers beat inline
  primary recording?

