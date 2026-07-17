# GPU BVH Scene-Tree Improvements TODO

Last Updated: 2026-07-17
Owner: Rendering
Status: Not started (audit complete; implementation and measurement pending)
Scope: GPU scene-BVH bounds, build, refit, storage, traversal, profiling, and
integration centered on [`GpuBvhTree`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs),
`GPUScene`, and the scene culling shaders.

Related local docs:

- [CPU BVH Scene-Tree Improvements TODO](../cpu/cpu-bvh-scene-tree-improvements-todo.md)
- [GPU-Driven Occlusion Culling Architecture TODO](gpu-driven-occlusion-culling-architecture-todo.md)
- [GPU BVH Async Overflow Readback TODO](../../COMPLETED/gpu-bvh-async-overflow-readback-todo.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

Research references:

- [Maximizing Parallelism in the Construction of BVHs, Octrees, and k-d Trees](https://research.nvidia.com/publication/2012-06_maximizing-parallelism-construction-bvhs-octrees-and-k-d-trees)
- [HLBVH: Hierarchical LBVH Construction for Real-Time Ray Tracing](https://research.nvidia.com/sites/default/files/publications/HLBVH-final.pdf)
- [PLOC++: Parallel Locally-Ordered Clustering for Bounding Volume Hierarchy Construction Revisited](https://diglib.eg.org/items/cf7c86c3-0fd6-4f26-a2da-a8b20808199c)
- [Efficient Incoherent Ray Traversal on GPUs Through Compressed Wide BVHs](https://research.nvidia.com/publication/2017-07_efficient-incoherent-ray-traversal-gpus-through-compressed-wide-bvhs)
- [Selective BVH Restructuring](https://diglib.eg.org/server/api/core/bitstreams/679b22a1-314e-4102-bfba-5db17383005f/content)

## Goal

Turn the current GPU BVH from an expensive leaf-oriented prefilter into a
measured, genuinely hierarchical, zero-readback scene-culling structure with
correct spatial normalization, dirty-aware lifecycle management, compact
storage, scalable construction, and bounded quality under motion.

The first objective is to eliminate work that should not happen. Compact or
wide layouts, advanced builders, and ray-specific optimizations follow only
after profiling shows that a real hierarchy beats the flat culling baseline.

## Non-Goals

- Replacing the two-phase Hi-Z and persistent-visibility architecture described
  in the GPU-driven occlusion TODO. This document supplies the efficient BVH
  substrate and traversal contract used by that design.
- Introducing CPU readback into zero-readback submission modes.
- Hiding GPU build, overflow, or unsupported-path failures behind an
  unreported CPU fallback.
- Assuming ray-tracing BVH results automatically transfer to scene-frustum
  culling. Wide and compressed layouts must win XRENGINE's actual workloads.
- Changing per-mesh triangle packing or BLAS ownership unless a measured shared
  bottleneck requires it.

## Current Baseline And Audit Findings

The audit identified several high-impact problems:

- `GPUScene` marks BVH refit pending after an executed command-buffer swap when
  topology counts match, even if no command AABB changed. With the default
  clean-swap behavior, static scenes can copy command state and refit every
  frame.
- `VisualScene3D.SetBounds` is the only located path that supplies scene bounds
  to `GPUScene`, but normal world initialization does not appear to call it.
  The tree therefore expands its zero-sized default to approximately
  `[-0.5, 0.5]`; Morton generation clamps ordinary world-space centers into
  this range, collapsing spatial keys and degrading topology.
- `bvh_frustum_cull.comp` dispatches one invocation per leaf. It tests the leaf
  and then walks from that leaf toward the root. If the leaf intersects, every
  conservative ancestor containing it must also intersect, so this upward walk
  cannot reject the candidate. With the default one primitive per leaf, the
  command AABB is then often tested again.
- Nodes are explicitly padded to 80 bytes while their meaningful `std430`
  fields fit in approximately 48 bytes. Primitive ranges are also duplicated
  in a separate per-node buffer.
- Morton keys are sorted using a multi-dispatch bitonic sort with
  `O(N log^2 N)` compare stages and barriers; small builds can still pay a
  fixed padded sort.
- Both refit stages dispatch across the complete node count even when a stage
  only operates on leaves or internals. Leaf counters are cleared despite not
  being consumed by the leaf stage.
- Changed command bounds are uploaded with individual sub-data updates instead
  of coalesced ranges or a GPU-side copy from an existing bounds source.
- Node and AABB buffers grow to exact requested sizes, which encourages
  repeated reallocation during scene growth.
- Refit policy does not monitor tree quality. Stable-count motion can refit a
  badly degraded topology indefinitely.
- `BvhGpuProfiler` defines build/refit stages but the audited paths do not place
  the scopes needed to measure them.
- The optional `MortonPlusSah` refinement is not used by production callers and
  appears unsafe when leaf capacity exceeds one: it can reinterpret a leaf as
  an internal node without creating the corresponding child topology. Its
  large fixed per-invocation arrays are also likely to spill.

Primary sources and tests:

- [`GpuBvhTree.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs)
- [`GpuBvhTree.Dispatch.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs)
- [`GpuBvhTree.Buffers.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Buffers.cs)
- [`GpuBvhTypes.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTypes.cs)
- [`GPUScene.CommandBuffers.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs)
- [`GPUScene.Bvh.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs)
- [`GPUScene.BoundsHelpers.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs)
- [`bvh_frustum_cull.comp`](../../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp)
- [`bvh_build.comp`](../../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp)
- [`bvh_refit.comp`](../../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_refit.comp)
- [`bvh_nodes.glslinc`](../../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_nodes.glslinc)
- [`GpuBvhAndIndirectIntegrationTests.cs`](../../../../../XREngine.UnitTests/Rendering/GpuBvhAndIndirectIntegrationTests.cs)

## Target Architecture And Invariants

1. **Spatial inputs are authoritative and versioned.** Topology, AABB, command
   payload, and view changes have separate revisions and trigger only the work
   they require.
2. **Static scenes are free after stabilization.** They issue no BVH build,
   refit, AABB upload, buffer copy, or clear solely because a frame advanced.
3. **Bounds normalization is valid and observable.** Every build receives
   finite, non-degenerate bounds that contain live inputs, with an explicit
   escape/rebuild policy.
4. **Traversal is hierarchical.** A rejected internal node prevents all of its
   descendants from being visited.
5. **Flat culling remains the measured baseline.** Small or highly visible
   scenes may select flat culling when it is cheaper, but selection and fallback
   reasons are visible in diagnostics.
6. **Zero readback remains a contract.** Profiling counters use asynchronous or
   diagnostics-only readback and never gate frame progress.
7. **Tree quality is bounded under motion.** Refit can trigger restructure or
   rebuild based on measured degradation.
8. **Shader/host layouts are verified.** C# mirrors, GLSL offsets, buffer
   strides, descriptor ranges, and tests agree exactly.
9. **Every dispatch and barrier has a producer/consumer reason.** Redundant
   barriers and padded no-op invocations are removed after backend validation.

## Phase 0 - Instrumentation And Comparative Baseline

- [ ] Place real `BvhGpuProfiler` scopes around Morton generation, sorting,
  hierarchy build, optional refinement, refit clear, leaf initialization,
  traversal, and command emission.
- [ ] Record CPU submission time and GPU timestamp duration separately.
- [ ] Add GPU-written counters for:
  - [ ] builds, refits, skipped-clean frames, and rebuild reasons;
  - [ ] visited internal nodes, leaves, and commands;
  - [ ] frustum-plane tests and active-plane-mask reductions;
  - [ ] internal/leaf rejections and emitted commands;
  - [ ] maximum queue/stack occupancy and overflow;
  - [ ] Morton duplicate count, occupied-prefix histogram, and longest common
    prefix distribution;
  - [ ] normalized SAH/overlap metrics and refit age;
  - [ ] bytes allocated, copied, uploaded, and read back;
  - [ ] buffer reallocations and retained capacity.
- [ ] Surface low-frequency diagnostics through existing asynchronous stats
  plumbing; zero-readback modes must not synchronously map counters.
- [ ] Compare three culling paths using the same inputs:
  - [ ] flat per-command frustum culling;
  - [ ] the current leaf/upward-walk path;
  - [ ] the new root-down hierarchical path when implemented.
- [ ] Benchmark `1K`, `10K`, `100K`, and `1M` commands where memory permits.
- [ ] Cover uniform, clustered, identical-center, long-thin,
  giant-plus-many-small, and rapidly expanding bounds distributions.
- [ ] Cover dirty ratios of `0%`, `0.1%`, `1%`, `10%`, and `100%`, visible
  ratios near `0%`, `10%`, `50%`, and `100%`, and one/multiple/stereo views.
- [ ] Capture Vulkan and OpenGL baselines. Use RenderDoc to verify dispatch
  order, descriptor ranges, buffer contents, and barriers when timing alone is
  inconclusive.
- [ ] Store captures and reports under one bounded
  `Build/_AgentValidation/<run>/` root and summarize durable results in
  `docs/work/progress/rendering/`.

Acceptance criteria:

- [ ] Build, refit, traversal, and transfer costs are independently visible.
- [ ] The baseline can prove whether BVH culling beats flat culling for each
  scene-size and visibility regime.
- [ ] Zero-readback submission records zero synchronous BVH readback bytes.

## Phase 1 - Authoritative Scene Bounds And Morton Quality

- [ ] Route configured world/scene bounds into `VisualScene3D.SetBounds` and
  `GPUScene` during initialization and world changes.
- [ ] Assert or diagnose any build that receives invalid, non-finite,
  zero-volume, or non-containing normalization bounds.
- [ ] Add tests proving representative world-space centers produce a useful
  Morton distribution instead of collapsing to clamped corners.
- [ ] Define the authoritative long-term bounds policy:
  - [ ] reduce live command AABBs on GPU or CPU;
  - [ ] expand by a configurable margin;
  - [ ] retain bounds with hysteresis to prevent rebuild thrashing;
  - [ ] rebuild when a command escapes or quality falls below threshold;
  - [ ] handle an empty scene without fabricating a misleading domain.
- [ ] If the reduction runs on the GPU, keep the result resident and schedule a
  rebuild through GPU-visible state or asynchronous diagnostics without frame
  readback.
- [ ] Define behavior for giant, invalid, or temporarily unbounded commands.
  Conservative visibility is required; silent Morton corruption is not.
- [ ] Add a debug histogram/overlay for Morton occupancy and common-prefix
  depth, gated outside production hot paths.

Acceptance criteria:

- [ ] Every non-empty build uses finite bounds containing all build inputs.
- [ ] Uniform and clustered test scenes occupy spatially meaningful Morton
  ranges; clamping is exceptional and counted.
- [ ] Expanding motion triggers a documented hysteresis/rebuild action rather
  than permanent key collapse.

## Phase 2 - Dirty Revisions, Clean Frames, And Capacity Policy

- [ ] Split GPU-scene revisions into at least topology, bounds/AABB, command
  payload, and material/indirect metadata revisions.
- [ ] Mark `_bvhRefitPending` only when a command AABB or GPU-produced bound
  actually changes.
- [ ] Give skinned, blendshape, particle, or other GPU-updated bounds producers
  an explicit AABB revision/request contract; do not infer cleanliness from CPU
  commands alone.
- [ ] Make clean command-buffer publication safe for every producer before
  enabling it by default.
- [ ] Skip command snapshot copies when their source revision is unchanged.
- [ ] Make empty-tree `Clear()` idempotent and avoid CPU-zeroing/uploading the
  full retained capacity.
- [ ] Clear only logical headers/counters needed for correctness, using backend
  buffer-fill operations where appropriate.
- [ ] Grow node, range, Morton, primitive-AABB, and scratch buffers
  geometrically or by power-of-two capacity; retain logical count separately.
- [ ] Add shrink policy only if retained memory becomes significant; never
  resize in response to small frame-to-frame oscillation.
- [ ] Report capacity, logical use, reallocation count, and reason.

Acceptance criteria:

- [ ] A stabilized static scene issues zero BVH refits/builds and zero bounds
  uploads across an extended frame capture.
- [ ] GPU-deformed bounds still request refit correctly.
- [ ] Gradual scene growth causes logarithmically bounded reallocations rather
  than one reallocation per size increase.

## Phase 3 - Genuine Hierarchical Frustum Traversal

- [ ] Add an explicit flat-cull diagnostic path and use it as the correctness
  and performance baseline.
- [ ] Remove the leaf-to-root ancestor walk from the production traversal once
  parity tests demonstrate that it cannot provide additional rejection.
- [ ] Implement root-down traversal in which rejecting an internal AABB skips
  its complete descendant range.
- [ ] Choose and benchmark a GPU-appropriate traversal mechanism:
  - [ ] persistent threads consuming a bounded global work queue;
  - [ ] breadth-first frontier buffers with indirect dispatch;
  - [ ] cooperative subgroup traversal for small subtrees;
  - [ ] a bounded per-workgroup stack for treelets.
- [ ] Do not use an unbounded private stack per command; report every queue or
  stack overflow and resolve it conservatively.
- [ ] Propagate a view/frustum plane mask so descendants skip planes that fully
  contain their parent.
- [ ] Tune leaf capacities `1`, `2`, `4`, `8`, and `16`. A leaf test should
  amortize traversal overhead across commands without making bounds too loose.
- [ ] Avoid retesting a one-command leaf and its identical command AABB. Define
  when the leaf result is sufficient and when exact per-command testing is
  still required.
- [ ] Support multiple views without rebuilding topology; preserve independent
  visibility output and stereo correctness.
- [ ] Add a data-driven threshold selecting flat culling for small/highly
  visible workloads where hierarchy overhead loses. Log the selected reason in
  diagnostics.
- [ ] Coordinate node-level Hi-Z rejection and deferred-node recovery with the
  GPU-driven occlusion TODO rather than creating a second incompatible
  traversal.

Acceptance criteria:

- [ ] GPU results match flat frustum culling for deterministic and randomized
  scenes, including all leaf capacities and degenerate inputs.
- [ ] In a low-visibility or clustered scene, internal-node rejection reduces
  visited commands materially below flat culling.
- [ ] In a fully visible scene, the selector can choose the cheapest measured
  path without hidden CPU intervention.
- [ ] No traversal queue/stack overflow causes false occlusion.

## Phase 4 - Refit, Bounds Upload, And Synchronization Efficiency

- [ ] Dispatch internal-counter clear only across internal nodes.
- [ ] Dispatch leaf initialization/refit only across leaves.
- [ ] Remove clearing or atomics that the active refit algorithm never reads.
- [ ] Preserve the correct bottom-up completion invariant and prove memory
  ordering for OpenGL and Vulkan.
- [ ] Coalesce changed CPU AABB uploads into contiguous ranges.
- [ ] Where command conversion already writes a suitable GPU bounds buffer,
  evaluate a GPU copy/transform kernel instead of duplicate CPU sub-data calls.
- [ ] Keep dirty indices or dirty ranges in reusable storage; avoid per-frame
  collection allocation.
- [ ] Audit the barrier following cull dispatch. `DispatchCompute` already
  accepts a barrier mask; remove any duplicate explicit barrier only after
  Vulkan command-chain and OpenGL ordering tests prove it redundant.
- [ ] Record actual dirty leaf count and bytes transferred per refit.
- [ ] Add quality metrics and a maximum consecutive-refit policy so motion
  cannot retain a poor topology indefinitely.

Acceptance criteria:

- [ ] Refit invocation count is proportional to the required leaf/internal
  work, not two full node-count dispatches.
- [ ] Sparse CPU AABB changes produce bounded/coalesced uploads.
- [ ] OpenGL and Vulkan validation report no synchronization or visibility
  errors after barrier consolidation.
- [ ] Refit remains zero-readback in production submission modes.

## Phase 5 - Scalable Sort And Hierarchy Construction

- [ ] Add correctness and timing coverage for Morton counts around workgroup,
  padding, and power-of-two boundaries.
- [ ] Introduce tiered small-count handling so tiny trees do not pay a fixed
  large bitonic network.
- [ ] Replace the general bitonic path with a stable GPU radix sort over Morton
  key plus primitive identity.
- [ ] Preserve deterministic ordering for duplicate Morton codes and validate
  identical-center scenes.
- [ ] Keep the parallel linear hierarchy build compatible with the sorted
  output and validate root/parent/child invariants entirely on GPU-visible
  data.
- [ ] Compare plain LBVH against an upper-level SAH/HLBVH treelet pass or
  PLOC++ only after bounds and traversal are correct.
- [ ] Disable or remove `MortonPlusSah` from selectable production modes until
  it creates valid child topology for every leaf capacity and demonstrates a
  quality/performance win.
- [ ] Replace large fixed per-invocation SAH arrays with workgroup-shared or
  bounded treelet data if refinement remains.
- [ ] Count dispatches and barriers per build and set size-dependent budgets.

Acceptance criteria:

- [ ] Large builds scale approximately with radix passes plus linear hierarchy
  construction rather than `O(N log^2 N)` compare stages.
- [ ] Small builds avoid large padded sort networks.
- [ ] Duplicate and degenerate keys construct a deterministic, valid tree.
- [ ] Any refinement mode improves measured traversal enough to repay its build
  cost over its expected lifetime.

## Phase 6 - Compact Node And Buffer Layout

- [ ] Define one canonical host/shader node-layout specification with explicit
  byte offsets and stride assertions.
- [ ] Correct or remove the C# `GpuBvhTypes` mirror if its field offsets do not
  match the active shader layout.
- [ ] Compact the current 80-byte binary node toward its approximately 48-byte
  meaningful `std430` representation.
- [ ] Remove the separate primitive-range buffer when `first/count` can live in
  the node without expanding its stride.
- [ ] Separate traversal-hot bounds/child data from build/refit-only metadata
  if doing so reduces fetched cache lines.
- [ ] Update descriptor ranges, buffer allocation, debug export, and tests as
  one layout change; do not rely on implicit struct packing.
- [ ] Measure an SoA layout against compact AoS before committing to either.
- [ ] After binary compaction, benchmark BVH4/BVH8 and quantized child bounds.
  Treat compressed wide layouts as optional because scene-frustum behavior may
  differ from published incoherent ray workloads.
- [ ] Version any serialized/cached GPU BVH representation if one is introduced
  later.

Acceptance criteria:

- [ ] Host and shader layout tests verify every field offset and total stride.
- [ ] Binary node plus primitive-range memory falls by at least 40% at the
  current one-primitive leaf default, or a measured alternative justifies a
  different target.
- [ ] Compact layout produces identical build, refit, cull, and debug-export
  results on OpenGL and Vulkan.

## Phase 7 - Quality Maintenance Under Motion

- [ ] Establish baseline normalized SAH/overlap, depth, and Morton-prefix
  metrics immediately after build.
- [ ] Update quality estimates after refit without synchronous CPU readback.
- [ ] Trigger a rebuild or bounded GPU restructuring when overlap, Morton
  displacement, bounds escape, refit age, or dirty fraction crosses measured
  thresholds.
- [ ] Evaluate local treelet rebuild/rotation only if full rebuild frequency or
  traversal degradation remains material after radix construction lands.
- [ ] Budget maintenance work per frame and keep the previous conservative tree
  active until replacement data is complete.
- [ ] Ensure rebuild scheduling is visible in diagnostics and cannot oscillate
  around a threshold.
- [ ] Add hysteresis and minimum lifetime for rebuilt normalization bounds.

Acceptance criteria:

- [ ] Long-running animated scenes do not show unbounded traversal-cost growth.
- [ ] Rebuild/restructure frequency remains within the Phase-0 GPU budget.
- [ ] Maintenance never creates false-negative visibility or a synchronous
  readback dependency.

## Phase 8 - Shared Ray-Traversal And Mesh-BVH Follow-Up

These tasks are secondary to scene frustum traversal but share BVH layout or
shader infrastructure:

- [ ] Order ray children by AABB entry distance so nearer hits tighten the
  search distance before farther traversal.
- [ ] Instrument fixed-stack occupancy and overflow in
  [`BvhRaycastCore.glsl`](../../../../../Build/CommonAssets/Shaders/Snippets/BvhRaycastCore.glsl).
- [ ] Evaluate short-stack plus escape-link/rope traversal only if stack
  pressure or private-memory spills are measured.
- [ ] Fix packet-ray dispatch for widths below the shader subgroup width so
  inactive lanes cannot duplicate rays or writes.
- [ ] Avoid repacking and uploading immutable static mesh triangles on every
  `Prepare`; cache the packed-data revision.
- [ ] Keep scene TLAS and per-mesh BLAS ownership explicit if a two-level
  traversal model is introduced.

Acceptance criteria:

- [ ] Ray/mesh changes have dedicated parity tests and do not regress scene
  culling memory or dispatch cost.
- [ ] Stack overflow remains conservative, observable, and absent in supported
  benchmark scenes.

## Phase 9 - Validation, Rollout, And Documentation

- [ ] Expand `GpuBvhAndIndirectIntegrationTests` beyond shader-source policy to
  cover build/refit bounds, hierarchy invariants, culling parity, clean-frame
  behavior, and layout offsets.
- [ ] Add randomized GPU-vs-CPU brute-force visibility parity across all
  workload distributions and leaf capacities.
- [ ] Add build/refit tests for zero, one, duplicate-center, degenerate, giant,
  and maximum-capacity inputs.
- [ ] Validate buffer growth and empty-tree lifecycle without repeated clears
  or reallocations.
- [ ] Run the narrowest shader/build tests after each functional slice, then
  the relevant rendering regression suites.
- [ ] Use the Unit Testing World to validate static, skinned, editor-moved,
  streaming add/remove, shadow, and stereo scenes.
- [ ] Capture at least two camera positions per visual validation; an artifact
  invariant to the camera may indicate stale or uninitialized data.
- [ ] Inspect Vulkan/OpenGL rendering logs and RenderDoc captures for dispatch
  order, descriptors, buffer contents, and barriers.
- [ ] Compare flat and BVH paths in shipping-representative zero-readback modes,
  not only instrumented diagnostics.
- [ ] Keep a runtime kill switch and observable flat fallback during rollout.
  Missing shaders, overflow, invalid bounds, or unsupported features must log a
  reason.
- [ ] Update mesh-submission, default-pipeline, and GPU-driven occlusion docs
  with the final traversal, lifecycle, and fallback contracts.

Acceptance criteria:

- [ ] Static scenes perform zero BVH work after stabilization.
- [ ] Hierarchical traversal wins its enabled workload classes against flat
  culling and reduces visited commands in rejection-heavy scenes.
- [ ] No false-negative visibility is observed across Vulkan, OpenGL, stereo,
  shadow, skinned, and mutation validation.
- [ ] Production modes perform zero synchronous BVH readback.
- [ ] The compact layout, radix builder, and refit policy meet budgets recorded
  from Phase 0 rather than unmeasured targets.
- [ ] No shader validation errors, compiler warnings, or hidden CPU fallbacks
  are introduced.

## Risks And Open Questions

- Which traversal model best fits Vulkan and OpenGL without excessive queue
  management or dispatch synchronization?
- At what command count and visibility ratio does a hierarchy beat the flat
  compute culler on target NVIDIA, AMD, and Intel hardware?
- Should scene bounds be reduced every frame, only when dirty, or maintained by
  an incremental GPU expansion/rebuild policy?
- Can GPU-produced deformation bounds publish a cheap monotonic revision
  without CPU participation?
- Which leaf capacity minimizes total traversal plus exact-command tests for
  frustum, Hi-Z, shadow, and stereo workloads?
- Is binary compact AoS better than BVH4/8 for scene culling once plane masks
  and multi-view reuse are included?
- Can normalized SAH/overlap be estimated cheaply enough on GPU to guide
  rebuilds, or should proxy metrics such as Morton displacement and bounds
  escape drive the first version?
- Should root-down frustum and node-level Hi-Z traversal share one queue and
  shader, or remain separate phases with a common node format?

## Definition Of Done

- [ ] Scene bounds and Morton normalization are correct and instrumented.
- [ ] Clean frames perform no build, refit, upload, or clear work.
- [ ] The enabled GPU BVH path performs real root-down hierarchical rejection.
- [ ] Refit dispatches, AABB transfers, barriers, and buffer growth are bounded
  and measured.
- [ ] General construction uses a scalable stable sort/build path.
- [ ] Host and shader layouts match and materially reduce BVH memory.
- [ ] Motion quality has a deterministic rebuild/restructure policy.
- [ ] Flat/BVH parity, randomized correctness, backend validation, and runtime
  captures pass with zero production readback.
- [ ] Related architecture and occlusion documents describe the final contract.
