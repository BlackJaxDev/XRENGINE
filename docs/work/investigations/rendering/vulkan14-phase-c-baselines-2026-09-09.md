# Vulkan 1.4 phase C measurement baseline

Opened: 2026-09-09. Status: C1/C2 measurement deliverables complete; invalid and noisy comparisons deferred as listed below.

Scope: phase C of the Vulkan 1.4 performance and shader modernization TODO.
The uncommitted A/B closeout is part of this baseline. No phase D–I performance
change is included, and no speedup is claimed by collecting these measurements.

## Measurement contract, declared before collection

Use the tracked `VulkanPerformance/Cohorts/vulkan14-phase-c.jsonc` fixture:
256 procedural deferred boxes sharing one mesh and 16 materials, a directional
light with shadows, DefaultRenderPipeline and FXAA. No imported user assets or
editor panels are required. Render at 1920x1080, scale 1.0, windowed, VSync off,
uncapped presentation, configured 60 Hz update and 30 Hz fixed update. Keep GPU Hi-Z,
GPU-generated indirect work, dynamic rendering and normal command-reuse policy.
Record effective values; reject silent renderer, submission or binding fallbacks.

Compare descriptor indexing and descriptor heaps only within the same workload
and sampling policy. Warm static and moving-camera runs establish ordinary frame
cost. Material edits, streaming and volatile UI are distinct sustained workloads;
resize and process/cold compilation are separate transient windows. Do not pool
these into a single FPS figure or treat startup samples as steady state.

The production procedure uses Release binaries, `ReleaseBenchmark`,
25 seconds warmup, 60 seconds capture and three fresh processes per workload.
Use a five-second output-scheduling stability gate for static/moving cases; deliberate continuous
mutation uses a fixed warmup and separately reports event cadence. Sample one
completed frame in ten using the existing asynchronous NDJSON capture. Preserve
frame IDs and capture timestamps; summarize only the selected capture window.
Report sample counts, p50/p95/p99 and min–max variation of per-run medians.
GPU samples must have completed timestamps; absence is unavailable, not zero.
The gate waits for stable output identity, ready material rows, and completed
startup upload/shader work. Descriptor-pool allocation and auto-uniform
compatibility writes remain measured engine costs rather than reasons to demand
an indefinitely resource-idle window. A fully ready table of untextured rows
legitimately has texture-descriptor generation zero.

Correctness uses standard and synchronization validation in separate sessions,
plus viewed captures before/after input changes. Production runs disable those
layers, device-fault tracing, descriptor tracing, dense GPU timestamps, profiler
panels and MCP. Capture/aggregation overhead remains measurable observer cost;
do not describe this as an entirely uninstrumented run.

## Criteria for subsequent experiments (C2)

| Candidate change | Primary evidence | Required companion evidence |
| --- | --- | --- |
| Wait placement / dependency scope | Full CPU frame and render preparation p50/p95/p99; causal slot/wait distributions; completed GPU frame time | Same visible work, no ownership/validation failures, no new deferrals or latency regression |
| Command reuse / heap publication | Full preparation + refresh + recording cost and managed bytes, across static, camera, material and UI workloads | Reuse/call counts explain the result; unchanged image/resource generations remain valid; no upload/retirement growth |
| Queue overlap | Completed GPU frame time and CPU submission/wait cost | Real overlapped work, queue ownership proof, no increased latency or readback stalls |
| Shader/compiler change | Cold process/compile latency and warmed CPU/GPU distributions, cache misses/hits separately | Same shader capabilities and viewed output; cache identity and compile failures accounted for |

**Retain** a change only if all correctness checks pass and three matched runs
show at least a 5% primary-cost improvement exceeding measured baseline
variation, with no greater than 5% regression in full-frame p95/p99 or latency.
Keep managed allocation bytes at or below baseline; an allocation increase needs
an explicit measured tradeoff. **Reject** correctness failures, hidden capability
loss, or a repeatable total-cost regression. **Defer** a noisy or incomplete result:
if run-median spread exceeds 7.5%, or the change is smaller than observed noise,
collect longer/additional matched runs before deciding. These thresholds are
comparison policy, not claims about current engine performance.

Before each candidate, pin its workload, executable/source hash, cache state,
binding backend, render policy and latency/output expectations. Alternate the
baseline/candidate order across repetitions. Record both run-level metrics and
the decision; a high cache-reuse ratio alone never satisfies the criteria.

## Environment

AMD Ryzen 9 7950X3D (16 cores / 32 logical processors), approximately 47.1 GiB
physical RAM, RTX 3090 (24 GiB), NVIDIA driver 610.88, Windows 11 Pro build 26200,
.NET SDK 10.0.301, PowerShell 7.6.5, Vulkan SDK/loader 1.4.350, device API 1.4.341.
GPU clocks are unmanaged boost; record temperature/clock/power samples and
run-to-run variation. Do not change the user's driver or global power settings.

Task evidence root: `Build/_AgentValidation/20260909-132157-vulkan14-c/`.
This note will retain the numeric results and reproduction commands; ignored
artifacts provide supporting raw samples and images rather than required code.

## Fixture and capture validation

