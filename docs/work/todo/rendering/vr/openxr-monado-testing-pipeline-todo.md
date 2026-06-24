# OpenXR Monado Testing Pipeline TODO

Last Updated: 2026-06-24
Owner: XR / Rendering / Testing
Status: Implemented + validation
Target Branch: `openxr-monado-testing-pipeline`

Design source:

- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [Unit Testing World](../../../../developer-guides/testing/unit-testing-world.md)

## Goal

Add a repeatable no-headset OpenXR validation lane backed by Monado's
simulated runtime, while keeping the existing non-API scene-only VR lane as the
fast editor and pawn smoke path.

The Monado lane must use the real OpenXR loader, select the runtime per process
with `XR_RUNTIME_JSON`, exercise instance/session/swapchain/frame submission,
and fail with enough diagnostics to explain where startup or submission broke.

## Scope

- Lane 0 C# contract tests for OpenXR timing and state-machine invariants.
- Lane 1 non-API scene-only VR testing through `VR.Mode=Emulated` and
  `InitRenderEmulated`.
- Lane 2 Monado-backed OpenXR smoke testing through `VR.Mode=MonadoOpenXR`
  and the standard loader.
- OpenGL first for Lane 2; Vulkan follows only after extension probing confirms
  support in the selected Monado build.
- Local developer scripts first, then optional CI once dependency and runner
  ownership are approved.

## Non-Goals

- Do not replace SteamVR, Oculus/Meta, WMR, or other hardware validation.
- Do not mutate the Windows active-runtime registry from tests or scripts.
- Do not make Monado a normal editor startup dependency.
- Do not treat Monado on Windows as a physical-HMD compatibility target.
- Do not build an XREngine-owned mock runtime before proving Monado and API
  layers cannot provide the required control.

## Phase 0 - Branch, Baseline, And Lane Names

- [x] Create dedicated branch `openxr-monado-testing-pipeline`.
- [x] Link this tracker from the design doc, work index, OpenXR timing tests,
  and OpenXR future-work tracker.
- [x] Inventory the existing startup hooks: `UnitTestingWorldSettings.VR.Mode`,
  legacy `VRPawn`/`UseOpenXR`/`SceneOnlyVRPawn` flags,
  `VR.PreviewStereoViews`,
  `Engine.InitializeVR(...)`, `VRState.InitializeOpenXR(...)`,
  `InitRenderEmulated(...)`, and existing `XR_RUNTIME_JSON` diagnostics.
- [x] Update Unit Testing World docs to distinguish Lane 1 scene-only VR from
  Lane 2 Monado OpenXR runtime testing.
- [x] Document the minimum settings for Lane 1:

  ```jsonc
  {
    "VR": {
      "Mode": "Emulated",
      "PreviewStereoViews": true,
      "AllowDesktopEditing": true,
      "OpenXrRuntimeJson": null
    }
  }
  ```

- [x] Document the minimum settings for Lane 2:

  ```jsonc
  {
    "VR": {
      "Mode": "MonadoOpenXR",
      "PreviewStereoViews": true,
      "AllowDesktopEditing": true,
      "OpenXrRuntimeJson": null
    }
  }
  ```

  `OpenXrRuntimeJson=null` auto-detects common Monado install/build paths for
  `VR.Mode=MonadoOpenXR`; explicit paths and `XR_RUNTIME_JSON` still win.

Acceptance criteria:

- [x] Repository docs make it clear that `VR.Mode=Emulated` does not emulate the
  OpenXR API.
- [x] Future OpenXR changes can identify whether they require Lane 0, Lane 1,
  Lane 2, or hardware validation.

## Phase 1 - Local Monado Smoke Runner

- [x] Add `Tools/OpenXR/Find-MonadoRuntime.ps1`.
- [x] Support `-RuntimeJson` as the authoritative runtime manifest override.
- [x] Search common Monado build/install paths only when no override is
  supplied.
- [x] Parse the runtime manifest with structured JSON parsing and resolve
  `runtime.library_path` relative to the manifest file when needed.
