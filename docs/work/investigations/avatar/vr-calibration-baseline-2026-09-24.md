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

The required persistence contract is preserved as the explicit NUnit test `Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates`. It checks every target's non-null state and reference identity across five ticks, plus recalibration node count. It currently fails by design when explicitly selected. After the ownership repair, remove its `Explicit` annotation and replace the old loss-characterization expectation with the corrected contract.

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