The screen-space UI fixture alternates white `Profile workload A` / `B` text at
4 Hz. `capture_viewport_screenshot` now accepts `include_screen_space_ui: true`;
the response identifies `CompositedWindow` versus the default `ViewportTarget`.
This option is a diagnostic desktop-window capture and is excluded from timing
runs. Vulkan waits for the final graphics timeline, copies the acquired window
image, restores its present layout, and completes the copy before handing image
ownership to WSI. Ordinary frames do not allocate a capture queue or take this
wait. Detached targets and VR-eye requests reject the option explicitly.

Release validation on 2026-09-09 visually confirmed both UI text states in the
composited Vulkan captures and no UI in the viewport-only capture. Retained MCP
validation counters were zero errors and zero messages. Example evidence under
the task root: `mcp-captures/Screenshot_20260909_135359_176_f9cdb215dee343fbada0425c8513832d.png`
(A), `Screenshot_20260909_135409_885_4d1e3f50306940828faf6065ffd92b7d.png` (B),
and `Screenshot_20260909_135409_522_7d37c37bd0144100b530865d33fa8121.png`
(viewport only). The successful B readback reported a 1.176 ms GPU copy and
74.6 ms CPU image processing; these diagnostic costs are not baseline frame
timings.

The initial streaming fixture repeatedly uploaded into two already published
texture objects. It exposed a fail-closed descriptor lifetime failure: frame
preparation rejected the global material heap array because a referenced image
view had no published generation. Waiting for the first completed upload before
exposing the texture delayed but did not resolve the failure. Metadata changes
recreating the image before immutable-slot ownership transfer were subsequently
identified and corrected as described below. Preserve this as a separate re-upload correctness
case; its rejected frames are invalid performance samples. Evidence:
`reports/streaming-first-resident-stats.json` and `reports/streaming-stats2.json`.

The asset-streaming cohort loads fresh texture objects from the same pinned
image, retains at most two pending jobs, and checks for completed uploads to
publish at 4 Hz. Visible publication cadence depends on upload completion and is
reported separately. The prior resident object stays visible during an
upload. This measures new-asset streaming, including its deliberate asset
allocations, and does not establish correctness or performance of in-place
re-upload. It grows the asset set during the timed process rather than reaching
a stationary memory state. Compare schedule, completion and visible-publication
counts alongside timing. File/decode cache behavior remains streaming-manager-owned.

Initial OpenGL runs rendered a black window, independently confirmed with a
native window capture. Later inspection found that the UI fixture selected the
bootstrap editor camera rather than the active play-mode camera. The fixture now
selects the active viewport camera after rendering begins. With OpenGL CPU
submission and `GPURenderDispatch=false`, scene capture succeeds, but the text
overlay remains absent from the OpenGL window. Its UI rendering is not validated;
do not describe the screenshot result alone as passing that check.

### Fixture corrections found with composited capture

The second matrix was stopped at 2026-09-09 22:04 UTC, during the second heap
streaming warmup, to complete missing visual checks. Its final partial cohort is
invalid. Do not promote the earlier moving or streaming rows to a baseline:

- Play mode creates `Player1_Pawn`, which does not use the bootstrap editor
  camera. The original moving component never changed the displayed camera.
  It now lives on the unit-world root and resolves the active viewport camera
  after initial rendering. Two viewed captures show different camera positions
  and orientations: `Screenshot_20260909_150900_996_7f62e8c5b35f4ff2a3c4b9b23c6b1f02.png`
  and `Screenshot_20260909_150924_163_10e5db5e3ffd4cec8a04438a3985d817.png`.
  A completion log reports the camera and applied update count; the summary
  rejects captures missing this evidence.
- The streamed quad was back-facing despite its double-sided request and was
  absent from viewed output. The fixture now uses a front-facing +Z quad with
  standard back-face culling. The Rive source image is visible in
  `Screenshot_20260909_151357_568_9e12827086414e789a4cf734491e2977.png`, with 257
  GPU scene commands and zero Vulkan validation messages. The earlier binding
  counters alone did not prove visibility. This does not establish correctness
  of the double-sided route.
- The UI fixture now resolves the active viewport camera too. Its earlier
  editor-camera attachment was unreliable across rendering backends.

All image paths above are under the task root's `mcp-captures/`. The latest
capture implementation was rebuilt with zero warnings/errors and its composited
readback visually retained the UI while the corrected camera moved. Detailed
frame checks remain separate from production measurements.

Final integration moves root-component installation from unit-box construction
to the shared editor-camera setup hook. This preserves moving-camera profiling
in worlds without unit boxes and installs only one root component. The measured
fixture still uses the same root component, active-camera resolver and motion
function; the matrix keeps its frozen executable while this setup change is
validated separately.

The final Release build (`logs/active-camera-ui-build.log`, zero warnings/errors)
also passed the corrected UI fixture on both Vulkan descriptor backends:

- Heap, composited: `Screenshot_20260909_152221_705_fb954b25f7984e77bb044e785f4bd861.png`
  shows the box scene and white text; default capture
  `Screenshot_20260909_152222_035_231164c1d02945938492c0ac9d479b1d.png` shows only the scene.
- Indexing, composited: `Screenshot_20260909_152355_999_13d170252fb0407ea6f999bb10a2ab4f.png`
  shows the scene and white text. `reports/final-heap-ui-stats.json` and
  `reports/final-indexing-ui-stats.json` each report zero Vulkan validation errors
  and messages. The images were opened and inspected.

### Streaming lifetime correction and remaining limit

