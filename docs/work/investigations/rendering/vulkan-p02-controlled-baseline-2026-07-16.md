# Vulkan P0.2 Timing And Controlled Baseline

Date: 2026-07-16

## Outcome

The desktop Vulkan scene-recording timer was wrong: successful scene recording
was omitted while ImGui snapshot and overlay work was charged to the same value.
Scene, ImGui snapshot, ImGui recording, and dynamic-text overlay recording now
have separate counters. `vulkan_frame_record_command_buffer_ms` is the sum of
actual scene/overlay command recording only; snapshot time is excluded.

The warmed baseline shows that occlusion queries are not the primary frame-time
problem. Both occlusion modes spend most slow frames rebuilding Vulkan work. The
largest measured CPU scopes are resource planning, frame-data refresh, and
primary command recording. The normal path was also allocating diagnostic reuse-
miss strings; those strings are now produced only when Vulkan diagnostic tracing
or command-chain validation is enabled.

## Instrumentation Added

- CPU time and managed allocation/high-water counters for frame-op preparation,
  resource planning, frame-data refresh, packet construction, primary recording,
  secondary recording, descriptor publication, and submission.
- Separate frame lifecycle counters for ImGui snapshot, scene recording, ImGui
  recording, and dynamic-text overlay recording.
- Allocation scopes for visibility collection and collect swap/publication.
- Allocation-free numeric primary/secondary command-buffer decision flags for
  recording, clean reuse, invalidation, and LRU eviction, plus consumed
  visibility generation, structural signature, pipeline/descriptor generation,
  and swapchain slot.
- Profile-capture and MCP exposure for all new counters. Existing visibility
  wait, worker wait, submit, present, GPU timestamps, record/reuse counts, and
  invalidation counters remain separate.

## Controlled Cohorts

All cohorts used Release, desktop Vulkan, dynamic rendering, CPU-direct
submission, warm caches, `BlockUntilFresh`, the default fixed editor camera,
1920x1080 output, and workload identity `6325129635686552578`.

Performance runs used a 10-second warmup and 20-second capture with validation,
dense GPU timestamps, and detailed Vulkan tracing disabled.

| Metric | Disabled | CpuQueryAsync |
|---|---:|---:|
| Samples | 653 | 516 |
| CPU frame p50 / p95 / p99 (ms) | 21.433 / 124.559 / 160.980 | 20.792 / 119.710 / 146.946 |
| CollectVisible p95 (ms) | 3.424 | 5.719 |
| Vulkan frame p50 / p95 (ms) | 12.450 / 96.882 | 11.553 / 92.299 |
| Command record p50 / p95 / worst (ms) | 11.755 / 95.373 / 374.139 | 10.784 / 90.874 / 398.237 |
| Primary records / clean reuses / forced dirties | 62 / 591 / 51 | 93 / 423 / 78 |
| Record-path managed allocation | 807,808,944 B | 990,068,320 B |
| Draw calls p50 | 67 | 54 |

The initial Disabled launch was rejected as a baseline because three workload
identities occurred during capture. It is retained only as exploratory evidence;
the warmed repeat above has one identity and is the comparison cohort.

Correctness/GPU cohorts used a 10-second warmup and 10-second capture with Vulkan
validation and dense GPU timestamps enabled. Both logs contained zero validation
errors, VUIDs, `InvalidOperationException`s, or submission rejections.

| Metric | Disabled | CpuQueryAsync |
|---|---:|---:|
| CPU p50 / p95 / p99 / worst (ms) | 22.996 / 143.443 / 172.034 / 198.970 | 20.773 / 122.051 / 136.588 / 172.595 |
| GPU p50 / p95 / p99 / worst (ms) | 23.570 / 143.441 / 161.395 / 161.395 | 21.717 / 128.986 / 130.771 / 130.771 |
| Scene record p50 / p95 (ms) | 11.434 / 110.703 | 10.437 / 94.997 |
| Resource planning p95 (ms) | 22.163 | 22.079 |
| Frame-data refresh p95 (ms) | 6.568 | 10.945 |
| Primary recording p95 (ms) | 86.304 | 72.462 |

The CpuQueryAsync correctness window reported the effective mode as
`CpuQueryAsync`, tested 5,428 candidates, culled 3,776, submitted 630 queries,
resolved 631 query results, and retained at most zero pending results at a sampled
frame boundary.

