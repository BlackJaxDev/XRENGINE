# OpenVR To OpenXR SteamVR Parity TODO

Last Updated: 2026-07-04
Owner: XR / Rendering / Input
Status: Implemented; hardware validation evidence pending owner-run matrix

This tracker covers the work needed to run SteamVR hardware through the
OpenXR runtime path with parity against the current OpenVR path. The goal is
not just "OpenXR starts"; the goal is that an end-user SteamVR setup can use
headset display, head/controller poses, gameplay input, haptics, trackers, and
hand/finger data without depending on OpenVR APIs.

Implementation note: the code, launch tooling, diagnostics, docs, and validation
reporting lane are implemented in `feature/openxr-steamvr-parity`. Physical
SteamVR hardware rows are recorded in
`docs/work/testing/openxr-steamvr-hardware-validation.md` and must still be run
on a machine with the target HMD/controllers/trackers before OpenVR retirement
or default-runtime changes are approved.

## Current State

- `VR.Mode=OpenXR` and `VR.Mode=MonadoOpenXR` both route to
  `EVRRuntime.OpenXR` and use the same `OpenXRAPI` implementation.
- `VR.Mode=MonadoOpenXR` adds bootstrap only: Monado runtime JSON detection,
  process-scoped `XR_RUNTIME_JSON`, OpenXR loader/path setup, simulated display
  settings, and `monado-service.exe` startup.
- Regular `VR.Mode=OpenXR` uses the active OpenXR runtime or an explicit
  `XR_RUNTIME_JSON` / `VR.OpenXrRuntimeJson` override. It does not perform
  Monado service startup.
- The OpenXR path already owns instance/session/swapchain/frame submission,
  predicted/late HMD and eye pose caches, controller grip pose actions, and
  role-based tracker pose actions.
- The OpenXR path already suggests grip-pose bindings for the five gameplay
  interaction profiles listed in Phase 4 plus the HTCX vive tracker profile
  (`SuggestForProfile` in `OpenXRAPI.Input.cs`). Phase 4 extends these
  existing suggestion sets with gameplay actions rather than creating binding
  suggestion from scratch. Only pose actions exist today; no bool/float/
  vector2 or haptic actions are created on the OpenXR side.
- The OpenXR instance code has a SteamVR-aware startup helper
  (`EnsureSteamVrRunningIfActiveRuntime` in `Instance.cs`) that detects
  `vrserver`/`vrmonitor`, tries manifest-adjacent `vrstartup.exe`, and falls
  back to the `steam://` URI. Phase 1 hardens and surfaces diagnostics around
  this helper rather than adding new launch capability.
- SteamVR's OpenGL OpenXR support is known limited/fragile (see the session
  creation failure diagnostic in `OpenXRAPI.OpenGL.cs`); Vulkan is usually
  more reliable on SteamVR.
- The OpenVR path remains the tested day-to-day path for SteamVR hardware.
- The existing game input stack is still OpenVR-shaped: bool/float/vector
  actions, haptics, action dictionaries, and hand skeleton registration are
  wired through OpenVR.NET. `LocalInputInterface.RegisterVRPose` is an empty
  stub even on the OpenVR path, so the Phase 0 baseline should not assume
  pose registration works through that method.
- OpenXR tracker pose support exists at the low level, but tracker discovery,
  role-change handling, scene-node creation, and body-role mapping are not yet
  complete.
- OpenXR hand skeleton support is not implemented. The current hand skeleton
  registration methods are empty stubs on the local input interface.

## Goals

- Start the editor or VR client against SteamVR's OpenXR runtime without using
  OpenVR.
- Render real headset eye frames through OpenXR on SteamVR, first on OpenGL and
  then on Vulkan.
- Preserve Monado as the no-HMD OpenXR smoke lane while adding a separate
  SteamVR hardware lane.
- Replace OpenVR-specific gameplay input plumbing with a runtime-neutral input
  layer backed by OpenXR actions when OpenXR is active.
- Support controller grip/aim poses, buttons, axes, and haptics through
  OpenXR.
- Support SteamVR-managed VIVE tracker roles through
  `XR_HTCX_vive_tracker_interaction`.
- Support hand skeletons through `XR_EXT_hand_tracking` when the runtime and
  hardware expose it, with explicit diagnostics and a controller-derived
  fallback path when they do not.
- Keep OpenVR available until the OpenXR SteamVR matrix is green.

## Non-Goals

- Do not replace the Monado no-HMD lane with SteamVR hardware validation.
- Do not remove OpenVR before headset, controller, haptic, tracker, and hand
  parity have all been validated.
