# XREngine.Profiler — Out-of-Process Profiling Tool

## Overview

A standalone ImGui application (`XREngine.Profiler`) that runs in a separate process and renders the same profiler panel currently embedded in `XREngine.Editor`. The engine/editor sends telemetry over **UDP on localhost** with minimal sender-side overhead, and the profiler tool receives, deserializes, and visualizes it independently.

### Goals

1. **Zero-copy sender path** — serialization happens on a dedicated background thread; the hot path (render/update/physics) is never blocked.
2. **Graceful degradation** — if the profiler app is not running, packets are silently dropped by the OS (UDP fire-and-forget).
3. **Exact visual parity** — the profiler window should look and behave identically to the existing `EditorImGuiUI.ProfilerPanel`.
4. **Minimal engine dependency** — the profiler app depends only on `XREngine.Data` (shared protocol types) and ImGui. It does **not** reference the engine or editor.

---

## Architecture

```
┌──────────────────────────┐         UDP (localhost:9142)        ┌───────────────────────────┐
│   XREngine / Editor      │  ────────────────────────────────►  │   XREngine.Profiler       │
│                          │                                     │                           │
│  Engine.CodeProfiler     │    fire-and-forget datagrams        │  UdpProfilerReceiver      │
│  Engine.Rendering.Stats  │    (MessagePack binary frames)      │  ProfilerImGuiApp         │
│  Engine.Allocations      │                                     │  (standalone Silk.NET      │
│  Engine.Rendering.BvhStats│                                    │   + ImGui window)          │
│  Engine.Jobs             │                                     │                           │
│                          │                                     │  Renders:                 │
│  UdpProfilerSender       │                                     │  - Thread Allocations     │
│  (background thread,     │                                     │  - Rendering Statistics   │
│   pools + ring buffer)   │                                     │  - Code Profiler Tree     │
│                          │                                     │  - BVH GPU Metrics        │
└──────────────────────────┘                                     │  - Job System Stats       │
                                                                 │  - FPS Drop Spikes        │
                                                                 │  - Root Method Graphs     │
                                                                 └───────────────────────────┘
```

---

## Project Layout

```
XREngine.Profiler/
    XREngine.Profiler.csproj        # WinExe, net10.0-windows7.0
    Program.cs                      # Entry point — creates GLFW/OpenGL window + ImGui context
    ProfilerImGuiApp.cs             # Main render loop, docking, menu bar
    ProfilerPanel.cs                # Port of EditorImGuiUI.ProfilerPanel (reads from local state, not Engine.*)
    UdpProfilerReceiver.cs          # Background thread listening on UDP, deserializes into ring buffers

XREngine.Data/
    Profiling/
        ProfilerProtocol.cs         # Shared enums, message IDs, constants (port, max datagram size)
        ProfilerFramePacket.cs      # MemoryPack-serializable frame snapshot packet
        ProfilerStatsPacket.cs      # MemoryPack-serializable rendering/allocation/BVH/job stats packet
        UdpProfilerSender.cs        # Static fire-and-forget sender (used by engine, lives in XREngine.Data so engine can call it)
```

> **Why XREngine.Data?** — It is the lowest-level shared library already referenced by both `XREngine` (engine) and can be referenced by `XREngine.Profiler` without pulling in the entire engine. It already has `MemoryPack` as a dependency.

---

## Wire Protocol

### Transport

| Property | Value |
|----------|-------|
| Transport | UDP / IPv4 loopback (`127.0.0.1`) |
| Default port | `9142` (configurable via `XRE_PROFILER_PORT` env var) |
| Max datagram | 65,000 bytes (below 65,507 UDP limit) |
| Serialization | **MemoryPack** (already in XREngine.Data; zero-alloc, extremely fast) |
| Framing | `[1 byte MessageType][4 bytes payload length][payload]` |

### Message Types

