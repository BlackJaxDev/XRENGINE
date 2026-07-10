# Vulkan CPU Framerate Regression Investigation

Date: 2026-07-09  
Status: Diagnosis complete; no performance fix implemented in this investigation  
Scope: Current Phase 5.1 working tree, desktop Vulkan CPU render-loop behavior, and correlated OpenXR/capture stalls  
Hardware: NVIDIA GeForce RTX 3090, Windows, .NET 10

## Outcome

The current default Vulkan path has a real CPU regression and should not be used
as the performance baseline for Phase 6 or the remaining dynamic-rendering work.
The largest measured problem is that desktop primary-command-buffer reuse is
opt-in and therefore disabled in a normal launch. That forces every frame to be
recorded again. Phase 5.1's new resource-lifetime and image-layout safety work
then amplifies the cost through allocation, retirement, invalidation, validation,
and submit bookkeeping.

In a controlled Release run with the production default (reuse disabled), the
loop delivered 56.86 completed render samples/s with 15.169 ms p50, 28.800 ms
p95, and 35.358 ms p99 CPU frame time. The 30.32-second capture recorded all
1,724 frames, allocated 6.226 GB inside the recording path, retired 5,178 image
views, and rejected four submissions because a referenced resource retired
after recording.

On the same settled 49-draw / 301,924-triangle workload with safe-reuse
experimentation enabled, the loop delivered 108.79 samples/s with 8.883 ms p50,
11.031 ms p95, and 12.128 ms p99. It clean-reused all 2,197 captured command
buffers, allocated zero bytes in the measured record path, retired no Vulkan
resources, and rejected no submissions.

This is not evidence that the environment flag should simply become the
production default. The performance cohort ran with validation disabled and did
not repeat the full Phase 5.1 correctness matrix. It is evidence that
production-safe reuse, precise invalidation, and stable resource identity must
be restored before continuing. The effective default currently violates the
June 23 baseline contract, which clean-reused 1,395 of 1,399 sampled frames.

Dynamic rendering itself is not the regression. With reuse stable, explicit
dynamic rendering measured 8.883/11.031/12.128 ms at p50/p95/p99; explicit
legacy render passes measured 8.944/11.442/12.782 ms. The difference is small
enough to treat the modes as CPU-parity in this single-run diagnostic.

## Decision

Do not use the current reuse-disabled result as a baseline and do not expect
core-hardening Phases 6-10 or the remaining dynamic-rendering migration to fix
it incidentally. Add a performance closeout gate to Phase 5.1, restore a
correctness-proven primary reuse path, eliminate steady resource churn, and then
record a three-repetition baseline before Phase 6.

The immediate work belongs partly in Phase 5.1 and partly in the separate
[Vulkan Primary Command Recording Fast Path TODO](../../todo/rendering/optimization/vulkan-primary-command-recording-fast-path-todo.md).
It must not remain merely a later optional optimization because the current
correctness architecture makes fresh recording a self-sustaining invalidation
loop.

## Test Method

### Live editor iteration

The first iteration used the freshly built Debug editor with the Unit Testing
World, Vulkan, desktop mode, `CpuDirect`, warm caches, diagnostics preset `Off`,
the CPU profiler, P3 logging, ImGui, and MCP on port 5467. Primary reuse was
left at its production default (disabled). The run was:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_20-55-26_pid34796`

Two MCP viewport captures were visually inspected from different camera
positions. The image changed with the camera, so the renderer was producing
live camera-dependent output rather than returning a stale target. The second
camera was accidentally placed close to an interior wall, but that made the
live change unambiguous. Screenshot capture was performed outside the timed
Release windows.

The Debug run had one important confounder: the implicit `VK_LAYER_OBS_HOOK`
layer was left enabled by the loader's Auto policy. It also had verbose P3 and
Debug-only checks enabled. Its CPU dumps are therefore used only to attribute
hot scopes, not as the numerical performance gate.

The run never became steady. Image-view retirement continued more than seven
minutes after launch. It also emitted resource-lifetime submission rejections
with Vulkan validation layers disabled, proving these were engine safety checks
rather than validation-layer overhead.

### Controlled Release cohorts

The editor was built with:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release --no-restore
```

The build completed with zero errors. It retained the repository's known
Magick.NET advisory warnings. Every cohort used:

