using System.ComponentModel;
using XREngine.Rendering;
using XREngine.Scene.Components.Landscape.Interfaces;

namespace XREngine.Scene.Components.Landscape.TerrainModules;

/// <summary>
/// Height-based texture splatting module.
/// </summary>
public class HeightSplatModule : ITerrainSplatModule
{
    public string ModuleName => "HeightSplat";
    public int Priority => 10;
    public bool Enabled { get; set; } = false;

    [Description("Height thresholds for layer transitions.")]
    public float[] HeightThresholds { get; set; } = [0.3f, 0.6f, 0.8f];

    [Description("Blend range for height transitions.")]
    public float BlendRange { get; set; } = 0.1f;

    [Description("Height threshold for texture transition (normalized 0-1).")]
    public float HeightThreshold { get; set; } = 0.5f;

    [Description("Height blending factor.")]
    public float HeightBlend { get; set; } = 0.2f;

    [Description("Layer index to use for height-based splatting.")]
    public uint HeightLayerIndex { get; set; } = 1;

    public string GetUniformDeclarations() => @"
uniform float uHeightThreshold0;
uniform float uHeightThreshold1;
uniform float uHeightThreshold2;
uniform float uHeightBlendRange;
";

    public void SetUniforms(XRRenderProgram program)
    {
        // Set height thresholds (with safety for array bounds)
        program.Uniform("uHeightThreshold0", HeightThresholds.Length > 0 ? HeightThresholds[0] : 0.3f);
        program.Uniform("uHeightThreshold1", HeightThresholds.Length > 1 ? HeightThresholds[1] : 0.6f);
        program.Uniform("uHeightThreshold2", HeightThresholds.Length > 2 ? HeightThresholds[2] : 0.8f);
        program.Uniform("uHeightBlendRange", BlendRange);
    }

    public string GetSplatShaderCode() => @"
// Height-based Splat Module
float normalizedHeight = (height - uTerrainParams.MinHeight) / 
                         (uTerrainParams.MaxHeight - uTerrainParams.MinHeight);

vec4 weights = vec4(0.0);

// Simple height bands
weights.x = 1.0 - smoothstep(uHeightThreshold0 - uHeightBlendRange, uHeightThreshold0 + uHeightBlendRange, normalizedHeight);
weights.y = smoothstep(uHeightThreshold0 - uHeightBlendRange, uHeightThreshold0 + uHeightBlendRange, normalizedHeight) *
            (1.0 - smoothstep(uHeightThreshold1 - uHeightBlendRange, uHeightThreshold1 + uHeightBlendRange, normalizedHeight));
weights.z = smoothstep(uHeightThreshold1 - uHeightBlendRange, uHeightThreshold1 + uHeightBlendRange, normalizedHeight) *
            (1.0 - smoothstep(uHeightThreshold2 - uHeightBlendRange, uHeightThreshold2 + uHeightBlendRange, normalizedHeight));
weights.w = smoothstep(uHeightThreshold2 - uHeightBlendRange, uHeightThreshold2 + uHeightBlendRange, normalizedHeight);

return weights;
";
}
