namespace XREngine.Rendering.Modeling;

/// <summary>
/// Determines how modeling export orders vertices and triangles before writing an <see cref="XRMesh"/>.
/// </summary>
public enum XRMeshModelingExportOrderingPolicy
{
    /// <summary>
    /// Preserve the incoming modeling document ordering exactly.
    /// </summary>
    PreserveDocumentOrder = 0,

    /// <summary>
    /// Rebuild a canonical ordering by sorting vertices on attribute keys, remapping indices,
    /// and sorting triangles lexicographically (with stable face-index tie breaks).
    /// </summary>
    Canonicalized = 1
}
