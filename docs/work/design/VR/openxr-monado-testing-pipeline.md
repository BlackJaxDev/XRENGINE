# OpenXR Monado Testing Pipeline

Last updated: 2026-05-20

Status: Design proposal

Branch lifecycle: this work lives on a dedicated branch (for example `openxr-monado-testing-pipeline`). Phase 0's first task creates the branch; Phase 5's last task merges it back into `main` after implementation and validation. Both endpoints are restated in their phases below; do not lose either.

## Summary

XREngine needs two complementary no-headset VR validation paths:

1. The existing engine-side scene-only VR path, which validates the VR pawn, transforms, stereo preview, editor UI, and scene wiring without entering the OpenXR API.
2. A new OpenXR API-driven no-headset path, using Monado's simulated HMD/controller runtime through the standard OpenXR loader.

The existing `SceneOnlyVRPawn` path is still valuable as the fast inner loop. It should remain the default no-HMD workflow for scene and editor-pawn testing. Monado should become the repeatable "real OpenXR" smoke and regression lane: the engine creates an OpenXR instance, system, graphics-bound session, swapchains, reference spaces, actions, predicted/late poses, and frame submissions without requiring physical hardware.

This document defines how those lanes fit together and what needs to be hardened before OpenXR changes can be trusted without a headset on the desk.

## Problem

The current unit-testing-world VR controls conflate two concepts that need to be separated operationally:

- `VR.Mode=Emulated` means "build a VR-shaped scene rig without requiring a VR runtime."
- `VR.Mode=MonadoOpenXR` means "route runtime startup through the OpenXR loader with Monado selected per process."

Those are different test surfaces. The emulated rig catches scene/pawn regressions, but it does not execute `xrCreateInstance`, runtime discovery, graphics binding negotiation, swapchain acquire/wait/release, `xrLocateViews`, action sync, session state transitions, or `xrEndFrame`.

Hardware OpenXR testing catches those things, but it is slow, machine-dependent, and hard to run in CI. Monado gives us a middle lane: an OpenXR runtime selected through the real loader, backed by simulated devices.

## Goals

- Keep the existing non-API scene-only VR path as a fast deterministic smoke path.
- Add a Monado-backed OpenXR runtime smoke path that requires no physical HMD.
- Select Monado per process via `XR_RUNTIME_JSON`, not by mutating the user's Windows active-runtime registry value.
- Make OpenXR testing observable enough that a failed run answers "where did it fail?" without attaching a debugger.
- Land OpenGL first as the primary Monado-backed Lane 2 path, since OpenGL is XRENGINE's primary renderer; add Vulkan as a follow-up gated on extension probing.
- Keep hardware runtime validation as a separate final confidence lane, not the only way to validate routine OpenXR work.
- Leave room for a future XREngine-owned mock runtime only if Monado cannot provide the deterministic control we need.

## Non-Goals

- Do not use Monado on Windows as a physical-HMD compatibility target. Monado's Windows posture is mainly simulated devices.
- Do not replace SteamVR/Oculus/WMR hardware validation.
- Do not make Monado a required dependency for normal editor startup.
- Do not mutate `HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime` from tests.
- Do not hand-roll an XREngine OpenXR runtime before proving Monado is insufficient.

## Terms

| Term | Meaning |
|---|---|
| Non-API scene-only VR | `VR.Mode=Emulated` plus `Engine.VRState.InitRenderEmulated(...)`. Exercises engine VR scene/render-preview logic without OpenXR. |
| API-driven mock runtime | A real OpenXR runtime selected by the loader and backed by simulated devices. Monado is the first target. |
| Hardware runtime lane | SteamVR, Oculus/Meta, WMR, or another end-user runtime with a physical or vendor-managed headset. |
| Loader override | `XR_RUNTIME_JSON=<absolute runtime manifest path>`, scoped to a process or task. |

## External Constraints

Khronos OpenXR loader behavior gives us the correct integration point:

- The OpenXR loader sits between the application, API layers, and the active runtime.
- On Windows, the active runtime normally comes from `HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime`.
- `XR_RUNTIME_JSON` overrides standard runtime discovery and forces the loader to use one runtime manifest for that process.
- Runtime manifests are JSON files with `file_format_version`, `runtime`, and `library_path`; relative `library_path` values resolve relative to the manifest file.
- API layers are optional components that can intercept OpenXR commands. They are useful for future tracing/fault-injection, but should not be required for the first Monado smoke lane.

