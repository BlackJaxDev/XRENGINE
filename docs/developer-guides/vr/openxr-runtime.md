# OpenXR Runtime

XREngine includes an OpenXR runtime path alongside the older OpenVR path. OpenVR remains the currently tested day-to-day VR path, while OpenXR is implemented for engine integration, validation, and runtime portability work.

This feature doc promotes the implemented reference review from `docs/work/design/VR/openxr-implementation-comparison.md`.

## Startup Behavior

VR startup is controlled by `VRGameStartupSettings` and the selected `EVRRuntime`.

- `OpenXR` forces the OpenXR path. If initialization fails, VR startup fails visibly with diagnostics.
- `OpenVR` uses the existing OpenVR path.
- `Auto` can try OpenXR first and fall back to OpenVR when configured.

The Unit Testing World can request OpenXR with `UseOpenXR: true`.

### Runtime Modes

Use these modes deliberately; they are not interchangeable.

| Mode | Runtime selection | Startup behavior |
|---|---|---|
| `VR.Mode=OpenXR` | Windows active OpenXR runtime unless `XR_RUNTIME_JSON` or `VR.OpenXrRuntimeJson` is set. | Uses the engine OpenXR path. When SteamVR is the selected runtime, startup may best-effort launch SteamVR, but failures stay visible. |
| `VR.Mode=OpenXR` with process `XR_RUNTIME_JSON` | The manifest named by the process environment variable. | Useful for one-off SteamVR, Monado, or vendor-runtime validation without writing the Windows active-runtime registry key. |
| `VR.Mode=MonadoOpenXR` | Monado runtime JSON auto-detection or explicit Monado JSON. | Adds Monado-only bootstrap: loader/path setup, simulated display settings, and `monado-service.exe` handling. |
| `VR.Mode=OpenVR` | SteamVR/OpenVR. | Existing OpenVR path. Keep it available until the SteamVR OpenXR validation matrix is green and owner-approved. |

Do not use `VR.Mode=OpenXR` as a silent OpenVR fallback. If the active runtime is wrong, the loader is missing, SteamVR is unavailable, or the graphics extension is absent, the launch should fail with actionable diagnostics.

## SteamVR OpenXR Hardware Lane

The SteamVR lane is separate from the Monado no-HMD lane. It exercises the real OpenXR API path against SteamVR hardware and writes diagnostics under `Build/_AgentValidation/<run>/`.

Preferred smoke command:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
```

Explicit process-scoped runtime override:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json" -Renderer Vulkan
```

