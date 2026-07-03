# Hot-Path Memory Control

[Back to developer guides](../README.md)

This guide describes the runtime memory contract for engine code that runs every
frame or every high-rate network tick. The implementation is centered on
explicit GC profiles, named allocation scopes, reusable scratch/pool helpers,
and tests that verify zero or bounded allocations after warmup.

## Run-Mode GC Policy

`EngineMemoryPolicy` defines the named profiles used by executable entry points
and generated published-game launchers:

- `EditorInteractive`
- `DesktopRuntime`
- `VRLowLatency`
- `HeadlessServer`
- `Benchmark`
- `PublishedDefault`

Executable project files declare the compile/runtimeconfig side of GC behavior:
editor and VR client use workstation/background GC, the server uses server GC,
and generated launcher projects use workstation/background GC by default.
Runtime latency mode is selected by `Engine.ConfigureMemoryPolicy(...)` and
`Engine.EnsureMemoryPolicyConfigured(...)` so launch mode can still choose
editor, desktop, VR, server, benchmark, or published-game behavior.

The startup diagnostics log the selected XRENGINE memory profile, GC latency
mode, server/workstation mode, and relevant GC environment/configuration values.
Use these overrides only for diagnostics or controlled benchmarks:

- `XRE_MEMORY_PROFILE`
- `XRE_MEMORY_DIAGNOSTICS`
- `XRE_GC_LATENCY_MODE`
- `XRE_DISABLE_MAINTENANCE_GC`
- `XRE_BENCHMARK_NOGC_REGION`
- `XRE_BENCHMARK_NOGC_BYTES`
- `DOTNET_gcConcurrent`

## Hot-Path Rules

Render submission, collect/swap, visible collection, fixed update, variable
update, VR input, high-rate network replication, ECS range systems, animation/IK
range solves, and GPU-upload preparation should allocate zero managed bytes
after warmup unless the code declares a small explicit budget and documents why.

Avoid the following in those paths:

- `new T[]`, `new List<T>()`, or ad hoc temporary collections.
- LINQ materialization such as `ToArray`, `ToList`, `Select`, or `Where`.
- Captured lambdas or delegates created per frame.
- Boxing, interpolated strings, or string concatenation in per-frame diagnostics.
- `foreach` over enumerators that are not known struct enumerators.
- `GC.Collect()` or `NoGCRegion` as a normal frame-loop mechanism.

Warmup allocation is allowed when capacity growth is visible and prewarmable.
Editor/tooling code can remain readable unless it runs inside a measured hot
path.

## Temporary Storage Choices

Use `stackalloc` for very small, fixed-size, synchronous spans that never escape
the current method.

Use `FrameScratchAllocator` for unmanaged frame-local temporary storage. It
supports alignment, span access, reset at phase boundaries, and debug lifetime
checks through lease generation. Use `FrameScratchRing` when render handoff data
must survive for a bounded number of later frames.

Use `PooledArray.Rent<T>()` for unavoidable managed arrays. Return the wrapper
with `Dispose`; request clear-on-return when references or sensitive data are
stored. Pool stats expose rent, return, miss, and discarded-buffer counts.

Use persistent preallocated arrays or hot collections for stable per-system
state: `HotDenseList<T>`, `HotBitSet`, `HotSparseSet`, and fixed-capacity rings.
These types keep capacity explicit and expose overflow/growth counters.

Use unmanaged/native memory only when ownership and release are deterministic.
If a managed object owns large unmanaged memory and cleanup may depend on
finalization, pair `NativeMemoryPressureTracker.Add(bytes)` with disposal so
`GC.AddMemoryPressure` and `GC.RemoveMemoryPressure` stay balanced.

## Pools And Collections

`ResourcePool<T>` remains the general-purpose thread-safe pool. Hot paths should
use `HotPathObjectPool<T>` when take/release must avoid `ConcurrentBag.Count`
and similar overhead. Prewarm hot pools before measurement, choose a fixed or
bounded retained capacity, and watch overflow counters.

Packet/render handoff queues should use fixed-capacity rings with explicit drop
or degrade policy. High-rate replication uses `RealtimePacketSendRing` and
`PersistentReceiveSlabPool` to avoid per-message `byte[]` allocation.

## Allocation Telemetry

`Engine.ThreadAllocationTracker` records coarse thread allocation deltas and
named scopes. Scope categories include runtime systems, ECS systems, render
passes, render submission, network codecs, VR input, animation/IK, GPU upload
preparation, editor UI, and diagnostics.

Use `BeginScope(name, category, budgetBytes)` for debug-only attribution around
selected subsystems. The tracker keeps last, average, max, sample count, and
over-budget count; over-budget logging is throttled. The profiler panel and UDP
profiler packet include scope summaries without allocating in the steady-state
send path.

The engine currently places scopes around frame update, fixed update, visible
collection, render, swap buffers, render/app/physics/update job dispatch, and
OpenVR/OpenXR input/render paths.

## ECS And Replication Contracts

Runtime ECS hot stores use dense component storage, dirty bitsets, dirty ranges,
capacity-growth counters, and range partitioning helpers. Systems receive
scratch storage through `RuntimeSystemContext`; `RuntimeSystemRunner` measures
per-system allocation bytes with `GC.GetAllocatedBytesForCurrentThread()`.

High-rate avatar replication has span-based fixed-pose baseline/delta codecs,
batch packet writers, read cursors, send rings, and receive slabs. Generic
editor/networking components may allocate; production avatar/ECS replication
paths should not allocate after warmup.

## Maintenance GC

Induced collections must go through
`Engine.RequestMaintenanceGarbageCollection(...)`. Valid reasons include scene
or world unload, asset import/cook completion, exiting play mode, benchmark
setup/teardown, dynamic assembly unload, and explicit editor idle requests.

The maintenance API records reason, detail, generation, LOH compaction request,
finalizer wait, and before/after heap size. Requests from render-frame dispatch
are rejected or warned instead of silently collecting inside hot paths.

Benchmark `NoGCRegion` is optional and disabled by default. Enable it only with
`XRE_BENCHMARK_NOGC_REGION` and an explicit byte budget. Startup diagnostics
report success or failure; normal editor and VR runs must not rely on it.

## Validation

Targeted tests live in `XREngine.UnitTests/Core/RuntimeMemoryControlTests.cs`
and cover scratch lifetimes, pooled arrays, hot-path pools, ECS allocation
contracts, fixed humanoid pose codecs, batch packet writer/cursor paths,
replication rings/slabs, and memory-pressure pairing. Profiler packet coverage
lives in `XREngine.UnitTests/Core/ProfilerProtocolTests.cs`.

For new hot-path code, add a warmup pass, then assert measured allocation deltas
and per-system over-budget counters in the steady-state pass. Keep validation
logs or profiler captures under `Build/_AgentValidation/<run>/`.
