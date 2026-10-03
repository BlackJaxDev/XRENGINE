# Six-device VR calibration baseline

## Scope and source audit

Implementation baseline: `a9a1f9384` on September 24, 2026, with pre-existing local editor/OpenXR startup, FBX import, and rendering changes retained. The calibration todo was introduced by `3893e7e6d`; its review baseline is `4a0d4a2f6`.

At the start of this work, `VRPlayerCharacterComponent`, `HumanoidIKComponentBase`, `VRIKSolverComponent`, `VRIKCalibrator`, and `RuntimeVRIKCalibrator` matched that review baseline. The current calls still exhibit split target ownership: the player stores raw devices on the humanoid, calibration assigns concrete children directly to the solver, and `SyncSolverTargets` casts the humanoid references to `Transform` on every update. Raw `VRDeviceTransformBase` instances do not pass that cast.

Other reviewed findings remain present: independent nearest-tracker assignment, sticky `PoseAvailable` metadata, tracker collection clearing without owned-node destruction, ignored calibration return and fence result, and cancel followed by recalibration. This slice does not change those behaviors or establish tracker acquisition compatibility.

## Executable scene

The reusable fixture is [SyntheticVrCalibrationRig](../../../../XREngine.UnitTests/Animation/SyntheticVrCalibrationRig.cs). It builds a separate avatar and playspace, a fully mapped humanoid skeleton, VRIK, an HMD, two controllers, and exactly three body trackers. No asset import, physics world, renderer, or installed VR runtime is needed.

[SyntheticVrDeviceTransform](../../../../XREngine.UnitTests/Animation/SyntheticVrDeviceTransform.cs) inherits the production raw VR transform base. Each sample has a deterministic provider-qualified identity independent of its body slot, pose matrix, timestamp, connection flag, and independent position/orientation validity. These flags describe synthetic samples; they do not implement production tracking-loss policy. The fixture never replaces the global VR provider. There is no native `VrDevice`, so native `HasDevice` remains false.

The fixture publishes matrices in parent-first order because it has no world scheduler. Initialization suppresses solving until references are ready; subsequent ticks call the public `UpdateSolverExternal` path at full weight. Completed solver callbacks are counted. Calibration calls the actual runtime reflection bridge. Disposal destroys the owned scene.

## Blocker repaired

The first executable run failed before reaching target synchronization: `RuntimeVRIKCalibrator` could not resolve the calibration method. Inside the partial `VRIKCalibrator` class, the nested compatibility class `Settings` shadowed the file's same-named alias for `VRIKCalibrationSettings`. The public method therefore accepted the derived compatibility type while the bridge searched for the shared base type.

The alias and active parameter declarations now use the unambiguous name `CalibrationSettings`. The runtime bridge resolves successfully with ordinary `VRIKCalibrationSettings`. The nested settings class remains compatible as a derived argument. No dependency or assembly direction changed.

## Observed behavior

[VRIKCalibrationTests](../../../../XREngine.UnitTests/Animation/VRIKCalibrationTests.cs) executes the calibration and solver rather than inspecting source text.

| Observation | Before calibration | After calibration | After five solver updates |
|---|---:|---:|---:|
| Non-null solver targets | 0 | 6 | 0 |
| Device-child target nodes | 0 | 6 | 6 |
| Avatar/root scale | `(1, 1, 1)` | `(1, 1, 1)` | `(1, 1, 1)` |
| Tracking origin | Identity | Identity | Identity |
| Humanoid and solver enabled | Yes | Yes | Yes |

All six raw humanoid references and their calibrated child nodes remain present after synchronization clears the solver references. Recalibrating before a solver tick reuses the six children. Recalibrating after synchronization creates another six children: the explicit regression reports 12 rather than 6.

The positive control explicitly publishes the calibrated concrete targets to the humanoid in test setup. All six references then survive repeated updates, and a moved controller changes the wrist position through actual IK solving. This establishes a working harness, not a production ownership fix.

The required persistence contract is preserved as the explicit NUnit test `Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates`. It checks every target's non-null state and reference identity across five ticks, plus recalibration node count. At that baseline it failed when explicitly selected. The subsequent ownership repair below promotes it to the normal suite without weakening its assertions.

## Shared construction decision

Both `UnitTestingWorld.Pawns` and `BootstrapPawnFactory` duplicate character/flying VR pawn creation, HMD/controllers, tracker collection, and first-person desktop output. Only the editor's `InitializeLocomotion` currently creates `VRPlayerCharacterComponent` and wires calibration to `VRPlayerInputSet.IsMutedChanged`; the runtime factory has no equivalent avatar-calibration setup.

Use `BootstrapPawnFactory.CreatePlayerPawn`, already called by `BootstrapWorldFactory`, as the shared runtime construction entry point. Extract a shared runtime avatar-rig setup service alongside it when integrating production calibration; let the editor supply its UI and synthetic inputs. The private helper methods are not yet a shared API. This is a decision, not a completed extraction.

Pre-existing local changes in both paths always create the first-person desktop view and add a separate desktop `PawnComponent` when editing in VR. Those differ from the review baseline and are preserved. Relevant interactive profiles remain `Build-Editor` and `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`; this deterministic harness runs through NUnit instead.

## Validation and reproduction

The complete test-project build stops at the unrelated `AdvancedNativeShadingClosureContractTests` reference to the missing `IAdvancedGlobalIlluminationProvider`. All project dependencies built. A temporary compile selection under the task's ignored evidence directory built the three new files and existing `VRIKSolverComponentTests` and `HumanoidIKSolverComponentTests` against those runtime assemblies.

