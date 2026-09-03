namespace XREngine.Rendering;

/// <summary>
/// Logical render lanes dedicated to specialized geometry and simulation effects.
/// </summary>
public enum EAdvancedSpecialEffectLane : uint
{
    Water = 0u,
    HairCards = 1u,
    Particles = 2u,
    Trails = 3u,
    Beams = 4u,
    Portals = 5u,
    Mirrors = 6u,
    VolumetricFog = 7u,
}
