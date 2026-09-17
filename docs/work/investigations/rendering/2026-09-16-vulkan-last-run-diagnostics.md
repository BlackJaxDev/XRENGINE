# Vulkan Last-Run Diagnostics

## Report

The September 16 run in
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-16_19-34-19_pid7888/`
recorded repeated missing accepted-plan picking authority, missing Advanced TSR
snapshots, and a probe generation unavailable during texture-array assembly after
entering Play mode. It also logged 240 attachment-metadata warnings and 590
unsupported GPU BVH warnings.

## Findings And Changes

- Deferrable background redraws do not own a desktop accepted plan. They may
  record visibility without publishing accepted-plan picking state. PresentNow
  recording still requires that authority; XR retains its existing exception.
- Deferred Advanced TAA/TSR bindings must read the pipeline-owned immutable
  snapshot, not resolve a new temporal key from a cleared ambient viewport.
- Probe readiness must include an active output generation, not only restored
  texture properties. A generation that becomes unavailable before retention
  defers the refresh, releases temporary references, and preserves the last
  publication with a diagnostic rather than using an exception for normal retry.
- The BVH dispatcher logged unsupported-backend warnings even with an empty
  queue. Idle processing now returns before that check. Actual unsupported
  geometric GPU raycast requests remain rejected; this does not implement Vulkan
  geometric BVH raycasts or substitute CPU results.
- Depth-only framebuffer targets legitimately lack optional stencil attachment
  usage. Planner validation now follows the backend's optional-stencil contract.
  Missing framebuffers, textures, depth, and color remain diagnostic failures.
- Atmosphere and fog quad materials have no source color attachments. Their four
  stages now declare actual sampled textures and output textures explicitly.
- Advanced editor picking now releases the shared in-flight gate on success and
  rejection, including any pending follow-up pick.
- Local player camera diagnostics distinguish an absent pawn from an unnamed
  component, using the scene-node name where available.

DLL-loading/symbol messages and the audio/pawn lifecycle events are normal.
The reported cancellation has no saved stack trace; it is not evidence of a
failure. First-chance logging remains enabled and throttled rather than globally
suppressed.

## Validation

- Focused rendering and Vulkan builds after each change: no errors reported.
- Two isolated full editor builds passed with zero warnings and zero errors.
- The first isolated Vulkan run exposed a separate Advanced atmosphere/fog
  command chain that still lacked resource descriptors. That chain was then
  corrected and rebuilt. The second run's inspected logs contained no matching
  attachment-planner or idle BVH warnings.
- Isolated viewport captures used FXAA, not TSR. They do not validate temporal
  quality or performance. Full runtime verification remains incomplete.
- Required runtime checks: Advanced Vulkan rendering and TSR history, repeated
  redraws and picking, Play entry/exit with probe recapture, attachment metadata,
  and no idle BVH warning spam. Inspect screenshots and session logs.
- No tests added or modified. Live feature validation precedes any test work.
- User confirmation: negative. On September 17 the user reported worse CPU
  framerate and TSR ghosting. Do not treat the temporal change as validated.

## September 17 Regression Report

The newer run is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-17_09-34-06_pid7452/`.

- Its `log_rendering.log` FPS overlay samples show approximately 14-15 Hz and
  repeated CPU command-recording stages around 153-165 ms. Successful GPU timing
  samples in the same interval are approximately 1.1-1.5 ms; zero GPU samples on
  long CPU frames must not be interpreted as zero GPU cost. The CPU cause is not
  yet attributed to a specific source change.
- Its `log_vulkan.log` presents `TsrOutputTexture`, and the inspected logs no
  longer contain the old missing temporal-snapshot warnings. The changed lookup
  can enable history blending that the prior failure path disabled. This makes
  history frame/view identity and reprojection the leading ghosting hypothesis,
  not a confirmed root cause. Quiet diagnostics are not proof of correct history.
- Releasing the Advanced picking gate restores repeated requests, but the
  dispatch code still gates stationary cursors and throttles ordinary hover
  requests. An idle runaway pick loop has not been demonstrated.
- The coordinator stopped only its named isolated editor when the regression
  was reported. Concurrent editor load is a possible confounder, not an
  established explanation for this run's recording stalls.
- Next discriminating checks are a controlled temporal-lookup A/B with identical
  scene, camera motion, and settings, plus a CPU profile of a long recording
  frame. Do not reduce history feedback, disable picking, or suppress warnings
  merely to conceal these regressions.

## Evidence

New captures and reports belong under
`Build/_AgentValidation/20260916-210000-last-run-fixes/`.
The named isolated session is `last-run-fixes-0916`.
