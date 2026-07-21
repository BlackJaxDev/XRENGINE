# Physics Chain Performance

Last updated: 2026-07-20

The physics-chain optimization work reduces `PhysicsChainComponent` CPU cost, GPU transfer bandwidth, synchronization stalls, and hot-path allocations across CPU, standalone GPU, and batched GPU modes.

The implementation now has the core architecture in place: low-allocation buffer upload paths, async readback for compatibility sync, dirty/version-aware uploads, reduced transform propagation cost, reusable CPU scheduling, and regression coverage for the main synchronization and versioning risks. Remaining work is validation and benchmark capture, tracked separately in [Physics Chain Performance Testing](../../work/testing/physics-chain-performance.md).

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

The compute dispatcher talks through `IPhysicsChainComputeBackend`; OpenGL is
the production mapping, while unsupported Vulkan/DX12 capabilities fail
explicitly instead of silently switching to CPU. Templates and collider shape
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
as CPU transform mirroring, GPU bone readback, and per-chain debug rendering.
A failed GPU status never indicates that a CPU fallback was used.
## Key Files

- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs`
- `XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs`
- `XREngine.Runtime.Rendering/Objects/XRDataBuffer.cs`
- `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`

## Related Documentation

- [Physics Chain Performance Testing](../../work/testing/physics-chain-performance.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../../work/design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [Physics](../../user-guide/physics.md)
