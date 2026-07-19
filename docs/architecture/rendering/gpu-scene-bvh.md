# GPU Scene BVH

The GPU scene BVH accelerates command-level frustum culling for GPU-indirect
and meshlet submission strategies. It is a zero-readback, GPU-resident LBVH
owned by `GPUScene` and built by `GpuBvhTree`.

It is a scene TLAS over render commands, not a per-mesh triangle BLAS and not
an occlusion authority. Frustum traversal produces conservative candidates;
the GPU-driven occlusion pipeline owns Hi-Z persistence and final indirect
command emission policy.

## Lifecycle contract

Scene state is published independently:

- command payload snapshots use the command-content revision;
- draw metadata and bounds use their own dirty ranges;
- command AABBs use `_commandAabbRevision` and a coalesced dirty range;
- topology is rebuilt when the represented command count changes;
- GPU-produced skinned AABBs publish a new AABB revision by reseeding their
  slot before the reduction dispatch.

The command payload, stable bounds snapshot, and command-AABB input share one
dense command index. Swap-removal moves all three together. When the internal
BVH is enabled after commands already exist, the first rebuild seeds every
CPU-owned command AABB from the stable `BoundsGpu` snapshot before publishing
the build input; uninitialized allocator contents can never become leaf data.
GPU-owned skinned slots remain under their reducer's ownership. If direct GPU
publication cannot be scheduled, ownership is revoked and the CPU tight bounds
are republished before the tree is prepared.

A static scene does not copy its command snapshot, upload AABBs, build, refit,
clear the tree, reset overflow state, or allocate timestamp queries after
stabilization. `GpuBvhTree.Build` also has a defensive allocation-free identity
gate: a clean request is reusable only when the AABB buffer object, primitive
count, and normalized bounds exactly match the last completed build. Explicit
topology, normalization-domain, or periodic-quality rebuild reasons invalidate
that identity.

Viewport strategy scopes may temporarily disable the internal provider without
invalidating its retained topology. Re-enabling builds only when no tree exists
or the retained tree is not ready; ordinary command-count and AABB revisions
continue to drive rebuild/refit decisions. This prevents multi-viewport state
transitions from turning into per-frame rebuilds.

Every retained resource has a stable `GpuBvhTree[owner:id].*` backend label.
This distinguishes the scene TLAS from per-mesh trees and makes an unexpected
upload attributable without allocating diagnostic strings in the render path.

## Normalization bounds

World-setting changes flow through `VisualScene3D.SetBounds` into `GPUScene`.
At build time, finite live CPU command AABBs are reduced and expanded by a ten
percent margin (with a small absolute minimum). Explicit world bounds are used
when they contain the live scene without diluting each axis by more than 2x
(one octree level, or 8x volume).
The previous domain is retained while it contains the new candidate and is no
more than 4x its volume. A finite command escaping the active domain requests
a rebuild. Invalid GPU normalization bounds abort the build with a diagnostic.

GPU-owned deformation bounds remain resident. Their producer explicitly
requests refit; production zero-readback modes never map them to schedule work.
A full rebuild after 120 consecutive refits bounds topology degradation and
refreshes Morton order without a synchronous quality readback. A resident
quality pass runs after each build and every 30th refit. Its fixed-size SSBO
records duplicate Morton keys, occupied high-prefix bins, longest-common-prefix
distribution, and normalized overlap/SAH proxies. Normal rendering never maps
this buffer; overlays and capture tools may consume it asynchronously.

## Construction and storage

Small builds of at most 1024 primitives use the single-workgroup shared-memory
sort. Larger builds use four stable 8-bit radix passes:

1. per-block 256-bin histogram;
2. bin and block prefix offsets;
3. stable scatter preserving object-id order for duplicate keys.

The linear Karras hierarchy build consumes the sorted output. The experimental
`MortonPlusSah` mode is rejected because its former shader could reinterpret a
leaf without creating valid child topology.

The four-word overflow SSBO is mandatory build state. It is allocated before
dispatch descriptors are captured and remains bound through Morton generation,
sorting, hierarchy construction, refit, quality analysis, and traversal. The
first Morton dispatch clears it in-band, followed by a shader-storage barrier;
the next Morton dispatch performs generation with the same stable descriptor.
This avoids replacing a Vulkan buffer after descriptor capture and guarantees
that every build stage can report overflow without a CPU reset upload.

Nodes use one canonical 48-byte `std430` layout:

