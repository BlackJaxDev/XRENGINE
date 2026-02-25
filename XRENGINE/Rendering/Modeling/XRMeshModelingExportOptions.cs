namespace XREngine.Rendering.Modeling;

public sealed class XRMeshModelingExportOptions
{
    public bool ValidateDocument { get; init; } = true;

    /// <summary>
    /// Controls deterministic ordering policy for exported vertex/index streams.
    /// </summary>
    public XRMeshModelingExportOrderingPolicy OrderingPolicy { get; init; } = XRMeshModelingExportOrderingPolicy.PreserveDocumentOrder;

    /// <summary>
    /// Controls fallback behavior for skinning/blendshape channels when topology changes make exact preservation impossible.
    /// </summary>
    public XRMeshModelingSkinningBlendshapeFallbackPolicy SkinningBlendshapeFallbackPolicy { get; init; }
        = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject;

    /// <summary>
    /// Emits warning diagnostics during permissive fallback paths.
    /// </summary>
    public bool EmitFallbackDiagnostics { get; init; } = true;
}
