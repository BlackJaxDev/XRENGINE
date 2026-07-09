# OpenXR Monado Recovery And Performance Iteration

Status: recovery fixed; performance profile improved; follow-ups remain

Run root: `Build/_AgentValidation/20260626-191812-openxr-monado-recovery-perf`

## Problem

Closing the Monado preview window terminates or disconnects the Monado IPC endpoint. The last failure log showed:

- `ERROR [ipc_send] WriteFile ... failed: 232 The pipe is being closed.`
- Monado IPC `XRT_ERROR_IPC_FAILURE`.
- Repeated `OpenXrGraphicsSessionException` while recreating the OpenXR session.

The inspected failing session was:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_19-08-16_pid33672`
- Monado initially started at `2026-06-26 19:08:18 -07:00`.
- OpenXR session began at `2026-06-26 19:08:31 -07:00`.
- Runtime loss started at `2026-06-26 19:14:28 -07:00` with `xrPollEvent failed: ErrorInstanceLost`.
- Session recreation then looped through `ErrorRuntimeFailure` and `RuntimeUnavailable`.

## Root Causes

- Monado service startup only ran during Unit Testing World settings normalization. After the preview close killed `monado-service.exe`, the OpenXR state machine retried against a dead runtime service.
- Runtime-loss reporting could downgrade `InstanceLostError` to `SessionLostError` when events arrived from different threads.
- Renderer-owned `XR_KHR_vulkan_enable2` bootstrap handles could survive OpenXR instance teardown, leaving stale Vulkan/OpenXR coupling across runtime restart.
- Vulkan timeline waits could attempt `ulong.MaxValue`, producing invalid wait payloads during loss/recovery.
- The direct Vulkan eye preview copy still ran when no desktop mirror/eye capture needed it.
- Resource-plan replacement used `DeviceWaitIdle`, and auto-exposure history preservation could allocate/copy during VR resource-plan churn.

## Fixes

- Added `RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer` and wired it to `UnitTestingWorldSettingsStore.TryEnsureMonadoServiceForCurrentProcess` for editor Unit Testing `VR.Mode=MonadoOpenXR`.
- Ensured Monado service during OpenXR runtime probe, session creation, and runtime-loss recovery.
- Added severity ordering and locking for OpenXR runtime-loss reasons.
- Invalidated renderer-owned OpenXR Vulkan bootstrap instances on OpenXR instance teardown and marked the Vulkan renderer device-lost so normal renderer recreation runs.
- Changed Vulkan timeline waits to finite polling and rejected `ulong.MaxValue`.
- Gated direct eye preview copy to explicit capture or desktop mirror composition.
- Deferred replaced resource-plan retirement through frame-slot queues instead of `DeviceWaitIdle`.
- Skipped auto-exposure history preservation on first plan, device loss, and VR.

## Validation

- Build passed:
  `Build/_AgentValidation/20260626-191812-openxr-monado-recovery-perf/logs/build-editor-after-skip-vr-autoexposure-preserve.log`
- Final build after adding the env override also passed:
  `Build/_AgentValidation/20260626-191812-openxr-monado-recovery-perf/logs/build-editor-after-renderwindows-env.log`
- Focused tests passed 37/37:
  `Build/_AgentValidation/20260626-191812-openxr-monado-recovery-perf/logs/test-openxr-vulkan-autoexposure-preserve-gate.log`
- Final focused tests passed 38/38:
  `Build/_AgentValidation/20260626-191812-openxr-monado-recovery-perf/logs/test-openxr-renderwindows-env.log`
- Live recovery session:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_20-05-28_pid32048`
  - First OpenXR session began at `2026-06-26 20:05:39 -07:00`.
  - Killed Monado service PID `35476` around `20:05:51`.
  - New Monado service PID `32356` started at `20:05:52`.
  - Second OpenXR session began at `20:05:54`.
  - No post-recovery `QueueSubmit returned ErrorDeviceLost`, `DeviceWaitIdle returned ErrorDeviceLost`, `wait_payload=18446744073709551615`, or one-shot command-buffer-after-device-loss signatures were found.

Remaining recovery noise:

- Forced teardown still reports Vulkan child-object validation warnings during the old device destroy path. These look like teardown noise after the recovery path has already succeeded, but should be cleaned up separately.

## RenderDoc Notes

- `rdc doctor` passed and RenderDoc 1.44 was available.
- Initial `rdc capture` attempts grabbed OpenGL frames because direct executable launch loaded stale output-folder settings.
- Copying the root Unit Testing settings into the editor output folder and pinning `XR_RUNTIME_JSON=D:\Documents\XRENGINE\Build\Deps\Monado\openxr_monado.json` produced a real Vulkan + Monado OpenXR run with RenderDoc's Vulkan layer loaded:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_20-18-05_pid39184`
- `rdc capture-trigger` and the RenderDoc hotkey did not produce a completed `.rdc` for the Vulkan/OpenXR target. The `.rdc` files currently under the run root are OpenGL startup/preview captures and are not useful Vulkan evidence.

## Performance Findings

The expensive profile was the local Unit Testing settings asking for desktop editing plus stereo preview while in VR:

- `PreviewStereoViews=true`
- `AllowDesktopEditing=true`
- `RenderWindowsWhileInVR=true`

That produced full desktop editor render frames alongside OpenXR eye frames. Baseline logs showed full-window frames around `170` frame ops and render hot paths commonly in `Vulkan.RecordPrimary.MainOpLoop` or `OpenXR.Vulkan.SubmitFenceWait`.

For the high-framerate local pass, the ignored local `Assets/UnitTestingWorldSettings.jsonc` was changed to:

- `PreviewStereoViews=false`
- `AllowDesktopEditing=false`
- `RenderWindowsWhileInVR=false`

The same no-desktop-preview profile can now be selected without editing local JSON by setting:

- `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=0`
- `XRE_UNIT_TEST_ALLOW_DESKTOP_EDITING_IN_VR=0`
- `XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR=0`

Validation session:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_20-22-14_pid44688`
- Settings applied: `RenderWindowsWhileInVR=False`, `VrMirrorComposeFromEyeTextures=True`.
- OpenXR session began at `2026-06-26 20:22:24 -07:00`.
- Frame-counter estimate: frame `1` to `11057` over `64.153s`, about `172.3 fps`.
- The old recovery/device-loss signatures did not reappear.

Remaining performance hotspots:

- The main post-optimization hotspots are still `OpenXR.Vulkan.SubmitFenceWait` and `Vulkan.RecordPrimary.MainOpLoop`.
- Occasional 100+ op frames remain, likely from shadow/model/render-resource work rather than desktop mirror composition.
- A successful Vulkan `.rdc` capture is still needed for pass/resource-level GPU inspection.
