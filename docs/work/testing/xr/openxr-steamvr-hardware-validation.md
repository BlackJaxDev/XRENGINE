# OpenXR SteamVR Hardware Validation

Scope: Validate the SteamVR OpenXR hardware lane for headset presentation, controller actions, haptics, hand data, tracker exposure, runtime loss, allocation audits, and OpenVR parity boundaries.

Architecture: [OpenXR Runtime](../../../developer-guides/vr/openxr-runtime.md#steamvr-openxr-hardware-lane), [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md), [VR Output Pacing And Mirror Policy](../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md). Code todos: [OpenXR Monado CI And Hardware Follow-ups](../../todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md), [OpenXR Future Work](../../todo/rendering/vr/openxr-future-work-todo.md), [Editor OpenXR Toggle](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md).

## Setup

Use `Build-Editor` before a hardware run. Use `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` for an editor lane with SteamVR selected. Use `Test-OpenXR-SteamVR-Smoke` for the scripted smoke lane. Use `Test-OpenXR-Monado-Smoke` after changes that also affect no-HMD runtime behavior.

Primary Vulkan lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
```

OpenGL diagnostic lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer OpenGL
```

Explicit runtime manifest lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "<steamvr-runtime-manifest>" -Renderer Vulkan
```

Record the headset model, controller models, tracker count, tracker roles, expected hand/finger data, SteamVR version, OpenXR runtime manifest, Windows version, GPU, and driver. Do not record user profile paths. Keep raw disposable evidence outside tracked docs.

## Checks

### Runtime selection and smoke tooling

Architecture: [OpenXR Runtime, SteamVR OpenXR Hardware Lane](../../../developer-guides/vr/openxr-runtime.md#steamvr-openxr-hardware-lane).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Active SteamVR runtime | Run `Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan`. | The startup diagnostics show SteamVR as the selected OpenXR runtime. A non-SteamVR runtime is reported before launch. | Open | 2026-09-26 SteamVR runtime 2.17.10 was selected successfully. |
| Explicit SteamVR manifest | Run `Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "<steamvr-runtime-manifest>" -Renderer Vulkan`. | The child process uses the explicit manifest without writing the Windows active-runtime registry key. | Open | Last evidence: none. |
| SteamVR not running | Run the smoke when SteamVR is not already running. | The runner records process state and attempts `vrstartup.exe` or the Steam URI before launch. | Open | Tooling implemented. |
| Wrong or missing runtime manifest | Run the smoke with a missing or wrong `XR_RUNTIME_JSON`. | The runner validates the selected manifest and warns on non-SteamVR runtime kinds. | Open | Tooling implemented. |
| No new OpenXR hot-path allocations | Run the smoke without `-SkipAllocationAudit`. | The runner invokes `Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations` and reports any new allocation. | Open | Tooling implemented. |

### Headset presentation and timing

Architecture: [OpenXR VR Rendering, startup and graphics binding](../../../architecture/rendering/openxr-vr-rendering.md#startup-and-graphics-binding).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| OpenGL headset frame submission | Run the OpenGL hardware lane with a connected headset. | The session reaches `SessionRunning` and `Focused`, submits frames, and reports predicted and late HMD views. | Open | 2026-09-26 OpenGL run submitted 416 frames but missed 411 deadlines. |
| Vulkan headset frame submission | Run the Vulkan hardware lane with a connected headset. | The session reaches `SessionRunning` and `Focused`, submits frames, and shows the expected mirror/headset visuals. | Open | Last evidence: none. |
| Frame timing acceptance | Run a headset frame-timing pass on OpenGL and Vulkan. | Missed deadline count is within the accepted runtime budget for the target refresh rate. | Open | 2026-09-26 OpenGL timing failed. Median render times were above budget in short samples. |
| Session loss and runtime restart | Remove the headset, open the dashboard, restart SteamVR, and run the smoke again. | The smoke summary records session-state transitions and teardown without leaked runtime resources. | Open | 2026-09-27 later retry returned `ErrorFormFactorUnavailable`; final acceptance remains open. |
| OpenGL final-eye preview on latest binary | Run the OpenGL lane after the latest runtime and renderer fixes. | Final-eye preview appears and no stale historical build result is used as acceptance. | Open | 2026-09-27 latest binary was not confirmed on physical hardware. |

### Controllers, hands, haptics, and trackers

Architecture: [OpenXR Runtime, runtime-neutral input](../../../developer-guides/vr/openxr-runtime.md#runtime-neutral-input).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Controller pose availability | Run the hardware lane with Valve Index or Vive controllers. Inspect grip and aim pose availability. | Left and right controller grip and aim poses are available through OpenXR. | Open | 2026-09-26 grip and aim pose caches were available. |
| Gameplay actions per controller profile | With `VR.Mode=OpenXR` on SteamVR, exercise locomote, turn, grab left and right, jump, quick menu, and mute on Valve Index and Vive controllers. | Each action fires through the OpenXR action set. Component paths are correct per profile. Any required manual SteamVR binding step is recorded. | Open | Grip and aim poses observed on OpenGL; gestures not exercised. |
| Haptics | Trigger the haptic action on each hand on OpenGL and Vulkan. | `xrApplyHapticFeedback` and `xrStopHapticFeedback` give felt feedback on the correct hand. | Open | Last evidence: none. |
| OpenXR hand data | Run with hardware and runtime support for hand data. | Hand-joint activity and finger articulation are validated visually or numerically. Controller-derived fallback diagnostics are clear when the extension is absent. | Open | 2026-09-26 hand-joint active flags were observed; articulation remained open. |
| HTCX tracker enumeration | Run the OpenXR hardware lane with VIVE trackers connected and roles assigned. | `XR_HTCX_vive_tracker_interaction` reports persistent tracker paths when the runtime exposes them. | Open | 2026-09-26 engine and independent native HTCX queries returned zero paths while OpenVR saw tracked Tundra trackers. |
| Late tracker connection and role reassignment | Connect or reassign trackers after startup, then explicitly restart VR when required. | Diagnostics distinguish not enumerated, discovered but requiring restart, unbound, inactive, stale, and tracking-lost states. | Open | Last evidence: none. |
| No OpenVR API usage | Run OpenXR lanes with OpenVR inactive. | Headset, poses, input, haptics, and hand data work without OpenVR calls. | Open | Last evidence: none. |
| Existing OpenVR path baseline | Run the existing OpenVR hardware path. | OpenVR remains available as the baseline until SteamVR OpenXR validation is green and owner-approved. | Open | 2026-09-26 OpenVR inventory saw HMD, controllers, and Tundra trackers. Gameplay baseline remained open. |

### Editor runtime toggle on SteamVR

Architecture: [OpenXR Runtime, startup behavior](../../../developer-guides/vr/openxr-runtime.md#startup-behavior).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| SteamVR editor toggle | Start with `VR.Mode=Desktop`. Enable the editor OpenXR checkbox and choose SteamVR. | The editor selects SteamVR for the process, creates or reuses the correct VR rig, preserves the desktop pawn, and starts the session. | Open | Last evidence: none. |
| Toggle-off and repeated toggle | Toggle SteamVR off, then repeat desktop to SteamVR to desktop. | The original desktop pawn is restored. Temporary rigs, callbacks, leases, and publication pins are cleaned up. | Open | Last evidence: none. |
| Runtime unavailable and cancel | Use Cancel, missing SteamVR, and startup failure paths. | Cancel leaves desktop control unchanged. Missing runtimes and startup failure report visible errors and restore the previous configuration. | Open | Last evidence: none. |

### Hardware matrix

Architecture: [OpenXR Runtime, SteamVR OpenXR Hardware Lane](../../../developer-guides/vr/openxr-runtime.md#steamvr-openxr-hardware-lane).

| Scenario | Status | Last evidence |
|---|---|---|
| SteamVR OpenXR, OpenGL, headset only | Open, frame submission observed; timing failed. | 2026-09-26 Focused epoch 1, 416 submitted frames, predicted/late HMD views, 411 missed deadlines. |
| SteamVR OpenXR, OpenGL, headset plus controllers | Open, pose availability observed; gestures pending. | 2026-09-26 both grip and aim pose caches were available. |
| SteamVR OpenXR, OpenGL, headset plus controllers plus VIVE trackers | Open, blocked by runtime exposure. | 2026-09-26 HTCX OpenXR enumeration count was zero while OpenVR saw three pose-valid Tundra trackers. |
| SteamVR OpenXR, OpenGL, hand/finger data where supported | Open, joint activity observed; behavior pending. | 2026-09-26 both hand-joint active flags were set. |
| SteamVR OpenXR, Vulkan, headset only | Open. | Last evidence: none. |
| SteamVR OpenXR, Vulkan, headset plus controllers | Open. | Last evidence: none. |
| SteamVR OpenXR, Vulkan, headset plus controllers plus VIVE trackers | Open. | Last evidence: none. |
| SteamVR OpenXR, Vulkan, hand/finger data where supported | Open. | Last evidence: none. |
| Monado no-HMD smoke after hardware-lane changes | Open. | Last evidence: none. |

## Hardware Matrix

| Field | Value |
|---|---|
| Headset model | Bigscreen Beyond observed on 2026-09-26; later retry reported out of range. |
| Controller models | Valve Knuckles left and right observed on 2026-09-26; later retry did not see controllers. |
| VIVE tracker count | Three Tundra Labs trackers observed through OpenVR on 2026-09-26; HTCX OpenXR enumeration returned zero. |
| VIVE tracker roles | Not established through OpenXR. |
| Expected hand/finger data | Hand-joint active flags observed; expected values and articulation not accepted. |
| SteamVR version | 2.17.10, application build 25330290, in the 2026-09-26 probe. |
| OpenXR runtime manifest | Active SteamVR manifest in the 2026-09-26 probe. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| HTCX tracker enumeration | OpenXR reports zero tracker paths while OpenVR reports connected and pose-valid Tundra trackers. | [OpenXR Monado CI And Hardware Follow-ups](../../todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md) |
| OpenGL frame timing | The 2026-09-26 OpenGL run submitted frames but missed most deadlines. | [VR Rendering Performance Contract](../../todo/rendering/optimization/vr-rendering-performance-contract-todo.md) |
| Later physical retry | `xrGetSystem` returned `ErrorFormFactorUnavailable` after the headset became unavailable. | [OpenXR SteamVR Hardware Validation](openxr-steamvr-hardware-validation.md) |

