# Vulkan Framerate Root-Cause Investigation

Date: 2026-07-28

## Problem statement

Vulkan desktop rendering does not reliably sustain its target frame rate
despite repeated primary- and secondary-command-buffer optimizations. Determine
the actual CPU, render-thread, synchronization, and GPU bottlenecks without
implementing production fixes yet.

The owner-confirmed promotion targets are:

- desktop-only rendering: at least 200 Hz, or a 5.00 ms whole-frame budget;
- Vulkan `GpuIndirectZeroReadback` with RVC eye rendering: at least 120 Hz, or
  an 8.33 ms whole-frame budget, with a minimum of one desktop render plus both
  eye renders per frame;
- foveation does not relax the RVC budget, and the 8.33 ms target is not a
  per-eye or per-render allowance.

The controlled desktop captures below were originally framed against 120 Hz.
They remain valid root-cause evidence, but 120 Hz is not the desktop promotion
threshold.

The investigation must distinguish:

- command recording and submission overhead;
- forward+ depth/normal prepass cost versus deferred rendering;
- occlusion-culling cost and effectiveness;
- imported-texture streaming and upload/finalization work;
- render-thread work that should be prepared by the collect-visible thread;
- frame pacing, waits, and instrumentation overhead from actual rendering.

## Method

- Use controlled Release Vulkan runs with warm caches and identical scene,
  viewport, camera, and diagnostic settings.
- Change one feature or scheduling variable at a time.
- Capture CPU profiler summaries, render-stall logs, Vulkan timing summaries,
  command-buffer telemetry, and validation diagnostics.
- Use RenderDoc only where frame/pass structure or GPU cost cannot be resolved
  from the runtime timing data.
- Keep disposable evidence under
  `Build/_AgentValidation/20260728-vulkan-framerate-root-cause/`.

## Current status

Diagnosis complete. No production renderer fix was implemented.

## Issues found

### Executive conclusion

The engine is not missing its desktop target because of one shader or one
command-buffer bug. It has several additive CPU-side problems, and the
command-buffer work addressed only one layer of them.

Ranked by current evidence:

1. **The default zero-readback material path is an O(material slots × three
   tiers × render passes) render-thread scan.** It creates a large frame-op
   population before the Vulkan frame starts, then clean primary reuse still
   walks that population to refresh descriptors and frame data. Deferred-only
   `GpuIndirectZeroReadback` took 24.97 ms p50 even though the GPU took only
   2.93 ms and the primary was reused on all 480 samples.
2. **The CPU-direct primary skeleton is re-recorded every frame because its
   recorded image-entry state is rejected.** Its mesh command chains were
   reused, but the primary was not. The decision mask was consistently
   `Recorded | PrimaryFrameState` (1026), which source tracing narrows to the
   image-entry-state reuse gate. This is why chain reuse alone did not produce
   stable target performance.
3. **The primary/secondary parallel-recording work does not apply to the main
   zero-readback path.** Zero-readback and mutable GPU-driven operations are
   explicitly excluded from command-chain lowering. Persistent command-chain
   worker recording is also hard-disabled globally; the fallback records dirty
   chains serially on the render thread.
4. **The Uber forward depth/normal prepass is a real, measured cost, but it is
   not the sole root cause.** Disabling only that prepass improved Uber
   CPU-direct from 22.00 to 16.09 ms p50. The 5.91 ms delta comprised about
   4.60 ms more Vulkan CPU work and 0.97 ms more GPU work. Sixteen milliseconds
   is still far outside both the historical 120 Hz diagnostic threshold and
   the 200+ Hz desktop promotion budget.
5. **GPU Hi-Z occlusion is catastrophically expensive in this workload.**
   Deferred GPU-driven rendering rose from 24.97 to 182.04 ms p50. Command
   preparation/recording rose to 145.85 ms and GPU work rose to 17.49 ms.
   This is a separate failure when Hi-Z is enabled, not the explanation for
   the disabled-occlusion baseline.
6. **CPU software occlusion is pure loss in the measured static view.** It
   increased CPU-direct Deferred from 9.36 to 15.89 ms p50 while reporting
   four final AABB tests and zero culls. The exported counters omit its more
   expensive candidate sorting and triangle rasterization.
7. **Imported-texture streaming is a startup/churn amplifier, not the warmed
   steady-state limiter.** Representative launches uploaded 35-243 MB across
   78-89 jobs during startup, but every measured capture window recorded zero
   texture-upload jobs, bytes, and time.
8. **Collect-visible is backpressured, not late.** It remains one generation
   ahead with no stale reuse, typically finishes in 1-2 ms, and then waits for
   the render thread. It only publishes engine-level command collections;
   Vulkan packets, material fan-out, resource plans, descriptors, frame-data
   refresh, command encoding, and submission remain render-thread work.

### Controlled Release results

All cohorts used Vulkan, Release, warm caches, desktop mode, VSync off,
validation off, primary reuse enabled, command chains enabled, parallel flags
enabled, and a stability gate. Unless noted otherwise, occlusion was disabled.
The per-material comparison loaded one identical Sponza import at a time so
auto-framing and geometry were comparable.

