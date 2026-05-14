# OpenXR Timing And Pipeline — Tests And Diagnostics

Last updated: 2026-05-13

Tracks the remaining test, allocation-audit, and hardware-validation work for the OpenXR timing pipeline. The production-code todo (Phases 0-8) is complete and has been removed; this file is what is left.

Sibling future-work tracker: [openxr-future-work-todo.md](../rendering/vr/openxr-future-work-todo.md).

## Contract Tests

Existing coverage lives in `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs` (9 tests, all passing). It covers Phase 1-5 invariants plus the Phase 7 pacing-mode wiring, ping-pong hand-off, stop-on-teardown, streak-gated tracking-loss warning, and padded-frustum policy marker.

Still missing:

- [ ] Runtime allocation-sentinel test (not source-level): drive `HandleLocatedViewState` under sustained tracking loss with `OpenXrDebugLifecycle` off and assert zero managed allocations across N frames after warmup. Needs a small GC-allocations harness; current contract tests only inspect source text.
- [ ] Runtime pacing-thread invariant test: with `OpenXrRenderPacingMode == DedicatedThread` and a fake `IOpenXrApi`-style surface, assert (a) exactly one `xrBeginFrame` outstanding per `xrEndFrame`, (b) `xrWaitFrame` never executes on the simulated render thread, (c) steady-state prep iteration allocates nothing.
- [ ] Frustum-expansion stat behavior test: with policy != `PaddedFrustum`, assert `VrXrCollectFrustumExpansionDegrees` is reset to 0 each frame (currently covered only by source-text inspection).

## Allocation Audits

- [ ] After landing any further OpenXR layer changes, re-run `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations` and clear any new flags or add a justification comment.

## Manual Hardware Validation Matrix

Required before declaring `OpenXrRenderPacingMode == DedicatedThread` production-default.

| Runtime | Backend | Mirror + ImGui | Headset off / session-stopping | Loss-pending recovery |
|---------|---------|----------------|---------------------------------|------------------------|
| SteamVR / OpenXR | OpenGL | [ ] | [ ] | [ ] |
| SteamVR / OpenXR | Vulkan | [ ] | [ ] | [ ] |
| Oculus / OpenXR | OpenGL | [ ] | [ ] | [ ] |
| Oculus / OpenXR | Vulkan | [ ] | [ ] | [ ] |

Per-row checks:

- [ ] Editor with desktop mirror + headset active: desktop ImGui FPS exceeds HMD refresh after enabling `DedicatedThread`, within 10% of non-VR baseline.
- [ ] `VrXrPacingHandoffStalls` does not grow unboundedly under steady-state load.
- [ ] `VrXrPacingThreadIdleTimeMs` ≈ (frame interval - active prep time) in steady state (proves pacing thread waits, not spins).
- [ ] Cover the HMD sensors for ~10 s: exactly one tracking-loss warning per streak, plus one `FreezeLastValid`→identity warning when no cached views exist.
- [ ] Session-loss / runtime restart: pacing thread shuts down cleanly (no `XR Pacing` thread surviving in profiler after session end).

## Baseline Diagnostics

- [ ] Capture a baseline run with `OpenXrDebugLifecycle=true` on a reference scene; archive lifecycle logs under `Build/Logs/`.
- [ ] Re-run baseline with `DedicatedThread` enabled and attach updated lifecycle logs + stats to the validation PR.

## Phase 4 Input Audit (deferred from main todo)

- [ ] Audit input listeners on action edges for sensitivity to the `OpenXrActionSyncPolicy.PredictedOnly` default (Phase 4 changed behavior from two-sync to one-sync per frame). Regression sweep on existing bindings; flip to `PredictedAndLate` per-binding if any consumer regresses.

## Related

- [OpenXR VR Rendering (architecture)](../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Future Work TODO](../rendering/vr/openxr-future-work-todo.md)
