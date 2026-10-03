namespace XREngine.Rendering;

/// <summary>Explicit runtime-only instance semantics on a renderer's existing binding publisher.</summary>
public interface IAuthoredMeshInstanceProvider : IRenderResourceBindingPublisher
{
    /// <summary>
    /// Supplies real current/previous transforms and bounds for this exact mesh.
    /// Generations must advance on content or layout changes. Shared deformation
    /// admission explicitly certifies that the mesh-keyed output is pre-instance
    /// geometry; separate instance palettes or previous vertex streams are not inferred.
    /// </summary>
    bool TryGetInstanceSource(XRMesh mesh, out AuthoredMeshInstanceSource source);
}
