# Vulkan Resident Draw Stream Phase 0 Investigation

Last Updated: 2026-08-17

Status: Paused with original-laptop and third-laptop checkpoints; matched
desktop and privileged trace evidence remain open

Owner: Rendering / Frame Scheduling / Vulkan

Implementation plan: [Vulkan Resident Draw Stream And Render Task Pool TODO](../../todo/rendering/optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)

## Outcome

Phase 0 now has a deterministic Release capture contract, per-stage Vulkan CPU
telemetry, valid dense-Sponza original-laptop captures for `CpuDirect`,
`GpuIndirectZeroReadback`, and `GpuIndirectInstrumented`, requested-versus-
resolved evidence for both meshlet modes, and source audits for worker topology,
GPU-scene ownership, and diagnostic readback behavior.

A third-laptop checkpoint was captured on an HP OMEN 17 with an i7-13700HX and
RTX 4070 Laptop GPU. It is intentionally not merged into the matched hardware
table: the checkout commit and stable workload identity differ from the
original-laptop run, and the current capture's workload shape varied materially
between the short and long windows. The checkpoint is useful machine evidence,
but not a hardware-only comparison.

The evidence identifies two immediate architectural facts:

1. Stable `CpuDirect` is still an O(draw) CPU pipeline. At 625 visible draws it
   spends about 4 ms per frame in prepared-cohort matching and rematerializes 59
   unsafe entries every frame. Primary native encoding is already reused, so
   further command-buffer caching cannot remove this preparation/planning cost.
2. `GpuIndirectInstrumented` is not an asynchronous diagnostic path. Its
   buffer-read helper records a one-shot copy and waits indefinitely on that
   submission's fence before returning. The measured ten-second capture made
   3,701 mappings and read 292,304 bytes. It therefore fails the planned
   no-current-frame-wait contract.

Phase 0 is not closed. The 7950X3D/RTX 3090 comparison cannot be collected on
this laptop, kernel CPU/context-switch tracing requires an elevated WPR run,
the three-view screenshot/RenderDoc set was not completed, and both requested
meshlet modes resolve to indirect and then terminate before a valid capture.

## Evidence workspace

- Repository commit at start: `a2d15e430edd68ab9fe06360eb36070ac8e79805`
- Branch: `vulkan-refactor`
- Local run root:
  `Build/_AgentValidation/20260817-132212-vulkan-phase0/`
- Original laptop host: Intel Core Ultra 9 185H, 16 cores / 22 logical processors,
  NVIDIA GeForce RTX 4070 Laptop GPU, NVIDIA driver 581.57, 8,188 MiB reported
  device memory, Windows 11 Pro build 26200, Performance power plan.
- Third laptop checkpoint: HP OMEN by HP Laptop 17-cm2xxx, Intel Core i7-13700HX,
  16 cores / 24 logical processors, 16 GB system memory, NVIDIA GeForce RTX 4070
  Laptop GPU, NVIDIA driver 592.82, 8,188 MiB reported device memory, Windows 11
  Home build 26200, Balanced power plan. Balanced was the only registered power
  scheme; GPU clocks were not pinned. The GPU reported P0 at 2,175 MHz core and
  8,001 MHz memory immediately before the run.
- Comparison host still required: Ryzen 9 7950X3D / RTX 3090 desktop.
- RenderDoc environment: `rdc doctor` passed the Windows, replay, RenderDoc,
  and registered Vulkan-layer checks with RenderDoc 1.41/1.44 components.
- WPR environment: `GeneralProfile + DotNET` was attempted and rejected with
  `0xc5585011` because the current process cannot enable system-performance
  profiling. `wpr -status collectors` confirms no recorder remains active.
- A disposable `dotnet-trace` 9.0 tool was installed only under the ignored run
  root for the next user-mode trace pass; it is not a repository dependency.

Ignored files under the run root are disposable evidence. The conclusions
below are the durable record.

## Fixed capture contract

The accepted laptop captures use:

