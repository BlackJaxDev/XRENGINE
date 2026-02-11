using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanRenderGraphCompiler _renderGraphCompiler = new();

    internal sealed class VulkanRenderGraphCompiler
    {
        public VulkanCompiledRenderGraph Compile(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            if (passMetadata is null || passMetadata.Count == 0)
                return VulkanCompiledRenderGraph.Empty;

            IReadOnlyList<RenderPassMetadata> orderedPasses = RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);
            RenderGraphSynchronizationInfo synchronization = RenderGraphSynchronizationPlanner.Build(passMetadata);

            Dictionary<int, int> passOrder = new(orderedPasses.Count);
            List<VulkanCompiledPassBatch> batches = [];

            for (int i = 0; i < orderedPasses.Count; i++)
            {
                RenderPassMetadata pass = orderedPasses[i];
                passOrder[pass.PassIndex] = i;

                string signature = BuildAttachmentSignature(pass);
                if (batches.Count > 0 && IsBatchCompatible(batches[^1], pass, signature))
                {
                    batches[^1].AddPass(pass.PassIndex);
                }
                else
                {
                    VulkanCompiledPassBatch batch = new(batches.Count, pass.Stage, signature);
                    batch.AddPass(pass.PassIndex);
                    batches.Add(batch);
                }
            }

            return new VulkanCompiledRenderGraph(
                orderedPasses,
                passOrder,
                batches,
                synchronization);
        }

        public FrameOp[] SortFrameOps(FrameOp[] ops, VulkanCompiledRenderGraph graph)
        {
            if (ops.Length <= 1 || graph.PassOrder.Count == 0)
                return ops;

            return ops
                .Select((op, index) => new
                {
                    Operation = op,
                    OriginalIndex = index,
                    SchedulingIdentity = op.Context.SchedulingIdentity,
                    PassOrder = graph.PassOrder.TryGetValue(op.PassIndex, out int order) ? order : int.MaxValue
                })
                .OrderBy(x => x.SchedulingIdentity)
                .ThenBy(x => x.PassOrder)
                .ThenBy(x => x.OriginalIndex)
                .Select(x => x.Operation)
                .ToArray();
        }

        private static bool IsBatchCompatible(VulkanCompiledPassBatch existingBatch, RenderPassMetadata pass, string signature)
            => existingBatch.Stage == RenderGraphPassStage.Graphics &&
               pass.Stage == RenderGraphPassStage.Graphics &&
               string.Equals(existingBatch.AttachmentSignature, signature, StringComparison.Ordinal);

        private static string BuildAttachmentSignature(RenderPassMetadata pass)
        {
            if (pass.ResourceUsages.Count == 0)
                return "none";

            string[] attachmentEntries = pass.ResourceUsages
                .Where(static usage => usage.IsAttachment)
                .Select(static usage => $"{usage.ResourceType}:{usage.ResourceName}:{usage.LoadOp}:{usage.StoreOp}")
                .OrderBy(static entry => entry, StringComparer.Ordinal)
                .ToArray();

            return attachmentEntries.Length == 0
                ? "none"
                : string.Join("|", attachmentEntries);
        }
    }

    internal sealed class VulkanCompiledRenderGraph
    {
        public static VulkanCompiledRenderGraph Empty { get; } = new(
            Array.Empty<RenderPassMetadata>(),
            new Dictionary<int, int>(),
            Array.Empty<VulkanCompiledPassBatch>(),
            RenderGraphSynchronizationInfo.Empty);

        internal VulkanCompiledRenderGraph(
            IReadOnlyList<RenderPassMetadata> orderedPasses,
            IReadOnlyDictionary<int, int> passOrder,
            IReadOnlyList<VulkanCompiledPassBatch> batches,
            RenderGraphSynchronizationInfo synchronization)
        {
            OrderedPasses = orderedPasses;
            PassOrder = passOrder;
            Batches = batches;
            Synchronization = synchronization;
        }

        public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }
        public IReadOnlyDictionary<int, int> PassOrder { get; }
        public IReadOnlyList<VulkanCompiledPassBatch> Batches { get; }
        public RenderGraphSynchronizationInfo Synchronization { get; }
    }

    internal sealed class VulkanCompiledPassBatch
    {
        private readonly List<int> _passIndices = [];

        internal VulkanCompiledPassBatch(int batchIndex, RenderGraphPassStage stage, string attachmentSignature)
        {
            BatchIndex = batchIndex;
            Stage = stage;
            AttachmentSignature = attachmentSignature;
        }

        public int BatchIndex { get; }
        public RenderGraphPassStage Stage { get; }
        public string AttachmentSignature { get; }
        public ReadOnlyCollection<int> PassIndices => _passIndices.AsReadOnly();

        internal void AddPass(int passIndex)
            => _passIndices.Add(passIndex);
    }
}
