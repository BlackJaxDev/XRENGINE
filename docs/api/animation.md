# Animation System

The animation module in XRENGINE mixes traditional timeline playback, data-driven property animation, blend trees, state machines, and inverse kinematics to drive characters and props. This document explains how those systems are structured in the repository and how they work together at runtime.

## Design Goals
- Treat animations as reusable assets (`XRAsset`) that can be authoring-time data or generated on the fly.
- Decouple animation data from scene graph objects so clips can target arbitrary properties, not just skeletal transforms.
- Support multiple blend strategies (1D, 2D, direct) with deterministic evaluation order.
- Provide extensible state machines with layered blending, parameter-driven transitions, and deterministic root-motion output.
- Offer humanoid helpers and full body IK that auto-detect rig structure and can be driven from VR tracking or procedural inputs.
- Allow authoring data to be baked for runtime efficiency while keeping keyframe precision for editing tools.

## Timeline Primitives

- **BaseAnimation** (`XREngine.Animation/BaseAnimation.cs`) is the core timeline abstraction. It tracks length, playback speed (including reverse playback), loop state, current time, and raises start/stop/pause notifications. The `Tick` method advances time, handles loop remapping, and delegates per-frame logic to subclasses via `OnProgressed`.
- **Property Animations** inherit from `BasePropAnim` (`Property/Core/BasePropAnim.cs`). These classes evaluate a value for a given time (`GetCurrentValueGeneric`) and optionally expose baked frame data. Vector implementations like `PropAnimFloat`, `PropAnimVector3`, and `PropAnimQuaternion` support keyframes, smoothing, tangent evaluation, velocity/acceleration tracking, and optional frame-rate constraints for lower-resolution data. Baking (see `PropAnimVector.Bake`) precomputes values into arrays when runtime cost must be minimized.
- **AnimationMember** (`Property/Core/AnimationMember.cs`) represents a binding from a clip to a field, property, or method on a target object. Members build a tree mirroring the scene hierarchy or object graph. They cache reflection lookups using ImmediateReflection to minimize per-frame overhead and store each member’s default value so blends can bias toward rest pose.

## Motion Assets

- **MotionBase** (`Property/Core/MotionBase.cs`) collects animation outputs into dictionaries keyed by member paths (e.g., `SceneNode/Transform/SetWorldRotation`). Each motion registers its members, evaluates child motions, and exposes helper methods to blend the resulting dictionaries (linear, tri-linear, quad-linear) depending on blend tree dimensionality.
- **AnimationClip** (`Property/Core/AnimationClip.cs`) owns a tree of `AnimationMember` nodes. When started, it registers all animations, tracks how many sub-animations are active, and loops or stops once they have finished. During evaluation it computes weighted values per member (default-to-animated interpolation) and populates the parent motion’s value dictionary. The clip can import MikuMikuDance VMD files, generating property animations and IK bindings directly from third-party data.
- **Blend Trees**:
  - `BlendTree1D` sorts children by threshold and blends between the two bounding motions based on a float parameter.
  - `BlendTree2D` supports Cartesian (bilinear), barycentric, and directional modes. It pre-sorts children along axes, finds bounding motions, and computes weights using inverse-distance, barycentric, or directional logic.
  - `BlendTreeDirect` (not shown above but present in `Property/Core`) maps inputs to weights explicitly, useful for additive or scripted blending.
  Each blend tree tick calls into child motions, gathers their value dictionaries, and blends the results into the parent motion using the helper methods on `MotionBase`.

## State Machine Architecture

- **AnimStateMachine** (`State Machine/AnimStateMachine.cs`) aggregates layers, variables, and root motion data. It maintains dictionaries of default values and currently blended values, calling `ApplyAnimationValues` to write results back through `AnimationMember.ApplyAnimationValue`. Root motion support stores pivot delta information so callers can decide how to apply movement.
- **Variables** (`State Machine/Parameters`) are strongly typed wrappers (`AnimFloat`, `AnimInt`, `AnimBool`) derived from `AnimVar`. They expose getter/setter pairs, bit-packing helpers for replication, and comparison logic used by `AnimTransitionCondition`. The state machine raises a `VariableChanged` event whenever a parameter mutates.
- **AnimLayer** (`State Machine/Layers/AnimLayer.cs`) owns states, applies layer-level weights, and defines whether values override or add to previous layers. It maintains a dictionary of animated values for the current evaluation pass and uses `BlendManager` to transition between states smoothly. The layer also contains an `AnyState` for global transitions and keeps track of `CurrentState`, `NextState`, and the active `AnimStateTransition` object.
- **AnimState** (`State Machine/Layers/States/AnimState.cs`) associates a `MotionBase` (clip or blend tree) with optional components (custom per-state logic) and metadata such as start/end time slices. States call `Motion.EvaluateRootMotion`, tick their property animations, and notify components of enter/exit events.
- **Transitions** (`State Machine/Layers/States/Transitions`) capture duration, blend curve (linear, cosine, quadratic, or custom via `PropAnimFloat`), priority, exit time, interruption behavior, and conditions. `AnimTransitionCondition` compares variables using numeric or boolean comparisons and caches parameter names for fast lookup. `BlendManager` computes a normalized blend clock, applies easing, and writes blended values back into the layer dictionary each frame until the transition completes.

