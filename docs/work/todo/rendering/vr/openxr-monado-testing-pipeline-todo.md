# OpenXR Monado Testing Pipeline TODO

Last Updated: 2026-06-22
Owner: XR / Rendering / Testing
Status: Active
Target Branch: `openxr-monado-testing-pipeline`

Design source:

- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [Unit Testing World](../../../../developer-guides/testing/unit-testing-world.md)

## Goal

Add a repeatable no-headset OpenXR validation lane backed by Monado's
simulated runtime, while keeping the existing non-API emulated VR scene lane as
the fast editor and pawn smoke path.

The Monado lane must use the real OpenXR loader, select the runtime per process
with `XR_RUNTIME_JSON`, exercise instance/session/swapchain/frame submission,
and fail with enough diagnostics to explain where startup or submission broke.

## Scope

- Lane 0 C# contract tests for OpenXR timing and state-machine invariants.
- Lane 1 non-API emulated VR scene testing through `EmulatedVRPawn` and
  `InitRenderEmulated`.
- Lane 2 Monado-backed OpenXR smoke testing through the standard loader.
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

- [ ] Create dedicated branch `openxr-monado-testing-pipeline`.
- [ ] Link this tracker from the design doc, work index, OpenXR timing tests,
  and OpenXR future-work tracker.
- [ ] Inventory the existing startup hooks: `UnitTestingWorldSettings.VRPawn`,
  `UseOpenXR`, `EmulatedVRPawn`, `PreviewVRStereoViews`,
  `Engine.InitializeVR(...)`, `VRState.InitializeOpenXR(...)`,
  `InitRenderEmulated(...)`, and existing `XR_RUNTIME_JSON` diagnostics.
- [ ] Update Unit Testing World docs to distinguish Lane 1 scene-only VR from
  Lane 2 Monado OpenXR runtime testing.
- [ ] Document the minimum settings for Lane 1:

  ```jsonc
  {
    "VRPawn": true,
    "UseOpenXR": false,
    "EmulatedVRPawn": true,
    "PreviewVRStereoViews": true
  }
  ```

- [ ] Document the minimum settings for Lane 2:

  ```jsonc
  {
    "VRPawn": true,
    "UseOpenXR": true,
    "EmulatedVRPawn": false,
    "PreviewVRStereoViews": true
  }
  ```

Acceptance criteria:

- [ ] Repository docs make it clear that `EmulatedVRPawn` does not emulate the
  OpenXR API.
- [ ] Future OpenXR changes can identify whether they require Lane 0, Lane 1,
  Lane 2, or hardware validation.

## Phase 1 - Local Monado Smoke Runner

- [ ] Add `Tools/OpenXR/Find-MonadoRuntime.ps1`.
- [ ] Support `-RuntimeJson` as the authoritative runtime manifest override.
- [ ] Search common Monado build/install paths only when no override is
  supplied.
- [ ] Parse the runtime manifest with structured JSON parsing and resolve
  `runtime.library_path` relative to the manifest file when needed.
- [ ] Validate that the manifest and runtime DLL exist; fail with actionable
  setup guidance.
- [ ] Ensure the discovery script never edits
  `HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime`.
- [ ] Add `Tools/OpenXR/Start-MonadoService.ps1` if the chosen Monado build
  needs an out-of-process service.
- [ ] Track Monado service ownership with a marker containing PID, start time,
  manifest path, and `ownedByRunner`.
- [ ] Make teardown idempotent and kill only services started by the runner.
- [ ] Add `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1`.
- [ ] Resolve `XR_RUNTIME_JSON`, set it only for the child process, and launch
  Unit Testing World through the editor.
- [ ] Run a loader preflight before editor startup:
  `xrEnumerateApiLayerProperties` and `xrEnumerateInstanceExtensionProperties`.
- [ ] Fail early when the selected renderer lacks the required OpenXR graphics
  binding: `XR_KHR_opengl_enable` for OpenGL, `XR_KHR_vulkan_enable` or
  `XR_KHR_vulkan_enable2` for Vulkan.
- [ ] Start with OpenGL as the only required Lane 2 renderer.
- [ ] Capture or identify the `Build/Logs/...` session used for assertions.
- [ ] Assert the first lifecycle markers from logs: loader resolved, runtime
  manifest selected, instance created, system found, session created,
  swapchains created, first views located, and first frame submitted.

Acceptance criteria:

- [ ] A developer can run one script with an explicit Monado runtime manifest
  and get a pass/fail result without changing global OpenXR runtime settings.
