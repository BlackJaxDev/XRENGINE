namespace XREngine.Scene.Components.Particles.Interfaces;

/// <summary>
/// Module that provides initialization data for particles when they spawn.
/// </summary>
public interface IParticleSpawnModule : IParticleModule
{
    /// <summary>
    /// Gets the GLSL code that initializes particle data.
    /// Available variables: particle (inout), spawnIndex (int), randomSeed (uint)
    /// </summary>
    string GetSpawnShaderCode();
}