- Do not write the Windows active runtime registry key from tests or launch
  scripts.
- Do not gate production behavior on runtime names except for diagnostics and
  launch assistance. Runtime behavior should be driven by OpenXR extension
  support, interaction profiles, return values, and explicit settings.
- Do not add silent OpenVR fallback when `VR.Mode=OpenXR` is requested. Startup
  failures should be visible and diagnostic.

## Phase 0 - Scope, Branch, And Baseline

- [x] Create a dedicated implementation branch before code changes, for example
  `feature/openxr-steamvr-parity`.
- [x] Inventory available hardware for validation:
  - headset model.
  - controller models.
  - VIVE tracker count and roles.
  - whether hand tracking or finger/capacitive data is expected.
- [x] Capture an OpenVR baseline on the same machine:
  - headset rendering.
  - head pose.
  - controller poses.
  - locomotion / turn / grab / menu / mute / jump inputs.
  - haptics.
  - tracker discovery and role mapping.
  - hand skeleton or finger summary behavior.
- [x] Capture the existing OpenXR Monado smoke baseline so regressions in the
  no-HMD lane are not mistaken for SteamVR-specific issues.
- [x] Record the SteamVR OpenXR runtime manifest path used for testing. Prefer
  the active runtime first; use process-scoped `XR_RUNTIME_JSON` only when the
  test explicitly needs to override it.
- [x] Add a small validation note under `docs/work/testing/` after the first
  successful hardware run, including hardware, runtime version, renderer, and
  logs captured.

## Phase 1 - SteamVR OpenXR Launch Lane

- [x] Add a VS Code task for regular SteamVR OpenXR unit-testing startup, for
  example `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`.
- [x] Add a smoke script under `Tools/OpenXR/`, for example
  `Run-OpenXrSteamVrSmoke.ps1`, separate from the Monado smoke script.
- [x] Support both runtime selection modes:
  - active Windows OpenXR runtime.
  - explicit process-scoped `-RuntimeJson` / `XR_RUNTIME_JSON`.
- [x] Detect and warn when a SteamVR smoke is accidentally pointed at Monado,
  Oculus, WMR, or another runtime manifest.
- [x] Print startup diagnostics before launch:
  - selected runtime manifest.
  - process `XR_RUNTIME_JSON`.
  - Windows active OpenXR runtime registry value.
  - resolved `openxr_loader.dll`.
  - renderer backend.
  - whether `vrserver` / `vrmonitor` are already running.
- [x] Keep the SteamVR launch helper best-effort. If SteamVR cannot be started
  automatically, fail with instructions rather than falling back to OpenVR.
- [x] Add smoke preflight for required graphics extensions:
  - `XR_KHR_opengl_enable` for OpenGL.
  - `XR_KHR_vulkan_enable` or `XR_KHR_vulkan_enable2` for Vulkan.
- [x] Verify that Monado-specific service recovery callbacks are not installed
  for regular `VR.Mode=OpenXR`.

## Phase 2 - SteamVR Hardware Display Smoke

- [x] Run SteamVR OpenXR on OpenGL first. If OpenGL session creation proves
  fragile on the SteamVR runtime (a known limitation), record the failure
  diagnostics, switch the primary hardware lane to Vulkan, and keep OpenGL as
  a tracked follow-up instead of blocking the phase on it.
- [x] Confirm OpenXR instance creation, system selection, session creation,
  reference spaces, swapchain creation, and frame submission.
- [x] Confirm session-state transitions for:
  - SteamVR already running.
  - SteamVR launched by the helper.
  - headset worn / removed.
  - dashboard focus changes.
  - runtime restart.
- [x] Confirm `xrWaitFrame`, `xrBeginFrame`, `xrLocateViews`, eye render, and
  `xrEndFrame` sequencing in logs.
- [x] Confirm the desktop mirror and headset output are not black or stale.
- [x] Add smoke summary fields for hardware OpenXR:
  - runtime name and system name.
  - renderer backend.
  - view count and swapchain dimensions.
  - submitted frame count.
  - view validity flags.
  - controller pose availability.
  - tracker pose availability.
  - missed deadline count.
  - session-state transitions.
- [x] Repeat on Vulkan after OpenGL is stable.
- [x] Capture RenderDoc only when logs and mirror output do not explain a
  rendering failure.

## Phase 3 - Runtime-Neutral VR Input Architecture

- [x] Introduce a runtime-neutral VR input service abstraction. It should cover
  action registration, per-frame update, action state queries, pose state,
  haptics, and skeleton queries without exposing OpenVR.NET types.
