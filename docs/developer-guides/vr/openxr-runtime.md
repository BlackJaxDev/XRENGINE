# OpenXR Runtime

XREngine includes an OpenXR runtime path alongside the older OpenVR path. OpenVR remains the currently tested day-to-day VR path, while OpenXR is implemented for engine integration, validation, and runtime portability work.

The native API is owned by `XREngine.Runtime.XR.OpenXR`. Rendering and Bootstrap depend on `IOpenXrRuntime`, graphics-host contracts, and explicit backend registration; OpenGL and Vulkan keep their native graphics bindings in their renderer projects. This source organization has not itself qualified a headset/runtime combination.

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

The engine enumerates physical trackers through `XR_HTCX_vive_tracker_interaction` and keys scene nodes by the opaque persistent path within a session generation. Body slots are assigned only by calibration proximity. A transport role change does not change a tracker's identity or assign a body part.

Suggested component bindings contain only profile-supported role paths; persistent paths are used only as action subaction paths. The advertised extension revision determines whether wrist and ankle bindings (revision 3) are included. All other defined roles, including handheld objects, are covered. The engine never enables an alternate pose provider because a tracker fails to stream.

Role-independent streaming on SteamVR hardware is still unverified. The extension permits persistent-path subactions but does not establish that every runtime streams devices with default, duplicated, absent, or disabled mappings. A runtime-hidden/disabled device is indistinguishable from a disconnected device when enumeration omits it. Do not infer a disabled status from an inactive action.

Trackers discovered after input attachment are reported as requiring a VR restart. OpenXR action subactions cannot be extended after attachment, and a session cannot attach a second action set. The player must explicitly restart VR after connecting new physical trackers; reconnecting an already admitted persistent identity can resume its existing space. There is no automatic gameplay rebuild or provider switch.

Simulation/calibration reads one caller-buffered predicted snapshot containing headset, controllers, and tracker samples with a session generation, snapshot ID, and OpenXR nanosecond display time. Late poses remain separate render inputs. Current validity requires an active action and valid position/orientation; a failed location clears it immediately. Retained display poses and separate ever-tracked/last-valid diagnostics never qualify as capture samples. Publications older than 250 ms, and all stopped sessions, are unusable.

Native discovery callbacks update transport metadata only. Scene tracker reconciliation and discontinuity delivery happen on the engine's pre-update scene owner. Owned tracker nodes are destroyed on collection deactivation and replaced on session-generation change.

The smoke summary includes extension revision and per-identity connection, binding, activity, validity, sample/snapshot IDs, last valid sample, and restart requirement. For a hardware probe, record the runtime/version and implementation commit, start with every tracker connected, capture the summary with default and duplicate mappings, then test occlusion, reconnection, disabled devices, and late connections. A nonempty enumeration alone is not evidence of streaming. Current pose availability requires an active action, valid position and orientation, a matching predicted snapshot and sample time, and a running session with publication within 250 ms. Last-known pose is diagnostic only.

On a SteamVR 2.17.10 hardware run, OpenXR reached a Focused session with HMD and controller poses but no HTCX tracker paths. Independent instance-level enumeration returned `XR_SUCCESS` and count zero twice while OpenVR found three connected Tundra Trackers. This isolates the empty list to that runtime's OpenXR exposure during the probe; it does not explain why the paths were omitted. See the [hardware validation record](../../work/testing/xr/openxr-steamvr-hardware-validation.md) and [player calibration guide](../../user-guide/vr/full-body-calibration.md).

### Calibration action bindings

The Global action category provides `CalibrationOpen` and `CalibrationCancel` booleans and independent `CalibrationCaptureLeft`/`CalibrationCaptureRight` float actions. Index uses left A/right B to open/cancel; Touch uses left Y/right B; Vive, Microsoft Motion, and simple controllers use left/right menu. Capture uses both triggers; simple controllers use both select buttons. These actions do not share mute bindings. Vive/Motion quick-menu activation uses left trackpad click, keeping left menu dedicated to calibration. Simple controller right-menu jump is omitted so cancel does not also jump.

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
- release after a successful image wait, including failure cleanup when release is legal,
- GL flush before release,
- viewport/scissor/mask sanitation,
- and state restoration to avoid contaminating desktop rendering.

## Tracking Loss And Diagnostics

Engine settings expose OpenXR pose, tracking-loss, action-sync, pacing, and diagnostic policies. Debug options include frame lifecycle logging, OpenGL diagnostics, eye-order testing, and clear-only eye rendering for swapchain verification. `SinglePassStereo` is strict: OpenXR Vulkan uses the layered multiview staging path when its required capabilities are available and otherwise logs the precise rejection reason and submits no projection layer. Choose `SequentialViews` explicitly for per-eye rendering.

## Monado Tooling

The repo-local Monado test runtime lives at `Build/Submodules/monado` and is sourced from `https://github.com/BlackJaxDev/Monado.git`.

Use `Tools/OpenXR/Build-Monado.ps1` to initialize/update that submodule and build Monado in place. Use `Tools/OpenXR/Install-Monado.ps1` when you also want the runtime staged under `Build/Deps/Monado`, an environment helper written, and `openxr_loader.dll` copied into the editor output when available.