| Setting | Value |
| --- | --- |
| Build/backend | Release, Vulkan, dynamic rendering |
| Scene | Generated Unit Testing World, dense Sponza |
| Camera | position `(-11, 6, 0)`, look-at `(-28, 3, 0)` |
| Window / profile viewport | 1600 x 900 |
| TSR render scale | 0.67, applied by the engine at startup |
| VSync / WSI policy | Unit-test VSync `Off`; desktop swapchain prefers `MailboxKHR`, falls back to `FifoKHR` |
| Validation / labels | Off / off |
| Occlusion culling | Disabled |
| Primary reuse / command chains | Enabled / enabled |
| Parallel chain / secondary recording | Enabled / enabled |
| Material submission | Portable `MaterialTable` rung |
| Sample cadence | One profile row every 10 frames for short captures; 20 for the 60-second capture |
| Short window | 5 s warmup, 3 s stable-identity gate, 10 s capture |
| Long CPU window | 25 s warmup, 5 s stable-identity gate, 60 s capture |
| Stable workload identity | `15290802679255583872` |

`BindlessMaterialTable` was rejected as a matched baseline because this device
did not publish the required bindless descriptor generation; the run reported
an unsupported binding rung and forbidden fallback events. Keeping the portable
material-table rung prevents a capability failure from being mistaken for a
CPU/GPU comparison.

## Laptop strategy results

The short captures are matched to the fixed contract above. Times are capture-
window medians unless marked p95.

| Requested | Resolved | Valid samples | Render p50 / p95 | Vulkan frame p50 | GPU command buffer p50 | Read bytes / maps | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `CpuDirect` | `CpuDirect` | 95 | 9.496 / 14.605 ms | 7.613 ms | 2.582 ms | 0 / 0 | Valid |
| `GpuIndirectZeroReadback` | same | 147 | 6.050 / 8.516 ms | 2.629 ms | 0.695 ms | 0 / 0 | Valid short capture; a separate 60 s attempt stopped advancing at frame 1500 |
| `GpuIndirectInstrumented` | same | 139 | 6.417 / 9.337 ms | 3.352 ms | 4.946 ms | 292,304 / 3,701 | Valid measurement, but fails the async diagnostic contract |
| `GpuMeshletZeroReadback` | `GpuIndirectZeroReadback` | 0 | n/a | n/a | n/a | 0 / 0 before failure | Capability downgrade, then exit `0xc0000409` near frame 10 |
| `GpuMeshletInstrumented` | `GpuIndirectZeroReadback` | 0 | n/a | n/a | n/a | 0 / 0 before failure | Capability downgrade, then exit `0xc0000409` near frame 10 |

The valid zero-readback capture reports zero readback bytes, zero mapped
buffers, zero fallback events, zero forbidden fallback events, and zero delayed
diagnostic readback bytes. Its effective material rung is `CoarseBucket` and its
compaction rung is `WorkgroupPrefixScan64`.

The long `CpuDirect` run contains 281 samples over 60 seconds and reports render
p50/p95 of 10.266/14.079 ms. The short run is retained for the matched strategy
table; the long run is the stronger O(draw) cost baseline.

## Third laptop checkpoint

This checkpoint was captured on 2026-08-17 at commit
`6ff61dae1fe41ae02c6788945d4f8f52b85a52cc` on branch `vulkan-refactor`.
The Release editor build completed with zero warnings and zero errors. Evidence
is under
`Build/_AgentValidation/20260817-194535-vulkan-resident-phase0-third-laptop/`;
the short sweep is in `reports/short/summary.json`, the long CPU-direct run is
in `reports/long-cpu/summary.json`, and the user-mode sampled-thread trace is
`reports/cpu-direct-user-mode.nettrace` with a converted
`reports/cpu-direct-user-mode.speedscope.json`. `rdc doctor` passed the local
RenderDoc 1.44 replay, command-line, and registered Vulkan-layer checks.