Retirement diagnostics identified the stale view as an imported upload view
retired through `VkImageBackedTexture.DeleteObjectInternal.BackingImageView`.
Upload preparation was applying `Mipmaps`/format/sampler metadata through normal
property notifications, which destroyed the currently published native image.
Preparation now keeps the native image and layout unchanged, suppresses only its
own metadata notifications on the applying thread, and derives the pending
upload's layout independently. Publication transfers the old allocation to the
immutable descriptor element only after the upload completes. The replacement
sampler uses the pending image's mip count. Exact descriptor publication also
invalidates the heap's cached image-info array.

This removed the stale-view failure in the live synchronization-validation run
and allowed streaming to advance to frame 923. It then failed closed on descriptor
heap capacity (`offset=130304`, `size=1056`, `capacity=131072`). That sustained
streaming limitation is unresolved; this is not a passing streaming performance
baseline. Both runs retained zero Vulkan validation messages. Relevant evidence:
`reports/streaming-retirement-owner-stats.json` and
`reports/streaming-atomic-metadata-stats2.json`.

The final front-facing fixture reproduced heap exhaustion at frame 879:
`offset=130496`, `size=1088`, `capacity=131072`. Frame 3660 was still rejected
with the same terminal readiness failure. Evidence is retained in
`reports/final-heap-streaming-stats1.json`; this is an engine failure despite zero
Vulkan validation messages. Heap streaming is excluded from the completed timing
matrix, and no performance claim may use its rejected-frame loop. Descriptor
indexing streaming has visible output and is measured separately. Resolving heap
array version reclamation requires a lifetime-safe change, not an unexplained
capacity increase.

The second and third final indexing streaming attempts also failed the declared harness
contract: startup/warmup recorded 8192 GPU readback bytes even though
`GpuIndirectZeroReadback` was requested. The timed capture had zero readback
bytes and zero rejected/failed frames, and texture uploads reported zero
failures. Preserve that distinction, but exclude the attempt rather than
silently relaxing the whole-process zero-readback check. Evidence:
`scratch/matrix-v3/reports/DescriptorIndexing-Streaming-r2/validation.json`
and the matching `r3` directory, each with `summary.json`/`raw/`. The readback
source remains unresolved, so one accepted attempt is insufficient for a
repeatable streaming comparison.

Indexing material edits also reproduced an intermittent invalid frame: attempts
`r1` and `r3` each report one rejected/failed frame and two output identities.
Native submission rejection remains zero, illustrating why that counter alone
is insufficient. Their metadata and failure evidence are retained in
`scratch/matrix-v3/reports/DescriptorIndexing-MaterialEdits-r1/` and `r3/`.
This comparison is deferred pending a correctness investigation; successful
attempts do not erase either failure.

Boundary inspection confirms these are not shutdown samples. Material-edit `r3`
rejected frame 1920 at 23:03:05.998 UTC, about 22 seconds into its capture.
Indexing cold-start `r2` rejected frame 650 at 23:07:35.914 UTC, about 2.7 seconds
into capture, and is excluded too. Streaming `r2`'s startup readback coincided
with a `Failed` outcome at frame 47, before its timed window. The compact review
is `scratch/matrix-v3/reports/invalid-boundary-review.json`; full samples remain
in each attempt's `raw/` directory. The fixed 25-second warmup does not establish
that every cold-start process has reached a valid steady state.

### Reproduction and artifacts

The final measurement matrix writes beneath
`Build/_AgentValidation/20260909-132157-vulkan14-c/scratch/matrix-v3/`.
Its `reports/retained-evidence.json` identifies twelve static, material-edit,
resize and cold-start captures retained from `matrix-v2`; those workload paths
are unchanged by the camera/UI/streaming fixture corrections. Each original
invocation keeps its actual executable and assembly hashes. This is baseline
collection across documented fixture revisions, not an optimization comparison
between different binaries. The invalid indexing material-edit attempt remains
invalid. All moving, streaming and UI rows from v2 are excluded. V3 collects the
corrected rows and missing repetitions with one frozen Release build.
Earlier `pilot*` directories and the first matrix at the task root are runner
diagnostics, not production baselines. The first matrix was cancelled after a
resource-quiet gate rejected recurring descriptor-pool allocation. Its final
incomplete run must not be used.

From the repository root, use a Release editor built by the named-session manager,
stop that named session, then pass its resolved executable to the runner:

```powershell
pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun
$RunRoot = Join-Path (Get-Location) "Build/_AgentValidation/$(Get-Date -Format yyyyMMdd-HHmmss)-vulkan14-c"
New-Item -ItemType Directory -Path "$RunRoot/scratch" -Force | Out-Null
$Settings = (Resolve-Path 'XREngine.Benchmarks/VulkanPerformance/Cohorts/vulkan14-phase-c.jsonc').Path
@{
    XRE_UNIT_TEST_WORLD_SETTINGS_PATH = $Settings
    XRE_UNIT_TEST_RENDER_API = 'Vulkan'
    XRE_VK_DESCRIPTOR_BACKEND = 'DescriptorHeap'
    XRE_FORCE_MESH_SUBMISSION_STRATEGY = 'GpuIndirectZeroReadback'
    XRE_ZERO_READBACK_MATERIAL_DRAW_PATH = 'BindlessMaterialTable'
    XRE_VULKAN_VALIDATION = '1'
    XRE_VULKAN_SYNC_VALIDATION = '1'
} | ConvertTo-Json | Set-Content "$RunRoot/scratch/build-session-env.json"
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name vulkan14-c-repro `
    -Configuration Release -SessionEnvironmentFile "$RunRoot/scratch/build-session-env.json"