| Offset | Field |
|---:|---|
| 0 | `vec3 minBounds` |
| 12 | `uint leftChild` |
| 16 | `vec3 maxBounds` |
| 28 | `uint rightChild` |
| 32 | `uvec2 primitiveRange` |
| 40 | `uint parentIndex` |
| 44 | `uint flags` |

Primitive ranges are embedded in nodes; no second range allocation is used.
Node, Morton, radix scratch, refit counter, and command-AABB buffers grow to
power-of-two capacity and retain logical counts separately. Clearing a tree
updates only its four-word header.

## Refit and traversal

Refit clears counters only for internal nodes and dispatches leaf initialization
only across leaves. Counters are indexed by `parentIndex - leafCount`; the
second arriving child combines its sibling bounds and propagates upward.

Frustum traversal is root-down. A power-of-two set of workgroups selects
disjoint top-level subtrees by deterministic binary root descent, then each
workgroup consumes breadth-first frontiers from its bounded shared queue. The
dispatch targets 512 commands per workgroup and caps at 256 workgroups. If an
unbalanced tree reaches a leaf early, only the zero-suffix partition owns it,
preventing duplicate emission. Rejected internal nodes skip their full primitive
ranges. Active frustum-plane masks flow to children, allowing planes that fully
contain a parent to be skipped below it. A one-command leaf does not repeat its
identical command-AABB test.

If the shared frontier is full, the shader reports queue pressure and directly
tests the affected subtree's primitive range. This is slower but conservative:
queue pressure cannot cause false-negative visibility.

Selection is driven by `GpuBvhSelectorCalibration`. Threshold buckets separate
OpenGL/Vulkan, one/multiple/stereo views, and low/medium/high visibility. A
calibration is produced from median flat-versus-BVH samples and requires BVH to
win at two consecutive command counts, preventing a single noisy sample from
promoting hierarchy traversal. Missing hardware buckets intentionally remain
on flat GPU culling. When an instrumented mode already reads
GPU counters, the previous frame's emitted/input ratio updates the visibility
bucket; zero-readback modes never map stats for selector input.

Multiple views reuse the same topology and retain independent visibility
outputs. Node-level Hi-Z integration remains owned by the GPU-driven occlusion
architecture; it must reuse this node layout and traversal contract.

## Diagnostics and fallback

`BvhGpuProfiler` exposes separate CPU submission and asynchronous GPU timestamp
durations for Morton coding, sorting, hierarchy build, refit clear, leaf refit,
traversal, command emission, aggregate build/refit/cull, and raycasts. Traversal
and emission remain separate through the profiler transport and editor UI. The
GPU stats buffer also records visited internal nodes, leaves, commands, plane
tests and reductions, rejections, emissions, maximum queue occupancy, and queue
overflow.

`GpuBvhDiagnostics` reports lifecycle and capacity counters, rebuild reasons,
the exact dirty-leaf count and AABB upload/copy byte counts for the last
maintenance operation, synchronous/asynchronous readback bytes, zero-readback
submission count, and resident-quality cadence. Production zero-readback
strategies neither enqueue overflow/stat mappings nor hide a failed GPU path
behind a CPU culler.

Missing programs, invalid bounds, malformed topology, or unavailable provider
buffers keep the flat GPU culler visible as an explicit logged fallback. The
runtime strategy remains the kill switch. Zero-readback strategies do not
enqueue overflow or stats mappings.

Ray traversal uses the same compact node layout. It visits the nearer child
first, falls back to the node primitive range on stack pressure, and masks
inactive packet lanes when the requested packet width is below the shader
workgroup width. A four-word diagnostics SSBO records trace count, maximum
stack occupancy, overflow count, and conservative recovery count. Binding 4 is
always writable: callers may supply a diagnostics target, otherwise the
dispatcher owns a persistent fallback buffer that is released on reset/dispose.

The scene BVH is the TLAS over render commands. `GpuMeshBvh` owns triangle BLAS
data for mesh picking and collision consumers. Immutable triangle packing is
cached across builds and invalidated only when the source geometry buffer
revision changes; dynamic/skinned geometry keeps its explicit refresh path.
No scene-TLAS traversal currently descends into a mesh BLAS.

## Render-graph and synchronization ownership

