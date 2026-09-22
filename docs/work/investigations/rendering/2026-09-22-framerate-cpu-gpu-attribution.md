# Vulkan Advanced pipeline frame-rate attribution

Status: Paused at the user's request. Initial CPU attribution captured; exact
GPU feature attribution and CPU leaf attribution remain open. No optimization
or test changes made. The requested HUD diagnostic labels were implemented.

## Reported problem

The supplied screenshot shows Vulkan, AdvancedRenderPipeline, CpuDirect at 6 Hz:
171.48 ms render interval, 59.09 ms dispatch, 45.36 ms Vulkan CPU frame time,
14.19 ms GPU, 91.57 ms render waiting for collect, 54.21 ms collect waiting
for render, 28.00 ms update interval and 33.33 ms fixed interval. Draw counters
show 14 calls, no multidraw, 511 triangles, and zero CPU fallbacks.

These independently sampled, overlapping counters are not an additive frame
breakdown. Exact feature and subsystem attribution requires runtime evidence.

## Scope and method

- Preserve the existing working-tree changes, including concurrent Advanced
  pipeline and editor work.
- Reproduce the configured Vulkan/Advanced/CpuDirect path in a named isolated
  editor session and inspect actual viewport images.
- Obtain CPU scope, frame lifecycle and GPU stage timings; measure controlled
  feature changes only after observing the warmed baseline.
- Separate startup, debug/diagnostic overhead, steady state, and teardown.
- Do not add or modify tests during the investigation.

Evidence root: `Build/_AgentValidation/20260922-164341-framerate-investigation/`.
Session: `perf-diagnosis-0922` (Debug initial reproduction).

## Initial configuration

The current generated settings select Vulkan with required backend, dynamic
rendering, AdvancedRenderPipeline, CpuDirect (`GPURenderDispatch=false`), TSR,
VSync off, unlimited render rate, 60 Hz update and 30 Hz fixed update. The enabled
model is static deferred Sponza at scale 0.01 and translation (-20, 0, 0), with
a shadow-casting directional light, skybox and ImGui. Animated model import is
disabled, even though animation/skinning preferences are enabled.

The user confirmed the screenshot came from the VS Code Unit Testing World
Debug launcher with the debugger attached. The isolated reproduction used Debug
without a debugger. GPU: NVIDIA GeForce RTX 4070 Laptop GPU. Output 1920x1080,
internal 1286x723, TSR scale 0.67. Vulkan validation was off; command labels were
on. No matched Release or attached-debugger A/B was performed.

## Findings and validation

### Reproduction and measurements

The first isolated build failed with missing generated `XREngine.Data` source
files. Retrying the same build succeeded with zero warnings/errors. This is a
build artifact issue, not evidence of the frame-rate root cause.

Viewed three composited screenshots from different camera positions. The first
view faced outside Sponza; the final view at (-20, 2, 4), looking toward
(-20, 2, -8), shows the blue-curtain Sponza interior. The views changed, confirming
fresh scene rendering. Initial Debug HUD captures showed 7 Hz, 132.82-163.75 ms
intervals, 60.60-73.55 ms dispatch, 49.73-59.77 ms Vulkan CPU time, and
72.15-90.07 ms render waiting for collect publication.

Two consecutive stationary 20-second windows used the same final camera and
settings. CPU profiling was enabled for the first window and disabled through a
session-only preference for the second. GPU dense timestamps were disabled in
both. Samples are diagnostic observations, not a controlled hardware benchmark:
no GPU clock/load isolation or alternating repeated pairs were performed.

| Metric (median ms) | CPU profiler on, 165 frames | CPU profiler off, 168 frames |
| --- | ---: | ---: |
| Present interval | 125.637 | 121.594 |
| Render dispatch | 51.458 | 50.984 |
| Render waiting for collect publication | 74.445 | 67.906 |
| Collect waiting for render release | 48.409 | 48.309 |
| Actual visibility collection | 2.547 | 2.513 |
| Vulkan CPU frame | 39.979 | 40.074 |
| Vulkan resource preparation | 7.632 | 7.702 |
| Scene command-buffer recording | 31.247 | 31.347 |
| Primary recording, inclusive | 28.274 | 28.503 |
| Primary command encoding, nested | 8.616 | 8.666 |
| Primary operation loop, nested | 7.653 | 7.713 |
| Primary prewarm | 1.164 | 1.175 |
| Native command-buffer end | 0.659 | 0.659 |
| Swapchain acquire | 0.016 | 0.012 |
| Submission | 0.371 | 0.371 |
| Present call | 0.063 | 0.062 |
| Update work | 0.160 | 0.155 |
| Fixed-update work | 0.024 | 0.020 |
| Coarse GPU duration, unique completed queries | 15.537 | 15.423 |

The GPU query sequences were deduplicated (165/168 unique completed queries).
GPU p95 was 33.506/28.564 ms. These GPU queries complete asynchronously and are
not necessarily the CPU frame on the same row. These inclusive/nested metrics
must not be added together.

