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

- The component boundary contains editable settings and a runtime handle. `PhysicsChainRuntimeGraph` holds the particle trees and their first authored local poses. A weak pending binding preserves these poses across disable and re-enable. The live world slot retains the same graph. The component caches a nonserialized reference; packing, quality, and worker scratch still reside in its partial files. The world owns the simulation clock and one reused GPU rest-input range for each runtime slot.
- `PhysicsChainWorld` owns registration, capacity, scheduling, state transitions, and output publication. One world-level tick schedules all chains. A component does not register its own tick callbacks.
- Chains are addressed through `PhysicsChainRuntimeHandle`, a slot plus a generation. Every external operation checks the generation. A stale handle fails deterministically. It never aliases reused storage.
- The world keeps clock state in a compact slot-indexed sidecar. Registration seeds the cadence phase. Rate changes keep normalized progress. Reset clears elapsed time and fixed-render ticks. Slot removal clears the clock before reuse.
- `PhysicsChainDeferredLifetimeArena` retires registration metadata by world epoch. Native GPU output pages use their own exact producer tokens, fences, and consumer leases. A metadata epoch is not proof that a GPU consumer has finished.

## Implementation Map

### Mapped GPU inputs

`GPUPhysicsChainDispatcher.InputPages` retains eight input pages. Each page
owns persistently mapped coherent header, instance, tree-work, collider, and
authored-transform buffers. The dispatcher copies reused CPU ranges into a
reserved page and emits a mapped-write barrier before GPU reads. The source
catalog changes beside each committed group upload. A reused journal restores
the catalog and invalidates upload witnesses when that group rolls back.

The bridge reads borrowed `PhysicsChainGpuDispatchSnapshot` spans and writes
them directly into the owned pending packet. It retains no borrowed span or
intermediate staging list. Reused pending and accepted arrays keep the packet
valid until renderer acceptance.

Authored affine transforms use `PhysicsChainAffineInput`, a 48-byte record with
three `Vector4` columns. Each column stores three linear terms and one
translation term. The shared shader decoder restores the homogeneous row.
The pending packet checks finite values and the exact affine row before
acceptance. A malformed newer packet rejects the publication without changing
the accepted input identity or substituting CPU work. Palette-input reads,
gather-input reads, and rollback storage use this input layout. Skinning
palettes use a separate 48-byte output layout.

World-wide selective gathers run after all solve groups commit. Page markers
are sealed after gathers and output publication. Pending or unsubmitted work
prevents reuse. On a healthy Vulkan renderer, a failed marker permits reuse
only when every exact native buffer generation has no queued references and
all queue sequences have completed. A device fault or unavailable proof
quarantines the page. No CPU fallback replaces a rejected GPU dispatch.

`InputPageDiagnostics` reports acquisitions, mapped writes, buffer allocations,
busy attempts, failed markers, quarantined pages, and the last failure. A
steady-state timing window requires advancing acquisitions and writes, no new
failure, no quarantined page, and no new input-buffer allocation.

### Runtime types

- `PhysicsChainWorld.cs` owns the scheduler entry points. Its partial files add lifecycle snapshots, clock and GPU input ranges, template and collider-set cache access, readback extensions, telemetry, quality-budget evaluation, and selected activity diagnostics.
- `PhysicsChainComponent` owns authoring and lifecycle entry points. Its partial files use the bound runtime graph and keep GPU input preparation, CPU preparation, quality state, worker scratch, and diagnostics.
- `GPUPhysicsChainDispatcher` in `XREngine.Runtime.Rendering` owns GPU simulation, active-work compaction, indirect dispatch, resident palette storage, bounds publication, selective readback gather, and debug geometry output.
- `VisualScene3D.GlobalPreRender` schedules the dispatcher through the rendering host bridge. `GlobalPostRender` processes completion. A chain owns no render command. Viewport debug drawing uses the depth-tested physics pass.
- `PhysicsChainReadbackService` and `PhysicsChainWorldReadbackExtensions` own delayed selected readback. Requests use `PhysicsChainReadbackFields`, limits, generations, expiry, and terminal states.
- `PhysicsChainWorld.QualityBudget`, `PhysicsChainQualityBudgetDiagnostics`, and `PhysicsChainQualityPolicy` own automatic tier pressure. They do not change chains that request `Strict`.
- `PhysicsChainWorld.Telemetry` and `PhysicsChainLateTickTelemetrySnapshot` own delayed timing counters. `PhysicsChainWorld.ActivityDiagnostics` owns bounded selected activity output.

