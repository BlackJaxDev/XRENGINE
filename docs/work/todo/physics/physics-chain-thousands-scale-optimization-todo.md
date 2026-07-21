# Physics Chain Thousands-Scale Optimization TODO

Last Updated: 2026-07-20
Owner: Physics / Rendering / Performance
Status: In progress - foundational world scheduling, CPU hot-path reductions,
dependency-correct GPU traversal, and selective particle readback implemented;
baseline budgets and the full data-oriented runtime remain outstanding
Execution: Current worktree unless the owner explicitly requests a dedicated
branch.

Checklist convention: `[x]` means the entire statement is implemented in the
current worktree. Partial foundations remain `[ ]` until every clause is met;
acceptance and performance items remain open until measured evidence exists.

Related contracts and evidence:

- [Physics Chain Performance Test Plan](../../testing/physics-chain-performance.md)
- [Physics Chain Performance Guide](../../../developer-guides/rendering/physics-chain-performance.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Compact Zero-Readback Rendering TODO](../rendering/optimization/compact-zero-readback-rendering-todo.md)
- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs`
- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent Fields.cs`
- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.Particle.cs`
- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.ParticleTree.cs`
- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs`
- `XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs`
- `Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChain.comp`
- `XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferPersistentRingAllocator.cs`
- `XREngine.Editor/Unit Tests/Math/MathIntersectionsWorldControllerComponent.cs`
- `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`

## Goal

Make thousands to tens of thousands of concurrent physics chains practical on
the existing multithreaded CPU and strict zero-readback GPU paths. Steady-state
cost must scale primarily with active simulated particles and relevant
colliders, not with component count, transform-object count, maximum arena
capacity, renderer count, or authored scene hierarchy size.

The completed system should use a single world-owned runtime, data-oriented
CPU and GPU backends, stable resident memory, direct palette and bounds output,
GPU-driven active-work generation, selective asynchronous compatibility
readback, and explicit quality budgeting. The common short linear-chain case
must have specialized CPU and GPU kernels; general branched chains remain
supported through a measured fallback path.

This is not a request to make the current component loop incrementally less
slow and stop there. Immediate wins are useful, but the final architecture must
remove per-component scheduling, per-particle notification objects, per-frame
GPU repacking, transform-hierarchy publication, and broad readback from the
mass-production path.

## Definition Of Success

- [x] One world-level tick schedules all CPU chains; chain count does not add
  one or more engine tick callbacks per component.
- [ ] One render-graph integration point schedules GPU simulation, palette,
  bounds, and optional readback work; chains do not own steady-state render
  commands.
- [ ] The CPU hot path performs zero managed heap allocations, takes no global
  locks, and performs no per-chain atomics in a steady-state frame.
- [ ] CPU work is parallel across input gathering, simulation, palette
  generation, bounds generation, and output publication, with deterministic
  scalar/reference coverage for correctness.
- [ ] The GPU zero-readback path keeps topology, static particle data,
  collider sets, runtime state, palettes, and bounds resident across frames.
- [x] A steady-state GPU frame uploads only dirty dynamic instance headers or
  explicitly changed resources; it does not snapshot/repack every chain or
  copy whole resident groups into transient combined buffers.
- [ ] GPU dispatch count and barriers scale with active topology/feature
  buckets, not individual chains.
- [x] GPU kernels have explicit parent-before-child ordering. No invocation
  reads a parent that another invocation may still be writing without a valid
  synchronization boundary.
- [ ] The normal rendering path consumes current/previous palette slices and
  conservative GPU bounds directly, without writing every simulated result
  back through scene `Transform` objects.
- [x] Strict zero-readback profiles perform no current-frame CPU readback for
  simulation, palette generation, bounds, culling, or dispatch sizing.
- [x] Explicit CPU consumers can request narrowly gathered chain, bone, or
  socket results through a bounded asynchronous readback contract.
- [ ] Sleep and update-rate LOD reduce work predictably while a documented
  strict full-rate mode preserves gameplay-critical behavior.
- [ ] Release benchmark evidence covers 100 through 10,000 chains, reports
  CPU and GPU stage costs separately, and demonstrates the approved budgets on
  named hardware without hiding rendering, uploads, barriers, or readback.

## Guardrails

- Do not silently fall back from an explicitly selected GPU path to CPU.
  Report unsupported capabilities, capacity failures, and backend failures.
- Do not silently lower update rate, collision quality, or constraint quality.
  Quality tiers are explicit policy selected by the caller or budget system.
- Preserve a strict mode that simulates every active chain at its requested
  fixed rate and iteration count.
- Heap allocations in per-frame physics, rendering, skinning, bounds, or
  culling paths are bugs unless a profile-backed exception is documented.
- Optimize end-to-end cost. A faster solver that leaves thousands of
  hierarchy writes, skinning dispatches, bounds stalls, or draw calls is not
  sufficient.
- Treat all capacities and overflows as explicit contracts. Never truncate
  particles, chains, colliders, palettes, bounds, or readback requests
  silently.
- Keep diagnostic telemetry delayed or sampled so it cannot become a shipping
  hot-path tax.
- Do not introduce an asset/data migration without owner approval. The runtime
  architecture may break pre-v1 APIs, but a serialized format change requires
  a separately recorded migration decision.
- Do not merge a more complex kernel merely because it is theoretically
  faster. It must win a relevant benchmark bucket and retain correctness.

## Current-State Bottleneck Inventory

These findings define the starting hypotheses. Phase 0 must measure them and
correct this list where traces disagree.

### Component and scheduling overhead

- `PhysicsChainComponent` registers multiple tick callbacks per component.
- Each component owns render/debug submission objects even when production
  debug drawing is not useful.
- Global update coordination uses shared queues, work items, locks, and atomic
  update accounting around individually scheduled components.
- The existing batch executor parallelizes the solver but leaves preparation,
  snapshots, transform-tree refresh, and result application substantially
  serial.
- Transform initialization can be reached from more than one preparation path;
  verify and remove redundant work rather than relying on call-site folklore.

### CPU data and solver overhead

- Runtime particles are managed `XRBase` objects whose hot property writes
  participate in property-change machinery.
- Preparation walks and refreshes scene transform hierarchies and snapshots
  data that should be static or versioned.
- Inner loops use object-oriented particle/collider access and virtual shape
  dispatch instead of contiguous feature-specialized data.
- Constraint work recomputes values such as rest length or force terms that
  can be authored or precomputed.
- Applying results through thousands of individual transforms serializes work,
  invalidates hierarchy state, and adds downstream notification cost.

### GPU submission and residency overhead

- GPU data preparation clears, rebuilds, hashes, and snapshots particle,
  transform, tree, and collider collections.
- The dispatcher sorts active requests, rebuilds combined arrays, recomputes
  signatures, and uploads data that is often unchanged.
- Resident and combined buffers can be copied when group layouts change,
  weakening the benefit of persistent GPU-authoritative state.
- CPU-side grouping by isolation key, skip state, and loop count creates extra
  groups and barriers.
- The dispatcher is tied directly to the OpenGL renderer instead of a narrow
  backend-neutral physics-chain compute contract.
- Batched palette generation has restrictions that can fall back to
  per-renderer dispatch and barrier patterns.
- When compatibility synchronization is requested, readback can cover a whole
  combined group instead of only requested chains or outputs.

### GPU kernel overhead and correctness risk

- The current shader maps work to particles, while a child can read a parent
  concurrently being written by another invocation in the same dispatch.
  This is not a valid dependency schedule.
- Every particle can scan every collider even when a chain cannot intersect
  most of them.
- Full transform matrices and relatively wide particle/tree/collider records
  consume bandwidth where compact static and dynamic records would suffice.
- Rest-length and collision math includes repeated square roots and other
  invariant work.
- Shader fields that are unused or redundant still consume layout/bandwidth
  and obscure the real contract.
- Separate simulation, palette, and bounds passes can add traffic and barriers
  that may be fused or globally batched.

### Downstream rendering and synchronization overhead

- Thousands of individually submitted skinned renderers can dominate after
  solver cost is reduced.
- CPU transform publication can trigger unrelated hierarchy/render work.
- Explicit bounds paths still contain wait/readback behavior for CPU consumers;
  the mass path needs GPU-owned conservative bounds.
- Per-chain debug rendering and profiler detail can distort the workload being
  measured.

## Target Runtime Architecture

The intended ownership flow is:

```text
PhysicsChainComponent (authoring facade)
    -> structural/dynamic command buffers
