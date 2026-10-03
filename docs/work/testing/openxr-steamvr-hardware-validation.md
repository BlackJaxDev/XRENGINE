# OpenXR SteamVR Hardware Validation

Last Updated: 2026-10-01

This report tracks the SteamVR OpenXR hardware matrix for the OpenVR parity work. It records the runnable validation lane added in this pass and the evidence that must be captured on a machine with SteamVR hardware attached.

## Full-body calibration follow-up, September 26, 2026

The current uncommitted implementation is based on `9fee4b983efda6f3f79ac107eba2137a5ab8c5fa`. The full-body calibration [validation record](../progress/avatar/openxr-full-body-calibration-spectator-implementation.md) records its separate synthetic and physical results.

The machine reports an NVIDIA GeForce RTX 3090 with driver `32.0.16.1714`. The runtime in the successful September 26 probe was SteamVR/OpenXR 2.17.10 (Steam application build 25330290). Starting SteamVR and probing OpenXR then succeeded in creating an instance, reading runtime properties, and obtaining the head-mounted-display system. An early OpenVR inventory reported a Beyond headset, one Tundra tracker, four tracking references, and no controllers. The inventory changed while SteamVR remained active: a subsequent OpenVR Background probe found a Bigscreen Beyond HMD, Valve Knuckles left/right controllers, and three Tundra Labs Tundra Trackers, all connected with valid `Running_OK` poses. An intervening probe had found one tracker temporarily `Calibrating_OutOfRange`; validity is therefore time dependent. Earlier `Init_HmdNotFound` was transient during SteamVR startup. These inventories are historical observations, not the current device availability.

The loader advertises `XR_HTCX_vive_tracker_interaction` revision 3, `XR_KHR_opengl_enable` revision 12, and `XR_KHR_vulkan_enable2` revision 4. The first loader enumeration attempt crashed its PowerShell process because the SteamVR smoke tool declared the native `XrVersion` field as 32 bits, under-allocating API-layer property elements. The declaration now uses 64 bits and validates the 544-byte Windows x64 layout; the repeated native preflight completed successfully. This fixes the probe without substituting a pose provider.

The rebuilt OpenGL editor reached `SessionRunning` / `Focused` in lifecycle epoch 1 without teardown or frame-submit errors. At the `hardware-openxr-running.json` snapshot it had submitted 416 frames, acquired/published/released both 2688 × 2688 eye swapchains 416 times, cached predicted and late HMD views and controller grip/aim poses, and reported active joints for both hands. It missed 411 of 416 frame deadlines. This establishes headset/controller pose and frame submission, but not controller gestures, haptics, or performance acceptance. It reported no known tracker paths or tracker pose.

An independent process loaded SteamVR's OpenXR loader, created an instance with `XR_HTCX_vive_tracker_interaction` revision 3, resolved `xrEnumerateViveTrackerPathsHTCX`, and called its instance-level count query twice. Both calls returned `XR_SUCCESS` with count zero; the instance and function lookup also succeeded. Its Windows x64 native structures were checked against the local OpenXR header (application info 272 bytes, instance create info 328 bytes, tracker paths 32 bytes). This reproduces the engine's empty tracker inventory outside the engine. The OpenVR inventories prove physically connected, currently tracked generic devices existed during the probe period, but do not prove SteamVR exposes them through HTCX OpenXR. Khronos specifies that enumeration includes connected VIVE trackers and that an unassigned role may be `XR_NULL_PATH`; no SteamVR role assignment was changed to force a result.

Evidence is in `Build/_AgentValidation/20260926-190000-vr-fullbody/reports/`: `hardware-presence.json`, `hardware-inventory-ready.json`, `openxr-system-probe.txt`, `openxr-loader-preflight-resume.json`, `openxr-extension-revisions.json`, `hardware-openxr-running.json`, `hardware-running-view-state.json`, `htcx-native-tracker-enumeration.json`, `htcx-native-tracker-enumeration-repeat.json`, `openvr-tracker-inventory.json`, and `openvr-tracker-inventory-named.json`. Physical tracker streaming, controller gestures/haptics, late tracker connection, and a passing headset frame-timing run remain open.

Short, single-validation-editor performance samples used the same OpenGL runtime and avatar scene. The first-person sample submitted 25 frames and missed all 25 deadlines; median render time was 40.0012 ms and p95 was 317.3575 ms. The spectator sample submitted 75 frames and missed all 75 deadlines; median render time was 56.0683 ms and p95 was 76.3531 ms. These windows include the multi-rig imported asset, editor diagnostics, and concurrent GPU activity. Whole-process managed allocation rates were 30.0 MB/s and 90.6 MB/s respectively across editor threads and MCP sampling; the OpenXR summary's constant-zero allocation field is not a measurement. The samples fail timing acceptance and do not isolate spectator-only cost. Raw data is in `performance-first-person-single-validation-session.json` and `performance-spectator-single-validation-session.json`.