The visible simulated-HMD preview is the `monado-service.exe` windowed compositor target, not `monado-gui.exe`. When an OpenXR session is active, the staged Windows build titles that window with the requested headset preset, internal per-eye resolution, current window size, and preview-eye scale. Leave `XRT_WINDOW_PEEK` unset for the default editor path; it enables Monado's separate experimental peek target.

### No-HMD test lanes

| Lane | Selection | Use |
|---|---|---|
| Contract tests | `OpenXrTimingPipelineContractTests` in `XREngine.UnitTests` | Timing and state-machine rules. No runtime. |
| Scene-only VR | `VR.Mode=Emulated`; `Tools/OpenXR/Run-OpenXrSceneOnlyVrSmoke.ps1`; task `Test-OpenXR-SceneOnlyVR-Smoke` | VR pawn and editor paths. It does not call the OpenXR API. |
| Monado OpenXR | `VR.Mode=MonadoOpenXR`; `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1`; task `Test-OpenXR-Monado-Smoke` | The real loader, instance, session, swapchains, and frame submission against Monado's simulated HMD. |

`Tools/OpenXR/Find-MonadoRuntime.ps1` resolves the runtime manifest. `-RuntimeJson` wins; otherwise it searches the common Monado build and install paths. The scripts never write the Windows active-runtime registry key. `Run-OpenXrMonadoSmoke.ps1` sets `XR_RUNTIME_JSON` for the child process only, runs a loader preflight for the renderer's graphics binding extension, and starts `monado-service.exe` only when needed. It stops only a service that it started.

`--smoke-frames N` or `XRE_SMOKE_FRAMES=N` makes the editor exit after N submitted OpenXR frames. The editor then drains the session, destroys swapchains, the session, and the instance, and writes a structured smoke summary with a `schemaVersion`. The runner fails if a required summary field is missing. The process exit code is zero only when the smoke criteria pass. The editor uses 21 for startup failure, 22 for frame timeout, 23 for summary failure, 24 for teardown failure, and 25 for an engine exception (`Program.OpenXrSmokeRunController.cs`). Runtime differences are gated by extension support or capability probes, not by runtime name, except in diagnostics.

Monado lane baselines, CI promotion, and persistent smoke settings are open decisions in [OpenXR Monado CI And Hardware Follow-ups](../../work/todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md). Checks are in [OpenXR Validation](../../work/testing/xr/openxr-validation.md).

## Implementation References

- `XREngine.Input/RuntimeVrInputServices.cs`
- `XREngine.Input/RuntimeVrStateServices.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.FrameLifecycle.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.State.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.XrCalls.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.RenderModels.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.Input.RuntimeNeutral.cs`
- `XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenXR/OpenGlXrGraphicsBinding.Implementation.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/IOpenXrRuntime.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/IOpenXrGraphicsHost.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.Pacing.cs`
- `XREngine.Runtime.XR.OpenXR/OpenXRAPI.RuntimeStateMachine.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeVrRenderingServices.cs`
- `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs`
- `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrStateServices.cs`
- `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeVrRenderingServices.cs`
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

### Avatar pose timing and coordinate ownership

The OpenXR adapter publishes predicted device poses with one snapshot identifier and predicted display time. Calibration and the VR player sample every required device from that publication; a mixed publication is rejected. Real samples expire after 250 ms without a new publication. Runtime poses are metric reference-space transforms. Each raw device composes that pose with its explicitly assigned playspace transform to produce a world pose. Avatar scale does not change this tracking basis.

On the scene owner, the VR player reads a coherent sample, updates the room-scale playspace compensation and queues its locomotion delta, then publishes slot sources and targets. Stored device-to-target offsets multiply the device world matrix exactly once. The solver owns calibrated child targets under their physical sources and publishes replacements transactionally; humanoid slots retain the raw source and its captured offset. Animation resets run in the normal animation tick, target publication runs in the normal scene tick, IK evaluates in the late animation tick, and spectator follow runs in the late scene tick after IK. Character-controller movement consumes its queued delta through its existing physics owner. Losing a bound hips tracker suspends room-scale compensation rather than treating head lean as locomotion.

The skeleton deliberately uses the simulation pose. The render owner may late-locate headset and controller view transforms, but it does not rerun IK or mutate live bones. Consequently late-located eye views can be newer than the rendered skeleton. This is an explicit latency tradeoff, not a claim of late-latched avatar animation. Hardware timing validation must measure that difference.

Each body slot keeps its physical identity through tracking loss. It briefly holds its target, then fades to zero weight; the same physical device can fade back without rebinding another device. Estimated body poses are not supplied by the current calibration owner. Unknown reference-space changes invalidate calibration; teleport, snap turn, avatar replacement, and session generation changes reset spectator history. Session restoration requires matching provider generation and reference-space identity.

### A tracker is not streaming

Check the per-identity tracker diagnostics in the smoke summary. Distinguish not enumerated, discovered but requiring a VR restart, unbound, inactive, stale, and tracking-lost states. Connect new trackers before explicitly restarting VR. If a runtime does not stream admitted persistent paths, record its version, extension revision, enumeration and pose results; do not silently switch to OpenVR or require player-side body assignments.

### Hand tracking extension is unavailable

SteamVR may not expose `XR_EXT_hand_tracking` for all hardware. The OpenXR path logs this explicitly and falls back to controller-derived finger summary where possible; it does not promise OpenVR skeleton data through OpenXR.