PhysicsChainWorld (one simulation owner and one render integration point)
    -> immutable template arena + collider-set arena + instance/state arena
    -> CPU backend or GPU backend
    -> current/previous palette atlas + bounds + activity/status buffers
    -> renderers and GPUScene culling
    -> optional selective asynchronous CPU mirror/readback
```

### Required runtime records

| Record | Lifetime | Required contents |
| --- | --- | --- |
| `PhysicsChainTemplate` | Immutable, deduplicated | Topology, depth ranges, rest offsets/lengths, authored coefficients, bone mapping, influence bounds, kernel feature mask |
| `PhysicsChainInstance` | Stable registration | Template ID, collider-set ID, root/input slice, state slice, palette slice, bounds slot, update/quality policy, flags, generation |
| `PhysicsChainState` | Dynamic, backend-owned | Current/previous particle state, velocity/inertia terms, sleep/error state, simulation clock |
| `PhysicsChainColliderSet` | Shared and versioned | Typed compact collider arrays, transforms, broadphase structure or candidate metadata, version |
| `PhysicsChainOutput` | Backend-neutral | Current/previous palette slice, bounds slot, validity/generation, optional CPU mirror status |
| `PhysicsChainHandle` | Generational | Stable slot plus generation; stale handles must fail validation rather than alias reused storage |

### Data ownership rules

- Authoring components own editable settings and references, not solver storage.
- The world owns registration, capacity, scheduling, state transitions, and
  output publication.
- Templates and collider sets use stable IDs and explicit versions.
- Structural commands are add/remove/retemplate/resize/rebind operations.
  Dynamic commands update roots, forces, parameters, visibility, or quality.
- Structural changes may rebuild a bucket or grow an arena outside its hot
  dispatch. Ordinary motion must not look structural.
- CPU and GPU output contracts are identical at the renderer boundary even
  when their internal layouts differ.
- A chain may request a CPU transform mirror, but the renderer must not require
  one.

## Priority And Dependency Order

| Priority | Work | Why it comes here |
| --- | --- | --- |
| P0 | Baseline, correctness oracle, budgets | Prevents optimizing the wrong stage or accepting invalid GPU ordering |
| P0 | World ownership and stable records | Removes component-count overhead and enables every later optimization |
| P0 | GPU residency and direct outputs | Removes repack/copy/hierarchy costs that dominate at thousands of chains |
| P0 | CPU data-oriented solver | Makes the CPU path viable and provides a strong deterministic reference |
| P1 | GPU dependency-correct specialized kernels | Improves simulation throughput after submission overhead is bounded |
| P1 | Collider sharing, broadphase, and specializations | Prevents collision cost from multiplying particles by all colliders |
| P1 | Palette, bounds, and renderer batching | Ensures solver gains survive end-to-end rendering |
| P1 | Sleep and explicit update-rate LOD | Provides the largest scalable reduction for real scenes |
| P2 | Selective readback and tooling | Supports gameplay/debug consumers without compromising the mass path |
| Experimental | Compression, async compute, kernel autotuning | Hardware-dependent; adopt only with profile evidence |

## Phase 0 - Measurement Contract, Baseline, And Decisions

### Reproducible benchmark setup

- [x] Create a bounded validation root under
  `Build/_AgentValidation/<timestamp>-physics-chain-scale/` for reports,
  traces, logs, and temporary captures when implementation starts.
- [ ] Build and run Release binaries with debugger, validation layers, verbose
  per-chain logging, debug drawing, and editor-only instrumentation disabled.
- [x] Record exact CPU, core topology, memory configuration, GPU, driver,
  Windows version, power mode, renderer backend, resolution, and refresh mode.
- [x] Use deterministic chain templates, collider layouts, root motion,
  external forces, visibility patterns, and random seeds.
- [x] Separate startup allocation, shader compilation, buffer growth, asset
  upload, and pipeline warmup from the timed steady-state interval.
- [x] Add an explicit settle gate that does not start timing until chain count,
  arena capacities, shader/pipeline compilation, upload queues, and renderer
  object counts remain stable for the configured interval.
- [x] Capture cold-start and structural-churn measurements separately; do not
  mix them into steady-state medians.
- [ ] Record at least 1,000 steady-state frames per matrix point or enough time
  to produce stable p95/p99 values, whichever is greater.
- [ ] Preserve raw per-frame or histogram data under the validation root and
  summarize it in a durable progress or testing note.

### Benchmark matrix

- [ ] Sweep chain counts: 100, 500, 1,000, 2,000, 5,000, and 10,000.
- [ ] Sweep particle/segment counts representative of 4, 8, 16, and 32
  dynamic segments per chain.
- [ ] Cover the common linear topology and at least one branched topology.
- [ ] Cover collider cases: none, 2 simple shared colliders, 5 mixed
  colliders, and a large set that requires broadphase candidate reduction.
- [ ] Cover shared collider sets and unique collider sets independently.
- [ ] Cover active ratios of 100%, 50%, 10%, and sleeping/offscreen-heavy
  scenes.
- [ ] Cover no rendering, palette/bounds only, identical instanced meshes, and
  diverse skinned renderers.
- [ ] Cover CPU strict, GPU strict zero-readback, explicit quality-tiered CPU,
  and explicit quality-tiered GPU modes.
- [ ] Cover CPU mirror/readback disabled, sparse socket requests, sparse whole
  chain requests, and a deliberately expensive diagnostic full-sync case.
- [ ] Cover OpenGL first and Vulkan when its compute/buffer contract is ready;
  report unsupported backend cases explicitly.
- [ ] Measure fixed simulation rates of 30, 60, 90, and 120 Hz where relevant,
  without conflating simulation rate with render rate.

### Required metrics

- [ ] Report whole-frame CPU and GPU p50, p95, p99, and maximum times.
- [ ] Report simulation cost per active chain and per active particle.
- [ ] Report CPU time for registration/structural commands, input gathering,
  scheduling, solve, collision, palette, bounds, publication, renderer
  submission, and compatibility synchronization.
- [ ] Report CPU worker utilization, work imbalance, queue latency, context
  switches where available, and time spent waiting on locks/fences.
- [ ] Report managed allocations per frame, GC collections, and hot-path
  boxing/closure/LINQ evidence.
- [ ] Capture CPU cache misses, branch misses, memory bandwidth, and SIMD width
  on at least one representative high-count profile where tooling permits.
- [ ] Add GPU timestamps for active-list generation, simulation, collision,
  palette, bounds, skinning, culling, and relevant draw work.
- [ ] Report GPU dispatch count, workgroups, active lanes, barriers by kind,
  buffer copy bytes, upload bytes, readback bytes, and fence waits.
- [ ] Capture shader occupancy, register pressure/spills, shared-memory use,
  cache behavior, and bandwidth with the best available vendor/tool capture.
- [ ] Report active, rate-limited, sleeping, culled, and woken chain counts.
- [ ] Report palette dispatches, skinning dispatches, renderer/draw count, and
  bounds/culling cost so physics improvements cannot shift cost out of view.
- [ ] Record arena capacity, live use, fragmentation, growth count, and bytes
  by static, state, collider, palette, bounds, and readback resource.

### Correctness oracle and policy decisions

- [x] Define a deterministic scalar CPU reference step used by unit tests and
  offline comparisons; it need not be the shipping fast path.
- [x] Define position, orientation, constraint-length, collision, and palette
  tolerances for SIMD and GPU comparisons.
- [ ] Add a targeted GPU test that exposes unordered parent/child updates and
  require the replacement kernel to pass it repeatedly across vendors.
- [x] Decide whether branch topology is required in the first optimized
  release or may initially use an explicit measured general kernel.
- [x] Decide which consumers truly require scene `Transform` mutation, which
  require delayed socket/bone values, and which can consume palette/bounds
  output directly.
- [x] Inventory all call sites that read chain transforms or particle state on
  the CPU after simulation.
- [ ] Set named-hardware physics and end-to-end frame budgets after the
  baseline is captured. Record absolute milliseconds plus scaling slopes; do
  not accept a percentage-only target.
- [ ] Record the CPU/GPU break-even point by chain length, collider class,
  active ratio, and rendering mode.

Acceptance criteria:

- [ ] Baseline results are reproducible and include end-to-end rendering cost,
  not just an isolated solver timer.
- [ ] Every later phase has an assigned metric and an approved regression
  threshold.
- [ ] Correctness tolerances, target hardware, strict-mode budgets, and
  quality-tier budgets are written down before implementation is declared
  successful.

## Phase 1 - Low-Risk Hot-Path Reductions

This phase reduces current-path waste while the world-owned runtime is being
built. Do not add abstractions here that make Phases 2-5 harder to complete.

### Component and CPU quick wins

- [x] Measure and remove duplicate transform initialization/preparation work.
- [x] Cache authored rest length, inverse rest length where useful, final force
  coefficients, topology depth, and other invariants at initialization or
  structural-change time.
- [x] Replace hot `Particle` property-notification writes with direct runtime
  state access as an interim step, while preserving authored/editor change
  notification outside the solver.
- [ ] Remove LINQ, captured closures, boxing, transient arrays/lists, and
  non-struct enumeration from preparation, solve, apply, and debug paths.
- [ ] Pre-size and reuse temporary collections by observed high-water marks.
- [x] Disable per-chain debug render objects and commands when debug
  visualization is not explicitly enabled.
- [ ] Aggregate profiler counters per worker/bucket and merge once; sample or
  compile out fine-grained telemetry in production profiles.
- [x] Replace repeated global update atomics with batch/range accounting where
  safe before full world ownership lands.

### GPU submission and shader quick wins

- [x] Version static topology, particles, transforms, and collider resources;
  skip snapshots/hashes/uploads when their version is unchanged.
- [x] Preserve stable request bucket membership across frames instead of
  sorting all active requests every frame.
- [x] Avoid combined/resident copies when a GPU-authoritative allocation and
  its topology are unchanged.
- [x] Move loop count, skip/update decision, and compatible feature flags into
  compact instance data where doing so reduces CPU group splitting.
- [ ] Remove or repurpose unused shader fields only after confirming host and
  shader layouts remain identical and tests cover the contract.
- [x] Use squared-distance collision rejection before square roots and
  precompute capsule direction/inverse-length terms.
- [x] Add specialized no-collider and small-collider paths before invoking the
  general collider loop.
- [x] Gather only explicitly requested particles/bones for asynchronous
  compatibility readback; stop copying an entire combined group for one
  request.

Acceptance criteria:

- [ ] Current architecture is measurably faster or neutral at every baseline
  scale and retains existing correctness tests.
- [ ] Steady-state allocations and unchanged-resource upload/copy bytes are
  reduced and reported.
- [ ] No interim work blocks replacement by the world, arenas, or specialized
  kernels.

## Phase 2 - Central `PhysicsChainWorld` And Stable Runtime Records

### Ownership and registration

- [x] Introduce one world/service owner for every active physics chain.
- [ ] Keep `PhysicsChainComponent` as an authoring/lifecycle facade that holds
  a generational runtime handle rather than per-particle solver state.
- [x] Replace per-component tick registration with one world-level input,
  simulation, and publication schedule integrated at explicit engine phases.
- [ ] Replace per-component render commands with one render-graph integration
  point for GPU work and global debug rendering.
- [x] Add lock-free or low-contention command buffers for registration,
  removal, template change, collider-set rebind, parameter update, root input,
  force/event input, visibility, quality, and readback requests.
- [x] Apply structural commands at a documented safe boundary and dynamic
  commands without rebuilding unrelated instances.
- [x] Validate handle generations on every external operation and reject stale
  handles deterministically.
- [x] Make activation/deactivation and component destruction safe while CPU or
  GPU work from a previous frame is in flight.

### Template and collider deduplication

- [x] Define immutable `PhysicsChainTemplate` data separate from instance
  state.
- [x] Deduplicate templates by stable content identity; avoid per-frame deep
  hashes of immutable arrays.
- [x] Precompute topology order, parent indices, depth ranges, rest data,
  coefficient packs, bone mappings, feature masks, and conservative influence
  bounds in each template.
- [x] Define explicit template rebuild/version behavior for editor changes.
- [x] Define shared, versioned `PhysicsChainColliderSet` resources with stable
  IDs instead of copying the same colliders into every chain.
- [x] Track collider transform/version dirtiness independently from collider
  topology or shape changes.

### Arenas and capacity

- [x] Add stable CPU and GPU arenas for instances, static templates, dynamic
  state, collider sets, roots/inputs, palettes, bounds, activity, and readback
  metadata.
- [x] Use free lists and generational slots; grow capacity geometrically or by
  measured size classes rather than exact-count reallocation.
- [x] Reuse the synchronization and frame-slot principles in
  `XRBufferPersistentRingAllocator` for tiny transient/dynamic header uploads
  rather than inventing per-frame buffer objects.
- [x] Distinguish arena capacity from live count and expose both.
- [x] Define fragmentation thresholds and an explicit out-of-band compaction
  or rebuild policy. Never move live GPU slices silently while consumers use
  them.
- [x] Preserve state across capacity growth and backend changes only when the
  transition contract explicitly supports it.
- [ ] Add capacity guards and delayed diagnostics for every arena writer.

### Backend-neutral output

- [x] Define a stable output record containing current and previous palette
  bases, palette count, bounds slot, validity, instance generation, and backend
  status.
- [ ] Make renderers bind output slices directly rather than discover results
  through mutated bone transforms.
- [x] Make CPU transform mirroring an opt-in output consumer with explicit
  cadence and cost.
- [ ] Define reset/teleport semantics for current/previous state and palettes
  so motion vectors never consume unrelated history.

Acceptance criteria:

- [x] A stress scene with 10,000 registered but sleeping/inactive chains has
  near-constant scheduling overhead relative to active work and no per-chain
  tick/render callbacks.
- [ ] Adding, removing, retemplating, and resizing chains cannot alias stale
  handles or corrupt another chain's state/output.
- [ ] Shared templates and collider sets consume memory proportional to unique
  content, not instance count.
- [x] Existing components can author and control chains through the new world
  without requiring renderers to read scene transforms.

## Phase 3 - Data-Oriented Multithreaded CPU Backend

### Runtime layout

- [x] Replace managed runtime `Particle` objects with unmanaged or blittable
  state owned by the CPU backend.
- [x] Separate static, input, dynamic, and output streams so a solve touches
  only required cache lines.
- [ ] Benchmark SoA and AoSoA layouts; select block widths that match supported
  SIMD paths while retaining a scalar tail.
- [ ] Bucket chains by segment count/topology/feature mask/collider class so
  hot loops avoid per-particle branches.
- [x] Use segment-major layout across a block of short chains so the same
  parent depth can be processed vector-wide.
- [ ] Keep frequently written state away from world metadata and pad worker
  counters/output boundaries to prevent false sharing.
- [x] Store compact parent/depth data for general topology without pointer
  chasing.

### Scheduler

- [x] Integrate with a persistent engine job system or implement a dedicated
  persistent worker pool; do not enqueue one ThreadPool work item per chain or
  allocate tasks each frame.
- [x] Partition by estimated particle/collider/substep work, not raw component
  count.
- [ ] Use coarse ranges large enough to amortize scheduling, then measured work
  stealing for imbalance caused by topology, colliders, and quality tiers.
- [ ] Parallelize root/input gathering, solve, collision, palette generation,
  bounds generation, and opt-in transform mirror publication.
- [x] Batch structural commands before workers begin and publish outputs after
  range completion without one atomic per chain.
- [x] Keep a deterministic scheduling/testing mode that produces stable
  reference ordering when needed.
- [ ] Measure Windows scheduler overhead before considering thread affinity,
  core-class awareness, or NUMA partitioning; keep those as explicit optional
  policies.

### Specialized solver kernels

- [x] Implement a scalar reference kernel using the new data contract.
- [x] Implement the common short linear-chain kernel with exact
  parent-before-child traversal and no virtual calls.
- [x] Vectorize across independent chains with `Vector256<float>`/AVX2 where
  supported and retain portable/scalar fallbacks.
- [ ] Evaluate AVX-512 only on hardware where frequency, register pressure,
  and downclock behavior produce a measured end-to-end win.
- [x] Precompute rest data and pack coefficients so the inner step performs no
  redundant square roots, matrix inversions, or authored-value conversion.
- [ ] Specialize kernels by collision class: none, very small fixed count,
  candidate-list, and general.
- [ ] Specialize optional features such as elasticity, stiffness, freeze axis,
  and branching only when feature masks demonstrate enough volume to justify
  a kernel variant.
- [ ] Implement a general depth-ordered branched kernel and measure it
  separately from the linear fast path.
- [ ] Evaluate an angular/joint representation for linear chains that preserves
  segment length by construction; adopt only if it matches authored behavior
  and beats positional correction end to end.
- [ ] Keep fixed-step accumulation, substep count, damping, gravity, external
  force, teleport/reset, and time-scale semantics explicit and shared with the
  GPU contract.
- [x] Ensure every inner loop is allocation-free and uses direct/ref access.

### CPU outputs

- [x] Generate current and previous skin palettes directly into world-owned
  contiguous output slices.
- [x] Generate conservative bounds from particles plus precomputed bone
  influence radii without mutating the transform hierarchy.
- [ ] Provide a vectorized/batched transform-mirror writer only for consumers
  that explicitly request it.
- [x] Skip palette, bounds, or mirror work when no registered consumer requires
  that output, while preserving visible-renderer correctness.

Acceptance criteria:

- [ ] The new CPU backend matches the scalar reference within approved
  tolerances across topology, collider, force, reset, and timestep tests.
- [x] The steady-state CPU backend allocates zero bytes and takes no global
  locks.
- [ ] Worker traces show useful parallelism across the full pipeline, not only
  the solver, with bounded imbalance at 1,000-10,000 chains.
- [ ] The common linear-chain AVX2 path demonstrates an approved throughput
  gain over scalar without regressing small-count latency beyond its threshold.
- [ ] The normal renderer path consumes CPU-generated palette/bounds slices
  without per-bone scene-transform publication.

## Phase 4 - Resident GPU Arenas And Backend-Neutral Submission

### Resource model

- [x] Define a narrow RHI-facing physics-chain compute interface for buffer
  allocation/binding, compute dispatch/indirect dispatch, barriers, timestamps,
  and optional async-copy/readback.
- [x] Implement the OpenGL 4.6 backend first without direct
  `OpenGLRenderer` casts in world-level physics logic.
- [x] Implement or document the Vulkan mapping using the same resource and
  synchronization contract; keep DX12 mapping explicit for later work.
- [x] Fail visibly when an explicitly selected GPU backend lacks a required
  capability. Do not silently run CPU simulation.
- [ ] Allocate permanent GPU arenas for template data, dynamic state,
  collider sets, roots/inputs, instance headers, active IDs, palettes, bounds,
  indirect commands, activity, and readback gather output.
- [ ] Use stable offsets/generations for every live slice and bind them through
  compact instance records.
- [x] Use geometric capacity growth and copy live state only during an
  explicit resize/rebuild event, never as ordinary steady-state submission.
- [x] Keep old resources alive until all frames that reference them have
  completed.

### Dirty updates and steady-state submission

- [x] Upload immutable template data once per template version.
- [x] Upload collider topology/shape data once per collider-set version and
  only dirty collider transforms thereafter.
- [ ] Upload compact per-instance dynamic headers/roots/forces through a
  persistently mapped multi-frame ring or equivalent backend facility.
- [x] Track dirty ranges or dirty IDs; do not snapshot and hash entire managed
  arrays each frame to discover changes.
- [x] Remove the resident-to-combined and combined-to-resident steady-state
  copy model.
- [x] Keep active instance IDs and indirect arguments GPU-authored when their
  inputs are already GPU-visible.
- [x] Ensure buffer counts and offsets required for current-frame dispatch are
  never read back to the CPU.
- [x] Keep resource bindings and pass-level command topology reusable across
  warm frames; rerecord/rebuild only for reported topology, capacity, pipeline,
  binding, or resource-generation changes.

### GPU active-work generation

- [x] Store per-instance enabled, visibility/relevance, sleep, quality tier,
  phase, loop count, feature mask, and bucket metadata in compact GPU-readable
  records.
- [x] Compact active chain IDs on the GPU with subgroup/workgroup prefix sums
  and one reservation atomic per group where supported.
- [x] Generate per-kernel/bucket indirect dispatch arguments on the GPU.
- [ ] Add capacity checks, clamped counts, overflow counters, and next-frame
  resize policy for active lists and indirect arguments.
- [x] Treat GPU-written counts as dynamic data, not a reason to rerecord stable
  pass topology.
- [x] Retain a portable fallback for missing subgroup arithmetic and label it
  in profiles.

Acceptance criteria:

- [x] An unchanged strict zero-readback frame uploads only expected dynamic
  headers/inputs and performs no whole-chain static snapshot/repack/copy.
- [ ] CPU submission time scales with dirty structural ranges and bucket/pass
  count, not total registered chain count.
- [x] Current-frame dispatch sizing, activity, palette, bounds, and rendering
  require zero CPU readback.
- [x] Every capacity failure or unsupported GPU capability is observable and
  cannot corrupt adjacent data.

## Phase 5 - Dependency-Correct GPU Solver Kernels

### Kernel family and scheduling

- [x] Replace the unsafe one-invocation-per-particle dependency pattern.
- [ ] Implement and benchmark a one-lane-per-short-linear-chain kernel where
  each lane advances segments sequentially in parent-before-child order and
  keeps immediate state in registers where practical.
- [ ] Implement and benchmark a workgroup-per-long-or-branched-chain kernel
  using precomputed depth ranges and explicit workgroup barriers between
  dependent depths.
- [ ] Evaluate a segment-major wave kernel across chains of equal/similar
  length; retain it only when dependency ordering and occupancy are both
  superior.
- [ ] Bucket by length/topology/feature/collider class to reduce divergence and
  select the measured kernel family.
- [x] Keep general/fallback kernels explicit in profiler labels and counters.
- [ ] Add empty/small-count handling so the high-throughput kernels do not
  impose disproportionate latency below their crossover.

### Substeps and barriers

- [ ] Move per-instance loop count and fixed-step accumulation into compact
  instance state.
- [x] Fuse multiple solver iterations/substeps inside a kernel when this
  removes global dispatch barriers and remains within register/watchdog limits.
- [ ] When fusion is not viable, batch all compatible chains per iteration and
  issue the narrowest required storage barrier between iterations.
- [x] Remove CPU dispatch-group splitting whose only purpose is a dynamic skip
  or loop count that the kernel/indirect active list can handle.
- [ ] Verify that all inter-pass state, indirect arguments, palette data, and
  bounds use backend-correct visibility barriers and no broader barriers than
  required.

### Bandwidth and arithmetic

- [ ] Split immutable template data from dynamic state and avoid reading fields
  unused by the selected kernel.
- [ ] Replace full 4x4 transform traffic with the smallest representation that
  preserves required precision, such as affine 3x4 or quaternion/translation
  inputs, after profiling conversion cost.
- [ ] Pack indices, flags, counts, and feature data to appropriate widths while
  keeping alignment explicit in shared C#/shader layout tests.
- [ ] Precompute rest lengths, inverse values, capsule terms, and coefficient
  combinations used by every step.
- [ ] Use reciprocal square root/refinement or reduced precision only after
  tolerance and visual tests approve the error on target vendors.
- [ ] Eliminate unused loads and stores and verify compiler output rather than
  relying only on source inspection.
- [ ] Measure register pressure, spills, occupancy, and memory bandwidth for
  every retained shader variant.

### Palette and bounds fusion decision

- [ ] Prototype writing palette and particle-derived bounds during the final
  simulation pass when state is already resident in registers/cache.
- [ ] Compare fusion with separate global batched palette/bounds passes; include
  register pressure, barriers, multi-consumer reuse, and renderer timing.
- [ ] Retain the lowest end-to-end cost per bucket, not necessarily one global
  strategy.
- [x] Ping-pong current/previous palette atlas roles per frame without copying
  unchanged history.

Acceptance criteria:

- [ ] Repeated cross-vendor stress runs show dependency-correct results within
  approved tolerances and no data races or NaN propagation.
- [x] Dispatches/barriers are bounded by active kernel buckets and necessary
  iteration boundaries, not chain count.
- [ ] Each retained kernel wins its documented chain-length/count/feature
  region and exposes fallback usage in telemetry.
- [ ] GPU stage timings meet the Phase 0 budget on named target hardware.

## Phase 6 - Collision Scaling

### Shared collider representation

- [x] Store colliders in shared versioned sets referenced by stable ID.
- [x] Use typed compact arrays or feature-specialized records so a particle
  does not branch through virtual collider objects.
- [x] Separate static shape data from dynamic pose data and update only dirty
  poses.
- [x] Precompute capsule direction, inverse length squared, radii combinations,
  plane terms, and other invariant shape data.
- [x] Deduplicate identical authored collider sets and report unique/live set
  counts and memory.

### Broadphase and narrowphase

- [x] Add a chain-level swept/conservative AABB test before particle-shape
  narrowphase.
- [x] Specialize zero-collider and fixed small-count collider kernels to avoid
  general list overhead.
- [x] Group chains sharing a collider set so CPU cache or GPU shared/cache data
  can be reused.
- [x] For larger sets, build a spatial hash, grid, BVH, or compact candidate
  list and benchmark build/refit versus query cost.
- [x] Keep broadphase generation CPU- or GPU-owned according to where collider
  poses originate; do not add readback merely to build candidates elsewhere.
- [x] Use squared rejection tests before expensive distance/square-root work.
- [ ] Sort or classify candidates by collider type only when it reduces total
  divergence/memory traffic.
- [x] Define maximum candidates, overflow behavior, conservative fallback, and
  diagnostics.
- [x] Validate fast motion, teleports, degenerate capsules, zero radius,
  coincident points, and large-coordinate scenes.

### Collision correctness and feature policy

- [x] Document whether chain particles push colliders, only receive collision,
  or interact with dynamic physics bodies through a separate impulse/event
  path.
- [x] Keep gameplay-relevant collision events separate from rendering-only
  simulation and define any delayed GPU event/readback behavior explicitly.
- [x] Define self-collision as unsupported, opt-in, or separately accelerated;
  do not let it silently enter the common kernel.
- [ ] Verify CPU/GPU contact normal, penetration correction, friction, and
  constraint behavior within approved tolerances.

Acceptance criteria:

- [x] Zero/small collider scenes pay no general broadphase or dynamic-dispatch
  tax.
- [x] Large collider-set cost scales with generated candidates rather than
  every particle times every collider.
- [x] Candidate overflow is conservative, visible in diagnostics, and memory
  safe.
- [ ] Shared sets measurably reduce upload bytes, memory, and cache/bandwidth
  cost in representative avatar crowds.

## Phase 7 - Palette, Bounds, Skinning, Culling, And Debug Rendering

### Global palette output

- [ ] Allocate stable current/previous palette atlas slices per live chain or
  renderer-compatible instance.
- [x] Expose palette base/count through the existing external skin-palette
  renderer contract instead of writing bone transforms first.
- [ ] Define sharing when multiple renderers consume one chain and avoid
  duplicate palette generation.
- [x] Handle partial palettes without falling back to one compute dispatch and
  barrier per renderer.
- [ ] Reset both palette histories correctly on spawn, teleport, template
  change, backend switch, and slot reuse.
- [x] Validate motion-vector consumers against the previous-palette contract.

### Skinning and draw submission

- [ ] Measure direct vertex-shader skinning versus globally batched compute
  skinning by mesh size, vertex reuse across passes, and renderer count.
- [ ] Use direct palette lookup for small/one-pass meshes where a compute
  pre-skin pass would cost more.
- [ ] Batch compute skinning globally for larger or multiply consumed meshes;
  do not issue a separate command/barrier per chain renderer.
- [ ] Instance identical mesh/material/pipeline combinations and provide a
  palette-base instance attribute or table lookup.
- [ ] Feed chain-driven renderers into GPUScene/indirect culling so thousands
  of chains do not imply thousands of CPU draw submissions.
- [ ] Report renderer, skinning dispatch, indirect command, draw, and triangle
  counts beside physics timing.

### GPU-owned bounds

- [ ] Derive conservative chain bounds directly from particles plus template
  influence radii, or from precomputed per-bone influence bounds transformed
  by the palette.
- [ ] Write bounds into stable GPU-visible slots consumed by GPUScene culling.
- [x] Avoid `WaitForGpu`, blocking maps, or bounds readback in production
  chain-renderer paths.
- [x] Retain explicit CPU bounds access only through delayed selective readback
  or a documented conservative CPU proxy.
- [ ] Validate fast motion, long interpolation intervals, teleport, sleep,
  offscreen wake, and previous/current-frame culling behavior.

### Debug rendering and inspection

- [ ] Replace per-chain debug render commands with one global compact debug
  vertex/instance buffer and indirect draw.
- [ ] Generate debug geometry only for explicitly selected/visible chains or a
  bounded sample.
- [ ] Keep debug readback, detailed per-chain telemetry, and validation scans
  out of production profiles.
- [ ] Add editor diagnostics for handle, template, bucket, state slice, quality
  tier, sleep state, palette slice, bounds, and last error without requiring a
  full synchronous GPU dump.

Acceptance criteria:

- [ ] End-to-end crowd profiles retain solver gains after palette, skinning,
  bounds, culling, and drawing are included.
- [ ] Identical chain-driven renderers batch/instance without one CPU submission
  or skinning barrier per renderer.
- [ ] Production visible chains require no CPU transform mirror or bounds
  readback.
- [ ] Debug visualization cost is bounded and disappears when disabled.

## Phase 8 - Sleep, Relevance, Update-Rate LOD, And Budgeting

### Activity and sleep

- [x] Define activity error from particle velocity, constraint error, root
  acceleration, collider motion, external force, and recent visibility/use.
- [x] Add sleep thresholds, minimum quiet duration, and hysteresis to prevent
  rapid sleep/wake oscillation.
- [x] Wake on root teleport/acceleration, collider-set or collider-pose change,
  force/event input, explicit gameplay request, visibility/relevance change,
  template/parameter change, and excessive accumulated error.
- [ ] Preserve current/previous output coherently while sleeping.
- [ ] Compute GPU activity and compact sleeping chains without reading activity
  back to the CPU.
- [x] Expose delayed aggregate active/sleep/wake counters and bounded selected
  instance inspection.

### Explicit quality tiers

- [x] Define named tiers such as full rate, 30 Hz, 15 Hz, 7.5 Hz, and sleep;
  store exact rate/substep/iteration/collision/palette policy rather than magic
  integers.
- [x] Provide a strict full-rate tier for gameplay-critical or captured
  deterministic scenarios.
- [x] Base automatic tier selection on distance, projected size, visibility,
  importance, recent interaction, and measured budget pressure.
- [x] Add independent policy controls for simulation, collision, palette, and
  bounds cadence only where interpolation/conservatism makes decoupling safe.
- [x] Phase-stagger lower-rate chains to flatten per-frame work.
- [x] Interpolate current/previous simulated outputs for rendering without
  changing physical elapsed time or accumulating timestep drift.
- [x] Add hysteresis and minimum residency time per tier.
- [x] Define offscreen behavior explicitly: simulate, decay then sleep, or
  sleep immediately depending on authored importance.
- [ ] Ensure GPU tier assignment and active compaction remain zero-readback.

### Budget controller

- [x] Add configurable CPU and GPU physics-chain budgets in milliseconds or
  normalized work units.
- [x] Use delayed timing/error data to adjust only chains authorized for
  automatic quality changes.
- [x] Cap quality changes per frame to avoid visible waves and oscillation.
- [x] Prefer sleeping irrelevant chains and reducing distant update cadence
  before reducing constraint/collision quality on important chains.
- [x] Surface requested versus effective tier, reason, and time in tier in
  profiler/editor diagnostics.
- [x] Provide a deterministic fixed-tier mode for tests and captures.

Acceptance criteria:

- [x] Tier transitions preserve elapsed-time behavior, current/previous output
  history, and bounded visual error.
- [x] Wake triggers prevent stale chains when roots, colliders, forces, or
  visibility change.
- [ ] Budgeted scenarios meet the approved frame budget without silently
  changing strict chains.
- [ ] Work scales with active tier-weighted chains, and phase staggering avoids
  periodic frame spikes.

## Phase 9 - Selective Readback And CPU Compatibility Mirrors

### Request contract

- [x] Inventory required CPU consumers and classify them as chain particles,
  specific bones, sockets, bounds, collision events, debug data, or full
  transform mirror.
- [x] Define asynchronous request handles with instance generation, requested
  fields, submission frame, expected earliest completion, and cancellation.
- [x] Document latency and freshness. Callers must never mistake delayed data
  for current-frame authoritative state.
- [x] Reject stale generation results after instance destruction or slot reuse.
- [x] Coalesce duplicate requests and cap per-frame requested elements/bytes.
- [x] Make overflow, expiry, timeout, or unavailable data explicit rather than
  blocking the render thread.

### GPU gather and transfer

- [x] Compact requested instance/output IDs into a small gather list.
- [x] Gather only requested particles, bone/socket matrices, bounds, or events
  into a tightly packed GPU transfer buffer.
- [x] Use a double/triple-buffered staging ring with fences and non-blocking
  polling.
- [x] Never copy or map a whole simulation arena/group because one chain was
  requested.
- [x] Schedule copy after the producing pass with the narrowest required
  synchronization and no current-frame wait.
- [x] Record requested, gathered, transferred, discarded-stale, and delivered
  element/byte counts plus latency histograms.

### Transform mirror

- [x] Keep full scene-transform mirroring disabled by default.
- [x] Allow selected chains to mirror at an explicit rate on a worker job after
  data becomes available.
- [x] Batch hierarchy updates and invalidation instead of one notification path
  per property/particle where transform semantics permit.
- [x] Make the cost and age of mirrored data visible to editor/gameplay code.

Acceptance criteria:

- [x] Strict zero-readback remains at zero current-frame bytes with no requests.
- [x] Sparse requests transfer bytes proportional to requested outputs and do
  not alter current-frame dispatch/render decisions.
- [x] No render-thread or simulation-thread blocking wait is reachable from
  production readback APIs.
- [x] Destroy/reuse/resize/backend-switch tests cannot deliver data to the wrong
  chain generation.

## Phase 10 - Tests, Profiling Gates, Migration, And Closeout

### Unit and contract tests

- [x] Add template deduplication/version/rebuild tests.
- [x] Add generational handle reuse and stale-command rejection tests.
- [x] Add arena allocation, free, fragmentation, growth, in-flight lifetime,
  overflow, and state-preservation tests.
- [x] Add structural command ordering tests for add/remove/retemplate/rebind
  during active frames.
- [x] Add scalar versus SIMD parity tests for all retained feature kernels.
- [ ] Add scalar versus GPU tolerance tests across chain lengths, topologies,
  colliders, substeps, forces, reset, teleport, and large coordinates.
- [x] Add repeated GPU parent/child dependency-ordering regression tests.
- [x] Add NaN/Inf containment and degenerate-input tests.
- [x] Add sleep/wake, quality transition, phase staggering, and deterministic
  fixed-tier tests.
- [x] Add current/previous palette history and motion-vector reset tests.
- [ ] Add GPU bounds conservatism and culling tests under motion/interpolation.
- [x] Add zero-readback source/runtime contract tests for strict GPU profiles.
- [x] Add selective readback ordering, byte-count, latency, stale generation,
  overflow, and cancellation tests.
- [x] Add backend capability/failure tests proving there is no silent CPU
  fallback.

### Performance gates

- [x] Extend the current benchmark controller or create a focused benchmark
  harness with warmup/settle, deterministic motion, automated matrix sweeps,
  raw capture, and summary output.
- [ ] Run the Phase 0 matrix after every major backend phase on the same named
  hardware and settings.
- [ ] Compare p50/p95/p99/max, scaling slope, per-active-chain/particle cost,
  upload/copy/readback bytes, dispatch/barrier counts, allocations, and memory.
- [x] Require three or more matched runs for acceptance and report variance.
- [ ] Capture at least one CPU profile for strict CPU, one GPU trace for strict
  zero-readback, and one end-to-end rendered crowd trace at target scale.
- [x] Verify no hidden setup, shader compilation, capacity growth, or upload
  backlog contaminates steady-state windows.
- [ ] Verify low-count latency remains within its approved threshold while
  high-count throughput improves.
- [ ] Verify disabled physics-chain systems have negligible frame cost.

### Migration and rollout

- [ ] Preserve the component's authoring/serialization surface where doing so
  does not compromise the clean runtime architecture.
- [ ] Record intentional pre-v1 API breaks and update all call sites in one
  coherent change.
- [ ] If serialized data must change, stop for owner approval and create a
  separate migration plan before editing assets or storage formats.
- [ ] Add an explicit backend/mode selector for old versus new runtime only
  while comparison is necessary; do not retain two permanent architectures.
- [x] Report selected CPU/GPU kernel family, backend, quality policy, and any
  explicit compatibility feature in diagnostics.
- [ ] Remove obsolete queues, component work-item orchestration, transient GPU
  repacking, unsafe shader kernels, and redundant transform paths after parity
  and rollout gates pass.
- [ ] Do not leave silent fallbacks to the removed paths.

### Documentation and evidence

- [x] Update the physics-chain performance guide with the final architecture,
  selection policy, profiling procedure, and tuning controls.
- [ ] Update the performance test plan with exact accepted hardware, scenes,
  commands, budgets, and final measurements.
- [x] Document authoring guidance for shared templates/collider sets, strict
  chains, automatic tiers, readback/socket requests, and debug inspection.
- [x] Document backend capability requirements and visible failure behavior.
- [ ] Record final before/after tables and trace paths in a durable progress
  note; ignored validation output is evidence, not the sole record.
- [ ] Mark this TODO complete only after all accepted deferrals have named
  follow-up trackers and owners.

Final acceptance criteria:

- [ ] Strict CPU and strict GPU results satisfy approved correctness tolerances
  and named-hardware performance budgets across the required matrix.
- [ ] The 10,000-chain target scenario meets its approved simulation and
  end-to-end frame budgets with p95/p99 evidence, not only average FPS.
- [ ] CPU steady state has zero managed allocations, no global lock, no
  per-chain scheduling objects, and no required transform hierarchy writes.
- [ ] GPU steady state has no static repack, no whole-state resident/transient
  copy, no current-frame readback, and no per-chain dispatch/render command.
- [ ] Collision cost is candidate-driven, palettes and bounds are direct
  outputs, renderer submission is batched, and debug work is globally bounded.
- [ ] Explicit LOD/sleep tiers and compatibility readback cannot silently alter
  strict behavior.
- [ ] Tests, profiles, documentation, and cleanup are complete.

## Experimental Optimization Ladder

These items are deliberately outside the required critical path. Promote one
only after the baseline architecture is complete and a named workload proves
the benefit.

- [ ] Autotune chain-per-lane, wave-per-depth, and workgroup-per-tree kernels by
  vendor, segment bucket, collider class, and active count; cache the selected
  variant without current-frame benchmarking.
- [ ] Evaluate 16-bit storage for indices, rest data, coefficients, or state
  subsets with explicit range/precision contracts and layout tests.
- [ ] Evaluate quaternion/dual-quaternion palette output versus affine 3x4
  matrices for bandwidth, shader cost, scale support, and renderer reuse.
- [ ] Evaluate shared-memory collider staging for chains grouped by collider
  set.
- [ ] Evaluate persistent GPU work queues or cooperative kernels only if normal
  indirect dispatch overhead remains material; account for watchdog and
  portability constraints.
- [ ] Evaluate Vulkan asynchronous compute only when timestamps demonstrate
  overlap with independent graphics work and no harmful queue/resource
  contention. OpenGL must not claim unsupported overlap.
- [ ] Evaluate GPU-driven quality assignment and broadphase entirely on the GPU
  when root/collider inputs already reside there.
- [ ] Evaluate CPU core-class-aware scheduling on hybrid processors and NUMA
  partitioning on high-end workstations.
- [ ] Evaluate fixed-function or approximate math variants only after visual,
  numerical, and cross-vendor acceptance.

Acceptance criteria:

- [ ] Every promoted experiment has an isolated before/after benchmark, a
  correctness result, a supported-hardware policy, and a fallback that is
  explicit rather than silent.
- [ ] Unpromoted experiments do not complicate the shipping runtime.

## Risk Register

| Risk | Required mitigation |
| --- | --- |
| One-lane-per-chain under-occupies the GPU at small counts or long chains | Maintain measured kernel buckets and low-count/general alternatives |
| Workgroup-per-tree consumes too much shared memory or registers | Track occupancy/spills; bound segment/depth classes and use a general fallback |
| Fused substeps exceed watchdog or register budgets | Cap fusion by measured variant and preserve batched multi-dispatch iteration |
| Mixed topology/features create excessive kernel fragmentation | Keep a compact feature taxonomy and send rare combinations to a labeled general kernel |
| Arena growth or compaction invalidates in-flight slices | Use generations, deferred resource lifetime, explicit rebuild boundaries, and tests |
| Template deduplication hashes cost more than they save | Compute identity on structural change, cache it, and never deep-hash per frame |
| Shared collider dependencies cause broad invalidation | Separate shape/topology versions from pose dirty ranges and group by set |
| GPU collision broadphase overflows | Clamp safely, emit a conservative result/fallback, resize next frame, and report it |
| Direct palettes break CPU attachments, sockets, gizmos, or tools | Inventory consumers and route only explicit needs through selective delayed mirrors |
| Bounds become too loose or cull visible motion | Precompute influence radii, account for interpolation/velocity, and validate conservatism |
| Multiple renderers or partial palettes reintroduce per-renderer dispatch | Use stable shared atlas slices and global batched remap/palette work |
| Update-rate LOD changes simulation character | Keep strict mode, preserve elapsed time, interpolate output, and bound error with tests |
| Sleep misses a root/collider/gameplay change | Centralize wake reasons, version inputs, add hysteresis and adversarial tests |
| Readback results attach to reused slots | Carry and validate instance generation through request, gather, staging, and delivery |
| Async compute competes with graphics instead of overlapping | Make it opt-in per backend and enable only from trace evidence |
| RHI abstraction hides required backend synchronization | Keep explicit resource/stage contracts and backend validation tests |
| Profiling overhead changes the result | Use pass-level timestamps, aggregate counters, delayed reads, and production-equivalent captures |

## Non-Goals

- Replacing the engine's general rigid-body solver or character controller.
- A broad ECS rewrite unrelated to physics-chain ownership.
- Silent visual-quality reduction to reach a headline chain count.
- Silent CPU fallback when a GPU path was explicitly requested.
- Current-frame full-state readback for editor convenience.
- Bit-identical CPU/GPU floating-point trajectories when documented tolerance
  and behavioral invariants are sufficient.
- Permanent support for both the current per-component runtime and the new
  world-owned runtime after migration.
- Micro-optimizing isolated arithmetic while component scheduling, transform
  hierarchy publication, GPU repacking, and renderer submission still dominate.

## Progress Recording Template

For each completed phase, append or link a durable progress note containing:

- Date, commit/branch, hardware, backend, build configuration, and exact scene.
- Changed architecture and any contract decision.
- Correctness tests and tolerances.
- Before/after p50/p95/p99/max and per-active-chain/particle scaling.
- CPU allocations/worker utilization and GPU dispatch/barrier/upload/copy/
  readback evidence.
- End-to-end palette/skinning/bounds/culling/draw impact.
- Regressions, unsupported cases, accepted deferrals, and the next gate.
