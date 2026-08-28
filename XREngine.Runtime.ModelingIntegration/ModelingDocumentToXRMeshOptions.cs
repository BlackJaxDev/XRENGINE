namespace XREngine.Rendering.Modeling;

public sealed class ModelingDocumentToXRMeshOptions
{
    public bool ValidateDocument { get; init; } = true;

    public ModelingDocumentToXRMeshOrderingPolicy OrderingPolicy { get; init; } = ModelingDocumentToXRMeshOrderingPolicy.PreserveDocumentOrder;

    public ModelingDocumentToXRMeshSkinningBlendshapeFallbackPolicy SkinningBlendshapeFallbackPolicy { get; init; }
        = ModelingDocumentToXRMeshSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject;

    public bool EmitFallbackDiagnostics { get; init; } = true;
}