$Session = pwsh Tools/Manage-McpEditorSession.ps1 Status -Name vulkan14-c-repro -AsJson | ConvertFrom-Json
$Editor = $Session.Editor
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name vulkan14-c-repro
$Runner = '.\Tools\Benchmarks\Measure-Vulkan14Baseline.ps1'
$Common = @{
    EditorExecutablePath = $Editor
    RunRoot = "$RunRoot/scratch/matrix"
    ContinueOnInvalidCohort = $true
}
try {
    & $Runner @Common -Workloads Static,Moving,MaterialEdits,VolatileUi,Resize,ColdStart
} catch { Write-Warning $_.Exception.Message }
try {
    & $Runner @Common -Bindings DescriptorIndexing -Workloads Streaming -SkipCacheSeed
} catch { Write-Warning $_.Exception.Message }
python Tools/Benchmarks/Summarize-Vulkan14Baseline.py "$RunRoot/scratch/matrix"
```

The catches preserve failed-attempt reporting while allowing the independent
workload group and final summarizer to finish. Inspect all exclusions; catching
a runner failure does not authorize accepting its measurements. Sustained heap
streaming is a separate correctness reproduction until its capacity failure is
resolved, so it is intentionally absent from this comparison matrix.

Keep the stopped session's binaries until the matrix finishes; do not run session
retention cleanup while they are in use. The 2026-09-09 matrix used the Release
editor from logical session `vulkan14-phase-c-0909`, physical directory
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260909-133543-vulkan14-phase-c-0909/`,
at Git HEAD `08283cda19ad48bc57fd75c71db3fbcc036bd162` with the documented A/B/C
working changes. Exact executable and assembly hashes are in every invocation.

`-ContinueOnInvalidCohort` retains failed attempts and continues the matrix, then
returns failure if any attempt was invalid; it does not mark them valid.
`-FirstRepetition` selects the starting repetition when completing a partial
matrix without overwriting earlier attempts. Each cohort contains `invocation.json` (exact arguments,
environment, settings/asset/executable/assembly hashes), `validation.json`, the
standard harness summary, GPU clock/power observations, and a copied `raw/` log
directory. The runner refuses to overwrite an existing cohort directory.

The Python report is authoritative for the precise capture window and uses
linearly interpolated percentiles, including zero CPU costs/allocations/counters.
It excludes zero/unready GPU timestamps. The standard PowerShell summary uses
its existing percentile convention. Its timestamp conversion now preserves
fractional seconds on PowerShell versions that deserialize JSON dates directly;
early pilot summaries could select a different set of boundary samples.

With dense per-command timestamps disabled, `gpu_pipeline_frame_ms` is unavailable
on Vulkan. Report `vulkan_frame_gpu_command_buffer_ms` as the completed GPU
command-buffer duration, not a per-pass timing or a same-frame CPU/GPU difference.
Raw counters are sampled every tenth completed render frame: summed sampled
counts are not process-wide totals. Keep process startup/compilation counters and
first-completed-frame latency separate from warmed capture distributions.

The startup number is **first observed completed frame latency**, bounded by the
ten-frame sampling interval, not an exact first-present timestamp. The Python
report selects the first `Completed` Vulkan frame and summarizes pre-capture
startup/warmup samples separately. The isolated root contains native pipeline
cache, pipeline prewarm, and shader-artifact stores; OS and driver caches remain
uncontrolled. Detailed `vulkan_cpu_*_allocated_bytes` probes are disabled and
their exported zeros are unavailable measurements. The always-on GPU submission
and command-buffer-recording allocation counters remain part of the baseline.

Frame readiness rejection can precede native submission. Both the harness and
retained-sample report reject `Rejected`/`Failed` frame outcomes even when the
native submission-rejection counter is zero. Resize can legitimately produce
`Deferred` frames; its outcome histogram is retained with the transient-window
distribution. Mutation logs must show repeated changes and no setup/upload
failure. The report also records the actual completed-frame presentation policy
and rejects mismatches in renderer, profile mode, instrumentation, binding rung,
submission strategy, dynamic rendering or uncapped presentation. A passing
telemetry check still requires viewed output.

Submission-strategy checks apply to frames with reported scene commands or mesh
draws. Heap resize `r2` had one completed, zero-draw frame at 23:06:56.745 UTC
whose reset strategy marker was `CpuDirect`; CPU direct draws, GPU indirect
draws and scene commands were all zero. It is not evidence of CPU fallback.
The report preserves that raw marker and counts empty completed frames, while
validating actual scene submissions against the requested GPU strategy. The
resize attempt remains valid after this classification correction.

`ProfileMutationWorkloadComponent` selects `MaterialEdits`, `Streaming`,
`VolatileUi`, or `Resize` through `XRE_PROFILE_MUTATION_WORKLOAD`.
`XRE_PROFILE_STREAMING_ASSET` names the real image for streaming. Moving uses the
existing deterministic `XRE_PROFILE_CAMERA=Moving` path. Resize alternates
1600x900 and 1920x1080 every ten seconds; only that cohort explicitly permits
multiple output identities via `-AllowWorkloadIdentityChanges`. All other
strategy, backend, rejection and output-policy checks remain active.

