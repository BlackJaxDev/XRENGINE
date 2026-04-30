using System.ComponentModel;
using XREngine.Rendering;
using XREngine.Scene.Components.Landscape.Interfaces;

namespace XREngine.Scene.Components.Landscape.TerrainModules;

/// <summary>
/// Slope-based texture splatting module.
/// </summary>
public class SlopeSplatModule : ITerrainSplatModule
{
    public string ModuleName => "SlopeSplat";
    public int Priority => 0;
    public bool Enabled { get; set; } = true;

    [Description("Slope angle threshold for cliff texture (degrees).")]
    public float CliffAngle { get; set; } = 45.0f;

    [Description("Blend range for slope transitions.")]
    public float BlendRange { get; set; } = 5.0f;

    [Description("Slope threshold for texture transition (normalized 0-1).")]
    public float SlopeThreshold { get; set; } = 0.5f;

    [Description("Slope blending factor.")]
    public float SlopeBlend { get; set; } = 0.2f;

    [Description("Layer index to use for slopes.")]
    public uint SlopeLayerIndex { get; set; } = 2;

    public string GetUniformDeclarations() => @"
uniform float uCliffAngle;
uniform float uSlopeBlendRange;
uniform float uSlopeThreshold;
uniform float uSlopeBlend;
uniform uint uSlopeLayerIndex;
";

    public void SetUniforms(XRRenderProgram program)
    {
        program.Uniform("uCliffAngle", CliffAngle);
        program.Uniform("uSlopeBlendRange", BlendRange);
        program.Uniform("uSlopeThreshold", SlopeThreshold);
        program.Uniform("uSlopeBlend", SlopeBlend);
        program.Uniform("uSlopeLayerIndex", SlopeLayerIndex);
    }

    public string GetSplatShaderCode() => @"
// Slope-based Splat Module
float slopeAngle = acos(normal.y) * 57.2957795; // radians to degrees

// Layer 0: flat ground
// Layer 1: slopes
// Layer 2: cliffs
vec4 weights = vec4(0.0);

float cliffFactor = smoothstep(uCliffAngle - uSlopeBlendRange, uCliffAngle + uSlopeBlendRange, slopeAngle);
float slopeFactor = smoothstep(10.0, 30.0, slopeAngle);

weights.x = 1.0 - slopeFactor; // flat
weights.y = slopeFactor * (1.0 - cliffFactor); // slope
weights.z = cliffFactor; // cliff

return weights;
";
}
