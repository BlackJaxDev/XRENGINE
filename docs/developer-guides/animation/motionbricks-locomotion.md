# MotionBricks Procedural Locomotion

This guide explains how XRENGINE runs NVIDIA MotionBricks (SOMA 30-joint human locomotion) inside `AnimStateMachineComponent` and couples procedural root motion to Jolt character physics.

## Architecture Overview

`MotionBricksLocomotionMotion` is a `MotionBase` asset inside `XREngine.Animation`. You place it in an `AnimState` alongside traditional `AnimationClip` and `BlendTree` states.

- **Shared animation slots:** `MotionBricksLocomotionMotion` registers the 49 canonical body and limb `EHumanoidValue` channels (`Spine`, `Chest`, `UpperChest`, `Neck`, `Head`, shoulders, arms, hands, legs, feet, and toes) through the same `SceneNode -> GetComponentInHierarchy("HumanoidComponent") -> SetImportedRawValue` binding path as imported Unity humanoid clips.
- **Uncontested facial and finger layers:** MotionBricks does not write finger, jaw, or eye channels. Gesture, hand, and facial layers keep ownership of those slots.
- **Native TensorRT isolation:** `XREngine.Animation` has no dependency on TensorRT or CUDA. The TensorRT runtime lives in `XREngine.Animation.MotionBricks` and `Build/Native/MotionBricksBridge/` (`MotionBricksBridge.Native.dll`), wired through `IMotionBricksLocomotionDriver` and `MotionBricksLocomotionRuntimeServices.CurrentFactory`.
- **Automatic retargeting:** `SomaToHumanoidMuscleConverter` maps fixed 30-joint SOMA poses into canonical normalized humanoid muscles and scales planar stride displacement by `avatarLegScale`. `HumanoidComponent` retargets those muscles to any humanoid avatar rig.

## Staging Dependencies And Weights

MotionBricks weights and TensorRT binaries are external dependencies and are never committed to Git.

1. Install the CUDA 12.x toolkit and stage the TensorRT 10.x SDK under `Build/Dependencies/TensorRT/` with `Tools/Dependencies/Get-TensorRT.ps1`.
2. Download and SHA-256 verify the SOMA model contract, FP16 ONNX weights, and Smart Locomotion preset files under `Build/Dependencies/MotionBricks/`:

   ```powershell
   pwsh Tools/Dependencies/Get-MotionBricks.ps1
   ```

   Pass `-IncludeFp32` to also stage the FP32 ONNX weights.
3. Build `Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj` (`x64`). When TensorRT and CUDA headers are present, `MotionBricksBridge.Native.dll` compiles with full TensorRT 10.x execution and `.plan` engine caching. When either SDK is missing, the bridge builds in explicit unavailable mode and reports a diagnostic error instead of falling back to CPU execution.

## Registering The Runtime Backend

Call `MotionBricksRuntimeBackend.RegisterTensorRtBackend(...)` during application startup to bind `MotionBricksLocomotionRuntimeServices.CurrentFactory` to `MotionBricksTensorRtBackend`.

Each active `MotionBricksLocomotionMotion` receives a `MotionBricksAsyncLocomotionDriver` instance with:

- `SomaSmartLocomotionPlanner`: converts joystick steering into smooth 1D linear or 2D directional/strafe SOMA future target poses using critically damped springs and dynamic token horizons (`6` to `16` tokens).
- A dedicated `MotionBricks-Worker` background thread with preallocated double-buffered trajectory storage (`SomaTrajectoryPlaybackBuffer`).
- A `50 ms` splice cross-blend whenever a newly planned trajectory replaces the active buffer.

## State Machine Parameters

`MotionBricksLocomotionMotion` reads locomotion inputs from `AnimStateMachine.Variables` on every animation tick. Each parameter name is configurable on the motion asset:

| Property | Default Parameter | Type | Purpose |
|---|---|---|---|
| `MoveXParameter` | `MoveX` | `float` | Desired world-space X movement (`-1` to `1`). |
| `MoveYParameter` | `MoveY` | `float` | Desired world-space Z movement (`-1` to `1`). |
| `DesiredFacingYawParameter` | `DesiredFacingYaw` | `float` | Target facing yaw in radians (used in strafe mode). |
| `StrafeModeParameter` | `StrafeMode` | `bool` | Separates character facing yaw from travel direction when `true`. |
| `StyleParameter` | `LocomotionStyle` | `int` | Selects locomotion preset or style index. |
| `IsGroundedParameter` | `IsGrounded` | `bool` | Indicates whether the physics capsule has ground support. |

## Blending, Layering, And Live Pose Context Seeding

