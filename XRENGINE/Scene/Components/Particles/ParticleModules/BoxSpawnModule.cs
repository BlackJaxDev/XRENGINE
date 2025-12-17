using System.ComponentModel;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for box-based spawn shape.
/// </summary>
public class BoxSpawnModule : IParticleSpawnModule
{
    public string ModuleName => "BoxSpawn";
    public int Priority => 0;
    public bool Enabled { get; set; } = true;

    [Description("Half-extents of the spawn box.")]
    public Vector3 HalfExtents { get; set; } = Vector3.One;

    public string GetUniformDeclarations() => @"
uniform vec3 uSpawnBoxExtents;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uSpawnBoxExtents", HalfExtents);
    }

    public string GetSpawnShaderCode() => @"
// Box Spawn Module
vec3 randomOffset = (randomVec3(seed) * 2.0 - 1.0) * uSpawnBoxExtents;
particle.Position = uEmitterParams.EmitterPosition + randomOffset;
";
}