- Unit Testing World, Vulkan, desktop/mono mode;
- `CpuDirect` submission;
- warm shader/texture/profile caches;
- diagnostics preset `Off`, all validation options disabled;
- `XRE_VK_OBS_HOOK=Disable`;
- P3 logging disabled;
- render scale 0.67, as recorded by the profiler manifest;
- RTX 3090;
- a 45-60 second warmup followed by a 20-30 second capture;
- frame statistics emitted once per completed render-frame sample.

The profiler manifest labels the internal configuration as `Development` even
though the executable was built and launched from the Release output. That is a
telemetry-label defect to fix, but all cohorts used the same binary and are
valid for relative comparison.

The harness had to force-stop each editor after the capture because graceful
shutdown exceeded its 20-second limit. Shutdown time is outside each capture
window, so it does not alter the frame distributions, but the teardown delay is
a separate lifecycle symptom.

These are diagnostic cohorts, not publication-quality benchmarks: one
repetition each, no fixed GPU clock policy, and an editor workload rather than a
locked benchmark scene. The large A/B effects and exact cache/allocation
counters are nevertheless decisive. Final gates should use three repetitions.

## Release Results

`Observed Hz` is completed render samples divided by capture wall time. It is a
CPU render-loop throughput measure, not a claim about display scanout FPS.
`GPU CB` is the coarse Vulkan command-buffer timestamp and is contained within,
not additive to, the CPU frame time.

| Mode | Observed Hz | CPU avg | CPU p50 | CPU p95 | CPU p99 | Vulkan CPU p50/p95 | GPU CB p50/p95 | Record / reuse |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Dynamic, reuse off, ImGui | 56.86 | 17.570 ms | 15.169 ms | 28.800 ms | 35.358 ms | 9.487 / 19.825 ms | unavailable | 1,724 / 0 |
| Dynamic, reuse on, ImGui, settled repeat | 108.79 | 9.169 ms | 8.883 ms | 11.031 ms | 12.128 ms | 4.273 / 4.853 ms | 2.531 / 4.023 ms | 0 / 2,197 |
| Dynamic, reuse on, Vulkan ImGui draw skipped | 110.74 | 9.012 ms | 8.720 ms | 10.938 ms | 12.584 ms | 4.131 / 4.834 ms | not summarized | 0 / 2,237 |
| Legacy render pass, reuse on, ImGui | 107.49 | 9.284 ms | 8.944 ms | 11.442 ms | 12.782 ms | 4.293 / 5.045 ms | 2.553 / 3.943 ms | 0 / 2,172 |

The matched dynamic reuse-off versus settled reuse-on comparison changed:

- observed throughput: 56.86 -> 108.79 samples/s, a 1.91x increase;
- p50: 15.169 -> 8.883 ms, 41.4% lower;
- p95: 28.800 -> 11.031 ms, 61.7% lower;
- p99: 35.358 -> 12.128 ms, 65.7% lower;
- Vulkan backend p50: 9.487 -> 4.273 ms, 55.0% lower;
- Vulkan backend p95: 19.825 -> 4.853 ms, 75.5% lower.

The settled workload medians matched at 49 draw calls and 301,924 triangles.
The comparison is therefore not explained by a different median scene load.

### Allocation and retirement behavior

| Counter over capture | Reuse off | Settled reuse on |
|---|---:|---:|
| Captured frames | 1,724 | 2,197 |
| Primary records | 1,724 | 0 |
| Clean reuses | 0 | 2,197 |
| Record-path allocation | 6,225,691,240 bytes | 0 bytes |
| Allocation per recorded frame | 3.61 MB / 3.44 MiB | 0 |
| Retired image views | 5,178 | 0 |
| Retired buffers | 193 | 0 |
| Retired images | 14 | 0 |
| Retired samplers | 14 | 0 |
| Retired descriptor pools | 23 | 0 |
| Submission-rejection samples | 4 | 0 |

The reuse-off run retired about 171 image views per second. Its dirty summaries
classified 1,399 samples as `reuse-disabled`, 302 as retiring an image view, 15
as retiring a buffer, 308 as `image-forced-dirty`, and four as submission
rejected. Categories overlap within a summary.

