using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Immutable result of render graph compilation used during command recording.
    /// </summary>
    internal sealed class VulkanCompiledRenderGraph
    {
        /// <summary>
        /// Canonical empty graph instance for frames/pipelines with no pass metadata.
        /// </summary>
        public static VulkanCompiledRenderGraph Empty { get; } = new(
            Array.Empty<RenderPassMetadata>(),
            new Dictionary<int, int>(),
            Array.Empty<VulkanCompiledPassBatch>(),
            RenderGraphSynchronizationInfo.Empty,
            int.MaxValue);

        /// <summary>
        /// Initializes a compiled graph snapshot.
        /// </summary>
        internal VulkanCompiledRenderGraph(
            IReadOnlyList<RenderPassMetadata> orderedPasses,
            IReadOnlyDictionary<int, int> passOrder,
            IReadOnlyList<VulkanCompiledPassBatch> batches,
            RenderGraphSynchronizationInfo synchronization,
            int screenSpaceUiPassOrder = int.MaxValue)
        {
            OrderedPasses = orderedPasses;
            PassOrder = passOrder;
            Batches = batches;
            Synchronization = synchronization;
            ScreenSpaceUiPassOrder = screenSpaceUiPassOrder;
            Plan = new VulkanRenderGraphPlan(orderedPasses, batches, synchronization);
        }

        /// <summary>Topologically sorted passes from the source graph.</summary>
        public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

        /// <summary>Lookup from pass index to its topological order rank.</summary>
        public IReadOnlyDictionary<int, int> PassOrder { get; }

        /// <summary>Precomputed topological order for nested screen-space UI, or int.MaxValue when absent.</summary>
        public int ScreenSpaceUiPassOrder { get; }

        /// <summary>Adjacent compatible graphics pass batches.</summary>
        public IReadOnlyList<VulkanCompiledPassBatch> Batches { get; }

        /// <summary>Derived synchronization plan for barriers/dependencies.</summary>
        public RenderGraphSynchronizationInfo Synchronization { get; }

        /// <summary>Immutable structural plan generation used for cache and recording identity.</summary>
        public VulkanRenderGraphPlan Plan { get; }
    }
}
