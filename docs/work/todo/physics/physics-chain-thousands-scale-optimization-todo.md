# Physics Chain Thousands-Scale Optimization TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Physics Chain World Runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Compute Backends](../../../architecture/physics/physics-chain-compute-backends.md), [Output And Readback](../../../architecture/physics/physics-chain-output-and-readback.md)
Guide: [Physics Chain Performance](../../../developer-guides/rendering/physics-chain-performance.md)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#physics-chain-scale)

## Current State

`PhysicsChainWorld` schedules the chains, but components still own particle state and prepare GPU inputs. `GPUPhysicsChainDispatcher` has resident state and template buffers, GPU active-work compaction, indirect dispatch, short-linear and depth-ordered kernels, and global palette generation. Advanced aggregate deformation exists. GPU bounds reach legacy GPUScene slots, but not Advanced visibility candidates. The 2,000-chain benchmark remains below the required 100 completed rendered frames per second. Resume with canonical bounds ownership, shared renderer preparation, and world-owned GPU inputs. See the [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) for measurements and the [compute contract](../../../architecture/physics/physics-chain-compute-backends.md#implemented-gpu-solver-path) for existing kernel support.

## Open Code Items

### Canonical bounds and measured CPU costs

- [ ] Publish GPU bounds routes for the exact Advanced scene publication. `GPUPhysicsChainDispatcher.Bounds`, `GPUScene`, and `AdvancedPreparationExtractor`. Done when: each route identifies the draw handle and generation, current candidate index, bounds slot, and full deformation coverage; command compaction or slot reuse cannot redirect a route to another draw.
- [ ] Apply routed GPU bounds to frame-slot visibility candidates before early and late visibility. `AdvancedPreparationExtractor`, the backend visibility publishers, `EarlyVisibility.comp`, and `LateVisibility.comp`. Done when: route and buffer storage survives GPU consumption, bounds writes have the required barriers, and malformed numeric bounds cause a diagnostic rejection. Writing the legacy `CommandAabbBuffer` alone does not meet this condition.
- [ ] Preserve CPU view and shadow eligibility for renderers with full GPU deformation coverage. `AdvancedPreparationExtractor.ClassifyCandidatesForViews`, `VisualScene3D`, and `VulkanDirectionalShadowLaneCulling.ComputeRecordMasks`. Done when: stale CPU bone bounds cannot remove an eligible draw before GPU culling; layer, material, and `CastShadow` rules still apply. Partial coverage must retain a conservative bound for the remaining geometry.
- [ ] Share immutable renderer preparation for identical mesh, material, and pipeline combinations. `AdvancedPreparationExtractor` and `AdvancedGpuDeformationResources`. Done when: compatible chain renderers reuse mesh and material preparation while draw identity, pose, palette offsets, and current and previous outputs remain independent.
- [ ] Move GPU input gathering into world-owned ranges. `PhysicsChainWorld`, `PhysicsChainComponent.Prepare`, and `PhysicsChainComponent.GPU`. Done when: the normal GPU path consumes current root and authored-pose snapshots without per-component particle hierarchy evaluation; animated roots, rest pose, collider changes, reset, and teleport keep their existing semantics.
- [ ] Make GPU bone registrations identify their bone-buffer generation, then suspend redundant CPU palette subscriptions. `XRMeshRenderer`, `RuntimePhysicsChainRenderingBridge`. Done when: stale releases cannot change a replacement buffer's ownership; the last GPU owner restores the CPU subscription and current authored matrix; partial coverage, buffer replacement, rollback, and in-flight callbacks retain the locked ownership check. Keep bounds listeners attached until the canonical bounds route is complete.

### World ownership and outputs

- [ ] Make `PhysicsChainComponent` hold only a `PhysicsChainRuntimeHandle` and authoring data. `PhysicsChainComponent.Particle.cs`, `PhysicsChainComponent.ParticleTree.cs`. Done when: the component has no per-particle solver state.
- [ ] Replace per-component render commands with one render-graph integration point for GPU simulation, palette, bounds, optional readback, and global debug rendering. `PhysicsChainWorld`, `GPUPhysicsChainDispatcher`. Done when: a chain owns no steady-state render command.
- [ ] Add capacity guards and delayed diagnostics to every arena writer. `PhysicsChainSlotArena`, `PhysicsChainDeferredLifetimeArena`. Done when: each writer reports overflow through `PhysicsChainArenaCapacityException` or a counter, with a unit test.
- [ ] Make renderers bind output slices from `PhysicsChainOutput` directly. Done when: no visible renderer path reads chain results through mutated bone transforms.
- [ ] Define reset and teleport semantics for current and previous state and palettes on spawn, teleport, template change, backend switch, and slot reuse. Done when: unit tests show motion vectors never use unrelated history.
- [ ] Define palette sharing when many renderers use one chain. `PhysicsChainPaletteAtlasAllocator`. Done when: two renderers on one chain cause one palette generation.

### CPU backend

- [ ] Remove LINQ, captured closures, boxing, transient arrays and lists, and non-struct enumeration from preparation, solve, apply, and debug paths. Pre-size reused collections by high-water marks. Done when: `Report-NewAllocations` shows no new entries in physics-chain files.
- [ ] Aggregate profiler counters per worker or bucket and merge once. Sample or compile out fine telemetry in production profiles. Done when: no per-chain profiler call remains in the hot loop.
- [ ] Add an AoSoA layout variant beside the SoA layout. `PhysicsChainCpuBackend`. Done when: the benchmark harness can select either layout.
- [ ] Bucket chains by segment count, topology, feature mask, and collider class. `PhysicsChainCpuKernelSelector`. Done when: hot loops have no per-particle feature branch.
- [ ] Pad worker counters and output boundaries to prevent false sharing. `PhysicsChainCpuWorkScheduler`. Done when: shared counters are cache-line aligned.
- [ ] Use coarse ranges with work stealing for imbalance. Parallelize root and input gathering, solve, collision, palette, bounds, and opt-in mirror publication. `PhysicsChainCpuWorkScheduler`. Done when: each stage runs in worker ranges.
- [ ] Specialize CPU kernels by collision class: none, small fixed count, candidate list, and general. Done when: `PhysicsChainCpuKernelFamily` names each variant and unit tests compare each with the scalar reference.
- [ ] Specialize optional features (elasticity, stiffness, freeze axis, branching) only for feature masks with enough volume. Done when: each added variant has a scalar parity test.
- [ ] Make fixed-step accumulation, substep count, damping, gravity, external force, teleport, reset, and time scale one shared contract for CPU and GPU. Done when: both backends read one definition.
- [ ] Add a batched transform-mirror writer for consumers that request a mirror. `PhysicsChainCpuMirrorPolicy`. Done when: mirror writes run in worker ranges.

### GPU residency and submission

- [ ] Allocate permanent GPU arenas for templates, dynamic state, collider sets, roots and inputs, instance headers, active IDs, palettes, bounds, indirect commands, activity, and readback gather output. Bind them through stable offsets and generations in compact instance records. Done when: steady-state frames allocate no GPU buffers.
- [ ] Upload compact per-instance dynamic headers, roots, and forces through a persistently mapped multi-frame ring (`XRBufferPersistentRingAllocator` or the backend equivalent). Done when: header upload uses no per-frame buffer object.
- [ ] Add capacity checks, clamped counts, overflow counters, and a next-frame resize policy for active lists and indirect arguments. Done when: a unit test overflows the active list and sees a counter and no corruption.

### GPU kernels

- [ ] Extend the existing `ShortLinear` and `BranchedOrLong` buckets with justified feature and collider classes and small-count handling. `PhysicsChainKernelBucket` and the active-work shaders. Done when: the retained classes have explicit selection and empty-work behavior.
- [ ] Move fixed-step accumulation from the component into compact world-owned instance state. `PhysicsChainComponent.ResolveSimulationLoopAndTimeScale` and `PhysicsChainWorld`. Done when: each instance retains its clock across scheduling boundaries and publishes the resolved loop count and time scale. GPU loop counts and dispatch grouping already support fused per-tree substeps.
- [ ] Remove or repurpose unused shader-record fields. `GPUParticleData`, `GPUParticleStaticData`, and the physics-chain shaders. Done when: `PhysicsChainShaderContractTests` covers the retained layouts. Static templates and dynamic particle state already use separate buffers.
- [ ] Replace full 4x4 transform traffic with affine 3x4 or quaternion and translation inputs. Pack indices, flags, and counts to smaller widths. Done when: C# and shader layout tests pass.
- [ ] Precompute rest lengths, inverse values, capsule terms, and coefficient combinations for the GPU path. Done when: the kernel computes no invariant square root per step.
- [ ] Prototype palette and bounds writes in the final simulation pass. Done when: a dispatcher option selects fused or separate palette and bounds passes.

### Collision

- [ ] Sort or classify broadphase candidates by collider type when this reduces divergence. `PhysicsChainColliderBroadphase`. Done when: an option selects sorted candidates and a benchmark scenario can compare it.

### Skinning, draw submission, and debug

- [ ] Use direct palette lookup in the vertex shader for small or one-pass meshes. Done when: the renderer selects direct lookup by mesh size.
- [ ] Route remaining chain compute-skinning paths through aggregate deformation. `AdvancedGpuDeformationResources`, `AdvancedDeformationDispatchPlanner`, and renderer skinning submission. Done when: no chain renderer issues its own skinning command or barrier. Reuse the existing Advanced aggregate path.
- [ ] Instance identical mesh, material, and pipeline combinations with a palette-base instance attribute or table lookup. Done when: identical chain renderers share draw and canonical preparation work; per-instance pose and palette offsets remain independent. Reducing draw count alone does not remove per-renderer CPU preparation.
- [ ] Feed chain-driven renderers into GPUScene indirect culling. Done when: chain renderers cause no per-renderer CPU draw submission.
- [ ] Report renderer, skinning dispatch, indirect command, draw, and triangle counts beside physics timing. Done when: `PhysicsChainRuntimeDiagnostics` exposes the counts.
- [ ] Replace per-chain debug render commands with one global compact debug buffer and indirect draw. Generate debug geometry only for selected or visible chains or a bounded sample. Keep debug readback and validation scans out of production profiles. Done when: debug cost is zero with debug off.
- [ ] Add editor diagnostics for handle, template, bucket, state slice, quality tier, sleep state, palette slice, bounds, and last error. Done when: the editor shows them without a synchronous GPU dump.

### Sleep and quality on the GPU

- [ ] Keep current and previous outputs coherent while a chain sleeps. Done when: a unit test wakes a chain and sees no history jump.
- [ ] Compute GPU activity, tier assignment, and sleep compaction without CPU readback. Done when: a strict zero-readback test passes with sleeping chains.

### Tests

- [ ] Add scalar versus GPU tolerance tests across chain lengths, topologies, colliders, substeps, forces, reset, teleport, and large coordinates. Use the tolerances in the [correctness contract](../../testing/physics/physics-chain-correctness-contract.md). Done when: the tests exist.
- [ ] Add GPU bounds conservatism tests for fast motion, interpolation, teleport, sleep, and offscreen wake. Done when: the tests exist beside `PhysicsChainGpuBoundsContractTests`.

### Cleanup

- [ ] Remove obsolete queues, component work-item orchestration, transient GPU repacking, unsafe shader kernels, and redundant transform paths after the comparison gates pass. Done when: one runtime architecture remains, with no silent fallback to removed paths.

## Decisions Needed

- [ ] Set additional named-hardware physics budgets and scaling targets. The current user requirement is already fixed: at least 100 completed rendered frames per second with 2,000 visible animated chains. Keep this acceptance check in the [validation plan](../../testing/physics/physics-validation.md). Owner: physics runtime.
- [ ] Name the cross-vendor GPU and lower-tier CPU and GPU target machines. Owner: physics runtime.
- [ ] Approve any serialized data change before it is made. Create a separate migration plan. Owner: maintainer.
- [ ] Decide whether a temporary old-versus-new runtime selector is necessary for comparison. Owner: physics runtime.

## Out Of Scope

Experiments. Promote one only when a named workload proves the benefit, with a correctness result and an explicit fallback:

- AVX-512 CPU kernels.
- An angular joint representation for linear chains.
- A segment-major wave GPU kernel.
- Reciprocal square root or reduced-precision GPU math.
- Kernel autotuning by vendor, segment bucket, collider class, and active count.
- 16-bit storage for indices, rest data, coefficients, or state.
- Quaternion or dual-quaternion palette output.
- Shared-memory collider staging.
- Persistent GPU work queues or cooperative kernels.
- Vulkan asynchronous compute.
- GPU-only broadphase. GPU activity, tier assignment, and sleep compaction remain open code items above.
- Core-class-aware scheduling and NUMA partitioning.

Not planned: replacing the rigid-body solver or character controller, a broad ECS rewrite, silent quality reduction, silent CPU fallback, current-frame full-state readback, bit-identical CPU and GPU trajectories, and permanent support for two runtime architectures.
