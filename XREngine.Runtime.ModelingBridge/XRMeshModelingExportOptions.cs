namespace XREngine.Rendering.Modeling;

public sealed class XRMeshModelingExportOptions
{
    public bool ValidateDocument { get; init; } = true;

    public XRMeshModelingExportOrderingPolicy OrderingPolicy { get; init; } = XRMeshModelingExportOrderingPolicy.PreserveDocumentOrder;

    public XRMeshModelingSkinningBlendshapeFallbackPolicy SkinningBlendshapeFallbackPolicy { get; init; }
        = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject;

    public bool EmitFallbackDiagnostics { get; init; } = true;
}