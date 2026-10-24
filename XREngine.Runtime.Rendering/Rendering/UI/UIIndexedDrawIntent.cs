using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>One indexed native draw required by a frozen UI mesh command.</summary>
internal readonly record struct UIIndexedDrawIntent(
    XRMeshRenderer Renderer,
    long GeometryRevision,
    EPrimitiveType Topology,
    IndexSize ElementType,
    uint IndexCount,
    uint Instances,
    nint IndexOffset)
{
    /// <summary>Captures the index stream that the mesh buffer builder will upload.</summary>
    internal static UIIndexedDrawIntent Capture(
        XRMeshRenderer renderer, EPrimitiveType topology, uint instances)
    {
        XRMesh mesh = renderer.Mesh ??
            throw new InvalidOperationException("A frozen UI draw has no owned mesh.");
        int count = topology switch
        {
            EPrimitiveType.Triangles => checked((mesh.Triangles?.Count ?? 0) * 3),
            EPrimitiveType.Lines => checked((mesh.Lines?.Count ?? 0) * 2),
            EPrimitiveType.Points => mesh.Points?.Count ?? 0,
            _ => throw new NotSupportedException("The frozen UI draw has an unsupported indexed topology."),
        };
        if (count <= 0 || instances == 0 || mesh.VertexCount <= 0)
            throw new InvalidOperationException("A visible frozen UI draw has no indexed work.");

        IndexSize elementType = mesh.VertexCount < short.MaxValue
            ? IndexSize.TwoBytes : IndexSize.FourBytes;
        return new(renderer, mesh.GeometryRevision, topology,
            elementType, checked((uint)count), instances, 0);
    }
}
