# OpenXR SteamVR Hardware Validation

Last Updated: 2026-07-04
Branch: `feature/openxr-steamvr-parity`

This report tracks the SteamVR OpenXR hardware matrix for the OpenVR parity work. It records the runnable validation lane added in this pass and the evidence that must be captured on a machine with SteamVR hardware attached.

## Current Validation Status

This agent pass validated source integration, tooling syntax, VS Code orchestration JSON, and the targeted editor build. It did not claim a physical headset pass because no SteamVR HMD/controller/tracker hardware was available through this session.

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
| Headset model | Pending hardware run |
| Controller models | Pending hardware run |
| VIVE tracker count | Pending hardware run |
| VIVE tracker roles | Pending hardware run |
| Expected hand/finger data | Pending hardware run |
| SteamVR version | Pending hardware run |
| OpenXR runtime manifest | Pending hardware run |

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
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json" -Renderer Vulkan
```

## Matrix

| Scenario | Status | Evidence |
|---|---|---|
| SteamVR OpenXR, OpenGL, headset only | Pending hardware run | Capture smoke summary and logs; mark blocked-by-runtime if OpenGL session creation fails with diagnostics. |
| SteamVR OpenXR, OpenGL, headset plus controllers | Pending hardware run | Capture controller grip/aim pose and action diagnostics. |
| SteamVR OpenXR, OpenGL, headset plus controllers plus VIVE trackers | Pending hardware run | Capture tracker role/persistent path diagnostics. |
| SteamVR OpenXR, OpenGL, hand/finger data where supported | Pending hardware run | Capture `XR_EXT_hand_tracking` availability and joint validity. |
| SteamVR OpenXR, Vulkan, headset only | Pending hardware run | Capture smoke summary and headset/mirror visual result. |
| SteamVR OpenXR, Vulkan, headset plus controllers | Pending hardware run | Capture gameplay action and haptic result. |
| SteamVR OpenXR, Vulkan, headset plus controllers plus VIVE trackers | Pending hardware run | Capture tracker reconnect/role reassignment behavior. |
| SteamVR OpenXR, Vulkan, hand/finger data where supported | Pending hardware run | Capture real joints or controller-derived fallback diagnostics. |
| Wrong or missing `XR_RUNTIME_JSON` | Tooling implemented | `Run-OpenXrSteamVrSmoke.ps1` validates selected manifests and warns on non-SteamVR runtime kind. |
| SteamVR not running | Tooling implemented | Smoke runner records process state and attempts `vrstartup.exe` or Steam URI before launch. |
| Headset removed, dashboard opened, runtime restarted, session lost | Pending hardware run | Capture session-state transitions in smoke summary and engine logs. |
| No new OpenXR hot-path allocations | Tooling implemented | Smoke runner invokes `Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations` unless skipped. |
| Monado no-HMD smoke | Pending follow-up run | Run `Test-OpenXR-Monado-Smoke` after hardware lane changes. |
| Existing OpenVR path | Pending hardware run | Capture baseline on same machine before retiring OpenVR. |

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
