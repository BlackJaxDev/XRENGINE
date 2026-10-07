# OpenXR Monado CI And Hardware Follow-ups

Last Updated: 2026-06-24
Owner: XR / Rendering / Testing
Status: Active

Follow-up tracker for work intentionally left out of the local OpenXR Monado
smoke implementation. The local runner and scene-only lane are described in the
[OpenXR runtime guide](../../../../developer-guides/vr/openxr-runtime.md), the
[Unit Testing World guide](../../../../developer-guides/testing/unit-testing-world.md),
and the [design](../../../design/VR/openxr-monado-testing-pipeline.md). Runtime
checks are in [OpenXR Validation](../../../testing/xr/openxr-validation.md).

## CI Promotion

- [ ] Decide whether CI uses a Windows self-hosted runner.
- [ ] Decide whether Monado artifacts become repo-managed dependencies.
- [ ] Get owner approval before adding runner infrastructure.
- [ ] If a Monado binary/artifact becomes repo-managed, pin the version or
  commit and update [docs/DEPENDENCIES.md](../../../../DEPENDENCIES.md) and
  generated license files with `pwsh Tools/Generate-Dependencies.ps1`.
- [ ] Do not make hosted CI download mutable Monado binaries at test time.
- [ ] Add CI only after the bounded smoke exit and summary assertions are
  reproducible on the chosen runner.
- [ ] Upload `Build/Logs`, the smoke summary, and runner diagnostics as
  artifacts.
- [ ] Add a longer nightly lane only after the short smoke lane is stable.

## Hardware Matrix

- [ ] Re-run SteamVR OpenXR/OpenGL.
- [ ] Re-run SteamVR OpenXR/Vulkan.
- [ ] Re-run Oculus/Meta OpenXR/OpenGL.
- [ ] Re-run Oculus/Meta OpenXR/Vulkan.
- [ ] Confirm pacing defaults, session-state transitions, tracking-loss policy,
  headset-off behavior, focus/visibility transitions, and runtime restart on
  physical or vendor-managed runtimes.
- [ ] Keep runtime-specific behavior gated by extension support, capability
  probing, spec-visible return/result, or an explicit debug setting.
- [ ] Do not gate production behavior on runtime name except for diagnostics.

## Deterministic Pose And Fault Injection

- [ ] Evaluate `XR_EXT_conformance_automation` support in the selected Monado
  build and relevant hardware runtimes.
- [ ] Evaluate a development-only OpenXR API layer for call tracing.
- [ ] Evaluate a development-only OpenXR API layer for fault injection, such as
  session-loss, invalid view-state flags, swapchain errors, and runtime restart.
- [ ] Keep input and dynamic pose assertions out of Lane 2 until deterministic
  automation exists.
- [ ] Revisit an XREngine-owned mock runtime only if Monado plus API-layer
  automation cannot cover required cases.

## Open Code Items Moved From openxr-monado-testing-pipeline-todo.md

- [ ] If the owner selects persistent smoke settings (see Decisions Needed), add
  `OpenXrExpectedRuntimeName`, `OpenXrRequireMockRuntime`, and
  `OpenXrSmokeFrameCount` beside the existing `VR.OpenXrRuntimeJson`.
  `UnitTestingVrSettings`, `UnitTestingWorldSettingsStore`, the smoke runner
  in `XREngine.Editor/Program.OpenXrSmokeRunController.cs`. Use `SetField(...)`
  if the owner type derives from `XRBase`. Done when: the settings appear in the
  regenerated schema, an existing `XR_RUNTIME_JSON` or `XRE_SMOKE_FRAMES`
  process value still wins, and `OpenXrTimingPipelineContractTests` covers the
  precedence.
- [ ] Remove the formatted-logging allocation candidates from OpenXR hot paths.
  `XREngine.Runtime.XR.OpenXR`. Done when:
  `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`
  passes with no recorded baseline exceptions.

## Decisions Needed

- [ ] Which Monado Windows build/tag/commit is the first supported Lane 2
  baseline?
- [ ] Does the chosen Monado build require `monado-service.exe` on Windows?
- [ ] Should persistent OpenXR smoke settings be added, or is process
  environment enough for v1?
- [ ] Which CI ownership model is acceptable: local-only, self-hosted Windows,
  or pinned internal artifact?
- [ ] Does the target SteamVR hardware expose `XR_EXT_hand_tracking`, or only
  controller profile inputs that can be synthesized into finger curls?
- [ ] Which Valve Index and Vive controller component paths are first-class
  defaults for the gameplay action set?
- [ ] Do OpenXR action binding overrides live in engine settings, in generated
  files, or only in the runtime binding UI for v1?
- [ ] Are tracker persistent paths stored in user calibration data, or does v1
  use role paths only?
- [ ] What is the minimum hardware matrix before OpenXR becomes the default
  SteamVR path and OpenVR can retire?

## Related

- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Validation](../../../testing/xr/openxr-validation.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
