namespace XREngine.Scene.Components.Landscape.Interfaces;

/// <summary>
/// Module that generates splat/blend weights for terrain texturing.
/// </summary>
public interface ITerrainSplatModule : ITerrainModule
{
    /// <summary>
    /// Gets GLSL code that returns splat weights at the given position.
    /// Available variables: vec2 uv, vec3 worldPos, vec3 normal, float height
    /// Should return: vec4 weights (up to 4 layer weights)
    /// </summary>
    string GetSplatShaderCode();
}
