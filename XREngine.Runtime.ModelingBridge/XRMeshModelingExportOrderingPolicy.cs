namespace XREngine.Rendering.Modeling;

/// <summary>
/// Determines how modeling export orders vertices and triangles before writing mesh data.
/// </summary>
public enum XRMeshModelingExportOrderingPolicy
{
    PreserveDocumentOrder = 0,
    Canonicalized = 1
}