A separate reuse-on cohort captured too early in an unsettled state even after
a nominal 60-second warmup. It recorded 147 `query-ops` frames, reused 1,626,
and allocated 791 MB in the record path. This is why a fixed warmup duration is
not a sufficient performance gate. The later settled repeat produced 100%
clean reuse and zero retirement.

### ImGui is not the bottleneck

With the same settled workload and reuse enabled, skipping the Vulkan ImGui
draw changed p50 by only 0.163 ms and p95 by 0.093 ms. That is about 1.9% and
0.8%, respectively, and within single-run noise.

The settled ImGui-on frame attribution was:

- desktop scene command production/render callback: 4.407 ms p50, 5.387 ms p95;
- ImGui construction: 0.213 ms p50;
- Vulkan ImGui command recording: 0.112 ms p50;
- dynamic-text command recording: 0.060 ms p50;
- Vulkan backend/window-present callback: 4.274 ms p50, 4.854 ms p95;
- actual `vkQueuePresentKHR` wrapper: only 0.040 ms p50, 0.054 ms p95;
- coarse GPU command-buffer execution: 2.531 ms p50, 4.023 ms p95.

The profiler's `Window present` output owns the whole Vulkan backend callback;
it must not be interpreted as time spent only in `vkQueuePresentKHR`. Chasing
the output label as a present stall would target the wrong code.

### Dynamic rendering is at CPU parity

Against explicit legacy mode on the settled workload, dynamic rendering was
0.061 ms faster at p50, 0.411 ms faster at p95, and 0.654 ms faster at p99.
Coarse GPU times were also effectively identical. One repetition cannot prove a
small win, but it rules out dynamic-rendering mode selection as the cause of the
large current regression.

## CPU Frame-Dump Attribution

MCP CPU dumps sample threads asynchronously. They can catch render, collect, or
update work from different logical frames, so a single dump's `TotalThreadMs`
is not a coherent frame-time statistic. NDJSON distributions above are the
performance gate; the dumps identify hot scopes.

The first useful Debug dump caught the render phase:

- total sampled thread CPU: 45.115 ms;
- render thread: 42.549 ms;
- `EngineTimer.DispatchRender`: 29.108 ms;
- `XRWindow.RenderWindowViewports`: 7.980 ms;
- `Vulkan.FrameLifecycle.RecordCommandBuffer`: 18.302 ms;
- `Vulkan.RecordPrimary`: 12.708 ms;
- `Vulkan.RecordPrimary.MainOpLoop`: 11.961 ms self time;
- `Vulkan.RecordCommandBuffer.ResourcePlan`: 2.953 ms;
- `Vulkan.FrameLifecycle.Submit`: 2.014 ms.

Another dump caught an update-only slice at about 1.19 ms. A third caught a
68.0 ms collection/command-production slice with about 32.2 ms in collection.
This variation confirms why snapshots are attribution evidence rather than FPS
measurements.

The Release NDJSON field `vulkan_frame_record_command_buffer_ms` reports only a
narrow local interval and was around 0.16-0.24 ms even while every frame was
being re-recorded. It does not cover the full command-production/recording cost
seen in the hierarchical CPU dump. Cache record/reuse counters, allocation,
whole-frame distributions, and scoped CPU dumps are the reliable combined
evidence.

## Ranked Causes

### 1. Primary reuse is disabled by default

`VulkanRenderer.CommandBufferState.cs:73-74` enables desktop primary reuse only
when `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=1` is present.
`VulkanRenderer.CommandBufferRecording.cs:397-407` consequently marks every
static frame `reuse-disabled` and forces a fresh primary recording. Query ops
also force fresh recording.

This is the only change tested here that nearly doubled observed CPU-loop
throughput and removed all measured record allocation/retirement churn. It is
the top issue by a wide margin.

The correct fix is not an unvalidated flag flip. Make reuse safe under the Phase
5.1 lifetime/layout contracts, separate genuinely volatile/query work from
stable command ranges, validate it under dynamic and legacy modes plus OpenXR,
and then make the safe path the normal default.

### 2. Retirement invalidation is global despite having exact dependencies

`VulkanRenderer.ResourceLifetimeTracking.cs:1427-1452` looks up the exact set of
dependent command buffers and records its count, then discards that set for
invalidation purposes. A retiring resource invalidates all command-chain
secondaries, all OpenXR primaries, and all desktop primary variants.

