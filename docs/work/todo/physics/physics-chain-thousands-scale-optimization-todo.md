# Physics Chain Thousands-Scale Optimization TODO

Last Updated: 2026-10-07
Status: Active
Branch: `physics-chain-gpu-covered-rendering`
Architecture: [Physics Chain World Runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Compute Backends](../../../architecture/physics/physics-chain-compute-backends.md), [Output And Readback](../../../architecture/physics/physics-chain-output-and-readback.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md)
Guide: [Physics Chain Performance](../../../developer-guides/rendering/physics-chain-performance.md)
Design: [Distance cadence and GPU presentation](../../design/physics/distance-cadence-gpu-presentation.md)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale)
Investigation: [Skinned GPU chain benchmark](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)
Implementation summary: [Completed code, validation, and resume point](../../progress/physics/physics-chain-scale-status-2026-10-07.md)

## Current State

The main GPU input, bounds, covered rendering, shadow, world clock, rest-input capture, and selective readback paths are implemented. Mapped input banks use capacity planning. Debug and readback resource access require active admission leases. Compute operations reuse sealed snapshot storage. The explicit rigid rest-input cache reduces gathering work; a reliable end-to-end gain remains unproven. Grouped GPU draws already exist. The scene publisher now retains its registration lookup, ordered source/primitive/draw identities, source groups, and primitive handle slices. Failed identity delivery retains clear recipients. Registration, source-group, and fixed-slot material contracts now have focused tests. Structural transaction plans remain the next code item on this path. The [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) owns the timing and callback-fault evidence. Extended acceptance and the 100 Hz target remain open. Every checkbox describes unfinished code or test work.

## Remaining Implementation Backlog

These features and refactors remain in scope. Use measurements to set their order. Keep runtime checks and performance acceptance in the [validation plan](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale); the [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) records the GPU timing, remaining CPU costs, and rejected shortcuts.

### World ownership and outputs

- [ ] Make `PhysicsChainComponent` hold only a `PhysicsChainRuntimeHandle` and authoring data. `PhysicsChainComponent.Particle.cs`, `PhysicsChainComponent.ParticleTree.cs`. Done when: the component has no per-particle solver state.
- [ ] Add capacity guards and delayed diagnostics to every arena writer. `PhysicsChainSlotArena`, `PhysicsChainDeferredLifetimeArena`. Done when: each writer reports overflow through `PhysicsChainArenaCapacityException` or a counter, with a unit test.
- [ ] Make renderers bind output slices from `PhysicsChainOutput` directly. Done when: no visible renderer path reads chain results through mutated bone transforms.
- [ ] Define reset and teleport semantics for current and previous state and palettes on spawn, teleport, template change, backend switch, and slot reuse. Done when: unit tests show that motion vectors never use unrelated history.

### CPU backend

- [ ] Remove LINQ, captured closures, boxing, transient arrays and lists, and non-struct enumeration from preparation, solve, apply, and debug paths. Pre-size reused collections by high-water marks. Done when: `Report-NewAllocations` shows no new entries in physics-chain files.
- [ ] Aggregate profiler counters per worker or bucket and merge once. Sample or compile out fine telemetry in production profiles. Done when: no per-chain profiler call remains in the hot loop.
- [ ] Add an AoSoA layout variant beside the SoA layout. `PhysicsChainCpuBackend`. Done when: the benchmark harness can select either layout.
- [ ] Bucket chains by segment count, topology, feature mask, and collider class. `PhysicsChainCpuKernelSelector`. Done when: hot loops have no per-particle feature branch.
- [ ] Use coarse ranges with work stealing for imbalance. Parallelize root and input gathering, solve, collision, palette, bounds, and opt-in mirror publication. `PhysicsChainCpuWorkScheduler`. Done when: each stage runs in worker ranges.
- [ ] Specialize CPU kernels by collision class: none, small fixed count, candidate list, and general. Done when: `PhysicsChainCpuKernelFamily` names each variant and unit tests compare each with the scalar reference.
- [ ] Specialize optional features (elasticity, stiffness, freeze axis, branching) only for feature masks with enough volume. Done when: each added variant has a scalar parity test.
- [ ] Make fixed-step accumulation, substep count, damping, gravity, external force, teleport, reset, and time scale one shared contract for CPU and GPU. Done when: both backends read one definition.
- [ ] Add a batched transform-mirror writer for consumers that request a mirror. `PhysicsChainCpuMirrorPolicy`. Done when: mirror writes run in worker ranges.

