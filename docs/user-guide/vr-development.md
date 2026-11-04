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
