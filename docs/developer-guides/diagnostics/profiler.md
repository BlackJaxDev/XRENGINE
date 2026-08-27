# XREngine Remote Profiler

The XREngine Remote Profiler is an **out-of-process** ImGui application that
displays real-time engine telemetry streamed over UDP. Moving the profiler UI
out of the editor eliminates render-thread overhead and prevents the profiler
itself from perturbing the measurements it displays.

---

## Architecture Overview

```
┌──────────────────────────┐        UDP (localhost:9142)        ┌────────────────────────┐
│   XREngine.Editor /      │  ─────────────────────────────►    │   XREngine.Profiler    │
│   Engine Process         │   fire-and-forget, MemoryPack      │   (standalone app)     │
│                          │                                    │                        │
│  ┌────────────────────┐  │   6 packet types @ ~30 Hz          │  UdpProfilerReceiver   │
│  │ CodeProfiler       │  │   1 heartbeat    @ ~1 Hz           │  ProfilerPanelRenderer │
│  │ (stats thread)     │──┤                                    │  ImGui + OpenGL 3.3    │
│  └────────────────────┘  │                                    └────────────────────────┘
│  ┌────────────────────┐  │
│  │ UdpProfilerSender  │  │  ◄── BelowNormal background thread
│  │ (sender thread)    │  │      MemoryPack serialize → UDP send
│  └────────────────────┘  │
└──────────────────────────┘
```

The engine process has **two background threads** when profiling is active:

| Thread | Priority | Role |
|--------|----------|------|
| `XREngine.ProfilerStats` | BelowNormal | Drains the `ConcurrentQueue<ProfilerEvent>`, builds per-thread call-tree snapshots at ~30 Hz, publishes via volatile reference swap (lock-free). |
| `XREngine.ProfilerSender` | BelowNormal | Every ~33 ms: reads engine snapshots via 6 delegate collectors, serializes with MemoryPack, sends as UDP datagrams to `127.0.0.1:9142`. |

## Implementation Status

The remote-profiler design is now implemented in shipping repo code:

- `XREngine.Profiler` provides the standalone Silk.NET + ImGui application.
- `XREngine.Profiler.UI` provides the shared panel renderer used by both the standalone app and the editor.
- `Engine.ProfilerSender.cs` and `UdpProfilerSender` bridge engine snapshots into the UDP protocol.
- Editor preferences can enable UDP sending at runtime and optionally launch the standalone profiler on startup.

Published launcher builds compile out the runtime profiler sender and related live profiling hooks behind `XRE_PUBLISHED`, so shipped builds do not keep the editor/developer profiling surface active.

## Engine-Side Overhead

### When profiling is **disabled** (default)

| Component | Cost |
|-----------|------|
| `Profiler.Start()` / `Dispose()` | Returns `default` immediately — **zero work** |
| Stats thread | Not running |
| Sender thread | Not running, no socket bound |
| **Total** | **Effectively zero** |

### When profiling is **enabled**

| Component | Thread | Cost |
|-----------|--------|------|
| `Profiler.Start()` + scope `Dispose()` | Caller (main, render, update, etc.) | ~100 ns per scope: one `Stopwatch` timestamp read + one lock-free `ConcurrentQueue.Enqueue` |
| Stats thread (snapshot building) | Background (BelowNormal) | Processes event queue at ~250 Hz, publishes immutable snapshots at ~30 Hz |
| Sender thread (collection + serialization + send) | Background (BelowNormal) | ~20–40 small managed allocations per 33 ms cycle; 6 MemoryPack serializations; 6–7 UDP `Send()` calls to loopback |
| **Main-thread total** | | **~100 ns × number of profiler scopes per frame** |

All collection, serialization, and network I/O runs on the sender background
thread. The six collector delegates read engine state through lock-free /
volatile patterns — **no collector takes a lock on any engine subsystem**.

### Profiler scope kinds

`Engine.Profiler.Start(...)` accepts an optional `ProfilerScopeKind` so call
sites can describe the expected cadence of the work being measured:

| Kind | Use for | Logging policy |
|------|---------|----------------|
| `AlwaysOnHotPathLoop` | Work expected every frame or every render/update loop pass | Aggregate in frame history and FPS-drop/render-stall diagnostics; do not emit per-scope spike logs. |
| `ConditionalLoop` | Work checked from a loop but only active sometimes, such as queue drains or async polling | Aggregate normally and write rate-limited slow-scope entries to `profiler-conditional-loop-spikes.log`. |
| `OneOffInvoke` | Startup, linking, compilation, cache load, or other discrete invokes | Aggregate normally and write rate-limited slow individual invokes to `profiler-one-off-invokes.log`. |
| `Unspecified` | Legacy or not-yet-classified scopes | Preserve existing aggregate behavior. |

Scope kind is carried through the MemoryPack profiler frame packet, in-process
editor profiler data source, UDP sender, and shared profiler UI. The profiler
tree labels non-default scopes with their kind and keeps otherwise-identical
method names separate if their cadence differs.

### Allocation budget (sender thread, per ~33 ms cycle)

| Source | Allocation |
|--------|-----------|
| `CollectProfilerFrame` | `ProfilerThreadData[]` + recursive `ProfilerNodeData[]` per tree level |
| `CollectRenderStats` | `RenderMatrixListenerEntry[]` (small) |
| `CollectJobSystemStats` | `JobPriorityStatsEntry[5]` + 5 enum `.ToString()` strings |
| `CollectMainThreadInvokes` | `MainThreadInvokeEntryData[]` (size = recent count, often 0) |
| `MemoryPackSerializer.Serialize()` | One `byte[]` per packet (6 per cycle) |