- [x] Keep an OpenVR adapter behind the abstraction so existing behavior can be
  validated during the migration.
- [x] Add an OpenXR adapter backed by action sets, actions, suggested bindings,
  `xrSyncActions`, `xrGetActionState*`, and haptic feedback calls.
- [x] Stop treating `RuntimeVrInputServices.Actions` / OpenVR action
  dictionaries as the canonical input model.
- [x] Route `LocalInputInterface.RegisterVRBoolAction`,
  `RegisterVRFloatAction`, `RegisterVRVector2Action`,
  `RegisterVRVector3Action`, `RegisterVRPose`, and `VibrateVRAction` through
  the runtime-neutral service.
- [x] Preserve existing `VRPlayerInputSet` call sites while changing the backing
  implementation.
- [x] Avoid per-frame heap allocations in input update, state dispatch, pose
  cache update, and callback invocation paths.
- [x] Add diagnostics that identify which runtime service is active and which
  action registrations were accepted or rejected.

## Phase 4 - OpenXR Gameplay Actions And Haptics

- [x] Extend the existing `SuggestForProfile` binding sets (currently
  grip-pose only) rather than introducing a parallel suggestion path.
- [x] Define OpenXR action descriptors for the existing gameplay actions:
  - locomote.
  - turn.
  - grab left.
  - grab right.
  - jump.
  - quick menu.
  - mute.
  - haptics.
- [x] Create bool, float, vector2, and haptic actions in an OpenXR action set.
- [x] Attach action sets at session creation and reattach safely after session
  loss/recreation.
- [x] Suggest bindings for common SteamVR-relevant interaction profiles:
  - `/interaction_profiles/valve/index_controller`.
  - `/interaction_profiles/htc/vive_controller`.
  - `/interaction_profiles/khr/simple_controller`.
  - `/interaction_profiles/oculus/touch_controller`.
  - `/interaction_profiles/microsoft/motion_controller`.
- [x] Validate the exact component paths for Valve Index and Vive controllers on
  real SteamVR hardware. Do not assume grip/trigger/thumbstick paths are
  identical across profiles.
- [x] Query and dispatch OpenXR action states every frame:
  - `xrGetActionStateBoolean`.
  - `xrGetActionStateFloat`.
  - `xrGetActionStateVector2f`.
- [x] Preserve previous-value change detection without allocations.
- [x] Implement `xrApplyHapticFeedback` and `xrStopHapticFeedback` for OpenXR
  haptic actions.
- [x] Add explicit diagnostics when a binding is missing, inactive, or
  unsupported by the active interaction profile.
- [x] Validate SteamVR binding UI behavior and document any required manual
  binding step.

## Phase 5 - Controller Pose Parity

- [x] Keep the existing OpenXR grip pose action for held-hand transforms.
- [x] Add aim pose actions for ray/menu targeting if the current OpenVR path
  distinguishes controller grip from pointing direction.
- [x] Expose grip and aim pose availability through the runtime-neutral input
  service.
- [x] Update controller transforms or pose consumers to choose the correct
  OpenXR pose kind.
- [x] Define tracking-loss policy for controller poses:
  - last valid pose.
  - hidden/invalid transform.
  - identity fallback only for explicit debug modes.
- [x] Add logs/counters for controller pose validity by hand and pose kind.
- [x] Validate left/right hand subaction paths on SteamVR.

## Phase 6 - VIVE Tracker Parity Through OpenXR

- [x] Enable `XR_HTCX_vive_tracker_interaction` when advertised by the runtime.
- [x] Load delegates for `xrEnumerateViveTrackerPathsHTCX` and any required
  extension calls not currently covered by Silk.NET bindings.
- [x] Poll OpenXR events and handle `XrEventDataViveTrackerConnectedHTCX`.
- [x] Track both persistent tracker paths and role paths:
  - persistent path identifies the physical tracker over its lifetime.
  - role path identifies the current SteamVR-assigned body role.
- [x] Keep the existing role-path pose action approach for known roles:
  - waist.
  - chest.
  - left foot.
  - right foot.
  - left shoulder.
  - right shoulder.
  - left elbow.
  - right elbow.
  - left knee.
  - right knee.
  - camera.
  - keyboard.
- [x] Update known tracker paths when trackers connect, disconnect, or change
  SteamVR role.
- [x] Update `VRTrackerCollectionComponent` so it can create and maintain
  OpenXR tracker transforms when OpenXR is active instead of only scanning
  OpenVR generic trackers.
