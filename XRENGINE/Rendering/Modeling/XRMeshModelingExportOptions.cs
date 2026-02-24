namespace XREngine.Rendering.Modeling;

public sealed class XRMeshModelingExportOptions
{
    public bool ValidateDocument { get; init; } = true;

    /// <summary>
    /// Controls deterministic ordering policy for exported vertex/index streams.
    /// </summary>
    public XRMeshModelingExportOrderingPolicy OrderingPolicy { get; init; } = XRMeshModelingExportOrderingPolicy.PreserveDocumentOrder;
}
