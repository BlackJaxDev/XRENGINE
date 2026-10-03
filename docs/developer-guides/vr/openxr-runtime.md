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

In the ImGui Unit Testing editor, `VR.Mode=Desktop` with
`VR.AllowDesktopEditing=true` starts on the desktop pawn. Enabling the toolbar
**OpenXR** checkbox asks for **Monado (testing)** or **SteamVR (headset)**;
Cancel leaves desktop control unchanged. The choice resolves an explicit runtime
manifest for this process without changing the Windows active-runtime registry.
Pawn
possession follows the session state; startup and teardown can take several
frames. The toggle creates a VR rig on demand and destroys that owned rig after
the session stops. It preserves and restores the previously controlled desktop
pawn, including a character pawn; if that pawn is unavailable it creates an editor
camera pawn. A world does not need prebuilt VR or desktop pawns. Existing authored
VR rigs may be reused and are not destroyed by the toggle.
Temporary rigs belong to the hidden editor scene. For a new runtime choice, the
editor retires the old renderer and its OpenXR instance, applies the selected
manifest, then recreates the renderer before starting the session. This preserves
the world and desktop pawn. Failed renderer initialization restores the previous
configuration. Missing runtimes report an error; there is no silent runtime fallback.

Vulkan session creation waits for a valid device, completion of the editor startup
presentation, and a boundary outside desktop frame recording/submission. It does
not wait for scene texture imports, decode queues, upload queues, command-buffer
quiet periods, or a minimum desktop frame count. The graphics transition takes
exclusive device queue admission and waits for submitted GPU work before and after
session/swapchain creation. Actual runtime or allocation failures remain visible.
Eye resource preparation retains its separate readiness checks.
An eye whose resource generation or captured frame package is not ready defers
that eye preparation and discards partial captured work. This ordinary readiness
result must not trigger whole-window render-error backoff; genuine invariant and
capacity failures remain errors.

Eye-specific pipeline restrictions apply only to the current external-swapchain
viewport. Keeping an OpenXR runtime prepared must not change desktop passes.
BRDF lookup texture factories use the size captured from their resource-generation
profile, so changing runtime state between declaration and creation cannot change
the texture extent.

To investigate texture progress, sample `get_texture_streaming_summary` more than
once. `active_gpu_upload_count` includes outstanding requests awaiting preparation;
it does not mean that many GPU transfers are executing. Compare backend completed
chunks/bytes, final publications, failures, and pending preparation/transfer counts.
A temporarily idle worker between bounded batches is expected. A persistent queue
with unchanged completion counters needs investigation; queue depth alone cannot
establish a stall or scene complexity.

## SteamVR OpenXR Hardware Lane

The SteamVR lane is separate from the Monado no-HMD lane. It exercises the real OpenXR API path against SteamVR hardware and writes diagnostics under `Build/_AgentValidation/<run>/`.