`VPRC_BuildAccelerationStructure` registers one synthetic compute pass and
pushes that exact pass identity around GPU skinned-bounds production, BVH
construction/refit, and quality analysis. Vulkan treats missing metadata as an
observable unavailable path and publishes an empty acceleration structure
instead of borrowing an unrelated fallback pass. OpenGL follows the same scope
when metadata exists. Dispatch-local barriers describe producer/consumer needs;
the render graph owns cross-pass ordering.

Vulkan auto-generated compute uniform buffers are keyed by render program,
image, and reusable dispatch identity. A sequence of build stages may reuse one
program, but each dispatch retains its own constants; later stage updates must
not overwrite an earlier dispatch's descriptor snapshot. The in-band overflow
reset is likewise a distinct dispatch: its `resetOverflow = 1` constants cannot
alias the following Morton-generation dispatch where `resetOverflow = 0`.

## Validation status and performance claims

Automated coverage includes host/shader layout, build/refit/root bounds,
revision and clean-frame lifecycle, stable radix construction, randomized
GPU-versus-CPU frustum parity, leaf capacities 1/2/4/8/16, multi/stereo views,
queue-pressure recovery, compact-node ray parity, packet lanes, and forced ray
stack recovery. The workload generator enumerates the full command-count,
bounds-distribution, dirty/visible ratio, view, leaf-capacity, and backend
calibration space.

Live validation and disposable captures are summarized in
[GPU BVH Scene-Tree Implementation](../../work/progress/rendering/gpu-bvh-scene-tree-implementation.md)
and the bounded agent-validation report referenced there. Automated and
single-adapter results establish the implementation contract; they do not
constitute an AMD/Intel/NVIDIA cross-vendor performance claim. Unavailable
hardware qualification remains explicit in
[GPU Scene BVH External-Hardware Qualification](../../work/testing/rendering/gpu-scene-bvh-external-hardware-qualification.md).
The flat GPU fallback and runtime strategy kill switch remain production
architecture until a separate, representative promotion decision removes them.

On the recorded RTX 4070 Laptop/OpenGL run, the partitioned traversal removed
queue pressure from all 20 measured cells and beat flat culling by 3.17x for the
1M-command, 10%-visible uniform cell. Flat culling won the other 19 cells. No
bucket therefore met the two-consecutive-win calibration rule, so the shipped
uncalibrated policy remains flat rather than encoding the former 512-command
guess as a performance claim.

A second five-row 10K maintenance sweep covered dirty ratios
0/0.1/1/10/100%, leaf capacities 1/2/4/8/16, visible ratios
0/10/50/100%, and one/two/three-view classes. It measured production build,
refit, traversal, emission, transfer bytes, dispatches, barriers, retained
capacity, and overflow. All GPU-built trees matched flat visibility with zero
build or traversal overflow. This bounded single-adapter result validates the
measurement path; it does not promote a selector bucket.

A forced RTX 4070 Laptop/Vulkan RenderDoc capture then verified the production
build chain and resident data. Morton reset/generation dispatched at events
94/98; hierarchy stages 0/1/2/3 at 108/112/116/120 retained distinct uniform
buffers; traversal ran at 151. Overflow binding 8 referenced the same non-null
16-byte resource throughout. The exported compact tree contained 251 nodes
with root 126, all 251 nodes reachable, 126 leaves covering 126 commands, zero
invalid node or command bounds, zero invalid children, parents, or ranges, and
zero overflow flags. RenderDoc reported no capture log messages. This remains
single-adapter correctness evidence, not cross-vendor qualification.

## Research basis

- [Maximizing Parallelism in the Construction of BVHs, Octrees, and k-d Trees](https://research.nvidia.com/publication/2012-06_maximizing-parallelism-construction-bvhs-octrees-and-k-d-trees)
- [HLBVH: Hierarchical LBVH Construction for Real-Time Ray Tracing](https://research.nvidia.com/sites/default/files/publications/HLBVH-final.pdf)
- [PLOC++: Parallel Locally-Ordered Clustering for Bounding Volume Hierarchy Construction Revisited](https://diglib.eg.org/items/cf7c86c3-0fd6-4f26-a2da-a8b20808199c)
- [Efficient Incoherent Ray Traversal on GPUs Through Compressed Wide BVHs](https://research.nvidia.com/publication/2017-07_efficient-incoherent-ray-traversal-gpus-through-compressed-wide-bvhs)
- [Selective BVH Restructuring](https://diglib.eg.org/server/api/core/bitstreams/679b22a1-314e-4102-bfba-5db17383005f/content)