## Commands

Commands are split into two kinds:

| Kind | Type | Values | When applied |
| --- | --- | --- | --- |
| Structural | `PhysicsChainWorldCommandKind` | `Add`, `Remove`, `Retemplate`, `Resize`, `Rebind`, `BackendSwitch` | Only at a world boundary. They can rebuild a bucket or grow an arena outside the hot dispatch. |
| Dynamic | `PhysicsChainWorldDynamicCommandKind` | `Root`, `Force`, `Parameters`, `Relevance`, `Quality` | After structural commands. Latest value wins. They never rebuild unrelated instances. |

Ordinary motion never looks structural.

### World and hierarchy transitions

World ticks hold a shared transition gate through worker completion. A cold
world, parent, child-list, or activation change takes the exclusive gate
before it changes the hierarchy. Independent worlds can still tick in
parallel. The transition waits for source and destination workers, retires
the exact old handle, and blocks new registration through world propagation.
Registration resumes after the transition releases its gates.

The transition captures the full affected subtree, its ordinary components,
and its physics owners. It checks the snapshot again before each mutation.
Detached construction can join an active transition only when every captured
object has no world and every physics component has no runtime owner.

Do not hold a caller-owned child-list monitor when you enter a transition.
Do not change the hierarchy from a physics worker or a hierarchy read
predicate. These paths reject before they wait or change fields. Nested
lifecycle callbacks use the current transition and its recorded objects and
child pairs. They do not start another world transfer.

### World disposal

`RuntimeWorld` disposal closes admission on its `PhysicsChainWorld` first. After
that, the world accepts no new registration, command, or tick. A tick that is
in progress finishes. A context without a disposal event must call
`PhysicsChainWorld.Release(context)`. That context cannot register chains again.

- Disposal waits for the current tick. A batch worker of the world never waits
  for its own tick. The outer tick finishes the teardown after all workers
  signal completion.
- If code inside a tick or a batch worker requests disposal of the owning
  `RuntimeWorld`, that disposal runs after the tick releases its gate and its
  transition read lock. Owner disposal can change the scene, so it cannot run
  inside a tick.
- Teardown releases only the bindings that the disposing world owns. A
  component that already moved to another world registers there again.
- A deferred transfer never adds a component to a disposed destination. The
  destination rejects the add command after admission closes.
- Readback calls on a disposed world do not throw. Disposal frees the
  requests, and new requests fail with `InvalidInstance`. In-flight staging
  slots keep their renderer fences. The next `PollReadbackTransfers` call on the
  render thread releases them, and then the renderer retires the world. A
  readback call from a batch worker of the same world throws.
- Teardown never shrinks slot-indexed lists. Some readers resolve handles
  without the tick gate, so teardown invalidates every slot instead.

## Runtime Records

| Record | Lifetime | Contents |
| --- | --- | --- |
| `PhysicsChainTemplate` | Immutable, deduplicated by `PhysicsChainTemplateCache` | Topology order, parent indices, depth ranges (`PhysicsChainDepthRange`), rest data, coefficient packs (`PhysicsChainCoefficientPack`), bone mapping, influence bounds (`PhysicsChainInfluenceBounds`), feature mask (`PhysicsChainTemplateFeatureMask`) |
| `PhysicsChainInstance` | Stable registration | Template, collider set, input slice, state slice, palette slice, bounds slot, quality policy, flags, generation |
| `PhysicsChainState` | Backend-owned | Current and previous particle state, velocity terms, sleep and error state, simulation clock |
| `PhysicsChainColliderSet` | Shared and versioned, deduplicated by `PhysicsChainColliderSetCache` | Typed compact collider arrays, pose data, broadphase metadata |
| `PhysicsChainOutput` | Backend-neutral registration metadata | Logical palette ranges, bounds slot, validity, generation, backend status. Its registration ranges do not identify native renderer storage. GPU consumers bind the dispatcher's retained output pages. |

