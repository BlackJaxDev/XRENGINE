# CPU BVH Scene-Tree Improvements - Completion Report

Date: 2026-07-17

Status: Complete

## Delivered architecture

The former recursive object-node scene BVH was replaced by reusable flat
snapshots with atomic publication and reader lifetime counts. Bounds-only moves
use a journaled item-to-entry/leaf map and sparse bottom-up refit. Topology,
bounds, and payload revisions are independent. Typed visitors, iterative
plane-mask frustum traversal, near-first bounded ray traversal, deterministic
16-bin centroid SAH construction, local rotations, quality rebuild thresholds,
bounded mutation admission, and detailed diagnostics are in place.

The legacy per-mesh BVH follow-up is also complete: exact positioned triangle
AABBs are used, the unreachable rotation branch is repaired, initial builds use
one array plus in-place selection instead of recursive list sorting, segment
queries use a normalized direction and endpoint clamp, and the disk cache is
versioned at `BVH/v2` with hash schema 2.

## Benchmark evidence

Evidence is stored in the ignored run root
`Build/_AgentValidation/20260717-cpu-bvh-scene-tree/reports/`:

- `cpu-bvh-report.json`: 330 workloads at 1K, 10K, and 100K.
- `cpu-bvh-report-1m.json`: 110 workloads at 1M.
- `cpu-bvh-leaf-capacity-report.json`: 550 workloads comparing leaf caps
  1, 2, 4, 8, and 16 at 10K.

All reports cover uniform, clustered, identical-centroid, long-thin, and
giant-plus-many-small distributions; 0%, 0.1%, 1%, 10%, and 100% dirty ratios;
approximately 0%, 10%, 50%, and 100% visibility; and 1, 2, 4, and 8 views.

Representative measurements on the validation machine:

| Workload | Result |
| --- | ---: |
| 100K uniform build | 1,321.6 ms, 87.1 MiB, depth 20 |
| 1M uniform build | 13,970.2 ms, 736.2 MiB, depth 23 |
| 100K clean swap | 0.002 ms, 0 B |
| 100K uniform 100% visible traversal, one view | 26.2 ms |
| 1M uniform 100% visible traversal, one view | 304.7 ms |
| Live editor clean CPU-BVH swap | 0.002-0.005 ms |
| Live editor CPU-BVH camera collection | 0.255-1.647 ms |

The allocation test fixture proves zero managed allocation after warmup for
clean swap and typed traversal. The corrected report harness uses timestamp
struct APIs so its measurement code does not add the former 40-byte `Stopwatch`
allocation.

The leaf sweep found cap 4 had the lowest average normalized SAH (1828), cap 8
the fastest average construction (298 ms), and cap 16 the fastest full-visible
traversal (2.23 ms) at 10K. Cap 8 remains the conservative default: it is near
the traversal minimum, avoids cap 16's higher SAH/overlap exposure under motion,
and limits worst-leaf callback bursts. The option remains configurable.

The binary layout was retained. BVH4/BVH8 and SIMD plane packets were not
promoted because the flattened binary plane-mask path already has sequential
access and zero hot allocation, while wider snapshots multiply the dominant
memory cost at 1M. The scalar path remains the oracle. Partial subtree rebuilds
were likewise rejected after design/measurement: flat subtree replacement needs
range compaction and stable-map repair, while the bounded rotation budget plus
deterministic full rebuild already bounds degradation without a second topology
mutation mechanism.

## Correctness and concurrency evidence

The isolated targeted run passed 26/26 tests. Coverage includes randomized
mixed add/remove/move sequences against brute force, sparse-refit topology
stability, empty/one-item/degenerate/invalid/giant bounds, identical centroids,
deterministic depth, plane-mask parity and counters, bounded ray hits, stable
reader generations, writer progress, four simultaneous readers, clean-swap and
traversal allocation, exact triangle bounds, and segment endpoint rejection.

Isolated builds completed with no compile errors and no new compiler warnings.
The build output contains the repository's existing Magick.NET vulnerability
advisories and two pre-existing unused-field warnings in `VPRC_SurfelGIPass`.

## Runtime evidence

The Unit Testing World was launched from the isolated editor build with MCP.
The Vulkan attempt reached world/camera setup but terminated on the existing
`vkCreateBuffer: Invalid device` failure. An OpenGL retry ran the world in play
mode, accepted two immediate camera positions, reported 32 active viewport
commands including nine opaque mesh commands, executed directional shadow-map
work, and produced a CPU profile containing repeated CPU-BVH camera collections
and clean swaps. The profile was copied to the run root's `logs/` directory.

Both MCP readbacks were viewed and were black even though render state reported
active mesh commands. The user confirmed that the editor was resized during
this run; the rendering log shows pending render-resource generations being
repeatedly superseded by resize/profile changes and command chains being skipped
while resources caught up. The black frames are therefore resize-regression
evidence, not a CPU-BVH visibility verdict, and are retained without being
treated as visual success. Camera,
multi-view, shadow, and stereo snapshot semantics are therefore promoted on the
brute-force/concurrent 1/2/4/8-view tests plus the live camera/shadow profile,
not on unreliable pixels. No CPU-BVH exception, false-negative result, reader
stall, or spatial update appeared in the runtime profile.

## Selected defaults and budgets

- Snapshot slots: 4, bounded to 2-16.
- SAH bins: 16.
- Leaf cap: 8, configurable 1-64.
- Local rotations: at most 32 per refit batch; kill switch available.
- Partial-quality observation threshold: SAH ratio 1.35 after 128 refits; the
  selected response remains bounded rotations rather than subtree replacement.
- Full rebuild: SAH ratio 1.75, root growth 3.0, or 512 consecutive refits.
- Mutation backlog: bounded and reported; dropped admissions are counted.

Static scenes stabilize at an O(1), zero-allocation swap. Sparse scenes scale
with unique dirty leaves and ancestors. Fully dynamic scenes remain an explicit
budget class and may cross the deterministic quality-rebuild thresholds.