## Final validation and comparison decisions

The matrix completed at 2026-09-09 23:16:42 UTC. It retains 39 workload attempts
plus two cache seeds: 34 workload captures passed the retained-sample checks,
with 12,445 selected samples; five attempts are excluded below. Sustained heap
streaming was reproduced separately and is not an accepted timing series.
No phase D–I optimization was evaluated or promoted.

Completed production frames used Vulkan, dynamic rendering, uncapped Immediate
presentation, two frame slots and three swapchain images, with the presentation
limiter and Vulkan validation disabled. Present ID and present wait were enabled.
GPU scene submissions used `GpuIndirectZeroReadback` and `BindlessMaterialTable`.
Every invocation records the actual build hashes and isolated cache paths.

The final source build passed with zero warnings and zero errors
(`logs/final-integration-build.log`). Final synchronization-validation sessions
visually verified material changes and both resize extents on heap and indexing.
Each retained `final-material-*-stats.json` and `final-resize-*-stats.json`
reported zero Vulkan validation messages/errors. Viewed image pairs under
`mcp-captures/` are:

| Case | First image | Second image |
| --- | --- | --- |
| Heap material edits | `Screenshot_20260909_162016_944_1e4e3eb2c5c8477ab983336839773123.png` | `Screenshot_20260909_162017_879_22faaf7ad2bc4dde8a6beffb6a3c0d33.png` |
| Indexing material edits | `Screenshot_20260909_162132_311_3738f10a1c3a47a4baeea43dadda91fb.png` | `Screenshot_20260909_162133_285_59715357d87048b6a17d8c158c35aab6.png` |
| Indexing resize, 1920×1080 then 1600×900 | `Screenshot_20260909_162246_015_68377606e43c42dc97f3ebe6cc5a6cb6.png` | `Screenshot_20260909_162256_593_73b172e7a23a47538a7e963f2ddec78e.png` |
| Heap resize, 1600×900 then 1920×1080 | `Screenshot_20260909_162354_212_c7bf6ae193c9434987bdee0bb2ce31c1.png` | `Screenshot_20260909_162404_772_782b29aedcef4f3ba257457f8399b20c.png` |

The final source's moving UI captures (`Screenshot_20260909_161804_427_18b0f7d2ba474472a45c79cf2a93ff4d.png`
and `Screenshot_20260909_161849_231_8323097f938f41149dfbb619bd7288d6.png`) show
different camera positions with the overlay retained. Root-hook completion
reported 4432 updates. A separate `CreateUnitBox=false` run reported 7775 updates
on `Player1_Pawn`, with distinct queried poses and zero Vulkan validation messages;
see `reports/final-no-boxes-render-state1.json`, `final-no-boxes-render-state2.json`
and `final-no-boxes-camera-motion.log`. The owned session is stopped. A review of
21 retained final-session logs found no `VUID`, `Validation Error` or
`ErrorDeviceLost` matches (`reports/final-source-log-review.json`). These short
visual checks do not erase failures found in the longer performance captures.

Apply the declared criteria as follows:

- **Defer** heap moving-camera CPU comparisons: the three run medians span
  13.758–22.538 ms, a 50.22% spread. Do not attribute the cause without further
  controlled measurement.
- **Defer** GPU improvement claims for groups whose median spread exceeds 7.5%,
  including static and moving scenes on both backends and heap material edits
  and resize. Retain these measurements as a noise baseline.
- **Defer** indexing material-edit and streaming comparisons: each has only one
  accepted run and repeated invalid attempts. Indexing cold-start has two
  accepted runs and one invalid attempt, so it also lacks the required three.
- **Defer** heap streaming until its descriptor-capacity failure is corrected.
- Other complete, stable groups are baseline evidence for matched future
  experiments. They do not establish that one backend is universally faster or
  that an optimization already passed the retain criteria.

Cold-process first-observed completion is approximately 9.5–9.7 seconds for heap
and 9.9–10.2 seconds for indexing. The sampled startup compile counters are zero;
that does **not** establish zero compilation cost. Compilation can precede the
first serialized frame or fall between samples. Isolated compiler duration is
unavailable in this profile and needs separate event-level evidence before a
shader/compiler experiment can claim a compilation improvement. OpenGL text UI
rendering remains unvalidated as described above; the Vulkan results do not
establish that separate renderer's UI parity.

## Recorded baseline distributions

Times are milliseconds. H = DescriptorHeap; I = DescriptorIndexing. Each triple
is p50 / p95 / p99 within one capture. Failed captures are excluded from timing
comparisons and retained below. The GPU column is completed command-buffer time.

