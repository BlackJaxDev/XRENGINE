# Physics Chain Performance

Last updated: 2026-10-07

The physics-chain optimization work reduces `PhysicsChainComponent` CPU cost, GPU transfer bandwidth, synchronization stalls, and hot-path allocations across CPU, standalone GPU, and batched GPU modes.

The runtime has low-allocation buffer uploads, asynchronous compatibility
readback, version-aware uploads, reduced transform propagation, and reusable
CPU scheduling. The thousands-scale path also has generation-safe bone
coverage, retained canonical GPU bounds, committed CPU spatial snapshots,
mapped input pages, world-owned clocks, and reused rest-input ranges.
Covered untextured identity-world draws retain static plans. Strict canonical
submission uses GPU collection and indexed instance groups in the main view.
Directional shadows and the remaining world input work are tracked in the
[implementation todo](../../work/todo/physics/physics-chain-thousands-scale-optimization-todo.md).
Runtime evidence and acceptance checks remain in
[Physics Chain Performance Testing](../../work/testing/physics/physics-validation.md).

## GPU skinned scale measurement

Use `Tools/Benchmarks/Measure-PhysicsChainScale.ps1` for the Math Intersections
skinned-chain scene in a named isolated Release session. Put output in the
current task run:

```powershell
pwsh Tools/Benchmarks/Measure-PhysicsChainScale.ps1 -Session physics-chain-scale -ChainCount 2000 -WindowSeconds 30 -OutputFolder "Build/_AgentValidation/<task-run>/reports/scale"
```

`-Session` is required. Chain count defaults to 2,000 and accepts 1 to 10,000.
The timing window defaults to 30 seconds and accepts 5 to 60 seconds.
`-SubmissionStrategy` defaults to `GpuIndirectZeroReadback`. Use `CpuDirect`
for an explicit CPU submission comparison. The command restores the prior
runtime override after cleanup. It checks the frozen package before and after
timing and rejects a changed strategy, a downgrade, or new physics readback
in the strict GPU window.
`-Telemetry` enables stage counters for a separate diagnostic window.
`-UseExistingSession` uses the named running session and leaves it running.
`-CaptureEvidence` writes wide and near MCP images after timing. Native
RenderDoc exports remain necessary when MCP readback does not match the target.
Use the [editor workflow](../ai/agent-editor-workflows.md) for session ownership,
capture, and evidence retention.

The command waits for canonical readiness. It rejects changed chain counts,
new bad frame outcomes, new dispatch or input-page failures, quarantined input
pages, new input-buffer allocations, and input writes or a producer epoch that
do not advance. On a readiness timeout, it saves the final frame counters,
profiler, and dispatcher. Completed Hz uses the actual elapsed window. A preallocated ring
records completed-frame timestamps. The command calculates frame-interval p95
from these timestamps and rejects a reset, overwritten samples, or incomplete
coverage. The summary reports whether p95 meets the 10 ms target. One-second
counter samples remain for rate trends only. Benchmark cleanup restores the source rig.
The count sample calls `GPUPhysicsChainDispatcher.CaptureRegisteredComponentCount()`.
It does not serialize the full dispatcher. The command calls `get_render_state`
with `viewport_index: 0` before and after the timed window. This selects the
desktop window viewport when a transient shadow viewport is active. These
snapshots include retained draw-plan and material-resolution counts. It also
saves the final profiler and a
`<label>-window-start.json` marker. Set `-CpuTraceSeconds 15` with a window of
at least 20 seconds to collect a bounded `dotnet-trace` sampled-thread trace.
The harness starts the trace inside the timed window and saves its output
beside the summary. This requires `dotnet-trace` on `PATH`. A traced or
telemetry window is diagnostic evidence. Run a separate unobserved acceptance
window. Set `-RequireDirectionalShadows` to require new accepted cascade
groups with no new rejected, unconsumed, or generic groups. These counters
prevent a cached static atlas from passing a moving-shadow check.
The harness temporarily disables `Debug.EnableProfilerFrameLogging`,
`Debug.EnableProfilerComponentTiming`,
`Debug.EnableGpuRenderPipelineProfiling`, and
`Debug.EnableProfilerUdpSending` through session preferences. It checks these
settings before and after the window, then restores them. It saves failure
evidence before it checks the final frozen strategy snapshot. The
`XRE_PROFILER_ENABLED` environment flag controls UDP sending. It does not
disable the editor code profiler.
Failed setup removes the partial benchmark root, resets the run state, and
records the exception and stack.