- [x] Validate that the manifest and runtime DLL exist; fail with actionable
  setup guidance.
- [x] Ensure the discovery script never edits
  `HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime`.
- [x] Add `Tools/OpenXR/Start-MonadoService.ps1` if the chosen Monado build
  needs an out-of-process service.
- [x] Track Monado service ownership with a marker containing PID, start time,
  manifest path, and `ownedByRunner`.
- [x] Make teardown idempotent and kill only services started by the runner.
- [x] Add `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1`.
- [x] Resolve `XR_RUNTIME_JSON`, set it only for the child process, and launch
  Unit Testing World through the editor.
- [x] Run a loader preflight before editor startup:
  `xrEnumerateApiLayerProperties` and `xrEnumerateInstanceExtensionProperties`.
- [x] Fail early when the selected renderer lacks the required OpenXR graphics
  binding: `XR_KHR_opengl_enable` for OpenGL, `XR_KHR_vulkan_enable` or
  `XR_KHR_vulkan_enable2` for Vulkan.
- [x] Start with OpenGL as the only required Lane 2 renderer.
- [x] Capture or identify the `Build/Logs/...` session used for assertions.
- [x] Assert the first lifecycle markers from logs: loader resolved, runtime
  manifest selected, instance created, system found, session created,
  swapchains created, first views located, and first frame submitted.

Acceptance criteria:

- [x] A developer can run one script with an explicit Monado runtime manifest
  and get a pass/fail result without changing global OpenXR runtime settings.
- [x] Failures before editor launch report the missing file, loader, runtime
  manifest field, runtime DLL, or graphics extension.

## Phase 2 - Bounded Editor Smoke Exit

- [x] Add `--smoke-frames N` CLI support.
- [x] Add matching `XRE_SMOKE_FRAMES` environment-variable support.
- [x] Count successfully submitted `xrEndFrame` calls for the active OpenXR
  session.
- [x] After N submitted frames, request session exit, drain session state to
  `XR_SESSION_STATE_EXITING` or `XR_SESSION_STATE_IDLE`, destroy swapchains,
  destroy the session, destroy the instance, and exit cleanly.
- [x] Return process exit code 0 only when the smoke criteria pass.
- [x] Return stable non-zero exit codes for preflight failure, startup failure,
  frame-submission failure, summary/assertion failure, and teardown failure.
- [x] Ensure the bounded exit path is local-only until it reliably avoids
  hanging the editor.

Acceptance criteria:

- [x] Monado smoke runs can finish without human interaction.
- [x] A failed smoke run cannot hang indefinitely in the editor process.

## Phase 3 - Settings And Launch Hygiene

- [x] Add a named Unit Testing World profile or launch task for Monado.
- [x] Add VS Code tasks only after the scripts are stable:
  `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`,
  `Test-OpenXR-Monado-Smoke`, and `Test-OpenXR-SceneOnlyVR-Smoke`.
- [x] Add grouped `VR.Mode` settings so desktop, scene-only emulated VR,
  Monado-backed OpenXR, OpenVR, and OpenXR launches are selected directly.
- [x] Keep legacy flat `VRPawn`, `UseOpenXR`, and `SceneOnlyVRPawn` values as
  compatibility inputs that normalize into `VR.Mode` when the grouped object is
  absent.
- [x] Select `SceneOnlyVRPawn` as the internal name for the no-runtime VR scene
  lane.
- [x] Expose the user-facing launch choice through `VR.Mode` across settings,
  docs, schema, tasks, scripts, tests, and environment overrides.
- [ ] If repeated local use needs persistent settings, add
  `OpenXrRuntimeJson`, `OpenXrExpectedRuntimeName`,
  `OpenXrRequireMockRuntime`, and `OpenXrSmokeFrameCount`.
- [ ] If those settings derive from `XRBase`, implement setters with
  `SetField(...)`.
- [x] Ensure an existing process environment value for `XR_RUNTIME_JSON` wins
  over any XREngine-specific setting.