| ID | Name | Frequency | Description |
|----|------|-----------|-------------|
| `0x01` | `ProfilerFrame` | ~30 Hz | Full profiler tree snapshot (mirrors `ProfilerFrameSnapshot`) |
| `0x02` | `RenderStats` | ~30 Hz | Draw calls, triangles, VRAM, FBO bandwidth, octree, render-matrix |
| `0x03` | `ThreadAllocations` | ~30 Hz | Per-thread GC allocation ring snapshot |
| `0x04` | `BvhMetrics` | ~30 Hz | GPU BVH build/refit/cull/raycast counters and timings |
| `0x05` | `JobSystemStats` | ~30 Hz | Worker count, queue depths per priority, avg wait times |
| `0x06` | `MainThreadInvokes` | on change | Latest main-thread invoke log entries (delta) |
| `0x07` | `Heartbeat` | 1 Hz | Keeps connection "alive" in the UI; carries process name & PID |

### Packet Structures (MemoryPack)

```csharp
[MemoryPackable]
public partial class ProfilerFramePacket
{
    public float FrameTime;
    public ProfilerThreadData[] Threads;
    public Dictionary<int, float[]> ThreadHistory;  // threadId → sample ring
}

[MemoryPackable]
public partial class ProfilerThreadData
{
    public int ThreadId;
    public float TotalTimeMs;
    public ProfilerNodeData[] RootNodes;
}

[MemoryPackable]
public partial class ProfilerNodeData
{
    public string Name;
    public float ElapsedMs;
    public ProfilerNodeData[] Children;
}

[MemoryPackable]
public partial class RenderStatsPacket
{
    public int DrawCalls;
    public int MultiDrawCalls;
    public int TrianglesRendered;
    // VRAM
    public long AllocatedVRAMBytes;
    public long AllocatedBufferBytes;
    public long AllocatedTextureBytes;
    public long AllocatedRenderBufferBytes;
    // FBO
    public long FBOBandwidthBytes;
    public int FBOBindCount;
    // Render matrix
    public bool RenderMatrixStatsReady;
    public int RenderMatrixApplied;
    public int RenderMatrixSetCalls;
    public int RenderMatrixListenerInvocations;
    public KeyValuePair<string, int>[] RenderMatrixListenerCounts;
    // Octree
    public bool OctreeStatsReady;
    public int OctreeAddCount;
    public int OctreeMoveCount;
    public int OctreeRemoveCount;
    public int OctreeSkippedMoveCount;
}

[MemoryPackable]
public partial class ThreadAllocationsPacket
{
    public AllocationSlice Render;
    public AllocationSlice CollectSwap;
    public AllocationSlice Update;
    public AllocationSlice FixedUpdate;
}

[MemoryPackable]
public partial class AllocationSlice
{
    public long LastBytes;
    public double AverageBytes;
    public long MaxBytes;
    public int Samples;
    public int Capacity;
}

[MemoryPackable]
public partial class BvhMetricsPacket
{
    public int BuildCount;
    public float BuildMilliseconds;
    public int RefitCount;
    public float RefitMilliseconds;
    public int CullCount;
    public float CullMilliseconds;
    public int RaycastCount;
    public float RaycastMilliseconds;
}

[MemoryPackable]
public partial class JobSystemStatsPacket
{
    public int WorkerCount;
    public bool IsQueueBounded;
    public int QueueCapacity;
    public int QueueSlotsInUse;
    public int QueueSlotsAvailable;
    public JobPriorityStats[] Priorities;
}

[MemoryPackable]
public partial class JobPriorityStats
{
    public int Priority;
    public int QueuedAny;
    public int QueuedMain;
    public int QueuedCollect;
    public double AvgWaitMs;
}

[MemoryPackable]
public partial class HeartbeatPacket
{
    public string ProcessName;
    public int ProcessId;
    public long UptimeMs;
}
```

### Large-Snapshot Fragmentation