Template identity is computed at structural change time and cached. The runtime never deep-hashes immutable arrays per frame. Editor changes to a chain rebuild its template version.

## Arenas And Capacity

- `PhysicsChainSlotArena` uses free lists and generational slots. Capacity grows geometrically. Live count, capacity, growth, and rejected capacity attempts are reported in `PhysicsChainArenaSnapshot`. `TryAllocate` returns an invalid handle and increments `CapacityFailureCount` at the configured limit. `Allocate` then throws `PhysicsChainArenaCapacityException`. The deferred arena exposes the same counter and limit.
- `PhysicsChainArenaCompactionPolicy` defines fragmentation thresholds. Compaction is an explicit out-of-band rebuild. Live GPU slices never move while a consumer uses them.
- Capacity failure raises `PhysicsChainArenaCapacityException` or a reported rejection. The runtime never truncates particles, chains, colliders, palettes, bounds, or readback requests.
- Dynamic headers, instance rows, tree work, collider inputs, and authored transforms use the retained mapped input pages described above. Capacity growth occurs outside steady dispatch. Each page retains its exact native generations until reuse is safe.

## Backends

`PhysicsChainRuntimeBackend` selects `CpuDataOriented`, `GpuBatched`, or `GpuStandalone`. GPU-covered renderers consume retained palette and bounds pages. CPU renderer integration still uses transform publication. An explicitly selected GPU backend never falls back to CPU simulation.

### CPU backend

`PhysicsChainCpuWorkScheduler` uses pinned counter storage with a 64-byte
aligned base and 128-byte worker slots. Completion and failure counters use
separate cache lines. The scheduler joins every dispatched worker before it
clears the batch or rethrows a captured executor fault. Worker exceptions
cannot prevent completion signaling or end a reusable worker thread.

- `PhysicsChainCpuBackend` keeps state, outputs, and palettes in separate arrays per runtime instance. The AVX2 linear kernel visits one particle index across eight compatible instances. A packed AoSoA layout remains open.
- `PhysicsChainCpuWorkScheduler` partitions work by estimated particle, collider, and substep cost on a persistent worker pool. It does not queue one work item per chain. A deterministic mode gives stable reference ordering.
- `PhysicsChainCpuKernelSelector` picks a `PhysicsChainCpuKernelFamily`: `ScalarLinear` (`PhysicsChainScalarReferenceKernel`), `Avx2LinearBatch` (`PhysicsChainAvx2LinearBatchKernel`, `Vector256<float>`), or `DepthOrderedBranched` (`PhysicsChainDepthOrderedBranchedKernel`). Branched topology always uses the depth-ordered kernel. It is never flattened into the linear family.
- The steady-state CPU path allocates nothing and takes no global lock. Structural commands are batched before workers start. Outputs publish after range completion without one atomic per chain.
- The backend writes current and previous skin palettes (`PhysicsChainCpuSkinPaletteComposer`) and conservative bounds from particles plus influence radii. It does not mutate the transform hierarchy. Palette, bounds, or mirror work is skipped when no consumer needs it (`PhysicsChainCpuConsumerFlags`).

### GPU backend

