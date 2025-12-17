using System.ComponentModel;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Components.ParticleModules;

/// <summary>
/// Module for cone-based velocity initialization.
/// </summary>
public class ConeVelocityModule : IParticleSpawnModule
{
    public string ModuleName => "ConeVelocity";
    public int Priority => 10;
    public bool Enabled { get; set; } = true;

    [Description("Angle of the cone in degrees.")]
    public float ConeAngle { get; set; } = 30.0f;

    [Description("Speed range (min, max).")]
    public Vector2 SpeedRange { get; set; } = new(5.0f, 10.0f);

    public string GetUniformDeclarations() => @"
uniform float uConeAngle;
uniform float uSpeedMin;
uniform float uSpeedMax;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uConeAngle", ConeAngle);
        program.Uniform("uSpeedMin", SpeedRange.X);
        program.Uniform("uSpeedMax", SpeedRange.Y);
    }

    public string GetSpawnShaderCode() => @"
// Cone Velocity Module
float angleRad = radians(uConeAngle);
float theta = randomFloat(seed) * angleRad;
float phi = randomFloat(seed) * 6.28318530718;

vec3 localDir = vec3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
mat3 emitterBasis = mat3(uEmitterParams.EmitterRight, uEmitterParams.EmitterUp, uEmitterParams.EmitterForward);
vec3 worldDir = emitterBasis * localDir;

float speed = mix(uSpeedMin, uSpeedMax, randomFloat(seed));
particle.Velocity = worldDir * speed;
";
}