OpenGL validation is still supported:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer OpenGL
```

SteamVR OpenXR on OpenGL can be runtime/driver fragile. If OpenGL session creation fails while Vulkan succeeds, record the OpenGL diagnostics in the validation report and keep Vulkan as the primary hardware lane.

The smoke runner prints and records:

- selected runtime manifest and selection mode,
- inherited and child-process `XR_RUNTIME_JSON`,
- Windows active OpenXR runtime registry value,
- resolved `openxr_loader.dll`,
- renderer backend,
- `vrserver` / `vrmonitor` process state before launch,
- graphics-extension preflight results,
- normalized OpenXR smoke summary,
- allocation audit output unless `-SkipAllocationAudit` is used.

The VS Code hardware lane names are:

- `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`
- `Test-OpenXR-SteamVR-Smoke`
- `Editor (Unit Testing OpenXR SteamVR)`

## Runtime-Neutral Input

VR gameplay input now routes through `RuntimeVrInputServices` instead of treating OpenVR action dictionaries as the canonical model. The service covers action registration, per-frame update, boolean/float/vector2/vector3 state, grip and aim poses, haptics, and hand skeleton queries.

OpenVR remains behind the same abstraction for parity checks. The OpenXR adapter creates runtime-neutral gameplay actions, suggests bindings for common controller profiles, syncs actions every frame, dispatches action state, applies/stops haptics, exposes grip/aim pose availability, tracks SteamVR VIVE tracker user paths through `XR_HTCX_vive_tracker_interaction`, and uses `XR_EXT_hand_tracking` when available. When hand joints are unavailable, controller grab state is exposed as a synthesized finger summary with diagnostics.

## Runtime-Neutral Render Models

Controller and tracker visuals route through `RuntimeVrRenderingServices.RenderModelProvider` instead of querying OpenVR devices directly from scene components.

- OpenXR controller poses and inputs remain authoritative through runtime-neutral actions.
- When the active OpenXR runtime exposes `XR_MSFT_controller_model`, the engine loads the runtime-supplied controller GLB and imports it as normal scene content.
- When `XR_MSFT_controller_model` is unavailable, or when tracker meshes are needed, the provider can query SteamVR's OpenVR render-model service for the matching controller role or generic tracker render model.
- OpenXR VIVE tracker poses still come from `XR_HTCX_vive_tracker_interaction` user paths. OpenXR does not currently provide tracker meshes through the Silk.NET bindings in this repo, so tracker visuals use SteamVR render models when SteamVR/OpenVR is reachable.

The scene components only ask for a left/right controller model or a tracker model by OpenXR user path/OpenVR device index. This keeps poses, input, and visual assets decoupled enough for future `XR_EXT_render_model` or `XR_EXT_interaction_render_model` support when bindings are available.

## SteamVR VIVE Trackers

`XR_HTCX_vive_tracker_interaction` is integrated as the OpenXR source of truth for SteamVR tracker identity and role paths. The engine suggests bindings for the standard VIVE tracker role paths, calls `xrEnumerateViveTrackerPathsHTCX` when the extension is enabled, and handles `XR_TYPE_EVENT_DATA_VIVE_TRACKER_CONNECTED_HTCX` when trackers connect or roles change.

The default role paths are only binding candidates. They are not exposed as scene trackers until SteamVR reports a tracker through HTCX enumeration/events or a tracker action pose becomes valid. Each scene tracker keeps the canonical user path plus any persistent path, assigned role path, role name, and pose-availability flag supplied by the runtime.

## Frame Lifecycle

OpenXR follows the standard runtime-owned swapchain lifecycle:

1. Poll runtime events and session state.
2. Wait and begin the next frame.
3. Locate predicted views for visible collection.
4. Build per-eye visibility from predicted poses/FOV.
5. Locate late views near render time.
6. Acquire, wait, render, flush, and release each swapchain image.
7. Submit an OpenXR projection layer with per-eye pose and FOV.

The implementation keeps OpenXR calls on the render side while allowing the engine's visible-collection work to use predicted views.

## Pose Timing

The runtime maintains predicted and late pose caches for:

- HMD views,
- eye FOV,
- controllers,
- and trackers/user paths.

Callers pass an explicit runtime pose timing when asking VR transforms to update render matrices. This avoids process-global timing switches and lets update, collection, and rendering readers use the correct pose cache for their phase.

OpenXR pose location normally uses the runtime's `xrWaitFrame` predicted display time. For runtime-specific tuning, `OpenXrPoseTimeOffsetMs` or `XRE_OPENXR_POSE_TIME_OFFSET_MS` adds a small signed millisecond bias to `xrLocateViews` and action-space pose location only. Positive values ask the runtime to predict poses further ahead; negative values reduce the prediction lead. The frame is still submitted with the runtime-provided predicted display time.

## OpenGL Swapchain Safety

The OpenXR OpenGL path avoids forced WGL context switching from arbitrary threads. Session setup is deferred until the render side can safely initialize GL-backed swapchains.

Per-eye rendering uses:

- acquire/wait/release discipline,
- release in `finally` paths,
- GL flush before release,
- viewport/scissor/mask sanitation,
- and state restoration to avoid contaminating desktop rendering.

## Tracking Loss And Diagnostics

Engine settings expose OpenXR pose, tracking-loss, action-sync, pacing, and diagnostic policies. Debug options include frame lifecycle logging, OpenGL diagnostics, eye-order testing, and clear-only eye rendering for swapchain verification. `SinglePassStereo` is strict: OpenXR Vulkan uses the layered multiview staging path when its required capabilities are available and otherwise logs the precise rejection reason and submits no projection layer. Choose `SequentialViews` explicitly for per-eye rendering.

## Monado Tooling

The repo-local Monado test runtime lives at `Build/Submodules/monado` and is sourced from `https://github.com/BlackJaxDev/Monado.git`.

