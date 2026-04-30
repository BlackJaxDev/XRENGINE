using XREngine.Rendering;
using XREngine.Scene.Components.Landscape.Interfaces;

namespace XREngine.Scene.Components.Landscape.TerrainModules;

/// <summary>
/// Simple heightmap-based height module.
/// </summary>
public class HeightmapModule : ITerrainHeightModule
{
    public string ModuleName => "Heightmap";
    public int Priority => 0;
    public bool Enabled { get; set; } = true;

    public string GetUniformDeclarations() => @"
// Heightmap sampler is bound via uHeightmap
";

    public void SetUniforms(XRRenderProgram program)
    {
        // Heightmap texture is bound separately by the component
    }

    public string GetHeightShaderCode() => @"
// Heightmap Module
float heightSample = texture(uHeightmap, uv).r;
return mix(uTerrainParams.MinHeight, uTerrainParams.MaxHeight, heightSample);
";
}