The requested contract matched the fixed central camera, 1600 x 900 window,
0.67 render scale, desktop Vulkan dynamic rendering, VSync off, validation and
labels off, disabled occlusion, enabled primary reuse/command chains/parallel
recording, `MaterialTable`, and `ShippingFast`. Short captures used 5 seconds of
minimum warmup, a 3-second stable-identity gate, and 10 seconds of capture. The
long CPU capture used 25 seconds of minimum warmup, a 5-second stable-identity
gate, and 60 seconds of capture.

### Short strategy sweep

All five requested modes completed. The meshlet requests resolved explicitly to
indirect zero-readback and are not meshlet measurements.

| Requested | Resolved | Samples | Render p50 / p95 | Vulkan frame p50 | GPU command buffer p50 | Read bytes / maps | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `CpuDirect` | same | 319 | 2.765 / 3.959 ms | 2.117 ms | 2.193 ms | 0 / 0 | Valid capture; only 3 GPU-scene commands at p50 |
| `GpuIndirectZeroReadback` | same | 35 | 19.518 / 25.851 ms | 12.179 ms | 2.102 ms | 0 / 0 | Zero-readback contract passes; 125 GPU-scene commands at p50 |
| `GpuIndirectInstrumented` | same | 79 | 11.773 / 17.298 ms | 9.553 ms | 1.937 ms | 24,964 / 316 | Diagnostic readback remains active; delayed diagnostic bytes were 14,536 |
| `GpuMeshletZeroReadback` | `GpuIndirectZeroReadback` | 37 | 25.225 / 40.907 ms | 16.613 ms | 2.750 ms | 0 / 0 | Explicit capability downgrade; not meshlet evidence |
| `GpuMeshletInstrumented` | `GpuIndirectZeroReadback` | 70 | 10.896 / 14.334 ms | 5.779 ms | 2.080 ms | 0 / 0 | Explicit capability downgrade; not instrumented-meshlet evidence |

Every row reported stable workload identity `17674158090218751745`, zero
capture-window fallback events, and zero forbidden fallback events. The real
GPU paths selected `CoarseBucket` and `WorkgroupPrefixScan64`. The sweep should
not be ranked as a strategy performance comparison: the GPU-scene command count
was 3 for the short CPU-direct row and 125 for the GPU rows, and sequential runs
showed large timing variance despite the identity gate passing.

### Long CPU-direct window

The 60-second CPU-direct capture recorded 694 samples and render p50/p95 of
3.495/8.860 ms. Its workload shape also varied: GPU-scene command count was 83
at p50 and 125 maximum, while prepared cohorts rebuilt 1,062 times. This makes
it a third-machine checkpoint rather than the settled 625-draw O(draw) baseline
captured on the original laptop.

| Stage | p50 / p95 | Count evidence |
| --- | ---: | --- |
| Raw mesh-request drain | 0.014 / 0.023 ms | 13,860 invocations |
| Prepared-cohort work | 1.476 / 1.838 ms | 12,792 hits and 1,062 builds |
| Binding validation | 0.016 / 0.030 ms | 12,792 invocations |
| Unsafe-hole materialization | 1.326 / 1.624 ms | 313,137 operations |
| Resource-use lowering | 0.066 / 0.112 ms | 13,854 invocations |
| Frame-plan construction | 0.137 / 0.213 ms | 13,860 invocations |
| Primary native encoding | 0 ms p50 | 1,464 invocations; sampled clean-reuse ratio 1.0 |

The capture read zero GPU bytes, mapped zero GPU buffers, and reported zero
fallback events. It reused 963,839 prepared operations, but the nonzero cohort
build and native-encoding counts confirm that the long window was not the same
fully settled workload as the original-laptop baseline.

### CPU and scheduler evidence

