# GC And Hot-Path Memory Control Closeout - 2026-07-02

[Back to work docs](../README.md)

## Summary

The runtime now has explicit memory profiles, startup GC diagnostics, maintenance
GC routing, named allocation scopes, scratch/pool/hot-collection helpers, ECS
allocation counters, and allocation-free avatar replication codec primitives.
The implementation stayed on the current branch because the user explicitly
requested "Don't branch."

## Baseline And Allocation Map

The available baseline for this pass is a code-level and synthetic hot-path
baseline rather than a hardware VR capture. No SteamVR/OpenVR hardware session
was available in this run. The VR lane was validated by compile coverage and
instrumentation of the OpenVR/OpenXR update/render paths; hardware profiler
capture should still be repeated on a local headset before changing VR policy
defaults again.

| Source | Baseline/Current Evidence | Classification |
|---|---:|---|
| Synthetic ECS avatar range systems | 0 managed bytes after warmup across 32 steady-state passes | Hot-path verified |
| Fixed humanoid pose baseline/delta encode/decode | 0 managed bytes after warmup | Hot-path verified |
| Mixed avatar packet writer/cursor | 0 managed bytes after warmup | Hot-path verified |
| Realtime send ring and receive slab pool | 0 managed bytes after warmup | Hot-path verified |
| Profiler sender serialization | Reworked to reusable fixed buffer writer | Hot-path improved |
| Editor/VR render/update scopes | Instrumented by named allocation scopes | Requires scene profiler capture for bytes/frame ranking |
| Native memory pressure pairing | Add/remove covered by unit test | Lifecycle verified |

Hot-path allocation sources found during implementation were concentrated in
test warmup and profiler UDP serialization. The steady-state synthetic ECS and
replication paths now assert zero allocations; profiler packets avoid per-send
payload array creation.

## Implemented

- Added `EngineMemoryPolicy` profiles for editor, desktop runtime, VR,
  headless server, benchmark, and published default modes.
- Added executable-specific GC settings and startup profile selection for the
  editor, server, VR client, and generated published-game launchers.
- Added memory-policy environment overrides and startup GC diagnostics.
- Added `Engine.RequestMaintenanceGarbageCollection(...)` and routed dynamic
  assembly unload/build unload GC calls through it.
- Added optional benchmark `NoGCRegion` support with explicit byte budget and
  diagnostics.
- Extended allocation telemetry with named scopes, rolling stats, budgets, and
  throttled over-budget logging.
- Added allocation scope data to the profiler UDP packet and profiler UI.
- Reworked UDP profiler serialization to use a reusable fixed buffer writer.
- Added frame scratch, scratch rings, pooled arrays, hot object pools, hot dense
  lists, bitsets, sparse sets, and fixed rings.
- Added ECS dense stores, dirty ranges, range partitioning, lookup helpers,
  capacity growth counters, and per-system allocation stats.
- Added fixed span-based humanoid pose codecs, batch writer/cursor helpers,
  realtime send rings, and persistent receive slabs.
- Added native memory pressure tracking with deterministic add/remove pairing.

## Validation

Run root:

`Build/_AgentValidation/20260702-172534-gc-hot-path-memory`

Commands run:

```powershell
dotnet build .\XREngine.Data\XREngine.Data.csproj
dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj
dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~RuntimeMemoryControlTests|FullyQualifiedName~ProfilerProtocolTests.ThreadAllocationsPacket_RoundTrip"
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet build .\XREngine.Server\XREngine.Server.csproj --no-restore
dotnet build .\XREngine.VRClient\XREngine.VRClient.csproj --no-restore
```

Results:

- `XREngine.Data` build passed.
- `XREngine.Runtime.Core` build passed.
- `XREngine.UnitTests` build passed.
- Focused runtime memory/profiler protocol tests passed: 10 passed, 0 failed.
- `XREngine.Editor` build passed.
- `XREngine.Server` build passed.
- `XREngine.VRClient` build passed.

The build/test logs are under the run root's `logs/` folder. Builds report
existing `Magick.NET-Q16-HDRI-AnyCPU` package vulnerability warnings
(`NU1902`/`NU1903`); those are unrelated to this memory-control work.

## Remaining Hardware Follow-Up

- Capture a real editor steady-state profiler session with a representative
  model import and camera movement.
- Capture an OpenVR hardware run with allocation scopes visible in the profiler
  panel.
- Capture a no-HMD OpenXR or Monado smoke when that lane is active locally.
- Rank live render/VR scope allocation by bytes/frame and frequency from those
  captures.

These are validation follow-ups, not missing implementation items for this
todo. The code paths are instrumented and the synthetic hot-path allocation
tests are in place.