- Final focused run: **6 passed** (three new baseline/control/sample tests and three existing VRIK component tests), no new compiler warnings.
- Explicit persistence regression: **1 failed as expected**, including all six cleared target slots and the duplicate-node assertion.
- Broader neighboring IK run additionally found an existing `ContactCompensation_IsPostPoseAndCanTargetFeetSeparatelyFromHands` failure: `SkippedBodyFrameUnavailable` instead of `AppliedWithContactCompensation`. It is unrelated to calibration and remains unchanged.
- No headset, rendered-editor, SteamVR role-free acquisition, player confirm/cancel, or spectator acceptance is claimed. The fixture invokes the calibrator/solver boundary directly; it does not run the player's calibration state machine.

Normal commands, once the unrelated test compilation blocker is resolved:

```powershell
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter 'FullyQualifiedName~VRIKCalibrationTests|FullyQualifiedName~VRIKSolverComponentTests'
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter 'FullyQualifiedName=XREngine.UnitTests.Animation.VRIKCalibrationTests.Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates'
```

For isolated output, first reserve a task run with `pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun`, then pass `--artifacts-path Build/_AgentValidation/<run>/temp-build` and `--results-directory Build/_AgentValidation/<run>/reports`. No checked-in behavior depends on the temporary compile filter or saved binaries.

Disposable evidence: `Build/_AgentValidation/20260924-195314-vr-calibration-baseline/`, with build/test logs, NUnit TRX results, and the temporary compile selection. Findings needed for subsequent work are recorded above. The optional broker review was not launched because the available tool contract/routing response only supported deprecated GPT-5.6 models; no model substitution was attempted.

Next: repair authoritative target ownership, offset semantics, rebind/reuse, and teardown; then validate tracking acquisition and transactional player calibration. There has been no user-reported hardware validation of this change.

## Linux headless rerun (2026-09-30)

Rechecked active development commit `00233106ac1abcae4eca70357051182da04ae47b`. The calibration implementation and regression assertions are unchanged from the baseline. Normalizing the tracked Core directory to `XREngine.Runtime.Core/` allows the platform-neutral production dependency closure to compile on a case-sensitive Linux filesystem; no portability guards or production code were bypassed.

The first-class [headless test project](../../../../XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj) links the existing calibration fixture and test sources unchanged. NUnitLite runs them in-process without the editor, a window, GPU, VR runtime, or test-host IPC. It references the real animation/input integration projects, not substitutes.

- Linux build on .NET SDK 10.0.401: **0 warnings, 0 errors**.
- Default focused suite: **6 passed**, with the known explicit persistence regression skipped.
- Explicit persistence regression: **1 failed**, reproducing all six cleared target slots across five updates and the recalibration count of **12 instead of 6**.
- Positive control: all six concrete targets survive and a moved controller changes the wrist through actual IK.
- Production calibration was not repaired. No rendered-editor, headset, player confirm/cancel, or software-renderer acceptance is implied.

From the repository root, run the normal suite:

```sh
dotnet run --project XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj -- --workers=0 --result=<run>/reports/headless.xml
```

Select the persistence contract independently (it failed before the ownership repair below):

```sh
dotnet run --project XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj -- --workers=0 --test=XREngine.UnitTests.Animation.VRIKCalibrationTests.Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates --result=<run>/reports/persistence.xml
```

Reserve `<run>` under `Build/_AgentValidation/` as described in `AGENTS.md`. For isolated builds, pass `--artifacts-path <run>/temp-build` to `dotnet build`, then run its `XREngine.HeadlessTests.dll` directly. Restricted executors can build with `-m:1 -nr:false -p:UseSharedCompilation=false` to avoid MSBuild/compiler server IPC. The failing contract retains a nonzero process exit code; it is not converted into a passing test.

## Calibrated target ownership repair (2026-09-30)

`VRIKSolverComponent` now owns the concrete children for its six calibrated slots, independently of both the humanoid's raw source tuples and the solver's transient target fields. Synchronization resolves an owned child only when the humanoid still binds the corresponding source (or explicitly binds that child). A changed or cleared source no longer drives the old child. Raw source tuple offsets are preserved; the existing calibration offset formulas are unchanged.

Calibration reuses the owned child even if synchronization cleared the solver field, reparents it when its source changes, and unwraps an owned child passed back into calibration to its original source before computing offsets. Optional sources removed during calibration release their owned nodes and zero the corresponding positional/rotational weights. Explicit target clearing and solver destruction release owned nodes without destroying source devices or external targets. Ordinary component deactivation retains ownership for recalibration.

Verification against the development baseline `00233106ac1abcae4eca70357051182da04ae47b` plus this repair:

- Headless production-closure build: **0 warnings, 0 errors**.
- Default suite: **16 passed, 0 failed, 0 skipped**. The persistence contract is no longer `Explicit` and retains all target identity/non-null and six-node assertions.
- Positive control and a raw-source movement regression both drive the wrist through actual IK.
- Additional regressions cover clear/restore with non-identity tuple offsets, controller rebinding, calibrated children supplied as inputs, removal/reinstatement of optional trackers, external target preservation, explicit clearing, solver-only destruction with a surviving playspace, and recovery after immediate or deferred child-node destruction.

The clear/restore regression emulates the target-store transitions used during player calibration; it does not execute the player `BeginCalibration`, `EndCalibration`, or `CancelCalibration` state machine. Transactional cancellation, tracker acquisition/assignment, invalid-pose handling, new offset semantics, hardware VR, and the remaining full-body slots are not qualified by this repair. No SteamVR role assumptions were introduced.