The 43.5 MB user-mode `.nettrace` captured about 14.9 seconds of sampled data
before the profiled process completed, produced 66 thread profiles in the
Speedscope conversion, and enabled one-second `System.Runtime` counters.
Exclusive sampled thread time was dominated by waits:
`SemaphoreSlim.WaitCore` 82.65%, `WaitHandle.WaitOneNoCheck` 5.07%,
`Thread.Sleep` 4.82%, and the ThreadPool IO completion poller 3.38%.
`EngineTimer.RunCollectVisibleIteration` accounted for 1.36% exclusive sampled
time. This trace is useful for managed ownership and wait-state inspection, but
it does not contain kernel context switches, ready-thread delay, core migration,
or QoS evidence.

The required WPR `GeneralProfile + DotNET` capture was attempted and rejected
with `0xc5585011` because this shell cannot enable system-performance profiling.
`wpr -status collectors` confirmed that no recorder remained active. An
elevated rerun is still required for the scheduler portion of the Phase 0 gate.

### Comparison limits

- The third-laptop checkout commit differs from the original-laptop baseline
  commit `a2d15e430edd68ab9fe06360eb36070ac8e79805`.
- Its stable identity `17674158090218751745` differs from the original dense
  identity `15290802679255583872`.
- The power plan is Balanced rather than Performance, and GPU clocks were not
  pinned.
- The command-count and cohort-build variation means the current stability gate
  did not prove an equivalent settled workload. Do not attribute timing deltas
  between the two laptops to hardware until both are rerun at one commit and one
  accepted identity/workload signature.

## O(draw) CPU evidence

The 60-second `CpuDirect` capture observed 5,600 actual frames between sampled
counter snapshots:

| Stage | p50 / p95 | Count evidence |
| --- | ---: | --- |
| Raw mesh-request drain | 0.086 / 0.145 ms | Once per frame |
| Prepared-cohort match/reuse | 3.993 / 5.994 ms | 5,600 hits |
| Binding validation | 0.261 / 0.481 ms | Once per stable hit |
| Unsafe-hole materialization | 2.611 / 3.384 ms | 330,400 operations, exactly 59/frame |
| Resource-use lowering | 0.616 / 0.824 ms | Once per frame |
| Frame-plan construction | 0.863 / 1.232 ms | Once per frame |
| Primary native encoding | 0 ms steady state | Primary command buffer reused 100% |
| Command-chain worker wait | 0 ms | No stable-frame worker dispatch |

The same interval reused 3,169,600 prepared operations, exactly 566/frame. The
566 reusable entries plus 59 holes equal the 625-draw cohort. This is the main
reason CPU-direct performance regresses easily: the cache retains command
artifacts but still scans every draw, validates bindings, rebuilds unsafe
entries, lowers dependencies, and reconstructs a plan every frame. Small
changes to eligibility or invalidation expand the hole count or defeat the
cohort, immediately restoring more of the full per-draw pipeline.

The GPU-indirect paths reduce the cohort to one reusable producer operation plus
16 holes per frame. Their measured cohort cost is about 0.348 ms and preparation
cost about 0.41 ms, which explains most of the laptop difference without
attributing it to command recording alone.

## Current worker topology

Source inspection found the following persistent or lazily persistent workers.
This is the topology to replace in Phase 1, not a recommended final budget.

| Owner | Current count/policy | Render-critical overlap |
| --- | --- | --- |
| `Engine.Jobs` | One `JobManager`; default `min(logicalProcessors - 4, 16)` workers | Can run asset/preparation work concurrent with render |
| `RuntimeEngine.Jobs` | A second independent `JobManager` with the same default | Duplicates the first pool; direct oversubscription risk |
| Engine loops | update, fixed update, collect-visible, render/main, optional window-pump threads; critical loops use `AboveNormal` | Direct critical path |
| Vulkan command-chain workers | Lazy, configurable; auto currently bounded to four | Render thread may wait when chains require recording |
| Vulkan pipeline compilation | One worker by default, `BelowNormal` | Can compete for CPU/cache but should not gate a stable frame |
| OpenXR | Two eye workers plus collection/pacing workers where enabled | XR critical path; inactive in this desktop capture |
| .NET ThreadPool | Elastic; used by imports, async IO, diagnostics, and assorted tasks | Can contend with both job pools and engine loops |
| Other subsystems | Physics/native pools, FFmpeg, profiler/statistics helpers | Workload dependent |

