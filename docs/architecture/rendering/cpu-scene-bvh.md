# CPU Scene BVH

The CPU scene BVH is the production CPU-direct spatial index used by
`VisualScene3D` when `CpuSceneCullingStructure` is `Bvh`. It is a binary,
flat-array BVH optimized for immutable multi-view reads and sparse scene-item
motion. It is distinct from the legacy per-mesh triangle BVH.

## Mutation and publication contract

- Adds/removes increment the topology revision and conservatively rebuild the
  topology at the next swap.
- Bounds changes increment the bounds revision, journal the stable item handle,
  and refit its leaf plus each distinct ancestor once.
- Payload-only changes increment the payload revision without spatial work.
- A clean swap performs only revision checks and does not publish a generation.
- A completed staging snapshot is published atomically with a monotonically
  increasing generation. Readers increment the selected snapshot's embedded
  reader count, validate that it is still published, and decrement it on exit.
- Four reusable snapshot slots are retained by default. A writer may reuse only
  a non-published slot with no readers, so callbacks never run under the
  mutation lock and snapshots cannot be recycled while in use.
- Mutation admission is bounded by `MaxPendingMutations`; rejected mutations
  are counted rather than growing memory without limit.

Items keep a stable `OctreeNodeBase` handle for move notification and query
identity. Topology rebuilds may change flat entry/leaf indices but do not change
that handle. Null, invalid, infinite, or otherwise uncertain bounds are routed
to the conservative unbounded lane.

## Construction and maintenance

Construction uses an iterative 16-bin centroid SAH builder. Split ties use axis,
bin, and stable item identity; zero-extent centroid ranges use a stable median
fallback. The builder compares split and leaf costs, is depth bounded, and
defaults to an eight-item leaf cap.

Refits update a generation-marked dirty-leaf set, order unique ancestors from
deepest to root, then apply a bounded local-rotation budget. Quality is measured
with normalized SAH, sibling overlap, root-volume growth, maximum/average depth,
leaf occupancy, dirty fraction, and refit age. Configurable deterministic
thresholds select continued refit/local rotation or a complete quality rebuild.
Partial subtree rebuilding was evaluated but not selected: retaining flat range
ownership while replacing an arbitrary subtree added copying and remapping cost
without improving the measured scene workloads over bounded rotations followed
by a deterministic rebuild.

`CpuBvhOptions.EnableLocalRestructuring` is the rollout kill switch.

## Traversal contract

The typed `ICpuBvhVisitor<T>` path is the allocation-free render hot path.
Frustum traversal carries an active six-plane mask, removes planes that fully
contain a parent, and uses a bounded stack with a conservative recursive
overflow fallback that is counted. Ordered segment/ray traversal tests bounded
slab entry distance and visits the nearer child first.

The binary layout remains selected for v1. Wider BVH4/BVH8 layouts and SIMD
plane packets were evaluated after the flat scalar implementation: the scene
workloads are dominated by cheap frustum masks and item callbacks, while wider
nodes increase staging/snapshot memory. Scalar binary traversal remains both
the selected path and correctness oracle.

## Diagnostics and benchmarking

`CpuBvhRenderTree.GetDiagnostics()` returns the published topology/bounds/
payload revisions, update and rebuild reasons, topology/refit/rotation counts,
traversal and plane-mask counters, quality metrics, queue pressure, and mutation
lock/publication timing. `VisualScene3D.CpuBvhDiagnostics` exposes the same
sample to editor and profiler integrations.

Run the repeatable workload report with:

```powershell
dotnet run --project .\XREngine.Benchmarks\XREngine.Benchmarks.csproj -- `
  --cpu-bvh-report --counts 1000,10000,100000 `
  --leaf-capacities 8 --output Build\_AgentValidation\<run>\reports\cpu-bvh-report.json
```

Use `--counts 1000000` for the memory-permitting stress tier and
`--leaf-capacities 1,2,4,8,16` for the leaf-capacity sweep. The report separates
build, clean swap, each dirty ratio, and each visible-ratio/view-count traversal,
including managed allocation and GC counts.