Turning off CPU profiling did not eliminate the slow path: render dispatch and
Vulkan CPU medians barely changed. This one pair does not establish an exact
observer-overhead percentage. The screenshot's attached debugger is not required
to reproduce poor frame rate.

### CPU ownership established so far

Completed CPU profiler scopes repeatedly identify the serialized swap phase:

- `RenderCommandCollection.SwapBuffers.RenderPasses`: 65.66 ms in an early
  completed snapshot; later completed scopes were 58.063 and 42.272 ms, with
  additional collection instances of 15.724 and 2.182 ms in the same dump.
- `GpuIndirect.GPUScene.SwapCommandBuffers.AdvancedPublication`: 8.565-9.960 ms
  in sampled completed scopes.
- `UI.DrawPlayerCamerasPanel`: about 3.45-4.40 ms self time in sampled frames.
- `Advanced.VisibilityPreparation`: about 3.57-4.62 ms in sampled frames.

`EngineTimer` collects visibility, waits for render release, processes swap jobs,
swaps buffers, then publishes the next visibility generation. Thus the expensive
swap work blocks the next render even when geometric visibility collection is
cheap. The render-side wait is not a measurement of culling alone.

The expensive render-pass swap scope walks dirty commands and invokes
`RenderCommand.SwapBuffers` callbacks. The source path for mirrored scene meshes
is `RenderInfo.SwapBuffers` -> `VisualScene3D.OnRenderableSwapBuffers` ->
`GPUScene.TryUpdateMeshCommand`. That path resolves mesh/material residency and
updates metadata. Its internal leaf costs have **not** been isolated; do not yet
claim a particular lookup, hash, equality operation, or callback is the cause.

Vulkan primary recording also has a substantial unseparated preparation cost:
roughly 28 ms inclusive versus 8.6 ms command encoding and 1.2 ms prewarm in the
window medians. Potential owners include Advanced preparation and primary
publication/admission, but further instrumentation is required to assign the
remainder. Native acquire/submit/present and frame-slot waits were small in the
observed baseline, so this reproduction does not support a GPU fence/present
stall as the dominant source of the 120-170 ms interval.

### GPU and HUD limits

The live post-process state reports TSR, bloom, ambient occlusion and auto
exposure enabled; motion blur and depth of field disabled. Atmospheric settings
report enabled, but no atmosphere component is initialized, so that preference
alone does not prove an executed atmosphere pass. No individual effect was
disabled for a measured GPU A/B before the user requested wrap-up.

`get_render_profiler_stats` explicitly reported GPU command-level timing disabled
despite the GPU profiler preference being enabled: Vulkan requires
`XRE_GPU_TIMESTAMP_DENSE=1` for dense pass timing. Only coarse GPU timing was
collected. RenderDoc doctor passed; no RenderDoc frame was captured.

The scene contains 398 tracked renderables and 396 GPU scene commands, including
361 eligible opaque deferred commands. The HUD still reports 14 draws and 511
triangles. That generic counter stream does not fully describe Advanced native
scene work and must not be interpreted as the whole Sponza workload.

The original HUD's update/fixed rows are tick intervals, not execution costs.
The HUD updates at 4 Hz and reads separate last-value publications; FPS uses a
60-render-frame rolling window. Opposing thread waits and GPU/CPU values are not
a coherent additive single-frame breakdown.

### Requested HUD change

`XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs` now reports:

- Debug/Release build, pipeline-latched effective camera AA, and MSAA samples
  only when MSAA is active.
- Actual debugger attachment and CPU profiler frame-logging state.
- GPU pipeline timing status, explicitly disabled when Vulkan dense timestamps
  are off; dense mode and coarse Vulkan query state are separate.
- Actual Vulkan validation/synchronization telemetry and message/error counts.
  Other diagnostic flags are explicitly labeled **requested**, because the
  existing public telemetry does not prove their post-bootstrap activation.

The text still rebuilds at the existing 4 Hz cadence. Added lines have more
vertical room. The isolated Debug editor build with the HUD change succeeded
with zero warnings and zero errors (`logs/hud-build.log`). Its new layout has
not been viewed live because the user requested wrap-up before another launch.

### Evidence and next steps

The run's `reports/window-summary.json` contains sample counts, medians, means
and p95; `reports/windows.ndjson` records exact UTC windows. Saved `logs/` contain
the frame stream, manifest and CPU dumps. `mcp-output/` contains full telemetry,
feature-state and screenshot responses. Images are in `mcp-captures/`.

Next investigation should measure the dirty-command callback path and Advanced
publication internals, split the primary-recording preparation gap, then gather
dense GPU scopes with an observer-off comparison. A matched Release run and
one-feature-at-a-time GPU A/B remain necessary. Do not present these pending
steps as completed attribution.

The named editor session was stopped through the session manager, and useful
logs copied into the task evidence root. Only this session's runtime preferences
and camera were changed; persistent user settings were not changed. Broker
evidence review completed with requested and actual model both `gpt-5.6-sol`.

## Attempted solutions and user feedback

No optimization attempted. User reports the screenshot performance is poor;
no performance fix has been validated or reported working. User requested work
to wrap up before completing attribution and the HUD live layout check.
