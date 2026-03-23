# Physics Chain Speed Update TODO

Last Updated: 2026-03-22
Current Status: Phases 0-3 complete, Phase 4 CPU/transform cleanup in progress
Scope: reduce `PhysicsChainComponent` CPU cost, GPU transfer bandwidth, synchronization stalls, and hot-path allocations across CPU, standalone GPU, and batched GPU modes.

Related docs:

- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)

## Goal

Make physics chains scale to substantially more active instances without frame-time spikes, GPU/CPU stalls, or excessive upload/readback traffic.

Success means:

- the default visible path does not require synchronous GPU readback,
- hot paths do not allocate per frame unless unavoidable and explicitly justified,
- buffer uploads reuse storage instead of rebuilding CPU-side staging every frame,
- CPU and GPU modes both have clear bandwidth and frame-time budgets,
- benchmark scenes can distinguish simulation cost from upload, readback, and transform propagation cost.

## Current Reality

What the current implementation already does well:

- GPU physics-chain execution is already integrated into the unified `PhysicsChainComponent` behind `UseGPU`, with optional batched dispatch through `GPUPhysicsChainDispatcher`.
- compute shader, shader-storage-buffer, and dispatcher plumbing are already present and working in the engine architecture.
- static particle metadata is versioned in parts of the GPU path,
- batched GPU dispatch already amortizes compute submission across multiple components,
- the component already tracks bandwidth counters through `GPUPhysicsChainDispatcher`,
- some temporary data is already reused with grow-only lists and pooled arrays.

What currently blocks scale:

- standalone GPU mode still performs synchronous `GetBufferSubData` readback when `GpuSyncToBones` is enabled,
- batched GPU mode also forces grouped synchronous readback when any participating request needs CPU bone sync,
- `XRDataBuffer.SetDataRaw(...)` rebuilds CPU-side staging storage, which turns per-frame uploads into per-frame allocation and repack work,
- transform matrices and collider buffers are rebuilt and uploaded every frame without dirty-range tracking,
- `UpdatePerTreeParams` uses `stackalloc` but then defeats it with `ToArray()`,
- CPU multithreaded mode allocates a new `ActionJob` per active component per frame,
- transform hierarchy recalculation is likely a major CPU cost in `Prepare()` and transform application.

## Consolidated Prior Integration Findings

The earlier GPU-physics-chain compatibility and integration verification notes established these points, which remain true but are no longer separate work docs:

- the GPU path is not a side prototype anymore; it is part of the main `PhysicsChainComponent` surface,
- the engine already supports the compute-shader, buffer-binding, and batched-dispatch contracts this feature needs,
- async readback exists as a compatibility mechanism, but one-frame-latency CPU sync is still the wrong default for the primary visible rendering path,
- current work is therefore focused on removing visible-path readback, upload churn, and hot-path allocation, not on proving that the basic GPU path can execute at all.

## Optimization Priorities

Priority order is based on likely impact, not implementation ease.

1. Remove synchronous GPU readback from the visible path.
2. Eliminate per-frame buffer repack/allocation in `XRDataBuffer` upload paths.
3. Stop full-buffer uploads for data that did not change.
4. Reduce transform propagation cost and unnecessary hierarchy recalculation.
5. Remove remaining hot-path managed allocations in CPU and GPU modes.
6. Revisit data layout for cache locality only after the transfer path is fixed.

## Hot-Path Findings To Address

### Confirmed Hot-Path Allocations

- `PhysicsChainComponent.GPU.UpdatePerTreeParams()` allocates every frame via `paramsSpan.ToArray()`.
- CPU multithreaded mode allocates `new ActionJob(db.UpdateParticles)` per active component per frame.
- `XRDataBuffer.SetDataRaw(...)` allocates a fresh `DataSource` and repacks with `Marshal.StructureToPtr(...)` on buffer writes, so per-frame physics-chain uploads also incur CPU-side allocation and marshalling overhead.
- standalone readback resizes `_readbackData` on particle-count changes.

### Confirmed High-Bandwidth / Stall Paths

