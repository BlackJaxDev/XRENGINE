# Physics Validation

Architecture: [Physics Architecture](../../../architecture/physics/overview.md), [Physics Debug Frame](../../../architecture/physics/physics-debug-frame.md), [Physics Chain World Runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Physics-chain compute backends](../../../architecture/physics/physics-chain-compute-backends.md), [Physics-chain output and readback](../../../architecture/physics/physics-chain-output-and-readback.md)
Code todos: [Physics Chain Thousands-Scale Optimization](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md), [Jolt Character Controller Correctness](../../todo/physics/jolt-character-controller-correctness-todo.md), [Box3D Backend Integration](../../todo/physics/box3d-backend-integration-todo.md)
Reference: [Physics-chain correctness contract](physics-chain-correctness-contract.md) (tolerances, topology decision, required matrix)

## Setup

- Physics debug unit tests: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~PhysicsDebug"`.
- Physics debug frame benchmarks: `pwsh .\Tools\Benchmarks\Measure-PhysicsDebugFrames.ps1 -Configuration Release`.
- Physics debug live preset matrix: `pwsh .\Tools\Benchmarks\Measure-PhysicsDebugVisualizationMatrix.ps1 -Configuration Release`.
- Physics debug RenderDoc captures: `pwsh .\Tools\RenderDoc\Capture-PhysicsDebugVisualization.ps1 -Configuration Release -Preset All`.
- `XRE_PHYSICS_DEBUG_PRESET` selects `Disabled`, `Shapes`, `Contacts`, `Joints`, `Simulation`, or `All` in the Unit Testing World. The matrix and RenderDoc scripts set and clear it.
- Physics chain benchmarks: Math Intersections World benchmark harness presets in an isolated Release editor (`Tools/Manage-McpEditorSession.ps1`). The required matrix is `PhysicsChainBenchmarkRequiredMatrix`. The scenario generator is `PhysicsChainBenchmarkDeterministicScenario`.
- Physics chain scale timing: `pwsh Tools/Benchmarks/Measure-PhysicsChainScale.ps1 -Session <name> -Label <label> -OutputFolder Build/_AgentValidation/<run>/reports`. Add `-UseExistingSession` to use a running owned session. Add `-Telemetry` for a separate diagnostic window. Add `-CaptureEvidence` to save wide and near captures after timing ends. View both PNG files. The script also runs in Windows PowerShell 5.1. It writes completed-frame rates, raw samples, absolute palette and readback counts, outcome changes, and telemetry differences. Its one-second samples do not establish a per-frame p95.
- Physics chain runtime metrics: `GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot()`. Allocation verification: the `Report-NewAllocations` task.
- Character controller tests: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~JoltControllerParityTests|FullyQualifiedName~CharacterMotion|FullyQualifiedName~CharacterMovement"`.

### Physics chain benchmark run controls

Every accepted physics chain run records and uses:

- a Release build with no debugger, validation layers, verbose logging, debug displays, editor inspection, or capture instrumentation;
- an explicit OpenGL or Vulkan backend, resolution, refresh rate, and VSync state;
- a separate profiler or capture run when hardware counters are necessary;
- a settle gate: chain counts, arena capacities, pipelines, uploads, and renderer counts stay stable before timing starts;
- at least 1,000 steady-state frames and 30 seconds after settle, with raw samples kept;
- three or more matched runs per accepted matrix point, with variance reported;
- separate cold-start, structural-churn, and steady-state records;
- unsupported backend points recorded with a reason, not dropped.

## Checks

### Physics debug visualization

