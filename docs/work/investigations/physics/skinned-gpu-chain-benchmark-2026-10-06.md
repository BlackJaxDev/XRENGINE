# Skinned GPU Chain Benchmark Investigation

Status: Open. The 100 Hz target is not met.
Last updated: 2026-10-13

[Design](../../design/physics/physics-chain-steady-state-cpu-design.md) ·
[Code items](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md) ·
[Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale) ·
[Performance guide](../../../developer-guides/rendering/physics-chain-performance.md) ·
[Critical-path mapping](physics-chain-critical-path-mapping-2026-10-12.md)

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
| 10-12 | Laptop | Baseline at `beb7d7896` (branch merged with integration). | 18.4–21.4 | 55–70 |
| 10-12 | Laptop | Committed spatial query cache; see [lock traffic](#lock-traffic-and-the-committed-spatial-query-cache). | 21.7–23.8 | 48–58 |
| 10-12 | Laptop | Publisher unchanged-row gate, shared and unique meshes; see [unchanged-row gate](#publisher-unchanged-row-gate). | 27.9–30.1 | 38–43 |

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
| Locks | A query resolve in `GPUPhysicsChainDispatcher.TryGetCommittedSpatialBounds` held the renderer's committed spatial query lock while it took the scene lock, and scene publication held the scene lock while `TryCaptureGpuBoundsSource` waited for the query lock. The update thread (a root-bone render-matrix notification) and the swap deadlocked; see [lock-order defect](#lock-order-defect). `TryCaptureGpuBoundsSource` now only tries the query lock. |
| MCP | Calls stalled for more than 60 seconds during the 2,000-chain benchmark; none of nine windows on 2026-10-13 afternoon was accepted. The harness sets MCP dispatch to `MainThread`, so each call is an app-thread job. `Engine.ProcessPendingAppThreadWork` drained that queue only between update iterations and only when no render frame dispatched. The update iteration (about 31 ms) and the render frame (about 27 of 33 ms) had almost the same period, so the check stayed inside render frames for many iterations in a row. A request trace showed a queued `invoke_method` call that waited 118 ms even before the benchmark. Work that render frames have deferred for 50 ms now starts anyway (`AppThreadWorkDeferralPolicy`, counters in `Engine.CaptureAppThreadDispatchDiagnostics()`, tests in `AppThreadWorkDeferralPolicyTests`). Two rejected designs: an update-thread wait for the end of a render frame loses wakeups (a `ManualResetEventSlim` reset by the next frame) and can stall both threads when a render frame waits for an update-thread task. |
| MCP | The MCP server stopped itself during a session-only `set_editor_preference` call, and the harness failed with "An error occurred while sending the request". Preference composition copies the global values and then applies overrides and session values; the `--mcp` switch is a session value, so `McpServerEnabled` was false for a short time. The server reacted to each property change after a fixed 25 ms delay and dropped a second change while a refresh was pending. The server now acts only on settled values (`Engine.IsComposingEditorPreferences`, `Engine.EditorPreferencesChanged`), and `McpServerStateRefreshScheduler` repeats a refresh until no request arrived during it (`McpServerStateRefreshSchedulerTests`). The `McpServerHostProtocolTests` reflection call, stale since a trace parameter was added on 2026-10-07, now passes that parameter. |
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
| The collect thread stopped 20 seconds after a fresh start (desktop, 2026-10-13, one of four starts). `get_time_state` showed a terminal fault: `InvalidOperationException` "Render object 'XRDataBuffer' requested a backend operation before an active render owner was available." The stack is `VisualScene3D.ProcessPendingRenderableOperations` → `GPUScene.Add` → `VerifyUpdatingBufferSize` → `EnsureLodTransitionBufferCapacity` → `SyncLodTransitionBufferFromGpu` → `XRDataBuffer.MapBufferData`. When the GPU scene grows its command capacity after a GPU LOD dispatch has set `_lodTransitionGpuWritePending`, the sync maps the LOD transition buffer on the collect thread, where no render owner exists. That code is the same at HEAD. | Unresolved. A restart of the same binary was healthy. | Code item in the [backlog](../../todo/physics/physics-chain-backlog-todo.md#gpu-residency-and-submission). |

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

## Lock traffic and the committed spatial query cache

Status: implemented and live-validated on 2026-10-12 (laptop). Unit tests for the cache are an open test item.

**Evidence.** A sampled CPU trace (`-CpuTraceSeconds 15`) of the HEAD build put 66% of the collect thread, 21% of the render thread, and 32% of the update thread in `Monitor.Enter_Slowpath`. On the collect thread, 41% of the samples were under `GPUPhysicsChainDispatcher.TryGetCommittedSpatialBounds` and 12% under `TryCaptureGpuBoundsSource` in the swap publisher. Each committed bound query took about six shared locks: `_outputPageSync` twice, the global `_cpuSpatialMaterialSync`, `GPUScene._lock` two or three times, and the per-material contract cache lock. All 2,000 benchmark chains share one material, so every query on every thread took the same material lock. Collection made about 10 queries per renderer per frame: reconcile, canonical-collection checks (two per mesh), culling and octree volumes, the primary shadow view, and root-bone render-matrix notifications during both render-matrix publications.

A runtime contention trace (`Microsoft-Windows-DotNETRuntime:0x4000:4` with `ContentionStart` owner thread IDs, parsed with TraceEvent) measured the real blocking waits over 10 seconds:

| Waiter | Owner | Wait | Cause |
| --- | --- | ---: | --- |
| Fixed update (`PhysicsChainWorld.FixedTick`) | Update thread (`LateTickExclusive`) | 2.67 s, 12.4 ms each | World tick gate. Not on the render critical path. |
| Collect (bound queries) | Render (`CommitOutputPage` → `NotifyCommittedSpatialOutputChanged` under `_outputPageSync`) | 0.38 s | Per-renderer commit notifications sent under the page lock. |
| Collect (`PublishRenderMatrices`, collect and swap) | Update (`TransformHierarchyStore.Process`) | 0.22 s, about 3 ms each | Publication waits for the update pass gate. |
| Transform propagation workers | Each other | about 0.3 s | Worker queue lock. |

The blocking waits on the collect thread were therefore about 3 ms per frame. The rest of the sampled `Monitor.Enter_Slowpath` time was lock-word traffic between cores and sampler bias: the EventPipe sample profiler suspends threads at safe points, so it over-attributes time to lock entry and to `BulkMoveWithWriteBarrier` (large struct copies). The update thread's 26% in `SubmitData` had no contention event at all; world-tick telemetry measured that bridge stage at about 1.4 ms per late tick. Use the sampled trace to find call paths, and use phase timings or scope timers for cost. GC was not a cause: 2.6 gen0 collections per second and 1.4% pause time (`dotnet-counters`).

**Change.** `GPUPhysicsChainDispatcher.TryGetCommittedSpatialBounds` keeps each renderer's last result in `XRMeshRenderer.CommittedSpatialQuery` and reuses it while its witnesses are unchanged. A reuse takes only the renderer's own lock. The witnesses are a dispatcher spatial epoch, the usability of the published page (read without the page lock), coverage, the bone buffer generation, the mesh envelope, the scene's renderer-command material publication generation (read without the scene lock), and the drawn material snapshots and contracts. The epoch advances only when the committed bounds sources change, when a renderer's committed query state changes between pages, and on invalidation, retirement, or reset. `CommitOutputPage` keeps an unchanged bounds-source map and sends renderer notifications after it releases `_outputPageSync`. `TryCaptureGpuBoundsSource` reads the cached query first. `PhysicsChainMaterialBoundsEvaluator.Evaluate` reads an immutable contract entry without a lock.

**Validation.** `GPUPhysicsChainDispatcher.CaptureCommittedSpatialQueryDiagnostics()` counts misses by reason. Over 10 seconds of the 2,000-chain steady state, no query missed; all misses happened during startup, before the scene route existed. The epoch advanced 15 times in the whole session. Targeted unit tests: 422 passed; the 23 failures are the known stale tests.

| Build | Windows | Hz | p95 ms | Update | Collect | Swap | Render |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| HEAD | 3 | 18.4–21.4 | 57.5–69.9 | 31.0–35.3 | 23.9–27.6 | 16.5–18.4 | 30.3–36.2 |
| Query cache | 3 | 21.7–23.8 | 47.6–57.7 | 30.1–32.4 | 15.8–17.8 | 15.0–16.9 | 27.1–30.0 |

Collect fell by about 30%, but collect is not on the critical path: the collect thread now waits about 11 ms per cycle for render. Render plus swap (about 44 ms) still sets the frame interval.

**Limit (cache).** The epoch is dispatcher-wide. A proxy refit of one renderer invalidates every cached query. This benchmark refits almost never, but a scene with many chains that travel far would resolve each query about once per frame. Per-renderer change stamps would remove this limit.

<a id="lock-order-defect"></a>**Lock-order defect (found and fixed 2026-10-13).** The cache added a lock-order inversion. A resolve holds the renderer's query lock while it takes `_outputPageSync`, `_cpuSpatialMaterialSync`, and the scene lock (`GPUScene.TryGetRendererCommandMaterialGeneration`). Scene publication holds the scene lock in `GPUScene.SwapCommandBuffers` and called `TryCaptureGpuBoundsSource`, which waited for the same query lock. A session stopped making progress at 11:07 during a benchmark stop: the editor used almost no CPU, every MCP call timed out, and the logs stopped. A mini dump showed the update thread in `RenderableMesh.RootBone_WorldMatrixChanged` → `TryGetCommittedSpatialBounds` waiting for the scene lock with the query lock held, and the collect thread in `AdvancedGpuScenePublisher.Publish` → `TryCaptureGpuBoundsSource` waiting for the query lock with the scene lock held. The render thread waited for the next frame signal. `TryCaptureGpuBoundsSource` now uses `Monitor.TryEnter` and takes the existing locked path when the query lock is busy; that path does not take a query lock. The rule is in the [output architecture](../../../architecture/physics/physics-chain-output-and-readback.md). Some earlier MCP timeouts and session stops may have had this cause. A unit test for the rule is an open todo item.

## Publisher unchanged-row gate

Status: implemented and live-validated on 2026-10-12 (laptop). The [advanced render pipeline](../../../architecture/rendering/advanced-render-pipeline.md) owns the contract.

**Defect.** `AdvancedGpuScenePublisher.PublishCore` planned every command (`TryBuildAndPreflightWholeScenePlan`, about 7 ms at 2,000 chains) before its reuse check. The reuse check then failed in every frame, because `AdvancedGlobalResourceCapture` writes the cascade's `LastRenderedFrame` into each directional shadow record, and moving covered casters re-render the cascade on each physics output. So each swap planned and committed the whole scene.

**Change.** Before the whole-scene plan, a gate compares each row's inputs with the retained plan of the last accepted publication: the command row and source lookup, the render snapshot, the primitive mesh and material versions, the raster state, the CPU cull-bound version, frame continuity, and the registration. It checks each material header one time. One read of the dispatcher spatial epoch covers all covered rows. When only global resources changed, the publisher commits the retained submission and deformation-source rows with the global transition and skips the per-command apply, tombstone, and plan-capture passes. Any difference runs the full plan unchanged. `get_render_state` reports `unchangedRowGateHitCount` and `retainedRowCommit`.

**Validation.** At 2,000 chains, every window reused all 2,002 rows (`unchangedRowGateHitCount` 2002) and committed the retained rows (`retainedRowCommit` true), as the shadow-record cause predicts. 42 publisher contract tests pass, 12 of them new (`AdvancedGpuSceneUnchangedRowGateTests`). Windows of 30 seconds, all accepted, values in milliseconds per call:

| Build | Mesh | Hz | p95 ms | Update | Collect | Swap | Render |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Query cache | Shared | 21.7–23.8 | 47.6–57.7 | 30.1–32.4 | 15.8–17.8 | 15.0–16.9 | 27.1–30.0 |
| Gate | Shared | 27.9–30.1 | 37.7–42.5 | 31.2–31.9 | 15.9–16.9 | 6.8–8.3 | 26.5–27.6 |
| Gate | Unique | 28.2 | 40.9 | 31.9 | 16.4 | 7.7 | 28.0 |

The swap fell by about 8 ms, and the frame rate rose by about 30%. The render callback (about 27 ms) is now the largest part of the critical path, and collect waits about 11 ms for it.

**Remaining.** The gate still visits each row in each publication (scene lookups, a render-snapshot copy, and the registration checks), so its cost grows with the row count. Page versions for the command rows would remove that walk.

## Render-thread dispatcher stages

Status: partly done on 2026-10-12 (laptop). The remaining items are in the [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#remaining-render-thread-items).

**Measurement.** `GPUPhysicsChainDispatcher.CaptureStageTimings()` gives the cumulative render-thread time of each dispatcher stage. Two captures 10 seconds apart inside a 2,000-chain window give the cost per frame. These timers corrected the sampled trace: `PollSimulationReceipts` costs 0.07–0.11 ms per frame, not about 3.5 ms.

| Stage (ms per frame) | Query cache and gate | Route reuse, bound-once scene, single binding snapshot | + request in binding, map swap, prior state by reference |
| --- | ---: | ---: | ---: |
| Total | 12.62 | 9.86 | 9.07 |
| Bounds | 4.79 | 3.54 | 3.10 |
| — bounds rows | — | 3.02 | 2.72 |
| — route capture | — | 0.15 | 0.14 |
| — scene copy | — | 0.10 | 0.09 |
| Simulation group | 2.37 | 2.18 | 2.14 |
| Commit | 0.92 | 0.79 | 0.71 |
| Accept inputs | 0.92 | 0.73 | 0.66 |
| Previous palette | 0.81 | 0.72 | 0.67 |
| Palette sources | 0.80 | 0.64 | 0.57 |
| Collect bindings | 0.51 | 0.42 | 0.35 |
| Palette matrices | 0.43 | 0.15 | 0.13 |
| Ownership finalize | 0.38 | 0.11 | 0.11 |
| Poll receipts | 0.11 | 0.07 | 0.07 |

The bounds stage had copied each scene's renderer route table (2,000 entries, under the scene lock) twice per frame, and resolved each binding's render world about four times. Route snapshots now persist until the scene publishes a new route generation.

**Render callback (resolved 2026-10-13).** The question was why the render callback stayed at about 27 ms when the dispatcher fell by 3.5 ms. `RenderCallbackStageTimings.Capture()` now gives the cumulative time of each part of `XRWindow.RenderCallback`. In a 2,000-chain window, the total of 24.85 ms per frame was the sum of three parts with nothing between them: pre-render (dispatcher) 8.78 ms, viewports 6.94 ms, and the Vulkan frame 9.12 ms. The Advanced preparation lock waits (`AdvancedSharedPreparationService.GetCurrentDiagnostics()`, acquire and copy) were 0.3 ms in total over the probe interval. There is no wait that absorbs the saving. That window ran at 31.0 Hz with p95 37.2 ms and render 25.1 ms; the earlier flat readings were window-to-window noise. Later windows showed how large that noise is: the Vulkan frame part varied from 9.1 to 13.7 ms with no change in its code. Judge changes by stage timers, not by single-window frame rates. The Advanced preparation build cost about 5.7 ms per rebuild, of which extraction was about 4.1 ms; Vulkan recording is most of the Vulkan frame part.

**Simulation group and colliders (2026-10-13).** Sub-stage timers split the simulation stage (1.94 ms): tree parameters 0.11, active-work records 0.33, dirty-range uploads 0.45, per-request collider writes 0.53, other uploads 0.19, and the dispatch about 0.33 ms. Each collider write was a separate mapped write with lease lookups and an allocator lock, for 128 bytes. Colliders now go into a dense catalog at their arena offsets; an input bank receives one write of the catalog only when it holds an older catalog version or another buffer. The collider writes fell from 0.53 to 0.05 ms per frame, and the simulation stage from 1.94 to 1.53 ms. The second `EnsureRequestArenaSlices` pass was also removed. The window was accepted with no input-page failure. The catalog protocol is in `PhysicsChainColliderInputCatalog`; `PhysicsChainColliderInputCatalogTests` show that a collider change reaches each of the 8 rotating banks exactly once, that unchanged frames write nothing, and that rollback, arena reallocation, a cleared stamp, and a replaced bank buffer each force the needed writes.

**Session faults (2026-10-12 and 2026-10-13).** Two of about ten fresh sessions latched at startup with the known cold-start fault, and one window failed with a 60-second MCP request timeout while the editor stayed healthy. A restart or a retry with the same binary was healthy each time.

**Validation.** 434 targeted unit tests pass; the 23 failures are the known stale tests. One fresh session latched at startup (input and output page fences failed with `QueueDiscard`, and the dispatcher reported no failure); a restart of the same binary was healthy. This is the known cold-start fault.

## Advanced preparation extraction

Status: partly done on 2026-10-13 (laptop). The remaining items are in the [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#structural-frame-reuse).

**Change.** `AdvancedPreparationExtractor` did the same per-row work twice for each covered skinned draw. It captured the renderer output state (a lock on `_outputPageSync`, a page check, a dictionary lookup, and an envelope compare) once for the material check and again for the pose. It prepared the mesh (`TryPrepareMesh`: two table lookups and two witness checks) in the pre-pass and again in extraction. It also evaluated the material bounds contract for each row, although the benchmark chains share one material. Now each covered draw captures its output state once; extraction reuses the pre-pass mesh slice while `AdvancedGpuDeformationResources.MeshSliceRevision` and the mesh geometry are unchanged; consecutive rows with one shared mesh use one preparation; and `PhysicsChainMaterialBoundsContractMemo` evaluates each published material state once per `Build`.

**Result.** `AdvancedSharedPreparationService.GetCurrentDiagnostics()` `SharedTelemetry`, two reads 10 seconds apart inside 2,000-chain windows, divided by the rebuild count:

| Build | Windows | Extraction (ms per rebuild) | Build (ms per rebuild) | Build allocations |
| --- | ---: | ---: | ---: | ---: |
| Before (dispatcher and collider changes) | 4 | 4.11–4.74 | 5.66–6.41 | 0 B |
| Extraction changes | 2 | 3.68–3.81 | 5.48–5.58 | 0 B |

Extraction fell by about 0.7 ms per rebuild (about 16%). The mapping estimated 1.1 ms for these three items. In both new windows the benchmark stopped just after the second read, so the harness rejected the windows. The reads still cover steady-state frames: 27–28 rebuilds per second, the same as the frame rate. After the MCP fixes, two harness windows were accepted (16.7 and 17.2 Hz), but the laptop was thermally limited: the GPU stayed at 83 °C in power state P5, and the update phase, which these changes do not touch, rose from 31 to 42–44 ms. Those frame rates cannot be compared with the earlier windows.

**Extraction parts.** `XRE_ADVANCED_EXTRACTION_SUBSTAGE_TIMERS=1` times each part of an extraction row (`AdvancedPreparationExtractor.CaptureExtractionSubStageTimings`). Milliseconds per build at 2,000 chains, shared mesh, GPU clocks locked:

| Part | Before, window 1 | Before, window 2 | Covered pose fast path and schedule skip |
| --- | ---: | ---: | ---: |
| Mesh pre-pass | 0.78 | 1.07 | 1.12 |
| Route, output state, material check | 1.09 | 1.39 | 1.49 |
| Geometry and candidate | 0.17 | — | 0.22 |
| Mesh preparation | 0.41 | 0.41 | 0.41 |
| Pose capture | 1.18 | 1.27 | 0.71 |
| History and arena slice | 0.37 | 0.43 | 0.40 |
| Schedule and job | 0.56 | 0.86 | 0.24 |
| Extraction total (service ticks) | 3.94 | 4.90 | 3.61 |

A later build added the lock-free renderer state reader. Two windows: route part 1.15 and 1.10 ms, pose 0.63 and 0.60 ms, schedule and job 0.30 and 0.28 ms, mesh pre-pass 0.96 and 1.37 ms; extraction total 3.17 and 2.85 ms, build 4.96 and 4.89 ms.

- Parts that only read scattered objects (the mesh pre-pass and the route part) vary by up to 45% between windows of the same binary. Each chain owns its `XRMesh` in the shared-mesh mode too ("shared" means identical content that the deformation static inputs intern), so these parts take one or more cache misses for each row.
- The covered pose fast path skips `XRMeshRenderer.TryPrepareDeformationInputs` for a covered renderer without active blendshapes. The retained envelope already proves the mesh, geometry revision, bind root, and bone layout; only the pose version is read. The full path still runs for blendshape meshes and until the renderer records GPU-owned bones, so a later loss of coverage resets the CPU palette seed state. The pose part fell by about 0.5 ms.
- Covered rows no longer call the CPU animation scheduler: their palette comes from the physics output page, and the scheduler returned the constant bone tier 0. The schedule part fell by 0.3–0.6 ms.
- The lock-free renderer state reader did not measurably change the route part (1.10–1.15 ms against 1.09–1.49 ms). The page lock and the renderer dictionary were not its cost. The remaining cost is the per-row read of each renderer and mesh: the bone-buffer generation, the mesh geometry revision, bind root, and bone layout in the envelope check, and the state copy. The reader stays: it removes about 2,000 acquisitions per frame of the lock that collection also takes.
- Retained row templates for the mesh path would still validate the same scattered mesh state (skinning buffers, blendshape buffers, and their revisions), so they would save only the lookups. A real saving needs meshes and renderers to report their changes in dense versions (the shared versioning contract in the todo).
- Comparing two binaries: `Tools/Manage-McpEditorSession.ps1` keeps no build output of stopped sessions (`$retainedStoppedSessionBuildCount = 0`), so a `-NoBuild` restart of an older session fails after any other session starts. These windows compare one new window with two windows of the old code instead.

**Validation.** `PhysicsChainMaterialBoundsContractMemoTests` (7 tests: one evaluation per pass for a shared material, a new evaluation after a clear, a changed value, a rejected slot, a rotation effect, more materials than entries, and a null material) and three `AdvancedGpuDeformationResourceTests` (the revision advances on frame open and static reset, an unsupported mesh keeps it, and the reuse checks) pass. Frame outcome counts showed no new deferred frames: the deferred count stayed at the 89–101 startup deferrals of each session.

## Vulkan visibility family

Status: in progress on 2026-10-13 (laptop). The remaining items are in the [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#structural-frame-reuse).

**Measurement.** `XRE_S13A_PUBLICATION_TELEMETRY=1` and the `get_s13a_publication_telemetry` tool give the cumulative calls and ticks of each Advanced family step (`S13aAdvancedFamilyStep`). Read two captures inside a window. The recording validation stage (`EVulkanCpuStage.PrimaryAdvancedRasterValidationOperation`, read with `RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCpuStageTelemetry`) records only with `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`.

**Records by reference.** `VulkanPreparedStableBinStream` copied each frozen record (about 2 KB) by value in most of its per-frame passes. Template retention, manifest resolution, submission-plan sealing, raster payloads, native-state resolution, and raster pipeline preparation now read records and headers by `ref readonly`. Milliseconds per frame at 2,000 chains:

| Step | Before (1 window) | By reference (2 windows) |
| --- | ---: | ---: |
| Submission plans | 0.24 | 0.02–0.03 |
| Template retention | 0.19 | 0.01–0.02 |
| Manifests | 1.01 | 0.70–0.79 |
| Raster realization | 0.50 | 0.30–0.43 |
| Raster pipelines (two calls) and header validation | 1.96 | 1.53–1.90 |
| Geometry payloads | 1.51 | 1.48–1.64 |
| Family total | about 4.6 | about 3.4–4.0 |

The geometry payload pass did not change: its cost is the per-payload work, not record copies. Individual steps vary between windows of one build (one later window measured manifests at 4.1 ms), so compare totals over several windows.

**Native vertex-source check once per bin (ruled out).** A trial checked the native lifetime of the shared deformation output range once per build at seal, and once per bin header during recording, instead of once per record. A/B windows with `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`: the recording validation stage took 0.454 ms per raster phase before and 0.456 ms after, and the geometry payload step 1.48 and 1.45 ms. The native check (a lifetime lock and a wrapper lookup) is not the cost. The stage still costs about 0.9 ms per frame (two raster phases, 2,000 records each); the likely cost is the copy of each record's 660-byte geometry closure, which a positional record property cannot return by reference, and the value checks. The trial was reverted. The remaining options are per-header validation of sealed records and slimmer records (main todo, structural frame reuse).

**Native state and key memo.** In `TryBuildVisibilityGeometryStream`, consecutive payloads with equal `VisibilityRecordInputs` (cull mode, instance count, index count, raster class, coverage, topology, skinned flag, producer, binding geometry, and prepared vertex source) reuse the previous native state and bin key. The geometry payload step fell from 1.45–1.64 to 1.36–1.41 ms.

## Visibility content stability

Status: measured on 2026-10-13 (laptop). This is step 1 of the [structural frame reuse contract](../../design/physics/physics-chain-steady-state-cpu-design.md#structural-frame-reuse-contract).

**Question.** Does Advanced extraction give the same visibility content in each steady-state frame? If it does, the per-row work of extraction and of the Vulkan visibility family repeats the same result in each frame, and a witness can let consumers keep it.

**Change.** `AdvancedVisibilityContentWitness` compares the visibility columns of each build with the last compared build: payloads, producers, indirect ranges, range payload order, and deformed-arena slices without their frame slots. `StableVisibilityContentGeneration` changes only when a column changes. `GetCurrentDiagnostics()` reports the counts in `VisibilityContent`.

**Result.** Two reads 10 seconds apart inside accepted 2,000-chain windows:

| Mesh | Builds compared | Unchanged | Columns changed | Build (ms per rebuild) | Extraction | Hz |
| --- | ---: | ---: | --- | ---: | ---: | ---: |
| Shared | 316 | 316 | none | 4.45 | 2.75 | 29.5 |
| Unique | 311 | 311 | none | 4.65 | 2.91 | 27.2 |

- Every steady-state build gave the same content. Only frame bindings change between frames.
- The comparison cost was within noise: the build took 4.45–4.65 ms, against 4.89–4.96 ms in the earlier windows. The build allocated 0 bytes.
- The Vulkan visibility records could not be kept as they were. Each record and bin key contained the scene native generation (`VulkanAdvancedSceneResourceRuntime.NextNativeGeneration` advances on each scene publication preparation) and the deformation output buffer. Desktop Vulkan has two frame plans and the deformed arena has three frame slots, so each frame plan sees all three output buffers in turn. The static vertex and index slices are not frame bindings: they come from the resident geometry cache and keep their identity. The [next section](#vulkan-visibility-stream-reuse) removes the two frame bindings.

## Vulkan visibility stream reuse

Status: implemented and live-validated on 2026-10-13 (laptop, GPU clocks unlocked). The [advanced render pipeline](../../../architecture/rendering/advanced-render-pipeline.md#vulkan-visibility-stream-reuse) owns the contract.

**Change.** Records and bin keys no longer contain the scene native generation or the deformation output buffer. A deformed record keeps only its vertex base, and the raster pipeline step binds the frame's deformation output into each deformed bin header. The frame plan reset keeps a valid seal. The next seal keeps the stream when `VulkanVisibilitySealSignature` and an exact comparison of the visibility columns are equal, and it applies only the frame bindings. Range members are validated once for each sealed content, and recording checks each bin once instead of each record. `AdvancedVisibilityStreamReuseTelemetry.Capture()` counts reuses and reseals by first reason.

**Result.** Two reads 10 seconds apart inside accepted 2,000-chain windows. Before: the content-witness windows of the same day.

| Build | Mesh | Hz | Update | Collect | Swap | Render | Render waits for collect |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Before | Shared | 29.5 | 34.0 | 17.0 | 7.9 | 26.1 | 6.5 |
| Before | Unique | 27.2 | 36.9 | 17.7 | 8.4 | 28.5 | 6.1 |
| Stream reuse | Shared | 33.0 | 31.8 | 15.7 | 9.4 | 20.9 | 7.1 |
| Stream reuse | Unique | 34.2 | 30.5 | 15.1 | 8.4 | 20.9 | 6.4 |

| Family step (ms per frame) | Before | Stream reuse |
| --- | ---: | ---: |
| Bin sealing (geometry stream, manifests, submission plans) | 2.1–2.2 | 0.07 |
| Raster pipelines and member validation (two calls) | 1.5–1.9 | 0.08 |
| Template retention | 0.01–0.02 | 0.05 |
| Raster realization | 0.30–0.43 | 0.44 |

- The stream was kept in every steady-state frame: 384 of 384 seals with a shared mesh and 408 of 408 with unique meshes. The reseals since session start came from benchmark spawn and teardown (publication 92, submission 6, deformation output 6, no retained stream 2, columns 1).
- Render time fell by about 5 ms (26.1 to 20.9 ms with a shared mesh, 28.5 to 20.9 ms with unique meshes). The recording validation stage was not measured, because `XRE_VULKAN_RECORDING_PROFILE_DETAIL` was not set.
- The reuse path allocated 0 bytes. Other family steps allocate about 46 KB per frame, before and after this change: native compute about 6.5 KB and native descriptors about 4.6 KB per call (three calls per frame), the late closure about 1.5 KB, and the target closure about 290 B per call, which allocates its expected attachment arrays in `TryValidateAdvancedVisibilityTarget`.
- Render now waits 6.4–7.1 ms for collect. Collect takes 15–16 ms. (Correction, 2026-10-13: this wait is outside the render phase. It is the wait for the serial swap, and it is shorter than the swap. Collection is not on the critical path; see [frame handoff](#frame-handoff-and-stage-telemetry).)
- Visual check: wide, middle, and close captures of the running chains show the full grid and chains bent by the simulation. The session logs have no Vulkan validation message or plan exception. The 8 rejected frames were mesh readiness retries during startup and spawn (`visible-mesh-cold-admission`).

## Collection, dispatcher, and allocation mapping

Status: mapped on 2026-10-13. The telemetry build and the low-risk allocation fixes are done; see [frame handoff and stage telemetry](#frame-handoff-and-stage-telemetry). A read-only pass of five mapping agents and one critic covered collection, CPU bone work, the dispatcher bounds stage, dispatcher per-request work, and the Vulkan family allocations. The full result is disposable evidence (`collect-dispatcher-alloc-map-2026-10-13.json` in the run reports folder). The findings that later work needs are below.

**Collect thread, sampled trace.** One 2,000-chain window (shared mesh, 28.4 Hz with tracing), as a share of the collect thread. The sampler overstates lock entry, so confirm each part with stage timers.

| Part | Share |
| --- | ---: |
| Primary directional shadow collection of covered meshes (`BeforeAdd`, culling volume reads, `ComputeShadowCasterCommandSetSignature` 6%) | 26% |
| `VisualScene3D.RefreshCoveredCommandSources` (several committed-bound queries with locks for each renderer) | 15% |
| Main view `CanUseCanonicalGpuCollection` for each mesh | 9% |
| Swap on the same thread: `PublishRenderMatrices` (covered `RootBone_WorldMatrixChanged` handlers 8%) | 16% |
| Swap on the same thread: publisher unchanged-row gate walk | 12% |

**Findings.**

- The primary directional shadow viewport may be collected in every frame with no consumer. On the Vulkan atlas path, `EnsureCascadeShadowResources` releases the cascade receiver textures, so `ShouldCollectPrimaryShadowViewport` is always true, but the atlas submits the primary tile only when `NeedsPrimaryDirectionalShadowMap()` is true. Confirm at runtime with a counter of primary collections against primary tile renders. If it is confirmed, gating the collection is the largest collect saving (estimated 2.5–4 ms). Confirmed on 2026-10-13: the collection costs 6.5–7.4 ms of collect per frame on the desktop and has no consumer.
- No stage timer splits the collect phase, and no counter measures the `TransformHierarchyStore._passGate` wait at pre-collect or at the swap. Add these before collect changes.
- The bones of covered chains (12,000 non-root bones) are propagated, published, and notified in each tick, but no steady-state consumer reads them. They also hold the rest pose, not the simulated pose.
- The dispatcher reads `_publishedOutputPageIndex`, `_committedBoundsSources`, and `_lastPublishedPaletteInputBoundsByRenderer` on the render thread without `_outputPageSync`, while collect-thread queries can clear them through `InvalidateFailedPublishedPages` (also `TryBeginSimulationReceipt`). These races occur only on page failure or arena reset. Fix them before other dispatcher changes.
- The Commit stage timer includes `backend.CommitBatch`, so the 0.71 ms is not all notification work.
- The 32-bit palette layout signature is a rebuild witness that can collide, and each of the four output pages keeps its own signature, so one binding change rebuilds the layout four times.
- About half of the 46.6 KB per frame in the Vulkan family comes from one line: the generated record equality of `VulkanAdvancedScenePublicationState` in `TryPrepareAdvancedVisibilityFamily` boxes Silk.NET handles (about 3.3 KB for each of seven compares). Most other owners are also generated equality over Silk.NET handles (native closure compares, `Extent3D`, compute pipeline), interpolated resource names, target signature arrays, and a readiness closure. `TryValidateAdvancedVisibilityTarget` runs before the TargetClosure probe, so its arrays are in the unprobed bytes.
- Collect-thread allocations outside the Vulkan family: `XRViewport.CollectVisible` calls `GetUpdatingCommandCount` (LINQ `Sum` over a dictionary) with all result consumers commented out.

**Planned order.** One telemetry build first (collect stage timers, `_passGate` waits, commit split, primary-shadow consumption counter) together with the low-risk allocation fixes. Then the primary shadow gate (if confirmed), the low-risk transform store changes on the swap, the persistent covered row store (stage A), covered render-matrix handler removal, the dispatcher race fixes, the low-risk dispatcher simulation items, notification changes, the exact binding-set version, the bulk previous-palette copy, retained bounds rows, and the remaining allocation fixes. Dormant bones and the row store stage B come last, behind measurement, because most of their saving is on the update thread or they carry high risk. Re-measure collect against render work after each collect step: when collect falls below render work (about 14 ms), more collect savings do not shorten the frame.

## Frame handoff and stage telemetry

Status: measured on 2026-10-13 (desktop, Ryzen 9 7950X3D, RTX 3090, GPU clocks not locked). The telemetry build adds always-on counters; the [performance guide](../../../developer-guides/rendering/physics-chain-performance.md#frame-loop-profiling-method) lists them.

**Change.** The build measures only. It adds these counters:

- Collect and swap stage timers (`CollectVisibleStageTimings`). The collect and swap totals equal the phase totals exactly (`phaseCrossCheck` is 0 in every window).
- Frame handoff counters: render time until the collect thread is released, and the frames in which collect finishes after that release.
- Transform store pass-gate counters (`TransformHierarchyStore.GetPassGateTelemetrySnapshot`) for the pre-collect publication, the swap publication, and the update-thread `Process`.
- A split of the dispatcher Commit stage.
- Directional shadow consumption counters (`DirectionalShadowConsumptionTelemetry`).

The harness reads all of them in every window (`stageDifferences`). The build also has the low-risk allocation fixes from the [mapping](#collection-dispatcher-and-allocation-mapping).

**Desktop baseline.** HEAD before the telemetry build, two unobserved windows.

| Mesh | Hz | p95 ms | Update | Collect | Swap | Render | Render waits for collect | Collect waits for render |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 39.3 | 31.5 | 32.4 | 14.8 | 7.3 | 18.8 | 6.6 | 3.4 |
| Unique | 38.3 | 31.3 | 31.6 | 14.1 | 7.5 | 19.3 | 6.7 | 4.6 |

A probed baseline window divided the render callback (18.7 ms) as follows: pre-render (dispatcher) 8.5 ms, viewports 5.4 ms, and the Vulkan frame 4.7 ms. The Advanced preparation build took 4.3 ms per rebuild, of which extraction was 2.6 ms.

**Critical path.** The Vulkan frame loop releases the collect thread just after queue submit (`MarkRenderFrameReadyForCollect`). The collect thread then runs the serial swap, and the render thread waits in `EngineTimer.WaitToRender` for the swap to publish. That wait is `render_wait_for_collect`. The frame interval is therefore the longer of "render until release" and "collect", plus the swap. The evidence:

- In the cleanest telemetry window, render until release took 19.0 ms and the swap took 7.6 ms. Their sum, 26.6 ms, matches the mean interval of 26.8 ms (37.3 Hz).
- Collect finished after the release in only 3.8–8.9% of frames.
- In the baselines, the render wait (6.6–6.7 ms) is shorter than the swap (7.3–7.5 ms). That is possible only when collect finishes first. The laptop windows of the same day show the same pattern (wait 6.4–7.1 ms, swap 8.4–9.4 ms).

The statement in [stream reuse](#vulkan-visibility-stream-reuse) that collection is on the critical path was a misreading. The [frame-loop phase totals](#frame-loop-phase-totals) of 2026-10-08 had already found the correct cycle.

**Results.** Four telemetry windows ran on the final build. Unity shader compilation (34 compiler processes) and an editor session that another tool started ran at the same time. The rates fell from 37.3 to 32.7, 31.6, and 16.2 Hz, and in the last window every phase on every thread doubled. Use the absolute values below as upper bounds. The proportions agree across the windows. Values are milliseconds per frame from window 1 (shared mesh, 37.3 Hz).

| Collect part | ms | Notes |
| --- | ---: | --- |
| Transform store publication (pre-collect) | 1.00 | Gate wait 0.30, hold 0.70, notification of about 2,300 owners 0.52 |
| Covered command refresh | 4.76 | 2,000 rows, about 2.4 µs per row |
| GPU bounds eligibility refresh | 0.34 | |
| Primary directional shadow view | 6.49 | 2,004 rows; package preparation 1.51 |
| Main view | 2.83 | GPU collection path |
| Cascades | 0.01 | GPU package path; no CPU collection |
| Collect total | 15.57 | Allocates about 2.9 KB per frame |

| Swap part | ms | Notes |
| --- | ---: | --- |
| Transform store publication | 3.36 | Gate wait 0.86 (contended in 33% of swaps, always by the update-thread `Process`); hold 2.49, of which owner notification 1.82 for about 8,900 owners |
| Advanced scene publication | 3.97 | `AdvancedGpuScenePublisher.Publish` |
| Covered command sources | 0.11 | |
| Viewports | 0.11 | Main view command swap 0.09 |
| Lights | 0.06 | Atlas publication 0.03 |
| Swap total | 7.64 | Allocates about 3.9 KB per frame |

| Render-thread dispatcher stage | ms per call |
| --- | ---: |
| Bounds (rows 3.49) | 3.98 |
| Simulation | 1.38 |
| Commit | 0.98 |
| Accept inputs | 0.75 |
| Palette sources | 0.62 |
| Previous palette | 0.51 |
| Collect bindings | 0.48 |
| Total | 9.60 |

- **Commit split.** Notification collection took 0.31 ms and per-renderer notification 0.62 ms (2,000 notifications per page). Page state took 0.07 ms. The backend batch commit took 0.001 ms. The Commit stage is therefore notification work.
- **Primary directional shadow.** Confirmed in every window. In each frame, `NeedsPrimaryDirectionalShadowMap` returns false. `ShouldCollectPrimaryShadowViewport` still returns true with the reason "no cascade receiver", because the Vulkan atlas path releases the cascade receiver textures. The primary view is collected and swapped (about 2,000 mesh commands), but nothing consumes it: there are no primary tile renders, no legacy renders, and no render-thread reads of its package. It costs 6.5–7.4 ms of collect and 0.015 ms of swap per frame.
- **Update thread.** `Process` holds the pass gate for 3.8 ms per frame (5.1 ms per pass) and notifies about 11,300 owners. It rarely waits.
- **Allocations.** A separate window with `XRE_S13A_PUBLICATION_TELEMETRY=1` measured the Vulkan Advanced family. Values are bytes per frame (360 frames, three family calls each); the "before" values come from the laptop before the fixes. The counts depend on the code path, not on the machine.

  | Step | After | Before |
  | --- | ---: | ---: |
  | `advancedPlanAllocatedBytes` | about 14,000 | 46,600 |
  | Scene publication | 152 | about 22,800 |
  | Native closure capture | 0 | about 12,100 |
  | Native descriptors | 9,524 | — |
  | Late closure | 1,916 | about 1,500 |
  | Raster realization | 944 | — |
  | Target closure | 567 | about 870 |

  The native descriptor rewrite is the largest remaining owner.

**What the results mean.**

- Collect has 3–4 ms of slack. A collect saving alone does not shorten the frame. The primary shadow gate frees 6.5 ms of collect, and that slack lets swap work move into collect.
- The swap is on the critical path. If the Advanced publication (4.0 ms) moves into the collect phase, the swap falls to about 3.6 ms. Collect then grows to about 19.6 ms, which is longer than render (19.0 ms), unless the primary shadow gate comes first. With the gate, collect is about 13 ms. The estimated frame is 19.0 + 3.6 = 22.6 ms (about 44 Hz). This is an estimate.
- Swap transform publication costs 3.4 ms. The owner notification (1.8 ms for about 8,900 owners) is mostly covered-chain bones that no steady-state consumer reads. The wait for `Process` costs 0.9 ms.
- On the render thread, the largest single parts are the dispatcher bounds rows (3.5 ms), the Advanced preparation build (about 4.3 ms), and the Vulkan frame (about 4.7 ms).

**Validation.** Editor and test builds have 0 warnings and 0 errors. The targeted test filter gave 61 failures in 336 tests. All 61 also fail on a HEAD worktree with the same assertions, so the build caused none of them. Visual capture was not repeated, because the build measures only. The allocation fixes keep the equality semantics, and reviewers checked them field by field.

## GPU floor with locked clocks

Status: measured on 2026-10-13 (laptop). The RTX 4070 Laptop GPU was locked at 2,595 MHz graphics and 8,101 MHz memory (P0, 65–68 °C). `Tools/Benchmarks/Sample-GpuFrameTime.ps1` sampled the GPU time of the frame command buffer for each completed frame inside accepted harness windows (shared mesh).

| Chains | GPU ms mean | GPU ms p95 | Hz | Render | Swap | Collect | Update |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 3.84 | 4.34 | 172.2 | 5.21 | 0.68 | 0.23 | 0.41 |
| 1,000 | 5.25 | 5.73 | 50.2 | 15.62 | 4.38 | 7.38 | 14.66 |
| 2,000 | 6.99 | 7.89 | 30.2 | 26.06 | 7.11 | 16.48 | 31.07 |

- The GPU time is about 3.7 ms fixed plus about 1.6 µs per chain. At 2,000 chains it is 7.0 ms, inside the 10 ms budget with about 3 ms to spare. The earlier values (8.8 ms in the Nsight trace, 12.6–15 ms in a throttled session) had lower clocks.
- GPU utilization was 21–32% in the 2,000-chain window. The CPU sets the frame.
- At 100 Hz the GPU would be busy about 70% of the time. A laptop can lose clock speed under that sustained load. Check the clocks in a sustained test when the CPU frame allows 100 Hz.
- CPU budget: at 1 chain, render plus swap is 5.9 ms. At 2,000 chains it is 33.2 ms, so the critical path grows by about 13.6 µs per chain. To reach 10 ms, the cost per chain on the critical path must fall to about 2 µs, or the fixed cost must fall as well.
- The frozen-scene ceiling flag was not built. Freezing Advanced preparation needs the split of stable rows from per-frame bindings, which is the same work as the retained template item. The GPU floor and the chain-count ladder answer the hardware question instead.

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
- To compare two binaries, you cannot alternate `-NoBuild` restarts: each session start removes the build output of every stopped session (`$retainedStoppedSessionBuildCount = 0` in `Tools/Manage-McpEditorSession.ps1`). Build the baseline from a temporary edit, measure it, then rebuild the change; or compare several windows of each binary. For a baseline, remove the temporary edit right after the build. Compare phase times relative to the update phase when a change does not touch it: the update phase shows machine slowdowns, for example thermal ones.
- Routine `Tools/Limit-AgentValidation.ps1` removes the build output of every stopped session, so a later `Start -NoBuild` fails with "The isolated build did not create ...". Run it only after the last `-NoBuild` restart of the task.
- After a session name is rebuilt, `Start -NoBuild` with that name can resolve to an older session folder with the same name. Use a new session name for each binary.
- A session can latch into rejecting every frame at startup (see the open faults). Check completed frame outcomes before reading any counter, because a latched session shows failure behavior, not the steady state. The harness readiness gate does this for timed windows.
- MCP calls in `MainThread` dispatch mode run as app-thread jobs. Render frames can defer them for 50 ms, and a long app-thread job, such as the benchmark spawn, delays them for its full duration. Probe calls during a window took about 1.5–2.5 seconds including the client start. Keep probes out of timed windows.
- `render_wait_for_collect` is the render thread's wait for the serial swap to publish, not a wait inside the render phase. Read the critical path from the handoff counters: the longer of `render_until_collect_release` and collect, plus the swap.
- Check for other heavy processes before a window. On 2026-10-13 (desktop), Unity shader compilation and an editor session that another tool started cut the rate of later windows by up to half on every thread.
- After hours of builds and benchmark runs, the laptop throttles: the GPU reached 83 °C in power state P5, and the update phase grew by about 35%. Check `machineState.gpuClocks` and the update phase before you compare windows, and let the machine cool.
- The cold-start latch (0 completed frames, every frame rejected) hit 4 of 5 fresh starts between 11:50 and 12:40 on 2026-10-13, with old and new binaries. A `-NoBuild` restart was healthy. Check the completed frame count about 45 seconds after a start, before a harness run.
- The `dotnet-trace` sample profiler over-attributes time to `Monitor.Enter_Slowpath` and `BulkMoveWithWriteBarrier`, because it samples at safe points. Before you act on a lock in a sampled trace, confirm it with a contention trace: `dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0x4000:4,Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4"`. `ContentionStart` carries the owner thread ID, and the sample stacks of the owner show what it held the lock for.
- A traced window ran about 6% slower than an untraced window, so its call paths are representative.
- The [profiling method](../../../developer-guides/rendering/physics-chain-performance.md#frame-loop-profiling-method) lists the tools: `Tools/Benchmarks/Invoke-McpStaticMethod.ps1` for stage timers, `Tools/Benchmarks/Summarize-SpeedscopeTrace.py` for sampled traces, and the contention-trace parser source.

The evidence files were disposable runs under `Build/_AgentValidation/`, and most have been pruned. This document is the durable record.

## Next steps

Follow the [todo priority order](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md#priority-order). Measure each change in both mesh modes.

The [stage telemetry](#frame-handoff-and-stage-telemetry) of 2026-10-13 supports this order for the next changes. It is a proposal in the todo's decisions list.

1. Skip the primary directional shadow collection when nothing consumes it. This frees about 6.5 ms of collect.
2. Build the Advanced scene publication during collect instead of in the swap. This removes about 4 ms from the critical path.
3. Remove the swap transform publication costs: the owner notification (1.8 ms) and the wait for `Process` (0.9 ms).
4. Make the render-thread dispatcher changes, starting with the retained bounds rows (3.5 ms).

Repeat the windows on a quiet machine before you compare the frame rate with the baseline.

The earlier notes follow. After the 2026-10-12 changes, the critical path at 2,000 chains is the render callback (about 27 ms) plus the swap (about 7–8 ms). A sampled trace of the query-cache build divided the render callback as follows; the sampler is biased, so confirm each cost before you act:

- `GPUPhysicsChainDispatcher.ProcessDispatches`: about 9–10 ms per frame by stage timers; see [dispatcher stages](#render-thread-dispatcher-stages). The render callback does shrink with it; single windows had hidden the change.
- Vulkan frame transaction, about 10 ms: `TryPreparePrimaryFrameData` about 5 ms.
- Advanced preparation build, about 5.5 ms per rebuild, of which extraction is about 3.8 ms after the [extraction changes](#advanced-preparation-extraction).
- Repeat the extraction comparison in accepted windows on a cool machine.

The read-only mapping pass of 2026-10-12 proposed items for each of these areas with locations, estimated savings, risks, and validation. The [critical-path mapping](physics-chain-critical-path-mapping-2026-10-12.md) keeps its full results.

- Count the silent output-page producer failures, and find why the startup compute operation context can have no resource-planner generation.
- Run the controlled desktop ladder in both mesh modes when the desktop is available.
