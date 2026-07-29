namespace XREngine.Rendering;

/// <summary>
/// Shader family currently available to the advanced pipeline.
/// </summary>
public enum EAdvancedShaderFamily
{
    None = 0,

    /// <summary>
    /// The target visibility-buffer raster, reconstruction, classification, and shading family.
    /// </summary>
    VisibilityBuffer,
}
