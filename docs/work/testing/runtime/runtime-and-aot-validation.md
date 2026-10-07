# Runtime And AOT Validation

Scope: runtime, profiler, allocation, and hardware checks for runtime memory control, GC policy, and hot-path allocation budgets.

Architecture: [Hot-Path Memory Control](../../../developer-guides/runtime/hot-path-memory.md), [Remote Profiler](../../../developer-guides/diagnostics/profiler.md)

Code todos: none open for GC and hot-path memory control.

Evidence: [GC And Hot-Path Memory Control Closeout - 2026-07-02](../../runtime/gc-hot-path-memory-control-2026-07-02.md). Capture template: [Memory-Control Investigation Template](../memory-control-investigation-template.md).

## Setup

- Tasks: `Start-Editor-WithProfiler-NoDebug`, `Start-Profiler-NoDebug`, `Report-NewAllocations`.
- Unit tests: `XREngine.UnitTests/Core/RuntimeMemoryControlTests.cs`, `XREngine.UnitTests/Core/ProfilerProtocolTests.cs`.
- Record each run with the memory-control investigation template.

## Imported Checks

### From gc-and-hot-path-memory-control-todo.md

- [ ] Capture a live editor allocation baseline with the profiler: default ImGui editor startup, Unit Testing World steady state, representative camera movement, and one model import or asset load. Expected: allocation totals per thread (render, collect/swap, update, fixed update, profiler sender) are recorded.
- [ ] Rank render and VR allocation scopes by bytes per frame and frequency from a live profiler session. Expected: each hot-path source has a classification (hot-path blocker, warmup-only, editor-only, acceptable background).
- [ ] Capture a VR allocation baseline on hardware: OpenVR, OpenXR with SteamVR, and a no-HMD OpenXR Monado smoke. Expected: steady-state VR update and render scopes stay within their budgets.
- [ ] Repeat the hardware VR profiler capture before any change to VR memory or GC policy defaults.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
