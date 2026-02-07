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
| `0x02` | `RenderStats` | ~30 Hz | Draw calls, VRAM, FBO, render matrix, octree |
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
3. Renders 8 dockable panels via `ProfilerPanelRenderer`:
   - **Profiler Tree** — call-tree hierarchy with root method graphs (`PlotLines`) and worst-frame tracking
   - **FPS Drop Spikes** — sortable table of frame-time anomalies with hottest call path
   - **Render Stats** — draw calls, VRAM, FBO bandwidth, render matrix, octree
   - **Thread Allocations** — per-phase GC allocation stats (last / avg / max KB)
   - **BVH Metrics** — build, refit, cull, raycast counts and timings
   - **Job System** — worker count, queue depth, per-priority wait times
   - **Main Thread Invokes** — cross-thread dispatch log
   - **Connection Info** — link status, packet counters, multi-instance source table

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

The VS Code task **start-editor-with-profiler-no-debug** does this automatically.

### Option 2: Runtime Toggle

In the editor's Settings panel, toggle **Enable Profiler UDP Sending** under
Debug Options. This starts/stops the sender thread at runtime with zero residual
overhead when off.

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
| `build-profiler` | Builds `XREngine.Profiler.csproj` |
| `start-profiler-no-debug` | Builds and launches the profiler app |
| `start-editor-with-profiler-no-debug` | Launches the editor with `XRE_PROFILER_ENABLED=1` |

### Launch Configurations (`.vscode/launch.json`)

| Configuration | Description |
|---------------|-------------|
| **Debug Profiler** | F5-debugs the profiler app (editor must be started separately) |
| **Debug Profiler (with Editor)** | Launches the editor with profiling enabled, then F5-debugs the profiler |

### Typical Workflow

1. Run the **start-editor-with-profiler-no-debug** task (or launch the editor with `XRE_PROFILER_ENABLED=1`).
2. Run the **start-profiler-no-debug** task, or select **Debug Profiler** from the launch dropdown.
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
├── ProfilerPanelRenderer.cs     # All 8 panel draw methods + aggregation logic
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
