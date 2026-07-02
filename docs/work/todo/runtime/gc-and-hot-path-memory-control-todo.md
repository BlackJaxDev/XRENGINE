# GC And Hot-Path Memory Control TODO

Last Updated: 2026-07-02
Owner: Runtime / Rendering / Networking
Status: Active

This tracker covers the work needed to make managed allocation and garbage
collection behavior predictable enough for editor, desktop runtime, VR, server,
network replication, ECS, animation, and render hot paths. The goal is not to
manually micromanage every collection. The goal is to make hot-path allocation
rare, visible, budgeted, and testable, with explicit GC policy chosen per run
mode.

## Research Sources

- [.NET garbage collector runtime config](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- [.NET latency modes](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)
- [`GC.AddMemoryPressure`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.addmemorypressure)

## Primary Code And Docs

- `Directory.Build.props`
- `XREngine.Editor/XREngine.Editor.csproj`
- `XREngine.Server/XREngine.Server.csproj`
- `XREngine.VRClient/XREngine.VRClient.csproj`
- `XREngine/Core/Time/EngineTimer.cs`
- `XREngine/Engine/Subclasses/Engine.ThreadAllocationTracker.cs`
- `XREngine.Data/ResourcePool.cs`
- `XREngine.Data/Profiling/UdpProfilerSender.cs`
- `XREngine/Scene/Components/Networking/`
- `docs/work/design/runtime/generic-ecs-avatar-networking-design.md`
- `docs/developer-guides/runtime/job-system.md`

## Current State

- Engine hot loops already have coarse allocation telemetry:
  `EngineTimer` records per-thread allocation deltas for render,
  collect/swap, update, and fixed update when
  `EnableThreadAllocationTracking` is enabled.
- `Engine.ThreadAllocationTracker` keeps rolling allocation snapshots for the
  profiler panel and UDP profiler.
- The generic ECS/avatar networking design already calls for unmanaged hot
  stores, pooled temporary buffers, per-system allocation counters, and no
  managed per-frame allocations.
- There is no central GC policy layer or named runtime memory profile for
  editor, VR, server, benchmark, or published game modes.
- The executable project files do not currently declare explicit GC runtime
  settings such as server/workstation GC, background GC, heap count, or DATAS
  policy.
- `ResourcePool<T>` is useful as a general-purpose thread-safe pool, but its
  `ConcurrentBag` and `Count` usage are not ideal for tight render, network,
  or ECS loops.
- Some generic networking components allocate per message or snapshot. This is
  acceptable for tool/generic components, but not for high-rate pose,
  replication, or realtime avatar channels.
- Profiler UDP sending preallocates the outer send buffer but still serializes
  packets through allocation-producing APIs in the steady-state send path.

## Goals

- Choose GC behavior explicitly per executable and run mode.
- Keep render, collect/swap, update, fixed update, VR input, network
  replication, ECS systems, animation/IK solve, and GPU-upload preparation at
  zero or bounded allocations after warmup.
- Extend allocation telemetry from coarse engine threads to systems, render
  passes, network codecs, and important runtime services.
- Provide frame-local scratch memory and pooled buffer APIs that feature code
  can use consistently.
- Make allocation regressions visible in tests, profiler UI, logs, and MCP or
  profiler captures.
- Schedule any intentional full GC or LOH compaction only in explicit
  maintenance windows such as scene unload, asset import completion, editor
  mode transitions, or benchmark setup/teardown.
- Keep native/GPU memory lifetime deterministic through disposal and explicit
  resource retirement, with `GC.AddMemoryPressure` used only where it is
  genuinely appropriate.

## Non-Goals

- Do not call `GC.Collect()` from render, update, collect/swap, VR input,
  network receive/send, or ECS hot paths.
- Do not use `NoGCRegion` as a blanket frame-loop wrapper.
- Do not hide hot-path allocation problems behind more aggressive GC tuning.
- Do not replace all existing general-purpose collections in one sweep.
  Prioritize measured hot paths.
- Do not force editor/tooling components to obey realtime network or render
  allocation rules unless they run inside a hot path.
- Do not silently downgrade GPU or accelerated paths to CPU fallbacks as a
  memory-control strategy.

## Phase 0 - Branch, Baseline, And Allocation Map

- [ ] Create a dedicated implementation branch, for example
  `runtime-gc-hot-path-memory-control`.
- [ ] Capture a baseline editor run with allocation tracking enabled:
  - default ImGui editor startup.
  - unit-testing world steady state.
  - representative camera movement.
  - one model import or asset-load scenario.
- [ ] Capture a baseline VR run where hardware is available:
  - OpenVR current tested path.
  - OpenXR/SteamVR when the parity lane is ready enough.
  - no-HMD OpenXR/Monado smoke where useful.
- [ ] Capture a baseline network/avatar stress run or synthetic proxy:
  - hundreds of represented avatars.
  - pose packet encode/decode.
  - interpolation buffer update.
  - render-output preparation.
- [ ] Record current allocation totals from:
  - render thread.
  - collect/swap thread.
  - update thread.
  - fixed-update thread.
  - profiler sender.
  - relevant network receive/send loops.
- [ ] Produce a short allocation map that classifies findings as:
  - hot-path blocker.
  - warmup-only churn.
  - editor/tooling-only churn.
  - acceptable background allocation.

Acceptance criteria:

- [ ] The doc or linked validation note records baseline allocation numbers,
  run mode, renderer backend, VR runtime if any, and test scene.
- [ ] Hot-path allocation sources are ranked by bytes/frame and frequency.

## Phase 1 - Runtime GC Policy Profiles

- [ ] Add a central runtime memory/GC policy concept, for example
  `EngineMemoryPolicy`, with named profiles:
  - `EditorInteractive`.
  - `DesktopRuntime`.
  - `VRLowLatency`.
  - `HeadlessServer`.
  - `Benchmark`.
  - `PublishedDefault`.
- [ ] Decide which settings are compile/runtimeconfig settings and which are
  runtime `GCSettings.LatencyMode` settings. Document the split.
- [ ] Add executable-specific GC configuration where appropriate:
  - editor.
  - server.
  - VR client.
  - published game/client template.
- [ ] Benchmark workstation GC versus server GC for:
  - editor.
  - VR client.
  - headless server.
- [ ] If server GC is used for server or benchmark modes, test bounded heap
  count and affinity options so GC workers do not starve engine job workers.
- [ ] Keep background/concurrent GC enabled for latency-sensitive interactive
  modes unless measurement proves otherwise.
- [ ] Set `GCLatencyMode.SustainedLowLatency` only for steady-state
  latency-sensitive modes where allocation volume is already low enough.
- [ ] Add startup diagnostics that log:
  - current GC mode.
  - latency mode.
  - concurrent/background GC status if observable.
  - server/workstation GC status.
  - heap count/config variables where available.
  - selected XRENGINE memory profile.
- [ ] Add settings and environment-variable overrides for diagnostics and
  benchmarking, not as hidden behavior changes.

Acceptance criteria:

- [ ] Each executable has an intentional GC policy or an explicit note that the
  default runtime policy is currently preferred.
- [ ] Startup logs show the effective memory profile and GC mode.
- [ ] VR/editor latency testing does not regress under the selected policy.

## Phase 2 - Allocation Telemetry And Budgets

- [ ] Extend the existing thread allocation tracker to support named scopes:
  - runtime systems.
  - ECS systems.
  - render passes.
  - network codecs.
  - VR input update.
  - animation/IK solve.
  - GPU upload preparation.
- [ ] Add low-overhead debug-only allocation scopes using
  `GC.GetAllocatedBytesForCurrentThread()` around selected subsystems.
- [ ] Record allocation telemetry into the profiler panel and UDP profiler
  without introducing new steady-state allocations.
- [ ] Add per-scope rolling statistics:
  - last bytes.
  - average bytes.
  - max bytes.
  - sample count.
  - over-budget count.
- [ ] Add configurable allocation budgets by scope/category, with defaults:
  - zero bytes for render submission hot loops after warmup.
  - zero bytes for ECS range systems after warmup.
  - zero bytes for avatar pose encode/decode after warmup.
  - small explicit budgets for editor UI and diagnostics.
- [ ] Log over-budget allocation events with throttling and enough context to
  identify the subsystem.
- [ ] Add an MCP/profiler query surface for allocation scope summaries if the
  profiler MCP tools need this data.

Acceptance criteria:

- [ ] Allocation regression can be attributed to a named system/pass/codec, not
  only to a thread.
- [ ] Allocation-budget failures can be turned into test assertions for
  synthetic hot-path tests.
- [ ] The allocation telemetry path does not allocate every frame when enabled.

## Phase 3 - Frame Scratch And Pooled Buffer Infrastructure

- [ ] Add a frame-local scratch API for unmanaged temporary memory:
  - per-thread or per-job lane.
  - reset at a known frame/phase boundary.
  - supports alignment.
  - exposes `Span<T>` / `ReadOnlySpan<T>` where safe.
  - has debug poisoning or lifetime checks in development builds.
- [ ] Add a managed pooled buffer API for unavoidable arrays:
  - typed rental wrappers.
  - deterministic return via `Dispose`.
  - optional clear-on-return.
  - max-retained-size policy.
  - leak diagnostics in debug builds.
- [ ] Add ring-buffered scratch variants for render handoff where data must
  survive until a later render/swap frame.
- [ ] Add capacity prewarm hooks for:
  - render command collection.
  - visibility collection.
  - avatar replication.
  - interpolation buffers.
  - GPU upload staging.
- [ ] Add counters for scratch high-water mark, overflow, fallback allocation,
  and pool misses.
- [ ] Document when to use:
  - `stackalloc`.
  - frame scratch.
  - typed pooled arrays.
  - persistent preallocated arrays.
  - unmanaged/native memory.

Acceptance criteria:

- [ ] Hot subsystems can request temporary storage without direct `new T[]`,
  LINQ materialization, or ad hoc `ArrayPool<T>` handling.
- [ ] Scratch lifetimes are enforced or diagnosable in debug builds.
- [ ] Pool misses and fallback allocations are visible in profiler counters.

## Phase 4 - Hot-Path Pools And Collections

- [ ] Keep `ResourcePool<T>` for general-purpose thread-safe pooling.
- [ ] Add a separate hot-path pool for frame-sensitive loops:
  - thread-local fast path.
  - fixed or bounded capacity.
  - no `ConcurrentBag.Count` checks in the hot release path.
  - prewarm support.
  - overflow behavior with counters.
- [ ] Add or standardize reusable hot collections:
  - dense typed lists with explicit `ClearFast`.
  - bitsets for dirty flags.
  - sparse sets for ECS component membership.
  - fixed-capacity queues/rings for packet and render handoff.
- [ ] Replace hot-path LINQ materialization with explicit loops or
  `CollectionsMarshal.AsSpan` where safe.
- [ ] Audit `foreach` in hot loops for non-struct enumerator allocation.
- [ ] Audit closure/captured lambda use in per-frame paths.
- [ ] Audit string interpolation/concatenation in per-frame diagnostics.
- [ ] Keep diagnostic and editor code readable; only optimize it when it runs
  inside a measured hot path.

Acceptance criteria:

- [ ] Render/network/ECS hot pools have no allocation on take/release after
  prewarm.
- [ ] Hot-path collection use has explicit capacity and overflow telemetry.
- [ ] Any remaining allocation in a hot path has a short written reason.

## Phase 5 - ECS Storage And System Allocation Contract

- [ ] Add ECS allocation rules to the runtime ECS implementation plan:
  - hot component stores prefer unmanaged data.
  - managed references stay in bridge stores not iterated by high-volume
    systems.
  - systems receive scratch/pool services through context.
  - structural changes are queued and applied at phase boundaries.
- [ ] Add per-system allocation counters to `RuntimeSystemContext` or the
  equivalent scheduler context.
- [ ] Add deterministic tests for component store growth, dirty bitsets, dirty
  ranges, and range partitioning without per-entity allocations.
- [ ] Add synthetic avatar ECS tests for hundreds/thousands of entities:
  - local input sample.
  - network receive decode.
  - interpolation.
  - IK/animation placeholder range solve.
  - replication build.
  - render-output publish.
- [ ] Require zero managed allocations after warmup for synthetic ECS range
  systems unless the system explicitly declares a budget.
- [ ] Add diagnostics for store capacity growth so one-time warmup allocations
  are visible and can be prewarmed.

Acceptance criteria:

- [ ] Synthetic ECS avatar hot loops pass allocation-budget tests after warmup.
- [ ] Store growth and structural-change churn are visible separately from
  steady-state system execution.

## Phase 6 - Network Replication Buffering And Codecs

- [ ] Separate generic editor/tool networking components from high-rate
  realtime replication paths in documentation and code ownership.
- [ ] Add a dedicated span/buffer-writer based replication codec path for
  avatar pose and future ECS replication channels.
- [ ] Use persistent receive slabs or pooled receive buffers for high-rate UDP
  and replication traffic.
- [ ] Avoid per-message `byte[]` payload allocation in the high-rate path.
- [ ] Avoid per-avatar DTO construction during packet fan-in/fan-out.
- [ ] Add batch packet builders that pack multiple avatars/entities per frame
  without per-avatar heap allocation.
- [ ] Add per-connection send rings with explicit capacity, drop/degrade
  policy, and counters.
- [ ] Add receive-side packet cursors that parse from `ReadOnlySpan<byte>`
  into dense ECS/interpolation buffers.
- [ ] Keep text/JSON conversion lazy and off hot paths.
- [ ] Add allocation tests for:
  - pose baseline encode.
  - pose delta encode.
  - mixed-avatar packet encode.
  - receive decode.
  - interpolation buffer insert.

Acceptance criteria:

- [ ] Avatar/ECS replication encode/decode is allocation-free after warmup.
- [ ] Send/receive queue overflow is explicit and counted.
- [ ] Generic TCP/WebSocket/REST tooling components are not mistaken for the
  production high-rate replication path.

## Phase 7 - Render, VR, Animation, And GPU Upload Hot Paths

- [ ] Audit render submission, visible collection, swap/present, VR input,
  animation/IK, and GPU-upload preparation for:
  - `new`.
  - LINQ.
  - `ToArray` / `ToList`.
  - boxing.
  - captured closures.
  - per-frame strings.
  - enumerator allocations.
- [ ] Convert nearby low-risk allocations to scratch, preallocated storage, or
  persistent buffers.
- [ ] Add allocation scopes around:
  - visibility collection.
  - render command build.
  - shadow atlas planning/execution.
  - VR pose/action update.
  - GPU skinning upload preparation.
  - texture upload staging.
  - avatar render-output publication.
- [ ] Ensure diagnostic logging in render/VR paths is gated and throttled.
- [ ] Add tests or targeted builds for touched rendering/VR paths.

Acceptance criteria:

- [ ] Main render-frame, collect/swap, and VR pose/action update scopes stay
  within their allocation budgets after warmup.
- [ ] Allocation fixes do not regress rendering correctness, VR pose validity,
  or GPU upload diagnostics.

## Phase 8 - Native And GPU Memory Lifecycle

- [ ] Inventory native/GPU resource wrappers that own substantial unmanaged
  memory:
  - buffers.
  - textures.
  - framebuffers/render targets.
  - shader/pipeline objects.
  - native import/cook resources.
  - video/audio buffers.
- [ ] Confirm each owner has deterministic disposal or explicit retirement.
- [ ] Add leak diagnostics for native/GPU resources that survive past expected
  shutdown or scene unload boundaries.
- [ ] Use `GC.AddMemoryPressure` / `GC.RemoveMemoryPressure` only for managed
  objects that own large unmanaged memory and may rely on finalization for
  cleanup. Prefer deterministic disposal instead.
- [ ] Ensure any memory pressure added is removed exactly once with the same
  byte count.
- [ ] Add counters for resident native/GPU memory by subsystem where available.
- [ ] Keep GPU resource retirement synchronized with render-frame fences rather
  than finalizer timing.

Acceptance criteria:

- [ ] Large unmanaged memory ownership is visible and deterministically
  released.
- [ ] Memory pressure APIs, if used, are paired and covered by tests or debug
  assertions.
- [ ] GPU resource retirement does not depend on GC finalization for normal
  operation.

## Phase 9 - Explicit GC Maintenance Windows

- [ ] Define engine maintenance windows where induced GC may be allowed:
  - after scene/world unload.
  - after bulk asset import/cook.
  - after exiting play mode.
  - before benchmark measurement begins.
  - after benchmark measurement ends.
  - before long idle editor wait if memory reclamation is explicitly requested.
- [ ] Add a central API for requesting maintenance GC with reason, generation,
  LOH compaction option, and logging.
- [ ] Reject or warn on maintenance-GC requests from hot-path threads.
- [ ] Remove or route existing ad hoc `GC.Collect()` calls through the central
  maintenance API when appropriate.
- [ ] Add optional benchmark-only `NoGCRegion` support:
  - preflight available memory.
  - fixed byte budget.
  - clear start/end diagnostics.
  - failure is reported, not hidden.
  - never enabled by default for editor or normal VR.
- [ ] Add profiler markers for induced collections and no-GC-region attempts.

Acceptance criteria:

- [ ] Every induced collection has a reason and a non-hot-path call site.
- [ ] No-GC-region usage is short, explicit, diagnostic, and optional.
- [ ] GC maintenance does not occur during render/update/network/VR hot loops.

## Phase 10 - Validation, CI, And Documentation

- [ ] Add unit or integration tests for allocation budgets in:
  - ECS synthetic avatar systems.
  - avatar pose encode/decode.
  - render command build where practical.
  - scratch/pool leak detection.
  - memory-pressure pairing if used.
- [ ] Add a profiler capture or log report template for memory-control
  investigations under `docs/work/testing/`.
- [ ] Update developer documentation with:
  - hot-path allocation rules.
  - scratch allocator usage.
  - pool usage.
  - GC policy profile behavior.
  - maintenance GC policy.
- [ ] Update VS Code tasks or launch docs only if new env vars, profiles, or
  validation tasks are introduced.
- [ ] Ensure new code introduces no compiler warnings.
- [ ] Run targeted tests/builds for changed subsystems.
- [ ] Record any unrelated allocation or validation failures separately instead
  of hiding them in this work item.

Acceptance criteria:

- [ ] Hot-path memory rules are documented for future engine contributors.
- [ ] Allocation-budget tests catch regressions in at least ECS/network hot
  paths.
- [ ] Validation evidence records before/after allocation and frame-time
  behavior.

## Phase 11 - Merge And Follow-Up

- [ ] Review all checklist items and mark intentionally deferred work with a
  reason.
- [ ] Confirm editor, server, VR client, and unit-testing world launch paths
  still work.
- [ ] Confirm allocation telemetry is useful without overwhelming profiler UI
  or logs.
- [ ] Move this todo to `docs/work/todo/COMPLETED/` only after implementation
  and validation are complete.
- [ ] Merge the dedicated branch back into `main` after completion and
  validation.