- [ ] Failures before editor launch report the missing file, loader, runtime
  manifest field, runtime DLL, or graphics extension.

## Phase 2 - Bounded Editor Smoke Exit

- [ ] Add `--smoke-frames N` CLI support.
- [ ] Add matching `XRE_SMOKE_FRAMES` environment-variable support.
- [ ] Count successfully submitted `xrEndFrame` calls for the active OpenXR
  session.
- [ ] After N submitted frames, request session exit, drain session state to
  `XR_SESSION_STATE_EXITING` or `XR_SESSION_STATE_IDLE`, destroy swapchains,
  destroy the session, destroy the instance, and exit cleanly.
- [ ] Return process exit code 0 only when the smoke criteria pass.
- [ ] Return stable non-zero exit codes for preflight failure, startup failure,
  frame-submission failure, summary/assertion failure, and teardown failure.
- [ ] Ensure the bounded exit path is local-only until it reliably avoids
  hanging the editor.

Acceptance criteria:

- [ ] Monado smoke runs can finish without human interaction.
- [ ] A failed smoke run cannot hang indefinitely in the editor process.

## Phase 3 - Settings And Launch Hygiene

- [ ] Add a named Unit Testing World profile or launch task for Monado.
- [ ] Add VS Code tasks only after the scripts are stable:
  `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`,
  `Test-OpenXR-Monado-Smoke`, and `Test-OpenXR-EmulatedVR-Smoke`.
- [ ] Add a structured startup warning when `UseOpenXR=true` and
  `EmulatedVRPawn=true`.
- [ ] Keep that warning once-per-startup, non-fatal, and explicit that
  `EmulatedVRPawn` is scene-only.
- [ ] Do not silently disable either `UseOpenXR` or `EmulatedVRPawn` when both
  are set.
- [ ] Propose a clearer name for `EmulatedVRPawn`, such as `SceneOnlyVRPawn` or
  `NoRuntimeVRPawn`.
- [ ] If the rename is approved, execute it across settings, docs, schema, and
  tasks. Since v1 has not shipped, prefer the clean name over legacy aliases
  unless a migration note is truly needed.
- [ ] If repeated local use needs persistent settings, add
  `OpenXrRuntimeJson`, `OpenXrExpectedRuntimeName`,
  `OpenXrRequireMockRuntime`, and `OpenXrSmokeFrameCount`.
- [ ] If those settings derive from `XRBase`, implement setters with
  `SetField(...)`.
- [ ] Ensure an existing process environment value for `XR_RUNTIME_JSON` wins
  over any XREngine-specific setting.

Acceptance criteria:

- [ ] Launch configuration expresses the selected lane without requiring a
  developer to remember flag combinations.
- [ ] Mixed `UseOpenXR` and `EmulatedVRPawn` settings produce one clear warning
  and otherwise preserve user intent.

## Phase 4 - Diagnostics, Summary, And Assertions

- [ ] Emit a structured OpenXR smoke summary with `schemaVersion`.
- [ ] Include runtime manifest path, runtime name/version, renderer, enabled
  extension list, reference-space type, swapchain metadata, session state
  transitions, located view count, submitted frame count, and failure flags.
- [ ] Record per-eye swapchain acquire/wait/release counts.
- [ ] Record first successful predicted and late pose-cache updates.
- [ ] Record first desktop mirror composition.
- [ ] Add `perFrameAllocationsBytes` or a recorded allocation baseline for the
  OpenXR submission hot path.
- [ ] Wire `Tools/Reports/Find-NewAllocations.ps1` and/or profiler allocation
  evidence into the assertion step.
- [ ] Teach the runner to fail on missing required summary fields.
- [ ] Bump `schemaVersion` whenever required fields or field meanings change.
- [ ] Store runner-owned Monado service diagnostics under the run's log session.

Acceptance criteria:

- [ ] A passing summary proves instance, system, session, swapchain, view locate,
  frame submit, pose-cache, and mirror milestones occurred.
- [ ] A failing summary points to a stable failure class without needing a
  debugger.
- [ ] The OpenXR frame-submission hot path introduces no new unmanaged or
  managed per-frame allocations unless a justified baseline is recorded.

## Phase 5 - CI Candidate And Dependency Hygiene

- [ ] Keep the first implementation local-only.
- [ ] Decide separately whether CI uses a Windows self-hosted runner and whether
  Monado artifacts become repo-managed dependencies.
- [ ] Get owner approval before adding runner infrastructure.
- [ ] If a Monado binary/artifact becomes repo-managed, pin the version or
  commit and update [docs/DEPENDENCIES.md](../../../../DEPENDENCIES.md) and
  generated license files with `pwsh Tools/Generate-Dependencies.ps1`.
