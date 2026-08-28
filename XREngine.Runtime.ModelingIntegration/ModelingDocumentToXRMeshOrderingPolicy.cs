namespace XREngine.Rendering.Modeling;

/// <summary>
/// Determines how modeling-document conversion orders vertices and triangles in the runtime mesh.
/// </summary>
public enum ModelingDocumentToXRMeshOrderingPolicy
{
    PreserveDocumentOrder = 0,
    Canonicalized = 1
}
