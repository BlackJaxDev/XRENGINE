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
   - **Profiler Tree** — call-tree hierarchy with root method graphs (`PlotLines`) and worst-frame tracking
   - **FPS Drop Spikes** — sortable table of frame-time anomalies with hottest call path
   - **Render Stats** — draw calls, VRAM, FBO bandwidth, render matrix, CPU spatial tree
  - **GPU Pipeline** — generic render-pipeline command GPU timings, root history plots, and hierarchical pass breakdowns
   - **Thread Allocations** — per-phase GC allocation stats (last / avg / max KB)
   - **BVH Metrics** — build, refit, cull, raycast counts and timings
   - **Job System** — worker count, queue depth, per-priority wait times
   - **Main Thread Invokes** — cross-thread dispatch log
   - **Connection Info** — link status, packet counters, multi-instance source table

### GPU Pipeline Timing

The profiler can collect GPU timestamp timings around generic `ViewportRenderCommand`
execution, which makes it possible to see where render-pipeline time is being spent
without instrumenting each pass manually.

- Enable it from the in-editor profiler window with **Enable GPU Pipeline Profiling**.
- Results appear in the new **GPU Pipeline** panel in both the in-process and remote profilers.
- The panel shows backend/status text, a resolved whole-frame GPU total, root-series history plots, and a hierarchical per-command timing tree.
- In the in-editor profiler, each render-pipeline root history graph has a **Dump** button that writes a unique `profiler-gpu-pipeline-*.log` file under the active `Build/Logs/.../<session>/` folder. The dump includes retained frame samples, warmup-excluded summaries, worst frames, render-thread CPU/present deltas, named XRWindow CPU phase aggregates, slow command/scope rankings, shader/material hint rankings, and full aggregate tables for LLM analysis.
- To avoid OpenGL driver stalls, timestamp sampling is capped per frame and temporarily throttled after slow query calls. Shadow-map passes keep high-level pass timings but skip per-mesh shadow draw scopes.
- Current backend support is **OpenGL**. Unsupported renderers report status text rather than falling back to CPU timings.

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

The in-editor profiler window also exposes **Enable GPU Pipeline Profiling**,
which turns on command-level GPU timestamp collection for supported renderers.

Use **Dump Speed Profile** in the in-editor profiler window after the editor has
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

### Render Stats Capture Schema v2

`profiler-capture-manifest.json` now records
`xrengine.profile_capture.render_stats.v2` with `schema_version = 2`. The
manifest makes benchmark context explicit: build configuration, world mode,
forced and effective mesh submission strategy, zero-readback material draw path,
render backend, GPU/vendor, scene, camera, lights, viewport, render scale,
stereo mode, validation/debug state, shader and texture cache mode, GPU clock
policy, target refresh rate, frame budget, warmup/capture durations, and any
invalid benchmark environment overrides detected at launch.

Each `profiler-render-stats.ndjson` sample includes the old frame timing fields
plus renderer-state churn counters: indirect-count and multi-draw calls, shader
program and pipeline switches, VAO/buffer/SSBO/UBO binds, texture binds and
redundant bind skips, active texture-binding rung, uniform calls, upload bytes,
barriers by kind, readback bytes, mapped-buffer reads, active stereo mode,
active backend, validation/debug flags, and timestamp query/readback counts.

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
  -ProfileScene "AvatarDeferred" `
  -ProfileLights "None" `
  -GpuClockPolicy "Pinned manually in vendor control panel"
```

Use `-CacheMode Cold` for startup/cache-miss measurements; the harness clears
OpenGL shader-program caches only in cold mode unless
`-NoClearCachesBetweenVariants` is supplied. Use `-CacheMode Warm` for steady
renderer comparisons. Reports separate startup, warmup, steady-state capture,
and streaming interpretation and include p50/p90/p95/p99 frame timings, dropped
sample notes, state churn totals, asset counters, readback totals, fallback
events, and GPU-driven compactness counters.

Do not compare Debug and Release numbers as architectural evidence. Disable
validation layers and verbose GL debug output for benchmark captures unless the
test explicitly measures validation cost. Pin GPU clocks manually through the
vendor tool when possible and record that policy in `-GpuClockPolicy`; the
harness documents the policy but does not change driver power settings.

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
Profile captures issue coarse begin/end timestamps per render command by
default and read results with a delayed, non-blocking policy. Dense timestamp
mode is reserved for diagnostics, is marked in manifests and samples via
`gpu_timestamps_dense_mode`, and can perturb very small passes.

The in-editor profiler window also exposes **Enable Profiler Component Timing**,
which independently controls per-component tick timing capture for the
Components panel without affecting frame logging or render statistics.

When code-profiler frame logging is enabled, the stats thread also writes
disk diagnostics for severe frame anomalies:

- `profiler-fps-drops.log` records completed-frame spikes using the per-thread snapshot history.
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

## In-Process Profiler Panel (Legacy)

The editor still contains the original in-process profiler panel at
`XREngine.Editor/IMGUI/EditorImGuiUI.ProfilerPanel.cs` (1,308 lines). It
operates **independently** of the remote profiler:

- When hidden (`_showProfiler == false`), it returns immediately — zero cost.
- When open, it reads the same engine snapshots on the main/render thread and
  performs its own aggregation, caching, and ImGui rendering.
- It can run simultaneously with the UDP sender without conflict.

For minimal overhead, keep the in-process panel closed and use the remote
profiler instead.