| Attempt | Samples | Render CPU | GPU command buffer |
| --- | ---: | ---: | ---: |
| H-ColdStart-r1 | 391 | 14.552 / 17.765 / 30.035 | 3.027 / 4.231 / 4.731 |
| H-ColdStart-r2 | 397 | 14.380 / 18.614 / 29.815 | 3.193 / 4.508 / 5.614 |
| H-ColdStart-r3 | 402 | 14.248 / 17.196 / 28.678 | 3.223 / 4.458 / 4.697 |
| H-MaterialEdits-r1 | 407 | 13.917 / 18.421 / 28.787 | 2.890 / 4.147 / 4.214 |
| H-MaterialEdits-r2 | 404 | 14.042 / 18.412 / 29.132 | 2.889 / 4.170 / 4.453 |
| H-MaterialEdits-r3 | 405 | 14.059 / 15.791 / 29.059 | 3.220 / 4.536 / 4.803 |
| H-Moving-r1 | 339 | 17.481 / 25.087 / 35.122 | 3.338 / 4.599 / 5.275 |
| H-Moving-r2 | 356 | 13.758 / 17.638 / 28.698 | 2.974 / 4.202 / 4.583 |
| H-Moving-r3 | 306 | 22.538 / 28.646 / 39.224 | 3.203 / 3.660 / 5.185 |
| H-Resize-r1 | 485 | 14.146 / 16.292 / 28.809 | 2.806 / 4.094 / 4.377 |
| H-Resize-r2 | 479 | 13.856 / 16.977 / 28.633 | 3.049 / 4.436 / 4.712 |
| H-Resize-r3 | 484 | 13.701 / 15.749 / 28.341 | 3.060 / 4.372 / 4.831 |
| H-Static-r1 | 407 | 14.035 / 17.843 / 28.541 | 2.912 / 4.208 / 4.507 |
| H-Static-r2 | 404 | 14.070 / 17.274 / 28.278 | 3.231 / 4.471 / 5.119 |
| H-Static-r3 | 411 | 13.796 / 15.588 / 28.392 | 3.260 / 4.487 / 5.034 |
| H-VolatileUi-r1 | 318 | 17.837 / 37.251 / 39.526 | 2.934 / 4.226 / 4.445 |
| H-VolatileUi-r2 | 320 | 17.541 / 19.242 / 25.846 | 2.956 / 3.516 / 5.497 |
| H-VolatileUi-r3 | 326 | 17.764 / 36.367 / 37.733 | 3.036 / 3.635 / 4.770 |
| I-ColdStart-r1 | 325 | 17.002 / 26.115 / 27.025 | 2.821 / 4.081 / 4.466 |
| I-ColdStart-r2 | 347 | excluded | excluded |
| I-ColdStart-r3 | 347 | 16.577 / 20.771 / 22.074 | 2.843 / 4.174 / 4.473 |
| I-MaterialEdits-r1 | 331 | excluded | excluded |
| I-MaterialEdits-r2 | 338 | 17.002 / 21.136 / 22.429 | 2.684 / 3.901 / 4.057 |
| I-MaterialEdits-r3 | 331 | excluded | excluded |
| I-Moving-r1 | 292 | 18.432 / 26.070 / 30.578 | 2.885 / 4.104 / 4.615 |
| I-Moving-r2 | 278 | 18.318 / 25.832 / 30.669 | 3.031 / 4.258 / 4.592 |
| I-Moving-r3 | 261 | 18.426 / 26.636 / 31.895 | 2.771 / 3.384 / 3.688 |
| I-Resize-r1 | 498 | 16.530 / 20.544 / 26.082 | 2.540 / 3.765 / 3.933 |
| I-Resize-r2 | 511 | 16.397 / 20.564 / 21.809 | 2.701 / 4.040 / 4.423 |
| I-Resize-r3 | 501 | 16.433 / 20.679 / 22.591 | 2.734 / 4.107 / 4.435 |
| I-Static-r1 | 334 | 17.593 / 21.774 / 23.231 | 2.659 / 3.934 / 4.184 |
| I-Static-r2 | 315 | 18.362 / 24.476 / 28.734 | 2.959 / 4.187 / 4.538 |
| I-Static-r3 | 334 | 17.366 / 21.431 / 23.167 | 2.845 / 4.180 / 4.405 |
| I-Streaming-r1 | 247 | 21.769 / 38.454 / 42.166 | 2.858 / 3.391 / 4.702 |
| I-Streaming-r2 | 248 | excluded | excluded |
| I-Streaming-r3 | 239 | excluded | excluded |
| I-VolatileUi-r1 | 272 | 21.139 / 29.759 / 38.517 | 2.783 / 4.227 / 4.773 |
| I-VolatileUi-r2 | 274 | 21.112 / 28.376 / 39.245 | 2.783 / 3.969 / 4.511 |
| I-VolatileUi-r3 | 277 | 20.603 / 27.152 / 39.332 | 2.670 / 3.203 / 3.649 |

### Full output-frame cost and variation

Full output-frame triples are medians of the valid runs' individual p50/p95/p99
values, not pooled frame samples. Spread is (largest run median − smallest run
median) / median of run medians. It describes these runs, not a confidence interval.