| Cohort | Render p50 / p95 | Vulkan frame p50 | Vulkan CPU record/prep p50 | GPU p50 | Primary result |
|---|---:|---:|---:|---:|---|
| Deferred, CPU-direct, disabled repeat | 9.36 / 13.84 ms | 7.50 ms | 6.88 ms | 1.98 ms | 1,047 records, 0 reuse |
| Deferred, zero-readback FullBucketScan | 24.97 / 30.15 ms | 13.45 ms | 11.60 ms | 2.93 ms | 480 clean reuse |
| Deferred, zero-readback ActiveBucketList | 20.23 / 32.47 ms | 10.98 ms | 9.30 ms | 3.59 ms | clean reuse; readback-assisted |
| Deferred, zero-readback MaterialTable | 21.82 / 27.63 ms | 11.57 ms | 9.91 ms | 2.26 ms | clean reuse; readback-assisted |
| Deferred, zero-readback BindlessMaterialTable | 22.75 / 28.02 ms | 11.78 ms | 10.07 ms | 2.55 ms | clean reuse; readback-assisted |
| Uber, CPU-direct, prepass on | 22.00 / 26.92 ms | 16.90 ms | 16.15 ms | 3.65 ms | every primary recorded |
| Uber, CPU-direct, prepass off | 16.09 / 20.14 ms | 12.29 ms | 11.53 ms | 2.68 ms | every primary recorded |
| Deferred, CPU software occlusion | 15.89 / 23.05 ms | 12.44 ms | 11.51 ms | 1.06 ms | 0 culls |
| Deferred, CPU query async | 4.70 / 13.86 ms | 2.16 ms | 1.40 ms | 1.04 ms | bimodal: 2 reuse frames per record frame |
| Deferred, GPU Hi-Z | 182.04 / 344.73 ms | 150.46 ms | 145.85 ms | 17.49 ms | every primary recorded |

The CPU-query median is not a culling win. It tested two objects, culled zero,
and deliberately concentrated query commands onto one cadence frame so the
intervening two frames could reuse their primaries. Its p95 remained 13.86 ms,
nearly identical to disabled CPU-direct. It therefore improves the median by
changing command-record cadence, not by removing visible work.

The three FullBucket alternatives are not valid replacements for a
zero-readback contract in the current implementation. Active buckets mapped
three buffers and read about 556 bytes per sampled frame. MaterialTable and
BindlessMaterialTable mapped two buffers and read about 536 bytes per frame.

### Why prior primary/secondary fixes did not stick

There are three distinct meanings hidden behind "recording":

1. The command-chain scheduler excludes zero-readback and mutable GPU-driven
   frame operations. In the zero-readback captures, chains scheduled, recorded,
   reused, and worker timings were all zero.
2. Persistent parallel chain recording is guarded by
   `ParallelCommandChainWorkerRecordingSafe = false`. If enabled, the worker
   implementation still takes a renderer-wide mutable resource-planner lock
   and the render thread waits on a countdown event.
3. `VulkanFrame.RecordCommandBuffer` times the whole
   `EnsureCommandBufferRecorded` operation, not only Vulkan command encoding.
   A clean primary still calls `TryRefreshReusableCommandBufferFrameData` over
   every frame op. In the Deferred FullBucket capture, the 11.60 ms
   "record" time contained 9.67 ms of frame-data refresh and zero primary or
   secondary command encoding.

CPU-direct has the inverse failure. All mesh chains can report reused while the
primary skeleton is freshly encoded around them. The disabled cohort's
decision reason was `PrimaryFrameState`, and source tracing identifies the
image-layout entry-state gate as the only matching no-query path. Reused
secondary state can mark the merged primary entry state incomplete or conflict
with the primary's prior state, causing the next frame's primary reuse test to
fail again.

### Zero-readback material fan-out

`FullBucketScan` is the default in both editor and runtime settings. For each
render pass it:

- iterates every material slot;
- resolves the material/program and reapplies material state;
- loops all three atlas tiers;
- configures an indirect renderer and enqueues an indirect-count bucket
  operation even though most bucket counts will be zero on the GPU.

The Deferred capture reported 77,760 candidate bucket scans across 480 samples,
or exactly 162 per frame. This work happens inside pipeline command execution
before `Vulkan.Frame.Total`, matching the measured 11.44 ms p50 outside the
Vulkan frame. The resulting indirect frame ops then cause another 9.67 ms
serialized frame-data refresh inside clean primary reuse.

The active-bucket and material-table paths reduce some fan-out, but currently
read the GPU-produced active list back to the CPU. Material-table dispatch also
falls back to FullBucketScan for overrides and depth/normal variants, so the
Uber prepass cannot benefit from it.

### Uber forward+ versus Deferred

The current JSONC is not itself a valid material comparison: it loads one Uber
Sponza at `+20 X` and one Deferred Sponza at `-20 X` simultaneously. The
default pipeline is hybrid, so it runs deferred GBuffer/lighting and then
forward rendering in the same graph.

The isolated A/B establishes:

- Uber is materially more expensive than Deferred in the current static
  Sponza scene.
- The shared full-resolution depth/normal prepass contributes about 5.91 ms
  p50 render time.