An explicitly selected alternative tracker transport is a possible future compatibility option, not an implemented fallback. It would need a visible per-session provider choice, stable physical identity matching, fresh pose validity and timestamps, and a defined way to combine its tracker samples with the OpenXR headset/controller calibration snapshot before capture could be enabled. The current OpenVR.NET pose facade does not expose a publication timestamp or frame identifier, so merely reading its matrices would not satisfy the capture-coherence requirement. The current mode remains OpenXR-only and reports undiscovered trackers honestly.

## Later software validation

Body calibration and spectator acceptance use the additional
[integrated behavior procedure](avatar/openxr-calibration-spectator-validation.md).
The [October 1 Windows validation](../investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md)
records software/runtime checks and rendering blockers without connected XR
devices; it does not change the pending hardware rows below.

## Current Validation Status

The original parity/tooling pass validated source integration, tooling syntax, VS Code orchestration JSON, and the targeted editor build. The hardware follow-up above supersedes its earlier no-device status while preserving the original command checks below.

Validated in this pass:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj -c Debug --filter "FullyQualifiedName=XREngine.UnitTests.Rendering.OpenXrSteamVrParityToolingContractTests.SteamVrSmokeTooling_UsesOpenXrModeAndRuntimeDiagnostics"`
- PowerShell parser check for `Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1`
- JSON parse check for `.vscode/tasks.json`
- JSON parse check for `.vscode/launch.json`

## Hardware Inventory

Fill these fields on the first hardware run:

| Field | Value |
|---|---|
| Headset model | Bigscreen Beyond, tracked in the September 26 Focused run; out of range at the latest retry |
| Controller models | Valve Knuckles left/right, connected and pose-valid in the September 26 inventory; absent at the latest retry |
| VIVE tracker count | Three Tundra Labs Tundra Trackers, connected and pose-valid in the September 26 OpenVR probe; HTCX OpenXR enumeration count zero |
| VIVE tracker roles | Not established; independent OpenXR enumeration yielded no paths |
| Expected hand/finger data | Both OpenXR hand-joint active flags observed; joint articulation and expected values not validated |
| SteamVR version | 2.17.10, application build 25330290 |
| OpenXR runtime manifest | Active SteamVR manifest; process-scoped path recorded in the disposable report |

## Smoke Commands

Primary Vulkan hardware lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
```

OpenGL diagnostic lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer OpenGL
```

Explicit SteamVR manifest lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "<steamvr-runtime-manifest>" -Renderer Vulkan
```

## Matrix

| Scenario | Status | Evidence |
|---|---|---|
| SteamVR OpenXR, OpenGL, headset only | Session/frame submission observed | Focused epoch 1, 416 submitted frames, predicted/late HMD views; 411 deadlines missed. |
| SteamVR OpenXR, OpenGL, headset plus controllers | Pose availability observed; gestures pending | Both grip and aim pose caches available; two controllers also tracked in OpenVR inventory. |
| SteamVR OpenXR, OpenGL, headset plus controllers plus VIVE trackers | Blocked by runtime exposure | Engine and independent native HTCX queries both report zero paths while the latest OpenVR query finds three pose-valid Tundra Trackers. |
| SteamVR OpenXR, OpenGL, hand/finger data where supported | Joint activity observed; behavior pending | Both hand-joint active flags set; finger articulation not visually or numerically validated. |
| SteamVR OpenXR, Vulkan, headset only | Pending hardware run | Capture smoke summary and headset/mirror visual result. |
| SteamVR OpenXR, Vulkan, headset plus controllers | Pending hardware run | Capture gameplay action and haptic result. |
| SteamVR OpenXR, Vulkan, headset plus controllers plus VIVE trackers | Pending hardware run | Capture tracker reconnect/role reassignment behavior. |
| SteamVR OpenXR, Vulkan, hand/finger data where supported | Pending hardware run | Capture real joints or controller-derived fallback diagnostics. |
| Wrong or missing `XR_RUNTIME_JSON` | Tooling implemented | `Run-OpenXrSteamVrSmoke.ps1` validates selected manifests and warns on non-SteamVR runtime kind. |
| SteamVR not running | Tooling implemented | Smoke runner records process state and attempts `vrstartup.exe` or Steam URI before launch. |
| Headset removed, dashboard opened, runtime restarted, session lost | Pending hardware run | Capture session-state transitions in smoke summary and engine logs. |
| No new OpenXR hot-path allocations | Tooling implemented | Smoke runner invokes `Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations` unless skipped. |
| Monado no-HMD smoke | Pending follow-up run | Run `Test-OpenXR-Monado-Smoke` after hardware lane changes. |
| Existing OpenVR path | Inventory/pose query observed; gameplay baseline pending | Latest Background query found tracked HMD, two controllers, and three valid Tundra Tracker poses. |

