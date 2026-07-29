using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Compact global-buffer offsets for one complete mesh deformation. Buffer
/// roles are fixed by the aggregate shader binding contract, so the job does
/// not carry per-renderer descriptor objects.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDeformationJobRecord
{
    public AdvancedGpuHandle Mesh;
    public AdvancedGpuHandle SharedPose;
    public uint SourceVertexOffset;
    public uint CurrentVertexOffset;
    public uint PreviousVertexOffset;
    public uint BoneInfluenceOffset;
    public uint BonePaletteOffset;
    public uint InverseBindOffset;
    public uint BlendshapeWeightOffset;
    public uint BlendshapeShapeOffset;
    public uint VertexFirst;
    public uint VertexCount;
    public uint MeshletFirst;
    public uint MeshletCount;
    public uint BoneCount;
    public uint BlendshapeCount;
    public uint MeshGeneration;
    public uint PoseGeneration;
    public uint PaletteGeneration;
    public uint TopologyGeneration;
    public ulong VertexLayoutId;
    public EAdvancedDeformationFeatureFlags Features;
    public EAdvancedDeformationPrecision Precision;
    public EAdvancedDeformationOrder Order;
    public uint OutputStride;
}
