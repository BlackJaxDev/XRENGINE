namespace XREngine.Rendering;

/// <summary>
/// Owns backend-specific GPU deformation when the desktop pre-pass buffer layout cannot
/// be used. Raster consumers must request preparation before consuming the output.
/// </summary>
public interface IMeshDeformationBackendCapability
{
    /// <summary>
    /// Warms the renderer's canonical source generation and backend compute pipeline only.
    /// Raster consumption records the producer after render-data callbacks; a successful
    /// warm-up never promises completed output. This never requests CPU deformation, and
    /// backend-specific layouts are not published as canonical Skinned* buffers.
    /// </summary>
    bool TryPrepareMeshDeformation(XRMeshRenderer renderer);
}
