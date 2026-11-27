# MotionBricks Procedural Locomotion

This guide explains how XRENGINE runs NVIDIA MotionBricks (SOMA 30-joint human locomotion) inside `AnimStateMachineComponent` and couples procedural root motion to Jolt character physics. For a step-by-step editor setup walkthrough, see [MotionBricks Procedural Locomotion Setup](../../user-guide/motionbricks-animation.md).

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

`DesktopRuntimeBackendBootstrap.EnsureRegistered()` automatically calls `MotionBricksRuntimeBackend.Register(...)` during desktop startup to bind `MotionBricksLocomotionRuntimeServices.CurrentFactory` to `MotionBricksTensorRtBackend`. Custom hosts without `XREngine.Runtime.Bootstrap` call `MotionBricksRuntimeBackend.Register(...)` directly. Registration is lazy and does not load TensorRT, CUDA, ONNX weights, or worker threads until a scene activates a `MotionBricksLocomotionMotion` or calls `MotionBricksLocomotionRuntimeServices.Prewarm()`.

## Authoring Without Code In The Editor

You can configure dependencies, state machines, and event links directly inside the ImGui editor:

- **Dependencies & Builds panel (`Tools -> Dependencies & Builds...` or `View -> Dependencies & Builds`):** Discovers all `Tools/Dependencies/*.ps1` scripts (with parsed typed parameters), `Build/Native/*/*.vcxproj` C++ bridge projects, and managed build targets, shows staged/built status, and runs them asynchronously with live streaming output.
- **Animation State Machine Graph panel (`View -> Animation State Machine` or `Open State Machine Graph` on `AnimStateMachineComponent`):** Author layers, parameters, states, and transition conditions visually. The state creation menu uses the source-generated, AOT-safe `MotionFactoryRegistry` to instantiate any concrete `MotionBase` subclass (`AnimationClip`, `BlendTree1D`, `BlendTree2D`, `BlendTreeDirect`, `MotionBricksLocomotionMotion`, or custom motions) without reflection.
- **Inspector `XREvent` bindings:** `CharacterPawnComponent.RootMotionSteeringUpdated`, `LocomotionDebugToggled`, and `PauseToggled` use `XREvent` / `XREvent<T>`, so you can click `Add Callback`, select a scene node, and pick a compatible method (such as `CharacterRootMotionBridgeComponent.ApplySteeringSnapshot` or `MotionBricksDebugDrawComponent.ToggleRenderEnabled`) directly in the Inspector without code.

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
| `StyleParameter` | `LocomotionStyle` | `int` | Selects locomotion preset or style index (`0` = standing, `1` = crouched, `2` = prone). |
| `IsGroundedParameter` | `IsGrounded` | `bool` | Indicates whether the physics capsule has ground support. |
| `VerticalVelocityParameter` | `VerticalVelocity` | `float` | Supplies physics vertical velocity in m/s to drive airborne jump and fall pose phase. |

## Blending, Layering, And Live Pose Context Seeding

Because `MotionBricksLocomotionMotion` uses the same `AnimSlot` indices as traditional humanoid clips:

- **State transitions:** Standard `AnimStateTransition` cross-fades blend smoothly between `MotionBricksLocomotionMotion` and traditional `AnimationClip` states (such as `Jump`, `Fall`, `Land`, or `Emote`).
- **Live pose context seeding:** Whenever an `AnimStateMachine` contains a `MotionBricksLocomotionMotion`, `AnimStateMachineComponent` records evaluated humanoid muscles and root transforms into a preallocated 30 Hz ring buffer (`HumanoidPoseHistoryBuffer`). When a transition enters the MotionBricks state, `HumanoidToSomaPoseSampler` converts the last four 30 Hz frames into SOMA context poses so the first inference continues directly from the previous clip's live pose. State machines without MotionBricks allocate no pose history buffer.
- **Multi-layer composition:** Place `MotionBricksLocomotionMotion` on the base layer (`Override`) and add upper-body `Override` layers (for aiming, waving, or carrying items) or `Additive` layers (for breathing or leaning) above it.

## Character Control And Jolt Root Motion

Use three components together for root-motion locomotion (first-person, third-person, VR, or AI):

