# Physics Chain Thousands-Scale Optimization TODO

Last Updated: 2026-10-08
Status: Active
Branch: `physics-chain-gpu-covered-rendering`
Architecture: [Physics Chain World Runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Compute Backends](../../../architecture/physics/physics-chain-compute-backends.md), [Output And Readback](../../../architecture/physics/physics-chain-output-and-readback.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md)
Guide: [Physics Chain Performance](../../../developer-guides/rendering/physics-chain-performance.md)
Design: [Steady-state CPU design](../../design/physics/physics-chain-steady-state-cpu-design.md), [Distance cadence and GPU presentation](../../design/physics/distance-cadence-gpu-presentation.md)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale)
Investigation: [Skinned GPU chain benchmark](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)

## Current State

The GPU input, bounds, covered rendering, shadow, world clock, rest-input capture, and selective readback paths are implemented. The scene publisher retains registration identities, source groups, and primitive handle slices. Chains that share one mesh draw as one indexed instance group. The scale harness reports frame-loop phase timings (`phaseTimings`), records the machine state and in-window GPU clocks, fits a chain-count ladder (`-Ladder`), and selects shared or unique mesh content (`-MeshSharing`). Each covered renderer has a committed CPU bound version and an enlarged proxy bound, so an unchanged bound no longer reconciles or moves the CPU spatial tree. At 2,000 chains the frame is CPU-bound at 16–25 Hz. After the bound change, collect is below render, and the render callback plus the serial swap (about 42 ms) is the critical path. Shared and unique meshes measure the same. The first ladder measured about 20 µs of frame interval for each chain, against a budget of about 2.5 µs. The target applies to a shared-mesh and a unique-mesh scenario. The [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) owns the evidence, and the [steady-state CPU design](../../design/physics/physics-chain-steady-state-cpu-design.md) owns the response that the items below implement.

## Priority Order

