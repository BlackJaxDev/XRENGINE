# Physics Chain World Runtime

This page describes the world-owned runtime for `PhysicsChainComponent`. The runtime lets thousands of chains share one schedule, shared immutable data, and stable memory. For the GPU backend adapter contract, see [Physics-chain compute backends](physics-chain-compute-backends.md). For palette, bounds, and CPU readback, see [Physics-chain output and readback](physics-chain-output-and-readback.md). For tuning, see [Physics Chain Performance](../../developer-guides/rendering/physics-chain-performance.md).

## Ownership Flow

```text
PhysicsChainComponent (authoring facade)
    -> structural and dynamic command buffers
PhysicsChainWorld (one simulation owner, one schedule)
    -> template cache + collider-set cache + instance and state arenas
    -> PhysicsChainCpuBackend or GPUPhysicsChainDispatcher
    -> current and previous palette atlas + bounds + activity and status
    -> renderers and GPUScene culling
    -> optional selective asynchronous readback or transform mirror
```

- Components own editable settings and references. They do not own solver storage.
- `PhysicsChainWorld` owns registration, capacity, scheduling, state transitions, and output publication. One world-level tick schedules all chains. A component does not register its own tick callbacks.
- Chains are addressed through `PhysicsChainRuntimeHandle`, a slot plus a generation. Every external operation checks the generation. A stale handle fails deterministically. It never aliases reused storage.
- Activation, deactivation, and destruction are safe while work from an earlier frame is in flight. `PhysicsChainDeferredLifetimeArena` keeps resources alive until every frame that uses them completes.

## Commands

Commands are split into two kinds:

| Kind | Type | Values | When applied |
| --- | --- | --- | --- |
| Structural | `PhysicsChainWorldCommandKind` | `Add`, `Remove`, `Retemplate`, `Resize`, `Rebind`, `BackendSwitch` | Only at a world boundary. They can rebuild a bucket or grow an arena outside the hot dispatch. |
| Dynamic | `PhysicsChainWorldDynamicCommandKind` | `Root`, `Force`, `Parameters`, `Relevance`, `Quality` | After structural commands. Latest value wins. They never rebuild unrelated instances. |

Ordinary motion never looks structural.

## Runtime Records

| Record | Lifetime | Contents |
| --- | --- | --- |
| `PhysicsChainTemplate` | Immutable, deduplicated by `PhysicsChainTemplateCache` | Topology order, parent indices, depth ranges (`PhysicsChainDepthRange`), rest data, coefficient packs (`PhysicsChainCoefficientPack`), bone mapping, influence bounds (`PhysicsChainInfluenceBounds`), feature mask (`PhysicsChainTemplateFeatureMask`) |
| `PhysicsChainInstance` | Stable registration | Template, collider set, input slice, state slice, palette slice, bounds slot, quality policy, flags, generation |
| `PhysicsChainState` | Backend-owned | Current and previous particle state, velocity terms, sleep and error state, simulation clock |
| `PhysicsChainColliderSet` | Shared and versioned, deduplicated by `PhysicsChainColliderSetCache` | Typed compact collider arrays, pose data, broadphase metadata |
| `PhysicsChainOutput` | Backend-neutral | Current and previous palette bases, palette count, bounds slot, validity, generation, backend status |

Template identity is computed at structural change time and cached. The runtime never deep-hashes immutable arrays per frame. Editor changes to a chain rebuild its template version.

## Arenas And Capacity

- `PhysicsChainSlotArena` uses free lists and generational slots. Capacity grows geometrically. Live count and capacity are reported separately (`PhysicsChainArenaSnapshot`).
- `PhysicsChainArenaCompactionPolicy` defines fragmentation thresholds. Compaction is an explicit out-of-band rebuild. Live GPU slices never move while a consumer uses them.
- Capacity failure raises `PhysicsChainArenaCapacityException` or a reported rejection. The runtime never truncates particles, chains, colliders, palettes, bounds, or readback requests.
- Small dynamic header uploads use the frame-slot principles of `XRBufferPersistentRingAllocator`.

## Backends

`PhysicsChainRuntimeBackend` selects `CpuDataOriented`, `GpuBatched`, or `GpuStandalone`. CPU and GPU outputs are identical at the renderer boundary. An explicitly selected GPU backend never falls back to CPU simulation.

### CPU backend

- `PhysicsChainCpuBackend` keeps blittable state in separate static, input, dynamic, and output streams. Short chains use a segment-major layout across a block so one parent depth runs vector-wide.
- `PhysicsChainCpuWorkScheduler` partitions work by estimated particle, collider, and substep cost on a persistent worker pool. It does not queue one work item per chain. A deterministic mode gives stable reference ordering.
- `PhysicsChainCpuKernelSelector` picks a `PhysicsChainCpuKernelFamily`: `ScalarLinear` (`PhysicsChainScalarReferenceKernel`), `Avx2LinearBatch` (`PhysicsChainAvx2LinearBatchKernel`, `Vector256<float>`), or `DepthOrderedBranched` (`PhysicsChainDepthOrderedBranchedKernel`). Branched topology always uses the depth-ordered kernel. It is never flattened into the linear family.
- The steady-state CPU path allocates nothing and takes no global lock. Structural commands are batched before workers start. Outputs publish after range completion without one atomic per chain.
- The backend writes current and previous skin palettes (`PhysicsChainCpuSkinPaletteComposer`) and conservative bounds from particles plus influence radii. It does not mutate the transform hierarchy. Palette, bounds, or mirror work is skipped when no consumer needs it (`PhysicsChainCpuConsumerFlags`).

