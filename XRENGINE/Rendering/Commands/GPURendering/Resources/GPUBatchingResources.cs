using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUSortKeyEntry
    {
        public uint PackedPassPipelineState;
        public uint MaterialID;
        public uint MeshID;
        public uint SourceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUBatchRangeEntry
    {
        public uint DrawOffset;
        public uint DrawCount;
        public uint MaterialID;
        public uint PackedPassPipelineState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUMaterialSlotEntry
    {
        public uint MaterialID;
        public uint SlotIndex;
    }

    public static class GPUBatchingBindings
    {
        public const uint MaterialTierCount = 3u;
        public const uint InvalidMaterialSlot = 0xFFFFFFFFu;

        // Graphics vertex-shader bindings
        public const int InstanceTransformBuffer = 9;
        public const int InstanceSourceIndexBuffer = 10;

        // Compute GPURenderBuildKeys.comp bindings
        public const int BuildKeysInputCommands = 0;
        public const int BuildKeysCulledCount = 1;
        public const int BuildKeysSortKeys = 2;

        // Compute GPURenderBuildBatches.comp bindings
        public const int BuildBatchesInputCommands = 0;
        public const int BuildBatchesMeshData = 1;
        public const int BuildBatchesCulledCount = 2;
        public const int BuildBatchesSortKeys = 3;
        public const int BuildBatchesIndirectDraws = 4;
        public const int BuildBatchesDrawCount = 5;
        public const int BuildBatchesBatchRanges = 6;
        public const int BuildBatchesBatchCount = 7;
        public const int BuildBatchesInstanceTransforms = 8;
        public const int BuildBatchesInstanceSources = 9;
        public const int BuildBatchesMaterialAggregation = 10;
        public const int BuildBatchesIndirectOverflow = 11;
        public const int BuildBatchesTruncation = 12;
        public const int BuildBatchesStats = 13;
        public const int BuildBatchesSortScratch = 14;
        public const int BuildBatchesLodTransitions = 15;

        // Compute GPURenderMaterialScatter.comp bindings
        public const int MaterialScatterInputCommands = 0;
        public const int MaterialScatterMeshData = 1;
        public const int MaterialScatterCulledCount = 2;
        public const int MaterialScatterSortKeys = 3;
        public const int MaterialScatterMaterialSlotLookup = 4;
        public const int MaterialScatterIndirectDraws = 5;
        public const int MaterialScatterDrawCounts = 6;
        public const int MaterialScatterOverflow = 7;
        public const int MaterialScatterLodTransitions = 8;
    }

    public static class GPUBatchingLayout
    {
        public const uint SortKeyUIntCount = 4;
        public const uint BatchRangeUIntCount = 4;
        public const uint InstanceTransformFloatCount = 16;
        public const uint MaterialSlotEntryUIntCount = 1;

        public static readonly uint SortKeyStride = (uint)Marshal.SizeOf<GPUSortKeyEntry>();
        public static readonly uint BatchRangeStride = (uint)Marshal.SizeOf<GPUBatchRangeEntry>();
    }

    public static class GPUTransparencyBindings
    {
        public const int ClassifyInputCommands = 0;
        public const int ClassifyCulledCount = 1;
        public const int ClassifyMetadata = 2;
        public const int ClassifyMaskedVisibleIndices = 3;
        public const int ClassifyApproximateVisibleIndices = 4;
        public const int ClassifyExactVisibleIndices = 5;
        public const int ClassifyDomainCounts = 6;
    }

    public static class GPUTransparencyLayout
    {
        public const uint MetadataUIntCount = 4;
        public const uint DomainCountBufferUIntCount = 4;
    }
}