Acceptance criteria:

- [x] Launch configuration expresses the selected lane without requiring a
  developer to remember flag combinations.
- [x] Generated settings express the selected VR launch path with one `VR.Mode`
  value instead of requiring a boolean combination.

## Phase 4 - Diagnostics, Summary, And Assertions

- [x] Emit a structured OpenXR smoke summary with `schemaVersion`.
- [x] Include runtime manifest path, runtime name/version, renderer, enabled
  extension list, reference-space type, swapchain metadata, session state
  transitions, located view count, submitted frame count, and failure flags.
- [x] Record per-eye swapchain acquire/wait/release counts.
- [x] Record first successful predicted and late pose-cache updates.
- [x] Record first desktop mirror composition.
- [x] Add `perFrameAllocationsBytes` or a recorded allocation baseline for the
  OpenXR submission hot path.
- [x] Wire `Tools/Reports/Find-NewAllocations.ps1` and/or profiler allocation
  evidence into the assertion step.
- [x] Teach the runner to fail on missing required summary fields.
- [x] Bump `schemaVersion` whenever required fields or field meanings change.
- [x] Store runner-owned Monado service diagnostics under the run's log session.

Acceptance criteria:

- [x] A passing summary proves instance, system, session, swapchain, view locate,
  frame submit, pose-cache, and mirror milestones occurred.
- [x] A failing summary points to a stable failure class without needing a
  debugger.
- [x] The OpenXR frame-submission hot path introduces no new unmanaged or
  managed per-frame allocations unless a justified baseline is recorded.

## Phase 5 - CI Candidate And Dependency Hygiene

- [x] Keep the first implementation local-only.
- [x] Move CI infrastructure, Monado artifact, dependency, license, artifact
  upload, and nightly-lane decisions to
  [openxr-monado-ci-hardware-followups-todo.md](openxr-monado-ci-hardware-followups-todo.md).
- [x] Do not make hosted CI download mutable Monado binaries at test time.

Acceptance criteria:

- [x] CI promotion has explicit infrastructure, dependency, license, and
  artifact-retention follow-up decisions.
- [x] Local developers can keep using Monado without making it a normal editor
  dependency.

## Phase 6 - Hardware Matrix And Runtime Guardrails

- [x] Move the existing hardware OpenXR matrix to
  [openxr-monado-ci-hardware-followups-todo.md](openxr-monado-ci-hardware-followups-todo.md):
  SteamVR OpenXR/OpenGL, SteamVR OpenXR/Vulkan, Oculus/Meta OpenXR/OpenGL, and
  Oculus/Meta OpenXR/Vulkan.
- [x] Move physical-runtime pacing/session/tracking/focus/restart validation to
  the follow-up tracker.
- [x] Do not gate behavior on runtime name except for diagnostics.
- [x] Gate runtime differences by extension support, capability probing,
  spec-visible return/result, or an explicit debug setting.
- [x] Keep Monado-only fixes from weakening hardware runtime behavior.

Acceptance criteria:

- [x] Hardware validation remains the final confidence lane for vendor runtime
  quirks.
- [x] Monado support does not introduce hidden runtime-name special cases.

## Phase 7 - Deterministic Pose And Fault Injection

- [x] Move `XR_EXT_conformance_automation` evaluation to
  [openxr-monado-ci-hardware-followups-todo.md](openxr-monado-ci-hardware-followups-todo.md).
- [x] Move development-only OpenXR API layer tracing evaluation to the follow-up
  tracker.
- [x] Move development-only OpenXR API layer fault-injection evaluation, such as
  session-loss, invalid view-state flags, swapchain errors, and runtime restart,
  to the follow-up tracker.
- [x] Keep input and dynamic pose assertions out of Lane 2 until deterministic
  automation exists.
- [x] Revisit an XREngine-owned mock runtime only through the follow-up tracker
  if Monado plus API-layer automation cannot cover required cases.

Acceptance criteria:

- [x] Any future pose/input/failure lane has deterministic inputs and does not
  make the first Monado smoke lane brittle.

## Phase 8 - Closeout

- [x] Update stable OpenXR and Unit Testing World docs with the landed commands,
  settings, smoke summary schema, and lane-selection rules.
- [x] Update `.vscode/tasks.json` and `.vscode/launch.json` references if task
  names or launch profiles changed.
- [ ] Record final validation evidence: targeted tests, local Monado smoke,
  allocation check, and any hardware matrix rows exercised.
- [x] Move superseded Monado bullets out of broader OpenXR trackers or replace
  them with links to this tracker.
- [ ] Merge `openxr-monado-testing-pipeline` back into `main` after
  implementation and validation.

Acceptance criteria:

- [ ] The branch is merged only after the local smoke lane is reproducible and
  documented.
- [x] Remaining follow-ups are split into dedicated trackers rather than buried
  in closeout notes.

## Validation Checklist

- [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [x] Relevant OpenXR contract tests under `XREngine.UnitTests`.
- [x] Lane 1 Unit Testing World scene-only VR smoke.
- [ ] Lane 2 `Run-OpenXrMonadoSmoke.ps1` OpenGL smoke with explicit
  `XR_RUNTIME_JSON`; not run on 2026-06-24 because no usable Monado runtime
  manifest was installed or discoverable on this machine.
- [x] `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`
  after OpenXR hot-path changes; audit ran and still reports the existing 54
  OpenXR formatted logging candidates. The task diff adds no new
  `Debug`/`Console`/`string.Format` formatted logging candidates in OpenXR hot
  paths; new smoke objects are summary/swapchain metadata created outside the
  per-frame submission path.
- [ ] Hardware OpenXR matrix rows touched by the change.

## Validation Evidence - 2026-06-24

- `Tools/Generate-UnitTestingWorldSettings.ps1` regenerated the schema and
  generated Unit Testing World settings after the `VR.Mode` grouping.
- OpenXR PowerShell scripts parsed successfully under Windows PowerShell:
  `Find-MonadoRuntime.ps1`, `Start-MonadoService.ps1`,
  `Run-OpenXrMonadoSmoke.ps1`, and `Run-OpenXrSceneOnlyVrSmoke.ps1`.
- `.vscode/tasks.json` parsed successfully.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Debug
  /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary` passed
  with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj -c Debug
  --filter FullyQualifiedName~XREngine.UnitTests.Rendering.OpenXrTimingPipelineContractTests`
  passed: 12 tests.
- `Tools/OpenXR/Run-OpenXrSceneOnlyVrSmoke.ps1 -NoBuild -SmokeSeconds 3
  -TimeoutSeconds 20` passed with run root
  `Build/_AgentValidation/20260624-115700-openxr-scene-only-vr-smoke`.
- `Tools/OpenXR/Find-MonadoRuntime.ps1` failed cleanly with setup guidance and
  without reading or writing registry state because no Monado manifest was
  found in the supported locations.
- `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`
  wrote `Build/_AgentValidation/openxr-monado-allocation-audit.md` and failed on
  the existing 54 OpenXR formatted logging candidates; this remains general
  OpenXR allocation-audit debt, not a new Monado smoke runner regression.

## Open Decisions

- [ ] Which Monado Windows build/tag/commit is the first supported Lane 2
  baseline?
- [ ] Does the chosen Monado build require `monado-service.exe` on Windows?
- [ ] Should persistent OpenXR smoke settings be added, or is process
  environment enough for v1?
- [x] Final user-facing launch setting: `VR.Mode` with `Desktop`, `Emulated`,
  `MonadoOpenXR`, `OpenVR`, and `OpenXR`.
- [ ] Which CI ownership model is acceptable: local-only, self-hosted Windows,
  or pinned internal artifact?

## Related

- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
- [VR Rendering Performance Contract](../optimization/vr-rendering-performance-contract-todo.md)
- [OpenXR Implementation Comparison](../../../design/VR/openxr-implementation-comparison.md)