- The prepass replays forward opaque/masked geometry before the lit pass.
- The shared-GBuffer path also performs three logical full-resolution
  color+depth copies. Vulkan resolves color and depth separately, producing six
  image blits and up to 24 image transition/barrier calls.
- Current TSR additionally renders a velocity geometry pass. Ambient
  occlusion, bloom, temporal accumulation, post-processing, directional
  shadows, and probe lighting remain part of the current graph.

The prepass is therefore a major additive cost, but disabling it does not make
Uber approach even the historical 120 Hz threshold. Deferred CPU-direct is
closer: its repeated cohort reached 9.36 ms p50 and 13.84 ms p95, missing both
the historical 8.33 ms comparison threshold and the current 5.00 ms desktop
promotion budget.

### Occlusion verdict

- **Disabled:** already misses stable target performance, so occlusion is not
  the universal root cause.
- **CPU software:** harmful in this scene; sorting and CPU triangle
  rasterization occur on the render thread and produced no culls.
- **CPU query async:** cadence-bimodal and zero culls. It changes primary reuse
  frequency; its low median is not evidence of effective occlusion.
- **GPU Hi-Z:** currently unusable for this workload. It builds an RGBA32F Hi-Z
  pyramid with initialization, per-mip dispatch/barrier work, refinement, and
  count-copy operations. The reported phase-one value is accumulated candidate
  count across invocations, not actual draw count. Zero phase-two draws is also
  expected in zero-readback mode and does not mean every candidate was culled.

### Texture-streaming verdict

Streaming work is correctly prepared mostly off-thread, but sparse-transition
finalization and Vulkan publication still enqueue render-thread jobs and can
invalidate descriptors/command reuse during churn. One-shot Vulkan command
submission also shares the desktop queue lock and may hold it through a fence
wait; current telemetry does not attribute this lock wait.

It is not the persistent static-frame cause in these captures:

- Deferred representative launch: 78 startup jobs, 242.8 MB, 105.1 ms total
  measured upload time; capture window zero.
- Uber representative launch: 89 startup jobs, 34.8 MB, 38.7 ms total upload
  time; capture window zero.
- Every analyzed steady capture: zero texture-upload jobs, bytes, and time.

### Collect-visible and render-thread utilization

The single-window handoff itself works:

- collect generation age never exceeded one frame;
- stale collect reuse was zero;
- render wait for collect was near zero;
- collect wait for render tracked the slow render duration.

The architectural problem is the boundary. Collect-visible builds and swaps
generic `RenderCommandCollection` data, then waits until the render thread has
successfully submitted Vulkan work. It does not prepare the upcoming frame's
backend-specific material buckets, Vulkan frame ops, resource plans, dependency
snapshots, descriptor writes, uniform/frame-data refresh, or reusable-primary
entry-state contract. Consequently the collect thread can sit idle for
20-180 ms while those stages execute serially on the render thread.

Viewport collection is also serial across event listeners despite a parallel
event-invocation implementation existing. That is secondary in the measured
static scene because collection itself is only 1-2 ms.

Other non-render work still eligible to run on the render thread includes:

- generic main-thread jobs, with a nominal 4 ms per-frame budget and no
  preemption of an individual over-budget job;
- texture publication/finalization;
- BVH raycast processing;
- GPU physics dispatch/completion processing.

These are tail-risk and interaction-hitch sources. They were not the dominant
steady-state cause in the static cohorts.

### Source anchors

The main code paths correlated with the measurements are:

- zero-readback strategy switch and FullBucket loop:
  `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs:482-524`
  and `:4787-4939`;
