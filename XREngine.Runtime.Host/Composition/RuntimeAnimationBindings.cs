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