Monado is a suitable first mock runtime target because it is an open-source OpenXR runtime for Linux, Windows, and Android. Its own developer site currently notes that Windows support is mostly simulated HMD and controller drivers, which aligns with our no-HMD test goal.

Windows setup is source-build based. Upstream does not publish a generic Windows binary installer, so XREngine provides `Tools/OpenXR/Install-Monado.ps1` to clone Monado, bootstrap/use vcpkg, configure CMake/Ninja, stage the result under `Build/Deps/Monado`, and copy `openxr_loader.dll` into the editor build output when available. The script still uses per-process/runtime-manifest selection only; it does not write `HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime`.

## Current XREngine Hooks

Relevant existing behavior:

- `UnitTestingWorldSettings` exposes grouped `VR.Mode`, `VR.PreviewStereoViews`, `VR.AllowDesktopEditing`, and `VR.OpenXrRuntimeJson`.
- `VR.Mode=MonadoOpenXR` auto-detects a usable Monado runtime manifest when `VR.OpenXrRuntimeJson` and `XR_RUNTIME_JSON` are unset.
- Editor unit-testing startup normalizes `VR.Mode` into the existing runtime flags before scene construction.
- `VR.Mode=OpenXR` and `VR.Mode=MonadoOpenXR` map to `EVRRuntime.OpenXR`.
- `Engine.InitializeVR(...)` calls `VRState.InitializeOpenXR(window)` when OpenXR is forced.
- `OpenXRAPI.CreateInstance()` already honors `XR_RUNTIME_JSON` by preferring it over the Windows active-runtime registry path for diagnostics and SteamVR startup checks.
- The non-API stereo preview path calls `Engine.VRState.InitRenderEmulated(window)`.

That means the first Monado lane should not need a new runtime abstraction. It needs launch tooling, settings hygiene, diagnostics, and validation checks.

## Lane Selection Matrix

Use this table to pick the minimum lane that must pass for a given change. Higher lanes always include the lower ones.

| Change scope | Lane 0 (contract) | Lane 1 (scene-only VR) | Lane 2 (Monado OpenXR) | Lane 3 (hardware) |
|---|---|---|---|---|
| Pure C# OpenXR timing/state-machine logic | required | optional | optional | optional |
| VR pawn / scene graph / editor UI in VR-shaped scenes | required | required | optional | optional |
| OpenXR binding, swapchain, view-locate, frame submit | required | required | required | optional |
| Pacing defaults, session-state transitions, tracking-loss policy | required | required | required | recommended |
| Runtime-specific quirk fixes (SteamVR/Oculus/WMR) | required | required | required | required |

A "runtime-specific quirk" means a fix gated on observed behavior of a vendor runtime; it must not be gated on runtime *name* unless the gating is diagnostics-only.

Naming cleanup: the user-facing launch choice now lives in `VR.Mode`, so the settings file describes complete modes instead of asking users to compose several booleans.

## Test Lane Model

### Lane 0: Contract Tests

Purpose: Fast C# tests for OpenXR timing and state-machine invariants without a native runtime.

Examples:

- Frame handoff invariants.
- Pacing thread lifecycle.
- Tracking-loss policy.
- Allocation sentinels.
- Source-level hot-path allocation audits.

This lane should stay independent of Monado. It catches logic regressions before any process/run orchestration starts.

### Lane 1: Non-API Scene-only VR

Purpose: Validate XREngine's own VR-shaped scene and editor path without OpenXR.

Recommended settings:

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

Coverage:

- VR pawn creation.
- HMD/controller/tracker scene nodes.
- Manual tracker setup.
- Stereo render target setup through `InitRenderEmulated`.
- Editor UI interaction in VR-shaped scenes.
- Desktop mirror/stereo-preview layout.

This lane intentionally does not prove OpenXR API correctness.

### Lane 2: Monado OpenXR Mock Runtime

Purpose: Validate XREngine's OpenXR API path without physical hardware.

Recommended settings:

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

Set `OpenXrRuntimeJson` to an explicit `openxr_monado.json` path when auto-detection is not enough.

Optional process environment:

