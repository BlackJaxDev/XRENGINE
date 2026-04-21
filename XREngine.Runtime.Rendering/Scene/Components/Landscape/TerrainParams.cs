using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Terrain-wide parameters.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainParams
{
    public Vector3 TerrainOrigin;
    public float TerrainSize;
    public float MinHeight;
    public float MaxHeight;
    public uint HeightmapResolution;
    public uint ChunkCount;
    public Vector4 LODDistances; // Up to 4 LOD levels
    public float MorphStartRatio;
    public uint TotalChunks;
    public float Padding0;
    public float Padding1;
}
