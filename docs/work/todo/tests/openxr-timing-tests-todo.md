# OpenXR Timing Tests TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md#frame-lifecycle-and-pose-timing), [OpenXR Runtime](../../../developer-guides/vr/openxr-runtime.md#no-hmd-test-lanes)
Validation: [OpenXR Validation](../../testing/xr/openxr-validation.md)

## Current State

`XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs` exists and covers timing pipeline invariants, pacing-mode wiring, handoff, teardown, tracking-loss warning policy, and padded-frustum policy markers. Several checks still use source-text inspection. Runtime allocation and pacing-thread invariants need executable test coverage.

## Open Code Items

### Runtime contract tests

- [ ] Add a runtime allocation-sentinel test for sustained tracking loss. Update `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs` or a new focused test file. Done when: the test drives `HandleLocatedViewState` with `OpenXrDebugLifecycle` off and asserts zero managed allocations across warmed steady-state frames.
- [ ] Add a runtime pacing-thread invariant test. Update OpenXR timing test fixtures with a fake `IOpenXrRuntime` or equivalent seam. Done when: the test asserts one outstanding `xrBeginFrame` per `xrEndFrame`, no `xrWaitFrame` call on the simulated render thread, and zero steady-state allocation in the preparation loop.
- [ ] Replace source-text frustum-expansion coverage with behavior coverage. Update OpenXR timing tests. Done when: a policy other than `PaddedFrustum` resets `VrXrCollectFrustumExpansionDegrees` to `0` each frame in an executable test.

### Test boundaries

- [ ] Keep Monado-independent timing contract tests free of runtime process orchestration. Update test helpers if needed. Done when: timing regressions can fail before Monado, SteamVR, or editor smoke scripts start.
- [ ] Audit input listeners on action edges for sensitivity to the `OpenXrActionSyncPolicy.PredictedOnly` default after runtime validation completes. Update tests for any binding that must use `PredictedAndLate`. Done when: each affected binding has an explicit policy and a deterministic regression test.

### Allocation audit tooling

- [ ] Keep the OpenXR hot-path allocation audit current after OpenXR layer changes. Update `Tools/Reports/Find-NewAllocations.ps1` or its OpenXR patterns when code moves. Done when: the audit still covers render, collect-visible, pacing, swapchain, and submission hot paths after layout changes.

## Decisions Needed

- [ ] What fake or adapter surface should timing tests use for runtime pacing without starting a real OpenXR runtime? Owner: Testing / XR.

## Out Of Scope

- Running hardware or editor smoke validation. Those checks live in [OpenXR Validation](../../testing/xr/openxr-validation.md).
- Promoting Monado CI. That work lives in [OpenXR Monado CI And Hardware Follow-ups](../rendering/vr/openxr-monado-ci-hardware-followups-todo.md).
