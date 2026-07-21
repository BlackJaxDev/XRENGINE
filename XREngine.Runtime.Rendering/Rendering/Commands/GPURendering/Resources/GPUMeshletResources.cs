using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuMeshletTaskRecord
    {
        public uint MeshletIndex;
        public uint DrawID;
        public uint TransformID;
        public uint MaterialID;
    }

    public readonly struct GpuMeshletExpansionInputs(
        XRDataBuffer visibleCommandBuffer,
        XRDataBuffer? visibleHotCommandBuffer,
        bool useHotCommandLayout,
        XRDataBuffer culledCountBuffer,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer meshDataBuffer,
        XRDataBuffer meshletRangeBuffer,
        XRDataBuffer meshletDescriptorBuffer,
        XRDataBuffer meshletVertexIndexBuffer,
        XRDataBuffer meshletTriangleIndexBuffer,
        XRDataBuffer lodTransitionBuffer,
        uint visibleCommandUpperBound)
    {
        public XRDataBuffer VisibleCommandBuffer { get; } = visibleCommandBuffer;
        public XRDataBuffer? VisibleHotCommandBuffer { get; } = visibleHotCommandBuffer;
        public bool UseHotCommandLayout { get; } = useHotCommandLayout;
        public XRDataBuffer CulledCountBuffer { get; } = culledCountBuffer;
        public XRDataBuffer DrawMetadataBuffer { get; } = drawMetadataBuffer;
        public XRDataBuffer MeshDataBuffer { get; } = meshDataBuffer;
        public XRDataBuffer MeshletRangeBuffer { get; } = meshletRangeBuffer;
        public XRDataBuffer MeshletDescriptorBuffer { get; } = meshletDescriptorBuffer;
        public XRDataBuffer MeshletVertexIndexBuffer { get; } = meshletVertexIndexBuffer;
        public XRDataBuffer MeshletTriangleIndexBuffer { get; } = meshletTriangleIndexBuffer;
        public XRDataBuffer LodTransitionBuffer { get; } = lodTransitionBuffer;
        public uint VisibleCommandUpperBound { get; } = visibleCommandUpperBound;
    }

    public static class GPUMeshletBindings
    {
        public const int ExpandVisibleCommands = 0;
        public const int ExpandCulledCount = 1;
        public const int ExpandDrawMetadata = 2;
        public const int ExpandMeshData = 3;
        public const int ExpandMeshletRanges = 4;
        public const int ExpandMeshletDescriptors = 5;
        public const int ExpandMeshletVertexIndices = 6;
        public const int ExpandMeshletTriangleIndices = 7;
        public const int ExpandLodTransitions = 8;
        public const int ExpandVisibleMeshletTasks = 9;
        public const int ExpandMeshletTaskCount = 10;
        public const int ExpandDispatchIndirect = 11;
        public const int ExpandOverflow = 12;
        public const int ExpandVisibleHotCommands = 13;
        public const int ExpandDispatchCount = 14;
        public const int ExpandTransparencyMetadata = 15;
    }

    public static class GPUMeshletLayout
    {
        public const uint MeshletRangeUIntCount = 4u;
        public const uint MeshletTaskRecordUIntCount = 4u;
        public const uint MeshTaskIndirectCommandUIntCount = 3u;
        public const uint MeshTaskIndirectCommandStride = MeshTaskIndirectCommandUIntCount * sizeof(uint);
        public const uint MeshTaskIndirectCommandMaxDrawCount = 1u;
        public const uint MeshletTaskPreviousLodFlag = 0x80000000u;
        public const uint MeshletTaskMeshletIndexMask = ~MeshletTaskPreviousLodFlag;

        public static readonly uint MeshletTaskRecordStride = (uint)Marshal.SizeOf<GpuMeshletTaskRecord>();
    }
}