Steady-state target: an unchanged, fully covered GPU chain and its renderer cause no per-frame CPU work, except the bulk root write and a fixed number of dispatches and draw commands for each bucket. See the [design rules](../../design/physics/physics-chain-steady-state-cpu-design.md#design-rules).

Make decisions from the cost per chain of each frame phase, not from the frame rate alone, and measure each change in both mesh modes.

1. [Ceiling prototypes](#ceiling-prototypes). Measure the best case of the target structure before the full implementation.
2. [Shared versioning contract](#shared-versioning-contract). The physics and renderer items below use it to skip unchanged rows.
3. [Physics steady state](#physics-steady-state).
4. [Change-driven renderer path](#change-driven-renderer-path) and [serial swap](#serial-swap). Both mesh scenarios need these items.
5. The remaining sections. After the CPU frame meets its budget, GPU time becomes the next limit. The API trace measured about 8.8 ms of GPU time per frame at 2,000 chains, with uncontrolled clocks.

Keep runtime checks and performance acceptance in the [validation plan](../../testing/physics/physics-validation.md#bottleneck-measurements).

## Open Code Items

### Ceiling prototypes

These prototypes are temporary measurement tools, not correctness paths. Each one uses an explicit, non-persistent flag that the benchmark sets only for a fixed population with no structural change. The default path stays unchanged. Remove each prototype after its validation check records a result.

- [ ] Add a frozen-scene ceiling flag for fully covered chain renderers. After settle, collection, swap, scene publication, and Advanced preparation reuse the last accepted state for these renderers. GPU physics, palettes, and bounds continue to update. `VisualScene3D`, `RuntimeWorldRenderer.GlobalSwapBuffersCore`, `AdvancedGpuScenePublisher`, and `AdvancedSharedPreparationService`. Done when: the flag works in the shared and unique mesh modes, and a structural change while the flag is set stops the benchmark with a diagnostic instead of drawing stale state.
- [ ] Add a root-only physics ceiling flag. In the steady state, the frame writes the root affine records into the mapped input bank and records the fixed indirect dispatches. It skips per-request input acceptance, bone-binding refresh, rest dependency checks, quality assignment, and the activity scan for unchanged chains. `GPUPhysicsChainDispatcher.ProcessDispatchesCore`, `PhysicsChainWorld.GpuInputs`, and `PhysicsChainWorld.QualityBudget`. Done when: the flag works with chain rendering hidden and shown, and a structural or input-source change while the flag is set stops the benchmark with a diagnostic.

### Shared versioning contract

- [ ] Add one shared page-version type for dense row stores. Put it in a shared low-level project so that Core and Rendering can both use it. Each page of rows has a local 64-bit version that increases only on a real write to a row in that page. A reader keeps its last-read version for each page and skips unchanged pages. Done when: unit tests cover a write, an unchanged read, scattered and clustered changes, page growth, row removal, and a reader that misses several writes; and a "no changes" read of 2,000 rows makes one comparison for each page.
- [ ] Give `TransformHierarchyStore` page-local sequences and a range read. Today one `_sequence` covers the whole store, each write takes `_gate`, and each read uses a full memory barrier. A write to any transform therefore makes concurrent readers of every transform retry. Done when: a write to one page does not make readers of another page retry; a range read copies many matrices under one sequence check; and unit tests cover a concurrent writer during a range read, a stale handle, and growth.

### Physics steady state

- [ ] Replace the per-request loops in `GPUPhysicsChainDispatcher.ProcessDispatchesCore` with page-versioned request rows. In each render frame, the dispatcher calls `TryAcceptInput` and `PhysicsChainComponent.AcceptGpuDrivenBoneBindings` for every active request, and `RefreshGpuDrivenBoneBindings` for every registered component, under the registration locks. Done when: an unchanged population causes no per-request work on the render thread, and unit tests show that input, binding, and registration changes still reach the dispatcher with the current latency.
- [ ] Replace per-tick rest dependency checks with versioned dependency sets. `PhysicsChainWorld.GpuInputs` (`PrepareGpuRestInputPhase`, `TryCaptureGpuRestInputs`) and `PhysicsChainComponent.HasGpuRestColliderDependency`. The opaque, owner, and collider checks cost about 4.4 µs per chain per tick, but depend only on topology, ownership, and the collider list. Each range keeps the hierarchy topology version and the versions of the pages that hold its dependencies, and checks again only when one of them changes. Done when: an unchanged range causes no dependency walk, and unit tests show that child transform, child order, reparent, collider membership, collider transform, and ownership changes still block or refresh the range.
- [ ] Replace per-tick `IsActiveInHierarchy` walks with a world-owned active flag for each slot. `PhysicsChainWorld` loops and the collider checks in `PhysicsChainComponent.GpuInputs` call it about 13 times per chain per tick, and each call recurses up to the scene root. Done when: activation and hierarchy changes update the flag at the world boundary, and steady-state loops read a dense flag; unit tests cover activation and deactivation of the chain, an ancestor, and a collider.
- [ ] Keep the fast GPU pack path for chains with colliders. `PhysicsChainComponent.GPU.cs` takes `PrepareGpuDispatchDataFromWorld` only when the chain has no colliders, so every benchmark chain does the full particle pack. Done when: collider data has its own version, an unchanged collider set does not force the full pack, and a unit test shows that a collider change still reaches the GPU input.
- [ ] Write colliders and the transform catalog into a mapped input page only when that page's copy is stale. `UploadInputPageColliders` and `UploadInputPageTransforms` in `GPUPhysicsChainDispatcher.InputPages.cs` now write all colliders and the full catalog into each page. Done when: each mapped bank records the versions it holds, unchanged rows are not written again, and a unit test rotates through all banks after a collider change and finds current data in each.
- [ ] Write all chain roots in one bulk pass. Keep a dense root-handle array in `PhysicsChainWorld`, and write the 48-byte root affine records into the mapped input bank in one loop. Done when: steady-state root capture has no per-chain virtual call, lock acquisition, or callback, and the GPU input layout is unchanged.
- [ ] Make quality assignment and the activity scan one pass over dense world arrays. `PhysicsChainWorld.QualityBudget` and `PhysicsChainWorld.ActivityDiagnostics`. Done when: these stages have no per-component virtual call or lock, and they do no work when the population, the camera tier inputs, and the settings are unchanged.
- [ ] Make the bones of fully covered GPU chains dormant on the CPU. While coverage is complete, these bones get no world-matrix recalculation, render-matrix publication, or change events. CPU bone values are created only for selective readback or loss of coverage. `RuntimeWorldRenderer.ApplyRenderMatrixChanges`, the transform hierarchy publication, and `RenderableMesh.ProcessPendingRenderMatrixUpdates`. Done when: a unit test shows that a fully covered chain publishes no bone render matrices, and that loss of coverage reseeds the bones before the next CPU consumer reads them.
- [ ] Make `PhysicsChainComponent` hold only a `PhysicsChainRuntimeHandle` and authoring data. `PhysicsChainComponent.Particle.cs`, `PhysicsChainComponent.ParticleTree.cs`. Done when: the component has no per-particle solver state.
- [ ] Make renderers bind output slices from `PhysicsChainOutput` directly. Done when: no visible renderer path reads chain results through mutated bone transforms.

### Change-driven renderer path

These items apply to shared and unique meshes. A fully covered renderer gets its pose and bounds from the GPU. After registration, it needs no per-frame CPU work until its source, material, mesh, or coverage changes.

- [ ] Resolve committed bounds once for each renderer in each frame. `TryGetCommittedWorldBounds` ran about 9–12 times per chain per frame before the bound version change, with about 5 locks and 6 hash lookups for each call. `RenderableMesh.ReconcileCommittedWorldBounds` alone still runs about 4 times per mesh per frame, and each call queries the bound. Done when: collection, reconcile, BVH moves, and shadow admission read one per-frame resolved value; and a unit test shows that a new output page in a later frame is still seen.
- [ ] Keep fully covered renderers in a persistent GPU-culled draw set. `VisualScene3D.CollectRenderedItemsGpu`, `RuntimeWorldRenderer.GlobalPreCollectVisible`, and `CpuBvhRenderTree`. Today `RefreshGpuBoundsEligibility` rebuilds its list and set in every frame, and `CanUseCanonicalGpuCollection` runs twice for each mesh. Done when: a fully covered renderer enters the set once at registration and leaves it at removal or loss of coverage; steady-state collection does not visit unchanged covered renderers; and unit tests cover entry, removal, and coverage loss.
- [ ] Publish only changed rows from `AdvancedGpuScenePublisher`. `TryBuildAndPreflightWholeScenePlan` plans every command in each publication, and the reuse check runs only after that plan. Retain structural transaction plans across publications and use page versions for the command rows. Done when: an unchanged publication does not visit unchanged rows; changed primitive counts, membership, source replacement, compaction, failure, and retirement invalidate the relevant plan; current GPU bounds, output-page witnesses, and transaction capacity checks remain fresh; and the existing publisher contract tests pass with new dirty-row cases. Build on the retained registration lookup, source groups, and committed material plans. Do not retain frame scratch closures as lifetime leases.
- [ ] Separate stable Advanced preparation templates from frame bindings. `AdvancedPreparationExtractor`, `AdvancedSharedPreparationService`, `AdvancedGpuDeformationResources`, `VulkanAdvancedVisibilityInputStorage`, and `VulkanPreparedStableBinStream`. Done when: unchanged mesh, material, topology, and group revisions reuse immutable preparation templates across render frames, and steady-state extraction visits only changed rows; current pose, bounds, root motion, interpolation, previous-render history, frame-specific resources, and output-page leases remain fresh; and an unsupported or changed source takes the existing strict preparation path. A held physics output page alone must not authorize reuse of a complete frame request or displayed deformation.
- [ ] Give the primary directional shadow tile an explicit native GPU consumer and command ownership contract. `DirectionalLightComponent.CascadeShadows`, `ShadowRenderPipeline`, `RenderCommandCollection`, and GPU shadow culling. `VisualScene3D.CanCollectCoveredSourcesOnGpu` excludes shadow passes, so each covered mesh now runs `BeforeAdd` and `AddCPU`, with a lock and a sorted insert, for the primary shadow view. This is the second `CollectRenderedItemsGpu` call in each cycle. Done when: eligible primary tiles avoid CPU mesh collection while retaining CastShadow, layer, mirror, and non-mesh policy, and rejected strict work remains dirty without an empty or CPU fallback draw. The existing cascade contract alone does not make the primary path safe.
- [ ] Validate sealed visibility records only when they change. `VulkanRenderer.CommandBufferRecording.Primary.Operations.cs` and `VulkanVisibilityAtlasRecordClosure.TryValidate` validate every record for each view, bin, and phase in each frame. Done when: an unchanged sealed record skips validation, and a test shows that a changed or replaced record is still validated before recording.
- [ ] Route remaining chain compute-skinning paths through aggregate deformation. `AdvancedGpuDeformationResources`, `AdvancedDeformationDispatchPlanner`, and renderer skinning submission. Done when: no chain renderer issues its own skinning command or barrier.

### Serial swap

`EngineTimer` runs the swap after collection and render both finish. Neither can run during the swap. At 2,000 chains, the swap took 13–17 ms of a 43–47 ms cycle.

- [ ] Build the Advanced scene publication during collection, not in the serial swap. `GPUScene.SwapCommandBuffers` calls `PublishAdvancedResidentSceneIfRequested`, so `AdvancedGpuScenePublisher.Publish` runs inside the swap. Done when: the publisher builds a pending publication during the collect phase and the swap only commits it; a change that arrives after the build and before the swap invalidates the pending publication or applies as a delta; and a publisher contract test covers a publication that is built before the swap and committed at the swap.
- [ ] Skip unchanged render commands at the swap. `RuntimeWorldRenderer.GlobalSwapBuffersCore`, `RenderableMesh.ProcessPendingRenderMatrixUpdates`, `RenderCommandMesh3D.SwapBuffers`, and `CpuBvhRenderTree.Swap`. Done when: swap work grows with the number of changed commands, and a unit test shows that an unchanged command keeps its published state with no per-command swap.

### World ownership and outputs

- [ ] Count every output-page producer failure. `GPUPhysicsChainDispatcher.SimulationReceipts` (`PollSimulationReceipts`) and `GPUPhysicsChainDispatcher.SpatialBounds` (`ConfirmResidentSpatialState`) set `KnownProducerFailure` without a failure count or fence observation. A renderer that rejected every frame therefore withdrew every page while `OutputPageDiagnostics.FailureCount` stayed constant; see the [open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults). Done when: each path increments the failure count once per page and records the fence observation, and the scale harness rejects a window in which the count changes.
- [ ] Build runtime templates from a consistent particle snapshot. `PhysicsChainComponent.BuildRuntimeTemplate` runs on the world fixed tick and reads `_particleTrees` in a counting pass and a fill pass. One benchmark stop crashed it with `IndexOutOfRangeException` during source restoration; see the [open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults). Done when: template building cannot observe a particle-tree rebuild between its passes, and a unit test that changes the trees between registration and template construction gets a consistent template or a counted rejection, never an overrun.
- [ ] Add capacity guards and delayed diagnostics to every arena writer. `PhysicsChainSlotArena`, `PhysicsChainDeferredLifetimeArena`. Done when: each writer reports overflow through `PhysicsChainArenaCapacityException` or a counter, with a unit test.
- [ ] Define reset and teleport semantics for current and previous state and palettes on spawn, teleport, template change, backend switch, and slot reuse. Done when: unit tests show that motion vectors never use unrelated history.

### GPU residency and submission

- [ ] Allocate permanent GPU arenas for templates, dynamic state, collider sets, roots and inputs, instance headers, active IDs, palettes, bounds, indirect commands, activity, and readback gather output. Bind them through stable offsets and generations in compact instance records. Done when: steady-state frames allocate no GPU buffers.
- [ ] Add capacity checks, clamped counts, overflow counters, and a next-frame resize policy for active lists and indirect arguments. Done when: a unit test overflows the active list and sees a counter and no corruption.
- [ ] Batch the skinned-vertex bounds reduction for skinned meshes without chains. `SkinnedMeshBoundsCalculator.DispatchPathADirectWrite` dispatches once and uploads one sentinel for each command slot. Done when: one dispatch reduces all registered renderers, and the reduction can read the Advanced aggregate deformation output.

### GPU kernels

- [ ] Extend the existing `ShortLinear` and `BranchedOrLong` buckets with justified feature and collider classes and small-count handling. `PhysicsChainKernelBucket` and the active-work shaders. Done when: the retained classes have explicit selection and empty-work behavior.
- [ ] Remove or repurpose unused shader-record fields. `GPUParticleData`, `GPUParticleStaticData`, and the physics-chain shaders. Done when: `PhysicsChainShaderContractTests` covers the retained layouts.
- [ ] Pack remaining eligible GPU indices, flags, and counts to smaller widths, with explicit range rejection. `GPUParticleData`, `GPUParticleStaticData`, and the shared shader records. Transform inputs already use the 48-byte affine record. Done when: retained C# and shader layout tests cover every packed field and reject overflow.
- [ ] Precompute remaining invariant GPU coefficient combinations. `PhysicsChainConstraints.glslinc`, GPU static records, and template packing. Rest lengths and capsule inverse terms already exist. Solved-position distance and direction are dynamic and must stay in the kernel. Done when: stiffness/weight and rest-limit terms have a correct version witness, and no invariant square root remains per step.
- [ ] Prototype palette and bounds writes in the final simulation pass. Done when: a dispatcher option selects fused or separate palette and bounds passes.
- [ ] Use direct palette lookup in the vertex shader for small or one-pass meshes. Done when: the renderer selects direct lookup by mesh size.

### Collision

- [ ] Sort or classify broadphase candidates by collider type when this reduces divergence. `PhysicsChainColliderBroadphase`. Done when: an option selects sorted candidates and a benchmark scenario can compare it.

### Distance cadence, sleep, and quality on the GPU

- [ ] Decide whether a reduced-rate GPU chain has work before full rest-input capture and preparation. `PhysicsChainWorld.GpuInputs`, `PhysicsChainWorld.Clock`, and `PhysicsChainComponent.GPU`. Done when: a no-solve update skips particle input gathering, the normal serial boundary advances the clock exactly once, reset and source changes invalidate the decision, and root movement remains pending until a solve consumes it.
- [ ] Separate physical step time from distance-based submission cadence. `PhysicsChainSimulationClock`, GPU dispatch snapshots, dispatcher state arenas, and the short and branched solver shaders. Done when: cumulative physical target ticks survive packet replacement and native retries, 60/30/15 Hz submission batches retain the authored physical step, and bounded catch-up retains unprocessed time.
- [ ] Add GPU presentation on rendered frames with no new solve. `GPUPhysicsChainDispatcher`, palette shaders, output history, and spatial bounds. Done when: timestamped root-relative simulation history produces the displayed palette, rate changes preserve a continuous presentation clock, reset seeds valid history, and CPU and GPU bounds cover the displayed mesh. Keep previous-render history separate from simulation history.
- [ ] Supply safe camera relevance for distance quality. `PhysicsChainWorld.QualityBudget`, component observation APIs, and the Math Intersections controller. Done when: observations enter under world ownership, use active color views from the same world, exclude shadow and probe cameras, and stale or unsupported observations retain full-rate GPU execution.
- [ ] Keep current and previous outputs coherent while a chain sleeps. Done when: a unit test wakes a chain and sees no history jump.
- [ ] Compute GPU activity, tier assignment, and sleep compaction without CPU readback. Done when: a strict zero-readback test passes with sleeping chains.

### Vulkan asynchronous submission experiment

Start this experiment only after the CPU frame meets its budget. The GPU is mostly idle until then.

- [ ] Add retained per-queue GPU timestamp ranges for split submissions. `VulkanCommandRuntime.AdvancedQueueOverlap`, `VulkanExplicitTargetRendererHost.GpuDiagnostics`, and Vulkan timing snapshots. Done when: each completed sample identifies its queue, submission, source frame, timestamp validity, and begin/end range; retrieval never waits; query storage remains retained until completion; and incomplete samples cannot appear as valid elapsed time.
- [ ] Add an opt-in physics submission split after an exact resource-use audit identifies independent work. `VulkanPhysicsChainComputeBackend`, `VulkanFrameLoop`, and `VulkanCommandRuntime` submission and lifetime owners. Done when: the selected secondary queue belongs to the graphics family and supports compute; captured inputs and all output resources remain retained through every submission; explicit dependencies join before same-frame palette, bounds, deformation, visibility, and shadow consumers; rejected or partially accepted work cannot reuse in-flight storage; and unsupported requested execution rejects without a hidden lane change. Keep cadence and output latency unchanged. Use the [research constraints](../../../developer-guides/rendering/physics-chain-performance.md#vulkan-asynchronous-compute-research) and the separate [runtime experiment](../../testing/physics/physics-validation.md#vulkan-asynchronous-physics-experiment).

### CPU backend

These items mainly apply to the CPU solver backend. They are not on the GPU-path route to the frame-rate target.

- [ ] Remove LINQ, captured closures, boxing, transient arrays and lists, and non-struct enumeration from preparation, solve, apply, and debug paths. Pre-size reused collections by high-water marks. Done when: `Report-NewAllocations` shows no new entries in physics-chain files.
- [ ] Aggregate profiler counters per worker or bucket and merge once. Sample or compile out fine telemetry in production profiles. Done when: no per-chain profiler call remains in the hot loop.
- [ ] Add an AoSoA layout variant beside the SoA layout. `PhysicsChainCpuBackend`. Done when: the benchmark harness can select either layout.
- [ ] Bucket chains by segment count, topology, feature mask, and collider class. `PhysicsChainCpuKernelSelector`. Done when: hot loops have no per-particle feature branch.
- [ ] Use coarse ranges with work stealing for imbalance. Parallelize root and input gathering, solve, collision, palette, bounds, and opt-in mirror publication. `PhysicsChainCpuWorkScheduler`. Done when: each stage runs in worker ranges.
- [ ] Specialize CPU kernels by collision class: none, small fixed count, candidate list, and general. Done when: `PhysicsChainCpuKernelFamily` names each variant and unit tests compare each with the scalar reference.
- [ ] Specialize optional features (elasticity, stiffness, freeze axis, branching) only for feature masks with enough volume. Done when: each added variant has a scalar parity test.
- [ ] Make fixed-step accumulation, substep count, damping, gravity, external force, teleport, reset, and time scale one shared contract for CPU and GPU. Done when: both backends read one definition.
- [ ] Add a batched transform-mirror writer for consumers that request a mirror. `PhysicsChainCpuMirrorPolicy`. Done when: mirror writes run in worker ranges.

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
- [ ] Add committed bound version and proxy tests for `PhysicsChainCommittedSpatialProxy`, `GPUPhysicsChainDispatcher.SpatialBounds` (`ResolveCommittedSpatialProxy`, `NotifyCommittedSpatialOutputChanged`), and `RenderableMesh.CommittedBounds`. Done when: an exact bound that stays inside the proxy keeps the bound and version; one renderer's new output does not change another renderer's version; a bound that leaves the proxy, or a proxy that is more than twice the exact extent, gets a new proxy and version and a changed-bound notification; a withdrawn page reports a changed bound; an unchanged bound does not set the reconcile dirty flag but still advances the covered shadow output revision; and coverage loss restores CPU bounds. The change is live-validated; see the [committed bound section](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#committed-bound-version-and-proxy).
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

### Stale physics-chain tests

- [ ] Repair or remove the 23 physics-chain tests that fail on 2026-10-08 because they read renamed private fields or old source text. `PhysicsChainComponentTests` (3, reflection on `_particleTrees`), `PhysicsChainQualityTransitionTests` (1, reflection on `_time`), `PhysicsChainStructuralCommandTests.RemoveAddBeforeDrainDoesNotRecycleTheLiveSlot`, and source-text checks in `PhysicsChainDebugDefaultTests` (3), `PhysicsChainGpuBoundsContractTests` (3), `PhysicsChainShaderContractTests` (3), `VulkanPhysicsChainParityContractTests` (6), `PhysicsChainGpuKernelFamilyTests` (1), `PhysicsChainGpuResidencyTests` (1), and `PhysicsChainSelectiveReadbackDispatcherTests` (1). None of them covers the committed bound or mesh-sharing changes. Done when: each test checks current behavior through a public or internal contract, or is removed with its intent covered elsewhere, and the physics-chain test filter passes.

### Palette sharing, selective readback, and resource lifetime

- [ ] Add complete-palette sharing tests for `PhysicsChainPaletteAtlasAllocator`. Done when: compatible renderers with the same chain and mesh cause one palette generation; partial coverage and different bind data keep separate palettes.
- [ ] Update selective readback contract tests for source generations, independent world banks, solved affine values, and the 32-byte GPU gather item. `PhysicsChainReadbackRequestTests`, `PhysicsChainReadbackTransferTests`, and `PhysicsChainSelectiveReadbackDispatcherTests`. Done when: same-capacity source replacement, reset/transfer, cancellation/expiry, held input, renderer replacement, partial failure, and stale completion cannot deliver unrelated data or reuse retained storage. Remove stale dispatcher source-string expectations.
- [ ] Add deferred resource admission and debug lease tests. `RenderResourceLeaseOwner`, `VulkanFrameOpWorkspace`, `ComputeDispatchOp`, and debug request cancellation. Done when: concurrent release/retirement disposes once, retired zero-use owners cannot return to active, an occupied operation and sealed snapshot cannot be overwritten, and all rejected or cancelled requests release their uses.

### CPU and GPU parity

- [ ] Add scalar versus GPU tolerance tests across chain lengths, topologies, colliders, substeps, forces, reset, teleport, and large coordinates. Use the tolerances in the [correctness contract](../../testing/physics/physics-chain-correctness-contract.md). Done when: the tests exist.

## Decisions Needed

- Set additional named-hardware physics budgets and scaling targets. The current requirement is at least 100 completed rendered frames per second with 2,000 visible animated chains and frame-interval p95 at most 10 ms, in both the shared-mesh and unique-mesh scenarios. Keep acceptance in the [validation plan](../../testing/physics/physics-validation.md). Owner: physics runtime.
- Choose the page size for each row store and the enlargement margin for covered spatial proxies. In the microbenchmark, 1% scattered changes in 64-row pages cost 61–65% of a full dense rewrite. Rows that change together should share pages. Owner: physics runtime and rendering.
- Decide whether shared-mesh chains also need a world-owned batch source, with one scene registration for each mesh and material set. Decide after the change-driven renderer path is measured in the shared-mesh scenario. Owner: rendering.
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
