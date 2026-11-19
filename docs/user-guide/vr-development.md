# VR Development

[Back to user guide](README.md)

Use this page when configuring and testing VR projects. For code-facing VR APIs and components, see [VR Developer Guide](../developer-guides/vr/vr-development.md). For renderer internals, see [OpenVR Rendering](../architecture/rendering/openvr-rendering.md) and [OpenXR VR Rendering](../architecture/rendering/openxr-vr-rendering.md).

## Runtime Paths

XRENGINE has OpenXR and SteamVR/OpenVR paths. OpenVR is currently the tested path. OpenXR exists and is actively documented, but should be validated for the specific runtime and headset before relying on it.

VR can run in local, client, or server-oriented modes depending on whether tracking, rendering, and networking live in one process or are split across processes.

## Setup Checklist

- Choose the target runtime and renderer.
- Provide the action manifest and VR manifest expected by the runtime path.
- Start with local mode while validating tracking and input.
- Add a humanoid or VR player rig only after headset/controller poses are visible.
- Watch frame timing closely; VR work should target stable low-latency rendering.

## Frame Rate Limit

`VrTargetFramesPerSecond` sets the engine render limit while a VR runtime owns
presentation. It is in the project startup settings, and `UserSettings` has an
override. The default is null, which removes the engine limit. A value below 1
also removes the limit. While VR is active, this setting replaces the desktop
`TargetFramesPerSecond`, `UnfocusedTargetFramesPerSecond`, and VSync rules.
The engine applies the change when a VR session starts or stops.

On the desktop, when VSync is off and `TargetFramesPerSecond` is null or below
1, the engine limits rendering to 60 Hz, or to the primary display refresh rate
if that is lower. An uncapped or high-rate desktop keeps the GPU almost always
busy, and then the whole PC becomes slow. Set `TargetFramesPerSecond` for a
higher rate. Set `XRE_DESKTOP_RENDER_UNCAPPED=1` to remove this
limit, for example for desktop frame-rate benchmarks.

While no engine window has focus and `UnfocusedTargetFramesPerSecond` is null,
the desktop limit is 30 Hz. A window that opens behind another app starts in
this state. `XRE_DESKTOP_RENDER_UNCAPPED=1` also removes this limit.

In desktop mode, the editor sets its Windows GPU scheduling class to
BelowNormal, so games and the desktop get the GPU first. When the VR launch
mode is not `Desktop`, or while a VR runtime is active, the editor uses the
Normal class. At BelowNormal, a VR compositor at Normal gets the GPU ahead of
the editor, and the editor then submits no XR frames. Set
`XRE_EDITOR_NORMAL_GPU_PRIORITY=1` to always keep the Normal class.

The VR runtime always paces frames to the headset refresh rate through
`xrWaitFrame` (OpenXR) or `WaitGetPoses` (OpenVR). Thus the presented VR rate
cannot be higher than the headset refresh rate, even with no engine limit. To
present more frames, set a higher refresh rate in the runtime. For SteamVR, open
**Settings > Video** (or the headset-specific page) and select the refresh rate,
for example 120 Hz or 144 Hz, then restart the VR session.

To see how fast the engine could go without runtime pacing, read
`vr.headroom` in the MCP `get_render_profiler_stats` result. It reports:

- `engine_vr_limit_hz` and `active_timer_render_limit_hz`: the engine limit (0 is uncapped).
- `runtime_display_refresh_hz`: the rate that the runtime paces to.
- `unpaced_cpu_frame_ms`: the CPU time from the return of `xrWaitFrame` to the
  end of `xrEndFrame`, over the last 128 frames. The runtime wait is not included.
- `implied_unpaced_cpu_hz_median` and `implied_unpaced_cpu_hz_p95`: `1000 / unpaced_cpu_frame_ms`.
- `cpu_headroom_ratio_median`: the implied rate divided by the display rate. A
  value above 1 means that the CPU frame work fits in the display period.
- `gpu`: GPU hints. Eye command buffers do not have resolved GPU timestamps yet,
  so `eye_gpu_frame_ms` is null. A large `eye_completion_wait_ms` shows that the
  CPU waits for eye GPU work.

The implied rate does not include GPU work that finishes after `xrEndFrame`.

## Desktop View Rate In VR

With the `WorkerRecord` VR desktop frame mode, a render worker records the
desktop frame while the render thread renders the next VR frame. This mode
needs at least two render worker threads. When this mode is selected and the
render worker count is 0 or automatic, the engine uses two workers at startup.
An explicit count of 1 or more stays unchanged; with only one worker, every
desktop frame fails, and `desktop-execution-mode.log` records the failure.

`UserSettings.VrDesktopSplitSlackGate` controls when the next desktop frame
starts. The engine reads it each frame, so a change applies at once.

- Off (the default): the next desktop frame always starts. The desktop view can
  run at the VR rate, but a slow desktop frame can delay a VR frame.
