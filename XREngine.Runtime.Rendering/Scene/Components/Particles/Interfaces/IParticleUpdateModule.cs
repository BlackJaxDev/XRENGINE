namespace XREngine.Scene.Components.Particles.Interfaces;

/// <summary>
/// Module that updates particles each frame.
/// </summary>
public interface IParticleUpdateModule : IParticleModule
{
    /// <summary>
    /// Gets the GLSL code that updates particle data.
    /// Available variables: particle (inout), deltaTime (float), particleIndex (int)
    /// </summary>
    string GetUpdateShaderCode();
}
