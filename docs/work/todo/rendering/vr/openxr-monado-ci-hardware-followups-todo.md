# OpenXR Monado CI And Hardware Follow-ups TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR Runtime](../../../../developer-guides/vr/openxr-runtime.md#no-hmd-test-lanes), [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md), [OpenXR SteamVR Hardware Validation](../../../testing/xr/openxr-steamvr-hardware-validation.md)

## Current State

The repository has Monado no-HMD runner scripts, scene-only VR smoke, SteamVR smoke tooling, and Unit Testing World OpenXR tasks. The local Monado lane is not yet promoted to CI. Persistent smoke settings, allocation cleanup, and the minimum physical hardware matrix remain open.

## Open Code Items

### CI promotion

- [ ] Design a Windows CI lane for Monado. Update `.github` workflow files only after owner approval and after the local smoke exit and summary assertions are reproducible on the chosen runner. Done when: the lane uses a self-hosted or approved runner model and does not download mutable Monado binaries at test time.
- [ ] Add pinned Monado artifact management if the owner selects repo-managed binaries. Update `docs/DEPENDENCIES.md` and generated license files with `pwsh Tools/Generate-Dependencies.ps1`. Done when: the version or commit is pinned and license output is regenerated.
- [ ] Add smoke-summary, log, and runner-diagnostic artifact publication for the CI lane. Done when: a failed CI run retains enough non-sensitive evidence to diagnose startup, summary, and teardown failures.
- [ ] Add a longer nightly OpenXR lane after the short smoke lane is stable. Done when: the nightly lane is separate from required PR validation and reports runtime, backend, frame count, and failure summary.

### Runtime settings and smoke harness

- [ ] Add persistent smoke settings if the owner selects that option. Update `UnitTestingVrSettings`, `UnitTestingWorldSettingsStore`, and `XREngine.Editor/Program.OpenXrSmokeRunController.cs` for `OpenXrExpectedRuntimeName`, `OpenXrRequireMockRuntime`, and `OpenXrSmokeFrameCount`. Use `SetField(...)` if the owner type derives from `XRBase`. Done when: the settings appear in the regenerated schema, process `XR_RUNTIME_JSON` or `XRE_SMOKE_FRAMES` still wins, and `OpenXrTimingPipelineContractTests` covers precedence.
- [ ] Remove formatted-logging allocation candidates from OpenXR hot paths. Update `XREngine.Runtime.XR.OpenXR`. Done when: `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations` passes with no recorded baseline exceptions.

### Deterministic automation

- [ ] Evaluate a development-only OpenXR API layer for call tracing. Done when: the layer can be enabled without changing production runtime behavior and without adding per-frame allocations when disabled.
- [ ] Evaluate a development-only OpenXR API layer for fault injection. Cover session loss, invalid view-state flags, swapchain errors, and runtime restart. Done when: fault injection can drive deterministic smoke failures with stable summary fields.
- [ ] Add an XREngine-owned mock runtime only if Monado plus API-layer automation cannot cover required deterministic cases. Done when: the owner approves the added maintenance cost and the mock runtime remains outside production fallback paths.

## Decisions Needed

- [ ] Which Monado Windows build, tag, or commit is the first supported no-HMD baseline? Owner: Rendering / XR.
- [ ] Does the chosen Monado build require `monado-service.exe` on Windows? Owner: Rendering / XR.
- [ ] Should persistent OpenXR smoke settings be added, or is process environment enough for v1? Owner: Rendering / XR.
- [ ] Which CI ownership model is acceptable: local-only, self-hosted Windows, or pinned internal artifact? Owner: Owner.
- [ ] Does the target SteamVR hardware expose `XR_EXT_hand_tracking`, or only controller profile inputs that can be synthesized into finger curls? Owner: XR.
- [ ] Which Valve Index and Vive controller component paths are first-class defaults for the gameplay action set? Owner: XR / Input.
- [ ] Do OpenXR action binding overrides live in engine settings, generated files, or only in the runtime binding UI for v1? Owner: XR / Input.
- [ ] Are tracker persistent paths stored in user calibration data, or does v1 use role paths only? Owner: XR / Avatar.
- [ ] What minimum hardware matrix must pass before OpenXR becomes the default SteamVR path and OpenVR can retire? Owner: Owner / XR.

## Out Of Scope

- Making hosted CI download mutable Monado binaries at test time.
- Gating production behavior on runtime name except for diagnostics.
- Input and dynamic pose assertions in the Monado lane before deterministic automation exists.