## Expected Summary Fields

The OpenXR smoke summary includes runtime/system name, renderer backend, view count, swapchain dimensions, submitted frame count, view validity, controller grip/aim pose availability, tracker pose availability, missed deadline count, and session-state transitions.

## Evidence To Attach Per Pass

Record these paths after each run:

- SteamVR smoke run root under `Build/_AgentValidation/<run>/`
- `reports/steamvr-openxr-startup-diagnostics.json`
- `reports/openxr-loader-preflight.json`
- `reports/openxr-steamvr-smoke-summary.normalized.json`
- relevant engine log session under `Build/Logs/...`
- RenderDoc capture path only when logs and mirror/headset output do not explain a rendering failure

### September 26 source checkpoint

The final input/preview build succeeded without warnings or errors, but its hardware attempt stalled at Ready with zero submitted frames and eye capture timed out. Earlier Focused OpenGL results are not final-binary acceptance. Latest focused tests: 21 passed, one failed (cross-action callback removal ordering); earlier broad tests: 224 passed, 61 failed, not repeated after the final input edits. Vulkan was not run before the owner requested wrap-up. Both owned validation editor sessions were stopped. See the [investigation checkpoint](../investigations/avatar/vr-full-body-calibration-2026-09-26.md#wrap-up-checkpoint).

### September 27 resume checkpoint

The Ready/zero-frame attempt logged a root-list collection-modification exception during editor pawn switching. CollectVisible marked the exception terminal and stopped frame generation. The root-list snapshot fix, callback dispatch fix, and avatar-instance cache fix built in the editor with zero warnings and errors. That initial resumed focused selection passed 24/24. Its broader selection passed 229/290; its 61 failure names exactly matched the preceding 224/285 selection. This comparison is not a clean baseline against repository HEAD.

The next physical retry did not create a graphics session: `xrGetSystem` returned `ErrorFormFactorUnavailable`. The first OpenVR check reported the Beyond out of range and no controllers; a later check found the SteamVR server absent. The September 26 Focused eye submissions and device inventories remain historical evidence. The snapshot fix and OpenGL final-eye preview have not been confirmed on the latest binary; physical tracker, gesture, repeated-toggle, and frame-timing acceptance remain open.

A separate simulated Vulkan `RequireRequested` editor run built with zero warnings and errors, completed three-point calibration, and showed the avatar in a desktop screenshot. Its first offscreen attempt stopped on `A mesh command swap is already in progress` after capture version 17; `reports/resumed-vulkan-offscreen-fault.json` preserves that failure. Private spectator collection and swap now execute in the engine's canonical callbacks, with only GPU submission before present. A later isolated Vulkan run completed 119 offscreen captures through cuts, disable/re-enable, resize, and component reactivation without the mesh-swap fault. The final editor build had zero warnings and errors. Its focused tests passed 36/36; the broader run passed 229/290 with the same 61 failure names as before (`reports/final-focused.trx`, `reports/final-broad.trx`, `reports/final-failure-comparison.json`).

The final Vulkan Default-pipeline texture was exported through an exact submitted offscreen planner receipt, selected by capture owner and texture ID. The viewed 1920 × 1080 image is upright, contains finite HDR color samples and the avatar, and excludes editor debug overlays (`reports/vulkan-staged-final-texture.json`). Complete avatar color and garment appearance remain unaccepted: the full scene shows straight bright garment arms and an overbright body. The broad flat gray lower region is consistent with the validation scene's gray physics floor and is not itself evidence of a fault. The avatar asset contains independently rigged garments, but a later held-IK image with only Body and Shirt active did not reproduce the straight arms; in three held samples, each mesh's buffered bone palette matched its own expected pose with maximum difference zero. RenderDoc found Shirt's indexed draw and a populated finite, nonidentity 19-matrix GPU palette, and Body's draws had populated finite palettes. Viewed after-draw images showed bent arms without obvious straight white arms (`reports/renderdoc-shirt-held-frame.md`). The remaining full-scene visual cause is unresolved; the held frame does not prove exact CPU/GPU palette-byte agreement. This simulated editor result is not physical Vulkan headset, eye-image, or full-body hardware acceptance. Physical tracker exposure, controller gestures, repeated editor toggles, final eye visuals, and passing frame-time measurements remain open.