Preferred smoke command:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
```

Explicit process-scoped runtime override:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "<steamvr-runtime-manifest>" -Renderer Vulkan
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

OpenVR remains behind the same abstraction for parity checks. The OpenXR adapter creates runtime-neutral gameplay actions, suggests bindings for common controller profiles, syncs actions every frame, dispatches action state, applies/stops haptics, exposes grip/aim pose availability, tracks SteamVR VIVE tracker persistent paths through `XR_HTCX_vive_tracker_interaction`, and uses `XR_EXT_hand_tracking` when available. When hand joints are unavailable, controller grab state is exposed as a synthesized finger summary with diagnostics. Calibration open, cancel, and simultaneous trigger capture are delivered on press edges through `RuntimeVrInputServices.CalibrationActionPressed`, independently of desktop pawn possession.

## Runtime-Neutral Render Models

Controller and tracker visuals route through `RuntimeVrRenderingServices.RenderModelProvider` instead of querying OpenVR devices directly from scene components.

- OpenXR controller poses and inputs remain authoritative through runtime-neutral actions.
- When the active OpenXR runtime exposes `XR_MSFT_controller_model`, the engine loads the runtime-supplied controller GLB and imports it as normal scene content.
- When `XR_MSFT_controller_model` is unavailable, or when tracker meshes are needed, the provider can query SteamVR's OpenVR render-model service for the matching controller role or generic tracker render model.
- OpenXR VIVE tracker poses still come from `XR_HTCX_vive_tracker_interaction` user paths. OpenXR does not currently provide tracker meshes through the Silk.NET bindings in this repo, so tracker visuals use SteamVR render models when SteamVR/OpenVR is reachable.

The scene components only ask for a left/right controller model or a tracker model by OpenXR user path/OpenVR device index. This keeps poses, input, and visual assets decoupled enough for future `XR_EXT_render_model` or `XR_EXT_interaction_render_model` support when bindings are available.

## SteamVR VIVE Trackers

`XR_HTCX_vive_tracker_interaction` supplies session-scoped physical identity through persistent paths. The engine suggests pose bindings only on the extension's role paths, including handheld-object, wrist, and ankle paths. It uses enumerated persistent paths only as action subpaths and scene tracker keys. Slot assignment never reads role metadata. The engine enumerates trackers when the extension is enabled and handles `XR_TYPE_EVENT_DATA_VIVE_TRACKER_CONNECTED_HTCX` when a tracker connects or its metadata changes.

Each reported physical path creates at most one scene tracker. The render owner re-enumerates tracker paths at a throttled interval and records provider presence separately from action binding, action activity, position validity, orientation validity, and current-snapshot pose availability. `RuntimeVrStateServices.GetKnownOpenXrTrackers()` returns an immutable value snapshot under the pose-cache lock; `GetOpenXrTrackerStatus(path)` also reports provider unavailable and undiscovered paths. Absence from enumeration does not reveal whether SteamVR disabled a device. Current pose availability requires an active pose action, valid position and orientation, the matching predicted frame and sample time, a running session, and publication within 250 ms. The same lock guards the pose and its snapshot metadata; a stopped frame loop therefore cannot keep supplying a valid calibration pose. Last-known pose is retained only as a diagnostic. A tracker connected after action creation requires a new action set. Opening calibration requests a refresh; if a newly discovered tracker needs one, the runtime drains an active frame and recreates its OpenXR session. Calibration must be reopened after that interruption. A runtime that does not stream all physical trackers through persistent-path subactions remains a hardware limitation; there is no automatic provider fallback. See the [player calibration guide](../../user-guide/vr/full-body-calibration.md).

On a SteamVR 2.17.10 hardware run, OpenXR reached a stable Focused session with HMD and controller poses, but the engine received no HTCX tracker paths. An independent instance-level `xrEnumerateViveTrackerPathsHTCX` call through SteamVR's loader returned `XR_SUCCESS` and count zero twice, while OpenVR Background queries found three connected Tundra Trackers. Two had valid poses during the first query; all three did during a later query. This isolates the empty list to current SteamVR OpenXR exposure rather than scene reconciliation; it does not identify why that runtime omitted the paths. The extension permits a null role path, so missing role metadata alone is not proof of the cause. Do not treat OpenVR device indices or last-known matrices as substitute OpenXR calibration samples. A future explicit tracker-provider choice would need stable physical identity, freshness timestamps, and a coherent cross-provider capture contract before use. See the [hardware validation record](../../work/testing/openxr-steamvr-hardware-validation.md).

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
- `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenXR/OpenGlXrGraphicsBinding.Implementation.cs`
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

### Tracker is discovered but has no current pose

Check that `XR_HTCX_vive_tracker_interaction` is advertised, then reopen calibration to request a controlled input refresh for late connections. The diagnostic distinguishes a discovered tracker from one with an active, valid pose. The SteamVR OpenXR runtime's behavior with default, duplicate, or disabled tracker metadata still needs physical hardware validation. Do not infer pose support from enumeration alone.

### Avatar pose timing and coordinate ownership

The OpenXR adapter publishes predicted device poses with one snapshot identifier and predicted display time. Calibration and the VR player sample every required device from that publication; a mixed publication is rejected. Real samples expire after 250 ms without a new publication. Runtime poses are metric reference-space transforms. Each raw device composes that pose with its explicitly assigned playspace transform to produce a world pose. Avatar scale does not change this tracking basis.

On the scene owner, the VR player reads a coherent sample, updates the room-scale playspace compensation and queues its locomotion delta, then publishes slot sources and targets. Stored device-to-target offsets multiply the device world matrix exactly once. The stable target owner retains world goals and derives target locals through the avatar hierarchy; humanoid tuple offsets remain identity. Animation resets run in the normal animation tick, target publication runs in the normal scene tick, IK evaluates in the late animation tick, and spectator follow runs in the late scene tick after IK. Character-controller movement consumes its queued delta through its existing physics owner.

The skeleton deliberately uses the simulation pose. The render owner may late-locate headset and controller view transforms, but it does not rerun IK or mutate live bones. Consequently late-located eye views can be newer than the rendered skeleton. This is an explicit latency tradeoff, not a claim of late-latched avatar animation. Hardware timing validation must measure that difference.

Each body slot keeps its physical identity through tracking loss. It briefly holds its last world goal, then fades toward an optional `IVrBodyPoseSource` estimate or to zero weight. Estimates are already calibrated world-space goals. Their weights and poses blend independently from the frozen tracked goal, and the same physical device can fade back without rebinding another device. Unknown reference-space changes invalidate calibration; teleport, snap turn, avatar replacement, and session generation changes reset estimator and spectator history.

### Hand tracking extension is unavailable

SteamVR may not expose `XR_EXT_hand_tracking` for all hardware. The OpenXR path logs this explicitly and falls back to controller-derived finger summary where possible; it does not promise OpenVR skeleton data through OpenXR.
