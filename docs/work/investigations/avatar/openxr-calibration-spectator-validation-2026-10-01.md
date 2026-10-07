# Windows body calibration and spectator validation

## Scope

Validation date: October 1, 2026. Source baseline:
`77c9810b087040f4e10d3038016a39220560567d`, with the documentation and
OpenGL entry-point lookup correction described below applied locally.
The user confirmed that no headset, controllers, or body trackers were available.
This pass cannot establish hardware acceptance.

Environment: Windows build 26200, .NET SDK 10.0.401, .NET runtime 10.0.12,
NVIDIA GeForce RTX 4070 Laptop GPU (driver 32.0.16.1656), and Intel Arc
Graphics (driver 32.0.101.8132). Adapter inventory is not proof of which
adapter an individual renderer selected. No active OpenXR runtime was returned
by the Windows runtime registry query.

The [integration guide](../../../developer-guides/vr/openxr-body-tracking.md)
describes the engine behavior. The [acceptance procedure](../../testing/avatar/avatar-validation.md#openxr-body-calibration-and-spectator)
retains the unverified hardware and visual checks.

## Automated behavior

The existing headless project built with **zero warnings and zero errors**.
All **144 tests passed**, with zero failures, warnings, inconclusive results,
or skips. No tests were added or modified. NUnit executed the production
calibration, tracking, IK, input-state-machine, measurement, continuity, and
spectator contracts in 3.470 seconds.

Observed controls include six stable target nodes after repeated solver ticks,
unchanged avatar scale and tracking origin, zero repeated-sample walk drift,
and approximately 0.00000016 m analytic-limb endpoint error. The suite also
covers eight-tracker permutation invariance, transactional failure/cancel,
eleven-slot constraints, tracking validity and source recovery, and spectator
follow/routing/output lifetime. These are synthetic samples and controlled
fences, not headset observations or physical GPU race evidence.

Reproduce from the repository root after reserving one validation directory:

```powershell
dotnet build XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj --artifacts-path <run>/temp-build -p:UseSharedCompilation=false -m:1
dotnet <run>/temp-build/bin/XREngine.HeadlessTests/debug/XREngine.HeadlessTests.dll --workers=0 --work=<run>/reports --result=integration.xml
```

## Windows editor integration

The first isolated editor build found duplicate `DesktopAssetFileSystem` and
`DesktopAssetChangeMonitor` definitions. The duplicates were older, ignored
files under `XREngine.Runtime.Platform.Desktop/Assets/`, in addition to the
tracked root-level implementations. MSBuild included all four files even though
Git ignored the `Assets` copies. The two older files were preserved under this
run's `scratch/duplicate-desktop-sources/` and removed from compilation by moving
them out of the project. No tracked filesystem implementation was changed.

With those local duplicates out of the project, the complete Windows editor
and all-backend composition built with **zero warnings and zero errors**. The
named `xr-calibration-validation` session launched and answered MCP calls.
This closes the previous record's whole-editor compilation gap on this machine.

The initial saved scene contained Sponza rather than an avatar. Its Vulkan
capture was refused because no matching submitted resource generation existed.
Later logs identified `NativeOpaqueShading` rejection with a canonical texture
`SourceMismatch` after texture extent changed. This is a separate scene/texture
publication issue; neither startup nor MCP readiness counted as a rendered
spectator pass. The session was stopped before changing scenes.

A separate settings file under this run selected explicit VR emulation, the
local ARYIA humanoid FBX, locomotion, physics, and OpenGL. Shared unit-world
settings were not edited. The initial OpenGL launch exited during renderer
creation because an optional `glMultiDrawMeshTasksIndirectCountEXT` lookup
threw `SymbolLoadingException` from the desktop context adapter.

`DesktopSilkGlContext.GetProcAddress` now uses Silk's nonthrowing
`TryGetProcAddress`, returning zero for an unavailable entry point while
retaining the owner-thread and retired-context checks. The runtime interface
documents this contract. Existing renderer capability checks retain
responsibility for unsupported features; this does not enable a CPU renderer
or change the explicitly selected graphics backend.

## Live emulated-player results

After the entry-point fix, the complete editor rebuilt with zero warnings and
zero errors. OpenGL initialized on the NVIDIA GPU. The avatar imported and
the shared factory created the calibration canvas, footprints, spectator
anchor/boom/camera, and both output slots. The FBX reported 41 unresolved texture
references, so it is not a material/color reference scene.

MCP invoked the production player's queued methods and inspected the resulting
simulation-owned state. This exercises the application wiring without simulating
physical controller gestures or claiming valid OpenXR poses:

| Operation | Observed result |
|---|---|
| Open calibration with no measurement | Remained idle and requested standing height or arm span. |
| Temporarily set height to 1.7 m and open | Entered preview and returned the stance/capture instructions. |
| Request capture without usable tracked poses | Remained in preview; reported that the headset, controllers, and selected trackers must be tracking. `HasCommittedCalibration` remained false. |
| Cancel | Returned to idle with the previous-rig-restored message; no calibration was committed. |
| Enable spectator | The desktop viewport selected the spectator camera; follow evaluated to approximately `(0, 1.475, 3)` with a -4.764-degree pitch. |
| Disable spectator | Restored the prior desktop selection and cleared the routing ownership fields; follow/output became inactive. |

The original unset height and disabled spectator setting were restored before
shutdown; spectator routing and final calibration state were read back. The factory and state transitions are confirmed;
successful physical calibration, calibrated-avatar movement, and first-person
visibility remain unverified.

Visual validation did **not** pass. A PNG captured and viewed before the model
finished importing showed editor UI over a black scene. After import, both
ordinary and composited spectator screenshots timed out at 20 seconds. A second
editor camera position also timed out after disabling the spectator. Therefore
the evidence does not establish that the spectator itself causes the failure,
nor that the avatar or spectator rendered correctly. Profiler evidence reported
long window-event-pump intervals; the root cause remains unresolved.

RenderDoc's environment check passed. A capture was requested after injection
into this workflow's own editor process, but no frame was obtained. Injection
after OpenGL context creation does not establish a usable capture hook, so that
attempt cannot diagnose the rendering failure. The next rendering investigation
should arrange capture before context creation, retain this exact scene/settings,
and distinguish window pumping from scene submission before changing rendering
code. The RenderDoc connection and named editor session were closed.

## Evidence and remaining acceptance

Disposable evidence root:
`Build/_AgentValidation/20261001-093445-xr-calibration-validation/`.
The retained results include `reports/integration.xml`,
`logs/headless-build.log`, `logs/headless-tests.log`, initial/retry editor
launch logs, `logs/editor-build-initial.log`, `logs/editor-build-final.log`,
`logs/avatar-opengl/`, RenderDoc diagnostics, and MCP responses under
`mcp-output/`. `mcp-captures/` holds the viewed startup PNG; failed requests are
retained as explicit error responses rather than fabricated images. The named editor build/log root is
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261001-093542-xr-calibration-validation/`.
Required conclusions are recorded here because those directories are disposable.

Still requiring named-hardware evidence: role-independent tracker streaming,
measured controller offsets, readable headset calibration UI and gestures,
physical loss/reconnect/recenter behavior, actual VR-toggle continuity,
headset/spectator coexistence and visibility, and measured frame deadlines,
allocations, and spectator cost. Encoding and a calibration mirror remain
optional unimplemented features. The user has not reported a hardware result.
