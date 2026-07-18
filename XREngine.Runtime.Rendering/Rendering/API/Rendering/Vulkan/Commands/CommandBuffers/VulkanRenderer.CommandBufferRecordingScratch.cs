using System.Runtime.CompilerServices;
using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private sealed class CommandBufferRecordingScratch
        {
            public Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket> SecondaryBucketByStart { get; } = new();
            public Dictionary<int, int> SwapchainWritesByPipeline { get; } = new();
            public Dictionary<int, string> SwapchainWriterLabelByPipeline { get; } = new();
            public Dictionary<int, string> SwapchainWriterDetailByPipeline { get; } = new();
            public Dictionary<int, FrameOp> SwapchainWriterOpByPipeline { get; } = new();
            public Dictionary<int, int> SwapchainWriterDynamicUiDrawCountByPipeline { get; } = new();
            public HashSet<nint> ExecutedCommandChainSecondaryHandles { get; } = new();
            public HashSet<VkRenderQuery> PreparedInlineQueries { get; } = new(ReferenceEqualityComparer.Instance);
            public HashSet<VkRenderQuery> BegunInlineQueries { get; } = new(ReferenceEqualityComparer.Instance);
            public HashSet<object> VisitedResourceRegistries { get; } = new(ReferenceEqualityComparer.Instance);
            public Dictionary<int, int> SwapchainWriterPassByPipeline { get; } = new();
            public Dictionary<int, int> SwapchainWriterOpIndexByPipeline { get; } = new();
            public Dictionary<int, string> PipelineNameByIdentity { get; } = new();
            public Dictionary<VkMeshRenderer, int> MeshDrawSlotsByRenderer { get; } = new(ReferenceEqualityComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> MeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> PrimaryMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> OpenXrMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataFamilyKey, int> MeshFrameDataFamilyStrides { get; } = [];
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> ReusableMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> PrimaryMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> DynamicUiMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> OpenXrMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public VulkanMeshFrameDataReservationManifest MeshFrameDataManifest { get; } = new();
            public Dictionary<XRFrameBuffer, ImageLayout[]> FboLayoutTracking { get; } = new(ReferenceEqualityComparer.Instance);
            public ConditionalWeakTable<XRFrameBuffer, FboAttachmentLayoutScratch> FboAttachmentLayouts { get; } = new();
            public CommandChainKey[] ScheduledCommandChainKeysByOpIndex { get; set; } = [];
            public List<KeyValuePair<int, int>> SwapchainWriterCountSort { get; } = new();
            public StringBuilder SwapchainWriterSummaryBuilder { get; } = new(256);
            public int SecondaryBucketByStartCapacityHint { get; set; } = 1;
            public int RecordSwapchainWriterCapacityHint { get; set; } = 1;
            public int RecordPipelineNameCapacityHint { get; set; } = 1;
            public int RecordMeshDrawSlotCapacityHint { get; set; } = 1;
            public int RecordFboLayoutCapacityHint { get; set; } = 1;

            public sealed class FboAttachmentLayoutScratch
            {
                public ImageLayout[] Layouts { get; set; } = [];
            }
        }

    }
}
