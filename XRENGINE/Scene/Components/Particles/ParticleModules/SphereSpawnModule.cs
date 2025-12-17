using System.ComponentModel;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for sphere-based spawn shape.
/// </summary>
public class SphereSpawnModule : IParticleSpawnModule
{
    public string ModuleName => "SphereSpawn";
    public int Priority => 0;
    public bool Enabled { get; set; } = true;

    [Description("Radius of the spawn sphere.")]
    public float Radius { get; set; } = 1.0f;

    [Description("Whether to spawn only on the surface.")]
    public bool SurfaceOnly { get; set; } = false;

    public string GetUniformDeclarations() => @"
uniform float uSpawnRadius;
uniform int uSpawnSurfaceOnly;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uSpawnRadius", Radius);
        program.Uniform("uSpawnSurfaceOnly", SurfaceOnly ? 1 : 0);
    }

    public string GetSpawnShaderCode() => @"
// Sphere Spawn Module
vec3 randomDir = randomDirection(seed);
float dist = (uSpawnSurfaceOnly != 0) ? uSpawnRadius : uSpawnRadius * randomFloat(seed);
particle.Position = uEmitterParams.EmitterPosition + randomDir * dist;
";
}
