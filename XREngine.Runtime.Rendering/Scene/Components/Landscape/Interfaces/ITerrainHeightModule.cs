namespace XREngine.Scene.Components.Landscape.Interfaces;

/// <summary>
/// Module that generates or modifies terrain height values.
/// </summary>
public interface ITerrainHeightModule : ITerrainModule
{
    /// <summary>
    /// Gets GLSL code that returns height at the given position.
    /// Available variables: vec2 uv, vec3 worldPos
    /// Should return: float height
    /// </summary>
    string GetHeightShaderCode();
}
