using System.ComponentModel;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Components.Landscape.Interfaces;

namespace XREngine.Scene.Components.Landscape.TerrainModules;

/// <summary>
/// Procedural noise-based height generation.
/// </summary>
public class ProceduralNoiseModule : ITerrainHeightModule
{
    public string ModuleName => "ProceduralNoise";
    public int Priority => 10;
    public bool Enabled { get; set; } = false;

    [Description("Noise frequency scale.")]
    public float Frequency { get; set; } = 0.01f;

    [Description("Number of noise octaves.")]
    public int Octaves { get; set; } = 4;

    [Description("Amplitude multiplier per octave.")]
    public float Persistence { get; set; } = 0.5f;

    [Description("Frequency multiplier per octave.")]
    public float Lacunarity { get; set; } = 2.0f;

    [Description("Overall height amplitude.")]
    public float Amplitude { get; set; } = 1.0f;

    [Description("Noise offset for variation.")]
    public Vector2 Offset { get; set; } = Vector2.Zero;

    [Description("Random seed for noise generation.")]
    public float Seed { get; set; } = 0.0f;

    public string GetUniformDeclarations() => @"
uniform float uNoiseFrequency;
uniform int uNoiseOctaves;
uniform float uNoisePersistence;
uniform float uNoiseLacunarity;
uniform float uNoiseAmplitude;
uniform vec2 uNoiseOffset;
uniform float uNoiseSeed;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uNoiseFrequency", Frequency);
        program.Uniform("uNoiseOctaves", Octaves);
        program.Uniform("uNoisePersistence", Persistence);
        program.Uniform("uNoiseLacunarity", Lacunarity);
        program.Uniform("uNoiseAmplitude", Amplitude);
        program.Uniform("uNoiseOffset", Offset);
        program.Uniform("uNoiseSeed", Seed);
    }

    public string GetHeightShaderCode() => @"
// Procedural Noise Module
float height = 0.0;
float amplitude = uNoiseAmplitude;
float frequency = uNoiseFrequency;
float maxAmplitude = 0.0;
vec2 samplePos = worldPos.xz + uNoiseOffset;

for (int i = 0; i < uNoiseOctaves; i++) {
    height += amplitude * snoise(samplePos * frequency + uNoiseSeed);
    maxAmplitude += amplitude;
    amplitude *= uNoisePersistence;
    frequency *= uNoiseLacunarity;
}

return (height / maxAmplitude) * uNoiseAmplitude;
";
}