- clean-reuse frame-data refresh:
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs:631-647`
  and `:1949-2085`;
- zero-readback chain exclusion:
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs:276-299`;
- disabled persistent chain workers:
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs:10-14`;
- primary image-entry-state reuse gate:
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs:358-369`
  and
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs:1838-1869`;
- collect/render handoff:
  `XREngine/Core/Time/EngineTimer.cs:385-443` and `:544-572`;
- forward prepass and restore copies:
  `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs:242-246`
  and `:394-440`;
- CPU software occlusion:
  `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs:762-838`
  and
  `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuSoftwareOcclusionCuller.cs:80-246`;
- GPU Hi-Z pyramid/refinement:
  `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs:1086-1199`.

### Additional motion/startup amplifiers

The prior current-JSONC investigation remains relevant:

- moving the camera caused four directional shadow cascades to invalidate and
  re-record, dropping to about 10.5 FPS while GPU time stayed near 4.3 ms;
- disabling the directional light roughly halved the CPU frame cost;
- cold Uber pipeline compilation previously consumed about 180 worker-seconds
  and temporarily reduced rendering to 0.2-3 FPS.

Those are separate from the steady static bottlenecks above and explain why
the frame rate can vary dramatically during camera motion or a cold launch.

## Suggested solutions

No fixes were implemented. The evidence indicates that future work should be
ordered around these boundaries:

1. Make the GPU-driven zero-readback path genuinely GPU-driven at material
   dispatch time; avoid both the full CPU bucket fan-out and active-list CPU
   readback.
2. Make clean primary reuse avoid O(all frame ops) render-thread refresh, or
   prepare immutable per-frame descriptor/uniform data before the render stage.
3. Repair primary image-entry-state reuse so a stable CPU-direct frame can
   reuse the primary skeleton as well as mesh chains.
4. Decide whether persistent chain workers can be made safe without a global
   planner lock; otherwise remove the misleading parallel configuration.
5. Rework or disable the current GPU Hi-Z path until its pass fan-out,
   allocations, and pyramid/refinement cost are bounded.
6. Reconsider the forward prepass copies and geometry replay; the measured
   cost is large enough to matter but is not the first root cause.
7. Move backend-specific preparation into a well-defined next-frame package
   produced alongside or immediately after collect-visible.
8. Add telemetry for the one-time submission queue-lock wait, fence wait,
   software-occlusion selection/raster time, per-pass Hi-Z invocations, and
   exact primary image-state mismatch.

## Attempted solutions

None. This task was diagnostic-only.

Two diagnostic changes were added:

- the benchmark harness now locates logs under `XRE_EDITOR_SESSION_ROOT`, so
  unattended profiles can use isolated preferences rather than inheriting the
  user's global MCP settings;
- `XRE_PROFILE_DISABLE_FORWARD_DEPTH_PREPASS=1` disables the forward prepass
  at pipeline construction for controlled profiling. Default behavior is
  unchanged.

## Validation evidence

- `rdc doctor`: passed, including Vulkan layer and replay support.
- Release editor build after diagnostic instrumentation: succeeded with zero
  errors. Existing Magick.NET advisory warnings and two existing unused-field
  warnings remain.
- The original `Assets/UnitTestingWorldSettings.jsonc` was restored
  byte-for-byte after material isolation.
- Consolidated cohort summaries:
  `Build/_AgentValidation/20260728-vulkan-framerate-root-cause/reports/`.
- Cross-cohort per-stage calculation:
  `Build/_AgentValidation/20260728-vulkan-framerate-root-cause/reports/stage-breakdown.json`.

RenderDoc capture was not required for the root-cause ranking. Runtime GPU
timestamps already separate 1-4 ms GPU frames from 9-25 ms render-thread
frames in the important disabled-occlusion cohorts, and source plus stage
telemetry identify the CPU fan-out and refresh loops. GPU Hi-Z is also
CPU-recording dominated (145.85 ms CPU versus 17.49 ms GPU). A RenderDoc frame
would be appropriate after those CPU issues are addressed or when optimizing
individual GPU passes, but it would not explain the current primary limiter.

Dense GPU timestamps were used once as a diagnostic, but that mode forced the
primary dirty on every sample and was excluded from CPU comparisons. Normal
runtime command-buffer timestamps supplied the GPU values in the tables.

## Performance truth and regression gates implementation

Workstream 01 is implemented. The tracked contract is
`XREngine.Benchmarks/VulkanPerformance/vulkan-performance-cohorts.json`; it
defines `Quick`, `Compare`, and `Gate`, the four observer modes, a 7.5% maximum
run-to-run range, a 5% baseline-regression threshold, four desktop cohorts, and
four Vulkan RVC cohorts. The RVC cohorts require one `DesktopScene` view and two
`OpenXREyeSubmit` views in every measured frame. Foveation-off and
foveation-fixed are distinct identities, so an unavailable runtime or
unsupported foveation mode is reported rather than substituted.

The canonical command is:

```powershell
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 -Preset Quick -Cohort desktop-deferred-static
```

`Compare` and `Gate` require an explicit baseline path. Baseline replacement is
a separate `-AcceptBaseline` action, and the evaluator refuses to write a
candidate that has any issue. The standalone evaluator is built from
`XREngine.Benchmarks` with `VulkanPerformanceToolOnly=true`; it does not load
the editor or renderer. Its fixture suite covers percentile and variance
calculation, exact manifest-mismatch fields, absolute budgets, baseline
regressions, missing required outputs, forbidden fallbacks, readback
violations, exit codes, Quick non-promotion, and rejected baseline writes.

### First canonical Quick result

Evidence root:
`Build/_AgentValidation/20260728-143000-vulkan-perf-quick-final/`.

The run used the tracked Deferred large-scene settings, static camera,
Release build, warm cache, Vulkan `GpuIndirectZeroReadback`,
`BindlessMaterialTable`, `CleanProfile`, 1920x1080 windowed with VSync off, an
NVIDIA GeForce RTX 4070 Laptop GPU, driver 581.57, and Windows
10.0.26200.0. The manifest records source commit
`62738e2519e021bdf41f38959cbab07093ef184d`, dirty-worktree state, executable
SHA-256, settings SHA-256, output extents, feature state, and the exact engine
log session.

The stability gate passed with one workload identity and 449 samples. Evaluator
statistics were:

| Metric | p50 | p95 | p99 | Worst |
| --- | ---: | ---: | ---: | ---: |
| Render dispatch | 28.394 ms | 58.887 ms | 125.456 ms | 1946.813 ms |
| Render outside `Vulkan.Frame.Total` | 26.102 ms | 52.199 ms | 118.353 ms | 255.458 ms |

All 449 frames missed the 5.00 ms desktop budget and the evaluator classified
the failure as CPU-bound. The result was explicitly
`NonPromotableQuickRun` and `Fail`. It also found current-frame readback
(including frames with 1,048 bytes and two mappings) and the capture harness
rejected 3,442,176 bytes of steady-state command-buffer-recording allocation.
This is the intended gate behavior: the current renderer does not yet qualify
for a zero-readback or zero-allocation promotion baseline, so no baseline was
accepted.

### Observer overhead

Evidence root:
`Build/_AgentValidation/20260728-145000-vulkan-profile-overhead-final/`.
All four modes used the same cohort and five-second stable capture window:

| Mode | Render p50 | Render p95 | p95 delta from Release | Samples |
| --- | ---: | ---: | ---: | ---: |
| `ReleaseBenchmark` | 4.765 ms | 6.768 ms | 0.000 ms | 881 |
| `CleanProfile` | 4.262 ms | 6.027 ms | -0.741 ms | 1041 |
| `DevelopmentProfile` | 7.047 ms | 9.647 ms | +2.879 ms (+42.5%) | 539 |
| `Diagnostics` | 13.517 ms | 25.754 ms | +18.986 ms (+280.5%) | 243 |

The short single-repetition result is observer-overhead evidence, not promotion
evidence. It confirms why `DevelopmentProfile` and `Diagnostics` are excluded
from pass/fail performance totals. MCP was explicitly disabled for all four
runs because it is not part of the profile-mode contract.

### Added attribution and counters

- Vulkan queue-lock acquisition, auxiliary/resource/OpenXR fence waits,
  context/pass transitions, barrier planning/emission, and op dispatch.
- Render work outside `Vulkan.Frame.Total` plus existing frame preparation,
  descriptor, binding, draw/dispatch, upload, secondary, and overlay stages.
- Software-occlusion selection, sort/compaction, raster, query, Hi-Z, tile,
  selected-object, rasterized-object, self-skip, and force-visible values.
- Profiler ingestion, aggregation, graph/table preparation, ImGui draw, visible
  rows, and graph samples.
- Render-thread jobs by source kind with count, execution duration, queue delay,
  and over-budget duration.
- Existing exact primary/secondary record/reuse/dirty reasons, resource and
  command counters, allocation stages, and GPU readback bytes/mappings are now
  evaluated as validity gates rather than informational values.

### Monado, RVC, and RenderDoc infrastructure closeout

Evidence root:
`Build/_AgentValidation/20260728-workstream03-acceptance/`.

- Monado source `326ba6302383fb213af32197633e0c74f59d88f0` was built
  and staged under `Build/Deps/Monado`. `XR_RUNTIME_JSON` is set only for the
  launched process, so the machine-wide SteamVR selection is irrelevant. The
  installer now preserves the pinned repository submodule with `-NoFetch`, and
  the benchmark starts/stops only its marker-owned Monado service.
- `openxr-smoke-pass2/reports/openxr-smoke-summary.json` records runtime
  `Monado`, Vulkan, instance/system/session/swapchain success, submitted eye
  frames, zero retained per-frame allocations, clean teardown, and no warnings
  or failures.
- `rvc-quick-deferred-off-pass5` passed the canonical 5-second stability gate
  after 29 seconds. Its 306 retained samples all contained a fresh independent
  desktop render; 124 runtime-paced XR frames contained both fresh eyes and no
  retained frame contained only one eye. Capture-window GPU readback bytes and
  mappings, full scans, forbidden fallbacks, VUIDs, and submission rejections
  were zero. `reports/evaluation-fixed.json` is
  `NonPromotableQuickRun`; required outputs are evaluated over their declared
  capture cadence instead of incorrectly requiring an XR submit in every
  faster desktop frame.
- That RVC evidence is not a performance pass: render p50/p95/p99 was
  34.778/109.139/112.717 ms, the 8.33 ms target was missed in 285/306
  samples, aggregate command-buffer recording allocated 3,263,104 bytes,
  primary recording allocated 3,255,936 bytes, frame-data refresh allocated
  40,384 bytes, and submission allocated 136 bytes. Stage counters can overlap
  and are not summed. The absolute frame result is a workstream-08 handoff,
  frame-data refresh is a workstream-04 handoff, generic command encoding is a
  workstream-05 handoff, and submission-owned allocation remains in
  workstream 03.
- `renderdoc/ws03-zero-readback-explicit.rdc` is a 65,775,716-byte Vulkan
  capture made by `Tools/RenderDoc/capture_xrengine.py`, which preserves the
  explicit production-cohort environment that `rdc-cli 0.5.6` dropped on
  Windows. Replay reports 566 events, 19 dispatches, 177 draws, and a
  40-command `vkCmdDrawIndexedIndirectCount` compact material submission.
  `ws03-explicit-final-pass.png` and `ws03-explicit-gbuffer.png` visibly contain
  the production scene; replay closed cleanly with no high-severity message.

Root causes fixed along this path:

- `SyncRuntimeVrState` no longer clears a configured OpenXR API before its
  monitor can activate it.
- profile capture now authoritatively enables render-statistics tracking;
  persisted preference side effects can no longer produce empty manifests.
- successful `xrEndFrame` is authoritative for two-eye submit telemetry and
  does not race the mirrored `IsInVR` flag.
- workload identity hashes the configured XR eye family, so runtime-owned eye
  cadence gaps do not look like workload mutations.
- the evaluator validates required fresh outputs across the retained capture
  and uses the most complete output frame for comparison identity.

### Validation

- Standalone Vulkan-performance evaluator Release build: zero warnings and zero
  errors.
- GPU-free evaluator fixture suite: 5 passed.
- `UnitTestingWorldModelImportSettingsTests`: 22 passed; existing Magick.NET
  advisory warnings remain.
- Release editor build: zero errors; existing dependency advisory warnings
  remain.
- Quick editor-process launch/capture/evaluation: completed with a meaningful
  nonzero gate result and durable manifest/evaluation files.
- PowerShell parsing: `Invoke-VulkanPerf.ps1`,
  `Measure-VulkanProfileOverhead.ps1`,
  `Measure-GameLoopRenderPipeline.ps1`, and `Measure-VulkanFrameLoop.ps1`
  passed.
- JSON parsing: `.vscode/tasks.json` and the cohort contract passed.
- `rdc doctor`: passed, including Vulkan layer and replay support.

## Workstream 02 - Primary reuse correctness

Status: implementation complete on 2026-07-28.

### Root cause and repair

The first Deferred CPU-direct reproduction recorded all 466 captured primaries
and reused none. Exact rejection telemetry reduced the stable
`PrimaryFrameState` bit to a stage-mask conflict on an image whose layout was
already `ShaderReadOnlyOptimal`: the primary expected shader-read stages/access
(`2184` / `32`), while submitted state paired that layout with
color-attachment stages/access (`1024` / `384`).

`RecordFboAttachmentAccessState` was publishing the attachment's final layout
with stage/access masks derived from its earlier reference layout. Barrier
tracking could manufacture the same contradictory tuple. The repair now:

- derives framebuffer final stage/access state from the final layout;
- normalizes incompatible non-`General` access scopes from final layout and
  aspect while preserving compatible precise scopes;
- keeps `General` explicit because its layout does not identify an access
  domain;
- merges complete secondary entry/exit snapshots into primary state, while
  unknown or conflicting secondary state forces a typed record reason;
- transitions descriptor-backed secondary images even when the layout is
  unchanged but the dependency scope differs;
- publishes recorded image state only after successful queue submission;
- keeps OpenXR primary reuse on by default with explicit diagnostic overrides.

The reusable tuple, ownership boundaries, unknown-versus-conflict behavior,
cache identity, and data-only mutations are documented in
`docs/architecture/rendering/vulkan-primary-command-buffer-reuse.md`.

### Runtime evidence

All paths below used Release, warm cache, Vulkan, CPU-direct, clean profiling,
command chains enabled, validation/fallback/rejection counters retained, and
the workstream-01 evaluator. Evidence is under
`Build/_AgentValidation/20260728-vulkan-framerate-root-cause/`.

| Cohort | Samples | Primary reused / recorded | Chain reused / recorded | Render p50 / p95 / p99 |
| --- | ---: | ---: | ---: | ---: |
| Deferred static, final code | 1,260 | 1,260 / 0 | 27,114 / 0 | 7.988 / 23.611 / 92.621 ms |
| Deferred moving | 814 | 814 / 0 | 15,282 / 0 | 9.190 / 49.937 / 419.528 ms |
| Uber static | 1,243 | 1,243 / 0 | 48,509 / 0 | 10.886 / 18.736 / 23.763 ms |
| Uber moving | 1,351 | 1,351 / 0 | 22,967 / 0 | 10.959 / 13.783 / 15.473 ms |
| Deferred legacy render pass | 714 | 712 / 2 | 12,138 / 0 | 10.574 / 15.335 / 22.947 ms |

The four canonical dynamic-rendering cohorts achieved 100% captured primary
reuse after the stability gate. Legacy render-pass reuse was 99.72%. The
Deferred static starting reproduction was 0 / 466 reused with p50/p95/p99 of
31.327 / 43.094 / 62.579 ms; its final-code p95 is therefore lower despite
normal short-run variance and outliers. The clean final-code run reported zero
primary-recording allocation, zero submission rejection, zero VUID, and no
forbidden fallback. The evaluator's `NonPromotableQuickRun` classification is
expected for this short preset; no primary-reuse-ratio issue was emitted.

A dynamic-rendering StandardValidation run enabled Vulkan validation and
command labels. It was intentionally diagnostic and produced only three
capture samples because of validation overhead, but reported zero VUID and
zero rejected submission. The legacy clean run also reported zero VUID and
zero rejected submission.

The configured `MonadoOpenXR` probe rendered 1,064 clean frames with 100%
primary reuse, but runtime telemetry reported mono output and `vr_active=false`.
No OpenXR or OpenVR runtime was available, so this is desktop-fallback evidence
only. OpenXR primary/mirror defaults and invalidations remain covered by the
focused source-contract tests; an actual eye-submit run remains hardware/runtime
validation rather than an implementation gap.

### Correctness and tooling validation

- Focused primary-reuse plus Vulkan/OpenXR regression suite: 297 passed, zero
  failed.
- Release editor build completed; pre-existing Magick.NET advisory warnings
  remain.
- Tests cover equal/broader/narrower tuples, unknown/incomplete state,
  recreation generations, per-image queue ownership, descriptor layout,
  framebuffer final-state normalization, compatible precise scopes,
  successful-submit-only publication, resize/resource/query/overlay topology,
  camera/data-only behavior, `LinesBuffer` capacity growth, and OpenXR policy.
- The benchmark contract contains four explicit `primary-reuse-*` cohorts and
  fails any repetition below 99% reuse or with missing decisions.
- `rdc doctor` passed, including the registered Vulkan layer. Automated
  RenderDoc captures repeatedly timed out or disconnected before producing an
  `.rdc`; the isolated MCP fallback build then stalled on unreachable package
  sources, and a copied Release session exposed a host `Path`/`PATH` collision
  in `Start-Process`. No visual artifact is claimed. Runtime required-output
  counts, clean rendering, zero VUID/rejection, and the forced-record starting
  cohort provide the available non-visual comparison evidence.

## User validation

The user has not yet evaluated the completed workstream-02 repair.

## Workstream 03 - Compact zero-readback submission

Status: production implementation complete; promotion gates remain open.

### Root cause and repair

The old `GpuIndirectZeroReadback` default still made the render thread submit
one bucket per configured material slot and atlas tier. The alternatives mapped
a GPU-produced active list, so none of the modes was both compact and genuinely
zero-readback.

The production path now:

- defaults to `BindlessMaterialTable`;
- compacts material-table commands into three fixed static/dynamic/streaming
  tier ranges;
- uses a 64-lane workgroup prefix scan with one clamped global reservation per
  workgroup/tier instead of a per-survivor global atomic;
- consumes GPU counts through Vulkan indirect-count draws without current-frame
  mapping, enumeration, or max-draw fallback;
- uses one coalesced shader-storage/command barrier before the tier draws;
- prepares override and depth/normal material rows and provides a generated
  forward depth-normal fragment variant with alpha cutoff and normal mapping;
- reports material binding, compaction rung, configured slots, pass groups, and
  unsupported scheduled variants;
- keeps exact transparency and arbitrary forward shader semantics explicitly
  unsupported and visible, with no CPU or full-bucket fallback;
- exposes `IGpuCompactVisibilityInput` for the later optional Hi-Z producer.

The architectural contract is
`docs/architecture/rendering/vulkan-compact-zero-readback-submission.md`.

### Runtime evidence

Evidence is under
`Build/_AgentValidation/20260728-vulkan-framerate-root-cause/workstream-03-*`.
All four short Release desktop captures used Vulkan, warm cache,
`GpuIndirectZeroReadback`, `BindlessMaterialTable`, clean profiling, dynamic
rendering, and command-chain/primary reuse. Every capture-window sample
reported zero readback bytes, zero mapped buffers, zero full scans, zero
fallbacks, zero forbidden fallbacks, and no VUID or submission rejection.

| Cohort | Samples | Render p50 / p95 / p99 | Vulkan frame p50 / p95 | Configured slots / pass groups |
| --- | ---: | ---: | ---: | ---: |
| Deferred static | 740 | 7.444 / 24.226 / 37.839 ms | 4.048 / 12.581 ms | 14 / 3 |
| Deferred moving | 253 | 14.840 / 21.047 / 53.308 ms | 7.458 / 11.528 ms | 14 / 3 |
| Uber static | 186 | 21.384 / 29.349 / 92.268 ms | 9.296 / 15.425 ms | 14 / 3 |
| Uber moving | 175 | 23.688 / 33.131 / 70.111 ms | 10.456 / 15.525 ms | 14 / 3 |

The first clean Deferred-static proof after fixing the depth-normal gap reached
5.343 ms render p50, 3.020 ms Vulkan-frame p50, and 99.93% primary reuse,
versus the 24.97 ms starting full-capacity zero-readback result. Short-window
variance and continued resource churn produced the less favorable final table.

### Remaining promotion blockers

- A matched Uber-static CPU-direct capture was 9.159 ms render p50, materially
  faster than compact GPU-driven Uber at 21.384 ms. Workstream 03 therefore
  cannot claim the CPU-stage performance gate.
- The captures still contained allocations. Workstream 03 owns any allocation
  traced to compact submission and still has a 136-byte submission-stage
  failure in the retained RVC Quick capture. Its 40,384 frame-data-refresh
  bytes are handed to workstream 04, and its 3,255,936 primary-recording bytes
  are handed to workstream 05. Primary reuse was 95.16-97.84% in three of the
  four short windows; the canonical stability gate timed out because texture
  upload and retirement activity did not quiesce.
- Exact depth-peeling/per-pixel-list transparency and arbitrary forward shader
  semantics remain explicitly unsupported. Scheduled empty passes are counted
  and warned; scenes that require them are not promotable.
- The Monado/OpenXR and RenderDoc infrastructure blockers are resolved; exact
  evidence is recorded below. Workstream 03 still needs its relative
  submission-performance, submission-owned allocation, full Gate/foveation,
  and remaining correctness suite. Generic frame-data-refresh and
  command-encoding allocations transfer to workstreams 04 and 05; final
  whole-renderer RVC performance transfers to workstream 08.
- No image comparison is claimed. The new source/GLSL contracts and runtime
  counters passed and the production RenderDoc capture is usable, but the
  deterministic CPU-direct/zero-readback comparator has not been run.

### Monado, RVC, and RenderDoc infrastructure closeout

Evidence root:
`Build/_AgentValidation/20260728-workstream03-acceptance/`.

- Monado source `326ba6302383fb213af32197633e0c74f59d88f0` was built
  and staged under `Build/Deps/Monado`. `XR_RUNTIME_JSON` is set only for the
  launched process, so the machine-wide SteamVR selection is irrelevant. The
  installer now preserves the pinned repository submodule with `-NoFetch`, and
  the benchmark starts/stops only its marker-owned Monado service.
- `openxr-smoke-pass2/reports/openxr-smoke-summary.json` records runtime
  `Monado`, Vulkan, instance/system/session/swapchain success, submitted eye
  frames, zero retained per-frame allocations, clean teardown, and no warnings
  or failures.
- `rvc-quick-deferred-off-pass5` passed the canonical 5-second stability gate
  after 29 seconds. Its 306 retained samples all contained a fresh independent
  desktop render; 124 runtime-paced XR frames contained both fresh eyes and no
  retained frame contained only one eye. Capture-window GPU readback bytes and
  mappings, full scans, forbidden fallbacks, VUIDs, and submission rejections
  were zero. `reports/evaluation-fixed.json` is
  `NonPromotableQuickRun`; required outputs are evaluated over their declared
  capture cadence instead of incorrectly requiring an XR submit in every
  faster desktop frame.
- That RVC evidence is not a performance pass: render p50/p95/p99 was
  34.778/109.139/112.717 ms, the 8.33 ms target was missed in 285/306
  samples, aggregate command-buffer recording allocated 3,263,104 bytes,
  primary recording allocated 3,255,936 bytes, frame-data refresh allocated
  40,384 bytes, and submission allocated 136 bytes. Stage counters can overlap
  and are not summed. The absolute frame result is a workstream-08 handoff,
  frame-data refresh is a workstream-04 handoff, generic command encoding is a
  workstream-05 handoff, and submission-owned allocation remains in
  workstream 03.
- `renderdoc/ws03-zero-readback-explicit.rdc` is a 65,775,716-byte Vulkan
  capture made by `Tools/RenderDoc/capture_xrengine.py`, which preserves the
  explicit production-cohort environment that `rdc-cli 0.5.6` dropped on
  Windows. Replay reports 566 events, 19 dispatches, 177 draws, and a
  40-command `vkCmdDrawIndexedIndirectCount` compact material submission.
  `ws03-explicit-final-pass.png` and `ws03-explicit-gbuffer.png` visibly contain
  the production scene; replay closed cleanly with no high-severity message.

Root causes fixed along this path:

- `SyncRuntimeVrState` no longer clears a configured OpenXR API before its
  monitor can activate it.
- profile capture now authoritatively enables render-statistics tracking;
  persisted preference side effects can no longer produce empty manifests.
- successful `xrEndFrame` is authoritative for two-eye submit telemetry and
  does not race the mirrored `IsInVR` flag.
- workload identity hashes the configured XR eye family, so runtime-owned eye
  cadence gaps do not look like workload mutations.
- the evaluator validates required fresh outputs across the retained capture
  and uses the most complete output frame for comparison identity.

### Validation

- Focused zero-readback, material-scatter, settings, phase-7, buffer-parity,
  and primary-reuse suite: 58 passed.
- `glslangValidator -S comp` accepted
  `GPURenderMaterialScatter.comp`.
- Release editor build completed with zero errors using the repository's
  existing native bridges; existing advisory/compiler warnings remain.
- PowerShell and JSON benchmark defaults now select the production
  `BindlessMaterialTable` path, while diagnostic names are explicit.
### Workstream 03 acceptance pause handoff

Workstream 03 remains open and workstream 04 remains blocked. Deterministic
Deferred and Uber CPU-direct/zero-readback parity is now exact (including
finite-depth coverage and seeded negative controls), the Quick scaling and
high-count crossover probes are directionally successful, and the benchmark
harness now retains each frame stream with its capture manifest. The first
formal scaling run is invalid because its earlier streams were pruned before
that harness fix and must not be reused as acceptance evidence.

The current blocker is
`Build/_AgentValidation/20260728-workstream03-acceptance/frame-data-reuse-diagnostic/reports/evaluation.json`:
1,061 of 1,129 eligible primaries were reused (93.98%, below the 99% floor),
with 68 records. The local zero-readback invariants still passed: zero
workstream-03-owned managed allocation, current-frame readback bytes, mappings,
full scans, and forbidden fallbacks. The preceding persisted probe identifies
the clustered misses as reason mask 66 (`Recorded | FrameData`) with constant
descriptor generation and no pending pipeline. Two CPU-stage reconciliation
issues also remain. The exact diagnosis, correctness evidence, formal scaling,
crossover, canonical desktop/RVC Gate, matched CPU-reference runs, and final
closeout order are recorded in
the [combined workstreams 03-05 validation gate](../../testing/rendering/03-05-optimization-validation-todo.md#workstream-03-validation).