Use `Tools/OpenXR/Build-Monado.ps1` to initialize/update that submodule and build Monado in place. Use `Tools/OpenXR/Install-Monado.ps1` when you also want the runtime staged under `Build/Deps/Monado`, an environment helper written, and `openxr_loader.dll` copied into the editor output when available.

The visible simulated-HMD preview is the `monado-service.exe` windowed compositor target, not `monado-gui.exe`. When an OpenXR session is active, the staged Windows build titles that window with the requested headset preset, internal per-eye resolution, current window size, and preview-eye scale. Leave `XRT_WINDOW_PEEK` unset for the default editor path; it enables Monado's separate experimental peek target.

## Implementation References

- `XREngine.Input/RuntimeVrInputServices.cs`
- `XREngine.Input/RuntimeVrStateServices.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RenderModels.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.RuntimeNeutral.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Pacing.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeVrRenderingServices.cs`
- `XREngine/Engine/Engine.VRState.cs`
- `XREngine/Engine/Engine.RuntimeVrStateServices.cs`
- `XREngine/Engine/Engine.RuntimeVrRenderingServices.cs`
- `XREngine.Runtime.Rendering/Rendering/Camera/XROpenXRFovCameraParameters.cs`
- `XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRDeviceTransformBase.cs`
- `XREngine.Runtime.InputIntegration/Scene/Components/VR/VRDeviceModelComponent.cs`
- `XREngine.UnitTests/Rendering/OpenXrTimingPipelineContractTests.cs`

## Troubleshooting

### Active runtime is not SteamVR

Run `Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime`. The startup diagnostics show the Windows active runtime and warn when the selected manifest looks like Monado, Oculus, Windows Mixed Reality, or another runtime. Set SteamVR as the active OpenXR runtime in SteamVR settings, or pass SteamVR's manifest with `-RuntimeJson`.

### `XR_RUNTIME_JSON` still points at Monado

An inherited `XR_RUNTIME_JSON` overrides the Windows active runtime unless `-UseActiveRuntime` is used. Clear it in the shell or use `-UseActiveRuntime` to remove it from the smoke child process.

### OpenXR loader cannot be found

Install/stage the Khronos loader or use the repo Monado install flow, which copies `openxr_loader.dll` into the editor output when available. The SteamVR smoke also searches common SteamVR and PATH locations and records the resolved loader.

### Graphics binding extension is missing

The smoke preflight requires `XR_KHR_opengl_enable` for OpenGL and either `XR_KHR_vulkan_enable` or `XR_KHR_vulkan_enable2` for Vulkan. Use `-SkipLoaderPreflight` only when you are intentionally collecting runtime failure diagnostics.

### SteamVR is running but the headset is unavailable

Check SteamVR status, HMD power/USB/DisplayPort, SteamVR dashboard focus, and the OpenXR smoke summary. Startup should fail visibly rather than falling back to OpenVR.

### Head motion feels late or over-predicted

Start by enabling OpenXR lifecycle/profiler diagnostics and checking predicted display lead time, predicted-to-late pose delta, and missed-deadline counts. If those look healthy but a specific runtime still feels consistently behind or ahead, test a small pose-time bias:

```powershell
$env:XRE_OPENXR_POSE_TIME_OFFSET_MS = "2.0"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

Use small values first, usually within a few milliseconds. Large positive values can make head motion overshoot; large negative values can make it feel laggy.

### Action bindings are missing or inactive

The OpenXR adapter logs missing/inactive action diagnostics for the active interaction profile. Open SteamVR's binding UI and verify locomotion, turn, grab, jump, quick menu, mute, and haptic outputs are bound for the controller profile in use.

### Tracker roles are not assigned

VIVE tracker pose actions use SteamVR role paths. Assign tracker roles in SteamVR before expecting OpenXR tracker transforms to appear. The engine records known role and persistent paths when `XR_HTCX_vive_tracker_interaction` is advertised.

### Hand tracking extension is unavailable

SteamVR may not expose `XR_EXT_hand_tracking` for all hardware. The OpenXR path logs this explicitly and falls back to controller-derived finger summary where possible; it does not promise OpenVR skeleton data through OpenXR.
