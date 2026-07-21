# Physics Chain Scale Implementation Evidence — 2026-07-20

This note records implementation evidence added while executing
`physics-chain-thousands-scale-optimization-todo.md`. It is not benchmark
acceptance evidence; hardware-dependent gates remain open until the required
matched Release matrix is captured.

## Shared collider candidates

- Immutable collider shape streams are content-deduplicated per world and use
  stable set IDs. Dynamic poses are independently versioned and dirty-ranged.
- `PhysicsChainColliderRuntimeSet` owns the shared BVH and applies dirty-range
  refits once before queries. Unbounded planes are explicit always-candidates.
- `PhysicsChainCpuSharedColliderSet` converts only changed poses into compact
  world-space CPU collider records. Sets with zero through four colliders skip
  the BVH; larger sets emit compact candidates. Candidate overflow never uses
  a truncated list: it reports diagnostics and conservatively falls back to
  the full set when caller capacity permits.
- Focused Release, warnings-as-errors validation:
  - `PhysicsChainColliderRuntime.Tests.csproj`: 6/6
  - `PhysicsChainCpuSharedColliders.Tests.csproj`: 3/3

### Production bridge and world CPU ranges

- Production CPU components now register shared collider resources through the
  world rather than copying authoritative colliders into every backend
  instance. Authored shape changes rebuild at registration boundaries while
  ordinary pose changes update and refit the shared runtime set before workers
  query it.
- Each CPU instance owns only bounded candidate-index, candidate-collider, and
  traversal scratch allocated during registration. Chain-level bounds include
  previous/current particle positions, current root inputs, influence radii,
  object motion, force, and gravity before the shared BVH is queried.
- Zero-through-four collider sets bypass the BVH. Larger sets use exact compact
  candidates; overflow is explicit and conservatively selects the shared full
  set without an authoritative per-chain copy. Query/candidate/fallback
  diagnostics use atomic aggregation because independent ranges can query one
  shared set concurrently.
- `PhysicsChainWorld.ExecutePreparedCpuBatch` reuses its existing
  `BatchWorkItem`/`ManualResetEventSlim` objects and ThreadPool workers for
  coarse 32-handle-or-larger ranges. Execution is sequential on an engine job
  worker, and a rejected coarse range falls back only that range through the
  deterministic component solve path.
- CPU hierarchy/root/input gathering uses the same reusable work items and
  geometrically grown component/result/fault arrays. Ranges are weighted by
  estimated particle/collider work; successful results are compacted in stable
  world order before solve. Non-CPU paths remain on the world thread, shared
  colliders prepare once in a sequential prepass, and component faults abort
  only that component while the earliest ordered fault is preserved.
- Focused validation:
  - isolated shared/backend Release suite: 39/39, including concurrent 4x32
    range publication and exact shared-query diagnostics
  - isolated world-pipeline suite: 2/2, covering 64-instance backend solve to
    palette/bounds publication and the reusable weighted preparation source
    contract
  - isolated engine compile: 0 warnings, 0 errors
  - standard engine compile reaches only the unrelated secondary-context API
    errors documented below

## Persistent CPU scheduling

- `PhysicsChainCpuWorkScheduler` creates its worker threads and synchronization
  primitives once, reuses high-water handle storage, and distributes coarse
  ranges through an atomic range cursor.
- Deterministic mode consumes the same ranges in stable single-thread order.
- Execute/dispose ownership uses one atomic lifecycle state, so concurrent
  dispose cannot strand workers or alias a later execution.
- Focused Release, warnings-as-errors validation:
  - `PhysicsChainCpuScheduler.Tests.csproj`: 3/3
  - broader scalar/SIMD/backend suite: 27/27
- After warmup, 100 parallel schedules allocate zero bytes on the calling
  thread. Worker traces and scheduler tuning remain hardware acceptance work.

## CPU render palette semantics

- CPU particle world transforms are composed with the renderer's canonical
  `root bind * inverse bind * current world` contract.
- Output uses the same 48-byte three-`vec4` affine representation as
  `SkinPaletteMatrix`, including translation in each row's W component.
- Current and previous palettes are composed independently for motion vectors;
  mapping validation completes before any destination element is written.
- Focused Release, warnings-as-errors validation:
  - `PhysicsChainCpuSkinPalette.Tests.csproj`: 3/3
- Atlas upload and normal renderer binding remain open; this slice establishes
  the format/composition boundary needed to do that without skin corruption.

## Arena compaction policy

- Fragmented arenas are never compacted in place. The explicit policy is to
  rebuild/remap/swap at a quiescent structural boundary and retire the old
  generation behind active-frame completion.
- Capacity, reclaimable-slot, fragmentation, and active-frame decisions are
  observable with a reason string.
- Focused Release, warnings-as-errors validation:
  - `PhysicsChainArenaCompaction.Tests.csproj`: 3/3

## Benchmark evidence contract

- The required matrix is represented lazily and cold-start, structural-churn,
  and steady-state work items are separate. Acceptance requires at least three
  matched Release runs.
- Evidence now carries CPU cache/branch/context-switch/migration/bandwidth/SIMD
  counters, GPU occupancy/register/spill/shared-memory/bandwidth/barrier and
  indirect-command counters, arena capacity/live/fragmentation/high-water
  metrics, and trace paths.
- Acceptance validation rejects short runs, missing hardware counters, missing
  strict-GPU traces, impossible arena totals, and any strict-profile readback.
- Focused Release validation:
  - `PhysicsChainBenchmark.Tests.csproj`: 23/23

## Transform mirror hierarchy batching

- `TransformHierarchyMutationBatch` preserves each transform's `SetField`
  notification and local/world dirty semantics while suppressing individual
  world dirty registration and emitting one tree-root enqueue at scope exit.
- Suppression is thread-local and limited to the transform currently being
  changed by the batch, so listener-driven unrelated mutations remain visible.
- `PhysicsChainComponent.ApplyParticlesToTransforms` now uses one scope per tree,
  so CPU publication and GPU compatibility readback share the same batched
  root-invalidation path.
- Generic `SetField` reference identity guards now skip `ReferenceEquals` for
  value types, preserving behavior without boxing transform state values.
- Focused validation:
  - Runtime.Core warnings-as-errors build: 0 warnings, 0 errors
  - Release transform batch suite: 3/3, including exact root enqueue, final
    local/world pose checks, and zero managed bytes over 1,000 warm batches
## Current integration blockers

The focused physics-chain suites above are green. The repository-wide engine
build is currently blocked by concurrent, unrelated rendering contract work in
`Engine.Rendering.SecondaryContext.cs`; this does not replace final solution
and editor validation once that worktree stabilizes.