- standalone GPU mode calls synchronous readback when `GpuSyncToBones` is enabled,
- batched GPU mode synchronously reads the combined particle buffer when any request requires CPU readback,
- transform matrices are uploaded every frame (unconditional `SetDataRaw` + `PushData`),
- colliders are uploaded every frame (unconditional `SetDataRaw` + `PushData`),
- per-tree params are rebuilt and uploaded every frame,
- particle and static-particle buffers *do* have version-gated conditional upload, but still go through the allocating `SetDataRaw` path when they fire,
- batched mode copies resident particle buffers into and back out of the combined buffer using GPU copy operations.

### Confirmed CPU-Cost Hot Spots

- `Prepare()` forces full hierarchy recalculation for each root,
- transform application recalculates matrices inside the per-particle loop for parent transforms,
- root-change detection allocates a temporary list during validation,
- GPU-driven renderer binding rebuild creates temporary dictionaries and arrays during rebuilds,
- `GPUPhysicsChainDispatcher` dispatch loop enumerates a `ConcurrentDictionary`, which allocates an enumerator each tick.

## Phase 0 - Instrumentation And Baseline

Outcome: we can quantify whether a change reduces solver time, transform time, upload time, readback time, or total transfer bandwidth.

### 0.1 Add Narrow Profiler Markers

- [x] Add profiler scopes for `Prepare`, `PrepareGPUData`, `UpdateBufferData`, `UpdatePerTreeParams`, standalone readback, batched readback, `ApplyParticlesToTransforms`, and `PublishGpuDrivenBoneMatrices`
- [x] Split reported physics-chain bandwidth into upload, GPU copy, and readback buckets per frame and per component mode
- [x] Add a benchmark summary field for total hierarchy recalc time if practical

### 0.2 Define Repeatable Scenarios

- [x] Reuse the existing Math Intersections World benchmark harness instead of creating a separate physics-chain benchmark system
- [x] Add benchmark presets in that harness for CPU single-thread, CPU multithread, standalone GPU, and batched GPU
- [x] Add at least one no-collider scene and one heavy-collider scene
- [x] Add at least one skinned mesh chain scene with `GpuSyncToBones` on and off

Acceptance criteria:

- [ ] We can answer which path is bottlenecked by CPU math, matrix propagation, upload bandwidth, GPU copy bandwidth, or readback stall.

## Phase 1 - Remove Visible-Path Readback Dependency

Outcome: the primary rendering path no longer depends on synchronous GPU readback.

### 1.1 Make Zero-Readback The Default Target