- On: the next desktop frame starts only if the time left before the next VR
  frame can hold it. This protects VR frame deadlines, but the desktop view can
  run at half the VR rate or less.

`UserSettings.VrRenderPriority` selects which output gets render time first
while VR is active. The engine reads it each frame.

- `Vr` (the default): the headset comes first. A desktop frame runs only when
  it fits before the next headset frame, so the desktop view can drop to a low
  rate when the headset frames are slow.
- `Desktop`: every due desktop frame runs. This also turns off
  `VrDesktopSplitSlackGate`. Headset frames can be late, and the runtime
  reprojects them.

The FPS overlay shows the presented desktop rate and the OpenXR submitted rate,
so you can compare the two modes.

## VR Eye Post-Process Settings

The VR eyes have their own post-process settings (AO, bloom, color grading and the other
camera stages). They are separate from the desktop camera, and the left and right eye
always use the same settings:

- `VrEyeCameraSettingsLink` links the two eye cameras. OpenXR and OpenVR each own one link
  (`OpenXRAPI.EnsureOpenXrEyeSettingsOwnership`, `EngineVrLifecycle.LinkOpenVrEyeCameraSettings`).
- Both eye cameras use one post-process state collection.
- The per-camera render overrides (anti-aliasing mode, MSAA sample count, HDR output, TSR render
  scale, post-process material) are mirrored: a change on either eye camera is copied to the other
  at once. OpenXR also rewrites both eyes from the last edited eye each frame.
- A camera keeps one state per pipeline ID. In two-pass modes each eye has its own pipeline
  object, so the right eye pipeline names the left eye pipeline as its
  `RenderPipeline.PostProcessStateOwner`. Both eyes then read and write one state.
- Single-pass stereo uses one pipeline for both eyes and reads the left eye camera.
- The editor and MCP (`vr_eye` = `left`, `right` or `stereo`) edit this one eye state.
- Auto exposure is the only adaptation that can differ between the eyes. The exposure
  settings stay shared, but each eye pipeline keeps its own adapted exposure. The Color
  Grading setting **Independent Eye Exposure** (`IndependentEyeExposure`) selects the mode
  for two-pass eyes:
  - Off (default): one headset exposure. The left eye meters its own image, and the right
    eye uses the left eye's value (the same frame, or the previous frame when the right eye
    renders first).
  - On: each eye meters its own image and adapts on its own, as real eyes do. The eyes can
    then differ in brightness.
  Single-pass stereo always meters both eye layers together and ignores this setting.
  Vulkan and OpenGL both support the shared headset exposure, so it works for OpenVR and
  OpenXR two-pass eyes.
- The engine setting **VR Eye Features** (engine settings, category **VR**; `VrEyeFeatures`)
  turns optional features on or off for every VR eye frame, in every view mode and pipeline:
  ambient occlusion, fog and aerial perspective, order-independent transparency, depth of
  field, motion blur, deferred MSAA and Forward+ light culling. Depth of field and motion blur
  are off by default for comfort; the other features are on. A feature also needs the eye
  camera's own setting (for example an enabled ambient occlusion stage). The desktop camera
  never reads this profile. In the Unit Testing World, set `VR.EyeFeatures`.

## Testing

The Unit Testing World supports `VR.Mode = "Emulated"` for desktop stereo
rendering without a headset runtime. Its simulated eyes use a 64 mm baseline,
adjusted by the existing avatar scale and IPD scalar. This mode is explicitly
selected; it is not a fallback for a failed headset connection. With single-pass
stereo, MCP texture captures select the shared viewport with `vr_eye="stereo"`
and the left/right layer with `layer_index=0/1`. `get_render_state` accepts the
same viewport selector.

To start the ImGui editor on its desktop camera and enter VR later, leave
`VR.Mode` set to `Desktop` and keep `VR.AllowDesktopEditing` enabled. Enabling the
**OpenXR** checkbox asks you to choose **Monado (testing)** for no-headset testing
or **SteamVR (headset)** for real VR. Cancel keeps desktop control. The local
player switches to a VR pawn created on demand when the session runs and back to
the previous desktop pawn when it stops. The temporary VR rig is then destroyed;
the world does not need prebuilt pawns. Temporary rigs belong to the editor scene.
Changing runtime recreates the renderer while preserving the world and desktop
pawn. Missing runtimes produce an error rather than choosing another runtime.

Always test on real hardware before trusting comfort, performance, tracking, or input behavior. Desktop preview can verify scene setup but cannot validate motion-to-photon latency or headset runtime behavior.

## Deeper Docs

- [VR Developer Guide](../developer-guides/vr/vr-development.md)
- [OpenXR Runtime](../developer-guides/vr/openxr-runtime.md)
- [OpenVR Rendering](../architecture/rendering/openvr-rendering.md)
- [OpenXR VR Rendering](../architecture/rendering/openxr-vr-rendering.md)
