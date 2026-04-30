using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for point-based spawn shape.
/// </summary>
public class PointSpawnModule : IParticleSpawnModule
{
    public string ModuleName => "PointSpawn";
    public int Priority => 0;
    public bool Enabled { get; set; } = true;

    public string GetUniformDeclarations() => "";

    public void SetUniforms(XRRenderProgram program)
    {
        // No uniforms needed for point spawn
    }

    public string GetSpawnShaderCode() => @"
// Point Spawn Module
particle.Position = uEmitterParams.EmitterPosition;
";
}