`VkMeshRenderer.Buffers.cs:34-61` also clears/rebuilds the complete buffer cache
and unconditionally marks legacy mesh command buffers dirty. In the correlated
Phase 5 OpenXR run, 35 dirty-summary windows contained 34,920 `CollectBuffers`
invalidations and 4,348 `TrySyncMeshRendererIndexBuffer` invalidations. There
were 1,792 retirement invalidations: 1,005 image views, 572 buffers, 121 images,
and 94 samplers.

Use stable backing-handle + allocation-generation identity, make collection
diff-based, and dirty only the exact variants/chains whose dependency generation
changed.

### 3. Phase 5.1 safety bookkeeping is inside per-bind/per-barrier hot paths

The correctness checks are valuable, but their current placement is expensive
when re-recording:

- `TrackVulkanCommandBufferResource` takes `_vulkanResourceLifetimeLock` for
  every tracked bind and updates reverse dependency hash sets
  (`ResourceLifetimeTracking.cs:365-488`).
- `TrackVulkanDescriptorSetBinding` holds the same lock, expands every descriptor
  reference, and in Debug walks descriptor image layouts on every bind
  (`ResourceLifetimeTracking.cs:1015-1091`).
- `RecordImageAccess` takes `_vulkanImageLayoutLock` and writes per
  aspect/mip/layer for every tracked barrier (`Synchronization.cs:792-832`).
- submit validates entry/exit subresource maps, publishes them, and
  `AdvanceCompletedImageLayouts` scans all tracked subresources
  (`Synchronization.cs:1198-1369`).

In the correlated Phase 5 OpenXR logs, 172 FPS-drop samples reached
`Vulkan.RecordPrimary.MainOpLoop`; their hot-sample average was 137.829 ms and
their maximum was 708.551 ms. Those are drop samples, not normal-frame averages,
but they show where the failure tail accumulates.

Record dependencies and layout deltas in command-buffer-local structures,
deduplicate descriptor expansion once per descriptor-set/recording generation,
and bulk-publish the finished state. Keep Debug correctness validation, but
cache it by command-buffer generation, descriptor generation, and layout-state
version.

### 4. Resource-planner eviction and replacement can synchronously wait for all GPU work

This is the most severe correlated OpenXR/capture tail problem, although the
settled desktop Release captures did not hit it.

`BuildFrameOpPlannerStateKey` includes output framebuffer/target identity and
resource generation (`VulkanRenderer.ResourcePlannerState.cs:1409-1422`).
Rotating OpenXR images or context identities can therefore create distinct
planner states. When the state cache exceeds its cap,
`PruneFrameOpResourcePlannerStatesToCapacity` calls `WaitForAllInFlightWork`,
tears down state, and then calls a force-flush helper that waits again
(`ResourcePlannerState.cs:858-909`). Physical plan replacement also immediately
force-waits (`ResourcePlannerState.cs:2508-2532`).

In the current Phase 5 OpenXR run
`xrengine_2026-07-09_20-48-13_pid57032`, profiler/log timestamps correlate a
43,676.012 ms `ResourcePlan` drop with planner-state pruning and an 8,437.019 ms
drop with resource-plan replacement/pruning. That run used SteamVR OpenXR and
had the OBS layer enabled, so it is not a clean baseline, but neither confounder
explains engine calls that deliberately wait and force-flush for seconds.

Key planner state by physical plan/context compatibility rather than rotating
external image identity, bind the acquired external image separately, and keep
replaced allocators alive behind retirement tickets. Command recording must not
globally drain the device.

### 5. Submit performs locking and multiple full dependency/state scans

`SubmitToQueueTracked` acquires `_oneTimeSubmitLock`, builds diagnostic context,
validates ordered image state, validates resource lifetimes, submits, republishes
lifetime state, republishes image state, and advances completions
(`VulkanRenderer.Synchronization.cs:278-338`). The aggregate submit scope does
not distinguish queue-lock wait, CPU validation, driver submission, or
publication.

