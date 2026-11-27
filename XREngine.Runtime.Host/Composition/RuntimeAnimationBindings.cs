using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Scene.Transforms;

// Connects animation tracks to the Transform properties they change during playback.
// UnityModelAnimationBuilder.AddTransform creates tracks named Translation, Rotation, and Scale.
// Each attribute tells RuntimeContractGenerator to generate a direct property assignment,
// such as (target, value) => target.Translation = value, for that track name and value type.
// RuntimeAssetBootstrap installs these setters in AnimationMemberBindingRegistry.
// AnimationMember finds the setter when it binds a track to a Transform. During playback,
// it calls the cached setter with the value calculated for the current animation time.
// This gives the track a compiled assignment instead of setting a property through reflection.
[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Translation))]
[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Rotation))]
[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Scale))]

// Roots SteeringSnapshot and the MotionBricks persistent-call targets so UnitTesting and
// published NativeAOT builds resolve and invoke root-motion steering and debug toggles
// through generated accessors without reflective type scans or method binding.
[assembly: XREngine.RuntimeClosedFormatter(typeof(SteeringSnapshot))]
[assembly: XREngine.RuntimeMemberAccess(typeof(CharacterRootMotionBridgeComponent), nameof(CharacterRootMotionBridgeComponent.ApplySteeringSnapshot))]
[assembly: XREngine.RuntimeMemberAccess(typeof(MotionBricksDebugDrawComponent), nameof(MotionBricksDebugDrawComponent.ToggleRenderEnabled))]
