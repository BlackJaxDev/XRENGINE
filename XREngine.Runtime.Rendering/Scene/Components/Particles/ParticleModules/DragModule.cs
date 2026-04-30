using System.ComponentModel;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for drag/air resistance simulation.
/// </summary>
public class DragModule : IParticleUpdateModule
{
    public string ModuleName => "Drag";
    public int Priority => 110;
    public bool Enabled { get; set; } = true;

    [Description("Drag coefficient (0-1). Higher = more drag.")]
    public float Drag { get; set; } = 0.1f;

    public string GetUniformDeclarations() => @"
uniform float uDrag;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uDrag", Drag);
    }

    public string GetUpdateShaderCode() => @"
// Drag Module
particle.Velocity *= (1.0 - uDrag * deltaTime);
";
}