The settled desktop submit path was healthy at about 0.414 ms p50, so this is
not the main steady desktop problem after reuse works. In the correlated OpenXR
drop log, however, 58 submit hot samples averaged 158.710 ms and reached
320.835 ms; 23 overlapped long `UpdateOpenXRRuntime` samples, consistent with
shared lock contention.

Add subscopes and counts before changing synchronization. Replace full
completion scans with per-submission touched-key lists and narrow lock
ownership. Do not mistake the whole aggregate for driver cost.

### 6. Recording contains concrete avoidable allocations and repeated scans

Measured allocation reached 3.44 MiB per fresh primary frame. Identified
contributors include:

- `QueryCurrentAttachmentLayouts`: a new `ImageLayout[]` per FBO begin
  (`VulkanRenderer.CommandBufferRecording.cs:7589-7623`);
- `SplitDynamicUiBatchTextFrameOps`: two arrays for dynamic UI splitting
  (`VulkanRenderer.FrameOpDiagnostics.cs:443-481`);
- exact-length frame-op arrays in mesh-renderer upload/draw drains;
- image-layout snapshot arrays plus per-group arrays/sorts
  (`VulkanRenderer.Synchronization.cs:1372+`);
- planner registry merge `List`/array creation and filtered metadata arrays;
- retirement-drain temporary lists/arrays;
- duplicate render-graph sorting and O(n^2) context-order/clear scans in some
  ordering modes.

The dynamic-rendering-specific `DynamicRenderingFormatSignature` `ToArray()`
work is real, but it is only one subset of this larger generic recording
allocation problem. Pool/capacity-reuse buffers, use spans/counts, structurally
share snapshots, and sort once.

### 7. Remaining steady CPU time is command production plus backend coordination

Once reuse was stable, about 4.4 ms p50 remained in desktop scene command
production and 4.27 ms in the Vulkan backend callback. The GPU command buffer
used only 2.53 ms p50. Visible collection itself was small in Release
(`CollectVisible` p95 about 0.28 ms), and ImGui was small.

The next steady-state optimization after correctness-safe reuse is to cache
stable command ranges/secondary buffers and reduce default-pipeline command
production. GPU shader/pass optimization will not remove the current CPU gap.

## Impact of the Remaining Roadmaps

| Planned work | Expected CPU-FPS effect | Guardrail |
|---|---|---|
| Core Phase 6 descriptor robustness | Neutral if fingerprints are cached; possible improvement from precise invalidation; regression risk from per-draw hashing/scans | Cache immutable fingerprints and update on generation changes. Do not add every field to a fresh per-draw hash. |
| Core Phase 7 OpenXR synchronization | Potentially large OpenXR tail improvement, little desktop effect | Replace per-submit fence creation/indefinite waits with frame-slot/timeline ownership without adding more unconditional waits. |
| Core Phase 8 probe/IBL/upload/TDR budgeting | Strong worst-frame improvement while capture work is active; steady idle FPS mostly neutral | Slice the currently indivisible capture/finalize/mip work, not only check a budget before a large item. |
| Core Phase 9 tests/tooling | No direct FPS gain, but the correct home for a regression gate | Extend the harness to gate every retired resource kind, allocations, rejection, and global waits. |
| Core Phase 10 docs/workflow | Neutral | Record the performance contract and diagnostic controls. |
| Dynamic Phase 1.1 allocation removal | Direct, low-risk CPU improvement on fresh dynamic recordings | Use bounded inline/value storage and prove zero per-draw identity allocation. It does not replace the generic recording cleanup. |
| Dynamic parity/promotion | Neutral; current dynamic/legacy CPU parity is already encouraging | Keep a <=5% unexplained-regression gate across repeated cohorts. |
| Dynamic local read | Primarily GPU bandwidth/pass-count work | Adopt only for a measured pass. It will not fix reuse-disabled CPU recording. |
| Descriptor heap completion | Conditional CPU upside from batched writes; significant invalidation/synchronization risk | Batch publication/generation changes and benchmark against descriptor indexing. |
| Shader objects | Possible cold-create/hot-reload win; likely small warm steady-FPS effect | Preserve the roadmap's go/no-go gate; emitting all dynamic state can add driver-call CPU. |
| Vulkan XR foveation | GPU fragment-work win when GPU-bound | Do not invalidate command buffers every gaze sample. It does not address the current CPU bound. |
| Transient/GPU-driven/ray-tracing compatibility | Mostly validation/GPU-memory behavior | Treat performance changes as measured side effects, not presumed CPU wins. |

