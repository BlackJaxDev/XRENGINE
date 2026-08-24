namespace XREngine.Rendering.Commands;

/// <summary>
/// Independent invalidation domain carried by canonical projection deltas.
/// Backends can update content or dense-resource mappings without rebuilding
/// native draw structure.
/// </summary>
public enum EBackendTemplateMutationDomain : byte
{
    DataContent,
    ResourceTable,
    LayoutTopology,
    Recording,
}