| Binding/workload | Valid/attempted | Whole output frame | Render median range | Render spread | GPU median range | GPU spread |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| H/ColdStart | 3/3 | 14.380 / 17.764 / 29.814 | 14.248–14.552 | 2.11% | 3.027–3.223 | 6.12% |
| H/MaterialEdits | 3/3 | 14.042 / 18.410 / 29.058 | 13.917–14.059 | 1.01% | 2.889–3.220 | 11.47% |
| H/Moving | 3/3 | 17.480 / 25.087 / 35.121 | 13.758–22.538 | 50.22% | 2.974–3.338 | 11.35% |
| H/Resize | 3/3 | 13.856 / 16.291 / 28.633 | 13.701–14.146 | 3.21% | 2.806–3.060 | 8.33% |
| H/Static | 3/3 | 14.035 / 17.274 / 28.392 | 13.796–14.070 | 1.96% | 2.912–3.260 | 10.77% |
| H/VolatileUi | 3/3 | 17.764 / 36.366 / 37.732 | 17.541–17.837 | 1.67% | 2.934–3.036 | 3.45% |
| I/ColdStart | 2/3 | 16.789 / 23.442 / 24.549 | 16.577–17.002 | 2.53% | 2.821–2.843 | 0.79% |
| I/MaterialEdits | 1/3 | 17.001 / 21.136 / 22.429 | 17.002–17.002 | insufficient repeats | 2.684–2.684 | insufficient repeats |
| I/Moving | 3/3 | 18.426 / 26.069 / 30.669 | 18.318–18.432 | 0.62% | 2.771–3.031 | 9.03% |
| I/Resize | 3/3 | 16.432 / 20.564 / 22.590 | 16.397–16.530 | 0.81% | 2.540–2.734 | 7.18% |
| I/Static | 3/3 | 17.593 / 21.774 / 23.231 | 17.366–18.362 | 5.66% | 2.659–2.959 | 10.56% |
| I/Streaming | 1/3 | 21.769 / 38.454 / 42.165 | 21.769–21.769 | insufficient repeats | 2.858–2.858 | insufficient repeats |
| I/VolatileUi | 3/3 | 21.111 / 28.376 / 39.245 | 20.603–21.139 | 2.54% | 2.670–2.783 | 4.06% |

### Managed allocations and CPU work

Allocation triples are medians of valid-run p50/p95/p99, in bytes per sampled
operation. GPU submission and command-buffer recording scopes may overlap and
must not be added into a supposed whole-frame allocation total. Detailed Vulkan
stage allocation probes were disabled; their zero exports are unavailable.

| Binding/workload | GPU submission bytes | Command recording bytes | Record CPU p50 | Update CPU p50 | Collect CPU p50 |
| --- | ---: | ---: | ---: | ---: | ---: |
| H/ColdStart | 141984 / 142664 / 145448 | 370288 / 376826 / 439861 | 7.332 | 0.043 | 0.618 |
| H/MaterialEdits | 141984 / 145176 / 145448 | 370288 / 377338 / 519245 | 7.242 | 0.043 | 0.640 |
| H/Moving | 142696 / 219408 / 222872 | 464352 / 576198 / 641380 | 8.934 | 0.053 | 0.717 |
| H/Resize | 141984 / 142656 / 145448 | 370296 / 388728 / 541759 | 7.143 | 0.043 | 0.616 |
| H/Static | 141984 / 142656 / 145448 | 370296 / 374288 / 377392 | 7.209 | 0.044 | 0.626 |
| H/VolatileUi | 139232 / 142424 / 143002 | 593948 / 671880 / 1356456 | 10.851 | 0.042 | 0.650 |
| I/ColdStart | 127384 / 157628 / 158890 | 503788 / 705548 / 722603 | 8.353 | 0.043 | 0.612 |
| I/MaterialEdits | 127384 / 130168 / 130576 | 503792 / 510888 / 697523 | 8.421 | 0.038 | 0.628 |
| I/Moving | 127384 / 185088 / 187872 | 676088 / 900216 / 903054 | 9.611 | 0.045 | 0.852 |
| I/Resize | 127384 / 129474 / 130576 | 503792 / 511528 / 582344 | 8.114 | 0.042 | 0.614 |
| I/Static | 127384 / 130168 / 130576 | 503792 / 510888 / 731762 | 8.866 | 0.041 | 0.636 |
| I/Streaming | 127880 / 130664 / 131072 | 665312 / 764392 / 846866 | 12.343 | 0.043 | 0.813 |
| I/VolatileUi | 128024 / 130808 / 130906 | 677960 / 764304 / 1744002 | 12.018 | 0.039 | 0.661 |

### Wait and submission costs

Each entry is the median of valid-run p50 values in milliseconds. Collect and
render waits occur on different threads; these are not additive frame costs.

| Binding/workload | Collect→render wait | Render→collect wait | Slot fence | Swapchain image wait | Tree wait | Submit | Present |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| H/ColdStart | 13.699 | 0.286 | 0.013 | 0.018 | 0.032 | 0.108 | 0.048 |
| H/MaterialEdits | 13.332 | 0.290 | 0.012 | 0.018 | 0.030 | 0.105 | 0.048 |
| H/Moving | 16.669 | 1.110 | 0.014 | 0.019 | 0.034 | 0.114 | 0.051 |
| H/Resize | 13.081 | 0.282 | 0.019 | 0.025 | 0.045 | 0.103 | 0.049 |
| H/Static | 13.352 | 0.291 | 0.013 | 0.018 | 0.031 | 0.105 | 0.048 |
| H/VolatileUi | 17.055 | 0.202 | 0.014 | 0.019 | 0.034 | 0.173 | 0.051 |
| I/ColdStart | 16.098 | 0.302 | 0.013 | 0.019 | 0.034 | 0.141 | 0.051 |
| I/MaterialEdits | 16.293 | 0.282 | 0.013 | 0.019 | 0.032 | 0.136 | 0.050 |
| I/Moving | 17.518 | 1.169 | 0.014 | 0.020 | 0.034 | 0.143 | 0.051 |
| I/Resize | 15.750 | 0.290 | 0.018 | 0.025 | 0.045 | 0.129 | 0.050 |
| I/Static | 16.927 | 0.289 | 0.013 | 0.019 | 0.034 | 0.147 | 0.050 |
| I/Streaming | 20.586 | 0.000 | 0.031 | 0.038 | 0.070 | 0.137 | 0.052 |
| I/VolatileUi | 20.401 | 0.216 | 0.014 | 0.019 | 0.035 | 0.359 | 0.051 |