On this 22-logical-processor laptop the two default `JobManager` instances can
create 32 persistent `XRJobWorker-*` threads before Vulkan, engine-loop,
ThreadPool, driver, and subsystem threads are counted. Captured processes
reported roughly 96-125 total threads. Both job pools also reuse the same thread
name pattern, obscuring ownership in traces. Phase 1 must centralize the budget
and give every lane stable owner/name identifiers.

## Canonical GPU-scene migration map

The final owner is `AdvancedSharedGpuSceneDatabase`; legacy `GPUScene` and
`HybridRenderingManager` remain migration inputs only. No new renderer-neutral
identity allocator should be introduced.

| Legacy responsibility | Final canonical owner | Missing work before cutover |
| --- | --- | --- |
| Command/draw row, mesh/material/state/transform IDs | `Scene.Draws` + generation-checked `AdvancedGpuHandle` | Add managed-publication registry that maps source objects to canonical handles and emits tombstones/deltas |
| Current/previous transforms, bounds, visibility, instance slots | `Scene.Instances` and `Scene.Transforms` | Add independently dirty owner ranges and consumer acknowledgements; avoid duplicating matrices between records |
| Mesh IDs, atlas offsets, index ranges, meshlet ranges/data | `Scene.Geometry.Records` + `AdvancedGeometryDatabase` byte arenas | Add atlas-tier allocation adapter, residency/streaming request record, and explicit arena dirty/tombstone publication |
| Logical mesh LOD table/request stream and thresholds | Geometry plus a new canonical logical-LOD record/arena | Record is missing; do not keep the legacy logical-mesh ID allocator |
| Deformed current/previous geometry and skinning palette | `Scene.Deformations` + geometry pre-skinned arenas | Add palette/deformation job ranges and temporal-slot ownership |
| Render-state classes and representative material map | `Scene.RenderStates` + `Materials.Materials` | Replace legacy representative dictionaries with canonical handle references |
| Material IDs, constants, texture references, shader variants | `Materials.Materials`, `Kernels`, `Layouts`, constant/texture arenas | Add source-material publication registry and backend projection generations |
| Classification, transparency metadata, LOD transition state | `Scene.RenderStates`/`Instances` plus a new temporal visibility record | Exact temporal transparency/LOD record is missing |
| BVH/AABB buffers and rebuild/refit state | New visibility-acceleration projection sourced from canonical geometry/instances | Backend-owned BVH is not a second scene database; add dirty AABB stream and build-generation contract |
| Indirect commands, visible lists, counts, bins | Per-view/per-pass backend package derived from canonical records | Must never become canonical identity; publish bin manifests and ordered exceptions |
| Hybrid program dictionaries and tier renderers | Backend template/program cache keyed by canonical material/kernel/layout handles | Add bounded lifetime and generation invalidation; keep out of shared database |
| Editor selection/picking identity | `Scene.EditorIdentities` | Source publication/tombstone wiring still required |

`AdvancedSharedGpuSceneDatabase` is currently not used by the production
Sponza path; it appears in definitions, reconstruction support, and tests. Phase
2 therefore needs dual publication and equivalence checks before deleting any
legacy allocator or upload stream.

## Readback and diagnostic contract audit