## World-owned runtime architecture

`PhysicsChainComponent` remains the serialized authoring/lifecycle surface,
while `PhysicsChainWorld` owns runtime registration, generational handles,
structural and dynamic command drains, immutable templates, state, output, and
backend scheduling. Structural mutations apply at the world tick boundary;
dynamic commands apply immediately before scheduling with latest-intent
semantics. Removed slices are retired only after active frames complete.

CPU and GPU storage grows geometrically. Capacity failures and fragmentation
are observable. Fragmented arenas are rebuilt and swapped only at a quiescent
structural boundary; they are never compacted in place while a frame can hold a
generation. Stable template and collider content is deduplicated per world.

## CPU execution

The scalar reference kernel defines correctness. The optimized CPU backend
selects explicit scalar-linear, AVX2 eight-chain linear, and depth-ordered
branched families. Templates precompute depth ranges, influence bounds,
segment lengths/inverses, and coefficient packs. A persistent coarse-range
worker scheduler reuses threads and high-water buffers; deterministic mode is
available for captures/tests. Warm steady scheduling and kernel execution must
allocate zero managed bytes.

Palette, conservative bounds, and transform mirroring are independent output
consumers. Unrequested outputs do no work. Mirroring is opt-in and exposes its
cadence, age, and cost. CPU renderer palettes use the renderer's canonical
`root bind * inverse bind * particle world` composition and 48-byte affine
three-row layout, with independent current/previous history for motion vectors.

## GPU execution and strict zero-readback

The compute dispatcher talks through `IPhysicsChainComputeBackend`. The Math
Intersections skinned dispatcher scenario has live Vulkan Advanced-pipeline
evidence. See the [skinned chain investigation](../../work/investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)
for scope, repairs, and current scale limits. Unsupported backend capabilities
fail explicitly instead of switching to CPU. Templates and collider shape
topology upload once, dynamic headers and poses upload by dirty range, and
resident arenas retain stable generation-tagged offsets across frames.

GPU workgroup prefix-sum passes compact active tree IDs into short-linear and
branched/long buckets and author indirect dispatch arguments on the GPU.
Current-frame activity, dispatch sizing, simulation, palette, bounds, culling,
and rendering never require CPU readback. Capacity is provisioned to the known
candidate maximum; shader clamping/counters are a defensive corruption guard,
not a reason to introduce background readback into strict mode.

Strict mode forbids `WaitForGpu`, blocking maps, current-frame readback, and
hidden CPU fallback. A capability or capacity failure is a visible backend
failure. The last valid output may remain visible according to the selected
failure policy, but CPU simulation cannot silently become authoritative.

## Shared collision data

Authored sphere, capsule, box, and plane shapes live in stable versioned shared
sets. Dynamic poses have a separate version and dirty range. CPU sets with up
to four colliders use specialized direct paths. Larger sets refit a shared BVH
and generate candidates from a swept conservative chain AABB. Candidate
overflow is reported and conservatively falls back to the full set; a truncated
candidate prefix is never accepted as authoritative collision data.

Collider shape records precompute normalized capsule/plane axes, axis length
terms, radius terms, local plane distance, and conservative local extents once
at the structural boundary. The per-world content-addressed cache reports
unique/live retained sets, unique shape count, estimated shape bytes, lookups,
and deduplicated lookups. Sets are retained for the world lifetime, so live and
unique set counts intentionally match until an explicit eviction policy exists.
CPU batches process instances sharing the exact pose stream consecutively and
expose grouped-set and grouped-instance counters.

Broadphase ownership follows pose ownership. Up to four colliders use the
direct narrowphase path. Larger CPU-authored pose sets use the CPU BVH. A
GPU-authored pose set must use an available GPU broadphase; an unavailable GPU
broadphase is an explicit capability failure, never authorization to read poses
back merely to build candidates on the CPU.

Chain particles are receive-only collision participants: narrowphase corrects
particle positions but never pushes collider or rigid-body state. Dynamic-body
impulses require a separate, explicitly enabled event/impulse integration path.
Gameplay collision events are independent from rendering output and simulation
visibility. GPU events, when requested, use selective asynchronous readback and
become visible only after their fence completes; request frame, source epoch,
and instance generation validation reject stale results. Self-collision is
unsupported by the common kernels and must not be enabled implicitly; a future
implementation must be opt-in and separately accelerated.