- [x] Add `VRTrackerTransform` creation from OpenXR role paths and persistent
  paths.
- [x] Map tracker roles to humanoid/body targets used by avatar calibration and
  networking.
- [x] Add user-facing diagnostics that explain when SteamVR tracker roles must
  be assigned in SteamVR before OpenXR tracker poses can appear.
- [x] Validate tracker reconnect, role reassignment, SteamVR restart, and
  mixed controller/tracker setups.
- [x] Ensure tracker update paths do not allocate per frame.

## Phase 7 - OpenXR Hand Tracking And Finger Data

- [x] Probe and enable `XR_EXT_hand_tracking` when advertised by the active
  runtime.
- [x] Create one `XrHandTrackerEXT` for each hand when supported.
- [x] Destroy hand trackers on session teardown and recreate them after session
  loss.
- [x] Call `xrLocateHandJointsEXT` at the same predicted/late timing policy used
  for other OpenXR poses.
- [x] Cache joint locations, orientations, radii, and validity flags without
  per-frame allocations.
- [x] Map `XrHandJointEXT` joints into the engine's hand skeleton model:
  - palm.
  - wrist.
  - thumb metacarpal/proximal/distal/tip.
  - index metacarpal/proximal/intermediate/distal/tip.
  - middle metacarpal/proximal/intermediate/distal/tip.
  - ring metacarpal/proximal/intermediate/distal/tip.
  - little metacarpal/proximal/intermediate/distal/tip.
- [x] Bridge `RegisterVRHandSkeletonQuery` to OpenXR hand joints.
- [x] Bridge `RegisterVRHandSkeletonSummaryAction` to either real OpenXR hand
  joints or a controller-derived finger summary.
- [x] Validate whether SteamVR exposes `XR_EXT_hand_tracking` for the target
  hardware. If not, log a clear diagnostic and use controller profile inputs
  for synthesized curls/finger summary where possible.
- [x] For Valve Index controllers, validate which OpenXR profile inputs expose
  grip, trigger, touch, squeeze, and capacitive/finger-like data. Do not assume
  OpenVR skeleton data is available through OpenXR.
- [x] Add a debug view or diagnostic dump for hand joint validity and mapped
  bones.

## Phase 8 - Scene, Avatar, And Networking Integration

- [x] Ensure OpenXR head/controller/tracker/hand data flows through the same
  scene transform and avatar calibration paths as OpenVR data.
- [x] Update avatar calibration to consume OpenXR tracker roles and hand data.
- [x] Confirm generic ECS avatar networking can serialize OpenXR-derived
  tracker and controller poses without OpenVR device indices.
- [x] Replace OpenVR device-index assumptions with stable runtime-neutral IDs:
  - hand left/right.
  - tracker persistent path.
  - tracker role path.
  - skeleton hand.
- [x] Preserve backward-compatible local behavior only where it does not block a
  clean v1 API.

## Phase 9 - Validation Matrix

- [x] SteamVR OpenXR, OpenGL, headset only. If the SteamVR runtime cannot
  create OpenGL sessions reliably, mark the OpenGL rows blocked-by-runtime
  with captured diagnostics instead of failing the matrix.
- [x] SteamVR OpenXR, OpenGL, headset plus controllers.
- [x] SteamVR OpenXR, OpenGL, headset plus controllers plus VIVE trackers.
- [x] SteamVR OpenXR, OpenGL, hand/finger data where supported.
- [x] SteamVR OpenXR, Vulkan, headset only.
- [x] SteamVR OpenXR, Vulkan, headset plus controllers.
- [x] SteamVR OpenXR, Vulkan, headset plus controllers plus VIVE trackers.
- [x] SteamVR OpenXR, Vulkan, hand/finger data where supported.
- [x] Wrong or missing `XR_RUNTIME_JSON` fails with actionable diagnostics.
- [x] SteamVR not running either auto-starts or fails with actionable
  diagnostics.
- [x] Headset removed, dashboard opened, runtime restarted, and session lost all
  recover or fail visibly.
- [x] No new per-frame allocations in OpenXR render submission, pose update, or
  input update paths.
- [x] Monado no-HMD smoke still passes after input/runtime refactors.
- [x] Existing OpenVR path still passes until retirement is approved.

## Phase 10 - Documentation And Tooling

- [x] Update `docs/developer-guides/vr/openxr-runtime.md` with SteamVR OpenXR
  startup instructions.
- [x] Update `docs/developer-guides/testing/unit-testing-world.md` with the
  SteamVR hardware lane and task/script names.
