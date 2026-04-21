using XREngine.Data.Rendering;

namespace XREngine.Scene.Components.Particles.Interfaces;

/// <summary>
/// Defines a buffer used by a particle module.
/// </summary>
public record ParticleBufferDefinition(
    string Name,
    EComponentType ComponentType,
    uint ComponentCount,
    bool PerParticle = true);
