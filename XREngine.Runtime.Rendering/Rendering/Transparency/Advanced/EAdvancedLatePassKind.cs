namespace XREngine.Rendering;

/// <summary>
/// Categories of late and post-visibility passes in the Advanced Render Pipeline.
/// </summary>
public enum EAdvancedLatePassKind : uint
{
    SortedAlpha = 0u,
    ParticipatingTransparency = 1u,
    Refraction = 2u,
    WeightedBlendedOit = 3u,
    Ppll = 4u,
    DepthPeeling = 5u,
    VolumetricFog = 6u,
    SpecialEffects = 7u,
    OnTopOverlay = 8u,
    UserInterface = 9u,
}