- [ ] Run the `PhysicsDebug` unit tests. Procedure: the Setup command. Expected: all pass. Last evidence: none.
- [ ] Wide-view benchmark with `LitBoxBatchComponent`. Procedure: Physics Testing World, Vulkan editor, VSync off, camera at `(0,20,45)` looking at `(0,2.5,12)`. Record whole-frame, render CPU, command recording, draw count, and allocation deltas. Expected: compare with the earlier wireframe-batch result (median 11.82 ms, p95 14.81 ms). Last evidence: none.
- [ ] Frame benchmark at scale. Procedure: `Measure-PhysicsDebugFrames.ps1` at 10K, 100K, and 1M primitives with line-heavy and triangle-heavy mixes. Expected: extraction plus upload median is at most `1.10x` the allocation-free debug baseline and p95 at most `1.20x`. Last evidence: none.
- [ ] Steady-state allocation. Procedure: the frame benchmark after warmup. Expected: `0 B` managed allocation per frame. Last evidence: none.
- [ ] Multi-view upload. Procedure: one mono view, two editor viewports, and stereo XR. Expected: extraction and upload happen at most once per published generation. Last evidence: none.
- [ ] PhysX batch path review. Procedure: profile and inspect the PhysX debug batch path. Expected: no per-primitive interface or delegate dispatch, `ConcurrentBag` insertion, `Task.Run`, or blocking task wait. Last evidence: none.
- [ ] Live preset matrix. Procedure: `Measure-PhysicsDebugVisualizationMatrix.ps1` for `Shapes`, `Contacts` (with normals), `Joints` (frames and limits), `Simulation` (meshes and collision edges), and `All`. Expected: at most three physics debug draw calls per world and view (points, lines, triangles), and no triangle-to-line expansion unless the mode requests it. Last evidence: none.
- [ ] RenderDoc capture. Procedure: `Capture-PhysicsDebugVisualization.ps1` on OpenGL and Vulkan. Inspect draw count, uploaded resources, target contents, and GPU timing. Expected: colors, depth behavior, and overlay semantics match the earlier output. Last evidence: none.
- [ ] Depth testing. Procedure: place diagnostics in front of and behind opaque meshes. Expected: depth-tested diagnostics occlude correctly. On-top diagnostics stay opt-in. Last evidence: none.
- [ ] Overlay use. Procedure: inspect scene fixtures. Expected: no fixture uses the late unlit overlay to get batching. Last evidence: none.
- [ ] Retained batch lifecycle. Procedure: save the scene, enter Play mode, and replace the world. Expected: retained diagnostic and fixture batches keep their logical entries. Only runtime CPU and GPU resources rebuild. Last evidence: none.
- [ ] Debug budget. Procedure: set a small `PhysicsDebugBudget`. Expected: logs and profiler telemetry show dropped primitive counts. Last evidence: none.

### GPU skinned chain scale