### GPU backend

- `GPUPhysicsChainDispatcher` uploads template data once per template version and collider topology once per collider-set version. After that it uploads only dirty collider poses and dirty dynamic ranges (`GPUPhysicsChainUploadPlan`). Steady-state frames do not snapshot, repack, or copy whole resident groups.
- Active work is compacted on the GPU (`PhysicsChainComputePassKind.ActiveWorkCompaction`). `PhysicsChainActiveWorkScanMode` uses `SubgroupArithmetic` where supported and `PortableWorkgroup` otherwise. Profiles show the selected mode.
- Indirect dispatch arguments (`PhysicsChainIndirectDispatchArguments`) are GPU-written. GPU-written counts are dynamic data. They do not cause rerecording of stable pass topology.
- Kernels run in explicit parent-before-child order. No invocation reads a parent that another invocation may still write without a synchronization boundary. Substeps are fused in the kernel where register and watchdog limits allow.
- The dispatcher ping-pongs current and previous palette atlas roles (`PhysicsChainPaletteAtlasAllocator`) without copying unchanged history.
- Bounds envelope current and previous particle positions with influence radius and publish to stable slots that GPUScene culling reads directly. No production chain-renderer path waits on the GPU for bounds.
- Strict zero-readback profiles read nothing back for simulation, palettes, bounds, culling, or dispatch sizing.

Pass kinds are listed in `PhysicsChainComputePassKind`: arena growth, active-work reset and compaction, indirect argument generation, simulation, selective readback gather, bounds publication, bone palette publication, readback transfer, and debug visualization.

## Collision

- Colliders live in shared versioned sets. Static shape data is separate from dynamic pose data (`PhysicsChainColliderPoseBuffer`). Only dirty poses update.
- Invariant shape terms (capsule direction, inverse length squared, radii sums, plane terms) are precomputed.
- A chain-level conservative AABB test runs before particle narrowphase. Zero-collider and small fixed-count cases use specialized paths with no general broadphase cost.
- `PhysicsChainColliderBroadphase` builds a candidate list for larger sets. `PhysicsChainColliderBroadphaseOwner` keeps the broadphase on the CPU or GPU where the collider poses come from. The runtime adds no readback only to build candidates.
- Candidate overflow is conservative, visible in diagnostics, and memory safe.
- Chain particles receive collision only. They do not push colliders. Gameplay collision events are delayed and separate from rendering simulation. Self-collision is not part of the common kernel.

## Sleep, Quality Tiers, And Budget

- Activity error combines particle velocity, constraint error, root acceleration, collider motion, external force, and recent visibility or use (`PhysicsChainActivityEvaluation`). Sleep uses thresholds, a minimum quiet duration, and hysteresis.
- `PhysicsChainWakeReason` lists the wake triggers, for example root teleport, root acceleration, collider pose or shape change, force or event input, explicit request, relevance or visibility change, parameter change, and accumulated error.
- `PhysicsChainQualityTier` values are `Strict`, `Hz30`, `Hz15`, `Hz7_5`, `Sleep`, and `Automatic`. Each tier stores an exact rate, substep, iteration, collision, and palette policy. `Strict` simulates at the requested fixed rate and iteration count. Automatic tiers never change a strict chain.
- Automatic selection uses distance, projected size, visibility, importance, recent interaction, and budget pressure. Tiers have hysteresis and minimum residency time. Lower-rate chains are phase-staggered.
- Rendering interpolates current and previous simulated outputs (`PhysicsChainPaletteInterpolation`). Interpolation does not change physical elapsed time.
- `PhysicsChainOffscreenBehavior` selects `Simulate`, `DecayThenSleep`, `SleepImmediately`, or `AutomaticByImportance`.
- The budget controller uses delayed timing and error data. It changes only chains that allow automatic quality. It caps changes per frame and prefers sleeping irrelevant chains over reducing quality on important chains. Diagnostics show requested and effective tier, reason, and time in tier. A fixed-tier mode exists for tests and captures.

## Diagnostics

`PhysicsChainRuntimeDiagnostics` reports the selected backend, kernel family, quality policy, compatibility features, arena use, activity counters, and readback counters. Diagnostics are delayed or sampled so they do not add hot-path cost.

## Related Documentation

- [Physics Architecture](overview.md)
- [Physics Validation](../../work/testing/physics/physics-validation.md)
- [Physics-chain correctness contract](../../work/testing/physics/physics-chain-correctness-contract.md)
