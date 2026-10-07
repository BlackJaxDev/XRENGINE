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

Procedure for this group: Math Intersections World, `Physics Chain GPU Dispatcher Skinned Mesh Test`, isolated Release editor. See the [performance guide](../../../developer-guides/rendering/physics-chain-performance.md) and the [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md). The latest desktop continuation passed 64, 2,000, and 96-chain restart checks and native wide/near palette-box containment. It used the Ryzen 9 7950X3D and RTX 3090. Earlier laptop checks used an Intel Core Ultra 9 185H and RTX 4070 Laptop GPU; keep these results separate. Current evidence does not meet the 100 Hz target. The remaining checks need their stated evidence.

Timing procedure for a 2,000-chain window:

1. Start a named isolated Release session with `Tools/Manage-McpEditorSession.ps1 Start -Configuration Release -SessionEnvironmentFile <file>`. The JSON file sets `XRE_UNIT_TEST_WORLD_KIND=MathIntersections`. For stage counters, it also sets `XRE_WORLD_TICK_TELEMETRY=1`. Acceptance windows set it to `0`.
2. Through MCP, set `IsActiveSelf` to true on the `Physics Chain GPU Dispatcher Skinned Mesh Test` node. Put the editor camera at `(0, 260, 310)`, looking at the origin.
3. On the `MathIntersectionsWorldControllerComponent` of `Root Node`, set `_benchmarkCopyCount` to `2000` and `_benchmarkDurationSeconds` to `120`. Call `SetBenchmarkRunToggle(true, false)`. The controller limits each run to 120 seconds. Finish warmup and sampling before this limit.
4. Poll `get_render_profiler_stats`. Wait until `vulkan.frame_lifecycle.outcome_counts.completed` increases by 30 with no change in `rejected`, `deferred`, or `failed`.

Set the four `Debug` observer preferences listed in the performance guide to
false. Read each value with `get_editor_preference`. Select `viewport_index: 0`
for render-state snapshots and confirm requested and resolved frozen strategy
value `2` for strict indirect submission. Check advancing directional accepted
groups for animated-shadow runs. The benchmark flags `-CpuTraceSeconds` and
`-RequireDirectionalShadows` automate these diagnostic and shadow checks.

5. Read `GPUPhysicsChainDispatcher.Instance.RegisteredComponentCount`. Sample `completed` once per second for 30 seconds. Completed Hz is the change in `completed` divided by the elapsed wall time. Read the registered count again. Reject the window if either count is not 2,000.
6. For diagnostic windows, read before and after the window: the source chain's `PhysicsChainComponent.WorldLateTickTelemetry`, `RuntimeWorldRenderer.CollectionTelemetry`, and `RenderableMeshStageTelemetry.Snapshot()`. Report the differences per call or per rendered frame.

The timing script allows up to 80 seconds for cold readiness. It reserves the
sampling window and a 10-second margin within the controller's 120-second run.
Cold readiness can create more than 2,000 native mesh resources. It is not part
of the accepted interval window. Diagnostic runs retain both raw telemetry
snapshots and named-stage differences.

`Tools/Invoke-Mcp.ps1 -Url <endpoint>` works in Windows PowerShell 5.1. With `-Session`, it searches every session manifest on each call.