## Sleep, quality, and compatibility outputs

Automatic quality uses explicit CPU/GPU work budgets, deterministic relevance
and importance ordering, transition caps, tier hysteresis, and minimum
residency. Irrelevant chains sleep and distant chains reduce cadence before
constraint/collision quality is reduced. Strict/fixed tiers remain available
for gameplay and captures. Sleep holds current/previous output coherently and

## Runtime Goals

- Default visible GPU physics-chain rendering should not require same-frame GPU readback.
- CPU bone sync remains an explicit compatibility/debug path with known cost.
- Steady-state uploads reuse staging storage instead of rebuilding CPU-side buffers every frame.
- Static particle metadata, collider data, transform data, and per-tree parameters upload only when their versions or dirty ranges require it.
- CPU and GPU paths expose bandwidth and timing counters so solver, upload, copy, readback, and transform costs can be distinguished.

## Implemented Optimizations

### Zero-Readback Direction

Isolated and batched GPU modes both use the world dispatcher; isolated requests receive a unique dispatch key while retaining the same fence-based asynchronous readback path. The previous completed readback stays live until the next fence completes, avoiding same-frame `GetBufferSubData` stalls on the preferred visible path.

`GpuSyncToBones` should be treated as a compatibility mode rather than the normal rendering path. Visible deformation should use GPU-authored particle or bone-palette data where possible.

### Upload Architecture

`XRDataBuffer` now supports low-allocation unmanaged span/direct-write update paths. Steady-state physics-chain uploads avoid fresh `DataSource` allocation when capacity and stride are reusable, and unmanaged copies replace per-element marshalling where safe.

Physics-chain per-tree parameter updates no longer allocate through `ToArray()`, and static metadata, colliders, and transforms use versioning or dirty tracking to avoid unconditional full-buffer uploads.

### Transfer Bandwidth

The GPU path separates static particle metadata from dynamic state more aggressively. Stable scenes avoid static-data uploads; unchanged colliders drop toward zero upload bandwidth; transform upload cost is reduced in animation-stable cases.

Batched mode has been reevaluated around resident-buffer copy churn so combined-buffer work can be measured separately from upload and readback bandwidth.

### CPU And Transform Cleanup

CPU multithreaded mode no longer allocates a new `ActionJob` per active component per frame. Transform hierarchy recalculation and validation paths have been cleaned up to avoid unnecessary temporary lists, closure-heavy lookups, and rebuild-time dictionary churn on steady-frame paths.

### Data Layout And Unsafe Use

Span and unsafe code are confined to buffer-writing and upload/readback layers where they replace real overhead. A broader particle SoA rewrite is deferred because the transfer-path fixes moved the dominant cost elsewhere; it should only be revisited if profiling shows particle object layout is again the limiting factor.

## Diagnostics

Profiler scopes and bandwidth counters distinguish:

- `Prepare`
- `PrepareGPUData`
- `UpdateBufferData`
- `UpdatePerTreeParams`
- standalone readback
- batched readback
- `ApplyParticlesToTransforms`
- `PublishGpuDrivenBoneMatrices`
- upload bytes
- GPU copy bytes
- readback bytes

Runtime bandwidth/frame metrics are available from `GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot()`.

`PhysicsChainComponent.GetRuntimeDiagnostics()` is the allocation-free
per-chain inspection surface. It reports the requested CPU/batched-GPU/
standalone-GPU path, backend readiness, retained CPU and GPU kernel families,
requested and effective quality policy, and explicit compatibility costs such
as CPU transform mirroring, GPU bone readback, and selected chain debug rendering.
A failed GPU status never indicates that a CPU fallback was used.