### GPU residency and submission

- [ ] Allocate permanent GPU arenas for templates, dynamic state, collider sets, roots and inputs, instance headers, active IDs, palettes, bounds, indirect commands, activity, and readback gather output. Bind them through stable offsets and generations in compact instance records. Done when: steady-state frames allocate no GPU buffers.
- [ ] Add capacity checks, clamped counts, overflow counters, and a next-frame resize policy for active lists and indirect arguments. Done when: a unit test overflows the active list and sees a counter and no corruption.

### Vulkan asynchronous submission experiment

- [ ] Add retained per-queue GPU timestamp ranges for split submissions. `VulkanCommandRuntime.AdvancedQueueOverlap`, `VulkanExplicitTargetRendererHost.GpuDiagnostics`, and Vulkan timing snapshots. Done when: each completed sample identifies its queue, submission, source frame, timestamp validity, and begin/end range; retrieval never waits; query storage remains retained until completion; and incomplete samples cannot appear as valid elapsed time.
- [ ] Add an opt-in physics submission split after an exact resource-use audit identifies independent work. `VulkanPhysicsChainComputeBackend`, `VulkanFrameLoop`, and `VulkanCommandRuntime` submission and lifetime owners. Done when: the selected secondary queue belongs to the graphics family and supports compute; captured inputs and all output resources remain retained through every submission; explicit dependencies join before same-frame palette, bounds, deformation, visibility, and shadow consumers; rejected or partially accepted work cannot reuse in-flight storage; and unsupported requested execution rejects without a hidden lane change. Keep cadence and output latency unchanged. Use the [research constraints](../../../developer-guides/rendering/physics-chain-performance.md#vulkan-asynchronous-compute-research) and the separate [runtime experiment](../../testing/physics/physics-validation.md#vulkan-asynchronous-physics-experiment).

### GPU kernels

- [ ] Extend the existing `ShortLinear` and `BranchedOrLong` buckets with justified feature and collider classes and small-count handling. `PhysicsChainKernelBucket` and the active-work shaders. Done when: the retained classes have explicit selection and empty-work behavior.
- [ ] Remove or repurpose unused shader-record fields. `GPUParticleData`, `GPUParticleStaticData`, and the physics-chain shaders. Done when: `PhysicsChainShaderContractTests` covers the retained layouts.
- [ ] Pack remaining eligible GPU indices, flags, and counts to smaller widths, with explicit range rejection. `GPUParticleData`, `GPUParticleStaticData`, and the shared shader records. Transform inputs already use the 48-byte affine record. Done when: retained C# and shader layout tests cover every packed field and reject overflow.
- [ ] Precompute remaining invariant GPU coefficient combinations. `PhysicsChainConstraints.glslinc`, GPU static records, and template packing. Rest lengths and capsule inverse terms already exist. Solved-position distance and direction are dynamic and must stay in the kernel. Done when: stiffness/weight and rest-limit terms have a correct version witness, and no invariant square root remains per step.
- [ ] Prototype palette and bounds writes in the final simulation pass. Done when: a dispatcher option selects fused or separate palette and bounds passes.

### Collision

- [ ] Sort or classify broadphase candidates by collider type when this reduces divergence. `PhysicsChainColliderBroadphase`. Done when: an option selects sorted candidates and a benchmark scenario can compare it.

### Skinning and draw submission

- [ ] Retain structural transaction plans across publications. `AdvancedGpuScenePublisher` and `AdvancedGpuScenePublisher.MaterialTransitions`. Done when: unchanged membership and exact source, geometry, material, and render-state inputs do not rebuild these plans; changed primitive counts, membership, source replacement, compaction, failure, and retirement invalidate the relevant plan; and current GPU bounds, output-page witnesses, and transaction capacity checks remain fresh. Build on the retained registration lookup, source groups, and committed material plans. Do not retain frame scratch closures as lifetime leases.
- [ ] Separate stable Advanced preparation templates from frame bindings. `AdvancedPreparationExtractor`, `AdvancedSharedPreparationService`, `AdvancedGpuDeformationResources`, `VulkanAdvancedVisibilityInputStorage`, and `VulkanPreparedStableBinStream`. Done when: unchanged mesh, material, topology, and group revisions reuse immutable preparation templates across render frames; current pose, bounds, root motion, interpolation, previous-render history, frame-specific resources, and output-page leases remain fresh; and an unsupported or changed source takes the existing strict preparation path. A held physics output page alone must not authorize reuse of a complete frame request or displayed deformation.
- [ ] Give the primary directional shadow tile an explicit native GPU consumer and command ownership contract. `DirectionalLightComponent.CascadeShadows`, `ShadowRenderPipeline`, `RenderCommandCollection`, and GPU shadow culling. Done when: eligible primary tiles avoid CPU mesh collection while retaining CastShadow, layer, mirror, and non-mesh policy, and rejected strict work remains dirty without an empty or CPU fallback draw. The existing cascade contract alone does not make the primary path safe.
- [ ] Use direct palette lookup in the vertex shader for small or one-pass meshes. Done when: the renderer selects direct lookup by mesh size.
- [ ] Route remaining chain compute-skinning paths through aggregate deformation. `AdvancedGpuDeformationResources`, `AdvancedDeformationDispatchPlanner`, and renderer skinning submission. Done when: no chain renderer issues its own skinning command or barrier.
- [ ] Batch the skinned-vertex bounds reduction for skinned meshes without chains. `SkinnedMeshBoundsCalculator.DispatchPathADirectWrite` dispatches once and uploads one sentinel for each command slot. Done when: one dispatch reduces all registered renderers, and the reduction can read the Advanced aggregate deformation output.

### Sleep and quality on the GPU

- [ ] Decide whether a reduced-rate GPU chain has work before full rest-input capture and preparation. `PhysicsChainWorld.GpuInputs`, `PhysicsChainWorld.Clock`, and `PhysicsChainComponent.GPU`. Done when: a no-solve update skips particle input gathering, the normal serial boundary advances the clock exactly once, reset and source changes invalidate the decision, and root movement remains pending until a solve consumes it.
- [ ] Separate physical step time from distance-based submission cadence. `PhysicsChainSimulationClock`, GPU dispatch snapshots, dispatcher state arenas, and the short and branched solver shaders. Done when: cumulative physical target ticks survive packet replacement and native retries, 60/30/15 Hz submission batches retain the authored physical step, and bounded catch-up retains unprocessed time.
- [ ] Add GPU presentation on rendered frames with no new solve. `GPUPhysicsChainDispatcher`, palette shaders, output history, and spatial bounds. Done when: timestamped root-relative simulation history produces the displayed palette, rate changes preserve a continuous presentation clock, reset seeds valid history, and CPU and GPU bounds cover the displayed mesh. Keep previous-render history separate from simulation history.
- [ ] Supply safe camera relevance for distance quality. `PhysicsChainWorld.QualityBudget`, component observation APIs, and the Math Intersections controller. Done when: observations enter under world ownership, use active color views from the same world, exclude shadow and probe cameras, and stale or unsupported observations retain full-rate GPU execution.
- [ ] Keep current and previous outputs coherent while a chain sleeps. Done when: a unit test wakes a chain and sees no history jump.
- [ ] Compute GPU activity, tier assignment, and sleep compaction without CPU readback. Done when: a strict zero-readback test passes with sleeping chains.

### Cleanup

- [ ] Remove obsolete queues, component work-item orchestration, transient GPU repacking, unsafe shader kernels, and redundant transform paths after the comparison gates pass. Done when: one runtime architecture remains, with no silent fallback to removed paths.

## Open Test Code

These items identify missing or stale tests. Most cover code that is already implemented; the resource tests also depend on the fixes above. Follow [AGENTS.md](../../../../AGENTS.md#validation) for test sequencing. Build, native capture, benchmark, and hardware checks remain in the [validation plan](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale).

### GPU bounds, materials, and output history

- [ ] Add failed-producer history tests for `AdvancedGpuDeformationResources`. Done when: failed, poisoned, disposed, unsubmitted, and device-lost production cannot yield `PreviousOutputValid`, while ordered submitted work needs no CPU completion wait.
- [ ] Add producer-token history tests for `AdvancedGpuDeformationResources.PhysicsHistory` and `AdvancedPreparationExtractor`. Done when: tests cover direct successors, held pages, multiple physics publications, frame gaps, same-frame reuse, and source replacement. Incompatible history must use current vertices as previous vertices.
- [ ] Update `PhysicsChainGpuBoundsContractTests` for the 48-byte bounds work item and the palette-box shader contract. The existing 16-byte expectation and particle-radius assumptions are stale. Done when: tests check the retained CPU/GPU layouts and malformed-bound rejection, and pass on the final code.
- [ ] Add material bounds contract tests. `PhysicsChainMaterialBoundsEvaluator`, `PhysicsChainMaterialBoundsContract`, and `GpuSceneRendererCommandIndexSnapshot.TryGetCommandMaterials`. Done when: tests show that each translation-class parameter increases the padding, scale, rotation, look-at, barrel, and non-finite values reject, a parameter value change recalculates the cached contract, combined contracts take the largest padding and keep a rejection, and route captures return the drawn material of an overridden command.
- [ ] Update `PhysicsChainMeshEnvelopeTests` and add palette-box containment tests for a thick weighted mesh. Include affine scale and shear, maximum substeps, fast motion, interpolation, teleport, sleep, offscreen wake, and current/prior blendshape extents. Done when: every current and valid previous deformed vertex fits the bound, raw morph history does not accumulate, and invalid input rejects the route.

### Covered renderer transitions and spatial bounds

- [ ] Add covered-mode transition tests for `RenderableMesh` and `XRMeshRenderer`. Done when: complete coverage has no bone subscriptions or CPU bounds refresh, partial coverage restores them, and source loss cannot leave a stale committed bound.
- [ ] Add committed spatial-snapshot containment tests for `GPUPhysicsChainDispatcher.SpatialBounds` and `PhysicsChainMeshEnvelope`. Done when: random valid poses, shrinking roots, animated scale, independent anchors, and held or failed production use matching request, bone, and output generations and contain the mesh.
- [ ] Add covered command-stability tests for `RenderableMesh.Transforms`, `RenderableMesh.CommittedBounds`, and `RenderInfo3D`. Done when: root-only motion causes no command matrix swap or culling-volume publication; the CPU spatial tree moves independently; and material, mesh, and LOD edits still publish.

### Retained Advanced draw plans and content sharing

- [ ] Add retained draw-plan tests for `AdvancedGpuScenePublisher.MaterialTransitions`. Done when: covered untextured identity-world draws skip material resolution and geometry planning until an exact source, mesh, material, draw metadata, render state, or generation witness changes; textured sources keep the full plan route; and a nonidentity-to-identity transition first writes a new plan.
- [ ] Add covered content-signature tests for `ComputeContentSignature`. Done when: covered root and GPU-bound motion produce no content delta, while ordinary or custom nonidentity source transforms still change content.
- [ ] Add immutable payload-sharing tests for `AdvancedGpuDeformationResources` and `AdvancedIndexedInstanceGroupPlanner`. Done when: identical content can share preparation and draws, differing content cannot share, and each member keeps independent pose, palette, bounds, draw identity, and current and previous outputs.

### GPU submission, instance groups, and directional shadows

- [ ] Add frozen submission-strategy and exact binding-readiness tests for `BackendReadyFramePackage`, `AdvancedPreparationExtractor`, and the native indirect lane. Done when: each consumer uses its package strategy, shared deformation is prepared once per world/scene/frame, and failed native binding admission cannot substitute another submission lane.
- [ ] Add directional shadow slice and strict-admission tests for `VPRC_AdvancedRenderStage`, Vulkan directional shadow resources, native recording, and shadow-atlas admission. The culling and grouped raster implementation is present. Done when: tests confirm that each cascade reads canonical caster inputs and current bounds, owns independent member/count/argument slices and sealed descriptors, applies its caster policy, and retries failed strict admission without CPU collection or fallback.
- [ ] Add indexed instance-group tests for `AdvancedIndexedInstanceGroupPlanner` and the group finalizers. Done when: exact content and range matches group, unequal content and authored multi-instance draws stay independent, partial visibility emits only matching members, and current/prior offsets and late-phase table selection remain correct.

### World inputs, clocks, and telemetry

- [ ] Add world GPU-input ordering tests for `PhysicsChainWorld.GpuInputs`, `PhysicsChainComponent.GpuInputs`, and `PhysicsChainGpuRestInputRange`. Done when: compatible ranges are captured and consumed once per phase; reset targets, root inertia, distance re-entry, known and opaque hierarchy dependencies, source changes, and slot reuse cannot admit stale matrices.
- [ ] Add world-clock tests for `PhysicsChainWorld.Clock` and `PhysicsChainSimulationClock`. Done when: tests cover cadence, rate changes, reset, backend changes, scheduling boundaries, and slot reuse.
- [ ] Add GPU input-cache tests for `PhysicsChainComponent.GPU` and `PhysicsChainWorld.GpuInputs`. Done when: route changes cannot reuse stale component transforms; parameter-only edits keep the submitted seed; and topology rebuilds advance the seed version.
- [ ] Add interval telemetry tests for `VulkanCompletedFrameIntervalTelemetry`. Done when: tests cover percentile calculation, wrap, authority change, reset, and dropped samples.

### Palette sharing, selective readback, and resource lifetime

- [ ] Add complete-palette sharing tests for `PhysicsChainPaletteAtlasAllocator`. Done when: compatible renderers with the same chain and mesh cause one palette generation; partial coverage and different bind data keep separate palettes.
- [ ] Update selective readback contract tests for source generations, independent world banks, solved affine values, and the 32-byte GPU gather item. `PhysicsChainReadbackRequestTests`, `PhysicsChainReadbackTransferTests`, and `PhysicsChainSelectiveReadbackDispatcherTests`. Done when: same-capacity source replacement, reset/transfer, cancellation/expiry, held input, renderer replacement, partial failure, and stale completion cannot deliver unrelated data or reuse retained storage. Remove stale dispatcher source-string expectations.
- [ ] Add deferred resource admission and debug lease tests. `RenderResourceLeaseOwner`, `VulkanFrameOpWorkspace`, `ComputeDispatchOp`, and debug request cancellation. Done when: concurrent release/retirement disposes once, retired zero-use owners cannot return to active, an occupied operation and sealed snapshot cannot be overwritten, and all rejected or cancelled requests release their uses.

### CPU and GPU parity

- [ ] Add scalar versus GPU tolerance tests across chain lengths, topologies, colliders, substeps, forces, reset, teleport, and large coordinates. Use the tolerances in the [correctness contract](../../testing/physics/physics-chain-correctness-contract.md). Done when: the tests exist.

## Decisions Needed

- Set additional named-hardware physics budgets and scaling targets. The current requirement is at least 100 completed rendered frames per second with 2,000 visible animated chains and frame-interval p95 at most 10 ms. Keep acceptance in the [validation plan](../../testing/physics/physics-validation.md). Owner: physics runtime.
- Select the presentation latency and history policy for distance quality. The user permits lower simulation cadence with smooth rendered interpolation. Keep full-rate measurements as the reference and report the exact distance profile for quality-tiered acceptance. Owner: physics runtime.
- Name the cross-vendor GPU and lower-tier CPU and GPU targets. Owner: physics runtime.
- Approve any serialized data change before implementation. Use a separate migration plan. Owner: maintainer.
- Decide whether a temporary old-versus-new runtime selector is necessary for comparison. Owner: physics runtime.

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
- General cross-family Vulkan asynchronous scheduling and automatic async-physics promotion. The bounded same-family experiment is listed above.
- GPU-only broadphase. GPU activity, tier assignment, and sleep compaction remain backlog items.
- Core-class-aware scheduling and NUMA partitioning.

Not planned: replacing the rigid-body solver or character controller, a broad ECS rewrite, silent quality reduction, silent CPU fallback, current-frame full-state readback, bit-identical CPU and GPU trajectories, and permanent support for two runtime architectures.
