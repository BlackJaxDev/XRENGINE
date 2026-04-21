namespace XREngine.Scene.Components.Landscape.Interfaces;

/// <summary>
/// Module that handles terrain detail placement (grass, rocks, etc.).
/// </summary>
public interface ITerrainDetailModule : ITerrainModule
{
    /// <summary>
    /// Gets GLSL code for detail placement/culling.
    /// </summary>
    string GetDetailShaderCode();
}
