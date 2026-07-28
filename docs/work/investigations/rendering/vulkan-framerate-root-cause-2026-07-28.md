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

## User validation

The user has not yet evaluated any attempted fix because no fix is in scope.