If a `ProfilerFrame` packet exceeds 65 KB (deep call trees), the sender **prunes leaf nodes** with `ElapsedMs < 0.01` before serializing. If still too large, it splits into multiple numbered fragments with a shared sequence ID. The receiver reassembles before deserialization. This is an edge case; most frames will fit in a single datagram.

---

## Sender Design (`UdpProfilerSender`)

Lives in **XREngine.Data** so the engine can call it without a circular dependency. The engine hooks it up during initialization.

### Key Design Decisions

1. **Dedicated sender thread** — a single background thread at `BelowNormal` priority wakes at ~30 Hz, snapshots all stat sources, serializes with MemoryPack, and calls `UdpClient.Send`. The hot threads (render, update) never touch the socket.
2. **Lock-free data hand-off** — stats are already published via `volatile` references or `Interlocked` (see `Engine.CodeProfiler._readySnapshot`, `Engine.Rendering.Stats` double-buffer pattern). The sender thread simply reads these the same way the UI does today.
3. **Pre-allocated send buffer** — a single `byte[65000]` buffer is reused across sends to avoid allocations.
4. **Fire-and-forget** — no ACKs, no retransmission. If the profiler app is down, `UdpClient.Send` to localhost drops silently.
5. **Opt-in activation** — sending is enabled by environment variable `XRE_PROFILER_ENABLED=1` or a runtime toggle. When disabled, zero overhead (no thread, no allocations).

### Startup Wiring (in Engine)

```csharp
// In Engine initialization, after CodeProfiler is created:
if (Environment.GetEnvironmentVariable("XRE_PROFILER_ENABLED") == "1")
{
    UdpProfilerSender.Start(
        port: int.TryParse(Environment.GetEnvironmentVariable("XRE_PROFILER_PORT"), out var p) ? p : 9142);
}
```

The sender reads from:
- `Engine.Profiler.TryGetSnapshot(...)` → `ProfilerFramePacket`
- `Engine.Rendering.Stats.*` → `RenderStatsPacket`
- `Engine.Allocations.GetSnapshot()` → `ThreadAllocationsPacket`
- `Engine.Rendering.BvhStats.Latest` → `BvhMetricsPacket`
- `Engine.Jobs.*` → `JobSystemStatsPacket`
- `Engine.GetMainThreadInvokeLogSnapshot()` → `MainThreadInvokesPacket` (delta)

---

## Receiver Design (`UdpProfilerReceiver`)

Lives in **XREngine.Profiler**.

- Background thread binds `UdpClient` to `0.0.0.0:9142`.
- On each datagram: read message type byte, MemoryPack-deserialize into the corresponding packet type.
- Store latest packet in a `volatile` reference per message type (same lock-free pattern the engine already uses).
- The ImGui render loop reads these volatile references each frame — no locking, no blocking.

---

## Profiler App (`XREngine.Profiler`)

### Window Setup

Standalone **Silk.NET.Windowing** + **Silk.NET.OpenGL** + **Silk.NET.OpenGL.Extensions.ImGui** window. No engine, no scene graph, no ECS. Just:

1. Create GLFW window (1280×800, resizable).
2. Initialize OpenGL 3.3+ context.
3. Create `ImGuiController` (from Silk.NET.OpenGL.Extensions.ImGui).
4. Main loop: poll events → `ImGui.NewFrame()` → render profiler panels → `ImGui.Render()` → swap buffers.

### Panel Code

The `ProfilerPanel.cs` in `XREngine.Profiler` is a **direct port** of `EditorImGuiUI.ProfilerPanel.cs` with these changes:

| In Editor | In Profiler |
|-----------|-------------|
| `Engine.Profiler.TryGetSnapshot(...)` | `UdpProfilerReceiver.LatestFrame` |
| `Engine.Rendering.Stats.DrawCalls` | `UdpProfilerReceiver.LatestRenderStats.DrawCalls` |
| `Engine.Allocations.GetSnapshot()` | `UdpProfilerReceiver.LatestAllocations` |
| `Engine.Rendering.BvhStats.Latest` | `UdpProfilerReceiver.LatestBvhMetrics` |
| `Engine.Jobs.*` | `UdpProfilerReceiver.LatestJobStats` |
| `Engine.GetMainThreadInvokeLogSnapshot()` | `UdpProfilerReceiver.LatestMainThreadInvokes` |
| `Engine.EditorPreferences.Debug.*` toggles | Local booleans (no effect on engine) |

All the aggregation/cache logic (`ProfilerRootMethodAggregate`, `AggregatedChildNode`, `FpsDropSpikePathEntry`, worst-frame window, etc.) is copied over and operates on the deserialized packets.

---

## TODO Steps

### Phase 1 — Shared Protocol (XREngine.Data) ✅

- [x] **1.1** Create `XREngine.Data/Profiling/` directory.
- [x] **1.2** Define `ProfilerProtocol.cs` — message type enum, default port constant, max datagram size constant.
- [x] **1.3** Define all MemoryPack packet structs: `ProfilerFramePacket`, `RenderStatsPacket`, `ThreadAllocationsPacket`, `BvhMetricsPacket`, `JobSystemStatsPacket`, `MainThreadInvokesPacket`, `HeartbeatPacket`.
- [x] **1.4** Implement `UdpProfilerSender` — background thread, 30 Hz loop, reads engine stats via delegate-based collectors, serializes with MemoryPack, sends via `UdpClient`. Include enable/disable toggle and configurable port.
- [x] **1.5** Add fragmentation logic for oversized profiler frame packets (prune + split).
- [x] **1.6** Unit test: serialize → deserialize round-trip for each packet type (13 tests, all passing).

### Phase 2 — Engine Integration (XREngine) ✅

- [x] **2.1** Wire `UdpProfilerSender.Start()` into engine initialization, gated by `XRE_PROFILER_ENABLED` env var. Added `Engine.ProfilerSender.cs` with delegate-based collectors, called from `Engine.Lifecycle.Initialize()`.
- [x] **2.2** Expose a runtime toggle on `Engine.EditorPreferences.Debug` so the editor UI can enable/disable sending without restart. Added `EnableProfilerUdpSending` property + override.
- [x] **2.3** Add `UdpProfilerSender.Stop()` call to engine shutdown path (`Engine.Cleanup()`).
- [x] **2.4** Verify zero overhead when sender is disabled (no thread, no allocations, no socket). Confirmed structurally: `TryStartFromEnvironment()` no-ops when env var absent; delegates are null-safe.

### Phase 3 — Profiler App Skeleton (XREngine.Profiler)

- [x] **3.1** Create `XREngine.Profiler/XREngine.Profiler.csproj` — WinExe, `net10.0-windows7.0`, references `XREngine.Data`. NuGet: `Silk.NET.Windowing`, `Silk.NET.OpenGL`, `Silk.NET.OpenGL.Extensions.ImGui`, `Silk.NET.Input`, `MemoryPack`. ✅ Done previously.
- [x] **3.2** Add project to `XRENGINE.sln`. ✅ Done previously.
- [x] **3.3** Implement `Program.cs` — GLFW window creation (1440×900), OpenGL 3.3 core context, `ImGuiController` init, main loop. Supports port override via CLI arg or `XRE_PROFILER_PORT` env var.
- [x] **3.4** Implement `UdpProfilerReceiver.cs` — background thread with `UdpClient.Receive`, MemoryPack deserialize by `MessageType`, volatile latest-snapshot storage, heartbeat-based connection detection, cumulative counters.
- [x] **3.5** Implement `ProfilerImGuiApp.cs` — ImGui docking layout (DockBuilder P/Invoke), menu bar with connection indicator, 7 panels (Profiler Tree, Render Stats, Thread Allocations, BVH Metrics, Job System, Main Thread Invokes, Connection Info), dark theme. Also added `ImGuiDockBuilderNative.cs` (P/Invoke wrapper).