```powershell
$env:XR_RUNTIME_JSON = "C:\Path\To\Monado\openxr_monado.json"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

First-target Monado build: pick a single Monado Windows build/binding combination for v1 of this lane and document its tag/commit hash. Treat OpenGL as the first supported renderer because it is XRENGINE's primary path. Vulkan is added in a follow-up once Lane 2 is green and `XR_KHR_vulkan_enable2` is confirmed available in the chosen Monado build.

Coverage:

- OpenXR loader resolution.
- Monado runtime manifest selection.
- `xrCreateInstance`.
- Required graphics extension filtering.
- `xrGetSystem` for HMD form factor.
- OpenGL or Vulkan session creation.
- Reference space creation.
- Swapchain creation and image enumeration.
- `xrWaitFrame` / `xrBeginFrame` / `xrLocateViews` / `xrEndFrame`.
- Per-eye swapchain acquire/wait/release.
- Predicted and late pose-cache updates.
- Desktop mirror composition.

Input is intentionally out of scope for Lane 2 acceptance. Pose/action dynamics belong to Lane 4. The most Lane 2 may assert about input is that creating action sets and querying aim/grip pose actions returns without `XR_ERROR_*`, regardless of the pose value. If even that proves flaky on the chosen Monado build, drop input from Lane 2 entirely.

This is the main new hardening lane.

### Lane 3: Hardware OpenXR Runtime Matrix

Purpose: Validate behavior that simulated runtimes cannot prove.

Keep the existing hardware matrix in the OpenXR timing tests tracker:

- SteamVR OpenXR, OpenGL.
- SteamVR OpenXR, Vulkan.
- Oculus/Meta OpenXR, OpenGL.
- Oculus/Meta OpenXR, Vulkan.

Use this lane for final validation of pacing defaults, runtime-specific quirks, real tracking loss, headset-off behavior, focus/visibility transitions, and runtime restart.

### Lane 4: Optional API-Layer Or CTS-Inspired Automation

Purpose: Deterministic pose/input/failure injection once the Monado smoke lane is stable.

Possible directions:

- Use `XR_EXT_conformance_automation` when a runtime supports it.
- Add a development-only API layer for tracing selected OpenXR calls.
- Add a development-only API layer for failure injection, for example forcing `XR_SESSION_STATE_LOSS_PENDING` or invalid view-state flags.

This is a later lane. The first milestone should not depend on API-layer mechanics.

## Proposed Tooling

### Monado Runtime Discovery

Add a small PowerShell helper, for example:

```powershell
Tools/OpenXR/Find-MonadoRuntime.ps1
```

Responsibilities:

- Accept `-RuntimeJson` as the authoritative override.
- Search common Monado build/install paths only when no override is supplied.
- Validate that the JSON file exists.
- Parse the JSON enough to resolve `runtime.library_path`.
- Validate that the runtime DLL exists after relative-path resolution.
- Print a compact result object or fail with actionable guidance.

This script must not edit the Windows active-runtime registry.

### Monado Service Startup

If the selected Monado build uses an out-of-process service, add:

```powershell
Tools/OpenXR/Start-MonadoService.ps1
```

Responsibilities:

- Accept an explicit `-ServiceExe`.
- Infer `monado-service.exe` relative to the runtime manifest only when safe.
- Start the service if not already running.
- Capture the service PID and log path for teardown/diagnostics.
- Avoid killing a service process that was already running before the test.

The initial version can be manual if Monado packaging differs too much across machines.

### Smoke Runner

Add:

```powershell
Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1
```

Responsibilities:

- Resolve `XR_RUNTIME_JSON`.
- Optionally start Monado service (see service-ownership rules below).
- Set process-local environment variables.
- Run an OpenXR loader preflight (`xrEnumerateApiLayerProperties` and `xrEnumerateInstanceExtensionProperties`) before launching the editor; fail fast if the loader cannot enumerate against the selected runtime.
- Launch the editor in Unit Testing World with a bounded smoke duration (`--smoke-frames N` or `XRE_SMOKE_FRAMES`, see below). The lane is not CI-eligible without this exit path.
- Capture logs under `Build/Logs/...`.
- Fail if expected OpenXR lifecycle markers are missing or if the smoke summary's allocation/perf invariants regress.

#### Bounded smoke exit

Lane 2 is only CI-eligible when the editor supports a clean bounded-run mode. Add a `--smoke-frames N` CLI flag (and matching `XRE_SMOKE_FRAMES` env var) that:

- Counts successfully submitted `xrEndFrame` calls.
- After N frames, requests `xrRequestExitSession`, drains session state to `XR_SESSION_STATE_EXITING` / `IDLE`, destroys swapchains/session/instance in order, and exits the process.
- Returns process exit code 0 only if the smoke summary JSON's required fields are populated and no failure flags are set; otherwise non-zero with a stable code per failure class.

#### Monado service ownership

If Monado on the target machine uses an out-of-process service:

- The runner must record a marker file at `Build/Logs/<session>/monado-service.json` with `{ pid, startedAtUtc, manifestPath, ownedByRunner }`.
- `ownedByRunner` is true only if the runner started the service. Teardown only kills the PID when `ownedByRunner` is true and the recorded `startedAtUtc` matches the live process.
- Teardown must be idempotent and safe to run after a crashed runner: re-reading the marker, verifying PID/start-time, then cleaning up.

The runner should produce a small machine-readable summary:

```json
{
  "schemaVersion": 1,
  "runtimeJson": "...",
  "runtimeName": "Monado",
  "runtimeVersion": "...",
  "renderer": "OpenGL",
  "instanceCreated": true,
  "systemCreated": true,
  "sessionCreated": true,
  "sessionRunning": true,
  "swapchainsCreated": true,
  "locatedViewCount": 2,
  "submittedFrameCount": 120,
  "perFrameAllocationsBytes": 0,
  "warnings": [],
  "failures": []
}
```

`schemaVersion` is required so the assertion runner can evolve without silently passing on old summaries. Bump it whenever a field's meaning changes or a required field is added.

### VS Code Tasks

Add tasks only after the scripts are stable:

- `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`
- `Test-OpenXR-Monado-Smoke`
- `Test-OpenXR-SceneOnlyVR-Smoke`

The tasks should compose the scripts rather than duplicating environment setup.

## Settings Design

The implementation supports process environment and grouped settings:

- `XR_RUNTIME_JSON`: standard OpenXR loader override.
- `XRE_WORLD_MODE=UnitTesting`: existing world selector.
- `VR.OpenXrRuntimeJson`: optional settings-level runtime manifest path. When this is `null` in `VR.Mode=MonadoOpenXR`, the loader searches `MONADO_RUNTIME_JSON`, `MONADO_INSTALL_DIR`, common Monado install paths, and repo-local Monado build/dependency paths.

Important rules:

- The engine sets `XR_RUNTIME_JSON` from `VR.OpenXrRuntimeJson` or auto-detected Monado runtime paths *only if the env var is not already set* in the current process. An explicit shell override always wins.
- The env var is set before the first OpenXR loader call and is scoped to the current process. It is never persisted globally and never written to the Windows active-runtime registry.
- If the settings type derives from `XRBase`, all property setters must use `SetField(...)` rather than direct backing-field assignment, per repo convention.

### Legacy `SceneOnlyVRPawn` + `UseOpenXR` combination

When legacy flat settings have both `UseOpenXR=true` and `SceneOnlyVRPawn=true`, the engine must:

- Log a single structured warning at startup explaining that `SceneOnlyVRPawn` is scene-only and does not emulate the OpenXR API.
- Continue startup. Do not fail. The combination is legitimate when a user wants Lane 2 with the stereo preview window enabled.
- Not silently disable either flag.

The warning is the only behavior change; lane semantics are otherwise determined by `UseOpenXR`.

## Runtime Preflight

Before launching the editor, the smoke runner should verify:

- Runtime manifest path is absolute.
- Manifest exists and has a `runtime.library_path`.
- Runtime DLL exists.
- `openxr_loader.dll` can be resolved from the app base directory, native runtime directory, or known installed locations.
- The selected renderer has a compatible OpenXR graphics binding:
  - OpenGL requires `XR_KHR_opengl_enable`.
  - Vulkan requires `XR_KHR_vulkan_enable` or `XR_KHR_vulkan_enable2`.

If the selected Monado build does not expose the binding for the current renderer, the run should fail before the expensive editor startup path and say which renderer/runtime combination is unsupported.

## Engine Diagnostics To Harden

The Monado lane should make these events easy to assert from logs or a summary file:

- OpenXR loader DLL resolved.
- `XR_RUNTIME_JSON` value used.
- Runtime manifest path and runtime name.
- Enabled instance extensions after filtering.
- Required renderer extension found.
- `xrCreateInstance` result.
- `xrGetSystem` result and form factor.
- Session creation result.
- Reference space type.
- Swapchain count, dimensions, format, and image count.
- Session state transitions.
- First successful `xrWaitFrame` / `xrBeginFrame` / `xrLocateViews`.
- First successful predicted and late pose-cache update.
- First submitted frame with projection layer.
- First desktop mirror composition.

The first version can parse existing logs. The better v1 shape is a dedicated smoke summary object written by the OpenXR layer or editor bootstrap.

## Validation Criteria

### Non-API Scene-only VR Acceptance

- Unit Testing World launches with `VR.Mode=Emulated` and `VR.PreviewStereoViews=true`.
- Left and right emulated eye render targets are created.
- HMD, controllers, and manual trackers are present in the scene.
- Editor UI can attach to the scene-only VR-shaped scene.
- No OpenXR or OpenVR runtime startup is attempted.

### Monado OpenXR Acceptance

- The process uses `XR_RUNTIME_JSON` to select Monado.
- OpenXR instance and system creation succeed.
- Runtime extension probing records the selected graphics binding.
- A graphics-bound session is created for the active renderer.
- Two eye swapchains are created.
- At least two views are located each frame after session start.
- At least N frames are submitted through `xrEndFrame` with projection layers.
- Per-eye acquire/wait/release counts match for the smoke interval.
- Predicted and late pose caches are populated.
- No unbounded growth in pacing stalls or session warnings.
- No new per-frame heap allocations are introduced on the OpenXR submission hot path. The smoke summary's `perFrameAllocationsBytes` must be zero (or match a recorded baseline). Wire `Report-NewAllocations` and/or `profiler-fps-drops.log` into the assertion step.
- The process exits cleanly via the bounded smoke exit path and leaves no runner-owned Monado service process behind.

### Hardware Acceptance

- Existing hardware matrix remains green after the Monado lane lands.
- Monado-only fixes must not special-case runtime behavior unless the code gates by observed runtime capability or an explicit debug setting.

## CI Strategy

Start with local developer tasks. Promote to CI in stages:

1. Local-only script, documented.
2. Provision a Windows self-hosted runner. (Approval surface: infrastructure ownership, secrets, runner labels.)
3. Pin a Monado build for that runner. (Approval surface: dependency licensing per `docs/DEPENDENCIES.md`, artifact storage, version policy.) Steps 2 and 3 must not be conflated; they have different reviewers.
4. CI lane that runs only the Monado smoke test and uploads `Build/Logs`.
5. Nightly lane that runs longer OpenXR pacing and tracking-loss scenarios.

Do not make hosted CI depend on downloading unpinned binaries at test time. If Monado artifacts are used, pin the version or commit and record the license/source in dependency docs before making it required.

## Future XREngine-Owned Mock Runtime

Only build our own runtime if Monado cannot support the determinism we need. A minimal XREngine mock runtime would need:

- Runtime manifest.
- Native DLL exporting `xrNegotiateLoaderRuntimeInterface`.
- Instance, system, session, space, action, and swapchain handles.
- `XR_KHR_opengl_enable` at minimum, plus Vulkan later.
- Swapchain image allocation compatible with the renderer.
- Deterministic `xrWaitFrame`, `xrLocateViews`, and event queue.
- Optional scripted controller/tracker poses.
- Failure injection for session loss, invalid tracking, swapchain errors, and runtime restart.

That is a real runtime project. Monado should absorb the generic runtime burden first; an XREngine-owned mock should only add deterministic scriptability or failure injection that Monado/API layers cannot provide.

## Implementation Phases

### Phase 0: Document And Name The Lanes

- [x] Create a dedicated branch for this work, for example `openxr-monado-testing-pipeline`.
- [x] Add this design doc.
- [x] Update related OpenXR trackers to link here.
- [x] Update unit-testing-world docs to clarify that `VR.Mode=Emulated` is not OpenXR API emulation.

### Phase 1: Local Monado Smoke

- [x] Add `Find-MonadoRuntime.ps1`.
- [x] Add a minimal `Run-OpenXrMonadoSmoke.ps1`.
- [x] Document manual setup and `XR_RUNTIME_JSON` usage.
- [x] Add a summary assertion pass for instance/system/session/swapchain/frame markers.

### Phase 2: Settings And Launch Hygiene

- [x] Add a named Unit Testing World profile or task for Monado.
- [x] Add grouped `VR.Mode` launch settings so users choose Desktop, Emulated, MonadoOpenXR, OpenVR, or OpenXR directly.
- [x] Rename the no-runtime VR pawn setting to `SceneOnlyVRPawn` internally and expose the user-facing launch choice through `VR.Mode`.
- [x] Add `--smoke-frames N` / `XRE_SMOKE_FRAMES` bounded-run support with clean teardown and stable exit codes.

### Phase 3: Diagnostics And Summary Output

- [x] Emit a structured OpenXR smoke summary.
- [x] Include runtime manifest, runtime name, renderer, extension list, session transitions, swapchain metadata, and submitted frame counts.
- [x] Teach the smoke runner to fail on missing summary fields.

### Phase 4: CI Candidate

- [ ] Decide whether CI uses self-hosted Windows or a pinned local Monado artifact.
- [ ] Add dependency/license documentation if a pinned Monado artifact becomes repo-managed.
- [ ] Upload OpenXR logs and smoke summary as artifacts.

### Phase 5: Deterministic Pose And Fault Injection

- [ ] Evaluate `XR_EXT_conformance_automation` support.
- [ ] Evaluate a development API layer for call tracing/fault injection.
- [ ] Revisit XREngine-owned mock runtime only if the above cannot cover required cases.
- [ ] Merge the dedicated branch back into `main` after implementation and validation. (Pairs with Phase 0's branch creation; do not skip.)

## Risks

| Risk | Mitigation |
|---|---|
| Monado Windows builds may differ in service layout or manifest names. | Require explicit `-RuntimeJson` first; add discovery heuristics only after testing real installs. |
| Monado may not expose the graphics binding needed by the selected renderer. | Add preflight extension probing and fail early with renderer-specific guidance. |
| Simulated input may be static or limited. | Treat pose/action dynamics as optional until controlled automation is added. |
| Runtime-specific fixes could accidentally target Monado behavior only. | Gate by capability, setting, or spec-visible result, not by runtime name unless diagnostics only. |
| CI could become brittle if it downloads mutable Monado builds. | Pin artifacts/commits or keep Monado smoke local-only until dependency flow is approved. |
| Multiple flat VR booleans invited confusing combinations. | Resolved by adding grouped `VR.Mode` values and deriving the internal booleans from the selected mode. |
| Lane 2 results drift between developer machines because Monado builds vary. | Pin a specific Monado tag/commit hash in `docs/DEPENDENCIES.md` (or a planning addendum) before declaring Lane 2 stable, even when the binary is developer-supplied. |
| Smoke runs hang or never exit, blocking CI promotion. | Make `--smoke-frames N` / `XRE_SMOKE_FRAMES` a Phase 2 prerequisite for any CI talk; runs without it are local-only. |

## References

- Khronos OpenXR Loader - Design and Operation: https://registry.khronos.org/OpenXR/specs/1.0/loader.html
- Monado project site: https://monado.dev/
- Monado developer site: https://monado.freedesktop.org/
- Monado simulated driver docs: https://monado.pages.freedesktop.org/monado/group__drv__simulated.html
- OpenXR `XR_EXT_conformance_automation`: https://registry.khronos.org/OpenXR/specs/1.0/man/html/XR_EXT_conformance_automation.html
- OpenXR CTS usage guide: https://registry.khronos.org/OpenXR/conformance/cts_usage.html

## Related

- [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Implementation Comparison](openxr-implementation-comparison.md)
- [OpenXR Monado Testing Pipeline TODO](../../todo/rendering/vr/openxr-monado-testing-pipeline-todo.md)
- [OpenXR Timing Tests TODO](../../todo/tests/openxr-timing-tests-todo.md)
- [OpenXR Future Work TODO](../../todo/rendering/vr/openxr-future-work-todo.md)
- [Unit Testing World](../../../developer-guides/testing/unit-testing-world.md)
