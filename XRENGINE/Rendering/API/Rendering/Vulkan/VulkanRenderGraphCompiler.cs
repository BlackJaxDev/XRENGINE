using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanRenderGraphCompiler _renderGraphCompiler = new();

    /// <summary>
    /// Compiles frame-level render graph metadata into a compact Vulkan-friendly plan and
    /// provides deterministic operation ordering/grouping helpers used during command recording.
    /// </summary>
    internal sealed class VulkanRenderGraphCompiler
    {
        /// <summary>
        /// Describes one contiguous run of compatible operations that can be recorded into
        /// secondary command buffers as a batch.
        /// </summary>
        internal readonly record struct SecondaryRecordingBucket(
            int StartIndex,
            int Count,
            int PassIndex,
            int SchedulingIdentity,
            Type OpType,
            FrameOpContext Context);

        /// <summary>
        /// Compiles the high-level pass metadata into:
        /// 1) topological pass order,
        /// 2) compatible graphics pass batches,
        /// 3) synchronization plan.
        /// </summary>
        /// <param name="passMetadata">Per-pass metadata emitted by render graph construction.</param>
        /// <returns>A compiled graph snapshot consumed by frame op sorting and barrier emission.</returns>
        public VulkanCompiledRenderGraph Compile(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            // No metadata means there is no ordering/batching/synchronization work to perform.
            if (passMetadata is null || passMetadata.Count == 0)
                return VulkanCompiledRenderGraph.Empty;

            // Topological order ensures producers are recorded before their consumers.
            IReadOnlyList<RenderPassMetadata> orderedPasses = RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);

            // Build explicit synchronization requirements from the same metadata source.
            RenderGraphSynchronizationInfo synchronization = RenderGraphSynchronizationPlanner.Build(passMetadata);

            // passIndex -> topological order index lookup used by SortFrameOps.
            Dictionary<int, int> passOrder = new(orderedPasses.Count);

            // Graphics passes with compatible attachment signatures are merged into batches.
            List<VulkanCompiledPassBatch> batches = [];

            for (int i = 0; i < orderedPasses.Count; i++)
            {
                RenderPassMetadata pass = orderedPasses[i];
                passOrder[pass.PassIndex] = i;

                // Signature captures the effective attachment contract for compatibility checks.
                string signature = BuildAttachmentSignature(pass);
                if (batches.Count > 0 && IsBatchCompatible(batches[^1], pass, signature))
                {
                    // Extend current batch when stage/signature compatibility holds.
                    batches[^1].AddPass(pass.PassIndex);
                }
                else
                {
                    // Start a new batch when compatibility is broken or this is the first pass.
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

        /// <summary>
        /// Sorts frame operations deterministically by:
        /// 1) first-occurrence scheduling group,
        /// 2) compiled pass topological order,
        /// 3) original index (stable tie-breaker).
        /// </summary>
        /// <remarks>
        /// Using first occurrence instead of raw scheduling hash preserves inter-pipeline enqueue
        /// order while still grouping same-context operations together.
        /// </remarks>
        /// <param name="ops">Operations to sort.</param>
        /// <param name="graph">Compiled pass-order metadata.</param>
        /// <returns>A new sorted array (or original array for length 0/1).</returns>
        public static FrameOp[] SortFrameOps(FrameOp[] ops, VulkanCompiledRenderGraph graph)
        {
            // Fast path: trivial arrays are already sorted and preserving reference identity helps tests.
            if (ops.Length <= 1)
                return ops;

            // Build a first-occurrence map: for each unique SchedulingIdentity,
            // record the original index of its first op.  Sorting by this value
            // instead of the raw hash preserves the inter-pipeline enqueue order
            // (e.g. DebugOpaque ops before UserInterface ops) while still grouping
            // ops that share the same pipeline+viewport together.
            Dictionary<int, int> firstOccurrence = [];
            for (int i = 0; i < ops.Length; i++)
            {
                int sid = ops[i].Context.SchedulingIdentity;
                if (!firstOccurrence.ContainsKey(sid))
                    firstOccurrence[sid] = i;
            }

            // If pass order is unavailable, pass rank falls back to int.MaxValue.
            bool hasPassOrder = graph.PassOrder.Count > 0;

            return [.. ops
                .Select((op, index) => new
                {
                    Operation = op,
                    OriginalIndex = index,
                    GroupOrder = firstOccurrence[op.Context.SchedulingIdentity],
                    PassOrder = hasPassOrder && graph.PassOrder.TryGetValue(op.PassIndex, out int order)
                        ? order
                        : int.MaxValue
                })
                .OrderBy(x => x.GroupOrder)
                .ThenBy(x => x.PassOrder)
                .ThenBy(x => x.OriginalIndex)
                .Select(x => x.Operation)];
        }

        /// <summary>
        /// Determines whether a <see cref="FrameOp"/> targets the swapchain (i.e. has no
        /// explicit FBO target).  Swapchain-targeting ops must share a single context to
        /// avoid render-pass restarts that lose composited content.
        /// </summary>
        internal static bool OpTargetsSwapchain(FrameOp op) => op switch
        {
            ClearOp c => c.Target is null,
            MeshDrawOp d => d.Target is null,
            BlitOp b => b.OutFbo is null,
            IndirectDrawOp id => id.Target is null,
            _ => false,
        };

        /// <summary>
        /// Coalesces all swapchain-targeting ops so they share the context of the first
        /// swapchain op.  This prevents render-pass restarts across pipeline boundaries
        /// when multiple pipelines (e.g. scene + UI) composite onto the swapchain.
        /// FBO-targeting ops keep their original context for correct barrier/resource planning.
        /// </summary>
        internal static void CoalesceSwapchainContexts(FrameOp[] ops)
        {
            // Null until first swapchain op is observed.
            FrameOpContext? firstSwapchainContext = null;
            for (int i = 0; i < ops.Length; i++)
            {
                if (OpTargetsSwapchain(ops[i]))
                {
                    if (firstSwapchainContext is null)
                    {
                        // First swapchain op establishes canonical context.
                        firstSwapchainContext = ops[i].Context;
                    }
                    else if (!Equals(ops[i].Context, firstSwapchainContext.Value))
                    {
                        // Rewrite only swapchain ops; non-swapchain ops retain original contexts.
                        ops[i] = ops[i] with { Context = firstSwapchainContext.Value };
                    }
                }
            }
        }

        /// <summary>
        /// Builds contiguous runs of secondary-command-buffer-eligible operations.
        /// A run ends when op type, pass, scheduling identity, or full context changes.
        /// </summary>
        /// <param name="ops">Sorted frame operations for the current frame.</param>
        /// <returns>Ordered bucket list for optional parallel secondary recording.</returns>
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
                    // Ineligible ops break the current run.
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

                // Runs must remain homogeneous to be safely co-recorded.
                bool sameBucket =
                    runType == opType &&
                    runPassIndex == passIndex &&
                    runSchedulingIdentity == schedulingIdentity &&
                    Equals(runContext, op.Context);

                if (!sameBucket)
                {
                    // Close previous run and start a new compatible run at i.
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
                    // Emit one bucket per contiguous compatible run.
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

        /// <summary>
        /// Determines whether an op type participates in secondary command recording buckets.
        /// </summary>
        private static bool IsSecondaryBucketEligible(FrameOp op)
            => op is BlitOp or IndirectDrawOp or ComputeDispatchOp;

        /// <summary>
        /// Determines whether a pass can be merged into an existing compiled batch.
        /// </summary>
        private static bool IsBatchCompatible(VulkanCompiledPassBatch existingBatch, RenderPassMetadata pass, string signature)
            => existingBatch.Stage == ERenderGraphPassStage.Graphics &&
               pass.Stage == ERenderGraphPassStage.Graphics &&
               string.Equals(existingBatch.AttachmentSignature, signature, StringComparison.Ordinal);

        /// <summary>
        /// Builds an order-insensitive attachment signature used for graphics batch compatibility.
        /// </summary>
        /// <remarks>
        /// Signature format: ResourceType:ResourceName:LoadOp:StoreOp joined by '|'.
        /// Non-attachment usages are excluded.
        /// </remarks>
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
            RenderGraphSynchronizationInfo.Empty);

        /// <summary>
        /// Initializes a compiled graph snapshot.
        /// </summary>
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

        /// <summary>Topologically sorted passes from the source graph.</summary>
        public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

        /// <summary>Lookup from pass index to its topological order rank.</summary>
        public IReadOnlyDictionary<int, int> PassOrder { get; }

        /// <summary>Adjacent compatible graphics pass batches.</summary>
        public IReadOnlyList<VulkanCompiledPassBatch> Batches { get; }

        /// <summary>Derived synchronization plan for barriers/dependencies.</summary>
        public RenderGraphSynchronizationInfo Synchronization { get; }
    }

    /// <summary>
    /// Represents one compiled batch of graphics passes sharing a compatible attachment signature.
    /// </summary>
    internal sealed class VulkanCompiledPassBatch
    {
        private readonly List<int> _passIndices = [];

        /// <summary>
        /// Initializes a compiled pass batch descriptor.
        /// </summary>
        internal VulkanCompiledPassBatch(int batchIndex, ERenderGraphPassStage stage, string attachmentSignature)
        {
            BatchIndex = batchIndex;
            Stage = stage;
            AttachmentSignature = attachmentSignature;
        }

        /// <summary>Monotonic index of this batch in compilation order.</summary>
        public int BatchIndex { get; }

        /// <summary>Render-graph stage shared by this batch.</summary>
        public ERenderGraphPassStage Stage { get; }

        /// <summary>Compatibility signature derived from attachment usages.</summary>
        public string AttachmentSignature { get; }

        /// <summary>Read-only ordered pass indices contained in this batch.</summary>
        public ReadOnlyCollection<int> PassIndices => _passIndices.AsReadOnly();

        /// <summary>
        /// Appends a pass index to this batch.
        /// </summary>
        internal void AddPass(int passIndex)
            => _passIndices.Add(passIndex);
    }
}
