# OpenXR Monado And Desktop Framerate Investigation

Date: 2026-07-15  
Status: Immediate correctness and benchmark-hygiene fixes implemented; CPU/output architecture and trusted-occlusion gates remain open before Phase 5.2.5  
Related TODO: [Vulkan core hardening and device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

## Problem

The current Unit Testing World has extremely poor framerate while rendering
Monado-backed OpenXR eyes and the independent desktop editor output. The user
also observed three first-chance `InvalidOperationException` notifications in
the previous F5 run and asked whether the remaining Vulkan hardening TODO work
already owns the causes or whether an immediate fix should precede it.

## Active Workload

The checked-in local settings request Vulkan dynamic rendering, Monado OpenXR,
true single-pass stereo at the runtime-recommended 896x1007 resolution,
screen-space stereo preview, an independently rendered desktop editor camera,
TSR, and uncapped rendering with VSync off. The runtime log confirms
`VrMirrorMode=FullIndependentRender`, so this is not a cheap eye-texture mirror:
the engine prepares and renders a separate 1537x865 physical desktop view
alongside the 896x1007x2 layered eye family. Bootstrap resources also include
1920x1080 desktop-sized targets.

## Previous Run Baseline

Source session:
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-15_12-05-16_pid21312`

The normal logs preserve one of the three reported first-chance exceptions:

- At 12:05:41, desktop command-buffer recording reached mesh `sponza_194`, draw
  slot 96, after its frame-data pre-record reservation reported
  `descriptors pending`.
- `RecordCommandBuffer` deliberately threw `InvalidOperationException` before
  recording an unsafe command buffer. `WindowRenderCallback` caught it through
  the render diagnostic boundary and disabled the failing render path for
  100 ms.
- Immediately before the throw, eight render-thread buffer-upload jobs had
  already waited about 4.1-4.2 seconds in the queue. Texture telemetry later
  reported a maximum upload time of 3865 ms and maximum queue wait of 3690 ms.
- After the exception, CPU occlusion proxy preparation repeatedly remained
  `DescriptorsPending`. This ties the exception to incomplete asynchronous
  descriptor/program readiness during multi-output startup pressure, not to a
  GPU device-loss result.

Only one exception text is present in the persisted logs. The debugger's count
of three is therefore not evidence of three distinct root causes: this call path
throws during recording, catches and rethrows through the Vulkan frame loop,
and is finally caught by `XRWindow`; its warning paths are rate limited. IDE
first-chance notifications can consequently outnumber persisted root exceptions.

## Fresh Profiled Run

Source session:
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-15_12-15-55_pid22224`

Agent evidence root:
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-framerate-invalidoperations`

The editor was rebuilt successfully, launched with the same Unit Testing World,
OpenXR/Monado, independent desktop output, MCP, CPU profiler, frame-stat capture,
and coarse Vulkan GPU timing, then observed for about ten minutes. Six CPU frame
dumps, profiler snapshots, two camera-dependent desktop screenshots, and both
OpenXR eye-preview textures were captured. The editor was stopped after capture.

The fully instrumented run generated 173 MB of Vulkan log output plus 30 MB of
frame-stat/fps-drop output. Its absolute throughput is therefore a diagnostics
worst case, not a clean shipping baseline. The CPU/GPU split, sampled call
hierarchies, and output-family duplication remain decisive; earlier low-trace
captures in the related TODO already showed the same CPU-bound shape at a less
perturbed approximately 138-149 ms CPU p95 versus 4.8-5.5 ms GPU p95.

## Findings

### 1. The collapse is CPU-bound, not GPU-bound

After startup, the fully instrumented run completed only about 1-1.5 frames per
second. Representative settled distributions were:

| Measurement | p50 | p95 | Worst/notes |
| --- | ---: | ---: | --- |
| Whole output frame CPU | 259-332 ms | 2,138-2,323 ms | 4,936 ms reported by the final snapshot |
| Collect waiting for render | 149-223 ms | 2,065-2,182 ms | Render-thread backpressure |
| Vulkan command-buffer GPU | 13.9-14.3 ms | 15.1-17.1 ms | 19.2 ms observed worst in the sampled settled window |
| Vulkan fence wait | 0.04 ms | 0.08 ms | Not blocking on GPU completion |
| Raw `vkQueuePresentKHR` | 0.08-0.09 ms | 0.14-0.19 ms | Not a present/compositor stall |
| Vulkan command recording | 1.2 ms | 1.7 ms | Does not include upstream CPU command production |

The CPU p95 is over 100 times the coarse GPU p95. Update itself averaged about
1.4 ms; the long samples were on the render/output threads. The final CPU dump
history showed render-thread average 417.9 ms and maximum 2,980.8 ms, while the
other output thread averaged 287.9 ms and reached 2,460.8 ms.

### 2. CPU command production and independent output work dominate

The useful hot CPU dump caught 238.8 ms on the output thread:

- `XRViewport.RenderStereo`: 237.8 ms.
- `ViewportRenderCommandContainer.Execute`: 237.5 ms.
- The inner 96-child command container: 174.2 ms self time.
- Motion-vectors pass: 60.1 ms total across 129 mesh-render children.

Settled output-ledger samples put the independent desktop scene render near
269 ms p50 / 336 ms p95 and an OpenXR eye render near 202 ms p50 / 253 ms p95.
The XR submit interval reached about 909 ms p50; the ledger attributes the same
overall interval to both eyes, so those left/right values must not be added.
Likewise, the `Window present` wrapper was about 73 ms p50 while the raw Vulkan
present call was below 0.2 ms: that wrapper time is CPU backend/output work, not
the GPU or compositor waiting.

Typical stereo samples expanded two logical output requests into 13 output
events, five view families, and seven target variants. They published one scene
snapshot but still built visibility twice and reported zero shared-pass reuse.
This is exactly the independent-eye/desktop work multiplication described by
Phases 5.2.5-5.2.9.

### 3. The current F5 configuration adds avoidable diagnostic work

`Assets/UnitTestingWorldSettings.jsonc` says `GPURenderDispatch: false`, but
`UnitTestingWorldSettingsStore.RequiresGpuRenderDispatchForOpenXrVulkan`
unconditionally changes it to `true` for this Vulkan OpenXR lane. A contract
test explicitly requires that override. Separately, the persisted global engine
defaults at
`%LOCALAPPDATA%/XREngine/Global/Config/engine_defaults.asset` select
`VulkanGpuDrivenProfile: Diagnostics`.

The resulting runtime fingerprint was:

`Configured=Diagnostics Active=Diagnostics GpuDispatch=True(requested=True) MeshStrategy=GpuIndirectInstrumented ZeroReadbackDrawPath=FullBucketScan`

Startup then emitted CPU mesh safety-net fallback warnings when instrumented GPU
passes produced zero commands, and the live stats retained 87,056 frame-data
reservations, 1,390 mesh descriptor sets, 4,411 tracked descriptor sets, five
32 MiB mapped arena chunks, and 361 dynamic-uniform exhaustion events. This is
not the whole sustained CPU problem--the low-trace captures are also CPU-bound--
but forced GPU dispatch plus the Diagnostics/instrumented lane, CPU fallback,
and extremely high-volume logging materially amplify this run and make the JSON
setting misleading.

### 4. The `InvalidOperationException` is a descriptor-readiness guard

The fresh run reproduced the same exception family once, for `sponza_177`, slot
64:

`Mesh frame-data reservation failed before command recording: descriptors pending`

It followed render-thread uploads that had waited about 4.5-4.6 seconds.
`VulkanRenderer.RecordCommandBuffer` deliberately throws before recording with
an incomplete descriptor/frame-data manifest. The frame loop treats that as a
generic recording failure, recreates the desktop swapchain (130 ms in this run),
rethrows, and `XRWindow` catches it with a 100 ms circuit breaker. This is not a
device-lost result and it is not three distinct engine failures.

The previous session and the fresh session each persist exactly one
`InvalidOperationException` root throw. The IDE-reported count of three cannot
be reconstructed as three separate exceptions from the available logs; it is
consistent with repeated/reraised first-chance notifications through this
caught, rate-limited path. No evidence points to two additional exception types.

The guard itself is valid, but exception-driven control flow and a full
swapchain recreation are the wrong recovery for temporary descriptor readiness.
Manifest sealing should defer the entire output before command recording and
retry after publication, using the existing resource-pressure/deferred-recording
result path.

### 5. Two smaller correctness/telemetry issues remain

- Three retired resources remained pending and the oldest
  `ImGui.PipelineLayout` retirement age grew past 350 seconds even though its
  recorded graphics ticket was far behind the advancing graphics timeline.
  The queue depth is too small to explain the framerate, but the retirement
  predicate/ownership is not draining correctly.
- The profiler reported an 11.11 ms `VR90` budget yet zero missed deadlines and
  sometimes an achieved rate of 90 Hz while measured completion was about
  1-1.5 Hz. Scheduler acceptance work cannot trust those counters until their
  frame-completion/deadline accounting is repaired.

### 6. The eye and desktop outputs are live but visually corrupt

Both 896x1007 eye previews were non-black and camera-dependent desktop captures
changed with the camera, ruling out a stale/uninitialized target. Both eyes and
the desktop output showed severe overexposed green/red lighting corruption.
That is a separate rendering-correctness issue, not the measured framerate
bottleneck.

## Recommendation

Do not detour into an unplanned performance rewrite: Phases 5.2.5-5.2.9 already
own the broad root cause. In particular, 5.2.5 owns reusable versioned plans and
arenas, 5.2.6 owns one immutable scene/view-family DAG and composition-only
desktop mirror, 5.2.7 owns deadline/cadence scheduling, 5.2.8 removes CPU submit
serialization, and 5.2.9 removes remaining hot-path allocation and superlinear
work. Those phases are necessary to make eyes plus desktop scale correctly.

However, do not advance past the current 5.2.4c live gate yet. Fix these items
now because they invalidate or contaminate the benchmark that later phases use:

1. Replace descriptor-pending throw/swapchain-recreate recovery with whole-output
   pre-record deferral and retry; close the 5.2.4c manifest-sealing invariant.
2. Make the forced Vulkan/OpenXR GPU-dispatch override explicit and run normal F5
   performance baselines with a non-diagnostic profile and CPU safety-net/logging
   disabled. Keep `Diagnostics` as a named diagnostic lane.
3. Repair the stuck retirement predicate for the three old resources.
4. Repair missed-deadline/achieved-rate telemetry before accepting 5.2.7 results.

After those fixes, continue the todo rather than micro-optimizing the current
duplicated command traversal. The highest-value planned change for this exact
workload is 5.2.6's requirement that a normal desktop VR mirror compose from
already rendered eye output and add no independent scene traversal.

## Closeout Implementation And Results

The immediate fixes were implemented and validated on July 15 without running
either unfinished accelerated lane:

1. The Unit Testing World's explicit `GPURenderDispatch: false` is now
   authoritative. Vulkan OpenXR selects the non-diagnostic development-parity
   profile and `CpuDirect`; the smoke runner also preserves the parent process's
   preview setting instead of silently changing the workload.
2. Descriptor/program readiness is now a structured whole-output recording
   deferral before `vkBeginCommandBuffer`. It no longer throws an
   `InvalidOperationException`, recreates the desktop swapchain, or enters the
   window circuit breaker for temporary `DescriptorsPending` state.
3. Pipeline-layout retirement now drains against its exact completed queue
   tickets. The stale greater-than-350-second `ImGui.PipelineLayout` cohort is
   gone; the final run ended with zero pending entries and zero oldest age.
4. The output ledger now records actual output completion timestamps and derives
   rate, content age, and budget misses from those completions. Requested 90 Hz
   is no longer reported as achieved throughput.
5. The frame-wide reservation manifest is rooted by renderer and compatible
   family, so unrelated output families do not multiply every renderer's draw
   slots. One post-fix proof used 43,072 reservations / 15,453,120 bytes instead
   of the failing approximately 186,000-reservation / 32 MiB-ceiling shape; it
   resolved 73/73 queries and completed 118 strict-SPS submissions.

The final clean performance run is:

`Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-cpudirect-closeout/f5-baseline-final`

Its source engine session is:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-15_16-28-52_pid28092`

It used 100 warmup frames and 30 retained frames with `CpuDirect`,
`CpuQueryAsync`, true SPS, `FullIndependentRender`, screen-space eye preview,
TSR/bloom, diagnostics off, and Vulkan validation off.

| Measurement | Final clean CPU-direct result | Meaning |
| --- | ---: | --- |
| Whole-frame CPU average / p95 | 27.546 / 30.536 ms | About 36.3 internal frames/s on average; still CPU limited |
| Vulkan GPU average / p95 | 3.766 / 4.069 ms | Far below CPU time; GPU is not the limiting resource |
| Command recording average / p95 | 1.181 / 1.420 ms | Recording itself is not the dominant CPU cost |
| Raw present average | 0.080 ms | `vkQueuePresentKHR` is not the stall |
| True XR output completion | 4.155 Hz | Runtime/composition/output completion remains far below internal frame capacity |
| Desktop present completion | approximately 12-14 Hz | Independent desktop output remains an expensive additional consumer |
| Retained VR-budget misses | 30/30 | Completion telemetry now reflects the missed 11.11 ms budget |
| Strict-SPS submissions/fallbacks | 128 / 0 | CPU-direct true SPS is functional without hidden accelerated fallback |
| Invalid operations / VUIDs / device loss | 0 / 0 / 0 | The three reported first-chance notifications no longer reproduce |
| Pending retirement / oldest age | 0 / 0 | Exact-ticket retirement drains |
| Final reservation count / bytes | 28,992 / 7,275,392 | The manifest no longer hits the former capacity ceiling |

The original fully instrumented run's approximately 1-1.5 frames/s is therefore
not representative of the repaired baseline: high-volume diagnostics, forced
instrumented dispatch, frame-data multiplication, and descriptor-readiness
recovery were amplifying the problem. The repaired internal frame capacity is
roughly 36 frames/s, but actual XR completion is still only 4.155 Hz. The latter
is the value users perceive, and the CPU/GPU split shows that the next gains must
come from output scheduling, composition, and eliminating duplicated desktop/
eye preparation rather than GPU shader optimization.

## Occlusion Safety Finding

Exact enabled/off isolation found a separate correctness problem in Vulkan's
CPU-query path: zero-sample results caused valid Sponza foreground curtains and
columns to disappear. Moving the query before the contributing draw and using
the owning camera did not make those negative results trustworthy. Vulkan now
keeps issuing and resolving the queries for observation, but explicitly
quarantines each negative as forced-visible and emits a bounded
`CpuOcclusion.VulkanNegativeResultQuarantined` diagnostic. Positive results are
already fail-visible.

This policy restores the missing geometry and prevents a known false negative
from controlling visibility. It is deliberately not counted as a successful
occlusion implementation: the required sustained nonzero valid-cull result and
the exact enabled/off image-parity gate remain open. Short captures also showed
lighting/readback timing variation, so their numerical RMSE cannot be used to
claim parity even though the final quarantine image restores the foreground.

No `GpuIndirectInstrumented` or `GpuIndirectZeroReadback` cohort was launched or
used as evidence during this closeout.

## Current Decision

The immediate exception, benchmark-lane, retirement, telemetry, and manifest
faults are addressed now. Phase 5.2.4c must remain open for the long plateau and
capture-parity proofs and for a trusted Vulkan negative-query implementation.
Once those correctness gates are satisfied, the next implementation work is
the existing Phase 5.2.5/5.2.6 architecture: versioned reusable plans/arenas,
one immutable scene/view-family DAG, and composition-only desktop mirroring.
Those phases directly target the remaining 27.5 ms CPU frame and 4.155 Hz true
XR completion; more GPU instrumentation would not.