| Strategy family | Reachable behavior today | Frozen contract |
| --- | --- | --- |
| Indirect zero-readback | GPU-written count consumed by indirect-count submission; portable material table; readback diagnostics suppressed | Zero buffer/image read bytes, mappings, CPU count/build fallback, one-shot diagnostic submissions, and current-frame waits |
| Meshlet zero-readback | Requested path downgrades on this device/build; no valid meshlet capture | Same zero contract; downgrade must remain explicit and is not meshlet evidence |
| Indirect instrumented | `TryReadDrawCount`, mapped argument dumps, and `IBufferDiagnosticReadbackBackendCapability.TryReadBufferBytes` are reachable | Move all production diagnostics to bounded delayed slots; poll a prior-frame fence and skip if not ready |
| Meshlet instrumented | Requested path downgrades and fails before capture | Same delayed diagnostic contract; no current-frame wait |
| General Vulkan delayed stats | `VulkanGpuStatsReadback` has a bounded 32-slot delayed queue with fence-status polling | Reuse this ownership model or a shared successor; never wait to make a diagnostic sample ready |

The critical synchronous chain is:

`HybridRenderingManager` diagnostic read ->
`IBufferDiagnosticReadbackBackendCapability.TryReadBufferBytes` ->
`VulkanFrameLoop.TryReadBufferBytesForDiagnosticsCore` ->
`VulkanCommandRuntime.TryReadBufferBytes` -> `NewCommandScope.Dispose` ->
`CommandsStop` -> one-shot submit ->
`vkWaitForFences(..., waitAll=true, timeout=ulong.MaxValue)`.

That path must be removed from per-frame instrumented rendering rather than
hidden behind a different counter name.

## Instrumentation added in this checkpoint

- Deterministic camera, window size, render scale, sample cadence, and Vulkan
  GPU-driven profile overrides in the profiling harness.
- Requested/resolved strategy and stable workload identity remain distinct.
- New Vulkan CPU stages for raw request drain, cohort work, binding validation,
  hole materialization, resource-use lowering, planning, native encoding, and
  worker waits.
- Process-level deltas for cohort hits/builds, reusable operations, and legacy
  hole materializations.
- The profiler samples the first completed frame and then a configurable frame
  stride, preventing the telemetry writer itself from producing gigabyte-scale
  captures. A failed pre-fix run produced a 1.73 GB NDJSON stream; the retained
  cadence smoke produced about 82.7 MB for 1,100 samples.

The targeted Release editor/Vulkan build completed with zero warnings and zero
errors after these changes. `git diff --check` is clean at this checkpoint.

## Remaining evidence package

1. Run the same fixed contract on the 7950X3D/RTX 3090 desktop and retain the
   requested/resolved pair for all five strategies.
2. Run an elevated WPR `GeneralProfile + DotNET` capture around the CPU-direct
   steady-state window. Export sampled CPU, process/thread, context switch,
   ready-thread, activity interval, and .NET ThreadPool events. Do not infer
   migration or scheduling from FPS.
3. Capture three fixed camera views with image hashes plus draw/pass/material/
   shadow/UI signatures.
4. Complete one RenderDoc open-work-close session, export the central-view final
   output and relevant depth/G-buffer targets, inspect them visually, and close
   the replay session.
5. Diagnose the reproducible frame-1500 indirect stall and both meshlet-request
   exits. Crash dumps were generated, but the initial managed inspection found
   no current managed exception and is not a root-cause result.
6. Replace synchronous instrumented readback with delayed, bounded, poll-only
   diagnostics before accepting the instrumented exit gate.

## Phase 0 exit-gate status

- [ ] Desktop/laptop difference separated into CPU work, scheduler/QoS, GPU
  execution, and presentation. Laptop categories are measured; desktop and
  kernel scheduler traces remain absent.
- [ ] Every planned O(draw) stage has a measured baseline count and time.
  CPU-direct and indirect are measured; a real meshlet path is unavailable.
- [ ] Canonical database migration map has one final owner for every
  scene/material identity and GPU upload stream. The owner map is drafted above
  but requires review before Phase 2 begins.
- [ ] Both zero-readback modes demonstrate zero readback bytes/mappings/waits
  and CPU fallback. Indirect passes; meshlet is unavailable and its request
  fails after downgrade.
- [ ] Both instrumented modes report bounded expected diagnostic activity
  without a current-frame wait. Indirect currently violates this contract;
  meshlet is unavailable.
