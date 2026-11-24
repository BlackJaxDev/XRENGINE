# Procedural Motions, Terrain IK, And Physical Animation

XRENGINE supports modular procedural motions that compose inside `AnimStateMachine.Layers` and combine with post-animation world-space IK and physics drives without per-frame heap allocations.

## Layer Composition And Avatar Masks

`AnimStateMachine` evaluates layers in order (`Layer 0` through `Layer N`) into a shared `AnimationValueStore`:

- **`AnimLayer.AvatarMask` (`EHumanoidBodyMask`):** Filters float muscle slots, translation DOFs, limb IK slots, root-motion contributions (`EHumanoidBodyMask.Root`), and non-humanoid slots (`EHumanoidBodyMask.NonHumanoid`) with one bitwise mask check per slot.
- Clearing `EHumanoidBodyMask.Root` on upper-body layers ensures full-body clips played on upper layers never overwrite or attenuate Layer 0's locomotion root motion.
- Configure layer masks in the **Layers** tab of **View -> Animation State Machine** using presets (`All`, `Upper`, `Lower`, `Hands`, `Face`, `None`) or per-region checkboxes.

## Built-In Procedural `MotionBase` Assets

All concrete `MotionBase` types are automatically discovered by `RuntimeFactoryGenerator` and exposed in `MotionFactoryRegistry` for AOT-safe creation in the State Machine Graph panel:

| Motion Type | Typical Layer | Purpose |
|---|---|---|
| `MotionBricksLocomotionMotion` | Base (`Override`) | Neural SOMA TensorRT full-body locomotion implementing `IProceduralLocomotionMotion`. |
| `ProceduralStrideMotion` | Base (`Override`) | Pure-CPU zero-asset foot-stepping locomotion implementing `IProceduralLocomotionMotion`. Supports walking, running, strafing, slow micro-step shuffles, turn-in-place pivoting, and `ELocomotionStyle` stances. |
| `MotionWarpingMotion` | Base (`Override`) | Wraps an `AnimationClip` with `MotionWarpWindow` intervals to align root translation and yaw onto runtime `MotionWarpTarget` transforms (for example vaulting, mantling, or seating). |
| `HumanoidLookAtAimMotion` | Upper (`Additive` or `Override`) | Distributes critically damped yaw and pitch look-at offsets across spine, chest, upper chest, neck, head, and optional arm aim offsets (`AimYaw`, `AimPitch`, `AimWeight`, `ArmAimWeight`), leaving eye channels to `HumanoidProceduralEyeGazeComponent`. |
| `ProceduralSecondaryMotion` | Secondary (`Additive`) | Adds exertion-scaled chest/shoulder breathing, acceleration pitch lean, and turn-rate spine banking (`MoveX`, `MoveY`, `DesiredFacingYaw`, `Exertion`). |

## Runtime Helper And Post-Animation Components

- **`HumanoidLookTargetDriverComponent`** (`XREngine.Runtime.AnimationIntegration`): Converts a world `TargetTransform` (or explicit `TargetWorldPosition`) into avatar-local `AimYaw`, `AimPitch`, and attenuated `AimWeight` parameters on `AnimStateMachineComponent`.
- **`HumanoidFootPlacementIKComponent`** (`XREngine.Runtime.AnimationIntegration`): Runs immediately after `ETickOrder.Animation` on top of any base locomotion source. Probes static physics geometry (or an injected `IFootGroundProber`), drops `Humanoid.Hips` within safe leg reach on slopes and stairs, locks planted stance feet against horizontal sliding, solves two-bone world-space leg IK, and tilts ankles to match ground normals.
- **`PhysicsTraversalProber`** (`XREngine.Runtime.AnimationIntegration`): Sweeps static physics geometry in front of a character to measure obstacle front face, top ledge height, ledge depth, and landing clearance, returning a `TraversalLedgeProbeResult` that converts directly to a `MotionWarpTarget`.
- **`VrLowerBodyLocomotionBridgeComponent`** (`XREngine.Runtime.InputIntegration`): Derives planar velocity, facing yaw, and `ELocomotionStyle` stance from calibrated VR head and optional pelvis tracking, publishing `SteeringSnapshot` via `XREvent<SteeringSnapshot>` (`SteeringUpdated`) to drive lower-body procedural locomotion alongside upper-body VR IK (`CharacterRootMotionBridgeComponent.ConfigureLayerAvatarMask(0, EHumanoidBodyMask.LowerBody)`).
- **`HumanoidPhysicalAnimationComponent`** (`XREngine.Runtime.AnimationIntegration`): Applies per-region (`EHumanoidBodyMask`) proportional-derivative linear and angular spring drives toward animated humanoid bones on `IAbstractDynamicRigidBody` instances. Supports localized `TriggerHitReaction(...)`, Inspector-bindable `TriggerUpperBodyHitReaction()`, `EnterRagdoll()`, and smooth `BeginGetUpRecovery(...)`.
