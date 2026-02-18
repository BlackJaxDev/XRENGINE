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
        internal readonly record struct SecondaryRecordingBucket(
            int StartIndex,
            int Count,
            int PassIndex,
            int SchedulingIdentity,
            Type OpType,
            FrameOpContext Context);

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

        public IReadOnlyList<SecondaryRecordingBucket> BuildSecondaryRecordingBuckets(FrameOp[] ops)
        {
            if (ops.Length == 0)
                return Array.Empty<SecondaryRecordingBucket>();

            List<SecondaryRecordingBucket> buckets = [];
            int runStart = -1;
            int runPassIndex = int.MinValue;
            int runSchedulingIdentity = int.MinValue;
            Type? runType = null;
            FrameOpContext runContext = default;

            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (!IsSecondaryBucketEligible(op))
                {
                    FinalizeRun(i);
                    continue;
                }

                int passIndex = op.PassIndex;
                int schedulingIdentity = op.Context.SchedulingIdentity;
                Type opType = op.GetType();

                if (runStart < 0)
                {
                    runStart = i;
                    runPassIndex = passIndex;
                    runSchedulingIdentity = schedulingIdentity;
                    runType = opType;
                    runContext = op.Context;
                    continue;
                }

                bool sameBucket =
                    runType == opType &&
                    runPassIndex == passIndex &&
                    runSchedulingIdentity == schedulingIdentity &&
                    Equals(runContext, op.Context);

                if (!sameBucket)
                {
                    FinalizeRun(i);
                    runStart = i;
                    runPassIndex = passIndex;
                    runSchedulingIdentity = schedulingIdentity;
                    runType = opType;
                    runContext = op.Context;
                }
            }

            FinalizeRun(ops.Length);
            return buckets;

            void FinalizeRun(int runEndExclusive)
            {
                if (runStart < 0 || runType is null)
                    return;

                int runCount = runEndExclusive - runStart;
                if (runCount > 0)
                {
                    buckets.Add(new SecondaryRecordingBucket(
                        runStart,
                        runCount,
                        runPassIndex,
                        runSchedulingIdentity,
                        runType,
                        runContext));
                }

                runStart = -1;
                runPassIndex = int.MinValue;
                runSchedulingIdentity = int.MinValue;
                runType = null;
                runContext = default;
            }
        }

        private static bool IsSecondaryBucketEligible(FrameOp op)
            => op is BlitOp or IndirectDrawOp or ComputeDispatchOp;

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