## Runtime Components and Integration

- **AnimStateMachineComponent** (`XRENGINE/Scene/Components/AnimStateMachineComponent.cs`) is the runtime bridge. It instantiates a state machine, wires it into the scene node, registers per-frame ticks, and optionally links to a `HumanoidComponent` for convenience functions like `SetHumanoidValue`. It tracks which variables changed during evaluation, packs them into a bit buffer, and enqueues replication payloads labeled `PARAMS` so remote clients can stay in sync.
- **AnimationClipComponent** (`Scene/Components/Animation/AnimationClipComponent.cs`) provides a lightweight way to play a single clip on activation. It starts the clip, registers a tick, and forwards stop events when the component is disabled. This component is often used for simple looping ambient motions or testing imported data before wiring it into a state machine.
- Other systems hook into these components: `FaceTrackingReceiverComponent` writes incoming blendshape weights straight into state machine parameters, while VR controllers feed IK targets through the humanoid component.

## Humanoid Rigging and IK

- **HumanoidComponent** (`Scene/Components/Animation/HumanoidComponent.cs`) represents a rigged character. When added to a `SceneNode`, it traverses the hierarchy to locate bones by name pattern or spatial hints, populating `BoneDef` entries for hips, spine, limbs, fingers, and eyes. It caches bone bind poses, exposes setters for blendshape weights and bone transforms, and optionally renders debug lines for solved chains.
- The component stores IK targets as pairs of transform references plus calibration offsets. It can reset poses, clear targets, and toggle IK per limb. During late tick it calls `InverseKinematics.SolveFullBodyIK`, a FABRIK-style solver implemented in `Scene/Components/Animation/IK/InverseKinematics.cs`. That solver supports single-target and dual-target chains, optional elbow/knee/chest goals, and constraint hooks for future limit enforcement.
- **IK Components** (`Scene/Components/Animation/IK/`) include:
  - `HumanoidIKSolverComponent` for humanoid-specific solver management.
  - `VRIKSolverComponent` plus `VRIKCalibrator` to map VR device poses into the humanoid skeleton, including calibration data for headset/hand/foot offsets.
  - `IKRotationConstraintComponent` and `IKHingeConstraintComponent` to clamp joint ranges.
  - `SingleTargetIKComponent` for simple chains outside the humanoid rig.

## Data Import and Authoring

- **External Formats**: `AnimationClip` can load `.vmd` files (`Load3rdParty` / `LoadFromVMD`). During import it constructs property animations for bone translation and rotation, respecting Bézier curves baked into the source file, and optionally routes foot targets through humanoid IK APIs.
- **Authoring Blendshape Animations**: `AnimationMember.SetBlendshapeNormalized` and related helpers build method-call animations that drive `ModelComponent` blendshapes. Because members can cache method call results, the system avoids repeated scene graph searches each frame.
- **Baking & Optimization**: Property animation classes expose options like `ConstrainKeyframedFPS`, `LerpConstrainedFPS`, and `Bake(framesPerSecond)` so authoring tools can reduce runtime evaluation cost while preserving spline fidelity. The `GetMinMax` helpers on `PropAnimVector` compute extrema for editor bounds checking or LOD heuristics.

## Performance Considerations

- Reflection lookups are cached the first time an animation member initializes against a target. Subsequent frames reuse `ImmediateReflection` handles and cached default values.
- Motion evaluation populates dictionaries once per tick and reuses them during blending to avoid allocation.
- Blend trees pre-sort children and reuse temporary arrays for bounding child search, preventing per-frame allocations even with large blends.
- Networking writes only the bits for parameters that changed in the last evaluation pass, keeping replication payloads minimal.

## Extending the System

- Add new timeline types by deriving from `BaseAnimation` or `BasePropAnim`. Override `OnProgressed` to compute custom values.
- Create procedural motions by subclassing `MotionBase` and populating `_animationValues` directly.
- Register custom state components by inheriting from `AnimStateComponent` (located with states) to inject gameplay logic on enter/exit/tick without modifying the state machine runtime.
- Extend IK by adding solvers under `Scene/Components/Animation/IK/Solvers` and exposing them through `BaseIKSolverComponent`.

## Related Documentation
- [Component System](components.md)
- [Scene System](scene.md)
- [Rendering System](rendering.md)
- [Physics System](physics.md)
- [VR Development](vr-development.md)