No unchecked item in the two main todos directly restores safe primary reuse or
replaces global retirement invalidation. Therefore the future phases will not
automatically cure the current regression.

## Required Pre-Phase-6 Performance Gate

### Runtime acceptance

For a static, fully loaded Unit Testing World on this machine/settings:

- primary command-buffer reuse is production-safe and enabled by normal policy;
- clean reuse is at least 99% after the stability gate, with every miss reason
  accounted for;
- record-path allocation is zero in a stable 60-second capture;
- retired image view/image/buffer/sampler/framebuffer/pipeline/descriptor-pool
  counters are all zero in the stable window;
- resource-plan replacement and allocator-state pruning are zero;
- submission rejection is zero;
- no command-recording path calls `WaitForAllInFlightWork` or force-flushes;
- on the RTX 3090 / 0.67-scale diagnostic scene, CPU frame p50 is <=10 ms,
  p95 <=12 ms, and p99 <=14 ms, or an explicitly approved new baseline explains
  the difference;
- dynamic rendering stays within 5% of legacy at p50/p95/p99 across three
  repetitions;
- validation-enabled correctness cohorts remain separate from
  validation-disabled performance cohorts, and both pass.

OpenXR/capture adds these gates:

- no multi-second `ResourcePlan`, submit, runtime-update, or retirement wait;
- no planner-state churn from rotating swapchain image identity;
- no per-eye unconditional CPU fence wait beyond image/frame-slot reuse needs;
- probe/IBL work stays within the configured frame budget and retirement depth
  remains bounded.

### Harness corrections

`-FailOnSteadyStateResourceChurn` currently checks resource-plan replacements
but misses image views and other retired resource kinds. Extend it to gate:

- image views, images/image memory, buffers/buffer memory, samplers,
  framebuffers, pipelines, and descriptor pools;
- command-buffer record/clean-reuse/forced-dirty counts and allocated bytes;
- submission-rejection and `Command buffers marked dirty` events in the exact
  capture timestamp range;
- planner prune, global wait, and force-flush events;
- stable draw/frame-op/triangle/viewport/render-scale/backend values.

Capture should start only after those counters and asset/shader activity are
stable, not after a fixed number of seconds. The manifest should also record
render-target mode, primary-reuse policy, OBS-hook policy, ImGui-skip state, and
the actual build configuration.

After the canary is clean, run 60-second captures with three repetitions for
`CpuDirect`, then a separate `GpuIndirectZeroReadback` cohort. Do not pool the
strategies.

## Recommended Implementation Order

1. Make primary reuse safe under the Phase 5.1 contracts and separate query or
   truly volatile ops from stable ranges.
2. Stop transient image-view/buffer identity oscillation and make mesh buffer
   collection diff-based.
3. Use the existing reverse dependency index for targeted variant/chain
   invalidation instead of global dirtying.
4. Batch command-buffer lifetime/layout tracking locally and publish once.
5. Remove render-thread global waits from planner prune/replacement; retire
   versioned allocator state asynchronously.
6. Decompose submit timing, then eliminate full-dictionary completion scans and
   unnecessary lock contention.
7. Pool recording/snapshot/drain arrays, remove duplicate sorting, and complete
   dynamic Phase 1.1 inline signatures.
8. Land the expanded performance gate before beginning Phase 6.

## Evidence

Ignored task evidence is under:

`Build/_AgentValidation/20260709-210000-vulkan-cpu-framerate/`

It contains:

- two inspected MCP viewport captures;
- three Debug CPU frame dumps;
- Debug and Release build transcripts;
- full harness transcripts for every cohort;
- copied profiler manifests and compact speed-profile summaries.

Tracked context used in the comparison:

- [June 23 Vulkan render-loop speed work](../../vulkan-render-loop-speed-2026-06-23.md)
- [Core hardening/device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)
- [Dynamic-rendering migration TODO](../../todo/rendering/vulkan-dynamic-rendering-migration-todo.md)
- [Primary command recording fast-path TODO](../../todo/rendering/optimization/vulkan-primary-command-recording-fast-path-todo.md)

No renderer source was changed during this investigation.