1. **`CharacterPawnComponent` (plus optional `ThirdPersonCameraRigComponent`):** Set `UseRootMotionSteering = true` (and optional `StrafeMode` / `KeyboardWalkMagnitude`). The pawn projects keyboard (`WASD`, hold `Shift` to run, `Space` to jump, `C` to crouch, `Z` for prone) and analog gamepad stick input onto the camera XZ plane without normalizing analog stick magnitude to `1.0`, and publishes `SteeringSnapshot` (using `ELocomotionStyle`) through `RootMotionSteeringUpdated`. For third-person rigs, attach `ThirdPersonCameraRigComponent` so `Q`/`E` (or gamepad `LB`/`RB`) toggle lowered left/right shoulder views, holding `R`/`T` (or `D-Pad Up`/`Down`) zooms the centered view, and vertical pivot height glides smoothly across standing, crouched, prone, and airborne states.
2. **`CharacterMovement3DComponent` with `RootMotionMovementModule`:** Assign `RootMotionMovementModule` to `MovementModule` with `TickInputWithPhysics = true`. On grounded steps, it converts accumulated frame root-motion deltas into a Jolt `CharacterMotionInputModel.Velocity` command in `ETickGroup.PrePhysics` right before `StepSimulation`, so `JoltCharacterVirtualController` handles floor sticking, stairs, slopes, and walls with zero FIFO queue lag. When grounded support drops, it switches to Jolt gravity and optional air control. After each fixed step, `CharacterMovement3DComponent` publishes `CharacterMovementPhysicsStepReport` via `PhysicsStepCompleted`.
3. **`CharacterRootMotionBridgeComponent`:** Connects the pawn, `AnimStateMachineComponent`, and `CharacterMovement3DComponent` through the `IProceduralLocomotionMotion` contract:
   - Writes `SteeringSnapshot` into the state machine's locomotion parameters (deferring `IsGrounded` solely to Jolt physics once physics feedback starts).
   - Configures `AnimStateMachineComponent.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer` and forwards `RootMotionEvaluated` world deltas into `RootMotionMovementModule.AccumulateRootMotionDelta(...)`.
   - Configures all `IProceduralLocomotionMotion` states across every layer (including late-entered or rebuilt states) with initial facing yaw, avatar motion scale, and `PhysicsMotionBricksTrajectoryProber`.
   - Feeds `PhysicsStepCompleted` back into `IProceduralLocomotionMotion.ApplyPhysicsRootReconciliation(...)` so walls clamp the procedural root and prevent visual drift from the Jolt capsule, and writes `IsGrounded` back into the state machine to trigger airborne transitions.

## Performance And Diagnostics

- **Zero per-frame allocations:** Steady-state `MotionBricksAsyncLocomotionDriver`, `SomaSmartLocomotionPlanner`, `SomaToHumanoidMuscleConverter`, `HumanoidToSomaPoseSampler`, `HumanoidPoseHistoryBuffer`, `SomaTensorCodec`, and `RootMotionMovementModule` paths preallocate all buffers and produce zero managed allocations per frame.
- **Terrain- and collision-aware future trajectory projection:** `CharacterRootMotionBridgeComponent` binds `PhysicsMotionBricksTrajectoryProber` (`IMotionBricksTrajectoryProber`) to the active driver. Before each replan, `SomaSmartLocomotionPlanner.ProjectFutureTrajectory(...)` integrates 8 future ground trajectory points along the steering arc, queries static physics geometry for obstacle deflection and terrain elevation, and feeds the resolved ground endpoint to SOMA. When `SomaSmartPrimitiveAssetLoader` and `SomaBvhClipLoader` load staged SOMA BVH mocap presets (`SomaLocomotionPoseBank.HasMocapTargets == true`), inference conditions target local poses (`has_local_poses = 1`) for standing, crouch, idle, and jump clips. When no mocap presets are staged, active walking and running leave target limb poses unconstrained (`has_local_poses = 0`) and condition only idle, stance changes, and instability recovery.
- **SOMA N1 flat-foot calibration:** `SomaRestSkeletonCalibration.SomaN1Default` matches SOMA's standing ankle-to-toe (`-17.5°`) and toe-base (`-11.5°`) rest pitch so flat-footed standing maps to neutral `FootUpDown` and `ToesUpDown` muscle values.
- **Unit-testing debug toggles:** Unit-testing bootstrap always attaches `MotionBricksDebugDrawComponent` (green and red-orange projected ground dots, current teal SOMA skeleton with foot-contact markers, yellow mid-horizon future skeleton, and magenta end-horizon future skeleton) with `RenderEnabled` initialized from `MotionBricksDebugVisualization` (default `false`) in `Assets/UnitTestingWorldSettings.jsonc`. Press `F` at runtime to toggle debug visualization. Enable `ShowAvatarCullingBoxes` (or per-model `RenderBounds` inside `ModelsToImport`) to draw culling boxes only on the avatar.
- **Uncontested eye gaze and blink:** `HumanoidProceduralEyeGazeComponent` drives eye look and blink muscles independently because MotionBricks leaves eye, jaw, and finger channels untouched.
- **Explicit failure reporting:** If TensorRT, CUDA, or SOMA ONNX weights are unavailable, `IMotionBricksLocomotionDriver.IsReady` returns `false` and `DiagnosticStatus` reports the exact remediation steps. The runtime never silently substitutes CPU inference.
- **Numeric telemetry:** Inspect `CompletedReplanCount`, `ContextSeedCount`, `LastDecodedFrameCount`, `LastInferenceMilliseconds`, and `ActiveBufferHeadroomFrames` on `MotionBricksAsyncLocomotionDriver`.

## Tests And Key Source Files

- Native TensorRT bridge: `Build/Native/MotionBricksBridge/`
- Weight staging script: `Tools/Dependencies/Get-MotionBricks.ps1`
- State machine contracts and motion asset: `XREngine.Animation/Property/Core/MotionBricksLocomotionMotion.cs`, `HumanoidPoseHistoryBuffer.cs`
- SOMA runtime, codec, planner, and retargeters: `XREngine.Animation.MotionBricks/`
- Jolt root-motion module and character bridge: `XREngine.Runtime.Core/Scene/Components/Movement/Modules/RootMotionMovementModule.cs`, `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/CharacterRootMotionBridgeComponent.cs`
- Unit and Jolt integration tests: `XREngine.UnitTests/Animation/MotionBricks/`
- Related procedural motions, terrain IK, and physical animation guide: [procedural-motions.md](procedural-motions.md)
