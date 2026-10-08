# Skinned GPU Chain Benchmark Investigation

Status: Open. The 100 Hz target is not met.
Last updated: 2026-10-08

[Design](../../design/physics/physics-chain-steady-state-cpu-design.md) ·
[Code items](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md) ·
[Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale) ·
[Performance guide](../../../developer-guides/rendering/physics-chain-performance.md)

This document records the evidence: measurements, root causes, fixed defects, rejected experiments, and open faults. The design doc owns the response. The todo owns the code items. The validation doc owns the checks that remain.

## Problem

The `Physics Chain GPU Dispatcher Skinned Mesh Test` in the Math Intersections world must render 2,000 visible animated chains at 100 or more completed frames per second, with a frame-interval p95 of at most 10 ms. Simulation, bone palettes, deformation, and bounds stay on the GPU with zero physics readback. The target applies to a shared-mesh scenario and a unique-mesh scenario. Current results are 16–25 Hz.

## Setup

- Scenario: each copy has 7 particles (a root and 6 bones), a sphere and a plane collider of its own, a moving root, and one skinned box mesh with 104 vertices. 2,000 copies give 14,000 bone mappings and 208,000 vertices. By default all copies share one mesh, so the Advanced pipeline draws them as one indexed instance group. `-MeshSharing Unique` gives each copy distinct vertex content, so each copy draws in its own group.
- Runtime: Release editor in a named isolated session, Vulkan, Advanced pipeline, strict `GpuIndirectZeroReadback` submission (earlier runs used `CpuDirect`), animated directional shadows, VSync off.
- Laptop: Intel Core Ultra 9 185H, RTX 4070 Laptop GPU, 1920 × 1080 output, 1286 × 723 internal.
- Desktop: Ryzen 9 7950X3D, RTX 3090 (driver 617.14), 1920 × 1080.
- Harness: `Tools/Benchmarks/Measure-PhysicsChainScale.ps1`. See the [performance guide](../../../developer-guides/rendering/physics-chain-performance.md#gpu-skinned-scale-measurement) for its gates and options. Results from the two machines are not a matched comparison.

## Conclusion

The frame is CPU-bound. Each steady-state frame does CPU work for every chain on four threads, at about 20 µs per chain, against a budget of about 2.5 µs. No loop skips an unchanged chain, and most of the work is in the object pattern. The physics simulation itself is not the limit: freezing the physics callbacks gave no stable gain, while hiding the whole chain rendering path tripled the rate. After the committed bound version and proxy change, collect is below render, and the render callback plus the serial swap (about 42 ms at 2,000 chains) is the critical path. Instancing does not change the CPU-bound frame. Small caches inside full per-chain walks saved 1 ms or less each, which is inside the 15–25% window noise. The response is in the [design](../../design/physics/physics-chain-steady-state-cpu-design.md).

## Results

### Scaling

| Chains | Machine | Completed Hz | Interval p95 | Notes |
| ---: | --- | ---: | ---: | --- |
| 1 | Laptop | 182 | — | Early source-only window. The setup is uncertain. |
| 1 | Desktop | 204 | — | A window that ended at one chain. It is not a valid scale window. |
| 64 | Laptop | 130.0–234.4 | 5.6–9.4 ms | Three windows on later builds. |
| 1,000 | Laptop | 23.7 | — | After the visibility append repair, with `CpuDirect`. |
| 2,000 | Laptop | 16–22 | 52–75 ms | Latest builds. |
| 2,000 | Desktop | 20.9–25.3 | 46–59 ms | Latest builds. |

Between 64 and 2,000 chains, the frame interval grows by about 22 µs per chain. A matched one-chain baseline and a chain-count ladder are open checks.

### 2,000-chain history

Machines, cameras, observers, and code changed between rows. The table shows the trend, not matched gains.

| Date | Machine | Change | Completed Hz | p95 ms |
| --- | --- | --- | ---: | ---: |
| 10-06 | Laptop | First measurement. RenderDoc showed 266.6 ms in `Advanced.Visibility.Early`. | 3.7 | — |
| 10-06 | Laptop | One atomic reservation for each visibility append. | 11.7–12.8 | — |
| 10-06 | Laptop | CPU preparation, subscription, and allocation work. | 9.0–12.2 | — |
| 10-06 | Desktop | Lock reduction and renderer subscription reuse. | 12.1–15.2 | — |
| 10-07 | Laptop | Canonical bounds, committed spatial bounds, world gather, instance groups. | 10.7–16.6 | 70–90 |
| 10-07 | Laptop | Atomic reservation for group counters; device-local visibility memory. | 18.9–21.8 | 52–65 |
| 10-07 | Laptop | Input-bank provisioning, full-grid camera, repeat windows. | 15.0–22.2 | 52–85 |
| 10-07 | Laptop | Rigid rest-input cache off/on comparison. | 18.5–21.3 | 54–66 |
| 10-08 | Desktop | Registration lookup cache (baseline and candidate). | 20.9–24.3 | 47–59 |
| 10-08 | Desktop | Retained source groups. | 23.6–25.3 | 46–49 |
| 10-08 | Laptop | Frame-loop phase counters added; no optimization. | 19.42 | 70.9 |
| 10-08 | Laptop | Baseline for the A/B below, shared and unique meshes. | 21.8–23.3 | 49–56 |
| 10-08 | Laptop | Committed bound version and proxy, shared and unique meshes. | 21.0–24.8 | 46–62 |

### Ablations

Three cycles on the laptop, each with 2,000 registered chains, zero new bad outcomes, and zero physics readback. Cycles 1 and 2 used a client timer; cycle 3 used engine-side timestamps.

| Cycle | Full scene Hz | Chain rendering hidden Hz | Physics callbacks frozen Hz |
| --- | ---: | ---: | ---: |
| 1 | 19.18 | 58.92 | 25.04 |
| 2 | 16.57 | 51.97 | 13.03 |
| 3 | 16.33 | 57.34 | 21.61 |

Hiding chain rendering removes collection, publication, deformation, and drawing. It is not a raster-only test. Freezing physics keeps root motion and CPU hierarchy work. Even with chain rendering hidden, the rate stays near 55 Hz, so the physics-side CPU work also needs reduction.

### Critical-path timeline

A 12-second scope timeline at 2,000 chains (laptop, 252 render scopes) supports this cycle: the longer of render and collection, followed by a serial swap.

| Measure | Full, mean ms | Physics frozen, mean ms |
| --- | ---: | ---: |
| Render start spacing | 47.32 | 43.44 |
| `EngineTimer.DispatchRender` | 30.24 | 30.19 |
| `DispatchCollectVisible` | 25.32 | 21.92 |
| Collection wait for render | 5.08 | — |
| `DispatchSwapBuffers` (serial) | 16.91 | 13.32 |
| `XRWindow.GlobalPreRender` residual | 10.54 | 0.35 |
| `Advanced.VisibilityPreparation` residual | 6.18 | 19.47 |
| Command recording residual | 6.73 | 5.37 |

`GlobalPreRender` runs `GPUPhysicsChainDispatcher.ProcessDispatches` on the render thread. When physics is frozen, that time moves into visibility preparation instead of leaving the frame; a wait is the likely cause, and the preparation lock-wait counters are an open check. Other large residual scopes in the full run: `GpuIndirect.AdvancedPublication.ScenePlan` 6.96 ms, `RuntimeWorldRenderer.GlobalPreCollectVisible` 8.95 ms, `VisualScene3D.CollectRenderedItemsGpu` 6.17 ms (two calls per cycle), `RuntimeWorldRenderer.GlobalSwapBuffers` 4.59 ms, and `CpuBvhRenderTree.Swap` 1.63 ms (two calls per cycle). These are wall times; they can include waits.

### Frame-loop phase totals

The always-on phase counters (`frame_lifecycle.phase_totals`, reported by the harness as `phaseTimings`) were first measured on 2026-10-08 on the laptop, with telemetry and the Debug observers off. Clocks were not controlled. Values are milliseconds per call; the update thread runs at its own rate.

| Phase | 64 chains | 2,000 chains | Two-point slope (µs per chain) |
| --- | ---: | ---: | ---: |
| Completed Hz / interval p95 | 179.8 Hz / 7.7 ms | 19.4 Hz / 70.9 ms | — |
| Mean frame interval | 5.56 | 51.5 | 23.7 |
| Update iteration | 0.91 | 34.39 | 17.3 |
| Collect | 0.55 | 27.21 | 13.8 |
| Serial swap | 0.36 | 17.15 | 8.7 |
| Render callback | 5.25 | 34.04 | 14.9 |
| Collect wait for render | 4.66 | 7.14 | — |
| Render wait for collect, per completed frame | 0.27 | 17.38 | — |

At 2,000 chains each thread accounts for the whole interval. Collect thread: collect 27.2 + wait 7.1 + swap 17.2 = 51.5 ms. Render thread: render 34.0 + wait 17.4 = 51.4 ms. The critical path is the render callback followed by the serial swap, about 23.5 µs per chain together. The render callback includes present and fence waits; an idle check measured 21 ms per callback with the collect thread waiting 20.6 ms for it. Two points are not a fit; the chain-count ladder remains open.

### Chain-count ladder

First complete ladder on 2026-10-08 (laptop, Turbo power plan, no affinity mask, clocks not locked, 20-second windows, all five points accepted). Fits are ordinary least squares over 1, 250, 500, 1,000, and 2,000 chains; phase values are per call.

| Fit | Fixed ms | µs per chain | R² |
| --- | ---: | ---: | ---: |
| Mean frame interval | 2.27 | 19.95 | 0.985 |
| Interval p95 | 3.06 | 24.38 | 0.983 |
| Render callback | 3.35 | 11.89 | 0.989 |
| Serial swap | −0.95 | 7.82 | 0.980 |
| Collect | −1.40 | 12.47 | 0.983 |
| Update iteration | −0.65 | 15.24 | 0.996 |
| Render wait for collect | 0.04 | 4.72 | 0.980 |

| Chains | Completed Hz | p95 ms | Render | Swap | Collect | Update | Mean GPU MHz |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 222.5 | 5.7 | 4.39 | 0.14 | 0.15 | 0.38 | 2,595 |
| 250 | 140.1 | 8.9 | 6.28 | 0.90 | 1.62 | 2.94 | 2,595 |
| 500 | 89.4 | 14.4 | 8.93 | 2.32 | 3.92 | 6.36 | 2,595 |
| 1,000 | 51.0 | 23.8 | 13.78 | 5.86 | 9.53 | 13.87 | 1,422 |
| 2,000 | 22.9 | 53.9 | 27.95 | 15.38 | 24.54 | 30.37 | 1,177 |

- The critical path (render callback plus serial swap) grows by about 19.7 µs per chain. The 100 Hz target allows about 2.5 µs.
- Cost per chain rises with count: the swap costs about 3.6 µs per chain at 250 chains and about 7.7 µs at 2,000. The negative intercepts show the same curvature. This agrees with the larger per-row cost of the object pattern at 14,000 rows in the microbenchmark.
- The GPU dropped toward P5 at 1,000 and 2,000 chains because the CPU limits the frame. At 64 chains in a later session, every phase ran about 2.5 times slower than in the first session, including CPU-only phases. The harness does not yet record CPU clocks; compare windows only under controlled conditions.

### GPU and update-thread measurements

- An Nsight API-only trace (laptop) found a mean GPU span of 8.80 ms (p95 12.83 ms) against a mean submit spacing of 48.08 ms: 18% observed workload coverage. GPU clocks varied between P0 and P5 during windows.
- World callbacks: `PhysicsChainWorld.LateTick` 23.79 ms per call and `FixedTick` 10.32 ms per call (fixed tick includes the tick-gate wait). Nested late-tick stages: quality plus dependency preparation 5.59 ms, rest-input gathering 7.16 ms, component preparation 3.11 ms, packing 1.91 ms, bridge submission 1.42 ms, activity scan 1.42 ms. The stages are nested; do not add them.
- Sampled rest-input stages, per chain: opaque dependency check 1.88 µs, owner check 0.79 µs, collider check 1.77 µs, root preparation 0.85 µs, cached input expansion 0.32 µs, publication 0.21 µs.
- Process-wide managed allocation was about 1.3–1.4 MB per completed frame. It is not attributed to a subsystem.

## Root-cause evidence

### Per-chain work inventory

Source review on 2026-10-08 at commit `52f011649`. Read-only review agents counted the operations; the items marked "confirmed" were checked by hand. Counts are operations per chain per frame.

| Thread and phase | Per-chain work | Skips unchanged chains? | Main causes |
| --- | --- | --- | --- |
| Update: world late tick | About 13 `IsActiveInHierarchy` walks, 12 parent walks, 25–30 lookups in dictionaries with 14,000 entries, 7 fenced store reads, 8 `Interlocked` reads, 1 monitor lock, 10 interface calls, 15 matrix multiplications, 9 `RuntimeSlot` struct copies. | No. | Dependency checks run every tick although they depend only on topology, ownership, and the collider list. Any collider disables the fast pack path (confirmed). `IsActiveInHierarchy` recurses to the scene root (confirmed). |
| Render: GPU dispatcher | About 8 lock acquisitions, 15 interface calls, and bounds and palette publication rebuilt each frame: about 25 hash operations and one `CommittedWorldBoundsChanged` event for each renderer. | No. Version checks skip copies but still visit each row 2–4 times. Output page tables are cleared each frame (confirmed). | Each mapped input page receives all colliders and the full transform catalog (confirmed). Several passes run twice. |
| Render: Advanced preparation | `ExtractCommand` does about 150–250 operations, 2 locks, and 8 hash operations for each draw. | No. `commandChanged` is a constant `false` (confirmed). | Reuse applies only inside one frame ID. |
| Render: Vulkan recording | One `CmdDrawIndexedIndirectCount` for each bin. Record validation runs for each view, bin, and phase. | No. | Validation of unchanged sealed records. |
| Collect | About 9–12 `TryGetCommittedWorldBounds` calls, each with about 5 locks and 6 hash lookups. Every covered mesh moves in the octree and CPU BVH, and the BVH refits. | No. | The bounds generation is the global `ProducerEpoch` (confirmed), so the reconcile early exit never applies. Shadow passes cannot use canonical GPU collection (confirmed), so the primary shadow view runs `BeforeAdd` and `AddCPU` for each covered mesh: this is the second collection call per cycle. |
| Serial swap | The whole-scene plan visits every command under `GPUScene._lock`: about 150 operations and 2 locks each. `PublishRenderMatrices` and `DispatchNotifications` visit every transform row during collection and again at the swap. | No. The reuse check runs after the full plan (confirmed). | No dirty-row journal. |

Cross-cutting: about 50 or more lock acquisitions and 1,000–1,500 operations per chain per frame, which agrees with about 20 µs per chain. `TransformHierarchyStore` uses one seqlock sequence for the whole store (confirmed): each write takes `_gate` and increases `_sequence` twice, and each read uses a full memory barrier. One earlier diagnostic window recorded 1,041,463 contended render reads and 803.9 ms of cumulative retry time.

### Row-pattern microbenchmark

Laptop, 2026-10-08, .NET 10.0.12 RyuJIT, BenchmarkDotNet 0.15.8, not pinned to a core type. No pattern allocated. The object pattern is an interface call on a chain object, then a property on a transform object that takes a lock to copy the matrix, with objects scattered on the heap.

| Pattern | ns per row, 2,000 rows | ns per row, 14,000 rows | Ratio to dense |
| --- | ---: | ---: | ---: |
| Dense 48-byte affine write | 2.0 | 1.8 | 1 |
| Store-style seqlock read and write, no writer | 9.0 | 5.0 | 2.7–4.4 |
| Object pattern | 21.4 | 47.2 | 10.6–25.7 |
| Object pattern with a dictionary lookup and an event | 33.5 | 83.2 | 16.5–45.3 |
| Object poll for changes, nothing changed | 1.9 | 7.7 | 0.9–4.2 |
| Dense dirty-flag scan, nothing changed | 0.44 | 0.28 | 0.15–0.22 |
| Page versions, nothing changed (whole store) | 6.6 ns total | 54.9 ns total | 0.002 |
| Page versions, 1% scattered changes | 2.45 µs total | 16.7 µs total | 0.61–0.65 |

The 14,000-row object results varied widely (object pattern: standard deviation 257 µs, median 730 µs). Conclusions: the object layer, not code generation, is the cost; page versions make "nothing changed" almost free; scattered changes in 64-row pages cost about 60% of a full rewrite. A desktop rerun is an open check.

### Defects found by the inventory

Each open defect has a code item in the [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md).

- The committed-bounds generation was the global `ProducerEpoch` (`GPUPhysicsChainDispatcher.SpatialBounds`), so every covered mesh reconciled and moved in the spatial structures in every frame. Fixed; see [committed bound version and proxy](#committed-bound-version-and-proxy).
- `TransformHierarchyStore` has one sequence and one write gate for the whole store.
- `VisualScene3D.CanCollectCoveredSourcesOnGpu` excludes shadow passes.
- Any collider disables the fast GPU pack path in `PhysicsChainComponent.GPU`.
- `UploadInputPageColliders` and `UploadInputPageTransforms` write all colliders and the full transform catalog into each mapped page.
- `AdvancedPreparationExtractor.ExtractCommand` has no per-row change detection.
- `AdvancedGpuScenePublisher` runs its reuse check after the full scene plan.
- Rest dependency checks and `IsActiveInHierarchy` walks run every tick for every chain.

## Fixed defects

These defects were found and fixed during the investigation. The architecture docs own the resulting contracts.

| Area | Defect and fix |
| --- | --- |
| GPU visibility | `Advanced.Visibility.Early` used a global compare-and-swap retry loop for each visible candidate (266.6 ms in RenderDoc). Early, late, and directional shadow group appends now use one atomic reservation, with capacity checks and a clamping finalizer. |
| Startup | The rendering bridge had no production installation. Shader readiness treated `LinkReady` as in progress. Vulkan buffer and copy facades used retained lookups before creating wrappers. Ordered compute operations were placed by free-form labels. |
| Input contracts | Per-frame particle versions replaced GPU state with stale CPU state. Constant signatures blocked moving inputs. The GPU path ignored the step count and time scale. Palette mappings used the parent's rest direction. Palette registrations leaked on rebuild. Bindings omitted reset and rebuild versions. |
| Bounds | The bounds shader used an 80-byte particle stride instead of 64. Previous palette storage was not initialized. OpenGL restored all slot bindings after the patch. First use required previously published coverage. The legacy bounds copy captured updating command indices. A covered draw without a committed route threw in `DispatchSwapBuffers`; it now rejects the publication. A restart race could publish a removed component's palette. A particle envelope did not contain the previous rendered pose; palette-box bounds replaced it. Every renderer query copied the full scene route table. |
| Materials | Translation-class vertex effects now pad the bound; scale, rotation, look-at, and barrel effects reject the GPU route. |
| History | Failed aggregate output could become valid previous history. Producer markers and consumed page tokens now guard it. |
| Lifecycle | `GPUScene.Remove` enumerated a list that was being disposed. A reclaim race between publication and lease acknowledgement. A duplicate key in the profiling dictionary. Material and scene tables needed old-plus-incoming capacity. Boundary growth checked only draw fields. Input pages reused storage after a failed marker without a native reuse proof. Input banks grew during timing; capacity planning now provisions all eight banks. The debug batch destroyed a shared cached shader. Debug and readback paths accessed storage before admission. |
| Frame package | Mutable strategy state could produce two submission lanes in one package (`RangeExecutionLaneMismatch`). `EditorRenderInfo3D` was excluded from canonical collection. |
| Shadows | A new covered palette did not dirty the cached cascade. A coalesced covered-caster output revision now does. |
| Selective readback | Affine gather omitted translation and read the authored pose. Worlds shared physical slots. Ranges and template changes had no witness. |
| CPU | Root-motion callbacks allocated 240,000 bytes per call; now zero. GPU-owned bones no longer keep CPU palette listeners. The root bone no longer recomputes skinned bounds on `WorldMatrixChanged`. Skinned commands publish once after matrix and bounds changes. |
| Shader | `BuildDepthPyramid.comp` kept an unused layout qualifier that the compiler treats as an error. |

The Math world ambient light repair is recorded in the [ambient validation](../../testing/rendering/advanced-world-ambient.md).

## Rejected and unproven experiments

| Experiment | Result | Decision |
| --- | --- | --- |
| `System.Threading.Lock` in place of four monitor gates | No measurable gain. | Removed. |
| Ancestry cache for rest-input dependencies | Late-tick body time rose from 23.79 to 31.86 ms. | Removed. |
| Early no-solve admission for reduced tiers | No end-to-end gain; component preparation rose from about 3.3 to 4.3–4.7 ms. | Removed. |
| Phase-local collider dependency proof | Not built or validated. | Removed; superseded by versioned dependency sets. |
| 64- and 128-thread solver workgroups | Both windows failed with `InputPageFenceFailed`; GPU clocks differed. | Invalid; 32 threads kept. |
| Primary directional shadow collection bypass | Unsafe: the general shadow pipeline still consumes CPU-owned commands. | Replaced by an explicit GPU consumer item. |
| Collecting GPU input after the normal transform pass | Can change callback, rest-pose, and fixed-step ordering. | Rejected. |
| Reading GPU bone ownership without its lock | Can observe a retired bone-buffer state. | Rejected. |
| Existing 15 Hz tier with `Interpolate` | 22.31 Hz against 21.91 Hz at full rate; rest gathering still about 7.3 ms. | No gain; the GPU palette has no interpolation path. |
| Rigid rest-input cache | Gathering fell from 7.2–9.2 ms to 5.9–6.2 ms per late tick. | Kept; no proven end-to-end gain. |
| Registration lookup cache | ScenePlan scope fell from 7.25 to 6.97 ms. | Kept; no proven end-to-end gain. |
| Retained source groups | ScenePlan scope 6.87 ms. | Kept; no proven end-to-end gain. |
| Lock reduction for IDs and transforms | Late-tick body fell from 22.21 to 19.37 ms. | Kept; no proven end-to-end gain. |
| Device-local visibility memory | Placement confirmed; no matched gain. | Kept. |
| CPU palette listener detach | Within run-to-run noise. | Kept. |

## Open faults

| Fault | State | Next step |
| --- | --- | --- |
| Intermittent `ResourceGenerationBlocked` at frame pacing after repeated runs. Rendering stopped, then recovered without a restart. | Unresolved. A retained first/latest blocker diagnostic exists. | Reproduce with the diagnostic active. |
| A benchmark start request timed out after 180 seconds although the editor started the benchmark. | Unresolved. The harness now marks a pending start. `SetBenchmarkRunToggle` returns `void` and does not wait. | Use the opt-in MCP request trace. |
| Input and output page marker failures (`UnsubmittedMarker`, `PlanUnsubmitted`, `RequiredProducerMissing`, all with no native submission). | The origin of the older failures is unknown. Retained fence diagnostics exist. | Correlate creation, bind, failure, and observation identities. |
| Physics output stalls for a whole session after an early unsubmitted plan. On 2026-10-08 (laptop, `-NoBuild` restart of an unchanged binary), an input-page fence failed at authored frame 237 with site `PlanUnsubmitted` and `NativeSubmission=NotCalled`. Four input-page and four output-page failures followed. The producer epoch then stayed at 11, and `OutputPageBusyCount` grew by one each frame: no page passed the retain and reuse checks in `TryBeginOutputPage`. Every later window failed with `OutputPageBusyOrUnsafe`. A second fresh session repeated it (fence failure at frame 167, producer epoch stuck at 5). Both stalled sessions began with a 1-chain benchmark start; both healthy sessions began with 64 chains, and a 1-chain start after a healthy start passed. Two samples each; this is a lead, not a cause. | Fixed and live-validated: free pages now release unprepared bounds buffers. See [output page stall](#output-page-stall). | `PhysicsChainBufferReuseTests` cover the reclaim rule. Why the atlas's deferred upload never completes is still open. |
| Explicit RenderDoc start/end causes an access violation in `renderdoc.dll` from `VulkanCommandRuntime.PushConstantsTracked` during directional shadow recording. | Isolated to the capture-layer path. | Use `RenderDocCaptureBridge.TryTriggerCapture`. Investigate explicit start/end separately. |
| MCP viewport readback returns black images that do not match native targets. | Unresolved. | Use presented-frame trigger captures for image evidence. |
| One launch stopped with "Canonical resident tables exhausted their preflighted frame-boundary capacity". | Did not repeat. The message also covers invalid material, geometry, and lookup failures. | If it repeats, add failure-only diagnostics at the first failed check. |
| One benchmark start threw `NullReferenceException` and left 70 copies alive. | Did not repeat. Failed-start cleanup now removes partial copies. | None until it repeats. |
| Cold start: `visible-mesh-cold-admission` retries, one exhausted descriptor-preparation recovery, one cold `GpuBoundsPublication.AtlasBufferReadiness` failure. Readiness can take more than 45 seconds at 2,000 chains. On 2026-10-08, one of about eight session starts latched at frame 83: "Compute descriptor resources for 'UnnamedProgram' could not be prepared before recording ... The frame plan has no resource-planner generation for the compute operation context." PresentNow recovery used its budget of three attempts. After that, the renderer rejected every frame (0 completed, 11,181 rejected) and discarded the queued scene work. Each physics output page was committed, its fence failed with `QueueDiscard`, and a simulation receipt then withdrew the page as a known producer failure. This was silent: `OutputPageDiagnostics.FailureCount` did not change. A restart without a rebuild was healthy. | Excluded from timing windows by the readiness gate, which needs completed frames. The silent page failures are a code item in the todo. | Find why the compute operation context has no resource-planner generation at cold start. Track separately from steady-state work. |
| Two cold launches lost their engine loop threads, and one process exited after startup with no log. | Causes unknown. | Capture a dump on the next occurrence. |
| A terminating `IndexOutOfRangeException` in `PhysicsChainComponent.BuildRuntimeTemplate`, on the fixed-update thread (`PhysicsChainWorld.DrainStructuralCommands` → `AddComponent` → `GetOrCreateRuntimeTemplate`). It happened once in about 40 benchmark stops on 2026-10-08, in the same second that a 1-chain window ended and the controller restored the source rig. The window had already been accepted. | Cause unconfirmed. The method counts particles in one pass and fills arrays in a second pass, so a concurrent particle-tree rebuild during source restoration could overrun them. | Code item in the todo. |
| A callback exception during identity delivery stops the editor timer. | Existing terminal-fault policy; direct publisher tests prove caller retry. | No change planned. |

## Output page stall

Status: fixed, live-validated, and covered by `PhysicsChainBufferReuseTests`. The deferred-upload question remains open. Laptop, 2026-10-08.

**Fix.** `CanReuseOutputPage` now releases a free page's bounds atlas or slot metadata when that buffer reports `Unsupported` native reuse and every other reuse check passes. Production recreates the buffer. `PhysicsChainBufferReuse.TryEvaluateFreeOutputPage` makes the buffer decision. `PhysicsChainGpuOutputPageDiagnostics.UnpreparedBufferReleaseCount` counts the releases. The [output and readback architecture](../../../architecture/physics/physics-chain-output-and-readback.md#canonical-gpu-bounds) states the rule.

**Validation.** 20 fresh sessions with a 1-chain start: none stalled, the busy count stayed at 0, and every session released one or two unprepared buffers during startup. The unprepared buffer therefore forms on every cold start; before the fix, one blocked free page silently reduced the ring to three pages, and two blocked pages stalled it. Three of the first 12 windows were rejected for in-window input-page `PlanUnsubmitted` failures. All three showed a late directional-shadow compute pipeline compile (`VulkanPresentNowReadinessRetry`, `RetryFrame`) at frames 334–336, with zero accepted and 158–197 rejected shadow groups at window start; no buffer release happened inside those windows. The harness now waits for accepted shadow groups with no new rejections before a window. With that gate, 8 of 8 windows were accepted, and a 2,000-chain window was accepted at 22.17 Hz with p95 55.65 ms.

**Reproduction.** Start a fresh editor session and start the benchmark with 1 chain. About 1 in 4 to 7 fresh sessions stall (3 stalls in about 20 sessions). Each stall begins in the first 200 rendered frames, while cold startup defers frames and settles their submission markers as `PlanUnsubmitted`. A 1-chain start after a healthy start has not stalled. Injected desktop frame failures at `SceneRecording` and `Submission` did not reproduce it: the physics markers were not in those plans, so deferred startup frames are the likely real trigger.

**Evidence.** Two instrumented stalls recorded identical page states:

| Page | Generation | Producer epoch | Role | Retain | Fence | Native reuse (atlas / metadata / current / previous palette) |
| ---: | ---: | ---: | --- | ---: | --- | --- |
| 0 | 3 | 2 | free | 0 | Submitted | Unsupported / Ready / Ready / Ready |
| 1 | 2 | 3 | free | 0 | Submitted | Unsupported / Ready / Ready / Ready |
| 2 | 1 | 4 | history | 0 | Submitted | Ready / Ready / Ready / Ready |
| 3 | 1 | 5 | published | 4 | Submitted | PendingCompletion ×3 / Ready |

Neither free page has a known producer failure. Pages 0 and 1 were acquired three and two times but published only once, so later production attempts on them were abandoned.

**Mechanism.** `TryBeginOutputPage` skips the published and history pages and requires every buffer of a free page to report native reuse `Ready`. For the bounds atlas, `VulkanResourceRuntime.QueryBufferContentReuse` returns `Unsupported` when `TryCaptureComputeBufferSnapshot(allowSynchronousUpload: false)` fails, that is, while the buffer is not ready for rendering. The atlas is created or resized lazily inside production (`GPUPhysicsChainDispatcher.Bounds`), and only that production path calls `EnsureGpuBufferReady` on it. When production is abandoned after the atlas changed, the page keeps an atlas that is not ready. The page must be reusable before anything makes its atlas ready, so both free pages stay blocked, and the ring never produces again. The investigation earlier recorded a cold `GpuBoundsPublication.AtlasBufferReadiness` failure at startup, which fits this path.

**Open question.** `VkDataBuffer.TryEnsureReadyForRendering` queues a deferred upload when a synchronous upload is not allowed, and the drain calls `PushData`. That upload should eventually make the atlas ready, but it never did in a stalled session. A lost queued drain or a `PushData` early exit would explain it. The upload-stage trace (`XRE_UPLOAD_STAGE_LOGGING`) uses the Vulkan log category, which is not written to the session log files, so the trace did not show it.

**Diagnostics added.** `GPUPhysicsChainDispatcher.CaptureOutputPageStallDiagnostics()` keeps the first and latest page snapshots of a stall: after 60 consecutive busy attempts, then every 1,000 attempts. `CaptureOutputPageStates()` now reports `KnownProducerFailure`, `FailureRecoveryRequested`, and a nested `NativeReuse` record. The harness saves both on failure. The `arm_vulkan_desktop_frame_fault` MCP tool fails one upcoming desktop frame at a chosen phase boundary.

## Committed bound version and proxy

Status: implemented and live-validated. Unit tests are an open test item. Laptop, 2026-10-08.

**Defect.** The committed CPU bound of each covered renderer used the global `ProducerEpoch` as its generation, and each page commit notified every renderer. Every covered mesh therefore took the `RenderableMesh` reconcile slow path in every frame, and its CPU BVH entry moved. A baseline probe counted 4,000 tree moves per stats interval with 2,006 tree items.

**Change.** The committed bound is now an enlarged proxy of the exact bound (`PhysicsChainCommittedSpatialProxy`) with a per-renderer version. A new page keeps the prior proxy and version while the proxy contains the exact bound. A commit notification carries a changed-bound flag, and `RenderableMesh` reconciles only a changed bound. Covered shadow casters still advance the shadow output revision on each output. A version alone would not help this benchmark, because every root moves on every tick and the exact bound changes on almost every page.

**Validation.** Temporary counters in a healthy session, over about 43 seconds of measurement: every renderer kept its proxy and version on every page (about 1.44 million keeps). There was no changed-bound notification, no reconcile slow path, and no CPU tree move; the octree move count was 0. The counters are removed. `ReconcileCommittedWorldBounds` still runs about four times per mesh per frame, and each call queries the bound; this is the "resolve once per frame" todo item.

A/B windows at 2,000 chains, 20 seconds each. The baseline binary restored the old behavior with a temporary edit. Values are means in milliseconds per call.

| Build | Windows | Hz | p95 ms | Update | Collect | Swap | Render |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 4 | 22.6 | 51.9 | 28.9 | 25.2 | 15.0 | 28.8 |
| Version and proxy | 6 | 23.5 | 51.1 | 30.5 | 23.3 | 13.3 | 29.5 |

Two windows of the new build also had a slower update phase, which this change does not affect, so the machine was slower in those windows. Relative to the update phase, collect fell by about 12% and swap by about 16%. Render did not change. Render plus swap (about 42 ms) is still the critical path, so the frame rate rose only about 4–8%.

**Mesh sharing.** `-MeshSharing Unique` formed 2,000 indexed instance groups for 2,000 payloads, and `Shared` formed one. The two modes measured the same in both builds, so instancing does not change this CPU-bound frame.

## Measurement notes

- Window-to-window variation is 15–25%. Do not claim a gain smaller than this from frame rate alone.
- Nested stage timers are not additive, and wall times can include waits.
- `XRE_WORLD_TICK_TELEMETRY` is read at startup. A runtime override has no effect.
- Set MCP dispatch to `MainThread` after every restart. Direct dispatch can corrupt scene setup.
- Use the full-grid camera at (0, 400, 450). The older (0, 260, 310) camera clipped front rows.
- The benchmark controller stops each run after 120 seconds. Wait for gradual teardown before checking restoration.
- Use focused getters on `RenderableMesh`. Broad `get_object_properties` calls can time out.
- Run `rdc close` before each `rdc open`. The RenderDoc layer and replay module must match; a process-only `VK_IMPLICIT_LAYER_PATH` override selected the installed 1.41 layer.
- WPR needs an elevated shell (error `0xc5585011` otherwise). Nsight API-only traces work without elevation.
- GPU clock samples were sometimes empty, and clocks moved between P0 and P5.
- `invoke_method` serializes at most 20 public properties of a struct or object. It now lists the rest in `omittedProperties`. A 21st dispatcher property once hid `OutputPageDiagnostics` from the harness, so expose new diagnostics through methods.
- Pass `-LadderChainCounts` as one comma-separated string. `pwsh -File` does not pass arrays to a script.
- To compare two binaries, build each into its own named session and alternate windows between them. For a baseline, build from a temporary edit and remove the edit right after the build. Compare phase times relative to the update phase when a change does not touch it: the update phase shows machine slowdowns, for example thermal ones.
- Routine `Tools/Limit-AgentValidation.ps1` removes the build output of every stopped session, so a later `Start -NoBuild` fails with "The isolated build did not create ...". Run it only after the last `-NoBuild` restart of the task.
- After a session name is rebuilt, `Start -NoBuild` with that name can resolve to an older session folder with the same name. Use a new session name for each binary.
- A session can latch into rejecting every frame at startup (see the open faults). Check completed frame outcomes before reading any counter, because a latched session shows failure behavior, not the steady state. The harness readiness gate does this for timed windows.
- MCP tool calls during a 2,000-chain benchmark can take several seconds and slow the frame. Keep probes out of timed windows.

The evidence files were disposable runs under `Build/_AgentValidation/`, and most have been pruned. This document is the durable record.

## Next steps

Follow the [todo priority order](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#priority-order): ceiling prototypes, the shared versioning contract, the physics steady state, and the change-driven renderer path with the serial swap. Measure each change in both mesh modes. The evidence from 2026-10-08 points to these items first:

- The render callback (about 29 ms) and the serial swap (about 13 ms) are the critical path at 2,000 chains.
- `ReconcileCommittedWorldBounds` runs about four times per mesh per frame and queries the bound each time. This is the "resolve committed bounds once" item.
- Count the silent output-page producer failures, and find why the startup compute operation context can have no resource-planner generation.
- Run the controlled desktop ladder in both mesh modes when the desktop is available.