These are all short-lived Gen-0 allocations on a background thread and do not
cause GC pauses visible on the main thread.

## Wire Protocol

| Property | Value |
|----------|-------|
| Transport | UDP, unicast, `127.0.0.1` |
| Default port | `9142` (override via `XRE_PROFILER_PORT` env var or CLI arg) |
| Serialization | [MemoryPack](https://github.com/Cysharp/MemoryPack) — zero-copy, high perf |
| Framing | 5-byte header: 1 byte message type + 4 bytes LE payload length |
| Max datagram | 65,000 bytes (profiler frames are auto-pruned if they exceed this) |
| Reliability | None — fire-and-forget; dropped packets are silently lost |
| Direction | One-way: engine → profiler |

### Message Types

| Code | Type | Frequency | Content |
|------|------|-----------|---------|
| `0x01` | `ProfilerFrame` | ~30 Hz | Per-thread call trees, timing history |
| `0x02` | `RenderStats` | ~30 Hz | Draw calls, VRAM, FBO, render matrix, CPU spatial tree |
| `0x03` | `ThreadAllocations` | ~30 Hz | Per-phase GC allocation ring buffers |
| `0x04` | `BvhMetrics` | ~30 Hz | Build/refit/cull/raycast counts and timings |
| `0x05` | `JobSystemStats` | ~30 Hz | Worker count, queue state, per-priority stats |
| `0x06` | `MainThreadInvokes` | ~30 Hz | Recent cross-thread invoke log |
| `0x07` | `Heartbeat` | ~1 Hz | Process name, PID, uptime |

Shared protocol types live in `XREngine.Data/Profiling/` and are referenced by
both the engine and the profiler app — the profiler has **no dependency on the
engine assembly**.

## Profiler App (XREngine.Profiler)

A standalone Silk.NET window (GLFW + OpenGL 3.3 + ImGui with docking) that:

1. Listens on the configured UDP port via `UdpProfilerReceiver` (background thread).
2. Deserializes incoming packets and stores the latest snapshot per type (volatile refs).
3. Renders 9 dockable panels via `ProfilerPanelRenderer`:
   - **Profiler Tree** — call-tree hierarchy with root method graphs (`PlotLines`), total/self/average/peak timing columns, and worst-frame tracking
   - **FPS Drop Spikes** — sortable table of frame-time anomalies with hottest call path
   - **Render Stats** — draw calls, VRAM, FBO bandwidth, render resource churn, render matrix, CPU spatial tree
  - **GPU Pipeline** — generic render-pipeline command GPU timings, root history plots, and hierarchical pass breakdowns
   - **Thread Allocations** — per-phase GC allocation stats (last / avg / max KB)
   - **BVH Metrics** — build, refit, cull, raycast counts and timings
   - **Job System** — worker count, queue depth, per-priority wait times
   - **Main Thread Invokes** — cross-thread dispatch log
   - **Connection Info** — link status, packet counters, multi-instance source table

### GPU Pipeline Timing

The Render Stats panel includes a `Frame Outputs` table that attributes the
shared render-thread frame to desktop scene, desktop mirror, XR submit, overlay,
and present rows. It shows mirror mode, visibility policy, budget band,
configured and achieved output rates, skip counts, command counts, and per-phase
CPU timing.

The profiler can collect GPU timestamp timings around generic `ViewportRenderCommand`
execution, which makes it possible to see where render-pipeline time is being spent
without instrumenting each pass manually.

- Enable it from **Profiler Settings** with **Enable GPU Pipeline Profiling**.
- Results appear in the new **GPU Pipeline** panel in both the in-process and remote profilers.
- The panel shows backend/status text, a resolved whole-frame GPU total, root-series history plots, and a hierarchical per-command timing tree.
- In the in-editor profiler, each render-pipeline root history graph has a **Dump** button that writes a unique `profiler-gpu-pipeline-*.log` file under the active `Build/Logs/.../<session>/` folder. The dump includes retained frame samples, warmup-excluded summaries, worst frames, render-thread CPU/present deltas, named XRWindow CPU phase aggregates, slow command/scope rankings, shader/material hint rankings, and full aggregate tables for LLM analysis.
- To avoid OpenGL driver stalls, timestamp sampling is capped per frame and temporarily throttled after slow query calls. Shadow-map passes keep high-level pass timings but skip per-mesh shadow draw scopes.
- Current backend support is **OpenGL**. Unsupported renderers report status text rather than falling back to CPU timings.

### CPU Hierarchy And Render Resource Churn

The profiler tree separates inclusive command time from untracked self time:
`Total` is the inclusive aggregate, `Self` is the portion not attributed to
visible children, `Avg` is total divided by calls, and `Peak` is the largest
single sample retained by the display window. Use `Self` to find genuinely
opaque work and `Peak` to distinguish one-off stalls from many small calls.

Render Stats also reports per-frame render resource churn. The summary counters
and table group FBO, texture, renderbuffer, and buffer create/recreate/resize/
destroy events by resource name and reason. Steady-state frames should generally
show no churn; repeated `Recreated` or `Resized` rows point to a resource
lifetime or size-policy bug rather than normal render cost.

### Connection States

| State | Menu Bar | Overlay |
|-------|----------|---------|
| Never received a heartbeat | `WAITING...` (yellow) | "Waiting for engine data…" with animated dots |
| Connected (heartbeat < 3 s ago) | `CONNECTED` (green) | None |
| Lost (heartbeat > 3 s ago) | `LOST (Xs)` (red) | "Reconnecting…" with elapsed time |

### Multi-Instance Detection

When multiple engine processes send heartbeats to the same port, the Connection
Info panel shows a **Known Sources** table listing each PID, process name,
uptime, and last-seen age. A warning recommends using separate ports to avoid
interleaved data. Sources are pruned after 10 seconds of inactivity.

## Enabling Profiling

### Option 1: Environment Variable (recommended)

```bash
# Before launching the engine
set XRE_PROFILER_ENABLED=1

# Optional: custom port
set XRE_PROFILER_PORT=9200
```

The VS Code task **Start-Editor-WithProfiler-NoDebug** does this automatically.

### Option 2: Runtime Toggle

In the editor's Settings panel, toggle **Enable Profiler UDP Sending** under
Debug Options. This starts/stops the sender thread at runtime with zero residual
overhead when off.

If you want the external profiler every time the editor boots, enable
**Start External Profiler On Startup** in either Global Editor Preferences or
Editor Preferences Overrides. Startup uses that setting to launch
`XREngine.Profiler` and force profiler UDP sending on for the session.

The in-editor **Profiler Settings** panel also exposes **Enable GPU Pipeline
Profiling**, which turns on command-level GPU timestamp collection for supported
renderers.

Use **Dump Speed Profile** in **Profiler Settings** after the editor has
settled into the workload you care about. It captures the same per-frame render
stats stream for the selected number of seconds and writes
`profiler-render-stats.ndjson`, `profiler-capture-manifest.json`, and
`profiler-capture-summary.json` under the current session's
`Build/Logs/.../<session>/speed-profiles/<timestamp>_profiler-panel/` folder.
Only the latest three in-session speed-profile captures are retained.

For repeatable run-to-run capture, launch with `XRE_PROFILE_CAPTURE=1`. This writes
`profiler-render-stats.ndjson` with one completed render-frame sample per line,
including game-loop CPU timings, render counters, GPU pipeline timing readiness,
fallback counters, and GPU readback/mapped-buffer totals. `XRE_PROFILE_AUTO_DUMP=1`
also dumps all GPU render-pipeline timing histories on graceful shutdown; it is
enabled automatically when `XRE_PROFILE_CAPTURE=1`.

The game-loop/default-pipeline harness reports both capture-window `Samples` and
process-lifetime `AllSamples`. If a GPU path freezes during warmup and never reaches
the timed capture window, the summary still records the final sample timestamp,
frame id, render/GPU time, readback bytes, and fallback counters. Strict
`GpuIndirectZeroReadback` runs do not queue async stats-buffer readbacks; draw and
triangle stats that require CPU readback are intentionally unavailable on that path.
The harness also has a `-NoSampleHangSec` watchdog, enabled by default, that
force-stops a variant when `profiler-render-stats.ndjson` stops advancing after
at least one sample has been written. Harness summaries are written under
`Build/Logs/speed-profiles/game-loop-render-pipeline/<timestamp>/` as
`summary.json`, `summary.txt`, and `run-logdirs.txt`; by default the harness keeps
only the latest three summary runs, configurable with `-RetainedRunCount`.

### Render Stats Capture Schema v8

`profiler-capture-manifest.json` now records
`xrengine.profile_capture.render_stats.v8` with `schema_version = 8`. The
manifest makes benchmark context explicit: build configuration, world mode,
forced and effective mesh submission strategy, zero-readback material draw path,
render backend, GPU/vendor, scene, camera, lights, viewport, render scale,
stereo mode, VR view render mode, VR mirror mode, desktop mirror target rates,
validation/debug state, shader and texture cache mode, GPU clock policy, target
refresh rate, frame budget, warmup/capture durations, and any invalid benchmark
environment overrides detected at launch. Schema v4 also records Vulkan render-
target mode, primary-command reuse and OBS-hook policies, ImGui skip state, the
actual assembly build configuration, scene/settings hashes, and a structured
output/view-family inventory with target extents and cadence/budget policy.
Schema v5 adds the named-profile suitability result, promotion eligibility,
command-label/P3 logging state, profiler/editor UI state, dynamic/debug overlay
state, active Vulkan diagnostic-trace flags, log verbosity and exact log-session
path, XR runtime, output anti-aliasing identity, and active AO, exposure, MSAA,
TSR, bloom, motion-blur, and motion-vector settings. Clean captures are
therefore self-describing and are rejected when an intrusive observer or cold
shader/texture cache remains.

Schema v8 adds the Phase 1 presentation profile, actual present cadence,
causal-wait, device/memory/submission breadcrumb, foreground arbitration, and
correlated Vulkan frame-tree fields. Tree rows retain stable engine, render,
output, and authority IDs and partition stage-exclusive work, waits, native
driver time, external runtime time, and diagnostic observer time. Worker
overlap is keyed to the immutable render frame rather than sampled from a
rolling global counter.

Each causal-wait row also carries the coarse frame stage in which the wait was
observed. The desktop aggregate path times current-slot and pre-collect next-slot
reuse independently, separates admission from native acquire/submit/present,
and instruments contended command-pool, descriptor, submission, queue-lease,
lifetime, upload, pipeline-compiler, and synchronization authorities. Lock
instrumentation takes the uncontended `TryEnter` fast path without reading the
clock; only contended intervals of at least 0.1 ms are retained in the bounded
per-frame payload.

The in-process and UDP profiler sources materialize the same diagnostic tree
only when a profiler packet is requested; the renderer's aggregate publication
remains allocation-free. The ImGui profiler presents a collapsible root with
category totals, critical path, stage children, causal waits, and explicit
attribution warnings. Frames at or above the slow-frame threshold also emit a
rate-limited `[Vulkan][FrameTree]` log record with the same identities and
totals. Observer overhead and the detailed 99% attribution target remain
empirical promotion gates rather than assumed properties of schema v8.

Each `profiler-render-stats.ndjson` sample includes the old frame timing fields
plus renderer-state churn counters: indirect-count and multi-draw calls, shader
program and pipeline switches, VAO/buffer/SSBO/UBO binds, texture binds and
redundant bind skips, active texture-binding rung, uniform calls, upload bytes,
barriers by kind, readback bytes, mapped-buffer reads, active stereo mode,
active backend, validation/debug flags, and timestamp query/readback counts.

Frame lifecycle fields identify fence pressure between update, collect/swap, and
render: `update_frame_id`, `collect_frame_id`, `swap_frame_id`,
`present_frame_id`, `collect_visible_late_policy`,
`collect_wait_for_render_ms`, `collect_wait_reason`,
`render_wait_for_collect_ms`, `render_wait_reason`,
`skipped_collect_frames`, and `stale_collect_reuse_frames`.
`XRE_COLLECT_VISIBLE_LATE_POLICY=ReusePreviousVisibility` records stale-snapshot
reuse when render intentionally skips a late collect/swap wait; the default
`BlockUntilFresh` preserves fresh visibility publication before render.

Frame output fields decompose the shared render-thread frame across desktop,
mirror, XR submit, overlay, and present work. Top-level fields include
`vr_mirror_mode`, `vr_visibility_policy`, `frame_output_budget_band`,
`frame_output_budget_ms`, `frame_output_whole_frame_ms`, and whole-frame
`p50/p90/p95/p99/worst` values. The raw `frame_outputs` object includes one row
per active output such as `DesktopScene`, `DesktopMirror`, `OpenXREyeSubmit`,
`OpenVRSubmit`, `ImGuiOverlay`, `DynamicTextOverlay`, and `Present`, with
configured/achieved rate, skip reason/counts, command count, phase CPU timings,
GPU timing when available, and flags for mirror vs. separate scene render. Each
row also publishes its stable output/view-family identity, target class and
generation, display/internal extent, format/sample/view-mask compatibility,
external-image slot, deadline/budgets/staleness, quality requirements, allowed
fallbacks, dependency/completion contract, actual work disposition, and whether
the policy decision was authorized. Aggregate fields count scene snapshots,
visibility builds, output requests, unique view families, target variants,
compiled-plan hits/misses, shared and reused work, deferrals, stale reuse,
deadline misses, planner pruning, global waits/force flushes, rejected submits,
and unapproved policy events.

Scene and asset counters identify whether a slowdown is global or asset-local:
visible renderer/submesh/triangle counts, material slots, active materials,
texture count, resident texture memory, texture upload jobs/bytes/time, shader
variant request/warm/link/fail/cache counters, skinned renderer count, bone and
blendshape upload bytes, skinning and blendshape compute dispatches, avatar
representation counts, and per-asset rows with source identity, cooked variant,
mesh, material, representation, draw count, triangles, material slots, texture
count, and skinned draw count.

GPU-driven captures now expose compactness and readback discipline: culled
commands, active buckets, empty bucket skips, full bucket scans, material
scatter dispatches, indirect generation/cull/compact timings, delayed
diagnostic draw-count values, compaction/list/bucket/meshlet overflow counters,
one-phase vs. two-phase Hi-Z mode and phase draw counts, meshlet task counters,
and visibility-buffer counters. Zero-readback variants should keep current-frame
`gpu_readback_bytes` and `gpu_mapped_buffers` at zero; delayed diagnostic
readbacks are reported separately.

The game-loop harness retains meshlet requested/production frames, Vulkan
mesh-task frame-op count, task records and frustum/cone/Hi-Z culls, resident/live/
retired bytes, lifetime rebuild/retire counters, and capture-window rebuild/
retire deltas. `ShippingFast` and `DevParity` production acceptance uses the
recorded production frame plus retained Vulkan mesh-task operation because those
profiles intentionally suppress diagnostic readback. The explicit `Diagnostics`
profile instead requires fence-delayed task records and dispatch groups. Its MCP
snapshot supplies the stable latest task/cull values so a low-frequency NDJSON
sample cannot miss the single frame in which an asynchronous readback completed.
Generic readback/map counters must remain zero in either case; delayed diagnostic
bytes stay in their separately named fields.

### Benchmark Harness

Use `Tools/Measure-GameLoopRenderPipeline.ps1` for reproducible run-to-run
rendering comparisons. It validates environment overrides before launch and
fails loud for invalid mesh-submission strategies, zero-readback material paths,
cache modes, booleans, and positive numeric fields. Important options:

```powershell
pwsh Tools/Measure-GameLoopRenderPipeline.ps1 `
  -Configuration Release `
  -CacheMode Warm `
  -Strategies CpuDirect,GpuIndirectZeroReadback,GpuMeshletZeroReadback `
  -WarmupSec 25 `
  -CaptureSec 60 `
  -Repetitions 3 `
  -OcclusionCullingMode Disabled `
  -ProfileScene "AvatarDeferred" `
  -ProfileLights "None" `
  -GpuClockPolicy "Pinned manually in vendor control panel"
```

For cross-machine renderer evidence, also fix the camera and output rather than
using a descriptive camera label alone. Specify all six `-CameraPosition*` and
`-CameraLookAt*` values together, plus `-WindowWidth`, `-WindowHeight`,
`-ProfileViewport`, and `-RenderScale`. The harness positions the camera through
MCP before warmup and rejects a partial pose. `-SampleIntervalFrames` controls
the frame-stride of the NDJSON stream (the first completed frame is always
written), so long captures do not turn profiler serialization into the measured
workload. `-VulkanGpuDrivenProfile ShippingFast`, `DevParity`, or `Diagnostics`
freezes the effective GPU-driven feature profile independently of saved user
settings. Every value is copied into the summary and capture manifest.

Use `-CacheMode Cold` for startup/cache-miss measurements; the harness clears
OpenGL shader-program caches only in cold mode unless
`-NoClearCachesBetweenVariants` is supplied. Use `-CacheMode Warm` for steady
renderer comparisons. Reports separate startup, warmup, steady-state capture,
and streaming interpretation and include p50/p90/p95/p99 frame timings, dropped
sample notes, state churn totals, asset counters, readback totals, fallback
events, and GPU-driven compactness counters.

Use `-OcclusionCullingMode Disabled`, `CpuQueryAsync`,
`CpuSoftwareOcclusion`, or `GpuHiZ` to make visibility cohorts explicit. For
correctness cohorts, select `-VulkanDiagnosticPreset StandardValidation` or
`SyncValidation`; `-VulkanCommandBufferLabels` enables Vulkan Debug Utils
command-buffer regions without passing environment overrides through an
external profiler. These selections are included in the JSON and text
manifests. The focused `Tools/Measure-VulkanFrameLoop.ps1` wrapper forwards the
same options.

After the minimum `-WarmupSec`, capture begins only after a measured quiet
window (default: five seconds, with a 120-second timeout): output workload
identity and target generations must be stable, asset/shader work must be quiet,
and retirement, planner-prune, global-wait, and force-flush counters must be
zero. Use `-StabilityWindowSec` and `-StabilityTimeoutSec` to tune this gate;
`-NoStabilityGate` is diagnostic-only. A capture is invalid if workload identity
changes, an output takes an unapproved fallback, or a Vulkan submission is
rejected. `-FailOnSteadyStateResourceChurn` covers every published retirement
kind plus planner/global synchronization, while
`-FailOnSteadyStateCommandBufferChurn` reports and gates record/reuse/dirty
outcomes with an optional `-MinSteadyStateCommandBufferCleanReuseRatio`.

Do not compare Debug and Release numbers as architectural evidence. Disable
validation layers and verbose GL debug output for benchmark captures unless the
test explicitly measures validation cost. Pin GPU clocks manually through the
vendor tool when possible and record that policy in `-GpuClockPolicy`; the
harness documents the policy but does not change driver power settings.

### Vulkan Performance Presets And Gates

`Tools/Benchmarks/Invoke-VulkanPerf.ps1` is the canonical one-command path for
Vulkan baseline and regression work. It rebuilds the Release editor unless
`-NoBuild` is supplied, selects tracked unit-testing-world settings through
`XRE_UNIT_TEST_WORLD_SETTINGS_PATH`, runs the existing process capture harness,
writes a run manifest, and evaluates the result through
`XREngine.Benchmarks --vulkan-perf`.

```powershell
# Short, one-cohort feedback; always reported as non-promotable.
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 -Preset Quick

# Explicit clean desktop and OpenXR quick profiles.
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 `
  -Preset Quick -Cohorts desktop-deferred-static
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 `
  -Preset Quick -Cohorts rvc-deferred-foveation-off

# Three warm desktop repetitions per selected Deferred/Uber cohort.
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 `
  -Preset Compare `
  -BaselinePath Build/_AgentValidation/00000000-000000-shared/baselines/vulkan-perf/desktop.json

# Full desktop and available Vulkan RVC matrix.
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 `
  -Preset Gate `
  -BaselinePath Build/_AgentValidation/00000000-000000-shared/baselines/vulkan-perf/gate.json

# Baselines are replaced only by this explicit action.
pwsh Tools/Benchmarks/Invoke-VulkanPerf.ps1 `
  -Preset Gate `
  -BaselinePath Build/_AgentValidation/00000000-000000-shared/baselines/vulkan-perf/gate.json `
  -AcceptBaseline
```

The tracked contract is
`XREngine.Benchmarks/VulkanPerformance/vulkan-performance-cohorts.json`.
Captured machine evidence remains under
`Build/_AgentValidation/<timestamp>-vulkan-perf-<preset>/`. A Gate or Compare
run returns nonzero for capture invalidity, manifest mismatches, excessive
variance, absolute-budget failures, baseline regressions, fallbacks, readbacks,
or missing required desktop/eye renders. Unsupported requested foveation is an
explicit failure, never a silent substitution.

The four profile modes have distinct intent:

| Mode | Validation and labels | Editor diagnostics | Comparison use |
| --- | --- | --- | --- |
| `Diagnostics` | Allowed/enabled by the selected diagnostic preset | Profiler panels, ImGui, dynamic text, and verbose logging may be enabled | Intrusive investigation only |
| `DevelopmentProfile` | Explicitly selected; labels and detailed scopes allowed | Normal editor and profiling tools allowed | Development trend only |
| `CleanProfile` | Validation, dense timestamps, command labels, and P3 logging prohibited | ImGui and dynamic diagnostic overlays skipped | Quick feedback; non-promotable |
| `ReleaseBenchmark` | Same non-intrusive restrictions as CleanProfile | ImGui and dynamic diagnostic overlays skipped | Compare/Gate promotion evidence |

Vulkan presentation policy is an independent axis from observer/profile mode.
The editor defaults to `Stable` (FIFO, refresh paced, no frame generation).
`LowLatency` selects Mailbox with the bounded hybrid limiter,
`Uncapped` requires Immediate mode for GPU-headroom diagnosis, and
`FrameGeneration` requires the separately provisioned Streamline presentation
path. Unsupported requested modes fail or visibly downgrade according to their
profile contract; they are never silently measured as another mode.

Use `-VulkanPresentationProfile Stable|LowLatency|Uncapped|FrameGeneration`
with `Measure-GameLoopRenderPipeline.ps1`. `Invoke-VulkanPerf.ps1` selects the
profile declared by each tracked cohort (desktop headroom cohorts default to
`Uncapped`, while RVC/OpenXR mirror cohorts remain `Stable`). The launch-only
diagnostic overrides are `XRE_VULKAN_PRESENTATION_PROFILE` and
`XRE_TARGET_REFRESH_HZ`; every capture records the requested/resolved profile,
native present mode, target and actual intervals, swapchain image/frame-slot
counts, frame-generation state, validation state, render-target mode, and
present-timing extension capabilities.

The matching VS Code tasks are `Benchmark-Vulkan-Clean-Desktop` and
`Benchmark-Vulkan-Clean-OpenXR`. At engine startup, the resolved mode emits one
`[PerformanceProfile]` line stating its suitability and promotion status; an
unspecified mode resolves to `DevelopmentProfile`. Clean modes also apply
process-local normal-verbosity, validation-off, command-label-off, P3-off, GPU
indirect diagnostic-logging-off, Vulkan diagnostic-trace-off, and ImGui-off
overrides before renderer creation without changing persisted user or game
settings.

Warmup covers shader/pipeline and texture residency. Capture begins only after
the existing stability window reports a stable workload and no streaming or
resource-retirement churn. Cold-start and streaming-churn studies remain
separate diagnostic captures and cannot be used as warm steady-state promotion
evidence. Clean and ReleaseBenchmark launches also pass `--no-mcp`, so a saved
MCP preference cannot add a listener or profiler RPC work to the benchmark.

Measure observer overhead for all four modes against the same ReleaseBenchmark
capture with:

```powershell
pwsh Tools/Benchmarks/Measure-VulkanProfileOverhead.ps1 `
  -Cohort desktop-deferred-static
```

The report records the mode, clean-comparison classification, expected observer
overhead, p50/p90/p95/p99/worst, and the p95 absolute/percentage delta for each
mode. Diagnostic and DevelopmentProfile captures deliberately enable validation,
command labels, and dense timestamps; CleanProfile and ReleaseBenchmark force
those observers off. The overhead comparison disables MCP in every mode because
MCP is not part of the profile-mode contract.

The evaluator itself has GPU-free fixtures:

```powershell
dotnet run -c Release `
  -p:VulkanPerformanceToolOnly=true `
  -p:BuildProjectReferences=false `
  --no-restore `
  --project XREngine.Benchmarks/XREngine.Benchmarks.csproj `
  -- --vulkan-perf --self-test
```

### LLM Self-Iteration Campaigns

`XREngine.Benchmarks --self-iterate` composes the editor build, process
measurement harness, MCP CPU/GPU dumps, renderer reload tools, crash recovery,
formal scenario-matrix comparison, and accepted/rejected attempt ledgers into a
bounded autonomous loop. Campaign JSONC can mix Vulkan/OpenGL,
CPU-direct/GPU-zero-readback, Unit Testing World settings files, and
scenario-specific rendering controls.

Each scenario normally records a short dense `DevelopmentProfile` cohort for
LLM CPU/per-pipeline GPU diagnosis and a separate repeated `CleanProfile`
cohort for acceptance. Detailed timestamp overhead therefore does not
contaminate the formal before/after result.

Use `Tools/Benchmarks/Invoke-SelfIteration.ps1 -ValidateOnly` to validate a
campaign without launching an editor or LLM, `-BaselineOnly` to capture the
formal matrix without permitting edits, and the command without either switch
to run the bounded loop. See
[Self-Iterating Rendering Performance Loop](self-iterating-performance-loop.md)
for configuration, safety, reload, evidence, and acceptance details.

### Sampling CPU Profilers

Counter streams explain what changed; sampled CPU profilers explain where the
time went. Capture a run with `XRE_PROFILE_CAPTURE=1` and record the engine
frame id, then collect CPU samples from the same window:

- PerfView or Windows Performance Recorder/Analyzer for ETW CPU stacks.
- `dotnet-trace collect --process-id <pid> --profile cpu-sampling` for portable
  .NET sampling, then open the result in SpeedScope if desired.
- Superluminal for native and managed mixed stacks on Windows.
- VTune or Nsight Systems when correlating CPU submission with GPU queue work.

Match samples back to `render_frame_id`, profiler scope names, and the
`profiler-render-stats.ndjson` timestamp. Hot render scopes should keep stable,
allocation-free names that match the engine profiler rows; marker creation in
per-frame paths must not allocate.

### GPU Timestamp Policy

GPU timestamp instrumentation is opt-in diagnostic work, not a hidden benchmark
variable. Production frames keep GPU pipeline profiling disabled by default.
Vulkan keeps a coarse whole-command-buffer GPU timing path available for frame
lifecycle stats, but dense per-command Vulkan render-pipeline histories are
recorded only when `XRE_GPU_TIMESTAMP_DENSE=1` is set at launch. Dense timestamp
mode is reserved for diagnostics, is marked in manifests and samples via
`gpu_timestamps_dense_mode`, and can perturb frame pacing.

The in-editor **Profiler Settings** panel also exposes **Enable Profiler
Component Timing**, which independently controls per-component tick timing
capture for the Components panel without affecting frame logging or render
statistics.

When code-profiler frame logging is enabled, the stats thread also writes
disk diagnostics for severe frame anomalies:

- `profiler-fps-drops.log` records completed-frame spikes using the per-thread snapshot history. Thread totals are classified work time; wall time and downstream render-pressure wait time are logged separately as `ThreadWallTimeMs` and `ThreadDownstreamRenderPressureMs`.
- `profiler-render-stalls.log` records when an active render dispatch goes longer than
  **CodeProfilerRenderStallThresholdMs** without completing a render, then logs how long recovery took once the next render finishes.
- `profiler-conditional-loop-spikes.log` records rate-limited slow scopes tagged
  `ConditionalLoop`.
- `profiler-one-off-invokes.log` records rate-limited slow scopes tagged
  `OneOffInvoke`.
- `profiler-main-thread-invokes.log` records verbose queued render-thread invoke diagnostics when **Enable Main Thread Invoke Diagnostics** is enabled.

Profiler settings also allow **Update (s)** to be set to `0` for every-render
graph refresh, and expose per-category CPU/GPU timing graph toggles for raw ms
lines, smoothed display lines, and interpolation between buffered updates.

### Option 3: Programmatic

```csharp
Engine.WireProfilerSenderCollectors();
UdpProfilerSender.Start(9142);

// … later …
UdpProfilerSender.Stop();
```

## VS Code Integration

### Tasks (`.vscode/tasks.json`)

| Task | Description |
|------|-------------|
| `Build-Profiler` | Builds `XREngine.Profiler.csproj` |
| `Start-Profiler-NoDebug` | Builds and launches the profiler app |
| `Start-Editor-WithProfiler-NoDebug` | Launches the editor with `XRE_PROFILER_ENABLED=1` |

### Launch Configurations (`.vscode/launch.json`)

| Configuration | Description |
|---------------|-------------|
| **Debug Profiler** | F5-debugs the profiler app (editor must be started separately) |
| **Debug Profiler (with Editor)** | Launches the editor with profiling enabled, then F5-debugs the profiler |

### Typical Workflow

1. Run the **Start-Editor-WithProfiler-NoDebug** task (or launch the editor with `XRE_PROFILER_ENABLED=1`).
2. Run the **Start-Profiler-NoDebug** task, or select **Debug Profiler** from the launch dropdown.
3. The profiler window connects automatically — the overlay disappears and panels populate.

## Project Structure

```
XREngine.Data/Profiling/
├── ProfilerProtocol.cs          # Wire constants, framing helpers
├── ProfilerFramePacket.cs       # Frame + thread + node MemoryPack DTOs
├── ProfilerStatsPacket.cs       # Render, alloc, BVH, job, invoke, heartbeat DTOs
└── UdpProfilerSender.cs         # Background sender thread (engine-side)

XRENGINE/Engine/
├── Engine.ProfilerSender.cs     # 6 collector delegates bridging engine → packets
└── Engine.Lifecycle.cs          # Init/cleanup hooks

XREngine.Profiler/
├── Program.cs                   # Entry point (Silk.NET GLFW window)
├── UdpProfilerReceiver.cs       # Background receiver thread + multi-instance tracking
├── ProfilerImGuiApp.cs          # ImGui lifecycle, docking, menu bar, waiting overlay
├── ProfilerPanelRenderer.cs     # All 9 panel draw methods + aggregation logic
├── ImGuiDockBuilderNative.cs    # P/Invoke wrapper for cimgui DockBuilder
└── XREngine.Profiler.csproj     # Depends only on XREngine.Data (not the engine)
```

## In-Process Profiler Panels

The editor also contains in-process profiler panels at
`XREngine.Editor/IMGUI/EditorImGuiUI.ProfilerPanel.cs`. They operate
independently of the remote profiler and are opened from **View > Profiler**.
There is no in-editor profiler dockspace host; each view is a normal standalone
ImGui panel with its own menu item:

- **Profiler Settings**
- **CPU Timings**
- **FPS Drop Spikes**
- **Render Stats**
- **GPU Timings**
- **Thread Allocations**
- **Component Timings**
- **BVH Metrics**
- **Job System**
- **Main Thread Invokes**

Panels that depend on a profiler collection setting expose that toggle at the
top of the panel. CPU Timings and FPS Drop Spikes expose **Frame Logging**;
Component Timings exposes **Frame Logging** and **Component Timing**; Render
Stats exposes **Stats Tracking**; GPU Timings exposes **Stats Tracking** and
**GPU Pipeline**; Thread Allocations exposes **Alloc Tracking**; Main Thread
Invokes exposes **Invoke Diagnostics**.

- When the profiler group is hidden (`_showProfiler == false`), it returns
  immediately.
- Engine snapshot collection is throttled on the app thread and only requests
  telemetry needed by visible panels.
- When UDP sending is enabled, the editor keeps Profiler Settings available and
  avoids drawing local telemetry panels while the external profiler owns live
  collection.

For minimal editor overhead, keep in-process profiler panels closed and use the
remote profiler instead.

## Dedicated Vulkan RenderBench

`XREngine.RenderBench` is the editor-free process for deterministic
presentationless Vulkan control measurements. It constructs no `XRWindow`,
editor panel, ImGui UI, input service, dynamic text, or window title. The Phase
2 fixture is a synthetic clear whose fixed-step animation, random seed, output
contract, warmup, stability window, and capture length are explicit.

Run a bounded process without MCP:

```powershell
dotnet run --project .\XREngine.RenderBench\XREngine.RenderBench.csproj -- `
  --output-dir .\Build\_AgentValidation\<run> `
  --execution-mode Presentationless `
  --recipe deterministic-clear `
  --fixture synthetic-clear `
  --warmup-frames 30 `
  --stability-frames 5 `
  --capture-frames 120 `
  --fixed-step 0.016666666666666666 `
  --random-seed 5784133 `
  --frozen-world
```

Use the named manager for an isolated build and MCP lifecycle:

```powershell
.\Tools\Manage-McpRenderBenchSession.ps1 Start -Name profile-control
.\Tools\Manage-McpRenderBenchSession.ps1 Run -Name profile-control
.\Tools\Manage-McpRenderBenchSession.ps1 Status -Name profile-control
.\Tools\Manage-McpRenderBenchSession.ps1 Stop -Name profile-control
```

The manager places build artifacts, logs, cache, metadata, PID/start-time
ownership, endpoint identity, and the shutdown event under its named shared
session. Result evidence is placed in a bounded
`Build/_AgentValidation/<run>/` root. `Stop` validates process ownership and
requests frame-boundary cancellation before it considers forced termination.

MCP is available while the process is idle and after completion. The listener
and all in-flight request handlers are stopped and drained before a measured
capture begins. The legacy `start_render_bench` path also stops MCP before
warmup. Runtime profile preparation and stabilization may run asynchronously
while MCP remains available; `start_render_profile` serializes its accepted
response, suspends the listener, and only then releases the parked capture
worker at its published frame boundary. The listener resumes after capture and
delayed GPU-query drainage reach a terminal state. The result contains canonical effective-configuration and
workload hashes, managed executable hash, adapter/driver identity, delayed GPU
query results, CPU samples, allocation count, output hash, and explicit
stability gates. Control-fixture results are diagnostic renderer-submit
evidence; they are not equivalent to desktop WSI, OpenXR, or a production
render-graph cohort.

### RenderBench Runtime Profile MCP Tools

The editor-independent implementation lives in `XREngine.Runtime.Automation`.
Its context declares optional `World`, `Renderer`, `RenderTarget`,
`ProfilerSession`, `Editor`, and `Window` capabilities. A tool is rejected with
the exact missing capability list; a synthetic presentationless recipe needs
no editor, window, or world. Mutating calls require RenderBench `Control` policy
and the named session token, and successful idempotent calls are not executed a
second time.

| Tool | Purpose |
| --- | --- |
| `list_render_profile_targets` | Report supported and explicitly unsupported component/execution-mode targets. |
| `load_render_profile_recipe` | Parse JSON/JSONC, reject unknown fields, validate the schema and target, and return a stable recipe hash/id. |
| `prepare_render_profile` | Return a session ID immediately while device/fixture preparation and stabilization continue asynchronously. |
| `wait_render_profile_ready` | Wait outside capture for preparation to become armable. |
| `arm_render_profile` | Warm and park the dedicated capture worker, then publish its exact next engine/render frame. |
| `start_render_profile` | Return acceptance, suspend MCP, release the worker, drain delayed queries, and resume MCP at a terminal state. |
| `stop_render_profile` / `cancel_render_profile` | Stop at a frame boundary or cancel preparation/capture/drain with renderer cleanup. |
| `get_render_profile_status` | Read buffered state without calling the renderer or render workers. |
| `get_render_profile_result` | Return only a completed result plus artifact paths. |
| `run_render_profile_matrix` | Return a job ID for a bounded worker-count matrix; variants execute sequentially and MCP is suspended only for each measured capture/drain interval. |
| `get_render_profile_matrix_status` / `cancel_render_profile_matrix` | Inspect or cancel a matrix job. |

`Created` after preparation means ready-to-arm for schema-v1 clients. Other
states are `Preparing`, `Stabilizing`, `Armed`, `Capturing`, `Draining`,
`Completed`, `Failed`, and `Cancelled`. Timeouts fail visibly; unsupported
targets and requirements never select a fallback renderer.

### Phase 4 Recipes and Deterministic Fixtures

The authoritative JSONC schema is
`.vscode/schemas/render-profile-recipe.schema.json`. A recipe declares every
execution, output, timing, instrumentation, scene, mutation, workload,
validation, and acceptance-budget input. Unknown fields and unsupported enum
values fail during `load_render_profile_recipe`; no editor preference is read.
Tracked examples are under `docs/examples/profiling/recipes/`.

Run a recipe without MCP through the same executor:

```powershell
dotnet .\Build\RenderBench\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.RenderBench.dll `
  --output-dir .\Build\_AgentValidation\<run>\reports\lighting `
  --recipe-file .\docs\examples\profiling\recipes\gpu-lighting-pass.jsonc
```

The stable fixture names are:

- Controls and CPU preparation: `synthetic-clear`, `noop-control`,
  `command-chain-signature`, and `packet-lowering`.
- Command work: `primary-command-small`, `primary-command-medium`,
  `primary-command-large`, `secondary-command-recording`,
  `command-buffer-stable-reuse`, and `command-buffer-forced-dirty`.
- Resource/submission work: `descriptor-publication`, `resource-planning`,
  `queue-lock-submit`, and `upload-fixed`.
- GPU passes: `gpu-shadow`, `gpu-depth-normal`, `gpu-gbuffer`,
  `gpu-lighting`, `gpu-transparency`, `gpu-ao`, `gpu-bloom`, `gpu-tsr`, and
  `gpu-final-composition`.
- Full presentationless proxies: `presentationless-deferred` and
  `presentationless-uber`.

GPU-pass fixtures compile their fullscreen shader and create their dynamic-
rendering pipeline before capture. Secondary fixtures create persistent workers
with one command pool per worker and one secondary buffer per frame slot.
Descriptor layouts/pools/buffers and upload staging/device buffers are likewise
resident before capture. Native object creation during capture occurs only when
the recipe explicitly selects resource, descriptor, or pipeline churn.

The effective-configuration hash includes the complete recipe and resolved
catalog defaults. The workload hash deliberately excludes recipe name, worker
count, mutation policy, instrumentation, and budgets; therefore scaling and
reuse/dirty variants remain directly comparable. Results publish exact work
counters, per-repetition retained samples, inclusion/exclusion manifests,
adapter/driver identity, output hash, optional PNG, and explicit gates for
fixture/shader/fallback identity, expected work, query drainage, allocations,
and percentile budgets. Expected counters are per retained frame and are
multiplied by `capture_frames * repetitions` during validation.
