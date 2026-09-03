using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-written record for one froxel cell indexing local point and spot lights.
/// 16-byte packed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct AdvancedFroxelRecord
{
    /// <summary>
    /// Offset into the light index list for point lights intersecting this froxel.
    /// </summary>
    public uint PointLightOffset;

    /// <summary>
    /// Count of point lights intersecting this froxel.
    /// </summary>
    public uint PointLightCount;

    /// <summary>
    /// Offset into the light index list for spot lights intersecting this froxel.
    /// </summary>
    public uint SpotLightOffset;

    /// <summary>
    /// Count of spot lights intersecting this froxel.
    /// </summary>
    public uint SpotLightCount;

    public AdvancedFroxelRecord(uint pointOffset, uint pointCount, uint spotOffset, uint spotCount)
    {
        PointLightOffset = pointOffset;
        PointLightCount = pointCount;
        SpotLightOffset = spotOffset;
        SpotLightCount = spotCount;
    }
}