### Phase 4 — Profiler Panel Port

- [x] **4.1** Port `DrawProfilerTabContent()` — replace all `Engine.*` reads with `UdpProfilerReceiver.Latest*` reads. Implemented in `ProfilerPanelRenderer.ProcessLatestData()`.
- [x] **4.2** Port `ProcessProfilerSnapshotInline()` and all cache/aggregation helpers (`UpdateRootMethodCache`, `UpdateFpsDropSpikeLog`, `UpdateWorstFrameStatistics`, etc.). All helpers ported in `ProfilerPanelRenderer.cs`.
- [x] **4.3** Port `DrawAggregatedRootMethodHierarchy()` — the recursive tree table. Ported as `DrawAggregatedRootMethodHierarchy()` + `DrawAggregatedChildNode()`.
- [x] **4.4** Port root method graphs (`ImGui.PlotLines`). Ported in `DrawProfilerTreePanel()`.
- [x] **4.5** Port FPS drop spike table with sorting. Ported as dedicated `DrawFpsDropSpikesPanel()` with sortable 8-column table.
- [x] **4.6** Port thread allocations table. Ported in `DrawThreadAllocationsPanel()`.
- [x] **4.7** Port rendering statistics section (draw calls, VRAM, FBO, render matrix, octree). Ported in `DrawRenderStatsPanel()`.
- [x] **4.8** Port BVH GPU metrics section. Ported in `DrawBvhMetricsPanel()`.
- [x] **4.9** Port job system stats table. Ported in `DrawJobSystemPanel()`.
- [x] **4.10** Port main thread invokes table. Ported in `DrawMainThreadInvokesPanel()`.
- [x] **4.11** Add connection status indicator (heartbeat age, packets/sec, data rate). Ported in `DrawConnectionInfoPanel()`.

### Phase 5 — Polish & Tasks

- [x] **5.1** Add VS Code task `Start-Profiler-NoDebug` to `.vscode/tasks.json`. Also added `Start-Editor-WithProfiler-NoDebug` (launches editor with `XRE_PROFILER_ENABLED=1`).
- [x] **5.2** Add VS Code launch configuration for debugging the profiler. Added "Debug Profiler" and "Debug Profiler (with Editor)" configs to `launch.json`.
- [x] **5.3** Add `Build-Profiler` task. Added to `tasks.json`.
- [ ] **5.4** Test end-to-end: run editor with `XRE_PROFILER_ENABLED=1`, launch profiler app, verify all panels match.
- [ ] **5.5** Measure sender overhead — profile with and without sender enabled, confirm < 0.1 ms per frame on sender thread.
- [x] **5.6** Handle multiple engine instances — profiler shows "Known Sources" table in Connection Info panel when multiple heartbeats arrive from different PIDs. Warning displayed about interleaved data with recommendation to use separate ports.
- [x] **5.7** Add "reconnect" / "waiting for data" UI state when no heartbeat received for > 3 seconds. Centered overlay with animated dots, 3-state menu bar indicator (CONNECTED / WAITING / LOST), detailed status in Connection Info panel.

---

## Open Questions

1. **Multicast vs unicast?** — Unicast to `127.0.0.1` is simpler and sufficient for single-machine profiling. Multicast would allow multiple profiler instances but adds complexity. **Default: unicast.**
2. **Compression?** — MemoryPack is already very compact. LZ4 could be added later if packet sizes become problematic. **Default: no compression.**
3. **Bi-directional control?** — The profiler could send commands back (e.g., "enable render-matrix tracking"). This is a nice-to-have for Phase 6. **Default: one-way for now.**
4. **Cross-machine profiling?** — Changing the target IP from `127.0.0.1` to a LAN address would work out of the box. Leave port/IP configurable for this future use. **Default: localhost only.**
