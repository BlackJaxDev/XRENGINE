namespace XREngine.Scene.Components.Particles.Interfaces;

/// <summary>
/// Module that provides additional buffer data for particles.
/// </summary>
public interface IParticleBufferModule : IParticleModule
{
    /// <summary>
    /// Gets the buffer definitions for this module.
    /// </summary>
    IEnumerable<ParticleBufferDefinition> GetBufferDefinitions();
}