Batched GPU chain debug uses one compact world batch and two indexed indirect
draws. It selects only explicit chains, with limits of 256 chains and 16,384
particles. It does no CPU readback and creates no debug work when selection
is off. Read `GPUPhysicsChainDispatcher.GetGpuDebugDiagnosticsSnapshot()` for
selected counts, the limit flag, dispatches, draw submissions, and readback
use. A foreign renderer, a busy retained batch, or a retired native source
rejects work with a diagnostic. Native debug and teardown checks remain in the
[validation plan](../../work/testing/physics/physics-validation.md#gpu-skinned-chain-scale).

`GPUPhysicsChainDispatcher.Instance.GpuBoundsDiagnostics` reports GPU bounds
slots, dispatches, copied commands, material-contract rejections, and
compatibility-route rejections. Use `LastMaterialContractRejectionCount` and
`LastCompatibilityRouteRejectionCount` to see each cause in the latest bounds
publication. `CompatibilityRouteRejectionCount` counts covered chain commands
whose GPUScene copy was rejected because the compatibility culling draw has no
retained physics output page and source identity. The canonical Advanced route
uses the retained atlas bound. CpuDirect still draws a rejected compatibility
command. No CPU bound replaces the rejected GPUScene copy. Release builds do
not write the warning text, so read the counters in Release sessions.

To test material vertex effects in the Math Intersections world, set
`XRE_PHYSICS_CHAIN_TEST_VERTEX_EFFECTS=1` before the editor starts. The skinned
chain test material then declares neutral vertex-effect parameters. Change them
with the `set_material_uniforms` MCP tool. Do not set this variable for timing
windows.
Use `set_object_reference` to assign an existing material to a command override
or an existing transform to `RootBone` or `ReferenceObject`. Supply target and
reference object IDs and optional member paths. The action checks the reference
type and uses the property setter. Omit the reference ID to clear it.

## Key Files

- `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`
- `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`

## Related Documentation

- [Physics Chain Performance Testing](../../work/testing/physics/physics-validation.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../../work/design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [Physics](../../user-guide/physics.md)


### Versioned CPU input and GPU arena placement

The component caches particle seed state, static templates, and bone topology
at their source version boundaries. GPU preparation does not create CPU job
snapshots. Rest gravity has its own tree version, so a dynamic header change
does not upload particle templates. The rendering bridge copies request-owned
inputs only when the matching source version or dynamic signature changes.

The particle arena requests `StaticCopy`. The Vulkan backend can then use
device-local storage at its normal size threshold. Seed/reset uploads and
ordered GPU growth copies remain required. Transform and header arenas retain
their dynamic upload policy. `ParticleArenaBufferStatus.ResolvedRoute` reports
the actual backend choice; requested usage alone does not prove placement.

`PhysicsChainWorkgroups.glslinc` defines the short-linear group size for both
the solver and indirect argument builder. Change both through this shared
constant. A group-size change requires full-population and animation checks.
The 32-, 64-, and 128-thread development samples do not yet establish a winner;
CPU stalls and varying GPU clocks prevent a controlled GPU comparison.

The rendering bridge retains one readback coordinator per physics world.
World tracking ends after the last source leaves and outstanding work drains.
GPU bounds routing captures renderer command indices once per scene, then
uses indexed lookups for each palette binding. The compatibility GPUScene copy
writes a rejected record for covered chain commands. Its command bounds and
cull bounds buffers do not carry the physics atlas bound. Use the canonical
Advanced route for GPU-culled covered chains.

With `XRE_WORLD_TICK_TELEMETRY=1`,
`PhysicsChainComponent.WorldLateTickTelemetry` exposes cumulative late-tick
stage times and gate wait in Stopwatch ticks. Compare snapshots and divide by
`StopwatchFrequency`. These are elapsed times, not exclusive CPU use. Keep
this observer disabled for final performance acceptance.

`PhysicsChainComponent.GetGpuRestInputCompatibilityCount(reason)` reads the
world's cumulative compatibility-input count for one reason. A compatibility
input still uses the selected GPU solver. Compare the counter before and after
a change; do not treat a process-lifetime total as one frame's result.

`RenderableMeshStageTelemetry.Snapshot()` reports named stages as
`stage:ticks/calls` when world tick telemetry is enabled. These counters cover
all scene meshes. They do not isolate one covered renderer. Use the selected
renderer's `TrackedSkinnedBoneCount`, `HasPendingRenderMatrixUpdate`, and
`UsesCommittedWorldBounds` for focused inspection.

The same flag enables `RuntimeWorldRenderer.CollectionTelemetry`. Compare
snapshots to get collect and swap call counts and matrix, mesh, and scene time.
It also enables
`TransformHierarchyStore.GetReadContentionTelemetrySnapshot()`, which reports
contended local, world, and render reads, odd and changed-sequence retries,
cumulative retry ticks, and the maximum spin count. Divide retry ticks by
`StopwatchFrequency` to get elapsed time. The read counters are process-wide
and can add time across threads; they are not one frame's critical-path time.
Uncontended reads do not call the timer or update contention counters. Disable
the flag for final performance acceptance.