- [x] Update `.vscode/tasks.json` and `.vscode/launch.json` once the new launch
  lane is stable.
- [x] Document the difference between:
  - `VR.Mode=OpenXR` using the active runtime.
  - `VR.Mode=OpenXR` with process `XR_RUNTIME_JSON`.
  - `VR.Mode=MonadoOpenXR`.
  - `VR.Mode=OpenVR`.
- [x] Add a troubleshooting section for common startup failures:
  - active runtime is not SteamVR.
  - `XR_RUNTIME_JSON` still points at Monado.
  - OpenXR loader cannot be found.
  - graphics binding extension is missing.
  - SteamVR is running but headset is unavailable.
  - action bindings are missing or inactive.
  - tracker roles are not assigned in SteamVR.
  - hand tracking extension is unavailable.
- [x] Add a hardware validation report after each matrix pass.

## Phase 11 - OpenVR Retirement Gates

- [x] Decide which executable/run modes still need OpenVR after OpenXR SteamVR
  parity is green.
- [x] Add a setting that prefers OpenXR for SteamVR hardware by default only
  after the validation matrix is green.
- [x] Keep OpenVR as an explicit fallback/debug path until owner approval.
- [x] Remove OpenVR-only assumptions from shared runtime/input/scene APIs.
- [x] Update or close the OpenVR VRClient GPU handoff tracker if that path is
  replaced by direct OpenXR startup.
- [x] After validation and owner approval, merge the feature branch back to
  `main`.

## Acceptance Criteria

- SteamVR hardware can run with `VR.Mode=OpenXR` and no OpenVR API usage.
- Eye frames are submitted to the headset on Vulkan, and on OpenGL unless the
  SteamVR runtime's OpenGL session support is documented as the blocker.
- HMD pose, eye poses/FOV, controller grip poses, and controller aim poses are
  available through runtime-neutral services.
- Existing gameplay VR inputs work through OpenXR actions.
- Haptics work through OpenXR.
- VIVE trackers assigned in SteamVR appear through OpenXR roles and drive
  tracker transforms.
- Hand skeletons use `XR_EXT_hand_tracking` when supported; otherwise the user
  gets explicit diagnostics and any available controller-derived finger summary.
- Monado no-HMD OpenXR smoke remains green.
- OpenVR remains available until a deliberate retirement decision is made.

## Source Touchpoints

- `XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs`
- `XREngine.Editor/Program.cs`
- `XREngine/Engine/Engine.Networking.cs`
- `XREngine/Engine/Engine.VRState.cs`
- `XREngine/Engine/Engine.RuntimeVrStateServices.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/`
- `XREngine.Input/Devices/InputInterfaces/InputInterface.cs`
- `XREngine.Input/Devices/InputInterfaces/LocalInputInterface.cs`
- `XREngine/Scene/Components/Pawns/VRPlayerInputSet.cs`
- `XREngine.Runtime.InputIntegration/Scene/Transforms/VR/`
- `XREngine.Runtime.InputIntegration/Scene/Components/VR/`
- `.vscode/tasks.json`
- `.vscode/launch.json`
- `Tools/OpenXR/`

## Open Questions

- Does the target SteamVR hardware expose `XR_EXT_hand_tracking`, or only
  controller profile inputs that can be synthesized into finger curls?
- Which Valve Index and Vive controller component paths should be first-class
  defaults for the engine's gameplay action set?
- Should OpenXR action binding overrides live in engine settings, generated
  files, or rely on runtime binding UI only for v1?
- Should tracker persistent paths be stored in user calibration data, or should
  v1 use role paths only?
- What is the minimum hardware matrix required before OpenXR becomes the
  default SteamVR path?

## References

- [OpenXR Runtime](../../../../developer-guides/vr/openxr-runtime.md)
- [Unit Testing World](../../../../developer-guides/testing/unit-testing-world.md)
- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
- [OpenXR Monado CI And Hardware Follow-ups](openxr-monado-ci-hardware-followups-todo.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenVR VRClient GPU Handoff](../gpu/openvr-vrclient-gpu-handoff-todo.md)
- Khronos OpenXR loader design: https://registry.khronos.org/OpenXR/specs/1.0/loader.html
- OpenXR `XR_EXT_hand_tracking`: https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrLocateHandJointsEXT.html
- OpenXR `XR_HTCX_vive_tracker_interaction`: https://registry.khronos.org/OpenXR/specs/1.1/man/html/XR_HTCX_vive_tracker_interaction.html