## Timer Reconciliation

Across all 653 retained Disabled performance samples:

- scene record p50/p95: 11.380/94.897 ms;
- overlay record p95: 0.584 ms;
- ImGui snapshot p95: 0.003 ms and excluded from record time;
- maximum error between the aggregate recording counter and scene plus overlay
  recording counters: 0.001 ms.

Nsight Systems 2026.3.1 was subsequently installed and an elevated Windows
capture completed with Vulkan API tracing, Vulkan annotations, process-tree CPU
sampling/context switches, 1 kHz sampling, and Release `CpuDirect` desktop
Vulkan. The finalized report is 41.5 MB and its SQLite export is 501.9 MB.

The capture contains 31 presented Vulkan frames, 564,644 sampled callchains
(9,559,017 callchain rows), and 151 `vkBeginCommandBuffer`/
`vkEndCommandBuffer` calls. NVIDIA defines Vulkan command-buffer creation time as
the interval between those two calls. Excluding the first eleven captured
frames, pairing the remaining begin/end calls and taking the longest
command-buffer span per frame produces:

| Measurement | p50 | p95 |
|---|---:|---:|
| Nsight present interval | 164.490 ms | 251.896 ms |
| Nsight longest command-buffer creation span | 71.907 ms | 114.127 ms |
| Nsight sum of all command-buffer creation spans | 74.956 ms | 115.175 ms |
| Engine validation-cohort scene record scope | 11.434 ms | 110.703 ms |

The scene-record p95 differs by 3.1%, which is within the expected perturbation
from 1 kHz CPU sampling, Vulkan interception, and the trace starting during a
command-buffer recording interval. The p50 is not comparable: the short Nsight
window contains startup/streaming work and runs about 7.7x slower than the
warmed performance cohort. Per-frame Vulkan API-call execution itself is only
0.882/2.443 ms p50/p95; most of the command-buffer creation span is therefore
engine-side planning, refresh, and managed recording between Vulkan calls, which
agrees with the internal stage decomposition. Snapshot time remains excluded
from the engine scene-record scope, and the internal aggregate still reconciles
to scene plus overlay recording within 0.001 ms.

The trace was requested with `vulkan-annotations` and the engine command-buffer
label override, but the export contains no Vulkan Debug Utils table or marker
rows. A minimal elevated retry using Nsight's documented `--env-var` option
failed inside Nsight 2026.3.1 with `std::bad_function_call` and produced no
report. This is now a narrow marker-export blocker rather than a CPU/API timing
blocker; do not claim that the current `.nsys-rep` visually identifies named
engine passes.

## Root-Cause Direction For P0.3

The baseline makes the next work concrete:

1. Resource planning and reusable frame-data refresh currently occur before a
   clean primary can be reused and allocate heavily even on reuse frames. In the
   653-sample Disabled window they allocated about 1.69 GB and 2.06 GB,
   respectively.
2. Forced invalidation is dominated by repeated
   `XRRenderPipelineInstance.SetTexture` dirties, which drives full primary
   re-recording.
3. Primary recording, not packet construction or overlay work, accounts for the
   long recording tail (73.737 ms p95 in the Disabled performance window).
4. CpuQueryAsync increases collection cost and cache churn, but fixing it alone
   cannot recover desktop frame rate while planning/refresh/primary recording
   remain this expensive.

## Evidence

- `Build/_AgentValidation/20260716-p02-vulkan-baseline/reports/disabled-summary.json`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/reports/cpu-query-async-summary.json`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/reports/validation-disabled-summary.json`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/reports/validation-cpu-query-async-summary.json`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/reports/nsight-reconciliation.json`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/nsight/p02-disabled-release-short.nsys-rep`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/nsight/p02-disabled-release-short.sqlite`
- `Build/_AgentValidation/20260716-p02-vulkan-baseline/nsight/p02-disabled-release-short_vulkan_api_sum.csv`
- Disabled performance log:
  `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_12-32-51_pid30184`
- Disabled validation log:
  `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_12-34-30_pid42644`
- CpuQueryAsync validation log:
  `Build/Logs/Release_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_12-35-36_pid3732`

The benchmark shutdown handshake timed out in all cohorts, so the harness forced
process termination after capture. This did not invalidate retained samples, but
GPU timing dump files were unavailable; the dense validation windows still
contained 230 and 236 ready GPU samples.
