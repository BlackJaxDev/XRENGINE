using System.ComponentModel;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for basic gravity simulation.
/// </summary>
public class GravityModule : IParticleUpdateModule
{
    public string ModuleName => "Gravity";
    public int Priority => 100;
    public bool Enabled { get; set; } = true;

    [Description("Gravity vector applied to particles.")]
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    public string GetUniformDeclarations() => @"
uniform vec3 uGravity;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uGravity", Gravity);
    }

    public string GetUpdateShaderCode() => @"
// Gravity Module
particle.Velocity += uGravity * deltaTime;
";
}