- [ ] Do not make hosted CI download mutable Monado binaries at test time.
- [ ] Add CI only after the bounded smoke exit and summary assertions are
  stable.
- [ ] Upload `Build/Logs`, the smoke summary, and runner diagnostics as
  artifacts.
- [ ] Add a longer nightly lane only after the short smoke lane is stable.

Acceptance criteria:

- [ ] CI promotion has explicit infrastructure, dependency, license, and
  artifact-retention decisions.
- [ ] Local developers can keep using Monado without making it a normal editor
  dependency.

## Phase 6 - Hardware Matrix And Runtime Guardrails

- [ ] Re-run the existing hardware OpenXR matrix after the Monado lane lands:
  SteamVR OpenXR/OpenGL, SteamVR OpenXR/Vulkan, Oculus/Meta OpenXR/OpenGL, and
  Oculus/Meta OpenXR/Vulkan.
- [ ] Confirm pacing defaults, session-state transitions, tracking-loss policy,
  headset-off behavior, focus/visibility transitions, and runtime restart on
  physical or vendor-managed runtimes.
- [ ] Do not gate behavior on runtime name except for diagnostics.
- [ ] Gate runtime differences by extension support, capability probing,
  spec-visible return/result, or an explicit debug setting.
- [ ] Keep Monado-only fixes from weakening hardware runtime behavior.

Acceptance criteria:

- [ ] Hardware validation remains the final confidence lane for vendor runtime
  quirks.
- [ ] Monado support does not introduce hidden runtime-name special cases.

## Phase 7 - Deterministic Pose And Fault Injection

- [ ] Evaluate `XR_EXT_conformance_automation` support in the selected Monado
  build and relevant hardware runtimes.
- [ ] Evaluate a development-only OpenXR API layer for call tracing.
- [ ] Evaluate a development-only OpenXR API layer for fault injection, such as
  session-loss, invalid view-state flags, swapchain errors, and runtime restart.
- [ ] Keep input and dynamic pose assertions out of Lane 2 until deterministic
  automation exists.
- [ ] Revisit an XREngine-owned mock runtime only if Monado plus API-layer
  automation cannot cover required cases.

Acceptance criteria:

- [ ] Any future pose/input/failure lane has deterministic inputs and does not
  make the first Monado smoke lane brittle.

## Phase 8 - Closeout

- [ ] Update stable OpenXR and Unit Testing World docs with the landed commands,
  settings, smoke summary schema, and lane-selection rules.
- [ ] Update `.vscode/tasks.json` and `.vscode/launch.json` references if task
  names or launch profiles changed.
- [ ] Record final validation evidence: targeted tests, local Monado smoke,
  allocation check, and any hardware matrix rows exercised.
- [ ] Move superseded Monado bullets out of broader OpenXR trackers or replace
  them with links to this tracker.
- [ ] Merge `openxr-monado-testing-pipeline` back into `main` after
  implementation and validation.

Acceptance criteria:

- [ ] The branch is merged only after the local smoke lane is reproducible and
  documented.
- [ ] Remaining follow-ups are split into dedicated trackers rather than buried
  in closeout notes.

## Validation Checklist

- [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Relevant OpenXR contract tests under `XREngine.UnitTests`.
- [ ] Lane 1 Unit Testing World emulated VR smoke.
- [ ] Lane 2 `Run-OpenXrMonadoSmoke.ps1` OpenGL smoke with explicit
  `XR_RUNTIME_JSON`.
- [ ] `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`
  after OpenXR hot-path changes.
- [ ] Hardware OpenXR matrix rows touched by the change.

## Open Decisions

- [ ] Which Monado Windows build/tag/commit is the first supported Lane 2
  baseline?
- [ ] Does the chosen Monado build require `monado-service.exe` on Windows?
- [ ] Should persistent OpenXR smoke settings be added, or is process
  environment enough for v1?
- [ ] What is the final replacement name for `EmulatedVRPawn`?
- [ ] Which CI ownership model is acceptable: local-only, self-hosted Windows,
  or pinned internal artifact?

## Related

- [OpenXR Monado Testing Pipeline](../../../design/VR/openxr-monado-testing-pipeline.md)
- [OpenXR Timing And Pipeline Tests](../../tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work](openxr-future-work-todo.md)
- [VR Rendering Performance Contract](../optimization/vr-rendering-performance-contract-todo.md)
- [OpenXR Implementation Comparison](../../../design/VR/openxr-implementation-comparison.md)
