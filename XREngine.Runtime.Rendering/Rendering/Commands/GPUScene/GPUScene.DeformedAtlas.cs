namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    /// <summary>Gets the atlas buffers and vertex range that hold one mesh.</summary>
    internal bool TryGetAtlasVertexRange(
        XRMesh mesh,
        out XRDataBuffer? positions,
        out XRDataBuffer? normals,
        out XRDataBuffer? tangents,
        out uint firstVertex)
    {
        positions = normals = tangents = null;
        firstVertex = 0u;
        using (_lock.EnterScope())
        {
            if (!_activeAtlasTiers.TryGetValue(mesh, out EAtlasTier tier))
                return false;

            AtlasTierState state = GetTierState(tier);
            if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation allocation))
                return false;

            positions = state.Positions;
            normals = state.Normals;
            tangents = state.Tangents;
            firstVertex = checked((uint)allocation.FirstVertex);
            return positions is not null;
        }
    }
}