- [ ] Frame rate at 2,000 chains. Expected: 2,000 visible chains at 100 or more completed render frames per second, with frame-interval p95 at most 10 ms. Record hardware, clocks, render resolution, scene settings, and observer flags. Last evidence: the strict moving-shadow window completed 397 frames in 30.971 seconds (12.818 Hz), with interval p95 83.689 ms. It added 471 accepted directional groups, with no new native or shadow rejection, CPU fallback, input growth, or physics readback. All four Debug observers, RenderDoc, and world telemetry were off. Evidence: `reports/shadow-dirty-2000`. The target failed. Earlier windows with cached static shadows or active code profiling are not equivalent timing evidence.
- [x] Native mesh readiness. Procedure: wait before each timing window. Expected: at least 30 completed frames with no new deferred, rejected, or failed outcomes. Registration counts alone do not prove readiness. Last evidence: three clean timed windows with 31 samples each, more than 30 new completed outcomes each, and unchanged deferred/rejected/failed counts.
- [ ] Binding counts. Expected: 2,000 palette bindings, 2,000 admitted deformation jobs, 208,000 vertices, no CPU fallback, and zero physics readback before and after timing. Last evidence: 2,000 registered chains and palette bindings, no CPU fallback, and zero physics readback before and after all three clean windows; confirm job and vertex counts separately.
- [x] Functional counter lifecycle. Expected: three runs keep 2,000 registered chains and palette bindings before and after timing, then restore one chain, one palette slice, and one binding with zero physics readback. Last evidence: all three clean-cycle before, after, and restored reports.
- [ ] Start and stop cycles. Procedure: three cycles on the final binary. View moving and bent meshes from near and wide cameras. Expected: after each stop, one source chain, one palette slice, one renderer binding, and continuing source animation. Last evidence: three counter-cleanup cycles restored one chain and one palette binding with zero readbacks. Root viewed wide images from all three cycles, near deformation pairs from cycles 2 and 3, and restored-source animation pairs after cycles 1 and 3. A separate restored-source animation pair after cycle 2 and exhaustive per-chain visual validation remain open.
- [ ] Solver group size. Procedure: compare 32-, 64-, and 128-thread short-linear solver groups on matched hardware. Expected: the shared indirect argument builder uses the same group size and every chain continues to update. Last evidence: trials are not a controlled comparison; no winner proven.
- [ ] Final acceptance runs have diagnostic timing and frame-capture writers off. Last evidence: three clean no-build windows on the CPU6 binary with the timing observer off; frame-capture writer state is not confirmed, and frame rate remains below target.
- [ ] Canonical bounds and shadow admission. Procedure: move GPU-deformed chains across view and shadow-cascade boundaries with CPU bone synchronization off. Capture and view near and wide images. Include partial deformation coverage, disabled shadow casting, and layer exclusions. Expected: no rejection from stale CPU bounds, no policy bypass, and zero physics readback. Last evidence: wide and near native frames each contain all 416,000 current and previous vertex positions within routed bounds. Viewed exports show the full grid and bent meshes. Each frame has 2,000 main visibility draws and 8,000 shadow draws. Layer and render-info visibility exclusions remove and restore the main draw. Partial coverage, boundary motion, and shadow-exclusion checks remain open. MCP viewport readback returns black images that do not match native targets. Use presented-frame trigger captures; see the separate capture check below.
- [ ] Bounds route lifetime. Procedure: repeat removal, command compaction, slot reuse, and arena growth with frames in flight. Expected: routes keep the correct draw generation and candidate index; no draw reads another chain's bounds or retired storage. Last evidence: command compaction and slot reuse behavior tests passed before the palette-box correction. The source-ordering repair and final palette correction passed 64, 2,000, and 96-chain restart windows. Each stop restored one source chain and one resident draw without publication rejection. Final layout tests, failure handling, and backend replacement remain open. See the investigation.
- [ ] Material vertex effects. Procedure: start the session with `XRE_PHYSICS_CHAIN_TEST_VERTEX_EFFECTS=1`. Use a GPU-covered draw with local translation effects, a command material override, and animated effect parameters. Change the parameters with `set_material_uniforms`. Exercise each GPUScene consumer that admits the material. Read `GPUPhysicsChainDispatcher.Instance.GpuBoundsDiagnostics` and inspect the routed bound in a trigger capture. Expected: translation effects stay inside the padded bound; scale, rotation, look-at, and barrel effects reject the slot with a counted diagnostic, no CPU bound, and zero physics readback. Last evidence: strict one-chain Vulkan captures use frozen strategy value `2`. A (0, 3, 0) translation adds exactly 3 units of work-item padding; all 208 current and valid previous offset positions fit. A 15-degree rotation sets the rejection flag, writes a NaN atlas slot, clears the candidate view mask, and emits no chain draw. A neutral command override restores a valid bound and draw while the source material still has rotation. Rotation on that override rejects the bound and removes the draw again. Clearing the effects and override restores the draw. Presented exports were viewed. The full effect matrix, other consumers, and final 2,000-chain timing remain open.
- [ ] Previous deformation history. Procedure: force a failed aggregate submission, skip Advanced preparation frames, reuse one preparation within a frame, and publish several physics pages between consecutive preparations. Expected: failed output is never valid previous history; every retained previous vertex fits the current routed bound. Ordered submitted work needs no CPU completion wait. Last evidence: the failed-fence guard, exact consumed page lineage, and retained blendshape inputs are present. Two native 64-chain frames contained all 13,312 current and previous vertex positions. Fault injection, skipped-page cases, source replacement, and the final binary remain open. See the investigation.
- [ ] OpenGL bounds execution. Procedure: run the same scene with OpenGL and inspect candidate patching, binding restoration, native bounds containment, removal, and backend replacement. Expected: the same route and lifetime contract as Vulkan, without stale CPU rejection or readback. Last evidence: the OpenGL implementation builds; no final live OpenGL check is recorded.
- [ ] Capture lifecycle. Procedure: use `RenderDocCaptureBridge.TryTriggerCapture` for presented frames, then check `get_time_state` and stop the owned session. Investigate explicit start/end calls separately. Expected: capture state changes cannot interrupt native command recording. Last evidence: wide and near trigger captures completed and the engine remained running after benchmark stop. Explicit start/end reproduced a first access violation in `renderdoc.dll`, called from Vulkan shadow push-constant recording. The capture-layer cause remains open. The owned session is stopped.
- [ ] Shared preparation and GPU input gathering. Procedure: repeat matched 2,000-chain runs after each related code change. Compare preparation, hierarchy evaluation, collection, and swap counters in separate diagnostic windows. Then disable observers and measure completed render intervals. Expected: lower end-to-end cost with current animated roots, independent palettes, correct reset and teleport, and zero physics readback. Last evidence: the desktop continuation reduced repeated ID and hierarchy lock work. Late-tick body means were 22.214 ms before and 19.365 ms after; hierarchy means were 8.276 and 7.029 ms. Shared preparation and world-owned input gathering remain open.
- [ ] Strict GPU-indirect submission. Procedure: use the same scene with `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`. Expected: visible animated meshes and a stable completed-frame window, without CPU fallback. Last evidence: near and wide 64-chain native captures use `vkCmdDrawIndexedIndirectCount`. All 13,312 current and previous positions fit the routed bounds; presented targets were viewed. Latest 64- and 2,000-chain windows keep requested and resolved strategy value `2`, without downgrade, bad native outcomes, or physics readback. The moving-shadow window advances accepted groups without generic fallback. Final native cascade capture, failure injection, and backend comparison remain open.