- The normal GPU input route composes rest matrices in a reused world-owned range. It reads live root translation, live scale and transform order, and the saved rest rotation and child translation. It supports all six built-in transform orders. Independent roots use their actual external parent matrices. The route does not reset or recalculate each particle transform in the scene hierarchy.
- The range retains local rest matrices. Exact translation, scale, and transform-order bits control reuse. A topology capture invalidates those matrices. Each capture still reads and composes the actual external parent matrix.
- The world checks runtime-handle generation, direct parent links, shared reset-node ownership, external parent and collider dependencies, custom transforms, and CPU bone mirroring. Fast capture also requires root rotation and child translation/rotation to match the initial rest values bit for bit. If a reset must change a value, `AuthoredRestResetRequired` selects the hierarchy compatibility input route for that frame. This preserves reset effects on other chains, parameter changes, and hierarchy consumers. Before any chain captures inputs, a world prepass marks both readers and owners of hierarchy dependencies. This includes excluded child roots, collider inputs, root tracking, and enabled distance references. `ForceManualRecalc` particles keep explicit hierarchy preparation. An external parent that serial root or distance preparation can recalculate uses `PrerequisiteMatrixRefresh`. Root tracking can recalculate during reset even with zero root inertia. The prepass also detects opaque custom input transforms. Their source dependencies can lie outside their parent chains. `OpaqueTransformDependency` keeps all possible source chains on the hierarchy input route for that phase, so matrix-change notifications retain their ordering. The GPU solver remains selected. Per-reason counters report these cases.
- One serial world stage captures all compatible GPU rest-input ranges before component dispatch. Each component consumes only its current world-phase range and exact runtime-handle generation. Consumption is single-use. A source-version change rejects the range. A capture fault is reported at that component's serial tick boundary. Compatibility inputs keep the existing hierarchy preparation order. The stage does not run transform getters on worker threads.
- `GPUPhysicsChainDispatcher` uploads template data once per template version and collider topology once per collider-set version. After that it uploads only dirty collider poses and dirty dynamic ranges (`GPUPhysicsChainUploadPlan`). Steady-state frames do not snapshot, repack, or copy whole resident groups.
- Active work is compacted on the GPU (`PhysicsChainComputePassKind.ActiveWorkCompaction`). `PhysicsChainActiveWorkScanMode` uses `SubgroupArithmetic` where supported and `PortableWorkgroup` otherwise. Profiles show the selected mode.
- Indirect dispatch arguments (`PhysicsChainIndirectDispatchArguments`) are GPU-written. GPU-written counts are dynamic data. They do not cause rerecording of stable pass topology.
- Kernels run in explicit parent-before-child order. No invocation reads a parent that another invocation may still write without a synchronization boundary. Substeps are fused in the kernel where register and watchdog limits allow.
- Retained output pages own current and previous palette atlases. The dispatcher copies matching prior palette slices with ordered GPU work. A page remains pinned while a renderer consumes its generation. Slot and source changes reset unrelated history.
- Bounds reduce the current and matching previous canonical skin palettes over each mesh's retained local box. Material contracts add finite translation padding or reject unsupported effects. A retained output page supplies the canonical Advanced bounds patch. The same publication supplies a conservative CPU spatial snapshot with exact source generations. See [canonical GPU bounds](physics-chain-output-and-readback.md#canonical-gpu-bounds).
- Strict zero-readback profiles read nothing back for simulation, palettes, bounds, culling, or dispatch sizing.

Pass kinds are listed in `PhysicsChainComputePassKind`: arena growth, active-work reset and compaction, indirect argument generation, simulation, selective readback gather, bounds publication, bone palette publication, readback transfer, and debug visualization.

## Collision

- Colliders live in shared versioned sets. Static shape data is separate from dynamic pose data (`PhysicsChainColliderPoseBuffer`). Only dirty poses update.
- Invariant shape terms (capsule direction, inverse length squared, radii sums, plane terms) are precomputed.
- A packed capsule stores its start and radius in `Center`, its end and inverse squared length in `Params`, and its direction in `Orientation`. The solver uses these packed terms for its segment projection. A zero-length capsule uses the sphere path.
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

`PhysicsChainRuntimeDiagnostics` reports the runtime handle, template, backend, kernel family, quality policy, sleep state, state and palette slices, bounds slot, output generation, compatibility features, arena use, activity counters, and readback counters.

`PhysicsChainRenderingDiagnostics` reports bound renderers and captured scene-frame counts for visible renderers, aggregate deformation, legacy skinning, prepared indirect command slots, draw calls, and CPU-visible triangles. Submitted indirect draw counts are available on Vulkan. These counts cover the full scene. They do not read GPU buffers. The ImGui inspector displays them in the expanded Physics Chain Runtime section, with the conservative-bound status and last global GPU error.

Complete palette mappings share one atlas slice and one palette dispatch when renderers use the same chain and mesh. Partial coverage keeps separate mappings because bones outside the chain can differ. Different mesh bind data also keeps separate mappings.

## Related Documentation

- [Physics Architecture](overview.md)
- [Physics Validation](../../work/testing/physics/physics-validation.md)
- [Physics-chain correctness contract](../../work/testing/physics/physics-chain-correctness-contract.md)