### Warm compilation, cache and reuse observations

Counts are per sampled snapshot, not whole-process totals. The first four columns
are medians of valid-run p50 counts. Compile time is the largest observed per-sample
value across valid captures. A high reuse count does not imply low total cost.

| Binding/workload | Pipeline hits | Pipeline misses | Reused chains | Recorded chains | Max compile ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| H/ColdStart | 987 | 0 | 638 | 0 | 0.000 |
| H/MaterialEdits | 987 | 0 | 638 | 0 | 0.000 |
| H/Moving | 987 | 0 | 0 | 0 | 0.000 |
| H/Resize | 987 | 0 | 638 | 0 | 0.183 |
| H/Static | 987 | 0 | 638 | 0 | 0.000 |
| H/VolatileUi | 989 | 0 | 0 | 0 | 0.000 |
| I/ColdStart | 988 | 0 | 640 | 0 | 0.000 |
| I/MaterialEdits | 988 | 0 | 640 | 0 | 0.000 |
| I/Moving | 988 | 0 | 0 | 0 | 0.000 |
| I/Resize | 988 | 0 | 640 | 0 | 0.042 |
| I/Static | 988 | 0 | 640 | 0 | 0.000 |
| I/Streaming | 988 | 0 | 0 | 0 | 0.000 |
| I/VolatileUi | 990 | 0 | 0 | 0 | 0.000 |

### Cold-process observations

Each cold attempt starts with an empty isolated engine cache. OS and driver caches
are not flushed. The first-frame number is first observed completion from launch;
compile maxima below come from sampled startup/warmup, not full-process totals.

| Attempt | Valid | First observed completed frame ms | Pre-capture samples | Max sampled compile ms | Max sampled pipeline misses |
| --- | --- | ---: | ---: | ---: | ---: |
| H-ColdStart-r1 | True | 9670.728 | 64 | 0.000 | 0 |
| H-ColdStart-r2 | True | 9612.792 | 64 | 0.000 | 0 |
| H-ColdStart-r3 | True | 9529.318 | 62 | 0.000 | 0 |
| I-ColdStart-r1 | True | 10180.224 | 56 | 0.000 | 0 |
| I-ColdStart-r2 | False | 10100.735 | 51 | 0.000 | 0 |
| I-ColdStart-r3 | True | 9927.085 | 52 | 0.000 | 0 |

### Streaming workload progress

These counters cover each process's active workload, including warmup. The
asset set grows throughout the run; this is not a stationary memory workload.
Shutdown cancellation callbacks are retained separately from upload failures.

| Attempt | Update checks | Scheduled | Completed | Bound for display | Failed | Canceled |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| I-Streaming-r1 | 324 | 310 | 306 | 95 | 0 | 4 |
| I-Streaming-r2 | 324 | 310 | 306 | 91 | 0 | 4 |
| I-Streaming-r3 | 324 | 309 | 305 | 88 | 0 | 4 |

### Retained exclusions

- `DescriptorIndexing-ColdStart-r2`: Invalid render-pipeline performance capture: GpuIndirectZeroReadback r1: stable=True identities=2 unapprovedPolicy=0 rejectedSubmissions=0 failedFrames=1 reason=disabled by NoStabilityGate; 1 sampled Vulkan frames were rejected or failed.
- `DescriptorIndexing-MaterialEdits-r1`: Invalid render-pipeline performance capture: GpuIndirectZeroReadback r1: stable=True identities=2 unapprovedPolicy=0 rejectedSubmissions=0 reason=disabled by NoStabilityGate; 1 sampled Vulkan frames were rejected or failed.
- `DescriptorIndexing-MaterialEdits-r3`: Invalid render-pipeline performance capture: GpuIndirectZeroReadback r1: stable=True identities=2 unapprovedPolicy=0 rejectedSubmissions=0 failedFrames=1 reason=disabled by NoStabilityGate; 1 sampled Vulkan frames were rejected or failed.
- `DescriptorIndexing-Streaming-r2`: Incomplete or invalid capture: samples=248, stable=True, note=MCP CPU/GPU diagnostics unavailable: Disabled for clean performance capture.; zero-readback violation capture(readbackBytes=0 mappedBuffers=0) all(readbackBytes=8192 mappedBuffers=0)
- `DescriptorIndexing-Streaming-r3`: Incomplete or invalid capture: samples=239, stable=True, note=MCP CPU/GPU diagnostics unavailable: Disabled for clean performance capture.; zero-readback violation capture(readbackBytes=0 mappedBuffers=0) all(readbackBytes=8192 mappedBuffers=0)
- DescriptorHeap/Streaming: terminal descriptor-heap capacity failure in the separate validation run; no accepted sustained timing comparison.