### Physics chain scale

Additional canonical submission checks:

- [ ] Indexed instance groups. Procedure: strict indirect near and wide trigger captures; inspect arguments, member IDs, deformation offsets, material identity, and temporal validity. Include partial visibility, distinct compatible geometry sources, material changes, authored multi-instance draws, late recovery, and OpenGL stereo. Expected: one argument per nonempty compatible group, independent member state, and no readback. Last evidence: Vulkan 64-chain captures show one indirect draw with 64 wide-view instances and 25 near-view instances. Each selected payload is unique. All 13,312 current and previous positions fit their own bounds; presented targets were viewed. The additional cases remain open.
- [ ] Frozen consumer strategy. Procedure: switch CpuDirect and strict indirect submission, then use two consumers with different strategies in one frame. Expected: each package keeps its captured lane, shared deformation runs once, no sealed slot is reopened, and no lane mismatch rejects the frame. Last evidence: six strict 64-chain samples after the captured-strategy repair have no new bad native outcomes. The multi-consumer and transition matrix remains open.
- [ ] Incremental covered plans. Procedure: read `ReusedStaticDrawPlanCount` and `MaterialResolveAttemptCount` during root motion, then edit material parameters, a command override, and mesh geometry. Expected: root motion reuses the static plan with no material resolution or content delta; each source edit invalidates its plan. Last evidence: 2,000-chain final snapshots have 2,000 retained plans, zero material resolution, and zero content deltas. The prior identity witness is built. Source-edit and nonidentity-to-identity runtime cases remain open.
- [ ] Directional GPU shadow cache and policy. Procedure: use `-RequireDirectionalShadows`; move covered casters, apply texture and command overrides, exclude caster layers, use multiple consumers, and disable the lane. Inspect native arguments and independent cascade members. Expected: current output publications dirty every matching cascade; held pages keep the cache; strict rejection keeps work dirty with no generic fallback; every cascade owns its slices and descriptors. Last evidence: strict 64- and 2,000-chain windows advance accepted groups with constant rejected, unconsumed, and generic counts. Main collection eligibility is now independent of shadow invalidation in source. Final native and policy checks remain open.
- [x] Normal world GPU-input capture and parent guards. Procedure: baseline motion, authored reset, manual external-parent scale change, root inertia, distance reentry, and restore. Expected: normal inputs use the world range without per-particle hierarchy refresh; affected inputs use the explicit hierarchy route and preserve the GPU solver. Last evidence: `reports/parent-input-live.json` and `reports/world-input-restored-final.json`; 183 baseline and 91 restored ticks have zero rest-reset, hierarchy-refresh, and particle-transform-read tick deltas. Multi-chain custom dependency and final ownership changes still need their separate checks.
- [ ] World and hierarchy ownership. Procedure: world detach and reattach, parent move and restore, activation changes, transfer between two ticking worlds, and stopped-source transfer. Expected: the old owner retires before hierarchy writes, the new owner admits only after propagation, and active strict publication recovers without a stale handle or deadlock. Last evidence: the combined editor passes one-chain world detach, reattach, parent move, restore, deactivation, and reactivation. Evidence: `reports/scene-boundary-*.json`. Two-world concurrent and stopped-source cases remain open.
- [ ] Selective solved-pose readback. Procedure: request particles and bone, socket, and full-transform values, with translated roots, affine inputs, and a held chain. Compare translation and rotation with the same solved output. Expected: bounded delayed data, correct 48-byte affine rows, no blocking wait, and no transfer without an explicit request. Last evidence: five one-chain particle-and-bone requests each delivered 216 bytes in three frames. Bone translation matched the selected solved particle exactly; rotations were nonidentity. Evidence: `reports/live-readback-summary.json`. Socket, full-transform, affine scale, and held-input cases remain open.
- [ ] Selective transfer lifetime. Procedure: simultaneous requests in two worlds, cancellation, expiry, reset, same-capacity template change, renderer replacement, partial enqueue failure, and device loss. Expected: exact world and source identity, no stale delivery, no physical slot overwrite, and storage retirement after raw, authoring, and native readers finish. Last evidence: source review passes the world banks, source witness, held-input route, failed marker reuse proof, and atomic disposal guard. The latest combined Release build passes. First-use admission still needs the code correction in the todo; the native failure matrix remains open.
- [ ] Global GPU chain debug. Procedure: debug off, one selected chain, a bounded sample, two views, same-frame selection changes, capacity growth, renderer mismatch, cancellation, and teardown during deferred work. Inspect both indexed indirect draws and view exported targets. Expected: one compact world batch per frame, two point-topology indirect draws, no CPU readback, zero debug work when disabled, frozen content for same-frame views, and exact deferred disposal. Last evidence: the indexed path, atomic retirement, native Ready guard, request/payload leases, and occupied-operation protection build with zero warnings and errors. Final review found late first-use admission and recurring compute snapshot allocation. Both code corrections are in the todo. Native checks remain open.

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
- [ ] Final acceptance. Expected: strict CPU and GPU meet the correctness tolerances and named-hardware budgets across the matrix. The 10,000-chain scenario meets its simulation and end-to-end budgets with p95 and p99 evidence. Before and after tables are recorded in a durable progress note. Last evidence: none.

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
|---|---|---|
| GPU skinned chain scale: frame rate at 2,000 chains | Below the 100 Hz target | [Skinned GPU chain benchmark investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) |