Because `MotionBricksLocomotionMotion` uses the same `AnimSlot` indices as traditional humanoid clips:

- **State transitions:** Standard `AnimStateTransition` cross-fades blend smoothly between `MotionBricksLocomotionMotion` and traditional `AnimationClip` states (such as `Jump`, `Fall`, `Land`, or `Emote`).
- **Live pose context seeding:** Whenever an `AnimStateMachine` contains a `MotionBricksLocomotionMotion`, `AnimStateMachineComponent` records evaluated humanoid muscles and root transforms into a preallocated 30 Hz ring buffer (`HumanoidPoseHistoryBuffer`). When a transition enters the MotionBricks state, `HumanoidToSomaPoseSampler` converts the last four 30 Hz frames into SOMA context poses so the first inference continues directly from the previous clip's live pose. State machines without MotionBricks allocate no pose history buffer.
- **Multi-layer composition:** Place `MotionBricksLocomotionMotion` on the base layer (`Override`) and add upper-body `Override` layers (for aiming, waving, or carrying items) or `Additive` layers (for breathing or leaning) above it.

## Third-Person Control And Jolt Root Motion

Use three components together for third-person root-motion locomotion:

1. **`CharacterPawnComponent`:** Set `UseRootMotionSteering = true` (and optional `StrafeMode` / `KeyboardWalkMagnitude`). The pawn projects keyboard and analog gamepad stick input onto the camera XZ plane without normalizing analog stick magnitude to `1.0`, and publishes `ThirdPersonSteeringSnapshot` through `RootMotionSteeringUpdated`.
2. **`CharacterMovement3DComponent` with `RootMotionMovementModule`:** Assign `RootMotionMovementModule` to `MovementModule`. On grounded steps, it converts accumulated frame root-motion deltas into a Jolt `CharacterMotionInputModel.Velocity` command so `JoltCharacterVirtualController` handles floor sticking, stairs, slopes, and walls. When grounded support drops, it switches to Jolt gravity and optional air control. After each fixed step, `CharacterMovement3DComponent` publishes `CharacterMovementPhysicsStepReport` via `PhysicsStepCompleted`.
3. **`ThirdPersonRootMotionBridgeComponent`:** Connects the pawn, `AnimStateMachineComponent`, and `CharacterMovement3DComponent`:
   - Writes `ThirdPersonSteeringSnapshot` into the state machine's locomotion parameters.
   - Configures `AnimStateMachineComponent.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer` and forwards `RootMotionEvaluated` world deltas into `RootMotionMovementModule.AccumulateRootMotionDelta(...)`.
   - Feeds `PhysicsStepCompleted` back into `IMotionBricksLocomotionDriver.ApplyPhysicsRootReconciliation(...)` so walls clamp the procedural root and prevent visual drift from the Jolt capsule, and writes `IsGrounded` back into the state machine to trigger airborne transitions.

## Performance And Diagnostics

- **Zero per-frame allocations:** Steady-state `MotionBricksAsyncLocomotionDriver`, `SomaSmartLocomotionPlanner`, `SomaToHumanoidMuscleConverter`, `HumanoidToSomaPoseSampler`, `HumanoidPoseHistoryBuffer`, `SomaTensorCodec`, and `RootMotionMovementModule` paths preallocate all buffers and produce zero managed allocations per frame.
- **Explicit failure reporting:** If TensorRT, CUDA, or SOMA ONNX weights are unavailable, `IMotionBricksLocomotionDriver.IsReady` returns `false` and `DiagnosticStatus` reports the exact remediation steps. The runtime never silently substitutes CPU inference.
- **Numeric telemetry:** Inspect `CompletedReplanCount`, `ContextSeedCount`, `LastDecodedFrameCount`, `LastInferenceMilliseconds`, and `ActiveBufferHeadroomFrames` on `MotionBricksAsyncLocomotionDriver`.

## Tests And Key Source Files

- Native TensorRT bridge: `Build/Native/MotionBricksBridge/`
- Weight staging script: `Tools/Dependencies/Get-MotionBricks.ps1`
- State machine contracts and motion asset: `XREngine.Animation/Property/Core/MotionBricksLocomotionMotion.cs`, `HumanoidPoseHistoryBuffer.cs`
- SOMA runtime, codec, planner, and retargeters: `XREngine.Animation.MotionBricks/`
- Jolt root-motion module and third-person bridge: `XREngine.Runtime.Core/Scene/Components/Movement/Modules/RootMotionMovementModule.cs`, `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/ThirdPersonRootMotionBridgeComponent.cs`
- Unit and Jolt integration tests: `XREngine.UnitTests/Animation/MotionBricks/`
