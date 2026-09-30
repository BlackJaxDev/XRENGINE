# OpenXR calibration and spectator implementation record

**Date:** 2026-09-30
**Branch:** `engine-validation`
**Source baseline:** `de9ee0d4018668c9b961be82a7e138266c4a0feb`; the independent software-rendering design document is committed separately at `c894abff71ad99ecbc6163b177eaf36f340fa00f`
**Status:** Software implementation and regression validation; hardware acceptance remains open

This record supports the checked software rows in the [active checklist](../../todo/avatar/openxr-full-body-calibration-spectator-todo.md). It does not mark the hardware milestone complete.

## Implemented behavior

- Eleven owned calibration targets with one offset representation, staged commit, exact compensating rollback, finite/singular validation, typed results and production settings initialization
- Canonical T-pose placement; fixed eye geometry; arbitrary body mount offsets; deterministic one-to-one proximity assignment including segment distances and scale-aware cutoffs
- Explicit queued calibration lifecycle, dedicated press-edge actions, coherent stationary capture, eye-level feedback, exact cancel restoration, no automatic start or mute coupling
- Persisted body measurements and single-owner avatar-only scaling, including fingertip estimates and explicit notices
- Role-path binding suggestions separated from persistent physical identity/subaction transport, current validity, retained pose diagnostics, scene-owner collection lifecycle, immutable copied snapshots and explicit late-connection restart policy
- Simulation-owned predicted pose snapshots and separate late render poses; hold/fade recovery weights; immediate rejection of different identities/generations; shared discontinuity resets
- Independent monoscopic spectator, collision boom, per-view visibility, desktop routing without possession/audio changes, completed-output leases and nonblocking cadence
- Memory-only body calibration restoration for exact avatar/measurement/provider-generation/reference-basis matches

## IK instability reproduced and fixed

The actual `Assets/Walks/Sexy Walk.anim` is imported and evaluated in a deterministic regression, not replaced by an invented walk. The investigation reproduced stale intermediate analytic-limb transforms, scheduling/reset order problems, virtual-limb bend direction/selected-joint-length errors and reversed knee-goal behavior. The integrated compressed-torso case also exposed a pelvis re-solve plane overwriting tracked chest roll.

- Reachable analytic-limb error reduced from approximately 0.312 m to 0.00000016 m
- Repeated exact-time walk evaluation has zero measured pose drift in the fixture
- Chest roll residual in the compressed-torso regression reduced from 0.146 radians to 0.0086 radians while retaining head/hip/wrist endpoint priority
- Knee goals move the knees toward their tracker direction while feet remain constrained

Detailed procedures and limits: [animation stability](../../investigations/avatar/animation-ik-stability-2026-09-30.md), [tracked-body solving](../../investigations/avatar/tracked-body-solver-2026-09-30.md), [spectator validation](../../investigations/avatar/spectator-validation-2026-09-30.md).

## Automated evidence

The final headless build completed with **0 warnings and 0 errors**; **144/144 tests passed**, with no skips. It builds the production Core, Rendering, AnimationIntegration and InputIntegration dependencies and includes:

- Target persistence/cleanup/rebinding, eleven-slot hookup, arbitrarily rotated mounts, non-origin playspaces, no scale mutation, tilted-head refusal and fixed-offset repeated captures
- A matrix callback fault injected after an earlier reused target was changed, proving compensating restoration of target matrices and exact external solver references
- Every one of the 40,320 eight-tracker enumeration permutations, duplicate identity exclusion, invalid/distant rejection and stable ties
- Real registered calibration actions driving the production player state machine and real VR headset/controller transforms, including six-point capture, cancel and tracker loss at capture
- All-source angular/linear stationary windows, exact snapshot coherence, pose retention, loss/recovery fade, generation mismatch and late-controller initialization
- Body measurement persistence, canonical A-pose straightening, metric IPD independence and same-session value/rig restoration
- Spectator geometry, collision/output lifetime rules, visibility restoration, camera routing and discontinuity behavior

Run from the repository root, reserving a validation directory per `AGENTS.md`:

```sh
dotnet build XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj \
  --artifacts-path <run>/temp-build -p:NuGetAudit=false -p:UseSharedCompilation=false -m:1
dotnet <run>/temp-build/bin/XREngine.HeadlessTests/debug/XREngine.HeadlessTests.dll \
  --workers=0 --result=<run>/reports/integration.xml
```

Disposable evidence is under `Build/_AgentValidation/20260930-220800-fullbody-calibration/`: `reports/integration.xml`, `logs/integration-build.log`, `logs/bootstrap-all-build.log`, `logs/final-vulkan-build.log` and `logs/final-vulkan-smoke.log`. All required findings are recorded here; runtime behavior does not depend on those ignored files.

The standalone native OpenXR adapter was built without warnings/errors. Bootstrap composition builds without warnings/errors for both the explicit OpenGL flavor and the all-backend flavor using Windows cross-targeting and the documented `XREngineUseExistingNativeBridges=true` option. These are compilation diagnostics, not renderer fallbacks or hardware runtime runs.

All five existing production software Vulkan checks pass with standard and synchronization validation reporting zero warnings/errors, using the documented explicit shaderc 2025.2 override. The bundled shaderc remains unqualified. These checks verify real offscreen Vulkan work; they do not render a full avatar spectator scene.

### Build limits and adjacent repair

The default native-build Bootstrap lane initially reached missing generated Windows VMA bridge artifacts; the documented existing-native-bridge option permits the compilation lane. Native Windows payloads were not qualified or launched here. The Editor's OpenGL-only diagnostic fails on existing unconditional Vulkan references in profiler tools; its all-backend retry stops at an unavailable `FastGltfBridge.Native.dll`. No complete Editor build or live Editor launch is claimed.

Two existing pinned submodules, OpenVR.NET and OscCore-NET9, were initialized without changing their pins. A referenced but absent `DesktopAssetFileSystem` adapter was confirmed missing in the tracked tree/history and supplied as a bounded wrapper over `Directory`/`FileSystemWatcher`; its narrow filesystem regression is included. It does not alter operating-system permissions. A validation-only `powershell` alias to installed `pwsh` was used for the existing Bootstrap generator command.

### Final review corrections

The final review found two additional runtime edges. Room-scale movement now ignores retained invalid hips/head poses and refreshes the selected calibrated target's current world matrix before reading it, so a valid head takes over immediately after hips tracking loss. Calibration discontinuities accumulate atomically until the simulation tick: a later teleport, snap turn, or camera change cannot overwrite a pending recenter, session-generation, or avatar invalidation.

The final headless rebuild has zero warnings/errors, and all **144 tests pass** (2026-09-30 23:37 UTC), including the lost-hips movement regression and twelve discontinuity ordering/preservation cases. Evidence: `logs/review-final-build.log`, `logs/review-final.log`, and `reports/review-final.xml` within the same validation run. The earlier 131-test result and native/backend compilation and Vulkan smoke results remain the baseline evidence; these final changes are limited to player simulation logic.

## Remaining acceptance gates

1. Run the existing SteamVR OpenXR smoke procedure on named hardware. Prove whether every tracker streams independent of default/duplicate role configuration. No alternate provider has been enabled or approved
2. Tune/verify controller-profile grip-to-wrist offsets. The implemented profile-aware defaults are labeled geometric assumptions, not measured presets
3. Validate the VR canvas/markers/footprints and all supported controller gestures on a headset, including focus loss and reconnection
4. Validate full-avatar spectator output, orientation/aspect/color/post-processing, obstruction, output cadence and temporal reset on OpenGL and Vulkan alongside real headset submission
5. Measure headset deadlines, allocations and spectator GPU/CPU cost on the named system
6. Demonstrate actual VR-toggle continuity when the OpenXR provider session changes. Unknown coordinate-basis relationships intentionally refuse restoration
7. Optional encoder/capture synchronization and calibration mirror are not certified by rendered-texture support

The [runtime guide](../../../developer-guides/vr/openxr-runtime.md), [calibration guide](../../../developer-guides/vr/full-body-calibration.md), [measurement guide](../../../developer-guides/vr/body-measurements.md), [spectator guide](../../../developer-guides/vr/spectator-camera.md) and [session guide](../../../developer-guides/vr/calibration-session-continuity.md) describe the available behavior and limits.
