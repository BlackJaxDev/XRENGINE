using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for color over lifetime.
/// </summary>
public class ColorOverLifetimeModule : IParticleUpdateModule
{
    public string ModuleName => "ColorOverLifetime";
    public int Priority => 200;
    public bool Enabled { get; set; } = true;

    [Description("Start color at particle birth.")]
    public ColorF4 StartColor { get; set; } = ColorF4.White;

    [Description("End color at particle death.")]
    public ColorF4 EndColor { get; set; } = new ColorF4(1, 1, 1, 0);

    public string GetUniformDeclarations() => @"
uniform vec4 uColorStart;
uniform vec4 uColorEnd;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uColorStart", new Vector4(StartColor.R, StartColor.G, StartColor.B, StartColor.A));
        program.Uniform("uColorEnd", new Vector4(EndColor.R, EndColor.G, EndColor.B, EndColor.A));
    }

    public string GetUpdateShaderCode() => @"
// Color Over Lifetime Module
particle.Color = mix(uColorStart, uColorEnd, lifeRatio);
";
}
