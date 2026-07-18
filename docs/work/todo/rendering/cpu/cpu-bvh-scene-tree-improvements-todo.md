# CPU BVH Scene-Tree Improvements TODO

Last Updated: 2026-07-17
Owner: Rendering
Status: Complete
Scope: scene-level CPU spatial indexing, mutation, traversal, concurrency, and
quality maintenance centered on
[`CpuBvhRenderTree`](../../../../../XREngine.Data/Trees/BVH/CpuBvhRenderTree.cs).

Related local docs:

- [GPU BVH Scene-Tree Improvements TODO](../gpu/gpu-bvh-scene-tree-improvements-todo.md)
- [Masked Software Occlusion Culling TODO](../masked-software-occlusion-culling-todo.md)
- [GPU-Driven Occlusion Culling Architecture TODO](../gpu/gpu-driven-occlusion-culling-architecture-todo.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)

Research references:

- [Embree: A Kernel Framework for Efficient CPU Ray Tracing](https://www.embree.org/papers/2014-Siggraph-Embree.pdf)
- [Fast, Effective BVH Updates for Animated Scenes](https://hwrt.cs.utah.edu/papers/hwrt_rotations.pdf)
- [Selective BVH Restructuring](https://diglib.eg.org/server/api/core/bitstreams/679b22a1-314e-4102-bfba-5db17383005f/content)
- [PBRT: Bounding Volume Hierarchies](https://pbr-book.org/4ed/Primitives_and_Intersection_Acceleration/Bounding_Volume_Hierarchies)

## Goal

Replace full-tree rebuilds and lock-held callback traversal with an
allocation-free, cache-friendly CPU scene BVH that supports inexpensive sparse
motion, deterministic quality maintenance, and concurrent read-only traversal
by cameras, shadow views, and other render consumers.

The target is a production v1 architecture, not compatibility with the current
node layout. Breaking internal APIs and replacing the tree representation are
acceptable when they simplify ownership and improve measurable performance.

## Non-Goals

- Replacing GPU-driven culling or deciding which mesh-submission strategy is
  the global default. That work belongs in the GPU BVH and submission docs.
- Adding per-triangle scene-tree leaves. Scene leaves continue to reference
  renderable scene items or commands.
- Making the scene BVH responsible for occlusion verdicts. It supplies
  conservative candidates to frustum, shadow, ray, and occlusion consumers.
- Preserving the legacy object-node representation or its exact traversal API.
- Optimizing the older per-mesh triangle BVH before the scene-tree path is
  measured and stabilized. Its known defects are recorded separately below.

## Current Baseline And Audit Findings

The current scene tree is functionally straightforward but structurally costly
for dynamic workloads:

- `RenderInfo3D` changes enqueue move operations, and a move marks the whole
  `CpuBvhRenderTree` dirty.
- The next command-buffer swap rebuilds the complete tree, including recursive
  `List.Sort` operations. The resulting construction cost is approximately
  `O(N log^2 N)` rather than a sparse update proportional to moved leaves.
- Each rebuild creates a hierarchy of class-based `BvhNode` objects, adding
  allocation, pointer chasing, and garbage-collector pressure.
- Construction splits the longest union-bounds axis at the item median. It does
  not use centroid bounds, binned SAH, leaf-cost comparison, or a linear Morton
  build, so clustered, overlapping, and giant-plus-tiny distributions can
  produce weak trees.
- Traversal holds the tree monitor while invoking caller callbacks. Slow or
  re-entrant consumers extend the critical section, and concurrent camera or
  shadow traversals serialize on the same lock.
- No performance benchmark currently covers build time, refit time, traversal
  node visits, hot-path allocation, or quality degradation under motion.
- Existing tests emphasize functional behavior and explicitly encode the
  current blocking traversal behavior; those expectations must change with the
  snapshot architecture.

Primary sources and tests:

- [`CpuBvhRenderTree.cs`](../../../../../XREngine.Data/Trees/BVH/CpuBvhRenderTree.cs)
- [`RenderInfo3D.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Info/RenderInfo3D.cs)
- [`CpuSpatialRenderTreeTests.cs`](../../../../../XREngine.UnitTests/Rendering/CpuSpatialRenderTreeTests.cs)

## Target Architecture And Invariants

The target tree should have these properties:

1. **Moves refit by default.** A moved item updates its leaf and only the
   ancestors whose bounds change.
2. **Adds and removals are explicit topology mutations.** They may initially
   rebuild, then graduate to incremental insertion/removal if measurement
   justifies the complexity.
3. **Readers consume immutable snapshots.** A traversal never holds the
   mutation lock and never observes partially updated topology or bounds.
4. **Nodes are stored contiguously.** Parent, child, and leaf ranges use indices
   into reusable arrays rather than references between heap objects.
5. **Traversal is allocation-free.** Per-query stacks, delegates, enumerators,
   and result storage must not allocate on per-frame paths.
6. **Quality is observable.** The tree records normalized SAH cost, overlap,
   depth, leaf occupancy, dirty fraction, refit age, and rebuild reason.
7. **Degradation is bounded.** Refit is followed by local restructuring,
   partial rebuild, or full rebuild when measured quality crosses a threshold.
8. **Queries remain conservative and deterministic.** Invalid or uncertain
   bounds become visible/candidate results; equal-key build decisions have a
   stable tie-breaker.

## Phase 0 - Baseline, Telemetry, And Benchmark Harness

- [x] Add a dedicated CPU scene-BVH benchmark group under `XREngine.Benchmarks`
  or the repository's current benchmark harness.
- [x] Measure construction, no-change swap, sparse movement, full movement,
  and traversal separately; do not hide rebuild cost inside a frame aggregate.
- [x] Capture allocation bytes and collection counts for build, update, and
  traversal. Traversal and no-change swap must have zero managed allocation.
- [x] Add counters for:
  - [x] topology builds and their reason;
  - [x] refitted leaves and ancestors;
  - [x] visited internal nodes, leaves, and primitives per query;
  - [x] frustum-plane tests and plane-mask eliminations;
  - [x] maximum/average depth and leaf occupancy;
  - [x] normalized SAH cost, sibling overlap, and root-volume growth;
  - [x] mutation-lock wait/hold time and snapshot publication time.
- [x] Exercise at least `1K`, `10K`, and `100K` scene items; add `1M` where
  memory permits.
- [x] Include uniform, clustered, identical-centroid, long-thin, and
  giant-plus-many-small spatial distributions.
- [x] Test dirty ratios of `0%`, `0.1%`, `1%`, `10%`, and `100%` per frame.
- [x] Test visible ratios near `0%`, `10%`, `50%`, and `100%`, with one, two,
  four, and many view traversals per published snapshot.
- [x] Save benchmark output under `Build/_AgentValidation/<run>/reports/` and
  record durable conclusions in a progress or investigation document.

Acceptance criteria:

- [x] A repeatable command records time, allocation, node visits, quality, and
  synchronization cost for the current implementation.
- [x] Baseline results identify separate budgets for static, sparsely dynamic,
  and fully dynamic scenes.

## Phase 1 - Mutation Revisions And Sparse Bottom-Up Refit

- [x] Separate content revisions into at least topology, bounds, and payload
  revisions so non-spatial render changes cannot force spatial work.
- [x] Assign each live scene item a stable leaf slot or maintain an efficient
  item-to-leaf map that is updated on snapshot publication.
- [x] Collect moved leaf indices in a reusable dirty set or generation-marked
  array; deduplicate repeated moves in the same frame without allocation.
- [x] Recompute dirty leaf bounds from their current items, then propagate
  changed bounds to the root in bottom-up order.
- [x] Avoid recomputing an ancestor more than once when multiple dirty leaves
  share it.
- [x] Make a no-change swap an `O(1)` revision check with no rebuild, refit,
  traversal interruption, or allocation.
- [x] Keep add/remove handling conservative. The first implementation may
  request a topology rebuild while moves use refit.
- [x] Define behavior for empty, invalid, infinite, and degenerate item AABBs;
  invalid state must not produce NaNs in parent bounds.
- [x] Preserve stable query identity across refits and document identity
  changes caused by topology rebuilds.
- [x] Add telemetry distinguishing `Clean`, `BoundsRefit`, `PartialRebuild`,
  `TopologyRebuild`, and `QualityRebuild` outcomes.

Acceptance criteria:

- [x] Moving one item updates one leaf and at most one ancestor chain rather
  than rebuilding all nodes.
- [x] Sparse-refit results exactly match a brute-force query oracle across
  randomized movement and degenerate-bounds cases.
- [x] Refit and clean-swap paths allocate zero managed memory after warmup.
- [x] Adds and removals remain correct even if their first implementation uses
  a full topology rebuild.

## Phase 2 - Flat Immutable Snapshots And Lock-Free Traversal

- [x] Replace class-based nodes with a compact node struct stored in reusable
  contiguous arrays.
- [x] Store child indices or leaf `first/count` ranges; keep parent indices or
  an explicit bottom-up order for refit.
- [x] Keep leaf items in a compact permutation array so traversal touches
  sequential memory.
- [x] Build/refit into a staging snapshot or otherwise guarantee that the
  published snapshot cannot be mutated while readers use it.
- [x] Publish the completed snapshot atomically with a monotonically increasing
  generation number.
- [x] Define snapshot lifetime using double buffering, reference-counted
  leases, epochs, or another bounded mechanism that does not allocate per
  traversal.
- [x] Move all caller callbacks outside mutation locks. No callback may execute
  while the tree owns a monitor or writer lock.
- [x] Support simultaneous camera, cascade, reflection, and editor queries on
  the same snapshot.
- [x] Update tests that currently require lock-held traversal to instead verify
  snapshot consistency and writer/reader progress.
- [x] Keep mutation queues bounded and observable when publication falls
  behind producer activity.

Acceptance criteria:

- [x] Multiple read-only traversals run concurrently without serializing on a
  tree-wide monitor.
- [x] A writer can stage the next snapshot while readers finish the current
  snapshot, with no partial-state visibility or use-after-recycle.
- [x] Traversal remains allocation-free after warmup.

## Phase 3 - Construction Quality And Determinism

- [x] Replace recursive full-list sorting with one of these measured builders:
  - [x] a 12- or 16-bin centroid SAH builder;
  - [x] a single stable Morton sort followed by a linear hierarchy build;
  - [x] a hybrid in which Morton partitions feed small SAH treelets.
- [x] Compute split axes from centroid bounds rather than only union-bounds
  extent.
- [x] Compare split cost against leaf cost so the builder can stop before a
  forced maximum capacity when another split is not beneficial.
- [x] Tune leaf capacity across representative frustum and shadow workloads;
  include `1`, `2`, `4`, `8`, and `16` items per leaf.
- [x] Define deterministic tie-breaking for equal centroids, equal Morton
  codes, and equal SAH costs using stable scene-item identity.
- [x] Add robust fallbacks for zero centroid extent and highly overlapping
  inputs without unbalanced recursion.
- [x] Build iteratively or bound recursion depth so pathological input cannot
  overflow the managed stack.
- [x] Retain the old builder behind a diagnostics-only comparison switch until
  quality and correctness parity are established, then remove it.

Acceptance criteria:

- [x] The selected builder improves or matches traversal node visits on every
  benchmark distribution without unacceptable construction regression.
- [x] Identical input and stable identities produce identical topology.
- [x] Pathological distributions remain bounded in depth and build time.

## Phase 4 - Traversal Hot-Path Improvements

- [x] Propagate a frustum-plane active mask so descendants do not retest planes
  that already fully contain an ancestor.
- [x] Use an iterative traversal with `stackalloc` or a bounded reusable stack;
  define a conservative overflow path and count every overflow.
- [x] Replace allocation-prone delegates/enumerables with a typed visitor,
  function pointer, or generic struct visitor where profiling shows callback
  dispatch is significant.
- [x] Add specialized query entry points only when they remove measurable work
  for common operations such as frustum collection, shadow-cascade collection,
  AABB overlap, and nearest/ordered ray candidates.
- [x] Visit nearer children first for ordered ray queries so a tightened hit
  distance prunes the farther child sooner.
- [x] Evaluate SIMD AABB/plane testing only after node layout and plane-mask
  propagation land; keep scalar code as the correctness oracle.
- [x] Benchmark binary, BVH4, and possibly BVH8 layouts on supported CPUs.
  Favor the smallest layout that improves total frame cost, not isolated
  traversal throughput.
- [x] Keep hot/cold node data separate if instrumentation or mutation metadata
  would otherwise expand the traversal cache footprint.

Acceptance criteria:

- [x] Query results match brute force for randomized frusta, AABBs, rays, and
  invalid-input cases.
- [x] Plane-test and node-visit counters demonstrate the benefit of plane masks
  and any selected wider layout.
- [x] No hot traversal path allocates or performs hidden LINQ/enumerator work.

## Phase 5 - Adaptive Quality Maintenance

- [x] Record normalized SAH cost after build and after each refit batch.
- [x] Track sibling overlap, root-volume growth, maximum depth, dirty fraction,
  and consecutive refit count.
- [x] Establish deterministic, configurable thresholds for:
  - [x] continue refitting;
  - [x] rotate/restructure a bounded set of poor internal nodes;
  - [x] rebuild a damaged subtree;
  - [x] rebuild the complete topology.
- [x] Evaluate local tree rotations using a bounded work budget so maintenance
  cannot create an unpredictable frame spike.
- [x] Evaluate partial subtree rebuilds for localized motion before adding
  complex fully incremental insertion/removal.
- [x] If rebuilds are staged asynchronously, retain the current snapshot until
  the replacement is complete and never publish stale item membership.
- [x] Expose the chosen action and quality reason in rendering diagnostics.
- [x] Provide conservative defaults and a kill switch for local restructuring
  during rollout.

Acceptance criteria:

- [x] Long-running motion tests do not show unbounded node-visit or SAH growth.
- [x] Rebuild frequency and worst-frame maintenance cost stay within the
  budgets established in Phase 0.
- [x] Quality-trigger decisions are reproducible for a deterministic workload.

## Phase 6 - Tests, Runtime Validation, And Promotion

- [x] Expand `CpuSpatialRenderTreeTests` with randomized brute-force parity for
  build, refit, add, remove, and mixed mutation sequences.
- [x] Add regression coverage for identical centers, zero-size bounds, giant
  objects, empty trees, one-item trees, and maximum-depth distributions.
- [x] Add concurrent reader/writer stress tests with snapshot generation and
  lifetime assertions.
- [x] Add deterministic topology or quality-metric tests without asserting an
  implementation-specific node order unnecessarily.
- [x] Add performance tests or benchmark gates for clean swap, sparse refit,
  full rebuild, and one/four concurrent traversals.
- [x] Build the narrowest affected projects, then run the targeted rendering
  tests after the implementation is functionally sound.
- [x] Launch the Unit Testing World and validate static, animated, editor-drag,
  and camera-motion scenarios with at least two camera positions.
- [x] Validate multiple shadow cascades and stereo views against the same
  published tree generation.
- [x] Confirm rendering output and visible-item sets match the old tree or a
  brute-force oracle before removing the old implementation.
- [x] Record benchmark tables and runtime evidence in
  `docs/work/progress/rendering/` before promotion.

Acceptance criteria:

- [x] Static scenes perform no spatial update work after stabilization.
- [x] Sparse movement cost scales with changed leaves and tree depth, not total
  scene size.
- [x] Concurrent readers do not block one another on the tree implementation.
- [x] No false-negative visibility or query result is observed.
- [x] The new implementation introduces no compiler warnings or per-frame heap
  allocation.

## Adjacent Legacy Per-Mesh BVH Follow-Up

The older triangle/object BVH under `XREngine.Data/Trees/BVH` is distinct from
the CPU scene tree and should not delay Phases 0-6. A separate implementation
slice should nevertheless address these audited problems:

- [x] Include object position when computing `AABBofOBJ`; the current
  origin-centered calculation degrades SAH split quality.
- [x] Repair or remove the rotation-selection control flow whose rotation
  switch is currently unreachable for a selected non-`NONE` rotation.
- [x] Use exact cached triangle AABBs where available instead of repeatedly
  reconstructing loose bounds.
- [x] Correct nearest-mesh query direction, triangle-hit consumption, and
  segment clamping.
- [x] Remove recursive list sorting/allocation if this BVH remains in active
  runtime use.
- [x] Bump the BVH disk-cache format/version if cached topology or bound
  semantics change.

## Risks And Open Questions

- How many concurrent snapshot generations must be retained for the editor,
  shadow, and VR render paths without unbounded memory growth?
- Is a binary tree with plane masks faster for scene frustum culling than BVH4
  on the engine's supported CPUs? Measure both; ray-tracing results alone are
  not sufficient evidence.
- Should topology rebuilds run on a worker, and which world-lifecycle boundary
  guarantees that item references remain valid until publication?
- Does stable item identity already provide a dense index suitable for the
  item-to-leaf map, or is a dedicated generation-safe handle needed?
- What normalized SAH/overlap thresholds predict real traversal degradation in
  XRENGINE scenes rather than synthetic ray workloads?
- Can add/remove remain rebuild-only for v1, or do editor-heavy workloads
  justify incremental insertion and removal?

## Definition Of Done

- [x] The scene CPU BVH uses flat, reusable snapshots and lock-free traversal.
- [x] Bounds-only movement uses sparse refit with bounded quality maintenance.
- [x] Static/no-change frames perform no rebuild or refit work.
- [x] The selected builder and leaf capacity are justified by checked-in or
  durable benchmark evidence.
- [x] Correctness, concurrency, degenerate-input, and allocation coverage pass.
- [x] Runtime validation covers editor motion, animation, shadows, and stereo.
- [x] Relevant architecture and rendering workflow documentation reflects the
  final mutation, snapshot, and diagnostics contracts.
