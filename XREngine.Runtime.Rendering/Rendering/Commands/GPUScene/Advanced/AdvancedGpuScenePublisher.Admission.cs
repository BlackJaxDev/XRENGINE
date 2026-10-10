namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>Checks the exact cooked producer required by a native meshlet selection without generating a replacement.</summary>
    public static bool TryInspectCanonicalMeshlets(XRMesh? mesh, out string reason)
    {
        if (mesh?.MeshletPayload is { HasMeshlets: true } payload && payload.IsValidatedFor(mesh))
        {
            reason = string.Empty;
            return true;
        }
        reason = "The selected meshlet strategy requires an exact validated cooked meshlet payload for this mesh geometry; indexed substitution is not allowed.";
        return false;
    }

    /// <summary>Checks the same immutable source used by canonical publication without creating a database or GPU resources.</summary>
    public static bool TryInspectCanonicalGeometry(XRMesh? mesh, out string reason)
    {
        if (TryValidateCanonicalGeometry(mesh, out EAdvancedCanonicalCompatibilityReason compatibility))
        {
            reason = string.Empty;
            return true;
        }
        reason = compatibility switch
        {
            EAdvancedCanonicalCompatibilityReason.UnsupportedGeometryTopology =>
                "Canonical native visibility requires triangle topology.",
            _ => "Canonical geometry requires a complete CPU-readable rich or packed vertex source and nonempty in-range triangle indices.",
        };
        return false;
    }
}
