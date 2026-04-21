using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Per-chunk data stored on the GPU.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainChunkData
{
    public Vector3 BoundsMin;
    public float LODLevel;
    public Vector3 BoundsMax;
    public float MorphFactor;
    public Vector2 ChunkOffset;
    public Vector2 ChunkScale;
    public uint IndexOffset;
    public uint IndexCount;
    public float Padding0;
    public float Padding1;
}
