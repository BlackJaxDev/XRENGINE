using System.ComponentModel;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for size over lifetime.
/// </summary>
public class SizeOverLifetimeModule : IParticleUpdateModule
{
    public string ModuleName => "SizeOverLifetime";
    public int Priority => 210;
    public bool Enabled { get; set; } = true;

    [Description("Size multiplier at particle birth.")]
    public float StartSize { get; set; } = 1.0f;

    [Description("Size multiplier at particle death.")]
    public float EndSize { get; set; } = 0.0f;

    public string GetUniformDeclarations() => @"
uniform float uSizeStart;
uniform float uSizeEnd;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uSizeStart", StartSize);
        program.Uniform("uSizeEnd", EndSize);
    }

    public string GetUpdateShaderCode() => @"
// Size Over Lifetime Module
float sizeMult = mix(uSizeStart, uSizeEnd, lifeRatio);
particle.Scale = particle.Scale * sizeMult;
";
}