Scene: Math Intersections world, `Physics Chain GPU Dispatcher Skinned Mesh Test`, isolated Release editor. [Design](../../design/physics/physics-chain-steady-state-cpu-design.md) · [Investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) · [Performance guide](../../../developer-guides/rendering/physics-chain-performance.md#gpu-skinned-scale-measurement). Keep laptop (Core Ultra 9 185H, RTX 4070 Laptop GPU) and desktop (Ryzen 9 7950X3D, RTX 3090) results separate.

Common procedure: `pwsh Tools/Benchmarks/Measure-PhysicsChainScale.ps1 -Session <name> -ChainCount 2000 -WindowSeconds 30 -OutputFolder Build/_AgentValidation/<run>/reports`. The harness starts the benchmark, waits for 30 clean completed frames, uses the full-grid camera, checks the frozen strict strategy (value `2`), and rejects changed chain counts, new bad frame outcomes, input-page failures, input-buffer growth, and physics readback. For a manual run: activate the test node, set `_benchmarkCopyCount` and `_benchmarkDurationSeconds` on the `MathIntersectionsWorldControllerComponent`, call `SetBenchmarkRunToggle(true, false)`, and set MCP dispatch to `MainThread` first. Acceptance windows run with `XRE_WORLD_TICK_TELEMETRY=0`, the four Debug observer preferences off, and RenderDoc off.

- [ ] Frame rate at 2,000 chains, shared mesh. Expected: 100 or more completed frames per second and frame-interval p95 at most 10 ms. Record hardware, clocks, resolution, and observer flags. Last evidence: 2026-10-08 desktop, 23.6–25.3 Hz with p95 46–49 ms; laptop 16–22 Hz, and 22.0–24.7 Hz with p95 47–55 ms after the committed bound version and proxy. Failed.
- [ ] Frame rate at 2,000 chains, unique meshes. Procedure: the common procedure with `-MeshSharing Unique`. The harness rejects the window when the indexed instance-group count (`meshSharingEvidence`) is less than the chain count. Expected: the same targets. Last evidence: 2026-10-08 laptop, 2,000 groups for 2,000 payloads; 21.0–24.8 Hz with p95 46–62 ms, the same as shared meshes. Failed.
- [x] Native mesh readiness. Expected: at least 30 completed frames with no new deferred, rejected, or failed outcomes before timing. Last evidence: 2026-10-07, three clean windows.
- [ ] Binding counts. Expected: 2,000 palette bindings, 2,000 deformation jobs, 208,000 vertices, no CPU fallback, and zero physics readback before and after timing. Last evidence: chains, bindings, fallback, and readback confirmed; job and vertex counts not yet read.
- [x] Functional counter lifecycle. Expected: three runs keep 2,000 chains and bindings, then restore one chain, one palette slice, and one binding. Last evidence: 2026-10-07, three clean cycles.
- [ ] Start and stop cycles. Procedure: three cycles on the final binary; view near and wide images; wait for gradual teardown. Expected: one source chain, palette slice, and binding after each stop, with continuing source animation. Last evidence: windows pass, but one run hit `ResourceGenerationBlocked` and one start request timed out. See the [open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults).
- [ ] Canonical bounds and shadow admission. Procedure: move GPU-deformed chains across view and cascade boundaries; include partial coverage, disabled shadow casting, and layer exclusions. Expected: no stale-bound rejection, no policy bypass, zero readback. Last evidence: wide and near native frames hold all 416,000 current and previous positions inside their routed bounds, with 2,000 main and 8,000 shadow draws. Partial coverage, boundary motion, and shadow exclusion remain open.
- [ ] Bounds route lifetime. Procedure: removal, command compaction, slot reuse, and arena growth with frames in flight. Expected: correct draw generation and candidate index; no read of another chain's bounds or retired storage. Last evidence: 64-, 2,000-, and 96-chain restart windows pass. Failure handling and backend replacement remain open.
- [ ] Material vertex effects. Procedure: start with `XRE_PHYSICS_CHAIN_TEST_VERTEX_EFFECTS=1`; change translation, rotation, and command overrides with `set_material_uniforms`; read `GpuBoundsDiagnostics`; inspect a trigger capture. Expected: translation stays inside the padded bound; scale, rotation, look-at, and barrel effects reject the slot with a counted diagnostic. Last evidence: strict one-chain checks pass for translation, rotation, neutral override, rotated override, and recovery. Other consumers and 2,000-chain timing remain open.
- [ ] Previous deformation history. Procedure: force a failed aggregate submission, skip preparation frames, reuse a preparation within a frame, and publish several physics pages between preparations. Expected: failed output is never valid history; previous vertices fit the current bound; no CPU completion wait. Last evidence: guards are present and 64-chain native frames pass containment. Fault injection remains open.
- [ ] OpenGL bounds execution. Expected: the same route and lifetime contract as Vulkan. Last evidence: builds only.
- [ ] Capture lifecycle. Procedure: `RenderDocCaptureBridge.TryTriggerCapture` for presented frames, then `get_time_state`. Expected: capture changes cannot interrupt native recording. Last evidence: trigger captures pass. Explicit start/end still faults in `renderdoc.dll`.
- [ ] Strict GPU-indirect submission. Expected: visible animated meshes without CPU fallback or downgrade. Last evidence: 64-chain native captures use `vkCmdDrawIndexedIndirectCount`; 64- and 2,000-chain windows keep strategy `2` with moving shadows. Native cascade capture and failure injection remain open.
- [ ] Solver group size. Procedure: compare 32-, 64-, and 128-thread short-linear groups under controlled clocks. Expected: the argument builder uses the same size and every chain updates. Last evidence: the 64- and 128-thread windows were invalid (input-page fence failures); 32 threads stay.

### Bottleneck measurements

Use these checks to choose and confirm the [design](../../design/physics/physics-chain-steady-state-cpu-design.md#measurement-approach) work. Controlled conditions for every check here: lock the GPU clocks (`nvidia-smi -lgc` from an elevated shell, or the driver's maximum-performance mode), use the High Performance power plan, and on the Ryzen 9 7950X3D pin the editor process to one CCD and record the mask. Run WPR from an elevated shell for CPU scheduling and stacks.

- [x] Three-mode ablation. Procedure: with 2,000 chains, compare the full scene, chain rendering hidden with physics active, and physics callbacks frozen with rendering active. Last evidence: 2026-10-07, three cycles: 16.3–19.2, 52.0–58.9, and 13.0–25.0 Hz. See the [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#ablations).
- [ ] Controlled timing and critical path. Procedure: engine-side timestamped windows under the controlled conditions, plus a WPR trace. Expected: bounded skew, explained clock variation, and the critical thread and its waits identified. Last evidence: scope timelines only; WPR failed without elevation; clocks were not controlled.
- [ ] Cost per chain for each frame phase. Procedure: the harness chain-count ladder (1, 250, 500, 1,000, 2,000) in both mesh modes. Expected: a fixed cost and a cost per chain for update, collect, swap, and render; the critical-path phases together at most about 2.5 µs per chain. Procedure detail: `-Ladder` with the default counts; add `-CpuAffinityMask` on the desktop. Last evidence: 2026-10-08 laptop, shared mesh only, uncontrolled clocks: the mean frame interval grows by 19.95 µs per chain (R² 0.985), and render plus swap by about 19.7 µs. See the [ladder results](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#chain-count-ladder). The unique-mesh run and controlled conditions remain open.
- [ ] Frozen-scene rendering ceiling. Procedure: set the ceiling flag after settle, physics active, in both mesh modes; compare with the full and hidden-rendering windows. Expected: a rate near the hidden-rendering rate shows that the change-driven renderer path can remove the rendering share. Last evidence: none.
- [ ] Root-only physics ceiling. Procedure: set the ceiling flag with chain rendering hidden, then shown. Expected: the rate and phase costs against the hidden-rendering control. Last evidence: none.
- [ ] Preparation lock waits. Procedure: read the acquire and copy lock-wait counters from `get_advanced_profile_diagnostics` in full and frozen-physics windows. Expected: show whether the rise of `Advanced.VisibilityPreparation` from 6 to 19 ms in frozen mode is lock waiting. Last evidence: none.
- [ ] Row-pattern microbenchmark on the desktop. Procedure: move the row-pattern benchmark into `XREngine.Benchmarks`, then run it pinned to one CCD. Expected: the same order of ratios as the [laptop results](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#row-pattern-microbenchmark). Last evidence: laptop only, 2026-10-08.

### Rigid rest input cache

- [x] Input equivalence and authoring cases. Expected: cached and ordinary inputs match within float tolerance; child edits block reuse until rearm; unsupported hierarchy changes reject capture. Last evidence: 2026-10-07; all 2,000 ranges and 14,000 matrices matched (maximum difference 1.5e-5); child scale, order, translation, rotation, and reparent cases behaved as expected.
- [ ] Retirement and scale. Procedure: repeated Strict 2,000-chain cache-off/cache-on windows and three restarts. Expected: lower total cost, zero readback, no warm allocations, no retained child listeners. Last evidence: gathering fell from 7.2–9.2 to 5.9–6.2 ms per late tick; retirement removed all 12,000 child registrations. Observer-free timing and the full matrix remain open.

### Distance quality and GPU presentation

The user permits a lower simulation cadence for distant chains. Keep the full-rate profile as the reference. A quality-tiered result must name the distance thresholds, physical step rate, submission rates, interpolation delay, camera path, and tier distribution, and must still meet the 2,000-chain target. See the [distance cadence design](../../design/physics/distance-cadence-gpu-presentation.md).

- [ ] No-solve admission. Procedure: compare an explicit reduced tier before and after the change, with world telemetry, plus the Strict profile. Expected: skipped solves avoid rest capture and preparation; root movement is kept; the clock advances once. Last evidence: the first prototype was rejected; the 15 Hz route still spends about 7.3 ms gathering rest inputs.
- [ ] Physical time and admission. Procedure: compare 60/30/15 Hz submission schedules over equal simulated time, including delayed and rejected native frames. Expected: authored steps and cumulative time stay consistent. Last evidence: none.
- [ ] Continuous presentation. Procedure: record motion while the camera crosses each threshold in both directions, with a fast approach, a stalled solve, reset, teleport, and three restarts. Expected: no visible pose step; meshes move on frames without a new solve; simulation and render history stay independent. Last evidence: the GPU palette reads only the latest solve.
- [ ] Presentation bounds and view relevance. Procedure: compare deformed vertices with CPU and GPU bounds during root translation and rate changes, with several color views and directional shadows. Expected: bounds cover the displayed mesh; only color views control quality. Last evidence: none.

### Vulkan asynchronous physics experiment

Start only after the CPU frame meets its budget. Use the [research constraints](../../../developer-guides/rendering/physics-chain-performance.md#vulkan-asynchronous-compute-research)
and [open implementation items](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#vulkan-asynchronous-submission-experiment).
The existing presentationless classification split is a separate control; it
does not move the physics solver to another queue.

Physical availability recorded on 2026-10-07 for the RTX 4070 Laptop GPU:

| Queue family | Queue count | Capabilities | Timestamp valid bits |
| --- | --- | --- | --- |
| 0 | 16 | Graphics, compute, transfer | 64 |
| 1 | 2 | Transfer | 64 |
| 2 | 8 | Compute, transfer | 64 |

`vulkaninfo` also reports timeline semaphore and synchronization2 support.
These are physical-device facts. Confirm enabled logical-device features,
selected family/index pairs, and distinct queue handles separately. None of
these values proves hardware overlap.

- [ ] Queue and execution admission. Procedure: record enabled synchronization features, selected queue family/index/handle values, requested and executed modes, and accepted native submission counts. First compare the existing presentationless classification split with `GraphicsOnly`; label it as a control, not a physics result. Expected: no alias presented as an independent queue and no silent change of requested execution. Last evidence: physical availability above; no async-physics execution evidence.
- [ ] Independent work and same-frame correctness. Procedure: before a physics split, identify the exact read/write resources of each branch. Compare native current/previous member positions, bounds, visible member counts, moving shadows, reset, teleport, and simulation cadence with the graphics-only path. Expected: the join precedes every dependent consumer, all 2,000 chains remain present and animated, output latency is unchanged, and solved physics readback remains zero. Last evidence: none for async physics.
- [ ] Overlap and end-to-end cost. Procedure: use matched full-grid cameras, resolution, quality, chain count, clocks, and observer settings. Collect completed per-queue timestamp ranges, CPU recording/submission cost, and a GPU scheduling trace in diagnostic windows. Then disable observers and compare repeated completed-frame Hz and interval p95 windows. Expected: report actual overlap or serialization, added submission/wait cost, and total-frame changes without treating queue count, policy counters, CPU worker overlap, or pending-fence evidence as hardware overlap. Read timestamp results asynchronously without blocking. Last evidence: no matched async-physics comparison.
- [ ] Split-submission lifetime. Procedure: exercise partial submission rejection, stop/restart, arena growth, source removal/replacement, and teardown with both queues in flight. Expected: accepted work keeps its resources and query ranges until completion; rejected work cannot publish valid output or prematurely recycle a frame slot; no deadlock, stale generation, or hidden CPU fallback occurs. Last evidence: none for async physics.

### Physics chain scale

Canonical submission checks:

- [ ] Retained registration identities and source groups. Procedure: read the identity and group reuse and rebuild counters in `get_render_state`; run scale, primitive growth and shrink, removal, reactivation, reorder, source replacement, compaction, and failed identity delivery. Expected: stable rows reuse exact draw generations and groups; changes rebuild safely; removed sources receive cleared identities. Last evidence: 2026-10-08 desktop windows reuse 2,002 rows and groups with zero rebuilds; primitive, removal, reactivation, and material override checks pass; 20 publisher contract tests pass. Manual reorder, compaction, and renderer replacement remain open.
- [ ] Indexed instance groups. Procedure: strict near and wide trigger captures; include partial visibility, distinct compatible sources, material changes, authored multi-instance draws, late recovery, and OpenGL stereo. Expected: one argument per nonempty group and independent member state. Last evidence: 64-chain captures show one draw with 64 and 25 unique members; 2,000-chain captures show one group with 2,000 unique members. Additional cases remain open.
- [ ] Frozen consumer strategy. Procedure: switch CpuDirect and strict submission, then use two consumers with different strategies in one frame. Expected: each package keeps its captured lane; shared deformation runs once. Last evidence: 64-chain samples pass; the multi-consumer matrix remains open.
- [ ] Incremental covered plans. Procedure: read `ReusedStaticDrawPlanCount` and `MaterialResolveAttemptCount` during root motion, then edit material parameters, an override, and geometry. Expected: root motion reuses plans; each source edit invalidates its plan. Last evidence: 2,000 retained plans with zero material resolution; source-edit cases remain open.
- [ ] Directional GPU shadow cache and policy. Procedure: `-RequireDirectionalShadows`; move casters, apply overrides, exclude caster layers, use several consumers. Expected: new output dirties matching cascades; held pages keep the cache; no generic fallback. Last evidence: accepted groups advance at 64 and 2,000 chains; native and policy checks remain open.
- [x] Normal world GPU-input capture and parent guards. Expected: normal inputs use the world range without per-particle hierarchy refresh. Last evidence: 2026-10-07; 183 baseline and 91 restored ticks with zero rest-reset, hierarchy-refresh, and particle-read deltas.
- [ ] World and hierarchy ownership. Procedure: world detach and reattach, parent move, activation changes, transfer between two ticking worlds, stopped-source transfer. Expected: no stale handle or deadlock. Last evidence: one-chain cases pass; two-world and stopped-source cases remain open.
- [ ] Selective solved-pose readback. Procedure: request particles, bones, sockets, and full transforms with translated roots, affine inputs, and a held chain. Expected: bounded delayed data and correct 48-byte affine rows. Last evidence: particle-and-bone requests return 216 bytes in three frames with exact translations; socket, full-transform, and held cases remain open.
- [ ] Selective transfer lifetime. Procedure: two-world requests, cancellation, expiry, reset, template change, renderer replacement, partial enqueue failure, device loss. Expected: no stale delivery or slot overwrite. Last evidence: cancellation and release pass; the native failure matrix remains open.
- [ ] Global GPU chain debug. Procedure: debug off, one selected chain, a sample, two views, selection changes, capacity growth, and teardown during deferred work. Expected: at most one batch per frame, two point-topology indirect draws, zero debug work when disabled. Last evidence: native replay shows one compute dispatch and two indirect draws; three off/on cycles pass. Two-view, capacity, teardown, and failure cases remain open.

Run the full `PhysicsChainBenchmarkRequiredMatrix`. Do not replace it with a hand-picked subset.

| Dimension | Values |
| --- | --- |
| Chain count | 100, 500, 1,000, 2,000, 5,000, 10,000 |
| Dynamic segments per chain | 4, 8, 16, 32 |
| Topology | linear, branched |
| Colliders | none, two simple shared, five mixed, large set needing broadphase |
| Collider ownership | shared, unique |
| Activity | 100%, 50%, 10% active, sleeping or offscreen heavy |
| Rendering | none, palette and bounds only, identical instanced meshes, diverse skinned renderers |
| Mode | strict CPU, strict GPU zero-readback, quality-tiered CPU, quality-tiered GPU |
| Readback | disabled, sparse sockets, sparse whole chains, diagnostic full sync |
| Backend | OpenGL, Vulkan |
| Fixed simulation rate | 30, 60, 90, 120 Hz |

Scenario presets in the Math Intersections harness: CPU single-thread, CPU multithread, standalone GPU, batched GPU, no-collider, heavy-collider, skinned mesh with `GpuSyncToBones` off, and skinned mesh with `GpuSyncToBones` on as the compatibility baseline.

Metrics for every accepted point:

- whole-frame CPU and GPU p50, p95, p99, and maximum;
- simulation cost per active chain and per active particle;
- CPU time for structural commands, input gathering, scheduling, solve, collision, palette, bounds, publication, renderer submission, and compatibility synchronization;
- CPU worker utilization, work imbalance, queue latency, context switches, and lock or fence wait time;
- managed allocations per frame, GC collections, and boxing, closure, or LINQ evidence;
- GPU timestamps for active-list generation, simulation, collision, palette, bounds, skinning, culling, and draws;
- dispatch count, workgroups, active lanes, barriers by kind, copy, upload, and readback bytes, and fence waits;
- active, rate-limited, sleeping, culled, and woken chain counts;
- palette dispatches, skinning dispatches, renderer and draw counts, and bounds and culling cost;
- arena capacity, live use, fragmentation, growth count, and bytes by resource.

Checks:

- [ ] Baseline matrix capture. Procedure: the run controls above on the primary machine. Expected: reproducible results that include end-to-end rendering, not only a solver timer. Last evidence: none.
- [ ] Repeat the matrix after each major backend change on the same hardware and settings. Expected: compare p50, p95, p99, maximum, scaling slope, per-chain and per-particle cost, bytes, dispatch and barrier counts, allocations, and memory. Last evidence: none.
- [ ] Hardware counters. Procedure: one high-count profile with the best available CPU and GPU tools. Expected: CPU cache misses, branch misses, memory bandwidth, and SIMD width; GPU occupancy, register pressure and spills, shared-memory use, cache behavior, and bandwidth for each retained shader variant. Last evidence: none.
- [ ] Profiles at target scale. Expected: one CPU profile for strict CPU, one GPU trace for strict zero-readback, and one end-to-end rendered crowd trace. Last evidence: none.
- [ ] CPU/GPU break-even. Expected: recorded by chain length, collider class, active ratio, and rendering mode. Last evidence: none.
- [ ] CPU parallelism. Expected: worker traces show useful parallelism across the full pipeline with bounded imbalance at 1,000 to 10,000 chains. Last evidence: none.
- [ ] Windows scheduler overhead. Expected: measured before thread affinity, core-class, or NUMA policies are considered. Last evidence: none.
- [ ] AVX2 gain. Expected: the linear AVX2 path beats scalar by the approved amount without exceeding the low-count latency threshold. Last evidence: none.
- [ ] SoA versus AoSoA. Expected: the selected layout and block width win on the benchmark. Last evidence: none.
- [ ] CPU renderer path. Expected: the normal renderer consumes CPU palette and bounds slices without per-bone scene-transform publication. Last evidence: none.
- [ ] CPU submission scaling. Expected: submission time scales with dirty structural ranges and bucket count, not total registered chains. Last evidence: none.
- [ ] Interim hot-path reductions. Expected: faster or neutral at every baseline scale, with fewer steady-state allocations and fewer unchanged-resource upload and copy bytes. Last evidence: none.
- [ ] Handle and slot safety under churn. Procedure: structural-churn records with add, remove, retemplate, and resize. Expected: no stale handle alias and no corruption of another chain's state or output. Last evidence: none.
- [ ] Shared data memory. Expected: templates and collider sets use memory in proportion to unique content, not instance count. Shared sets reduce upload bytes, memory, and bandwidth in avatar crowds. Last evidence: none.
- [ ] GPU kernel correctness across vendors. Procedure: repeated stress runs and the dependency-ordering tests on each target vendor. Expected: results within the contract tolerances, with no data race or NaN. Last evidence: none.
- [ ] Existing GPU kernel families. Procedure: validate the short-linear and long/branched live paths, then run the existing `PhysicsChainGpuDependencyOrderingTests`, `PhysicsChainGpuBranchedDependencyOrderingTests`, `PhysicsChainGpuKernelFamilyTests`, and `PhysicsChainShaderContractTests`. Include mixed loop counts in one compatible batch. Expected: parent-before-child results, matching layouts, and correct substeps without grouping by loop count. Last evidence: source audit confirms both kernels, depth barriers, separate state/template buffers, and fused substeps; these tests were not run in this audit.
- [ ] Retained GPU kernel regions. Expected: each retained kernel wins its chain-length, count, and feature region. Telemetry shows fallback use. Last evidence: none.
- [ ] GPU barriers. Procedure: RenderDoc or backend validation. Expected: inter-pass state, indirect arguments, palettes, and bounds use correct, narrow visibility barriers. Last evidence: none.
- [ ] Shader compiler output. Expected: no unused loads or stores in retained variants. Last evidence: none.
- [ ] Palette and bounds fusion. Expected: fused versus separate passes compared on register pressure, barriers, multi-consumer reuse, and renderer timing. Keep the lowest end-to-end cost per bucket. Last evidence: none.
- [ ] Skinning strategy. Expected: direct vertex-shader skinning versus batched compute skinning measured by mesh size, vertex reuse across passes, and renderer count. Last evidence: none.
- [ ] End-to-end crowd. Expected: solver gains remain after palette, skinning, bounds, culling, and draws. Identical renderers batch without per-renderer submission or skinning barrier. Production visible chains need no CPU mirror or bounds readback. Last evidence: none.
- [ ] Bounds behavior. Expected: no visible culling errors with fast motion, long interpolation intervals, teleport, sleep, and offscreen wake. Last evidence: none.
- [ ] Debug cost. Expected: debug visualization cost is bounded and zero when disabled. Last evidence: none.
- [ ] Budgeted scenes. Expected: the approved frame budget is met without changing strict chains. Work scales with tier-weighted active chains. Phase staggering prevents periodic spikes. Last evidence: none.
- [ ] Zero-readback path. Expected: the default visible GPU path has zero same-frame readback in representative scenes. Readback bandwidth and stalls are near zero. CPU bone sync runs only on request. Debug and editor tools do not force visible-path readback. Last evidence: none.
- [ ] Low-count latency. Expected: within its approved threshold while high-count throughput improves. Last evidence: none.
- [ ] Disabled system cost. Expected: negligible frame cost with physics chains disabled. Last evidence: none.
- [ ] Final acceptance. Expected: strict CPU and GPU meet the correctness tolerances and named-hardware budgets across the matrix. The 10,000-chain scenario meets its simulation and end-to-end budgets with p95 and p99 evidence. Before and after tables are recorded in the investigation. Last evidence: none.

### Character controller

- [ ] Baseline traces. Procedure: Jolt and PhysX traces for level walking, acceleration, stopping, falling, jumping, a walkable slope, a steep slope, and a step at 30, 60, 120, and 144 Hz Update with 60 Hz physics. Record requested and effective velocity, position, support state, contact flags, ground normal, and ground velocity per step. Expected: the traces show whether the old Update-rate drift and zero-input gap existed. Last evidence: none (skipped by owner request on 2026-07-13).
- [ ] Focused tests. Procedure: the character controller test filter in Setup, then the full `XREngine.UnitTests/Physics` namespace. Expected: all pass. Last evidence: none.
- [ ] Builds. Procedure: build `XREngine.UnitTests` and `XREngine.Editor`. Expected: zero new warnings. Last evidence: none.
- [ ] Final traces. Procedure: repeat the baseline trace matrix on the final code. Expected: distance, acceleration, braking, fall speed, and jump do not change with Update cadence in either input model or `TickInputWithPhysics` mode. Write a short before and after report. Last evidence: none.
- [ ] Allocation and lifetime. Procedure: profile fixed-step updates and repeat scene initialize, create, step, and destroy. Expected: no hot-path heap allocation, no unbounded queue or registry growth, and controller, listener, character-collision, and inner-body counts return to zero. Last evidence: none.

### Box3D backend

- [ ] Upstream tests. Procedure: build the pinned source with upstream unit tests on in a separate build. Expected: all pass. Last evidence: none.
- [ ] Native artifact. Procedure: inspect the shipping DLL. Expected: exported C symbols, calling convention, CRT, native file name, and transitive DLL dependencies match the bindings. Last evidence: none.
- [ ] Interop spike. Procedure: create a world, a ground box, and a falling box. Step 120 frames, read movement events, run one callback query and one debug-draw callback, then destroy all. Expected: no leak or fault. Last evidence: none.
- [ ] Native lifecycle. Procedure: repeat the lifecycle and callback tests in Debug and Release x64. Expected: stable passes. Last evidence: none.
- [ ] Backend regression. Procedure: targeted PhysX and Jolt suites after shared contract changes. Build `XREngine.Runtime.Core`, `XREngine.Editor`, and the affected runtime projects. Expected: pass. Last evidence: none.
- [ ] Editor session. Procedure: isolated Unit Testing World session with Box3D. Capture and view physics-debug images from several cameras. Read the session logs. Expected: correct debug output and no native warnings. Last evidence: none.
- [ ] Publish layout. Procedure: inspect editor build and publish outputs. Expected: the Box3D DLL is present. Last evidence: none.
- [ ] Determinism. Procedure: compare state hashes across repeated runs and accepted worker counts on one platform and build. Expected: equal hashes. Last evidence: none.
- [ ] Worker policy. Procedure: single-worker baseline, then the Box3D internal scheduler on performance cores. Expected: the accepted policy improves a named workload with no correctness or frame-tail regression. Last evidence: none.
- [ ] Solver benchmarks. Procedure: matched Box3D, Jolt, and PhysX scenes for falling stacks and piles, sleeping and waking, fast CCD bodies, many static meshes and height fields, ray, sweep, and overlap batches, joint chains and ragdolls, and character movers if promoted. Expected: record fixed-step p50, p95, p99, and maximum, body, shape, contact, and island counts, allocations, native bytes, worker use, and publication cost. Steady-state stepping allocates nothing. Last evidence: none.

Box3D minimum case matrix:

| Area | Minimum cases |
| --- | --- |
| Lifecycle | empty and populated world, failed init, repeated reload, teardown with live bodies and joints |
| Bodies | static, kinematic, dynamic, sleep and wake, gravity off, locks, CCD, teleport, velocity |
| Geometry | sphere, box, capsule, convex, multi-shape, static mesh, static height field, invalid plane and dynamic concave |
| Filtering | each layer, masks, static-only, dynamic-only, all, runtime filter change |
| Queries | any, single, and multiple ray, sphere, box, and capsule sweep, overlap, initial overlap, mesh hit |
| Joints | fixed, distance, hinge, prismatic, spherical, world attachment, limits, motor and spring, removal |
| Character | timing, slopes, steps, floor, moving platform, arbitrary up, dynamic interaction, teleport and resize |
| Events and debug | begin, end, hit, movement, sensors, joint event, debug on and off, budget overflow |
| Scale | small scene, 1k and 10k bodies, large static set, batched queries |
| Failure | missing DLL, wrong architecture, version, or precision, invalid ID, unsupported field or type, callback fault |

## Hardware Matrix

Primary local target (recorded 2026-07-20). It is suitable for the first OpenGL and Vulkan high-end desktop gate.

| Item | Value |
| --- | --- |
| CPU | AMD Ryzen 9 7950X3D, 16 cores, 32 logical processors |
| Memory | 48 GiB (2 x 24 GiB Corsair `CMH48GX5M2B7200C36`) at 4800 MHz |
| GPU | NVIDIA GeForce RTX 3090, 24 GiB, driver 610.74 |
| Display | 2560 x 1440 at 144 Hz |
| OS | Windows 11 Pro 10.0.26200 |
| Power plan | High performance |
| SDK | .NET 10.0.301 |
| Baseline commit | `12dd9359dfbbcd887c7986a6104d33c35715705c` |

Still required: a cross-vendor GPU target and a lower-tier CPU and GPU target. Name them before cross-vendor and low-count latency gates can close.

## Failures

| Check | Symptom | Investigation or code item |
| --- | --- | --- |
| GPU skinned chain scale: frame rate at 2,000 chains | Below the 100 Hz target | [Skinned GPU chain benchmark investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) |
| GPU skinned chain scale: start and stop cycles | Intermittent `ResourceGenerationBlocked`; one start request timed out | [Open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults) |
| GPU skinned chain scale: capture lifecycle | Explicit RenderDoc start/end faults in `renderdoc.dll`; MCP readback images are black | [Open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults) |
| GPU skinned chain scale: solver group size | 64- and 128-thread windows failed with input-page fence failures | [Open faults](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#open-faults) |