- [ ] Align runtime work with the existing [zero-readback plan](../design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [ ] Keep GPU particle state authoritative on GPU for visible deformation where possible
- [ ] Treat CPU bone sync as fallback, editor-only, or opt-in compatibility path

### 1.2 Replace Synchronous Readback

- [x] Add fence-based asynchronous readback for standalone GPU mode
- [x] Add fence-based asynchronous grouped readback for batched GPU mode when CPU sync is explicitly required
- [x] Keep the previous completed readback alive until the next fence completes instead of blocking the frame
- [x] Reject any implementation that still uses same-frame `GetBufferSubData` on the main visible path

### 1.3 Tighten `GpuSyncToBones` Contract

- [ ] Document that `GpuSyncToBones` is a compatibility mode with higher cost
- [x] Add an explicit cost warning in editor UI or component metadata
- [ ] Ensure debug rendering and editor tooling can still function without forcing visible-path readback

Acceptance criteria:

- [ ] Standard visible rendering works without same-frame readback.
- [ ] CPU bone sync remains available only when explicitly requested.
- [ ] Benchmarks show readback bandwidth and stalls near zero on the preferred rendering path.

## Phase 2 - Fix Buffer Upload Architecture

Outcome: per-frame uploads stop rebuilding CPU-side staging storage and can use `Span<T>` or direct memory writes efficiently.

### 2.1 Extend `XRDataBuffer`

- [x] Add `SetDataRaw(ReadOnlySpan<T>)` for unmanaged payloads
- [x] Add a direct write/update API that reuses existing `ClientSideSource` when capacity is sufficient
- [x] Avoid allocating a new `DataSource` on every update when element count and stride are unchanged
- [x] Replace `Marshal.StructureToPtr(...)` loops with direct unmanaged copies where safe

### 2.2 Add Capacity-Reuse Semantics

- [x] Reuse existing CPU staging memory for same-size or smaller uploads
- [x] Grow capacity geometrically instead of exact-size reallocation when appropriate for dynamic buffers
- [x] Preserve existing render backend behavior and disposal semantics

### 2.3 Apply To Physics Chain

- [x] Update physics-chain uploads to use span/direct-write paths instead of `SetDataRaw(IList<T>)`
- [x] Remove `paramsSpan.ToArray()` in `UpdatePerTreeParams`
- [x] Verify standalone and batched paths use the same low-allocation upload strategy

Acceptance criteria:

- [x] No per-frame `DataSource.Allocate(...)` in steady-state physics-chain uploads.
- [x] `UpdatePerTreeParams` performs zero managed allocations in steady state.

## Phase 3 - Reduce Transfer Bandwidth

Outcome: only data that changed is uploaded or copied.

### 3.1 Separate Dynamic And Static State More Aggressively

- [x] Keep particle static metadata fully versioned and upload only on topology or parameter changes
- [x] Split transform and collider update policy from particle static metadata policy
- [x] Decide whether per-tree params belong in a dedicated tiny dynamic buffer or in a packed ring/staging buffer

### 3.2 Add Dirty Tracking

- [x] Add collider dirty/version tracking so static colliders are not fully rebuilt every frame
- [x] Add transform dirty/version tracking at least per tree, preferably with range tracking
- [x] Support subrange upload calls for transform and collider buffers

### 3.3 Reevaluate Batched Resident Copy Strategy

- [x] Measure whether resident-buffer copy in/out is cheaper than direct combined-buffer authoring after upload-path fixes
- [x] If not, replace resident-copy churn with direct authoritative combined-buffer updates
- [x] Keep zero-readback goals aligned with this decision

Acceptance criteria:

- [x] Stable scenes with unchanged topology avoid static-data uploads.
- [x] Collider upload bandwidth drops to near zero for unchanged colliders.
- [x] Transform upload cost is reduced measurably in animation-stable cases.

## Phase 4 - CPU Solver And Transform Propagation Cleanup

Outcome: CPU mode and hybrid GPU mode spend less time on hierarchy work and less time allocating scheduler objects.

### 4.1 Remove Per-Frame Job Allocation

- [x] Replace `new ActionJob(db.UpdateParticles)` with reusable scheduled work items or a no-allocation batch job path
- [x] Validate worker-thread behavior remains correct under repeated activation/deactivation

### 4.2 Reduce Hierarchy Recalculation

- [x] Audit why full `RecalculateMatrixHierarchy(...).Wait()` is required every frame in `Prepare()`
- [x] Determine whether only dirty branches or root-to-chain branches need refresh
- [x] Avoid `RecalculateMatrices(forceWorldRecalc: true)` inside the inner transform-application loop if a cheaper propagation path can preserve correctness

### 4.3 Validation-Path Cleanup

- [x] Remove temporary list allocation from `IsRootChanged()`
- [x] Replace `List.Exists(...)` root dedupe in `SetupParticles()` with a reference set or equivalent no-closure lookup
- [x] Cache or pool the temporary dictionaries in `RebuildGpuDrivenRendererBindings()` instead of allocating three new dictionaries per rebuild
- [x] Keep rebuild-time allocations acceptable but clearly separated from per-frame paths

### 4.4 Dispatcher Allocation Cleanup

- [x] Replace `ConcurrentDictionary` enumeration in the dispatch loop with a pre-snapshotted list or lock-free alternative that avoids enumerator allocation
- [x] Audit `_activeRequests` list clear-and-refill pattern for avoidable churn

Acceptance criteria:

- [x] CPU multithreaded mode has no per-component job allocation each frame.
- [x] Transform propagation cost is measurably lower in profiling.

## Phase 5 - Data Layout And Unsafe Optimization Pass

Outcome: after the transfer architecture is fixed, the remaining solver hot path is optimized for locality and direct memory access.

### 5.1 Apply `Span<T>` Where It Actually Helps

- [x] Use `ReadOnlySpan<T>` and `Span<T>` on contiguous buffer-writing paths
- [x] Use spans for small temporary parameter blocks and staging slices
- [x] Do not add span wrappers around `List<Particle>` loops unless the underlying storage is first made contiguous enough to benefit

### 5.2 Use `unsafe` Where It Replaces Real Overhead

- [x] Use direct unmanaged copies for buffer staging instead of marshalling per element where types are blittable
- [x] Consider persistent mapped buffer writes or pointer-based bulk writes for dynamic SSBO updates
- [x] Keep unsafe confined to buffer upload/readback layers and well-tested math helpers

### 5.3 Reevaluate Particle Layout

- [x] Audit whether `Particle` object layout is still a dominant CPU cost after Phases 1-4
- [x] If yes, prototype a contiguous-array or SoA particle storage path behind tests
- [x] Only proceed if profiling shows cache/locality limits still dominate after transfer fixes

Current audit result: after the transfer-path fixes, particle object layout is no longer the dominant cost, so a SoA rewrite is deferred until profiling shows it is warranted.

Acceptance criteria:

- [x] `unsafe` is used only where it replaces measurable overhead.
- [x] Any layout rewrite is justified by profiling, not style preference.

## Phase 6 - Validation And Regression Coverage

Outcome: performance changes stay correct and measurable.

### 6.1 Add Performance Regressions Tests And Guards

- [x] Add targeted tests for no-readback mode behavior
- [x] Add tests for async/fenced readback ordering and generation handling
- [x] Add tests for upload dirty-version logic
- [x] Add tests ensuring renderer-driven GPU bone palette still works when CPU sync is disabled

Phase 6 automated coverage now lives in `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs` and `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`.
Those tests lock down the zero-readback contract, stale generation/submission rejection, transform dirty-range detection, per-tree parameter warm-path allocations, GPU-driven renderer registration, and bandwidth snapshot accounting.

### 6.2 Add Allocation Audits

- [x] Add focused allocation checks for `UpdatePerTreeParams`, standalone GPU update, and CPU multithread scheduling
- [x] Ensure the work doc can reference `Report-NewAllocations` output for verification

Verification split used for this phase:

- `UpdatePerTreeParams` steady-state allocation guard is covered by unit tests.
- Standalone GPU update and CPU multithread scheduling remain audited through the existing `Report-NewAllocations` task and its generated report output.

### 6.3 Benchmarks Before And After

- [ ] Capture before/after benchmark summaries for representative scenes
- [ ] Record CPU upload bytes, GPU copy bytes, CPU readback bytes, dispatch count, and frame times
- [ ] Keep one benchmark specifically for `GpuSyncToBones` compatibility mode and one for zero-readback mode

Use `Report-NewAllocations` for hot-path allocation verification, then capture runtime bandwidth/frame metrics from `GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot()` in both compatibility and zero-readback modes during live scene runs.

Acceptance criteria:

- [ ] The optimization work has reproducible before/after evidence.
- [x] Regression tests cover the main synchronization and versioning risks.

## Recommended Execution Order

Implement in this order unless profiling shows a different bottleneck:

1. Phase 0 instrumentation
2. Phase 1 zero-readback and async readback work
3. Phase 2 `XRDataBuffer` upload architecture fix
4. Phase 3 dirty-range and bandwidth reduction work
5. Phase 4 CPU scheduling and hierarchy cleanup
6. Phase 5 targeted `Span<T>` and `unsafe` work
7. Phase 6 validation and benchmark capture

## Explicit Non-Goals For This Pass

- Do not rewrite the entire solver into SoA before fixing transfer architecture.
- Do not micro-optimize math loops while same-frame readback remains in the visible path.
- Do not add broad unsafe code in gameplay/component layers when the real overhead lives in buffer staging and synchronization.
- Do not regress editor/debug functionality without a documented fallback path.

## Definition Of Done

- [ ] Default visible GPU physics-chain path is zero-readback.
- [ ] Steady-state per-frame uploads do not allocate fresh CPU staging buffers.
- [ ] No known hot-path managed allocations remain in `UpdatePerTreeParams` or CPU multithread scheduling.
- [ ] Transform and collider uploads use dirty/version-aware policy.
- [ ] Benchmarks show reduced total bandwidth and improved frame time at scale.
- [ ] Tests cover async readback, versioning, and zero-readback renderer consumption.